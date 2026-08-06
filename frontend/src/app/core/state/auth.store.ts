/**
 * Authentication session state for the administration console, held as Angular
 * signals.
 *
 * This is the COMPOSITION POINT for authentication. `core/services/auth.service.ts`
 * is a typed transport wrapper closed at four operations and
 * `core/services/token-storage.service.ts` is the session's custodian; neither
 * sequences a multi-step flow, and neither holds the one piece of state the legacy
 * sign-in screen genuinely kept between attempts. Both are injected here and
 * nothing here is injected back into either, so the graph stays acyclic.
 *
 * Minimal Change Clause item 5 confines a service to API communication, which is
 * what puts command sequencing, the loading and failure slices, and the
 * verification ladder's progression in this file rather than in a service or a
 * component.
 *
 * ## What this store deliberately does NOT do
 *
 * - **It builds no HTTP.** No transport type is imported, no URL is constructed and
 *   no endpoint table or environment file is read. Every request is made by the
 *   injected service.
 * - **It holds no token.** Not in a signal, not in a field, not in a log. Custody
 *   belongs to the storage service, which keeps the session in memory only. Every
 *   token-derived fact below is projected from that service's own signals, so there
 *   is exactly one copy of a credential in the application.
 * - **It does not orchestrate renewal on a refused request.** That policy belongs to
 *   `core/interceptors/auth.interceptor.ts`, which is referenced by path and never
 *   imported. {@link AuthStore.refreshSession} exists so a caller *may* renew
 *   deliberately; the retry policy itself is not this file's.
 * - **It presents nothing.** No value is formatted for display, no message is built
 *   from a template, and no markup is produced or stored. Wording belongs to
 *   `core/utils/form-errors.util.ts`; this store keeps the STRUCTURED problem
 *   document and the classifications derived from it.
 * - **It decides no authorisation question.** Roles and permission keys are held so a
 *   screen can avoid offering an action the server would refuse. The server
 *   re-authorises every request and answers 403; nothing here may stand in for that.
 * - **It caches nothing.** No map, no time-to-live, no expiry bookkeeping of its own
 *   and no staleness flag.
 *
 * ## Provenance
 *
 * The behaviour reproduced here comes from
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197`, whose
 * sign-in handler is the only place in the legacy application where the
 * verification ladder existed. Supporting sources are
 * `Library/Components/Users/Membership/UserLoginStatus.vb:L23-L31` for the outcome
 * vocabulary, `Library/Components/Shared/Null.vb:L36-L85` for the sentinel contract
 * that governs every guard below, `Website/admin/Security/AccessDenied.ascx.vb:L41-L47`
 * for the severity a refusal is presented at, and `Website/release.config` for the
 * session lifetime and the credential policy.
 *
 * MIGRATION: `ViewState` and `Session` are eliminated outright rather than
 * translated, and the measurement is why that costs nothing. Across the five
 * in-scope `Library/Components` trees there are exactly FOUR `ViewState` sites, all
 * carrying one key, all in `Library/Components/Users/UserModuleBase.vb` (L469, L491,
 * L498 and L503), and there are ZERO `Session(` sites anywhere in those trees or in
 * the 39 administrative code-behinds. There is consequently no general page-state
 * mechanism to port: no key/value bag, no serialise-and-rehydrate round trip and no
 * session-state analogue is introduced here. What DOES survive is a single fact that
 * happened to live in control state — see {@link AuthStore.verificationRequired}.
 *
 * MIGRATION: the legacy client-side cache is not reproduced. The legacy data layer
 * reached `Library/Components/Providers/Caching/DataCache.vb` from 116 in-scope call
 * sites with a static-read, multiplier-scaled-write and portal-wide-or-host-wide
 * invalidation idiom. Caching is a server concern behind an explicit abstraction in
 * the target; this store memoises nothing and every command issues its request.
 *
 * MIGRATION: implicit conversions are made explicit. The 39 legacy administrative
 * code-behinds compiled with strict type checking DISABLED
 * (`Website/release.config:L125`, `<compilation debug="false" strict="false">`), so
 * they could legally rely on late binding and silent narrowing. Strict TypeScript is
 * what forces each such coercion to surface; no member of this file is typed as an
 * escape hatch, no assertion suppresses a diagnostic, and every presence test below
 * is an explicit comparison rather than a truthiness test.
 */

import { Injectable, computed, inject, signal } from '@angular/core';
import type { Signal } from '@angular/core';
import { catchError, defer, finalize, tap, throwError } from 'rxjs';
import type { Observable } from 'rxjs';

import type { AuthSession, CurrentUser, LoginRequest } from '../models/auth.model';
import { isProblemDetails } from '../models/problem-details.model';
import type { ProblemDetails, ProblemDetailsErrors } from '../models/problem-details.model';
import { AuthService } from '../services/auth.service';
import type { LoginPortalSelector } from '../utils/http-params.util';
import { TokenStorageService } from '../services/token-storage.service';
import { SessionTeardownService } from './session-teardown.service';
import {
  failureCode,
  isAuthFailureCode,
  isValidationProblemDetails,
  problemSeverity,
  problemSupportReference,
  resolveVerificationPrompt,
} from '../utils/form-errors.util';
import type { ProblemSeverity, VerificationPrompt } from '../utils/form-errors.util';

/**
 * The operations this store can be part-way through.
 *
 * Declared as a frozen tuple with a derived union rather than as an enumeration.
 * The workspace compiles with isolated modules, under which a constant enumeration
 * is not permitted, and an ordinary enumeration would emit runtime JavaScript for a
 * vocabulary that never crosses a boundary. This is the same shape
 * `core/utils/form-errors.util.ts` uses for its own closed vocabularies.
 *
 * Exported so a consumer can name a phase without restating the literal.
 */
export const AUTH_STORE_PHASES = Object.freeze([
  /** No command is running. */
  'idle',
  /** {@link AuthStore.login} is in flight. */
  'authenticating',
  /** {@link AuthStore.refreshSession} is in flight. */
  'refreshing',
  /** {@link AuthStore.logout} is in flight. */
  'signingOut',
  /** {@link AuthStore.loadCurrentUser} is in flight. */
  'loadingIdentity',
] as const);

/** One of the phases in {@link AUTH_STORE_PHASES}. */
export type AuthStorePhase = (typeof AUTH_STORE_PHASES)[number];

/**
 * The status the rate limiter refuses a credential request with.
 *
 * MIGRATION: the legacy sign-in was guarded by an image-based human-verification
 * challenge — `Login.ascx.vb:L162` gated the ENTIRE handler on
 * `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha)`. That control
 * belongs to `Library/Controls/**`, a tree this migration excludes wholesale, so the
 * challenge is a deliberate functional reduction rather than an oversight. It is not
 * uncompensated: the named compensating control is a request rate limiter on the
 * credential endpoints, partitioned by calling address and applied by request path
 * so a newly written credential endpoint cannot silently escape it. Its permit count
 * and window are deployment settings and are deliberately NOT restated here.
 *
 * The consequence for this store is that a refusal for being early is an expected
 * outcome of {@link AuthStore.login} and is reported separately from a refused
 * credential — see {@link AuthStore.rateLimited}. It is a WARNING and not a fault,
 * because nothing has broken.
 */
const RATE_LIMITED_STATUS = 429;

