using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     A note as a player makes it, before swing, feel and humanisation: its grid tick (project ticks), length, pitch,
///     velocity, its step in its bar (for swing; −1 none), the event it belongs to (notes of one event move together),
///     and whether it is a grace note (it stays tied to the note it leads into) or a passing note.
/// </summary>
sealed class Hit {
    public long Tick;
    public long Length;
    public int Pitch;
    public int Velocity;
    public int Step = -1;
    public long Event;
    public bool Grace;
    public bool Passing;
}

/// <summary>
///     What a bar plays: its phrase (and which of the phrase's bars), its role, its groove (a fill plays over it until
///     its <c>From</c> step) and the section's other one, its form letter, the pad's point there and the section's
///     grammar.
/// </summary>
sealed record BarPlan(
    Bar Bar,
    Phrase Phrase,
    int PhraseBar,
    PhraseRole Role,
    Phrase Groove,
    int GrooveBar,
    Phrase Alternate,
    char Letter,
    double Complexity,
    double Intensity,
    Grammar Grammar
) {
    /// <summary>Steps of the style's grid in this bar (a longer bar repeats the phrase's steps).</summary>
    public int Steps(Style style) => (int)((Bar.Length + style.StepTicks - 1) / style.StepTicks);

    /// <summary>Phrase bar <paramref name="bar" />'s token of <paramref name="lane" /> at <paramref name="step" />.</summary>
    public static string Token(Style style, Lane lane, int bar, int step) =>
        lane.Tokens[bar * style.StepsPerBar + step % style.StepsPerBar];

    /// <summary>
    ///     The bar's token of lane <paramref name="name" /> at <paramref name="step" />: a fill's from its <c>From</c>
    ///     step, the groove's before it; a rest where the phrase has no such lane.
    /// </summary>
    public string Token(Style style, string name, int step) {
        var (phrase, bar) = Role == PhraseRole.Fill && step % style.StepsPerBar < Phrase.From
            ? (Groove, GrooveBar)
            : (Phrase, PhraseBar);
        return phrase.Find(name) is { } lane ? Token(style, lane, bar, step) : ".";
    }
}

/// <summary>
///     The parts every player shares: the bar plan (phrase selection by the pad, the grammar's form and roles, fills at
///     section ends and by the fill amount) and the performance (swing, feel, seeded humanisation, the region's bounds).
/// </summary>
static class Arranger {
    // Draw keys: each use of the seed has its own, so draws never collide.
    const long PickKey = 0x5049434B;
    const long FillKey = 0x46494C4C;
    const long TimeKey = 0x54494D45;
    const long VelocityKey = 0x56454C4F;

    public static List<BarPlan> Plan(Style style, SessionPlayerParameters parameters, Timeline timeline, long seed) {
        var plans = new List<BarPlan>();
        var grooves = style.Phrases.Where(static p => p.Role == PhraseRole.Groove).ToList();
        var chosen = new Dictionary<Segment, (Phrase A, Phrase B)>();
        foreach (var bar in timeline.Bars) {
            var segment = bar.Segment;
            var grammar = style.Grammar[segment.Kind];
            var c = Math.Clamp(parameters.Pad.Complexity + grammar.Complexity, 0, 1);
            var i = Math.Clamp(parameters.Pad.Intensity + grammar.Intensity, 0, 1);
            if (!chosen.TryGetValue(segment, out var pair)) {
                var a = Nearest(grooves, c, i, seed, segment.SectionStart, null);
                var near = grooves.Where(g =>
                        g != a
                        && Math.Abs(g.Complexity - a.Complexity) <= 0.3
                        && Math.Abs(g.Intensity - a.Intensity) <= 0.3
                    )
                    .ToList();
                pair = (a, near.Count == 0 ? a : Nearest(near, c, i, seed, segment.SectionStart + 1, null));
                chosen[segment] = pair;
            }

            var letter = grammar.Form[bar.InSection % grammar.Form.Length];
            var groove = letter == 'B' ? pair.B : pair.A;
            var grooveBar = bar.InSection % groove.Bars;
            var other = letter == 'B' ? pair.A : pair.B;
            var plan = new BarPlan(
                bar,
                groove,
                grooveBar,
                PhraseRole.Groove,
                groove,
                grooveBar,
                other,
                letter,
                c,
                i,
                grammar
            );
            var role = RoleOf(bar, grammar, parameters, seed);
            if (role is { } r && Pick(style, r, c, i, parameters.FillAmount, bar, seed) is { } phrase) {
                plan = plan with { Phrase = phrase, PhraseBar = 0, Role = r };
            }

            plans.Add(plan);
        }

        return plans;
    }

    /// <summary>The role a bar takes instead of its groove: the grammar's first and last, fills at ends and by amount.</summary>
    static PhraseRole? RoleOf(Bar bar, Grammar grammar, SessionPlayerParameters parameters, long seed) {
        if (bar.InSection == 0 && grammar.First is { } first && bar.Start >= bar.Segment.SectionStart) {
            return first;
        }

        if (bar.LastOfSection) {
            return grammar.Last ?? (parameters.FillAtSectionEnds ? PhraseRole.Fill : null);
        }

        // Fills by amount: now and then at the end of a four-bar phrase inside a section.
        if ((uint)bar.InSection % 4 == 3
            && parameters.FillAmount > 0
            && Rng.Unit(seed, FillKey, bar.Start) < parameters.FillAmount * 0.5) {
            return PhraseRole.Fill;
        }

        return null;
    }

