using Anode.Contracts;
using Anode.Model;

namespace Anode.Interchange.Aaf;

/// <summary>
///     AAF import (docs/10-phase-9.md, workstream E): the compound file and the object model subset Anode writes, as
///     Pro Tools and Logic write it. A file that is not AAF, or holds no audio composition, throws
///     <see cref="FormatException" />.
/// </summary>
public static class AafReader {
    /// <summary>
    ///     The composition of the AAF at <paramref name="path" />, extracting embedded essence as WAV files into
    ///     <paramref name="mediaFolder" /> (a scratch folder the job gives) and resolving linked media relative to the
    ///     file; a linked file that cannot be found throws <see cref="FileNotFoundException" />.
    /// </summary>
    public static AafComposition Read(string path, string mediaFolder) => throw Pending.WorkstreamE();

    /// <summary>
    ///     The <c>importTracks</c> content of <paramref name="composition" /> for <paramref name="state" />: an audio track
    ///     per AAF track with a region per clip (positions converted from samples to ticks by the project's tempo map, to
    ///     the sample), its markers; <paramref name="fileIds" /> maps each clip's <c>MediaPath</c> to the project file the
    ///     job made of it.
    /// </summary>
    public static ImportedContent ToContent(
        AafComposition composition,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    ) =>
        throw Pending.WorkstreamE();
}
