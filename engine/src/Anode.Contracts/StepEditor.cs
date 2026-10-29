using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>What a Step Editor lane shows and draws.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaneKind>))]
public enum LaneKind {
    /// <summary>Every note's velocity.</summary>
    [JsonStringEnumMemberName("velocity")]
    Velocity,

    /// <summary>Control change <c>number</c>.</summary>
    [JsonStringEnumMemberName("controlChange")]
    ControlChange,

    [JsonStringEnumMemberName("pitchBend")]
    PitchBend,

    [JsonStringEnumMemberName("channelPressure")]
    ChannelPressure,

    /// <summary>Poly pressure on key <c>number</c>.</summary>
    [JsonStringEnumMemberName("polyPressure")]
    PolyPressure,

    [JsonStringEnumMemberName("program")]
    Program,

    /// <summary>The notes of pitch <c>number</c> (a drum's own lane): their velocity, drawn as notes.</summary>
    [JsonStringEnumMemberName("note")]
    Note
}

/// <summary>
///     A Step Editor lane: its kind, its controller or key (<c>number</c>: for <c>controlChange</c>,
///     <c>polyPressure</c> and <c>note</c>; null otherwise), its name, and the grid the pencil and line tools draw on
///     (<c>resolutionTicks</c> &gt; 0).
/// </summary>
public sealed record Lane(LaneKind Kind, int? Number, string Name, long ResolutionTicks);

/// <summary>
///     A saved set of Step Editor lanes (Logic's lane sets), in the project (<c>Project.laneSets</c>; ids
///     <c>lns_</c>). A project without any shows the default set: Velocity, Modulation (CC 1), Pitch Bend, Channel
///     Pressure and Program, each at a 1/16 (240 ticks); the first <c>setLaneSet</c> stores the set it edits.
/// </summary>
public sealed record LaneSet(string Id, string Name, IReadOnlyList<Lane> Lanes);
