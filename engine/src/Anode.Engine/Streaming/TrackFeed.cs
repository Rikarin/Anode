namespace Anode.Engine.Streaming;

/// <summary>
///     One track's audio between the disk thread (producer) and the audio thread (consumer): an interleaved float ring
///     plus a queue saying which transport clock each run of frames ("chunk") belongs to and which fill produced it.
/// </summary>
/// <remarks>
///     <para>
///         A fill's identity (<see cref="Identity" />) names the anchor and the track's compiled generation it was
///         rendered for. When either changes (a locate, a cycle change, an edit), the disk thread starts a new fill at
///         a clock a little ahead and queues it behind the old one; the consumer plays the old audio until the new
///         fill's first clock and then splices to it, sample-exactly, so an edit or a cycle change during playback
///         does not gap. Audio from before a hard anchor (a locate) is dropped at once instead.
///     </para>
///     <para>
///         The ring has room for <see cref="TargetFrames" /> plus a headroom of half that again, so a new fill can be
///         queued while the old one still holds the ring's target.
///     </para>
/// </remarks>
public sealed unsafe class TrackFeed : IDisposable {
    const int QueueCapacity = 512; // power of two
    const int IdentityGenerationBits = 24;

    readonly Chunk[] queue = new Chunk[QueueCapacity];
    long head; // consumer-owned
    long tail; // producer-owned
    int headConsumed; // consumer: frames of the head chunk already consumed

    // Disk thread state, published for the control thread's priming check.
    long fillIdentity = -1;
    long fillCursor;
    long fillStart;

