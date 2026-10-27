using Anode.Dsp.Instruments;
using Anode.Engine.Midi;
using Anode.Ipc.Segments;

namespace Anode.Engine;

// The audio callback's MIDI (docs/03-phase-2.md, workstream A): instrument and external MIDI tracks. Per block, a
// MIDI track collects its live input (from the MIDI thread and the server's live MIDI, placed in the block by arrival
// time, one callback late so the timing is jitter-free), its regions' events in play order, and the note-offs its
// bookkeeping owes (stop, locate, cycle wrap, recompile); an instrument renders them at their frames, an external
// MIDI track sends them to its port timestamped to the buffer's output time. While recording, an armed track's live
// input is captured in project samples. Everything here is audio-thread code: no allocation, no locks.
public sealed unsafe partial class AudioEngine {
    readonly (byte Status, byte Note)[] releases = new (byte, byte)[16 * 128];
    long panics;
    long callbackNs;
    long previousCallbackNs;
    long recordStart = -1;
    long recordEnd = -1;

    /// <summary>Messages for external MIDI tracks' ports (consumer: the MIDI thread).</summary>
    public SpscChannel<OutgoingMidi> MidiOut { get; } = new(8192);

    /// <summary>Live MIDI armed tracks received while recording (consumer: the MIDI thread).</summary>
    public SpscChannel<CapturedMidi> CapturedMidi { get; } = new(16384);

    /// <summary>The device's output latency in frames: external MIDI is timestamped this far ahead.</summary>
    public int OutputLatency { get; set; }

    /// <summary>
    ///     How far recorded live MIDI is shifted back, in frames: the callback it waits for plus the output latency, so
    ///     a note lands where the player heard the music when they played it.
    /// </summary>
    public int MidiRecordShift { get; set; }

    /// <summary>Where the last recording pass started rolling (−1: it did not), in project samples.</summary>
    public long RecordStart => Volatile.Read(ref recordStart);

    /// <summary>Where the last recording pass stopped, in project samples.</summary>
    public long RecordEnd => Volatile.Read(ref recordEnd);

    /// <summary>Stop and locate: every MIDI track releases what its playback holds.</summary>
    void Panic() => panics++;

    void BeginRecording() {
        Volatile.Write(ref recordStart, transport.Position);
        Volatile.Write(ref recordEnd, -1);
    }

    void RenderMidi(
        TrackState track,
        in TrackView view,
        int generation,
        int blockOffset,
        long position,
        bool countIn,
        Span<float> left,
        Span<float> right
    ) {
        var midi = track.Midi;
        var slot = track.Instrument;
        var io = track.MidiIo;
        var external = slot is null && io?.OutputDevice is not null;
        midi.Count = 0;
        var frames = left.Length;

        if (midi.Panic != panics) {
            midi.Panic = panics;
            if (slot is not null) {
                slot.Instrument.Reset(); // no release tails on stop or locate
                midi.Held.Clear();
            } else {
                Release(midi, 0);
            }
        }

        if (midi.Generation != generation) {
            midi.Generation = generation; // the regions changed under held notes: end them
            Release(midi, 0);
        }

        LiveInput(track, midi, blockOffset, frames, position, countIn);
        if (transport.Rolling && !countIn) {
            Playback(midi, view, position, frames);
        }

        midi.Sort();
        var events = new ReadOnlySpan<MidiEvent>(midi.Events, 0, midi.Count);
        if (slot is not null) {
            slot.ApplyParameters();
            slot.Instrument.Process(events, left, right);
        } else {
            left.Clear();
            right.Clear();
        }

        if (external) {
            Send(track.Id, io!.OutputChannel, events, blockOffset);
        }
    }

    /// <summary>Takes the track's live MIDI due in this block; captures it while recording an armed track.</summary>
    void LiveInput(TrackState track, MidiTrackState midi, int blockOffset, int frames, long position, bool countIn) {
        var capture = transport.State == TransportState.Recording && !countIn && track.Armed;
        var period = callbackNs - previousCallbackNs;
        while (midi.Live.TryPeek(out var e)) {
            var at = Offset(e.TimeNs, period);
            if (at >= blockOffset + frames && blockOffset + frames < callbackFrames) {
                break; // due in a later block of this callback
            }

            midi.Live.TryDequeue(out _);
            var frame = Math.Clamp(at - blockOffset, 0, frames - 1);
            midi.Add(frame, e.Status, e.Data1, e.Data2);
            if (capture) {
                CapturedMidi.TryEnqueue(
                    new(track.Id, position + frame - MidiRecordShift, e.Status, e.Data1, e.Data2)
                );
            }
        }
    }

