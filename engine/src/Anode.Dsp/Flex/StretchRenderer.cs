using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anode.Dsp.Flex;

/// <summary>
///     Polyphonic, Monophonic and Pitch (docs/06-phase-5.md, "Flex algorithms"): Signalsmith Stretch through
///     <c>an_stretch</c>, at the warp's local rate, transposed by the pitch points' cents, its formants shifted by their
///     formant cents (and, in Pitch mode, compensated for the transposition), and scaled by their gain. Polyphonic and
///     Pitch use the default preset (120 ms blocks every 30 ms); Monophonic a shorter block (100 ms every 25 ms), which
///     keeps attacks tighter on a single voice (docs/flex/README.md has the measurements). Split computation is off: the
///     disk thread absorbs the bursts.
///     <para>
///         <b>Fill-start independence.</b> A phase vocoder's output depends on everything it processed since it
///         started, so a stretcher primed just before a seek would never converge on the stream played from the entry's
///         start. Instead, streams restart at fixed anchors: stream k is started (aligned, with a reseeded random
///         engine) <see cref="leadIn" /> + <see cref="fade" /> frames before entry offset k·<see cref="segment" />, and
///         takes over from stream k − 1 with a <see cref="fade" />-long crossfade ending there (stream 0 starts at
///         offset 0). Each stream is fed in fixed chunks of <see cref="Chunk" /> output frames from its anchor, with the
///         rate and the pitch shift of each chunk taken from the maps, so every stream, and therefore every output
///         frame, is a function of the entry alone: renders from any offset are bit-identical once primed. A seek starts
///         the one or two streams that cover it at their anchors and runs them up to the offset, so
///         <see cref="PrimingFrames" /> is about one segment (two seconds).
///     </para>
///     <para>
///         <b>Crossfades in phase.</b> Untransposed, a stretched partial's phase drifts from the file's by 2πf times
///         the frames the stream has gained or lost on the file. A stream that starts its lead-in on the file frame it
///         would have reached at 1:1 from the entry's start (and then jumps to where the warp map is) carries the same
///         drift as its predecessor, so the two are in phase through the crossfade, which sums them with a raised
///         cosine (gains summing to one). Transposed, the stretcher's phases follow no such rule (measured: no lead-in
///         start keeps them in phase), so where the pitch points shift anything around a crossfade, the lead-in starts
///         on the warp map and the crossfade is equal-power. docs/flex/README.md measures both.
///     </para>
///     <para>
///         <b>Stereo as mid and side.</b> The stretcher locks each channel's phases to the loudest one's, but its
///         arithmetic leaves equal channels differing by about 4e-4. Stereo is stretched as mid and side instead, so a
///         centred (or mono) source comes out with exactly equal channels, and opposite channels cancel exactly.
///     </para>
///     Two native stretchers per renderer; allocation-free after construction.
/// </summary>
internal sealed unsafe class StretchRenderer : IFlexRenderer {
    /// <summary>Output frames per native call; a stream's whole history is a sequence of such calls from its anchor.</summary>
    const int Chunk = 128;

    /// <summary>Monophonic's block and interval in seconds (docs/flex/README.md has the measurements).</summary>
    public const double MonophonicBlock = 0.1, MonophonicInterval = 0.025;

    readonly int channels;
    readonly long fade;
    readonly bool formantCompensation;
    readonly int inputLatency;
    readonly long leadIn;
    readonly int outputLatency;
    readonly long segment;
    readonly int seekLength;
    readonly int step;
    readonly Stream[] streams;
    bool disposed;
    long expected = long.MinValue;

