import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AuthSession, CurrentUser } from '../../core/models/auth.model';
import type { PermissionKey } from '../../core/models/permission.model';
import { TokenStorageService } from '../../core/services/token-storage.service';
import { HasPermissionDirective, isPermitted, normaliseRequiredKeys } from './has-permission.directive';

/**
 * A host for the structural directive.
 *
 * Default change detection deliberately, so the bound specification can be changed by
 * assigning the field and then flushing — which is what a feature screen does when it
 * swaps the required key on the same element.
 *
 * The bound field is typed exactly as the directive's input is, which is itself part of
 * what these specs assert: a differently cased key, an invented key or a blank string is
 * NOT ASSIGNABLE here, so those mistakes cannot reach a template at all. They are proven
 * refused at the runtime boundary instead, against `normaliseRequiredKeys` further down,
 * which is where an untyped caller would arrive.
 */
@Component({
  standalone: true,
  imports: [HasPermissionDirective],
  template: `
    <button *appHasPermission="required" class="guarded" type="button">Edit</button>
  `,
})
class HostComponent {
  required: PermissionKey | readonly PermissionKey[] | null | undefined = 'EDIT';
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
    mustUpdateProfile: false,
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
      tokenStorage.store(sessionWith(['VIEW', 'READ']));
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
    // Case-folding either side would fail OPEN: an unrecognised key that happened to
    // lower-case onto a held one would admit the content, and a misspelling would be
    // indistinguishable from a real grant. The server and the database compare these keys
    // with ordinal equality, so exact comparison is the faithful behaviour as well as the
    // safe one. Each spec below would have passed with the opposite expectation under a
    // lenient comparison, which is exactly why they are worth pinning.

    it('matches only when the granted key is spelled exactly as required', () => {
      host.required = 'EDIT';
      tokenStorage.store(sessionWith(['EDIT']));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('refuses a granted key spelled in a different case', () => {
      host.required = 'EDIT';
      tokenStorage.store(sessionWith(['edit']));
      sync();

      expect(guarded())
        .withContext('a lower-cased grant is not the EDIT key and must not admit content')
        .toBeNull();
    });

    it('refuses a granted key padded with whitespace', () => {
      host.required = 'EDIT';
      tokenStorage.store(sessionWith([' EDIT ']));
      sync();

      expect(guarded())
        .withContext('the server sends trimmed keys; a padded one is not a match')
        .toBeNull();
    });

    it('refuses a granted key that merely contains the required one', () => {
      host.required = 'EDIT';
      tokenStorage.store(sessionWith(['EDITOR', 'NOEDIT']));
      sync();

      expect(guarded()).toBeNull();
    });
  });

  describe('list semantics', () => {
    it('admits the content when ANY ONE of the listed keys is held', () => {
      // "All" is expressible by nesting the directive; "any" is not expressible from
      // "all", which is why the list means any.
      host.required = ['EDIT', 'VIEW'];
      tokenStorage.store(sessionWith(['VIEW']));
      sync();

      expect(guarded()).not.toBeNull();
    });

    it('denies the content when none of the listed keys is held', () => {
      host.required = ['EDIT', 'VIEW'];
      tokenStorage.store(sessionWith(['READ']));
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

      tokenStorage.store(sessionWith(['EDIT', 'VIEW']));
      sync();

      expect(guarded()).toBe(before);
    });
  });

