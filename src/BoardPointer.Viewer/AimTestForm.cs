using System.Drawing.Drawing2D;
using BoardPointer.Core.Mapping;
using BoardPointer.Core.Settings;
using BoardPointer.Core.Training;

namespace BoardPointer.Viewer;

/// <summary>
/// エイムテストの画面。全画面で的を出し、仮想カーソルで狙わせる。
///
/// **全画面なのは見栄えの問題ではない。** 的までの距離は画面の大きさで決まるので、窓の中で
/// やると距離が窓のサイズに依存し、別のときに測った結果と比べられなくなる。同じ理由で、
/// 的の大きさも画面ピクセルで固定してある。
///
/// カーソルは <see cref="AimTestSession"/> の中の仮想カーソルで、本物の Windows のカーソルは
/// 動かさない。テスト中に本物が飛ぶと、中断したあとに窓へ戻るのが難しくなる
/// (マウス出力中に窓のボタンを押しに行けないのと同じ問題)。Esc はどの場面でも効く。
///
/// 判定はサンプルスレッドで進む (<see cref="Feed"/>)。描画のタイマーで判定すると、維持の1秒が
/// 「UIが何回描けたか」で測られてしまう --- WinForms のタイマーは約15.6ms に丸められるので、
/// 整定時間のオーダーではそれ自体が誤差になる。
/// </summary>
public sealed class AimTestForm : Form
{
    private enum Phase
    {
        /// <summary>開始前。カーソルは動くが計測はしない。足を中立に戻してもらう時間。</summary>
        Ready,
        Running,
        Result,
    }

    /// <summary>開始までの猶予 [秒]。足を中立に戻して、カーソルが止まることを確かめるのに要る。</summary>
    private const int ReadySeconds = 4;

    private readonly Func<AimTestConditions> _conditions;
    private readonly AppSettings _settings;
    private readonly int _seed = Environment.TickCount;

    private readonly object _gate = new();
    private AimTestSession _session;
    private PointF[] _markers = [];

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly System.Diagnostics.Stopwatch _readyClock = System.Diagnostics.Stopwatch.StartNew();

    private Phase _phase = Phase.Ready;
    private Snapshot _view;

    private readonly Panel _resultPanel;
    private readonly Label _resultTitle;
    private readonly Label _resultNumbers;
    private readonly FlowLayoutPanel _findings;
    private readonly Label _savedPath;

    /// <summary>テストが1回終わった。引数は結果を書き出したCSVのパス (書けなければ null)。</summary>
    public event Action<string?>? Finished;

    /// <summary>描画のために1フレームぶん切り出した状態。ロックはここで閉じる。</summary>
    private readonly record struct Snapshot(
        double CursorX,
        double CursorY,
        AimTarget Target,
        double IndexOfDifficulty,
        bool Inside,
        double DwellProgress,
        int TrialIndex,
        int TrialCount,
        bool Inactive,
        bool Complete);

