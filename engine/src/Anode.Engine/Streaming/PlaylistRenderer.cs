using Anode.Ipc.Segments;
using System.Runtime.InteropServices;

namespace Anode.Engine.Streaming;

/// <summary>
///     Renders a track's compiled playlist (<see cref="AudioPlaylistEntry" />s, sorted and not overlapping) at project
///     positions into interleaved track channels: each region's file frames, at its gain, under its linear fades.
///     Disk-thread code: it reads files, and owns its scratch memory so it never allocates.
/// </summary>
public sealed unsafe class PlaylistRenderer : IDisposable {
    /// <summary>Most channels a source file may have (more are refused at load).</summary>
    public const int MaxFileChannels = 8;

    readonly byte* scratch;
    readonly int scratchBytes;
    readonly float[] decoded;

    public PlaylistRenderer(int maxFrames) {
        MaxFrames = maxFrames;
        scratchBytes = StreamFile.ScratchBytes(maxFrames, MaxFileChannels * 4);
        scratch = (byte*)NativeMemory.AlignedAlloc((nuint)scratchBytes, 16_384);
        decoded = GC.AllocateArray<float>(maxFrames * MaxFileChannels, true);
    }

    public int MaxFrames { get; }

    /// <summary>Bytes read from disk so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Reads issued so far.</summary>
    public long Reads { get; private set; }

    /// <summary>
    ///     The level of region <paramref name="entry" /> at frame <paramref name="frame" /> from its start: its gain,
    ///     times the fade-in (0 at the first frame, rising linearly) and the fade-out (falling linearly to 0 at the last).
    /// </summary>
    public static float Level(in AudioPlaylistEntry entry, long frame) {
        var level = entry.Gain;
        if (entry.FadeIn > 0 && frame < entry.FadeIn) {
            level *= (float)frame / entry.FadeIn;
        }

        var toEnd = entry.Length - 1 - frame;
        if (entry.FadeOut > 0 && toEnd < entry.FadeOut) {
            level *= (float)toEnd / entry.FadeOut;
        }

        return level;
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
    ) {
        destination[..(frames * channels)].Clear();
        var playlist = view.Playlist;
        var count = view.PlaylistCount;
        var end = position + frames;
        for (var i = First(playlist, count, position); i < count && playlist[i].Start < end; i++) {
            ref readonly var entry = ref playlist[i];
            var from = Math.Max(position, entry.Start);
            var to = Math.Min(end, entry.Start + entry.Length);
            if (to <= from) {
                continue;
            }

            var file = (uint)entry.FileId < (uint)files.Length ? files[entry.FileId] : null;
            if (file is null) {
                continue;
            }

            // Region frames [r0, r1) are file frames [FileOffset + r0, ...), clipped to the file.
            var r0 = from - entry.Start;
            var fileFrame = entry.FileOffset + r0;
            var skip = fileFrame < 0 ? -fileFrame : 0;
            var take = Math.Min(to - from, file.Frames - fileFrame) - skip;
            if (take <= 0) {
                continue;
            }

            var n = (int)take;
            BytesRead += file.Read(
                fileFrame + skip,
                n,
                new(scratch, scratchBytes),
                decoded.AsSpan(0, n * file.Channels)
            );
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
                if (fileChannels == 1) {
                    destination[f] = frame[0] * level;
                } else {
                    var sum = 0f;
                    foreach (var s in frame) {
                        sum += s;
                    }

                    destination[f] = sum / fileChannels * level;
                }
            } else {
                destination[2 * f] = frame[0] * level;
                destination[2 * f + 1] = (fileChannels == 1 ? frame[0] : frame[1]) * level;
            }
        }
    }

    /// <summary>The first entry that ends after <paramref name="position" />.</summary>
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

        return lo > 0 && playlist[lo - 1].Start + playlist[lo - 1].Length > position ? lo - 1 : lo;
    }

    public void Dispose() => NativeMemory.AlignedFree(scratch);
}
