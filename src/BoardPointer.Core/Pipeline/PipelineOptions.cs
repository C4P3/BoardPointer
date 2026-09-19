namespace BoardPointer.Core.Pipeline;

/// <summary>
/// パイプライン全段のつまみ。Viewer のスライダーから実行中に書き換えられるし、Replay には
/// コマンドライン引数から渡る。同じ options + 同じ生CSV なら必ず同じ結果が出る (パイプラインは
/// サンプル列だけに依存して、実時間には一切依存しない) ので、ライブで「なんか変」と思った瞬間の
/// 記録を、あとから机上で何度でも再現できる。
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>
    /// センサー間の距離。重心をミリメートルで出すために要る。Wii バランスボードの一般に使われている
    /// 概算値で、実測した個体差で置き換えてよい。左右 (X) のほうが広い。
    /// </summary>
    public double BoardWidthMm { get; set; } = 433.0;

    /// <summary>前後 (Y) のセンサー間距離。</summary>
    public double BoardLengthMm { get; set; } = 238.0;

    /// <summary>ゼロ点補正を適用するか。オフにすると工場較正そのままの値が出る。</summary>
    public bool TareEnabled { get; set; } = true;

    /// <summary>1ユーロフィルタを通すか。オフにすると生の重心がそのまま出る (揺れの観察用)。</summary>
    public bool FilterEnabled { get; set; } = true;

    /// <summary>
    /// 1ユーロフィルタの最低カットオフ周波数 [Hz]。ゆっくり動いているときの平滑化の強さを決める。
    /// 小さくするほど静止時のジッタが消えるが、動き出しの遅れが増える。
    /// </summary>
    public double MinCutoffHz { get; set; } = 1.0;

    /// <summary>
    /// 速度に対する応答の強さ。大きいほど「速く動いているときは遅れを減らす」側に振れる。
    /// 単位が mm/s なので、よくある 0.007 (正規化座標前提) より桁が小さい値が効く。
    /// </summary>
    public double Beta { get; set; } = 0.00005;

    /// <summary>速度推定そのものにかけるローパスのカットオフ [Hz]。ふつう 1.0 のままでよい。</summary>
    public double DerivativeCutoffHz { get; set; } = 1.0;

    /// <summary>これを超える荷重が続いたら「乗っている」と判定する [kg]。</summary>
    public double PresenceOnKg { get; set; } = 15.0;

    /// <summary>
    /// これを下回る荷重が続いたら「降りた」と判定する [kg]。オン閾値と別の値にしてあるのが肝で、
    /// 1つの閾値で上下を判定すると、境界上で値が震えたときに両方のタイマーが毎サンプル
    /// リセットされて、どちらも発火しなくなる。
    /// </summary>
    public double PresenceOffKg { get; set; } = 10.0;

    /// <summary>オン判定に必要な継続時間 [ms]。短い接触で誤って操作が始まらないように。</summary>
    public int PresenceOnMs { get; set; } = 300;

    /// <summary>オフ判定に必要な継続時間 [ms]。動作中の一瞬の荷重抜けで操作が切れないように。</summary>
    public int PresenceOffMs { get; set; } = 500;

    /// <summary>
    /// 重心を計算するのに最低限必要な合計荷重 [kg]。重心は合計で割る比なので、合計が小さいほど
    /// ノイズが拡大される --- 各隅の読み取り誤差を e とすると重心の誤差はおおよそ (半幅 × e / 合計)
    /// で、60kg で立っているときに 0.2mm 相当だったものが 3kg では 4mm 相当まで膨らむ。合計が 0 に
    /// 近づけば値は端まで振り切れる。
    ///
    /// これを下回っている間は重心を更新せず、直前の値を保持して <see cref="BoardFrame.CopValid"/> を
    /// false にする。カーソルが暴れるより、止まっているほうがまだ扱いやすい。
    /// </summary>
    public double MinLoadForCopKg { get; set; } = 2.0;

    /// <summary>
    /// 工場較正が読めなかったときに使う公称感度 [カウント/kg]。
    ///
    /// ゲインはボードの機構とひずみゲージで決まるので、個体差が小さい。実測した個体の工場較正から
    /// 逆算すると 101.7 / 107.4 / 102.9 / 102.0 カウント/kg で、4隅の平均が 103.5。ここを既定値にする。
    ///
    /// 重要なのは、この換算を**ゼロ点を引いたあとに**掛けること。工場較正の値のうち原点は個体ごとに
    /// まるで違う (実測で 18238 / 18553 / 18850 / 6940) ので推定できないが、原点は荷重ゼロ点が
    /// 消してくれる。残る傾きだけなら公称値で十分実用になる。
    ///
    /// これがあるので、工場較正が読めなくても kg で決めた閾値 (在席判定・重心の最低荷重・踏み込み)
    /// がそのまま効く。ただし荷重ゼロ点を取っていることが前提になる。
    /// </summary>
    public double NominalCountsPerKg { get; set; } = 103.5;

    /// <summary>
    /// 立って使う既定値。合計荷重が体重ぶんあるので、閾値は高めでよい。
    /// </summary>
    public static PipelineOptions Standing() => new();

    /// <summary>
    /// 椅子に座って足先で操作する場合の既定値。
    ///
    /// この姿勢では脚の重さ (実測で 12〜15kg 程度) がボードに載りっぱなしで、操作そのものは
    /// その上に乗る数 kg の重心移動として出る。だから在席の閾値は「体重が載ったか」ではなく
    /// 「足が置かれているか」を見る高さまで下げる。立位の既定値 (15kg) のままだと、脚の重さが
    /// ちょうど閾値付近をうろついて在席判定がちらつく。
    ///
    /// フィルタを少し強めにしてあるのは、合計荷重が立位の 1/4 程度しかなく、その比で効く重心の
    /// ノイズが同じだけ増えるため。
    /// </summary>
    public static PipelineOptions SeatedFoot() => new()
    {
        PresenceOnKg = 2.5,
        PresenceOffKg = 1.2,
        PresenceOnMs = 200,
        PresenceOffMs = 400,
        MinLoadForCopKg = 1.5,
        MinCutoffHz = 0.7,
    };

    public PipelineOptions Clone() => (PipelineOptions)MemberwiseClone();

    public void CopyFrom(PipelineOptions other)
    {
        BoardWidthMm = other.BoardWidthMm;
        BoardLengthMm = other.BoardLengthMm;
        TareEnabled = other.TareEnabled;
        FilterEnabled = other.FilterEnabled;
        MinCutoffHz = other.MinCutoffHz;
        Beta = other.Beta;
        DerivativeCutoffHz = other.DerivativeCutoffHz;
        PresenceOnKg = other.PresenceOnKg;
        PresenceOffKg = other.PresenceOffKg;
        PresenceOnMs = other.PresenceOnMs;
        PresenceOffMs = other.PresenceOffMs;
        MinLoadForCopKg = other.MinLoadForCopKg;
        NominalCountsPerKg = other.NominalCountsPerKg;
    }
}
