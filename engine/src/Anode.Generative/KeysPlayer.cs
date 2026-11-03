using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     The Keyboard Player (and the Synth Player): every chord voiced once, voice-led from the one before
///     (<see cref="Voicer" />); each bar's right-hand tokens strike the voicing or its voices (the rhythm switch's
///     pattern in place of the phrase's), struck again at a chord change they hold across; the left hand below the
///     range by its switch; passing notes into a chord change and octave doubling by the grammar. Every note but a
///     passing note is a tone of the chord sounding where it starts (or its slash bass).
/// </summary>
static class KeysPlayer {
    const long PassingKey = 0x50415353;
    const int LowestLeftHand = 24;

    /// <summary>At most this share of the notes are passing notes.</summary>
    const double MaxPassing = 0.08;

    public static List<Hit> Play(
        Style style,
        SessionPlayerParameters parameters,
        Timeline timeline,
        List<BarPlan> plans,
        long seed
    ) {
        var switches = parameters.Keys ?? StyleCompiler.DefaultSwitches(SessionPlayerKind.Keyboard).Keys!;
        var voicings = new Dictionary<Harmony, int[]>();
        int[]? previous = null;
        foreach (var harmony in timeline.Harmonies) {
            previous = voicings[harmony] =
                Voicer.Voice(harmony, switches.Voicing, switches.LowNote, switches.HighNote, previous);
        }

        var rhythm = switches.RhythmId is { } id ? style.Rhythms.GetValueOrDefault(id) : null;
        var hits = new List<Hit>();
        foreach (var plan in plans) {
            var steps = plan.Steps(style);
            var rh = Tokens(style, plan, "rh", rhythm?.Rh);
            var lh = plan.Phrase.Find("lh") is null && plan.Groove.Find("lh") is null && rhythm?.Lh is null
                ? [..Enumerable.Range(0, style.StepsPerBar).Select(static s => s == 0 ? "R" : "-")]
                : Tokens(style, plan, "lh", rhythm?.Lh);
            Lane(
                style,
                plan,
                steps,
                rh,
                timeline,
                (tick, length, token, harmony, restruck) =>
                    Right(hits, tick, length, token, voicings[harmony], plan, restruck, switches.HighNote)
            );
            if (switches.LeftHand != KeysLeftHand.Off) {
                Lane(
                    style,
                    plan,
                    steps,
                    lh,
                    timeline,
                    (tick, length, token, harmony, restruck) =>
                        Left(hits, tick, length, token, harmony, switches, plan, restruck)
                );
            }
        }

        if (plans.Count > 0
            && plans.Exists(static p => p.Grammar.Allows("passingNotes"))
            && plans[0].Complexity >= 0.6) {
            Passing(timeline, voicings, hits, plans[0].Intensity, seed);
        }

        return hits;
    }

    /// <summary>
    ///     A bar's tokens of a lane: the rhythm switch's pattern (when set) in place of the groove's, a fill's own from
    ///     its <c>From</c> step.
    /// </summary>
    static string[] Tokens(Style style, BarPlan plan, string lane, string[]? rhythm) {
        var tokens = new string[style.StepsPerBar];
        for (var step = 0; step < tokens.Length; step++) {
            var fill = plan.Role != PhraseRole.Groove && (plan.Role != PhraseRole.Fill || step >= plan.Phrase.From);
            tokens[step] = rhythm is not null && !fill ? rhythm[step] : plan.Token(style, lane, step);
        }

        return tokens;
    }

    /// <summary>
    ///     Each token of a bar's lane as an event (its grid tick, held length, token and chord), split where a chord
    ///     changes under it: the part after the change is struck again on the new chord.
    /// </summary>
    static void Lane(
        Style style,
        BarPlan plan,
        int steps,
        string[] tokens,
        Timeline timeline,
        Action<long, long, string, Harmony, bool> play
    ) {
        for (var step = 0; step < steps; step++) {
            var token = tokens[step % tokens.Length];
            if (token is "." or "-") {
                continue;
            }

            var hold = 1;
            while (step + hold < steps && tokens[(step + hold) % tokens.Length] == "-") {
                hold++;
            }

            var tick = plan.Bar.Start + (long)step * style.StepTicks;
            var end = Math.Min(tick + (long)hold * style.StepTicks, plan.Bar.End);
            var restruck = false;
            while (tick < end) {
                var harmony = timeline.HarmonyAt(tick);
                var next = Math.Min(end, harmony.End);
                play(tick, next - tick, token, harmony, restruck);
                (tick, restruck) = (next, true);
            }
        }
    }

    static double Base(string token, double strong, double soft) =>
        (token[0] == 'c' ? soft : strong)
        * token[^1] switch {
            '!' => 1.2,
            '?' => 0.75,
            _ => 1
        };

