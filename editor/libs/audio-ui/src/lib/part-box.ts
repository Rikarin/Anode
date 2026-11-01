import { ChangeDetectionStrategy, Component, booleanAttribute, input, output } from '@angular/core';

/** A note value as the score names it. */
export type PartNoteValue = 'whole' | 'half' | 'quarter' | 'eighth' | 'sixteenth' | 'thirtySecond';

/** A score symbol kind (the model's `ScoreSymbolKind`). */
export type PartSymbolKind =
  | 'staccato'
  | 'staccatissimo'
  | 'accent'
  | 'marcato'
  | 'tenuto'
  | 'fermata'
  | 'trill'
  | 'tieHidden'
  | 'dynamic'
  | 'text'
  | 'tempoText'
  | 'rehearsal'
  | 'pedalDown'
  | 'pedalUp'
  | 'slur'
  | 'crescendo'
  | 'diminuendo'
  | 'octaveUp'
  | 'octaveDown';

/** What the Part box arms: a note or rest value (with dots and triplet), a symbol (a dynamic with its marking), lyrics or chord symbols. */
export type PartBoxTool =
  | { kind: 'note'; value: PartNoteValue; dots: number; triplet: boolean }
  | { kind: 'rest'; value: PartNoteValue; dots: number; triplet: boolean }
  | { kind: 'symbol'; symbol: PartSymbolKind; text?: string }
  | { kind: 'lyric' }
  | { kind: 'chord' };

interface Item {
  id: string;
  /** What the chip shows. */
  glyph: string;
  /** Its accessible name. */
  name: string;
  tool: PartBoxTool;
}

interface Group {
  name: string;
  items: Item[];
}

const Values: readonly { value: PartNoteValue; glyph: string; name: string }[] = [
  { value: 'whole', glyph: '1', name: 'Whole' },
  { value: 'half', glyph: '½', name: 'Half' },
  { value: 'quarter', glyph: '¼', name: 'Quarter' },
  { value: 'eighth', glyph: '⅛', name: 'Eighth' },
  { value: 'sixteenth', glyph: '16', name: 'Sixteenth' },
  { value: 'thirtySecond', glyph: '32', name: 'Thirty-second' }
];

const symbol = (id: PartSymbolKind, glyph: string, name: string): Item => ({
  id,
  glyph,
  name,
  tool: { kind: 'symbol', symbol: id }
});

const dynamic = (text: string): Item => ({
  id: `dynamic-${text}`,
  glyph: text,
  name: `Dynamic ${text}`,
  tool: { kind: 'symbol', symbol: 'dynamic', text }
});

/** The Part box's groups (Logic's, the subset the score model has). */
export const PartBoxGroups: readonly Group[] = [
  {
    name: 'Notes',
    items: Values.map(v => ({
      id: `note-${v.value}`,
      glyph: v.glyph,
      name: `${v.name} note`,
      tool: { kind: 'note', value: v.value, dots: 0, triplet: false }
    }))
  },
  {
    name: 'Rests',
    items: Values.map(v => ({
      id: `rest-${v.value}`,
      glyph: v.glyph,
      name: `${v.name} rest`,
      tool: { kind: 'rest', value: v.value, dots: 0, triplet: false }
    }))
  },
  {
    name: 'Articulations',
    items: [
      symbol('staccato', '·', 'Staccato'),
      symbol('staccatissimo', '▾', 'Staccatissimo'),
      symbol('accent', '>', 'Accent'),
      symbol('marcato', '^', 'Marcato'),
      symbol('tenuto', '–', 'Tenuto'),
      symbol('fermata', '⌒·', 'Fermata'),
      symbol('trill', 'tr', 'Trill'),
      symbol('tieHidden', '⁀̸', 'Hide tie')
    ]
  },
  {
    name: 'Dynamics',
    items: ['pp', 'p', 'mp', 'mf', 'f', 'ff', 'fff', 'sf', 'sfz', 'fp'].map(dynamic)
  },
  {
    name: 'Lines',
    items: [
      symbol('slur', '⌒', 'Slur'),
      symbol('crescendo', '<', 'Crescendo'),
      symbol('diminuendo', '>', 'Diminuendo'),
      symbol('octaveUp', '8va', 'Octave up (8va)'),
      symbol('octaveDown', '8vb', 'Octave down (8vb)')
    ]
  },
  {
    name: 'Text',
    items: [
      symbol('text', 'Txt', 'Text'),
      symbol('tempoText', 'Tmp', 'Tempo text'),
      symbol('rehearsal', '[A]', 'Rehearsal mark'),
      { id: 'lyric', glyph: 'Lyr', name: 'Lyrics', tool: { kind: 'lyric' } },
      { id: 'chord', glyph: 'C7', name: 'Chord symbol', tool: { kind: 'chord' } }
    ]
  },
  {
    name: 'Pedal',
    items: [symbol('pedalDown', 'Ped.', 'Pedal down'), symbol('pedalUp', '✱', 'Pedal up')]
  }
];

