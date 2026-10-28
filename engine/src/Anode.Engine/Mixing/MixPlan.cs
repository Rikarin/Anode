namespace Anode.Engine.Mixing;

/// <summary>
///     Where an insert's sidechain comes from: another track's post-fader signal (<see cref="Channel" />; one block late
///     when that track is processed after the insert's), or a bus (<see cref="Bus" />; read from the previous block,
///     <see cref="Late" />, unless every channel feeding it is processed before the insert's).
/// </summary>
public readonly record struct SidechainSource(ChannelState? Channel, int Bus, bool Late) {
    public bool None => Channel is null && Bus <= 0;
}

/// <summary>An automated parameter of an insert or instrument: its index and the target driving it.</summary>
public readonly record struct ParameterBinding(int Parameter, int Target);

/// <summary>
///     One channel in the plan: its track (null for the master), its published configuration and what the plan
///     resolved for it (destinations, sidechains, automation targets).
/// </summary>
public sealed class MixNode {
    public required ChannelState Channel { get; init; }

    public required ChannelConfig Config { get; init; }

    /// <summary>Index into the model's tracks (−1: the master).</summary>
    public int TrackIndex { get; init; } = -1;

    public TrackState? Track { get; init; }

    public int TrackId { get; init; } = -1;

    /// <summary>An aux track's input bus (1…64), else 0.</summary>
    public int InputBus { get; init; }

    /// <summary>Plays its freeze file instead of its source and inserts.</summary>
    public bool Frozen { get; init; }

    /// <summary>The output's destination (<see cref="MixPlan.Destination" />; −1: nowhere, the master's own output).</summary>
    public int OutputDestination { get; init; } = -1;

    /// <summary>Each send's destination (−1: nowhere).</summary>
    public int[] SendDestinations { get; init; } = [];

    public SidechainSource[] Sidechains { get; init; } = [];

    /// <summary>Automated parameters of each insert.</summary>
    public ParameterBinding[][] InsertBindings { get; init; } = [];

    /// <summary>Audio thread: the value each insert's automated parameters were last set to.</summary>
    public float[][] InsertLast { get; init; } = [];

    /// <summary>
    ///     Each insert's <c>tempo</c> parameter (−1: none), which the engine sets from the project's tempo at the playhead
    ///     (tempo-synced delays, tremolo, auto filter).
    /// </summary>
    public int[] TempoParameters { get; init; } = [];

    public ParameterBinding[] InstrumentBindings { get; init; } = [];

    public float[] InstrumentLast { get; init; } = [];

    public int VolumeTarget { get; init; } = -1;

    public int PanTarget { get; init; } = -1;

    public int MuteTarget { get; init; } = -1;

    /// <summary>Each send's level target (−1: none).</summary>
    public int[] SendTargets { get; init; } = [];

    /// <summary>Other inserts take this channel's post-fader signal as a sidechain.</summary>
    public bool WritePost { get; init; }
}

/// <summary>
///     The mixer graph as the audio thread runs it (docs/05-phase-4.md, "Rules the contracts imply"): the channels in
///     processing order (every aux after everything feeding its input bus, so a bus is complete when its aux reads it),
///     then the master. Built by the control thread from the tracks' configurations and the model's automation targets,
///     immutable, and published with the model (RCU).
/// </summary>
public sealed class MixPlan {
    /// <summary>Summing points: 0 the master, 1…64 the buses, then the device's output pairs 1…32.</summary>
    public const int Buses = 64;

    public const int OutputPairs = 32;

    public const int Destinations = 1 + Buses + OutputPairs;

    public static readonly MixPlan Empty =
        new([], new() { Channel = new(new()), Config = ChannelConfig.Empty }, [0], []);

    MixPlan(MixNode[] order, MixNode master, int[] used, int[] snapshotBuses) {
        Order = order;
        Master = master;
        UsedDestinations = used;
        SnapshotBuses = snapshotBuses;
    }

    public MixNode[] Order { get; }

    public MixNode Master { get; }

