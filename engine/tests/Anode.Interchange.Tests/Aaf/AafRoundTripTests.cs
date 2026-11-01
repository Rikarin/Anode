using Anode.Contracts;
using Anode.Interchange.Aaf;
using Anode.Model.Audio;

namespace Anode.Interchange.Tests.Aaf;

/// <summary>An arrangement exported as AAF (embedded and linked) and read back by Anode's reader.</summary>
public sealed class AafRoundTripTests : IDisposable {
    readonly AafScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_exported_arrangement_reads_back_as_the_same_composition(bool embed) {
        var path = Path.Combine(scenario.Exports, "Song.aaf");
        var files = AafWriter.Write(scenario.State, scenario.Folder, new("Song", EmbedMedia: embed), path);
        Assert.Equal(path, files[0]);
        Assert.Equal(embed ? 1 : 4, files.Count);
        if (!embed) {
            Assert.All(
                files.Skip(1),
                f => Assert.Equal(Path.Combine(scenario.Exports, "Song Media"), Path.GetDirectoryName(f))
            );
        }

        var media = Path.Combine(scenario.Folder, "Imported");
        var composition = AafReader.Read(path, media);

        Assert.Equal(("Song", 48_000), (composition.Name, composition.SampleRate));
        Assert.Equal(["Drums", "Keys"], composition.Tracks.Select(static t => t.Name));
        var drums = composition.Tracks[0].Clips;
        Assert.Equal(3, drums.Count);
        Assert.Equal(
            (24_000L, 1_000L, 30_000L, 500L, 4_800L),
            (drums[0].Position, drums[0].SourceOffset, drums[0].Length,
                drums[0].FadeIn, drums[0].FadeOut)
        );
        Assert.Equal(-6, drums[0].GainDb, 6);
        Assert.Equal(
            (49_200L, 2_000L, 20_000L, 4_800L, 1_000L),
            (drums[1].Position, drums[1].SourceOffset, drums[1].Length,
                drums[1].FadeIn, drums[1].FadeOut)
        );
        Assert.Equal(0, drums[1].GainDb, 6);
        Assert.Equal(
            (120_000L, 0L, 10_000L, 0L, 0L),
            (drums[2].Position, drums[2].SourceOffset, drums[2].Length,
                drums[2].FadeIn, drums[2].FadeOut)
        );
        Assert.Equal(3, drums[2].GainDb, 6);
        var keys = Assert.Single(composition.Tracks[1].Clips);
        Assert.Equal((0L, 100L, 40_000L), (keys.Position, keys.SourceOffset, keys.Length));
        Assert.Equal(-1.5, keys.GainDb, 6);
        Assert.Equal(["Kick", "Pad", "Kick"], drums.Select(static c => c.Name));

        // The media: the same samples (the float file as 24-bit PCM).
        Assert.Equal(drums[0].MediaPath, drums[2].MediaPath);
        AssertSamples(Path.Combine(scenario.Folder, "Audio Files/Kick.wav"), drums[0].MediaPath, 1.0 / 32767);
        AssertSamples(Path.Combine(scenario.Folder, "Audio Files/Pad.wav"), drums[1].MediaPath, 1.0 / 8388607);
        AssertSamples(Path.Combine(scenario.Folder, "Audio Files/Strings.wav"), keys.MediaPath, 1.0 / 8388607);
        Assert.All(
            composition.Tracks.SelectMany(static t => t.Clips),
            c => Assert.Equal(
                embed ? media : Path.Combine(scenario.Exports, "Song Media"),
                Path.GetDirectoryName(c.MediaPath)
            )
        );

        Assert.Equal(
            [new AafMarker("Verse", 48_000, 24_000, "first verse"), new AafMarker("Chorus", 96_000, 0, string.Empty)],
            composition.Markers
        );
    }

    [Fact]
    public void The_content_of_a_composition_places_regions_on_ticks_with_the_audio_at_its_sample() {
        var path = Path.Combine(scenario.Exports, "Song.aaf");
        AafWriter.Write(scenario.State, scenario.Folder, new("Song"), path);
        var composition = AafReader.Read(path, Path.Combine(scenario.Folder, "Imported"));
        var paths = composition.Tracks.SelectMany(static t => t.Clips)
            .Select(static c => c.MediaPath)
            .Distinct()
            .ToList();
        var fileIds = paths.Select(static (p, i) => (p, $"file-{i}"))
            .ToDictionary(static x => x.p, static x => x.Item2);
        var content = AafReader.ToContent(composition, scenario.State, fileIds);

        Assert.Equal(["Drums", "Keys"], content.Tracks.Select(static t => t.Name));
        Assert.All(content.Tracks, static t => Assert.Equal(TrackKind.Audio, t.Kind));
        var regions = content.Tracks[0].Regions;
        Assert.Equal([960L, 1_968L, 4_800L], regions.Select(static r => r.StartTick));
        Assert.Equal([1_000L, 2_000L, 0L], regions.Select(static r => r.FileOffset));
        Assert.Equal([30_000L, 20_000L, 10_000L], regions.Select(static r => r.Length));
        Assert.Equal([500L, 4_800L, 0L], regions.Select(static r => r.FadeIn));
        Assert.Equal([4_800L, 1_000L, 0L], regions.Select(static r => r.FadeOut));
        Assert.Equal(fileIds[composition.Tracks[0].Clips[0].MediaPath], regions[0].FileId);
        Assert.Equal(["Verse", "Chorus"], content.Markers!.Select(static m => m.Name));
        Assert.Equal([1920L, 3840L], content.Markers!.Select(static m => m.Tick));
        Assert.Equal("first verse", content.Markers![0].Text);
        Assert.Null(content.TempoEvents);
    }

