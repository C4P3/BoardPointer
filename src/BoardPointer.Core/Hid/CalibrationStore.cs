namespace BoardPointer.Core.Hid;

/// <summary>
/// 一度読めた工場較正を保存しておいて、次に読めなかったときに使い回す。
///
/// 工場較正はボードに焼かれた定数で、実測でも別セッション間で1ビットも変わらなかった
/// (18238,18553,18850,6940|...)。一方で読み出しは接続のたびに成否が揺れる --- 拡張機能レジスタを
/// 初期化した直後はまだ読めないことがあり、リトライを入れても毎回成功するとは限らない。
///
/// 変わらない値を毎回取りに行って、失敗したら精度を捨てる、というのは筋が悪い。一度でも読めたら
/// 保存して、以降はそれを使う。読み直しに成功したら上書きするので、別のボードに差し替えても
/// 次の成功時点で追従する。
/// </summary>
public static class CalibrationStore
{
    /// <summary>
    /// 保存先。実行ファイルの隣ではなく %LOCALAPPDATA% に置く。Viewer と Replay は別々の
    /// ディレクトリにビルドされるので、AppContext.BaseDirectory だと片方で保存した較正を
    /// もう片方が見つけられない。
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BoardPointer",
        "calibration.txt");

    public static bool TrySave(FactoryCalibration calibration, string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, calibration.ToStorageString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても動作には影響しない (次回また読みに行くだけ)。
            return false;
        }
    }

    public static FactoryCalibration? TryLoad(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            return File.Exists(file) ? FactoryCalibration.TryParseStorage(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
