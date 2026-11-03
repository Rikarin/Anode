using Anode.Contracts;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Anode.Interchange.Adm;

/// <summary>
///     ADM BWF writer (docs/11-phase-10.md, workstream D): BW64 (RIFF/WAVE below 4 GB; <c>BW64</c> with <c>ds64</c>
///     above, ITU-R BS.2088) with the beds' and objects' channels interleaved in scene order, a <c>chna</c> chunk mapping
///     each track to its <c>audioTrackUID</c>, and an <c>axml</c> chunk with the ADM (BS.2076): a programme, content,
///     an <c>audioObject</c> per bed and per object, pack formats (common definitions for the beds' layouts), channel
///     formats with the objects' block formats. 24-bit PCM or 32-bit float.
/// </summary>
/// <remarks>
///     The ADM (docs/spatial/README.md, "ADM BWF"): BS.2076-2 inside <c>ebuCoreMain</c>; one <c>audioProgramme</c>
///     (<c>APR_1001</c>) and <c>audioContent</c> (<c>ACO_1001</c>) holding every <c>audioObject</c> (<c>AO_1001</c>…, beds
///     first); a bed references its layout's BS.2094 pack (<c>AP_00010017</c> for 7.1.4) or, for quad, 7.0, 7.1.2 and
///     9.1.6, an Anode pack (<c>AP_00011001</c>…) of BS.2094's channel formats, whose stream and track formats the common
///     definitions hold too, so the file does not repeat them; an object has its pack (<c>AP_00031001</c>…), channel
///     formats (<c>AC_00031001</c>…, one per channel, the same blocks), stream and track formats; every track an
///     <c>audioTrackUID</c> (<c>ATU_00000001</c>… in file order) here and in <c>chna</c>. Block times use BS.2076-2's
///     sample-exact fractional format. Sizes: <c>width</c> = <c>height</c> = 360° × size, <c>depth</c> 0.
/// </remarks>
public static class AdmWriter {
    const int Block = 4096;

    /// <summary>Writes <paramref name="scene" /> to <paramref name="path" />; the sources must match its rate and length.</summary>
    public static void Write(AdmScene scene, string path, int bitDepth = 24) => Write(scene, path, bitDepth, false);

    /// <summary>As <see cref="Write(AdmScene, string, int)" />; <paramref name="forceDs64" /> writes BW64 with ds64 at any size (tests).</summary>
    internal static void Write(AdmScene scene, string path, int bitDepth, bool forceDs64) {
        if (bitDepth is not (24 or 32)) {
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "24 (PCM) or 32 (float).");
        }

