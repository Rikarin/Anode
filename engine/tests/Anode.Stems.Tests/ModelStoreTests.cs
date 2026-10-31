using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Anode.Stems.Tests;

/// <summary>The model store: the manifest, checksums, downloads and installs (docs/adr/0010-stem-splitter.md).</summary>
public sealed class ModelStoreTests : IDisposable {
    readonly string folder = Path.Combine(Path.GetTempPath(), "anode-stems-" + Guid.NewGuid().ToString("N"));
    readonly byte[] content = Encoding.UTF8.GetBytes("not really a model, but a file with a checksum");

    public void Dispose() {
        if (Directory.Exists(folder)) {
            Directory.Delete(folder, true);
        }
    }

    ModelStore Store(string? sha = null, Uri? url = null) =>
        new(
            folder,
            [
                new(
                    StemModel.FourStems,
                    "htdemucs.onnx",
                    url ?? new Uri("https://example.test/htdemucs.onnx"),
                    sha ?? Convert.ToHexStringLower(SHA256.HashData(content)),
                    content.Length
                )
            ]
        );

    [Fact]
    public void The_manifest_lists_both_models_with_checksums() {
        var manifest = ModelStore.Manifest;
        Assert.Equal(2, manifest.Count);
        foreach (var file in manifest) {
            Assert.Equal(StemSeparators.FileName(file.Model), file.FileName);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.True(file.Size > 1_000_000);
        }
    }

    [Fact]
    public void A_manifest_not_yet_published_has_no_urls() {
        var json = """
                   { "published": false, "models": [ { "file": "htdemucs.onnx", "url": "https://x.test/a", "sha256": "ab", "size": 2 } ] }
                   """;
        var files = ModelStore.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Null(Assert.Single(files).Url);
        var published = ModelStore.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json.Replace("false", "true"))));
        Assert.Equal(new("https://x.test/a"), Assert.Single(published).Url);
    }

    [Fact]
    public async Task A_download_is_verified_and_reports_progress() {
        var store = Store();
        Assert.False(store.IsPresent(StemModel.FourStems));
        using var http = new HttpClient(new Serve(content));
        var reports = new List<double>();
        var path = await store.EnsureAsync(
            StemModel.FourStems,
            http,
            new Inline(reports.Add),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(store.PathOf(StemModel.FourStems), path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(store.IsPresent(StemModel.FourStems));
        Assert.True(await store.VerifyAsync(StemModel.FourStems, TestContext.Current.CancellationToken));
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task A_download_that_does_not_match_keeps_nothing() {
        var store = Store(new('0', 64));
        using var http = new HttpClient(new Serve(content));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.DownloadAsync(StemModel.FourStems, http, null, TestContext.Current.CancellationToken)
        );
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_model_installs_from_a_local_file() {
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.bin");
        await File.WriteAllBytesAsync(source, content, TestContext.Current.CancellationToken);
        var store = Store();
        var path = await store.InstallAsync(StemModel.FourStems, source, null, TestContext.Current.CancellationToken);
        Assert.True(await store.VerifyAsync(StemModel.FourStems, TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(folder, "htdemucs.onnx"), path);
    }

    [Fact]
    public async Task An_unpublished_model_cannot_download() {
        var store = new ModelStore(folder, [new(StemModel.FourStems, "htdemucs.onnx", null, "00", 1)]);
        using var http = new HttpClient(new Serve(content));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync(StemModel.FourStems, http, null, TestContext.Current.CancellationToken)
        );
    }

    sealed class Serve(byte[] body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    sealed class Inline(Action<double> report) : IProgress<double> {
        public void Report(double value) => report(value);
    }
}
