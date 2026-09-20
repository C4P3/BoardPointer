using System.Runtime.InteropServices;

namespace BoardPointer.Core.Mapping;

/// <summary>カーソルの動かし方。どちらが正しいではなく、**受け取る相手が違う**。</summary>
public enum PointerMode
{
    /// <summary>
    /// 絶対座標。現在位置を読んで、足した先へ送る。Windows のカーソルを動かす相手向け。
    ///
    /// ポインタ加速 (「ポインターの精度を高める」) を迂回できるので、設計した応答曲線が
    /// そのままカーソルの速度になる。デスクトップや普通のアプリを操作するならこちら。
    /// </summary>
    Absolute,

    /// <summary>
    /// 相対デルタ。移動量だけを送る。Raw Input を読む相手 (3D の視点操作) 向け。
    ///
    /// 絶対座標が効かない相手が存在する。詳しくは <see cref="RelativeDeltaSink"/>。
    /// </summary>
    Relative,
}

/// <summary>整数ピクセルの移動量を、実際の入力として送る先。</summary>
public interface IPointerSink
{
    void Send(int dx, int dy);
}

/// <summary>
/// 絶対座標で送る。現在位置を読んで、足した先へ飛ばす。
///
/// 相対移動 (MOUSEEVENTF_MOVE 単体) ではなくこちらにしてあるのは、相対移動が Windows の
/// ポインタ加速を通るため。せっかく設計した応答曲線の上からもう一本カーブがかかり、何を
/// 調整しているのか分からなくなる。絶対座標なら加速を迂回でき、曲線がそのままカーソルの
/// 速度になる。
///
/// 現在位置を毎回読み直しているので、物理マウスと同時に使っても喧嘩しない。
/// </summary>
public sealed class AbsoluteCursorSink : IPointerSink
{
    public void Send(int dx, int dy)
    {
        if (!Win32Mouse.TryGetCursorPos(out int cursorX, out int cursorY))
        {
            return;
        }

        var (originX, originY, width, height) = Win32Mouse.VirtualScreen();
        if (width <= 1 || height <= 1)
        {
            return;
        }

        // 画面の外には出せないので、ここで仮想デスクトップに収める。
        //
        // この clamp は絶対座標モードの性質そのもので、相対モードには無い。3D の視点操作の
        // ように「カーソルが画面外へ出続ける」ことを前提にした相手では、端に張り付いた時点で
        // 以降の移動量が 0 になり、視点が止まる。だから相手によってモードを分ける必要がある。
        int x = Math.Clamp(cursorX + dx, originX, originX + width - 1);
        int y = Math.Clamp(cursorY + dy, originY, originY + height - 1);

        Win32Mouse.Send(
            Win32Mouse.MOUSEEVENTF_MOVE | Win32Mouse.MOUSEEVENTF_ABSOLUTE | Win32Mouse.MOUSEEVENTF_VIRTUALDESK,
            (int)Math.Round((x - originX) * 65535.0 / (width - 1)),
            (int)Math.Round((y - originY) * 65535.0 / (height - 1)));
    }
}

/// <summary>
/// 相対デルタで送る。移動量だけを渡し、現在位置には触らない。
///
/// 絶対座標モードが 3D の視点操作で効かない理由は2つあって、どちらも「絶対座標である」ことに
/// 由来する:
///
///   1. 画面端で止まる。視点操作のゲームはカーソルを隠して画面中央にロックする (ClipCursor か
///      毎フレームの再センタリング) ので、カーソルは端に張り付き、その先の移動量が clamp で
///      0 になる。加えて、ゲームの再センタリングと GetCursorPos → SendInput の間がレースに
///      なり、こちらの絶対座標がゲームの再センタリングを打ち消してしまう。
///
///   2. Raw Input での扱い。絶対移動は WM_INPUT に MOUSE_MOVE_ABSOLUTE フラグ付きで届く。
///      ゲーム側は lLastX/lLastY を相対デルタとしか見ていないことが多く、無視されるか、
///      無茶な値として解釈される。
///
/// **このモードでポインタ加速を心配しなくていい理由**: Raw Input には加速 (ballistics) を
/// 通す前のデルタが渡る。つまり絶対座標で回避したかった加速は、Raw Input を読むゲームには
/// 最初からかからない。濁るのは「OS カーソル経由で相対デルタを読む古いウィンドウモードの
/// ゲーム」だけで、そこは EPP の影響を受ける (<see cref="PointerSettings"/> で検出して
/// UI に出している)。
///
/// 逆に、速度スライダーや SmoothMouseXCurve の逆関数を噛ませる補正は**入れていない**。
/// 加速がかからない Raw Input 経路でかえって狂う。代わりにモードごとに独立したゲインを持たせ、
/// ゲーム側の感度スライダーと合わせて詰める。
/// </summary>
public sealed class RelativeDeltaSink : IPointerSink
{
    public void Send(int dx, int dy) => Win32Mouse.Send(Win32Mouse.MOUSEEVENTF_MOVE, dx, dy);
}

/// <summary>
/// Windows 側のポインタ設定。相対モードのときだけ意味を持つ。
///
/// 相対で送ると速度スライダーの倍率がかかり、EPP が入っていれば移動量に応じたカーブも
/// かかる。こちらから消せる設定ではない (勝手に書き換えるのは論外) ので、検出して知らせる
/// ところまでをやる。
/// </summary>
public static class PointerSettings
{
    /// <param name="EnhancePointerPrecision">「ポインターの精度を高める」が入っているか。</param>
    /// <param name="SpeedSlider">ポインタの速度 1〜20。既定は 10 (等倍)。</param>
    public readonly record struct Values(bool EnhancePointerPrecision, int SpeedSlider);

    public static Values Read()
    {
        // SPI_GETMOUSE は3つの int を返す。3つ目 (加速の段) が 0 でなければ EPP が入っている。
        int[] mouse = new int[3];
        bool ok = Win32Mouse.SystemParametersInfo(Win32Mouse.SPI_GETMOUSE, 0, mouse, 0);
        bool epp = ok && mouse[2] != 0;

        int speed = 10;
        int[] speedValue = new int[1];
        if (Win32Mouse.SystemParametersInfo(Win32Mouse.SPI_GETMOUSESPEED, 0, speedValue, 0) && speedValue[0] > 0)
        {
            speed = speedValue[0];
        }

        return new Values(epp, speed);
    }
}

/// <summary>
/// user32 のマウス関連の宣言。
///
/// 出力先が2つに分かれた以上、同じ INPUT 構造体と SendInput の宣言を両方に持たせると、
/// 片方だけ直して気づかない類の食い違いが出る。宣言は1か所に置く。
/// </summary>
internal static class Win32Mouse
{
    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    internal const uint SPI_GETMOUSE = 0x0003;
    internal const uint SPI_GETMOUSESPEED = 0x0070;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int INPUT_MOUSE = 0;

    internal static (int OriginX, int OriginY, int Width, int Height) VirtualScreen() => (
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    internal static bool TryGetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out POINT point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    internal static void Send(uint flags, int dx = 0, int dy = 0)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    internal static void SendPair(uint firstFlags, uint secondFlags)
    {
        INPUT[] inputs =
        [
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = firstFlags } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = secondFlags } } },
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint winIni);
}
