using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     The Drummer: each bar's phrase lanes as kit notes (ghost notes kept by complexity), a fill over its groove's
///     first steps, the grammar's substitutions (crash on one, open hat, ride for hat, ghost notes, half time), the
///     kit pieces' switches and variations, and the kick following the rhythm source.
/// </summary>
static class DrumPlayer {
    const long GhostKey = 0x47484F53;
    const int DrumLength = 120;

    /// <summary>How far from an onset a fill's kick stays (ticks): inside a sixteenth, with room for the humanising.</summary>
    const long FillKickReach = 180;

    public static List<Hit> Play(
        Style style,
        SessionPlayerParameters parameters,
        List<BarPlan> plans,
        IReadOnlyList<long>? onsets,
        long seed
    ) {
        var switches = parameters.Drums ?? StyleCompiler.DefaultSwitches(SessionPlayerKind.Drummer).Drums!;
        var hits = new List<Hit>();
        var crashNext = false;
        foreach (var plan in plans) {
            var steps = plan.Steps(style);
            var bar = Bar(style, plan, steps, seed);
            if (plan.Role == PhraseRole.Groove) {
                Substitute(style, plan, steps, bar, seed);
                Vary(style, switches, steps, bar);
                if (onsets is not null) {
                    Follow(style, plan, steps, bar, onsets);
                }
            } else if (plan.Role == PhraseRole.Fill) {
                Fill(style, plan, steps, bar);
                if (onsets is not null) {
                    KeepKicksOnOnsets(style, plan, bar, onsets);
                }
            }

            // A crash on the one after a fill, and on a section's first beat where the grammar has it.
            if ((crashNext || plan.Bar.InSection == 0 && plan.Grammar.Allows("crashOnOne"))
                && plan.Role != PhraseRole.Fill) {
                bar.RemoveAll(static h => h.Step == 0 && h.Note is DrumKitNotes.ClosedHiHat or DrumKitNotes.Ride);
                bar.Add((0, DrumKitNotes.Crash, 'X'));
            }

            crashNext = plan.Role == PhraseRole.Fill;
            Switch(style, switches, bar);
            Emit(hits, style, plan, bar);
        }

        return hits;
    }

    /// <summary>A bar's hits as notes at their grid ticks (steps past a short bar's end dropped).</summary>
    static void Emit(List<Hit> hits, Style style, BarPlan plan, List<(int Step, int Note, char Mark)> bar) {
        foreach (var (step, note, mark) in bar) {
            var tick = plan.Bar.Start + (long)step * style.StepTicks;
            if (tick < plan.Bar.End) {
                hits.Add(
                    new() {
                        Tick = tick,
                        Length = DrumLength,
                        Pitch = note,
                        Velocity = Arranger.Velocity(Base(mark), plan.Intensity),
                        Step = step % style.StepsPerBar,
                        Event = Arranger.EventOf(tick, note)
                    }
                );
            }
        }
    }

    static double Base(char mark) =>
        mark switch {
            'X' => 118,
            'x' => 96,
            'o' => 74,
            _ => 42
        };

    /// <summary>A phrase bar's hits (step, note, mark), ghost notes kept with a probability rising with complexity.</summary>
    static List<(int Step, int Note, char Mark)> Hits(
        Style style,
        Phrase phrase,
        int phraseBar,
        int from,
        int steps,
        double complexity,
        long barStart,
        long seed
    ) {
        var hits = new List<(int, int, char)>();
        var keep = Math.Clamp((complexity - 0.2) / 0.6, 0, 1);
        foreach (var lane in phrase.Lanes) {
            for (var step = from; step < steps; step++) {
                var token = BarPlan.Token(style, lane, phraseBar, step);
                if (token is "." or "-") {
                    continue;
                }

                var mark = token[0];
                if (mark == 'g' && Rng.Unit(seed, GhostKey, barStart + step, lane.Note) >= keep) {
                    continue;
                }

                hits.Add((step, lane.Note, mark));
            }
        }

        return hits;
    }

    /// <summary>
    ///     A bar's hits: its phrase's; a fill's from its <c>From</c> step over the groove, whose hats, cymbals and
    ///     percussion keep time under it.
    /// </summary>
    static List<(int Step, int Note, char Mark)> Bar(Style style, BarPlan plan, int steps, long seed) {
        if (plan.Role != PhraseRole.Fill) {
            return Hits(style, plan.Phrase, plan.PhraseBar, 0, steps, plan.Complexity, plan.Bar.Start, seed);
        }

        var from = Math.Min(steps, plan.Phrase.From);
        var groove = Hits(style, plan.Groove, plan.GrooveBar, 0, steps, plan.Complexity, plan.Bar.Start, seed);
        groove.RemoveAll(h => h.Step >= from && !KeepsTime(h.Note));
        return [..groove, ..Hits(style, plan.Phrase, 0, from, steps, plan.Complexity, plan.Bar.Start, seed)];
    }

