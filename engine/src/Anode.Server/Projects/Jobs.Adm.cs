using Anode.Contracts;
using Anode.Interchange.Adm;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;

namespace Anode.Server.Projects;

/// <summary>
///     Phase 10's ADM BWF export and import (docs/11-phase-10.md, "ADM BWF"), through <see cref="AdmMapping" /> and
///     <see cref="ISpatialFiles" /> (workstream D's writer and reader).
/// </summary>
/// <remarks>
///     <para>
///         <b>What an export bounces.</b> One render of stems (the engine's existing bounce, <c>tracks</c> set) of every
///         track that outputs to the master: an ordinary strip's stem is its post-fader signal as it reaches the master
///         (panned into the project's format: a bed contribution), an object's stem is its post-fader signal
///         <i>before</i> the master's object renderer (its own mono or stereo channels). The bed is the sum of the bed
///         contributions (aux returns included, the tracks feeding their buses through them), the objects their own
///         stems; both through the master's fader (its volume, and silence when muted). The master's inserts are not in
///         the file: ADM is the renderer's input, as Logic's ADM export of beds and objects; the export of a project with
///         master inserts differs from its bounce by them. Tracks to a device output pair are not in the master and not
///         in the file.
///     </para>
///     <para>
///         <b>Import.</b> D's reader extracts each bed's and object's audio; each is brought into <c>Audio Files</c> (at
///         the project's rate), then one <c>importTracks</c> adds a track per bed (its format, straight through) and per
///         object (object mode, its positions as automation), as one undo step; a stereo or narrower project takes the
///         first bed's surround format first (<c>setSurroundFormat</c>, the same step), so its beds pass straight
///         through and its objects stay objects.
///     </para>
/// </remarks>
public sealed partial class Jobs {
    /// <summary>Queues an ADM BWF export: 404 for an unknown track, 422 for a request that cannot be carried out.</summary>
    public Job ExportAdm(AdmExportRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var path = ExportPath(view, request?.FileName, request?.Directory, ".wav");
        if (request!.BitDepth is not (24 or 32)) {
            throw ProjectException.Unprocessable($"An ADM BWF is 24-bit or 32-bit float, not {request.BitDepth}.");
        }

        if (request.TrackIds is { } chosen) {
            foreach (var id in chosen) {
                var track = state.FindTrack(id ?? string.Empty) ?? throw ProjectException.NotFound($"No track '{id}'.");
                if (track.Strip.Panner.Mode != PannerMode.Object) {
                    throw ProjectException.Unprocessable($"Track '{track.Name}' is not an object.");
                }
            }
        }

        (long Start, long End) range;
        try {
            range = AdmMapping.Range(state, request.StartTick, request.EndTick);
        } catch (ArgumentException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        var feeders = state.Tracks.Where(static t => Edits.HasAudioStrip(t.Kind) && t.Strip.Output == Routing.Master)
            .ToArray();
        var objects = AdmMapping.ObjectTracks(state, request.TrackIds);
        return Enqueue(
            JobKind.AdmExport,
            async (entry, token) => {
                var scratch = Path.Combine(view.Folder, $".adm-{entry.Job.Id}");
                Directory.CreateDirectory(scratch);
                try {
                    var current = session.View is { } v && v.Session == view.Session
                        ? v
                        : throw ProjectException.Conflict("The project was closed.");
                    var stems = Path.Combine(scratch, "stems");
                    if (feeders.Length > 0) {
                        var finished = await engine.RenderAsync(
                                new(
                                    entry.EngineId,
                                    range.Start,
                                    range.End,
                                    stems,
                                    [..feeders.Select(t => current.Ids.Track(t.Id))]
                                ),
                                f => Progress(entry, 0.7 * f),
                                token
                            )
                            .ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (finished.Error is { } error) {
                            throw ProjectException.Unprocessable($"The engine could not render: {error}");
                        }
                    }

                    var gain = state.Master.Mute ? 0 : Units.DbToGain(state.Master.VolumeDb);
                    var frames = range.End - range.Start;
                    var format = state.Settings.SurroundFormat;
                    var bed = new float[SpeakerLayouts.ChannelCount(format)][];
                    for (var c = 0; c < bed.Length; c++) {
                        bed[c] = new float[frames];
                    }

                    var objectPaths = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var track in feeders) {
                        token.ThrowIfCancellationRequested();
                        var stem = Path.Combine(stems, $"{current.Ids.Track(track.Id)}.wav");
                        var (audio, _) = BounceProcessor.Read(stem);
                        if (track.Strip.Panner.Mode == PannerMode.Object) {
                            if (objects.Any(o => o.Id == track.Id)) {
                                var file = Path.Combine(scratch, $"object-{objectPaths.Count + 1}.wav");
                                WriteFloat(file, ObjectAudio(audio, AdmMapping.ObjectChannels(track), frames), state.SampleRate, gain);
                                objectPaths[track.Id] = file;
                            }

                            continue;
                        }

                        if (audio.Length != bed.Length) {
                            throw ProjectException.Unprocessable(
                                $"The engine rendered {audio.Length} channels of '{track.Name}' for a {SpeakerLayouts.Of(format).Name} bed."
                            );
                        }

                        for (var c = 0; c < bed.Length; c++) {
                            var source = audio[c];
                            var target = bed[c];
                            for (var i = 0; i < Math.Min(frames, source.Length); i++) {
                                target[i] += source[i];
                            }
                        }
                    }

                    var bedPath = Path.Combine(scratch, "bed.wav");
                    WriteFloat(bedPath, bed, state.SampleRate, gain);
                    Progress(entry, 0.8);
                    var scene = AdmMapping.ToScene(state, request, bedPath, objectPaths);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    spatial.WriteAdm(scene, path, request.BitDepth);
                    return [path];
                } finally {
                    RemoveScratch(scratch);
                }
            }
        );
    }

