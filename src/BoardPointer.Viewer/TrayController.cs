using System.Runtime.InteropServices;

namespace BoardPointer.Viewer;

/// <summary>
/// タスクトレイの常駐。
///
/// 常駐の価値は「起動しっぱなしにできる」ことより、**窓を出さずに測り直せる**ことのほうが大きい。
/// マウス出力中はカーソルが足に取られるので、窓のボタンを押しに行くのが難しい。原点合わせや
/// 出力の入り切りをトレイのメニューから叩けると、その困りごとが消える。
///
/// アイコンは画像ファイルではなく実行時に描く。状態 (未接続 / 接続中 / 出力中) で色を変えたいが、
/// そのために .ico を3つ抱えるのは割に合わない。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _connectItem = new("接続");
    private readonly ToolStripMenuItem _outputItem = new("マウス出力");
    private readonly ToolStripMenuItem _tareItem = new("荷重ゼロ点 (降りて3秒)");
    private readonly ToolStripMenuItem _centerItem = new("重心の原点 (3秒)");
    private readonly ToolStripMenuItem _recenterItem = new("重心の原点を今に合わせる");
    private readonly ToolStripMenuItem _windowItem = new("設定の窓を開く");
    private readonly ToolStripMenuItem _exitItem = new("終了");

    private readonly Icon _idleIcon;
    private readonly Icon _connectedIcon;
    private readonly Icon _outputIcon;

    public event Action? ConnectRequested;
    public event Action? OutputToggleRequested;
    public event Action? TareRequested;
    public event Action? CenterRequested;
    public event Action? RecenterRequested;
    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public TrayController()
    {
        _idleIcon = CreateIcon(Color.FromArgb(120, 128, 140));
        _connectedIcon = CreateIcon(Color.FromArgb(120, 200, 255));
        _outputIcon = CreateIcon(Color.FromArgb(255, 190, 90));

        _menu.Items.AddRange(
        [
            _connectItem,
            _outputItem,
            new ToolStripSeparator(),
            _tareItem,
            _centerItem,
            _recenterItem,
            new ToolStripSeparator(),
            _windowItem,
            _exitItem,
        ]);

        _connectItem.Click += (_, _) => ConnectRequested?.Invoke();
        _outputItem.Click += (_, _) => OutputToggleRequested?.Invoke();
        _tareItem.Click += (_, _) => TareRequested?.Invoke();
        _centerItem.Click += (_, _) => CenterRequested?.Invoke();
        _recenterItem.Click += (_, _) => RecenterRequested?.Invoke();
        _windowItem.Click += (_, _) => ShowWindowRequested?.Invoke();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "BoardPointer",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();
    }

    /// <param name="connected">ソースが繋がっているか。</param>
    /// <param name="outputEnabled">マウス出力中か。</param>
    /// <param name="status">ツールチップに出す1行。</param>
    /// <param name="outputHotkey">出力の入り切りに割り当てられているキーの表記。</param>
    public void SetState(bool connected, bool outputEnabled, string status, string outputHotkey)
    {
        _notifyIcon.Icon = outputEnabled ? _outputIcon : connected ? _connectedIcon : _idleIcon;

        // NotifyIcon.Text は 63 文字までで、超えると例外になる。
        string text = $"BoardPointer — {status}";
        _notifyIcon.Text = text.Length > 63 ? text[..60] + "..." : text;

        _connectItem.Text = connected ? "停止" : "接続";
        _outputItem.Text = (outputEnabled ? "マウス出力を止める" : "マウス出力を始める")
                         + (string.IsNullOrEmpty(outputHotkey) ? string.Empty : $" ({outputHotkey})");
        _outputItem.Checked = outputEnabled;
        _tareItem.Enabled = connected;
        _centerItem.Enabled = connected;
        _recenterItem.Enabled = connected;
    }

    /// <summary>
    /// ボードを真上から見た形に点を打っただけのアイコン。16x16 に縮んでも状態の色が分かるよう、
    /// 点は大きめに取る。
    /// </summary>
    private static Icon CreateIcon(Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(225, 232, 240), 3f);
            g.DrawRectangle(pen, 3, 8, 26, 16);
            using var brush = new SolidBrush(accent);
            g.FillEllipse(brush, 11, 11, 10, 10);
        }

        // GetHicon で作ったハンドルは自分で解放する必要がある。Clone してから捨てる。
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _connectedIcon.Dispose();
        _outputIcon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
