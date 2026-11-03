namespace Anode.Dsp.Spatial;

/// <summary>
///     An object's position (the contract's <c>ObjectPanner</c>): azimuth and elevation in degrees, distance 0…1, size
///     0…1.
/// </summary>
public readonly record struct ObjectPosition(float Azimuth, float Elevation, float Distance, float Size);

/// <summary>
///     Vector base amplitude panning over a layout's triangulation (docs/adr/0012-spatial-audio.md, workstream D),
///     modelled on the EBU ADM Renderer's point-source panner (virtual speakers where the layout has gaps, the
///     triangulation of its convex hull, gains normalised for power), with size spread over neighbouring directions.
///     The LFE takes no object signal.
/// </summary>
public sealed class Vbap {
    /// <summary>Off the audio thread: triangulates <paramref name="layout" /> (the triplets and their inverse matrices).</summary>
    public Vbap(SpeakerLayout layout) {
        throw Pending.WorkstreamD();
    }

    public SpeakerLayout Layout => throw Pending.WorkstreamD();

    /// <summary>
    ///     The gain of each of the layout's channels for an object at <paramref name="position" />, power-normalised;
    ///     real-time safe.
    /// </summary>
    public void Gains(in ObjectPosition position, Span<float> gains) => throw Pending.WorkstreamD();
}

/// <summary>
///     Renders objects to the master's layout at the master (after the bed sum, before the master's inserts): each
///     object's mono or stereo signal through <see cref="Vbap" /> gains, ramped from the previous block's to this
///     block's position (the position automation delivers per block, sample-accurately at its events). Real-time safe
///     after <see cref="Prepare" />.
/// </summary>
public sealed class ObjectRenderer {
    /// <summary>The ADM profile's limit for a 7.1.4 bed (docs/11-phase-10.md).</summary>
    public const int MaxObjects = 118;

    /// <summary>Off the audio thread: the layout's VBAP and gain state for up to <paramref name="maxObjects" /> objects.</summary>
    public void Prepare(SpeakerLayout layout, int maxObjects, float sampleRate, int maxFrames) =>
        throw Pending.WorkstreamD();

    /// <summary>Object <paramref name="index" />'s position from the next block (or from <paramref name="frame" /> within it).</summary>
    public void SetPosition(int index, in ObjectPosition position, int frame = 0) => throw Pending.WorkstreamD();

    /// <summary>
    ///     Adds object <paramref name="index" />'s signal (one or two channels), rendered, into
    ///     <paramref name="output" />.
    /// </summary>
    public void Render(int index, PlanarBlock input, PlanarBlock output) => throw Pending.WorkstreamD();

    /// <summary>Forgets object <paramref name="index" />'s previous gains (it starts at its position, no ramp).</summary>
    public void Reset(int index) => throw Pending.WorkstreamD();
}
