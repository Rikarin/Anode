using Anode.Contracts;
using Anode.Dsp.Analysis.Chords;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Server.Projects;

/// <summary>
///     Phase 11's jobs (docs/12-phase-11.md): Chord ID over MIDI and audio regions (workstream D's recognisers, through
///     <see cref="IChordRecognition" />), the Mastering Assistant's analysis of an offline render of the master before it
///     (workstream E's analysis, <see cref="IMasteringAnalysis" />), and Convert to MIDI or to a pattern of Session
///     Player regions (<see cref="SessionPlayers" />). Each applies its result as one undo step.
/// </summary>
public sealed partial class Jobs {
    /// <summary>Spelled pitch classes: sharps, and the flats a flat key spells with.</summary>
    static readonly PitchName[] Sharps = [
        new(PitchStep.C, 0), new(PitchStep.C, 1), new(PitchStep.D, 0), new(PitchStep.D, 1), new(PitchStep.E, 0),
        new(PitchStep.F, 0), new(PitchStep.F, 1), new(PitchStep.G, 0), new(PitchStep.G, 1), new(PitchStep.A, 0),
        new(PitchStep.A, 1), new(PitchStep.B, 0)
    ];

    static readonly PitchName[] Flats = [
        new(PitchStep.C, 0), new(PitchStep.D, -1), new(PitchStep.D, 0), new(PitchStep.E, -1), new(PitchStep.E, 0),
        new(PitchStep.F, 0), new(PitchStep.G, -1), new(PitchStep.G, 0), new(PitchStep.A, -1), new(PitchStep.A, 0),
        new(PitchStep.B, -1), new(PitchStep.B, 0)
    ];

    /// <summary>
    ///     Queues Chord ID (<c>chordId</c>): 404 for a region that is not a MIDI or audio region of the project, 422 for
    ///     a request that cannot be carried out. The range left out is the regions' span; the resolution a beat.
    /// </summary>
    public Job DetectChords(ChordIdRequest? request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        if (request?.RegionIds is not { Count: > 0 } regionIds) {
            throw ProjectException.Unprocessable("Name at least one MIDI or audio region.");
        }

        var midi = new List<MidiRegion>();
        var audio = new List<AudioRegion>();
        foreach (var id in regionIds.Distinct(StringComparer.Ordinal)) {
            if (state.MidiRegions.TryGetValue(id ?? string.Empty, out var m)) {
                midi.Add(m);
            } else if (state.Regions.TryGetValue(id ?? string.Empty, out var a)) {
                audio.Add(a);
            } else {
                throw ProjectException.NotFound($"No MIDI or audio region '{id}'.");
            }
        }

        var start = request.StartTick
            ?? midi.Select(static r => r.StartTick).Concat(audio.Select(static r => r.StartTick)).Min();
        var end = request.EndTick
            ?? midi.Select(static r => r.StartTick + r.LengthTicks)
                .Concat(audio.Select(r => Edits.EndTick(state, r)))
                .Max();
        if (start < 0 || end > Edits.MaxTick || end <= start) {
            throw ProjectException.Unprocessable("Chord ID's range ends after it starts, inside the project.");
        }

        var resolution = request.ResolutionTicks ?? SignatureMap.BeatTicks(state.SignatureMap.At(start).Denominator);
        if (resolution < 1 || !Enum.IsDefined(request.Write)) {
            throw ProjectException.Unprocessable(
                "Chord ID's resolution is a tick or more, and its write replace, merge or none."
            );
        }

        var plan = new ChordIdPlan(view.Session, view.Folder, state, midi, audio, start, end, resolution, request);
        return Enqueue(JobKind.ChordId, (entry, token) => ChordIdAsync(entry, plan, token));
    }

