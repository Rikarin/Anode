#!/usr/bin/env node
// Fetches the pinned LGPL build of ffmpeg and ffprobe (docs/adr/0013-video-and-listen-in.md, docs/media/README.md) for
// one platform into engine/artifacts/ffmpeg/<platform>/ (gitignored), from the archives listed in
// scripts/ffmpeg.json, each checked against its SHA-256 before it is unpacked. The build is then asked for its
// configuration (`ffmpeg -hide_banner -buildconf`) and refused when it has `--enable-gpl` or `--enable-nonfree`.
//
//   node scripts/fetch-ffmpeg.mjs                       this machine's platform
//   node scripts/fetch-ffmpeg.mjs --platform linux-x64  another one (the configuration check needs this machine's)
//   node scripts/fetch-ffmpeg.mjs --out <dir>           into another folder (the tests and server read ANODE_FFMPEG)
//   node scripts/fetch-ffmpeg.mjs --force               fetch again even when the folder holds this pin
//
// Sources: macOS from conda-forge (the `lgpl` variant of its ffmpeg package and the libraries it loads, unpacked into
// one folder: bin/ffmpeg, bin/ffprobe, lib/*.dylib); Windows and Linux from BtbN/FFmpeg-Builds (the `lgpl` static
// builds of a month-end auto-build, which that project keeps). Only the two executables (and, on macOS, the libraries
// they load) are kept, with each package's licence files under licenses/. Nothing from an archive is run except ffmpeg
// and ffprobe themselves, with the dynamic loader's search variables removed (a DYLD_LIBRARY_PATH of /opt/homebrew/lib
// would otherwise load Homebrew's GPL libraries in place of the build's own).
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  chmodSync,
  cpSync,
  createReadStream,
  createWriteStream,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  readlinkSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import { zstdDecompressSync } from 'node:zlib';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const option = (name) => {
  const at = args.indexOf(name);
  return at >= 0 ? args[at + 1] : undefined;
};
const manifest = JSON.parse(readFileSync(join(root, 'scripts', 'ffmpeg.json'), 'utf8'));
const os = { darwin: 'macos', win32: 'windows', linux: 'linux' }[process.platform];
const here = `${os}-${process.arch === 'arm64' ? 'arm64' : 'x64'}`;
const platform = option('--platform') ?? here;
const pin = manifest.platforms[platform];
if (!pin) {
  const known = Object.keys(manifest.platforms).join(', ');
  console.error(`No FFmpeg build is pinned for ${platform} (scripts/ffmpeg.json has ${known}).`);
  process.exit(1);
}

const out = resolve(option('--out') ?? join(root, 'engine', 'artifacts', 'ffmpeg', platform));
const force = args.includes('--force');
const exe = platform.startsWith('windows') ? '.exe' : '';
const pinId = createHash('sha256').update(JSON.stringify(pin)).digest('hex').slice(0, 16);
const stampPath = join(out, 'build.json');
if (!force && existsSync(stampPath) && JSON.parse(readFileSync(stampPath, 'utf8')).pin === pinId) {
  console.log(`FFmpeg ${manifest.version} (${platform}) is already in ${out}.`);
  process.exit(0);
}

// The environment ffmpeg and ffprobe run in: without the loader's search paths (as Anode.Media's runner).
const toolEnvironment = Object.fromEntries(
  Object.entries(process.env).filter(([name]) => !/^(DYLD_|LD_LIBRARY_PATH$|LD_PRELOAD$)/.test(name)),
);
const runTool = (path, toolArgs) => execFileSync(path, toolArgs, { encoding: 'utf8', env: toolEnvironment });

async function sha256(path) {
  const hash = createHash('sha256');
  await pipeline(createReadStream(path), hash);
  return hash.digest('hex');
}

// Downloads `url` into `target` and keeps it only when its SHA-256 is `expected`.
async function download(url, target, expected) {
  const response = await fetch(url, { redirect: 'follow' });
  if (!response.ok || !response.body) {
    throw new Error(`${url}: HTTP ${response.status}`);
  }

  await pipeline(Readable.fromWeb(response.body), createWriteStream(target));
  const actual = await sha256(target);
  if (actual !== expected) {
    rmSync(target, { force: true });
    throw new Error(`${basename(target)}: SHA-256 ${actual}, expected ${expected}`);
  }
}

