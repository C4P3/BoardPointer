using BoardPointer.Core.Sampling;

namespace BoardPointer.Core.Recording;

/// <summary>記録した生CSVを読み戻す。ヘッダのコメント行から工場較正も復元する。</summary>
public static class RawCsvReader
{
    public static (RecordingHeader Header, List<RawSample> Samples) Read(string path)
    {
        var comments = new List<string>();
        var samples = new List<RawSample>();

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }
            if (line[0] == '#')
            {
                comments.Add(line);
                continue;
            }
            if (line.StartsWith("t_ms", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 列見出し
            }

            string[] parts = line.Split(',');
            if (parts.Length < 5)
            {
                continue;
            }
            if (long.TryParse(parts[0], out long t)
                && ushort.TryParse(parts[1], out ushort tr)
                && ushort.TryParse(parts[2], out ushort br)
                && ushort.TryParse(parts[3], out ushort tl)
                && ushort.TryParse(parts[4], out ushort bl))
            {
                samples.Add(new RawSample(t, tr, br, tl, bl));
            }
        }

        return (RecordingHeader.Parse(comments), samples);
    }
}
