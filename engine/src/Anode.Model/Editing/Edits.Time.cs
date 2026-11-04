using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Insert Time, Cut Time and ripple delete: edits of the timeline itself, across every track. Regions crossing the
///     point are split there (a looped one's repeats become regions first); what lies after moves, with the global
///     tracks (tempo, signature, key, chord), markers, sections, automation, score symbols, the cycle, the punch
///     locators, the project end and the movie.
///     <list type="bullet">
///         <item>
///             Pattern and Session Player regions are not split: one crossing an insertion grows by it, and one crossing
///             a cut shrinks by what it held. So do sections and markers with a length.
///         </item>
///         <item>
///             A tempo ramp across an insertion holds its tempo through the new time; a cut keeps what the tempo, the
///             signature, the key and the chord were at its end, from its start. A signature change that no longer falls
///             on a bar line of the signature before it moves to the nearest one.
///         </item>
///         <item>
///             Ripple delete removes regions and closes the time they took: everything starting after them moves back
///             as a cut's does, and what other tracks hold across that time stays where it is, uncut.
///         </item>
///     </list>
/// </summary>
public static partial class Edits {
    /// <summary>What an edit of time does to positions: <c>Removed</c> ticks from <c>At</c> go, <c>Added</c> arrive.</summary>
    readonly record struct TimeChange(long At, long Removed, long Added) {
        public long End => At + Removed;
        long Delta => Added - Removed;

        /// <summary>Where something starting at <paramref name="tick" /> goes.</summary>
        public long Start(long tick) => tick < At ? tick : tick >= End ? tick + Delta : At;

        /// <summary>Where something ending at <paramref name="tick" /> ends.</summary>
        public long Stop(long tick) => tick <= At ? tick : tick >= End ? tick + Delta : At;

        /// <summary>Whether <paramref name="tick" /> is in the time that goes.</summary>
        public bool Inside(long tick) => tick >= At && tick < End;
    }

    /// <summary>The operations of an edit of time or of a loop; null for any other command.</summary>
    static List<DeltaOp>? TimeOps(ProjectState state, EditCommand command, IIdSource ids) =>
        command switch {
            ConvertLoopsCommand c => ConvertLoops(state, c, ids),
            InsertTimeCommand c => InsertTime(state, c, ids),
            CutTimeCommand c => CutTime(state, c, ids),
            RippleDeleteCommand c => RippleDelete(state, c),
            _ => null
        };

    static List<DeltaOp> InsertTime(ProjectState state, InsertTimeCommand c, IIdSource ids) {
        CheckTick(c.AtTick, "insert");
        CheckLength(c.LengthTicks);
        var change = new TimeChange(c.AtTick, 0, c.LengthTicks);
        var ops = new List<DeltaOp>();
        state = SplitAt(state, c.AtTick, ids, ops);
        ops.AddRange(MovedRegions(state, change, false));
        ops.AddRange(MovedGlobals(state, change));
        return ops;
    }

    static List<DeltaOp> CutTime(ProjectState state, CutTimeCommand c, IIdSource ids) {
        CheckTick(c.StartTick, "cut start");
        CheckTick(c.EndTick, "cut end");
        if (c.EndTick <= c.StartTick) {
            throw new EditException("A cut ends after it starts.");
        }

        var change = new TimeChange(c.StartTick, c.EndTick - c.StartTick, 0);
        var ops = new List<DeltaOp>();
        state = SplitAt(state, c.StartTick, ids, ops);
        state = SplitAt(state, c.EndTick, ids, ops);
        ops.AddRange(MovedRegions(state, change, false));
        ops.AddRange(MovedGlobals(state, change));
        return ops;
    }

    static List<DeltaOp> RippleDelete(ProjectState state, RippleDeleteCommand c) {
        if (c.RegionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one region to delete.");
        }

        var items = c.RegionIds.Distinct(StringComparer.Ordinal)
            .Select(id => Timeline.Region(state, id) ?? throw new EditException($"No region '{id}'."))
            .ToArray();
        var spans = Merged(items.Select(i => (i.Start, PlayedEnd(state, i))));
        var ops = new List<DeltaOp>(items.Select(Timeline.Remove));
        state = state.Apply(ops);
        // Later ones first, so an earlier span is not moved by a later one's closing.
        foreach (var (start, end) in spans.OrderByDescending(static s => s.Start)) {
            var change = new TimeChange(start, end - start, 0);
            var step = MovedRegions(state, change, true);
            step.AddRange(MovedGlobals(state, change));
            ops.AddRange(step);
            state = state.Apply(step);
        }

        return ops;
    }

