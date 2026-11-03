namespace Anode.Dsp.Spatial;

/// <summary>
///     Down-mix from one layout to a smaller one (docs/11-phase-10.md, workstream D): ITU-R BS.775 for 5.x to stereo
///     (centre and surrounds at −3 dB, the LFE dropped), and the standard extensions for 7.x and height layouts (upper
///     layer folded into the middle at −3 dB, sides and rears as surrounds), as a matrix of gains. Used for monitoring
///     (never in bounces) and for the listen-in tap's stereo. Real-time safe after construction.
/// </summary>
/// <remarks>
///     Each source speaker maps by these rules, in order (docs/spatial/README.md has the full matrices):
///     <list type="number">
///         <item>A speaker the target has (by ADM label) passes at 1.</item>
///         <item>
///             The LFE goes to the target's LFE, else at <c>lfeGain</c> (0 by default, BS.775's "LFE dropped") to the
///             target's front pair at −3 dB each (to the centre of a mono target).
///         </item>
///         <item>
///             An upper speaker goes to the nearest upper speaker(s) of the target by azimuth (a tie splits at −3 dB
///             each); with no upper layer in the target it folds into the ear layer at −3 dB (Dolby's and the common
///             practice for height down-mixes) as an ear-layer speaker at its azimuth, then the rules below.
///         </item>
///         <item>The centre without a centre in the target: the front pair at −3 dB each (BS.775); mono takes it at 1.</item>
///         <item>
///             A front speaker (|azimuth| &lt; 90°: L/R, quad's fronts, wides): the target's front speaker on its side
///             nearest in azimuth at 1 (mono: −3 dB, BS.775).
///         </item>
///         <item>
///             A surround (|azimuth| ≥ 90°: sides, surrounds, rears): the target's surround on its side nearest in
///             azimuth (a tie splits at −3 dB each); with no surround in the target, its front speaker on that side at
///             −3 dB (BS.775; mono −6 dB). When k surrounds of one side and layer fold into one target speaker, each is
///             scaled by 1/√k (7.1 to 5.1: sides and rears at −3 dB each, so 7.1 to stereo puts them at −6 dB).
///         </item>
///     </list>
/// </remarks>
public sealed class Downmixer {
    readonly float[] matrix;

    /// <summary>Off the audio thread: the matrix from <paramref name="from" /> to <paramref name="to" />.</summary>
    public Downmixer(SpeakerLayout from, SpeakerLayout to) : this(from, to, 0) { }

    /// <summary>
    ///     Off the audio thread: the matrix from <paramref name="from" /> to <paramref name="to" />, the LFE (when the
    ///     target has none) at <paramref name="lfeGain" /> into the front pair.
    /// </summary>
    public Downmixer(SpeakerLayout from, SpeakerLayout to, float lfeGain) {
        From = from;
        To = to;
        matrix = Build(from, to, lfeGain);
    }

    public SpeakerLayout From { get; }
    public SpeakerLayout To { get; }

    /// <summary>
    ///     The gain of input channel <paramref name="input" /> in output channel <paramref name="output" /> (linear; the
    ///     matrix the tests check against BS.775 within 0.1 dB).
    /// </summary>
    public float Gain(int output, int input) => matrix[output * From.ChannelCount + input];

    /// <summary>Writes the down-mix of <paramref name="input" /> into <paramref name="output" /> (replacing it).</summary>
    public void Process(PlanarBlock input, PlanarBlock output) {
        var frames = Math.Min(input.Frames, output.Frames);
        var inputs = From.ChannelCount;
        for (var o = 0; o < To.ChannelCount; o++) {
            var destination = output.Channel(o)[..frames];
            destination.Clear();
            for (var i = 0; i < inputs; i++) {
                var g = matrix[o * inputs + i];
                if (g == 0) {
                    continue;
                }

                var source = input.Channel(i);
                for (var n = 0; n < frames; n++) {
                    destination[n] += g * source[n];
                }
            }
        }
    }

