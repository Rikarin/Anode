using Anode.Dsp.Effects.Pitch;

namespace Anode.Dsp.Effects.Metering;

/// <summary>
///     <c>anode.tuner</c>: passes the audio (or mutes it, to tune silently) and measures the pitch of the mono sum. Every
///     <see cref="Hop" /> samples, <see cref="YinDetector" /> finds the period of the last window, from
///     <see cref="MinHz" /> (below a five-string bass's low B) to <see cref="MaxHz" /> (above a piano's top C), measured
///     over as many periods as the window holds. The nearest equal-tempered note follows from Reference, the A above
///     middle C. The <see cref="IAudioEffect.Reading" /> is how far the pitch is from that note in cents (0 while nothing
///     is heard).
///     <para>
///         Display (<see cref="IEffectDisplay" />): the pitch in Hz (0 while nothing is heard), the note's MIDI number,
///         and the cents.
///     </para>
/// </summary>
public sealed class Tuner : AudioEffect, IEffectDisplay {
    public const string Id = "anode.tuner";
    public const double MinHz = 25;
    public const double MaxHz = 4_500;
    public const int Hop = 1_024;

    static readonly EffectParameters P = new();
    public static readonly int Reference = P.Add("reference", "Reference A", 400, 480, 440, "Hz");
    public static readonly int Mute = P.Toggle("mute", "Mute", false);

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Tuner", EffectCategories.Metering, Reference, Mute);

    YinDetector yin = new(48_000, MinHz, MaxHz);
    float[] mono = [];
    GainRamp through;
    double reference = 440;
    int mask;
    long now;
    float hz;
    float note;
    float cents;

    public Tuner() : base(Info) { }

    /// <summary>The pitch at the last analysis, Hz (0 while nothing is heard).</summary>
    public float PitchHz => Volatile.Read(ref hz);

    /// <summary>The nearest note's MIDI number at the last analysis.</summary>
    public float Note => Volatile.Read(ref note);

    public int DisplaySize => 3;

    public int ReadDisplay(Span<float> values) {
        values[0] = PitchHz;
        values[1] = Note;
        values[2] = Volatile.Read(ref cents);
        return 3;
    }

    protected override void OnPrepare(float sampleRate) {
        yin = new(sampleRate, MinHz, MaxHz);
        var ring = 1;
        while (ring < yin.Size * 2) {
            ring <<= 1;
        }

        mono = new float[ring];
        mask = ring - 1;
    }

    protected override void OnReset() {
        Array.Clear(mono);
        now = 0;
        Volatile.Write(ref hz, 0);
        Volatile.Write(ref note, 0);
        Volatile.Write(ref cents, 0);
        Publish(0);
    }

    protected override void Update() {
        reference = Value(Reference);
        through.Set(Switch(Mute) ? 0 : 1, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            mono[(int)(now & mask)] = 0.5f * (left[i] + right[i]);
            if ((now + 1) % Hop == 0) {
                Analyse();
            }

            now++;
            var g = through.Next();
            left[i] *= g;
            right[i] *= g;
        }
    }

    protected override void Finish() => Publish(Volatile.Read(ref cents));

    void Analyse() {
        if (!yin.Detect(mono, mask, now, out var period)) {
            Volatile.Write(ref hz, 0);
            Volatile.Write(ref cents, 0);
            return;
        }

        var pitch = SampleRate / yin.Refined(period);
        var exact = 69 + 12 * Math.Log2(pitch / reference);
        var nearest = Math.Round(exact);
        Volatile.Write(ref hz, (float)pitch);
        Volatile.Write(ref note, (float)nearest);
        Volatile.Write(ref cents, (float)((exact - nearest) * 100));
    }
}
