namespace Anode.Dsp.Instruments.Alchemy;

/// <summary>
///     <c>anode.sample-alchemy</c> (workstream E): a loaded sample (the user's, or a generated default) resynthesised in
///     granular (position, size, density, spray, pitch per grain), additive (partials tracked from the sample, morphable
///     by number and spread) or spectral (a smoothed spectral envelope over noise and partials) mode, with ADSR and
///     filter.
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts (docs/12-phase-11.md): its id and name are fixed, its parameters and the rest
///     are workstream E's. Not in <see cref="BuiltinInstruments" /> until it works.
/// </remarks>
public sealed class SampleAlchemy : IInstrument {
    public const string Id = "anode.sample-alchemy";

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Sample Alchemy", "Anode", "1.0.0", [], [".wav", ".aif", ".aiff"]);

    public InstrumentDescriptor Descriptor => Info;

    public void Prepare(float sampleRate, int maxFrames) => throw Pending.WorkstreamE();

    public void SetParameter(int index, float value) => throw Pending.WorkstreamE();

    public float GetParameter(int index) => throw Pending.WorkstreamE();

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) =>
        throw Pending.WorkstreamE();

    public void Reset() => throw Pending.WorkstreamE();

    public bool LoadFile(string path) => throw Pending.WorkstreamE();

    public byte[]? SaveState() => throw Pending.WorkstreamE();

    public bool LoadState(ReadOnlySpan<byte> state) => throw Pending.WorkstreamE();

    public void Dispose() { }
}
