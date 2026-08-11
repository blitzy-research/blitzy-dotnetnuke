import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { NotFoundComponent } from './not-found.component';

/**
 * The heading and the recovery caption, RESTATED INDEPENDENTLY rather than read back off the
 * component.
 *
 * Reading an expectation out of the subject makes actual and expected move together, so an
 * accidental edit to the shipped wording would update both sides at once and the spec would stay
 * green while readers saw different words. Restating them here is what makes these assertions an
 * oracle: changing either caption now fails this spec, which is the signal a caption change
 * should produce.
 */
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
      // The template declares a `routerLink`, which needs the directive's providers to resolve.
      // An empty route table is enough: nothing here navigates, and the assertion below reads
      // the rendered `href` rather than following it.
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
    // The wildcard route used to load the shared empty state directly, and that component
    // states its heading as an `<h2>` because eight screens embed it BENEATH their own page
    // heading. Rendered as a whole routed view it therefore left the address with no `<h1>`
    // at all. Asserting the level explicitly - not merely that the words appear somewhere -
    // is what keeps that regression from returning.
    const heading = query<HTMLHeadingElement>('h1');

    expect(heading).not.toBeNull();
    expect(textOf(heading)).toBe(EXPECTED_TITLE);
  });

  it('opens the document outline at level one, with no heading above the h1', () => {
    const headings: readonly HTMLHeadingElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('h1,h2,h3,h4,h5,h6') as NodeListOf<HTMLHeadingElement>,
    );

    expect(headings.length).toBeGreaterThan(0);
    // The FIRST heading in document order must be the h1. The shared empty state contributes
    // its own h2 below, which is correct and expected - what must never happen again is an h2
    // preceding the h1.
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

    // `routerLink` resolves to an href of the root, so the navigation stays inside the
    // application. A document reload would end the session, because the access token is held
    // in memory only - recovering from a mistyped address must not cost the reader their
    // session.
    expect(links[0].getAttribute('href')).toBe('/');
  });

  it('fills the empty state action slot that a router-loaded component could never fill', () => {
    // The second half of the original defect: `empty-state` ends in an action slot wrapping an
    // `<ng-content />`, and a component loaded straight from the router has no host template
    // projecting into it, so the slot was structurally guaranteed to be empty. Asserting the
    // slot has an element child - not just that a link exists somewhere - is what proves the
    // projection actually lands where the reader looks for it.
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
});
