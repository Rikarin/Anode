namespace Anode.Audio.Midi;

/// <summary>A MIDI endpoint as the OS reports it. <see cref="Uid" /> is stable across runs.</summary>
public sealed record MidiPortInfo(string Uid, string Name, bool IsInput);

/// <summary>
///     A short MIDI message as it arrived: its OS timestamp in nanoseconds (the <see cref="Realtime.NowNanoseconds" />
///     clock), its bytes and the input that sent it (<see cref="Source" />: an index into
///     <see cref="IMidiBackend.Ports" /> as last listed, −1 when unknown).
/// </summary>
public readonly record struct MidiInputMessage(long TimeNs, byte Status, byte Data1, byte Data2, int Source);

/// <summary>
///     MIDI input and output: the OS through <see cref="Native.NativeMidiBackend" />, or a fake in tests. One thread (the
///     engine's MIDI thread) calls it.
/// </summary>
public interface IMidiBackend : IDisposable {
    /// <summary>How many endpoints the OS reports, without listing them (cheap; polled for hotplug).</summary>
    int PortCount();

    /// <summary>The endpoints, inputs and outputs, in the order <see cref="MidiInputMessage.Source" /> numbers them.</summary>
    IReadOnlyList<MidiPortInfo> Ports();

    /// <summary>Connects every input (again: picks up devices plugged in since). False when MIDI is unavailable.</summary>
    bool Open();

    /// <summary>Drains queued input into <paramref name="into" />; returns how many messages it wrote.</summary>
    int Read(Span<MidiInputMessage> into);

    /// <summary>
    ///     Sends a short message to the output <paramref name="uid" />, to be heard at <paramref name="timeNs" />
    ///     (0: now). False when it could not.
    /// </summary>
    bool Send(string uid, ReadOnlySpan<byte> message, long timeNs);

    /// <summary>
    ///     True when <see cref="Send" /> schedules a future time itself (CoreMIDI); otherwise the caller holds a message
    ///     until it is due.
    /// </summary>
    bool SchedulesOutput { get; }
}
