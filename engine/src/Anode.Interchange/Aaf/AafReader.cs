using Anode.Contracts;
using Anode.Interchange.Aaf.Cfb;
using Anode.Model;
using Anode.Model.Time;
using System.Buffers.Binary;

namespace Anode.Interchange.Aaf;

/// <summary>
///     AAF import (docs/10-phase-9.md, workstream E; docs/interchange/aaf.md): the compound file and the object model
///     subset Anode writes, as Pro Tools, Media Composer and pyaaf2 write it. A file that is not AAF, or holds no audio
///     composition, throws <see cref="FormatException" />.
/// </summary>
public static class AafReader {
    /// <summary>
    ///     The composition of the AAF at <paramref name="path" />, extracting embedded essence as WAV files into
    ///     <paramref name="mediaFolder" /> (a scratch folder the job gives) and resolving linked media relative to the
    ///     file; a linked file that cannot be found throws <see cref="FileNotFoundException" />.
    /// </summary>
    public static AafComposition Read(string path, string mediaFolder) {
        CfbReader file;
        try {
            file = CfbReader.Open(path);
        } catch (FormatException e) {
            throw new FormatException($"{Path.GetFileName(path)} is not an AAF file: {e.Message}", e);
        }

        using (file) {
            try {
                return new Session(file, Path.GetFullPath(path), mediaFolder).Read();
            } catch (Exception e) when (e is ArgumentException
                                            or IndexOutOfRangeException
                                            or OverflowException
                                            or InvalidDataException) {
                throw new FormatException($"{Path.GetFileName(path)} is not a valid AAF file: {e.Message}", e);
            }
        }
    }

    /// <summary>
    ///     The <c>importTracks</c> content of <paramref name="composition" /> for <paramref name="state" />: an audio track
    ///     per AAF track with a region per clip, its markers; <paramref name="fileIds" /> maps each clip's
    ///     <c>MediaPath</c> to the project file the job made of it. Positions convert from samples to ticks by the
    ///     project's tempo map; where no tick lands on a clip's sample exactly, the region starts at the nearest tick
    ///     whose sample is not later (or, when the clip has too little audio before it, not earlier) and its file offset,
    ///     length and fade in absorb the difference, so every file frame plays at the sample the AAF puts it. Positions
    ///     and lengths in samples of another rate are scaled to the project's (the job resamples the media), and a region
    ///     never reaches past the end of its project file.
    /// </summary>
    public static ImportedContent ToContent(
        AafComposition composition,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    ) {
        var placement = new Placement(state, composition.SampleRate);
        var tracks = new List<ImportedTrack>();
        var regionNumber = 0;
        for (var t = 0; t < composition.Tracks.Count; t++) {
            var track = composition.Tracks[t];
            var trackId = $"aaf-track-{t + 1}";
            var regions = new List<AudioRegion>();
            foreach (var clip in track.Clips) {
                if (!fileIds.TryGetValue(clip.MediaPath, out var fileId)) {
                    throw new ArgumentException($"No project file for {clip.MediaPath}.", nameof(fileIds));
                }

                if (placement.Region(clip, $"aaf-region-{regionNumber + 1}", trackId, fileId) is { } region) {
                    regions.Add(region);
                    regionNumber++;
                }
            }

            placement.WidenCrossfades(regions);
            tracks.Add(new(trackId, track.Name, TrackKind.Audio, null, [], regions));
        }

        var markers = composition.Markers.Select((m, i) => placement.Marker(m, $"aaf-marker-{i + 1}")).ToList();
        return new(tracks, Markers: markers.Count > 0 ? markers : null);
    }

    /// <summary>Samples of the composition's rate placed on the project's ticks and frames.</summary>
    sealed class Placement(ProjectState state, int compositionRate) {
        readonly TempoMap map = state.TempoMap;
        readonly int rate = state.SampleRate;
        readonly double scale = compositionRate > 0 ? (double)state.SampleRate / compositionRate : 1;

        long Scaled(long samples) => scale == 1 ? samples : (long)Math.Round(samples * scale);

