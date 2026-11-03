using Anode.Ipc.Segments;
using System.Runtime.InteropServices;

namespace Anode.Server.Engine;

/// <summary>
///     A run of the master tap's audio as the reader hands it on: <see cref="Frames" /> interleaved stereo frames whose
///     first is project sample <see cref="TransportSample" /> (−1 while stopped); <see cref="Discontinuity" /> when it
///     does not follow the previous run (a locate, a cycle jump, an overrun, the start).
/// </summary>
public readonly record struct TapRun(int Offset, int Frames, long TransportSample, bool Playing, bool Discontinuity);

/// <summary>
///     Reads the host's master tap ring (segment format 5, <see cref="MasterTapHeader" />, docs/11-phase-10.md) as its
///     single reader: from its own position up to the published write position, skipping to half a ring behind on an
///     overrun, re-checking after the copy, and splitting what it read at the stamps so each run has one transport
///     mapping. Over a mapped region (<see cref="TryOpen" />) or any memory laid out so (tests).
/// </summary>
public sealed unsafe class MasterTapReader : IDisposable {
    readonly SharedSegment? segment;
    readonly byte* region;
    long position = -1;
    bool pendingDiscontinuity = true;

    MasterTapReader(SharedSegment? segment, byte* region) {
        this.segment = segment;
        this.region = region;
    }

    /// <summary>Over a ring at <paramref name="region" /> the caller keeps alive (tests, <see cref="MasterTapWriter" />).</summary>
    public static MasterTapReader Over(nint region) => new(null, (byte*)region);

    /// <summary>Maps the ring <paramref name="name" /> (the hello's); null when there is none or it is not a tap.</summary>
    public static MasterTapReader? TryOpen(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        try {
            var segment = SharedSegment.Open(name, MasterTapLayout.Size);
            var reader = new MasterTapReader(segment, segment.Pointer);
            if (!reader.Valid) {
                reader.Dispose();
                return null;
            }

            return reader;
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or InvalidDataException
                                        or PlatformNotSupportedException) {
            return null;
        }
    }

    MasterTapHeader* Header => (MasterTapHeader*)region;

    bool Valid =>
        Header->Magic == MasterTapHeader.MagicValue
        && Header->Format == MasterTapHeader.CurrentFormat
        && Header->Channels == 2
        && Header->CapacityFrames > 0
        && (Header->CapacityFrames & (Header->CapacityFrames - 1)) == 0
        && Header->StampCapacity > 0
        && (Header->StampCapacity & (Header->StampCapacity - 1)) == 0;

    /// <summary>The device's (the project's) sample rate the ring runs at.</summary>
    public int SampleRate => Header->SampleRate;

    /// <summary>Frames published and not read yet (0 before the first read: reading starts at the write position).</summary>
    public long Behind => position < 0 ? 0 : Volatile.Read(ref Header->WriteFrame) - position;

    /// <summary>
    ///     Copies what was written since the last read (at most <paramref name="audio" />'s length / 2 frames) into
    ///     <paramref name="audio" /> and the runs it splits into into <paramref name="runs" />; the frames read. The first
    ///     read starts at the write position (nothing yet).
    /// </summary>
    public int Read(Span<float> audio, List<TapRun> runs) {
        runs.Clear();
        if (!Valid) {
            return 0;
        }

        var capacity = Header->CapacityFrames;
        var write = Volatile.Read(ref Header->WriteFrame);
        var stampCount = Volatile.Read(ref Header->StampCount);
        if (position < 0 || position > write) {
            position = write;
            pendingDiscontinuity = true;
            return 0;
        }

        if (write - position > capacity) {
            // Overrun: half a ring behind the writer, and the listeners start afresh.
            position = write - capacity / 2;
            pendingDiscontinuity = true;
        }

        var count = (int)Math.Min(write - position, audio.Length / 2);
        if (count <= 0) {
            return 0;
        }

        var samples = (float*)(region + Header->SamplesOffset);
        var mask = capacity - 1;
        for (var i = 0; i < count; i++) {
            var at = (int)((position + i) & mask) * 2;
            audio[i * 2] = samples[at];
            audio[i * 2 + 1] = samples[at + 1];
        }

        // Frames the writer overwrote while they were copied are dropped.
        var after = Volatile.Read(ref Header->WriteFrame);
        var lost = after - capacity - position;
        var skip = (int)Math.Clamp(lost, 0, count);
        if (skip > 0) {
            audio.Slice(skip * 2, (count - skip) * 2).CopyTo(audio);
            count -= skip;
            position += skip;
            pendingDiscontinuity = true;
        }

        Split(position, count, stampCount, runs);
        position += count;
        return count;
    }

