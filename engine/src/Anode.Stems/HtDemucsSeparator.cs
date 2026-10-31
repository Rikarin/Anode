using Anode.Contracts;
using Anode.Dsp.Resampling;

namespace Anode.Stems;

/// <summary>
///     HTDemucs around a segment network, as demucs 4.0.1's
///     <c>
///         apply_model(model, mix, shifts=1, split=True,
///         overlap=0.25)
///     </c>
///     runs it, with the one shift fixed at half the maximum (0.25 s) so it is deterministic:
///     <list type="bullet">
///         <item>
///             The input is preceded by 0.25 s of silence (the shift), then cut into segments of the model's length L
///             (343980 samples, 7.8 s) every ⌊0.75 L⌋ samples. A last, shorter segment is centred in a window of L
///             filled with the signal before it (and silence after), as <c>TensorChunk.padded</c> does, and trimmed
///             back to its centre.
///         </item>
///         <item>
///             Each segment's stems (time branch plus iSTFT of the frequency branch) are added with a triangular weight
///             (1 … L/2 … 1, over its peak), and the sum divided by the summed weights: a linear cross-fade over each
///             25 % overlap.
///         </item>
///     </list>
///     Other rates are resampled to the model's and back with the Phase 7 resampler; mono is separated as two equal
///     channels and the stems folded back to one.
/// </summary>
sealed class HtDemucsSeparator : IStemSeparator {
    const double Overlap = 0.25;
    const double ShiftSeconds = 0.25;

    readonly ISegmentModel network;
    readonly Spectrogram spectrogram;
    readonly float[] weight;
    readonly int stride;
    readonly int shift;
    readonly float[] mix;
    readonly float[] spectrum;
    readonly float[] spectrumOut;
    readonly float[] waveOut;
    readonly float[] stem;

    public HtDemucsSeparator(StemModel model, ISegmentModel network) {
        Model = model;
        this.network = network;
        var length = network.SegmentLength;
        spectrogram = new(length);
        stride = (int)((1 - Overlap) * length);
        shift = (int)(ShiftSeconds * network.SampleRate);
        var half = length / 2;
        weight = new float[length];
        for (var i = 0; i < length; i++) {
            weight[i] = (float)(i < half ? i + 1 : length - i) / half;
        }

        var stems = network.Stems.Count;
        mix = new float[2 * length];
        spectrum = new float[spectrogram.SpectrumLength];
        spectrumOut = new float[stems * spectrogram.SpectrumLength];
        waveOut = new float[stems * 2 * length];
        stem = new float[length];
    }

    public StemModel Model { get; }

    public IReadOnlyList<StemKind> Stems => network.Stems;

    public int SampleRate => network.SampleRate;

    public IReadOnlyDictionary<StemKind, float[][]> Separate(
        float[][] channels,
        int sampleRate,
        IReadOnlyList<StemKind> stems,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channels.Length is not (1 or 2) || channels.Any(c => c.Length != channels[0].Length)) {
            throw new ArgumentException("One or two channels of equal length.", nameof(channels));
        }

