using Anode.Audio.Midi;
using System.Runtime.InteropServices;

namespace Anode.Audio.Native;

/// <summary>
///     MIDI through the <c>cth_audio</c> shim (CoreMIDI on macOS, WinMM on Windows, the ALSA sequencer on Linux; ported
///     from Cathode's <c>NativeMidi</c>). Input from every source is timestamped and queued natively; <see cref="Read" />
///     drains it. Output is timestamped: CoreMIDI schedules it, the other backends send at once.
/// </summary>
public sealed unsafe partial class NativeMidiBackend : IMidiBackend {
    const string Library = "cth_audio";
    const int ReadBatch = 256;
    bool opened;

    /// <summary>True when the native library is built and has a MIDI backend for this platform.</summary>
    public static bool IsAvailable => NativeAudio.IsLibraryPresent && NativeAudio.Backend != "none";

    public bool SchedulesOutput => OperatingSystem.IsMacOS();

    public int PortCount() => PortCountNative();

    public IReadOnlyList<MidiPortInfo> Ports() {
        var ports = new List<MidiPortInfo>();
        var count = PortCount();
        for (var i = 0; i < count; i++) {
            PortNative port;
            if (PortInfo(i, &port) == 0) {
                ports.Add(new(Text(port.Uid, 64), Text(port.Name, 256), port.IsInput != 0));
            }
        }

        return ports;
    }

    public bool Open() {
        opened = OpenNative() == 0;
        return opened;
    }

    public int Read(Span<MidiInputMessage> into) {
        var buffer = stackalloc EventNative[ReadBatch];
        var total = 0;
        while (total < into.Length) {
            var read = ReadNative(buffer, Math.Min(ReadBatch, into.Length - total));
            if (read <= 0) {
                break;
            }

            for (var i = 0; i < read; i++) {
                ref var e = ref buffer[i];
                into[total++] = new((long)e.TimeNs, e.Data[0], e.Data[1], e.Data[2], e.Source);
            }
        }

        return total;
    }

    // False positive: `fixed` pins the span and reads it; it does not overwrite the parameter.
#pragma warning disable SK2142
    public bool Send(string uid, ReadOnlySpan<byte> message, long timeNs) {
#pragma warning restore SK2142
        fixed (byte* data = message) {
            return SendAtNative(uid, data, message.Length, (ulong)Math.Max(0, timeNs)) >= 0;
        }
    }

    /// <summary>The clock MIDI timestamps are on (the monotonic clock <see cref="Realtime.NowNanoseconds" /> reads).</summary>
    public static long NowNanoseconds() => (long)NowNative();

    public void Dispose() {
        if (opened) {
            CloseNative();
            opened = false;
        }
    }

    static string Text(byte* text, int size) =>
        Marshal.PtrToStringUTF8((nint)text, Math.Max(0, IndexOfZero(text, size))) ?? string.Empty;

    static int IndexOfZero(byte* text, int size) {
        for (var i = 0; i < size; i++) {
            if (text[i] == 0) {
                return i;
            }
        }

        return size;
    }

    [LibraryImport(Library, EntryPoint = "cth_midi_port_count")]
    private static partial int PortCountNative();

    [LibraryImport(Library, EntryPoint = "cth_midi_port_info")]
    private static partial int PortInfo(int index, PortNative* port);

    [LibraryImport(Library, EntryPoint = "cth_midi_open")]
    private static partial int OpenNative();

    [LibraryImport(Library, EntryPoint = "cth_midi_read")]
    private static partial int ReadNative(EventNative* events, int max);

    [LibraryImport(Library, EntryPoint = "cth_midi_send_at", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SendAtNative(string? destinationUid, byte* data, int length, ulong timeNs);

    [LibraryImport(Library, EntryPoint = "cth_midi_close")]
    private static partial void CloseNative();

    [LibraryImport(Library, EntryPoint = "cth_midi_now_ns")]
    private static partial ulong NowNative();

    [StructLayout(LayoutKind.Sequential)]
    struct PortNative {
        public fixed byte Uid[64];
        public fixed byte Name[256];
        public int IsInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EventNative {
        public ulong TimeNs;
        public fixed byte Data[3];
        public byte Length;
        public int Source;
    }
}
