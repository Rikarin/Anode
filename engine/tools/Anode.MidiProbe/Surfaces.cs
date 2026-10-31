using System.Text;
using System.Text.Json.Nodes;

namespace Anode.MidiProbe;

// The probe as a control surface: Mackie Control and HUI moves as their bytes, and a reading of what the host sent a
// Mackie Control (faders, scribble strips, LEDs, the displays, meters). The bytes follow the protocols as Logic's
// surfaces use them (docs/control/README.md has Anode's side).
public sealed partial class Probe {
    /// <summary>Mackie Control's button notes, by name (the strip buttons add the strip, 0–7).</summary>
    static readonly Dictionary<string, int> McuButtons = new(StringComparer.OrdinalIgnoreCase) {
        ["recArm"] = 0x00,
        ["solo"] = 0x08,
        ["mute"] = 0x10,
        ["select"] = 0x18,
        ["vpotPush"] = 0x20,
        ["assignTrack"] = 0x28,
        ["assignSend"] = 0x29,
        ["assignPan"] = 0x2A,
        ["assignPlugin"] = 0x2B,
        ["assignEq"] = 0x2C,
        ["assignInstrument"] = 0x2D,
        ["bankLeft"] = 0x2E,
        ["bankRight"] = 0x2F,
        ["channelLeft"] = 0x30,
        ["channelRight"] = 0x31,
        ["flip"] = 0x32,
        ["global"] = 0x33,
        ["nameValue"] = 0x34,
        ["smpteBeats"] = 0x35,
        ["f1"] = 0x36,
        ["f2"] = 0x37,
        ["f3"] = 0x38,
        ["f4"] = 0x39,
        ["f5"] = 0x3A,
        ["f6"] = 0x3B,
        ["f7"] = 0x3C,
        ["f8"] = 0x3D,
        ["read"] = 0x4A,
        ["write"] = 0x4B,
        ["trim"] = 0x4C,
        ["touch"] = 0x4D,
        ["latch"] = 0x4E,
        ["group"] = 0x4F,
        ["save"] = 0x50,
        ["undo"] = 0x51,
        ["cancel"] = 0x52,
        ["enter"] = 0x53,
        ["marker"] = 0x54,
        ["nudge"] = 0x55,
        ["cycle"] = 0x56,
        ["drop"] = 0x57,
        ["replace"] = 0x58,
        ["click"] = 0x59,
        ["soloGlobal"] = 0x5A,
        ["rewind"] = 0x5B,
        ["forward"] = 0x5C,
        ["stop"] = 0x5D,
        ["play"] = 0x5E,
        ["record"] = 0x5F,
        ["up"] = 0x60,
        ["down"] = 0x61,
        ["left"] = 0x62,
        ["right"] = 0x63,
        ["zoom"] = 0x64,
        ["scrub"] = 0x65,
        ["faderTouch"] = 0x68
    };

    /// <summary>
    ///     <c>mcuFader</c>: fader <c>strip</c> (0–7) to <c>value</c> (0…1) as 14-bit pitch bend on its channel, inside a
    ///     touch (note 0x68 + strip) unless <c>touch</c> is false; <c>release</c> (true) lets go after it.
    /// </summary>
    JsonObject McuFader(JsonObject request) {
        var strip = Math.Clamp(Int(request, "strip", 0), 0, 8);
        var value = (int)Math.Round(Math.Clamp(Double(request, "value", 0), 0, 1) * 16383);
        var touch = request["touch"]?.GetValue<bool>() != false;
        var bytes = new List<byte>();
        if (touch) {
            bytes.AddRange([0x90, (byte)(0x68 + strip), 0x7F]);
        }

        bytes.AddRange([(byte)(0xE0 | strip), (byte)(value & 0x7F), (byte)(value >> 7)]);
        if (touch && request["release"]?.GetValue<bool>() != false) {
            bytes.AddRange([0x90, (byte)(0x68 + strip), 0x00]);
        }

        SendBytes(bytes.ToArray(), 0);
        return new() { ["hex"] = Hex([..bytes]) };
    }

    /// <summary>
    ///     <c>mcuButton</c>: a press and release of <c>button</c> (a name from the table, plus <c>strip</c> for the strip
    ///     buttons) or <c>note</c>; <c>press</c> or <c>release</c> alone with <c>action</c>.
    /// </summary>
    JsonObject McuButton(JsonObject request) {
        var note = request["note"] is not null ? Int(request, "note", 0) : ButtonNote(request);
        var action = Text(request, "action") ?? "click";
        byte[] bytes = action switch {
            "press" => [0x90, (byte)note, 0x7F],
            "release" => [0x90, (byte)note, 0x00],
            _ => [0x90, (byte)note, 0x7F, 0x90, (byte)note, 0x00]
        };
        SendBytes(bytes, 0);
        return new() { ["hex"] = Hex(bytes) };
    }

    /// <summary>A named button's note: the strip buttons (and the fader touches) add the strip.</summary>
    static int ButtonNote(JsonObject request) {
        if (!McuButtons.TryGetValue(Text(request, "button") ?? string.Empty, out var note)) {
            throw new ArgumentException($"no Mackie Control button '{Text(request, "button")}'");
        }

        return note < 0x28 || note == 0x68 ? note + Int(request, "strip", 0) : note;
    }

    /// <summary><c>mcuVpot</c>: VPot <c>strip</c> turned by <c>ticks</c> (negative: counter-clockwise), CC 0x10 + strip.</summary>
    JsonObject McuVpot(JsonObject request) {
        var strip = Math.Clamp(Int(request, "strip", 0), 0, 7);
        var ticks = Math.Clamp(Int(request, "ticks", 1), -63, 63);
        var value = (byte)(ticks < 0 ? 0x40 | -ticks : ticks);
        byte[] bytes = [0xB0, (byte)(0x10 + strip), value];
        SendBytes(bytes, 0);
        return new() { ["hex"] = Hex(bytes) };
    }

