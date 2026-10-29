using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>An articulation of a set: its id (1…254, unique in the set), its name, and a short symbol (null: none).</summary>
public sealed record Articulation(int Id, string Name, string? Symbol);

/// <summary>The kind of MIDI an articulation switch listens for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArticulationSwitchKind>))]
public enum ArticulationSwitchKind {
    /// <summary>A keyswitch: a note-on of key <c>number</c>.</summary>
    [JsonStringEnumMemberName("note")]
    Note,

    /// <summary>A control change of controller <c>number</c>, with value <c>value</c> (null: any value).</summary>
    [JsonStringEnumMemberName("controlChange")]
    ControlChange
}

/// <summary>
///     An input switch: while recording (or playing live) a track with the set, this MIDI selects articulation
///     <c>articulationId</c> for the notes that follow; the switch itself is neither recorded nor played.
/// </summary>
public sealed record ArticulationSwitch(int ArticulationId, ArticulationSwitchKind Kind, int Number, int? Value);

/// <summary>What an articulation sends before each of its notes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArticulationOutputKind>))]
public enum ArticulationOutputKind {
    /// <summary>A keyswitch: key <c>number</c> at velocity <c>value</c> (1…127), one tick long.</summary>
    [JsonStringEnumMemberName("note")]
    Note,

    /// <summary>Control change <c>number</c> at <c>value</c>.</summary>
    [JsonStringEnumMemberName("controlChange")]
    ControlChange,

    /// <summary>Program change to <c>value</c> (0…127; <c>number</c> 0).</summary>
    [JsonStringEnumMemberName("program")]
    Program,

    /// <summary>No message: the note plays on channel <c>value</c> (1…16; <c>number</c> 0) instead of its own.</summary>
    [JsonStringEnumMemberName("channel")]
    Channel
}

/// <summary>
///     An output of articulation <c>articulationId</c>: sent at the note's tick, sorted before it, on the note's channel
///     (an articulation may have several outputs; they play in list order).
/// </summary>
public sealed record ArticulationOutput(int ArticulationId, ArticulationOutputKind Kind, int Number, int Value);

/// <summary>
///     An articulation set (Logic's Articulation Set): articulations, input switches and outputs. Ids start with
///     <c>ars_</c>; sets live in the project (<c>Project.articulationSets</c>, a track naming one in
///     <c>articulationSetId</c>) and in the user library (<c>/library/articulation-sets</c>).
/// </summary>
public sealed record ArticulationSet(
    string Id,
    string Name,
    IReadOnlyList<Articulation> Articulations,
    IReadOnlyList<ArticulationSwitch> Switches,
    IReadOnlyList<ArticulationOutput> Outputs
);
