using System.Text;

namespace BoardPointer.Core.Training;

/// <summary>測定したときの設定。診断の文面を「次にどのつまみをどちらへ」まで書くために要る。</summary>
public readonly record struct AimTestConditions(
    string PostureMode,
    double Deadzone,
    double FullScale,
    double Exponent,
    double MaxSpeedPxPerSec,
    double MinCutoffHz,
    double Beta,
    bool ReachIsCalibrated,
    double ReachFrontMm,
    double ReachBackMm,
    double ReachLeftMm,
    double ReachRightMm,
    string PressureMode,
    double PressureEngageRatio,
    double PressureFullRatio,
    double LoadFactorAtEngage,
    double LoadFactorAtFull,
    double CopResolutionMm,
    double TotalKg)
{
    public bool UsesPressure => PressureMode != "Off";

    /// <summary>方向の名前から、その向きの可動域 [mm] を引く。診断で名指しするため。</summary>
    public double ReachFor(string directionLabel) => directionLabel switch
    {
        "前" => ReachFrontMm,
        "後" => ReachBackMm,
        "左" => ReachLeftMm,
        "右" => ReachRightMm,
        // 斜めは2軸の合成なので1つの数字では言えない。近いほうの軸を返す。
        "右前" or "左前" => ReachFrontMm,
        _ => ReachBackMm,
    };
}

/// <summary>指摘の強さ。</summary>
public enum AimFindingLevel
{
    /// <summary>問題なし。触らなくてよい、と言えることもまた結果。</summary>
    Good,

    /// <summary>気にとめておく程度。</summary>
    Note,

    /// <summary>効いている。直すと体感が変わる。</summary>
    Problem,
}

/// <param name="Title">何が起きているか。数字込みで1行。</param>
/// <param name="Detail">なぜそう言えるか、どのつまみをどちらへ動かすか。</param>
public readonly record struct AimFinding(AimFindingLevel Level, string Title, string Detail);

/// <summary>
/// 集計からつまみへの対応づけ。
///
/// ここのルールは経験則ではなく、README に書いたパイプラインの物理から出ている。速度は
/// `s = 最大速度 · t^指数`、`t = (ρ − デッドゾーン) / (フルスケール − デッドゾーン)`、`ρ` は
/// 可動域で割った半径。だから「速度が足りない」の原因は、最大速度・指数・可動域・デッドゾーンの
/// どれかに必ず帰着する。どれなのかを分けるのに要るのが、初到達と整定を分けた計測と、
/// 試行中に出た `ρ` の最大値。
///
/// **自動で設定を書き換えることはしない。** レート制御は閉ループで、しかも人は数十秒で上達する。
/// 1回の測定でつまみを動かすと、次の測定は「設定が良くなった」のか「慣れた」のか分からない。
/// 数字と根拠を出して、動かすのは本人に任せる。
/// </summary>
public static class AimTestDiagnosis
{
    /// <summary>再入場がこれを超えたら行き過ぎている。0が理想なので、1回/試行でも多い。</summary>
    private const double ReEntryProblem = 1.0;
    private const double ReEntryNote = 0.5;

    /// <summary>曲線の端まで使えているとみなす割合。これ未満なら最大速度には届いていない。</summary>
    private const double PeakRadiusReachedFraction = 0.9;

    /// <summary>経路効率がこれを下回ると、まっすぐ向かえていない。</summary>
    private const double PathEfficiencyProblem = 0.6;

    /// <summary>震えが的の半径のこの割合を超えると、その大きさは限界に近い。</summary>
    private const double DriftFractionOfRadius = 0.5;

    /// <summary>最悪方向が全体の中央値のこの倍数を超えたら、方向の偏りとみなす。</summary>
    private const double DirectionBiasFactor = 1.4;

    /// <summary>大きい的にこれ以上かかるなら、素の速さが足りない [ms]。</summary>
    private const double SlowFirstTouchMs = 1500;

