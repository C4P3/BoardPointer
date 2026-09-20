using System.Globalization;
using System.Text;

namespace BoardPointer.Core.Training;

/// <summary>
/// テストの結果を1ファイルに落とす。
///
/// 生CSV とは別のファイルにする。生CSV は「生値だけを書く」という約束で動いていて、そこに
/// 試行の結果という派生値を混ぜると約束が崩れる。逆にこちらは**測ったときの設定を丸ごと
/// ヘッダに焼き込む**のが肝で、それが無いと「先週のほうが速かった」に意味が無くなる
/// --- 何を変えたから速かったのかが残らない。
/// </summary>
public static class AimTestLog
{
    public const string Magic = "# board-pointer-aimtest v1";

    /// <param name="allTrials">
    /// ウォームアップも含めた全試行。集計からは外すが、書き出しには残す --- 「最初の数回だけ
    /// 極端に遅い」のを後から確かめられるほうが、捨てた事実だけが残るより役に立つ。
    /// </param>
    public static string Write(
        string folder,
        AimTestScore score,
        IReadOnlyList<AimTrialResult> allTrials,
        in AimTestConditions c,
        double dwellMs)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"aimtest_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine(Magic);
        sb.AppendLine($"# measured={DateTimeOffset.Now:O}");
        sb.AppendLine($"# posture={c.PostureMode}");
        sb.AppendLine($"# dwell_ms={dwellMs:F0}");
        sb.AppendLine(Invariant($"# curve=deadzone:{c.Deadzone:F3},fullscale:{c.FullScale:F3},exponent:{c.Exponent:F3},maxspeed:{c.MaxSpeedPxPerSec:F0}"));
        sb.AppendLine(Invariant($"# filter=mincutoff:{c.MinCutoffHz:F3},beta:{c.Beta:F6}"));
        sb.AppendLine(Invariant($"# reach=calibrated:{(c.ReachIsCalibrated ? 1 : 0)},front:{c.ReachFrontMm:F1},back:{c.ReachBackMm:F1},left:{c.ReachLeftMm:F1},right:{c.ReachRightMm:F1}"));
        sb.AppendLine(Invariant($"# pressure=mode:{c.PressureMode},engage:{c.PressureEngageRatio:F3},full:{c.PressureFullRatio:F3},at_engage:{c.LoadFactorAtEngage:F2},at_full:{c.LoadFactorAtFull:F2}"));
        sb.AppendLine(Invariant($"# signal=cop_res_mm:{c.CopResolutionMm:F3},total_kg:{c.TotalKg:F2}"));
        sb.AppendLine(Invariant($"# score=first_touch_ms:{score.MedianFirstTouchMs:F0},settle_ms:{score.MedianSettleMs:F0},completion_ms:{score.MedianCompletionMs:F0},re_entries:{score.MeanReEntries:F3},throughput_bits:{score.ThroughputBitsPerSec:F3},success:{score.SuccessRate:F3}"));
        sb.AppendLine(AimTrialResult.CsvHeader);

        foreach (var trial in allTrials)
        {
            sb.AppendLine(trial.ToCsv());
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
