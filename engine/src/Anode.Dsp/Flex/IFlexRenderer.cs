namespace Anode.Dsp.Flex;

/// <summary>
///     Renders warped audio (docs/06-phase-5.md, "Flex algorithms"): one instance per playing lane, made by
///     <see cref="FlexRenderers.Create" /> and pooled by the engine.
///     <para>
///         Threading: construction and disposal on the engine's control thread; <see cref="Render" /> and
///         <see cref="Reset" /> on the disk thread, allocation-free. Never on the audio thread.
///     </para>
///     <para>
///         The rules every algorithm keeps: output frame n of a call is entry offset <c>offset + n</c>; a call whose
///         offset is not the previous call's end (or the first after <see cref="Reset" />) primes, reading from up to
///         <see cref="PrimingFrames" /> before it, so a locate costs disk-thread time, not a gap; after priming, output
///         does not depend on where a fill started beyond 1e-4. Offsets outside 0…<c>Length</c> render silence.
///     </para>
///     <para>
///         The algorithms: Polyphonic and Monophonic stretch at the warp's local rate with the pitch points' transpose
///         (Monophonic with a shorter block); Pitch as Polyphonic, with formant compensation; Slicing starts each slice
///         (between warp points) at its warped position at original speed, cut at the next slice with a 2 ms fade,
///         gaps silent; Rhythmic as Slicing, gaps filled by looping the slice's last 30 ms with crossfades; Speed
///         resamples (windowed sinc), so pitch follows speed; Tempophone is granular (60 ms grains, Hann crossfades).
///     </para>
/// </summary>
public interface IFlexRenderer : IDisposable {
    /// <summary>How many project samples before a seek's offset a render reads to prime (the stretcher's latency).</summary>
    int PrimingFrames { get; }

    /// <summary>
    ///     Renders <c>interleaved.Length / channels</c> frames (at most the <c>maxFrames</c> it was made for) of
    ///     <paramref name="region" /> from entry offset <paramref name="offset" /> into <paramref name="interleaved" />,
    ///     overwriting it, reading the file from <paramref name="source" />.
    /// </summary>
    void Render(in FlexRegion region, long offset, Span<float> interleaved, int channels, IFlexSource source);

    /// <summary>Forgets the stream: the next render primes.</summary>
    void Reset();
}
