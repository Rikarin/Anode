using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Server.Library;

namespace Anode.Server.Projects;

/// <summary>
///     Files of installed content packs in a project (docs/07-phase-6.md, "Packs"): a pack can be removed, so what a
///     project takes from one (a pad's sample, a patch's sample or impulse response) is copied into it, as loops are.
///     Patches and kits come through <see cref="PatchesOf" />, a pack's file named by a command through
///     <c>Resolve</c>, and a project saved with paths into a pack is put right when it is opened.
/// </summary>
public sealed partial class ProjectSession {
    /// <summary>The folder of a project that instruments' samples from packs are copied into.</summary>
    public const string SamplesFolder = "Samples";

    string PacksFolder => Path.Combine(options.Value.DataDirectory, "Library", "Packs");

    /// <summary>The library's patches as <paramref name="o" /> takes them: with their pack files copied into it.</summary>
    PatchesFor PatchesOf(Open o) => new(this, patches, o.Folder);

    /// <summary>
    ///     A path as the project in <paramref name="folder" /> keeps it: a file of an installed pack is copied into
    ///     <paramref name="target" />, the path then the project's relative one. Any other path stays as it is.
    /// </summary>
    string Kept(string folder, string target, string path) =>
        Path.IsPathRooted(path) && IsInside(PacksFolder, path) && File.Exists(path)
            ? CopiedInto(folder, target, Path.GetFullPath(path))
            : path;

    InstrumentSlot? Kept(string folder, InstrumentSlot? slot) =>
        slot is { File: { } file } && Kept(folder, SamplesFolder, file) is var kept && kept != file
            ? slot with { File = kept }
            : slot;

    IReadOnlyList<Insert> Kept(string folder, IReadOnlyList<Insert> inserts) =>
        inserts.Any(i => i.File is { } file && Kept(folder, ImpulseResponsesFolder, file) != file)
            ? [
                ..inserts.Select(i => i.File is { } file
                        ? i with { File = Kept(folder, ImpulseResponsesFolder, file) }
                        : i
                )
            ]
            : inserts;

    /// <summary>
    ///     <paramref name="state" /> with the paths into installed packs that a project saved before they were copied
    ///     holds put right. When a file cannot be copied the project still opens, as it was (logged).
    /// </summary>
    ProjectState WithPackFilesKept(string folder, ProjectState state) {
        try {
            var kept = state;
            for (var i = 0; i < state.Tracks.Count; i++) {
                var track = state.Tracks[i];
                var instrument = Kept(folder, track.Instrument);
                var inserts = Kept(folder, track.Strip.Inserts);
                if (!ReferenceEquals(instrument, track.Instrument) || !ReferenceEquals(inserts, track.Strip.Inserts)) {
                    kept = kept.Apply(
                        new TrackUpserted(
                            track with { Instrument = instrument, Strip = track.Strip with { Inserts = inserts } },
                            i
                        )
                    );
                }
            }

            var master = Kept(folder, state.Master.Inserts);
            return ReferenceEquals(master, state.Master.Inserts)
                ? kept
                : kept.Apply(new MasterChanged(state.Master with { Inserts = master }));
        } catch (ProjectException e) {
            ProjectLog.PackFilesNotKept(logger, folder, e.Message);
            return state;
        }
    }

    /// <summary>
    ///     <paramref name="track" /> as a patch stores it: a file in the project by its absolute path, since a patch is
    ///     applied in other projects.
    /// </summary>
    static Track Portable(string folder, Track track) =>
        track with {
            Instrument = track.Instrument is { File: { } file } slot
                ? slot with { File = Path.GetFullPath(file, folder) }
                : track.Instrument,
            Strip = track.Strip with {
                Inserts = [
                    ..track.Strip.Inserts.Select(i =>
                        i.File is { } file ? i with { File = Path.GetFullPath(file, folder) } : i
                    )
                ]
            }
        };

    /// <summary>Copies of the library's patches for one project.</summary>
    sealed class PatchesFor(ProjectSession session, PatchLibrary library, string folder) : IPatchSource {
        public ChannelPatch? Find(string id) =>
            library.Find(id) is { } patch
                ? patch with {
                    Instrument = session.Kept(folder, patch.Instrument),
                    Inserts = session.Kept(folder, patch.Inserts),
                    Pads = patch.Pads?.Select(p => p with {
                                Instrument = session.Kept(folder, p.Instrument)!,
                                Inserts = session.Kept(folder, p.Inserts)
                            }
                        )
                        .ToArray()
                }
                : null;
    }
}

static partial class ProjectLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not copy pack files into {Folder}: {Error}")]
    public static partial void PackFilesNotKept(ILogger logger, string folder, string error);
}
