using Anode.Contracts;
using Anode.Interchange.Aaf.Cfb;
using Anode.Model;
using Anode.Model.Audio;
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Anode.Interchange.Aaf;

/// <summary>
///     AAF export (docs/10-phase-9.md, workstream E; docs/interchange/aaf.md): the arrangement's audio tracks as a
///     composition mob with a timeline slot per track (edit rate = the project's sample rate), source clips with offsets
///     and clip gain (Avid's mono audio gain operation group), fillers, crossfades as <c>Transition</c>s (mono audio
///     dissolve) and other fades as the source clips' <c>FadeInLength</c>/<c>FadeOutLength</c>, the markers as
///     <c>DescriptiveMarker</c>s on an event slot; a master mob and a file source mob with a PCM descriptor per audio
///     file, the essence embedded or linked by network locators to WAV files written beside it. MIDI is not exported
///     (SMF covers it).
/// </summary>
public static class AafWriter {
    /// <summary>
    ///     Writes <paramref name="request" />'s tracks of <paramref name="state" /> (a project in
    ///     <paramref name="projectFolder" />, whose files are relative to it) to <paramref name="path" />, and, with linked
    ///     media, the WAV files into <c>&lt;path without extension&gt; Media/</c>. Returns every file written, the AAF
    ///     first. Only audio tracks are written (others named in <c>trackIds</c> are skipped); muted regions are left out.
    /// </summary>
    public static IReadOnlyList<string> Write(
        ProjectState state,
        string projectFolder,
        AafExportRequest request,
        string path
    ) =>
        Write(state, projectFolder, request, path, DateTimeOffset.UtcNow, 4);

    internal static IReadOnlyList<string> Write(
        ProjectState state,
        string projectFolder,
        AafExportRequest request,
        string path,
        DateTimeOffset now,
        int cfbVersion
    ) {
        var rate = state.SampleRate;
        var plan = Plan(state, request);
        var written = new List<string> { path };
        var mediaFolder = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
            Path.GetFileNameWithoutExtension(path) + " Media"
        );
        var media = new Dictionary<string, Media>(StringComparer.Ordinal);
        foreach (var fileId in plan.SelectMany(static t => t.Clips).Select(static c => c.Region.FileId).Distinct()) {
            var file = state.Files.GetValueOrDefault(fileId)
                ?? throw new InvalidOperationException($"Region file {fileId} is not in the project.");
            var source = Path.GetFullPath(Path.Combine(projectFolder, file.Path));
            var info = WavReader.Probe(source);
            var encoding = info.Encoding == WavEncoding.Float32 ? WavEncoding.Pcm24 : info.Encoding;
            var item = new Media(
                file,
                source,
                info,
                encoding,
                AafEncoding.MobId(Material(state.Id, "master", fileId)),
                AafEncoding.MobId(Material(state.Id, "file", fileId))
            );
            if (!request.EmbedMedia) {
                Directory.CreateDirectory(mediaFolder);
                item.LinkedPath = Unique(mediaFolder, Path.GetFileNameWithoutExtension(file.Path), written);
                WriteLinked(item);
                written.Add(item.LinkedPath);
            }

            media[fileId] = item;
        }

        var root = new AafObject(AafIds.Root)
            .Strong(AafPid.RootMetaDictionary, AafMetaModel.MetaDictionary())
            .Strong(AafPid.RootHeader, Header(state, plan, media, rate, now, request.EmbedMedia));
        var storage = AafStorageWriter.Build(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var temporary = path + ".partial";
        try {
            CfbWriter.Write(temporary, storage, cfbVersion);
            File.Move(temporary, path, true);
        } finally {
            if (File.Exists(temporary)) {
                File.Delete(temporary);
            }
        }

        return written;
    }

