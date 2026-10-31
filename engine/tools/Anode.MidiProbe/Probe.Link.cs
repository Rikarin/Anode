using Anode.Audio;
using Anode.Audio.Native;
using System.Text.Json.Nodes;

namespace Anode.MidiProbe;

// The probe as a Link peer, on the app thread's session state (it has no audio thread).
public sealed partial class Probe {
    NativeLink? link;
    long linkOffsetMicros;

    /// <summary>Link's time for host time <paramref name="hostNs" /> (the cth_midi_now_ns clock).</summary>
    long LinkTime(long hostNs) => hostNs / 1000 + linkOffsetMicros;

    /// <summary>
    ///     <c>linkJoin</c>: joins the session (start/stop sync with <c>startStop</c>), waits up to <c>timeoutMs</c> (5000)
    ///     for <c>peers</c> (1) other peers, then sets the session's tempo to <c>bpm</c> when given (a peer that joins
    ///     takes the session's tempo, so the probe sets its own once it is in).
    /// </summary>
    JsonObject LinkJoin(JsonObject request) {
        if (!NativeLink.IsAvailable) {
            throw new InvalidOperationException("an_link is not built (engine/native/build.sh)");
        }

        var bpm = request["bpm"] is not null ? Double(request, "bpm", 120) : (double?)null;
        if (link is null) {
            link = new(bpm ?? 120);
            linkOffsetMicros = link.ClockMicros - Realtime.NowNanoseconds() / 1000;
        }

        link.StartStopSync = request["startStop"]?.GetValue<bool>() == true;
        link.Enabled = true;
        var wanted = Int(request, "peers", 1);
        var deadline = Environment.TickCount64 + Int(request, "timeoutMs", 5000);
        while (link.Peers < wanted && Environment.TickCount64 < deadline) {
            Thread.Sleep(20);
        }

        if (bpm is { } tempo) {
            SetLinkTempo(tempo);
        }

        return LinkSummary();
    }

    NativeLink Peer => link ?? throw new InvalidOperationException("not in a Link session: linkJoin first");

    void SetLinkTempo(double bpm) {
        lock (Peer.AppGate) {
            Peer.CaptureApp();
            NativeLink.SetTempo(Peer.AppState, bpm, Peer.ClockMicros);
            Peer.CommitApp();
        }
    }

    /// <summary><c>linkTempo</c>: the session's tempo to <c>bpm</c>.</summary>
    JsonObject LinkTempo(JsonObject request) {
        SetLinkTempo(Double(request, "bpm", 120));
        return LinkSummary();
    }

    /// <summary><c>linkPlay</c>: start (<c>playing</c> true) or stop the session's transport (start/stop sync).</summary>
    JsonObject LinkPlay(JsonObject request) {
        var playing = request["playing"]?.GetValue<bool>() != false;
        var quantum = Double(request, "quantum", 4);
        lock (Peer.AppGate) {
            Peer.CaptureApp();
            var now = Peer.ClockMicros;
            if (playing) {
                NativeLink.SetIsPlayingAndRequestBeatAtTime(Peer.AppState, true, now, 0, quantum);
            } else {
                NativeLink.SetIsPlaying(Peer.AppState, false, now);
            }

            Peer.CommitApp();
        }

        return LinkSummary();
    }

    /// <summary>
    ///     <c>linkState</c>: the session's tempo, peers and playing, and at each host time in <c>at</c> (ns; default
    ///     now) the beat and the phase at <c>quantum</c> (4).
    /// </summary>
    JsonObject LinkState(JsonObject request) {
        var quantum = Double(request, "quantum", 4);
        var times = request["at"] is JsonArray array ? [..array.Select(static t => t!.GetValue<long>())] : new[] { Now() };
        var beats = new JsonArray();
        var summary = LinkSummary();
        lock (Peer.AppGate) {
            Peer.CaptureApp();
            foreach (var t in times) {
                var time = LinkTime(t);
                beats.Add(new JsonObject {
                    ["at"] = t,
                    ["beat"] = NativeLink.BeatAtTime(Peer.AppState, time, quantum),
                    ["phase"] = NativeLink.PhaseAtTime(Peer.AppState, time, quantum)
                });
            }
        }

        summary["beats"] = beats;
        return summary;
    }

    /// <summary>
    ///     <c>linkClockError</c>: how far the MIDI Clock received (Anode's, while it follows Link) is from the session's
    ///     beat: each clock's beat as the clock counts it (from Start, or a Song Position Pointer and Continue) against
    ///     Link's beat at its timestamp, at <c>quantum</c> (4), in milliseconds. Uses the clocks received since
    ///     <c>since</c>, skipping the first <c>skip</c> (24).
    /// </summary>
    JsonObject LinkClockError(JsonObject request) {
        var quantum = Double(request, "quantum", 4);
        var skip = Int(request, "skip", 24);
        var messages = Snapshot(request);
        var errors = new List<double>();
        double beat = double.NaN, pointer = 0;
        var seen = 0;
        lock (Peer.AppGate) {
            Peer.CaptureApp();
            var tempo = NativeLink.Tempo(Peer.AppState);
            foreach (var m in messages) {
                switch (m.Data[0]) {
                    case 0xFA:
                        beat = 0;
                        break;
                    case 0xF2 when m.Data.Length >= 3:
                        pointer = ((m.Data[2] << 7) | m.Data[1]) / 4.0;
                        break;
                    case 0xFB:
                        beat = pointer;
                        break;
                    case 0xFC:
                        beat = double.NaN;
                        break;
                    case 0xF8 when !double.IsNaN(beat):
                        if (seen++ >= skip) {
                            var phase = NativeLink.PhaseAtTime(Peer.AppState, LinkTime(m.TimeNs), quantum);
                            var mine = beat % quantum;
                            var delta = (phase - mine + quantum * 1.5) % quantum - quantum / 2;
                            errors.Add(delta * 60_000 / tempo);
                        }

                        beat += 1.0 / 24;
                        break;
                }
            }
        }

        var summary = LinkSummary();
        summary["clocks"] = errors.Count;
        summary["maxAbsMs"] = errors.Count > 0 ? errors.Max(Math.Abs) : null;
        summary["meanMs"] = errors.Count > 0 ? errors.Average() : null;
        return summary;
    }

    /// <summary><c>linkLeave</c>: leaves the session.</summary>
    JsonObject LinkLeave() {
        link?.Dispose();
        link = null;
        return [];
    }

    JsonObject LinkSummary() {
        var peer = Peer;
        lock (peer.AppGate) {
            peer.CaptureApp();
            return new() {
                ["tempo"] = NativeLink.Tempo(peer.AppState),
                ["peers"] = peer.Peers,
                ["playing"] = NativeLink.IsPlaying(peer.AppState),
                ["now"] = Now()
            };
        }
    }
}
