namespace Anode.Audio.Midi;

/// <summary>
///     A MIDI backend that shows only the virtual ports this process publishes (uids <c>{prefix}:…</c>, Anode's "Anode
///     In" and "Anode Out"), hiding every other endpoint: <c>anode-host --virtual-midi</c> on a platform with virtual
///     ports (CoreMIDI), so end-to-end tests have ports on any machine that other processes (<c>anode-midiprobe</c>) can
///     reach, without touching the machine's real devices. Input from hidden sources is dropped; sources are renumbered
///     to the shown list. One thread calls it, as <see cref="IMidiBackend" /> says; <see cref="Read" /> does not
///     allocate.
/// </summary>
public sealed class OwnPortsMidiBackend(IMidiBackend inner, string prefix) : IMidiBackend {
    readonly string own = prefix + ":";

    /// <summary>The shown index of each of the inner backend's ports (−1 hidden), as last listed.</summary>
    int[] shown = [];

    public bool SchedulesOutput => inner.SchedulesOutput;

    /// <summary>The inner count: a change in any endpoint asks for a relisting, which then filters.</summary>
    public int PortCount() => inner.PortCount();

    public IReadOnlyList<MidiPortInfo> Ports() {
        var all = inner.Ports();
        var map = new int[all.Count];
        var ports = new List<MidiPortInfo>();
        for (var i = 0; i < all.Count; i++) {
            if (all[i].Uid.StartsWith(own, StringComparison.Ordinal)) {
                map[i] = ports.Count;
                ports.Add(all[i]);
            } else {
                map[i] = -1;
            }
        }

        Volatile.Write(ref shown, map);
        return ports;
    }

    public bool Open() => inner.Open();

    public int Read(Span<MidiInputMessage> into) {
        var read = inner.Read(into);
        var map = Volatile.Read(ref shown);
        var kept = 0;
        for (var i = 0; i < read; i++) {
            var m = into[i];
            if ((uint)m.Source < (uint)map.Length && map[m.Source] >= 0) {
                into[kept++] = m with { Source = map[m.Source] };
            }
        }

        return kept;
    }

    public int ReadSystemExclusive(Span<byte> buffer, out long timeNs, out int source) {
        while (true) {
            var length = inner.ReadSystemExclusive(buffer, out timeNs, out source);
            if (length <= 0) {
                return length;
            }

            var map = Volatile.Read(ref shown);
            if ((uint)source < (uint)map.Length && map[source] >= 0) {
                source = map[source];
                return length;
            }
        }
    }

    public bool Send(string uid, ReadOnlySpan<byte> message, long timeNs) =>
        uid.StartsWith(own, StringComparison.Ordinal) && inner.Send(uid, message, timeNs);

    public int OpenVirtual(string name, string uid, bool isInput) => inner.OpenVirtual(name, uid, isInput);

    public bool CloseVirtual(string uid) => inner.CloseVirtual(uid);

    public void Dispose() => inner.Dispose();
}
