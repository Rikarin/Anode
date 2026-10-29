using Anode.Dsp.Instruments;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Effects;

/// <summary>
///     What the built-in effects share: the parameters (clamped and snapped by a <see cref="ParameterBank" />), their
///     smoothing, the control-rate grid, the state blob and the <see cref="IAudioEffect.Reading" />.
/// </summary>
/// <remarks>
///     <para>
///         The render loop splits each buffer at a fixed grid of <see cref="ControlInterval" /> samples counted from
///         <see cref="Reset" />. At each grid point every continuous parameter moves a step toward its target (a one-pole
///         with a <see cref="SmoothingSeconds" /> time constant; discrete ones jump) and the effect recomputes what it
///         derives from them (<see cref="Update" />: coefficients, delay times). So the output does not depend on how
///         the host divides time into buffers, and the work per sample stays free of parameter maths. An effect that
///         applies a smoothed value as a gain ramps it per sample across the interval (<see cref="GainRamp" />).
///     </para>
///     <para>
///         The first grid point after <see cref="Prepare" />, <see cref="Reset" /> or <see cref="LoadState" /> takes
///         the parameters as they are rather than gliding to them. An effect that is not prepared leaves the audio as it
///         is. Every <see cref="Render" /> call is at most <see cref="ControlInterval" /> frames long, so scratch
///         buffers need no more than that, whatever the host's buffer size.
///     </para>
///     <para>
///         The transport (Phase 6): <see cref="SetTransport" /> keeps the block's <see cref="TransportContext" />.
///         Tempo-synced effects read the tempo from it (<see cref="HostTempo" />) and, while it plays, their position
///         in quarter notes (<see cref="QuarterAt" />). Until an engine passes one, they keep their own <c>tempo</c>
///         parameter and a free-running clock that advances at that tempo on the control grid, so the output still
///         does not depend on the buffer size. A context the engine does not renew advances by itself with each
///         <see cref="Process" /> call.
///     </para>
/// </remarks>
public abstract class AudioEffect : IAudioEffect {
    /// <summary>The control-rate grid, in samples.</summary>
    public const int ControlInterval = 32;

    /// <summary>
    ///     How far ahead of a grid line (in quarter notes) a position counts as on it: a synced step whose boundary is an
    ///     exact sample starts on that sample, however the position was rounded.
    /// </summary>
    public const double GridEpsilon = 1e-9;

    /// <summary>The time constant of the parameter smoothing.</summary>
    public const double SmoothingSeconds = 0.02;

    /// <summary>What a level reads in silence, instead of minus infinity.</summary>
    public const float FloorDb = -120f;

    readonly ParameterBank parameters;
    readonly float[] smoothed;
    readonly bool[] continuous;
    float smoothing = 1;
    int untilControl;
    bool snap = true;
    float reading = float.NaN;
    TransportContext transport;
    bool transported;
    int renderStart;
    double clock;
    double clockFrom;
    double clockStep;
    long clockIntervals;

    protected AudioEffect(EffectDescriptor descriptor) {
        Descriptor = descriptor;
        parameters = new(descriptor.Parameters);
        smoothed = new float[descriptor.Parameters.Count];
        continuous = new bool[descriptor.Parameters.Count];
        for (var i = 0; i < smoothed.Length; i++) {
            smoothed[i] = parameters[i];
            continuous[i] = descriptor.Parameters[i].Steps == 0;
        }
    }

    public EffectDescriptor Descriptor { get; }

    public float SampleRate { get; private set; }

    public bool Prepared => SampleRate > 0;

    public virtual int LatencySamples => 0;

    public float Reading => Volatile.Read(ref reading);

    /// <summary>
    ///     Where the current <see cref="Render" /> call starts within its control interval (0 …
    ///     <see cref="ControlInterval" /> − 1), for values interpolated across the interval.
    /// </summary>
    protected int ControlOffset { get; private set; }

    public void Prepare(float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        SampleRate = sampleRate;
        smoothing = (float)(1 - Math.Exp(-ControlInterval / (SmoothingSeconds * sampleRate)));
        OnPrepare(sampleRate);
        Reset();
    }

    public void SetParameter(int index, float value) => parameters.Set(index, value);

    public float GetParameter(int index) => parameters.Get(index);

    public void Process(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        if (!Prepared) {
            return;
        }

        var frames = Math.Min(left.Length, right.Length);
        var side = Descriptor.HasSidechain && sidechainLeft.Length >= frames && sidechainRight.Length >= frames;
        for (var at = 0; at < frames;) {
            renderStart = at;
            if (untilControl == 0) {
                ControlOffset = 0;
                Control();
                untilControl = ControlInterval;
            }

            var count = Math.Min(untilControl, frames - at);
            ControlOffset = ControlInterval - untilControl;
            Render(
                left.Slice(at, count),
                right.Slice(at, count),
                side ? sidechainLeft.Slice(at, count) : default,
                side ? sidechainRight.Slice(at, count) : default
            );
            untilControl -= count;
            at += count;
        }

        Finish();
        if (transported) {
            transport = transport with {
                Sample = transport.Sample + frames, Quarter = transport.Quarter + frames * transport.QuartersPerSample
            };
        }
    }

    /// <summary>Keeps the block's transport (see the remarks).</summary>
    public void SetTransport(in TransportContext transport) {
        this.transport = transport;
        transported = transport.SampleRate > 0 && transport.Bpm > 0;
    }

