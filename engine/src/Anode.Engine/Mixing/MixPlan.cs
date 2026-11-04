using Anode.Dsp.Spatial;
using Anode.Engine.Spatial;
using Anode.Ipc;

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

    /// <summary>Automated parameters of each MIDI FX slot (Phase 6), set at the start of every block.</summary>
    public ParameterBinding[][] MidiEffectBindings { get; init; } = [];

    /// <summary>Audio thread: the value each MIDI FX slot's automated parameters were last set to.</summary>
    public float[][] MidiEffectLast { get; init; } = [];

    public ParameterBinding[] InstrumentBindings { get; init; } = [];

    public float[] InstrumentLast { get; init; } = [];

    public int VolumeTarget { get; init; } = -1;

    public int PanTarget { get; init; } = -1;

    public int MuteTarget { get; init; } = -1;

    /// <summary>Each send's level target (−1: none).</summary>
    public int[] SendTargets { get; init; } = [];

    /// <summary>
    ///     A track with a sandboxed insert (<see cref="Anode.Plugins.ISplitProcessing" />): its first sandboxed block is
    ///     handed over before the mix runs, so sandboxes overlap.
    /// </summary>
    public bool Prefetch { get; init; }

    /// <summary>Other inserts take this channel's post-fader signal as a sidechain.</summary>
    public bool WritePost { get; init; }

    /// <summary>
    ///     Phase 10: the channel runs the multichannel path (<c>AudioEngine.Spatial.cs</c>): its signal has more than two
    ///     channels, or its output or a send reaches a summing point that is not stereo. Otherwise it runs the stereo
    ///     path exactly as before.
    /// </summary>
    public bool Multi { get; init; }

    /// <summary>
    ///     The channels of the channel's signal after its source: an aux's bus's, the master's format's; a track's as
    ///     the plan expects them (2 for a stereo source or one with inserts, else 1; its source decides each block).
    /// </summary>
    public int Width { get; init; } = 2;

    /// <summary>The format of a signal of more than two channels (its layout's order).</summary>
    public SpeakerFormat Format { get; init; } = SpeakerFormat.Stereo;

    /// <summary>
    ///     Phase 11: the channels of the signal after the inserts, which the fader, the sends and the output carry: the
    ///     source's (<see cref="Width" />), unless an insert changes the format (<see cref="FormatInserts" />).
    /// </summary>
    public int SignalWidth { get; init; } = 2;

    /// <summary>The format after the inserts (<see cref="Format" /> unless an insert changes it).</summary>
    public SpeakerFormat SignalFormat { get; init; } = SpeakerFormat.Stereo;

    /// <summary>An insert changes the channel's format (it then runs the multichannel path).</summary>
    public bool FormatInserts { get; init; }

    /// <summary>How the output reaches its summing point (null: nowhere, or the master's own output).</summary>
    public MixEdge? OutputEdge { get; init; }

    /// <summary>How each send reaches its bus (null where the send goes nowhere).</summary>
    public MixEdge?[] SendEdges { get; init; } = [];

    /// <summary>
    ///     The stereo down-mix AFL and PFL listen to, output-major: of the channel's signal when it has more than two
    ///     channels (PFL), of its output edge's channels when they do (AFL). Null otherwise.
    /// </summary>
    public float[]? ListenSignal { get; init; }

    public float[]? ListenOutput { get; init; }

    /// <summary>The Surround Panner's automation targets: angle, diversity, elevation, centre, LFE, spread (−1: none).</summary>
    public int[] SurroundTargets { get; init; } = [-1, -1, -1, -1, -1, -1];

    /// <summary>The object's automation targets: azimuth, elevation, distance, size (−1: none).</summary>
    public int[] ObjectTargets { get; init; } = [-1, -1, -1, -1];

    /// <summary>Whether any panner target has a lane (the panners then run in 32-frame pieces).</summary>
    public bool PannerAutomated { get; init; }
}

/// <summary>How a multichannel channel's output or send reaches its summing point (Phase 10).</summary>
public enum EdgeKind {
    /// <summary>A mono or stereo signal into a stereo point: the stereo pan or balance, as before Phase 10.</summary>
    Stereo,

    /// <summary>A surround signal into a stereo point: its down-mix, then the balance.</summary>
    DownmixStereo,