    /// <summary>The tracks to write and their clips (unmuted regions, by start) in samples.</summary>
    static List<TrackPlan> Plan(ProjectState state, AafExportRequest request) {
        var wanted = request.TrackIds is { } ids ? new HashSet<string>(ids, StringComparer.Ordinal) : null;
        var map = state.TempoMap;
        var plan = new List<TrackPlan>();
        foreach (var track in state.Tracks) {
            if (track.Kind != TrackKind.Audio || (wanted is not null && !wanted.Contains(track.Id))) {
                continue;
            }

            var clips = state.RegionsOf(track.Id)
                .Where(static r => !r.Muted && r.Length > 0)
                .Select(r => new ClipPlan(r, map.SampleAt(r.StartTick, state.SampleRate)))
                .OrderBy(static c => c.Position)
                .ToList();
            if (wanted is null && clips.Count == 0) {
                continue;
            }

            plan.Add(new(track, clips));
        }

        return plan;
    }

    static AafObject Header(
        ProjectState state,
        List<TrackPlan> plan,
        Dictionary<string, Media> media,
        int rate,
        DateTimeOffset now,
        bool embed
    ) {
        var stamp = AafEncoding.TimeStamp(now);
        var mobs = new List<(byte[], AafObject)> {
            (AafEncoding.MobId(Material(state.Id, "composition", string.Empty)),
                Composition(state, plan, media, rate, stamp))
        };
        var essence = new List<(byte[], AafObject)>();
        foreach (var item in media.Values) {
            mobs.Add((item.MasterId, MasterMob(item, rate, stamp)));
            mobs.Add((item.FileId, FileMob(item, stamp, embed)));
            if (embed) {
                essence.Add((item.FileId, EssenceData(item)));
            }
        }

        var content = new AafObject(AafIds.ContentStorage).Set(AafPid.Mobs, AafPid.MobId, mobs);
        if (essence.Count > 0) {
            content.Set(AafPid.EssenceDataSet, AafPid.EssenceMobId, essence);
        }

        return new AafObject(AafIds.Header)
            .Data(AafPid.ByteOrder, AafEncoding.Int16(0x4949))
            .Data(AafPid.LastModified, stamp)
            .Strong(AafPid.Content, content)
            .Strong(AafPid.HeaderDictionary, Dictionary(embed))
            .Data(AafPid.Version, [1, 1])
            .Vector(AafPid.IdentificationList, [Identification(stamp)])
            .Data(AafPid.ObjectModelVersion, AafEncoding.UInt32(1))
            .Data(AafPid.OperationalPattern, AafEncoding.Auid(AafIds.EditProtocol));
    }

