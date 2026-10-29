using Anode.Contracts;
using Anode.Ipc;
using Anode.Model.Time;

namespace Anode.Server.Projects;

/// <summary>
///     Turns the raw MIDI of a recorded take into notes and controller events at absolute ticks (ids 0: the caller
///     numbers them). A note-on (velocity &gt; 0) is paired with the next note-off (or note-on at velocity 0) of its
///     channel and pitch; a note still held when the pass ends ends there. Polyphonic aftertouch is kept (Phase 7).
///     With a track's articulation set's input switches (Phase 7), a keyswitch note-on or a matching control change
///     selects an articulation for the notes that start after it, which carry its id; the switches themselves are not
///     recorded (nor their note-offs).
/// </summary>
static class MidiRecording {
    public static (List<Note> Notes, List<ControlEvent> Controls) Events(
        RecordedMidiTake take,
        TempoMap map,
        int sampleRate,
        long passEndTick,
        IReadOnlyList<ArticulationSwitch>? switches = null
    ) {
        switches ??= [];
        var articulation = 0;
        var switchedOff = new HashSet<(int Channel, int Key)>();
        var notes = new List<Note>();
        var controls = new List<ControlEvent>();
        var held = new Dictionary<(int Channel, int Pitch), Queue<(long Tick, int Velocity, int Articulation)>>();
        foreach (var e in (take.Events ?? []).OrderBy(static e => e.Sample)) {
            if (e is null) {
                continue;
            }

            var tick = map.TickAtSample(Math.Max(0, e.Sample), sampleRate);
            var channel = (e.Status & 0x0F) + 1;
            var (d1, d2) = (e.Data1 & 0x7F, e.Data2 & 0x7F);
            if (Switch(switches, e.Status & 0xF0, d1, d2) is { } selected) {
                articulation = selected;
                if ((e.Status & 0xF0) == 0x90) {
                    switchedOff.Add((channel, d1));
                }

                continue;
            }

            if ((e.Status & 0xF0) is 0x80 or 0x90 && switchedOff.Remove((channel, d1))) {
                continue;
            }

            switch (e.Status & 0xF0) {
                case 0x90 when d2 > 0:
                    if (!held.TryGetValue((channel, d1), out var queue)) {
                        held[(channel, d1)] = queue = new();
                    }

                    queue.Enqueue((tick, d2, articulation));
                    break;
                case 0x80:
                case 0x90:
                    if (held.TryGetValue((channel, d1), out var on) && on.TryDequeue(out var start)) {
                        notes.Add(
                            new(
                                0,
                                d1,
                                start.Velocity,
                                channel,
                                start.Tick,
                                Math.Max(1, tick - start.Tick),
                                start.Articulation
                            )
                        );
                    }

                    break;
                case 0xA0:
                    controls.Add(new(0, ControlKind.PolyPressure, channel, d1, d2, tick));
                    break;
                case 0xB0:
                    controls.Add(new(0, ControlKind.ControlChange, channel, d1, d2, tick));
                    break;
                case 0xC0:
                    controls.Add(new(0, ControlKind.ProgramChange, channel, 0, d1, tick));
                    break;
                case 0xD0:
                    controls.Add(new(0, ControlKind.ChannelPressure, channel, 0, d1, tick));
                    break;
                case 0xE0:
                    controls.Add(new(0, ControlKind.PitchBend, channel, 0, ((d2 << 7) | d1) - 8192, tick));
                    break;
            }
        }

        foreach (var ((channel, pitch), queue) in held) {
            foreach (var (tick, velocity, chosen) in queue) {
                notes.Add(new(0, pitch, velocity, channel, tick, Math.Max(1, passEndTick - tick), chosen));
            }
        }

        return (notes, controls);
    }

    /// <summary>The articulation an input switch selects with this message, or null when it is not a switch.</summary>
    static int? Switch(IReadOnlyList<ArticulationSwitch> switches, int status, int d1, int d2) {
        foreach (var s in switches) {
            var match = s.Kind switch {
                ArticulationSwitchKind.Note => status == 0x90 && d2 > 0 && d1 == s.Number,
                _ => status == 0xB0 && d1 == s.Number && (s.Value is null || s.Value == d2)
            };
            if (match) {
                return s.ArticulationId;
            }
        }

        return null;
    }
}
