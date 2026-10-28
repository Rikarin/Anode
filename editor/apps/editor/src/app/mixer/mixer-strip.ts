import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { AutomationMode, ChannelStrip, Insert, PluginInfo, Send, Track } from '@anode/api-client';
import {
  AnAutomationMode,
  AnChannelStrip,
  AnInsertSlot,
  AnSendKnob,
  automationModeLabel,
  AutomationModes,
  type MeterSource,
  type SlotMenuPoint
} from '@anode/audio-ui';
import { XuiContextMenuImports } from '@xui/context-menu';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { AutomationStore } from '../automation/automation.store';
import { IoLabelsStore } from '../state/io-labels.store';
import { MasterMeter, MeterBus } from '../state/meters';
import { MixerStore } from '../state/mixer.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { effectMenu, pluginLabel } from './plugin-menu';
import { PluginWindows } from './plugin-windows';
import {
  busChoices,
  BusCount,
  busName,
  busRoute,
  DefaultOutputPairs,
  lowestFreeBus,
  outputPairName,
  outputRoute,
  routeName
} from './routing';

/** What an insert slot's menu acts on: a filled slot's insert, or an empty slot at `index`. */
interface SlotContext {
  insert: Insert | null;
  index: number;
}

/** The rows the Mixer gives each strip's sections: an input row (when an aux shows), insert slots and send rows. */
export interface StripLayout {
  input: boolean;
  inserts: number;
  sends: number;
}

/** A slot row's height and the gap between rows, in CSS pixels. */
const SlotPx = 18;
const SlotGapPx = 1;

/** Buses in pages of 16, as Logic's bus menus list them. */
const BusPages = Array.from({ length: BusCount / 16 }, (_, page) => ({
  label: `Bus ${page * 16 + 1}–${page * 16 + 16}`,
  first: page * 16 + 1
}));

/**
 * One strip of the Mixer, top to bottom as in Logic: an aux's input bus; the Audio FX slots (a plugin menu by category,
 * bypass, drag or ⌥↑/⌥↓ to reorder, the editor on a click); the sends (bus menu that refuses loops, level knob,
 * pre/post); the output (Stereo Out, a bus, or a device output pair, by I/O label); the group and VCA slots; the
 * automation mode; then pan, fader and meter, mute and solo, and the name. The master strip has its inserts only; a VCA
 * strip has no inserts, sends, output, pan or meter.
 */
