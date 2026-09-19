namespace BoardPointer.Core.Mapping;

/// <summary>
/// 荷重の閾値を実測から決める。
///
/// 可動域と同じ考え方。「作動 0.97 倍」が自分にとって近いのか遠いのかは、事前に分かるものでは
/// ない。実測では、既定値だと 81% の時間が倍率 0 に張り付き、**全開には一度も届いていなかった**
/// (2つの記録で 0.0% / 0.0%)。「全力が分かりにくい」のは、体感したことがないからで、つまみの
/// 説明を足しても解決しない。測るのが正しい。
///
/// 一番軽くする／一番踏み込む、を数回繰り返してもらい、その分布から閾値を置く。上下の端ではなく
/// パーセンタイルを取るのは、1回の行き過ぎで閾値が遠くに登録されると以降ずっと届かなくなるため。
/// </summary>
public sealed class LoadRangeCalibration
{
    /// <summary>端の外れ値を避けるためのパーセンタイル。</summary>
    private const double LowPercentile = 0.05;
    private const double HighPercentile = 0.95;

    /// <summary>安静時 (比 1.0) から作動点までどれだけ離すか。測った幅に対する割合。</summary>
    private const double EngageMargin = 0.10;

    /// <summary>到達端から全開点をどれだけ内側に置くか。測った幅に対する割合。</summary>
    private const double FullMargin = 0.10;

    private const int MinimumSamples = 100;

    private readonly List<double> _ratios = [];

    public bool IsSampling { get; private set; }
    public int SampleCount => _ratios.Count;

    /// <summary>測定中に実際に出ていた比の範囲。UI に進捗として出す。</summary>
    public double ObservedMin { get; private set; } = 1.0;
    public double ObservedMax { get; private set; } = 1.0;

    public void BeginSampling()
    {
        _ratios.Clear();
        ObservedMin = 1.0;
        ObservedMax = 1.0;
        IsSampling = true;
    }

    public void Feed(double ratio, bool available)
    {
        if (!IsSampling || !available)
        {
            return;
        }
        _ratios.Add(ratio);
        ObservedMin = Math.Min(ObservedMin, ratio);
        ObservedMax = Math.Max(ObservedMax, ratio);
    }

    public void Cancel() => IsSampling = false;

    /// <summary>
    /// 測定を終えて閾値を返す。軽くする側と踏み込む側の、**よく出ていたほう**を採る。
    /// どちらへ動かしたかは人と姿勢で変わるので、こちらで決め打ちしない。
    /// </summary>
    /// <returns>(作動, 全開)。サンプルが足りなければ null。</returns>
    public (double Engage, double Full)? FinishSampling()
    {
        IsSampling = false;
        if (_ratios.Count < MinimumSamples)
        {
            return null;
        }

        var sorted = _ratios.ToArray();
        Array.Sort(sorted);
        double low = sorted[Math.Clamp((int)Math.Round((sorted.Length - 1) * LowPercentile), 0, sorted.Length - 1)];
        double high = sorted[Math.Clamp((int)Math.Round((sorted.Length - 1) * HighPercentile), 0, sorted.Length - 1)];

        double lighterSpan = Math.Max(0, 1.0 - low);
        double heavierSpan = Math.Max(0, high - 1.0);

        // 幅が広いほうを操作方向とみなす。どちらもほぼ無いなら決められない。
        if (Math.Max(lighterSpan, heavierSpan) < 0.02)
        {
            return null;
        }

        return lighterSpan >= heavierSpan
            ? (1.0 - lighterSpan * EngageMargin, low + lighterSpan * FullMargin)
            : (1.0 + heavierSpan * EngageMargin, high - heavierSpan * FullMargin);
    }
}
