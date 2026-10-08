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
    List<Rectangle> windows = [];
    Rectangle? selection, chosen;
    Point? pressed;
    bool dragging;
    Stopwatch? clock;
    readonly Pen border = new(Color.FromArgb(0x3d, 0x8b, 0xfd));

    /// From the hotkey to the overlay's first frame on screen.
    public long ShownAfterMs { get; private set; }

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
    public Rectangle? Choose(Frame shot, List<Rectangle> windows, Stopwatch clock)
    {
        this.shot = shot;
        this.windows = windows;
        this.clock = clock;
        dim = Dimmed(shot);
        chosen = null;
        pressed = null;
        dragging = false;
        var area = SystemInformation.VirtualScreen;
        Bounds = area;
        selection = Under(Cursor.Position - (Size)area.Location);
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
    Rectangle? Under(Point point)
    {
        foreach (var window in windows)
            if (window.Contains(point)) return window;
        var area = SystemInformation.VirtualScreen;
        var monitor = Screen.FromPoint(point + (Size)area.Location).Bounds;
        monitor.Offset(-area.X, -area.Y);
        return monitor;
    }

    void Select(Rectangle? next)
    {
        if (next == selection) return;
        if (selection is Rectangle old) Invalidate(Around(old));
        if (next is Rectangle now) Invalidate(Around(now));
        selection = next;
    }

    /// Everything a selection draws: itself, its border, and the size label.
    static Rectangle Around(Rectangle r) =>
        Rectangle.FromLTRB(r.Left - 2, r.Top - 28, Math.Max(r.Right, r.Left + 160) + 2, r.Bottom + 2);

    void Finish(Rectangle? result)
    {
        chosen = result;
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (pressed is not Point from)
        {
            Select(Under(e.Location));
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

    protected override void OnPaint(PaintEventArgs e)
    {
        if (shot == null || dim == null) return;
        var g = e.Graphics;
        var clip = e.ClipRectangle;

        // Dim outside, bright inside, never both over the same pixel, so
        // nothing flickers without a back buffer. A back buffer the size of the
        // virtual screen would be allocated on every paint.
        var hdc = g.GetHdc();
        try
        {
            if (selection is Rectangle cut) Gdi.ExcludeClipRect(hdc, cut.Left, cut.Top, cut.Right, cut.Bottom);
            Gdi.BitBlt(hdc, clip.X, clip.Y, clip.Width, clip.Height, dim.Dc, clip.X, clip.Y, Gdi.SRCCOPY);
            if (selection is Rectangle lit)
            {
                // Back to the update region alone; the system keeps that apart
                // from the clip set above.
                Gdi.SelectClipRgn(hdc, IntPtr.Zero);
                var bright = Rectangle.Intersect(clip, lit);
                if (!bright.IsEmpty)
                    Gdi.BitBlt(hdc, bright.X, bright.Y, bright.Width, bright.Height, shot.Dc, bright.X, bright.Y, Gdi.SRCCOPY);
            }
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }

        if (selection is Rectangle sel)
        {
            g.DrawRectangle(border, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
            var label = new Point(sel.X, sel.Y >= 26 ? sel.Y - 26 : sel.Y + 2);
            TextRenderer.DrawText(g, $" {sel.Width} x {sel.Height} ", Font, label,
                Color.White, Color.FromArgb(32, 32, 32));
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
    static Frame Dimmed(Frame shot)
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
