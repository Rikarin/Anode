using Anode.Dsp.Flex;
using Anode.Dsp.Instruments;
using Anode.Engine.Midi;
using Anode.Engine.Mixing;
using Anode.Engine.Recording;
using Anode.Engine.Streaming;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine;

/// <summary>
///     A track as the engine runs it: its stream from the disk thread, its strip, its input settings and, while a
///     recording pass runs, its take; for an instrument or external MIDI track also its instrument, its MIDI settings
///     and the audio thread's MIDI state. One object per engine track id lives across model swaps (its stream keeps
///     playing); it is replaced only when the track's channel count changes, and the replacement takes over the
///     instrument and MIDI state.
/// </summary>
public sealed class TrackState : IDisposable {
    int inputChannel;
    bool armed;
    bool monitoring;
    RecordTake? take;
    FlashbackBuffer? flashback;
    TrackFeed? crossfade;
    InstrumentSlot? instrument;
    EngineMidiIo? midiIo;

    public TrackState(
        int id,
        bool stereo,
        int streamFrames,
        MidiTrackState? midi = null,
        ChannelState? channel = null
    ) {
        Id = id;
        Stream = new(stereo ? 2 : 1, streamFrames);
        Midi = midi ?? new();
        Channel = channel ?? new(new());
        SourceStereo = stereo;
    }

    public int Id { get; }

    public bool Stereo => Stream.Channels == 2;

    /// <summary>The track's stream: every playlist entry, or those in lane 0 when it has crossfades.</summary>
    public TrackFeed Stream { get; }

    /// <summary>
    ///     The track's second stream, for the entries in lane 1 (<see cref="PlaylistLanes" />): made by the control
    ///     thread the first time a compile of the track has crossfades, and kept.
    /// </summary>
    public TrackFeed? Crossfade => Volatile.Read(ref crossfade);

    /// <summary>The track's fader (<see cref="ChannelState.Strip" />).</summary>
    public Strip Strip => Channel.Strip;

    /// <summary>The track's channel strip: fader, inserts, sends and output, kept when the track's stream is replaced.</summary>
    public ChannelState Channel { get; }

    /// <summary>An aux track's input bus (1…64), else 0. Control thread; the audio thread reads the plan's copy.</summary>
    public int InputBus { get; set; }

    /// <summary>The freeze file the track plays instead of its source and inserts (null: not frozen). Control thread.</summary>
    public string? FreezeFile { get; set; }

    public bool Frozen => FreezeFile is not null;

    /// <summary>
    ///     Whether the track's source is stereo (<see cref="EngineTrack.Stereo" />); a frozen mono track streams its
    ///     stereo freeze file but keeps its mono pan law when it had no inserts (the file's channels are then equal).
    /// </summary>
    public bool SourceStereo { get; set; }

    /// <summary>The 0-based first device input (−1: none); a stereo track takes it and the next one.</summary>
    public int InputChannel => Volatile.Read(ref inputChannel);

    public bool Armed => Volatile.Read(ref armed);

    public bool Monitoring => Volatile.Read(ref monitoring);

    /// <summary>The take this track records into during the current pass, if it is armed.</summary>
    public RecordTake? Take {
        get => Volatile.Read(ref take);
        set => Volatile.Write(ref take, value);
    }

    /// <summary>Flashback Capture's memory of the track's input (an armed audio track), swapped by the control thread.</summary>
    public FlashbackBuffer? Flashback {
        get => Volatile.Read(ref flashback);
        set => Volatile.Write(ref flashback, value);
    }

    /// <summary>Control thread: makes the second stream (before publishing a model whose playlist needs it).</summary>
    public void EnsureCrossfade() {
        if (Crossfade is null) {
            Volatile.Write(ref crossfade, new(Stream.Channels, Stream.TargetFrames));
        }
    }

    /// <summary>
    ///     Control thread, before publishing a model with <paramref name="view" />: gives each stream the flex renderers
    ///     its warped entries need (by <paramref name="lanes" />; the second stream must exist when they use lane 1).
    /// </summary>
    public unsafe void EnsureFlex(in TrackView view, byte[]? lanes, int sampleRate) {
        for (long e = 0; e < view.PlaylistCount; e++) {
            ref readonly var entry = ref view.Playlist[e];
            if (entry.WarpCount <= 0) {
                continue;
            }

            var lane = lanes is not null && e < lanes.Length ? lanes[e] : 0;
            (lane == 0 ? Stream : Crossfade)?.Flex.Ensure(
                (FlexKind)entry.Algorithm,
                sampleRate,
                DiskStreamer.ReadFrames
            );
        }
    }