@Component({
  selector: 'an-mixer-strip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AnAutomationMode,
    AnChannelStrip,
    AnInsertSlot,
    AnSendKnob,
    XuiContextMenuImports,
    XuiMenuImports,
    XuiTooltipImports
  ],
  host: { class: 'flex h-full' },
  template: `
    <an-channel-strip
      [name]="name()"
      [color]="color()"
      [volumeDb]="mixer.value(trackId(), 'volumeDb')"
      [pan]="mixer.value(trackId(), 'pan')"
      [mute]="strip().mute"
      [solo]="strip().solo"
      [master]="track() === null"
      [vca]="kind() === 'vca'"
      [mono]="track() !== null && track()?.kind !== 'aux' && !track()?.input?.stereo"
      [selected]="selected()"
      [flow]="flow()"
      [meter]="meter()"
      [attr.data-strip]="trackId() ?? 'master'"
      (volumeChange)="mixer.set(trackId(), 'volumeDb', $event)"
      (volumeCommit)="mixer.release(trackId(), 'volumeDb')"
      (panChange)="mixer.set(trackId(), 'pan', $event)"
      (panCommit)="mixer.release(trackId(), 'pan')"
      (muteToggle)="mixer.toggleMute(trackId())"
      (soloToggle)="soloToggle()"
      (click)="select()"
    >
      @if (layout().input) {
        @if (kind() === 'aux') {
          <div class="strip-slot text-foreground justify-center font-semibold" [attr.title]="'Input: ' + inputName()">
            <span class="sr-only">Input:</span>{{ inputName() }}
          </div>
        } @else {
          <div class="h-4.5" aria-hidden="true"></div>
        }
      }
      @if (hasInserts()) {
        <div
          class="flex flex-col gap-px"
          role="group"
          [attr.aria-label]="'Audio FX of ' + name()"
          [style.min-height.px]="rowsHeight(layout().inserts)"
        >
          @for (slot of slots(); track slot.key) {
            <div
              #wrap
              [xuiContextMenuTriggerFor]="insertMenu"
              [xuiContextMenuTriggerData]="{ $implicit: slot.context }"
              [class.opacity-50]="dragging() === slot.context.insert?.id"
              [class.shadow-[inset_0_2px_0_var(--color-primary)]]="dropAt() === slot.context.index"
              (dragstart)="dragStart(slot.context, $event)"
              (dragend)="dragEnd()"
              (dragover)="dragOver(slot.context.index, $event)"
              (dragleave)="dropAt.set(null)"
              (drop)="drop(slot.context.index, $event)"
            >
              <an-insert-slot
                [name]="slot.context.insert?.name ?? null"
                [channel]="name()"
                [index]="slot.context.index"
                [bypassed]="slot.context.insert?.bypassed ?? false"
                [problem]="slot.problem"
                [attr.data-testid]="slot.context.insert ? 'insert-slot' : 'insert-empty'"
                (open)="activate(slot.context, wrap)"
                (bypassToggle)="toggleBypass(slot.context)"
                (menu)="openMenu(wrap, $event)"
                (move)="moveBy(slot.context, $event)"
              />
            </div>
          }
        </div>
      } @else {
        <div aria-hidden="true" [style.height.px]="rowsHeight(layout().inserts)"></div>
      }
      @if (hasSends()) {
        <div
          class="flex flex-col gap-px"
          role="group"
          [attr.aria-label]="'Sends of ' + name()"
          [style.min-height.px]="rowsHeight(layout().sends)"
        >
          @for (send of strip().sends; track send.id; let i = $index) {
            <div #sendWrap [xuiContextMenuTriggerFor]="sendMenu" [xuiContextMenuTriggerData]="{ $implicit: send }">
              <an-send-knob
                data-testid="send"
                [busName]="busLabel(send.bus)"
                [channel]="name()"
                [index]="i"
                [levelDb]="sendLevel(send)"
                [preFader]="send.preFader"
                (levelChange)="setSendLevel(send, $event)"
                (levelCommit)="releaseSend(send)"
                (preToggle)="togglePre(send)"
                (menu)="openMenu(sendWrap, $event)"
              />
            </div>
          }
          <button
            type="button"
            class="strip-slot text-foreground-muted hover:text-foreground justify-center"
            data-testid="add-send"
            [attr.aria-label]="'Add a send to ' + name()"
            [xuiMenuTriggerFor]="sendMenu"
            [xuiMenuTriggerData]="{ $implicit: null }"
          >
            Send
          </button>
        </div>
      } @else {
        <div aria-hidden="true" [style.height.px]="rowsHeight(layout().sends)"></div>
      }
      @if (hasOutput()) {
        <button
          type="button"
          class="strip-slot text-foreground justify-center font-semibold"
          data-testid="output"
          [attr.aria-label]="'Output of ' + name() + ': ' + outputName()"
          [attr.title]="'Output: ' + outputName()"
          [xuiMenuTriggerFor]="outputMenu"
        >
          {{ outputName() }}
        </button>
      } @else {
        <div class="h-4.5" aria-hidden="true"></div>
      }
      @if (track(); as t) {
        <button
          type="button"
          class="strip-slot text-foreground justify-center gap-1"
          data-testid="group-slot"
          [attr.aria-label]="'Group of ' + name() + ': ' + (group()?.name ?? 'none')"
          [attr.title]="group() ? 'Group ' + group()?.number + ': ' + group()?.name : 'No group'"
          [xuiMenuTriggerFor]="groupMenu"
        >
          @if (group(); as g) {
            <span class="size-1.5 shrink-0 rounded-full" [style.background-color]="g.color"></span>
            <span class="truncate font-semibold">{{ g.number }}: {{ g.name }}</span>
          } @else {
            <span class="text-foreground-muted">Group</span>
          }
        </button>
        @if (t.kind !== 'vca' && t.kind !== 'stack') {
          <button
            type="button"
            class="strip-slot text-foreground justify-center"
            data-testid="vca-slot"
            [attr.aria-label]="'VCA of ' + name() + ': ' + (vca()?.name ?? 'none')"
            [xuiMenuTriggerFor]="vcaMenu"
          >
            @if (vca(); as v) {
              <span class="truncate font-semibold">{{ v.name }}</span>
            } @else {
              <span class="text-foreground-muted">VCA</span>
            }
          </button>
        } @else {
          <div class="h-4.5" aria-hidden="true"></div>
        }
        <button
          anAutomationMode
          type="button"
          class="w-full justify-center"
          data-testid="mixer-automation-mode"
          [attr.aria-label]="'Automation mode of ' + name() + ': ' + modeLabel(automation.mode(t.id))"
          [mode]="automation.mode(t.id)"
          [channel]="name()"
          [xuiMenuTriggerFor]="modeMenu"
        ></button>
      } @else {
        <div class="h-[calc(2*1.125rem+1.25rem+0.25rem)]" aria-hidden="true"></div>
      }
    </an-channel-strip>

    <ng-template #insertMenu let-slot>
      <xui-menu class="min-w-44" [attr.aria-label]="'Audio FX menu of ' + name()">
        @if (slot.insert; as insert) {
          <button xuiMenuItem type="button" (click)="windows.open(trackId(), insert)">Open Editor</button>
          <button xuiMenuItem type="button" (click)="toggleBypass(slot)">
            {{ insert.bypassed ? 'Turn On' : 'Bypass' }}
          </button>
          @if (plugin(insert.pluginId)?.hasSidechain) {
            <button
              xuiMenuItem
              type="button"
              [xuiMenuTriggerFor]="sidechainMenu"
              [xuiMenuTriggerData]="{ $implicit: insert }"
            >
              Side Chain: {{ sidechainName(insert.sidechain) }}
            </button>
          }
          <xui-menu-divider />
        }
        @for (group of effects(); track group.category) {
          <button
            xuiMenuItem
            type="button"
            [xuiMenuTriggerFor]="categoryMenu"
            [xuiMenuTriggerData]="{ $implicit: group.plugins, slot }"
          >
            {{ group.category }}
          </button>
        } @empty {
          <span class="text-foreground-muted px-3 py-1 text-xs">No effects in the plugin catalogue.</span>
        }
        @if (slot.insert; as insert) {
          <xui-menu-divider />
          <button xuiMenuItem type="button" color="error" (click)="mixer.removeInsert(trackId(), insert)">
            Remove {{ insert.name }}
          </button>
        }
      </xui-menu>
    </ng-template>
    <ng-template #categoryMenu let-plugins let-slot="slot">
      <xui-menu class="min-w-44">
        @for (plugin of plugins; track plugin.id) {
          <button
            xuiMenuItem
            type="button"
            [attr.data-plugin]="plugin.id"
            [selected]="slot.insert?.pluginId === plugin.id"
            (click)="choose(slot, plugin)"
          >
            {{ label(plugin) }}
          </button>
        }
      </xui-menu>
    </ng-template>
    <ng-template #sidechainMenu let-insert>
      <xui-menu class="max-h-80 min-w-40 overflow-y-auto">
        <button
          xuiMenuItem
          type="button"
          role="menuitemradio"
          [attr.aria-checked]="!insert.sidechain"
          (click)="mixer.setSidechain(trackId(), insert, null)"
        >
          None
        </button>
        <xui-menu-divider />
        @for (other of sidechainTracks(); track other.id) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="insert.sidechain === other.id"
            (click)="mixer.setSidechain(trackId(), insert, other.id)"
          >
            {{ other.name }}
          </button>
        }
        <xui-menu-divider />
        @for (bus of usedBuses(); track bus) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="insert.sidechain === 'bus:' + bus"
            (click)="mixer.setSidechain(trackId(), insert, 'bus:' + bus)"
          >
            {{ busLabel(bus) }}
          </button>
        }
      </xui-menu>
    </ng-template>

    <ng-template #sendMenu let-send>
      <xui-menu class="min-w-48" [attr.aria-label]="'Send menu of ' + name()">
        @if (send) {
          <button xuiMenuItem type="button" (click)="togglePre(send)">
            {{ send.preFader ? 'Post Fader' : 'Pre Fader' }}
          </button>
          <xui-menu-divider />
        }
        <button
          xuiMenuItem
          type="button"
          data-testid="send-new-aux"
          [disabled]="freeBus() === 0"
          (click)="sendToNewAux(send)"
        >
          New Auxiliary Channel Strip (Bus {{ freeBus() }})
        </button>
        <xui-menu-divider />
        @for (page of busPages; track page.first) {
          <button
            xuiMenuItem
            type="button"
            [xuiMenuTriggerFor]="busMenu"
            [xuiMenuTriggerData]="{ $implicit: page.first, pick: sendPicker(send) }"
          >
            {{ page.label }}
          </button>
        }
        @if (send) {
          <xui-menu-divider />
          <button xuiMenuItem type="button" color="error" (click)="removeSend(send)">Remove Send</button>
        }
      </xui-menu>
    </ng-template>
    <ng-template #busMenu let-first let-pick="pick">
      <xui-menu class="min-w-48">
        @for (choice of busPage(first); track choice.bus) {
          <button
            xuiMenuItem
            type="button"
            [attr.data-bus]="choice.bus"
            [disabled]="choice.loops"
            [attr.title]="choice.loops ? 'This would feed the bus back into itself' : null"
            (click)="pick(choice.bus)"
          >
            <span class="flex-1">{{ busLabel(choice.bus) }}</span>
            @if (auxOn(choice.bus); as aux) {
              <span class="text-foreground-muted ms-3 text-[11px]">{{ aux }}</span>
            } @else if (choice.loops) {
              <span class="text-foreground-muted ms-3 text-[11px]">loop</span>
            }
          </button>
        }
      </xui-menu>
    </ng-template>

    <ng-template #outputMenu>
      <xui-menu class="min-w-44" [attr.aria-label]="'Output menu of ' + name()">
        <button
          xuiMenuItem
          type="button"
          role="menuitemradio"
          [attr.aria-checked]="strip().output === 'master'"
          (click)="setOutput('master')"
        >
          Stereo Out
        </button>
        <button xuiMenuItem type="button" [xuiMenuTriggerFor]="outputPairs">Output</button>
        <xui-menu-divider />
        @for (page of busPages; track page.first) {
          <button
            xuiMenuItem
            type="button"
            [xuiMenuTriggerFor]="busMenu"
            [xuiMenuTriggerData]="{ $implicit: page.first, pick: outputPicker }"
          >
            {{ page.label }}
          </button>
        }
      </xui-menu>
    </ng-template>
    <ng-template #outputPairs>
      <xui-menu class="min-w-40">
        @for (pair of pairs(); track pair) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="strip().output === 'output:' + pair"
            (click)="setOutput(outputRoute(pair))"
          >
            {{ pairName(pair) }}
          </button>
        }
      </xui-menu>
    </ng-template>

    <ng-template #groupMenu>
      <xui-menu class="min-w-44" [attr.aria-label]="'Group menu of ' + name()">
        <button xuiMenuItem type="button" role="menuitemradio" [attr.aria-checked]="!group()" (click)="setGroup(null)">
          No Group
        </button>
        @for (g of project.groupList(); track g.number) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="group()?.number === g.number"
            (click)="setGroup(g.number)"
          >
            {{ g.number }}: {{ g.name }}
          </button>
        }
        <xui-menu-divider />
        <button xuiMenuItem type="button" data-testid="new-group" (click)="newGroup()">New Group…</button>
        <button xuiMenuItem type="button" [disabled]="!group()" (click)="ui.groupSettings.set(group()?.number ?? null)">
          Open Group Settings…
        </button>
      </xui-menu>
    </ng-template>

    <ng-template #vcaMenu>
      <xui-menu class="min-w-40" [attr.aria-label]="'VCA menu of ' + name()">
        <button xuiMenuItem type="button" role="menuitemradio" [attr.aria-checked]="!vca()" (click)="assignVca(null)">
          No VCA
        </button>
        @for (v of vcas(); track v.id) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="vca()?.id === v.id"
            (click)="assignVca(v.id)"
          >
            {{ v.name }}
          </button>
        }
        <xui-menu-divider />
        <button xuiMenuItem type="button" (click)="newVca()">New VCA</button>
      </xui-menu>
    </ng-template>

    <ng-template #modeMenu>
      <xui-menu class="min-w-32" [attr.aria-label]="'Automation mode menu of ' + name()">
        @for (mode of modes; track mode.value) {
          <button
            xuiMenuItem
            type="button"
            role="menuitemradio"
            [attr.aria-checked]="trackId() !== null && automation.mode(trackId()) === mode.value"
            (click)="setMode(mode.value)"
          >
            {{ mode.label }}
          </button>
        }
      </xui-menu>
    </ng-template>
  `
})
export class MixerStrip {
  protected readonly mixer = inject(MixerStore);
  protected readonly project = inject(ProjectStore);
  protected readonly automation = inject(AutomationStore);
  protected readonly windows = inject(PluginWindows);
  protected readonly ui = inject(UiStore);
  private readonly plugins = inject(PluginsStore);
  private readonly labels = inject(IoLabelsStore);
  private readonly selection = inject(SelectionStore);
  private readonly meters = inject(MeterBus);

