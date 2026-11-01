using Anode.Contracts;
using Anode.Model;

namespace Anode.Interchange.Aaf;

/// <summary>
///     AAF export (docs/10-phase-9.md, workstream E): the arrangement's audio tracks as a composition mob with a
///     timeline slot per track (edit rate = the project's sample rate), source clips with offsets and clip gain,
///     fillers, crossfades as <c>Transition</c>s and other fades as Avid's <c>FadeIn</c>/<c>FadeOut</c> lengths, the
///     markers as <c>DescriptiveMarker</c>s; master mobs and file source mobs with WAVE descriptors, the essence embedded
///     or linked by network locators to WAV files written beside it. MIDI is not exported (SMF covers it).
/// </summary>
public static class AafWriter {
    /// <summary>
    ///     Writes <paramref name="request" />'s tracks of <paramref name="state" /> (a project in
    ///     <paramref name="projectFolder" />, whose files are relative to it) to <paramref name="path" />, and, with linked
    ///     media, the WAV files into <c>&lt;path without extension&gt; Media/</c>. Returns every file written, the AAF
    ///     first.
    /// </summary>
    public static IReadOnlyList<string> Write(
        ProjectState state,
        string projectFolder,
        AafExportRequest request,
        string path
    ) =>
        throw Pending.WorkstreamE();
}
