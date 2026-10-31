import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * What the golden paths need besides the built app and engine: Phase 8's `anode-midiprobe` (built with the engine's
 * Release configuration), the Stem Splitter's test mix (`scripts/build-content.mjs --stems`, when it is not there yet)
 * and, through `ANODE_MODELS`, the locally exported separation models in the repository's `models/` (ADR-0010: never
 * published) for the Server to install from.
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
  const models = join(root, 'models');
  if (!process.env['ANODE_MODELS'] && existsSync(join(models, 'htdemucs.onnx'))) {
    process.env['ANODE_MODELS'] = models;
  }
}
