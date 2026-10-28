using Anode.Contracts;

namespace Anode.Model.Compile;

/// <summary>
///     One piece of a comp in samples: <c>Length</c> frames of take <c>TakeId</c>'s file from <c>FileOffset</c> at
///     project sample <c>Start</c>, fading in and out over <c>FadeIn</c> and <c>FadeOut</c> frames (linear: curve 0).
/// </summary>
public sealed record CompPiece(
    string TakeId,
    string FileId,
    long Start,
    long FileOffset,
    long Length,
    long FadeIn,
    long FadeOut
);

/// <summary>
///     Turns a take folder's comp into what plays (docs/04-phase-3.md, "Take folders"): each segment plays its take
///     where the take has material, and where two segments meet, the folder's <c>crossfade</c> frames of crossfade
///     are centred on the boundary (less where a take has no material to extend into, or a segment is short). What
///     the compiler plays and what <c>flattenTakeFolder</c> leaves are the same pieces.
/// </summary>
public static class Comps {
    public static IReadOnlyList<CompPiece> Pieces(ProjectState state, TakeFolder folder) {
        var map = state.TempoMap;
        var rate = state.SampleRate;
        var takes = folder.Takes.ToDictionary(static t => t.Id, StringComparer.Ordinal);
        var spans = new List<(Take Take, long TakeStart, long From, long To)>();
        foreach (var segment in folder.Comp.OrderBy(static s => s.StartTick)) {
            if (!takes.TryGetValue(segment.TakeId, out var take)) {
                continue;
            }

            var takeStart = map.SampleAt(take.StartTick, rate);
            var from = Math.Max(map.SampleAt(segment.StartTick, rate), takeStart);
            var to = Math.Min(map.SampleAt(segment.EndTick, rate), takeStart + take.Length);
            if (to > from) {
                spans.Add((take, takeStart, from, to));
            }
        }

        // Extensions before (into the previous piece) and after (into the next) each piece's span.
        var before = new long[spans.Count];
        var after = new long[spans.Count];
        for (var i = 0; i + 1 < spans.Count; i++) {
            var (left, right) = (spans[i], spans[i + 1]);
            if (left.To != right.From) {
                continue;
            }

            var length = Math.Min(folder.Crossfade, Math.Min(left.To - left.From, right.To - right.From) / 2);
            if (length < 2) {
                continue;
            }

            var boundary = left.To;
            var early = Math.Min(length / 2, boundary - right.TakeStart); // the right take starts earlier
            var late = Math.Min(length - length / 2, left.TakeStart + left.Take.Length - boundary); // the left goes on
            before[i + 1] = Math.Max(0, early);
            after[i] = Math.Max(0, late);
        }

        var pieces = new CompPiece[spans.Count];
        for (var i = 0; i < spans.Count; i++) {
            var (take, takeStart, from, to) = spans[i];
            var start = from - before[i];
            var fadeIn = i > 0 && spans[i - 1].To == from ? before[i] + after[i - 1] : 0;
            var fadeOut = i + 1 < spans.Count && spans[i + 1].From == to ? after[i] + before[i + 1] : 0;
            pieces[i] = new(
                take.Id,
                take.FileId,
                start,
                take.FileOffset + (start - takeStart),
                to + after[i] - start,
                fadeIn,
                fadeOut
            );
        }

        return pieces;
    }
}
