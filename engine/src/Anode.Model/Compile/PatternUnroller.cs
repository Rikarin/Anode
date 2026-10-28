using Anode.Contracts;
using System.Globalization;

namespace Anode.Model.Compile;

/// <summary>A note a pattern plays: its tick relative to the region's start, pitch, velocity and length in ticks.</summary>
public readonly record struct PatternNote(long Tick, int Pitch, int Velocity, long Length);

/// <summary>
///     Unrolls a pattern region into the notes it plays (docs/07-phase-6.md, "Pattern regions"), as the compiler, "convert
///     to MIDI" and the editor (through <c>PatternVectors.json</c>) do alike:
///     <list type="bullet">
///         <item>
///             Every unmuted row walks positions <c>k</c> = 0, 1, … every <c>stepTicks</c> (its own, else the pattern's)
///             while <c>k·stepTicks</c> is inside the region; its pass is <c>k div length</c>, rows cycling independently.
///         </item>
///         <item>
///             The step at position <c>k</c>: forward <c>k mod L</c>; reverse <c>L − 1 − k mod L</c>; ping-pong over a
///             cycle of <c>2L − 2</c> positions without repeating the ends (<c>c = k mod (2L − 2)</c>, the step
///             <c>c</c> while <c>c &lt; L</c>, else <c>2L − 2 − c</c>; a one-step row stays on it); random
///             <c>floor(u·L)</c> for <c>u = Draw(seed, region, row, "r", k)</c>.
///         </item>
///         <item>
///             An on step plays unless <c>Draw(seed, region, row, k mod L, k div L) ≥ chance</c>: the generator is keyed
///             on the row and the position in the pass and the pass, so each position of every pass draws anew.
///         </item>
///         <item>
///             Its start is <c>k·stepTicks</c>, plus <c>swing·120</c> ticks when that start is an even 16th (240 mod
///             480), plus <c>offset·stepTicks</c>, rounded, at least 0; its gate is <c>gate·stepTicks</c> ticks, split
///             into <c>repeats</c> equal notes (each from <c>round(start + i·gate/n)</c> to <c>round(start + (i +
///             1)·gate/n)</c>, at least a tick); a note starting at or after the region's end is dropped, one running
///             past it is cut there.
///         </item>
///         <item>
///             Pitch: a note row's or pad row's <c>note</c>; a chord-degree row plays the triad of degree <c>note</c>
///             (1…7) of the key at the step's tick (major or natural minor), its root in 60…71.
///         </item>
///     </list>
///     The notes come sorted by tick, then pitch, then row order.
/// </summary>
public static class PatternUnroller {
    static readonly int[] Major = [0, 2, 4, 5, 7, 9, 11];
    static readonly int[] Minor = [0, 2, 3, 5, 7, 8, 10];

    /// <summary>
    ///     The notes of a pattern region (<paramref name="keyAt" /> gives the key at an absolute tick; null: C major).
    /// </summary>
    public static List<PatternNote> Unroll(PatternRegion region, Func<long, KeyEvent>? keyAt = null) {
        var notes = new List<(PatternNote Note, int Row)>();
        var pattern = region.Pattern;
        var seed = pattern.Seed.ToString(CultureInfo.InvariantCulture);
        for (var r = 0; r < pattern.Rows.Count; r++) {
            var row = pattern.Rows[r];
            if (row.Muted || row.Length < 1 || row.Steps.Count < row.Length) {
                continue;
            }

            var stepTicks = row.StepTicks ?? pattern.StepTicks;
            if (stepTicks <= 0) {
                continue;
            }

            var length = row.Length;
            for (long k = 0; k * stepTicks < region.LengthTicks; k++) {
                var step = row.Steps[StepAt(row.PlayMode, length, k, seed, region.Id, row.Id)];
                if (!step.On) {
                    continue;
                }

                if (step.Chance < 1
                    && Draw(seed, region.Id, row.Id, (k % length).ToString(CultureInfo.InvariantCulture), k / length)
                    >= step.Chance) {
                    continue;
                }

                var at = (double)(k * stepTicks);
                if ((k * stepTicks) % 480 == 240) {
                    at += pattern.Swing * 120;
                }

                at += step.Offset * stepTicks;
                at = Math.Max(0, at);
                var gate = step.Gate * stepTicks;
                var repeats = Math.Max(1, step.Repeats);
                var pitches = Pitches(row, keyAt?.Invoke(region.StartTick + (long)Math.Round(at)));
                for (var i = 0; i < repeats; i++) {
                    var on = (long)Math.Round(at + i * gate / repeats);
                    var off = Math.Max(on + 1, (long)Math.Round(at + (i + 1) * gate / repeats));
                    if (on >= region.LengthTicks) {
                        break;
                    }

                    off = Math.Min(off, region.LengthTicks);
                    foreach (var pitch in pitches) {
                        notes.Add((new(on, pitch, Math.Clamp(step.Velocity, 1, 127), Math.Max(1, off - on)), r));
                    }
                }
            }
        }

        return [
            ..notes.Select(static (n, i) => (n.Note, n.Row, i))
                .OrderBy(static x => x.Note.Tick)
                .ThenBy(static x => x.Note.Pitch)
                .ThenBy(static x => x.Row)
                .ThenBy(static x => x.i)
                .Select(static x => x.Note)
        ];
    }

    /// <summary>The step a row plays at position <paramref name="k" />.</summary>
    public static int StepAt(PatternPlayMode mode, int length, long k, string seed, string regionId, string rowId) =>
        mode switch {
            PatternPlayMode.Reverse => length - 1 - (int)(k % length),
            PatternPlayMode.PingPong when length > 1 => (int)(k % (2 * length - 2)) is var c && c < length
                ? c
                : 2 * length - 2 - c,
            PatternPlayMode.PingPong => 0,
            PatternPlayMode.Random => Math.Min(
                length - 1,
                (int)(Draw(seed, regionId, rowId, "r", k) * length)
            ),
            _ => (int)(k % length)
        };

    /// <summary>The pitches a row plays: its note, or a chord degree's triad in <paramref name="key" />.</summary>
    static int[] Pitches(PatternRow row, KeyEvent? key) {
        if (row.Kind != PatternRowKind.ChordDegree) {
            return [Math.Clamp(row.Note, 0, 127)];
        }

        var scale = key?.Mode == KeyMode.Minor ? Minor : Major;
        var tonic = ((key?.Tonic ?? 0) % 12 + 12) % 12;
        var degree = Math.Clamp(row.Note, 1, 7) - 1;
        var root = 60 + (tonic + scale[degree]) % 12;
        var rootRaw = tonic + scale[degree];
        int Pitch(int j) => root + (tonic + scale[j % 7] + 12 * (j / 7)) - rootRaw;
        return [Pitch(degree), Pitch(degree + 2), Pitch(degree + 4)];
    }

    /// <summary>
    ///     The deterministic generator: a uniform number in [0, 1) for the key
    ///     <c>"{seed}:{regionId}:{rowId}:{step}:{pass}"</c>, FNV-1a (32-bit) over its UTF-16 code units, then MurmurHash3's
    ///     32-bit finaliser, over 2³². JavaScript computes the same with <c>Math.imul</c> and <c>&gt;&gt;&gt;</c>.
    /// </summary>
    public static double Draw(string seed, string regionId, string rowId, string step, long pass) {
        var key = string.Concat(seed, ":", regionId, ":", rowId, ":", step, ":", pass.ToString(CultureInfo.InvariantCulture));
        var h = 2166136261u;
        foreach (var unit in key) {
            h ^= unit;
            h *= 16777619u;
        }

        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h / 4294967296.0;
    }
}
