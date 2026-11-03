using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using System.Runtime.CompilerServices;

namespace Anode.Interchange.Tests.Aaf;

/// <summary>
///     A project folder with an arrangement of audio regions for the AAF tests (48 kHz, 120 BPM: a tick is 25 samples):
///     two audio tracks and an instrument track; offsets, clip gain, fades and a crossfade; three audio files (mono
///     16-bit, stereo 24-bit and stereo float); two markers.
/// </summary>
sealed class AafScenario : IDisposable {
    public const int Rate = 48_000;

    public AafScenario() {
        Folder = Directory.CreateTempSubdirectory("anode-aaf-").FullName;
        Directory.CreateDirectory(Path.Combine(Folder, "Audio Files"));
        Write("Audio Files/Kick.wav", 1, WavEncoding.Pcm16, 96_000, 0.5);
        Write("Audio Files/Pad.wav", 2, WavEncoding.Pcm24, 96_000, 0.25);
        Write("Audio Files/Strings.wav", 2, WavEncoding.Float32, 48_000, 0.125);
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_aaf", "Song", Rate)
            .Apply(new AudioFileUpserted(new("aud_1", "Kick", "Audio Files/Kick.wav", Rate, 1, 96_000, 16, null, 0)))
            .Apply(new AudioFileUpserted(new("aud_2", "Pad", "Audio Files/Pad.wav", Rate, 2, 96_000, 24, null, 0)))
            .Apply(
                new AudioFileUpserted(new("aud_3", "Strings", "Audio Files/Strings.wav", Rate, 2, 48_000, 32, null, 0))
            );
        state = Edits.Apply(
                state,
                [
                    new AddTrackCommand("Drums", Kind: TrackKind.Audio),
                    new AddTrackCommand("Synth", Kind: TrackKind.Instrument),
                    new AddTrackCommand("Keys", Kind: TrackKind.Audio, Stereo: true),
                    new AddMarkerCommand("Verse", 1920, 960), new AddMarkerCommand("Chorus", 3840)
                ],
                ids
            )
            .State;
        var drums = state.Tracks[0].Id;
        var keys = state.Tracks[2].Id;
        var verse = state.Markers.Values.First(static m => m.Name == "Verse");
        state = state.Apply(new MarkerUpserted(verse with { Text = "first verse" }))
            // 24 000 … 54 000, fading into the next over 4 800 samples.
            .Apply(new RegionUpserted(Region("reg_1", drums, "aud_1", 960, 1_000, 30_000, -6, 500, 4_800)))
            // 49 200 … 69 200.
            .Apply(new RegionUpserted(Region("reg_2", drums, "aud_2", 1_968, 2_000, 20_000, 0, 4_800, 1_000)))
            // 120 000 … 130 000.
            .Apply(new RegionUpserted(Region("reg_3", drums, "aud_1", 4_800, 0, 10_000, 3, 0, 0)))
            .Apply(new RegionUpserted(Region("reg_4", keys, "aud_3", 0, 100, 40_000, -1.5, 0, 0)));
        State = state;
    }

    public string Folder { get; }

    public ProjectState State { get; }

    public string Exports => Path.Combine(Folder, "Exports");

    public void Dispose() {
        try {
            Directory.Delete(Folder, true);
        } catch (IOException) {
            // A scratch folder left behind is the system temp folder's to clean.
        }
    }

    public static AudioRegion Region(
        string id,
        string track,
        string file,
        long tick,
        long offset,
        long length,
        double gain,
        long fadeIn,
        long fadeOut
    ) =>
        new(
            id,
            track,
            file,
            id,
            tick,
            offset,
            length,
            gain,
            fadeIn,
            fadeOut,
            0,
            0,
            false,
            FlexFollow.Off,
            [],
            0,
            0,
            [],
            PitchSourceMode.Off,
            null
        );

    /// <summary>A WAV file of a ramp per channel (distinct values, so offsets show), at <paramref name="peak" />.</summary>
    void Write(string relative, int channels, WavEncoding encoding, int frames, double peak) {
        using var writer = new WavWriter(Path.Combine(Folder, relative), channels, Rate, encoding);
        var block = new float[1000 * channels];
        for (var start = 0; start < frames; start += 1000) {
            for (var i = 0; i < 1000; i++) {
                for (var c = 0; c < channels; c++) {
                    block[i * channels + c] = (float)(peak * ((start + i) * (c + 3) % 2001 - 1000) / 1000.0);
                }
            }

            writer.Write(block);
        }
    }

    /// <summary>The folder of this source file (fixtures and scripts are found from it, not copied).</summary>
    public static string SourceFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    public static string RepositoryRoot() => Path.GetFullPath(Path.Combine(SourceFolder(), "..", "..", "..", ".."));
}
