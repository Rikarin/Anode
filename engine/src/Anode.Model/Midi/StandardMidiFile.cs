using Anode.Contracts;
using Anode.Model.Time;
using System.Buffers.Binary;
using System.Text;

namespace Anode.Model.Midi;

/// <summary>
///     One track of a Standard MIDI File: its name (meta event 3) and its notes and controller events at absolute
///     ticks of 960 PPQ (ids 0: the caller numbers them).
/// </summary>
public sealed record SmfTrack(string? Name, IReadOnlyList<Note> Notes, IReadOnlyList<ControlEvent> Controls);

/// <summary>
///     A Standard MIDI File read for import: its format (0 or 1), its tracks, and its tempo and signature events from
///     every track at 960 PPQ — a tempo track and a signature track as the project keeps them (120 BPM and 4/4 at tick 0
///     where the file says nothing, changes off a bar line moved to the next one).
/// </summary>
public sealed record SmfFile(
    int Format,
    IReadOnlyList<SmfTrack> Tracks,
    IReadOnlyList<TempoEvent> Tempo,
    IReadOnlyList<SignatureEvent> Signatures
);

/// <summary>Reads Standard MIDI Files, format 0 and 1, with a PPQ time division. A damaged file throws <see cref="FormatException" />.</summary>
public static class StandardMidiFile {
    public const long MaxBytes = 64L << 20;
    const double DefaultBpm = 120;

    public static SmfFile Read(string path) {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) {
            throw new FormatException($"'{path}' is larger than a MIDI file Anode reads ({MaxBytes >> 20} MB).");
        }

