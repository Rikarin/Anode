using System.Text.Json.Nodes;

namespace Anode.Model.Storage;

/// <summary>
///     The migration steps after Phase 11 (<see cref="PackageMigrations" />); each leaves fields that are present
///     alone.
/// </summary>
static class PackageSteps {
    /// <summary>
    ///     Schema 11 to 12, everything a project gained after Phase 11. Each part keeps what a project did: alternatives
    ///     hold pattern regions, regions loop and stretch, the count-in has a length and a pre-roll, and strips have an
    ///     input stage, solo safe and a choice of meter.
    /// </summary>
    public static JsonObject ToSchema12(string kind, JsonObject json) {
        AlternativePatterns(kind, json);
        Loops(kind, json);
        Stretches(kind, json);
        CountInAndPreRoll(kind, json);
        Mixer(kind, json);
        return json;
    }

    /// <summary>
    ///     An older alternative holds no pattern regions: while it was put away, the track's pattern regions stayed live.
    /// </summary>
    static void AlternativePatterns(string kind, JsonObject json) {
        if (kind == PackageKeys.TrackKind) {
            foreach (var alternative in (json[PackageKeys.Track]?["alternatives"] as JsonArray ?? [])
                     .OfType<JsonObject>()) {
                alternative[PackageKeys.PatternRegions] ??= new JsonArray();
            }
        }
    }

    /// <summary>Audio, MIDI and pattern regions gain <c>loopTicks</c>; none loops yet.</summary>
    static void Loops(string kind, JsonObject json) {
        foreach (var holder in RegionHolders(kind, json)) {
            foreach (var list in (string[])["regions", PackageKeys.MidiRegions, "patternRegions"]) {
                foreach (var region in (holder[list] as JsonArray ?? []).OfType<JsonObject>()) {
                    region["loopTicks"] ??= 0;
                }
            }
        }
    }

    /// <summary>Audio regions gain <c>stretch</c>; each plays as recorded.</summary>
    static void Stretches(string kind, JsonObject json) {
        foreach (var holder in RegionHolders(kind, json)) {
            foreach (var region in (holder["regions"] as JsonArray ?? []).OfType<JsonObject>()) {
                region["stretch"] ??= 1;
            }
        }
    }

    /// <summary>The count-in was one bar, and there was no pre-roll.</summary>
    static void CountInAndPreRoll(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind && json[PackageKeys.Settings] is JsonObject settings) {
            if (settings["metronome"] is JsonObject metronome) {
                metronome["countInLength"] ??= 1;
                metronome["countInUnit"] ??= "bars";
            }

            if (settings["recording"] is JsonObject recording) {
                recording["preRollLength"] ??= 0;
                recording["preRollUnit"] ??= "bars";
            }
        }
    }

    /// <summary>
    ///     Every strip keeps its sound and its meter: no trim, polarity as recorded, no delay, plain pan, not solo safe, a
    ///     post-fader peak meter. The pan law is the one Anode always had, −3 dB compensated, and solo is solo in place.
    /// </summary>
    static void Mixer(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            if (json[PackageKeys.Settings]?["mixer"] is JsonObject mixer) {
                mixer["panLaw"] ??= "minus3DbCompensated";
                mixer["soloMode"] ??= "inPlace";
            }

            Strip(json["master"] as JsonObject);
        } else if (kind == PackageKeys.TrackKind) {
            Strip(json[PackageKeys.Track]?["strip"] as JsonObject);
        }

        static void Strip(JsonObject? strip) {
            if (strip is null) {
                return;
            }

            strip["trimDb"] ??= 0;
            strip["polarityInverted"] ??= false;
            strip["delayMs"] ??= 0;
            strip["dualPan"] ??= false;
            strip["panLeft"] ??= -1;
            strip["panRight"] ??= 1;
            strip["soloSafe"] ??= false;
            strip["meter"] ??= new JsonObject { ["type"] = "peak", ["tap"] = "postFader" };
        }
    }

    /// <summary>What holds regions in a track file: it, and each of its track's alternatives.</summary>
    static IEnumerable<JsonObject> RegionHolders(string kind, JsonObject json) {
        if (kind != PackageKeys.TrackKind) {
            yield break;
        }

        foreach (var alternative in (json[PackageKeys.Track]?["alternatives"] as JsonArray
                     ?? []).OfType<JsonObject>()) {
            yield return alternative;
        }

        yield return json;
    }
}
