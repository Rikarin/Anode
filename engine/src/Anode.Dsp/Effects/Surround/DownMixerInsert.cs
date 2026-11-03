using Anode.Dsp.Spatial;

namespace Anode.Dsp.Effects.Surround;

/// <summary>
///     <c>anode.down-mixer</c>, the Down Mixer as an insert (docs/12-phase-11.md, workstream E): a surround strip to a
///     smaller layout, by Phase 10's <see cref="Downmixer" /> (ITU-R BS.775). Its <c>format</c> parameter is the target:
///     the index of a <see cref="SpeakerFormat" /> narrower than the input (stereo by default); its <c>lfe</c> parameter
///     the LFE's gain into the front pair when the target has no LFE (dB, −144 dropped, as BS.775).
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts. Not in <see cref="BuiltinEffects" /> (it is not an
///     <see cref="IAudioEffect" />): the engine creates it by id.
/// </remarks>
public sealed class DownMixerInsert : IFormatEffect {
    public const string Id = "anode.down-mixer";
    static readonly EffectParameters P = new();

    public static readonly int Format = P.Choice(
        "format",
        "Format",
        [..Enum.GetNames<SpeakerFormat>()],
        (int)SpeakerFormat.Stereo
    );

    public static readonly int Lfe = P.Add("lfe", "LFE", -144, 0, -144, "dB");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Down Mixer", EffectCategories.Utility);

    public EffectDescriptor Descriptor => Info;

    public SpeakerFormat OutputFormat(SpeakerFormat input) => throw Pending.WorkstreamE();

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
