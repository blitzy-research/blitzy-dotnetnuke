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
import { TokenStorageService } from '../services/token-storage.service';
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

  // -------------------------------------------------------------------------
  // WRITABLE SLICES — private, without exception
  // -------------------------------------------------------------------------

  /** Which command, if any, is in flight. */
  private readonly _phase = signal<AuthStorePhase>('idle');

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
   */
  private readonly _identity = signal<CurrentUser | null>(null);

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
   * Null means NOT SIGNED IN. It never means "signed in with an empty identity",
   * which is the distinction {@link AuthStore.portalId} depends on.
   */
  readonly currentUser: Signal<CurrentUser | null> = computed(() => {
    const fetched = this._identity();

    return fetched !== null ? fetched : this.tokenStorage.currentUser();
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
  login(request: LoginRequest): Observable<CurrentUser> {
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

      this._phase.set('authenticating');
      this.clearFailure();

      return this.auth.login(request).pipe(
        tap((user) => {
          this._identity.set(user);
          this.clearFailure();
          // Reset on success, and only on success. The ladder's revealed state is
          // deliberately NOT reset by a failed attempt.
          this.resetVerificationLadder();
          this._phase.set('idle');
        }),
        catchError((error: unknown) => {
          this.recordFailure(error);
          this.advanceVerificationLadder(submittedCode);

          return throwError(() => error);
        }),
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
      this._phase.set('refreshing');
      this.clearFailure();

      return this.auth.refresh().pipe(
        tap(() => {
          this.clearFailure();
          this._phase.set('idle');
        }),
        catchError((error: unknown) => {
          // Ordered deliberately: the session is discarded FIRST and the failure
          // recorded second, because discarding resets the phase and the ladder while
          // recording sets the problem. Reversing the two would clear the very problem
          // a sign-in screen needs in order to explain why the caller is back at it.
          this.discardSession();
          this.recordFailure(error);

          return throwError(() => error);
        }),
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
   * The local discard is performed in a `finalize`, which is stronger than doing it on
   * the success and failure paths separately: it also covers a caller unsubscribing
   * early. All three exits therefore end with the session gone. A person who asks to
   * sign out must end up signed out on this device; leaving the session in place
   * because a revocation request failed would be the opposite of what they asked for,
   * and they could not act on the error in any case.
   *
   * @returns Completion of the revocation attempt. Must be subscribed for the request
   * to be issued.
   */
  logout(): Observable<void> {
    return defer(() => {
      this._phase.set('signingOut');

      return this.auth.logout().pipe(
        finalize(() => {
          this.discardSession();
          this.clearFailure();
        }),
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
      this._phase.set('loadingIdentity');
      this.clearFailure();

      return this.auth.me().pipe(
        tap((user) => {
          this._identity.set(user);
          this.clearFailure();
          this._phase.set('idle');
        }),
        catchError((error: unknown) => {
          this.recordFailure(error);

          return throwError(() => error);
        }),
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
   */
  private discardSession(): void {
    this.tokenStorage.clear();
    this._identity.set(null);
    this.resetVerificationLadder();
    this._phase.set('idle');
  }
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
