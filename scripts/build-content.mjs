#!/usr/bin/env node
// Builds Anode's content packs (docs/07-phase-6.md, "Content packs") with the content builder
// (engine/tools/Anode.ContentBuilder): Anode Essentials into engine/artifacts/content/AnodeEssentials.anodepack and the
// e2e test pack into engine/artifacts/content/TestPack.anodepack, with a synthetic impulse response for the e2e in
// engine/artifacts/content/TestIR.wav. Generated, never committed.
//
//   node scripts/build-content.mjs            both packs
//   node scripts/build-content.mjs --test     the test pack (and TestIR.wav, an impulse response) only
//   node scripts/build-content.mjs --essentials
//   node scripts/build-content.mjs --t1       render drums with the T1 synth even when the Drum Synth is built
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
