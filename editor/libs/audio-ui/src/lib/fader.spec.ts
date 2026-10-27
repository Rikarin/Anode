import { TestBed } from '@angular/core/testing';
import { AnChannelStrip } from './channel-strip';
import { AnFader } from './fader';
import { AnPanKnob } from './pan-knob';
import { AnPeakMeter, MeterBallistics, MeterFallDbPerSecond, PeakHoldMs } from './peak-meter';

function key(element: Element, key: string, shiftKey = false): void {
  element.dispatchEvent(new KeyboardEvent('keydown', { key, shiftKey, bubbles: true }));
}

describe('AnFader', () => {
  it('is a slider in dB that steps with the keys and commits each step', async () => {
    const fixture = TestBed.createComponent(AnFader);
    fixture.componentRef.setInput('label', 'Bass volume');
    const committed: number[] = [];
    fixture.componentInstance.committed.subscribe(value => committed.push(value));
    await fixture.whenStable();
    const slider = (fixture.nativeElement as HTMLElement).querySelector('[role="slider"]') as HTMLElement;
    expect(slider.getAttribute('aria-label')).toBe('Bass volume');
    expect(slider.getAttribute('aria-valuetext')).toBe('0.0 dB');

    key(slider, 'ArrowUp');
    key(slider, 'ArrowUp', true);
    await fixture.whenStable();
    expect(fixture.componentInstance.value()).toBeCloseTo(1.1);
    expect(slider.getAttribute('aria-valuenow')).toBe('1.1');

    key(slider, 'Home');
    await fixture.whenStable();
    expect(slider.getAttribute('aria-valuetext')).toBe('−∞ dB');
    expect(committed).toHaveLength(3);
  });

  it('resets to 0 dB on double-click', async () => {
    const fixture = TestBed.createComponent(AnFader);
    fixture.componentRef.setInput('value', -20);
    await fixture.whenStable();
    const slider = (fixture.nativeElement as HTMLElement).querySelector('[role="slider"]') as HTMLElement;
    slider.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(fixture.componentInstance.value()).toBe(0);
  });
});

describe('AnPanKnob', () => {
  it('shows the pan as Logic does and centres on double-click', async () => {
    const fixture = TestBed.createComponent(AnPanKnob);
    fixture.componentRef.setInput('value', -0.5);
    await fixture.whenStable();
    const slider = (fixture.nativeElement as HTMLElement).querySelector('[role="slider"]') as HTMLElement;
    expect(slider.getAttribute('aria-valuetext')).toBe('L32');
    key(slider, 'End');
    await fixture.whenStable();
    expect(slider.getAttribute('aria-valuetext')).toBe('R63');
    slider.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(fixture.componentInstance.value()).toBe(0);
  });
});

describe('MeterBallistics', () => {
  it('rises at once, falls steadily and holds the peak', () => {
    const meter = new MeterBallistics();
    meter.step(-6, 1000);
    expect(meter.display).toBe(-6);
    expect(meter.peak).toBe(-6);

    meter.step(-60, 1500); // half a second later the bar has fallen 13 dB, the peak holds
    expect(meter.display).toBeCloseTo(-6 - MeterFallDbPerSecond / 2);
    expect(meter.peak).toBe(-6);

    meter.step(-60, 1000 + PeakHoldMs + 100);
    expect(meter.peak).toBeLessThan(-6);
    expect(meter.clipped).toBe(false);
    meter.step(1, 4000);
    expect(meter.clipped).toBe(true);
  });
});

describe('AnPeakMeter', () => {
  it('moves its bars from its source without change detection and announces the level', async () => {
    const fixture = TestBed.createComponent(AnPeakMeter);
    fixture.componentRef.setInput('label', 'Bass level');
    fixture.componentRef.setInput('source', () => ({ left: 1, right: 0.5 }));
    await fixture.whenStable();
    fixture.componentInstance.draw(1000);
    const host = fixture.nativeElement as HTMLElement;
    const meter = host.querySelector('[role="meter"]') as HTMLElement;
    const covers = host.querySelectorAll<HTMLElement>('.origin-top');
    expect(covers[0]?.style.transform).toBe('scaleY(0.0000)'); // 0 dBFS: full
    expect(covers[1]?.style.transform).toBe('scaleY(0.1003)'); // −6 dB of a 60 dB scale
    expect(meter.getAttribute('aria-label')).toBe('Bass level');
    expect(meter.getAttribute('aria-valuenow')).toBe('0');
  });
});

describe('AnChannelStrip', () => {
  it('reports its controls and names them after the channel', async () => {
    const fixture = TestBed.createComponent(AnChannelStrip);
    fixture.componentRef.setInput('name', 'Vocals');
    fixture.componentRef.setInput('solo', true);
    const toggled: string[] = [];
    fixture.componentInstance.muteToggle.subscribe(() => toggled.push('mute'));
    fixture.componentInstance.soloToggle.subscribe(() => toggled.push('solo'));
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const mute = host.querySelector('[aria-label="Mute Vocals"]') as HTMLButtonElement;
    const solo = host.querySelector('[aria-label="Solo Vocals"]') as HTMLButtonElement;
    expect(solo.getAttribute('aria-pressed')).toBe('true');
    mute.click();
    solo.click();
    expect(toggled).toEqual(['mute', 'solo']);
    expect(host.querySelector('[aria-label="Vocals volume"]')).not.toBeNull();
  });

  it('has no solo on the master', async () => {
    const fixture = TestBed.createComponent(AnChannelStrip);
    fixture.componentRef.setInput('name', 'Stereo Out');
    fixture.componentRef.setInput('master', true);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('[aria-label="Solo Stereo Out"]')).toBeNull();
  });
});
