namespace Anode.Dsp.Spatial;

/// <summary>
///     The Surround Panner's settings in engine units (the contract's <c>SurroundPanner</c>): angle and elevation in
///     degrees, diversity 0…1, the centre and LFE levels as linear gains, the stereo spread in degrees.
/// </summary>
public readonly record struct SurroundPannerSettings(
    float Angle,
    float Diversity,
    float Elevation,
    float CentreGain,
    float LfeGain,
    float Spread
) {
    /// <summary>The contract's default: front centre as a point, centre at 0 dB, no LFE, a stereo input at ±30°.</summary>
    public static SurroundPannerSettings Default { get; } = new(0, 1, 0, 1, 0, 60);
}

/// <summary>
///     The Surround Panner (docs/11-phase-10.md, workstream D), as Logic's: a mono or stereo input (each stereo channel
///     at the angle ± spread/2) panned into an output layout by angle and diversity (diversity 1: between the two
///     nearest speakers; 0: every non-LFE speaker equally, energy preserved), the centre speaker's share scaled by the
///     centre level, the elevation spreading into the upper layer, and a low-passed (120 Hz) copy at the LFE level into
///     the LFE. Real-time safe after <see cref="Prepare" />: <see cref="Process" /> allocates nothing and ramps gain
///     changes over the block.
/// </summary>
/// <remarks>
///     The gains (docs/spatial/README.md): a point P at the angle is a constant-power pair pan (cos/sin of the fraction
///     of the angle between the two neighbouring ear-layer speakers); with a centre level c below 1, P² = c²·P²(with the
///     centre) + (1 − c²)·P²(without it), so −144 dB leaves a phantom centre. Elevation e (layouts with height) moves the
///     point into the upper layer: ear·cos(πt/2) and upper·sin(πt/2) with t = e/30 up to the layer's 30°, then above
///     30° the upper share spreads towards all upper speakers equally at 90°. Diversity d mixes in power with the
///     all-speaker spread A (every non-LFE speaker, the centre weighted by c²): G² = d·P² + (1 − d)·A², so every setting
///     has unit power. The LFE takes each input at LFE level / √inputs, through a 4th-order Butterworth low-pass at
///     120 Hz. Changes ramp linearly per sample over one block, from the gains at its start to the new ones.
/// </remarks>
public sealed class SurroundPanner {
    const float LfeCutoff = 120;
    SpeakerLayout? layout;
    int inputs;
    int outputs;
    int lfe = -1;
    int centre = -1;
    int[] ring = [];
    double[] ringAzimuths = [];
    int[] ringNoCentre = [];
    double[] ringNoCentreAzimuths = [];
    int[] upper = [];
    double[] upperAzimuths = [];
    float[] current = [];
    float[] target = [];
    float[] lfeBuffer = [];
    readonly Biquad[] lowPass = [new(), new()];
    SurroundPannerSettings settings = SurroundPannerSettings.Default;
    bool jump = true;

    /// <summary>Off the audio thread: sizes the gain tables and the LFE filter for this input and layout.</summary>
    public void Prepare(SpeakerLayout output, int inputChannels, float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputChannels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputChannels, 2);
        layout = output;
        inputs = inputChannels;
        outputs = output.ChannelCount;
        lfe = output.LfeIndex;
        centre = -1;
        var ear = new List<int>();
        var top = new List<int>();
        for (var c = 0; c < outputs; c++) {
            if (c == lfe) {
                continue;
            }

            if (output.Elevations[c] > 0) {
                top.Add(c);
            } else {
                ear.Add(c);
                if (output.Label(c) == "M+000") {
                    centre = c;
                }
            }
        }

