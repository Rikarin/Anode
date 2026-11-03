#!/usr/bin/env dotnet
// The golden path's test movie (docs/11-phase-10.md, exit criterion step 3; docs/media/README.md): Anode.Media's
// GenerateTestMovieAsync with the spec the media tests use — 6 s at 25 fps, 640×360, scene cuts at frames 40 and 100,
// timecode 00:00:10:00, the frame number and timecode burned in, a 50 ms 1 kHz beep at the start of every second,
// H.264 (h264_videotoolbox on macOS, else libopenh264) with 48 kHz AAC in MOV — through the pinned LGPL FFmpeg
// (node scripts/fetch-ffmpeg.mjs first, or ANODE_FFMPEG). Then prints what FFprobe reads back.
//
//   dotnet run scripts/make-test-movie.cs -- [<output.mov>] [<seconds>]
//
// Default output: engine/artifacts/media/test-movie.mov (generated, never committed).
#:project ../engine/src/Anode.Media/Anode.Media.csproj
#:property TreatWarningsAsErrors=false
#:property PublishAot=false

using Anode.Media.Ffmpeg;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("engine", "artifacts", "media", "test-movie.mov"));
var seconds = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 6;
var tools = FfmpegTools.Locate(Path.Combine("engine", "artifacts", "ffmpeg", FfmpegTools.Platform)) ?? FfmpegTools.Locate();
if (tools is null) {
    Console.Error.WriteLine("No FFmpeg: run `node scripts/fetch-ffmpeg.mjs` or set ANODE_FFMPEG.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var runner = new FfmpegRunner(tools);
var cuts = new List<long> { 40, 100 }.Where(f => f < seconds * 25).ToList();
await MovieMedia.GenerateTestMovieAsync(runner, output, new(seconds, cuts, TimecodeStartSeconds: 10));
var probe = await MovieMedia.ProbeAsync(runner, output);
Console.WriteLine($"{output}: {probe}");
return 0;