    /// <summary>
    ///     <c>huiFader</c>: HUI fader <c>strip</c> to <c>value</c> (0…1): the zone select and touch (B0 0F strip,
    ///     B0 2F 40), the 14-bit value (CC strip, CC 0x20 + strip), and the release unless <c>release</c> is false.
    /// </summary>
    JsonObject HuiFader(JsonObject request) {
        var strip = Math.Clamp(Int(request, "strip", 0), 0, 7);
        var value = (int)Math.Round(Math.Clamp(Double(request, "value", 0), 0, 1) * 16383);
        var bytes = new List<byte> {
            0xB0,
            0x0F,
            (byte)strip,
            0xB0,
            0x2F,
            0x40
        };
        bytes.AddRange([0xB0, (byte)strip, (byte)(value >> 7), 0xB0, (byte)(0x20 + strip), (byte)(value & 0x7F)]);
        if (request["release"]?.GetValue<bool>() != false) {
            bytes.AddRange([0xB0, 0x0F, (byte)strip, 0xB0, 0x2F, 0x00]);
        }

        SendBytes(bytes.ToArray(), 0);
        return new() { ["hex"] = Hex([..bytes]) };
    }

    /// <summary><c>huiPing</c>: answers the host's HUI ping (90 00 00) with 90 00 7F while <c>on</c> (true).</summary>
    JsonObject HuiPing(JsonObject request) {
        huiPing = request["on"]?.GetValue<bool>() != false;
        return new() { ["on"] = huiPing };
    }
}

/// <summary>Reads what a host sent a Mackie Control surface.</summary>
public static class Surfaces {
    /// <summary>
    ///     <c>mcuState</c>: from the messages received (in order): the faders (pitch bend, 0…1), the scribble strip's
    ///     two lines (SysEx 12) and each strip's two 7-character cells, the LEDs that are on (note → velocity), the
    ///     timecode and assignment displays (CC 0x40–0x4B, digit 0 rightmost), and the last meter level per strip
    ///     (channel pressure, 0–12).
    /// </summary>
    public static JsonObject McuState(IReadOnlyList<Received> messages, bool extender) {
        var reading = new McuReading(extender ? 0x15 : 0x14);
        foreach (var m in messages) {
            reading.Apply(m.Data);
        }

        var (faders, strip, leds, display, meters) =
            (reading.Faders, reading.Strip, reading.Leds, reading.Display, reading.Meters);

        var text = new string(strip);
        var cells = new JsonArray();
        for (var s = 0; s < 8; s++) {
            cells.Add(
                new JsonObject {
                    ["top"] = text.Substring(s * 7, 7).TrimEnd(), ["bottom"] = text.Substring(56 + s * 7, 7).TrimEnd()
                }
            );
        }

        var digits = new StringBuilder();
        for (var i = 9; i >= 0; i--) {
            digits.Append(display[i]);
        }

        var leds2 = new JsonObject();
        foreach (var (note, velocity) in leds) {
            leds2[note.ToString(System.Globalization.CultureInfo.InvariantCulture)] = velocity;
        }

        return new() {
            ["faders"] = new JsonArray([..faders.Take(8).Select(static f => (JsonNode)Math.Round(f, 4))]),
            ["master"] = Math.Round(faders[8], 4),
            ["lines"] = new JsonArray(text[..56], text[56..]),
            ["strips"] = cells,
            ["leds"] = leds2,
            ["timecode"] = digits.ToString(),
            ["assignment"] = new string([display[11], display[10]]),
            ["meters"] = new JsonArray([..meters.Select(static v => (JsonNode)v)])
        };
    }

    /// <summary>What the messages so far have set on a Mackie Control.</summary>
    sealed class McuReading(int device) {
        public double[] Faders { get; } = new double[9];

        public char[] Strip { get; } = [..Enumerable.Repeat(' ', 112)];

        public SortedDictionary<int, int> Leds { get; } = [];

        public char[] Display { get; } = [..Enumerable.Repeat(' ', 12)];

        public int[] Meters { get; } = new int[8];

        public void Apply(byte[] d) {
            switch (d[0] & 0xF0) {
                case 0xE0 when d.Length >= 3:
                    Faders[d[0] & 0x0F] = ((d[2] << 7) | d[1]) / 16383.0;
                    break;
                case 0x90 when d.Length >= 3:
                    Led(d[1], d[2]);
                    break;
                case 0xB0 when d.Length >= 3 && d[1] is >= 0x40 and <= 0x4B:
                    Display[d[1] - 0x40] = Character(d[2]);
                    break;
                case 0xD0 when d.Length >= 2:
                    Meters[(d[1] >> 4) & 7] = d[1] & 0x0F;
                    break;
                case 0xF0 when d is [0xF0, 0x00, 0x00, 0x66, var id, 0x12, var offset, ..] && id == device:
                    for (var i = 7; i < d.Length - 1 && offset + i - 7 < Strip.Length; i++) {
                        Strip[offset + i - 7] = (char)d[i];
                    }

                    break;
            }
        }

        void Led(int note, int velocity) {
            if (velocity == 0) {
                Leds.Remove(note);
            } else {
                Leds[note] = velocity;
            }
        }
    }

    /// <summary>A 7-segment display character: 0x30–0x39 digits, 0x01–0x1A letters, 0x20 space; bit 6 the dot.</summary>
    static char Character(byte value) {
        var v = value & 0x3F;
        return v < 0x20 ? (char)(v + 0x40) : (char)v;
    }
}