    static AafObject Identification(byte[] stamp) {
        var version = typeof(AafWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0";
        return new AafObject(AafIds.Identification)
            .Data(AafPid.CompanyName, AafEncoding.String("Anode contributors"))
            .Data(AafPid.ProductName, AafEncoding.String("Anode"))
            .Data(AafPid.ProductVersionString, AafEncoding.String(version))
            .Data(AafPid.ProductId, AafEncoding.Auid(AafIds.ProductId))
            .Data(AafPid.Date, stamp)
            .Data(AafPid.Platform, AafEncoding.String("Anode " + Environment.OSVersion.Platform))
            .Data(AafPid.GenerationAuid, AafEncoding.Auid(Guid.NewGuid()));
    }

    /// <summary>The definitions the file refers to: sound and descriptive data, gain and dissolve, amplitude, containers.</summary>
    static AafObject Dictionary(bool embed) {
        static AafObject Definition(Guid classId, Guid id, string name) =>
            new AafObject(classId).Data(AafPid.DefinitionId, AafEncoding.Auid(id))
                .Data(AafPid.DefinitionName, AafEncoding.String(name));

        var gain = Definition(AafIds.OperationDefinition, AafIds.MonoAudioGain, "Mono Audio Gain")
            .Weak(AafPid.OperationDataDefinition, AafIds.DataDefinitions, AafEncoding.Auid(AafIds.DataDefSound))
            .Data(AafPid.IsTimeWarp, AafEncoding.Bool(false))
            .Data(AafPid.OperationCategory, AafEncoding.Auid(AafIds.CategoryEffect))
            .Data(AafPid.NumberInputs, AafEncoding.Int32(1))
            .WeakSet(AafPid.ParametersDefined, AafIds.ParameterDefinitions, [AafEncoding.Auid(AafIds.Amplitude)]);
        var dissolve = Definition(AafIds.OperationDefinition, AafIds.MonoAudioDissolve, "Mono Audio Dissolve")
            .Weak(AafPid.OperationDataDefinition, AafIds.DataDefinitions, AafEncoding.Auid(AafIds.DataDefSound))
            .Data(AafPid.IsTimeWarp, AafEncoding.Bool(false))
            .Data(AafPid.OperationCategory, AafEncoding.Auid(AafIds.CategoryEffect))
            .Data(AafPid.NumberInputs, AafEncoding.Int32(2));
        var amplitude = Definition(AafIds.ParameterDefinition, AafIds.Amplitude, "Amplitude")
            .Weak(AafPid.ParameterType, AafIds.TypeDefinitions, AafEncoding.Auid(AafIds.TypeRational));
        var container = embed
            ? Definition(AafIds.ContainerDefinition, AafIds.ContainerAaf, "AAF")
            : Definition(AafIds.ContainerDefinition, AafIds.ContainerRiffWave, "RIFFWAVE");
        return new AafObject(AafIds.Dictionary)
            .Set(
                AafPid.OperationDefinitions,
                AafPid.DefinitionId,
                [
                    (AafEncoding.Auid(AafIds.MonoAudioGain), gain),
                    (AafEncoding.Auid(AafIds.MonoAudioDissolve), dissolve)
                ]
            )
            .Set(AafPid.ParameterDefinitions, AafPid.DefinitionId, [(AafEncoding.Auid(AafIds.Amplitude), amplitude)])
            .Set(
                AafPid.DataDefinitions,
                AafPid.DefinitionId,
                [
                    (AafEncoding.Auid(AafIds.DataDefSound),
                        Definition(AafIds.DataDefinition, AafIds.DataDefSound, "Sound")),
                    (AafEncoding.Auid(AafIds.DataDefDescriptiveMetadata),
                        Definition(AafIds.DataDefinition, AafIds.DataDefDescriptiveMetadata, "DescriptiveMetadata"))
                ]
            )
            .Set(
                AafPid.ContainerDefinitions,
                AafPid.DefinitionId,
                [(AafEncoding.Auid(embed ? AafIds.ContainerAaf : AafIds.ContainerRiffWave), container)]
            );
    }

    static AafObject Composition(
        ProjectState state,
        List<TrackPlan> plan,
        Dictionary<string, Media> media,
        int rate,
        byte[] stamp
    ) {
        var slots = new List<AafObject>();
        for (var i = 0; i < plan.Count; i++) {
            var (components, length) = Components(plan[i], media);
            var sequence = Component(AafIds.Sequence, AafIds.DataDefSound, length)
                .Vector(AafPid.Components, components);
            slots.Add(
                new AafObject(AafIds.TimelineMobSlot)
                    .Data(AafPid.SlotId, AafEncoding.UInt32((uint)(i + 1)))
                    .Data(AafPid.SlotName, AafEncoding.String(plan[i].Track.Name))
                    .Strong(AafPid.Segment, sequence)
                    .Data(AafPid.PhysicalTrackNumber, AafEncoding.UInt32((uint)(i + 1)))
                    .Data(AafPid.EditRate, AafEncoding.Rational(rate, 1))
                    .Data(AafPid.Origin, AafEncoding.Int64(0))
            );
        }

        var markers = state.Markers.Values.OrderBy(static m => m.Tick)
            .ThenBy(static m => m.Id, StringComparer.Ordinal)
            .ToList();
        if (markers.Count > 0) {
            var map = state.TempoMap;
            var events = markers.Select(m => {
                        var position = map.SampleAt(m.Tick, rate);
                        var marker = Component(
                                AafIds.DescriptiveMarker,
                                AafIds.DataDefDescriptiveMetadata,
                                map.SampleAt(m.Tick + Math.Max(0, m.LengthTicks), rate) - position
                            )
                            .Data(AafPid.Position, AafEncoding.Int64(position))
                            .Data(AafPid.Comment, AafEncoding.String(m.Name));
                        if (!string.IsNullOrEmpty(m.Text)) {
                            marker.Vector(AafPid.UserComments, [Tagged("Comment", m.Text)]);
                        }

                        return marker;
                    }
                )
                .ToList();
            var sequence = new AafObject(AafIds.Sequence)
                .Weak(
                    AafPid.DataDefinition,
                    AafIds.DataDefinitions,
                    AafEncoding.Auid(AafIds.DataDefDescriptiveMetadata)
                )
                .Vector(AafPid.Components, events);
            slots.Add(
                new AafObject(AafIds.EventMobSlot)
                    .Data(AafPid.SlotId, AafEncoding.UInt32((uint)(plan.Count + 1)))
                    .Data(AafPid.SlotName, AafEncoding.String("Markers"))
                    .Strong(AafPid.Segment, sequence)
                    .Data(AafPid.EventEditRate, AafEncoding.Rational(rate, 1))
            );
        }

        return new AafObject(AafIds.CompositionMob)
            .Data(AafPid.MobId, AafEncoding.MobId(Material(state.Id, "composition", string.Empty)))
            .Data(AafPid.MobName, AafEncoding.String(state.Name))
            .Vector(AafPid.Slots, slots)
            .Data(AafPid.MobLastModified, stamp)
            .Data(AafPid.CreationTime, stamp)
            .Data(AafPid.UsageCode, AafEncoding.Auid(AafIds.UsageTopLevel));
    }

    /// <summary>
    ///     A track's sequence: fillers for gaps, a clip (in a gain operation group when it has clip gain) per region,
    ///     and a transition where a region overlaps the one before (a crossfade); an overlap longer than a transition
    ///     may be (the later clip, or what the earlier one has left after its own incoming transition) shortens the
    ///     earlier clip. Returns the components and the sequence's length.
    /// </summary>
    static (List<AafObject> Components, long Length) Components(TrackPlan track, Dictionary<string, Media> media) {
        var (pieces, length) = Pieces(track);
        var components = pieces.Select(piece => piece switch {
                    { IsTransition: true } => Transition(piece.Length),
                    { Clip: null } => Component(AafIds.Filler, AafIds.DataDefSound, piece.Length),
                    _ => Clip(piece, media[piece.Clip.Region.FileId])
                }
            )
            .ToList();
        return (components, length);
    }

    /// <summary>The sequence's pieces (fillers, clips and transitions) and its length.</summary>
    static (List<Piece> Pieces, long Length) Pieces(TrackPlan track) {
        var pieces = new List<Piece>();
        long cursor = 0;
        Piece? previous = null;
        foreach (var clip in track.Clips) {
            var piece = new Piece(clip, clip.Region.Length);
            if (previous is not null && clip.Position < cursor) {
                Overlap(pieces, previous, piece, cursor - clip.Position);
            } else if (clip.Position > cursor) {
                pieces.Add(new(null, clip.Position - cursor));
            }

            pieces.Add(piece);
            cursor = clip.Position + piece.Length;
            previous = piece;
        }

        return (pieces, cursor);
    }

    /// <summary>A crossfade between two pieces as a transition, shortening the earlier piece where it must.</summary>
    static void Overlap(List<Piece> pieces, Piece previous, Piece piece, long overlap) {
        var allowed = Math.Min(piece.Length, previous.Length - previous.TransitionIn);
        if (overlap > allowed) {
            previous.Length -= overlap - allowed;
            overlap = allowed;
        }

        if (overlap > 0) {
            pieces.Add(new(null, overlap) { IsTransition = true });
            previous.TransitionOut = overlap;
            piece.TransitionIn = overlap;
        }
    }

    static AafObject Transition(long length) {
        var group = Component(AafIds.OperationGroup, AafIds.DataDefSound, length)
            .Weak(AafPid.Operation, AafIds.OperationDefinitions, AafEncoding.Auid(AafIds.MonoAudioDissolve));
        return Component(AafIds.Transition, AafIds.DataDefSound, length)
            .Strong(AafPid.TransitionOperationGroup, group)
            .Data(AafPid.CutPoint, AafEncoding.Int64(length / 2));
    }

    static AafObject Clip(Piece piece, Media media) {
        var region = piece.Clip!.Region;
        var clip = Component(AafIds.SourceClip, AafIds.DataDefSound, piece.Length)
            .Data(AafPid.SourceId, media.MasterId)
            .Data(AafPid.SourceMobSlotId, AafEncoding.UInt32(1))
            .Data(AafPid.StartTime, AafEncoding.Int64(region.FileOffset));
        if (piece.TransitionIn == 0 && region.FadeIn > 0) {
            clip.Data(AafPid.FadeInLength, AafEncoding.Int64(Math.Min(region.FadeIn, piece.Length)))
                .Data(AafPid.FadeInType, AafEncoding.UInt8(1));
        }

        if (piece.TransitionOut == 0 && region.FadeOut > 0) {
            clip.Data(AafPid.FadeOutLength, AafEncoding.Int64(Math.Min(region.FadeOut, piece.Length)))
                .Data(AafPid.FadeOutType, AafEncoding.UInt8(1));
        }

        if (region.GainDb == 0) {
            return clip;
        }

        var (numerator, denominator) = Amplitude(region.GainDb);
        var amplitude = new AafObject(AafIds.ConstantValue)
            .Data(AafPid.ParameterDefinition, AafEncoding.Auid(AafIds.Amplitude))
            .Data(
                AafPid.Value,
                AafEncoding.Indirect(AafIds.TypeRational, AafEncoding.Rational(numerator, denominator))
            );
        return Component(AafIds.OperationGroup, AafIds.DataDefSound, piece.Length)
            .Weak(AafPid.Operation, AafIds.OperationDefinitions, AafEncoding.Auid(AafIds.MonoAudioGain))
            .Vector(AafPid.InputSegments, [clip])
            .Set(AafPid.Parameters, AafPid.DefinitionId, [(AafEncoding.Auid(AafIds.Amplitude), amplitude)]);
    }

    /// <summary>A gain in dB as a linear amplitude ratio, with the largest power-of-two denominator that fits.</summary>
    internal static (int Numerator, int Denominator) Amplitude(double db) {
        var linear = Math.Pow(10, db / 20);
        var denominator = 1 << 30;
        while (denominator > 1 && linear * denominator > int.MaxValue) {
            denominator >>= 1;
        }

        return ((int)Math.Min(int.MaxValue, Math.Round(linear * denominator)), denominator);
    }

    static AafObject MasterMob(Media media, int rate, byte[] stamp) {
        var clip = Component(AafIds.SourceClip, AafIds.DataDefSound, media.Info.Frames)
            .Data(AafPid.SourceId, media.FileId)
            .Data(AafPid.SourceMobSlotId, AafEncoding.UInt32(1))
            .Data(AafPid.StartTime, AafEncoding.Int64(0));
        return new AafObject(AafIds.MasterMob)
            .Data(AafPid.MobId, media.MasterId)
            .Data(AafPid.MobName, AafEncoding.String(media.File.Name))
            .Vector(AafPid.Slots, [Slot(clip, media.Info.SampleRate)])
            .Data(AafPid.MobLastModified, stamp)
            .Data(AafPid.CreationTime, stamp);
    }

    static AafObject FileMob(Media media, byte[] stamp, bool embed) {
        var info = media.Info;
        var bits = Bits(media.Encoding);
        var descriptor = new AafObject(AafIds.PcmDescriptor);
        if (!embed) {
            descriptor.Vector(
                AafPid.Locator,
                [
                    new AafObject(AafIds.NetworkLocator).Data(
                        AafPid.UrlString,
                        AafEncoding.String(new Uri(media.LinkedPath!).AbsoluteUri)
                    )
                ]
            );
        }

        descriptor.Data(AafPid.SampleRate, AafEncoding.Rational(info.SampleRate, 1))
            .Data(AafPid.DescriptorLength, AafEncoding.Int64(info.Frames))
            .Weak(
                AafPid.ContainerFormat,
                AafIds.ContainerDefinitions,
                AafEncoding.Auid(embed ? AafIds.ContainerAaf : AafIds.ContainerRiffWave)
            )
            .Data(AafPid.AudioSamplingRate, AafEncoding.Rational(info.SampleRate, 1))
            .Data(AafPid.Channels, AafEncoding.UInt32((uint)info.Channels))
            .Data(AafPid.QuantizationBits, AafEncoding.UInt32((uint)bits))
            .Data(AafPid.BlockAlign, AafEncoding.UInt16((ushort)(info.Channels * bits / 8)))
            .Data(AafPid.AverageBps, AafEncoding.UInt32((uint)(info.SampleRate * info.Channels * bits / 8)));
        var end = Component(AafIds.SourceClip, AafIds.DataDefSound, info.Frames)
            .Data(AafPid.SourceId, new byte[32])
            .Data(AafPid.SourceMobSlotId, AafEncoding.UInt32(0))
            .Data(AafPid.StartTime, AafEncoding.Int64(0));
        return new AafObject(AafIds.SourceMob)
            .Data(AafPid.MobId, media.FileId)
            .Data(AafPid.MobName, AafEncoding.String(media.File.Name))
            .Vector(AafPid.Slots, [Slot(end, info.SampleRate)])
            .Data(AafPid.MobLastModified, stamp)
            .Data(AafPid.CreationTime, stamp)
            .Strong(AafPid.EssenceDescription, descriptor);
    }

    static AafObject Slot(AafObject segment, int rate) =>
        new AafObject(AafIds.TimelineMobSlot)
            .Data(AafPid.SlotId, AafEncoding.UInt32(1))
            .Strong(AafPid.Segment, segment)
            .Data(AafPid.EditRate, AafEncoding.Rational(rate, 1))
            .Data(AafPid.Origin, AafEncoding.Int64(0));

    /// <summary>The embedded essence: the file's samples as interleaved little-endian PCM, streamed from the file.</summary>
    static AafObject EssenceData(Media media) =>
        new AafObject(AafIds.EssenceData)
            .Data(AafPid.EssenceMobId, media.FileId)
            .Stream(AafPid.EssenceStream, PcmLength(media), s => CopyPcm(media, s));

    static long PcmLength(Media media) => media.Info.Frames * media.Info.Channels * (Bits(media.Encoding) / 8);

    static int Bits(WavEncoding encoding) =>
        encoding switch {
            WavEncoding.Pcm16 => 16,
            WavEncoding.Pcm24 => 24,
            _ => 32
        };

    static void CopyPcm(Media media, Stream into) {
        var info = media.Info;
        if (info.Encoding == media.Encoding) {
            using var file = new FileStream(media.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            file.Position = info.DataOffset;
            var left = info.Frames * info.BytesPerFrame;
            var buffer = new byte[1 << 16];
            while (left > 0) {
                var n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n == 0) {
                    throw new InvalidDataException($"{media.Source} is shorter than its header says.");
                }

                into.Write(buffer, 0, n);
                left -= n;
            }

            return;
        }

        // Float → 24-bit PCM.
        using var reader = WavReader.Open(media.Source);
        var samples = new float[4096 * info.Channels];
        var bytes = new byte[samples.Length * 3];
        var remaining = info.Frames;
        while (remaining > 0) {
            var frames = reader.Read(samples);
            if (frames == 0) {
                throw new InvalidDataException($"{media.Source} is shorter than its header says.");
            }

            frames = (int)Math.Min(frames, remaining);
            var count = frames * info.Channels;
            for (var i = 0; i < count; i++) {
                var v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607);
                bytes[i * 3] = (byte)v;
                bytes[i * 3 + 1] = (byte)(v >> 8);
                bytes[i * 3 + 2] = (byte)(v >> 16);
            }

            into.Write(bytes, 0, count * 3);
            remaining -= frames;
        }
    }

