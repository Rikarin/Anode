namespace Anode.Engine;

public enum TransportState {
    Stopped,
    Playing
}

/// <summary>
///     The playhead, in samples. Advanced by the audio callback, one buffer at a time, so it never allocates or locks;
///     commands from the control thread arrive through the engine's command queue (still to come), not by calling
///     <see cref="Play" /> from another thread.
/// </summary>
public sealed class Transport {
    public TransportState State { get; private set; }

    /// <summary>The sample position of the next buffer's first frame.</summary>
    public long Position { get; private set; }

    public void Play() => State = TransportState.Playing;

    public void Stop() => State = TransportState.Stopped;

    public void Locate(long position) {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        Position = position;
    }

    /// <summary>Moves the playhead past a rendered buffer of <paramref name="frames" />, if playing.</summary>
    public void Advance(int frames) {
        if (State == TransportState.Playing) {
            Position += frames;
        }
    }
}
