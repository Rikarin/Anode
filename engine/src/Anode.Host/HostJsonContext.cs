using Anode.Audio.Native;
using System.Text.Json.Serialization;

namespace Anode.Host;

/// <summary>What <c>--devices --json</c> prints: the backend (<c>none</c> without one) and its devices.</summary>
sealed record AudioDeviceList(string Backend, IReadOnlyList<AudioDeviceInfo> Devices);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AudioDeviceList))]
sealed partial class HostJsonContext : JsonSerializerContext;
