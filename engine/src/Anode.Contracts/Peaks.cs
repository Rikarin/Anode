namespace Anode.Contracts;

/// <summary>
///     The binary body of <c>GET /api/v1/project/files/{fileId}/peaks</c> (<c>application/octet-stream</c>, little
///     endian): a 24-byte header — magic <c>"ANPK"</c> (u32), format version (u16, 1), channels (u16), frames per peak
///     (u32), first peak index (u32), peak count (u64) — then, for each peak, for each channel, a min and a max sample
///     as i16 (full scale ±32767).
/// </summary>
public static class PeakFormat {
    public const uint Magic = 0x4B504E41; // "ANPK"
    public const ushort Version = 1;
    public const int HeaderBytes = 24;

    /// <summary>The resolutions the server keeps, in frames per peak; a request is answered at the nearest one.</summary>
    public static ReadOnlySpan<int> Levels => [64, 256, 1024, 4096, 16384];
}
