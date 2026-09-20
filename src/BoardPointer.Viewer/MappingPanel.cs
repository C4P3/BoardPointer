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
    private readonly MouseOutput _output;

    private readonly Button _outputButton = new() { Text = "マウス出力 開始", AutoSize = true, Height = 30, FlatStyle = FlatStyle.System, Margin = new Padding(0, 0, 8, 6) };
    private readonly Button _calibrateButton = new() { Text = "可動域キャリブレーション (12秒)", AutoSize = true, Height = 30, FlatStyle = FlatStyle.System, Margin = new Padding(0, 0, 8, 6) };

    private readonly TrackBar _deadzoneBar = new() { Minimum = 0, Maximum = 60, Value = 18, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _exponentBar = new() { Minimum = 50, Maximum = 400, Value = 200, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _speedBar = new() { Minimum = 1, Maximum = 40, Value = 9, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly Label _deadzoneLabel = MakeLabel();
    private readonly Label _exponentLabel = MakeLabel();
    private readonly Label _speedLabel = MakeLabel();

    /// <summary>
    /// カーソルの送り方。好みではなく**相手**で選ぶものなので、挙動 (絶対座標/相対) ではなく
    /// 相手の名前で並べる。絶対座標・相対デルタと書いても、どちらを選べばいいかは分からない。
    /// </summary>
    private readonly ComboBox _pointerModeCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150,
        Margin = new Padding(0, 2, 0, 4),
    };
    private readonly TrackBar _relativeGainBar = new() { Minimum = 10, Maximum = 400, Value = 100, TickStyle = TickStyle.None, AutoSize = false, Width = 150, Height = 30, Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _relativeGainLabel = new() { AutoSize = true, Width = 150, ForeColor = Color.FromArgb(180, 188, 200), Margin = new Padding(0, 2, 0, 0) };

    private readonly CheckBox _shareLeftRight = new() { Text = "左右を共通化", Checked = true, AutoSize = true };
    private readonly CheckBox _invertY = new() { Text = "前後を反転", AutoSize = true };
    private readonly CheckBox _autoCenter = new() { Text = "原点を自動追従", AutoSize = true };

    private readonly ComboBox _pressureCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150,
        Margin = new Padding(0, 2, 0, 4),
    };
    private readonly TrackBar _engageBar = new() { Minimum = 50, Maximum = 200, Value = 97, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _fullBar = new() { Minimum = 50, Maximum = 250, Value = 85, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _loadSmoothBar = new() { Minimum = 0, Maximum = 500, Value = 100, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _loadExponentBar = new() { Minimum = 30, Maximum = 300, Value = 100, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _loadAtEngageBar = new() { Minimum = 0, Maximum = 100, Value = 100, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly TrackBar _loadAtFullBar = new() { Minimum = 0, Maximum = 100, Value = 25, TickStyle = TickStyle.None, AutoSize = false, Width = 130, Height = 30 };
    private readonly Label _loadSmoothLabel = MakeLabel();
    private readonly Label _loadExponentLabel = MakeLabel();
    private readonly Label _loadAtEngageLabel = MakeLabel();
    private readonly Label _loadAtFullLabel = MakeLabel();
    private readonly Button _loadRangeButton = new() { Text = "荷重の範囲を測る (8秒)", AutoSize = true, Height = 28, FlatStyle = FlatStyle.System, Margin = new Padding(0, 4, 8, 0) };
    private readonly Label _engageLabel = MakeLabel();
    private readonly Label _fullLabel = MakeLabel();

    /// <summary>
    /// 右の読み取り表示に出す行。このパネルに置くと、つまみが増えるたびに行が溢れる。
    /// 操作するものと読むものは面を分ける。
    /// </summary>
    public string StatusText { get; private set; } = string.Empty;

    private readonly CurveView _curveView = new() { Dock = DockStyle.Right, Width = 215 };

    /// <summary>[マウス出力] が押された。実際の有効/無効は MainForm が持つ。</summary>
    public event Action? OutputToggleRequested;

    /// <summary>[可動域キャリブレーション] が押された。</summary>
    public event Action? CalibrationRequested;

    /// <summary>[荷重の範囲を測る] が押された。</summary>
    public event Action? LoadRangeRequested;

    /// <summary>送り方が変わった。MainForm が状況表示とトレイを更新する。</summary>
    public event Action? PointerModeChanged;

    public bool ShareLeftRight => _shareLeftRight.Checked;

    /// <summary>原点をゆっくり今の重心へ寄せるか。操作していない間だけ動く。</summary>
    public bool AutoCenter => _autoCenter.Checked;

    public MappingPanel(PointerMapper mapper, MouseOutput output)
    {
        _mapper = mapper;
        _output = output;
        BackColor = Color.FromArgb(27, 29, 34);
        _curveView.SetCurve(mapper.Curve);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 8, 6, 6),
            AutoScroll = true,
        };

        // 送り先は出力ボタンのすぐ下に置く。どちらも「出した先」の話で、重心の曲線のつまみとは
        // 別の階層にある。横に並べると1行目が伸びて、荷重のつまみが折り返しの下に落ちる。
        _pointerModeCombo.Items.AddRange(["送り先: デスクトップ", "送り先: ゲーム (視点)"]);
        _pointerModeCombo.SelectedIndex = 0;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 14, 0) };
        buttons.Controls.AddRange([
            _outputButton, _calibrateButton,
            _pointerModeCombo, _relativeGainLabel, _relativeGainBar,
            _shareLeftRight, _invertY, _autoCenter]);
        left.Controls.Add(buttons);

        left.Controls.Add(Stack(_deadzoneLabel, _deadzoneBar));
        left.Controls.Add(Stack(_exponentLabel, _exponentBar));
        left.Controls.Add(Stack(_speedLabel, _speedBar));
        var smoothStack = Stack(_loadSmoothLabel, _loadSmoothBar);
        left.Controls.Add(smoothStack);
        left.SetFlowBreak(smoothStack, true); // ここで1行目を閉じる

        _pressureCombo.Items.AddRange(["荷重: 使わない", "荷重: クラッチ", "荷重: 速度"]);
        _pressureCombo.SelectedIndex = 0;
        var pressureCombo = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 6, 0) };
        pressureCombo.Controls.AddRange([_pressureCombo, _loadRangeButton]);
        left.Controls.Add(pressureCombo);
        left.Controls.Add(Stack(_engageLabel, _engageBar));
        left.Controls.Add(Stack(_fullLabel, _fullBar));
        left.Controls.Add(Stack(_loadExponentLabel, _loadExponentBar));
        left.Controls.Add(Stack(_loadAtEngageLabel, _loadAtEngageBar));
        left.Controls.Add(Stack(_loadAtFullLabel, _loadAtFullBar));

        Controls.Add(left);
        Controls.Add(_curveView);

        _outputButton.Click += (_, _) => OutputToggleRequested?.Invoke();
        _calibrateButton.Click += (_, _) => CalibrationRequested?.Invoke();
        _pointerModeCombo.SelectedIndexChanged += (_, _) => { Apply(); PointerModeChanged?.Invoke(); };
        _relativeGainBar.ValueChanged += (_, _) => Apply();
        _deadzoneBar.ValueChanged += (_, _) => Apply();
        _exponentBar.ValueChanged += (_, _) => Apply();
        _speedBar.ValueChanged += (_, _) => Apply();
        _invertY.CheckedChanged += (_, _) => Apply();
        _pressureCombo.SelectedIndexChanged += (_, _) => Apply();
        _engageBar.ValueChanged += (_, _) => Apply();
        _fullBar.ValueChanged += (_, _) => Apply();
        _loadSmoothBar.ValueChanged += (_, _) => Apply();
        _loadExponentBar.ValueChanged += (_, _) => Apply();
        _loadAtEngageBar.ValueChanged += (_, _) => Apply();
        _loadAtFullBar.ValueChanged += (_, _) => Apply();
        _loadRangeButton.Click += (_, _) => LoadRangeRequested?.Invoke();
        Apply();
    }

    /// <summary>
    /// 安静時 (比 1.00) が作動と全開の間に無いと、倍率は常に端に張り付いて動かない。
    /// 両方の閾値を 1.00 より下 (あるいは上) に置いてしまうと起きる。
    /// 「荷重モードにしたのに効かない」の典型なので、気づけるようにしておく。
    /// </summary>
    private string RestOutsideWarning()
    {
        double lo = Math.Min(_mapper.PressureEngageRatio, _mapper.PressureFullRatio);
        double hi = Math.Max(_mapper.PressureEngageRatio, _mapper.PressureFullRatio);
        return lo <= 1.0 && 1.0 <= hi
            ? string.Empty
            : "\n[!] 安静時(1.00)が作動〜全開の外です。\n    倍率が張り付いて効きません。";
    }

    /// <summary>
    /// 今の4つの値が結局どう振る舞うのかを、1行の日本語にする。
    ///
    /// 作動/振り切りは荷重比、作動側/振り切り側は速度の倍率で、組み合わせの意味が頭の中でしか
    /// 繋がらない。「浮かせると 100% → 25%」と書けば、触る前に分かる。
    /// </summary>
    private string DescribeLoadEffect()
    {
        if (_mapper.Pressure == PressureMode.Off)
        {
            return string.Empty;
        }

        string action = _mapper.PressureEngagesWhenLighter ? "浮かせる" : "踏み込む";
        if (_mapper.Pressure == PressureMode.Clutch)
        {
            return $"{action}と動く (はっきり切り替え)";
        }

        double a = _mapper.LoadFactorAtEngage;
        double b = _mapper.LoadFactorAtFull;
        string direction = b > a ? "速くなる" : b < a ? "遅くなる" : "変わらない";
        return $"{action}と {a:P0} → {b:P0} ({direction})";
    }

    /// <summary>
    /// 送り方についての一言。相対モードのときだけ、Windows 側の設定が効いていることを知らせる。
    ///
    /// Raw Input を読むゲーム (相対モードで狙っている相手そのもの) には加速がかからないので、
    /// これは「効かない」ではなく「効く相手と効かない相手がいる」という警告。倍率が思ったのと
    /// 違うときに、こちらのつまみではなく Windows の設定を疑えるだけの情報を出しておく。
    /// </summary>
    private string PointerModeNote()
    {
        if (_output.Mode != PointerMode.Relative)
        {
            return string.Empty;
        }

        string speed = _pointerSettings.SpeedSlider == 10
            ? string.Empty
            : $"\n    速度スライダー {_pointerSettings.SpeedSlider}/20 が倍率に乗ります。";
        string epp = _pointerSettings.EnhancePointerPrecision
            ? "\n[!] 「ポインターの精度を高める」が入っています。\n    OSカーソル経由のゲームでは曲線が濁ります\n    (Raw Input のゲームには影響しません)。"
            : string.Empty;
        return epp + speed;
    }

    private static Label MakeLabel() => new() { AutoSize = true, Width = 130, ForeColor = Color.FromArgb(180, 188, 200) };

    private static Control Stack(Control top, Control bottom)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 8, 0) };
        stack.Controls.Add(top);
        stack.Controls.Add(bottom);
        return stack;
    }

    /// <summary>
    /// Windows 側のポインタ設定。相対モードのときだけ効いてくるので、モードを触ったときに
    /// 読み直す。毎フレーム読む種類の値ではない (本人が設定を開いて変えたときにしか動かない)。
    /// </summary>
    private PointerSettings.Values _pointerSettings = PointerSettings.Read();

    private void Apply()
    {
        _output.Mode = _pointerModeCombo.SelectedIndex == 1 ? PointerMode.Relative : PointerMode.Absolute;
        _output.RelativeGain = _relativeGainBar.Value / 100.0;
        _mapper.Curve.Deadzone = _deadzoneBar.Value / 100.0;
        _mapper.Curve.Exponent = _exponentBar.Value / 100.0;
        _mapper.Curve.MaxSpeedPxPerSec = _speedBar.Value * 100.0;
        _mapper.InvertY = _invertY.Checked;
        _mapper.Pressure = (PressureMode)_pressureCombo.SelectedIndex;
        _mapper.PressureEngageRatio = _engageBar.Value / 100.0;
        // 全開側は作動側より小さくてよい (軽くするほど速い向き)。同値だけは避ける。
        double full = _fullBar.Value / 100.0;
        double engage = _engageBar.Value / 100.0;
        _mapper.LoadSmoothingMs = _loadSmoothBar.Value;
        _mapper.LoadExponent = _loadExponentBar.Value / 100.0;
        _mapper.LoadFactorAtEngage = _loadAtEngageBar.Value / 100.0;
        _mapper.LoadFactorAtFull = _loadAtFullBar.Value / 100.0;
        _mapper.PressureFullRatio = Math.Abs(full - engage) < 0.02
            ? (full >= engage ? engage + 0.02 : engage - 0.02)
            : full;

        bool relative = _output.Mode == PointerMode.Relative;
        if (relative)
        {
            _pointerSettings = PointerSettings.Read();
        }
        _relativeGainLabel.Text = $"ゲームでの倍率 : {_output.RelativeGain:F2}x";

        // 絶対座標モードでは倍率を使わない。灰色にして残すのではなく、畳む。
        // このパネルは既に埋まっていて、常時見えている「効かないつまみ」1つぶんの高さが、
        // 下の段 (荷重のつまみ) を折り返しの外へ押し出す。要るときにだけ場所を取ればいい。
        _relativeGainLabel.Visible = relative;
        _relativeGainBar.Visible = relative;

        _deadzoneLabel.Text = $"デッドゾーン : {_mapper.Curve.Deadzone:F2}";
        _exponentLabel.Text = $"指数 : {_mapper.Curve.Exponent:F2}";
        _speedLabel.Text = $"最大速度 : {_mapper.Curve.MaxSpeedPxPerSec:F0} px/s";
        _engageLabel.Text = $"荷重 作動 : {_mapper.PressureEngageRatio:F2} 倍"
                          + (_mapper.Pressure == PressureMode.Off ? string.Empty
                             : _mapper.PressureEngagesWhenLighter ? " (浮かせる)" : " (踏み込む)");
        _fullLabel.Text = $"荷重 振り切り : {_mapper.PressureFullRatio:F2} 倍";
        _loadSmoothLabel.Text = _mapper.LoadSmoothingMs < 1
            ? "荷重の平滑 : なし"
            : $"荷重の平滑 : {_mapper.LoadSmoothingMs:F0} ms";
        _loadExponentLabel.Text = $"荷重の指数 : {_mapper.LoadExponent:F2}";
        _loadAtEngageLabel.Text = $"作動側の速度 : {_mapper.LoadFactorAtEngage:P0}";
        _loadAtFullLabel.Text = $"振り切り側 : {_mapper.LoadFactorAtFull:P0}";
        bool usesPressure = _mapper.Pressure != PressureMode.Off;
        _engageBar.Enabled = usesPressure;
        _loadSmoothBar.Enabled = usesPressure;
        _loadExponentBar.Enabled = _mapper.Pressure == PressureMode.Throttle;
        _loadAtEngageBar.Enabled = _mapper.Pressure == PressureMode.Throttle;
        _loadAtFullBar.Enabled = _mapper.Pressure == PressureMode.Throttle;
        _loadRangeButton.Enabled = usesPressure;
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
        _loadSmoothBar.Value = Clamp(_loadSmoothBar, (int)Math.Round(settings.LoadSmoothingMs));
        _loadExponentBar.Value = Clamp(_loadExponentBar, (int)Math.Round(settings.LoadExponent * 100));
        _loadAtEngageBar.Value = Clamp(_loadAtEngageBar, (int)Math.Round(settings.LoadFactorAtEngage * 100));
        _loadAtFullBar.Value = Clamp(_loadAtFullBar, (int)Math.Round(settings.LoadFactorAtFull * 100));
        _engageBar.Value = Clamp(_engageBar, (int)Math.Round(settings.PressureEngageRatio * 100));
        _fullBar.Value = Clamp(_fullBar, (int)Math.Round(settings.PressureFullRatio * 100));
        _relativeGainBar.Value = Clamp(_relativeGainBar, (int)Math.Round(settings.RelativeGain * 100));
        _pointerModeCombo.SelectedIndex = settings.PointerMode == "Relative" ? 1 : 0;
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
        settings.PointerMode = _output.Mode.ToString();
        settings.RelativeGain = _output.RelativeGain;
        settings.InvertY = _invertY.Checked;
        settings.ShareLeftRight = _shareLeftRight.Checked;
        settings.AutoCenter = _autoCenter.Checked;
        settings.PressureMode = _mapper.Pressure.ToString();
        settings.PressureEngageRatio = _mapper.PressureEngageRatio;
        settings.LoadSmoothingMs = _mapper.LoadSmoothingMs;
        settings.LoadExponent = _mapper.LoadExponent;
        settings.LoadFactorAtEngage = _mapper.LoadFactorAtEngage;
        settings.LoadFactorAtFull = _mapper.LoadFactorAtFull;
        settings.PressureFullRatio = _mapper.PressureFullRatio;

        settings.ReachIsCalibrated = _mapper.Reach.IsCalibrated;
        settings.ReachFrontMm = _mapper.Reach.FrontMm;
        settings.ReachBackMm = _mapper.Reach.BackMm;
        settings.ReachLeftMm = _mapper.Reach.LeftMm;
        settings.ReachRightMm = _mapper.Reach.RightMm;
        settings.ReferenceLoadKg = _mapper.Reach.ReferenceLoadKg;
    }

    private static int Clamp(TrackBar bar, int value) => Math.Clamp(value, bar.Minimum, bar.Maximum);

    /// <summary>
    /// 送り方を外から変える。ショートカットで切り替えたときに、コンボの表示が置いていかれない
    /// ようにするための経路。コンボに書けば SelectedIndexChanged 経由で Apply が走り、
    /// MouseOutput への反映もそこで揃う。
    /// </summary>
    public void SetPointerMode(PointerMode mode)
    {
        _pointerModeCombo.SelectedIndex = mode == PointerMode.Relative ? 1 : 0;
    }

    /// <summary>人に見せる送り先の名前。状況表示とトレイで同じ言葉を使う。</summary>
    public static string PointerModeLabel(PointerMode mode) =>
        mode == PointerMode.Relative ? "ゲーム (視点)" : "デスクトップ";

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

    public void SetLoadRangeCalibrating(bool calibrating)
    {
        _loadRangeButton.Enabled = !calibrating;
        _loadRangeButton.Text = calibrating ? "測定中..." : "荷重の範囲を測る (8秒)";
    }

    /// <summary>測定結果を UI に反映する。</summary>
    public void SetPressureThresholds(double engage, double full)
    {
        _engageBar.Value = Clamp(_engageBar, (int)Math.Round(engage * 100));
        _fullBar.Value = Clamp(_fullBar, (int)Math.Round(full * 100));
        Apply();
    }

    /// <summary>UIタイマーから毎回呼ぶ。</summary>
    public void UpdateLive(PointerCommand command, bool outputEnabled)
    {
        var reach = _mapper.Reach;
        string reachText = reach.IsSampling
            ? $"可動域 測定中 ({reach.SampleCount})\n"
              + $"前{reach.PreviewFrontMm,4:F0} 後{reach.PreviewBackMm,4:F0} 左{reach.PreviewLeftMm,4:F0} 右{reach.PreviewRightMm,4:F0} mm"
            : $"可動域 {(reach.IsCalibrated ? $"実測 (基準 {reach.ReferenceLoadKg:F1}kg)" : "既定値 (未測定)")}\n"
              + $"前{reach.FrontMm,4:F0} 後{reach.BackMm,4:F0} 左{reach.LeftMm,4:F0} 右{reach.RightMm,4:F0} mm";

        var range = _mapper.RecentRatioRange();
        double speed = Math.Sqrt(
            command.VelocityXPxPerSec * command.VelocityXPxPerSec +
            command.VelocityYPxPerSec * command.VelocityYPxPerSec);

        string state = !command.Active ? "停止 (乗っていない/荷重不足)"
            : command.Engaged ? $"{speed:F0} px/s"
            : command.PressureFactor <= 0 ? "荷重待ち"
            : "デッドゾーン内";

        // 荷重を使っているなら、今の比と直近で実際に出ていた範囲を並べる。閾値をどこに置けるかは
        // この範囲で決まるので、両方を同時に見せないと判断できない。
        string pressure = _mapper.Pressure == PressureMode.Off
            ? string.Empty
            : _mapper.PressureIsAvailable
                ? $"\n荷重 {command.PressureRatio:F2} 倍 (直近 {range.Min:F2}〜{range.Max:F2})"
                  + $"\n倍率 {command.PressureFactor:F2}"
                  + $"\n{DescribeLoadEffect()}"
                  + RestOutsideWarning()
                : "\n[!] 荷重モードは無効 ([重心の原点] が必要)";

        StatusText = reachText
                   + $"\n半径 {command.NormalizedRadius:F2}  {state}"
                   + $"\n出力 {(outputEnabled ? "ON" : "OFF")}  送り先 {PointerModeLabel(_output.Mode)}"
                   + PointerModeNote()
                   + pressure;

        _curveView.SetPosition(command.NormalizedRadius, command.Engaged, command.PressureFactor);
        _curveView.Invalidate();
    }
}
