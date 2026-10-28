import { DestroyRef, type ElementRef, type Signal, afterNextRender, effect, inject, signal } from '@angular/core';

/**
 * The size of an element as a signal (CSS pixels), kept by a ResizeObserver; 0 × 0 until it is measured (and in tests
 * without layout). Call in an injection context.
 */
export function elementSize(
  element: () => ElementRef<HTMLElement> | undefined
): Signal<{ width: number; height: number }> {
  const size = signal({ width: 0, height: 0 });
  const observer =
    typeof ResizeObserver === 'function'
      ? new ResizeObserver(entries => {
          const rect = entries[0]?.contentRect;
          if (rect) {
            size.set({ width: Math.round(rect.width), height: Math.round(rect.height) });
          }
        })
      : null;
  let observed: HTMLElement | null = null;
  const observe = () => {
    const el = element()?.nativeElement ?? null;
    if (el !== observed) {
      if (observed) {
        observer?.unobserve(observed);
      }
      observed = el;
      if (el) {
        observer?.observe(el);
        const rect = el.getBoundingClientRect();
        size.set({ width: Math.round(rect.width), height: Math.round(rect.height) });
      }
    }
  };
  afterNextRender(observe);
  effect(observe);
  inject(DestroyRef).onDestroy(() => observer?.disconnect());
  return size.asReadonly();
}
