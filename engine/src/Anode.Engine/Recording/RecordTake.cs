using Anode.Engine.Streaming;
using Anode.Ipc;

namespace Anode.Engine.Recording;

/// <summary>
///     One armed track's take in a recording: the audio thread pushes the track's input into a lock-free ring, the
///     writer thread drains it into a 24-bit WAV file (docs/02-phase-1.md, "Recording"). One file holds the whole
///     recording; each run of frames the audio thread pushes says where it was played, so a cycle recording (or a
///     punch inside a cycle) is one file of several passes (<see cref="RecordedTake.Passes" />).
/// </summary>
/// <remarks>
///     The audio thread pushes positions already shifted by the device's round-trip latency (input that arrives at
///     timeline position P was played at P − latency), and only the frames it keeps: inside the punch range when
///     autopunch is on, and never before the project's zero.
/// </remarks>
public sealed unsafe class RecordTake : IDisposable {
    /// <summary>Passes a take can describe; a recording that wraps more often merges the rest into its last pass.</summary>
    public const int MaxPasses = 4096;

    readonly WavWriter writer;
    readonly long[] passPositions = new long[MaxPasses];
    readonly long[] passOffsets = new long[MaxPasses];
    int passes; // audio thread; published by `pushed`
    long nextPosition = long.MinValue;
    long pushed;
    long dropped;

    public RecordTake(int trackId, string path, int channels, int sampleRate, double ringSeconds = 4) {
        TrackId = trackId;
        Channels = channels;
        SampleRate = sampleRate;
        Ring = new(checked((int)(sampleRate * ringSeconds) * channels));
        writer = new(path, channels, sampleRate);
    }

    public int TrackId { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public string Path => writer.Path;

    public SpscRing Ring { get; }

    /// <summary>The timeline position of the first frame kept (−1 until then).</summary>
    public long StartPosition => Volatile.Read(ref pushed) > 0 ? passPositions[0] : -1;

    /// <summary>Frames the audio thread pushed.</summary>
    public long Pushed => Volatile.Read(ref pushed);

    /// <summary>Frames the audio thread could not push because the ring was full (the writer fell behind).</summary>
    public long Dropped => Volatile.Read(ref dropped);

    /// <summary>Frames in the file so far.</summary>
    public long Frames => writer.Frames;

    /// <summary>
    ///     Audio thread: appends <paramref name="frames" /> frames of input (null channels record silence) played at
    ///     timeline positions [<paramref name="position" />, + <paramref name="frames" />). A position that does not
    ///     continue the last push starts a new pass.
    /// </summary>
    public void Push(float* left, float* right, int frames, long position) {
        if (frames <= 0) {
            return;
        }

        if (position != nextPosition && passes < MaxPasses) {
            passPositions[passes] = position;
            passOffsets[passes] = pushed;
            passes++;
        }

        nextPosition = position + frames;
        var samples = frames * Channels;
        if (Ring.Free < samples) {
            Volatile.Write(ref dropped, dropped + frames); // the rest of the pass plays early by as much
            return;
        }

        Ring.WriteRegions(samples, out var first, out var second);
        for (var f = 0; f < frames; f++) {
            for (var c = 0; c < Channels; c++) {
                var source = c == 0 ? left : right;
                var value = source is null ? 0f : source[f];
                var at = f * Channels + c;
                if (at < first.Length) {
                    first[at] = value;
                } else {
                    second[at - first.Length] = value;
                }
            }
        }

        Ring.CommitWrite(samples);
        Volatile.Write(ref pushed, pushed + frames);
    }

    /// <summary>Writer thread: moves what the ring holds into the file. Returns the frames written.</summary>
    public int Drain(Span<float> scratch) {
        var total = 0;
        while (true) {
            var available = Ring.Available;
            if (available == 0) {
                return total;
            }

            var samples = Math.Min(available, scratch.Length - scratch.Length % Channels);
            var chunk = scratch[..samples];
            Ring.Read(chunk);
            writer.Write(chunk);
            total += samples / Channels;
        }
    }

    /// <summary>
    ///     Control thread, once nothing pushes or drains any more: writes what is left, completes the file and describes
    ///     it.
    /// </summary>
    public RecordedTake Complete(Span<float> scratch) {
        Drain(scratch);
        writer.Complete();
        return Describe(TrackId, Path, Channels, SampleRate, writer.Frames, passPositions, passOffsets, passes);
    }

    /// <summary>
    ///     A take of <paramref name="frames" /> frames whose passes start at file frames <paramref name="offsets" />,
    ///     played from <paramref name="positions" />: one linear pass has no <see cref="RecordedTake.Passes" />.
    /// </summary>
    public static RecordedTake Describe(
        int trackId,
        string path,
        int channels,
        int sampleRate,
        long frames,
        ReadOnlySpan<long> positions,
        ReadOnlySpan<long> offsets,
        int count
    ) {
        if (count <= 1 || frames == 0) {
            return new(trackId, path, count == 0 ? 0 : positions[0], frames, channels, sampleRate);
        }

        var list = new RecordedPass[count];
        for (var i = 0; i < count; i++) {
            var end = i + 1 < count ? offsets[i + 1] : frames;
            list[i] = new(positions[i], offsets[i], end - offsets[i]);
        }

        return new(trackId, path, positions[0], frames, channels, sampleRate, list);
    }

    public void Dispose() {
        writer.Dispose();
        Ring.Dispose();
    }
}
