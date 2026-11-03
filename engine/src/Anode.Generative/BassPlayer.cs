using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     The Bass Player: each bar's tokens as chord-relative degrees on the chord sounding there (the root, or the slash
///     bass, low in the range; the other degrees above it), folded into the range; the root (or slash bass) on every
///     chord change's downbeat; approach notes and octave doubling by the grammar; slides and dead notes by the
///     switches. One note at a time.
/// </summary>
static class BassPlayer {
    const long ApproachKey = 0x41505052;
    const long SlideKey = 0x534C4944;
    const int DeadVelocity = 28;
    const int DeadLength = 50;
    const int GraceLength = 60;

    public static List<Hit> Play(
        Style style,
        SessionPlayerParameters parameters,
        Timeline timeline,
        List<BarPlan> plans,
        long seed
    ) {
        var switches = parameters.Bass ?? StyleCompiler.DefaultSwitches(SessionPlayerKind.Bass).Bass!;
        var range = new Range(switches.LowNote, switches.HighNote);
        var hits = new List<Hit>();
        foreach (var plan in plans) {
            Bar(style, switches, range, timeline, plan, hits, seed);
        }

        Downbeats(timeline, range, hits);
        var c = plans.Count > 0 ? plans[0].Complexity : 0;
        if (plans.Exists(static p => p.Grammar.Allows("approachNotes")) && c >= 0.45) {
            Approach(timeline, range, hits, seed);
        }

        if (switches.Slides && c >= 0.3) {
            Slides(timeline, range, hits, seed);
        }

        Monophonic(hits);
        return hits;
    }

    /// <summary>A bar's tokens as notes on the chords under them.</summary>
    static void Bar(
        Style style,
        BassSwitches switches,
        Range range,
        Timeline timeline,
        BarPlan plan,
        List<Hit> hits,
        long seed
    ) {
        var steps = plan.Steps(style);
        for (var step = 0; step < steps; step++) {
            var token = plan.Token(style, "bass", step);
            var tick = plan.Bar.Start + (long)step * style.StepTicks;
            if (token is "." or "-" || tick >= plan.Bar.End || token[0] == 'X' && !switches.DeadNotes) {
                continue;
            }

            var hold = 1;
            while (step + hold < steps && plan.Token(style, "bass", step + hold) == "-") {
                hold++;
            }

            var harmony = timeline.HarmonyAt(tick);
            var hit = new Hit {
                Tick = tick,
                Length = Math.Max(60, hold * style.StepTicks * 9 / 10),
                Pitch = Pitch(token, harmony, range, timeline, tick, seed),
                Velocity = Arranger.Velocity(Accent(token), plan.Intensity),
                Step = step % style.StepsPerBar,
                Event = Arranger.EventOf(tick, 0)
            };
            if (token[0] == 'X') {
                (hit.Velocity, hit.Length) = (DeadVelocity, DeadLength);
            } else if (token[0] == 'R'
                       && plan.Intensity >= 0.6
                       && step % (style.StepsPerBar / 4) != 0
                       && plan.Grammar.Allows("octaveDoubling")) {
                hit.Pitch = range.Fold(hit.Pitch + 12);
            }

            hits.Add(hit);
        }
    }

    /// <summary>A token's base velocity: 96, accented (<c>!</c>) 112, soft (<c>?</c>) 70.</summary>
    static double Accent(string token) =>
        token[^1] switch {
            '!' => 112,
            '?' => 70,
            _ => 96
        };

    /// <summary>
    ///     A degree token's pitch on <paramref name="harmony" />: <c>R</c> the root (the slash bass under a slash chord),
    ///     <c>r</c> the root itself, <c>3 5 7</c> the chord's third, fifth and seventh (the sixth, else the octave, when it
    ///     has none), <c>8</c> the octave, <c>2 4 6</c> the scale's steps above the root, <c>A</c> a half step under the
    ///     next chord's root; <c>'</c> up and <c>,</c> down an octave.
    /// </summary>
    static int Pitch(string token, Harmony harmony, Range range, Timeline timeline, long tick, long seed) {
        var root = range.Root(harmony.Root);
        var degree = token[0];
        var octaves = token.Count(static c => c == '\'') - token.Count(static c => c == ',');
        var intervals = harmony.Intervals;
        int pitch;
        switch (degree) {
            case 'R' or 'X':
                pitch = range.Root(harmony.Bass);
                break;
            case 'r':
                pitch = root;
                break;
            case '3':
                pitch = root + intervals[1];
                break;
            case '5':
                pitch = root + intervals[2];
                break;
            case '7':
                pitch = root + (intervals.Count >= 4 ? intervals[3] : 12);
                break;
            case '8':
                pitch = root + 12;
                break;
            case 'A':
                var next = timeline.Harmonies.FirstOrDefault(h => h.Start > tick && h.Start <= tick + Ticks.Bar)
                    ?? harmony;
                var target = range.Root(next.Bass);
                pitch = target + (Rng.Unit(seed, ApproachKey, tick) < 0.7 ? -1 : 1);
                break;
            default:
                pitch = ScaleStep(timeline, tick, root, degree - '0');
                break;
        }

        return range.Fold(pitch + 12 * octaves);
    }

