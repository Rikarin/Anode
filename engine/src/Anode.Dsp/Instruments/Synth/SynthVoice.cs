using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Synth;

/// <summary>
///     The control-rate values every <see cref="SynthVoice" /> reads, computed by <see cref="AnodeSynth" /> on its
///     control grid from the smoothed parameters, the controllers and the LFO.
/// </summary>
internal sealed class SynthControl {
    public const int MaxUnison = 4;

    public double[] Ratio1 { get; } = new double[MaxUnison];
    public double[] Ratio2 { get; } = new double[MaxUnison];
    public float[] PanLeft { get; } = new float[MaxUnison];
    public float[] PanRight { get; } = new float[MaxUnison];
    public double SampleRate;
    public Waveform Wave1;
    public Waveform Wave2;
    public double Width1;
    public double Width2;
    public float Gain1;
    public float Gain2;
    public int Unison = 1;
    public FilterMode Mode;
    public double Quality;
    public double Cutoff;
    public double KeyTracking;
    public double EnvelopeOctaves;
    public AdsrSettings Amp;
    public AdsrSettings Filter;

    /// <summary>Pitch bend and LFO, in semitones.</summary>
    public double PitchModulation;

    /// <summary>LFO to the filter, in octaves.</summary>
    public double FilterModulation;

    /// <summary>LFO to the amplitude, a factor in 0 … 1.</summary>
    public float AmpModulation = 1;

    public float VelocitySensitivity;
    public float StealStep;
}

/// <summary>
///     One voice of <see cref="AnodeSynth" />: up to <see cref="SynthControl.MaxUnison" /> unison layers of two
///     oscillators, a multimode filter (one per side when the layers spread in stereo), and the amp and filter
///     envelopes. Pitch, filter coefficients and gain are updated every <see cref="PolyInstrument{T}.ControlInterval" />
///     samples counted from the voice's own start, so a note sounds the same wherever it falls in a buffer.
/// </summary>
internal sealed class SynthVoice(SynthControl control, int index) : Voice {
    const double ReferenceNote = 69;
    const double ReferenceFrequency = 440;
    const double MaxIncrement = 0.45;

    readonly double[] phase1 = new double[SynthControl.MaxUnison];
    readonly double[] phase2 = new double[SynthControl.MaxUnison];
    readonly double[] increment1 = new double[SynthControl.MaxUnison];
    readonly double[] increment2 = new double[SynthControl.MaxUnison];
    Adsr amp;
    Adsr filterEnvelope;
    MultimodeFilter filterLeft;
    MultimodeFilter filterRight;
    MultimodeCoefficients coefficients;
    double pitch;
    double glideStep;
    float velocityGain;
    float gain;
    float gainStep;
    float fade = 1;
    int untilTick;
    bool fresh;
    uint noise;

    /// <summary>The current amp envelope level (tests).</summary>
    public float AmpLevel => amp.Level;

    /// <summary>
    ///     Starts <paramref name="note" /> on an idle voice, gliding from <paramref name="from" /> over
    ///     <paramref name="glideSeconds" /> when that is a note and the time is not 0.
    /// </summary>
    public void Start(int note, int velocity, int from, double glideSeconds) {
        Begin(note);
        for (var k = 0; k < SynthControl.MaxUnison; k++) {
            // Layer 0 starts at phase 0, so a single voice renders the same every time; the others are spread so the
            // unison does not start in phase.
            phase1[k] = k * 0.381966011250105 % 1;
            phase2[k] = k * 0.618033988749895 % 1;
        }

        noise = (0x9E3779B9u * (uint)(index + 1)) | 1;
        amp.Reset();
        filterEnvelope.Reset();
        filterLeft.Reset();
        filterRight.Reset();
        fade = 1;
        fresh = true;
        untilTick = 0;
        Trigger(note, velocity, from, glideSeconds);
    }

    /// <summary>Plays <paramref name="note" /> again on this voice, from where its envelopes are.</summary>
    public void Retrigger(int note, int velocity, int from, double glideSeconds) {
        Begin(note);
        Trigger(note, velocity, from, glideSeconds);
    }

    void Trigger(int note, int velocity, int from, double glideSeconds) {
        var v = velocity / 127f;
        velocityGain = 1 - control.VelocitySensitivity + control.VelocitySensitivity * v * v;
        var glideTicks = glideSeconds * control.SampleRate / PolyInstrument<SynthVoice>.ControlInterval;
        if (from >= 0 && from != note && glideTicks >= 1) {
            pitch = from;
            glideStep = (note - from) / glideTicks;
        } else {
            pitch = note;
            glideStep = 0;
        }

        amp.Trigger();
        filterEnvelope.Trigger();
    }

    protected override void OnRelease() {
        amp.Release();
        filterEnvelope.Release();
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        filterEnvelope.Reset();
        filterLeft.Reset();
        filterRight.Reset();
        fade = 1;
    }

