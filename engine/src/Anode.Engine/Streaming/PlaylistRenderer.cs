using Anode.Dsp.Flex;
using Anode.Ipc.Segments;
using System.Runtime.InteropServices;

namespace Anode.Engine.Streaming;

/// <summary>
///     Renders a track's compiled playlist (<see cref="AudioPlaylistEntry" />s, sorted by start, overlapping only as
///     crossfades) at project positions into interleaved track channels: each region's file frames, at its gain, under
///     its curved fades. A track with crossfades plays through two streams, one per lane (<see cref="PlaylistLanes" />),
///     and a render takes the entries of one lane; entries that still overlap within a lane are summed. Disk-thread
///     code: it reads files, and owns its scratch memory so it never allocates.
/// </summary>
/// <remarks>
///     Warped entries (format 3, docs/06-phase-5.md) render through the stream's <see cref="FlexLane" />: its renderer
///     for the entry's algorithm writes the entry's offsets into a scratch buffer, reading the file through an
///     <see cref="IFlexSource" /> over the same reads (in the track's channels, mapped as unwarped entries map them),
///     and the gain and fades (in project samples, as for any entry) apply as the result is added. Unwarped entries
///     take the path they always took, sample for sample.
/// </remarks>
public sealed unsafe class PlaylistRenderer : IDisposable {
    /// <summary>Most channels a source file may have (more are refused at load).</summary>
    public const int MaxFileChannels = 8;

    readonly byte* scratch;
    readonly int scratchBytes;
    readonly float[] decoded;
    readonly float[] warped;
    readonly FileSource source;

    public PlaylistRenderer(int maxFrames) {
        MaxFrames = maxFrames;
        scratchBytes = StreamFile.ScratchBytes(maxFrames, MaxFileChannels * 4);
        scratch = (byte*)NativeMemory.AlignedAlloc((nuint)scratchBytes, 16_384);
        decoded = GC.AllocateArray<float>(maxFrames * MaxFileChannels, true);
        warped = GC.AllocateArray<float>(maxFrames * 2, true);
        source = new(this);
    }

    public int MaxFrames { get; }

    /// <summary>Bytes read from disk so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Reads issued so far.</summary>
    public long Reads { get; private set; }

    /// <summary>
    ///     The level of region <paramref name="entry" /> at frame <paramref name="frame" /> from its start: its gain,
    ///     times the fade-in (0 at the first frame, rising to 1) and the fade-out (falling to 0 at the last), each shaped
    ///     by its curve (<see cref="FadeShape" />).
    /// </summary>
    public static float Level(in AudioPlaylistEntry entry, long frame) {
        var level = entry.Gain;
        if (entry.FadeIn > 0 && frame < entry.FadeIn) {
            level *= FadeShape(frame, entry.FadeIn, entry.FadeInCurve);
        }

        var toEnd = entry.Length - 1 - frame;
        if (entry.FadeOut > 0 && toEnd < entry.FadeOut) {
            level *= FadeShape(toEnd, entry.FadeOut, entry.FadeOutCurve);
        }

        return level;
    }

    /// <summary>
    ///     A fade's gain <paramref name="step" /> frames from its silent end, of <paramref name="length" /> frames: with
    ///     x = step / length, x^(2^(−2c)) for curve c in −1…1 (docs/04-phase-3.md, "Fade curves"). A fade-in counts from
    ///     its first frame, a fade-out from its last (the mirror). A linear fade (c = 0) is x itself, in float, as Phase 1
    ///     rendered it; a curved one is evaluated in double and rounded once.
    /// </summary>
    public static float FadeShape(long step, int length, float curve) {
        if (curve == 0f || !float.IsFinite(curve)) {
            return (float)step / length;
        }

        var exponent = Math.Pow(2, -2.0 * Math.Clamp(curve, -1f, 1f));
        return (float)Math.Pow((double)step / length, exponent);
    }

    /// <summary>
    ///     Writes positions [<paramref name="position" />, + <paramref name="frames" />) of <paramref name="view" />'s
    ///     playlist into <paramref name="destination" /> (<paramref name="channels" /> interleaved; silence where no
    ///     region plays or a file is missing). <paramref name="frames" /> is at most <see cref="MaxFrames" />.
    /// </summary>
    public void Render(
        in TrackView view,
        StreamFile?[] files,
        long position,
        int frames,
        int channels,
        Span<float> destination
    ) =>
        Render(view, null, 0, files, position, frames, channels, destination);