        var indexes = new int[stems.Count];
        for (var i = 0; i < stems.Count; i++) {
            indexes[i] = IndexOf(stems[i]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var frames = channels[0].Length;
        var left = channels[0];
        var right = channels.Length == 2 ? channels[1] : channels[0];
        if (sampleRate != SampleRate) {
            var converted = SampleRateConverter.Convert([left, right], sampleRate, SampleRate);
            (left, right) = (converted[0], converted[1]);
        }

        var separated = Run(left, right, indexes, progress, cancellationToken);
        var result = new Dictionary<StemKind, float[][]>();
        for (var i = 0; i < stems.Count; i++) {
            result[stems[i]] = Restore(separated[i], sampleRate, frames, channels.Length);
        }

        return result;
    }

    /// <summary>A stem at the model's rate back at the input's rate, length and channel count.</summary>
    float[][] Restore(float[][] output, int sampleRate, int frames, int channelCount) {
        if (sampleRate != SampleRate) {
            output = SampleRateConverter.Convert(output, SampleRate, sampleRate);
            for (var c = 0; c < output.Length; c++) {
                Array.Resize(ref output[c], frames);
            }
        }

        if (channelCount == 2) {
            return output;
        }

        var mono = output[0];
        for (var n = 0; n < frames; n++) {
            mono[n] = 0.5f * (mono[n] + output[1][n]);
        }

        return [mono];
    }

    public void Dispose() => network.Dispose();

    int IndexOf(StemKind kind) {
        for (var s = 0; s < network.Stems.Count; s++) {
            if (network.Stems[s] == kind) {
                return s;
            }
        }

        throw new ArgumentException($"{Model} does not make {kind}.", nameof(kind));
    }

    /// <summary>The stems <paramref name="indexes" /> of a stereo signal at the model's rate.</summary>
    float[][][] Run(
        float[] left,
        float[] right,
        int[] indexes,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        var frames = left.Length;
        var output = new float[indexes.Length][][];
        for (var i = 0; i < indexes.Length; i++) {
            output[i] = [new float[frames], new float[frames]];
        }

        if (frames == 0) {
            progress?.Report(1);
            return output;
        }

        // In "shifted" time the input starts at `shift`; segments start at 0, stride, 2·stride, … before its end.
        var total = frames + shift;
        var segments = (total + stride - 1) / stride;
        var sum = new float[frames];
        for (var j = 0; j < segments; j++) {
            cancellationToken.ThrowIfCancellationRequested();
            Segment(left, right, j * stride, indexes, output, sum, cancellationToken);
            progress?.Report((double)(j + 1) / segments);
        }

        foreach (var stemChannels in output) {
            foreach (var channel in stemChannels) {
                for (var n = 0; n < frames; n++) {
                    channel[n] /= sum[n];
                }
            }
        }

        return output;
    }

    /// <summary>
    ///     Runs the segment at <paramref name="offset" /> (in shifted time) and adds its weighted stems to
    ///     <paramref name="output" /> and its weights to <paramref name="sum" />.
    /// </summary>
    void Segment(
        float[] left,
        float[] right,
        int offset,
        int[] indexes,
        float[][][] output,
        float[] sum,
        CancellationToken cancellationToken
    ) {
        var length = network.SegmentLength;
        var chunk = Math.Min(length, left.Length + shift - offset);
        var trim = (length - chunk) / 2;

        // The window of L around the segment, in input samples: silence outside the input.
        var start = offset - trim - shift;
        Fill(left, start, mix.AsSpan(0, length));
        Fill(right, start, mix.AsSpan(length, length));
        spectrogram.Forward(mix.AsSpan(0, length), 0, spectrum);
        spectrogram.Forward(mix.AsSpan(length, length), 1, spectrum);
        network.Run(mix, spectrum, spectrumOut, waveOut, cancellationToken);

        // Segment sample trim + k lands on input sample offset − shift + k.
        var first = Math.Max(0, shift - offset);
        var at = offset - shift;
        var specLength = spectrogram.SpectrumLength;
        for (var i = 0; i < indexes.Length; i++) {
            var s = indexes[i];
            for (var c = 0; c < 2; c++) {
                waveOut.AsSpan((s * 2 + c) * length, length).CopyTo(stem);
                spectrogram.InverseAdd(spectrumOut.AsSpan(s * specLength, specLength), c, stem);
                var target = output[i][c].AsSpan(at + first, chunk - first);
                var weighted = weight.AsSpan(first, chunk - first);
                var source = stem.AsSpan(trim + first, chunk - first);
                for (var k = 0; k < target.Length; k++) {
                    target[k] += weighted[k] * source[k];
                }
            }
        }

        var sums = sum.AsSpan(at + first, chunk - first);
        for (var k = 0; k < sums.Length; k++) {
            sums[k] += weight[first + k];
        }
    }

    static void Fill(float[] source, int start, Span<float> destination) {
        destination.Clear();
        var from = Math.Max(0, start);
        var to = Math.Min(source.Length, start + destination.Length);
        if (to > from) {
            source.AsSpan(from, to - from).CopyTo(destination[(from - start)..]);
        }
    }
}
