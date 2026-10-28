namespace Anode.Dsp.Flex;

/// <summary>Makes flex renderers.</summary>
public static class FlexRenderers {
    /// <summary>
    ///     Whether the Stretch algorithms (Polyphonic, Monophonic, Pitch) are available: the <c>an_stretch</c> library,
    ///     built by engine/native/build.sh, loads. Without it, <see cref="Create" /> gives those algorithms the
    ///     <see cref="ReferenceFlexRenderer" />, which keeps the warp map's timing but lets the pitch follow the rate and
    ///     ignores pitch shifts.
    /// </summary>
    public static bool StretchAvailable => StretchNative.Available;

    /// <summary>
    ///     A renderer of <paramref name="algorithm" /> for <paramref name="channels" /> channels at
    ///     <paramref name="sampleRate" />, rendering up to <paramref name="maxFrames" /> frames a call (docs/flex/README.md).
    ///     Construction allocates (and, for the Stretch algorithms, creates two native stretchers); renders do not.
    /// </summary>
    public static IFlexRenderer Create(FlexKind algorithm, int channels, int sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        return algorithm switch {
            FlexKind.None => throw new ArgumentOutOfRangeException(
                nameof(algorithm),
                "An unwarped entry needs no renderer."
            ),
            FlexKind.Polyphonic or FlexKind.Monophonic or FlexKind.Pitch => StretchAvailable
                ? new StretchRenderer(algorithm, channels, sampleRate, maxFrames)
                : new ReferenceFlexRenderer(channels, maxFrames),
            FlexKind.Slicing => new SlicingRenderer(false, channels, sampleRate, maxFrames),
            FlexKind.Rhythmic => new SlicingRenderer(true, channels, sampleRate, maxFrames),
            FlexKind.Speed => new SpeedRenderer(channels, maxFrames),
            FlexKind.Tempophone => new TempophoneRenderer(channels, sampleRate, maxFrames),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown flex algorithm.")
        };
    }
}