    /// <summary>Control thread: gives this track's streams renderers for the algorithms <paramref name="previous" />'s have.</summary>
    public void EnsureFlexLike(TrackState previous, int sampleRate) {
        foreach (var algorithm in previous.Stream.Flex.Algorithms) {
            Stream.Flex.Ensure(algorithm, sampleRate, DiskStreamer.ReadFrames);
        }

        if (previous.Crossfade is { } second && Crossfade is { } mine) {
            foreach (var algorithm in second.Flex.Algorithms) {
                mine.Flex.Ensure(algorithm, sampleRate, DiskStreamer.ReadFrames);
            }
        }
    }

    /// <summary>The track's instrument (an instrument track), swapped whole by the control thread.</summary>
    public InstrumentSlot? Instrument {
        get => Volatile.Read(ref instrument);
        set => Volatile.Write(ref instrument, value);
    }

    /// <summary>The track's MIDI input and output (instrument and external MIDI tracks).</summary>
    public EngineMidiIo? MidiIo {
        get => Volatile.Read(ref midiIo);
        set => Volatile.Write(ref midiIo, value);
    }

    /// <summary>An instrument or external MIDI track: it plays MIDI, and records MIDI rather than audio.</summary>
    public bool IsMidi => Instrument is not null || MidiIo is not null;

    /// <summary>The audio thread's MIDI state, and the queue live MIDI reaches it through.</summary>
    public MidiTrackState Midi { get; }

    /// <summary>Control thread.</summary>
    public void SetIo(int input, bool recordArmed, bool inputMonitoring) {
        Volatile.Write(ref inputChannel, input);
        Volatile.Write(ref armed, recordArmed);
        Volatile.Write(ref monitoring, inputMonitoring);
    }

    /// <summary>Control thread.</summary>
    public void Apply(EngineTrack settings) {
        SetIo(settings.InputChannel, settings.RecordArmed, settings.Monitoring);
        Strip.Set(settings.Strip);
        MidiIo = settings.Midi;
        InputBus = settings.InputBus;
        FreezeFile = settings.FreezeFile;
        SourceStereo = settings.Stereo;
    }

    public void Dispose() {
        Stream.Dispose();
        crossfade?.Dispose();
        flashback?.Dispose();
        flashback = null;
    }
}

/// <summary>
///     One track's MIDI as the audio thread renders it: the block's events (preallocated), the notes its playback holds
///     (released on stop, locate, cycle wrap and recompiles), and live MIDI queued for it by the MIDI thread and the
///     control thread.
/// </summary>
public sealed class MidiTrackState {
    /// <summary>Most events one block passes to an instrument; more are dropped.</summary>
    public const int MaxEvents = 1024;

    /// <summary>The block's events, sorted by frame before an instrument sees them.</summary>
    public MidiEvent[] Events { get; } = new MidiEvent[MaxEvents];

    public int Count { get; set; }

    /// <summary>Notes the track's regions started and have not ended (by their own channel).</summary>
    public NoteSet Held { get; } = new();

    /// <summary>Live MIDI for the track (producers: the MIDI thread and the control thread, under its gate).</summary>
    public SpscChannel<LiveMidi> Live { get; } = new(1024);

    /// <summary>The compile generation the held notes came from.</summary>
    public int Generation { get; set; } = -1;

    /// <summary>The engine's stop/locate count the track has acted on.</summary>
    public long Panic { get; set; }

    /// <summary>Audio thread: appends an event (dropped when the block is full).</summary>
    public void Add(int frame, byte status, byte data1, byte data2) {
        if (Count < MaxEvents) {
            Events[Count++] = new(frame, status, data1, data2);
        }
    }

    /// <summary>Audio thread: a stable insertion sort by frame (a block holds few events, mostly in order).</summary>
    public void Sort() {
        var events = Events;
        for (var i = 1; i < Count; i++) {
            var e = events[i];
            var j = i - 1;
            while (j >= 0 && events[j].Frame > e.Frame) {
                events[j + 1] = events[j];
                j--;
            }

            events[j + 1] = e;
        }
    }
}

/// <summary>
///     The published model (RCU, docs/adr/0006-bulk-ipc.md): immutable once published, swapped whole by the control
///     thread, read by the audio and disk threads without locks. It holds each track's view into a mapped segment and
///     the generation of that view, so the disk thread knows when to re-render a track.
/// </summary>
public sealed class ModelRoot {
    public static readonly ModelRoot Empty = new(0, 0, [], [], [], [], default, null, []);

    /// <summary>The mixer graph (published with the model, RCU).</summary>
    public MixPlan Mix { get; init; } = MixPlan.Empty;

    /// <summary>The master's events (its automation), from the segment's track −1.</summary>
    public TrackView MasterView { get; init; }

    public MappedSegment? MasterOwner { get; init; }

    /// <summary>Each frozen track's freeze file (null for the others); its view plays it instead of the playlist.</summary>
    public FreezeSource?[] Freeze { get; init; } = [];

