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
);

/// <summary>
///     The Surround Panner (docs/11-phase-10.md, workstream D), as Logic's: a mono or stereo input (each stereo channel
///     at the angle ± spread/2) panned into an output layout by angle and diversity (diversity 1: between the two
///     nearest speakers; 0: every non-LFE speaker equally, energy preserved), the centre speaker's share scaled by the
///     centre level, the elevation spreading into the upper layer, and a low-passed (120 Hz) copy at the LFE level into
///     the LFE. Real-time safe after <see cref="Prepare" />: <see cref="Process" /> allocates nothing and ramps gain
///     changes over the block.
/// </summary>
public sealed class SurroundPanner {
    /// <summary>Off the audio thread: sizes the gain tables and the LFE filter for this input and layout.</summary>
    public void Prepare(SpeakerLayout output, int inputChannels, float sampleRate, int maxFrames) =>
        throw Pending.WorkstreamD();

    /// <summary>New settings, taking effect over the next block.</summary>
    public void Set(in SurroundPannerSettings settings) => throw Pending.WorkstreamD();

    /// <summary>
    ///     The steady-state gains of input channel <paramref name="inputChannel" /> into each output speaker (the LFE's
    ///     before its filter), for tests and meters.
    /// </summary>
    public void Gains(int inputChannel, Span<float> gains) => throw Pending.WorkstreamD();

    /// <summary>Adds <paramref name="input" />, panned, into <paramref name="output" /> (the layout's channels).</summary>
    public void Process(PlanarBlock input, PlanarBlock output) => throw Pending.WorkstreamD();

    /// <summary>Clears the LFE filter's state and jumps to the settings (stop, locate).</summary>
    public void Reset() => throw Pending.WorkstreamD();
}
