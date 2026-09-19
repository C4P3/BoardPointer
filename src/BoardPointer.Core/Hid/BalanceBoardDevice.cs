using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BoardPointer.Core.Hid;

/// <summary>4隅の生値ひと組。タイムスタンプは上位層 (LiveBoardSource) が付ける。</summary>
public readonly record struct BoardSensors(ushort TopRight, ushort BottomRight, ushort TopLeft, ushort BottomLeft);

/// <summary>
/// ペアリング済みのボードとHIDで話す。生のL2CAPもベンダードライバも要らず、Windowsからは普通の
/// Bluetooth HIDデバイスに見えるので CreateFile/ReadFile/WriteFile だけで済む。
///
/// ここの起動シーケンスとキープアライブは WiiFitToVRC (MIT, https://github.com/Nyamochi/WiiFitToVRC)
/// の docs/BALANCE_BOARD.md と Hid/BalanceBoardDevice.cs で実証されているものをほぼそのまま。
/// 自力で再発見すると日単位で溶ける部分なので素直に借りている。
/// 追加してあるのは工場出荷較正テーブルの読み出し (レポート 0x17 / 応答 0x21) のみ。
/// </summary>
public sealed class BalanceBoardDevice : IDisposable
{
    private const ushort NintendoVendorId = 0x057E;
    public const ushort BalanceBoardProductId = 0x0306;
    private const int ReportLength = 22;

    // 読みは専用スレッドで回りっぱなし、書きは起動シーケンスとキープアライブタイマーから飛んでくる。
    // FileStream は非同期モードだと別スレッドからの読み書き同時実行が安全でないので、ハンドルを分ける。
    private readonly FileStream _readStream;
    private readonly SafeFileHandle _readHandle;
    private readonly FileStream _writeStream;
    private readonly SafeFileHandle _writeHandle;
    private readonly Thread _readThread;
    private Timer? _keepAliveTimer;
    private volatile bool _stopping;

    // レポート 0x21 (メモリ読み出しの応答) を Start() 中の同期的な待ちに渡すための受け口。
    private readonly object _readReplyLock = new();
    private byte[]? _readReplyBuffer;
    private readonly HashSet<int> _readReplyChunks = [];
    private int _readReplyBase;
    private int _readReplyError;
    private byte[]? _readReplyPartial;
    private ManualResetEventSlim? _readReplyComplete;

    public event Action<BoardSensors>? SensorsReported;
    public event Action<string>? Disconnected;

    /// <summary>工場出荷較正。読めなかった個体/タイミングでは null のまま。</summary>
    public FactoryCalibration? Calibration { get; private set; }

    /// <summary>較正の読み出しに何回目で成功したか (0 = 全部失敗)。接続の質を見るための情報。</summary>
    public int CalibrationAttemptsUsed { get; private set; }

    /// <summary>
    /// 較正が読めなかったときに、何が起きたか。null なら成功。
    ///
    /// 「読めなかった」には性質の違う失敗が混ざっていて、区別できないと手の打ちようがない:
    /// 応答が一切来ない (書き込みが届いていない/レジスタ初期化が効いていない)、エラー応答が来た
    /// (そのアドレスが読めない個体)、応答は来たが中身が想定と違う (レイアウトが違う)。
    /// 中身が来ている場合は生バイトも載せる。
    /// </summary>
    public string? CalibrationDiagnostics { get; private set; }

