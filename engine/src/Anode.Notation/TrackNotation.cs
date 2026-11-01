using Anode.Contracts;
using Anode.Model;

namespace Anode.Notation;

/// <summary>A note of a track's regions in project ticks (cut at its region's end), with where it is drawn.</summary>
sealed class SourceNote(Note note, MidiRegion region, long start, long end) {
    public long Id { get; } = note.Id;
    public int Pitch { get; } = note.Pitch;
    public MidiRegion Region { get; } = region;
    public long Start { get; } = start;
    public long End { get; } = end;
    public int Staff { get; set; }
    public long DisplayStart { get; set; }
    public long DisplayEnd { get; set; }
}

/// <summary>
///     Notes of one staff drawn together (the same display start and end: one chord), assigned to a lane (a line of
///     non-overlapping units) and then to a voice.
/// </summary>
sealed class Unit(List<SourceNote> notes, int staff, long start, long end) {
    public List<SourceNote> Notes { get; } = notes;
    public int Staff { get; } = staff;
    public long Start { get; } = start;
    public long End { get; set; } = end;
    public int Lane { get; set; }
    public int Voice { get; set; } = 1;

    /// <summary>The tied segments drawn so far.</summary>
    public int Segments { get; set; }

    public RegionNotation Notation => Notes[0].Region.Notation;
    public int Top => Notes.Max(static n => n.Pitch);
    public double Mean => Notes.Average(static n => n.Pitch);
}

/// <summary>
///     One track's notes as units on its staves (docs/notation/README.md): staves by split point, display quantise,
///     chords, lanes by overlap (no overlap), interpretation, and voices numbered from the top within each polyphonic
///     passage.
/// </summary>
sealed class TrackNotation {
    TrackNotation(Track track, StaffStyle style, List<MidiRegion> regions, Quantizer quantizer, List<Unit>[] staves) {
        Track = track;
        Style = style;
        Regions = regions;
        Quantizer = quantizer;
        Staves = staves;
    }

    public Track Track { get; }
    public StaffStyle Style { get; }
    public List<MidiRegion> Regions { get; }
    public Quantizer Quantizer { get; }

    /// <summary>Each staff's units by start.</summary>
    public List<Unit>[] Staves { get; }

    /// <summary>The last display tick any unit reaches (−1 without notes).</summary>
    public long LastTick => Staves.SelectMany(static s => s).Select(static u => u.End - 1).DefaultIfEmpty(-1).Max();

    public static TrackNotation Build(ProjectState state, Track track, StaffStyle style, Bars bars) {
        var regions = state.MidiRegionsOf(track.Id).ToList();
        var quantizer = new Quantizer(bars);
        var notes = Sounding(regions);
        notes.Sort(static (x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) :
            x.Pitch != y.Pitch ? x.Pitch.CompareTo(y.Pitch) : x.Id.CompareTo(y.Id)
        );
        foreach (var note in notes) {
            note.Staff = StaffOf(style, note.Pitch);
            quantizer.Add(note.Start, DisplayGrid.GridsOf(note.Region.Notation, style));
        }

        foreach (var note in notes) {
            var grids = DisplayGrid.GridsOf(note.Region.Notation, style);
            note.DisplayStart = quantizer.Start(note.Start, grids);
            var end = quantizer.End(note.End, grids);
            note.DisplayEnd = Math.Max(end, quantizer.Step(note.DisplayStart, 1, grids));
        }

        var staves = new List<Unit>[style.Staves.Count];
        for (var s = 0; s < staves.Length; s++) {
            staves[s] = Staff(notes, s, style, bars, quantizer);
        }

        return new(track, style, regions, quantizer, staves);
    }

    /// <summary>The notes of the regions that sound, in project ticks, cut at their region's end.</summary>
    static List<SourceNote> Sounding(List<MidiRegion> regions) {
        var notes = new List<SourceNote>();
        foreach (var region in regions) {
            var regionEnd = region.StartTick + region.LengthTicks;
            foreach (var note in region.Notes) {
                var start = region.StartTick + note.StartTick;
                var end = Math.Min(start + note.LengthTicks, regionEnd);
                if (note.StartTick >= 0 && start < regionEnd && end > start) {
                    notes.Add(new(note, region, start, end));
                }
            }
        }

        return notes;
    }

    /// <summary>A staff's units: chords of its notes, in lanes, interpreted, numbered into voices, by start.</summary>
    static List<Unit> Staff(List<SourceNote> notes, int s, StaffStyle style, Bars bars, Quantizer quantizer) {
        var units = notes.Where(n => n.Staff == s)
            .GroupBy(static n => (n.DisplayStart, n.DisplayEnd))
            .Select(g => new Unit(
                    [..g.OrderBy(static n => n.Pitch).ThenBy(static n => n.Id)],
                    s,
                    g.Key.DisplayStart,
                    g.Key.DisplayEnd
                )
            )
            .OrderBy(static u => u.Start)
            .ThenByDescending(static u => u.Top)
            .ThenBy(static u => u.Notes[0].Id)
            .ToList();
        var lanes = Lanes(units, Math.Clamp(style.Staves[s].Voices, 1, 4));
        foreach (var lane in lanes) {
            Interpret(lane, bars, quantizer, style);
        }

        List<Unit> staff = [..units.OrderBy(static u => u.Start).ThenBy(static u => u.Lane)];
        NumberVoices(staff);
        return staff;
    }

