using System.Runtime.InteropServices;

namespace Anode.Plugins.Sandbox;

/// <summary>
///     The header of a sandbox's shared segment, written by the host before it starts the sandbox (except the
///     sandbox-written fields at 128).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct SandboxHeader {
    public const int MagicValue = 0x414E5047; // "ANPG"
    public const int CurrentVersion = 1;

    [FieldOffset(0)]
    public int Magic;

    [FieldOffset(4)]
    public int Version;

    [FieldOffset(8)]
    public int MaxSlots;

    [FieldOffset(12)]
    public int MaxFrames;

    [FieldOffset(16)]
    public int HostPid;

    /// <summary>Host → sandbox: stop the audio thread.</summary>
    [FieldOffset(20)]
    public int Stop;

    /// <summary>The device period, for the audio thread's real-time policy (0: 64 frames at 48 kHz).</summary>
    [FieldOffset(24)]
    public long PeriodNs;

    /// <summary>Bumped by the host when <see cref="PeriodNs" /> or the workgroup service changes.</summary>
    [FieldOffset(32)]
    public int ConfigGeneration;

    /// <summary>1 when the host publishes the device's workgroup (as <see cref="SharedLayout.WorkgroupService" />).</summary>
    [FieldOffset(36)]
    public int Workgroup;

    // Sandbox-written.
    [FieldOffset(128)]
    public int SandboxPid;

    [FieldOffset(132)]
    public int AudioReady;

    /// <summary>The audio thread's real-time promotion: 0 granted, negative refused.</summary>
    [FieldOffset(136)]
    public int RtStatus;

    /// <summary>The audio thread's workgroup join: 0 joined, 1 not asked to, negative failed.</summary>
    [FieldOffset(140)]
    public int WorkgroupStatus;

    /// <summary>Audio-thread wakes so far.</summary>
    [FieldOffset(144)]
    public long Wakes;
}

/// <summary>
///     One slot's (one plugin instance's) handshake. The done word sits alone on its 128-byte line (Apple silicon's
///     line size), the host-written and the sandbox-written fields on lines of their own, so the two sides never write
///     the same line.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 512)]
internal struct SlotControl {
    public const int FlagReset = 1;
    public const int FlagSidechain = 2;

    [FieldOffset(0)]
    public uint Done;

    // Host-written, before it raises Requested and signals the sandbox.
    [FieldOffset(128)]
    public long Requested;

    [FieldOffset(136)]
    public int Frames;

    [FieldOffset(140)]
    public int Flags;

    [FieldOffset(144)]
    public int ParamCount;

    [FieldOffset(148)]
    public int MidiCount;

    [FieldOffset(152)]
    public long SignalNs;

    // Sandbox-written.
    [FieldOffset(256)]
    public long Completed;

    /// <summary>0 when the last process call succeeded, else what an_plug_process returned.</summary>
    [FieldOffset(264)]
    public int Status;

    [FieldOffset(272)]
    public long WakeNs;

    [FieldOffset(280)]
    public long DoneNs;
}

/// <summary>A parameter change in a slot's parameter area.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct ParamChange {
    public int Index;
    public int Reserved;
    public double Value;
}

/// <summary>
///     The shared segment of one <c>anode-plugin</c> sandbox: the header, the sandbox's go word (its own line), one
///     <see cref="SlotControl" /> per slot, then per slot its parameter changes, MIDI events and six planar channels
///     (input, sidechain and output, left and right) of <see cref="SandboxHeader.MaxFrames" /> floats.
/// </summary>
internal static unsafe class SharedLayout {
    public const int MaxParams = 256;
    public const int MaxMidi = 1024;
    public const int Channels = 6;
    public const int InLeft = 0;
    public const int SidechainLeft = 2;
    public const int OutLeft = 4;
    const long GoOffset = 256;
    const long ControlsOffset = 512;

    public static long ControlOffset(int slot) => ControlsOffset + (long)slot * sizeof(SlotControl);

    public static long DataOffset(int maxSlots) => ControlOffset(maxSlots);

    public static long SlotDataSize(int maxFrames) =>
        MaxParams * sizeof(ParamChange) + MaxMidi * 8 + (long)Channels * maxFrames * sizeof(float);

    public static long Size(int maxSlots, int maxFrames) => DataOffset(maxSlots) + maxSlots * SlotDataSize(maxFrames);

    public static SandboxHeader* Header(byte* segment) => (SandboxHeader*)segment;

    public static uint* Go(byte* segment) => (uint*)(segment + GoOffset);

    public static SlotControl* Control(byte* segment, int slot) => (SlotControl*)(segment + ControlOffset(slot));

    static byte* SlotData(byte* segment, int slot) {
        var h = Header(segment);
        return segment + DataOffset(h->MaxSlots) + slot * SlotDataSize(h->MaxFrames);
    }

    public static ParamChange* Params(byte* segment, int slot) => (ParamChange*)SlotData(segment, slot);

    public static Native.PlugMidi* Midi(byte* segment, int slot) =>
        (Native.PlugMidi*)(SlotData(segment, slot) + MaxParams * sizeof(ParamChange));

    /// <summary>Channel <paramref name="channel" /> (0–5: in L/R, sidechain L/R, out L/R) of a slot.</summary>
    public static float* Channel(byte* segment, int slot, int channel) =>
        (float*)(SlotData(segment, slot) + MaxParams * sizeof(ParamChange) + MaxMidi * 8) +
        (long)channel * Header(segment)->MaxFrames;

    /// <summary>A segment name (at most 30 characters, as macOS allows).</summary>
    public static string ShmName(int hostPid, int sandbox) => $"/anp.{hostPid}.{sandbox}";

    /// <summary>The Mach bootstrap service the host publishes the device's IO workgroup under (per publication).</summary>
    public static string WorkgroupService(int hostPid, int generation) => $"org.anode.plugins.wg.{hostPid}.{generation}";
}
