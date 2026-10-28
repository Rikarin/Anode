import { Injectable, signal } from '@angular/core';

export type Appearance = 'dark' | 'light';

const Key = 'anode.appearance';

/**
 * Light or dark appearance. Dark is Anode's primary theme (index.html pins it); the choice is kept per browser. The
 * root element's `dark` / `light` class re-themes every token; the timeline canvases read {@link appearance} and redraw
 * in the matching palette at once.
 */
@Injectable({ providedIn: 'root' })
export class ThemeStore {
  readonly appearance = signal<Appearance>(stored());

  constructor() {
    apply(this.appearance());
  }

  set(appearance: Appearance): void {
    this.appearance.set(appearance);
    apply(appearance);
    try {
      localStorage.setItem(Key, appearance);
    } catch {
      // blocked storage: this session only
    }
  }

  toggle(): void {
    this.set(this.appearance() === 'dark' ? 'light' : 'dark');
  }
}

function stored(): Appearance {
  try {
    return localStorage.getItem(Key) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

function apply(appearance: Appearance): void {
  if (typeof document === 'undefined') {
    return;
  }
  const root = document.documentElement;
  root.classList.toggle('dark', appearance === 'dark');
  root.classList.toggle('light', appearance === 'light');
  root.style.colorScheme = appearance;
}
