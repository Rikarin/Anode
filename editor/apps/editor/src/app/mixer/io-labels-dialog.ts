import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { IoLabels } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { XuiTabsImports } from '@xui/tabs';
import { IoLabelsStore, NoLabels, withLabel } from '../state/io-labels.store';
import { UiStore } from '../state/ui.store';
import { BusCount, DefaultOutputPairs } from './routing';

/** Device inputs the window lists when none beyond them is labelled. */
const DefaultInputs = 16;

type Kind = keyof IoLabels;

/** A label table's rows: the default count, or more when a higher number is labelled. */
export function labelRows(kind: Kind, labels: IoLabels): { number: number; channel: string; name: string }[] {
  const base = kind === 'inputs' ? DefaultInputs : kind === 'outputs' ? DefaultOutputPairs : BusCount;
  const count = Math.max(base, ...labels[kind].map(l => l.number));
  return Array.from({ length: count }, (_, i) => {
    const number = i + 1;
    const channel =
      kind === 'inputs'
        ? `Input ${number}`
        : kind === 'outputs'
          ? `Output ${2 * number - 1}-${2 * number}`
          : `Bus ${number}`;
    return { number, channel, name: labels[kind].find(l => l.number === number)?.name ?? '' };
  });
}

/**
 * Logic's I/O Labels window: a user name per device input, output pair and bus, which the Mixer's pickers and slots
 * show in place of "Bus 3". Edited here and saved together (`/settings/io-labels`); an empty name goes back to the
 * default.
 */
@Component({
  selector: 'an-io-labels-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports, XuiTabsImports],
  template: `
    <xui-dialog [(open)]="ui.ioLabelsOpen" title="I/O Labels" [canOutsideClickClose]="false">
      <xui-dialog-body class="flex min-h-0 flex-col" data-testid="io-labels">
        <xui-tabs class="flex min-h-0 flex-col" [(selectedTabId)]="tab">
          @for (kind of kinds; track kind.value) {
            <xui-tab [id]="kind.value" [title]="kind.label">
              <div
                class="border-hairline bg-surface-inset mt-2 max-h-[50vh] overflow-y-auto rounded-md border"
                role="table"
                [attr.aria-label]="kind.label + ' labels'"
              >
                @for (row of rows(kind.value); track row.number) {
                  <div
                    class="border-hairline flex items-center gap-3 border-b px-3 py-1 text-xs last:border-b-0"
                    role="row"
                  >
                    <span class="text-foreground-muted readout w-24 shrink-0" role="cell">{{ row.channel }}</span>
                    <span class="flex-1" role="cell">
                      <input
                        #field
                        xuiInput
                        class="h-6 w-full text-xs"
                        [attr.aria-label]="'Label of ' + row.channel"
                        [attr.data-testid]="'io-label-' + kind.value + '-' + row.number"
                        [placeholder]="row.channel"
                        [value]="row.name"
                        (input)="edit(kind.value, row.number, field.value)"
                      />
                    </span>
                  </div>
                }
              </div>
            </xui-tab>
          }
        </xui-tabs>
      </xui-dialog-body>
      <xui-dialog-footer>
        <button xuiButton type="button" variant="ghost" (click)="ui.ioLabelsOpen.set(false)">Cancel</button>
        <button xuiButton type="button" data-testid="save-io-labels" [loading]="saving()" (click)="save()">Save</button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class IoLabelsDialog {
  protected readonly ui = inject(UiStore);
  private readonly store = inject(IoLabelsStore);

  protected readonly kinds: { value: Kind; label: string }[] = [
    { value: 'inputs', label: 'Inputs' },
    { value: 'outputs', label: 'Outputs' },
    { value: 'buses', label: 'Buses' }
  ];
  protected readonly tab = signal<string>('buses');
  /** The labels when the window opened: what the rows show (edits go to {@link draft} only). */
  private readonly initial = signal<IoLabels>(NoLabels);
  private draft: IoLabels = NoLabels;
  protected readonly saving = signal(false);
  private readonly tables = computed(() => {
    const initial = this.initial();
    return new Map(this.kinds.map(k => [k.value, labelRows(k.value, initial)]));
  });

  constructor() {
    // Each time the window opens it starts from the saved labels.
    effect(() => {
      if (this.ui.ioLabelsOpen()) {
        untracked(() => {
          this.initial.set(this.store.labels());
          this.draft = structuredClone(this.store.labels());
        });
      }
    });
  }

  protected rows(kind: Kind) {
    return this.tables().get(kind) ?? [];
  }

  protected edit(kind: Kind, number: number, name: string): void {
    // Kept as typed, trimmed when saved.
    this.draft = { ...this.draft, [kind]: [...this.draft[kind].filter(l => l.number !== number), { number, name }] };
  }

  protected async save(): Promise<void> {
    const draft = this.draft;
    const tidy = (kind: Kind) => draft[kind].reduce<IoLabels[Kind]>((list, l) => withLabel(list, l.number, l.name), []);
    this.saving.set(true);
    const ok = await this.store.save({ inputs: tidy('inputs'), outputs: tidy('outputs'), buses: tidy('buses') });
    this.saving.set(false);
    if (ok) {
      this.ui.ioLabelsOpen.set(false);
    }
  }
}
