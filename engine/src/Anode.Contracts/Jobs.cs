using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind {
    [JsonStringEnumMemberName("bounce")]
    Bounce,

    [JsonStringEnumMemberName("freeze")]
    Freeze,

    /// <summary>Pitch analysis of a file (Flex Pitch); an <c>analysis</c> event follows when it is cached.</summary>
    [JsonStringEnumMemberName("analyze")]
    Analyze,

    [JsonStringEnumMemberName("bounceInPlace")]
    BounceInPlace,

    /// <summary>Selection-based processing.</summary>
    [JsonStringEnumMemberName("process")]
    Process
}

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState {
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>A long-running job; <c>progress</c> 0…1; <c>files</c> are what it wrote (absolute paths), when done.</summary>
public sealed record Job(
    string Id,
    JobKind Kind,
    JobState State,
    double Progress,
    string? Message,
    IReadOnlyList<string> Files
);

[JsonConverter(typeof(JsonStringEnumConverter<BounceSource>))]
public enum BounceSource {
    /// <summary>The master output.</summary>
    [JsonStringEnumMemberName("mix")]
    Mix,

    /// <summary>One file per track in <c>trackIds</c> (post-fader, as it reaches its output).</summary>
    [JsonStringEnumMemberName("stems")]
    Stems
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioFormat>))]
public enum AudioFormat {
    [JsonStringEnumMemberName("wav")]
    Wav,

    [JsonStringEnumMemberName("aiff")]
    Aiff
}

[JsonConverter(typeof(JsonStringEnumConverter<DitherMode>))]
public enum DitherMode {
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Triangular (TPDF) dither.</summary>
    [JsonStringEnumMemberName("tpdf")]
    Tpdf,

    /// <summary>TPDF with first-order noise shaping.</summary>
    [JsonStringEnumMemberName("shaped")]
    Shaped
}

[JsonConverter(typeof(JsonStringEnumConverter<NormalizeMode>))]
public enum NormalizeMode {
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>Peak to <c>peakCeilingDb</c> (true peak, 4× oversampled).</summary>
    [JsonStringEnumMemberName("peak")]
    Peak,

    /// <summary>Integrated loudness (EBU R128) to <c>loudnessLufs</c>, limited to <c>peakCeilingDb</c> true peak.</summary>
    [JsonStringEnumMemberName("loudness")]
    Loudness
}

/// <summary>
///     Bounce (export): the range (ticks; left out: the cycle when it is on, else the project start to its end), the
///     source, and the file: format, bit depth (16, 24, or 32 for float), sample rate (left out: the project's), dither
///     (applied below 32 bits), normalisation. <c>realtime</c> plays the bounce through the device instead of rendering
///     offline; <c>addToProject</c> adds the result to the project's audio files.
/// </summary>
public sealed record BounceRequest(
    string FileName,
    string? Directory = null,
    long? StartTick = null,
    long? EndTick = null,
    BounceSource Source = BounceSource.Mix,
    IReadOnlyList<string>? TrackIds = null,
    AudioFormat Format = AudioFormat.Wav,
    int BitDepth = 24,
    int? SampleRate = null,
    DitherMode Dither = DitherMode.Tpdf,
    NormalizeMode Normalize = NormalizeMode.Off,
    double LoudnessLufs = -14,
    double PeakCeilingDb = -1,
    bool Realtime = false,
    bool AddToProject = false
);

[JsonConverter(typeof(JsonStringEnumConverter<BounceInPlaceDestination>))]
public enum BounceInPlaceDestination {
    /// <summary>A new audio track under each source track, with the bounced regions.</summary>
    [JsonStringEnumMemberName("newTrack")]
    NewTrack,

    /// <summary>The bounced regions replace the source regions on their own track (audio tracks only).</summary>
    [JsonStringEnumMemberName("replace")]
    Replace
}

[JsonConverter(typeof(JsonStringEnumConverter<BounceSourceAction>))]
public enum BounceSourceAction {
    [JsonStringEnumMemberName("leave")]
    Leave,

    [JsonStringEnumMemberName("mute")]
    Mute,

    [JsonStringEnumMemberName("delete")]
    Delete
}

/// <summary>
///     Bounce in place: tracks (<c>trackIds</c>, their whole range) or regions (<c>regionIds</c>, theirs) rendered
///     isolated by the host through <c>render</c>, through their inserts unless <c>bypassEffects</c>, with fader and pan
///     only when <c>includeVolumePan</c>, plus the effects' tail when <c>includeTail</c>; normalised as a bounce. When
///     the job finishes, the server adds the files and, in one undo step, adds a new track (or replaces the regions) and
///     leaves, mutes or deletes the source. <c>name</c> names the new track or regions (left out: the source's, plus
///     " Bounce").
/// </summary>
public sealed record BounceInPlaceRequest(
    IReadOnlyList<string>? TrackIds = null,
    IReadOnlyList<string>? RegionIds = null,
    BounceInPlaceDestination Destination = BounceInPlaceDestination.NewTrack,
    BounceSourceAction Source = BounceSourceAction.Mute,
    bool BypassEffects = false,
    bool IncludeTail = true,
    bool IncludeVolumePan = false,
    NormalizeMode Normalize = NormalizeMode.Off,
    string? Name = null
);

/// <summary>A built-in effect of a processing chain, with the parameters it is set to (the rest at their defaults).</summary>
public sealed record ProcessEffect(string PluginId, IReadOnlyDictionary<string, double>? Parameters = null);

/// <summary>
///     Selection-based processing: runs <c>chain</c> (built-in effects only, in order) in-process over the regions'
///     audio between <c>startTick</c> and <c>endTick</c> (left out: the regions' own ends), with the chain's tail when
///     <c>includeTail</c>. When the job finishes, the server writes a new file per region and, in one undo step,
///     replaces that part of each region with a new region of it.
/// </summary>
public sealed record ProcessRequest(
    IReadOnlyList<string> RegionIds,
    IReadOnlyList<ProcessEffect> Chain,
    long? StartTick = null,
    long? EndTick = null,
    bool IncludeTail = false
);
