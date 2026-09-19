namespace BoardPointer.Viewer;

/// <summary>
/// 数値の読み取り表示。標準の Label / TextBox ではなく自前描画にしてある。
/// 33ms ごとに全文を差し替える使い方だと、Label は GDI 側の改行の扱いとオートサイズの相互作用で
/// 描画が途中で切れることがあり、TextBox は改行コードに敏感で等幅の桁揃えも崩れやすい。
/// ここでやりたいのは「等幅で何行か出す」だけなので、自分で描くのが一番素直で確実。
/// </summary>
public sealed class ReadoutView : Control
{
    private string[] _lines = [];

    public ReadoutView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(27, 29, 34);
        ForeColor = Color.FromArgb(215, 220, 228);
        Font = new Font("Consolas", 9.5f);
    }

    /// <summary>見出し行 (先頭が -- で始まる行) を少し暗く出すための色。</summary>
    public Color SectionColor { get; set; } = Color.FromArgb(140, 170, 205);

    public void SetText(string text)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        if (!lines.SequenceEqual(_lines))
        {
            _lines = lines;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var textBrush = new SolidBrush(ForeColor);
        using var sectionBrush = new SolidBrush(SectionColor);

        float lineHeight = Font.GetHeight(g) + 2f;
        float y = 10f;
        foreach (string line in _lines)
        {
            if (line.Length > 0)
            {
                var brush = line.StartsWith("--", StringComparison.Ordinal) ? sectionBrush : textBrush;
                g.DrawString(line, Font, brush, 12f, y);
            }
            y += lineHeight;
        }
    }
}