/** Whether an item is the armed tool (a value matches whatever its dots and triplet). */
function armed(item: Item, tool: PartBoxTool | null): boolean {
  if (tool?.kind !== item.tool.kind) {
    return false;
  }
  switch (tool.kind) {
    case 'note':
    case 'rest':
      return item.tool.kind === tool.kind && item.tool.value === tool.value;
    case 'symbol':
      return (
        item.tool.kind === 'symbol' &&
        item.tool.symbol === tool.symbol &&
        (item.tool.text ?? null) === (tool.text ?? null)
      );
    default:
      return true;
  }
}

/**
 * Logic's Part box, the presentational part (DESIGN.md "Phase 9 patterns"): groups of chips under Inspector bands —
 * note and rest values with dotted and triplet keys, articulations, dynamics, lines, text, lyrics, chord symbols and
 * pedal. A chip arms its tool (`toolChange`); the armed chip again disarms it. Each group is a radio group with arrow
 * keys. The owner applies a tool to the selected notes, or arms it for the next click in the score.
 */
@Component({
  selector: 'an-part-box',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'flex flex-col overflow-y-auto pb-2 select-none',
    role: 'group',
    'aria-label': 'Part box',
    'data-testid': 'part-box'
  },
  template: `
    @for (group of groups; track group.name) {
      <div
        class="text-foreground border-hairline bg-surface-raised truncate border-y px-2 py-0.5 text-[10px] font-bold shadow-[inset_0_1px_0_var(--highlight)]"
        [id]="'an-part-' + group.name"
      >
        {{ group.name }}
      </div>
      <div
        class="grid grid-cols-4 gap-1 px-2 py-1.5"
        role="radiogroup"
        tabindex="-1"
        [attr.aria-labelledby]="'an-part-' + group.name"
        (keydown)="arrow($event)"
      >
        @for (item of group.items; track item.id; let first = $first) {
          <button
            type="button"
            role="radio"
            class="bezel text-foreground h-6 min-w-0 truncate px-1 text-[11px] font-semibold disabled:opacity-35"
            [class.text-lit]="isArmed(item)"
            [attr.aria-checked]="isArmed(item)"
            [attr.aria-label]="item.name"
            [attr.title]="item.name"
            [attr.tabindex]="isArmed(item) || (first && !groupArmed(group)) ? 0 : -1"
            [attr.data-testid]="'part-' + item.id"
            [disabled]="disabled()"
            (click)="choose(item)"
          >
            {{ item.glyph }}
          </button>
        }
      </div>
      @if (group.name === 'Notes') {
        <div class="flex gap-1 px-2 pb-1.5" role="group" aria-label="Note value options">
          <button
            type="button"
            class="bezel text-foreground h-6 flex-1 text-[11px] font-semibold disabled:opacity-35"
            [class.text-lit]="dotted()"
            [attr.aria-pressed]="dotted()"
            data-testid="part-dotted"
            [disabled]="disabled()"
            (click)="dottedChange.emit(!dotted())"
          >
            Dotted
          </button>
          <button
            type="button"
            class="bezel text-foreground h-6 flex-1 text-[11px] font-semibold disabled:opacity-35"
            [class.text-lit]="triplet()"
            [attr.aria-pressed]="triplet()"
            data-testid="part-triplet"
            [disabled]="disabled()"
            (click)="tripletChange.emit(!triplet())"
          >
            Triplet
          </button>
        </div>
      }
    }
  `
})
export class AnPartBox {
  /** The armed tool (null: the pointer). */
  readonly tool = input<PartBoxTool | null>(null);
  /** Whether note and rest values are dotted, and triplets. */
  readonly dotted = input(false, { transform: booleanAttribute });
  readonly triplet = input(false, { transform: booleanAttribute });
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly toolChange = output<PartBoxTool | null>();
  readonly dottedChange = output<boolean>();
  readonly tripletChange = output<boolean>();

  protected readonly groups = PartBoxGroups;

  protected isArmed(item: Item): boolean {
    return armed(item, this.tool());
  }

  protected groupArmed(group: Group): boolean {
    return group.items.some(i => armed(i, this.tool()));
  }

  protected choose(item: Item): void {
    if (armed(item, this.tool())) {
      this.toolChange.emit(null);
      return;
    }
    const tool = item.tool;
    this.toolChange.emit(
      tool.kind === 'note' || tool.kind === 'rest'
        ? { ...tool, dots: this.dotted() ? 1 : 0, triplet: this.triplet() }
        : tool
    );
  }

  /** ←/→ (and ↑/↓) move between a group's chips. */
  protected arrow(event: KeyboardEvent): void {
    const step =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0;
    if (!step) {
      return;
    }
    event.preventDefault();
    const chips = Array.from(
      (event.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>('[role="radio"]')
    );
    const index = chips.indexOf(event.target as HTMLButtonElement);
    chips[(index + step + chips.length) % chips.length]?.focus();
  }
}
