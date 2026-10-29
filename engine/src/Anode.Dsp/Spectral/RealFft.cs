namespace Anode.Dsp.Spectral;

/// <summary>
///     A real FFT of a fixed power-of-two size in single precision, for the audio thread: the transform of
///     <see cref="Size" /> real samples as <see cref="Bins" /> = <c>Size/2 + 1</c> complex bins on split arrays, and back.
///     It runs a complex FFT of half the size on the even and odd samples and untangles the halves (the usual "two for
///     one" packing). Twiddles and the bit reversal are tables built once, in double precision; after construction
///     nothing allocates. Unlike <see cref="Metering.SplitFft" /> (double precision, any caller), one instance is not
///     safe on two threads at once: it keeps working buffers.
/// </summary>
public sealed class RealFft {
    readonly int half;
    readonly int[] reverse;
    readonly float[] cos;
    readonly float[] sin;
    readonly float[] postCos;
    readonly float[] postSin;
    readonly float[] re;
    readonly float[] im;

    public RealFft(int size) {
        if (size < 4 || (size & (size - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A power of two of at least 4.");
        }

        Size = size;
        half = size / 2;
        reverse = new int[half];
        var bits = 0;
        while (1 << bits < half) {
            bits++;
        }

        for (var i = 0; i < half; i++) {
            var r = 0;
            for (var b = 0; b < bits; b++) {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            reverse[i] = r;
        }

        // The half-size FFT's twiddles e^(−2πik/half), k < half/2, and the untangling's e^(−2πik/size), k ≤ half.
        cos = new float[Math.Max(1, half / 2)];
        sin = new float[cos.Length];
        for (var k = 0; k < cos.Length; k++) {
            cos[k] = (float)Math.Cos(2 * Math.PI * k / half);
            sin[k] = (float)-Math.Sin(2 * Math.PI * k / half);
        }

        postCos = new float[half + 1];
        postSin = new float[half + 1];
        for (var k = 0; k <= half; k++) {
            postCos[k] = (float)Math.Cos(2 * Math.PI * k / size);
            postSin[k] = (float)-Math.Sin(2 * Math.PI * k / size);
        }

        re = new float[half];
        im = new float[half];
    }

    /// <summary>The number of real samples.</summary>
    public int Size { get; }

    /// <summary>The number of complex bins, 0 … Nyquist.</summary>
    public int Bins => half + 1;

    /// <summary>
    ///     The spectrum of <paramref name="input" /> (<see cref="Size" /> samples; missing ones are zeros) into
    ///     <paramref name="real" /> and <paramref name="imaginary" /> (<see cref="Bins" /> each). Unscaled.
    /// </summary>
    public void Forward(ReadOnlySpan<float> input, Span<float> real, Span<float> imaginary) {
        for (var i = 0; i < half; i++) {
            var j = reverse[i];
            var at = 2 * j;
            re[i] = at < input.Length ? input[at] : 0;
            im[i] = at + 1 < input.Length ? input[at + 1] : 0;
        }

        Transform(re, im);
        // X[k] = E + W^k·O with E = (Z[k] + conj Z[h−k]) / 2 and O = (Z[k] − conj Z[h−k]) / 2i.
        real[0] = re[0] + im[0];
        imaginary[0] = 0;
        real[half] = re[0] - im[0];
        imaginary[half] = 0;
        for (var k = 1; k < half; k++) {
            var zr = re[k];
            var zi = im[k];
            var cr = re[half - k];
            var ci = -im[half - k];
            var er = 0.5f * (zr + cr);
            var ei = 0.5f * (zi + ci);
            // (Z − conj) / 2i = (dr + i·di) / 2i = (di − i·dr) / 2
            var or = 0.5f * (zi - ci);
            var oi = -0.5f * (zr - cr);
            var wr = postCos[k];
            var wi = postSin[k];
            real[k] = er + wr * or - wi * oi;
            imaginary[k] = ei + wr * oi + wi * or;
        }
    }

    /// <summary>
    ///     The <see cref="Size" /> samples whose spectrum is <paramref name="real" />, <paramref name="imaginary" />
    ///     (<see cref="Bins" /> each), into <paramref name="output" />, scaled so that
    ///     <see cref="Forward" /> then <see cref="Inverse" /> is the identity.
    /// </summary>
    public void Inverse(ReadOnlySpan<float> real, ReadOnlySpan<float> imaginary, Span<float> output) {
        // E = (X[k] + conj X[h−k]) / 2, O = (X[k] − conj X[h−k]) · conj(W^k) / 2, Z = E + i·O; the inverse half-size
        // FFT by conjugation: z = conj(FFT(conj Z)) / h.
        for (var k = 0; k < half; k++) {
            var xr = real[k];
            var xi = imaginary[k];
            var cr = real[half - k];
            var ci = -imaginary[half - k];
            var er = 0.5f * (xr + cr);
            var ei = 0.5f * (xi + ci);
            var dr = 0.5f * (xr - cr);
            var di = 0.5f * (xi - ci);
            var wr = postCos[k];
            var wi = -postSin[k];
            var or = dr * wr - di * wi;
            var oi = dr * wi + di * wr;
            var j = reverse[k];
            re[j] = er - oi;
            im[j] = -(ei + or);
        }

        Transform(re, im);
        var scale = 1f / half;
        for (var n = 0; n < half; n++) {
            output[2 * n] = re[n] * scale;
            output[2 * n + 1] = -im[n] * scale;
        }
    }

    /// <summary>The in-place complex FFT of bit-reversed input.</summary>
    void Transform(float[] real, float[] imaginary) {
        for (var length = 2; length <= half; length <<= 1) {
            var span = length >> 1;
            var stride = half / length;
            for (var start = 0; start < half; start += length) {
                for (var k = 0; k < span; k++) {
                    var a = start + k;
                    var b = a + span;
                    var wr = cos[k * stride];
                    var wi = sin[k * stride];
                    var vr = real[b] * wr - imaginary[b] * wi;
                    var vi = real[b] * wi + imaginary[b] * wr;
                    real[b] = real[a] - vr;
                    imaginary[b] = imaginary[a] - vi;
                    real[a] += vr;
                    imaginary[a] += vi;
                }
            }
        }
    }
}
