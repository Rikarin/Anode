using Anode.Stems;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Anode.Server.Projects;

/// <summary>
///     Where the Stem Splitter's separators come from (docs/adr/0010-stem-splitter.md): a model by its kind, fetched on
///     first use with progress (0…1) and cancellation, then opened. Tests substitute their own.
/// </summary>
public interface IStemModels {
    Task<IStemSeparator> OpenAsync(StemModel model, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
///     The models in the user library, <c>&lt;DataDirectory&gt;/Models/</c>, through workstream E's
///     <see cref="ModelStore" /> (the files and SHA-256s of <c>scripts/models.json</c>): a model there with its checksum
///     opens as it is; a missing one is installed from <see cref="ServerOptions.ModelSource" /> when that folder has it
///     (development and the e2e tests point it at the repository's <c>models/</c>), else downloaded by checksum with
///     progress. Until the models are published (ADR-0010) a missing model with no source is refused (422), saying where
///     to put it. A model verified once is not hashed again while its file is unchanged.
/// </summary>
public sealed class StemModels(IOptions<ServerOptions> options) : IStemModels {
    public const string Folder = "Models";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(1) };

    /// <summary>The files verified so far, with their size and time of last write then.</summary>
    readonly ConcurrentDictionary<string, (long Length, DateTime Written)> verified = new(StringComparer.Ordinal);

    public string Directory => Path.Combine(options.Value.DataDirectory, Folder);

    public async Task<IStemSeparator> OpenAsync(
        StemModel model,
        IProgress<double> progress,
        CancellationToken cancellationToken
    ) {
        var store = new ModelStore(Directory);
        var path = await PathAsync(store, model, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(1);
        try {
            return StemSeparators.Open(model, path);
        } catch (InvalidDataException e) {
            throw ProjectException.Unprocessable($"The Stem Splitter's model is damaged: {e.Message}");
        }
    }

    /// <summary>The verified file of <paramref name="model" />, installed or downloaded first when it is not there.</summary>
    async Task<string> PathAsync(
        ModelStore store,
        StemModel model,
        IProgress<double> progress,
        CancellationToken cancellationToken
    ) {
        var path = store.PathOf(model);
        try {
            if (IsVerified(path)) {
                return path;
            }

            if (await store.VerifyAsync(model, cancellationToken).ConfigureAwait(false)) {
                Remember(path);
                return path;
            }

            if (options.Value.ModelSource is { Length: > 0 } source
                && Path.Combine(source, store.Describe(model).FileName) is var file
                && File.Exists(file)) {
                await store.InstallAsync(model, file, progress, cancellationToken).ConfigureAwait(false);
            } else {
                await store.DownloadAsync(model, Http, progress, cancellationToken).ConfigureAwait(false);
            }

            Remember(path);
            return path;
        } catch (InvalidOperationException) {
            throw ProjectException.Unprocessable(
                $"The Stem Splitter's model is not installed: put {Path.GetFileName(path)} in '{Directory}', or set "
                + "Anode:ModelSource (ANODE_MODELS) to a folder that has it (scripts/fetch-models.mjs makes one)."
            );
        } catch (InvalidDataException e) {
            throw ProjectException.Unprocessable($"The Stem Splitter's model does not match its checksum: {e.Message}");
        } catch (HttpRequestException e) {
            throw ProjectException.Unprocessable($"The Stem Splitter's model could not be downloaded: {e.Message}");
        }
    }

    bool IsVerified(string path) {
        var info = new FileInfo(path);
        return info.Exists
            && verified.TryGetValue(path, out var seen)
            && seen == (info.Length, info.LastWriteTimeUtc);
    }

    void Remember(string path) {
        var info = new FileInfo(path);
        verified[path] = (info.Length, info.LastWriteTimeUtc);
    }
}
