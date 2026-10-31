using Anode.Stems;
using Microsoft.Extensions.Options;

namespace Anode.Server.Projects;

/// <summary>
///     Where the Stem Splitter's separators come from (docs/adr/0010-stem-splitter.md): a model by its kind, fetched on
///     first use with progress (0…1) and cancellation, then opened. Tests substitute their own.
/// </summary>
public interface IStemModels {
    Task<IStemSeparator> OpenAsync(StemModel model, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
///     The models in the user library, <c>&lt;DataDirectory&gt;/Models/</c> under
///     <see cref="StemSeparators.FileName" />, opened with <see cref="StemSeparators.Open" /> (workstream E). A
///     missing model is refused (422) until E's <c>ModelStore</c> downloads it here by checksum on first use,
///     reporting its progress.
/// </summary>
public sealed class StemModels(IOptions<ServerOptions> options) : IStemModels {
    public const string Folder = "Models";

    public string Directory => Path.Combine(options.Value.DataDirectory, Folder);

    public Task<IStemSeparator> OpenAsync(
        StemModel model,
        IProgress<double> progress,
        CancellationToken cancellationToken
    ) {
        string path;
        try {
            path = Path.Combine(Directory, StemSeparators.FileName(model));
        } catch (NotImplementedException e) {
            throw ProjectException.Unprocessable($"The Stem Splitter is not available yet: {e.Message}.");
        }

        if (!File.Exists(path)) {
            throw ProjectException.Unprocessable(
                $"The Stem Splitter's model is not installed: put {Path.GetFileName(path)} in '{Directory}' "
                + "(scripts/fetch-models.mjs fetches it)."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(1);
        try {
            return Task.FromResult(StemSeparators.Open(model, path));
        } catch (InvalidDataException e) {
            throw ProjectException.Unprocessable($"The Stem Splitter's model is damaged: {e.Message}");
        } catch (NotImplementedException e) {
            throw ProjectException.Unprocessable($"The Stem Splitter is not available yet: {e.Message}.");
        }
    }
}
