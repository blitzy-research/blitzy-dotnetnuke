/**
 * The navigation gate that reads the authorisation POLICY a route declares and refuses
 * to navigate to a screen the caller demonstrably cannot use.
 *
 * The companion to `core/guards/auth.guard.ts`, which asks only whether anybody is
 * signed in. That file explicitly defers the finer-grained question to "the separate
 * permission gate named by the migration plan"; this is that gate. The two are written
 * as one design: identical import shape, identical redirect construction, identical
 * sentinel discipline, and the same query-parameter key carrying the attempted address.
 *
 * ONE DECLARATIVE GATE REPLACES A SCATTER OF IMPERATIVE ONES. The legacy application
 * had no route table, so every administrative page re-asked its own access question in
 * its own load handler and navigated away by side effect. Three of those tests are
 * quoted below because, between them, they show why a per-page reproduction would be
 * the wrong thing to build:
 *
 * ```vbnet
 * ' Website/admin/Portal/Portals.ascx.vb:L339-L341 — a host account is REQUIRED
 * If Not UserInfo.IsSuperUser Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 *
 * ' Website/admin/Modules/ModuleSettings.ascx.vb:L191-L193 — portal OR active-tab administration
 * ' (one line in the original; wrapped here with the language's own continuation)
 * If PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False _
 *         And PortalSecurity.IsInRoles(PortalSettings.ActiveTab.AdministratorRoles.ToString) = False Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 *
 * ' Website/admin/Security/SecurityRoles.ascx.vb:L321-L323 — a host account is EXCLUDED
 * If (Not (objUser Is Nothing) AndAlso objUser.IsSuperUser) OrElse _
 *             PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False Then
 *     Response.Redirect(NavigateURL("Access Denied"), True)
 * End If
 * ```
 *
 * THOSE THREE TESTS DISAGREE WITH ONE ANOTHER, which is a measured finding rather than
 * an impression. A host account is mandatory on the first screen, excluded on the
 * third, and on the second it is admitted only as a side effect of the helper: the
 * condition at `Library/Components/Security/PortalSecurity.vb:L124` opens with
 * `If objUserInfo.IsSuperUser Or (…)`, so `IsInRoles` answers true for a host account
 * against ANY role string, including an empty one. Reproducing all three would import
 * three contradictory rules into one application. What they DO share is a single
 * uniform core, and it is the only thing reproduced here: a caller who is neither a
 * host account nor a portal administrator is refused by all three.
 *
 * ⚠ THIS GATE IS AN AFFORDANCE, NEVER AN ENFORCEMENT POINT. It decides what is worth
 * NAVIGATING to; the API decides what is PERMITTED. Every policy-protected endpoint is
 * re-authorised server-side against stored state and answers 403 on its own account,
 * and that refusal is the authoritative one. Consequently a caller this gate admits may
 * still be refused, admission NEVER implies the next request will succeed, no verdict is
 * cached, and no request of any kind is issued from here. Nothing in this file evaluates
 * stored permission records: no per-module or per-tab permission collection is fetched,
 * compared or interpreted, because doing so would be a second authorisation engine
 * disagreeing with the first.
 *
 * ⚠ TWO CLOSED, NON-INTERCHANGEABLE VOCABULARIES, AND THIS FILE OWNS ONLY ONE. The
 * names below are the API's authorisation POLICY names. The persisted permission keys
 * held against module and tab records are a separate, unrelated four-name set belonging
 * to the shared permission directive; `core/state/auth.store.ts` documents the same
 * split on its own permission projection. A policy name must never be passed where a
 * persisted key is expected, nor the reverse, so neither this file nor its imports
 * touch `core/models/permission.model.ts` — that model owns the other vocabulary.
 *
 * @see `Website/admin/Security/AccessDenied.ascx.vb` — the legacy destination, whose
 * class is `AccessDeniedPage`. It contains NO access test of its own; measured, its
 * permission-check count is zero and its whole behaviour is to PRESENT the refusal.
 * Both of its branches, at L43 and L45, present it with
 * `ModuleMessage.ModuleMessageType.YellowWarning`, so a refusal was a WARNING and never
 * a fault. That is why every refusal here is raised at warning severity, and it must
 * never be escalated.
 */

import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { ActivatedRouteSnapshot, CanActivateFn } from '@angular/router';

import { RETURN_URL_QUERY_KEY, SIGN_IN_ROUTE } from '../config/app-routes.config';
import { NotificationService } from '../services/notification.service';
import { AuthStore } from '../state/auth.store';

