import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import type { DrumPad, PluginInfo, Track } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import {
  matChevronLeftRound,
  matChevronRightRound,
  matDeleteOutlineRound,
  matFolderOpenRound
} from '@ng-icons/material-icons/round';
import { XuiButtonImports } from '@xui/button';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { ConnectionService } from '../core/connection';
import { ParameterEditor } from '../inspector/parameter-editor';
import { noteName } from '../midi/midi-math';
import { InstrumentStore } from '../state/instrument.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { DrumSynthId, FirstPage, LastPage, padGrid, padLabel, pageLabel, pageOf, QuickSamplerId } from './drum-machine';
import { DrumMachineStore } from './drum-machine.store';

const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 min-w-0 rounded-[5px] border px-1 text-xs focus-visible:outline-2';

const Band =
  'text-foreground border-hairline bg-surface-raised truncate border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

const Notes = Array.from({ length: 128 }, (_, note) => note);

/**
 * The Drum Machine Designer (Logic's) for the selected DMD track or pad: the pad grid, 4 × 4 by note from C1 with pages
 * for the rest of the MIDI range; a click on a pad auditions and selects it, on an empty pad adds one (Drum Synth). The
 * selected pad's name, instrument, note and removal, then its instrument's editor (a custom panel where one exists,
 * else the generic controls) and, for an instrument that plays a file (Quick Sampler), loading one.
 */
