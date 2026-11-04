using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Anode.Server.Library;

/// <summary>
///     The user's templates (docs/04-phase-3.md, "Templates"), each a project package, so everything that reads a package
///     reads them, and a sidecar saying what it is: <c>Library/Project Templates/&lt;id&gt;.anode</c> and
///     <c>.json</c>, and the same in <c>Library/Track Templates</c>. A template without its sidecar is not listed, so
///     one cut short by a crash is not offered.
/// </summary>
public sealed class TemplateLibrary(IOptions<ServerOptions> options) {
    readonly string root = Path.Combine(options.Value.DataDirectory, "Library");

    string ProjectsFolder => Path.Combine(root, "Project Templates");

    string TracksFolder => Path.Combine(root, "Track Templates");

    /// <summary>The factory's templates, then the user's by name.</summary>
    public IReadOnlyList<ProjectTemplate> ProjectTemplates() => [
        ..FactoryTemplates.All.Select(static t => new ProjectTemplate(
                t.Id,
                t.Name,
                t.Category,
                t.Description,
                true,
                null,
                false
            )
        ),
        ..Read(ProjectsFolder)
            .Select(static t => new ProjectTemplate(
                    t.Id,
                    t.Name,
                    t.Category,
                    string.Empty,
                    false,
                    t.SampleRate,
                    t.HasAudio
                )
            )
    ];

    public IReadOnlyList<TrackTemplate> TrackTemplates() => [
        ..Read(TracksFolder).Select(static t => new TrackTemplate(t.Id, t.Name, t.Category, t.Tracks))
    ];

    /// <summary>Where a new project template goes.</summary>
    public string NewProjectFolder(string id) => Package(ProjectsFolder, id);

    /// <summary>Where a new track template goes.</summary>
    public string NewTrackFolder(string id) => Package(TracksFolder, id);

    /// <summary>The package of a user's project template, or null.</summary>
    public string? ProjectFolder(string id) => Listed(ProjectsFolder, id);

    /// <summary>The package of a track template, or null.</summary>
    public string? TrackFolder(string id) => Listed(TracksFolder, id);

    /// <summary>Lists a written template.</summary>
    public void Describe(string package, TemplateFile template) =>
        AtomicRename.Write(
            Path.ChangeExtension(package, ".json"),
            JsonSerializer.Serialize(template, PackageJsonContext.Default.TemplateFile)
        );

    public bool DeleteProject(string id) => Delete(ProjectsFolder, id);

    public bool DeleteTrack(string id) => Delete(TracksFolder, id);

    static string Package(string folder, string id) => Path.Combine(folder, id + ProjectPackage.Extension);

    static string? Listed(string folder, string id) =>
        ProjectPackage.IsSafeId(id)
        && File.Exists(Path.Combine(folder, id + ".json"))
        && ProjectPackage.Exists(Package(folder, id))
            ? Package(folder, id)
            : null;

    /// <summary>Unlisted first, so that a stopped delete leaves nothing offered.</summary>
    static bool Delete(string folder, string id) {
        if (Listed(folder, id) is not { } package) {
            return false;
        }

        File.Delete(Path.Combine(folder, id + ".json"));
        Directory.Delete(package, true);
        return true;
    }

    static TemplateFile[] Read(string folder) {
        if (!Directory.Exists(folder)) {
            return [];
        }

        return [
            ..Directory.EnumerateFiles(folder, "*.json")
                .Select(Sidecar)
                .OfType<TemplateFile>()
                .Where(t => Listed(folder, t.Id) is not null)
                .OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    static TemplateFile? Sidecar(string path) {
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), PackageJsonContext.Default.TemplateFile) is {
                Id: not null, Name: not null, Category: not null
            } template
                ? template
                : null;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}
