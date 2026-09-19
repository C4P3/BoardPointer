using BoardPointer.Core.Sampling;

namespace BoardPointer.Core.Pipeline;

/// <summary>
/// 生サンプル1つをパイプラインに通した結果。途中の段の値も全部持たせてあるのは、Replay が
/// 「各段が何をしたか」をそのままCSVに吐けるようにするため。どの段で値が壊れたのかを目で追える。
/// </summary>
/// <param name="TimestampMs">元の生サンプルの経過ミリ秒。</param>
/// <param name="Raw">元の生サンプル。</param>
/// <param name="TopRightKg">工場較正 + ゼロ点補正を通した前右の荷重。</param>
/// <param name="TotalKg">4隅の合計荷重。</param>
/// <param name="CopXMm">重心の左右位置 [mm]。ボード中心が0、右が正。</param>
/// <param name="CopYMm">重心の前後位置 [mm]。ボード中心が0、前 (つま先側) が正。</param>
/// <param name="CopXFilteredMm">1ユーロフィルタ通過後の左右位置。実際に操作に使うのはこちら。</param>
/// <param name="CopYFilteredMm">同、前後位置。</param>
/// <param name="Present">人が乗っているか (ヒステリシス付き)。false のときは重心に意味はない。</param>
/// <param name="CopResolutionMm">
/// 生値が1カウント変わったときに重心が動く距離 [mm]。重心は合計で割る比なので、この値は
/// 合計荷重に反比例する --- 同じセンサーでも、合計が小さいほど重心は粗くなる。
/// </param>
/// <param name="CopValid">
/// 合計荷重が <see cref="PipelineOptions.MinLoadForCopKg"/> を超えていて、重心が信用できるか。
/// false のときの重心は直前の値の保持で、今のものではない。
/// </param>
/// <param name="LoadIsCalibrated">
/// 荷重の値が kg として意味を持つか。工場較正があれば常に true。無い場合は公称ゲインで換算して
/// いるので、原点を与える荷重ゼロ点を取ってあることが条件になる。false のときは在席判定も
/// 重心も止めてある。
/// </param>
/// <param name="UsesFactoryCalibration">
/// ボードから読んだ工場較正を使っているか。false なら公称ゲイン (約103.5カウント/kg) による推定で、
/// 絶対値は数パーセントずれる。重心と操作にはほぼ影響しない。
/// </param>
public readonly record struct BoardFrame(
    long TimestampMs,
    RawSample Raw,
    double TopRightKg,
    double BottomRightKg,
    double TopLeftKg,
    double BottomLeftKg,
    double TotalKg,
    double CopXMm,
    double CopYMm,
    double CopXFilteredMm,
    double CopYFilteredMm,
    double CopResolutionMm,
    bool Present,
    bool CopValid,
    bool LoadIsCalibrated,
    bool UsesFactoryCalibration)
{
    /// <summary>Replay が吐く派生CSVのヘッダ行。</summary>
    public const string CsvHeader =
        "t_ms,raw_tr,raw_br,raw_tl,raw_bl,kg_tr,kg_br,kg_tl,kg_bl,kg_total,cop_x,cop_y,cop_x_f,cop_y_f,cop_res,present,cop_valid";

    public string ToCsv() =>
        $"{TimestampMs},{Raw.TopRight},{Raw.BottomRight},{Raw.TopLeft},{Raw.BottomLeft}," +
        $"{TopRightKg:F3},{BottomRightKg:F3},{TopLeftKg:F3},{BottomLeftKg:F3},{TotalKg:F3}," +
        $"{CopXMm:F2},{CopYMm:F2},{CopXFilteredMm:F2},{CopYFilteredMm:F2},{CopResolutionMm:F3},{(Present ? 1 : 0)},{(CopValid ? 1 : 0)}";
}