    /// <summary>Whether a kit note keeps time under a fill: the hi-hats, the ride and the percussion.</summary>
    static bool KeepsTime(int note) =>
        DrumKitNotes.Of(DrumPiece.HiHat).Contains(note)
        || note is DrumKitNotes.Ride or DrumKitNotes.RideBell
        || note != DrumKitNotes.Clap && DrumKitNotes.Of(DrumPiece.Percussion).Contains(note);

    /// <summary>
    ///     A fill has more hits than any groove bar of its section could (every hit of either groove's lanes, ghost
    ///     notes too, and the substitutions' few): when the phrase alone does not, a snare roll grows back from the
    ///     bar's end.
    /// </summary>
    static void Fill(Style style, BarPlan plan, int steps, List<(int Step, int Note, char Mark)> bar) {
        var most = Math.Max(MostHits(style, plan.Groove, steps), MostHits(style, plan.Alternate, steps)) + 3;
        // The snare first, then the kick in unison under it, then the high tom, over the bar's second half.
        var first = Math.Min(plan.Phrase.From, steps / 2);
        foreach (var note in (ReadOnlySpan<int>)[DrumKitNotes.Snare, DrumKitNotes.Kick, DrumKitNotes.HighTom]) {
            for (var step = steps - 1; bar.Count <= most && step >= first; step--) {
                if (!bar.Exists(h => h.Step == step && h.Note == note)) {
                    bar.Add((step, note, step == steps - 1 ? 'X' : 'o'));
                }
            }
        }
    }

    /// <summary>The most hits a bar of <paramref name="groove" /> has (every lane's, ghost notes too).</summary>
    static int MostHits(Style style, Phrase groove, int steps) {
        var most = 0;
        for (var b = 0; b < groove.Bars; b++) {
            var count = 0;
            foreach (var lane in groove.Lanes) {
                for (var step = 0; step < steps; step++) {
                    count += BarPlan.Token(style, lane, b, step) is "." or "-" ? 0 : 1;
                }
            }

            most = Math.Max(most, count);
        }

        return most;
    }

    /// <summary>The grammar's substitutions on a groove bar.</summary>
    static void Substitute(Style style, BarPlan plan, int steps, List<(int Step, int Note, char Mark)> bar, long seed) {
        var beat = style.StepsPerBar / 4;
        var grammar = plan.Grammar;
        if (grammar.Allows("halfTime")) {
            HalfTime(bar, beat);
        }

        if (grammar.Allows("ghostNotes") && plan.Complexity >= 0.6) {
            GhostNotes(plan, steps, bar, beat, seed);
        }

        if (grammar.Allows("openHat") && plan.Complexity >= 0.4 && (plan.Letter == 'B' || plan.Intensity >= 0.7)) {
            OpenHat(bar, beat);
        }

        if (grammar.Allows("rideForHat") && plan.Intensity >= 0.6) {
            for (var k = 0; k < bar.Count; k++) {
                if (bar[k].Note is DrumKitNotes.ClosedHiHat or DrumKitNotes.OpenHiHat) {
                    bar[k] = (bar[k].Step, DrumKitNotes.Ride, bar[k].Mark);
                }
            }
        }
    }

    /// <summary>Half time: the backbeats (beats 2 and 4) move to beat 3.</summary>
    static void HalfTime(List<(int Step, int Note, char Mark)> bar, int beat) {
        var backbeats = bar.RemoveAll(h =>
            h.Note == DrumKitNotes.Snare && h.Mark is 'X' or 'x' && h.Step % (2 * beat) == beat
        );
        if (backbeats > 0) {
            bar.Add((2 * beat, DrumKitNotes.Snare, 'X'));
        }
    }

    /// <summary>Ghost notes: the step before each backbeat, now and then.</summary>
    static void GhostNotes(BarPlan plan, int steps, List<(int Step, int Note, char Mark)> bar, int beat, long seed) {
        foreach (var step in (int[])[beat - 1, 3 * beat - 1]) {
            if (step < steps
                && !bar.Exists(h => h.Step == step && h.Note == DrumKitNotes.Snare)
                && Rng.Unit(seed, GhostKey + 1, plan.Bar.Start, step) < 0.6) {
                bar.Add((step, DrumKitNotes.Snare, 'g'));
            }
        }
    }

    /// <summary>An open hat: the bar's last offbeat closed hat opens.</summary>
    static void OpenHat(List<(int Step, int Note, char Mark)> bar, int beat) {
        var last = bar.Where(h => h.Note == DrumKitNotes.ClosedHiHat && h.Step % beat != 0)
            .OrderBy(static h => h.Step)
            .LastOrDefault();
        if (last.Note != 0) {
            bar.Remove(last);
            bar.Add((last.Step, DrumKitNotes.OpenHiHat, last.Mark));
        }
    }

