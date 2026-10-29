using Anode.Dsp.Effects;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Tests.Support;

/// <summary>
///     Runs an effect under a playing transport, as the engine will: before each block a <see cref="TransportContext" />
///     at the block's first sample, its tempo from <c>bpm(sample)</c> and its position integrated from the blocks before
///     (a tempo map of steps, one per block; a ramp is a staircase of blocks). <see cref="SampleOf" /> maps a song
///     position back to the sample it falls on in that map, for the expectations.
/// </summary>
public sealed class TransportRun {
    readonly List<(long Sample, double Quarter, double Bpm)> blocks = [];

    public TransportRun(
        IAudioEffect effect,
        float[] left,
        float[]? right,
        Func<long, double> bpm,
        double startQuarter = 0,
        int block = Play.Block,
        Action<long, IAudioEffect>? before = null
    ) {
        Left = (float[])left.Clone();
        Right = (float[])(right ?? left).Clone();
        var quarter = startQuarter;
        for (var at = 0; at < Left.Length; at += block) {
            var count = Math.Min(block, Left.Length - at);
            var tempo = bpm(at);
            var bar = Math.Floor(quarter / 4) * 4;
            blocks.Add((at, quarter, tempo));
            before?.Invoke(at, effect);
            effect.SetTransport(new(true, at, quarter, tempo, 4, 4, bar, Fx.Rate, count));
            effect.Process(Left.AsSpan(at, count), Right.AsSpan(at, count), default, default);
            quarter += count * tempo / (60 * Fx.Rate);
        }

        EndQuarter = quarter;
    }

    public float[] Left { get; }

    public float[] Right { get; }

    /// <summary>The song position after the last sample.</summary>
    public double EndQuarter { get; }

    /// <summary>The (fractional) sample at which song position <paramref name="quarter" /> falls.</summary>
    public double SampleOf(double quarter) {
        for (var i = blocks.Count - 1; i >= 0; i--) {
            var (sample, start, bpm) = blocks[i];
            if (quarter >= start) {
                return sample + (quarter - start) / (bpm / (60 * Fx.Rate));
            }
        }

        return double.NaN;
    }

    /// <summary>A tempo step from <paramref name="from" /> to <paramref name="to" /> bpm at sample <paramref name="at" />.</summary>
    public static Func<long, double> Step(double from, double to, long at) => s => s < at ? from : to;

    /// <summary>A tempo ramp from <paramref name="from" /> to <paramref name="to" /> bpm over <paramref name="frames" />.</summary>
    public static Func<long, double> Ramp(double from, double to, long frames) =>
        s => from + (to - from) * Math.Clamp(s / (double)frames, 0, 1);
}
