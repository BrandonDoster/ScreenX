// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenX;

static class Capture
{
    /// The screen inside `area`, in physical pixels.
    ///
    /// CaptureBlt is what brings layered windows along: tooltips, menus and
    /// anything else drawn with per-pixel alpha are missing without it.
    public static Frame Grab(Rectangle area)
    {
        var frame = new Frame(area.Width, area.Height);
        var screen = Gdi.GetDC(IntPtr.Zero);
        try
        {
            if (!Gdi.BitBlt(frame.Dc, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Gdi.SRCCOPY | Gdi.CAPTUREBLT))
            {
                frame.Dispose();
                throw new InvalidOperationException($"could not read the screen (error {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            Gdi.ReleaseDC(IntPtr.Zero, screen);
        }
        return frame;
    }

    /// Visible top-level windows, front-most first, relative to `area`.
    ///
    /// The extended frame bounds are the window as drawn, without the invisible
    /// resize border Windows 10 and later put around it. `GetWindowRect`
    /// includes that border, so a snapped window would take a strip of
    /// whatever is behind it on three sides.
    public static List<Rectangle> Windows(Rectangle area)
    {
        var found = new List<Rectangle>();
        var name = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            // Cloaked: suspended UWP apps and windows on other virtual desktops.
            // Visible as far as the API is concerned, not on screen.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            // Click-through windows are overlays (GPU, game, recording), often
            // full-screen and invisible; snapping to one selects nothing useful.
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;
            name.Clear();
            GetClassName(hwnd, name, name.Capacity);
            // The desktop. Falling through to the monitor does the same job.
            if (name.ToString() is "Progman" or "WorkerW") return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;

            var rect = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), area);
            if (rect.Width < 48 || rect.Height < 48) return true;
            rect.Offset(-area.X, -area.Y);
            found.Add(rect);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// Return the capture's memory to the system once it is finished with.
    ///
    /// The pixels are native allocations and are already freed by Dispose;
    /// this trims the working set so Task Manager shows the idle footprint
    /// rather than the high-water mark of the last screenshot.
    public static void GiveMemoryBack()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}

/// Pixels in a GDI DIB section, selected into a memory DC.
///
/// Not a GDI+ `Bitmap`: that goes through a temporary copy every time an HDC
/// is taken from it, and `DrawImage` converts its format on every paint. With
/// a DIB section the screen is read straight into these pixels and painted
/// straight out of them, and the pixels are still addressable for the dimming.
sealed unsafe class Frame : IDisposable
{
    public readonly int Width, Height;
    public readonly IntPtr Dc;
    readonly IntPtr bitmap, previous;
    readonly uint* bits;

    public Frame(int width, int height)
    {
        Width = width;
        Height = height;
        // Negative height: rows run top to bottom, as everything else assumes.
        var header = new Gdi.BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        Dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        bitmap = Gdi.CreateDIBSection(Dc, ref header, 0, out var pixels, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            Gdi.DeleteDC(Dc);
            throw new OutOfMemoryException($"could not allocate a {width} x {height} capture");
        }
        bits = (uint*)pixels;
        previous = Gdi.SelectObject(Dc, bitmap);
    }

    /// 32 bpp rows carry no padding, so the image is one run of pixels.
    public Span<uint> Pixels => new(bits, Width * Height);

    /// A copy of `rect` as a GDI+ bitmap, for saving and the clipboard.
    public Bitmap ToBitmap(Rectangle rect)
    {
        using var view = new Bitmap(rect.Width, rect.Height, Width * 4, PixelFormat.Format32bppRgb,
            (IntPtr)(bits + (long)rect.Y * Width + rect.X));
        return view.Clone(new Rectangle(Point.Empty, rect.Size), PixelFormat.Format32bppRgb);
    }

    public void CopyToClipboard(Rectangle rect, IntPtr owner) => Clip.Copy(Pixels, Width, rect, owner);

    public void Dispose()
    {
        Gdi.SelectObject(Dc, previous);
        Gdi.DeleteObject(bitmap);
        Gdi.DeleteDC(Dc);
    }
}

static unsafe class Clip
{
    const uint CF_DIB = 8, GMEM_MOVEABLE = 2;

    public static void Copy(Bitmap image, IntPtr owner)
    {
        var whole = new Rectangle(Point.Empty, image.Size);
        var data = image.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            // 32 bpp, so the stride is the width and the rows are one run.
            Copy(new Span<uint>((void*)data.Scan0, image.Width * image.Height), image.Width, whole, owner);
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    /// Put `rect` of an image `width` pixels wide on the clipboard as CF_DIB;
    /// Windows derives the other bitmap formats from it.
    ///
    /// Not `Clipboard.SetImage` or `SetDataObject`: both leave a GDI bitmap of
    /// the whole image inside this process. Three full-screen copies took
    /// private memory from 11 MB to 97 MB. Handed over as an HGLOBAL, the
    /// copy belongs to the clipboard.
    public static void Copy(Span<uint> pixels, int width, Rectangle rect, IntPtr owner)
    {
        var size = DibSize(rect);
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)size);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException("could not allocate the clipboard copy");
        var p = (byte*)GlobalLock(memory);
        if (p == null)
        {
            GlobalFree(memory);
            throw new OutOfMemoryException("could not lock the clipboard copy");
        }
        PackDib(pixels, width, rect, new Span<byte>(p, checked((int)size)));
        GlobalUnlock(memory);

        // A clipboard manager can be holding it for a moment.
        var open = false;
        for (var attempt = 0; attempt < 10 && !(open = OpenClipboard(owner)); attempt++) Thread.Sleep(20);
        if (!open)
        {
            GlobalFree(memory);
            throw new InvalidOperationException("the clipboard is in use by another program");
        }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_DIB, memory) == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new InvalidOperationException("the clipboard refused the image");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static long DibSize(Rectangle rect) => 40 + (long)rect.Width * rect.Height * 4;

    /// A CF_DIB: a BITMAPINFOHEADER, then the rows of `rect`, bottom row
    /// first. `pixels` is an image `width` pixels wide.
    public static void PackDib(ReadOnlySpan<uint> pixels, int width, Rectangle rect, Span<byte> dib)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Right > width || (long)rect.Bottom * width > pixels.Length)
            throw new ArgumentOutOfRangeException(nameof(rect), "outside the image");
        if (dib.Length < DibSize(rect)) throw new ArgumentException("buffer too small", nameof(dib));
        // Bottom-up: readers of CF_DIB mishandle the top-down form often
        // enough that it is not worth the saved loop.
        var header = new Gdi.BITMAPINFOHEADER { Size = 40, Width = rect.Width, Height = rect.Height, Planes = 1, BitCount = 32 };
        System.Runtime.InteropServices.MemoryMarshal.Write(dib, in header);
        var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dib[40..]);
        for (var y = 0; y < rect.Height; y++)
        {
            var src = pixels.Slice((rect.Y + y) * width + rect.X, rect.Width);
            var dst = to.Slice((rect.Height - 1 - y) * rect.Width, rect.Width);
            // The screen read leaves alpha at zero, and some programs honour
            // it in a 32 bpp DIB and paste a transparent image.
            for (var x = 0; x < src.Length; x++) dst[x] = src[x] | 0xFF000000;
        }
    }

    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
}

static class Gdi
{
    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern int ExcludeClipRect(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern int SelectClipRgn(IntPtr dc, IntPtr region);
}
