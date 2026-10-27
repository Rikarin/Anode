using Anode.Contracts;
using Anode.Model.Time;
using System.Collections.Immutable;

namespace Anode.Model;

/// <summary>What a new project and a new track start with.</summary>
public static class ProjectDefaults {
    public const int SampleRate = 48_000;
    public const double Tempo = 120;
    public const int EndBars = 128;

    /// <summary>The sample rates a project may have.</summary>
    public static IReadOnlyList<int> SampleRates { get; } = [44_100, 48_000, 88_200, 96_000, 176_400, 192_000];

    /// <summary>Track colours, in the order new tracks take them.</summary>
    public static IReadOnlyList<string> Colors { get; } = [
        "#4f8ff7", "#3fbf7f", "#f2b33d", "#e5604d", "#a46be8", "#3fc4d0", "#e879b7", "#9fbf3f"
    ];

    public static ChannelStrip Strip { get; } = new(0, 0, false, false);

    /// <summary>A new region's playback: as recorded, no quantise.</summary>
    public static RegionPlayback Playback { get; } = new(0, 0, 0, 1, 0);

    /// <summary>Live MIDI from every device, on every channel.</summary>
    public static MidiInputSettings MidiInput { get; } = new(null, 0);

    public static ProjectState NewProject(string id, string name, int sampleRate) {
        var signature = new TimeSignature(4, 4);
        return new(
            id,
            name,
            sampleRate,
            new(
                Tempo,
                signature,
                new(false, 0, 4L * BarTicks(signature)),
                new(false, false, -6),
                EndBars * BarTicks(signature),
                false
            ),
            Strip,
            [],
            ImmutableSortedDictionary.Create<string, AudioRegion>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, AudioFile>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, MidiRegion>(StringComparer.Ordinal)
        );
    }

    /// <summary>Ticks in one bar of <paramref name="signature" />.</summary>
    public static long BarTicks(TimeSignature signature) => BeatTicks(signature) * signature.Numerator;

    /// <summary>Ticks in one beat (the signature's denominator note).</summary>
    public static long BeatTicks(TimeSignature signature) => TempoMap.Ppq * 4L / signature.Denominator;
}