    /// <summary>
    ///     As <see cref="Render(in TrackView, StreamFile?[], long, int, int, Span{float})" />, but only the entries in
    ///     lane <paramref name="lane" /> of <paramref name="lanes" /> (null: every entry is in lane 0). Warped entries
    ///     render through <paramref name="flex" />, the stream's renderers (null, or without the entry's algorithm: they
    ///     play silence).
    /// </summary>
    public void Render(
        in TrackView view,
        byte[]? lanes,
        int lane,
        StreamFile?[] files,
        long position,
        int frames,
        int channels,
        Span<float> destination,
        FlexLane? flex = null
    ) {
        destination[..(frames * channels)].Clear();
        if (lanes is null && lane != 0) {
            return;
        }

        var playlist = view.Playlist;
        var count = view.PlaylistCount;
        var end = position + frames;
        for (var i = First(playlist, count, position); i < count && playlist[i].Start < end; i++) {
            if (lanes is not null && (i >= lanes.Length || lanes[i] != lane)) {
                continue;
            }

            if (playlist[i].WarpCount > 0) {
                RenderWarped(view, i, flex, files, position, end, channels, destination);
            } else {
                RenderEntry(playlist[i], files, position, end, channels, destination);
            }
        }
    }

    /// <summary>
    ///     Adds warped entry <paramref name="index" />'s part of positions [<paramref name="position" />,
    ///     <paramref name="end" />): its renderer's output from the entry's offset, at its gain under its fades.
    /// </summary>
    void RenderWarped(
        in TrackView view,
        long index,
        FlexLane? flex,
        StreamFile?[] files,
        long position,
        long end,
        int channels,
        Span<float> destination
    ) {
        ref readonly var entry = ref view.Playlist[index];
        var from = Math.Max(position, entry.Start);
        var to = Math.Min(end, entry.Start + entry.Length);
        var file = (uint)entry.FileId < (uint)files.Length ? files[entry.FileId] : null;
        if (to <= from || file is null || flex is null || flex.Channels != channels) {
            return;
        }

        var algorithm = (FlexKind)entry.Algorithm;
        if (flex.For(algorithm, index) is not { } renderer) {
            return;
        }

        var region = new FlexRegion(
            algorithm,
            new((FlexWarpPoint*)(view.Warp + entry.WarpStart), entry.WarpCount),
            entry.PitchCount > 0 ? new((FlexPitchPoint*)(view.Pitch + entry.PitchStart), entry.PitchCount) : default,
            entry.Length
        );
        var n = (int)(to - from);
        var offset = from - entry.Start;
        var output = warped.AsSpan(0, n * channels);
        source.Bind(file, channels);
        renderer.Render(region, offset, output, channels, source);
        var plain = entry.FadeIn <= 0 && entry.FadeOut <= 0;
        var target = destination[((int)(from - position) * channels)..];
        for (var f = 0; f < n; f++) {
            var level = plain ? entry.Gain : Level(entry, offset + f);
            for (var c = 0; c < channels; c++) {
                target[f * channels + c] += output[f * channels + c] * level;
            }
        }
    }

    /// <summary>Adds one entry's part of positions [<paramref name="position" />, <paramref name="end" />).</summary>
    void RenderEntry(
        in AudioPlaylistEntry entry,
        StreamFile?[] files,
        long position,
        long end,
        int channels,
        Span<float> destination
    ) {
        var from = Math.Max(position, entry.Start);
        var to = Math.Min(end, entry.Start + entry.Length);
        var file = (uint)entry.FileId < (uint)files.Length ? files[entry.FileId] : null;
        if (to <= from || file is null) {
            return;
        }

        // Region frames [r0, r1) are file frames [FileOffset + r0, ...), clipped to the file.
        var r0 = from - entry.Start;
        var fileFrame = entry.FileOffset + r0;
        var skip = fileFrame < 0 ? -fileFrame : 0;
        var take = Math.Min(to - from, file.Frames - fileFrame) - skip;
        if (take <= 0) {
            return;
        }

        var n = (int)take;
        BytesRead += file.Read(fileFrame + skip, n, new(scratch, scratchBytes), decoded.AsSpan(0, n * file.Channels));
        Reads++;
        Mix(
            entry,
            r0 + skip,
            file.Channels,
            decoded.AsSpan(0, n * file.Channels),
            destination[((int)(from - position + skip) * channels)..],
            n,
            channels
        );
    }

