using Anode.Dsp.Effects;

namespace Anode.Dsp.Mastering;

/// <summary>
///     <c>anode.mastering-assistant</c>, the Mastering Assistant (docs/12-phase-11.md, workstream E): EQ to the
///     character's curve, gentle compression, the character's colour, stereo width and a true-peak limiter whose input
///     gain reaches the loudness target; a level-matched bypass. Its parameters are the user's settings (the contract's
///     <c>MasteringParameters</c> ids, automatable); its chain is what the analysis set (<see cref="SetChain" />).
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts: its id and parameters are fixed, its processing is workstream E's. Not in
///     <see cref="BuiltinEffects" /> until it works (the engine registers it with the T3 effects, workstream A).
/// </remarks>
public sealed class MasteringAssistant : AudioEffect {
    public const string Id = "anode.mastering-assistant";
    static readonly EffectParameters P = new();

    public static readonly int Character = P.Choice(
        "character",
        "Character",
        ["Clean", "Valve", "Punch", "Transparent"],
        0
    );

    public static readonly int TargetLufs = P.Add("targetLufs", "Loudness", -24, -6, -14, "LUFS");
    public static readonly int CeilingDbtp = P.Add("ceilingDbtp", "Ceiling", -3, 0, -1, "dBTP");
    public static readonly int Eq = P.Add("eq", "EQ", 0, 1, 1, string.Empty);
    public static readonly int Dynamics = P.Add("dynamics", "Dynamics", 0, 1, 1, string.Empty);
    public static readonly int Width = P.Add("width", "Width", 0, 2, 1, string.Empty);
    public static readonly int MatchedBypass = P.Toggle("matchedBypass", "Level-Matched Bypass", false);

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Mastering Assistant",
        EffectCategories.Dynamics,
        TargetLufs,
        Width
    );

    public MasteringAssistant() : base(Info) { }

    /// <summary>On the control thread: the chain from the analysis (null: none yet, the limiter alone).</summary>
    public void SetChain(MasteringChainSettings? chain) => throw Pending.WorkstreamE();

    protected override void OnPrepare(float sampleRate) => throw Pending.WorkstreamE();

    protected override void OnReset() => throw Pending.WorkstreamE();

    protected override void Update() => throw Pending.WorkstreamE();

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) =>
        throw Pending.WorkstreamE();
}
