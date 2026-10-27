namespace Anode.Dsp.Envelopes;

/// <summary>The stage an <see cref="Adsr" /> is in.</summary>
public enum AdsrStage : byte {
    Idle,
    Attack,
    Hold,
    Decay,
    Release
}

/// <summary>
///     The per-sample increments and coefficients of an <see cref="Adsr" />, computed once from its times (control
///     rate) and shared by every voice that uses them.
/// </summary>
/// <remarks>
///     The attack is linear, from the current level to 1 in the attack time. Decay and release are exponential, and
///     their times are T60s: the decay brings the distance to the sustain level down by 60 dB in the decay time, and the
///     release brings the level down by 60 dB in the release time.
/// </remarks>
public readonly record struct AdsrSettings(
    float AttackStep,
    int HoldSamples,
    float DecayCoefficient,
    float Sustain,
    float ReleaseCoefficient) {
    /// <summary>ln(1000): a T60 in time constants.</summary>
    const double Ln1000 = 6.907755278982137;

    /// <summary>Settings for times in seconds and a sustain level in 0 … 1 at <paramref name="sampleRate" />.</summary>
    public static AdsrSettings From(
        double attack,
        double hold,
        double decay,
        double sustain,
        double release,
        double sampleRate
    ) =>
        new(
            attack * sampleRate <= 1 ? 1f : (float)(1 / (attack * sampleRate)),
            (int)Math.Round(Math.Max(hold, 0) * sampleRate),
            T60Coefficient(decay, sampleRate),
            (float)Math.Clamp(sustain, 0, 1),
            T60Coefficient(release, sampleRate)
        );

    /// <summary>The per-sample factor that decays by 60 dB in <paramref name="seconds" />; 0 for an instant one.</summary>
    public static float T60Coefficient(double seconds, double sampleRate) =>
        seconds * sampleRate <= 1 ? 0f : (float)Math.Exp(-Ln1000 / (seconds * sampleRate));
}

/// <summary>
///     An attack-hold-decay-sustain-release envelope generator (see <see cref="AdsrSettings" /> for the curves). It
///     reads its settings on every sample, so they can change while a note sounds; a retrigger attacks from the level
///     it is at, so a stolen or repeated voice does not click.
/// </summary>
public struct Adsr {
    /// <summary>Below this (−100 dB) a releasing envelope ends.</summary>
    public const float Silence = 1e-5f;

    float level;
    int held;

    public readonly float Level => level;

    public AdsrStage Stage { get; private set; }

    public readonly bool IsIdle => Stage == AdsrStage.Idle;

    /// <summary>Starts (or restarts) the attack from the current level.</summary>
    public void Trigger() {
        Stage = AdsrStage.Attack;
        held = 0;
    }

    /// <summary>Starts the release from the current level (an idle envelope stays idle).</summary>
    public void Release() {
        if (Stage != AdsrStage.Idle) {
            Stage = AdsrStage.Release;
        }
    }

    /// <summary>Silences at once.</summary>
    public void Reset() {
        level = 0;
        held = 0;
        Stage = AdsrStage.Idle;
    }

    /// <summary>The next sample's level.</summary>
    public float Next(in AdsrSettings s) {
        switch (Stage) {
            case AdsrStage.Attack:
                level += s.AttackStep;
                if (level >= 1) {
                    level = 1;
                    Stage = s.HoldSamples > 0 ? AdsrStage.Hold : AdsrStage.Decay;
                }

                break;
            case AdsrStage.Hold:
                if (++held >= s.HoldSamples) {
                    Stage = AdsrStage.Decay;
                }

                break;
            case AdsrStage.Decay:
                level = s.Sustain + (level - s.Sustain) * s.DecayCoefficient;
                if (s.Sustain == 0 && level < Silence) {
                    level = 0;
                    Stage = AdsrStage.Idle; // decayed to nothing with no sustain: the note is over
                }

                break;
            case AdsrStage.Release:
                level *= s.ReleaseCoefficient;
                if (level < Silence) {
                    level = 0;
                    Stage = AdsrStage.Idle;
                }

                break;
            case AdsrStage.Idle:
                break;
        }

        return level;
    }
}
