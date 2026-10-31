#!/usr/bin/env dotnet
// The Stem Splitter's test mix (docs/09-phase-8.md, exit criterion step 9): 20 s at 44.1 kHz, stereo, from four known
// stems played by Anode's own instruments, each written as 32-bit float WAV beside their sum:
//
//   vocals.wav  a "vocal-like" lead: the Synth's sawtooth with vibrato and glide, in the voice range (G3–E5), through
//               three formant band-passes per vowel (a, e, i, o, u, a new one every note)
//   drums.wav   the Drum Synth: kick, snare, closed hats
//   bass.wav    the Synth: a low-passed sawtooth bass line (E1–A2)
//   other.wav   keys and a pad: Electric Piano chords and a soft Synth pad
//   mix.wav     their sum (no limiting, so the stems add up to it exactly)
//
// Run by scripts/build-content.mjs --stems (into engine/artifacts/content/stem-mix/, generated, never committed), or:
//   dotnet run scripts/build-stem-mix.cs -- <output folder>
#:project ../engine/src/Anode.Dsp/Anode.Dsp.csproj
#:property TreatWarningsAsErrors=false
#:property PublishAot=false

using System.Buffers.Binary;
using System.Text;
using Anode.Dsp.Filters;
using Anode.Dsp.Instruments;

const int Rate = 44100;
const int Block = 256;
const double Bpm = 120;
const int Frames = 20 * Rate;
const double Beat = Rate * 60 / Bpm;

var output = args.Length > 0 ? args[0] : Path.Combine("engine", "artifacts", "content", "stem-mix");
Directory.CreateDirectory(output);

long At(double beats) => (long)Math.Round(beats * Beat);

// Drums: four on the floor kick on 1 and 3 (and the "and" of 3 every other bar), snare on 2 and 4, eighth hats.
var kick = new List<(double, double, int, int)>();
var snare = new List<(double, double, int, int)>();
var hats = new List<(double, double, int, int)>();
for (var bar = 0; bar < 10; bar++) {
    var b = bar * 4.0;
    kick.Add((b, 0.5, 36, 120));
    kick.Add((b + 2, 0.5, 36, 115));
    if (bar % 2 == 1) {
        kick.Add((b + 2.5, 0.5, 36, 100));
    }

    snare.Add((b + 1, 0.5, 38, 110));
    snare.Add((b + 3, 0.5, 38, 112));
    for (var e = 0; e < 8; e++) {
        hats.Add((b + e * 0.5, 0.25, 42, e % 2 == 0 ? 90 : 65));
    }
}

var drums = Sum(
    Render("anode.drum-synth", new() { ["model"] = 0 }, kick, 1.0f, 0),
    Render("anode.drum-synth", new() { ["model"] = 1 }, snare, 0.8f, -0.1f),
    Render("anode.drum-synth", new() { ["model"] = 3, ["decay"] = 15 }, hats, 0.5f, 0.3f)
);

// Bass: E, C, G, D roots, eighth notes with octave jumps.
int[] roots = [40, 36, 43, 38];
var bassNotes = new List<(double, double, int, int)>();
for (var bar = 0; bar < 10; bar++) {
    var root = roots[bar % 4] - 12;
    for (var e = 0; e < 8; e++) {
        bassNotes.Add((bar * 4 + e * 0.5, 0.45, root + (e == 3 || e == 7 ? 12 : 0), e % 2 == 0 ? 110 : 90));
    }
}

var bass = Render(
    "anode.synth",
    new() {
        ["osc1Wave"] = 0, ["osc2Wave"] = 1, ["oscMix"] = 30, ["filterMode"] = 0, ["cutoff"] = 500,
        ["resonance"] = 20, ["filterEnvAmount"] = 30, ["filterDecay"] = 200, ["ampSustain"] = 90,
        ["ampRelease"] = 60, ["lfoPitch"] = 0
    },
    bassNotes,
    0.9f,
    0
);

