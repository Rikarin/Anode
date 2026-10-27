namespace Anode.Spikes.DiskStream;

/// <summary>
///     The transport's cycle (loop) region in timeline frames, shared by the disk thread and the audio thread so both
///     walk the same play order: up to <see cref="End" />, then on from <see cref="Start" />.
/// </summary>
readonly record struct Cycle(bool Enabled, long Start, long End) {
    public static Cycle Off => new(false, 0, 0);

    /// <summary>Frames that can be played from <paramref name="position" /> before the jump.</summary>
    public long UntilJump(long position) => Enabled && position < End ? End - position : long.MaxValue;

    /// <summary>The position <paramref name="frames" /> after <paramref name="position" /> in play order.</summary>
    public long Advance(long position, long frames) {
        if (!Enabled || position >= End) {
            return position + frames;
        }

        var next = position + frames;
        return next < End ? next : Start + (next - End) % (End - Start);
    }
}