        /// <summary>A clip's region, its start moved onto a tick with its audio kept at its sample; null when empty.</summary>
        public AudioRegion? Region(AafClip clip, string id, string trackId, string fileId) {
            var position = Scaled(clip.Position);
            var offset = Scaled(clip.SourceOffset);
            var length = Scaled(clip.Length);
            var fadeIn = Scaled(clip.FadeIn);
            var tick = TickAtOrBefore(position);
            var early = position - map.SampleAt(tick, rate);
            if (early > offset) {
                // Too little audio before the clip: start at the next tick instead, a little later.
                tick = TickAtOrAfter(position);
                var late = map.SampleAt(tick, rate) - position;
                (offset, length, fadeIn) = (offset + late, length - late, Math.Max(0, fadeIn - late));
            } else {
                (offset, length, fadeIn) = (offset - early, length + early, fadeIn > 0 ? fadeIn + early : 0);
            }

            if (state.Files.TryGetValue(fileId, out var file)) {
                // Scaled from another rate, a clip may round a frame past its resampled file.
                length = Math.Min(length, file.Frames - offset);
            }

            return length <= 0
                ? null
                : new(
                    id,
                    trackId,
                    fileId,
                    clip.Name,
                    tick,
                    offset,
                    length,
                    clip.GainDb,
                    Math.Min(fadeIn, length),
                    Math.Min(Scaled(clip.FadeOut), length),
                    0,
                    0,
                    false,
                    FlexFollow.Off,
                    [],
                    0,
                    0,
                    []
                );
        }

        /// <summary>
        ///     A crossfade whose later region moved to an earlier tick overlaps by a few more samples: the earlier region's
        ///     fade out covers them too.
        /// </summary>
        public void WidenCrossfades(List<AudioRegion> regions) {
            for (var i = 1; i < regions.Count; i++) {
                var (before, after) = (regions[i - 1], regions[i]);
                var overlap = map.SampleAt(before.StartTick, rate)
                    + before.Length
                    - map.SampleAt(after.StartTick, rate);
                if (overlap > before.FadeOut && before.FadeOut > 0 && after.FadeIn > 0) {
                    regions[i - 1] = before with { FadeOut = Math.Min(overlap, before.Length) };
                }
            }
        }

        public Marker Marker(AafMarker marker, string id) {
            var tick = map.TickAtSample(Scaled(marker.Position), rate);
            var end = map.TickAtSample(Scaled(marker.Position + marker.Length), rate);
            return new(id, marker.Name, tick, Math.Max(0, end - tick), null, marker.Comment);
        }

        long TickAtOrBefore(long sample) {
            var tick = map.TickAtSample(sample, rate);
            while (tick > 0 && map.SampleAt(tick, rate) > sample) {
                tick--;
            }

            return tick;
        }

        long TickAtOrAfter(long sample) {
            var tick = TickAtOrBefore(sample) + 1;
            while (map.SampleAt(tick, rate) < sample) {
                tick++;
            }

            return tick;
        }
    }

    /// <summary>One read: the file's objects, its mobs by MobID, and the media extracted so far.</summary>
    sealed class Session(CfbReader file, string path, string mediaFolder) {
        readonly Dictionary<string, AafStored> mobs = new(StringComparer.Ordinal);
        readonly Dictionary<string, AafStored> essence = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> extracted = new(StringComparer.Ordinal);
        readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        int sampleRate;

        public AafComposition Read() {
            var root = AafStored.Load(file, file.Root);
            var header = root.Strong(AafPid.RootHeader) ?? throw new FormatException("No AAF header.");
            if (header.ClassId != AafIds.Header) {
                throw new FormatException("No AAF header.");
            }

            var content = header.Strong(AafPid.Content) ?? throw new FormatException("No AAF content storage.");
            foreach (var mob in content.Elements(AafPid.Mobs)) {
                if (mob.Data(AafPid.MobId) is { Length: 32 } id) {
                    mobs[AafEncoding.MobKey(id)] = mob;
                }
            }

            foreach (var data in content.Elements(AafPid.EssenceDataSet)) {
                if (data.Data(AafPid.EssenceMobId) is { Length: 32 } id) {
                    essence[AafEncoding.MobKey(id)] = data;
                }
            }

            var composition = TopLevel() ?? throw new FormatException("The AAF file holds no composition.");
            var slots = composition.Elements(AafPid.Slots);
            var audio = slots
                .Where(static s => s.ClassId == AafIds.TimelineMobSlot && IsSound(s.Strong(AafPid.Segment)))
                .ToList();
            if (audio.Count == 0) {
                throw new FormatException("The AAF composition has no audio tracks.");
            }

            sampleRate = audio.Select(static s => Rate(s, AafPid.EditRate))
                .Where(static r => r.Denominator == 1 && r.Numerator >= 8000)
                .Select(static r => r.Numerator)
                .FirstOrDefault();
            if (sampleRate == 0) {
                sampleRate = MediaRate(audio)
                    ?? throw new FormatException("The AAF composition's sample rate is unknown.");
            }

            var tracks = audio.Select((s, i) => Track(s, i)).ToList();
            var markers = slots.Where(static s => s.ClassId == AafIds.EventMobSlot).SelectMany(Markers).ToList();
            return new(composition.String(AafPid.MobName) ?? "AAF", sampleRate, tracks, markers);
        }

