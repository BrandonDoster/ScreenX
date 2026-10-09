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
    // What the settings window's recorder writes: Keys names as ToString gives them.
    [InlineData("Control+D1")]
    [InlineData("Control+Oemplus")]
    [InlineData("Control+Next")]
    [InlineData("Win+Shift+S")]
    [InlineData("Shift+Return")]
    [InlineData("Control+A")]
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
        Assert.Equal("Control+Shift+Q", settings.RegionHotkey);
        Assert.True(System.IO.File.Exists(File));
    }

    [Fact]
    public void Saved_settings_read_back_and_stay_readable_by_hand()
    {
        var chosen = new Settings { RegionHotkey = "Control+Shift+Q", ScrollHotkey = "Alt+F13", AfterCapture = "copy", Folder = folder };
        Settings.Save(chosen, File);
        var text = System.IO.File.ReadAllText(File);
        Assert.Contains("Control+Shift+Q", text); // not Control+Shift+Q
        var (loaded, error) = Settings.Load(File);
        Assert.Null(error);
        Assert.Equal(("Control+Shift+Q", "Alt+F13", "copy", folder),
            (loaded.RegionHotkey, loaded.ScrollHotkey, loaded.AfterCapture, loaded.Folder));
        Assert.False(System.IO.File.Exists(File + ".partial"));
    }

    [Fact]
    public void A_broken_file_is_reported_and_never_overwritten()
    {
        Directory.CreateDirectory(folder);
        const string broken = "{ \"RegionHotkey\": \"Control+Alt+Q\", oops";
        System.IO.File.WriteAllText(File, broken);
        var (settings, error) = Settings.Load(File);
        Assert.NotNull(error);
        Assert.Equal("Control+Shift+Q", settings.RegionHotkey);
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
        Assert.Equal("", settings.ScrollHotkey);
        Assert.Equal("editor", settings.AfterCapture);
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
    public void A_rectangle_far_down_a_tall_image_is_packed_from_the_right_rows()
    {
        const int width = 5, height = 30_010;
        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (uint)i & 0x00FFFFFF;
        var rect = new Rectangle(2, 30_000, 2, 3);
        var dib = new byte[Clip.DibSize(rect)];
        Clip.PackDib(pixels, width, rect, dib);
        var rows = MemoryMarshal.Cast<byte, uint>(dib.AsSpan(40)).ToArray();
        // Bottom row (30002) first.
        Assert.Equal(0xFF000000u | (uint)(30_002 * width + 2), rows[0]);
        Assert.Equal(0xFF000000u | (uint)(30_000 * width + 3), rows[^1]);
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

/// One collection with CutTests: both measure text through `Shape.Measuring`, a
/// single GDI+ Graphics that throws when two test threads use it at once.
[Collection("Measuring")]
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
    public void Dragging_a_corner_handle_moves_only_that_corner()
    {
        var box = Box(10, 10, 20, 20); // corners (10,10) and (30,30)
        Assert.Equal(4, box.Handles.Length);
        // The top-right corner takes its X from the second point, Y from the first.
        var topRight = box.Handles.Single(h => box.HandleAt(h) == new PointF(30, 10));
        box.MoveHandle(topRight, new PointF(50, 0));
        Assert.Equal(new PointF(10, 0), box.Points[0]);
        Assert.Equal(new PointF(50, 30), box.Points[1]);
    }

    [Fact]
    public void A_line_has_a_handle_at_each_end_and_text_has_none()
    {
        var line = new Shape { Kind = Kind.Arrow, Points = [new(0, 0), new(5, 9)] };
        Assert.Equal(new[] { new PointF(0, 0), new PointF(5, 9) }, line.Handles.Select(line.HandleAt));
        Assert.Empty(new Shape { Kind = Kind.Text, Points = [new(1, 1)] }.Handles);
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

public class ImageTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)] // PNG
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)] // JPEG
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0 }, true)] // BMP
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, true)] // GIF
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x00 }, false)] // EMF
    [InlineData(new byte[] { 0xD7, 0xCD, 0xC6, 0x9A }, false)] // WMF
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, false)] // TIFF
    [InlineData(new byte[] { }, false)]
    public void Only_the_formats_it_needs_are_decoded(byte[] head, bool supported) =>
        Assert.Equal(supported, Images.IsSupported(head));

    /// The editor's Overwrite needs the file free. A file-backed GDI+ bitmap
    /// keeps it locked, which made Overwrite fail in --edit.
    [Fact]
    public void An_opened_image_does_not_hold_its_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"screenx-{Guid.NewGuid():N}.png");
        using (var made = new Bitmap(4, 4)) made.Save(path, ImageFormat.Png);
        try
        {
            using var image = Images.Open(path);
            Assert.Equal(new Size(4, 4), image.Size);
            File.WriteAllBytes(path, [1, 2, 3]); // would throw if still locked
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_with_an_image_name_but_other_contents_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"screenx-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x01, 0, 0, 0, 0, 0, 0, 0]);
        try
        {
            var e = Assert.Throws<InvalidDataException>(() => Images.Open(path));
            Assert.True(Images.IsLoadFailure(e)); // shown as a message, not a crash
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FrameBoundsTests
{
    /// The rectangle can come from a different screen layout than the frame.
    /// Past the edge must throw, not read memory beyond the pixels.
    [Theory]
    [InlineData(0, 0, 11, 8)]
    [InlineData(5, 5, 6, 3)]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(0, 0, 0, 4)]
    public void A_rectangle_past_the_frame_is_refused(int x, int y, int w, int h)
    {
        using var frame = new Frame(10, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.ToBitmap(new Rectangle(x, y, w, h)));
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var frame = new Frame(4, 4);
        frame.Dispose();
        frame.Dispose();
    }
}

public sealed class FolderSettingsTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("screenx-folder-").FullName;
    string File => Path.Combine(folder, "screenx.json");

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_relative_folder_is_refused_with_a_reason()
    {
        System.IO.File.WriteAllText(File, "{ \"Folder\": \"shots\" }");
        var (settings, error) = Settings.Load(File);
        Assert.NotNull(error);
        Assert.True(Path.IsPathFullyQualified(settings.Folder));
    }

    [Fact]
    public void Environment_variables_in_the_folder_are_expanded()
    {
        System.IO.File.WriteAllText(File, "{ \"Folder\": \"%TEMP%\\\\shots\" }");
        var (settings, error) = Settings.Load(File);
        Assert.Null(error);
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "shots"), settings.Folder);
    }

    [Fact]
    public void A_null_hotkey_means_none_rather_than_a_crash()
    {
        System.IO.File.WriteAllText(File, "{ \"ScrollHotkey\": null }");
        var (settings, _) = Settings.Load(File);
        Assert.Equal("", settings.ScrollHotkey);
    }
}

