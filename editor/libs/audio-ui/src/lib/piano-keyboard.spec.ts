import { TestBed } from '@angular/core/testing';
import { AnPianoKeyboard } from './piano-keyboard';

function pointer(element: Element, type: string, pointerId: number): void {
  element.dispatchEvent(new PointerEvent(type, { pointerId, button: 0, buttons: 1, bubbles: true, cancelable: true }));
}

describe('AnPianoKeyboard', () => {
  it('holds a chord with two pointers, each key ending when its own pointer lifts', async () => {
    const fixture = TestBed.createComponent(AnPianoKeyboard);
    const played: string[] = [];
    fixture.componentInstance.noteOn.subscribe(({ pitch }) => played.push(`on ${pitch}`));
    fixture.componentInstance.noteOff.subscribe(pitch => played.push(`off ${pitch}`));
    await fixture.whenStable();
    const key = (name: string) =>
      (fixture.nativeElement as HTMLElement).querySelector(`[aria-label="${name}"]`) as HTMLElement;

    pointer(key('F3'), 'pointerdown', 1);
    pointer(key('A3'), 'pointerdown', 2);
    await fixture.whenStable();
    expect(key('F3').getAttribute('aria-pressed')).toBe('true');
    expect(key('A3').getAttribute('aria-pressed')).toBe('true');
    pointer(key('F3'), 'pointerup', 1);
    pointer(key('A3'), 'pointerup', 2);
    expect(played).toEqual(['on 65', 'on 69', 'off 65', 'off 69']);
  });
});
