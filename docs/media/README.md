# Media: FFmpeg and listen-in

`engine/src/Anode.Media` does Anode's movie work through FFmpeg and encodes listen-in's Opus
([docs/11-phase-10.md](../11-phase-10.md), [ADR-0013](../adr/0013-video-and-listen-in.md)). Only the Server references
it. The engine host never runs FFmpeg or encodes Opus.

## Getting FFmpeg

```sh
node scripts/fetch-ffmpeg.mjs                      # this machine's platform → engine/artifacts/ffmpeg/<platform>/
node scripts/fetch-ffmpeg.mjs --platform linux-x64 # another platform (not run, so -buildconf is not checked)
node scripts/fetch-ffmpeg.mjs --out <dir>          # elsewhere (then ANODE_FFMPEG=<dir>)
```

The pins are in `scripts/ffmpeg.json`: every archive's URL and SHA-256. macOS comes from conda-forge (the `lgpl` variant
and the libraries it loads), Windows and Linux from BtbN/FFmpeg-Builds (`lgpl` static builds). All are FFmpeg 9.0.2,
LGPL-3.0-or-later. The script downloads into a new temporary folder and checks each checksum before unpacking. It then
keeps only `bin/ffmpeg`, `bin/ffprobe`, the libraries they load (on macOS) and every package's licence files. Last, it
runs `ffmpeg -buildconf` and `-L` and refuses a build with `--enable-gpl` or `--enable-nonfree`. It writes `build.json`
(the pin, version line, configuration and packages) beside the tools.

To update the pin:
- **BtbN:** take a month-end `autobuild-YYYY-MM-DD-*` release (BtbN deletes the others after a few weeks) and its
  `checksums.sha256`.
- **conda-forge:** run `conda create -n x --dry-run --json --override-channels -c conda-forge "ffmpeg=<v>=lgpl*"`, once
  with `CONDA_SUBDIR=osx-arm64` and once with `osx-64`. Copy `actions.FETCH` (url, sha256) into the file. The script
  prints the packages whose libraries are not loaded, and those can be dropped.

`FfmpegTools.Locate()` looks in these places, in order:
1. `ANODE_FFMPEG`, a folder holding `ffmpeg` and `ffprobe` or a `bin/` with them. When set, it is the only place looked.
2. The server's folder, then its `ffmpeg/` subfolder (a release bundles the tools there).
3. `engine/artifacts/ffmpeg/<platform>/` above the server's folder (development).

`FfmpegTools.Platform` names the platform: `macos-arm64`, `macos-x64`, `windows-x64`, `windows-arm64`, `linux-x64` or
`linux-arm64`. Locate returns null when no place has both tools, and the Server answers 503.

Homebrew's `/opt/homebrew/bin/ffmpeg` is a **GPL** build. It may be used for local experiments (`ANODE_FFMPEG=/opt/homebrew/bin`),
but it must never be shipped.

## The runner

`FfmpegRunner` starts `ffmpeg` and `ffprobe` as child processes:
- Arguments are passed as a list, never through a shell.
- ffmpeg gets `-hide_banner -nostdin`, and also `-progress pipe:1 -nostats` when progress is asked for. Progress is
  reported as `out_time_us` over the given duration, from 0 to 1.
- **Timeout:** the process is killed and a `TimeoutException` is thrown.
- **Cancellation:** the process tree is killed and waited for, then `OperationCanceledException` is thrown. Deleting
  any partial output file is the caller's job.
- **Non-zero exit:** throws `FfmpegException` with `ExitCode`, the last 12 lines of standard error in the message, and
  all of standard error in `StandardError`. A tool that cannot start throws `FfmpegException` with `ExitCode` −1.
- **Environment:** the tools run without `DYLD_*`, `LD_LIBRARY_PATH` and `LD_PRELOAD`. A `DYLD_LIBRARY_PATH=/opt/homebrew/lib`
  would otherwise make the pinned executables load Homebrew's GPL libraries.

## Operations (`MovieMedia`)

Frame numbers count decoded frames from 0. "The movie's start" is the first video frame's timestamp.

