// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Numerics;

namespace ScreenX;

/// Joins the frames of a scrolling capture into one tall image.
///
/// Pure: frames go in as pixel arrays and an image comes out, so all of it is
/// tested without a screen. Every frame is the same region of the screen,
/// taken after scrolling down.
///
/// Rows. A frame splits into three bands. The header is rows that did not
/// change since the previous frame (a toolbar, a sticky nav bar), worked out
/// again for every pair, because a sticky header often appears only once the
/// page has scrolled. The footer (a status bar) is fixed by the first pair,
/// because the strips are cut from just above it and must stay continuous.
/// The band between them is what scrolled, and each new frame adds the rows it
/// brought into view at the bottom of it.
///
/// Columns. A whole window is rarely one thing scrolling: VS Code has an
/// activity bar that does not move, a minimap that moves at its own rate, and
/// the text. Matching whole rows found nothing at all. So the width is cut
/// into vertical strips, each strip finds its own offset, strips that did not
/// move are set aside, and the offset most of the rest agree on wins.
///
/// Matching is exact first, by hash. That is not enough on its own:
/// DirectWrite and WinUI place text at fractional positions, so Notepad
/// scrolled by 178 px draws every line a little differently from the line it
/// was, and the best exact match was 70% of rows. The second pass compares
/// brightness profiles and takes the offset that is clearly better than any
/// other.
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
    /// is not its neighbour, or that strip's match is a guess.
    const float MinProfileMargin = 0.5f;
    /// Strip width to aim for. Narrow enough that a sidebar or a minimap gets
    /// strips of its own, wide enough that a strip of text is distinctive.
    const int StripWidth = 48;

    public readonly int Width, Height;
    /// Columns left out of matching on the right: a scrollbar thumb moves
    /// every frame and would make every row it touches look different.
    readonly int used;
    readonly int strips, stripWidth, bins;

    uint[]? first, previous;
    Rows? last;
    int top, bottom = -1;
    readonly List<uint[]> added = [];
    /// Per added row, per strip: unchanged since the frame before.
    readonly List<bool[]> unchanged = [];
    int addedRows;
    int lastShift;
    int expected;

    public Stitcher(int width, int height, int ignoreRight)
    {
        Width = width;
        Height = height;
        used = width - Math.Clamp(ignoreRight, 0, width / 2);
        strips = Math.Clamp(used / StripWidth, 1, 64);
        stripWidth = used / strips;
        bins = Math.Clamp(stripWidth / 8, 1, 8);
    }

    /// Rows in the finished image so far.
    public int TotalHeight => first == null ? 0 : Height + addedRows;

    /// The most recent offset, in rows; 0 before the first scroll.
    public int LastShift => lastShift;

    int Band => Height - top - Math.Max(0, bottom);

    /// Overlap is what an offset is proved by. A small one is proved by a
    /// handful of rows, which repeat by coincidence often enough to matter.
    int MinOverlap(int band) => Math.Max(MinInformativeRows, band / 4);

    /// The largest scroll that can still be matched. The driver keeps its
    /// steps below this.
    public int MaxShift => Band - MinOverlap(Band);

    /// Everything about a frame's rows that matching needs. Per-strip arrays
    /// are strip-major: strip `s`, row `y` is at `s * Height + y`.
    /// `Count` strips of `StripWidth` columns, each row summarised in `Bins`
    /// brightness bins. Kept twice per frame: as narrow strips, and as one
    /// strip the whole width, which is the fallback.
    sealed record Rows(int[] Whole, int[] Hashes, bool[] Flat, float[] Profiles, int Count, int StripWidth, int Bins);

    /// `guess` is the driver's estimate of how far this step scrolled, in rows.
    /// It only decides between offsets that fit equally well, which repeating
    /// content (a grid, identical list rows) always has. Without one, the last
    /// offset seen is used instead.
    public Result Add(uint[] frame, int guess = 0)
    {
        if (frame.Length != Width * Height) throw new ArgumentException("frame size changed");
        var rows = Describe(frame, strips);
        var whole = Describe(frame, 1);
        if (first == null || last == null || lastWhole == null)
        {
            first = previous = frame;
            last = rows;
            lastWhole = whole;
            return Result.Added;
        }
        if (rows.Whole.AsSpan().SequenceEqual(last.Whole)) return Result.End;
        // The driver's estimate follows the step size, which it changes every
        // step; the last offset alone was a whole period out on repeating
        // content once the step grew.
        expected = guess > 0 ? guess : lastShift;

        // Header and footer, strip by strip, so a bar across part of the width
        // counts: a notification in a corner, a horizontal scrollbar track. As
        // whole rows they were neither, and were copied into every strip that
        // was added, a line every step down the finished image.
        var limit = (int)(Height * MaxStaticShare);
        if (bottom < 0)
        {
            // A strip still for this long is a sidebar, not a footer: it is
            // filled with its background in the added rows (see Compose).
            still = new bool[strips];
            movedSteps = new int[strips];
            bottom = 0;
            for (var s = 0; s < strips; s++)
            {
                var run = StillRun(last, rows, s, fromTop: false, limit);
                if (run >= limit) still[s] = true;
                else bottom = Math.Max(bottom, run);
            }
            // And the columns no strip covers: the scrollbar margin on the
            // right, where the end of a notification also sits.
            var margin = strips * stripWidth;
            if (margin < Width && previous != null)
            {
                var run = 0;
                while (run < limit && previous.AsSpan((Height - 1 - run) * Width + margin, Width - margin)
                           .SequenceEqual(frame.AsSpan((Height - 1 - run) * Width + margin, Width - margin)))
                    run++;
                if (run < limit) bottom = Math.Max(bottom, run);
            }
        }
        top = 0;
        for (var s = 0; s < strips; s++)
            if (!still[s]) top = Math.Max(top, StillRun(last, rows, s, fromTop: true, limit));
        top = Math.Min(top, Height - bottom - 2 * MinOverlap(Height - bottom));
        if (drawn.Length == 0)
        {
            // Within the band: a toolbar above a blank strip is not content.
            drawn = new bool[strips];
            for (var s = 0; s < strips; s++)
                drawn[s] = Array.IndexOf(last.Flat, false, s * Height + top, Band) >= 0;
        }

        var shift = Vote(last, rows, Band, track: true);
        // Narrow strips of uniform content (a document of similar lines) can
        // each be too alike to decide alone, where the whole row is not. The
        // whole width as one strip is how this matched before strips existed.
        // Its rows include any sidebar, though, so sparse content beside a wide
        // still pane matches at 0 and read as the end. With a sidebar known
        // that 0 means nothing; lost makes the driver retry a smaller step. A
        // real end is caught above, when the frame did not change at all.
        if (shift < 0)
        {
            shift = Vote(lastWhole, whole, Band, track: false);
            // Only a sidebar with something in it: the blank right-hand strips
            // of a document are "sidebars" too, and counting them turned a
            // real end with a blinking caret into lost.
            if (shift == 0 && Enumerable.Range(0, strips).Any(s => Sidebar(s) && drawn[s])) shift = -1;
        }
        if (shift == 0) return Result.End;
        if (shift < 0) return Result.Lost;

        // Whether each strip moved, by looking rather than by whether its own
        // match succeeded: a strip that is blank in the first pair and gets
        // long lines later has no match of its own, and was then treated as a
        // sidebar and painted over. But not on any change at all: a panel that
        // highlights the current heading, a spinner or a badge then stopped
        // being a sidebar and its rows repeated down the capture. Moved is
        // most of what is drawn in the strip changing; content arriving in a
        // blank strip is all change, a highlight in a still panel is little.
        // Or what is drawn fitting the scroll as well as it fits standing
        // still: a list column of the same value on every row ("Yes", a
        // checkbox) scrolled by whole rows barely changes, and was taken for
        // a sidebar and painted over. A sidebar's icons do not fit the scroll.
        for (var s = 0; s < strips; s++)
        {
            int changed = 0, kept = 0, fitsStill = 0, fitsScroll = 0;
            for (var y = top; y < top + Band; y++)
            {
                var i = s * Height + y;
                if (last.Flat[i] && rows.Flat[i]) continue;
                if (rows.Hashes[i] != last.Hashes[i]) changed++;
                else kept++;
                if (y + shift >= top + Band || rows.Flat[i]) continue;
                if (rows.Hashes[i] == last.Hashes[i]) fitsStill++;
                if (rows.Hashes[i] == last.Hashes[i + shift]) fitsScroll++;
            }
            if ((changed > 0 && changed >= kept) || (fitsScroll > 0 && fitsScroll >= fitsStill)) movedSteps[s]++;
        }

        // The new rows are the bottom `shift` rows of this frame's band, just
        // above the footer.
        var from = Height - bottom - shift;
        var strip = new uint[shift * Width];
        Array.Copy(frame, from * Width, strip, 0, strip.Length);
        added.Add(strip);
        // Which of the new rows sat still in each strip: only those are ever
        // repainted as a sidebar, so content arriving in a strip taken for
        // one is kept.
        var same = new bool[shift * strips];
        for (var k = 0; k < shift; k++)
            for (var s = 0; s < strips; s++)
                same[k * strips + s] = rows.Hashes[s * Height + from + k] == last.Hashes[s * Height + from + k];
        unchanged.Add(same);
        addedRows += shift;
        lastShift = shift;
        previous = frame;
        last = rows;
        lastWhole = whole;
        return Result.Added;
    }

    Rows? lastWhole;

    /// How many of an overlap's informative rows to pass over before judging.
    /// Only the lower part is judged: a sticky header that appears once the
    /// page has scrolled covers the top of the new frame, and on that first
    /// pair it is not yet known to be a header. Matching from the bottom, where
    /// new content arrives, is what ShareX does too.
    ///
    /// Counted in informative rows, not rows: at the end of a short file the
    /// content is all at the top of the overlap and blank space fills the
    /// rest, and skipping by rows skipped everything there was.
    int Skipped(Rows a, int offset, int shift, int overlap)
    {
        var informative = 0;
        for (var i = 0; i < overlap; i++)
            if (!a.Flat[offset + top + i + shift]) informative++;
        return informative * 2 / 5;
    }

    bool Nearer(int shift, int than) => Math.Abs(shift - expected) < Math.Abs(than - expected);

    /// Strips that sat still through the first scroll, and whether each has
    /// moved since. A strip that never moves is a sidebar or an activity bar.
    bool[] still = [];
    /// Strips with anything but flat rows in the band of the first frame.
    bool[] drawn = [];
    int[] movedSteps = [];

    bool Sidebar(int s) => still.Length > s && still[s] && movedSteps[s] == 0;

    /// Rows unchanged at the same place in strip `s`, from the top or the
    /// bottom, up to `limit`.
    ///
    /// From the bottom it tolerates a few changed rows: a notification sits a
    /// little above the status bar with moving content (VS Code's minimap)
    /// between them, and stopping at that gap left the notification in the
    /// band, copied into every added strip. The run is the deepest point with
    /// at least `Tolerance` of the rows below it unchanged.
    int StillRun(Rows a, Rows b, int s, bool fromTop, int limit)
    {
        var o = s * Height;
        if (fromTop)
        {
            var run = 0;
            while (run < limit && a.Hashes[o + run] == b.Hashes[o + run]) run++;
            return run;
        }
        int deepest = 0, unchanged = 0;
        for (var k = 1; k <= limit; k++)
        {
            var y = Height - k;
            if (a.Hashes[o + y] != b.Hashes[o + y]) continue;
            unchanged++;
            if (unchanged >= k * Tolerance) deepest = k;
        }
        return deepest;
    }

    const double Tolerance = 0.85;

    /// Each strip's own answer, then the one most of them agree on: 0 if
    /// nothing moved, -1 if what moved could not be matched.
    int Vote(Rows a, Rows b, int band, bool track)
    {
        // The content has gone: scrolled past the last line into blank space,
        // as VS Code allows. That is the end. Judged by the strips that had
        // something in them before, most of which are now empty; not all,
        // because VS Code's minimap keeps drawing down the side. (A page with
        // an empty gap taller than the whole area would stop there too; rare
        // enough to accept, and the capture so far is kept.)
        if (ContentVanished(a, b, band)) return 0;

        var votes = new Dictionary<int, double>();
        var guesses = new Dictionary<int, double>();
        double stillWeight = 0, unmatched = 0;
        var pending = new List<int>();
        for (var s = 0; s < a.Count; s++)
        {
            var (shift, score, compared, ambiguous) = ExactShift(a, b, s, band);
            if (compared == 0) continue;
            if (score < MinAgreement) { pending.Add(s); continue; }
            // A known sidebar is still by definition and says nothing about
            // whether the page moved: counted, a wide one turned every failed
            // match into "the end".
            if (shift == 0) stillWeight += track && Sidebar(s) ? 0 : compared;
            // A strip that fits several offsets (the same words on every line
            // of a document) cannot tell them apart and must not outvote one
            // that can (the line numbers). Notepad locked onto one line height
            // that way while the real scroll was six times larger.
            else if (ambiguous) guesses[shift] = guesses.GetValueOrDefault(shift) + compared;
            else votes[shift] = votes.GetValueOrDefault(shift) + compared;
        }
        // Nothing distinctive anywhere: an empty grid. Guess, which is what
        // the ambiguous strips did, each choosing nearest the expected offset.
        if (votes.Count == 0 && guesses.Count > 0) votes = guesses;
        // Strips that no exact offset fits get the profile pass. When exact
        // votes already exist they are only a cross-check, so skip the cost.
        if (votes.Count == 0)
        {
            foreach (var s in pending)
            {
                var (shift, compared) = NearShift(a, b, s, band);
                if (shift < 0) unmatched += compared;
                else if (shift == 0) stillWeight += track && Sidebar(s) ? 0 : compared;
                else votes[shift] = votes.GetValueOrDefault(shift) + compared;
            }
        }
        if (votes.Count == 0)
            // Nothing moved that could be matched. If most of the picture sat
            // still, the scroll did nothing: the end. If there is nothing in
            // it at all, it has scrolled past the last line into blank space,
            // which VS Code allows: also the end. Otherwise it is lost.
            return (stillWeight > 0 && stillWeight >= unmatched) || (stillWeight == 0 && unmatched == 0 && pending.Count == 0) ? 0 : -1;

        // Neighbouring offsets are one answer: a fractional scroll rounds
        // either way in different strips.
        var best = -1;
        var bestWeight = 0.0;
        foreach (var shift in votes.Keys)
        {
            var weight = votes.GetValueOrDefault(shift - 1) + votes[shift] + votes.GetValueOrDefault(shift + 1);
            if (weight > bestWeight + 1e-9 || (Math.Abs(weight - bestWeight) <= 1e-9 && Nearer(shift, best)))
            {
                best = shift;
                bestWeight = weight;
            }
        }
        return best;
    }

    /// One strip's offset by exact match, with its score and how many rows
    /// that rests on. Of offsets that fit equally, the nearest the expected
    /// one: repeating content is guessed at rather than refused, because a
    /// slightly wrong capture can be fixed in the editor and no capture cannot.
    (int shift, double score, int compared, bool ambiguous) ExactShift(Rows a, Rows b, int s, int band)
    {
        var offset = s * Height;
        var scores = new double[band + 1];
        scores.AsSpan().Fill(-1);
        var best = -1;
        var bestCompared = 0;
        for (var shift = 0; shift <= band - MinOverlap(band); shift++)
        {
            int compared = 0, agreed = 0;
            var skip = Skipped(a, offset, shift, band - shift);
            for (var i = 0; i < band - shift; i++)
            {
                // Rows of one flat colour match anywhere and prove nothing.
                if (a.Flat[offset + top + i + shift] || skip-- > 0) continue;
                compared++;
                if (a.Hashes[offset + top + i + shift] == b.Hashes[offset + top + i]) agreed++;
            }
            if (compared < MinInformativeRows) continue;
            var score = scores[shift] = (double)agreed / compared;
            if (best < 0 || score > scores[best] + 1e-9 || (Math.Abs(score - scores[best]) <= 1e-9 && Nearer(shift, best)))
            {
                best = shift;
                bestCompared = compared;
            }
        }
        if (best < 0) return (-1, 0, 0, false);
        // Another offset away from this one that fits about as well.
        var ambiguous = false;
        for (var shift = 0; shift < scores.Length && !ambiguous; shift++)
            ambiguous = Math.Abs(shift - best) > 2 && scores[shift] >= Math.Max(MinAgreement, scores[best] - RivalMargin);
        return (best, scores[best], bestCompared, ambiguous);
    }

    /// Offsets within this much of the best are rivals for it.
    const double RivalMargin = 0.02;

    /// The same by brightness profile, for content drawn at fractional
    /// positions. -1 when no offset is clearly the best.
    (int shift, int compared) NearShift(Rows a, Rows b, int s, int band)
    {
        var offset = s * Height;
        var distances = new float[band];
        distances.AsSpan().Fill(float.MaxValue);
        var mostCompared = 0;
        for (var shift = 0; shift <= band - MinOverlap(band); shift++)
        {
            double sum = 0;
            var compared = 0;
            var skip = Skipped(a, offset, shift, band - shift);
            for (var i = 0; i < band - shift; i++)
            {
                var row = offset + top + i + shift;
                if (a.Flat[row] || skip-- > 0) continue;
                compared++;
                sum += Distance(a.Profiles.AsSpan(row * a.Bins, a.Bins), b.Profiles.AsSpan((offset + top + i) * a.Bins, a.Bins));
            }
            if (compared < MinInformativeRows) continue;
            distances[shift] = (float)(sum / compared);
            mostCompared = Math.Max(mostCompared, compared);
        }

        var best = -1;
        for (var shift = 0; shift < band; shift++)
            if (distances[shift] < float.MaxValue
                && (best < 0 || distances[shift] < distances[best] - 1e-4f
                    || (Math.Abs(distances[shift] - distances[best]) <= 1e-4f && Nearer(shift, best))))
                best = shift;
        if (best < 0 || distances[best] > MaxProfileDistance) return (-1, mostCompared);

        // A fractional scroll matches the offsets either side of it almost as
        // well, so the runner-up is taken from outside that neighbourhood.
        var runnerUp = float.MaxValue;
        for (var shift = 0; shift < band; shift++)
            if (Math.Abs(shift - best) > 2) runnerUp = Math.Min(runnerUp, distances[shift]);
        return distances[best] <= runnerUp * MinProfileMargin ? (best, mostCompared) : (-1, mostCompared);
    }

    /// Mean absolute difference, vectorised where the strip is wide enough.
    static float Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var total = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            total += Vector.Abs(new Vector<float>(a[i..]) - new Vector<float>(b[i..]));
        var sum = Vector.Sum(total);
        for (; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    Rows Describe(uint[] frame, int strips)
    {
        var stripWidth = used / strips;
        var bins = strips == 1 ? Math.Clamp(used / 8, 1, 64) : Math.Clamp(stripWidth / 8, 1, 8);
        var whole = new int[Height];
        var hashes = new int[strips * Height];
        var flat = new bool[strips * Height];
        var profiles = new float[strips * Height * bins];
        var perBin = stripWidth / bins;
        for (var y = 0; y < Height; y++)
        {
            var row = frame.AsSpan(y * Width, used);
            var all = new HashCode();
            all.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row));
            whole[y] = all.ToHashCode();

            for (var s = 0; s < strips; s++)
            {
                var part = row.Slice(s * stripWidth, stripWidth);
                var hash = new HashCode();
                hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(part));
                var index = s * Height + y;
                hashes[index] = hash.ToHashCode();

                float low = float.MaxValue, high = float.MinValue;
                for (var bin = 0; bin < bins; bin++)
                {
                    var total = 0;
                    foreach (var p in part.Slice(bin * perBin, perBin))
                        total += (int)(((p >> 16) & 0xFF) * 3 + ((p >> 8) & 0xFF) * 6 + (p & 0xFF));
                    var mean = total / (perBin * 10f);
                    profiles[index * bins + bin] = mean;
                    low = Math.Min(low, mean);
                    high = Math.Max(high, mean);
                }
                // Flat: one colour, or so nearly one that its profile says
                // nothing. Most of a sidebar strip is flat, and so it votes
                // only on the rows that have something in them.
                flat[index] = !part.ContainsAnyExcept(part[0]) || (bins > 1 && high - low < 1);
            }
        }
        // Also flat: a row the same as the three above and the three below it.
        // That is empty space with edges in it, such as VS Code past its last
        // line, where the gutter and the editor are different colours and no
        // row is one colour. Antialiased text does not repeat seven identical
        // rows; if something does, it is only left out of the evidence.
        const int reach = 3;
        for (var s = 0; s < strips; s++)
        {
            var o = s * Height;
            for (var y = reach; y < Height - reach; y++)
            {
                var same = true;
                for (var d = 1; d <= reach && same; d++)
                    same = hashes[o + y - d] == hashes[o + y] && hashes[o + y + d] == hashes[o + y];
                if (same) flat[o + y] = true;
            }
        }
        return new Rows(whole, hashes, flat, profiles, strips, stripWidth, bins);
    }

    /// Almost all of what was there is gone, rather than moved. Counted in
    /// strip-rows with something in them, leaving out rows that are the same
    /// in both frames (a status line, a scrollbar track): content that only
    /// moved up is still there in about the same amount, and content that
    /// scrolled out of view leaves next to nothing. A sticky-scroll remnant or
    /// a minimap is that next to nothing.
    bool ContentVanished(Rows a, Rows b, int band)
    {
        int before = 0, after = 0;
        for (var s = 0; s < a.Count; s++)
            for (var y = top; y < top + band; y++)
            {
                var i = s * Height + y;
                if (a.Hashes[i] == b.Hashes[i]) continue;
                if (!a.Flat[i]) before++;
                if (!b.Flat[i]) after++;
            }
        return before >= MinInformativeRows && after < before * 0.15;
    }

    /// Strips on the right that the tail trim lets differ: where a minimap or
    /// a scrollbar is still drawn after the content has run out.
    const int SideStrips = 2;

    /// Drop blank rows from the end of what was added, keeping a small margin.
    ///
    /// An editor that scrolls past its last line (VS Code does by default)
    /// fills the last steps with empty background, and a capture of a short
    /// file came back mostly blank. Blank here is a run of rows at the end
    /// that match the last row everywhere but the rightmost strips: empty space
    /// whatever its colours (VS Code's gutter and editor differ), allowing for
    /// a minimap still drawn down the right. A short line such as a closing
    /// brace sits on the left, so it is never mistaken for blank.
    public void TrimBlankTail(int keep)
    {
        if (added.Count == 0) return;
        var final = added[^1];
        var model = final.AsSpan(final.Length - Width, used).ToArray();
        // Sidebars are left out too: Compose repaints them anyway. But some
        // strip must be judged, or every row reads as blank: a narrow area
        // with an icon column cut a capture from 1173 rows to 324.
        var judged = Enumerable.Range(0, Math.Max(1, strips - SideStrips)).Where(s => !Sidebar(s)).ToArray();
        if (judged.Length == 0) judged = Enumerable.Range(0, strips).Where(s => !Sidebar(s)).ToArray();
        if (judged.Length == 0) return;
        bool Blank(ReadOnlySpan<uint> row)
        {
            foreach (var s in judged)
                if (!row.Slice(s * stripWidth, stripWidth).SequenceEqual(model.AsSpan(s * stripWidth, stripWidth)))
                    return false;
            return true;
        }
        var blank = 0;
        for (var i = added.Count - 1; i >= 0; i--)
        {
            var strip = added[i];
            var rows = strip.Length / Width;
            var y = rows - 1;
            while (y >= 0 && Blank(strip.AsSpan(y * Width, used))) y--;
            blank += rows - 1 - y;
            if (y >= 0) break;
        }
        var cut = blank - keep;
        while (cut > 0 && added.Count > 0)
        {
            var strip = added[^1];
            var rows = strip.Length / Width;
            if (rows <= cut)
            {
                added.RemoveAt(added.Count - 1);
                unchanged.RemoveAt(unchanged.Count - 1);
                addedRows -= rows;
                cut -= rows;
            }
            else
            {
                added[^1] = strip.AsSpan(0, (rows - cut) * Width).ToArray();
                unchanged[^1] = unchanged[^1][..((rows - cut) * strips)];
                addedRows -= cut;
                cut = 0;
            }
        }
    }

    /// The finished image: the first frame down to the footer, every added
    /// strip, then the footer as it was last seen. Written into `output`, which
    /// must hold `TotalHeight` rows, so the caller can put it straight where it
    /// is needed instead of holding a second copy of a tall image.
    public void Compose(Span<uint> output)
    {
        if (first == null || previous == null) return;
        if (output.Length < TotalHeight * Width) throw new ArgumentException("output too small", nameof(output));
        var footer = Math.Max(0, bottom);
        var head = Height - footer;
        first.AsSpan(0, head * Width).CopyTo(output);
        var at = head * Width;
        // A sidebar never moved, so every added row of it is the same icons
        // again: a column of repeated activity-bar buttons down the image. It
        // is continued as its own background instead.
        var colours = new uint?[strips];
        for (var s = 0; s < strips; s++)
            if (Sidebar(s)) colours[s] = Background(first, s, head);
        for (var i = 0; i < added.Count; i++)
        {
            added[i].CopyTo(output[at..]);
            for (var k = 0; k < added[i].Length / Width; k++, at += Width)
                for (var s = 0; s < strips; s++)
                    if (colours[s] is { } colour && unchanged[i][k * strips + s])
                        output.Slice(at + s * stripWidth, stripWidth).Fill(colour);
        }
        previous.AsSpan(head * Width, footer * Width).CopyTo(output[at..]);
    }

    /// The commonest colour of strip `s` in the top `rows` of `frame`.
    uint Background(uint[] frame, int s, int rows)
    {
        var counts = new Dictionary<uint, int>();
        for (var y = 0; y < rows; y += 2)
            for (var x = s * stripWidth; x < (s + 1) * stripWidth; x += 3)
            {
                var p = frame[y * Width + x];
                counts[p] = counts.GetValueOrDefault(p) + 1;
            }
        return counts.Count == 0 ? 0xFF000000 : counts.MaxBy(c => c.Value).Key;
    }

    public uint[] Compose()
    {
        var output = new uint[TotalHeight * Width];
        Compose(output);
        return output;
    }
}
