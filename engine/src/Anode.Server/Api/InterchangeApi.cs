using Anode.Contracts;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 9 (docs/10-phase-9.md): MusicXML, AAF and Standard MIDI File export and import, as jobs. Exports write files
///     as the bounce does (the job's files when done); imports read a file from the server's disk (the desktop app) or an
///     upload (a browser on the network), copy its media into the project and add its tracks in one undo step.
/// </summary>
static class InterchangeApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Interchange").AddEndpointFilter<ProblemFilter>();
        project.MapPost(
                "/export/musicxml",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MusicXmlExportRequest request, Jobs jobs) =>
                    Accepted(jobs.ExportMusicXml(request))
            )
            .WithName("ExportMusicXml")
            .WithSummary(
                "Exports a score set or a part as MusicXML 4.0 (.musicxml, or .mxl compressed): a musicXmlExport job."
            );
        project.MapPost(
                "/export/aaf",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(AafExportRequest request, Jobs jobs) =>
                    Accepted(jobs.ExportAaf(request))
            )
            .WithName("ExportAaf")
            .WithSummary(
                "Exports audio tracks as AAF, media embedded or linked (WAV files in a folder beside it): an aafExport job."
            );
        project.MapPost(
                "/export/midi",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MidiExportRequest request, Jobs jobs) =>
                    Accepted(jobs.ExportMidi(request))
            )
            .WithName("ExportMidi")
            .WithSummary(
                "Exports the project, tracks or a region as a Standard MIDI File (type 0 or 1): a midiExport job."
            );
        project.MapPost(
                "/import/musicxml",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MusicXmlImportRequest request, Jobs jobs) =>
                    Accepted(jobs.ImportMusicXml(request))
            )
            .WithName("ImportMusicXml")
            .WithSummary(
                "Imports a MusicXML file from the server's disk as tracks with their score symbols: a musicXmlImport job, "
                + "one undo step when it finishes."
            );
        project.MapPost(
                "/import/musicxml/upload",
                static async Task<Results<Accepted<Job>, NotFound, ProblemHttpResult>>(
                    IFormFile file,
                    Jobs jobs,
                    CancellationToken token
                ) =>
                    Accepted(await jobs.UploadMusicXmlAsync(file, token))
            )
            .WithName("UploadMusicXml")
            .WithSummary("Imports an uploaded MusicXML file (.musicxml, .xml or .mxl), as ImportMusicXml does.")
            .DisableAntiforgery();
        project.MapPost(
                "/import/aaf",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(AafImportRequest request, Jobs jobs) =>
                    Accepted(jobs.ImportAaf(request))
            )
            .WithName("ImportAaf")
            .WithSummary(
                "Imports an AAF file from the server's disk (embedded or linked media, copied into the project) as audio "
                + "tracks: an aafImport job, one undo step when it finishes."
            );
        project.MapPost(
                "/import/aaf/upload",
                static async Task<Results<Accepted<Job>, NotFound, ProblemHttpResult>>(
                    IFormFile file,
                    Jobs jobs,
                    CancellationToken token
                ) =>
                    Accepted(await jobs.UploadAafAsync(file, token))
            )
            .WithName("UploadAaf")
            .WithSummary(
                "Imports an uploaded AAF file, as ImportAaf does (embedded media only: linked files stay behind)."
            )
            .DisableAntiforgery();
        project.MapPost(
                "/import/midi",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MidiImportRequest request, Jobs jobs) =>
                    Accepted(jobs.ImportMidi(request))
            )
            .WithName("ImportMidi")
            .WithSummary(
                "Imports a Standard MIDI File from the server's disk with options (tempo map, markers, merge): a "
                + "midiImport job, one undo step when it finishes."
            );
        project.MapPost(
                "/import/midi/upload",
                static async Task<Results<Accepted<Job>, NotFound, ProblemHttpResult>>(
                    IFormFile file,
                    bool? importTempo,
                    bool? importMarkers,
                    bool? merge,
                    Jobs jobs,
                    CancellationToken token
                ) =>
                    Accepted(await jobs.UploadMidiAsync(file, importTempo, importMarkers, merge, token))
            )
            .WithName("UploadMidi")
            .WithSummary(
                "Imports an uploaded Standard MIDI File, as ImportMidi does; the options are query parameters (left out: "
                + "MidiImportRequest's defaults)."
            )
            .DisableAntiforgery();
    }

    static Accepted<Job> Accepted(Job job) => TypedResults.Accepted($"/api/v1/jobs/{job.Id}", job);
}
