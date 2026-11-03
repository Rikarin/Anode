using Anode.Dsp.Instruments;
using Anode.Dsp.Spatial;

namespace Anode.Dsp.Effects.Surround;

/// <summary>
///     <c>anode.down-mixer</c>, the Down Mixer as an insert (docs/12-phase-11.md, workstream E): a surround strip to a
///     smaller layout, by Phase 10's <see cref="Downmixer" /> (ITU-R BS.775). Its <c>format</c> parameter is the target:
///     the index of a <see cref="SpeakerFormat" /> narrower than the input (stereo by default); its <c>lfe</c> parameter
///     the LFE's gain into the front pair when the target has no LFE (dB, −144 dropped, as BS.775).
/// </summary>
/// <remarks>
///     A target with no fewer channels than the input passes the input as it is (its output format is the input's).
///     The LFE's gain is the one thing that moves while it plays: its route into the front pair is linear in that gain,
///     so <see cref="Prepare" /> builds the matrix without the LFE and the LFE's own column at unit gain, and the gain
///     (on a 32-sample grid from <see cref="Reset" />, ramped across each interval) scales that column. Not in
///     <see cref="BuiltinEffects" /> (it is not an <see cref="IAudioEffect" />): the engine creates it by id.
/// </remarks>
public sealed class DownMixerInsert : IFormatEffect {
    public const string Id = "anode.down-mixer";
    static readonly EffectParameters P = new();

    public static readonly int Format = P.Choice(
        "format",
        "Format",
        [..Enum.GetNames<SpeakerFormat>()],
        (int)SpeakerFormat.Stereo
    );

    public static readonly int Lfe = P.Add("lfe", "LFE", -144, 0, -144, "dB");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Down Mixer", EffectCategories.Utility);

    /// <summary>At or below this the LFE is dropped.</summary>
    const float DroppedDb = -143.5f;

    readonly ParameterBank parameters = new(Info.Parameters);
    float[] matrix = [];
    float[] lfeColumn = [];
    int inputs;
    int outputs;
    int lfe = -1;
    bool passThrough = true;
    bool prepared;
    int untilControl;
    bool snap = true;
    GainRamp lfeGain;

    public EffectDescriptor Descriptor => Info;

    /// <summary>
    ///     The target format of <paramref name="input" /> at the current <c>format</c>: that format when it has fewer
    ///     channels, else the input's.
    /// </summary>
    public SpeakerFormat OutputFormat(SpeakerFormat input) {
        var target = (SpeakerFormat)(int)parameters[Format];
        return SpeakerLayout.Of(target).ChannelCount < SpeakerLayout.Of(input).ChannelCount ? target : input;
    }

    /// <summary>The gain of input channel <paramref name="input" /> in output channel <paramref name="output" /> now.</summary>
    public float Gain(int output, int input) {
        if (passThrough) {
            return output == input ? 1 : 0;
        }

        var lfeRoute = input == lfe ? lfeGain.Current * lfeColumn[output] : 0;
        return matrix[output * inputs + input] + lfeRoute;
    }

    public void Prepare(SpeakerLayout input, SpeakerLayout output, float sampleRate, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        inputs = input.ChannelCount;
        outputs = output.ChannelCount;
        passThrough = input.Format == output.Format;
        if (!passThrough) {
            var dropped = new Downmixer(input, output, 0);
            var unit = new Downmixer(input, output, 1);
            matrix = new float[outputs * inputs];
            lfeColumn = new float[outputs];
            lfe = input.LfeIndex;
            for (var o = 0; o < outputs; o++) {
                for (var i = 0; i < inputs; i++) {
                    matrix[o * inputs + i] = dropped.Gain(o, i);
                }

                lfeColumn[o] = lfe >= 0 ? unit.Gain(o, lfe) - dropped.Gain(o, lfe) : 0;
            }
        }

        prepared = true;
        Reset();
    }

    public void SetParameter(int index, float value) => parameters.Set(index, value);

    public float GetParameter(int index) => parameters.Get(index);

    public int LatencySamples => 0;

    public void Process(PlanarBlock input, PlanarBlock output) {
        var frames = Math.Min(input.Frames, output.Frames);
        if (!prepared || passThrough) {
            for (var c = 0; c < output.Channels; c++) {
                if (c < input.Channels) {
                    input.Channel(c)[..frames].CopyTo(output.Channel(c));
                } else {
                    output.Channel(c)[..frames].Clear();
                }
            }

            return;
        }

        for (var o = 0; o < outputs; o++) {
            var destination = output.Channel(o)[..frames];
            destination.Clear();
            for (var i = 0; i < inputs; i++) {
                var g = matrix[o * inputs + i];
                if (g == 0) {
                    continue;
                }

                var source = input.Channel(i);
                for (var n = 0; n < frames; n++) {
                    destination[n] += g * source[n];
                }
            }
        }

        if (lfe >= 0) {
            AddLfe(input.Channel(lfe), output, frames);
        }
    }

    /// <summary>The LFE's route at its gain, ramped on the control grid.</summary>
    void AddLfe(ReadOnlySpan<float> source, PlanarBlock output, int frames) {
        for (var n = 0; n < frames; n++) {
            if (untilControl == 0) {
                lfeGain.Set(Target(), snap);
                snap = false;
                untilControl = AudioEffect.ControlInterval;
            }

            untilControl--;
            var g = lfeGain.Next();
            if (g == 0) {
                continue;
            }

            for (var o = 0; o < outputs; o++) {
                var column = lfeColumn[o];
                if (column != 0) {
                    output.Channel(o)[n] += g * column * source[n];
                }
            }
        }
    }

    float Target() {
        var db = parameters[Lfe];
        return db <= DroppedDb ? 0 : Decibels.ToGain(db);
    }

    public void Reset() {
        untilControl = 0;
        snap = true;
        lfeGain.Set(Target(), true);
    }

    public byte[]? SaveState() => InstrumentState.Write(Id, null, Info.Parameters, parameters);

    public bool LoadState(ReadOnlySpan<byte> state) {
        if (!InstrumentState.TryRead(state, Id, out _, out var values)) {
            return false;
        }

        InstrumentState.Apply(values, parameters);
        Reset();
        return true;
    }

    public void Dispose() { }
}
