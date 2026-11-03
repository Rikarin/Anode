namespace Anode.Interchange.Adm;

/// <summary>
///     ADM BWF writer (docs/11-phase-10.md, workstream D): BW64 (RIFF/WAVE below 4 GB; <c>BW64</c> with <c>ds64</c>
///     above, ITU-R BS.2088) with the beds' and objects' channels interleaved in scene order, a <c>chna</c> chunk mapping
///     each track to its <c>audioTrackUID</c>, and an <c>axml</c> chunk with the ADM (BS.2076): a programme, content,
///     an <c>audioObject</c> per bed and per object, pack formats (common definitions for the beds' layouts), channel
///     formats with the objects' block formats. 24-bit PCM or 32-bit float.
/// </summary>
public static class AdmWriter {
    /// <summary>Writes <paramref name="scene" /> to <paramref name="path" />; the sources must match its rate and length.</summary>
    public static void Write(AdmScene scene, string path, int bitDepth = 24) => throw Pending.WorkstreamD();
}
