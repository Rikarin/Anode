using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 11 (docs/12-phase-11.md): the chord track (<c>addChords</c>, <c>editChords</c>, <c>deleteChords</c>,
///     <c>setScales</c>, and Chord ID's <c>applyChordId</c>) and how regions follow it (<c>setPitchSource</c>,
///     <c>setRegionKey</c>). The chord track's chords are in tick order, at most one per tick; its key and scale
///     changes too.
/// </summary>
public static partial class Edits {
    /// <summary>The most chords a chord track holds.</summary>
    public const int MaxChords = 10_000;

    /// <summary>The most key and scale changes a chord track holds.</summary>
    public const int MaxScales = 1_000;

    /// <summary>The Phase 11 commands; null for every other command.</summary>
    static IReadOnlyList<DeltaOp>? Phase11Ops(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins,
        IPatchSource patches,
        SessionPlayerCatalog styles
    ) =>
        command switch {
            AddChordsCommand c => AddChords(state, c, ids),
            EditChordsCommand c => EditChords(state, c),
            DeleteChordsCommand c => DeleteChords(state, c),
            SetScalesCommand c => SetScales(state, c),
            ApplyChordIdCommand c => ApplyChordId(state, c, ids),
            SetPitchSourceCommand c => SetPitchSource(state, c),
            SetRegionKeyCommand c => SetRegionKey(state, c),
            _ => SessionPlayerOps(state, command, ids, plugins, patches, styles)
                ?? MasteringOps(state, command)
                ?? EnvironmentOps(state, command, ids)
        };

    static DeltaOp[] AddChords(ProjectState state, AddChordsCommand c, IIdSource ids) {
        if (c.Chords is not { Count: > 0 }) {
            throw new EditException("Name at least one chord to add.");
        }

        var chords = state.ChordTrack.Chords.ToDictionary(static ch => ch.Tick);
        foreach (var chord in c.Chords) {
            var added = CheckChord(
                new(
                    ids.Next(IdPrefix.Chord),
                    chord?.Tick ?? -1,
                    chord?.Root ?? throw new EditException("A chord has a root."),
                    chord.Quality,
                    chord.Bass
                )
            );
            chords[added.Tick] = added;
        }

        return [ChordsChanged(state, chords.Values)];
    }

    static DeltaOp[] EditChords(ProjectState state, EditChordsCommand c) {
        if (c.Chords is not { Count: > 0 }) {
            throw new EditException("Name at least one chord to change.");
        }

        var byId = state.ChordTrack.Chords.ToDictionary(static ch => ch.Id, StringComparer.Ordinal);
        foreach (var chord in c.Chords) {
            var changed = CheckChord(chord ?? throw new EditException("A chord is null."));
            if (!byId.ContainsKey(changed.Id ?? string.Empty)) {
                throw new EditException($"No chord '{changed.Id}'.");
            }

            byId[changed.Id!] = changed;
        }

        var chords = byId.Values.ToArray();
        if (chords.DistinctBy(static ch => ch.Tick).Count() != chords.Length) {
            throw new EditException("Two chords would be at one tick.");
        }

        return [ChordsChanged(state, chords)];
    }

    static DeltaOp[] DeleteChords(ProjectState state, DeleteChordsCommand c) {
        if (c.ChordIds is not { Count: > 0 }) {
            throw new EditException("Name at least one chord to delete.");
        }

        var gone = c.ChordIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in gone) {
            if (!state.ChordTrack.Chords.Any(ch => ch.Id == id)) {
                throw new EditException($"No chord '{id}'.");
            }
        }

        return [ChordsChanged(state, state.ChordTrack.Chords.Where(ch => !gone.Contains(ch.Id)))];
    }

    static DeltaOp[] SetScales(ProjectState state, SetScalesCommand c) {
        if (c.Scales is null) {
            throw new EditException("Name the key and scale changes (none: an empty list).");
        }

        if (c.Scales.Count > MaxScales) {
            throw new EditException($"A chord track holds at most {MaxScales} key and scale changes.");
        }

        foreach (var scale in c.Scales) {
            CheckTick(scale?.Tick ?? -1, "scale");
            CheckPitchName(scale!.Tonic ?? throw new EditException("A key and scale change has a tonic."));
            if (!Enum.IsDefined(scale.Scale)) {
                throw new EditException($"Unknown scale {scale.Scale}.");
            }
        }

        if (c.Scales.DistinctBy(static s => s.Tick).Count() != c.Scales.Count) {
            throw new EditException("Two key and scale changes at one tick.");
        }

        return [new ChordTrackChanged(state.ChordTrack with { Scales = [..c.Scales.OrderBy(static s => s.Tick)] })];
    }

    /// <summary>
    ///     Chord ID's result: by its <c>write</c>, the chords in its range replaced (or, merging, added where the chord
    ///     track has none within a beat), and the detected keys set on their regions.
    /// </summary>
    static List<DeltaOp> ApplyChordId(ProjectState state, ApplyChordIdCommand c, IIdSource ids) {
        var result = c.Result ?? throw new EditException("Name Chord ID's result.");
        CheckTick(result.StartTick, "range start");
        CheckTick(result.EndTick, "range end");
        if (result.EndTick <= result.StartTick) {
            throw new EditException("Chord ID's range ends after it starts.");
        }

        if (!Enum.IsDefined(result.Write) || result.Chords is null || result.Keys is null) {
            throw new EditException("Chord ID's result has a way to write, chords and keys.");
        }

        var ops = new List<DeltaOp>();
        if (result.Write != ChordIdWrite.None) {
            var detected = result.Chords.Select(static d => CheckChord(
                        new(
                            string.Empty,
                            d?.Tick ?? -1,
                            d?.Root ?? throw new EditException("A detected chord has a root."),
                            d.Quality,
                            d.Bass
                        )
                    )
                )
                .Where(ch => ch.Tick >= result.StartTick && ch.Tick < result.EndTick)
                .DistinctBy(static ch => ch.Tick)
                .ToArray();
            var kept = result.Write == ChordIdWrite.Replace
                ? state.ChordTrack.Chords.Where(ch => ch.Tick < result.StartTick || ch.Tick >= result.EndTick).ToList()
                : [..state.ChordTrack.Chords];
            var signatures = state.SignatureMap;
            foreach (var chord in detected) {
                var beat = SignatureMap.BeatTicks(signatures.At(chord.Tick).Denominator);
                if (result.Write == ChordIdWrite.Merge && kept.Exists(k => Math.Abs(k.Tick - chord.Tick) < beat)) {
                    continue;
                }

                kept.RemoveAll(k => k.Tick == chord.Tick);
                kept.Add(chord with { Id = ids.Next(IdPrefix.Chord) });
            }

            ops.Add(ChordsChanged(state, kept));
        }

        foreach (var key in result.Keys.DistinctBy(static k => k?.RegionId)) {
            var regionKey = CheckRegionKey(key?.Key ?? throw new EditException("A detected key has a key."));
            ops.Add(WithKey(state, key.RegionId ?? string.Empty, regionKey));
        }

        return ops;
    }

    static List<DeltaOp> SetPitchSource(ProjectState state, SetPitchSourceCommand c) {
        if (!Enum.IsDefined(c.Mode)) {
            throw new EditException($"Unknown Pitch Source {c.Mode}.");
        }

        return [
            ..FollowingRegions(state, c.RegionIds)
                .Select(id => state.MidiRegions.TryGetValue(id, out var midi)
                        ? (DeltaOp)new MidiRegionUpserted(midi with { PitchSource = c.Mode })
                        : c.Mode == PitchSourceMode.Chords
                            ? throw new EditException($"Region '{id}' is an audio region: it follows by root only.")
                            : new RegionUpserted(state.Regions[id] with { PitchSource = c.Mode })
                )
        ];
    }

    static List<DeltaOp> SetRegionKey(ProjectState state, SetRegionKeyCommand c) {
        var key = c.Key is null ? null : CheckRegionKey(c.Key);
        return [..FollowingRegions(state, c.RegionIds).Select(id => WithKey(state, id, key))];
    }

    /// <summary>The distinct ids of MIDI and audio regions (the regions that follow the chord track), checked.</summary>
    static string[] FollowingRegions(ProjectState state, IReadOnlyList<string>? ids) {
        if (ids is not { Count: > 0 }) {
            throw new EditException("Name at least one MIDI or audio region.");
        }

        var distinct = ids.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in distinct) {
            if (id is null || (!state.MidiRegions.ContainsKey(id) && !state.Regions.ContainsKey(id))) {
                throw new EditException($"No MIDI or audio region '{id}'.");
            }
        }

        return distinct;
    }

    static DeltaOp WithKey(ProjectState state, string regionId, RegionKey? key) =>
        state.MidiRegions.TryGetValue(regionId, out var midi)
            ? new MidiRegionUpserted(midi with { Key = key })
            : state.Regions.TryGetValue(regionId, out var audio)
                ? new RegionUpserted(audio with { Key = key })
                : throw new EditException($"No MIDI or audio region '{regionId}'.");

    /// <summary>A chord as the chord track keeps it: at a tick in range, a spelled root and bass, a known quality.</summary>
    public static Chord CheckChord(Chord chord) {
        CheckTick(chord.Tick, "chord");
        CheckPitchName(chord.Root ?? throw new EditException("A chord has a root."));
        if (chord.Quality is null || !ChordQualities.IsKnown(chord.Quality)) {
            throw new EditException(
                $"Chord quality '{chord.Quality}' is not one of {string.Join(", ", ChordQualities.All.Select(static q => $"'{q}'"))}."
            );
        }

        if (chord.Bass is { } bass) {
            CheckPitchName(bass);
        }

        // A slash bass that is the root is no slash.
        return chord.Bass == chord.Root ? chord with { Bass = null } : chord;
    }

    public static RegionKey CheckRegionKey(RegionKey key) {
        CheckPitchName(key.Tonic ?? throw new EditException("A key has a tonic."));
        return Enum.IsDefined(key.Mode) ? key : throw new EditException($"Unknown key mode {key.Mode}.");
    }

    /// <summary>The chord track with <paramref name="chords" />, sorted by tick (at most <see cref="MaxChords" />).</summary>
    static ChordTrackChanged ChordsChanged(ProjectState state, IEnumerable<Chord> chords) {
        Chord[] sorted = [..chords.OrderBy(static ch => ch.Tick)];
        return sorted.Length > MaxChords
            ? throw new EditException($"A chord track holds at most {MaxChords} chords.")
            : new(state.ChordTrack with { Chords = sorted });
    }

    /// <summary>
    ///     The chord sounding at <paramref name="tick" /> (the last at or before it), or null before the first.
    /// </summary>
    public static Chord? ChordAt(ChordTrack track, long tick) {
        Chord? at = null;
        foreach (var chord in track.Chords) {
            if (chord.Tick > tick) {
                break;
            }

            at = chord;
        }

        return at;
    }

    /// <summary>
    ///     The pitch class of the key a region follows the chord track from: its own key's tonic, else the project's key
    ///     track's at its start.
    /// </summary>
    public static int KeyRootOf(ProjectState state, RegionKey? key, long startTick) =>
        key is not null ? PitchSources.PitchClass(key.Tonic) : (KeyAt(state, startTick).Tonic % 12 + 12) % 12;
}