    /// <summary>An object's channels from its stem: its first ones, a mono stem doubled for a stereo object; <paramref name="frames" /> long.</summary>
    static float[][] ObjectAudio(float[][] stem, int channels, long frames) {
        var audio = new float[channels][];
        for (var c = 0; c < channels; c++) {
            audio[c] = new float[frames];
            if (stem.Length > 0) {
                var source = stem[Math.Min(c, stem.Length - 1)];
                Array.Copy(source, audio[c], Math.Min(frames, source.Length));
            }
        }

        return audio;
    }

    /// <summary>A plain (not extensible) 32-bit float WAV, channels in layout order, at <paramref name="gain" />.</summary>
    static void WriteFloat(string path, float[][] channels, int rate, float gain) {
        using var writer = new WavWriter(path, channels.Length, rate, WavEncoding.Float32);
        var frames = channels.Length == 0 ? 0 : channels[0].Length;
        var block = new float[4096 * channels.Length];
        for (var at = 0; at < frames;) {
            var n = Math.Min(4096, frames - at);
            for (var i = 0; i < n; i++) {
                for (var c = 0; c < channels.Length; c++) {
                    block[i * channels.Length + c] = channels[c][at + i] * gain;
                }
            }

            writer.Write(block.AsSpan(0, n * channels.Length));
            at += n;
        }
    }

    /// <summary>Queues an ADM BWF import from the server's disk: 422 when there is no such file.</summary>
    public Job ImportAdm(AdmImportRequest request) {
        var open = session.View ?? throw ProjectException.NoProject();
        var path = ImportPath(request?.Path);
        return Enqueue(
            JobKind.AdmImport,
            (entry, token) => Task.Run(
                IReadOnlyList<string>() => {
                    var media = Path.Combine(open.Folder, $".adm-{entry.Job.Id}");
                    Directory.CreateDirectory(media);
                    var copied = new List<string>();
                    try {
                        AdmScene scene;
                        try {
                            scene = spatial.ReadAdm(path, media);
                        } catch (FormatException e) {
                            throw ProjectException.Unprocessable($"'{path}' is not an ADM BWF Anode reads: {e.Message}");
                        }

                        Progress(entry, 0.4);
                        var sources = scene.Beds.Select(static b => b.AudioPath)
                            .Concat(scene.Objects.Select(static o => o.AudioPath))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        var files = new List<(string Path, WavInfo Info)>();
                        foreach (var source in sources) {
                            token.ThrowIfCancellationRequested();
                            var (_, brought, info) = import.Bring(
                                AtRate(source, open.State.SampleRate, media),
                                Spatial.MaxChannels
                            );
                            copied.Add(brought);
                            files.Add((brought, info));
                        }

                        Progress(entry, 0.9);
                        session.ApplyJob(
                            open.Session,
                            "Import ADM BWF",
                            files,
                            (job, added) => {
                                if (scene.Beds is [var first, ..]
                                    && SpeakerLayouts.IsSurround(first.Format)
                                    && !SpeakerLayouts.IsSurround(job.State.Settings.SurroundFormat)) {
                                    job.Apply(new SetSurroundFormatCommand(first.Format));
                                }

                                job.Apply(
                                    new ImportTracksCommand(
                                        AdmMapping.ToContent(
                                            scene,
                                            job.State,
                                            sources.Select((s, i) => (s, added[i].Id))
                                                .ToDictionary(static p => p.s, static p => p.Id, StringComparer.Ordinal)
                                        )
                                    )
                                );
                            }
                        );
                        return [..files.Select(static f => f.Path)];
                    } catch {
                        foreach (var file in copied) {
                            TryDelete(file);
                        }

                        throw;
                    } finally {
                        RemoveScratch(media);
                    }
                },
                token
            )
        );
    }
}
