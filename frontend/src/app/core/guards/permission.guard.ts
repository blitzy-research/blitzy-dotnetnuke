import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { ActivatedRouteSnapshot, CanActivateFn } from '@angular/router';

import { RETURN_URL_QUERY_KEY, SIGN_IN_ROUTE } from '../config/app-routes.config';
import { NotificationService } from '../services/notification.service';
import { AuthStore } from '../state/auth.store';

/**
 * The authorisation policy names the API registers, and the complete set of values a route may declare. ⚠
 * CLOSED AT NINE, AND NINE IS THE WHOLE REGISTERED SET — not a convenient subset.
 * `Api/Authorization/PolicyNames.cs` declares exactly these nine names and
 * `Api/Extensions/AuthenticationExtensions.cs` registers exactly these nine and no others.
 */
const PERMISSION_POLICIES = [
  'ModuleView',
  'ModuleEdit',
  'TabView',
  'TabEdit',
  'PortalAdministrator',
  'HostAdministrator',
  'AccountOwner',
  'AccountOwnerOrPortalAdministrator',
  'PortalContentEditor',
] as const;

/** The closed set of ASP.NET Core authorisation policy names registered by the API. */
export type PermissionPolicy = (typeof PERMISSION_POLICIES)[number];

/**
 * The route `data` key carrying the declared policy name. Load-bearing and shared with the route table,
 * which declares it as `data: { permission: 'PortalAdministrator' }`.
 */
const POLICY_DATA_KEY = 'permission';

const RETURN_URL_KEY = RETURN_URL_QUERY_KEY;

const MODULE_SCOPE_PARAM = 'moduleId';

/**
 * The route parameter a tab-scoped policy is resolved from. The same arrangement as {@link
 * MODULE_SCOPE_PARAM} and the same single-name contract: `PermissionAuthorizationHandler.cs:L128`
 * declares `TabRouteKey = "tabId"`. ⚠ NOT `tabModuleId`.
 */
const TAB_SCOPE_PARAM = 'tabId';

const ACCOUNT_SCOPE_PARAM = 'userId';

/**
 * The route parameter naming the portal a screen acts on.
 *
 * ⚠ RESOLVED FOR `PortalAdministrator` BECAUSE THE SERVER RESOLVES IT, AND ITS ABSENCE HERE WAS MEASURED AS
 * A DEFECT. `PortalAdministrationEvaluator` reads this same route key and then requires
 * `ReadTokenPortalId(user) == portalId`, so a tenant administrator may act on THEIR OWN portal and no
 * other. This gate previously resolved no scope for the policy at all and asked only "does the caller
 * administer some portal", which admitted a tenant administrator to another tenant's screens: measured
 * live, `/portals/{other}` and `/portals/{other}/aliases` both answer 403 auth.not_permitted, and the
 * screens rendered host-only content over five refused reads.
 *
 * Most routes carrying this policy name no portal at all - the account, module, role and settings groups
 * act on the caller's own tenant implicitly - so the scope is treated as OPTIONAL below and those routes
 * keep exactly the behaviour they had.
 */
const PORTAL_SCOPE_PARAM = 'portalId';

/**
 * The wording a refusal is presented with. Authored inline in English rather than resolved from a
 * resource file, and deliberately opening on the same stem as every other app-authored denial in this
 * application - `FORBIDDEN`, and the two host-only field refusals - so that a refusal is recognisable
 * as a refusal from its first four words wherever it is met.
 *
 * ⚠ THIS DOES NOT ADOPT THE FULL LEGACY `AccessDenied.Text` SENTENCE, and the reason is accuracy rather
 * than preference. That sentence hedges across two states at once - "Either you are not currently logged
 * in, or you do not have access to this content." - because the legacy page served both. Here the two
 * states are separated: `authGuard` owns the unauthenticated case, and the 401 path presents the legacy
 * sentence verbatim through `NOT_AUTHENTICATED`. By the time THIS guard refuses, the caller is known to
 * be signed in, so the "not currently logged in" half of the legacy sentence would be simply false.
 */
