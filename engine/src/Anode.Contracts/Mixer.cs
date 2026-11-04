using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     An insert effect on a channel strip: the plugin, whether it is bypassed, its parameter values by parameter id
///     (all of them), and its sidechain source (a track id or <c>bus:N</c>; null: none). A plugin's opaque state lives in
///     the project's <c>Plugin States</c> folder. A MIDI FX slot (<c>ChannelStrip.midiEffects</c>) has the same shape,
///     never a sidechain. Phase 6: <c>file</c> is the file the effect loads (an impulse response for
///     <c>anode.space</c>; <c>loadInsertFile</c>), relative to the project when it is inside it (null: none).
///     Phase 7 (docs/08-phase-7.md): <c>runtimeParameters</c> is the parameter list of a slot whose plugin defines its
///     parameters at run time (Scripter's <c>PluginParameters</c>; the host reports it after each compile, and the
///     server stores it here): <c>parameters</c> holds their values, and they automate
///     (<c>midiEffect:{slotId}:{parameterId}</c>) and map to Smart Controls as any parameter (null: the plugin's
///     catalogue list applies). <c>source</c> is a Scripter slot's script (<c>setScript</c>; null on every other
///     slot). <c>mastering</c> (Phase 11, docs/12-phase-11.md) is a Mastering Assistant's analysis (null on every other
///     insert, and on an assistant not analysed yet); its settings are its parameters
///     (<see cref="MasteringParameters" />).
/// </summary>
public sealed record Insert(
    string Id,
    string PluginId,
    string Name,
    bool Bypassed,
    IReadOnlyDictionary<string, double> Parameters,
    string? Sidechain,
    string? File = null,
    IReadOnlyList<ParameterInfo>? RuntimeParameters = null,
    string? Source = null,
    MasteringAnalysis? Mastering = null
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
///     parameter the control maps). Phase 10 (docs/11-phase-10.md) adds the panners' parameters
///     (<see cref="PannerTargets" />): <c>surround:angle</c> (degrees), <c>surround:diversity</c> (0…1),
///     <c>surround:elevation</c> (degrees), <c>surround:centre</c> and <c>surround:lfe</c> (dB),
///     <c>surround:spread</c> (degrees), <c>object:azimuth</c> and <c>object:elevation</c> (degrees),
///     <c>object:distance</c> and <c>object:size</c> (0…1); they play whatever the strip's panner mode (only the mode's
///     panner sounds). Points are sorted by tick.
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
///     How a mono signal's level follows its pan: what the centre is, relative to a hard pan. Stereo signals are balanced
///     (the far side falls linearly) whatever the law; dual pan takes the law's shape with a hard pan at unity.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PanLaw>))]
public enum PanLaw {
    /// <summary>The centre as loud as a hard pan: the far side falls linearly, the near side stays at unity.</summary>
    [JsonStringEnumMemberName("zeroDb")]
    ZeroDb,

    /// <summary>Constant power: a hard pan at unity, the centre −3 dB.</summary>
    [JsonStringEnumMemberName("minus3Db")]
    Minus3Db,

    /// <summary>Constant power with the centre at unity: a hard pan +3 dB (Logic's default, and Anode's).</summary>
    [JsonStringEnumMemberName("minus3DbCompensated")]
    Minus3DbCompensated,

    /// <summary>Between constant power and linear: a hard pan at unity, the centre −4.5 dB.</summary>
    [JsonStringEnumMemberName("minus4Point5Db")]
    Minus4Point5Db,

    /// <summary>Linear: a hard pan at unity, the centre −6 dB.</summary>
    [JsonStringEnumMemberName("minus6Db")]
    Minus6Db
}

/// <summary>What a strip's meter shows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeterType>))]
public enum MeterType {
    /// <summary>Sample peaks.</summary>
    [JsonStringEnumMemberName("peak")]
    Peak,

    /// <summary>True RMS over 300 ms: a sine reads 3 dB under its peak.</summary>
    [JsonStringEnumMemberName("rms")]
    Rms,

    /// <summary>K-20: RMS over 600 ms, a sine reading its peak (AES17), 0 on the scale at −20 dBFS.</summary>
    [JsonStringEnumMemberName("k20")]
    K20,

    /// <summary>K-14: as K-20, 0 on the scale at −14 dBFS.</summary>
    [JsonStringEnumMemberName("k14")]
    K14,

    /// <summary>K-12: as K-20, 0 on the scale at −12 dBFS.</summary>
    [JsonStringEnumMemberName("k12")]
    K12,

    /// <summary>Momentary loudness (BS.1770, 400 ms). A mono signal before the pan counts as one channel.</summary>
    [JsonStringEnumMemberName("lufs")]
    Lufs
}

/// <summary>Where a strip's meter reads the signal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeterTap>))]
public enum MeterTap {
    /// <summary>The source after trim, before the inserts.</summary>
    [JsonStringEnumMemberName("input")]
    Input,

    /// <summary>After the inserts, before the fader.</summary>
    [JsonStringEnumMemberName("preFader")]
    PreFader,

    /// <summary>After fader and pan, what goes to the output.</summary>
    [JsonStringEnumMemberName("postFader")]
    PostFader
}

/// <summary>A strip's meter: its type and tap point (a new strip's: peak, post-fader).</summary>
public sealed record StripMeter(MeterType Type, MeterTap Tap) {
    public static StripMeter Default { get; } = new(MeterType.Peak, MeterTap.PostFader);
}

/// <summary>What a strip's solo does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SoloMode>))]
public enum SoloMode {
    /// <summary>Solo in place: everything else falls silent, in the mix and in a bounce.</summary>
    [JsonStringEnumMemberName("inPlace")]
    InPlace,

    /// <summary>After-fader listen: the mix stays as it is; the monitors hear the soloed strips after fader and pan.</summary>
    [JsonStringEnumMemberName("afl")]
    Afl,

    /// <summary>Pre-fader listen: the mix stays as it is; the monitors hear the soloed strips before their faders.</summary>
    [JsonStringEnumMemberName("pfl")]
    Pfl
}

/// <summary>
///     Plugin delay compensation, and low-latency mode: while it is on, plugins on the path of record-armed or
///     monitored tracks whose latency exceeds <c>lowLatencyLimitMs</c> are bypassed. <c>panLaw</c> is the project's pan
///     law, <c>soloMode</c> what solo does.
/// </summary>
public sealed record MixerSettings(
    PdcMode Pdc,
    bool LowLatency,
    double LowLatencyLimitMs,
    PanLaw PanLaw,
    SoloMode SoloMode
);

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

/// <summary>An insert a plugin window shows; <c>trackId</c> null is the master.</summary>
public sealed record InsertDisplayTarget(string? TrackId, string InsertId);

/// <summary>The inserts whose live display (a spectrum, a scope, band gains) an editor shows now.</summary>
public sealed record InsertDisplayRequest(IReadOnlyList<InsertDisplayTarget> Inserts);

/// <summary>
///     A Scripter factory script (Phase 7, <c>GET /library/scripts</c>): its stable id (<c>factory.…</c>), name, a
///     line about what it does, and its source, which the Script Editor loads as a slot's script.
/// </summary>
public sealed record ScriptPreset(string Id, string Name, string Description, string Source);