    /// <summary>
    ///     The right hand: <c>C</c> (<c>c</c> softer) the whole voicing, <c>T</c> its top, <c>U</c> all but its bottom,
    ///     <c>1</c>…<c>4</c> one voice from the bottom (arpeggios).
    /// </summary>
    static void Right(
        List<Hit> hits,
        long tick,
        long length,
        string token,
        int[] voicing,
        BarPlan plan,
        bool restruck,
        int high
    ) {
        IEnumerable<int> voices = token[0] switch {
            'C' or 'c' => voicing,
            'T' => [voicing[^1]],
            'U' => voicing[1..],
            _ => [voicing[(token[0] - '1') % voicing.Length]]
        };
        var velocity = Arranger.Velocity(Base(token, 92, 68) * (restruck ? 0.8 : 1), plan.Intensity);
        var doubled = token[0] == 'C'
            && plan.Grammar.Allows("octaveDoubling")
            && plan.Intensity >= 0.7
            && voicing[^1] + 12 <= high;
        foreach (var pitch in doubled ? [..voices, voicing[^1] + 12] : voices) {
            hits.Add(
                new() {
                    Tick = tick,
                    Length = Math.Max(60, length - 20),
                    Pitch = pitch,
                    Velocity = velocity,
                    Step = restruck ? -1 : (int)((tick - plan.Bar.Start) / (Ticks.Bar / 16)),
                    Event = Arranger.EventOf(tick, 1)
                }
            );
        }
    }

    /// <summary>
    ///     The left hand below the range: <c>R</c> the bass (the root, or the slash bass), <c>5</c> the fifth, <c>8</c>
    ///     the root's octave, by the switch (root: <c>R</c> only; octaves: the bass and its octave; root and fifth: as
    ///     written).
    /// </summary>
    static void Left(
        List<Hit> hits,
        long tick,
        long length,
        string token,
        Harmony harmony,
        KeysSwitches switches,
        BarPlan plan,
        bool restruck
    ) {
        var top = switches.LowNote - 1;

        int Below(int pc) {
            var pitch = top - Harmony.Mod12(top - pc);
            return pitch < LowestLeftHand ? pitch + 12 : pitch;
        }

        var bass = Below(harmony.Bass);
        int[] pitches = switches.LeftHand switch {
            KeysLeftHand.Root => [bass],
            KeysLeftHand.Octaves => bass - 12 >= LowestLeftHand ? [bass - 12, bass] : [bass],
            _ => token[0] switch {
                '5' => [Below(harmony.Root + harmony.Intervals[2])],
                '8' => [Below(harmony.Root)],
                _ => [bass]
            }
        };
        var velocity = Arranger.Velocity(Base(token, 88, 88) * (restruck ? 0.8 : 1), plan.Intensity);
        foreach (var pitch in pitches) {
            hits.Add(
                new() {
                    Tick = tick,
                    Length = Math.Max(60, length - 20),
                    Pitch = pitch,
                    Velocity = velocity,
                    Step = restruck ? -1 : (int)((tick - plan.Bar.Start) / (Ticks.Bar / 16)),
                    Event = Arranger.EventOf(tick, 2)
                }
            );
        }
    }

    /// <summary>
    ///     Passing notes: now and then, a 16th before a chord change, a scale tone between the two voicings' tops leads
    ///     into the new chord; at most <see cref="MaxPassing" /> of the notes.
    /// </summary>
    static void Passing(
        Timeline timeline,
        Dictionary<Harmony, int[]> voicings,
        List<Hit> hits,
        double intensity,
        long seed
    ) {
        var allowed = (int)(MaxPassing * hits.Count);
        for (var k = 1; k < timeline.Harmonies.Count && allowed > 0; k++) {
            var (from, to) = (timeline.Harmonies[k - 1], timeline.Harmonies[k]);
            var at = to.Start - Ticks.Quarter / 4;
            if (at < from.Start || Rng.Unit(seed, PassingKey, to.Start) >= 0.5) {
                continue;
            }

            var (a, b) = (voicings[from][^1], voicings[to][^1]);
            var middle = (a + b) / 2.0;
            var between = Enumerable.Range(Math.Min(a, b) + 1, Math.Max(0, Math.Abs(a - b) - 1))
                .Where(p => timeline.InScale(at, p) && !from.Holds(p))
                .OrderBy(p => Math.Abs(p - middle))
                .ThenBy(static p => p)
                .ToList();
            if (between.Count == 0) {
                continue;
            }

            hits.Add(
                new() {
                    Tick = at,
                    Length = Ticks.Quarter / 4 - 40,
                    Pitch = between[0],
                    Velocity = Arranger.Velocity(70, intensity),
                    Event = Arranger.EventOf(at, 3),
                    Passing = true
                }
            );
            allowed--;
        }
    }
}
