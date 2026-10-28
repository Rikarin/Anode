using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     A track's flex algorithm (docs/06-phase-5.md). A region is <em>warped</em> when its track's flex is on, its
///     <c>follow</c> is not <c>off</c>, or it has a transpose or pitch notes; warped regions play through the track's
///     algorithm (Polyphonic while flex is off).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlexMode>))]
public enum FlexMode {
    /// <summary>Stretch with a short block, for single voices.</summary>
    [JsonStringEnumMemberName("monophonic")]
    Monophonic,

    /// <summary>Slices at the warp points, each played at its original speed and cut at the next; gaps are silent.</summary>
    [JsonStringEnumMemberName("slicing")]
    Slicing,

    /// <summary>As slicing, with each gap filled by looping the slice's last 30 ms, crossfaded.</summary>
    [JsonStringEnumMemberName("rhythmic")]
    Rhythmic,

    /// <summary>Stretch, for mixes and chords: the default.</summary>
    [JsonStringEnumMemberName("polyphonic")]
    Polyphonic,

    /// <summary>Resampled: pitch follows speed.</summary>
    [JsonStringEnumMemberName("speed")]
    Speed,

    /// <summary>Granular, 60 ms grains with Hann crossfades.</summary>
    [JsonStringEnumMemberName("tempophone")]
    Tempophone,

    /// <summary>Flex Pitch: Stretch with the pitch notes' transpose curve and formant compensation.</summary>
    [JsonStringEnumMemberName("pitch")]
    Pitch
}

/// <summary>How an audio region follows the project tempo (Logic's Flex &amp; Follow).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlexFollow>))]
public enum FlexFollow {
    /// <summary>The file plays in time: its frames map 1:1 to project samples.</summary>
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>The file's average tempo (<c>tempo.bpm</c>) is the project tempo: a file beat maps to a quarter note.</summary>
    [JsonStringEnumMemberName("on")]
    On,

    /// <summary>The file's downbeats land on the project's bars, linear in ticks between them.</summary>
    [JsonStringEnumMemberName("bars")]
    Bars,

    /// <summary>The file's downbeats and beats land on the project's bars and beats.</summary>
    [JsonStringEnumMemberName("barsAndBeats")]
    BarsAndBeats
}

/// <summary>
///     A flex marker: file frame <c>fileFrame</c> (absolute in the file) plays at <c>tick</c>, relative to the region's
///     <c>startTick</c>. Between neighbouring markers, and between the outermost markers and the region's ends, the
///     region's base map is scaled linearly; the region's start and end stay where the base map puts them.
/// </summary>
public sealed record FlexMarker(long FileFrame, long Tick);

/// <summary>
///     A Flex Pitch note of a region, over file frames <c>startFrame</c>…<c>endFrame</c>. Pitches are MIDI cents (6900
///     is A4): the analysed <c>detectedCents</c> plays as <c>targetCents</c>. <c>drift</c> and <c>vibrato</c> scale the
///     analysed drift and vibrato around the note's own trend (1 unchanged, 0 removed, up to 2); <c>formantCents</c>
///     shifts the formants, <c>gainDb</c> the level.
/// </summary>
public sealed record PitchNote(
    long StartFrame,
    long EndFrame,
    double DetectedCents,
    double TargetCents,
    double Drift,
    double Vibrato,
    double FormantCents,
    double GainDb
);

/// <summary>
///     A file's tempo (Smart Tempo): its average tempo, whether it varies, and its beats and downbeats in file frames,
///     ascending.
/// </summary>
public sealed record FileTempo(double Bpm, bool Variable, IReadOnlyList<long> Beats, IReadOnlyList<long> Downbeats);

/// <summary>What an audio import or recording does to the project tempo (Logic's project tempo modes).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectTempoMode>))]
public enum ProjectTempoMode {
    /// <summary>The project tempo stays.</summary>
    [JsonStringEnumMemberName("keep")]
    Keep,

    /// <summary>The first audio import or recording sets the tempo events from its beats, and plays unwarped.</summary>
    [JsonStringEnumMemberName("adapt")]
    Adapt,

    /// <summary>As adapt while the project has no MIDI regions and a single tempo event; keep otherwise.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto
}

/// <summary>
///     Smart Tempo: the project tempo mode, the <c>follow</c> new audio regions get, and whether imports are analysed
///     (transients and beats) as they arrive.
/// </summary>
public sealed record SmartTempoSettings(ProjectTempoMode Mode, FlexFollow DefaultFollow, bool AnalyzeImports);

[JsonConverter(typeof(JsonStringEnumConverter<VarispeedMode>))]
public enum VarispeedMode {
    /// <summary>The project plays faster or slower at its pitch: audio is stretched.</summary>
    [JsonStringEnumMemberName("speedOnly")]
    SpeedOnly,

    /// <summary>As tape: audio is resampled, so its pitch follows the speed; MIDI keeps its pitch.</summary>
    [JsonStringEnumMemberName("speedAndPitch")]
    SpeedAndPitch,

    /// <summary>As speed and pitch, with MIDI notes transposed by round(12·log2(speed)) semitones.</summary>
    [JsonStringEnumMemberName("speedAndPitchMidi")]
    SpeedAndPitchMidi
}

/// <summary>
///     Varispeed: while enabled, the whole project plays at <c>speed</c> (0.5…1.5) of its tempo; the server compiles it
///     (every position scaled by 1/speed), and the transport reports compiled samples.
/// </summary>
public sealed record VarispeedSettings(bool Enabled, VarispeedMode Mode, double Speed);

/// <summary>
///     An inactive alternative of a track: a snapshot of its regions, MIDI regions and take folders. The track's live
///     ones are its active alternative (<c>alternativeId</c>); selecting another swaps them in one undoable step, and
///     ids stay unique project-wide.
/// </summary>
public sealed record TrackAlternative(
    string Id,
    string Name,
    IReadOnlyList<AudioRegion> Regions,
    IReadOnlyList<MidiRegion> MidiRegions,
    IReadOnlyList<TakeFolder> TakeFolders
);

/// <summary>
///     Beat mapping: tick <c>tick</c> lands on project sample <c>sample</c> (the tempo events are set so that it does,
///     keeping earlier mappings and the positions before them).
/// </summary>
public sealed record BeatMapping(long Tick, long Sample);

/// <summary>A stable voiced run of a file's pitch curve, in file frames, at <c>cents</c> (MIDI cents).</summary>
public sealed record PitchSegment(long StartFrame, long EndFrame, double Cents, double Confidence);

/// <summary>
///     A file's analysis, cached in the package as <c>Analysis/&lt;fileId&gt;.json</c> and rebuilt on demand: its
///     transients (file frames), its tempo (null until beats are tracked), its pitch curve (MIDI cents per
///     <c>hop</c> frames from frame 0; null where unvoiced; empty until pitch is analysed), its notes, and whether
///     it sounds polyphonic (Flex Pitch then works less well).
/// </summary>
public sealed record FileAnalysis(
    string FileId,
    int Hop,
    IReadOnlyList<long> Transients,
    FileTempo? Tempo,
    IReadOnlyList<float?> Pitch,
    IReadOnlyList<PitchSegment> Notes,
    bool Polyphonic
);
