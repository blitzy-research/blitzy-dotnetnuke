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
 *   is exactly one copy of a credential in the application. The commands below DO
 *   hand a freshly issued session to that custodian and clear it again, because
 *   deciding *when* a session begins and ends is the lifecycle question this file
 *   owns — but the value passes straight through and is never retained here.
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
import { catchError, defer, finalize, map, of, retry, shareReplay, switchMap, tap, throwError, timer } from 'rxjs';
import type { Observable } from 'rxjs';

import { sessionFromLoginResponse } from '../models/auth.model';
import type {
  AuthSession,
  CurrentUser,
  LoginRequest,
  RefreshTokenRequest,
} from '../models/auth.model';
import { isProblemDetails } from '../models/problem-details.model';
import type { ProblemDetails, ProblemDetailsErrors } from '../models/problem-details.model';
import { AuthService } from '../services/auth.service';
import { NotificationService } from '../services/notification.service';
import type { LoginPortalSelector } from '../utils/http-params.util';
import { TokenStorageService } from '../services/token-storage.service';
import { SessionTeardownService } from './session-teardown.service';
import type { SessionResetReason } from './session-teardown.service';
import {
  failureCode,
  isAuthFailureCode,
  isValidationProblemDetails,
  problemSeverity,
  problemSupportReference,
  resolveVerificationPrompt,
  statusMessage,
  summarizeProblem,
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
 * Raised when a sign-out could not withdraw its renewal credential on the server.
 *
 * ⚠ DELIBERATELY FREE OF DETAIL. It carries no status code, none of the server's own wording and
 * nothing derived from the credential, because this is a failure report about a credential
 * operation and the person reading it can act on the ADVICE without any of that. What it must do
 * is not lie: the behaviour this replaced reported a clean sign-out while the renewal credential
 * was still live.
 *
 * MIGRATION: this sentence used to be declared by `core/services/auth.service.ts`, which also
 *   emitted it. Announcing something to a person is a presentation concern and a transport has no
 *   business holding one, so the sentence lives with the lifecycle owner that decides local
 *   sign-out has completed anyway — the one place that knows both what the server answered and
 *   what was done about it.
 */
export const REVOCATION_FAILED_MESSAGE =
  'You have been signed out on this device, but the server could not confirm that the session ' +
  'was ended. It will expire on its own; if you are concerned that it may be used, change your ' +
  'password.';

/**
 * How many times a sign-out revocation is retried before the residue is reported.
 *
 * SEC-F14. Three attempts, because the failures worth retrying are short-lived — a rate-limit
 * window, a restarting API, a dropped connection — and a longer ladder would hold the sign-out
 * command open while the operator waits at the sign-in screen.
 */
const REVOCATION_RETRY_ATTEMPTS = 3;

/**
 * Base backoff between revocation attempts, in milliseconds.
 */
const REVOCATION_RETRY_BASE_DELAY_MS = 1_000;

/**
 * Longest backoff honoured from a `Retry-After` header, in milliseconds.
 *
 * A server is trusted to name its own window, but not to name an unbounded one: a hostile or
 * misconfigured value would otherwise park the sign-out indefinitely.
 */
const REVOCATION_RETRY_MAX_DELAY_MS = 10_000;

/**
 * Whether a refusal means the presented credential can never name a session.
 *
 * SEC-F14. `400` is a malformed value, `404` is one the server does not recognise, and `422` is
 * one it cannot process — none of them will become revocable by being sent again, and each means
 * there is no residue to report. Everything else, including a status of `0` for an unreachable
 * server, may still be pending and is retried.
 *
 * @param cause The value the error callback received.
 * @returns `true` when retrying cannot help and no residue remains.
 */
function isTerminalRevocationRefusal(cause: unknown): boolean {
  const status: unknown = readStatus(cause);

  return status === 400 || status === 404 || status === 422;
}

/**
 * The delay before the next revocation attempt.
 *
 * @param cause The refusal that prompted the retry.
 * @param attempt The one-based attempt number just completed.
 * @returns The delay in milliseconds.
 */
function retryDelayMs(cause: unknown, attempt: number): number {
  const advertised: number | null = readRetryAfterMs(cause);

  if (advertised !== null) {
    return Math.min(advertised, REVOCATION_RETRY_MAX_DELAY_MS);
  }

  return Math.min(
    REVOCATION_RETRY_BASE_DELAY_MS * 2 ** Math.max(attempt - 1, 0),
    REVOCATION_RETRY_MAX_DELAY_MS,
  );
}

/**
 * Reads the numeric status from an error the transport produced.
 *
 * @param cause The value the error callback received.
 * @returns The status, or `null` when the value carries none.
 */
function readStatus(cause: unknown): number | null {
  if (typeof cause !== 'object' || cause === null) {
    return null;
  }

  const status: unknown = (cause as { status?: unknown }).status;

  return typeof status === 'number' ? status : null;
}

/**
 * Reads a `Retry-After` header expressed in seconds, in milliseconds.
 *
 * Only the delta-seconds form is honoured. The HTTP-date form is legal and is deliberately not
 * parsed here: it would make the delay depend on agreement between two clocks, and the fallback
 * ladder is a safe answer when it is absent.
 *
 * @param cause The value the error callback received.
 * @returns The advertised delay in milliseconds, or `null`.
 */
function readRetryAfterMs(cause: unknown): number | null {
  if (typeof cause !== 'object' || cause === null) {
    return null;
  }

  const headers: unknown = (cause as { headers?: unknown }).headers;

  if (typeof headers !== 'object' || headers === null) {
    return null;
  }

  const get: unknown = (headers as { get?: unknown }).get;

  if (typeof get !== 'function') {
    return null;
  }

  const raw: unknown = (get as (name: string) => string | null).call(headers, 'Retry-After');

  if (typeof raw !== 'string') {
    return null;
  }

  const seconds = Number.parseInt(raw.trim(), 10);

  return Number.isFinite(seconds) && seconds > 0 ? seconds * 1_000 : null;
}
/**
 * Confirms a sign-out the operator asked for.
 *
 * MIGRATION: AUTHORED BECAUSE THE LEGACY HAD NO EQUIVALENT TO PORT, and for a reason that no longer
 * holds. The legacy sign-out was a full-page navigation, so the portal's own home page arriving in
 * place of the administration screen was itself the confirmation. A single-page application replaces
 * only the routed view, so the same event produces a sign-in form inside a shell that never
 * reloaded — and a sign-in form is ALSO what an unattended session lapsing produces. This sentence
 * is what separates the two.
 *
 * ⚠ DELIBERATELY DISTINCT IN WORDING FROM THE SESSION-ENDED NOTICE that
 * `core/interceptors/auth.interceptor.ts` raises, and the distinction is the entire value of both.
 * One says the operator's instruction was carried out; the other says something happened TO them
 * that they must respond to. Wording them alike, or worse routing both through one sentence, would
 * collapse a deliberate act and an interruption into the same report and leave the operator unable
 * to tell whether they had lost anything.
 *
 * It states only what is certainly true at the moment it is raised: the session is gone from this
 * device. It claims nothing about the server, because at this point nothing has been posted yet —
 * {@link REVOCATION_FAILED_MESSAGE} is what qualifies it if the withdrawal is then refused, and the
 * two are designed to sit together without contradicting each other.
 */
export const SIGNED_OUT_MESSAGE = 'You have been signed out.';

/**
 * The failure a renewal reports when no renewal credential is held.
 *
 * Produced rather than posting an empty credential, which the server would refuse as a malformed
 * request and leave the caller unable to tell that refusal from a genuine rejection of a real
 * credential. It carries no credential and names none.
 */
const NO_RENEWAL_CREDENTIAL_MESSAGE =
  'No refresh token is held, so the session cannot be renewed.';

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
 * The status a refused credential is answered with.
 *
 * Named here because {@link AuthStore.announceRenewalRefusal} has to recognise it and stay silent:
 * a terminal refusal of authority is owned by `core/interceptors/auth.interceptor.ts`, which ends
 * the session and sends the operator to the sign-in screen. Arriving there is the report.
 */
const UNAUTHORIZED_STATUS = 401;

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

  /**
   * The transient-message channel, for the one thing this store has to say to a person.
   *
   * It says exactly one sentence — {@link REVOCATION_FAILED_MESSAGE} — and only when a sign-out
   * could not withdraw its credential. Nothing else here announces anything: a failed command
   * publishes a structured problem document through the projections below, and the screen that owns
   * the moment decides how to present it. This one is different because the session is already
   * gone by the time the answer arrives, so no screen is left to read a projection.
   */
  private readonly notifications = inject(NotificationService);

  // -------------------------------------------------------------------------
  // WRITABLE SLICES — private, without exception
  // -------------------------------------------------------------------------

  /** Which command, if any, is in flight. */
  private readonly _phase = signal<AuthStorePhase>('idle');

  /**
   * The renewal currently in progress, or null when none is.
   *
   * ⚠ THE SINGLE-FLIGHT SLOT, AND THERE IS EXACTLY ONE IN THE APPLICATION. It is what makes a
   * burst of simultaneous `401` responses produce ONE renewal rather than one per refused request.
   * Without it, six parallel list requests expiring together would each present the same rotating
   * credential; the first would rotate it and the remaining five would present a credential that
   * had already been used, which the server treats as a replay and answers by revoking the
   * account's entire credential family — signing the person out precisely because the client tried
   * to keep them signed in.
   *
   * MIGRATION: THE SLOT USED TO LIVE ON `core/services/auth.service.ts`, alongside custody of the
   *   session it renewed. It belongs here for a reason the store's own sign-out demonstrates:
   *   abandoning an in-flight renewal and advancing the session generation are two halves of ONE
   *   act, and a slot held by another owner could be advanced by neither. The interceptor
   *   deliberately keeps no slot of its own for the same reason — a private second slot could not
   *   be cleared by a sign-out, so a refusal racing one would replay a cached renewal and
   *   resurrect the session the operator had just ended.
   *
   * Not a signal: no derived state reads it, and making it reactive would invite exactly that. It
   * holds an observable, never a credential.
   */
  private renewalInFlight: Observable<AuthSession> | null = null;

  /**
   * Backing state for {@link AuthStore.revocationOutstanding}.
   *
   * Holds a BOOLEAN and never a credential, a status code or the server's wording.
   */
  private readonly _revocationOutstanding = signal(false);

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
   * {@link _failureStatus} being set, because a failure can carry NEITHER.
   * {@link AuthStore.renewSession} refuses a renewal with a plain error before any request
   * is made when no refresh token is held, and a transport failure can arrive with no
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

  /**
   * Whether the caller may administer the tenant it is signed in to — THE ONE ANSWER THE
   * APPLICATION ASKS.
   *
   * ⚠ THIS IS THE ONLY PLACE THE QUESTION IS DECIDED, AND CENTRALISING IT REPLACED A
   * DEFECT RATHER THAN TIDYING A DUPLICATE. The route gate, the credential screen and
   * several list screens each used to answer it for themselves by testing {@link roles}
   * for the literal name `Administrators`, which is wrong three times over: the
   * designation is a per-tenant COLUMN (`Portals.AdministratorRoleId`) naming whichever
   * role confers administration, so it is fixed to no name at all; `Roles.RoleName` is an
   * ordinary updatable column, so renaming the role silently stripped every administrator
   * of their affordances; and a role of the same name may belong to a DIFFERENT tenant,
   * which makes a name match right about the word and wrong about the portal. A
   * legitimate administrator was therefore refused by every screen that asked.
   *
   * ⚠ THE HOST ARM IS PART OF THE RULE, NOT A COURTESY, AND IT IS WHY THIS IS NOT SIMPLY
   * {@link holdsPortalAdministration}. The API's own handler opens with
   * `if (account.IsSuperUser) return true` — a host account administers every tenant,
   * which is the same answer the enforcing policy gives — and it also publishes the
   * derived fact as `true` for a host account on the current-account read. But the
   * sign-in and renewal responses carry an authority-minimised snapshot in which the
   * derived fact is `false` while the host flag is present and true, so reading the
   * derived fact alone would withhold every administrative affordance from a host account
   * between signing in and the current-account read completing. Taking both arms matches
   * the server in every state the client can be in.
   *
   * ⚠ ADVISORY, LIKE EVERYTHING ELSE HERE. It exists so a screen can avoid offering an
   * action the server would refuse, and it unlocks nothing: every tenant-scoped decision
   * is re-evaluated server-side against stored state on every request, so an
   * administrator demoted a moment ago is refused however recently this said otherwise.
   *
   * ⚠ NOT A PERMISSION KEY, AND NOT INTERCHANGEABLE WITH ONE. The persisted permission
   * keys are `VIEW`, `EDIT`, `READ` and `WRITE`; this answers the server's
   * `PortalAdministrator` POLICY. A screen that gated an administrative action with a
   * permission key was asking a different question of a different vocabulary — the union
   * of page and module keys the caller happens to hold — and could both hide an action
   * the caller may take and offer one the server will refuse.
   */
  readonly administersCurrentPortal: Signal<boolean> = computed(
    () => this.isSuperUser() || this.holdsPortalAdministration(),
  );

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
   * Whether the server will refuse ordinary work until the caller has remediated the account.
   *
   * ⚠ THE BLOCKING PAIR ONLY, AND THE OMISSION OF THE THIRD ADVISORY IS THE POINT.
   * {@link passwordExpiring} is informational: the server neither refuses anything over it nor
   * asks for anything, so folding it in here would strand a caller on a remediation screen that
   * has nothing to remediate. This mirrors the server's own predicate exactly —
   * `AuthenticationRemediationState.IsRequired` is `MustChangePassword || MustUpdateProfile`
   * — so that the client's idea of "restricted" cannot drift from the condition the API
   * actually enforces.
   *
   * WHAT THE SERVER DOES WHILE THIS IS TRUE, measured against the running API for a session
   * carrying an outstanding credential change: `GET api/v1/users/{id}`,
   * `GET api/v1/users/{id}/services`, `GET api/v1/users/settings`, `GET api/v1/portals` and
   * `GET api/v1/modules` are each refused `403` with `auth.remediation_required`. Only
   * `GET api/v1/auth/me`, the sign-out and renewal operations, and the remediation endpoint
   * matching the OUTSTANDING advisory remain open. So this is not a hint that a caller may
   * ignore — while it holds, almost every read the console performs is refused.
   *
   * ⚠ TRUE HERE IMPLIES A RESOLVED IDENTITY, which is what lets a consumer build an
   * account-scoped remediation address without a fallback. Both advisories are read from the
   * held session and `AuthSession.user` is not optional, so a session that can report an
   * advisory necessarily carries the account it applies to.
   */
  readonly sessionRestricted: Signal<boolean> = computed(
    () => this.mustChangePassword() || this.mustUpdateProfile(),
  );

  /**
   * Records that the caller has satisfied the MANDATORY CREDENTIAL CHANGE, clearing that one
   * advisory on the held session and leaving every other member of it alone.
   *
   * ⚠ WHY THIS EXISTS INSTEAD OF A RENEWAL, WHICH WOULD BE THE OBVIOUS CHOICE.
   * `POST api/v1/users/{id}/password` deliberately revokes EVERY refresh token the account
   * holds — `UserService` ends the account's sessions immediately before replacing the
   * credential, on the stated grounds that a credential changed with sessions left
   * exchangeable would not end the session the change was performed to end. So the refresh
   * token this client is holding is dead the moment the change succeeds, and asking
   * `POST api/v1/auth/refresh` to renew with it cannot succeed. It does not merely fail
   * harmlessly: the renewal answers `401 auth.invalid_refresh_token`, the store treats a
   * refused renewal as a session that is over and DISCARDS it, and the caller is signed out
   * moments after correctly doing the one thing the server demanded. Observed end to end in
   * a browser against the running stack, including the `401` and the sign-out that followed.
   *
   * The access token is NOT revoked and remains valid for the rest of its lifetime — measured,
   * with roughly fifty minutes remaining, still answering `200` on the account, profile and
   * services reads. That is documented server-side as the irreducible floor for stateless
   * bearer tokens, so continuing to use it is the intended behaviour rather than a loophole.
   * Only the renewal is gone, which means this session ends when the access token expires and
   * the caller signs in again with the credential they have just chosen.
   *
   * ⚠ WHY UPDATING THE ADVISORY LOCALLY IS SAFE. This advisory is a NAVIGATION HINT, never a
   * gate. The server decides remediation for itself on every single request, from the
   * account's own state — `RestrictedSessionMiddleware` and `RemediationAuthorizationHandler`
   * both evaluate it per request — so a client that cleared this wrongly would simply be
   * refused, exactly as it is today. What is asserted here is only what the server has just
   * reported: a `204` from that endpoint means the credential was replaced and the flag that
   * raised this advisory was cleared with it.
   *
   * A caller holding no session is a no-op rather than an error: there is no advisory to clear
   * and nothing to assert.
   */
  noteCredentialRemediated(): void {
    const session = this.tokenStorage.session();

    if (session === null || !session.mustChangePassword) {
      return;
    }

    // Every other member is carried through unchanged, INCLUDING mustUpdateProfile: an account
    // can owe both, and clearing the one that has been satisfied is what lets the root redirect
    // move the caller on to the other rather than back to the screen they have just finished.
    this.tokenStorage.store({ ...session, mustChangePassword: false });
  }

  /**
   * Records that the caller has satisfied the MANDATORY PROFILE COMPLETION, clearing that one
   * advisory on the held session and leaving every other member of it alone.
   *
   * ⚠ ASSERTED LOCALLY FOR THE SAME REASON AS ITS SIBLING, PLUS ONE OF ITS OWN. The general
   * reason is on {@link noteCredentialRemediated}: this advisory is a navigation hint and never
   * a gate, so the server re-decides it per request and a client that cleared it wrongly is
   * simply refused. The reason specific to this one is that `PUT api/v1/users/{id}/profile`
   * REFUSES a submission that omits any required property or supplies it blank, and that is
   * precisely the condition the advisory is computed from — so a `204` from it means every
   * required property now holds a value, which means the advisory is false. The client is not
   * guessing at the server's judgement; it is reading the answer the server just gave.
   *
   * ⚠ AND THIS IS WHY IT DOES NOT RENEW EITHER, even though the profile write revokes nothing.
   * A caller can owe BOTH advisories, in which case the credential change came first and has
   * already revoked every refresh token the account holds. A renewal here would then answer
   * `401` and sign the caller out at the very end of a journey they had completed. One
   * mechanism that works in both orders is better than two that each work in one.
   */
  noteProfileRemediated(): void {
    const session = this.tokenStorage.session();

    if (session === null || !session.mustUpdateProfile) {
      return;
    }

    this.tokenStorage.store({ ...session, mustUpdateProfile: false });
  }

  /**
   * Whether the last sign-out failed to confirm that the session was ended on the server.
   *
   * A BOOLEAN, deliberately, and not the failure. The status code, the server's wording and the
   * credential itself are all withheld: a screen needs only to know that the withdrawal is
   * unconfirmed in order to say so, and anything richer would put failure detail about a
   * credential operation into a template or a log.
   *
   * It exists so the sign-in screen can say so. Sign-out sends the operator there, which makes
   * it the one place the report is certain to be seen, and a warning nothing renders is the
   * same silence it was meant to replace.
   *
   * ⚠ ITS LIFETIME IS ONE SESSION BOUNDARY, AND IT USED TO HAVE NONE. Three things retire it
   * now: a later sign-out that CONFIRMS the withdrawal, any discard of the session, and the
   * start of the next sign-in attempt. Previously only the first of those did, so a single
   * failed withdrawal left the report standing on the sign-in screen indefinitely — through the
   * next successful sign-in, and in front of whoever typed there next, describing a session that
   * was not theirs. The report is about one session and must not outlive it.
   *
   * The one-time warning immediately after a failed withdrawal is preserved by ORDERING rather
   * than by exception: {@link AuthStore.logout} discards the session before posting the
   * withdrawal and raises this only in that request's own failure handler, so nothing that
   * clears it can run afterwards.
   *
   * MIGRATION: this was re-published from the authentication client, which both held the flag and
   *   decided when to raise it. Both halves moved here with the sign-out policy they belong to —
   *   the transport now propagates the server's refusal, and the decision that local sign-out has
   *   nevertheless completed, together with the record that revocation is outstanding, is made in
   *   one place by the owner that made it.
   */
  readonly revocationOutstanding: Signal<boolean> = this._revocationOutstanding.asReadonly();

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
   * this store publishes is maintained either way. Token CUSTODY is not performed here:
   * the session this composes is handed to the memory-only custodian and cleared again
   * through that same custodian, so it passes through without being retained, and this
   * store never holds, copies or logs a credential.
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
       * ⚠ THE UNCONFIRMED-WITHDRAWAL REPORT BELONGS TO THE SESSION THAT RAISED IT, AND THIS IS
       * WHERE IT STOPS.
       *
       * The flag records that a sign-out could not confirm the server had withdrawn the refresh
       * credential, and the sign-in screen renders it — which is right, because sign-out sends
       * the operator there and it is the one place the report is certain to be seen. But it was
       * cleared ONLY by a later sign-out that succeeded, so nothing else in the application
       * retired it: after one failed withdrawal the warning stood on the sign-in screen
       * indefinitely, and it was still standing after the next successful sign-in, after the
       * next expiry returned somebody to that screen, and in front of whoever typed there next.
       * A sentence about a session two boundaries ago, attributed to the one in front of them.
       *
       * Cleared HERE — at the start of the attempt rather than on its success — for two reasons.
       * The report has already been displayed by the time anyone submits credentials, so it has
       * served its purpose; and clearing only on success would leave a refused attempt still
       * carrying somebody else's warning above the form.
       *
       * ⚠ THE ONE-TIME WARNING IS PRESERVED, and the ordering is what preserves it.
       * {@link AuthStore.logout} discards the session — which clears this flag — BEFORE it posts
       * the withdrawal, and sets the flag only in that request's own failure handler. So the
       * clear can never race ahead of the report it is meant to retire.
       */
      this._revocationOutstanding.set(false);

      /*
       * ⚠ THE DOMAIN STORES ARE EMPTIED BEFORE THE ATTEMPT, NOT AFTER IT SUCCEEDS.
       *
       * This is the ACCOUNT-REPLACEMENT path, and it does not pass through
       * {@link AuthStore.logout}. The held session is discarded below before the attempt is
       * issued, so from here on nothing is signed in — but the portal, user, role and module
       * stores are root-provided and would still be holding whatever the previous account had
       * loaded. A sign-in from a screen that already had a session, or one arrived at with a
       * session still held, would therefore leave the previous operator's tenant listings, open
       * account record and module export visible to the new one.
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
      this.sessionTeardown.purge('signedIn');

      /*
       * ⚠ THE HELD SESSION IS DISCARDED BEFORE THE ATTEMPT IS ISSUED, AND THE EPOCH IS CAPTURED
       * AFTER THAT DISCARD.
       *
       * Discarding first is what stops a REFUSED sign-in leaving an earlier session in place: an
       * operator who signs in as somebody else and is refused must not be left holding the
       * previous account's authority. Capturing the epoch after the discard is what makes the
       * commit below conditional on THIS attempt still being the current one — capturing before
       * would compare against a value this attempt itself superseded, and every commit would be
       * suppressed.
       *
       * MIGRATION: both statements, and the two-request composition beneath them, used to live in
       *   `core/services/auth.service.ts`. They are session lifecycle rather than transport, and
       *   holding them here is what lets one owner reason about the two races below at once.
       */
      this.tokenStorage.clear();

      /*
       * ⚠ AND THE SHARED RENEWAL SLOT IS SURRENDERED WITH IT, for the same reason signing out
       * surrenders it. A renewal held in the slot belongs to the session this attempt is
       * replacing; leaving it there would let a refusal arriving mid-sign-in coalesce onto a
       * renewal of the PREVIOUS account's credentials. Its commit is epoch-suppressed either
       * way, so nothing is resurrected — but a caller would wait on an answer that can never
       * be committed instead of starting a renewal that can.
       */
      this.renewalInFlight = null;

      const startedAt = this.tokenStorage.generation();

      return this.auth.login(request, selector).pipe(
        /*
         * ⚠ SIGN-IN IS TWO REQUESTS, AND THE SECOND ONE IS NOT OPTIONAL.
         *
         * The credential exchange answers with an AUTHORITY-MINIMISED identity and the access
         * token carries no role or permission claims, so the caller's roles and granted
         * permission codes are read from the describe-caller operation with the freshly issued
         * token presented explicitly. That token is deliberately NOT stored until the identity has
         * arrived, so a failed bootstrap cannot leave a half-populated session behind — which is
         * also why the token is handed to the transport rather than left to the interceptor, which
         * would attach the PREVIOUS token or none at all.
         */
        map((response) => sessionFromLoginResponse(response)),
        switchMap((session) =>
          this.auth.me(session.accessToken).pipe(map((user) => ({ ...session, user }))),
        ),
        map((session) => {
          /*
           * ⚠ CONDITIONAL, AND THE CONDITION IS THE CAPTURED EPOCH.
           *
           * An unconditional commit here is the mechanism by which two overlapping attempts
           * corrupt one another, in both directions:
           *
           *   - the LATE SUCCESS: attempt A's session stored after attempt B's has been
           *     established, so the operator is silently returned to the account they switched
           *     away from, holding B's screens;
           *   - the LATE FAILURE (in the handler below): attempt A's rejection clearing B's live
           *     session, signing out an operator whose own sign-in succeeded.
           *
           * The test is read ONCE, before the store, because storing a session advances the epoch
           * itself — asking again afterwards would answer no for the attempt that had just
           * legitimately won. The identity stamp and the rest of the success bookkeeping are
           * suppressed with the commit, because clearing a failure or resetting the verification
           * ladder on behalf of a session this attempt no longer describes would be equally wrong.
           *
           * The observable still emits unchanged in either case, so the caller learns its own
           * outcome; only the write to shared state is withheld.
           */
          if (this.tokenStorage.isCurrentGeneration(startedAt)) {
            this.tokenStorage.store(session);
            this.stampIdentity(session.user);
            this.clearFailure();
            // Reset on success, and only on success. The ladder's revealed state is
            // deliberately NOT reset by a failed attempt.
            this.resetVerificationLadder();
          }

          return session.user;
        }),
        catchError((error: unknown) => {
          // Conditioned for the LATE FAILURE above: an older attempt's rejection must not discard
          // the session a newer one has already established. The failure record itself is NOT
          // conditioned — the caller that submitted this attempt is owed the reason it failed, and
          // the sign-in screen is where it is read.
          if (this.tokenStorage.isCurrentGeneration(startedAt)) {
            this.tokenStorage.clear();
          }

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
   * Renews the session from the stored refresh token, coalescing concurrent callers onto
   * one request.
   *
   * Exposed so a caller may renew deliberately — a route resolver bootstrapping a
   * reload, for instance. THE REFUSED-REQUEST RETRY POLICY IS NOT THIS STORE'S: deciding
   * that a refusal warrants a renewal, and retrying the request that was refused,
   * belongs to `core/interceptors/auth.interceptor.ts`. That module is referenced by path
   * and never imported, and nothing here re-implements its job.
   *
   * COALESCING, however, IS this store's, because the thing being coalesced is the
   * session. Several requests refused at once must produce ONE renewal: the refresh token
   * rotates on use, so a second renewal presenting the credential the first one consumed
   * is refused, and that refusal ends a session that was in fact healthy. The slot that
   * guarantees one renewal lives in {@link AuthStore.renewSession}, which this method wraps.
   *
   * ⚠ THE DIFFERENCE BETWEEN THIS METHOD AND {@link AuthStore.renewSession} IS BOOKKEEPING,
   * AND CHOOSING THE WRONG ONE IS A REAL DEFECT RATHER THAN A STYLE PREFERENCE. This is the
   * DELIBERATE command: it claims a phase so a busy projection reports the renewal, and on failure
   * it DISCARDS THE SESSION, because a caller that asked to renew — a navigation gate resolving an
   * expired token, for instance — is about to send the operator to sign in. The refused-request path
   * wants neither the phase nor the discard on its own terms: it makes its own terminal decision,
   * with its own conditions, and an older renewal's refusal must leave a NEWER session completely
   * alone. That path therefore calls the primitive directly.
   *
   * ⚠ WHAT IT DOES **NOT** DO IS RECORD THE PROBLEM DOCUMENT, and that is deliberate — see the
   * failure handler below for the browser measurement that removed it. The operator is told the
   * session ended by the boundary's one owner, in wording chosen for them; putting the renewal's own
   * transport failure in front of them as though it were a refused sign-in told them about a token
   * instead.
   *
   * MIGRATION: the slot, the two-request composition and the epoch conditioning around the
   *   commit used to live on `core/services/auth.service.ts`. They are session lifecycle,
   *   not transport, and holding them beside the phase ladder and the failure record is what
   *   lets one owner reason about all of the races at once.
   *
   * On failure the session is discarded, because a refresh token the server refuses
   * cannot be retried and keeping it would mean presenting it again and being refused
   * again. Discarding is what explains the ending too: it purges through the one owner of the
   * session boundary, and that owner raises the operator-facing sentence for this reason alone.
   *
   * @returns The renewed session. Must be subscribed for the request to be issued.
   */
  refreshSession(): Observable<AuthSession> {
    return defer(() => {
      const ticket = this.claimPhase('refreshing');

      this.clearFailure();

      /*
       * ⚠ THE SESSION BOUNDARY THIS RENEWAL BELONGS TO, captured before the request goes out so the
       * failure handler can tell whether its answer still concerns the session that asked.
       *
       * ⚠ AND IT IS THE **TEARDOWN** GENERATION, NOT THE CUSTODIAN'S EPOCH, which is the only one of
       * the two that reads correctly here. `renewSession` clears the custodian as its own first act
       * on a refusal, so the custodian's epoch has ALWAYS moved by the time the handler below runs
       * and a test against it would skip the discard on every single refusal, including the ordinary
       * one. The teardown generation moves only when a session boundary is actually crossed — a
       * sign-out, a sign-in, a tenant change — and `renewSession` crosses none, so it is unchanged
       * on the ordinary path and advanced on precisely the paths the discard must stand down for.
       *
       * MIGRATION: the discard below used to be unconditional. That was harmless while it only
       *   emptied stores that a superseded session had already emptied, and it stopped being
       *   harmless once the boundary owner began EXPLAINING an unasked-for ending: a renewal begun
       *   before a sign-out, refused afterwards, would have told somebody who had just signed out
       *   that their session had ended — about a session that no longer existed, in answer to a
       *   request they had never made. `renewSession` already declines to touch a superseded
       *   session in both directions; this is the same judgement applied to the discard.
       */
      const boundary = this.sessionTeardown.generation();

      /*
       * The phase ticket, the failure record and the handlers below are PER SUBSCRIBER, while
       * the request itself is shared. That asymmetry is deliberate: two callers that coalesce
       * onto one renewal each claim and release their own phase ticket, and each is told the
       * outcome, but only one request is issued and only one rotated pair is stored.
       */
      return this.renewSession().pipe(
        tap(() => {
          this.clearFailure();
        }),
        catchError((error: unknown) => {
          /*
           * ⚠ THE SESSION IS DISCARDED AND THE PROBLEM DOCUMENT IS DELIBERATELY NOT RECORDED, and
           * this is a correction rather than an omission. Recording it put the RENEWAL's own
           * failure into the slot the sign-in screen reads as "why your sign-in attempt was
           * refused", and that screen then rendered it faithfully. Measured in a browser: an
           * operator who had done nothing but click a menu item after a long pause was shown
           * "Unauthorized" over "The refresh token is not valid." over a bare correlation
           * identifier. Every word true, none of it usable, and all of it about a credential they
           * never knew existed.
           *
           * The comment that stood here defended the record on the grounds that the sign-in screen
           * "has to be able to say why". That reasoning is right and is exactly why the record is
           * gone: the discard below purges through the one owner of the session boundary, and that
           * owner now raises `SESSION_ENDED_MESSAGE` for this reason and this reason alone — in the
           * operator's vocabulary, on both of the paths that end a session un-asked-for, and
           * exempted from the navigation sweep so it survives the trip to the sign-in screen. One
           * explanation, worded once, reached however the ending was discovered.
           *
           * Nothing is lost by not recording. The failure is re-thrown unchanged, so a caller still
           * learns its renewal was refused; `clearFailure` above has already emptied the slot, so
           * the sign-in screen cannot show a stale sentence from an earlier attempt; and the phase
           * returns to idle through this operator chain's own `finalize`, which the record only ever
           * duplicated.
           *
           * Conditioned on the boundary captured above: a refusal that arrives after the session it
           * belonged to has already been replaced discards nothing and explains nothing, because from
           * where the operator is standing nothing has happened to them.
           */
          if (this.sessionTeardown.isCurrent(boundary)) {
            this.discardSession('renewalRefused');
          }

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
   * Renews the session, coalescing concurrent callers onto one request and committing the
   * rotated pair exactly once — and doing nothing else.
   *
   * THE PRIMITIVE BENEATH {@link AuthStore.refreshSession}, and the entry point for the
   * REFUSED-REQUEST path in `core/interceptors/auth.interceptor.ts`. It claims no phase,
   * records no failure and discards no session: the only state it touches is the custodian,
   * and every touch is conditioned on the epoch captured before the request went out. That
   * narrowness is the point. The interceptor decides for itself whether a refused renewal is
   * terminal — it has to, because it can also be reached after a sign-out or after a newer
   * sign-in, and those three cases demand three different answers — so a discard or a failure
   * record applied here would pre-empt a decision that is not this method's to make.
   *
   * MIGRATION: the interceptor used to reach an identical primitive on
   *   `core/services/auth.service.ts`, which held the slot alongside custody of the session.
   *   Moving the slot here without exposing the primitive would have forced that path through
   *   the deliberate command above, which discards unconditionally and records the failure —
   *   and the interceptor's own specification pins both consequences as defects: an older
   *   renewal's refusal would have signed out an operator whose newer sign-in had just
   *   succeeded, and the footprint purge would have run twice for one ended session.
   *
   * Fails immediately when no refresh token is held, rather than posting an empty one: the
   * server would answer 400 and the caller would have to distinguish that from a genuine
   * rejection. The error is produced lazily inside the returned observable so this method
   * never throws synchronously — an interceptor reaching it inside a `catchError` must be
   * able to rely on getting an observable back.
   *
   * @returns The renewed session, shared by every caller that arrives while it is in flight.
   */
  renewSession(): Observable<AuthSession> {
    const inFlight = this.renewalInFlight;

    if (inFlight !== null) {
      return inFlight;
    }

    const refreshToken = this.tokenStorage.refreshToken();

    if (refreshToken === null || refreshToken.length === 0) {
      return throwError(() => new Error(NO_RENEWAL_CREDENTIAL_MESSAGE));
    }

    const body: RefreshTokenRequest = { refreshToken };

    /*
     * The epoch this renewal belongs to, captured before the request is issued.
     *
     * A renewal takes two round trips, and a sign-out or a sign-in can happen during either
     * of them. `shareReplay({ refCount: false })` keeps the source subscribed even when every
     * subscriber has gone, so the renewal WILL arrive and WILL run its operators regardless —
     * which is what made an unconditional commit a resurrection: a renewal begun before a
     * sign-out stored its rotated pair afterwards, handing back a session the operator had
     * just ended.
     *
     * `TokenStorageService.clear()` advances the epoch, so testing it here is what makes such
     * a renewal inert without cancelling it — the answer is still delivered to whoever is
     * still waiting, and only the write to shared state is withheld.
     */
    const startedAt = this.tokenStorage.generation();

    const renewal = this.auth.refresh(body).pipe(
      /*
       * ⚠ RENEWAL IS TWO REQUESTS, FOR THE SAME REASON SIGN-IN IS.
       *
       * The rotated pair arrives with an authority-minimised identity, so the caller's roles and
       * permission codes are read again with the new access token presented explicitly. The token
       * is deliberately NOT stored until that read returns: an interceptor reading storage at
       * this instant would attach the token that was just superseded and treat the resulting
       * refusal as cause for ANOTHER renewal, which is the recursion the explicit bearer breaks.
       */
      map((response) => sessionFromLoginResponse(response)),
      switchMap((session) =>
        this.auth.me(session.accessToken).pipe(map((user) => ({ ...session, user }))),
      ),
      // Storing here rather than at the call site is what guarantees the rotated refresh
      // token replaces the consumed one exactly once, however many subscribers are sharing
      // this request.
      //
      // Conditioned on the epoch: a renewal that began before a sign-out or a sign-in must
      // not write its pair over whatever replaced it.
      tap((session) => {
        if (this.tokenStorage.isCurrentGeneration(startedAt)) {
          this.tokenStorage.store(session);
        }
      }),
      catchError((error: unknown) => {
        // Equally conditioned, and this direction matters just as much: a refused renewal
        // from a superseded session must not clear the session that superseded it.
        if (this.tokenStorage.isCurrentGeneration(startedAt)) {
          this.tokenStorage.clear();

          this.announceRenewalRefusal(error);
        }

        return throwError(() => error);
      }),
      // Clears the slot on completion, error and unsubscription alike, so a later refusal
      // starts a fresh renewal rather than replaying this one's outcome forever. Placed before
      // `shareReplay` so it observes the source, not each subscriber.
      //
      // Released by OBJECT IDENTITY, never by epoch. The question this asks is whether the slot
      // still holds THIS request, which is a question about identity and not about the session.
      // An epoch test would be wrong in both directions: a renewal that completes after a
      // sign-out still needs to release its own slot, or the slot stays held by a settled
      // observable forever and no future renewal can start; while a renewal whose slot has
      // already been claimed by a successor must not clear it even if the epoch happens to match.
      finalize(() => {
        if (this.renewalInFlight === renewal) {
          this.renewalInFlight = null;
        }
      }),
      // `refCount: false` keeps the single subscription to the HTTP request alive even if
      // every current subscriber unsubscribes, so a cancelled request does not silently
      // abandon the renewal for the subscribers still waiting.
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    this.renewalInFlight = renewal;

    return renewal;
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
   * ⚠ THE STREAM COMPLETES SUCCESSFULLY EVEN WHEN THE WITHDRAWAL FAILED, AND A FAILED
   * WITHDRAWAL IS STILL REPORTED. Both halves matter. The caller navigates away from the
   * signed-in shell when this completes, so erroring here would leave somebody who asked to
   * sign out looking at a screen that behaves as though they had not. But a 400, a 429, a 503
   * or a dropped connection means the renewal credential is still live on the server for its
   * full lifetime — precisely the outcome signing out exists to prevent — so the failure is
   * RECORDED in {@link AuthStore.revocationOutstanding} and ANNOUNCED, rather than discarded.
   * Absorbing the failure was never the defect; claiming success was.
   *
   * MIGRATION: the absorption used to happen one layer lower, inside
   *   `core/services/auth.service.ts`, which returned successful completion for a refused
   *   revocation and left this store unable to tell the two apart. The transport now
   *   propagates the real refusal and the policy — local sign-out regardless, plus a report —
   *   is applied here, where the session and the operator-facing state both live.
   *
   * ⚠ NO RETRY IS OFFERED, AND NO CREDENTIAL IS CACHED TO MAKE ONE POSSIBLE. A client-side
   * retry would mean holding the renewal credential in a field of this store so a later attempt
   * could re-send it, and custody of that credential belongs to `TokenStorageService` alone.
   * What makes that acceptable is the other half of the same fix, on the server: revocation
   * draws on a rate-limit budget of its own rather than the one sign-in attempts spend, so the
   * 429 that made this failure common can no longer be caused by traffic that has nothing to do
   * with this caller.
   *
   * @returns Completion of the revocation attempt. Must be subscribed for the request
   * to be issued.
   */
  /**
   * Retries an outstanding sign-out revocation with the credential held aside for it.
   *
   * SEC-F14. Signing out discards the session at once, so a revocation the server never
   * acknowledged used to be unrecoverable. The credential is now retained by
   * {@link TokenStorageService.retainForRevocation} until the server either acknowledges the
   * revocation or says the value can never name a session, and this method is how a later
   * attempt is made — from application start-up, or from the sign-in screen that renders
   * {@link AuthStore.revocationOutstanding}.
   *
   * Completes immediately, and does nothing, when nothing is outstanding.
   *
   * @returns Completion. Never errors: the outcome is reported through
   * {@link AuthStore.revocationOutstanding}.
   */
  retryOutstandingRevocation(): Observable<void> {
    return defer(() => {
      const pending: string | null = this.tokenStorage.pendingRevocation();

      if (pending === null || pending.length === 0) {
        return of(undefined);
      }

      return this.revokeWithRetry(pending).pipe(
        map(() => {
          this.tokenStorage.clearPendingRevocation();
          this._revocationOutstanding.set(false);

          return undefined;
        }),
        catchError((cause: unknown) => {
          if (isTerminalRevocationRefusal(cause)) {
            this.tokenStorage.clearPendingRevocation();
            this._revocationOutstanding.set(false);

            return of(undefined);
          }

          this._revocationOutstanding.set(true);

          return of(undefined);
        }),
      );
    });
  }

  /**
   * Posts one revocation, retrying only outcomes that can plausibly succeed later.
   *
   * SEC-F14. The bound is deliberate on both sides. WITHOUT a bound a sign-out could hold the
   * command phase open indefinitely against an unreachable server; WITHOUT any retry a single
   * rate-limited or briefly unavailable attempt left a live session behind. The delay honours a
   * `Retry-After` header when the server sends one — a rate limiter that names its own window is
   * the only party that knows it — and otherwise doubles from one second, so three attempts span
   * a few seconds rather than a minute.
   *
   * @param refreshToken The credential to withdraw.
   * @returns Completion, or the last error when every permitted attempt failed.
   */
  private revokeWithRetry(refreshToken: string): Observable<void> {
    return this.auth.logout({ refreshToken }).pipe(
      retry({
        count: REVOCATION_RETRY_ATTEMPTS,
        delay: (cause: unknown, attempt: number) => {
          // A terminal refusal is rethrown rather than retried: repeating a request the server
          // has already judged unanswerable only delays the honest report of it.
          if (isTerminalRevocationRefusal(cause)) {
            return throwError(() => cause);
          }

          return timer(retryDelayMs(cause, attempt));
        },
      }),
    );
  }

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
       * ⚠ THE STATEMENTS BELOW ARE IN THIS ORDER FOR A REASON, AND REORDERING THEM SILENTLY
       * BREAKS REVOCATION.
       *
       * The renewal credential is read FIRST, because discarding the session clears the
       * custodian that holds it. Reading afterwards would find nothing, take the no-request
       * branch below, and leave the refresh token live on the server for its full lifetime —
       * the exact outcome signing out exists to prevent, and a silent one: locally everything
       * would look correctly signed out.
       *
       * MIGRATION: the read used to be the transport's, which did it eagerly on call and made the
       *   ordering here a matter of when `AuthService.logout()` was invoked rather than of when
       *   the credential was read. The custody question is unchanged; only the layer that asks
       *   it has moved, and asking it here is what makes the ordering legible.
       */
      const refreshToken = this.tokenStorage.refreshToken();

      /*
       * ⚠ SEC-F14. THE CREDENTIAL IS PUT ASIDE BEFORE THE SESSION IS DISCARDED.
       *
       * `discardSession` below clears the held session, and the refresh token lives inside
       * it — so until this line existed, the value needed to end the session ON THE SERVER
       * was destroyed a few statements before the request carrying it had been answered.
       * Every transient refusal (429, 503, a dropped connection) therefore became permanent:
       * the store reported an outstanding revocation and there was nothing left to retry it
       * with. The slot survives the discard precisely so a retry remains possible.
       */
      if (refreshToken !== null && refreshToken.length !== 0) {
        this.tokenStorage.retainForRevocation(refreshToken);
      }

      /*
       * ⚠ THE SHARED RENEWAL SLOT IS RELEASED AS PART OF SIGNING OUT.
       *
       * A renewal held in the slot is a settled or in-flight observable carrying the session
       * being ended. Leaving it there would let the next refusal — from a request already in
       * flight, or from a subsequent sign-in's bootstrap — replay THAT renewal and reinstate a
       * session the operator had just discarded. The epoch conditioning inside the renewal
       * withholds its commit, but the slot itself must still be surrendered so a genuinely new
       * renewal can be started later.
       */
      this.renewalInFlight = null;

      this.discardSession('signedOut');
      this.clearFailure();

      /*
       * ⚠ NOTHING IS ANNOUNCED FROM HERE, AND THAT IS A CORRECTION RATHER THAN AN OMISSION. A
       * revision of this method DID raise the sign-out confirmation at this point, which reads as
       * the obvious place for it: the local sign-out is complete by now and this is where the fact
       * is established. It was destroyed on every single run.
       *
       * `SessionLifecycleService.signOut` — the one path that reaches this method — runs its
       * teardown in a `finalize`, so the queue is emptied AFTER this line executes: once by
       * `SessionTeardownService.purge` and again by {@link AuthStore.reset}'s own discard. Both
       * clears are total and must be, because a queued notice can name the departing operator's
       * records. A real browser measured the resulting lifetime at 10 ms — added here, gone before
       * a single display frame, invisible to a point-in-time read and to a 40 ms poller alike.
       *
       * The statement therefore belongs to the last actor in the teardown, not the first, and
       * `session-lifecycle.service.ts` raises both it and the residue warning after its purge has
       * run. {@link SIGNED_OUT_MESSAGE} and {@link REVOCATION_FAILED_MESSAGE} stay declared in this
       * file because their wording and the legacy authority for it belong with the session model;
       * only the raising moved.
       */

      if (refreshToken === null || refreshToken.length === 0) {
        // Nothing to withdraw, so nothing is posted: an empty credential would be answered 400
        // and reported as an outstanding revocation, which would be a false alarm. Local
        // sign-out has already happened above, which is the part that was asked for.
        return of(undefined).pipe(finalize(() => this.releasePhase(ticket)));
      }

      // No envelope here, and none is expected. Sign-out answers 204, which HTTP forbids from
      // carrying a body, so there is nothing to unwrap.
      /*
       * ⚠ ONE ATTEMPT HERE, THE BOUNDED LADDER LATER. Signing out must not hold the operator on a
       * spinner while a backoff ladder plays out against an unreachable server - the local sign-out
       * has already happened and the redirect is waiting on this. The credential is retained above,
       * so the retry is a separate, bounded operation driven by
       * {@link AuthStore.retryOutstandingRevocation} from the screen the operator lands on.
       */
      return this.auth.logout({ refreshToken }).pipe(
        map(() => {
          // Cleared only by a withdrawal that actually succeeded, so a screen that reported an
          // unconfirmed sign-out stops reporting it once a later one is confirmed.
          this._revocationOutstanding.set(false);
          this.tokenStorage.clearPendingRevocation();

          return undefined;
        }),
        catchError((cause: unknown) => {
          /*
           * SEC-F14. A TERMINAL REFUSAL RELEASES THE CREDENTIAL; A TRANSIENT ONE KEEPS IT.
           * A 400 or a 404 is the server saying this value can never name a session, so
           * retaining it would leave a notice nothing could ever clear. Anything else - an
           * outage, a rate limit, an unreachable server - leaves the session possibly live,
           * so the credential stays and {@link AuthStore.retryOutstandingRevocation} can be
           * driven again.
           */
          const terminal: boolean = isTerminalRevocationRefusal(cause);

          if (terminal) {
            this.tokenStorage.clearPendingRevocation();
          }

          /*
           * The refusal is deliberately NOT recorded through `recordFailure`. That record is
           * read by the sign-in screen to explain why a caller is back at it, and a failed
           * WITHDRAWAL is not a reason they were signed out — they asked to be. It is reported
           * as its own boolean and announced once, in words that name the action that genuinely
           * exists for the residue.
           */
          // A terminal refusal leaves NO residue - the server has said the value cannot name a
          // session - so the flag stays down and no notice is raised for it. Everything else may
          // have left a live session behind and is reported.
          this._revocationOutstanding.set(!terminal);

          /*
           * The residue is RECORDED here and ANNOUNCED elsewhere, for the reason set out beside the
           * removed confirmation above: a statement raised from inside this method is erased by the
           * teardown that follows it. `SessionLifecycleService.signOut` reads this flag BEFORE its
           * teardown runs — it has to, because {@link AuthStore.reset} sets the flag back to `false`
           * — and raises {@link REVOCATION_FAILED_MESSAGE} afterwards.
           *
           * The flag is also read by the sign-in screen through {@link AuthStore.revocationOutstanding}
           * as a rendered notice, so the fact has a durable surface as well as a transient one.
           */

          // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP. Signing out returns the caller to the sign-in
          // screen, and the shell discards stale notifications on a completed navigation — so
          // without this the one message explaining that a residue remains on the server would be
          // queued and swept inside the same task as the redirect it accompanies.
          this.notifications.retainAcrossNavigation();

          return of(undefined);
        }),
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
    this.discardSession('signedOut');
    this.clearFailure();
  }

  /**
   * Discards the session locally, KEEPING the recorded failure.
   *
   * The one difference from {@link AuthStore.reset} is the whole point of it existing: a failure
   * ALREADY recorded by some other command survives, so a screen that was explaining something is
   * not blanked by an unrelated session ending.
   *
   * ⚠ IT IS NOT WHAT EXPLAINS A REFUSED RENEWAL, AND IT HAS NO PRODUCTION CALLER. Both halves of
   * that were once untrue of the comment standing here, which claimed the authentication interceptor
   * called this and that the sign-in screen read the resulting problem document. The interceptor
   * performs its own teardown and always has; and a refused renewal deliberately records no problem
   * document at all now, because putting the renewal's own transport failure in front of an operator
   * worded an ended session as "The refresh token is not valid.". The explanation comes from the
   * boundary owner — `SESSION_ENDED_MESSAGE` in `core/state/session-teardown.service.ts` — which this
   * method reaches through {@link AuthStore.discardSession} like every other ending does.
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
    this.discardSession('renewalRefused');
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
  /**
   * Announces a refused renewal, for the refusals that no other surface reports.
   *
   * ⚠ THIS EXISTS BECAUSE A RENEWAL HAS NO SCREEN. Every other credential operation is reported by
   * something an operator is looking at - a sign-in by the sign-in screen's banner and sentence, a
   * sign-out by {@link REVOCATION_FAILED_MESSAGE} - but a renewal happens behind whatever the
   * operator is doing, so nothing binds its outcome. While the transport left its requests unmarked
   * the global announcer in `core/interceptors/error.interceptor.ts` reported it; marking them made
   * this store the owner, and an owner that says nothing would have turned a duplicate report into
   * no report at all.
   *
   * ⚠ A TERMINAL REFUSAL OF AUTHORITY IS DELIBERATELY SILENT HERE, and it always was: the global
   * announcer returns early on that status too, on the documented grounds that
   * `core/interceptors/auth.interceptor.ts` owns the lifecycle of a refused credential. What that
   * owner does is end the session and send the operator to the sign-in screen — and the sentence
   * explaining that now comes from the boundary owner itself, `SESSION_ENDED_MESSAGE` in
   * `core/state/session-teardown.service.ts`, so speaking here would be a second report of one event.
   *
   * MIGRATION: this silence used to rest on the claim that "arriving at the sign-in screen IS the
   *   report". Measured in a browser, it was not: the teardown was complete and correct and BOTH live
   *   regions were empty, so somebody mid-task reached a sign-in form with no account, no work and no
   *   explanation. The reasoning that stood here was right about ONE thing, and it is the reason the
   *   sentence had to be raised somewhere else — the teardown clears the queue, so a notice raised
   *   from this method could never have survived it.
   *
   * ⚠ WHICH ESTABLISHES THE PRECEDENCE FOR EVERY OTHER STATUS TOO, and it is structural rather than
   * remembered. Whatever this method queues is raised BEFORE the terminal owner purges, and the purge
   * empties the queue and then raises its own sentence — so when a teardown follows, the teardown
   * speaks and this does not, whatever was said here. No production path currently reaches a renewal
   * refusal WITHOUT a teardown following it, so in practice this method's wording is superseded every
   * time; it remains because {@link AuthStore.renewSession} is public and a future caller may renew
   * without ending the session, and in that case this is the only thing that would report it.
   *
   * No wording is authored here. Severity and sentence both come from the shared summariser, so a
   * renewal refused for a reason the operator has seen elsewhere - a spent request budget, an
   * unreachable server - is described in the same words wherever it is met.
   *
   * @param error The value the renewal failed with.
   */
  private announceRenewalRefusal(error: unknown): void {
    const status: number | null = readTransportStatus(error);

    if (status === UNAUTHORIZED_STATUS) {
      return;
    }

    const summary = summarizeProblem(readProblemDocument(error), statusMessage(status));

    this.notifications.notify(
      problemSeverity(status),
      summary.message,
      summary.supportReference,
    );
  }

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
   * Clearing the custodian is idempotent and is performed here even though the commands
   * that fail clear it too, conditioned on their own epoch. That is not duplicated
   * storage — this store holds no token to clear — it is the discard INVARIANT being
   * enforced at the point that publishes the session, so the projections above cannot
   * outlive the session they describe whichever path reached this method.
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
  private discardSession(reason: SessionResetReason): void {
    this.tokenStorage.clear();
    this._identity.set(null);
    this.resetVerificationLadder();
    this._revocationOutstanding.set(false);
    this.sessionTeardown.purge(reason);
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
