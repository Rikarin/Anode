using System.Text.Json.Nodes;

namespace Anode.Server.Tests;

/// <summary>The committed OpenAPI document (the editor's api-client is generated from it) and versioning.</summary>
public sealed class ContractTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string OpenApiPath => Path.Combine(EngineRoot(), "src", "Anode.Server", "openapi.json");

    [Fact]
    public async Task OpenApi_document_matches_the_committed_copy() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var current = Normalise(await client.GetStringAsync("/openapi/v1.json", Token));

        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_OPENAPI") == "1" || !File.Exists(OpenApiPath)) {
            await File.WriteAllTextAsync(OpenApiPath, current, Token);
        }

        var committed = Normalise(await File.ReadAllTextAsync(OpenApiPath, Token));
        Assert.True(
            committed == current,
            "The API changed. Review the difference, then update the committed document with "
            + "ANODE_UPDATE_OPENAPI=1 dotnet test, and regenerate the editor's client (nx run api-client:generate)."
        );
        Assert.Contains("\"/api/v1/system\"", current, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_version_matches_the_editor_workspace() {
        var package = Path.Combine(EngineRoot(), "..", "editor", "package.json");
        Assert.SkipUnless(File.Exists(package), "No editor workspace next to the engine.");
        var editorVersion = JsonNode.Parse(File.ReadAllText(package))!["version"]!.GetValue<string>();
        var engineVersion = Engine.EngineConnection.Version.Split('+')[0];
        Assert.True(
            Environment.GetEnvironmentVariable("ANODE_VERSION") is { Length: > 0 } || editorVersion == engineVersion,
            $"engine/Directory.Build.props says {engineVersion}, editor/package.json says {editorVersion}."
        );
    }

    /// <summary>Canonical form; XML doc text carries the platform's line endings into descriptions, so unify them.</summary>
    static string Normalise(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new() { WriteIndented = true })
            .Replace("""\r\n""", """\n""", StringComparison.Ordinal)
        + "\n";

    static string EngineRoot() {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "Anode.slnx"))) {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Anode.slnx not found above the test's folder.");
    }
}
