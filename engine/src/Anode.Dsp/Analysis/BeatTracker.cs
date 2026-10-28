namespace Anode.Dsp.Analysis;

/// <summary>
///     Beat tracking: an onset envelope, a tempo estimate and a dynamic-programming beat tracker, plus a downbeat
///     estimate and whether the tempo varies. Deterministic.
/// </summary>
/// <remarks>
///     <list type="number">
///         <item>
///             The onset envelope (<see cref="OnsetEnvelope" />, 5 ms hop), normalised by its standard deviation.
///         </item>
///         <item>
///             The global beat period: the autocorrelation peak between 40 and 240 bpm, weighted by a log-normal prior
///             around 120 bpm (one octave wide), refined by parabolic interpolation.
///         </item>
///         <item>
///             The local period: the autocorrelation peak within ±25 % of the global one over 8 s windows every 2 s,
///             so tempo ramps are followed.
///         </item>
///         <item>
///             Ellis's dynamic programme: each frame's score is its onset strength plus the best predecessor's score
///             less a penalty of 100·log²(interval ÷ local period); the beats are backtracked from the last strong
///             peak and weak beats are trimmed off the ends.
///         </item>
///         <item>
///             Each beat snaps to the onset (<see cref="Transients" />) within 40 ms; the others move by the snapped
///             beats' median offset. The tempo is the beats' count over their span; it varies when the median
///             interval over 8 beats does by more than 4 %.
///         </item>
///         <item>
///             Downbeats assume 4/4: the beat phase whose low-band (below 200 Hz) onsets are strongest, the first
///             beat's phase when within 10 %.
///         </item>
///     </list>
/// </remarks>
public static class BeatTracker {
    const double MinBpm = 40;
    const double MaxBpm = 240;
    const double PriorBpm = 120;
    const double PriorOctaves = 1;
    const double Tightness = 100;
    const double SnapSeconds = 0.04;
    const int BeatsPerBar = 4;

    public static BeatTrack Track(ReadOnlySpan<float> mono, int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var envelope = OnsetEnvelope.Compute(mono, sampleRate);
        var none = new BeatTrack(PriorBpm, false, [], [], 0);
        var frames = envelope.Flux.Length;
        var fps = (double)sampleRate / envelope.Hop;
        if (frames < fps * 2) {
            return none;
        }

        var onset = Normalised(envelope.Flux);
        if (onset is null) {
            return none;
        }

        var minLag = (int)Math.Floor(60 * fps / MaxBpm);
        var maxLag = Math.Min(frames - 1, (int)Math.Ceiling(60 * fps / MinBpm));
        if (maxLag <= minLag + 2) {
            return none;
        }

        // Periods come from a 20 ms blur of the envelope, so a drifting tempo still correlates.
        var blurred = Smooth(onset, 0.02 * fps);
        var period = GlobalPeriod(blurred, minLag, maxLag, fps);
        if (period <= 0) {
            return none;
        }

        var periods = LocalPeriods(blurred, period, fps);
        var local = Smooth(onset, period / 32);
        var beatFrames = Dynamic(local, periods);
        if (beatFrames.Count < 2) {
            return none;
        }

        var transients = Transients.Detect(mono, sampleRate, envelope, 0.5);
        var (beats, snapped) = Snap(beatFrames, envelope.Hop, transients, (long)Math.Round(SnapSeconds * sampleRate));
        var bpm = 60.0 * sampleRate * (beats.Length - 1) / (beats[^1] - beats[0]);
        var downbeats = Downbeats(beats, envelope);
        return new(bpm, IsVariable(beats), beats, downbeats, (double)snapped / beats.Length);
    }

    /// <summary>The envelope divided by its standard deviation; null when it is flat.</summary>
    static double[]? Normalised(float[] flux) {
        double mean = 0;
        foreach (var f in flux) {
            mean += f;
        }

        mean /= flux.Length;
        double variance = 0;
        foreach (var f in flux) {
            variance += (f - mean) * (f - mean);
        }

        var deviation = Math.Sqrt(variance / flux.Length);
        if (deviation < 1e-9) {
            return null;
        }

        var result = new double[flux.Length];
        for (var k = 0; k < flux.Length; k++) {
            result[k] = flux[k] / deviation;
        }

        return result;
    }

