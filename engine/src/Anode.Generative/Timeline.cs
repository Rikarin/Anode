using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     A stretch of the region in one arrangement section (or between sections): its kind, where its section starts,
///     and whether the section ends inside the region (so its last bar takes a fill).
/// </summary>
sealed record Segment(long Start, long End, long SectionStart, SectionKind Kind, bool EndsSection);

/// <summary>
///     A bar overlapping the region: its project ticks, its segment, its index in its section (from 0), and whether
///     it is its section's last.
/// </summary>
sealed record Bar(long Start, long Length, Segment Segment, int InSection, bool LastOfSection) {
    public long End => Start + Length;
}

/// <summary>
///     A chord over the region: from <c>Start</c> to <c>End</c>, its root and slash bass (pitch classes), quality and
///     intervals above the root.
/// </summary>
sealed record Harmony(long Start, long End, int Root, int Bass, string Quality, IReadOnlyList<int> Intervals) {
    /// <summary>Whether pitch class <paramref name="pc" /> is one of the chord's tones (or its slash bass).</summary>
    public bool Holds(int pc) {
        pc = Mod12(pc);
        if (pc == Bass) {
            return true;
        }

        foreach (var interval in Intervals) {
            if (Mod12(Root + interval) == pc) {
                return true;
            }
        }

        return false;
    }

    public static int Mod12(int value) => (value % 12 + 12) % 12;
}

/// <summary>
///     The region's place in the song: its bars (by the signatures), its section segments, its chords (the chord
///     track's, or its key's tonic chord when it does not follow them) and the scale at a tick.
/// </summary>
sealed class Timeline {
    static readonly Dictionary<ScaleKind, int[]> ScaleSteps = new() {
        [ScaleKind.Major] = [0, 2, 4, 5, 7, 9, 11],
        [ScaleKind.Minor] = [0, 2, 3, 5, 7, 8, 10],
        [ScaleKind.HarmonicMinor] = [0, 2, 3, 5, 7, 8, 11],
        [ScaleKind.MelodicMinor] = [0, 2, 3, 5, 7, 9, 11],
        [ScaleKind.Dorian] = [0, 2, 3, 5, 7, 9, 10],
        [ScaleKind.Phrygian] = [0, 1, 3, 5, 7, 8, 10],
        [ScaleKind.Lydian] = [0, 2, 4, 6, 7, 9, 11],
        [ScaleKind.Mixolydian] = [0, 2, 4, 5, 7, 9, 10],
        [ScaleKind.Locrian] = [0, 1, 3, 5, 6, 8, 10]
    };

    readonly GenerationInput input;

    public Timeline(GenerationInput input, bool followChords) {
        this.input = input;
        Start = input.StartTick;
        End = input.StartTick + input.LengthTicks;
        Segments = MakeSegments();
        Bars = MakeBars();
        Harmonies = MakeHarmonies(followChords);
    }

    public long Start { get; }
    public long End { get; }
    public IReadOnlyList<Segment> Segments { get; }
    public IReadOnlyList<Bar> Bars { get; }
    public IReadOnlyList<Harmony> Harmonies { get; }

    /// <summary>The chord sounding at <paramref name="tick" /> (the first before the region starts, the last after it ends).</summary>
    public Harmony HarmonyAt(long tick) {
        var found = Harmonies[0];
        foreach (var harmony in Harmonies) {
            if (harmony.Start > tick) {
                break;
            }

            found = harmony;
        }

        return found;
    }

    /// <summary>The scale at <paramref name="tick" />: its tonic and steps (pitch classes from the tonic).</summary>
    public (int Tonic, int[] Steps, bool Minor) ScaleAt(long tick) {
        ScaleChange? change = null;
        foreach (var scale in input.Chords.Scales) {
            if (scale.Tick <= tick) {
                change = scale;
            }
        }

        if (change is not null) {
            var kind = change.Scale;
            var minor = kind is not (ScaleKind.Major or ScaleKind.Lydian or ScaleKind.Mixolydian);
            return (PitchSources.PitchClass(change.Tonic), ScaleSteps[kind], minor);
        }

        KeyEvent? key = input.Keys.Count > 0 ? input.Keys[0] : null;
        foreach (var k in input.Keys) {
            if (k.Tick <= tick) {
                key = k;
            }
        }

        if (key is null) {
            return (0, ScaleSteps[ScaleKind.Major], false);
        }

        var minorKey = key.Mode == KeyMode.Minor;
        return (Harmony.Mod12(key.Tonic), ScaleSteps[minorKey ? ScaleKind.Minor : ScaleKind.Major], minorKey);
    }