    /// <summary>Spans joined where they overlap or touch.</summary>
    static List<(long Start, long End)> Merged(IEnumerable<(long Start, long End)> spans) {
        var merged = new List<(long Start, long End)>();
        foreach (var (start, end) in spans.OrderBy(static s => s.Start)) {
            if (merged.Count > 0 && start <= merged[^1].End) {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            } else {
                merged.Add((start, end));
            }
        }

        return merged;
    }

    /// <summary>Where a region's playing ends, in whole ticks: with its repeats, its last frame rounded up.</summary>
    static long PlayedEnd(ProjectState state, TimelineItem item) =>
        item.Kind switch {
            TimelineKind.AudioRegion => Loops.EndTick(state, state.Regions[item.Id]),
            TimelineKind.MidiRegion => Loops.EndTick(state, state.MidiRegions[item.Id]),
            TimelineKind.PatternRegion => Loops.EndTick(state, state.PatternRegions[item.Id]),
            TimelineKind.SessionPlayerRegion => item.Start + state.SessionPlayerRegions[item.Id].LengthTicks,
            _ => item.Start + state.TakeFolders[item.Id].LengthTicks
        };

    /// <summary>Splits what crosses <paramref name="tick" />: loops into regions, then audio, MIDI and take folders.</summary>
    static ProjectState SplitAt(ProjectState state, long tick, IIdSource ids, List<DeltaOp> ops) {
        state = StepInto(state, ops, Unlooped(state, tick, ids));
        foreach (var region in state.Regions.Values.ToArray()) {
            if (SplitFrames(state, region, tick) is > 0 and var at && at < region.Length) {
                state = StepInto(state, ops, Split(region, at, tick, ids));
            }
        }

        foreach (var midi in state.MidiRegions.Values.ToArray()) {
            if (midi.StartTick < tick && tick < midi.StartTick + midi.LengthTicks) {
                state = StepInto(state, ops, SplitMidiRegion(midi, tick, ids));
            }
        }

        foreach (var folder in state.TakeFolders.Values.ToArray()) {
            if (folder.StartTick < tick && tick < folder.StartTick + folder.LengthTicks) {
                state = StepInto(state, ops, SplitFolder(state, folder, tick, ids));
            }
        }

        return state;
    }

    static ProjectState StepInto(ProjectState state, List<DeltaOp> ops, IEnumerable<DeltaOp> step) {
        var list = step.ToArray();
        ops.AddRange(list);
        return state.Apply(list);
    }

    /// <summary>The operations that turn into regions the loops playing across <paramref name="tick" />.</summary>
    static List<DeltaOp> Unlooped(ProjectState state, long tick, IIdSource ids) {
        var ops = new List<DeltaOp>();
        var next = state.LastEventId;
        foreach (var item in Timeline.Items(state)) {
            if (item.Kind is TimelineKind.AudioRegion or TimelineKind.MidiRegion or TimelineKind.PatternRegion
                && item.Start < tick
                && PlayedEnd(state, item) > tick) {
                ops.AddRange(LoopRegions(state, item, ids, ref next));
            }
        }

        return ops;
    }

    /// <summary>
    ///     A take folder as two, of ids of their own for the second, cut at <paramref name="tick" />: its takes and comp
    ///     with it. One whose takes all lie on one side stays whole, and a cut leaves it or removes it by its start.
    /// </summary>
    static IEnumerable<DeltaOp> SplitFolder(ProjectState state, TakeFolder folder, long tick, IIdSource ids) {
        var map = state.TempoMap;
        var rate = state.SampleRate;
        var at = map.SampleAt(tick, rate);
        var left = new List<Take>();
        var right = new List<Take>();
        foreach (var take in folder.Takes) {
            var cut = at - map.SampleAt(take.StartTick, rate);
            if (cut > 0) {
                left.Add(take with { Length = Math.Min(take.Length, cut) });
            }

            if (cut < take.Length) {
                var kept = Math.Max(0, cut);
                right.Add(
                    take with {
                        StartTick = Math.Max(tick, take.StartTick),
                        FileOffset = take.FileOffset + kept,
                        Length = take.Length - kept
                    }
                );
            }
        }

        if (left.Count == 0 || right.Count == 0) {
            return [];
        }

        // A take that ends before the cut or starts after it stays on one side, and its comp segments with it.
        var onLeft = left.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        var onRight = right.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        var first = folder with {
            LengthTicks = tick - folder.StartTick,
            Takes = left,
            Comp = [
                ..folder.Comp.Where(s => s.StartTick < tick && onLeft.Contains(s.TakeId))
                    .Select(s => s with { EndTick = Math.Min(s.EndTick, tick) })
            ]
        };
        var second = Timeline.WithNewIds(
            folder with {
                StartTick = tick,
                LengthTicks = folder.StartTick + folder.LengthTicks - tick,
                Takes = right,
                Comp = [
                    ..folder.Comp.Where(s => s.EndTick > tick && onRight.Contains(s.TakeId))
                        .Select(s => s with { StartTick = Math.Max(s.StartTick, tick) })
                ]
            },
            ids
        );
        return [new TakeFolderUpserted(first), new TakeFolderUpserted(second)];
    }