const ACCESS_REFUSED_MESSAGE = 'You do not have permission to view this content.';

// `Website/admin/Security/App_LocalResources/AccessDenied.ascx.resx` holds the sentence `Either you are not
// currently logged in, or you do not have access to this content.` and
// `Website/admin/Security/AccessDenied.ascx.vb:45` presents it with
// `ModuleMessage.ModuleMessageType.YellowWarning` - the WARNING band of the legacy's measured three-level
// vocabulary, which is the severity the three call sites below use.

/**
 * Whether a string is one of the nine registered policy names. A type predicate rather than a plain
 * boolean test, so a successful check NARROWS the value to {@link PermissionPolicy} for everything
 * downstream and the policy can then be switched on exhaustively.
 *
 * @param value The candidate name, already known to be a string.
 * @returns True when the name is registered, narrowing `value` on success.
 */
function isPermissionPolicy(value: string): value is PermissionPolicy {
  return (PERMISSION_POLICIES as readonly string[]).includes(value);
}

/**
 * The route parameter a policy's scope is resolved from, or null when it needs none. Three of the nine
 * policies resolve no scope, and for three different reasons that are worth keeping distinct.
 *
 * @param policy A registered policy name.
 * @returns The single parameter name to resolve, or null when no scope is required.
 */
function scopeParamName(policy: PermissionPolicy): string | null {
  switch (policy) {
    case 'ModuleView':
    case 'ModuleEdit':
      return MODULE_SCOPE_PARAM;
    case 'TabView':
    case 'TabEdit':
      return TAB_SCOPE_PARAM;
    case 'AccountOwner':
    case 'AccountOwnerOrPortalAdministrator':
      return ACCOUNT_SCOPE_PARAM;
    case 'PortalAdministrator':
    case 'HostAdministrator':
    case 'PortalContentEditor':
      return null;
  }
}

/**
 * The route parameter a policy BINDS TO WHEN PRESENT but does not require.
 *
 * ⚠ DELIBERATELY SEPARATE FROM {@link scopeParamName}, AND MERGING THE TWO BREAKS THE APPLICATION. A name
 * returned by that function is MANDATORY: the gate refuses outright when the route supplies no value for
 * it, on the ground that the question is unanswerable. `PortalAdministrator` cannot be declared that way,
 * because most routes carrying it name no portal at all - the account, module, role and settings groups act
 * on the caller's own tenant implicitly. Declaring it mandatory was tried and the existing suite caught it
 * immediately: every one of those routes was refused outright. So the portal is bound only when the address
 * actually names one, which is exactly the three portal screens.
 *
 * @param policy A registered policy name.
 * @returns The optional parameter name to resolve, or null when the policy binds to none.
 */
function optionalScopeParamName(policy: PermissionPolicy): string | null {
  return policy === 'PortalAdministrator' ? PORTAL_SCOPE_PARAM : null;
}

/**
 * @param route The activated route snapshot the router is deciding.
 * @param name The single parameter name the policy's scope is carried under.
 * @returns The nearest identifier found, or null when the route supplies none.
 */
function resolveScopeId(route: ActivatedRouteSnapshot, name: string): string | null {
  const deepestFirst = [...route.pathFromRoot].reverse();

  for (const snapshot of deepestFirst) {
    const value = snapshot.paramMap.get(name);

    if (value !== null && value.length > 0) {
      return value;
    }
  }

  return null;
}

/**
 * A route identifier read as the integer it denotes, or null when it does not denote one. ⚠ DELIBERATELY
 * NOT `Number(value)`, AND NOT `parseInt` ALONE. Both are too generous for a value that is about to be
 * compared against an account key.
 *
 * @param value A non-empty route parameter value.
 * @returns The integer it denotes, or null when it denotes none.
 */
function parseIdentifier(value: string): number | null {
  if (/^-?\d+$/.test(value) === false) {
    return null;
  }

  const parsed = Number(value);

  return Number.isSafeInteger(parsed) ? parsed : null;
}

