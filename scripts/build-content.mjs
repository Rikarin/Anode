#!/usr/bin/env node
// Builds Anode's content packs (docs/07-phase-6.md, "Content packs") with the content builder
// (engine/tools/Anode.ContentBuilder): Anode Essentials into engine/artifacts/content/AnodeEssentials.anodepack and the
// e2e test pack into engine/artifacts/content/TestPack.anodepack, with a synthetic impulse response for the e2e in
// engine/artifacts/content/TestIR.wav, and the Stem Splitter's test mix (docs/09-phase-8.md, step 9) in
// engine/artifacts/content/stem-mix/ (vocals.wav, drums.wav, bass.wav, other.wav and mix.wav, 32-bit float, 20 s at
// 44.1 kHz stereo). Generated, never committed.
//
//   node scripts/build-content.mjs            both packs and the stem test mix
//   node scripts/build-content.mjs --test     the test pack (and TestIR.wav, an impulse response) and the stem test mix
//   node scripts/build-content.mjs --essentials
//   node scripts/build-content.mjs --t1       render drums with the T1 synth even when the Drum Synth is built
//   node scripts/build-content.mjs --stems    the stem test mix only (scripts/build-stem-mix.cs)
import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const engine = join(root, 'engine');
const out = join(engine, 'artifacts', 'content');
const args = process.argv.slice(2);
const only = args.includes('--test') ? ['test'] : args.includes('--essentials') ? ['essentials'] : ['essentials', 'test'];
const extra = args.includes('--t1') ? ['--t1'] : [];

mkdirSync(out, { recursive: true });

// The Stem Splitter's test mix: a file-based app on Anode.Dsp's instruments.
function buildStemMix() {
  const stems = spawnSync('dotnet', ['run', join(root, 'scripts', 'build-stem-mix.cs'), '--', join(out, 'stem-mix')], {
    cwd: root,
    stdio: 'inherit',
  });
  if (stems.status !== 0) {
    process.exit(stems.status ?? 1);
  }
}

if (args.includes('--stems')) {
  buildStemMix();
  process.exit(0);
}

const project = join(engine, 'tools', 'Anode.ContentBuilder', 'Anode.ContentBuilder.csproj');
const build = spawnSync('dotnet', ['build', project, '-c', 'Release', '--nologo', '-v', 'quiet'], {
  cwd: engine,
  stdio: 'inherit',
});
if (build.status !== 0) {
  process.exit(build.status ?? 1);
}

for (const pack of only) {
  const file = join(out, pack === 'test' ? 'TestPack.anodepack' : 'AnodeEssentials.anodepack');
  const run = spawnSync(
    'dotnet',
    [
      'run', '--project', project, '-c', 'Release', '--no-build', '--',
      ...(pack === 'test' ? ['--test', '--ir', join(out, 'TestIR.wav')] : []),
      '--out', file, ...extra,
    ],
    { cwd: engine, stdio: 'inherit' },
  );
  if (run.status !== 0) {
    process.exit(run.status ?? 1);
  }
}

if (!args.includes('--essentials')) {
  buildStemMix();
}
