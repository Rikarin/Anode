namespace Anode.Dsp.Spatial;

/// <summary>
///     A set of head-related impulse responses (SADIE II, converted offline from SOFA into Anode's compact file:
///     docs/adr/0012-spatial-audio.md): measured directions (azimuth, elevation in degrees) with a left and right impulse
///     response each, at one sample rate. Loaded off the audio thread; immutable.
/// </summary>
public sealed class HrtfSet {
    /// <summary>Reads Anode's HRTF file (<c>.anhrtf</c>); a file that is not one throws <see cref="InvalidDataException" />.</summary>
    public static HrtfSet Load(string path) => throw Pending.WorkstreamD();

    public int SampleRate => throw Pending.WorkstreamD();

    /// <summary>Taps of each impulse response.</summary>
    public int Length => throw Pending.WorkstreamD();

    public int DirectionCount => throw Pending.WorkstreamD();

    /// <summary>
    ///     The left and right impulse responses for a direction, interpolated between the nearest measured ones (for
    ///     objects), resampled to the set's rate; off the audio thread.
    /// </summary>
    public void ImpulseResponses(float azimuth, float elevation, Span<float> left, Span<float> right) =>
        throw Pending.WorkstreamD();
}

/// <summary>
///     Binaural monitoring (docs/11-phase-10.md, workstream D): each virtual speaker of a layout (and, optionally,
///     objects directly) convolved with its HRTF pair by uniformly partitioned FFT convolution, summed to two ears;
///     the LFE goes to both ears unfiltered. Real-time safe after <see cref="Prepare" />; its latency is reported for
///     the monitoring path only (never in bounces).
/// </summary>
public sealed class BinauralRenderer {
    /// <summary>Off the audio thread: the HRTF pairs of <paramref name="layout" />'s speakers, partitioned for the block size.</summary>
    public void Prepare(HrtfSet hrtfs, SpeakerLayout layout, float sampleRate, int maxFrames) =>
        throw Pending.WorkstreamD();

    /// <summary>The added delay in samples (0 for a partition size equal to the block).</summary>
    public int LatencySamples => throw Pending.WorkstreamD();

    /// <summary>
    ///     Renders the layout's channels in <paramref name="speakers" /> to <paramref name="left" /> and
    ///     <paramref name="right" />.
    /// </summary>
    public void Process(PlanarBlock speakers, Span<float> left, Span<float> right) => throw Pending.WorkstreamD();

    /// <summary>Clears the convolution history (stop, locate).</summary>
    public void Reset() => throw Pending.WorkstreamD();
}
