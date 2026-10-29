using Anode.Contracts;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Anode.Server.Library;

/// <summary>
///     Reads a tagged loop's metadata (docs/07-phase-6.md, "Tagged loops"): Anode's sidecar
///     (<c>&lt;file&gt;.anodeloop.json</c>) first; else, for a WAV, its ACID chunk (<c>acid</c>: one-shot flag, root
///     note, beats, tempo) and BWF <c>iXML</c> (a <c>TEMPO</c> or <c>BPM</c>, <c>KEY</c>, <c>BEATS</c> element where
///     present); else a WAV is a one-shot without key and a MIDI file a loop of its own length.
/// </summary>
public static class LoopFiles {
    public const string SidecarSuffix = ".anodeloop.json";

    /// <summary>The ACID chunk's flags: a one-shot, and whether the root note is set.</summary>
    const uint AcidOneShot = 0x01;

    const uint AcidRootSet = 0x02;

    /// <summary>Whether <paramref name="path" /> is a loop's media (a WAV or a MIDI file).</summary>
    public static bool IsMedia(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".wave" or ".mid" or ".midi";

    /// <summary>The sidecar of <paramref name="path" /> (a media file), or null when it has none or it is unreadable.</summary>
    public static LoopSidecar? Sidecar(string path) {
        var sidecar = path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase) ? path : path + SidecarSuffix;
        if (!File.Exists(sidecar)) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize(File.ReadAllText(sidecar), ContractsJsonContext.Default.LoopSidecar) is {
                Version: >= 1
            } read
                ? read with { Tags = read.Tags ?? [] }
                : null;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>A loop's metadata: its sidecar's, else what its file says (null: not a loop file Anode reads).</summary>
    public static LoopSidecar? Read(string path) {
        if (Sidecar(path) is { } sidecar) {
            return sidecar;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".mid" or ".midi") {
            return new(1, LoopKind.Midi, null, null, null, null, LoopScale.Neither, [], false);
        }

        if (extension is not (".wav" or ".wave")) {
            return null;
        }

        try {
            using var stream = File.OpenRead(path);
            return Wav(stream);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) {
            return null;
        }
    }

    /// <summary>A WAV's loop metadata from its <c>acid</c> and <c>iXML</c> chunks (a one-shot when it has neither).</summary>
    public static LoopSidecar Wav(Stream stream) {
        var (acid, ixml) = Chunks(stream);
        var loop = new LoopSidecar(1, LoopKind.Audio, null, null, null, null, LoopScale.Neither, [], true);
        if (ixml is not null) {
            loop = IXml(loop, ixml);
        }

        if (acid is { Length: >= 24 } a) {
            loop = Acid(loop, a);
        }

        return loop;
    }

    /// <summary>The ACID chunk read onto <paramref name="loop" />: one-shot flag, root note (its key), beats and tempo.</summary>
    public static LoopSidecar Acid(LoopSidecar loop, ReadOnlySpan<byte> chunk) {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        var root = BinaryPrimitives.ReadUInt16LittleEndian(chunk[4..]);
        var beats = BinaryPrimitives.ReadUInt32LittleEndian(chunk[12..]);
        var tempo = BinaryPrimitives.ReadSingleLittleEndian(chunk[20..]);
        var oneShot = (flags & AcidOneShot) != 0;
        return loop with {
            OneShot = oneShot,
            Key = (flags & AcidRootSet) != 0 ? root % 12 : loop.Key,
            Beats = oneShot || beats == 0 ? loop.Beats : beats,
            Tempo = AcidTempo(loop, oneShot, tempo)
        };
    }

    /// <summary>An ACID loop's tempo: none for a one-shot, the chunk's when it is one, else what the loop had.</summary>
    static double? AcidTempo(LoopSidecar loop, bool oneShot, float tempo) {
        if (oneShot) {
            return null;
        }

        return tempo is > 5 and < 1000 ? Math.Round(tempo, 3) : loop.Tempo;
    }

    /// <summary>The <c>iXML</c> chunk read onto <paramref name="loop" />: its tempo, key and beats where it names them.</summary>
    static LoopSidecar IXml(LoopSidecar loop, byte[] chunk) {
        try {
            var document = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(
                new MemoryStream(chunk),
                new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
            );
            document.Load(reader);
            var tempo = Number(document, "TEMPO") ?? Number(document, "BPM");
            var beats = Number(document, "BEATS");
            var key = Text(document, "KEY") is { } k ? Tonic(k) : null;
            return loop with {
                Tempo = tempo ?? loop.Tempo,
                Beats = beats ?? loop.Beats,
                Key = key ?? loop.Key,
                OneShot = loop.OneShot && tempo is null
            };
        } catch (XmlException) {
            return loop;
        }
    }

    static string? Text(XmlDocument document, string name) =>
        document.GetElementsByTagName(name) is { Count: > 0 } nodes ? nodes[0]!.InnerText.Trim() : null;

    static double? Number(XmlDocument document, string name) =>
        Text(document, name) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : null;

    /// <summary>A key's tonic (0 C … 11 B) from a name such as <c>C</c>, <c>F#m</c> or <c>Bb minor</c>.</summary>
    public static int? Tonic(string name) {
        if (name.Length == 0) {
            return null;
        }

        int? tonic = char.ToUpperInvariant(name[0]) switch {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => null
        };
        if (tonic is not { } t) {
            return null;
        }

        if (name.Length > 1) {
            t += name[1] switch {
                '#' or '♯' => 1,
                'b' or '♭' => -1,
                _ => 0
            };
        }

        return (t + 12) % 12;
    }

    /// <summary>The <c>acid</c> and <c>iXML</c> chunks of a RIFF WAVE stream (null where absent).</summary>
    static (byte[]? Acid, byte[]? IXml) Chunks(Stream stream) {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (Encoding.ASCII.GetString(header[..4]) != "RIFF" || Encoding.ASCII.GetString(header[8..12]) != "WAVE") {
            throw new FormatException("Not a RIFF WAVE file.");
        }

        byte[]? acid = null;
        byte[]? ixml = null;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length) {
            stream.ReadExactly(chunk);
            var id = Encoding.ASCII.GetString(chunk[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var next = stream.Position + size + (size & 1);
            if (id is "acid" or "iXML" && size <= 1 << 20 && stream.Position + size <= stream.Length) {
                var bytes = new byte[size];
                stream.ReadExactly(bytes);
                if (id == "acid") {
                    acid = bytes;
                } else {
                    ixml = bytes;
                }
            }

            if (next > stream.Length) {
                break;
            }

            stream.Position = next;
        }

        return (acid, ixml);
    }

    /// <summary>An ACID chunk (24 bytes) for a loop: what <see cref="Acid" /> reads back.</summary>
    public static byte[] AcidChunk(bool oneShot, int? rootNote, uint beats, float tempo) {
        var chunk = new byte[24];
        var flags = (oneShot ? AcidOneShot : 0) | (rootNote is null ? 0 : AcidRootSet);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, flags);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(4), (ushort)(rootNote ?? 60));
        BinaryPrimitives.WriteSingleLittleEndian(chunk.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), beats);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(18), 4);
        BinaryPrimitives.WriteSingleLittleEndian(chunk.AsSpan(20), tempo);
        return chunk;
    }
}
