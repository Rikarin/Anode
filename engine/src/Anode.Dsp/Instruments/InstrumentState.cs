using System.Buffers.Binary;
using System.Text;

namespace Anode.Dsp.Instruments;

/// <summary>
///     The state blob of the built-in instruments: the magic <c>ANIS</c>, a format version, the instrument's id, the
///     file it loaded (empty for none), then every parameter as its id and value. Parameters go by id, so a later version
///     that adds, removes or reorders parameters still reads an older state; unknown ids are ignored. Little-endian;
///     strings are a 16-bit byte length and UTF-8.
/// </summary>
internal static class InstrumentState {
    const byte Version = 1;
    static ReadOnlySpan<byte> Magic => "ANIS"u8;

    /// <summary>A state with <paramref name="file" /> and the values in <paramref name="parameters" />.</summary>
    public static byte[] Write(
        string instrumentId,
        string? file,
        IReadOnlyList<InstrumentParameter> definitions,
        ParameterBank parameters
    ) {
        using var stream = new MemoryStream();
        stream.Write(Magic);
        stream.WriteByte(Version);
        WriteString(stream, instrumentId);
        WriteString(stream, file ?? string.Empty);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(number, (ushort)definitions.Count);
        stream.Write(number[..2]);
        for (var i = 0; i < definitions.Count; i++) {
            WriteString(stream, definitions[i].Id);
            BinaryPrimitives.WriteSingleLittleEndian(number, parameters[i]);
            stream.Write(number);
        }

        return stream.ToArray();
    }

    /// <summary>
    ///     Reads a state of <paramref name="instrumentId" />: its file (null for none) and its values by id; false when
    ///     it is not one.
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> state,
        string instrumentId,
        out string? file,
        out List<(string Id, float Value)> values
    ) {
        file = null;
        values = [];
        if (state.Length < Magic.Length + 1
            || !state[..Magic.Length].SequenceEqual(Magic)
            || state[Magic.Length] != Version) {
            return false;
        }

        var at = Magic.Length + 1;
        if (!TryReadString(state, ref at, out var id)
            || id != instrumentId
            || !TryReadString(state, ref at, out var path)) {
            return false;
        }

        file = path.Length == 0 ? null : path;
        if (at + 2 > state.Length) {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(state[at..]);
        at += 2;
        for (var i = 0; i < count; i++) {
            if (!TryReadString(state, ref at, out var parameter) || at + 4 > state.Length) {
                return false;
            }

            values.Add((parameter, BinaryPrimitives.ReadSingleLittleEndian(state[at..])));
            at += 4;
        }

        return true;
    }

    /// <summary>Applies the values of a state read by <see cref="TryRead" /> to <paramref name="parameters" />.</summary>
    public static void Apply(List<(string Id, float Value)> values, ParameterBank parameters) {
        foreach (var (id, value) in values) {
            var index = parameters.IndexOf(id);
            if (index >= 0) {
                parameters.Set(index, value);
            }
        }
    }

    static void WriteString(MemoryStream stream, string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    static bool TryReadString(ReadOnlySpan<byte> state, ref int at, out string text) {
        text = string.Empty;
        if (at + 2 > state.Length) {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(state[at..]);
        at += 2;
        if (at + length > state.Length) {
            return false;
        }

        text = Encoding.UTF8.GetString(state.Slice(at, length));
        at += length;
        return true;
    }
}
