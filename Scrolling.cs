// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenX;

/// Scrolling capture: scroll the area with the mouse wheel, photograph it after
/// each step, and let the `Stitcher` join the photographs.
///
/// Runs on the UI thread with awaits between steps, so the tray and the border
/// keep their message loop and nothing is left running when it ends.
static class Scrolling
{
    /// Enough for a long page, small enough not to take the machine with it:
    /// a 2000 px wide capture at this limit holds about 200 MB of strips and
    /// 200 MB of finished image.
    const long MaxPixels = 50_000_000;
    const int MaxSteps = 400;
    static readonly TimeSpan MaxTime = TimeSpan.FromMinutes(3);

    public sealed record Outcome(Frame Image, int Steps, string Ended);

    /// `area` is in screen pixels. Returns null if nothing was captured.
    public static async Task<Outcome?> Run(Rectangle area, Action<string> log)
    {
        if (area.Width < 16 || area.Height < 64) return null;
        using var border = new Border(area);
        border.Show();
        using var surface = new Frame(area.Width, area.Height);
        // Clears the "pressed since last asked" bit, which an Esc from some
        // earlier moment would otherwise set and stop this before it starts.
        Escape();

        var restore = Cursor.Position;
        // Over the area, so the wheel reaches it, but near the right edge,
        // away from content that reacts to hover.
        var aim = new Point(area.Right - Math.Min(40, area.Width / 4), area.Y + area.Height / 2);
        Cursor.Position = aim;
        // With "scroll inactive windows" turned off, the wheel goes to the
        // focused window, which after the overlay is not necessarily this one.
        SetForegroundWindow(GetAncestor(WindowFromPoint(aim), GA_ROOT));
        try
        {
            // Let whatever the overlay covered finish repainting.
            await Task.Delay(150);
            // Wide enough for a scrollbar at 200% scale; ShareX settles on a
            // similar margin.
            var ignoreRight = Math.Max(24, area.Width / 20);
            var stitcher = new Stitcher(area.Width, area.Height, ignoreRight);
            var compare = area.Width - Math.Min(ignoreRight, area.Width / 2);
            var current = await Settled(area, surface, null, compare);
            if (current == null) return null;
            stitcher.Add(current);

            var notches = 2;
            var retries = 0;
            var ended = "it reached the step limit";
            var steps = 0;
            var clock = Stopwatch.StartNew();
            for (; steps < MaxSteps; steps++)
            {
                if (Escape()) { ended = "stopped"; break; }
                // The user moving the mouse means they want it back.
                if (Cursor.Position != aim) { ended = "the mouse moved"; break; }
                if (clock.Elapsed > MaxTime) { ended = "it reached the time limit"; break; }
                if (!Wheel(-notches))
                {
                    ended = "Windows refused the scroll (the window may be running as administrator)";
                    break;
                }
                var step = Stopwatch.StartNew();
                var frame = await Settled(area, surface, current, compare);
                if (frame == null) { ended = "stopped"; break; }
                // Matching a tall area can take a noticeable fraction of a
                // second, and the stitcher is pure, so it runs off the UI thread.
                var result = await Task.Run(() => stitcher.Add(frame));
                log($"scroll step {steps}: {notches} notches, {result}, shift {stitcher.LastShift}, {step.ElapsedMilliseconds} ms");
                if (result == Stitcher.Result.End) { ended = "it reached the end"; break; }
                if (result == Stitcher.Result.Lost)
                {
                    // Usually a step too big to overlap. Scroll back, halve it,
                    // and try again from the frame that is still the last good one.
                    if (notches == 1 || retries++ == 2) { ended = "it lost its place; try a smaller area"; break; }
                    Wheel(notches);
                    notches = Math.Max(1, notches / 2);
                    if (await Settled(area, surface, frame, compare) == null) { ended = "stopped"; break; }
                    continue;
                }
                current = frame;
                retries = 0;
                // Aim for a third to four fifths of what can still be matched:
                // enough overlap to be sure, not so little it takes forever.
                var shift = stitcher.LastShift;
                if (shift < stitcher.MaxShift / 3 && notches < 15) notches++;
                else if (shift > stitcher.MaxShift * 4 / 5 && notches > 1) notches--;
                if ((long)stitcher.TotalHeight * area.Width > MaxPixels) { ended = "it reached the size limit"; break; }
            }

            log($"scrolling capture ended after {steps} steps: {ended}");
            var image = new Frame(area.Width, stitcher.TotalHeight);
            stitcher.Compose(image.Pixels);
            return new Outcome(image, steps, ended);
        }
        finally
        {
            // Not if the user has taken the mouse back: pulling it away from
            // them again is worse than leaving it where it is.
            if (Cursor.Position == aim) Cursor.Position = restore;
        }
    }

