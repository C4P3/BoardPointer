using BoardPointer.Core.Pipeline;

namespace BoardPointer.Core.Mapping;

/// <summary>マッピング1回ぶんの結果。速度は画面座標系 (右が正、下が正)。</summary>
/// <param name="VelocityXPxPerSec">画面X方向の速度 [px/秒]。</param>
/// <param name="VelocityYPxPerSec">画面Y方向の速度 [px/秒]。</param>
/// <param name="NormalizedX">可動域で正規化した重心X。</param>
/// <param name="NormalizedY">同Y (前が正のまま。画面座標への反転はここでは済ませない)。</param>
/// <param name="NormalizedRadius">正規化半径。応答曲線の入力そのもの。</param>
/// <param name="Active">出力すべき状態か。乗っていない / 重心が信用できないときは false。</param>
/// <param name="InDeadzone">デッドゾーンの内側にいるか。</param>
/// <param name="PressureRatio">合計荷重が基準（可動域測定時の中央値）の何倍か。</param>
/// <param name="PressureFactor">踏み込みによる速度の倍率 (0〜1)。踏み込みモードが切なら常に1。</param>
/// <param name="Engaged">実際にカーソルを動かしている状態か。原点の自動追従はこれが false の間だけ。</param>
public readonly record struct PointerCommand(
    double VelocityXPxPerSec,
    double VelocityYPxPerSec,
    double NormalizedX,
    double NormalizedY,
    double NormalizedRadius,
    bool Active,
    bool InDeadzone,
    double PressureRatio,
    double PressureFactor,
    bool Engaged);

/// <summary>
/// 重心 → カーソル速度。レート制御 (傾けている間ずっと動く) であって、位置制御ではない。
///
/// 足で操作するならこちらが有利で、理由は2つ。中立に足を戻せること（絶対座標だとカーソルは常に
/// 足のある場所にあり、休めない）。それと、速度は積分されるので重心のノイズが打ち消し合うこと。
/// 15kg のときの 1mm 程度のジッタは、位置に直結すると震えになるが、速度に入れると見えなくなる。
///
/// 段の構成は「方向ごとに正規化 → 正規化空間で半径 → 1本の応答曲線 → 方向に分配」。
/// 正規化で非対称を吸収してから半径を取るので、実空間でのデッドゾーンは楕円になり（揺れの形に
/// 合っていて正しい）、しかも斜め方向で軸スナップが出ない。
/// </summary>
public sealed class PointerMapper
{
    // 直近の踏み込み比を覚えておくための輪。閾値をいくつにすれば届くのかは、その人がその姿勢で
    // どれだけ荷重を動かせるか次第で、事前には決められない。実際に出ている範囲を見せるのが早い。
    private const int RatioWindowSamples = 500; // 100Hz で約5秒
    private readonly double[] _ratioWindow = new double[RatioWindowSamples];
    private int _ratioWriteIndex;
    private int _ratioCount;

    private bool _clutchEngaged;

    public ReachCalibration Reach { get; } = new();
    public ResponseCurve Curve { get; } = new();

    /// <summary>足を前に出したときにカーソルを上へ動かす (既定)。反転させたいときに true。</summary>
    public bool InvertY { get; set; }

    /// <summary>踏み込みの強さ (合計荷重) をどう使うか。</summary>
    public PressureMode Pressure { get; set; } = PressureMode.Off;

    /// <summary>
    /// 動き始める荷重比 [安静時の荷重に対する比]。
    ///
    /// <see cref="PressureFullRatio"/> との大小で向きが決まる。全開側のほうが小さければ
    /// 「軽くするほど速い」、大きければ「踏み込むほど速い」。
    /// </summary>
    public double PressureEngageRatio { get; set; } = 0.97;

    /// <summary>
    /// 倍率が 1.0 になる荷重比。<see cref="PressureEngageRatio"/> より小さくしてよい。
    ///
    /// 既定は軽くする向き (0.97 → 0.85)。実測では、座って足先で操作しているときの合計荷重は
    /// 安静時の 0.82〜1.13 倍の範囲でしか動かなかった。踏み込む側は椅子を押し返す必要があって
    /// 可動幅が取りにくく、足を浮かせ気味にする側のほうが細かく制御しやすい。
    /// </summary>
    public double PressureFullRatio { get; set; } = 0.85;

    /// <summary>クラッチの復帰幅 [比]。閾値ちょうどで入り切りが反転するのを防ぐ。</summary>
    private const double ClutchHysteresis = 0.03;

    /// <summary>軽くする向きなら true。UI に「どちらに動かせば効くか」を出すため。</summary>
    public bool PressureEngagesWhenLighter => PressureFullRatio < PressureEngageRatio;

    /// <summary>
    /// 踏み込みの基準となる安静時の合計荷重 [kg]。0 なら未測定。
    ///
    /// 重心の原点を測ったとき（操作する姿勢で力を抜いた状態）の荷重を入れるのが本筋で、
    /// 無ければ可動域測定時の下位25パーセンタイルで代用する。呼び出し側が設定する。
    /// </summary>
    public double ReferenceLoadKg { get; set; }