/**
 * The authorisation policy names the API registers, and the complete set of values a
 * route may declare.
 *
 * ⚠ CLOSED AT NINE, AND NINE IS THE WHOLE REGISTERED SET — not a convenient subset.
 * `Api/Authorization/PolicyNames.cs` declares exactly these nine names (L86, L93, L105,
 * L112, L147, L171, L185, L196, L231) and `Api/Extensions/AuthenticationExtensions.cs`
 * registers exactly these nine and no others. Listing fewer would be worse than it
 * looks in BOTH directions: a route declaring a real policy this list omitted would be
 * refused here for no reason a person could see, while the omission would also hide the
 * scope that policy needs, so nothing would demand the identifier the server is about to
 * require.
 *
 * A name OUTSIDE the registered set is a FAULT rather than a mere typo. The API
 * registers no policy provider that could invent a policy on demand, so an unregistered
 * name does not produce a tidy refusal at the endpoint; it throws while the request is
 * being authorised. Refusing such a route here, before anything is sent, is therefore
 * strictly safer than forwarding it, which is why {@link permissionGuard} fails closed
 * rather than shrugging and admitting.
 *
 * Declared as a `const` tuple rather than an enumeration for two reasons. A tuple is
 * the single source of BOTH the runtime list membership is tested against and the
 * compile-time type derived from it, so the two can never drift; and `isolatedModules`
 * is enabled in `tsconfig.json`, which rules out a constant enumeration outright.
 *
 * WHAT EACH ONE IS FOR, since the names alone do not distinguish the three
 * administration policies and choosing wrongly between them is silent:
 *
 * - `ModuleView` / `ModuleEdit` — one specific module instance. The API answers these
 *   from the permission records held against that module, which is why nothing here
 *   attempts to answer them.
 * - `TabView` / `TabEdit` — one specific page. Same arrangement, same reason.
 * - `PortalAdministrator` — administration WITHIN a tenant. The API decides it against
 *   the portal the ROUTE names, falling back to the tenant the caller arrived through
 *   when the route names none (`PortalAdministrationEvaluator.cs:L193-L203`). A host
 *   account satisfies it. The client answers it from the server's OWN published verdict
 *   and never from a role name — see {@link holdsPortalAdministration} for why the role
 *   name it once matched was wrong in both directions.
 * - `HostAdministrator` — for operations that address NO SINGLE PORTAL: the portal
 *   collection, portal creation, and aliases addressed by their own global identifier.
 *   ⚠ Not interchangeable with the one above. `PolicyNames.cs:L120-L130` records why the
 *   distinction exists: because portal administration falls back to the arrival tenant,
 *   using it on a global operation asked a truthful but irrelevant question and let an
 *   administrator of one tenant enumerate every tenant, create new ones, or reach
 *   another tenant's alias by guessing its identifier.
 * - `AccountOwner` — the account the route names AND NOBODY ELSE. For the credential
 *   change alone: a change presents the current credential, so only its holder can
 *   perform one. It has no administrator arm at all.
 * - `AccountOwnerOrPortalAdministrator` — the account the route names, or an
 *   administrator of that account's portal. For the account resources both legitimately
 *   reach: the account's own representation and its profile.
 * - `PortalContentEditor` — an administrator of the resolved tenant, OR a caller holding
 *   `EDIT` on at least one of its pages. Requires NO item identifier. For the supporting
 *   reads of an operation whose target does not exist yet, which is a category none of the
 *   others can express: `ModuleEdit` and `TabEdit` resolve their scope from an item
 *   identifier in the route, and module CREATION names no page at all, because the target
 *   page arrives in the request BODY. `PolicyNames.cs:L198-L231` records the reasoning and
 *   the legacy gate it reproduces — `ModuleSettings.ascx.vb:L191`, the tenant's
 *   administrators or the roles holding EDIT on the page being administered from.
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
 * The route `data` key carrying the declared policy name.
 *
 * Load-bearing and shared with the route table, which declares it as
 * `data: { permission: 'PortalAdministrator' }`. Held as a named constant so a
 * specification asserts against the same string the gate reads.
 */
const POLICY_DATA_KEY = 'permission';

/*
 * The sign-in destination is IMPORTED from `core/config/app-routes.config.ts`; see the
 * import at the head of this file.
 *
 * MIGRATION: a private copy used to stand here, justified on the grounds that the companion
 * gate "does not export the constant, and the two gates agreeing by construction matters
 * less than neither gate reaching into the other". The second clause had it backwards. The
 * two gates AGREEING is the property that matters — a mismatch sends an expired session to
 * the catch-all instead of to the sign-in screen, silently — and neither gate has to reach
 * into the other to get it, because the value now lives in a module that reaches into
 * nothing itself.
 */

/**
 * The query parameter key carrying the address the caller was trying to reach.
 *
 * IMPORTED, so that "identical to the key the session gate uses" is now true BY
 * CONSTRUCTION rather than by assertion. This comment previously claimed the two matched and
 * nothing compared them; the sign-in screen reads exactly one key, so a divergence here
 * would have silently stranded every caller this gate turns away.
 */
const RETURN_URL_KEY = RETURN_URL_QUERY_KEY;

/**
 * The route parameter a module-scoped policy is resolved from.
 *
 * ⚠ EXACTLY ONE NAME, AND THERE IS NO FALLBACK — this is the API's contract read from
 * the API, not an approximation of it. `PermissionAuthorizationHandler.cs:L122` declares
 * `ModuleRouteKey = "moduleId"` and its own remark states in as many words that only
 * this name is accepted and why a bare identifier segment is deliberately refused: it
 * "would let a nested route hand this handler some other entity's key — deciding a module
 * question from a page's or an account's identifier, which is worse than refusing".
 * `ResolveRouteKey` at L367-L372 returns that single name and nothing else, and when the
 * route carries no such value the handler logs a registration mistake and refuses
 * (L275-L283).
 *
 * A previous revision of this file listed `['moduleId', 'id']` and described the second
 * entry as mirroring the API. It did not: no such fallback exists server-side. Accepting
 * a bare `id` here would have let the gate authorise one record while the endpoint
 * authorised another — precisely the confusion the server's remark refuses — so the fact
 * that the two sides then disagreed would have surfaced as an unexplainable 403 on a
 * screen this gate had just admitted. Held as a single string rather than a list so the
 * fallback cannot be reintroduced by appending to it.
 */
const MODULE_SCOPE_PARAM = 'moduleId';

/**
 * The route parameter a tab-scoped policy is resolved from.
 *
 * The same arrangement as {@link MODULE_SCOPE_PARAM} and the same single-name contract:
 * `PermissionAuthorizationHandler.cs:L128` declares `TabRouteKey = "tabId"`.
 *
 * ⚠ NOT `tabModuleId`. That name exists server-side (L134) but is read from the QUERY
 * STRING rather than the route, and it names one particular PLACEMENT of a module on a
 * page rather than the page itself. It is therefore not a scope this gate resolves.
 */