/**
 * The signed-in session, the state of the verification ladder, and the outcome of
 * the last authentication command.
 *
 * Root-provided and injected with {@link inject}, so no component declares a
 * provider for it and every consumer shares one instance. That is required rather
 * than merely convenient: the ladder's revealed state must survive a component being
 * destroyed and recreated by navigation, which is exactly what the legacy control
 * state did across a postback.
 *
 * Every slice is a private writable signal exposed through a read-only projection,
 * so a component is structurally unable to mutate this store — the only way state
 * changes is a command method below. Updates replace values rather than mutating
 * them, which is what lets consumers using the on-push change-detection strategy
 * observe a change at all.
 *
 * No `effect()` appears anywhere in this file. An effect is for a genuine side
 * effect, and this store has none: every derivation is a `computed()` and every
 * state change is driven by a command. Using one to move data between slices would
 * make the data flow implicit and the ordering unpredictable.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly auth = inject(AuthService);
  private readonly tokenStorage = inject(TokenStorageService);

  /**
   * The fan-out that purges the domain stores when a session ends.
   *
   * Injected rather than reached for at the call site so that a test can substitute it, and
   * held here rather than having each domain store injected directly so that this file keeps
   * its one-way relationship with them — see {@link AuthStore.discardSession}.
   */
  private readonly sessionTeardown = inject(SessionTeardownService);

  // -------------------------------------------------------------------------
  // WRITABLE SLICES — private, without exception
  // -------------------------------------------------------------------------

  /** Which command, if any, is in flight. */
  private readonly _phase = signal<AuthStorePhase>('idle');

  /**
   * A ticket identifying the command that currently owns {@link _phase}.
   *
   * ## What this exists to prevent
   *
   * Every command below is a cold observable the CALLER subscribes to, and a caller is
   * usually a component. When that component is destroyed mid-flight — a navigation away
   * from the sign-in screen, a route change during an identity read — the subscription is
   * torn down and the observable is unsubscribed. Neither `tap` nor `catchError` runs on
   * unsubscription, so a phase set on subscribe was never returned to idle: the store stayed
   * `authenticating` or `loadingIdentity` FOREVER, and every consumer of the derived busy
   * projections stayed busy with it. A disabled submit button that never re-enables and a
   * progress indicator that never stops are the visible symptoms; the underlying fact is
   * that the store was reporting a command in flight that had ceased to exist.
   *
   * `finalize` fixes the "runs on cancellation" half, because it runs on completion, error
   * AND unsubscription alike. But `finalize` alone would introduce the mirror defect: a
   * cancelled command finalising LATE would reset a phase belonging to a command started
   * since, so an operator who abandoned a sign-in and immediately started another would see
   * the second one report idle while it was still running.
   *
   * This ticket is what separates the two. A command claims the phase by taking a fresh
   * ticket, and its `finalize` returns the store to idle ONLY if it still holds that ticket.
   * A superseded command's cleanup is therefore inert, which is exactly right — the command
   * that displaced it owns the phase and will return it to idle in its own time.
   *
   * Private with no projection: this is bookkeeping about the store's own concurrency and
   * nothing outside needs to observe it. A plain mutable counter rather than a signal,
   * because no derived state reads it and making it reactive would invite exactly that.
   */
  private phaseTicket = 0;

  /**
   * The problem document from the last failed command, or null.
   *
   * The STRUCTURED document, kept whole. It is deliberately not reduced to a
   * sentence here: the document carries the failure code, the per-field dictionary
   * and the support reference an operator needs, and collapsing it to text would
   * discard all three. Presentation reads what it needs through the projections
   * below.
   */
  private readonly _problem = signal<ProblemDetails | null>(null);

  /**
   * The transport status of the last failed command, or null when none failed.
   *
   * Held alongside {@link _problem} rather than read out of it, because the two are
   * not the same fact. RFC 7807 makes every member of a problem document optional
   * and a proxy between the browser and the API can answer with a body this
   * application never produced, whereas the transport status is always present. A
   * response that carried no document at all — a network failure, or an error page
   * from an intermediary — still has a status worth classifying.
   */
  private readonly _failureStatus = signal<number | null>(null);

  /**
   * Whether the last command failed.
   *
   * Held as its own slice rather than inferred from {@link _problem} and
   * {@link _failureStatus} being set, because a failure can carry NEITHER. The
   * authentication service refuses a renewal with a plain error before any request is
   * made when no refresh token is held, and a transport failure can arrive with no
   * body; in both cases there is no document and no status to record, yet the command
   * unquestionably failed. Inferring failure from the presence of its details would
   * report those cases as success.
   */
  private readonly _failed = signal(false);

  /**
   * Whether the verification field has been revealed.
   *
   * THIS IS THE ONE PIECE OF LEGACY CONTROL STATE THAT SURVIVES. See
   * {@link AuthStore.verificationRequired} for the full reasoning.
   */
  private readonly _verificationRequired = signal(false);

  /**
   * The outcome of the last turn of the verification ladder, or null when the
   * ladder has not been reached.
   */
  private readonly _verificationPrompt = signal<VerificationPrompt | null>(null);

  /**
   * The identity last read from the current-user endpoint, or null.
   *
   * Held in addition to the identity inside the stored session because the two
   * answer different questions. The stored session's copy is a snapshot taken when
   * the credentials were issued, so a role or permission granted afterwards does not
   * appear in it until the session is renewed. This slice is what
   * {@link AuthStore.loadCurrentUser} refreshes, and it takes precedence in
   * {@link AuthStore.currentUser}.
   *
   * Not a second copy of the session and not token custody: an identity projection
   * carries no credential of any kind.
   *
   * ⚠ STAMPED WITH THE AUTH EPOCH IT WAS PUBLISHED UNDER, AND HONOURED ONLY WHILE THAT
   * EPOCH IS CURRENT. An identity is a description of ONE session and has no meaning apart
   * from it, but this slice used to be an independent signal that could outlive or disagree
   * with the session the custodian held. Three concrete defects followed, all of them
   * visible:
   *
   * - after a SIGN-OUT the previous account's display name, roles and e-mail address stayed
   *   published, because clearing the custodian did not clear this;
   * - after an ACCOUNT SWITCH this slice could still hold the previous account while the new
   *   account's credentials were the ones being sent — and because
   *   {@link AuthStore.currentUser} PREFERS this slice, the wrong one won;
   * - after a RENEWAL this slice was left untouched, so a stale snapshot outranked the
   *   fresher copy the rotated session carried.
   *
   * The stamp closes all three at once, with no special case per path. The epoch advances on
   * every session transition, so any transition after publication invalidates the stamp, and
   * {@link AuthStore.currentUser} falls back to the copy inside the session actually held —
   * which is by construction the right one. Clearing the slice on sign-out is still done
   * explicitly as well, so the value is not merely ignored but gone.
   */
  private readonly _identity = signal<StampedIdentity | null>(null);

  // -------------------------------------------------------------------------
  // LIFECYCLE PROJECTIONS
  // -------------------------------------------------------------------------

  /** Which command, if any, is in flight. */
  readonly phase: Signal<AuthStorePhase> = this._phase.asReadonly();

  /** Whether any command is in flight. */
  readonly isBusy: Signal<boolean> = computed(() => this._phase() !== 'idle');

  /** Whether a sign-in is in flight. */
  readonly isAuthenticating: Signal<boolean> = computed(() => this._phase() === 'authenticating');

  /** Whether a session renewal is in flight. */
  readonly isRefreshing: Signal<boolean> = computed(() => this._phase() === 'refreshing');

  /** Whether a sign-out is in flight. */
  readonly isSigningOut: Signal<boolean> = computed(() => this._phase() === 'signingOut');

  /** Whether an identity read is in flight. */
  readonly isLoadingIdentity: Signal<boolean> = computed(
    () => this._phase() === 'loadingIdentity',
  );

  // -------------------------------------------------------------------------
  // SESSION PROJECTIONS
  // -------------------------------------------------------------------------

  /**
   * The signed-in identity, or null when nobody is signed in.
   *
   * Prefers the identity last read from the current-user endpoint and falls back to
   * the copy inside the stored session, so a deliberate refresh of a caller's roles
   * is visible without renewing the credentials. Resolved with an explicit `!== null`
   * comparison rather than a coalescing operator, because every guard in this file
   * tests presence explicitly.
   *
   * ⚠ THE PREFERENCE IS CONDITIONAL ON THE FETCHED IDENTITY BELONGING TO THE SESSION
   * CURRENTLY HELD, and that condition is what makes the preference safe. A fetched
   * identity outranks the session's own copy only while its stamp matches the live auth
   * epoch; the moment any session transition occurs the stamp is stale and the session's
   * copy takes over. The fallback is therefore not merely a default for the
   * never-fetched case — it is the correct answer whenever the fetched value describes a
   * session that is over or has been replaced, which is exactly when a stale identity
   * would otherwise have won.
   *
   * Reading the epoch here makes this projection recompute on every session transition,
   * which is intended: withdrawing a stale identity must be immediate rather than waiting
   * for something else to change.
   *
   * Null means NOT SIGNED IN. It never means "signed in with an empty identity",
   * which is the distinction {@link AuthStore.portalId} depends on.
   */
  readonly currentUser: Signal<CurrentUser | null> = computed(() => {
    const fetched = this._identity();
    const stored = this.tokenStorage.currentUser();

    if (fetched !== null && fetched.generation === this.tokenStorage.generation()) {
      return fetched.user;
    }

    return stored;
  });

  /**
   * Whether a session is held.
   *
   * Reports the PRESENCE of a session, not its validity, because that is what the
   * custodian reports and re-deciding it here would put two answers in the
   * application. An access token that has lapsed still yields true: the correct
   * response to expiry is to renew, which requires the session to still be here.
   */
  readonly isAuthenticated: Signal<boolean> = this.tokenStorage.isAuthenticated;

  /**
   * When the access token expires, exactly as the server stamped it, or null when no
   * session is held.
   *
   * An absolute instant in Coordinated Universal Time, passed through as the string
   * the contract publishes. No parse, no clock read and no lapsed-or-valid verdict is
   * performed here — the custodian owns that question and takes the instant to
   * compare against, so a verdict is never computed against some earlier moment.
   *
   * MIGRATION: the 60-minute access-token lifetime is deliberate parity with
   * `Website/release.config:L147`,
   * `<forms name=".DOTNETNUKE" protection="All" timeout="60" cookieless="UseCookies"/>`.
   * The number is a server-side configuration value and is deliberately not restated
   * on the client; only the instant the server stamped is published. Neither a date
   * library nor any decoding of the token is used to reach it.
   */
  readonly accessTokenExpiresAt: Signal<string | null> = this.tokenStorage.accessTokenExpiresAt;

  /**
   * The portal, or tenant, the caller is signed in to, or null when nobody is.
   *
   * ⚠ SENTINEL DISCIPLINE. `Portals.PortalID` is declared `IDENTITY(-1, 1)`, so minus
   * one is a REAL tenant key and the shipped default portal is inserted explicitly as
   * zero. Minus one is simultaneously the legacy encoding for a missing integer
   * (`Library/Components/Shared/Null.vb:L41-L45` returns `-1`, and L36-L40 returns the
   * same for the 16-bit case), so one value means both a real portal and "no portal".
   *
   * Absence is therefore expressed as `null` and NEVER as `0` or `-1`. Both of those
   * are DATA and are passed through untouched. A guard written as `if (portalId)` or
   * `portalId > 0` would reject two real tenants, which is why presence is decided by
   * testing the identity for null instead of testing the number for a magnitude.
   */
  readonly portalId: Signal<number | null> = computed(() => {
    const user = this.currentUser();

    return user === null ? null : user.portalId;
  });

  /**
   * The role names the caller holds in the resolved tenant, empty when nobody is
   * signed in.
   *
   * FOR RENDERING AFFORDANCES ONLY. The server re-authorises every request against
   * stored state and answers 403; a screen may use this to avoid offering an action
   * that would be refused, and nothing more.
   */
  readonly roles: Signal<readonly string[]> = computed(() => {
    const user = this.currentUser();

    return user === null ? EMPTY_STRINGS : user.roles;
  });

  /**
   * The permission keys the caller holds, empty when nobody is signed in.
   *
   * ⚠ THIS DECIDES NOTHING. The server is authoritative and refuses with 403.
   *
   * ⚠ TWO CLOSED, NON-INTERCHANGEABLE VOCABULARIES. The persisted permission keys are
   * `VIEW`, `EDIT`, `READ` and `WRITE`; the server's authorisation POLICY names are
   * `ModuleView`, `ModuleEdit`, `TabView`, `TabEdit` and `PortalAdministrator`. They
   * must never be conflated, and there is no deny prefix in this generation of the
   * product — nothing here parses one.
   *
   * MIGRATION: authorisation moved server-side. `Library/Components/Users/UserModuleBase.vb:L466-L505`
   * embedded a complete access check INSIDE a page property getter — an own-record
   * test at L474, a super-user test at L476, an administrator-role test at L479, a
   * nested super-user exclusion at L481-L487, and a redirect to the access-denied page
   * at L494 — so merely reading a property could navigate. None of that is
   * reimplemented here. Reading this signal has no side effect and reaches no verdict.
   */
  readonly permissions: Signal<readonly string[]> = computed(() => {
    const user = this.currentUser();

    return user === null ? EMPTY_STRINGS : user.permissions;
  });

  /**
   * Whether the caller holds a host, or super-user, account.
   *
   * ⚠ `false` IS DATA, not absence. In the legacy null contract the absence test
   * reported true for `false` itself — `Null.vb`'s `IsNull` treats `False`, `""`,
   * `-1`, `255`, the minimum date and the empty globally unique identifier all as
   * "not set" — so a legacy `false` and a legacy "unknown" were indistinguishable.
   * Every boolean on the wire is now a plain non-nullable boolean precisely because
   * admitting a third state would advertise a distinction the source data cannot
   * make. Nothing here treats `false` as missing.
   *
   * A host account widens the set of tenants reachable, not the set of operations
   * permitted within one, so this is not a substitute for a permission key.
   */
  /**
   * Whether the account administers the tenant it is signed in to.
   *
   * Read from the snapshot the server derived, never recomputed from {@link roles}. The server
   * resolves it from the tenant's own administrator-role designation and the caller's live role
   * assignments, so matching a role NAME here would be a second, weaker copy of that rule that
   * drifts the moment a tenant designates a differently named role.
   *
   * ⚠ FALSE ON THE SIGN-IN AND RENEWAL RESPONSES BY DESIGN, exactly as the role and permission
   * lists are empty there: those carry an authority-minimised snapshot. A screen or gate that
   * needs the fact reads it after the current account has been loaded.
   *
   * This is an affordance gate only. The server re-decides on every request, so withholding a
   * control here never stands in for the policy the API enforces.
   */
  readonly holdsPortalAdministration: Signal<boolean> = computed(() => {
    const user: CurrentUser | null = this.currentUser();

    return user === null ? false : user.isPortalAdministrator;
  });

  readonly isSuperUser: Signal<boolean> = computed(() => {
    const user = this.currentUser();

    return user === null ? false : user.isSuperUser;
  });

  // -------------------------------------------------------------------------
  // ADVISORY PROJECTIONS
  // -------------------------------------------------------------------------

  /*
   * MIGRATION: the legacy post-credential check was a SINGLE-VALUED enumeration with
   * precedence — `Library/Components/Users/Membership/UserValidStatus.vb` carried
   * `VALID`, `PASSWORDEXPIRED`, `PASSWORDEXPIRING`, `UPDATEPROFILE` and
   * `UPDATEPASSWORD`, of which exactly one could be reported. The migrated contract
   * carries THREE INDEPENDENT BOOLEANS instead, so more than one advisory can now be
   * true at once and a consumer must render each on its own terms rather than
   * switching on a single value. That is a real semantic divergence and is recorded
   * here rather than absorbed.
   *
   * ⚠ NONE OF THE THREE IS AN AUTHENTICATION FAILURE. They interrupt a sign-in
   * without refusing it: the session is live and the credentials were accepted. Each
   * reads `false` when nobody is signed in, which is the same answer as "no advisory"
   * and the correct one for a gate — an unauthenticated caller is stopped by the
   * authentication check, not by an advisory.
   */

  /**
   * Whether the caller must change the password before continuing.
   *
   * MIGRATION: this is also where two legacy SUCCESS outcomes land. The legacy
   * vocabulary reported a sign-in with the product's well-known default
   * administrator or host credential as its own outcome — ordinals 5 and 6 of
   * `UserLoginStatus.vb` — reached by promoting an ALREADY-ACCEPTED sign-in. They are
   * completed sign-ins carrying a security warning, never refusals, and they surface
   * to a caller folded onto this flag rather than as an error.
   */
  readonly mustChangePassword: Signal<boolean> = computed(() => {
    const session = this.tokenStorage.session();

    return session === null ? false : session.mustChangePassword;
  });

  /** Whether the caller's password is approaching expiry. Informational only. */
  readonly passwordExpiring: Signal<boolean> = computed(() => {
    const session = this.tokenStorage.session();

    return session === null ? false : session.passwordExpiring;
  });

  /**
   * Whether the caller must complete the profile before continuing.
   *
   * Taken from the custodian's own projection, which exists because this advisory is
   * the blocking one and something has to be able to gate navigation on a single
   * value.
   */
  readonly mustUpdateProfile: Signal<boolean> = this.tokenStorage.mustUpdateProfile;

  /**
   * Whether the last sign-out failed to confirm that the session was ended on the server.
   *
   * Re-published from the authentication client, exactly as the two signals above are
   * re-published from the custody collaborator, so a screen needs one injection rather than
   * two. It is a BOOLEAN and carries no credential and no failure detail.
   *
   * It exists so the sign-in screen can say so. Sign-out sends the operator there, which makes
   * it the one place the report is certain to be seen, and a warning nothing renders is the
   * same silence it was meant to replace.
   */
  readonly revocationOutstanding: Signal<boolean> = this.auth.revocationOutstanding;

  /** Whether any of the three advisories applies. */
  readonly hasAdvisory: Signal<boolean> = computed(
    () => this.mustChangePassword() || this.passwordExpiring() || this.mustUpdateProfile(),
  );

  // -------------------------------------------------------------------------
  // FAILURE PROJECTIONS
  // -------------------------------------------------------------------------

  /**
   * The problem document from the last failed command, or null.
   *
   * The whole document, including the support reference. That is deliberate: the
   * correlation identifier the server validated for the request appears on the
   * response header, on the request envelope in the server's log and on every audit
   * event the request produced, so it is the operator's only join key between a
   * browser-side report and a server-side record. Reducing this slice to a sentence
   * would throw it away.
   */
  readonly problem: Signal<ProblemDetails | null> = this._problem.asReadonly();

  /** The transport status of the last failed command, or null when none failed. */
  readonly failureStatus: Signal<number | null> = this._failureStatus.asReadonly();

  /**
   * Whether the last command failed.
   *
   * True for EVERY failure, including one that carried no problem document and no
   * status — see {@link _failed}.
   */
  readonly hasFailure: Signal<boolean> = this._failed.asReadonly();

  /**
   * How forcefully to present the last failure, or null when none failed.
   *
   * Derived by the single function in the workspace that makes this decision, so the
   * rule is not encoded twice. Derived from the TRANSPORT status rather than the
   * status repeated in the body, because a document written by an intermediary may
   * carry no status at all while the transport status is always present.
   *
   * MIGRATION: A REFUSAL IS A WARNING, NOT AN ERROR, and the legacy application is
   * the authority for that. `Website/admin/Security/AccessDenied.ascx.vb` contains no
   * permission check whatsoever — its handler at L41-L47 only PRESENTS a denial — and
   * BOTH of its branches use the yellow-warning message type: L43 for the message
   * arriving through the `message` query-string key, which it renders
   * HTML-encoded after URL-decoding, and L45 for the localised default. Presenting a
   * refusal in danger styling would tell a person something is broken when the system
   * is working exactly as configured. A 403 therefore resolves to `'warning'` here,
   * and so does the rate-limiter's 429.
   */
  readonly severity: Signal<ProblemSeverity | null> = computed(() => {
    if (this._failed() === false) {
      return null;
    }

    // An absent status resolves to `'error'` in the utility, which is the right answer
    // for a failure nobody anticipated: it is the one most worth showing.
    return problemSeverity(this._failureStatus());
  });

  /**
   * The identifier a person should quote when reporting the last failure, or null when
   * there is none to quote.
   *
   * Prefers the correlation identifier the server validated and falls back to the
   * framework's trace identifier, which is the precedence the utility establishes:
   * the two are independent values in different formats, and only the former appears
   * in the server's own records.
   */
  readonly supportReference: Signal<string | null> = computed(() =>
    problemSupportReference(this._problem()),
  );

  /**
   * The application failure code from the last failure, or null when the document
   * carried none.
   *
   * Read by the utility that parses the code out of the document's `type` member,
   * which is the ONLY channel the code travels on — there is no separate code member
   * to read, so a consumer that does not parse `type` cannot key on a code at all.
   *
   * MIGRATION: the legacy outcome travelled as an integer through a by-reference
   * argument — `Login.ascx.vb:L164` passed a status variable by reference into the
   * eight-argument validation call and L168 compared it against the not-approved
   * member. No status crosses the wire now, and the outcome vocabulary declared in
   * `auth.model.ts` is REFERENCE ONLY: a successful response carries no status member
   * in any form. Store logic must therefore never key on a numeric ordinal received
   * from the network; it keys on this string code.
   */
  readonly failureCode: Signal<string | null> = computed(() => failureCode(this._problem()));

  /**
   * The per-field validation failures from the last failure, or null when it was not
   * a validation failure.
   *
   * ⚠ READ WITH BRACKET ACCESS — `errors['Username']`, never `errors.Username`. The
   * member is an index signature and this workspace enables the compiler option that
   * makes dot access on one an error, deliberately: a key is only ever known at run
   * time, and dot access would let a typo compile as a silent `undefined`. The keys
   * are the server's model-state keys reproduced byte for byte and are Pascal-cased,
   * because they name model members rather than JSON members.
   *
   * The dictionary may legitimately be empty; the narrowing guarantees only that it
   * is there to read. Narrowed by the utility's own guard rather than by a test
   * written here.
   */
  readonly validationErrors: Signal<ProblemDetailsErrors | null> = computed(() => {
    const problem = this._problem();

    return isValidationProblemDetails(problem) ? problem.errors : null;
  });

  /**
   * Whether the last failure was the rate limiter refusing because the caller is
   * early.
   *
   * A DISTINCT, NON-ALARMING STATE, kept separate from a refused credential on
   * purpose. Nothing has failed — the caller has simply attempted too often — so a
   * consumer must present it calmly rather than as a fault, which is why
   * {@link AuthStore.severity} resolves it to a warning. This store owns the flag; the
   * error interceptor owns detection of the response and the form-errors utility owns
   * the wording, neither of which is duplicated here.
   *
   * The retry hint accompanying such a refusal arrives in the standard response
   * header rather than the body, so no slice here carries one.
   */
  readonly rateLimited: Signal<boolean> = computed(
    () => this._failureStatus() === RATE_LIMITED_STATUS,
  );

  // -------------------------------------------------------------------------
  // THE VERIFICATION LADDER
  // -------------------------------------------------------------------------

  /**
   * Whether the verification field has been revealed and should stay on screen.
   *
   * ⚠ THIS IS THE STATE THE LADDER TURNS ON, AND IT IS THE ONE PIECE OF LEGACY
   * CONTROL STATE THAT SURVIVES THE MIGRATION.
   *
   * MIGRATION: `Login.ascx.vb:L171` reads `If Not rowVerification1.Visible Then`, so
   * the legacy flow branched on whether the field had ALREADY been revealed — and in
   * Web Forms that visibility survived the postback through control state. The first
   * refusal revealed the field (L173 and L174) and asked for a code; only a
   * SUBSEQUENT refusal could judge what had been typed into it. A flat mapping from
   * failure code to message would lose that progression entirely, which is why this
   * is a signal in the store rather than a field on a component: a component would
   * lose it the moment navigation destroyed it, and the ladder would silently restart
   * at its first rung.
   *
   * Initialised `false`. Set `true` on the turn that first reveals the field, and it
   * PERSISTS across further failed attempts — that persistence is the entire point.
   * It is cleared only by a successful sign-in, by a sign-out and by
   * {@link AuthStore.reset}; a failed attempt never clears it.
   */
  readonly verificationRequired: Signal<boolean> = this._verificationRequired.asReadonly();

  /**
   * The outcome of the last turn of the verification ladder, or null when the ladder
   * has not been reached.
   *
   * Carries which of EXACTLY THREE codes applies, its wording, whether this turn is
   * the one that reveals the field, and the severity to present at. The three codes
   * are closed because the legacy flow had exactly three: `Login.ascx.vb` assigns only
   * `"EnterCode"` (L175 and L180), `"InvalidCode"` (L178) and `"UserNotAuthorized"`
   * (L184) to its outgoing message. There is no fourth, and none is invented.
   *
   * MIGRATION: localisation is not ported. The wording carried here is reproduced by
   * the form-errors utility verbatim from
   * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx` — `EnterCode.Text`
   * at L163, `InvalidCode.Text` at L166 and `UserNotAuthorized.Text` at L223. That path
   * matters: the resource file sitting beside the sign-in control itself holds only the
   * button caption, the two verification labels and the title, so reading the nearer
   * file would find none of these three and invite invented text. This store neither
   * authors nor formats that wording — it holds the value the utility produced.
   */
  readonly verificationPrompt: Signal<VerificationPrompt | null> =
    this._verificationPrompt.asReadonly();

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------
  //
  // Each command returns the service's observable with state maintenance piped onto
  // it, and each MUST BE SUBSCRIBED EXACTLY ONCE for its request to be issued.
  // Returning the observable rather than subscribing internally is deliberate on
  // three counts: a root-provided store is never destroyed, so a subscription taken
  // here would have no natural end; a caller usually needs to act on the outcome, and
  // a command that swallowed it would force the caller to watch a signal to discover
  // whether its own call finished; and a cold observable means a command that nobody
  // subscribed to changes no state, which is the correct behaviour.
  //
  // ⚠ EVERY COMMAND BODY IS WRAPPED IN `defer`, AND THAT IS LOAD-BEARING RATHER THAN
  // STYLISTIC. Setting the phase eagerly — before the returned observable is
  // subscribed — would make the paragraph above false for that one slice: a command
  // that was built and then discarded would leave the store reporting itself busy
  // forever, and a consumer showing a spinner while `isBusy()` holds would never stop.
  // `defer` moves the whole body, phase transition included, onto subscription, so
  // "no subscription, no state change" is true without exception. It also means a
  // resubscription re-runs the command from a clean phase rather than replaying a
  // stale one.

  /**
   * Exchanges credentials for a session, and advances the verification ladder when
   * the server refuses because the account is awaiting verification.
   *
   * Ported from `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197`.
   *
   * The failure is re-thrown unchanged so a caller can still react to it; the state
   * this store publishes is maintained either way. Token custody is NOT performed
   * here — the authentication service stores the resulting session with the
   * memory-only custodian and clears it on failure, so this store never holds, copies
   * or logs a credential.
   *
   * MIGRATION: the legacy call took EIGHT arguments (L164), of which the literal
   * authentication-type discriminator `"DNN"` — passed there and again at L191 when
   * constructing the event arguments — disappears with the single bearer-token path. A
   * discriminator that can hold only one value is not modelled, here or on the wire.
   *
   * MIGRATION: `Login.ascx.vb:L187` carries a DEFECT, recorded and deliberately NOT
   * reproduced. It reads `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`,
   * and because the preceding branch at L168 consumes only the not-approved outcome,
   * every other non-zero outcome fell into that else arm and counted as authenticated
   * — including the locked-out outcome at ordinal 3, so a locked account passed the
   * gate. The seven outcomes are mapped deliberately server-side instead: refusal and
   * lockout become problem documents, lockout specifically a 403, the not-approved
   * outcome drives the ladder below, and the two insecure-default-credential outcomes
   * at ordinals 5 and 6 are SUCCESSES carrying an informational code that surfaces as
   * {@link AuthStore.mustChangePassword}. The migration discipline is to annotate a
   * discovered defect rather than repair it, and the correction here is forced
   * structurally by the target's HTTP semantics rather than chosen — which is exactly
   * why it is documented instead of absorbed silently.
   *
   * MIGRATION: the failure discloses nothing beyond a code. The legacy flow drew no
   * distinction between an unknown account and an incorrect password, and that
   * non-disclosure is preserved exactly: nothing here enriches a failure, names which
   * field was wrong, or echoes the submitted account name into any published slice.
   *
   * MIGRATION: REVERSIBLE PASSWORD STORAGE IS REPLACED BY A ONE-WAY ADAPTIVE HASH, AND
   * PASSWORD RETRIEVAL IS NOT CARRIED FORWARD. The legacy membership provider was
   * registered with `passwordFormat="Encrypted"` and `enablePasswordRetrieval="true"`
   * (`Website/release.config:L236-L247`) against a decryption key committed to source
   * control at L89-L93, so every stored credential was recoverable by anyone with
   * repository access. Three consequences bind this file:
   *
   * - The submitted password travels once, inside the request object, and is compared
   *   against a hash on the server. **No slice here holds, derives, echoes or logs it**,
   *   and this store publishes no credential-shaped value of any kind.
   * - There is deliberately **no password-recovery command**. Retrieval is abolished
   *   rather than ported; an administrative reset is the only remedy for a forgotten
   *   credential, and that reset belongs to `core/state/user.store.ts`, not here.
   * - The legacy password POLICY is preserved verbatim rather than tightened —
   *   minimum length seven, no required non-alphanumeric character, no
   *   question-and-answer requirement and no unique-address requirement (L241-L244).
   *   Tightening a policy mid-migration would lock out existing accounts. Enforcement
   *   lives in the server's validators and in the sign-in form, never here; this store
   *   validates nothing and surfaces no policy metadata that could contradict them.
   *
   * ⚠ No key, secret or token value is reproduced anywhere in this file.
   *
   * @param request The credentials, and the verification code when one was supplied.
   * @returns The signed-in identity. Must be subscribed for the request to be issued.
   */
  login(
    request: LoginRequest,
    selector?: LoginPortalSelector | null,
  ): Observable<CurrentUser> {
    return defer(() => {
      // Captured here rather than stored in a slice. The ladder needs to know what was
      // submitted on THIS attempt in order to tell a wrong code from a missing one, and
      // a closure supplies that without publishing a value the person typed.
      //
      // MIGRATION: `Login.ascx.vb:L177` reads `If txtVerification.Text <> ""`, and the
      // legacy absent-text sentinel IS the empty string — `Null.vb:L71-L75` has the body
      // `Return ""`, not `Return Nothing` — so an empty code and an absent one were
      // always one branch. An omitted member is therefore mapped to null and NOTHING
      // ELSE is rewritten: an empty string stays an empty string and a null stays null.
      // Coalescing to `''`, or trimming, would silently move that decision boundary.
      const submittedCode: string | null =
        request.verificationCode === undefined ? null : request.verificationCode;

      const ticket = this.claimPhase('authenticating');

      this.clearFailure();

      /*
       * ⚠ THE DOMAIN STORES ARE EMPTIED BEFORE THE ATTEMPT, NOT AFTER IT SUCCEEDS.
       *
       * This is the ACCOUNT-REPLACEMENT path, and it does not pass through
       * {@link AuthStore.logout}. `AuthService.login()` clears the held session eagerly the
       * moment it is called, so from here on nothing is signed in — but the portal, user, role
       * and module stores are root-provided and would still be holding whatever the previous
       * account had loaded. A sign-in from a screen that already had a session, or one arrived
       * at with a session still held, would therefore leave the previous operator's tenant
       * listings, open account record and module export visible to the new one.
       *
       * Before rather than after, for two reasons. Purging only on SUCCESS would leave the
       * previous account's data in memory for the whole duration of a failed attempt, which is
       * the case where the person at the keyboard is least likely to be the previous operator.
       * And purging after would race the new session's own first reads: a screen that loads on
       * sign-in could have its fresh data wiped by a purge that arrived a moment later.
       *
       * Every `reset()` this calls also cancels that store's in-flight requests, so a read
       * belonging to the previous session cannot land afterwards and repopulate it.
       */
      this.sessionTeardown.purge();

      return this.auth.login(request, selector).pipe(
        tap((user) => {
          /*
           * ⚠ CONDITIONAL, AND THE CONDITION IS IDENTITY RATHER THAN AN EPOCH COMPARISON.
           *
           * An unconditional write here was the mechanism by which a superseded account switch
           * left the previous account's roles and personal details on screen: attempt A's
           * identity landing after attempt B had established its session published A's
           * identity while B's credentials were the ones held — and `currentUser` PREFERS
           * `_identity` over the session's own copy, so A won the disagreement.
           *
           * An epoch comparison would be awkward here and needlessly coupled. Sign-in advances
           * the epoch TWICE on the way through — the service clears before its request and
           * stores after it — so a captured value would have to be compared against a
           * predicted offset that encodes the service's internals. Object identity answers the
           * question directly instead: the service builds the session with this very identity
           * as its `user` member and returns that member, so if the held identity IS this
           * object then this attempt is the one that established the current session. If the
           * store was suppressed, or a further sign-in replaced it, the held identity is a
           * different object and this result is stale.
           *
           * The rest of the success bookkeeping is suppressed with the write, because clearing
           * a failure or resetting the verification ladder on behalf of a session this attempt
           * no longer describes would be equally wrong.
           */
          if (this.tokenStorage.currentUser() !== user) {
            return;
          }

          this.stampIdentity(user);
          this.clearFailure();
          // Reset on success, and only on success. The ladder's revealed state is
          // deliberately NOT reset by a failed attempt.
          this.resetVerificationLadder();
        }),
        catchError((error: unknown) => {
          this.recordFailure(error);
          this.advanceVerificationLadder(submittedCode);

          return throwError(() => error);
        }),
        // Returns the store to idle on success, on failure AND on cancellation, but only
        // while this attempt still owns the phase. The `_phase.set('idle')` that used to sit
        // inside the `tap` above is subsumed by this and was strictly weaker: it covered the
        // success path alone, so a component destroyed mid-sign-in — a navigation away from
        // the sign-in screen, most obviously — left the store reporting `authenticating` for
        // the remainder of the application's life, with every derived busy projection stuck
        // with it.
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Renews the session from the stored refresh token.
   *
   * Exposed so a caller may renew deliberately — a route resolver bootstrapping a
   * reload, for instance. THE REFUSED-REQUEST RETRY POLICY IS NOT THIS STORE'S: that
   * belongs to `core/interceptors/auth.interceptor.ts`, which coalesces concurrent
   * renewals and decides when one is warranted. That module is referenced by path and
   * never imported, and nothing here re-implements its job.
   *
   * On failure the session is discarded, because a refresh token the server refuses
   * cannot be retried and keeping it would mean presenting it again and being refused
   * again. The failure is recorded AFTER the session is discarded so the recorded
   * problem survives, which is what lets a sign-in screen explain why the caller is
   * back at it.
   *
   * @returns The renewed session. Must be subscribed for the request to be issued.
   */
  refreshSession(): Observable<AuthSession> {
    return defer(() => {
      const ticket = this.claimPhase('refreshing');

      this.clearFailure();

      return this.auth.refresh().pipe(
        tap(() => {
          this.clearFailure();
        }),
        catchError((error: unknown) => {
          // Ordered deliberately: the session is discarded FIRST and the failure
          // recorded second, because discarding resets the ladder while recording sets
          // the problem. Reversing the two would clear the very problem a sign-in screen
          // needs in order to explain why the caller is back at it.
          this.discardSession();
          this.recordFailure(error);

          return throwError(() => error);
        }),
        // Covers success, failure and cancellation alike, and only while this renewal still
        // owns the phase. `discardSession` no longer sets the phase itself, so this is the
        // single place a renewal returns the store to idle.
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Ends the session, locally without condition.
   *
   * MIGRATION: `FormsAuthentication.SignOut` has no stateless counterpart. It cleared a
   * cookie and took effect at once, whereas a signed bearer token cannot be recalled
   * once issued. Signing out is therefore REVOCATION PLUS CLIENT-SIDE DISCARD: the
   * server revokes the refresh token only, keeps no deny-list, and answers 204
   * whatever it finds. The already-issued access token stays technically valid until it
   * lapses, which is why that lifetime is short and why the expiry instant is
   * published rather than left implicit.
   *
   * ⚠ THE LOCAL DISCARD IS SYNCHRONOUS AND HAPPENS BEFORE THE REQUEST IS ISSUED, not in a
   * `finalize` afterwards. A `finalize` covers success, failure and early unsubscription, so
   * it looked sufficient — but every one of those exits is on the FAR side of a network round
   * trip, and until one of them ran the session was still held and the account's details were
   * still on screen. See the note in the body for what was observable in that window. A person
   * who asks to sign out is signed out at the moment they ask, and the revocation request is a
   * separate concern that proceeds afterwards.
   *
   * Local sign-out is unconditional either way. Leaving the session in place because a
   * revocation request failed would be the opposite of what was asked for, and the caller
   * could not act on the error in any case.
   *
   * @returns Completion of the revocation attempt. Must be subscribed for the request
   * to be issued.
   */
  logout(): Observable<void> {
    return defer(() => {
      const ticket = this.claimPhase('signingOut');

      /*
       * ⚠ LOCAL STATE IS DISCARDED SYNCHRONOUSLY, HERE, BEFORE THE REQUEST IS ANSWERED — NOT
       * IN A `finalize` AFTERWARDS.
       *
       * The `finalize` placement this replaces was too late, and the gap was observable. Sign-out
       * is a network round trip, so between the operator asking to sign out and the server
       * answering, the previous arrangement left the session held and the identity published.
       * During that window the shell still rendered the account's display name, the screens still
       * showed its records, and a request issued from any of them still carried its bearer token.
       * On a slow or failing network that window is unbounded.
       *
       * ⚠ THE TWO STATEMENTS BELOW ARE IN THIS ORDER FOR A REASON, AND SWAPPING THEM SILENTLY
       * BREAKS REVOCATION.
       *
       * `AuthService.logout()` does its work EAGERLY when called, not when subscribed: it reads
       * the held renewal credential, clears the stored session, and returns a cold observable
       * that already carries that credential in its request body. Calling it first is therefore
       * what lets it capture the credential while one is still there to capture.
       *
       * Discarding this store's own state first would clear the session before the service could
       * read it. The service would then find no credential, take its no-op branch, and issue NO
       * REVOCATION REQUEST AT ALL — leaving the refresh token live on the server for its full
       * seven days, which is the exact outcome signing out exists to prevent. That failure is
       * silent: locally everything looks correctly signed out.
       */
      const revocation = this.auth.logout();

      this.discardSession();
      this.clearFailure();

      return revocation.pipe(
        // The discard above already ran, so this exists solely to return the phase to idle —
        // on success, on a revocation failure and on an early unsubscription alike. Ticketed
        // like every other command so a sign-out that is abandoned mid-flight cannot reset a
        // phase belonging to a command started after it.
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Re-reads the caller's own identity, roles and permission keys from the server.
   *
   * The one authorised operation of the four, and the way a caller learns its own
   * entitlements. The result takes precedence in {@link AuthStore.currentUser}, so an
   * entitlement granted after the credentials were issued becomes visible without
   * renewing them — the stored session's copy is a snapshot taken at issue time and
   * cannot show it.
   *
   * ⚠ THE ANSWER IS NOT ENFORCEMENT. Every authorisation decision is made again on the
   * server for every request.
   *
   * @returns The caller's identity. Must be subscribed for the request to be issued.
   */
  loadCurrentUser(): Observable<CurrentUser> {
    return defer(() => {
      const ticket = this.claimPhase('loadingIdentity');

      /*
       * The auth epoch this read belongs to, captured before the request goes out.
       *
       * An identity read is the LAST thing that can republish a signed-out account, and it is
       * the easiest to overlook because it looks harmless — it only reads. But its result is
       * written to `_identity`, which `currentUser` PREFERS over the stored session's copy, so
       * a read that lands after a sign-out repopulated the display name, the roles and the
       * permission list of an account that was no longer signed in. Worse, after an account
       * SWITCH it published the previous account's roles and personal details while the new
       * account's session was the one held — two identities disagreeing, with the wrong one
       * winning.
       *
       * Testing the captured epoch before the write closes both. See {@link publishIdentity}.
       */
      const startedAt = this.tokenStorage.generation();

      this.clearFailure();

      return this.auth.me().pipe(
        tap((user) => {
          if (this.publishIdentity(user, startedAt)) {
            this.clearFailure();
          }
        }),
        catchError((error: unknown) => {
          // Recorded only while this read's own session is still current. A refusal of a read
          // issued under a session that has since ended is not a failure of the session now
          // held, and surfacing it would put a stale error in front of an operator who had
          // just signed in successfully.
          if (this.tokenStorage.isCurrentGeneration(startedAt)) {
            this.recordFailure(error);
          }

          return throwError(() => error);
        }),
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Discards the recorded failure without touching the session or the ladder.
   *
   * For a consumer dismissing a banner. It deliberately does NOT clear
   * {@link AuthStore.verificationRequired}: dismissing a message must not retract a
   * field the person is being asked to fill in.
   */
  clearError(): void {
    this.clearFailure();
  }

  /**
   * Returns the store to its initial state.
   *
   * The explicit reset referred to by {@link AuthStore.verificationRequired} — the
   * only way, besides a successful sign-in or a sign-out, that the ladder returns to
   * its first rung. Discards the session as well, so this is a full sign-out of local
   * state WITHOUT a revocation call; use {@link AuthStore.logout} when the server
   * should be told.
   */
  reset(): void {
    this.discardSession();
    this.clearFailure();
  }

  /**
   * Discards the session locally, KEEPING the recorded failure.
   *
   * What the authentication interceptor calls when a renewal is refused terminally, and the
   * one difference from {@link AuthStore.reset} is the whole point of it existing: the
   * operator is about to be sent to the sign-in screen, and that screen reads the recorded
   * problem in order to explain why they are back at it. Clearing the explanation along with
   * the session would return them to a blank form with no reason given.
   *
   * No revocation call is made. A terminal refusal means the refresh token is already
   * unusable, so there is nothing left to revoke; use {@link AuthStore.logout} when the
   * server should be told.
   *
   * Supersession is inherited rather than re-implemented: the discard clears the token store,
   * which ADVANCES the session generation every late callback tests itself against, so a
   * renewal already in flight cannot store its rotated pair afterwards.
   *
   * The phase is deliberately left alone. Returning it to idle here would stomp the phase
   * belonging to whichever command is still settling, which is precisely what the phase
   * ticket exists to prevent; that command's own `finalize` releases it.
   */
  endSession(): void {
    this.discardSession();
  }


  // -------------------------------------------------------------------------
  // PRIVATE STATE TRANSITIONS
  // -------------------------------------------------------------------------

  /**
   * Advances the verification ladder by one turn after a refused sign-in.
   *
   * A faithful reproduction of `Login.ascx.vb:L168-L185`, and the reason it is
   * PROGRESSIVE rather than a lookup table:
   *
   * - registration is not verified by code, so the refusal is final and no field is
   *   ever revealed — the legacy `"UserNotAuthorized"` at L184;
   * - registration is verified and the field is not yet on screen, so reveal it and
   *   ask — the legacy `"EnterCode"` at L175, with L173 and L174 doing the revealing;
   * - the field is on screen and something was typed, so what was typed was wrong —
   *   the legacy `"InvalidCode"` at L178;
   * - the field is on screen and nothing was typed, so ask again — the legacy
   *   `"EnterCode"` at L180.
   *
   * The resolution itself is delegated to the form-errors utility, which owns the
   * ladder and whose emptiness test is untrimmed exactly as the legacy `<> ""`
   * comparison was. Re-implementing it here would put the rule in two places.
   *
   * Whether the ladder applies at all is decided from the server's failure code, not
   * from a numeric outcome and not from the transport status: a code outside the
   * closed set of three leaves the ladder untouched, so an ordinary bad-credential
   * refusal never reveals a verification field.
   *
   * MIGRATION: the legacy gate at L170 was
   * `PortalSettings.UserRegistration = PortalRegistrationType.VerifiedRegistration`,
   * read from ambient per-request page state. The equivalent enumeration is renamed
   * `UserRegistrationMode` in the target and is declared in `core/models/portal.model.ts`
   * ALONE. It is deliberately neither imported nor duplicated here: whether the tenant
   * requires verification is a fact the SERVER already applied when it chose which of
   * the three codes to emit, so that code is read as the authority instead. This keeps
   * the server authoritative and keeps one declaration of the enumeration in the
   * workspace.
   *
   * @param submittedCode The verification code sent with the attempt, where null and
   * the empty string both mean none was supplied and neither is rewritten into the
   * other.
   */
  private advanceVerificationLadder(submittedCode: string | null): void {
    const code = this.failureCode();

    if (isAuthFailureCode(code) === false) {
      // Not a verification refusal, so the ladder does not advance and any field
      // already revealed stays revealed.
      return;
    }

    const prompt = resolveVerificationPrompt({
      verificationVisible: this._verificationRequired(),
      verificationCode: submittedCode,
      // Derived from the code the server chose, per the migration note above.
      verifiedRegistration: code !== 'auth.account_not_approved',
    });

    this._verificationPrompt.set(prompt);

    if (prompt.revealVerification) {
      // Set once and left set. The utility reports this only on the turn that first
      // reveals the field, so persistence across later attempts is exactly what not
      // clearing it achieves.
      this._verificationRequired.set(true);
    }
  }

  /** Returns the verification ladder to its first rung. */
  private resetVerificationLadder(): void {
    this._verificationRequired.set(false);
    this._verificationPrompt.set(null);
  }

  /**
   * Records a failed command's outcome.
   *
   * Sets the phase back to idle, because a failed command is no longer in flight.
   *
   * @param error The value the observable failed with, of unknown type by contract.
   */
  private recordFailure(error: unknown): void {
    this._failureStatus.set(readTransportStatus(error));
    this._problem.set(readProblemDocument(error));
    // Set unconditionally, so a failure carrying neither a document nor a status is
    // still reported as a failure rather than mistaken for success.
    this._failed.set(true);
    this._phase.set('idle');
  }

  /** Discards the recorded failure. */
  private clearFailure(): void {
    this._problem.set(null);
    this._failureStatus.set(null);
    this._failed.set(false);
  }

  /**
   * Discards every trace of the session from local state.
   *
   * Clearing the custodian is idempotent and is performed here even though the
   * authentication service clears it too. That is not duplicated storage — this store
   * holds no token to clear — it is the discard INVARIANT being enforced at the point
   * that publishes the session, so the projections above cannot outlive the session
   * they describe whichever path reached this method.
   *
   * ⚠ THE PURGE REACHES BEYOND THIS STORE, and it has to. Discarding the token and the
   * identity projection ends the session's AUTHORITY but not its FOOTPRINT: the portal,
   * user, role and module stores are root-provided too, so each holds one instance that
   * outlives the session, and clearing only what is held here left the previous account's
   * tenant listings, the account record they had open, that account's profile values, the
   * role assignments naming other accounts, and a serialised export of a module's data
   * legible to whoever signed in next on the same page load. The fan-out lives in
   * `core/state/session-teardown.service.ts` rather than here so that this store keeps its
   * one-way relationship with the domain stores — none of them imports this file, and none
   * of them should be able to.
   *
   * ORDER MATTERS in one direction only: the custodian is cleared FIRST, because clearing it
   * advances the session generation that every late callback tests itself against. A purge
   * that ran before the generation moved would leave a read in flight still believing its
   * session was current.
   *
   * Idempotent throughout, which is what lets the overlapping paths that reach it — an
   * explicit sign-out, a refresh that could not be completed, and a `401` the interceptor
   * could not recover — each call it without checking whether another already had.
   */
  private discardSession(): void {
    this.tokenStorage.clear();
    this._identity.set(null);
    this.resetVerificationLadder();
    this.sessionTeardown.purge();
  }

  /**
   * Takes ownership of {@link AuthStore.phase} for a command that is starting.
   *
   * Sets the phase and returns the ticket the command must present to
   * {@link releasePhase}. The pairing is the whole mechanism: a command may only return the
   * store to idle if nothing has claimed the phase since, so a late or cancelled command's
   * cleanup cannot reset a phase belonging to its successor.
   *
   * A pre-increment, so the first ticket ever issued is 1 and never 0. That leaves 0 as a
   * value no command holds, which makes an accidental zero-initialised ticket fail to match
   * rather than matching the first command by coincidence.
   *
   * @param phase The phase the starting command occupies.
   * @returns The ticket identifying this command's ownership.
   */
  private claimPhase(phase: AuthStorePhase): number {
    this.phaseTicket += 1;
    this._phase.set(phase);

    return this.phaseTicket;
  }

  /**
   * Returns the store to idle, if and only if the presenting command still owns the phase.
   *
   * Called from `finalize`, so it runs on completion, on error and on UNSUBSCRIPTION — which
   * is the case the previous arrangement missed entirely and the reason this method exists.
   *
   * The ownership test is what keeps that broad coverage from causing a new problem. A
   * command whose ticket has been superseded does nothing at all here: the command that
   * displaced it set the phase deliberately and will release it in its own time, so resetting
   * on the loser's behalf would report idle while work was genuinely in flight.
   *
   * @param ticket The ticket returned by the matching {@link claimPhase} call.
   */
  private releasePhase(ticket: number): void {
    if (this.phaseTicket === ticket) {
      this._phase.set('idle');
    }
  }

  /**
   * Publishes a freshly read identity, if it still belongs to the session being held.
   *
   * ⚠ THE ONE PLACE `_identity` IS WRITTEN FROM AN ASYNCHRONOUS RESULT, and therefore the one
   * place the check can be enforced. An identity is a description of a particular session; it
   * has no meaning apart from one, and publishing it against a different session — or against
   * none — states something false about who is signed in. The consequence is not cosmetic:
   * `currentUser` prefers `_identity` over the stored session's copy, so a stale write wins,
   * and the roles and personal details of one account are then displayed and reasoned about
   * while another account's credentials are the ones being sent.
   *
   * Returns whether the write happened, so the caller can suppress the rest of its
   * success-path bookkeeping too rather than clearing a failure that belongs to a session it
   * no longer describes.
   *
   * @param user The identity the server returned.
   * @param startedAt The auth epoch captured when the read was issued.
   * @returns True when the identity was published, false when it was discarded as stale.
   */
  private publishIdentity(user: CurrentUser, startedAt: number): boolean {
    if (this.tokenStorage.isCurrentGeneration(startedAt) === false) {
      return false;
    }

    this.stampIdentity(user);

    return true;
  }

  /**
   * Records an identity together with the auth epoch it describes.
   *
   * ⚠ THE ONLY WRITER OF `_identity` THAT SETS A VALUE, so the stamp cannot be omitted at a
   * call site. Reading the epoch here rather than accepting it as an argument is deliberate:
   * the stamp must be the epoch AT THE MOMENT OF PUBLICATION, not the one captured when the
   * work started. Those differ precisely on the sign-in path, where the session store that
   * makes the identity valid has already advanced the epoch past the value the caller
   * captured — so stamping the captured value would mark every freshly signed-in identity
   * stale on arrival.
   *
   * The two callers each apply their own, differently-shaped staleness test BEFORE reaching
   * here, and those tests are what decide whether publication is warranted. This method
   * decides only how long the published value remains authoritative.
   *
   * @param user The identity to publish.
   */
  private stampIdentity(user: CurrentUser): void {
    this._identity.set({ user, generation: this.tokenStorage.generation() });
  }
}

/**
 * A fetched identity together with the auth epoch it was published under.
 *
 * Internal to this module and deliberately not exported: the stamp is bookkeeping that keeps
 * {@link AuthStore.currentUser} honest, and no consumer outside this file has any business
 * reading or reasoning about it. Consumers read the projection, which yields a plain identity
 * or null and never exposes the pairing.
 */
interface StampedIdentity {
  /** The identity the server described. */
  readonly user: CurrentUser;

  /**
   * The value of the auth epoch when {@link user} was published.
   *
   * Compared for exact equality against the live epoch. Any difference at all means a session
   * transition has occurred since, so the identity describes a session that is no longer the
   * one being held and must stop being honoured.
   */
  readonly generation: number;
}

/**
 * The role and permission list handed out when no session is held.
 *
 * Frozen and shared rather than allocated per read, so the derived signals return a
 * stable reference and do not appear to change on every evaluation.
 */
const EMPTY_STRINGS: readonly string[] = Object.freeze([]);

/**
 * Reads the transport status off a failed request.
 *
 * Read STRUCTURALLY rather than by narrowing to the transport library's error type,
 * because this file must not import from that library at all — building requests is
 * the services' responsibility and taking the dependency here would blur that
 * boundary for one property read. The indexed access is what
 * `noPropertyAccessFromIndexSignature` requires of a value typed as a record.
 *
 * Returns null for anything that carries no numeric status, which includes a plain
 * error thrown by a service before any request was made. A status of zero is
 * returned as zero rather than as null: the transport reports it when no response
 * arrived at all, so it is a real observation and the severity function classifies it.
 *
 * @param error The value the observable failed with.
 * @returns The status, or null when the failure carries none.
 */
function readTransportStatus(error: unknown): number | null {
  if (typeof error !== 'object' || error === null) {
    return null;
  }

  const candidate = error as Record<string, unknown>;
  const status: unknown = candidate['status'];

  return typeof status === 'number' ? status : null;
}

/**
 * Reads the problem document out of a failed request's body.
 *
 * ⚠ ONLY THE BODY IS TESTED, AND THE OUTER FAILURE DELIBERATELY IS NOT. The narrowing
 * predicate is permissive about absence — it accepts any object carrying at least one
 * well-typed standard member — and a failed-response object carries a numeric
 * `status` of its own. Testing the outer value would therefore let the response
 * wrapper masquerade as the document it wraps, and every consumer would then read a
 * `detail` and a `title` that were never sent. The body is where a document actually
 * arrives, so the body is what is tested.
 *
 * No parsing is attempted. When the response declares a JSON payload the transport
 * has already parsed it, and when parsing failed the transport substitutes a wrapper
 * around the original text that is not a problem document in any case — so a second
 * parse here could only produce a false positive.
 *
 * @param error The value the observable failed with.
 * @returns The document, or null when the failure carried none.
 */
function readProblemDocument(error: unknown): ProblemDetails | null {
  if (typeof error !== 'object' || error === null) {
    return null;
  }

  const candidate = error as Record<string, unknown>;
  const body: unknown = candidate['error'];

  return isProblemDetails(body) ? body : null;
}
