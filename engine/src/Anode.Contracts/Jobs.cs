using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind {
    [JsonStringEnumMemberName("bounce")]
    Bounce,

    [JsonStringEnumMemberName("freeze")]
    Freeze
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