    /// <summary>
    ///     Where in this callback live MIDI that arrived at <paramref name="timeNs" /> plays: as far into it as the
    ///     message arrived into the previous one (0 for "now").
    /// </summary>
    int Offset(long timeNs, long period) {
        if (timeNs <= previousCallbackNs || period <= 0) {
            return 0;
        }

        var frames = (long)((timeNs - previousCallbackNs) * (double)SampleRate / 1e9);
        return (int)Math.Clamp(frames, 0, Math.Max(0, callbackFrames - 1));
    }

    /// <summary>The track's region events in [position, + frames) in play order, releasing held notes at a wrap.</summary>
    void Playback(MidiTrackState midi, in TrackView view, long position, int frames) {
        var order = transport.PlayOrder;
        var done = 0;
        while (done < frames) {
            var count = (int)Math.Min(frames - done, order.UntilJump(position));
            AddEvents(midi, view, position, count, done);
            done += count;
            var next = order.Advance(position, count);
            if (next != position + count) {
                Release(midi, Math.Min(done, frames - 1)); // the cycle wrapped
            }

            position = next;
        }
    }

    static void AddEvents(MidiTrackState midi, in TrackView view, long from, int count, int offset) {
        var events = view.Events;
        var end = from + count;
        for (var i = LowerBound(events, view.EventCount, from); i < view.EventCount && events[i].SamplePos < end; i++) {
            ref readonly var e = ref events[i];
            var channel = (byte)(e.Channel & 0x0F);
            byte status, data1 = (byte)(e.Data1 & 0x7F), data2 = (byte)(e.Data2 & 0x7F);
            switch (e.Kind) {
                case PlaybackEventKind.NoteOn:
                    status = (byte)(0x90 | channel);
                    break;
                case PlaybackEventKind.NoteOff:
                    status = (byte)(0x80 | channel);
                    break;
                case PlaybackEventKind.ControlChange:
                    status = (byte)(0xB0 | channel);
                    break;
                case PlaybackEventKind.PitchBend:
                    status = (byte)(0xE0 | channel);
                    break;
                case PlaybackEventKind.ChannelPressure:
                    status = (byte)(0xD0 | channel);
                    data2 = 0;
                    break;
                case PlaybackEventKind.ProgramChange:
                    status = (byte)(0xC0 | channel);
                    data2 = 0;
                    break;
                default:
                    continue;
            }

            midi.Held.Track(status, data1, data2);
            midi.Add(offset + (int)(e.SamplePos - from), status, data1, data2);
        }
    }

    /// <summary>Note-offs for every note the track's playback holds, at <paramref name="frame" />.</summary>
    void Release(MidiTrackState midi, int frame) {
        if (midi.Held.IsEmpty) {
            return;
        }

        var count = midi.Held.Release(releases);
        for (var i = 0; i < count; i++) {
            midi.Add(frame, releases[i].Status, releases[i].Note, 0);
        }
    }

    /// <summary>Queues an external MIDI track's events for its port, on its channel, at the buffer's output time.</summary>
    void Send(int trackId, int channel, ReadOnlySpan<MidiEvent> events, int blockOffset) {
        var nsPerFrame = 1e9 / SampleRate;
        var to = (byte)(Math.Clamp(channel, 1, 16) - 1);
        foreach (var e in events) {
            var time = callbackNs + (long)((OutputLatency + blockOffset + e.Frame) * nsPerFrame);
            MidiOut.TryEnqueue(new(trackId, time, (byte)((e.Status & 0xF0) | to), e.Data1, e.Data2));
        }
    }

    static long LowerBound(PlaybackEvent* events, long count, long position) {
        long lo = 0, hi = count;
        while (lo < hi) {
            var mid = lo + ((hi - lo) >> 1);
            if (events[mid].SamplePos < position) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }

        return lo;
    }
}