public class HistoryTests
{
    static readonly Rectangle Whole = new(0, 0, 100, 100);
    static Shape Box(int x) => new() { Kind = Kind.Rect, Points = [new(x, x), new(x + 10, x + 10)] };

    [Fact]
    public void Undo_and_redo_walk_the_steps_including_a_crop()
    {
        var history = new History(Whole);
        var shapes = new List<Shape> { Box(1) };
        history.Push(shapes, Whole);
        history.Push(shapes, new Rectangle(10, 10, 50, 50));
        Assert.True(history.Undo());
        Assert.Equal(Whole, history.Current.crop);
        Assert.Single(history.Current.shapes);
        Assert.True(history.Undo());
        Assert.Empty(history.Current.shapes);
        Assert.False(history.Undo());
        Assert.True(history.Redo());
        Assert.True(history.Redo());
        Assert.Equal(new Rectangle(10, 10, 50, 50), history.Current.crop);
        Assert.False(history.Redo());
    }

    [Fact]
    public void A_new_step_after_undo_discards_what_was_undone()
    {
        var history = new History(Whole);
        history.Push([Box(1)], Whole);
        history.Push([Box(1), Box(2)], Whole);
        history.Undo();
        history.Push([Box(3)], Whole);
        Assert.False(history.CanRedo);
        Assert.Equal(new PointF(3, 3), history.Current.shapes.Single().Points[0]);
    }

    /// The editor moves shapes in place. That must not reach back and change
    /// the step it came from, or undoing a move would undo nothing.
    [Fact]
    public void Editing_the_current_shapes_does_not_change_history()
    {
        var history = new History(Whole);
        var shapes = new List<Shape> { Box(1) };
        history.Push(shapes, Whole);
        shapes[0].Offset(50, 50);
        var current = history.Current.shapes;
        Assert.Equal(new PointF(1, 1), current[0].Points[0]);
        current[0].Offset(5, 5);
        Assert.Equal(new PointF(1, 1), history.Current.shapes[0].Points[0]);
    }

