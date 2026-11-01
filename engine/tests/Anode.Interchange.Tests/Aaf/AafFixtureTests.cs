using Anode.Contracts;
using Anode.Interchange.Aaf;
using Anode.Model;
using Anode.Model.Audio;

namespace Anode.Interchange.Tests.Aaf;

/// <summary>
///     AAF files written by pyaaf2 (<c>scripts/make-aaf-fixtures.py</c>, which documents their content) read by Anode:
///     the tracks, clips (positioned to the sample), transitions, gain, markers and media come back.
/// </summary>
public sealed class AafFixtureTests : IDisposable {
    readonly string scratch = Directory.CreateTempSubdirectory("anode-aaf-fixture-").FullName;

    public void Dispose() {
        try {
            Directory.Delete(scratch, true);
        } catch (IOException) {
            // A scratch folder left behind is the system temp folder's to clean.
        }
    }

    static string Fixture(string name) => Path.Combine(AafScenario.SourceFolder(), "Fixtures", name);

    [Fact]
    public void The_embedded_fixture_comes_back_with_its_clips_to_the_sample() {
        var composition = AafReader.Read(Fixture("pyaaf2-embedded.aaf"), scratch);

        Assert.Equal(("Fixture", 48_000), (composition.Name, composition.SampleRate));
        Assert.Equal(["Dialog", "Music"], composition.Tracks.Select(static t => t.Name));
        var dialog = composition.Tracks[0].Clips;
        Assert.Equal(
            [
                ("tone", 4_800L, 1_200L, 6_000L, 0.0, 240L, 960L), ("noise", 9_840L, 0L, 4_000L, -6.020600, 960L, 0L),
                ("tone", 14_840L, 0L, 2_400L, 0.0, 0L, 480L)
            ],
            dialog.Select(static c => (c.Name, c.Position, c.SourceOffset, c.Length, Math.Round(c.GainDb, 6), c.FadeIn,
                    c.FadeOut)
            )
        );
        var music = Assert.Single(composition.Tracks[1].Clips);
        Assert.Equal(("noise", 0L, 2_000L, 6_000L), (music.Name, music.Position, music.SourceOffset, music.Length));
        Assert.Equal(dialog[0].MediaPath, dialog[2].MediaPath);
        Assert.Equal(dialog[1].MediaPath, music.MediaPath);
        Assert.Equal(
            [new AafMarker("Hit", 9_600, 0, string.Empty), new AafMarker("Out", 16_000, 480, string.Empty)],
            composition.Markers
        );

        // The extracted media: the generator's samples.
        using (var tone = WavReader.Open(dialog[0].MediaPath)) {
            Assert.Equal(
                (1, 48_000, WavEncoding.Pcm16, 12_000L),
                (tone.Info.Channels, tone.Info.SampleRate,
                    tone.Info.Encoding, tone.Info.Frames)
            );
            var samples = new float[12_000];
            Assert.Equal(12_000, ReadAll(tone, samples));
            Assert.All(
                Enumerable.Range(0, 12_000),
                i => Assert.Equal((i * 7 % 2000 - 1000) * 16 / 32768f, samples[i])
            );
        }

        using (var noise = WavReader.Open(music.MediaPath)) {
            Assert.Equal((2, WavEncoding.Pcm24, 8_000L), (noise.Info.Channels, noise.Info.Encoding, noise.Info.Frames));
            var samples = new float[16_000];
            Assert.Equal(8_000, ReadAll(noise, samples));
            Assert.Equal((1234 * 18 % 4001 - 2000) * 2048 / 8388608f, samples[1234 * 2 + 1]);
        }
    }

    [Fact]
    public void The_linked_fixture_finds_its_media_beside_the_file() {
        var composition = AafReader.Read(Fixture("pyaaf2-linked.aaf"), scratch);

        Assert.Equal(("Linked fixture", 44_100), (composition.Name, composition.SampleRate));
        var clip = Assert.Single(Assert.Single(composition.Tracks).Clips);
        Assert.Equal(
            ("linked-tone", 4_410L, 441L, 21_609L),
            (clip.Name, clip.Position, clip.SourceOffset, clip.Length)
        );
        Assert.Equal(Path.GetFullPath(Fixture("linked-tone.wav")), clip.MediaPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    [Fact]
    public void A_linked_file_that_cannot_be_found_throws() {
        var moved = Path.Combine(scratch, "Moved", "pyaaf2-linked.aaf");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Copy(Fixture("pyaaf2-linked.aaf"), moved);
        var missing = Assert.Throws<FileNotFoundException>(() => AafReader.Read(moved, Path.Combine(scratch, "Media")));
        Assert.Contains("linked-tone.wav", missing.Message, StringComparison.Ordinal);

        // Beside it in "<name> Media/", it is found.
        Directory.CreateDirectory(Path.Combine(scratch, "Moved", "pyaaf2-linked Media"));
        var beside = Path.Combine(scratch, "Moved", "pyaaf2-linked Media", "linked-tone.wav");
        File.Copy(Fixture("linked-tone.wav"), beside);
        Assert.Equal(beside, AafReader.Read(moved, Path.Combine(scratch, "Media")).Tracks[0].Clips[0].MediaPath);
    }

    [Fact]
    public void A_fixture_imports_into_a_project_at_its_rate_and_tempo() {
        var composition = AafReader.Read(Fixture("pyaaf2-linked.aaf"), scratch);
        // A 48 kHz project at 100 BPM: 4 410 samples of 44.1 kHz are 4 800 project samples, 0.1 s, 160 ticks.
        var state = ProjectDefaults.NewProject("prj", "P", 48_000) with {
            Settings = ProjectDefaults.NewProject("prj", "P", 48_000).Settings with {
                Tempo = 100, TempoEvents = [new(0, 100, TempoCurve.Step)]
            }
        };
        var media = composition.Tracks[0].Clips[0].MediaPath;
        var content = AafReader.ToContent(composition, state, new Dictionary<string, string> { [media] = "aud_9" });
        var track = Assert.Single(content.Tracks);
        Assert.Equal(("Linked", TrackKind.Audio), (track.Name, track.Kind));
        var region = Assert.Single(track.Regions);
        Assert.Equal(
            ("aud_9", 160L, 480L, 23_520L),
            (region.FileId, region.StartTick, region.FileOffset, region.Length)
        );
        Assert.Equal(4_800, state.TempoMap.SampleAt(region.StartTick, 48_000));
        Assert.Null(content.Markers);
    }

    static int ReadAll(WavReader reader, float[] into) {
        var total = 0;
        while (total * reader.Info.Channels < into.Length) {
            var n = reader.Read(into.AsSpan(total * reader.Info.Channels));
            if (n == 0) {
                break;
            }

            total += n;
        }

        return total;
    }
}
