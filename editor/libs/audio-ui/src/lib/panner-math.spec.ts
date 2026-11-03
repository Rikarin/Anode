import { TestBed } from '@angular/core/testing';
import { AnMultichannelMeter } from './multichannel-meter';
import { AnObjectPanner } from './object-panner';
import {
  formatAngle,
  formatLevel,
  knobToLevel,
  levelToKnob,
  objectFromSidePoint,
  objectFromTopPoint,
  objectSidePoint,
  objectTopPoint,
  puckFromPoint,
  puckPoint,
  shortSpeakerLabel,
  speakerName,
  speakerPoint,
  spreadPucks,
  wrapAngle
} from './panner-math';
import { AnSurroundPanner, pointerToPanner, viewPercent } from './surround-panner';

describe('the Surround Panner puck', () => {
  it('puts front centre at the top of the rim, and the angle positive to the left', () => {
    expect(puckPoint({ angle: 0, diversity: 1 })).toEqual({ x: 0, y: 1 });
    expect(puckPoint({ angle: 90, diversity: 1 })).toEqual({ x: -1, y: 0 });
    expect(puckPoint({ angle: -90, diversity: 0.5 })).toEqual({ x: 0.5, y: 0 });
    expect(puckPoint({ angle: 180, diversity: 1 })).toEqual({ x: 0, y: -1 });
  });

  it('round-trips angle and diversity: front centre, Ls at 110°, rim and centre', () => {
    for (const position of [
      { angle: 0, diversity: 1 },
      { angle: 110, diversity: 1 },
      { angle: -110, diversity: 0.4 },
      { angle: 30, diversity: 0.75 },
      { angle: 180, diversity: 1 }
    ]) {
      expect(puckFromPoint(puckPoint(position))).toEqual(position);
    }
    // Ls (M+110) sits behind and to the left.
    const ls = puckPoint({ angle: 110, diversity: 1 });
    expect(ls.x).toBeLessThan(0);
    expect(ls.y).toBeLessThan(0);
  });

  it('clamps to the rim and keeps the angle at the centre', () => {
    expect(puckFromPoint({ x: 0, y: 3 })).toEqual({ angle: 0, diversity: 1 });
    expect(puckFromPoint({ x: 0, y: 0 }, 110)).toEqual({ angle: 110, diversity: 0 });
  });

  it('spreads a stereo input around the puck', () => {
    const [left, right] = spreadPucks({ angle: 0, diversity: 1 }, 60);
    expect(left).toEqual(puckPoint({ angle: 30, diversity: 1 }));
    expect(right).toEqual(puckPoint({ angle: -30, diversity: 1 }));
  });

  it('wraps angles into −180…180', () => {
    expect(wrapAngle(190)).toBe(-170);
    expect(wrapAngle(-180)).toBe(180);
    expect(wrapAngle(540)).toBe(180);
    expect(formatAngle(110)).toBe('110° L');
    expect(formatAngle(-30)).toBe('30° R');
    expect(formatAngle(0)).toBe('0°');
  });
});

describe('speakers', () => {
  it('draws the ear-level ring on the rim, the upper layer inside, the LFE apart', () => {
    expect(speakerPoint({ label: 'M+030', azimuth: 30, elevation: 0, lfe: false })).toEqual({ x: -0.5, y: 0.866025 });
    const upper = speakerPoint({ label: 'U-135', azimuth: -135, elevation: 30, lfe: false });
    expect(Math.hypot(upper?.x ?? 0, upper?.y ?? 0)).toBeCloseTo(0.62);
    expect(speakerPoint({ label: 'LFE1', azimuth: 45, elevation: -30, lfe: true })).toBeNull();
  });

  it('names speakers as mixers do', () => {
    expect(['M+030', 'M-030', 'M+000', 'LFE1', 'M+110', 'U+045', 'U-135'].map(shortSpeakerLabel)).toEqual([
      'L',
      'R',
      'C',
      'LFE',
      'Ls',
      'Ltf',
      'Rtr'
    ]);
    expect(speakerName('M+110')).toBe('Left Surround');
  });

  it('shows panner levels on a knob with −∞ at its floor', () => {
    expect(levelToKnob(-144)).toBe(-60);
    expect(knobToLevel(-60)).toBe(-144);
    expect(knobToLevel(-6)).toBe(-6);
    expect(formatLevel(-144)).toBe('−∞ dB');
    expect(formatLevel(-6)).toBe('−6.0 dB');
  });
});

