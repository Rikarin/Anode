using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Collections.Immutable;

namespace Anode.Server.Projects;

/// <summary>
///     Templates (docs/04-phase-3.md, "Templates"). A project template is a package; a new project made from it is a copy
///     under its own id and name. A track template is a package of the chosen tracks with their routing, without regions
///     or automation, which goes into a project through the same import as another project's tracks.
/// </summary>
public sealed partial class ProjectSession {
    /// <summary>A project at <paramref name="folder" /> from a template of the user's, which stays as it is.</summary>
    Project FromTemplate(string name, string folder, string template, int? rate) {
        ProjectState state;
        Dictionary<string, byte[]> states;
        try {
            state = ProjectPackage.Load(template);
            states = ProjectPackage.LoadStates(template, state);
        } catch (PackageException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        if (rate is { } wanted && wanted != state.SampleRate) {
            if (!state.Files.IsEmpty) {
                throw ProjectException.Unprocessable(
                    $"This template holds audio at {state.SampleRate} Hz: create the project at that rate, then change it."
                );
            }

            state = ProjectRate.Changed(state, wanted, SampleRateChangeMode.Convert, new Dictionary<string, AudioFile>());
        }

        try {
            ProjectCopy.Write(
                template,
                folder,
                state with { Id = ids.Next(IdPrefix.Project), Name = name },
                states,
                new(false)
            );
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException) {
            throw ProjectException.Unprocessable($"Could not create '{folder}': {e.Message}");
        }

        return OpenFolder(folder);
    }

    /// <summary>The open project, unsaved edits included, as a template of the user's: all of it, without its history.</summary>
    public ProjectTemplate SaveProjectTemplate(
        SaveTemplateRequest request,
        IReadOnlyDictionary<string, byte[]?>? hostStates,
        Action<double>? progress,
        CancellationToken token
    ) {
        var (name, category) = CheckTemplate(request);
        var id = ids.Next(IdPrefix.Template);
        var target = templates.NewProjectFolder(id);
        var written = WriteCopy(target, hostStates, progress, token, name, new(false));
        var state = written.State;
        templates.Describe(
            target,
            new(id, name, category, state.SampleRate, state.Tracks.Count, !state.Files.IsEmpty, time.GetUtcNow())
        );
        return new(id, name, category, string.Empty, false, state.SampleRate, !state.Files.IsEmpty);
    }

    /// <summary>
    ///     Chosen tracks as a template: with their stack members, the auxes they reach, their plugins and their states, and
    ///     the files those name inside the project. 404 for a track there is not.
    /// </summary>
    public TrackTemplate SaveTrackTemplate(
        SaveTrackTemplateRequest request,
        IReadOnlyDictionary<string, byte[]?>? hostStates
    ) {
        var (name, category) = CheckTemplate(new(request.Name, request.Category));
        if (request.TrackIds is not { Count: > 0 } chosen) {
            throw ProjectException.Unprocessable("Choose the tracks to save.");
        }

        ProjectState project;
        string folder;
        ImmutableDictionary<string, SavedState> states;
        lock (gate) {
            var o = Required();
            if (chosen.FirstOrDefault(t => o.State.FindTrack(t ?? string.Empty) is null) is { } missing) {
                throw ProjectException.NotFound($"No track '{missing}'.");
            }

            (project, folder) = (o.State, o.Folder);
            states = States(o, hostStates);
        }

        var bundle = TrackBundle.Of(project, chosen, new(Regions: false, Automation: false));
        var id = ids.Next(IdPrefix.Template);
        var target = templates.NewTrackFolder(id);
        var keys = ProjectPackage.StateKeys(ProjectDefaults.NewProject(id, name, project.SampleRate) with { Tracks = bundle.Tracks })
            .ToHashSet(StringComparer.Ordinal);
        try {
            ProjectCopy.Build(
                target,
                build => {
                    var media = ProjectMedia.Copy(folder, build, bundle.Tracks, []);
                    var state = ProjectDefaults.NewProject(id, name, project.SampleRate) with {
                        Tracks = [..media.Apply(bundle.Tracks)],
                        Groups = bundle.Groups.ToImmutableSortedDictionary(static g => g.Number, static g => g),
                        ArticulationSets = bundle.ArticulationSets.ToImmutableSortedDictionary(
                            static a => a.Id,
                            static a => a,
                            StringComparer.Ordinal
                        )
                    };
                    ProjectPackage.Save(build, state);
                    ProjectPackage.SaveStates(
                        build,
                        states.Where(s => keys.Contains(s.Key)).ToDictionary(static s => s.Key, static s => s.Value.Bytes)
                    );
                },
                CancellationToken.None
            );
            templates.Describe(
                target,
                new(id, name, category, project.SampleRate, bundle.Tracks.Count, false, time.GetUtcNow())
            );
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException) {
            throw ProjectException.Unprocessable($"Could not save the template: {e.Message}");
        }

        return new(id, name, category, bundle.Tracks.Count);
    }

    /// <summary>A template's name and category, trimmed: 422 when they cannot be.</summary>
    public static (string Name, string Category) CheckTemplate(SaveTemplateRequest? request) {
        var name = (request?.Name ?? string.Empty).Trim();
        var category = (request?.Category ?? string.Empty).Trim();
        if (name.Length is 0 or > Edits.MaxNameLength) {
            throw ProjectException.Unprocessable($"A template name has 1…{Edits.MaxNameLength} characters.");
        }

        return category.Length > Library.PatchLibrary.MaxCategoryLength
            ? throw ProjectException.Unprocessable(
                $"A template category has at most {Library.PatchLibrary.MaxCategoryLength} characters."
            )
            : (name, category.Length == 0 ? "My Templates" : category);
    }
}
