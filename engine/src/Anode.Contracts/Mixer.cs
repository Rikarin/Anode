using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     An insert effect on a channel strip: the plugin, whether it is bypassed, its parameter values by parameter id
///     (all of them), and its sidechain source (a track id or <c>bus:N</c>; null: none). A plugin's opaque state lives in
///     the project's <c>Plugin States</c> folder. A MIDI FX slot (<c>ChannelStrip.midiEffects</c>) has the same shape,
///     never a sidechain. Phase 6: <c>file</c> is the file the effect loads (an impulse response for
///     <c>anode.space</c>; <c>loadInsertFile</c>), relative to the project when it is inside it (null: none).
/// </summary>
public sealed record Insert(
    string Id,
    string PluginId,
    string Name,
    bool Bypassed,
    IReadOnlyDictionary<string, double> Parameters,
    string? Sidechain,
    string? File = null
);

/// <summary>A send from a channel strip to bus 1…64, at <c>levelDb</c>, before or after the fader.</summary>
public sealed record Send(string Id, int Bus, double LevelDb, double Pan, bool PreFader);

/// <summary>What a mixer group links among its members (Logic's group settings).</summary>
public sealed record GroupProperties(
    bool Volume,
    bool Mute,
    bool Solo,
    bool Pan,
    bool Sends,
    bool RecordArm,
    bool Automation,
    bool Selection
);

/// <summary>A mixer group 1…32; tracks join it through their strip's <c>groups</c>.</summary>
public sealed record MixerGroup(int Number, string Name, string Color, bool Enabled, GroupProperties Properties);

[JsonConverter(typeof(JsonStringEnumConverter<AutomationMode>))]
public enum AutomationMode {
    /// <summary>Automation neither plays nor records.</summary>
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>Automation plays; moving a control changes it only until the next point.</summary>
    [JsonStringEnumMemberName("read")]
    Read,

    /// <summary>Plays; while a control is held during playback it records, and returns to the lane when released.</summary>
    [JsonStringEnumMemberName("touch")]
    Touch,

    /// <summary>As touch, but after release keeps the last value, recording it until playback stops.</summary>
    [JsonStringEnumMemberName("latch")]
    Latch,

    /// <summary>Records every automated control's current value over the whole pass, replacing what was there.</summary>
    [JsonStringEnumMemberName("write")]
    Write
}

/// <summary>A point of an automation lane, in the target's own unit; <c>curve</c> −1…1 shapes the segment to the next.</summary>
public sealed record AutomationPoint(long Tick, double Value, double Curve);

/// <summary>
///     Track automation of one parameter. <c>trackId</c> null is the master strip. <c>target</c> is <c>volume</c> (dB),
///     <c>pan</c> (−1…1), <c>mute</c> (0/1), <c>send:{sendId}</c> (dB), <c>insert:{insertId}:{parameterId}</c> or
///     <c>instrument:{parameterId}</c> (the parameter's unit). Phase 6 adds <c>midiEffect:{slotId}:{parameterId}</c> (a
///     MIDI FX slot's parameter) and <c>smartControl:{controlId}</c> (0…1; it compiles to automation of every
///     parameter the control maps). Points are sorted by tick.
/// </summary>
public sealed record AutomationLane(string Id, string? TrackId, string Target, IReadOnlyList<AutomationPoint> Points);

[JsonConverter(typeof(JsonStringEnumConverter<PdcMode>))]
public enum PdcMode {
    /// <summary>Compensate every path, buses and the master included.</summary>
    [JsonStringEnumMemberName("all")]
    All,

    /// <summary>Compensate audio and instrument tracks only (aux and master plugins add latency).</summary>
    [JsonStringEnumMemberName("tracks")]
    Tracks,

    [JsonStringEnumMemberName("off")]
    Off
}

/// <summary>
///     Plugin delay compensation, and low-latency mode: while it is on, plugins on the path of record-armed or
///     monitored tracks whose latency exceeds <c>lowLatencyLimitMs</c> are bypassed.
/// </summary>
public sealed record MixerSettings(PdcMode Pdc, bool LowLatency, double LowLatencyLimitMs);

/// <summary>A user name for an input, output pair or bus (Logic's I/O Labels).</summary>
public sealed record IoLabel(int Number, string Name);

public sealed record IoLabels(
    IReadOnlyList<IoLabel> Inputs,
    IReadOnlyList<IoLabel> Outputs,
    IReadOnlyList<IoLabel> Buses);

/// <summary>A saved channel strip (Logic's patch): its instrument, inserts and sends, for a kind of track.</summary>
public sealed record PatchInfo(string Id, string Name, string Category, TrackKind Kind);

public sealed record SavePatchRequest(string TrackId, string Name, string Category);

/// <summary>Opens (or closes) a plugin's own editor window: an insert, or with no <c>insertId</c> the instrument.</summary>
public sealed record PluginEditorRequest(string? TrackId, string? InsertId, bool Open);
