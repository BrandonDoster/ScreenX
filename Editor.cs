// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ScreenX;

enum Tool { Select, Crop, Cut, Arrow, Rect, Ellipse, Line, Pen, Highlight, Text, Step, Redact }

/// The annotation editor. Modeless, so the hotkeys keep working while it is
/// open, and gone completely when it closes: its bitmaps (two, three once
/// something is cut out) are the only large thing it holds, and all are
/// disposed with it.
sealed class Editor : Form
{
    static readonly Color[] Swatches =
    [
        Color.FromArgb(0xe5, 0x48, 0x4d), Color.FromArgb(0xf5, 0xa5, 0x24), Color.FromArgb(0xf7, 0xe7, 0x33),
        Color.FromArgb(0x37, 0xb2, 0x4d), Color.FromArgb(0x3d, 0x8b, 0xfd), Color.FromArgb(0x9b, 0x59, 0xf5),
        Color.White, Color.Black,
    ];

    /// Icons are Segoe Fluent Icons glyphs, which Windows 11 ships; Step has
    /// none and is drawn as the numbered dot it places.
    static readonly (Tool tool, string label, Keys key, char glyph)[] Tools =
    [
        (Tool.Select, "Select", Keys.V, '\uE8B0'), (Tool.Arrow, "Arrow", Keys.A, '\uE8AD'),
        (Tool.Rect, "Box", Keys.R, '\uE739'), (Tool.Ellipse, "Ellipse", Keys.E, '\uEA3A'),
        (Tool.Line, "Line", Keys.L, '\uE738'), (Tool.Pen, "Pen", Keys.P, '\uEE56'),
        (Tool.Highlight, "Highlight", Keys.H, '\uE7E6'), (Tool.Text, "Text", Keys.T, '\uE8D2'),
        (Tool.Step, "Step", Keys.N, '\0'), (Tool.Redact, "Redact", Keys.X, '\uE80A'),
        (Tool.Crop, "Crop", Keys.C, '\uE7A8'), (Tool.Cut, "Cut out", Keys.U, '\uE8C6'),
    ];

    static readonly (string label, float width)[] Sizes = [("Small", 2), ("Medium", 4), ("Large", 8)];

    static readonly bool Dark = Application.IsDarkModeEnabled;
    static readonly Color Ink = Dark ? Color.FromArgb(0xe8, 0xe8, 0xe8) : Color.FromArgb(0x1f, 0x1f, 0x1f);
    static readonly Color Surface = Dark ? Color.FromArgb(0x2b, 0x2b, 0x2b) : Color.FromArgb(0xf3, 0xf3, 0xf3);


    /// The capture as it arrived. Undoing a cut-out rebuilds `source` from it.
    readonly Bitmap original;
    /// `original` with `cuts` taken out, and the same bitmap while there are
    /// none. Redaction samples it, so nothing drawn on top can leak through a
    /// pixelated block.
    Bitmap source;
    /// `source` with every committed shape drawn on it, except `live`.
    Bitmap cache;
    List<Shape> shapes = [];
    Rectangle crop;
    Cut[] cuts = [];
    /// Dragged out and waiting for Enter or a click on it.
    Cut? pending;
    readonly History history;

    Tool tool = Tool.Arrow;
    Color colour = Swatches[0];
    float width = 4;
    float zoom = 1;

    /// Being drawn or dragged right now. Painted over the cache rather than
    /// into it, so moving it does not mean redrawing everything under it.
    Shape? live;
    Shape? selected;
    PointF? grabbed;
    /// The resize handle being dragged, if it is a resize and not a move.
    (int x, int y)? handle;
    /// Handle size on screen, whatever the zoom.
    const float HandleSize = 9;
    bool moved;
    TextBox? typing;
    PointF typingAt;
    /// The text shape being retyped, if it is an existing one.
    Shape? editing;

    /// Annotations not yet copied or saved anywhere.
    bool dirty;
    readonly string folder;
    readonly string? path;

