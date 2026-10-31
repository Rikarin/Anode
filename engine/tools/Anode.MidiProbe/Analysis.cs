using Anode.Engine.Sync;
using Anode.Ipc;
using System.Text.Json.Nodes;

namespace Anode.MidiProbe;

/// <summary>What the received MIDI Clock and MTC say: the analyses the golden path asserts on.</summary>
public static class Analysis {
    /// <summary>
    ///     <c>analyzeClock</c>: the clocks' spacing (count, mean interval and its tempo, the largest deviation of an
    ///     interval from the mean, and the standard deviation, in ms) over the clocks received since <c>since</c>, and
    ///     the transport messages among them (Start, Continue, Stop, Song Position Pointer) in order, with their times.
    ///     Intervals across a Stop or a Start are left out.
    /// </summary>
    public static JsonObject Clock(IReadOnlyList<Received> messages) {
        var intervals = new List<double>();
        var transport = new JsonArray();
        long last = 0;
        foreach (var m in messages) {
            switch (m.Data[0]) {
                case 0xF8:
                    if (last != 0) {
                        intervals.Add((m.TimeNs - last) / 1e6);
                    }

                    last = m.TimeNs;
                    break;
                case 0xFA or 0xFB or 0xFC:
                    transport.Add(new JsonObject {
                        ["t"] = m.TimeNs,
                        ["kind"] = m.Data[0] switch { 0xFA => "start", 0xFB => "continue", _ => "stop" }
                    });
                    last = 0;
                    break;
                case 0xF2 when m.Data.Length >= 3:
                    transport.Add(new JsonObject {
                        ["t"] = m.TimeNs,
                        ["kind"] = "songPosition",
                        ["sixteenths"] = (m.Data[2] << 7) | m.Data[1]
                    });
                    break;
            }
        }

        var result = new JsonObject { ["clocks"] = intervals.Count + (last != 0 || intervals.Count > 0 ? 1 : 0), ["transport"] = transport };
        if (intervals.Count > 0) {
            var mean = intervals.Average();
            result["meanMs"] = mean;
            result["bpm"] = 60_000 / (mean * 24);
            result["maxDeviationMs"] = intervals.Max(i => Math.Abs(i - mean));
            result["stdDevMs"] = Math.Sqrt(intervals.Average(i => (i - mean) * (i - mean)));
            result["minMs"] = intervals.Min();
            result["maxMs"] = intervals.Max();
        }

        return result;
    }

    /// <summary>
    ///     <c>analyzeMtc</c>: the positions the received MTC decodes to (Anode's MTC decoder, at <c>rate</c> unless the
    ///     timecode says otherwise): each full frame and each completed set of quarter frames, with its time, seconds
    ///     and label; and the last.
    /// </summary>
    public static JsonObject Mtc(IReadOnlyList<Received> messages, EngineMtcRate rate) {
        var decoder = new MtcDecoder(rate);
        var positions = new JsonArray();
        JsonObject? last = null;
        foreach (var m in messages) {
            if (decoder.Receive(m.Data, m.TimeNs) is not { } position) {
                continue;
            }

            var label = Timecode.Label(Timecode.CountAt(position.Seconds, decoder.Rate), decoder.Rate);
            last = new JsonObject {
                ["t"] = position.TimeNs,
                ["seconds"] = position.Seconds,
                ["label"] = $"{label.Hours:00}:{label.Minutes:00}:{label.Seconds:00}:{label.Frames:00}",
                ["fullFrame"] = position.FullFrame
            };
            positions.Add(last);
        }

        // The quarter frames' positions against their times: the largest drift from the first, in frames.
        var quarters = positions.OfType<JsonObject>().Where(static p => p["fullFrame"]!.GetValue<bool>() == false).ToList();
        double? drift = null;
        if (quarters.Count > 1) {
            var t0 = quarters[0]["t"]!.GetValue<long>();
            var s0 = quarters[0]["seconds"]!.GetValue<double>();
            drift = quarters.Max(p => Math.Abs(p["seconds"]!.GetValue<double>() - s0 - (p["t"]!.GetValue<long>() - t0) / 1e9))
                    * Timecode.FramesPerSecond(decoder.Rate);
        }

        return new() {
            ["rate"] = decoder.Rate.ToString(),
            ["count"] = positions.Count,
            ["positions"] = positions,
            ["last"] = last?.DeepClone(),
            ["maxDriftFrames"] = drift
        };
    }
}
