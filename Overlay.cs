// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Diagnostics;
using System.Numerics;

namespace ScreenX;

/// The selection overlay: the frozen capture across every monitor, dimmed,
/// with the part that will be taken shown at full brightness.
///
/// Hovering picks the window under the pointer, or the monitor if there is
/// none, and a click takes it. Dragging draws a rectangle instead. Esc or a
/// right click cancels.
///
/// Created once and reused: the form is hidden between screenshots, which in
/// plain Win32 costs nothing and keeps its message loop working.
sealed class Overlay : Form
{
    const int DragThreshold = 4;
    const int WM_DPICHANGED = 0x02E0;

    Frame? shot, dim;
    List<WindowArea> windows = [];
    /// Corner radius of the window the selection is snapped to; 0 for a drag.
    int radius;
    Rectangle? selection, chosen;
    Rectangle screen;
    Point? pointer;
    Point? pressed;
    bool dragging;
    Stopwatch? clock;
    readonly Pen border = new(Color.FromArgb(0x3d, 0x8b, 0xfd));

    /// From the hotkey to the overlay's first frame on screen.
    public long ShownAfterMs { get; private set; }

    /// Corner radius of what was chosen, when it was a whole window.
    public int ChosenRadius { get; private set; }

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Cross;
        // Every pixel is painted by OnPaint, so the background erase would only
        // be a flash of the form colour.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

    /// Show the overlay over `shot` and return what was chosen, in the shot's
    /// own pixels, or null if it was cancelled.
    ///
    /// `screen` is the rectangle `shot` was read from. It is passed in rather
    /// than read again, because the layout can change in between (a monitor
    /// plugged in) and the overlay must match the pixels it is showing.
    public Rectangle? Choose(Frame shot, List<WindowArea> windows, Stopwatch clock, Rectangle screen)
    {
        this.shot = shot;
        this.windows = windows;
        this.clock = clock;
        this.screen = screen;
        dim = Dimmed(shot);
        chosen = null;
        pressed = null;
        dragging = false;
        Bounds = screen;
        pointer = Cursor.Position - (Size)screen.Location;
        (selection, radius) = Under(pointer.Value);
        ChosenRadius = 0;
        try
        {
            ShowDialog();
        }
        finally
        {
            dim.Dispose();
            dim = null;
            this.shot = null;
            this.windows = [];
        }
        return chosen;
    }

    /// The window under `point`, front-most first, or the monitor under it.
    (Rectangle, int radius) Under(Point point)
    {
        foreach (var window in windows)
            if (window.Rect.Contains(point)) return (window.Rect, window.Radius);
        var monitor = Screen.FromPoint(point + (Size)screen.Location).Bounds;
        monitor.Offset(-screen.X, -screen.Y);
        return (monitor, 0);
    }

    void Select(Rectangle? next, int corners = 0)
    {
        radius = corners;
        if (next == selection) return;
        if (selection is Rectangle old) Invalidate(Around(old));
        if (next is Rectangle now) Invalidate(Around(now));
        selection = next;
    }

    /// Everything a selection draws: itself, its border, and the size label,
    /// which is measured because it grows with the display scale.
    Rectangle Around(Rectangle r)
    {
        var label = TextRenderer.MeasureText(" 99999 x 99999 ", Font);
        var height = Math.Max(label.Height, Font.Height) + 4;
        return Rectangle.FromLTRB(r.Left - 2, r.Top - height - 2,
            Math.Max(r.Right, r.Left + label.Width) + 2, Math.Max(r.Bottom, r.Top + height + 2) + 2);
    }

