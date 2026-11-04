using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Storage;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anode.Server.Projects;

/// <summary>
///     Changing the project's sample rate as a job. Each file gets a new one beside it, under a new id, so its peaks and
///     analysis are its own and backups and alternatives keep theirs. A converted file keeps its bit depth unless the
///     conversion peaks over full scale, when it stays float rather than clip.
/// </summary>
public sealed partial class Jobs {
    /// <summary>Queues a change of sample rate, checked first (<see cref="ProjectSession.PlanRate" />).</summary>
    public Job ChangeSampleRate(ChangeSampleRateRequest? request) {
        var plan = session.PlanRate(request);
        return Enqueue(
            JobKind.SampleRateChange,
            async (entry, token) => {
                var written = new List<string>();
                var committed = false;
                try {
                    var files = await Task.Run(() => WriteRateFiles(entry, plan, written, token), token)
                        .ConfigureAwait(false);
                    var states = await engine.InstrumentStatesAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    session.CommitRate(plan, files, states, () => committed = true);
                    return written;
                } catch when (!committed) {
                    written.ForEach(TryDelete);
                    throw;
                }
            }
        );
    }

    Dictionary<string, AudioFile> WriteRateFiles(
        Entry entry,
        RatePlan plan,
        List<string> written,
        CancellationToken token
    ) {
        var files = new Dictionary<string, AudioFile>(StringComparer.Ordinal);
        foreach (var (file, index) in plan.Files.Select(static (f, i) => (f, i))) {
            token.ThrowIfCancellationRequested();
            var source = Path.Combine(plan.Folder, file.Path);
            var path = RatePath(source, plan.Rate);
            written.Add(path);
            if (plan.Mode == SampleRateChangeMode.Convert) {
                Convert(file, source, path, plan.Rate);
            } else {
                ProjectRate.Relabel(source, path, plan.Rate);
            }

            var info = Checked(file, path, plan.Rate, plan.Mode);
            var relative = Path.GetRelativePath(plan.Folder, path).Replace(Path.DirectorySeparatorChar, '/');
            var result = ProjectRate.Written(file, RandomIds.Instance.Next(IdPrefix.File), relative, info, plan.Mode);
            files[file.Id] = result;
            if (FileAnalyses.Cached(plan.Folder, file.Id) is { } analysis) {
                var target = FileAnalyses.PathOf(plan.Folder, result.Id);
                var hop = FileAnalyses.Hop(plan.Rate);
                written.Add(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                AtomicRename.Write(
                    target,
                    JsonSerializer.Serialize(
                        ProjectRate.Written(analysis, file, result, hop, plan.Mode),
                        ContractsJsonContext.Default.FileAnalysis
                    )
                );
            }

            Progress(entry, 0.9 * (index + 1) / plan.Files.Count);
        }

        return files;
    }

    void Convert(AudioFile file, string source, string path, int rate) {
        var (channels, from, layout) = BounceProcessor.ReadLayout(source);
        var frames = channels.Length == 0 ? 0 : channels[0].LongLength;
        (float[][] Channels, int SampleRate) converted;
        try {
            converted = editor.Apply(
                channels,
                from,
                new(AudioEditKind.ConvertRate, 0, frames, TargetRate: rate)
            );
        } catch (ArgumentException e) {
            throw ProjectException.Unprocessable($"Could not convert '{file.Name}': {e.Message}");
        }

        var bits = file.BitDepth is 16 or 24 && !Overs(converted.Channels) ? file.BitDepth : 32;
        var temporary = path + ".tmp";
        try {
            BounceProcessor.Write(
                temporary,
                converted.Channels,
                new(AudioFormat.Wav, bits, converted.SampleRate, DitherMode.None, NormalizeMode.Off, -16, -1),
                0,
                layout
            );
            File.Move(temporary, path);
        } finally {
            TryDelete(temporary);
        }
    }

    /// <summary>Whether a conversion's overshoot would clip in an integer file.</summary>
    static bool Overs(float[][] channels) => channels.Any(static c => Array.Exists(c, static x => MathF.Abs(x) > 1));

    /// <summary>A written file must be what it was written for: an empty or wrongly made one stops the change here.</summary>
    static WavInfo Checked(AudioFile file, string path, int rate, SampleRateChangeMode mode) {
        var info = WavReader.Probe(path);
        var expected = mode == SampleRateChangeMode.Convert
            ? ProjectRate.Scale(file.Frames, file.SampleRate, rate)
            : file.Frames;
        return info.SampleRate == rate
            && info.Channels == file.Channels
            && Math.Abs(info.Frames - expected) <= MaxRateFrameDrift(rate)
            && info.Frames > 0
                ? info
                : throw ProjectException.Unprocessable(
                    $"The converted '{file.Name}' came out with {info.Frames} frames at {info.SampleRate} Hz, not "
                    + $"{expected} at {rate} Hz."
                );
    }

    /// <summary>How far a converter's length may be from the exact one: a millisecond.</summary>
    static long MaxRateFrameDrift(int rate) => rate / 1000;

    /// <summary>A free path beside <paramref name="source" /> for its version at <paramref name="rate" />.</summary>
    static string RatePath(string source, int rate) {
        var directory = Path.GetDirectoryName(source)!;
        var stem = RateSuffix().Replace(Path.GetFileNameWithoutExtension(source), string.Empty);
        var path = Path.Combine(directory, $"{stem}.{rate}.wav");
        for (var n = 2; File.Exists(path); n++) {
            path = Path.Combine(directory, $"{stem}.{rate} {n}.wav");
        }

        return path;
    }

    [GeneratedRegex(@"\.[0-9]{5,6}( [0-9]+)?$")]
    private static partial Regex RateSuffix();
}
