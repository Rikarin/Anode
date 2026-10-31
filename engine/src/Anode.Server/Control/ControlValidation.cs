using Anode.Contracts;
using Anode.Server.Projects;

namespace Anode.Server.Control;

/// <summary>
///     The shape checks of the control settings (docs/09-phase-8.md), as the server answers them (422): the assignment
///     engine checks them again when it is built.
/// </summary>
public static class ControlValidation {
    public const int MaxZones = 64;
    public const int MaxModes = 64;
    public const int MaxAssignments = 4096;
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 128;
    public const int MaxPortLength = 256;
    public const int MaxSurfaces = 16;
    public const int MaxOscClients = 32;
    public const int MaxHostLength = 253;

    /// <summary>
    ///     The assignments checked: ids unique across zones, modes and assignments; every zone has a mode and its active
    ///     one; inputs, targets, modes and ranges valid; zoneMode targets name a mode of their zone.
    /// </summary>
    public static ControllerAssignments Check(ControllerAssignments? assignments) {
        if (assignments?.Zones is not { } zones || zones.Count > MaxZones) {
            throw ProjectException.Unprocessable($"Send the zones (at most {MaxZones}).");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var modes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var zone in zones) {
            modes[zone?.Id ?? string.Empty] = Check(zone, ids, ref count);
        }

        foreach (var target in zones.SelectMany(static z => z.Modes)
                     .SelectMany(static m => m.Assignments)
                     .Select(static a => a.Target)
                     .Where(static t => t.Kind == ControlTargetKind.ZoneMode)) {
            CheckMode(target.ZoneId, target.ModeId, modes);
        }

        return assignments;
    }

    /// <summary>A zone checked (its id, name, modes and active mode); the ids of its modes.</summary>
    static HashSet<string> Check(ControlZone? zone, HashSet<string> ids, ref int count) {
        if (zone is null) {
            throw ProjectException.Unprocessable("A zone is null.");
        }

        Id(zone.Id, ids);
        Name(zone.Name);
        if (zone.Modes is not { Count: > 0 and <= MaxModes }) {
            throw ProjectException.Unprocessable($"Zone '{zone.Id}' has 1…{MaxModes} modes.");
        }

        var mine = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in zone.Modes) {
            if (mode is null) {
                throw ProjectException.Unprocessable($"A mode of zone '{zone.Id}' is null.");
            }

            Id(mode.Id, ids);
            Name(mode.Name);
            mine.Add(mode.Id);
            if (mode.Assignments is null || (count += mode.Assignments.Count) > MaxAssignments) {
                throw ProjectException.Unprocessable($"Send each mode's assignments (at most {MaxAssignments}).");
            }

            foreach (var assignment in mode.Assignments) {
                Check(assignment, mode.Id, ids);
            }
        }

