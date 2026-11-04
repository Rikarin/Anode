using Anode.Model.Storage;
using System.Text;

namespace Anode.Model.Tests;

/// <summary>The autosaved snapshot of a package (docs/04-phase-3.md, "Autosave and crash recovery").</summary>
public sealed class ProjectAutosaveTests : IDisposable {
    static readonly Dictionary<string, byte[]> NoStates = [];

    readonly string folder = Directory.CreateTempSubdirectory("anode-autosave-").FullName;
    readonly DateTimeOffset start = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_snapshot_holds_the_edits_and_leaves_the_saved_document_alone() {
        var (saved, _, _) = RandomProjects.Build(3, 30);
        var (edited, _, _) = RandomProjects.Build(4, 30);
        ProjectPackage.Save(folder, saved);
        var before = SavedDocument();

        var manifest = ProjectAutosave.Write(folder, edited, NoStates, start);

        Assert.Equal(before, SavedDocument());
        Assert.Equal(RandomProjects.Json(saved), RandomProjects.Json(ProjectPackage.Load(folder)));
        Assert.Equal(manifest, ProjectAutosave.Recoverable(folder));
        Assert.Equal(start, manifest.Autosaved);
        Assert.Equal(edited.Name, manifest.Name);
        Assert.Equal(RandomProjects.Json(edited), RandomProjects.Json(Snapshot()));
    }

