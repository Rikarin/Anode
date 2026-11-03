using Anode.Contracts;
using Anode.Model.Audio;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Anode.Interchange.Adm;

/// <summary>
///     ADM BWF reader (docs/11-phase-10.md, workstream D): the subset <see cref="AdmWriter" /> writes (beds as
///     <c>DirectSpeakers</c> of the named layouts, objects with polar block formats), from RIFF, RF64 or BW64. A file
///     without <c>chna</c> and <c>axml</c>, or with ADM Anode cannot place, throws <see cref="FormatException" />.
/// </summary>
/// <remarks>
///     It reads the first <c>audioProgramme</c> (or, without one, every <c>audioObject</c> no other references): each
///     object (nested ones flattened) whose pack is <c>DirectSpeakers</c> becomes a bed when its speaker labels (from the
///     common definitions or its own channel formats) are exactly one of Anode's layouts, in any order; one whose pack
///     is <c>Objects</c> with one or two channels and polar positions becomes an object, its blocks those of its first
///     channel (times offset by the object's start; <c>width</c>/<c>height</c> back to size, <c>jumpPosition</c> 1 as no
///     interpolation). HOA, Binaural, Matrix, Cartesian positions and other layouts are refused.
/// </remarks>
public static class AdmReader {
    const int Block = 4096;

    /// <summary>
    ///     The scene of the file at <paramref name="path" />, its beds' and objects' audio extracted as WAV files into
    ///     <paramref name="mediaFolder" /> (a scratch folder the job gives).
    /// </summary>
    public static AdmScene Read(string path, string mediaFolder) {
        using var reader = Bw64Reader.Open(path);
        if (!reader.Has("chna") || !reader.Has("axml")) {
            throw new FormatException("The file has no chna and axml chunks: it is not ADM BWF.");
        }

        var tracks = Chna(reader.Chunk("chna"));
        XDocument document;
        try {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var xml = XmlReader.Create(new MemoryStream(reader.Chunk("axml")), settings);
            document = XDocument.Load(xml);
        } catch (XmlException e) {
            throw new FormatException("The axml chunk is not well-formed XML.", e);
        }

        var adm = new Model(document, tracks, reader.SampleRate, reader.Frames);
        var plan = adm.Plan(reader.Channels);
        Directory.CreateDirectory(mediaFolder);
        var encoding = (reader.BitsPerSample, reader.IsFloat) switch {
            (16, _) => WavEncoding.Pcm16,
            (24, _) => WavEncoding.Pcm24,
            (32, true) => WavEncoding.Float32,
            _ => WavEncoding.Pcm32
        };
        var writers = new List<(WavWriter Writer, int[] Tracks, float[] Buffer)>();
        var beds = new List<AdmBed>();
        var objects = new List<AdmObject>();
        try {
            var index = 0;
            foreach (var item in plan) {
                var file = Path.Combine(mediaFolder, $"{++index:00} {Sanitise(item.Name)}.wav");
                writers.Add((new(file, item.Tracks.Length, reader.SampleRate, encoding), item.Tracks, new float[Block * item.Tracks.Length]));
                if (item.Bed is { } format) {
                    beds.Add(new(item.Name, format, file));
                } else {
                    objects.Add(new(item.Name, file, item.Tracks.Length, item.Blocks!));
                }
            }

            var interleaved = new float[Block * reader.Channels];
            int frames;
            while ((frames = reader.Read(interleaved)) > 0) {
                foreach (var (writer, channels, buffer) in writers) {
                    for (var f = 0; f < frames; f++) {
                        for (var c = 0; c < channels.Length; c++) {
                            buffer[f * channels.Length + c] = channels[c] < 0 ? 0 : interleaved[f * reader.Channels + channels[c]];
                        }
                    }

                    writer.Write(buffer.AsSpan(0, frames * channels.Length));
                }
            }
        } finally {
            foreach (var (writer, _, _) in writers) {
                writer.Dispose();
            }
        }

        return new(adm.Name ?? Path.GetFileNameWithoutExtension(path), reader.SampleRate, reader.Frames, beds, objects);
    }

