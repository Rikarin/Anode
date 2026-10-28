using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<TempoCurve>))]
public enum TempoCurve {
    /// <summary>The tempo holds until the next event.</summary>
    [JsonStringEnumMemberName("step")]
    Step,

    /// <summary>The tempo changes (in BPM over ticks, along the event's <c>shape</c>) to the next event's.</summary>
    [JsonStringEnumMemberName("ramp")]
    Ramp
}

/// <summary>
///     A point of the tempo track. The first is at tick 0. A ramp's tempo at fraction x (0…1) of the way to the next
///     event is bpm₀ + (bpm₁ − bpm₀)·x^(2^(−2·<c>shape</c>)), the fade formula: <c>shape</c> −1…1, 0 the linear ramp.
/// </summary>
public sealed record TempoEvent(long Tick, double Bpm, TempoCurve Curve, double Shape = 0);

/// <summary>A time signature change, on a bar line of the signature before it. The first is at tick 0.</summary>
public sealed record SignatureEvent(long Tick, int Numerator, int Denominator);

[JsonConverter(typeof(JsonStringEnumConverter<KeyMode>))]
public enum KeyMode {
    [JsonStringEnumMemberName("major")]
    Major,

    [JsonStringEnumMemberName("minor")]
    Minor
}

/// <summary>A key change; <c>tonic</c> is a pitch class 0 (C) … 11 (B). The first is at tick 0.</summary>
public sealed record KeyEvent(long Tick, int Tonic, KeyMode Mode);

/// <summary>A marker: a named position, with an optional length, colour and text (Logic's Marker List).</summary>
public sealed record Marker(string Id, string Name, long Tick, long LengthTicks, string? Color, string Text);

/// <summary>
///     A section of the song on the arrangement track (Intro, Verse, Chorus). Sections do not overlap; moving or copying
///     one with <c>moveSection</c> moves or copies everything in its time range.
/// </summary>
public sealed record ArrangementMarker(string Id, string Name, long StartTick, long LengthTicks, string Color);

[JsonConverter(typeof(JsonStringEnumConverter<CycleRecordMode>))]
public enum CycleRecordMode {
    /// <summary>Each pass of a cycle recording becomes a take in one take folder.</summary>
    [JsonStringEnumMemberName("takeFolder")]
    TakeFolder,

    /// <summary>Each pass becomes its own region, the last one on top.</summary>
    [JsonStringEnumMemberName("regions")]
    Regions
}

/// <summary>
///     How recording works: cycle recording mode; replace mode (a recording removes what it covers on its track); and
///     autopunch, which records only between <c>punchInTick</c> and <c>punchOutTick</c> while playback continues around
///     them.
/// </summary>
public sealed record RecordingSettings(
    CycleRecordMode CycleMode,
    bool Replace,
    bool Autopunch,
    long PunchInTick,
    long PunchOutTick
);

/// <summary>
///     A take folder: the takes of a cycle recording (or regions packed together) at one place on an audio track. Only
///     the comp plays: each segment plays its take from <c>startTick</c> to <c>endTick</c>, with <c>crossfade</c>
///     frames of crossfade at every boundary between segments. An empty comp plays nothing.
/// </summary>
public sealed record TakeFolder(
    string Id,
    string TrackId,
    string Name,
    long StartTick,
    long LengthTicks,
    IReadOnlyList<Take> Takes,
    IReadOnlyList<CompSegment> Comp,
    long Crossfade,
    bool Muted
);

/// <summary>A take: <c>length</c> frames of a file from <c>fileOffset</c>, starting at <c>startTick</c>.</summary>
public sealed record Take(string Id, string Name, string FileId, long StartTick, long FileOffset, long Length);

/// <summary>Part of a comp: take <c>takeId</c> plays from <c>startTick</c> to <c>endTick</c>.</summary>
public sealed record CompSegment(long StartTick, long EndTick, string TakeId);

/// <summary>
///     A saved layout of the editor's windows (Logic's screensets), recalled with keys 1–9. <c>layout</c> is the
///     editor's own serialised layout, opaque to the server.
/// </summary>
public sealed record Screenset(int Number, string Name, bool Locked, string Layout);
