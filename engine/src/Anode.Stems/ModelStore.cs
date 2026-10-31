using System.Security.Cryptography;
using System.Text.Json;

namespace Anode.Stems;

/// <summary>
///     A model file as <c>scripts/models.json</c> lists it: its name, where it is published (null until it is), its
///     SHA-256 (lowercase hex) and size in bytes.
/// </summary>
public sealed record ModelFile(StemModel Model, string FileName, Uri? Url, string Sha256, long Size);

/// <summary>
///     The separation models in one folder (the user library's, or <c>models/</c> for development): where each file is,
///     whether it is there, its checksum, and its download (docs/adr/0010-stem-splitter.md). The files and checksums
///     come from <c>scripts/models.json</c>, embedded in this assembly, so the server and
///     <c>scripts/fetch-models.mjs</c> agree. Safe to share between threads; two downloads of one model into one folder
///     at once are not.
/// </summary>
public sealed class ModelStore {
    readonly IReadOnlyList<ModelFile> files;

    /// <summary>The models of <see cref="Manifest" /> in <paramref name="folder" />.</summary>
    public ModelStore(string folder) : this(folder, Manifest) { }

    public ModelStore(string folder, IReadOnlyList<ModelFile> files) {
        Folder = folder;
        this.files = files;
    }

    /// <summary>The model files of <c>scripts/models.json</c>.</summary>
    public static IReadOnlyList<ModelFile> Manifest { get; } = ReadManifest();

    public string Folder { get; }

    /// <summary>The listed file of <paramref name="model" />.</summary>
    public ModelFile Describe(StemModel model) =>
        files.FirstOrDefault(f => f.Model == model)
        ?? throw new InvalidOperationException($"No model file is listed for {model}.");

    /// <summary>Where <paramref name="model" />'s file is (or goes).</summary>
    public string PathOf(StemModel model) => Path.Combine(Folder, Describe(model).FileName);

    /// <summary>Whether <paramref name="model" />'s file is there with the listed size (cheap; not the checksum).</summary>
    public bool IsPresent(StemModel model) {
        var info = new FileInfo(PathOf(model));
        return info.Exists && info.Length == Describe(model).Size;
    }

    /// <summary>Whether <paramref name="model" />'s file is there with the listed SHA-256 (reads the whole file).</summary>
    public async Task<bool> VerifyAsync(StemModel model, CancellationToken cancellationToken = default) {
        if (!IsPresent(model)) {
            return false;
        }

        await using var stream = File.OpenRead(PathOf(model));
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == Describe(model).Sha256;
    }

    /// <summary>
    ///     The path of <paramref name="model" />'s verified file, downloading it first (<see cref="DownloadAsync" />)
    ///     when it is missing or does not match.
    /// </summary>
    public async Task<string> EnsureAsync(
        StemModel model,
        HttpClient http,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        if (await VerifyAsync(model, cancellationToken).ConfigureAwait(false)) {
            progress?.Report(1);
            return PathOf(model);
        }

        return await DownloadAsync(model, http, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Downloads <paramref name="model" />'s file with <paramref name="http" /> into a temporary file beside it,
    ///     hashing as it goes, reporting progress 0…1 (by the listed size), and moves it into place when the SHA-256
    ///     matches. A mismatch is an <see cref="InvalidDataException" /> (nothing is kept); a model not yet published
    ///     (no URL) an <see cref="InvalidOperationException" />; network failures are <see cref="HttpRequestException" />.
    /// </summary>
    public async Task<string> DownloadAsync(
        StemModel model,
        HttpClient http,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        var file = Describe(model);
        var url = file.Url ?? throw new InvalidOperationException($"{file.FileName} is not published yet.");
        Directory.CreateDirectory(Folder);
        var path = PathOf(model);
        var temporary = path + ".download";
        try {
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false)) {
                await using var target = File.Create(temporary);
                await CopyHashedAsync(source, target, file, progress, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
            return path;
        } finally {
            File.Delete(temporary);
        }
    }

    /// <summary>
    ///     Installs <paramref name="model" /> from a local file (a model installed by hand, or a folder of fetched
    ///     models), checking its SHA-256; a mismatch is an <see cref="InvalidDataException" />.
    /// </summary>
    public async Task<string> InstallAsync(
        StemModel model,
        string sourcePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        var file = Describe(model);
        Directory.CreateDirectory(Folder);
        var path = PathOf(model);
        var temporary = path + ".download";
        try {
            await using (var source = File.OpenRead(sourcePath)) {
                await using var target = File.Create(temporary);
                await CopyHashedAsync(source, target, file, progress, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
            return path;
        } finally {
            File.Delete(temporary);
        }
    }

    static async Task CopyHashedAsync(
        Stream source,
        Stream target,
        ModelFile file,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    ) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (file.Size > 0) {
                progress?.Report(Math.Min(1, (double)copied / file.Size));
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (actual != file.Sha256) {
            throw new InvalidDataException($"{file.FileName}: SHA-256 {actual}, expected {file.Sha256}.");
        }

        progress?.Report(1);
    }

    static ModelFile[] ReadManifest() {
        using var stream = typeof(ModelStore).Assembly.GetManifestResourceStream("Anode.Stems.models.json")
            ?? throw new InvalidOperationException("Anode.Stems is built without scripts/models.json.");
        return Parse(stream);
    }

    /// <summary>Reads a manifest in the shape of <c>scripts/models.json</c>.</summary>
    public static ModelFile[] Parse(Stream json) {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var published = !root.TryGetProperty("published", out var flag) || flag.GetBoolean();
        var result = new List<ModelFile>();
        foreach (var entry in root.GetProperty("models").EnumerateArray()) {
            if (Entry(entry, published) is { } file) {
                result.Add(file);
            }
        }

        return [..result];
    }

    static ModelFile? Entry(JsonElement entry, bool published) {
        var name = entry.GetProperty("file").GetString() ?? string.Empty;
        StemModel? model = name == StemSeparators.FileName(StemModel.FourStems) ? StemModel.FourStems
            : name == StemSeparators.FileName(StemModel.SixStems) ? StemModel.SixStems
            : null;
        if (model is null) {
            return null;
        }

        var url = published && entry.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } text
            ? new Uri(text)
            : null;
        var sha = entry.TryGetProperty("sha256", out var hash) ? hash.GetString() ?? string.Empty : string.Empty;
        var size = entry.TryGetProperty("size", out var bytes) ? bytes.GetInt64() : 0;
        return new(model.Value, name, url, sha, size);
    }
}
