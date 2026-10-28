using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Global tracks (tempo, signature, key), markers, arrangement sections, recording settings and the project's notes
///     (docs/04-phase-3.md).
/// </summary>
public static partial class Edits {
    public const int MaxGlobalEvents = 10_000;
    public const int MaxMarkers = 10_000;
    public const int MaxTextLength = 100_000;
    public const int MaxColorLength = 64;

    static DeltaOp[] SetTempo(ProjectState state, SetTempoCommand c) {
        CheckBpm(c.Bpm);
        var events = TempoEventsOf(state.Settings);
        events[0] = events[0] with { Bpm = c.Bpm };
        return [new SettingsChanged(state.Settings with { Tempo = c.Bpm, TempoEvents = events })];
    }

    static DeltaOp[] SetTempoEvents(ProjectState state, SetTempoEventsCommand c) {
        if (c.Events is not { Count: > 0 and <= MaxGlobalEvents } events) {
            throw new EditException($"A tempo track has 1…{MaxGlobalEvents} events.");
        }

        for (var i = 0; i < events.Count; i++) {
            var e = events[i] ?? throw new EditException("A tempo event is null.");
            CheckEventTick(e.Tick, i == 0 ? null : events[i - 1]?.Tick, "tempo");
            CheckBpm(e.Bpm);
            if (!Enum.IsDefined(e.Curve)) {
                throw new EditException($"Unknown tempo curve {e.Curve}.");
            }
        }

        return [new SettingsChanged(state.Settings with { Tempo = events[0].Bpm, TempoEvents = [..events] })];
    }

    /// <summary>
    ///     Sets the first signature; later changes keep their bar numbers (they move to where those bars now start), as
    ///     Logic keeps them.
    /// </summary>
    static DeltaOp[] SetSignature(ProjectState state, SetSignatureCommand c) {
        CheckSignature(c.Numerator, c.Denominator);
        var old = state.SignatureMap;
        var events = new List<SignatureEvent> { new(0, c.Numerator, c.Denominator) };
        for (var i = 1; i < old.Count; i++) {
            var bar = old.Position(old[i].Tick).Bar;
            var tick = new SignatureMap(events).BarStart(bar);
            CheckTick(tick, "signature");
            events.Add(old[i] with { Tick = tick });
        }

        return [
            new SettingsChanged(
                state.Settings with { Signature = new(c.Numerator, c.Denominator), Signatures = [..events] }
            )
        ];
    }

    static DeltaOp[] SetSignatures(ProjectState state, SetSignaturesCommand c) {
        if (c.Events is not { Count: > 0 and <= MaxGlobalEvents } events) {
            throw new EditException($"A signature track has 1…{MaxGlobalEvents} events.");
        }

        for (var i = 0; i < events.Count; i++) {
            var e = events[i] ?? throw new EditException("A signature event is null.");
            CheckEventTick(e.Tick, i == 0 ? null : events[i - 1]?.Tick, "signature");
            CheckSignature(e.Numerator, e.Denominator);
            if (i > 0 && (e.Tick - events[i - 1].Tick) % SignatureMap.BarTicks(events[i - 1]) != 0) {
                throw new EditException($"The signature change at tick {e.Tick} is not on a bar line.");
            }
        }

        var first = events[0];
        return [
            new SettingsChanged(
                state.Settings with { Signature = new(first.Numerator, first.Denominator), Signatures = [..events] }
            )
        ];
    }

    static DeltaOp[] SetKeys(ProjectState state, SetKeysCommand c) {
        if (c.Events is not { Count: > 0 and <= MaxGlobalEvents } events) {
            throw new EditException($"A key track has 1…{MaxGlobalEvents} events.");
        }

        for (var i = 0; i < events.Count; i++) {
            var e = events[i] ?? throw new EditException("A key event is null.");
            CheckEventTick(e.Tick, i == 0 ? null : events[i - 1]?.Tick, "key");
            if (e.Tonic is < 0 or > 11) {
                throw new EditException($"A key's tonic is a pitch class 0…11, not {e.Tonic}.");
            }

            if (!Enum.IsDefined(e.Mode)) {
                throw new EditException($"Unknown key mode {e.Mode}.");
            }
        }

        return [new SettingsChanged(state.Settings with { Keys = [..events] })];
    }

    static DeltaOp[] SetRecording(ProjectState state, SetRecordingCommand c) {
        var old = state.Settings.Recording;
        var recording = new RecordingSettings(
            c.CycleMode ?? old.CycleMode,
            c.Replace ?? old.Replace,
            c.Autopunch ?? old.Autopunch,
            c.PunchInTick ?? old.PunchInTick,
            c.PunchOutTick ?? old.PunchOutTick
        );
        if (!Enum.IsDefined(recording.CycleMode)) {
            throw new EditException($"Unknown cycle recording mode {recording.CycleMode}.");
        }

        CheckTick(recording.PunchInTick, "punch-in");
        CheckTick(recording.PunchOutTick, "punch-out");
        if (recording.PunchOutTick <= recording.PunchInTick) {
            throw new EditException("The punch-out locator is after the punch-in locator.");
        }

        return [new SettingsChanged(state.Settings with { Recording = recording })];
    }

