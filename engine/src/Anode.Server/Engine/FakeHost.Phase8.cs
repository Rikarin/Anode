using Anode.Ipc;
using System.Diagnostics;

namespace Anode.Server.Engine;

/// <summary>
///     Phase 8 (docs/09-phase-8.md): the fake host keeps the control routing and the sync settings it was given and
///     records the MIDI the server sends to ports (surface feedback); it answers each <see cref="SetSyncMessage" /> with a
///     <see cref="SyncStateMessage" /> (locked for internal and Link, as the real host is with no external source yet).
///     <see cref="PlayControlAsync" /> and <see cref="ReportSyncStateAsync" /> play the host's side for tests.
/// </summary>
public sealed partial class FakeHost {
    const int MaxSentMidi = 4096;

    readonly List<SendMidiMessage> sentMidi = [];
    SetControlRoutingMessage? controlRouting;
    SetSyncMessage? sync;

    /// <summary>The control routing last set (null: none yet).</summary>
    public SetControlRoutingMessage? ControlRouting {
        get {
            lock (gate) {
                return controlRouting;
            }
        }
    }

    /// <summary>The sync settings last set (null: none yet).</summary>
    public SetSyncMessage? Sync {
        get {
            lock (gate) {
                return sync;
            }
        }
    }

    /// <summary>The MIDI sent to ports (the latest <c>4096</c>), in order.</summary>
    public IReadOnlyList<SendMidiMessage> SentMidi {
        get {
            lock (gate) {
                return [..sentMidi];
            }
        }
    }

    /// <summary>Forgets the MIDI sent so far.</summary>
    public void ClearSentMidi() {
        lock (gate) {
            sentMidi.Clear();
        }
    }

    /// <summary>The host's clock for control events (nanoseconds, as <c>cth_midi_now_ns</c>).</summary>
    public static long NowNs => (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));

    /// <summary>Plays messages arriving for control on a port (consumed, a control port's, or learning), now.</summary>
    public Task PlayControlAsync(string port, params byte[][] messages) =>
        SendAsync(new ControlMidiMessage([..messages.Select(m => new EngineControlEvent(port, NowNs, m))]));

    /// <summary>Plays the host reporting its sync state.</summary>
    public Task ReportSyncStateAsync(SyncStateMessage state) => SendAsync(state);

    /// <summary>The Phase 8 messages; null for any other message, else what it answers (possibly nothing).</summary>
    IpcMessage[]? HandlePhase8(IpcMessage message) {
        switch (message) {
            case SetControlRoutingMessage routing:
                lock (gate) {
                    controlRouting = routing;
                }

                return [];
            case SendMidiMessage send:
                lock (gate) {
                    sentMidi.Add(send);
                    if (sentMidi.Count > MaxSentMidi) {
                        sentMidi.RemoveRange(0, sentMidi.Count - MaxSentMidi);
                    }
                }

                return [];
            case SetSyncMessage set:
                lock (gate) {
                    sync = set;
                }

                return [
                    new SyncStateMessage(
                        set.Source,
                        set.Source is EngineSyncSource.Internal or EngineSyncSource.Link,
                        null,
                        0
                    )
                ];
            default:
                return null;
        }
    }
}
