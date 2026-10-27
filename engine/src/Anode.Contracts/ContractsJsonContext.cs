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
public sealed partial class ContractsJsonContext : JsonSerializerContext;