    /// <summary>
    ///     A renderer of <paramref name="algorithm" />. <paramref name="blockSeconds" /> and
    ///     <paramref name="intervalSeconds" />, when set, replace the algorithm's block (to measure others).
    /// </summary>
    public StretchRenderer(
        FlexKind algorithm,
        int channels,
        int sampleRate,
        int maxFrames,
        double blockSeconds = 0,
        double intervalSeconds = 0
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        this.channels = channels;
        formantCompensation = algorithm == FlexKind.Pitch;
        if (blockSeconds <= 0 && algorithm == FlexKind.Monophonic) {
            (blockSeconds, intervalSeconds) = (MonophonicBlock, MonophonicInterval);
        }

        streams = new Stream[2];
        for (var i = 0; i < streams.Length; i++) {
            nint handle;
            var status = blockSeconds > 0
                ? StretchNative.CreateCustom(
                    channels,
                    sampleRate,
                    (int)Math.Round(sampleRate * blockSeconds),
                    (int)Math.Round(sampleRate * intervalSeconds),
                    0,
                    out handle
                )
                : StretchNative.Create(channels, sampleRate, StretchNative.PresetDefault, 0, out handle);
            if (status != 0) {
                Dispose();
                throw new InvalidOperationException("an_stretch: " + StretchNative.LastErrorText);
            }

            streams[i] = new(this, handle);
        }

        var first = streams[0].Handle;
        inputLatency = StretchNative.InputLatency(first);
        outputLatency = StretchNative.OutputLatency(first);
        seekLength = StretchNative.SeekLength(first);
        step = StretchNative.IntervalFrames(first);
        segment = 64L * step;
        leadIn = 8L * step;
        fade = 2L * step;
        foreach (var stream in streams) {
            stream.WarmUp();
        }
    }

    /// <summary>
    ///     Whether untransposed lead-ins start on the in-phase file frame (see the class remarks): off only to measure
    ///     what that buys.
    /// </summary>
    internal bool PhaseAlignedLeadIn { get; set; } = true;

    public int PrimingFrames => (int)(segment + leadIn + fade);

    public void Render(in FlexRegion region, long offset, Span<float> interleaved, int channels, IFlexSource source) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (channels != this.channels || source.Channels != channels) {
            throw new ArgumentException("The renderer, the output and the source must have the same channel count.");
        }

        var frames = interleaved.Length / channels;
        interleaved.Clear();
        if (region.Warp.Length < 2) {
            expected = long.MinValue;
            return;
        }

        if (offset != expected) {
            Prime();
        }

        expected = offset + frames;
        var end = Math.Min(offset + frames, region.Length);
        var t = Math.Max(offset, 0);
        while (t < end) {
            // Segment k is [k·S − X, (k + 1)·S − X): stream k alone, after a crossfade from stream k − 1 over its first
            // X frames. Stream k + 1 runs its lead-in from a(k + 1) on.
            var k = t < segment - fade ? 0 : (t + fade) / segment;
            var fadeEnd = k * segment;
            var nextAnchor = Anchor(k + 1);
            var runEnd = t < fadeEnd ? fadeEnd : t < nextAnchor ? nextAnchor : (k + 1) * segment - fade;
            runEnd = Math.Min(runEnd, end);

            var current = Acquire(k, t, region, source);
            var outgoing = t < fadeEnd ? Acquire(k - 1, t, region, source) : null;
            var incoming = t >= nextAnchor ? Acquire(k + 1, t, region, source) : null;
            var output = interleaved.Slice((int)(t - offset) * channels, (int)(runEnd - t) * channels);
            var coherent = outgoing is not null && Coherent(k, region);
            Mix(current, outgoing, incoming, t, fadeEnd, coherent, output, region, source);
            t = runEnd;
        }

        if (channels == 2) {
            var first = (int)(Math.Max(offset, 0) - offset);
            var to = (int)Math.Max(0, end - offset);
            for (var n = first; n < to; n++) {
                var mid = interleaved[n * 2];
                var side = interleaved[n * 2 + 1];
                interleaved[n * 2] = mid + side;
                interleaved[n * 2 + 1] = mid - side;
            }
        }

        if (region.Pitch.IsEmpty) {
            return;
        }

