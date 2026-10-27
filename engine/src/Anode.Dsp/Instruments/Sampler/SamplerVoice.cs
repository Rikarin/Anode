using Anode.Dsp.Envelopes;
using System.Runtime.CompilerServices;

namespace Anode.Dsp.Instruments.Sampler;

/// <summary>The control-rate values every <see cref="SamplerVoice" /> reads, computed by <see cref="AnodeSampler" />.</summary>
internal sealed class SamplerControl {
    public double SampleRate;

    /// <summary>The pitch factor of the Tune parameter and the pitch bend.</summary>
    public double PitchRatio = 1;

    public float StealStep;
}

/// <summary>
///     One voice of <see cref="AnodeSampler" />: a zone's sample read with 4-point cubic (Catmull-Rom) interpolation,
///     looped as its loop mode says, through an AHDSR amp envelope, panned.
/// </summary>
internal sealed class SamplerVoice(SamplerControl control) : Voice {
    SamplerZone? zone;
    AdsrSettings settings;
    Adsr amp;
    double position;
    double baseIncrement;
    float gainLeft;
    float gainRight;
    float fade = 1;
    bool looping;

    /// <summary>The zone it plays (tests).</summary>
    public SamplerZone? Zone => zone;

    /// <summary>
    ///     Starts <paramref name="z" /> for <paramref name="note" />, the velocity curve scaled by
    ///     <paramref name="sensitivity" /> and the envelope times extended by the instrument's attack and release.
    /// </summary>
    public void Start(SamplerZone z, int note, int velocity, float sensitivity, double attack, double release) {
        Begin(note);
        zone = z;
        var r = z.Region;
        settings = AdsrSettings.From(
            r.Attack + attack,
            r.Hold,
            r.Decay,
            r.Sustain / 100,
            r.Release + release,
            control.SampleRate
        );
        amp.Reset();
        amp.Trigger();
        position = Math.Min(r.Offset, z.Sample.Frames);
        baseIncrement = z.Sample.SampleRate / control.SampleRate * Math.Pow(2, (note + z.Semitones) / 12);
        looping = z.Mode is LoopMode.Continuous or LoopMode.Sustain;
        fade = 1;

        var v = velocity / 127.0;
        var track = r.AmpVeltrack / 100 * sensitivity;
        var curve = track >= 0 ? 1 - track + track * v * v : 1 + track - track * (1 - v) * (1 - v);
        var gain = z.Gain * (float)curve;
        gainLeft = gain * z.GainLeft;
        gainRight = gain * z.GainRight;
    }

    protected override void OnRelease() {
        if (zone?.Mode == LoopMode.OneShot) {
            return;
        }

        if (zone?.Mode == LoopMode.Sustain) {
            looping = false;
        }

        amp.Release();
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        zone = null;
        fade = 1;
    }

    /// <summary>Adds the voice's next samples into the buffers.</summary>
    public void Render(Span<float> left, Span<float> right) {
        if (zone is null) {
            Kill();
            return;
        }

        var sample = zone.Sample;
        var l = sample.Left;
        var r = sample.Right;
        var frames = sample.Frames;
        var loopStart = zone.LoopStart;
        var loopEnd = zone.LoopEnd;
        var loopLength = loopEnd - loopStart + 1;
        var increment = baseIncrement * control.PitchRatio;
        var stereo = sample.Stereo;
        for (var i = 0; i < left.Length; i++) {
            var index = (long)position;
            var t = (float)(position - index);
            float outLeft, outRight;
            if (!looping || index + 2 <= loopEnd) {
                var at = (int)index + SampleBuffer.PadBefore;
                outLeft = Cubic(l[at - 1], l[at], l[at + 1], l[at + 2], t);
                outRight = stereo ? Cubic(r[at - 1], r[at], r[at + 1], r[at + 2], t) : outLeft;
            } else {
                // Near the loop's end: the points past it come from its start.
                var a = Wrap(index - 1, loopEnd, loopLength);
                var b = Wrap(index, loopEnd, loopLength);
                var c = Wrap(index + 1, loopEnd, loopLength);
                var d = Wrap(index + 2, loopEnd, loopLength);
                outLeft = Cubic(l[a], l[b], l[c], l[d], t);
                outRight = stereo ? Cubic(r[a], r[b], r[c], r[d], t) : outLeft;
            }

            var g = amp.Next(settings) * fade;
            left[i] += outLeft * g * gainLeft;
            right[i] += outRight * g * gainRight;

            position += increment;
            if (looping && position >= loopEnd + 1) {
                position -= loopLength * Math.Floor((position - loopEnd - 1) / loopLength + 1);
            }

            if (amp.IsIdle || (!looping && position >= frames) || (Stolen && (fade -= control.StealStep) <= 0)) {
                Kill();
                return;
            }
        }
    }

    /// <summary>The array index of frame <paramref name="index" />, with frames past the loop's end wrapped into it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Wrap(long index, long loopEnd, long loopLength) =>
        (int)(index > loopEnd ? index - loopLength : index) + SampleBuffer.PadBefore;

    /// <summary>
    ///     4-point, 3rd-order Hermite (Catmull-Rom) interpolation between <paramref name="x0" /> and
    ///     <paramref name="x1" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cubic(float xm1, float x0, float x1, float x2, float t) {
        var c1 = 0.5f * (x1 - xm1);
        var c2 = xm1 - 2.5f * x0 + 2 * x1 - 0.5f * x2;
        var c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }
}
