namespace Anode.Dsp.Instruments.Physical;

/// <summary>
///     <c>anode.physical-model</c>, the physical-modelling instrument (Sculpture-like; workstream E): an object (string,
///     bar, membrane, tube; material: stiffness, damping, inharmonicity) excited by up to three exciters (pick, bow,
///     strike, blow) at positions and read by two pickups, a body resonance, then filter, envelopes, LFOs and vibrato
///     as the T2 synths.
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts (docs/12-phase-11.md): its id and name are fixed, its parameters and the rest
///     are workstream E's. Not in <see cref="BuiltinInstruments" /> until it works.
/// </remarks>
public sealed class PhysicalModel : IInstrument {
    public const string Id = "anode.physical-model";

    public static InstrumentDescriptor Info { get; } = new(Id, "Physical Model", "Anode", "1.0.0", [], []);

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
