using Anode.Contracts;
using System.Collections.Immutable;
using System.Globalization;

namespace Anode.Model.Editing;

/// <summary>
///     The mixer (docs/05-phase-4.md): inserts, sends, outputs, aux and VCA tracks, mixer groups, automation lanes and
///     modes, mixer settings, patches and unfreezing. Routing loops are refused (<see cref="Routing" />).
/// </summary>
public static partial class Edits {
    public const int MaxInserts = 15;
    public const int MaxSends = 8;
    public const int MaxBus = 64;
    public const int MaxGroup = 32;
    public const int MaxOutputPair = 64;
    public const int MaxAutomationPoints = 100_000;
    public const double MaxLowLatencyLimitMs = 1000;

    /// <summary>The send level a new send has unless told otherwise: −∞, as Logic's.</summary>
    public const double NewSendLevelDb = Units.SilenceDb;

    /// <summary>What a new mixer group links: volume, mute, solo, pan, record arm and selection (not sends or automation).</summary>
    public static GroupProperties DefaultGroupProperties { get; } =
        new(true, true, true, true, false, true, false, true);

    public const string GroupColor = "#8a8f98";

    /// <summary>
    ///     The parameter tempo-synced effects (Anode.Dsp's Stereo Delay, Tape Delay, Tremolo, AutoFilter…) take the
    ///     project tempo through: the engine sets it, so inserts do not keep it, edits refuse it and it is no automation
    ///     target.
    /// </summary>
    public const string HostTempoParameter = "tempo";

    static List<DeltaOp> AddInsert(ProjectState state, AddInsertCommand c, IIdSource ids, PluginCatalog plugins) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        if (string.IsNullOrWhiteSpace(c.PluginId) || plugins.Find(c.PluginId) is not { } plugin) {
            throw new EditException($"No plugin '{c.PluginId}'.");
        }

        if (plugin.IsInstrument) {
            throw new EditException($"Plugin '{plugin.Id}' is an instrument, not an effect.");
        }

        if (IsMidiEffect(plugin)) {
            throw new EditException($"Plugin '{plugin.Id}' is a MIDI effect: it goes in a MIDI FX slot.");
        }

        if (strip.Inserts.Count >= MaxInserts) {
            throw new EditException($"A channel strip holds at most {MaxInserts} inserts.");
        }

        var index = c.Index ?? strip.Inserts.Count;
        if (index < 0 || index > strip.Inserts.Count) {
            throw new EditException($"Insert slot {index} is outside 0…{strip.Inserts.Count}.");
        }

