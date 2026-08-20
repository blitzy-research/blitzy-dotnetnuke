/**
 * Specifications for {@link RovingFocusDirective}.
 *
 * \u26a0 WHAT THESE SPECIFICATIONS HAVE TO DISCRIMINATE. The defect being closed is not "the strip is
 * unreachable" - it was reachable, twenty-seven times over. It is that a run of sibling filter entries each
 * held its own place in the sequential tab order and no arrow key moved between them: a real ArrowRight press
 * with the third letter focused left `document.activeElement` on that same third letter, on both strips. So a
 * specification that only asserted "some item is focusable" would pass under the defect as well as under the
 * fix and would not be a test. Every assertion below therefore names a COUNT or a DESTINATION, which is what
 * the two implementations actually disagree about.
 */

import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RovingFocusDirective } from './roving-focus.directive';

/** A strip shaped like the two alphabet filters: a toolbar of native buttons, one of them pressed. */
@Component({
  standalone: true,
  imports: [RovingFocusDirective],
  template: `
    <div role="toolbar" appRovingFocus data-role="strip">
      @for (entry of entries; track entry) {
        <button type="button" [attr.aria-pressed]="entry === pressed" [attr.data-entry]="entry">
          {{ entry }}
        </button>
      }
    </div>
  `,
})
class StripHostComponent {
  public entries: readonly string[] = ['A', 'B', 'C', 'D'];

  public pressed: string | null = null;
}

/** A strip sharing its container with a text box, to prove the arrow keys are not taken from the caret. */
@Component({
  standalone: true,
  imports: [RovingFocusDirective],
  template: `
    <div role="toolbar" appRovingFocus="button.entry" data-role="strip">
      <input type="text" data-role="query" />
      <button type="button" class="entry" data-entry="A">A</button>
      <button type="button" class="entry" data-entry="B">B</button>
    </div>
  `,
})
class MixedHostComponent {}

