using Anode.Dsp.Spatial;

namespace Anode.Dsp.Effects.Surround;

/// <summary>
///     <c>anode.binaural</c>, Binaural Post-Processing as an insert (docs/12-phase-11.md, workstream E): a surround strip
///     to binaural stereo through ADR-0012's renderer (Phase 10's <see cref="BinauralRenderer" />, the HRTF set the
///     monitoring uses). Its output is always stereo.
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts. Not in <see cref="BuiltinEffects" /> (it is not an
///     <see cref="IAudioEffect" />): the engine creates it by id.
/// </remarks>
public sealed class BinauralInsert : IFormatEffect {
    public const string Id = "anode.binaural";

    public static EffectDescriptor Info { get; } = new(
        Id,
        "Binaural Post-Processing",
        EffectParameters.Vendor,
        EffectParameters.Version,
        EffectCategories.Utility,
        [],
        false
    );

    public EffectDescriptor Descriptor => Info;

    /// <summary>On the control thread, before <see cref="Prepare" />: the HRTF set it renders with.</summary>
    public void UseHrtfs(HrtfSet hrtfs) => throw Pending.WorkstreamE();

    public SpeakerFormat OutputFormat(SpeakerFormat input) => SpeakerFormat.Stereo;

    public void Prepare(SpeakerLayout input, SpeakerLayout output, float sampleRate, int maxFrames) =>
        throw Pending.WorkstreamE();

    public void SetParameter(int index, float value) => throw Pending.WorkstreamE();

    public float GetParameter(int index) => throw Pending.WorkstreamE();

    public int LatencySamples => throw Pending.WorkstreamE();

    public void Process(PlanarBlock input, PlanarBlock output) => throw Pending.WorkstreamE();

    public void Reset() => throw Pending.WorkstreamE();

    public byte[]? SaveState() => throw Pending.WorkstreamE();

    public bool LoadState(ReadOnlySpan<byte> state) => throw Pending.WorkstreamE();

    public void Dispose() { }
}
