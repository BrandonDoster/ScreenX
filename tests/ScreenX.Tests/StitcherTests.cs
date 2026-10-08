// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

namespace ScreenX.Tests;

/// Scrolling capture against a synthetic page: a tall image, viewed through a
/// window with a fixed header and footer and a scrollbar whose thumb moves.
public class StitcherTests
{
    const int Width = 200, View = 300, Header = 40, Footer = 30, Scrollbar = 12;
    const int Band = View - Header - Footer;

    static uint[] Page(int rows, int seed = 1)
    {
        var random = new Random(seed);
        var page = new uint[rows * Width];
        for (var y = 0; y < rows; y++)
        {
            // Some flat rows, like the gaps between paragraphs.
            var flat = y % 37 < 6;
            for (var x = 0; x < Width; x++)
                page[y * Width + x] = flat ? 0xFFFFFFFF : (uint)random.Next() | 0xFF000000;
        }
        return page;
    }

    /// What the screen shows with the page scrolled to `offset`.
    static uint[] Frame(uint[] page, int offset, bool chrome = true)
    {
        var frame = new uint[View * Width];
        var top = chrome ? Header : 0;
        var band = chrome ? Band : View;
        for (var y = 0; y < View; y++)
        for (var x = 0; x < Width; x++)
        {
            uint pixel;
            if (chrome && y < Header) pixel = 0xFF102030u + (uint)x;
            else if (chrome && y >= Header + Band) pixel = 0xFF302010u + (uint)x;
            else pixel = page[(offset + y - top) * Width + x];
            frame[y * Width + x] = pixel;
        }
        // The thumb: a dark block on the right whose position follows the
        // scroll, so it differs between every pair of frames.
        var thumb = top + offset % Math.Max(1, band - 40);
        for (var y = 0; y < View; y++)
        for (var x = Width - Scrollbar; x < Width; x++)
            frame[y * Width + x] = y >= thumb && y < thumb + 40 ? 0xFF000000 : 0xFFDDDDDD;
        return frame;
    }

    static uint[] Rows(uint[] image, int from, int count) => image.AsSpan(from * Width, count * Width).ToArray();

    [Fact]
    public void A_page_scrolled_to_the_end_is_rebuilt_with_one_header_and_one_footer()
    {
        var page = Page(1500);
        var stitcher = new Stitcher(Width, View, ignoreRight: 16);
        var last = page.Length / Width - Band;
        var offsets = new List<int>();
        for (var o = 0; o < last; o += 97) offsets.Add(o);
        offsets.Add(last);
        offsets.Add(last); // the page stops moving

        Stitcher.Result result = default;
        foreach (var o in offsets) result = stitcher.Add(Frame(page, o));
        Assert.Equal(Stitcher.Result.End, result);

        var image = stitcher.Compose();
        Assert.Equal(Header + page.Length / Width + Footer, image.Length / Width);
        var expectedHeader = Rows(Frame(page, 0), 0, Header);
        Assert.Equal(expectedHeader.Skip(0).Take(Header * Width - 0), Rows(image, 0, Header));
        // The page itself, without the scrollbar column, which is not page.
        for (var y = 0; y < page.Length / Width; y++)
            Assert.True(
                image.AsSpan((Header + y) * Width, Width - Scrollbar).SequenceEqual(page.AsSpan(y * Width, Width - Scrollbar)),
                $"page row {y} is wrong");
        var expectedFooter = Rows(Frame(page, last), Header + Band, Footer);
        Assert.Equal(expectedFooter, Rows(image, image.Length / Width - Footer, Footer));
    }

    [Fact]
    public void A_frame_with_no_header_or_footer_still_joins()
    {
        var page = Page(900, seed: 2);
        var stitcher = new Stitcher(Width, View, ignoreRight: 16);
        foreach (var o in new[] { 0, 120, 240, 360, 480, 600 })
            Assert.Equal(Stitcher.Result.Added, stitcher.Add(Frame(page, o, chrome: false)));
        Assert.Equal(Stitcher.Result.End, stitcher.Add(Frame(page, 600, chrome: false)));
        var image = stitcher.Compose();
        Assert.Equal(900, image.Length / Width);
        Assert.True(image.AsSpan(800 * Width, Width - Scrollbar).SequenceEqual(page.AsSpan(800 * Width, Width - Scrollbar)));
    }