@Component({
  selector: 'an-drum-machine-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ParameterEditor, XuiButtonImports, XuiIconImports, XuiInputImports],
  providers: [provideIcons({ matChevronLeftRound, matChevronRightRound, matDeleteOutlineRound, matFolderOpenRound })],
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Drum Machine Designer' },
  template: `
    @if (dmd(); as d) {
      <div class="flex min-h-0 flex-1">
        <div class="border-hairline flex shrink-0 flex-col gap-2 border-e p-3">
          <div class="flex items-center gap-2">
            <span class="text-foreground min-w-0 flex-1 truncate text-xs font-semibold">{{ d.name }}</span>
            <div class="bezel-group text-foreground h-6 text-[11px]" role="group" aria-label="Pad pages">
              <button
                type="button"
                class="grid w-6 place-items-center disabled:opacity-35"
                aria-label="Lower pads"
                data-testid="pads-page-down"
                [disabled]="page() <= firstPage"
                (click)="setPage(page() - 1)"
              >
                <ng-icon xui name="matChevronLeftRound" size="14px" />
              </button>
              <span class="readout grid min-w-20 place-items-center px-1 font-semibold" data-testid="pads-page">
                {{ pageName() }}
              </span>
              <button
                type="button"
                class="grid w-6 place-items-center disabled:opacity-35"
                aria-label="Higher pads"
                data-testid="pads-page-up"
                [disabled]="page() >= lastPage"
                (click)="setPage(page() + 1)"
              >
                <ng-icon xui name="matChevronRightRound" size="14px" />
              </button>
            </div>
          </div>
          <div class="bg-surface-sunken well grid gap-1.5 rounded-md p-1.5" role="grid" aria-label="Pads">
            @for (row of grid(); track $index) {
              <div class="grid grid-cols-4 gap-1.5" role="row">
                @for (cell of row; track $index) {
                  <div role="gridcell" [attr.aria-selected]="cell.note !== null && cell.note === selectedNote()">
                    @if (cell.note !== null) {
                      <button
                        type="button"
                        class="bezel flex size-18 flex-col items-start justify-between overflow-hidden p-1.5 text-start"
                        data-testid="drum-pad"
                        [attr.data-note]="cell.note"
                        [attr.aria-label]="label(cell.pad, cell.note)"
                        [class.ring-2]="cell.note === selectedNote()"
                        [class.ring-primary]="cell.note === selectedNote()"
                        [class.opacity-60]="!cell.pad"
                        (click)="press(d, cell.note, cell.pad)"
                      >
                        @if (cell.pad; as pad) {
                          <span class="h-1 w-full rounded-full" [style.background-color]="d.color"></span>
                          <span class="text-foreground line-clamp-2 w-full text-[11px] leading-tight font-semibold">
                            {{ pad.name }}
                          </span>
                        } @else {
                          <span class="h-1"></span>
                          <span class="text-foreground-muted text-[18px] leading-none" aria-hidden="true">+</span>
                        }
                        <span class="text-foreground-muted readout text-[10px]">{{ noteName(cell.note) }}</span>
                      </button>
                    } @else {
                      <span class="block size-18" aria-hidden="true"></span>
                    }
                  </div>
                }
              </div>
            }
          </div>
          <p class="text-foreground-muted max-w-76 text-[11px]">
            Click a pad to hear and select it; an empty pad adds a Drum Synth.
          </p>
        </div>

        <div class="flex min-w-0 flex-1 flex-col overflow-y-auto" data-testid="pad-editor">
          @if (selectedPad(); as pad) {
            <h3 [class]="band">Pad: {{ label(pad, pad.note) }}</h3>
            <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-1.5 px-3 py-2 text-xs">
              <label class="text-foreground-muted" for="dmd-pad-name">Name</label>
              <input
                #nameField
                id="dmd-pad-name"
                xuiInput
                class="h-6 text-xs"
                aria-label="Pad name"
                data-testid="pad-name"
                [value]="pad.name"
                (keydown.enter)="nameField.blur()"
                (keydown.escape)="nameField.value = pad.name; nameField.blur()"
                (blur)="store.rename(d.id, pad.note, nameField.value)"
              />
              <label class="text-foreground-muted" for="dmd-pad-instrument">Instrument</label>
              <select
                id="dmd-pad-instrument"
                [class]="field"
                aria-label="Pad instrument"
                data-testid="pad-instrument"
                (change)="setInstrument(d, pad, $event)"
              >
                @for (plugin of instruments(); track plugin.id) {
                  <option [value]="plugin.id" [selected]="plugin.id === padTrack()?.instrument?.pluginId">
                    {{ plugin.name }}
                  </option>
                }
              </select>
              <label class="text-foreground-muted" for="dmd-pad-note">Note</label>
              <div class="flex items-center gap-1">
                <select
                  id="dmd-pad-note"
                  [class]="field + ' w-20'"
                  aria-label="Pad note"
                  data-testid="pad-note"
                  (change)="moveTo(d, pad, $event)"
                >
                  @for (note of notes; track note) {
                    <option
                      [value]="note"
                      [selected]="note === pad.note"
                      [disabled]="taken().has(note) && note !== pad.note"
                    >
                      {{ noteName(note) }} ({{ note }})
                    </option>
                  }
                </select>
                <button
                  type="button"
                  class="bezel text-foreground ms-auto flex h-6 items-center gap-1 px-2 text-[11px] font-semibold"
                  data-testid="remove-pad"
                  [attr.aria-label]="'Remove pad ' + label(pad, pad.note)"
                  (click)="store.remove(d.id, pad.note)"
                >
                  <ng-icon xui name="matDeleteOutlineRound" size="14px" aria-hidden="true" />
                  Remove
                </button>
              </div>
            </div>
            @if (padTrack(); as t) {
              @if (padPlugin(); as p) {
                @if (p.fileTypes.length > 0) {
                  <div class="flex flex-col gap-1 px-3 pb-2 text-xs">
                    <span
                      class="text-foreground-muted truncate"
                      data-testid="pad-file"
                      [attr.title]="t.instrument?.file"
                    >
                      {{ t.instrument?.file ?? 'No file loaded' }}
                    </span>
                    @if (desktopDialog) {
                      <button
                        xuiButton
                        type="button"
                        size="sm"
                        variant="outline"
                        class="self-start"
                        (click)="loadWithDialog(t, p)"
                      >
                        <ng-icon xui name="matFolderOpenRound" size="sm" />
                        Load file…
                      </button>
                    } @else {
                      <form
                        class="flex gap-1"
                        (submit)="$event.preventDefault(); instrumentStore.loadFile(t.id, path.value)"
                      >
                        <input
                          #path
                          xuiInput
                          class="h-7 min-w-0 flex-1 text-xs"
                          data-testid="pad-file-path"
                          [attr.aria-label]="'Path of a ' + p.fileTypes.join(' or ') + ' file on the server'"
                          [placeholder]="'/path/to/sample' + (p.fileTypes[0] ?? '')"
                        />
                        <button xuiButton type="submit" size="sm" variant="outline">Load</button>
                      </form>
                    }
                  </div>
                }
                <an-parameter-editor class="px-3 pb-3" [trackId]="t.id" [plugin]="p" />
              } @else if (t.instrument; as slot) {
                <p class="text-foreground-muted px-3 text-xs">
                  {{ slot.name }} ({{ slot.pluginId }}) is not in the engine's catalogue.
                </p>
              }
            }
          } @else {
            <p class="text-foreground-muted p-3 text-xs">
              No pad selected. Click a pad to select it, or an empty pad to add one.
            </p>
          }
        </div>
      </div>
    } @else {
      <p class="text-foreground-muted p-3 text-xs">
        Select a Drum Machine Designer track or one of its pads, or create one with the Tracks area's + menu (Drum
        Machine Designer) or a kit in the Library.
      </p>
    }
  `
})
export class DrumMachineEditor {
  protected readonly store = inject(DrumMachineStore);
  protected readonly instrumentStore = inject(InstrumentStore);
  private readonly project = inject(ProjectStore);
  private readonly plugins = inject(PluginsStore);

