namespace BoardPointer.Core.Training;

/// <summary>1つの的。画面ピクセル座標で持つ。</summary>
/// <param name="CenterXPx">中心のX。</param>
/// <param name="CenterYPx">中心のY (下が正、画面座標のまま)。</param>
/// <param name="RadiusPx">半径。この中に入れば「触れた」。</param>
public readonly record struct AimTarget(double CenterXPx, double CenterYPx, double RadiusPx)
{
    public double DiameterPx => RadiusPx * 2;

    public double DistanceFrom(double xPx, double yPx)
    {
        double dx = xPx - CenterXPx;
        double dy = yPx - CenterYPx;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public bool Contains(double xPx, double yPx) => DistanceFrom(xPx, yPx) <= RadiusPx;
}

/// <summary>
/// 1試行ぶんの課題。「直前の的から次の的へ移す」が1試行。
/// </summary>
/// <param name="Index">通し番号 (0始まり)。</param>
/// <param name="From">出発点になる的。移動距離と方向はここから出す。</param>
/// <param name="Target">狙う的。</param>
/// <param name="IsWarmup">集計から除く試行か。</param>
public readonly record struct AimTrialPlan(int Index, AimTarget From, AimTarget Target, bool IsWarmup)
{
    /// <summary>的の中心間の距離 [px]。Fitts の D。</summary>
    public double DistancePx =>
        Math.Sqrt(Math.Pow(Target.CenterXPx - From.CenterXPx, 2) + Math.Pow(Target.CenterYPx - From.CenterYPx, 2));

    /// <summary>移動の向き [度]。右が0、上 (＝足を前に出す側) が90。</summary>
    public double DirectionDegrees
    {
        get
        {
            double dx = Target.CenterXPx - From.CenterXPx;
            double dy = -(Target.CenterYPx - From.CenterYPx); // 画面のYは下が正なので反転
            double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            return (deg % 360 + 360) % 360;
        }
    }

    /// <summary>
    /// Fitts の難易度指標 (Shannon 形式)。ID = log2(D/W + 1) [bit]。
    ///
    /// 距離と的の大きさを1つの数にまとめたもの。これがあると「大きい的に近く」と
    /// 「小さい的に遠く」を同じ土俵で比べられるので、初到達時間を ID に対して回帰すれば
    /// 設定ごとの素の速さ (bit/秒) が出る。
    /// </summary>
    public double IndexOfDifficulty => Math.Log2(DistancePx / Math.Max(1.0, Target.DiameterPx) + 1.0);
}

/// <summary>
/// 試行列を組み立てる。
///
/// 形は ISO 9241-411 の multi-directional tapping test に合わせてある。円周上に的を並べて
/// 順に叩かせるやり方で、**移動距離を一定に保ったまま方向だけを変えられる**のが要点。
/// 方向を散らすのはこのツールでは特に重要で、座って足先で操作すると前後と左右で可動域が
/// まるで違う (実測で前60 / 後45 / 左右70mm)。1方向だけ測っても、その非対称は見えない。
///
/// 的の数を8個にして3つ飛ばしで回る。8と3は互いに素なので8回で一周し、そのあいだに移動の
/// 向きが8方向すべて1回ずつ出る。弦の長さは常に同じなので、距離は固定のまま方向だけが変わる。
/// </summary>
public static class AimTestPlan
{
    /// <summary>円周上の的の数。移動の方向がそのまま8方向になる。</summary>
    public const int Directions = 8;

    /// <summary>何個飛ばしで回るか。8と互いに素なら何でも一周するが、3が最も対角に近い。</summary>
    private const int Step = 3;

    /// <summary>
    /// 的を並べる円の回転 [度]。22.5度ずらしてあるのには理由がある。
    ///
    /// 弦の向きは、的の位置そのものではなく2点の中点で決まる。的を真上から等間隔に置くと、
    /// 3つ飛ばしの弦の向きはちょうど 22.5度・67.5度・… と、**8方向の区切りの真上**に来る。
    /// そうなると方向の分類が丸め方次第で隣へ転び、8方向あるはずの集計が4方向に潰れる
    /// (実際に潰れた)。円ごと 22.5度 回すと弦の向きが 0・45・90・… に乗り、前後左右と斜めに
    /// きれいに1本ずつ入る。
    ///
    /// 代わりに、最初の「中央 → 1つ目の的」だけは斜めの半端な向きになる。これは距離も他と
    /// 違う導入の1手で、もともとウォームアップとして集計から外してある。
    /// </summary>
    private const double RingOffsetDegrees = 22.5;

    /// <summary>的を並べる円の半径を、画面の短辺の何倍にするか。</summary>
    private const double RingRadiusFraction = 0.34;

    /// <summary>
    /// 的の大きさ [px]。3水準あるのは、難易度によって壊れ方が違うため --- 大きい的で遅ければ
    /// 素の速さ (最大速度) の問題で、小さい的だけ落ちるなら止め際か分解能の問題になる。
    /// 1水準しか測らないと、この2つが混ざって原因が特定できない。
    ///
    /// 一番小さい的でも半径22px あるのは、足で操作するレート制御の分解能に対して余裕を持たせる
    /// ため。座位 (合計15kg) の重心の刻みは実測 0.14mm/count、静止時の揺れは中央値 1.1mm で、
    /// 可動域70mm で割ると正規化で 0.016 ぶれる。これがデッドゾーンの縁にいるときにそのまま
    /// カーソルの這いになる。小さすぎる的は「どの設定でも0%」になって水準として死ぬので、
    /// 1水準まるごと (試行の1/3) を無駄にするより、少し甘いほうがまだ情報が取れる。
    /// </summary>
    public static readonly double[] DefaultDiametersPx = [120, 72, 44];

    /// <summary>
    /// 集計から除く先頭の試行数。
    ///
    /// レート制御は数十秒で目に見えて上達するので、最初の数回を混ぜると「設定が良い」のか
    /// 「まだ慣れていなかった」のかが区別できなくなる。捨てるぶんは測定時間に載るが、
    /// 交絡を持ったまま数字を出すよりはるかにましで、しかもこれは自動調整をしない今でも効く
    /// --- 前回との比較が慣れの差で埋まってしまうため。
    /// </summary>
    public const int DefaultWarmupTrials = 4;

    /// <summary>
    /// 試行列を作る。
    /// </summary>
    /// <param name="widthPx">テスト画面の幅。</param>
    /// <param name="heightPx">テスト画面の高さ。</param>
    /// <param name="diametersPx">的の直径の水準。</param>
    /// <param name="warmupTrials">先頭の何試行を集計から除くか。</param>
    /// <param name="seed">
    /// 的の大きさの並びを決める乱数の種。大きさをブロックで固めると、疲労と学習が大きさに
    /// そのまま乗る (後半のブロックだけ不利になる)。試行ごとにばらして均す。
    /// </param>
    public static IReadOnlyList<AimTrialPlan> Build(
        int widthPx,
        int heightPx,
        IReadOnlyList<double>? diametersPx = null,
        int warmupTrials = DefaultWarmupTrials,
        int? seed = null)
    {
        diametersPx ??= DefaultDiametersPx;
        double centerX = widthPx / 2.0;
        double centerY = heightPx / 2.0;
        double ring = Math.Min(widthPx, heightPx) * RingRadiusFraction;

        // 大きさの並びは、各水準が同じ回数ずつ出るように作ってから混ぜる。均等であることを
        // 崩さずに順序だけ散らしたいので、水準ごとの繰り返しを作ってシャッフルする。
        int perDiameter = Directions;
        var sizes = new List<double>(diametersPx.Count * perDiameter);
        foreach (double d in diametersPx)
        {
            for (int i = 0; i < perDiameter; i++)
            {
                sizes.Add(d);
            }
        }
        var random = seed is null ? new Random() : new Random(seed.Value);
        for (int i = sizes.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (sizes[i], sizes[j]) = (sizes[j], sizes[i]);
        }

        AimTarget At(int slot, double diameter)
        {
            double angle = Math.PI / 2 + RingOffsetDegrees * Math.PI / 180
                         - 2 * Math.PI * slot / Directions;
            return new AimTarget(
                centerX + ring * Math.Cos(angle),
                centerY - ring * Math.Sin(angle),
                diameter / 2);
        }

        var trials = new List<AimTrialPlan>(sizes.Count + 1);

        // 最初の1つは画面中央から。ここだけ移動距離が他と違うので、必ずウォームアップに入れる。
        var first = At(0, sizes[0]);
        trials.Add(new AimTrialPlan(0, new AimTarget(centerX, centerY, first.RadiusPx), first, IsWarmup: true));

        int slot = 0;
        var previous = first;
        for (int i = 1; i < sizes.Count; i++)
        {
            slot = (slot + Step) % Directions;
            var target = At(slot, sizes[i]);
            trials.Add(new AimTrialPlan(i, previous, target, IsWarmup: i < warmupTrials));
            previous = target;
        }

        return trials;
    }

    /// <summary>
    /// 移動の向きを、ボードの上での言葉に直す。
    ///
    /// 画面の上下ではなく前後で呼ぶ。診断で突き合わせる相手が可動域 (前60 / 後45 / 左右70mm)
    /// なので、同じ言葉でないと「どの数字を測り直せばいいか」が繋がらない。
    /// </summary>
    public static string DirectionLabel(double degrees)
    {
        int bin = (int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8;
        return bin switch
        {
            0 => "右",
            1 => "右前",
            2 => "前",
            3 => "左前",
            4 => "左",
            5 => "左後",
            6 => "後",
            _ => "右後",
        };
    }
}
