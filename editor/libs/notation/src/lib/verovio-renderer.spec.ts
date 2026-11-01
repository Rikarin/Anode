import type { ScoreLayout } from '@anode/api-client';
import { RenderSuperseded, VerovioNotationRenderer, type WorkerLike } from './verovio-renderer';
import type { WorkerReply, WorkerRequest } from './worker-protocol';

class FakeWorker implements WorkerLike {
  readonly requests: WorkerRequest[] = [];
  private listener: ((event: MessageEvent<WorkerReply>) => void) | null = null;
  terminated = false;

  postMessage(message: WorkerRequest): void {
    this.requests.push(message);
  }

  addEventListener(type: 'message' | 'error', listener: never): void {
    if (type === 'message') {
      this.listener = listener;
    }
  }

  terminate(): void {
    this.terminated = true;
  }

  reply(message: WorkerReply): void {
    this.listener?.({ data: message } as MessageEvent<WorkerReply>);
  }
}

const layout = { pageWidthMm: 210 } as ScoreLayout;

describe('VerovioNotationRenderer', () => {
  it('starts the worker on the first render, with where Verovio is', async () => {
    const worker = new FakeWorker();
    let created = 0;
    const renderer = new VerovioNotationRenderer({
      baseUrl: () => 'http://x/verovio/',
      createWorker: () => {
        created++;
        return worker;
      }
    });
    expect(created).toBe(0);
    const pages = renderer.render('<mei/>', { view: 'linear', layout });
    expect(worker.requests[0]).toEqual({ type: 'init', baseUrl: 'http://x/verovio/' });
    expect(worker.requests[1]).toMatchObject({ type: 'render', id: 1, mei: '<mei/>' });
    worker.reply({
      type: 'rendered',
      id: 1,
      pages: [{ page: 1, svg: '<svg/>', width: 10, height: 5 }],
      path: 'load',
      ms: 12,
      totalMs: 40
    });
    await expect(pages).resolves.toEqual([{ page: 1, svg: '<svg/>', width: 10, height: 5 }]);
    expect(renderer.lastTiming).toMatchObject({ path: 'load', engineMs: 12, workerMs: 40 });
    renderer.dispose();
    expect(worker.terminated).toBe(true);
    expect(created).toBe(1);
  });

  it('runs one render at a time, a newer request replacing the waiting one', async () => {
    const worker = new FakeWorker();
    const renderer = new VerovioNotationRenderer({ baseUrl: () => '', createWorker: () => worker });
    const first = renderer.render('a', { view: 'linear', layout });
    const second = renderer.render('b', { view: 'linear', layout });
    const third = renderer.render('c', { view: 'linear', layout });
    await expect(second).rejects.toBeInstanceOf(RenderSuperseded);
    expect(worker.requests.filter(r => r.type === 'render')).toHaveLength(1);
    worker.reply({ type: 'rendered', id: 1, pages: [], path: 'load', ms: 1, totalMs: 1 });
    await expect(first).resolves.toEqual([]);
    expect(worker.requests.at(-1)).toMatchObject({ type: 'render', id: 3, mei: 'c' });
    worker.reply({ type: 'failed', id: 3, message: 'Verovio could not read the score' });
    await expect(third).rejects.toThrow(/could not read/);
  });
});
