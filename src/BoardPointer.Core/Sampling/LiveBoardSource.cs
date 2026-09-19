using System.Diagnostics;
using BoardPointer.Core.Hid;

namespace BoardPointer.Core.Sampling;

/// <summary>実機のボードをサンプル源として見せる薄いラッパ。タイムスタンプはここで付ける。</summary>
public sealed class LiveBoardSource : ISampleSource
{
    private readonly BalanceBoardDevice _device;
    private readonly Stopwatch _clock = new();
    private FactoryCalibration? _stored;

    public event Action<RawSample>? SampleReceived;
    public event Action<string>? Ended;

    /// <summary>ボードから読めた較正。読めなければ、前回保存しておいたものを使う。</summary>
    public FactoryCalibration? Calibration => _device.Calibration ?? _stored;

    /// <summary>今使っている較正が、保存しておいたものか (= 今回は読めなかったか)。</summary>
    public bool CalibrationIsFromStore => _device.Calibration is null && _stored is not null;

    /// <summary>較正が読めなかったときの理由。null なら成功。</summary>
    public string? CalibrationDiagnostics => _device.CalibrationDiagnostics;
    public string Description { get; }

    private LiveBoardSource(BalanceBoardDevice device, string description)
    {
        _device = device;
        Description = description;
        _device.SensorsReported += OnSensors;
        _device.Disconnected += reason => Ended?.Invoke(reason);
    }

    /// <summary>
    /// 既にペアリング済みのボードを開く。見つからなければ null --- 呼び出し側はそこで
    /// <see cref="Bluetooth.BalanceBoardPairing"/> のSYNC手順に落とす。
    /// </summary>
    public static LiveBoardSource? TryOpen()
    {
        var device = BalanceBoardDevice.TryOpen();
        return device is null ? null : new LiveBoardSource(device, "Wii Balance Board (HID)");
    }

    public void Start()
    {
        _clock.Restart();
        _device.Start(); // 較正読み出しの応答待ちで最大1.5秒ブロックする

        // 読めたら保存、読めなければ前回のものを使う。工場較正はボード固有の定数なので、
        // 読み出しの成否が揺れるたびに精度を捨てる理由がない。
        if (_device.Calibration is not null)
        {
            CalibrationStore.TrySave(_device.Calibration);
        }
        else
        {
            _stored = CalibrationStore.TryLoad();
        }
    }

    public void Stop() => _device.Dispose();

    private void OnSensors(BoardSensors sensors) =>
        SampleReceived?.Invoke(new RawSample(
            _clock.ElapsedMilliseconds,
            sensors.TopRight,
            sensors.BottomRight,
            sensors.TopLeft,
            sensors.BottomLeft));

    public void Dispose() => _device.Dispose();
}
