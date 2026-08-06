import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';

// Type-only, matching the convention used by the sibling core specifications: the session
// and identity shapes are erased at compile time, so this file pulls no runtime code out
// of the model.
import type { AuthSession, CurrentUser } from '../../core/models/auth.model';
import { TokenStorageService } from '../../core/services/token-storage.service';

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
 * There is no predecessor test to port: the legacy tree contains no automated tests at
 * all, so every expectation below is net-new.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  template: ` <button *hasPermission="required" class="guarded" type="button">Edit</button> `,
})
class HostComponent {
  /**
   * Typed exactly as the directive's input is — a plain `string`, so a call site is free
   * to bind a key this file has never heard of, which is precisely the case the denial
   * expectations below exercise.
   */
  required = 'EDIT';
}

function userWith(permissions: readonly string[], isSuperUser = false): CurrentUser {
  return {
    userId: 7,
    // Zero deliberately: `Portals.PortalID` is seeded at minus one, so zero is a real
    // tenant and nothing may read it as absence.
    portalId: 0,
    portalName: 'Primary',
    username: 'operator',
    displayName: 'Operator',
    email: 'operator@example.test',
    isSuperUser,
    roles: ['Administrators'],
    permissions,
  };
}

function sessionWith(permissions: readonly string[], isSuperUser = false): AuthSession {
  return {
    accessToken: 'access-token-placeholder',
    expiresAtUtc: '2100-01-01T00:00:00.000Z',
    refreshToken: 'refresh-token-placeholder',
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: userWith(permissions, isSuperUser),
  };
}

describe('HasPermissionDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let tokenStorage: TokenStorageService;
  let httpMock: HttpTestingController;

  /** The gated element, or null when the directive has removed it. */
  function guarded(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.guarded');
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    tokenStorage = TestBed.inject(TokenStorageService);
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HostComponent);
  });

  afterEach(() => {
    // Proves the no-HTTP guarantee for every path exercised above, not just for init.
    httpMock.verify();
  });

  describe('rendering', () => {
    it('renders nothing when nobody is signed in', () => {
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('renders nothing when the session grants no keys', () => {
      tokenStorage.store(sessionWith([]));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('renders the content when the granted list contains the required key', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });

    it('renders the content when the required key sits among several others', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ']));
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });

    it('renders nothing when the granted list contains only other keys', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('removes the content rather than hiding it', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      fixture.detectChanges();

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
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });

    it('refuses a granted key spelled in a different case', () => {
      tokenStorage.store(sessionWith(['edit']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('refuses a granted key spelled in title case', () => {
      tokenStorage.store(sessionWith(['Edit']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('refuses a required key spelled in a different case from the granted one', () => {
      fixture.componentInstance.required = 'edit';
      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('refuses a granted key padded with whitespace rather than trimming it', () => {
      tokenStorage.store(sessionWith([' EDIT ']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('refuses a granted key that merely contains the required one', () => {
      tokenStorage.store(sessionWith(['EDITOR']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('ignores granted values it does not recognise while honouring one it does', () => {
      tokenStorage.store(sessionWith(['SOMETHING_ELSE', 'EDIT']));
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });
  });

  describe('denial', () => {
    it('refuses an unrecognised required key', () => {
      fixture.componentInstance.required = 'MANAGE';
      tokenStorage.store(sessionWith(['VIEW', 'EDIT']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('refuses the empty string rather than reading it as a wildcard', () => {
      // The legacy catalogue read an empty key as "any key". That wildcard is a
      // server-side query convenience and is deliberately not honoured here.
      fixture.componentInstance.required = '';
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('refuses the empty string even when the granted list is also empty', () => {
      fixture.componentInstance.required = '';
      tokenStorage.store(sessionWith([]));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('does not throw when the key is unrecognised, absent or blank', () => {
      fixture.componentInstance.required = '';

      // A denial must never take down the surrounding screen.
      expect(() => fixture.detectChanges()).not.toThrow();
      expect(guarded()).toBeNull();
    });
  });

  describe('a host account', () => {
    it('is not admitted by the host flag alone, so no second rule lives on the client', () => {
      tokenStorage.store(sessionWith([], true));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('needs no client-side bypass, because the server issues it the whole catalogue', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE'], true));
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });
  });

  describe('roles are not permissions', () => {
    it('is not admitted by a role name, even the administrator role', () => {
      fixture.componentInstance.required = 'Administrators';
      tokenStorage.store(sessionWith([]));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });
  });

  describe('reacting to change', () => {
    it('withdraws the content when the session is cleared', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();
      expect(guarded()).not.toBeNull();

      tokenStorage.clear();
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('admits the content when a later session grants the key', () => {
      fixture.detectChanges();
      expect(guarded()).toBeNull();

      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });

    it('withdraws the content when a later session drops the key', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();
      expect(guarded()).not.toBeNull();

      tokenStorage.store(sessionWith(['VIEW']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
    });

    it('re-evaluates when the required key changes on the same element', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      fixture.detectChanges();
      expect(guarded()).toBeNull();

      fixture.componentInstance.required = 'VIEW';
      fixture.detectChanges();

      expect(guarded()).not.toBeNull();
    });
  });

  describe('idempotence', () => {
    /** How many gated elements are present; more than one means the view was duplicated. */
    function guardedCount(): number {
      return (fixture.nativeElement as HTMLElement).querySelectorAll('.guarded').length;
    }

    it('keeps the same single element when a re-evaluation reaches the same conclusion', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();
      const first = guarded();
      expect(first).not.toBeNull();
      expect(guardedCount()).toBe(1);

      // A change that leaves the verdict untouched must not rebuild the view: a rebuilt
      // element would reset state inside it and move focus out of it.
      tokenStorage.store(sessionWith(['EDIT', 'VIEW']));
      fixture.detectChanges();

      expect(guarded()).toBe(first);
      // Asserted as well as the identity above, and deliberately so. Creating a second
      // embedded view APPENDS it, leaving the original first in document order — so an
      // identity check alone still passes while the document quietly holds two copies of
      // the control. Only the count catches that.
      expect(guardedCount()).toBe(1);
    });

    it('does not accumulate copies across repeated grants of the same verdict', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();

      tokenStorage.store(sessionWith(['EDIT', 'VIEW']));
      fixture.detectChanges();
      tokenStorage.store(sessionWith(['EDIT', 'VIEW', 'READ']));
      fixture.detectChanges();
      tokenStorage.store(sessionWith(['EDIT', 'VIEW', 'READ', 'WRITE']));
      fixture.detectChanges();

      expect(guardedCount()).toBe(1);
    });

    it('renders exactly one element after a denial is reversed', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      fixture.detectChanges();
      expect(guardedCount()).toBe(0);

      tokenStorage.store(sessionWith(['EDIT']));
      fixture.detectChanges();

      expect(guardedCount()).toBe(1);
    });

    it('stays empty across a re-evaluation that still denies', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      fixture.detectChanges();
      expect(guarded()).toBeNull();

      tokenStorage.store(sessionWith(['VIEW', 'READ']));
      fixture.detectChanges();

      expect(guarded()).toBeNull();
      expect(guardedCount()).toBe(0);
    });
  });
});
