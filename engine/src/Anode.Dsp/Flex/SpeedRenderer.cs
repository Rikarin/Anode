namespace Anode.Dsp.Flex;

/// <summary>
///     Speed (docs/06-phase-5.md, "Flex algorithms"): each output frame is the file band-limited and interpolated at
///     the fractional frame the warp map gives it, with a Kaiser-windowed sinc of 16 zero crossings each side. Where
///     the warp plays faster than 1:1 the kernel widens by the rate, so its cutoff follows the output's Nyquist frequency
///     (up to <see cref="MaxRate" />, beyond which it aliases). Pitch follows speed exactly, as on a tape.
/// </summary>
internal sealed class SpeedRenderer : WindowedFlexRenderer {
    /// <summary>The widest rate the anti-aliasing kernel follows.</summary>
    public const double MaxRate = 8;

    const int ZeroCrossings = 16;
    const int Resolution = 512;
    static readonly float[] Kernel = MakeKernel();

    public SpeedRenderer(int channels, int maxFrames)
        : base(channels, Math.Max(maxFrames, 1_024) + 2 * (int)(ZeroCrossings * MaxRate) + 8_192) { }

    protected override void RenderRun(in FlexRegion region, long offset, Span<float> output, IFlexSource source) {
        var warp = region.Warp;
        var channels = Channels;
        var data = Window.Data;
        var kernel = Kernel;
        Span<double> sums = stackalloc double[channels];
        Span<float> taps = stackalloc float[2 * (int)(ZeroCrossings * MaxRate) + 2];
        var hint = 0;
        for (var n = 0; n < output.Length / channels; n++) {
            var s = WarpMap.Segment(warp, offset + n, ref hint);
            var a = warp[s];
            var b = warp[s + 1];
            var rate = (b.FileFrame - a.FileFrame) / (b.Offset - a.Offset);
            var position = a.FileFrame + (offset + n - a.Offset) * rate;
            rate = Math.Abs(rate);
            var cutoff = rate > 1 ? 1 / Math.Min(rate, MaxRate) : 1;
            var half = ZeroCrossings / cutoff;
            var first = (long)Math.Ceiling(position - half);
            var count = (int)((long)Math.Floor(position + half) - first + 1);
            var index = Window.Locate(source, first, count);

            // The kernel's taps, then their weighted sum per channel, normalised so that DC passes exactly.
            double weights = 0;
            var step = cutoff * Resolution;
            var from = position - first;
            for (var j = 0; j < count; j++) {
                var distance = Math.Min(Math.Abs(from - j) * step, ZeroCrossings * Resolution);
                var k = (int)distance;
                var weight = kernel[k] + (kernel[k + 1] - kernel[k]) * (float)(distance - k);
                taps[j] = weight;
                weights += weight;
            }

            if (weights == 0) {
                continue;
            }

            if (channels == 2) {
                double left = 0, right = 0;
                for (var j = 0; j < count; j++, index += 2) {
                    left += taps[j] * data[index];
                    right += taps[j] * data[index + 1];
                }

                output[n * 2] = (float)(left / weights);
                output[n * 2 + 1] = (float)(right / weights);
                continue;
            }

            sums.Clear();
            for (var j = 0; j < count; j++, index += channels) {
                for (var c = 0; c < channels; c++) {
                    sums[c] += taps[j] * data[index + c];
                }
            }

            for (var c = 0; c < channels; c++) {
                output[n * channels + c] = (float)(sums[c] / weights);
            }
        }
    }

    /// <summary>sinc(x)·kaiser(x) for x from 0 to <see cref="ZeroCrossings" />, <see cref="Resolution" /> steps each.</summary>
    static float[] MakeKernel() {
        const double beta = 8.6;
        var kernel = new float[ZeroCrossings * Resolution + 2];
        var norm = BesselI0(beta);
        for (var i = 0; i < kernel.Length; i++) {
            var x = (double)i / Resolution;
            if (x >= ZeroCrossings) {
                continue;
            }

            var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var r = x / ZeroCrossings;
            kernel[i] = (float)(sinc * BesselI0(beta * Math.Sqrt(1 - r * r)) / norm);
        }

        return kernel;
    }

    static double BesselI0(double x) {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++) {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
            if (term < sum * 1e-17) {
                break;
            }
        }

        return sum;
    }
}
