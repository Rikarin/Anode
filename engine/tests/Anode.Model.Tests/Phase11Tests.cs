using Anode.Contracts;
using Anode.Dsp.Analysis.Chords;
using Anode.Dsp.Mastering;
using Anode.Ipc;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     The Phase 11 contracts (docs/12-phase-11.md) before workstream B implements them: the model refuses the new
///     commands and the Session Player track kinds; projects have an empty chord track and Environment and no Session
///     Player regions, every region's Pitch Source is off, and schema-10 packages read so. Also what the contract, the
///     DSP and the IPC each spell out (chord qualities, mastering bands and parameters, Environment kinds), and the
///     contract's own arithmetic (pitch classes, root intervals, section kinds).
/// </summary>
public sealed class Phase11Tests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-phase11-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void The_model_refuses_the_Phase_11_commands_until_workstream_B() {
        var (state, ids, random) = RandomProjects.Build(11, 40);
        var refused = 0;
        foreach (var type in RandomProjects.CommandTypes.Where(static t =>
                     RandomProjects.PendingPhase11.Contains(t.Name)
                 )) {
            if (RandomProjects.Command(type, state, random) is not { } command) {
                continue;
            }

            var e = Assert.Throws<EditException>(() => Edits.Apply(state, command, ids, RandomProjects.Plugins));
            Assert.Contains("Phase 11", e.Message, StringComparison.Ordinal);
            refused++;
        }

