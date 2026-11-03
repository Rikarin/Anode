namespace Anode.Dsp;

/// <summary>
///     Phase 11's T3 instruments and effects (docs/12-phase-11.md): the physical model, tape replay, Sample Alchemy,
///     saturation, the Down Mixer and Binaural Post-Processing inserts and the Mastering Assistant. They are registered
///     (<see cref="Instruments.BuiltinInstruments" />, <see cref="Effects.BuiltinEffects" />,
///     <see cref="Effects.BuiltinFormatEffects" />) behind <see cref="Ready" />: the contract, golden-render and demo
///     tests instantiate and render every registered entry, so the registration waits for workstream E's DSP.
/// </summary>
/// <remarks>At integration, once E's classes no longer throw, <see cref="Ready" /> becomes <c>true</c>.</remarks>
public static class T3 {
    /// <summary>Whether the T3 instruments and effects are in the built-in catalogues.</summary>
    public static bool Ready => false;
}