describe('RovingFocusDirective', () => {
  let fixture: ComponentFixture<StripHostComponent>;

  async function create(entries?: readonly string[], pressed?: string | null): Promise<void> {
    await TestBed.configureTestingModule({ imports: [StripHostComponent] }).compileComponents();
    fixture = TestBed.createComponent(StripHostComponent);

    if (entries !== undefined) {
      fixture.componentInstance.entries = entries;
    }

    if (pressed !== undefined) {
      fixture.componentInstance.pressed = pressed;
    }

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function strip(): HTMLElement {
    const found = (fixture.nativeElement as HTMLElement).querySelector('[data-role="strip"]');

    if (!(found instanceof HTMLElement)) {
      throw new Error('the strip is missing from the rendered host');
    }

    return found;
  }

  function entries(): readonly HTMLButtonElement[] {
    return Array.from(strip().querySelectorAll('button'));
  }

  function entry(name: string): HTMLButtonElement {
    const found = entries().find((candidate) => candidate.dataset['entry'] === name);

    if (found === undefined) {
      throw new Error(`no strip entry named ${name}`);
    }

    return found;
  }

  /** The tabindex of every entry, in document order - the measurement the two implementations disagree on. */
  function tabIndexes(): readonly number[] {
    return entries().map((candidate) => candidate.tabIndex);
  }

  /** How many entries Tab would land on. Twenty-seven under the defect; one under the fix. */
  function tabStops(): number {
    return tabIndexes().filter((value) => value >= 0).length;
  }

  /**
   * Presses a key on the entry currently holding focus.
   *
   * \u26a0 A SYNTHETIC KEY PRESS NEVER PERFORMS A DEFAULT ACTION, so nothing here can prove the page stops
   * scrolling. What it CAN prove is whether the directive claimed the press, which is what `defaultPrevented`
   * on the returned event reports, and that is asserted explicitly rather than assumed from a focus move.
   */
  function press(
    on: HTMLElement,
    key: string,
    modifiers: Partial<Record<'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey', boolean>> = {},
  ): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...modifiers });

    on.dispatchEvent(event);
    fixture.detectChanges();

    return event;
  }

  describe('the single tab stop', () => {
    it('leaves exactly one entry in the tab order and passes over the rest', async () => {
      await create();

      // The discriminating measurement: four entries, ONE stop. Without the directive every one of them is a
      // stop, which is the twenty-seven-press traversal that was measured on both live strips.
      expect(entries().length).withContext('four entries were rendered').toBe(4);
      expect(tabStops()).toBe(1);
      expect(tabIndexes()).toEqual([0, -1, -1, -1]);
    });

    it('puts the tab stop on the applied entry rather than the first one', async () => {
      // \u26a0 THIS IS THE INPUT THAT DISCRIMINATES A PREFERENCE FROM A HARDCODED FIRST ITEM. An
      // implementation that always published items[0] passes the specification above and fails this one.
      await create(['A', 'B', 'C', 'D'], 'C');

      expect(entry('C').tabIndex).toBe(0);
      expect(tabIndexes()).toEqual([-1, -1, 0, -1]);
    });

    it('falls back to the first entry when none is applied', async () => {
      await create(['A', 'B', 'C', 'D'], null);

      expect(entry('A').tabIndex).toBe(0);
    });

    it('never leaves the strip with no reachable entry at all', async () => {
      // The failure this guards is strictly worse than the defect being fixed: publishing minus one everywhere
      // would take the filter out of the keyboard completely. The applied entry is removed from the DOM and a
      // republish is then provoked, so the fallback rather than the preference decides the outcome.
      await create(['A', 'B', 'C', 'D'], 'C');
      entry('C').remove();

      entry('B').dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
      fixture.detectChanges();

      expect(tabStops()).withContext('one entry is still reachable').toBe(1);
    });
  });

  describe('arrow key movement', () => {
    it('moves forward and carries the tab stop with it', async () => {
      await create();
      const first = entry('A');

      first.focus();
      const event = press(first, 'ArrowRight');

      expect(document.activeElement).toBe(entry('B'));
      expect(tabIndexes()).withContext('the stop moved rather than multiplied').toEqual([-1, 0, -1, -1]);
      expect(event.defaultPrevented).withContext('the press was claimed').toBeTrue();
    });

    it('moves backward', async () => {
      await create();
      const third = entry('C');

      third.focus();
      press(third, 'ArrowLeft');

      expect(document.activeElement).toBe(entry('B'));
    });

    it('treats the vertical arrows as the horizontal ones, because the strip wraps onto several lines', async () => {
      await create();

      entry('B').focus();
      press(entry('B'), 'ArrowDown');
      expect(document.activeElement).toBe(entry('C'));

      press(entry('C'), 'ArrowUp');
      expect(document.activeElement).toBe(entry('B'));
    });

    it('wraps at both ends', async () => {
      await create();

      entry('D').focus();
      press(entry('D'), 'ArrowRight');
      expect(document.activeElement).withContext('past the last entry').toBe(entry('A'));

      press(entry('A'), 'ArrowLeft');
      expect(document.activeElement).withContext('before the first entry').toBe(entry('D'));
    });

    it('jumps to the ends for Home and End', async () => {
      await create();

      entry('B').focus();
      press(entry('B'), 'End');
      expect(document.activeElement).toBe(entry('D'));

      press(entry('D'), 'Home');
      expect(document.activeElement).toBe(entry('A'));
    });
  });

  describe('presses it deliberately does not claim', () => {
    it('leaves a modified arrow to the browser', async () => {
      await create();
      const first = entry('A');

      first.focus();
      const event = press(first, 'ArrowRight', { ctrlKey: true });

      expect(document.activeElement).withContext('focus stayed put').toBe(first);
      expect(event.defaultPrevented).withContext('the browser keeps the press').toBeFalse();
    });

    it('leaves a key it does not handle alone, so the page stays scrollable from inside the strip', async () => {
      await create();
      const first = entry('A');

      first.focus();
      const event = press(first, 'PageDown');

      expect(document.activeElement).toBe(first);
      expect(event.defaultPrevented).toBeFalse();
    });

    it('does not take the arrow keys from a text box sharing the container', async () => {
      await TestBed.configureTestingModule({ imports: [MixedHostComponent] }).compileComponents();
      const mixed = TestBed.createComponent(MixedHostComponent);
      mixed.detectChanges();
      await mixed.whenStable();
      mixed.detectChanges();

      const host = mixed.nativeElement as HTMLElement;
      const query = host.querySelector('[data-role="query"]');
      const firstEntry = host.querySelector('button.entry');

      if (!(query instanceof HTMLInputElement) || !(firstEntry instanceof HTMLButtonElement)) {
        throw new Error('the mixed strip did not render');
      }

      query.focus();
      const event = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
      query.dispatchEvent(event);
      mixed.detectChanges();

      expect(document.activeElement).withContext('the caret keeps the press').toBe(query);
      expect(event.defaultPrevented).toBeFalse();
      expect(firstEntry.tabIndex).withContext('the strip is still entered in one press').toBe(0);
    });
  });

  describe('agreement between a pointer press and the keyboard', () => {
    it('adopts whichever entry was focused, so leaving and returning comes back to it', async () => {
      await create();

      // Clicking an entry focuses it; the strip must then be re-entered AT that entry rather than back at the
      // start. Without adoption the tab stop stays on A and this expectation reads zero.
      entry('C').dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
      fixture.detectChanges();

      expect(entry('C').tabIndex).toBe(0);
      expect(tabStops()).toBe(1);
    });

    it('ignores focus arriving from outside the item set', async () => {
      await create();
      const before = tabIndexes();

      strip().dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
      fixture.detectChanges();

      expect(tabIndexes()).toEqual(before);
    });
  });

  describe('a strip with nothing in it', () => {
    it('publishes nothing and swallows a key press without throwing', async () => {
      await create([]);

      expect(entries().length).toBe(0);
      expect(() => press(strip(), 'ArrowRight')).not.toThrow();
    });
  });

  it('is applied to a host that declares the toolbar role, which is what tells a reader arrows apply', async () => {
    // The role is deliberately left to the template rather than forced from the directive, so this asserts the
    // pairing the directive documents rather than a behaviour it implements.
    await create();

    expect(strip().getAttribute('role')).toBe('toolbar');
  });

  describe('a directive instance whose host has gone away', () => {
    it('stops writing tab indexes once destroyed', async () => {
      await create();
      const captured = entries();

      fixture.destroy();
      captured.forEach((candidate) => {
        candidate.tabIndex = 5;
      });

      captured[0]?.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));

      expect(captured.map((candidate) => candidate.tabIndex))
        .withContext('nothing was rewritten after teardown')
        .toEqual([5, 5, 5, 5]);
    });
  });
});
