using Anode.Dsp.Metering;
using System.Numerics;

namespace Anode.Dsp.Analysis;

/// <summary>Pitch tracking: pYIN, with voicing. Deterministic.</summary>
/// <remarks>
///     pYIN (Mauch and Dixon, 2014), as librosa's <c>pyin</c> but picking the first trough per threshold as the paper
///     does:
///     <list type="number">
///         <item>
///             Per hop, YIN's cumulative mean normalised difference over a 21 ms window and lags up to 50 Hz, the
///             difference computed by FFT.
///         </item>
///         <item>
///             100 thresholds weighted by a Beta(2, 18) prior each vote for the first trough below them (refined by
///             parabolic interpolation), or with weight 0.01 for the deepest trough when none is below; the votes sum to
///             the frame's voicing probability.
///         </item>
///         <item>
///             A hidden Markov model over 20-cent pitch bins (50–2,000 Hz), each voiced and unvoiced, with pitch moves of
///             up to 35.92 octaves a second (triangular) and a voicing switch probability of 0.01, decoded by Viterbi.
///         </item>
///         <item>
///             A voiced frame's pitch is the candidate nearest its bin, corrected to the hop's own time (a lag-τ estimate
///             is centred τ/2 later than the window's start), so glides are not delayed.
///         </item>
///     </list>
///     A frame whose 5 ms around its centre is below −70 dBFS, or 30 dB below its whole window, is unvoiced: notes end
///     where their sound does.
/// </remarks>
public static class PitchTracker {
    const double MinHz = 50;
    const double MaxHz = 2_000;
    const double BinCents = 20;
    const double OctavesPerSecond = 35.92;
    const double SwitchProbability = 0.01;
    const double NoTroughWeight = 0.01;
    const int Thresholds = 100;
    const int MaxCandidates = 16;
    const double SilenceRms = 3.16e-4; // −70 dBFS

    /// <summary>The pitch curve of <paramref name="mono" /> at one value per <paramref name="hop" /> frames (about 5 ms).</summary>
    public static PitchTrack Track(ReadOnlySpan<float> mono, int sampleRate, int hop) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(hop, 1);
        var frames = mono.Length == 0 ? 0 : (mono.Length + hop - 1) / hop;
        var cents = new float[frames];
        var voicing = new float[frames];
        if (frames == 0) {
            return new(hop, cents, voicing);
        }

        var yin = new Yin(sampleRate);
        var bins = (int)Math.Floor(1_200 * Math.Log2(MaxHz / MinHz) / BinCents) + 1;
        var lowCents = 6_900 + 1_200 * Math.Log2(MinHz / 440);
        var candidates = new (double Cents, double Probability)[frames][];
        var offsets = new double[frames][]; // per candidate: its estimate's time minus the hop's, in frames
        var probabilities = new double[frames];
        for (var k = 0; k < frames; k++) {
            var (found, voiced) = yin.Analyse(mono, (long)k * hop);
            candidates[k] = found.Select(static c => (c.Cents, c.Probability)).ToArray();
            offsets[k] = found.Select(static c => c.Offset).ToArray();
            probabilities[k] = voiced;
            voicing[k] = (float)voiced;
        }

        var path = Viterbi(candidates, probabilities, bins, lowCents, sampleRate, hop);
        var raw = new double[frames];
        var offset = new double[frames];
        for (var k = 0; k < frames; k++) {
            var state = path[k];
            if (state >= bins || candidates[k].Length == 0) {
                raw[k] = double.NaN;
                continue;
            }

            var centre = lowCents + state * BinCents;
            var best = 0;
            for (var c = 1; c < candidates[k].Length; c++) {
                if (Math.Abs(candidates[k][c].Cents - centre) < Math.Abs(candidates[k][best].Cents - centre)) {
                    best = c;
                }
            }

            raw[k] = candidates[k][best].Cents;
            offset[k] = offsets[k][best];
        }

