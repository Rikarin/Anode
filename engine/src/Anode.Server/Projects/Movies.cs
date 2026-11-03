using Anode.Contracts;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Anode.Server.Projects;

/// <summary>
///     Where a project's movie and its thumbnails are (docs/11-phase-10.md, "Video"). A copied movie is under the
///     project's <c>Movies/</c> (its path relative), a referenced one where it is (absolute). Thumbnails are cached per
///     movie and height in <c>Movies/.thumbnails/&lt;key&gt;/&lt;height&gt;/&lt;frame&gt;.jpg</c>, the key a hash of the
///     movie's path, size and modification time, so a replaced file gets fresh thumbnails.
/// </summary>
public static class Movies {
    public const string Folder = "Movies";
    public const string ThumbnailsFolder = ".thumbnails";
    public const int DefaultThumbnailHeight = 64;
    public const int MinThumbnailHeight = 16;
    public const int MaxThumbnailHeight = 2160;
    public const int MaxThumbnailFrames = 4096;

    /// <summary>The movie files Anode imports.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".mov", ".mp4", ".m4v"];

    /// <summary>The movie's file on disk.</summary>
    public static string FileOf(string projectFolder, Movie movie) =>
        movie.InProject ? Path.GetFullPath(Path.Combine(projectFolder, movie.Path)) : movie.Path;

    /// <summary>A movie file's media type, by its extension.</summary>
    public static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch {
            ".mov" => "video/quicktime",
            ".m4v" => "video/x-m4v",
            _ => "video/mp4"
        };

    /// <summary>A thumbnail height as asked (left out: 64), checked.</summary>
    public static int Height(int? height) {
        var h = height ?? DefaultThumbnailHeight;
        return h is >= MinThumbnailHeight and <= MaxThumbnailHeight
            ? h
            : throw ProjectException.Unprocessable(
                $"A thumbnail is {MinThumbnailHeight}…{MaxThumbnailHeight} pixels high, not {h}."
            );
    }

    /// <summary>The cache folder of the movie's thumbnails <paramref name="height" /> pixels high.</summary>
    public static string ThumbnailFolder(string projectFolder, Movie movie, int height) {
        var file = FileOf(projectFolder, movie);
        var info = new FileInfo(file);
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{file}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}"
        );
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(
            projectFolder,
            Folder,
            ThumbnailsFolder,
            key,
            height.ToString(CultureInfo.InvariantCulture)
        );
    }

    /// <summary>A cached thumbnail's file (it may not exist).</summary>
    public static string ThumbnailOf(string projectFolder, Movie movie, int height, long frame) =>
        Path.Combine(
            ThumbnailFolder(projectFolder, movie, height),
            frame.ToString(CultureInfo.InvariantCulture) + ".jpg"
        );

    /// <summary>What is cached at <paramref name="height" />: the size (the width keeps the aspect, even) and the frames.</summary>
    public static MovieThumbnails Thumbnails(string projectFolder, Movie movie, int height) {
        var folder = ThumbnailFolder(projectFolder, movie, height);
        long[] frames = Directory.Exists(folder)
            ? [
                ..Directory.EnumerateFiles(folder, "*.jpg")
                    .Select(static f => long.TryParse(
                            Path.GetFileNameWithoutExtension(f),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var n
                        )
                            ? n
                            : -1
                    )
                    .Where(static n => n >= 0)
                    .Order()
            ]
            : [];
        var width = (int)Math.Max(2, Math.Round((double)height * movie.Width / movie.Height / 2) * 2);
        return new(width, height, frames);
    }

    /// <summary>The channel format of a file of <paramref name="channels" /> channels (each layout's count is its own).</summary>
    public static ChannelFormat FormatOf(int channels) =>
        SpeakerLayouts.All.FirstOrDefault(l => l.ChannelCount == channels)?.Format
        ?? throw ProjectException.Unprocessable($"Anode has no channel format of {channels} channels.");
}
