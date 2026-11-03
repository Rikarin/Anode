using Anode.Contracts;
using Anode.Interchange.Adm;
using Anode.Ipc;
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
///         <b>What an export bounces</b> (decided at integration, docs/11-phase-10.md): two renders through the host. The
///         <b>bed</b> is workstream A's bed-only render of the master (<c>RenderMessage.BedOnly</c>): the master as a
///         bounce has it (every bed contribution, aux returns included, through the master's inserts and fader) without
///         its objects, in the project's format. The <b>objects</b> are a stem render of the object tracks asked for:
///         each its post-fader signal <i>before</i> the master's object renderer (its own mono or stereo channels),
///         through the master's fader (its volume, silence when muted) but not its inserts, which a renderer cannot
///         apply to one object; so the objects of a project with master inserts are exported without them. Every source
///         given to D's writer is a plain 32-bit float WAV in layout order (the host's extensible renders are read back
///         into it by <see cref="BounceProcessor.ReadLayout" />) of exactly the scene's frames, at the project's rate.
///         Tracks to a device output pair are not in the master and not in the file.
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
        CheckAdmExport(state, request!);
        (long Start, long End) range;
        try {
            range = AdmMapping.Range(state, request!.StartTick, request.EndTick);
        } catch (ArgumentException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        var plan = new AdmPlan(
            view,
            request,
            path,
            range.Start,
            range.End,
            AdmMapping.ObjectTracks(state, request.TrackIds)
        );
        return Enqueue(JobKind.AdmExport, (entry, token) => ExportAdmAsync(entry, plan, token));
    }

    /// <summary>An export's bit depth and its tracks: objects of the project.</summary>
    static void CheckAdmExport(ProjectState state, AdmExportRequest request) {
        if (request.BitDepth is not (24 or 32)) {
            throw ProjectException.Unprocessable($"An ADM BWF is 24-bit or 32-bit float, not {request.BitDepth}.");
        }

        foreach (var id in request.TrackIds ?? []) {
            var track = state.FindTrack(id ?? string.Empty) ?? throw ProjectException.NotFound($"No track '{id}'.");
            if (track.Strip.Panner.Mode != PannerMode.Object) {
                throw ProjectException.Unprocessable($"Track '{track.Name}' is not an object.");
            }
        }
    }

    /// <summary>An ADM export as it was asked for: the project then, the range, the objects.</summary>
    sealed record AdmPlan(
        OpenProjectView View,
        AdmExportRequest Request,
        string Path,
        long Start,
        long End,
        IReadOnlyList<Track> Objects
    ) {
        public ProjectState State => View.State;

        public long Frames => End - Start;

        /// <summary>The master's fader as a gain (0 when it is muted).</summary>
        public float Gain => State.Master.Mute ? 0 : Units.DbToGain(State.Master.VolumeDb);
    }

    async Task<IReadOnlyList<string>> ExportAdmAsync(Entry entry, AdmPlan plan, CancellationToken token) {
        var scratch = Path.Combine(plan.View.Folder, $".adm-{entry.Job.Id}");
        Directory.CreateDirectory(scratch);
        try {
            var current = session.View is { } v && v.Session == plan.View.Session
                ? v
                : throw ProjectException.Conflict("The project was closed.");
            var rate = plan.State.SampleRate;
            var format = plan.State.Settings.SurroundFormat;

            // The bed: the master without its objects (workstream A's bed-only render), in the master's layout.
            var bedRender = Path.Combine(scratch, "bed-render.wav");
            var objectShare = plan.Objects.Count > 0 ? 0.4 : 0;
            await RenderAsync(
                    entry,
                    new(entry.EngineId, plan.Start, plan.End, bedRender, BedOnly: true),
                    0,
                    0.8 - objectShare,
                    token
                )
                .ConfigureAwait(false);
            var (bed, _) = BounceProcessor.Read(bedRender);
            if (bed.Length != SpeakerLayouts.ChannelCount(format)) {
                throw ProjectException.Unprocessable(
                    $"The engine rendered a bed of {bed.Length} channels for a {SpeakerLayouts.Of(format).Name} project."
                );
            }

            var bedPath = Path.Combine(scratch, "bed.wav");
            WriteFloat(bedPath, Exactly(bed, bed.Length, plan.Frames), rate, 1);

            // The objects: their stems before the master's renderer, through the master's fader.
            var objectPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (plan.Objects.Count > 0) {
                var stems = Path.Combine(scratch, "stems");
                await RenderAsync(
                        entry,
                        new(
                            entry.EngineId,
                            plan.Start,
                            plan.End,
                            stems,
                            [..plan.Objects.Select(t => current.Ids.Track(t.Id))]
                        ),
                        0.8 - objectShare,
                        objectShare,
                        token
                    )
                    .ConfigureAwait(false);
                foreach (var track in plan.Objects) {
                    token.ThrowIfCancellationRequested();
                    var (audio, _) = BounceProcessor.Read(Path.Combine(stems, $"{current.Ids.Track(track.Id)}.wav"));
                    var file = Path.Combine(scratch, $"object-{objectPaths.Count + 1}.wav");
                    WriteFloat(file, Exactly(audio, AdmMapping.ObjectChannels(track), plan.Frames), rate, plan.Gain);
                    objectPaths[track.Id] = file;
                }
            }

            Progress(entry, 0.85);
            var scene = AdmMapping.ToScene(plan.State, plan.Request, bedPath, objectPaths);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Path)!);
            spatial.WriteAdm(scene, plan.Path, plan.Request.BitDepth);
            return [plan.Path];
        } finally {
            RemoveScratch(scratch);
        }
    }

    /// <summary>One render through the host for an export, its progress mapped onto [from, from + share].</summary>
    async Task RenderAsync(Entry entry, RenderMessage message, double from, double share, CancellationToken token) {
        var finished = await engine.RenderAsync(message, f => Progress(entry, from + share * f), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (finished.Error is { } error) {
            throw ProjectException.Unprocessable($"The engine could not render: {error}");
        }
    }

    /// <summary>
    ///     <paramref name="channels" /> channels of exactly <paramref name="frames" /> (the scene's length, which D's
    ///     writer requires of every source): the render's first ones, cut or padded with silence, a mono stem doubled
    ///     for a stereo object.
    /// </summary>
    static float[][] Exactly(float[][] stem, int channels, long frames) {
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
                        var scene = ReadAdm(path, media);
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
                            (job, added) => ApplyAdm(job, scene, sources, added)
                        );
                        return [..files.Select(static f => f.Path)];
                    } catch {
                        copied.ForEach(TryDelete);
                        throw;
                    } finally {
                        RemoveScratch(media);
                    }
                },
                token
            )
        );
    }

    AdmScene ReadAdm(string path, string media) {
        try {
            return spatial.ReadAdm(path, media);
        } catch (FormatException e) {
            throw ProjectException.Unprocessable($"'{path}' is not an ADM BWF Anode reads: {e.Message}");
        }
    }

    /// <summary>
    ///     The import's edit: the first bed's surround format for a project narrower than surround, then the tracks
    ///     (<paramref name="added" /> are the files of <paramref name="sources" />, in order).
    /// </summary>
    static void ApplyAdm(JobEdit job, AdmScene scene, string[] sources, IReadOnlyList<AudioFile> added) {
        if (scene.Beds is [var first, ..]
            && SpeakerLayouts.IsSurround(first.Format)
            && !SpeakerLayouts.IsSurround(job.State.Settings.SurroundFormat)) {
            job.Apply(new SetSurroundFormatCommand(first.Format));
        }

        var fileIds = sources.Select((s, i) => (s, added[i].Id))
            .ToDictionary(static p => p.s, static p => p.Id, StringComparer.Ordinal);
        job.Apply(new ImportTracksCommand(AdmMapping.ToContent(scene, job.State, fileIds)));
    }
}
