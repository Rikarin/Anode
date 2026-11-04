using Anode.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anode.Model.Storage;

/// <summary>
///     The Phase 11 additions to a schema-10 file (docs/12-phase-11.md): an empty chord track and Environment, Pitch
///     Source <c>off</c> on every MIDI and audio region (on tracks, in track alternatives and in Live Loops cells; their
///     keys null, left out), and no Session Player regions. No Mastering Assistant has an analysis (left out). Leaves
///     fields that are present alone.
/// </summary>
static class Schema11 {
    public static void Fill(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            json["chordTrack"] ??= new JsonObject { ["chords"] = new JsonArray(), ["scales"] = new JsonArray() };
            json["environment"] ??= new JsonObject {
                ["layers"] = new JsonArray(), ["objects"] = new JsonArray(), ["cables"] = new JsonArray()
            };
            foreach (var cell in (json["liveLoops"]?["cells"] as JsonArray ?? []).OfType<JsonObject>()) {
                Follows(cell["content"]?["midi"] as JsonObject);
                Follows(cell["content"]?["audio"] as JsonObject);
            }
        } else if (kind == PackageKeys.TrackKind) {
            json["sessionPlayerRegions"] ??= new JsonArray();
            foreach (var alternative in (json[PackageKeys.Track]?["alternatives"] as JsonArray
                         ?? []).OfType<JsonObject>()) {
                Regions(alternative);
            }

            Regions(json);
        }
    }

    static void Regions(JsonObject holder) {
        foreach (var list in (string[])["regions", PackageKeys.MidiRegions]) {
            foreach (var region in (holder[list] as JsonArray ?? []).OfType<JsonObject>()) {
                Follows(region);
            }
        }
    }

    static void Follows(JsonObject? region) {
        if (region is not null) {
            region["pitchSource"] ??= "off";
        }
    }
}

/// <summary>
///     The Phase 10 additions to a schema-9 file (docs/11-phase-10.md): the project is stereo
///     (<c>settings.surroundFormat</c>), every strip (the master's and each track's) is stereo in and out with
///     <see cref="Panner.Default" />, every marker is of kind <c>marker</c>, and there is no movie (left out). Leaves
///     fields that are present alone.
/// </summary>
static class Schema10 {
    public static void Fill(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            if (json[PackageKeys.Settings] is JsonObject settings) {
                settings["surroundFormat"] ??= "stereo";
            }

            Strip(json["master"] as JsonObject);
            foreach (var marker in (json["markers"] as JsonArray ?? []).OfType<JsonObject>()) {
                marker["kind"] ??= "marker";
            }
        } else if (kind == PackageKeys.TrackKind) {
            Strip(json[PackageKeys.Track]?["strip"] as JsonObject);
        }
    }

    static void Strip(JsonObject? strip) {
        if (strip is not null) {
            strip["inputFormat"] ??= "stereo";
            strip["outputFormat"] ??= "stereo";
            strip["panner"] ??= JsonSerializer.SerializeToNode(Panner.Default, PackageJsonContext.Default.Panner);
        }
    }
}

/// <summary>
///     The Phase 9 additions to a schema-8 file (docs/10-phase-9.md): every MIDI region (on tracks, in track
///     alternatives and in Live Loops cells) has <see cref="RegionNotation.Default" />; tracks have no staff style (null,
///     left out) and the project's score is <see cref="ProjectScore.Empty" />. Leaves fields that are present alone.
/// </summary>
static class Schema9 {
    public static void Fill(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            json["score"] ??= JsonSerializer.SerializeToNode(
                ProjectScore.Empty,
                PackageJsonContext.Default.ProjectScore
            );
            foreach (var cell in (json["liveLoops"]?["cells"] as JsonArray ?? []).OfType<JsonObject>()) {
                Notation(cell["content"]?["midi"] as JsonObject);
            }
        } else if (kind == PackageKeys.TrackKind) {
            foreach (var alternative in (json[PackageKeys.Track]?["alternatives"] as JsonArray
                         ?? []).OfType<JsonObject>()) {
                foreach (var region in (alternative[PackageKeys.MidiRegions] as JsonArray ?? []).OfType<JsonObject>()) {
                    Notation(region);
                }
            }

            foreach (var region in (json[PackageKeys.MidiRegions] as JsonArray ?? []).OfType<JsonObject>()) {
                Notation(region);
            }
        }
    }

    static void Notation(JsonObject? region) {
        if (region is not null) {
            region["notation"] ??= new JsonObject {
                ["displayQuantizeTicks"] = 0,
                ["tripletQuantizeTicks"] = 0,
                ["interpretation"] = true,
                ["noOverlap"] = true,
                ["syncopation"] = false,
                ["maxDots"] = 1
            };
        }
    }
}