        return Read(File.ReadAllBytes(path));
    }

    public static SmfFile Read(ReadOnlySpan<byte> data) {
        var reader = new Reader(data.ToArray());
        if (reader.Tag() != "MThd") {
            throw new FormatException("Not a Standard MIDI File: no MThd header.");
        }

        var headerLength = reader.UInt32();
        if (headerLength < 6) {
            throw new FormatException("The MIDI file's header is too short.");
        }

        var format = reader.UInt16();
        var trackCount = reader.UInt16();
        var division = reader.UInt16();
        reader.Skip(headerLength - 6);
        if (format is not (0 or 1)) {
            throw new FormatException($"MIDI file format {format} is not imported (only 0 and 1).");
        }

        if ((division & 0x8000) != 0 || division == 0) {
            throw new FormatException("MIDI files timed in SMPTE frames are not imported (only PPQ).");
        }

        var tracks = new List<SmfTrack>();
        var tempo = new SortedDictionary<long, double>();
        var signatures = new SortedDictionary<long, (int Numerator, int Denominator)>();
        for (var t = 0; t < trackCount && !reader.AtEnd; t++) {
            var tag = reader.Tag();
            var length = reader.UInt32();
            if (tag != "MTrk") {
                reader.Skip(length);
                t--;
                continue;
            }

            var chunk = new Reader(reader.Bytes(length));
            tracks.Add(ReadTrack(chunk, division, tempo, signatures));
        }

        return new(format, tracks, TempoTrack(tempo), SignatureTrack(signatures));
    }

    static SmfTrack ReadTrack(
        Reader r,
        int division,
        SortedDictionary<long, double> tempo,
        SortedDictionary<long, (int, int)> signatures
    ) {
        string? name = null;
        var notes = new List<Note>();
        var controls = new List<ControlEvent>();
        var held = new Dictionary<(int Channel, int Pitch), Queue<(long Tick, int Velocity)>>();
        long ticks = 0;
        var status = 0;
        while (!r.AtEnd) {
            ticks += r.VarInt();
            var tick = ticks * TempoMap.Ppq / division;
            var b = r.Byte();
            if (b == 0xFF) {
                var type = r.Byte();
                var data = r.Bytes(r.VarInt());
                switch (type) {
                    case 0x03 when name is null:
                        name = Encoding.Latin1.GetString(data).Trim();
                        break;
                    case 0x51 when data.Length >= 3:
                        var microseconds = (data[0] << 16) | (data[1] << 8) | data[2];
                        if (microseconds > 0) {
                            tempo[tick] = Math.Clamp(60_000_000.0 / microseconds, TempoMap.MinBpm, TempoMap.MaxBpm);
                        }

                        break;
                    case 0x58 when data.Length >= 2 && data[0] is >= 1 and <= 32 && data[1] <= 5:
                        signatures[tick] = (data[0], 1 << data[1]);
                        break;
                    case 0x2F:
                        r.SkipToEnd();
                        break;
                }

                continue;
            }

            if (b is 0xF0 or 0xF7) {
                r.Skip(r.VarInt());
                continue;
            }

            int data1;
            if (b < 0x80) {
                if (status == 0) {
                    throw new FormatException("A MIDI track starts with running status.");
                }

                data1 = b;
            } else {
                status = b;
                data1 = r.Byte();
            }

            var channel = (status & 0x0F) + 1;
            var kind = status & 0xF0;
            var data2 = kind is 0xC0 or 0xD0 ? 0 : r.Byte();
            (data1, data2) = (data1 & 0x7F, data2 & 0x7F);
            switch (kind) {
                case 0x90 when data2 > 0:
                    if (!held.TryGetValue((channel, data1), out var queue)) {
                        held[(channel, data1)] = queue = new();
                    }

                    queue.Enqueue((tick, data2));
                    break;
                case 0x80:
                case 0x90:
                    if (held.TryGetValue((channel, data1), out var on) && on.TryDequeue(out var start)) {
                        notes.Add(new(0, data1, start.Velocity, channel, start.Tick, Math.Max(1, tick - start.Tick)));
                    }

                    break;
                case 0xB0:
                    controls.Add(new(0, ControlKind.ControlChange, channel, data1, data2, tick));
                    break;
                case 0xC0:
                    controls.Add(new(0, ControlKind.ProgramChange, channel, 0, data1, tick));
                    break;
                case 0xD0:
                    controls.Add(new(0, ControlKind.ChannelPressure, channel, 0, data1, tick));
                    break;
                case 0xE0:
                    controls.Add(new(0, ControlKind.PitchBend, channel, 0, ((data2 << 7) | data1) - 8192, tick));
                    break;
            }
        }

        // Notes still held at the track's end end there.
        var end = ticks * TempoMap.Ppq / division;
        foreach (var ((channel, pitch), queue) in held) {
            foreach (var (tick, velocity) in queue) {
                notes.Add(new(0, pitch, velocity, channel, tick, Math.Max(1, end - tick)));
            }
        }

        return new(name is { Length: > 0 } ? name : null, notes, controls);
    }

    static TempoEvent[] TempoTrack(SortedDictionary<long, double> tempo) {
        var events = new List<TempoEvent>();
        if (!tempo.ContainsKey(0)) {
            events.Add(new(0, DefaultBpm, TempoCurve.Step));
        }

        foreach (var (tick, bpm) in tempo) {
            events.Add(new(tick, bpm, TempoCurve.Step));
        }

        return [..events];
    }

    /// <summary>The signature changes on bar lines: one off a bar line moves to the next bar line.</summary>
    static SignatureEvent[] SignatureTrack(SortedDictionary<long, (int Numerator, int Denominator)> signatures) {
        var events = new List<SignatureEvent>();
        if (!signatures.ContainsKey(0)) {
            events.Add(new(0, 4, 4));
        }

        foreach (var (tick, (numerator, denominator)) in signatures) {
            var at = tick;
            if (events.Count > 0) {
                var previous = events[^1];
                var bar = SignatureMap.BarTicks(previous);
                var offset = at - previous.Tick;
                at = previous.Tick + (offset + bar - 1) / bar * bar;
                if (at == previous.Tick) {
                    events.RemoveAt(events.Count - 1);
                }
            }

            events.Add(new(at, numerator, denominator));
        }

        return [..events];
    }

    /// <summary>Big-endian reads over a byte array that fail as <see cref="FormatException" /> at its end.</summary>
    sealed class Reader(byte[] data) {
        int position;

        public bool AtEnd => position >= data.Length;

        public byte Byte() => position < data.Length ? data[position++] : throw Truncated();

        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));

        public int UInt32() {
            var value = BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));
            return value > int.MaxValue ? throw new FormatException("A MIDI chunk is too long.") : (int)value;
        }

        public string Tag() => Encoding.ASCII.GetString(Bytes(4));

        public int VarInt() {
            var value = 0;
            for (var i = 0; i < 4; i++) {
                var b = Byte();
                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) {
                    return value;
                }
            }

            throw new FormatException("A MIDI variable-length number is longer than four bytes.");
        }

        public byte[] Bytes(int count) {
            if (count < 0 || count > data.Length - position) {
                throw Truncated();
            }

            var bytes = data.AsSpan(position, count).ToArray();
            position += count;
            return bytes;
        }

        public void Skip(int count) => Bytes(count);

        public void SkipToEnd() => position = data.Length;

        static FormatException Truncated() => new("The MIDI file is cut short.");
    }
}