    private BalanceBoardDevice(SafeFileHandle readHandle, SafeFileHandle writeHandle)
    {
        _readHandle = readHandle;
        _writeHandle = writeHandle;
        _readStream = new FileStream(readHandle, FileAccess.Read, ReportLength, isAsync: true);
        _writeStream = new FileStream(writeHandle, FileAccess.Write, ReportLength, isAsync: true);
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "BalanceBoardRead" };
    }

    public static BalanceBoardDevice? TryOpen(ushort productId = BalanceBoardProductId)
    {
        foreach (string path in EnumerateHidDevicePaths())
        {
            var handle = OpenHandle(path);
            if (handle.IsInvalid)
            {
                continue;
            }

            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            bool isMatch = NativeMethods.HidD_GetAttributes(handle, ref attributes)
                && attributes.VendorID == NintendoVendorId
                && attributes.ProductID == productId;

            if (!isMatch)
            {
                handle.Dispose();
                continue;
            }

            var writeHandle = OpenHandle(path);
            if (writeHandle.IsInvalid)
            {
                handle.Dispose();
                writeHandle.Dispose();
                continue;
            }

            return new BalanceBoardDevice(handle, writeHandle);
        }

        return null;
    }

    private static SafeFileHandle OpenHandle(string path) => NativeMethods.CreateFile(
        path,
        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
        IntPtr.Zero,
        NativeMethods.OPEN_EXISTING,
        NativeMethods.FILE_FLAG_OVERLAPPED,
        IntPtr.Zero);

    /// <summary>
    /// ステータス要求 → 拡張機能の有効化 → 較正テーブルの読み出し → 連続レポート開始、の順。
    /// 較正テーブルは拡張機能レジスタを初期化したあとでないと読めないので、この順序でないと駄目。
    /// 較正の応答待ちで最大 <paramref name="calibrationTimeoutMs"/> ミリ秒ブロックするので、UIスレッド
    /// からは呼ばないこと。
    /// </summary>
    public void Start(int calibrationTimeoutMs = 1500, int calibrationAttempts = 3)
    {
        // 0x21 応答を拾うために、読みループを先に回し始める。
        _readThread.Start();

        WriteReport(0x15, 0x00);

        // 較正の読み出しは時々すべる。拡張機能レジスタを初期化した直後はまだ読めないことがあり、
        // ハンドルを開いた直後の書き込み自体が空振りすることもある。1回失敗しただけで諦めると
        // 生カウントモードに落ちてしまい、kg で決めた閾値 (在席判定など) が全部意味を失うので、
        // 初期化からやり直して数回試す。待ち時間は 50ms では足りない個体があったため 120ms。
        for (int attempt = 1; attempt <= Math.Max(1, calibrationAttempts); attempt++)
        {
            WriteMemory(0xA400F0, [0x55]);
            Thread.Sleep(120);
            WriteMemory(0xA400FB, [0x00]);
            Thread.Sleep(120);

            Calibration = TryReadFactoryCalibration(calibrationTimeoutMs);
            if (Calibration is not null)
            {
                CalibrationAttemptsUsed = attempt;
                break;
            }
        }

        // 接続待ちの点滅を止める。ボードの青いLEDは「まだホストに認識されていない」間ずっと
        // 点滅し続け、Windows のペアリングが済んだだけでは止まらない。プレイヤーLEDのレポート
        // (0x11) を1本投げて LED1 を点けると点灯に変わる。WiiBalanceWalker が
        // WiimoteLib 経由で SetLEDs(true,false,false,false) を呼んでいるのと同じこと。
        SetLeds(led1: true);

        WriteReport(0x12, 0x04, 0x32);

        // ホストが何も書き返さないと、データを流している最中でもボードは数分で勝手に切断する。
        // 5秒ごとにステータス要求を投げてホストの生存を見せ続ける。タイマースレッドで例外を投げると
        // プロセスごと落ちるので、本物の切断検出は読みループに任せて、ここでは握りつぶす。
        _keepAliveTimer = new Timer(_ =>
        {
            try
            {
                WriteReport(0x15, 0x00);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private FactoryCalibration? TryReadFactoryCalibration(int timeoutMs)
    {
        try
        {
            byte[]? raw = ReadMemory(FactoryCalibration.RegisterAddress, FactoryCalibration.RegisterLength, timeoutMs);
            if (raw is null)
            {
                CalibrationDiagnostics = _readReplyError != 0
                    ? $"エラー応答 (error={_readReplyError})。そのアドレスが読めない個体の可能性。"
                    : _readReplyPartial is null
                        ? "応答が一切来ませんでした (レポート 0x21 が届いていない)。"
                        : $"応答が途中までしか来ませんでした: {Hex(_readReplyPartial)}";
                return null;
            }

            var parsed = FactoryCalibration.TryParse(raw);
            CalibrationDiagnostics = parsed is null
                ? $"24バイト読めましたが、0kg<17kg<34kg の順になっていません: {Hex(raw)}"
                : null;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            CalibrationDiagnostics = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    /// <summary>
    /// レポート 0x17 で拡張機能レジスタを読む。応答はレポート 0x21 で、1通あたり最大16バイトなので
    /// 24バイトは2通に割れて届く。全チャンクが揃うか、タイムアウトするまで待つ。
    /// </summary>
    private byte[]? ReadMemory(int address, int length, int timeoutMs)
    {
        using var complete = new ManualResetEventSlim(false);
        lock (_readReplyLock)
        {
            _readReplyBuffer = new byte[length];
            _readReplyChunks.Clear();
            _readReplyError = 0;
            _readReplyPartial = null;
            _readReplyBase = address & 0xFFFF;
            _readReplyComplete = complete;
        }

        var report = new byte[ReportLength];
        report[0] = 0x17;
        report[1] = 0x04; // アドレス空間: 拡張機能/コントロールレジスタ (0x00 なら本体EEPROM)
        report[2] = (byte)((address >> 16) & 0xFF);
        report[3] = (byte)((address >> 8) & 0xFF);
        report[4] = (byte)(address & 0xFF);
        report[5] = (byte)((length >> 8) & 0xFF);
        report[6] = (byte)(length & 0xFF);
        WriteWithRetry(report);

        bool ok = complete.Wait(timeoutMs);

        lock (_readReplyLock)
        {
            byte[]? result = ok ? _readReplyBuffer : null;
            _readReplyBuffer = null;
            _readReplyComplete = null;
            return result;
        }
    }

    // 0x21 応答: [3] の上位ニブルが (バイト数-1)、下位ニブルがエラーコード (0以外は読めなかった)。
    // [4..5] がアドレスの下位16ビット、[6..] がデータ本体。
    private void HandleReadReply(byte[] buffer)
    {
        int error = buffer[3] & 0x0F;
        int size = ((buffer[3] >> 4) & 0x0F) + 1;
        int address = (buffer[4] << 8) | buffer[5];

        lock (_readReplyLock)
        {
            if (_readReplyBuffer is null || _readReplyComplete is null)
            {
                return;
            }
            if (error != 0)
            {
                // 読めないアドレスを要求した。待っている側をタイムアウトまで待たせずに諦めさせる。
                _readReplyError = error;
                _readReplyBuffer = null;
                _readReplyComplete.Set();
                return;
            }

            int offset = address - _readReplyBase;
            if (offset < 0 || offset + size > _readReplyBuffer.Length)
            {
                return; // こちらの要求と無関係な応答
            }

            Array.Copy(buffer, 6, _readReplyBuffer, offset, size);
            for (int i = 0; i < size; i++)
            {
                _readReplyChunks.Add(offset + i);
            }
            _readReplyPartial = (byte[])_readReplyBuffer.Clone();
            if (_readReplyChunks.Count >= _readReplyBuffer.Length)
            {
                _readReplyComplete.Set();
            }
        }
    }

    /// <summary>
    /// プレイヤーLED (レポート 0x11)。上位ニブルが LED1〜4 で、bit0 は振動。
    /// バランスボードには青いLEDが1つしか無いので、実質 led1 だけが効く。
    /// </summary>
    public void SetLeds(bool led1 = false, bool led2 = false, bool led3 = false, bool led4 = false)
    {
        byte bits = 0;
        if (led1) bits |= 0x10;
        if (led2) bits |= 0x20;
        if (led3) bits |= 0x40;
        if (led4) bits |= 0x80;
        WriteReport(0x11, bits);
    }

    private void WriteMemory(int address, byte[] data)
    {
        var report = new byte[ReportLength];
        report[0] = 0x16;
        report[1] = (byte)((address >> 16) & 0xFF);
        report[2] = (byte)((address >> 8) & 0xFF);
        report[3] = (byte)(address & 0xFF);
        report[4] = (byte)data.Length;
        Array.Copy(data, 0, report, 5, data.Length);
        WriteWithRetry(report);
    }

    private void WriteReport(params byte[] bytes)
    {
        var report = new byte[ReportLength];
        Array.Copy(bytes, report, bytes.Length);
        WriteWithRetry(report);
    }

    // ハンドルを開いた直後の1〜2回の書き込みは、デバイスが正常でも
    // OperationCanceledException/IOException を吐くことがある。数回リトライしてから諦める。
    private void WriteWithRetry(byte[] report)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _writeStream.Write(report, 0, report.Length);
                return;
            }
            catch (Exception ex) when (attempt < 3 && (ex is OperationCanceledException or IOException))
            {
                Thread.Sleep(100);
            }
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[ReportLength];
        string? disconnectReason = null;
        while (!_stopping)
        {
            int read;
            try
            {
                read = _readStream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                disconnectReason = $"{ex.GetType().Name}: {ex.Message}";
                break;
            }

            if (read < 7)
            {
                continue;
            }

            if (buffer[0] == 0x21)
            {
                HandleReadReply(buffer);
                continue;
            }

            if (buffer[0] != 0x32 || read < 11)
            {
                continue;
            }

            // [1..2] = 本体ボタン (ボードには無いので無視)、[3..10] = 4センサー × 2バイト big-endian。
            SensorsReported?.Invoke(new BoardSensors(
                TopRight: (ushort)((buffer[3] << 8) | buffer[4]),
                BottomRight: (ushort)((buffer[5] << 8) | buffer[6]),
                TopLeft: (ushort)((buffer[7] << 8) | buffer[8]),
                BottomLeft: (ushort)((buffer[9] << 8) | buffer[10])));
        }

        if (!_stopping)
        {
            Disconnected?.Invoke(disconnectReason ?? "不明な理由");
        }
    }

    private static IEnumerable<string> EnumerateHidDevicePaths()
    {
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            uint index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    break;
                }
                index++;

                int requiredSize = 0;
                NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

                IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    // SetupDiGetDeviceInterfaceDetail は cbSize を構造体の実サイズではなく歴史的な
                    // パディングの都合の値で検証する: x86 なら 6、x64 なら 8。
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, ref requiredSize, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringAuto(detailBuffer + 4);
                        if (path is not null)
                        {
                            yield return path;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _keepAliveTimer?.Dispose();
        _readStream.Dispose();
        _readHandle.Dispose();
        _writeStream.Dispose();
        _writeHandle.Dispose();
    }
}
