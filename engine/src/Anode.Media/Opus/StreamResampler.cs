using Anode.Dsp.Resampling;
using System.Runtime.InteropServices;

namespace Anode.Media.Opus;

/// <summary>
///     Interleaved stereo from <c>fromRate</c> to <c>toRate</c> as it arrives: the same Kaiser-windowed sinc as
///     <see cref="SampleRateConverter" /> (<see cref="WindowedSinc" />, polyphase at the exact rational position of every
///     output sample, 110 dB stopband), with history kept between calls. Output sample <c>n</c> sits at input time
///     <c>n · from / to</c> (input sample 0 is time 0, silence before it); it is written once the input reaches
///     <c>half</c> samples past it, so the stream lags by that look-ahead (<see cref="LatencyInputFrames" />) but its
///     positions are exact. At equal rates it copies.
/// </summary>
sealed class StreamResampler {
    const int Channels = 2;
    const double StopbandDb = 110;
    const double CutoffFraction = 0.955;
    const double TransitionFraction = 0.09;
    const int MaxPhases = 4096;

    readonly bool copy;
    readonly long up;
    readonly long down;
    readonly int phases;
    readonly int half;
    readonly double[][] rows = [];

    /// <summary>Input frames from <see cref="historyStart" /> on, interleaved.</summary>
    readonly List<float> history = [];

    long historyStart;
    long received;
    long produced;

    public StreamResampler(int fromRate, int toRate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);
        FromRate = fromRate;
        ToRate = toRate;
        copy = fromRate == toRate;
        if (copy) {
            return;
        }

        var divisor = Gcd(fromRate, toRate);
        up = toRate / divisor;
        down = fromRate / divisor;
        phases = (int)Math.Min(up, MaxPhases);
        var nyquist = Math.Min(fromRate, toRate) / 2.0;
        var cutoff = CutoffFraction * nyquist / fromRate;
        var transition = TransitionFraction * nyquist / fromRate;
        var beta = WindowedSinc.Beta(StopbandDb);
        half = WindowedSinc.HalfLength(StopbandDb, transition);
        rows = new double[phases + 1][];
        for (var p = 0; p <= phases; p++) {
            var row = new double[2 * half];
            var fraction = (double)p / phases;
            for (var j = 0; j < row.Length; j++) {
                row[j] = WindowedSinc.Kernel(j - half + 1 - fraction, cutoff, half, beta);
            }

            rows[p] = row;
        }
    }

    public int FromRate { get; }

    public int ToRate { get; }

    /// <summary>How many input frames past an output sample's position the input must reach before it is written.</summary>
    public int LatencyInputFrames => copy ? 0 : half;

    /// <summary>
    ///     Adds <paramref name="interleaved" /> stereo input and appends every output frame it completes to
    ///     <paramref name="output" /> (interleaved).
    /// </summary>
    public void Process(ReadOnlySpan<float> interleaved, List<float> output) {
        if (copy) {
            output.AddRange(interleaved);
            return;
        }

        history.AddRange(interleaved);
        received += interleaved.Length / Channels;
        var samples = CollectionsMarshal.AsSpan(history);
        while (true) {
            var position = produced * down;
            var whole = position / up;

            // Input frames whole − half + 1 … whole + half are needed.
            if (whole + half >= received) {
                break;
            }

            var phase = (double)(position % up) * phases / up;
            var row = (int)phase;
            var weight = phase - row;
            var first = whole - half + 1;
            for (var c = 0; c < Channels; c++) {
                var value = Dot(samples, first, c, rows[row]);
                if (weight > 0) {
                    value += weight * (Dot(samples, first, c, rows[row + 1]) - value);
                }

                output.Add((float)value);
            }

            produced++;
        }

        // Keep what the next output needs.
        var keepFrom = Math.Max(historyStart, produced * down / up - half + 1);
        var drop = (int)(keepFrom - historyStart);
        if (drop > 4096) {
            history.RemoveRange(0, drop * Channels);
            historyStart = keepFrom;
        }
    }

    double Dot(ReadOnlySpan<float> samples, long first, int channel, double[] row) {
        var sum = 0.0;
        for (var j = 0; j < row.Length; j++) {
            var index = first + j;
            if (index >= historyStart) {
                sum += samples[(int)((index - historyStart) * Channels) + channel] * row[j];
            }
        }

        return sum;
    }

    static long Gcd(long a, long b) {
        while (b != 0) {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