const TAB_SCOPE_PARAM = 'tabId';

/**
 * The route parameter an account-scoped policy is resolved from.
 *
 * `PortalAdministrationEvaluator.cs:L84` declares `UserRouteKey = "userId"`, and the
 * account handler reads exactly that value (`PortalAdministratorAuthorizationHandler.cs:L247`).
 *
 * ⚠ THE COMPANION PORTAL IDENTIFIER IS DELIBERATELY NOT DEMANDED, and this is the one
 * place where the API's prose and the API's behaviour differ, so it is recorded rather
 * than guessed at. `PolicyNames.cs` describes the owner-or-administrator policy as
 * requiring both identifiers, but the handler does not fail without a route `portalId`:
 * `ResolveTargetPortalIdAsync` (`PortalAdministrationEvaluator.cs:L193-L203`) uses the
 * route's portal WHEN THE ROUTE NAMES ONE and otherwise the tenant the caller arrived
 * through. The handler's own comment records that an earlier revision did demand it and
 * that the demand could never be satisfied, because the account routes are mounted flat
 * as `api/v1/users/{userId}` and name no portal segment at all — which refused every
 * account holder its own self-service routes, including the credential change a blocking
 * remediation exists to send it to. Demanding it here would reintroduce exactly that
 * defect on the client.
 */
const ACCOUNT_SCOPE_PARAM = 'userId';

/**
 * The wording a refusal is presented with.
 *
 * MIGRATION: authored inline in English rather than resolved from a resource file.
 * `Website/admin/Security/AccessDenied.ascx.vb:L45` read its text through
 * `Services.Localization.Localization.GetString("AccessDenied", …)`, and that mechanism
 * is deliberately not carried forward — the localisation package is out of scope for
 * this migration, so no translation runtime exists to resolve a key against. The legacy
 * resource remains the authority for the PHRASING: the value of `AccessDenied.Text` in
 * `Website/admin/Security/App_LocalResources/AccessDenied.ascx.resx` reads "Either you
 * are not currently logged in, or you do not have access to this content." Its second
 * clause is used verbatim and its first is dropped, because by the time this gate
 * refuses anything the caller IS signed in — an unauthenticated caller was already
 * redirected — so retaining "either you are not currently logged in" would offer a
 * reason that cannot apply.
 *
 * MIGRATION: rendered as PLAIN TEXT, never as markup. The legacy page passed its
 * message through `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(…))` at L43 before
 * displaying it, and that precedent is honoured rather than relaxed. It matters because
 * legacy resource values are not inert: across the in-scope resource files a
 * substantial minority carry HTML, and at least one carries a live script element, so
 * treating any of that wording as markup would be an injection vector. The notification
 * service this is handed to documents its parameter as already-composed plain text and
 * performs no interpretation of its own.
 *
 * One constant serves all three refusal branches. They differ in CAUSE — a mis-declared
 * policy, an unresolvable scope, and a caller the client can already see lacks the
 * administration or the ownership the policy requires — but not in what the person reading
 * the screen can do about it, and the legacy presented every refusal with a single sentence
 * too.
 *
 * ⚠ THE CAUSE IS DELIBERATELY NOT DISCLOSED. Telling a caller which of the three applies
 * would tell them whether the account, module or page they named exists and whether they
 * merely lack a role — a disclosure the API itself avoids, since every refusal it issues is
 * a uniform 403.
 */
const ACCESS_REFUSED_MESSAGE = 'You do not have access to this content.';

/*
 * PROVENANCE OF THE WORDING AND OF THE SEVERITY, both measured rather than chosen, and recorded here
 * because a review asked why a refused NAVIGATION reads differently from a refused REQUEST.
 *
 * `Website/admin/Security/App_LocalResources/AccessDenied.ascx.resx` holds the sentence
 * `Either you are not currently logged in, or you do not have access to this content.` and
 * `Website/admin/Security/AccessDenied.ascx.vb:45` presents it with
 * `ModuleMessage.ModuleMessageType.YellowWarning` - the WARNING band of the legacy's measured
 * three-level vocabulary, which is the severity the three call sites below use. The first clause is
 * dropped deliberately: it covered the unauthenticated case, and an unauthenticated caller never
 * reaches this guard's refusal - `authGuard` sends them to the sign-in screen carrying a return
 * address instead - so reproducing it would state an alternative that cannot apply.
 *
 * The legacy also DELIVERED it the way this guard does: `AccessDenied` is a page the operator was sent
 * TO, carrying its message, rather than a panel raised on the page they were refused. That is why a
 * refusal here is announced through the shared channel and survives the navigation, while a server 403
 * is rendered inline on the screen that issued the request. The two are not one event presented twice:
 * a guard refusal is decided from the session's own claims with no request made, so it has no
 * correlation reference to offer and no destination screen on which to anchor a panel, whereas a 403
 * answers a request that was really issued, on a screen the operator legitimately reached, and carries
 * a reference a support call can quote.
 */

/**
 * Whether a string is one of the nine registered policy names.
 *
 * A type predicate rather than a plain boolean test, so a successful check NARROWS the
 * value to {@link PermissionPolicy} for everything downstream and the policy can then
 * be switched on exhaustively.
 *
 * The widening cast is the point of the implementation. `PERMISSION_POLICIES` is a
 * readonly tuple of literal types, so its own `includes` accepts only members of that
 * union — passing an arbitrary string is a compile error, which is precisely the check
 * being attempted. Widening the tuple to `readonly string[]` for the call restores an
 * honest signature without weakening any type: the cast discards literal information it
 * does not need and the predicate hands back a fully narrowed result. No escape hatch
 * is used, and the value under test is never coerced.
 *
 * @param value The candidate name, already known to be a string.
 * @returns True when the name is registered, narrowing `value` on success.
 */
