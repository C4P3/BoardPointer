using System.Diagnostics;
using BoardPointer.Core.Bluetooth;
using BoardPointer.Core.Mapping;
using BoardPointer.Core.Pipeline;
using BoardPointer.Core.Recording;
using BoardPointer.Core.Sampling;

namespace BoardPointer.Viewer;

/// <summary>
/// 重心を見ながら、信号のつまみ (フィルタ) と操作のつまみ (マウス) を触るための窓。
///
/// レート制御は閉ループ --- 足の動きは見えているカーソルへの反応なので、記録を流し直しても
/// 操作感は再現できない。だからマウスのつまみは「触りながら決める」しかなく、その場で反映される
/// ことと、曲線のどこを自分が使っているかが同時に見えることが要件になる。
/// Replay で裏を取れるのは、静止時にカーソルが流れないか・最大速度に届くか・低速域が
/// 死んでいないか、といった閉ループに依らない部分だけ。
/// </summary>
public sealed class MainForm : Form
{
    private readonly BoardView _boardView = new() { Dock = DockStyle.Fill };
    private readonly ReadoutView _readout = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 24,
        ForeColor = Color.FromArgb(160, 168, 180),
        Padding = new Padding(10, 4, 4, 4),
        Text = "未接続",
    };

    private readonly Button _connectButton = MakeButton("接続");
    private readonly Button _pairButton = MakeButton("ペアリング (SYNC)");
    private readonly Button _syntheticButton = MakeButton("合成データ");
    private readonly Button _replayButton = MakeButton("CSVを再生...");
    private readonly Button _stopButton = MakeButton("停止");
    private readonly Button _tareButton = MakeButton("荷重ゼロ点 (降りて3秒)");
    private readonly Button _centerButton = MakeButton("重心の原点 (3秒)");
    private readonly ComboBox _modeCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 130,
        Margin = new Padding(12, 9, 4, 6),
    };
    private readonly Button _recordButton = MakeButton("記録開始");

    private readonly TrackBar _minCutoffBar = new() { Minimum = 1, Maximum = 500, Value = 100, TickStyle = TickStyle.None, Width = 190 };
    private readonly TrackBar _betaBar = new() { Minimum = 0, Maximum = 200, Value = 5, TickStyle = TickStyle.None, Width = 190 };
    private readonly TrackBar _trailBar = new() { Minimum = 20, Maximum = 400, Value = 200, TickStyle = TickStyle.None, Width = 190 };
    private readonly CheckBox _filterCheck = new() { Text = "フィルタ", Checked = true, AutoSize = true };
    private readonly CheckBox _tareCheck = new() { Text = "ゼロ点補正", Checked = true, AutoSize = true };
    private readonly Label _minCutoffLabel = MakeSliderLabel();
    private readonly Label _betaLabel = MakeSliderLabel();
    private readonly Label _trailLabel = MakeSliderLabel();

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 33 };

    // レート表示は「タイマーの公称間隔」ではなく実測の経過時間で割る。WinForms のタイマーは
    // Windows の既定のタイマー分解能 (約15.6ms) に丸められるので、33ms 指定でも実際は 31ms や
    // 47ms で発火する。公称値で割ると 100Hz のソースが 145Hz と表示されてしまう。
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();
    private readonly PipelineOptions _options = new();

    private BoardPipeline _pipeline;
    private ISampleSource? _source;
    private RawCsvWriter? _recorder;
    private CancellationTokenSource? _pairingCts;

    private readonly PointerMapper _mapper = new();
    private readonly MouseOutput _mouseOutput = new();
    private MappingPanel _mappingPanel = null!;

    private long _tareDeadlineMs = -1;
    private long _centerDeadlineMs = -1;
    private long _reachDeadlineMs = -1;
    private long _lastMappedTimestampMs = -1;
    private volatile bool _reachJustFinished;
    private volatile bool _mouseEnabled;
    private PointerCommand _latestCommand;
    private volatile bool _tareJustFinished;
    private volatile bool _centerJustFinished;
    private int _samplesSinceTick;
    private double _sampleRateHz;
    private BoardFrame _latest;
    private volatile bool _hasFrame;

    private readonly bool _autoSynthetic;
    private readonly string? _autoReplayPath;

    public MainForm(bool autoSynthetic = false, string? autoReplayPath = null, bool seated = false)
    {
        _autoSynthetic = autoSynthetic;
        _autoReplayPath = autoReplayPath;
        Text = "BoardPointer Viewer — 重心の可視化と記録";
        ClientSize = new Size(1120, 800);
        BackColor = Color.FromArgb(32, 35, 40);
        ForeColor = Color.FromArgb(220, 225, 232);
        StartPosition = FormStartPosition.CenterScreen;

        _pipeline = new BoardPipeline(_options);

        Controls.Add(_boardView);
        Controls.Add(BuildSidePanel());
        Controls.Add(BuildBottomTabs());
        Controls.Add(BuildToolbar());
        Controls.Add(_status);

        _connectButton.Click += (_, _) => ConnectLive();
        _pairButton.Click += (_, _) => TogglePairing();
        _syntheticButton.Click += (_, _) => StartSource(ReplaySource.Synthetic(60), loop: true);
        _replayButton.Click += (_, _) => OpenCsv();
        _stopButton.Click += (_, _) => StopSource("停止しました");
        _tareButton.Click += (_, _) => BeginTare();
        _centerButton.Click += (_, _) => BeginCentering();

        _modeCombo.Items.AddRange(["立ち", "座り・足先"]);
        _modeCombo.SelectedIndexChanged += (_, _) => ApplyPreset();
        _modeCombo.SelectedIndex = seated ? 1 : 0;
        _recordButton.Click += (_, _) => ToggleRecording();

        _minCutoffBar.ValueChanged += (_, _) => ApplyOptions();
        _betaBar.ValueChanged += (_, _) => ApplyOptions();
        _trailBar.ValueChanged += (_, _) => ApplyOptions();
        _filterCheck.CheckedChanged += (_, _) => ApplyOptions();
        _tareCheck.CheckedChanged += (_, _) => ApplyOptions();
        ApplyOptions();

        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 30,
        FlatStyle = FlatStyle.System,
        Margin = new Padding(4, 6, 4, 6),
    };

    private static Label MakeSliderLabel() => new() { AutoSize = true, Width = 190, ForeColor = Color.FromArgb(180, 188, 200) };

    private Control BuildToolbar()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 4, 6, 4), AutoSize = false };
        panel.Controls.AddRange([_connectButton, _pairButton, _syntheticButton, _replayButton, _stopButton, _modeCombo, _tareButton, _centerButton, _recordButton]);
        return panel;
    }

    private Control BuildSidePanel()
    {
        var panel = new Panel { Dock = DockStyle.Right, Width = 290, BackColor = Color.FromArgb(27, 29, 34) };
        panel.Controls.Add(_readout);
        return panel;
    }

    /// <summary>
    /// 下段はタブ2枚。信号のつまみ (フィルタ) と操作のつまみ (マウス) は詰める局面が別なので、
    /// 同時に出すと窓が狭くなるだけで、どちらも触りにくくなる。
    /// </summary>
    private Control BuildBottomTabs()
    {
        _mappingPanel = new MappingPanel(_mapper) { Dock = DockStyle.Fill };
        _mappingPanel.OutputToggleRequested += ToggleMouseOutput;
        _mappingPanel.CalibrationRequested += BeginReachCalibration;

        var signalTab = new TabPage("フィルタ") { BackColor = Color.FromArgb(27, 29, 34) };
        signalTab.Controls.Add(BuildFilterPanel());

        var mouseTab = new TabPage("マウス") { BackColor = Color.FromArgb(27, 29, 34) };
        mouseTab.Controls.Add(_mappingPanel);

        var tabs = new TabControl { Dock = DockStyle.Bottom, Height = 262 };
        tabs.TabPages.Add(mouseTab);
        tabs.TabPages.Add(signalTab);
        return tabs;
    }

    private Control BuildFilterPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 6, 6),
            BackColor = Color.FromArgb(27, 29, 34),
            FlowDirection = FlowDirection.LeftToRight,
        };

        panel.Controls.Add(Stack(_minCutoffLabel, _minCutoffBar));
        panel.Controls.Add(Stack(_betaLabel, _betaBar));
        panel.Controls.Add(Stack(_trailLabel, _trailBar));

        var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(16, 12, 0, 0) };
        checks.Controls.AddRange([_filterCheck, _tareCheck]);
        panel.Controls.Add(checks);

        var hint = new Label
        {
            AutoSize = false,
            Width = 250,
            Height = 76,
            Margin = new Padding(20, 8, 0, 0),
            ForeColor = Color.FromArgb(140, 148, 160),
            Text = "白い輪 = 生の重心、水色の点 = フィルタ後。\n"
                 + "min-cutoff を下げると静止時が静かになり、\n"
                 + "beta を上げると速い動きの遅れが減る。\n"
                 + "2つの点の離れ方が、そのまま遅れの量。",
        };
        panel.Controls.Add(hint);
        return panel;
    }

    private static Control Stack(Control top, Control bottom)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 8, 14, 0) };
        stack.Controls.Add(top);
        stack.Controls.Add(bottom);
        return stack;
    }

    /// <summary>
    /// 姿勢のプリセットを流し込む。在席の閾値と重心の最低荷重は姿勢で桁が変わる --- 立位なら
    /// 体重ぶん (数十kg) が載るが、椅子に座って足先で操作する場合に載るのは脚の重さ (実測で
    /// 12〜15kg) で、操作そのものはその上の数kgの移動として出る。立位の閾値のままだと、脚の
    /// 重さがちょうど閾値付近をうろついて在席判定がちらつく。
    /// </summary>
    private void ApplyPreset()
    {
        var preset = _modeCombo.SelectedIndex == 1 ? PipelineOptions.SeatedFoot() : PipelineOptions.Standing();
        _options.CopyFrom(preset);

        // スライダーはプリセットの値に追従させる (ValueChanged から ApplyOptions が走る)。
        _minCutoffBar.Value = Math.Clamp((int)Math.Round(preset.MinCutoffHz * 100), _minCutoffBar.Minimum, _minCutoffBar.Maximum);
        ApplyOptions();

        _status.Text = _modeCombo.SelectedIndex == 1
            ? "座り・足先モード。脚の重さが載りっぱなしなので、荷重ゼロ点は「足を降ろして」測ること。中立姿勢を消すのは [重心の原点] のほう。"
            : "立ちモード。";
    }

    private void ApplyOptions()
    {
        _options.MinCutoffHz = _minCutoffBar.Value / 100.0;
        _options.Beta = _betaBar.Value / 100000.0;
        _options.FilterEnabled = _filterCheck.Checked;
        _options.TareEnabled = _tareCheck.Checked;
        _boardView.TrailLength = _trailBar.Value;

        _minCutoffLabel.Text = $"min-cutoff : {_options.MinCutoffHz:F2} Hz";
        _betaLabel.Text = $"beta : {_options.Beta:F5}";
        _trailLabel.Text = $"軌跡の長さ : {_trailBar.Value}";
    }

    // ---- ソースの切り替え ----

    private void ConnectLive()
    {
        var source = LiveBoardSource.TryOpen();
        if (source is null)
        {
            _status.Text = "ボードが見つかりません。電源が入っていないか、まだペアリングされていません。[ペアリング (SYNC)] を試してください。";
            return;
        }

        _status.Text = "接続中... (工場較正の読み出しを待っています)";
        Enabled = false;
        Task.Run(() =>
        {
            try
            {
                StartSourceCore(source, loop: false);
                BeginInvoke(() =>
                {
                    Enabled = true;
                    _status.Text = source.CalibrationIsFromStore
                        ? $"接続しました。今回は工場較正を読めなかったので、保存しておいた値を使います（原因: {source.CalibrationDiagnostics ?? "不明"}）。"
                        : source.Calibration is null
                            ? $"接続しましたが工場較正が無く、保存された値もありません。公称ゲインで換算するので、ボードから降りて [荷重ゼロ点] を押してください（原因: {source.CalibrationDiagnostics ?? "不明"}）。"
                            : "接続しました。工場較正を読めています (値は kg)。";
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    Enabled = true;
                    _status.Text = $"接続に失敗しました: {ex.Message}";
                });
            }
        });
    }

    private void TogglePairing()
    {
        if (_pairingCts is not null)
        {
            _pairingCts.Cancel();
            return;
        }

        _pairingCts = new CancellationTokenSource();
        _pairButton.Text = "ペアリング中止";
        _status.Text = "ボードのSYNCボタン (電池蓋の中) を押してください。見つかるまで探し続けます。";

        var token = _pairingCts.Token;
        Task.Run(() => BalanceBoardPairing.PairAndInstall(cancellationToken: token)).ContinueWith(task =>
        {
            BeginInvoke(() =>
            {
                _pairingCts?.Dispose();
                _pairingCts = null;
                _pairButton.Text = "ペアリング (SYNC)";

                var outcome = task.Result;
                _status.Text = outcome.Result switch
                {
                    PairingResult.Success => $"ペアリングしました ({outcome.DeviceAddress})。[接続] を押してください。",
                    PairingResult.Cancelled => "ペアリングを中止しました。",
                    _ => $"ペアリングに失敗しました: {outcome.Message}",
                };
            });
        });
    }

    private void OpenCsv()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "生CSV (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            Title = "記録した生CSVを選ぶ",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var source = ReplaySource.FromCsv(dialog.FileName);
            if (source.SampleCount == 0)
            {
                _status.Text = "サンプルが1つも読めませんでした。";
                return;
            }
            StartSource(source, loop: true);
        }
        catch (Exception ex)
        {
            _status.Text = $"読み込みに失敗しました: {ex.Message}";
        }
    }

    private void StartSource(ReplaySource source, bool loop)
    {
        source.Loop = loop;
        StartSourceCore(source, loop);
        _status.Text = $"{source.Description} を再生しています ({source.SampleCount} サンプル)。";
    }

    private void StartSourceCore(ISampleSource source, bool loop)
    {
        StopSource(null);

        _pipeline = new BoardPipeline(_options, source.Calibration);
        _boardView.ClearTrail();
        _hasFrame = false;
        _tareDeadlineMs = -1;

        source.SampleReceived += OnSample;
        source.Ended += OnEnded;
        _source = source;
        source.Start();
    }

    private void StopSource(string? message)
    {
        StopRecording();
        if (_mouseEnabled)
        {
            _mouseEnabled = false;
            _mappingPanel.SetOutputEnabled(false);
        }
        _mouseOutput.Reset();
        _mapper.Reset();
        _lastMappedTimestampMs = -1;
        if (_source is not null)
        {
            _source.SampleReceived -= OnSample;
            _source.Ended -= OnEnded;
            _source.Dispose();
            _source = null;
        }
        _hasFrame = false;
        _boardView.ClearTrail();
        if (message is not null)
        {
            _status.Text = message;
        }
    }

    private void OnEnded(string reason) => BeginInvoke(() => StopSource($"ソースが終了しました: {reason}"));

    // ---- サンプル処理 (サンプルスレッド) ----

    private void OnSample(RawSample sample)
    {
        var pipeline = _pipeline;

        // ゼロ点サンプリングの締め切り管理。記録の時計で測るので、リプレイでも同じ挙動になる。
        if (pipeline.Tare.IsSampling)
        {
            if (_tareDeadlineMs < 0)
            {
                _tareDeadlineMs = sample.TimestampMs + 3000;
            }
            else if (sample.TimestampMs >= _tareDeadlineMs)
            {
                pipeline.Tare.FinishSampling();
                _tareDeadlineMs = -1;
                _tareJustFinished = true;
            }
        }

        if (pipeline.Centering.IsSampling)
        {
            if (_centerDeadlineMs < 0)
            {
                _centerDeadlineMs = sample.TimestampMs + 3000;
            }
            else if (sample.TimestampMs >= _centerDeadlineMs)
            {
                pipeline.Centering.FinishSampling();
                _centerDeadlineMs = -1;
                _centerJustFinished = true;
            }
        }

        var frame = pipeline.Process(sample);
        _latest = frame;
        _hasFrame = true;
        Interlocked.Increment(ref _samplesSinceTick);

        // 可動域の測定。締め切りは記録側の時計で測るので、リプレイでも同じ挙動になる。
        if (_mapper.Reach.IsSampling)
        {
            _mapper.Reach.Feed(frame.CopXFilteredMm, frame.CopYFilteredMm, frame.TotalKg, frame.CopValid);
            if (_reachDeadlineMs < 0)
            {
                _reachDeadlineMs = sample.TimestampMs + ReachCalibrationMs;
            }
            else if (sample.TimestampMs >= _reachDeadlineMs)
            {
                _mapper.Reach.FinishSampling(_mappingPanel.ShareLeftRight);
                _reachDeadlineMs = -1;
                _reachJustFinished = true;
            }
        }

        // 踏み込みの基準は、重心の原点を測ったとき (力を抜いた姿勢) の荷重が本筋。無ければ
        // 可動域測定時の値で代用し、どちらも無ければ踏み込みは効かない (0 のまま)。
        _mapper.ReferenceLoadKg = _pipeline.Centering.RestingLoadKg > 0.1
            ? _pipeline.Centering.RestingLoadKg
            : _mapper.Reach.ReferenceLoadKg;

        var command = _mapper.Update(frame);
        _latestCommand = command;

        double dt = _lastMappedTimestampMs < 0 ? 0.01 : (sample.TimestampMs - _lastMappedTimestampMs) / 1000.0;
        _lastMappedTimestampMs = sample.TimestampMs;

        if (_mouseEnabled && command.Active)
        {
            _mouseOutput.Apply(command.VelocityXPxPerSec, command.VelocityYPxPerSec, dt);
        }
        else if (!command.Active)
        {
            // 足を離した瞬間に端数が残っていると、次に乗ったときに1px飛ぶ。
            _mouseOutput.Reset();
        }

        // 原点の自動追従。操作していない間 (デッドゾーン内、あるいは踏み込みが閾値未満) だけ動かす。
        // レート制御では原点のずれがそのまま恒常的なカーソルの流れになるので、座り続けて足の中立
        // 位置が動いたときに効く。操作中に動かすと狙いが逃げるので、条件は必ず Engaged で見る。
        if (_mappingPanel.AutoCenter && command.Active && !command.Engaged)
        {
            _pipeline.Centering.Follow(
                frame.CopXMm + _pipeline.Centering.OriginXMm,
                frame.CopYMm + _pipeline.Centering.OriginYMm,
                dt,
                AutoCenterTimeConstantSeconds);
        }

        _boardView.Push(frame);
        _recorder?.Write(sample);
    }

    private const int ReachCalibrationMs = 12000;

    /// <summary>
    /// 原点の自動追従の時定数 [秒]。意図的なゆっくりした操作と競合しない程度に長く取る。
    /// 短くすると狙っている途中で原点が逃げ、長すぎると姿勢の変化についていけない。
    /// </summary>
    private const double AutoCenterTimeConstantSeconds = 20.0;

    /// <summary>
    /// マウス出力の入り切り。押して有効にすると、以降このアプリがカーソルを動かす --- つまり
    /// この窓のボタンを押しに行くのが難しくなる。F9 をグローバルホットキーとして登録してあるのは
    /// そのためで、カーソルが暴れても必ず止められる。
    /// </summary>
    private void ToggleMouseOutput()
    {
        if (!_mouseEnabled && _source is null)
        {
            _status.Text = "先にソースを繋いでください。";
            return;
        }

        _mouseEnabled = !_mouseEnabled;
        _mouseOutput.Reset();
        _mappingPanel.SetOutputEnabled(_mouseEnabled);
        _status.Text = _mouseEnabled
            ? "マウス出力を開始しました。止めるときは F9 (この窓が後ろにいても効きます)。"
            : "マウス出力を停止しました。";
    }

    private void BeginReachCalibration()
    {
        if (_source is null)
        {
            _status.Text = "先にソースを繋いでください。";
            return;
        }

        _reachDeadlineMs = -1;
        _reachJustFinished = false;
        _mapper.Reach.BeginSampling();
        _mappingPanel.SetCalibrating(true);
        _status.Text = $"可動域を測っています ({ReachCalibrationMs / 1000}秒)。"
                     + "足先で大きく円を描くように、前後左右いっぱいまで動かしてください。";
    }

    private void BeginTare()
    {
        if (_source is null)
        {
            _status.Text = "先にソースを繋いでください。";
            return;
        }
        _tareDeadlineMs = -1;
        _tareJustFinished = false;
        _pipeline.Tare.BeginSampling();
        _status.Text = "荷重のゼロ点を測っています。ボードから完全に降りて3秒待ってください。";
    }

    /// <summary>
    /// 重心の原点合わせ。荷重のゼロ点とは目的が違う。
    ///
    /// 荷重のゼロ点は「ボードに何も載っていない状態」で測って、床の傾きや脚の高さのばらつきを消す。
    /// 重心の原点は「操作する姿勢のまま力を抜いた状態」で測って、その人の中立姿勢を消す。
    /// 前者を足を載せたまま測ると、足の重さごと差し引かれて合計荷重がほぼ0になり、重心は合計で
    /// 割る比なので端まで振り切れる。中立姿勢を消したいときに押すのはこちら。
    /// </summary>
    private void BeginCentering()
    {
        if (_source is null)
        {
            _status.Text = "先にソースを繋いでください。";
            return;
        }
        _centerDeadlineMs = -1;
        _centerJustFinished = false;
        _pipeline.Centering.BeginSampling();
        _status.Text = "重心の原点を測っています。操作する姿勢のまま、力を抜いて3秒動かないでください。";
    }

    // ---- 記録 ----

    private void ToggleRecording()
    {
        if (_recorder is not null)
        {
            string path = _recorder.Path;
            int count = _recorder.SamplesWritten;
            StopRecording();
            _status.Text = $"{path} に {count} サンプルを記録しました。";
            return;
        }

        if (_source is null)
        {
            _status.Text = "先にソースを繋いでください。";
            return;
        }

        string folder = Path.Combine(AppContext.BaseDirectory, "debug");
        string file = Path.Combine(folder, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var header = new RecordingHeader(DateTimeOffset.Now, _source.Description, string.Empty, _source.Calibration);
        _recorder = new RawCsvWriter(file, header);
        _recordButton.Text = "記録停止";
        _status.Text = $"{file} に記録しています。";
    }

    private void StopRecording()
    {
        _recorder?.Dispose();
        _recorder = null;
        _recordButton.Text = "記録開始";
    }

    // ---- 表示 ----

    private void OnUiTick(object? sender, EventArgs e)
    {
        int samples = Interlocked.Exchange(ref _samplesSinceTick, 0);
        double elapsedSeconds = _tickClock.Elapsed.TotalSeconds;
        _tickClock.Restart();
        if (elapsedSeconds > 0)
        {
            _sampleRateHz = _sampleRateHz * 0.8 + (samples / elapsedSeconds) * 0.2;
        }

        if (_tareJustFinished)
        {
            _tareJustFinished = false;
            double onBoard = _pipeline.Tare.SampledTotalMedian;
            // 空のボードで測れていないと、そのゼロ点は足の重さごと引いてしまう。黙って進めない。
            // 閾値も表示も単位に合わせる --- 生カウントモードでは空のボードでも合計が6万前後あるので、
            // kg 用の 3.0 を当てると「62736 kg 載っています」のような無意味な警告になる。
            if (!_pipeline.CanCheckEmptyBoard)
            {
                // 生カウントには原点が無いので、空だったかどうかを判定できない。
                _status.Text = "荷重のゼロ点を取りました。ただし工場較正が読めていないため、ボードが空だったかは判定できません。"
                             + "[停止] → [接続] で較正を読み直すことをおすすめします。";
            }
            else
            {
                _status.Text = onBoard > _pipeline.EmptyBoardThresholdKg
                    ? $"[!] 測定中に {onBoard:F1} kg 載っていました。ボードから降りて測り直してください。中立姿勢を消したいなら [重心の原点] のほうです。"
                    : $"荷重のゼロ点を取りました (残り {onBoard:F2} kg)。";
            }
        }
        if (_centerJustFinished)
        {
            _centerJustFinished = false;
            _status.Text = $"重心の原点を取りました (X {_pipeline.Centering.OriginXMm:F1} / Y {_pipeline.Centering.OriginYMm:F1} mm)。";
        }

        if (_reachJustFinished)
        {
            _reachJustFinished = false;
            var r = _mapper.Reach;
            _mappingPanel.SetCalibrating(false);
            _status.Text = $"可動域を測りました: 前 {r.FrontMm:F0} / 後 {r.BackMm:F0} / 左 {r.LeftMm:F0} / 右 {r.RightMm:F0} mm。";
        }

        _mappingPanel.UpdateLive(_latestCommand, _mouseEnabled);
        _boardView.Invalidate();
        _readout.SetText(BuildReadout());
    }

    private string BuildReadout()
    {
        if (_source is null)
        {
            return "ソース : (未接続)\n\n"
                 + "[接続]       ペアリング済みのボードを開く\n"
                 + "[ペアリング] SYNCボタンを押しながら\n"
                 + "[合成データ] 実機なしで動作を確認\n"
                 + "[CSVを再生]  記録した生CSVを流し直す\n\n"
                 + "記録したCSVは BoardPointer.Replay に\n"
                 + "渡すと統計が出ます。";
        }

        var f = _latest;
        string Load(double value) => value.ToString("F2").PadLeft(8);
        string tare = _pipeline.Tare.IsSampling
            ? $"測定中 ({_pipeline.Tare.SampleCount})"
            : _pipeline.Tare.IsCalibrated ? "済" : "未";
        string center = _pipeline.Centering.IsSampling
            ? $"測定中 ({_pipeline.Centering.SampleCount})"
            : _pipeline.Centering.IsCalibrated
                ? $"X {_pipeline.Centering.OriginXMm:F0} Y {_pipeline.Centering.OriginYMm:F0}"
                : "未";

        if (!_hasFrame)
        {
            return $"ソース : {_source.Description}\nサンプル待ち...";
        }

        string lowLoadWarning = f.CopValid
            ? string.Empty
            : "[!] 荷重不足。重心は直前の値で止めています";

        // 重心の分解能が荒いのは、ほぼ必ず「合計荷重が小さすぎる」ことが原因。生値が整数である
        // こと自体ではなく、その整数を小さな合計で割っていることが効く。
        string resolutionWarning = f.CopResolutionMm <= 1.0
            ? string.Empty
            : $"[!] 重心の刻みが {f.CopResolutionMm:F1}mm あります。\n    合計荷重が小さすぎます。荷重ゼロ点を\n    足を載せたまま取っていませんか。";

        // 工場較正が読めていないときは公称ゲインで kg に換算するが、その原点は荷重ゼロ点が
        // 与える。ゼロ点を取るまでは荷重が確定しないので、在席判定も重心も止めてある。
        string calibrationWarning = f.LoadIsCalibrated
            ? string.Empty
            : "[!] 荷重ゼロ点が未測定です。\n    工場較正が読めていないので、ゼロ点が\n    無いと荷重が確定しません。ボードから\n    降りて [荷重ゼロ点] を押してください。";

        return $"""
            ソース   : {_source.Description}
            較正     : {(f.UsesFactoryCalibration ? "工場較正 (kg)" : "公称ゲイン (推定kg)")}
            レート   : {_sampleRateHz,6:F1} Hz
            在席     : {(f.Present ? "乗っている" : "降りている")}
            荷重ゼロ点: {tare}
            重心の原点: {center}
            記録     : {(_recorder is null ? "停止中" : $"{_recorder.SamplesWritten} サンプル")}

            -- 荷重 [kg] --
            前左 {Load(f.TopLeftKg)}   前右 {Load(f.TopRightKg)}
            後左 {Load(f.BottomLeftKg)}   後右 {Load(f.BottomRightKg)}
            合計 {Load(f.TotalKg)}

            -- 重心 [mm] --
            生       X {f.CopXMm,7:F1}  Y {f.CopYMm,7:F1}
            フィルタ X {f.CopXFilteredMm,7:F1}  Y {f.CopYFilteredMm,7:F1}
            ずれ       {Math.Sqrt(Math.Pow(f.CopXMm - f.CopXFilteredMm, 2) + Math.Pow(f.CopYMm - f.CopYFilteredMm, 2)),7:F1} mm
            分解能     {f.CopResolutionMm,7:F2} mm/count
            {lowLoadWarning}
            {resolutionWarning}
            {calibrationWarning}
            """;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_autoReplayPath is not null && File.Exists(_autoReplayPath))
        {
            StartSource(ReplaySource.FromCsv(_autoReplayPath), loop: true);
        }
        else if (_autoSynthetic)
        {
            StartSource(ReplaySource.Synthetic(60), loop: true);
        }
    }

    // F9 をグローバルホットキーとして登録する。マウス出力中はこの窓のボタンを押しに行くのが
    // 難しくなるので、フォーカスに依らず必ず止められる経路が要る。
    private const int HotkeyId = 0xB001;
    private const int WmHotkey = 0x0312;
    private const int VkF9 = 0x78;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!RegisterHotKey(Handle, HotkeyId, 0, VkF9))
        {
            // 他のアプリに取られている場合。致命的ではないので、窓のボタンで操作してもらう。
            _status.Text = "F9 を登録できませんでした (他のアプリが使用中)。停止はこの窓のボタンから。";
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            ToggleMouseOutput();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _mouseEnabled = false;
        UnregisterHotKey(Handle, HotkeyId);
        _pairingCts?.Cancel();
        StopSource(null);
        base.OnFormClosed(e);
    }
}
