using System.Globalization;

namespace Anode.Dsp.Instruments.Sampler;

/// <summary>How a region's sample plays (SFZ <c>loop_mode</c>).</summary>
public enum LoopMode {
    /// <summary>Plays to its end or until its release ends.</summary>
    NoLoop,

    /// <summary>Plays to its end whatever the key does.</summary>
    OneShot,

    /// <summary>Loops until its release ends.</summary>
    Continuous,

    /// <summary>Loops while the key (or the sustain pedal) holds it, then plays on to its end.</summary>
    Sustain
}

/// <summary>
///     One SFZ <c>&lt;region&gt;</c> with what it inherited from its <c>&lt;global&gt;</c>, <c>&lt;master&gt;</c> and
///     <c>&lt;group&gt;</c>. Defaults are SFZ's. Times are in seconds, levels in dB, <see cref="Sustain" /> and
///     <see cref="AmpVeltrack" /> in percent, <see cref="Tune" /> in cents.
/// </summary>
public sealed record SfzRegion {
    /// <summary>The sample's full path.</summary>
    public string Sample { get; init; } = string.Empty;

    public int LoKey { get; init; }
    public int HiKey { get; init; } = 127;
    public int KeyCenter { get; init; } = 60;
    public int LoVel { get; init; } = 1;
    public int HiVel { get; init; } = 127;
    public double Tune { get; init; }
    public int Transpose { get; init; }
    public double Volume { get; init; }
    public double AmpVeltrack { get; init; } = 100;
    public double Pan { get; init; }
    public long Offset { get; init; }

    /// <summary>The loop mode, or null for the default (a loop when the sample has one, else none).</summary>
    public LoopMode? LoopMode { get; init; }

    /// <summary>The loop's first and last frame, or −1 for the sample's own.</summary>
    public long LoopStart { get; init; } = -1;

    public long LoopEnd { get; init; } = -1;
    public double Attack { get; init; }
    public double Hold { get; init; }
    public double Decay { get; init; }
    public double Sustain { get; init; } = 100;
    public double Release { get; init; } = 0.001;
    public int SeqLength { get; init; } = 1;
    public int SeqPosition { get; init; } = 1;

    /// <summary>The keyswitch range (<c>sw_lokey</c> … <c>sw_hikey</c>), or −1: notes in it switch and do not sound.</summary>
    public int SwLoKey { get; init; } = -1;

    public int SwHiKey { get; init; } = -1;

    /// <summary>The keyswitch this region plays under (<c>sw_last</c>), or −1 for every one.</summary>
    public int SwLast { get; init; } = -1;

    /// <summary>The keyswitch in force before any is played (<c>sw_default</c>), or −1.</summary>
    public int SwDefault { get; init; } = -1;

    /// <summary>The name of the region's keyswitch (<c>sw_label</c>), or null.</summary>
    public string? SwLabel { get; init; }

    public bool Matches(int note, int velocity) =>
        note >= LoKey && note <= HiKey && velocity >= LoVel && velocity <= HiVel;
}

/// <summary>
///     Parses the SFZ subset Anode's sampler plays: the headers <c>&lt;control&gt;</c> (<c>default_path</c>),
///     <c>&lt;global&gt;</c>, <c>&lt;master&gt;</c>, <c>&lt;group&gt;</c> and <c>&lt;region&gt;</c>, and the opcodes of
///     <see cref="SfzRegion" />, keyswitches among them (<c>sw_lokey</c>, <c>sw_hikey</c>, <c>sw_last</c>,
///     <c>sw_default</c> and <c>sw_label</c>; the range and default also under <c>&lt;control&gt;</c>). Keys are numbers
///     or note names (<c>c4</c> is 60, <c>c#4</c> and <c>db4</c> 61).
///     Comments (<c>//</c> and <c>/* */</c>) and preprocessor lines (<c>#define</c>, <c>#include</c>) are skipped, as
///     are other headers and opcodes. A value runs to the next opcode, header or line end, so sample paths may contain
///     spaces.
/// </summary>
public static class SfzParser {
    enum Level {
        None,
        Control,
        Global,
        Master,
        Group,
        Region
    }

    /// <summary>The regions of an SFZ file at <paramref name="path" />, samples resolved against its folder.</summary>
    public static IReadOnlyList<SfzRegion> ParseFile(string path) =>
        Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

