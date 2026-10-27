import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  effect,
  inject,
  signal,
  viewChild
} from '@angular/core';
import { XuiInputImports } from '@xui/input';
import { TransportStore, barsBeats, clockTime, parseSignature } from '../state/transport.store';

/**
 * The LCD in the control bar: the playhead as bars/beats/divisions/ticks and as clock time, plus the tempo and time
 * signature, each editable in place (click, type, Return; Escape cancels). It reads the playhead from its own
 * animation frames while playing, not through change detection.
 */
@Component({
  selector: 'an-lcd',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiInputImports],
  host: {
    class: 'an-lcd bg-surface-inset border-border flex items-center gap-4 rounded border px-3 py-1 font-mono',
    role: 'group',
    'aria-label': 'Position'
  },
  template: `
    <div class="flex flex-col items-end leading-tight">
      <span class="text-sm" data-testid="lcd-bars">{{ bars() }}</span>
      <span class="text-foreground-muted text-[10px]" data-testid="lcd-time">{{ time() }}</span>
    </div>
    <div class="border-border flex flex-col items-end border-s ps-4 leading-tight">
      @if (editing() === 'tempo') {
        <input
          #editor
          xuiInput
          class="h-5 w-16 px-1 text-end text-sm"
          aria-label="Tempo in bpm"
          [value]="transport.tempo()"
          (keydown.enter)="commitTempo(editor.value)"
          (keydown.escape)="editing.set(null)"
          (blur)="commitTempo(editor.value)"
        />
      } @else {
        <button
          type="button"
          class="hover:text-primary text-sm"
          data-testid="lcd-tempo"
          [attr.aria-label]="'Tempo ' + transport.tempo() + ' bpm, click to change'"
          (click)="editing.set('tempo')"
        >
          {{ transport.tempo() }}
        </button>
      }
      <span class="text-foreground-muted text-[10px]">bpm</span>
    </div>
    <div class="flex flex-col items-end leading-tight">
      @if (editing() === 'signature') {
        <input
          #editor
          xuiInput
          class="h-5 w-12 px-1 text-end text-sm"
          aria-label="Time signature"
          [value]="signatureText()"
          (keydown.enter)="commitSignature(editor.value)"
          (keydown.escape)="editing.set(null)"
          (blur)="commitSignature(editor.value)"
        />
      } @else {
        <button
          type="button"
          class="hover:text-primary text-sm"
          data-testid="lcd-signature"
          [attr.aria-label]="'Time signature ' + signatureText() + ', click to change'"
          (click)="editing.set('signature')"
        >
          {{ signatureText() }}
        </button>
      }
      <span class="text-foreground-muted text-[10px]">time</span>
    </div>
  `
})
export class Lcd {
  protected readonly transport = inject(TransportStore);
  protected readonly bars = signal('1 1 1   1');
  protected readonly time = signal(clockTime(0));
  protected readonly editing = signal<'tempo' | 'signature' | null>(null);
  private readonly editor = viewChild<ElementRef<HTMLInputElement>>('editor');

  constructor() {
    let frame = 0;
    const draw = () => {
      const now = performance.now();
      const ticks = this.transport.positionAt(now);
      const { bar, beat, division, tick } = barsBeats(ticks, this.transport.signature());
      this.bars.set(`${bar} ${beat} ${division} ${String(tick).padStart(3, ' ')}`);
      this.time.set(clockTime(this.transport.secondsAt(now)));
      frame = requestAnimationFrame(draw);
    };
    if (typeof requestAnimationFrame === 'function') {
      frame = requestAnimationFrame(draw);
    }
    inject(DestroyRef).onDestroy(() => {
      if (typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(frame);
      }
    });
    effect(() => {
      const input = this.editor()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  protected signatureText(): string {
    const { numerator, denominator } = this.transport.signature();
    return `${numerator}/${denominator}`;
  }

  protected commitTempo(text: string): void {
    if (this.editing() !== 'tempo') {
      return;
    }

    this.editing.set(null);
    const bpm = Number(text.replace(',', '.'));
    if (Number.isFinite(bpm) && bpm >= 20 && bpm <= 999) {
      this.transport.setTempo(bpm);
    }
  }

  protected commitSignature(text: string): void {
    if (this.editing() !== 'signature') {
      return;
    }

    this.editing.set(null);
    const signature = parseSignature(text);
    if (signature) {
      this.transport.setSignature(signature);
    }
  }
}