function isPermissionPolicy(value: string): value is PermissionPolicy {
  return (PERMISSION_POLICIES as readonly string[]).includes(value);
}

/**
 * The route parameter a policy's scope is resolved from, or null when it needs none.
 *
 * Three of the nine policies resolve no scope, and for three different reasons that are
 * worth keeping distinct. Portal administration is a question about the caller WITHIN a
 * tenant, and the API resolves that tenant from the route's portal when it names one and
 * from the arrival tenant otherwise, so the route is free not to name it. Host
 * administration has no portal binding of any kind by design — it exists precisely for
 * the operations that address no single portal — so there is nothing for it to scope to.
 * Content editing is the third and the least obvious: it IS tenant-scoped, like portal
 * administration, but the operations it supports name no ITEM because the item does not
 * exist yet — module creation carries its target page in the request body — so there is no
 * identifier in the route for it to read even in principle.
 *
 * The remaining six each identify one specific record: a module, a page, or an account.
 *
 * Written as an exhaustive switch over the narrowed union rather than a lookup object, so
 * adding a TENTH policy to {@link PERMISSION_POLICIES} fails to compile here until its
 * scope requirement is stated. The ninth, `PortalContentEditor`, was added exactly that
 * way. That is the intended behaviour, and it is what the
 * previous five-name revision of this file could not offer: an unhandled policy silently
 * defaulting to "needs no scope" would quietly stop requiring the very identifier the API
 * is about to demand, and nothing would report it.
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
    /*
     * ⚠ NO SCOPE, AND THAT IS THE WHOLE POINT OF THE THIRD ENTRY HERE. `PortalContentEditor`
     * supports operations whose target does not exist yet, so there is no item identifier in
     * the route to resolve — the tenant is resolved by the server from the request, exactly as
     * it is for the two above. Giving it a scope param would make it fail closed on every route
     * that declares it, which is the failure mode the create screen previously avoided by
     * declaring no policy at all.
     */
    case 'PortalAdministrator':
    case 'HostAdministrator':
    case 'PortalContentEditor':
      return null;
  }
}

/**
 * The identifier a scoped policy applies to, searched for across the whole route
 * ancestry, or null when the route declares none.
 *
 * WHY THE ANCESTRY IS WALKED AT ALL. Route `data` is merged down onto every snapshot,
 * but route PARAMETERS are not: the router's default inheritance strategy leaves a
 * parameter on the snapshot whose path segment declared it. A child route such as the
 * settings screen beneath `modules/:moduleId` therefore sees the policy on its own
 * snapshot while the identifier the policy applies to sits on its parent. Reading only
 * the activated snapshot would find nothing and refuse a correctly configured route.
 *
 * ⚠ ONE NAME, SEARCHED DEEPEST-FIRST. A previous revision searched a LIST of names and
 * documented an elaborate precedence rule for exhausting the explicit name across every
 * ancestor before trying a bare `id` anywhere. That rule solved a problem the API does
 * not have: there is no bare-identifier fallback server-side, so there is no second name
 * to give precedence to. What remains is the only ambiguity that can actually arise —
 * the SAME name appearing at more than one depth, for which the nearest ancestor is the
 * one the activated screen is about.
 *
 * ⚠ SENTINEL DISCIPLINE — ZERO IS A REAL IDENTIFIER. Module, tab and role keys are all
 * declared `IDENTITY(0, 1)` in the baseline schema and portal keys `IDENTITY(-1, 1)`, so
 * `0` and `-1` are DATA rather than absence. Compounding it, the legacy null contract at
 * `Library/Components/Shared/Null.vb:L41-L45` returns `-1` for a missing integer and
 * L71-L75 returns the empty string for a missing string, so one value means both a real
 * record and "no record".
 *
 * Presence is therefore decided by testing the identity of the value and never its
 * magnitude or its truthiness. Two specific traps are avoided by construction: a
 * parameter arrives as a STRING, so the identifier `'0'` is truthy while the number it
 * denotes is falsy — a truthiness test would appear to work until the day it silently
 * refused row zero; and no default is substituted anywhere, because a fallback to `0` or
 * `-1` would manufacture a real identifier out of an absent one. The empty string is
 * treated as absent, and that is asserted through an explicit length comparison rather
 * than truthiness, because the empty string is exactly what the legacy contract returns
 * for a missing string.
 *
 * The ancestry is reversed on a COPY. `pathFromRoot` hands back an array the router
 * assembled, and reversing it in place would be a side effect in a function whose entire
 * job is to answer a question.
 *
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
 * A route identifier read as the integer it denotes, or null when it does not denote one.
 *
 * ⚠ DELIBERATELY NOT `Number(value)`, AND NOT `parseInt` ALONE. Both are too generous for
 * a value that is about to be compared against an account key. `Number('0x10')` is 16,
 * `Number('1e3')` is 1000, `Number(' 7 ')` is 7 and `Number('')` is 0 — so a route segment
 * that is not an identifier at all could be coerced into one, and the coercion of the
 * empty string into ZERO is the dangerous case, because zero is a legitimate key in this
 * schema. `parseInt` is worse in the other direction: it reads `'7abc'` as 7. The pattern
 * is therefore matched first and the conversion performed only on a value already known to
 * be nothing but an optional sign and digits.
 *
 * ⚠ SENTINEL DISCIPLINE. `-1` and `0` are DATA, so the sign is accepted and no magnitude
 * test is applied: portals seed at `IDENTITY(-1, 1)` and pages, roles and modules at
 * `IDENTITY(0, 1)`. Nothing here rejects, defaults or coalesces either value.
 *
 * Values beyond the safe integer range are refused rather than silently rounded, because
 * a rounded key compares equal to a key it is not.
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
 * Whether the caller holds administration of the tenant, as far as the client can tell.
 *
 * ⚠ READ FROM ONE AUTHORITY AND NEVER RE-DERIVED HERE. `AuthStore.administersCurrentPortal`
 * answers this for the whole application, from the fact the SERVER derived — the tenant's
 * own `Portals.AdministratorRoleId` designation evaluated against the caller's live role
 * assignments — plus the host-account arm the enforcing policy also takes.
 *
 * A previous revision of this file answered it locally, by testing the caller's role list
 * for the literal name `Administrators`. That was wrong three times over and it refused
 * legitimate administrators: the designation is a per-tenant column naming whichever role
 * confers administration, so it is fixed to no name; the role name is an ordinary updatable
 * column, so renaming the role stripped every administrator of their access to every route
 * gated here; and a role of the same name may belong to a different tenant. The role name
 * no longer appears anywhere in this file, and it must not be reintroduced.
 *
 * MIGRATION: the target rule is the INTERSECTION of the three legacy tests quoted at the
 * top of this file, and none of their individual quirks. All three refuse a caller who is
 * neither a host account nor a portal administrator, so that much is behaviour worth
 * preserving; they then disagree about whether a host account is required, admitted or
 * excluded, and that disagreement is deliberately not reproduced.
 *
 * @param authStore The identity projection.
 * @returns True when the caller is a host account or administers the resolved tenant.
 */
