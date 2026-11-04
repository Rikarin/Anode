using Anode.Contracts;
using Anode.Dsp.Editing;
using Anode.Model.Audio;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Changing a project's sample rate (docs/01-implementation-plan.md §5.6). The conversion is proved on what it leaves
///     on the disk and, through the real engine, on what the project sounds like; the failures on the bytes of the
///     project, which must not move.
/// </summary>
public sealed class SampleRateApiTests : IDisposable {
    const int From = 44_100;
    const int To = 48_000;
    const double Tone = 1_000;
    const double Amplitude = 0.5;
    readonly string data = Directory.CreateTempSubdirectory("anode-rate-data-").FullName;
    readonly string media = Directory.CreateTempSubdirectory("anode-rate-media-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() {
        Directory.Delete(media, true);
        Directory.Delete(data, true);
    }

    ServerFixture Start(IAudioEditor? editor = null) =>
        new(
            data: data,
            editor: editor,
            configure: static s => s.Configure<ServerOptions>(static o => o.AutosaveSeconds = 0)
        );

    /// <summary>A stereo WAV: a sine in the left channel, one click of a sample in the right at <paramref name="click" /> s.</summary>
    string Wav(string name, int rate, double seconds, double click, WavEncoding encoding = WavEncoding.Pcm24) {
        var frames = (int)(seconds * rate);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++) {
            samples[i * 2] = (float)(Amplitude * Math.Sin(2 * Math.PI * Tone * i / rate));
        }