    static string Sanitise(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([..name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c)]).Trim();
        return clean.Length == 0 ? "Untitled" : clean[..Math.Min(clean.Length, 60)];
    }

    /// <summary>The <c>chna</c> rows: UID → (0-based track, track format or channel format, pack).</summary>
    static Dictionary<string, (int Track, string TrackFormat, string? Pack)> Chna(byte[] body) {
        if (body.Length < 4) {
            throw new FormatException("The chna chunk is cut short.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
        if (body.Length < 4 + 40 * count) {
            throw new FormatException("The chna chunk is cut short.");
        }

        var rows = new Dictionary<string, (int, string, string?)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++) {
            var row = body.AsSpan(4 + 40 * i, 40);
            var track = BinaryPrimitives.ReadUInt16LittleEndian(row);
            var uid = Encoding.ASCII.GetString(row.Slice(2, 12)).TrimEnd('\0', ' ');
            var trackFormat = Encoding.ASCII.GetString(row.Slice(14, 14)).TrimEnd('\0', ' ');
            var pack = Encoding.ASCII.GetString(row.Slice(28, 11)).TrimEnd('\0', ' ');
            if (trackFormat.StartsWith("AC_", StringComparison.OrdinalIgnoreCase) && trackFormat.EndsWith("_00", StringComparison.Ordinal)) {
                trackFormat = trackFormat[..^3];
            }

            if (track == 0 || uid.Length == 0) {
                continue;
            }

            rows[uid] = (track - 1, trackFormat, pack.Length == 0 ? null : pack);
        }

        return rows;
    }

    /// <summary>A bed or object to extract: its name, its tracks (−1 silent) in output order, and its layout or blocks.</summary>
    sealed record Item(string Name, int[] Tracks, ChannelFormat? Bed, IReadOnlyList<AdmBlock>? Blocks);

    /// <summary>The ADM elements by ID, local names regardless of namespace.</summary>
    sealed class Model {
        readonly Dictionary<string, XElement> byId = new(StringComparer.OrdinalIgnoreCase);
        readonly XElement root;
        readonly Dictionary<string, (int Track, string TrackFormat, string? Pack)> chna;
        readonly int rate;
        readonly long frames;

        public Model(XDocument document, Dictionary<string, (int, string, string?)> chna, int rate, long frames) {
            this.chna = chna;
            this.rate = rate;
            this.frames = frames;
            root = document.Descendants().FirstOrDefault(static e => e.Name.LocalName == "audioFormatExtended")
                   ?? throw new FormatException("The axml chunk has no audioFormatExtended element.");
            foreach (var element in root.Elements()) {
                var id = element.Attributes()
                    .FirstOrDefault(static a => a.Name.LocalName.EndsWith("ID", StringComparison.Ordinal) || a.Name.LocalName == "UID");
                if (id is not null) {
                    byId[id.Value] = element;
                }
            }
        }

        public string? Name { get; private set; }

        static IEnumerable<string> Refs(XElement element, string name) =>
            element.Elements().Where(e => e.Name.LocalName == name).Select(static e => e.Value.Trim());

        static string? Attribute(XElement element, string name) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

        static IEnumerable<XElement> Children(XElement element, string name) =>
            element.Elements().Where(e => e.Name.LocalName == name);

        public List<Item> Plan(int channels) {
            var programme = Children(root, "audioProgramme").FirstOrDefault();
            List<string> objectIds;
            if (programme is not null) {
                Name = Attribute(programme, "audioProgrammeName");
                objectIds = [
                    ..Refs(programme, "audioContentIDRef")
                        .SelectMany(c => byId.TryGetValue(c, out var content)
                            ? Refs(content, "audioObjectIDRef")
                            : throw new FormatException($"audioContent {c} is not in the axml chunk."))
                ];
            } else {
                var all = Children(root, "audioObject").ToList();
                var nested = all.SelectMany(static o => Refs(o, "audioObjectIDRef")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                objectIds = [..all.Select(o => Attribute(o, "audioObjectID")!).Where(id => !nested.Contains(id))];
            }

            var items = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in objectIds) {
                Add(id, items, seen, channels);
            }

            if (items.Count == 0) {
                throw new FormatException("The ADM has no beds or objects Anode can place.");
            }

            return items;
        }

        void Add(string id, List<Item> items, HashSet<string> seen, int channels) {
            if (!seen.Add(id)) {
                return;
            }

            if (!byId.TryGetValue(id, out var element)) {
                throw new FormatException($"audioObject {id} is not in the axml chunk.");
            }

            foreach (var child in Refs(element, "audioObjectIDRef")) {
                Add(child, items, seen, channels);
            }

            var uids = Refs(element, "audioTrackUIDRef").ToList();
            if (uids.Count == 0) {
                return;
            }

            var name = Attribute(element, "audioObjectName") ?? id;
            var start = Attribute(element, "start") is { } s ? AdmDefinitions.Frames(s, rate) : 0;
            var resolved = uids.Select(u => Track(u, channels)).ToList();
            var packs = resolved.Select(static r => r.Pack).Where(static p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pack = packs.Count > 0 ? packs[0]! : Refs(element, "audioPackFormatIDRef").FirstOrDefault()
                ?? throw new FormatException($"audioObject {id} has no pack format.");
            var type = PackType(pack);
            if (type == "0001") {
                items.Add(Bed(name, resolved));
            } else if (type == "0003") {
                if (resolved.Count is not (1 or 2)) {
                    throw new FormatException($"Object '{name}' has {resolved.Count} channels; Anode takes 1 or 2.");
                }

                var channel = byId.GetValueOrDefault(resolved[0].Channel)
                              ?? throw new FormatException($"Object '{name}''s channel format is not in the axml chunk.");
                items.Add(new(name, [..resolved.Select(static r => r.Track)], null, Blocks(channel, start)));
            } else {
                throw new FormatException($"audioObject {id}: pack type {type} (only DirectSpeakers and Objects).");
            }
        }

        /// <summary>A track UID's file track, channel format and pack.</summary>
        (int Track, string Channel, string? Pack) Track(string uid, int channels) {
            if (uid.Equals("ATU_00000000", StringComparison.OrdinalIgnoreCase)) {
                return (-1, "", null);
            }

            string? trackFormat = null;
            string? pack = null;
            var track = -1;
            if (chna.TryGetValue(uid, out var row)) {
                (track, trackFormat, pack) = row;
            }

            if (byId.TryGetValue(uid, out var element)) {
                trackFormat ??= Refs(element, "audioTrackFormatIDRef").FirstOrDefault() ?? Refs(element, "audioChannelFormatIDRef").FirstOrDefault();
                pack ??= Refs(element, "audioPackFormatIDRef").FirstOrDefault();
            }

            if (track < 0 || track >= channels || trackFormat is null) {
                throw new FormatException($"audioTrackUID {uid} is not a track of the file (chna).");
            }

            return (track, ChannelOf(trackFormat), pack);
        }

        /// <summary>The channel format of a track format (through its stream format, or BS.2094's numbering), or of itself.</summary>
        string ChannelOf(string trackFormat) {
            if (trackFormat.StartsWith("AC_", StringComparison.OrdinalIgnoreCase)) {
                return trackFormat;
            }

            if (byId.TryGetValue(trackFormat, out var track)) {
                var stream = Refs(track, "audioStreamFormatIDRef").FirstOrDefault();
                if (stream is not null && byId.TryGetValue(stream, out var streamElement)) {
                    var channel = Refs(streamElement, "audioChannelFormatIDRef").FirstOrDefault();
                    if (channel is not null) {
                        return channel;
                    }
                }
            }

            // BS.2094: AT_yyyyxxxx_01 belongs to AS_yyyyxxxx and AC_yyyyxxxx.
            if (trackFormat.Length >= 11 && trackFormat.StartsWith("AT_", StringComparison.OrdinalIgnoreCase)) {
                return $"AC_{trackFormat[3..11]}";
            }

            throw new FormatException($"Track format {trackFormat} cannot be resolved.");
        }

        string PackType(string pack) {
            if (byId.TryGetValue(pack, out var element) && Attribute(element, "typeLabel") is { } label) {
                return label;
            }

            return pack.Length >= 7 && pack.StartsWith("AP_", StringComparison.OrdinalIgnoreCase)
                ? pack[3..7]
                : throw new FormatException($"Pack format {pack} cannot be resolved.");
        }

        Item Bed(string name, List<(int Track, string Channel, string? Pack)> tracks) {
            var labels = tracks.Select(t => Label(t.Channel)).ToList();
            foreach (var layout in SpeakerLayouts.All) {
                if (layout.ChannelCount != labels.Count
                    || !layout.Speakers.Select(static s => s.Label).Order().SequenceEqual(labels.Order())) {
                    continue;
                }

                var order = layout.Speakers.Select(sp => tracks[labels.IndexOf(sp.Label)].Track).ToArray();
                return new(name, order, layout.Format, null);
            }

            throw new FormatException($"Bed '{name}' ({string.Join(' ', labels)}) is not one of Anode's layouts.");
        }

        string Label(string channel) {
            if (byId.TryGetValue(channel, out var element)) {
                var block = Children(element, "audioBlockFormat").FirstOrDefault();
                var label = block is null ? null : Children(block, "speakerLabel").FirstOrDefault()?.Value.Trim();
                if (label is not null) {
                    return AdmDefinitions.NormaliseLabel(label);
                }
            }

            return AdmDefinitions.LabelOfChannel(channel)
                   ?? throw new FormatException($"Channel format {channel} has no speaker label Anode knows.");
        }

        List<AdmBlock> Blocks(XElement channel, long start) {
            var blocks = new List<AdmBlock>();
            foreach (var block in Children(channel, "audioBlockFormat")) {
                var rtime = Attribute(block, "rtime") is { } r ? AdmDefinitions.Frames(r, rate) : 0;
                var duration = Attribute(block, "duration") is { } d ? AdmDefinitions.Frames(d, rate) : -1;
                if (Children(block, "cartesian").FirstOrDefault()?.Value.Trim() == "1") {
                    throw new FormatException("Cartesian object positions are not supported.");
                }

                double? azimuth = null, elevation = null;
                var distance = 1.0;
                foreach (var position in Children(block, "position")) {
                    var value = double.Parse(position.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                    switch (Attribute(position, "coordinate")) {
                        case "azimuth":
                            azimuth = value;
                            break;
                        case "elevation":
                            elevation = value;
                            break;
                        case "distance":
                            distance = value;
                            break;
                        default:
                            throw new FormatException("Cartesian object positions are not supported.");
                    }
                }

                if (azimuth is null || elevation is null) {
                    throw new FormatException("An object block has no polar position.");
                }

                double Number(string name) =>
                    Children(block, name).FirstOrDefault() is { } e
                        ? double.Parse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
                        : 0;

                var jump = Children(block, "jumpPosition").FirstOrDefault()?.Value.Trim() == "1";
                blocks.Add(
                    new(
                        start + rtime,
                        duration,
                        azimuth.Value,
                        elevation.Value,
                        distance,
                        Math.Clamp(Math.Max(Number("width"), Number("height")) / 360, 0, 1),
                        !jump
                    )
                );
            }

            if (blocks.Count == 0) {
                throw new FormatException("An object has no block formats.");
            }

            // Contiguous from 0: a missing duration runs to the next block (or the end), a late start holds the first
            // position from 0.
            blocks.Sort(static (a, b) => a.StartFrame.CompareTo(b.StartFrame));
            for (var b = 0; b < blocks.Count; b++) {
                var end = b + 1 < blocks.Count ? blocks[b + 1].StartFrame : Math.Max(frames, blocks[b].StartFrame + 1);
                if (blocks[b].DurationFrames < 0 || b + 1 < blocks.Count) {
                    blocks[b] = blocks[b] with { DurationFrames = Math.Max(1, end - blocks[b].StartFrame) };
                }
            }

            if (blocks[0].StartFrame > 0) {
                blocks.Insert(0, blocks[0] with { StartFrame = 0, DurationFrames = blocks[0].StartFrame, Interpolate = false });
            }

            return blocks;
        }
    }
}
