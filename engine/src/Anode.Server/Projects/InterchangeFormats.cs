using Anode.Contracts;
using Anode.Interchange.Aaf;
using Anode.Interchange.MusicXml;
using Anode.Model;

namespace Anode.Server.Projects;

/// <summary>
///     The MusicXML and AAF readers and writers as the interchange jobs use them (docs/10-phase-9.md, workstreams D and
///     E): a seam so the jobs' tests run with doubles. The Standard MIDI File mapping (<c>Anode.Interchange.Smf</c>) is
///     the server workstream's own and is called directly.
/// </summary>
public interface IInterchangeFormats {
    /// <summary>A MusicXML file of a score view (<see cref="MusicXmlWriter.Write" />).</summary>
    byte[] WriteMusicXml(ScoreView view, bool compressed);

    /// <summary>
    ///     A MusicXML file's content (<see cref="MusicXmlReader.Read" />); <see cref="FormatException" /> when it is not
    ///     one.
    /// </summary>
    ImportedContent ReadMusicXml(byte[] data);

    /// <summary>Writes the AAF and its linked media (<see cref="AafWriter.Write" />); every file written, the AAF first.</summary>
    IReadOnlyList<string> WriteAaf(ProjectState state, string projectFolder, AafExportRequest request, string path);

    /// <summary>
    ///     An AAF's composition, embedded media extracted into <paramref name="mediaFolder" />
    ///     (<see cref="AafReader.Read" />).
    /// </summary>
    AafComposition ReadAaf(string path, string mediaFolder);

    /// <summary>
    ///     The composition's content, its media as the project files <paramref name="fileIds" /> names
    ///     (<see cref="AafReader.ToContent" />).
    /// </summary>
    ImportedContent AafContent(
        AafComposition composition,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    );
}

/// <summary><see cref="IInterchangeFormats" /> over <c>Anode.Interchange</c>'s MusicXML and AAF.</summary>
public sealed class InterchangeFormats : IInterchangeFormats {
    public byte[] WriteMusicXml(ScoreView view, bool compressed) => MusicXmlWriter.Write(view, compressed);

    public ImportedContent ReadMusicXml(byte[] data) => MusicXmlReader.Read(data);

    public IReadOnlyList<string> WriteAaf(
        ProjectState state,
        string projectFolder,
        AafExportRequest request,
        string path
    ) =>
        AafWriter.Write(state, projectFolder, request, path);

    public AafComposition ReadAaf(string path, string mediaFolder) => AafReader.Read(path, mediaFolder);

    public ImportedContent AafContent(
        AafComposition composition,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    ) =>
        AafReader.ToContent(composition, state, fileIds);
}
