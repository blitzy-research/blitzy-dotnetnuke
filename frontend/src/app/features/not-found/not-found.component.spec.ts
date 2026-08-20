import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { NotFoundComponent } from './not-found.component';

/** The heading and the recovery caption, RESTATED INDEPENDENTLY rather than read back off the component. */
const EXPECTED_TITLE = 'Not Found';

const EXPECTED_RECOVERY_LABEL = 'Return to Administration Home';

/** The wording the wildcard route supplies through `data.message`. */
const ROUTE_MESSAGE = 'No administration screen is available at this address.';

/** A different sentence, to prove the input is genuinely rendered rather than hardcoded. */
const OTHER_MESSAGE = 'This address resolves to no screen.';

describe('NotFoundComponent', () => {
  let fixture: ComponentFixture<NotFoundComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NotFoundComponent],
      // The template declares a `routerLink`, which needs the directive's providers to resolve. An empty
      // route table is enough: nothing here navigates, and the assertion below reads the rendered `href`
      // rather than following it.
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(NotFoundComponent);
    fixture.detectChanges();
  });

  function query<T extends HTMLElement>(selector: string): T | null {
    return fixture.nativeElement.querySelector(selector) as T | null;
  }

  function textOf(element: Element | null): string {
    return (element?.textContent ?? '').trim();
  }

  it('states a level-one heading, which is the defect that made this component necessary', () => {
    const heading = query<HTMLHeadingElement>('h1');

    expect(heading).not.toBeNull();
    expect(textOf(heading)).toBe(EXPECTED_TITLE);
  });

  it('opens the document outline at level one, with no heading above the h1', () => {
    const headings: readonly HTMLHeadingElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('h1,h2,h3,h4,h5,h6') as NodeListOf<HTMLHeadingElement>,
    );

    expect(headings.length).toBeGreaterThan(0);
    // The FIRST heading in document order must be the h1. The shared empty state contributes its own h2
    // below, which is correct and expected - what must never happen again is an h2 preceding the h1.
    expect(headings[0].tagName).toBe('H1');
  });

  it('offers exactly one recovery affordance, and it is an in-application link to the root', () => {
    const links: readonly HTMLAnchorElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('a') as NodeListOf<HTMLAnchorElement>,
    );

    // Exactly one: the page header's action slot is deliberately left unfilled so a keyboard
    // reader is not given two stops to the same address on a view holding one sentence.
    expect(links.length).toBe(1);
    expect(textOf(links[0])).toBe(EXPECTED_RECOVERY_LABEL);

    // `routerLink` resolves to an href of the root, so the navigation stays inside the application. A
    // document reload would end the session, because the access token is held in memory only - recovering
    // from a mistyped address must not cost the reader their session.
    expect(links[0].getAttribute('href')).toBe('/');
  });

  it('fills the empty state action slot that a router-loaded component could never fill', () => {
    const actions = query('.empty-state__actions');

    expect(actions).not.toBeNull();
    expect(actions?.childElementCount).toBeGreaterThan(0);
    expect(actions?.querySelector('a')).not.toBeNull();
  });

  it('renders the sentence the route supplies, and re-renders when it changes', () => {
    // Defaults to the route's wording, so a route that omitted the key still explains itself.
    expect(fixture.nativeElement.textContent).toContain(ROUTE_MESSAGE);

    fixture.componentRef.setInput('message', OTHER_MESSAGE);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(OTHER_MESSAGE);
    expect(fixture.nativeElement.textContent).not.toContain(ROUTE_MESSAGE);
  });
  // THE RECOVERY LINK IS ALSO A POINTER TARGET - QA-18. It is the only control on the only screen a lost
  // operator reaches, and it was a bare inline anchor at the shared 17px line height.
  it('gives the recovery link the full target minimum without changing how it reads', () => {
    const link = query<HTMLAnchorElement>('a');

    expect(link).not.toBeNull();

    const bounds = (link as HTMLAnchorElement).getBoundingClientRect();

    expect(bounds.height).toBeGreaterThanOrEqual(44);

    // BLOCK-LEVEL, AND NOT BECAUSE THIS COMPONENT ASKED FOR IT: the slot the link is projected into is a
    // flex container, so the anchor is a flex item and its display is blockified. That is what makes
    // `align-content` able to centre the caption, and it is why the stylesheet declares no `display` at all.
    expect(getComputedStyle(link as HTMLAnchorElement).display).toBe('block');
  });

  it('keeps the recovery caption legible inside the enlarged box', () => {
    const link = query<HTMLAnchorElement>('a');

    expect(textOf(link)).toBe(EXPECTED_RECOVERY_LABEL);
  });
});
