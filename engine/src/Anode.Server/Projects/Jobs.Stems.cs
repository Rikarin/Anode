using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Stems;

namespace Anode.Server.Projects;

/// <summary>
///     The Stem Splitter (docs/09-phase-8.md, docs/adr/0010-stem-splitter.md): a <c>stemSplit</c> job that opens the
///     model the stems need (fetched on first use, <see cref="IStemModels" />), reads the region's file over the region's
///     span, separates it with progress and cancellation, writes one WAV per stem into the project's <c>Audio Files</c>
///     at the file's rate and bit depth, and then adds the files, the stem tracks and their regions, muting the source
///     unless kept, as one undo step "Split into Stems" (<c>addStems</c>). A cancelled or failed job leaves no files.
/// </summary>
public sealed partial class Jobs {
    /// <summary>The stems made when a request names none.</summary>
    public static readonly IReadOnlyList<StemKind> DefaultStems =
        [StemKind.Vocals, StemKind.Drums, StemKind.Bass, StemKind.Other];

    /// <summary>Queues splitting an audio region: 404 for an unknown region, 422 for stems that cannot be made.</summary>
    public Job SplitStems(string regionId, StemSplitRequest? request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var region = view.State.Regions.TryGetValue(regionId, out var r)
            ? r
            : throw ProjectException.NotFound($"No audio region '{regionId}'.");
        var file = view.State.Files.TryGetValue(region.FileId, out var f)
            ? f
            : throw ProjectException.Unprocessable($"Region '{region.Name}' has no file.");
        var stems = CheckStems(request?.Stems);
        var keep = request?.KeepSource ?? false;
        return Enqueue(
            JobKind.StemSplit,
            (entry, token) => Task.Run(
                () => SplitAsync(entry, view.Session, view.Folder, region, file, stems, keep, token),
                token
            )
        );
    }

    /// <summary>The stems asked for (the four when none), each once, in the order of <see cref="StemKind" />.</summary>
    static StemKind[] CheckStems(IReadOnlyList<StemKind>? stems) {
        if (stems is null) {
            return [..DefaultStems];
        }

        if (stems.Count == 0 || stems.Any(static s => !Enum.IsDefined(s)) || stems.Distinct().Count() != stems.Count) {
            throw ProjectException.Unprocessable("Name the stems to make, each once.");
        }

        return [..stems.Order()];
    }

    /// <summary>The model that makes every stem of <paramref name="stems" />: six stems with guitar or piano, else four.</summary>
    public static StemModel ModelFor(IReadOnlyList<StemKind> stems) =>
        stems.Any(static s => s is StemKind.Guitar or StemKind.Piano) ? StemModel.SixStems : StemModel.FourStems;

    async Task<IReadOnlyList<string>> SplitAsync(
        Entry entry,
        long sessionId,
        string folder,
        AudioRegion region,
        AudioFile file,
        StemKind[] stems,
        bool keepSource,
        CancellationToken token
    ) {
        var written = new List<string>();
        try {
            using var separator = await models.OpenAsync(
                    ModelFor(stems),
                    new ProgressTo(f => Progress(entry, 0.1 * f)),
                    token
                )
                .ConfigureAwait(false);
            var missing = stems.Where(s => !separator.Stems.Contains(s)).ToArray();
            if (missing.Length > 0) {
                throw ProjectException.Unprocessable($"The model makes no {string.Join(", ", missing)} stem.");
            }

            var (channels, rate) = BounceProcessor.Read(Path.Combine(folder, file.Path));
            var start = (int)Math.Clamp(region.FileOffset, 0, channels[0].Length);
            var length = (int)Math.Clamp(region.Length, 0, channels[0].Length - start);
            if (length == 0) {
                throw ProjectException.Unprocessable($"Region '{region.Name}' plays nothing of its file.");
            }

            float[][] span = [..channels.Select(c => c.AsSpan(start, length).ToArray())];
            Progress(entry, 0.15);
            token.ThrowIfCancellationRequested();
            var separated = separator.Separate(
                span,
                rate,
                stems,
                new ProgressTo(f => Progress(entry, 0.15 + 0.75 * f)),
                token
            );
            token.ThrowIfCancellationRequested();
            var audio = Path.Combine(folder, Model.Storage.ProjectPackage.AudioFolder);
            Directory.CreateDirectory(audio);
            var bits = file.BitDepth is 16 or 24 ? file.BitDepth : 32;
            var format = new BounceFormat(AudioFormat.Wav, bits, rate, DitherMode.None, NormalizeMode.Off, -16, -1);
            var files = new List<(string Path, WavInfo Info)>();
            foreach (var stem in stems) {
                if (!separated.TryGetValue(stem, out var output) || output is not { Length: > 0 }) {
                    throw ProjectException.Unprocessable($"The model made no {stem} stem.");
                }

                var path = Unique(audio, $"{Safe(region.Name)} {StemName(stem)}");
                written.Add(path);
                BounceProcessor.Write(path, output, format);
                files.Add((path, WavReader.Probe(path)));
                token.ThrowIfCancellationRequested();
            }

            Progress(entry, 0.95);
            session.ApplyJob(
                sessionId,
                "Split into Stems",
                files,
                (job, added) => job.Apply(
                    new AddStemsCommand(
                        region.Id,
                        [..stems.Select((s, i) => new StemFile(s, added[i].Id))],
                        keepSource
                    )
                )
            );
            return written;
        } catch {
            foreach (var path in written) {
                TryDelete(path);
            }

            throw;
        }
    }

    /// <summary>A file path in <paramref name="folder" /> named <paramref name="stem" />, numbered when taken.</summary>
    static string Unique(string folder, string stem) {
        var path = Path.Combine(folder, stem + ".wav");
        for (var n = 2; File.Exists(path); n++) {
            path = Path.Combine(folder, $"{stem} {n}.wav");
        }

        return path;
    }

    /// <summary>A stem's name in file and track names, from <see cref="Edits.StemTrackName" />.</summary>
    static string StemName(StemKind stem) => Edits.StemTrackName(string.Empty, stem);

    /// <summary>Progress reported on the calling thread (no synchronisation context: a job runs on the pool).</summary>
    sealed class ProgressTo(Action<double> report) : IProgress<double> {
        public void Report(double value) => report(double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);
    }
}