/// <summary>
///     The Phase 8 additions to a schema-7 file (docs/09-phase-8.md): the project's sync settings at
///     <see cref="SyncSettings.Default" /> (internal, nothing sent, 25 fps, from timecode 0, Link start/stop off, any
///     input). Leaves fields that are present alone.
/// </summary>
static class Schema8 {
    public static void Project(JsonObject json) {
        if (json[PackageKeys.Settings] is JsonObject settings) {
            settings["sync"] ??= new JsonObject {
                ["source"] = "internal",
                ["destinations"] = new JsonArray(),
                ["frameRate"] = "fps25",
                ["startOffsetSeconds"] = 0,
                ["linkStartStop"] = false
            };
        }
    }
}

/// <summary>The Phase 7 additions to a schema-6 file (docs/08-phase-7.md).</summary>
static class Schema7 {
    public static void Project(JsonObject json) {
        json["grooves"] ??= new JsonArray();
        json["articulationSets"] ??= new JsonArray();
        json["laneSets"] ??= new JsonArray();
        foreach (var file in (json["audioFiles"] as JsonArray ?? []).OfType<JsonObject>()) {
            file["version"] ??= 0;
        }

        foreach (var cell in (json["liveLoops"]?["cells"] as JsonArray ?? []).OfType<JsonObject>()) {
            Notes(cell["content"]?["midi"] as JsonObject);
        }
    }

    public static void Track(JsonObject json) {
        if (json[PackageKeys.Track] is JsonObject track) {
            track["matchGroove"] ??= false;
            foreach (var alternative in (track["alternatives"] as JsonArray ?? []).OfType<JsonObject>()) {
                foreach (var region in (alternative[PackageKeys.MidiRegions] as JsonArray ?? []).OfType<JsonObject>()) {
                    Notes(region);
                }
            }
        }

        foreach (var region in (json[PackageKeys.MidiRegions] as JsonArray ?? []).OfType<JsonObject>()) {
            Notes(region);
        }
    }

    /// <summary>The notes of a MIDI region without articulations.</summary>
    public static void Notes(JsonObject? region) {
        foreach (var note in (region?["notes"] as JsonArray ?? []).OfType<JsonObject>()) {
            note["articulationId"] ??= 0;
        }
    }
}

/// <summary>The Phase 6 additions to a schema-5 file (docs/07-phase-6.md).</summary>
static class Schema6 {
    /// <summary>A channel strip without MIDI FX.</summary>
    public static void Strip(JsonObject? strip) {
        if (strip is not null) {
            strip["midiEffects"] ??= new JsonArray();
        }
    }
}

/// <summary>The Phase 4 additions to a schema-3 file (docs/05-phase-4.md).</summary>
static class Schema4 {
    /// <summary>A channel strip without inserts or sends, to the master, in no group and no VCA.</summary>
    public static void Strip(JsonObject? strip) {
        if (strip is null) {
            return;
        }

        strip["inserts"] ??= new JsonArray();
        strip["sends"] ??= new JsonArray();
        strip["output"] ??= "master";
        strip["groups"] ??= new JsonArray();
    }
}