function holdsPortalAdministration(authStore: AuthStore): boolean {
  return authStore.administersCurrentPortal();
}

/**
 * Whether the caller IS the account a route names.
 *
 * The client can answer this one exactly, because it is a comparison of two identifiers it
 * already holds rather than a question about stored records. The API asks the same
 * question of the same two values — the token's subject against the route's `userId`
 * (`PortalAdministratorAuthorizationHandler.cs:L246-L270`) — and then additionally
 * requires the account to be bound to the tenant, which the client cannot check and does
 * not attempt to.
 *
 * ⚠ AN UNPARSEABLE OR ABSENT IDENTIFIER IS NOT OWNERSHIP. Both return false rather than
 * throwing or defaulting, because a route segment that does not denote an account cannot
 * denote THIS account. A null identity likewise: an unresolved identity owns nothing, and
 * the caller of this function is responsible for not asking until the identity is known.
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
 * Whether the client can already see that the caller cannot use the screen.
 *
 * ONE COARSE CONVENIENCE CHECK PER POLICY, AND EXPLICITLY NOT AN AUTHORISATION ENGINE.
 * Each arm answers a question from the identity the client already holds, or declines to
 * answer at all. Nothing here fetches, and nothing here interprets a stored permission
 * record.
 *
 * ⚠ THE FOUR RECORD-SCOPED POLICIES ARE NOT EVALUATED, and that is the load-bearing
 * omission. Answering a module or page policy would mean fetching and interpreting the
 * permission records held against that one record, which is exactly the second
 * authorisation engine this file must not become. They are admitted and left to the
 * server, which is the only party holding the records.
 *
 * MIGRATION: the legacy access-record gate is not reproduced in EITHER of its two forms,
 * because the two disagree with each other. The collection form of the module check at
 * `Library/Components/Security/Permissions/ModulePermissionController.vb:L33-L50` compares
 * the permission key WITHOUT consulting the record's allow flag, while the same file's
 * L243 requires the flag as well, and the tab controller splits the same way at L41
 * against L218 and L309. Reproducing one half would embed a defect and reproducing both is
 * impossible, so the record-level question is left entirely to the API, which holds the
 * records and resolves it once.
 *
 * MIGRATION: the delimited role string and the bracketed pseudo-role are not carried
 * forward. The legacy evaluator flattened grants into a semicolon-delimited string and
 * encoded a per-account grant as a bracketed identifier inside it, and that bracketed form
 * was an evaluation INPUT rather than a display format — `ModulePermissionController.vb:L42`
 * feeds it straight into `PortalSecurity.IsInRoles`, which splits on the delimiter at
 * `PortalSecurity.vb:L124`. Nothing here parses, builds or reproduces either
 * representation; a per-account grant is a first-class nullable identifier server-side.
 *
 * MIGRATION: no negation concept is modelled, because none exists to model. Measured
 * across `Library/Components/Security/`, a leading-bang role prefix, a prefix test and a
 * prefix strip all occur zero times, and so does any mention of denial. This generation of
 * the product grants and never revokes, so a role either appears in a grant or does not.
 *
 * Written as an exhaustive switch for the same reason {@link scopeParamName} is: a TENTH
 * policy must not silently inherit "the client cannot tell", because that is indeed the
 * safe default but it is also the answer that hides an omission. The ninth,
 * `PortalContentEditor`, does inherit that answer - but it is STATED below, with the measured
 * reason, rather than fallen into.
 *
 * @param policy A registered policy name.
 * @param scopeId The resolved scope identifier, or null when the policy needs none.
 * @param authStore The identity projection, already known to be resolved.
 * @returns True only when the client can see the caller plainly cannot use the screen.
 */
