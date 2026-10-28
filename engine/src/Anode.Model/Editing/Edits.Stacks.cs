using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>Folder track stacks (docs/04-phase-3.md, "Stacks"): a stack's members follow it in the track list.</summary>
public static partial class Edits {
    static List<DeltaOp> CreateStack(ProjectState state, CreateStackCommand c, IIdSource ids) {
        if (c.TrackIds is not { Count: > 0 }) {
            throw new EditException("Name at least one track to stack.");
        }

        if (state.Tracks.Count >= MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        var members = c.TrackIds.Distinct(StringComparer.Ordinal)
            .Select(id => TrackOf(state, id ?? string.Empty))
            .OrderBy(t => state.IndexOfTrack(t.Id))
            .ToArray();
        if (members.Any(static t => t.Kind == TrackKind.Stack)) {
            throw new EditException("Stacks do not nest: a stack cannot go into a stack.");
        }

        var stack = new Track(
            ids.Next(IdPrefix.Track),
            c.Name is null ? NextTrackName(state, TrackKind.Stack) : Name(c.Name, "stack name"),
            TrackKind.Stack,
            ProjectDefaults.Colors[state.Tracks.Count % ProjectDefaults.Colors.Count],
            new(0, false),
            false,
            false,
            ProjectDefaults.Strip,
            null,
            ProjectDefaults.MidiInput,
            null,
            null,
            false,
            string.Empty,
            0,
            AutomationMode.Read,
            null,
            false,
            FlexMode.Polyphonic,
            [],
            null,
            null,
            null,
            false
        );
        // Upserts in final order from the first member's place: the stack, then its members right after it.
        var at = state.IndexOfTrack(members[0].Id);
        var ops = new List<DeltaOp> { new TrackUpserted(stack, at) };
        for (var i = 0; i < members.Length; i++) {
            ops.Add(new TrackUpserted(members[i] with { ParentId = stack.Id }, at + 1 + i));
        }

        return ops;
    }

    /// <summary>The stack goes; its members stay where they are, outside any stack.</summary>
    static List<DeltaOp> RemoveStack(ProjectState state, RemoveStackCommand c) {
        var stack = StackOf(state, c.StackId);
        var ops = new List<DeltaOp>();
        foreach (var member in state.MembersOf(stack.Id)) {
            ops.Add(new TrackUpserted(member with { ParentId = null }, state.IndexOfTrack(member.Id)));
        }

        ops.Add(new TrackRemoved(stack.Id));
        return ops;
    }

    /// <summary>Moves a track into a stack (after its last member) or out of its stack (after the stack's members).</summary>
    static DeltaOp[] SetTrackParent(ProjectState state, SetTrackParentCommand c) {
        var track = TrackOf(state, c.TrackId);
        if (c.ParentId is null) {
            if (track.ParentId is null) {
                return [];
            }

            var rest = state with { Tracks = state.Tracks.RemoveAll(t => t.Id == track.Id) };
            return [new TrackUpserted(track with { ParentId = null }, AfterStack(rest, track.ParentId))];
        }

        if (track.Kind == TrackKind.Stack) {
            throw new EditException("Stacks do not nest: a stack cannot go into a stack.");
        }

        var stack = StackOf(state, c.ParentId);
        if (track.ParentId == stack.Id) {
            return [];
        }

        var without = state with { Tracks = state.Tracks.RemoveAll(t => t.Id == track.Id) };
        return [new TrackUpserted(track with { ParentId = stack.Id }, AfterStack(without, stack.Id))];
    }

    /// <summary>The index just after a stack and its members in <paramref name="state" />'s track list.</summary>
    static int AfterStack(ProjectState state, string stackId) {
        var index = state.IndexOfTrack(stackId);
        for (var i = 0; i < state.Tracks.Count; i++) {
            if (state.Tracks[i].ParentId == stackId) {
                index = Math.Max(index, i);
            }
        }

        return index + 1;
    }

    static Track StackOf(ProjectState state, string? id) {
        var track = TrackOf(state, id ?? string.Empty);
        return track.Kind == TrackKind.Stack ? track : throw new EditException($"Track '{id}' is not a stack.");
    }
}
