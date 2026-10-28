namespace Anode.Audio.Midi;

/// <summary>
///     A MIDI backend with one virtual input (which never sends) and one virtual output (which accepts and counts what
///     it is sent), for machines without MIDI ports: end-to-end tests and demos (<c>anode-host --virtual-midi</c>).
///     Nothing loops back, so an external MIDI track sending to it cannot feed itself.
/// </summary>
public sealed class VirtualMidiBackend : IMidiBackend {
    public const string InputUid = "anode-virtual-in";
    public const string OutputUid = "anode-virtual-out";

    static readonly MidiPortInfo[] VirtualPorts = [
        new(InputUid, "Anode Virtual In", true),
        new(OutputUid, "Anode Virtual Out", false)
    ];

    long sent;

    /// <summary>Messages sent to the virtual output so far.</summary>
    public long Sent => Interlocked.Read(ref sent);

    public int PortCount() => VirtualPorts.Length;

    public IReadOnlyList<MidiPortInfo> Ports() => VirtualPorts;

    public bool Open() => true;

    public int Read(Span<MidiInputMessage> into) => 0;

    public bool Send(string uid, ReadOnlySpan<byte> message, long timeNs) {
        if (uid != OutputUid) {
            return false;
        }

        Interlocked.Increment(ref sent);
        return true;
    }

    public bool SchedulesOutput => false;

    public void Dispose() { }
}
