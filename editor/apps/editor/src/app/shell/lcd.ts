import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  computed,
  effect,
  inject,
  signal,
  viewChild
} from '@angular/core';
import { XuiInputImports } from '@xui/input';
import { ProjectStore } from '../state/project.store';
import { TransportStore, barsBeats, clockTime, parseSignature } from '../state/transport.store';

/** Pitch-class names for the key, as Logic spells them. */
const KeyNames = ['C', 'C♯', 'D', 'E♭', 'E', 'F', 'F♯', 'G', 'A♭', 'A', 'B♭', 'B'];

/**
 * The LCD in the control bar (Logic's, in its glossy glass): the playhead as bars/beats/divisions/ticks and as clock
 * time, the tempo and time signature, each editable in place (click, type, Return; Escape cancels), and the project's
 * key. It reads the playhead from its own animation frames while playing, not through change detection.
 */
@Component({
  selector: 'an-lcd',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiInputImports],
  host: {
    class: 'an-lcd lcd-glass flex h-10 items-stretch px-1 font-sans',
    role: 'group',
    'aria-label': 'Position'
  },
  template: `
    <div class="flex min-w-36 flex-col items-center justify-center px-3 leading-none">
      <span class="text-[19px] font-semibold tracking-[0.02em] whitespace-pre" data-testid="lcd-bars">{{
        bars()
      }}</span>
      <span class="text-lcd-muted mt-1 text-[10px] font-medium" data-testid="lcd-time">{{ time() }}</span>
    </div>
    <div class="flex flex-col items-center justify-center border-s border-(--lcd-divider) px-3 leading-none">
      @if (editing() === 'tempo') {
        <input
          #editor
          xuiInput
          class="text-lcd-foreground border-lcd-accent h-5 w-14 bg-transparent px-1 text-center text-[15px] font-semibold"
          aria-label="Tempo in bpm"
          [value]="transport.tempo()"
          (keydown.enter)="commitTempo(editor.value)"
          (keydown.escape)="editing.set(null)"
          (blur)="commitTempo(editor.value)"
        />
      } @else {
        <button
          type="button"
          class="hover:text-lcd-accent text-[15px] font-semibold transition-colors duration-100"
          data-testid="lcd-tempo"
          [attr.aria-label]="'Tempo ' + transport.tempo() + ' bpm, click to change'"
          (click)="editing.set('tempo')"
        >
          {{ transport.tempo() }}
        </button>
      }
      <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">bpm</span>
    </div>
    <div class="flex flex-col items-center justify-center border-s border-(--lcd-divider) px-3 leading-none">
      @if (editing() === 'signature') {
        <input
          #editor
          xuiInput
          class="text-lcd-foreground border-lcd-accent h-5 w-12 bg-transparent px-1 text-center text-[15px] font-semibold"
          aria-label="Time signature"
          [value]="signatureText()"
          (keydown.enter)="commitSignature(editor.value)"
          (keydown.escape)="editing.set(null)"
          (blur)="commitSignature(editor.value)"
        />
      } @else {
        <button
          type="button"
          class="hover:text-lcd-accent text-[15px] font-semibold transition-colors duration-100"
          data-testid="lcd-signature"
          [attr.aria-label]="'Time signature ' + signatureText() + ', click to change'"
          (click)="editing.set('signature')"
        >
          {{ signatureText() }}
        </button>
      }
      <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">time</span>
    </div>
    <div class="flex flex-col items-center justify-center border-s border-(--lcd-divider) px-3 leading-none">
      <span class="text-[15px] font-semibold" data-testid="lcd-key">{{ keyText() }}</span>
      <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">key</span>
    </div>
  `
})
export class Lcd {
  protected readonly transport = inject(TransportStore);
  private readonly project = inject(ProjectStore);
  /** The project's key at its start (the key track's first event), read-only until the global tracks land. */
  protected readonly keyText = computed(() => {
    const key = this.project.settings().keys[0];
    return key ? `${KeyNames[key.tonic] ?? '?'} ${key.mode === 'minor' ? 'min' : 'maj'}` : '—';
  });
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
