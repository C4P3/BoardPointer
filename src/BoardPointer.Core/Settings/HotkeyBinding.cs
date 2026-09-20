using System.Text.Json.Serialization;

namespace BoardPointer.Core.Settings;

/// <summary>ショートカットに割り当てられる動作。</summary>
public enum HotkeyAction
{
    /// <summary>マウス出力の入り切り。カーソルが暴れても止められる経路なので、必ず割り当てておくこと。</summary>
    ToggleOutput,

    /// <summary>左クリック。</summary>
    LeftClick,

    /// <summary>右クリック。</summary>
    RightClick,

    /// <summary>
    /// 重心の原点を今の位置に合わせ直す。
    ///
    /// ボタンの [重心の原点 (3秒)] とは別物で、こちらは待たずに即座に合わせる。操作中に姿勢が
    /// 変わって「カーソルが一方向に流れる」と感じた瞬間に押すためのもので、3秒待てる場面のための
    /// 丁寧な測定とは用途が違う。値はフィルタ後の重心を使うので、1サンプルぶんのノイズは乗らない。
    /// </summary>
    RecenterOrigin,

    /// <summary>
    /// カーソルの送り方 (絶対座標 / 相対) を切り替える。
    ///
    /// 設定タブにも置いてあるが、押したい場面がまさに「ゲームに入ってカーソルが効かないと
    /// 気づいたとき」なので、窓に戻らずに切り替えられる経路が要る。全画面のゲームから
    /// この窓を出すのは、マウス出力を止めるのと同じくらい面倒。
    /// </summary>
    TogglePointerMode,
}

/// <summary>
/// グローバルショートカット1つぶん。RegisterHotKey に渡す修飾キーと仮想キーの組。
///
/// グローバルにしてあるのは、マウス出力中はこのアプリの窓のボタンを押しに行くのが難しくなるため。
/// フォーカスに依らず効く経路が要る。
/// </summary>
public sealed class HotkeyBinding
{
    /// <summary>RegisterHotKey の修飾キー。Alt=1, Ctrl=2, Shift=4, Win=8。</summary>
    public int Modifiers { get; set; }

    /// <summary>仮想キーコード。0 なら未割り当て。</summary>
    public int VirtualKey { get; set; }

    /// <summary>派生値なので保存しない。ファイルに出ても読み戻せず、紛らわしいだけ。</summary>
    [JsonIgnore]
    public bool IsAssigned => VirtualKey != 0;

    public HotkeyBinding() { }

    public HotkeyBinding(int modifiers, int virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>人が読める表記。設定画面に出す。</summary>
    public override string ToString()
    {
        if (!IsAssigned)
        {
            return "(なし)";
        }

        var parts = new List<string>(4);
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(int virtualKey) => virtualKey switch
    {
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        0x91 => "ScrollLock",
        0x13 => "Pause",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x20 => "Space",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x60 and <= 0x69 => $"テンキー{virtualKey - 0x60}",
        _ => $"0x{virtualKey:X2}",
    };
}
