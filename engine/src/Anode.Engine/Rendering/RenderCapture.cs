using Anode.Engine.Streaming;

namespace Anode.Engine.Rendering;

/// <summary>What a render takes from the mix.</summary>
public enum RenderKind {
    /// <summary>The master's output (after its fader), before the click.</summary>
    Master,

    /// <summary>Each listed track's post-fader output as it reaches its destination.</summary>
    Stems,

    /// <summary>One track's source and inserts, before its fader.</summary>
    Freeze
}

/// <summary>
///     One file's worth of a render: the audio thread writes the captured signal (stereo, interleaved) into a ring,
///     dropping the latency it had at the start (so the file lines up with the project), up to the wanted length; the
///     render thread drains it to the file.
/// </summary>
public sealed class RenderTarget : IDisposable {
    long skip = -1;
    long remaining;
    long dropped;

    public RenderTarget(int trackId, string path, long frames, int ringFrames) {
        TrackId = trackId;
        Path = path;
        remaining = frames;
        Frames = frames;
        Ring = new(ringFrames * 2);
    }

    /// <summary>The engine track (−1: the master).</summary>
    public int TrackId { get; }

    public string Path { get; }

    /// <summary>Frames the file gets.</summary>
    public long Frames { get; }

    public SpscRing Ring { get; }

    /// <summary>Frames still to capture.</summary>
    public long Remaining => Volatile.Read(ref remaining);

    /// <summary>Frames that did not fit the ring (a real-time render whose writer fell behind).</summary>
    public long Dropped => Volatile.Read(ref dropped);

    /// <summary>
    ///     Audio thread: captures a block (a mono signal passes the same span twice). The first block sets how many
    ///     frames to drop: <paramref name="latency" />, the path's latency at the start.
    /// </summary>
    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int latency) {
        if (skip < 0) {
            skip = Math.Max(0, latency);
        }

        var from = (int)Math.Min(skip, left.Length);
        skip -= from;
        var count = (int)Math.Min(left.Length - from, remaining);
        if (count <= 0) {
            return;
        }

        var fit = Math.Min(count, Ring.Free / 2);
        Ring.WriteRegions(fit * 2, out var first, out var second);
        for (var i = 0; i < fit; i++) {
            var l = left[from + i];
            var r = right[from + i];
            var at = 2 * i;
            if (at < first.Length) {
                first[at] = l;
                first[at + 1] = r;
            } else {
                second[at - first.Length] = l;
                second[at - first.Length + 1] = r;
            }
        }

        Ring.CommitWrite(fit * 2);
        if (fit < count) {
            Volatile.Write(ref dropped, dropped + count - fit);
        }

        Volatile.Write(ref remaining, remaining - count);
    }

    public void Dispose() => Ring.Dispose();
}

/// <summary>
///     A render in progress, as the audio thread sees it (<see cref="AudioEngine.Rendering" />): the master's target, or
///     the tracks' by engine track id. Built by the control thread; read without locks.
/// </summary>
public sealed class RenderCapture(RenderKind kind, RenderTarget? master, Dictionary<int, RenderTarget> tracks) {
    public RenderKind Kind { get; } = kind;

    public RenderTarget? Master { get; } = master;

    /// <summary>The stems' or the frozen track's targets (alloc-free lookups on the audio thread).</summary>
    public Dictionary<int, RenderTarget> Tracks { get; } = tracks;

    /// <summary>Every target has its frames.</summary>
    public bool Complete {
        get {
            if (Master is { Remaining: > 0 }) {
                return false;
            }

            foreach (var target in Tracks.Values) {
                if (target.Remaining > 0) {
                    return false;
                }
            }

            return true;
        }
    }

    public IEnumerable<RenderTarget> Targets => Master is null ? Tracks.Values : [Master, ..Tracks.Values];
}