        Assert.Equal(RandomProjects.PendingPhase11.Count, refused);
    }

    [Fact]
    public void Projects_have_no_chords_players_or_Environment_and_regions_do_not_follow() {
        var ids = new SequentialIds();
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_1", "Song", 48_000),
                new AddTrackCommand(Kind: TrackKind.Instrument),
                ids,
                RandomProjects.Plugins
            )
            .State;
        state = Edits.Apply(state, new AddMidiRegionCommand(state.Tracks[0].Id, 0, 3840), ids).State;
        var project = state.ToProject(string.Empty, 0, false);
        Assert.Equal(ChordTrack.Empty, project.ChordTrack);
        Assert.Empty(project.SessionPlayerRegions);
        Assert.Equal(MidiEnvironment.Empty, project.Environment);
        var region = Assert.Single(project.MidiRegions);
        Assert.Equal((PitchSourceMode.Off, null), (region.PitchSource, region.Key));
        foreach (var kind in (TrackKind[])[TrackKind.Drummer, TrackKind.BassPlayer, TrackKind.KeyboardPlayer]) {
            var e = Assert.Throws<EditException>(() =>
                Edits.Apply(state, new AddTrackCommand(Kind: kind), ids, RandomProjects.Plugins)
            );
            Assert.Contains("Phase 11", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_schema_10_package_without_the_Phase_11_fields_reads_with_Pitch_Source_off() {
        var (state, _, _) = RandomProjects.Build(5, 60);
        ProjectPackage.Save(folder, state);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "tracks"))) {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var list in (string[])["regions", "midiRegions"]) {
                foreach (var region in (json[list] as JsonArray ?? []).OfType<JsonObject>()) {
                    region.Remove("pitchSource");
                    region.Remove("key");
                }
            }

            File.WriteAllText(file, json.ToJsonString());
        }

        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.All(loaded.MidiRegions.Values, static r => Assert.Equal(PitchSourceMode.Off, r.PitchSource));
        Assert.All(loaded.Regions.Values, static r => Assert.Null(r.Key));
    }

    [Fact]
    public void The_chord_qualities_agree_across_the_contract_and_Chord_ID() {
        Assert.Equal(ChordQualities.All, ChordTemplates.Qualities);
        for (var i = 0; i < ChordTemplates.Qualities.Count; i++) {
            Assert.Equal(ChordQualities.Intervals(ChordQualities.All[i]), ChordTemplates.Intervals(i).ToArray());
        }

        Assert.Equal(ChordQualities.All.Count, ChordQualities.All.Distinct().Count());
        Assert.True(ChordQualities.IsKnown("m7b5"));
        Assert.False(ChordQualities.IsKnown("min"));
        Assert.Empty(ChordQualities.Intervals("min"));
    }

    [Fact]
    public void The_Mastering_Assistant_agrees_across_the_contract_and_the_DSP() {
        Assert.Equal(30, Contracts.MasteringBands.CentresHz.Count);
        Assert.Equal(
            Contracts.MasteringBands.CentresHz,
            Dsp.Mastering.MasteringBands.CentresHz.Select(static f => (double)f)
        );
        Assert.Equal(Enum.GetNames<MasteringCharacter>(), Enum.GetNames<MasteringCharacterKind>());
        Assert.Equal(MasteringParameters.PluginId, MasteringAssistant.Id);
        Assert.Equal(
            [
                MasteringParameters.Character, MasteringParameters.TargetLufs, MasteringParameters.CeilingDbtp,
                MasteringParameters.Eq, MasteringParameters.Dynamics, MasteringParameters.Width,
                MasteringParameters.MatchedBypass
            ],
            MasteringAssistant.Info.Parameters.Select(static p => p.Id)
        );
        var defaults = MasteringAssistant.Info.Parameters.ToDictionary(static p => p.Id, static p => (double)p.Default);
        var settings = MasteringSettings.Default;
        Assert.Equal(
            (settings.TargetLufs, settings.CeilingDbtp, settings.Eq, settings.Dynamics, settings.Width),
            (defaults["targetLufs"], defaults["ceilingDbtp"], defaults["eq"], defaults["dynamics"], defaults["width"])
        );
    }

    [Fact]
    public void The_Environment_kinds_agree_across_the_contract_and_the_IPC() {
        Assert.Equal(Enum.GetNames<EnvironmentObjectKind>(), Enum.GetNames<EngineEnvironmentKind>());
        Assert.Equal(Enum.GetValues<EnvironmentObjectKind>(), EnvironmentObjectKinds.All.Select(static k => k.Kind));
        Assert.Equal(17, EnvironmentObjectKinds.Of(EnvironmentObjectKind.ChannelSplitter).Outlets.Count);
        Assert.Empty(EnvironmentObjectKinds.Of(EnvironmentObjectKind.PhysicalInput).Inlets);
        Assert.Empty(EnvironmentObjectKinds.Of(EnvironmentObjectKind.SequencerInput).Outlets);
        Assert.Equal(2, EnvironmentObjectKinds.Of(EnvironmentObjectKind.Transformer).Outlets.Count);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 7, -5)]
    [InlineData(0, 5, 5)]
    [InlineData(0, 6, -6)]
    [InlineData(9, 0, 3)]
    [InlineData(11, 1, 2)]
    [InlineData(2, 11, -3)]
    public void Pitch_Source_transposes_by_the_shortest_interval(int from, int to, int interval) =>
        Assert.Equal(interval, PitchSources.RootInterval(from, to));

    [Fact]
    public void Spelled_pitches_have_their_classes() {
        Assert.Equal(0, PitchSources.PitchClass(new(PitchStep.C, 0)));
        Assert.Equal(11, PitchSources.PitchClass(new(PitchStep.C, -1)));
        Assert.Equal(0, PitchSources.PitchClass(new(PitchStep.B, 1)));
        Assert.Equal(10, PitchSources.PitchClass(new(PitchStep.B, -1)));
        Assert.Equal(6, PitchSources.PitchClass(new(PitchStep.F, 1)));
    }

    [Theory]
    [InlineData("Intro", SectionKind.Intro)]
    [InlineData("Chorus 2", SectionKind.Chorus)]
    [InlineData("pre-chorus", SectionKind.Chorus)]
    [InlineData("Bridge", SectionKind.Bridge)]
    [InlineData("Ending", SectionKind.Outro)]
    [InlineData("Solo", SectionKind.Verse)]
    [InlineData(null, SectionKind.Verse)]
    public void Section_kinds_come_from_section_names(string? name, SectionKind kind) =>
        Assert.Equal(kind, SectionKinds.Of(name));

    [Fact]
    public void Every_kit_piece_has_notes_and_none_shares_one() {
        var notes = Enum.GetValues<DrumPiece>().SelectMany(DrumKitNotes.Of).ToList();
        Assert.Equal(notes.Count, notes.Distinct().Count());
        Assert.All(Enum.GetValues<DrumPiece>(), static p => Assert.NotEmpty(DrumKitNotes.Of(p)));
        Assert.Equal(DrumKitNotes.Kick, DrumKitNotes.Of(DrumPiece.Kick)[0]);
    }
}
