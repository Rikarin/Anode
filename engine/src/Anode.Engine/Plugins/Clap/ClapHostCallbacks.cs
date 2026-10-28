using System.Runtime.InteropServices;

namespace Anode.Engine.Plugins.Clap;

/// <summary>
///     What the host offers a CLAP plugin: the <c>clap_host</c> callbacks and the host extensions instruments need
///     (params, state, note ports, audio ports, latency, thread check, log). The extension tables are static native
///     memory shared by every instance; <c>host_data</c> names the <see cref="ClapInstrument" />. Callbacks a plugin may
///     make on the audio thread (thread check, request_callback, request_flush, log) neither allocate nor lock.
/// </summary>
static unsafe class ClapHostCallbacks {
    static readonly ClapHostParams* Params;
    static readonly ClapHostState* State;
    static readonly ClapHostNotePorts* NotePorts;
    static readonly ClapHostAudioPorts* AudioPorts;
    static readonly ClapHostLatency* Latency;
    static readonly ClapHostThreadCheck* ThreadCheck;
    static readonly ClapHostLog* Log;
    static readonly byte* Name = ClapModule.Utf8("Anode");
    static readonly byte* Vendor = ClapModule.Utf8("Anode contributors");
    static readonly byte* Url = ClapModule.Utf8("https://github.com/anode-daw/anode");

    static readonly byte* Version =
        ClapModule.Utf8(typeof(ClapHostCallbacks).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    static ClapHostCallbacks() {
        Params = Alloc<ClapHostParams>();
        Params->Rescan = &ParamsRescan;
        Params->Clear = &ParamsClear;
        Params->RequestFlush = &ParamsRequestFlush;
        State = Alloc<ClapHostState>();
        State->MarkDirty = &StateMarkDirty;
        NotePorts = Alloc<ClapHostNotePorts>();
        NotePorts->SupportedDialects = &NotePortsDialects;
        NotePorts->Rescan = &NotePortsRescan;
        AudioPorts = Alloc<ClapHostAudioPorts>();
        AudioPorts->IsRescanFlagSupported = &AudioPortsFlagSupported;
        AudioPorts->Rescan = &AudioPortsRescan;
        Latency = Alloc<ClapHostLatency>();
        Latency->Changed = &LatencyChanged;
        ThreadCheck = Alloc<ClapHostThreadCheck>();
        ThreadCheck->IsMainThread = &IsMainThread;
        ThreadCheck->IsAudioThread = &IsAudioThread;
        Log = Alloc<ClapHostLog>();
        Log->Log = &LogMessage;
    }

    static T* Alloc<T>() where T : unmanaged => (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));

    /// <summary>A <c>clap_host</c> for one instance (free it with <see cref="NativeMemory.Free" />).</summary>
    public static ClapHost* Create(GCHandle owner) {
        var host = Alloc<ClapHost>();
        host->Version = ClapVersion.Current;
        host->HostData = (void*)GCHandle.ToIntPtr(owner);
        host->Name = Name;
        host->Vendor = Vendor;
        host->Url = Url;
        host->HostVersion = Version;
        host->GetExtension = &GetExtension;
        host->RequestRestart = &RequestRestart;
        host->RequestProcess = &RequestProcess;
        host->RequestCallback = &RequestCallback;
        return host;
    }

    static ClapInstrument? Owner(ClapHost* host) =>
        host is null || host->HostData is null
            ? null
            : GCHandle.FromIntPtr((nint)host->HostData).Target as ClapInstrument;

    static bool Is(byte* id, ReadOnlySpan<byte> name) =>
        MemoryMarshal.CreateReadOnlySpanFromNullTerminated(id).SequenceEqual(name);

    [UnmanagedCallersOnly]
    static void* GetExtension(ClapHost* host, byte* id) {
        if (id is null) {
            return null;
        }

        if (Is(id, "clap.params"u8)) {
            return Params;
        }

        if (Is(id, "clap.state"u8)) {
            return State;
        }

        if (Is(id, "clap.note-ports"u8)) {
            return NotePorts;
        }

        if (Is(id, "clap.audio-ports"u8)) {
            return AudioPorts;
        }

        if (Is(id, "clap.latency"u8)) {
            return Latency;
        }

        if (Is(id, "clap.thread-check"u8)) {
            return ThreadCheck;
        }

        return Is(id, "clap.log"u8) ? Log : null;
    }

    [UnmanagedCallersOnly]
    static void RequestRestart(ClapHost* host) => Owner(host)?.Flag(ClapInstrument.Requests.Restart);

    [UnmanagedCallersOnly]
    static void RequestProcess(ClapHost* host) { } // instruments always process

    [UnmanagedCallersOnly]
    static void RequestCallback(ClapHost* host) => Owner(host)?.Flag(ClapInstrument.Requests.Callback);

    [UnmanagedCallersOnly]
    static void ParamsRescan(ClapHost* host, uint flags) => Owner(host)?.Flag(ClapInstrument.Requests.Rescan);

    [UnmanagedCallersOnly]
    static void ParamsClear(ClapHost* host, uint id, uint flags) { } // no automation or modulation yet

    [UnmanagedCallersOnly]
    static void ParamsRequestFlush(ClapHost* host) { } // the instrument processes every buffer, which flushes

    [UnmanagedCallersOnly]
    static void StateMarkDirty(ClapHost* host) => Owner(host)?.Flag(ClapInstrument.Requests.Dirty);

    [UnmanagedCallersOnly]
    static uint NotePortsDialects(ClapHost* host) => ClapNoteDialect.Clap | ClapNoteDialect.Midi;

    [UnmanagedCallersOnly]
    static void NotePortsRescan(ClapHost* host, uint flags) { }

    [UnmanagedCallersOnly]
    static byte AudioPortsFlagSupported(ClapHost* host, uint flag) => 0;

    [UnmanagedCallersOnly]
    static void AudioPortsRescan(ClapHost* host, uint flags) { }

    [UnmanagedCallersOnly]
    static void LatencyChanged(ClapHost* host) => Owner(host)?.Flag(ClapInstrument.Requests.Latency);

    [UnmanagedCallersOnly]
    static byte IsMainThread(ClapHost* host) => PluginThread.Shared.IsCurrent ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    static byte IsAudioThread(ClapHost* host) => AudioThread.IsCurrent ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    static void LogMessage(ClapHost* host, int severity, byte* message) {
        if (Owner(host) is not { } owner) {
            return;
        }

        if (AudioThread.IsCurrent) {
            owner.Flag(ClapInstrument.Requests.AudioThreadLog); // formatting a string would allocate
            return;
        }

        owner.Report(severity, ClapModule.Text(message));
    }
}