        /// <summary>The composition to import: the top-level one (Avid's usage code), else one no other uses.</summary>
        AafStored? TopLevel() {
            var compositions = mobs.Values.Where(static m => m.ClassId == AafIds.CompositionMob).ToList();
            var top = compositions.Find(static m => m.Auid(AafPid.UsageCode) == AafIds.UsageTopLevel);
            if (top is not null) {
                return top;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mob in compositions) {
                foreach (var slot in mob.Elements(AafPid.Slots)) {
                    Collect(slot.Strong(AafPid.Segment), used);
                }
            }

            return compositions.Find(m => !used.Contains(AafEncoding.MobKey(m.Data(AafPid.MobId)!)))
                ?? compositions.FirstOrDefault();
        }

        static void Collect(AafStored? segment, HashSet<string> into) {
            if (segment is null) {
                return;
            }

            if (segment.ClassId == AafIds.SourceClip && segment.Data(AafPid.SourceId) is { Length: 32 } id) {
                into.Add(AafEncoding.MobKey(id));
            }

            foreach (var child in segment.Elements(AafPid.Components).Concat(segment.Elements(AafPid.InputSegments))) {
                Collect(child, into);
            }
        }

        static bool IsSound(AafStored? segment) {
            if (segment?.WeakKey(AafPid.DataDefinition) is not { Length: 16 } key) {
                return false;
            }

            var id = new Guid(key);
            return id == AafIds.DataDefSound || id == AafIds.DataDefLegacySound;
        }

        static (int Numerator, int Denominator) Rate(AafStored slot, ushort pid) =>
            slot.Rational(pid) is { Numerator: > 0, Denominator: > 0 } r ? r : (0, 1);

        int? MediaRate(List<AafStored> slots) {
            foreach (var slot in slots) {
                foreach (var clip in Clips(slot.Strong(AafPid.Segment))) {
                    if (Resolve(clip, Rate(slot, AafPid.EditRate)) is { } media
                        && media.Descriptor.Rational(AafPid.SampleRate) is { Numerator: > 0, Denominator: 1 } r) {
                        return r.Numerator;
                    }
                }
            }

            return null;
        }

        static IEnumerable<AafStored> Clips(AafStored? segment) {
            if (segment is null) {
                yield break;
            }

            if (segment.ClassId == AafIds.SourceClip) {
                yield return segment;
            }

            foreach (var child in segment.Elements(AafPid.Components).Concat(segment.Elements(AafPid.InputSegments))) {
                foreach (var clip in Clips(child)) {
                    yield return clip;
                }
            }
        }

        long Samples(long units, (int Numerator, int Denominator) editRate) =>
            editRate == (sampleRate, 1)
                ? units
                : (long)Math.Round((double)units * sampleRate * editRate.Denominator / editRate.Numerator);

