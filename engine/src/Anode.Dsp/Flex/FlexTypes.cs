using System.Runtime.InteropServices;

namespace Anode.Dsp.Flex;

/// <summary>
///     A flex algorithm (docs/06-phase-5.md). It mirrors <c>Anode.Ipc.Segments.FlexAlgorithm</c> value for value
///     (Anode.Dsp references nothing), so the engine casts one to the other; it has its own name so that code using both
///     namespaces needs no alias.
/// </summary>
public enum FlexKind : byte {
    /// <summary>Unwarped: the engine plays such entries itself and never asks for a renderer.</summary>
    None = 0,
    Polyphonic = 1,
    Monophonic = 2,
    Slicing = 3,
    Rhythmic = 4,
    Speed = 5,
    Tempophone = 6,
    Pitch = 7
}

/// <summary>
///     A warp point: project sample <see cref="Offset" /> from the entry's start plays file frame
///     <see cref="FileFrame" />. The layout of <c>Anode.Ipc.Segments.WarpPoint</c> (16 bytes), so a segment's points
///     are reinterpreted, not copied (<c>MemoryMarshal.Cast</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FlexWarpPoint {
    public long Offset;
    public double FileFrame;
}

/// <summary>
///     A pitch point: at file frame <see cref="FileFrame" />, the pitch shift and formant shift in cents and a gain in
///     dB. The layout of <c>Anode.Ipc.Segments.PitchPoint</c> (24 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FlexPitchPoint {
    public double FileFrame;
    public float Cents;
    public float FormantCents;
    public float GainDb;
    public int Reserved;
}

/// <summary>
///     A file a flex renderer reads, by file frame. Called on the disk thread, from <see cref="IFlexRenderer.Render" />.
/// </summary>
public interface IFlexSource {
    int Channels { get; }

    long Frames { get; }

    /// <summary>
    ///     Reads <c>interleaved.Length / Channels</c> frames from <paramref name="fileFrame" /> (which may be negative),
    ///     zero-filling those outside the file; returns how many were inside it.
    /// </summary>
    int Read(long fileFrame, Span<float> interleaved);
}

/// <summary>
///     A warped playlist entry as a renderer sees it: its algorithm, its warp map (sorted by offset, strictly
///     increasing, the first at 0 and the last at <see cref="Length" />; file frames linear between points), its pitch
///     points (sorted by file frame; none: no pitch change), and its length in project samples. A view over the
///     segment's arrays, made per call without allocating.
/// </summary>
public readonly ref struct FlexRegion(
    FlexKind algorithm,
    ReadOnlySpan<FlexWarpPoint> warp,
    ReadOnlySpan<FlexPitchPoint> pitch,
    long length
) {
    public FlexKind Algorithm { get; } = algorithm;
    public ReadOnlySpan<FlexWarpPoint> Warp { get; } = warp;
    public ReadOnlySpan<FlexPitchPoint> Pitch { get; } = pitch;
    public long Length { get; } = length;
}