    /// <summary>Into a mono point: the mean of a stereo signal, a surround signal's down-mix.</summary>
    Mono,

    /// <summary>A signal of the point's format (a bed): channel to channel.</summary>
    Straight,

    /// <summary>A surround signal into a point of another surround format: a gain matrix.</summary>
    Matrix,

    /// <summary>A mono or stereo signal into a surround point: the Surround Panner.</summary>
    Panner,

    /// <summary>The strip is an object: its signal and position go to the master's object renderer.</summary>
    Object
}

/// <summary>An edge of the plan (Phase 10): its kind, its summing point's channels and what it pans or mixes with.</summary>
public sealed class MixEdge {
    public required EdgeKind Kind { get; init; }

    /// <summary>The summing point's channels.</summary>
    public int Width { get; init; } = 2;

    public SpeakerFormat Format { get; init; } = SpeakerFormat.Stereo;

    /// <summary>
    ///     <see cref="EdgeKind.DownmixStereo" />, <see cref="EdgeKind.Mono" /> (of a surround signal),
    ///     <see cref="EdgeKind.Matrix" /> and an object of a surround signal: the gains, output-major
    ///     (<c>[o × inputs + i]</c>).
    /// </summary>
    public float[]? Matrix { get; init; }

    /// <summary><see cref="EdgeKind.Panner" />: the prepared panner and the input width it was prepared for.</summary>
    public ISurroundPanner? Panner { get; init; }

    public int PannerInputs { get; init; }

