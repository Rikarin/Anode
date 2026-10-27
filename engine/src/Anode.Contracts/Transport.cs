using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<TransportState>))]
public enum TransportState {
    [JsonStringEnumMemberName("stopped")]
    Stopped,

    [JsonStringEnumMemberName("playing")]
    Playing,

    [JsonStringEnumMemberName("recording")]
    Recording
}

[JsonConverter(typeof(JsonStringEnumConverter<TransportCommand>))]
public enum TransportCommand {
    [JsonStringEnumMemberName("play")]
    Play,

    [JsonStringEnumMemberName("stop")]
    Stop,

    [JsonStringEnumMemberName("record")]
    Record,

    [JsonStringEnumMemberName("locate")]
    Locate
}

/// <summary>Play, stop or record (from <c>tick</c> when given), or locate the playhead to <c>tick</c>.</summary>
public sealed record TransportRequest(TransportCommand Command, long? Tick = null);

/// <summary>The playhead: state and position, in samples and ticks.</summary>
public sealed record TransportStatus(TransportState State, long PositionSamples, long PositionTick, int SampleRate);