    /// <summary>Splits frames [<paramref name="start" />, start + count) at the stamps that begin inside them.</summary>
    void Split(long start, int count, long stampCount, List<TapRun> runs) {
        var stamps = (TapStamp*)(region + Header->StampsOffset);
        var stampMask = Header->StampCapacity - 1;
        var oldest = Math.Max(0, stampCount - Header->StampCapacity);
        // The stamps that apply: the latest at or before start, then those inside the range, oldest first.
        var applying = new List<TapStamp>();
        TapStamp? before = null;
        for (var k = stampCount - 1; k >= oldest; k--) {
            var stamp = stamps[k & stampMask];
            if (stamp.Frame >= start + count) {
                continue;
            }

            if (stamp.Frame <= start) {
                before = stamp;
                break;
            }

            applying.Add(stamp);
        }

        applying.Reverse();
        var at = 0;
        var current = before;
        var discontinuity = pendingDiscontinuity
            || (before is { } first && first.Frame == start && (first.Flags & TapStamp.FlagDiscontinuity) != 0);
        pendingDiscontinuity = false;
        foreach (var stamp in applying) {
            var length = (int)(stamp.Frame - start) - at;
            if (length > 0) {
                runs.Add(Run(current, start + at, at, length, discontinuity));
                discontinuity = false;
            }

            at += Math.Max(0, length);
            current = stamp;
            discontinuity |= (stamp.Flags & TapStamp.FlagDiscontinuity) != 0;
        }

        if (count - at > 0) {
            runs.Add(Run(current, start + at, at, count - at, discontinuity));
        }
    }

    static TapRun Run(TapStamp? stamp, long frame, int offset, int length, bool discontinuity) {
        var playing = stamp is { } s && (s.Flags & TapStamp.FlagPlaying) != 0 && s.TransportSample >= 0;
        var transport = playing ? stamp!.Value.TransportSample + (frame - stamp.Value.Frame) : -1;
        return new(offset, length, transport, playing, discontinuity);
    }

    public void Dispose() => segment?.Dispose();
}

/// <summary>
///     Writes a master tap ring as the host's audio callback does (<see cref="FakeHost" /> and tests): frames, then the
///     block's stamp, then the counters with release stores, stamp count first.
/// </summary>
public sealed unsafe class MasterTapWriter : IDisposable {
    readonly SharedSegment? segment;
    readonly byte* region;
    readonly nint owned;

    MasterTapWriter(SharedSegment? segment, byte* region, nint owned) {
        this.segment = segment;
        this.region = region;
        this.owned = owned;
    }

    /// <summary>A ring in a new shared region (<see cref="Name" /> for the hello).</summary>
    public static MasterTapWriter Create(string prefix, long id, int sampleRate) {
        var segment = SharedSegment.Create(
            SegmentWriter.DefaultBackend,
            SharedSegment.DefaultDirectory(),
            prefix,
            id,
            MasterTapLayout.Size
        );
        var writer = new MasterTapWriter(segment, segment.Pointer, 0);
        writer.Initialise(sampleRate, MasterTapHeader.DefaultCapacityFrames, MasterTapHeader.DefaultStampCapacity);
        return writer;
    }

    /// <summary>A ring in process memory with the given capacities (tests); <see cref="Region" /> for the reader.</summary>
    public static MasterTapWriter InMemory(int sampleRate, int capacityFrames, int stampCapacity) {
        var size = MasterTapLayout.Offsets(capacityFrames, stampCapacity).Total;
        var memory = (nint)NativeMemory.AllocZeroed((nuint)size);
        var writer = new MasterTapWriter(null, (byte*)memory, memory);
        writer.Initialise(sampleRate, capacityFrames, stampCapacity);
        return writer;
    }

    public string? Name => segment?.Name;

    public nint Region => (nint)region;

    MasterTapHeader* Header => (MasterTapHeader*)region;

    void Initialise(int sampleRate, int capacityFrames, int stampCapacity) {
        var (samples, stamps, total) = MasterTapLayout.Offsets(capacityFrames, stampCapacity);
        *Header = new() {
            Magic = MasterTapHeader.MagicValue,
            Format = MasterTapHeader.CurrentFormat,
            HeaderSize = (ushort)sizeof(MasterTapHeader),
            SampleRate = sampleRate,
            Channels = 2,
            CapacityFrames = capacityFrames,
            StampCapacity = stampCapacity,
            SamplesOffset = samples,
            StampsOffset = stamps,
            TotalLength = total
        };
    }

    /// <summary>Writes one block: <paramref name="interleaved" /> stereo frames, at <paramref name="transportSample" />.</summary>
    public void Write(ReadOnlySpan<float> interleaved, long transportSample, bool playing, bool discontinuity) {
        var frames = interleaved.Length / 2;
        var write = Header->WriteFrame;
        var mask = Header->CapacityFrames - 1;
        var samples = (float*)(region + Header->SamplesOffset);
        for (var i = 0; i < frames; i++) {
            var at = (int)((write + i) & mask) * 2;
            samples[at] = interleaved[i * 2];
            samples[at + 1] = interleaved[i * 2 + 1];
        }

        var count = Header->StampCount;
        var stamps = (TapStamp*)(region + Header->StampsOffset);
        stamps[count & (Header->StampCapacity - 1)] = new() {
            Frame = write,
            TransportSample = playing ? transportSample : -1,
            Flags = (playing ? TapStamp.FlagPlaying : 0) | (discontinuity ? TapStamp.FlagDiscontinuity : 0)
        };
        Volatile.Write(ref Header->StampCount, count + 1);
        Volatile.Write(ref Header->WriteFrame, write + frames);
    }

    public void Dispose() {
        segment?.Dispose();
        if (owned != 0) {
            NativeMemory.Free((void*)owned);
        }
    }
}
