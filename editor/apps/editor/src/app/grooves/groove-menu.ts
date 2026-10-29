import type { EditCommand, GrooveTemplate, MidiRegion } from '@anode/api-client';

/**
 * A choice of a quantise menu: a grid value (`ticks`, 0: Off) or one of the project's groove templates (`grooveId`),
 * listed below the grid values as in Logic (docs/08-phase-7.md, "Groove templates").
 */
export interface QuantizeChoice {
  ticks: number;
  label: string;
  grooveId?: string;
}

/** The grid values, then the project's grooves by name. */
export function quantizeChoices(
  grids: readonly { ticks: number; label: string }[],
  grooves: readonly Pick<GrooveTemplate, 'id' | 'name'>[]
): QuantizeChoice[] {
  return [...grids.map(g => ({ ticks: g.ticks, label: g.label })), ...grooveChoices(grooves)];
}

/** The project's grooves as quantise choices ("Groove: Funk"). */
export function grooveChoices(grooves: readonly Pick<GrooveTemplate, 'id' | 'name'>[]): QuantizeChoice[] {
  return grooves.map(g => ({ ticks: 0, label: `Groove: ${g.name}`, grooveId: g.id }));
}

/**
 * The region inspector's quantise changed: a groove sets the region's `grooveId`; a grid value sets its grid and, when
 * it played a groove, returns it to the grid (`grooveId` ''). Null when nothing changes.
 */
export function regionQuantizeCommand(
  region: Pick<MidiRegion, 'id' | 'playback'>,
  choice: QuantizeChoice
): EditCommand | null {
  const groove = region.playback.grooveId ?? null;
  if (choice.grooveId) {
    return choice.grooveId === groove
      ? null
      : { type: 'setMidiRegion', regionId: region.id, grooveId: choice.grooveId };
  }
  if (!groove && choice.ticks === region.playback.quantizeTicks) {
    return null;
  }
  return {
    type: 'setMidiRegion',
    regionId: region.id,
    quantizeTicks: choice.ticks,
    ...(groove ? { grooveId: '' } : {})
  };
}
