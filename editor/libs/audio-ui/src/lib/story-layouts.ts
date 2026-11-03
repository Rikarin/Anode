import type { PannerSpeaker } from './panner-math';

/** Speaker layouts for the panner and meter stories (`SpeakerLayouts` in `@anode/api-client`; not in the barrel). */
function speaker(label: string, azimuth: number, elevation = 0, lfe = false): PannerSpeaker {
  return { label, azimuth, elevation, lfe };
}

export const Layout51: readonly PannerSpeaker[] = [
  speaker('M+030', 30),
  speaker('M-030', -30),
  speaker('M+000', 0),
  speaker('LFE1', 45, -30, true),
  speaker('M+110', 110),
  speaker('M-110', -110)
];
export const Layout714: readonly PannerSpeaker[] = [
  speaker('M+030', 30),
  speaker('M-030', -30),
  speaker('M+000', 0),
  speaker('LFE1', 45, -30, true),
  speaker('M+090', 90),
  speaker('M-090', -90),
  speaker('M+135', 135),
  speaker('M-135', -135),
  speaker('U+045', 45, 30),
  speaker('U-045', -45, 30),
  speaker('U+135', 135, 30),
  speaker('U-135', -135, 30)
];
