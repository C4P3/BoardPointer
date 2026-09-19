using System.Text.Json;

namespace BoardPointer.Core.Settings;

/// <summary>
/// 設定の読み書き。INI ではなく JSON なのは、.NET に標準で入っていて依存が増えず、
/// 手で開いて直すぶんにも困らないため。整形して書くので差分も読める。
///
/// 較正 (<see cref="Hid.CalibrationStore"/>) と同じ場所に置く。
/// </summary>
public static class SettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BoardPointer",
        "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static bool TrySave(AppSettings settings, string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 保存できなくても動作には影響しない。次回が既定値になるだけ。
            return false;
        }
    }

    /// <summary>読めなければ既定値。壊れたファイルで起動できなくなるほうが困るので、例外は握る。</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file))
            {
                return new AppSettings();
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }
}
