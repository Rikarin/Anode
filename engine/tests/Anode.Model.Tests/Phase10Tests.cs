using Anode.Contracts;
using Anode.Dsp.Spatial;
using Anode.Ipc;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     The Phase 10 contracts (docs/11-phase-10.md) before workstream B implements them: the model refuses the new
///     commands, scene markers and imported formats, panners and automation; projects are stereo with no movie, and
///     schema-9 packages read so. Also the speaker layouts, which the contract, the DSP and the IPC each spell out.
/// </summary>
public sealed class Phase10Tests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-phase10-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void The_model_refuses_the_Phase_10_commands_until_workstream_B() {
        var (state, ids, random) = RandomProjects.Build(7, 40);
        var refused = 0;
        foreach (var type in RandomProjects.CommandTypes.Where(static t =>
                     RandomProjects.PendingPhase10.Contains(t.Name)
                 )) {
            if (RandomProjects.Command(type, state, random) is not { } command) {
                continue;
            }

            var e = Assert.Throws<EditException>(() => Edits.Apply(state, command, ids, RandomProjects.Plugins));
            Assert.Contains("Phase 10", e.Message, StringComparison.Ordinal);
            refused++;
        }

        Assert.Equal(RandomProjects.PendingPhase10.Count, refused);
    }

    [Fact]
    public void Projects_are_stereo_without_a_movie_and_scene_markers_wait_for_workstream_B() {
        var ids = new SequentialIds();
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_1", "Song", 48_000),
                new AddTrackCommand(Kind: TrackKind.Instrument),
                ids,
                RandomProjects.Plugins
            )
            .State;
        var project = state.ToProject(string.Empty, 0, false);
        Assert.Equal(ChannelFormat.Stereo, project.Settings.SurroundFormat);
        Assert.Null(project.Movie);
        foreach (var strip in new[] { project.Master, project.Tracks[0].Strip }) {
            Assert.Equal((ChannelFormat.Stereo, ChannelFormat.Stereo), (strip.InputFormat, strip.OutputFormat));
            Assert.Equal(Panner.Default, strip.Panner);
        }

        state = Edits.Apply(state, new AddMarkerCommand("Verse", 3840), ids).State;
        var marker = Assert.Single(state.Markers.Values);
        Assert.Equal(MarkerKind.Marker, marker.Kind);
        EditCommand[] refused = [
            new AddMarkerCommand("Cut", 0, Kind: MarkerKind.Scene),
            new SetMarkerCommand(marker.Id, Kind: MarkerKind.Scene),
            new ImportTracksCommand(
                new(
                    [
                        new(
                            "t1",
                            "Object",
                            TrackKind.Audio,
                            null,
                            [],
                            [],
                            Panner: Panner.Default with { Mode = PannerMode.Object }
                        )
                    ]
                )
            )
        ];
        foreach (var command in refused) {
            var e = Assert.Throws<EditException>(() => Edits.Apply(state, command, ids, RandomProjects.Plugins));
            Assert.Contains("Phase 10", e.Message, StringComparison.Ordinal);
        }

        state = Edits.Apply(state, new SetMarkerCommand(marker.Id, "Chorus", Kind: MarkerKind.Marker), ids).State;
        Assert.Equal("Chorus", state.Markers[marker.Id].Name);
    }

    [Fact]
    public void A_schema_9_package_without_the_Phase_10_fields_reads_stereo() {
        var (state, ids, _) = RandomProjects.Build(5, 40);
        state = Edits.Apply(state, new AddMarkerCommand("Verse", 3840), ids).State;
        ProjectPackage.Save(folder, state);
        var path = Path.Combine(folder, "project.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["schemaVersion"] = 9;
        root["settings"]!.AsObject().Remove("surroundFormat");
        Strip(root["master"]!.AsObject());
        foreach (var marker in root["markers"]!.AsArray()) {
            marker!.AsObject().Remove("kind");
        }

        File.WriteAllText(path, root.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "tracks"))) {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["schemaVersion"] = 9;
            Strip(json["track"]!["strip"]!.AsObject());
            File.WriteAllText(file, json.ToJsonString());
        }

        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.Equal(ChannelFormat.Stereo, loaded.Settings.SurroundFormat);
        Assert.All(
            loaded.Tracks.Select(static t => t.Strip).Append(loaded.Master),
            static s => Assert.Equal(
                (ChannelFormat.Stereo, ChannelFormat.Stereo, Panner.Default),
                (s.InputFormat, s.OutputFormat, s.Panner)
            )
        );
        Assert.All(loaded.Markers.Values, static m => Assert.Equal(MarkerKind.Marker, m.Kind));

        static void Strip(JsonObject strip) {
            strip.Remove("inputFormat");
            strip.Remove("outputFormat");
            strip.Remove("panner");
        }
    }

    [Fact]
    public void The_speaker_layouts_agree_across_the_contract_the_DSP_and_the_IPC() {
        Assert.Equal(Enum.GetNames<ChannelFormat>(), Enum.GetNames<SpeakerFormat>());
        Assert.Equal(Enum.GetNames<ChannelFormat>(), Enum.GetNames<EngineChannelFormat>());
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16],
            SpeakerLayouts.All.Select(static l => l.ChannelCount)
        );
        foreach (var layout in SpeakerLayouts.All) {
            var dsp = Dsp.Spatial.SpeakerLayout.Of((SpeakerFormat)layout.Format);
            Assert.Equal(layout.ChannelCount, dsp.ChannelCount);
            Assert.Equal(layout.Speakers.ToList().FindIndex(static s => s.Lfe), dsp.LfeIndex);
            Assert.Equal(layout.HasHeight, dsp.HasHeight);
            for (var c = 0; c < layout.ChannelCount; c++) {
                var speaker = layout.Speakers[c];
                Assert.Equal(
                    (speaker.Label, speaker.Azimuth, speaker.Elevation),
                    (dsp.Label(c), (double)dsp.Azimuths[c], (double)dsp.Elevations[c])
                );
            }

            Assert.Equal(layout.Speakers.Count, layout.Speakers.Select(static s => s.Label).Distinct().Count());
        }

        Assert.Equal(["M+030", "M-030"], SpeakerLayouts.Of(ChannelFormat.Stereo).Speakers.Select(static s => s.Label));
        Assert.Equal(7, SpeakerLayouts.Of(ChannelFormat.Surround714).IndexOf(SpeakerLabels.RightRear));
    }

    [Fact]
    public void A_listen_in_frame_header_round_trips() {
        var frame = new byte[ListenIn.HeaderBytes + 3];
        ListenIn.WriteHeader(frame, 7, 123_456_789_012, 3, ListenIn.FlagPlaying);
        frame.AsSpan()[^3..].Fill(9);
        Assert.True(ListenIn.TryReadHeader(frame, out var sequence, out var sample, out var flags, out var packet));
        Assert.Equal((7u, 123_456_789_012L, ListenIn.FlagPlaying), (sequence, sample, flags));
        Assert.Equal([9, 9, 9], packet.ToArray());
        Assert.False(ListenIn.TryReadHeader(frame.AsSpan(0, ListenIn.HeaderBytes + 2), out _, out _, out _, out _));
    }
}
