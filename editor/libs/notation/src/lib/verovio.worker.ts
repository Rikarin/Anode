/// <reference lib="webworker" />
import type { VerovioToolkitLike } from './verovio-engine';
import { VerovioEngine } from './verovio-engine';
import type { WorkerReply, WorkerRequest } from './worker-protocol';

/**
 * The notation worker: Verovio (LGPL-3.0, the `verovio` npm package's own files, unmodified) loaded on the first
 * request from `<base>/verovio/` (the editor build copies them there; they are never bundled), then MEI → SVG through
 * {@link VerovioEngine}. Replacing those two files replaces Verovio.
 */
const scope = self as unknown as DedicatedWorkerGlobalScope;

let engine: Promise<VerovioEngine> | null = null;

async function load(baseUrl: string): Promise<VerovioEngine> {
  const moduleUrl = new URL('verovio-module.mjs', baseUrl).href;
  const toolkitUrl = new URL('verovio.mjs', baseUrl).href;
  const [{ default: createVerovioModule }, { VerovioToolkit }] = (await Promise.all([
    import(/* @vite-ignore */ moduleUrl),
    import(/* @vite-ignore */ toolkitUrl)
  ])) as [{ default: () => Promise<unknown> }, { VerovioToolkit: new (module: unknown) => VerovioToolkitLike }];
  const module = await createVerovioModule();
  return new VerovioEngine(new VerovioToolkit(module));
}

scope.addEventListener('message', (event: MessageEvent<WorkerRequest>) => {
  const request = event.data;
  if (request.type === 'init') {
    engine ??= load(request.baseUrl);
    return;
  }
  const reply = (message: WorkerReply) => scope.postMessage(message);
  const started = performance.now();
  void (engine ?? Promise.reject(new Error('The notation worker was not started.')))
    .then(verovio => {
      const result = verovio.render(request.mei, request.options);
      reply({ type: 'rendered', id: request.id, ...result, totalMs: performance.now() - started });
    })
    .catch((error: unknown) => {
      reply({ type: 'failed', id: request.id, message: error instanceof Error ? error.message : String(error) });
    });
});
