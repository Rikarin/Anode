using Anode.Dsp.Instruments;
using Anode.Engine.Midi;
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

    public TrackState(int id, bool stereo, int streamFrames, MidiTrackState? midi = null) {
        Id = id;
        Stream = new(stereo ? 2 : 1, streamFrames);
        Midi = midi ?? new();
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

    public Strip Strip { get; } = new();

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
            Volatile.Write(ref crossfade, new TrackFeed(Stream.Channels, Stream.TargetFrames));
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