    public static IReadOnlyList<AimFinding> Diagnose(AimTestScore score, in AimTestConditions c)
    {
        var findings = new List<AimFinding>();
        if (score.Trials == 0)
        {
            return findings;
        }

        AddFailureFindings(findings, score, c);
        AddReachFindings(findings, score, c);
        AddOvershootFindings(findings, score, c);
        AddSettlingFindings(findings, score, c);
        AddResolutionFindings(findings, score, c);
        AddDirectionFindings(findings, score, c);
        AddPressureFindings(findings, score, c);

        if (findings.Count == 0 || findings.All(f => f.Level == AimFindingLevel.Good))
        {
            findings.Insert(0, new AimFinding(
                AimFindingLevel.Good,
                "目立った問題は出ませんでした。",
                $"粗合わせ {AimTestScore.Format(score.MedianFirstTouchMs, "F0", "ms")}・"
                + $"詰め {AimTestScore.Format(score.MedianSettleMs, "F0", "ms")}・"
                + $"入り直し {score.MeanReEntries:F2}回/試行。次に触るなら、体感で気になるほうの"
                + "つまみを少しだけ動かして、同じテストをもう一度。"));
        }

        // 強い指摘から順に。触る順番がそのまま効き目の順になる。
        return findings.OrderByDescending(f => f.Level).ToArray();
    }

    /// <summary>
    /// 曲線の端まで使えているか。ここが最初に来るのは、届いていない場合に他の指摘の前提が
    /// 崩れるため --- 最大速度に一度も届いていないなら、「速度が足りない」の原因は最大速度の
    /// つまみではない。
    /// </summary>
    private static void AddReachFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        double peak = score.PeakRadiusP95;
        if (peak < 0)
        {
            return;
        }

