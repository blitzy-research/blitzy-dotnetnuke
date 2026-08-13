import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
// The router is imported for its INJECTION TOKEN alone, so a double can stand in its place
// and be proven untouched. No routing is configured: neither `provideRouter` nor the
// deprecated router testing module appears below, which is itself half of the no-navigation
// proof — a directive that needed the router could not resolve one here at all.
import { Router } from '@angular/router';

// Type-only, matching the convention used by the sibling core specifications: the session
// and identity shapes are erased at compile time, so this file pulls no runtime code out
// of the model.
import type { AuthSession, CurrentUser } from '../../core/models/auth.model';
import type { PermissionKey } from '../../core/models/permission.model';
import { TokenStorageService } from '../../core/services/token-storage.service';
import { AuthStore } from '../../core/state/auth.store';

import { HasPermissionDirective } from './has-permission.directive';

/**
 * Specification for {@link HasPermissionDirective}.
 *
 * ## What is under test
 *
 * The directive against the REAL auth store, driven by storing and clearing a real
 * session through the token custodian. The store is not stubbed, because the thing worth
 * proving is that the directive reads the resolved permission list the application
 * actually publishes — a hand-written stub would prove only that the stub was shaped the
 * way this file imagined.
 *
 * A transport provider and its testing double are configured because the store reaches
 * the authentication service, which reaches the HTTP client. `httpMock.verify()` in
 * `afterEach` then earns its place twice over: it is what turns "this directive issues no
 * HTTP request" from a claim in a comment into an enforced expectation, since any request
 * the directive provoked on init, on a session change or on a key change would fail the
 * verification.
 *
 * ## The load-bearing expectations
 *
 * `key comparison` and `denial` matter more than the render cases. A permission affordance
 * that fails OPEN is worse than one that never appears, so the specifications that a
 * differently cased key, a padded key, a merely containing key and the empty string are
 * all REFUSED are the ones that would catch a well-meaning `toUpperCase()` or a
 * truthiness guard being introduced later.
 *
 * `removes rather than hides` and `keeps the same element` are the other two: the first
 * fixes structural-removal semantics against a future change to CSS hiding, and the second
 * fixes idempotence, so an effect that rebuilt its view on every unrelated signal change
 * would be caught rather than silently degrading focus and DOM state.
 *
 * `guarantees` is the architectural pair, and it earns its place because both properties it
 * fixes are invisible in the rendered output: the directive issues NO request and performs
 * NO navigation. The legacy code this replaces did both from inside a property getter, so
 * neither is a hypothetical regression.
 *
 * There is no predecessor test to port: the legacy tree contains no automated tests at
 * all — not a test project, not a fixture, not an assertion — so every expectation below is
 * net-new rather than translated.
 *
 * ## How change is driven, verified against the installed framework
 *
 * The directive's verdict lives in an `effect()`, so a specification that only mutated a
 * signal would assert against a view that had not been rebuilt yet. Every case therefore
 * settles through the local `settle()` helper, which runs change detection and then drains
 * the effect schedulers.
 *
 * The draining call is `TestBed.flushEffects()`. That choice was verified against the
 * installed `@angular/core` rather than assumed: `flushEffects(): void` is declared on the
 * `TestBed` interface in `@angular/core/testing`, and `TestBed.tick()` does NOT exist in
 * this version — the only `tick` it publishes is the zone-based timer helper, which belongs
 * to the simulated-time utilities and is not wanted here because nothing under test is
 * asynchronous and nothing below uses a timer of any kind. `flushEffects()` drains both the
 * microtask and the root effect scheduler, so it is the complete form of "let every pending
 * effect run" available on this version.
 *
 * `httpMock.expectNone(...)` is likewise the matcher this version publishes — a predicate
 * overload taking the request and answering whether it matches — so the "no request at all"
 * expectation is stated positively rather than left implicit in `verify()`.
 *
 * ## Files this specification reaches, and why each is unavoidable
 *
 * Three collaborators are imported beyond the directive itself, and each is a genuine
 * dependency of the subject rather than a convenience:
 *
 * - the permission-key vocabulary, because the directive's input is typed to the closed
 *   union rather than to `string`. Under strict template checking a host cannot bind the
 *   input without that type, and the alternatives — a loosely typed field or a suppression
 *   comment — would each defeat the very narrowing being tested;
 * - the token custodian, because the permission list the directive reads is a DERIVED
 *   projection over the session and the store holds no writable member for it. Storing a
 *   real session is the only supported way to state "the caller holds these keys";
 * - the auth store, so the projection the directive depends on can be asserted to be
 *   read-only, closing the loop on the claim that nothing here can mutate it.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  // Matches the workspace-wide discipline: every component in the target declares this
  // strategy, so a host that did not would be testing the directive under conditions no real
  // consumer runs it in. The directive's verdict reaches the DOM through its own effect and
  // its own view container, so it must hold under a host that is only checked when marked
  // dirty — which the reactivity cases below prove it does.
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: ` <button *hasPermission="required()" class="guarded" type="button">Edit</button> `,
})
class HostComponent {
  /**
   * Typed exactly as the directive's input is — {@link PermissionKey}, the closed
   * vocabulary, rather than a plain `string`.
   *
   * The narrowing is the point of the design under test. A call site cannot bind a policy
   * name, a permission code or an installation-specific key here without the compiler
   * objecting, which removes a whole class of vocabulary confusion before it can reach a
   * permission decision. The run-time fail-closed behaviour is still exercised below,
   * through {@link UntypedHostComponent}, because a static type is a claim about the source
   * and not a fact about the value.
   *
   * ⚠ HELD IN A SIGNAL, AND THAT IS NOT DECORATION. This host is declared with the
   * push-based change-detection strategy, exactly as every component in the target is, and
   * under that strategy a plain field mutated after the first render never reaches the
   * template: nothing marks the view for checking, so the binding is not re-evaluated and
   * the directive's input never changes. Writing a signal the template reads DOES mark it,
   * which is both the framework-idiomatic way to drive this and the way a real consumer
   * would write it. This was established by execution rather than reasoning — the
   * requirement-changes-on-the-same-element case below fails against a plain field and
   * passes against a signal.
   */
  readonly required = signal<PermissionKey>('EDIT');
}

