using System.Runtime.InteropServices;

namespace BoardPointer.Core.Mapping;

/// <summary>
/// カーソルを動かす。速度 [px/秒] を受け取り、端数を溜めながら実際の入力を送る。
///
/// 相対移動 (MOUSEEVENTF_MOVE 単体) ではなく、現在位置を読んで足した先へ絶対座標で送っている。
/// 相対移動は Windows のポインタ加速 (「ポインターの精度を高める」) を通るので、せっかく設計した
/// 応答曲線の上からもう一本カーブがかかってしまい、何を調整しているのか分からなくなる。絶対座標
/// なら加速を迂回でき、曲線がそのままカーソルの速度になる。
///
/// 現在位置を毎回読み直しているので、物理マウスと同時に使っても喧嘩しない。
/// </summary>
public sealed class MouseOutput
{
    private readonly SubPixelAccumulator _accumulator = new();

    /// <summary>直近に送った移動量の累計 [px]。動作確認用。</summary>
    public long TotalDxPx { get; private set; }
    public long TotalDyPx { get; private set; }

    public void Reset()
    {
        _accumulator.Reset();
        TotalDxPx = 0;
        TotalDyPx = 0;
    }

    public void Apply(double velocityXPxPerSec, double velocityYPxPerSec, double dtSeconds)
    {
        var (dx, dy) = _accumulator.Accumulate(velocityXPxPerSec, velocityYPxPerSec, dtSeconds);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        TotalDxPx += dx;
        TotalDyPx += dy;
        MoveBy(dx, dy);
    }

    /// <summary>
    /// 実際にカーソルが指示どおり動くかを1回だけ試して、元の位置に戻す。
    ///
    /// 絶対座標への変換 (仮想デスクトップの原点とサイズ、65535 への正規化) を間違えていても
    /// コンパイルは通るし、マルチモニタや高DPIでしか出ない形でずれる。送って読んで戻す、という
    /// 実測が一番早い。
    /// </summary>
    /// <returns>指示した移動量と、実際に動いた量。</returns>
    public static (int RequestedDx, int RequestedDy, int ActualDx, int ActualDy) SelfTest(int dx = 10, int dy = 7)
    {
        if (!GetCursorPos(out POINT before))
        {
            return (dx, dy, 0, 0);
        }

        MoveBy(dx, dy);
        Thread.Sleep(30); // 入力キューが処理されるのを待つ
        GetCursorPos(out POINT after);

        // 元に戻す。テストのためにユーザーのカーソルを置き去りにしない。
        MoveBy(before.X - after.X, before.Y - after.Y);

        return (dx, dy, after.X - before.X, after.Y - before.Y);
    }

    /// <summary>
    /// クリックを1回送る。押してすぐ離す。
    ///
    /// カーソルを動かせても押せなければマウスとして完成しないので、これは親切機能ではなく本体の
    /// 一部。今はキーボードのショートカットから呼ぶが、送っている入力自体は本物のマウスと同じ
    /// なので、あとで足の動作 (荷重のタップなど) から呼ぶようにしても出力側は変えなくていい。
    /// </summary>
    public static void Click(bool rightButton = false)
    {
        uint down = rightButton ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint up = rightButton ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;

        INPUT[] inputs =
        [
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = down } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = up } } },
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void MoveBy(int dx, int dy)
    {
        if (!GetCursorPos(out POINT cursor))
        {
            return;
        }

        int originX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int originY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 1 || height <= 1)
        {
            return;
        }

        int x = Math.Clamp(cursor.X + dx, originX, originX + width - 1);
        int y = Math.Clamp(cursor.Y + dy, originY, originY + height - 1);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - originX) * 65535.0 / (width - 1)),
                    dy = (int)Math.Round((y - originY) * 65535.0 / (height - 1)),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
