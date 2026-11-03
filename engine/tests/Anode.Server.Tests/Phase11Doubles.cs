using Anode.Contracts;
using Anode.Dsp.Analysis.Chords;
using Anode.Dsp.Mastering;
using Anode.Generative;
using Anode.Ipc;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Server.Projects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>
///     A stand-in for workstream D's generator and style library: a style per player (two presets, its section
///     defaults), and a deterministic "generator" that plays, on every beat of a region, the root of the chord there
///     (the Drummer: a kick on beats 1 and 3, a snare on 2 and 4), on the rhythm source's onsets when there are any, and
///     its seed's lowest bit as an extra note; its hash is the SHA-256 of the input as JSON. Counts its generations.
/// </summary>
/// <summary>A style library that cannot be loaded: its catalogue answers 503, its generator fails.</summary>
sealed class MissingSessionPlayers : ISessionPlayerLibrary {
    static ProjectException Missing =>
        new(503, "The Session Player style library is not available: missing.");

    public SessionPlayerCatalog Catalog => throw Missing;

    public GeneratedNotes Generate(GenerationInput input) => throw Missing;

    public string Hash(GenerationInput input) => throw Missing;

    public Pattern ToPattern(IReadOnlyList<Note> notes, long lengthTicks) => throw Missing;
}

sealed class FakeSessionPlayers : ISessionPlayerLibrary {
    int generated;

    public int Generated => Volatile.Read(ref generated);

    public SessionPlayerCatalog Catalog { get; } = new(
        "fake-1",
        [
            Style("fake.drums", SessionPlayerKind.Drummer), Style("fake.bass", SessionPlayerKind.Bass),
            Style("fake.keys", SessionPlayerKind.Keyboard)
        ]
    );

    static SessionPlayerStyle Style(string id, SessionPlayerKind player) {
        return new(
            id,
            id,
            player,
            "Test",
            "A fake style.",
            false,
            null,
            [],
            [new($"{id}.a", "A", Parameters(0.25, $"{id}.a")), new($"{id}.b", "B", Parameters(0.75, $"{id}.b"))],
            [..Enum.GetValues<SectionKind>().Select((k, i) => new SectionDefaults(k, Parameters(0.1 * (i + 1), null)))]
        );

        SessionPlayerParameters Parameters(double complexity, string? preset) =>
            new(id, preset, new(complexity, 0.5), 0.5, true, 0, 0, true, null, null, null);
    }

    public GeneratedNotes Generate(GenerationInput input) {
        Interlocked.Increment(ref generated);
        var notes = new List<Note>();
        var beats = input.RhythmOnsets is { Count: > 0 } onsets
            ? [..onsets.Select(t => t - input.StartTick)]
            : Enumerable.Range(0, (int)(input.LengthTicks / 960)).Select(static b => b * 960L).ToArray();
        foreach (var beat in beats) {
            var pitch = input.Player == SessionPlayerKind.Drummer
                ? beat / 960 % 2 == 0 ? DrumKitNotes.Kick : DrumKitNotes.Snare
                : 36
                + (Edits.ChordAt(input.Chords, input.StartTick + beat) is { } chord
                    ? PitchSources.PitchClass(chord.Root)
                    : 0);
            notes.Add(new(notes.Count + 1, pitch, 100, 1, beat, 240, 0));
        }

        if ((input.Seed & 1) == 1) {
            notes.Add(
                new(
                    notes.Count + 1,
                    input.Player == SessionPlayerKind.Drummer ? DrumKitNotes.ClosedHiHat : 60,
                    80,
                    1,
                    0,
                    120,
                    0
                )
            );
        }

        return new(Hash(input), [..notes.OrderBy(static n => n.StartTick).Select((n, i) => n with { Id = i + 1 })]);
    }

    public string Hash(GenerationInput input) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Catalog.Version + JsonSerializer.Serialize(input)))
        );

    public Pattern ToPattern(IReadOnlyList<Note> notes, long lengthTicks) =>
        new(
            [
                ..notes.Select(static n => n.Pitch)
                    .Distinct()
                    .Order()
                    .Select(p => new PatternRow(
                            $"row_{p}",
                            PatternRowKind.Note,
                            p,
                            Edits.NoteName(p),
                            (int)(lengthTicks / 240),
                            null,
                            PatternPlayMode.Forward,
                            false,
                            [
                                ..Enumerable.Range(0, (int)(lengthTicks / 240))
                                    .Select(s => new PatternStep(
                                            notes.Any(n => n.Pitch == p && n.StartTick == s * 240L),
                                            100,
                                            0.5,
                                            1,
                                            1,
                                            0
                                        )
                                    )
                            ]
                        )
                    )
            ],
            0,
            1
        );
}