    void Finish(Rectangle? result)
    {
        // Never more than the capture holds.
        var whole = result;
        if (result is Rectangle r && shot != null)
        {
            r.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
            result = r.Width > 0 && r.Height > 0 ? r : null;
        }
        chosen = result;
        // Rounded only when it is still exactly the window that was snapped to.
        ChosenRadius = !dragging && result == whole && result == selection ? radius : 0;
        // Ends ShowDialog. A modal form is hidden by this, not disposed.
        DialogResult = DialogResult.OK;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The hotkey gave this process the right to take the foreground, so
        // this is honoured rather than turned into a flashing taskbar button.
        Activate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) pressed = e.Location;
        else if (e.Button == MouseButtons.Right) Finish(null);
    }

    /// Pixels either side of the pointer shown in the magnifier.
    const int Reach = 7;
    int Zoom => 8 * Math.Max(1, DeviceDpi / 96);

    /// Where the magnifier goes for a pointer at `p`: below and right of it,
    /// flipped to the other side at the edges of the monitor it is on.
    Rectangle Magnifier(Point p)
    {
        var size = (Reach * 2 + 1) * Zoom;
        var label = Font.Height + 6;
        var monitor = Screen.FromPoint(p + (Size)screen.Location).Bounds;
        monitor.Offset(-screen.X, -screen.Y);
        var x = p.X + 24 + size > monitor.Right ? p.X - 24 - size : p.X + 24;
        var y = p.Y + 24 + size + label > monitor.Bottom ? p.Y - 24 - size - label : p.Y + 24;
        return new Rectangle(x, y, size, size + label);
    }

    void MovePointer(Point p)
    {
        if (pointer == p) return;
        if (pointer is Point old) Invalidate(Rectangle.Inflate(Magnifier(old), 2, 2));
        pointer = p;
        Invalidate(Rectangle.Inflate(Magnifier(p), 2, 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        MovePointer(e.Location);
        if (pressed is not Point from)
        {
            var (under, corners) = Under(e.Location);
            Select(under, corners);
            return;
        }
        if (!dragging && Math.Abs(e.X - from.X) < DragThreshold && Math.Abs(e.Y - from.Y) < DragThreshold) return;
        dragging = true;
        // +1 so the pixel under the pointer is included: a drag to the last
        // column of the screen takes the last column.
        Select(Rectangle.FromLTRB(
            Math.Min(from.X, e.X), Math.Min(from.Y, e.Y),
            Math.Max(from.X, e.X) + 1, Math.Max(from.Y, e.Y) + 1));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pressed != null) Finish(selection);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Finish(null);
        else if (e.KeyCode == Keys.Enter) Finish(selection);
    }

    /// Arrow keys move the pointer a pixel at a time (ten with Shift), so an
    /// edge can be placed exactly with the magnifier. Handled here because a
    /// form treats arrows as focus navigation before KeyDown sees them.
    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        var step = (keys & Keys.Shift) != 0 ? 10 : 1;
        var move = (keys & Keys.KeyCode) switch
        {
            Keys.Left => new Size(-step, 0),
            Keys.Right => new Size(step, 0),
            Keys.Up => new Size(0, -step),
            Keys.Down => new Size(0, step),
            _ => Size.Empty,
        };
        if (move.IsEmpty) return base.ProcessCmdKey(ref msg, keys);
        Cursor.Position += move;
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (shot == null || dim == null) return;
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        Rectangle? magnifier = pointer is Point at ? Magnifier(at) : null;

        // Dim outside, bright inside, magnifier on top, and never two of them
        // over the same pixel, so nothing flickers without a back buffer. A back
        // buffer the size of the virtual screen would be allocated every paint.
        var hdc = g.GetHdc();
        try
        {
            void KeepOut(Rectangle? r)
            {
                if (r is Rectangle k) Gdi.ExcludeClipRect(hdc, k.Left, k.Top, k.Right, k.Bottom);
            }
            KeepOut(selection);
            KeepOut(magnifier);
            Gdi.BitBlt(hdc, clip.X, clip.Y, clip.Width, clip.Height, dim.Dc, clip.X, clip.Y, Gdi.SRCCOPY);
            // Back to the update region alone; the system keeps that apart
            // from the clip set here.
            Gdi.SelectClipRgn(hdc, IntPtr.Zero);
            if (selection is Rectangle lit)
            {
                KeepOut(magnifier);
                var bright = Rectangle.Intersect(clip, lit);
                if (!bright.IsEmpty)
                    Gdi.BitBlt(hdc, bright.X, bright.Y, bright.Width, bright.Height, shot.Dc, bright.X, bright.Y, Gdi.SRCCOPY);
                Gdi.SelectClipRgn(hdc, IntPtr.Zero);
            }
            if (magnifier is Rectangle m && pointer is Point p && m.IntersectsWith(clip))
            {
                // Black where the reach goes past the edge of the capture:
                // reading outside a DIB gives whatever is there.
                Gdi.PatBlt(hdc, m.X, m.Y, m.Width, m.Width, Gdi.BLACKNESS);
                var want = new Rectangle(p.X - Reach, p.Y - Reach, Reach * 2 + 1, Reach * 2 + 1);
                var have = Rectangle.Intersect(want, new Rectangle(0, 0, shot.Width, shot.Height));
                if (!have.IsEmpty)
                {
                    var zoom = Zoom;
                    Gdi.SetStretchBltMode(hdc, Gdi.COLORONCOLOR);
                    Gdi.StretchBlt(hdc, m.X + (have.X - want.X) * zoom, m.Y + (have.Y - want.Y) * zoom,
                        have.Width * zoom, have.Height * zoom, shot.Dc, have.X, have.Y, have.Width, have.Height, Gdi.SRCCOPY);
                }
            }
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }

        if (magnifier is Rectangle keep) g.ExcludeClip(keep);
        if (selection is Rectangle sel)
        {
            g.DrawRectangle(border, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
            var height = Font.Height + 4;
            // Above the selection, or inside it when there is no room above.
            var label = new Point(sel.X, sel.Y >= height ? sel.Y - height : sel.Y + 2);
            TextRenderer.DrawText(g, $" {sel.Width} x {sel.Height} ", Font, label,
                Color.White, Color.FromArgb(32, 32, 32));
        }
        g.ResetClip();
        if (magnifier is Rectangle box && pointer is Point here)
        {
            var zoom = Zoom;
            g.DrawRectangle(Pens.White, box.X, box.Y, box.Width - 1, box.Width - 1);
            // The pixel under the pointer.
            g.DrawRectangle(border, box.X + Reach * zoom, box.Y + Reach * zoom, zoom - 1, zoom - 1);
            var caption = new Rectangle(box.X, box.Y + box.Width, box.Width, box.Height - box.Width);
            var screenPoint = here + (Size)screen.Location;
            using (var back = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillRectangle(back, caption);
            TextRenderer.DrawText(g, $"{screenPoint.X}, {screenPoint.Y}", Font, caption, Color.White,
                Color.FromArgb(32, 32, 32), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (clock != null)
        {
            ShownAfterMs = clock.ElapsedMilliseconds;
            clock = null;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // The overlay spans monitors of different scale. Left to itself, WinForms
        // would resize it to the scale of whichever one it is judged to be on.
        if (m.Msg == WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    /// The capture at half brightness, made once rather than blended per frame.
    internal static Frame Dimmed(Frame shot)
    {
        var dim = new Frame(shot.Width, shot.Height);
        ReadOnlySpan<uint> from = shot.Pixels;
        var to = dim.Pixels;
        // Halving each channel is a shift, once the bit each byte would borrow
        // from its neighbour is masked off.
        var mask = new Vector<uint>(0x7F7F7F7Fu);
        var i = 0;
        for (; i <= from.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            (Vector.ShiftRightLogical(new Vector<uint>(from[i..]), 1) & mask).CopyTo(to[i..]);
        for (; i < from.Length; i++) to[i] = (from[i] >> 1) & 0x7F7F7F7Fu;
        return dim;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) border.Dispose();
        base.Dispose(disposing);
    }
}