    [Fact]
    public void History_is_bounded()
    {
        var history = new History(Whole);
        for (var i = 0; i < 500; i++) history.Push([Box(i)], Whole);
        var undos = 0;
        while (history.Undo()) undos++;
        Assert.Equal(199, undos);
    }
}

[Collection("Measuring")]
public class CutTests
{
    static readonly Rectangle Whole = new(0, 0, 100, 80);

    [Fact]
    public void A_sideways_drag_takes_out_columns_and_a_downward_one_rows()
    {
        Assert.Equal(new Cut(true, 10, 30), Cut.Across(new(40, 5), new(10, 9), Whole));
        Assert.Equal(new Cut(false, 20, 15), Cut.Across(new(50, 20), new(52, 35), Whole));
    }

    [Fact]
    public void Nothing_or_everything_is_no_cut()
    {
        Assert.Null(Cut.Across(new(10, 10), new(10.2f, 10), Whole));
        Assert.Null(Cut.Across(new(0, 10), new(100, 10), Whole));
    }

    [Fact]
    public void A_drag_past_a_crop_away_from_the_origin_is_held_to_the_crop()
    {
        var crop = new Rectangle(50, 30, 100, 80);
        Assert.Equal(new Cut(true, 50, 40), Cut.Across(new(10, 40), new(90, 45), crop));
        Assert.Equal(new Cut(false, 90, 20), Cut.Across(new(60, 90), new(61, 300), crop));
    }

    /// Points sit on pixel edges, so the band's far edge is in it: a box drawn
    /// exactly over the band goes, rather than staying as a flat line.
    [Fact]
    public void A_box_exactly_over_the_band_goes()
    {
        var rows = new Cut(false, 20, 10);
        Assert.True(rows.Contains(new(0, 20)) && rows.Contains(new(0, 30)));
        Assert.False(rows.Contains(new(0, 19.9f)) || rows.Contains(new(0, 30.1f)));
        Assert.Empty(rows.Shift([new Shape { Kind = Kind.Rect, Points = [new(0, 20), new(50, 30)] }]));
    }

    /// Exact pixels, translucent ones included: the band is gone and both
    /// sides are copied untouched.
    [Theory]
    [InlineData(true, 3, 2)]
    [InlineData(false, 1, 3)]
    [InlineData(true, 0, 2)] // the band at the near edge
    [InlineData(true, 5, 2)] // and at the far edge
    [InlineData(false, 3, 2)]
    public void Applying_a_cut_closes_the_gap_pixel_for_pixel(bool columns, int at, int length)
    {
        using var image = new Bitmap(7, 5, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 7; x++)
            image.SetPixel(x, y, Color.FromArgb(40 + x * 30, x * 30, y * 40, 7));
        using var cut = Cut.Apply(image, [new Cut(columns, at, length)]);
        Assert.Equal(columns ? new Size(7 - length, 5) : new Size(7, 5 - length), cut.Size);
        for (var y = 0; y < cut.Height; y++)
        for (var x = 0; x < cut.Width; x++)
        {
            var (fx, fy) = columns ? (x < at ? x : x + length, y) : (x, y < at ? y : y + length);
            Assert.Equal(image.GetPixel(fx, fy).ToArgb(), cut.GetPixel(x, y).ToArgb());
        }
    }

    /// Undo rebuilds from the capture through every cut in one copy; that
    /// must match making the cuts one after another.
    [Fact]
    public void Several_cuts_in_one_pass_match_cutting_one_at_a_time()
    {
        using var image = new Bitmap(9, 8, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 9; x++)
            image.SetPixel(x, y, Color.FromArgb(255, x * 25, y * 30, 9));
        Cut[] cuts = [new(true, 2, 2), new(false, 1, 3), new(true, 4, 1), new(true, 0, 1), new(false, 3, 1)];
        using var once = Cut.Apply(image, cuts);
        var step = image;
        foreach (var c in cuts)
        {
            var next = Cut.Apply(step, [c]);
            if (step != image) step.Dispose();
            step = next;
        }
        using (step)
        {
            Assert.Equal(new Size(5, 4), once.Size);
            Assert.Equal(step.Size, once.Size);
            for (var y = 0; y < once.Height; y++)
            for (var x = 0; x < once.Width; x++)
                Assert.Equal(step.GetPixel(x, y).ToArgb(), once.GetPixel(x, y).ToArgb());
        }
    }

