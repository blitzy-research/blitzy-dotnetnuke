import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, finalize, map, of, shareReplay, tap, throwError } from 'rxjs';

import { AUTH_ENDPOINTS } from '../config/api-endpoints';
import {
  AuthSession,
  CurrentUser,
  LoginRequest,
  LoginResponse,
  RefreshTokenRequest,
  sessionFromLoginResponse,
} from '../models/auth.model';
import { TokenStorageService } from './token-storage.service';

/**
 * Owns the three token operations: sign in, refresh, sign out.
 *
 * Deliberately narrow. It performs API communication and updates the stored
 * session, and it holds no screen state, no form state and no business rules —
 * Angular services are restricted to API communication by the migration
 * discipline, and every authorisation decision belongs to the server.
 *
 * The single-flight refresh is the reason this is a service rather than a helper
 * inside the interceptor. Interceptors are functions, so per-instance state has
 * nowhere to live in them except module scope, and module scope leaks between test
 * cases and between application instances. Holding the in-flight refresh on an
 * injectable keeps it scoped to the injector that created it.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tokenStorage = inject(TokenStorageService);

  /**
   * The refresh currently in progress, or null when none is.
   *
   * This is what makes a burst of simultaneous 401 responses produce ONE refresh
   * call rather than one per failed request. Without it, six parallel list
   * requests expiring together would each present the same refresh token; the
   * first would rotate it and the remaining five would present a token that had
   * already been used, which the server treats as a replay and answers by revoking
   * the account's entire refresh-token family — signing the person out precisely
   * because the client tried to keep them signed in.
   */
  private refreshInFlight: Observable<AuthSession> | null = null;

  /** The signed-in identity, or null. Re-exposed so consumers need one injection. */
  readonly currentUser = this.tokenStorage.currentUser;

  /** Whether a session is held. */
  readonly isAuthenticated = this.tokenStorage.isAuthenticated;

  /**
   * Exchanges credentials for a token pair and stores the resulting session.
   *
   * A failure is re-thrown unchanged so the caller can render the server's problem
   * document, which distinguishes bad credentials from a locked-out account and
   * from a password the server refuses to accept as secure. The stored session is
   * cleared first, so a failed sign-in cannot leave an earlier session in place.
   *
   * @param request The credentials, optionally naming the tenant.
   * @returns The signed-in identity.
   */
  login(request: LoginRequest): Observable<CurrentUser> {
    this.tokenStorage.clear();

    return this.http.post<LoginResponse>(AUTH_ENDPOINTS.login, request).pipe(
      map(sessionFromLoginResponse),
      tap((session) => this.tokenStorage.store(session)),
      map((session) => session.user),
    );
  }

  /**
   * Exchanges the stored refresh token for a new pair, coalescing concurrent
   * callers onto one request.
   *
   * Fails immediately when no refresh token is held, rather than posting an empty
   * one: the server would answer 400, and the caller would have to distinguish
   * that from a genuine rejection. The error is produced lazily inside the returned
   * observable so that this method never throws synchronously — an interceptor
   * calling it inside a `catchError` must be able to rely on getting an observable
   * back.
   *
   * On failure the session is discarded. A refresh token that the server refuses
   * cannot be retried, and keeping it would mean re-presenting it on the next
   * 401 and being refused again.
   *
   * @returns The refreshed session.
   */
  refresh(): Observable<AuthSession> {
    const inFlight = this.refreshInFlight;

    if (inFlight !== null) {
      return inFlight;
    }

    const refreshToken = this.tokenStorage.refreshToken();

    if (refreshToken === null || refreshToken.length === 0) {
      return throwError(() => new Error('No refresh token is held, so the session cannot be renewed.'));
    }

    const body: RefreshTokenRequest = { refreshToken };

    const request = this.http.post<LoginResponse>(AUTH_ENDPOINTS.refresh, body).pipe(
      map(sessionFromLoginResponse),
      // Storing here rather than at the call site is what guarantees the rotated
      // refresh token replaces the consumed one exactly once, however many
      // subscribers are sharing this request.
      tap((session) => this.tokenStorage.store(session)),
      catchError((error: unknown) => {
        this.tokenStorage.clear();

        return throwError(() => error);
      }),
      // Clears the slot on completion, error and unsubscription alike, so a later
      // 401 starts a fresh refresh rather than replaying this one's outcome
      // forever. Placed before `shareReplay` so it observes the source, not each
      // subscriber.
      finalize(() => {
        this.refreshInFlight = null;
      }),
      // `refCount: false` keeps the single subscription to the HTTP request alive
      // even if every current subscriber unsubscribes, so a cancelled request does
      // not silently abandon the refresh for the subscribers still waiting.
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    this.refreshInFlight = request;

    return request;
  }

  /**
   * Revokes the stored refresh token and discards the session.
   *
   * Local state is cleared whatever the server answers, and the observable
   * completes successfully even when the call fails. A person who asks to sign out
   * must end up signed out on this device; leaving the session in place because a
   * revocation request failed would be the opposite of what they asked for, and
   * they cannot act on the error in any case.
   *
   * MIGRATION: sign-out has no effect on an access token that has already been
   * issued. A bearer token cannot be recalled, so this revokes the refresh token
   * and the access token remains valid until it expires — which is why its
   * lifetime is short. The legacy `FormsAuthentication.SignOut` cleared a cookie
   * and took effect at once.
   */
  logout(): Observable<void> {
    const refreshToken = this.tokenStorage.refreshToken();

    this.tokenStorage.clear();
    this.refreshInFlight = null;

    if (refreshToken === null || refreshToken.length === 0) {
      return of(undefined);
    }

    const body: RefreshTokenRequest = { refreshToken };

    return this.http.post<void>(AUTH_ENDPOINTS.logout, body).pipe(
      map(() => undefined),
      catchError(() => of(undefined)),
    );
  }
}
