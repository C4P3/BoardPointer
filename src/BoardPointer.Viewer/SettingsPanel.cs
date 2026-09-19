using BoardPointer.Core.Settings;

namespace BoardPointer.Viewer;

/// <summary>キー入力をそのまま割り当てとして受け取る枠。フォーカス中は全キーを横取りする。</summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBinding Binding { get; private set; } = new();

    /// <summary>割り当てが変わった。呼び出し側は登録し直す。</summary>
    public event Action? Changed;

    public HotkeyBox()
    {
        ReadOnly = true;
        Width = 150;
        TextAlign = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(38, 41, 48);
        ForeColor = Color.FromArgb(220, 225, 232);
        BorderStyle = BorderStyle.FixedSingle;
    }

    public void SetBinding(HotkeyBinding binding)
    {
        Binding = binding;
        Text = binding.ToString();
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Text = "キーを押してください (Esc で解除)";
        BackColor = Color.FromArgb(58, 52, 30);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        BackColor = Color.FromArgb(38, 41, 48);
        Text = Binding.ToString();
    }

    // OnKeyDown ではなく ProcessCmdKey なのは、Tab や矢印キーがフォーカス移動として先に消費されて
    // しまい、割り当てられなくなるため。
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return true; // 修飾キー単体では確定しない
        }

        if (key == Keys.Escape)
        {
            SetBinding(new HotkeyBinding());
            Changed?.Invoke();
            return true;
        }

        int modifiers = 0;
        if ((keyData & Keys.Control) != 0) modifiers |= HotkeyBinding.ModControl;
        if ((keyData & Keys.Alt) != 0) modifiers |= HotkeyBinding.ModAlt;
        if ((keyData & Keys.Shift) != 0) modifiers |= HotkeyBinding.ModShift;

        SetBinding(new HotkeyBinding(modifiers, (int)key));
        Changed?.Invoke();
        return true;
    }
}

/// <summary>
/// ショートカットの割り当てと、設定の保存場所を出す面。
///
/// 「マウス出力 入/切」は必ず割り当てておくこと。出力中はこの窓のボタンを押しに行くのが難しく
/// なるので、フォーカスに依らず止められる経路が無いと詰む。
/// </summary>
public sealed class SettingsPanel : UserControl
{
    private readonly Dictionary<HotkeyAction, HotkeyBox> _boxes = [];
    private readonly Label _note = new()
    {
        AutoSize = false,
        Width = 520,
        Height = 56,
        ForeColor = Color.FromArgb(140, 148, 160),
        Margin = new Padding(0, 10, 0, 0),
    };

    /// <summary>どれかの割り当てが変わった。</summary>
    public event Action? HotkeysChanged;

    /// <summary>キー入力を捕まえている間は、グローバル登録を外してほしい。</summary>
    public event Action? CaptureStarted;
    public event Action? CaptureEnded;

    public SettingsPanel()
    {
        BackColor = Color.FromArgb(27, 29, 34);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14, 10, 10, 10),
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            var label = new Label
            {
                Text = AppSettings.Label(action),
                AutoSize = true,
                Margin = new Padding(0, 7, 8, 6),
                ForeColor = Color.FromArgb(200, 208, 218),
            };
            var box = new HotkeyBox { Margin = new Padding(0, 4, 0, 4) };
            box.Changed += () => HotkeysChanged?.Invoke();
            box.Enter += (_, _) => CaptureStarted?.Invoke();
            box.Leave += (_, _) => CaptureEnded?.Invoke();
            _boxes[action] = box;

            layout.Controls.Add(label);
            layout.Controls.Add(box);
        }

        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true });
        layout.Controls.Add(_note);
        Controls.Add(layout);

        _note.Text = "枠をクリックしてからキーを押すと割り当てが変わります (Esc で解除)。\n"
                   + "「マウス出力 入/切」は必ず割り当てておいてください。出力中はこの窓を操作しにくくなります。\n"
                   + $"設定の保存先: {SettingsStore.DefaultPath}";
    }

    public void ApplySettings(AppSettings settings)
    {
        foreach (var (action, box) in _boxes)
        {
            box.SetBinding(settings.For(action));
        }
    }

    public void WriteTo(AppSettings settings)
    {
        settings.ToggleOutput = _boxes[HotkeyAction.ToggleOutput].Binding;
        settings.LeftClick = _boxes[HotkeyAction.LeftClick].Binding;
        settings.RightClick = _boxes[HotkeyAction.RightClick].Binding;
        settings.RecenterOrigin = _boxes[HotkeyAction.RecenterOrigin].Binding;
    }

    /// <summary>登録できなかった割り当てを赤くして知らせる。</summary>
    public void MarkFailed(IReadOnlyCollection<HotkeyAction> failed)
    {
        foreach (var (action, box) in _boxes)
        {
            box.ForeColor = failed.Contains(action)
                ? Color.FromArgb(255, 150, 140)
                : Color.FromArgb(220, 225, 232);
        }
    }
}
