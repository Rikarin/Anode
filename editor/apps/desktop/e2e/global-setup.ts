import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * What the golden paths need besides the built app and engine: Phase 8's `anode-midiprobe` (built with the engine's
 * Release configuration), the test pack and the Stem Splitter's test mix (`scripts/build-content.mjs --test`, when they
 * are not there or are out of date) and, through `ANODE_MODELS`, the locally exported separation models in the repository's `models/` (ADR-0010: never
 * published) for the Server to install from. Phase 9's needs pyaaf2 (`scripts/setup-pyaaf2.py` makes its venv in
 * `engine/artifacts/pyaaf2` when it is not there; `ANODE_PYTHON` is that venv's python unless set), and xmllint and
 * pdftotext on the PATH (`scripts/musicxml-validate.py`, the printed PDF's text), which it checks. Phase 10's needs the
 * pinned LGPL FFmpeg (`scripts/fetch-ffmpeg.mjs`, by checksum, when `engine/artifacts/ffmpeg/<platform>` is missing),
 * the test movie (`scripts/media-check.mjs make-movie`, cached in `engine/artifacts/media`) and the EBU ADM Renderer
 * (`scripts/setup-ear.py` makes its venv in `engine/artifacts/ear`; `ANODE_EAR_PYTHON` is that venv's python unless
 * set).
 */
export default function globalSetup(): void {
  const root = resolve(__dirname, '../../../..');
  execFileSync(
    'dotnet',
    ['build', join(root, 'engine', 'tools', 'Anode.MidiProbe'), '-c', 'Release', '--nologo', '-v', 'quiet'],
    { stdio: 'inherit' }
  );
  // The test pack (and with it the stem mix) when it is missing or older than the content builder's sources: Phase 11's
  // golden path needs the Drummer kit and tape set in it.
  if (stalePack(root)) {
    execFileSync('node', [join(root, 'scripts', 'build-content.mjs'), '--test'], { cwd: root, stdio: 'inherit' });
  } else if (!existsSync(join(root, 'engine', 'artifacts', 'content', 'stem-mix', 'mix.wav'))) {
    execFileSync('node', [join(root, 'scripts', 'build-content.mjs'), '--stems'], { cwd: root, stdio: 'inherit' });
  }
  if (!process.env['ANODE_PYTHON']) {
    const python = join(
      root,
      'engine',
      'artifacts',
      'pyaaf2',
      process.platform === 'win32' ? 'Scripts/python.exe' : 'bin/python'
    );
    if (!existsSync(python)) {
      execFileSync('python3', [join(root, 'scripts', 'setup-pyaaf2.py')], { cwd: root, stdio: 'inherit' });
    }
    process.env['ANODE_PYTHON'] = python;
  }
  for (const tool of [process.env['ANODE_PDF_TEXT'] ?? 'pdftotext', 'xmllint']) {
    try {
      execFileSync(tool, ['-v'], { stdio: 'ignore' });
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        throw new Error(`${tool} is not on the PATH: the Phase 9 golden path needs it (poppler, libxml2).`, {
          cause: error
        });
      }
    }
  }
  // Phase 10: FFmpeg (the Server finds it in engine/artifacts/ffmpeg), the golden path's movie, and ear.
  const platform = `${process.platform === 'darwin' ? 'macos' : process.platform === 'win32' ? 'windows' : 'linux'}-${
    process.arch === 'arm64' ? 'arm64' : 'x64'
  }`;
  if (
    !process.env['ANODE_FFMPEG'] &&
    !existsSync(join(root, 'engine', 'artifacts', 'ffmpeg', platform, 'build.json'))
  ) {
    execFileSync('node', [join(root, 'scripts', 'fetch-ffmpeg.mjs')], { cwd: root, stdio: 'inherit' });
  }
  const media = join(root, 'engine', 'artifacts', 'media');
  execFileSync(
    'node',
    [
      join(root, 'scripts', 'media-check.mjs'),
      'make-movie',
      join(media, 'movie2-12s-25fps-50_125_200.mov'),
      '12',
      '25',
      '50,125,200'
    ],
    { cwd: root, stdio: ['ignore', 'ignore', 'inherit'] }
  );
  if (!process.env['ANODE_EAR_PYTHON']) {
    const ear = join(
      root,
      'engine',
      'artifacts',
      'ear',
      process.platform === 'win32' ? 'Scripts/python.exe' : 'bin/python'
    );
    if (!existsSync(ear)) {
      execFileSync('python3', [join(root, 'scripts', 'setup-ear.py')], {
        cwd: root,
        stdio: 'inherit',
        env: withoutLoader()
      });
    }
    process.env['ANODE_EAR_PYTHON'] = ear;
  }
  const models = join(root, 'models');
  if (!process.env['ANODE_MODELS'] && existsSync(join(models, 'htdemucs.onnx'))) {
    process.env['ANODE_MODELS'] = models;
  }
}

/** Whether the test pack is missing, or older than a source of the content builder. */
function stalePack(root: string): boolean {
  const pack = join(root, 'engine', 'artifacts', 'content', 'TestPack.anodepack');
  if (!existsSync(pack)) {
    return true;
  }
  const built = statSync(pack).mtimeMs;
  const builder = join(root, 'engine', 'tools', 'Anode.ContentBuilder');
  return readdirSync(builder).some(file => file.endsWith('.cs') && statSync(join(builder, file)).mtimeMs > built);
}

/** This environment without the dynamic loader's search variables (Homebrew's DYLD_LIBRARY_PATH, say). */
function withoutLoader(): NodeJS.ProcessEnv {
  return Object.fromEntries(
    Object.entries(process.env).filter(
      ([key]) => !key.startsWith('DYLD_') && key !== 'LD_LIBRARY_PATH' && key !== 'LD_PRELOAD'
    )
  );
}
