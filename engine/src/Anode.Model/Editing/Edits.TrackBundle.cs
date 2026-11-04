using Anode.Contracts;
using System.Collections.Immutable;

namespace Anode.Model.Editing;

/// <summary>What of each track goes with it when tracks leave a project (<see cref="TrackBundle" />).</summary>
public sealed record TrackParts(bool Regions = true, bool Inserts = true, bool Sends = true, bool Automation = true);

/// <summary>
///     Tracks taken out of a project with what belongs to them, ready to go into another
///     (<see cref="Edits.InsertTracks" />, docs/04-phase-3.md, "Import tracks from another project"): their regions, take
///     folders, lanes and pattern and Session Player regions, the mixer groups and articulation sets they use, and the
///     audio files their content names. Ids are the source's.
/// </summary>
public sealed record TrackBundle(
    ImmutableList<Track> Tracks,
    ImmutableList<AudioRegion> Regions,
    ImmutableList<MidiRegion> MidiRegions,
    ImmutableList<TakeFolder> TakeFolders,
    ImmutableList<AutomationLane> Automation,
    ImmutableList<PatternRegion> PatternRegions,
    ImmutableList<SessionPlayerRegion> SessionPlayerRegions,
    ImmutableList<MixerGroup> Groups,
    ImmutableList<ArticulationSet> ArticulationSets,
    ImmutableList<AudioFile> Files
) {
    /// <summary>
    ///     The tracks <paramref name="trackIds" /> name, in project order, with what they need to work: a stack's members,
    ///     and, with <see cref="TrackParts.Sends" />, the auxes their sends and outputs reach, and the auxes those reach.
    ///     Without a part it is left out, along with what only it needed.
    /// </summary>
    public static TrackBundle Of(ProjectState state, IEnumerable<string> trackIds, TrackParts parts) {
        var chosen = Closure(state, trackIds, parts);
        var tracks = state.Tracks.Where(t => chosen.Contains(t.Id)).Select(t => Stripped(t, parts)).ToImmutableList();
        var regions = parts.Regions
            ? tracks.SelectMany(t => state.RegionsOf(t.Id)).ToImmutableList()
            : [];
        var folders = parts.Regions
            ? tracks.SelectMany(t => state.TakeFoldersOf(t.Id)).ToImmutableList()
            : [];
        var used = FilesOf(tracks, regions, folders);
        return new(
            tracks,
            regions,
            parts.Regions ? [..tracks.SelectMany(t => state.MidiRegionsOf(t.Id))] : [],
            folders,
            parts.Automation
                ? [
                    ..tracks.SelectMany(t => state.LanesOf(t.Id)
                        .Where(l => (parts.Inserts || !IsInsertTarget(l.Target))
                            && (parts.Sends || !IsSendTarget(l.Target))
                        )
                    )
                ]
                : [],
            parts.Regions ? [..tracks.SelectMany(t => state.PatternRegionsOf(t.Id))] : [],
            parts.Regions ? [..tracks.SelectMany(t => state.SessionPlayerRegionsOf(t.Id))] : [],
            [
                ..tracks.SelectMany(static t => t.Strip.Groups)
                    .Distinct()
                    .Where(state.Groups.ContainsKey)
                    .Select(g => state.Groups[g])
            ],
            [
                ..tracks.Select(static t => t.ArticulationSetId)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Where(state.ArticulationSets.ContainsKey)
                    .Select(id => state.ArticulationSets[id])
            ],
            [..used.Where(state.Files.ContainsKey).Select(id => state.Files[id])]
        );
    }

    /// <summary>The ids of the chosen tracks and of what comes with them.</summary>
    static HashSet<string> Closure(ProjectState state, IEnumerable<string> trackIds, TrackParts parts) {
        var chosen = trackIds.ToHashSet(StringComparer.Ordinal);
        var added = true;
        while (added) {
            added = false;
            foreach (var track in state.Tracks.Where(t => !chosen.Contains(t.Id))) {
                if (track.ParentId is { } parent && chosen.Contains(parent)) {
                    added |= chosen.Add(track.Id);
                }
            }

            if (!parts.Sends) {
                continue;
            }

            var fed = state.Tracks.Where(t => chosen.Contains(t.Id)).SelectMany(Routing.Feeds).ToHashSet();
            foreach (var aux in state.Tracks.Where(t => t.Kind == TrackKind.Aux && fed.Contains(t.InputBus))) {
                added |= chosen.Add(aux.Id);
            }
        }

        return chosen;
    }

    static Track Stripped(Track track, TrackParts parts) {
        var strip = track.Strip;
        var smart = track.SmartControls;
        if (!parts.Inserts) {
            strip = strip with { Inserts = [] };
            smart = Without(smart, IsInsertTarget);
        }

        if (!parts.Sends) {
            strip = strip with {
                Sends = [], Output = Routing.BusOf(strip.Output) is null ? strip.Output : Routing.Master
            };
            smart = Without(smart, IsSendTarget);
        }

        return track with {
            Strip = strip,
            SmartControls = smart,
            FrozenFileId = parts.Regions ? track.FrozenFileId : null,
            Alternatives = parts.Regions ? track.Alternatives : [],
            AlternativeId = parts.Regions ? track.AlternativeId : null
        };
    }

    static SmartControls? Without(SmartControls? smart, Func<string, bool> target) =>
        smart is null
            ? null
            : smart with {
                Controls = [
                    ..smart.Controls.Select(c => c with { Mappings = [..c.Mappings.Where(m => !target(m.Target))] })
                ]
            };

    static bool IsInsertTarget(string target) => target.StartsWith("insert:", StringComparison.Ordinal);

    static bool IsSendTarget(string target) => target.StartsWith("send:", StringComparison.Ordinal);

    /// <summary>Every audio file the content names: regions and takes, also in alternatives, and frozen files.</summary>
    static HashSet<string> FilesOf(
        IEnumerable<Track> tracks,
        IEnumerable<AudioRegion> regions,
        IEnumerable<TakeFolder> folders
    ) {
        var used = regions.Select(static r => r.FileId)
            .Concat(folders.SelectMany(static f => f.Takes).Select(static t => t.FileId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var track in tracks) {
            if (track.FrozenFileId is { } frozen) {
                used.Add(frozen);
            }

            foreach (var alternative in track.Alternatives) {
                used.UnionWith(alternative.Regions.Select(static r => r.FileId));
                used.UnionWith(alternative.TakeFolders.SelectMany(static f => f.Takes).Select(static t => t.FileId));
            }
        }

        return used;
    }
}

/// <summary>What <see cref="Edits.InsertTracks" /> made: its operations, and each source track's new id.</summary>
public sealed record TrackInsertion(IReadOnlyList<DeltaOp> Ops, IReadOnlyDictionary<string, string> Tracks);

public static partial class Edits {
    /// <summary>
    ///     Adds a bundle's tracks after the project's, as a new part of it: everything gets a new id, and references
    ///     between the tracks follow, so importing the same tracks twice gives two independent sets. Routing stays as it
    ///     was among them on buses the project does not use, so they mix with nothing of its; a group takes a free number.
    ///     What pointed outside the bundle goes: a parent, a VCA, a sidechain, a groove. Audio files must already be in
    ///     <paramref name="state" /> under the bundle's ids. The tracks come disarmed, and playing their regions.
    /// </summary>
    public static TrackInsertion InsertTracks(ProjectState state, TrackBundle bundle, IIdSource ids) {
        if (state.Tracks.Count + bundle.Tracks.Count > MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        return new Insertion(state, bundle, ids).Run();
    }

    /// <summary>One insertion: the bundle, what its ids became, and the operations so far.</summary>
    sealed class Insertion(ProjectState state, TrackBundle bundle, IIdSource ids) {
        readonly Dictionary<string, string> tracks = new(StringComparer.Ordinal);
        readonly Dictionary<int, int> buses = [];
        readonly Dictionary<int, int> groups = [];
        readonly Dictionary<string, string> sets = new(StringComparer.Ordinal);
        readonly List<DeltaOp> ops = [];
        long next = state.LastEventId;

        public TrackInsertion Run() {
            foreach (var track in bundle.Tracks) {
                tracks[track.Id] = ids.Next(IdPrefix.Track);
            }

            MapBuses();
            MapGroups();
            foreach (var set in bundle.ArticulationSets) {
                sets[set.Id] = ids.Next(IdPrefix.ArticulationSet);
                ops.Add(new ArticulationSetUpserted(set with { Id = sets[set.Id] }));
            }

            var index = state.Tracks.Count;
            foreach (var track in bundle.Tracks) {
                ops.Add(new TrackUpserted(Track(track), index++));
            }

            ops.AddRange(bundle.Regions.Select(r => new RegionUpserted(Audio(r, tracks[r.TrackId]))));
            ops.AddRange(bundle.MidiRegions.Select(r => new MidiRegionUpserted(Midi(r, tracks[r.TrackId]))));
            ops.AddRange(bundle.TakeFolders.Select(f => new TakeFolderUpserted(Folder(f, tracks[f.TrackId]))));
            ops.AddRange(
                bundle.PatternRegions.Select(r => new PatternRegionUpserted(
                        r with { Id = ids.Next(IdPrefix.PatternRegion), TrackId = tracks[r.TrackId] }
                    )
                )
            );
            ops.AddRange(bundle.SessionPlayerRegions.Select(r => new SessionPlayerRegionUpserted(Player(r))));
            ops.AddRange(
                bundle.Automation.Select(l => new AutomationLaneUpserted(
                        l with { Id = ids.Next(IdPrefix.Automation), TrackId = tracks[l.TrackId!] }
                    )
                )
            );
            return new(ops, tracks);
        }

        /// <summary>A free bus for each bus the tracks use, in order.</summary>
        void MapBuses() {
            var taken = state.Tracks.SelectMany(Routing.Feeds)
                .Concat(state.Tracks.Where(static t => t.Kind == TrackKind.Aux).Select(static t => t.InputBus))
                .Concat(
                    state.Tracks.SelectMany(static t => t.Strip.Inserts)
                        .Concat(state.Master.Inserts)
                        .Select(static i => Routing.BusOf(i.Sidechain) ?? 0)
                )
                .ToHashSet();
            var used = bundle.Tracks.SelectMany(Routing.Feeds)
                .Concat(bundle.Tracks.Where(static t => t.Kind == TrackKind.Aux).Select(static t => t.InputBus))
                .Concat(
                    bundle.Tracks.SelectMany(static t => t.Strip.Inserts)
                        .Select(static i => Routing.BusOf(i.Sidechain) ?? 0)
                )
                .Where(static b => b > 0)
                .Distinct()
                .Order();
            var free = 1;
            foreach (var bus in used) {
                while (free <= MaxBus && taken.Contains(free)) {
                    free++;
                }

                if (free > MaxBus) {
                    throw new EditException($"The tracks need more buses than the {MaxBus} a project has.");
                }

                buses[bus] = free++;
            }
        }

        /// <summary>A free number for each group; a group with no number left stays out, and its tracks with it.</summary>
        void MapGroups() {
            var free = new Queue<int>(Enumerable.Range(1, MaxGroup).Where(n => !state.Groups.ContainsKey(n)));
            foreach (var group in bundle.Groups) {
                if (free.TryDequeue(out var number)) {
                    groups[group.Number] = number;
                    ops.Add(new GroupUpserted(group with { Number = number }));
                }
            }
        }

        string? Mapped(string? trackId) => trackId is null ? null : tracks.GetValueOrDefault(trackId);

        string Output(string output) => Routing.BusOf(output) is { } bus ? $"bus:{buses[bus]}" : output;

        Insert Inserted(Insert insert) =>
            insert with {
                Sidechain = insert.Sidechain is null ? null
                    : Routing.BusOf(insert.Sidechain) is { } bus ? $"bus:{buses[bus]}"
                    : Mapped(insert.Sidechain)
            };

        Track Track(Track track) {
            var strip = track.Strip;
            var regionTrack = tracks[track.Id];
            return track with {
                Id = regionTrack,
                ParentId = Mapped(track.ParentId),
                RecordArmed = false,
                InputMonitoring = false,
                InputBus = track.Kind == TrackKind.Aux ? buses.GetValueOrDefault(track.InputBus, track.InputBus) : 0,
                LiveLoopsActive = false,
                FrozenFileId = track.FrozenFileId,
                ArticulationSetId = track.ArticulationSetId is { } set ? sets.GetValueOrDefault(set) : null,
                StaffStyleId = track.StaffStyleId is { } style && StaffStyleIds.All.Contains(style) ? style : null,
                DrumMachine = track.DrumMachine is { } kit
                    ? new(
                        [
                            ..kit.Pads.Where(p => tracks.ContainsKey(p.TrackId))
                                .Select(p => p with { TrackId = tracks[p.TrackId] })
                        ]
                    )
                    : null,
                Alternatives = [..track.Alternatives.Select(a => Alternative(a, regionTrack))],
                AlternativeId = track.AlternativeId,
                Strip = strip with {
                    Inserts = [..strip.Inserts.Select(Inserted)],
                    Sends = [..strip.Sends.Select(s => s with { Bus = buses[s.Bus] })],
                    Output = Output(strip.Output),
                    Groups = [..strip.Groups.Where(groups.ContainsKey).Select(g => groups[g]).Order()],
                    VcaId = Mapped(strip.VcaId)
                }
            };
        }

        TrackAlternative Alternative(TrackAlternative alternative, string trackId) =>
            alternative with {
                Regions = [..alternative.Regions.Select(r => Audio(r, trackId))],
                MidiRegions = [..alternative.MidiRegions.Select(r => Midi(r, trackId))],
                TakeFolders = [..alternative.TakeFolders.Select(f => Folder(f, trackId))]
            };

        AudioRegion Audio(AudioRegion region, string trackId) =>
            region with { Id = ids.Next(IdPrefix.Region), TrackId = trackId };

        MidiRegion Midi(MidiRegion region, string trackId) =>
            region with {
                Id = ids.Next(IdPrefix.MidiRegion),
                TrackId = trackId,
                Playback = region.Playback with { GrooveId = null },
                Notes = Sorted(region.Notes.Select(n => n with { Id = ++next })),
                Controls = Sorted(region.Controls.Select(c => c with { Id = ++next }))
            };

        TakeFolder Folder(TakeFolder folder, string trackId) {
            var takes = folder.Takes.ToDictionary(
                static t => t.Id,
                _ => ids.Next(IdPrefix.Take),
                StringComparer.Ordinal
            );
            return folder with {
                Id = ids.Next(IdPrefix.TakeFolder),
                TrackId = trackId,
                Takes = [..folder.Takes.Select(t => t with { Id = takes[t.Id] })],
                Comp = [..folder.Comp.Select(s => s with { TakeId = takes[s.TakeId] })]
            };
        }

        SessionPlayerRegion Player(SessionPlayerRegion region) {
            var drums = region.Parameters.Drums;
            return region with {
                Id = ids.Next(IdPrefix.SessionPlayerRegion),
                TrackId = tracks[region.TrackId],
                Parameters = drums is null
                    ? region.Parameters
                    : region.Parameters with {
                        Drums = drums with { RhythmSourceTrackId = Mapped(drums.RhythmSourceTrackId) }
                    }
            };
        }
    }
}
