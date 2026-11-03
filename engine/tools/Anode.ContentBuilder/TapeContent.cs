using Anode.Contracts;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Tape;
using System.Text.Json;
using System.Text.Json.Nodes;
using A = Anode.Dsp.Instruments.Alchemy.SampleAlchemyParameters;
using P = Anode.Dsp.Instruments.Physical.PhysicalModelParameters;

namespace Anode.ContentBuilder;

/// <summary>
///     The tape-replay instrument's tape sets (docs/12-phase-11.md, workstream E): strings, flutes and choir, each key's
///     recording (up to the tape's 8 s) rendered from Anode's own instruments through a tape model.
/// </summary>
/// <remarks>
///     <para>
///         Each key of the classic range, G2 … F5 (35 keys), is one note held for the tape's 8 s: strings from the
///         physical model (a bowed string with a violin body and vibrato), flutes from it too (a blown tube), the choir
///         from Sample Alchemy's spectral mode on its sung "ah". The tape model is <c>anode.saturation</c>'s Tape model
///         (light drive, a little darker), a head bump and the tape's own high-frequency loss, then 24 kHz (a
///         windowed-sinc decimation), the rate the sets are stored at; the last 60 ms fade out. Every set is brought to
///         one peak, its keys keeping their levels.
///     </para>
///     <para>
///         A set is <c>patches/tapes/&lt;name&gt;.antape</c> beside an instrument patch,
///         <c>patches/tape_&lt;name&gt;.json</c>
///         (category Tape Replay), whose <c>anode.tape-replay</c> loads it (the server makes the file absolute where the
///         pack is installed). The test pack has the strings over five keys.
///     </para>
/// </remarks>
static class TapeContent {
    const int Rate = PackBuilder.SampleRate;
    const int TapeRate = 24_000;
    const int LowKey = 43;
    const int Keys = 35;
    const int Block = 256;

    /// <summary>Whether this build makes the tape sets (it does).</summary>
    public static bool Ready => true;

    /// <summary>Adds the tape sets to <paramref name="pack" /> (the test pack: one, over a few keys).</summary>
    public static void AddTapeSets(PackBuilder pack, bool test) {
        var low = test ? 55 : LowKey;
        var count = test ? 5 : Keys;
        Add(pack, "strings", "Tape Strings", Strings, low, count);
        if (test) {
            return;
        }

        Add(pack, "flutes", "Tape Flutes", Flutes, low, count);
        Add(pack, "choir", "Tape Choir", Choir, low, count);
    }

    static void Add(PackBuilder pack, string name, string title, Func<IInstrument> create, int low, int count) {
        var recordings = new float[count][];
        for (var k = 0; k < count; k++) {
            using var instrument = create();
            recordings[k] = Tape(Record(instrument, low + k));
        }

        var peak = 0f;
        foreach (var recording in recordings) {
            foreach (var x in recording) {
                peak = Math.Max(peak, MathF.Abs(x));
            }
        }

        foreach (var recording in recordings) {
            for (var i = 0; i < recording.Length; i++) {
                recording[i] *= 0.7f / peak;
            }
        }

        var set = TapeSet.From(title, TapeRate, low, recordings);
        var file = pack.Asset(Path.Combine("patches", "tapes"), name + TapeSet.Extension, set.ToBytes());
        var instrumentSlot = new InstrumentSlot(
            TapeReplay.Id,
            "Tape Replay",
            TapeReplay.Info.Parameters.ToDictionary(static p => p.Id, static p => (double)p.Default),
            file
        );
        var patch = new JsonObject {
            ["schemaVersion"] = 1,
            ["id"] = "tape_" + name,
            ["name"] = title,
            ["category"] = "Tape Replay",
            ["kind"] = "instrument",
            ["instrument"] = JsonSerializer.SerializeToNode(
                instrumentSlot,
                ContractsJsonContext.Default.InstrumentSlot
            ),
            ["inserts"] = new JsonArray(),
            ["sends"] = new JsonArray()
        };
        pack.Asset(
            "patches",
            "tape_" + name + ".json",
            System.Text.Encoding.UTF8.GetBytes(patch.ToJsonString(new() { WriteIndented = true }))
        );
    }

    /// <summary>A bowed string with a violin body, a slow bow and a little vibrato.</summary>
    static IInstrument Strings() =>
        Prepared(
            new PhysicalModel(),
            (P.ExciterType(1), (int)PhysicalExciter.Bow),
            (P.ExciterType(1) + 1, 12),
            (P.ExciterType(1) + 3, 35),
            (P.Damping, 20),
            (P.Body, (int)PhysicalBody.Violin),
            (P.BodyAmount, 70),
            (P.Cutoff, 7_000),
            (P.EnvelopeAttack(1), 90),
            (P.VibratoDepth, 9),
            (P.VibratoRate, 5.2f)
        );

