using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Storage;
using CsCheck;

namespace Anode.Model.Tests;

/// <summary>What a change of sample rate does to what a project stores in frames, and how its document is replaced.</summary>
public sealed class ProjectRateTests : IDisposable {
    const int From = 44_100;
    const int To = 48_000;
    readonly string folder = Directory.CreateTempSubdirectory("anode-rate-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    static AudioRegion Region(string id, string fileId, long offset, long length) =>
        new(
            id,
            "trk_a",
            fileId,
            id,
            960,
            offset,
            length,
            0,
            441,
            882,
            0,
            0,
            false,
            FlexFollow.Off,
            [new(offset, 960), new(offset + 1, 1000), new(offset + 2, 1100)],
            0,
            0,
            [new(offset, offset + 4410, 6900, 6950, 1, 1, 0, 0)],
            PitchSourceMode.Off,
            null
        );

    static AudioFile File44(string id, long frames) =>
        new(id, id, $"Audio Files/{id}.wav", From, 2, frames, 24, new(120, false, [22_050, 44_100], [44_100]), 0);

    /// <summary>A project with every place frames are stored: regions, takes, an alternative, a frozen file and a cell.</summary>
    static ProjectState EveryPlace() {
        var state = ProjectDefaults.NewProject("prj_rate", "Rate", From);
        var file = File44("aud_a", 441_000);
        var track = new Track(
            "trk_a",
            "A",
            TrackKind.Audio,
            "#fff",
            new(1, false),
            false,
            false,
            ProjectDefaults.Strip,
            null,
            ProjectDefaults.MidiInput,
            null,
            null,
            false,
            string.Empty,
            0,
            AutomationMode.Read,
            "aud_a",
            false,
            FlexMode.Polyphonic,
            [
                new(
                    "alt_a",
                    "Other",
                    [Region("rgn_alt", "aud_a", 100, 4410)],
                    [],
                    [
                        new(
                            "tkf_alt",
                            "trk_a",
                            "Alt",
                            0,
                            960,
                            [new("tak_alt", "Take", "aud_a", 0, 4410, 8820)],
                            [],
                            441,
                            false
                        )
                    ]
                )
            ],
            "alt_a",
            null,
            null,
            false,
            false,
            null,
            null
        );
        var folder = new TakeFolder(
            "tkf_a",
            "trk_a",
            "Takes",
            0,
            960,
            [new("tak_a", "Take", "aud_a", 0, 441, 44_100)],
            [],
            441,
            false
        );
        return state with {
            Tracks = [track],
            Files = state.Files.Add(file.Id, file),
            Regions = state.Regions.Add("rgn_a", Region("rgn_a", "aud_a", 22_050, 44_100)),
            TakeFolders = state.TakeFolders.Add(folder.Id, folder),
            LiveLoops = state.LiveLoops with {
                Scenes = [new("scn_a", "Scene")],
                Cells = [
                    new(
                        "cel_a",
                        "trk_a",
                        "scn_a",
                        CellKind.Audio,
                        "Cell",
                        960,
                        CellPlayMode.Loop,
                        null,
                        new(Region("rgn_cell", "aud_a", 441, 441))
                    )
                ]
            }
        };
    }

    static Dictionary<string, AudioFile> Converted(ProjectState state, string id = "aud_new") {
        var source = state.Files["aud_a"];
        var written = new WavInfo(2, To, WavEncoding.Pcm24, 44, ProjectRate.Scale(source.Frames, From, To));
        return new() {
            [source.Id] = ProjectRate.Written(
                source,
                id,
                "Audio Files/aud_a.48000.wav",
                written,
                SampleRateChangeMode.Convert
            )
        };
    }

    [Fact]
    public void Converting_rewrites_every_stored_frame_and_every_name_of_the_file() {
        var state = EveryPlace();

        var next = ProjectRate.Changed(state, To, SampleRateChangeMode.Convert, Converted(state));

        Assert.Equal(To, next.SampleRate);
        var file = Assert.Single(next.Files.Values);
        Assert.Equal(("aud_new", To, 480_000L, 0), (file.Id, file.SampleRate, file.Frames, file.Version));
        Assert.Equal([24_000L, 48_000], file.Tempo!.Beats);
        Assert.Equal(120, file.Tempo.Bpm);
        var region = next.Regions["rgn_a"];
        Assert.Equal(("aud_new", 24_000L, 48_000L), (region.FileId, region.FileOffset, region.Length));
        Assert.Equal((480L, 960L), (region.FadeIn, region.FadeOut));
        Assert.Equal(960, region.StartTick); // ticks are tempo-relative
        Assert.Equal([24_000L, 24_001, 24_002], region.FlexMarkers.Select(static m => m.FileFrame));
        Assert.Equal((24_000L, 28_800L), (region.PitchNotes[0].StartFrame, region.PitchNotes[0].EndFrame));
        var folder = next.TakeFolders["tkf_a"];
        Assert.Equal(480, folder.Crossfade);
        Assert.Equal(("aud_new", 480L, 48_000L), (folder.Takes[0].FileId, folder.Takes[0].FileOffset, folder.Takes[0].Length));
        var track = next.Tracks[0];
        Assert.Equal("aud_new", track.FrozenFileId);
        Assert.Equal("aud_new", track.Alternatives[0].Regions[0].FileId);
        Assert.Equal(109, track.Alternatives[0].Regions[0].FileOffset);
        var take = track.Alternatives[0].TakeFolders[0].Takes[0];
        Assert.Equal(("aud_new", 4_800L, 9_600L), (take.FileId, take.FileOffset, take.Length));
        var cell = next.LiveLoops.Cells[0].Content.Audio!;
        Assert.Equal(("aud_new", 480L, 480L), (cell.FileId, cell.FileOffset, cell.Length));
    }

    [Fact]
    public void A_project_at_the_new_rate_goes_through_the_package_unchanged() {
        var state = EveryPlace();
        var next = ProjectRate.Changed(state, To, SampleRateChangeMode.Convert, Converted(state));

        ProjectPackage.Save(folder, next);

        Assert.Equal(RandomProjects.Json(next), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void Keeping_the_samples_leaves_the_frames_and_scales_the_tempo_so_every_tick_stays_on_its_sample() {
        var state = EveryPlace();
        state = state with {
            Settings = state.Settings with {
                TempoEvents = [new(0, 100, TempoCurve.Step), new(9_600, 140, TempoCurve.Ramp)]
            }
        };
        var source = state.Files["aud_a"];
        var info = new WavInfo(2, To, WavEncoding.Pcm24, 44, source.Frames);
        var files = new Dictionary<string, AudioFile> {
            [source.Id] = ProjectRate.Written(source, "aud_new", "x.wav", info, SampleRateChangeMode.KeepSamples)
        };

        var next = ProjectRate.Changed(state, To, SampleRateChangeMode.KeepSamples, files);

        var region = next.Regions["rgn_a"];
        Assert.Equal(("aud_new", 22_050L, 44_100L), (region.FileId, region.FileOffset, region.Length));
        Assert.Equal((441L, 882L), (region.FadeIn, region.FadeOut));
        Assert.Equal(441, next.TakeFolders["tkf_a"].Crossfade);
        Assert.Equal(120 * (double)To / From, next.Files["aud_new"].Tempo!.Bpm, 9);
        Assert.Equal([22_050L, 44_100], next.Files["aud_new"].Tempo!.Beats);
        foreach (var tick in new long[] { 0, 480, 960, 9_600, 12_000, 48_000 }) {
            Assert.InRange(
                next.TempoMap.SampleAt(tick, To) - state.TempoMap.SampleAt(tick, From),
                -1,
                1
            );
        }

        // Audio moved up with its rate, and a Flex Pitch correction stays the same interval.
        var note = region.PitchNotes[0];
        var cents = 1200 * Math.Log2((double)To / From);
        Assert.Equal(6900 + cents, note.DetectedCents, 6);
        Assert.Equal(6950 + cents, note.TargetCents, 6);
    }

    [Fact]
    public void Keeping_the_samples_is_refused_when_the_tempo_would_leave_its_range() {
        var state = EveryPlace();
        state = state with { Settings = state.Settings with { TempoEvents = [new(0, 300, TempoCurve.Step)] } };

        Assert.Null(ProjectRate.Refusal(state, 192_000, SampleRateChangeMode.Convert));
        Assert.NotNull(ProjectRate.Refusal(state, 192_000, SampleRateChangeMode.KeepSamples));
    }

    [Fact]
    public void Rate_scaling_keeps_every_region_inside_its_file_and_its_fades_inside_it() =>
        Gen.Select(
                Gen.Long[1, 40_000_000],
                Gen.Double[0, 1],
                Gen.Double[0, 1],
                Gen.Long[0, 100_000],
                Gen.Long[0, 100_000],
                Gen.Int[0, 5],
                Gen.Int[0, 5]
            )
            .Sample(sample => {
                    var (frames, offsetShare, lengthShare, fadeIn, fadeOut, fromIndex, toIndex) = sample;
                    var (from, to) = (ProjectDefaults.SampleRates[fromIndex], ProjectDefaults.SampleRates[toIndex]);
                    if (from == to) {
                        return;
                    }

                    var offset = (long)(offsetShare * (frames - 1));
                    var length = Math.Max(1, (long)(lengthShare * (frames - offset)));
                    var state = EveryPlace() with { SampleRate = from };
                    var region = Region("rgn_a", "aud_a", offset, length) with {
                        FadeIn = Math.Min(fadeIn, length), FadeOut = Math.Min(fadeOut, length - Math.Min(fadeIn, length))
                    };
                    var file = File44("aud_a", frames) with { SampleRate = from };
                    state = state with {
                        Files = state.Files.SetItem("aud_a", file), Regions = state.Regions.SetItem("rgn_a", region)
                    };
                    var written = new WavInfo(2, to, WavEncoding.Pcm24, 44, Math.Max(1, ProjectRate.Scale(frames, from, to)));
                    var result = ProjectRate.Written(file, "aud_new", "x.wav", written, SampleRateChangeMode.Convert);

                    var next = ProjectRate.Changed(
                            state,
                            to,
                            SampleRateChangeMode.Convert,
                            new Dictionary<string, AudioFile> { ["aud_a"] = result }
                        )
                        .Regions["rgn_a"];

                    Assert.InRange(next.FileOffset, 0, written.Frames - 1);
                    Assert.InRange(next.Length, 1, written.Frames - next.FileOffset);
                    Assert.True(next.FadeIn + next.FadeOut <= next.Length);
                    Assert.True(next.FadeIn >= 0 && next.FadeOut >= 0);

                    // A second of the source is a second here, to the frame of either rate.
                    var seconds = (double)(offset + length) / from;
                    Assert.True(Math.Abs((double)(next.FileOffset + next.Length) / to - seconds) <= 2.0 / Math.Min(from, to));
                },
                iter: 2_000
            );

    [Fact]
    public void Scale_is_exact_where_doubles_would_round() {
        Assert.Equal(480_000, ProjectRate.Scale(441_000, 44_100, 48_000));
        Assert.Equal(long.MaxValue / 4 / 96_000 * 192_000, ProjectRate.Scale(long.MaxValue / 4 / 96_000 * 96_000, 96_000, 192_000));
        Assert.Equal(1, ProjectRate.Scale(1, 48_000, 48_000));
        Assert.Equal(0, ProjectRate.Scale(0, 44_100, 192_000));
    }

    [Fact]
    public void An_analysis_follows_the_file_it_was_made_of() {
        var state = EveryPlace();
        var source = state.Files["aud_a"];
        var result = Converted(state)["aud_a"];
        var analysis = new FileAnalysis(
            source.Id,
            220,
            [441, 44_100],
            source.Tempo,
            [6900f, null, 6901f, 6902f],
            [new(0, 880, 6900, 1)],
            false
        );

        var next = ProjectRate.Written(analysis, source, result, 240, SampleRateChangeMode.Convert);

        Assert.Equal((result.Id, 240), (next.FileId, next.Hop));
        Assert.Equal([480L, 48_000], next.Transients);
        Assert.Equal(958, next.Notes[0].EndFrame);
        Assert.Equal(4, next.Pitch.Count);
        Assert.Equal(6900f, next.Pitch[0]);
    }

    /// <summary>Two documents: what the root holds, and the one meant to replace it.</summary>
    (ProjectState Old, ProjectState New, string Staged, string Backup) Replacing() {
        var old = EveryPlace();
        ProjectPackage.Save(folder, old);
        var replacement = ProjectRate.Changed(old, To, SampleRateChangeMode.Convert, Converted(old));
        var staged = Path.Combine(folder, ".staged");
        ProjectPackage.Save(staged, replacement, false);
        var backup = ProjectHistory.Backup(folder, new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero))!;
        return (old, replacement, staged, backup);
    }

    [Fact]
    public void A_replaced_document_takes_over_and_leaves_no_rollback() {
        var (_, replacement, staged, backup) = Replacing();

        ProjectHistory.ReplaceDocument(folder, staged, backup);

        Assert.Equal(RandomProjects.Json(replacement), RandomProjects.Json(ProjectPackage.Load(folder)));
        Assert.False(File.Exists(Path.Combine(folder, ProjectHistory.RollbackName)));
    }

    [Fact]
    public void A_replacement_that_fails_half_way_puts_the_old_document_back() {
        var (old, _, staged, backup) = Replacing();

        var failure = Assert.Throws<IOException>(() => ProjectHistory.ReplaceDocument(
                folder,
                staged,
                backup,
                (from, to) => {
                    // Half written: the track files new, the root gone.
                    ProjectHistory.ClearDocument(to);
                    Directory.CreateDirectory(Path.Combine(to, ProjectPackage.TracksFolder));
                    File.Copy(
                        Directory.EnumerateFiles(Path.Combine(from, ProjectPackage.TracksFolder)).First(),
                        Path.Combine(to, ProjectPackage.TracksFolder, "half.json")
                    );
                    throw new IOException("The disk is full.");
                }
            )
        );

        Assert.Equal("The disk is full.", failure.Message);
        Assert.Equal(RandomProjects.Json(old), RandomProjects.Json(ProjectPackage.Load(folder)));
        Assert.False(File.Exists(Path.Combine(folder, ProjectHistory.RollbackName)));
        Assert.False(File.Exists(Path.Combine(folder, ProjectPackage.TracksFolder, "half.json")));
    }

    [Fact]
    public void A_crash_during_a_replacement_is_undone_at_the_next_look_and_can_be_stopped_itself() {
        var (old, _, _, backup) = Replacing();
        AtomicRename.Write(Path.Combine(folder, ProjectHistory.RollbackName), backup);
        ProjectHistory.ClearDocument(folder);

        // Until then, what can be read is the backup.
        Assert.Equal(ProjectHistory.BackupFolder(folder, backup), ProjectHistory.Document(folder));

        ProjectHistory.FinishInterrupted(folder);
        ProjectHistory.FinishInterrupted(folder);

        Assert.Equal(RandomProjects.Json(old), RandomProjects.Json(ProjectPackage.Load(folder)));
        Assert.Equal(folder, ProjectHistory.Document(folder));
    }

    [Fact]
    public void Relabelling_a_wav_changes_its_rate_and_not_one_sample() {
        var source = Path.Combine(folder, "source.wav");
        var target = Path.Combine(folder, "target.wav");
        using (var writer = new WavWriter(source, 2, From, WavEncoding.Pcm24)) {
            writer.Write([0.25f, -0.25f, 0.5f, -0.5f, 0.125f, 0]);
        }

        ProjectRate.Relabel(source, target, To);

        var (a, b) = (WavReader.Probe(source), WavReader.Probe(target));
        Assert.Equal(To, b.SampleRate);
        Assert.Equal((a.Frames, a.Channels, a.Encoding), (b.Frames, b.Channels, b.Encoding));
        var (before, after) = (File.ReadAllBytes(source), File.ReadAllBytes(target));
        Assert.True(before.AsSpan((int)a.DataOffset).SequenceEqual(after.AsSpan((int)b.DataOffset)));
        var (left, right) = (BounceProcessor.Read(source).Channels, BounceProcessor.Read(target).Channels);
        Assert.Equal(left, right);
    }

    [Fact]
    public void A_file_already_at_the_new_rate_is_left_as_it_is() {
        var state = EveryPlace();
        var at48 = new AudioFile("aud_48", "x", "Audio Files/x.wav", To, 1, 100, 24, null, 0);
        state = state with { Files = state.Files.Add(at48.Id, at48) };

        Assert.Equal(["aud_a"], ProjectRate.FilesToWrite(state, To, SampleRateChangeMode.Convert).Select(static f => f.Id));
        var next = ProjectRate.Changed(state, To, SampleRateChangeMode.Convert, Converted(state));
        Assert.Contains("aud_48", next.Files.Keys);
    }
}
