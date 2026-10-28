import { computed, DestroyRef, effect, inject, Injectable, signal, untracked } from '@angular/core';
import type { LiveMidiEvent, MidiActivity } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { isNoteOff, isNoteOn, MusicalTypingKeys, noteOff, noteOn } from '../midi/midi-math';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';

/** How long a track's activity dot stays lit after its last MIDI message. */
export const ActivityHoldMs = 180;
/** The lowest and highest octave musical typing reaches (its C: C-2 … C7, as Logic numbers them). */
const MinOctave = 0;
const MaxOctave = 9;

/**
 * Live MIDI from the editor and what the engine heard (docs/03-phase-2.md, "Live input"). The on-screen keyboard and
 * musical typing play notes on the selected instrument or external MIDI track through `/midi/live`; the selected
 * track is also the MIDI-thru track (`/midi/thru`), so a device plays it when nothing is record-armed. `midiActivity`
 * from the Server lights the keyboard (the notes sounding on the selected track) and each track's activity dot.
 */
@Injectable({ providedIn: 'root' })
export class LiveMidiStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly notify = inject(Notify);

  /** The track live MIDI plays: the selected track when it takes MIDI. */
  readonly target = computed(() => {
    const id = this.selection.track();
    const track = id ? this.project.tracks().get(id) : undefined;
    return track && track.kind !== 'audio' ? track : null;
  });
  /** Musical typing's octave: the A key plays this octave's C (5 is C3, middle C). */
  readonly octave = signal(5);
  /** The velocity musical typing and the keyboard play with. */
  readonly velocity = signal(100);
  /**
   * Musical typing: off until turned on (⌘K, or the Keyboard pane's button), as in Logic, so its letter keys do not take
   * the key commands (A, E, G, S, T…) whenever an instrument track is selected.
   */
  readonly typing = signal(false);
  /** Notes held here (keyboard, typing), by pitch, and the track they play on. */
  private readonly held = signal<ReadonlySet<number>>(new Set());
  private heldOn: string | null = null;
  /** Notes musical typing holds, by key code. */
  private readonly typed = new Map<string, number>();
  /** Notes the engine reported sounding on the target track, by pitch. */
  private readonly heard = signal<ReadonlySet<number>>(new Set());
  /** What the keyboard lights: what is held here or heard on the target track. */
  readonly sounding = computed<ReadonlySet<number>>(() => new Set([...this.held(), ...this.heard()]));
  /** Tracks with MIDI activity in the last {@link ActivityHoldMs}. */
  readonly active = signal<ReadonlySet<string>>(new Set());
  private readonly timers = new Map<string, ReturnType<typeof setTimeout>>();
  /** The thru track the Server was last told about (undefined: not yet told). */
  private thru: string | null | undefined = undefined;

  constructor() {
    const events = inject(EventStream);
    events.events$.subscribe(message => {
      if (message.type === 'midiActivity') {
        this.activity(message.events);
      } else if (message.type === 'projectOpened' || message.type === 'projectClosed') {
        this.thru = undefined;
      }
    });
    events.connected$.subscribe(() => {
      this.thru = undefined;
      this.syncThru(this.target()?.id ?? null);
    });
    effect(() => {
      const target = this.target()?.id ?? null;
      const open = this.project.isOpen();
      untracked(() => {
        this.typed.clear();
        this.releaseAll();
        this.heard.set(new Set());
        if (open) {
          this.syncThru(target);
        }
      });
    });
    inject(DestroyRef).onDestroy(() => this.timers.forEach(timer => clearTimeout(timer)));
  }

  /** Starts a note on the target track. */
  noteOn(pitch: number, velocity = this.velocity()): void {
    const track = this.target();
    if (!track || pitch < 0 || pitch > 127 || this.held().has(pitch)) {
      return;
    }

    if (this.heldOn !== track.id) {
      this.releaseAll();
      this.heldOn = track.id;
    }
    this.held.update(held => new Set(held).add(pitch));
    this.send(track.id, [{ status: noteOn(), data1: pitch, data2: Math.min(127, Math.max(1, velocity)) }]);
  }

  /** Ends a note started with {@link noteOn}. */
  noteOff(pitch: number): void {
    const trackId = this.heldOn;
    if (!this.held().has(pitch)) {
      return;
    }

    this.held.update(held => {
      const next = new Set(held);
      next.delete(pitch);
      return next;
    });
    if (trackId) {
      this.send(trackId, [{ status: noteOff(), data1: pitch, data2: 0 }]);
    }
  }

  /** Ends every note held here (the target changed, the window lost focus). */
  releaseAll(): void {
    const held = [...this.held()];
    const trackId = this.heldOn;
    this.held.set(new Set());
    if (trackId && held.length > 0) {
      this.send(
        trackId,
        held.map(pitch => ({ status: noteOff(), data1: pitch, data2: 0 }))
      );
    }
  }

  /**
   * Musical typing: a key down plays its note (A W S E D F T G Y H U J K from the octave's C), Z and X move the
   * octave. True when the key was musical typing's (the caller then keeps it from other shortcuts).
   */
  typeDown(code: string, repeat = false): boolean {
    if (!this.typing() || !this.target()) {
      return false;
    }
    if (code === 'KeyZ' || code === 'KeyX') {
      if (!repeat) {
        this.releaseTyped();
        this.shiftOctave(code === 'KeyZ' ? -1 : 1);
      }
      return true;
    }
    const semitone = MusicalTypingKeys[code];
    if (semitone === undefined) {
      return false;
    }
    if (!repeat && !this.typed.has(code)) {
      const pitch = this.octave() * 12 + semitone;
      if (pitch <= 127) {
        this.typed.set(code, pitch);
        this.noteOn(pitch);
      }
    }
    return true;
  }

  /** Musical typing: a key up ends the note its key down started. */
  typeUp(code: string): boolean {
    const pitch = this.typed.get(code);
    if (pitch === undefined) {
      return false;
    }
    this.typed.delete(code);
    this.noteOff(pitch);
    return true;
  }

  /** Ends every note musical typing holds (the octave moved, the window lost focus). */
  releaseTyped(): void {
    for (const [code, pitch] of this.typed) {
      this.typed.delete(code);
      this.noteOff(pitch);
    }
  }

  shiftOctave(by: number): void {
    this.octave.update(octave => Math.min(MaxOctave, Math.max(MinOctave, octave + by)));
  }

  isActive(trackId: string): boolean {
    return this.active().has(trackId);
  }

  private send(trackId: string, events: LiveMidiEvent[]): void {
    this.api.sendLiveMidi(trackId, events).catch((error: unknown) => this.notify.failure(error, 'Could not play'));
  }

  private syncThru(trackId: string | null): void {
    if (this.thru === trackId) {
      return;
    }

    this.thru = trackId;
    this.api.setMidiThru(trackId).catch((error: unknown) => {
      this.thru = undefined;
      this.notify.failure(error, 'Could not route MIDI to the selected track');
    });
  }

  private activity(events: readonly MidiActivity[]): void {
    const target = this.target()?.id ?? null;
    let heard: Set<number> | null = null;
    const tracks = new Set<string>();
    for (const event of events) {
      if (!event.trackId) {
        continue;
      }

      tracks.add(event.trackId);
      if (event.trackId === target) {
        heard ??= new Set(this.heard());
        if (isNoteOn(event.status, event.data2)) {
          heard.add(event.data1);
        } else if (isNoteOff(event.status, event.data2)) {
          heard.delete(event.data1);
        } else if ((event.status & 0xf0) === 0xb0 && (event.data1 === 120 || event.data1 === 123)) {
          heard.clear(); // all sound off, all notes off
        }
      }
    }

    if (heard) {
      this.heard.set(heard);
    }
    if (tracks.size > 0) {
      this.active.update(active => new Set([...active, ...tracks]));
      for (const id of tracks) {
        clearTimeout(this.timers.get(id));
        this.timers.set(
          id,
          setTimeout(() => {
            this.timers.delete(id);
            this.active.update(active => {
              const next = new Set(active);
              next.delete(id);
              return next;
            });
          }, ActivityHoldMs)
        );
      }
    }
  }
}