    public TrackFeed(int channels, int targetFrames) {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 2);
        Channels = channels;
        TargetFrames = targetFrames;
        Ring = new(checked((targetFrames + targetFrames / 2) * channels));
        Flex = new(channels);
    }

    public int Channels { get; }

    /// <summary>Frames the disk thread keeps queued ahead of the playhead.</summary>
    public int TargetFrames { get; }

    public SpscRing Ring { get; }

    /// <summary>The flex renderers of the warped entries this stream plays (docs/06-phase-5.md).</summary>
    public FlexLane Flex { get; }

    /// <summary>
    ///     The identity of a fill for anchor <paramref name="anchorId" /> and compiled generation
    ///     <paramref name="generation" />.
    /// </summary>
    public static long Identity(long anchorId, int generation) =>
        (anchorId << IdentityGenerationBits) | (uint)(generation & ((1 << IdentityGenerationBits) - 1));

    public static long AnchorOf(long identity) => identity >> IdentityGenerationBits;

    // ---- Producer (disk thread) ------------------------------------------------------------------------------------

    /// <summary>The identity of the fill in progress (-1 before the first).</summary>
    public long FillIdentity => Volatile.Read(ref fillIdentity);

    /// <summary>The clock of the next frame the current fill will write.</summary>
    public long FillCursor => Volatile.Read(ref fillCursor);

    /// <summary>The clock the current fill started at.</summary>
    public long FillStart => fillStart;

    /// <summary>Starts a new fill at <paramref name="startClock" />; earlier fills stay queued for the consumer to drop.</summary>
    public void BeginFill(long identity, long startClock) {
        fillStart = startClock;
        Volatile.Write(ref fillCursor, startClock);
        Volatile.Write(ref fillIdentity, identity);
    }

    /// <summary>Frames the producer can queue now (ring space and a queue slot).</summary>
    public int FreeFrames => tail - Volatile.Read(ref head) >= QueueCapacity ? 0 : Ring.Free / Channels;

    /// <summary>
    ///     Queues <paramref name="interleaved" /> (whole frames) as the current fill's next frames and advances its
    ///     cursor. It must fit <see cref="FreeFrames" />.
    /// </summary>
    public void Append(ReadOnlySpan<float> interleaved) {
        var frames = interleaved.Length / Channels;
        if (frames == 0) {
            return;
        }

        Ring.Write(interleaved[..(frames * Channels)]);
        ref var slot = ref queue[tail & (QueueCapacity - 1)];
        slot = new(fillCursor, frames, fillIdentity);
        Volatile.Write(ref tail, tail + 1);
        Volatile.Write(ref fillCursor, fillCursor + frames);
    }

    // ---- Consumer (audio thread) -----------------------------------------------------------------------------------

    /// <summary>
    ///     Reads the frames for clocks [<paramref name="clock" />, + <paramref name="frames" />) of fill
    ///     <paramref name="identity" /> into <paramref name="left" /> and <paramref name="right" /> (a mono stream writes
    ///     only <paramref name="left" />), dropping what is stale or late on the way; frames it has nothing for stay as
    ///     they were (zero). Returns how many frames it supplied.
    /// </summary>
    public int Read(long clock, int frames, long identity, long hardFloor, Span<float> left, Span<float> right) {
        var supplied = 0;
        var i = 0;
        while (i < frames && Peek(out var chunk)) {
            var at = chunk.StartClock + headConsumed;
            var remaining = chunk.Frames - headConsumed;
            var want = clock + i;
            var current = chunk.Identity == identity;
            if (Superseded(chunk, identity, hardFloor, want, out var next)) {
                Drop(remaining);
                continue;
            }

            if (at < want) {
                Drop((int)Math.Min(want - at, remaining));
                continue;
            }

            if (at > want) {
                i += (int)Math.Min(at - want, frames - i); // a gap: silence until the chunk starts
                continue;
            }

            var count = Math.Min(frames - i, remaining);
            if (!current && next > want) {
                count = (int)Math.Min(count, next - want);
            }

            Copy(count, left.Slice(i, count), right.Slice(i, count));
            i += count;
            supplied += count;
        }

        return supplied;
    }

    /// <summary>
    ///     While the transport is held: drops what can never play at <paramref name="clock" /> (stale, or before it),
    ///     so a new fill has room. Consumes nothing playable.
    /// </summary>
    public void Align(long clock, long identity, long hardFloor) {
        while (Peek(out var chunk)) {
            var at = chunk.StartClock + headConsumed;
            var remaining = chunk.Frames - headConsumed;
            if (Superseded(chunk, identity, hardFloor, clock, out _)) {
                Drop(remaining);
                continue;
            }

            if (at < clock) {
                Drop((int)Math.Min(clock - at, remaining));
                continue;
            }

            return;
        }
    }

    /// <summary>
    ///     Whether <paramref name="chunk" /> must not play at <paramref name="clock" />: it is of another fill, and
    ///     either from before the last hard anchor or the current fill has begun. <paramref name="next" /> is where the
    ///     current fill begins (−1: not queued yet).
    /// </summary>
    bool Superseded(in Chunk chunk, long identity, long hardFloor, long clock, out long next) {
        next = -1;
        if (chunk.Identity == identity) {
            return false;
        }

        return AnchorOf(chunk.Identity) < hardFloor || (FindCurrent(identity, out next) && next <= clock);
    }

    /// <summary>Consumer: whether fill <paramref name="identity" /> is queued and has begun by <paramref name="clock" />.</summary>
    public bool Started(long identity, long clock) => FindCurrent(identity, out var start) && start <= clock;

    bool Peek(out Chunk chunk) {
        if (head == Volatile.Read(ref tail)) {
            chunk = default;
            return false;
        }

        chunk = queue[head & (QueueCapacity - 1)];
        return true;
    }

    /// <summary>The first clock of the first queued chunk of fill <paramref name="identity" />.</summary>
    bool FindCurrent(long identity, out long startClock) {
        var end = Volatile.Read(ref tail);
        for (var i = head; i < end; i++) {
            ref readonly var chunk = ref queue[i & (QueueCapacity - 1)];
            if (chunk.Identity == identity) {
                startClock = chunk.StartClock;
                return true;
            }
        }

        startClock = -1;
        return false;
    }

    void Drop(int frames) {
        Ring.Skip(frames * Channels);
        Consumed(frames);
    }

    void Copy(int frames, Span<float> left, Span<float> right) {
        Ring.ReadRegions(frames * Channels, out var first, out var second);
        if (Channels == 1) {
            first.CopyTo(left);
            second.CopyTo(left[first.Length..]);
        } else {
            Deinterleave(first, left, right, 0);
            Deinterleave(second, left, right, first.Length / 2);
        }

        Ring.Skip(frames * Channels);
        Consumed(frames);
    }

    static void Deinterleave(ReadOnlySpan<float> source, Span<float> left, Span<float> right, int offset) {
        var frames = source.Length / 2;
        for (var f = 0; f < frames; f++) {
            left[offset + f] = source[2 * f];
            right[offset + f] = source[2 * f + 1];
        }
    }

    void Consumed(int frames) {
        headConsumed += frames;
        if (headConsumed >= queue[head & (QueueCapacity - 1)].Frames) {
            headConsumed = 0;
            Volatile.Write(ref head, head + 1);
        }
    }

    public void Dispose() {
        Ring.Dispose();
        Flex.Dispose();
    }

    readonly record struct Chunk(long StartClock, int Frames, long Identity);
}
