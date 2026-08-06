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

import { NotificationService } from '../services/notification.service';
import { AuthStore } from '../state/auth.store';

/**
 * The authorisation policy names the API registers, and the complete set of values a
 * route may declare.
 *
 * ⚠ CLOSED AT FIVE. A name outside this list is not merely unrecognised — it is a
 * FAULT, and a louder one than it looks. The API registers no policy provider that
 * could invent a policy on demand, so an unregistered name does not produce a tidy
 * refusal at the endpoint; it throws while the request is being authorised. Refusing
 * such a route here, before anything is sent, is therefore strictly safer than
 * forwarding it, which is why {@link permissionGuard} fails closed rather than
 * shrugging and admitting.
 *
 * Declared as a `const` tuple rather than an enumeration for two reasons. A tuple is
 * the single source of BOTH the runtime list membership is tested against and the
 * compile-time type derived from it, so the two can never drift; and `isolatedModules`
 * is enabled in `tsconfig.json`, which rules out a constant enumeration outright.
 *
 * Of the five, portal, account, role and role-group administration all resolve to
 * portal administration, and module administration resolves to module editing. The two
 * tab policies are legal and currently unreferenced: no tab route exists in
 * `app.routes.ts`, and `core/services/tab.service.ts` is a lookup for the module
 * screens rather than a feature of its own. They are retained because the API
 * registers them, so a tab route added later needs no change here.
 */