    /// <summary>The autocorrelation of the mean-removed <paramref name="x" /> over <c>from…to</c>, per overlapping term.</summary>
    static double[] Autocorrelation(double[] x, int from, int to, int maxLag) {
        var n = to - from;
        double mean = 0;
        for (var k = from; k < to; k++) {
            mean += x[k];
        }

        mean /= n;
        var centred = new double[n];
        for (var k = 0; k < n; k++) {
            centred[k] = x[from + k] - mean;
        }

        var result = new double[maxLag + 2];
        for (var lag = 0; lag < result.Length && lag < n; lag++) {
            double sum = 0;
            for (var k = 0; k + lag < n; k++) {
                sum += centred[k] * centred[k + lag];
            }

            result[lag] = sum / (n - lag);
        }

        return result;
    }

    static double Prior(double lag, double fps) {
        var octaves = Math.Log2(60 * fps / lag / PriorBpm) / PriorOctaves;
        return Math.Exp(-0.5 * octaves * octaves);
    }

    /// <summary>
    ///     The beat period in frames: the median of the best periods of 8 s windows every 2 s (or of the whole envelope
    ///     when shorter), so a tempo ramp does not blur the choice of octave; 0 when none is found.
    /// </summary>
    static double GlobalPeriod(double[] onset, int minLag, int maxLag, double fps) {
        var length = (int)Math.Round(8 * fps);
        var step = (int)Math.Round(2 * fps);
        var periods = new List<double>();
        if (onset.Length < length + step) {
            periods.Add(BestPeriod(onset, 0, onset.Length, minLag, maxLag, fps));
        } else {
            for (var start = 0; start + length <= onset.Length; start += step) {
                periods.Add(BestPeriod(onset, start, start + length, minLag, maxLag, fps));
            }
        }

        periods.RemoveAll(static p => p <= 0);
        if (periods.Count == 0) {
            return 0;
        }

        periods.Sort();
        return periods[(periods.Count - 1) / 2];
    }

    /// <summary>The prior-weighted autocorrelation peak of <c>onset[from…to)</c>, refined; 0 when there is none.</summary>
    static double BestPeriod(double[] onset, int from, int to, int minLag, int maxLag, double fps) {
        var reach = Math.Min(to - from - 2, 2 * maxLag + 1);
        maxLag = Math.Min(maxLag, reach - 1);
        if (maxLag <= minLag) {
            return 0;
        }

        var acf = Autocorrelation(onset, from, to, reach);
        var best = -1;
        var bestScore = 0.0;
        for (var lag = Math.Max(1, minLag); lag <= maxLag; lag++) {
            if (acf[lag] < acf[lag - 1] || acf[lag] < acf[lag + 1]) {
                continue;
            }

            // A beat's metrical neighbours correlate too: two beats (a bar's half) and half a beat (the eighths); a
            // confusion with a dotted or triplet period has neither.
            var score = (acf[lag] + 0.5 * Peak(acf, 2 * lag, reach) + 0.5 * Peak(acf, lag / 2.0, reach))
                * Prior(lag, fps);
            if (score > bestScore) {
                bestScore = score;
                best = lag;
            }
        }

        return best < 0 ? 0 : best + Vertex(acf[best - 1], acf[best], acf[best + 1]);
    }

    /// <summary>The largest of <paramref name="acf" /> within a frame of <paramref name="lag" />, at least 0.</summary>
    static double Peak(double[] acf, double lag, int reach) {
        var from = Math.Max(1, (int)Math.Floor(lag) - 1);
        var to = Math.Min(reach, (int)Math.Ceiling(lag) + 1);
        double peak = 0;
        for (var i = from; i <= to; i++) {
            peak = Math.Max(peak, acf[i]);
        }

        return peak;
    }

    /// <summary>The offset −0.5…0.5 of the parabola's vertex through three equally spaced values.</summary>
    static double Vertex(double a, double b, double c) {
        var denominator = a - 2 * b + c;
        return denominator == 0 ? 0 : Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
    }

