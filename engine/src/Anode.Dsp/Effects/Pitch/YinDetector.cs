using Anode.Dsp.Spectral;

namespace Anode.Dsp.Effects.Pitch;

/// <summary>
///     YIN (de Cheveigné and Kawahara, 2002) on the end of a ring of mono audio, allocation-free after construction: the
///     difference function by FFT, its cumulative-mean normalisation, the first trough under 0.12 (refined by a
///     parabola), periods between the two frequencies given. No trough under 0.25, or a quiet window (below −70 dBFS), is
///     unvoiced. Pitch Correction, the Voice Shaper and the Tuner run it on the audio thread.
/// </summary>
public sealed class YinDetector {
    const double Trough = 0.12;
    const double Voicing = 0.25;
    const double QuietPower = 1e-7;

    readonly RealFft fft;
    readonly float[] segment;
    readonly float[] head;
    readonly float[] segmentRe;
    readonly float[] segmentIm;
    readonly float[] headRe;
    readonly float[] headIm;
    readonly float[] correlation;
    readonly double[] energy;
    readonly double[] normalised;
    readonly int window;

    public YinDetector(float sampleRate, double minHz, double maxHz) {
        MaxPeriod = (int)Math.Ceiling(sampleRate / minHz);
        MinPeriod = (int)Math.Floor(sampleRate / maxHz);
        window = 1;
        while (window < MaxPeriod) {
            window <<= 1;
        }

        var size = 1;
        while (size < window + MaxPeriod + 2) {
            size <<= 1;
        }

        fft = new(size);
        segment = new float[size];
        head = new float[size];
        segmentRe = new float[fft.Bins];
        segmentIm = new float[fft.Bins];
        headRe = new float[fft.Bins];
        headIm = new float[fft.Bins];
        correlation = new float[size];
        energy = new double[size + 1];
        normalised = new double[MaxPeriod + 2];
    }

    /// <summary>The longest period it looks for, in samples.</summary>
    public int MaxPeriod { get; }

    /// <summary>The shortest period it looks for, in samples.</summary>
    public int MinPeriod { get; }

    /// <summary>The history a <see cref="Detect" /> reads, and so the least a ring must hold.</summary>
    public int Size => segment.Length;

    /// <summary>
    ///     Looks for a period in the window of <paramref name="ring" /> (a power-of-two ring, <paramref name="mask" /> its
    ///     length − 1) ending at sample <paramref name="now" />; false when the window is unvoiced.
    /// </summary>
    public bool Detect(float[] ring, int mask, long now, out double period) {
        period = 0;
        var size = segment.Length;
        var span = window + MaxPeriod;
        var start = now - span + 1;
        double power = 0;
        energy[0] = 0;
        for (var k = 0; k < size; k++) {
            var x = k < span ? ring[(int)((start + k) & mask)] : 0;
            segment[k] = x;
            head[k] = k < window ? x : 0;
            energy[k + 1] = energy[k] + (double)x * x;
            if (k < window) {
                power += (double)x * x;
            }
        }

        if (now + 1 < span || power / window < QuietPower) {
            return false;
        }

        // r(τ) = Σ head[j]·segment[j + τ], from conj(HEAD)·SEGMENT.
        fft.Forward(segment, segmentRe, segmentIm);
        fft.Forward(head, headRe, headIm);
        for (var k = 0; k < segmentRe.Length; k++) {
            var ar = headRe[k];
            var ai = -headIm[k];
            var br = segmentRe[k];
            var bi = segmentIm[k];
            segmentRe[k] = ar * br - ai * bi;
            segmentIm[k] = ar * bi + ai * br;
        }

        fft.Inverse(segmentRe, segmentIm, correlation);
        var e0 = energy[window];
        double running = 0;
        normalised[0] = 1;
        var found = -1;
        for (var tau = 1; tau <= MaxPeriod + 1 && tau < normalised.Length; tau++) {
            var et = energy[tau + window] - energy[tau];
            var d = Math.Max(0, e0 + et - 2 * correlation[tau]);
            running += d;
            normalised[tau] = running > 0 ? d * tau / running : 1;
        }

        for (var tau = MinPeriod; tau <= MaxPeriod; tau++) {
            if (normalised[tau] < Trough) {
                while (tau + 1 <= MaxPeriod && normalised[tau + 1] < normalised[tau]) {
                    tau++;
                }

                found = tau;
                break;
            }
        }

        if (found < 0) {
            var best = MinPeriod;
            for (var tau = MinPeriod + 1; tau <= MaxPeriod; tau++) {
                if (normalised[tau] < normalised[best]) {
                    best = tau;
                }
            }

            if (normalised[best] >= Voicing) {
                return false;
            }

            found = best;
        }

        period = Peak(found);
        return true;
    }

    /// <summary>
    ///     The last <see cref="Detect" />'s period measured over as many periods as the window holds: the trough at k·T
    ///     gives T k times finer, which a high note, whose period is a few samples, needs for a cent's accuracy.
    /// </summary>
    public double Refined(double period) {
        var refined = period;
        for (var k = 2; k * refined + 2 <= MaxPeriod; k *= 2) {
            var near = (int)Math.Round(k * refined);
            var best = near;
            for (var tau = near - 1; tau <= near + 1; tau++) {
                if (normalised[tau] < normalised[best]) {
                    best = tau;
                }
            }

            refined = Peak(best) / k;
        }

        return refined;
    }

    /// <summary>The trough at lag <paramref name="tau" />, refined by a parabola through it and its neighbours.</summary>
    double Peak(int tau) {
        double refined = tau;
        if (tau > 1 && tau < MaxPeriod + 1) {
            var a = normalised[tau - 1];
            var b = normalised[tau];
            var c = normalised[tau + 1];
            var curvature = a - 2 * b + c;
            if (curvature > 1e-12) {
                refined += Math.Clamp((a - c) / (2 * curvature), -0.5, 0.5);
            }
        }

        return refined;
    }
}
