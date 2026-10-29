using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>An Audio File Editor operation (docs/08-phase-7.md, "Audio File Editor").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AudioEditKind>))]
public enum AudioEditKind {
    /// <summary>Scales the selection so its peak is <c>value</c> dBFS (−1 by default).</summary>
    [JsonStringEnumMemberName("normalize")]
    Normalize,

    /// <summary>Changes the selection's level by <c>value</c> dB.</summary>
    [JsonStringEnumMemberName("gain")]
    Gain,

    /// <summary>Fades the selection in from silence, with <c>curve</c> −1…1 as a region fade's (0: linear).</summary>
    [JsonStringEnumMemberName("fadeIn")]
    FadeIn,

    /// <summary>Fades the selection out to silence, with <c>curve</c> as a region fade's.</summary>
    [JsonStringEnumMemberName("fadeOut")]
    FadeOut,

    [JsonStringEnumMemberName("reverse")]
    Reverse,

    /// <summary>Inverts the selection's polarity.</summary>
    [JsonStringEnumMemberName("invert")]
    Invert,

    [JsonStringEnumMemberName("silence")]
    Silence,

    /// <summary>Removes the selection's DC offset (its mean, per channel).</summary>
    [JsonStringEnumMemberName("removeDc")]
    RemoveDc,

    /// <summary>Keeps the selection only: the file becomes <c>endFrame − startFrame</c> frames long.</summary>
    [JsonStringEnumMemberName("trim")]
    Trim,

    /// <summary>Converts the whole file to <c>targetRate</c> Hz (the selection is ignored), with the bounce resampler.</summary>
    [JsonStringEnumMemberName("convertRate")]
    ConvertRate
}

/// <summary>
///     An operation on frames <c>startFrame</c> (inclusive) … <c>endFrame</c> (exclusive) of a file, every channel.
///     <c>value</c>: normalize's peak (dBFS, default −1) or gain's change (dB); <c>curve</c>: a fade's curve (default
///     0); <c>targetRate</c>: convertRate's rate. Posted to <c>/project/files/{fileId}/edit</c>, it runs as an
///     <c>audioEdit</c> job that reads the file's current version and writes <c>&lt;name&gt;.v&lt;N&gt;.wav</c> beside
///     it; when it finishes, the server applies <see cref="SetFileVersionCommand" /> (with the region shifts a trim or
///     rate conversion needs) as one undo step and recomputes the peaks.
/// </summary>
public sealed record AudioEditOperation(
    AudioEditKind Kind,
    long StartFrame,
    long EndFrame,
    double? Value = null,
    double? Curve = null,
    int? TargetRate = null
);

/// <summary>
///     Loop search over frames <c>startFrame</c>…<c>endFrame</c> of a file's current version: the <c>count</c> best
///     loops (1…20; left out: 5) at least <c>minFrames</c> long (left out: 10 ms).
/// </summary>
public sealed record LoopSearchRequest(long StartFrame, long EndFrame, int? Count = null, long? MinFrames = null);

/// <summary>
///     A loop found by loop search: frames <c>startFrame</c>…<c>endFrame</c> (exclusive), and how seamlessly it
///     repeats (<c>score</c> 0…1, 1 best). Results come best first.
/// </summary>
public sealed record LoopSearchResult(long StartFrame, long EndFrame, double Score);

/// <summary>
///     Samples of a file's current version for the Audio File Editor (<c>/project/files/{fileId}/samples</c>):
///     <c>frames</c> frames from <c>startFrame</c>, in buckets of <c>bucket</c> frames. With <c>bucket</c> 1,
///     <c>samples</c> holds each channel's samples and <c>min</c> and <c>max</c> are null; with a larger bucket,
///     <c>min</c> and <c>max</c> hold each channel's extremes per bucket (the last bucket may be short) and
///     <c>samples</c> is null.
/// </summary>
public sealed record SampleRange(
    long StartFrame,
    long Frames,
    int Channels,
    int Bucket,
    IReadOnlyList<IReadOnlyList<float>>? Samples,
    IReadOnlyList<IReadOnlyList<float>>? Min,
    IReadOnlyList<IReadOnlyList<float>>? Max
);

/// <summary>
///     Plays frames <c>startFrame</c>…<c>endFrame</c> of a file's current version through the preview channel
///     (<c>/project/files/{fileId}/preview</c>), looping when <c>loop</c>; <c>POST /files/preview</c> with a null path
///     stops it.
/// </summary>
public sealed record PreviewRangeRequest(long StartFrame, long EndFrame, bool? Loop = null);
