using Anode.Audio.Native;
using System.Text.Json.Serialization;

namespace Anode.Host;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IReadOnlyList<AudioDeviceInfo>))]
sealed partial class HostJsonContext : JsonSerializerContext;
