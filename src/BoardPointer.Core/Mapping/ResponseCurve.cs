namespace BoardPointer.Core.Mapping;

/// <summary>
/// 正規化された半径 (0〜1) から、カーソルの速度 [px/秒] を出す曲線。
///
/// 形はこう:
///
///   半径 &lt; デッドゾーン        → 速度 0
///   デッドゾーン 〜 フルスケール → 0 から最大速度まで、指数で立ち上がる
///   フルスケール以上            → 最大速度で頭打ち
///
/// デッドゾーンの縁で速度がちょうど 0 から立ち上がるので、境界がばたついてもカーソルは飛ばない。
/// 段ごとに定速にすると、境界を跨ぐたびに速度が階段状に変わって明確に気持ち悪くなるので、
/// 途中は必ず連続にしてある。
///
/// つまみが指数1つなのは意図的。制御点を打てる曲線エディタは後から足せばよく、先に指数で
/// 遊んでおくと「どういう形が欲しいか」が分かった状態でエディタに向かえる。
/// 指数 1 で直線、上げるほど中心付近が緩く（狙いやすく）、端が急に（速く）なる。
/// </summary>
public sealed class ResponseCurve
{
    /// <summary>ここまでは速度0 [正規化半径]。可動域に対する割合なので、人と姿勢に依らず意味が同じ。</summary>
    public double Deadzone { get; set; } = 0.18;

    /// <summary>最大速度に到達する半径 [正規化]。1.0 なら可動域いっぱいで最大。</summary>
    public double FullScale { get; set; } = 0.95;

    /// <summary>立ち上がりの指数。1 で直線、大きいほど中心付近が緩やかになる。</summary>
    public double Exponent { get; set; } = 2.0;

    /// <summary>フルスケールでのカーソル速度 [px/秒]。</summary>
    public double MaxSpeedPxPerSec { get; set; } = 900;

    public double SpeedAt(double radius)
    {
        double dead = Math.Clamp(Deadzone, 0, 0.95);
        double full = Math.Max(dead + 0.01, FullScale);

        if (radius <= dead)
        {
            return 0;
        }

        double t = Math.Clamp((radius - dead) / (full - dead), 0, 1);
        return MaxSpeedPxPerSec * Math.Pow(t, Math.Max(0.1, Exponent));
    }
}
