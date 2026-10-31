using Anode.Contracts;
using Anode.Server.Projects;
using Anode.Stems;
using Microsoft.Extensions.Options;

namespace Anode.Server.Tests;

/// <summary>
///     The server's Stem Splitter models through workstream E's <see cref="ModelStore" />: refused while not installed
///     (the models are not published, ADR-0010), and installed from <see cref="ServerOptions.ModelSource" /> by checksum
///     when a folder of fetched models is given (the repository's <c>models/</c>, or <c>ANODE_MODELS</c>; skipped when
///     the models were not exported here).
/// </summary>
public sealed class StemModelsTests : IDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string data = Directory.CreateTempSubdirectory("anode-models-").FullName;

    public void Dispose() => Directory.Delete(data, true);

    StemModels Models(string? source) =>
        new(Options.Create(new ServerOptions { DataDirectory = data, ModelSource = source }));

    static string RepositoryModels() {
        if (Environment.GetEnvironmentVariable("ANODE_MODELS") is { Length: > 0 } folder) {
            return folder;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "models.json"))) {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? ".", "models");
    }

    [Fact]
    public async Task A_model_that_is_not_installed_and_has_no_source_is_refused_saying_where_it_goes() {
        var models = Models(Path.Combine(data, "nowhere"));
        var refused = await Assert.ThrowsAsync<ProjectException>(() =>
            models.OpenAsync(StemModel.FourStems, new Progress<double>(), Token)
        );
        Assert.Equal(422, refused.Status);
        Assert.Contains("htdemucs.onnx", refused.Message, StringComparison.Ordinal);
        Assert.Contains(models.Directory, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_model_is_installed_from_the_source_folder_by_checksum_and_opened() {
        var source = RepositoryModels();
        Assert.SkipUnless(
            File.Exists(Path.Combine(source, "htdemucs.onnx")),
            $"No {source}/htdemucs.onnx: export it (scripts/export-demucs.py) or fetch it (scripts/fetch-models.mjs)."
        );
        var models = Models(source);
        var reported = new List<double>();
        using (var separator = await models.OpenAsync(
                   StemModel.FourStems,
                   new SynchronousProgress(reported.Add),
                   Token
               )) {
            Assert.Equal(
                [StemKind.Drums, StemKind.Bass, StemKind.Other, StemKind.Vocals],
                separator.Stems
            );
        }

        Assert.True(new ModelStore(models.Directory).IsPresent(StemModel.FourStems));
        Assert.Equal(1, reported[^1]);
        // Installed: opened again from the library.
        using var again = await Models(null).OpenAsync(StemModel.FourStems, new Progress<double>(), Token);
        Assert.Equal(StemModel.FourStems, again.Model);
    }

    sealed class SynchronousProgress(Action<double> report) : IProgress<double> {
        public void Report(double value) => report(value);
    }
}