/**
 * @param authStore The identity projection.
 * @returns True when the caller is a host account or administers the resolved tenant.
 */
function holdsPortalAdministration(authStore: AuthStore): boolean {
  return authStore.administersCurrentPortal();
}

/**
 * Whether the caller may administer the portal a route NAMES, rather than merely some portal.
 *
 * Mirrors `PortalAdministrationEvaluator`: a host account may act on any portal, and everyone else only on
 * the portal their own session names. The identifiers are both already held by the client, so this is a
 * comparison rather than a question about stored records - the same footing as {@link isTheNamedAccount}.
 *
 * Two deliberate abstentions, each because refusing would be worse than deferring:
 *
 * - When the route names NO portal, the previous, unscoped judgement is kept. The account, module, role and
 *   settings groups all carry this policy without a `portalId`, and they act on the caller's own tenant
 *   implicitly; refusing them here would break screens that work.
 * - When the route's value does not denote an integer, nothing is claimed. `/portals/abc/settings` is a
 *   malformed address, not an unauthorised one, and answering it with "you do not have access" would send
 *   the caller to ask for rights that would not help. The server answers that case properly.
 *
 * ⚠ `-1` IS A REAL PORTAL IDENTIFIER HERE, NOT AN ABSENT ONE. `Portals.PortalID` is seeded `IDENTITY(-1,1)`,
 * so the first portal is -1 and the second 0 — both of which the legacy sentinel vocabulary would have read
 * as "nothing". The comparison is therefore strict numeric equality against a parsed value, and no value is
 * treated as a sentinel.
 *
 * @param authStore The identity projection.
 * @param scopeId The route's portal identifier, as received, or null when the route names none.
 * @returns True when the caller may administer the named portal.
 */
function administersTheNamedPortal(authStore: AuthStore, scopeId: string | null): boolean {
  if (authStore.isSuperUser()) {
    return true;
  }

  if (scopeId === null) {
    return holdsPortalAdministration(authStore);
  }

  const target = parseIdentifier(scopeId);

  if (target === null) {
    return true;
  }

  const own = authStore.portalId();

  return holdsPortalAdministration(authStore) && own !== null && own === target;
}

/**
 * Whether the caller IS the account a route names. The client can answer this one exactly, because it is
 * a comparison of two identifiers it already holds rather than a question about stored records.
 *
 * @param authStore The identity projection.
 * @param scopeId The route's account identifier, as received.
 * @returns True only when both identifiers are present and denote the same account.
 */
function isTheNamedAccount(authStore: AuthStore, scopeId: string | null): boolean {
  if (scopeId === null) {
    return false;
  }

  const target = parseIdentifier(scopeId);
  const caller = authStore.currentUser()?.userId;

  return target !== null && caller !== undefined && caller === target;
}

/**
 * Whether the client can already see that the caller cannot use the screen. ONE COARSE CONVENIENCE CHECK
 * PER POLICY, AND EXPLICITLY NOT AN AUTHORISATION ENGINE. Each arm answers a question from the identity
 * the client already holds, or declines to answer at all.
 *
 * @param policy A registered policy name.
 * @param scopeId The resolved scope identifier, or null when the policy needs none.
 * @param authStore The identity projection, already known to be resolved.
 * @param namedPortalId The portal the address names, or null when it names none.
 * @returns True only when the client can see the caller plainly cannot use the screen.
 */
function isPlainlyRefused(
  policy: PermissionPolicy,
  scopeId: string | null,
  authStore: AuthStore,
  namedPortalId: string | null,
): boolean {
  switch (policy) {
    case 'ModuleView':
    case 'ModuleEdit':
    case 'TabView':
    case 'TabEdit':
      return false;

    case 'PortalAdministrator':
      return administersTheNamedPortal(authStore, namedPortalId) === false;

    // Answered from the host flag ALONE, with no tenant reasoning of any kind, because the policy has no
    // portal binding by design.
    case 'HostAdministrator':
      return authStore.isSuperUser() === false;

    case 'AccountOwner':
      return isTheNamedAccount(authStore, scopeId) === false;

    // Both arms, in the same order the server evaluates them: ownership first, then the administrator of
    // the account's portal. Refused only when NEITHER holds.
    case 'AccountOwnerOrPortalAdministrator':
      return (
        isTheNamedAccount(authStore, scopeId) === false &&
        holdsPortalAdministration(authStore) === false
      );

    // ⚠ NEVER REFUSED HERE, AND NOT FOR WANT OF A FACT THAT LOOKS USABLE. The session projection carries an
    // advisory `permissions` list, so `EDIT` being absent from it looks like grounds for a plain refusal.
    case 'PortalContentEditor':
      return false;
  }
}

