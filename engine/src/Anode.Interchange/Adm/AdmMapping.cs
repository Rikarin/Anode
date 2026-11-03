using Anode.Contracts;
using Anode.Model;

namespace Anode.Interchange.Adm;

/// <summary>
///     The project ↔ ADM scene mapping (docs/11-phase-10.md, owned by workstream B): what the <c>admExport</c> and
///     <c>admImport</c> jobs give <see cref="AdmWriter" /> and take from <see cref="AdmReader" />.
/// </summary>
public static class AdmMapping {
    /// <summary>
    ///     The scene of <paramref name="request" /> for <paramref name="state" />: the bed (the master's bed bounce,
    ///     <paramref name="bedPath" />, in the project's format) and an object per object-mode track asked for (its
    ///     bounce from <paramref name="objectPaths" /> by track id), with blocks from its <c>object:…</c> automation over
    ///     the range (a block per automation change, interpolated, or per 20 ms where it ramps).
    /// </summary>
    public static AdmScene ToScene(
        ProjectState state,
        AdmExportRequest request,
        string bedPath,
        IReadOnlyDictionary<string, string> objectPaths
    ) =>
        throw Pending.WorkstreamB();

    /// <summary>
    ///     The <c>importTracks</c> content of <paramref name="scene" /> for <paramref name="state" />: an audio track per
    ///     bed (input format the bed's) and per object (object mode, its blocks as <c>object:…</c> automation), each with
    ///     a region of the file the job made of its audio (<paramref name="fileIds" />, by <c>AudioPath</c>).
    /// </summary>
    public static ImportedContent ToContent(
        AdmScene scene,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    ) =>
        throw Pending.WorkstreamB();
}
