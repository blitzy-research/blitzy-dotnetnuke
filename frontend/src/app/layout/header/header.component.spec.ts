/**
 * Specification for {@link HeaderComponent} — the administration console's single `banner` landmark. The
 * band is presentational by construction: it injects nothing, performs no I/O, makes no routing decision
 * and holds no state beyond its three declared inputs.
 */

// Signing out is a THREE-PARTY arrangement, and this band owns only the first part.

// MIGRATION: the legacy REGISTER affordance is deliberately not carried forward, and its absence is
// asserted rather than merely omitted.

import { ChangeDetectionStrategy, Component } from '@angular/core';
import type { Signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { By } from '@angular/platform-browser';
import { RouterLink, provideRouter } from '@angular/router';

import { AuthStore } from '../../core/state/auth.store';
import { HeaderComponent } from './header.component';

/** The component's protected surface, exposed to this specification under a structural type. */
type HeaderInternals = {
  onSignOutClick(): void;
};

/**
 * A host that mounts the band through its element selector using the BARE attribute form of the in-flight
 * input. The `booleanAttribute` transform is observable through `setInput` as well, but only a real
 * template proves that the attribute form — an attribute written with no value at all — is understood,
 * because that form has no equivalent in a programmatic call.
 */
@Component({
  standalone: true,
  imports: [HeaderComponent],
  template: '<app-header signingOut userName="Grace Hopper" />',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class BareAttributeHostComponent {}

/**
 * Narrows a query result to a present element, failing loudly when it is absent.
 *
 * @param found The result of a DOM query.
 * @param description What the caller was looking for, for the failure message.
 * @returns The same element, narrowed to non-nullable.
 */
function present<T extends Element>(found: T | null, description: string): T {
  if (found === null) {
    throw new Error(`Expected the banner to render ${description}, but no such element was present.`);
  }

  return found;
}

/**
 * Asserts that a signal is a read-only projection: callable, and carrying neither of the mutators a
 * writable signal exposes.
 *
 * @param candidate The published projection.
 * @param name The member name, for the failure message.
 */
function expectReadOnlySignal(candidate: Signal<unknown>, name: string): void {
  expect(typeof candidate).withContext(`${name} must be a callable signal`).toBe('function');
  expect('set' in candidate).withContext(`${name} must not publish set()`).toBeFalse();
  expect('update' in candidate).withContext(`${name} must not publish update()`).toBeFalse();
}

describe('HeaderComponent', () => {
  let fixture: ComponentFixture<HeaderComponent>;
  let component: HeaderComponent;
  let httpMock: HttpTestingController;

  /** The component's host element, typed once so that every query below is typed too. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * Sets one of the component's inputs and re-renders.
   *
   * @param name The input to set.
   * @param value The value to set it to, before any declared transform.
   */
  function setInput(
    name:
      | 'applicationName'
      | 'userName'
      | 'signingOut'
      | 'accountProfileLink'
      | 'accountPasswordLink',
    value: unknown,
  ): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /** Returns the banner element, or null when none is rendered. */
  function banner(): HTMLElement | null {
    return host().querySelector('header');
  }

  /** Returns the identity affordance, or null when none is rendered. */
  function brand(): HTMLAnchorElement | null {
    return host().querySelector<HTMLAnchorElement>('a.app-header__brand');
  }

  /** Returns the trailing session cluster, or null when none is rendered. */
  function actions(): HTMLElement | null {
    return host().querySelector<HTMLElement>('div.app-header__actions');
  }

  /** Returns the signed-in account's display-name element, or null when absent. */
  function userNameElement(): HTMLElement | null {
    return host().querySelector<HTMLElement>('span.app-header__user');
  }

  /** Returns the sign-out control, or null when absent. */
  function signOutButton(): HTMLButtonElement | null {
    return host().querySelector<HTMLButtonElement>('button.app-header__logout');
  }

  /** Returns the account affordance, or null when absent. */
  function accountLink(): HTMLAnchorElement | null {
    return host().querySelector<HTMLAnchorElement>('a.app-header__account-link');
  }

  /**
   * Returns every account-scoped affordance, in document order. The band renders two, so a case about
   * ORDER or about one appearing without the other needs the whole set rather than the first match.
   *
   * @returns the account-scoped anchors the band currently renders.
   */
  function accountLinks(): readonly HTMLAnchorElement[] {
    return Array.from(host().querySelectorAll<HTMLAnchorElement>('a.app-header__account-link'));
  }

  /**
   * Counts the emissions the band produces for the remainder of a specification.
   *
   * @returns a reader for the number of emissions observed since this call.
   */
  function countEmissions(): () => number {
    let emissions = 0;
    component.signOut.subscribe(() => {
      emissions += 1;
    });

    return () => emissions;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // The band is standalone, so it is IMPORTED rather than declared. There is no declaring module
      // anywhere in this workspace to add it to.
      imports: [HeaderComponent],
      providers: [
        // The identity affordance is a router link, so a router must be present for the directive to
        // resolve. An empty route table is sufficient: every expectation is about the rendered `href`,
        // never about navigation occurring.
        provideRouter([]),
        // Order is load-bearing, and this is the only correct order. The real transport is registered FIRST
        // and the testing backend SECOND, because `provideHttpClientTesting()` REPLACES the backend the
        // first provider installed.
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HeaderComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.expectNone(() => true, 'the banner must issue no HTTP request');
    httpMock.verify();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        HeaderComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone, so the shell can import it directly', () => {
      const definition = (
        HeaderComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });
  });

  describe('landmark structure', () => {
    it('renders a native header element, which is the banner landmark', () => {
      expect(banner()).not.toBeNull();
    });

    it('renders EXACTLY ONE banner, because two banners is an accessibility defect', () => {
      // A screen reader offers the banner landmark as a navigation target. Two of them makes that target
      // ambiguous, and the ambiguity is silent: nothing about the rendered page looks wrong.
      expect(host().querySelectorAll('header').length).toBe(1);
    });

    it('writes no explicit role, because a native header is already the banner landmark', () => {
      expect(present(banner(), 'a banner').hasAttribute('role')).toBeFalse();
    });

    it('claims no landmark that belongs to a sibling of the shell', () => {
      expect(host().querySelectorAll('nav').length).toBe(0);
      expect(host().querySelectorAll('main').length).toBe(0);
      expect(host().querySelectorAll('footer').length).toBe(0);
    });

    it('renders the identity affordance as the first child of the banner', () => {
      expect(present(banner(), 'a banner').firstElementChild).toBe(present(brand(), 'an identity affordance'));
    });

    it('renders the session cluster as the last child of the banner', () => {
      expect(present(banner(), 'a banner').lastElementChild).toBe(present(actions(), 'a session cluster'));
    });
  });

  describe('identity affordance', () => {
    it('renders the build-time application name when nothing is bound', () => {
      const applicationName: string = component.applicationName;

      expect(typeof applicationName).toBe('string');
      expect(applicationName.length).toBeGreaterThan(0);
      expect(present(brand(), 'an identity affordance').textContent?.trim()).toBe(applicationName);
    });

    it('renders a bound application name in preference to the default', () => {
      setInput('applicationName', 'Contoso Administration');

      expect(present(brand(), 'an identity affordance').textContent?.trim()).toBe('Contoso Administration');
    });

    it('resolves to the application root, so the affordance returns the operator home', () => {
      const href = present(brand(), 'an identity affordance').getAttribute('href');

      expect(href).toBe('/');
    });

    it('is a router link rather than a plain href, so activating it does not reload the document', () => {
      const linked = fixture.debugElement.queryAll(By.directive(RouterLink));

      expect(linked.length).toBe(1);
      expect(linked[0].nativeElement).toBe(present(brand(), 'an identity affordance'));
    });

    it('renders exactly one link in the whole band, so no second destination is offered', () => {
      expect(host().querySelectorAll('a').length).toBe(1);
    });

    // The identity affordance is retained as legacy parity but resolves to the application ROOT rather than
    // to a profile screen, and it is captioned with the application's name rather than with the operator's.
    it('offers no profile destination, because the band exposes no identifier to build one from', () => {
      setInput('userName', 'Grace Hopper');

      const links = Array.from(host().querySelectorAll('a'));
      const profileLinks = links.filter((link) => (link.getAttribute('href') ?? '').includes('/profile'));

      expect(profileLinks.length).toBe(0);
      expect(present(userNameElement(), 'a display name').closest('a')).toBeNull();
    });

    it('attaches no profile tooltip, because there is no profile affordance to describe', () => {
      setInput('userName', 'Grace Hopper');

      const displayName = present(userNameElement(), 'a display name');

      expect(displayName.hasAttribute('title')).toBeFalse();
      expect(host().textContent ?? '').not.toContain('Click Here To Edit Your Account Profile');
    });
  });

  describe('session cluster when no account is signed in', () => {
    it('renders the cluster element so the stylesheet can collapse it', () => {
      expect(actions()).not.toBeNull();
    });

    it('leaves the cluster with no element children, which is what the empty rule keys on', () => {
      expect(present(actions(), 'a session cluster').children.length).toBe(0);
    });

    it('renders no display name', () => {
      expect(userNameElement()).toBeNull();
    });

    it('renders no sign-out control, because there is no session to end', () => {
      expect(signOutButton()).toBeNull();
    });

    // Signing in is a screen of its own in the target, reached as a route rather than as a control in the
    // chrome, and the console's guards send an unauthenticated caller there before any shell is mounted.
    it('renders no sign-in affordance, because reaching the banner already implies a session', () => {
      expect(host().textContent ?? '').not.toContain('Login');
      expect(host().textContent ?? '').not.toContain('Sign in');
    });

    it('treats a whitespace-only display name as no account at all', () => {
      setInput('userName', '   ');

      expect(userNameElement()).toBeNull();
      expect(signOutButton()).toBeNull();
      expect(present(actions(), 'a session cluster').children.length).toBe(0);
    });

    it('treats an empty display name as no account at all', () => {
      setInput('userName', '');

      expect(userNameElement()).toBeNull();
      expect(signOutButton()).toBeNull();
    });
  });

  describe('session cluster when an account is signed in', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    it('renders the display name it was given, verbatim', () => {
      expect(present(userNameElement(), 'a display name').textContent?.trim()).toBe('Grace Hopper');
    });

    it('renders the sign-out control', () => {
      expect(signOutButton()).not.toBeNull();
    });

    it('renders the display name ahead of the sign-out control', () => {
      const children = Array.from(present(actions(), 'a session cluster').children);

      expect(children.length).toBe(2);
      expect(children[0]).toBe(present(userNameElement(), 'a display name'));
      expect(children[1]).toBe(present(signOutButton(), 'a sign-out control'));
    });

    it('declares the control as a button rather than a submit, so it cannot post a form', () => {
      // Explicitly, not by default: a bare `<button>` inside a form is a SUBMIT button, and the band cannot
      // know whether a future consumer will place it inside one.
      expect(present(signOutButton(), 'a sign-out control').getAttribute('type')).toBe('button');
    });

    it('is a real button element rather than an anchor styled as one', () => {
      const control = present(signOutButton(), 'a sign-out control');

      expect(control.tagName).toBe('BUTTON');
      expect(control.hasAttribute('href')).toBeFalse();
    });

    it('leaves the control enabled while no sign-out is in flight', () => {
      expect(present(signOutButton(), 'a sign-out control').disabled).toBeFalse();
    });
  });

  describe('measured legacy wording', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    // MIGRATION: the caption is the measured legacy wording, pinned here rather than left to drift. The
    // delivered template renders that same word, so there is no divergence to record — only an expectation,
    // which is what makes any future drift a failing test rather than an unnoticed change.
    it('captions the sign-out control with the measured legacy wording', () => {
      expect(present(signOutButton(), 'a sign-out control').textContent?.trim()).toBe('Logout');
    });

    it('labels the control with text rather than an icon alone, so it is announced', () => {
      // The substance of the parity requirement, independent of the exact wording: the control carries an
      // accessible name derived from real text content.
      const control = present(signOutButton(), 'a sign-out control');
      const accessibleName = (control.textContent ?? '').trim();

      expect(accessibleName.length).toBeGreaterThan(0);
      expect(control.querySelectorAll('img').length).toBe(0);
      expect(control.hasAttribute('aria-label')).toBeFalse();
    });

    it('renders no register affordance, because self-registration is not carried forward', () => {
      const text = host().textContent ?? '';
      const links = Array.from(host().querySelectorAll('a'));

      expect(text).not.toContain('Register');
      expect(links.some((link) => (link.getAttribute('href') ?? '').includes('register'))).toBeFalse();
    });

    it('offers no sign-out ROUTE, because ending a session is a command and not a destination', () => {
      // The route table declares no sign-out route. A link to one would be announced as a link and would
      // navigate nowhere — the legacy band did exactly this, navigating to a "Logoff" address, and that is
      // the shape being deliberately not reproduced.
      const targets = Array.from(host().querySelectorAll('a')).map((link) => link.getAttribute('href') ?? '');

      expect(targets.some((target) => target.toLowerCase().includes('logoff'))).toBeFalse();
      expect(targets.some((target) => target.toLowerCase().includes('logout'))).toBeFalse();
    });

    it('contributes no decorative separator text to the accessibility tree', () => {
      expect(host().textContent ?? '').not.toContain('|');
      expect(host().querySelectorAll('[aria-hidden]').length).toBe(0);
    });
  });

  describe('account affordances', () => {
    it('renders nothing when no address has been supplied', () => {
      setInput('userName', 'The Caller');

      // A signed-in session alone is not enough: without an address there is nowhere to go, and
      // a link with an empty target navigates to the current page.
      expect(accountLink()).toBeNull();
    });

    it('renders nothing when an address is supplied without a session', () => {
      setInput('accountProfileLink', '/users/7/profile');

      // An address to one account's own screen is meaningless without the account that holds it,
      // and the whole cluster is gated on the session in any case.
      expect(accountLink()).toBeNull();
    });

    it('renders a link to each supplied address once both are present', () => {
      setInput('userName', 'The Caller');
      setInput('accountProfileLink', '/users/7/profile');
      setInput('accountPasswordLink', '/users/7/password');

      const links = accountLinks();

      expect(links.length).toBe(2);
      expect(links.map((link) => link.getAttribute('href'))).toEqual([
        '/users/7/profile',
        '/users/7/password',
      ]);
    });

    it('captions each link with the measured legacy command wording', () => {
      setInput('userName', 'The Caller');
      setInput('accountProfileLink', '/users/7/profile');
      setInput('accountPasswordLink', '/users/7/password');

      expect(accountLinks().map((link) => link.textContent?.trim())).toEqual([
        'Manage Profile',
        'Manage Password',
      ]);
      expect(component.accountProfileLabel).toBe('Manage Profile');
      expect(component.accountPasswordLabel).toBe('Manage Password');
    });

    it('renders an address naming account zero, which is a real key', () => {
      // ⚠ SENTINEL DISCIPLINE. The band applies no test of any kind to the address it is given, so an
      // account key of zero survives.
      setInput('userName', 'The Caller');
      setInput('accountProfileLink', '/users/0/profile');

      expect(accountLink()?.getAttribute('href')).toBe('/users/0/profile');
    });

    it('treats an address of whitespace as no address at all', () => {
      setInput('userName', 'The Caller');
      setInput('accountProfileLink', '   ');
      setInput('accountPasswordLink', '   ');

      // The same emptiness test the caption is given: a blank address would render a link that
      // announces itself and then navigates nowhere.
      expect(accountLink()).toBeNull();
    });

    it('renders each affordance independently of the other', () => {
      // The two share one presence test but not one condition: a container that resolves only
      // one address must publish only one link rather than both or neither.
      setInput('userName', 'The Caller');
      setInput('accountPasswordLink', '/users/7/password');

      const links = accountLinks();

      expect(links.length).toBe(1);
      expect(links[0].getAttribute('href')).toBe('/users/7/password');
    });

    it('is a link and not a button, because it is a destination rather than a command', () => {
      setInput('userName', 'The Caller');
      setInput('accountProfileLink', '/users/7/profile');

      // The distinction is what assistive technology announces, and it is the same distinction
      // the sign-out control beside it makes in the opposite direction.
      expect(accountLink()?.tagName).toBe('A');
      expect(host().querySelectorAll('button.app-header__account-link').length).toBe(0);
    });

    it('does not disable itself while a sign-out is in flight', () => {
      setInput('userName', 'The Caller');
      setInput('accountProfileLink', '/users/7/profile');
      setInput('signingOut', true);

      expect(accountLink()).not.toBeNull();
      expect(accountLink()?.hasAttribute('disabled')).toBeFalse();
    });
  });

  describe('sign-out gesture', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    it('emits exactly once per activation', () => {
      const emissions = countEmissions();

      present(signOutButton(), 'a sign-out control').click();

      expect(emissions()).toBe(1);
    });

    it('emits no payload, because the session already knows whose it is', () => {
      const payloads: unknown[] = [];
      component.signOut.subscribe((value) => {
        payloads.push(value);
      });

      present(signOutButton(), 'a sign-out control').click();

      expect(payloads).toEqual([undefined]);
    });

    it('emits once per activation when activated repeatedly', () => {
      const emissions = countEmissions();
      const control = present(signOutButton(), 'a sign-out control');

      control.click();
      control.click();
      control.click();

      expect(emissions()).toBe(3);
    });

    it('disables the control while a sign-out is in flight', () => {
      setInput('signingOut', true);

      // A genuine `disabled` attribute rather than `aria-disabled`: the control must actually stop accepting
      // activation while a request is in flight, not merely announce that it will not.
      expect(present(signOutButton(), 'a sign-out control').disabled).toBeTrue();
    });

    it('emits nothing when the control is activated while a sign-out is in flight', () => {
      setInput('signingOut', true);
      const emissions = countEmissions();

      present(signOutButton(), 'a sign-out control').click();

      expect(emissions()).toBe(0);
    });

    it('refuses a direct call while a sign-out is in flight, not merely a click', () => {
      setInput('signingOut', true);
      const emissions = countEmissions();

      (component as unknown as HeaderInternals).onSignOutClick();

      expect(emissions()).toBe(0);
    });

    it('accepts a direct call once the sign-out completes', () => {
      setInput('signingOut', true);
      setInput('signingOut', false);
      const emissions = countEmissions();

      (component as unknown as HeaderInternals).onSignOutClick();

      expect(emissions()).toBe(1);
    });

    it('performs no session teardown of its own when the gesture is reported', () => {
      // The band REPORTS and does not act. It holds no credential, so there is nothing here to clear, and
      // it takes no router dependency, so there is nowhere here to navigate.
      const emissions = countEmissions();

      present(signOutButton(), 'a sign-out control').click();

      expect(emissions()).toBe(1);
      expect(userNameElement()).not.toBeNull();
      expect(signOutButton()).not.toBeNull();
      expect(present(signOutButton(), 'a sign-out control').disabled).toBeFalse();
    });
  });

  describe('in-flight input', () => {
    it('defaults to not in flight', () => {
      expect(component.signingOut).toBeFalse();
    });

    it('reads an empty string as true, through the boolean attribute transform', () => {
      // The programmatic equivalent of the bare attribute form: a browser reports a valueless attribute as
      // the empty string, and without the transform the empty string would be falsy.
      setInput('userName', 'Grace Hopper');
      setInput('signingOut', '');

      expect(component.signingOut).toBeTrue();
      expect(present(signOutButton(), 'a sign-out control').disabled).toBeTrue();
    });

    it('reads the literal string false as false, which is the transform contract', () => {
      setInput('userName', 'Grace Hopper');
      setInput('signingOut', 'false');

      expect(component.signingOut).toBeFalse();
      expect(present(signOutButton(), 'a sign-out control').disabled).toBeFalse();
    });

    it('reads the bare attribute form as true, in a real template', () => {
      const hosted = TestBed.createComponent(BareAttributeHostComponent);
      hosted.detectChanges();

      const hostedElement = hosted.nativeElement as HTMLElement;
      const hostedButton = hostedElement.querySelector<HTMLButtonElement>('button.app-header__logout');

      expect(present(hostedButton, 'a sign-out control in the hosted band').disabled).toBeTrue();
    });
  });

  describe('sentinel discipline', () => {
    // The legacy null module encoded a missing integer as MINUS ONE and a missing string as the EMPTY
    // STRING. Minus one is simultaneously a real key — `Portals.PortalID` is declared `IDENTITY(-1, 1)` and
    // the shipped default portal is inserted as zero, while `Roles.RoleID` is declared `IDENTITY(0, 1)` —
    // so `0` and `-1` are both DATA and both sentinels, depending only on which column is being read.

    it('renders a display name of "-1", because a sentinel-shaped name is still a name', () => {
      setInput('userName', '-1');

      expect(present(userNameElement(), 'a display name').textContent?.trim()).toBe('-1');
      expect(signOutButton()).not.toBeNull();
    });

    it('renders a display name of "0", because a sentinel-shaped name is still a name', () => {
      setInput('userName', '0');

      expect(present(userNameElement(), 'a display name').textContent?.trim()).toBe('0');
      expect(signOutButton()).not.toBeNull();
    });

    it('renders a display name that is a bare zero character rather than treating it as absent', () => {
      // Written out separately from the case above because this is the shape a naive numeric guard
      // mishandles: a caption arriving as the digit zero is indistinguishable from a falsy integer to
      // anything that tests it for truthiness after coercion.
      setInput('userName', ' 0 ');

      expect(present(userNameElement(), 'a display name').textContent?.trim()).toBe('0');
    });

    it('holds no identifier input at all, so no identifier can be truthiness-guarded', () => {
      // A negative structural expectation, and the point of it is durability: if an identifier input is
      // ever added, this fails and whoever adds it has to come and read the sentinel discipline above
      // before deciding how to guard it.
      const inputNames = ['userId', 'userID', 'portalId', 'portalID', 'roleId', 'tabId'];
      const held = Object.keys(component);

      for (const name of inputNames) {
        expect(held).withContext(`the band must not hold ${name}`).not.toContain(name);
      }
    });
  });

  describe('untrusted text', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    it('renders a display name containing markup as literal text', () => {
      setInput('userName', '<b>Grace</b>');

      const displayName = present(userNameElement(), 'a display name');

      expect(displayName.textContent).toBe('<b>Grace</b>');
      expect(displayName.querySelector('b')).toBeNull();
      expect(displayName.innerHTML).toContain('&lt;b&gt;');
      expect(displayName.innerHTML).not.toContain('<b>');
    });

    it('renders already-escaped legacy resource text without unescaping it', () => {
      // Dozens of values in the legacy resource corpus carry escaped HTML. Presenting such a value must show
      // the escape sequence the data actually holds, not silently resolve it into a line break.
      setInput('userName', '&lt;br&gt;');

      const displayName = present(userNameElement(), 'a display name');

      expect(displayName.textContent).toBe('&lt;br&gt;');
      expect(displayName.querySelector('br')).toBeNull();
      expect(displayName.innerHTML).toContain('&amp;lt;');
    });

    it('renders the legacy image-caption shape as text, materialising no image', () => {
      // This is the exact shape the legacy controls rewrote: a caption carrying `src=` had its path fixed up
      // and was then emitted as markup. Interpolation makes that impossible here.
      setInput('userName', '<img src="logo.gif">');

      const displayName = present(userNameElement(), 'a display name');

      expect(displayName.querySelectorAll('img').length).toBe(0);
      expect(displayName.textContent).toContain('src=');
      expect(host().querySelectorAll('img').length).toBe(0);
    });

    it('renders an application name containing markup as literal text', () => {
      setInput('applicationName', '<span>Contoso</span>');

      const identity = present(brand(), 'an identity affordance');

      expect(identity.textContent).toBe('<span>Contoso</span>');
      expect(identity.querySelector('span')).toBeNull();
    });

    it('never materialises an executable element from bound text', () => {
      setInput('applicationName', '<b>name</b>');
      setInput('userName', '<i>user</i>');

      expect(host().querySelectorAll('script').length).toBe(0);
      expect(host().querySelectorAll('iframe').length).toBe(0);
      expect(host().querySelectorAll('object').length).toBe(0);
      expect(host().querySelectorAll('b').length).toBe(0);
      expect(host().querySelectorAll('i').length).toBe(0);
    });
  });

  describe('accessibility', () => {
    beforeEach(() => {
      setInput('userName', 'Grace Hopper');
    });

    it('places the identity affordance in the tab order without a tabindex override', () => {
      const identity = present(brand(), 'an identity affordance');

      expect(identity.hasAttribute('tabindex')).toBeFalse();
      expect(identity.tabIndex).toBe(0);
    });

    it('places the sign-out control in the tab order without a tabindex override', () => {
      const control = present(signOutButton(), 'a sign-out control');

      expect(control.hasAttribute('tabindex')).toBeFalse();
      expect(control.tabIndex).toBe(0);
    });

    it('lets the keyboard reach the identity affordance', () => {
      const identity = present(brand(), 'an identity affordance');

      identity.focus();

      expect(document.activeElement).toBe(identity);
    });

    it('lets the keyboard reach the sign-out control', () => {
      const control = present(signOutButton(), 'a sign-out control');

      control.focus();

      expect(document.activeElement).toBe(control);
    });

    it('takes the sign-out control out of the tab order while a sign-out is in flight', () => {
      // A disabled control is correctly skipped by sequential navigation. This is the behavioural
      // difference between a real `disabled` attribute and an `aria-disabled` annotation, and it is why the
      // component uses the former.
      setInput('signingOut', true);
      const control = present(signOutButton(), 'a sign-out control');

      control.focus();

      expect(control.disabled).toBeTrue();
      expect(document.activeElement).not.toBe(control);
    });

    it('surfaces no live region, because announcing a failure belongs to the shared error banner', () => {
      expect(host().querySelectorAll('[aria-live]').length).toBe(0);
      expect(host().querySelectorAll('[role="alert"]').length).toBe(0);
    });
  });

  describe('boundary with the session store', () => {
    let store: AuthStore;

    beforeEach(() => {
      store = TestBed.inject(AuthStore);
    });

    it('holds no reference to the session store, so the band needs no transport to mount', () => {
      // The seam at which the session belongs is one level above the layout. Injecting the store here would
      // pull in the authentication service and, through it, the HTTP transport — which is why the band
      // takes a display name as an input and reports the gesture as an output instead.
      const held = Object.values(component);

      expect(held.some((value) => value instanceof AuthStore)).toBeFalse();
    });

    it('publishes the three members this band would otherwise have injected', () => {
      // Cross-checked rather than assumed: the reason the band does not inject the store is not that the
      // store lacks what it needs. It has all three.
      expect(typeof store.currentUser).toBe('function');
      expect(typeof store.isSigningOut).toBe('function');
      expect(typeof store.logout).toBe('function');
    });

    it('publishes every slice as a read-only projection, so no component can mutate it', () => {
      expectReadOnlySignal(store.phase, 'phase');
      expectReadOnlySignal(store.problem, 'problem');
      expectReadOnlySignal(store.failureStatus, 'failureStatus');
      expectReadOnlySignal(store.currentUser, 'currentUser');
      expectReadOnlySignal(store.isSigningOut, 'isSigningOut');
      expectReadOnlySignal(store.portalId, 'portalId');
      expectReadOnlySignal(store.verificationRequired, 'verificationRequired');
    });

    it('expresses an absent tenant as null and never as a legacy integer sentinel', () => {
      // The counterpart to the sentinel suite above, checked at the seam the band reads from.
      // `Portals.PortalID` is declared `IDENTITY(-1, 1)` and the shipped default portal is inserted as
      // zero, so a guard written as `if (portalId)` or `portalId > 0` would reject two real tenants.
      expect(store.currentUser()).toBeNull();
      expect(store.portalId()).toBeNull();
      expect(store.portalId()).not.toBe(0);
      expect(store.portalId()).not.toBe(-1);
    });

    it('reports no sign-out in flight before any command is issued', () => {
      expect(store.isSigningOut()).toBeFalse();
      expect(store.phase()).toBe('idle');
    });

    it('renders the band and resolves the store without issuing a single request', () => {
      // The positive form of the proof the afterEach applies to every specification here. The store's
      // commands are cold and deferred, so merely resolving it must not reach the network — and the band
      // itself has no transport to reach it with.
      setInput('userName', 'Grace Hopper');

      expect(store).toBeTruthy();
      expect(banner()).not.toBeNull();
      expect(signOutButton()).not.toBeNull();

      httpMock.expectNone(() => true, 'neither the banner nor store resolution may issue a request');
    });

    it('does not end the session when the band reports the gesture', () => {
      // Reporting is not acting. The store must remain idle with no session torn down, because the consumer
      // that owns the session is the one that calls the command.
      setInput('userName', 'Grace Hopper');
      const emissions = countEmissions();

      present(signOutButton(), 'a sign-out control').click();

      expect(emissions()).toBe(1);
      expect(store.phase()).toBe('idle');
      expect(store.isSigningOut()).toBeFalse();
    });
  });
});
