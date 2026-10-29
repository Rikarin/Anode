using Anode.Contracts;
using Anode.Model.Groove;
using Anode.Model.Time;
using Anode.Model.Transform;

namespace Anode.Model;

/// <summary>
///     The MIDI algorithms edits and the compiler use (docs/08-phase-7.md): the MIDI Transform engine and groove
///     templates (<see cref="TransformEngine" />, <see cref="Grooves" />, workstream E). The seam lets tests run the
///     model's commands against small fakes where those algorithms are not there yet; production uses
///     <see cref="StandardMidiAlgorithms" />.
/// </summary>
public interface IMidiAlgorithms {
    /// <summary><see cref="TransformEngine.Apply" />.</summary>
    TransformResult Transform(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent> controls,
        TransformSet set,
        SignatureMap signatures
    );

    /// <summary><see cref="TransformEngine.Presets" />.</summary>
    IReadOnlyList<TransformSet> TransformPresets();

    /// <summary><see cref="Grooves.FromOnsets" />.</summary>
    GrooveTemplate GrooveFromOnsets(
        string id,
        string name,
        IReadOnlyList<GrooveOnsetTick> onsets,
        long startTick,
        long lengthTicks,
        long stepTicks
    );

    /// <summary><see cref="Grooves.FromNotes" />.</summary>
    GrooveTemplate GrooveFromNotes(
        string id,
        string name,
        IReadOnlyList<Note> notes,
        long startTick,
        long lengthTicks,
        long stepTicks
    );

    /// <summary><see cref="Grooves.Target" />.</summary>
    (long Tick, double VelocityScale) GrooveTarget(GrooveTemplate groove, long tick, double strength);

    /// <summary><see cref="Grooves.Quantize" />.</summary>
    IReadOnlyList<Note> GrooveQuantize(
        IReadOnlyList<Note> notes,
        long regionStartTick,
        GrooveTemplate groove,
        double strength,
        bool velocity
    );
}

/// <summary>The algorithms of <see cref="TransformEngine" /> and <see cref="Grooves" />.</summary>
public sealed class StandardMidiAlgorithms : IMidiAlgorithms {
    public static StandardMidiAlgorithms Instance { get; } = new();

    public TransformResult Transform(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent> controls,
        TransformSet set,
        SignatureMap signatures
    ) =>
        TransformEngine.Apply(notes, controls, set, signatures);

    public IReadOnlyList<TransformSet> TransformPresets() => TransformEngine.Presets();

    public GrooveTemplate GrooveFromOnsets(
        string id,
        string name,
        IReadOnlyList<GrooveOnsetTick> onsets,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) =>
        Grooves.FromOnsets(id, name, onsets, startTick, lengthTicks, stepTicks);

    public GrooveTemplate GrooveFromNotes(
        string id,
        string name,
        IReadOnlyList<Note> notes,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) =>
        Grooves.FromNotes(id, name, notes, startTick, lengthTicks, stepTicks);

    public (long Tick, double VelocityScale) GrooveTarget(GrooveTemplate groove, long tick, double strength) =>
        Grooves.Target(groove, tick, strength);

    public IReadOnlyList<Note> GrooveQuantize(
        IReadOnlyList<Note> notes,
        long regionStartTick,
        GrooveTemplate groove,
        double strength,
        bool velocity
    ) =>
        Grooves.Quantize(notes, regionStartTick, groove, strength, velocity);
}

/// <summary>
///     Where the model finds its <see cref="IMidiAlgorithms" />: <see cref="StandardMidiAlgorithms" />, unless a test
///     assembly puts another in place once, before any test runs (a fallback while workstream E's algorithms are
///     stubs). Production code never sets it.
/// </summary>
public static class MidiAlgorithms {
    static IMidiAlgorithms current = StandardMidiAlgorithms.Instance;

    public static IMidiAlgorithms Current {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value ?? StandardMidiAlgorithms.Instance);
    }
}
