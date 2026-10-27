// Runs the S1 rendering bench (bench/index.html) in Chromium, WebKit and Electron through Playwright, and writes the
// results as JSON to dist/bench/timeline-results/. `nx run timeline:bench-run`, or directly:
//   node libs/timeline/bench/run-bench.mts [--targets=chromium-headed,webkit-headed,...] [--duration=10000]
//                                         [--scenes=notes,regions] [--backends=webgl2,canvas2d] [--threads=worker,main]
//                                         [--pacing=animation-frame|message]
// Targets:
// - chromium-headless: full Chromium in new headless mode on the GPU (ANGLE over Metal on macOS);
// - chromium-headless-shell: Playwright's default headless shell, which rasterises WebGL in software (SwiftShader);
// - chromium-headed, webkit-headless, webkit-headed;
// - electron: the workspace's Electron in a 1600 × 900 BrowserWindow (electron-main.cjs).
// Install the browsers first: `pnpm exec playwright install chromium webkit`.
import { _electron, type Browser, chromium, type Page, webkit } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { cpus, platform, release, totalmem } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { preview } from 'vite';

const here = import.meta.dirname;
const workspace = resolve(here, '../../..');
const config = resolve(here, '../vite.bench.config.mts');

const args = new Map(
  process.argv.slice(2).map(arg => {
    const [key, value = 'true'] = arg.replace(/^--/, '').split('=');
    return [key, value] as const;
  })
);
const list = (key: string, fallback: string) => (args.get(key) ?? fallback).split(',').filter(Boolean);
const targets = list('targets', 'chromium-headless,chromium-headed,webkit-headless,webkit-headed,electron');
const scenes = list('scenes', 'notes,regions');
const backends = list('backends', 'webgl2,canvas2d');
const threads = list('threads', 'worker,main');
const duration = Number(args.get('duration') ?? 10_000);
const pacing = args.get('pacing') ?? 'animation-frame';

const viewport = { width: 1600, height: 900 };
// Playwright already passes --disable-renderer-backgrounding and friends; GPU access is the only addition.
const chromiumArgs = ['--ignore-gpu-blocklist'];
const headlessGpuArgs = [...chromiumArgs, '--use-angle=metal', '--enable-gpu'];

interface Target {
  /** A page for one run, and how to dispose of it. */
  open(): Promise<{ page: Page; close: () => Promise<void> }>;
  close(): Promise<void>;
  version: string;
}

async function browserTarget(name: string): Promise<Target> {
  const headless = name.includes('-headless');
  const browser: Browser = name.startsWith('webkit')
    ? await webkit.launch({ headless })
    : name === 'chromium-headless'
      ? await chromium.launch({ headless, channel: 'chromium', args: headlessGpuArgs })
      : await chromium.launch({ headless, args: chromiumArgs });
  return {
    version: browser.version(),
    open: async () => {
      const context = await browser.newContext({ viewport, deviceScaleFactor: 2 });
      const page = await context.newPage();
      return { page, close: () => context.close() };
    },
    close: () => browser.close()
  };
}

async function electronTarget(): Promise<Target> {
  const app = await _electron.launch({ args: [join(here, 'electron-main.cjs'), ...chromiumArgs] });
  const page = await app.firstWindow();
  const version = await app.evaluate(
    ({ app: electronApp }) =>
      `Electron ${process.versions.electron} (Chromium ${process.versions.chrome}) ${electronApp.getName()}`
  );
  return {
    version,
    open: () => Promise.resolve({ page, close: () => Promise.resolve() }),
    close: () => app.close()
  };
}

async function main(): Promise<void> {
  if (args.get('skip-build') !== 'true') {
    // In a child process: after an in-process Vite build, Chrome for Testing crashes at launch (SIGBUS) on macOS.
    const vite = join(dirname(createRequire(import.meta.url).resolve('vite/package.json')), 'bin/vite.js');
    execFileSync(process.execPath, [vite, 'build', '--config', config, '--logLevel', 'warn'], { stdio: 'inherit' });
  }
  const server = await preview({ configFile: config, logLevel: 'warn' });
  const base = server.resolvedUrls?.local[0] ?? 'http://localhost:4510/';

  const results: unknown[] = [];
  try {
    for (const name of targets) {
      const launch = () => (name === 'electron' ? electronTarget() : browserTarget(name));
      let target: Target | null = null;
      for (const scene of scenes) {
        for (const backend of backends) {
          for (const thread of threads) {
            const url = `${base}?scene=${scene}&backend=${backend}&thread=${thread}&duration=${duration}&pacing=${pacing}`;
            let result: Record<string, unknown> | null = null;
            // Headed Chrome for Testing sometimes dies at startup on macOS (SIGBUS before the first page): relaunch.
            for (let attempt = 1; !result; attempt++) {
              try {
                target ??= await launch();
                const { page, close } = await target.open();
                try {
                  await page.goto(url);
                  await page.waitForFunction(() => window.benchResult !== undefined, undefined, {
                    timeout: duration + 90_000
                  });
                  result = (await page.evaluate(() => window.benchResult)) as Record<string, unknown>;
                } finally {
                  await close().catch(() => undefined);
                }
              } catch (error) {
                await target?.close().catch(() => undefined);
                target = null;
                console.error(`${name}: attempt ${attempt} failed: ${String(error).split('\n')[0] ?? ''}`);
                if (attempt === 3) {
                  result = { ok: false, scene, backendRequested: backend, thread, error: String(error) };
                }
              }
            }
            result = { target: name, browserVersion: target?.version, ...result };
            results.push(result);
            console.warn(line(result));
          }
        }
      }
      await target?.close().catch(() => undefined);
    }
  } finally {
    await server.close();
  }

  const outDir = join(workspace, 'dist/bench/timeline-results');
  mkdirSync(outDir, { recursive: true });
  const file = join(outDir, `s1-${new Date().toISOString().replace(/[:.]/g, '-')}.json`);
  const machine = {
    platform: `${platform()} ${release()}`,
    cpu: cpus()[0]?.model,
    cores: cpus().length,
    memoryGb: Math.round(totalmem() / 2 ** 30)
  };
  writeFileSync(file, JSON.stringify({ machine, duration, pacing, results }, null, 2));
  console.warn(`\nWrote ${file}`);
}

type Summary = Record<string, number>;

function line(r: Record<string, unknown>): string {
  const field = (key: string) => String(r[key]);
  const head = `${field('target').padEnd(17)} ${field('scene').padEnd(7)} ${field('backendRequested').padEnd(8)} ${field('thread').padEnd(6)}`;
  if (!r['ok']) {
    return `${head} FAILED ${field('error')}`;
  }
  const triple = (key: string) => {
    const summary = r[key] as Summary;
    return `${summary['p50']}/${summary['p95']}/${summary['p99']}`;
  };
  const visible = r['visibleItems'] as Summary;
  return (
    `${head} ${field('backend')} fps=${field('fps')} refresh=${field('refreshMs')}ms ` +
    `interval p50/p95/p99=${triple('frameIntervalMs')} ` +
    `dropped=${field('droppedFrames')} (${field('droppedPct')}%) >25ms=${field('over60HzBudget')} ` +
    `cpu p50/p95/p99=${triple('cpuMs')} sync p50/p95/p99=${triple('syncFrameCostMs')} ` +
    `visible=${visible['min']}..${visible['max']} gpu=${field('gpu')}`
  );
}

declare global {
  interface Window {
    benchResult?: unknown;
  }
}

await main();
