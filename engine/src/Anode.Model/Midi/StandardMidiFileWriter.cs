using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Midi;

/// <summary>
///     Writes a Standard MIDI File (format 0, 960 PPQ): a track name, a tempo and a 4/4 signature at tick 0, then the
///     notes (note-offs before note-ons at an equal tick) and controller events. What "Add to Loop Library" and the
///     content builder save MIDI loops as; <see cref="StandardMidiFile" /> reads it back.
/// </summary>
public static class StandardMidiFileWriter {
    /// <summary>A tempo ramp is written as steps this many ticks apart (a file has no ramps).</summary>
    public const int RampStepTicks = 240;

    /// <summary>
    ///     A Standard MIDI File of <paramref name="document" /> (docs/10-phase-9.md) at 960 PPQ: format 1 is a conductor
    ///     track (the name, tempo map, signatures, keys and markers), then one track per <c>Tracks</c> entry with its name,
    ///     notes and controller events; format 0 puts all of it in one track. Tempo ramps become steps
    ///     <see cref="RampStepTicks" /> apart. <see cref="StandardMidiFile" /> reads it back.
    /// </summary>
    public static byte[] Write(SmfDocument document) {
        if (document.Format is not (0 or 1)) {
            throw new ArgumentException($"A MIDI file is format 0 or 1, not {document.Format}.", nameof(document));
        }

        var conductor = Conductor(document);
        var chunks = new List<List<(long, int, byte[])>>();
        if (document.Format == 0) {
            var all = new List<(long, int, byte[])>(conductor);
            foreach (var track in document.Tracks) {
                AddEvents(all, track.Notes, track.Controls);
            }

            chunks.Add(all);
        } else {
            chunks.Add(conductor);
            foreach (var track in document.Tracks) {
                var events = new List<(long, int, byte[])>();
                if (track.Name is { Length: > 0 } trackName) {
                    events.Add((0, -3, Meta(0x03, System.Text.Encoding.UTF8.GetBytes(trackName))));
                }

                AddEvents(events, track.Notes, track.Controls);
                chunks.Add(events);
            }
        }

        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange(
            [
                0, 0, 0, 6, 0, (byte)document.Format, (byte)(chunks.Count >> 8), (byte)chunks.Count, TempoMap.Ppq >> 8,
                TempoMap.Ppq & 0xFF
            ]
        );
        foreach (var chunk in chunks) {
            Chunk(bytes, chunk, document.EndTick);
        }

        return [..bytes];
    }

    /// <summary>The conductor's events: the sequence name, tempo steps, signatures, keys and markers.</summary>
    static List<(long Tick, int Rank, byte[] Bytes)> Conductor(SmfDocument document) {
        var conductor = new List<(long Tick, int Rank, byte[] Bytes)>();
        if (document.Name is { Length: > 0 } name) {
            conductor.Add((0, -3, Meta(0x03, System.Text.Encoding.UTF8.GetBytes(name))));
        }

        foreach (var (tick, bpm) in TempoSteps(document.Tempo)) {
            var micros = (int)Math.Round(60_000_000 / Math.Clamp(bpm, TempoMap.MinBpm, TempoMap.MaxBpm));
            conductor.Add((tick, -1, Meta(0x51, [(byte)(micros >> 16), (byte)(micros >> 8), (byte)micros])));
        }

        foreach (var signature in document.Signatures) {
            var power = (byte)Math.Max(0, System.Numerics.BitOperations.Log2((uint)Math.Max(1, signature.Denominator)));
            conductor.Add((signature.Tick, -1, Meta(0x58, [(byte)signature.Numerator, power, 24, 8])));
        }

        foreach (var key in document.Keys) {
            var minor = key.Mode == KeyMode.Minor ? (byte)1 : (byte)0;
            conductor.Add((key.Tick, -1, Meta(0x59, [(byte)(sbyte)StandardMidiFile.FifthsOf(key), minor])));
        }

        foreach (var marker in document.Markers) {
            conductor.Add((marker.Tick, -1, Meta(0x06, System.Text.Encoding.UTF8.GetBytes(marker.Name))));
        }

        return conductor;
    }

    /// <summary>The tempo events as steps: a ramp's tempo every <see cref="RampStepTicks" /> to the next event.</summary>
    static IEnumerable<(long Tick, double Bpm)> TempoSteps(IReadOnlyList<TempoEvent> tempo) {
        if (tempo.Count == 0) {
            yield return (0, 120);
            yield break;
        }

        var map = TempoMap.Of(tempo);
        for (var i = 0; i < tempo.Count; i++) {
            var e = tempo[i];
            if (e.Curve != TempoCurve.Ramp || i + 1 >= tempo.Count) {
                yield return (e.Tick, e.Bpm);
                continue;
            }

            var end = tempo[i + 1].Tick;
            for (var tick = e.Tick; tick < end; tick += RampStepTicks) {
                var middle = Math.Min(tick + RampStepTicks / 2.0, (tick + end) / 2.0);
                yield return (tick, Math.Round(map.BpmAt(middle), 3));
            }
        }
    }