    /// <summary>The regions of SFZ <paramref name="text" />, samples resolved against <paramref name="folder" />.</summary>
    public static IReadOnlyList<SfzRegion> Parse(string text, string folder) {
        var regions = new List<SfzRegion>();
        var global = new List<(string, string)>();
        var master = new List<(string, string)>();
        var group = new List<(string, string)>();
        var region = new List<(string, string)>();
        var control = new List<(string, string)>();
        var defaultPath = string.Empty;
        var level = Level.None;

        void Close() {
            if (level == Level.Region) {
                regions.Add(Build(folder, defaultPath, control, global, master, group, region));
            }
        }

        foreach (var (kind, name, value) in Tokens(StripComments(text))) {
            if (kind == Token.Header) {
                Close();
                level = name switch {
                    "control" => Level.Control,
                    "global" => Level.Global,
                    "master" => Level.Master,
                    "group" => Level.Group,
                    "region" => Level.Region,
                    _ => Level.None
                };
                switch (level) {
                    case Level.Global:
                        global.Clear();
                        master.Clear();
                        group.Clear();
                        break;
                    case Level.Master:
                        master.Clear();
                        group.Clear();
                        break;
                    case Level.Group:
                        group.Clear();
                        break;
                    default: // a region, the control header or one this subset ignores clears nothing above it
                        break;
                }

                region.Clear();
                continue;
            }

            switch (level) {
                case Level.Control when name == "default_path":
                    defaultPath = value;
                    break;
                case Level.Control when name is "sw_lokey" or "sw_hikey" or "sw_default":
                    control.Add((name, value));
                    break;
                case Level.Global:
                    global.Add((name, value));
                    break;
                case Level.Master:
                    master.Add((name, value));
                    break;
                case Level.Group:
                    group.Add((name, value));
                    break;
                case Level.Region:
                    region.Add((name, value));
                    break;
                default: // other control opcodes, and the opcodes of ignored headers
                    break;
            }
        }

        Close();
        return regions;
    }

    /// <summary>A MIDI note from a number or a note name (<c>c4</c> = 60, <c>c-1</c> = 0), or null.</summary>
    public static int? ParseNote(string text) {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) {
            return number is >= 0 and <= 127 ? number : null;
        }

        if (text.Length < 2) {
            return null;
        }

        int? semitone = char.ToLowerInvariant(text[0]) switch {
            'c' => 0,
            'd' => 2,
            'e' => 4,
            'f' => 5,
            'g' => 7,
            'a' => 9,
            'b' => 11,
            _ => null
        };
        if (semitone is null) {
            return null;
        }

        var at = 1;
        var s = semitone.Value;
        if (text[at] == '#') {
            s++;
            at++;
        } else if (text[at] == 'b' && at + 1 < text.Length) {
            s--;
            at++;
        }

        if (!int.TryParse(
                text.AsSpan(at),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var octave
            )) {
            return null;
        }