    /// <summary>The beat period at every frame, from 8 s windows every 2 s (the global period where too short).</summary>
    static double[] LocalPeriods(double[] onset, double global, double fps) {
        var frames = onset.Length;
        var result = new double[frames];
        var length = (int)Math.Round(8 * fps);
        var step = (int)Math.Round(2 * fps);
        var minLag = Math.Max(1, (int)Math.Floor(global * 0.75));
        var maxLag = (int)Math.Ceiling(global * 1.25);
        if (frames < length + step || maxLag * 2 >= length) {
            Array.Fill(result, global);
            return result;
        }

        var centres = new List<double>();
        var values = new List<double>();
        for (var start = 0; start + length <= frames; start += step) {
            var acf = Autocorrelation(onset, start, start + length, maxLag);
            var best = -1;
            for (var lag = minLag; lag <= maxLag; lag++) {
                if (best < 0 || acf[lag] > acf[best]) {
                    best = lag;
                }
            }

            var value = best > minLag && best < maxLag
                ? best + Vertex(acf[best - 1], acf[best], acf[best + 1])
                : global;
            centres.Add(start + length / 2.0);
            values.Add(value);
        }

        // A median of three windows, then linear interpolation between window centres.
        var smoothed = new double[values.Count];
        for (var i = 0; i < values.Count; i++) {
            var a = values[Math.Max(0, i - 1)];
            var b = values[i];
            var c = values[Math.Min(values.Count - 1, i + 1)];
            smoothed[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }

        var w = 0;
        for (var k = 0; k < frames; k++) {
            while (w < centres.Count - 1 && centres[w + 1] <= k) {
                w++;
            }

            if (k <= centres[0]) {
                result[k] = smoothed[0];
            } else if (w >= centres.Count - 1) {
                result[k] = smoothed[^1];
            } else {
                var x = (k - centres[w]) / (centres[w + 1] - centres[w]);
                result[k] = smoothed[w] + x * (smoothed[w + 1] - smoothed[w]);
            }
        }

        return result;
    }

    /// <summary>
    ///     The onset envelope smoothed by a Gaussian of σ = <paramref name="sigma" /> frames (period ÷ 32 for the beat
    ///     tracker's local score, as librosa's).
    /// </summary>
    static double[] Smooth(double[] onset, double sigma) {
        sigma = Math.Max(0.5, sigma);
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new double[2 * radius + 1];
        for (var i = -radius; i <= radius; i++) {
            kernel[i + radius] = Math.Exp(-0.5 * (i / sigma) * (i / sigma));
        }

        var result = new double[onset.Length];
        for (var k = 0; k < onset.Length; k++) {
            double sum = 0;
            for (var i = -radius; i <= radius; i++) {
                var j = k + i;
                if (j >= 0 && j < onset.Length) {
                    sum += kernel[i + radius] * onset[j];
                }
            }

            result[k] = sum;
        }

        return result;
    }

    /// <summary>The dynamic programme: beat frames, ascending.</summary>
    static List<int> Dynamic(double[] local, double[] periods) {
        var frames = local.Length;
        var score = new double[frames];
        var back = new int[frames];
        double strongest = 0;
        foreach (var v in local) {
            strongest = Math.Max(strongest, v);
        }

        var started = false;
        for (var t = 0; t < frames; t++) {
            var period = periods[t];
            var from = t - (int)Math.Round(2 * period);
            var to = t - (int)Math.Round(period / 2);
            var best = double.NegativeInfinity;
            var arg = -1;
            for (var p = Math.Max(0, from); p <= to; p++) {
                var log = Math.Log((t - p) / period);
                var candidate = score[p] - Tightness * log * log;
                if (candidate > best) {
                    best = candidate;
                    arg = p;
                }
            }

            if (!started && local[t] < 0.01 * strongest) {
                arg = -1;
            } else {
                started = true;
            }

            score[t] = local[t] + (arg >= 0 ? best : 0);
            back[t] = arg;
        }

        // The last beat: the last local maximum of the cumulative score above half their median.
        var maxima = new List<int>();
        for (var t = 1; t < frames - 1; t++) {
            if (score[t] > score[t - 1] && score[t] >= score[t + 1]) {
                maxima.Add(t);
            }
        }

        var beats = new List<int>();
        if (maxima.Count == 0) {
            return beats;
        }

        var sorted = maxima.Select(t => score[t]).Order().ToArray();
        var median = sorted[sorted.Length / 2];
        var last = maxima[0];
        foreach (var t in maxima) {
            if (score[t] >= 0.5 * median) {
                last = t;
            }
        }

        for (var t = last; t >= 0; t = back[t]) {
            beats.Add(t);
        }

        beats.Reverse();

        // Trim weak beats off both ends: below half the RMS of the beats' onset strength.
        double power = 0;
        foreach (var b in beats) {
            power += local[b] * local[b];
        }

        var threshold = 0.5 * Math.Sqrt(power / beats.Count);
        var first = 0;
        while (first < beats.Count && local[beats[first]] < threshold) {
            first++;
        }

        var end = beats.Count;
        while (end > first && local[beats[end - 1]] < threshold) {
            end--;
        }

        return beats.GetRange(first, end - first);
    }

    /// <summary>
    ///     Beat frames in file frames: snapped to the nearest onset within <paramref name="tolerance" />, the others moved by
    ///     the snapped ones' median offset; strictly ascending.
    /// </summary>
    static (long[] Beats, int Snapped) Snap(List<int> beatFrames, int hop, long[] transients, long tolerance) {
        var beats = new long[beatFrames.Count];
        var isSnapped = new bool[beats.Length];
        var offsets = new List<long>();
        var j = 0;
        for (var i = 0; i < beats.Length; i++) {
            var raw = (long)beatFrames[i] * hop;
            while (j < transients.Length - 1 && Math.Abs(transients[j + 1] - raw) <= Math.Abs(transients[j] - raw)) {
                j++;
            }

            if (transients.Length > 0 && Math.Abs(transients[j] - raw) <= tolerance) {
                beats[i] = transients[j];
                isSnapped[i] = true;
                offsets.Add(transients[j] - raw);
            } else {
                beats[i] = raw;
            }
        }

        if (offsets.Count > 0) {
            offsets.Sort();
            var median = offsets[offsets.Count / 2];
            for (var i = 0; i < beats.Length; i++) {
                if (!isSnapped[i]) {
                    beats[i] = Math.Max(0, beats[i] + median);
                }
            }
        }

        var result = new List<long>(beats.Length);
        foreach (var b in beats) {
            if (result.Count == 0 || b > result[^1]) {
                result.Add(b);
            }
        }

        return ([..result], offsets.Count);
    }

    static bool IsVariable(long[] beats) {
        const int span = 8;
        if (beats.Length < span + 2) {
            return false;
        }

        var intervals = new long[beats.Length - 1];
        for (var i = 0; i < intervals.Length; i++) {
            intervals[i] = beats[i + 1] - beats[i];
        }

        double lowest = double.MaxValue, highest = 0;
        var window = new long[span];
        for (var i = 0; i + span <= intervals.Length; i++) {
            Array.Copy(intervals, i, window, 0, span);
            Array.Sort(window);
            var median = 0.5 * (window[span / 2 - 1] + window[span / 2]);
            lowest = Math.Min(lowest, median);
            highest = Math.Max(highest, median);
        }

        return highest > lowest * 1.04;
    }

    /// <summary>The beats of the 4/4 phase whose low-band onsets are strongest (the first beat's within 10 %).</summary>
    static long[] Downbeats(long[] beats, OnsetEnvelope envelope) {
        var strength = new double[BeatsPerBar];
        var counts = new int[BeatsPerBar];
        var low = envelope.Low;
        for (var i = 0; i < beats.Length; i++) {
            var centre = (int)Math.Round((double)beats[i] / envelope.Hop);
            double peak = 0;
            for (var k = Math.Max(0, centre - 2); k <= Math.Min(low.Length - 1, centre + 2); k++) {
                peak = Math.Max(peak, low[k]);
            }

            strength[i % BeatsPerBar] += peak;
            counts[i % BeatsPerBar]++;
        }

        var phase = 0;
        var best = counts[0] > 0 ? strength[0] / counts[0] : 0;
        var first = best;
        for (var p = 1; p < BeatsPerBar; p++) {
            var mean = counts[p] > 0 ? strength[p] / counts[p] : 0;
            if (mean > best && mean > first * 1.1) {
                best = mean;
                phase = p;
            }
        }

        var downbeats = new List<long>();
        for (var i = phase; i < beats.Length; i += BeatsPerBar) {
            downbeats.Add(beats[i]);
        }

        return [..downbeats];
    }
}