  protected readonly desktopDialog = typeof inject(ConnectionService).desktop?.dialogs?.instrumentFile === 'function';
  protected readonly field = Field;
  protected readonly band = Band;
  protected readonly notes = Notes;
  protected readonly firstPage = FirstPage;
  protected readonly lastPage = LastPage;
  protected readonly noteName = noteName;
  protected readonly label = padLabel;

  protected readonly dmd = this.store.current;
  protected readonly page = signal(0);
  protected readonly pageName = computed(() => pageLabel(this.page()));
  protected readonly grid = computed(() => padGrid(this.dmd()?.drumMachine?.pads ?? [], this.page()));
  protected readonly selectedNote = computed(() => {
    const dmd = this.dmd();
    return dmd ? this.store.selectedNote(dmd.id) : null;
  });
  protected readonly selectedPad = computed(
    () => this.dmd()?.drumMachine?.pads.find(p => p.note === this.selectedNote()) ?? null
  );
  protected readonly padTrack = computed(() => {
    const pad = this.selectedPad();
    return pad ? (this.project.track(pad.trackId) ?? null) : null;
  });
  protected readonly padPlugin = computed(() => this.plugins.plugin(this.padTrack()?.instrument?.pluginId));
  protected readonly taken = computed(() => new Set((this.dmd()?.drumMachine?.pads ?? []).map(p => p.note)));
  /** The instruments a pad can play: Drum Synth and Quick Sampler first (Logic's), then the rest by name. */
  protected readonly instruments = computed(() => {
    const rank = (p: PluginInfo) => (p.id === DrumSynthId ? 0 : p.id === QuickSamplerId ? 1 : 2);
    return [...this.plugins.instruments()].sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
  });

  constructor() {
    // Show the page of the selected pad when it changes (a pad track selected in the Tracks area, a move).
    effect(() => {
      const note = this.selectedNote();
      if (note !== null) {
        untracked(() => this.page.set(Math.min(LastPage, Math.max(FirstPage, pageOf(note)))));
      }
    });
  }

  protected setPage(page: number): void {
    this.page.set(Math.min(LastPage, Math.max(FirstPage, page)));
  }

  /** A pad: audition and select it; an empty pad: add one there. */
  protected press(dmd: Track, note: number, pad: DrumPad | null): void {
    if (pad) {
      this.store.selectPad(dmd.id, note);
      void this.store.audition(dmd.id, note);
    } else {
      void this.store.addPad(dmd.id, note);
    }
  }

  protected setInstrument(dmd: Track, pad: DrumPad, event: Event): void {
    void this.store.setInstrument(dmd.id, pad.note, (event.target as HTMLSelectElement).value);
  }

  protected moveTo(dmd: Track, pad: DrumPad, event: Event): void {
    void this.store.moveTo(dmd.id, pad.note, Number((event.target as HTMLSelectElement).value));
  }

  protected async loadWithDialog(track: Track, plugin: PluginInfo): Promise<void> {
    const path = await window.anode?.dialogs?.instrumentFile?.(plugin.fileTypes);
    if (path) {
      this.instrumentStore.loadFile(track.id, path);
    }
  }
}
