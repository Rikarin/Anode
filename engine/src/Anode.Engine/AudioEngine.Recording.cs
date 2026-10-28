using Anode.Engine.Recording;
using Anode.Engine.Streaming;

namespace Anode.Engine;

/// <summary>The autopunch range in project samples: armed tracks record (and hear their input) only inside it.</summary>
public readonly record struct PunchRange(bool Enabled, long In, long Out) {
    public bool Active => Enabled && Out > In;
}

// Phase 3's recording modes on the audio thread (docs/04-phase-3.md, workstream A): cycle recording (takes keep
// pushing across wraps, each wrap a new pass), autopunch (record and monitor only inside the punch range, sample-
// exactly at both edges), Flashback Capture's memory (armed tracks' input while playing, and where the playhead was at
// every clock), and the preview channel. Audio-thread code: no allocation, no locks.
public sealed unsafe partial class AudioEngine {
    PreviewSource? preview;
    PunchRange punch;
    bool flashbackRestart;
    bool flashbackActive;
    long flashbackStart = -1;
    long flashbackEnd = -1;
    long recordLow = long.MaxValue;
    long recordHigh = long.MinValue;

    /// <summary>Input plus output latency in frames: recorded input is placed this far back, where it was played.</summary>
    public int RecordLatency { get; set; }

    /// <summary>Where the playhead was at every clock while rolling (Flashback Capture maps its frames through it).</summary>
    public PositionHistory History { get; } = new();

    /// <summary>The autopunch range as the audio thread last applied it.</summary>
    public PunchRange Punch => punch;

    /// <summary>
    ///     The clocks of the last run of playback (not recording) Flashback Capture covers: from where it last started
    ///     (−1: none since the last recording) to where it is now or stopped.
    /// </summary>
    public (long Start, long End) FlashbackWindow =>
        (Volatile.Read(ref flashbackStart), Volatile.Read(ref flashbackEnd));

    /// <summary>The lowest and highest positions the last recording pass covered (linear; for its MIDI takes).</summary>
    public (long Low, long High) RecordRange => (Volatile.Read(ref recordLow), Volatile.Read(ref recordHigh));

    /// <summary>The file playing through the preview channel, if any.</summary>
    public PreviewSource? Preview => Volatile.Read(ref preview);

    /// <summary>
    ///     Control thread: plays <paramref name="next" /> through the preview channel (null: silence); returns the source
    ///     it replaced, to dispose after the audio and disk threads' grace periods.
    /// </summary>
    public PreviewSource? SetPreview(PreviewSource? next) => Interlocked.Exchange(ref preview, next);

    /// <summary>The block's bookkeeping before the tracks: the position history, Flashback's run, the recorded range.</summary>
    void BeginBlock(long clock, long position, int frames, bool countIn) {
        if (Volatile.Read(ref offline) != 0) {
            flashbackActive = false; // a render is neither played nor kept for Flashback Capture
            flashbackRestart = true;
            return;
        }

        var rolling = transport.Rolling && !countIn;
        if (rolling) {
            History.Played(clock, position, frames, transport.PlayOrder);
        }

        flashbackActive = transport.State == TransportState.Playing && !countIn;
        if (flashbackActive) {
            if (flashbackRestart || flashbackStart < 0 || flashbackEnd != clock) {
                flashbackRestart = false;
                Volatile.Write(ref flashbackStart, clock);
            }

            Volatile.Write(ref flashbackEnd, clock + frames);
        }

        if (transport.State == TransportState.Recording && !countIn) {
            var order = transport.PlayOrder;
            var done = 0;
            while (done < frames) {
                var run = (int)Math.Min(frames - done, order.UntilJump(position));
                Volatile.Write(ref recordLow, Math.Min(recordLow, position));
                Volatile.Write(ref recordHigh, Math.Max(recordHigh, position + run));
                done += run;
                position = order.Advance(position, run);
            }
        }
    }

    /// <summary>A recording pass starts: Flashback's run ends (a capture covers playback since it last started).</summary>
    void BeginRecordRange() {
        Volatile.Write(ref recordLow, long.MaxValue);
        Volatile.Write(ref recordHigh, long.MinValue);
        Volatile.Write(ref flashbackStart, -1);
        Volatile.Write(ref flashbackEnd, -1);
    }

    /// <summary>
    ///     Autopunch on an armed track while recording: inside the punch range the track's playback is replaced by its
    ///     input (null: silence), outside it plays back; the block's positions follow the cycle.
    /// </summary>
    void ApplyPunch(Span<float> left, Span<float> right, bool stereo, float* in0, float* in1, long position) {
        var order = transport.PlayOrder;
        var frames = left.Length;
        var done = 0;
        while (done < frames) {
            var run = (int)Math.Min(frames - done, order.UntilJump(position));
            var from = Math.Max(position, punch.In);
            var to = Math.Min(position + run, punch.Out);
            if (from < to) {
                var at = done + (int)(from - position);
                var count = (int)(to - from);
                Replace(left.Slice(at, count), in0, at);
                if (stereo) {
                    Replace(right.Slice(at, count), in1, at);
                }
            }

            done += run;
            position = order.Advance(position, run);
        }
    }

    /// <summary>Overwrites <paramref name="destination" /> with the input from frame <paramref name="at" /> (null: silence).</summary>
    static void Replace(Span<float> destination, float* input, int at) {
        if (input is null) {
            destination.Clear();
        } else {
            new ReadOnlySpan<float>(input + at, destination.Length).CopyTo(destination);
        }
    }

    /// <summary>
    ///     Pushes a block of input (arrived at clocks [<paramref name="clock" />, + <paramref name="frames" />)) into a
    ///     take at the positions where it was played (<see cref="RecordLatency" /> earlier), run by run through the
    ///     cycle, keeping only what lies inside the punch range (when autopunch is on) and at or after zero.
    /// </summary>
    void Record(RecordTake take, float* in0, float* in1, int frames, long clock) {
        var anchor = transport.Anchor;
        var done = 0;
        while (done < frames) {
            var played = clock + done - RecordLatency;
            long position;
            int run;
            if (played < anchor.Clock) {
                // Before the anchor (within a latency of a locate or a start): linear back from it.
                position = anchor.Position - (anchor.Clock - played);
                run = (int)Math.Min(frames - done, anchor.Clock - played);
            } else {
                position = anchor.PositionAt(played);
                run = (int)Math.Min(frames - done, anchor.Cycle.UntilJump(position));
            }

            var from = Math.Max(position, 0);
            var to = position + run;
            if (punch.Active) {
                from = Math.Max(from, punch.In);
                to = Math.Min(to, punch.Out);
            }

            if (from < to) {
                var at = done + (int)(from - position);
                take.Push(in0 is null ? null : in0 + at, in1 is null ? null : in1 + at, (int)(to - from), from);
            }

            done += run;
        }
    }

    /// <summary>The position played at <paramref name="clock" /> (before the anchor: linear back from it).</summary>
    long PlayedPosition(long clock) {
        var anchor = transport.Anchor;
        return clock >= anchor.Clock ? anchor.PositionAt(clock) : anchor.Position - (anchor.Clock - clock);
    }
}
