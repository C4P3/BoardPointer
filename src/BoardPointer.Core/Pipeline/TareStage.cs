namespace BoardPointer.Core.Pipeline;

/// <summary>
/// ゼロ点補正。工場較正の0kg点はボード単体のものなので、実際に床に置いた状態での残差
/// (床の傾き、脚の高さのばらつき、経年) が乗っている。誰も乗っていない状態を数秒サンプリングして、
/// 各隅の中央値を差し引く。
///
/// 中央値にしてあるのは、サンプリング中に誰かがボードに触ったり、通りすがりで一瞬荷重が乗ったりした
/// ときに平均だと引きずられるから。kg に直したあとの浮動小数を相手にするので、生値でよく使われる
/// 最頻値 (同じ整数が何度も出ることが前提) は使えない。
/// </summary>
public sealed class TareStage
{
    private readonly List<double>[] _samples = [[], [], [], []];
    private readonly List<double> _totals = [];
    private readonly double[] _offsets = new double[4];

    public bool IsSampling { get; private set; }
    public bool IsCalibrated { get; private set; }
    public int SampleCount => _samples[0].Count;

    /// <summary>各隅のゼロ点オフセット。0=TR, 1=BR, 2=TL, 3=BL。</summary>
    public IReadOnlyList<double> Offsets => _offsets;

    /// <summary>
    /// 測定中に見えていた合計荷重の中央値。ボードが本当に空だったかの検算に使う。
    /// これが数kgもあるなら、足を載せたまま測ってしまっている --- そのまま使うと足の重さごと
    /// 差し引かれ、合計荷重がほぼ0になって重心が端まで振り切れる。
    ///
    /// 単位はパイプラインが動いているモードのもので、kg とは限らない (工場較正が読めていなければ
    /// 生カウント)。判定に使う閾値は <see cref="BoardPipeline.EmptyBoardThreshold"/> のほう。
    /// </summary>
    public double SampledTotalMedian { get; private set; }

    public void BeginSampling()
    {
        foreach (var list in _samples)
        {
            list.Clear();
        }
        _totals.Clear();
        IsSampling = true;
        IsCalibrated = false;
    }

    public void Feed(ReadOnlySpan<double> corners)
    {
        if (!IsSampling)
        {
            return;
        }
        double total = 0;
        for (int i = 0; i < 4; i++)
        {
            _samples[i].Add(corners[i]);
            total += corners[i];
        }
        _totals.Add(total);
    }

    public void FinishSampling()
    {
        for (int i = 0; i < 4; i++)
        {
            _offsets[i] = Median(_samples[i]);
        }
        SampledTotalMedian = Median(_totals);
        IsSampling = false;
        IsCalibrated = true;
    }

    public void Reset()
    {
        Array.Clear(_offsets);
        IsSampling = false;
        IsCalibrated = false;
    }

    /// <summary>
    /// オフセットを引く。0 で下限を切っているのは、人が乗っていない間の負の揺らぎが合計荷重に
    /// 効いて在席判定を汚すのを避けるため。人が乗っている間は各隅とも0から十分離れるので、重心の
    /// 精度にこの切り捨ては効かない。
    /// </summary>
    public double Apply(int corner, double value) =>
        IsCalibrated ? Math.Max(0, value - _offsets[corner]) : value;

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