    readonly Canvas canvas = new();
    /// Flow, so a narrow window wraps the toolbar onto a second row. Laid out
    /// in one row, Copy and Save went into the overflow menu at 800 px wide,
    /// out of sight.
    readonly ToolStrip bar = new()
    {
        GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), LayoutStyle = ToolStripLayoutStyle.Flow,
    };
    readonly ToolStripLabel status = new();
    readonly ToolStripButton undo, redo;

    /// `image` is copied, so the caller keeps and disposes its own.
    public Editor(Bitmap image, string folder, string? path)
    {
        this.folder = folder;
        this.path = path;
        source = original = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(original))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            // An explicit size, not DrawImageUnscaled: that scales by the
            // file's DPI against the screen's, so a 96 DPI PNG opened at 150%
            // came out enlarged and clipped.
            g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
        }
        cache = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        crop = new Rectangle(Point.Empty, image.Size);
        history = new History(crop);

        Text = "ScreenX";
        Icon = Program.Icon;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;

        // Drawn at this window's DPI rather than scaled up from 16 px.
        // ponytail: drawn once; moving to a monitor of another DPI scales them
        // (softer), redraw on DpiChanged if that bothers anyone.
        var size = LogicalToDeviceUnits(20);
        bar.ImageScalingSize = new Size(size, size);
        ToolStripButton Button(string label, Bitmap icon, object? tag, string tip, Action click)
        {
            var button = new ToolStripButton(label, icon) { Tag = tag, ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Image };
            button.Click += (_, _) => click();
            bar.Items.Add(button);
            return button;
        }
        foreach (var (t, label, key, glyph) in Tools)
            Button(label, t == Tool.Step ? StepIcon(size) : Glyph(glyph, size), t, $"{label} ({key})", () => SetTool(t));
        bar.Items.Add(new ToolStripSeparator());
        foreach (var swatch in Swatches)
            Button("Colour", Dot(swatch, size * 0.65f, size), swatch, "Colour", () => SetColour(swatch));
        bar.Items.Add(new ToolStripSeparator());
        foreach (var (label, w) in Sizes)
            Button(label, Dot(Ink, size * (0.15f + w * 0.06f), size), w, $"{label} lines and text", () => SetWidth(w));
        bar.Items.Add(new ToolStripSeparator());
        undo = Button("Undo", Glyph('\uE7A7', size), null, "Undo (Ctrl+Z)", Undo);
        redo = Button("Redo", Glyph('\uE7A6', size), null, "Redo (Ctrl+Y)", Redo);
        bar.Items.Add(new ToolStripSeparator());
        // Copy only, without saving anything, is the everyday way out, so it
        // is the one in the accent colour.
        var copy = Button("Copy", Glyph('\uE8C8', size, Color.White), null, "Copy to the clipboard, nothing saved (Ctrl+C)", Copy);
        var save = Button("Save", Glyph('\uE74E', size), null, "Save (Ctrl+S)", Save);
        copy.DisplayStyle = save.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        bar.Items.Add(status);
        var pad = LogicalToDeviceUnits(4);
        foreach (ToolStripItem item in bar.Items)
        {
            item.Margin = new Padding(1, 2, 1, 2);
            if (item is ToolStripButton) item.Padding = new Padding(pad);
        }
        status.ForeColor = Color.FromArgb((Ink.R + Surface.R) / 2, (Ink.G + Surface.G) / 2, (Ink.B + Surface.B) / 2);
        bar.Renderer = new BarRenderer(Surface, Ink, copy);

        Controls.Add(canvas);
        Controls.Add(bar);
        canvas.Dock = DockStyle.Fill;
        canvas.BackColor = Dark ? Color.FromArgb(0x1c, 0x1c, 0x1c) : Color.FromArgb(0xe0, 0xe0, 0xe0);
        // Fill is laid out last, after the toolbar has taken its strip.
        canvas.BringToFront();
        canvas.Paint += PaintCanvas;
        canvas.MouseDown += Down;
        canvas.MouseMove += Drag;
        canvas.MouseUp += Up;
        canvas.MouseDoubleClick += DoubleClicked;
        canvas.ZoomWheel += (delta, at) => SetZoom(zoom * (delta > 0 ? 1.25f : 0.8f), at);

        // Sized to the image, as far as the screen it was taken on allows.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        // Min then Max rather than Clamp: on a screen smaller than the minimum,
        // Clamp throws instead of choosing.
        // At least as wide as the toolbar in one row, so it wraps only on a
        // screen too narrow for that.
        var toolbarWidth = bar.GetPreferredSize(new Size(int.MaxValue, 0)).Width + 16;
        ClientSize = new Size(
            Math.Min(Math.Max(image.Width + 40, Math.Max(720, toolbarWidth)), area.Width * 9 / 10),
            Math.Min(Math.Max(image.Height + bar.PreferredSize.Height + 40, 420), area.Height * 9 / 10));
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);

        Rebuild();
        UpdateBar();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetZoom(Fit());
        canvas.Focus();
    }

    /// As large as the window allows, but never enlarged: growing the image
    /// is the zoom's job, and doing it on open looks like a mistake.
    ///
    /// A scrolling capture is read top to bottom, so it fits by width alone;
    /// fitting its height as well would shrink it to a strip.
    float Fit()
    {
        var across = (canvas.ClientSize.Width - 24f) / crop.Width;
        var down = (canvas.ClientSize.Height - 24f) / crop.Height;
        return Math.Clamp(crop.Height > crop.Width * 2 ? across : Math.Min(across, down), 0.05f, 1);
    }

    /// `anchor` is a point on the canvas that should stay over the same part
    /// of the image, which is what makes wheel zoom go where the pointer is.
    void SetZoom(float next, Point? anchor = null)
    {
        CommitText();
        var hold = anchor is Point a ? ToImage(a) : (PointF?)null;
        zoom = Math.Clamp(next, 0.05f, 8);
        canvas.AutoScrollMinSize = new Size((int)(crop.Width * zoom), (int)(crop.Height * zoom));
        if (hold is PointF image && anchor is Point screen)
        {
            // Scroll so the image point lands back under the anchor. The
            // setter takes positive offsets and clamps them to the range.
            var x = (image.X - crop.X) * zoom - screen.X;
            var y = (image.Y - crop.Y) * zoom - screen.Y;
            canvas.AutoScrollPosition = new Point((int)Math.Max(0, x), (int)Math.Max(0, y));
        }
        Text = $"ScreenX - {crop.Width} x {crop.Height} - {zoom * 100:0}%";
        canvas.Invalidate();
    }

    static Bitmap Picture(int size, Action<Graphics> draw)
    {
        var icon = new Bitmap(size, size);
        using var g = Graphics.FromImage(icon);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Not ClearType: its colour fringes are wrong on a transparent bitmap.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        draw(g);
        return icon;
    }

    static readonly StringFormat Centred = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
    };

    static Bitmap Glyph(char glyph, int size, Color? ink = null) => Picture(size, g =>
    {
        using var font = new Font("Segoe Fluent Icons", size * 0.8f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(ink ?? Ink);
        g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), Centred);
    });

    /// A colour swatch or a line width. The faint ring keeps black visible on
    /// a dark toolbar and white on a light one.
    static Bitmap Dot(Color colour, float diameter, int size) => Picture(size, g =>
    {
        var at = (size - diameter) / 2;
        using var brush = new SolidBrush(colour);
        g.FillEllipse(brush, at, at, diameter, diameter);
        using var ring = new Pen(Color.FromArgb(0x60, Ink));
        g.DrawEllipse(ring, at, at, diameter, diameter);
    });

    static Bitmap StepIcon(int size) => Picture(size, g =>
    {
        using var brush = new SolidBrush(Ink);
        g.FillEllipse(brush, size * 0.1f, size * 0.1f, size * 0.8f, size * 0.8f);
        using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var number = new SolidBrush(Surface);
        g.DrawString("1", font, number, new RectangleF(0, 0, size, size), Centred);
    });

    void UpdateBar()
    {
        foreach (ToolStripItem item in bar.Items)
            if (item is ToolStripButton button)
                button.Checked = button.Tag switch
                {
                    Tool t => t == tool,
                    Color c => c == colour,
                    float w => w == width,
                    _ => false,
                };
        undo.Enabled = history.CanUndo;
        redo.Enabled = history.CanRedo;
    }

    void SetTool(Tool next)
    {
        CommitText();
        tool = next;
        if (tool != Tool.Select) selected = null;
        if (pending != null) Uncut();
        canvas.Cursor = tool == Tool.Select ? Cursors.Default : Cursors.Cross;
        UpdateBar();
        canvas.Invalidate();
    }

    /// Also recolours the selected shape, which is how a shape is fixed after
    /// the fact.
    void SetColour(Color next)
    {
        colour = next;
        if (typing != null)
        {
            // Applies to the text being typed, kept when it is committed.
            typing.ForeColor = next;
        }
        else if (selected != null)
        {
            selected.Colour = next;
            Commit();
        }
        UpdateBar();
    }

    void SetWidth(float next)
    {
        width = next;
        if (typing != null)
        {
            var old = typing.Font;
            typing.Font = new Font("Segoe UI", new Shape { Width = next }.FontSize * zoom, FontStyle.Bold, GraphicsUnit.Pixel);
            old.Dispose();
            SizeTyping();
        }
        else if (selected != null)
        {
            selected.Width = next;
            Commit();
        }
        UpdateBar();
    }

    // History. A snapshot is the shape list and the crop, never pixels, so it
    // costs a few hundred bytes however large the capture is.

    void Commit()
    {
        history.Push(shapes, crop, cuts);
        dirty = true;
        status.Text = "";
        Rebuild();
        UpdateBar();
    }

    /// Back to the current step of history. `changes` is false when that only
    /// puts back what was already there, so closing afterwards does not ask
    /// about nothing.
    void Restore(bool changes = true)
    {
        var (restored, restoredCrop) = history.Current;
        shapes = restored;
        var cropChanged = crop != restoredCrop;
        crop = restoredCrop;
        selected = null;
        pending = null;
        if (changes) dirty = true;
        Recut(history.Cuts);
        Rebuild();
        if (cropChanged) SetZoom(Fit());
        UpdateBar();
    }

    void Undo()
    {
        CommitText();
        if (history.Undo()) Restore();
    }

    void Redo()
    {
        if (history.Redo()) Restore();
    }

    /// `source` made again from the capture when the cut-outs differ. Each
    /// step's array is its own and never changed, so the same array means the
    /// same cuts.
    void Recut(Cut[] next)
    {
        if (next == cuts) return;
        var made = original;
        foreach (var cut in next)
        {
            var smaller = cut.Apply(made);
            if (made != original) made.Dispose();
            made = smaller;
        }
        if (source != original) source.Dispose();
        source = made;
        cuts = next;
    }

    /// Takes the band out of the image and closes the gap, carrying shapes
    /// and the crop across it.
    void CutOut(Cut cut)
    {
        pending = null;
        shapes = cut.Shift(shapes);
        crop = cut.Shift(crop);
        // From the current image, not from the capture through every cut.
        var smaller = cut.Apply(source);
        if (source != original) source.Dispose();
        source = smaller;
        cuts = [.. cuts, cut];
        Commit();
        SetZoom(zoom);
    }

    void Rebuild()
    {
        if (cache.Size != source.Size)
        {
            cache.Dispose();
            cache = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        }
        using (var g = Graphics.FromImage(cache))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(source, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            foreach (var shape in shapes)
                if (shape != live) shape.Draw(g, source);
        }
        canvas.Invalidate();
    }

    // Coordinates. Everything is held in image pixels; only these two
    // functions know where the image is on screen.

    Point Origin => new(
        canvas.AutoScrollPosition.X + Math.Max(0, (canvas.ClientSize.Width - canvas.AutoScrollMinSize.Width) / 2),
        canvas.AutoScrollPosition.Y + Math.Max(0, (canvas.ClientSize.Height - canvas.AutoScrollMinSize.Height) / 2));

    /// Clamped to the image: the pointer keeps reporting past the edge, and a
    /// box dragged off the side should stop at the side.
    PointF ToImage(Point p)
    {
        var origin = Origin;
        return new PointF(
            Math.Clamp(crop.X + (p.X - origin.X) / zoom, crop.Left, crop.Right),
            Math.Clamp(crop.Y + (p.Y - origin.Y) / zoom, crop.Top, crop.Bottom));
    }

    PointF ToScreen(PointF p)
    {
        var origin = Origin;
        return new PointF(origin.X + (p.X - crop.X) * zoom, origin.Y + (p.Y - crop.Y) * zoom);
    }

    (int x, int y)? HandleUnder(PointF p)
    {
        if (selected == null) return null;
        var reach = HandleSize / zoom;
        foreach (var h in selected.Handles)
        {
            var at = selected.HandleAt(h);
            if (Math.Abs(at.X - p.X) <= reach && Math.Abs(at.Y - p.Y) <= reach) return h;
        }
        return null;
    }

    Shape? HitTest(PointF p)
    {
        for (var i = shapes.Count - 1; i >= 0; i--)
            if (shapes[i].Hit(p, 6 / zoom)) return shapes[i];
        return null;
    }

    // Input.

    void Down(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        CommitText();
        canvas.Focus();
        var p = ToImage(e.Location);
        switch (tool)
        {
            case Tool.Select:
                // A handle of the selected shape first: it overlaps the shape,
                // and grabbing it means resize rather than move.
                handle = HandleUnder(p);
                if (handle == null) selected = HitTest(p);
                if (selected != null)
                {
                    grabbed = p;
                    moved = false;
                    live = selected;
                    Rebuild();
                }
                canvas.Invalidate();
                break;
            case Tool.Text:
                StartTyping(p, "");
                break;
            case Tool.Step:
                var number = shapes.Where(s => s.Kind == Kind.Step).Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
                shapes.Add(new Shape { Kind = Kind.Step, Colour = colour, Width = width, Points = [p], Number = number });
                Commit();
                break;
            case Tool.Cut when pending is { } cut && cut.Contains(p):
                CutOut(cut);
                break;
            default:
                // Dragging elsewhere starts the band again.
                pending = null;
                live = new Shape
                {
                    // A crop or a cut-out is dragged out like a box and never
                    // kept as one.
                    Kind = tool is Tool.Crop or Tool.Cut ? Kind.Rect : Enum.Parse<Kind>(tool.ToString()),
                    Colour = colour,
                    Width = width,
                    Points = [p, p],
                };
                break;
        }
    }

    void Drag(object? sender, MouseEventArgs e)
    {
        var p = ToImage(e.Location);
        if (tool == Tool.Select && grabbed is PointF from && selected != null)
        {
            if (handle is { } h) selected.MoveHandle(h, p);
            else selected.Offset(p.X - from.X, p.Y - from.Y);
            grabbed = p;
            moved = true;
            canvas.Invalidate();
            return;
        }
        if (tool == Tool.Select)
        {
            canvas.Cursor = HandleUnder(p) != null ? Cursors.Cross
                : HitTest(p) != null ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        if (live == null || e.Button != MouseButtons.Left) return;
        if (live.Kind == Kind.Pen) live.Points.Add(p);
        else live.Points[1] = p;
        canvas.Invalidate();
    }

    void Up(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (tool == Tool.Select && typing != null)
        {
            // The release of a double-click that opened text for retyping.
            // `live` stays on that text so it is not drawn under the box.
            grabbed = null;
            handle = null;
            return;
        }
        if (tool == Tool.Select)
        {
            grabbed = null;
            handle = null;
            live = null;
            if (moved) Commit();
            else Rebuild();
            return;
        }
        if (live == null) return;
        var done = live;
        live = null;
        if (done.Degenerate)
        {
            canvas.Invalidate();
            return;
        }
        if (tool == Tool.Cut)
        {
            // Shown and confirmed rather than cut at once: what goes is easy
            // to misjudge while dragging.
            pending = Cut.Across(done.Points[0], done.Points[1], crop);
            status.Text = pending != null ? "Enter or click the band to cut it out, Esc to keep it" : "";
            canvas.Invalidate();
            return;
        }
        if (tool == Tool.Crop)
        {
            var box = Rectangle.Round(RectangleF.FromLTRB(
                Math.Min(done.Points[0].X, done.Points[1].X), Math.Min(done.Points[0].Y, done.Points[1].Y),
                Math.Max(done.Points[0].X, done.Points[1].X), Math.Max(done.Points[0].Y, done.Points[1].Y)));
            box.Intersect(crop);
            if (box.Width < 2 || box.Height < 2) return;
            crop = box;
            Commit();
            SetZoom(Fit());
            // One crop is the usual; going back to drawing saves a click.
            SetTool(Tool.Arrow);
            return;
        }
        shapes.Add(done);
        Commit();
    }

    /// Double-clicking text with the select tool edits it.
    void DoubleClicked(object? sender, MouseEventArgs e)
    {
        if (tool != Tool.Select || selected is not { Kind: Kind.Text } text) return;
        selected = null;
        grabbed = null;
        // Left in the list and only hidden while it is retyped, so Esc puts it
        // back as it was and undo still lines up with history.
        editing = text;
        live = text;
        Rebuild();
        colour = text.Colour;
        width = text.Width;
        UpdateBar();
        StartTyping(text.Points[0], text.Text);
    }

    // Text is typed into a real text box laid over the image, so selection,
    // cursor keys, paste and IME all work, and becomes a shape when it is done.

    void StartTyping(PointF p, string initial)
    {
        typingAt = p;
        var probe = new Shape { Width = width };
        typing = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", probe.FontSize * zoom, FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = colour,
            BackColor = colour.GetBrightness() > 0.6f ? Color.FromArgb(0x30, 0x30, 0x30) : Color.White,
            Location = Point.Round(ToScreen(p)),
            Text = initial,
        };
        typing.TextChanged += (_, _) => SizeTyping();
        typing.KeyDown += (_, e) =>
        {
            // Enter places it; Shift+Enter is a new line.
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                CommitText();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                CancelText();
            }
        };
        typing.Leave += (_, _) => CommitText();
        canvas.Controls.Add(typing);
        SizeTyping();
        typing.Focus();
        typing.SelectAll();
    }

    void SizeTyping()
    {
        if (typing == null) return;
        var size = TextRenderer.MeasureText(typing.Text.Length == 0 ? "Text" : typing.Text + "M", typing.Font);
        typing.Size = new Size(size.Width + 8, size.Height + 8);
    }

    void CommitText()
    {
        if (typing == null) return;
        var text = typing.Text.TrimEnd('\r', '\n');
        var original = editing;
        CancelText();
        if (original != null)
        {
            // Retyped: changed in place, or deleted by emptying it.
            if (text == original.Text && colour == original.Colour && width == original.Width) return;
            if (text.Length == 0) shapes.Remove(original);
            else
            {
                original.Text = text;
                original.Colour = colour;
                original.Width = width;
            }
            Commit();
            return;
        }
        if (text.Length == 0) return;
        shapes.Add(new Shape { Kind = Kind.Text, Colour = colour, Width = width, Points = [typingAt], Text = text });
        Commit();
    }

    void CancelText()
    {
        if (typing == null) return;
        var box = typing;
        // Cleared first: removing a focused control raises Leave, which would
        // otherwise commit the text a second time.
        typing = null;
        canvas.Controls.Remove(box);
        box.Font.Dispose();
        box.Dispose();
        if (editing != null)
        {
            editing = null;
            live = null;
            Rebuild();
        }
        canvas.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        if (typing != null) return base.ProcessCmdKey(ref msg, keys);
        // Mid-drag, only Esc: an undo or a delete would pull the shape being
        // dragged out from under the mouse, and the release would then commit
        // a step that no longer matches anything.
        if ((grabbed != null || (live != null && live != editing)) && keys != Keys.Escape) return true;
        switch (keys)
        {
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y or Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.C: Copy(); return true;
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Control | Keys.Oemplus or Keys.Control | Keys.Add: SetZoom(zoom * 1.25f); return true;
            case Keys.Control | Keys.OemMinus or Keys.Control | Keys.Subtract: SetZoom(zoom * 0.8f); return true;
            case Keys.Control | Keys.D0 or Keys.Control | Keys.NumPad0: SetZoom(Fit()); return true;
            case Keys.Enter when pending is { } cut:
                CutOut(cut);
                return true;
            case Keys.Delete or Keys.Back when selected != null:
                shapes.Remove(selected);
                selected = null;
                Commit();
                return true;
            case Keys.Escape:
                if (grabbed != null)
                {
                    // Mid-move: put it back where the move started.
                    grabbed = null;
                    handle = null;
                    live = null;
                    Restore(changes: false);
                }
                else if (live != null) { live = null; canvas.Invalidate(); }
                else if (pending != null) Uncut();
                else if (selected != null) { selected = null; canvas.Invalidate(); }
                else Close();
                return true;
        }
        foreach (var (t, _, key, _) in Tools)
            if (keys == key) { SetTool(t); return true; }
        return base.ProcessCmdKey(ref msg, keys);
    }

    // Painting.

    void PaintCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var origin = Origin;
        var view = new Rectangle(origin, canvas.AutoScrollMinSize);

        // A hairline round the image, so a capture as dark (or light) as the
        // background still shows where it ends.
        using (var edge = new Pen(Color.FromArgb(0x30, Ink)))
            g.DrawRectangle(edge, view.X - 1, view.Y - 1, view.Width + 1, view.Height + 1);

        // Only the part of the image that is inside the repaint is scaled, so
        // a zoomed-in full-screen capture costs what the window shows.
        var clip = Rectangle.Intersect(e.ClipRectangle, view);
        if (!clip.IsEmpty)
        {
            var src = Rectangle.FromLTRB(
                crop.X + (int)Math.Floor((clip.Left - origin.X) / zoom),
                crop.Y + (int)Math.Floor((clip.Top - origin.Y) / zoom),
                crop.X + (int)Math.Ceiling((clip.Right - origin.X) / zoom),
                crop.Y + (int)Math.Ceiling((clip.Bottom - origin.Y) / zoom));
            src.Intersect(crop);
            var dest = RectangleF.FromLTRB(ToScreen(src.Location).X, ToScreen(src.Location).Y,
                ToScreen(new PointF(src.Right, src.Bottom)).X, ToScreen(new PointF(src.Right, src.Bottom)).Y);
            // ponytail: HighQualityBilinear only on the visible part; switch to
            // a pre-scaled cache if fitting a huge capture ever feels slow.
            g.InterpolationMode = zoom < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(cache, dest, src, GraphicsUnit.Pixel);
        }

        g.SetClip(view);
        using var transform = new Matrix(zoom, 0, 0, zoom, origin.X - crop.X * zoom, origin.Y - crop.Y * zoom);
        g.Transform = transform;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        if (tool == Tool.Cut)
        {
            if ((live != null ? Cut.Across(live.Points[0], live.Points[1], crop) : pending) is { } band)
                DrawCutting(g, band);
        }
        else if (live != null && tool == Tool.Crop)
        {
            DrawCropping(g, live);
        }
        else if (live != editing)
        {
            // Text being retyped is hidden; the text box shows it.
            live?.Draw(g, source);
        }

        if (selected != null)
        {
            using var dash = new Pen(Color.FromArgb(0x3d, 0x8b, 0xfd), 1.5f / zoom) { DashStyle = DashStyle.Dash };
            var handles = selected.Handles;
            if (handles.Length == 0)
            {
                var b = selected.Bounds;
                b.Inflate(4 / zoom, 4 / zoom);
                g.DrawRectangle(dash, b.X, b.Y, b.Width, b.Height);
            }
            using var edge = new Pen(Color.FromArgb(0x3d, 0x8b, 0xfd), 1.5f / zoom);
            var half = HandleSize / 2 / zoom;
            foreach (var h in handles)
            {
                var at = selected.HandleAt(h);
                var square = new RectangleF(at.X - half, at.Y - half, half * 2, half * 2);
                g.FillRectangle(Brushes.White, square);
                g.DrawRectangle(edge, square.X, square.Y, square.Width, square.Height);
            }
        }
    }

    /// The band in red across the whole image, edged where the two sides
    /// will meet.
    void DrawCutting(Graphics g, Cut band)
    {
        var box = band.Columns
            ? new RectangleF(band.At, crop.Top, band.Length, crop.Height)
            : new RectangleF(crop.Left, band.At, crop.Width, band.Length);
        using var shade = new SolidBrush(Color.FromArgb(110, 0xe5, 0x48, 0x4d));
        g.FillRectangle(shade, box);
        using var edge = new Pen(Color.White, 1.5f / zoom) { DashStyle = DashStyle.Dash };
        if (band.Columns)
        {
            g.DrawLine(edge, box.Left, box.Top, box.Left, box.Bottom);
            g.DrawLine(edge, box.Right, box.Top, box.Right, box.Bottom);
        }
        else
        {
            g.DrawLine(edge, box.Left, box.Top, box.Right, box.Top);
            g.DrawLine(edge, box.Left, box.Bottom, box.Right, box.Bottom);
        }
    }

    void Uncut()
    {
        pending = null;
        status.Text = "";
        canvas.Invalidate();
    }

    void DrawCropping(Graphics g, Shape shape)
    {
        var box = RectangleF.FromLTRB(
            Math.Min(shape.Points[0].X, shape.Points[1].X), Math.Min(shape.Points[0].Y, shape.Points[1].Y),
            Math.Max(shape.Points[0].X, shape.Points[1].X), Math.Max(shape.Points[0].Y, shape.Points[1].Y));
        using var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        using var outside = new Region(crop);
        outside.Exclude(box);
        g.FillRegion(shade, outside);
        using var edge = new Pen(Color.White, 1.5f / zoom) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
    }

    // Output.

    Bitmap Export()
    {
        CommitText();
        selected = null;
        live = null;
        Rebuild();
        return cache.Clone(crop, PixelFormat.Format32bppArgb);
    }

    void Copy()
    {
        try
        {
            using var image = Export();
            Clip.Copy(image, Handle);
            dirty = false;
            status.Text = "Copied";
        }
        catch (Exception e) when (e is InvalidOperationException or OutOfMemoryException)
        {
            status.Text = e.Message;
        }
    }

    /// Where this editor has saved to, once it has. Later saves go there
    /// without asking: it is this session's own file.
    string? savedTo;

    /// A capture that was never saved gets the usual name in the screenshots
    /// folder. One opened from a file asks first: overwrite it, or save beside
    /// it as "(edited)". After that, saving again updates the same file.
    ///
    /// The question is a task dialog, not the Save As dialog: the common file
    /// dialog loads shell extensions into the process, and they stay after it
    /// closes.
    void Save()
    {
        try
        {
            var target = savedTo ?? Target();
            if (target == null) return;
            using var image = Export();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Written beside it and moved over, so a failed write never leaves
            // half a file where a good screenshot was.
            var partial = target + ".partial";
            try
            {
                image.Save(partial, FormatFor(target));
                File.Move(partial, target, overwrite: true);
            }
            finally
            {
                // Its own temporary file, never the user's: gone whether the
                // save worked or not.
                if (File.Exists(partial)) File.Delete(partial);
            }
            savedTo = target;
            dirty = false;
            status.Text = $"Saved {Path.GetFileName(target)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            status.Text = e.Message;
        }
    }

    string? Target()
    {
        if (path == null) return Tray.UniquePath(folder, $"ScreenX_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
        var beside = Tray.UniquePath(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (edited)",
            Path.GetExtension(path));
        var overwrite = new TaskDialogCommandLinkButton($"Overwrite {Path.GetFileName(path)}", "Replace the file with this edited version.");
        var alongside = new TaskDialogCommandLinkButton($"Save alongside as {Path.GetFileName(beside)}", "Keep the original as it is.");
        var page = new TaskDialogPage
        {
            Caption = "ScreenX",
            Heading = "This screenshot is already saved",
            Buttons = { overwrite, alongside, TaskDialogButton.Cancel },
            DefaultButton = alongside,
        };
        var answer = TaskDialog.ShowDialog(this, page);
        return answer == overwrite ? path : answer == alongside ? beside : null;
    }

    /// By extension, so overwriting a JPEG writes a JPEG.
    static ImageFormat FormatFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".bmp" => ImageFormat.Bmp,
        ".gif" => ImageFormat.Gif,
        _ => ImageFormat.Png,
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CommitText();
        if (dirty && e.CloseReason == CloseReason.UserClosing
            && MessageBox.Show(this, "Close without copying or saving your changes?", "ScreenX",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// A closed modeless form is disposed by WinForms, so this is where the
    /// memory comes back. Not a BeginInvoke from OnFormClosed: that posts to
    /// the window being destroyed, and the message is lost with it.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (source != original) source.Dispose();
            original.Dispose();
            cache.Dispose();
            // A ToolStrip does not dispose the images it was given.
            foreach (ToolStripItem item in bar.Items) item.Image?.Dispose();
        }
        base.Dispose(disposing);
        // `ScreenX.` because a Form has a `Capture` property of its own.
        if (disposing) ScreenX.Capture.GiveMemoryBack();
    }
}