    [Fact]
    public void A_snapshot_keeps_plugin_states() {
        var (state, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        var track = state.Tracks.First(static t => t.Instrument is not null);
        Dictionary<string, byte[]> states = new() { [track.Id] = [1, 2, 3] };

        var manifest = ProjectAutosave.Write(folder, state, states, start);

        var document = ProjectAutosave.Document(folder, manifest);
        Assert.Equal([1, 2, 3], ProjectPackage.LoadStates(document, state)[track.Id]);
    }

    /// <summary>
    ///     A write stopped before every file it stores, as a killed process stops it: a torn temporary file beside the
    ///     real one and nothing after it. The previous snapshot stays whole each time, and the next write finishes.
    /// </summary>
    [Fact]
    public void A_write_interrupted_at_any_file_leaves_the_previous_snapshot_whole() {
        var (saved, _, _) = RandomProjects.Build(3, 30);
        var (first, _, _) = RandomProjects.Build(4, 30);
        var (second, _, _) = RandomProjects.Build(5, 30);
        ProjectPackage.Save(folder, saved);
        ProjectAutosave.Write(folder, first, NoStates, start);
        var before = SavedDocument();

        var files = 0;
        ProjectAutosave.Write(folder, second, NoStates, start, (_, _) => files++);
        Assert.True(files > 2, $"Only {files} files were written.");

        for (var stop = 0; stop < files; stop++) {
            var written = 0;
            var at = stop;
            Assert.Throws<Interrupted>(() => ProjectAutosave.Write(
                    folder,
                    second,
                    NoStates,
                    start.AddMinutes(1),
                    (path, bytes) => {
                        if (written++ == at) {
                            File.WriteAllBytes($"{path}.torn.tmp", bytes[..(bytes.Length / 2)]);
                            throw new Interrupted();
                        }

                        ProjectAutosave.WriteChanged(path, bytes);
                    }
                )
            );

            Assert.Equal(start, ProjectAutosave.Recoverable(folder)!.Autosaved);
            Assert.Equal(RandomProjects.Json(first), RandomProjects.Json(Snapshot()));
            Assert.Equal(before, SavedDocument());
        }

        ProjectAutosave.Write(folder, second, NoStates, start.AddMinutes(2));
        Assert.Equal(RandomProjects.Json(second), RandomProjects.Json(Snapshot()));
        Assert.Empty(Directory.EnumerateFiles(Autosave, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(before, SavedDocument());
    }

    [Fact]
    public void Garbage_in_the_slot_being_written_never_reaches_a_snapshot() {
        var (state, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        var live = ProjectAutosave.Write(folder, state, NoStates, start);
        var other = ProjectAutosave.Write(folder, state, NoStates, start);
        Assert.NotEqual(live.Slot, other.Slot);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(ProjectAutosave.Document(folder, live), ProjectPackage.TracksFolder)
                 )) {
            File.WriteAllText(file, "{ torn");
        }

        ProjectAutosave.Write(folder, state, NoStates, start);

        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(Snapshot()));
    }

    [Fact]
    public void Only_the_files_that_changed_are_written() {
        var (state, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        Assert.True(state.Tracks.Count >= 2);
        var slot = ProjectAutosave.Write(folder, state, NoStates, start);
        ProjectAutosave.Write(folder, state, NoStates, start);
        var edited = state with {
            Tracks = state.Tracks.SetItem(0, state.Tracks[0] with { Name = state.Tracks[0].Name + " edited" })
        };
        var tracks = Path.Combine(ProjectAutosave.Document(folder, slot), ProjectPackage.TracksFolder);
        var untouched = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var file in Directory.EnumerateFiles(tracks)) {
            File.SetLastWriteTimeUtc(file, untouched);
        }

        ProjectAutosave.Write(folder, edited, NoStates, start);

        var changed = Directory.EnumerateFiles(tracks).Where(f => File.GetLastWriteTimeUtc(f) != untouched);
        Assert.Equal(ProjectPackage.TrackFileName(edited.Tracks[0].Id), Path.GetFileName(Assert.Single(changed)));
    }

    [Fact]
    public void A_save_after_the_snapshot_stops_it_being_offered() {
        var (saved, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, saved);
        File.SetLastWriteTimeUtc(ProjectFile, start.UtcDateTime);
        ProjectAutosave.Write(folder, saved, NoStates, start);
        Assert.NotNull(ProjectAutosave.Recoverable(folder));

        File.SetLastWriteTimeUtc(ProjectFile, start.AddSeconds(1).UtcDateTime);

        Assert.Null(ProjectAutosave.Recoverable(folder));
        Assert.NotNull(ProjectAutosave.Read(folder));
    }

    [Fact]
    public void Keeping_a_snapshot_moves_it_to_a_backup() {
        var (saved, _, _) = RandomProjects.Build(3, 30);
        var (edited, _, _) = RandomProjects.Build(4, 30);
        ProjectPackage.Save(folder, saved);
        ProjectAutosave.Write(folder, edited, NoStates, start);

        var id = ProjectAutosave.Keep(folder, start.AddMinutes(1));

        Assert.Null(ProjectAutosave.Read(folder));
        Assert.False(Directory.Exists(Autosave));
        Assert.Equal(id, Assert.Single(ProjectHistory.Backups(folder)).Id);
        Assert.Equal(
            RandomProjects.Json(edited),
            RandomProjects.Json(ProjectPackage.Load(ProjectHistory.BackupFolder(folder, id!)!))
        );
    }

    [Fact]
    public void Keeping_nothing_makes_no_backup() {
        ProjectPackage.Save(folder, RandomProjects.Build(3, 30).State);
        Assert.Null(ProjectAutosave.Keep(folder, start));
        Assert.Empty(ProjectHistory.Backups(folder));
    }

    [Fact]
    public void A_clear_stopped_after_the_manifest_went_leaves_nothing_to_offer_and_room_for_the_next() {
        var (state, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        ProjectAutosave.Write(folder, state, NoStates, start);
        File.Delete(Path.Combine(Autosave, ProjectAutosave.ManifestName));

        Assert.Null(ProjectAutosave.Read(folder));

        ProjectAutosave.Write(folder, state, NoStates, start);
        Assert.NotNull(ProjectAutosave.Recoverable(folder));
    }

    [Theory]
    [InlineData("{ torn")]
    [InlineData("""{ "slot": "../..", "projectId": "p", "name": "n" }""")]
    public void A_damaged_manifest_offers_nothing(string manifest) {
        var (state, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        ProjectAutosave.Write(folder, state, NoStates, start);
        File.WriteAllText(Path.Combine(Autosave, ProjectAutosave.ManifestName), manifest, Encoding.UTF8);

        Assert.Null(ProjectAutosave.Read(folder));
    }

    string Autosave => Path.Combine(folder, ProjectPackage.AutosaveFolder);

    string ProjectFile => Path.Combine(folder, ProjectPackage.ProjectFileName);

    ProjectState Snapshot() => ProjectPackage.Load(ProjectAutosave.Document(folder, ProjectAutosave.Read(folder)!));

    /// <summary>The bytes of the saved document, by relative path.</summary>
    SortedDictionary<string, string> SavedDocument() {
        var tracks = Directory.EnumerateFiles(Path.Combine(folder, ProjectPackage.TracksFolder));
        return new(
            tracks.Append(ProjectFile).ToDictionary(p => Path.GetRelativePath(folder, p), File.ReadAllText),
            StringComparer.Ordinal
        );
    }

    sealed class Interrupted : Exception;
}
