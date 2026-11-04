using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>What a package keeps of what the disk lacks, and what may stand in for a file.</summary>
public sealed class OfflineFilesTests : IDisposable {
    const string FileId = "fil_kick";

    static readonly AudioFile Kick = new(
        FileId,
        "Kick",
        "Audio Files/Kick.wav",
        48_000,
        2,
        48_000,
        24,
        null,
        0
    );

    readonly string folder = Directory.CreateTempSubdirectory("anode-offline-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    static ProjectState WithRegion() {
        var state = ProjectDefaults.NewProject("prj_test", "Song", 48_000).Apply(new AudioFileUpserted(Kick));
        state = Edits.Apply(state, new AddTrackCommand("Drums"), RandomIds.Instance).State;
        return Edits.Apply(
                state,
                new AddRegionCommand(state.Tracks[0].Id, FileId, 960, 1_000, 5_000),
                RandomIds.Instance
            )
            .State;
    }

    string TrackPath(ProjectState state) =>
        Path.Combine(folder, ProjectPackage.TracksFolder, ProjectPackage.TrackFileName(state.Tracks[0].Id));

    static void Rewrite(string path, Action<JsonObject> change) {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    void Unlist() =>
        Rewrite(
            Path.Combine(folder, ProjectPackage.ProjectFileName),
            static root => root["audioFiles"] = new JsonArray()
        );

    static WavInfo Wav(int channels = 2, int rate = 48_000, long frames = 48_000) =>
        new(channels, rate, WavEncoding.Pcm16, 44, frames);

    [Fact]
    public void A_file_on_the_list_but_not_on_disk_is_missing_and_its_region_stays() {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);

        var loaded = ProjectPackage.Load(folder);

        Assert.Single(loaded.Regions);
        var offline = Assert.Single(OfflineFiles.Report(folder, loaded).Files);
        Assert.Equal((FileId, OfflineReason.Missing), (offline.FileId, offline.Reason));
    }

    [Fact]
    public void A_file_the_project_lost_comes_back_unlisted_and_a_save_keeps_it() {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);
        Unlist();

        var loaded = ProjectPackage.Load(folder);

        var region = Assert.Single(loaded.Regions.Values);
        var file = loaded.Files[region.FileId];
        Assert.True(OfflineFiles.IsUnlisted(file));
        Assert.Equal((48_000, 6_000L), (file.SampleRate, file.Frames));
        Assert.Equal(OfflineReason.Unlisted, Assert.Single(OfflineFiles.Report(folder, loaded).Files).Reason);

        ProjectPackage.Save(folder, loaded);
        Assert.Equal(RandomProjects.Json(loaded), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void Takes_and_frozen_tracks_keep_a_lost_file_too() {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);
        Unlist();
        Rewrite(
            TrackPath(state),
            json => {
                json["regions"] = new JsonArray();
                json["track"]!["frozenFileId"] = "fil_frozen";
                json["takeFolders"] = JsonNode.Parse(
                    $$"""
                      [{"id":"fld_1","trackId":"{{state.Tracks[0].Id}}","name":"Takes","startTick":0,"lengthTicks":960,
                        "takes":[{"id":"tak_1","name":"Take 1","fileId":"fil_take","startTick":0,"fileOffset":10,"length":90}],
                        "comp":[]}]
                      """
                );
            }
        );

        var loaded = ProjectPackage.Load(folder);

        Assert.Single(loaded.TakeFolders);
        Assert.Equal("fil_frozen", loaded.Tracks[0].FrozenFileId);
        Assert.Equal(100, loaded.Files["fil_take"].Frames);
        Assert.Equal(
            ["fil_frozen", "fil_take"],
            OfflineFiles.Report(folder, loaded).Files.Select(static f => f.FileId)
        );
    }

    [Fact]
    public void A_region_naming_a_track_that_is_not_there_stays_on_the_track_that_holds_it() {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);
        Rewrite(TrackPath(state), static json => json["regions"]![0]!["trackId"] = "trk_gone");

        var loaded = ProjectPackage.Load(folder);

        Assert.Equal(state.Tracks[0].Id, Assert.Single(loaded.Regions.Values).TrackId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_keeps_a_track_it_could_not_read(bool damaged) {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);
        var path = TrackPath(state);
        var good = File.ReadAllText(path);
        if (damaged) {
            File.WriteAllText(path, "{ not json");
        } else {
            File.Delete(path);
        }

        var loaded = ProjectPackage.Load(folder);
        Assert.Empty(loaded.Tracks);
        Assert.Equal(state.Tracks[0].Id, Assert.Single(OfflineFiles.Report(folder, loaded).UnreadableTracks));

        ProjectPackage.Save(folder, loaded);

        Assert.Equal(damaged, File.Exists(path));
        File.WriteAllText(path, good);
        Assert.Single(ProjectPackage.Load(folder).Regions);
    }

