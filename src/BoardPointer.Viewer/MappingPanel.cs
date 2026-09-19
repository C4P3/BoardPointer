using BoardPointer.Core.Mapping;
using BoardPointer.Core.Settings;

namespace BoardPointer.Viewer;

/// <summary>
/// 重心 → カーソルのつまみ。触りながら詰めるための面なので、値はすべて即座に反映され、
/// 何を触っているかが右の曲線グラフに出る。
///
/// マッピング自体の状態は <see cref="PointerMapper"/> が持っていて、このパネルはそこへ書き込む
/// だけ。読み書きは UI スレッドとサンプルスレッドをまたぐが、対象が独立した double なので、
/// 最悪でも1フレームだけ古い値が使われるだけで済む。
/// </summary>
public sealed class MappingPanel : UserControl
{
    private readonly PointerMapper _mapper;

    private readonly Button _outputButton = new() { Text = "マウス出力 開始", AutoSize = true, Height = 30, FlatStyle = FlatStyle.System, Margin = new Padding(0, 0, 8, 6) };
    private readonly Button _calibrateButton = new() { Text = "可動域キャリブレーション (12秒)", AutoSize = true, Height = 30, FlatStyle = FlatStyle.System, Margin = new Padding(0, 0, 8, 6) };

    private readonly TrackBar _deadzoneBar = new() { Minimum = 0, Maximum = 60, Value = 18, TickStyle = TickStyle.None, AutoSize = false, Width = 150, Height = 30 };
    private readonly TrackBar _exponentBar = new() { Minimum = 50, Maximum = 400, Value = 200, TickStyle = TickStyle.None, AutoSize = false, Width = 150, Height = 30 };
    private readonly TrackBar _speedBar = new() { Minimum = 1, Maximum = 40, Value = 9, TickStyle = TickStyle.None, AutoSize = false, Width = 150, Height = 30 };
    private readonly Label _deadzoneLabel = MakeLabel();
    private readonly Label _exponentLabel = MakeLabel();
    private readonly Label _speedLabel = MakeLabel();

    private readonly CheckBox _shareLeftRight = new() { Text = "左右を共通化", Checked = true, AutoSize = true };
    private readonly CheckBox _invertY = new() { Text = "前後を反転", AutoSize = true };
    private readonly CheckBox _autoCenter = new() { Text = "原点を自動追従", AutoSize = true };

