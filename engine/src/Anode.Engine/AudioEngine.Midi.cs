using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
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
    readonly MidiEventBuffer midiOutput = new();
    readonly MidiEvent[] midiStageA = new MidiEvent[MidiEventBuffer.Capacity];
    readonly MidiEvent[] midiStageB = new MidiEvent[MidiEventBuffer.Capacity];
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
        BeginRecordRange();
    }

    void RenderMidi(
        TrackState track,
        Mixing.MixNode node,
        in TrackView view,
        int generation,
        int blockOffset,
        long position,
        long clock,
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

        if (midi.WrapPending) {
            midi.WrapPending = false;
            Release(midi, 0); // the previous block ended at a cycle jump
        }

        var cells = track.LiveLoopsActive;
        if (midi.CellsActive != cells) {
            midi.CellsActive = cells; // switched between its regions and its cells: what the other held ends
            Release(midi, 0);
        }

        LiveInput(track, midi, blockOffset, frames, clock, countIn);
        if (cells) {
            CellMidi(midi, track, blockModel.Cells, frames);
        } else if (transport.Rolling && !countIn) {
            Playback(midi, view, position, frames);
        }

        midi.Sort();
        var events = new ReadOnlySpan<MidiEvent>(midi.Events, 0, midi.Count);
        if (node.Config.MidiEffects.Length > 0) {
            events = RunMidiEffects(node, midi, frames);
        }

        if (slot is not null) {
            slot.ApplyParameters();
            RenderInstrument(node, slot, events, left, right);
        } else {
            left.Clear();
            right.Clear();
        }

        if (external) {
            Send(track.Id, io!.OutputChannel, events, blockOffset);
        }
    }

    /// <summary>
    ///     Runs the track's MIDI FX slots in order on the block's events (<paramref name="midi" />, sorted): each slot's
    ///     queued and automated parameters are set first (automation at the block's start), a bypassed or empty slot
    ///     passes the events through, and each effect's output is sorted (stably) and kept inside the block before the
    ///     next. The result replaces the block's events (what the instrument plays, an external track sends). Effects are
    ///     reset on stop and locate, as instruments are.
    /// </summary>
    ReadOnlySpan<MidiEvent> RunMidiEffects(Mixing.MixNode node, MidiTrackState midi, int frames) {
        var chain = node.Config.MidiEffects;
        ReadOnlySpan<MidiEvent> input = new(midi.Events, 0, midi.Count);
        var transportContext = blockTransport;
        var into = midiStageA;
        var processed = false;
        for (var k = 0; k < chain.Length; k++) {
            var slot = chain[k].Slot;
            slot.ApplyParameters();
            if (slot.Effect is not { } effect) {
                continue;
            }

            if (slot.Panic != panics) {
                slot.Panic = panics;
                effect.Reset();
            }

            SetAutomatedMidi(node.MidiEffectBindings[k], node.MidiEffectLast[k], effect);
            if (chain[k].Bypassed) {
                continue;
            }

            midiOutput.Clear();
            effect.Process(transportContext, input, midiOutput);
            midiOutput.Sort();
            var produced = midiOutput.Events;
            for (var i = 0; i < produced.Length; i++) {
                var e = produced[i];
                into[i] = e with { Frame = Math.Clamp(e.Frame, 0, frames - 1) };
            }

            input = new(into, 0, produced.Length);
            into = ReferenceEquals(into, midiStageA) ? midiStageB : midiStageA;
            processed = true;
        }

        if (processed) {
            input.CopyTo(midi.Events); // the last effect's output becomes the block's events
            midi.Count = input.Length;
        }

        return new(midi.Events, 0, midi.Count);
    }

    /// <summary>Sets a MIDI FX slot's automated parameters to their lanes' values at the block's start, when they changed.</summary>
    void SetAutomatedMidi(Mixing.ParameterBinding[] bindings, float[] last, IMidiEffect effect) {
        if (bindings.Length == 0) {
            return;
        }

        var automation = blockModel.Automation;
        for (var b = 0; b < bindings.Length; b++) {
            if (LaneFor(automation, bindings[b].Target) is not { } lane) {
                continue;
            }

            var value = lane.ValueAt(positions[0]);
            if (value != last[b]) {
                effect.SetParameter(bindings[b].Parameter, value);
                last[b] = value;
            }
        }
    }

    /// <summary>
    ///     Takes the track's live MIDI due in this block; captures it at the position it was played (through the cycle):
    ///     while recording an armed track (inside the punch range, with autopunch), and for Flashback Capture while
    ///     playing.
    /// </summary>
    void LiveInput(TrackState track, MidiTrackState midi, int blockOffset, int frames, long clock, bool countIn) {
        var recording = transport.State == TransportState.Recording && !countIn && track.Armed;
        var flashback = flashbackActive;
        var period = callbackNs - previousCallbackNs;
        while (midi.Live.TryPeek(out var e)) {
            var at = Offset(e.TimeNs, period);
            if (at >= blockOffset + frames && blockOffset + frames < callbackFrames) {
                break; // due in a later block of this callback
            }

            midi.Live.TryDequeue(out _);
            var frame = Math.Clamp(at - blockOffset, 0, frames - 1);
            midi.Add(frame, e.Status, e.Data1, e.Data2);
            var (owner, armed) = e.Origin >= 0 ? Origin(e.Origin, countIn) : (track.Id, recording);
            if (armed || flashback) {
                Capture(e, owner, armed, flashback, clock + frame - MidiRecordShift);
            }
        }
    }

    /// <summary>
    ///     A routed message's owner (Phase 6): the Drum Machine Designer track it was played on, whose arm decides
    ///     whether it is recorded.
    /// </summary>
    (int Owner, bool Armed) Origin(int trackId, bool countIn) {
        var index = blockModel.IndexOf(trackId);
        return (trackId,
            transport.State == TransportState.Recording && !countIn && index >= 0 && blockModel.Tracks[index].Armed);
    }

    /// <summary>
    ///     Captures live MIDI played at clock <paramref name="played" /> for <paramref name="owner" />: into the take while
    ///     it records (inside the punch range, with autopunch), else into Flashback Capture's history.
    /// </summary>
    void Capture(in LiveMidi e, int owner, bool armed, bool flashback, long played) {
        var sample = PlayedPosition(played);
        if (armed && (!punch.Active || (sample >= punch.In && sample < punch.Out))) {
            CapturedMidi.TryEnqueue(new(owner, sample, e.Status, e.Data1, e.Data2));
        } else if (flashback) {
            CapturedMidi.TryEnqueue(new(owner, sample, e.Status, e.Data1, e.Data2, played, true));
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
                // The cycle wrapped. A block ends at a jump, so the notes end on the next block's first frame.
                if (done < frames) {
                    Release(midi, done);
                } else {
                    midi.WrapPending = true;
                }
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
