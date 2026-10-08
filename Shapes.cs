// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ScreenX;

enum Kind { Rect, Ellipse, Arrow, Line, Pen, Highlight, Text, Step, Redact }

/// One annotation, in image pixels. Shapes stay objects until export, so they
/// can be selected, moved and deleted, and undo is a list copy rather than an
/// image copy.
///
/// The same `Draw` paints the editor and the saved file. There is no second
/// renderer to fall out of step with the first.
sealed class Shape
{
    public Kind Kind;
    public Color Colour;
    public float Width;
    /// Two corners for the box kinds, the ends for lines, every sample for the
    /// pen, and the anchor for text and steps.
    public List<PointF> Points = [];
    public string Text = "";
    public int Number;

    public Shape Clone() => new()
    {
        Kind = Kind, Colour = Colour, Width = Width, Points = [.. Points], Text = Text, Number = Number,
    };

    public void Offset(float dx, float dy)
    {
        for (var i = 0; i < Points.Count; i++) Points[i] = new PointF(Points[i].X + dx, Points[i].Y + dy);
    }

    RectangleF Box => Points.Count < 2 ? RectangleF.Empty : RectangleF.FromLTRB(
        Math.Min(Points[0].X, Points[1].X), Math.Min(Points[0].Y, Points[1].Y),
        Math.Max(Points[0].X, Points[1].X), Math.Max(Points[0].Y, Points[1].Y));

    public float FontSize => Width * 6 + 6;
    float StepRadius => Width * 3 + 10;

    /// Too small to have been meant, so a stray click does not leave a dot.
    public bool Degenerate => Kind switch
    {
        Kind.Text => Text.Length == 0,
        Kind.Step => false,
        Kind.Pen => Points.Count < 2,
        _ => Box.Width < 3 && Box.Height < 3,
    };

    public RectangleF Bounds
    {
        get
        {
            switch (Kind)
            {
                case Kind.Step:
                    var r = StepRadius;
                    return new RectangleF(Points[0].X - r, Points[0].Y - r, r * 2, r * 2);
                case Kind.Text:
                    using (var font = Font())
                        return new RectangleF(Points[0], Measuring.MeasureString(Text, font, PointF.Empty, StringFormat.GenericTypographic));
                default:
                    var xs = Points.Select(p => p.X);
                    var ys = Points.Select(p => p.Y);
                    var bounds = RectangleF.FromLTRB(xs.Min(), ys.Min(), xs.Max(), ys.Max());
                    bounds.Inflate(Width / 2, Width / 2);
                    return bounds;
            }
        }
    }

    public bool Hit(PointF p, float tolerance)
    {
        switch (Kind)
        {
            case Kind.Line or Kind.Arrow or Kind.Pen:
                for (var i = 1; i < Points.Count; i++)
                    if (Distance(p, Points[i - 1], Points[i]) <= tolerance + Width / 2) return true;
                return false;
            default:
                var b = Bounds;
                b.Inflate(tolerance, tolerance);
                return b.Contains(p);
        }
    }

    static float Distance(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    public Font Font() => new("Segoe UI", FontSize, FontStyle.Bold, GraphicsUnit.Pixel);

    /// GDI+ text, not TextRenderer: TextRenderer is GDI, which ignores the
    /// zoom transform and writes garbage into the alpha of a 32 bpp bitmap.
    static readonly Graphics Measuring = Graphics.FromImage(new Bitmap(1, 1));

    /// `source` is the untouched capture, which redaction samples from.
    public void Draw(Graphics g, Bitmap source)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Colour, Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var box = Box;
        switch (Kind)
        {
            case Kind.Rect:
                g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                break;
            case Kind.Ellipse:
                g.DrawEllipse(pen, box);
                break;
            case Kind.Line:
                g.DrawLine(pen, Points[0], Points[1]);
                break;
            case Kind.Arrow:
                DrawArrow(g, pen);
                break;
            case Kind.Pen:
                g.DrawLines(pen, Points.ToArray());
                break;
            case Kind.Highlight:
                using (var brush = new SolidBrush(Color.FromArgb(96, Colour)))
                    g.FillRectangle(brush, box);
                break;
            case Kind.Text:
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (var font = Font())
                using (var ink = new SolidBrush(Colour))
                    g.DrawString(Text, font, ink, Points[0], StringFormat.GenericTypographic);
                break;
            case Kind.Step:
                DrawStep(g);
                break;
            case Kind.Redact:
                Pixelate(g, source, Rectangle.Round(box));
                break;
        }
    }

    void DrawArrow(Graphics g, Pen pen)
    {
        PointF from = Points[0], to = Points[1];
        float dx = to.X - from.X, dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        float ux = dx / length, uy = dy / length;
        // The head grows with the line, but not past half its length.
        var head = Math.Min(Width * 4 + 10, length / 2);
        var half = head * 0.6f;
        var baseX = to.X - ux * head;
        var baseY = to.Y - uy * head;
        g.DrawLine(pen, from, new PointF(baseX + ux, baseY + uy));
        using var brush = new SolidBrush(Colour);
        g.FillPolygon(brush, [to, new PointF(baseX - uy * half, baseY + ux * half), new PointF(baseX + uy * half, baseY - ux * half)]);
    }

    void DrawStep(Graphics g)
    {
        var r = StepRadius;
        var at = Points[0];
        using var brush = new SolidBrush(Colour);
        g.FillEllipse(brush, at.X - r, at.Y - r, r * 2, r * 2);
        using var font = new Font("Segoe UI", r, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Colour.GetBrightness() > 0.6f ? Color.Black : Color.White);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.DrawString(Number.ToString(), font, ink, new RectangleF(at.X - r, at.Y - r, r * 2, r * 2), centred);
    }

    /// Each block becomes the average of its pixels.
    ///
    /// This is redaction, so it has to destroy the detail rather than soften
    /// it: a blur of small text can be read back, the average of a 12 px block
    /// cannot. Sampled from the original capture so nothing drawn on top leaks
    /// into the result either.
    static unsafe void Pixelate(Graphics g, Bitmap source, Rectangle area)
    {
        area.Intersect(new Rectangle(Point.Empty, source.Size));
        if (area.Width < 1 || area.Height < 1) return;
        const int block = 12;
        var data = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var brush = new SolidBrush(Color.Black);
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            for (var by = 0; by < area.Height; by += block)
            for (var bx = 0; bx < area.Width; bx += block)
            {
                int w = Math.Min(block, area.Width - bx), h = Math.Min(block, area.Height - by);
                long r = 0, gr = 0, b = 0;
                for (var y = by; y < by + h; y++)
                {
                    var row = (uint*)((byte*)data.Scan0 + y * data.Stride);
                    for (var x = bx; x < bx + w; x++)
                    {
                        var p = row[x];
                        r += (p >> 16) & 0xFF;
                        gr += (p >> 8) & 0xFF;
                        b += p & 0xFF;
                    }
                }
                var n = w * h;
                brush.Color = Color.FromArgb((int)(r / n), (int)(gr / n), (int)(b / n));
                g.FillRectangle(brush, area.X + bx, area.Y + by, w, h);
            }
            g.SmoothingMode = mode;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
}