/**
 * A host that binds the directive through a deliberately WIDENED expression.
 *
 * This is how the run-time half of the fail-closed rule is exercised. The directive's input
 * is statically a {@link PermissionKey}, so a well-typed host cannot express "bind an
 * unknown key" at all — which is the improvement. But a static type constrains the SOURCE,
 * not the value: a template expression can widen, a view model can be loosely typed, and
 * route data is `unknown` at the edges. The cast below reproduces exactly that situation, so
 * the specifications using this host prove the directive still refuses an unrecognised value
 * that reached it despite the type.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: ` <button *hasPermission="widened" class="guarded" type="button">Edit</button> `,
})
class UntypedHostComponent {
  /** The value actually bound, held as an open string. */
  raw = 'EDIT';

  /**
   * The widened binding. The assertion is confined to this one accessor rather than
   * scattered across the template, so the deliberate loosening is visible in one place.
   */
  get widened(): PermissionKey {
    return this.raw as PermissionKey;
  }
}

/**
 * A host that gates a COMPLETE LABELLED UNIT rather than a bare control.
 *
 * This is the arrangement the directive's own guidance prescribes, and it exists here to
 * prove the accessibility consequence of removal rather than concealment. The label, the
 * control it names, and the help text the control points at all sit INSIDE the gated
 * wrapper, so a denial takes the whole group and leaves nothing referring to anything that
 * has gone:
 *
 * - no `label[for]` addressing an element that no longer exists, which would otherwise
 *   announce a field the caller cannot reach and, in most browsers, still move focus when
 *   clicked;
 * - no surviving `aria-describedby`, `aria-labelledby`, `aria-controls` or `aria-owns`
 *   pointing at a removed identifier, each of which degrades a screen reader's announcement
 *   to a bare control with no name or an unresolvable relationship.
 *
 * The unrelated element outside the gate is deliberate: it establishes that removal is
 * SURGICAL, taking the unit and nothing else. It carries no reference INTO the gated unit,
 * because a reference from outside the removed subtree would be the fixture's own defect
 * rather than the directive's — the directive can only be responsible for what it removes.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div *hasPermission="required" class="labelled-unit">
      <label class="unit-label" for="site-name">Site name</label>
      <input
        id="site-name"
        class="unit-control"
        type="text"
        aria-describedby="site-name-help"
        aria-labelledby="site-name-caption"
      />
      <span id="site-name-caption" class="unit-caption">Site name</span>
      <p id="site-name-help" class="unit-help">Shown in the browser title bar.</p>
    </div>
    <p class="outside">Nothing here is gated.</p>
  `,
})
class LabelledUnitHostComponent {
  /** The key the unit requires, typed to the closed vocabulary exactly as the input is. */
  required: PermissionKey = 'EDIT';
}

/**
 * The tenant key an identity fixture is issued against.
 *
 * ⚠ BOTH VALUES ARE REAL TENANTS AND NEITHER MEANS "ABSENT". The tenant table's key column
 * is an identity seeded at MINUS ONE, so the first site provisioned is numbered minus one
 * and the shipped default site is numbered zero. The legacy null contract independently used
 * minus one as its encoding for a missing integer, and its absence test reported true for
 * it — which is exactly the collision that makes `if (id)`, `id > 0` and `id ?? -1` wrong on
 * this data. Both values are therefore exercised here, and nothing below tests a tenant key
 * for truthiness or compares it against either sentinel.
 *
 * The directive reads no tenant key at all, and the specification that proves it reads none
 * is the reason these fixtures differ only in this member.
 */
const DEFAULT_TENANT = 0;

/** The first provisioned tenant, numbered from the identity seed. */
const SEED_TENANT = -1;

