// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ScreenX;

enum Kind { Rect, Ellipse, Arrow, Line, Pen, Highlight, Text, Step, Redact }

/// The editor's undo history. A step is the shape list, the crop and the
/// cut-outs, never pixels, so it costs a few hundred bytes however large the
/// capture is. Steps are copied going in and coming out, so editing the
/// current shapes never reaches back into history.
sealed class History
{
    const int Limit = 200;
    readonly List<(List<Shape> shapes, Rectangle crop, Cut[] cuts)> steps = [];
    int at;

    public History(Rectangle crop) => steps.Add(([], crop, []));

    public bool CanUndo => at > 0;
    public bool CanRedo => at < steps.Count - 1;

    /// A new step after the current one; anything that had been undone is gone.
    /// `cuts` is never changed after it is made, so it is kept as it is.
    public void Push(IEnumerable<Shape> shapes, Rectangle crop, Cut[]? cuts = null)
    {
        steps.RemoveRange(at + 1, steps.Count - at - 1);
        steps.Add((shapes.Select(s => s.Clone()).ToList(), crop, cuts ?? []));
        if (steps.Count > Limit) steps.RemoveAt(0);
        at = steps.Count - 1;
    }

    public (List<Shape> shapes, Rectangle crop) Current =>
        (steps[at].shapes.Select(s => s.Clone()).ToList(), steps[at].crop);

    public Cut[] Cuts => steps[at].cuts;

    public bool Undo()
    {
        if (!CanUndo) return false;
        at--;
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        at++;
        return true;
    }
}

/// A band taken out of the image, the two sides closing up over it, as
/// ShareX's Cut Out does: columns if `Columns`, else rows. In the coordinates
/// of the image as it was when the cut was made, so a list of them is applied
/// in order.
readonly record struct Cut(bool Columns, int At, int Length)
{
    /// Across a drag's longer side, so dragging sideways takes out columns.
    /// Null when nothing, or everything, would be taken out of `crop`.
    public static Cut? Across(PointF a, PointF b, Rectangle crop)
    {
        var columns = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
        var from = Math.Max((int)Math.Round(Math.Min(columns ? a.X : a.Y, columns ? b.X : b.Y)), columns ? crop.Left : crop.Top);
        var to = Math.Min((int)Math.Round(Math.Max(columns ? a.X : a.Y, columns ? b.X : b.Y)), columns ? crop.Right : crop.Bottom);
        var length = to - from;
        return length < 1 || length >= (columns ? crop.Width : crop.Height) ? null : new Cut(columns, from, length);
    }

    public bool Contains(PointF p) => (Columns ? p.X : p.Y) is var v && v >= At && v <= At + Length;

    /// Past the band moves back by its length; inside it lands on the seam.
    float Shift(float v) => v >= At + Length ? v - Length : v > At ? At : v;

    PointF Shift(PointF p) => Columns ? new(Shift(p.X), p.Y) : new(p.X, Shift(p.Y));

    public Rectangle Shift(Rectangle r) => Columns
        ? Rectangle.FromLTRB((int)Shift(r.Left), r.Top, (int)Shift(r.Right), r.Bottom)
        : Rectangle.FromLTRB(r.Left, (int)Shift(r.Top), r.Right, (int)Shift(r.Bottom));

    /// Text draws out from one point, so all it covers counts, not the point:
    /// a label anchored in the band can run well past it. A step is the
    /// point it marks, so it goes with that point however far its circle
    /// reaches; kept, it would land on the seam and mark something else.
    bool Holds(Shape s)
    {
        if (s.Kind != Kind.Text) return s.Points.All(Contains);
        var b = s.Bounds;
        return Columns ? b.Left >= At && b.Right <= At + Length : b.Top >= At && b.Bottom <= At + Length;
    }

    /// Shapes wholly inside the band go with what they marked; the rest
    /// follow the image.
    public List<Shape> Shift(IEnumerable<Shape> shapes)
    {
        var kept = new List<Shape>();
        foreach (var s in shapes)
        {
            if (Holds(s)) continue;
            var moved = s.Clone();
            for (var i = 0; i < moved.Points.Count; i++) moved.Points[i] = Shift(moved.Points[i]);
            kept.Add(moved);
        }
        return kept;
    }

    /// A new bitmap with `cuts` taken out in order; `image` is left as it was.
    /// One copy however many cuts: the columns and rows that survive them all
    /// are worked out first, then copied across in unbroken runs.
    public static Bitmap Apply(Bitmap image, IReadOnlyList<Cut> cuts)
    {
        var xs = Enumerable.Range(0, image.Width).ToList();
        var ys = Enumerable.Range(0, image.Height).ToList();
        foreach (var c in cuts) (c.Columns ? xs : ys).RemoveRange(c.At, c.Length);
        var cut = new Bitmap(xs.Count, ys.Count, image.PixelFormat);
        try
        {
            using var g = Graphics.FromImage(cut);
            // A straight copy: no blending, no resampling.
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var (fromX, toX, w) in Runs(xs))
            foreach (var (fromY, toY, h) in Runs(ys))
                g.DrawImage(image, new Rectangle(toX, toY, w, h), new Rectangle(fromX, fromY, w, h), GraphicsUnit.Pixel);
            return cut;
        }
        catch
        {
            // A full-size image, not left for the finalizer.
            cut.Dispose();
            throw;
        }
    }

    /// Each stretch of consecutive source indices: where it starts in the
    /// source, where it lands, and how long it is.
    static IEnumerable<(int from, int to, int length)> Runs(List<int> kept)
    {
        for (var i = 0; i < kept.Count;)
        {
            var start = i;
            while (++i < kept.Count && kept[i] == kept[i - 1] + 1) { }
            yield return (kept[start], start, i - start);
        }
    }
}

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

    /// Grab points for resizing. A handle names which point gives its X and
    /// which gives its Y, so a box's four corners come from its two points.
    public (int x, int y)[] Handles => Kind switch
    {
        Kind.Rect or Kind.Ellipse or Kind.Highlight or Kind.Redact => [(0, 0), (1, 0), (0, 1), (1, 1)],
        Kind.Line or Kind.Arrow => [(0, 0), (1, 1)],
        _ => [],
    };

    public PointF HandleAt((int x, int y) handle) => new(Points[handle.x].X, Points[handle.y].Y);

    public void MoveHandle((int x, int y) handle, PointF to)
    {
        Points[handle.x] = new PointF(to.X, Points[handle.x].Y);
        Points[handle.y] = new PointF(Points[handle.y].X, to.Y);
    }

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
