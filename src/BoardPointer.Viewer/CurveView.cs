using BoardPointer.Core.Mapping;

namespace BoardPointer.Viewer;

/// <summary>
/// 応答曲線のグラフ。横軸が正規化半径 (0=中立、1=可動域いっぱい)、縦軸がカーソル速度。
///
/// 今の足の位置を丸で重ねて描くのが肝。つまみを動かしたときに「曲線のどこを自分が使っているか」が
/// 同時に見えないと、デッドゾーンが広すぎるのか指数がきついのかの区別がつかない。
/// </summary>
public sealed class CurveView : Control
{
    private ResponseCurve? _curve;
    private double _radius;
    private bool _active;
    private double _pressureFactor = 1.0;

    public CurveView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(180, 188, 200);
    }

    public void SetCurve(ResponseCurve curve) => _curve = curve;

    /// <param name="pressureFactor">
    /// 荷重モードによる倍率 (0〜1)。曲線の値はこれを掛けた後が実際の速度なので、グラフにも
    /// 反映しないと「グラフでは出ているのにカーソルが動かない」という食い違いが起きる。
    /// </param>
    public void SetPosition(double normalizedRadius, bool active, double pressureFactor)
    {
        _radius = normalizedRadius;
        _active = active;
        _pressureFactor = Math.Clamp(pressureFactor, 0, 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (_curve is null || Width < 40 || Height < 40)
        {
            return;
        }

        const float left = 34f;
        const float bottom = 18f;
        const float top = 12f;
        const float right = 10f;
        float plotW = Width - left - right;
        float plotH = Height - top - bottom;
        if (plotW < 10 || plotH < 10)
        {
            return;
        }

        const double maxRadius = 1.15;
        double maxSpeed = Math.Max(1.0, _curve.MaxSpeedPxPerSec);

        float ToX(double r) => left + (float)(r / maxRadius) * plotW;
        float ToY(double speed) => top + plotH - (float)(speed / maxSpeed) * plotH;

        using var axisPen = new Pen(Color.FromArgb(64, 70, 80), 1f);
        using var gridPen = new Pen(Color.FromArgb(40, 44, 52), 1f);
        using var labelBrush = new SolidBrush(Color.FromArgb(120, 128, 140));
        using var font = new Font(Font.FontFamily, 7f);

        // デッドゾーンの帯。ここにいる限りカーソルは動かない、という範囲を面で見せる。
        float deadRight = ToX(Math.Clamp(_curve.Deadzone, 0, maxRadius));
        using (var deadBrush = new SolidBrush(Color.FromArgb(40, 90, 100, 120)))
        {
            g.FillRectangle(deadBrush, left, top, deadRight - left, plotH);
        }
        g.DrawString("無反応", font, labelBrush, left + 2, top + plotH - 14);

        g.DrawLine(gridPen, ToX(1.0), top, ToX(1.0), top + plotH);
        g.DrawString("可動域", font, labelBrush, ToX(1.0) - 20, top - 1);

        g.DrawLine(axisPen, left, top, left, top + plotH);
        g.DrawLine(axisPen, left, top + plotH, left + plotW, top + plotH);
        g.DrawString($"{maxSpeed:F0}", font, labelBrush, 2, top - 2);
        g.DrawString("px/s", font, labelBrush, 2, top + 10);
        g.DrawString("0", font, labelBrush, 20, top + plotH - 6);

        // 曲線は2本描く。細い薄い線が荷重を掛ける前の形、太い線が今の荷重での実効カーブ。
        // 荷重モードを使っていれば (倍率 < 1) 2本が離れ、どれだけ絞られているかが目で分かる。
        var basePoints = new List<PointF>();
        var effectivePoints = new List<PointF>();
        for (int i = 0; i <= 120; i++)
        {
            double r = maxRadius * i / 120.0;
            double speed = _curve.SpeedAt(r);
            basePoints.Add(new PointF(ToX(r), ToY(speed)));
            effectivePoints.Add(new PointF(ToX(r), ToY(speed * _pressureFactor)));
        }

        if (_pressureFactor < 0.999)
        {
            using var basePen = new Pen(Color.FromArgb(70, 120, 200, 255), 1f);
            g.DrawLines(basePen, basePoints.ToArray());
            g.DrawString($"荷重 ×{_pressureFactor:F2}", font, labelBrush, left + 4, top + 1);
        }
        using (var curvePen = new Pen(Color.FromArgb(120, 200, 255), 2f))
        {
            g.DrawLines(curvePen, effectivePoints.ToArray());
        }

        // 今いる場所。マーカーは実効カーブの上に置く --- ここが実際に出ている速度。
        double radius = Math.Clamp(_radius, 0, maxRadius);
        float markerX = ToX(radius);
        float markerY = ToY(_curve.SpeedAt(radius) * _pressureFactor);
        using (var markerPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
        {
            g.DrawLine(markerPen, markerX, top, markerX, top + plotH);
        }
        using (var markerBrush = new SolidBrush(_active ? Color.FromArgb(255, 210, 120) : Color.FromArgb(110, 118, 130)))
        {
            g.FillEllipse(markerBrush, markerX - 5, markerY - 5, 10, 10);
        }
    }
}
