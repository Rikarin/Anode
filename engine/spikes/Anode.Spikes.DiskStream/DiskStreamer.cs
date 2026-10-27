using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anode.Spikes.DiskStream;

/// <summary>One mono track: its open file and the ring the disk thread fills in play order.</summary>
sealed class StreamTrack : IDisposable {
    public StreamTrack(int index, string path, int ringFrames, bool noCache) {
        Index = index;
        Handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        if (noCache) {
            Posix.SetNoCache(Handle);
        }

        var (dataOffset, frames) = WavFile.ReadHeader(Handle);
        DataOffset = dataOffset;
        Frames = frames;
        FileLength = RandomAccess.GetLength(Handle);
        Ring = new(ringFrames);
    }

    public int Index { get; }

    public SafeFileHandle Handle { get; }

    public long DataOffset { get; }

    public long FileLength { get; }

    /// <summary>Frames in the file; the region plays silence after them.</summary>
    public long Frames { get; }

    public SpscRing Ring { get; }

    /// <summary>Disk-thread state: the timeline position of the next frame to put in the ring.</summary>
    public long FillPosition { get; set; }

    /// <summary>Audio-thread state: frames the callback already played as silence and must drop when they arrive.</summary>
    public long Debt { get; set; }

    public void Dispose() {
        Handle.Dispose();
        Ring.Dispose();
    }
}

/// <summary>
///     The prefetch side. Each disk thread owns a share of the tracks; it refills the emptiest ring that has room for a
///     whole read, with one large positional read (page-aligned, so <c>F_NOCACHE</c> needs no bounce buffer), decodes
///     24-bit to float straight into the ring and follows the cycle: past the cycle end it reads the cycle start, so the
///     ring already holds the audio after the jump.
/// </summary>
sealed unsafe class DiskStreamer : IDisposable {
    const int Page = 4096;
    readonly StreamTrack[] tracks;
    readonly Cycle cycle;
    readonly int readFrames;
    readonly Thread[] threads;
    readonly Histogram[] readLatency;
    readonly long[] bytesRead;
    readonly long[] reads;
    volatile bool running = true;

    public DiskStreamer(StreamTrack[] tracks, Cycle cycle, int readFrames, int threadCount) {
        this.tracks = tracks;
        this.cycle = cycle;
        this.readFrames = readFrames;
        threads = new Thread[threadCount];
        readLatency = new Histogram[threadCount];
        bytesRead = new long[threadCount];
        reads = new long[threadCount];
        for (var i = 0; i < threadCount; i++) {
            readLatency[i] = new(10, 100_000); // 10 µs bins up to 1 s
            var worker = i;
            threads[i] = new(() => Run(worker)) {
                IsBackground = true, Name = $"disk {i}", Priority = ThreadPriority.AboveNormal
            };
        }
    }

    public long BytesRead {
        get {
            long sum = 0;
            for (var i = 0; i < bytesRead.Length; i++) {
                sum += Volatile.Read(ref bytesRead[i]);
            }

            return sum;
        }
    }

    public long Reads {
        get {
            long sum = 0;
            for (var i = 0; i < reads.Length; i++) {
                sum += Volatile.Read(ref reads[i]);
            }

            return sum;
        }
    }

    public void Start() {
        foreach (var t in threads) {
            t.Start();
        }
    }

    /// <summary>A snapshot of read latency over all disk threads (racy by design: it is diagnostics).</summary>
    public Histogram ReadLatency() {
        var sum = new Histogram(10, 100_000);
        foreach (var h in readLatency) {
            sum.Add(h);
        }

        return sum;
    }

    public void ResetStats() {
        foreach (var h in readLatency) {
            h.Clear();
        }
    }

    void Run(int worker) {
        // One aligned scratch buffer per thread: a whole read plus a page of slack at either end.
        var scratchBytes = readFrames * WavFile.BytesPerFrame + 2 * Page;
        var scratch = (byte*)NativeMemory.AlignedAlloc((nuint)scratchBytes, Page);
        try {
            while (running) {
                var track = MostUrgent(worker);
                if (track is null) {
                    Thread.Sleep(1);
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                var bytes = Fill(track, new(scratch, scratchBytes));
                readLatency[worker].RecordTicks(Stopwatch.GetTimestamp() - started);
                Volatile.Write(ref bytesRead[worker], bytesRead[worker] + bytes);
                Volatile.Write(ref reads[worker], reads[worker] + 1);
            }
        } finally {
            NativeMemory.AlignedFree(scratch);
        }
    }

    /// <summary>The track of this worker's share with the least audio buffered that has room for a whole read.</summary>
    StreamTrack? MostUrgent(int worker) {
        StreamTrack? best = null;
        var bestAvailable = int.MaxValue;
        for (var i = worker; i < tracks.Length; i += threads.Length) {
            var ring = tracks[i].Ring;
            if (ring.Free >= readFrames) {
                var available = ring.Available;
                if (available < bestAvailable) {
                    best = tracks[i];
                    bestAvailable = available;
                }
            }
        }

        return best;
    }

    /// <summary>Reads, decodes and publishes one chunk; returns the bytes read from the file.</summary>
    int Fill(StreamTrack track, Span<byte> scratch) {
        var position = track.FillPosition;
        var count = (int)Math.Min(readFrames, cycle.UntilJump(position));
        var inFile = (int)Math.Clamp(track.Frames - position, 0, count);
        var bytes = 0;
        track.Ring.WriteRegions(count, out var first, out var second);
        if (inFile > 0) {
            var from = track.DataOffset + position * WavFile.BytesPerFrame;
            var to = from + (long)inFile * WavFile.BytesPerFrame;
            var alignedFrom = from & ~(long)(Page - 1);
            var alignedTo = Math.Min((to + Page - 1) & ~(long)(Page - 1), track.FileLength);
            var length = (int)(alignedTo - alignedFrom);
            var read = 0;
            while (read < length) {
                var n = RandomAccess.Read(track.Handle, scratch.Slice(read, length - read), alignedFrom + read);
                if (n <= 0) {
                    break;
                }

                read += n;
            }

            bytes = read;
            var source = scratch.Slice((int)(from - alignedFrom), inFile * WavFile.BytesPerFrame);
            Decode(source, first, second);
        }

        // Past the end of the file the region is silent.
        for (var i = inFile; i < count; i++) {
            if (i < first.Length) {
                first[i] = 0;
            } else {
                second[i - first.Length] = 0;
            }
        }

        track.Ring.CommitWrite(count);
        track.FillPosition = cycle.Advance(position, count);
        return bytes;
    }

    static void Decode(ReadOnlySpan<byte> source, Span<float> first, Span<float> second) {
        var frames = source.Length / WavFile.BytesPerFrame;
        var head = Math.Min(frames, first.Length);
        for (var i = 0; i < head; i++) {
            first[i] = Sample(source, i * 3);
        }

        for (var i = head; i < frames; i++) {
            second[i - head] = Sample(source, i * 3);
        }
    }

    /// <summary>A little-endian signed 24-bit sample as float in [-1, 1).</summary>
    static float Sample(ReadOnlySpan<byte> s, int at) =>
        (((s[at] << 8) | (s[at + 1] << 16) | (s[at + 2] << 24)) >> 8) * (1f / 8_388_608f);

    public void Dispose() {
        running = false;
        foreach (var t in threads) {
            if (t.IsAlive) {
                t.Join();
            }
        }
    }
}
