namespace BoardPointer.Core.Settings;

/// <summary>
/// 次回に引き継ぐ設定。
///
/// 引き継ぐものと、毎回測り直すものを意図的に分けてある。
///
///   引き継ぐ : 曲線・閾値・モード（好みなので変わらない）
///              可動域と基準荷重（その人の体と姿勢に依存。そう変わらないし、測るのに12秒かかる）
///
///   毎回測る : 重心の原点（その日の足の置き場所で変わる）
///              荷重ゼロ点（ボードの設置場所で変わる）
///
/// 後ろ2つを復元すると、ボードが少し動いただけでずれた原点から始まり、しかも「なぜかカーソルが
/// 流れる」という分かりにくい形で症状が出る。3秒で測り直せるものを危険な形で引き継ぐ理由がない。
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>"Standing" または "SeatedFoot"。</summary>
    public string PostureMode { get; set; } = "Standing";

    // --- 信号のつまみ ---
    public bool FilterEnabled { get; set; } = true;
    public bool TareEnabled { get; set; } = true;
    public double MinCutoffHz { get; set; } = 1.0;
    public double Beta { get; set; } = 0.00005;
    public int TrailLength { get; set; } = 200;

    // --- 操作のつまみ ---
    public double Deadzone { get; set; } = 0.18;
    public double Exponent { get; set; } = 2.0;
    public double MaxSpeedPxPerSec { get; set; } = 900;
    public bool InvertY { get; set; }
    public bool ShareLeftRight { get; set; } = true;
    public bool AutoCenter { get; set; }

    /// <summary>"Off" / "Clutch" / "Throttle"。</summary>
    public string PressureMode { get; set; } = "Off";
    public double PressureEngageRatio { get; set; } = 0.97;
    public double PressureFullRatio { get; set; } = 0.85;

    // --- 測ったもののうち、引き継ぐもの ---
    public bool ReachIsCalibrated { get; set; }
    public double ReachFrontMm { get; set; } = 60;
    public double ReachBackMm { get; set; } = 45;
    public double ReachLeftMm { get; set; } = 70;
    public double ReachRightMm { get; set; } = 70;

    /// <summary>安静時の基準荷重 [kg]。0 なら未測定で、荷重モードは無効になる。</summary>
    public double ReferenceLoadKg { get; set; }

    // --- ショートカット ---
    public HotkeyBinding ToggleOutput { get; set; } = new(0, 0x78);      // F9
    public HotkeyBinding LeftClick { get; set; } = new(0, 0x79);         // F10
    public HotkeyBinding RightClick { get; set; } = new(0, 0x7A);        // F11
    public HotkeyBinding RecenterOrigin { get; set; } = new(0, 0x7B);    // F12

    public HotkeyBinding For(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOutput => ToggleOutput,
        HotkeyAction.LeftClick => LeftClick,
        HotkeyAction.RightClick => RightClick,
        HotkeyAction.RecenterOrigin => RecenterOrigin,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOutput => "マウス出力 入/切",
        HotkeyAction.LeftClick => "左クリック",
        HotkeyAction.RightClick => "右クリック",
        HotkeyAction.RecenterOrigin => "重心の原点を今に合わせる",
        _ => action.ToString(),
    };
}
