using System.Runtime.InteropServices;

namespace Anode.Spikes.SandboxWake;

/// <summary>Run parameters the host writes once, before it spawns the sandboxes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
struct Header {
    public const int MagicValue = 0x414E5334; // "ANS4"

    [FieldOffset(0)]
    public int Magic;

    [FieldOffset(4)]
    public int Sandboxes;

    [FieldOffset(8)]
    public int Instances;

    [FieldOffset(12)]
    public int MaxFrames;

    [FieldOffset(16)]
    public int Channels;

    [FieldOffset(20)]
    public int Kind;

    [FieldOffset(24)]
    public int Biquads;

    [FieldOffset(28)]
    public int BurnNs;

    [FieldOffset(32)]
    public long SandboxSpinNs;

    [FieldOffset(40)]
    public long PeriodNs;

    [FieldOffset(48)]
    public int RealTime;

    [FieldOffset(52)]
    public int HostPid;

    [FieldOffset(56)]
    public int Workgroup;

    [FieldOffset(128)]
    public int Stop;
}

/// <summary>
///     One sandbox's handshake. The two wake words sit on their own 128-byte lines (Apple silicon's line size), apart
///     from the host-written and the sandbox-written fields, so the two sides never write the same line.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 512)]
struct SandboxControl {
    [FieldOffset(0)]
    public uint Go;

    [FieldOffset(128)]
    public uint Done;

    // Host-written, before it signals Go.
    [FieldOffset(256)]
    public long Cycle;

    [FieldOffset(264)]
    public long SignalNs;

    [FieldOffset(272)]
    public int Frames;

    // Sandbox-written.
    [FieldOffset(384)]
    public long DoneCycle;

    [FieldOffset(392)]
    public long WakeNs;

    [FieldOffset(400)]
    public long DoneNs;

    [FieldOffset(408)]
    public int Ready;

    [FieldOffset(412)]
    public int RtStatus;

    [FieldOffset(416)]
    public int Pid;

    [FieldOffset(420)]
    public int WorkgroupStatus;
}

/// <summary>The segment: header, one control block per sandbox, then planar float buffers per instance.</summary>
static unsafe class Layout {
    public const int Channels = 2;
    public const int MaxFrames = 1024;

    public static long ControlOffset(int sandbox) => sizeof(Header) + (long)sandbox * sizeof(SandboxControl);

    public static long BufferOffset(int sandboxes) => ControlOffset(sandboxes);

    public static long Size(int sandboxes, int instances) =>
        BufferOffset(sandboxes) + (long)instances * Channels * MaxFrames * sizeof(float);

    public static Header* HeaderOf(byte* segment) => (Header*)segment;

    public static SandboxControl* Control(byte* segment, int sandbox) =>
        (SandboxControl*)(segment + ControlOffset(sandbox));

    /// <summary>Channel <paramref name="channel" /> of instance <paramref name="instance" />.</summary>
    public static float* Buffer(byte* segment, int sandboxes, int instance, int channel) =>
        (float*)(segment + BufferOffset(sandboxes)) + ((long)instance * Channels + channel) * MaxFrames;

    /// <summary>Instances are dealt round-robin: instance i lives in sandbox i % K.</summary>
    public static int SandboxOf(int instance, int sandboxes) => instance % sandboxes;

    public static string ShmName(int hostPid) => $"/an4.{hostPid}";

    public static string WorkgroupService(int hostPid) => $"org.anode.spikes.s4.{hostPid}";

    public static string SemName(int hostPid, int sandbox, bool go) => $"/an4.{hostPid}.{(go ? 'g' : 'd')}{sandbox}";
}