    private readonly ComboBox _pressureCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150,
        Margin = new Padding(0, 2, 0, 2),
    };
    private readonly TrackBar _engageBar = new() { Minimum = 50, Maximum = 200, Value = 97, TickStyle = TickStyle.None, AutoSize = false, Width = 150, Height = 30 };
    private readonly TrackBar _fullBar = new() { Minimum = 50, Maximum = 250, Value = 85, TickStyle = TickStyle.None, AutoSize = false, Width = 150, Height = 30 };
    private readonly Label _engageLabel = MakeLabel();
    private readonly Label _fullLabel = MakeLabel();

    private readonly Label _reachLabel = new()
    {
        AutoSize = false,
        Width = 230,
        Height = 36,
        ForeColor = Color.FromArgb(180, 188, 200),
        Font = new Font("Consolas", 8.5f),
    };
    private readonly Label _stateLabel = new()
    {
        AutoSize = false,
        Width = 230,
        Height = 36,
        ForeColor = Color.FromArgb(150, 158, 170),
        Font = new Font("Consolas", 8.5f),
    };

    private readonly CurveView _curveView = new() { Dock = DockStyle.Right, Width = 250 };

    /// <summary>[マウス出力] が押された。実際の有効/無効は MainForm が持つ。</summary>
    public event Action? OutputToggleRequested;

    /// <summary>[可動域キャリブレーション] が押された。</summary>
    public event Action? CalibrationRequested;

    public bool ShareLeftRight => _shareLeftRight.Checked;

    /// <summary>原点をゆっくり今の重心へ寄せるか。操作していない間だけ動く。</summary>
    public bool AutoCenter => _autoCenter.Checked;

    public MappingPanel(PointerMapper mapper)
    {
        _mapper = mapper;
        BackColor = Color.FromArgb(27, 29, 34);
        _curveView.SetCurve(mapper.Curve);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 8, 6, 6),
            AutoScroll = true,
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 14, 0) };
        buttons.Controls.AddRange([_outputButton, _calibrateButton, _shareLeftRight, _invertY, _autoCenter]);
        left.Controls.Add(buttons);

        left.Controls.Add(Stack(_deadzoneLabel, _deadzoneBar));
        left.Controls.Add(Stack(_exponentLabel, _exponentBar));
        left.Controls.Add(Stack(_speedLabel, _speedBar));

        _pressureCombo.Items.AddRange(["荷重: 使わない", "荷重: クラッチ", "荷重: 速度"]);
        _pressureCombo.SelectedIndex = 0;
        var pressureCombo = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 24, 10, 0) };
        pressureCombo.Controls.Add(_pressureCombo);
        left.Controls.Add(pressureCombo);
        left.Controls.Add(Stack(_engageLabel, _engageBar));
        left.Controls.Add(Stack(_fullLabel, _fullBar));

        var info = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10, 6, 0, 0) };
        info.Controls.AddRange([_reachLabel, _stateLabel]);
        left.Controls.Add(info);

        Controls.Add(left);
        Controls.Add(_curveView);

        _outputButton.Click += (_, _) => OutputToggleRequested?.Invoke();
        _calibrateButton.Click += (_, _) => CalibrationRequested?.Invoke();
        _deadzoneBar.ValueChanged += (_, _) => Apply();
        _exponentBar.ValueChanged += (_, _) => Apply();
        _speedBar.ValueChanged += (_, _) => Apply();
        _invertY.CheckedChanged += (_, _) => Apply();
        _pressureCombo.SelectedIndexChanged += (_, _) => Apply();
        _engageBar.ValueChanged += (_, _) => Apply();
        _fullBar.ValueChanged += (_, _) => Apply();
        Apply();
    }

    private static Label MakeLabel() => new() { AutoSize = true, Width = 150, ForeColor = Color.FromArgb(180, 188, 200) };

    private static Control Stack(Control top, Control bottom)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 10, 0) };
        stack.Controls.Add(top);
        stack.Controls.Add(bottom);
        return stack;
    }

    private void Apply()
    {
        _mapper.Curve.Deadzone = _deadzoneBar.Value / 100.0;
        _mapper.Curve.Exponent = _exponentBar.Value / 100.0;
        _mapper.Curve.MaxSpeedPxPerSec = _speedBar.Value * 100.0;
        _mapper.InvertY = _invertY.Checked;
        _mapper.Pressure = (PressureMode)_pressureCombo.SelectedIndex;
        _mapper.PressureEngageRatio = _engageBar.Value / 100.0;
        // 全開側は作動側より小さくてよい (軽くするほど速い向き)。同値だけは避ける。
        double full = _fullBar.Value / 100.0;
        double engage = _engageBar.Value / 100.0;
        _mapper.PressureFullRatio = Math.Abs(full - engage) < 0.02
            ? (full >= engage ? engage + 0.02 : engage - 0.02)
            : full;

        _deadzoneLabel.Text = $"デッドゾーン : {_mapper.Curve.Deadzone:F2}";
        _exponentLabel.Text = $"指数 : {_mapper.Curve.Exponent:F2}";
        _speedLabel.Text = $"最大速度 : {_mapper.Curve.MaxSpeedPxPerSec:F0} px/s";
        _engageLabel.Text = $"荷重 作動 : {_mapper.PressureEngageRatio:F2} 倍"
                          + (_mapper.Pressure == PressureMode.Off ? string.Empty
                             : _mapper.PressureEngagesWhenLighter ? " (軽くする)" : " (踏み込む)");
        _fullLabel.Text = $"荷重 全開 : {_mapper.PressureFullRatio:F2} 倍";
        bool usesPressure = _mapper.Pressure != PressureMode.Off;
        _engageBar.Enabled = usesPressure;
        _fullBar.Enabled = _mapper.Pressure == PressureMode.Throttle;
        _curveView.Invalidate();
    }

    /// <summary>
    /// 保存された設定を流し込む。スライダーに書くと ValueChanged 経由で Apply が走るので、
    /// マッパーへの反映は自動で揃う。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _deadzoneBar.Value = Clamp(_deadzoneBar, (int)Math.Round(settings.Deadzone * 100));
        _exponentBar.Value = Clamp(_exponentBar, (int)Math.Round(settings.Exponent * 100));
        _speedBar.Value = Clamp(_speedBar, (int)Math.Round(settings.MaxSpeedPxPerSec / 100));
        _engageBar.Value = Clamp(_engageBar, (int)Math.Round(settings.PressureEngageRatio * 100));
        _fullBar.Value = Clamp(_fullBar, (int)Math.Round(settings.PressureFullRatio * 100));
        _invertY.Checked = settings.InvertY;
        _shareLeftRight.Checked = settings.ShareLeftRight;
        _autoCenter.Checked = settings.AutoCenter;
        _pressureCombo.SelectedIndex = settings.PressureMode switch
        {
            "Clutch" => 1,
            "Throttle" => 2,
            _ => 0,
        };

        // 可動域と基準荷重は測るのに12秒かかるうえ、その人の体と姿勢にしか依存しないので引き継ぐ。
        if (settings.ReachIsCalibrated)
        {
            _mapper.Reach.FrontMm = settings.ReachFrontMm;
            _mapper.Reach.BackMm = settings.ReachBackMm;
            _mapper.Reach.LeftMm = settings.ReachLeftMm;
            _mapper.Reach.RightMm = settings.ReachRightMm;
            _mapper.Reach.MarkRestored(settings.ReferenceLoadKg);
        }
        Apply();
    }

    public void WriteTo(AppSettings settings)
    {
        settings.Deadzone = _mapper.Curve.Deadzone;
        settings.Exponent = _mapper.Curve.Exponent;
        settings.MaxSpeedPxPerSec = _mapper.Curve.MaxSpeedPxPerSec;
        settings.InvertY = _invertY.Checked;
        settings.ShareLeftRight = _shareLeftRight.Checked;
        settings.AutoCenter = _autoCenter.Checked;
        settings.PressureMode = _mapper.Pressure.ToString();
        settings.PressureEngageRatio = _mapper.PressureEngageRatio;
        settings.PressureFullRatio = _mapper.PressureFullRatio;

        settings.ReachIsCalibrated = _mapper.Reach.IsCalibrated;
        settings.ReachFrontMm = _mapper.Reach.FrontMm;
        settings.ReachBackMm = _mapper.Reach.BackMm;
        settings.ReachLeftMm = _mapper.Reach.LeftMm;
        settings.ReachRightMm = _mapper.Reach.RightMm;
        settings.ReferenceLoadKg = _mapper.Reach.ReferenceLoadKg;
    }

    private static int Clamp(TrackBar bar, int value) => Math.Clamp(value, bar.Minimum, bar.Maximum);

    private string _outputHotkeyLabel = string.Empty;
    private bool _outputEnabled;

    public void SetOutputEnabled(bool enabled)
    {
        _outputEnabled = enabled;
        UpdateOutputButton();
    }

    /// <summary>ボタンにも実際の割り当てを出す。設定で変えたのに「F9」と書いてあると嘘になる。</summary>
    public void SetOutputHotkeyLabel(string label)
    {
        _outputHotkeyLabel = label;
        UpdateOutputButton();
    }

    private void UpdateOutputButton()
    {
        string suffix = string.IsNullOrEmpty(_outputHotkeyLabel) ? string.Empty : $" ({_outputHotkeyLabel})";
        _outputButton.Text = (_outputEnabled ? "マウス出力 停止" : "マウス出力 開始") + suffix;
    }

    public void SetCalibrating(bool calibrating)
    {
        _calibrateButton.Enabled = !calibrating;
    }

    /// <summary>UIタイマーから毎回呼ぶ。</summary>
    public void UpdateLive(PointerCommand command, bool outputEnabled)
    {
        var reach = _mapper.Reach;
        _reachLabel.Text = reach.IsSampling
            ? $"可動域 測定中 ({reach.SampleCount})\n"
              + $"前{reach.PreviewFrontMm,4:F0} 後{reach.PreviewBackMm,4:F0} 左{reach.PreviewLeftMm,4:F0} 右{reach.PreviewRightMm,4:F0} mm"
            : $"可動域 {(reach.IsCalibrated ? $"実測 (基準荷重 {reach.ReferenceLoadKg:F1}kg)" : "既定値 (未測定)")}\n"
              + $"前{reach.FrontMm,4:F0} 後{reach.BackMm,4:F0} 左{reach.LeftMm,4:F0} 右{reach.RightMm,4:F0} mm";

        var range = _mapper.RecentRatioRange();
        double speed = Math.Sqrt(
            command.VelocityXPxPerSec * command.VelocityXPxPerSec +
            command.VelocityYPxPerSec * command.VelocityYPxPerSec);

        string state = !command.Active ? "停止 (乗っていない/荷重不足)"
            : command.InDeadzone ? "デッドゾーン内"
            : $"{speed,5:F0} px/s";

        _stateLabel.Text = $"半径 {command.NormalizedRadius,5:F2}  {state}\n"
                         + $"出力 {(outputEnabled ? "ON" : "OFF")}";

        _curveView.SetPosition(command.NormalizedRadius, command.Active && !command.InDeadzone);
        _curveView.Invalidate();
    }
}