    [Fact]
    public void A_file_that_differs_from_the_one_the_project_knows_has_changed() {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);
        WriteWav(Path.Combine(folder, Kick.Path), 2, 48_000, 100);

        var offline = Assert.Single(OfflineFiles.Report(folder, state).Files);

        Assert.Equal(OfflineReason.Changed, offline.Reason);
        Assert.Contains("100 frames", offline.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_has_changed_and_one_that_matches_is_online() {
        var state = WithRegion();
        ProjectPackage.Save(folder, state);
        File.WriteAllText(Path.Combine(folder, Kick.Path), "not audio");
        Assert.Equal(OfflineReason.Changed, Assert.Single(OfflineFiles.Report(folder, state).Files).Reason);

        WriteWav(Path.Combine(folder, Kick.Path), 2, 48_000, 48_000);

        Assert.Empty(OfflineFiles.Report(folder, state).Files);
    }

    [Fact]
    public void A_candidate_must_match_in_length_rate_and_channels() {
        Assert.Null(OfflineFiles.Mismatch(Kick, Wav()));
        Assert.Contains("44100 Hz", OfflineFiles.Mismatch(Kick, Wav(rate: 44_100)), StringComparison.Ordinal);
        Assert.Contains("1 channels", OfflineFiles.Mismatch(Kick, Wav(1)), StringComparison.Ordinal);
        Assert.Contains("47999 frames", OfflineFiles.Mismatch(Kick, Wav(frames: 47_999)), StringComparison.Ordinal);
        Assert.NotNull(OfflineFiles.Mismatch(Kick, Wav(frames: 48_001)));
    }

    [Fact]
    public void A_candidate_for_a_lost_file_needs_its_rate_and_to_reach_as_far_as_its_regions() {
        var lost = Kick with { Channels = 0, BitDepth = 0, Frames = 6_000 };

        Assert.Null(OfflineFiles.Mismatch(lost, Wav(1, frames: 6_000)));
        Assert.Null(OfflineFiles.Mismatch(lost, Wav(16, frames: 90_000)));
        Assert.NotNull(OfflineFiles.Mismatch(lost, Wav(frames: 5_999)));
        Assert.NotNull(OfflineFiles.Mismatch(lost, Wav(rate: 44_100)));
        Assert.NotNull(OfflineFiles.Mismatch(lost, Wav(17)));
    }

    [Fact]
    public void A_search_finds_files_by_name_inside_folders_and_judges_them_by_their_content() {
        var deep = Directory.CreateDirectory(Path.Combine(folder, "a", "b")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(folder, "c")).FullName;
        var hidden = Directory.CreateDirectory(Path.Combine(folder, ".hidden")).FullName;
        WriteWav(Path.Combine(deep, "KICK.wav"), 2, 48_000, 48_000);
        WriteWav(Path.Combine(other, "Kick.wav"), 1, 48_000, 48_000);
        WriteWav(Path.Combine(other, "Snare.wav"), 2, 48_000, 48_000);
        WriteWav(Path.Combine(hidden, "Kick.wav"), 2, 48_000, 48_000);
        File.WriteAllText(Path.Combine(deep, "kick.txt"), "no");

        var search = OfflineFiles.Search(folder, [Kick]);

        Assert.False(search.Truncated);
        Assert.Equal(2, search.Candidates.Count);
        Assert.Equal(Path.Combine(deep, "KICK.wav"), search.Candidates[0].Path);
        Assert.Null(search.Candidates[0].Problem);
        Assert.Contains("1 channels", search.Candidates[1].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_knows_an_edited_version_by_its_original_name() {
        WriteWav(Path.Combine(folder, "Kick.wav"), 2, 48_000, 48_000);

        var search = OfflineFiles.Search(folder, [Kick with { Path = "Audio Files/Kick.v3.wav", Name = "Hit" }]);

        Assert.Null(Assert.Single(search.Candidates).Problem);
    }

    static void WriteWav(string path, int channels, int rate, long frames) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new WavWriter(path, channels, rate, WavEncoding.Pcm16);
        writer.Write(new float[frames * channels]);
    }
}