        var insert = new Insert(
            ids.Next(IdPrefix.Insert),
            plugin.Id,
            plugin.Name,
            false,
            PluginCatalog.Defaults(plugin).Remove(HostTempoParameter),
            null
        );
        return [Upsert(state, track, strip with { Inserts = strip.Inserts.ToImmutableList().Insert(index, insert) })];
    }

    static List<DeltaOp> RemoveInsert(ProjectState state, RemoveInsertCommand c) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        var insert = InsertOf(strip, c.InsertId);
        var ops = new List<DeltaOp>();
        ops.AddRange(
            LanesFor(state, track?.Id, $"insert:{insert.Id}:").Select(static l => new AutomationLaneRemoved(l.Id))
        );
        ops.Add(Upsert(state, track, strip with { Inserts = [..strip.Inserts.Where(i => i.Id != insert.Id)] }));
        return ops;
    }

    static List<DeltaOp> MoveInsert(ProjectState state, MoveInsertCommand c) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        var insert = InsertOf(strip, c.InsertId);
        if (c.Index < 0 || c.Index >= strip.Inserts.Count) {
            throw new EditException($"Insert slot {c.Index} is outside 0…{strip.Inserts.Count - 1}.");
        }

        var inserts = strip.Inserts.Where(i => i.Id != insert.Id).ToImmutableList().Insert(c.Index, insert);
        return [Upsert(state, track, strip with { Inserts = inserts })];
    }

    static List<DeltaOp> SetInsert(ProjectState state, SetInsertCommand c, PluginCatalog plugins) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        var insert = InsertOf(strip, c.InsertId);
        var sidechain = insert.Sidechain;
        if (c.Sidechain is { } source) {
            sidechain = source.Length == 0 ? null : CheckSidechain(state, track, source);
            if (sidechain is not null && plugins.Find(insert.PluginId) is { HasSidechain: false }) {
                throw new EditException($"Plugin '{insert.PluginId}' takes no sidechain.");
            }
        }

        var changed = insert with { Bypassed = c.Bypassed ?? insert.Bypassed, Sidechain = sidechain };
        return [
            Upsert(
                state,
                track,
                strip with { Inserts = [..strip.Inserts.Select(i => i.Id == insert.Id ? changed : i)] }
            )
        ];
    }

    /// <summary>Loads a file into an insert (an impulse response): one of its plugin's file types.</summary>
    static List<DeltaOp> LoadInsertFile(ProjectState state, LoadInsertFileCommand c, PluginCatalog plugins) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        var insert = InsertOf(strip, c.InsertId);
        var path = c.Path?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaxPathLength) {
            throw new EditException($"A file path has 1…{MaxPathLength} characters.");
        }

        if (plugins.Find(insert.PluginId) is { } plugin) {
            var extension = Path.GetExtension(path);
            if (!plugin.FileTypes.Any(t => string.Equals(t, extension, StringComparison.OrdinalIgnoreCase))) {
                throw new EditException(
                    plugin.FileTypes.Count == 0
                        ? $"Plugin '{plugin.Id}' loads no files."
                        : $"Plugin '{plugin.Id}' loads {string.Join(", ", plugin.FileTypes)} files, not '{extension}'."
                );
            }
        }

        var changed = insert with { File = path };
        return [
            Upsert(
                state,
                track,
                strip with { Inserts = [..strip.Inserts.Select(i => i.Id == insert.Id ? changed : i)] }
            )
        ];
    }

    static List<DeltaOp> SetInsertParameter(ProjectState state, SetInsertParameterCommand c, PluginCatalog plugins) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        var insert = InsertOf(strip, c.InsertId);
        if (c.ParameterId == HostTempoParameter) {
            throw new EditException("The engine sets the tempo parameter from the project tempo.");
        }

        var value = ParameterValue(
            plugins,
            insert.PluginId,
            insert.Parameters,
            c.ParameterId,
            c.Value,
            "Insert",
            insert.RuntimeParameters
        );
        var changed = insert with {
            Parameters = PluginCatalog.Sorted(insert.Parameters).SetItem(c.ParameterId, value)
        };
        return [
            Upsert(
                state,
                track,
                strip with { Inserts = [..strip.Inserts.Select(i => i.Id == insert.Id ? changed : i)] }
            )
        ];
    }

    /// <summary>A parameter value checked against the plugin's catalogue entry (rounded to its steps), or kept.</summary>
    /// <summary>
    ///     A parameter of a slot: of its run-time list when its plugin defines its parameters at run time (Scripter,
    ///     Phase 7), else of its plugin's catalogue entry; null when neither has it.
    /// </summary>
    static ParameterInfo? InfoOf(
        PluginCatalog plugins,
        string pluginId,
        IReadOnlyList<ParameterInfo>? runtime,
        string parameterId
    ) =>
        runtime is not null
            ? runtime.FirstOrDefault(p => p.Id == parameterId)
            : plugins.Find(pluginId)?.Parameters.FirstOrDefault(p => p.Id == parameterId);

    static double ParameterValue(
        PluginCatalog plugins,
        string pluginId,
        IReadOnlyDictionary<string, double> known,
        string? parameterId,
        double value,
        string what,
        IReadOnlyList<ParameterInfo>? runtime = null
    ) {
        if (string.IsNullOrEmpty(parameterId) || parameterId.Length > MaxParameterIdLength) {
            throw new EditException("Name a parameter.");
        }

        if (!double.IsFinite(value)) {
            throw new EditException("A parameter value is a finite number.");
        }

        var info = InfoOf(plugins, pluginId, runtime, parameterId);
        if (info is not null) {
            if (value < info.Min || value > info.Max) {
                throw new EditException($"Parameter '{info.Id}' takes {info.Min}…{info.Max}, not {value}.");
            }

            if (info.Steps > 0 && info.Max > info.Min) {
                var step = (info.Max - info.Min) / info.Steps;
                value = Math.Clamp(info.Min + Math.Round((value - info.Min) / step) * step, info.Min, info.Max);
            }
        } else if (!known.ContainsKey(parameterId)) {
            throw new EditException($"{what} '{pluginId}' has no parameter '{parameterId}'.");
        }

        return value;
    }

    static List<DeltaOp> AddSend(ProjectState state, AddSendCommand c, IIdSource ids) {
        var track = MixTrackOf(state, c.TrackId, "sends");
        CheckBus(c.Bus);
        if (track.Strip.Sends.Count >= MaxSends) {
            throw new EditException($"A channel strip has at most {MaxSends} sends.");
        }

        if (track.Strip.Sends.Any(s => s.Bus == c.Bus)) {
            throw new EditException($"Track '{track.Name}' already sends to bus {c.Bus}.");
        }

        var level = c.LevelDb ?? NewSendLevelDb;
        CheckLevel(level, "send level");
        var send = new Send(ids.Next(IdPrefix.Send), c.Bus, level, 0, c.PreFader ?? false);
        var changed = track with { Strip = track.Strip with { Sends = [..track.Strip.Sends, send] } };
        CheckRouting(state, changed);
        return [new TrackUpserted(changed, state.IndexOfTrack(track.Id))];
    }

    static List<DeltaOp> SetSend(ProjectState state, SetSendCommand c) {
        var track = MixTrackOf(state, c.TrackId, "sends");
        var send = SendOf(track, c.SendId);
        if (c.LevelDb is { } level) {
            CheckLevel(level, "send level");
        }

        CheckPan(c.Pan);
        if (c.Bus is { } bus) {
            CheckBus(bus);
            if (bus != send.Bus && track.Strip.Sends.Any(s => s.Bus == bus)) {
                throw new EditException($"Track '{track.Name}' already sends to bus {bus}.");
            }
        }

        var changed = send with {
            LevelDb = c.LevelDb ?? send.LevelDb,
            Pan = c.Pan ?? send.Pan,
            PreFader = c.PreFader ?? send.PreFader,
            Bus = c.Bus ?? send.Bus
        };
        var updated = WithSend(track, changed);
        CheckRouting(state, updated);
        var ops = new List<DeltaOp> { new TrackUpserted(updated, state.IndexOfTrack(track.Id)) };
        // Group members that link sends follow on the same bus: level and pan keep their offsets.
        foreach (var member in Linked(state, track, static p => p.Sends)) {
            if (member.Strip.Sends.FirstOrDefault(s => s.Bus == send.Bus) is not { } theirs) {
                continue;
            }

            var follows = theirs with {
                LevelDb = c.LevelDb is { } l ? Follow(theirs.LevelDb, l - send.LevelDb) : theirs.LevelDb,
                Pan = c.Pan is { } p ? Math.Clamp(theirs.Pan + (p - send.Pan), -1, 1) : theirs.Pan,
                PreFader = c.PreFader ?? theirs.PreFader
            };
            ops.Add(new TrackUpserted(WithSend(member, follows), state.IndexOfTrack(member.Id)));
        }

        return ops;
    }

    static Track WithSend(Track track, Send send) =>
        track with {
            Strip = track.Strip with { Sends = [..track.Strip.Sends.Select(s => s.Id == send.Id ? send : s)] }
        };

    static List<DeltaOp> RemoveSend(ProjectState state, RemoveSendCommand c) {
        var track = MixTrackOf(state, c.TrackId, "sends");
        var send = SendOf(track, c.SendId);
        var ops = new List<DeltaOp>();
        ops.AddRange(LanesFor(state, track.Id, $"send:{send.Id}").Select(static l => new AutomationLaneRemoved(l.Id)));
        ops.Add(
            new TrackUpserted(
                track with { Strip = track.Strip with { Sends = [..track.Strip.Sends.Where(s => s.Id != send.Id)] } },
                state.IndexOfTrack(track.Id)
            )
        );
        return ops;
    }

    static List<DeltaOp> SetOutput(ProjectState state, SetOutputCommand c) {
        var track = MixTrackOf(state, c.TrackId, "an output");
        var output = Routing.Normalise(c.Output)
            ?? throw new EditException(
                $"An output is 'master', 'bus:1'…'bus:{MaxBus}' or 'output:1'…'output:{MaxOutputPair}', not '{c.Output}'."
            );
        var changed = track with { Strip = track.Strip with { Output = output } };
        CheckRouting(state, changed);
        return [new TrackUpserted(changed, state.IndexOfTrack(track.Id))];
    }

    static List<DeltaOp> AddAux(ProjectState state, AddAuxCommand c, IIdSource ids) {
        CheckTrackCount(state);
        var bus = c.Bus
            ?? Routing.LowestFreeBus(state)
            ?? throw new EditException($"Every bus 1…{MaxBus} is in use.");
        CheckBus(bus);
        if (state.Tracks.Exists(t => t.Kind == TrackKind.Aux && t.InputBus == bus)) {
            throw new EditException($"Bus {bus} already feeds an aux.");
        }

        var aux = NewTrack(state, ids, TrackKind.Aux, c.Name is null ? $"Aux {bus}" : Name(c.Name, "track name")) with {
            InputBus = bus
        };
        return [new TrackUpserted(aux, state.Tracks.Count)];
    }

    static List<DeltaOp> AddVca(ProjectState state, AddVcaCommand c, IIdSource ids) {
        CheckTrackCount(state);
        var name = c.Name is null ? NextTrackName(state, TrackKind.Vca) : Name(c.Name, "track name");
        return [new TrackUpserted(NewTrack(state, ids, TrackKind.Vca, name), state.Tracks.Count)];
    }

    /// <summary>A new track of <paramref name="kind" /> with the default strip, reading automation, taking no input.</summary>
    static Track NewTrack(ProjectState state, IIdSource ids, TrackKind kind, string name) =>
        new(
            ids.Next(IdPrefix.Track),
            name,
            kind,
            ProjectDefaults.Colors[state.Tracks.Count % ProjectDefaults.Colors.Count],
            new(0, false),
            false,
            false,
            ProjectDefaults.Strip,
            null,
            ProjectDefaults.MidiInput,
            null,
            null,
            false,
            string.Empty,
            0,
            AutomationMode.Read,
            null,
            false,
            FlexMode.Polyphonic,
            [],
            null,
            null,
            null,
            false,
            false,
            null
        );

    static List<DeltaOp> AssignVca(ProjectState state, AssignVcaCommand c) {
        var track = TrackOf(state, c.TrackId);
        if (track.Kind is TrackKind.Vca or TrackKind.Stack or TrackKind.Midi) {
            throw new EditException($"A {Kind(track.Kind)} track does not join a VCA.");
        }

        if (c.VcaId is { } id && TrackOf(state, id).Kind != TrackKind.Vca) {
            throw new EditException($"Track '{id}' is not a VCA.");
        }

        return [
            new TrackUpserted(track with { Strip = track.Strip with { VcaId = c.VcaId } }, state.IndexOfTrack(track.Id))
        ];
    }

    static List<DeltaOp> SetGroup(ProjectState state, SetGroupCommand c) {
        CheckGroup(c.Number);
        var old = state.Groups.GetValueOrDefault(c.Number)
            ?? new MixerGroup(c.Number, $"Group {c.Number}", GroupColor, true, DefaultGroupProperties);
        var group = old with {
            Name = c.Name is null ? old.Name : Name(c.Name, "group name"),
            Color = c.Color is null ? old.Color : Color(c.Color),
            Enabled = c.Enabled ?? old.Enabled,
            Properties = c.Properties ?? old.Properties
        };
        return [new GroupUpserted(group)];
    }

    static List<DeltaOp> DeleteGroup(ProjectState state, DeleteGroupCommand c) {
        if (!state.Groups.ContainsKey(c.Number)) {
            throw new EditException($"No mixer group {c.Number}.");
        }

        var ops = new List<DeltaOp>();
        foreach (var track in state.Tracks.Where(t => t.Strip.Groups.Contains(c.Number))) {
            ops.Add(
                new TrackUpserted(
                    track with {
                        Strip = track.Strip with { Groups = [..track.Strip.Groups.Where(g => g != c.Number)] }
                    },
                    state.IndexOfTrack(track.Id)
                )
            );
        }

        ops.Add(new GroupRemoved(c.Number));
        return ops;
    }

    static List<DeltaOp> SetTrackGroups(ProjectState state, SetTrackGroupsCommand c) {
        var track = TrackOf(state, c.TrackId);
        if (track.Kind == TrackKind.Stack) {
            throw new EditException("A stack joins no mixer group: its members do.");
        }

        if (c.Groups is null) {
            throw new EditException("Name the groups (an empty list leaves every group).");
        }

        foreach (var number in c.Groups) {
            CheckGroup(number);
            if (!state.Groups.ContainsKey(number)) {
                throw new EditException($"No mixer group {number}: create it with setGroup first.");
            }
        }

        return [
            new TrackUpserted(
                track with { Strip = track.Strip with { Groups = [..c.Groups.Distinct().Order()] } },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    static List<DeltaOp> SetAutomation(
        ProjectState state,
        SetAutomationCommand c,
        IIdSource ids,
        PluginCatalog plugins
    ) {
        var track = c.TrackId is null ? null : TrackOf(state, c.TrackId);
        var target = c.Target ?? string.Empty;
        var range = RangeOf(state, track, target, plugins);
        var existing = state.LaneOf(track?.Id, target);
        if (c.Points is not { Count: <= MaxAutomationPoints } points) {
            throw new EditException($"A lane has at most {MaxAutomationPoints} points.");
        }

        if (points.Count == 0) {
            return existing is null ? [] : [new AutomationLaneRemoved(existing.Id)];
        }

        CheckPoints(points, range);
        return [
            new AutomationLaneUpserted(
                new(existing?.Id ?? ids.Next(IdPrefix.Automation), track?.Id, target, [..points])
            )
        ];
    }

    /// <summary>Points in tick order (two at most on one tick: a jump), inside the target's range, curves −1…1.</summary>
    static void CheckPoints(IReadOnlyList<AutomationPoint> points, LaneRange range) {
        for (var i = 0; i < points.Count; i++) {
            var p = CheckPoint(points[i], range);
            if (i > 0 && p.Tick < points[i - 1].Tick) {
                throw new EditException("Automation points are in tick order.");
            }

            if (i > 1 && p.Tick == points[i - 2].Tick) {
                throw new EditException($"At most two automation points share tick {p.Tick}.");
            }
        }
    }

    static AutomationPoint CheckPoint(AutomationPoint? point, LaneRange range) {
        var p = point ?? throw new EditException("An automation point is null.");
        CheckTick(p.Tick, "automation point");
        if (!double.IsFinite(p.Value) || p.Value < range.Min || p.Value > range.Max) {
            throw new EditException($"An automation value of this lane is {range.Min}…{range.Max}, not {p.Value}.");
        }

        if (range.Toggle && p.Value is not (0 or 1)) {
            throw new EditException("A mute lane's values are 0 and 1.");
        }

        return p.Curve is >= -1 and <= 1 ? p : throw new EditException($"An automation curve is −1…1, not {p.Curve}.");
    }

    /// <summary>What values a lane's target takes; whether it toggles (mute).</summary>
    public readonly record struct LaneRange(double Min, double Max, bool Toggle);

    /// <summary>The range of <paramref name="target" /> on a track's strip (null: the master), or EditException.</summary>
    public static LaneRange TargetRangeOf(
        ProjectState state,
        string? trackId,
        string target,
        PluginCatalog plugins
    ) =>
        RangeOf(state, trackId is null ? null : TrackOf(state, trackId), target, plugins);

    /// <summary>Whether a strip (null: the master) has the control <paramref name="target" />.</summary>
    public static bool TargetExists(ProjectState state, string? trackId, string target) {
        try {
            _ = TargetRangeOf(state, trackId, target, PluginCatalog.Empty);
            return true;
        } catch (EditException) {
            return false;
        }
    }

    static LaneRange RangeOf(ProjectState state, Track? track, string target, PluginCatalog plugins) {
        var kind = track?.Kind;
        if (kind is TrackKind.Stack or TrackKind.Midi) {
            throw new EditException($"A {Kind(kind.Value)} track has no automation.");
        }

        var vca = kind == TrackKind.Vca;
        var strip = track?.Strip ?? state.Master;
        switch (target) {
            case "volume":
                return new(Units.SilenceDb, MaxVolumeDb, false);
            case "mute":
                return new(0, 1, true);
            case "pan" when !vca:
                return new(-1, 1, false);
        }

        var parts = target.Split(':');
        switch (parts) {
            case ["send", var id] when track is not null && !vca:
                _ = track.Strip.Sends.FirstOrDefault(s => s.Id == id)
                    ?? throw new EditException($"Track '{track.Id}' has no send '{id}'.");
                return new(Units.SilenceDb, MaxVolumeDb, false);
            case ["insert", var id, var parameter] when !vca:
                var insert = strip.Inserts.FirstOrDefault(i => i.Id == id)
                    ?? throw new EditException($"No insert '{id}' on that strip.");
                if (parameter == HostTempoParameter) {
                    throw new EditException("The engine sets the tempo parameter from the project tempo.");
                }

                return ParameterRange(plugins, insert.PluginId, insert.Parameters, parameter, insert.RuntimeParameters);
            case ["instrument", var parameter] when track?.Instrument is { } slot:
                return ParameterRange(plugins, slot.PluginId, slot.Parameters, parameter);
            case ["midiEffect", var id, var parameter] when track is not null:
                var midiEffect = strip.MidiEffects.FirstOrDefault(i => i.Id == id)
                    ?? throw new EditException($"No MIDI FX slot '{id}' on that strip.");
                return ParameterRange(
                    plugins,
                    midiEffect.PluginId,
                    midiEffect.Parameters,
                    parameter,
                    midiEffect.RuntimeParameters
                );
            case ["smartControl", var id] when track is not null
                && HasAudioStrip(track.Kind)
                && ControlIds(SmartControlsOf(track).Layout).Contains(id):
                return new(0, 1, false);
            default:
                throw new EditException($"'{target}' is not an automation target of that strip.");
        }
    }

    static LaneRange ParameterRange(
        PluginCatalog plugins,
        string pluginId,
        IReadOnlyDictionary<string, double> known,
        string parameter,
        IReadOnlyList<ParameterInfo>? runtime = null
    ) {
        if (InfoOf(plugins, pluginId, runtime, parameter) is { } info) {
            return new(info.Min, info.Max, false);
        }

        return known.ContainsKey(parameter)
            ? new(double.MinValue, double.MaxValue, false)
            : throw new EditException($"Plugin '{pluginId}' has no parameter '{parameter}'.");
    }

    static List<DeltaOp> SetAutomationMode(ProjectState state, SetAutomationModeCommand c) {
        if (!Enum.IsDefined(c.Mode)) {
            throw new EditException($"Unknown automation mode {c.Mode}.");
        }

        // Without a track: every track (the master's lanes always read).
        IEnumerable<Track> tracks = c.TrackId is null
            ? state.Tracks
            : [TrackOf(state, c.TrackId), ..Linked(state, TrackOf(state, c.TrackId), static p => p.Automation)];
        return [
            ..tracks.Where(t => t.AutomationMode != c.Mode)
                .Select(t => new TrackUpserted(t with { AutomationMode = c.Mode }, state.IndexOfTrack(t.Id)))
        ];
    }

    static List<DeltaOp> SetMixerSettings(ProjectState state, SetMixerSettingsCommand c) {
        var old = state.Settings.Mixer;
        if (c.Pdc is { } pdc && !Enum.IsDefined(pdc)) {
            throw new EditException($"Unknown delay compensation mode {pdc}.");
        }

        if (c.LowLatencyLimitMs is { } limit && !(limit is >= 0 and <= MaxLowLatencyLimitMs)) {
            throw new EditException($"The low-latency limit is 0…{MaxLowLatencyLimitMs} ms, not {limit}.");
        }

        var mixer = new MixerSettings(
            c.Pdc ?? old.Pdc,
            c.LowLatency ?? old.LowLatency,
            c.LowLatencyLimitMs ?? old.LowLatencyLimitMs
        );
        return [new SettingsChanged(state.Settings with { Mixer = mixer })];
    }

    static List<DeltaOp> ApplyPatch(ProjectState state, ApplyPatchCommand c, IIdSource ids, IPatchSource patches) {
        var track = MixTrackOf(state, c.TrackId, "patches");
        var patch = patches.Find(c.PatchId ?? string.Empty) ?? throw new EditException($"No patch '{c.PatchId}'.");
        if (patch.Kind == TrackKind.Instrument && track.Kind != TrackKind.Instrument) {
            throw new EditException(
                $"Patch '{patch.Name}' is an instrument patch; '{track.Name}' is not an instrument track."
            );
        }

        if (track.FrozenFileId is not null) {
            throw new EditException($"Track '{track.Name}' is frozen: unfreeze it first.");
        }

        var ops = new List<DeltaOp>();
        var instrument = track.Instrument;
        if (track.Kind == TrackKind.Instrument && patch.Instrument is { } slot) {
            if (instrument?.PluginId != slot.PluginId) {
                ops.AddRange(
                    LanesFor(state, track.Id, "instrument:").Select(static l => new AutomationLaneRemoved(l.Id))
                );
            }

            instrument = slot with { Parameters = PluginCatalog.Sorted(slot.Parameters) };
        }

        foreach (var lane in state.LanesOf(track.Id)
                     .Where(static l => l.Target.StartsWith("insert:", StringComparison.Ordinal)
                         || l.Target.StartsWith("send:", StringComparison.Ordinal)
                     )) {
            ops.Add(new AutomationLaneRemoved(lane.Id));
        }

        var inserts = patch.Inserts.Take(MaxInserts)
            .Select(i => i with {
                    Id = ids.Next(IdPrefix.Insert),
                    Parameters = PluginCatalog.Sorted(i.Parameters).Remove(HostTempoParameter),
                    Sidechain = i.Sidechain is { } s && IsSidechain(state, track, s) ? s : null
                }
            )
            .ToArray();
        var sends = new List<Send>();
        foreach (var send in patch.Sends.Take(MaxSends)) {
            CheckBus(send.Bus);
            if (sends.TrueForAll(s => s.Bus != send.Bus)) {
                sends.Add(send with { Id = ids.Next(IdPrefix.Send) });
            }
        }

        var changed = track with {
            Instrument = instrument, Strip = track.Strip with { Inserts = inserts, Sends = sends }
        };
        CheckRouting(state, changed);
        ops.Add(new TrackUpserted(changed, state.IndexOfTrack(track.Id)));
        return ops;
    }

    static List<DeltaOp> UnfreezeTrack(ProjectState state, UnfreezeTrackCommand c) {
        var track = TrackOf(state, c.TrackId);
        return track.FrozenFileId is null
            ? throw new EditException($"Track '{track.Name}' is not frozen.")
            : [new TrackUpserted(track with { FrozenFileId = null }, state.IndexOfTrack(track.Id))];
    }

    /// <summary>
    ///     Freezes a track (docs/05-phase-4.md, "Freeze"): it plays <paramref name="fileId" />, a file of the project
    ///     rendered from its source and inserts. Not a command: the server applies it when the freeze job finishes.
    /// </summary>
    public static EditResult Freeze(ProjectState state, string trackId, string fileId) {
        var track = TrackOf(state, trackId);
        if (track.Kind is not (TrackKind.Audio or TrackKind.Instrument)) {
            throw new EditException("Only audio and instrument tracks freeze.");
        }

        _ = FileOf(state, fileId);
        var forward = new List<DeltaOp>();
        var inverse = new List<DeltaOp>();
        state = Step(
            state,
            [new TrackUpserted(track with { FrozenFileId = fileId }, state.IndexOfTrack(track.Id))],
            forward,
            inverse
        );
        inverse.Reverse();
        return new(state, forward, inverse);
    }

    /// <summary>A strip that takes inserts (a track's, or the master's when <paramref name="trackId" /> is null).</summary>
    static (Track? Track, ChannelStrip Strip) InsertStripOf(ProjectState state, string? trackId) {
        if (trackId is null) {
            return (null, state.Master);
        }

        var track = MixTrackOf(state, trackId, "inserts");
        return track.FrozenFileId is not null
            ? throw new EditException($"Track '{track.Name}' is frozen: unfreeze it to change its plugins.")
            : (track, track.Strip);
    }

    /// <summary>An audio, instrument or aux track: one with a full channel strip.</summary>
    static Track MixTrackOf(ProjectState state, string? id, string what) {
        var track = TrackOf(state, id ?? string.Empty);
        return HasAudioStrip(track.Kind)
            ? track
            : throw new EditException($"A {Kind(track.Kind)} track has no {what}.");
    }

    /// <summary>Whether a track of <paramref name="kind" /> has a full channel strip (inserts, sends, an output).</summary>
    public static bool HasAudioStrip(TrackKind kind) =>
        kind is TrackKind.Audio or TrackKind.Instrument or TrackKind.Aux;

    static string Kind(TrackKind kind) =>
        kind switch {
            TrackKind.Midi => "external MIDI",
            TrackKind.Vca => "VCA",
            _ => kind.ToString().ToLowerInvariant()
        };

    static DeltaOp Upsert(ProjectState state, Track? track, ChannelStrip strip) =>
        track is null
            ? new MasterChanged(strip)
            : new TrackUpserted(track with { Strip = strip }, state.IndexOfTrack(track.Id));

    static Insert InsertOf(ChannelStrip strip, string? id) =>
        strip.Inserts.FirstOrDefault(i => i.Id == id) ?? throw new EditException($"No insert '{id}' on that strip.");

    static Send SendOf(Track track, string? id) =>
        track.Strip.Sends.FirstOrDefault(s => s.Id == id)
        ?? throw new EditException($"Track '{track.Id}' has no send '{id}'.");

    /// <summary>The lanes of a strip whose target is <paramref name="target" /> or starts with it (a prefix ending ':').</summary>
    static AutomationLane[] LanesFor(ProjectState state, string? trackId, string target) => [
        ..state.LanesOf(trackId)
            .Where(l => target.EndsWith(':')
                    ? l.Target.StartsWith(target, StringComparison.Ordinal)
                    : l.Target == target
            )
    ];

    /// <summary>A sidechain source as kept: a track id, or a bus in canonical form.</summary>
    static string CheckSidechain(ProjectState state, Track? track, string source) {
        if (!IsSidechain(state, track, source)) {
            throw new EditException(
                $"A sidechain is another track with audio or 'bus:1'…'bus:{MaxBus}', not '{source}'."
            );
        }

        return Routing.BusOf(source) is { } bus ? $"bus:{bus}" : source;
    }

    static bool IsSidechain(ProjectState state, Track? track, string source) =>
        Routing.BusOf(source) is >= 1 and <= MaxBus
        || (source != track?.Id && state.FindTrack(source) is { } other && HasAudioStrip(other.Kind));

    static void CheckBus(int bus) {
        if (bus is < 1 or > MaxBus) {
            throw new EditException($"A bus is 1…{MaxBus}, not {bus}.");
        }
    }

    static void CheckGroup(int number) {
        if (number is < 1 or > MaxGroup) {
            throw new EditException($"A mixer group is 1…{MaxGroup}, not {number}.");
        }
    }

    static void CheckLevel(double level, string what) {
        if (!(level >= Units.SilenceDb && level <= MaxVolumeDb)) {
            throw new EditException($"The {what} {level} dB is outside {Units.SilenceDb}…{MaxVolumeDb} dB.");
        }
    }

    static void CheckPan(double? pan) {
        if (pan is { } p && !(p is >= -1 and <= 1)) {
            throw new EditException($"Pan {p} is outside −1…1.");
        }
    }

    static void CheckTrackCount(ProjectState state) {
        if (state.Tracks.Count >= MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }
    }

    /// <summary>Refuses <paramref name="changed" /> when, in place of its old self, it would close a routing loop.</summary>
    static void CheckRouting(ProjectState state, Track changed) {
        var after = state with {
            Tracks = state.Tracks.Select(t => t.Id == changed.Id ? changed : t).ToImmutableList()
        };
        if (Routing.Loop(after) is { } loop) {
            throw new EditException($"That would make a routing loop: {loop}.");
        }
    }

    /// <summary>
    ///     The other members of <paramref name="track" />'s enabled groups that link a property (one hop: a member's
    ///     own other groups do not follow).
    /// </summary>
    static Track[] Linked(ProjectState state, Track track, Func<GroupProperties, bool> links) {
        var groups = track.Strip.Groups
            .Where(g => state.Groups.TryGetValue(g, out var group) && group.Enabled && links(group.Properties))
            .ToHashSet();
        return groups.Count == 0
            ? []
            : state.Tracks.Where(t => t.Id != track.Id && t.Strip.Groups.Any(groups.Contains)).ToArray();
    }

    /// <summary>A level moved by <paramref name="delta" /> dB, within −∞…+6 dB.</summary>
    static double Follow(double level, double delta) => Math.Clamp(level + delta, Units.SilenceDb, MaxVolumeDb);
}

/// <summary>
///     Outputs and buses (docs/05-phase-4.md, "Graph"): a bus sums every send and output routed to it, an aux track
///     plays its input bus. A loop is a bus that feeds, directly or through other buses, an aux whose sends or output
///     reach that bus again.
/// </summary>
public static class Routing {
    public const string Master = "master";

    /// <summary>An output in canonical form (<c>master</c>, <c>bus:N</c>, <c>output:N</c>), or null when it is none.</summary>
    public static string? Normalise(string? output) {
        if (output == Master) {
            return Master;
        }

        if (BusOf(output) is { } bus) {
            return bus is >= 1 and <= Edits.MaxBus ? $"bus:{bus}" : null;
        }

        return PairOf(output) is { } pair && pair is >= 1 and <= Edits.MaxOutputPair ? $"output:{pair}" : null;
    }

    /// <summary>N of <c>bus:N</c>, or null.</summary>
    public static int? BusOf(string? output) => Number(output, "bus:");

    /// <summary>N of <c>output:N</c>, or null.</summary>
    public static int? PairOf(string? output) => Number(output, "output:");

    static int? Number(string? text, string prefix) =>
        text is not null
        && text.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>The buses a track's strip feeds: its sends' and its output's.</summary>
    public static IEnumerable<int> Feeds(Track track) {
        foreach (var send in track.Strip.Sends) {
            yield return send.Bus;
        }

        if (BusOf(track.Strip.Output) is { } bus) {
            yield return bus;
        }
    }

    /// <summary>A loop in <paramref name="state" />'s routing ("bus 1 → Aux 1 → bus 1"), or null.</summary>
    public static string? Loop(ProjectState state) {
        var search = new LoopSearch(state);
        foreach (var start in search.Buses) {
            if (search.Visit(start) is { } loop) {
                return loop;
            }
        }

        return null;
    }

    /// <summary>A depth-first search of the bus graph: bus → the auxes it feeds → the buses they feed.</summary>
    sealed class LoopSearch(ProjectState state) {
        readonly ILookup<int, Track> auxes =
            state.Tracks.Where(static t => t.Kind == TrackKind.Aux && t.InputBus > 0).ToLookup(static t => t.InputBus);

        readonly HashSet<int> visiting = [];
        readonly HashSet<int> done = [];
        readonly List<string> path = [];

        public IEnumerable<int> Buses => auxes.Select(static g => g.Key).Order();

        public string? Visit(int bus) {
            if (done.Contains(bus)) {
                return null;
            }

            path.Add($"bus {bus}");
            if (!visiting.Add(bus)) {
                return string.Join(" → ", path);
            }

            foreach (var aux in auxes[bus]) {
                if (Through(aux) is { } loop) {
                    return loop;
                }
            }

            visiting.Remove(bus);
            done.Add(bus);
            path.RemoveAt(path.Count - 1);
            return null;
        }

        string? Through(Track aux) {
            path.Add(aux.Name);
            foreach (var next in Feeds(aux).Distinct()) {
                if (Visit(next) is { } loop) {
                    return loop;
                }
            }

            path.RemoveAt(path.Count - 1);
            return null;
        }
    }

    /// <summary>The lowest bus no aux listens to and no strip feeds, or null when all are taken.</summary>
    public static int? LowestFreeBus(ProjectState state) {
        var used = state.Tracks.Where(static t => t.Kind == TrackKind.Aux).Select(static t => t.InputBus).ToHashSet();
        used.UnionWith(state.Tracks.SelectMany(Feeds));
        return Enumerable.Range(1, Edits.MaxBus).Cast<int?>().FirstOrDefault(b => !used.Contains(b!.Value));
    }
}

/// <summary>
///     A saved channel strip (Logic's patch; docs/05-phase-4.md, "Patches"): its instrument (an instrument patch), its
///     inserts with their parameters and its sends. Plugin states travel beside it, kept by the server.
/// </summary>
public sealed record ChannelPatch(
    string Id,
    string Name,
    string Category,
    TrackKind Kind,
    InstrumentSlot? Instrument,
    IReadOnlyList<Insert> Inserts,
    IReadOnlyList<Send> Sends
) {
    /// <summary>A drum kit's pads (Phase 6: a kit is a patch of kind stack that <c>createDrumMachine</c> takes).</summary>
    public IReadOnlyList<KitPad>? Pads { get; init; }
}

/// <summary>Where <c>applyPatch</c> finds patches (the server's library).</summary>
public interface IPatchSource {
    ChannelPatch? Find(string id);
}

/// <summary>No patches.</summary>
public sealed class NoPatches : IPatchSource {
    public static NoPatches Instance { get; } = new();

    public ChannelPatch? Find(string id) => null;
}