        Validate(scene);
        var sources = new List<Bw64Reader>();
        try {
            foreach (var bed in scene.Beds) {
                sources.Add(Source(bed.AudioPath, SpeakerLayouts.ChannelCount(bed.Format), scene));
            }

            foreach (var o in scene.Objects) {
                sources.Add(Source(o.AudioPath, o.Channels, scene));
            }

            var (axml, chna, channels) = Metadata(scene, bitDepth);
            using var writer = new Bw64Writer(path, channels, scene.SampleRate, bitDepth, [("chna", chna), ("axml", axml)], forceDs64);
            var output = new float[Block * channels];
            var inputs = sources.Select(static s => new float[Block * s.Channels]).ToArray();
            for (long done = 0; done < scene.Frames;) {
                var frames = (int)Math.Min(Block, scene.Frames - done);
                var at = 0;
                for (var s = 0; s < sources.Count; s++) {
                    var source = sources[s];
                    var input = inputs[s];
                    var read = 0;
                    while (read < frames) {
                        var n = source.Read(input.AsSpan(read * source.Channels, (frames - read) * source.Channels));
                        if (n == 0) {
                            throw new InvalidDataException("A source ended before the scene's length.");
                        }

                        read += n;
                    }

                    for (var f = 0; f < frames; f++) {
                        for (var c = 0; c < source.Channels; c++) {
                            output[f * channels + at + c] = input[f * source.Channels + c];
                        }
                    }

                    at += source.Channels;
                }

                writer.Write(output.AsSpan(0, frames * channels));
                done += frames;
            }
        } finally {
            foreach (var source in sources) {
                source.Dispose();
            }
        }
    }

    static Bw64Reader Source(string path, int channels, AdmScene scene) {
        var reader = Bw64Reader.Open(path, true);
        if (reader.SampleRate != scene.SampleRate || reader.Channels != channels || reader.Frames != scene.Frames) {
            var found = $"{reader.Channels} channels at {reader.SampleRate} Hz, {reader.Frames} frames";
            reader.Dispose();
            throw new ArgumentException(
                $"{path}: {found}; the scene needs {channels} at {scene.SampleRate} Hz, {scene.Frames} frames."
            );
        }

        return reader;
    }

    static void Validate(AdmScene scene) {
        ArgumentOutOfRangeException.ThrowIfLessThan(scene.SampleRate, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(scene.Frames, 1);
        if (scene.Beds.Count + scene.Objects.Count == 0) {
            throw new ArgumentException("The scene has neither beds nor objects.", nameof(scene));
        }

        foreach (var o in scene.Objects) {
            if (o.Channels is not (1 or 2)) {
                throw new ArgumentException($"Object '{o.Name}' has {o.Channels} channels (1 or 2).", nameof(scene));
            }

            if (o.Blocks.Count == 0 || o.Blocks[0].StartFrame != 0) {
                throw new ArgumentException($"Object '{o.Name}''s blocks must start at frame 0.", nameof(scene));
            }

            for (var b = 0; b < o.Blocks.Count; b++) {
                var block = o.Blocks[b];
                if (block.DurationFrames <= 0
                    || b + 1 < o.Blocks.Count && o.Blocks[b + 1].StartFrame != block.StartFrame + block.DurationFrames) {
                    throw new ArgumentException($"Object '{o.Name}''s blocks must be contiguous.", nameof(scene));
                }
            }
        }
    }

    /// <summary>The <c>axml</c> and <c>chna</c> chunks' bodies and the file's channel count.</summary>
    internal static (byte[] Axml, byte[] Chna, int Channels) Metadata(AdmScene scene, int bitDepth) {
        XNamespace ns = AdmDefinitions.Namespace;
        var rate = scene.SampleRate;
        var afx = new XElement(ns + "audioFormatExtended", new XAttribute("version", AdmDefinitions.Version));
        var programme = new XElement(
            ns + "audioProgramme",
            new XAttribute("audioProgrammeID", "APR_1001"),
            new XAttribute("audioProgrammeName", scene.Name),
            new XAttribute("start", AdmDefinitions.Time(0, rate)),
            new XAttribute("end", AdmDefinitions.Time(scene.Frames, rate)),
            new XElement(ns + "audioContentIDRef", "ACO_1001")
        );
        var content = new XElement(
            ns + "audioContent",
            new XAttribute("audioContentID", "ACO_1001"),
            new XAttribute("audioContentName", scene.Name)
        );
        afx.Add(programme, content);
        var objects = new List<XElement>();
        var formats = new List<XElement>();
        var uids = new List<XElement>();
        var chna = new List<(int Track, string Uid, string TrackFormat, string Pack)>();
        var track = 0;
        var objectNumber = 0x1000;

        XElement NewObject(string name, string pack) {
            var id = $"AO_{++objectNumber:x4}";
            content.Add(new XElement(ns + "audioObjectIDRef", id));
            var element = new XElement(
                ns + "audioObject",
                new XAttribute("audioObjectID", id),
                new XAttribute("audioObjectName", name),
                new XAttribute("start", AdmDefinitions.Time(0, rate)),
                new XAttribute("duration", AdmDefinitions.Time(scene.Frames, rate)),
                new XElement(ns + "audioPackFormatIDRef", pack)
            );
            objects.Add(element);
            return element;
        }

        void AddTrack(XElement owner, string trackFormat, string pack) {
            track++;
            var uid = $"ATU_{track:x8}";
            owner.Add(new XElement(ns + "audioTrackUIDRef", uid));
            uids.Add(
                new XElement(
                    ns + "audioTrackUID",
                    new XAttribute("UID", uid),
                    new XAttribute("sampleRate", rate),
                    new XAttribute("bitDepth", bitDepth),
                    new XElement(ns + "audioTrackFormatIDRef", trackFormat),
                    new XElement(ns + "audioPackFormatIDRef", pack)
                )
            );
            chna.Add((track, uid, trackFormat, pack));
        }

        var customPack = 0x1000;
        foreach (var bed in scene.Beds) {
            var layout = SpeakerLayouts.Of(bed.Format);
            if (!AdmDefinitions.PackByFormat.TryGetValue(bed.Format, out var pack)) {
                pack = $"AP_0001{++customPack:x4}";
                var element = new XElement(
                    ns + "audioPackFormat",
                    new XAttribute("audioPackFormatID", pack),
                    new XAttribute("audioPackFormatName", $"Anode {layout.Name}"),
                    new XAttribute("typeLabel", "0001"),
                    new XAttribute("typeDefinition", "DirectSpeakers")
                );
                foreach (var speaker in layout.Speakers) {
                    element.Add(new XElement(ns + "audioChannelFormatIDRef", AdmDefinitions.ChannelByLabel[speaker.Label]));
                }

                formats.Add(element);
            }

            var owner = NewObject(bed.Name, pack);
            foreach (var speaker in layout.Speakers) {
                var channel = AdmDefinitions.ChannelByLabel[speaker.Label];
                AddTrack(owner, $"AT_{channel[3..]}_01", pack);
            }
        }

        var objectChannel = 0x1000;
        var objectPack = 0x1000;
        foreach (var o in scene.Objects) {
            var pack = $"AP_0003{++objectPack:x4}";
            var packElement = new XElement(
                ns + "audioPackFormat",
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
                packElement.Add(new XElement(ns + "audioChannelFormatIDRef", $"AC_{number}"));
                formats.Add(ChannelFormat(ns, number, name, o.Blocks, rate));
                formats.Add(
                    new XElement(
                        ns + "audioStreamFormat",
                        new XAttribute("audioStreamFormatID", $"AS_{number}"),
                        new XAttribute("audioStreamFormatName", $"PCM_{name}"),
                        new XAttribute("formatLabel", "0001"),
                        new XAttribute("formatDefinition", "PCM"),
                        new XElement(ns + "audioChannelFormatIDRef", $"AC_{number}"),
                        new XElement(ns + "audioTrackFormatIDRef", $"AT_{number}_01")
                    )
                );
                formats.Add(
                    new XElement(
                        ns + "audioTrackFormat",
                        new XAttribute("audioTrackFormatID", $"AT_{number}_01"),
                        new XAttribute("audioTrackFormatName", $"PCM_{name}"),
                        new XAttribute("formatLabel", "0001"),
                        new XAttribute("formatDefinition", "PCM"),
                        new XElement(ns + "audioStreamFormatIDRef", $"AS_{number}")
                    )
                );
                AddTrack(owner, $"AT_{number}_01", pack);
            }
        }

        afx.Add(objects, formats, uids);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                ns + "ebuCoreMain",
                new XAttribute("version", "1.9"),
                new XElement(ns + "coreMetadata", new XElement(ns + "format", afx))
            )
        );
        using var memory = new MemoryStream();
        using (var xml = XmlWriter.Create(memory, new() { Encoding = new UTF8Encoding(false), Indent = true })) {
            document.Save(xml);
        }

        return (memory.ToArray(), Chna(chna), track);
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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)rows.Select(static r => r.Track).Distinct().Count());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)rows.Count);
        for (var i = 0; i < rows.Count; i++) {
            var row = span.Slice(4 + 40 * i, 40);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(row, (ushort)rows[i].Track);
            Encoding.ASCII.GetBytes(rows[i].Uid, row.Slice(2, 12));
            Encoding.ASCII.GetBytes(rows[i].TrackFormat, row.Slice(14, 14));
            Encoding.ASCII.GetBytes(rows[i].Pack, row.Slice(28, 11));
        }

        return bytes;
    }
}