        var from = Math.Max(offset, 0);
        int warpHint = 0, pitchHint = 0;
        for (var o = from; o < end; o++) {
            var gain = PitchCurve.GainAt(
                region.Pitch,
                WarpMap.FileFrameAt(region.Warp, o, ref warpHint),
                ref pitchHint
            );
            if (gain != 1) {
                foreach (ref var sample in interleaved.Slice((int)(o - offset) * channels, channels)) {
                    sample *= gain;
                }
            }
        }
    }

    public void Reset() {
        expected = long.MinValue;
        Prime();
    }

    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        foreach (var stream in streams) {
            if (stream is not null) {
                StretchNative.Destroy(stream.Handle);
            }
        }
    }

    /// <summary>Forgets every stream: the next render restarts the ones it needs at their anchors.</summary>
    void Prime() {
        foreach (var stream in streams) {
            stream?.Release();
        }
    }

    /// <summary>Where stream <paramref name="k" /> starts (its lead-in and crossfade before k·S; 0 for stream 0).</summary>
    long Anchor(long k) => k <= 0 ? 0 : k * segment - fade - leadIn;

    /// <summary>
    ///     Stream <paramref name="k" />, positioned at entry offset <paramref name="t" />: the one already running, or a
    ///     free one started at its anchor and run up to <paramref name="t" />.
    /// </summary>
    Stream Acquire(long k, long t, in FlexRegion region, IFlexSource source) {
        Stream? free = null;
        foreach (var stream in streams) {
            if (stream.Anchor == k && stream.Next <= t) {
                stream.Skip(t - stream.Next, region, source);
                return stream;
            }
        }

        foreach (var stream in streams) {
            if (stream.Anchor == k || stream.Anchor < 0 || stream.Anchor < k - 1 || stream.Anchor > k + 1) {
                free = stream;
                break;
            }
        }

        // Both busy with neighbours of k: take the one furthest from it.
        free ??= Math.Abs(streams[0].Anchor - k) >= Math.Abs(streams[1].Anchor - k) ? streams[0] : streams[1];
        free.Start(k, region, source);
        free.Skip(t - free.Next, region, source);
        return free;
    }

    void Mix(
        Stream current,
        Stream? outgoing,
        Stream? incoming,
        long t,
        long fadeEnd,
        bool coherent,
        Span<float> output,
        in FlexRegion region,
        IFlexSource source
    ) {
        var frames = output.Length / channels;
        var done = 0;
        while (done < frames) {
            var n = Math.Min(frames - done, current.Ready(region, source));
            if (outgoing is not null) {
                n = Math.Min(n, outgoing.Ready(region, source));
            }

            if (incoming is not null) {
                n = Math.Min(n, incoming.Ready(region, source));
            }

            for (var c = 0; c < channels; c++) {
                var from = current.Output(c);
                if (outgoing is null) {
                    for (var i = 0; i < n; i++) {
                        output[(done + i) * channels + c] = from[i];
                    }

                    continue;
                }

                // From the outgoing stream to the current one over [k·S − X, k·S): a raised cosine when the two
                // are in phase, equal power when not.
                var old = outgoing.Output(c);
                for (var i = 0; i < n; i++) {
                    var x = (t + done + i - (fadeEnd - fade) + 0.5) / fade;
                    float gain, keep;
                    if (coherent) {
                        gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * x));
                        keep = 1 - gain;
                    } else {
                        gain = (float)Math.Sin(Math.PI / 2 * x);
                        keep = (float)Math.Cos(Math.PI / 2 * x);
                    }

                    output[(done + i) * channels + c] = from[i] * gain + old[i] * keep;
                }
            }

            current.Consume(n);
            outgoing?.Consume(n);
            incoming?.Consume(n);
            done += n;
        }

        if (outgoing is not null && t + frames >= fadeEnd) {
            outgoing.Release();
        }
    }

    /// <summary>
    ///     Whether streams k − 1 and k play in phase through their crossfade: neither transposes anything, that is no
    ///     pitch point between the file frames they play (with a second's margin) shifts the pitch.
    /// </summary>
    bool Coherent(long k, in FlexRegion region) {
        if (!PhaseAlignedLeadIn) {
            return false;
        }

        var pitch = region.Pitch;
        if (pitch.IsEmpty) {
            return true;
        }

        var hint = 0;
        var margin = 32.0 * step; // about a second
        var from = WarpMap.FileFrameAt(region.Warp, Anchor(k - 1), ref hint) - margin;
        var to = WarpMap.FileFrameAt(region.Warp, k * segment, ref hint) + margin;
        int lo = 0, hi = pitch.Length - 1;
        while (lo < hi) {
            var mid = (lo + hi) >>> 1;
            if (pitch[mid].FileFrame < from) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }

        // The point before the range holds into it.
        for (var i = Math.Max(0, lo - 1); i < pitch.Length; i++) {
            if (pitch[i].Cents != 0) {
                return false;
            }

            if (pitch[i].FileFrame > to) {
                break;
            }
        }

        return true;
    }

    /// <summary>
    ///     One stretcher and the stream it is playing: anchor k (−1: free), the entry offset of the next frame it hands
    ///     out, the file frame it has been fed up to, and the chunk it rendered last.
    /// </summary>
    sealed class Stream {
        readonly float[][] input;
        readonly int inputCapacity;
        readonly float[] interleaved;
        readonly float[][] output;
        readonly StretchRenderer owner;
        int available;
        long fed;
        double formant = double.NaN;
        int ready;
        double semitones = double.NaN;

        public Stream(StretchRenderer owner, nint handle) {
            this.owner = owner;
            Handle = handle;
            var channels = owner.channels;
            inputCapacity = 32 * 1_024;
            input = new float[channels][];
            output = new float[channels][];
            for (var c = 0; c < channels; c++) {
                input[c] = GC.AllocateArray<float>(inputCapacity, true);
                output[c] = GC.AllocateArray<float>(Chunk, true);
            }

            interleaved = new float[inputCapacity * channels];
        }

        public nint Handle { get; }
        public long Anchor { get; private set; } = -1;
        public long Next { get; private set; }

        public void Release() => Anchor = -1;

        /// <summary>Runs the native calls once on silence, so their buffers are sized before the disk thread uses them.</summary>
        public void WarmUp() {
            for (var c = 0; c < input.Length; c++) {
                Array.Clear(input[c]);
            }

            OutputSeek(Math.Min(inputCapacity, StretchNative.OutputSeekLength(Handle, 1)));
            Seek(Math.Min(inputCapacity, owner.seekLength), 1);
            for (var n = 0; n < 4; n++) {
                Process(Chunk);
            }

            SetPitch(0, 0);
        }

        /// <summary>Starts stream <paramref name="k" /> at its anchor, aligned, its random engine reseeded.</summary>
        public void Start(long k, in FlexRegion region, IFlexSource source) {
            Anchor = k;
            var anchor = owner.Anchor(k);
            Next = anchor;
            available = 0;
            ready = 0;
            var warp = region.Warp;
            var hint = 0;
            var lookahead = anchor + owner.outputLatency;
            SetPitch(region, lookahead + Chunk / 2.0);
            if (k == 0) {
                // The entry's start: the file from the warp map's first frame, at its first segment's rate.
                var rate = Math.Clamp(WarpMap.RateAt(warp, 0, ref hint), 1.0 / 64, 64);
                var start = (long)Math.Round(warp[0].FileFrame);
                var length = Math.Min(inputCapacity, StretchNative.OutputSeekLength(Handle, rate));
                Read(source, start, length);
                OutputSeek(length);
                fed = start + length;
                return;
            }

            // A lead-in: aligned on the frame that keeps this stream in phase with the previous one (see the class
            // remarks), then a jump to where the warp map is.
            var phase = owner.Coherent(k, region)
                ? (long)Math.Round(warp[0].FileFrame) + anchor
                : (long)Math.Round(WarpMap.FileFrameAt(warp, anchor, ref hint));
            var seekLength = Math.Min(inputCapacity, StretchNative.OutputSeekLength(Handle, 1));
            Read(source, phase, seekLength);
            OutputSeek(seekLength);
            var target = (long)Math.Round(owner.inputLatency + WarpMap.FileFrameAt(warp, lookahead, ref hint));
            var rateAtJump = Math.Clamp(Math.Abs(WarpMap.RateAt(warp, lookahead, ref hint)), 1.0 / 64, 64);
            JumpTo(source, target, rateAtJump);
        }

        /// <summary>Frames ready to hand out, rendering the next chunk if none are.</summary>
        public int Ready(in FlexRegion region, IFlexSource source) {
            if (available == 0) {
                Produce(region, source);
            }

            return available;
        }

        public ReadOnlySpan<float> Output(int channel) => output[channel].AsSpan(ready, available);

        public void Consume(int frames) {
            ready += frames;
            available -= frames;
            Next += frames;
        }

        /// <summary>Renders and discards <paramref name="frames" /> frames.</summary>
        public void Skip(long frames, in FlexRegion region, IFlexSource source) {
            while (frames > 0) {
                var n = (int)Math.Min(frames, Ready(region, source));
                Consume(n);
                frames -= n;
            }
        }

        /// <summary>
        ///     The next chunk: fed up to the file frame the stretcher's processing time plays (the warp map at the chunk's
        ///     end plus the output latency, plus the input latency), transposed for the chunk's middle at that time.
        /// </summary>
        void Produce(in FlexRegion region, IFlexSource source) {
            var start = Next;
            var hint = 0;
            SetPitch(region, start + owner.outputLatency + Chunk / 2.0);
            var target = (long)Math.Round(
                owner.inputLatency + WarpMap.FileFrameAt(region.Warp, start + Chunk + owner.outputLatency, ref hint)
            );
            var take = target - fed;
            if (take > inputCapacity) {
                // Faster than the buffer holds: jump instead (the stretcher keeps only its last block of input).
                JumpTo(source, target, Math.Min(64, take / (double)Chunk));
                take = 0;
            }

            take = Math.Max(0, take);
            Read(source, fed, (int)take);
            Process((int)take);
            fed += take;
            ready = 0;
            available = Chunk;
        }

        void JumpTo(IFlexSource source, long target, double rate) {
            var length = Math.Min(inputCapacity, owner.seekLength);
            Read(source, target - length, length);
            Seek(length, rate);
            fed = target;
        }

        void SetPitch(in FlexRegion region, double offset) {
            if (region.Pitch.IsEmpty) {
                SetPitch(0, 0);
                return;
            }

            int warpHint = 0, pitchHint = 0;
            var file = WarpMap.FileFrameAt(region.Warp, offset, ref warpHint);
            var (cents, formantCents, _) = PitchCurve.At(region.Pitch, file, ref pitchHint);
            SetPitch(cents, formantCents);
        }

        void SetPitch(double cents, double formantCents) {
            var transpose = cents / 100;
            if (transpose != semitones) {
                semitones = transpose;
                StretchNative.SetTransposeSemitones(Handle, transpose, 0);
            }

            var factor = Math.Pow(2, formantCents / 1_200);
            if (factor != formant) {
                formant = factor;
                StretchNative.SetFormantFactor(Handle, factor, owner.formantCompensation ? 1 : 0);
            }
        }

        /// <summary>Reads <paramref name="frames" /> file frames from <paramref name="first" /> into the planar input.</summary>
        void Read(IFlexSource source, long first, int frames) {
            if (frames <= 0) {
                return;
            }

            var channels = owner.channels;
            var span = interleaved.AsSpan(0, frames * channels);
            source.Read(first, span);
            if (channels == 2) {
                // Mid and side (see the class remarks).
                var mid = input[0];
                var side = input[1];
                for (var n = 0; n < frames; n++) {
                    mid[n] = 0.5f * (span[n * 2] + span[n * 2 + 1]);
                    side[n] = 0.5f * (span[n * 2] - span[n * 2 + 1]);
                }

                return;
            }

            for (var c = 0; c < channels; c++) {
                var plane = input[c];
                for (var n = 0; n < frames; n++) {
                    plane[n] = span[n * channels + c];
                }
            }
        }

        void OutputSeek(int frames) {
            var ins = stackalloc float*[input.Length];
            Pointers(ins, input);
            StretchNative.OutputSeek(Handle, ins, frames);
        }

        void Seek(int frames, double rate) {
            var ins = stackalloc float*[input.Length];
            Pointers(ins, input);
            StretchNative.Seek(Handle, ins, frames, rate);
        }

        void Process(int frames) {
            var ins = stackalloc float*[input.Length];
            var outs = stackalloc float*[output.Length];
            Pointers(ins, input);
            Pointers(outs, output);
            StretchNative.Process(Handle, ins, frames, outs, Chunk);
        }

        /// <summary>Channel pointers into pinned arrays, which never move.</summary>
        static void Pointers(float** pointers, float[][] planes) {
            for (var c = 0; c < planes.Length; c++) {
                pointers[c] = (float*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(planes[c]));
            }
        }
    }
}