    /// <summary>A linked file beside the AAF: the project file itself when it is integer PCM, else 24-bit PCM.</summary>
    static void WriteLinked(Media media) {
        if (media.Encoding == media.Info.Encoding) {
            File.Copy(media.Source, media.LinkedPath!, true);
            return;
        }

        using var file = new FileStream(media.LinkedPath!, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        WriteWavHeader(file, media.Info.Channels, media.Info.SampleRate, 24, PcmLength(media));
        CopyPcm(media, file);
        if ((PcmLength(media) & 1) != 0) {
            file.WriteByte(0);
        }
    }

    internal static void WriteWavHeader(Stream into, int channels, int sampleRate, int bits, long dataBytes) {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[4..],
            (uint)Math.Min(uint.MaxValue, 36 + dataBytes + (dataBytes & 1))
        );
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)bits);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)Math.Min(uint.MaxValue, dataBytes));
        into.Write(header);
    }

    static AafObject Component(Guid classId, Guid dataDefinition, long length) =>
        new AafObject(classId)
            .Weak(AafPid.DataDefinition, AafIds.DataDefinitions, AafEncoding.Auid(dataDefinition))
            .Data(AafPid.Length, AafEncoding.Int64(length));

    static AafObject Tagged(string name, string value) =>
        new AafObject(AafIds.TaggedValue)
            .Data(AafPid.TaggedName, AafEncoding.String(name))
            .Data(AafPid.TaggedValue, AafEncoding.Indirect(AafIds.TypeString, AafEncoding.String(value)));

    /// <summary>A stable material number for a mob: the same project and file give the same MobID.</summary>
    static Guid Material(string projectId, string kind, string key) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"anode/{projectId}/{kind}/{key}"));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new(hash.AsSpan(0, 16));
    }

    static string Unique(string folder, string stem, List<string> taken) {
        var safe = string.Concat(stem.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var candidate = Path.Combine(folder, safe + ".wav");
        for (var n = 2; taken.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++) {
            candidate = Path.Combine(folder, $"{safe} {n}.wav");
        }

        return candidate;
    }

    sealed record TrackPlan(Track Track, List<ClipPlan> Clips);

    sealed record ClipPlan(AudioRegion Region, long Position);

    sealed class Piece(ClipPlan? clip, long length) {
        public ClipPlan? Clip { get; } = clip;

        public long Length { get; set; } = length;

        public bool IsTransition { get; init; }

        public long TransitionIn { get; set; }

        public long TransitionOut { get; set; }
    }

    sealed class Media(
        AudioFile file,
        string source,
        WavInfo info,
        WavEncoding encoding,
        byte[] masterId,
        byte[] fileId) {
        public AudioFile File { get; } = file;

        public string Source { get; } = source;

        public WavInfo Info { get; } = info;

        /// <summary>How the samples are written: the file's own integer PCM, or 24-bit for a float file.</summary>
        public WavEncoding Encoding { get; } = encoding;

        public byte[] MasterId { get; } = masterId;

        public byte[] FileId { get; } = fileId;

        public string? LinkedPath { get; set; }
    }
}
