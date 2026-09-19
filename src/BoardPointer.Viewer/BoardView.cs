using BoardPointer.Core.Pipeline;

namespace BoardPointer.Viewer;

/// <summary>
/// ボードを真上から見た図に、重心を重ねて描く。
///
/// 生の重心 (白い輪) とフィルタ後の重心 (塗りつぶし) を両方出しているのが肝で、フィルタのつまみを
/// 触ったときに「どれだけ震えが消えたか」と「どれだけ遅れたか」が同時に目に入る。片方だけ出すと、
/// 遅れているのか自分の体が遅いのか区別がつかない。
/// </summary>
public sealed class BoardView : Control
{
    private const int TrailCapacity = 400;

    private readonly Queue<PointF> _trail = new(TrailCapacity);
    private readonly object _lock = new();
    private BoardFrame _frame;
    private bool _hasFrame;

    public double BoardWidthMm { get; set; } = 433.0;
    public double BoardLengthMm { get; set; } = 238.0;
    public int TrailLength { get; set; } = 200;

    public BoardView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(24, 26, 30);
    }

    /// <summary>サンプルスレッドから呼ばれる。描画そのものはUIタイマーの Invalidate に任せる。</summary>
    public void Push(BoardFrame frame)
    {
        lock (_lock)
        {
            _frame = frame;
            _hasFrame = true;
            if (frame.Present)
            {
                _trail.Enqueue(new PointF((float)frame.CopXFilteredMm, (float)frame.CopYFilteredMm));
                while (_trail.Count > Math.Max(1, TrailLength))
                {
                    _trail.Dequeue();
                }
            }
            else
            {
                _trail.Clear();
            }
        }
    }

    public void ClearTrail()
    {
        lock (_lock)
        {
            _trail.Clear();
            _hasFrame = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        BoardFrame frame;
        PointF[] trail;
        bool hasFrame;
        lock (_lock)
        {
            frame = _frame;
            hasFrame = _hasFrame;
            trail = _trail.ToArray();
        }

        // ボードの矩形を、縦横比を保ったままコントロールに収める。
        float margin = 28f;
        float availableW = Width - margin * 2;
        float availableH = Height - margin * 2;
        if (availableW <= 10 || availableH <= 10)
        {
            return;
        }
        float scale = Math.Min(availableW / (float)BoardWidthMm, availableH / (float)BoardLengthMm);
        float boardW = (float)BoardWidthMm * scale;
        float boardH = (float)BoardLengthMm * scale;
        float cx = Width / 2f;
        float cy = Height / 2f;
        var board = new RectangleF(cx - boardW / 2, cy - boardH / 2, boardW, boardH);

        using var boardPen = new Pen(Color.FromArgb(70, 76, 86), 2f);
        using var gridPen = new Pen(Color.FromArgb(44, 48, 56), 1f);
        using var labelBrush = new SolidBrush(Color.FromArgb(110, 118, 130));
        using var font = new Font(Font.FontFamily, 7.5f);

        // 25/50/100mm の同心円。重心がどのくらいの大きさで動いているかの目盛り。
        foreach (int radiusMm in new[] { 25, 50, 100 })
        {
            float r = radiusMm * scale;
            g.DrawEllipse(gridPen, cx - r, cy - r, r * 2, r * 2);
            g.DrawString($"{radiusMm}mm", font, labelBrush, cx + r * 0.7f, cy - r * 0.78f);
        }
        g.DrawLine(gridPen, board.Left, cy, board.Right, cy);
        g.DrawLine(gridPen, cx, board.Top, cx, board.Bottom);
        g.DrawRectangle(boardPen, board.X, board.Y, board.Width, board.Height);
        g.DrawString("前", font, labelBrush, cx - 8, board.Top - 16);
        g.DrawString("後", font, labelBrush, cx - 8, board.Bottom + 3);

        if (!hasFrame)
        {
            using var hintBrush = new SolidBrush(Color.FromArgb(120, 128, 140));
            g.DrawString("ソースが未接続です。[合成データ] なら実機なしで動きます。", Font, hintBrush, board.X + 12, cy - 8);
            return;
        }

        DrawCornerLoads(g, board, frame, scale);

        if (!frame.Present)
        {
            using var offBrush = new SolidBrush(Color.FromArgb(120, 128, 140));
            g.DrawString("乗っていません (在席判定オフ)", Font, offBrush, board.X + 12, board.Bottom - 22);
            return;
        }

        // 軌跡。古いほど薄く。
        for (int i = 1; i < trail.Length; i++)
        {
            int alpha = (int)(200.0 * i / trail.Length);
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 8, 200), 90, 180, 230), 1.6f);
            g.DrawLine(pen, ToScreen(trail[i - 1], cx, cy, scale), ToScreen(trail[i], cx, cy, scale));
        }

        var rawPoint = ToScreen(new PointF((float)frame.CopXMm, (float)frame.CopYMm), cx, cy, scale);
        var filteredPoint = ToScreen(new PointF((float)frame.CopXFilteredMm, (float)frame.CopYFilteredMm), cx, cy, scale);

        // 生の重心は輪郭だけ。フィルタ後との差がそのまま「フィルタがどれだけ遅れているか」。
        using (var rawPen = new Pen(Color.FromArgb(150, 230, 230, 235), 1.5f))
        {
            g.DrawEllipse(rawPen, rawPoint.X - 6, rawPoint.Y - 6, 12, 12);
        }
        using (var linkPen = new Pen(Color.FromArgb(70, 230, 230, 235), 1f))
        {
            g.DrawLine(linkPen, rawPoint, filteredPoint);
        }
        using (var glow = new SolidBrush(Color.FromArgb(60, 120, 200, 255)))
        {
            g.FillEllipse(glow, filteredPoint.X - 13, filteredPoint.Y - 13, 26, 26);
        }
        using (var dot = new SolidBrush(Color.FromArgb(120, 200, 255)))
        {
            g.FillEllipse(dot, filteredPoint.X - 6, filteredPoint.Y - 6, 12, 12);
        }
    }

    /// <summary>四隅に、そのセンサーの荷重の大きさを四角の明るさで出す。</summary>
    private static void DrawCornerLoads(Graphics g, RectangleF board, BoardFrame frame, float scale)
    {
        double max = Math.Max(1.0, Math.Max(
            Math.Max(frame.TopRightKg, frame.BottomRightKg),
            Math.Max(frame.TopLeftKg, frame.BottomLeftKg)));

        (float X, float Y, double Kg)[] corners =
        [
            (board.Right, board.Top, frame.TopRightKg),
            (board.Right, board.Bottom, frame.BottomRightKg),
            (board.Left, board.Top, frame.TopLeftKg),
            (board.Left, board.Bottom, frame.BottomLeftKg),
        ];

        foreach (var (x, y, kg) in corners)
        {
            int intensity = (int)Math.Clamp(40 + 180 * (kg / max), 40, 220);
            using var brush = new SolidBrush(Color.FromArgb(intensity, 90, 140, 90));
            float size = 26f;
            g.FillRectangle(brush, x - size / 2, y - size / 2, size, size);
        }
    }

    private static PointF ToScreen(PointF mm, float cx, float cy, float scale) =>
        new(cx + mm.X * scale, cy - mm.Y * scale); // Y は前が正なので、画面座標では上向き
}
