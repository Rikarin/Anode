using Anode.Dsp.MidiEffects;
using Anode.Engine.Mixing;
using Anode.Engine.Plugins;
using Anode.Ipc;
using System.Diagnostics;

namespace Anode.Engine;

// Phase 7, workstream A (docs/08-phase-7.md): MIDI FX slots whose parameter list is defined at run time, and scripts
// (Scripter, docs/adr/0009-scripter.md) on the control thread: a new source compiles into a new instance while the old
// one plays, which is then swapped in between blocks; the scripts' parameter lists, trace lines and errors are polled
// and reported to the server (slotParameters, scriptLog, pluginProblem).
public sealed partial class EngineController {
    static readonly TimeSpan ScriptPumpInterval = TimeSpan.FromMilliseconds(50);

    readonly List<string> traceLines = [];
    Timer? scriptPump;

    /// <summary>
    ///     A MIDI FX slot's run-time parameter list is new (a compile, or the effect changed it): the host relays it to
    ///     the server. On the control thread, before the slot that has it plays.
    /// </summary>
    public Action<SlotParametersMessage>? SlotParameters { get; set; }

    /// <summary>A script slot's trace and error lines, at most 50 a second per slot (control thread).</summary>
    public Action<ScriptLogMessage>? ScriptLog { get; set; }

    /// <summary>
    ///     A script slot's new source (<c>setScript</c>): compiled into a new instance on this thread while the current
    ///     one keeps playing, which then replaces it between blocks with its parameter values (by id); the notes the old
    ///     one left sounding end. A source that does not compile is reported and the slot passes MIDI through until the
    ///     next. False when the track has no MIDI FX slot <paramref name="slotId" /> that runs scripts.
    /// </summary>
    public bool SetScript(int trackId, string slotId, string source) {
        ArgumentNullException.ThrowIfNull(source);
        lock (gate) {
            var root = Engine.Root;
            if (trackId < 0 || ChannelOf(root, trackId) is not { } channel) {
                return false;
            }

            var config = channel.Config;
            var at = Array.FindIndex(config.MidiEffects, m => m.Slot.Id == slotId);
            if (at < 0 || config.MidiEffects[at] is not { Slot: { Script: not null } old } placed) {
                return false;
            }

            var spec = new EngineInsert(old.Id, old.PluginId, placed.Bypassed, old.Values, Source: source);
            var slot = BuildMidiEffect(trackId, spec, old);
            var chain = config.MidiEffects.ToArray();
            chain[at] = placed with { Slot = slot };
            channel.Config = config with { MidiEffects = chain };
            PublishPlan();
            old.Dispose(); // the swap's grace period covered it
            return true;
        }
    }

    /// <summary>
    ///     One pass over the slots with run-time parameters or scripts (the control thread's timer runs it about every
    ///     50 ms; tests call it): a changed parameter list is re-read, reported and bound to automation; a script's new
    ///     error bypasses it and is reported (<c>pluginProblem</c> and a log line); trace lines go out at most 50 a
    ///     second per slot, the rest counted as dropped; and each script gets its idle turn.
    ///     <paramref name="timestamp" /> is now in <see cref="Stopwatch" /> ticks (default: the clock's).
    /// </summary>
    public void PumpScripts(long timestamp = -1) {
        if (timestamp < 0) {
            timestamp = Stopwatch.GetTimestamp();
        }

        lock (gate) {
            var rebind = false;
            foreach (var track in Engine.Root.Tracks) {
                foreach (var placed in track.Channel.Config.MidiEffects) {
                    rebind |= PumpSlot(track.Id, placed.Slot, timestamp);
                }
            }

            if (rebind) {
                PublishPlan(); // automation binds to the new list's indices
            }
        }
    }

    /// <summary>One slot's turn of <see cref="PumpScripts" />; true when its parameter list changed.</summary>
    bool PumpSlot(int trackId, MidiEffectSlot slot, long timestamp) {
        var changed = false;
        if (slot.Runtime is { } runtime && runtime.TakeParametersChanged()) {
            slot.RefreshParameters();
            AnnounceParameters(trackId, slot);
            changed = true;
        }

        if (slot.Script is not { } script) {
            return changed;
        }

        if (script.TakeError() is { } error) {
            slot.MarkFailed(); // the audio thread already bypasses it; a failed compile never ran
            ScriptProblem(trackId, slot, error);
        }

        traceLines.Clear();
        var dropped = script.DrainTrace(traceLines);
        var log = slot.Log;
        var lines = log.Admit(log.Pending.Concat(traceLines), dropped, timestamp);
        log.Pending.Clear();
        if (lines.Count > 0 || log.Dropped > 0) {
            ScriptLog?.Invoke(new(trackId, slot.Id, lines, log.TakeDropped()));
        }

        script.Idle();
        return changed;
    }

    /// <summary>Reports a slot's parameter list (<see cref="SlotParameters" />).</summary>
    void AnnounceParameters(int trackId, MidiEffectSlot slot) =>
        SlotParameters?.Invoke(new(trackId, slot.Id, [..slot.ParameterList.Select(PluginRegistry.Map)]));

    /// <summary>A script's error: <c>pluginProblem</c> with its line, and a line in its log.</summary>
    void ScriptProblem(int trackId, MidiEffectSlot slot, ScriptError error) {
        var message = error.Line > 0 ? $"line {error.Line}: {error.Message}" : error.Message;
        slot.Log.Pending.Add(message);
        Problem(trackId, slot.Id, slot.PluginId, message);
    }

    void StartScriptPump() =>
        scriptPump = new(
            _ => {
                try {
                    PumpScripts();
                } catch (Exception e) when (IsInstrumentFailure(e)) {
                    Log?.Invoke($"scripts: {e.Message}");
                }
            },
            null,
            ScriptPumpInterval,
            ScriptPumpInterval
        );

    void StopScriptPump() {
        scriptPump?.Dispose();
        scriptPump = null;
    }
}
