namespace Anode.Control.Osc;

/// <summary>An OSC 1.0 packet: a message or a bundle.</summary>
public abstract record OscPacket;

/// <summary>An OSC message: an address pattern and its arguments.</summary>
public sealed record OscMessage(string Address, IReadOnlyList<OscArgument> Arguments) : OscPacket;

/// <summary>An OSC bundle: an NTP time tag (1: immediately) and its elements, messages or bundles.</summary>
public sealed record OscBundle(ulong TimeTag, IReadOnlyList<OscPacket> Elements) : OscPacket;

/// <summary>An OSC argument of one of the types Anode reads and writes.</summary>
public abstract record OscArgument;

/// <summary>Type tag <c>i</c>.</summary>
public sealed record OscInt(int Value) : OscArgument;

/// <summary>Type tag <c>f</c>.</summary>
public sealed record OscFloat(float Value) : OscArgument;

/// <summary>Type tag <c>s</c>.</summary>
public sealed record OscString(string Value) : OscArgument;

/// <summary>Type tag <c>b</c>.</summary>
public sealed record OscBlob(byte[] Value) : OscArgument;

/// <summary>Type tags <c>T</c> and <c>F</c>.</summary>
public sealed record OscBool(bool Value) : OscArgument;

/// <summary>The OSC 1.0 binary format (4-byte aligned, big-endian, NUL-padded strings, type tag strings).</summary>
public static class OscCodec {
    /// <summary>A packet from a datagram; a malformed one is a <see cref="FormatException" />.</summary>
    public static OscPacket Decode(ReadOnlySpan<byte> data) => throw Pending.WorkstreamD();

    /// <summary>A packet as a datagram.</summary>
    public static byte[] Encode(OscPacket packet) => throw Pending.WorkstreamD();
}
