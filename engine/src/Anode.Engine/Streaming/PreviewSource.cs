using System.Runtime.InteropServices;

namespace Anode.Engine.Streaming;

/// <summary>
///     A file playing through the preview channel (docs/04-phase-3.md, "Files and MIDI import"): the disk thread reads
///     it from the start into a stereo ring, and the audio thread adds the ring at <see cref="Gain" /> to the master bus,
///     whatever the transport does, bypassing the tracks. The control thread swaps sources whole (start, replace,
///     stop) and frees the old one after both threads' grace periods.
/// </summary>
public sealed unsafe class PreviewSource : IDisposable {
    const int ReadFrames = 16_384;

    readonly byte* scratch;
    readonly int scratchBytes;
    readonly float[] decoded;
    readonly float[] stereo = GC.AllocateArray<float>(ReadFrames * 2, true);
    long nextFrame; // disk thread
    int finished;

    public PreviewSource(StreamFile file, float gain, int sampleRate) {
        File = file;
        Gain = float.IsFinite(gain) ? Math.Max(0, gain) : 0;
        Ring = new(sampleRate * 2); // one second of stereo
        scratchBytes = StreamFile.ScratchBytes(ReadFrames, file.Format.BytesPerFrame);
        scratch = (byte*)NativeMemory.AlignedAlloc((nuint)scratchBytes, 16_384);
        decoded = GC.AllocateArray<float>(ReadFrames * file.Channels, true);
    }

    public StreamFile File { get; }

    public float Gain { get; }

    public SpscRing Ring { get; }

    /// <summary>The disk thread has queued the whole file.</summary>
    public bool Finished => Volatile.Read(ref finished) != 0;

    /// <summary>Frames the audio thread has not played yet, and none are coming: it has ended.</summary>
    public bool Ended => Finished && Ring.Available == 0;

    /// <summary>How urgently the disk thread should read: frames queued ahead (long.MaxValue: nothing to read).</summary>
    public long Ahead {
        get {
            var queued = Ring.Available / 2;
            return Finished || queued >= Ring.Capacity / 3 || Ring.Free < 4_096 * 2 ? long.MaxValue : queued;
        }
    }

    /// <summary>Disk thread: reads the next part of the file into the ring, when there is room for a useful read.</summary>
    public bool Fill() {
        if (Finished) {
            return false;
        }

        var frames = (int)Math.Min(Math.Min(ReadFrames, Ring.Free / 2), File.Frames - nextFrame);
        if (frames <= 0) {
            if (nextFrame >= File.Frames) {
                Volatile.Write(ref finished, 1);
            }

            return false;
        }

        File.Read(nextFrame, frames, new(scratch, scratchBytes), decoded.AsSpan(0, frames * File.Channels));
        var channels = File.Channels;
        for (var f = 0; f < frames; f++) {
            var left = decoded[f * channels];
            stereo[2 * f] = left;
            stereo[2 * f + 1] = channels == 1 ? left : decoded[f * channels + 1];
        }

        Ring.Write(stereo.AsSpan(0, frames * 2));
        nextFrame += frames;
        if (nextFrame >= File.Frames) {
            Volatile.Write(ref finished, 1);
        }

        return true;
    }

    /// <summary>Audio thread: adds the next frames (as many as are queued) to the bus at the source's gain.</summary>
    public void MixInto(Span<float> left, Span<float> right) {
        var frames = Math.Min(left.Length, Ring.Available / 2);
        if (frames == 0) {
            return;
        }

        Ring.ReadRegions(frames * 2, out var first, out var second);
        var gain = Gain;
        for (var i = 0; i < first.Length / 2; i++) {
            left[i] += first[2 * i] * gain;
            right[i] += first[2 * i + 1] * gain;
        }

        var offset = first.Length / 2;
        for (var i = 0; i < second.Length / 2; i++) {
            left[offset + i] += second[2 * i] * gain;
            right[offset + i] += second[2 * i + 1] * gain;
        }

        Ring.Skip(frames * 2);
    }

    public void Dispose() {
        Ring.Dispose();
        NativeMemory.AlignedFree(scratch);
        File.Dispose();
    }
}
