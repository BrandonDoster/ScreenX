// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenX.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Control+Alt+A")]
    [InlineData("ctrl + shift + F5")]
    [InlineData("PrintScreen")]
    [InlineData("Pause")]
    [InlineData("F13")]
    [InlineData("Win+D1")]
    [InlineData("Alt+PrintScreen")]
    public void Accepted(string text) => Assert.True(HotkeyWindow.TryParse(text, out _, out _));

    [Theory]
    [InlineData("A")] // would take the letter from every program
    [InlineData("")]
    [InlineData("Control+Alt")]
    [InlineData("Control+A+B")]
    [InlineData("Control+65")] // Enum.TryParse takes numbers
    [InlineData("Control+A,B")] // and comma lists
    [InlineData("Control+Banana")]
    [InlineData("Control+Shift")] // modifiers only
    [InlineData("Control+ControlKey")]
    public void Refused(string text) => Assert.False(HotkeyWindow.TryParse(text, out _, out _));

    [Fact]
    public void Modifiers_and_key_are_what_was_written()
    {
        Assert.True(HotkeyWindow.TryParse("Control+Alt+A", out var mods, out var key));
        Assert.Equal(3u, mods); // MOD_ALT | MOD_CONTROL
        Assert.Equal(Keys.A, key);
    }
}

public sealed class SettingsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), $"screenx-settings-{Guid.NewGuid():N}");
    string File => Path.Combine(folder, "screenx.json");

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void A_missing_file_is_created_with_the_defaults()
    {
        var (settings, error) = Settings.Load(File);
        Assert.Null(error);
        Assert.Equal("Control+Alt+A", settings.RegionHotkey);
        Assert.True(System.IO.File.Exists(File));
    }

    [Fact]
    public void A_broken_file_is_reported_and_never_overwritten()
    {
        Directory.CreateDirectory(folder);
        const string broken = "{ \"RegionHotkey\": \"Control+Alt+Q\", oops";
        System.IO.File.WriteAllText(File, broken);
        var (settings, error) = Settings.Load(File);
        Assert.NotNull(error);
        Assert.Equal("Control+Alt+A", settings.RegionHotkey);
        Assert.Equal(broken, System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Missing_keys_take_their_defaults_and_a_null_folder_is_not_kept()
    {
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(File, "{ \"RegionHotkey\": \"PrintScreen\", \"Folder\": null }");
        var (settings, error) = Settings.Load(File);
        Assert.Null(error);
        Assert.Equal("PrintScreen", settings.RegionHotkey);
        Assert.Equal("Control+Alt+S", settings.ScrollHotkey);
        Assert.False(string.IsNullOrWhiteSpace(settings.Folder));
    }
}

public class ClipboardTests
{
    [Fact]
    public void A_dib_is_bottom_up_opaque_and_only_the_chosen_rectangle()
    {
        // 4 x 3 image, each pixel numbered, alpha zero as the screen read gives it.
        var pixels = Enumerable.Range(0, 12).Select(i => (uint)i).ToArray();
        var rect = new Rectangle(1, 1, 2, 2);
        var dib = new byte[Clip.DibSize(rect)];
        Clip.PackDib(pixels, 4, rect, dib);

        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8)); // positive height: bottom-up
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        var rows = MemoryMarshal.Cast<byte, uint>(dib.AsSpan(40)).ToArray();
        // Bottom row of the rectangle (pixels 9, 10) first, then 5, 6.
        Assert.Equal(new[] { 0xFF000009u, 0xFF00000Au, 0xFF000005u, 0xFF000006u }, rows);
    }

    [Fact]
    public void A_rectangle_outside_the_image_is_refused()
    {
        var pixels = new uint[12];
        var rect = new Rectangle(3, 0, 2, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Clip.PackDib(pixels, 4, rect, new byte[Clip.DibSize(rect)]));
    }
}

public class FrameTests
{
    [Fact]
    public void A_bitmap_from_a_frame_is_the_requested_rectangle()
    {
        using var frame = new Frame(10, 8);
        var pixels = frame.Pixels;
        for (var i = 0; i < pixels.Length; i++) pixels[i] = 0xFF000000u | (uint)i;
        using var bitmap = frame.ToBitmap(new Rectangle(2, 3, 4, 2));
        Assert.Equal(new Size(4, 2), bitmap.Size);
        Assert.Equal(Color.FromArgb(255, 0, 0, 3 * 10 + 2).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(255, 0, 0, 4 * 10 + 5).ToArgb(), bitmap.GetPixel(3, 1).ToArgb());
    }

