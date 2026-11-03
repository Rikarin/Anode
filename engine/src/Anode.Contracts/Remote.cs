using System.Buffers.Binary;

namespace Anode.Contracts;

// Remote (docs/11-phase-10.md): the remote route on phones and tablets, the Server's DNS-SD advertisement, key-command
// pads and listen-in (docs/adr/0013-video-and-listen-in.md).

/// <summary>
///     The DNS-SD advertisement: service type <c>_anode._tcp</c> on the Server's port, instance name the Server's
///     name, and TXT keys <c>version</c>, <c>project</c> (the open project's name, absent when none), <c>pairing</c>
///     (<c>1</c> when a token is needed, else <c>0</c>) and <c>path</c> (<c>/api/v1</c>).
/// </summary>
public static class Discovery {
    public const string ServiceType = "_anode._tcp";
    public const string TxtVersion = "version";
    public const string TxtProject = "project";
    public const string TxtPairing = "pairing";
    public const string TxtPath = "path";
}

/// <summary>
///     What a remote needs to find and pair with this Server (<c>GET /system/discovery</c>, and what DNS-SD
///     advertises): the Server's name (the machine's), the host name or address and port remotes reach it on, the open
///     project's name, whether pairing (the token, by QR code) is needed, and the Server's version.
/// </summary>
public sealed record DiscoveryInfo(
    string Name,
    string Host,
    int Port,
    string? ProjectName,
    bool PairingRequired,
    string Version
);

/// <summary>A key-command pad on the remote: its label, the key command it runs (a <c>KeyCommandSet</c> id), and a colour.</summary>
public sealed record KeyCommandPad(string Id, string Label, string CommandId, string? Color = null);

/// <summary>A page of key-command pads (the remote shows one set at a time).</summary>
public sealed record PadSet(string Id, string Name, IReadOnlyList<KeyCommandPad> Pads);

/// <summary>
///     The remote's key-command pad sets (<c>GET/PUT /remote/pads</c>), in the user settings; ids unique across sets
///     and pads.
/// </summary>
public sealed record RemotePads(IReadOnlyList<PadSet> Sets) {
    /// <summary>No sets.</summary>
    public static RemotePads Empty { get; } = new([]);

    /// <summary>
    ///     A new user's (decided at integration; the editor's fake Server has the same, <c>DefaultRemotePads</c>): Logic
    ///     Remote's transport and editing pads, by the editor's key command ids (<c>keys/key-commands.ts</c>).
    /// </summary>
    public static RemotePads Default { get; } = new(
        [
            new(
                "transport",
                "Transport",
                [
                    new("p1", "Play", "transport.play", "#3fae4a"), new("p2", "Record", "transport.record", "#e5483a"),
                    new("p3", "Cycle", "transport.cycle", "#e6b422"), new("p4", "Go to Start", "transport.goToStart"),
                    new("p5", "Metronome", "transport.metronome"),
                    new("p6", "Capture Recording", "transport.flashback"),
                    new("p7", "Undo", "edit.undo"), new("p8", "Redo", "edit.redo"), new("p9", "Save", "project.save")
                ]
            ),
            new(
                "editing",
                "Editing",
                [
                    new("e1", "Create Marker", "track.marker", "#3b82c4"),
                    new("e2", "Split at Playhead", "region.split"),
                    new("e3", "Join", "region.join"), new("e4", "Mute Regions", "region.mute"),
                    new("e5", "Automation Read", "automation.read"), new("e6", "Automation Touch", "automation.touch"),
                    new("e7", "Automation Latch", "automation.latch"), new("e8", "Live Loops", "view.liveLoops")
                ]
            )
        ]
    );
}

/// <summary>
///     <c>POST /remote/pads/run</c>: a key command for the desktop editor to run (one the remote cannot run itself, as
///     a pad's <see cref="KeyCommandPad.CommandId" />); the Server broadcasts it as the <c>keyCommand</c> event (Phase 8).
/// </summary>
public sealed record RunKeyCommandRequest(string CommandId);

/// <summary>
///     Listen-in (<c>GET /remote/listen/state</c>, the <c>listenIn</c> event): <c>on</c> while the master is being
///     encoded (at least one listener), the number of listeners (at most <see cref="ListenIn.MaxListeners" />), and the
///     Server's own delay from the host's ring to the socket in milliseconds (null while off); a remote adds its jitter
///     buffer and output to show its latency, measured from the frames' transport positions.
/// </summary>
public sealed record ListenInState(bool On, int Listeners, double? LatencyMs);

/// <summary>
///     The first message on the listen-in socket (text, JSON): the stream's sample rate (48000), channels (2), samples
///     per frame (960, 20 ms), bit rate (128000), and the project's sample rate, which transport positions count in.
/// </summary>
public sealed record ListenInFormat(int SampleRate, int Channels, int FrameSamples, int Bitrate, int ProjectSampleRate);

/// <summary>
///     The listen-in WebSocket (<c>/api/v1/remote/listen</c>, <c>?access_token=</c> when paired): after
///     <see cref="ListenInFormat" />, one binary message per Opus frame: a <see cref="HeaderBytes" />-byte header, little
///     endian, then the Opus packet. Header: <c>uint32</c> sequence at 0 (0, 1, … per connection; a gap is a lost
///     frame), <c>int64</c> transport sample at 4 (the project sample of the frame's first sample; −1 while stopped),
///     <c>uint16</c> packet length at 12, <c>uint8</c> flags at 14 (<see cref="FlagPlaying" />,
///     <see cref="FlagDiscontinuity" />), 0 at 15. At most <see cref="MaxListeners" /> sockets; another is refused (503).
/// </summary>
public static class ListenIn {
    public const int MaxListeners = 4;
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int FrameSamples = 960;
    public const int Bitrate = 128_000;
    public const int HeaderBytes = 16;

    /// <summary>The transport is playing (the transport sample advances with the frames).</summary>
    public const byte FlagPlaying = 1;

    /// <summary>The audio does not follow on from the previous frame (a locate, a ring overrun): reset the jitter buffer.</summary>
    public const byte FlagDiscontinuity = 2;

    /// <summary>Writes a frame's header into the first <see cref="HeaderBytes" /> of <paramref name="destination" />.</summary>
    public static void WriteHeader(
        Span<byte> destination,
        uint sequence,
        long transportSample,
        int packetBytes,
        byte flags
    ) {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], transportSample);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], checked((ushort)packetBytes));
        destination[14] = flags;
        destination[15] = 0;
    }

    /// <summary>Reads a frame's header; false when <paramref name="frame" /> is too short for it and its packet.</summary>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> frame,
        out uint sequence,
        out long transportSample,
        out byte flags,
        out ReadOnlySpan<byte> packet
    ) {
        sequence = 0;
        transportSample = 0;
        flags = 0;
        packet = default;
        if (frame.Length < HeaderBytes) {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame[12..]);
        if (frame.Length < HeaderBytes + length) {
            return false;
        }

        sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        transportSample = BinaryPrimitives.ReadInt64LittleEndian(frame[4..]);
        flags = frame[14];
        packet = frame.Slice(HeaderBytes, length);
        return true;
    }
}