    public AimTestForm(Func<AimTestConditions> conditions, AppSettings settings, Screen screen)
    {
        _conditions = conditions;
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(18, 20, 24);
        ForeColor = Color.FromArgb(220, 225, 232);
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Text = "BoardPointer — エイムテスト";

        _session = BuildSession(_seed);

        _resultPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 24, 29), Visible = false, Padding = new Padding(40, 28, 40, 20) };
        _resultTitle = new Label { Dock = DockStyle.Top, Height = 44, Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), ForeColor = Color.FromArgb(235, 240, 248) };
        _resultNumbers = new Label
        {
            Dock = DockStyle.Left,
            Width = 520,
            Font = new Font("MS Gothic", 10.5f),
            ForeColor = Color.FromArgb(200, 208, 220),
            Padding = new Padding(0, 6, 20, 0),
        };
        _findings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 6, 10, 6),
        };
        _savedPath = new Label { Dock = DockStyle.Bottom, Height = 46, ForeColor = Color.FromArgb(140, 148, 160), Padding = new Padding(0, 6, 0, 0) };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight };
        var again = new Button { Text = "もう一度 (Enter)", AutoSize = true, Height = 30, FlatStyle = FlatStyle.System, Margin = new Padding(0, 4, 8, 4) };
        var close = new Button { Text = "閉じる (Esc)", AutoSize = true, Height = 30, FlatStyle = FlatStyle.System, Margin = new Padding(0, 4, 8, 4) };
        again.Click += (_, _) => Restart();
        close.Click += (_, _) => Close();
        buttons.Controls.AddRange([again, close]);

        _resultPanel.Controls.Add(_findings);
        _resultPanel.Controls.Add(_resultNumbers);
        _resultPanel.Controls.Add(_resultTitle);
        _resultPanel.Controls.Add(_savedPath);
        _resultPanel.Controls.Add(buttons);
        Controls.Add(_resultPanel);

        _timer.Tick += OnTick;
        _timer.Start();
    }

    private AimTestSession BuildSession(int seed)
    {
        var plan = AimTestPlan.Build(Bounds.Width, Bounds.Height, seed: seed);
        _markers = plan
            .Select(p => new PointF((float)p.Target.CenterXPx, (float)p.Target.CenterYPx))
            .Distinct()
            .ToArray();
        return new AimTestSession(plan, Bounds.Width, Bounds.Height);
    }

    /// <summary>
    /// サンプルスレッドから呼ぶ。結果画面に入っているあいだは何もしない
    /// (窓は開いたままなので、呼ぶ側に状態を持たせずに済む)。
    /// </summary>
    public void Feed(in PointerCommand command, long timestampMs)
    {
        lock (_gate)
        {
            if (_phase == Phase.Result)
            {
                return;
            }
            _session.Feed(command, timestampMs);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        bool complete;
        lock (_gate)
        {
            var s = _session;
            _view = new Snapshot(
                s.CursorXPx, s.CursorYPx, s.Current.Target, s.Current.IndexOfDifficulty,
                s.InsideTarget, s.DwellProgress, s.TrialIndex, s.TrialCount, s.InputInactive, s.IsComplete);

            if (_phase == Phase.Ready && _readyClock.Elapsed.TotalSeconds >= ReadySeconds)
            {
                _phase = Phase.Running;
                s.Start();
            }
            complete = _phase == Phase.Running && s.IsComplete;
        }

        if (complete)
        {
            ShowResult();
            return;
        }

        if (_phase != Phase.Result)
        {
            Invalidate();
        }
    }

    private void Restart()
    {
        lock (_gate)
        {
            _session = BuildSession(Environment.TickCount);
            _phase = Phase.Ready;
        }
        _readyClock.Restart();
        _resultPanel.Visible = false;
        Invalidate();
    }

    private void ShowResult()
    {
        AimTestSession session;
        lock (_gate)
        {
            _phase = Phase.Result;
            session = _session;
        }

        var score = new AimTestScore(session.Results);
        var conditions = _conditions();
        var findings = AimTestDiagnosis.Diagnose(score, conditions);

        string? path = null;
        try
        {
            path = AimTestLog.Write(
                Path.Combine(AppContext.BaseDirectory, "debug"),
                score, session.Results, conditions, session.DwellMs);
        }
        catch (Exception ex)
        {
            _savedPath.Text = $"結果を書き出せませんでした: {ex.Message}";
        }

        _resultTitle.Text = score.Trials == 0
            ? "測定できませんでした"
            : $"エイムテストの結果 — 粗合わせ {AimTestScore.Format(score.MedianFirstTouchMs, "F0", "ms")}"
              + $" / 詰め {AimTestScore.Format(score.MedianSettleMs, "F0", "ms")}";
        _resultNumbers.Text = BuildNumbers(score);

        _findings.Controls.Clear();
        // 幅は実際の置き場所から取る。画面の幅から引くと、超ワイドでは1行が長すぎて読めず、
        // 狭い画面でははみ出す。
        int cardWidth = Math.Clamp(_findings.ClientSize.Width - 36, 320, 760);
        foreach (var finding in findings)
        {
            _findings.Controls.Add(MakeFindingCard(finding, cardWidth));
        }

        if (path is not null)
        {
            _savedPath.Text = $"{path} に書き出しました。\n"
                            + "測ったときの設定がヘッダに入っているので、つまみを変えて測り直したぶんと並べて比べられます。";
        }

        RememberScore(score);
        _resultPanel.Visible = true;
        _resultPanel.BringToFront();
        Finished?.Invoke(path);
    }

    /// <summary>
    /// 前回のスコアを設定に残す。1回ぶんだけなのは、比較したい相手がほぼ必ず「つまみを触る
    /// 直前の自分」だから。履歴が要るならCSVが全部残っている。
    /// </summary>
    private void RememberScore(AimTestScore score)
    {
        if (score.Trials == 0)
        {
            return;
        }
        _settings.LastAimTestAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _settings.LastAimFirstTouchMs = score.MedianFirstTouchMs;
        _settings.LastAimSettleMs = score.MedianSettleMs;
        _settings.LastAimCompletionMs = score.MedianCompletionMs;
        _settings.LastAimReEntries = score.MeanReEntries;
        _settings.LastAimThroughput = score.ThroughputBitsPerSec;
    }

    private string BuildNumbers(AimTestScore score)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(_settings.LastAimTestAt) && _settings.LastAimFirstTouchMs > 0 && score.Trials > 0)
        {
            sb.AppendLine($"前回 ({_settings.LastAimTestAt}) との比較");
            sb.AppendLine(Compare("粗合わせ", _settings.LastAimFirstTouchMs, score.MedianFirstTouchMs, "ms", lowerIsBetter: true));
            sb.AppendLine(Compare("詰め    ", _settings.LastAimSettleMs, score.MedianSettleMs, "ms", lowerIsBetter: true));
            sb.AppendLine(Compare("入り直し", _settings.LastAimReEntries, score.MeanReEntries, "回", lowerIsBetter: true, decimals: 2));
            sb.AppendLine();
            sb.AppendLine("※ 設定を変えていなくても、慣れと疲れで動きます。");
            sb.AppendLine("  1回の差で判断せず、気になる差は測り直してください。");
            sb.AppendLine();
        }

        sb.AppendLine(AimTestDiagnosis.Summarize(score));

        static string F(double v, string fmt) => AimTestScore.Format(v, fmt);

        sb.AppendLine("大きさ別 (入れず = 的に一度も入れなかった試行)");
        sb.AppendLine("  的     試行 成功 入れず  初到達   詰め 入直り   震え");
        foreach (var s in score.BySize)
        {
            sb.AppendLine($"  {s.DiameterPx,3:F0}px   {s.Trials,3} {s.SuccessRate,4:P0} {s.NeverTouched,5}  "
                        + $"{F(s.MedianFirstTouchMs, "F0"),5}ms {F(s.MedianSettleMs, "F0"),4}ms "
                        + $"{s.MeanReEntries,5:F2} {F(s.MedianDriftPx, "F1"),5}px");
        }

        sb.AppendLine();
        sb.AppendLine("方向別 (遅い順)");
        sb.AppendLine("  向き   試行 成功 入れず  初到達   詰め  半径");
        foreach (var d in score.ByDirection)
        {
            sb.AppendLine($"  {d.Label,-4}   {d.Trials,3} {d.SuccessRate,4:P0} {d.NeverTouched,5}  "
                        + $"{F(d.MedianFirstTouchMs, "F0"),5}ms {F(d.MedianSettleMs, "F0"),4}ms "
                        + $"{F(d.MedianPeakRadius, "F2"),5}");
        }

        return sb.ToString();
    }

    private static string Compare(string label, double before, double now, string unit, bool lowerIsBetter, int decimals = 0)
    {
        double delta = now - before;
        double epsilon = decimals > 0 ? 0.005 : 0.5;
        bool same = Math.Abs(delta) < epsilon;
        bool better = lowerIsBetter ? delta < 0 : delta > 0;
        string format = "F" + decimals;

        // 符号は自分で付ける。"+F0;-F0" のような節つきの書式は使えない --- 節の中では F が
        // 書式指定子ではなくただの文字になり、「-F310」のような表示になる。
        string signed = same ? "±0" : (delta > 0 ? "+" : "−") + Math.Abs(delta).ToString(format);
        string arrow = same ? " " : better ? "↓" : "↑";
        return $"  {label} {before.ToString(format),6} → {now.ToString(format),6} {unit} {arrow} ({signed,7})";
    }

    /// <summary>
    /// 指摘1つぶんのカード。
    ///
    /// Panel + Dock.Top + AutoSize は使わない。docked な子の幅から親が縮んで、カードが数px まで
    /// 潰れる (実際に潰れた)。縦に積むだけなので、積む仕事は FlowLayoutPanel に任せて、折り返しは
    /// ラベルの MaximumSize で決める。
    /// </summary>
    private Control MakeFindingCard(AimFinding finding, int width)
    {
        var (accent, mark) = finding.Level switch
        {
            AimFindingLevel.Problem => (Color.FromArgb(255, 150, 110), "[!]"),
            AimFindingLevel.Note => (Color.FromArgb(235, 210, 130), "[-]"),
            _ => (Color.FromArgb(140, 215, 160), "[o]"),
        };

        int textWidth = Math.Max(200, width - 28);
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 0),
            MaximumSize = new Size(width, 0),
            BackColor = Color.FromArgb(28, 31, 37),
            Padding = new Padding(12, 10, 12, 12),
            Margin = new Padding(0, 0, 0, 10),
        };
        card.Controls.Add(new Label
        {
            Text = $"{mark} {finding.Title}",
            AutoSize = true,
            MaximumSize = new Size(textWidth, 0),
            ForeColor = accent,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        });
        card.Controls.Add(new Label
        {
            Text = finding.Detail,
            AutoSize = true,
            MaximumSize = new Size(textWidth, 0),
            ForeColor = Color.FromArgb(178, 186, 198),
            Margin = new Padding(0),
        });
        return card;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_phase == Phase.Result)
            {
                Close();
                return;
            }

            // 途中で止めたぶんは結果にしない。測りきっていない成績を並べても比較に使えない。
            lock (_gate)
            {
                _session.Abort();
            }
            Close();
            return;
        }

        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            if (_phase == Phase.Result)
            {
                Restart();
            }
            else if (_phase == Phase.Ready)
            {
                lock (_gate)
                {
                    _phase = Phase.Running;
                    _session.Start();
                }
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (_phase == Phase.Result)
        {
            return;
        }

        var view = _view;

        // 的の並びを薄く出しておく。次がどこに出るかが分かると、視線の移動と体の準備が
        // 分かれず済む --- 測りたいのは狙う動作であって、的を探す時間ではない。
        using (var markerPen = new Pen(Color.FromArgb(40, 46, 56), 1.5f))
        {
            foreach (var m in _markers)
            {
                g.DrawEllipse(markerPen, m.X - 5, m.Y - 5, 10, 10);
            }
        }

        DrawTarget(g, view);
        DrawCursor(g, view);
        DrawHud(g, view);
    }

    private void DrawTarget(Graphics g, in Snapshot view)
    {
        var t = view.Target;
        float r = (float)t.RadiusPx;
        var rect = new RectangleF((float)t.CenterXPx - r, (float)t.CenterYPx - r, r * 2, r * 2);

        using (var fill = new SolidBrush(view.Inside
            ? Color.FromArgb(60, 120, 200, 255)
            : Color.FromArgb(30, 120, 200, 255)))
        {
            g.FillEllipse(fill, rect);
        }
        using (var pen = new Pen(view.Inside ? Color.FromArgb(150, 215, 255) : Color.FromArgb(90, 140, 180), 2f))
        {
            g.DrawEllipse(pen, rect);
        }

        // 維持の進み具合は的のまわりの弧で出す。数字で出すと的から視線が外れる。
        if (view.DwellProgress > 0)
        {
            var arcRect = RectangleF.Inflate(rect, 10, 10);
            using var arcPen = new Pen(Color.FromArgb(255, 210, 120), 4f);
            g.DrawArc(arcPen, arcRect, -90, (float)(360 * view.DwellProgress));
        }

        // 中心の点。「円の中ならどこでもよい」のではなく中心を狙ってもらうほうが、震えの
        // 測定が素直になる (縁で合格すると、そのあとの維持が縁の出入りになる)。
        using var center = new SolidBrush(Color.FromArgb(120, 200, 255));
        g.FillEllipse(center, (float)t.CenterXPx - 2, (float)t.CenterYPx - 2, 4, 4);
    }

    private static void DrawCursor(Graphics g, in Snapshot view)
    {
        float x = (float)view.CursorX;
        float y = (float)view.CursorY;
        using var pen = new Pen(view.Inactive ? Color.FromArgb(120, 128, 140) : Color.FromArgb(255, 235, 200), 1.5f);
        g.DrawLine(pen, x - 11, y, x - 4, y);
        g.DrawLine(pen, x + 4, y, x + 11, y);
        g.DrawLine(pen, x, y - 11, x, y - 4);
        g.DrawLine(pen, x, y + 4, x, y + 11);
        using var dot = new SolidBrush(view.Inactive ? Color.FromArgb(120, 128, 140) : Color.FromArgb(255, 210, 120));
        g.FillEllipse(dot, x - 2.5f, y - 2.5f, 5, 5);
    }

    private void DrawHud(Graphics g, in Snapshot view)
    {
        using var dim = new SolidBrush(Color.FromArgb(120, 128, 140));
        using var bright = new SolidBrush(Color.FromArgb(220, 228, 240));
        using var warn = new SolidBrush(Color.FromArgb(255, 170, 120));
        using var small = new Font(Font.FontFamily, 9.5f);
        using var big = new Font(Font.FontFamily, 22f, FontStyle.Bold);
        using var mid = new Font(Font.FontFamily, 12f);

        if (_phase == Phase.Ready)
        {
            int remaining = Math.Max(0, ReadySeconds - (int)_readyClock.Elapsed.TotalSeconds);
            var centered = new StringFormat { Alignment = StringAlignment.Center };
            float cx = Bounds.Width / 2f;
            float cy = Bounds.Height / 2f;

            g.DrawString($"{remaining}", big, bright, cx, cy - 120, centered);
            g.DrawString(
                "青い円に入って1秒とどまると、次の的に移ります。",
                mid, bright, cx, cy - 66, centered);
            g.DrawString(
                "足を中立に戻して、カーソルが止まることを確かめてください。\n"
                + "流れるなら F12 で原点を合わせ直してから始めてください。\n\n"
                + "Enter ですぐ開始 / Esc で中止",
                small, dim, cx, cy - 34, centered);
            return;
        }

        g.DrawString($"{view.TrialIndex + 1} / {view.TrialCount}", small, bright, 24, 20);
        g.DrawString($"難易度 {view.IndexOfDifficulty:F1} bit  ・  Esc で中止 (結果は出ません)", small, dim, 24, 40);

        if (view.Inactive)
        {
            var centered = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(
                "ボードから荷重が取れていません（降りている / 荷重不足）",
                mid, warn, Bounds.Width / 2f, Bounds.Height - 90, centered);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
