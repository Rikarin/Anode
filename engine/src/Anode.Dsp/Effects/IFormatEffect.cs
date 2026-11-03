using Anode.Dsp.Spatial;

namespace Anode.Dsp.Effects;

/// <summary>
///     Phase 11 (docs/12-phase-11.md): an insert that changes its strip's channel format (the Down Mixer, Binaural
///     Post-Processing): planar multichannel in, in its strip's format at that slot, and a different format out, which
///     the rest of the strip, its sends and its output carry (the strip's later inserts run in the new format). The
///     format after it follows from its parameters (<see cref="OutputFormat" />); a change of that format is a
///     structural change the server compiles (it is not automatable). Threading as <see cref="IAudioEffect" />.
/// </summary>
public interface IFormatEffect : IDisposable {
    EffectDescriptor Descriptor { get; }

    /// <summary>The format it outputs for <paramref name="input" /> at its current parameters.</summary>
    SpeakerFormat OutputFormat(SpeakerFormat input);

    /// <summary>Allocates everything processing needs, from <paramref name="input" /> to <paramref name="output" />.</summary>
    void Prepare(SpeakerLayout input, SpeakerLayout output, float sampleRate, int maxFrames);

    void SetParameter(int index, float value);

    float GetParameter(int index);

    /// <summary>The delay it adds, in samples (for delay compensation).</summary>
    int LatencySamples { get; }

    /// <summary>
    ///     Processes <c>input.Frames</c> frames of the prepared input layout into <paramref name="output" /> (the
    ///     prepared output layout, overwritten).
    /// </summary>
    void Process(PlanarBlock input, PlanarBlock output);

    void Reset();

    byte[]? SaveState();

    bool LoadState(ReadOnlySpan<byte> state);
}