describe('the 3D Object Panner views', () => {
  it('round-trips azimuth and distance in the top view, the elevation kept', () => {
    const position = { azimuth: 30, elevation: 0, distance: 0.8 };
    expect(objectFromTopPoint(objectTopPoint(position), position)).toEqual(position);
    const raised = { azimuth: -135, elevation: 45, distance: 1 };
    const top = objectTopPoint(raised);
    // Overhead objects come in towards the centre of the top view.
    expect(Math.hypot(top.x, top.y)).toBeCloseTo(Math.SQRT1_2);
    expect(objectFromTopPoint(top, raised)).toEqual(raised);
  });

  it('round-trips elevation and distance in the side view, the azimuth kept', () => {
    for (const position of [
      { azimuth: 110, elevation: 30, distance: 1 },
      { azimuth: 0, elevation: -45, distance: 0.5 },
      { azimuth: -30, elevation: 90, distance: 0.7 }
    ]) {
      expect(objectFromSidePoint(objectSidePoint(position), position)).toEqual(position);
    }
    expect(objectSidePoint({ azimuth: 0, elevation: 90, distance: 1 })).toEqual({ x: 0, y: 1 });
  });
});

describe('panner views', () => {
  it('maps pointer positions to panner units and back', () => {
    const box = { left: 100, top: 50, width: 248, height: 248 } as DOMRect;
    expect(pointerToPanner(box, 224, 174)).toEqual({ x: 0, y: 0 });
    const top = pointerToPanner(box, 224, 50 + 248 * (0.24 / 2.48));
    expect(top.y).toBeCloseTo(1);
    expect(viewPercent(1, 'y')).toBeCloseTo((0.24 / 2.48) * 100);
    expect(viewPercent(0, 'x')).toBe(50);
  });
});

describe('AnSurroundPanner', () => {
  it('turns the angle from the keyboard and emits only what changed', async () => {
    const fixture = TestBed.createComponent(AnSurroundPanner);
    const changes: unknown[] = [];
    fixture.componentInstance.changed.subscribe(c => changes.push(c));
    fixture.componentRef.setInput('label', 'Voice surround panner');
    await fixture.whenStable();
    const slider = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[role="slider"]');
    slider?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    slider?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    await fixture.whenStable();
    expect(changes).toEqual([{ angle: 5 }, { diversity: 0.95 }]);
    expect(slider?.getAttribute('aria-valuetext')).toBe('5° L, diversity 95 %');
  });
});

describe('AnObjectPanner', () => {
  it('moves the elevation in the side view', async () => {
    const fixture = TestBed.createComponent(AnObjectPanner);
    const changes: unknown[] = [];
    fixture.componentInstance.changed.subscribe(c => changes.push(c));
    await fixture.whenStable();
    const side = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[aria-roledescription="object panner side view"]'
    );
    side?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
    await fixture.whenStable();
    expect(changes).toEqual([{ elevation: 5 }]);
  });
});

describe('AnMultichannelMeter', () => {
  it('draws one bar per speaker from its source', async () => {
    const fixture = TestBed.createComponent(AnMultichannelMeter);
    fixture.componentRef.setInput('speakers', ['M+030', 'M-030', 'M+000', 'LFE1', 'M+110', 'M-110']);
    fixture.componentRef.setInput('source', () => [1, 0.5, 0, 0, 0.25, 0]);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect([...host.querySelectorAll('[data-channel]')].map(e => e.getAttribute('data-channel'))).toEqual([
      'L',
      'R',
      'C',
      'LFE',
      'Ls',
      'Rs'
    ]);
    fixture.componentInstance.draw(1000);
    expect(host.querySelector('[role="meter"]')?.getAttribute('aria-valuenow')).toBe('0');
  });
});