    /// <summary>
    ///     The regions after a change of time: those that start after it move, those in what a cut removes go. A ripple
    ///     leaves alone whatever does not start after the span it closes.
    /// </summary>
    static List<DeltaOp> MovedRegions(ProjectState state, TimeChange change, bool ripple) {
        var ops = new List<DeltaOp>();
        foreach (var item in Timeline.Items(state)) {
            if (item.Kind is TimelineKind.Marker or TimelineKind.Section) {
                continue;
            }

            var after = item.Start >= change.End;
            if (ripple && !after) {
                continue;
            }

            if (item.Kind is TimelineKind.PatternRegion or TimelineKind.SessionPlayerRegion) {
                ops.AddRange(MovedSpan(state, item, change));
            } else if (after) {
                var target = change.Start(item.Start);
                CheckTick(target, "position");
                ops.Add(Timeline.Move(state, item, target));
            } else if (change.Inside(item.Start)) {
                ops.Add(Timeline.Remove(item));
            }
        }

        return ops;
    }

    /// <summary>A pattern or Session Player region after a change: moved, resized or gone; nothing when it stays.</summary>
    static IEnumerable<DeltaOp> MovedSpan(ProjectState state, TimelineItem item, TimeChange change) {
        if (item.Kind == TimelineKind.PatternRegion) {
            var r = state.PatternRegions[item.Id];
            if (Span(r.StartTick, r.LengthTicks, change) is not var (start, length)) {
                yield return Timeline.Remove(item);
            } else if ((start, length) != (r.StartTick, r.LengthTicks)) {
                yield return new PatternRegionUpserted(r with { StartTick = start, LengthTicks = length });
            }
        } else {
            var r = state.SessionPlayerRegions[item.Id];
            if (Span(r.StartTick, r.LengthTicks, change) is not var (start, length)) {
                yield return Timeline.Remove(item);
            } else if ((start, length) != (r.StartTick, r.LengthTicks)) {
                yield return new SessionPlayerRegionUpserted(r with { StartTick = start, LengthTicks = length });
            }
        }
    }

    /// <summary>A span after a change, or null when nothing of it is left.</summary>
    static (long Start, long Length)? Span(long start, long length, TimeChange change) {
        var (from, to) = (change.Start(start), change.Stop(start + length));
        CheckTick(to, "position");
        return to > from ? (from, to - from) : null;
    }

    /// <summary>What a change of time does to everything but regions.</summary>
    static List<DeltaOp> MovedGlobals(ProjectState state, TimeChange change) {
        var ops = new List<DeltaOp> { new SettingsChanged(Moved(state, change)) };
        ops.AddRange(MovedMarkers(state, change));
        ops.AddRange(MovedSections(state, change));
        foreach (var lane in state.Automation.Values) {
            if (MovedPoints(lane.Points, change) is { Count: > 0 } points) {
                ops.Add(new AutomationLaneUpserted(lane with { Points = points }));
            }
        }

        ops.AddRange(MovedScore(state, change));
        var chords = state.ChordTrack;
        if (chords.Chords.Count + chords.Scales.Count > 0) {
            ops.Add(
                new ChordTrackChanged(
                    new(
                        [
                            ..Shifted(
                                chords.Chords,
                                change,
                                static c => c.Tick,
                                static (c, t) => c with { Tick = t },
                                false
                            )
                        ],
                        [
                            ..Shifted(
                                chords.Scales,
                                change,
                                static s => s.Tick,
                                static (s, t) => s with { Tick = t },
                                false
                            )
                        ]
                    )
                )
            );
        }

        if (state.Movie is { } movie) {
            ops.Add(new MovieChanged(movie with { StartTick = change.Start(movie.StartTick) }));
        }

        return ops;
    }