        samples[(int)(click * rate) * 2 + 1] = 0.9f;
        var path = Path.Combine(media, name);
        using var writer = new WavWriter(path, 2, rate, encoding);
        writer.Write(samples);
        return path;
    }

    /// <summary>A saved 44.1 kHz project with a region of a file, at a tick, inside the file, with fades.</summary>
    async Task<(Project Project, AudioFile File, string RegionId)> ProjectAsync(HttpClient client) {
        await client.CreateProjectAsync("Rate", From, media);
        var file = await client.ImportAsync(Wav("Tone.wav", From, 2, 1.25));
        await client.EditAsync(new AddTrackCommand("Audio"));
        await client.EditAsync(new AddRegionCommand((await client.ProjectAsync()).Tracks[0].Id, file.Id, 960));
        var region = (await client.ProjectAsync()).Regions[0];
        await client.EditAsync(
            new ResizeRegionCommand(region.Id, 960, 4_410, 44_100),
            new SetRegionCommand(region.Id, FadeIn: 441, FadeOut: 882)
        );
        await SaveAsync(client);
        return (await client.ProjectAsync(), file, region.Id);
    }

    static async Task SaveAsync(HttpClient client) =>
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);

    static Task<Job> ChangeAsync(
        HttpClient client,
        int rate,
        SampleRateChangeMode mode = SampleRateChangeMode.Convert
    ) =>
        client.RunJobAsync(
            "/api/v1/project/sample-rate",
            new(rate, mode),
            ContractsJsonContext.Default.ChangeSampleRateRequest
        );

    static Dictionary<string, byte[]> Bytes(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(folder, f)
                    .StartsWith(ProjectPackage.PeaksFolder, StringComparison.Ordinal)
            )
            .ToDictionary(f => Path.GetRelativePath(folder, f), File.ReadAllBytes);

    static void AssertUntouched(Dictionary<string, byte[]> before, string folder) {
        var after = Bytes(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) {
            Assert.True(bytes.AsSpan().SequenceEqual(after[path]), $"{path} changed.");
        }
    }

    [Fact]
    public async Task Converting_keeps_pitch_level_timing_and_length_measured_on_the_file() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, file, region) = await ProjectAsync(client);
        var saved = (await client.ProjectAsync()).Revision;

        var job = await ChangeAsync(client, To);

        Assert.Equal(JobState.Done, job.State);
        var now = await client.ProjectAsync();
        Assert.Equal((To, false), (now.SampleRate, now.Dirty));
        Assert.True(now.Revision > saved);
        var converted = Assert.Single(now.AudioFiles);
        Assert.Equal((file.Name, To), (converted.Name, converted.SampleRate));
        Assert.NotEqual(file.Id, converted.Id);
        Assert.Empty(now.Offline.Files);

        var path = Path.Combine(project.Path, converted.Path);
        var info = WavReader.Probe(path);
        Assert.Equal((To, 2, 24), (info.SampleRate, info.Channels, info.BitDepth));
        Assert.Equal(2.0, (double)info.Frames / To, 3); // length
        Assert.Equal(info.Frames, converted.Frames);
        var channels = BounceProcessor.Read(path).Channels;

        var middle = channels[0].AsSpan(To / 2, To);
        Assert.Equal(Tone, Frequency(middle, To), 1); // pitch, within 0.1 Hz
        Assert.Equal(Amplitude / Math.Sqrt(2), Rms(middle), 3); // level, within 0.02 dB
        Assert.True(SignalToError(channels[0], To) > 90, $"{SignalToError(channels[0], To):F1} dB"); // the waveform

        var click = Array.IndexOf(channels[1], channels[1].MaxBy(MathF.Abs));
        Assert.InRange(click, (long)(1.25 * To) - 1, (long)(1.25 * To) + 1); // timing, within a sample

        // The region plays the same stretch of the file, from the same place in the song.
        var moved = now.Regions.Single(r => r.Id == region);
        Assert.Equal(converted.Id, moved.FileId);
        Assert.Equal(4_800, moved.FileOffset);
        Assert.Equal(48_000, moved.Length);
        Assert.Equal((480L, 960L), (moved.FadeIn, moved.FadeOut));
        Assert.Equal(960, moved.StartTick);
        Assert.Equal(To, ProjectPackage.Load(project.Path).SampleRate);
    }

    [Fact]
    public async Task A_change_keeps_a_backup_that_goes_back_with_the_old_audio_and_keeps_the_unsaved_edits() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, file, _) = await ProjectAsync(client);
        var original = await File.ReadAllBytesAsync(Path.Combine(project.Path, file.Path), Token);
        await client.EditAsync(new AddTrackCommand("Unsaved"));
        var backupsBefore = ProjectHistory.Backups(project.Path).Count;

        Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);

        // The new rate has the unsaved edit, saved; both documents are backups.
        var now = await client.ProjectAsync();
        Assert.Contains(now.Tracks, static t => t.Name == "Unsaved");
        Assert.False(now.Dirty);
        var backups = ProjectHistory.Backups(project.Path);
        Assert.Equal(backupsBefore + 2, backups.Count);
        var documents = backups.Select(b => ProjectPackage.Load(ProjectHistory.BackupFolder(project.Path, b.Id)!))
            .ToArray();
        Assert.All(documents, static d => Assert.Equal(From, d.SampleRate));
        Assert.Contains(documents, static d => d.Tracks.Exists(static t => t.Name == "Unsaved"));
        Assert.Contains(documents, static d => d.Tracks.TrueForAll(static t => t.Name != "Unsaved"));

        // Undo from the old rate cannot apply here, so there is none.
        var undo = await client.PostAsync("/api/v1/project/undo", null, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, undo.StatusCode);

        var reverted = await client.PostAsync($"/api/v1/project/backups/{backups[1].Id}/revert", null, Token);
        Assert.Equal(HttpStatusCode.OK, reverted.StatusCode);
        var back = await client.ProjectAsync();
        Assert.Equal(From, back.SampleRate);
        Assert.Equal(file.Id, Assert.Single(back.AudioFiles).Id);
        Assert.Empty(back.Offline.Files);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(project.Path, file.Path), Token));
    }

    [Fact]
    public async Task An_alternative_stays_at_its_own_rate_with_its_own_files() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, file, _) = await ProjectAsync(client);
        var created = await client.PostAsJsonAsync(
            "/api/v1/project/alternatives",
            new("New Rate"),
            ContractsJsonContext.Default.CreateAlternativeRequest,
            Token
        );
        Assert.True(created.IsSuccessStatusCode);

        Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);

        var alternatives = await client.GetFromJsonAsync(
            "/api/v1/project/alternatives",
            ContractsJsonContext.Default.IReadOnlyListProjectAlternative,
            Token
        );
        var old = alternatives!.Single(static a => !a.Current);
        var opened = await client.PostAsync($"/api/v1/project/alternatives/{old.Id}/open", null, Token);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var back = await client.ProjectAsync();
        Assert.Equal((From, file.Id), (back.SampleRate, back.AudioFiles[0].Id));
        Assert.Empty(back.Offline.Files);
        Assert.Equal(project.Id, back.Id);
    }

    [Fact]
    public async Task Keeping_the_samples_labels_copies_of_the_files_and_keeps_every_frame() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, file, region) = await ProjectAsync(client);
        var old = Path.Combine(project.Path, file.Path);

        var job = await ChangeAsync(client, To, SampleRateChangeMode.KeepSamples);

        Assert.Equal(JobState.Done, job.State);
        var now = await client.ProjectAsync();
        var kept = Assert.Single(now.AudioFiles);
        Assert.Equal((To, file.Frames), (kept.SampleRate, kept.Frames));
        var (a, b) = (WavReader.Probe(old), WavReader.Probe(Path.Combine(project.Path, kept.Path)));
        Assert.Equal(From, a.SampleRate);
        Assert.Equal(To, b.SampleRate);
        Assert.True(
            File.ReadAllBytes(old)
                .AsSpan((int)a.DataOffset)
                .SequenceEqual(File.ReadAllBytes(Path.Combine(project.Path, kept.Path)).AsSpan((int)b.DataOffset))
        );
        var moved = now.Regions.Single(r => r.Id == region);
        Assert.Equal((4_410L, 44_100L, 441L, 882L), (moved.FileOffset, moved.Length, moved.FadeIn, moved.FadeOut));
        Assert.Equal(120 * (double)To / From, now.Settings.Tempo, 6);
        Assert.Equal(now.Settings.Tempo, now.Settings.TempoEvents[0].Bpm);
        Assert.Empty(now.Offline.Files);
    }

    [Fact]
    public async Task A_conversion_that_fails_leaves_every_byte_the_project_and_its_undo() {
        var failing = new FailingEditor();
        await using var server = Start(failing);
        using var client = server.CreateClient();
        var (project, _, _) = await ProjectAsync(client);
        await client.ImportAsync(Wav("Second.wav", From, 1, 0.5));
        await SaveAsync(client);
        await client.EditAsync(new AddTrackCommand("Unsaved"));
        var before = await client.ProjectAsync();
        var bytes = Bytes(project.Path);
        failing.FailOnCall = 2;

        var job = await ChangeAsync(client, To);

        Assert.Equal(JobState.Failed, job.State);
        AssertUntouched(bytes, project.Path);
        var now = await client.ProjectAsync();
        Assert.Equal((From, before.Revision, true), (now.SampleRate, now.Revision, now.Dirty));
        Assert.Equal(Files(before), Files(now));

        // Undo still works, and it can be tried again.
        await client.PostResultAsync("/api/v1/project/undo");
        failing.FailOnCall = 0;
        Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);
    }

    [Fact]
    public async Task A_conversion_with_the_wrong_length_stops_before_the_project_changes() {
        var failing = new FailingEditor { Truncate = true };
        await using var server = Start(failing);
        using var client = server.CreateClient();
        var (project, _, _) = await ProjectAsync(client);
        var bytes = Bytes(project.Path);

        var job = await ChangeAsync(client, To);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("frames", job.Message, StringComparison.Ordinal);
        AssertUntouched(bytes, project.Path);
        Assert.Equal(From, (await client.ProjectAsync()).SampleRate);
    }

    [Fact]
    public async Task A_document_that_cannot_be_replaced_goes_back_with_the_project_as_it_was() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, _, _) = await ProjectAsync(client);
        // A folder where the rollback note goes: replacing the document fails at its first step.
        Directory.CreateDirectory(Path.Combine(project.Path, ProjectHistory.RollbackName));
        var before = await client.ProjectAsync();

        var job = await ChangeAsync(client, To);

        Directory.Delete(Path.Combine(project.Path, ProjectHistory.RollbackName));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains($"stays at {From} Hz", job.Message, StringComparison.Ordinal);
        var now = await client.ProjectAsync();
        Assert.Equal((From, before.Revision), (now.SampleRate, now.Revision));
        Assert.Equal(From, ProjectPackage.Load(project.Path).SampleRate);
        Assert.Equal(Files(before), Files(now));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(project.Path, ProjectPackage.AudioFolder)));
        Assert.Empty(Directory.EnumerateDirectories(project.Path, ".rate-*"));
    }

    [Fact]
    public async Task A_failure_after_the_document_changed_keeps_the_files_it_names() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, _, _) = await ProjectAsync(client);
        // The recent list cannot be written, which comes after the document is replaced.
        File.Delete(Path.Combine(data, "recent.json"));
        Directory.CreateDirectory(Path.Combine(data, "recent.json"));

        await ChangeAsync(client, To);

        var read = ProjectPackage.Load(project.Path);
        Assert.Equal(To, read.SampleRate);
        Assert.All(read.Files.Values, f => Assert.True(File.Exists(Path.Combine(project.Path, f.Path)), f.Path));
        Directory.Delete(Path.Combine(data, "recent.json"));
    }

    [Fact]
    public async Task A_change_cut_short_by_a_crash_opens_as_the_old_project() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, _, _) = await ProjectAsync(client);
        var backup = ProjectHistory.Backup(project.Path, DateTimeOffset.UtcNow)!;
        await client.PostAsync("/api/v1/project/close", null, Token);
        File.WriteAllText(Path.Combine(project.Path, ProjectHistory.RollbackName), backup);
        ProjectHistory.ClearDocument(project.Path);

        var opened = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(project.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var now = await client.ProjectAsync();
        Assert.Equal((project.Id, From), (now.Id, now.SampleRate));
        Assert.Single(now.Regions);
        Assert.False(File.Exists(Path.Combine(project.Path, ProjectHistory.RollbackName)));
    }

    [Fact]
    public async Task Autosave_goes_on_at_the_new_rate_and_the_old_snapshot_is_gone() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = server.Services.GetRequiredService<ProjectSession>();
        var (project, _, _) = await ProjectAsync(client);
        await client.EditAsync(new AddTrackCommand("Unsaved"));
        Assert.True(session.Autosave());

        Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);

        Assert.Null(ProjectAutosave.Read(project.Path));
        await client.EditAsync(new AddTrackCommand("After"));
        Assert.True(session.Autosave());
        var manifest = ProjectAutosave.Recoverable(project.Path)!;
        Assert.Equal(To, ProjectPackage.Load(ProjectAutosave.Document(project.Path, manifest)).SampleRate);
    }

    [Fact]
    public async Task Offline_files_and_unreadable_tracks_stop_a_change_before_it_starts() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (project, file, _) = await ProjectAsync(client);
        var path = Path.Combine(project.Path, file.Path);
        var kept = path + ".away";
        File.Move(path, kept);

        var refused = await PostAsync(client, new(To, SampleRateChangeMode.Convert));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("offline", await refused.Content.ReadAsStringAsync(Token), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(From, (await client.ProjectAsync()).SampleRate);

        // Back on the disk, it goes through.
        File.Move(kept, path);
        Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);
    }

    [Fact]
    public async Task A_change_refuses_rates_there_are_not_and_the_rate_it_has() {
        await using var server = Start();
        using var client = server.CreateClient();
        await client.CreateProjectAsync("Rate", From);

        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostAsync(client, new(22_050, SampleRateChangeMode.Convert))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostAsync(client, new(From, SampleRateChangeMode.Convert))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostAsync(client, new(To, (SampleRateChangeMode)99))).StatusCode
        );
    }

    [Fact]
    public async Task A_project_without_audio_just_changes_rate_and_saves() {
        await using var server = Start();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync("Rate", From);

        Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);

        Assert.Equal(To, (await client.ProjectAsync()).SampleRate);
        Assert.Equal(To, ProjectPackage.Load(project.Path).SampleRate);
    }

    [Fact]
    public async Task A_cancelled_change_removes_what_it_wrote() {
        var slow = new FailingEditor { Hold = new() };
        await using var server = Start(slow);
        using var client = server.CreateClient();
        var (project, _, _) = await ProjectAsync(client);
        var bytes = Bytes(project.Path);
        var response = await PostAsync(client, new(To, SampleRateChangeMode.Convert));
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.True((await client.PostAsync($"/api/v1/jobs/{job.Id}/cancel", null, Token)).IsSuccessStatusCode);
        slow.Hold.SetResult();

        await ServerFixture.Eventually(async () =>
            (await client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token))!.State
            == JobState.Cancelled
        );
        AssertUntouched(bytes, project.Path);
        Assert.Equal(From, (await client.ProjectAsync()).SampleRate);
    }

    static IEnumerable<(string, string, long)> Files(Project project) =>
        project.AudioFiles.Select(static f => (f.Id, f.Path, f.Frames));

    static Task<HttpResponseMessage> PostAsync(HttpClient client, ChangeSampleRateRequest request) =>
        client.PostAsJsonAsync(
            "/api/v1/project/sample-rate",
            request,
            ContractsJsonContext.Default.ChangeSampleRateRequest,
            Token
        );

    /// <summary>
    ///     The same project played by the real engine before and after: a bounce measures what a listener hears. Each
    ///     render runs on a device at the project's rate, as a person would set it.
    /// </summary>
    [Fact]
    public async Task Through_the_real_engine_a_converted_project_sounds_the_same() {
        var socket = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
        var first = Host(socket, From);
        Process? second = null;
        try {
            await using var server = new ServerFixture(socket, data: data);
            using var client = server.CreateClient();
            await ConnectedAsync(server, client);
            await client.CreateProjectAsync("Rate", From, media);
            var file = await client.ImportAsync(Wav("Tone.wav", From, 2, 1.25));
            await client.EditAsync(new AddTrackCommand("Audio"));
            await client.EditAsync(new AddRegionCommand((await client.ProjectAsync()).Tracks[0].Id, file.Id, 3_840));
            await SaveAsync(client);
            var before = await BounceAsync(client, "before", From);

            Assert.Equal(JobState.Done, (await ChangeAsync(client, To)).State);
            await StopAsync(first);
            await ServerFixture.Eventually(async () =>
                (await server.SystemAsync(client)).Engine.State == EngineState.Disconnected
            );
            second = Host(socket, To);
            await ConnectedAsync(server, client);
            var after = await BounceAsync(client, "after", To);

            // The region starts at 3840 ticks, 2 s at 120 bpm, and the tone plays on from there.
            Assert.Equal(2.0, Onset(before[0], From), 3);
            Assert.Equal(Onset(before[0], From), Onset(after[0], To), 3); // timing within a millisecond
            var a = before[0].AsSpan(From * 5 / 2, From);
            var b = after[0].AsSpan(To * 5 / 2, To);
            Assert.Equal(Tone, Frequency(a, From), 0);
            Assert.Equal(Tone, Frequency(b, To), 0); // pitch
            Assert.Equal(20 * Math.Log10(Rms(a)), 20 * Math.Log10(Rms(b)), 1); // level within 0.1 dB
            Assert.Equal(before[0].Length / (double)From, after[0].Length / (double)To, 3); // length
        } finally {
            await StopAsync(first);
            if (second is not null) {
                await StopAsync(second);
            }

            File.Delete(socket);
        }

        static double Onset(float[] left, int rate) =>
            (double)Array.FindIndex(left, static x => MathF.Abs(x) > 0.01f) / rate;

        async Task<float[][]> BounceAsync(HttpClient client, string name, int rate) {
            var job = await client.RunJobAsync(
                "/api/v1/project/bounce",
                new(name, media, 0, 9_600, BitDepth: 32, Dither: DitherMode.None),
                ContractsJsonContext.Default.BounceRequest
            );
            Assert.True(job.State == JobState.Done, job.Message);
            var (channels, bounced) = BounceProcessor.Read(job.Files[0]);
            Assert.Equal(rate, bounced);
            return channels;
        }
    }

    static Task ConnectedAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );

    /// <summary>An anode-host on no device at <paramref name="rate" />.</summary>
    static Process Host(string socket, int rate) {
        var host = new Process {
            StartInfo = new(
                "dotnet",
                [
                    "exec",
                    Path.Combine(
                        AppContext.BaseDirectory.Replace(
                            Path.Combine("tests", "Anode.Server.Tests"),
                            Path.Combine("src", "Anode.Host"),
                            StringComparison.Ordinal
                        ),
                        "anode-host.dll"
                    ),
                    "--serve",
                    "--null-audio",
                    "--rate",
                    rate.ToString(CultureInfo.InvariantCulture),
                    "--socket",
                    socket
                ]
            ) { RedirectStandardOutput = true, RedirectStandardError = true }
        };
        host.Start();
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        return host;
    }

    static async Task StopAsync(Process host) {
        if (!host.HasExited) {
            host.Kill(true);
        }

        await host.WaitForExitAsync(CancellationToken.None);
    }

    static double Rms(ReadOnlySpan<float> samples) {
        double sum = 0;
        foreach (var x in samples) {
            sum += x * x;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>The frequency from the spacing of upward zero crossings, interpolated between samples.</summary>
    static double Frequency(ReadOnlySpan<float> samples, int rate) {
        double? first = null;
        double last = 0;
        var count = -1;
        for (var i = 1; i < samples.Length; i++) {
            if (samples[i - 1] < 0 && samples[i] >= 0) {
                last = i - 1 + samples[i - 1] / (samples[i - 1] - (double)samples[i]);
                first ??= last;
                count++;
            }
        }

        return count * rate / (last - first!.Value);
    }

    /// <summary>How far the middle of a channel is from the ideal tone at its rate, in dB below the tone.</summary>
    static double SignalToError(float[] channel, int rate) {
        double signal = 0, error = 0;
        for (var i = rate / 2; i < rate * 3 / 2; i++) {
            var ideal = Amplitude * Math.Sin(2 * Math.PI * Tone * i / rate);
            signal += ideal * ideal;
            error += (channel[i] - ideal) * (channel[i] - ideal);
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    /// <summary>The real conversion, which fails, is cut short, or waits, as a test asks.</summary>
    sealed class FailingEditor : IAudioEditor {
        readonly DspAudioEditor real = new();
        int calls;

        public int FailOnCall { get; set; }

        public bool Truncate { get; init; }

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (float[][] Channels, int SampleRate) Apply(
            float[][] channels,
            int sampleRate,
            AudioEditOperation operation
        ) {
            Entered.TrySetResult();
            Hold?.Task.Wait(TimeSpan.FromSeconds(30));
            if (++calls == FailOnCall) {
                throw new ArgumentException("The converter gave up.");
            }

            var (result, rate) = real.Apply(channels, sampleRate, operation);
            return (Truncate ? [..result.Select(static c => c.AsSpan(0, c.Length / 2).ToArray())] : result, rate);
        }

        public IReadOnlyList<LoopCandidate> Loops(
            float[] mono,
            int sampleRate,
            long start,
            long end,
            int count,
            long minFrames
        ) =>
            real.Loops(mono, sampleRate, start, end, count, minFrames);

        public IReadOnlyList<GrooveOnset> Onsets(float[] mono, int sampleRate) => real.Onsets(mono, sampleRate);
    }
}