/**
 * Admits a caller who may plausibly use the screen a route declares a policy for, and refuses everyone
 * else at warning severity. A FUNCTION AND NOT A CLASS, for the same reasons `core/guards/auth.guard.ts`
 * is one: the class-based activation contract is deprecated in this generation of the router, and a
 * functional gate needs no provider, no decorator and no registration.
 *
 * @param route The activated route snapshot, read for the declared policy and the scope identifier the
 * policy applies to.
 * @param state The router state, whose `url` is the full attempted address.
 * @returns `true` to admit, `false` to refuse, or a redirect to the sign-in route.
 */
export const permissionGuard: CanActivateFn = (route, state) => {
  const router = inject(Router);
  const authStore = inject(AuthStore);
  const notification = inject(NotificationService);

  // Authentication is settled first, and settled the same way the companion gate settles it, so a route
  // carrying both gates cannot produce two different answers to the one question. The store's verdict is
  // read rather than recomputed: re-deciding it here would put two answers in the application.
  if (authStore.isAuthenticated() === false) {
    return router.createUrlTree([SIGN_IN_ROUTE], {
      queryParams: { [RETURN_URL_KEY]: state.url },
    });
  }

  // The declared policy is widened to `unknown` before it is examined, which is the load-bearing detail of
  // this whole file.
  const declared: unknown = route.data[POLICY_DATA_KEY];

  // ⚠ THIS GATE DELIBERATELY DOES NOT TOUCH FOCUS, and a reported defect saying it should was investigated
  // and disproved. Runtime testing found `document.activeElement === document.body` after every permission
  // denial and concluded that "focus is not returned to the trigger".
  if (typeof declared !== 'string' || isPermissionPolicy(declared) === false) {
    notification.notify(
      'warning',
      ACCESS_REFUSED_MESSAGE,
      null,
      false,
      true,
    );

    notification.retainAcrossNavigation();

    return false;
  }

  // A scoped policy without a scope is refused for the same reason. The API authorises the module, page and
  // account policies against a specific record, so a route that names one of them without supplying an
  // identifier does not describe an answerable question.
  const scopeName = scopeParamName(declared);
  const scopeId = scopeName === null ? null : resolveScopeId(route, scopeName);

  if (scopeName !== null && scopeId === null) {
    notification.notify(
      'warning',
      ACCESS_REFUSED_MESSAGE,
      null,
      false,
      true,
    );

    notification.retainAcrossNavigation();

    return false;
  }

  // Resolved separately from the mandatory scope above and AFTER that refusal, so a policy that binds to a
  // portal only when the address names one cannot be mistaken for one that requires it.
  const optionalScopeName = optionalScopeParamName(declared);
  const namedPortalId =
    optionalScopeName === null ? null : resolveScopeId(route, optionalScopeName);

  if (
    authStore.currentUser() !== null &&
    isPlainlyRefused(declared, scopeId, authStore, namedPortalId)
  ) {
    // A refusal cancels the navigation and says so, rather than redirecting.
    notification.notify(
      'warning',
      ACCESS_REFUSED_MESSAGE,
      null,
      false,
      true,
    );

    notification.retainAcrossNavigation();

    return false;
  }

  // Admitted, and NOT declared authorised. Reaching this line means the route declares a registered policy,
  // the scope that policy needs is present, and nothing the client can see contradicts the caller's right
  // to be here.
  return true;
};
