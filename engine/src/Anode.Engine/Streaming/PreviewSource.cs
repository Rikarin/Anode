using Anode.Dsp.Flex;
using System.Runtime.InteropServices;

namespace Anode.Engine.Streaming;

/// <summary>
///     How the preview channel plays a loop in sync with the project (Phase 6, <c>setPreviewSync</c>): at
///     <see cref="TempoRatio" /> of its own speed, transposed by <see cref="TransposeSemitones" />, and, while the
///     transport plays, from the next bar (<see cref="StartOnBar" />).
/// </summary>
public readonly record struct PreviewSync(double TempoRatio, int TransposeSemitones, bool StartOnBar) {
    /// <summary>The file as it is, at once.</summary>
    public static PreviewSync None => new(1, 0, false);

    /// <summary>Whether the file must be rendered through the Stretch renderer (a ratio other than 1, or a transpose).</summary>
    public bool Stretched => TempoRatio != 1 || TransposeSemitones != 0;
}

/// <summary>
///     A file playing through the preview channel (docs/04-phase-3.md, "Files and MIDI import"): the disk thread reads
///     it from the start into a stereo ring, and the audio thread adds the ring at <see cref="Gain" /> to the master bus,
///     whatever the transport does, bypassing the tracks. The control thread swaps sources whole (start, replace,
///     stop) and frees the old one after both threads' grace periods.
///     <para>
///         Phase 6's in-sync preview (<see cref="PreviewSync" />): a stretched source is rendered by the disk thread
///         through a Stretch renderer (Polyphonic, made here on the control thread) over a two-point warp map that
///         plays the whole file in <see cref="TotalFrames" /> = its frames ÷ the ratio, with the transpose as a pitch
///         point; a source that starts on the bar waits, while the transport plays, for the next bar line, from which
///         frame of the block it starts.
///     </para>
/// </summary>
public sealed unsafe class PreviewSource : IDisposable {
    const int ReadFrames = 16_384;

    readonly byte* scratch;
    readonly int scratchBytes;
    readonly float[] decoded;
    readonly float[] stereo = GC.AllocateArray<float>(ReadFrames * 2, true);
    readonly IFlexRenderer? renderer;
    readonly FlexWarpPoint[] warp = [];
    readonly FlexPitchPoint[] pitch = [];
    readonly Source source;
    long nextFrame; // disk thread
    int finished;

    public PreviewSource(StreamFile file, float gain, int sampleRate, PreviewSync sync = default) {
        File = file;
        Gain = float.IsFinite(gain) ? Math.Max(0, gain) : 0;
        Sync = sync.TempoRatio > 0 && double.IsFinite(sync.TempoRatio) ? sync : sync with { TempoRatio = 1 };
        Ring = new(sampleRate * 2); // one second of stereo
        scratchBytes = StreamFile.ScratchBytes(ReadFrames, file.Format.BytesPerFrame);
        scratch = (byte*)NativeMemory.AlignedAlloc((nuint)scratchBytes, 16_384);
        decoded = GC.AllocateArray<float>(ReadFrames * file.Channels, true);
        source = new(this);
        TotalFrames = file.Frames;
        if (!Sync.Stretched || file.Frames <= 0) {
            return;
        }

        TotalFrames = Math.Max(1, (long)Math.Round(file.Frames / Sync.TempoRatio));
        warp = [new() { Offset = 0, FileFrame = 0 }, new() { Offset = TotalFrames, FileFrame = file.Frames }];
        if (Sync.TransposeSemitones != 0) {
            pitch = [new() { FileFrame = 0, Cents = 100f * Sync.TransposeSemitones }];
        }

        renderer = FlexRenderers.Create(FlexKind.Polyphonic, 2, sampleRate, ReadFrames);
    }

    public StreamFile File { get; }

    public float Gain { get; }

    public PreviewSync Sync { get; }

    /// <summary>Frames it plays: the file's, or its stretched length.</summary>
    public long TotalFrames { get; }

    public SpscRing Ring { get; }

    /// <summary>It has started playing (it may have waited for a bar line). Written by the audio thread.</summary>
    public bool Started { get; internal set; }

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

    /// <summary>Disk thread: reads (or renders) the next part into the ring, when there is room for a useful read.</summary>
    public bool Fill() {
        if (Finished) {
            return false;
        }

        var frames = (int)Math.Min(Math.Min(ReadFrames, Ring.Free / 2), TotalFrames - nextFrame);
        if (frames <= 0) {
            if (nextFrame >= TotalFrames) {
                Volatile.Write(ref finished, 1);
            }

            return false;
        }

        if (renderer is not null) {
            var region = new FlexRegion(FlexKind.Polyphonic, warp, pitch, TotalFrames);
            renderer.Render(region, nextFrame, stereo.AsSpan(0, frames * 2), 2, source);
        } else {
            Read(nextFrame, frames, stereo);
        }

        Ring.Write(stereo.AsSpan(0, frames * 2));
        nextFrame += frames;
        if (nextFrame >= TotalFrames) {
            Volatile.Write(ref finished, 1);
        }

        return true;
    }

    /// <summary>
    ///     Reads file frames [<paramref name="first" />, + <paramref name="frames" />) as stereo into
    ///     <paramref name="into" />.
    /// </summary>
    void Read(long first, int frames, Span<float> into) {
        File.Read(first, frames, new(scratch, scratchBytes), decoded.AsSpan(0, frames * File.Channels));
        var channels = File.Channels;
        for (var f = 0; f < frames; f++) {
            var left = decoded[f * channels];
            into[2 * f] = left;
            into[2 * f + 1] = channels == 1 ? left : decoded[f * channels + 1];
        }
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
        renderer?.Dispose();
        Ring.Dispose();
        NativeMemory.AlignedFree(scratch);
        File.Dispose();
    }

    /// <summary>The file as the Stretch renderer reads it: stereo, zero outside the file. Disk thread.</summary>
    sealed class Source(PreviewSource owner) : IFlexSource {
        public int Channels => 2;

        public long Frames => owner.File.Frames;

        public int Read(long fileFrame, Span<float> interleaved) {
            var frames = interleaved.Length / 2;
            interleaved.Clear();
            var first = Math.Max(fileFrame, 0);
            var last = Math.Min(fileFrame + frames, owner.File.Frames);
            for (var at = first; at < last;) {
                var n = (int)Math.Min(ReadFrames, last - at);
                owner.Read(at, n, interleaved[((int)(at - fileFrame) * 2)..]);
                at += n;
            }

            return (int)Math.Max(0, last - first);
        }
    }
}
