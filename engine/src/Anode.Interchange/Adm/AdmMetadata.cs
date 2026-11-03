using Anode.Contracts;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Anode.Interchange.Adm;

/// <summary>
///     The <c>axml</c> and <c>chna</c> of a scene, as <see cref="AdmWriter" /> documents them: a programme and content,
///     an object per bed and per object, the beds' packs, the objects' packs, channel, stream and track formats, and a
///     track UID per track.
/// </summary>
sealed class AdmMetadata {
    static readonly XNamespace Ns = AdmDefinitions.Namespace;
    readonly AdmScene scene;
    readonly int bitDepth;
    readonly int rate;
    readonly XElement content;
    readonly List<XElement> objects = [];
    readonly List<XElement> formats = [];
    readonly List<XElement> uids = [];
    readonly List<(int Track, string Uid, string TrackFormat, string Pack)> chna = [];
    int track;
    int objectNumber = 0x1000;
    int customPack = 0x1000;
    int objectPack = 0x1000;
    int objectChannel = 0x1000;

    public AdmMetadata(AdmScene scene, int bitDepth) {
        this.scene = scene;
        this.bitDepth = bitDepth;
        rate = scene.SampleRate;
        content = new(
            Ns + "audioContent",
            new XAttribute("audioContentID", "ACO_1001"),
            new XAttribute("audioContentName", scene.Name)
        );
    }

    public (byte[] Axml, byte[] Chna, int Channels) Build() {
        foreach (var bed in scene.Beds) {
            AddBed(bed);
        }

        foreach (var o in scene.Objects) {
            AddObject(o);
        }

        var programme = new XElement(
            Ns + "audioProgramme",
            new XAttribute("audioProgrammeID", "APR_1001"),
            new XAttribute("audioProgrammeName", scene.Name),
            new XAttribute("start", AdmDefinitions.Time(0, rate)),
            new XAttribute("end", AdmDefinitions.Time(scene.Frames, rate)),
            new XElement(Ns + "audioContentIDRef", "ACO_1001")
        );
        var afx = new XElement(
            Ns + "audioFormatExtended",
            new XAttribute("version", AdmDefinitions.Version),
            programme,
            content,
            objects,
            formats,
            uids
        );
        var document = new XDocument(
            new("1.0", "UTF-8", null),
            new XElement(
                Ns + "ebuCoreMain",
                new XAttribute("version", "1.9"),
                new XElement(Ns + "coreMetadata", new XElement(Ns + "format", afx))
            )
        );
        using var memory = new MemoryStream();
        using (var xml = XmlWriter.Create(memory, new() { Encoding = new UTF8Encoding(false), Indent = true })) {
            document.Save(xml);
        }

        return (memory.ToArray(), Chna(chna), track);
    }

    XElement NewObject(string name, string pack) {
        var id = $"AO_{++objectNumber:x4}";
        content.Add(new XElement(Ns + "audioObjectIDRef", id));
        var element = new XElement(
            Ns + "audioObject",
            new XAttribute("audioObjectID", id),
            new XAttribute("audioObjectName", name),
            new XAttribute("start", AdmDefinitions.Time(0, rate)),
            new XAttribute("duration", AdmDefinitions.Time(scene.Frames, rate)),
            new XElement(Ns + "audioPackFormatIDRef", pack)
        );
        objects.Add(element);
        return element;
    }

    void AddTrack(XElement owner, string trackFormat, string pack) {
        track++;
        var uid = $"ATU_{track:x8}";
        owner.Add(new XElement(Ns + "audioTrackUIDRef", uid));
        uids.Add(
            new(
                Ns + "audioTrackUID",
                new XAttribute("UID", uid),
                new XAttribute("sampleRate", rate),
                new XAttribute("bitDepth", bitDepth),
                new XElement(Ns + "audioTrackFormatIDRef", trackFormat),
                new XElement(Ns + "audioPackFormatIDRef", pack)
            )
        );
        chna.Add((track, uid, trackFormat, pack));
    }

    void AddBed(AdmBed bed) {
        var layout = SpeakerLayouts.Of(bed.Format);
        if (!AdmDefinitions.PackByFormat.TryGetValue(bed.Format, out var pack)) {
            pack = $"AP_0001{++customPack:x4}";
            formats.Add(
                new(
                    Ns + "audioPackFormat",
                    new XAttribute("audioPackFormatID", pack),
                    new XAttribute("audioPackFormatName", $"Anode {layout.Name}"),
                    new XAttribute("typeLabel", "0001"),
                    new XAttribute("typeDefinition", "DirectSpeakers"),
                    layout.Speakers.Select(static s =>
                        new XElement(Ns + "audioChannelFormatIDRef", AdmDefinitions.ChannelByLabel[s.Label])
                    )
                )
            );
        }

        var owner = NewObject(bed.Name, pack);
        foreach (var speaker in layout.Speakers) {
            AddTrack(owner, $"AT_{AdmDefinitions.ChannelByLabel[speaker.Label][3..]}_01", pack);
        }
    }