    static float[] Build(SpeakerLayout from, SpeakerLayout to, float lfeGain) {
        var half = MathF.Sqrt(0.5f);
        var inputs = from.ChannelCount;
        var outputs = to.ChannelCount;
        var gains = new float[outputs * inputs];
        var targetUpper = Enumerable.Range(0, outputs).Where(o => o != to.LfeIndex && to.Elevations[o] > 0).ToArray();
        var targetEar = Enumerable.Range(0, outputs).Where(o => o != to.LfeIndex && to.Elevations[o] <= 0).ToArray();
        var mono = targetEar.Length == 1 && targetUpper.Length == 0;
        var targetCentre = Find(to, "M+000");
        var targetFront = targetEar.Where(o => o != targetCentre && Math.Abs(to.Azimuths[o]) < 90).ToArray();
        var targetSurround = targetEar.Where(o => Math.Abs(to.Azimuths[o]) >= 90).ToArray();

        // (target, gain) pairs per source speaker, before the 1/√k shares.
        var routes = new List<(int Output, float Gain, string? Share)>[inputs];
        for (var i = 0; i < inputs; i++) {
            routes[i] = [];
            var label = from.Label(i);
            var same = Find(to, label);
            if (same >= 0) {
                routes[i].Add((same, 1, null));
                continue;
            }

            if (i == from.LfeIndex) {
                if (lfeGain > 0) {
                    foreach (var (o, g) in FrontPair(to, targetFront, targetCentre, mono)) {
                        routes[i].Add((o, g * lfeGain, null));
                    }
                }

                continue;
            }

            var azimuth = from.Azimuths[i];
            var factor = 1f;
            var layer = "M";
            if (from.Elevations[i] > 0) {
                layer = "U";
                if (targetUpper.Length > 0) {
                    foreach (var o in Nearest(to, targetUpper, azimuth)) {
                        routes[i].Add((o, Tie(Nearest(to, targetUpper, azimuth).Length), Side(azimuth) + layer));
                    }

                    continue;
                }

                factor = half;
            }

            if (azimuth == 0) {
                if (targetCentre >= 0) {
                    routes[i].Add((targetCentre, factor, null));
                } else {
                    foreach (var (o, g) in FrontPair(to, targetFront, targetCentre, mono)) {
                        routes[i].Add((o, g * factor, null));
                    }
                }
            } else if (Math.Abs(azimuth) < 90) {
                if (mono) {
                    routes[i].Add((targetEar[0], factor * half, null));
                } else {
                    var side = targetFront.Where(o => Math.Sign(to.Azimuths[o]) == Math.Sign(azimuth)).ToArray();
                    foreach (var o in Nearest(to, side, azimuth)) {
                        routes[i].Add((o, factor * Tie(Nearest(to, side, azimuth).Length), null));
                    }
                }
            } else {
                var side = targetSurround.Where(o => Math.Sign(to.Azimuths[o]) == Math.Sign(azimuth)).ToArray();
                if (side.Length > 0) {
                    var nearest = Nearest(to, side, azimuth);
                    foreach (var o in nearest) {
                        routes[i].Add((o, factor * Tie(nearest.Length), Side(azimuth) + layer));
                    }
                } else if (mono) {
                    routes[i].Add((targetEar[0], factor * 0.5f, Side(azimuth) + layer));
                } else {
                    var front = targetFront.Where(o => Math.Sign(to.Azimuths[o]) == Math.Sign(azimuth)).ToArray();
                    foreach (var o in Nearest(to, front, azimuth)) {
                        routes[i].Add((o, factor * half, Side(azimuth) + layer));
                    }
                }
            }
        }

        // 1/√k for the k speakers of one side and layer folding into the same target speaker.
        for (var o = 0; o < outputs; o++) {
            foreach (var group in Enumerable.Range(0, inputs)
                         .SelectMany(i => routes[i].Where(r => r.Output == o && r.Share is not null).Select(r => (i, r)))
                         .GroupBy(static x => x.r.Share)) {
                var k = group.Select(static x => x.i).Distinct().Count();
                foreach (var (i, r) in group) {
                    gains[o * inputs + i] += r.Gain / MathF.Sqrt(k);
                }
            }

            for (var i = 0; i < inputs; i++) {
                foreach (var r in routes[i].Where(r => r.Output == o && r.Share is null)) {
                    gains[o * inputs + i] += r.Gain;
                }
            }
        }

        return gains;
    }

    static string Side(float azimuth) => azimuth > 0 ? "L" : "R";

    static float Tie(int count) => count > 1 ? 1 / MathF.Sqrt(count) : 1;

    static (int, float)[] FrontPair(SpeakerLayout to, int[] fronts, int centre, bool mono) {
        if (mono || fronts.Length == 0) {
            return centre >= 0 ? [(centre, 1)] : [];
        }

        var left = fronts.Where(o => to.Azimuths[o] > 0).MinBy(o => to.Azimuths[o]);
        var right = fronts.Where(o => to.Azimuths[o] < 0).MaxBy(o => to.Azimuths[o]);
        var half = MathF.Sqrt(0.5f);
        return [(left, half), (right, half)];
    }

    static int[] Nearest(SpeakerLayout to, int[] candidates, float azimuth) {
        if (candidates.Length == 0) {
            return [];
        }

        var distances = candidates.Select(o => Distance(to.Azimuths[o], azimuth)).ToArray();
        var least = distances.Min();
        return [..candidates.Where((_, k) => distances[k] - least < 1e-3)];
    }

    static float Distance(float a, float b) {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    static int Find(SpeakerLayout layout, string label) {
        for (var c = 0; c < layout.ChannelCount; c++) {
            if (layout.Label(c) == label) {
                return c;
            }
        }

        return -1;
    }
}
