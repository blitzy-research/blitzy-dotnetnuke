import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AuthSession, CurrentUser } from '../../core/models/auth.model';
import { TokenStorageService } from '../../core/services/token-storage.service';
import { HasPermissionDirective, isPermitted, normaliseRequiredKeys } from './has-permission.directive';

/**
 * A host for the structural directive.
 *
 * Default change detection deliberately, so the bound specification can be changed by
 * assigning the field and then flushing — which is what a feature screen does when it
 * swaps the required key on the same element.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  template: `
    <button *appHasPermission="required" class="guarded" type="button">Edit</button>
  `,
})
class HostComponent {
  required: string | readonly string[] | null | undefined = 'EDIT';
}

function userWith(permissions: readonly string[], isSuperUser = false): CurrentUser {
  return {
    userId: 7,
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
    accessToken: 'access-1',
    expiresAtUtc: '2100-01-01T00:00:00.000Z',
    refreshToken: 'refresh-1',
    mustChangePassword: false,
    passwordExpiring: false,
    user: userWith(permissions, isSuperUser),
  };
}

describe('HasPermissionDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let tokenStorage: TokenStorageService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    tokenStorage = TestBed.inject(TokenStorageService);
  });

  /** Flushes the binding and the directive's effect together. */
  function sync(): void {
    fixture.detectChanges();
  }

  function guarded(): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector('.guarded');
  }

  describe('rendering', () => {
    it('renders nothing when there is no session', () => {
      sync();

      expect(guarded()).withContext('an unauthenticated caller holds no keys').toBeNull();
    });

    it('renders nothing when the session grants no keys', () => {
      // The server issues an empty key list rather than refusing the sign-in when it
      // cannot resolve one, so this is a real state and not a defensive branch.
      tokenStorage.store(sessionWith([]));
      sync();

      expect(guarded()).toBeNull();
    });

    it('renders the content when the granted list contains the required key', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      sync();

      expect(guarded()).not.toBeNull();
      expect(guarded()!.textContent!.trim()).toBe('Edit');
    });

    it('renders nothing when the granted list contains only other keys', () => {
      tokenStorage.store(sessionWith(['VIEW', 'DEPLOY']));
      sync();

      expect(guarded()).toBeNull();
    });

    it('removes the content rather than hiding it', () => {
      // A hidden control is still in the document, still reachable by a script that
      // clears the style, and depending on how it was hidden still in the accessibility
      // tree - none of which is what "you cannot do this" should mean.
      tokenStorage.store(sessionWith(['VIEW']));
      sync();

      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelectorAll('button').length).toBe(0);
      expect(root.innerHTML).not.toContain('hidden');
    });
  });

  describe('key comparison', () => {
    it('matches a template key spelled in a different case', () => {
      host.required = 'edit';
      tokenStorage.store(sessionWith(['EDIT']));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('matches a granted key spelled in a different case', () => {
      host.required = 'EDIT';
      tokenStorage.store(sessionWith(['edit']));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('ignores surrounding whitespace on both sides', () => {
      host.required = '  EDIT  ';
      tokenStorage.store(sessionWith([' edit ']));
      sync();

      expect(guarded()).not.toBeNull();
    });
  });

  describe('list semantics', () => {
    it('admits the content when ANY ONE of the listed keys is held', () => {
      // "All" is expressible by nesting the directive; "any" is not expressible from
      // "all", which is why the list means any.
      host.required = ['EDIT', 'MANAGE'];
      tokenStorage.store(sessionWith(['MANAGE']));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('denies the content when none of the listed keys is held', () => {
      host.required = ['EDIT', 'MANAGE'];
      tokenStorage.store(sessionWith(['VIEW']));
      sync();

      expect(guarded()).toBeNull();
    });
  });

  describe('reacting to change', () => {
    it('withdraws the content when the session is cleared', () => {
      tokenStorage.store(sessionWith(['EDIT']));
      sync();
      expect(guarded()).not.toBeNull();

      tokenStorage.clear();
      sync();

      expect(guarded()).withContext('sign-out withdraws the affordance immediately').toBeNull();
    });

    it('admits the content when a later session grants the key', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      sync();
      expect(guarded()).toBeNull();

      tokenStorage.store(sessionWith(['VIEW', 'EDIT']));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('re-evaluates when the required key changes on the same element', () => {
      tokenStorage.store(sessionWith(['VIEW']));
      host.required = 'EDIT';
      sync();
      expect(guarded()).toBeNull();

      host.required = 'VIEW';
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('keeps the same content when a re-evaluation reaches the same conclusion', () => {
      // Rebuilding the view would reset any state inside it and move focus out of it.
      tokenStorage.store(sessionWith(['EDIT']));
      sync();
      const before = guarded();

      tokenStorage.store(sessionWith(['EDIT', 'MANAGE']));
      sync();

      expect(guarded()).toBe(before);
    });
  });

  describe('a host account', () => {
    it('needs no client-side bypass, because the server grants it the whole catalogue', () => {
      tokenStorage.store(sessionWith(['EDIT', 'VIEW', 'DEPLOY'], true));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('is not admitted by the host flag alone, so the client keeps no second rule', () => {
      // A bypass here would be a divergent rule that had to be kept in step with the
      // server's own; the server already returns every key for a host account.
      tokenStorage.store(sessionWith([], true));
      sync();

      expect(guarded()).toBeNull();
    });
  });

  describe('a blank specification', () => {
    it('throws rather than quietly denying', () => {
      host.required = '';

      expect(() => sync()).toThrowError(/at least one non-blank permission key/);
    });

    it('throws for whitespace only', () => {
      host.required = '   ';

      expect(() => sync()).toThrowError(/at least one non-blank permission key/);
    });

    it('throws for an empty list', () => {
      host.required = [];

      expect(() => sync()).toThrowError(/at least one non-blank permission key/);
    });

    it('throws for an absent binding', () => {
      host.required = null;

      expect(() => sync()).toThrowError(/at least one non-blank permission key/);
    });
  });
});

describe('normaliseRequiredKeys', () => {
  it('wraps a single key', () => {
    expect(normaliseRequiredKeys('EDIT')).toEqual(['edit']);
  });

  it('lower-cases and trims every key', () => {
    expect(normaliseRequiredKeys([' Edit ', 'MANAGE'])).toEqual(['edit', 'manage']);
  });

  it('drops blank entries while keeping the rest', () => {
    expect(normaliseRequiredKeys(['EDIT', '  ', ''])).toEqual(['edit']);
  });

  it('throws when nothing usable remains', () => {
    expect(() => {
      normaliseRequiredKeys([' ', '']);
    }).toThrowError(/at least one non-blank permission key/);
  });

  it('throws for undefined', () => {
    expect(() => {
      normaliseRequiredKeys(undefined);
    }).toThrowError(/at least one non-blank permission key/);
  });
});

describe('isPermitted', () => {
  it('is satisfied by one key out of several', () => {
    expect(isPermitted(['edit', 'manage'], ['MANAGE'])).toBeTrue();
  });

  it('is not satisfied by an unrelated key', () => {
    expect(isPermitted(['edit'], ['view'])).toBeFalse();
  });

  it('is never satisfied by an empty granted set', () => {
    expect(isPermitted(['edit'], [])).toBeFalse();
  });

  it('is never satisfied by an empty required set', () => {
    expect(isPermitted([], ['edit'])).toBeFalse();
  });

  it('compares the granted side case-insensitively and ignoring whitespace', () => {
    expect(isPermitted(['edit'], [' EDIT '])).toBeTrue();
  });
});