    async Task<IReadOnlyList<string>> ChordIdAsync(Entry entry, ChordIdPlan plan, CancellationToken token) {
        var result = await Task.Run(() => ChordId(entry, plan, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        session.ApplyJob(plan.Session, "Chord ID", [], (edit, _) => edit.Apply(new ApplyChordIdCommand(result)));
        return [];
    }

    /// <summary>
    ///     What Chord ID finds: the MIDI regions' notes together, on the beats of the range; each audio region's audio
    ///     on the same beats (as file frames); the chords of both in tick order (a MIDI chord first where both start at
    ///     a tick); and, when asked, each region's key. The project's key track is the recognisers' key prior.
    /// </summary>
    ChordIdResult ChordId(Entry entry, ChordIdPlan plan, CancellationToken token) {
        var (state, start, end) = (plan.State, plan.Start, plan.End);
        long[] beats = [
            start,
            ..state.SignatureMap.Beats(end).Select(static b => b.Tick).Where(t => t > start && t < end),
            end
        ];
        var prior = state.Settings.Keys is { Count: > 0 } ? KeyOf(Edits.KeyAt(state, start)) : null;
        var found = new List<DetectedChord>();
        var keys = new List<DetectedKey>();
        if (plan.Midi.Count > 0) {
            var notes = plan.Midi.SelectMany(r => Notes(r, start, end)).OrderBy(static n => n.StartTick).ToArray();
            // Spelled in the key the notes are in (the recognisers give pitch classes only).
            var spelling = notes.Length > 0 ? Flat(recognition.KeyOfNotes(notes)) : false;
            found.AddRange(
                recognition.FromNotes(notes, beats, plan.Resolution, prior)
                    .Select(c => Detected(c, c.Start, c.End, spelling))
            );
            if (plan.Request.DetectKeys) {
                foreach (var region in plan.Midi) {
                    keys.Add(
                        Key(
                            region.Id,
                            recognition.KeyOfNotes(
                                [..Notes(region, region.StartTick, region.StartTick + region.LengthTicks)]
                            )
                        )
                    );
                }
            }
        }

        Progress(entry, 0.3);
        for (var i = 0; i < plan.Audio.Count; i++) {
            token.ThrowIfCancellationRequested();
            FromAudio(plan, plan.Audio[i], beats, prior, found, keys);
            Progress(entry, 0.3 + 0.6 * (i + 1) / plan.Audio.Count);
        }

        return new(
            start,
            end,
            plan.Request.Write,
            [
                ..found.Where(c => c.Tick >= start && c.Tick < end)
                    .DistinctBy(static c => c.Tick)
                    .OrderBy(static c => c.Tick)
            ],
            keys
        );
    }

    /// <summary>
    ///     An audio region's part of the range as Chord ID hears it: its file's frames there, mixed to mono, on the
    ///     range's beats as frames; what it finds back in ticks.
    /// </summary>
    void FromAudio(
        ChordIdPlan plan,
        AudioRegion region,
        long[] beats,
        DetectedMusicalKey? prior,
        List<DetectedChord> found,
        List<DetectedKey> keys
    ) {
        var state = plan.State;
        var timing = RegionTiming.Of(state, region);
        var (from, to) = (Math.Max(plan.Start, region.StartTick), Math.Min(plan.End, Edits.EndTick(state, region)));
        if (to <= from) {
            return;
        }

        var first = (long)Math.Floor(timing.FrameAt(from));
        var last = (long)Math.Ceiling(timing.FrameAt(to));
        var mono = Mono(Path.Combine(plan.Folder, state.Files[region.FileId].Path), first, last);
        long[] frames =
            [..beats.Where(t => t >= from && t <= to).Select(t => (long)Math.Round(timing.FrameAt(t)) - first)];
        var key = recognition.KeyOfAudio(mono, state.SampleRate);
        found.AddRange(
            recognition.FromAudio(mono, state.SampleRate, frames, prior)
                .Select(c => Detected(
                        c,
                        (long)Math.Round(timing.TickAt(first + c.Start)),
                        (long)Math.Round(timing.TickAt(first + c.End)),
                        Flat(key)
                    )
                )
        );
        if (plan.Request.DetectKeys) {
            keys.Add(Key(region.Id, key));
        }
    }

    /// <summary>A MIDI region's notes as Chord ID hears them (in project ticks, cut to the region and the range).</summary>
    static IEnumerable<ChordNote> Notes(MidiRegion region, long start, long end) {
        foreach (var note in region.Notes) {
            var at = region.StartTick + note.StartTick;
            var until = Math.Min(at + note.LengthTicks, Math.Min(region.StartTick + region.LengthTicks, end));
            if (note.StartTick >= 0 && at < end && until > Math.Max(at, start)) {
                var from = Math.Max(at, start);
                yield return new(
                    from,
                    until - from,
                    Math.Clamp(note.Pitch + region.Playback.Transpose, 0, 127),
                    Math.Clamp(note.Velocity + region.Playback.Velocity, 1, 127)
                );
            }
        }
    }

    /// <summary>File frames <paramref name="from" />…<paramref name="to" /> of a WAV, mixed to mono (silence past its end).</summary>
    static float[] Mono(string path, long from, long to) {
        from = Math.Max(0, from);
        var mono = new float[Math.Max(0, to - from)];
        using var reader = WavReader.Open(path);
        var channels = reader.Info.Channels;
        var buffer = new float[4096 * channels];
        long frame = 0;
        int read;
        while (frame < to && (read = reader.Read(buffer)) > 0) {
            for (var i = 0; i < read; i++, frame++) {
                if (frame >= from && frame < to) {
                    float sum = 0;
                    for (var c = 0; c < channels; c++) {
                        sum += buffer[i * channels + c];
                    }

                    mono[frame - from] = sum / channels;
                }
            }
        }

        return mono;
    }

    /// <summary>
    ///     A recognised chord as the chord track takes it, its root and slash bass spelled with flats or sharps as the
    ///     detected key spells.
    /// </summary>
    static DetectedChord Detected(RecognisedChord chord, long start, long end, bool flats) {
        var quality = chord.Quality >= 0 && chord.Quality < ChordQualities.All.Count
            ? ChordQualities.All[chord.Quality]
            : ChordQualities.Major;
        var root = (chord.Root % 12 + 12) % 12;
        return new(
            start,
            Math.Max(1, end - start),
            Spell(root, flats),
            quality,
            chord.Bass is >= 0 and <= 11 && chord.Bass != root ? Spell(chord.Bass, flats) : null,
            Math.Clamp(chord.Confidence, 0, 1)
        );
    }

    static DetectedKey Key(string regionId, DetectedMusicalKey key) {
        var tonic = (key.Tonic % 12 + 12) % 12;
        var mode = key.Minor ? KeyMode.Minor : KeyMode.Major;
        return new(
            regionId,
            new(Spell(tonic, Flat(new KeyEvent(0, tonic, mode))), mode),
            Math.Clamp(key.Confidence, 0, 1)
        );
    }

    static DetectedMusicalKey KeyOf(KeyEvent key) => new((key.Tonic % 12 + 12) % 12, key.Mode == KeyMode.Minor, 1);

    static bool Flat(DetectedMusicalKey key) =>
        Flat(new KeyEvent(0, key.Tonic, key.Minor ? KeyMode.Minor : KeyMode.Major));

    /// <summary>Whether a key spells with flats: F, B♭, E♭, A♭ and D♭ major; D, G, C, F, B♭ and E♭ minor.</summary>
    static bool Flat(KeyEvent key) =>
        key.Mode == KeyMode.Minor
            ? (key.Tonic % 12 + 12) % 12 is 2 or 7 or 0 or 5 or 10 or 3
            : (key.Tonic % 12 + 12) % 12 is 5 or 10 or 3 or 8 or 1;

    static PitchName Spell(int pitchClass, bool flats) => (flats ? Flats : Sharps)[pitchClass];

    /// <summary>
    ///     Queues the Mastering Assistant's analysis (<c>masteringAnalysis</c>): 404 when the master has none and
    ///     <c>add</c> is off, 422 for a range that is not one. The range left out is the cycle when it is on, else the
    ///     song.
    /// </summary>
    public Job AnalyseMastering(MasteringAnalysisRequest? request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        request ??= new();
        var assistant = Edits.MasteringAssistantOf(state);
        if (assistant is null && !request.Add) {
            throw ProjectException.NotFound("The master has no Mastering Assistant.");
        }

        if (assistant is null && state.Master.Inserts.Count >= Edits.MaxInserts) {
            throw ProjectException.Unprocessable(
                $"The master holds {Edits.MaxInserts} inserts: none is left for the assistant."
            );
        }

        var cycle = state.Settings.Cycle;
        var (start, end) = request.StartTick is null && request.EndTick is null && cycle.Enabled
            ? (cycle.StartTick, cycle.EndTick)
            : (request.StartTick ?? 0, request.EndTick ?? state.Settings.EndTick);
        var (from, to) = Range(state, start, end);
        var plan = new MasteringPlan(view.Session, view.Folder, view.Revision, assistant?.Id, start, end, from, to);
        return Enqueue(JobKind.MasteringAnalysis, (entry, token) => MasteringAsync(entry, plan, token));
    }

    async Task<IReadOnlyList<string>> MasteringAsync(Entry entry, MasteringPlan plan, CancellationToken token) {
        var scratch = Path.Combine(plan.Folder, $".mastering-{entry.Job.Id}");
        Directory.CreateDirectory(scratch);
        try {
            var path = Path.Combine(scratch, "master.wav");
            var finished = await engine.RenderBeforeAsync(
                    plan.InsertId,
                    new(entry.EngineId, plan.StartSample, plan.EndSample, path),
                    f => Progress(entry, 0.8 * f),
                    token
                )
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (finished.Error is { } error) {
                throw ProjectException.Unprocessable($"The engine could not render: {error}");
            }

            var (audio, rate) = BounceProcessor.Read(path);
            var (left, right) = (audio[0], audio.Length > 1 ? audio[1] : audio[0]);
            var character =
                session.View?.State is { } now && Edits.MasteringAssistantOf(now, plan.InsertId) is { } insert
                    ? Edits.MasteringSettingsOf(insert).Character
                    : MasteringSettings.Default.Character;
            var measured = mastering.Analyse(left, right, rate);
            var analysis = new MasteringAnalysis(
                plan.StartTick,
                plan.EndTick,
                plan.Revision,
                measured.IntegratedLufs,
                measured.MaxShortTermLufs,
                measured.LoudnessRange,
                measured.TruePeakDbtp,
                measured.CrestFactorDb,
                measured.Width,
                [..measured.SpectrumDb.Select(static v => (double)v)],
                [..mastering.Curve(character).Select(static v => (double)v)]
            );
            Progress(entry, 0.95);
            session.ApplyJob(
                plan.Session,
                "Analyse Mastering",
                [],
                (edit, _) => {
                    var id = Edits.MasteringAssistantOf(edit.State, plan.InsertId)?.Id;
                    if (id is null) {
                        // None on the master (or it went meanwhile): one last, made now.
                        var (op, added) = Edits.AddMasteringAssistant(edit.State, edit.Ids);
                        edit.Step([op]);
                        id = added.Id;
                    }

                    edit.Apply(new SetMasteringAnalysisCommand(id, analysis));
                }
            );
            return [];
        } finally {
            try {
                Directory.Delete(scratch, true);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                ProjectLog.JobFailed(logger, entry.Job.Id, $"could not remove {scratch}: {e.Message}");
            }
        }
    }

    /// <summary>
    ///     Queues Convert to MIDI or to a pattern (<c>sessionPlayerConvert</c>) of a Session Player region and those
    ///     selected with it: 404 for one that is not a Session Player region, 422 for a pattern from another player than
    ///     the Drummer.
    /// </summary>
    public Job ConvertSessionPlayer(string regionId, SessionPlayerConvertRequest? request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        request ??= new();
        if (!Enum.IsDefined(request.Target)) {
            throw ProjectException.Unprocessable($"Unknown conversion {request.Target}.");
        }

        string[] ids = [..new[] { regionId }.Concat(request.RegionIds ?? []).Distinct(StringComparer.Ordinal)];
        foreach (var id in ids) {
            if (!state.SessionPlayerRegions.TryGetValue(id ?? string.Empty, out var region)) {
                throw ProjectException.NotFound($"No Session Player region '{id}'.");
            }

            if (request.Target == SessionPlayerConvertTarget.Pattern
                && state.FindTrack(region.TrackId)?.Kind != TrackKind.Drummer) {
                throw ProjectException.Unprocessable(
                    $"Region '{region.Name}' is not a Drummer's: it converts to MIDI only."
                );
            }
        }

        var (session1, target) = (view.Session, request.Target);
        return Enqueue(
            JobKind.SessionPlayerConvert,
            (entry, token) => ConvertAsync(entry, session1, ids, target, token)
        );
    }

    async Task<IReadOnlyList<string>> ConvertAsync(
        Entry entry,
        long sessionId,
        IReadOnlyList<string> regionIds,
        SessionPlayerConvertTarget target,
        CancellationToken token
    ) {
        var view = session.View is { } v && v.Session == sessionId
            ? v
            : throw ProjectException.Conflict("The project was closed.");
        var converted = await Task.Run(
                () => regionIds.Select(id => {
                            token.ThrowIfCancellationRequested();
                            var region = view.State.SessionPlayerRegions.TryGetValue(id, out var r)
                                ? r
                                : throw ProjectException.Conflict($"Region '{id}' went while the job waited.");
                            var notes = players.Notes(view, region).Notes;
                            return target == SessionPlayerConvertTarget.Pattern
                                ? new ConvertedSessionPlayerRegion(
                                    id,
                                    target,
                                    [],
                                    library.ToPattern(notes, region.LengthTicks)
                                )
                                : new ConvertedSessionPlayerRegion(
                                    id,
                                    target,
                                    [
                                        ..notes.Select(static n => new NewNote(
                                                n.Pitch,
                                                n.Velocity,
                                                n.Channel,
                                                n.StartTick,
                                                n.LengthTicks,
                                                n.ArticulationId == 0 ? null : n.ArticulationId
                                            )
                                        )
                                    ],
                                    null
                                );
                        }
                    )
                    .ToArray(),
                token
            )
            .ConfigureAwait(false);
        session.ApplyJob(
            sessionId,
            target == SessionPlayerConvertTarget.Pattern ? "Convert to Pattern" : "Convert to MIDI",
            [],
            (edit, _) => edit.Apply(new ConvertSessionPlayerRegionsCommand(converted))
        );
        return [];
    }

    sealed record ChordIdPlan(
        long Session,
        string Folder,
        ProjectState State,
        IReadOnlyList<MidiRegion> Midi,
        IReadOnlyList<AudioRegion> Audio,
        long Start,
        long End,
        long Resolution,
        ChordIdRequest Request
    );

    sealed record MasteringPlan(
        long Session,
        string Folder,
        long Revision,
        string? InsertId,
        long StartTick,
        long EndTick,
        long StartSample,
        long EndSample
    );
}
