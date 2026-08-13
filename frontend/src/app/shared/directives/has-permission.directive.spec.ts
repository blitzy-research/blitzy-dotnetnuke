import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
// The router is imported for its INJECTION TOKEN alone, so a double can stand in its place and be proven
// untouched.
import { Router } from '@angular/router';

// Type-only, matching the convention used by the sibling core specifications: the session and identity
// shapes are erased at compile time, so this file pulls no runtime code out of the model.
import type { AuthSession, CurrentUser } from '../../core/models/auth.model';
import type { PermissionKey } from '../../core/models/permission.model';
import { TokenStorageService } from '../../core/services/token-storage.service';
import { AuthStore } from '../../core/state/auth.store';

import { HasPermissionDirective } from './has-permission.directive';

/**
 * Specification for {@link HasPermissionDirective}. ## What is under test The directive against the REAL
 * auth store, driven by storing and clearing a real session through the token custodian.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: ` <button *hasPermission="required()" class="guarded" type="button">Edit</button> `,
})
class HostComponent {
  /**
   * Typed exactly as the directive's input is — {@link PermissionKey}, the closed vocabulary, rather than
   * a plain `string`. The narrowing is the point of the design under test.
   */
  readonly required = signal<PermissionKey>('EDIT');
}

/** A host that binds the directive through a deliberately WIDENED expression. */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: ` <button *hasPermission="widened" class="guarded" type="button">Edit</button> `,
})
class UntypedHostComponent {
  /** The value actually bound, held as an open string. */
  raw = 'EDIT';

  /** The widened binding. */
  get widened(): PermissionKey {
    return this.raw as PermissionKey;
  }
}

/**
 * A host that gates a COMPLETE LABELLED UNIT rather than a bare control. This is the arrangement the
 * directive's own guidance prescribes, and it exists here to prove the accessibility consequence of
 * removal rather than concealment.
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
 * The tenant key an identity fixture is issued against. ⚠ BOTH VALUES ARE REAL TENANTS AND NEITHER MEANS
 * "ABSENT". The tenant table's key column is an identity seeded at MINUS ONE, so the first site
 * provisioned is numbered minus one and the shipped default site is numbered zero.
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
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: userWith(permissions, isSuperUser, portalId),
  };
}

/** Attribute names whose value is an element identifier, or a list of them. */
const REFERENCING_ATTRIBUTES = Object.freeze([
  'for',
  'aria-labelledby',
  'aria-describedby',
  'aria-controls',
  'aria-owns',
] as const);

/**
 * Identifier references inside a subtree that no longer resolve to an element. Returns a describable list
 * rather than a boolean so a failure names the offending attribute and identifier instead of merely
 * reporting that something is wrong.
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
 * Strings that are NOT permission keys but are plausible enough to be written by mistake. Three distinct
 * vocabularies exist in this system and only one of them belongs on this input.
 */
const NON_KEY_VOCABULARY = Object.freeze([
  // The nine registered authorisation policy names.
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

  /** Runs change detection and then drains every pending effect. */
  function settle(): void {
    fixture.detectChanges();
    TestBed.flushEffects();
  }

  /**
   * Renders the directive with a required key that bypassed the static type. Every expectation about an
   * UNRECOGNISED required key runs through here rather than through the well-typed host, because the
   * well-typed host can no longer express one.
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
    // A double for the router, provided so the no-navigation guarantee can be asserted rather than assumed.
    // Nothing in the directive's dependency graph injects the router, so this stands unused for the whole
    // suite — which is precisely the expectation.
    router = jasmine.createSpyObj<Pick<Router, 'navigate' | 'navigateByUrl'>>('Router', [
      'navigate',
      'navigateByUrl',
    ]);

    TestBed.configureTestingModule({
      // Standalone components are supplied through `imports`. There is no module declaration
      // anywhere in this workspace, so there is nothing for `declarations` to hold.
      imports: [HostComponent, UntypedHostComponent, LabelledUnitHostComponent],
      providers: [
        // ⚠ ORDER IS LOAD-BEARING. The real transport must be configured FIRST so the testing backend
        // registered after it wins; reversing the two leaves the live backend in place and the suite would
        // issue real requests while appearing to pass.
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

      // Bound through the widened host, because `'edit'` is not a member of the closed vocabulary and the
      // well-typed host can no longer express it. That the compiler now rejects it at the call site is the
      // primary defence; this proves the run-time refusal that backs it up.
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

    // The regression this whole change exists to prevent. An authorisation POLICY name is a different
    // vocabulary from a persisted permission key, and a caller holding a policy name in their granted list
    // must not thereby be admitted to an element that asked for one.
    it('refuses a policy name bound where a permission key belongs', () => {
      tokenStorage.store(sessionWith(['PortalAdministrator']));

      expect(renderWidened('PortalAdministrator')).toBeNull();
    });

    it('refuses a permission code bound where a permission key belongs', () => {
      tokenStorage.store(sessionWith(['SYSTEM_FOLDER']));

      expect(renderWidened('SYSTEM_FOLDER')).toBeNull();
    });

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
      // The identity below carries `roles: ['Administrators']` and no permission keys, so a directive that
      // consulted roles would admit this. Bound through the widened host, because a role name is not a
      // member of the permission vocabulary and the compiler now says so at the call site.
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
      // Asserted as well as the identity above, and deliberately so. Creating a second embedded view
      // APPENDS it, leaving the original first in document order — so an identity check alone still passes
      // while the document quietly holds two copies of the control.
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
    // Both tenant numbers below are REAL keys in this schema and neither encodes absence.
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
    // Both deny, and that is the point — but they are DIFFERENT facts and the specification says so rather
    // than collapsing them into one truthiness check. "Nobody is signed in" is an absent identity; "this
    // caller holds nothing" is a resolved identity carrying an empty list.
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
    // The fail-OPEN defect this whole set exists to prevent. Three closed vocabularies live in this system
    // — the four persisted permission keys, the nine authorisation policy names the API registers, and the
    // permission codes that scope a key — plus role names, which are a fourth namespace again.
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
      // The identity fixture carries the administrator role — spelled in the plural, as the legacy
      // provisioning code spells it — and this grants the same string as a permission.
      tokenStorage.store(sessionWith(['Administrators']));

      expect(renderWidened('Administrators')).toBeNull();
      expect(authStore.roles()).toContain('Administrators');
    });
  });

  describe('encodings that are deliberately not carried forward', () => {
    // A NEGATION PREFIX genuinely does not exist in this generation of the product. Searching the legacy
    // security tree for a leading-bang test, for a prefix-stripping substring call, for an inverted role
    // test and for the words themselves all return nothing.
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
    // Closes the loop on the claim that this directive decides nothing and owns nothing.
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
      // The positive control for the check below: while the unit is present, every reference it carries
      // resolves. Without this, an assertion that nothing dangles after removal would pass just as happily
      // against a fixture that never referenced anything.
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
      // Read as text, never as markup. Legacy wording is untrusted — the administration resource files
      // carry embedded markup, including script elements — and the legacy access-denied screen itself
      // encoded its message before displaying it.
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
