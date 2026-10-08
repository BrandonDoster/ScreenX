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
    /// a 2000 px wide capture at this limit is about 400 MB while it is built.
    const long MaxPixels = 50_000_000;
    const int MaxSteps = 400;

    public sealed record Outcome(Frame Image, int Steps, string Ended);

    /// `area` is in screen pixels. Returns null if nothing was captured.
    public static async Task<Outcome?> Run(Rectangle area)
    {
        if (area.Width < 16 || area.Height < 64) return null;
        using var border = new Border(area);
        border.Show();

        var restore = Cursor.Position;
        // Over the area, so the wheel reaches it, but near the right edge,
        // away from content that reacts to hover.
        var aim = new Point(area.Right - Math.Min(40, area.Width / 4), area.Y + area.Height / 2);
        Cursor.Position = aim;
        try
        {
            // Let whatever the overlay covered finish repainting.
            await Task.Delay(150);
            // Wide enough for a scrollbar at 200% scale; ShareX settles on a
            // similar margin.
            var stitcher = new Stitcher(area.Width, area.Height, ignoreRight: Math.Max(24, area.Width / 20));
            stitcher.Add(await Settled(area));

            var notches = 3;
            var ended = "it reached the step limit";
            var steps = 0;
            for (; steps < MaxSteps; steps++)
            {
                if (Escape()) { ended = "stopped"; break; }
                // The user moving the mouse means they want it back.
                if (Cursor.Position != aim) { ended = "the mouse moved"; break; }
                Wheel(-notches);
                var frame = await Settled(area);
                var result = stitcher.Add(frame);
                if (result == Stitcher.Result.End) { ended = "it reached the end"; break; }
                if (result == Stitcher.Result.Lost) { ended = "it lost its place; try a smaller area"; break; }
                // Aim for a third to two thirds of the area per step: enough
                // overlap to match, not so little that it takes forever.
                var shift = stitcher.LastShift;
                if (shift < area.Height / 3 && notches < 10) notches++;
                else if (shift > area.Height * 2 / 3 && notches > 1) notches--;
                if ((long)stitcher.TotalHeight * area.Width > MaxPixels) { ended = "it reached the size limit"; break; }
            }

            var pixels = stitcher.Compose();
            var image = new Frame(area.Width, stitcher.TotalHeight);
            pixels.CopyTo(image.Pixels);
            return new Outcome(image, steps, ended);
        }
        finally
        {
            Cursor.Position = restore;
        }
    }

    /// A capture of `area` once it has stopped changing: smooth scrolling and
    /// lazy loading both keep painting after the wheel event is handled.
    static async Task<uint[]> Settled(Rectangle area)
    {
        var clock = Stopwatch.StartNew();
        var last = Grab(area);
        while (clock.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(60);
            var next = Grab(area);
            if (next.AsSpan().SequenceEqual(last)) return next;
            last = next;
        }
        return last;
    }

    static uint[] Grab(Rectangle area)
    {
        using var frame = Capture.Grab(area);
        return frame.Pixels.ToArray();
    }

    static bool Escape() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;

    static void Wheel(int notches)
    {
        var input = new INPUT
        {
            Type = 0, // INPUT_MOUSE
            Mouse = new MOUSEINPUT { MouseData = notches * 120, Flags = 0x0800 }, // MOUSEEVENTF_WHEEL
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

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
