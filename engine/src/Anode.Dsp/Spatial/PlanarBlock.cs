namespace Anode.Dsp.Spatial;

/// <summary>
///     A block of multichannel audio, planar: channel c's <see cref="Frames" /> samples at
///     <c>[c × Frames, (c + 1) × Frames)</c> of the span, in the layout's channel order. The engine keeps one per
///     strip, sized for its format and the maximum block, allocated off the audio thread.
/// </summary>
public readonly ref struct PlanarBlock {
    readonly Span<float> samples;

    public PlanarBlock(Span<float> samples, int channels, int frames) {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples.Length, channels * frames);
        this.samples = samples;
        Channels = channels;
        Frames = frames;
    }

    public int Channels { get; }
    public int Frames { get; }

    public Span<float> Channel(int channel) => samples.Slice(channel * Frames, Frames);

    public void Clear() => samples[..(Channels * Frames)].Clear();
}