    /// <summary>The scale tone nearest <paramref name="degree" /> (2, 4 or 6) above <paramref name="root" />.</summary>
    static int ScaleStep(Timeline timeline, long tick, int root, int degree) {
        var wanted = degree switch {
            2 => 2,
            4 => 5,
            _ => 9
        };
        foreach (var delta in (int[])[0, -1, 1]) {
            if (timeline.InScale(tick, root + wanted + delta)) {
                return root + wanted + delta;
            }
        }

        return root + wanted;
    }

    /// <summary>
    ///     Every chord change in the region starts with its root (or slash bass): the note starting there is moved to
    ///     it, a note held across the change is cut there and the root struck, and a rest at the change gets the root
    ///     until the next note (a beat at most).
    /// </summary>
    static void Downbeats(Timeline timeline, Range range, List<Hit> hits) {
        foreach (var harmony in timeline.Harmonies) {
            var change = harmony.Start;
            var pitch = range.Root(harmony.Bass);
            var at = hits.Find(h => h.Tick == change && h.Velocity > DeadVelocity);
            if (at is not null) {
                at.Pitch = pitch;
                continue;
            }

            hits.RemoveAll(h => h.Tick == change);
            var held = hits.Find(h => h.Tick < change && h.Tick + h.Length > change);
            var next = hits.Where(h => h.Tick > change).Select(static h => h.Tick).DefaultIfEmpty(timeline.End).Min();
            var length = held is not null ? held.Tick + held.Length - change : Math.Min(next - change, Ticks.Quarter);
            held?.Length = change - held.Tick;

            hits.Add(
                new() {
                    Tick = change,
                    Length = Math.Max(60, Math.Min(length, next - change)),
                    Pitch = pitch,
                    Velocity = Arranger.Velocity(104, 0.6),
                    Step = 0,
                    Event = Arranger.EventOf(change, 0)
                }
            );
        }
    }

    /// <summary>Approach notes: the last note in the eighth before a chord change leads by a half step into its root.</summary>
    static void Approach(Timeline timeline, Range range, List<Hit> hits, long seed) {
        var changes = timeline.Harmonies.Select(static h => h.Start).ToHashSet();
        foreach (var harmony in timeline.Harmonies.Skip(1)) {
            var change = harmony.Start;
            var before = hits
                .Where(h => h.Tick < change && h.Tick >= change - Ticks.Quarter / 2 && !changes.Contains(h.Tick))
                .OrderBy(static h => h.Tick)
                .LastOrDefault();
            if (before is null) {
                continue;
            }

            var target = range.Root(harmony.Bass);
            before.Pitch = range.Fold(target + (Rng.Unit(seed, ApproachKey, change) < 0.7 ? -1 : 1));
        }
    }

    /// <summary>Slides: now and then a grace note a whole step under a chord change's root slides into it.</summary>
    static void Slides(Timeline timeline, Range range, List<Hit> hits, long seed) {
        foreach (var harmony in timeline.Harmonies.Skip(1)) {
            var change = harmony.Start;
            var target = hits.Find(h => h.Tick == change);
            if (target is null || Rng.Unit(seed, SlideKey, change) >= 0.5) {
                continue;
            }

            var grace = change - GraceLength;
            foreach (var h in hits.Where(h => h.Tick < change && h.Tick + h.Length > grace)) {
                h.Length = Math.Max(30, grace - h.Tick);
            }

            hits.RemoveAll(h => h.Tick >= grace && h.Tick < change);
            hits.Add(
                new() {
                    Tick = grace,
                    Length = GraceLength,
                    Pitch = range.Fold(target.Pitch - 2),
                    Velocity = Math.Max(1, target.Velocity - 20),
                    Event = target.Event,
                    Grace = true,
                    Passing = true
                }
            );
        }
    }

    /// <summary>One note at a time: each note ends before the next starts.</summary>
    static void Monophonic(List<Hit> hits) {
        hits.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        for (var k = 0; k + 1 < hits.Count; k++) {
            var gap = hits[k + 1].Tick - hits[k].Tick;
            if (hits[k].Length > gap - 10) {
                hits[k].Length = Math.Max(1, Math.Min(hits[k].Length, gap - 10));
            }
        }
    }

    /// <summary>The bass range: roots in its lowest octave, every pitch folded into it.</summary>
    sealed record Range(int Low, int High) {
        public int Fold(int pitch) {
            while (pitch > High) {
                pitch -= 12;
            }

            while (pitch < Low) {
                pitch += 12;
            }

            return pitch;
        }

        /// <summary>Pitch class <paramref name="pc" /> in the range's lowest octave.</summary>
        public int Root(int pc) => Low + Harmony.Mod12(pc - Low);
    }
}
