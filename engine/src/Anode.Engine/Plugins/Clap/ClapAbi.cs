using System.Runtime.InteropServices;

namespace Anode.Engine.Plugins.Clap;

// The CLAP C ABI (https://github.com/free-audio/clap, MIT), version 1.2.9 as pinned in
// engine/native/an_testclap/CMakeLists.txt: the structs the host reads and writes, with C bools as bytes and function
// pointers as unmanaged function pointers. Only what an instrument host needs is declared.
#pragma warning disable CA1051, SA1307 // ABI mirrors: public fields named as in C

[StructLayout(LayoutKind.Sequential)]
struct ClapVersion {
    public uint Major;
    public uint Minor;
    public uint Revision;

    public static ClapVersion Current => new() { Major = 1, Minor = 2, Revision = 9 };
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginEntry {
    public ClapVersion Version;
    public delegate* unmanaged<byte*, byte> Init;
    public delegate* unmanaged<void> Deinit;
    public delegate* unmanaged<byte*, void*> GetFactory;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginFactory {
    public delegate* unmanaged<ClapPluginFactory*, uint> GetPluginCount;
    public delegate* unmanaged<ClapPluginFactory*, uint, ClapPluginDescriptor*> GetPluginDescriptor;
    public delegate* unmanaged<ClapPluginFactory*, ClapHost*, byte*, ClapPlugin*> CreatePlugin;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginDescriptor {
    public ClapVersion Version;
    public byte* Id;
    public byte* Name;
    public byte* Vendor;
    public byte* Url;
    public byte* ManualUrl;
    public byte* SupportUrl;
    public byte* PluginVersion;
    public byte* Description;
    public byte** Features;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHost {
    public ClapVersion Version;
    public void* HostData;
    public byte* Name;
    public byte* Vendor;
    public byte* Url;
    public byte* HostVersion;
    public delegate* unmanaged<ClapHost*, byte*, void*> GetExtension;
    public delegate* unmanaged<ClapHost*, void> RequestRestart;
    public delegate* unmanaged<ClapHost*, void> RequestProcess;
    public delegate* unmanaged<ClapHost*, void> RequestCallback;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPlugin {
    public ClapPluginDescriptor* Desc;
    public void* PluginData;
    public delegate* unmanaged<ClapPlugin*, byte> Init;
    public delegate* unmanaged<ClapPlugin*, void> Destroy;
    public delegate* unmanaged<ClapPlugin*, double, uint, uint, byte> Activate;
    public delegate* unmanaged<ClapPlugin*, void> Deactivate;
    public delegate* unmanaged<ClapPlugin*, byte> StartProcessing;
    public delegate* unmanaged<ClapPlugin*, void> StopProcessing;
    public delegate* unmanaged<ClapPlugin*, void> Reset;
    public delegate* unmanaged<ClapPlugin*, ClapProcess*, int> Process;
    public delegate* unmanaged<ClapPlugin*, byte*, void*> GetExtension;
    public delegate* unmanaged<ClapPlugin*, void> OnMainThread;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapAudioBuffer {
    public float** Data32;
    public double** Data64;
    public uint ChannelCount;
    public uint Latency;
    public ulong ConstantMask;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapProcess {
    public long SteadyTime;
    public uint FramesCount;
    public void* Transport;
    public ClapAudioBuffer* AudioInputs;
    public ClapAudioBuffer* AudioOutputs;
    public uint AudioInputsCount;
    public uint AudioOutputsCount;
    public ClapInputEvents* InEvents;
    public ClapOutputEvents* OutEvents;
}

[StructLayout(LayoutKind.Sequential)]
struct ClapEventHeader {
    public uint Size;
    public uint Time;
    public ushort SpaceId;
    public ushort Type;
    public uint Flags;
}

static class ClapEventType {
    public const ushort NoteOn = 0;
    public const ushort NoteOff = 1;
    public const ushort ParamValue = 5;
    public const ushort Midi = 10;
    public const uint IsLive = 1;
}

[StructLayout(LayoutKind.Sequential)]
struct ClapEventNote {
    public ClapEventHeader Header;
    public int NoteId;
    public short PortIndex;
    public short Channel;
    public short Key;
    public double Velocity;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapEventParamValue {
    public ClapEventHeader Header;
    public uint ParamId;
    public void* Cookie;
    public int NoteId;
    public short PortIndex;
    public short Channel;
    public short Key;
    public double Value;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapEventMidi {
    public ClapEventHeader Header;
    public ushort PortIndex;
    public fixed byte Data[3];
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapInputEvents {
    public void* Ctx;
    public delegate* unmanaged<ClapInputEvents*, uint> Size;
    public delegate* unmanaged<ClapInputEvents*, uint, ClapEventHeader*> Get;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapOutputEvents {
    public void* Ctx;
    public delegate* unmanaged<ClapOutputEvents*, ClapEventHeader*, byte> TryPush;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapIStream {
    public void* Ctx;
    public delegate* unmanaged<ClapIStream*, void*, ulong, long> Read;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapOStream {
    public void* Ctx;
    public delegate* unmanaged<ClapOStream*, void*, ulong, long> Write;
}

// ---- Plugin extensions ----

static class ClapParamFlags {
    public const uint IsStepped = 1 << 0;
    public const uint IsHidden = 1 << 2;
    public const uint IsReadonly = 1 << 3;
    public const uint IsEnum = 1 << 16;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapParamInfo {
    public uint Id;
    public uint Flags;
    public void* Cookie;
    public fixed byte Name[256];
    public fixed byte Module[1024];
    public double MinValue;
    public double MaxValue;
    public double DefaultValue;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginParams {
    public delegate* unmanaged<ClapPlugin*, uint> Count;
    public delegate* unmanaged<ClapPlugin*, uint, ClapParamInfo*, byte> GetInfo;
    public delegate* unmanaged<ClapPlugin*, uint, double*, byte> GetValue;
    public delegate* unmanaged<ClapPlugin*, uint, double, byte*, uint, byte> ValueToText;
    public delegate* unmanaged<ClapPlugin*, uint, byte*, double*, byte> TextToValue;
    public delegate* unmanaged<ClapPlugin*, ClapInputEvents*, ClapOutputEvents*, void> Flush;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginState {
    public delegate* unmanaged<ClapPlugin*, ClapOStream*, byte> Save;
    public delegate* unmanaged<ClapPlugin*, ClapIStream*, byte> Load;
}

static class ClapNoteDialect {
    public const uint Clap = 1 << 0;
    public const uint Midi = 1 << 1;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapNotePortInfo {
    public uint Id;
    public uint SupportedDialects;
    public uint PreferredDialect;
    public fixed byte Name[256];
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginNotePorts {
    public delegate* unmanaged<ClapPlugin*, byte, uint> Count;
    public delegate* unmanaged<ClapPlugin*, uint, byte, ClapNotePortInfo*, byte> Get;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapAudioPortInfo {
    public uint Id;
    public fixed byte Name[256];
    public uint Flags;
    public uint ChannelCount;
    public byte* PortType;
    public uint InPlacePair;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginAudioPorts {
    public delegate* unmanaged<ClapPlugin*, byte, uint> Count;
    public delegate* unmanaged<ClapPlugin*, uint, byte, ClapAudioPortInfo*, byte> Get;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapPluginLatency {
    public delegate* unmanaged<ClapPlugin*, uint> Get;
}

// ---- Host extensions ----

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostParams {
    public delegate* unmanaged<ClapHost*, uint, void> Rescan;
    public delegate* unmanaged<ClapHost*, uint, uint, void> Clear;
    public delegate* unmanaged<ClapHost*, void> RequestFlush;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostState {
    public delegate* unmanaged<ClapHost*, void> MarkDirty;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostNotePorts {
    public delegate* unmanaged<ClapHost*, uint> SupportedDialects;
    public delegate* unmanaged<ClapHost*, uint, void> Rescan;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostAudioPorts {
    public delegate* unmanaged<ClapHost*, uint, byte> IsRescanFlagSupported;
    public delegate* unmanaged<ClapHost*, uint, void> Rescan;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostLatency {
    public delegate* unmanaged<ClapHost*, void> Changed;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostThreadCheck {
    public delegate* unmanaged<ClapHost*, byte> IsMainThread;
    public delegate* unmanaged<ClapHost*, byte> IsAudioThread;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct ClapHostLog {
    public delegate* unmanaged<ClapHost*, int, byte*, void> Log;
}

#pragma warning restore CA1051, SA1307