    /// A capture of `area` once it has stopped changing: smooth scrolling and
    /// lazy loading both keep painting after the wheel event is handled. Null
    /// if Esc was pressed while waiting.
    ///
    /// With `before`, it first waits for the picture to differ from it. A slow
    /// application (Word, Visual Studio) may not have repainted within the
    /// first poll, and its unscrolled frame would read as the end of the page.
    /// Only the columns the stitcher matches are compared, so a scrollbar
    /// fading in and out does not count as change.
    static async Task<uint[]?> Settled(Rectangle area, Frame surface, uint[]? before, int compare)
    {
        var clock = Stopwatch.StartNew();
        var last = new uint[surface.Width * surface.Height];
        Capture.GrabInto(surface, area);
        surface.Pixels.CopyTo(last);
        if (before != null)
        {
            while (Same(last, before, surface.Width, compare) && clock.ElapsedMilliseconds < 800)
            {
                await Task.Delay(40);
                if (Escape()) return null;
                Capture.GrabInto(surface, area);
                surface.Pixels.CopyTo(last);
            }
        }
        while (clock.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(60);
            if (Escape()) return null;
            Capture.GrabInto(surface, area);
            if (Same(surface.Pixels, last, surface.Width, compare)) return last;
            surface.Pixels.CopyTo(last);
        }
        return last;
    }

    /// Equal in the compared columns, allowing a few changed rows: a caret, a
    /// spinner or a GIF would otherwise keep every step waiting its full time.
    static bool Same(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int width, int compare)
    {
        var height = a.Length / width;
        var allowed = Math.Max(2, height / 50);
        for (var y = 0; y < height; y++)
            if (!a.Slice(y * width, compare).SequenceEqual(b.Slice(y * width, compare)) && --allowed < 0)
                return false;
        return true;
    }

    /// Checked rather than polled once per step: a quick tap would otherwise
    /// fall between checks.
    static bool Escape() => (GetAsyncKeyState(0x1B) & 0x8001) != 0;

    /// False if Windows did not accept the input: UIPI blocks it for windows
    /// running elevated, and the scroll then never happens.
    static bool Wheel(int notches)
    {
        var input = new INPUT
        {
            Type = 0, // INPUT_MOUSE
            Mouse = new MOUSEINPUT { MouseData = notches * 120, Flags = 0x0800 }, // MOUSEEVENTF_WHEEL
        };
        return SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 1;
    }

    const uint GA_ROOT = 2;
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int Dx, Dy, MouseData;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    // The union in the real INPUT is as large as its largest member, and
    // SendInput rejects the call if cbSize is wrong. MOUSEINPUT is the largest.
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mouse;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    /// A red frame just outside the area, so it is never in the photograph.
    /// Click-through and never activated, so the target keeps the focus.
    sealed class Border : Form
    {
        const int Thickness = 3;

        public Border(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(0xe5, 0x48, 0x4d);
            AutoScaleMode = AutoScaleMode.None;
            var outer = Rectangle.Inflate(area, Thickness, Thickness);
            Bounds = outer;
            var ring = new Region(new Rectangle(Point.Empty, outer.Size));
            ring.Exclude(new Rectangle(Thickness, Thickness, area.Width, area.Height));
            Region = ring;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
                p.ExStyle |= 0x80 | 0x20 | 0x08000000;
                return p;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Same reason as the overlay: it must not resize itself on a DPI change.
            if (m.Msg == 0x02E0) return;
            base.WndProc(ref m);
        }
    }
}