        return mine.Contains(zone.ActiveModeId ?? string.Empty)
            ? mine
            : throw ProjectException.Unprocessable($"Zone '{zone.Id}''s active mode is not one of its modes.");
    }

    /// <summary>An assignment checked: its id, name, input, target, mode and range.</summary>
    static void Check(ControlAssignment? assignment, string modeId, HashSet<string> ids) {
        if (assignment is null) {
            throw ProjectException.Unprocessable($"An assignment of mode '{modeId}' is null.");
        }

        Id(assignment.Id, ids);
        if (assignment.Name is { } name) {
            Name(name);
        }

        Check(assignment.Input);
        Check(assignment.Target);
        if (!Enum.IsDefined(assignment.Mode)) {
            throw ProjectException.Unprocessable($"Assignment '{assignment.Id}' has an unknown mode.");
        }

        if (!(assignment.Min is >= 0 and <= 1 && assignment.Max is >= 0 and <= 1)) {
            throw ProjectException.Unprocessable($"Assignment '{assignment.Id}''s range is within 0…1 (normalised).");
        }
    }

    /// <summary>A zone and mode a target or Learn names, checked against <paramref name="modes" /> (by zone).</summary>
    public static void CheckMode(string? zoneId, string? modeId, IReadOnlyDictionary<string, HashSet<string>> modes) {
        if (zoneId is null || !modes.TryGetValue(zoneId, out var mine) || !mine.Contains(modeId ?? string.Empty)) {
            throw ProjectException.Unprocessable($"There is no mode '{modeId}' in zone '{zoneId}'.");
        }
    }

    /// <summary>The modes of each zone.</summary>
    public static Dictionary<string, HashSet<string>> Modes(ControllerAssignments assignments) =>
        assignments.Zones.ToDictionary(
            static z => z.Id,
            static z => z.Modes.Select(static m => m.Id).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal
        );

    public static void Check(ControlInput? input) {
        if (input is null || !Enum.IsDefined(input.Kind)) {
            throw ProjectException.Unprocessable("An assignment's input has a known kind.");
        }

        if (input.Port is { } port && !IsPort(port)) {
            throw ProjectException.Unprocessable($"An input port is an id of 1…{MaxPortLength} characters.");
        }

        if (input.Channel is { } channel && channel is < 1 or > 16) {
            throw ProjectException.Unprocessable($"A MIDI channel is 1…16, not {channel}.");
        }

        var max = input.Kind switch {
            ControlInputKind.ControlChange or ControlInputKind.Note or ControlInputKind.ProgramChange => 127,
            ControlInputKind.ControlChange14 => 31,
            ControlInputKind.Nrpn => 16_383,
            _ => 0
        };
        if (input.Number < 0 || input.Number > max) {
            throw ProjectException.Unprocessable($"A {input.Kind} input's number is 0…{max}, not {input.Number}.");
        }
    }

    /// <summary>A target's shape: the fields its kind needs (whether its track or parameter exists is checked live).</summary>
    public static void Check(ControlTarget? target) {
        if (target is null || !Enum.IsDefined(target.Kind)) {
            throw ProjectException.Unprocessable("An assignment's target has a known kind.");
        }

        var ok = target.Kind switch {
            ControlTargetKind.Parameter or ControlTargetKind.SmartControl => target.Address is {
                Length: > 0 and <= MaxPortLength
            },
            ControlTargetKind.TrackSwitch => target.Switch is { } s && Enum.IsDefined(s),
            ControlTargetKind.Transport => target.Transport is { } t && Enum.IsDefined(t),
            ControlTargetKind.KeyCommand => target.KeyCommandId is { Length: > 0 and <= MaxNameLength },
            _ => target.ZoneId is not null && target.ModeId is not null
        };
        if (!ok) {
            throw ProjectException.Unprocessable($"A {target.Kind} target needs its fields.");
        }

        if (target.TrackId is { Length: 0 or > MaxIdLength }) {
            throw ProjectException.Unprocessable("A target's track is a track id (or null: the selected track).");
        }
    }

    public static bool IsPort(string? port) =>
        port is { Length: > 0 and <= MaxPortLength } && !string.IsNullOrWhiteSpace(port);

    /// <summary>OSC settings checked: a port 1024…65535, clients with a host and port.</summary>
    public static OscSettings Check(OscSettings? settings) {
        if (settings is null || settings.Port is < 1024 or > 65_535) {
            throw ProjectException.Unprocessable("The OSC server's port is 1024…65535.");
        }

        if (settings.Clients is not { Count: <= MaxOscClients } clients) {
            throw ProjectException.Unprocessable($"Send the OSC clients (at most {MaxOscClients}).");
        }

        foreach (var client in clients) {
            if (client?.Host is not { Length: > 0 and <= MaxHostLength } host
                || string.IsNullOrWhiteSpace(host)
                || client.Port is < 1 or > 65_535) {
                throw ProjectException.Unprocessable("An OSC client is a host name or address and a port 1…65535.");
            }
        }

        return settings with { Clients = [..clients.Select(static c => c with { Host = c.Host.Trim() }).Distinct()] };
    }

    static void Id(string? id, HashSet<string> ids) {
        if (id is not { Length: > 0 and <= MaxIdLength } || string.IsNullOrWhiteSpace(id)) {
            throw ProjectException.Unprocessable($"An id has 1…{MaxIdLength} characters.");
        }

        if (!ids.Add(id)) {
            throw ProjectException.Unprocessable($"Id '{id}' is used twice.");
        }
    }

    static void Name(string? name) {
        if (name is not { Length: <= MaxNameLength }) {
            throw ProjectException.Unprocessable($"A name has at most {MaxNameLength} characters.");
        }
    }
}
