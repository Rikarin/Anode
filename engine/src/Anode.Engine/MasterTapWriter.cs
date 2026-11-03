using Anode.Ipc.Segments;
using System.Runtime.InteropServices;

namespace Anode.Engine;

/// <summary>
///     The writer of the master tap ring (segment format 5, <see cref="MasterTapHeader" />, docs/11-phase-10.md): the
///     audio callback appends each block's stereo frames and its transport stamp, then publishes the counters with
///     release stores (the stamp count first, then the write frame). Single writer, never waits, allocation-free.
/// </summary>
public sealed unsafe class MasterTapWriter {
    readonly MasterTapHeader* header;
    readonly float* samples;
    readonly TapStamp* stamps;
    readonly int frameMask;
    readonly int stampMask;

    /// <param name="region">At least <see cref="MasterTapLayout.Size" /> writable bytes, shared with the reader.</param>
    /// <param name="sampleRate">The device's rate.</param>
    public MasterTapWriter(byte* region, int sampleRate) {
        var (samplesOffset, stampsOffset, total) = MasterTapLayout.Offsets();
        NativeMemory.Clear(region, (nuint)total);
        header = (MasterTapHeader*)region;
        samples = (float*)(region + samplesOffset);
        stamps = (TapStamp*)(region + stampsOffset);
        frameMask = MasterTapHeader.DefaultCapacityFrames - 1;
        stampMask = MasterTapHeader.DefaultStampCapacity - 1;
        header->Format = MasterTapHeader.CurrentFormat;
        header->HeaderSize = (ushort)sizeof(MasterTapHeader);
        header->SampleRate = sampleRate;
        header->Channels = 2;
        header->CapacityFrames = MasterTapHeader.DefaultCapacityFrames;
        header->StampCapacity = MasterTapHeader.DefaultStampCapacity;
        header->SamplesOffset = samplesOffset;
        header->StampsOffset = stampsOffset;
        header->TotalLength = total;
        Volatile.Write(ref header->Magic, MasterTapHeader.MagicValue); // last, so a reader never sees half of it
    }

    /// <summary>Frames written so far.</summary>
    public long WriteFrame => Volatile.Read(ref header->WriteFrame);

    /// <summary>
    ///     Audio thread: appends a block of the master's stereo and its stamp (<paramref name="transportSample" /> the
    ///     project sample of its first frame, −1 stopped; <see cref="TapStamp" /> flags).
    /// </summary>
    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, long transportSample, int flags) {
        var frame = header->WriteFrame;
        var count = header->StampCount;
        for (var i = 0; i < left.Length; i++) {
            var at = 2 * (int)((frame + i) & frameMask);
            samples[at] = left[i];
            samples[at + 1] = right[i];
        }

        stamps[count & stampMask] = new() { Frame = frame, TransportSample = transportSample, Flags = flags };
        Volatile.Write(ref header->StampCount, count + 1);
        Volatile.Write(ref header->WriteFrame, frame + left.Length);
    }

    /// <summary>
    ///     Reader side (tests and in-process readers): frames <paramref name="from" /> … <paramref name="from" /> +
    ///     <paramref name="interleaved" />.Length / 2 into <paramref name="interleaved" />, and the stamps written so far
    ///     (the latest <see cref="MasterTapHeader.StampCapacity" />) into <paramref name="stampsOut" />; returns the stamp
    ///     count and the write frame.
    /// </summary>
    public static (long Stamps, long WriteFrame) Read(
        byte* region,
        long from,
        Span<float> interleaved,
        List<TapStamp> stampsOut
    ) {
        var h = (MasterTapHeader*)region;
        var write = Volatile.Read(ref h->WriteFrame);
        var count = Volatile.Read(ref h->StampCount);
        var ring = (float*)(region + h->SamplesOffset);
        var stampRing = (TapStamp*)(region + h->StampsOffset);
        var mask = h->CapacityFrames - 1;
        for (var i = 0; i < interleaved.Length / 2; i++) {
            var at = 2 * (int)((from + i) & mask);
            interleaved[2 * i] = ring[at];
            interleaved[2 * i + 1] = ring[at + 1];
        }

        for (var k = Math.Max(0, count - h->StampCapacity); k < count; k++) {
            stampsOut.Add(stampRing[k & (h->StampCapacity - 1)]);
        }

        return (count, write);
    }
}