    static DeltaOp[] SetProjectNotes(SetProjectNotesCommand c) => [new NotesChanged(Text(c.Text, "project notes"))];

    static DeltaOp[] AddMarker(ProjectState state, AddMarkerCommand c, IIdSource ids) {
        if (state.Markers.Count >= MaxMarkers) {
            throw new EditException($"A project holds at most {MaxMarkers} markers.");
        }

        CheckTick(c.Tick, "marker");
        CheckMarkerLength(c.LengthTicks);
        var marker = new Marker(
            ids.Next(IdPrefix.Marker),
            Name(c.Name ?? string.Empty, "marker name"),
            c.Tick,
            c.LengthTicks,
            c.Color is null ? null : Color(c.Color),
            string.Empty
        );
        return [new MarkerUpserted(marker)];
    }

    static DeltaOp[] SetMarker(ProjectState state, SetMarkerCommand c) {
        var marker = state.Markers.TryGetValue(c.MarkerId ?? string.Empty, out var m)
            ? m
            : throw new EditException($"No marker '{c.MarkerId}'.");
        if (c.Tick is { } tick) {
            CheckTick(tick, "marker");
        }

        if (c.LengthTicks is { } length) {
            CheckMarkerLength(length);
        }

        return [
            new MarkerUpserted(
                marker with {
                    Name = c.Name is null ? marker.Name : Name(c.Name, "marker name"),
                    Tick = c.Tick ?? marker.Tick,
                    LengthTicks = c.LengthTicks ?? marker.LengthTicks,
                    Color = c.Color is null ? marker.Color : Color(c.Color),
                    Text = c.Text is null ? marker.Text : Text(c.Text, "marker text")
                }
            )
        ];
    }

    static DeltaOp[] DeleteMarkers(ProjectState state, DeleteMarkersCommand c) {
        if (c.MarkerIds is not { Count: > 0 }) {
            throw new EditException("Name at least one marker to delete.");
        }

        return [
            ..c.MarkerIds.Distinct(StringComparer.Ordinal)
                .Select(id => id is not null && state.Markers.ContainsKey(id)
                        ? new MarkerRemoved(id)
                        : throw new EditException($"No marker '{id}'.")
                )
        ];
    }

    static DeltaOp[] AddSection(ProjectState state, AddSectionCommand c, IIdSource ids) {
        if (state.Sections.Count >= MaxMarkers) {
            throw new EditException($"A project holds at most {MaxMarkers} sections.");
        }

        var section = new ArrangementMarker(
            ids.Next(IdPrefix.Section),
            Name(c.Name ?? string.Empty, "section name"),
            c.StartTick,
            c.LengthTicks,
            c.Color is null ? ProjectDefaults.SectionColor : Color(c.Color)
        );
        CheckSection(state, section);
        return [new SectionUpserted(section)];
    }

    static DeltaOp[] SetSection(ProjectState state, SetSectionCommand c) {
        var old = SectionOf(state, c.SectionId);
        var section = old with {
            Name = c.Name is null ? old.Name : Name(c.Name, "section name"),
            LengthTicks = c.LengthTicks ?? old.LengthTicks,
            Color = c.Color is null ? old.Color : Color(c.Color)
        };
        CheckSection(state, section);
        return [new SectionUpserted(section)];
    }

    static List<DeltaOp> DeleteSections(ProjectState state, DeleteSectionsCommand c) {
        if (c.SectionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one section to delete.");
        }

        var sections = c.SectionIds.Distinct(StringComparer.Ordinal)
            .Select(id => SectionOf(state, id))
            .OrderByDescending(static s => s.StartTick)
            .ToArray();
        var ops = new List<DeltaOp>();
        foreach (var section in sections) {
            if (!c.RemoveContent) {
                ops.Add(new SectionRemoved(section.Id));
                continue;
            }

            // Later ones first, so an earlier section's range is not moved by a later one's gap closing.
            var (start, end) = (section.StartTick, section.StartTick + section.LengthTicks);
            var removal = new List<DeltaOp>();
            foreach (var item in Timeline.Items(state)) {
                if (item.Start >= start && item.Start < end) {
                    removal.Add(Timeline.Remove(item));
                } else if (item.Start >= end) {
                    removal.Add(Timeline.Move(state, item, item.Start - section.LengthTicks));
                }
            }

            ops.AddRange(removal);
            state = state.Apply(removal);
        }

        return ops;
    }

