using Anode.Engine.Sync;
using Anode.Ipc;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Anode.MidiProbe;

// The probe as a MIDI Clock or MTC master: a thread that schedules its messages a little ahead, timestamped (CoreMIDI
// carries the timestamps to the receiver), so their spacing is exact whatever the thread's wake-ups.
public sealed partial class Probe {
    /// <summary>How far ahead of their time the sources send.</summary>
    const long LookaheadNs = 20_000_000;

    readonly Lock sourceGate = new();
    Thread? clockThread;
    volatile bool clockRunning;
    double clockBpm = 120;
    bool clockTicking; // the transport runs (else ticks only when tickWhileStopped)
    bool tickWhileStopped;
    long nextTickNs;
    byte[] startBytes = [];
    long startNs;
    Thread? mtcThread;
    volatile bool mtcRunning;

    /// <summary>
    ///     <c>clock</c>: MIDI Clock at <c>bpm</c> (120): with <c>start</c> (true) Start, or with <c>sixteenths</c> a Song
    ///     Position Pointer and Continue, then 24 clocks a beat. With <c>preRollMs</c> the clocks run that long before
    ///     the start (as many masters send clock while stopped); the start falls on a clock.
    /// </summary>
    JsonObject StartClock(JsonObject request) {
        StopClockThread();
        var bpm = Double(request, "bpm", 120);
        var period = (long)Math.Round(60e9 / (bpm * 24));
        var first = Now() + LookaheadNs;
        var preRoll = (long)(Double(request, "preRollMs", 0) * 1e6);
        var startAt = first + (preRoll + period - 1) / period * period;
        byte[] begin = request["sixteenths"] is not null
            ? [
                0xF2, (byte)(Int(request, "sixteenths", 0) & 0x7F), (byte)((Int(request, "sixteenths", 0) >> 7) & 0x7F),
                0xFB
            ]
            : request["start"]?.GetValue<bool>() != false
                ? [0xFA]
                : [];
        lock (sourceGate) {
            clockBpm = bpm;
            clockTicking = true;
            tickWhileStopped = false;
            nextTickNs = first;
            startBytes = begin;
            startNs = startAt;
        }

        clockRunning = true;
        clockThread = new(ClockLoop) { IsBackground = true, Name = "midiprobe clock" };
        clockThread.Start();
        return new() { ["start"] = startAt, ["bpm"] = bpm };
    }

    /// <summary><c>clockTempo</c>: the clock's tempo from the next tick on.</summary>
    JsonObject ClockTempo(JsonObject request) {
        lock (sourceGate) {
            clockBpm = Double(request, "bpm", clockBpm);
            return new() { ["bpm"] = clockBpm };
        }
    }

    /// <summary><c>clockStop</c>: Stop after the clocks already sent; <c>keepTicking</c> goes on with the clocks.</summary>
    JsonObject StopClock(JsonObject request) {
        long at;
        lock (sourceGate) {
            at = Math.Max(Now(), nextTickNs - (long)(60e9 / (clockBpm * 24)) + 100_000);
            clockTicking = false;
            tickWhileStopped = request["keepTicking"]?.GetValue<bool>() == true;
        }

        if (!tickWhileStopped) {
            StopClockThread();
        }

        SendBytes([0xFC], at);
        return new() { ["stop"] = at };
    }

    void ClockLoop() {
        while (clockRunning) {
            var (stop, at, begin) = NextClock();
            if (stop) {
                return;
            }

            if (at == 0) {
                Thread.Sleep(2);
                continue;
            }

            try {
                if (begin.Length > 0) {
                    SendBytes(begin, at - 500_000); // Start (or the pointer and Continue) just before its first clock
                }

                SendBytes([0xF8], at);
            } catch (InvalidOperationException) {
                return;
            }
        }
    }

    /// <summary>
    ///     The next clock when it is within the lookahead (else 0), the start messages due before it, or that the clock
    ///     has stopped.
    /// </summary>
    (bool Stop, long At, byte[] Begin) NextClock() {
        lock (sourceGate) {
            if (!clockTicking && !tickWhileStopped) {
                return (true, 0, []);
            }

            var at = nextTickNs;
            if (at > Now() + LookaheadNs) {
                return (false, 0, []);
            }

            nextTickNs += (long)Math.Round(60e9 / (clockBpm * 24));
            byte[] begin = [];
            if (startBytes.Length > 0 && at >= startNs) {
                begin = startBytes;
                startBytes = [];
            }

            return (false, at, begin);
        }
    }

    void StopClockThread() {
        clockRunning = false;
        if (clockThread is not null && clockThread != Thread.CurrentThread) {
            clockThread.Join();
        }

        clockThread = null;
    }

    /// <summary>
    ///     <c>mtc</c>: MTC at <c>rate</c> (fps25) from <c>from</c> ("hh:mm:ss:ff", or <c>seconds</c>): a full frame, then
    ///     quarter frames.
    /// </summary>
    JsonObject StartMtc(JsonObject request) {
        StopMtcThread();
        var rate = Rate(request);
        var seconds = Text(request, "from") is { } label ? ParseLabel(label, rate) : Double(request, "seconds", 0);
        var startAt = Now() + LookaheadNs;
        var encoder = new MtcEncoder(rate);
        var full = new byte[10];
        encoder.FullFrame(seconds, full);
        SendBytes(full, startAt - 1_000_000);
        mtcRunning = true;
        mtcThread = new(() => MtcLoop(encoder, seconds, startAt)) { IsBackground = true, Name = "midiprobe mtc" };
        mtcThread.Start();
        return new() { ["start"] = startAt, ["seconds"] = seconds, ["rate"] = rate.ToString() };
    }

    JsonObject StopMtc() {
        StopMtcThread();
        return [];
    }

    void MtcLoop(MtcEncoder encoder, double fromSeconds, long startNs) {
        Span<SyncMidiEvent> events = stackalloc SyncMidiEvent[64];
        var window = 10_000_000L; // 10 ms of timecode at a time, as microsecond "frames"
        var cursor = startNs;
        while (mtcRunning) {
            if (cursor > Now() + LookaheadNs) {
                Thread.Sleep(2);
                continue;
            }

            var a = fromSeconds + (cursor - startNs) / 1e9;
            var b = a + window / 1e9;
            var count = encoder.Process(true, a, b, (int)(window / 1000), events);
            for (var i = 0; i < count; i++) {
                var e = events[i];
                try {
                    SendBytes([e.Status, e.Data1], cursor + e.Frame * 1000L);
                } catch (InvalidOperationException) {
                    return;
                }
            }

            cursor += window;
        }
    }

    void StopMtcThread() {
        mtcRunning = false;
        mtcThread?.Join();
        mtcThread = null;
    }

    static double ParseLabel(string label, EngineMtcRate rate) {
        var parts = label.Split(':', ';', '.');
        if (parts.Length != 4) {
            throw new FormatException("a timecode is hh:mm:ss:ff");
        }

        var n = parts.Select(static p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var tc = new TimecodeLabel(n[0], n[1], n[2], n[3]);
        return Timecode.SecondsOf(tc, rate);
    }
}
