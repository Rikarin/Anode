using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>What the transport follows (docs/09-phase-8.md, "Sync"): one source at a time.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncSource>))]
public enum SyncSource {
    /// <summary>Anode's own clock and tempo map.</summary>
    [JsonStringEnumMemberName("internal")]
    Internal,

    /// <summary>
    ///     External MIDI Clock on <c>clockInPort</c>: Anode follows its tempo (a smoothed estimate, not written into the
    ///     tempo map) and its Start, Stop, Continue and Song Position Pointer.
    /// </summary>
    [JsonStringEnumMemberName("midiClock")]
    MidiClock,

    /// <summary>
    ///     External MIDI Time Code on <c>mtcInPort</c>: Anode chases the timecode (less <c>startOffsetSeconds</c>) and
    ///     stops when it stops.
    /// </summary>
    [JsonStringEnumMemberName("mtc")]
    Mtc,

    /// <summary>
    ///     An Ableton Link session: tempo both ways, the beat phase aligned at a bar, and start/stop when
    ///     <c>linkStartStop</c>.
    /// </summary>
    [JsonStringEnumMemberName("link")]
    Link
}

/// <summary>An MTC frame rate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MtcFrameRate>))]
public enum MtcFrameRate {
    [JsonStringEnumMemberName("fps24")]
    Fps24,

    [JsonStringEnumMemberName("fps25")]
    Fps25,

    /// <summary>29.97 drop frame.</summary>
    [JsonStringEnumMemberName("fps2997Drop")]
    Fps2997Drop,

    [JsonStringEnumMemberName("fps30")]
    Fps30
}

/// <summary>
///     A MIDI output (a <c>MidiPort.id</c>) that Anode sends sync to: MIDI Clock (24 ppq, Start, Stop, Continue, Song
///     Position Pointer on locate) and/or MTC (quarter frames while playing, a full frame on locate), each message
///     <c>delayMs</c> later than its sample (−50…50; negative sends early, within the output latency).
/// </summary>
public sealed record MidiSyncDestination(string Port, bool Clock, bool Mtc, double DelayMs);

/// <summary>
///     The project's synchronisation settings (<c>ProjectSettings.sync</c>, as in Logic): the source the transport
///     follows; the destinations Anode sends MIDI Clock and MTC to; the MTC frame rate (sent and expected);
///     <c>startOffsetSeconds</c>, the timecode of the project start in seconds (0…86399; MTC out starts there, MTC in
///     chases relative to it); <c>linkStartStop</c>, whether play and stop follow and lead the Link session (when the
///     source is <c>link</c>); and the ports clock and MTC are received on (null: any input).
/// </summary>
public sealed record SyncSettings(
    SyncSource Source,
    IReadOnlyList<MidiSyncDestination> Destinations,
    MtcFrameRate FrameRate,
    double StartOffsetSeconds,
    bool LinkStartStop,
    string? ClockInPort,
    string? MtcInPort
) {
    /// <summary>A new project's: internal, nothing sent, 25 fps, from timecode 0, Link start/stop off, any input.</summary>
    public static SyncSettings Default { get; } =
        new(SyncSource.Internal, [], MtcFrameRate.Fps25, 0, false, null, null);
}

/// <summary>
///     The live sync state (<c>GET /project/sync</c>, the <c>sync</c> event): the source; <c>locked</c>, whether the
///     transport is following it (always true for internal; for MIDI Clock and MTC, while their messages arrive in
///     time; for Link, while the session runs, peers or not); <c>externalTempo</c>, the tempo followed (MIDI Clock's
///     estimate or the Link session's; null otherwise); and the number of other Link peers.
/// </summary>
public sealed record SyncState(SyncSource Source, bool Locked, double? ExternalTempo, int LinkPeers);
