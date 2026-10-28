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
import { XuiMenuImports } from '@xui/menu';
import { KeyNames } from '../global/global-math';
import { ProjectStore } from '../state/project.store';
import { SmartTempoStore, TempoModes } from '../state/smart-tempo.store';
import { TransportStore, clockTime, parseSignature } from '../state/transport.store';

/**
 * The LCD in the control bar (Logic's, in its glossy glass): the playhead as bars/beats/divisions/ticks and as clock
 * time, the tempo and time signature, each editable in place (click, type, Return; Escape cancels), and the project's
 * key. Under the tempo, Logic's project tempo mode menu (Keep, Adapt, Automatic: Smart Tempo). It reads the playhead from its own animation frames while playing, not through change detection.
 */
@Component({
  selector: 'an-lcd',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiInputImports, XuiMenuImports],
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
      <button
        type="button"
        class="text-lcd-muted hover:text-lcd-accent mt-1 flex items-center gap-0.5 text-[9px] font-semibold tracking-[0.08em] uppercase transition-colors duration-100"
        aria-label="Project tempo mode"
        data-testid="tempo-mode"
        [attr.title]="'Project tempo mode: ' + tempoModeLabel()"
        [xuiMenuTriggerFor]="tempoModeMenu"
      >
        bpm · {{ tempoModeShort() }}
        <svg class="size-2 fill-current" viewBox="0 0 8 8" aria-hidden="true"><path d="M1 2.5h6L4 6z" /></svg>
      </button>
      <ng-template #tempoModeMenu>
        <xui-menu class="min-w-60">
          @for (mode of tempoModes; track mode.value) {
            <button
              xuiMenuItem
              type="button"
              role="menuitemradio"
              [attr.aria-checked]="smartTempo.mode() === mode.value"
              [attr.data-testid]="'tempo-mode-' + mode.value"
              [attr.title]="mode.detail"
              (click)="smartTempo.setMode(mode.value)"
            >
              {{ smartTempo.mode() === mode.value ? '✓ ' : '' }}{{ mode.label }}
            </button>
          }
        </xui-menu>
      </ng-template>
    </div>
    <!-- Time signature and key: from 1280 px wide (narrower, the global tracks show and edit them). -->
    <div
      class="flex flex-col items-center justify-center border-s border-(--lcd-divider) px-3 leading-none max-xl:hidden"
    >
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
    <div
      class="flex flex-col items-center justify-center border-s border-(--lcd-divider) px-3 leading-none max-xl:hidden"
    >
      <span class="text-[15px] font-semibold" data-testid="lcd-key">{{ keyText() }}</span>
      <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">key</span>
    </div>
  `
})
export class Lcd {
  protected readonly transport = inject(TransportStore);
  private readonly project = inject(ProjectStore);
  /** The key at the start (the key track's first event; the key track edits the rest). */
  protected readonly keyText = computed(() => {
    const key = this.project.settings().keys[0];
    return key ? `${KeyNames[key.tonic] ?? '?'} ${key.mode === 'minor' ? 'min' : 'maj'}` : '—';
  });
  protected readonly smartTempo = inject(SmartTempoStore);
  protected readonly tempoModes = TempoModes;
  protected readonly tempoModeLabel = computed(
    () => TempoModes.find(m => m.value === this.smartTempo.mode())?.label ?? ''
  );
  /** The mode as the LCD abbreviates it under the tempo: KEEP, ADAPT or AUTO. */
  protected readonly tempoModeShort = computed(
    () => ({ keep: 'Keep', adapt: 'Adapt', auto: 'Auto' })[this.smartTempo.mode()]
  );
  protected readonly bars = signal('1 1 1   1');
  protected readonly time = signal(clockTime(0));
  protected readonly editing = signal<'tempo' | 'signature' | null>(null);
  private readonly editor = viewChild<ElementRef<HTMLInputElement>>('editor');

  constructor() {
    let frame = 0;
    const draw = () => {
      const now = performance.now();
      const ticks = this.transport.positionAt(now);
      const { bar, beat, division, tick } = this.project.meter().position(ticks);
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