    /// <summary>The summing points anything reaches (cleared every block).</summary>
    public int[] UsedDestinations { get; }

    /// <summary>Buses a sidechain reads from the previous block (copied at the end of each block).</summary>
    public int[] SnapshotBuses { get; }

    /// <summary>The summing point of channel output <paramref name="output" /> or send bus (−1: none).</summary>
    public static int Destination(int output) =>
        output switch {
            0 => 0,
            >= 1 and <= Buses => output,
            <= -1 and >= -OutputPairs => Buses - output,
            _ => -1
        };

    /// <summary>The plan for <paramref name="tracks" /> (in model order) and <paramref name="master" />.</summary>
    public static MixPlan Build(TrackState[] tracks, ChannelState master, AutomationSet automation) {
        var n = tracks.Length;
        var configs = new ChannelConfig[n];
        for (var i = 0; i < n; i++) {
            configs[i] = tracks[i].Frozen ? Frozen(tracks[i].Channel.Config) : tracks[i].Channel.Config;
        }

        // Levels: an aux sits one above everything feeding its input bus (a loop the server failed to refuse is cut).
        var levels = new int[n];
        for (var pass = 0; pass <= n; pass++) {
            var changed = false;
            for (var i = 0; i < n; i++) {
                if (tracks[i].InputBus <= 0) {
                    continue;
                }

                var level = 0;
                for (var j = 0; j < n; j++) {
                    if (j != i && Feeds(configs[j], tracks[i].InputBus)) {
                        level = Math.Max(level, levels[j] + 1);
                    }
                }

                level = Math.Min(level, n);
                if (level != levels[i]) {
                    levels[i] = level;
                    changed = true;
                }
            }

            if (!changed) {
                break;
            }
        }

        var sequence = Enumerable.Range(0, n).OrderBy(i => levels[i]).ThenBy(static i => i).ToArray();
        var position = new int[n];
        for (var k = 0; k < n; k++) {
            position[sequence[k]] = k;
        }

        var sidechainSources = new HashSet<int>();
        var snapshots = new SortedSet<int>();
        var used = new SortedSet<int> { 0 };
        var order = new MixNode[n];
        for (var k = 0; k < n; k++) {
            var i = sequence[k];
            order[k] = Node(i, k, tracks[i], configs[i]);
        }

        var masterNode = Node(-1, n, null, master.Config);
        foreach (var index in sidechainSources) {
            tracks[index].Channel.EnsurePost();
        }

        for (var k = 0; k < n; k++) {
            if (sidechainSources.Contains(sequence[k])) {
                order[k] = Copy(order[k], true);
            }
        }

        return new(order, masterNode, [..used], [..snapshots]);

        MixNode Node(int index, int at, TrackState? track, ChannelConfig config) {
            var trackId = track?.Id ?? -1;
            var sidechains = new SidechainSource[config.Inserts.Length];
            var bindings = new ParameterBinding[config.Inserts.Length][];
            for (var s = 0; s < config.Inserts.Length; s++) {
                var insert = config.Inserts[s];
                sidechains[s] = Sidechain(insert, at);
                var list = new List<ParameterBinding>();
                for (var t = 0; t < automation.Targets.Length; t++) {
                    if (automation.Targets[t] is { } target
                        && target.TrackId == trackId
                        && AutomationLane.KindOf(target.Kind) == AutomationKind.Insert
                        && target.Id == insert.Slot.Id
                        && automation.Lane(t) is not null
                        && target.ParameterId is { } parameter
                        && insert.Slot.IndexOf(parameter) is var p and >= 0) {
                        list.Add(new(p, t));
                    }
                }

                bindings[s] = [..list];
            }

            var instrument = new List<ParameterBinding>();
            if (track?.Instrument is { } slot) {
                for (var t = 0; t < automation.Targets.Length; t++) {
                    if (automation.Targets[t] is { } target
                        && target.TrackId == trackId
                        && AutomationLane.KindOf(target.Kind) == AutomationKind.Instrument
                        && automation.Lane(t) is not null
                        && target.ParameterId is { } parameter
                        && slot.IndexOf(parameter) is var p and >= 0) {
                        instrument.Add(new(p, t));
                    }
                }
            }

            var output = track is null ? -1 : Destination(config.Output);
            if (output >= 0) {
                used.Add(output);
            }

            var sends = new int[track is null ? 0 : config.Sends.Length]; // the master has no sends
            var sendTargets = new int[sends.Length];
            for (var s = 0; s < sends.Length; s++) {
                var bus = config.Sends[s].Bus;
                sends[s] = bus is >= 1 and <= Buses ? bus : -1;
                if (sends[s] >= 0) {
                    used.Add(sends[s]);
                }

                sendTargets[s] = Lane(AutomationKind.Send, config.Sends[s].Slot.Id);
            }

            return new() {
                Channel = track?.Channel ?? master,
                Config = config,
                TrackIndex = index,
                Track = track,
                TrackId = trackId,
                InputBus = track?.InputBus is >= 1 and <= Buses ? track.InputBus : 0,
                Frozen = track?.Frozen ?? false,
                OutputDestination = output,
                SendDestinations = sends,
                Sidechains = sidechains,
                InsertBindings = bindings,
                InsertLast = [..bindings.Select(static b => Enumerable.Repeat(float.NaN, b.Length).ToArray())],
                TempoParameters = [..config.Inserts.Select(static i => i.Slot.IndexOf("tempo"))],
                InstrumentBindings = [..instrument],
                InstrumentLast = [..Enumerable.Repeat(float.NaN, instrument.Count)],
                VolumeTarget = Lane(AutomationKind.Volume),
                PanTarget = Lane(AutomationKind.Pan),
                MuteTarget = Lane(AutomationKind.Mute),
                SendTargets = sendTargets
            };

            int Lane(AutomationKind kind, string? id = null) {
                var target = automation.Find(trackId, kind, id);
                return target >= 0 && automation.Lane(target) is not null ? target : -1;
            }
        }

        SidechainSource Sidechain(ChannelInsert insert, int at) {
            if (insert.SidechainTrack >= 0) {
                for (var j = 0; j < n; j++) {
                    if (tracks[j].Id == insert.SidechainTrack) {
                        sidechainSources.Add(j);
                        return new(tracks[j].Channel, 0, false);
                    }
                }

                return default;
            }

            if (insert.SidechainBus is < 1 or > Buses) {
                return default;
            }

            var late = false;
            for (var j = 0; j < n; j++) {
                late |= position[j] >= at && Feeds(configs[j], insert.SidechainBus);
            }

            if (late) {
                snapshots.Add(insert.SidechainBus);
            }

            return new(null, insert.SidechainBus, late);
        }
    }