        AafTrack Track(AafStored slot, int index) {
            var editRate = Rate(slot, AafPid.EditRate);
            if (editRate.Numerator == 0) {
                throw new FormatException("An AAF audio slot has no edit rate.");
            }

            var segment = slot.Strong(AafPid.Segment)!;
            var components = segment.ClassId == AafIds.Sequence ? segment.Elements(AafPid.Components) : [segment];
            var clips = new List<AafClip>();
            long position = 0;
            long pendingFade = 0;
            foreach (var component in components) {
                var length = component.Int64(AafPid.Length) ?? 0;
                if (component.ClassId == AafIds.Transition) {
                    position -= length;
                    if (clips.Count > 0) {
                        clips[^1] = clips[^1] with { FadeOut = Samples(length, editRate) };
                    }

                    pendingFade = Samples(length, editRate);
                    continue;
                }

                if (Clip(component, editRate, 0) is { } found) {
                    var (clip, gain) = found;
                    clips.Add(
                        clip with {
                            Position = Samples(position, editRate),
                            Length = Samples(position + length, editRate) - Samples(position, editRate),
                            GainDb = gain,
                            FadeIn = pendingFade > 0 ? pendingFade : clip.FadeIn
                        }
                    );
                }

                pendingFade = 0;
                position += length;
            }

            var name = slot.String(AafPid.SlotName);
            return new(string.IsNullOrEmpty(name) ? $"Audio {index + 1}" : name, clips);
        }

        /// <summary>
        ///     The clip a component plays: a source clip, or the input of an operation group (gain operations multiply in
        ///     their amplitude); null for fillers and anything else.
        /// </summary>
        (AafClip Clip, double GainDb)? Clip(AafStored component, (int Numerator, int Denominator) editRate, int depth) {
            if (depth > 16) {
                throw new FormatException("AAF operation groups nest too deep.");
            }

            if (component.ClassId == AafIds.OperationGroup) {
                var inputs = component.Elements(AafPid.InputSegments);
                if (inputs.Count == 0 || Clip(inputs[0], editRate, depth + 1) is not { } inner) {
                    return null;
                }

                var operation = component.WeakKey(AafPid.Operation) is { Length: 16 } key ? new Guid(key) : Guid.Empty;
                return operation == AafIds.MonoAudioGain || operation == AafIds.StereoAudioGain
                    ? (inner.Clip, inner.GainDb + GainOf(component))
                    : inner;
            }

            if (component.ClassId != AafIds.SourceClip) {
                return null;
            }

            var media = Resolve(component, editRate);
            if (media is null) {
                return null;
            }

            var fadeIn = component.Int64(AafPid.FadeInLength) ?? 0;
            var fadeOut = component.Int64(AafPid.FadeOutLength) ?? 0;
            var scale = (double)sampleRate * editRate.Denominator / editRate.Numerator;
            return (new(
                media.Name,
                media.Path,
                0,
                (long)Math.Round(media.Offset * sampleRate),
                0,
                0,
                (long)Math.Round(fadeIn * scale),
                (long)Math.Round(fadeOut * scale)
            ), 0);
        }

        static double GainOf(AafStored group) {
            foreach (var parameter in group.Elements(AafPid.Parameters)) {
                if (parameter.ClassId != AafIds.ConstantValue
                    || parameter.Auid(AafPid.ParameterDefinition) != AafIds.Amplitude
                    || parameter.Data(AafPid.Value) is not { Length: 25 } value
                    || new Guid(value.AsSpan(1, 16)) != AafIds.TypeRational) {
                    continue;
                }

                var numerator = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(17));
                var denominator = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(21));
                if (denominator <= 0) {
                    return 0;
                }

                return numerator <= 0 ? -144 : 20 * Math.Log10((double)numerator / denominator);
            }