    /// <summary>Adds the voice's next samples into the buffers.</summary>
    public void Render(Span<float> left, Span<float> right) {
        var done = 0;
        while (done < left.Length && Active) {
            if (untilTick == 0) {
                Tick();
                untilTick = PolyInstrument<SynthVoice>.ControlInterval;
            }

            var count = Math.Min(untilTick, left.Length - done);
            var rendered = control.Unison == 1
                ? RenderMono(left.Slice(done, count), right.Slice(done, count))
                : RenderStereo(left.Slice(done, count), right.Slice(done, count));
            untilTick -= count;
            done += count;
            if (rendered < count) {
                Kill();
            }
        }
    }

    void Tick() {
        // On the voice's own grid, so the result does not depend on how the host splits time into buffers.
        filterLeft.FlushDenormals();
        filterRight.FlushDenormals();
        if (glideStep != 0) {
            pitch += glideStep;
            if (glideStep > 0 ? pitch >= Note : pitch <= Note) {
                pitch = Note;
                glideStep = 0;
            }
        }

        var c = control;
        var baseIncrement = ReferenceFrequency
            / c.SampleRate
            * Math.Pow(2, (pitch + c.PitchModulation - ReferenceNote) / 12);
        for (var k = 0; k < c.Unison; k++) {
            increment1[k] = Math.Min(baseIncrement * c.Ratio1[k], MaxIncrement);
            increment2[k] = Math.Min(baseIncrement * c.Ratio2[k], MaxIncrement);
        }

        var octaves = c.KeyTracking * (pitch - 60) / 12
            + c.EnvelopeOctaves * filterEnvelope.Level
            + c.FilterModulation;
        coefficients = MultimodeCoefficients.Design(c.Mode, c.SampleRate, c.Cutoff * Math.Pow(2, octaves), c.Quality);

        var target = velocityGain * c.AmpModulation;
        if (fresh) {
            gain = target;
            gainStep = 0;
            fresh = false;
        } else {
            gainStep = (target - gain) / PolyInstrument<SynthVoice>.ControlInterval;
        }
    }

    /// <summary>One layer, one filter, the same on both sides; the samples rendered before the voice ended.</summary>
    int RenderMono(Span<float> left, Span<float> right) {
        var c = control;
        var p1 = phase1[0];
        var p2 = phase2[0];
        var i1 = increment1[0];
        var i2 = increment2[0];
        for (var i = 0; i < left.Length; i++) {
            double x = 0;
            if (c.Gain1 != 0) {
                x += c.Gain1 * PolyBlep.Render(c.Wave1, p1, i1, c.Width1, ref noise);
            }

            if (c.Gain2 != 0) {
                x += c.Gain2 * PolyBlep.Render(c.Wave2, p2, i2, c.Width2, ref noise);
            }

            p1 += i1;
            if (p1 >= 1) {
                p1 -= 1;
            }

            p2 += i2;
            if (p2 >= 1) {
                p2 -= 1;
            }

            var y = (float)filterLeft.Tick(coefficients, x);
            filterEnvelope.Next(c.Filter);
            var output = y * amp.Next(c.Amp) * gain * fade;
            gain += gainStep;
            left[i] += output;
            right[i] += output;
            if (amp.IsIdle || (Stolen && (fade -= c.StealStep) <= 0)) {
                phase1[0] = p1;
                phase2[0] = p2;
                return i + 1;
            }
        }

        phase1[0] = p1;
        phase2[0] = p2;
        return left.Length;
    }

    /// <summary>Several layers spread across the stereo field, a filter per side.</summary>
    int RenderStereo(Span<float> left, Span<float> right) {
        var c = control;
        var layers = c.Unison;
        for (var i = 0; i < left.Length; i++) {
            double l = 0, r = 0;
            for (var k = 0; k < layers; k++) {
                double s = 0;
                if (c.Gain1 != 0) {
                    s += c.Gain1 * PolyBlep.Render(c.Wave1, phase1[k], increment1[k], c.Width1, ref noise);
                }

                if (c.Gain2 != 0) {
                    s += c.Gain2 * PolyBlep.Render(c.Wave2, phase2[k], increment2[k], c.Width2, ref noise);
                }

                phase1[k] += increment1[k];
                if (phase1[k] >= 1) {
                    phase1[k] -= 1;
                }

                phase2[k] += increment2[k];
                if (phase2[k] >= 1) {
                    phase2[k] -= 1;
                }

                l += s * c.PanLeft[k];
                r += s * c.PanRight[k];
            }

            filterEnvelope.Next(c.Filter);
            var g = amp.Next(c.Amp) * gain * fade;
            gain += gainStep;
            left[i] += (float)filterLeft.Tick(coefficients, l) * g;
            right[i] += (float)filterRight.Tick(coefficients, r) * g;
            if (amp.IsIdle || (Stolen && (fade -= c.StealStep) <= 0)) {
                return i + 1;
            }
        }

        return left.Length;
    }
}
