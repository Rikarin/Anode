using Anode.Dsp.Spectral;

namespace Anode.Stems;

/// <summary>
///     HTDemucs' STFT and iSTFT (<c>HTDemucs._spec</c>, <c>_ispec</c>, demucs 4.0.1) for segments of one length: a
///     periodic Hann window of 4096, hop 1024, normalised (÷ √4096 forward, × √4096 back), the Nyquist bin dropped.
///     The signal is reflect-padded by 1536 on the left and to a whole number of hops plus 1536 on the right, and the
///     transform keeps <see cref="Frames" /> = ⌈length / 1024⌉ frames, the ones whose window lies inside that padding
///     (so torch's own centre padding never reaches them). The inverse adds the frames back with the window and divides
///     by the full window envelope (Σ w² = 1.5), as <c>torch.istft</c> does with the two zero frames HTDemucs pads on
///     each side.
/// </summary>
/// <remarks>
///     As in HTDemucs, the pair is not the identity at a segment's ends: the frames that would complete the first and
///     last 1536 samples are dropped, and so is the Nyquist bin. Inside, it reconstructs to rounding. Not thread-safe.
/// </remarks>
sealed class Spectrogram {
    public const int Size = 4096;
    public const int Hop = 1024;
    public const int Bins = Size / 2;
    const int Pad = Hop / 2 * 3;
    const float Scale = 64; // √4096

    readonly RealFft fft = new(Size);
    readonly float[] window = new float[Size];
    readonly float[] inverseEnvelope = new float[Hop];
    readonly float[] frame = new float[Size];
    readonly float[] real = new float[Bins + 1];
    readonly float[] imaginary = new float[Bins + 1];
    readonly float[] accumulator;

    public Spectrogram(int length) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, Pad + Hop);
        Length = length;
        Frames = (length + Hop - 1) / Hop;
        for (var n = 0; n < Size; n++) {
            window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Size));
        }

        for (var p = 0; p < Hop; p++) {
            var sum = 0f;
            for (var j = 0; j < Size / Hop; j++) {
                sum += window[p + j * Hop] * window[p + j * Hop];
            }

            inverseEnvelope[p] = 1 / sum;
        }

        accumulator = new float[Frames * Hop + 2 * Pad];
    }

    /// <summary>The segment length in samples.</summary>
    public int Length { get; }

    /// <summary>The number of frames, ⌈<see cref="Length" /> / 1024⌉.</summary>
    public int Frames { get; }

    /// <summary>The floats of one stereo spectrogram: [4, 2048, <see cref="Frames" />].</summary>
    public int SpectrumLength => 4 * Bins * Frames;

    /// <summary>
    ///     The spectrogram of <paramref name="signal" /> (<see cref="Length" /> samples) into channel
    ///     <paramref name="channel" /> of <paramref name="spectrum" />, laid out [4, 2048, frames] with the real and
    ///     imaginary parts of channel <c>c</c> at <c>2c</c> and <c>2c + 1</c> (HTDemucs' complex-as-channels).
    /// </summary>
    public void Forward(ReadOnlySpan<float> signal, int channel, Span<float> spectrum) {
        var length = Length;
        var re = spectrum.Slice(2 * channel * Bins * Frames, Bins * Frames);
        var im = spectrum.Slice((2 * channel + 1) * Bins * Frames, Bins * Frames);
        for (var t = 0; t < Frames; t++) {
            var start = t * Hop - Pad;
            for (var m = 0; m < Size; m++) {
                var i = start + m;
                if (i < 0) {
                    i = -i;
                } else if (i >= length) {
                    i = 2 * (length - 1) - i;
                }

                frame[m] = signal[i] * window[m];
            }

            fft.Forward(frame, real, imaginary);
            for (var k = 0; k < Bins; k++) {
                re[k * Frames + t] = real[k] / Scale;
                im[k * Frames + t] = imaginary[k] / Scale;
            }
        }
    }

    /// <summary>
    ///     The signal (<see cref="Length" /> samples) of channel <paramref name="channel" /> of
    ///     <paramref name="spectrum" /> (laid out as <see cref="Forward" /> writes it), added to <paramref name="output" />.
    /// </summary>
    public void InverseAdd(ReadOnlySpan<float> spectrum, int channel, Span<float> output) {
        var re = spectrum.Slice(2 * channel * Bins * Frames, Bins * Frames);
        var im = spectrum.Slice((2 * channel + 1) * Bins * Frames, Bins * Frames);
        Array.Clear(accumulator);
        real[Bins] = 0;
        imaginary[Bins] = 0;
        for (var t = 0; t < Frames; t++) {
            for (var k = 0; k < Bins; k++) {
                real[k] = re[k * Frames + t] * Scale;
                imaginary[k] = im[k * Frames + t] * Scale;
            }

            // irfft ignores the imaginary part of DC (and of Nyquist, which is zero here).
            imaginary[0] = 0;
            fft.Inverse(real, imaginary, frame);
            var at = accumulator.AsSpan(t * Hop, Size);
            for (var m = 0; m < Size; m++) {
                at[m] += frame[m] * window[m];
            }
        }

        for (var n = 0; n < Length; n++) {
            var p = n + Pad;
            output[n] += accumulator[p] * inverseEnvelope[p % Hop];
        }
    }
}
