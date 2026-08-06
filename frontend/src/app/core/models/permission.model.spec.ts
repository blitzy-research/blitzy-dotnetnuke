import { PERMISSION_KEYS, isPermissionKey, toPermissionKeys } from './permission.model';

/**
 * Specification for the permission-vocabulary narrowing guards.
 *
 * No `TestBed`: the module under test is a frozen array and two pure functions over it, with
 * no injector, no transport and no DOM involvement.
 *
 * ## Why these expectations exist
 *
 * `PermissionKey` is a compile-time union and is erased at run time, so before this guard
 * existed there was no way to establish that a value arriving over the wire belonged to the
 * closed vocabulary. Two distinct vocabularies coexist in this system and are easy to
 * confuse — the persisted permission KEYS narrowed here, and the authorisation POLICY names
 * enumerated in `core/guards/permission.guard.ts` — plus a third axis, the permission CODE
 * that scopes a key to folders or module definitions. A value from any of the other two
 * reaching a permission decision must FAIL CLOSED, and the specifications below are what
 * hold that line.
 *
 * The load-bearing cases are the refusals, not the acceptances. A permission check that
 * fails open is worse than one that never runs, so the tests that a lower-case spelling, a
 * padded key, a policy name and a permission code are all refused are the ones that would
 * catch a well-meaning `toUpperCase()` or `trim()` being introduced later.
 */
describe('PERMISSION_KEYS', () => {
  it('holds exactly the four keys the schema stores', () => {
    expect(PERMISSION_KEYS).toEqual(['VIEW', 'EDIT', 'READ', 'WRITE']);
  });

  // A vocabulary a caller could push onto is a vocabulary a caller could widen, and widening
  // this list grants access.
  it('is frozen, so no caller can widen the vocabulary', () => {
    expect(Object.isFrozen(PERMISSION_KEYS)).toBeTrue();
  });
});

describe('isPermissionKey', () => {
  it('accepts each of the four recognised keys', () => {
    for (const key of PERMISSION_KEYS) {
      expect(isPermissionKey(key)).toBeTrue();
    }
  });

  it('refuses a key spelled in a different case', () => {
    expect(isPermissionKey('view')).toBeFalse();
    expect(isPermissionKey('Edit')).toBeFalse();
    expect(isPermissionKey('rEaD')).toBeFalse();
  });

  it('refuses a padded key rather than trimming it', () => {
    expect(isPermissionKey(' EDIT')).toBeFalse();
    expect(isPermissionKey('EDIT ')).toBeFalse();
    expect(isPermissionKey(' EDIT ')).toBeFalse();
  });

  it('refuses a key that merely contains a recognised one', () => {
    expect(isPermissionKey('EDITOR')).toBeFalse();
    expect(isPermissionKey('OVERVIEW')).toBeFalse();
  });

  // The legacy catalogue read an empty key as "any key". That wildcard is a server-side
  // query convenience and is deliberately not honoured on this side.
  it('refuses the empty string rather than reading it as a wildcard', () => {
    expect(isPermissionKey('')).toBeFalse();
  });

  // The vocabulary confusion this guard exists to prevent, in both directions.
  it('refuses an authorisation policy name', () => {
    expect(isPermissionKey('PortalAdministrator')).toBeFalse();
    expect(isPermissionKey('ModuleEdit')).toBeFalse();
    expect(isPermissionKey('HostAdministrator')).toBeFalse();
  });

  it('refuses a permission code', () => {
    expect(isPermissionKey('SYSTEM_FOLDER')).toBeFalse();
    expect(isPermissionKey('MODULE_DEFINITION')).toBeFalse();
  });

  it('refuses a role name', () => {
    expect(isPermissionKey('Administrators')).toBeFalse();
  });

  it('refuses an installation-specific key this codebase has never seen', () => {
    expect(isPermissionKey('MANAGE')).toBeFalse();
    expect(isPermissionKey('DEPLOY')).toBeFalse();
  });

  // Accepts `unknown` deliberately, so it is usable on a parsed response, on route data, or
  // on a component input whose declared type a caller may have subverted. None of these may
  // be coerced into a key.
  it('refuses a non-string without coercing it', () => {
    expect(isPermissionKey(null)).toBeFalse();
    expect(isPermissionKey(undefined)).toBeFalse();
    expect(isPermissionKey(0)).toBeFalse();
    expect(isPermissionKey(true)).toBeFalse();
    expect(isPermissionKey({})).toBeFalse();
    expect(isPermissionKey(['EDIT'])).toBeFalse();
    expect(isPermissionKey({ toString: () => 'EDIT' })).toBeFalse();
  });
});

describe('toPermissionKeys', () => {
  it('keeps every recognised key, in the order given', () => {
    expect(toPermissionKeys(['WRITE', 'VIEW', 'EDIT'])).toEqual(['WRITE', 'VIEW', 'EDIT']);
  });

  // Discarding rather than rejecting: a list carrying one unknown key alongside recognised
  // ones is a response from an installation that seeded a key this codebase does not
  // evaluate, not a malformed response. Refusing the whole list would withdraw valid grants.
  it('discards the unrecognised entries while keeping the recognised ones', () => {
    expect(toPermissionKeys(['SOMETHING_ELSE', 'EDIT', 'view', 'READ'])).toEqual([
      'EDIT',
      'READ',
    ]);
  });

  it('yields nothing when no entry is recognised', () => {
    expect(toPermissionKeys(['MANAGE', 'PortalAdministrator', ''])).toEqual([]);
  });

  it('yields nothing for an empty list', () => {
    expect(toPermissionKeys([])).toEqual([]);
  });

  it('treats an absent list as no grants', () => {
    expect(toPermissionKeys(null)).toEqual([]);
    expect(toPermissionKeys(undefined)).toEqual([]);
  });

  // A stable reference for the empty case, so a signal derived from this does not appear to
  // change on every evaluation.
  it('returns the same reference for every absent list', () => {
    expect(toPermissionKeys(null)).toBe(toPermissionKeys(undefined));
  });

  it('preserves duplicates rather than collapsing them', () => {
    expect(toPermissionKeys(['EDIT', 'EDIT'])).toEqual(['EDIT', 'EDIT']);
  });

  it('does not mutate the list it was given', () => {
    const granted = ['EDIT', 'MANAGE'];

    toPermissionKeys(granted);

    expect(granted).toEqual(['EDIT', 'MANAGE']);
  });
});