    /// <summary>
    ///     Each track's view as compiled (a frozen track's <see cref="Views" /> entry plays its freeze file instead), so
    ///     a patch or an unfreeze starts from the compiled playlist.
    /// </summary>
    public TrackView[] SourceViews {
        get => sourceViews ?? Views;
        init => sourceViews = value;
    }

    readonly TrackView[]? sourceViews;

    /// <summary>The automation targets and their lanes.</summary>
    public AutomationSet Automation { get; init; } = AutomationSet.Empty;

    /// <summary>The files track <paramref name="index" />'s playlist refers to (a frozen track: its freeze file).</summary>
    public StreamFile?[] FilesFor(int index) =>
        (uint)index < (uint)Freeze.Length && Freeze[index] is { } freeze ? freeze.Table : Files;

    /// <summary>A copy with other tracks (and their views, generations and lanes) or another plan.</summary>
    public ModelRoot With(
        TrackState[]? tracks = null,
        int[]? generations = null,
        MixPlan? mix = null
    ) =>
        new(
            Version,
            LengthSamples,
            tracks ?? Tracks,
            Views,
            generations ?? Generations,
            Owners,
            Metronome,
            MetronomeOwner,
            Files,
            Lanes
        ) {
            Mix = mix ?? Mix,
            MasterView = MasterView,
            MasterOwner = MasterOwner,
            Freeze = Freeze,
            SourceViews = SourceViews,
            Automation = Automation
        };

    public ModelRoot(
        long version,
        long lengthSamples,
        TrackState[] tracks,
        TrackView[] views,
        int[] generations,
        MappedSegment?[] owners,
        TrackView metronome,
        MappedSegment? metronomeOwner,
        StreamFile?[] files,
        byte[]?[]? lanes = null
    ) {
        Version = version;
        Lanes = lanes ?? new byte[]?[tracks.Length];
        LengthSamples = lengthSamples;
        Tracks = tracks;
        Views = views;
        Generations = generations;
        Owners = owners;
        Metronome = metronome;
        MetronomeOwner = metronomeOwner;
        Files = files;
    }

    public long Version { get; }

    public long LengthSamples { get; }

    /// <summary>The tracks, in mix order.</summary>
    public TrackState[] Tracks { get; }

    /// <summary>Each track's compiled events and playlist (default: none yet).</summary>
    public TrackView[] Views { get; }

    /// <summary>Each track's compile generation: changes whenever its view does.</summary>
    public int[] Generations { get; }

    /// <summary>The segment each view points into (reference-counted on the control thread).</summary>
    public MappedSegment?[] Owners { get; }

    /// <summary>The metronome's clicks (<see cref="TrackEntry.MetronomeTrack" />).</summary>
    public TrackView Metronome { get; }

    public MappedSegment? MetronomeOwner { get; }

    /// <summary>
    ///     Each track's playlist lanes (<see cref="PlaylistLanes.Of" />): null when nothing overlaps and the track
    ///     plays through one stream, else the lane of each entry, and the track plays its second stream too.
    /// </summary>
    public byte[]?[] Lanes { get; }

    /// <summary>The files the playlists name, indexed by engine file id.</summary>
    public StreamFile?[] Files { get; }

    public int IndexOf(int trackId) {
        for (var i = 0; i < Tracks.Length; i++) {
            if (Tracks[i].Id == trackId) {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
///     A frozen track's source: its freeze file (stereo, from the project start) as a one-region playlist the disk
///     thread streams like any other. Owned by the control thread; released once no published model uses it.
/// </summary>
public sealed unsafe class FreezeSource : IDisposable {
    readonly AudioPlaylistEntry* entry;

    public FreezeSource(string path) {
        File = new(0, path);
        try {
            if (File.Channels > PlaylistRenderer.MaxFileChannels) {
                throw new InvalidDataException($"{File.Channels} channels");
            }

            entry = (AudioPlaylistEntry*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(
                (nuint)sizeof(AudioPlaylistEntry)
            );
            *entry = new() {
                Start = 0,
                FileOffset = 0,
                Length = File.Frames,
                FileId = 0,
                Gain = 1f
            };
        } catch {
            File.Dispose();
            throw;
        }

        Table = [File];
    }

    public string Path => File.Path;

    public StreamFile File { get; }

    /// <summary>The file table its playlist indexes (the freeze file is id 0).</summary>
    public StreamFile?[] Table { get; }

    /// <summary>The track's view with its events kept and its playlist replaced by the freeze file.</summary>
    public TrackView View(in TrackView track) => new(track.Events, track.EventCount, entry, 1);

    public void Dispose() {
        System.Runtime.InteropServices.NativeMemory.Free(entry);
        File.Dispose();
    }
}
