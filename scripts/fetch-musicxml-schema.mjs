#!/usr/bin/env node
// Fetches the MusicXML 4.0 XSD (and the xlink and xml schemas it imports, and the .mxl container schema) from the W3C
// Music Notation Community Group's repository at the v4.0 tag, into the Interchange tests' fixtures
// (docs/interchange/musicxml.md). The files are committed; this script documents where they came from and checks
// them: `node scripts/fetch-musicxml-schema.mjs` re-downloads and verifies, `--check` only verifies what is committed.
//
// Licence: the MusicXML 4.0 specification and schema are published under the W3C Community Final Specification
// Agreement (FSA), https://www.w3.org/community/about/agreements/final/, which grants a copyright licence to
// reproduce, distribute and make derivative works of the specification (the XSD included); xml.xsd and xlink.xsd are
// W3C schemas under the W3C Software and Document Licence.

import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const tag = 'v4.0';
const base = `https://raw.githubusercontent.com/w3c-cg/musicxml/${tag}/schema/`;
const target = join(
  dirname(fileURLToPath(import.meta.url)),
  '../engine/tests/Anode.Interchange.Tests/MusicXml/Schema',
);

// SHA-256 of each file at the tag.
const files = {
  'musicxml.xsd': 'bfe37ed25a9ec00e6f2591d53df260b84efe12aed209ba3ac0a76f9287665a99',
  'xlink.xsd': '6e601f8eeb41618b50e4c7f944dff754e57ea43b602755470dda24c9c2f6df92',
  'xml.xsd': '616a3077df5cfc954ac74a75abe9697b95eef7a85dbe09367d995a483e840eb5',
  'container.xsd': 'deddcc2f51e856de21397bbe25e2cf304ca9e3253b0d25dcc6349c390bc22fa6',
};

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const checkOnly = process.argv.includes('--check');
let failed = false;

await mkdir(target, { recursive: true });
for (const [name, expected] of Object.entries(files)) {
  let bytes;
  if (checkOnly) {
    bytes = await readFile(join(target, name));
  } else {
    const response = await fetch(base + name);
    if (!response.ok) throw new Error(`${name}: HTTP ${response.status}`);
    bytes = Buffer.from(await response.arrayBuffer());
  }
  const actual = sha256(bytes);
  if (actual !== expected) {
    console.error(`${name}: sha256 ${actual}, expected ${expected}`);
    failed = true;
    continue;
  }
  if (!checkOnly) await writeFile(join(target, name), bytes);
  console.log(`${name}: ok`);
}
if (failed) process.exit(1);
