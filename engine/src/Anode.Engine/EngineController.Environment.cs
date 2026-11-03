using Anode.Dsp.Instruments;
using Anode.Engine.Environment;
using Anode.Engine.Mixing;
using Anode.Ipc;

namespace Anode.Engine;

// The Environment on the control thread (docs/12-phase-11.md, workstream A): compiling the server's graph and handing
// it to the callback, live MIDI into an on-screen object, and the control targets' moves, which reach the strips here
// as a control surface's would (a move of an automated parameter holds it against its lane).
public sealed partial class EngineController {
    static readonly TimeSpan ControlPumpInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>The fader law of a control target on a volume or send level: position (0…1) to dB (the editor's).</summary>
    static readonly (double Position, double Db)[] Taper = [
        (0, -144), (0.02, -60), (0.08, -45), (0.18, -30), (0.3, -20), (0.45, -12), (0.6, -6), (0.78, 0), (1, 6)
    ];

    /// <summary>How often the control targets' moves are reported to the server.</summary>
    const long ControlReportMilliseconds = 30;

    readonly Dictionary<EngineAutomationTarget, float> movedTargets = [];
    long lastControlReport = long.MinValue / 2;
    Timer? controlPump;

    /// <summary>The control targets' moves on their way to the server (<see cref="EnvironmentControlMessage" />).</summary>
    public Action<EnvironmentControlMessage>? EnvironmentControl { get; set; }

    /// <summary>
    ///     Replaces the Environment (<c>setEnvironment</c>, and a whole model's load; null: the default input path),
    ///     compiled here and swapped in by the callback, which resets the old one and ends its notes.
    /// </summary>
    public void SetEnvironment(EngineEnvironment? environment) {
        lock (gate) {
            Engine.Environment = EnvironmentGraph.Compile(environment, Options.SampleRate, AudioEngine.MaxFrames);
        }
    }

    /// <summary>
    ///     Live MIDI into on-screen object <paramref name="objectIndex" /> (<c>environmentMidi</c>), at the next block's
    ///     start. False when the Environment has no such object.
    /// </summary>
    public bool EnvironmentMidi(int objectIndex, IReadOnlyList<EngineMidiEvent> events) {
        var graph = Engine.Environment;
        if ((uint)objectIndex >= (uint)graph.Nodes.Count) {
            return false;
        }

        foreach (var e in events) {
            if (e.Status is >= 0x80 and < 0xF0) {
                Engine.EnterEnvironment(
                    new(0, (byte)e.Status, (byte)(e.Data1 & 0x7F), (byte)(e.Data2 & 0x7F), Object: objectIndex)
                );
            }
        }

        return true;
    }

    void StartControlPump() => controlPump = new(_ => PumpControl(), null, ControlPumpInterval, ControlPumpInterval);

    /// <summary>
    ///     The control targets' moves since the last pass, applied (tests call it directly): a volume or send level
    ///     along the fader law, a pan over −1…1, a mute on at 0.5, a plugin parameter over its range. Panner targets are
    ///     not driven. What moved goes to the server, the latest of each target at most every
    ///     <see cref="ControlReportMilliseconds" />.
    /// </summary>
    public void PumpControl() {
        lock (gate) {
            if (Engine.ControlOut.Count > 0) {
                var root = Engine.Root;
                while (Engine.ControlOut.TryDequeue(out var move)) {
                    var value = Math.Clamp(move.Value, 0f, 1f);
                    Apply(root, move.Target, value);
                    movedTargets[move.Target] = value;
                }
            }

            var now = System.Environment.TickCount64;
            if (movedTargets.Count > 0 && now - lastControlReport >= ControlReportMilliseconds) {
                lastControlReport = now;
                EnvironmentControl?.Invoke(
                    new([..movedTargets.Select(static m => new EngineControlMove(m.Key, m.Value))])
                );
                movedTargets.Clear();
            }
        }
    }

    void Apply(ModelRoot root, EngineAutomationTarget target, float value) {
        var trackId = target.TrackId;
        var channel = ChannelOf(root, trackId);
        if (channel is null) {
            return;
        }

        switch (AutomationLane.KindOf(target.Kind)) {
            case AutomationKind.Volume:
                Hold(root, trackId, AutomationKind.Volume);
                channel.Strip.SetGain(Gain(value));
                break;
            case AutomationKind.Pan:
                Hold(root, trackId, AutomationKind.Pan);
                channel.Strip.SetPan(2 * value - 1);
                break;
            case AutomationKind.Mute:
                Hold(root, trackId, AutomationKind.Mute);
                channel.Strip.SetMute(value >= 0.5f);
                break;
            case AutomationKind.Send when target.Id is { } id
                && channel.Config.Sends.FirstOrDefault(s => s.Slot.Id == id) is { } send:
                Hold(root, trackId, AutomationKind.Send, id);
                send.Slot.Set(Gain(value), send.Slot.Pan);
                break;
            case AutomationKind.Insert when target is { Id: { } id, ParameterId: { } parameter }
                && FindInsert(root, trackId, id) is { Effect: { } effect } slot:
                if (slot.Set(parameter, Over(effect.Descriptor.Parameters, parameter, value))) {
                    Hold(root, trackId, AutomationKind.Insert, id, parameter);
                }

                break;
            case AutomationKind.MidiEffect when target is { Id: { } id, ParameterId: { } parameter }
                && FindMidiEffect(root, trackId, id) is { } slot:
                if (slot.Set(parameter, Over(slot.ParameterList, parameter, value))) {
                    Hold(root, trackId, AutomationKind.MidiEffect, id, parameter);
                }

                break;
            case AutomationKind.Instrument when target.ParameterId is { } parameter
                && Find(root, trackId)?.Instrument is { } slot:
                if (slot.Set(parameter, Over(slot.Instrument.Descriptor.Parameters, parameter, value))) {
                    Hold(root, trackId, AutomationKind.Instrument, null, parameter);
                }

                break;
            default:
                break; // the panners' parameters, and targets that are gone
        }
    }

    /// <summary>A position on the fader law as a linear gain.</summary>
    static float Gain(float position) {
        for (var i = 1; i < Taper.Length; i++) {
            var (p1, d1) = Taper[i];
            if (position <= p1) {
                var (p0, d0) = Taper[i - 1];
                var db = d0 + (position - p0) / (p1 - p0) * (d1 - d0);
                return position <= 0 ? 0f : (float)Math.Pow(10, db / 20);
            }
        }

        return (float)Math.Pow(10, Taper[^1].Db / 20);
    }

    /// <summary>Parameter <paramref name="id" />'s value at <paramref name="value" /> of its range (stepped ones rounded).</summary>
    static double Over(IReadOnlyList<InstrumentParameter> parameters, string id, float value) {
        foreach (var p in parameters) {
            if (p.Id == id) {
                var x = p.Min + value * (p.Max - p.Min);
                return p.Steps > 0 || p.ValueNames is not null ? Math.Round(x) : x;
            }
        }

        return value;
    }
}
