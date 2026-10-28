using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>A saved alternative of the open project (Logic's project alternatives); one is current.</summary>
public sealed record ProjectAlternative(string Id, string Name, DateTimeOffset Modified, bool Current);

public sealed record CreateAlternativeRequest(string Name);

/// <summary>An automatic backup, made at each save of the current alternative (the newest ten are kept).</summary>
public sealed record ProjectBackup(string Id, DateTimeOffset Saved);

/// <summary>Key bindings by command id (<c>transport.play</c>); each binding like <c>Space</c> or <c>Meta+Shift+Z</c>.</summary>
public sealed record KeyCommandSet(IReadOnlyDictionary<string, IReadOnlyList<string>> Bindings);

/// <summary>Folders pinned in the All Files browser.</summary>
public sealed record Bookmarks(IReadOnlyList<string> Paths);

[JsonConverter(typeof(JsonStringEnumConverter<FileKind>))]
public enum FileKind {
    [JsonStringEnumMemberName("folder")]
    Folder,

    [JsonStringEnumMemberName("audio")]
    Audio,

    [JsonStringEnumMemberName("midi")]
    Midi,

    [JsonStringEnumMemberName("project")]
    Project,

    [JsonStringEnumMemberName("instrument")]
    Instrument,

    [JsonStringEnumMemberName("other")]
    Other
}

/// <summary>
///     An entry of a folder on the server's disk. Audio files carry their format when it could be read
///     (<c>sampleRate</c>, <c>channels</c>, <c>frames</c>).
/// </summary>
public sealed record FileEntry(
    string Name,
    string Path,
    FileKind Kind,
    long Size,
    DateTimeOffset Modified,
    int? SampleRate,
    int? Channels,
    long? Frames
);

/// <summary>A folder's entries, folders first; <c>parent</c> is null at a root.</summary>
public sealed record DirectoryListing(string Path, string? Parent, IReadOnlyList<FileEntry> Entries);

/// <summary>Plays a file through the engine's preview channel (null stops it).</summary>
public sealed record PreviewRequest(string? Path);
