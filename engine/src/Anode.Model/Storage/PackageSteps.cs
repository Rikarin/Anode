using System.Text.Json.Nodes;

namespace Anode.Model.Storage;

/// <summary>
///     The migration steps after Phase 11 (<see cref="PackageMigrations" />); each leaves fields that are present
///     alone.
/// </summary>
static class PackageSteps {
    /// <summary>Schema 11 to 12: audio, MIDI and pattern regions gain <c>loopTicks</c>; none loops yet.</summary>
    public static JsonObject ToSchema12(string kind, JsonObject json) {
        foreach (var holder in RegionHolders(kind, json)) {
            foreach (var list in (string[])["regions", PackageKeys.MidiRegions, "patternRegions"]) {
                foreach (var region in (holder[list] as JsonArray ?? []).OfType<JsonObject>()) {
                    region["loopTicks"] ??= 0;
                }
            }
        }

        return json;
    }

    /// <summary>Schema 12 to 13: audio regions gain <c>stretch</c>; each plays as recorded.</summary>
    public static JsonObject ToSchema13(string kind, JsonObject json) {
        foreach (var holder in RegionHolders(kind, json)) {
            foreach (var region in (holder["regions"] as JsonArray ?? []).OfType<JsonObject>()) {
                region["stretch"] ??= 1;
            }
        }

        return json;
    }

    /// <summary>Schema 13 to 14: the count-in was one bar, and there was no pre-roll.</summary>
    public static JsonObject ToSchema14(string kind, JsonObject json) {
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

        return json;
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