function userWith(
  permissions: readonly string[],
  isSuperUser = false,
  portalId: number = DEFAULT_TENANT,
): CurrentUser {
  return {
    // Zero deliberately, alongside the zero tenant below: account, role, page and module keys
    // are all seeded from zero in this schema, so a zero identifier is ordinary data.
    userId: 0,
    portalId,
    portalName: 'Primary',
    username: 'operator',
    displayName: 'Operator',
    email: 'operator@example.test',
    isSuperUser,
    // Follows the host flag, matching what the server reports: a host account administers every
    // tenant. The directive reads neither member, which these cases rely on.
    isPortalAdministrator: isSuperUser,
    roles: ['Administrators'],
    permissions,
  };
}

function sessionWith(
  permissions: readonly string[],
  isSuperUser = false,
  portalId: number = DEFAULT_TENANT,
): AuthSession {
  return {
    accessToken: 'access-token-placeholder',
    expiresAtUtc: '2100-01-01T00:00:00.000Z',
    refreshToken: 'refresh-token-placeholder',
    // Every boolean is a plain, non-nullable boolean, and `false` here is DATA rather than
    // absence: the legacy null contract could not distinguish the two, so admitting a third
    // state would advertise a distinction the source data never made.
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: userWith(permissions, isSuperUser, portalId),
  };
}

/**
 * Attribute names whose value is an element identifier, or a list of them.
 *
 * The set a removal can strand. Read by {@link danglingReferences} to prove that gating a
 * whole labelled unit leaves no reference pointing at something that has gone.
 */
const REFERENCING_ATTRIBUTES = Object.freeze([
  'for',
  'aria-labelledby',
  'aria-describedby',
  'aria-controls',
  'aria-owns',
] as const);

/**
 * Identifier references inside a subtree that no longer resolve to an element.
 *
 * Returns a describable list rather than a boolean so a failure names the offending
 * attribute and identifier instead of merely reporting that something is wrong.
 *
 * @param root The rendered subtree to inspect.
 * @returns One entry per unresolvable reference, empty when every reference resolves.
 */
function danglingReferences(root: HTMLElement): readonly string[] {
  const dangling: string[] = [];

  for (const attribute of REFERENCING_ATTRIBUTES) {
    for (const element of Array.from(root.querySelectorAll(`[${attribute}]`))) {
      const value = element.getAttribute(attribute);

      // Explicitly compared, never asserted away: the accessor answers `string | null` and a
      // present-but-empty value is a different fact from an absent one.
      if (value === null) {
        continue;
      }

      for (const identifier of value.split(/\s+/)) {
        if (identifier === '') {
          continue;
        }

        if (root.querySelector(`#${identifier}`) === null) {
          dangling.push(`${attribute}="${identifier}"`);
        }
      }
    }
  }

  return dangling;
}

/**
 * Strings that are NOT permission keys but are plausible enough to be written by mistake.
 *
 * Three distinct vocabularies exist in this system and only one of them belongs on this
 * input. Every entry below comes from one of the other two, or from a spelling that a
 * lenient comparison would have collapsed onto a real key:
 *
 * - the NINE authorisation policy names the API registers, which are the server's
 *   route-level contract and are evaluated by the route gate, not here;
 * - the permission CODES that scope a key to folders, page definitions or pages;
 * - role names, including the administrator role — spelled in the plural in the legacy
 *   provisioning code, which is the detail that makes it plausible;
 * - differently cased, padded, negated, separator-joined and bracketed spellings.
 *
 * Every one of them must be REFUSED. The failure being prevented is specific and is a
 * fail-OPEN one: a lenient comparison, a separator split or a prefix parse would let one of
 * these collide with a real grant and ADMIT content the server will refuse.
 */
const NON_KEY_VOCABULARY = Object.freeze([
  // The nine registered authorisation policy names. `PortalContentEditor` was ABSENT from this
  // list while it was already registered on the server, which left the one policy name a
  // migration-era reader is most likely to mistake for a permission key — it reads like a
  // capability rather than a role — as the one name this fixture never tried.
  'ModuleView',
  'ModuleEdit',
  'TabView',
  'TabEdit',
  'PortalAdministrator',
  'HostAdministrator',
  'AccountOwner',
  'AccountOwnerOrPortalAdministrator',
  'PortalContentEditor',
  // Plausible policy-shaped names that are not registered at all.
  'PortalView',
  'PortalEdit',
  'ManageRoles',
  'RolesWrite',
  'ManageUsers',
  'UsersWrite',
  'SuperUser',
  'AuthenticatedUser',
  // The three permission codes, which scope a key rather than being one.
  'SYSTEM_FOLDER',
  'SYSTEM_MODULE_DEFINITION',
  'SYSTEM_TAB',
  // Role names, and the legacy administrator role in its actual plural spelling.
  'Administrators',
  'Registered Users',
  'All Users',
  'Unauthenticated Users',
] as const);

