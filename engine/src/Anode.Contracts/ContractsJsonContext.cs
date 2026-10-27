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
public sealed partial class ContractsJsonContext : JsonSerializerContext;
