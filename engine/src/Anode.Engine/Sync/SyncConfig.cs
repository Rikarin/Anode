using Anode.Audio.Native;
using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>
///     What the audio callback needs of the project's sync settings (<see cref="SetSyncMessage" />), published by the
///     control thread as one immutable value (<see cref="AudioEngine.SetSync" />): which streams it generates, the MTC
///     rate and offset, and the Link peer it aligns to (when the source is Link). The destinations themselves (ports and
///     delays) are the MIDI thread's (<see cref="Midi.MidiHub.SetSyncDestinations" />).
/// </summary>
public sealed class SyncConfig(
    EngineSyncSource source,
    bool clockOut,
    MtcEncoder? mtc,
    double startOffsetSeconds,
    bool linkStartStop,
    NativeLink? link,
    long linkOffsetMicros
) {
    /// <summary>Internal sync, nothing sent.</summary>
    public static SyncConfig Internal { get; } = new(EngineSyncSource.Internal, false, null, 0, false, null, 0);

    public EngineSyncSource Source { get; } = source;

    /// <summary>Some destination takes MIDI Clock.</summary>
    public bool ClockOut { get; } = clockOut;

    /// <summary>The MTC generator when some destination takes MTC.</summary>
    public MtcEncoder? Mtc { get; } = mtc;

    /// <summary>Seconds of timecode at the project start.</summary>
    public double StartOffsetSeconds { get; } = startOffsetSeconds;

    public bool LinkStartStop { get; } = linkStartStop;

    /// <summary>The Link peer (source Link only).</summary>
    public NativeLink? Link { get; } = link;

    /// <summary>Link's clock less the engine's (both in microseconds): Link time = host ns / 1000 + this.</summary>
    public long LinkOffsetMicros { get; } = linkOffsetMicros;
}

/// <summary>Which sync message kind an <see cref="OutgoingSync" /> belongs to (its destinations are those that take it).</summary>
public enum SyncMessageKind : byte {
    Clock,
    Mtc
}

/// <summary>
///     A sync message the audio callback generated for the MIDI thread to send: its host time (the frame's output time,
///     before a destination's delay), its kind and its bytes (1–3, or a full-frame SysEx when
///     <see cref="Length" /> is 10: <see cref="B0" /> … <see cref="B3" /> are then its hours (with the rate), minutes,
///     seconds and frames).
/// </summary>
public readonly record struct OutgoingSync(
    long TimeNs,
    SyncMessageKind Kind,
    byte Length,
    byte B0,
    byte B1,
    byte B2,
    byte B3 = 0
);
