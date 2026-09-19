using BoardPointer.Core.Hid;

namespace BoardPointer.Core.Recording;

/// <summary>
/// 生CSVの先頭に付けるメタデータ。工場較正をここに焼き込んでおくのが肝で、そうしないと
/// 「別のボードで記録したCSV」や「較正が読めなかったセッション」を後から再生したときに、
/// 生値を何kgと解釈すべきかが分からなくなる。
/// </summary>
public sealed record RecordingHeader(
    DateTimeOffset RecordedAt,
    string Source,
    string Note,
    FactoryCalibration? Calibration)
{
    public const string Magic = "# board-pointer-raw v1";
    public const string ColumnHeader = "t_ms,tr,br,tl,bl";

    public IEnumerable<string> ToLines()
    {
        yield return Magic;
        yield return $"# recorded={RecordedAt:O}";
        yield return $"# source={Source}";
        yield return $"# note={Note}";
        yield return Calibration is null
            ? "# calib=none"
            : $"# calib={Calibration.Format(0)}|{Calibration.Format(17)}|{Calibration.Format(34)}";
        yield return ColumnHeader;
    }

    public static RecordingHeader Parse(IEnumerable<string> commentLines)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in commentLines)
        {
            string body = line.TrimStart('#', ' ');
            int eq = body.IndexOf('=');
            if (eq > 0)
            {
                fields[body[..eq].Trim()] = body[(eq + 1)..].Trim();
            }
        }

        FactoryCalibration? calibration = null;
        if (fields.TryGetValue("calib", out string? calib) && calib != "none")
        {
            string[] points = calib.Split('|');
            if (points.Length == 3)
            {
                calibration = FactoryCalibration.TryParseHeader(points[0], points[1], points[2]);
            }
        }

        DateTimeOffset.TryParse(fields.GetValueOrDefault("recorded"), out var recordedAt);
        return new RecordingHeader(
            recordedAt,
            fields.GetValueOrDefault("source", "不明"),
            fields.GetValueOrDefault("note", string.Empty),
            calibration);
    }
}
