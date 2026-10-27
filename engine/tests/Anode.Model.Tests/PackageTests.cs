using Anode.Model.Storage;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>The project package on disk: layout, schema versions, and damage.</summary>
public sealed class PackageTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-package-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_package_is_a_root_file_and_a_file_per_track() {
        var (state, _, _) = RandomProjects.Build(7, 30);
        ProjectPackage.Save(folder, state);
        Assert.True(File.Exists(Path.Combine(folder, "project.json")));
        Assert.True(Directory.Exists(Path.Combine(folder, "Audio Files")));
        Assert.True(Directory.Exists(Path.Combine(folder, "Peaks")));
        Assert.Equal(
            state.Tracks.Select(static t => t.Id + ".json").Order(),
            Directory.EnumerateFiles(Path.Combine(folder, "tracks")).Select(Path.GetFileName).Order()
        );
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "project.json")))!;
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Saving_removes_the_files_of_removed_tracks() {
        var (state, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        var fewer = state with { Tracks = state.Tracks.RemoveAt(0) };
        fewer = fewer with {
            Regions = fewer.Regions.RemoveRange(state.RegionsOf(state.Tracks[0].Id).Select(static r => r.Id))
        };
        ProjectPackage.Save(folder, fewer);
        Assert.Equal(fewer.Tracks.Count, Directory.EnumerateFiles(Path.Combine(folder, "tracks")).Count());
        Assert.Equal(RandomProjects.Json(fewer), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void A_file_without_a_schema_version_is_schema_1_and_a_newer_one_is_refused() {
        var (state, _, _) = RandomProjects.Build(5);
        ProjectPackage.Save(folder, state);
        var path = Path.Combine(folder, "project.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root.Remove("schemaVersion");
        File.WriteAllText(path, root.ToJsonString());
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));

        root["schemaVersion"] = PackageMigrations.CurrentSchemaVersion + 1;
        File.WriteAllText(path, root.ToJsonString());
        var e = Assert.Throws<PackageException>(() => ProjectPackage.Load(folder));
        Assert.Contains("newer Anode", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_root_is_refused_and_a_damaged_track_left_out() {
        var (state, _, _) = RandomProjects.Build(11, 30);
        Assert.NotEmpty(state.Tracks);
        ProjectPackage.Save(folder, state);
        File.WriteAllText(Path.Combine(folder, "tracks", state.Tracks[0].Id + ".json"), "{ not json");
        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(state.Tracks.Count - 1, loaded.Tracks.Count);
        Assert.DoesNotContain(loaded.Regions.Values, r => r.TrackId == state.Tracks[0].Id);

        File.WriteAllText(Path.Combine(folder, "project.json"), "[]");
        Assert.Throws<PackageException>(() => ProjectPackage.Load(folder));
        File.Delete(Path.Combine(folder, "project.json"));
        Assert.Throws<PackageException>(() => ProjectPackage.Load(folder));
    }
}
