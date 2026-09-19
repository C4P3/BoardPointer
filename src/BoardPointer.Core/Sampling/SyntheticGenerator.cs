using BoardPointer.Core.Hid;

namespace BoardPointer.Core.Sampling;

/// <summary>
/// 実機が無くてもパイプラインとUIを端から端まで動かすための、それらしい生データの生成。
///
/// 作っているのは「実測の代わり」ではなく「配管の検査用の水」。本物のチューニングは必ず実機の記録で
/// やること。ただし、以下の性質はわざと本物に寄せてある:
///
///  - 最初の数秒は誰も乗っていない (ゼロ点調整を試せる)
///  - 静止しているつもりでも重心は止まらない。人間の立位は閉ループの姿勢制御なので、0.2〜1.2Hz
///    あたりの揺れが常に出る。フィルタのつまみを触ったときの手応えはここから来る
///  - 数秒おきにゆっくり大きく傾ける区間が入る (1ユーロフィルタの「速い動きでは遅れを減らす」側が
///    効いているかを見るため)
///  - 床が完全に水平ではない前提の、各隅のわずかなオフセット
/// </summary>
public static class SyntheticGenerator
{
    private const double SampleRateHz = 100.0;
    private const double EmptySeconds = 3.0;
    private const double BodyWeightKg = 62.0;

    // 床の傾き/脚の高さのばらつきに相当する、誰も乗っていなくても乗っている各隅のオフセット [kg]。
    private static readonly double[] RestingOffsetKg = [0.42, -0.18, 0.15, 0.31];

    public static List<RawSample> Generate(double seconds, FactoryCalibration calibration, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new List<RawSample>((int)(seconds * SampleRateHz));
        double halfWidth = 433.0 / 2.0;
        double halfLength = 238.0 / 2.0;

        for (int i = 0; i < seconds * SampleRateHz; i++)
        {
            double t = i / SampleRateHz;
            double totalKg;
            double copX = 0, copY = 0;

            if (t < EmptySeconds)
            {
                totalKg = 0;
            }
            else
            {
                double u = t - EmptySeconds;

                // 乗り込みの1秒は荷重が立ち上がる途中。
                totalKg = BodyWeightKg * Math.Min(1.0, u / 1.0);

                // 静止立位の揺れ。足首まわりのゆっくりした成分に、速い小さい成分を重ねる。
                double swayX = 6.0 * Math.Sin(2 * Math.PI * 0.35 * u) + 2.2 * Math.Sin(2 * Math.PI * 1.10 * u + 1.3);
                double swayY = 8.0 * Math.Sin(2 * Math.PI * 0.28 * u + 0.7) + 3.0 * Math.Sin(2 * Math.PI * 0.90 * u);

                // 6秒周期で四隅方向へゆっくり大きく傾ける区間。
                int phase = (int)(u / 6.0) % 4;
                double within = (u % 6.0) / 6.0;
                double ramp = Math.Sin(Math.PI * Math.Clamp((within - 0.25) / 0.5, 0, 1));
                double leanX = phase is 0 or 2 ? 0 : (phase == 1 ? 1 : -1) * ramp * halfWidth * 0.55;
                double leanY = phase is 1 or 3 ? 0 : (phase == 0 ? 1 : -1) * ramp * halfLength * 0.55;

                copX = swayX + leanX + Gaussian(random) * 0.8;
                copY = swayY + leanY + Gaussian(random) * 0.8;
            }

            samples.Add(ToRawSample(t, totalKg, copX, copY, halfWidth, halfLength, calibration, random));
        }

        return samples;
    }

    /// <summary>
    /// 合計荷重と重心から4隅の荷重を復元する。重心の式をそのまま逆に解いた双線形配分で、
    /// パイプライン側の重心計算と厳密に逆関数の関係になる (合成 → パイプライン で元の重心が戻る)。
    /// </summary>
    private static RawSample ToRawSample(
        double t, double totalKg, double copX, double copY,
        double halfWidth, double halfLength, FactoryCalibration calibration, Random random)
    {
        double fx = totalKg > 0 ? Math.Clamp(copX / halfWidth, -1, 1) : 0;
        double fy = totalKg > 0 ? Math.Clamp(copY / halfLength, -1, 1) : 0;
        double quarter = totalKg / 4.0;

        double[] cornersKg =
        [
            quarter * (1 + fx) * (1 + fy), // TR
            quarter * (1 + fx) * (1 - fy), // BR
            quarter * (1 - fx) * (1 + fy), // TL
            quarter * (1 - fx) * (1 - fy), // BL
        ];

        var raw = new ushort[4];
        for (int c = 0; c < 4; c++)
        {
            // ゼロ点のオフセットと、ひずみゲージの読み取りノイズを足してから生値に戻す。
            double kg = cornersKg[c] + RestingOffsetKg[c] + Gaussian(random) * 0.05;
            raw[c] = calibration.ToRaw(c, kg);
        }

        return new RawSample((long)Math.Round(t * 1000), raw[0], raw[1], raw[2], raw[3]);
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
