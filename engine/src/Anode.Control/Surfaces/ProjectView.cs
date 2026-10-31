using Anode.Contracts;

namespace Anode.Control.Surfaces;

/// <summary>
///     A parameter as a surface or OSC shows it: its automation address (or Smart Control id), name, value normalised
///     0…1, and its value as text in its unit ("−6.0 dB").
/// </summary>
public sealed record ParameterView(string Address, string Name, double Value, string Display);

/// <summary>An insert of the selected track, for the plug-in VPot mode: its id, name and parameters in order.</summary>
public sealed record InsertView(string Id, string Name, IReadOnlyList<ParameterView> Parameters);

/// <summary>
///     A track as surfaces and OSC see it: id, name, its fader position (0…1 on the fader law) and volume in dB, pan
///     (−1…1), switches, automation mode, meter (post-fader peak, 0…1 on the meter scale), sends in order and, for the
///     selected track only, its inserts (empty on the others).
/// </summary>
public sealed record TrackView(
    string Id,
    string Name,
    double Fader,
    double VolumeDb,
    double Pan,
    bool Mute,
    bool Solo,
    bool RecordArm,
    bool Selected,
    AutomationMode Automation,
    double Meter,
    IReadOnlyList<ParameterView> Sends,
    IReadOnlyList<InsertView> Inserts
);

/// <summary>A timecode (the project's frame rate, offset by its start).</summary>
public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames);

/// <summary>A musical position: bar, beat, division (16ths) and ticks (of 240), all 1-based but ticks.</summary>
public readonly record struct BeatPosition(int Bar, int Beat, int Division, int Ticks);

/// <summary>A marker, for OSC (<c>/anode/marker/{n}</c>, 1-based in project order).</summary>
public sealed record MarkerView(string Id, string Name, long Tick);

/// <summary>
///     What the server shows surfaces and OSC (docs/09-phase-8.md, "Surfaces"): the tracks a mixer shows, in order
///     (surfaces bank over them); the transport, cycle and tempo; the playhead as timecode and beats; the markers; and
///     the selected track's Smart Controls (empty when none is selected). Built by the server from the project, the
///     transport and the meters; the protocol classes compare it with what they last sent.
/// </summary>
public sealed record ProjectView(
    IReadOnlyList<TrackView> Tracks,
    TransportState Transport,
    bool Cycle,
    double Tempo,
    Timecode Timecode,
    BeatPosition Beats,
    IReadOnlyList<MarkerView> Markers,
    IReadOnlyList<ParameterView> SmartControls
);