  /** The track whose strip this is; null for the master. */
  readonly track = input.required<Track | null>();
  readonly selected = input(false);
  /** Part of the selected strip's signal flow. */
  readonly flow = input(false);
  /** How many rows each section takes in this Mixer, so the sections line up across strips (as in Logic). */
  readonly layout = input<StripLayout>({ input: false, inserts: 1, sends: 1 });

  protected readonly modes = AutomationModes;
  protected readonly modeLabel = automationModeLabel;
  protected readonly busPages = BusPages;
  protected readonly outputRoute = outputRoute;
  protected readonly label = pluginLabel;
  protected readonly dragging = signal<string | null>(null);
  protected readonly dropAt = signal<number | null>(null);

  protected readonly trackId = computed(() => this.track()?.id ?? null);
  protected readonly kind = computed(() => this.track()?.kind ?? 'master');
  protected readonly strip = computed<ChannelStrip>(() => this.track()?.strip ?? this.project.master());
  protected readonly name = computed(() => this.track()?.name ?? 'Stereo Out');
  protected readonly color = computed(() => this.track()?.color ?? 'var(--color-foreground-muted)');
  protected readonly hasInserts = computed(() => !['vca', 'stack', 'midi'].includes(this.kind()));
  protected readonly hasSends = computed(() => ['audio', 'instrument', 'aux'].includes(this.kind()));
  protected readonly hasOutput = computed(() => ['audio', 'instrument', 'aux'].includes(this.kind()));
  protected readonly effects = computed(() => effectMenu(this.plugins.plugins()));
  protected readonly freeBus = computed(() => lowestFreeBus(this.project.trackList()));
  protected readonly outputName = computed(() => routeName(this.strip().output, this.labels.labels()));
  protected readonly inputName = computed(() => busLabelOf(this.track()?.inputBus ?? 0, this.labels.labels()));
  protected readonly group = computed(() => {
    const number = this.strip().groups[0];
    return number === undefined ? null : (this.project.groups().get(number) ?? null);
  });
  protected readonly vcas = computed(() => this.project.trackList().filter(t => t.kind === 'vca'));
  protected readonly vca = computed(() => {
    const id = this.strip().vcaId;
    return id ? (this.project.track(id) ?? null) : null;
  });
  protected readonly pairs = computed(() => {
    const labelled = this.labels.labels().outputs.map(l => l.number);
    const count = Math.max(DefaultOutputPairs, ...labelled);
    return Array.from({ length: count }, (_, i) => i + 1);
  });
  protected readonly sidechainTracks = computed(() =>
    this.project.trackList().filter(t => t.id !== this.trackId() && t.kind !== 'vca' && t.kind !== 'stack')
  );
  protected readonly usedBuses = computed(() =>
    busChoices(this.project.trackList(), null)
      .filter(c => c.used)
      .map(c => c.bus)
  );
  /** The filled slots, then one empty slot to add to (Logic shows one below the last). */
  protected readonly slots = computed(() => {
    const inserts = this.strip().inserts;
    const trackId = this.trackId();
    const problems = this.windows.problems();
    const filled = inserts.map((insert, index) => ({
      key: insert.id,
      context: { insert, index },
      problem: problems.has(`${trackId ?? 'master'}/${insert.id}`)
    }));
    return [...filled, { key: 'empty', context: { insert: null, index: inserts.length }, problem: false }];
  });
  private readonly meterSource: MeterSource = () => this.meters.level(this.trackId() ?? MasterMeter);
  protected readonly meter = computed(() => (this.kind() === 'vca' ? null : this.meterSource));

