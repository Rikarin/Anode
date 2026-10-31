using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Storage;

namespace Anode.Model.Tests;

/// <summary>Phase 8's model (docs/09-phase-8.md): the project's sync settings and the Stem Splitter's result.</summary>
public sealed class Phase8Tests : IDisposable {
    readonly SequentialIds ids = new();
    readonly string folder = Directory.CreateTempSubdirectory("anode-phase8-").FullName;
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    public void Dispose() => Directory.Delete(folder, true);

    EditResult Apply(params EditCommand[] commands) {
        var result = Edits.Apply(state, commands, ids, RandomProjects.Plugins);
        state = result.State;
        return result;
    }

    [Fact]
    public void Sync_settings_change_what_is_given_and_keep_the_rest() {
        Assert.Equal(SyncSettings.Default, state.Settings.Sync);
        Apply(
            new SetSyncSettingsCommand(
                SyncSource.MidiClock,
                [new("out-1", true, false, -3.5), new("out-2", false, true, 10)],
                ClockInPort: "in-1"
            )
        );
        Apply(new SetSyncSettingsCommand(FrameRate: MtcFrameRate.Fps2997Drop, StartOffsetSeconds: 3600));
        var sync = state.Settings.Sync;
        Assert.Equal(
            (SyncSource.MidiClock, MtcFrameRate.Fps2997Drop, 3600d, "in-1", (string?)null),
            (sync.Source, sync.FrameRate, sync.StartOffsetSeconds, sync.ClockInPort, sync.MtcInPort)
        );
        Assert.Equal(["out-1", "out-2"], sync.Destinations.Select(static d => d.Port));

        // An empty port is any input again; an empty list sends nothing.
        var result = Apply(new SetSyncSettingsCommand(Destinations: [], ClockInPort: string.Empty));
        Assert.Empty(state.Settings.Sync.Destinations);
        Assert.Null(state.Settings.Sync.ClockInPort);
        Assert.Equal(sync, state.Apply(result.Inverse).Settings.Sync);
    }

    [Theory]
    [InlineData(-1d, 0d, "a")]
    [InlineData(86_400d, 0d, "a")]
    [InlineData(0d, 50.5d, "a")]
    [InlineData(0d, 0d, "")]
    public void Sync_settings_out_of_range_are_refused(double offset, double delay, string port) {
        Assert.Throws<EditException>(() => Apply(
                new SetSyncSettingsCommand(StartOffsetSeconds: offset, Destinations: [new(port, true, true, delay)])
            )
        );
        Assert.Throws<EditException>(() => Apply(
                new SetSyncSettingsCommand(Destinations: [new("a", true, true, 0), new("a", false, true, 0)])
            )
        );
        Assert.Throws<EditException>(() => Apply(new SetSyncSettingsCommand((SyncSource)42)));
    }

    (string Track, string Region, string[] Files) Source() {
        Apply(new AddTrackCommand("Mix", Stereo: true), new AddTrackCommand("After"));
        Apply(new SetOutputCommand(state.Tracks[0].Id, "output:2"));
        foreach (var name in new[] { "Mix", "Mix vocals", "Mix drums" }) {
            state = state.Apply(
                new AudioFileUpserted(
                    new(ids.Next(IdPrefix.File), name, $"Audio Files/{name}.wav", 48_000, 2, 96_000, 24, null, 0)
                )
            );
        }

        var files = state.Files.Keys.Order(StringComparer.Ordinal).ToArray();
        Apply(new AddRegionCommand(state.Tracks[0].Id, files[0], 1920, 4800, 48_000));
        return (state.Tracks[0].Id, state.Regions.Keys.Single(), files);
    }

    [Fact]
    public void Stems_become_tracks_below_the_source_with_regions_at_its_position_and_the_source_muted() {
        var (track, region, files) = Source();
        var before = state;
        var result = Apply(
            new AddStemsCommand(region, [new(StemKind.Vocals, files[1]), new(StemKind.Drums, files[2])], false)
        );
        Assert.Equal(["Mix", "Mix – Vocals", "Mix – Drums", "After"], state.Tracks.Select(static t => t.Name));
        Assert.All(state.Tracks.Skip(1).Take(2), static t => Assert.Equal("output:2", t.Strip.Output));
        Assert.All(state.Tracks.Skip(1).Take(2), static t => Assert.True(t.Input.Stereo));
        var stems = state.Regions.Values.Where(r => r.Id != region).OrderBy(static r => r.FileId).ToArray();
        Assert.Equal([files[1], files[2]], stems.Select(static r => r.FileId));
        Assert.All(stems, static r => Assert.Equal((1920L, 0L, 96_000L), (r.StartTick, r.FileOffset, r.Length)));
        Assert.Equal(state.Tracks[1].Id, stems[0].TrackId);
        Assert.True(state.Regions[region].Muted);
        Assert.Equal(RandomProjects.Json(before), RandomProjects.Json(state.Apply(result.Inverse)));
        Assert.Equal(track, state.Tracks[0].Id);
    }

    [Fact]
    public void Keeping_the_source_leaves_it_playing_and_bad_stems_are_refused() {
        var (_, region, files) = Source();
        Apply(new AddStemsCommand(region, [new(StemKind.Bass, files[1])], true));
        Assert.False(state.Regions[region].Muted);
        Assert.Throws<EditException>(() => Apply(new AddStemsCommand(region, [], false)));
        Assert.Throws<EditException>(() => Apply(
                new AddStemsCommand(region, [new(StemKind.Bass, files[1]), new(StemKind.Bass, files[2])], false)
            )
        );
        Assert.Throws<EditException>(() => Apply(new AddStemsCommand(region, [new(StemKind.Bass, "fil_gone")], false)));
        Assert.Throws<EditException>(() => Apply(new AddStemsCommand("reg_gone", [new(StemKind.Bass, files[1])], false))
        );
    }

    [Fact]
    public void Stem_track_names_fit_the_name_length() {
        Assert.Equal("Piano", Edits.StemTrackName("  ", StemKind.Piano));
        var name = Edits.StemTrackName(new('x', 200), StemKind.Guitar);
        Assert.Equal(Edits.MaxNameLength, name.Length);
        Assert.EndsWith(" – Guitar", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_8_content_survives_the_package_format() {
        var (_, region, files) = Source();
        Apply(
            new SetSyncSettingsCommand(
                SyncSource.Link,
                [new("out-1", true, true, 12.5)],
                MtcFrameRate.Fps24,
                61,
                true,
                "clock",
                "mtc"
            ),
            new AddStemsCommand(region, [new(StemKind.Other, files[1])], false)
        );
        ProjectPackage.Save(folder, state);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
    }
}