        if (!c.ReachIsCalibrated)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                "可動域が未測定のままです。",
                "既定値 (前60 / 後45 / 左右70mm) は仮置きで、その人の体と姿勢には合っていません。"
                + "正規化がずれていると、以下の数字はすべて「ずれた正規化での成績」になります。"
                + "[可動域キャリブレーション (12秒)] を先に。"));
        }

        if (peak < c.FullScale * PeakRadiusReachedFraction)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                $"曲線の端まで届いていません (出た半径の95% = {peak:F2}、フルスケール {c.FullScale:F2})。",
                $"最大速度 {c.MaxSpeedPxPerSec:F0} px/s は一度も出ていないので、そこを上げても体感は"
                + "変わりません。効くのは可動域のほうです。[可動域キャリブレーション] を測り直すか"
                + $"（今より狭く出れば端に届くようになります）、指数 {c.Exponent:F2} を下げて"
                + "中間の速度を上げてください。"));
        }
        else if (peak >= 1.05)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Note,
                $"可動域を超えて出ています (出た半径の95% = {peak:F2})。",
                "測ったときより大きく動かせています。可動域が狭く登録されているぶん、中間の速度が"
                + "全体に速くなり、止めにくくなります。[可動域キャリブレーション] を測り直すのが"
                + "素直です。"));
        }

        // 大きい的でも遅いなら、止め際ではなく素の速さの問題。
        var largest = score.BySize.FirstOrDefault();
        if (largest.Trials > 0 && largest.MedianFirstTouchMs > SlowFirstTouchMs
            && peak >= c.FullScale * PeakRadiusReachedFraction)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                $"一番大きい的 ({largest.DiameterPx:F0}px) でも初到達に {largest.MedianFirstTouchMs:F0}ms かかっています。",
                $"曲線の端 ({peak:F2}) までは使えているので、足りないのは最大速度そのものです。"
                + $"{c.MaxSpeedPxPerSec:F0} px/s を上げてください。上げると行き過ぎが増えるので、"
                + $"そのときは指数 {c.Exponent:F2} も一緒に上げると中心付近だけ緩いままになります。"));
        }
    }

    private static void AddOvershootFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        if (score.MeanReEntries >= ReEntryProblem)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                $"行き過ぎています (入り直し {score.MeanReEntries:F2}回/試行、はみ出し {score.MedianOvershootPx:F0}px)。",
                $"的に入ってから止まりきれずに出ています。指数 {c.Exponent:F2} を上げると中心付近が"
                + "緩くなって止めやすくなります（端の速さは変わりません）。それでも残るなら最大速度"
                + $" {c.MaxSpeedPxPerSec:F0} px/s を下げてください。"
                + (c.UsesPressure
                    ? ""
                    : " 荷重モード（浮かせると100%→25%）を入れると、止める瞬間だけ遅くできます。")));
        }
        else if (score.MeanReEntries >= ReEntryNote)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Note,
                $"少し行き過ぎています (入り直し {score.MeanReEntries:F2}回/試行)。",
                $"気になるなら指数を {c.Exponent:F2} から 0.2〜0.4 上げてみてください。"));
        }

        if (score.MedianPathEfficiency >= 0 && score.MedianPathEfficiency < PathEfficiencyProblem)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                $"まっすぐ向かえていません (経路効率 {score.MedianPathEfficiency:P0})。",
                "的までの直線距離に対して、実際には倍近い距離を通っています。原点がずれていると"
                + "常に一方向へ流れて遠回りになるので、まず F12（重心の原点を今に合わせる）を押して"
                + "から測り直してください。それでも変わらないなら、左右と前後で速度の出方が違って"
                + "いる＝可動域の測り直しです。"));
        }
    }

    private static void AddSettlingFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        if (score.MedianSettleMs < 0 || score.MedianFirstTouchMs < 0)
        {
            return;
        }

        // 詰めのほうが粗合わせより長くかかっているなら、支配しているのは止め際。
        if (score.MedianSettleMs > score.MedianFirstTouchMs && score.MedianSettleMs > 300)
        {
            string cause = score.MeanReEntries >= ReEntryNote
                ? $"入り直しが {score.MeanReEntries:F2}回/試行あるので、原因は行き過ぎです。指数を上げてください。"
                : $"入り直しは {score.MeanReEntries:F2}回/試行と少ないので、行き過ぎではなく"
                  + $"「的の中で止まりきれない」側です。デッドゾーン {c.Deadzone:F2} を上げるか、"
                  + $"min-cutoff {c.MinCutoffHz:F2} Hz を下げて静止時を静かにしてください。";

            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                $"詰めに時間がかかっています (粗合わせ {score.MedianFirstTouchMs:F0}ms に対して詰め {score.MedianSettleMs:F0}ms)。",
                cause));
        }

    }

    /// <summary>
    /// 失敗した試行の扱い。
    ///
    /// ここが最初に来るのは、全滅したときに他のどの節も動かないため --- 中央値が存在しないので、
    /// 「遅い」「行き過ぎ」のような代表値に基づく指摘はすべて素通りする。何も言わずに
    /// 「目立った問題は出ませんでした」と出すのが一番まずい。
    ///
    /// 失敗を2種類に分けるのが肝。**一度も的に入れなかった**のと、**入れたが1秒保てなかった**のは
    /// 別の故障で、直すつまみも違う。最接近距離を測ってあるのはこの切り分けのため。
    /// </summary>
    private static void AddFailureFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        if (score.Timeouts == 0)
        {
            return;
        }

        int missed = score.NeverTouched;
        int touchedButLost = score.Timeouts - missed;
        var level = score.SuccessRate < 0.8 ? AimFindingLevel.Problem : AimFindingLevel.Note;

        if (missed > 0)
        {
            double closest = score.MedianClosestWhenMissedPx;
            double radius = score.MedianRadiusWhenMissedPx;
            double ratio = radius > 0.5 && closest >= 0 ? closest / radius : 0;
            bool stoppedShort = ratio >= 2.0;
            bool reachedTheEnd = score.PeakRadiusP95 >= c.FullScale * PeakRadiusReachedFraction;

            string detail = stoppedShort
                ? $"外した試行では、的の中心から {closest:F0}px (半径の {ratio:F1}倍) までしか"
                  + "近づけていません。的の手前で止まっています。"
                  + (reachedTheEnd
                      ? $" 曲線の端 ({score.PeakRadiusP95:F2}) までは使えているので、足りないのは"
                        + $"最大速度です ({c.MaxSpeedPxPerSec:F0} px/s)。あるいはデッドゾーン"
                        + $" {c.Deadzone:F2} が広すぎて、的に近づいたところで速度が 0 に落ちています"
                        + " --- この2つは、大きい的に届くかどうかで見分けられます。"
                      : $" 半径は {score.PeakRadiusP95:F2} までしか出ていないので、そもそも倒しきれて"
                        + "いません。[可動域キャリブレーション] を測り直してください。")
                : $"外した試行でも、的の中心から {closest:F0}px (半径 {radius:F0}px) までは来ています。"
                  + "縁まで届いているのに入れない状態なので、足りないのは速さではなく止め際です。"
                  + $"デッドゾーン {c.Deadzone:F2} を上げる、min-cutoff {c.MinCutoffHz:F2} Hz を下げる、"
                  + $"最大速度 {c.MaxSpeedPxPerSec:F0} px/s を下げる、のいずれかが効きます。";

            findings.Add(new AimFinding(
                level,
                $"的に一度も入れなかった試行が {missed}/{score.Trials} あります。",
                detail));
        }

        if (touchedButLost > 0)
        {
            findings.Add(new AimFinding(
                level,
                $"的に入ったのに1秒保てなかった試行が {touchedButLost}/{score.Trials} あります。",
                "止まりきれずに出ています。"
                + $"指数 {c.Exponent:F2} を上げると中心付近が緩くなって止めやすくなります。"
                + $"デッドゾーン {c.Deadzone:F2} を上げるのも直接効きます。"
                + (c.UsesPressure
                    ? ""
                    : " 荷重モード（浮かせると 100% → 25%）を入れると、止める瞬間だけ遅くできます。")));
        }
    }

    /// <summary>
    /// 震えが的の大きさに対して大きいとき、疑うのはフィルタより先に合計荷重。
    ///
    /// 重心は合計荷重で割る比なので、分解能は合計に反比例する (実測のボードで1カウント≒9.7g。
    /// 立位60kgなら0.03mm/count、座って15kgなら0.14mm/count、2kgまで落ちると1.05mm/count)。
    /// ここが粗いとフィルタをいくら強くしても階段が消えないので、先に姿勢と荷重を疑うほうが早い。
    /// </summary>
    private static void AddResolutionFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        var smallest = score.BySize.LastOrDefault();
        if (smallest.Trials == 0 || smallest.MedianDriftPx < 0)
        {
            return;
        }

        double radius = smallest.DiameterPx / 2;
        if (smallest.MedianDriftPx <= radius * DriftFractionOfRadius)
        {
            return;
        }

        bool coarse = c.CopResolutionMm > 0.5;
        findings.Add(new AimFinding(
            AimFindingLevel.Problem,
            $"一番小さい的 ({smallest.DiameterPx:F0}px) で震えが半径の {smallest.MedianDriftPx / radius:P0} あります。",
            coarse
                ? $"重心の刻みが {c.CopResolutionMm:F2} mm/count、合計荷重は {c.TotalKg:F1} kg でした。"
                  + "重心は合計で割る比なので、分解能は合計荷重に反比例します。ここが粗いうちは"
                  + "フィルタを強くしても階段は消えません。足をもっと載せる（座位なら足裏全体を"
                  + "預ける）か、立って使うほうが効きます。"
                : $"重心の刻み ({c.CopResolutionMm:F2} mm/count) は足りているので、これは姿勢の揺れです。"
                  + $"min-cutoff {c.MinCutoffHz:F2} Hz を下げると静止時が静かになります"
                  + $"（動き出しは遅れるので、beta {c.Beta:F5} を少し上げて補ってください）。"
                  + $"デッドゾーン {c.Deadzone:F2} を上げるのも効きます。"));
    }

    private static void AddDirectionFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        // 1方向あたり3試行しかないので、1試行だけの方向は代表値として扱わない。
        var worst = score.ByDirection.FirstOrDefault(d => d.MedianFirstTouchMs >= 0 && d.Trials >= 2);
        if (worst.Trials == 0 || score.MedianFirstTouchMs <= 0)
        {
            return;
        }

        if (worst.MedianFirstTouchMs < score.MedianFirstTouchMs * DirectionBiasFactor)
        {
            return;
        }

        bool saturated = worst.MedianPeakRadius >= 0.98;
        findings.Add(new AimFinding(
            AimFindingLevel.Note,
            $"「{worst.Label}」だけ遅いです ({worst.MedianFirstTouchMs:F0}ms、全体 {score.MedianFirstTouchMs:F0}ms)。",
            saturated
                ? $"その向きでは半径が {worst.MedianPeakRadius:F2} まで張り付いています。可動域が"
                  + $"実際より狭く登録されていて（今 {c.ReachFor(worst.Label):F0}mm）、それ以上速く"
                  + "できない状態です。[可動域キャリブレーション] を測り直してください。"
                : $"半径は {worst.MedianPeakRadius:F2} までしか出ていないので、その向きに体を"
                  + $"動かしきれていません。可動域が広く登録されている（今 {c.ReachFor(worst.Label):F0}mm）"
                  + "可能性があります。測り直すか、その向きだけ届く姿勢に足を置き直してください。"));
    }

    private static void AddPressureFindings(List<AimFinding> findings, AimTestScore score, in AimTestConditions c)
    {
        if (!c.UsesPressure)
        {
            // 詰めが支配していて荷重を使っていないなら、提案する価値がある。
            if (score.MedianSettleMs > 500)
            {
                findings.Add(new AimFinding(
                    AimFindingLevel.Note,
                    "荷重モードを使っていません。",
                    "詰めに時間がかかっているので、「浮かせると 100% → 25%」の設定が噛み合う可能性が"
                    + "あります。止める瞬間だけ速度を落とせるので、粗合わせの速さを犠牲にせずに"
                    + "済みます。先に [荷重の範囲を測る (8秒)] で閾値を実測してください。"));
            }
            return;
        }

        double factor = score.MedianMinPressureFactor;
        if (factor < 0)
        {
            return;
        }

        double idle = Math.Clamp(c.LoadFactorAtEngage, 0, 1);
        double full = Math.Clamp(c.LoadFactorAtFull, 0, 1);

        // 倍率が作動側から動いていない = テスト中ずっと荷重の軸を使えていない。
        if (Math.Abs(factor - idle) < 0.05)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Problem,
                $"荷重の倍率が {factor:P0} から動いていません。",
                $"設定は「{(c.PressureEngageRatio > c.PressureFullRatio ? "浮かせる" : "踏み込む")}と "
                + $"{idle:P0} → {full:P0}」ですが、テスト中に一度もそちら側へ届いていません。"
                + $"閾値（作動 {c.PressureEngageRatio:F2} / 振り切り {c.PressureFullRatio:F2}）が"
                + "自分の出せる範囲の外にあります。[荷重の範囲を測る (8秒)] で置き直してください。"));
        }
        else if (Math.Abs(factor - full) < 0.05 && score.MedianSettleMs > 500)
        {
            findings.Add(new AimFinding(
                AimFindingLevel.Note,
                $"荷重の倍率は振り切り側 ({factor:P0}) まで届いています。",
                "精密側は使えているのに詰めが長いので、絞り方が足りません。"
                + $"振り切り側の速度 {full:P0} をもっと下げてみてください。"));
        }
    }

    /// <summary>結果画面とCSVの両方に出す要約。数字の並びは同じにしておく。</summary>
    public static string Summarize(AimTestScore score)
    {
        string F(double v, string fmt, string unit = "") => AimTestScore.Format(v, fmt, unit);

        var sb = new StringBuilder();
        sb.AppendLine($"試行 {score.Trials} (成功 {score.SuccessRate:P0})");
        if (score.Timeouts > 0)
        {
            sb.AppendLine($"  失敗 {score.Timeouts} (うち的に入れず {score.NeverTouched})");
        }
        sb.AppendLine($"粗合わせ (初到達)   中央値 {F(score.MedianFirstTouchMs, "F0", " ms")}");
        sb.AppendLine($"詰め (整定)         中央値 {F(score.MedianSettleMs, "F0", " ms")}");
        sb.AppendLine($"維持完了まで        中央値 {F(score.MedianCompletionMs, "F0", " ms")}");
        sb.AppendLine($"入り直し            {score.MeanReEntries:F2} 回/試行");
        sb.AppendLine($"はみ出し            中央値 {F(score.MedianOvershootPx, "F0", " px")}");
        sb.AppendLine($"経路効率            {F(score.MedianPathEfficiency * 100, "F0", " %")}");
        sb.AppendLine($"維持中の震え        {F(score.MedianDriftPx, "F1", " px (RMS)")}");
        sb.AppendLine($"使えた半径 (95%)    {F(score.PeakRadiusP95, "F2")}");
        sb.AppendLine($"スループット        {score.ThroughputBitsPerSec:F2} bit/秒");
        return sb.ToString();
    }
}