        var note = (octave + 1) * 12 + s;
        return note is >= 0 and <= 127 ? note : null;
    }

    static SfzRegion Build(string folder, string defaultPath, params List<(string Name, string Value)>[] levels) {
        var r = new SfzRegion();
        foreach (var opcodes in levels) {
            foreach (var (name, value) in opcodes) {
                r = Apply(r, name, value, folder, defaultPath);
            }
        }

        return r;
    }

    static SfzRegion Apply(SfzRegion r, string name, string value, string folder, string defaultPath) =>
        name switch {
            "sample" => r with { Sample = ResolveSample(folder, defaultPath, value) },
            "key" when ParseNote(value) is { } k => r with { LoKey = k, HiKey = k, KeyCenter = k },
            "lokey" when ParseNote(value) is { } k => r with { LoKey = k },
            "hikey" when ParseNote(value) is { } k => r with { HiKey = k },
            "pitch_keycenter" when ParseNote(value) is { } k => r with { KeyCenter = k },
            "lovel" => r with { LoVel = Int(value, r.LoVel) },
            "hivel" => r with { HiVel = Int(value, r.HiVel) },
            "tune" => r with { Tune = Number(value, r.Tune) },
            "transpose" => r with { Transpose = Int(value, r.Transpose) },
            "volume" => r with { Volume = Number(value, r.Volume) },
            "amp_veltrack" => r with { AmpVeltrack = Number(value, r.AmpVeltrack) },
            "pan" => r with { Pan = Math.Clamp(Number(value, r.Pan), -100, 100) },
            "offset" => r with { Offset = Math.Max(0, Long(value, r.Offset)) },
            "loop_mode" => r with {
                LoopMode = value switch {
                    "no_loop" => LoopMode.NoLoop,
                    "one_shot" => LoopMode.OneShot,
                    "loop_continuous" => LoopMode.Continuous,
                    "loop_sustain" => LoopMode.Sustain,
                    _ => r.LoopMode
                }
            },
            "loop_start" or "loopstart" => r with { LoopStart = Long(value, r.LoopStart) },
            "loop_end" or "loopend" => r with { LoopEnd = Long(value, r.LoopEnd) },
            "ampeg_attack" => r with { Attack = Math.Max(0, Number(value, r.Attack)) },
            "ampeg_hold" => r with { Hold = Math.Max(0, Number(value, r.Hold)) },
            "ampeg_decay" => r with { Decay = Math.Max(0, Number(value, r.Decay)) },
            "ampeg_sustain" => r with { Sustain = Math.Clamp(Number(value, r.Sustain), 0, 100) },
            "ampeg_release" => r with { Release = Math.Max(0, Number(value, r.Release)) },
            "seq_length" => r with { SeqLength = Math.Max(1, Int(value, r.SeqLength)) },
            "seq_position" => r with { SeqPosition = Math.Max(1, Int(value, r.SeqPosition)) },
            "sw_lokey" when ParseNote(value) is { } k => r with { SwLoKey = k },
            "sw_hikey" when ParseNote(value) is { } k => r with { SwHiKey = k },
            "sw_last" when ParseNote(value) is { } k => r with { SwLast = k },
            "sw_default" when ParseNote(value) is { } k => r with { SwDefault = k },
            "sw_label" => r with { SwLabel = value },
            _ => r
        };

    static string ResolveSample(string folder, string defaultPath, string sample) {
        var relative = (defaultPath + sample).Replace('\\', '/');
        return Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(folder, relative));
    }

    static double Number(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : fallback;

    static int Int(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : (int)Math.Round(Number(value, fallback));

    static long Long(string value, long fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    enum Token {
        Header,
        Opcode
    }

    /// <summary>The text with <c>//</c> and <c>/* */</c> comments and <c>#</c> lines blanked out.</summary>
    static string StripComments(string text) {
        var result = new System.Text.StringBuilder(text.Length);
        var lineStart = true;
        for (var i = 0; i < text.Length; i++) {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') {
                while (i < text.Length && text[i] != '\n') {
                    i++;
                }

                result.Append('\n');
                lineStart = true;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                result.Append(' ');
                continue;
            }

            if (lineStart && text[i] == '#') {
                while (i < text.Length && text[i] != '\n') {
                    i++;
                }

                result.Append('\n');
                continue;
            }

            if (text[i] == '\n') {
                lineStart = true;
            } else if (!char.IsWhiteSpace(text[i])) {
                lineStart = false;
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }

    static IEnumerable<(Token Kind, string Name, string Value)> Tokens(string text) {
        foreach (var line in text.Split('\n')) {
            var i = 0;
            while (i < line.Length) {
                if (char.IsWhiteSpace(line[i])) {
                    i++;
                    continue;
                }

                if (line[i] == '<') {
                    var close = line.IndexOf('>', i);
                    if (close < 0) {
                        break;
                    }

                    yield return (Token.Header, line[(i + 1)..close].Trim().ToLowerInvariant(), string.Empty);
                    i = close + 1;
                    continue;
                }

                var nameEnd = i;
                while (nameEnd < line.Length && IsNameChar(line[nameEnd])) {
                    nameEnd++;
                }

                if (nameEnd == i || nameEnd >= line.Length || line[nameEnd] != '=') {
                    // Not an opcode: skip the word.
                    while (i < line.Length && !char.IsWhiteSpace(line[i])) {
                        i++;
                    }

                    continue;
                }

                var valueStart = nameEnd + 1;
                var valueEnd = ValueEnd(line, valueStart);
                yield return (Token.Opcode, line[i..nameEnd], line[valueStart..valueEnd].Trim());
                i = valueEnd;
            }
        }
    }

    /// <summary>Where a value ends: at the next header, the next <c>name=</c> after whitespace, or the line's end.</summary>
    static int ValueEnd(string line, int start) {
        for (var j = start; j < line.Length; j++) {
            if (line[j] == '<') {
                return j;
            }

            if (j > start && char.IsWhiteSpace(line[j - 1]) && IsNameChar(line[j])) {
                var k = j;
                while (k < line.Length && IsNameChar(line[k])) {
                    k++;
                }

                if (k < line.Length && line[k] == '=') {
                    return j;
                }
            }
        }

        return line.Length;
    }

    static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