function isPlainlyRefused(
  policy: PermissionPolicy,
  scopeId: string | null,
  authStore: AuthStore,
): boolean {
  switch (policy) {
    case 'ModuleView':
    case 'ModuleEdit':
    case 'TabView':
    case 'TabEdit':
      return false;

    case 'PortalAdministrator':
      return holdsPortalAdministration(authStore) === false;

    /*
     * Answered from the host flag ALONE, with no tenant reasoning of any kind, because the
     * policy has no portal binding by design. Reading a portal here — the browsed tenant,
     * the token's tenant, anything — would reintroduce the very question
     * `PolicyNames.cs:L120-L130` says this policy exists to avoid asking.
     */
    case 'HostAdministrator':
      return authStore.isSuperUser() === false;

    /*
     * ⚠ NO ADMINISTRATOR ARM, DELIBERATELY. This is the credential change, and
     * `PolicyNames.cs:L145-L152` records why admitting an administrator would be wrong: it
     * would collapse the change and the reset into one operation whose effect depended on
     * which fields were populated — the shape that previously allowed a credential to be
     * overwritten with no proof of entitlement at all. An administrator who must intervene
     * uses the reset, which carries portal administration and is recorded as its own act.
     */
    case 'AccountOwner':
      return isTheNamedAccount(authStore, scopeId) === false;

    /*
     * Both arms, in the same order the server evaluates them: ownership first, then the
     * administrator of the account's portal. Refused only when NEITHER holds.
     */
    case 'AccountOwnerOrPortalAdministrator':
      return (
        isTheNamedAccount(authStore, scopeId) === false &&
        holdsPortalAdministration(authStore) === false
      );

    /*
     * ⚠ NEVER REFUSED HERE, AND NOT FOR WANT OF A FACT THAT LOOKS USABLE. The session
     * projection carries an advisory `permissions` list, so `EDIT` being absent from it looks
     * like grounds for a plain refusal. It is not, and the reason is measured rather than
     * cautious: `PermissionEvaluator.ListEffectivePortalPermissionKeysAsync` builds that list
     * from GRANT ROWS ALONE and has no administrator arm, while the policy's server-side
     * handler asks its administration question FIRST
     * (`PermissionService.HasAnyTabPermissionInPortalAsync`). A tenant administrator whose
     * pages carry no explicit grants therefore holds the policy and does NOT carry the key,
     * so refusing on the key would lock the tenant's own administrator out of the create
     * screen. The list is also a superset in the other direction — it aggregates MODULE grants
     * as well as page grants, where the policy asks only about pages — so it is wrong in both
     * directions at once and is not the question this policy asks.
     *
     * Grouped with the four item-scoped grant policies for that reason: the honest client
     * answer is "cannot tell", the server re-evaluates against stored state on every request,
     * and the screen reports the refusal if one comes.
     */
    case 'PortalContentEditor':
      return false;
  }
}

/**
 * Admits a caller who may plausibly use the screen a route declares a policy for, and
 * refuses everyone else at warning severity.
 *
 * A FUNCTION AND NOT A CLASS, for the same reasons `core/guards/auth.guard.ts` is one:
 * the class-based activation contract is deprecated in this generation of the router,
 * and a functional gate needs no provider, no decorator and no registration. The router
 * calls it inside an injection context, which is what makes {@link inject} legal in the
 * body.
 *
 * FULLY SYNCHRONOUS, BY DESIGN. It returns a boolean or a redirect — never an observable
 * and never a promise — because it performs no work that could be asynchronous. No
 * request is issued, no token is renewed and no clock is read, so every navigation
 * resolves immediately and there is no window in which a half-decided gate is observable.
 *
 * IT FAILS CLOSED AT EVERY STEP. A route that declares no policy, declares something
 * that is not a name, declares an unregistered name, or declares a scoped policy without
 * a scope is REFUSED rather than waved through. The legacy code seeded this posture: the
 * sign-in handler at
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L163` opened with
 * `Dim loginStatus As UserLoginStatus = UserLoginStatus.LOGIN_FAILURE`, so the default
 * outcome was refusal and only an affirmative result displaced it.
 *
 * @param route The activated route snapshot, read for the declared policy and the scope
 * identifier the policy applies to.
 * @param state The router state, whose `url` is the full attempted address.
 * @returns `true` to admit, `false` to refuse, or a redirect to the sign-in route.
 */