    /// <summary>A phrase of <paramref name="role" /> for a bar, or null when the style has none (the groove plays).</summary>
    static Phrase? Pick(Style style, PhraseRole role, double c, double i, double fillAmount, Bar bar, long seed) {
        var candidates = style.Phrases.Where(p => p.Role == role).ToList();
        if (candidates.Count == 0) {
            return role == PhraseRole.Pickup || role == PhraseRole.Ending
                ? Pick(style, PhraseRole.Fill, c, i, fillAmount, bar, seed)
                : null;
        }

        if (role != PhraseRole.Fill) {
            return Nearest(candidates, c, i, seed, bar.Start, null);
        }

        // A fill's length follows the fill amount: a beat at 0, the whole bar at 1.
        var wanted = 0.25 + 0.75 * fillAmount;
        return Nearest(
            candidates,
            c,
            i,
            seed,
            bar.Start,
            p => Math.Abs((style.StepsPerBar - p.From) / (double)style.StepsPerBar - wanted)
        );
    }

    /// <summary>
    ///     The phrase nearest the pad's point (complexity counts twice intensity), plus <paramref name="extra" />; one
    ///     within 0.05 of the nearest is as likely (the seed picks).
    /// </summary>
    static Phrase Nearest(List<Phrase> phrases, double c, double i, long seed, long at, Func<Phrase, double>? extra) {
        var ranked = phrases
            .Select(p => (Phrase: p,
                    Distance: Math.Abs(p.Complexity - c) + 0.5 * Math.Abs(p.Intensity - i) + (extra?.Invoke(p) ?? 0))
            )
            .OrderBy(static x => x.Distance)
            .ThenBy(static x => x.Phrase.Id, StringComparer.Ordinal)
            .ToList();
        var close = ranked.TakeWhile(x => x.Distance <= ranked[0].Distance + 0.05).ToList();
        return close[(int)(Rng.Bits(seed, PickKey, at, phrases.Count) % (ulong)close.Count)].Phrase;
    }

    /// <summary>A velocity from a base and the intensity: 0.6× at 0 to 1.1× at 1.</summary>
    public static int Velocity(double baseVelocity, double intensity) =>
        Math.Clamp((int)Math.Round(baseVelocity * (0.6 + 0.5 * intensity)), 1, 127);

    /// <summary>
    ///     The notes of <paramref name="hits" />, performed: swing on the style's grid, feel (push or lay back by up to a
    ///     32nd), seeded timing and velocity per event, kept inside the region, relative to its start, sorted and
    ///     numbered (one note per pitch and tick, the loudest).
    /// </summary>
    public static List<Note> Perform(
        List<Hit> hits,
        Style style,
        SessionPlayerParameters parameters,
        Timeline timeline,
        long seed
    ) {
        var played = new List<(long Start, long Length, int Pitch, int Velocity)>();
        foreach (var hit in hits) {
            var time = Time(hit, style, parameters, seed);
            var tick = Math.Max(time, timeline.Start);
            var end = Math.Min(time + Math.Max(1, hit.Length), timeline.End);
            var velocity = Math.Clamp(
                hit.Velocity + Rng.Spread(seed, VelocityKey, hit.Event, hit.Pitch, style.HumaniseVelocity),
                1,
                127
            );
            if (tick < timeline.End && end > tick && hit.Pitch is >= 0 and <= 127) {
                played.Add((tick - timeline.Start, end - tick, hit.Pitch, velocity));
            }
        }

        var kept = played
            .GroupBy(static p => (p.Start, p.Pitch))
            .Select(static g =>
                g.OrderByDescending(static p => p.Velocity).ThenByDescending(static p => p.Length).First()
            )
            .OrderBy(static p => p.Start)
            .ThenBy(static p => p.Pitch)
            .ToList();
        if (style.Player == SessionPlayerKind.Bass) {
            kept = Monophonic(kept);
        }

        var notes = new List<Note>(kept.Count);
        foreach (var (start, length, pitch, velocity) in kept) {
            notes.Add(new(notes.Count + 1, pitch, velocity, 1, start, length, 0));
        }

        return notes;
    }

    /// <summary>
    ///     Where a hit is played: its grid tick, swung (an offbeat of the style's grid, by up to a third of the grid
    ///     step), moved by the feel and, unless a grace note, by its event's humanisation.
    /// </summary>
    static long Time(Hit hit, Style style, SessionPlayerParameters parameters, long seed) {
        var tick = hit.Tick - (long)Math.Round(parameters.Feel * 120);
        var step = (uint)hit.Step;
        var offbeat = style.SwingGrid == 240 ? step % 2 == 1 : step % 4 == 2;
        if (hit.Step >= 0 && style.StepsPerBar == 16 && offbeat) {
            tick += (long)Math.Round(style.SwingGrid / 3.0 * parameters.Swing);
        }

        return hit.Grace ? tick : tick + Rng.Spread(seed, TimeKey, hit.Event, 0, style.HumaniseTicks);
    }

    /// <summary>One note at a time, as played: the loudest of a tick, each ending where the next starts.</summary>
    static List<(long Start, long Length, int Pitch, int Velocity)> Monophonic(
        List<(long Start, long Length, int Pitch, int Velocity)> notes
    ) {
        List<(long Start, long Length, int Pitch, int Velocity)> kept = [
            ..notes.GroupBy(static p => p.Start)
                .Select(static g => g.OrderByDescending(static p => p.Velocity).ThenBy(static p => p.Pitch).First())
        ];
        for (var k = 0; k + 1 < kept.Count; k++) {
            kept[k] = kept[k] with { Length = Math.Min(kept[k].Length, kept[k + 1].Start - kept[k].Start) };
        }

        return kept;
    }

    /// <summary>An event key for <paramref name="tick" /> and <paramref name="lane" /> (notes of one event share it).</summary>
    public static long EventOf(long tick, int lane) => tick * 256 + lane;
}