    static IEnumerable<DeltaOp> MovedMarkers(ProjectState state, TimeChange change) {
        foreach (var marker in state.Markers.Values) {
            if (change.Inside(marker.Tick)) {
                yield return new MarkerRemoved(marker.Id);
            } else if (Span(marker.Tick, marker.LengthTicks + 1, change) is var (start, length)
                       && (start, length - 1) != (marker.Tick, marker.LengthTicks)) {
                yield return new MarkerUpserted(marker with { Tick = start, LengthTicks = length - 1 });
            }
        }
    }

    static IEnumerable<DeltaOp> MovedSections(ProjectState state, TimeChange change) {
        foreach (var section in state.Sections.Values) {
            if (Span(section.StartTick, section.LengthTicks, change) is not var (start, length)) {
                yield return new SectionRemoved(section.Id);
            } else if ((start, length) != (section.StartTick, section.LengthTicks)) {
                yield return new SectionUpserted(section with { StartTick = start, LengthTicks = length });
            }
        }
    }

    /// <summary>Score symbols placed at a tick, and chord symbols; those on notes go with their notes.</summary>
    static IEnumerable<DeltaOp> MovedScore(ProjectState state, TimeChange change) {
        foreach (var symbol in state.Score.Symbols.Where(s => s.Tick >= change.At || s.EndTick > change.At)) {
            yield return change.Inside(symbol.Tick!.Value)
                ? new ScoreSymbolRemoved(symbol.Id)
                : new ScoreSymbolUpserted(Moved(symbol, change));
        }

        foreach (var chord in state.Score.Chords.Where(c => c.Tick >= change.At)) {
            yield return change.Inside(chord.Tick)
                ? new ChordSymbolRemoved(chord.Id)
                : new ChordSymbolUpserted(chord with { Tick = change.Start(chord.Tick) });
        }
    }

    static ScoreSymbol Moved(ScoreSymbol symbol, TimeChange change) =>
        symbol with {
            Tick = change.Start(symbol.Tick!.Value), EndTick = symbol.EndTick is { } end ? change.Stop(end) : null
        };

    /// <summary>The settings after a change: their global tracks, cycle, punch locators and project end.</summary>
    static ProjectSettings Moved(ProjectState state, TimeChange change) {
        var settings = state.Settings;
        var tempo = MovedTempo(state, change);
        var keys = settings.Keys ?? [new KeyEvent(0, 0, KeyMode.Major)];
        var signatures = MovedSignatures(state, change);
        var (cycleStart, cycleLength) = Span(
                settings.Cycle.StartTick,
                settings.Cycle.EndTick - settings.Cycle.StartTick,
                change
            )
            ?? (change.At, settings.Cycle.EndTick - settings.Cycle.StartTick);
        var recording = settings.Recording;
        var (punchIn, punchLength) = Span(
                recording.PunchInTick,
                recording.PunchOutTick - recording.PunchInTick,
                change
            )
            ?? (change.At, recording.PunchOutTick - recording.PunchInTick);
        return settings with {
            Tempo = tempo[0].Bpm,
            TempoEvents = tempo,
            Signature = new(signatures[0].Numerator, signatures[0].Denominator),
            Signatures = signatures,
            Keys = Shifted(keys, change, static k => k.Tick, static (k, t) => k with { Tick = t }, true),
            Cycle = settings.Cycle with { StartTick = cycleStart, EndTick = cycleStart + cycleLength },
            Recording = recording with { PunchInTick = punchIn, PunchOutTick = punchIn + punchLength },
            EndTick = Math.Max(1, change.Stop(settings.EndTick))
        };
    }

    /// <summary>
    ///     A global track's events after a change: in tick order, the one in force at a cut's end from its start.
    ///     <paramref name="anchored" />: the track's first event stays at tick 0.
    /// </summary>
    static List<T> Shifted<T>(
        IReadOnlyList<T> events,
        TimeChange change,
        Func<T, long> tick,
        Func<T, long, T> at,
        bool anchored
    ) where T : class {
        var result = new List<T>();
        var after = new List<T>();
        T? held = null;
        foreach (var e in events) {
            var x = tick(e);
            if (x < change.At || (anchored && x == 0 && change.Removed == 0)) {
                result.Add(e);
            } else if (x >= change.End) {
                after.Add(at(e, change.Start(x)));
            } else {
                held = e;
            }
        }

        if (held is not null && !(after.Count > 0 && tick(after[0]) == change.At)) {
            result.Add(at(held, change.At));
        }

        result.AddRange(after);
        return result;
    }

