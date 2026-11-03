namespace Anode.Dsp.Instruments.Tape;

/// <summary>
///     <c>anode.tape-replay</c>, the tape-replay instrument (Mellotron-style; workstream E): each key plays its own
///     recording from its start for at most the tape's length (8 s), with wow and flutter, tape speed and the start
///     motor's time. It loads a tape set (<c>.antape</c>, the content builder's: strings, flutes, choir).
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts (docs/12-phase-11.md): its id and name are fixed, its parameters and the rest
///     are workstream E's. Not in <see cref="BuiltinInstruments" /> until it works.
/// </remarks>
public sealed class TapeReplay : IInstrument {
    public const string Id = "anode.tape-replay";

    public static InstrumentDescriptor Info { get; } = new(Id, "Tape Replay", "Anode", "1.0.0", [], [".antape"]);

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
