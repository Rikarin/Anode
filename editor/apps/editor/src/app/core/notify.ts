import { Injectable, inject, signal } from '@angular/core';
import { ApiError } from '@anode/api-client';
import { XuiToastService } from '@xui/toast';

/** What failed, in words: the Server's reason for a refused request, else the error's message. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message;
  }

  return error instanceof Error ? error.message : String(error);
}

/** Tells the person what went wrong (a toast), and keeps the last message for tests. */
@Injectable({ providedIn: 'root' })
export class Notify {
  private readonly toaster = inject(XuiToastService, { optional: true });
  readonly last = signal<string | null>(null);

  error(message: string): void {
    this.last.set(message);
    this.toaster?.show({ message, color: 'error' });
  }

  info(message: string): void {
    this.last.set(message);
    this.toaster?.show({ message, color: 'primary' });
  }

  /** Something finished (a bounce), with an optional action on the toast ("Show in Finder"). */
  success(message: string, action?: { text: string; run: () => void }): void {
    this.last.set(message);
    this.toaster?.show({
      message,
      color: 'success',
      ...(action ? { actionText: action.text, onAction: action.run, timeout: 10_000 } : {})
    });
  }

  /** Reports a failure, prefixed with what was being done ("Could not save"). */
  failure(error: unknown, doing?: string): void {
    const reason = describeError(error);
    this.error(doing ? `${doing}: ${reason}` : reason);
  }

  /** Runs `work`, reporting a failure instead of throwing; null when it failed. */
  async run<T>(work: () => Promise<T>, doing?: string): Promise<T | null> {
    try {
      return await work();
    } catch (error) {
      this.failure(error, doing);
      return null;
    }
  }
}
