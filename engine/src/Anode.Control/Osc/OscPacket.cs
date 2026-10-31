using System.Buffers.Binary;
using System.Text;

namespace Anode.Control.Osc;

/// <summary>An OSC 1.0 packet: a message or a bundle. Packets compare by value (arguments, elements and blobs).</summary>
public abstract record OscPacket;

/// <summary>An OSC message: an address pattern and its arguments.</summary>
public sealed record OscMessage(string Address, IReadOnlyList<OscArgument> Arguments) : OscPacket {
    public bool Equals(OscMessage? other) =>
        other is not null && Address == other.Address && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Address, Arguments.Count);
}

/// <summary>An OSC bundle: an NTP time tag (1: immediately) and its elements, messages or bundles.</summary>
public sealed record OscBundle(ulong TimeTag, IReadOnlyList<OscPacket> Elements) : OscPacket {
    /// <summary>The time tag that means "immediately".</summary>
    public const ulong Immediately = 1;

    public bool Equals(OscBundle? other) =>
        other is not null && TimeTag == other.TimeTag && Elements.SequenceEqual(other.Elements);

    public override int GetHashCode() => HashCode.Combine(TimeTag, Elements.Count);
}

/// <summary>An OSC argument of one of the types Anode reads and writes.</summary>
public abstract record OscArgument;

/// <summary>Type tag <c>i</c>.</summary>
public sealed record OscInt(int Value) : OscArgument;

/// <summary>Type tag <c>f</c>.</summary>
public sealed record OscFloat(float Value) : OscArgument;

/// <summary>Type tag <c>s</c>.</summary>
public sealed record OscString(string Value) : OscArgument;

/// <summary>Type tag <c>b</c>.</summary>
public sealed record OscBlob(byte[] Value) : OscArgument {
    public bool Equals(OscBlob? other) => other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode() => Value.Length;
}

/// <summary>Type tags <c>T</c> and <c>F</c>.</summary>
public sealed record OscBool(bool Value) : OscArgument;

/// <summary>
///     The OSC 1.0 binary format (4-byte aligned, big-endian, NUL-padded UTF-8 strings, type tag strings; bundles with
///     their time tags). Decoding also reads <c>h</c> (as <see cref="OscInt" /> when it fits), <c>d</c> (as
///     <see cref="OscFloat" />), <c>S</c> (as <see cref="OscString" />), and skips <c>N</c> and <c>I</c>; a message
///     without a type tag string has no arguments.
/// </summary>
public static class OscCodec {
    const string BundleTag = "#bundle";
    const int MaxDepth = 16;

    /// <summary>A packet from a datagram; a malformed one is a <see cref="FormatException" /> (never another exception).</summary>
    public static OscPacket Decode(ReadOnlySpan<byte> data) {
        var reader = new Reader(data);
        var packet = ReadPacket(ref reader, data.Length, 0);
        return reader.Position == data.Length ? packet : throw new FormatException("Trailing bytes after the packet.");
    }