    /// <summary>A blown tube, breathy, with a gentle vibrato.</summary>
    static IInstrument Flutes() =>
        Prepared(
            new PhysicalModel(),
            (P.Object, (int)PhysicalObject.Tube),
            (P.ExciterType(1), (int)PhysicalExciter.Blow),
            (P.ExciterType(1) + 1, 25),
            (P.ExciterType(1) + 3, 60),
            (P.Damping, 25),
            (P.Body, (int)PhysicalBody.Off),
            (P.Cutoff, 9_000),
            (P.EnvelopeAttack(1), 40),
            (P.VibratoDepth, 6),
            (P.VibratoRate, 4.8f)
        );

    /// <summary>Sample Alchemy's spectral mode on its sung "ah", swelling in.</summary>
    static IInstrument Choir() =>
        Prepared(
            new SampleAlchemy(),
            (A.Mode, (int)AlchemyMode.Spectral),
            (A.Partials, 48),
            (A.Noise, 15),
            (A.Speed, 50),
            (A.EnvelopeAttack(1), 180),
            (A.Cutoff, 9_000)
        );

    static IInstrument Prepared(IInstrument instrument, params (int Index, float Value)[] parameters) {
        instrument.Prepare(Rate, Block);
        foreach (var (index, value) in parameters) {
            instrument.SetParameter(index, value);
        }

        return instrument;
    }

    /// <summary>One note held for the tape's length, mixed to mono.</summary>
    static float[] Record(IInstrument instrument, int note) {
        var frames = (int)(TapeSet.MaxSeconds * Rate);
        var mono = new float[frames];
        var left = new float[Block];
        var right = new float[Block];
        for (var at = 0; at < frames; at += Block) {
            var n = Math.Min(Block, frames - at);
            MidiEvent[] events = at == 0 ? [new(0, 0x90, (byte)note, 100)] : [];
            instrument.Process(events, left.AsSpan(0, n), right.AsSpan(0, n));
            for (var i = 0; i < n; i++) {
                mono[at + i] = 0.5f * (left[i] + right[i]);
            }
        }

        return mono;
    }

    /// <summary>The tape model, then 24 kHz: the tape's saturation, head bump and high-frequency loss.</summary>
    static float[] Tape(float[] recording) {
        using var saturation = new Saturation();
        saturation.Prepare(Rate, Block);
        saturation.SetParameter(Saturation.Model, Saturation.Tape);
        saturation.SetParameter(Saturation.Drive, 25);
        saturation.SetParameter(Saturation.Tone, -25);
        var left = (float[])recording.Clone();
        var right = (float[])recording.Clone();
        for (var at = 0; at < left.Length; at += Block) {
            var n = Math.Min(Block, left.Length - at);
            saturation.Process(left.AsSpan(at, n), right.AsSpan(at, n), default, default);
        }

        // The head bump (+2 dB around 90 Hz) and the replay head's loss (a one-pole above 9 kHz).
        var bump = Anode.Dsp.Filters.BiquadCoefficients.Design(Anode.Dsp.Filters.BiquadType.Peak, Rate, 90, 1.2, 2);
        var state = new Anode.Dsp.Filters.BiquadState();
        var loss = 1 - Math.Exp(-2 * Math.PI * 9_000 / Rate);
        double lowPass = 0;
        for (var i = 0; i < left.Length; i++) {
            lowPass += (state.Tick(bump, left[i]) - lowPass) * loss;
            left[i] = (float)lowPass;
        }

        var tape = Decimate(left);
        var fade = (int)(0.06 * TapeRate);
        for (var i = 0; i < fade; i++) {
            tape[tape.Length - 1 - i] *= i / (float)fade;
        }

        return tape;
    }

    /// <summary>Halves the rate: a Blackman-windowed sinc low-pass at 10.5 kHz (127 taps), then every other sample.</summary>
    static float[] Decimate(float[] signal) {
        const int half = 63;
        var taps = new double[2 * half + 1];
        var cutoff = 10_500.0 / Rate;
        var sum = 0.0;
        for (var j = -half; j <= half; j++) {
            var sinc = j == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * j) / (Math.PI * j);
            var x = (j + half) / (2.0 * half);
            var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * x) + 0.08 * Math.Cos(4 * Math.PI * x);
            taps[j + half] = sinc * window;
            sum += taps[j + half];
        }

        var output = new float[signal.Length / 2];
        for (var o = 0; o < output.Length; o++) {
            var centre = 2 * o;
            var y = 0.0;
            for (var j = -half; j <= half; j++) {
                var at = centre + j;
                if ((uint)at < (uint)signal.Length) {
                    y += taps[j + half] / sum * signal[at];
                }
            }

            output[o] = (float)y;
        }

        return output;
    }
}