    /// <summary>Whether pitch class <paramref name="pc" /> is in the scale at <paramref name="tick" />.</summary>
    public bool InScale(long tick, int pc) {
        var (tonic, steps, _) = ScaleAt(tick);
        return steps.Contains(Harmony.Mod12(pc - tonic));
    }

    List<Segment> MakeSegments() {
        var sections = input.Sections.OrderBy(static s => s.StartTick).ToList();
        var segments = new List<Segment>();
        var at = Start;
        while (at < End) {
            var section = sections.Find(s => s.StartTick <= at && at < s.StartTick + s.LengthTicks);
            if (section is not null) {
                var end = section.StartTick + section.LengthTicks;
                segments.Add(new(at, Math.Min(end, End), section.StartTick, SectionKinds.Of(section.Name), end <= End));
                at = Math.Min(end, End);
                continue;
            }

            // Between sections (or with none): a verse from the previous section's end to the next one's start, which
            // ends there (a fill before the next section, or at the region's end).
            var next = sections.Where(s => s.StartTick > at).Select(static s => s.StartTick).DefaultIfEmpty(End).Min();
            var previous = sections.Where(s => s.StartTick + s.LengthTicks <= at)
                .Select(static s => s.StartTick + s.LengthTicks)
                .DefaultIfEmpty(Start)
                .Max();
            segments.Add(new(at, Math.Min(next, End), Math.Min(previous, at), SectionKind.Verse, true));
            at = Math.Min(next, End);
        }

        return segments;
    }

    /// <summary>The bar lines by the signatures (4/4 before the first, or with none).</summary>
    List<(long Start, long Length)> BarLines(long until) {
        var signatures = input.Signatures.OrderBy(static s => s.Tick).ToList();
        var lines = new List<(long, long)>();
        long at = 0;
        var index = 0;
        long length = Ticks.Bar;
        while (at < until) {
            while (index < signatures.Count && signatures[index].Tick <= at) {
                var s = signatures[index++];
                length = Math.Max(1, (long)s.Numerator * Ticks.Bar / Math.Max(1, s.Denominator));
            }

            var next = at + length;
            // A signature change inside a bar starts a new bar there.
            if (index < signatures.Count && signatures[index].Tick < next) {
                next = signatures[index].Tick;
            }

            lines.Add((at, next - at));
            at = next;
        }

        return lines;
    }

    List<Bar> MakeBars() {
        var lines = BarLines(End);
        var bars = new List<Bar>();
        for (var i = 0; i < lines.Count; i++) {
            var (start, length) = lines[i];
            if (start + length <= Start) {
                continue;
            }

            var probe = Math.Max(start, Start);
            var segment = Segments.Last(s => s.Start <= probe);
            // The section's first bar is the one its start falls in.
            var first = i;
            while (first > 0 && lines[first].Start > segment.SectionStart) {
                first--;
            }

            var last = segment.EndsSection && segment.End > start && segment.End <= start + length;
            bars.Add(new(start, length, segment, i - first, last));
        }

        return bars;
    }

    List<Harmony> MakeHarmonies(bool followChords) {
        var chords = followChords ? input.Chords.Chords.OrderBy(static c => c.Tick).ToList() : [];
        var (tonic, _, minor) = ScaleAt(Start);
        var tonicChord = new Harmony(
            Start,
            End,
            tonic,
            tonic,
            minor ? ChordQualities.Minor : ChordQualities.Major,
            ChordQualities.Intervals(minor ? ChordQualities.Minor : ChordQualities.Major)
        );
        var spans = new List<Harmony>();
        var before = chords.LastOrDefault(c => c.Tick <= Start);
        var inside = chords.Where(c => c.Tick > Start && c.Tick < End).ToList();
        if (before is null && inside.Count > 0) {
            spans.Add(tonicChord with { End = inside[0].Tick });
        }

        var all = before is null ? inside : [before, ..inside];
        for (var i = 0; i < all.Count; i++) {
            var chord = all[i];
            var start = Math.Max(Start, chord.Tick);
            var end = i + 1 < all.Count ? all[i + 1].Tick : End;
            var root = PitchSources.PitchClass(chord.Root);
            var quality = ChordQualities.IsKnown(chord.Quality) ? chord.Quality : ChordQualities.Major;
            spans.Add(
                new(
                    start,
                    end,
                    root,
                    chord.Bass is null ? root : PitchSources.PitchClass(chord.Bass),
                    quality,
                    ChordQualities.Intervals(quality)
                )
            );
        }

        return spans.Count == 0 ? [tonicChord] : spans;
    }
}