    [Fact]
    public void Shapes_follow_the_image_and_those_inside_the_band_go()
    {
        var rows = new Cut(false, 20, 10);
        var inside = new Shape { Kind = Kind.Rect, Points = [new(0, 22), new(50, 28)] };
        var below = new Shape { Kind = Kind.Rect, Points = [new(0, 40), new(50, 50)] };
        var across = new Shape { Kind = Kind.Line, Points = [new(5, 10), new(5, 25), new(5, 60)] };
        var shifted = rows.Shift([inside, below, across]);
        Assert.Equal(2, shifted.Count);
        Assert.Equal([new PointF(0, 30), new PointF(50, 40)], shifted[0].Points);
        Assert.Equal([new PointF(5, 10), new PointF(5, 20), new PointF(5, 50)], shifted[1].Points);
        Assert.Equal(new PointF(0, 40), below.Points[0]); // the original is left alone
        Assert.Equal(Rectangle.FromLTRB(0, 0, 100, 70), rows.Shift(Whole));
    }

    static Shape Label(float x, float y, string text) => new() { Kind = Kind.Text, Width = 4, Points = [new(x, y)], Text = text };
    static Shape Step(float x, float y) => new() { Kind = Kind.Step, Width = 2, Number = 1, Points = [new(x, y)] }; // radius 16

    /// Text is one point that draws outward: judged by what it covers, so a
    /// label anchored in the band but running past it stays.
    [Fact]
    public void Text_goes_only_when_all_it_covers_is_in_the_band()
    {
        var columns = new Cut(true, 20, 60);
        var kept = columns.Shift([Label(30, 5, "a label long enough to pass the band"), Label(30, 40, "in")]);
        Assert.Equal(new PointF(20, 5), kept.Single().Points[0]); // anchor lands on the seam
        var rows = new Cut(false, 10, 200);
        Assert.Empty(rows.Shift([Label(5, 20, "short")]));
        Assert.Single(new Cut(false, 10, 5).Shift([Label(5, 12, "taller than the band")]));
    }

    /// A step is the point it marks: cut that point and the step goes, even
    /// with its circle reaching out of the band; keep it and it moves with
    /// the image.
    [Fact]
    public void A_step_goes_with_its_point()
    {
        var columns = new Cut(true, 20, 60);
        Assert.Empty(columns.Shift([Step(75, 40)])); // circle reaches 91, past the band
        var beside = columns.Shift([Step(90, 40)]).Single(); // circle reaches into the band
        Assert.Equal(new PointF(30, 40), beside.Points[0]);
        var rows = new Cut(false, 20, 60);
        Assert.Empty(rows.Shift([Step(40, 75)]));
        Assert.Equal(new PointF(40, 30), rows.Shift([Step(40, 90)]).Single().Points[0]);
    }

    /// A pen stroke drops its samples in the band rather than piling them on
    /// the seam, and goes if fewer than two are left to draw.
    [Fact]
    public void A_pen_stroke_drops_its_samples_in_the_band()
    {
        var rows = new Cut(false, 20, 10);
        var across = new Shape { Kind = Kind.Pen, Points = [new(5, 10), new(6, 22), new(7, 28), new(8, 40)] };
        Assert.Equal([new PointF(5, 10), new PointF(8, 30)], rows.Shift([across]).Single().Points);
        var mostlyIn = new Shape { Kind = Kind.Pen, Points = [new(5, 22), new(6, 25), new(7, 35)] };
        Assert.Empty(rows.Shift([mostlyIn]));
        // Other kinds still clamp to the seam.
        var line = new Shape { Kind = Kind.Line, Points = [new(5, 25), new(5, 40)] };
        Assert.Equal([new PointF(5, 20), new PointF(5, 30)], rows.Shift([line]).Single().Points);
    }

    /// Each step keeps its own cuts, so undo puts the image back as it was.
    [Fact]
    public void History_keeps_the_cuts_of_each_step()
    {
        var history = new History(Whole);
        Assert.Empty(history.Cuts);
        Cut[] one = [new(true, 10, 5)];
        Cut[] two = [.. one, new(false, 3, 4)];
        history.Push([], Whole, one);
        history.Push([], Whole, two);
        Assert.Same(two, history.Cuts);
        history.Undo();
        Assert.Same(one, history.Cuts);
        history.Undo();
        Assert.Empty(history.Cuts);
        history.Redo();
        Assert.Same(one, history.Cuts);
        history.Push([], Whole); // a step without cuts given has none
        Assert.Empty(history.Cuts);
    }
}