        (ring, ringAzimuths) = Sorted(output, ear);
        (ringNoCentre, ringNoCentreAzimuths) = Sorted(output, [..ear.Where(c => c != centre)]);
        (upper, upperAzimuths) = Sorted(output, top);
        current = new float[inputs * outputs];
        target = new float[inputs * outputs];
        lfeBuffer = new float[maxFrames];
        lowPass[0].SetLowPass(sampleRate, LfeCutoff, 0.54119610f);
        lowPass[1].SetLowPass(sampleRate, LfeCutoff, 1.3065630f);
        Compute();
        jump = true;
    }

    static (int[], double[]) Sorted(SpeakerLayout layout, IEnumerable<int> channels) {
        var sorted = channels.OrderBy(c => layout.Azimuths[c]).ToArray();
        return (sorted, [..sorted.Select(c => (double)layout.Azimuths[c])]);
    }

    /// <summary>New settings, taking effect over the next block.</summary>
    public void Set(in SurroundPannerSettings settings) {
        this.settings = settings;
        if (layout is not null) {
            Compute();
        }
    }

    /// <summary>
    ///     The steady-state gains of input channel <paramref name="inputChannel" /> into each output speaker (the LFE's
    ///     before its filter), for tests and meters.
    /// </summary>
    public void Gains(int inputChannel, Span<float> gains) =>
        target.AsSpan(inputChannel * outputs, outputs).CopyTo(gains);

    /// <summary>Adds <paramref name="input" />, panned, into <paramref name="output" /> (the layout's channels).</summary>
    public void Process(PlanarBlock input, PlanarBlock output) {
        var frames = Math.Min(input.Frames, output.Frames);
        if (jump) {
            target.CopyTo(current.AsSpan());
            jump = false;
        }

        var inverse = 1f / frames;
        for (var i = 0; i < inputs; i++) {
            var source = input.Channel(Math.Min(i, input.Channels - 1));
            for (var o = 0; o < outputs; o++) {
                if (o == lfe) {
                    continue;
                }

                var g0 = current[i * outputs + o];
                var g1 = target[i * outputs + o];
                if (g0 == 0 && g1 == 0) {
                    continue;
                }

                var destination = output.Channel(o);
                if (g0 == g1) {
                    for (var n = 0; n < frames; n++) {
                        destination[n] += g0 * source[n];
                    }
                } else {
                    var step = (g1 - g0) * inverse;
                    for (var n = 0; n < frames; n++) {
                        destination[n] += (g0 + step * (n + 1)) * source[n];
                    }
                }
            }
        }

        if (lfe >= 0) {
            var send = lfeBuffer.AsSpan(0, frames);
            send.Clear();
            var any = false;
            for (var i = 0; i < inputs; i++) {
                var g0 = current[i * outputs + lfe];
                var g1 = target[i * outputs + lfe];
                if (g0 == 0 && g1 == 0) {
                    continue;
                }

                any = true;
                var source = input.Channel(Math.Min(i, input.Channels - 1));
                var step = (g1 - g0) * inverse;
                for (var n = 0; n < frames; n++) {
                    send[n] += (g0 + step * (n + 1)) * source[n];
                }
            }

            // The filter runs while the send is silent too, so its tail rings out.
            if (any || !lowPass[1].IsQuiet) {
                lowPass[0].Process(send);
                lowPass[1].Process(send);
                var destination = output.Channel(lfe);
                for (var n = 0; n < frames; n++) {
                    destination[n] += send[n];
                }
            }
        }

        target.CopyTo(current.AsSpan());
    }

    /// <summary>Clears the LFE filter's state and jumps to the settings (stop, locate).</summary>
    public void Reset() {
        lowPass[0].Clear();
        lowPass[1].Clear();
        jump = true;
    }

    void Compute() {
        Span<double> gains = stackalloc double[outputs];
        for (var i = 0; i < inputs; i++) {
            var side = i == 0 ? 0.5 : -0.5;
            var angle = inputs == 1 ? settings.Angle : settings.Angle + side * settings.Spread;
            Pan(angle, gains);
            var row = target.AsSpan(i * outputs, outputs);
            for (var o = 0; o < outputs; o++) {
                row[o] = (float)gains[o];
            }

            if (lfe >= 0) {
                row[lfe] = settings.LfeGain / MathF.Sqrt(inputs);
            }
        }
    }

    /// <summary>The power-normalised gains of one input at <paramref name="angle" /> (the LFE left at 0).</summary>
    void Pan(double angle, Span<double> gains) {
        gains.Clear();
        var c = Math.Clamp(settings.CentreGain, 0, 1);
        var d = Math.Clamp(settings.Diversity, 0, 1);
        var hasHeight = upper.Length > 0;
        var elevation = hasHeight ? Math.Clamp(settings.Elevation, 0, 90) : 0;
        Span<double> pair = stackalloc double[outputs];

        // The point, in power: the ear layer (with and without the centre), then the upper layer.
        var t = Math.Min(elevation / 30, 1);
        var earShare = Math.Cos(t * Math.PI / 2);
        var upperShare = 1 - earShare * earShare;
        earShare *= earShare;
        if (centre >= 0 && c < 1 && ringNoCentre.Length > 0) {
            PairPan(ring, ringAzimuths, angle, pair);
            Accumulate(gains, pair, earShare * c * c);
            PairPan(ringNoCentre, ringNoCentreAzimuths, angle, pair);
            Accumulate(gains, pair, earShare * (1 - c * c));
        } else {
            PairPan(ring, ringAzimuths, angle, pair);
            Accumulate(gains, pair, earShare);
        }

        if (upperShare > 0) {
            var spread = Math.Clamp((elevation - 30) / 60, 0, 1);
            PairPan(upper, upperAzimuths, angle, pair);
            Accumulate(gains, pair, upperShare * (1 - spread));
            for (var u = 0; u < upper.Length; u++) {
                gains[upper[u]] += upperShare * spread / upper.Length;
            }
        }

        // Diversity: towards every non-LFE speaker equally (the centre at its level).
        if (d < 1) {
            var centreWeight = ringNoCentre.Length > 0 || upper.Length > 0 ? c * c : 1;
            var total = 0.0;
            for (var o = 0; o < outputs; o++) {
                total += o == lfe ? 0 : o == centre ? centreWeight : 1;
            }

            for (var o = 0; o < outputs; o++) {
                var share = o == lfe ? 0 : o == centre ? centreWeight : 1;
                gains[o] = d * gains[o] + (1 - d) * share / total;
            }
        }

        for (var o = 0; o < outputs; o++) {
            gains[o] = Math.Sqrt(Math.Max(0, gains[o]));
        }
    }

    static void Accumulate(Span<double> power, ReadOnlySpan<double> gains, double weight) {
        if (weight <= 0) {
            return;
        }

        for (var o = 0; o < power.Length; o++) {
            power[o] += weight * gains[o] * gains[o];
        }
    }

    /// <summary>
    ///     A constant-power pan between the two speakers of <paramref name="speakers" /> (sorted by azimuth) either side
    ///     of <paramref name="angle" />, going round the circle.
    /// </summary>
    static void PairPan(int[] speakers, double[] azimuths, double angle, Span<double> gains) {
        gains.Clear();
        if (speakers.Length == 0) {
            return;
        }

        if (speakers.Length == 1) {
            gains[speakers[0]] = 1;
            return;
        }

        var a = Wrap(angle);
        var n = speakers.Length;
        // The pair (k, k + 1) with azimuths[k] ≤ a < azimuths[k + 1], the last pair wrapping from the highest to the lowest.
        var k = n - 1;
        for (var i = 0; i < n - 1; i++) {
            if (a >= azimuths[i] && a < azimuths[i + 1]) {
                k = i;
                break;
            }
        }

        var low = azimuths[k];
        var high = k == n - 1 ? azimuths[0] + 360 : azimuths[k + 1];
        var at = a < low ? a + 360 : a;
        var fraction = Math.Clamp((at - low) / (high - low), 0, 1);
        gains[speakers[k]] = Math.Cos(fraction * Math.PI / 2);
        gains[speakers[(k + 1) % n]] += Math.Sin(fraction * Math.PI / 2);
    }

    static double Wrap(double angle) {
        var a = (angle + 180) % 360;
        if (a < 0) {
            a += 360;
        }

        return a - 180;
    }

    /// <summary>A transposed direct form II biquad (RBJ low-pass) for the LFE send.</summary>
    sealed class Biquad {
        float b0, b1, b2, a1, a2, z1, z2;

        public bool IsQuiet => Math.Abs(z1) < 1e-12f && Math.Abs(z2) < 1e-12f;

        public void SetLowPass(float sampleRate, float frequency, float q) {
            var w = 2 * Math.PI * frequency / sampleRate;
            var alpha = Math.Sin(w) / (2 * q);
            var cos = Math.Cos(w);
            var a0 = 1 + alpha;
            b0 = (float)((1 - cos) / 2 / a0);
            b1 = (float)((1 - cos) / a0);
            b2 = b0;
            a1 = (float)(-2 * cos / a0);
            a2 = (float)((1 - alpha) / a0);
            Clear();
        }

        public void Clear() => (z1, z2) = (0, 0);

        public void Process(Span<float> samples) {
            for (var n = 0; n < samples.Length; n++) {
                var x = samples[n];
                var y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                samples[n] = y;
            }
        }
    }
}