  describe('a host account', () => {
    it('needs no client-side bypass, because the server grants it the whole catalogue', () => {
      tokenStorage.store(sessionWith(['VIEW', 'EDIT', 'READ', 'WRITE'], true));
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

  describe('an absent specification', () => {
    // A blank or whitespace-only string is not assignable to the input's type at all, so
    // it cannot reach a template and is proven refused at the runtime boundary instead —
    // see the normaliseRequiredKeys specs. What remains bindable is an empty list and an
    // absent binding, and both must fail loudly rather than hide the control: a hidden
    // control looks exactly like a correctly denied permission, which is the hardest
    // version of this mistake to find.

    it('throws for an empty list', () => {
      host.required = [];

      expect(() => sync()).toThrowError(/at least one permission key is required/);
    });

    it('throws for an absent binding', () => {
      host.required = null;

      expect(() => sync()).toThrowError(/at least one permission key is required/);
    });

    it('throws for an undefined binding', () => {
      host.required = undefined;

      expect(() => sync()).toThrowError(/at least one permission key is required/);
    });
  });
});

describe('normaliseRequiredKeys', () => {
  // This is the runtime boundary behind the directive's typed input. It takes `unknown`
  // on purpose, so these specs can supply exactly the values a typed template cannot —
  // which is what an untyped or JavaScript caller would arrive with. Every one of them
  // must be refused, and none of them may be quietly adjusted into a match.

  it('wraps a single key without altering it', () => {
    expect(normaliseRequiredKeys('EDIT')).toEqual(['EDIT']);
  });

  it('preserves every recognised key, in the order supplied', () => {
    expect(normaliseRequiredKeys(['WRITE', 'VIEW', 'EDIT', 'READ'])).toEqual([
      'WRITE',
      'VIEW',
      'EDIT',
      'READ',
    ]);
  });

  it('refuses a differently cased key rather than folding it', () => {
    // The defect this replaces returned ['edit'] here, which then matched a granted
    // 'EDIT' — an unrecognised spelling admitting content, which is a fail-open result.
    expect(() => {
      normaliseRequiredKeys('edit');
    }).toThrowError(/'edit' is not a recognised permission key/);
  });

  it('refuses a title-cased key', () => {
    expect(() => {
      normaliseRequiredKeys('Edit');
    }).toThrowError(/is not a recognised permission key/);
  });

  it('refuses a key padded with whitespace rather than trimming it', () => {
    expect(() => {
      normaliseRequiredKeys(' EDIT ');
    }).toThrowError(/is not a recognised permission key/);
  });

  it('refuses an invented key', () => {
    // The vocabulary is closed at four. Keys for management, deployment, addition,
    // removal, administration, creation, export or blanket full control belong to later
    // DotNetNuke versions and to other permission systems; none exists in this schema.
    for (const invented of ['MANAGE', 'DEPLOY', 'DELETE', 'ADD', 'ADMIN', 'FULLCONTROL']) {
      expect(() => {
        normaliseRequiredKeys(invented);
      }).toThrowError(/is not a recognised permission key/);
    }
  });

  it('refuses a blank key', () => {
    // The legacy catalogue read used '' as a wildcard meaning "any key"
    // (PortalController.vb:L1413). That semantic is deliberately not honoured here: a
    // blank key is none, never any.
    expect(() => {
      normaliseRequiredKeys('');
    }).toThrowError(/'' is not a recognised permission key/);
  });

  it('refuses a whitespace-only key', () => {
    expect(() => {
      normaliseRequiredKeys('   ');
    }).toThrowError(/is not a recognised permission key/);
  });

  it('refuses the whole list when any single entry is unrecognised', () => {
    // Silently dropping the bad entry would leave a control gated by a rule the author
    // did not write, and would hide the typo that produced it.
    expect(() => {
      normaliseRequiredKeys(['EDIT', 'MANAGE']);
    }).toThrowError(/'MANAGE' is not a recognised permission key/);
  });

  it('refuses a value that is not a string', () => {
    expect(() => {
      normaliseRequiredKeys(42);
    }).toThrowError(/a value of type number is not a recognised permission key/);
  });

  it('refuses an inherited object member masquerading as a key', () => {
    expect(() => {
      normaliseRequiredKeys('constructor');
    }).toThrowError(/is not a recognised permission key/);
  });

  it('throws for an empty list', () => {
    expect(() => {
      normaliseRequiredKeys([]);
    }).toThrowError(/at least one permission key is required/);
  });

  it('throws for undefined', () => {
    expect(() => {
      normaliseRequiredKeys(undefined);
    }).toThrowError(/at least one permission key is required/);
  });

  it('throws for null', () => {
    expect(() => {
      normaliseRequiredKeys(null);
    }).toThrowError(/at least one permission key is required/);
  });
});

describe('isPermitted', () => {
  // The granted side stays a plain string list because it is wire data — whatever the
  // server actually sent. It is compared exactly, so anything that is not a key simply
  // grants nothing, which is the same conclusion the server itself would reach.

  it('is satisfied by one key out of several', () => {
    expect(isPermitted(['EDIT', 'VIEW'], ['VIEW'])).toBeTrue();
  });

  it('is not satisfied by an unrelated key', () => {
    expect(isPermitted(['EDIT'], ['VIEW'])).toBeFalse();
  });

  it('is never satisfied by an empty granted set', () => {
    expect(isPermitted(['EDIT'], [])).toBeFalse();
  });

  it('is never satisfied by an empty required set', () => {
    expect(isPermitted([], ['EDIT'])).toBeFalse();
  });

  it('compares the granted side exactly, refusing a different case', () => {
    // The replaced defect asserted this was TRUE. It is the fail-open case: it made an
    // unrecognised spelling on either side behave as the real key.
    expect(isPermitted(['EDIT'], ['edit'])).toBeFalse();
  });

  it('compares the granted side exactly, refusing surrounding whitespace', () => {
    expect(isPermitted(['EDIT'], [' EDIT '])).toBeFalse();
  });

  it('refuses a granted value that only contains the required key', () => {
    expect(isPermitted(['EDIT'], ['EDITOR'])).toBeFalse();
  });

  it('is satisfied when the granted list also carries values it does not recognise', () => {
    // A server that ever sent an unexpected value must not stop a real grant working.
    expect(isPermitted(['EDIT'], ['something-else', 'EDIT'])).toBeTrue();
  });
});