    public void Reset() {
        snap = true;
        untilControl = 0;
        ControlOffset = 0;
        clock = 0;
        clockFrom = 0;
        clockStep = 0;
        clockIntervals = 0;
        OnReset();
    }

    public virtual byte[]? SaveState() => InstrumentState.Write(Descriptor.Id, null, Descriptor.Parameters, parameters);

    public virtual bool LoadState(ReadOnlySpan<byte> state) {
        if (!InstrumentState.TryRead(state, Descriptor.Id, out _, out var values)) {
            return false;
        }

        InstrumentState.Apply(values, parameters);
        Reset();
        return true;
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }

    /// <summary>The parameters as set (for an effect that writes its own state).</summary>
    private protected ParameterBank Bank => parameters;

    /// <summary>The smoothed value of parameter <paramref name="index" /> (what <see cref="Update" /> should use).</summary>
    protected float Value(int index) => smoothed[index];

    /// <summary>A discrete parameter's value as an integer step.</summary>
    protected int Choice(int index) => (int)MathF.Round(smoothed[index]);

    protected bool Switch(int index) => smoothed[index] >= 0.5f;

    /// <summary>Whether the parameters jump to their values at this grid point (the first after a reset).</summary>
    protected bool Snapping { get; private set; }

    protected void Publish(float value) => Volatile.Write(ref reading, value);

    /// <summary>Whether an engine passes the transport (<see cref="SetTransport" /> has been called).</summary>
    protected bool Transported => transported;

    /// <summary>The block's transport; meaningful when <see cref="Transported" />.</summary>
    protected ref readonly TransportContext Transport => ref transport;

    /// <summary>Whether the transport plays (false until an engine passes one).</summary>
    protected bool Playing => transported && transport.Playing;

    /// <summary>The transport's tempo when an engine passes one, else <paramref name="fallback" /> (a parameter).</summary>
    protected double HostTempo(double fallback) => transported ? transport.Bpm : fallback;

    /// <summary>
    ///     The tempo the free-running clock follows while the transport does not play: by default
    ///     <see cref="HostTempo" /> of 120 bpm. An effect with a <c>tempo</c> parameter returns
    ///     <c>HostTempo(Value(Tempo))</c>.
    /// </summary>
    protected virtual double ClockTempo => HostTempo(120);

    /// <summary>
    ///     The position, in quarter notes, of frame <paramref name="frame" /> of the current <see cref="Render" /> call:
    ///     the transport's while it plays, else the free-running clock's (which counts from <see cref="Reset" />).
    /// </summary>
    protected double QuarterAt(int frame) =>
        Playing
            ? transport.Quarter + (renderStart + frame) * transport.QuartersPerSample
            : clock + (ControlOffset + frame) * clockStep;

    /// <summary>Quarter notes per sample now (the transport's or the clock's).</summary>
    protected double QuartersPerSample => Playing ? transport.QuartersPerSample : clockStep;

    /// <summary>Control thread: allocates for <paramref name="sampleRate" />.</summary>
    protected abstract void OnPrepare(float sampleRate);

    /// <summary>Clears tails and history.</summary>
    protected abstract void OnReset();

    /// <summary>At each grid point, after the parameters have moved: recomputes what derives from them.</summary>
    protected abstract void Update();

    /// <summary>At most <see cref="ControlInterval" /> frames in place; the sidechain is empty when there is none.</summary>
    protected abstract void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    );

    /// <summary>After each <see cref="Process" /> call: publish readings, flush denormal state.</summary>
    protected virtual void Finish() { }

    void Control() {
        Snapping = snap;
        for (var i = 0; i < smoothed.Length; i++) {
            var target = parameters[i];
            if (snap || !continuous[i]) {
                smoothed[i] = target;
                continue;
            }

            var next = smoothed[i] + (target - smoothed[i]) * smoothing;
            // Settle exactly once within a millionth of the range, so derived values stop being recomputed.
            var p = Descriptor.Parameters[i];
            smoothed[i] = MathF.Abs(target - next) <= (p.Max - p.Min) * 1e-6f ? target : next;
        }

        // The free clock: one control interval on at the previous step, then the step at the current tempo. It counts
        // intervals from where the step last changed rather than summing them, so a steady tempo does not drift. While
        // the transport plays it follows the transport, so stopping does not jump.
        var step = Math.Max(ClockTempo, 1) / (60.0 * SampleRate);
        if (Playing || snap || step != clockStep) {
            clockFrom = Playing ? transport.Quarter + renderStart * transport.QuartersPerSample
                : snap ? 0
                : clock + ControlInterval * clockStep;
            clockIntervals = 0;
            clockStep = step;
        } else {
            clockIntervals++;
        }

        clock = clockFrom + clockIntervals * (ControlInterval * clockStep);
        snap = false;
        Update();
    }
}

/// <summary>
///     A gain applied per sample that moves linearly to a new value across one control interval, so a smoothed gain
///     changes without zipper steps.
/// </summary>
public struct GainRamp {
    float value;
    float target;
    float step;

    public readonly float Current => value;

    /// <summary>Starts a ramp to <paramref name="next" />; with <paramref name="jump" />, goes there at once.</summary>
    public void Set(float next, bool jump) {
        target = next;
        if (jump) {
            value = next;
            step = 0;
        } else {
            step = (next - value) / AudioEffect.ControlInterval;
        }
    }

    public float Next() {
        if (step != 0) {
            value += step;
            if ((step > 0 && value >= target) || (step < 0 && value <= target)) {
                value = target;
                step = 0;
            }
        }

        return value;
    }
}