    /// <summary>A piece's pattern variation (1…3, when the style has it) in place of the phrase's lane of its main note.</summary>
    static void Vary(Style style, DrummerSwitches switches, int steps, List<(int Step, int Note, char Mark)> bar) {
        foreach (var setting in switches.Pieces) {
            if (setting.Variation == 0
                || !style.Variations.TryGetValue(setting.Piece, out var lanes)
                || setting.Variation > lanes.Length) {
                continue;
            }

            var notes = DrumKitNotes.Of(setting.Piece);
            var main = notes[0];
            bar.RemoveAll(h => h.Note == main || setting.Piece == DrumPiece.HiHat && notes.Contains(h.Note));
            var lane = lanes[setting.Variation - 1];
            for (var step = 0; step < steps; step++) {
                var token = lane[step % style.StepsPerBar];
                if (token is not ("." or "-")) {
                    bar.Add((step, main, token[0]));
                }
            }
        }
    }

    /// <summary>
    ///     The kick follows the rhythm source: in a bar where it has onsets, the kicks are its onsets on the grid (the
    ///     strongest positions first, as many as the complexity allows), away from the backbeats.
    /// </summary>
    static void Follow(
        Style style,
        BarPlan plan,
        int steps,
        List<(int Step, int Note, char Mark)> bar,
        IReadOnlyList<long> onsets
    ) {
        var bar0 = plan.Bar.Start;
        var found = new SortedSet<int>();
        foreach (var onset in onsets) {
            if (onset < bar0 || onset >= plan.Bar.End) {
                continue;
            }

            var step = (int)((onset - bar0 + style.StepTicks / 2) / style.StepTicks);
            if (step < steps) {
                found.Add(step);
            }
        }

        if (found.Count == 0) {
            return;
        }

        var beat = style.StepsPerBar / 4;
        var snares = bar.Where(static h => h.Note == DrumKitNotes.Snare && h.Mark is 'X' or 'x')
            .Select(static h => h.Step)
            .ToHashSet();
        var cap = 2 + (int)Math.Round(plan.Complexity * 6);
        var kicks = found
            .Where(s => !snares.Contains(s))
            .OrderBy(s => Strength(s, beat))
            .ThenBy(static s => s)
            .Take(cap)
            .ToList();
        if (kicks.Count == 0) {
            return;
        }

        bar.RemoveAll(static h => h.Note == DrumKitNotes.Kick);
        foreach (var step in kicks) {
            bar.Add((step, DrumKitNotes.Kick, step % beat == 0 ? 'X' : 'x'));
        }
    }

    /// <summary>
    ///     A fill while following a rhythm source: its kicks stay only near the source's onsets, so the kick follows
    ///     through the fill bars too (a bar without onsets keeps its fill).
    /// </summary>
    static void KeepKicksOnOnsets(
        Style style,
        BarPlan plan,
        List<(int Step, int Note, char Mark)> bar,
        IReadOnlyList<long> onsets
    ) {
        var inBar = onsets.Where(o => o >= plan.Bar.Start && o < plan.Bar.End).ToArray();
        if (inBar.Length > 0) {
            bar.RemoveAll(h => h.Note == DrumKitNotes.Kick
                && !inBar.Any(o => Math.Abs(plan.Bar.Start + h.Step * style.StepTicks - o) <= FillKickReach));
        }
    }

    /// <summary>How strong a step's position is: 0 the half bars, 1 the beats, 2 the eighths, 3 the rest.</summary>
    static int Strength(int step, int beat) =>
        step % (2 * beat) == 0 ? 0
        : step % beat == 0 ? 1
        : step % Math.Max(1, beat / 2) == 0 ? 2
        : 3;

    /// <summary>
    ///     Pieces switched off go silent; with the hi-hat off, the ride (or its bell) keeps time, when the cymbals are
    ///     on.
    /// </summary>
    static void Switch(Style style, DrummerSwitches switches, List<(int Step, int Note, char Mark)> bar) {
        bool On(DrumPiece piece) => switches.Pieces.FirstOrDefault(p => p.Piece == piece)?.On ?? true;

        if (!On(DrumPiece.HiHat)) {
            // The hats to the time-keeper, or to nothing (−1, removed below) with the cymbals off too.
            var keeper = !On(DrumPiece.Cymbals) ? -1
                : style.RideKeepsTime ? DrumKitNotes.Ride
                : DrumKitNotes.RideBell;
            var hats = DrumKitNotes.Of(DrumPiece.HiHat);
            for (var k = 0; k < bar.Count; k++) {
                bar[k] = hats.Contains(bar[k].Note) ? (bar[k].Step, keeper, bar[k].Mark) : bar[k];
            }
        }

        foreach (var piece in Enum.GetValues<DrumPiece>().Where(p => p != DrumPiece.HiHat && !On(p))) {
            var notes = DrumKitNotes.Of(piece);
            bar.RemoveAll(h => notes.Contains(h.Note));
        }

        bar.RemoveAll(static h => h.Note < 0);
    }
}