// Other: Electric Piano chords on every half bar, and a soft pad an octave up holding each bar.
int[][] chords = [[52, 55, 59, 64], [48, 52, 55, 60], [55, 59, 62, 67], [50, 54, 57, 62]];
var keyNotes = new List<(double, double, int, int)>();
var padNotes = new List<(double, double, int, int)>();
for (var bar = 0; bar < 10; bar++) {
    foreach (var pitch in chords[bar % 4]) {
        keyNotes.Add((bar * 4, 1.8, pitch, 80));
        keyNotes.Add((bar * 4 + 2.5, 1.3, pitch, 70));
        padNotes.Add((bar * 4, 3.9, pitch + 12, 60));
    }
}

var other = Sum(
    Render("anode.electric-piano", new(), keyNotes, 0.6f, -0.25f),
    Render(
        "anode.synth",
        new() {
            ["osc1Wave"] = 2, ["osc2Wave"] = 0, ["oscMix"] = 40, ["cutoff"] = 2500, ["resonance"] = 5,
            ["ampAttack"] = 400, ["ampRelease"] = 600, ["unison"] = 2, ["lfoPitch"] = 0
        },
        padNotes,
        0.25f,
        0.25f
    )
);

// Vocals: a sung line in the voice range, legato, one vowel per note.
int[] melody = [64, 67, 69, 67, 64, 62, 60, 62, 64, 67, 71, 69, 67, 64, 67, 69, 72, 71, 69, 67];
var vocalNotes = new List<(double, double, int, int)>();
var vowelChanges = new List<(long Frame, int Vowel)>();
for (var i = 0; i < melody.Length; i++) {
    var start = 0.5 + i * 2.0;
    vocalNotes.Add((start, 1.85, melody[i], 100));
    vowelChanges.Add((At(start), i % 5));
}

var raw = Render(
    "anode.synth",
    new() {
        ["osc1Wave"] = 0, ["osc2Wave"] = 0, ["oscMix"] = 50, ["osc2Fine"] = 4, ["filterMode"] = 0,
        ["cutoff"] = 6000, ["resonance"] = 0, ["filterEnvAmount"] = 0, ["ampAttack"] = 60, ["ampSustain"] = 85,
        ["ampRelease"] = 150, ["lfoRate"] = 5.5f, ["lfoPitch"] = 0.35f, ["lfoModWheel"] = 0, ["glide"] = 60
    },
    vocalNotes,
    1,
    0
);
var vocals = Formants(raw, vowelChanges);

// Common levels: each stem's peak at 0.3, so the sum stays below full scale without limiting.
foreach (var stem in new[] { vocals, drums, bass, other }) {
    Normalize(stem, 0.3f);
}

var mix = Sum(vocals, drums, bass, other);
Write("vocals", vocals);
Write("drums", drums);
Write("bass", bass);
Write("other", other);
Write("mix", mix);
Console.WriteLine($"Stem test mix: {Path.GetFullPath(output)} (mix peak {Peak(mix):F3})");
return 0;

