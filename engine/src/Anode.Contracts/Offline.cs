using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>Why a file of the project is offline.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OfflineReason>))]
public enum OfflineReason {
    /// <summary>Nothing is at its path.</summary>
    [JsonStringEnumMemberName("missing")]
    Missing,

    /// <summary>Something else is at its path: unreadable, or not the length, rate or channels the project knows.</summary>
    [JsonStringEnumMemberName("changed")]
    Changed,

    /// <summary>Regions name it but the project has lost its entry, so only its rate and least length are known.</summary>
    [JsonStringEnumMemberName("unlisted")]
    Unlisted
}

/// <summary>
///     A file of the project the disk cannot give. Its regions stay, play silence (a <c>changed</c> file plays what is
///     there) and save as they are. <c>detail</c> says what is wrong with a <c>changed</c> file.
/// </summary>
public sealed record OfflineFile(string FileId, string Name, string Path, OfflineReason Reason, string? Detail);

/// <summary>
///     What the project could not find when last checked: its offline files, and the tracks whose
///     <c>tracks/&lt;id&gt;.json</c> is missing or unreadable (they are not in the project, and saving keeps their files).
/// </summary>
public sealed record OfflineReport(IReadOnlyList<OfflineFile> Files, IReadOnlyList<string> UnreadableTracks) {
    public static OfflineReport None { get; } = new([], []);
}

/// <summary>Searches a folder of the server's disk and those inside it (left out: the project's own).</summary>
public sealed record OfflineSearchRequest(string? Directory);

/// <summary>
///     A WAV with the name of an offline file. <c>problem</c> is null when its length, rate and channels match, so it
///     can be relinked; otherwise it says what differs.
/// </summary>
public sealed record OfflineCandidate(string FileId, string Path, string? Problem);

/// <summary>What a search found, those that match first; <c>truncated</c>: it stopped before seeing the whole folder.</summary>
public sealed record OfflineSearch(string Directory, IReadOnlyList<OfflineCandidate> Candidates, bool Truncated);

/// <summary>Points a file of the project at a WAV on the server's disk.</summary>
public sealed record RelinkTarget(string FileId, string Path);

/// <summary>Relinks files, as one undo step.</summary>
public sealed record RelinkRequest(IReadOnlyList<RelinkTarget> Files);
