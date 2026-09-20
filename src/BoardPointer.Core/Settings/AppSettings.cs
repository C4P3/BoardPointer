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

    /// <summary>
    /// カーソルの送り方。"Absolute" (Windows のカーソル向け) または "Relative" (Raw Input 向け)。
    ///
    /// 相手によって決まるもので、好みではない。既定は絶対座標 --- 普通に使う相手はデスクトップで、
    /// そちらはポインタ加速を迂回できる絶対座標のほうが素直。
    /// </summary>
    public string PointerMode { get; set; } = "Absolute";

    /// <summary>
    /// 相対モードだけにかかる速度の倍率。絶対座標モードでは使わない。
    ///
    /// 相対モードの移動量はゲーム側が好きに解釈するので、絶対座標で詰めた最大速度が
    /// そのまま通用しない。モードごとに独立した倍率を持たせてある。
    /// </summary>
    public double RelativeGain { get; set; } = 1.0;

    /// <summary>"Off" / "Clutch" / "Throttle"。</summary>
    public string PressureMode { get; set; } = "Off";
    public double PressureEngageRatio { get; set; } = 0.97;

    /// <summary>合計荷重を均す時定数 [ms]。0 で平滑化しない。</summary>
    public double LoadSmoothingMs { get; set; } = 100;

    /// <summary>荷重の応答曲線の指数。</summary>
    public double LoadExponent { get; set; } = 1.0;

    /// <summary>作動側での速度の倍率。</summary>
    public double LoadFactorAtEngage { get; set; } = 1.0;

    /// <summary>振り切り側での速度の倍率。作動側より小さくしてよい (浮かせると遅くなる)。</summary>
    public double LoadFactorAtFull { get; set; } = 0.25;
    public double PressureFullRatio { get; set; } = 0.85;

    // --- 測ったもののうち、引き継ぐもの ---
    public bool ReachIsCalibrated { get; set; }
    public double ReachFrontMm { get; set; } = 60;
    public double ReachBackMm { get; set; } = 45;
    public double ReachLeftMm { get; set; } = 70;
    public double ReachRightMm { get; set; } = 70;

    /// <summary>安静時の基準荷重 [kg]。0 なら未測定で、荷重モードは無効になる。</summary>
    public double ReferenceLoadKg { get; set; }

    // --- エイムテストの前回の成績 ---
    //
    // 1回ぶんしか持たない。比較したい相手はほぼ必ず「つまみを触る直前の自分」で、それ以上の
    // 履歴が要るなら debug/aimtest_*.csv に全部残っている (測ったときの設定ごと)。
    // ここに積み上げると、設定ファイルが記録ファイルの役をしはじめる。

    /// <summary>前回測った日時 (表示用の文字列)。空なら未測定。</summary>
    public string LastAimTestAt { get; set; } = string.Empty;

    /// <summary>粗合わせ (初到達) の中央値 [ms]。</summary>
    public double LastAimFirstTouchMs { get; set; }

    /// <summary>詰め (整定) の中央値 [ms]。</summary>
    public double LastAimSettleMs { get; set; }

    /// <summary>維持完了までの中央値 [ms]。</summary>
    public double LastAimCompletionMs { get; set; }

    /// <summary>入り直しの平均 [回/試行]。</summary>
    public double LastAimReEntries { get; set; }

    /// <summary>粗合わせのスループット [bit/秒]。</summary>
    public double LastAimThroughput { get; set; }

    // --- 常駐 ---

    /// <summary>窓を閉じても終了せずトレイに残る。既定で有効 --- 常駐して使うのが本来の形。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>起動時に窓を出さずトレイだけで始める。</summary>
    public bool StartMinimized { get; set; }

    // --- ショートカット ---
    public HotkeyBinding ToggleOutput { get; set; } = new(0, 0x78);      // F9
    public HotkeyBinding LeftClick { get; set; } = new(0, 0x79);         // F10
    public HotkeyBinding RightClick { get; set; } = new(0, 0x7A);        // F11
    public HotkeyBinding RecenterOrigin { get; set; } = new(0, 0x7B);    // F12
    public HotkeyBinding TogglePointerMode { get; set; } = new(0, 0x77);  // F8

    public HotkeyBinding For(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOutput => ToggleOutput,
        HotkeyAction.LeftClick => LeftClick,
        HotkeyAction.RightClick => RightClick,
        HotkeyAction.RecenterOrigin => RecenterOrigin,
        HotkeyAction.TogglePointerMode => TogglePointerMode,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleOutput => "マウス出力 入/切",
        HotkeyAction.LeftClick => "左クリック",
        HotkeyAction.RightClick => "右クリック",
        HotkeyAction.RecenterOrigin => "重心の原点を今に合わせる",
        HotkeyAction.TogglePointerMode => "送り方 (デスクトップ/ゲーム) の切替",
        _ => action.ToString(),
    };
}