    [Fact]
    public void A_clip_off_the_tick_grid_keeps_its_audio_at_the_sample() {
        // 120 BPM at 48 kHz: a tick is 25 samples. 1 000 010 is 10 samples after tick 40 000.
        var composition = new AafComposition(
            "Off",
            48_000,
            [
                new(
                    "A",
                    [new("x", "x.wav", 1_000_010, 50, 1_000, 0, 20, 0), new("y", "x.wav", 2_000_010, 5, 1_000, 0, 0, 0)]
                )
            ],
            []
        );
        var content = AafReader.ToContent(
            composition,
            scenario.State,
            new Dictionary<string, string> { ["x.wav"] = "f" }
        );
        var (early, late) = (content.Tracks[0].Regions[0], content.Tracks[0].Regions[1]);
        // Enough audio before it: one tick earlier, 10 more samples of the file.
        Assert.Equal((40_000L, 40L, 1_010L, 30L), (early.StartTick, early.FileOffset, early.Length, early.FadeIn));
        // Too little: the next tick, 15 samples of the file dropped.
        Assert.Equal((80_001L, 20L, 985L), (late.StartTick, late.FileOffset, late.Length));
    }

    [Fact]
    public void A_crossfade_whose_later_clip_moves_to_an_earlier_tick_widens_the_fade_out() {
        // 1 000 000 … 1 001 000, crossfading over 90 samples into a clip at 1 000 910 (10 samples after a tick).
        var composition = new AafComposition(
            "X",
            48_000,
            [
                new(
                    "A",
                    [
                        new("a", "x.wav", 1_000_000, 0, 1_000, 0, 0, 90),
                        new("b", "x.wav", 1_000_910, 500, 1_000, 0, 90, 0)
                    ]
                )
            ],
            []
        );
        var regions = AafReader.ToContent(
                composition,
                scenario.State,
                new Dictionary<string, string> { ["x.wav"] = "f" }
            )
            .Tracks[0]
            .Regions;
        Assert.Equal(
            (40_036L, 490L, 1_010L, 100L),
            (regions[1].StartTick, regions[1].FileOffset, regions[1].Length,
                regions[1].FadeIn)
        );
        Assert.Equal(100, regions[0].FadeOut);
    }

    [Fact]
    public void Only_the_requested_audio_tracks_are_exported() {
        var path = Path.Combine(scenario.Exports, "Keys.aaf");
        var keys = scenario.State.Tracks[2].Id;
        var synth = scenario.State.Tracks[1].Id;
        AafWriter.Write(scenario.State, scenario.Folder, new("Keys", TrackIds: [keys, synth]), path);
        var composition = AafReader.Read(path, Path.Combine(scenario.Folder, "Imported"));
        Assert.Equal(["Keys"], composition.Tracks.Select(static t => t.Name));
    }

    [Fact]
    public void A_version_3_compound_file_reads_the_same() {
        var four = Path.Combine(scenario.Exports, "Four.aaf");
        var three = Path.Combine(scenario.Exports, "Three.aaf");
        var now = DateTimeOffset.UnixEpoch;
        AafWriter.Write(scenario.State, scenario.Folder, new("Four"), four, now, 4);
        AafWriter.Write(scenario.State, scenario.Folder, new("Three"), three, now, 3);
        var a = AafReader.Read(four, Path.Combine(scenario.Folder, "A"));
        var b = AafReader.Read(three, Path.Combine(scenario.Folder, "B"));
        Assert.Equal(
            a.Tracks.SelectMany(static t => t.Clips).Select(c => c with { MediaPath = string.Empty }),
            b.Tracks.SelectMany(static t => t.Clips).Select(c => c with { MediaPath = string.Empty })
        );
    }

    [Theory]
    [InlineData(-6.0, 1 << 30)]
    [InlineData(12.0, 1 << 29)]
    [InlineData(-144.0, 1 << 30)]
    public void Clip_gain_is_an_amplitude_ratio_with_a_large_denominator(double db, int denominator) {
        var (n, d) = AafWriter.Amplitude(db);
        Assert.Equal(denominator, d);
        Assert.Equal(Math.Pow(10, db / 20), (double)n / d, 6);
    }

    static void AssertSamples(string expected, string actual, double tolerance) {
        using var a = WavReader.Open(expected);
        using var b = WavReader.Open(actual);
        Assert.Equal(
            (a.Info.Channels, a.Info.SampleRate, a.Info.Frames),
            (b.Info.Channels, b.Info.SampleRate, b.Info.Frames)
        );
        var x = new float[4096 * a.Info.Channels];
        var y = new float[4096 * a.Info.Channels];
        while (true) {
            var n = a.Read(x);
            Assert.Equal(n, b.Read(y));
            if (n == 0) {
                break;
            }

            for (var i = 0; i < n * a.Info.Channels; i++) {
                Assert.InRange(y[i] - x[i], -tolerance, tolerance);
            }
        }
    }
}