    static byte[] Meta(byte type, byte[] data) => [0xFF, type, ..VarInt(data.Length), ..data];

    /// <summary>Notes (note-offs before note-ons at an equal tick) and controller events as channel messages.</summary>
    static void AddEvents(
        List<(long Tick, int Rank, byte[] Bytes)> events,
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent>? controls
    ) {
        foreach (var note in notes) {
            var channel = (byte)(Math.Clamp(note.Channel, 1, 16) - 1);
            var pitch = (byte)Math.Clamp(note.Pitch, 0, 127);
            events.Add((note.StartTick, 2, [(byte)(0x90 | channel), pitch, (byte)Math.Clamp(note.Velocity, 1, 127)]));
            events.Add((note.StartTick + Math.Max(1, note.LengthTicks), 0, [(byte)(0x80 | channel), pitch, 0]));
        }

        foreach (var control in controls ?? []) {
            var channel = (byte)(Math.Clamp(control.Channel, 1, 16) - 1);
            events.Add((control.Tick, 1, ControlBytes(control, channel)));
        }
    }

    static byte[] ControlBytes(ControlEvent control, byte channel) =>
        control.Kind switch {
            ControlKind.ControlChange => [
                (byte)(0xB0 | channel), (byte)Math.Clamp(control.Number, 0, 127),
                (byte)Math.Clamp(control.Value, 0, 127)
            ],
            ControlKind.PitchBend => [
                (byte)(0xE0 | channel), (byte)((Math.Clamp(control.Value, -8192, 8191) + 8192) & 0x7F),
                (byte)(((Math.Clamp(control.Value, -8192, 8191) + 8192) >> 7) & 0x7F)
            ],
            ControlKind.ChannelPressure => [(byte)(0xD0 | channel), (byte)Math.Clamp(control.Value, 0, 127)],
            ControlKind.PolyPressure => [
                (byte)(0xA0 | channel), (byte)Math.Clamp(control.Number, 0, 127),
                (byte)Math.Clamp(control.Value, 0, 127)
            ],
            _ => [(byte)(0xC0 | channel), (byte)Math.Clamp(control.Value, 0, 127)]
        };

    /// <summary>
    ///     An <c>MTrk</c> chunk of <paramref name="events" /> in time order, ending at <paramref name="endTick" /> or
    ///     later.
    /// </summary>
    static void Chunk(List<byte> bytes, List<(long Tick, int Rank, byte[] Bytes)> events, long endTick) {
        var last = Math.Max(endTick, events.Count == 0 ? 0 : events.Max(static e => e.Tick));
        var track = new List<byte>();
        long tick = 0;
        foreach (var e in events.Select(static (e, i) => (e, i))
                     .OrderBy(static x => x.e.Tick)
                     .ThenBy(static x => x.e.Rank)
                     .ThenBy(static x => x.i)) {
            track.AddRange(VarInt((int)(e.e.Tick - tick)));
            track.AddRange(e.e.Bytes);
            tick = e.e.Tick;
        }

        track.AddRange(VarInt((int)(last - tick)));
        track.AddRange([0xFF, 0x2F, 0x00]);
        bytes.AddRange("MTrk"u8.ToArray());
        bytes.AddRange(
            [(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]
        );
        bytes.AddRange(track);
    }

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
            events.Add((control.Tick, 1, ControlBytes(control, channel)));
        }

        var last = Math.Max(endTick, events.Count == 0 ? 0 : events.Max(static e => e.Tick));
        events.Add((last, 3, [0xFF, 0x2F, 0x00]));
        var track = new List<byte>();
        long tick = 0;
        foreach (var e in events.Select(static (e, i) => (e, i))
                     .OrderBy(static x => x.e.Tick)
                     .ThenBy(static x => x.e.Rank)
                     .ThenBy(static x => x.i)) {
            track.AddRange(VarInt((int)(e.e.Tick - tick)));
            track.AddRange(e.e.Bytes);
            tick = e.e.Tick;
        }

        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange([0, 0, 0, 6, 0, 0, 0, 1, TempoMap.Ppq >> 8, TempoMap.Ppq & 0xFF]);
        bytes.AddRange("MTrk"u8.ToArray());
        bytes.AddRange(
            [(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]
        );
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

/// <summary>
///     What <see cref="StandardMidiFileWriter.Write(SmfDocument)" /> writes: the format (0 or 1), the conductor's content
///     (the sequence name, tempo map, signatures, keys, markers) and the tracks, at absolute ticks of 960 PPQ; the
///     tracks end at <c>EndTick</c> or their last event.
/// </summary>
public sealed record SmfDocument(
    int Format,
    IReadOnlyList<TempoEvent> Tempo,
    IReadOnlyList<SignatureEvent> Signatures,
    IReadOnlyList<KeyEvent> Keys,
    IReadOnlyList<SmfMarker> Markers,
    IReadOnlyList<SmfTrack> Tracks,
    string? Name = null,
    long EndTick = 0
);