// System tar (bsdtar on macOS and Windows reads zip as well; GNU tar on Linux reads .tar.xz).
const untar = (archive, folder) => execFileSync('tar', ['-xf', archive, '-C', folder], { stdio: 'inherit' });

function find(folder, name) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const path = join(folder, entry.name);
    if (entry.isDirectory()) {
      const found = find(path, name);
      if (found) {
        return found;
      }
    } else if (entry.name === name) {
      return path;
    }
  }

  return undefined;
}

// A .conda package: a zip holding info-*.tar.zst (metadata, licences) and pkg-*.tar.zst (the files), unpacked into
// <scratch>/<name>/files; its licences go to <licenses>/<name>.
function unpackConda(archive, scratch, licenses, name) {
  const folder = join(scratch, name);
  const outer = join(folder, 'archive');
  const files = join(folder, 'files');
  mkdirSync(outer, { recursive: true });
  mkdirSync(files);
  untar(archive, outer);
  for (const file of readdirSync(outer)) {
    if (!file.endsWith('.tar.zst')) {
      continue;
    }

    const tar = join(outer, file.replace(/\.zst$/, ''));
    writeFileSync(tar, zstdDecompressSync(readFileSync(join(outer, file))));
    if (file.startsWith('pkg-')) {
      untar(tar, files);
    } else {
      const info = join(folder, 'info');
      mkdirSync(info);
      untar(tar, info);
      const licenseFolder = join(info, 'info', 'licenses');
      if (existsSync(licenseFolder)) {
        cpSync(licenseFolder, join(licenses, name), { recursive: true });
      }
    }
  }

  return files;
}

