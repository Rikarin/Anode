using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anode.Spikes.BulkIpc;

/// <summary>
///     The spike's control messages. The wire format is Anode.Ipc's (a 4-byte little-endian length, then UTF-8 JSON
///     with a <c>type</c> discriminator); in production these become derived types of <c>IpcMessage</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LoadSegment), "loadSegment")]
[JsonDerivedType(typeof(SegmentLoaded), "segmentLoaded")]
[JsonDerivedType(typeof(ResetRequest), "reset")]
[JsonDerivedType(typeof(StatsRequest), "stats")]
[JsonDerivedType(typeof(StatsReply), "statsReply")]
[JsonDerivedType(typeof(QuitRequest), "quit")]
abstract record SpikeMessage;

/// <summary>
///     Server to host: map the segment at <paramref name="Path" /> (a file, or a named mapping on Windows) and install
///     it as the whole model (<paramref name="Track" /> = -1) or as track slot <paramref name="Track" />. Track loads are
///     staged until one arrives with <paramref name="Commit" />, which publishes them all in one swap. A
///     <paramref name="Pooled" /> segment is mapped once and kept: the server reuses it once the host has released it.
/// </summary>
sealed record LoadSegment(
    long SegmentId,
    string Path,
    long Length,
    long Version,
    int Track,
    bool Commit,
    bool Verify,
    bool Lock,
    long SentTicks,
    bool Pooled
) : SpikeMessage;

/// <summary>
///     Host to server, once the segment is mapped (and, on a commit, once the audio thread has used the new model and
///     the segments it replaced are unmapped). Ticks are <see cref="System.Diagnostics.Stopwatch" /> timestamps, which
///     are one system-wide monotonic clock on macOS, Linux and Windows.
/// </summary>
sealed record SegmentLoaded(
    long SegmentId,
    long Version,
    long ReceivedTicks,
    long MappedTicks,
    long PrefaultedTicks,
    long VerifiedTicks,
    long LockedTicks,
    long PublishedTicks,
    long FirstUseTicks,
    long FirstRenderTicks,
    long ReleasedTicks,
    int Released,
    string? Error
) : SpikeMessage;

/// <summary>Unpublish everything, release every segment and zero the audio thread's counters.</summary>
sealed record ResetRequest : SpikeMessage;

sealed record StatsRequest : SpikeMessage;

sealed record StatsReply(
    long Callbacks,
    long LateCallbacks,
    long SlowCallbacks,
    string SlowByPhase,
    double MaxLatenessMicros,
    double MaxCallbackMicros,
    long AudioThreadAllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    int MappedSegments,
    long MappedBytes,
    long WorkingSet,
    int OpenHandles,
    bool Realtime,
    double Sink
) : SpikeMessage;

sealed record QuitRequest : SpikeMessage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(SpikeMessage))]
sealed partial class SpikeJsonContext : JsonSerializerContext;

/// <summary>Blocking frame I/O; the control threads of both roles are plain threads in the spike.</summary>
static class Framing {
    const int MaxFrameBytes = 16 * 1024 * 1024;

    public static void Write(Stream stream, SpikeMessage message) {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SpikeJsonContext.Default.SpikeMessage);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        stream.Write(frame);
        stream.Flush();
    }

    /// <summary>Returns null when the peer closed the connection.</summary>
    public static SpikeMessage? Read(Stream stream) {
        Span<byte> header = stackalloc byte[4];
        if (stream.ReadAtLeast(header, 4, false) < 4) {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrameBytes) {
            throw new InvalidDataException($"IPC frame of {length} bytes is out of range.");
        }

        var payload = new byte[length];
        stream.ReadExactly(payload);
        return JsonSerializer.Deserialize(payload, SpikeJsonContext.Default.SpikeMessage)
            ?? throw new InvalidDataException("Empty IPC message.");
    }
}
