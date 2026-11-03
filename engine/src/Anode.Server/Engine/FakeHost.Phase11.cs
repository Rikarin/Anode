using Anode.Ipc;
using Anode.Ipc.Telemetry;

namespace Anode.Server.Engine;

/// <summary>
///     Phase 11 (docs/12-phase-11.md) in the fake host: it keeps the Environment it was sent (with a whole model, or by
///     <see cref="SetEnvironmentMessage" />) and the live MIDI sent into its objects, and reports what a test sets as
///     the Environment's monitors and faders and the Mastering Assistant's readings through its telemetry. It runs no
///     graph and no mastering.
/// </summary>
public sealed partial class FakeHost {
    readonly List<EnvironmentMidiMessage> environmentMidi = [];

    /// <summary>The Environment the server last sent (null: the default input path).</summary>
    public EngineEnvironment? Environment {
        get {
            lock (gate) {
                return environment;
            }
        }
    }

    /// <summary>How many times the Environment was sent (a whole model's or a <see cref="SetEnvironmentMessage" />).</summary>
    public int EnvironmentsReceived {
        get {
            lock (gate) {
                return environmentsReceived;
            }
        }
    }

    /// <summary>The live MIDI sent into Environment objects, in order.</summary>
    public IReadOnlyList<EnvironmentMidiMessage> EnvironmentMidi {
        get {
            lock (gate) {
                return [..environmentMidi];
            }
        }
    }

    EngineEnvironment? environment;
    int environmentsReceived;

    /// <summary>Puts monitors, faders and the assistant's readings in the telemetry region (the next frame reads them).</summary>
    public void SetPhase11Telemetry(
        IReadOnlyList<EnvironmentMonitorEntry> monitors,
        IReadOnlyList<EnvironmentFaderEntry> faders,
        MasteringEntry mastering
    ) =>
        telemetry.Write([..monitors], [..faders], mastering);

    /// <summary>A whole model's Environment (a per-track segment leaves it).</summary>
    void LoadEnvironment(LoadModelMessage load) {
        if (load.Replace) {
            lock (gate) {
                environment = load.Environment;
                environmentsReceived++;
            }
        }
    }

    /// <summary>The Phase 11 messages; null for any other message, else what it answers (nothing).</summary>
    IpcMessage[]? HandlePhase11(IpcMessage message) {
        switch (message) {
            case SetEnvironmentMessage set:
                lock (gate) {
                    environment = set.Environment;
                    environmentsReceived++;
                }

                return [];
            case EnvironmentMidiMessage midi:
                lock (gate) {
                    environmentMidi.Add(midi);
                }

                return [];
            default:
                return null;
        }
    }
}