    /// <summary><see cref="EdgeKind.Object" />: the object's index in the master's renderer.</summary>
    public int ObjectIndex { get; init; } = -1;
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
        DestinationWidths = [..Enumerable.Repeat(2, Destinations)];
        DestinationFormats = [..Enumerable.Repeat(SpeakerFormat.Stereo, Destinations)];
    }

    /// <summary>Each summing point's channels (Phase 10; the device's output pairs are stereo).</summary>
    public int[] DestinationWidths { get; private init; }

    /// <summary>Each summing point's format: the master's input format, an aux's bus's.</summary>
    public SpeakerFormat[] DestinationFormats { get; private init; }

    /// <summary>The master is not stereo (or an insert changes its format): it runs the multichannel path.</summary>
    public bool MasterMulti => Master.Width != 2 || Master.FormatInserts;

    /// <summary>Any channel or summing point is not stereo (the multichannel path runs at all).</summary>
    public bool Multichannel { get; private init; }

    /// <summary>The master's format (its input's, the sum's).</summary>
    public SpeakerFormat MasterFormat => DestinationFormats[0];

    /// <summary>What the master sends the device stage: its format after its inserts (Phase 11).</summary>
    public SpeakerFormat MasterOutputFormat => Master.SignalFormat;

    /// <summary>The channels whose output is an object, by object index (Phase 10).</summary>
    public MixNode[] Objects { get; private init; } = [];

    /// <summary>The master's object renderer (null: no objects).</summary>
    public IObjectRenderer? ObjectRenderer { get; private init; }

    /// <summary>A surround master's stereo down-mix for the listen-in tap (null: a stereo master).</summary>
    public IDownmix? TapDownmix { get; private init; }

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
    public static MixPlan Build(
        TrackState[] tracks,
        ChannelState master,
        AutomationSet automation,
        SpatialContext? spatial = null
    ) =>
        new Builder(tracks, master, automation, spatial ?? SpatialContext.Default).Build();

    /// <summary>Works a plan out on the control thread.</summary>
    sealed class Builder {
        readonly TrackState[] tracks;
        readonly ChannelState master;
        readonly AutomationSet automation;
        readonly ChannelConfig[] configs;
        readonly int[] position;
        readonly int[] sequence;
        readonly HashSet<int> sidechainSources = [];
        readonly SortedSet<int> snapshots = [];
        readonly SortedSet<int> used = [0];

        /// <summary>The Surround Panner's automation parameters, in <see cref="MixNode.SurroundTargets" />' order.</summary>
        static readonly string[] SurroundParameters = ["angle", "diversity", "elevation", "centre", "lfe", "spread"];

        /// <summary>The object's automation parameters, in <see cref="MixNode.ObjectTargets" />' order.</summary>
        static readonly string[] ObjectParameters = ["azimuth", "elevation", "distance", "size"];

        readonly SpatialContext spatial;
        readonly int[] widths = [..Enumerable.Repeat(2, Destinations)];
        readonly SpeakerFormat[] formats = [..Enumerable.Repeat(SpeakerFormat.Stereo, Destinations)];
        readonly List<MixNode> objects = [];
        bool surroundUsed;

        public Builder(TrackState[] tracks, ChannelState master, AutomationSet automation, SpatialContext spatial) {
            this.tracks = tracks;
            this.master = master;
            this.automation = automation;
            this.spatial = spatial;
            configs = [..tracks.Select(static t => t.Frozen ? Frozen(t.Channel.Config) : t.Channel.Config)];
            SetFormat(0, master.Config.InputFormat);
            for (var i = tracks.Length - 1; i >= 0; i--) { // the first aux of a bus decides its format
                if (tracks[i].InputBus is >= 1 and <= Buses) {
                    SetFormat(tracks[i].InputBus, configs[i].InputFormat);
                }
            }

            var levels = Levels();
            sequence = [..Enumerable.Range(0, tracks.Length).OrderBy(i => levels[i]).ThenBy(static i => i)];
            position = new int[tracks.Length];
            for (var k = 0; k < sequence.Length; k++) {
                position[sequence[k]] = k;
            }

            foreach (var config in configs.Append(master.Config)) {
                foreach (var insert in config.Inserts) {
                    if (TrackIndex(insert.SidechainTrack) is var j and >= 0) {
                        sidechainSources.Add(j);
                        tracks[j].Channel.EnsurePost();
                    }
                }
            }
        }

        public MixPlan Build() {
            var order = new MixNode[tracks.Length];
            for (var k = 0; k < order.Length; k++) {
                var i = sequence[k];
                order[k] = Node(i, k, tracks[i], configs[i]);
                if (order[k].OutputEdge is { Kind: EdgeKind.Object }) {
                    objects.Add(order[k]); // its index is the count before it
                }
            }

            var masterNode = Node(-1, order.Length, null, master.Config);
            return new(order, masterNode, [..used], [..snapshots]) {
                DestinationWidths = widths,
                DestinationFormats = formats,
                Multichannel = widths.Any(static w => w != 2) || order.Any(static n => n.Multi),
                Objects = [..objects],
                ObjectRenderer = objects.Count > 0 ? spatial.ObjectRenderer(formats[0]) : null,
                TapDownmix = masterNode.SignalWidth > 2 ? spatial.TapDownmix(masterNode.SignalFormat) : null
            };
        }

        void SetFormat(int destination, SpeakerFormat format) {
            formats[destination] = format;
            widths[destination] = SpeakerLayout.Of(format).ChannelCount;
        }

        /// <summary>The channels a channel's signal has (see <see cref="MixNode.Width" />).</summary>
        int WidthOf(TrackState? track, ChannelConfig config) {
            if (track is null) {
                return widths[0];
            }

            if (track.InputBus is >= 1 and <= Buses) {
                return widths[track.InputBus];
            }

            return track.Stereo || config.Inserts.Length > 0 ? 2 : 1;
        }

        /// <summary>A channel's Phase 10 parts of the plan: its width and format, its edges and its panner's targets.</summary>
        readonly record struct Parts(
            bool Multi,
            int Width,
            SpeakerFormat Format,
            int SignalWidth,
            SpeakerFormat SignalFormat,
            bool FormatInserts,
            MixEdge? Output,
            MixEdge?[] Sends,
            int[] Surround,
            int[] Object,
            bool Automated
        );

        Parts SpatialParts(
            TrackState? track,
            int trackId,
            ChannelState channel,
            ChannelConfig config,
            int output,
            int[] sends
        ) {
            var sourceWidth = WidthOf(track, config);
            var sourceFormat = FormatOf(track, sourceWidth);
            var (width, format, changes) = AfterInserts(config, sourceWidth, sourceFormat);
            surroundUsed = false;
            var outputEdge = track is null
                ? null
                : Edge(channel, null, width, format, output, config.PannerMode == EnginePannerMode.Object);
            var sendEdges = new MixEdge?[sends.Length];
            for (var s = 0; s < sends.Length; s++) {
                sendEdges[s] = Edge(channel, config.Sends[s].Slot, width, format, sends[s], false);
            }

            var multi = sourceWidth > 2 || width > 2 || surroundUsed || changes;
            if (!multi || track is null) {
                return new(
                    multi,
                    sourceWidth,
                    sourceFormat,
                    width,
                    format,
                    changes,
                    outputEdge,
                    sendEdges,
                    [-1, -1, -1, -1, -1, -1],
                    [-1, -1, -1, -1],
                    false
                );
            }

            int[] surround = [..SurroundParameters.Select(p => Lane(trackId, AutomationKind.Surround, null, p))];
            int[] position = [..ObjectParameters.Select(p => Lane(trackId, AutomationKind.Object, null, p))];
            var automated = surround.Any(static t => t >= 0) || position.Any(static t => t >= 0);
            return new(
                true,
                sourceWidth,
                sourceFormat,
                width,
                format,
                changes,
                outputEdge,
                sendEdges,
                surround,
                position,
                automated
            );
        }

        /// <summary>
        ///     The signal after the channel's inserts (Phase 11): each format insert's prepared stage outputs its format
        ///     (one without a stage passes the signal as it is); whether any insert changes the format.
        /// </summary>
        static (int Width, SpeakerFormat Format, bool Changes) AfterInserts(
            ChannelConfig config,
            int width,
            SpeakerFormat format
        ) {
            var changes = false;
            foreach (var insert in config.Inserts) {
                if (insert.Slot is { IsFormat: true, Format: { } stage }) {
                    format = stage.Out;
                    width = stage.OutChannels;
                    changes = true;
                }
            }

            return (width, format, changes);
        }

        /// <summary>The format of a channel's signal: the master's, an aux's bus's, a track's mono or stereo.</summary>
        SpeakerFormat FormatOf(TrackState? track, int width) {
            if (track is null) {
                return formats[0];
            }

            if (track.InputBus is >= 1 and <= Buses) {
                return formats[track.InputBus];
            }

            return width == 1 ? SpeakerFormat.Mono : SpeakerFormat.Stereo;
        }

        /// <summary>
        ///     How a signal of <paramref name="width" /> channels (<paramref name="format" /> when more than two) reaches
        ///     <paramref name="destination" />; an output in object mode becomes an object while the master is surround
        ///     and the renderer has room.
        /// </summary>
        MixEdge? Edge(
            ChannelState channel,
            SendSlot? send,
            int width,
            SpeakerFormat format,
            int destination,
            bool objectMode
        ) {
            if (destination < 0) {
                return null;
            }

            var to = widths[destination];
            var toFormat = formats[destination];
            var toLayout = SpeakerLayout.Of(toFormat);
            if (to == 2) {
                if (width <= 2) {
                    return StereoEdge;
                }

                surroundUsed = true;
                return new() { Kind = EdgeKind.DownmixStereo, Matrix = Convert(format, toLayout) };
            }

            surroundUsed = true;
            if (to == 1) {
                return new() {
                    Kind = EdgeKind.Mono,
                    Width = 1,
                    Format = toFormat,
                    Matrix = width > 2 ? Convert(format, toLayout) : null
                };
            }

            return SurroundEdge(channel, send, width, format, destination, objectMode);
        }

        /// <summary>An edge into a surround point: an object, a bed, a format conversion or the Surround Panner.</summary>
        MixEdge SurroundEdge(
            ChannelState channel,
            SendSlot? send,
            int width,
            SpeakerFormat format,
            int destination,
            bool objectMode
        ) {
            var to = widths[destination];
            var toFormat = formats[destination];
            var toLayout = SpeakerLayout.Of(toFormat);
            if (objectMode
                && destination == 0
                && objects.Count < Dsp.Spatial.ObjectRenderer.MaxObjects
                && spatial.ObjectRenderer(toFormat) is not null) {
                return new() {
                    Kind = EdgeKind.Object,
                    Width = to,
                    Format = toFormat,
                    ObjectIndex = objects.Count,
                    Matrix = width > 2 ? Convert(format, SpeakerLayout.Of(SpeakerFormat.Stereo)) : null
                };
            }

            if (width > 2) {
                return format == toFormat
                    ? new() { Kind = EdgeKind.Straight, Width = to, Format = toFormat }
                    : new() {
                        Kind = EdgeKind.Matrix, Width = to, Format = toFormat, Matrix = Convert(format, toLayout)
                    };
            }

            var panner = spatial.Try(
                () => send is null
                    ? channel.OutputPanner.Get(toFormat, width, spatial)
                    : send.Panner.Get(toFormat, width, spatial),
                "the Surround Panner"
            );
            if (panner is null) {
                // No panner to be had: the signal's channels by label (a mono one to the centre).
                var from = width == 1 ? SpeakerFormat.Mono : SpeakerFormat.Stereo;
                return new() {
                    Kind = EdgeKind.Matrix, Width = to, Format = toFormat, Matrix = Convert(from, toLayout)
                };
            }

            return new() {
                Kind = EdgeKind.Panner,
                Width = to,
                Format = toFormat,
                Panner = panner,
                PannerInputs = width
            };
        }

        float[] Convert(SpeakerFormat from, SpeakerLayout to) =>
            Formats.Conversion(SpeakerLayout.Of(from), to, spatial);

        static readonly MixEdge StereoEdge = new() { Kind = EdgeKind.Stereo };

        static readonly SpeakerLayout StereoLayout = SpeakerLayout.Of(SpeakerFormat.Stereo);

        /// <summary>What AFL and PFL hear a signal wider than stereo through; null for the others.</summary>
        float[]? ListenDownmix(int width, SpeakerFormat format) => width > 2 ? Convert(format, StereoLayout) : null;

        float[]? ListenDownmix(MixEdge? edge) => edge is { Width: > 2 } ? Convert(edge.Format, StereoLayout) : null;

        /// <summary>Each aux sits one level above everything feeding its input bus (a loop the server missed is cut).</summary>
        int[] Levels() {
            var n = tracks.Length;
            var levels = new int[n];
            var changed = true;
            for (var pass = 0; pass <= n && changed; pass++) {
                changed = false;
                for (var i = 0; i < n; i++) {
                    var level = tracks[i].InputBus > 0 ? Math.Min(n, Above(i, tracks[i].InputBus, levels)) : 0;
                    changed |= level != levels[i];
                    levels[i] = level;
                }
            }

            return levels;
        }

        int Above(int aux, int bus, int[] levels) {
            var level = 0;
            for (var j = 0; j < tracks.Length; j++) {
                if (j != aux && Feeds(configs[j], bus)) {
                    level = Math.Max(level, levels[j] + 1);
                }
            }

            return level;
        }

        int TrackIndex(int trackId) {
            if (trackId < 0) {
                return -1;
            }

            for (var j = 0; j < tracks.Length; j++) {
                if (tracks[j].Id == trackId) {
                    return j;
                }
            }

            return -1;
        }

        MixNode Node(int index, int at, TrackState? track, ChannelConfig config) {
            var trackId = track?.Id ?? -1;
            var inserts = config.Inserts;
            var bindings = new ParameterBinding[inserts.Length][];
            for (var s = 0; s < inserts.Length; s++) {
                var slot = inserts[s].Slot;
                bindings[s] = Bindings(trackId, AutomationKind.Insert, slot.Id, slot.IndexOf);
            }

            var midiEffects = config.MidiEffects;
            var midiBindings = new ParameterBinding[midiEffects.Length][];
            for (var s = 0; s < midiEffects.Length; s++) {
                var slot = midiEffects[s].Slot;
                midiBindings[s] = Bindings(trackId, AutomationKind.MidiEffect, slot.Id, slot.IndexOf);
            }

            var instrument = track?.Instrument is { } instrumentSlot
                ? Bindings(trackId, AutomationKind.Instrument, null, instrumentSlot.IndexOf)
                : [];
            var output = track is null ? -1 : Destination(config.Output);
            if (output >= 0) {
                used.Add(output);
            }

            var sends = new int[track is null ? 0 : config.Sends.Length]; // the master has no sends
            for (var s = 0; s < sends.Length; s++) {
                sends[s] = config.Sends[s].Bus is >= 1 and <= Buses ? config.Sends[s].Bus : -1;
                if (sends[s] >= 0) {
                    used.Add(sends[s]);
                }
            }

            var channel = track?.Channel ?? master;
            var spatialParts = SpatialParts(track, trackId, channel, config, output, sends);
            return new() {
                ListenSignal = ListenDownmix(spatialParts.SignalWidth, spatialParts.SignalFormat),
                ListenOutput = ListenDownmix(spatialParts.Output),
                Multi = spatialParts.Multi,
                Width = spatialParts.Width,
                Format = spatialParts.Format,
                SignalWidth = spatialParts.SignalWidth,
                SignalFormat = spatialParts.SignalFormat,
                FormatInserts = spatialParts.FormatInserts,
                OutputEdge = spatialParts.Output,
                SendEdges = spatialParts.Sends,
                SurroundTargets = spatialParts.Surround,
                ObjectTargets = spatialParts.Object,
                PannerAutomated = spatialParts.Automated,
                Channel = channel,
                Config = config,
                TrackIndex = index,
                Track = track,
                TrackId = trackId,
                InputBus = track?.InputBus is >= 1 and <= Buses ? track.InputBus : 0,
                Frozen = track?.Frozen ?? false,
                OutputDestination = output,
                SendDestinations = sends,
                Sidechains = [..inserts.Select(i => Sidechain(i, at))],
                InsertBindings = bindings,
                InsertLast = [..bindings.Select(static b => Enumerable.Repeat(float.NaN, b.Length).ToArray())],
                TempoParameters = [..inserts.Select(static i => i.Slot.IndexOf("tempo"))],
                MidiEffectBindings = midiBindings,
                MidiEffectLast =
                    [..midiBindings.Select(static b => Enumerable.Repeat(float.NaN, b.Length).ToArray())],
                InstrumentBindings = instrument,
                InstrumentLast = [..Enumerable.Repeat(float.NaN, instrument.Length)],
                VolumeTarget = Lane(trackId, AutomationKind.Volume),
                PanTarget = Lane(trackId, AutomationKind.Pan),
                MuteTarget = Lane(trackId, AutomationKind.Mute),
                SendTargets =
                    [..config.Sends.Take(sends.Length).Select(s => Lane(trackId, AutomationKind.Send, s.Slot.Id))],
                WritePost = index >= 0 && sidechainSources.Contains(index),
                Prefetch = Prefetches(track, inserts)
            };
        }

        /// <summary>Whether a track (not an aux, not frozen) has a sandboxed insert; gives it work buffers.</summary>
        static bool Prefetches(TrackState? track, ChannelInsert[] inserts) {
            if (track is not { InputBus: <= 0, Frozen: false }
                || !inserts.Any(static i => i.Slot.Effect is Anode.Plugins.ISplitProcessing)) {
                return false;
            }

            track.Channel.EnsureWork();
            return true;
        }

        /// <summary>The target of <paramref name="kind" /> on the track, when it has a lane (−1: none).</summary>
        int Lane(int trackId, AutomationKind kind, string? id = null, string? parameterId = null) {
            var target = automation.Find(trackId, kind, id, parameterId);
            return target >= 0 && automation.Lane(target) is not null ? target : -1;
        }

        /// <summary>The automated parameters (with lanes) of an insert (<paramref name="id" />) or the instrument.</summary>
        ParameterBinding[] Bindings(int trackId, AutomationKind kind, string? id, Func<string, int> indexOf) {
            var list = new List<ParameterBinding>();
            for (var t = 0; t < automation.Targets.Length; t++) {
                if (automation.Targets[t] is not { ParameterId: { } parameter } target
                    || target.TrackId != trackId
                    || AutomationLane.KindOf(target.Kind) != kind
                    || (id is not null && target.Id != id)
                    || automation.Lane(t) is null) {
                    continue;
                }

                if (indexOf(parameter) is var p and >= 0) {
                    list.Add(new(p, t));
                }
            }

            return [..list];
        }

        SidechainSource Sidechain(ChannelInsert insert, int at) {
            if (insert.SidechainTrack >= 0) {
                return TrackIndex(insert.SidechainTrack) is var j and >= 0 ? new(tracks[j].Channel, 0, false) : default;
            }

            if (insert.SidechainBus is < 1 or > Buses) {
                return default;
            }

            var late = false;
            for (var j = 0; j < tracks.Length; j++) {
                late |= position[j] >= at && Feeds(configs[j], insert.SidechainBus);
            }

            if (late) {
                snapshots.Add(insert.SidechainBus);
            }

            return new(null, insert.SidechainBus, late);
        }
    }

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
