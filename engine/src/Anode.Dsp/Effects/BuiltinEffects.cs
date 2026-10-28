namespace Anode.Dsp.Effects;

/// <summary>
///     Anode's own effects (docs/05-phase-4.md, workstream D: the T1 suite of the plan's §7). The engine lists
///     <see cref="All" /> in its plugin catalogue.
/// </summary>
public static class BuiltinEffects {
    public static IReadOnlyList<EffectDescriptor> All { get; } = [];

    /// <summary>A new, unprepared instance of effect <paramref name="id" />, or null when there is none.</summary>
    public static IAudioEffect? Create(string id) => null;
}
