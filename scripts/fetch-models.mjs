#!/usr/bin/env node
// Fetches the Stem Splitter's models (docs/adr/0010-stem-splitter.md) listed in scripts/models.json into models/ at the
// repository root (gitignored), checking each file's SHA-256. A file already there with the right checksum is kept.
//
//   node scripts/fetch-models.mjs                      download from the release (once "published" is true)
//   node scripts/fetch-models.mjs --from <dir>         copy from a local folder instead (e.g. scripts/export-demucs.py's)
//   node scripts/fetch-models.mjs --out <dir>          into another folder (the tests read ANODE_MODELS)
//   node scripts/fetch-models.mjs --only htdemucs.onnx one file
//
// The server downloads the same files into the user library on first use (Anode.Stems.ModelStore, which embeds this
// manifest).
import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream, existsSync, mkdirSync, readFileSync, renameSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const option = (name) => {
  const at = args.indexOf(name);
  return at >= 0 ? args[at + 1] : undefined;
};
const from = option('--from');
const out = resolve(option('--out') ?? join(root, 'models'));
const only = option('--only');
const manifest = JSON.parse(readFileSync(join(root, 'scripts', 'models.json'), 'utf8'));

async function sha256(path) {
  const hash = createHash('sha256');
  await pipeline(createReadStream(path), hash);
  return hash.digest('hex');
}

// Streams `source` into `target` through a temporary file, hashing; keeps it only when the checksum matches.
async function copyChecked(source, target, model, total) {
  const temporary = `${target}.download`;
  const hash = createHash('sha256');
  let copied = 0;
  let shown = -1;
  try {
    await pipeline(
      source,
      async function* (chunks) {
        for await (const chunk of chunks) {
          hash.update(chunk);
          copied += chunk.length;
          const percent = total ? Math.floor((100 * copied) / total) : -1;
          if (percent !== shown && percent % 10 === 0) {
            shown = percent;
            process.stdout.write(`  ${model.file}: ${percent}%\n`);
          }
          yield chunk;
        }
      },
      createWriteStream(temporary),
    );
    const actual = hash.digest('hex');
    if (actual !== model.sha256) {
      throw new Error(`${model.file}: SHA-256 ${actual}, expected ${model.sha256}`);
    }
    renameSync(temporary, target);
  } finally {
    rmSync(temporary, { force: true });
  }
}

mkdirSync(out, { recursive: true });
let failed = false;
for (const model of manifest.models.filter((m) => !only || m.file === only)) {
  const target = join(out, model.file);
  try {
    if (!model.sha256) {
      throw new Error(`${model.file} has no checksum in scripts/models.json (run scripts/export-demucs.py)`);
    }
    if (existsSync(target) && (await sha256(target)) === model.sha256) {
      console.log(`${model.file}: present`);
      continue;
    }
    if (from) {
      const source = join(resolve(from), model.file);
      console.log(`${model.file}: from ${source}`);
      await copyChecked(createReadStream(source), target, model, model.size);
    } else {
      if (!manifest.published || !model.url) {
        throw new Error(`${model.file} is not published yet; use --from <dir> with a local export`);
      }
      console.log(`${model.file}: from ${model.url}`);
      const response = await fetch(model.url, { redirect: 'follow' });
      if (!response.ok || !response.body) {
        throw new Error(`${model.file}: ${response.status} ${response.statusText} from ${model.url}`);
      }
      await copyChecked(Readable.fromWeb(response.body), target, model, model.size);
    }
    console.log(`${model.file}: ok`);
  } catch (error) {
    console.error(String(error.message ?? error));
    failed = true;
  }
}
process.exit(failed ? 1 : 0);
