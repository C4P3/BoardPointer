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

    /// <summary>
    /// 書き出したものを読み戻す。
    ///
    /// 書けるだけで読めないと、記録は「あとで見るかもしれない何か」で終わる。読み戻せると、
    /// **過去に実機で測ったぶん全部に対して、直した診断を当て直せる**。指摘の閾値は感覚では
    /// 置けない (0.5回/試行が多いのか少ないのかは、自分の普通を知らないと決まらない) ので、
    /// この経路が無いと閾値だけが一生仮のままになる。
    /// </summary>
    public static (IReadOnlyList<AimTrialResult> Trials, AimTestConditions Conditions) Read(string path)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trials = new List<AimTrialResult>();
        string[]? header = null;

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }
                string key = line[1..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                meta[key] = value;

                // curve=deadzone:0.180,exponent:2.000 のような入れ子は平らに展開しておく。
                foreach (string part in value.Split(','))
                {
                    int colon = part.IndexOf(':');
                    if (colon > 0)
                    {
                        meta[$"{key}.{part[..colon].Trim()}"] = part[(colon + 1)..].Trim();
                    }
                }
                continue;
            }

            if (header is null)
            {
                header = line.Split(',');
                continue;
            }

            string[] f = line.Split(',');
            double Field(string name, double fallback = -1)
            {
                int i = Array.IndexOf(header, name);
                return i >= 0 && i < f.Length
                       && double.TryParse(f[i], NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                    ? v : fallback;
            }

            trials.Add(new AimTrialResult(
                Index: (int)Field("index", 0),
                IsWarmup: Field("warmup", 0) != 0,
                TimedOut: Field("timed_out", 0) != 0,
                DistancePx: Field("distance_px"),
                TargetDiameterPx: Field("target_dia_px"),
                DirectionDegrees: Field("direction_deg"),
                IndexOfDifficulty: Field("id_bits"),
                FirstTouchMs: Field("first_touch_ms"),
                SettleMs: Field("settle_ms"),
                CompletionMs: Field("completion_ms"),
                ReEntries: (int)Field("re_entries", 0),
                MaxOvershootPx: Field("max_overshoot_px"),
                PathEfficiency: Field("path_efficiency"),
                DwellDriftPx: Field("dwell_drift_px"),
                ClosestApproachPx: Field("closest_px"),
                PeakRadius: Field("peak_radius"),
                PeakSpeedPxPerSec: Field("peak_speed_px_s"),
                MinPressureFactor: Field("min_pressure_factor", 1)));
        }

        double M(string key, double fallback = 0) =>
            meta.TryGetValue(key, out string? v)
            && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
                ? d : fallback;
        string S(string key, string fallback) => meta.TryGetValue(key, out string? v) ? v : fallback;

        var conditions = new AimTestConditions(
            PostureMode: S("posture", "Standing"),
            Deadzone: M("curve.deadzone"),
            FullScale: M("curve.fullscale", 0.95),
            Exponent: M("curve.exponent", 1),
            MaxSpeedPxPerSec: M("curve.maxspeed", 900),
            MinCutoffHz: M("filter.mincutoff", 1),
            Beta: M("filter.beta"),
            ReachIsCalibrated: M("reach.calibrated") != 0,
            ReachFrontMm: M("reach.front"),
            ReachBackMm: M("reach.back"),
            ReachLeftMm: M("reach.left"),
            ReachRightMm: M("reach.right"),
            PressureMode: S("pressure.mode", "Off"),
            PressureEngageRatio: M("pressure.engage", 1),
            PressureFullRatio: M("pressure.full", 1),
            LoadFactorAtEngage: M("pressure.at_engage", 1),
            LoadFactorAtFull: M("pressure.at_full", 1),
            CopResolutionMm: M("signal.cop_res_mm"),
            TotalKg: M("signal.total_kg"));

        return (trials, conditions);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
