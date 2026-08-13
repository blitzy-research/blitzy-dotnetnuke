import { Injectable, computed, inject, signal } from '@angular/core';
import type { Signal } from '@angular/core';
import {
  catchError,
  concatMap,
  defer,
  finalize,
  from,
  map,
  of,
  retry,
  shareReplay,
  switchMap,
  tap,
  throwError,
  timer,
  toArray,
} from 'rxjs';
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

/** The operations this store can be part-way through. */
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

export const REVOCATION_FAILED_MESSAGE =
  'You have been signed out on this device, but the server could not confirm that the session ' +
  'was ended. It will expire on its own; if you are concerned that it may be used, change your ' +
  'password.';

/**
 * How many times a sign-out revocation is retried before the residue is reported. Three attempts, because
 * the failures worth retrying are short-lived — a rate-limit window, a restarting API, a dropped
 * connection — and a longer ladder would hold the sign-out command open while the operator waits at the
 * sign-in screen.
 */
const REVOCATION_RETRY_ATTEMPTS = 3;

/** Base backoff between revocation attempts, in milliseconds. */
const REVOCATION_RETRY_BASE_DELAY_MS = 1_000;

/**
 * Longest backoff honoured from a `Retry-After` header, in milliseconds. A server is trusted to name its
 * own window, but not to name an unbounded one: a hostile or misconfigured value would otherwise park the
 * sign-out indefinitely.
 */
const REVOCATION_RETRY_MAX_DELAY_MS = 10_000;

/**
 * Whether a refusal means the presented credential can never name a session. `400` is a malformed value,
 * `404` is one the server does not recognise, and `422` is one it cannot process — none of them will
 * become revocable by being sent again, and each means there is no residue to report.
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
 * Reads a `Retry-After` header expressed in seconds, in milliseconds. Only the delta-seconds form is
 * honoured.
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
 * Confirms a sign-out the operator asked for. MIGRATION: AUTHORED BECAUSE THE LEGACY HAD NO EQUIVALENT TO
 * PORT, and for a reason that no longer holds.
 */
export const SIGNED_OUT_MESSAGE = 'You have been signed out.';

/**
 * The failure a renewal reports when no renewal credential is held. Produced rather than posting an empty
 * credential, which the server would refuse as a malformed request and leave the caller unable to tell
 * that refusal from a genuine rejection of a real credential.
 */
const NO_RENEWAL_CREDENTIAL_MESSAGE =
  'No refresh token is held, so the session cannot be renewed.';

const RATE_LIMITED_STATUS = 429;

/**
 * The status a refused credential is answered with. Named here because {@link
 * AuthStore.announceRenewalRefusal} has to recognise it and stay silent: a terminal refusal of authority
 * is owned by `core/interceptors/auth.interceptor.ts`, which ends the session and sends the operator to
 * the sign-in screen.
 */
const UNAUTHORIZED_STATUS = 401;

