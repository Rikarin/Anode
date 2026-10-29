using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Midi;

/// <summary>
///     Writes a Standard MIDI File (format 0, 960 PPQ): a track name, a tempo and a 4/4 signature at tick 0, then the
///     notes (note-offs before note-ons at an equal tick) and controller events. What "Add to Loop Library" and the
///     content builder save MIDI loops as; <see cref="StandardMidiFile" /> reads it back.
/// </summary>
public static class StandardMidiFileWriter {
    public static byte[] Write(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent>? controls = null,
        double bpm = 120,
        string? name = null,
        long endTick = 0
    ) {
        var events = new List<(long Tick, int Rank, byte[] Bytes)>();
        if (name is { Length: > 0 }) {
            var text = System.Text.Encoding.UTF8.GetBytes(name);
            events.Add((0, -2, [0xFF, 0x03, ..VarInt(text.Length), ..text]));
        }

        var micros = (int)Math.Round(60_000_000 / Math.Clamp(bpm, TempoMap.MinBpm, TempoMap.MaxBpm));
        events.Add((0, -1, [0xFF, 0x51, 0x03, (byte)(micros >> 16), (byte)(micros >> 8), (byte)micros]));
        events.Add((0, -1, [0xFF, 0x58, 0x04, 4, 2, 24, 8]));
        foreach (var note in notes) {
            var channel = (byte)(Math.Clamp(note.Channel, 1, 16) - 1);
            var pitch = (byte)Math.Clamp(note.Pitch, 0, 127);
            events.Add((note.StartTick, 2, [(byte)(0x90 | channel), pitch, (byte)Math.Clamp(note.Velocity, 1, 127)]));
            events.Add((note.StartTick + Math.Max(1, note.LengthTicks), 0, [(byte)(0x80 | channel), pitch, 0]));
        }

        foreach (var control in controls ?? []) {
            var channel = (byte)(Math.Clamp(control.Channel, 1, 16) - 1);
            events.Add((control.Tick, 1, control.Kind switch {
                ControlKind.ControlChange => [(byte)(0xB0 | channel), (byte)control.Number, (byte)control.Value],
                ControlKind.PitchBend => [
                    (byte)(0xE0 | channel), (byte)((control.Value + 8192) & 0x7F), (byte)(((control.Value + 8192) >> 7) & 0x7F)
                ],
                ControlKind.ChannelPressure => [(byte)(0xD0 | channel), (byte)control.Value],
                _ => [(byte)(0xC0 | channel), (byte)control.Value]
            }));
        }

        var last = Math.Max(endTick, events.Count == 0 ? 0 : events.Max(static e => e.Tick));
        events.Add((last, 3, [0xFF, 0x2F, 0x00]));
        var track = new List<byte>();
        long tick = 0;
        foreach (var e in events.Select(static (e, i) => (e, i)).OrderBy(static x => x.e.Tick).ThenBy(static x => x.e.Rank).ThenBy(static x => x.i)) {
            track.AddRange(VarInt((int)(e.e.Tick - tick)));
            track.AddRange(e.e.Bytes);
            tick = e.e.Tick;
        }

        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange([0, 0, 0, 6, 0, 0, 0, 1, TempoMap.Ppq >> 8, TempoMap.Ppq & 0xFF]);
        bytes.AddRange("MTrk"u8.ToArray());
        bytes.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]);
        bytes.AddRange(track);
        return [..bytes];
    }

    static byte[] VarInt(int value) {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0) {
            stack.Push((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        return [..stack];
    }
}