    /// <summary>
    ///     Moves or copies a section and what starts in it to a section boundary (docs/04-phase-3.md, "Arrangement"):
    ///     the material from there on makes room, and a move closes the gap it leaves.
    /// </summary>
    static List<DeltaOp> MoveSection(ProjectState state, MoveSectionCommand c, IIdSource ids) {
        var section = SectionOf(state, c.SectionId);
        var (start, length) = (section.StartTick, section.LengthTicks);
        var end = start + length;
        var to = c.ToTick;
        if (!state.Sections.Values.Any(s => s.StartTick == to || s.StartTick + s.LengthTicks == to)) {
            throw new EditException($"Tick {to} is not a section boundary.");
        }

        if (!c.Copy && (to == start || to == end)) {
            return []; // already there
        }

        var move = new SectionMove(start, length, to, c.Copy);
        var ops = new List<DeltaOp>();
        var copies = new List<DeltaOp>();
        var next = state.LastEventId;
        foreach (var item in Timeline.Items(state)) {
            var inside = item.Start >= start && item.Start < end;
            if (inside && c.Copy) {
                var copied = Timeline.Copy(state, item, move.Content(item.Start) - item.Start, null, ids, ref next);
                CheckTick(move.Content(item.Start), "position");
                copies.Add(copied);
            }

            var target = inside && !c.Copy ? move.Content(item.Start) : move.Other(item.Start);
            if (target != item.Start) {
                CheckTick(target, "position");
                ops.Add(Timeline.Move(state, item, target));
            }
        }

        ops.AddRange(copies);
        return ops;
    }

    /// <summary>Where a section move or copy takes each position: its own content's, and everything else's.</summary>
    readonly record struct SectionMove(long Start, long Length, long To, bool Copy) {
        long End => Start + Length;

        /// <summary>A position that does not start in the section: after the insertion point it makes room.</summary>
        public long Other(long x) {
            if (Copy) {
                return x >= To ? x + Length : x;
            }

            if (To < Start) {
                return x >= To && x < Start ? x + Length : x;
            }

            return x >= End && x < To ? x - Length : x;
        }

        /// <summary>A position in the section: it goes with it to where it is inserted.</summary>
        public long Content(long x) => (Copy || To < Start ? To : To - Length) + (x - Start);
    }

    static ArrangementMarker SectionOf(ProjectState state, string? id) =>
        state.Sections.TryGetValue(id ?? string.Empty, out var section)
            ? section
            : throw new EditException($"No section '{id}'.");

    /// <summary>A section is inside the project and overlaps no other.</summary>
    static void CheckSection(ProjectState state, ArrangementMarker section) {
        CheckTick(section.StartTick, "section start");
        CheckLength(section.LengthTicks, "section");
        CheckTick(section.StartTick + section.LengthTicks, "section end");
        var end = section.StartTick + section.LengthTicks;
        foreach (var other in state.Sections.Values) {
            if (other.Id != section.Id
                && other.StartTick < end
                && section.StartTick < other.StartTick + other.LengthTicks) {
                throw new EditException($"Section '{section.Name}' would overlap section '{other.Name}'.");
            }
        }
    }

    static void CheckBpm(double bpm) {
        if (!(bpm is >= TempoMap.MinBpm and <= TempoMap.MaxBpm)) {
            throw new EditException($"Tempo {bpm} is outside {TempoMap.MinBpm}…{TempoMap.MaxBpm} BPM.");
        }
    }

    static void CheckSignature(int numerator, int denominator) {
        if (numerator is < 1 or > 32) {
            throw new EditException($"A signature's numerator is 1…32, not {numerator}.");
        }

        if (denominator is not (1 or 2 or 4 or 8 or 16 or 32)) {
            throw new EditException($"A signature's denominator is 1, 2, 4, 8, 16 or 32, not {denominator}.");
        }
    }

    /// <summary>A global track's event: the first at tick 0, each later one after the one before.</summary>
    static void CheckEventTick(long tick, long? previous, string what) {
        if (previous is null ? tick != 0 : tick <= previous) {
            throw new EditException(
                previous is null
                    ? $"The first {what} event is at tick 0."
                    : $"The {what} events are in strictly increasing tick order."
            );
        }

        CheckTick(tick, what);
    }

    static void CheckMarkerLength(long length) {
        if (length is < 0 or > MaxTick) {
            throw new EditException($"A marker lasts 0…{MaxTick} ticks, not {length}.");
        }
    }

    static string Color(string color) =>
        string.IsNullOrWhiteSpace(color) || color.Length > MaxColorLength
            ? throw new EditException($"A colour is a CSS colour of at most {MaxColorLength} characters.")
            : color;

    static string Text(string? text, string what) =>
        text is { Length: <= MaxTextLength }
            ? text
            : throw new EditException($"The {what} hold at most {MaxTextLength} characters.");

    /// <summary>A copy of the tempo track (one event at the project's tempo when it has none).</summary>
    static TempoEvent[] TempoEventsOf(ProjectSettings settings) =>
        settings.TempoEvents is { Count: > 0 } events ? [..events] : [new(0, settings.Tempo, TempoCurve.Step)];
}