/**
 * The signed-in session, the state of the verification ladder, and the outcome of the last authentication
 * command. Root-provided and injected with {@link inject}, so no component declares a provider for it and
 * every consumer shares one instance.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly auth = inject(AuthService);
  private readonly tokenStorage = inject(TokenStorageService);

  /**
   * The fan-out that purges the domain stores when a session ends. Injected rather than reached for at
   * the call site so that a test can substitute it, and held here rather than having each domain store
   * injected directly so that this file keeps its one-way relationship with them — see {@link
   * AuthStore.discardSession}.
   */
  private readonly sessionTeardown = inject(SessionTeardownService);

  /**
   * The transient-message channel, for the one thing this store has to say to a person. It says exactly
   * one sentence — {@link REVOCATION_FAILED_MESSAGE} — and only when a sign-out could not withdraw its
   * credential.
   */
  private readonly notifications = inject(NotificationService);

  // -------------------------------------------------------------------------
  // WRITABLE SLICES — private, without exception
  // -------------------------------------------------------------------------

  /** Which command, if any, is in flight. */
  private readonly _phase = signal<AuthStorePhase>('idle');

  /**
   * The renewal currently in progress, or null when none is. ⚠ THE SINGLE-FLIGHT SLOT, AND THERE IS
   * EXACTLY ONE IN THE APPLICATION. It is what makes a burst of simultaneous `401` responses produce ONE
   * renewal rather than one per refused request.
   */
  private renewalInFlight: Observable<AuthSession> | null = null;

  /**
   * Backing state for {@link AuthStore.revocationOutstanding}. Holds a BOOLEAN and never a credential, a
   * status code or the server's wording.
   */
  private readonly _revocationOutstanding = signal(false);

  /** The withdrawal drain currently running, or null when none is. */
  private revocationRetryInFlight: Observable<void> | null = null;

  private phaseTicket = 0;

  /** The problem document from the last failed command, or null. The STRUCTURED document, kept whole. */
  private readonly _problem = signal<ProblemDetails | null>(null);

  /**
   * The transport status of the last failed command, or null when none failed. Held alongside {@link
   * _problem} rather than read out of it, because the two are not the same fact.
   */
  private readonly _failureStatus = signal<number | null>(null);

  /** Whether the last command failed. */
  private readonly _failed = signal(false);

  private readonly _verificationRequired = signal(false);

  /**
   * The outcome of the last turn of the verification ladder, or null when the ladder has not been
   * reached.
   */
  private readonly _verificationPrompt = signal<VerificationPrompt | null>(null);

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
   * The signed-in identity, or null when nobody is signed in. Prefers the identity last read from the
   * current-user endpoint and falls back to the copy inside the stored session, so a deliberate refresh
   * of a caller's roles is visible without renewing the credentials.
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
   * Whether a session is held. Reports the PRESENCE of a session, not its validity, because that is what
   * the custodian reports and re-deciding it here would put two answers in the application.
   */
  readonly isAuthenticated: Signal<boolean> = this.tokenStorage.isAuthenticated;

  /**
   * When the access token expires, exactly as the server stamped it, or null when no session is held. An
   * absolute instant in Coordinated Universal Time, passed through as the string the contract publishes.
   */
  readonly accessTokenExpiresAt: Signal<string | null> = this.tokenStorage.accessTokenExpiresAt;

  /**
   * The portal, or tenant, the caller is signed in to, or null when nobody is. ⚠ SENTINEL DISCIPLINE.
   * `Portals.PortalID` is declared `IDENTITY(-1, 1)`, so minus one is a REAL tenant key and the shipped
   * default portal is inserted explicitly as zero. Minus one is simultaneously the legacy encoding for a
   * missing integer, so one value means both a real portal and "no portal".
   */
  readonly portalId: Signal<number | null> = computed(() => {
    const user = this.currentUser();

    return user === null ? null : user.portalId;
  });

  /**
   * The role names the caller holds in the resolved tenant, empty when nobody is signed in. FOR RENDERING
   * AFFORDANCES ONLY. The server re-authorises every request against stored state and answers 403; a
   * screen may use this to avoid offering an action that would be refused, and nothing more.
   */
  readonly roles: Signal<readonly string[]> = computed(() => {
    const user = this.currentUser();

    return user === null ? EMPTY_STRINGS : user.roles;
  });

  /**
   * The permission keys the caller holds, empty when nobody is signed in. ⚠ THIS DECIDES NOTHING. The
   * server is authoritative and refuses with 403. ⚠ TWO CLOSED, NON-INTERCHANGEABLE VOCABULARIES. The
   * persisted permission keys are `VIEW`, `EDIT`, `READ` and `WRITE`; the server's authorisation POLICY
   * names are `ModuleView`, `ModuleEdit`, `TabView`, `TabEdit` and `PortalAdministrator`.
   */
  readonly permissions: Signal<readonly string[]> = computed(() => {
    const user = this.currentUser();

    return user === null ? EMPTY_STRINGS : user.permissions;
  });

  /**
   * Whether the caller holds a host, or super-user, account. ⚠ `false` IS DATA, not absence. In the
   * legacy null contract the absence test reported true for `false` itself — `Null.vb`'s `IsNull` treats
   * `False`, `""`, `-1`, `255`, the minimum date and the empty globally unique identifier all as "not
   * set" — so a legacy `false` and a legacy "unknown" were indistinguishable.
   */
  /**
   * Whether the account administers the tenant it is signed in to. Read from the snapshot the server
   * derived, never recomputed from {@link roles}.
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
   * Whether the caller may administer the tenant it is signed in to — THE ONE ANSWER THE APPLICATION
   * ASKS. ⚠ THIS IS THE ONLY PLACE THE QUESTION IS DECIDED, AND CENTRALISING IT REPLACED A DEFECT RATHER
   * THAN TIDYING A DUPLICATE. The route gate, the credential screen and several list screens each used to
   * answer it for themselves by testing {@link roles} for the literal name `Administrators`, which is
   * wrong three times over: the designation is a per-tenant COLUMN (`Portals.AdministratorRoleId`) naming
   * whichever role confers administration, so it is fixed to no name at all; `Roles.RoleName` is an
   * ordinary updatable column, so renaming the role silently stripped every administrator of their
   * affordances; and a role of the same name may belong to a DIFFERENT tenant, which makes a name match
   * right about the word and wrong about the portal.
   */
  readonly administersCurrentPortal: Signal<boolean> = computed(
    () => this.isSuperUser() || this.holdsPortalAdministration(),
  );

  // -------------------------------------------------------------------------
  // ADVISORY PROJECTIONS
  // -------------------------------------------------------------------------

  // The legacy post-credential check was a SINGLE-VALUED enumeration with precedence —
  // `Library/Components/Users/Membership/UserValidStatus.vb` carried `VALID`, `PASSWORDEXPIRED`,
  // `PASSWORDEXPIRING`, `UPDATEPROFILE` and `UPDATEPASSWORD`, of which exactly one could be reported.

  /**
   * Whether the caller must change the password before continuing. this is also where two legacy SUCCESS
   * outcomes land.
   */
  readonly mustChangePassword: Signal<boolean> = computed(() => {
    const session = this.tokenStorage.session();

    return session === null ? false : session.mustChangePassword;
  });

  /** Whether the caller's password is approaching expiry. */
  readonly passwordExpiring: Signal<boolean> = computed(() => {
    const session = this.tokenStorage.session();

    return session === null ? false : session.passwordExpiring;
  });

  /**
   * Whether the caller must complete the profile before continuing. Taken from the custodian's own
   * projection, which exists because this advisory is the blocking one and something has to be able to
   * gate navigation on a single value.
   */
  readonly mustUpdateProfile: Signal<boolean> = this.tokenStorage.mustUpdateProfile;

  readonly sessionRestricted: Signal<boolean> = computed(
    () => this.mustChangePassword() || this.mustUpdateProfile(),
  );

  noteCredentialRemediated(): void {
    const session = this.tokenStorage.session();

    if (session === null || !session.mustChangePassword) {
      return;
    }

    this.tokenStorage.store({ ...session, mustChangePassword: false });
  }

  /**
   * Records that the caller has satisfied the MANDATORY PROFILE COMPLETION, clearing that one advisory on
   * the held session and leaving every other member of it alone. ⚠ ASSERTED LOCALLY FOR THE SAME REASON
   * AS ITS SIBLING, PLUS ONE OF ITS OWN. The general reason is on {@link noteCredentialRemediated}: this
   * advisory is a navigation hint and never a gate, so the server re-decides it per request and a client
   * that cleared it wrongly is simply refused.
   */
  noteProfileRemediated(): void {
    const session = this.tokenStorage.session();

    if (session === null || !session.mustUpdateProfile) {
      return;
    }

    this.tokenStorage.store({ ...session, mustUpdateProfile: false });
  }

  readonly revocationOutstanding: Signal<boolean> = this._revocationOutstanding.asReadonly();

  /** Whether any of the three advisories applies. */
  readonly hasAdvisory: Signal<boolean> = computed(
    () => this.mustChangePassword() || this.passwordExpiring() || this.mustUpdateProfile(),
  );

  // -------------------------------------------------------------------------
  // FAILURE PROJECTIONS
  // -------------------------------------------------------------------------

  /**
   * The problem document from the last failed command, or null. The whole document, including the support
   * reference.
   */
  readonly problem: Signal<ProblemDetails | null> = this._problem.asReadonly();

  /** The transport status of the last failed command, or null when none failed. */
  readonly failureStatus: Signal<number | null> = this._failureStatus.asReadonly();

  /**
   * Whether the last command failed. True for EVERY failure, including one that carried no problem
   * document and no status — see {@link _failed}.
   */
  readonly hasFailure: Signal<boolean> = this._failed.asReadonly();

  /**
   * How forcefully to present the last failure, or null when none failed. Derived by the single function
   * in the workspace that makes this decision, so the rule is not encoded twice.
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
   * The identifier a person should quote when reporting the last failure, or null when there is none to
   * quote. Prefers the correlation identifier the server validated and falls back to the framework's
   * trace identifier, which is the precedence the utility establishes: the two are independent values in
   * different formats, and only the former appears in the server's own records.
   */
  readonly supportReference: Signal<string | null> = computed(() =>
    problemSupportReference(this._problem()),
  );

  /**
   * The application failure code from the last failure, or null when the document carried none. Read by
   * the utility that parses the code out of the document's `type` member, which is the ONLY channel the
   * code travels on — there is no separate code member to read, so a consumer that does not parse `type`
   * cannot key on a code at all.
   */
  readonly failureCode: Signal<string | null> = computed(() => failureCode(this._problem()));

  /**
   * The per-field validation failures from the last failure, or null when it was not a validation
   * failure. ⚠ READ WITH BRACKET ACCESS — `errors['Username']`, never `errors.Username`.
   */
  readonly validationErrors: Signal<ProblemDetailsErrors | null> = computed(() => {
    const problem = this._problem();

    return isValidationProblemDetails(problem) ? problem.errors : null;
  });

  /**
   * Whether the last failure was the rate limiter refusing because the caller is early. A DISTINCT,
   * NON-ALARMING STATE, kept separate from a refused credential on purpose.
   */
  readonly rateLimited: Signal<boolean> = computed(
    () => this._failureStatus() === RATE_LIMITED_STATUS,
  );

  // -------------------------------------------------------------------------
  // THE VERIFICATION LADDER
  // -------------------------------------------------------------------------

  /**
   * Whether the verification field has been revealed and should stay on screen. ⚠ THIS IS THE STATE THE
   * LADDER TURNS ON, AND IT IS THE ONE PIECE OF LEGACY CONTROL STATE THAT SURVIVES THE .
   */
  readonly verificationRequired: Signal<boolean> = this._verificationRequired.asReadonly();

  /**
   * The outcome of the last turn of the verification ladder, or null when the ladder has not been
   * reached. Carries which of EXACTLY THREE codes applies, its wording, whether this turn is the one that
   * reveals the field, and the severity to present at.
   */
  readonly verificationPrompt: Signal<VerificationPrompt | null> =
    this._verificationPrompt.asReadonly();

  // COMMANDS
  // ⚠ EVERY COMMAND BODY IS WRAPPED IN `defer`, AND THAT IS LOAD-BEARING RATHER THAN STYLISTIC. Setting the
  // phase eagerly — before the returned observable is subscribed — would make the paragraph above false for
  // that one slice: a command that was built and then discarded would leave the store reporting itself busy
  // forever, and a consumer showing a spinner while `isBusy()` holds would never stop.

  /**
   * Exchanges credentials for a session, and advances the verification ladder when the server refuses
   * because the account is awaiting verification. Ported from
   * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197`.
   *
   * @param request The credentials, and the verification code when one was supplied.
   * @returns The signed-in identity.
   */
  login(
    request: LoginRequest,
    selector?: LoginPortalSelector | null,
  ): Observable<CurrentUser> {
    return defer(() => {
      // Captured here rather than stored in a slice. The ladder needs to know what was submitted on THIS
      // attempt in order to tell a wrong code from a missing one, and a closure supplies that without
      // publishing a value the person typed.
      const submittedCode: string | null =
        request.verificationCode === undefined ? null : request.verificationCode;

      const ticket = this.claimPhase('authenticating');

      this.clearFailure();

      // The flag records that a sign-out could not confirm the server had withdrawn the refresh credential,
      // and the sign-in screen renders it — which is right, because sign-out sends the operator there and
      // it is the one place the report is certain to be seen.
      this._revocationOutstanding.set(false);

      // ⚠ THE DOMAIN STORES ARE EMPTIED BEFORE THE ATTEMPT, NOT AFTER IT SUCCEEDS.
      this.sessionTeardown.purge('signedIn');

      this.tokenStorage.clear();

      this.renewalInFlight = null;

      const startedAt = this.tokenStorage.generation();

      return this.auth.login(request, selector).pipe(
        // The credential exchange answers with an AUTHORITY-MINIMISED identity and the access token carries
        // no role or permission claims, so the caller's roles and granted permission codes are read from
        // the describe-caller operation with the freshly issued token presented explicitly.
        map((response) => sessionFromLoginResponse(response)),
        switchMap((session) =>
          this.auth.me(session.accessToken).pipe(map((user) => ({ ...session, user }))),
        ),
        map((session) => {
          // The test is read ONCE, before the store, because storing a session advances the epoch itself —
          // asking again afterwards would answer no for the attempt that had just legitimately won.
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
          // Conditioned for the LATE FAILURE above: an older attempt's rejection must not discard the
          // session a newer one has already established.
          if (this.tokenStorage.isCurrentGeneration(startedAt)) {
            this.tokenStorage.clear();
          }

          this.recordFailure(error);
          this.advanceVerificationLadder(submittedCode);

          return throwError(() => error);
        }),
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Renews the session from the stored refresh token, coalescing concurrent callers onto one request.
   * Exposed so a caller may renew deliberately — a route resolver bootstrapping a reload, for instance.
   *
   * @returns The renewed session.
   */
  refreshSession(): Observable<AuthSession> {
    return defer(() => {
      const ticket = this.claimPhase('refreshing');

      this.clearFailure();

      const boundary = this.sessionTeardown.generation();

      // The phase ticket, the failure record and the handlers below are PER SUBSCRIBER, while the request
      // itself is shared.
      return this.renewSession().pipe(
        tap(() => {
          this.clearFailure();
        }),
        catchError((error: unknown) => {
          // ⚠ THE SESSION IS DISCARDED AND THE PROBLEM DOCUMENT IS DELIBERATELY NOT RECORDED, and this is a
          // correction rather than an omission.
          if (this.sessionTeardown.isCurrent(boundary)) {
            this.discardSession('renewalRefused');
          }

          return throwError(() => error);
        }),
        // Covers success, failure and cancellation alike, and only while this renewal still owns the phase.
        // `discardSession` no longer sets the phase itself, so this is the single place a renewal returns
        // the store to idle.
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Renews the session, coalescing concurrent callers onto one request and committing the rotated pair
   * exactly once — and doing nothing else. THE PRIMITIVE BENEATH {@link AuthStore.refreshSession}, and
   * the entry point for the REFUSED-REQUEST path in `core/interceptors/auth.interceptor.ts`.
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

    // The epoch this renewal belongs to, captured before the request is issued.
    const startedAt = this.tokenStorage.generation();

    const renewal = this.auth.refresh(body).pipe(
      map((response) => sessionFromLoginResponse(response)),
      switchMap((session) =>
        this.auth.me(session.accessToken).pipe(map((user) => ({ ...session, user }))),
      ),
      // Storing here rather than at the call site is what guarantees the rotated refresh token replaces the
      // consumed one exactly once, however many subscribers are sharing this request.
      tap((session) => {
        if (this.tokenStorage.isCurrentGeneration(startedAt)) {
          this.tokenStorage.store(session);
        }
      }),
      catchError((error: unknown) => {
        if (this.tokenStorage.isCurrentGeneration(startedAt)) {
          this.tokenStorage.clear();

          this.announceRenewalRefusal(error);
        }

        return throwError(() => error);
      }),
      // Clears the slot on completion, error and unsubscription alike, so a later refusal starts a fresh
      // renewal rather than replaying this one's outcome forever. Placed before `shareReplay` so it
      // observes the source, not each subscriber.
      finalize(() => {
        if (this.renewalInFlight === renewal) {
          this.renewalInFlight = null;
        }
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    this.renewalInFlight = renewal;

    return renewal;
  }

  /** @returns Completion , shared by every caller that arrives while a drain is running. */
  retryOutstandingRevocation(): Observable<void> {
    const inFlight = this.revocationRetryInFlight;

    if (inFlight !== null) {
      return inFlight;
    }

    const drain: Observable<void> = defer(() => {
      const retained: readonly string[] = this.tokenStorage.pendingRevocations();

      if (retained.length === 0) {
        return of(undefined);
      }

      // The epoch this drain belongs to, captured before the first request goes out.
      const startedAt = this.tokenStorage.generation();

      return from(retained).pipe(
        concatMap((credential: string) => this.withdrawRetained(credential)),
        toArray(),
        map(() => {
          this.reportRetainedResidue(startedAt);

          return undefined;
        }),
      );
    }).pipe(
      // Released by OBJECT IDENTITY on completion, error and unsubscription alike, for the reason set out
      // on the renewal slot: the question is whether the slot still holds THIS drain, which is a question
      // about identity rather than about the session.
      finalize(() => {
        if (this.revocationRetryInFlight === drain) {
          this.revocationRetryInFlight = null;
        }
      }),
      // `refCount: false` keeps the requests alive even if the screen that started the drain is destroyed
      // mid-ladder — a navigation away must not abandon a withdrawal that is the only thing standing
      // between a refused sign-out and a session live until its absolute expiry.
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    this.revocationRetryInFlight = drain;

    return drain;
  }

  /**
   * Withdraws one retained credential, retiring it when the server has finished with it. Never errors, so
   * one credential the server will not withdraw cannot abandon the rest of the drain behind it.
   *
   * @param refreshToken The retained credential to withdraw.
   * @returns Completion , whatever the server answered.
   */
  private withdrawRetained(refreshToken: string): Observable<void> {
    return this.revokeWithRetry(refreshToken).pipe(
      map(() => {
        this.tokenStorage.releasePendingRevocation(refreshToken);

        return undefined;
      }),
      catchError((cause: unknown) => {
        // A TERMINAL REFUSAL RETIRES THE CREDENTIAL; A TRANSIENT ONE KEEPS IT. A 400, a 404 or a 422 is the
        // server saying this value can never name a session, so retaining it would leave a residue nothing
        // could ever clear.
        if (isTerminalRevocationRefusal(cause)) {
          this.tokenStorage.releasePendingRevocation(refreshToken);
        }

        return of(undefined);
      }),
    );
  }

  /**
   * Records whether any residue survived a completed drain. Reads the retention set rather than
   * accumulating outcomes, so the report describes what is actually still held — including a credential
   * retained by a sign-out that happened while the drain was running, which the drain itself did not
   * attempt.
   *
   * @param startedAt The session epoch the drain began under.
   */
  private reportRetainedResidue(startedAt: number): void {
    if (!this.tokenStorage.isCurrentGeneration(startedAt)) {
      return;
    }

    this._revocationOutstanding.set(this.tokenStorage.pendingRevocations().length !== 0);
  }

  /**
   * Posts one revocation, retrying only outcomes that can plausibly succeed later. The bound is
   * deliberate on both sides.
   *
   * @param refreshToken The credential to withdraw.
   * @returns Completion , or the last error when every permitted attempt failed.
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

  /**
   * Ends the session, locally without condition. `FormsAuthentication.SignOut` has no stateless
   * counterpart.
   *
   * @returns Completion of the revocation attempt.
   */
  logout(): Observable<void> {
    return defer(() => {
      const ticket = this.claimPhase('signingOut');

      const refreshToken = this.tokenStorage.refreshToken();

      // ⚠ THE CREDENTIAL IS PUT ASIDE BEFORE THE SESSION IS DISCARDED.
      if (refreshToken !== null && refreshToken.length !== 0) {
        this.tokenStorage.retainForRevocation(refreshToken);
      }

      this.renewalInFlight = null;

      this.discardSession('signedOut');
      this.clearFailure();

      // `SessionLifecycleService.signOut` — the one path that reaches this method — runs its teardown in a
      // `finalize`, so the queue is emptied AFTER this line executes: once by
      // `SessionTeardownService.purge` and again by {@link AuthStore.reset}'s own discard.

      if (refreshToken === null || refreshToken.length === 0) {
        // Nothing to withdraw, so nothing is posted: an empty credential would be answered 400 and reported
        // as an outstanding revocation, which would be a false alarm. Local sign-out has already happened
        // above, which is the part that was asked for.
        return of(undefined).pipe(finalize(() => this.releasePhase(ticket)));
      }

      // No envelope here, and none is expected. Sign-out answers 204, which HTTP forbids from
      // carrying a body, so there is nothing to unwrap.
      // ⚠ ONE ATTEMPT HERE, THE BOUNDED LADDER LATER. Signing out must not hold the operator on a spinner
      // while a backoff ladder plays out against an unreachable server - the local sign-out has already
      // happened and the redirect is waiting on this.
      return this.auth.logout({ refreshToken }).pipe(
        map(() => {
          // Retired only by a withdrawal that actually succeeded, so a screen that reported an
          // unconfirmed sign-out stops reporting it once a later one is confirmed.
          this.tokenStorage.releasePendingRevocation(refreshToken);

          // ⚠ DERIVED FROM WHAT IS STILL HELD, NOT SET TO FALSE OUTRIGHT. Retiring this credential is not
          // the same as there being no residue: an earlier sign-out whose withdrawal was refused has its
          // own credential retained, and reporting "confirmed" because a LATER session was withdrawn
          // cleanly is precisely the silence this finding was about.
          this._revocationOutstanding.set(this.tokenStorage.pendingRevocations().length !== 0);

          return undefined;
        }),
        catchError((cause: unknown) => {
          // A TERMINAL REFUSAL RELEASES THE CREDENTIAL; A TRANSIENT ONE KEEPS IT. A 400 or a 404 is the
          // server saying this value can never name a session, so retaining it would leave a notice nothing
          // could ever clear.
          const terminal: boolean = isTerminalRevocationRefusal(cause);

          if (terminal) {
            this.tokenStorage.releasePendingRevocation(refreshToken);
          }

          this._revocationOutstanding.set(!terminal);

          // The residue is RECORDED here and ANNOUNCED elsewhere, for the reason set out beside the removed
          // confirmation above: a statement raised from inside this method is erased by the teardown that
          // follows it.

          this.notifications.retainAcrossNavigation();

          return of(undefined);
        }),
        // The discard above already ran, so this exists solely to return the phase to idle — on success, on
        // a revocation failure and on an early unsubscription alike.
        finalize(() => this.releasePhase(ticket)),
      );
    });
  }

  /**
   * Re-reads the caller's own identity, roles and permission keys from the server. The one authorised
   * operation of the four, and the way a caller learns its own entitlements.
   *
   * @returns The caller's identity.
   */
  loadCurrentUser(): Observable<CurrentUser> {
    return defer(() => {
      const ticket = this.claimPhase('loadingIdentity');

      // An identity read is the LAST thing that can republish a signed-out account, and it is the easiest
      // to overlook because it looks harmless — it only reads.
      const startedAt = this.tokenStorage.generation();

      this.clearFailure();

      return this.auth.me().pipe(
        tap((user) => {
          if (this.publishIdentity(user, startedAt)) {
            this.clearFailure();
          }
        }),
        catchError((error: unknown) => {
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
   * Discards the recorded failure without touching the session or the ladder. For a consumer dismissing a
   * banner.
   */
  clearError(): void {
    this.clearFailure();
  }

  /**
   * Returns the store to its initial state. The explicit reset referred to by {@link
   * AuthStore.verificationRequired} — the only way, besides a successful sign-in or a sign-out, that the
   * ladder returns to its first rung.
   */
  reset(): void {
    this.discardSession('signedOut');
    this.clearFailure();
  }

  /** Discards the session locally, KEEPING the recorded failure. */
  endSession(): void {
    this.discardSession('renewalRefused');
  }

  // -------------------------------------------------------------------------
  // PRIVATE STATE TRANSITIONS
  // -------------------------------------------------------------------------

  /**
   * Advances the verification ladder by one turn after a refused sign-in.
   *
   * @param submittedCode The verification code sent with the attempt, where null and the empty string
   * both mean none was supplied and neither is rewritten into the other.
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
      // Set once and left set. The utility reports this only on the turn that first reveals the field, so
      // persistence across later attempts is exactly what not clearing it achieves.
      this._verificationRequired.set(true);
    }
  }

  /** Returns the verification ladder to its first rung. */
  private resetVerificationLadder(): void {
    this._verificationRequired.set(false);
    this._verificationPrompt.set(null);
  }

  /**
   * Reports a silent renewal that was refused, unless the refusal was itself an expiry.
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

  /**
   * Records a failed command's outcome. Sets the phase back to idle, because a failed command is no
   * longer in flight.
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
   * Discards every trace of the session from local state. Clearing the custodian is idempotent and is
   * performed here even though the commands that fail clear it too, conditioned on their own epoch.
   */
  private discardSession(reason: SessionResetReason): void {
    this.tokenStorage.clear();
    this._identity.set(null);
    this.resetVerificationLadder();
    this._revocationOutstanding.set(false);
    this.sessionTeardown.purge(reason);
  }

  /**
   * Takes ownership of {@link AuthStore.phase} for a command that is starting. Sets the phase and returns
   * the ticket the command must present to {@link releasePhase}.
   *
   * @param phase The phase the starting command occupies.
   * @returns The ticket identifying this command's ownership.
   */
  private claimPhase(phase: AuthStorePhase): number {
    this.phaseTicket += 1;
    this._phase.set(phase);

    return this.phaseTicket;
  }

  /** @param ticket The ticket returned by the matching {@link claimPhase} call. */
  private releasePhase(ticket: number): void {
    if (this.phaseTicket === ticket) {
      this._phase.set('idle');
    }
  }

  /**
   * Publishes a freshly read identity, if it still belongs to the session being held. ⚠ THE ONE PLACE
   * `_identity` IS WRITTEN FROM AN ASYNCHRONOUS RESULT, and therefore the one place the check can be
   * enforced.
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
   * Records an identity together with the auth epoch it describes. ⚠ THE ONLY WRITER OF `_identity` THAT
   * SETS A VALUE, so the stamp cannot be omitted at a call site. Reading the epoch here rather than
   * accepting it as an argument is deliberate: the stamp must be the epoch AT THE MOMENT OF PUBLICATION,
   * not the one captured when the work started.
   *
   * @param user The identity to publish.
   */
  private stampIdentity(user: CurrentUser): void {
    this._identity.set({ user, generation: this.tokenStorage.generation() });
  }
}

/**
 * A fetched identity together with the auth epoch it was published under. Internal to this module and
 * deliberately not exported: the stamp is bookkeeping that keeps {@link AuthStore.currentUser} honest,
 * and no consumer outside this file has any business reading or reasoning about it.
 */
interface StampedIdentity {
  /** The identity the server described. */
  readonly user: CurrentUser;

  /**
   * The value of the auth epoch when {@link user} was published. Compared for exact equality against the
   * live epoch.
   */
  readonly generation: number;
}

/** The role and permission list handed out when no session is held. */
const EMPTY_STRINGS: readonly string[] = Object.freeze([]);

/**
 * Reads the transport status off a failed request. Read STRUCTURALLY rather than by narrowing to the
 * transport library's error type, because this file must not import from that library at all — building
 * requests is the services' responsibility and taking the dependency here would blur that boundary for
 * one property read.
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
 * Reads the problem document out of a failed request's body. ⚠ ONLY THE BODY IS TESTED, AND THE OUTER
 * FAILURE DELIBERATELY IS NOT. The narrowing predicate is permissive about absence — it accepts any
 * object carrying at least one well-typed standard member — and a failed-response object carries a
 * numeric `status` of its own.
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
