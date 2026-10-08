// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

namespace ScreenX;

/// Joins the frames of a scrolling capture into one tall image.
///
/// Pure: frames go in as pixel arrays and an image comes out, so all of it is
/// tested without a screen. Every frame is the same region of the screen,
/// taken after scrolling down.
///
/// A frame splits into three bands. The header and footer are rows that did
/// not change between the first two frames (a toolbar, a sticky nav bar, a
/// status bar) and appear once. The band between them is what scrolled, and
/// each new frame adds the rows it brought into view.
///
/// Rows are matched exactly first, by hash. That is not enough on its own:
/// DirectWrite and WinUI place text at fractional positions, so Notepad
/// scrolled by 178 px draws every line a little differently from the line it
/// was, and the best exact match was 70% of rows. The second pass compares
/// each row's brightness profile and takes the offset that is clearly better
/// than any other.
sealed class Stitcher
{
    public enum Result { Added, End, Lost }

    /// The longest header or footer, as a share of the frame. A run of blank
    /// rows at the top would otherwise all count as header.
    const double MaxStaticShare = 0.4;
    /// How many overlapping rows must agree exactly to accept an offset. Not
    /// all of them: a blinking caret or an animation changes a few.
    const double MinAgreement = 0.9;
    const int MinInformativeRows = 8;
    /// Mean brightness difference per profile bin, 0-255, below which two rows
    /// are the same row drawn at a slightly different position.
    const float MaxProfileDistance = 8;
    /// And the best offset must be this much better than the next best that
    /// is not its neighbour, or the match is a guess.
    const float MinProfileMargin = 0.5f;

    public readonly int Width, Height;
    /// Columns left out of matching on the right: a scrollbar thumb moves
    /// every frame and would make every row it touches look different.
    readonly int used;
    readonly int bins;

    uint[]? first, previous;
    Rows? last;
    int top = -1, bottom;
    readonly List<uint[]> added = [];
    int addedRows;
    int lastShift;

    public Stitcher(int width, int height, int ignoreRight)
    {
        Width = width;
        Height = height;
        used = width - Math.Clamp(ignoreRight, 0, width / 2);
        bins = Math.Clamp(used / 8, 1, 64);
    }

    /// Rows in the finished image so far.
    public int TotalHeight => first == null ? 0 : Height + addedRows;

    /// The most recent offset, in rows; 0 before the first scroll.
    public int LastShift => lastShift;

    /// Everything about a frame's rows that matching needs.
    sealed record Rows(int[] Hashes, float[] Profiles, bool[] Flat);

    public Result Add(uint[] frame)
    {
        if (frame.Length != Width * Height) throw new ArgumentException("frame size changed");
        var rows = Describe(frame);
        if (first == null || last == null)
        {
            first = previous = frame;
            last = rows;
            return Result.Added;
        }

        if (top < 0)
        {
            if (rows.Hashes.AsSpan().SequenceEqual(last.Hashes)) return Result.End;
            FindStatic(last.Hashes, rows.Hashes);
        }

        var band = Height - top - bottom;
        var shift = ExactShift(last, rows, band);
        if (shift < 0) shift = NearShift(last, rows, band);
        if (shift == 0) return Result.End;
        if (shift < 0) return Result.Lost;

        // The new rows are the bottom `shift` rows of this frame's band.
        var strip = new uint[shift * Width];
        Array.Copy(frame, (top + band - shift) * Width, strip, 0, strip.Length);
        added.Add(strip);
        addedRows += shift;
        lastShift = shift;
        previous = frame;
        last = rows;
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

    /// Prefer the shift nearest the last one when two fit equally: a page of
    /// repeating rows fits several offsets, and the scroll amount barely
    /// changes between steps.
    bool Nearer(int shift, int than) => Math.Abs(shift - lastShift) < Math.Abs(than - lastShift);

    /// The rows the band moved up between `a` and `b` by exact match: 0 if it
    /// did not move, -1 if no offset fits well enough.
    int ExactShift(Rows a, Rows b, int band)
    {
        var best = -1;
        var bestScore = 0.0;
        for (var shift = 0; shift < band - MinInformativeRows; shift++)
        {
            int compared = 0, agreed = 0;
            for (var i = 0; i < band - shift; i++)
            {
                // Rows of one flat colour match anywhere and prove nothing.
                if (a.Flat[top + i + shift]) continue;
                compared++;
                if (a.Hashes[top + i + shift] == b.Hashes[top + i]) agreed++;
            }
            if (compared < MinInformativeRows) continue;
            var score = (double)agreed / compared;
            if (shift == 0 && score == 1) return 0;
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && Nearer(shift, best)))
            {
                best = shift;
                bestScore = score;
            }
        }
        return bestScore >= MinAgreement ? best : -1;
    }

    /// The same, by brightness profile, for content drawn at fractional
    /// positions.
    int NearShift(Rows a, Rows b, int band)
    {
        var distances = new float[band];
        distances.AsSpan().Fill(float.MaxValue);
        for (var shift = 0; shift < band - MinInformativeRows; shift++)
        {
            double sum = 0;
            var compared = 0;
            for (var i = 0; i < band - shift; i++)
            {
                var row = top + i + shift;
                if (a.Flat[row]) continue;
                compared++;
                sum += Distance(a.Profiles.AsSpan(row * bins, bins), b.Profiles.AsSpan((top + i) * bins, bins));
            }
            if (compared >= MinInformativeRows) distances[shift] = (float)(sum / compared);
        }

        var best = -1;
        for (var shift = 0; shift < band; shift++)
            if (distances[shift] < float.MaxValue
                && (best < 0 || distances[shift] < distances[best] - 1e-4f
                    || (Math.Abs(distances[shift] - distances[best]) <= 1e-4f && Nearer(shift, best))))
                best = shift;
        if (best < 0 || distances[best] > MaxProfileDistance) return -1;

        // A fractional scroll matches the offsets either side of it almost as
        // well, so the runner-up is taken from outside that neighbourhood.
        var runnerUp = float.MaxValue;
        for (var shift = 0; shift < band; shift++)
            if (Math.Abs(shift - best) > 2) runnerUp = Math.Min(runnerUp, distances[shift]);
        return distances[best] <= runnerUp * MinProfileMargin ? best : -1;
    }

    static float Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    Rows Describe(uint[] frame)
    {
        var hashes = new int[Height];
        var profiles = new float[Height * bins];
        var flat = new bool[Height];
        var perBin = used / bins;
        for (var y = 0; y < Height; y++)
        {
            var row = frame.AsSpan(y * Width, used);
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row));
            hashes[y] = hash.ToHashCode();

            float low = float.MaxValue, high = float.MinValue;
            for (var bin = 0; bin < bins; bin++)
            {
                var total = 0;
                foreach (var p in row.Slice(bin * perBin, perBin))
                    total += (int)(((p >> 16) & 0xFF) * 3 + ((p >> 8) & 0xFF) * 6 + (p & 0xFF)) ;
                var mean = total / (perBin * 10f);
                profiles[y * bins + bin] = mean;
                low = Math.Min(low, mean);
                high = Math.Max(high, mean);
            }
            // Flat: one colour, or so nearly one that its profile says nothing.
            flat[y] = !row.ContainsAnyExcept(row[0]) || high - low < 1;
        }
        return new Rows(hashes, profiles, flat);
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
