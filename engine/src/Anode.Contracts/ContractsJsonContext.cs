using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>Source-generated serialisation for every contract (camelCase, nulls omitted), for AOT and speed.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(EngineStatus))]
[JsonSerializable(typeof(RealtimeEvent))]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(ProjectSummary))]
[JsonSerializable(typeof(IReadOnlyList<ProjectSummary>))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(OpenProjectRequest))]
[JsonSerializable(typeof(ImportFileRequest))]
[JsonSerializable(typeof(AudioFile))]
[JsonSerializable(typeof(CommandBatch))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(UndoHistory))]
[JsonSerializable(typeof(TransportRequest))]
[JsonSerializable(typeof(TransportStatus))]
[JsonSerializable(typeof(IReadOnlyList<PluginInfo>))]
[JsonSerializable(typeof(MidiDevices))]
[JsonSerializable(typeof(LiveMidiRequest))]
[JsonSerializable(typeof(MidiThruRequest))]
[JsonSerializable(typeof(IReadOnlyList<ProjectAlternative>))]
[JsonSerializable(typeof(CreateAlternativeRequest))]
[JsonSerializable(typeof(IReadOnlyList<ProjectBackup>))]
[JsonSerializable(typeof(IReadOnlyList<Screenset>))]
[JsonSerializable(typeof(Screenset))]
[JsonSerializable(typeof(KeyCommandSet))]
[JsonSerializable(typeof(Bookmarks))]
[JsonSerializable(typeof(DirectoryListing))]
[JsonSerializable(typeof(PreviewRequest))]
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(BounceRequest))]
[JsonSerializable(typeof(IoLabels))]
[JsonSerializable(typeof(IReadOnlyList<PatchInfo>))]
[JsonSerializable(typeof(PatchInfo))]
[JsonSerializable(typeof(SavePatchRequest))]
[JsonSerializable(typeof(PluginEditorRequest))]
[JsonSerializable(typeof(FileAnalysis))]
[JsonSerializable(typeof(BounceInPlaceRequest))]
[JsonSerializable(typeof(ProcessRequest))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
