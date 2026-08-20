import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import type { ComponentFixture } from '@angular/core/testing';

import {
  ABSENT_VALUE_DESCRIPTION,
  ABSENT_VALUE_MARK,
  AbsentValueComponent,
} from './absent-value.component';

/** A host, so the component is exercised through its selector exactly as a listing uses it. */
@Component({
  standalone: true,
  imports: [AbsentValueComponent],
  template: `<td><app-absent-value /></td>`,
})
class AbsentValueHostComponent {}

describe('AbsentValueComponent', () => {
  let fixture: ComponentFixture<AbsentValueHostComponent>;

  /** @returns The rendered host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AbsentValueHostComponent] }).compileComponents();

    fixture = TestBed.createComponent(AbsentValueHostComponent);
    fixture.detectChanges();
  });

  it('paints a mark and hides it from assistive technology', () => {
    const mark: HTMLElement | null = host().querySelector('.absent-value__mark');

    expect(mark).not.toBeNull();
    expect(mark?.textContent?.trim()).toBe(ABSENT_VALUE_MARK);
    expect(mark?.getAttribute('aria-hidden'))
      .withContext('a screen reader must not read a punctuation character on every row')
      .toBe('true');
  });

  it('exposes words to assistive technology and hides them from the page', () => {
    const description: HTMLElement | null = host().querySelector('.absent-value__description');

    expect(description).not.toBeNull();
    expect(description?.textContent?.trim()).toBe(ABSENT_VALUE_DESCRIPTION);
    expect(description?.hasAttribute('data-visually-hidden'))
      .withContext('clipped rather than removed, so it stays in the accessibility tree')
      .toBeTrue();
    expect(description?.getAttribute('aria-hidden')).toBeNull();
  });

  it('renders SOMETHING, which is the defect it exists to end', () => {
    // Six of seven portal expiry cells previously rendered empty text, whitespace-only content and no children
    // at all, so nothing was communicated to anybody.
    const rendered: HTMLElement | null = host().querySelector('app-absent-value');

    expect(rendered?.textContent?.trim().length ?? 0).toBeGreaterThan(0);
    expect(rendered?.children.length).toBe(2);
  });

  it('offers no input, so no caller can reintroduce a second convention', () => {
    // The four conventions this replaces included two different screen-reader sentences for the same state.
    // Asserted on the class rather than the DOM, because it is the API that must stay closed.
    expect(Object.keys(new AbsentValueComponent() as unknown as Record<string, unknown>).sort()).toEqual([
      'description',
      'mark',
    ]);
  });
});
