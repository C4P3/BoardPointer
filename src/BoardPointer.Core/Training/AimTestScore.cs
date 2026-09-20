namespace BoardPointer.Core.Training;

/// <summary>的の大きさごとの成績。</summary>
/// <param name="SuccessRate">時間内に維持まで到達できた割合。小さい的だけ落ちるなら止め際の問題。</param>
/// <param name="NeverTouched">一度も的に入れなかった試行の数。入れたが保てなかったのとは別物。</param>
public readonly record struct AimSizeScore(
    double DiameterPx,
    int Trials,
    double SuccessRate,
    int NeverTouched,
    double MedianFirstTouchMs,
    double MeanSettleMs,
    double MeanReEntries,
    double MedianDriftPx);

/// <summary>方向ごとの成績。可動域の非対称がここに出る。</summary>
public readonly record struct AimDirectionScore(
    string Label,
    int Trials,
    double SuccessRate,
    int NeverTouched,
    double MedianFirstTouchMs,
    double MeanSettleMs,
    double MedianPeakRadius);

/// <summary>
/// 試行の集計。
///
/// 中央値を使うのは、レート制御の試行が右に長い裾を持つため --- 1回の行き過ぎで数秒余計に
/// かかることがあり、平均はその1回に引きずられる。回数そのものを見たい再入場だけは平均。
///
/// 代表値を1つに丸めていないのは意図的。「粗く合わせる速さ」と「止める速さ」は別のつまみで
/// 決まるので、混ぜた瞬間にどちらを触ればいいのか分からなくなる。
/// </summary>
public sealed class AimTestScore
{
    /// <summary>集計に使った試行 (ウォームアップを除く)。</summary>
    public IReadOnlyList<AimTrialResult> Scored { get; }

    public int Trials => Scored.Count;
    public int Timeouts { get; }
    public double SuccessRate => Trials == 0 ? 0 : 1.0 - (double)Timeouts / Trials;

    /// <summary>
    /// 的に一度も入れなかった試行の数。
    ///
    /// 時間切れをひとまとめにしないのが肝。「入れたが1秒保てなかった」は止め際の問題で、
    /// 「一度も入れなかった」は届いていない問題。直すつまみがまるで違うので、同じ失敗として
    /// 数えると診断がまるごと嘘になる。
    /// </summary>
    public int NeverTouched { get; }

    /// <summary>
    /// 一度も入れなかった試行での、的の中心への最接近距離の中央値 [px]。
    /// 的の半径と比べて、「遠くで止まっている」のか「縁まで来て入れない」のかを分ける。
    /// </summary>
    public double MedianClosestWhenMissedPx { get; }

    /// <summary>一度も入れなかった試行での、的の半径の中央値 [px]。上の値と比べるため。</summary>
    public double MedianRadiusWhenMissedPx { get; }

    /// <summary>「大まかに素早く合わせる」ほうの代表値 [ms]。</summary>
    public double MedianFirstTouchMs { get; }

    /// <summary>整定時間の中央値 [ms]。表に出すためのもので、代表値としては使わない (下を見よ)。</summary>
    public double MedianSettleMs { get; }

    /// <summary>
    /// 「そこから細かく詰める」ほうの代表値 [ms]。**中央値ではなく平均**。
    ///
    /// 実測200試行で、修正が要った試行は 37.5% しかなかった。残りは的に入ってそのまま1秒
    /// 保てているので整定が 0 で、中央値を取ると 10本中9本で 0 になる --- 代表値として退化していて、
    /// 設定を変えても動かない。
    ///
    /// ここで知りたいのは「1試行あたり、修正にどれだけ余計にかかっているか」で、それは
    /// 定義そのものが平均。分布の裾に引きずられるという中央値の理由 (初到達のほう) は、
    /// ゼロ過剰な分布には当てはまらない。
    /// </summary>
    public double MeanSettleMs { get; }

    /// <summary>整定時間の75パーセンタイル [ms]。修正が要ったときに、どれくらいかかるか。</summary>
    public double SettleP75Ms { get; }

    /// <summary>整定が0でなかった試行の割合。＝「一発で止まれなかった」割合。</summary>
    public double CorrectionRate { get; }

    /// <summary>維持完了までの代表値 [ms]。維持そのものの1秒を含む。</summary>
    public double MedianCompletionMs { get; }

    public double MeanReEntries { get; }

    /// <summary>はみ出しの中央値 [px]。実測では 0 になる (半分以上の試行は的から出ない)。</summary>
    public double MedianOvershootPx { get; }

    /// <summary>
    /// はみ出しの90パーセンタイル [px]。中央値が 0 に退化するので、指摘の根拠にはこちらを出す。
    /// 実測のレンジは 23〜83px。
    /// </summary>
    public double OvershootP90Px { get; }
    public double MedianPathEfficiency { get; }
    public double MedianDriftPx { get; }

    /// <summary>
    /// 粗合わせのスループット [bit/秒]。ID の平均 ÷ 初到達時間の平均。
    ///
    /// 初到達だけで出すのは、維持の1秒が定数として全試行に同じだけ乗っていて、含めると
    /// 難易度に依らない下駄になるため。Fitts の枠組みは「距離と的の大きさを1つの数にまとめて
    /// 比較できるようにする」ものなので、下駄を履かせると比較の意味が薄まる。
    /// </summary>
    public double ThroughputBitsPerSec { get; }