/// The scrolling surface the image is drawn on.
///
/// A ScrollableControl rather than a large control inside a scrolling panel:
/// a control is double-buffered at its own size, and a capture zoomed in eight
/// times would ask for a back buffer of hundreds of megabytes on every paint.
/// This one is only ever the size of the window.
sealed class Canvas : ScrollableControl
{
    public event Action<int, Point>? ZoomWheel;

    public Canvas()
    {
        AutoScroll = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            ZoomWheel?.Invoke(e.Delta, e.Location);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    // Scrolling moves the image under the pointer, so the old pixels are wrong.
    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }
}

/// A flat toolbar. The system renderer frames each checked button in a raised
/// box and greys disabled icons to near invisible on a dark bar.
sealed class BarRenderer(Color surface, Color ink, ToolStripItem primary) : ToolStripRenderer
{
    static readonly Color Accent = Color.FromArgb(0x3d, 0x8b, 0xfd);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(0x20, ink));
        var b = e.ToolStrip.ClientRectangle;
        e.Graphics.DrawLine(pen, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var button = (ToolStripButton)e.Item;
        var fill = button == primary
                ? (button.Pressed ? ControlPaint.Dark(Accent, 0.05f) : button.Selected ? ControlPaint.Light(Accent, 0.2f) : Accent)
            : button.Pressed ? Color.FromArgb(0x30, ink)
            : button.Checked ? Color.FromArgb(0x60, Accent)
            : button.Selected ? Color.FromArgb(0x18, ink)
            : Color.Empty;
        if (fill.IsEmpty) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        var radius = e.Item.Height / 5;
        g.FillRoundedRectangle(brush, new Rectangle(Point.Empty, e.Item.Size), new Size(radius, radius));
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(0x30, ink));
        var x = e.Item.Width / 2;
        e.Graphics.DrawLine(pen, x, e.Item.Height / 4, x, e.Item.Height * 3 / 4);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item == primary) e.TextColor = Color.White;
        else if (e.Item is ToolStripButton) e.TextColor = ink;
        base.OnRenderItemText(e);
    }

    /// Faded rather than greyed, so Undo with nothing to undo still reads as
    /// Undo.
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image == null) return;
        if (e.Item.Enabled)
        {
            e.Graphics.DrawImage(e.Image, e.ImageRectangle);
            return;
        }
        using var faded = new ImageAttributes();
        faded.SetColorMatrix(new ColorMatrix { Matrix33 = 0.35f });
        var r = e.ImageRectangle;
        e.Graphics.DrawImage(e.Image, r, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, faded);
    }
}
