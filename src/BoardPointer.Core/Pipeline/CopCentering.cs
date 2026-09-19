namespace BoardPointer.Core.Pipeline;

/// <summary>
/// 重心の原点合わせ。「楽にしているときの重心」を測って、そこを座標の (0,0) にする。
///
/// 荷重のゼロ点 (<see cref="TareStage"/>) とは別物で、混ぜると壊れる。役割はこう分かれている:
///
///   荷重のゼロ点 : ボードに何も載っていない状態で測る。床の傾きや脚の高さのばらつきを消す。
///   重心の原点   : 操作する姿勢のまま、力を抜いて測る。その人の中立姿勢を消す。
///
/// 荷重のゼロ点を「足を載せたまま」測ってしまうと、足の重さごと差し引かれて合計荷重がほぼ0に
/// なり、重心は合計で割る比なので値が端まで振り切れる。椅子に座って足先で操作する使い方だと、
/// 脚の重さ (10〜15kg) が常に載りっぱなしなので、この取り違えが致命的に効く。中立姿勢を消したい
/// ときに使うのはこちら。
///
/// 中央値を使うのは <see cref="TareStage"/> と同じ理由で、測定中の一瞬のぶれに引きずられないため。
/// </summary>
public sealed class CopCentering
{
    private readonly List<double> _x = [];
    private readonly List<double> _y = [];
    private readonly List<double> _loads = [];

    public bool IsSampling { get; private set; }
    public bool IsCalibrated { get; private set; }
    public int SampleCount => _x.Count;

    public double OriginXMm { get; private set; }
    public double OriginYMm { get; private set; }

    /// <summary>
    /// 原点を測ったときの合計荷重の中央値 [kg]。「操作する姿勢で力を抜いた状態」で測っているので、
    /// 踏み込みの強さを比で見るときの分母として意味が正しい。0 なら未測定。
    /// </summary>
    public double RestingLoadKg { get; private set; }

    public void BeginSampling()
    {
        _x.Clear();
        _y.Clear();
        _loads.Clear();
        IsSampling = true;
        IsCalibrated = false;
    }

    /// <summary>原点を引く前の、ボード中心を基準にした重心を食わせる。荷重が足りないサンプルは捨てる。</summary>
    public void Feed(double absoluteXMm, double absoluteYMm, double totalKg, bool copValid)
    {
        if (!IsSampling || !copValid)
        {
            return;
        }
        _x.Add(absoluteXMm);
        _y.Add(absoluteYMm);
        _loads.Add(totalKg);
    }

    public void FinishSampling()
    {
        if (_x.Count > 0)
        {
            OriginXMm = Median(_x);
            OriginYMm = Median(_y);
            RestingLoadKg = Median(_loads);
            IsCalibrated = true;
        }
        IsSampling = false;
    }

    public void SetOrigin(double xMm, double yMm)
    {
        OriginXMm = xMm;
        OriginYMm = yMm;
        IsCalibrated = true;
        IsSampling = false;
    }

    /// <summary>
    /// 原点をゆっくり今の重心へ寄せる。
    ///
    /// レート制御では原点のわずかなずれが「恒常的なカーソルの流れ」になるので、数分座っていて
    /// 足の中立位置が動くと効いてくる。ただし操作中に追従させると狙いが逃げるため、呼ぶのは
    /// 「操作していない」と判断できるとき (デッドゾーンの内側、踏み込みが閾値未満) に限ること。
    /// 時定数は秒単位で、意図的な遅い動きと競合しない程度に長く取る。
    /// </summary>
    public void Follow(double absoluteXMm, double absoluteYMm, double dtSeconds, double timeConstantSeconds)
    {
        if (IsSampling || dtSeconds <= 0 || timeConstantSeconds <= 0)
        {
            return;
        }

        double alpha = 1.0 - Math.Exp(-dtSeconds / timeConstantSeconds);
        OriginXMm += (absoluteXMm - OriginXMm) * alpha;
        OriginYMm += (absoluteYMm - OriginYMm) * alpha;
        IsCalibrated = true;
    }

    public void Reset()
    {
        OriginXMm = 0;
        OriginYMm = 0;
        RestingLoadKg = 0;
        IsSampling = false;
        IsCalibrated = false;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