    /// <summary>A packet from a datagram, or false for a malformed one.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out OscPacket? packet) {
        try {
            packet = Decode(data);
            return true;
        } catch (FormatException) {
            packet = null;
            return false;
        }
    }

    /// <summary>
    ///     A packet as a datagram. An address that does not start with '/', or a string with a NUL in it, is an
    ///     <see cref="ArgumentException" />.
    /// </summary>
    public static byte[] Encode(OscPacket packet) {
        ArgumentNullException.ThrowIfNull(packet);
        var output = new List<byte>(64);
        Write(output, packet);
        return [..output];
    }

    static OscPacket ReadPacket(ref Reader reader, int end, int depth) {
        if (depth > MaxDepth) {
            throw new FormatException("Bundles nested too deep.");
        }

        if (reader.Position >= end) {
            throw new FormatException("An empty packet.");
        }

        return reader.Peek == '#' ? ReadBundle(ref reader, end, depth) : ReadMessage(ref reader, end);
    }

    static OscBundle ReadBundle(ref Reader reader, int end, int depth) {
        if (reader.ReadString(end) != BundleTag) {
            throw new FormatException("Not a bundle.");
        }

        var timeTag = reader.ReadUInt64(end);
        var elements = new List<OscPacket>();
        while (reader.Position < end) {
            var size = reader.ReadInt32(end);
            if (size <= 0 || size % 4 != 0 || size > end - reader.Position) {
                throw new FormatException("A bundle element's size is wrong.");
            }

            var elementEnd = reader.Position + size;
            elements.Add(ReadPacket(ref reader, elementEnd, depth + 1));
            if (reader.Position != elementEnd) {
                throw new FormatException("A bundle element is shorter than its size.");
            }
        }

        return new(timeTag, elements);
    }

    static OscMessage ReadMessage(ref Reader reader, int end) {
        var address = reader.ReadString(end);
        if (address.Length == 0 || address[0] != '/') {
            throw new FormatException("An address must start with '/'.");
        }

        var arguments = new List<OscArgument>();
        if (reader.Position == end) {
            return new(address, arguments);
        }

        var tags = reader.ReadString(end);
        if (tags.Length == 0 || tags[0] != ',') {
            throw new FormatException("The type tag string must start with ','.");
        }

        for (var i = 1; i < tags.Length; i++) {
            if (ReadArgument(ref reader, end, tags[i]) is { } argument) {
                arguments.Add(argument);
            }
        }

        return new(address, arguments);
    }

    /// <summary>The argument of type <paramref name="tag" />; null for the types skipped (N, I).</summary>
    static OscArgument? ReadArgument(ref Reader reader, int end, char tag) {
        switch (tag) {
            case 'i':
                return new OscInt(reader.ReadInt32(end));
            case 'f':
                return new OscFloat(BitConverter.Int32BitsToSingle(reader.ReadInt32(end)));
            case 's' or 'S':
                return new OscString(reader.ReadString(end));
            case 'b':
                var size = reader.ReadInt32(end);
                return size < 0
                    ? throw new FormatException("A negative blob size.")
                    : new OscBlob(reader.ReadBytes(size, end));
            case 'T' or 'F':
                return new OscBool(tag == 'T');
            case 'h':
                var value = (long)reader.ReadUInt64(end);
                return value is < int.MinValue or > int.MaxValue
                    ? throw new FormatException("A 64-bit integer out of the 32-bit range.")
                    : new OscInt((int)value);
            case 'd':
                return new OscFloat((float)BitConverter.Int64BitsToDouble((long)reader.ReadUInt64(end)));
            case 'N' or 'I':
                return null;
            default:
                throw new FormatException($"Unsupported type tag '{tag}'.");
        }
    }

    static void Write(List<byte> output, OscPacket packet) {
        switch (packet) {
            case OscMessage message:
                WriteMessage(output, message);
                break;
            case OscBundle bundle:
                WriteString(output, BundleTag);
                Span<byte> tag = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(tag, bundle.TimeTag);
                output.AddRange(tag);
                foreach (var element in bundle.Elements) {
                    var start = output.Count;
                    WriteInt32(output, 0);
                    Write(output, element);
                    var size = output.Count - start - 4;
                    for (var i = 0; i < 4; i++) {
                        output[start + i] = (byte)(size >> (24 - 8 * i));
                    }
                }

                break;
            default:
                throw new ArgumentException("Unknown OSC packet type.", nameof(packet));
        }
    }

    static void WriteMessage(List<byte> output, OscMessage message) {
        if (message.Address.Length == 0 || message.Address[0] != '/') {
            throw new ArgumentException("An OSC address must start with '/'.", nameof(message));
        }

        WriteString(output, message.Address);
        var tags = new StringBuilder(",");
        foreach (var argument in message.Arguments) {
            tags.Append(TypeTag(argument));
        }

        WriteString(output, tags.ToString());
        foreach (var argument in message.Arguments) {
            switch (argument) {
                case OscInt i:
                    WriteInt32(output, i.Value);
                    break;
                case OscFloat f:
                    WriteInt32(output, BitConverter.SingleToInt32Bits(f.Value));
                    break;
                case OscString s:
                    WriteString(output, s.Value);
                    break;
                case OscBlob b:
                    WriteInt32(output, b.Value.Length);
                    output.AddRange(b.Value);
                    Pad(output);
                    break;
            }
        }
    }

    static char TypeTag(OscArgument argument) =>
        argument switch {
            OscInt => 'i',
            OscFloat => 'f',
            OscString => 's',
            OscBlob => 'b',
            OscBool { Value: true } => 'T',
            OscBool => 'F',
            _ => throw new ArgumentException("Unknown OSC argument type.", nameof(argument))
        };

    static void WriteString(List<byte> output, string value) {
        if (value.Contains('\0', StringComparison.Ordinal)) {
            throw new ArgumentException("An OSC string cannot hold a NUL.", nameof(value));
        }

        output.AddRange(Encoding.UTF8.GetBytes(value));
        output.Add(0);
        Pad(output);
    }

    static void WriteInt32(List<byte> output, int value) {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.AddRange(bytes);
    }

    static void Pad(List<byte> output) {
        while (output.Count % 4 != 0) {
            output.Add(0);
        }
    }

    /// <summary>Reads 4-byte aligned OSC fields, every overrun a <see cref="FormatException" />.</summary>
    ref struct Reader(ReadOnlySpan<byte> data) {
        readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public readonly byte Peek => _data[Position];

        public int ReadInt32(int end) {
            Need(4, end);
            var value = BinaryPrimitives.ReadInt32BigEndian(_data[Position..]);
            Position += 4;
            return value;
        }

        public ulong ReadUInt64(int end) {
            Need(8, end);
            var value = BinaryPrimitives.ReadUInt64BigEndian(_data[Position..]);
            Position += 8;
            return value;
        }

        public string ReadString(int end) {
            var rest = _data[Position..end];
            var nul = rest.IndexOf((byte)0);
            if (nul < 0) {
                throw new FormatException("A string without its NUL.");
            }

            var padded = (nul + 4) & ~3;
            if (padded > rest.Length) {
                throw new FormatException("A string without its padding.");
            }

            string value;
            try {
                value = new UTF8Encoding(false, true).GetString(rest[..nul]);
            } catch (ArgumentException e) {
                throw new FormatException("A string that is not UTF-8.", e);
            }

            Position += padded;
            return value;
        }

        public byte[] ReadBytes(int size, int end) {
            var padded = (size + 3) & ~3;
            if (size > end - Position || padded > end - Position) {
                throw new FormatException("A blob longer than the packet.");
            }

            var value = _data.Slice(Position, size).ToArray();
            Position += padded;
            return value;
        }

        readonly void Need(int count, int end) {
            if (count > end - Position) {
                throw new FormatException("A field past the end of the packet.");
            }
        }
    }
}
