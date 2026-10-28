using Anode.Dsp.Flex;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model.Audio;
using System.Runtime.InteropServices;

namespace Anode.Server.Engine;

/// <summary>
///     How the fake host renders a track with a playlist (Phase 5): its entries' audio from their files, unwarped ones
///     1:1 and warped ones through <see cref="FlexRenderers.Create" /> (the reference renderer until workstream E), at
///     their gain, with linear fades, mono files on both sides. Tracks without a playlist render the sine.
/// </summary>
public sealed partial class FakeHost {
    const int RenderBlock = 4096;

    readonly Dictionary<int, WarpPoint[][]> warps = [];
    readonly Dictionary<int, PitchPoint[][]> pitches = [];

    /// <summary>The warp points of each entry of a track's playlist in the loaded model (none for unwarped ones).</summary>
    public WarpPoint[][] WarpOf(int trackId) {
        lock (gate) {
            return warps.GetValueOrDefault(trackId) ?? [];
        }
    }

    /// <summary>Each playlist entry's own warp points (none for an unwarped one) of a track of a segment.</summary>
    static unsafe (WarpPoint[][] Warps, PitchPoint[][] Pitches) WarpsOf(MappedSegment segment, int index) {
        var view = segment.Track(index);
        var warp = new WarpPoint[view.PlaylistCount][];
        var pitch = new PitchPoint[view.PlaylistCount][];
        for (long e = 0; e < view.PlaylistCount; e++) {
            var entry = view.Playlist[e];
            warp[e] = entry.WarpCount > 0
                ? [..new ReadOnlySpan<WarpPoint>(view.Warp + entry.WarpStart, entry.WarpCount)]
                : [];
            pitch[e] = entry.PitchCount > 0
                ? [..new ReadOnlySpan<PitchPoint>(view.Pitch + entry.PitchStart, entry.PitchCount)]
                : [];
        }

        return (warp, pitch);
    }

    /// <summary>A track's playlist, warps and the files it plays, as a render reads them (null: no playlist).</summary>
    TrackAudio? AudioOf(int trackId) {
        lock (gate) {
            if (!playlists.TryGetValue(trackId, out var entries) || entries.Length == 0) {
                return null;
            }

            return new(
                entries,
                warps.GetValueOrDefault(trackId) ?? [..entries.Select(static _ => Array.Empty<WarpPoint>())],
                pitches.GetValueOrDefault(trackId) ?? [..entries.Select(static _ => Array.Empty<PitchPoint>())],
                [..entries.Select(e => files.GetValueOrDefault(e.FileId))],
                SampleRate
            );
        }
    }

    /// <summary>A track's playlist as a render plays it, with the files it reads, loaded on first use.</summary>
    sealed class TrackAudio(
        AudioPlaylistEntry[] entries,
        WarpPoint[][] warp,
        PitchPoint[][] pitch,
        EngineFile?[] files,
        int rate
    ) : IDisposable {
        readonly Dictionary<int, FileSource?> sources = [];
        readonly Dictionary<int, IFlexRenderer> renderers = [];
        readonly float[] scratch = new float[RenderBlock * 2];

        /// <summary>Adds the playlist's audio over samples <paramref name="start" />… into the two channels.</summary>
        public void Render(long start, Span<float> left, Span<float> right) {
            var n = left.Length;
            for (var e = 0; e < entries.Length; e++) {
                var entry = entries[e];
                var from = Math.Max(start, entry.Start);
                var to = Math.Min(start + n, entry.Start + entry.Length);
                if (to <= from || Source(e) is not { } source) {
                    continue;
                }

                var count = (int)(to - from);
                var offset = from - entry.Start;
                var buffer = scratch.AsSpan(0, count * source.Channels);
                if (warp[e].Length == 0) {
                    source.Read(entry.FileOffset + offset, buffer);
                } else {
                    var region = new FlexRegion(
                        (FlexKind)entry.Algorithm,
                        MemoryMarshal.Cast<WarpPoint, FlexWarpPoint>(warp[e]),
                        MemoryMarshal.Cast<PitchPoint, FlexPitchPoint>(pitch[e]),
                        entry.Length
                    );
                    Renderer(e, entry, source.Channels).Render(region, offset, buffer, source.Channels, source);
                }

                for (var i = 0; i < count; i++) {
                    var at = offset + i;
                    var gain = entry.Gain * Fade(entry, at);
                    var l = buffer[i * source.Channels];
                    var r = buffer[i * source.Channels + source.Channels - 1];
                    left[(int)(from - start) + i] += l * gain;
                    right[(int)(from - start) + i] += r * gain;
                }
            }
        }

        static float Fade(AudioPlaylistEntry entry, long at) {
            var fade = 1f;
            if (entry.FadeIn > 0 && at < entry.FadeIn) {
                fade *= (float)at / entry.FadeIn;
            }

            var fromEnd = entry.Length - at;
            if (entry.FadeOut > 0 && fromEnd < entry.FadeOut) {
                fade *= (float)fromEnd / entry.FadeOut;
            }

            return fade;
        }

        FileSource? Source(int e) {
            var file = files[e];
            if (file is null) {
                return null;
            }

            if (!sources.TryGetValue(file.Id, out var source)) {
                try {
                    source = new(BounceProcessor.Read(file.Path).Channels);
                } catch (Exception x) when (x is IOException or UnauthorizedAccessException or WavFormatException) {
                    source = null;
                }

                sources[file.Id] = source;
            }

            return source;
        }

        IFlexRenderer Renderer(int e, AudioPlaylistEntry entry, int channels) {
            if (!renderers.TryGetValue(e, out var renderer)) {
                renderers[e] = renderer = FlexRenderers.Create((FlexKind)entry.Algorithm, channels, rate, RenderBlock);
            }

            return renderer;
        }

        public void Dispose() {
            foreach (var renderer in renderers.Values) {
                renderer.Dispose();
            }
        }
    }

    /// <summary>A file's planar audio as a flex renderer reads it.</summary>
    sealed class FileSource(float[][] channels) : IFlexSource {
        public int Channels => channels.Length;

        public long Frames => channels[0].Length;

        public int Read(long fileFrame, Span<float> interleaved) {
            var frames = interleaved.Length / Channels;
            var inside = 0;
            for (var i = 0; i < frames; i++) {
                var at = fileFrame + i;
                var within = at >= 0 && at < Frames;
                inside += within ? 1 : 0;
                for (var c = 0; c < Channels; c++) {
                    interleaved[i * Channels + c] = within ? channels[c][at] : 0;
                }
            }

            return inside;
        }
    }
}
