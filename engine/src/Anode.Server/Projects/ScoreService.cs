using Anode.Contracts;
using Anode.Model.Editing;

namespace Anode.Server.Projects;

/// <summary>
///     <c>GET /project/score</c> (docs/10-phase-9.md): the open project's score view and MEI through the notation engine
///     (<see cref="INotation" />), cached per project version (the opening and its revision) and request (set, part,
///     view); any change of the project starts a fresh cache. The same project and request give the same document.
/// </summary>
public sealed class ScoreService(ProjectSession session, INotation notation) {
    /// <summary>The most documents kept for one version of the project.</summary>
    public const int MaxCached = 16;

    readonly Lock gate = new();
    readonly Dictionary<(string? Set, string? Part, ScoreViewMode Mode), ScoreDocument> cache = [];
    (long Session, long Revision) version = (-1, -1);

    /// <summary>
    ///     The score of set <paramref name="set" /> (null: every MIDI track) or track <paramref name="part" />'s part, in
    ///     view <paramref name="view" /> (<c>linear</c>, the default, or <c>page</c>): 404 without a project, 422 for an
    ///     unknown set, track or view.
    /// </summary>
    public ScoreDocument Get(string? set, string? part, string? view) {
        var mode = view switch {
            null or "" or "linear" => ScoreViewMode.Linear,
            "page" => ScoreViewMode.Page,
            _ => throw ProjectException.Unprocessable($"A score view is linear or page, not '{view}'.")
        };
        var open = session.View ?? throw ProjectException.NoProject();
        var state = open.State;
        if (set is not null && !state.ScoreSets.ContainsKey(set)) {
            throw ProjectException.Unprocessable($"No score set '{set}'.");
        }

        if (part is not null && (state.FindTrack(part) is not { } track || !Edits.HoldsMidi(track))) {
            throw ProjectException.Unprocessable($"No MIDI track '{part}'.");
        }

        var key = (set, part, mode);
        lock (gate) {
            if (version != (open.Session, open.Revision)) {
                cache.Clear();
                version = (open.Session, open.Revision);
            } else if (cache.TryGetValue(key, out var cached)) {
                return cached;
            }
        }

        ScoreDocument document;
        try {
            var rendered = notation.Render(state, new(set, part, mode));
            document = new(rendered, notation.Mei(rendered));
        } catch (ArgumentException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        lock (gate) {
            if (version == (open.Session, open.Revision) && cache.Count < MaxCached) {
                cache[key] = document;
            }
        }

        return document;
    }
}