/// <summary>
///     A stand-in for workstream D's Chord ID: from MIDI, a chord per beat whose root is its lowest note (a major
///     triad, or minor when the note a minor third above sounds too), merged with its neighbours; from audio, C then G
///     over the halves; keys: D minor from notes, G major from audio. Records what it heard.
/// </summary>
sealed class FakeChordRecognition : IChordRecognition {
    public List<(int Notes, IReadOnlyList<long> Beats, long Resolution)> Heard { get; } = [];

    public List<(int Samples, IReadOnlyList<long> Beats)> Listened { get; } = [];

    public IReadOnlyList<RecognisedChord> FromNotes(
        IReadOnlyList<ChordNote> notes,
        IReadOnlyList<long> beats,
        long resolutionTicks,
        DetectedMusicalKey? key
    ) {
        Heard.Add((notes.Count, beats, resolutionTicks));
        var chords = new List<RecognisedChord>();
        for (var b = 0; b + 1 < beats.Count; b++) {
            var sounding = notes.Where(n => n.StartTick < beats[b + 1] && n.StartTick + n.LengthTicks > beats[b])
                .ToArray();
            if (sounding.Length == 0) {
                continue;
            }

            var root = sounding.Min(static n => n.Pitch) % 12;
            var minor = sounding.Any(n => (uint)(n.Pitch - root) % 12 == 3) ? 1 : 0;
            if (chords is [.., var last] && last.Root == root && last.Quality == minor) {
                chords[^1] = last with { End = beats[b + 1] };
            } else {
                chords.Add(new(beats[b], beats[b + 1], root, minor, -1, 0.9));
            }
        }

        return chords;
    }

    public IReadOnlyList<RecognisedChord> FromAudio(
        float[] mono,
        float sampleRate,
        IReadOnlyList<long> beats,
        DetectedMusicalKey? key
    ) {
        Listened.Add((mono.Length, beats));
        var half = mono.Length / 2;
        return [new(0, half, 0, 0, -1, 0.8), new(half, mono.Length, 7, 0, 11, 0.7)];
    }

    public DetectedMusicalKey KeyOfNotes(IReadOnlyList<ChordNote> notes) => new(2, true, 0.75);

    public DetectedMusicalKey KeyOfAudio(float[] mono, float sampleRate) => new(7, false, 0.6);
}

/// <summary>
///     A stand-in for workstream E's analysis: the render's integrated loudness as its RMS in dB less 0.691 (a sine's
///     loudness, near enough), its true peak as its sample peak, a flat spectrum and curve. Keeps what it measured.
/// </summary>
sealed class FakeMasteringAnalysis : IMasteringAnalysis {
    public int Samples { get; private set; }

    public MasteringMeasurement Analyse(float[] left, float[] right, float sampleRate) {
        Samples = left.Length;
        var power = left.Concat(right).Select(static s => (double)s * s).DefaultIfEmpty().Average();
        var peak = left.Concat(right).Select(static s => (double)Math.Abs(s)).DefaultIfEmpty().Max();
        return new(
            10 * Math.Log10(Math.Max(power, 1e-12)) - 0.691,
            -12,
            4,
            20 * Math.Log10(Math.Max(peak, 1e-6)),
            9,
            0.5,
            [..Contracts.MasteringBands.CentresHz.Select(static _ => 0f)]
        );
    }

    public IReadOnlyList<float> Curve(MasteringCharacter character) =>
        [..Contracts.MasteringBands.CentresHz.Select(_ => (float)character)];
}

/// <summary>A stand-in for workstream E's chain designer: EQ flat, a −18 dB threshold, the character as the ratio.</summary>
sealed class FakeMasteringDesigner : IMasteringDesigner {
    public int Designs { get; private set; }

    public EngineMasteringChain Design(MasteringAnalysis analysis, MasteringCharacter character) {
        Designs++;
        return new(
            [..Contracts.MasteringBands.CentresHz.Select(static _ => 0f)],
            -18,
            2 + (int)character,
            (float)analysis.IntegratedLufs,
            -1.5f
        );
    }
}
