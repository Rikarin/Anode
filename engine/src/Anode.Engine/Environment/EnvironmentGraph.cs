using Anode.Dsp.Transport;
using Anode.Ipc;
using Anode.Ipc.Telemetry;

namespace Anode.Engine.Environment;

/// <summary>
///     A MIDI event in the Environment: its absolute engine sample (where it happens in the block), its bytes, and the
///     input it came from (a device's port id; null for live MIDI and on-screen objects).
/// </summary>
public readonly record struct EnvironmentEvent(long Sample, byte Status, byte Data1, byte Data2, string? Port = null);

/// <summary>
///     Where the graph's events leave it (the engine's MIDI input path implements it): the sequencer input (the
///     record-enabled or selected track, as the default path), a track's input, an external port, or a control target
///     (a strip parameter, by the IPC's automation addressing), each at the event's sample. On the audio thread.
/// </summary>
public interface IEnvironmentSink {
    void SequencerInput(in EnvironmentEvent e);

    void TrackInput(int trackId, in EnvironmentEvent e);

    void MidiPort(string port, in EnvironmentEvent e);

    /// <summary>Sets control target <paramref name="target" /> to <paramref name="value" /> (0…1 of its range).</summary>
    void Control(EngineAutomationTarget target, float value, long sample);
}

/// <summary>
///     What an object emits into: the graph's routes from its outlets, at once or (delays, the arpeggiator) scheduled
///     later in the graph's bounded queue. <see cref="Schedule" /> is false when the queue is full; the event is then
///     dropped and counted.
/// </summary>
public interface IEnvironmentEmitter {
    void Emit(int outlet, in EnvironmentEvent e);

    bool Schedule(int outlet, in EnvironmentEvent e);
}

/// <summary>
///     The Environment as the host runs it (docs/adr/0015-environment.md; workstream A): the compiled objects of an
///     <see cref="EngineEnvironment" /> in route order, on the MIDI input path. Built and prepared on the control thread
///     (<see cref="Compile" />, which allocates every queue), swapped in whole; <see cref="Input" />,
///     <see cref="InputAt" /> and <see cref="Process" /> run on the audio thread without allocating. The empty graph
///     (<see cref="Empty" />) costs nothing: the engine passes input straight to the sequencer input without it.
/// </summary>
public sealed class EnvironmentGraph {
    /// <summary>The most events the graph's queue holds (scheduled delays and arpeggios included).</summary>
    public const int QueueCapacity = 4096;

    EnvironmentGraph() { }

    /// <summary>The default input path: physical input to sequencer input.</summary>
    public static EnvironmentGraph Empty { get; } = new();

    public bool IsEmpty => this == Empty;

    /// <summary>Events dropped since it was compiled (the queue was full, or a schedule fell past its bound).</summary>
    public long Dropped => throw Pending.WorkstreamA();

    /// <summary>On the control thread: the graph of <paramref name="environment" /> (null: <see cref="Empty" />).</summary>
    public static EnvironmentGraph Compile(EngineEnvironment? environment, float sampleRate, int maxFrames) =>
        environment is null || environment.Objects.Count == 0 ? Empty : throw Pending.WorkstreamA();

    /// <summary>On the audio thread: an event from a physical input (a device, live MIDI, a control surface).</summary>
    public void Input(in EnvironmentEvent e) => throw Pending.WorkstreamA();

    /// <summary>On the audio thread: live MIDI into object <paramref name="objectIndex" /> (an on-screen keyboard or fader).</summary>
    public void InputAt(int objectIndex, in EnvironmentEvent e) => throw Pending.WorkstreamA();

    /// <summary>
    ///     On the audio thread, once per block: runs what is due in [<paramref name="blockStart" />,
    ///     <paramref name="blockStart" /> + <paramref name="frames" />) through the objects, in sample order, into
    ///     <paramref name="sink" />.
    /// </summary>
    public void Process(long blockStart, int frames, in TransportContext transport, IEnvironmentSink sink) =>
        throw Pending.WorkstreamA();

    /// <summary>On the control thread: writes the monitors' and faders' telemetry slots.</summary>
    public void Publish(Span<EnvironmentMonitorEntry> monitors, Span<EnvironmentFaderEntry> faders) =>
        throw Pending.WorkstreamA();

    /// <summary>Releases held notes and clears every queue (stop, locate, a graph swapped out).</summary>
    public void Reset() => throw Pending.WorkstreamA();
}