describe('HasPermissionDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let tokenStorage: TokenStorageService;
  let authStore: AuthStore;
  let httpMock: HttpTestingController;
  let router: jasmine.SpyObj<Pick<Router, 'navigate' | 'navigateByUrl'>>;

  /** The gated element, or null when the directive has removed it. */
  function guarded(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.guarded');
  }

  /**
   * Runs change detection and then drains every pending effect.
   *
   * The verdict is computed in an `effect()`, so settling has two halves and both are needed.
   * Change detection runs the directive's own view effect and is what puts the decision into
   * the document; `TestBed.flushEffects()` then drains the microtask and root effect
   * schedulers, so nothing a signal write scheduled is still queued when the expectation
   * runs. Every case goes through here rather than calling change detection directly, so no
   * expectation can accidentally assert against a view that had not settled.
   *
   * `flushEffects()` is the API this version of the framework publishes; there is no
   * `TestBed.tick()` to use instead, and no asynchronous work here to need one.
   */
  function settle(): void {
    fixture.detectChanges();
    TestBed.flushEffects();
  }

  /**
   * Renders the directive with a required key that bypassed the static type.
   *
   * Every expectation about an UNRECOGNISED required key runs through here rather than
   * through the well-typed host, because the well-typed host can no longer express one. The
   * fixture is built and settled in one call so the tests below read as a single statement
   * about the verdict.
   *
   * @param raw The value bound as the required key, unconstrained.
   * @returns The gated element, or null when the directive refused it.
   */
  function renderWidened(raw: string): HTMLElement | null {
    const widened = TestBed.createComponent(UntypedHostComponent);

    widened.componentInstance.raw = raw;
    widened.detectChanges();
    TestBed.flushEffects();

    return (widened.nativeElement as HTMLElement).querySelector('.guarded');
  }

  beforeEach(() => {
    // A double for the router, provided so the no-navigation guarantee can be asserted rather
    // than assumed. Nothing in the directive's dependency graph injects the router, so this
    // stands unused for the whole suite — which is precisely the expectation. Declared as a
    // `Pick` of the two members that would navigate, so the double cannot silently fall
    // behind the real surface and cannot pretend to implement members nobody checks.
    router = jasmine.createSpyObj<Pick<Router, 'navigate' | 'navigateByUrl'>>('Router', [
      'navigate',
      'navigateByUrl',
    ]);

    TestBed.configureTestingModule({
      // Standalone components are supplied through `imports`. There is no module declaration
      // anywhere in this workspace, so there is nothing for `declarations` to hold.
      imports: [HostComponent, UntypedHostComponent, LabelledUnitHostComponent],
      providers: [
        // ⚠ ORDER IS LOAD-BEARING. The real transport must be configured FIRST so the testing
        // backend registered after it wins; reversing the two leaves the live backend in place
        // and the suite would issue real requests while appearing to pass.
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: router },
      ],
    });

    tokenStorage = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HostComponent);
  });

  afterEach(() => {
    // Both halves of the no-HTTP guarantee, for every path exercised above rather than only
    // for initialisation. The positive expectation states it outright — no request matched
    // anything, which is what the always-true predicate asks — and the verification then
    // fails on any request that was issued and left unanswered. Either alone would let a
    // stray request through: a match-all expectation consumes nothing, and verification
    // alone reports only what remains outstanding.
    httpMock.expectNone(() => true);
    httpMock.verify();
  });

  describe('rendering', () => {
    it('renders nothing when nobody is signed in', () => {
      settle();

      expect(guarded()).toBeNull();
    });

    it('renders nothing when the session grants no keys', () => {
      tokenStorage.store(sessionWith([]));
      settle();

      expect(guarded()).toBeNull();
    });

    it('renders the content when the granted list contains the required key', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      expect(guarded()).not.toBeNull();
    });

    it('renders the content when the required key sits among several others', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ']));
      settle();

      expect(guarded()).not.toBeNull();
    });

    it('renders nothing when the granted list contains only other keys', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('removes the content rather than hiding it', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      settle();

      // Nothing is left behind to be un-hidden: no element, so no style to clear, no
      // node in the accessibility tree and nothing to receive focus.
      const host = fixture.nativeElement as HTMLElement;

      expect(host.querySelector('.guarded')).toBeNull();
      expect(host.querySelectorAll('button').length).toBe(0);
      expect(host.textContent).not.toContain('Edit');
    });
  });

  describe('key comparison', () => {
    it('admits the content only when the granted key is spelled exactly as required', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      expect(guarded()).not.toBeNull();
    });

    it('refuses a granted key spelled in a different case', () => {
      tokenStorage.store(sessionWith(['edit']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('refuses a granted key spelled in title case', () => {
      tokenStorage.store(sessionWith(['Edit']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('refuses a required key spelled in a different case from the granted one', () => {
      tokenStorage.store(sessionWith(['EDIT']));

      // Bound through the widened host, because `'edit'` is not a member of the closed
      // vocabulary and the well-typed host can no longer express it. That the compiler now
      // rejects it at the call site is the primary defence; this proves the run-time
      // refusal that backs it up.
      expect(renderWidened('edit')).toBeNull();
    });

    it('refuses a granted key padded with whitespace rather than trimming it', () => {
      tokenStorage.store(sessionWith([' EDIT ']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('refuses a granted key that merely contains the required one', () => {
      tokenStorage.store(sessionWith(['EDITOR']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('ignores granted values it does not recognise while honouring one it does', () => {
      tokenStorage.store(sessionWith(['SOMETHING_ELSE', 'EDIT']));
      settle();

      expect(guarded()).not.toBeNull();
    });
  });

  describe('denial', () => {
    it('refuses an unrecognised required key', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT']));

      expect(renderWidened('MANAGE')).toBeNull();
    });

    it('refuses the empty string rather than reading it as a wildcard', () => {
      // The legacy catalogue read an empty key as "any key". That wildcard is a
      // server-side query convenience and is deliberately not honoured here.
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE']));

      expect(renderWidened('')).toBeNull();
    });

    it('refuses the empty string even when the granted list is also empty', () => {
      tokenStorage.store(sessionWith([]));

      expect(renderWidened('')).toBeNull();
    });

    it('does not throw when the key is unrecognised, absent or blank', () => {
      // A denial must never take down the surrounding screen.
      expect(() => renderWidened('')).not.toThrow();
      expect(() => renderWidened('MANAGE')).not.toThrow();
    });

    // The regression this whole change exists to prevent. An authorisation POLICY name is a
    // different vocabulary from a persisted permission key, and a caller holding a policy
    // name in their granted list must not thereby be admitted to an element that asked for
    // one. Both halves are narrowed, so neither side can supply the match.
    it('refuses a policy name bound where a permission key belongs', () => {
      tokenStorage.store(sessionWith(['PortalAdministrator']));

      expect(renderWidened('PortalAdministrator')).toBeNull();
    });

    it('refuses a permission code bound where a permission key belongs', () => {
      tokenStorage.store(sessionWith(['SYSTEM_FOLDER']));

      expect(renderWidened('SYSTEM_FOLDER')).toBeNull();
    });

    // Fails closed on the required side specifically: the granted list here holds every real
    // key, so a naive membership test over unnarrowed strings would still refuse - but a
    // parser that COERCED the requirement (trimming, upper-casing) would admit it. This
    // pins the refusal.
    it('refuses a padded required key rather than trimming it into a match', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE']));

      expect(renderWidened(' EDIT')).toBeNull();
      expect(renderWidened('EDIT ')).toBeNull();
    });
  });

  describe('a host account', () => {
    it('is not admitted by the host flag alone, so no second rule lives on the client', () => {
      tokenStorage.store(sessionWith([], true));
      settle();

      expect(guarded()).toBeNull();
    });

    it('needs no client-side bypass, because the server issues it the whole catalogue', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE'], true));
      settle();

      expect(guarded()).not.toBeNull();
    });
  });

  describe('roles are not permissions', () => {
    it('is not admitted by a role name, even the administrator role', () => {
      // The identity below carries `roles: ['Administrators']` and no permission keys, so a
      // directive that consulted roles would admit this. Bound through the widened host,
      // because a role name is not a member of the permission vocabulary and the compiler
      // now says so at the call site.
      tokenStorage.store(sessionWith([]));

      expect(renderWidened('Administrators')).toBeNull();
    });

    it('is not admitted by a role name even when that name is also a granted value', () => {
      tokenStorage.store(sessionWith(['Administrators']));

      expect(renderWidened('Administrators')).toBeNull();
    });
  });

  describe('reacting to change', () => {
    it('withdraws the content when the session is cleared', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(guarded()).not.toBeNull();

      tokenStorage.clear();
      settle();

      expect(guarded()).toBeNull();
    });

    it('admits the content when a later session grants the key', () => {
      settle();
      expect(guarded()).toBeNull();

      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      expect(guarded()).not.toBeNull();
    });

    it('withdraws the content when a later session drops the key', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(guarded()).not.toBeNull();

      tokenStorage.store(sessionWith(['VIEW']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('re-evaluates when the required key changes on the same element', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      settle();
      expect(guarded()).toBeNull();

      fixture.componentInstance.required.set('VIEW');
      settle();

      expect(guarded()).not.toBeNull();
    });

    it('withdraws the content when the required key changes to one not held', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(guarded()).not.toBeNull();

      // The other direction on the same element. Both sources feed one effect, so a
      // re-evaluation triggered by the requirement must reach the same verdict machinery as one
      // triggered by the session.
      fixture.componentInstance.required.set('VIEW');
      settle();

      expect(guarded()).toBeNull();
    });

    it('re-evaluates on the requirement while the session stands unchanged', () => {
      tokenStorage.store(sessionWith(['VIEW', 'READ']));
      settle();
      expect(guarded()).toBeNull();

      fixture.componentInstance.required.set('READ');
      settle();
      expect(guarded()).not.toBeNull();

      fixture.componentInstance.required.set('WRITE');
      settle();
      expect(guarded()).toBeNull();
    });
  });

  describe('idempotence', () => {
    /** How many gated elements are present; more than one means the view was duplicated. */
    function guardedCount(): number {
      return (fixture.nativeElement as HTMLElement).querySelectorAll('.guarded').length;
    }

    it('keeps the same single element when a re-evaluation reaches the same conclusion', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      const first = guarded();
      expect(first).not.toBeNull();
      expect(guardedCount()).toBe(1);

      // A change that leaves the verdict untouched must not rebuild the view: a rebuilt
      // element would reset state inside it and move focus out of it.
      tokenStorage.store(sessionWith(['EDIT', 'VIEW']));
      settle();

      expect(guarded()).toBe(first);
      // Asserted as well as the identity above, and deliberately so. Creating a second
      // embedded view APPENDS it, leaving the original first in document order — so an
      // identity check alone still passes while the document quietly holds two copies of
      // the control. Only the count catches that.
      expect(guardedCount()).toBe(1);
    });

    it('does not accumulate copies across repeated grants of the same verdict', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      tokenStorage.store(sessionWith(['EDIT', 'VIEW']));
      settle();
      tokenStorage.store(sessionWith(['EDIT', 'VIEW', 'READ']));
      settle();
      tokenStorage.store(sessionWith(['EDIT', 'VIEW', 'READ', 'WRITE']));
      settle();

      expect(guardedCount()).toBe(1);
    });

    it('renders exactly one element after a denial is reversed', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      settle();
      expect(guardedCount()).toBe(0);

      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      expect(guardedCount()).toBe(1);
    });

    it('stays empty across a re-evaluation that still denies', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      settle();
      expect(guarded()).toBeNull();

      tokenStorage.store(sessionWith(['VIEW', 'READ']));
      settle();

      expect(guarded()).toBeNull();
      expect(guardedCount()).toBe(0);
    });
  });

  describe('the tenant key takes no part in the verdict', () => {
    // Both tenant numbers below are REAL keys in this schema and neither encodes absence. The
    // key column is an identity seeded at minus one, so the first provisioned site is numbered
    // minus one while the shipped default is zero — and the legacy null contract used minus
    // one as its own "missing integer" encoding, so the two meanings genuinely collide on this
    // data. A directive that filtered, defaulted or truthiness-tested a tenant key would
    // therefore behave differently on these two identical grants. It does not, because it
    // reads no tenant key at all.
    it('admits the content on the seed tenant exactly as on the default tenant', () => {
      tokenStorage.store(sessionWith(['EDIT'], false, SEED_TENANT));
      settle();

      expect(authStore.portalId()).toBe(SEED_TENANT);
      expect(guarded()).not.toBeNull();
    });

    it('refuses the content on the seed tenant when the key is absent', () => {
      tokenStorage.store(sessionWith(['VIEW'], false, SEED_TENANT));
      settle();

      expect(authStore.portalId()).toBe(SEED_TENANT);
      expect(guarded()).toBeNull();
    });

    it('reaches the same verdict on the default tenant, numbered zero', () => {
      tokenStorage.store(sessionWith(['EDIT'], false, DEFAULT_TENANT));
      settle();

      expect(authStore.portalId()).toBe(DEFAULT_TENANT);
      expect(guarded()).not.toBeNull();
    });
  });

  describe('an unresolved list is not the same fact as an empty one', () => {
    // Both deny, and that is the point — but they are DIFFERENT facts and the specification
    // says so rather than collapsing them into one truthiness check. "Nobody is signed in" is
    // an absent identity; "this caller holds nothing" is a resolved identity carrying an empty
    // list. Conflating them is how a directive ends up treating an empty array as "not loaded
    // yet" and rendering optimistically while it waits.
    it('denies while no identity is resolved, and reports the identity as absent', () => {
      settle();

      expect(authStore.currentUser()).toBeNull();
      expect(authStore.permissions().length).toBe(0);
      expect(guarded()).toBeNull();
    });

    it('denies on a resolved identity that holds no keys, which is a present answer', () => {
      tokenStorage.store(sessionWith([]));
      settle();

      expect(authStore.currentUser()).not.toBeNull();
      expect(authStore.permissions().length).toBe(0);
      expect(guarded()).toBeNull();
    });

    it('denies again once a resolved identity is withdrawn', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(authStore.currentUser()).not.toBeNull();

      tokenStorage.clear();
      settle();

      expect(authStore.currentUser()).toBeNull();
      expect(guarded()).toBeNull();
    });
  });

  describe('vocabulary confusion', () => {
    // The fail-OPEN defect this whole set exists to prevent. Three closed vocabularies live in
    // this system — the four persisted permission keys, the nine authorisation policy names
    // the API registers, and the permission codes that scope a key — plus role names, which
    // are a fourth namespace again. Only the first belongs on this input. A lenient
    // comparison, or a directive that consulted roles, would let one of the others collide
    // with a real grant and ADMIT content the server refuses.
    it('refuses every name drawn from another vocabulary as the required key', () => {
      // The caller holds every real key AND every impostor, so the granted side can never be
      // the reason for a refusal. Only the requirement's narrowing can be.
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE', ...NON_KEY_VOCABULARY]));

      const admitted = NON_KEY_VOCABULARY.filter(
        (candidate) => renderWidened(candidate) !== null,
      );

      expect(admitted).toEqual([]);
    });

    it('refuses to admit a real key on the strength of an impostor in the granted list', () => {
      // The mirror image: the requirement is a genuine key and the granted list holds only
      // values from the other vocabularies. Nothing there may satisfy it.
      tokenStorage.store(sessionWith(NON_KEY_VOCABULARY));
      settle();

      expect(guarded()).toBeNull();
    });

    it('is not admitted by a role name even when the granted list carries that role', () => {
      // The identity fixture carries the administrator role — spelled in the plural, as the
      // legacy provisioning code spells it — and this grants the same string as a permission.
      // A directive that mapped roles onto keys would admit it; roles and keys are different
      // namespaces and the mapping between them belongs to the server.
      tokenStorage.store(sessionWith(['Administrators']));

      expect(renderWidened('Administrators')).toBeNull();
      expect(authStore.roles()).toContain('Administrators');
    });
  });

  describe('encodings that are deliberately not carried forward', () => {
    /*
     * ⚠ THE JUSTIFICATION HERE IS THE OPPOSITE OF WHAT A CASUAL SEARCH SUGGESTS, so it is
     * written down rather than left to be rediscovered.
     *
     * A NEGATION PREFIX genuinely does not exist in this generation of the product. Searching
     * the legacy security tree for a leading-bang test, for a prefix-stripping substring call,
     * for an inverted role test and for the words themselves all return nothing. The legacy
     * grant mechanism was a boolean column on the permission row, honoured by the projection
     * functions. So a key beginning with a bang is not an inversion — it is simply an
     * unrecognised key, and it must deny rather than invert.
     *
     * A SEPARATOR-JOINED LIST AND A BRACKETED IDENTIFIER, by contrast, DO exist in the legacy
     * tree. The role argument was a semicolon-delimited string, split on that separator by the
     * server-side role test; the permission projections built it by appending a semicolon per
     * role; and the innermost role primitive additionally matched a bracketed account
     * identifier as a pseudo-role. None of it is carried forward, because the server now
     * flattens the answer into a plain array of keys before the client sees anything. The
     * expectation is therefore unchanged — each is an ordinary unrecognised key — but it holds
     * because the encoding was RETIRED, not because it never existed.
     */
    it('treats a negated key as an ordinary unrecognised key rather than an inversion', () => {
      // The caller does NOT hold the key, so an inverting implementation would render here.
      tokenStorage.store(sessionWith(['VIEW']));

      expect(renderWidened('!EDIT')).toBeNull();
    });

    it('does not invert a negated key that the caller does hold either', () => {
      tokenStorage.store(sessionWith(['EDIT']));

      expect(renderWidened('!EDIT')).toBeNull();
    });

    it('does not split a separator-joined requirement into keys', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE']));

      expect(renderWidened('EDIT;VIEW')).toBeNull();
      expect(renderWidened('EDIT,VIEW')).toBeNull();
      expect(renderWidened('EDIT VIEW')).toBeNull();
    });

    it('does not split a separator-joined granted value into keys', () => {
      tokenStorage.store(sessionWith(['EDIT;VIEW']));
      settle();

      expect(guarded()).toBeNull();
    });

    it('does not parse a bracketed identifier as a pseudo-grant', () => {
      // The account in the fixture is numbered zero, so the bracketed form of its own
      // identifier is included: a surviving pseudo-role parse would match precisely that.
      tokenStorage.store(sessionWith(['EDIT'], false, SEED_TENANT));

      expect(renderWidened('[0]')).toBeNull();
      expect(renderWidened('[42]')).toBeNull();
      expect(renderWidened('[EDIT]')).toBeNull();
    });

    it('does not parse a bracketed granted value as a grant of the key it contains', () => {
      tokenStorage.store(sessionWith(['[EDIT]', '[0]']));
      settle();

      expect(guarded()).toBeNull();
    });
  });

  describe('guarantees', () => {
    /*
     * The two architectural properties, asserted rather than described. Both are invisible in
     * the rendered output, and both were REAL behaviours of the code being replaced: the legacy
     * page base ran an own-record test, a host-account test and an administrator-role test,
     * ISSUED A DATABASE QUERY and could REDIRECT to the access-denied page — all from inside a
     * property getter, so merely reading a property could navigate. Neither behaviour is
     * reproduced, and these expectations are what keep it that way.
     */
    it('issues no request while granting, denying and re-granting', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(guarded()).not.toBeNull();

      tokenStorage.store(sessionWith(['VIEW']));
      settle();
      expect(guarded()).toBeNull();

      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(guarded()).not.toBeNull();

      // Stated here as well as in the shared teardown, so this case fails on the spot rather
      // than in an unrelated-looking verification if the directive ever starts fetching.
      httpMock.expectNone(() => true);
    });

    it('issues no request when the required key is refused', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE']));

      expect(renderWidened('MANAGE')).toBeNull();

      // A refusal must not trigger a lookup to double-check itself. The client holds an
      // already-resolved answer; asking again would put a second authority on the client.
      httpMock.expectNone(() => true);
    });

    it('performs no navigation while granting, denying and re-granting', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      tokenStorage.store(sessionWith(['VIEW']));
      settle();

      tokenStorage.store(sessionWith(['EDIT']));
      settle();

      expect(router.navigate).not.toHaveBeenCalled();
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });

    it('performs no navigation when the required key is refused', () => {
      // The legacy analogue redirected to an access-denied page at exactly this point. Hiding
      // an affordance is not a routing decision, and route-level gating is a separate concern
      // owned by the route gate.
      tokenStorage.store(sessionWith([]));

      expect(renderWidened('MANAGE')).toBeNull();
      expect(renderWidened('')).toBeNull();

      expect(router.navigate).not.toHaveBeenCalled();
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });

    it('performs no navigation when the session is withdrawn beneath it', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      settle();
      expect(guarded()).not.toBeNull();

      tokenStorage.clear();
      settle();

      expect(guarded()).toBeNull();
      expect(router.navigate).not.toHaveBeenCalled();
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });
  });

  describe('the permission projection it reads is read-only', () => {
    // Closes the loop on the claim that this directive decides nothing and owns nothing. The
    // store publishes the list as a derived, read-only signal, so neither the directive nor a
    // consumer can write a grant into it — which matters because a writable permission list on
    // the client would be a permission list an attacker could widen. Proven by the ABSENCE of
    // the two mutating members a writable signal carries, rather than by casting the signal to
    // a writable type and attempting a call, which would prove only that the cast compiled.
    it('publishes the permission list without a setter or an updater', () => {
      expect('set' in authStore.permissions).toBe(false);
      expect('update' in authStore.permissions).toBe(false);
    });

    it('publishes the identity and role projections the same way', () => {
      expect('set' in authStore.currentUser).toBe(false);
      expect('update' in authStore.currentUser).toBe(false);
      expect('set' in authStore.roles).toBe(false);
      expect('update' in authStore.roles).toBe(false);
    });

    it('still answers the list as a readable signal', () => {
      tokenStorage.store(sessionWith(['EDIT', 'VIEW']));

      expect(authStore.permissions()).toEqual(['EDIT', 'VIEW']);
    });
  });

  describe('a denial removes the whole labelled unit', () => {
    /**
     * Renders the labelled-unit host with a given requirement and returns its root element.
     *
     * @param required The key the unit asks for.
     * @returns The host's root element, settled.
     */
    function renderLabelledUnit(required: PermissionKey): HTMLElement {
      const labelled = TestBed.createComponent(LabelledUnitHostComponent);

      labelled.componentInstance.required = required;
      labelled.detectChanges();
      TestBed.flushEffects();

      return labelled.nativeElement as HTMLElement;
    }

    it('renders the unit intact, with every identifier reference resolving', () => {
      tokenStorage.store(sessionWith(['EDIT']));

      const host = renderLabelledUnit('EDIT');

      expect(host.querySelector('.labelled-unit')).not.toBeNull();
      expect(host.querySelector('label.unit-label')).not.toBeNull();
      expect(host.querySelector('input.unit-control')).not.toBeNull();
      // The positive control for the check below: while the unit is present, every reference it
      // carries resolves. Without this, an assertion that nothing dangles after removal would
      // pass just as happily against a fixture that never referenced anything.
      expect(danglingReferences(host)).toEqual([]);
    });

    it('removes the label together with the control it names', () => {
      tokenStorage.store(sessionWith(['VIEW']));

      const host = renderLabelledUnit('EDIT');

      // The label goes with the control. A label left behind would announce a field that is
      // not there and, in most browsers, would still take focus when clicked.
      expect(host.querySelector('.labelled-unit')).toBeNull();
      expect(host.querySelectorAll('label').length).toBe(0);
      expect(host.querySelectorAll('input').length).toBe(0);
    });

    it('leaves no identifier reference pointing at something that has gone', () => {
      tokenStorage.store(sessionWith(['VIEW']));

      const host = renderLabelledUnit('EDIT');

      // Nothing referencing survives at all, so nothing CAN dangle: no `for`, and none of the
      // four identifier-valued accessibility relationships.
      expect(danglingReferences(host)).toEqual([]);

      for (const attribute of REFERENCING_ATTRIBUTES) {
        expect(host.querySelectorAll(`[${attribute}]`).length).toBe(0);
      }
    });

    it('removes only the gated unit and leaves its surroundings untouched', () => {
      tokenStorage.store(sessionWith(['VIEW']));

      const host = renderLabelledUnit('EDIT');
      const outside = host.querySelector('.outside');

      expect(outside).not.toBeNull();
      // Read as text, never as markup. Legacy wording is untrusted — the administration
      // resource files carry embedded markup, including script elements — and the legacy
      // access-denied screen itself encoded its message before displaying it.
      expect(host.textContent).toContain('Nothing here is gated.');
      expect(host.textContent).not.toContain('Shown in the browser title bar.');
    });

    it('restores the intact unit when the key is later granted', () => {
      tokenStorage.store(sessionWith(['EDIT']));

      const host = renderLabelledUnit('EDIT');

      expect(host.querySelector('input.unit-control')).not.toBeNull();
      expect(host.querySelector('label.unit-label')).not.toBeNull();
      expect(danglingReferences(host)).toEqual([]);
    });
  });
});
