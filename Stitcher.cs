// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

namespace ScreenX;

/// Joins the frames of a scrolling capture into one tall image.
///
/// Pure: frames go in as pixel arrays and an image comes out, so all of it is
/// tested without a screen. Every frame is the same region of the screen,
/// taken after scrolling down. Rows are compared by hash.
///
/// A frame splits into three bands. The header and footer are rows that did
/// not change between the first two frames (a toolbar, a sticky nav bar, a
/// status bar) and appear once. The band between them is what scrolled, and
/// each new frame adds the rows it brought into view.
sealed class Stitcher
{
    public enum Result { Added, End, Lost }

    /// The longest header or footer, as a share of the frame. A run of blank
    /// rows at the top would otherwise all count as header, and content that
    /// scrolled into them later would be dropped.
    const double MaxStaticShare = 0.4;
    /// How many of the overlapping rows must agree to accept an offset. Not all
    /// of them: a blinking caret or an animation changes a few.
    const double MinAgreement = 0.9;
    const int MinInformativeRows = 8;

    public readonly int Width, Height;
    /// Columns left out of the row hashes on the right: a scrollbar thumb moves
    /// every frame and would make every row it touches look different.
    readonly int ignoreRight;

    uint[]? first, previous;
    int[] previousHashes = [];
    bool[] previousUniform = [];
    int top = -1, bottom;
    readonly List<uint[]> added = [];
    int addedRows;
    int lastShift;

    public Stitcher(int width, int height, int ignoreRight)
    {
        Width = width;
        Height = height;
        this.ignoreRight = Math.Clamp(ignoreRight, 0, width / 2);
    }

    /// Rows in the finished image so far.
    public int TotalHeight => first == null ? 0 : Height + addedRows;

    /// The most recent offset, in rows; 0 before the first scroll.
    public int LastShift => lastShift;

    public Result Add(uint[] frame)
    {
        if (frame.Length != Width * Height) throw new ArgumentException("frame size changed");
        var (hashes, uniform) = Hash(frame);
        if (first == null)
        {
            first = previous = frame;
            previousHashes = hashes;
            previousUniform = uniform;
            return Result.Added;
        }

        if (top < 0)
        {
            if (hashes.AsSpan().SequenceEqual(previousHashes)) return Result.End;
            FindStatic(previousHashes, hashes);
        }

        var band = Height - top - bottom;
        var shift = FindShift(previousHashes, previousUniform, hashes, band);
        if (shift == 0) return Result.End;
        if (shift < 0) return Result.Lost;

        // The new rows are the bottom `shift` rows of this frame's band.
        var strip = new uint[shift * Width];
        Array.Copy(frame, (top + band - shift) * Width, strip, 0, strip.Length);
        added.Add(strip);
        addedRows += shift;
        lastShift = shift;
        previous = frame;
        previousHashes = hashes;
        previousUniform = uniform;
        return Result.Added;
    }

    void FindStatic(int[] a, int[] b)
    {
        var limit = (int)(Height * MaxStaticShare);
        top = 0;
        while (top < limit && a[top] == b[top]) top++;
        bottom = 0;
        while (bottom < limit && a[Height - 1 - bottom] == b[Height - 1 - bottom]) bottom++;
    }

    /// The number of rows the band moved up between `a` and `b`: 0 if it did
    /// not move, -1 if no offset fits.
    int FindShift(int[] a, bool[] uniformA, int[] b, int band)
    {
        var best = -1;
        var bestScore = 0.0;
        // Unchanged band first: that is the end of the page.
        for (var shift = 0; shift < band - MinInformativeRows; shift++)
        {
            int compared = 0, agreed = 0;
            for (var i = 0; i < band - shift; i++)
            {
                // Rows of one flat colour match anywhere and prove nothing.
                if (uniformA[top + i + shift]) continue;
                compared++;
                if (a[top + i + shift] == b[top + i]) agreed++;
            }
            if (compared < MinInformativeRows) continue;
            var score = (double)agreed / compared;
            // Ties go to the shift nearest the last one: a page of repeating
            // rows fits several offsets, and the scroll amount barely changes.
            if (score > bestScore + 1e-9
                || (Math.Abs(score - bestScore) <= 1e-9 && Math.Abs(shift - lastShift) < Math.Abs(best - lastShift)))
            {
                best = shift;
                bestScore = score;
            }
            if (shift == 0 && score == 1) return 0;
        }
        return bestScore >= MinAgreement ? best : -1;
    }

    (int[] hashes, bool[] uniform) Hash(uint[] frame)
    {
        var hashes = new int[Height];
        var uniform = new bool[Height];
        var used = Width - ignoreRight;
        for (var y = 0; y < Height; y++)
        {
            var row = frame.AsSpan(y * Width, used);
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row));
            hashes[y] = hash.ToHashCode();
            uniform[y] = !row.ContainsAnyExcept(row[0]);
        }
        return (hashes, uniform);
    }

    /// The finished image: header and band of the first frame, every added
    /// strip, then the footer as it was last seen.
    public uint[] Compose()
    {
        if (first == null || previous == null) return [];
        var output = new uint[TotalHeight * Width];
        var footer = Math.Max(0, bottom);
        var head = Height - footer;
        Array.Copy(first, 0, output, 0, head * Width);
        var at = head * Width;
        foreach (var strip in added)
        {
            Array.Copy(strip, 0, output, at, strip.Length);
            at += strip.Length;
        }
        Array.Copy(previous, head * Width, output, at, footer * Width);
        return output;
    }
}
