namespace Anode.Dsp.Effects.Utility;

/// <summary>
///     <c>anode.gain</c>: gain (−96 … +24 dB, −96 is silence), balance, a phase invert per channel, swap and mono (the
///     mean of the channels). The order: swap, mono, invert, then gain and balance. Balance turns the far side down
///     linearly and leaves the near side alone.
/// </summary>
public sealed class Gain : AudioEffect {
    public const string Id = "anode.gain";
    static readonly EffectParameters P = new();
    public static readonly int Level = P.Add("gain", "Gain", -96, 24, 0, "dB");
    public static readonly int Balance = P.Add("balance", "Balance", -100, 100, 0, "%");
    public static readonly int InvertLeft = P.Toggle("invertLeft", "Invert Left", false);
    public static readonly int InvertRight = P.Toggle("invertRight", "Invert Right", false);
    public static readonly int Swap = P.Toggle("swap", "Swap Left/Right", false);
    public static readonly int Mono = P.Toggle("mono", "Mono", false);

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Gain", EffectCategories.Utility);

    GainRamp left;
    GainRamp right;

    public Gain() : base(Info) { }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() { }

    protected override void Update() {
        var level = Value(Level) <= -96 ? 0 : Decibels.ToGain(Value(Level));
        var balance = Value(Balance) * 0.01f;
        var l = level * MathF.Min(1, 1 - balance) * (Switch(InvertLeft) ? -1 : 1);
        var r = level * MathF.Min(1, 1 + balance) * (Switch(InvertRight) ? -1 : 1);
        left.Set(l, Snapping);
        right.Set(r, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var swap = Switch(Swap);
        var mono = Switch(Mono);
        for (var i = 0; i < left.Length; i++) {
            var l = swap ? right[i] : left[i];
            var r = swap ? left[i] : right[i];
            if (mono) {
                l = r = 0.5f * (l + r);
            }

            left[i] = l * this.left.Next();
            right[i] = r * this.right.Next();
        }
    }
}
