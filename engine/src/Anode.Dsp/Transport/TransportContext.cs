namespace Anode.Dsp.Transport;

/// <summary>
///     Where the transport is for one block (docs/07-phase-6.md, "Tempo in the engine"): whether it plays; the
///     block's first project sample and its position in quarter notes from the project start; the playback tempo there
///     (quarter notes per minute of playback, Varispeed included); the time signature in effect and the quarter-note
///     position of the bar the block starts in; the sample rate; and the block's length in frames.
/// </summary>
/// <remarks>
///     The engine computes it before each block from the segment's tempo points (<c>Segments.TempoMapPoint</c>), so it
///     is exact across tempo ramps; it splits a block at a cycle jump, so a context never spans one. Within a block,
///     positions advance at <see cref="Bpm" /> (blocks are short). Effects and instruments get it through
///     <c>SetTransport</c> once per block before <c>Process</c>; MIDI effects as an argument of their
///     <c>Process</c>. While stopped, <see cref="Playing" /> is false and the position is the playhead's.
/// </remarks>
public readonly record struct TransportContext(
    bool Playing,
    long Sample,
    double Quarter,
    double Bpm,
    int Numerator,
    int Denominator,
    double BarStartQuarter,
    double SampleRate,
    int Frames
) {
    /// <summary>Quarter notes per sample at <see cref="Bpm" />.</summary>
    public double QuartersPerSample => Bpm / (60 * SampleRate);

    /// <summary>A bar's length in quarter notes.</summary>
    public double BarQuarters => Numerator * 4.0 / Denominator;

    /// <summary>The sample offset into the block at which quarter-note position <paramref name="quarter" /> falls.</summary>
    public double OffsetOf(double quarter) => (quarter - Quarter) / QuartersPerSample;

    /// <summary>A stopped transport at the project start: 120 bpm, 4/4.</summary>
    public static TransportContext Stopped(double sampleRate, int frames) =>
        new(false, 0, 0, 120, 4, 4, 0, sampleRate, frames);
}
