using System.Runtime.InteropServices;

namespace Anode.Media.Ffmpeg;

/// <summary>
///     Where <c>ffmpeg</c> and <c>ffprobe</c> are: the pinned LGPL build fetched by <c>scripts/fetch-ffmpeg.mjs</c> for
///     development, bundled beside the server in a release (docs/adr/0013-video-and-listen-in.md, docs/media/README.md).
/// </summary>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath) {
    /// <summary>The environment variable naming a folder that holds the tools (itself or its <c>bin</c>).</summary>
    public const string EnvironmentVariable = "ANODE_FFMPEG";

    /// <summary>
    ///     This machine's platform as the fetch script names it (<c>macos-arm64</c>, <c>macos-x64</c>,
    ///     <c>windows-x64</c>, <c>windows-arm64</c>, <c>linux-x64</c>, <c>linux-arm64</c>): the folder under
    ///     <c>engine/artifacts/ffmpeg</c>.
    /// </summary>
    public static string Platform {
        get {
            var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";
            return $"{os}-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";
        }
    }

    /// <summary>
    ///     The tools in <paramref name="directory" /> (itself or its <c>bin</c>), or, when it is null, the first folder
    ///     that has both of: <see cref="EnvironmentVariable" />, beside the server (its folder, then its <c>ffmpeg</c>
    ///     subfolder), then the development fetch folder (<c>engine/artifacts/ffmpeg/&lt;platform&gt;</c> above the
    ///     server's folder). Null when none has both; the Server answers the media endpoints 503 without them.
    /// </summary>
    public static FfmpegTools? Locate(string? directory = null) {
        if (directory is not null) {
            return In(directory);
        }

        var named = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(named)) {
            // A folder named on purpose is the only one looked at, so a missing build is not hidden by another.
            return In(named);
        }

        var server = AppContext.BaseDirectory;
        return In(server) ?? In(Path.Combine(server, "ffmpeg")) ?? Development(server);
    }

    static FfmpegTools? Development(string start) {
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent) {
            var tools = In(Path.Combine(folder.FullName, "engine", "artifacts", "ffmpeg", Platform))
                ?? In(Path.Combine(folder.FullName, "artifacts", "ffmpeg", Platform));
            if (tools is not null) {
                return tools;
            }
        }

        return null;
    }

    static FfmpegTools? In(string directory) {
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        foreach (var folder in new[] { directory, Path.Combine(directory, "bin") }) {
            var ffmpeg = Path.Combine(folder, "ffmpeg" + extension);
            var ffprobe = Path.Combine(folder, "ffprobe" + extension);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) {
                return new(Path.GetFullPath(ffmpeg), Path.GetFullPath(ffprobe));
            }
        }

        return null;
    }
}