    [Fact]
    public void Dimming_halves_every_channel_without_bleeding_between_them()
    {
        using var frame = new Frame(37, 3); // not a multiple of any vector width
        frame.Pixels.Fill(0xFFFF8001u);
        using var dim = Overlay.Dimmed(frame);
        foreach (var pixel in dim.Pixels) Assert.Equal(0x7F7F4000u, pixel);
    }
}

public class ShapeTests
{
    static Shape Box(float x, float y, float w, float h) =>
        new() { Kind = Kind.Rect, Colour = Color.Red, Width = 4, Points = [new(x, y), new(x + w, y + h)] };

    [Fact]
    public void A_clone_does_not_share_its_points()
    {
        var a = Box(0, 0, 10, 10);
        var b = a.Clone();
        b.Offset(5, 5);
        Assert.Equal(new PointF(0, 0), a.Points[0]);
        Assert.Equal(new PointF(5, 5), b.Points[0]);
    }

    [Fact]
    public void A_line_is_hit_near_it_and_not_far_from_it()
    {
        var line = new Shape { Kind = Kind.Line, Width = 2, Points = [new(0, 0), new(100, 0)] };
        Assert.True(line.Hit(new PointF(50, 3), 3));
        Assert.False(line.Hit(new PointF(50, 30), 3));
        // Past the end does not count as on the line.
        Assert.False(line.Hit(new PointF(130, 0), 3));
    }

    [Fact]
    public void Stray_clicks_are_degenerate_and_steps_never_are()
    {
        Assert.True(Box(10, 10, 1, 1).Degenerate);
        Assert.False(Box(10, 10, 20, 1).Degenerate);
        Assert.False(new Shape { Kind = Kind.Step, Points = [new(1, 1)] }.Degenerate);
        Assert.True(new Shape { Kind = Kind.Text, Points = [new(1, 1)], Text = "" }.Degenerate);
    }

    /// Redaction has to destroy detail, so test it against the finest detail
    /// there is: a one-pixel checkerboard. A blur would leave a readable grey
    /// pattern at the edges; every block here must come out one flat colour.
    [Fact]
    public void Redaction_leaves_no_detail_inside_a_block()
    {
        using var source = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        for (var y = 0; y < 48; y++)
        for (var x = 0; x < 48; x++)
            source.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Black : Color.White);
        using var target = new Bitmap(source);
        using (var g = Graphics.FromImage(target))
            new Shape { Kind = Kind.Redact, Points = [new(0, 0), new(48, 48)] }.Draw(g, source);

        for (var by = 0; by < 48; by += 12)
        for (var bx = 0; bx < 48; bx += 12)
        {
            var first = target.GetPixel(bx, by);
            for (var y = by; y < by + 12; y++)
            for (var x = bx; x < bx + 12; x++)
                Assert.Equal(first, target.GetPixel(x, y));
        }
    }

    [Fact]
    public void Every_kind_draws_without_throwing()
    {
        // Two bitmaps, as in the editor: redaction reads the source while the
        // shapes are drawn onto the cache.
        using var source = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var cache = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(cache);
        foreach (var kind in Enum.GetValues<Kind>())
        {
            var shape = new Shape
            {
                Kind = kind, Colour = Color.Blue, Width = 4, Text = "hi", Number = 3,
                Points = [new(5, 5), new(40, 30), new(50, 50)],
            };
            shape.Draw(g, source);
            Assert.False(shape.Bounds.IsEmpty, $"{kind} has no bounds");
        }
    }
}

public sealed class PathTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("screenx-paths-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_new_name_never_overwrites_an_existing_file()
    {
        var first = Tray.UniquePath(folder, "shot");
        Assert.Equal("shot.png", Path.GetFileName(first));
        File.WriteAllText(first, "x");
        Assert.Equal("shot (2).png", Path.GetFileName(Tray.UniquePath(folder, "shot")));
    }
}
