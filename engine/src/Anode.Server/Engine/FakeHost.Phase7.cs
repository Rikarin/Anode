using Anode.Ipc;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Anode.Server.Engine;

/// <summary>
///     Phase 7 (docs/08-phase-7.md): the fake host keeps Scripter scripts (answering each <see cref="SetScriptMessage" />
///     with a <see cref="SlotParametersMessage" /> of the script's <c>PluginParameters</c>, read from a plain literal;
///     each parameter's id is its name), whether MIDI step input is forwarded, and the range the preview channel plays;
///     <see cref="StepAsync" /> and <see cref="TraceAsync" /> play the keyboard and a script for tests.
/// </summary>
public sealed partial class FakeHost {
    readonly Dictionary<(int Track, string Slot), string> scripts = [];
    bool stepInputForward;
    PreviewRangeMessage? previewRange;

    /// <summary>The fake Scripter in <see cref="Catalogue" /> while the built-in one is not there.</summary>
    public static EnginePlugin FakeScripter =>
        new(Anode.Model.Editing.Edits.ScripterId, "Scripter", "Anode", "0", "builtin", false, [], [], "MIDI Effect");

    /// <summary>The scripts the host was given, by engine track and MIDI FX slot.</summary>
    public IReadOnlyDictionary<(int Track, string Slot), string> Scripts {
        get {
            lock (gate) {
                return new Dictionary<(int Track, string Slot), string>(scripts);
            }
        }
    }

    /// <summary>Whether the host forwards MIDI note input as steps (<see cref="SetStepInputForwardMessage" />).</summary>
    public bool StepInputForward {
        get {
            lock (gate) {
                return stepInputForward;
            }
        }
    }

    /// <summary>The range the preview channel plays, or null.</summary>
    public PreviewRangeMessage? PreviewRange {
        get {
            lock (gate) {
                return previewRange;
            }
        }
    }

    /// <summary>Plays the MIDI keyboard entering a step (as forwarding sends it: the keys, the highest velocity).</summary>
    public Task StepAsync(IReadOnlyList<int> pitches, int velocity) =>
        SendAsync(new StepInputMidiMessage(pitches, velocity));

    /// <summary>Plays a script's <c>Trace</c> output.</summary>
    public Task TraceAsync(int trackId, string slotId, IReadOnlyList<string> lines, int dropped = 0) =>
        SendAsync(new ScriptLogMessage(trackId, slotId, lines, dropped));

    /// <summary>The Phase 7 messages; null for any other message, else what it answers (possibly nothing).</summary>
    IpcMessage[]? HandlePhase7(IpcMessage message) {
        switch (message) {
            case SetScriptMessage set:
                lock (gate) {
                    // As the host: only a Scripter slot takes a script.
                    if (!IsScripter(set.TrackId, set.SlotId)) {
                        return [new ErrorMessage($"MIDI FX slot '{set.SlotId}' is not a Scripter.", "setScript")];
                    }

                    scripts[(set.TrackId, set.SlotId)] = set.Source ?? string.Empty;
                }

                return [
                    new SlotParametersMessage(set.TrackId, set.SlotId, ScriptParameters(set.Source ?? string.Empty))
                ];
            case SetStepInputForwardMessage forward:
                lock (gate) {
                    stepInputForward = forward.On;
                }

                return [];
            case SetChannelMessage channel:
                // A Scripter slot whose source is new compiles it (the mixer keeps the strip itself).
                return [..Compiled(channel.TrackId, channel.Strip)];
            case PreviewRangeMessage range:
                if (range.EndFrame <= range.StartFrame || range.StartFrame < 0 || !File.Exists(range.Path)) {
                    return [new ErrorMessage("An empty range, or a file that cannot be read.", "previewRange")];
                }

                lock (gate) {
                    previewRange = range;
                    preview = range.Path;
                }

                return [];
            default:
                return null;
        }
    }

    bool IsScripter(int trackId, string slotId) =>
        tracks.TryGetValue(trackId, out var t)
        && (t.Strip.MidiEffects ?? []).Any(i => i.Id == slotId && i.PluginId == Anode.Model.Editing.Edits.ScripterId);

    /// <summary>The parameters of each Scripter slot of a strip whose script is new to this host.</summary>
    List<IpcMessage> Compiled(int trackId, EngineStrip strip) {
        var replies = new List<IpcMessage>();
        lock (gate) {
            foreach (var slot in strip.MidiEffects ?? []) {
                if (slot is { PluginId: Anode.Model.Editing.Edits.ScripterId, Source: { } source }
                    && (!scripts.TryGetValue((trackId, slot.Id), out var old) || old != source)) {
                    scripts[(trackId, slot.Id)] = source;
                    replies.Add(new SlotParametersMessage(trackId, slot.Id, ScriptParameters(source)));
                }
            }
        }

        return replies;
    }

    /// <summary>
    ///     A script's parameters from a plain
    ///     <c>
    ///         var PluginParameters = [{ name: "…", type: "lin", minValue: …,
    ///         maxValue: …, defaultValue: …, numberOfSteps: …, valueStrings: ["…"] }, …]
    ///     </c>
    ///     literal: enough for tests.
    /// </summary>
    public static IReadOnlyList<EngineParameter> ScriptParameters(string source) {
        var start = source.IndexOf("PluginParameters", StringComparison.Ordinal);
        var open = start < 0 ? -1 : source.IndexOf('[', start);
        if (open < 0) {
            return [];
        }

        var parameters = new List<EngineParameter>();
        var depth = 0;
        var close = open;
        for (; close < source.Length; close++) {
            depth += source[close] switch {
                '[' => 1,
                ']' => -1,
                _ => 0
            };
            if (depth == 0) {
                break;
            }
        }

        foreach (Match entry in ObjectLiteral().Matches(source[open..Math.Min(close, source.Length)])) {
            var body = entry.Groups[1].Value;
            if (Text(body, "name") is not { } name) {
                continue;
            }

            var names = ValueStrings(body);
            var type = Text(body, "type") ?? "lin";
            var (min, max, steps) = type switch {
                "menu" => (0.0, Math.Max(0, names.Count - 1), Math.Max(1, names.Count - 1)),
                "checkbox" or "momentary" => (0.0, 1.0, 1),
                _ => (Number(body, "minValue") ?? 0, Number(body, "maxValue") ?? 1,
                    (int)(Number(body, "numberOfSteps") ?? 0))
            };
            parameters.Add(
                new(
                    name,
                    name,
                    min,
                    max,
                    Math.Clamp(Number(body, "defaultValue") ?? min, min, Math.Max(min, max)),
                    Text(body, "unit") ?? string.Empty,
                    steps,
                    names.Count > 0 ? names : null
                )
            );
        }

        return parameters;
    }

    static string? Text(string body, string key) =>
        Regex.Match(body, $@"\b{key}\s*:\s*[""']([^""']*)[""']") is { Success: true } m ? m.Groups[1].Value : null;

    static double? Number(string body, string key) =>
        Regex.Match(body, $@"\b{key}\s*:\s*(-?[0-9.]+)") is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    static List<string> ValueStrings(string body) =>
        Regex.Match(body, @"\bvalueStrings\s*:\s*\[([^\]]*)\]") is { Success: true } m
            ? [..Regex.Matches(m.Groups[1].Value, """["']([^"']*)["']""").Select(static x => x.Groups[1].Value)]
            : [];

    [GeneratedRegex(@"\{((?:[^{}\[\]]|\[[^\]]*\])*)\}")]
    private static partial Regex ObjectLiteral();
}