/// <summary>
///     Object blocks from positions over time (for <see cref="AdmMapping" />): breakpoints of the object panner's
///     automation become <see cref="AdmBlock" />s, contiguous from 0 to the scene's end, a ramp cut into blocks of at
///     most <see cref="MaxRampFrames" /> (20 ms) that interpolate, a hold (or a step) one block that jumps.
/// </summary>
public static class AdmBlocks {
    /// <summary>The longest interpolating block, in seconds (docs/11-phase-10.md).</summary>
    public const double MaxRampSeconds = 0.020;

    /// <summary>The longest interpolating block at <paramref name="sampleRate" />.</summary>
    public static long MaxRampFrames(int sampleRate) => (long)Math.Floor(MaxRampSeconds * sampleRate);

    /// <summary>
    ///     The blocks of an object whose position is <paramref name="points" /> (by frame, ascending; the first at or
    ///     before 0): each point's position holds until the next, or, with <c>Ramp</c>, moves linearly to the next
    ///     point's, over <paramref name="frames" /> at <paramref name="sampleRate" />.
    /// </summary>
    public static IReadOnlyList<AdmBlock> FromPoints(
        IReadOnlyList<(long Frame, double Azimuth, double Elevation, double Distance, double Size, bool Ramp)> points,
        long frames,
        int sampleRate
    ) {
        if (points.Count == 0) {
            return [new(0, frames, 0, 0, 1, 0, false)];
        }

        var step = Math.Max(1, MaxRampFrames(sampleRate));
        var blocks = new List<AdmBlock>();
        for (var p = 0; p < points.Count; p++) {
            var from = points[p];
            var start = Math.Max(0, from.Frame);
            var end = p + 1 < points.Count ? Math.Min(frames, points[p + 1].Frame) : frames;
            if (end <= start || start >= frames) {
                continue;
            }

            if (blocks.Count == 0 && start > 0) {
                blocks.Add(new(0, start, from.Azimuth, from.Elevation, from.Distance, from.Size, false));
            }

            if (!from.Ramp || p + 1 >= points.Count) {
                blocks.Add(new(start, end - start, from.Azimuth, from.Elevation, from.Distance, from.Size, false));
                continue;
            }

            // A ramp: first a zero-motion block would be needed to start at the point; ADM interpolates from the
            // previous block's position, which is this point's when the previous segment ended here.
            var to = points[p + 1];
            var span = (double)(to.Frame - from.Frame);
            if (blocks.Count == 0 || !SamePosition(blocks[^1], from)) {
                var first = Math.Min(end, start + 1);
                blocks.Add(new(start, first - start, from.Azimuth, from.Elevation, from.Distance, from.Size, false));
                start = first;
            }

            for (var at = start; at < end; at += step) {
                var stop = Math.Min(end, at + step);
                var t = (stop - from.Frame) / span;
                blocks.Add(
                    new(
                        at,
                        stop - at,
                        from.Azimuth + t * (to.Azimuth - from.Azimuth),
                        from.Elevation + t * (to.Elevation - from.Elevation),
                        from.Distance + t * (to.Distance - from.Distance),
                        from.Size + t * (to.Size - from.Size),
                        true
                    )
                );
            }
        }

        if (blocks.Count == 0) {
            var last = points[^1];
            blocks.Add(new(0, frames, last.Azimuth, last.Elevation, last.Distance, last.Size, false));
        }

        return blocks;
    }

    static bool SamePosition(
        AdmBlock block,
        (long Frame, double Azimuth, double Elevation, double Distance, double Size, bool Ramp) point
    ) =>
        Math.Abs(block.Azimuth - point.Azimuth) < 1e-9 && Math.Abs(block.Elevation - point.Elevation) < 1e-9
                                                    && Math.Abs(block.Distance - point.Distance) < 1e-9
                                                    && Math.Abs(block.Size - point.Size) < 1e-9;
}