float[][] Render(
    string id,
    Dictionary<string, float> parameters,
    List<(double Start, double Length, int Pitch, int Velocity)> notes,
    float gain,
    float pan
) {
    using var instrument = BuiltinInstruments.Create(id) ?? throw new InvalidOperationException($"No instrument '{id}'.");
    instrument.Prepare(Rate, Block);
    foreach (var parameter in instrument.Descriptor.Parameters) {
        if (parameters.TryGetValue(parameter.Id, out var value)) {
            instrument.SetParameter(parameter.Index, value);
        }
    }

    var events = new List<(long Frame, int Rank, MidiEvent Event)>();
    foreach (var n in notes) {
        events.Add((At(n.Start), 1, new(0, 0x90, (byte)n.Pitch, (byte)n.Velocity)));
        events.Add((At(n.Start + n.Length), 0, new(0, 0x80, (byte)n.Pitch, 0)));
    }

    events.Sort(static (a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Rank.CompareTo(b.Rank));
    var left = new float[Frames];
    var right = new float[Frames];
    var blockLeft = new float[Block];
    var blockRight = new float[Block];
    var pending = new List<MidiEvent>();
    var next = 0;
    var leftGain = gain * MathF.Sqrt(0.5f * (1 - pan));
    var rightGain = gain * MathF.Sqrt(0.5f * (1 + pan));
    for (long at = 0; at < Frames; at += Block) {
        var length = (int)Math.Min(Block, Frames - at);
        pending.Clear();
        while (next < events.Count && events[next].Frame < at + length) {
            pending.Add(events[next].Event with { Frame = (int)Math.Max(0, events[next].Frame - at) });
            next++;
        }

        instrument.Process(pending.ToArray(), blockLeft.AsSpan(0, length), blockRight.AsSpan(0, length));
        for (var i = 0; i < length; i++) {
            left[at + i] = blockLeft[i] * leftGain;
            right[at + i] = blockRight[i] * rightGain;
        }
    }

    return [left, right];
}

// Three parallel band-passes at the vowel's first three formants (adult voice), with a short cross-fade at a change.
static float[][] Formants(float[][] input, List<(long Frame, int Vowel)> changes) {
    double[][] vowels = [
        [730, 1090, 2440], // a
        [530, 1840, 2480], // e
        [270, 2290, 3010], // i
        [570, 840, 2410], // o
        [300, 870, 2240] // u
    ];
    double[] gains = [1.0, 0.6, 0.35];
    var output = new float[2][];
    for (var c = 0; c < 2; c++) {
        var states = new BiquadState[3];
        output[c] = new float[input[c].Length];
        var change = 0;
        var vowel = 0;
        var coefficients = Design(vowels[0]);
        for (var n = 0; n < input[c].Length; n++) {
            if (change < changes.Count && n >= changes[change].Frame) {
                vowel = changes[change].Vowel;
                coefficients = Design(vowels[vowel]);
                change++;
            }

            var sum = 0.0;
            for (var f = 0; f < 3; f++) {
                sum += gains[f] * states[f].Tick(coefficients[f], input[c][n]);
            }

            output[c][n] = (float)sum;
        }
    }

    return output;

    static BiquadCoefficients[] Design(double[] formants) =>
        [.. formants.Select(f => BiquadCoefficients.Design(BiquadType.BandPass, Rate, f, f / 90))];
}

static float[][] Sum(params float[][][] parts) {
    var result = new float[2][];
    for (var c = 0; c < 2; c++) {
        result[c] = new float[parts[0][c].Length];
        foreach (var part in parts) {
            for (var n = 0; n < result[c].Length; n++) {
                result[c][n] += part[c][n];
            }
        }
    }

    return result;
}

static float Peak(float[][] audio) => audio.Max(static c => c.Max(static v => Math.Abs(v)));

static void Normalize(float[][] audio, float peak) {
    var scale = peak / Math.Max(1e-9f, Peak(audio));
    foreach (var channel in audio) {
        for (var n = 0; n < channel.Length; n++) {
            channel[n] *= scale;
        }
    }
}

void Write(string name, float[][] audio) {
    var frames = audio[0].Length;
    var data = new byte[frames * 2 * 4];
    for (var n = 0; n < frames; n++) {
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8 * n), audio[0][n]);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8 * n + 4), audio[1][n]);
    }

    using var file = File.Create(Path.Combine(output, name + ".wav"));
    using var writer = new BinaryWriter(file, Encoding.ASCII);
    writer.Write("RIFF"u8);
    writer.Write(36 + data.Length);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((short)3); // IEEE float
    writer.Write((short)2);
    writer.Write(Rate);
    writer.Write(Rate * 8);
    writer.Write((short)8);
    writer.Write((short)32);
    writer.Write("data"u8);
    writer.Write(data.Length);
    writer.Write(data);
}
