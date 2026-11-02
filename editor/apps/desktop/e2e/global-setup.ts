import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * What the golden paths need besides the built app and engine: Phase 8's `anode-midiprobe` (built with the engine's
 * Release configuration), the Stem Splitter's test mix (`scripts/build-content.mjs --stems`, when it is not there yet)
 * and, through `ANODE_MODELS`, the locally exported separation models in the repository's `models/` (ADR-0010: never
 * published) for the Server to install from. Phase 9's needs pyaaf2 (`scripts/setup-pyaaf2.py` makes its venv in
 * `engine/artifacts/pyaaf2` when it is not there; `ANODE_PYTHON` is that venv's python unless set), and xmllint and
 * pdftotext on the PATH (`scripts/musicxml-validate.py`, the printed PDF's text), which it checks.
 */
export default function globalSetup(): void {
  const root = resolve(__dirname, '../../../..');
  execFileSync(
    'dotnet',
    ['build', join(root, 'engine', 'tools', 'Anode.MidiProbe'), '-c', 'Release', '--nologo', '-v', 'quiet'],
    { stdio: 'inherit' }
  );
  if (!existsSync(join(root, 'engine', 'artifacts', 'content', 'stem-mix', 'mix.wav'))) {
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
        throw new Error(`${tool} is not on the PATH: the Phase 9 golden path needs it (poppler, libxml2).`);
      }
    }
  }
  const models = join(root, 'models');
  if (!process.env['ANODE_MODELS'] && existsSync(join(models, 'htdemucs.onnx'))) {
    process.env['ANODE_MODELS'] = models;
  }
}