    [Fact]
    public void Nothing_moving_after_the_first_scroll_ends_with_just_that_frame()
    {
        var page = Page(400);
        var stitcher = new Stitcher(Width, View, ignoreRight: 16);
        stitcher.Add(Frame(page, 0));
        Assert.Equal(Stitcher.Result.End, stitcher.Add(Frame(page, 0)));
        Assert.Equal(View, stitcher.Compose().Length / Width);
    }

    [Fact]
    public void An_unrelated_frame_is_reported_lost_rather_than_glued_on()
    {
        var stitcher = new Stitcher(Width, View, ignoreRight: 16);
        stitcher.Add(Frame(Page(800, seed: 3), 0, chrome: false));
        Assert.Equal(Stitcher.Result.Lost, stitcher.Add(Frame(Page(800, seed: 4), 0, chrome: false)));
        Assert.Equal(View, stitcher.TotalHeight);
    }

    [Fact]
    public void A_few_changed_rows_such_as_a_blinking_caret_do_not_break_the_match()
    {
        var page = Page(800, seed: 5);
        var stitcher = new Stitcher(Width, View, ignoreRight: 16);
        stitcher.Add(Frame(page, 0, chrome: false));
        var next = Frame(page, 100, chrome: false);
        for (var y = 10; y < 14; y++) next[y * Width + 50] ^= 0x00FFFFFF;
        Assert.Equal(Stitcher.Result.Added, stitcher.Add(next));
        Assert.Equal(100, stitcher.LastShift);
    }

    /// Lines of "text": glyph-sized dark blocks on white, 18 px per line, the
    /// way a document looks to the profile pass.
    static float[] TextPage(int rows, int seed)
    {
        var random = new Random(seed);
        var page = new float[rows * Width];
        page.AsSpan().Fill(255);
        for (var line = 0; line * 18 < rows; line++)
        {
            var x = 4;
            while (x < Width - Scrollbar - 12)
            {
                var glyph = random.Next(3, 9);
                var tall = random.Next(6, 12);
                for (var y = line * 18 + 14 - tall; y < line * 18 + 14 && y < rows; y++)
                for (var gx = x; gx < x + glyph; gx++)
                    page[y * Width + gx] = 30;
                x += glyph + random.Next(2, 10);
            }
        }
        return page;
    }

    /// The page drawn at a fractional offset: each screen row a blend of the
    /// two page rows it falls between, as antialiased text is.
    static uint[] Blended(float[] page, double offset)
    {
        var frame = new uint[View * Width];
        var whole = (int)Math.Floor(offset);
        var t = (float)(offset - whole);
        for (var y = 0; y < View; y++)
        for (var x = 0; x < Width; x++)
        {
            var v = page[(whole + y) * Width + x] * (1 - t) + page[(whole + y + 1) * Width + x] * t;
            var g = (uint)Math.Round(v);
            frame[y * Width + x] = 0xFF000000 | g << 16 | g << 8 | g;
        }
        return frame;
    }

    [Fact]
    public void Content_drawn_at_fractional_offsets_still_joins()
    {
        var page = TextPage(1400, seed: 6);
        var stitcher = new Stitcher(Width, View, ignoreRight: 16);
        Assert.Equal(Stitcher.Result.Added, stitcher.Add(Blended(page, 0)));
        // Steps of about 117.4 px, each landing on a different fraction.
        var offset = 0.0;
        while (offset + 117.4 < 1400 - View - 2)
        {
            offset += 117.4;
            Assert.Equal(Stitcher.Result.Added, stitcher.Add(Blended(page, offset)));
            Assert.InRange(stitcher.LastShift, 116, 119);
        }
        // Rounding each step costs at most a row, so the page comes back within
        // a row per step of its true length.
        var steps = (int)(offset / 117.4);
        Assert.InRange(stitcher.TotalHeight, (int)offset + View - steps, (int)offset + View + steps);
    }

    [Fact]
    public void A_frame_of_the_wrong_size_is_refused()
    {
        var stitcher = new Stitcher(Width, View, 0);
        Assert.Throws<ArgumentException>(() => stitcher.Add(new uint[10]));
    }
}