        // Each estimate belongs to its window's centre for its lag; move it to the hop's own time along the curve.
        for (var k = 0; k < frames; k++) {
            if (double.IsNaN(raw[k])) {
                cents[k] = float.NaN;
                continue;
            }

            var value = raw[k];
            if (k > 0 && k < frames - 1 && !double.IsNaN(raw[k - 1]) && !double.IsNaN(raw[k + 1])) {
                var slope = (raw[k + 1] - raw[k - 1]) / (2.0 * hop);
                value -= slope * offset[k];
            }

            cents[k] = (float)value;
        }

        return new(hop, cents, voicing);
    }

    /// <summary>The most likely state per frame: a pitch bin below <paramref name="bins" />, or unvoiced at or above it.</summary>
    static int[] Viterbi(
        (double Cents, double Probability)[][] candidates,
        double[] voicedProbability,
        int bins,
        double lowCents,
        int sampleRate,
        int hop
    ) {
        var frames = candidates.Length;
        var reach = Math.Max(1, (int)Math.Round(OctavesPerSecond * 1_200 / BinCents * hop / sampleRate));
        var width = 2 * reach + 1;

        // log T(i → i + d): triangular, each row normalised over the bins it reaches.
        var transition = new double[bins * width];
        for (var i = 0; i < bins; i++) {
            double sum = 0;
            for (var d = -reach; d <= reach; d++) {
                if (i + d >= 0 && i + d < bins) {
                    sum += reach + 1 - Math.Abs(d);
                }
            }

            for (var d = -reach; d <= reach; d++) {
                transition[i * width + d + reach] = Math.Log((reach + 1 - Math.Abs(d)) / sum);
            }
        }

        var stay = Math.Log(1 - SwitchProbability);
        var change = Math.Log(SwitchProbability);
        const double floor = -70; // log of a probability too small to matter, instead of −∞
        var observation = new double[2 * bins];
        var current = new double[2 * bins];
        var next = new double[2 * bins];
        var back = new byte[frames * 2 * bins]; // (d + reach) · 2 + (from the other voicing ? 1 : 0)

        Observe(candidates[0], voicedProbability[0], bins, lowCents, observation, floor);
        Array.Copy(observation, current, current.Length);
        for (var t = 1; t < frames; t++) {
            Observe(candidates[t], voicedProbability[t], bins, lowCents, observation, floor);
            var row = (long)t * 2 * bins;
            for (var j = 0; j < 2 * bins; j++) {
                var voiced = j < bins;
                var bin = voiced ? j : j - bins;
                var best = double.NegativeInfinity;
                var arg = 0;
                for (var d = -reach; d <= reach; d++) {
                    var i = bin - d; // from bin i, a move of d lands on bin
                    if (i < 0 || i >= bins) {
                        continue;
                    }

                    var moved = transition[i * width + d + reach];
                    var same = current[voiced ? i : bins + i] + stay + moved;
                    var other = current[voiced ? bins + i : i] + change + moved;
                    if (same > best) {
                        best = same;
                        arg = (d + reach) * 2;
                    }

                    if (other > best) {
                        best = other;
                        arg = (d + reach) * 2 + 1;
                    }
                }

                next[j] = best + observation[j];
                back[row + j] = (byte)arg;
            }

            // Keep the scores near zero; the decoding does not change.
            var top = double.NegativeInfinity;
            foreach (var v in next) {
                top = Math.Max(top, v);
            }

            for (var j = 0; j < next.Length; j++) {
                current[j] = next[j] - top;
            }
        }

        var path = new int[frames];
        var state = 0;
        for (var j = 1; j < 2 * bins; j++) {
            if (current[j] > current[state]) {
                state = j;
            }
        }

        for (var t = frames - 1; t >= 0; t--) {
            path[t] = state;
            if (t == 0) {
                break;
            }

            int code = back[(long)t * 2 * bins + state];
            var voiced = state < bins;
            var bin = voiced ? state : state - bins;
            var from = bin - (code / 2 - reach);
            var fromVoiced = (code & 1) == 0 ? voiced : !voiced;
            state = fromVoiced ? from : bins + from;
        }

        return path;
    }

    static void Observe(
        (double Cents, double Probability)[] candidates,
        double voiced,
        int bins,
        double lowCents,
        double[] observation,
        double floor
    ) {
        Array.Fill(observation, 0.0, 0, bins);
        foreach (var (cents, probability) in candidates) {
            var bin = (int)Math.Round((cents - lowCents) / BinCents);
            if (bin >= 0 && bin < bins) {
                observation[bin] += probability;
            }
        }

        for (var j = 0; j < bins; j++) {
            observation[j] = observation[j] > 0 ? Math.Max(floor, Math.Log(observation[j])) : floor;
        }

        var unvoiced = Math.Max(floor, Math.Log(Math.Max(1e-30, (1 - Math.Clamp(voiced, 0, 1)) / bins)));
        Array.Fill(observation, unvoiced, bins, bins);
    }

    /// <summary>One frame's YIN: the difference function by FFT, its troughs and their pYIN probabilities.</summary>
    sealed class Yin {
        readonly int sampleRate;
        readonly int window;
        readonly int minLag;
        readonly int maxLag;
        readonly int centreLag;
        readonly int size;
        readonly double[] re;
        readonly double[] im;
        readonly double[] segment;
        readonly double[] difference;
        readonly double[] prior = new double[Thresholds];
        readonly List<(double Cents, double Probability, double Offset)> found = [];

        public Yin(int sampleRate) {
            this.sampleRate = sampleRate;
            window = (int)Math.Round(sampleRate * 0.02133); // 1,024 frames at 48 kHz
            minLag = Math.Max(2, (int)Math.Floor(sampleRate / MaxHz));
            maxLag = (int)Math.Ceiling(sampleRate / MinHz);
            centreLag = (int)Math.Round(sampleRate / 220.0);
            size = (int)BitOperations.RoundUpToPowerOf2((uint)(window + maxLag + 1));
            re = new double[size];
            im = new double[size];
            segment = new double[window + maxLag + 1];
            difference = new double[maxLag + 2];

            // Beta(2, 18): its CDF is 1 − (1 − x)^18 · (1 + 18x); threshold i is (i + 1) / 100.
            const double b = 18;
            double previous = 0;
            for (var i = 0; i < Thresholds; i++) {
                var x = (i + 1) / (double)Thresholds;
                var cdf = 1 - Math.Pow(1 - x, b) * (1 + b * x);
                prior[i] = cdf - previous;
                previous = cdf;
            }
        }

        /// <summary>The candidates of the frame centred on <paramref name="centre" />, and their summed probability.</summary>
        public (List<(double Cents, double Probability, double Offset)> Candidates, double Voiced) Analyse(
            ReadOnlySpan<float> mono,
            long centre
        ) {
            found.Clear();

            // The window starts so that a lag of centreLag (220 Hz) is centred on the hop.
            var start = centre - (window + centreLag) / 2;
            var span = window + maxLag + 1;
            double energy = 0;
            for (var i = 0; i < span; i++) {
                var n = start + i;
                segment[i] = n >= 0 && n < mono.Length ? mono[(int)n] : 0;
                if (i < window + centreLag) {
                    energy += segment[i] * segment[i];
                }
            }

            var quarter = Math.Max(1, sampleRate / 400); // ±2.5 ms around the centre
            double near = 0;
            for (var n = centre - quarter; n < centre + quarter; n++) {
                if (n >= 0 && n < mono.Length) {
                    near += (double)mono[(int)n] * mono[(int)n];
                }
            }

            var nearRms = Math.Sqrt(near / (2 * quarter));
            var windowRms = Math.Sqrt(energy / (window + centreLag));
            if (nearRms < SilenceRms || nearRms < windowRms * 0.0316) {
                return (found, 0);
            }

            Difference();
            Cumulative();
            Vote();
            double voiced = 0;
            foreach (var c in found) {
                voiced += c.Probability;
            }

            return (found, Math.Min(1, voiced));
        }

        /// <summary>
        ///     d(τ) = Σ_{j &lt; W} (x_j − x_{j+τ})² = r_0(0) + r_τ(0) − 2·r(τ), the cross term by FFT: the window in the real
        ///     part, the whole segment in the imaginary part.
        /// </summary>
        void Difference() {
            var span = window + maxLag + 1;
            for (var i = 0; i < size; i++) {
                re[i] = i < window ? segment[i] : 0;
                im[i] = i < span ? segment[i] : 0;
            }

            SplitFft.Forward(re, im);

            // A = FFT(window), B = FFT(segment); conj(A)·B, conjugated again for an inverse through the forward FFT.
            for (var m = 0; m <= size / 2; m++) {
                var mirror = (size - m) & (size - 1);
                double zr = re[m], zi = im[m], cr = re[mirror], ci = im[mirror];
                double ar = 0.5 * (zr + cr), ai = 0.5 * (zi - ci);
                double br = 0.5 * (zi + ci), bi = 0.5 * (cr - zr);
                var pr = ar * br + ai * bi;
                var pi = ar * bi - ai * br;
                re[m] = pr;
                im[m] = -pi;
                if (mirror != m) {
                    re[mirror] = pr;
                    im[mirror] = pi;
                }
            }

            SplitFft.Forward(re, im);

            double windowEnergy = 0;
            for (var j = 0; j < window; j++) {
                windowEnergy += segment[j] * segment[j];
            }

            var shifted = windowEnergy;
            for (var lag = 0; lag <= maxLag + 1; lag++) {
                if (lag > 0) {
                    shifted += segment[lag + window - 1] * segment[lag + window - 1]
                        - segment[lag - 1] * segment[lag - 1];
                }

                var cross = re[lag] / size;
                difference[lag] = Math.Max(0, windowEnergy + shifted - 2 * cross);
            }
        }

        /// <summary>YIN's cumulative mean normalised difference, in place; 1 where undefined.</summary>
        void Cumulative() {
            difference[0] = 1;
            double sum = 0;
            for (var lag = 1; lag <= maxLag + 1; lag++) {
                sum += difference[lag];
                difference[lag] = sum > 0 ? difference[lag] * lag / sum : 1;
            }
        }

        void Vote() {
            // The troughs (local minima) in minLag…maxLag, in lag order.
            Span<int> troughs = stackalloc int[64];
            var count = 0;
            var deepest = -1;
            for (var lag = minLag; lag <= maxLag && count < troughs.Length; lag++) {
                if (difference[lag] < difference[lag - 1] && difference[lag] <= difference[lag + 1]) {
                    troughs[count++] = lag;
                    if (deepest < 0 || difference[lag] < difference[deepest]) {
                        deepest = lag;
                    }
                }
            }

            if (count == 0) {
                return;
            }

            Span<double> weight = stackalloc double[64];
            weight.Clear();
            var none = 0.0;
            for (var i = 0; i < Thresholds; i++) {
                var threshold = (i + 1) / (double)Thresholds;
                var chosen = -1;
                for (var t = 0; t < count; t++) {
                    if (difference[troughs[t]] < threshold) {
                        chosen = t;
                        break;
                    }
                }

                if (chosen >= 0) {
                    weight[chosen] += prior[i];
                } else {
                    none += prior[i];
                }
            }

            for (var t = 0; t < count && found.Count < MaxCandidates; t++) {
                var probability = weight[t] + (troughs[t] == deepest ? none * NoTroughWeight : 0);
                if (probability <= 0) {
                    continue;
                }

                var lag = troughs[t];
                var refined = lag + Vertex(difference[lag - 1], difference[lag], difference[lag + 1]);
                var cents = 6_900 + 1_200 * Math.Log2(sampleRate / refined / 440);
                found.Add((cents, probability, (refined - centreLag) / 2));
            }
        }

        static double Vertex(double a, double b, double c) {
            var denominator = a - 2 * b + c;
            return denominator == 0 ? 0 : Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
        }
    }
}
