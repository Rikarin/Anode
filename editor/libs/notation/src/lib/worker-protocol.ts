import type { RenderedPage, RenderOptions } from './renderer';
import type { RenderPath } from './verovio-engine';

/** What the main thread asks the notation worker: start (where Verovio's files are), or render. */
export type WorkerRequest =
  { type: 'init'; baseUrl: string } | { type: 'render'; id: number; mei: string; options: RenderOptions };

/** What the worker answers a render with: the pages (and how, and how long in the engine and in all), or a failure. */
export type WorkerReply =
  | { type: 'rendered'; id: number; pages: RenderedPage[]; path: RenderPath; ms: number; totalMs: number }
  | { type: 'failed'; id: number; message: string };