    /// <summary>踏み込みモードが実際に機能するか。基準荷重が無ければ比を出せないので false。</summary>
    public bool PressureIsAvailable => ReferenceLoadKg > 0.1;

    public void Reset()
    {
        _clutchEngaged = false;
        _ratioCount = 0;
        _ratioWriteIndex = 0;
    }

    /// <summary>直近数秒で実際に出ていた踏み込み比の範囲。閾値を決めるための目安。</summary>
    public (double Min, double Max) RecentRatioRange()
    {
        int count = Math.Min(_ratioCount, RatioWindowSamples);
        if (count == 0)
        {
            return (1, 1);
        }
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            double v = _ratioWindow[i];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        return (min, max);
    }

    /// <summary>
    /// 踏み込みの倍率を出す。<see cref="PressureMode.Clutch"/> にはヒステリシスを入れてある ---
    /// 閾値ちょうどで踏んでいると、入り切りが毎サンプル反転してカーソルが断続的に動く。
    /// </summary>
    private double PressureFactorFor(double ratio)
    {
        // 基準荷重が未測定のときは比が出せない。ここで閾値判定に進むと、比が常に 1.00 になって
        // 開始閾値 (既定 1.15) を一生超えられず、「踏み込みモードにするとマウスが一切動かない」
        // という無言の故障になる。判定できないなら踏み込みは使わない、が正しい。
        if (!PressureIsAvailable)
        {
            _clutchEngaged = false;
            return 1;
        }

        // 向きは2つの閾値の大小だけで決まる。span が負なら「軽くするほど速い」になり、
        // 下の式はどちらの向きでもそのまま正しい値を返す --- 分子と分母の符号が一緒に反転するため。
        double span = PressureFullRatio - PressureEngageRatio;
        if (Math.Abs(span) < 1e-6)
        {
            return 1;
        }

        switch (Pressure)
        {
            case PressureMode.Clutch:
                // 作動側の向きに合わせてヒステリシスの向きも変える。
                double signedDistance = (ratio - PressureEngageRatio) / span;
                double threshold = _clutchEngaged ? -ClutchHysteresis / Math.Abs(span) : 0;
                _clutchEngaged = signedDistance >= threshold;
                return _clutchEngaged ? 1 : 0;

            case PressureMode.Throttle:
                return Math.Clamp((ratio - PressureEngageRatio) / span, 0, 1);

            default:
                return 1;
        }
    }

    public PointerCommand Update(in BoardFrame frame)
    {
        // 乗っていない、あるいは荷重不足で重心が信用できないときは、何も出さない。
        // ここを素通りさせると、足を離した瞬間にカーソルが走る。
        if (!frame.Present || !frame.CopValid)
        {
            _clutchEngaged = false;
            return new PointerCommand(0, 0, 0, 0, 0, Active: false, InDeadzone: false, 0, 0, Engaged: false);
        }

        // 踏み込みの強さ。基準は可動域を測ったときの合計荷重の中央値なので、「測定時と同じくらい
        // の置き方」が 1.0 になる。足を載せているだけでは動かず、踏み込んだときだけ動く、という
        // クラッチが作れる --- 休めるうえ、足を外したときの暴れも二重に防げる。
        double pressureRatio = PressureIsAvailable ? frame.TotalKg / ReferenceLoadKg : 1.0;
        _ratioWindow[_ratioWriteIndex] = pressureRatio;
        _ratioWriteIndex = (_ratioWriteIndex + 1) % RatioWindowSamples;
        _ratioCount++;
        double pressureFactor = PressureFactorFor(pressureRatio);

        var (nx, ny) = Reach.Normalize(frame.CopXFilteredMm, frame.CopYFilteredMm);
        double radius = Math.Sqrt(nx * nx + ny * ny);

        double speed = Curve.SpeedAt(radius) * pressureFactor;
        if (speed <= 0 || radius <= 1e-9)
        {
            bool inDeadzone = radius <= Curve.Deadzone;
            return new PointerCommand(0, 0, nx, ny, radius, Active: true, InDeadzone: inDeadzone,
                pressureRatio, pressureFactor, Engaged: false);
        }

        // 速度は半径方向へ。単位ベクトルに掛けるので、斜めでも速度の大きさが曲線どおりになる。
        double vx = speed * nx / radius;
        double vyBoard = speed * ny / radius;

        // ボードのYは前が正。画面のYは下が正なので、既定では符号を反転する。
        double vyScreen = InvertY ? vyBoard : -vyBoard;

        return new PointerCommand(vx, vyScreen, nx, ny, radius, Active: true, InDeadzone: false,
            pressureRatio, pressureFactor, Engaged: true);
    }
}

/// <summary>踏み込み (合計荷重) の使い方。</summary>
public enum PressureMode
{
    /// <summary>使わない。重心の位置だけで動く。</summary>
    Off,

    /// <summary>クラッチ。踏み込んでいる間だけ動く。足を載せて休んでいても動かない。</summary>
    Clutch,

    /// <summary>速度に乗せる。強く踏むほど速い。クラッチの性質も兼ねる (閾値未満は 0 倍)。</summary>
    Throttle,
}
