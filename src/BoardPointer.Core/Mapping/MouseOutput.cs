namespace BoardPointer.Core.Mapping;

/// <summary>
/// カーソルを動かす。速度 [px/秒] を受け取り、端数を溜めながら実際の入力を送る。
///
/// 送り方は2つあり (<see cref="PointerMode"/>)、切り替えるのは好みではなく**相手**による。
///
///   絶対座標 … Windows のカーソルを動かす相手 (デスクトップ、普通のアプリ)。
///              ポインタ加速を迂回できるので、設計した応答曲線がそのまま速度になる。
///   相対     … Raw Input を読む相手 (3D の視点操作)。絶対座標だと画面端の clamp で
///              視点が止まり、MOUSE_MOVE_ABSOLUTE 付きのイベントは無視されやすい。
///
/// 自動判定はしていない。前面ウィンドウからの推定は当たらないので、明示的に切り替える。
/// 詳しい理由は <see cref="AbsoluteCursorSink"/> と <see cref="RelativeDeltaSink"/> に書いた。
///
/// 端数の持ち越し (<see cref="SubPixelAccumulator"/>) は両モードで共通。速度→整数ピクセルの
/// 変換はモードに依らないうえ、低速域が丸ごと死ぬかどうかがここで決まるので、出力先ごとに
/// 持たせて片方だけ直し忘れる形にはしない。
/// </summary>
public sealed class MouseOutput
{
    private readonly SubPixelAccumulator _accumulator = new();
    private readonly AbsoluteCursorSink _absolute = new();
    private readonly RelativeDeltaSink _relative = new();
    private volatile bool _relativeMode;

    /// <summary>直近に送った移動量の累計 [px]。動作確認用。</summary>
    public long TotalDxPx { get; private set; }
    public long TotalDyPx { get; private set; }

    /// <summary>
    /// 送り方。切り替えると端数を捨てる --- 溜まっていた端数は前のモードの縮尺のもので、
    /// 持ち越すと切り替えた瞬間に1px飛ぶ。
    /// </summary>
    public PointerMode Mode
    {
        get => _relativeMode ? PointerMode.Relative : PointerMode.Absolute;
        set
        {
            bool relative = value == PointerMode.Relative;
            if (relative == _relativeMode)
            {
                return;
            }
            _relativeMode = relative;
            _accumulator.Reset();
        }
    }

    /// <summary>
    /// 相対モードだけにかかる速度の倍率。
    ///
    /// 相対モードの「px」は実際の画面のピクセルではなく、ゲーム側が好きに解釈するカウント。
    /// 絶対座標モードで詰めた最大速度をそのまま送っても、視点の回る速さは全く別物になる。
    /// かといって Windows 側の倍率を逆算して補正すると、加速がかからない Raw Input 経路で
    /// かえって狂う。モードごとに独立した倍率を持たせて、ゲーム側の感度と合わせて詰めるのが
    /// 素直。絶対座標モードには倍率を置かない (曲線が実ピクセルで、掛けたら意味が濁るだけ)。
    /// </summary>
    public double RelativeGain { get; set; } = 1.0;

    private IPointerSink Sink => _relativeMode ? _relative : (IPointerSink)_absolute;

    public void Reset()
    {
        _accumulator.Reset();
        TotalDxPx = 0;
        TotalDyPx = 0;
    }

    public void Apply(double velocityXPxPerSec, double velocityYPxPerSec, double dtSeconds)
    {
        if (_relativeMode)
        {
            double gain = Math.Clamp(RelativeGain, MinRelativeGain, MaxRelativeGain);
            velocityXPxPerSec *= gain;
            velocityYPxPerSec *= gain;
        }

        var (dx, dy) = _accumulator.Accumulate(velocityXPxPerSec, velocityYPxPerSec, dtSeconds);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        TotalDxPx += dx;
        TotalDyPx += dy;
        Sink.Send(dx, dy);
    }

    public const double MinRelativeGain = 0.05;
    public const double MaxRelativeGain = 10.0;

    /// <summary>
    /// 実際にカーソルが指示どおり動くかを1回だけ試して、元の位置に戻す。
    ///
    /// 絶対座標への変換 (仮想デスクトップの原点とサイズ、65535 への正規化) を間違えていても
    /// コンパイルは通るし、マルチモニタや高DPIでしか出ない形でずれる。送って読んで戻す、という
    /// 実測が一番早い。
    ///
    /// 相対モードでも同じ枠で測れるようにしてある。こちらは変換の検算ではなく、**Windows 側の
    /// 倍率と加速がどれだけ乗っているか**が数字で出る。10px 指示して 6px しか動かなければ
    /// 速度スライダーが効いているし、指示と実測の比が移動量によって変わるなら EPP が入っている。
    /// </summary>
    /// <returns>指示した移動量と、実際に動いた量。</returns>
    public static (int RequestedDx, int RequestedDy, int ActualDx, int ActualDy) SelfTest(
        PointerMode mode = PointerMode.Absolute, int dx = 10, int dy = 7)
    {
        if (!Win32Mouse.TryGetCursorPos(out int beforeX, out int beforeY))
        {
            return (dx, dy, 0, 0);
        }

        IPointerSink sink = mode == PointerMode.Relative
            ? new RelativeDeltaSink()
            : new AbsoluteCursorSink();
        sink.Send(dx, dy);
        Thread.Sleep(30); // 入力キューが処理されるのを待つ
        Win32Mouse.TryGetCursorPos(out int afterX, out int afterY);

        // 戻すのは必ず絶対座標で。相対で戻すと、いま測ったばかりの倍率と加速がそこにも乗って
        // 元の位置に着かない。テストのためにユーザーのカーソルを置き去りにしない。
        new AbsoluteCursorSink().Send(beforeX - afterX, beforeY - afterY);

        return (dx, dy, afterX - beforeX, afterY - beforeY);
    }

    /// <summary>
    /// クリックを1回送る。押してすぐ離す。
    ///
    /// カーソルを動かせても押せなければマウスとして完成しないので、これは親切機能ではなく本体の
    /// 一部。今はキーボードのショートカットから呼ぶが、送っている入力自体は本物のマウスと同じ
    /// なので、あとで足の動作 (荷重のタップなど) から呼ぶようにしても出力側は変えなくていい。
    ///
    /// クリックはモードに依らない。押した/離したに座標は要らないので、絶対座標と相対を
    /// 分ける理由がそもそも無い。
    /// </summary>
    public static void Click(bool rightButton = false)
    {
        Win32Mouse.SendPair(
            rightButton ? Win32Mouse.MOUSEEVENTF_RIGHTDOWN : Win32Mouse.MOUSEEVENTF_LEFTDOWN,
            rightButton ? Win32Mouse.MOUSEEVENTF_RIGHTUP : Win32Mouse.MOUSEEVENTF_LEFTUP);
    }
}