            return 0;
        }

        /// <summary>
        ///     Follows a source clip down the mob chain (master mob, then file source mobs) to the media, adding up the
        ///     start times (as seconds); null when the chain ends without media (or loops).
        /// </summary>
        Media? Resolve(AafStored clip, (int Numerator, int Denominator) editRate) {
            double offset = 0;
            string? name = null;
            var current = clip;
            var rate = editRate;
            for (var step = 0; step < 16 && current is not null; step++) {
                if (Target(current) is not var (id, mob, slot)) {
                    return null;
                }

                offset += Seconds(current.Int64(AafPid.StartTime) ?? 0, rate);
                if (mob.ClassId == AafIds.MasterMob && string.IsNullOrEmpty(name)) {
                    name = mob.String(AafPid.MobName);
                }

                if (FileMedia(id, mob, name, offset) is { } media) {
                    return media;
                }

                current = FirstClip(slot.Strong(AafPid.Segment));
                var slotRate = Rate(slot, AafPid.EditRate);
                rate = slotRate.Numerator > 0 ? slotRate : rate;
            }

            return null;
        }

        static double Seconds(long units, (int Numerator, int Denominator) rate) =>
            rate.Numerator > 0 ? (double)units * rate.Denominator / rate.Numerator : 0;

        /// <summary>The media of a file source mob (one with a file descriptor), or null for any other mob.</summary>
        Media? FileMedia(byte[] id, AafStored mob, string? name, double offset) {
            if (mob.ClassId != AafIds.SourceMob
                || mob.Strong(AafPid.EssenceDescription) is not { } descriptor
                || !IsFileDescriptor(descriptor)) {
                return null;
            }

            var key = AafEncoding.MobKey(id);
            var mediaName = string.IsNullOrEmpty(name) ? mob.String(AafPid.MobName) : name;
            return new(Extract(key, mob, descriptor, mediaName), offset, mediaName ?? key, descriptor);
        }

        /// <summary>The MobID, mob and slot a source clip refers to; null at the end of a chain (a zero MobID).</summary>
        (byte[] Id, AafStored Mob, AafStored Slot)? Target(AafStored clip) {
            if (clip.Data(AafPid.SourceId) is not { Length: 32 } id || id.All(static b => b == 0)) {
                return null;
            }

            if (!mobs.TryGetValue(AafEncoding.MobKey(id), out var mob)) {
                throw new FormatException("An AAF clip refers to a mob the file does not have.");
            }

            var slotId = clip.UInt32(AafPid.SourceMobSlotId) ?? 0;
            var slot = mob.Elements(AafPid.Slots).FirstOrDefault(s => s.UInt32(AafPid.SlotId) == slotId)
                ?? throw new FormatException("An AAF clip refers to a slot its mob does not have.");
            return (id, mob, slot);
        }

        /// <summary>A mob slot's source clip: its segment, or the first clip of its sequence; null otherwise.</summary>
        static AafStored? FirstClip(AafStored? segment) =>
            segment?.ClassId == AafIds.Sequence
                ? segment.Elements(AafPid.Components).FirstOrDefault(static c => c.ClassId == AafIds.SourceClip)
                : segment?.ClassId == AafIds.SourceClip
                    ? segment
                    : null;

        static bool IsFileDescriptor(AafStored descriptor) =>
            descriptor.ClassId == AafIds.PcmDescriptor
            || descriptor.ClassId == AafIds.WaveDescriptor
            || descriptor.ClassId == AafIds.SoundDescriptor
            || descriptor.ClassId == AafIds.AifcDescriptor;

        /// <summary>The WAV file of a file source mob: its embedded essence extracted, or its linked file found.</summary>
        string Extract(string key, AafStored mob, AafStored descriptor, string? name) {
            if (extracted.TryGetValue(key, out var done)) {
                return done;
            }

            string result;
            if (essence.TryGetValue(key, out var data)) {
                result = ExtractEssence(data, descriptor, name ?? key);
            } else {
                result = Linked(descriptor, mob.String(AafPid.MobName) ?? key);
            }

            extracted[key] = result;
            return result;
        }

        string ExtractEssence(AafStored data, AafStored descriptor, string name) {
            if (descriptor.ClassId == AafIds.AifcDescriptor) {
                throw new FormatException("AIFC essence in AAF is not supported.");
            }

            Directory.CreateDirectory(mediaFolder);
            var target = UniqueName(name);
            using var stream = data.OpenStream(AafPid.EssenceStream)
                ?? throw new FormatException("AAF essence data has no stream.");
            Span<byte> magic = stackalloc byte[4];
            var head = stream.Read(magic);
            stream.Position = 0;
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            if (head == 4 && magic.SequenceEqual("RIFF"u8)) {
                stream.CopyTo(output);
                return target;
            }

            var channels = (int)(descriptor.UInt32(AafPid.Channels) ?? 1);
            var bits = (int)(descriptor.UInt32(AafPid.QuantizationBits) ?? 16);
            var rate = descriptor.Rational(AafPid.AudioSamplingRate) is { Numerator: > 0, Denominator: > 0 } a
                ? a.Numerator / a.Denominator
                : descriptor.Rational(AafPid.SampleRate) is { Numerator: > 0, Denominator: > 0 } s
                    ? s.Numerator / s.Denominator
                    : sampleRate;
            if (descriptor.ClassId == AafIds.WaveDescriptor
                || bits is not (16 or 24 or 32)
                || channels is < 1 or > 64) {
                throw new FormatException($"Unsupported AAF essence: {bits}-bit, {channels} channels.");
            }

            AafWriter.WriteWavHeader(output, channels, rate, bits, stream.Length);
            stream.CopyTo(output);
            if ((stream.Length & 1) != 0) {
                output.WriteByte(0);
            }

            return target;
        }

        string Linked(AafStored descriptor, string name) {
            var urls = descriptor.Elements(AafPid.Locator)
                .Where(static l => l.ClassId == AafIds.NetworkLocator)
                .Select(static l => l.String(AafPid.UrlString))
                .OfType<string>()
                .ToList();
            var folder = Path.GetDirectoryName(path) ?? ".";
            var beside = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " Media");
            foreach (var url in urls) {
                var local = LocalPath(url);
                if (local is null) {
                    continue;
                }

                if (Path.IsPathRooted(local) && File.Exists(local)) {
                    return local;
                }

                var fileName = Path.GetFileName(local.Replace('\\', '/'));
                foreach (var candidate in new[] {
                             Path.Combine(folder, local), Path.Combine(beside, fileName), Path.Combine(folder, fileName)
                         }) {
                    if (File.Exists(candidate)) {
                        return Path.GetFullPath(candidate);
                    }
                }
            }

            throw new FileNotFoundException(
                urls.Count > 0
                    ? $"The AAF file's media {name} is not at {string.Join(", ", urls)} nor beside the file."
                    : $"The AAF file's media {name} is neither embedded nor linked.",
                urls.FirstOrDefault()
            );
        }

        static string? LocalPath(string url) {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile) {
                return uri.LocalPath;
            }

            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) {
                return Uri.UnescapeDataString(url[5..].TrimStart('/'));
            }

            return Uri.TryCreate(url, UriKind.Relative, out _) ? Uri.UnescapeDataString(url) : null;
        }

        string UniqueName(string name) {
            var safe = string.Concat(name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))
                .Trim();
            if (safe.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
                safe = safe[..^4];
            }

            if (safe.Length == 0) {
                safe = "media";
            }

            var candidate = safe;
            for (var n = 2; !names.Add(candidate) || File.Exists(Path.Combine(mediaFolder, candidate + ".wav")); n++) {
                candidate = $"{safe} {n}";
            }

            return Path.Combine(mediaFolder, candidate + ".wav");
        }

        IEnumerable<AafMarker> Markers(AafStored slot) {
            var editRate = Rate(slot, AafPid.EventEditRate);
            if (editRate.Numerator == 0) {
                yield break;
            }

            var scale = (double)sampleRate * editRate.Denominator / editRate.Numerator;
            var segment = slot.Strong(AafPid.Segment);
            if (segment is null) {
                yield break;
            }

            var events = segment.ClassId == AafIds.Sequence ? segment.Elements(AafPid.Components) : [segment];
            foreach (var item in events) {
                if (item.ClassId != AafIds.DescriptiveMarker && item.ClassId != AafIds.CommentMarker) {
                    continue;
                }

                var comment = item.Elements(AafPid.UserComments)
                    .Where(static t => t.String(AafPid.TaggedName) == "Comment")
                    .Select(static t => t.Data(AafPid.TaggedValue))
                    .Where(static v => v is { Length: > 17 } && new Guid(v.AsSpan(1, 16)) == AafIds.TypeString)
                    .Select(static v => AafEncoding.ReadString(v![17..]))
                    .FirstOrDefault();
                yield return new(
                    item.String(AafPid.Comment) ?? string.Empty,
                    (long)Math.Round((item.Int64(AafPid.Position) ?? 0) * scale),
                    (long)Math.Round((item.Int64(AafPid.Length) ?? 0) * scale),
                    comment ?? string.Empty
                );
            }
        }

        sealed record Media(string Path, double Offset, string Name, AafStored Descriptor);
    }
}
