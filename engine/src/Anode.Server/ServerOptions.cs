namespace Anode.Server;

/// <summary>Configuration section <c>Anode</c>.</summary>
public sealed class ServerOptions {
    /// <summary>Where the library, settings and recent projects live.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anode");

    /// <summary>
    ///     Access token every request must present (<c>Authorization: Bearer</c>). Required when listening beyond
    ///     localhost; the desktop app generates one per run.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    ///     The editor's web build to serve at the root (LAN mode, or a headless box's packaged <c>editor</c> folder), or
    ///     null to serve only the API.
    /// </summary>
    public string? EditorDirectory { get; set; }

    /// <summary>
    ///     A folder of Stem Splitter models (<c>scripts/fetch-models.mjs</c>'s output, such as the repository's
    ///     <c>models/</c>) to install a missing model from, checksum verified, before trying to download it; default the
    ///     <c>ANODE_MODELS</c> environment variable (development and the e2e tests).
    /// </summary>
    public string? ModelSource { get; set; } = Environment.GetEnvironmentVariable("ANODE_MODELS");

    public EngineOptions Engine { get; set; } = new();
}

public sealed class EngineOptions {
    /// <summary><c>ipc</c> (production: a separate anode-host process) or <c>inprocess</c> (tests, no audio).</summary>
    public string Mode { get; set; } = "ipc";

    /// <summary>The host's Unix domain socket, when <see cref="Mode" /> is <c>ipc</c>.</summary>
    public string SocketPath { get; set; } = Path.Combine(Path.GetTempPath(), "anode-host.sock");
}
