import type { EditCommand, ProjectTemplate } from '@anode/api-client';
import { ApiError } from '@anode/api-client';

/** A factory template of the fake, with the commands that make it, as the Server's `FactoryTemplates`. */
interface FakeFactoryTemplate {
  template: ProjectTemplate;
  commands: EditCommand[];
}

const reverb = { type: 'addAux', bus: 1, name: 'Reverb' } as EditCommand;

function factory(
  id: string,
  name: string,
  category: string,
  description: string,
  commands: EditCommand[]
): FakeFactoryTemplate {
  return {
    template: { id, name, category, description, factory: true, sampleRate: null, hasAudio: false },
    commands
  };
}

function audio(name: string, stereo = false): EditCommand {
  return { type: 'addTrack', name, stereo };
}

function instrument(name: string): EditCommand {
  return { type: 'addTrack', name, stereo: false, kind: 'instrument' };
}

export const FactoryTemplates: readonly FakeFactoryTemplate[] = [
  factory('factory-empty', 'Empty Project', 'General', 'No tracks.', []),
  factory('factory-songwriter', 'Songwriter', 'Songwriting', 'Vocal, guitar, keys and bass, sharing a reverb.', [
    reverb,
    audio('Vocal'),
    audio('Guitar', true),
    instrument('Keys'),
    instrument('Bass')
  ]),
  factory('factory-band', 'Band Recording', 'Recording', 'Drums, bass, guitars and vocals.', [
    reverb,
    audio('Kick'),
    audio('Snare'),
    audio('Overheads', true),
    audio('Bass'),
    audio('Lead Vocal')
  ]),
  factory('factory-electronic', 'Electronic Production', 'Production', 'Instrument tracks on a reverb.', [
    reverb,
    instrument('Drums'),
    instrument('Bass'),
    instrument('Lead'),
    instrument('Pad')
  ]),
  factory('factory-voiceover', 'Voiceover', 'Recording', 'A voice track, and room tone.', [
    audio('Voice'),
    audio('Room Tone', true)
  ])
];

/** A template's name as the Server checks it. */
export function checkedTemplateName(name: string): string {
  const trimmed = name.trim();
  if (trimmed.length === 0 || trimmed.length > 128) {
    throw new ApiError(422, 'A template name has 1…128 characters.');
  }

  return trimmed;
}
