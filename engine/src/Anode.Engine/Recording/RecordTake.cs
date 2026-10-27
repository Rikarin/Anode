using Anode.Engine.Streaming;

namespace Anode.Engine.Recording;

/// <summary>
///     One armed track's take in a recording pass: the audio thread pushes the track's input into a lock-free ring,
///     the writer thread drains it into a 24-bit WAV file (docs/02-phase-1.md, "Recording").
/// </summary>
/// <remarks>
///     The take is shifted by the device's round-trip latency (<see cref="LatencyFrames" />), so what was played along
///     to lines up with what was heard: input that arrives at timeline position P was played at P − latency. When that
///     would start before the project's zero, the leading frames are dropped.
/// </remarks>
public sealed unsafe class RecordTake : IDisposable {
    readonly WavWriter writer;
    long startPosition = -1;
    long pushed;
    long dropped;
    long skip = -1; // writer: leading frames still to drop (−1: not known yet)

    public RecordTake(int trackId, string path, int channels, int sampleRate, int latencyFrames, double ringSeconds = 4) {
        TrackId = trackId;
        Channels = channels;
        SampleRate = sampleRate;
        LatencyFrames = Math.Max(0, latencyFrames);
        Ring = new(checked((int)(sampleRate * ringSeconds) * channels));
        writer = new(path, channels, sampleRate);
    }

    public int TrackId { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public int LatencyFrames { get; }

    public string Path => writer.Path;

    public SpscRing Ring { get; }

    /// <summary>The timeline position of the first frame pushed (−1 until then).</summary>
    public long StartPosition => Volatile.Read(ref startPosition);

    /// <summary>Frames the audio thread pushed.</summary>
    public long Pushed => Volatile.Read(ref pushed);

    /// <summary>Frames the audio thread could not push because the ring was full (the writer fell behind).</summary>
    public long Dropped => Volatile.Read(ref dropped);

    /// <summary>Frames in the file so far.</summary>
    public long Frames => writer.Frames;

    /// <summary>
    ///     Audio thread: appends <paramref name="frames" /> frames of input (null channels record silence), recorded at
    ///     timeline <paramref name="position" />.
    /// </summary>
    public void Push(float* left, float* right, int frames, long position) {
        if (startPosition < 0) {
            Volatile.Write(ref startPosition, position);
        }

        var samples = frames * Channels;
        if (Ring.Free < samples) {
            Volatile.Write(ref dropped, dropped + frames);
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
        var start = StartPosition;
        if (start < 0) {
            return 0;
        }

        if (skip < 0) {
            skip = Math.Max(0, LatencyFrames - start);
        }

        var total = 0;
        while (true) {
            var available = Ring.Available;
            if (available == 0) {
                return total;
            }

            var samples = Math.Min(available, scratch.Length - scratch.Length % Channels);
            var chunk = scratch[..samples];
            Ring.Read(chunk);
            var frames = samples / Channels;
            var drop = (int)Math.Min(skip, frames);
            skip -= drop;
            writer.Write(chunk[(drop * Channels)..]);
            total += frames - drop;
        }
    }

    /// <summary>
    ///     Control thread, once nothing pushes or drains any more: writes what is left, completes the file and describes
    ///     it.
    /// </summary>
    public Ipc.RecordedTake Complete(Span<float> scratch) {
        Drain(scratch);
        writer.Complete();
        var start = StartPosition < 0 ? 0 : Math.Max(0, StartPosition - LatencyFrames);
        return new(TrackId, Path, start, writer.Frames, Channels, SampleRate);
    }

    public void Dispose() {
        writer.Dispose();
        Ring.Dispose();
    }
}