    static MixNode Copy(MixNode node, bool writePost) =>
        new() {
            Channel = node.Channel,
            Config = node.Config,
            TrackIndex = node.TrackIndex,
            Track = node.Track,
            TrackId = node.TrackId,
            InputBus = node.InputBus,
            Frozen = node.Frozen,
            OutputDestination = node.OutputDestination,
            SendDestinations = node.SendDestinations,
            Sidechains = node.Sidechains,
            InsertBindings = node.InsertBindings,
            InsertLast = node.InsertLast,
            TempoParameters = node.TempoParameters,
            InstrumentBindings = node.InstrumentBindings,
            InstrumentLast = node.InstrumentLast,
            VolumeTarget = node.VolumeTarget,
            PanTarget = node.PanTarget,
            MuteTarget = node.MuteTarget,
            SendTargets = node.SendTargets,
            WritePost = writePost
        };

    /// <summary>A frozen track keeps its sends and output but none of its inserts.</summary>
    static ChannelConfig Frozen(ChannelConfig config) => config with { Inserts = [] };

    static bool Feeds(ChannelConfig config, int bus) {
        if (config.Output == bus) {
            return true;
        }

        foreach (var send in config.Sends) {
            if (send.Bus == bus) {
                return true;
            }
        }

        return false;
    }
}
