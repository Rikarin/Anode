using Anode.Contracts;
using Anode.Model.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Anode.Server.Projects;

/// <summary>
///     The recently opened projects, most recent first, in <c>&lt;DataDirectory&gt;/recent.json</c> (written
///     atomically). Projects whose folder is gone are left out of the list.
/// </summary>
public sealed class RecentProjects(IOptions<ServerOptions> options, ILogger<RecentProjects> logger) {
    public const int Capacity = 20;

    readonly string path = Path.Combine(options.Value.DataDirectory, "recent.json");
    readonly Lock gate = new();

    public IReadOnlyList<ProjectSummary> List() {
        lock (gate) {
            return [..Read().Where(static p => ProjectPackage.Exists(p.Path))];
        }
    }

    /// <summary>Puts <paramref name="project" /> first (replacing its older entry).</summary>
    public void Touch(ProjectSummary project) {
        lock (gate) {
            var list = Read();
            list.RemoveAll(p => string.Equals(p.Path, project.Path, StringComparison.Ordinal) || p.Id == project.Id);
            list.Insert(0, project);
            if (list.Count > Capacity) {
                list.RemoveRange(Capacity, list.Count - Capacity);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicRename.Write(path, JsonSerializer.Serialize(list, PackageJsonContext.Default.ListProjectSummary));
        }
    }

    List<ProjectSummary> Read() {
        if (!File.Exists(path)) {
            return [];
        }

        try {
            var json = File.ReadAllText(path);
            return [
                ..(JsonSerializer.Deserialize(json, PackageJsonContext.Default.ListProjectSummary) ?? [])
                    .Where(static p => p is { Path: not null, Id: not null, Name: not null })
            ];
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            ProjectLog.Unreadable(logger, path, e.Message);
            return [];
        }
    }
}