    void AddObject(AdmObject o) {
        var pack = $"AP_0003{++objectPack:x4}";
        var packElement = new XElement(
            Ns + "audioPackFormat",
            new XAttribute("audioPackFormatID", pack),
            new XAttribute("audioPackFormatName", o.Name),
            new XAttribute("typeLabel", "0003"),
            new XAttribute("typeDefinition", "Objects")
        );
        formats.Add(packElement);
        var owner = NewObject(o.Name, pack);
        for (var c = 0; c < o.Channels; c++) {
            var number = $"0003{++objectChannel:x4}";
            var name = o.Channels == 1 ? o.Name : $"{o.Name} {(c == 0 ? "L" : "R")}";
            packElement.Add(new XElement(Ns + "audioChannelFormatIDRef", $"AC_{number}"));
            formats.Add(ChannelFormat(Ns, number, name, o.Blocks, rate));
            formats.Add(
                new(
                    Ns + "audioStreamFormat",
                    new XAttribute("audioStreamFormatID", $"AS_{number}"),
                    new XAttribute("audioStreamFormatName", $"PCM_{name}"),
                    new XAttribute("formatLabel", "0001"),
                    new XAttribute("formatDefinition", "PCM"),
                    new XElement(Ns + "audioChannelFormatIDRef", $"AC_{number}"),
                    new XElement(Ns + "audioTrackFormatIDRef", $"AT_{number}_01")
                )
            );
            formats.Add(
                new(
                    Ns + "audioTrackFormat",
                    new XAttribute("audioTrackFormatID", $"AT_{number}_01"),
                    new XAttribute("audioTrackFormatName", $"PCM_{name}"),
                    new XAttribute("formatLabel", "0001"),
                    new XAttribute("formatDefinition", "PCM"),
                    new XElement(Ns + "audioStreamFormatIDRef", $"AS_{number}")
                )
            );
            AddTrack(owner, $"AT_{number}_01", pack);
        }
    }

    static XElement ChannelFormat(XNamespace ns, string number, string name, IReadOnlyList<AdmBlock> blocks, int rate) {
        var channel = new XElement(
            ns + "audioChannelFormat",
            new XAttribute("audioChannelFormatID", $"AC_{number}"),
            new XAttribute("audioChannelFormatName", name),
            new XAttribute("typeLabel", "0003"),
            new XAttribute("typeDefinition", "Objects")
        );
        for (var b = 0; b < blocks.Count; b++) {
            var block = blocks[b];
            var extent = 360 * Math.Clamp(block.Size, 0, 1);
            var element = new XElement(
                ns + "audioBlockFormat",
                new XAttribute("audioBlockFormatID", $"AB_{number}_{b + 1:x8}"),
                new XAttribute("rtime", AdmDefinitions.Time(block.StartFrame, rate)),
                new XAttribute("duration", AdmDefinitions.Time(block.DurationFrames, rate)),
                Position(ns, "azimuth", block.Azimuth),
                Position(ns, "elevation", block.Elevation),
                Position(ns, "distance", Math.Max(0, block.Distance)),
                new XElement(ns + "width", Number(extent)),
                new XElement(ns + "height", Number(extent)),
                new XElement(ns + "depth", "0")
            );
            if (!block.Interpolate) {
                element.Add(new XElement(ns + "jumpPosition", new XAttribute("interpolationLength", "0"), "1"));
            }

            channel.Add(element);
        }

        return channel;
    }

    static XElement Position(XNamespace ns, string coordinate, double value) =>
        new(ns + "position", new XAttribute("coordinate", coordinate), Number(value));

    static string Number(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The <c>chna</c> body (BS.2088): track count, UID count, then 40 bytes per UID.</summary>
    static byte[] Chna(List<(int Track, string Uid, string TrackFormat, string Pack)> rows) {
        var bytes = new byte[4 + 40 * rows.Count];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(
            span,
            (ushort)rows.Select(static r => r.Track).Distinct().Count()
        );
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)rows.Count);
        for (var i = 0; i < rows.Count; i++) {
            var row = span.Slice(4 + 40 * i, 40);
            BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)rows[i].Track);
            Encoding.ASCII.GetBytes(rows[i].Uid, row.Slice(2, 12));
            Encoding.ASCII.GetBytes(rows[i].TrackFormat, row.Slice(14, 14));
            Encoding.ASCII.GetBytes(rows[i].Pack, row.Slice(28, 11));
        }

        return bytes;
    }
}