  /** Picks the bus of a send (an existing one's, or a new send's). */
  protected sendPicker(send: Send | null): (bus: number) => void {
    return bus => {
      const trackId = this.trackId();
      if (!trackId) {
        return;
      }
      void (send ? this.mixer.setSendBus(trackId, send, bus) : this.mixer.addSend(trackId, bus));
    };
  }

  protected readonly outputPicker = (bus: number) => this.setOutput(busRoute(bus));

  /** The height of `rows` slot rows. */
  protected rowsHeight(rows: number): number {
    return rows * SlotPx + Math.max(0, rows - 1) * SlotGapPx;
  }

  protected busPage(first: number): { bus: number; used: boolean; loops: boolean }[] {
    return busChoices(this.project.trackList(), this.trackId()).slice(first - 1, first + 15);
  }

  protected busLabel(bus: number): string {
    return busName(bus, this.labels.labels());
  }

  protected pairName(pair: number): string {
    return outputPairName(pair, this.labels.labels());
  }

  /** The aux tracks listening to a bus, by name ("Reverb"), for the bus menus. */
  protected auxOn(bus: number): string {
    return this.project
      .trackList()
      .filter(t => t.kind === 'aux' && t.inputBus === bus)
      .map(t => t.name)
      .join(', ');
  }