    /// <summary>The signature track after a change, each change moved to the nearest bar line of the one before.</summary>
    static List<SignatureEvent> MovedSignatures(ProjectState state, TimeChange change) {
        var signatures = state.SignatureMap;
        var events = Enumerable.Range(0, signatures.Count).Select(i => signatures[i]).ToArray();
        var moved = new List<SignatureEvent>();
        foreach (var e in Shifted(events, change, static s => s.Tick, static (s, t) => s with { Tick = t }, true)) {
            if (moved.Count == 0) {
                moved.Add(e);
                continue;
            }

            var previous = moved[^1];
            var bar = SignatureMap.BarTicks(previous);
            var bars = Math.Max(1, (long)Math.Round((e.Tick - previous.Tick) / (double)bar));
            moved.Add(e with { Tick = previous.Tick + bars * bar });
        }

        return moved;
    }

    /// <summary>
    ///     The tempo track after a change. A ramp across an insertion stops at the tempo it had reached, holds it through
    ///     the new time and goes on; after a cut the tempo at its end takes over from its start, a ramp into the cut
    ///     reaching the tempo it had there first. A curved ramp cut in two keeps its shape on both parts, an approximation.
    /// </summary>
    static TempoEvent[] MovedTempo(ProjectState state, TimeChange change) {
        var events = TempoEventsOf(state.Settings);
        var map = state.TempoMap;
        var (at, end) = (change.At, change.End);
        var before = events.Where(e => e.Tick < at).ToList();
        var after = events.Where(e => e.Tick >= end)
            .Select(e => e with { Tick = change.Start(e.Tick) })
            .ToList();
        var previous = before.LastOrDefault();
        var ramping = previous is { Curve: TempoCurve.Ramp } && !ReferenceEquals(previous, events[^1]);
        if (change.Removed == 0) {
            if (at == 0) {
                return [events[0] with { Curve = TempoCurve.Step, Shape = 0 }, ..after];
            }

            if (ramping) {
                var bpm = map.BpmAt(at);
                before.Add(new(at, bpm, TempoCurve.Step));
                if (!events.Any(e => e.Tick == at)) {
                    before.Add(new(at + change.Added, bpm, TempoCurve.Ramp, previous!.Shape));
                }
            }

            return [..before, ..after];
        }

        var exact = events.Any(e => e.Tick == end);
        var holder = events.Last(e => e.Tick <= end);
        if (ramping && at - 1 > previous!.Tick) {
            before.Add(new(at - 1, map.BpmAt(at), TempoCurve.Step));
        }

        if (!exact && (at == 0 || !ReferenceEquals(holder, previous) || ramping)) {
            before.Add(new(at, map.BpmAt(end), holder.Curve, holder.Shape));
        }

        return [..before, ..after];
    }

    /// <summary>
    ///     An automation lane's points after a change: an insertion holds the value it falls on through the new time; a
    ///     cut keeps the values either side of it as a jump where they differ. Empty when it changes nothing.
    /// </summary>
    static IReadOnlyList<AutomationPoint> MovedPoints(IReadOnlyList<AutomationPoint> points, TimeChange change) {
        var (at, end) = (change.At, change.End);
        if (points.Count == 0 || points[^1].Tick < at) {
            return [];
        }

        var before = points.Where(p => p.Tick < at).ToList();
        var after = points.Where(p => p.Tick >= end).Select(p => p with { Tick = change.Start(p.Tick) }).ToList();
        if (change.Removed == 0) {
            if (before.Count > 0 && after.Count > 0) {
                var held = AutomationEvents.LeftAt(points, at);
                before.Add(new(at, held, 0));
                if (points.All(p => p.Tick != at)) {
                    before.Add(new(at + change.Added, held, before[^2].Curve));
                }
            }

            return [..before, ..after];
        }

        var (left, right) = (AutomationEvents.LeftAt(points, at), AutomationEvents.RightAt(points, end));
        if (points.All(p => p.Tick != end)) {
            after.Insert(0, new(at, right, points.LastOrDefault(p => p.Tick < end)?.Curve ?? 0));
        }

        if (before.Count > 0 && left != right && after.Count(p => p.Tick == at) < 2) {
            before.Add(new(at, left, 0));
        }

        return [..before, ..after];
    }
}
