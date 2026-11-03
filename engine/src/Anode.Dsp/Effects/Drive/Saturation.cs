namespace Anode.Dsp.Effects.Drive;

/// <summary>
///     <c>anode.saturation</c> (ChromaGlow-like; docs/12-phase-11.md, workstream E): oversampled saturation in four
///     models (tube, tape, transformer, modern), with drive, tone, mix and output level.
/// </summary>
/// <remarks>
///     A stub of the Phase 11 contracts: its id, name and parameters are fixed (E may add parameters after these), its
///     processing is workstream E's. Not in <see cref="BuiltinEffects" /> until it works.
/// </remarks>
public sealed class Saturation : AudioEffect {
    public const string Id = "anode.saturation";
    static readonly EffectParameters P = new();
    public static readonly int Model = P.Choice("model", "Model", ["Tube", "Tape", "Transformer", "Modern"], 0);
    public static readonly int Drive = P.Add("drive", "Drive", 0, 100, 30, "%");
    public static readonly int Tone = P.Add("tone", "Tone", -100, 100, 0, "%");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");
    public static readonly int Output = P.Add("output", "Output", -24, 24, 0, "dB");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Saturation", EffectCategories.Distortion, Drive, Mix);

    public Saturation() : base(Info) { }

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
