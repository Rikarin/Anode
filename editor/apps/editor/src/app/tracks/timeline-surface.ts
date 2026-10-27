import {
  createRenderer,
  TimelineCanvas,
  TimelineWorkerClient,
  type TimelineScene,
  type TimelineView,
  type Waveform
} from '@anode/timeline';

/**
 * Where the Tracks area draws: the render worker on a transferred `OffscreenCanvas` (ADR-0007), or the same renderer
 * on the main thread where a canvas cannot be handed to a worker. Either way the timeline posts the scene when it
 * changes, waveforms as they arrive, and a view per animation frame.
 */
export interface TimelineSurface {
  readonly mode: 'worker' | 'main';
  /** Replaces the scene; its buffers are transferred (unusable here afterwards). */
  setScene(scene: TimelineScene): void;
  setWaveform(key: number, waveform: Waveform): void;
  resize(width: number, height: number, dpr: number): void;
  frame(view: TimelineView): void;
  dispose(): void;
}

/** A surface for `canvas`, or null where there is nothing to draw with (tests in jsdom). */
export async function createSurface(
  canvas: HTMLCanvasElement,
  width: number,
  height: number,
  dpr: number
): Promise<TimelineSurface | null> {
  if (typeof Worker === 'function' && TimelineWorkerClient.supported(canvas)) {
    const client = new TimelineWorkerClient();
    await client.init(canvas, 'auto', width, height, dpr);
    return {
      mode: 'worker',
      setScene: scene => void client.setScene(scene).catch((error: unknown) => console.error('Anode timeline:', error)),
      setWaveform: (key, waveform) => client.setWaveform(key, waveform),
      resize: (w, h, d) => client.resize(w, h, d),
      frame: view => client.frame(view),
      dispose: () => client.terminate()
    };
  }

  if (typeof canvas.getContext !== 'function' || typeof requestAnimationFrame !== 'function') {
    return null;
  }

  let surface: TimelineCanvas;
  try {
    surface = new TimelineCanvas(createRenderer(canvas, 'auto'));
  } catch {
    return null;
  }

  surface.resize(width, height, dpr);
  let latest: TimelineView | null = null;
  let frame = 0;
  const draw = () => {
    frame = 0;
    if (latest) {
      surface.render(latest);
      latest = null;
    }
  };
  return {
    mode: 'main',
    setScene: scene => void surface.setScene(scene),
    setWaveform: (key, waveform) => surface.setWaveform(key, waveform),
    resize: (w, h, d) => surface.resize(w, h, d),
    frame: view => {
      latest = view;
      frame ||= requestAnimationFrame(draw);
    },
    dispose: () => {
      cancelAnimationFrame(frame);
      surface.renderer.dispose();
    }
  };
}