  protected sidechainName(sidechain: string | null | undefined): string {
    if (!sidechain) {
      return 'None';
    }
    const bus = /^bus:(\d+)$/.exec(sidechain)?.[1];
    return bus ? this.busLabel(Number(bus)) : (this.project.track(sidechain)?.name ?? sidechain);
  }

  protected plugin(id: string): PluginInfo | undefined {
    return this.plugins.plugin(id);
  }

  protected select(): void {
    const id = this.trackId();
    if (id) {
      this.selection.track.set(id);
    }
  }

  protected soloToggle(): void {
    const id = this.trackId();
    if (id) {
      this.mixer.toggleSolo(id);
    }
  }

  // ---- inserts

  /** A filled slot opens its editor; an empty one its plugin menu. */
  protected activate(slot: SlotContext, wrap: HTMLElement): void {
    if (slot.insert) {
      void this.windows.open(this.trackId(), slot.insert);
    } else {
      const box = wrap.getBoundingClientRect();
      this.openMenu(wrap, { x: box.left, y: box.bottom });
    }
  }

  /** Opens a slot's context menu at a point (the chevron's, or under an empty slot). */
  protected openMenu(trigger: HTMLElement, at: SlotMenuPoint): void {
    trigger.dispatchEvent(
      new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: at.x, clientY: at.y })
    );
  }

  protected choose(slot: SlotContext, plugin: PluginInfo): void {
    const trackId = this.trackId();
    if (slot.insert) {
      if (slot.insert.pluginId !== plugin.id) {
        void this.mixer.replaceInsert(trackId, slot.insert, plugin.id, slot.index);
      }
    } else {
      void this.mixer.addInsert(trackId, plugin.id, slot.index);
    }
  }

  protected toggleBypass(slot: SlotContext): void {
    if (slot.insert) {
      void this.mixer.toggleBypass(this.trackId(), slot.insert);
    }
  }

  protected moveBy(slot: SlotContext, offset: -1 | 1): void {
    const index = slot.index + offset;
    if (slot.insert && index >= 0 && index < this.strip().inserts.length) {
      void this.mixer.moveInsert(this.trackId(), slot.insert.id, index);
    }
  }

  protected dragStart(slot: SlotContext, event: DragEvent): void {
    if (!slot.insert) {
      event.preventDefault();
      return;
    }
    this.dragging.set(slot.insert.id);
    event.dataTransfer?.setData('application/x-anode-insert', slot.insert.id);
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
    }
  }

  protected dragEnd(): void {
    this.dragging.set(null);
    this.dropAt.set(null);
  }

  protected dragOver(index: number, event: DragEvent): void {
    if (this.dragging() !== null) {
      event.preventDefault();
      this.dropAt.set(index);
    }
  }

  /** A slot dropped on another of the same strip moves there (Logic's drag in the Audio FX list). */
  protected drop(index: number, event: DragEvent): void {
    const id = this.dragging();
    this.dragEnd();
    if (!id) {
      return;
    }
    event.preventDefault();
    const from = this.strip().inserts.findIndex(i => i.id === id);
    const to = Math.min(this.strip().inserts.length - 1, from < index ? index - 1 : index);
    if (from >= 0 && to !== from) {
      void this.mixer.moveInsert(this.trackId(), id, Math.max(0, to));
    }
  }

  // ---- sends

  protected sendLevel(send: Send): number {
    const trackId = this.trackId();
    return trackId ? this.mixer.sendLevel(trackId, send) : send.levelDb;
  }

  protected setSendLevel(send: Send, levelDb: number): void {
    const trackId = this.trackId();
    if (trackId) {
      void this.mixer.setSendLevel(trackId, send.id, Math.round(levelDb * 10) / 10);
    }
  }

  protected releaseSend(send: Send): void {
    const trackId = this.trackId();
    if (trackId) {
      this.mixer.releaseSend(trackId, send.id);
    }
  }

  protected togglePre(send: Send): void {
    const trackId = this.trackId();
    if (trackId) {
      void this.mixer.togglePreFader(trackId, send);
    }
  }

  protected removeSend(send: Send): void {
    const trackId = this.trackId();
    if (trackId) {
      void this.mixer.removeSend(trackId, send);
    }
  }

  /** A new aux on the lowest free bus, this strip's send (a new one, or the one the menu is for) going to it. */
  protected sendToNewAux(send: Send | null): void {
    const trackId = this.trackId();
    const bus = this.freeBus();
    if (!trackId || bus === 0) {
      return;
    }
    void (send
      ? this.project.execute(
          [
            { type: 'addAux', bus },
            { type: 'setSend', trackId, sendId: send.id, bus }
          ],
          'Send to New Aux'
        )
      : this.mixer.addSendToNewAux(trackId, bus));
  }

  // ---- routing, groups, VCA, automation

  protected setOutput(output: string): void {
    const trackId = this.trackId();
    if (trackId && output !== this.strip().output) {
      void this.mixer.setOutput(trackId, output);
    }
  }

  protected setGroup(number: number | null): void {
    const trackId = this.trackId();
    if (trackId) {
      void this.mixer.setTrackGroups(trackId, number === null ? [] : [number]);
    }
  }

  protected async newGroup(): Promise<void> {
    const number = await this.mixer.newGroup(this.trackId());
    if (number > 0) {
      this.ui.groupSettings.set(number);
    }
  }

  protected assignVca(vcaId: string | null): void {
    const trackId = this.trackId();
    if (trackId) {
      void this.mixer.assignVca(trackId, vcaId);
    }
  }

  /** A new VCA, with this track assigned to it. */
  protected async newVca(): Promise<void> {
    const trackId = this.trackId();
    const before = new Set(this.project.trackIds());
    const result = await this.mixer.addVca();
    const op = result?.delta.ops.find(o => o.op === 'trackUpserted' && !before.has(o.track.id));
    if (trackId && op?.op === 'trackUpserted') {
      await this.mixer.assignVca(trackId, op.track.id);
    }
  }

  protected setMode(mode: AutomationMode): void {
    const trackId = this.trackId();
    if (trackId) {
      void this.automation.setMode(trackId, mode);
    }
  }
}

function busLabelOf(bus: number, labels: Parameters<typeof busName>[1]): string {
  return bus > 0 ? busName(bus, labels) : 'No Input';
}
