using Anode.Contracts;
using Anode.Generative;
using Anode.Model;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.Time;
using System.Collections.Concurrent;

namespace Anode.Server.Projects;

/// <summary>
///     Session Player regions' notes (docs/12-phase-11.md, "Generation is deterministic"): each region's generation
///     input gathered from the project — its parameters, seed, place and length; the chords and key and scale changes,
///     arrangement sections, signatures and key track over its range (with the ones sounding at its start); its rhythm
///     source's onsets over its range — generated through <see cref="ISessionPlayerLibrary" /> and cached by the input's
///     hash, so a region regenerates exactly when something it depends on changes. A generator that fails gives the
///     region no notes (logged once per input).
/// </summary>
public sealed partial class SessionPlayers(ISessionPlayerLibrary library, ILogger<SessionPlayers> logger) {
    /// <summary>The most generated regions kept; more and the cache starts over.</summary>
    public const int MaxCached = 4096;

    readonly ConcurrentDictionary<string, GeneratedNotes> cache = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> failed = new(StringComparer.Ordinal);
    int generations;

    /// <summary>How many times the generator ran (a cached region does not run it).</summary>
    public int Generations => Volatile.Read(ref generations);

    /// <summary>The generated notes of every Session Player region of <paramref name="view" />, by region id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Note>> Played(OpenProjectView view) {
        var played = new Dictionary<string, IReadOnlyList<Note>>(StringComparer.Ordinal);
        foreach (var region in view.State.SessionPlayerRegions.Values) {
            try {
                played[region.Id] = Notes(view, region).Notes;
            } catch (Exception e) when (e is not OutOfMemoryException) {
                Failed(region, e);
            }
        }

        return played;
    }

    /// <summary>A region's notes and their input's hash: cached, else generated now.</summary>
    public GeneratedNotes Notes(OpenProjectView view, SessionPlayerRegion region) {
        var input = Input(view.State, region, view.Analyses);
        var hash = library.Hash(input);
        if (cache.TryGetValue(hash, out var notes)) {
            return notes;
        }

        Interlocked.Increment(ref generations);
        notes = library.Generate(input);
        if (cache.Count >= MaxCached) {
            cache.Clear();
        }

        return cache.GetOrAdd(hash, notes);
    }

    void Failed(SessionPlayerRegion region, Exception e) {
        if (failed.TryAdd($"{region.Id}|{region.Seed}|{e.Message}", 0)) {
            GenerationFailed(logger, region.Id, e.Message);
        }
    }

    /// <summary>
    ///     What region <paramref name="region" />'s notes depend on: only what lies over its range, with the chord, key
    ///     and scale change, section, signature and key sounding at its start, so a change elsewhere leaves its hash.
    /// </summary>
    public static GenerationInput Input(
        ProjectState state,
        SessionPlayerRegion region,
        IReadOnlyDictionary<string, FileAnalysis> analyses
    ) {
        var (start, end) = (region.StartTick, region.StartTick + region.LengthTicks);
        var player = Edits.PlayerOf(state.FindTrack(region.TrackId)?.Kind ?? TrackKind.Drummer)
            ?? SessionPlayerKind.Drummer;
        var chords = state.ChordTrack;
        var settings = state.Settings;
        return new(
            player,
            region.Parameters,
            region.Seed,
            start,
            region.LengthTicks,
            new(
                [..Over(chords.Chords, static c => c.Tick, start, end)],
                [..Over(chords.Scales, static s => s.Tick, start, end)]
            ),
            [
                ..state.Sections.Values.Where(s => s.StartTick < end && s.StartTick + s.LengthTicks > start)
                    .Concat(
                        state.Sections.Values.Where(s => s.StartTick + s.LengthTicks <= start)
                            .OrderBy(static s => s.StartTick)
                            .TakeLast(1)
                    )
                    .OrderBy(static s => s.StartTick)
                    .ThenBy(static s => s.Id, StringComparer.Ordinal)
            ],
            [
                ..Over(
                    settings.Signatures is { Count: > 0 } signatures
                        ? signatures
                        : [new(0, settings.Signature.Numerator, settings.Signature.Denominator)],
                    static s => s.Tick,
                    start,
                    end
                )
            ],
            [..Over(settings.Keys ?? [], static k => k.Tick, start, end)],
            region.Parameters.Drums?.RhythmSourceTrackId is { } source
                ? Onsets(state, source, start, end, analyses)
                : null
        );
    }

    /// <summary>The events in <paramref name="start" />…<paramref name="end" />, and the last one before it.</summary>
    static IEnumerable<T> Over<T>(IReadOnlyList<T> events, Func<T, long> tick, long start, long end) {
        var ordered = events.OrderBy(tick).ToArray();
        var first = Math.Max(0, Array.FindLastIndex(ordered, e => tick(e) <= start));
        for (var i = first; i < ordered.Length && tick(ordered[i]) < end; i++) {
            yield return ordered[i];
        }
    }

    /// <summary>
    ///     A rhythm source's onsets over <paramref name="start" />…<paramref name="end" /> (project ticks, ascending):
    ///     its unmuted MIDI and pattern regions' note starts, or its audio regions' transients (from the files' cached
    ///     analyses; none for a file not analysed yet). Its own Session Player regions are not followed.
    /// </summary>
    public static IReadOnlyList<long> Onsets(
        ProjectState state,
        string trackId,
        long start,
        long end,
        IReadOnlyDictionary<string, FileAnalysis> analyses
    ) {
        var notes = state.MidiRegionsOf(trackId)
            .Where(static r => !r.Muted)
            .SelectMany(static r => r.Notes.Where(n => n.StartTick >= 0 && n.StartTick < r.LengthTicks)
                .Select(n => r.StartTick + n.StartTick)
            );
        var steps = state.PatternRegionsOf(trackId)
            .Where(static r => !r.Muted)
            .SelectMany(r => PatternUnroller.Unroll(r, t => Edits.KeyAt(state, t)).Select(n => r.StartTick + n.Tick));
        return [
            ..notes.Concat(steps)
                .Concat(Transients(state, trackId, analyses))
                .Where(t => t >= start && t < end)
                .Distinct()
                .Order()
        ];
    }

    /// <summary>An audio track's unmuted regions' transients in project ticks (none for a file not analysed yet).</summary>
    static IEnumerable<long> Transients(
        ProjectState state,
        string trackId,
        IReadOnlyDictionary<string, FileAnalysis> analyses
    ) {
        foreach (var region in state.RegionsOf(trackId).Where(static r => !r.Muted)) {
            if (analyses.GetValueOrDefault(region.FileId) is not { } analysis) {
                continue;
            }

            var timing = RegionTiming.Of(state, region);
            foreach (var frame in analysis.Transients.Where(f => f >= region.FileOffset
                         && f < region.FileOffset + region.Length
                     )) {
                yield return (long)Math.Round(timing.TickAt(frame));
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session Player region {RegionId} could not be generated: {Message}"
    )]
    static partial void GenerationFailed(ILogger logger, string regionId, string message);
}
