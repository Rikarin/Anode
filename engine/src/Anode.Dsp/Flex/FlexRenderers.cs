namespace Anode.Dsp.Flex;

/// <summary>Makes flex renderers.</summary>
public static class FlexRenderers {
    /// <summary>
    ///     A renderer of <paramref name="algorithm" /> for <paramref name="channels" /> channels at
    ///     <paramref name="sampleRate" />, rendering up to <paramref name="maxFrames" /> frames a call. Until the Phase 5
    ///     algorithms land (workstream E), every algorithm is the <see cref="ReferenceFlexRenderer" />.
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
            _ => new ReferenceFlexRenderer(channels, maxFrames)
        };
    }
}