export const permissionGuard: CanActivateFn = (route, state) => {
  const router = inject(Router);
  const authStore = inject(AuthStore);
  const notification = inject(NotificationService);

  /*
   * Authentication is settled first, and settled the same way the companion gate settles
   * it, so a route carrying both gates cannot produce two different answers to the one
   * question. The store's verdict is read rather than recomputed: re-deciding it here
   * would put two answers in the application.
   *
   * A redirect rather than a refusal, and deliberately WITHOUT a notification. The
   * redirect is itself the affordance — the caller lands on the screen that resolves the
   * problem — and pairing it with a warning would announce a failure at the exact moment
   * the application is already doing the helpful thing. It is built and returned rather
   * than performed, so the router replaces the in-flight navigation atomically instead of
   * racing a second one against it, and the attempted address survives under
   * {@link RETURN_URL_KEY} so the caller resumes where they were aiming.
   */
  if (authStore.isAuthenticated() === false) {
    return router.createUrlTree([SIGN_IN_ROUTE], {
      queryParams: { [RETURN_URL_KEY]: state.url },
    });
  }

  /*
   * The declared policy is widened to `unknown` before it is examined, which is the
   * load-bearing detail of this whole file. The router types route data as an index
   * signature onto `any`, so the property arrives with every compile-time guarantee
   * switched off: it could be undefined on a route that forgot the key, or a number, or
   * an object, and none of that would be caught. Annotating the binding as `unknown`
   * discards that false confidence and forces the narrowing below to be written out.
   * Bracket access is not a style choice either — `noPropertyAccessFromIndexSignature`
   * is enabled, so reading the key as a property would not compile.
   *
   * MIGRATION: the question this replaces was asked imperatively, per page, in a load
   * handler — `Portals.ascx.vb:L339-L341` and `ModuleSettings.ascx.vb:L191-L193` are the
   * canonical shapes, and each one navigated away by side effect. It is now declared as
   * data on the route and answered in one place, so the set of guarded screens is
   * readable from the route table instead of having to be discovered by reading every
   * screen's implementation.
   */
  const declared: unknown = route.data[POLICY_DATA_KEY];

  /*
   * MIGRATION: refused rather than admitted when the declaration is unusable, and the
   * asymmetry is deliberate. An unregistered policy name is not a tidy 403 waiting to
   * happen: the API registers no provider that could manufacture a policy on demand, so
   * the name would fail while the request was being authorised rather than producing an
   * orderly refusal at the endpoint. Blocking here costs a correctly configured route
   * nothing and spares a mis-configured one an obscure server-side fault, so it is
   * strictly the safer default. The legacy code seeded exactly this posture: the sign-in
   * handler at `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L163`
   * opened with `Dim loginStatus As UserLoginStatus = UserLoginStatus.LOGIN_FAILURE`, so
   * refusal was the default outcome and only an affirmative result displaced it.
   */
  /*
   * ⚠ THIS GATE DELIBERATELY DOES NOT TOUCH FOCUS, and a reported defect saying it should was
   * investigated and disproved. Runtime testing found `document.activeElement === document.body`
   * after every permission denial and concluded that "focus is not returned to the trigger".
   *
   * Re-measured with focus genuinely on a real sidebar link — reached by six actual `Tab`
   * presses, with `:focus-visible` matching and the element captured by reference beforehand — a
   * refusal leaves focus EXACTLY where it was: `activeElement === savedLinkReference` compares
   * `true` by identity, `activeElement === document.body` is false, and ZERO `focusin` events
   * fire. The reported result was then reproduced as a controlled counterfactual by blurring to
   * `body` first, which is what a scripted or address-typed arrival produces. It is an artefact of
   * having no trigger, not a behaviour of this gate.
   *
   * Moving focus here would also be the wrong fix rather than a missing one. A refused navigation
   * CANCELS: the operator stays on the screen they were already using, so their place in it is
   * still valid and taking focus away from it would lose that place. The refusal reaches them
   * through a polite live region instead, which is the mechanism a status message is supposed to
   * use — announced without stealing focus. Escalating it to an assertive region was considered
   * and rejected on the same authority that fixes the severity: `AccessDenied.ascx.vb` presents
   * both of its branches as `YellowWarning`, so a refusal is a warning and not a fault.
   */
  if (typeof declared !== 'string' || isPermissionPolicy(declared) === false) {
    notification.notify(
      'warning',
      ACCESS_REFUSED_MESSAGE,
      null,
      false,
      // ⚠ THIS REFUSAL RETIRES ITSELF, AND IT IS THE ONE NOTIFICATION IN THE APPLICATION THAT SAYS SO.
      // The surface exempts `'warning'` from its countdown on three grounds - the outcome reports
      // something that did not happen, it frequently carries a support reference to quote, and removing
      // it would destroy the only record of a failure - and this refusal meets none of them. Nothing
      // failed, so there is no reference and none is passed above; and nothing is being asked of the
      // operator, because the remedy is a permission they do not hold and cannot grant themselves. A
      // browser audit measured the exemption applying anyway: the refusal stood for four minutes and
      // forty-two seconds and was cleared only by navigating away. The severity is deliberately left as
      // a warning - `AccessDenied.ascx.vb` presents both of its branches as `YellowWarning`, which is the
      // same authority the paragraph above cites - so the lifetime is stated as its own fact rather than
      // bought by understating the outcome.
      true,
    );

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, and without this the refusal would be invisible.
    // The shell discards stale notifications on a completed navigation, and this refusal is
    // raised DURING the very navigation that follows it — so the message would be queued and
    // swept inside one task, moving the operator with no explanation at all. Exempting it
    // survives exactly that one navigation and no further.
    notification.retainAcrossNavigation();

    return false;
  }

  /*
   * A scoped policy without a scope is refused for the same reason. The API authorises the
   * module, page and account policies against a specific record, so a route that names one
   * of them without supplying an identifier does not describe an answerable question. The
   * server reaches the same conclusion from the other side and says so in a log line:
   * `PermissionAuthorizationHandler.cs:L273-L283` refuses such a route as "a registration
   * mistake, not a permission decision", on the reasoning that the alternative "would be to
   * invent a key and grant against whatever it happened to match".
   *
   * The two unscoped policies take this branch too and pass it, because
   * {@link scopeParamName} reports that neither needs a scope — portal administration
   * because the API resolves the tenant itself, host administration because it has no
   * portal binding at all.
   *
   * The identifier is resolved ONCE and carried forward, rather than resolved here for the
   * presence test and again below for the comparison. Two resolutions of the same value are
   * two opportunities for them to differ.
   */
  const scopeName = scopeParamName(declared);
  const scopeId = scopeName === null ? null : resolveScopeId(route, scopeName);

  if (scopeName !== null && scopeId === null) {
    notification.notify(
      'warning',
      ACCESS_REFUSED_MESSAGE,
      null,
      false,
      // ⚠ THIS REFUSAL RETIRES ITSELF, AND IT IS THE ONE NOTIFICATION IN THE APPLICATION THAT SAYS SO.
      // The surface exempts `'warning'` from its countdown on three grounds - the outcome reports
      // something that did not happen, it frequently carries a support reference to quote, and removing
      // it would destroy the only record of a failure - and this refusal meets none of them. Nothing
      // failed, so there is no reference and none is passed above; and nothing is being asked of the
      // operator, because the remedy is a permission they do not hold and cannot grant themselves. A
      // browser audit measured the exemption applying anyway: the refusal stood for four minutes and
      // forty-two seconds and was cleared only by navigating away. The severity is deliberately left as
      // a warning - `AccessDenied.ascx.vb` presents both of its branches as `YellowWarning`, which is the
      // same authority the paragraph above cites - so the lifetime is stated as its own fact rather than
      // bought by understating the outcome.
      true,
    );

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, and without this the refusal would be invisible.
    // The shell discards stale notifications on a completed navigation, and this refusal is
    // raised DURING the very navigation that follows it — so the message would be queued and
    // swept inside one task, moving the operator with no explanation at all. Exempting it
    // survives exactly that one navigation and no further.
    notification.retainAcrossNavigation();

    return false;
  }

  /*
   * THE COARSE CONVENIENCE CHECK, whose entire reasoning — what each policy asks, what is
   * deliberately not asked, and which legacy behaviours are and are not carried forward —
   * lives on {@link isPlainlyRefused} rather than being restated here.
   *
   * ⚠ GATED ON THE IDENTITY ACTUALLY BEING RESOLVED, which prevents a real defect rather
   * than guarding against a hypothetical one. A session can be held while the identity
   * behind it is not yet known, and on that evidence a genuine administrator reports no
   * administration and a genuine account holder reports no key at all — so refusing there
   * would lock the very operators these screens exist for out of them, and would refuse an
   * account holder its own credential change. While the identity is unresolved the caller is
   * admitted and the server decides, which is the same posture this file takes everywhere
   * else it lacks information.
   *
   * ⚠ WHY THE APPLICATION DOES NOT NORMALLY SIT IN THAT WINDOW, recorded because the
   * alternative reading — that these decisions are routinely taken against a blank identity
   * — would be alarming and is untrue. Both paths that establish a session are TWO requests:
   * `AuthStore.login` and the renewal each chain the current-user read onto the credential
   * exchange and store the EXPANDED identity, precisely because the credential responses
   * carry an authority-minimised one. So by the time any navigation is gated, the identity in
   * hand is the server's full answer. The unresolved case remains handled because it is
   * reachable — an identity read that failed, or one belonging to a superseded session — not
   * because it is the normal state.
   *
   * The store's own permission-KEY projection is deliberately NOT consulted: those four
   * persisted keys are the other vocabulary, they cannot express a policy, and the store
   * documents itself as deciding nothing with them. The administration projection read by
   * {@link holdsPortalAdministration} is a different thing entirely — it is the server's own
   * verdict on the one question this gate is entitled to ask.
   */
  if (authStore.currentUser() !== null && isPlainlyRefused(declared, scopeId, authStore)) {
    /*
     * MIGRATION: a refusal cancels the navigation and says so, rather than redirecting. The
     * legacy `Response.Redirect(NavigateURL("Access Denied"), True)` had a page to send the
     * browser to; there is deliberately no not-authorised route in this application's route
     * table, so there is nowhere equivalent to go. Announcing the refusal and leaving the
     * caller where they are achieves the same outcome the legacy redirect did — the guarded
     * screen does not render — without inventing a route or rewriting the address bar, which
     * would make a refused navigation indistinguishable from a deliberate one.
     *
     * Raised at WARNING severity, matching `AccessDenied.ascx.vb`, which presented the
     * refusal with a yellow warning on both of its branches. A refusal is an expected outcome
     * of asking for something one cannot have, not a fault, and escalating it to error
     * severity would misreport it.
     */
    notification.notify(
      'warning',
      ACCESS_REFUSED_MESSAGE,
      null,
      false,
      // ⚠ THIS REFUSAL RETIRES ITSELF, AND IT IS THE ONE NOTIFICATION IN THE APPLICATION THAT SAYS SO.
      // The surface exempts `'warning'` from its countdown on three grounds - the outcome reports
      // something that did not happen, it frequently carries a support reference to quote, and removing
      // it would destroy the only record of a failure - and this refusal meets none of them. Nothing
      // failed, so there is no reference and none is passed above; and nothing is being asked of the
      // operator, because the remedy is a permission they do not hold and cannot grant themselves. A
      // browser audit measured the exemption applying anyway: the refusal stood for four minutes and
      // forty-two seconds and was cleared only by navigating away. The severity is deliberately left as
      // a warning - `AccessDenied.ascx.vb` presents both of its branches as `YellowWarning`, which is the
      // same authority the paragraph above cites - so the lifetime is stated as its own fact rather than
      // bought by understating the outcome.
      true,
    );

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, and without this the refusal would be invisible.
    // The shell discards stale notifications on a completed navigation, and this refusal is
    // raised DURING the very navigation that follows it — so the message would be queued and
    // swept inside one task, moving the operator with no explanation at all. Exempting it
    // survives exactly that one navigation and no further.
    notification.retainAcrossNavigation();

    return false;
  }

  /*
   * MIGRATION: admitted, and NOT declared authorised. Reaching this line means the route
   * declares a registered policy, the scope that policy needs is present, and nothing the
   * client can see contradicts the caller's right to be here. It does NOT mean the
   * request will succeed. The API re-authorises every policy-protected endpoint against
   * stored state and answers 403 on its own account, `core/interceptors/error.interceptor.ts`
   * surfaces that refusal at warning severity to match this file, and that server-side
   * verdict is the authoritative one. No decision reached here is cached, so a change in
   * the caller's roles takes effect on the next navigation rather than persisting until
   * something is invalidated.
   */
  return true;
};