    static void Mix(
        in AudioPlaylistEntry entry,
        long regionFrame,
        int fileChannels,
        ReadOnlySpan<float> source,
        Span<float> destination,
        int frames,
        int channels
    ) {
        var plain = entry.FadeIn <= 0 && entry.FadeOut <= 0;
        for (var f = 0; f < frames; f++) {
            var level = plain ? entry.Gain : Level(entry, regionFrame + f);
            var frame = source.Slice(f * fileChannels, fileChannels);
            if (channels == 1) {
                destination[f] += Downmix(frame) * level;
            } else {
                destination[2 * f] += frame[0] * level;
                destination[2 * f + 1] += (fileChannels == 1 ? frame[0] : frame[1]) * level;
            }
        }
    }

    /// <summary>A file frame for a mono track: the sample itself, or the average of the file's channels.</summary>
    static float Downmix(ReadOnlySpan<float> frame) {
        if (frame.Length == 1) {
            return frame[0];
        }

        var sum = 0f;
        foreach (var s in frame) {
            sum += s;
        }

        return sum / frame.Length;
    }

    /// <summary>
    ///     The first entry that ends after <paramref name="position" />. Entries are sorted by start, and one that
    ///     overlaps the next as a crossfade ends inside it, so the entries still sounding at the position that start
    ///     before it are a short run just before the first that starts after it.
    /// </summary>
    static long First(AudioPlaylistEntry* playlist, long count, long position) {
        long lo = 0, hi = count;
        while (lo < hi) {
            var mid = lo + ((hi - lo) >> 1);
            if (playlist[mid].Start <= position) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }

        var first = lo;
        for (var i = lo - 1; i >= 0 && lo - i <= 3; i--) {
            if (playlist[i].Start + playlist[i].Length > position) {
                first = i;
            }
        }

        return first;
    }

    public void Dispose() => NativeMemory.AlignedFree(scratch);

    /// <summary>
    ///     The file a flex renderer reads, through the renderer's own reads and scratch, in the track's channels (a
    ///     stereo file on a mono track averaged, a mono file on a stereo track on both sides, as unwarped entries play
    ///     them). Bound to an entry's file before each render; disk thread only.
    /// </summary>
    sealed class FileSource(PlaylistRenderer owner) : IFlexSource {
        StreamFile? file;

        public int Channels { get; private set; } = 1;

        public long Frames => file?.Frames ?? 0;

        public void Bind(StreamFile streamFile, int channels) {
            file = streamFile;
            Channels = channels;
        }

        public int Read(long fileFrame, Span<float> interleaved) {
            var frames = interleaved.Length / Channels;
            interleaved.Clear();
            if (file is null) {
                return 0;
            }

            var first = Math.Max(fileFrame, 0);
            var last = Math.Min(fileFrame + frames, file.Frames);
            for (var at = first; at < last;) {
                var n = (int)Math.Min(owner.MaxFrames, last - at);
                var fileChannels = file.Channels;
                var block = owner.decoded.AsSpan(0, n * fileChannels);
                owner.BytesRead += file.Read(at, n, new(owner.scratch, owner.scratchBytes), block);
                owner.Reads++;
                var target = interleaved[((int)(at - fileFrame) * Channels)..];
                for (var f = 0; f < n; f++) {
                    var frame = block.Slice(f * fileChannels, fileChannels);
                    if (Channels == 1) {
                        target[f] = Downmix(frame);
                    } else {
                        target[2 * f] = frame[0];
                        target[2 * f + 1] = fileChannels == 1 ? frame[0] : frame[1];
                    }
                }

                at += n;
            }

            return (int)Math.Max(0, last - first);
        }
    }
}

/// <summary>
///     Which of a track's two streams each playlist entry plays through: entries alternate lanes along a run of
///     crossfades, so the two entries sounding at once are always in different lanes, and an entry that overlaps
///     nothing before it starts in lane 0. Computed on the control thread when a track's compile is loaded.
/// </summary>
public static unsafe class PlaylistLanes {
    /// <summary>The lane of each entry, or null when no entry overlaps another (everything plays in lane 0).</summary>
    public static byte[]? Of(in TrackView view) {
        var playlist = view.Playlist;
        var count = view.PlaylistCount;
        byte[]? lanes = null;
        for (long e = 1; e < count; e++) {
            if (playlist[e].Start >= playlist[e - 1].Start + playlist[e - 1].Length) {
                continue;
            }

            lanes ??= new byte[count];
            lanes[e] = (byte)(1 - lanes[e - 1]);
        }

        return lanes;
    }
}
