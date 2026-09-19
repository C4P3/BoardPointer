namespace BoardPointer.Core.Mapping;

/// <summary>
/// その人がその姿勢で重心をどこまで動かせるかを測って、以降の計算をその範囲で正規化する。
///
/// 方向ごとに別の値を持つのが要点。座って足先で操作すると、足首を軸にした前後と、脚全体を振る
/// 左右では届く距離が違うし、前後も対称にならない（後ろに倒すほうが難しい、という体感が出る）。
/// ここで方向ごとに割っておくと、下流の応答曲線は「正規化された半径 0〜1」だけを相手にすればよく、
/// 方向ごとに曲線を3本持つ必要がなくなる。調整するつまみが減るぶん、詰めるのが速い。
///
/// 測り方は「一定時間、思い切り大きく動かしてもらって、方向ごとに上位パーセンタイルを取る」。
/// 最大値ではなくパーセンタイルなのは、1回の行き過ぎで可動域が広く登録されると、以降ずっと
/// 最大速度に届かなくなるため。
/// </summary>
public sealed class ReachCalibration
{
    /// <summary>可動域としてこれ以上小さい値は採用しない [mm]。0除算とノイズでの暴走を防ぐ。</summary>
    public const double MinimumReachMm = 8.0;

    /// <summary>方向ごとに必要な最低サンプル数。これに満たない方向は既定値のまま残す。</summary>
    private const int MinimumSamplesPerDirection = 20;

    /// <summary>上位何パーセンタイルを可動域とみなすか。</summary>
    private const double Percentile = 0.90;

    private readonly List<double> _front = [];
    private readonly List<double> _back = [];
    private readonly List<double> _left = [];
    private readonly List<double> _right = [];
    private readonly List<double> _loads = [];

    // 既定値は「とりあえず動く」ための仮置き。必ず実測で置き換えること。
    public double FrontMm { get; set; } = 60;
    public double BackMm { get; set; } = 45;
    public double LeftMm { get; set; } = 70;
    public double RightMm { get; set; } = 70;

    /// <summary>
    /// 測定中の合計荷重の下位25パーセンタイル [kg]。踏み込みの基準（安静時の荷重）の控えめな推定。
    ///
    /// 中央値ではなく下位側を取るのは、可動域の測定中は足を大きく動かしていて、その最中は自然に
    /// 荷重が増えるため。中央値を「安静時」とみなすと基準が重く出て、踏み込みの閾値に一生届かなく
    /// なる。より正しい基準は重心の原点を測ったとき（力を抜いた姿勢）の荷重なので、そちらが
    /// 取れていればそちらを優先する。
    /// </summary>
    public double ReferenceLoadKg { get; private set; }

    public bool IsCalibrated { get; private set; }
    public bool IsSampling { get; private set; }
    public int SampleCount { get; private set; }

    /// <summary>測定中の暫定値。UIに進捗を出すため。</summary>
    public double PreviewFrontMm { get; private set; }
    public double PreviewBackMm { get; private set; }
    public double PreviewLeftMm { get; private set; }
    public double PreviewRightMm { get; private set; }

    public void BeginSampling()
    {
        _front.Clear();
        _back.Clear();
        _left.Clear();
        _right.Clear();
        _loads.Clear();
        SampleCount = 0;
        PreviewFrontMm = PreviewBackMm = PreviewLeftMm = PreviewRightMm = 0;
        IsSampling = true;
    }

    /// <summary>原点合わせ済みの重心を食わせる。信用できないサンプルは捨てる。</summary>
    public void Feed(double copXMm, double copYMm, double totalKg, bool copValid)
    {
        if (!IsSampling || !copValid)
        {
            return;
        }

        SampleCount++;
        _loads.Add(totalKg);
        if (copXMm >= 0)
        {
            _right.Add(copXMm);
            PreviewRightMm = Math.Max(PreviewRightMm, copXMm);
        }
        else
        {
            _left.Add(-copXMm);
            PreviewLeftMm = Math.Max(PreviewLeftMm, -copXMm);
        }

        if (copYMm >= 0)
        {
            _front.Add(copYMm);
            PreviewFrontMm = Math.Max(PreviewFrontMm, copYMm);
        }
        else
        {
            _back.Add(-copYMm);
            PreviewBackMm = Math.Max(PreviewBackMm, -copYMm);
        }
    }

    /// <param name="shareLeftRight">
    /// 左右を平均して同じ値にする。左右は体感上ほぼ対称になるので、揃えたほうが操作が素直になる
    /// ことが多い。前後は対称にならないので、こちらは常に別々。
    /// </param>
    public void FinishSampling(bool shareLeftRight = true)
    {
        FrontMm = Reduce(_front, FrontMm);
        BackMm = Reduce(_back, BackMm);
        double left = Reduce(_left, LeftMm);
        double right = Reduce(_right, RightMm);

        if (shareLeftRight)
        {
            left = right = (left + right) / 2.0;
        }
        LeftMm = left;
        RightMm = right;
        if (_loads.Count > 0)
        {
            var sortedLoads = _loads.ToArray();
            Array.Sort(sortedLoads);
            int index = Math.Clamp((int)Math.Round((sortedLoads.Length - 1) * 0.25), 0, sortedLoads.Length - 1);
            ReferenceLoadKg = sortedLoads[index];
        }

        IsSampling = false;
        IsCalibrated = true;
    }

    public void Cancel() => IsSampling = false;

    private static double Reduce(List<double> values, double fallback)
    {
        if (values.Count < MinimumSamplesPerDirection)
        {
            return fallback;
        }
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int index = Math.Clamp((int)Math.Round((sorted.Length - 1) * Percentile), 0, sorted.Length - 1);
        return Math.Max(MinimumReachMm, sorted[index]);
    }

    /// <summary>
    /// 重心 [mm] を方向ごとの可動域で割って、-1〜1 くらいの無次元量にする。
    /// ここで方向の非対称をすべて吸収するので、下流は円だけを考えればよくなる。
    /// </summary>
    public (double X, double Y) Normalize(double copXMm, double copYMm)
    {
        double scaleX = Math.Max(MinimumReachMm, copXMm >= 0 ? RightMm : LeftMm);
        double scaleY = Math.Max(MinimumReachMm, copYMm >= 0 ? FrontMm : BackMm);
        return (copXMm / scaleX, copYMm / scaleY);
    }
}