public class CornerTests
{
    [Fact]
    public void Corners_are_clear_outside_the_curve_opaque_inside_and_partial_on_it()
    {
        const int w = 40, h = 30, r = 8;
        var pixels = new uint[w * h];
        pixels.AsSpan().Fill(0x00123456);
        Capture.Corners(pixels, w, h, r);
        uint Alpha(int x, int y) => pixels[y * w + x] >> 24;
        // The very corner pixel of all four corners is outside the curve.
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) }) Assert.Equal(0u, Alpha(x, y));
        // The middle and the straight edges are opaque.
        Assert.Equal(255u, Alpha(w / 2, h / 2));
        Assert.Equal(255u, Alpha(r, 0));
        Assert.Equal(255u, Alpha(0, r));
        // Somewhere on the curve is partly covered: antialiased, not stepped.
        var partial = Enumerable.Range(0, r).Any(i => Alpha(i, r - 1 - i) is > 0 and < 255);
        Assert.True(partial);
        // Colour is untouched.
        Assert.Equal(0x123456u, pixels[0] & 0xFFFFFF);
    }

    [Fact]
    public void A_radius_larger_than_the_image_is_held_to_half_of_it()
    {
        var pixels = new uint[6 * 6];
        Capture.Corners(pixels, 6, 6, 50);
        Assert.Equal(255u, pixels[3 * 6 + 3] >> 24);
    }
}

public class SettingsWindowTests
{
    static Settings With(string region = "Control+Shift+Q", string screen = "", string scroll = "", string folder = @"C:\Shots") =>
        new() { RegionHotkey = region, ScreenHotkey = screen, ScrollHotkey = scroll, Folder = folder };

    [Fact]
    public void Good_settings_pass() => Assert.Null(SettingsWindow.Validate(With(screen: "PrintScreen")));

    [Fact]
    public void Two_captures_on_one_hotkey_written_differently_are_refused() =>
        Assert.NotNull(SettingsWindow.Validate(With(region: "ctrl+shift+q", scroll: "Shift+Control+Q")));

    [Fact]
    public void A_bare_letter_is_refused() => Assert.NotNull(SettingsWindow.Validate(With(region: "Q")));

    [Fact]
    public void A_relative_folder_is_refused() => Assert.NotNull(SettingsWindow.Validate(With(folder: "Shots")));

    [Fact]
    public void Empty_hotkeys_are_allowed() => Assert.Null(SettingsWindow.Validate(With(region: "")));

    [Theory]
    [InlineData(@"\\nas\shots", true)]
    [InlineData(@"\\nas@SSL\DavWWWRoot\shots", true)]
    [InlineData(@"\\?\UNC\nas\shots", true)]
    [InlineData(@"\\.\UNC\nas\shots", true)]
    [InlineData("//nas/shots", true)]
    [InlineData(@"C:\Shots", false)]
    [InlineData(@"\\?\C:\Shots", false)]
    public void Network_folders_are_recognised(string folder, bool network) =>
        Assert.Equal(network, SettingsWindow.OnNetwork(folder));

    /// A local folder, or a folder inside one, that links to a share. Making a
    /// symbolic link needs Developer Mode or admin; without it this checks
    /// only that a junction to a local folder is not taken for one.
    [Fact]
    public void A_link_to_a_share_is_on_the_network()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screenx-{Guid.NewGuid():N}");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(Path.Combine(root, "local"));
        try
        {
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe")) { ArgumentList = { "/c", "mklink", "/J", junction, Path.Combine(root, "local") }, CreateNoWindow = true }))
            {
                mklink!.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }
            Assert.False(SettingsWindow.OnNetwork(Path.Combine(junction, "shots")));
            try { Directory.CreateSymbolicLink(Path.Combine(root, "share"), @"\\nas\shots"); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            Assert.True(SettingsWindow.OnNetwork(Path.Combine(root, "share", "2026")));
        }
        finally
        {
            // The link itself, not through it.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }
}

public class AfterCaptureTests
{
    [Theory]
    [InlineData("both", true, true, false)]
    [InlineData("Both", true, true, false)]
    [InlineData(" copy ", true, false, false)]
    [InlineData("save", false, true, false)]
    [InlineData("editor", false, false, true)]
    [InlineData("clipboard", false, true, false)] // the old build's word: saved, not lost
    [InlineData("", false, true, false)]
    [InlineData(null, false, true, false)]
    public void Each_value_does_what_it_says_and_anything_else_saves(string? value, bool copy, bool save, bool edit) =>
        Assert.Equal((copy, save, edit), Tray.Actions(value));
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
