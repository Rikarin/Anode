namespace Anode.Interchange.Adm;

/// <summary>
///     ADM BWF reader (docs/11-phase-10.md, workstream D): the subset <see cref="AdmWriter" /> writes (beds as
///     <c>DirectSpeakers</c> of the named layouts, objects with polar block formats), from RIFF, RF64 or BW64. A file
///     without <c>chna</c> and <c>axml</c>, or with ADM Anode cannot place, throws <see cref="FormatException" />.
/// </summary>
public static class AdmReader {
    /// <summary>
    ///     The scene of the file at <paramref name="path" />, its beds' and objects' audio extracted as WAV files into
    ///     <paramref name="mediaFolder" /> (a scratch folder the job gives).
    /// </summary>
    public static AdmScene Read(string path, string mediaFolder) => throw Pending.WorkstreamD();
}
