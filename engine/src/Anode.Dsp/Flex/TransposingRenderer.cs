namespace Anode.Dsp.Flex;

/// <summary>
///     Polyphonic and Monophonic with one constant transposition (a region's transpose, a Pitch Source segment; no flex
///     pitch curve, no formant or gain): the stretcher stretches at no transposition, longer by the transposition's
///     factor, and the windowed sinc of <see cref="SpeedRenderer" /> resamples its output back to the entry's length, so
///     the factor is exact.
/// </summary>
/// <remarks>
///     The stretcher's own transposition carries a partial's offset from its FFT bin across at the input's scale, not the
///     output's: a tone lands up to about (factor − 1) times half a bin off, 15 cents at 100 Hz (docs/flex/README.md,
///     "Pitch
///     of transposed tones"), which a chord-following region cannot have. Anything else (a pitch curve, formants, gain)
///     goes to the stretcher as before. Allocation-free after construction, but for a warp map longer than any before it.
/// </remarks>
internal sealed class TransposingRenderer : IFlexRenderer {
    readonly FlexKind algorithm;
    readonly StretchRenderer stretch;
    readonly SpeedRenderer speed;
    readonly Stretched stretched;
    readonly FlexWarpPoint[] resample = new FlexWarpPoint[2];
    FlexWarpPoint[] warp = new FlexWarpPoint[16];
    int warpLength;
    double factor;
    long length;
    bool warpIsSet;

    public TransposingRenderer(FlexKind algorithm, int channels, int sampleRate, int maxFrames) {
        this.algorithm = algorithm;
        speed = new(channels, maxFrames);
        stretch = new(algorithm, channels, sampleRate, speed.WindowFrames);
        stretched = new(this, channels, speed.WindowFrames);
    }

    public int PrimingFrames => stretch.PrimingFrames * 2;

    public void Render(in FlexRegion region, long offset, Span<float> interleaved, int channels, IFlexSource source) {
        if (!Constant(region.Pitch, out var cents) || region.Warp.Length < 2) {
            if (warpIsSet) {
                Forget();
            }

            stretch.Render(region, offset, interleaved, channels, source);
            return;
        }

        var next = Math.Pow(2, cents / 1_200);
        if (!Same(region, next)) {
            Forget();
            Adopt(region, next);
        }

        stretched.File = source;
        speed.Render(new(FlexKind.Speed, resample, [], region.Length), offset, interleaved, channels, stretched);
        stretched.File = null;
    }

    public void Reset() {
        Forget();
        stretch.Reset();
    }

    public void Dispose() => stretch.Dispose();

    /// <summary>Whether the pitch points are one shift, in pitch only.</summary>
    static bool Constant(ReadOnlySpan<FlexPitchPoint> pitch, out double cents) {
        cents = 0;
        if (pitch.IsEmpty) {
            return false;
        }

        foreach (ref readonly var point in pitch) {
            if (point.Cents != pitch[0].Cents || point.FormantCents != 0 || point.GainDb != 0) {
                return false;
            }
        }

        cents = pitch[0].Cents;
        return cents != 0;
    }

    bool Same(in FlexRegion region, double next) {
        if (!warpIsSet || next != factor || region.Length != length || region.Warp.Length != warpLength) {
            return false;
        }

        for (var i = 0; i < warpLength; i++) {
            if (region.Warp[i].FileFrame != warp[i].FileFrame
                || (long)Math.Round(region.Warp[i].Offset * factor) != warp[i].Offset) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The stretcher's map, longer by the factor, and the resampler's, back to the entry's length.</summary>
    void Adopt(in FlexRegion region, double next) {
        factor = next;
        length = region.Length;
        warpLength = region.Warp.Length;
        if (warp.Length < warpLength) {
            warp = new FlexWarpPoint[warpLength * 2];
        }

        for (var i = 0; i < warpLength; i++) {
            warp[i] = new() {
                Offset = (long)Math.Round(region.Warp[i].Offset * factor), FileFrame = region.Warp[i].FileFrame
            };
        }

        resample[0] = new() { Offset = 0, FileFrame = 0 };
        resample[1] = new() { Offset = length, FileFrame = warp[warpLength - 1].Offset };
        warpIsSet = true;
    }

    void Forget() {
        warpIsSet = false;
        speed.Reset();
        stretched.Forget();
    }

    /// <summary>
    ///     The stretcher's output as the resampler's file: its windows overlap, so what it has read stays here and the
    ///     stretcher runs on without a seek.
    /// </summary>
    sealed class Stretched(TransposingRenderer owner, int channels, int capacity) : IFlexSource {
        readonly float[] kept = new float[capacity * channels];
        long keptStart;
        int keptFrames;

        public IFlexSource? File { get; set; }

        public int Channels => channels;

        public long Frames => owner.warp[owner.warpLength - 1].Offset;

        public void Forget() => keptFrames = 0;

        public int Read(long fileFrame, Span<float> interleaved) {
            var frames = interleaved.Length / channels;
            var keep = (int)Math.Clamp(keptStart + keptFrames - fileFrame, 0, frames);
            if (keptFrames == 0 || fileFrame < keptStart) {
                keep = 0;
            }

            if (keep > 0) {
                kept.AsSpan((int)(fileFrame - keptStart) * channels, keep * channels).CopyTo(interleaved);
            }

            var region = new FlexRegion(
                owner.algorithm,
                owner.warp.AsSpan(0, owner.warpLength),
                [],
                owner.warp[owner.warpLength - 1].Offset
            );
            owner.stretch.Render(
                region,
                fileFrame + keep,
                interleaved[(keep * channels)..],
                channels,
                File!
            );
            interleaved.CopyTo(kept);
            (keptStart, keptFrames) = (fileFrame, frames);
            return frames;
        }
    }
}
