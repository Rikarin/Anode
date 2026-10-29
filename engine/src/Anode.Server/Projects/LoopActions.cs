using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Midi;
using Anode.Model.Storage;
using Anode.Model.Time;
using Anode.Server.Engine;
using Anode.Server.Library;

namespace Anode.Server.Projects;

/// <summary>
///     Loops and the open project (docs/07-phase-6.md, "Loops"): adding a loop (its file copied in; an audio loop as a
///     region that follows the tempo, at the sidecar's tempo, transposed to the project's key; a MIDI loop's notes
///     transposed; a pattern loop as a pattern region; a one-shot unwarped), "Add to Loop Library" from a region, and
///     previewing a loop in sync.
/// </summary>
public sealed class LoopActions(
    ProjectSession session,
    LoopLibrary library,
    AudioImport import,
    EngineConnection engine,
    TransportClock clock
) {
    /// <summary>
    ///     The interval from a loop's key to the project's, −6…+5 semitones (0 when the loop has no key: drums, one-shots).
    /// </summary>
    public static int Interval(int? loopKey, int projectTonic) {
        if (loopKey is not { } key) {
            return 0;
        }

        var up = ((projectTonic - key) % 12 + 12) % 12;
        return up > 5 ? up - 12 : up;
    }

    LoopInfo LoopOf(string id) => library.Find(id) ?? throw ProjectException.NotFound($"No loop '{id}'.");

    /// <summary>Adds a loop to the open project as one undoable step (the file joins the pool first, as imports do).</summary>
    public CommandResult Add(AddLoopRequest request) {
        var loop = LoopOf(request.LoopId);
        if (request.Tick is < 0 or > Edits.MaxTick) {
            throw ProjectException.Unprocessable($"Tick {request.Tick} is outside 0…{Edits.MaxTick}.");
        }

        var state = (session.View ?? throw ProjectException.NoProject()).State;
        var interval = Interval(loop.Key, Edits.KeyAt(state, request.Tick).Tonic);
        var sidecar = LoopFiles.Read(loop.Path)
            ?? throw ProjectException.Unprocessable($"Loop '{loop.Name}' cannot be read.");
        var steps = new List<Func<ProjectState, ProjectState, EditCommand?>>();
        string? trackId = request.TrackId;
        if (trackId is null) {
            var kind = loop.Kind == LoopKind.Audio ? TrackKind.Audio : TrackKind.Instrument;
            steps.Add((_, _) => new AddTrackCommand(loop.Name, Kind: kind));
            steps.Add((now, _) => {
                trackId = now.Tracks[^1].Id;
                return null;
            });
        }

        switch (loop.Kind) {
            case LoopKind.Audio:
                AddAudio(loop, request, interval, steps, () => trackId!);
                break;
            case LoopKind.Midi:
                var (notes, controls, length) = Midi(loop);
                var transposed = notes.Select(n => n with { Pitch = Math.Clamp(n.Pitch + interval, 0, 127) }).ToArray();
                if (request.CellSceneId is { } scene) {
                    steps.Add((_, _) => new SetCellCommand(
                            trackId!,
                            scene,
                            new(Midi: new("x", trackId!, loop.Name, 0, length, ProjectDefaults.Playback, transposed, controls, false)),
                            loop.Name
                        )
                    );
                } else {
                    steps.Add((_, _) => new AddMidiRegionCommand(trackId!, request.Tick, length, loop.Name));
                    steps.Add((now, before) => NewMidi(now, before) is { } id && transposed.Length > 0
                        ? new AddNotesCommand(
                            id,
                            [..transposed.Select(static n => new NewNote(n.Pitch, n.Velocity, n.Channel, n.StartTick, n.LengthTicks))]
                        )
                        : null
                    );
                    steps.Add((now, _) => controls.Length > 0 && LastMidi(now, trackId!, request.Tick) is { } id
                        ? new AddControlsCommand(
                            id,
                            [..controls.Select(static e => new NewControlEvent(e.Kind, e.Channel, e.Number, e.Value, e.Tick))]
                        )
                        : null
                    );
                }

                break;
            default:
                var pattern = Transposed(sidecar.Pattern ?? throw ProjectException.Unprocessable("The pattern loop has no pattern."), interval);
                var patternLength = (long)Math.Round((loop.Beats ?? 4) * TempoMap.Ppq);
                steps.Add((_, _) => request.CellSceneId is { } scene
                    ? new SetCellCommand(
                        trackId!,
                        scene,
                        new(Pattern: new("x", trackId!, loop.Name, 0, patternLength, false, pattern)),
                        loop.Name
                    )
                    : new AddPatternRegionCommand(trackId!, request.Tick, patternLength, pattern, loop.Name)
                );
                break;
        }

        return session.ApplyChain("Add Loop", steps);
    }

    void AddAudio(
        LoopInfo loop,
        AddLoopRequest request,
        int interval,
        List<Func<ProjectState, ProjectState, EditCommand?>> steps,
        Func<string> trackId
    ) {
        var file = import.Import(loop.Path);
        var follows = loop is { OneShot: false, Tempo: > 0 };
        if (follows) {
            // The sidecar's tempo, not analysis: beats every 60/tempo seconds from the file's start.
            var step = 60.0 * file.SampleRate / loop.Tempo!.Value;
            var count = (int)Math.Min(100_000, file.Frames / step + 1);
            long[] beats = [..Enumerable.Range(0, count).Select(i => (long)Math.Round(i * step)).Where(f => f < file.Frames)];
            steps.Add((_, _) => new SetFileTempoCommand(
                    file.Id,
                    new(loop.Tempo.Value, false, beats, [..beats.Where(static (_, i) => i % 4 == 0)])
                )
            );
        }

        if (request.CellSceneId is { } scene) {
            steps.Add((now, _) => {
                var region = new AudioRegion(
                    "x",
                    trackId(),
                    file.Id,
                    loop.Name,
                    0,
                    0,
                    file.Frames,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    follows ? FlexFollow.On : FlexFollow.Off,
                    [],
                    follows ? interval : 0,
                    0,
                    []
                );
                return new SetCellCommand(trackId(), scene, new(Audio: region), loop.Name);
            });
            return;
        }

        steps.Add((_, _) => new AddRegionCommand(trackId(), file.Id, request.Tick));
        steps.Add((now, before) => NewRegion(now, before) is { } id
            ? new SetRegionFlexCommand(id, follows ? FlexFollow.On : FlexFollow.Off, follows ? interval : 0)
            : null
        );
    }

    static string? NewRegion(ProjectState now, ProjectState before) =>
        now.Regions.Keys.FirstOrDefault(id => !before.Regions.ContainsKey(id));

    static string? NewMidi(ProjectState now, ProjectState before) =>
        now.MidiRegions.Keys.FirstOrDefault(id => !before.MidiRegions.ContainsKey(id));

    static string? LastMidi(ProjectState state, string trackId, long tick) =>
        state.MidiRegionsOf(trackId).LastOrDefault(r => r.StartTick == tick)?.Id;

    /// <summary>A MIDI loop's notes and controllers (all its tracks) and its length (its beats, else to the bar).</summary>
    static (Note[] Notes, ControlEvent[] Controls, long Length) Midi(LoopInfo loop) {
        SmfFile file;
        try {
            file = StandardMidiFile.Read(loop.Path);
        } catch (Exception e) when (e is FormatException or IOException) {
            throw ProjectException.Unprocessable($"Loop '{loop.Name}' is not a MIDI file Anode reads: {e.Message}");
        }

        Note[] notes = [..file.Tracks.SelectMany(static t => t.Notes).OrderBy(static n => n.StartTick)];
        ControlEvent[] controls = [..file.Tracks.SelectMany(static t => t.Controls).OrderBy(static e => e.Tick)];
        var end = notes.Length == 0 ? TempoMap.Ppq * 4L : notes.Max(static n => n.StartTick + n.LengthTicks);
        var length = loop.Beats is { } beats and > 0
            ? (long)Math.Round(beats * TempoMap.Ppq)
            : Math.Max(TempoMap.Ppq * 4L, (end + TempoMap.Ppq * 4L - 1) / (TempoMap.Ppq * 4L) * TempoMap.Ppq * 4L);
        return (notes, controls, length);
    }

    /// <summary>A pattern with its note rows moved by <paramref name="interval" /> (pads and chord degrees stay).</summary>
    static Pattern Transposed(Pattern pattern, int interval) =>
        interval == 0
            ? pattern
            : pattern with {
                Rows = [
                    ..pattern.Rows.Select(r => r.Kind == PatternRowKind.Note
                        ? r with { Note = Math.Clamp(r.Note + interval, 0, 127) }
                        : r
                    )
                ]
            };

    /// <summary>
    ///     "Add to Loop Library": saves a region of the open project as a user loop — an audio region's frames as a WAV,
    ///     a MIDI region's notes as a MIDI file, a pattern region as a pattern sidecar — at the project's tempo where the
    ///     region starts (no tempo for a one-shot).
    /// </summary>
    public LoopInfo AddToLibrary(AddToLoopLibraryRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > Edits.MaxNameLength) {
            throw ProjectException.Unprocessable($"A loop's name has 1…{Edits.MaxNameLength} characters.");
        }

        if (request.Key is { } key && key is < 0 or > 11) {
            throw ProjectException.Unprocessable($"A loop's key is a tonic 0…11, not {key}.");
        }

        var tags = (request.Tags ?? []).Where(static t => !string.IsNullOrWhiteSpace(t)).Select(static t => t.Trim()).ToArray();
        var scale = request.Scale ?? LoopScale.Neither;
        LoopSidecar Sidecar(LoopKind kind, long startTick, long lengthTicks, Pattern? pattern = null) {
            var tempo = request.OneShot ? (double?)null : Math.Round(state.TempoMap.BpmAt(startTick), 3);
            return new(
                1,
                kind,
                request.Name.Trim(),
                tempo,
                request.OneShot ? null : Math.Round((double)lengthTicks / TempoMap.Ppq, 3),
                request.Key,
                scale,
                tags,
                request.OneShot,
                null,
                pattern
            );
        }

        var temporary = Path.Combine(Path.GetTempPath(), $"anode-loop-{Guid.NewGuid():n}");
        try {
            if (state.Regions.TryGetValue(request.RegionId, out var audio)) {
                var file = state.Files[audio.FileId];
                WriteFrames(Path.Combine(view.Folder, file.Path), audio.FileOffset, audio.Length, temporary);
                var length = Math.Max(1, Edits.EndTick(state, audio) - audio.StartTick);
                return library.AddUserLoop(request.Name.Trim(), temporary, ".wav", Sidecar(LoopKind.Audio, audio.StartTick, length));
            }

            if (state.MidiRegions.TryGetValue(request.RegionId, out var midi)) {
                File.WriteAllBytes(
                    temporary,
                    StandardMidiFileWriter.Write(
                        [..midi.Notes.Where(n => n.StartTick < midi.LengthTicks)],
                        midi.Controls,
                        state.TempoMap.BpmAt(midi.StartTick),
                        request.Name,
                        midi.LengthTicks
                    )
                );
                return library.AddUserLoop(
                    request.Name.Trim(),
                    temporary,
                    ".mid",
                    Sidecar(LoopKind.Midi, midi.StartTick, midi.LengthTicks)
                );
            }

            if (state.PatternRegions.TryGetValue(request.RegionId, out var pattern)) {
                return library.AddUserLoop(
                    request.Name.Trim(),
                    null,
                    LoopFiles.SidecarSuffix,
                    Sidecar(LoopKind.Pattern, pattern.StartTick, pattern.LengthTicks, pattern.Pattern)
                );
            }

            throw ProjectException.NotFound($"No region '{request.RegionId}'.");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
            throw ProjectException.Unprocessable($"Could not save the loop: {e.Message}");
        } finally {
            if (File.Exists(temporary)) {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Copies <paramref name="length" /> frames of a WAV from <paramref name="offset" /> into a new WAV (float).</summary>
    static void WriteFrames(string source, long offset, long length, string target) {
        using var reader = WavReader.Open(source);
        var info = reader.Info;
        using var writer = new WavWriter(target, info.Channels, info.SampleRate, WavEncoding.Float32);
        var buffer = new float[4096 * info.Channels];
        var skip = offset;
        var left = length;
        while (left > 0) {
            var read = reader.Read(buffer);
            if (read == 0) {
                break;
            }

            var frames = (long)read;
            var from = (int)Math.Min(skip, frames);
            skip -= from;
            var take = (int)Math.Min(frames - from, left);
            if (take > 0) {
                writer.Write(buffer.AsSpan(from * info.Channels, take * info.Channels));
                left -= take;
            }
        }
    }

    /// <summary>
    ///     Previews a loop through the preview channel at the project's tempo (where the playhead is) and key, starting
    ///     on the next bar while the transport plays: audio loops (a one-shot plays as it is).
    /// </summary>
    public void Preview(string id) {
        var loop = LoopOf(id);
        var view = session.View;
        if (loop.Kind != LoopKind.Audio) {
            throw ProjectException.Unprocessable($"Loop '{loop.Name}' is a {loop.Kind.ToString().ToLowerInvariant()} loop: only audio loops preview.");
        }

        var tick = clock.Playing is var (_, _, at) ? at : 0;
        var (ratio, transpose) = view is null || loop.Tempo is not > 0 || loop.OneShot
            ? (1.0, 0)
            : (view.State.PlaybackMap.BpmAt(tick) * view.State.PlaybackSpeed / loop.Tempo!.Value,
                Interval(loop.Key, Edits.KeyAt(view.State, tick).Tonic));
        engine.PreviewSync(new(ratio, transpose, true));
        engine.Preview(loop.Path);
    }
}