    /// <summary>
    /// 試行中に出た正規化半径の、95パーセンタイル。曲線のどこまで使えているか。
    ///
    /// 1.0 近辺まで出ていないなら最大速度には一度も届いていない --- つまり最大速度のつまみは
    /// 効いておらず、可動域か指数のほうを疑うべき、という切り分けがここでつく。
    /// </summary>
    public double PeakRadiusP95 { get; }

    /// <summary>荷重の倍率が落ちたところの中央値。荷重モードを使っていなければ1。</summary>
    public double MedianMinPressureFactor { get; }

    public IReadOnlyList<AimSizeScore> BySize { get; }
    public IReadOnlyList<AimDirectionScore> ByDirection { get; }

    public AimTestScore(IEnumerable<AimTrialResult> results)
    {
        Scored = results.Where(r => !r.IsWarmup).ToArray();
        Timeouts = Scored.Count(r => r.TimedOut);
        var missed = Scored.Where(r => r.NeverTouched).ToArray();
        NeverTouched = missed.Length;
        MedianClosestWhenMissedPx = Median(missed.Select(r => r.ClosestApproachPx));
        MedianRadiusWhenMissedPx = Median(missed.Select(r => r.TargetDiameterPx / 2));

        var done = Scored.Where(r => !r.TimedOut).ToArray();
        MedianFirstTouchMs = Median(done.Select(r => r.FirstTouchMs));
        MedianSettleMs = Median(done.Select(r => r.SettleMs));
        MedianCompletionMs = Median(done.Select(r => r.CompletionMs));
        MeanReEntries = Scored.Count == 0 ? 0 : Scored.Average(r => (double)r.ReEntries);
        MedianOvershootPx = Median(Scored.Select(r => r.MaxOvershootPx));
        OvershootP90Px = Percentile(Scored.Select(r => r.MaxOvershootPx), 0.90);

        var settles = done.Select(r => r.SettleMs).Where(v => v >= 0).ToArray();
        MeanSettleMs = settles.Length == 0 ? -1 : settles.Average();
        SettleP75Ms = Percentile(settles, 0.75);
        CorrectionRate = settles.Length == 0 ? 0 : (double)settles.Count(v => v > 0) / settles.Length;
        MedianPathEfficiency = Median(done.Select(r => r.PathEfficiency));
        MedianDriftPx = Median(done.Select(r => r.DwellDriftPx));
        PeakRadiusP95 = Percentile(Scored.Select(r => r.PeakRadius), 0.95);
        MedianMinPressureFactor = Median(Scored.Select(r => r.MinPressureFactor));

        double meanId = done.Length == 0 ? 0 : done.Average(r => r.IndexOfDifficulty);
        double meanFirstTouchSec = done.Length == 0 ? 0 : done.Average(r => r.FirstTouchMs) / 1000.0;
        ThroughputBitsPerSec = meanFirstTouchSec > 0.001 ? meanId / meanFirstTouchSec : 0;

        BySize = Scored
            .GroupBy(r => r.TargetDiameterPx)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var ok = g.Where(r => !r.TimedOut).ToArray();
                return new AimSizeScore(
                    DiameterPx: g.Key,
                    Trials: g.Count(),
                    SuccessRate: (double)ok.Length / g.Count(),
                    NeverTouched: g.Count(r => r.NeverTouched),
                    MedianFirstTouchMs: Median(ok.Select(r => r.FirstTouchMs)),
                    MeanSettleMs: Mean(ok.Select(r => r.SettleMs)),
                    MeanReEntries: g.Average(r => (double)r.ReEntries),
                    MedianDriftPx: Median(ok.Select(r => r.DwellDriftPx)));
            })
            .ToArray();

        ByDirection = Scored
            .GroupBy(r => r.DirectionLabel)
            .Select(g =>
            {
                var ok = g.Where(r => !r.TimedOut).ToArray();
                return new AimDirectionScore(
                    Label: g.Key,
                    Trials: g.Count(),
                    SuccessRate: (double)ok.Length / g.Count(),
                    NeverTouched: g.Count(r => r.NeverTouched),
                    MedianFirstTouchMs: Median(ok.Select(r => r.FirstTouchMs)),
                    MeanSettleMs: Mean(ok.Select(r => r.SettleMs)),
                    MedianPeakRadius: Median(g.Select(r => r.PeakRadius)));
            })
            .OrderByDescending(d => d.MedianFirstTouchMs)
            .ToArray();
    }

    /// <summary>負の値 (未到達を表す -1) は代表値の計算から外す。</summary>
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v >= 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return -1;
        }
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// 代表値が無いこと (-1) を「-1ms」や「-100%」として出さないための整形。
    /// 全試行が失敗すると中央値は存在しないので、この場合は必ず起きる。
    /// </summary>
    public static string Format(double value, string format, string unit = "")
        => value < 0 ? "—" : value.ToString(format) + unit;

    /// <summary>未到達 (-1) を除いた平均。整定のように 0 が多い量は、中央値より平均が代表になる。</summary>
    internal static double Mean(IEnumerable<double> values)
    {
        var kept = values.Where(v => v >= 0).ToArray();
        return kept.Length == 0 ? -1 : kept.Average();
    }

    internal static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Where(v => v >= 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return -1;
        }
        int index = Math.Clamp((int)Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }
}
