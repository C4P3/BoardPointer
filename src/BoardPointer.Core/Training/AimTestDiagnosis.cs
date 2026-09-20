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
    // 以下の閾値は、実機で測った10本 (座位・足先、200試行) の分布から置いてある。
    // 指摘は**うまくいっている設定では出ない**のが条件。常に1つ点いていると、それは背景に
    // なって読まれなくなり、本当に効いている指摘まで一緒に無視される。
    //
    // 括弧内は実測のレンジ (10本それぞれの値の最小〜最大)。

    /// <summary>再入場の平均がこれを超えたら行き過ぎ [回/試行]。(実測 0.35〜0.85)</summary>
    private const double ReEntryProblem = 1.5;
    private const double ReEntryNote = 0.8;

    /// <summary>
    /// 曲線の端まで使えているとみなす割合 (フルスケールに対して)。これ未満なら最大速度には
    /// 届いていない。
    ///
    /// 実測の半径p95 は、正常な8本で 0.80〜1.16 とけっこう散る。0.9 (＝閾値 0.855) にすると
    /// 0.80 の回に Problem が点いたが、その回は初到達1688ms・成功100%で、他と比べて悪くない。
    /// 模擬の「倒しきれていない」設定は 0.64 だったので、境界はその間に置く。
    /// </summary>
    private const double PeakRadiusReachedFraction = 0.75;

    /// <summary>
    /// 出た半径の95%がこれを超えたら、可動域が狭く登録されすぎている。
    ///
    /// 実測のレンジは 0.80〜2.31。上の2本 (1.71 / 2.31) は可動域を 43mm で登録していたときの
    /// もので、測り直したら 0.80〜1.16 に収まった。つまりこの指標は**実際に壊れている設定を
    /// 拾っている**。境界はその間に置く。1.05 だと正常な6本にも点いた。
    /// </summary>
    private const double PeakRadiusOverRange = 1.30;

    /// <summary>経路効率の中央値がこれを下回ると、まっすぐ向かえていない。(実測 0.77〜0.96)</summary>
    private const double PathEfficiencyProblem = 0.65;

    /// <summary>
    /// 維持中の震えが的の半径のこの割合を超えると、その大きさは限界に近い。
    ///
    /// 実測では、震え / 半径が的の大きさに依らずほぼ一定だった (120px で 0.61、72px で 0.55、
    /// 44px で 0.54)。的が小さいほど慎重に止めているということで、**0.55 前後が普通**。
    /// 元の 0.5 は全10本・全サイズで点灯していた。
    /// </summary>
    private const double DriftFractionOfRadius = 0.85;

    /// <summary>
    /// 最悪方向が全体の中央値のこの倍数を超えたら、方向の偏りを疑う。
    ///
    /// 1方向あたり2〜3試行しかないので、ここは偶然がよく効く。実測10本で「最悪方向 / 全体中央」は
    /// **1.14〜1.90倍まで振れ、しかも最悪方向は毎回別の向きだった** (前・右前・後・左・右後…)。
    /// 1.4倍では偶然を拾って、毎回ちがう方向を名指しすることになる。
    ///
    /// そこで閾値を偶然の上限より上に置いたうえで、<see cref="AddDirectionFindings"/> では
    /// 半径による裏取りも要求する。遅いだけなら偶然、遅くて**かつ**半径が張り付いている
    /// (届かない) か低いまま (倒せない) なら、可動域の非対称という別の証拠がある。
    /// </summary>
    private const double DirectionBiasFactor = 2.0;

    /// <summary>大きい的にこれ以上かかるなら、素の速さが足りない [ms]。(実測 1153〜1909)</summary>
    private const double SlowFirstTouchMs = 2500;

    /// <summary>整定の平均がこれを超えたら、詰めに時間がかかっている [ms]。(実測 277〜961)</summary>
    private const double SettleMeanNoteMs = 1100;

    /// <summary>
    /// 重心の刻みがこれを超えたら「分解能が足りない」側を疑う [mm/count]。
    ///
    /// 実測は 0.19〜0.36 で、一番粗い 0.356 は合計荷重が 5.87kg しか載っていなかったとき。
    /// 分解能は合計荷重に反比例するので、この値は姿勢がそのまま出る。元の 0.5 は実機では
    /// 一度も超えず、判定が常に「姿勢の揺れ」側へ倒れていた。
    /// </summary>
    private const double CoarseResolutionMm = 0.30;

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
                + $"詰め {AimTestScore.Format(score.MeanSettleMs, "F0", "ms")}・"
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
        else if (peak >= PeakRadiusOverRange)
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
                $"行き過ぎています (入り直し {score.MeanReEntries:F2}回/試行、はみ出しは悪いほうの1割で {score.OvershootP90Px:F0}px)。",
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
        if (score.MeanSettleMs < 0 || score.MedianFirstTouchMs < 0)
        {
            return;
        }

        // 判定に中央値を使わない。実測では一発で止まれた試行が 62.5% あり、整定の中央値は
        // 10本中9本で 0 になった。設定を変えても動かない指標では、何も判定できない。
        bool dominant = score.MeanSettleMs > score.MedianFirstTouchMs;
        if (!dominant && score.MeanSettleMs < SettleMeanNoteMs)
        {
            return;
        }

        string cause = score.MeanReEntries >= ReEntryNote
            ? $"入り直しが {score.MeanReEntries:F2}回/試行あるので、原因は行き過ぎです。"
              + $"指数 {c.Exponent:F2} を上げてください。"
            : $"入り直しは {score.MeanReEntries:F2}回/試行と少ないので、行き過ぎではなく"
              + $"「的の中で止まりきれない」側です。デッドゾーン {c.Deadzone:F2} を上げるか、"
              + $"min-cutoff {c.MinCutoffHz:F2} Hz を下げて静止時を静かにしてください。";

        findings.Add(new AimFinding(
            dominant ? AimFindingLevel.Problem : AimFindingLevel.Note,
            $"詰めに時間がかかっています (粗合わせ {score.MedianFirstTouchMs:F0}ms に対して、"
            + $"詰めが1試行あたり平均 {score.MeanSettleMs:F0}ms。"
            + $"一発で止まれなかった試行が {score.CorrectionRate:P0})。",
            cause));
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
        // 1試行の失敗は数えない。20試行のうち1回は、設定ではなく気の緩みでも起きる
        // (実測10本のうち1本が、ちょうど 1/20 で点いた)。2回から意味を持たせる。
        if (score.Timeouts < 2)
        {
            return;
        }

        int missed = score.NeverTouched;
        int touchedButLost = score.Timeouts - missed;
        var level = score.SuccessRate < 0.8 ? AimFindingLevel.Problem : AimFindingLevel.Note;

        if (missed >= 2)
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

        if (touchedButLost >= 2)
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

        bool coarse = c.CopResolutionMm > CoarseResolutionMm;
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

        // 遅いだけでは偶然と区別できない (1方向2〜3試行)。半径のほうにも証拠が要る ---
        // 張り付いている (それ以上速くできない) か、低いまま (そちらへ倒せていない) か。
        bool saturated = worst.MedianPeakRadius >= 0.98;
        bool cannotLean = worst.MedianPeakRadius <= 0.60;
        if (!saturated && !cannotLean)
        {
            return;
        }
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
            if (score.MeanSettleMs > SettleMeanNoteMs)
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
        else if (Math.Abs(factor - full) < 0.05 && score.MeanSettleMs > SettleMeanNoteMs)
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
        sb.AppendLine($"詰め (整定)         平均   {F(score.MeanSettleMs, "F0", " ms")}");
        sb.AppendLine($"  一発で止まれず    {score.CorrectionRate:P0} の試行 "
                    + $"(要ったとき {F(score.SettleP75Ms, "F0", " ms")} 前後)");
        sb.AppendLine($"維持完了まで        中央値 {F(score.MedianCompletionMs, "F0", " ms")}");
        sb.AppendLine($"入り直し            {score.MeanReEntries:F2} 回/試行");
        sb.AppendLine($"はみ出し            悪い1割 {F(score.OvershootP90Px, "F0", " px")}");
        sb.AppendLine($"経路効率            {F(score.MedianPathEfficiency * 100, "F0", " %")}");
        sb.AppendLine($"維持中の震え        {F(score.MedianDriftPx, "F1", " px (RMS)")}");
        sb.AppendLine($"使えた半径 (95%)    {F(score.PeakRadiusP95, "F2")}");
        sb.AppendLine($"スループット        {score.ThroughputBitsPerSec:F2} bit/秒");
        return sb.ToString();
    }
}
