using Anode.Ipc;

namespace Anode.Engine.Environment;

/// <summary>
///     An Environment object as the host runs it (workstream A): built on the control thread from its
///     <see cref="EngineEnvironmentObject" />, then fed events at their inlets on the audio thread, emitting at its
///     outlets. Objects that hold events (delay lines, the arpeggiator) emit them through
///     <see cref="IEnvironmentEmitter.Schedule" /> or when <see cref="Advance" /> reaches them.
/// </summary>
public abstract class EnvironmentNode(EngineEnvironmentObject settings) {
    public EngineEnvironmentObject Settings { get; } = settings;

    /// <summary>An event at inlet <paramref name="inlet" />.</summary>
    public abstract void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit);

    /// <summary>The block [<paramref name="blockStart" />, + <paramref name="frames" />) is about to run.</summary>
    public virtual void Advance(long blockStart, int frames, IEnvironmentEmitter emit) => throw Pending.WorkstreamA();

    public virtual void Reset() => throw Pending.WorkstreamA();

    /// <summary>The node for <paramref name="settings" />' kind.</summary>
    public static EnvironmentNode Create(EngineEnvironmentObject settings) =>
        settings.Kind switch {
            EngineEnvironmentKind.PhysicalInput => new PhysicalInputNode(settings),
            EngineEnvironmentKind.SequencerInput => new SequencerInputNode(settings),
            EngineEnvironmentKind.Keyboard => new KeyboardNode(settings),
            EngineEnvironmentKind.Monitor => new MonitorNode(settings),
            EngineEnvironmentKind.Fader => new FaderNode(settings),
            EngineEnvironmentKind.Transformer => new TransformerNode(settings),
            EngineEnvironmentKind.DelayLine => new DelayLineNode(settings),
            EngineEnvironmentKind.Arpeggiator => new ArpeggiatorNode(settings),
            EngineEnvironmentKind.ChordMemorizer => new ChordMemorizerNode(settings),
            EngineEnvironmentKind.ChannelSplitter => new ChannelSplitterNode(settings),
            EngineEnvironmentKind.MidiPort => new MidiPortNode(settings),
            EngineEnvironmentKind.TrackInput => new TrackInputNode(settings),
            EngineEnvironmentKind.ControlTarget => new ControlTargetNode(settings),
            _ => throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Kind,
                "Unknown Environment object kind."
            )
        };
}

/// <summary>Every physical input (or one device, <c>Port</c>): what enters the graph.</summary>
public sealed class PhysicalInputNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>The sequencer input: the record-enabled or selected track (<see cref="IEnvironmentSink.SequencerInput" />).</summary>
public sealed class SequencerInputNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>An on-screen keyboard: live MIDI into it (<see cref="EnvironmentGraph.InputAt" />) leaves by its outlet.</summary>
public sealed class KeyboardNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>Passes everything through and keeps the last events for its telemetry slot.</summary>
public sealed class MonitorNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>A MIDI value moved by its event kind and sent when moved; its value goes to its telemetry slot.</summary>
public sealed class FaderNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>Conditions and operations (Phase 7's Transform rules): matched events out of 0, the rest out of 1.</summary>
public sealed class TransformerNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>Repeats each event at its taps, scheduled in the graph's queue.</summary>
public sealed class DelayLineNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>Phase 6's Arpeggiator engine on the input path.</summary>
public sealed class ArpeggiatorNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>Phase 6's Chord Trigger engine on the input path.</summary>
public sealed class ChordMemorizerNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>Channel n out of outlet n − 1, everything else out of outlet 16.</summary>
public sealed class ChannelSplitterNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>To an external MIDI output (<see cref="IEnvironmentSink.MidiPort" />).</summary>
public sealed class MidiPortNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>To a track's input (<see cref="IEnvironmentSink.TrackInput" />).</summary>
public sealed class TrackInputNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}

/// <summary>To a strip parameter (<see cref="IEnvironmentSink.Control" />).</summary>
public sealed class ControlTargetNode(EngineEnvironmentObject settings) : EnvironmentNode(settings) {
    public override void Receive(int inlet, in EnvironmentEvent e, IEnvironmentEmitter emit) =>
        throw Pending.WorkstreamA();
}