const PERMISSION_POLICIES = [
  'ModuleView',
  'ModuleEdit',
  'TabView',
  'TabEdit',
  'PortalAdministrator',
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

/**
 * The route an unauthenticated caller is redirected to.
 *
 * Deliberately a private copy of the value `core/guards/auth.guard.ts` declares rather
 * than an import of it: that module does not export the constant, and the two gates
 * agreeing by construction matters less than neither gate reaching into the other. A
 * rename on either side is caught by the specifications, which spell the path again.
 */
const SIGN_IN_ROUTE = '/login';

/**
 * The query parameter key carrying the address the caller was trying to reach.
 *
 * Identical to the key `core/guards/auth.guard.ts` uses, so the sign-in screen reads
 * one key whichever gate turned the caller away.
 */
const RETURN_URL_KEY = 'returnUrl';

/**
 * The portal administrator role name, spelled exactly as the product creates it.
 *
 * ⚠ PLURAL, AND MATCHED EXACTLY. `Library/Components/Portal/PortalController.vb:L1390`
 * creates it as `CreateRole(PortalId, "Administrators", "Portal Administrators", …)`,
 * so `Administrators` is the role NAME and `Portal Administrators` is merely its
 * description. The legacy comparison is `=` under Visual Basic's default binary
 * comparison, which is case-sensitive, so this is compared with exact string equality
 * and never case-folded.
 */
const PORTAL_ADMINISTRATOR_ROLE = 'Administrators';

/**
 * The parameter names a module-scoped policy is resolved from, in precedence order.
 *
 * Mirrors the API's own resolution, which tries the explicit name first and the bare
 * one second. Both are listed because the gate and the endpoint must agree on which
 * record is being authorised; a gate that resolved a different scope than the server
 * would admit navigation to a screen the server then refuses, for a reason no operator
 * could see.
 */
const MODULE_SCOPE_PARAMS = ['moduleId', 'id'] as const;

/**
 * The parameter names a tab-scoped policy is resolved from, in precedence order.
 *
 * The same arrangement as {@link MODULE_SCOPE_PARAMS}, and the same reason.
 */
const TAB_SCOPE_PARAMS = ['tabId', 'id'] as const;

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
 * One constant serves all three refusal branches. They differ in CAUSE — a
 * mis-declared policy, an unresolvable scope, and a caller who lacks portal
 * administration — but not in what the person reading the screen can do about it, and
 * the legacy presented every refusal with a single sentence too.
 */
const ACCESS_REFUSED_MESSAGE = 'You do not have access to this content.';

/**
 * Whether a string is one of the five registered policy names.
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
 * The parameter names a policy's scope is resolved from, or null when it needs none.
 *
 * Portal administration is the one policy with no scope to resolve: it is a question
 * about the caller within the tenant the API resolves from the request itself, not a
 * question about one record. The two module policies and the two tab policies each
 * identify a specific record, so each carries its own name list.
 *
 * Written as an exhaustive switch over the narrowed union rather than a lookup object,
 * so adding a sixth policy to {@link PERMISSION_POLICIES} fails to compile here until
 * its scope requirement is stated. That is the intended behaviour: an unhandled policy
 * silently defaulting to "needs no scope" would quietly stop requiring the very scope
 * the API is about to demand.
 *
 * @param policy A registered policy name.
 * @returns The parameter names to try in order, or null when no scope is required.
 */
function scopeParamNames(policy: PermissionPolicy): readonly string[] | null {
  switch (policy) {
    case 'ModuleView':
    case 'ModuleEdit':
      return MODULE_SCOPE_PARAMS;
    case 'TabView':
    case 'TabEdit':
      return TAB_SCOPE_PARAMS;
    case 'PortalAdministrator':
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
 * PRECEDENCE IS BY NAME, NOT BY DEPTH. The outer loop is the name list and the inner
 * loop is the ancestry, so the explicit name is exhausted across every ancestor before
 * the bare name is tried anywhere. That is the order the API resolves in, and matching
 * it is the whole point: were depth to dominate, a route carrying an unrelated bare
 * identifier on a nearer ancestor would win over the explicit one further up and the two
 * sides would authorise different records.
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
 * @param names The parameter names to try, in precedence order.
 * @returns The first identifier found, or null when the route supplies none.
 */
function resolveScopeId(route: ActivatedRouteSnapshot, names: readonly string[]): string | null {
  const deepestFirst = [...route.pathFromRoot].reverse();

  for (const name of names) {
    for (const snapshot of deepestFirst) {
      const value = snapshot.paramMap.get(name);

      if (value !== null && value.length > 0) {
        return value;
      }
    }
  }

  return null;
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
  if (typeof declared !== 'string' || isPermissionPolicy(declared) === false) {
    notification.notify('warning', ACCESS_REFUSED_MESSAGE);

    return false;
  }

  /*
   * A scoped policy without a scope is refused for the same reason. The API authorises
   * the module or tab policies against a specific record, so a route that names one of
   * them without supplying an identifier does not describe an answerable question — the
   * server would either resolve some unrelated record or fault outright.
   *
   * Portal administration takes this branch too and passes it, because
   * {@link scopeParamNames} reports that it needs no scope. That is not a special case
   * bolted on: it is the tenant-wide policy, and the tenant is resolved by the API from
   * the request rather than named in the route.
   */
  const scopeNames = scopeParamNames(declared);

  if (scopeNames !== null && resolveScopeId(route, scopeNames) === null) {
    notification.notify('warning', ACCESS_REFUSED_MESSAGE);

    return false;
  }

  /*
   * ONE COARSE CONVENIENCE CHECK, AND EXPLICITLY NOT AN AUTHORISATION ENGINE.
   *
   * It applies to portal administration ONLY. The module and tab policies are
   * deliberately not evaluated here at all: answering them would mean fetching and
   * interpreting the permission records held against a specific module or tab, which is
   * exactly the second authorisation engine this file must not become. Those policies are
   * admitted and left to the server, which is the only party holding the records.
   *
   * MIGRATION: this reproduces the INTERSECTION of the three legacy tests quoted at the
   * top of this file, and none of their individual quirks. All three refuse a caller who
   * is neither a host account nor a portal administrator, so that much is behaviour worth
   * preserving; they then disagree about whether a host account is required, admitted or
   * excluded, and that disagreement is deliberately not reproduced.
   *
   * MIGRATION: the legacy access-record gate is not reproduced either, in EITHER of its
   * two forms, because the two disagree with each other. The collection form of the module
   * check at `Library/Components/Security/Permissions/ModulePermissionController.vb:L33-L50`
   * compares the permission key WITHOUT consulting the record's allow flag, while the same
   * file's L243 requires the flag as well, and the tab controller splits the same way at
   * L41 against L218 and L309. Reproducing one half would embed a defect and reproducing
   * both is impossible, so the record-level question is left entirely to the API, which
   * holds the records and resolves it once.
   *
   * MIGRATION: the delimited role string and the bracketed pseudo-role are not carried
   * forward. The legacy evaluator flattened grants into a semicolon-delimited string and
   * encoded a per-account grant as a bracketed identifier inside it, and that bracketed
   * form was an evaluation INPUT rather than a display format — `ModulePermissionController.vb:L42`
   * feeds it straight into `PortalSecurity.IsInRoles`, which splits on the delimiter at
   * `PortalSecurity.vb:L124`. Nothing here parses, builds or reproduces either
   * representation; a per-account grant is a first-class nullable identifier server-side.
   *
   * MIGRATION: no negation concept is modelled, because none exists to model. Measured
   * across `Library/Components/Security/`, a leading-bang role prefix, a prefix test and
   * a prefix strip all occur zero times, and so does any mention of denial. This
   * generation of the product grants and never revokes, so a role either appears in a
   * grant or does not.
   *
   * ⚠ GATED ON THE IDENTITY ACTUALLY BEING RESOLVED, which prevents a real defect rather
   * than guarding against a hypothetical one. The store reports a held session from the
   * token custodian, but derives the role list and the host-account flag from a fetched
   * identity that is null until it arrives. Between those two moments a genuine portal
   * administrator reports an empty role list and a false host-account flag, so refusing
   * on that evidence would lock the very operators this screen exists for out of it.
   * While the identity is unresolved the caller is admitted and the server decides, which
   * is the same posture this file takes everywhere else it lacks information.
   *
   * The role list is matched with exact string equality and the host-account flag is read
   * as the plain boolean it is — `false` is DATA here, not absence. The store's own
   * permission projection is deliberately NOT consulted: it documents itself as deciding
   * nothing, and honouring that is what keeps the authoritative verdict in one place.
   */
  if (declared === 'PortalAdministrator' && authStore.currentUser() !== null) {
    const holdsPortalAdministration =
      authStore.isSuperUser() || authStore.roles().includes(PORTAL_ADMINISTRATOR_ROLE);

    if (holdsPortalAdministration === false) {
      /*
       * MIGRATION: a refusal cancels the navigation and says so, rather than redirecting.
       * The legacy `Response.Redirect(NavigateURL("Access Denied"), True)` had a page to
       * send the browser to; there is deliberately no not-authorised route in this
       * application's route table, so there is nowhere equivalent to go. Announcing the
       * refusal and leaving the caller where they are achieves the same outcome the legacy
       * redirect did — the guarded screen does not render — without inventing a route or
       * rewriting the address bar, which would make a refused navigation
       * indistinguishable from a deliberate one.
       *
       * Raised at WARNING severity, matching `AccessDenied.ascx.vb`, which presented the
       * refusal with a yellow warning on both of its branches. A refusal is an expected
       * outcome of asking for something one cannot have, not a fault, and escalating it to
       * error severity would misreport it.
       */
      notification.notify('warning', ACCESS_REFUSED_MESSAGE);

      return false;
    }
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

