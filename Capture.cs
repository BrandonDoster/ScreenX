// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenX;

/// A window the overlay can snap to: where it is, and how round its corners are.
readonly record struct WindowArea(Rectangle Rect, int Radius);

static class Capture
{
    /// The screen inside `area`, in physical pixels.
    ///
    /// CaptureBlt is what brings layered windows along: tooltips, menus and
    /// anything else drawn with per-pixel alpha are missing without it.
    public static Frame Grab(Rectangle area)
    {
        var frame = new Frame(area.Width, area.Height);
        try
        {
            GrabInto(frame, area);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
        return frame;
    }

    /// The same, into a frame that already exists, for a run of captures of
    /// one area that would otherwise allocate a new surface each time.
    public static void GrabInto(Frame frame, Rectangle area)
    {
        if (frame.Width != area.Width || frame.Height != area.Height) throw new ArgumentException("frame and area differ in size");
        var screen = Gdi.GetDC(IntPtr.Zero);
        try
        {
            if (!Gdi.BitBlt(frame.Dc, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Gdi.SRCCOPY | Gdi.CAPTUREBLT))
                throw new InvalidOperationException($"could not read the screen (error {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Gdi.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// Visible top-level windows, front-most first, relative to `area`.
    ///
    /// The extended frame bounds are the window as drawn, without the invisible
    /// resize border Windows 10 and later put around it. `GetWindowRect`
    /// includes that border, so a snapped window would take a strip of
    /// whatever is behind it on three sides.
    public static List<WindowArea> Windows(Rectangle area)
    {
        var found = new List<WindowArea>();
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

            var whole = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            var rect = Rectangle.Intersect(whole, area);
            if (rect.Width < 48 || rect.Height < 48) return true;
            rect.Offset(-area.X, -area.Y);
            // A window cut off by the edge of the screen has square corners
            // where it is cut; only a whole one is rounded.
            found.Add(new WindowArea(rect, rect.Size == whole.Size ? CornerRadius(hwnd) : 0));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// How round Windows 11 draws this window's corners, in pixels: 8 at 100%
    /// for an ordinary window, 4 for one that asked for small corners, none
    /// for a maximised window, one that opted out, or one with no frame.
    static int CornerRadius(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22000 || IsZoomed(hwnd)) return 0;
        DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out int preference, 4);
        const int DONOTROUND = 1, ROUNDSMALL = 3;
        if (preference == DONOTROUND) return 0;
        // With the default preference, Windows rounds framed windows only.
        const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        var style = GetWindowLong(hwnd, GWL_STYLE);
        if (preference == 0 && (style & (WS_CAPTION | WS_THICKFRAME)) == 0) return 0;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        return (int)Math.Round((preference == ROUNDSMALL ? 4 : 8) * scale);
    }

    /// `rect` with transparent corners of `radius`, antialiased. The pixels
    /// outside the curve are still in `frame` for the clipboard's plain copy.
    public static Bitmap Rounded(Frame frame, Rectangle rect, int radius)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, rect.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var to = new Span<uint>((void*)data.Scan0, rect.Width * rect.Height);
                var from = frame.Pixels;
                for (var y = 0; y < rect.Height; y++)
                    from.Slice((rect.Y + y) * frame.Width + rect.X, rect.Width).CopyTo(to.Slice(y * rect.Width, rect.Width));
                Corners(to, rect.Width, rect.Height, radius);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// Alpha for an image `width` x `height` with corners of `radius`: opaque
    /// inside, clear outside, the fraction of each edge pixel the curve covers.
    internal static void Corners(Span<uint> pixels, int width, int height, int radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        for (var i = 0; i < pixels.Length; i++) pixels[i] |= 0xFF000000;
        for (var y = 0; y < radius; y++)
        for (var x = 0; x < radius; x++)
        {
            // Distance of the pixel centre from the centre of the curve.
            double dx = radius - x - 0.5, dy = radius - y - 0.5;
            var coverage = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
            var alpha = (uint)Math.Round(coverage * 255) << 24;
            foreach (var (cx, cy) in new[] { (x, y), (width - 1 - x, y), (x, height - 1 - y), (width - 1 - x, height - 1 - y) })
            {
                ref var p = ref pixels[cy * width + cx];
                p = (p & 0x00FFFFFF) | alpha;
            }
        }
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
        // -1 is the current process's pseudo-handle, which needs no closing.
        SetProcessWorkingSetSize(-1, -1, -1);
    }

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

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
    bool disposed;

    public Frame(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "a frame needs at least one pixel");
        Width = width;
        Height = height;
        // Negative height: rows run top to bottom, as everything else assumes.
        var header = new Gdi.BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        Dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (Dc == IntPtr.Zero) throw new OutOfMemoryException("could not create a drawing surface");
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
    ///
    /// Checked, because this wraps a raw pointer: a rectangle chosen against
    /// a different screen layout (a monitor plugged in mid-capture) would
    /// otherwise read memory past the end of the pixels.
    public Bitmap ToBitmap(Rectangle rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width < 1 || rect.Height < 1 || rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside the {Width} x {Height} capture");
        using var view = new Bitmap(rect.Width, rect.Height, Width * 4, PixelFormat.Format32bppRgb,
            (IntPtr)(bits + (long)rect.Y * Width + rect.X));
        return view.Clone(new Rectangle(Point.Empty, rect.Size), PixelFormat.Format32bppRgb);
    }

    public void CopyToClipboard(Rectangle rect, IntPtr owner) => Clip.Copy(Pixels, Width, rect, owner);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Gdi.SelectObject(Dc, previous);
        Gdi.DeleteObject(bitmap);
        Gdi.DeleteDC(Dc);
    }
}

static unsafe class Clip
{
    const uint CF_DIB = 8, GMEM_MOVEABLE = 2;

    /// An image with transparency (a window with rounded corners) also goes on
    /// as PNG, which keeps it; the CF_DIB copy is laid over white, because
    /// most readers of CF_DIB ignore alpha and would show the corners black.
    public static void Copy(Bitmap image, IntPtr owner)
    {
        var whole = new Rectangle(Point.Empty, image.Size);
        var data = image.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        uint[]? flattened = null;
        try
        {
            // 32 bpp, so the stride is the width and the rows are one run.
            var pixels = new Span<uint>((void*)data.Scan0, image.Width * image.Height);
            // Any pixel below 0xFF000000 in value has alpha under 255.
            if (pixels.IndexOfAnyInRange(0u, 0xFEFFFFFFu) >= 0)
                flattened = OverWhite(pixels);
            else
            {
                Copy(pixels, image.Width, whole, owner);
                return;
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        Copy(flattened, image.Width, whole, owner, Png(image));
    }

    static uint[] OverWhite(ReadOnlySpan<uint> pixels)
    {
        var flat = new uint[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var a = p >> 24;
            uint Mix(int shift) => (((p >> shift) & 0xFF) * a + 255 * (255 - a)) / 255;
            flat[i] = 0xFF000000 | Mix(16) << 16 | Mix(8) << 8 | Mix(0);
        }
        return flat;
    }

    public static byte[] Png(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// Put `rect` of an image `width` pixels wide on the clipboard as CF_DIB;
    /// Windows derives the other bitmap formats from it.
    ///
    /// Not `Clipboard.SetImage` or `SetDataObject`: both leave a GDI bitmap of
    /// the whole image inside this process. Three full-screen copies took
    /// private memory from 11 MB to 97 MB. Handed over as an HGLOBAL, the
    /// copy belongs to the clipboard.
    public static void Copy(Span<uint> pixels, int width, Rectangle rect, IntPtr owner, byte[]? png = null)
    {
        var size = DibSize(rect);
        // Checked before allocating, not after: a span cannot be larger, and
        // finding out once the memory is held leaked it.
        if (size > int.MaxValue) throw new OutOfMemoryException("too large to put on the clipboard");
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
            // The "PNG" format is the one browsers, Office, Teams and Slack
            // read when it is there; it is the only one that keeps alpha.
            if (png != null)
            {
                var copy = GlobalAlloc(GMEM_MOVEABLE, (nuint)png.Length);
                var q = (byte*)GlobalLock(copy);
                if (q != null)
                {
                    png.CopyTo(new Span<byte>(q, png.Length));
                    GlobalUnlock(copy);
                    if (SetClipboardData(RegisterClipboardFormat("PNG"), copy) == IntPtr.Zero) GlobalFree(copy);
                }
                else if (copy != IntPtr.Zero) GlobalFree(copy);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
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
    public const int COLORONCOLOR = 3;
    public const uint BLACKNESS = 0x00000042;

    [DllImport("gdi32.dll")] public static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint rop);

    [DllImport("gdi32.dll")] public static extern int ExcludeClipRect(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")]
    public static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, uint rop);
    [DllImport("gdi32.dll")] public static extern int SelectClipRgn(IntPtr dc, IntPtr region);
}
