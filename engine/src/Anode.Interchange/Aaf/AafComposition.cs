namespace Anode.Interchange.Aaf;

/// <summary>
///     An AAF composition as Anode reads it (docs/10-phase-9.md, workstream E): positions and lengths in samples at
///     <c>SampleRate</c> (the edit rate), its audio slots as tracks, and its markers.
/// </summary>
public sealed record AafComposition(
    string Name,
    int SampleRate,
    IReadOnlyList<AafTrack> Tracks,
    IReadOnlyList<AafMarker> Markers
);

/// <summary>A timeline slot of audio: its clips in time order (fillers left out).</summary>
public sealed record AafTrack(string Name, IReadOnlyList<AafClip> Clips);

/// <summary>
///     A source clip: <c>MediaPath</c> is a WAV file on disk (embedded essence extracted by the reader, or a linked
///     file's resolved location), placed at <c>Position</c> from its <c>SourceOffset</c> for <c>Length</c> samples, with
///     its clip gain in dB and fades in samples. A <c>Transition</c> between two clips becomes their overlap by its
///     length, a fade out on the first and a fade in on the second.
/// </summary>
public sealed record AafClip(
    string Name,
    string MediaPath,
    long Position,
    long SourceOffset,
    long Length,
    double GainDb,
    long FadeIn,
    long FadeOut
);

/// <summary>A <c>DescriptiveMarker</c>: its name, position and length in samples, and its comment.</summary>
public sealed record AafMarker(string Name, long Position, long Length, string Comment);