    /// <summary>The first staff whose split point the pitch reaches (a null split point takes every pitch).</summary>
    public static int StaffOf(StaffStyle style, int pitch) {
        for (var i = 0; i < style.Staves.Count; i++) {
            if (style.Staves[i].SplitPitch is not { } split || pitch >= split) {
                return i;
            }
        }

        return style.Staves.Count - 1;
    }

    /// <summary>
    ///     Units into lanes (docs/notation/README.md, "Voices"), in start order, the top first at a start. A unit that
    ///     starts inside a held one and outlasts it is legato: with no overlap it cuts the held one and continues its
    ///     lane. Otherwise it takes a free lane (the nearest in pitch), else a new lane while the staff has voices, else
    ///     it cuts the most recent unit (joining it as a chord when they start together).
    /// </summary>
    static List<List<Unit>> Lanes(List<Unit> units, int voices) {
        var lanes = new List<List<Unit>>();
        foreach (var unit in units) {
            List<Unit>? chosen = null;
            if (unit.Notation.NoOverlap) {
                chosen = Nearest(
                    lanes.Where(l => l[^1].End > unit.Start && l[^1].Start < unit.Start && l[^1].End < unit.End),
                    unit
                );
                if (chosen is not null) {
                    chosen[^1].End = unit.Start;
                }
            }

            chosen ??= Nearest(lanes.Where(l => l[^1].End <= unit.Start), unit);
            if (chosen is null && lanes.Count < voices) {
                chosen = [];
                lanes.Add(chosen);
            }

            chosen ??= Displace(lanes, unit);
            if (chosen is null) {
                continue;
            }

            unit.Lane = lanes.IndexOf(chosen);
            chosen.Add(unit);
        }

        units.RemoveAll(u => !lanes[u.Lane].Contains(u));
        return lanes;

        static List<Unit>? Nearest(IEnumerable<List<Unit>> candidates, Unit unit) =>
            candidates.Select((l, i) => (Lane: l, Distance: Math.Abs(l[^1].Mean - unit.Mean), Index: i))
                .OrderBy(static c => c.Distance)
                .ThenBy(static c => c.Index)
                .Select(static c => c.Lane)
                .FirstOrDefault();
    }

    /// <summary>
    ///     With every voice taken: the lane of the most recent unit, cut where <paramref name="unit" /> starts, or null when
    ///     they start together and the unit joins it as a chord.
    /// </summary>
    static List<Unit>? Displace(List<List<Unit>> lanes, Unit unit) {
        var latest = lanes.OrderByDescending(static l => l[^1].Start).ThenBy(lanes.IndexOf).First();
        var held = latest[^1];
        if (held.Start != unit.Start) {
            held.End = unit.Start;
            return latest;
        }

        held.Notes.AddRange(unit.Notes);
        held.Notes.Sort(static (x, y) => x.Pitch != y.Pitch ? x.Pitch.CompareTo(y.Pitch) : x.Id.CompareTo(y.Id));
        held.End = Math.Max(held.End, unit.End);
        return null;
    }

    /// <summary>
    ///     Interpretation and no overlap within a lane (docs/notation/README.md, "Interpretation"): with interpretation an
    ///     end off the beat's halves goes to the nearer beat line (the later on a tie; never back to the start; in a
    ///     triplet span its bounds), and every end stops at the lane's next start; at least one grid unit long.
    /// </summary>
    static void Interpret(List<Unit> lane, Bars bars, Quantizer quantizer, StaffStyle style) {
        for (var i = 0; i < lane.Count; i++) {
            var unit = lane[i];
            var next = i + 1 < lane.Count ? lane[i + 1].Start : long.MaxValue;
            var grids = DisplayGrid.GridsOf(unit.Notation, style);
            var end = unit.End;
            if (unit.Notation.Interpretation) {
                end = Round(unit.Start, end, bars, quantizer);
            }

            end = Math.Min(end, next);
            var least = quantizer.Step(unit.Start, 1, grids);
            unit.End = end < least ? Math.Min(least, next) : end;
        }
    }

    static long Round(long start, long end, Bars bars, Quantizer quantizer) {
        var bar = bars.At(end);
        if (end == bar.Start) {
            return end;
        }

        long before, after;
        if (quantizer.TripletSpanAt(end) is { } span) {
            (before, after) = span;
            if (end == before) {
                return end;
            }
        } else {
            var beat = Meter.BeatAt(bar.Beats, end);
            if (end == beat.Start || beat.Children.Any(c => c.Start == end)) {
                return end;
            }

            (before, after) = (beat.Start, beat.End);
        }

        var rounded = end - before < after - end ? before : after;
        return quantizer.Normalize(rounded <= start ? after : rounded);
    }

    /// <summary>
    ///     Voices from lanes: within each passage of overlapping units (units chained by overlap), the lanes are numbered
    ///     from the highest mean pitch (voice 1, stems up) down; a passage with one lane is voice 1.
    /// </summary>
    static void NumberVoices(List<Unit> units) {
        var i = 0;
        while (i < units.Count) {
            var end = units[i].End;
            var j = i + 1;
            while (j < units.Count && units[j].Start < end) {
                end = Math.Max(end, units[j].End);
                j++;
            }

            var passage = units.GetRange(i, j - i);
            var order = passage.GroupBy(static u => u.Lane)
                .Select(static g => (Lane: g.Key, Mean: g.SelectMany(static u => u.Notes).Average(static n => n.Pitch)))
                .OrderByDescending(static l => l.Mean)
                .ThenBy(static l => l.Lane)
                .Select(static l => l.Lane)
                .ToList();
            foreach (var unit in passage) {
                unit.Voice = order.IndexOf(unit.Lane) + 1;
            }

            i = j;
        }
    }
}
