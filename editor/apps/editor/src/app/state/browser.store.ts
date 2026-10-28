import { Injectable, computed, inject, signal } from '@angular/core';
import type { DirectoryListing, FileEntry } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';

/** The drag type of a file dragged out of the All Files browser (its path on the Server's disk). */
export const ServerFileType = 'application/x-anode-server-file';
/** The drag type of a project audio file dragged out of the Project Audio browser (its file id). */
export const ProjectFileType = 'application/x-anode-project-file';

/**
 * The All Files browser (Logic's): a folder on the Server's disk (`/files`, home first), back and up, bookmarked
 * folders (per user, `/settings/bookmarks`), a search over the folder, and a preview of audio files through the
 * engine's preview channel.
 */
@Injectable({ providedIn: 'root' })
export class BrowserStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);

  readonly listing = signal<DirectoryListing | null>(null);
  readonly loading = signal(false);
  readonly bookmarks = signal<readonly string[]>([]);
  readonly previewing = signal<string | null>(null);
  readonly filter = signal('');
  private readonly history: string[] = [];

  readonly entries = computed<readonly FileEntry[]>(() => {
    const query = this.filter().trim().toLowerCase();
    const entries = this.listing()?.entries ?? [];
    return query ? entries.filter(e => e.name.toLowerCase().includes(query)) : entries;
  });
  readonly bookmarked = computed(() => {
    const path = this.listing()?.path;
    return path !== undefined && this.bookmarks().includes(path);
  });
  readonly canGoBack = signal(false);

  constructor() {
    inject(EventStream).connected$.subscribe(() => {
      void this.loadBookmarks();
    });
  }

  /** Opens the home folder the first time the browser shows. */
  async start(): Promise<void> {
    if (!this.listing() && !this.loading()) {
      await Promise.all([this.open(null), this.loadBookmarks()]);
    }
  }

  async open(path: string | null, remember = true): Promise<void> {
    this.loading.set(true);
    try {
      const listing = await this.api.listFiles(path);
      const previous = this.listing()?.path;
      if (remember && previous && previous !== listing.path) {
        this.history.push(previous);
      }
      this.canGoBack.set(this.history.length > 0);
      this.listing.set(listing);
      this.filter.set('');
    } catch (error) {
      this.notify.failure(error, 'Could not open the folder');
    } finally {
      this.loading.set(false);
    }
  }

  async back(): Promise<void> {
    const path = this.history.pop();
    this.canGoBack.set(this.history.length > 0);
    if (path) {
      await this.open(path, false);
    }
  }

  async up(): Promise<void> {
    const parent = this.listing()?.parent;
    if (parent) {
      await this.open(parent);
    }
  }

  async loadBookmarks(): Promise<void> {
    try {
      this.bookmarks.set((await this.api.bookmarks()).paths);
    } catch {
      this.bookmarks.set([]);
    }
  }

  /** Bookmarks the open folder, or removes its bookmark. */
  async toggleBookmark(path = this.listing()?.path): Promise<void> {
    if (!path) {
      return;
    }
    const previous = this.bookmarks();
    const next = previous.includes(path) ? previous.filter(p => p !== path) : [...previous, path];
    this.bookmarks.set(next);
    try {
      this.bookmarks.set((await this.api.saveBookmarks({ paths: next })).paths);
    } catch (error) {
      this.bookmarks.set(previous);
      this.notify.failure(error, 'Could not save the bookmarks');
    }
  }

  /** Plays an audio file through the preview channel, or stops it when it is the one playing. */
  async preview(entry: FileEntry | null): Promise<void> {
    const path = entry && entry.path !== this.previewing() ? entry.path : null;
    try {
      await this.api.previewFile(path);
      this.previewing.set(path);
    } catch (error) {
      this.previewing.set(null);
      this.notify.failure(error, 'Could not preview the file');
    }
  }
}

/** A size as Finder shows it: 1.2 MB. */
export function fileSize(bytes: number): string {
  if (bytes < 1000) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1000;
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

/** An audio file's format as the browser lists it: "48 kHz · stereo · 0:04". */
export function audioFormat(entry: Pick<FileEntry, 'sampleRate' | 'channels' | 'frames'>): string {
  if (!entry.sampleRate) {
    return '';
  }
  const parts = [`${entry.sampleRate / 1000} kHz`];
  if (entry.channels) {
    parts.push(entry.channels === 1 ? 'mono' : entry.channels === 2 ? 'stereo' : `${entry.channels} ch`);
  }
  if (entry.frames) {
    const seconds = Math.round(entry.frames / entry.sampleRate);
    parts.push(`${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`);
  }
  return parts.join(' · ');
}
