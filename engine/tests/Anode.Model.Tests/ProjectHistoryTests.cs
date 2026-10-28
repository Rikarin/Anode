using Anode.Model.Storage;

namespace Anode.Model.Tests;

/// <summary>Backups and alternatives of a package (docs/04-phase-3.md, "Alternatives and backups").</summary>
public sealed class ProjectHistoryTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-history-").FullName;
    readonly DateTimeOffset start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void Each_backup_copies_the_saved_document_and_the_newest_ten_are_kept() {
        Assert.Null(ProjectHistory.Backup(folder, start)); // nothing saved yet
        var (state, _, _) = RandomProjects.Build(5, 30);
        ProjectPackage.Save(folder, state);
        for (var i = 0; i < 12; i++) {
            ProjectHistory.Backup(folder, start.AddMinutes(i));
        }

        var backups = ProjectHistory.Backups(folder);
        Assert.Equal(10, backups.Count);
        Assert.Equal(start.AddMinutes(11), backups[0].Saved);
        Assert.Equal(start.AddMinutes(2), backups[^1].Saved);
        var copy = ProjectHistory.BackupFolder(folder, backups[0].Id)!;
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(copy)));
        Assert.False(Directory.Exists(Path.Combine(copy, ProjectPackage.AudioFolder)));
        Assert.Null(ProjectHistory.BackupFolder(folder, "../escape"));
    }

    [Fact]
    public void Two_backups_in_the_same_millisecond_both_stay() {
        ProjectPackage.Save(folder, RandomProjects.Build(1).State);
        var a = ProjectHistory.Backup(folder, start);
        var b = ProjectHistory.Backup(folder, start);
        Assert.NotEqual(a, b);
        Assert.Equal(2, ProjectHistory.Backups(folder).Count);
    }

    [Fact]
    public void Alternatives_swap_documents_with_the_root() {
        var (first, _, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, first);
        var initial = ProjectHistory.Alternatives(folder, "Song");
        Assert.Equal(ProjectHistory.FirstAlternative, initial.Current);
        Assert.Equal("Song", Assert.Single(initial.Alternatives).Name);

        // A new alternative: the first is kept as saved, the root becomes the second.
        ProjectHistory.CreateAlternative(folder, "Song", "alt_two", "Two", start);
        var (second, _, _) = RandomProjects.Build(4, 30);
        ProjectPackage.Save(folder, second);
        var manifest = ProjectHistory.Alternatives(folder, "Song");
        Assert.Equal("alt_two", manifest.Current);
        Assert.Equal(["Song", "Two"], manifest.Alternatives.Select(static a => a.Name));

        ProjectHistory.OpenAlternative(folder, "Song", ProjectHistory.FirstAlternative, start);
        Assert.Equal(RandomProjects.Json(first), RandomProjects.Json(ProjectPackage.Load(folder)));
        ProjectHistory.OpenAlternative(folder, "Song", "alt_two", start);
        Assert.Equal(RandomProjects.Json(second), RandomProjects.Json(ProjectPackage.Load(folder)));

        Assert.Throws<PackageException>(() => ProjectHistory.DeleteAlternative(folder, "Song", "alt_two"));
        ProjectHistory.DeleteAlternative(folder, "Song", ProjectHistory.FirstAlternative);
        Assert.Equal(["Two"], ProjectHistory.Alternatives(folder, "Song").Alternatives.Select(static a => a.Name));
        Assert.Throws<PackageException>(() => ProjectHistory.OpenAlternative(
                folder,
                "Song",
                ProjectHistory.FirstAlternative,
                start
            )
        );
    }
}
