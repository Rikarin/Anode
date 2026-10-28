using Anode.Dsp.Flex;

namespace Anode.Engine.Streaming;

/// <summary>
///     One stream's flex renderers (docs/06-phase-5.md, "Engine"): one <see cref="IFlexRenderer" /> per algorithm its
///     warped entries use, in the stream's channel count, made by <see cref="FlexRenderers.Create" /> on the control
///     thread when a model that needs it loads (<see cref="Ensure" />) and kept for the stream's life, so the disk thread
///     never allocates one. The disk thread asks for a renderer per entry (<see cref="For" />); one that last rendered
///     another entry, or a fill before the current one (<see cref="Begin" />), is reset first, so it primes and never
///     carries one entry's stream (or a stale model's) into another.
/// </summary>
public sealed class FlexLane : IDisposable {
    const int Kinds = (int)FlexKind.Pitch + 1;

    readonly IFlexRenderer?[] renderers = new IFlexRenderer?[Kinds];
    readonly long[] bound = new long[Kinds]; // disk thread: the entry each renderer last rendered (−1: none)
    long fill = -1; // disk thread: the fill being rendered

    public FlexLane(int channels) {
        Channels = channels;
        bound.AsSpan().Fill(-1);
    }

    /// <summary>The stream's channel count, which every renderer renders.</summary>
    public int Channels { get; }

    /// <summary>The algorithms this lane has renderers for (control thread).</summary>
    public IEnumerable<FlexKind> Algorithms {
        get {
            for (var k = 1; k < Kinds; k++) {
                if (Volatile.Read(ref renderers[k]) is not null) {
                    yield return (FlexKind)k;
                }
            }
        }
    }

    /// <summary>
    ///     Control thread, before publishing a model that needs it: makes the renderer of <paramref name="algorithm" />
    ///     unless the lane has one. <see cref="FlexKind.None" /> and unknown algorithms need none.
    /// </summary>
    public void Ensure(FlexKind algorithm, int sampleRate, int maxFrames) {
        var k = (int)algorithm;
        if (k is <= 0 or >= Kinds || Volatile.Read(ref renderers[k]) is not null) {
            return;
        }

        Volatile.Write(ref renderers[k], FlexRenderers.Create(algorithm, Channels, sampleRate, maxFrames));
    }

    /// <summary>Disk thread: the stream renders fill <paramref name="identity" /> now; a new fill resets every renderer.</summary>
    public void Begin(long identity) {
        if (identity != fill) {
            fill = identity;
            bound.AsSpan().Fill(-1);
        }
    }

    /// <summary>
    ///     Disk thread: the renderer of <paramref name="algorithm" /> for playlist entry <paramref name="entry" />, reset
    ///     when it last rendered something else (null: the lane has none, and the entry plays silence).
    /// </summary>
    public IFlexRenderer? For(FlexKind algorithm, long entry) {
        var k = (int)algorithm;
        if (k is <= 0 or >= Kinds || Volatile.Read(ref renderers[k]) is not { } renderer) {
            return null;
        }

        if (bound[k] != entry) {
            renderer.Reset();
            bound[k] = entry;
        }

        return renderer;
    }

    /// <summary>Once no thread renders through the lane (its track retired).</summary>
    public void Dispose() {
        for (var k = 0; k < Kinds; k++) {
            renderers[k]?.Dispose();
            renderers[k] = null;
        }
    }
}
