using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     What changed between two revisions of the open project (docs/adr/0004-state-ownership.md). Clients apply the
///     operations in order; a client that misses a revision refetches the project.
/// </summary>
public sealed record ProjectDelta(long Revision, IReadOnlyList<DeltaOp> Ops);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(TrackUpserted), "trackUpserted")]
[JsonDerivedType(typeof(TrackRemoved), "trackRemoved")]
[JsonDerivedType(typeof(RegionUpserted), "regionUpserted")]
[JsonDerivedType(typeof(RegionRemoved), "regionRemoved")]
[JsonDerivedType(typeof(AudioFileUpserted), "audioFileUpserted")]
[JsonDerivedType(typeof(SettingsChanged), "settingsChanged")]
[JsonDerivedType(typeof(MasterChanged), "masterChanged")]
[JsonDerivedType(typeof(ProjectRenamed), "projectRenamed")]
public abstract record DeltaOp;

/// <summary>A track was added or changed; <c>index</c> is its position in the track list.</summary>
public sealed record TrackUpserted(Track Track, int Index) : DeltaOp;

public sealed record TrackRemoved(string Id) : DeltaOp;

public sealed record RegionUpserted(AudioRegion Region) : DeltaOp;

public sealed record RegionRemoved(string Id) : DeltaOp;

public sealed record AudioFileUpserted(AudioFile File) : DeltaOp;

public sealed record SettingsChanged(ProjectSettings Settings) : DeltaOp;

public sealed record MasterChanged(ChannelStrip Strip) : DeltaOp;

public sealed record ProjectRenamed(string Name) : DeltaOp;
