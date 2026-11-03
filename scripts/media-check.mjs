#!/usr/bin/env node
// The Phase 10 golden path's media helper (editor/apps/desktop/e2e/phase10/helpers.ts, ANODE_MEDIA_HELPER): the test
// movie through workstream E's Anode.Media (scripts/make-test-movie.cs), and FFprobe and FFmpeg from the pinned LGPL
// build (node scripts/fetch-ffmpeg.mjs; ANODE_FFMPEG overrides, as for the Server). The tools always run without
// DYLD_*, LD_LIBRARY_PATH and LD_PRELOAD, which would make them load other (Homebrew's GPL) libraries.
//
//   node scripts/media-check.mjs make-movie <out.mov> <seconds> <fps> <cut,cut,…>   prints the movie (JSON)
//   node scripts/media-check.mjs probe <movie>                                         FFprobe's -show_streams (JSON)
//   node scripts/media-check.mjs audio <movie> <out.wav>         the first audio stream, 48 kHz 32-bit float WAV
//   node scripts/media-check.mjs video-md5 <movie>                the MD5 of the video stream's packets (stream copy)
//
// make-movie keeps what it made in engine/artifacts/media/ (keyed by its spec) and copies it from there next time.
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const platform = `${{ darwin: "macos", win32: "windows", linux: "linux" }[process.platform]}-${process.arch === "arm64" ? "arm64" : "x64"}`;
const exe = process.platform === "win32" ? ".exe" : "";

/** The environment the tools run in: this one without the dynamic loader's search variables. */
function toolEnvironment() {
  const env = { ...process.env };
  for (const key of Object.keys(env)) {
    if (
      key.startsWith("DYLD_") ||
      key === "LD_LIBRARY_PATH" ||
      key === "LD_PRELOAD"
    ) {
      delete env[key];
    }
  }
  return env;
}

/** Where ffmpeg and ffprobe are: ANODE_FFMPEG (a folder with them or with bin/ holding them), else the fetched build. */
function tool(name) {
  const folders = process.env.ANODE_FFMPEG
    ? [process.env.ANODE_FFMPEG, join(process.env.ANODE_FFMPEG, "bin")]
    : [
        join(root, "engine", "artifacts", "ffmpeg", platform, "bin"),
        join(root, "engine", "artifacts", "ffmpeg", platform),
      ];
  for (const folder of folders) {
    const path = join(folder, name + exe);
    if (existsSync(path)) {
      return path;
    }
  }
  throw new Error(
    `No ${name}: run \`node scripts/fetch-ffmpeg.mjs\` (or set ANODE_FFMPEG).`,
  );
}

function run(name, args, options = {}) {
  return execFileSync(tool(name), args, {
    env: toolEnvironment(),
    maxBuffer: 256 * 1024 * 1024,
    stdio: ["ignore", "pipe", "pipe"],
    ...options,
  });
}

/** The golden path's movie: made once per spec by Anode.Media (E), then copied. */
function makeMovie(out, seconds, fps, cuts) {
  const key = `movie-${seconds}s-${fps}fps-${cuts.join("_") || "nocuts"}.mov`;
  const cached = join(root, "engine", "artifacts", "media", key);
  if (!existsSync(cached)) {
    mkdirSync(dirname(cached), { recursive: true });
    execFileSync(
      "dotnet",
      [
        "run",
        join(root, "scripts", "make-test-movie.cs"),
        "--",
        cached,
        String(seconds),
        String(fps),
        cuts.join(","),
      ],
      {
        cwd: root,
        env: toolEnvironment(),
        stdio: ["ignore", "ignore", "inherit"],
      },
    );
  }
  if (out !== cached) {
    mkdirSync(dirname(resolve(out)), { recursive: true });
    copyFileSync(cached, out);
  }
  return {
    path: resolve(out),
    frameRate: fps,
    frames: Math.round(seconds * fps),
    durationSeconds: seconds,
    sceneCutFrames: cuts,
    beepSeconds: Array.from({ length: Math.ceil(seconds) }, (_, k) => k),
    beepHz: 1000,
  };
}

const [command, ...args] = process.argv.slice(2);
switch (command) {
  case "make-movie": {
    const [out, seconds, fps, cuts] = args;
    const list = (cuts ?? "").split(",").filter(Boolean).map(Number);
    process.stdout.write(
      JSON.stringify(makeMovie(out, Number(seconds), Number(fps), list)),
    );
    break;
  }
  case "probe":
    process.stdout.write(
      run("ffprobe", ["-v", "error", "-show_streams", "-of", "json", args[0]]),
    );
    break;
  case "audio":
    run("ffmpeg", [
      "-hide_banner",
      "-nostdin",
      "-y",
      "-v",
      "error",
      "-i",
      args[0],
      "-map",
      "0:a:0",
      "-ar",
      "48000",
      "-c:a",
      "pcm_f32le",
      args[1],
    ]);
    break;
  case "video-md5": {
    const out = run("ffmpeg", [
      "-hide_banner",
      "-nostdin",
      "-v",
      "error",
      "-i",
      args[0],
      "-map",
      "0:v:0",
      "-c:v",
      "copy",
      "-f",
      "md5",
      "-",
    ]);
    process.stdout.write(out.toString().replace(/^MD5=/, "").trim() + "\n");
    break;
  }
  default:
    console.error("Usage: media-check.mjs make-movie|probe|audio|video-md5 …");
    process.exit(2);
}