| Operation | How | Measured on the test movie |
|---|---|---|
| `ProbeAsync` → `MovieProbe` | `ffprobe -show_format -show_streams` (JSON). Takes the first video stream: codec, size, `r_frame_rate` as a fraction, and `nb_frames`, or counts packets when the container has no count. Duration is the video stream's (else the file's). Timecode comes from the `timecode` tag of the video stream, then of any stream (`tmcd`), then of the file; it is the label's frame count (drop-frame after `;` at 29.97 and 59.94) over the exact rate, as the engine counts MTC. Audio is the first audio stream. A file without video throws `FfmpegException` | h264 640×360, 25/1, 150 frames, 10.0 s timecode, 6.0 s, aac 2 ch 48 kHz: exact |
| `ExtractAudioAsync` | `-copyts`, then `aresample=<rate>:first_pts=<video start × rate>` to a 32-bit float WAV (`-rf64 auto`). Sample 0 is frame 0: audio that starts later is padded with silence, audio that starts earlier is trimmed | Beeps within 0.021 ms (48 kHz), 0.023 ms (44.1 kHz), 0.010 ms (96 kHz). The same holds with the audio starting 0.5 s late |
| `ThumbnailsAsync` | One decode pass: `select='eq(n,a)+eq(n,b)+…'` and `scale=-2:<height>`, then JPEG (q 3, full range). Writes `<frame>.jpg`. A frame past the end throws before anything is written | Each thumbnail is closer to its own decoded frame than to its neighbours (under 3 grey levels apart) |
| `DetectScenesAsync` | `-copyts`, then `select='gt(scene,T)',showinfo`. Each `pts_time` is turned into a frame number relative to the video start | Cuts at 40 and 100: exact. 24 at 29.97 fps: exact |
| `RemuxAsync` | `-c copy` for every video stream (and, when adding, for the existing audio). The new audio is the WAV, offset by `-itsoffset` (video start − file start), as AAC-LC at 256 kbit/s or `pcm_s24le` (MOV only; MP4 and M4V get AAC). Keeps the metadata (the timecode), with `faststart` | Video packets byte-identical: pts, dts, flags, size and SHA-256 of all 150. New audio within 0.021 ms. Timecode kept |
| `GenerateTestMovieAsync` | See below | Probe exact as above |

## The test movie

`TestMovieSpec(durationSeconds, sceneCutFrames, frameRate = 25/1, 640, 360, timecodeStartSeconds, 1000 Hz, 0.05 s,
48 kHz, videoEncoder = auto)`. The golden path's movie is `new(6, [40, 100], TimecodeStartSeconds: 10)`, made by
`dotnet run scripts/make-test-movie.cs` (into `engine/artifacts/media/test-movie.mov`) and by the tests' fixture.

- **Picture:** a solid background that changes colour at each scene cut (six colours, far apart, in rotation). In the
  middle, FFmpeg's `testsrc` at half size; its own counter shows the time in hundredths, so every frame differs. With
  `drawtext` and a system font (Arial on macOS and Windows, DejaVu or Liberation on Linux), the frame number is burned
  in top left and the timecode bottom left.
- **Sound:** a stereo 1 kHz sine at half scale (−6 dBFS), 50 ms long, starting at sample k × rate of every second k.
- **Video codec:** `h264_videotoolbox` (macOS, with `-allow_sw 1`, so it works without a GPU), else `libopenh264`, else
  `libx264` (only a GPL build has it; local development only). 4 Mbit/s, a keyframe every second. Chromium plays it
  (checked in Chrome 152: a seek to 1.82 s shows frame 45, timecode 00:00:11:20). Seeking needs HTTP range requests.
- **Audio codec:** AAC at 192 kbit/s. The timecode is in a `tmcd` track (`-timecode`). The file uses `faststart`.
- **Container:** MOV, or MP4 by the extension. A `.webm` path gets VP9 (`libvpx-vp9`) with Opus.

## Listen-in

**`OpusEncoder` and `OpusDecoder`:**
- Built on Concentus 2.2.2 (BSD-3-Clause), using the managed `Concentus.Structs` classes. The factory is not used,
  because it may load a native libopus.
- 48 kHz stereo, 20 ms frames (960 samples), 128 kbit/s constrained VBR, complexity 5, music signal.
- Application **restricted low delay** (CELT only, 2.5 ms look-ahead).
- A 1 kHz tone at −6 dBFS decodes at 1000.000 Hz and −0.016 dB, at 128.4 kbit/s.

**`ListenInFramer(projectSampleRate)`:**
- `Write(interleaved, transportSample, playing, discontinuity)` takes the tap's blocks at the project's rate.
- `StreamResampler` resamples them to 48 kHz. It is a streaming version of `Anode.Dsp`'s `SampleRateConverter`, with
  the same Kaiser-windowed sinc, 110 dB stopband and exact rational positions, and copies at 48 kHz.
- Its look-ahead (about 80 input samples at 44.1 kHz, 160 at 96 kHz) delays the stream, but the positions stay exact.
- `Read(frame)` writes the next frame (`ListenIn` header, then the packet) once 960 samples are buffered, and returns
  0 until then.
- Each frame's transport sample is the project sample of its first sample: input time `n × rate / 48000`, from the
  latest write at or before it, rounded down. It is −1 while stopped.
- **Flags:**
  - `playing`: while playing.
  - `discontinuity`: on the first frame, and on the first frame after a write marked as a discontinuity. That write
    resets the resampler and drops the old audio that did not yet make a whole frame.
- Sequence numbers run 0, 1, … per framer, so use one framer per connection.

Measured through the framer: 1000.000 Hz within 0.016 dB at 44.1, 48 and 96 kHz. Positions are exactly 960 × rate /
48000 apart after a locate.

## Tests

`engine/tests/Anode.Media.Tests`:
- `MovieMediaTests`: every operation against the generated movie, plus a 29.97 fps OpenH264 MP4.
- `FfmpegRunnerTests`: errors, start failure, cancellation, timeout, progress, `Locate`, and the build being LGPL.
- `ListenInTests`: the Opus round trip, the framer at three rates, headers across a discontinuity.

FFmpeg tests skip with "No FFmpeg: run `node scripts/fetch-ffmpeg.mjs` …" when no tools are found.