// The file names in `lib` that `binaries` load, directly or through each other (`otool -L`), with the files their
// symbolic links point at.
function loadedLibraries(binaries, lib) {
  const needed = new Set();
  const pending = [...binaries];
  const add = (name) => {
    if (needed.has(name) || !existsSync(join(lib, name))) {
      return;
    }

    needed.add(name);
    if (lstatSync(join(lib, name)).isSymbolicLink()) {
      add(basename(readlinkSync(join(lib, name))));
    } else {
      pending.push(join(lib, name));
    }
  };
  while (pending.length > 0) {
    const listing = execFileSync('otool', ['-L', pending.pop()], { encoding: 'utf8' });
    for (const match of listing.matchAll(/^\s+@(?:rpath|loader_path(?:\/\.\.\/lib)?)\/(\S+) \(/gm)) {
      add(match[1]);
    }
  }

  return needed;
}

const parent = dirname(out);
mkdirSync(parent, { recursive: true });
// Every download and unpacking goes into a new, empty folder, removed at the end.
const scratch = mkdtempSync(join(parent, `.fetch-${platform}-`));
const staged = join(scratch, 'result');
try {
  const downloads = join(scratch, 'downloads');
  const unpacked = join(scratch, 'unpacked');
  const licenses = join(staged, 'licenses');
  for (const folder of [downloads, unpacked, join(staged, 'bin'), licenses]) {
    mkdirSync(folder, { recursive: true });
  }

  const archivePath = (item) => join(downloads, basename(new URL(item.url).pathname));
  for (const [index, item] of pin.packages.entries()) {
    console.log(`[${index + 1}/${pin.packages.length}] ${basename(archivePath(item))}`);
    await download(item.url, archivePath(item), item.sha256);
  }

  if (pin.kind === 'conda') {
    // Each package's files merged into one prefix as conda would (bin/, lib/), remembering who brought each library.
    const prefix = join(unpacked, 'prefix');
    const owners = new Map();
    for (const item of pin.packages) {
      const files = unpackConda(archivePath(item), unpacked, licenses, item.name);
      if (existsSync(join(files, 'lib'))) {
        for (const name of readdirSync(join(files, 'lib'))) {
          owners.set(name, item.name);
        }
      }

      cpSync(files, prefix, { recursive: true, verbatimSymlinks: true });
    }

    for (const tool of ['ffmpeg', 'ffprobe']) {
      cpSync(join(prefix, 'bin', tool), join(staged, 'bin', tool));
    }

    // Only the libraries the two executables load (as @rpath/… from ../lib).
    const lib = join(prefix, 'lib');
    const needed = loadedLibraries([join(prefix, 'bin', 'ffmpeg'), join(prefix, 'bin', 'ffprobe')], lib);
    mkdirSync(join(staged, 'lib'));
    const used = new Set(['ffmpeg']);
    for (const name of needed) {
      cpSync(join(lib, name), join(staged, 'lib', name), { verbatimSymlinks: true });
      used.add(owners.get(name));
    }

    const unused = pin.packages.map((item) => item.name).filter((name) => !used.has(name));
    for (const name of unused) {
      rmSync(join(licenses, name), { recursive: true, force: true });
    }

    if (unused.length > 0) {
      console.log(`Not loaded by ffmpeg or ffprobe (could be unpinned): ${unused.join(' ')}`);
    }
  } else {
    for (const item of pin.packages) {
      untar(archivePath(item), unpacked);
    }

    for (const tool of ['ffmpeg', 'ffprobe']) {
      const path = find(unpacked, `${tool}${exe}`);
      if (!path) {
        throw new Error(`${tool}${exe} is not in the archive`);
      }

      cpSync(path, join(staged, 'bin', `${tool}${exe}`));
    }

    const license = find(unpacked, 'LICENSE.txt');
    if (license) {
      mkdirSync(join(licenses, 'ffmpeg'));
      cpSync(license, join(licenses, 'ffmpeg', 'LICENSE.txt'));
    }
  }

  for (const tool of ['ffmpeg', 'ffprobe']) {
    chmodSync(join(staged, 'bin', `${tool}${exe}`), 0o755);
  }

  const stamp = { pin: pinId, platform, version: manifest.version, source: pin.source, license: pin.license };
  // An Apple silicon Mac runs the x64 build through Rosetta.
  if (platform === here || (here === 'macos-arm64' && platform === 'macos-x64')) {
    const ffmpeg = join(staged, 'bin', `ffmpeg${exe}`);
    const configuration = runTool(ffmpeg, ['-hide_banner', '-buildconf']);
    const refused = ['--enable-gpl', '--enable-nonfree'].filter((flag) => configuration.includes(flag));
    if (refused.length > 0) {
      throw new Error(`The build is not LGPL-only: it has ${refused.join(' and ')}`);
    }

    const licenseText = runTool(ffmpeg, ['-hide_banner', '-L']);
    if (!licenseText.includes('GNU Lesser General Public License')) {
      throw new Error('ffmpeg -L does not report the LGPL');
    }

    stamp.versionLine = runTool(ffmpeg, ['-hide_banner', '-version']).split('\n')[0];
    stamp.configuration = configuration
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.startsWith('--'));
    runTool(join(staged, 'bin', `ffprobe${exe}`), ['-hide_banner', '-version']);
  } else {
    console.warn(`Not checked: ${platform}'s build cannot run here; run this script on ${platform} to check -buildconf.`);
  }

  stamp.packages = pin.packages.map(({ name, version, build, license, url, sha256: hash }) => ({
    name,
    version,
    build,
    license,
    url,
    sha256: hash,
  }));
  writeFileSync(join(staged, 'build.json'), `${JSON.stringify(stamp, null, 2)}\n`);
  rmSync(out, { recursive: true, force: true });
  renameSync(staged, out);
  const size = (folder) =>
    readdirSync(folder, { withFileTypes: true }).reduce((sum, entry) => {
      const path = join(folder, entry.name);
      return sum + (entry.isDirectory() ? size(path) : entry.isFile() ? statSync(path).size : 0);
    }, 0);
  console.log(`FFmpeg ${manifest.version} (${platform}, ${pin.license}) in ${out}, ${(size(out) / 1e6).toFixed(0)} MB.`);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
