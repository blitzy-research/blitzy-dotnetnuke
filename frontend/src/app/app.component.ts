import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';

import { AuthStore } from './core/state/auth.store';
import { SessionLifecycleService } from './core/state/session-lifecycle.service';
import { ShellComponent } from './layout/shell/shell.component';

import type { Signal } from '@angular/core';

/**
 * Where an operator is sent once their session has ended.
 *
 * A private copy of the value the two route gates and the bearer interceptor each hold
 * — `core/guards/auth.guard.ts:L83`, `core/guards/permission.guard.ts:L133` and
 * `core/interceptors/auth.interceptor.ts:L40` — rather than an import of one of them.
 * None of the three exports it, and the reason `permission.guard.ts:L127-L131` gives for
 * the duplication applies here too: the four agreeing by construction matters less than
 * no one of them reaching into another. The route itself is declared once, in
 * `app.routes.ts`.
 */
const SIGN_IN_ROUTE = '/login';

/**
 * The application's root component.
 *
 * Mounts the shell and supplies it with the session. `src/main.ts` bootstraps this
 * component into the `<app-root>` element declared by `src/index.html`, and the shell it
 * renders owns every fixed region of the administration console — skip link, banner,
 * secondary navigation, routed outlet and footer.
 *
 * WHY THE ROOT IS THIS THIN
 * -------------------------
 * A root component that rendered regions directly would put layout in two places: here,
 * and in the shell that also renders layout. Keeping the root to a single element means
 * the shell can be mounted in a specification, or nowhere at all, without the root's
 * contents having to be reasoned about. The paired stylesheet is written to exactly that
 * expectation — its own comment records that "the template mounts a single element" — and
 * it therefore styles only the host box: a block display so that a custom element does
 * not shrink-wrap the shell, and a viewport-height floor so that a route rendering very
 * little still fills the window.
 *
 * WHY THE SESSION IS BOUND HERE AND NOWHERE ELSE
 * ----------------------------------------------
 * The shell FORWARDS session facts rather than resolving them: it takes a display name,
 * an in-flight flag and a sign-out output, hands the first two to the banner and
 * re-emits the third. The banner is stricter still — its own comment records that it
 * "does not end the session, clear any credential or navigate anywhere: the container
 * that supplied the session owns all three, and duplicating any of them here would put
 * two authorities in the application for one decision".
 *
 * This component is that container. It is the only place in the workspace that mounts the
 * shell, so binding the session here rather than in an intermediate wrapper keeps the
 * chain from the token custodian to the rendered name to exactly three links, and means
 * no change to the shell, the banner or any of their specifications was needed to wire
 * it.
 *
 * WHAT THIS COMPONENT DELIBERATELY DOES NOT DO
 * --------------------------------------------
 * It does not discard the session itself. Ending a session means cancelling every
 * in-flight request and clearing every domain slice as well as the credentials, and
 * `core/state/session-lifecycle.service.ts` owns that invariant in one place precisely so
 * that no caller has to remember the whole list. This component asks; the coordinator
 * does.
 *
 * It does not decide WHETHER a session has ended either. It knows only that the operator
 * asked, which is the one thing the coordinator's documentation says this caller
 * contributes.
 *
 * It does navigate, and that split is the coordinator's design rather than a leak:
 * `session-lifecycle.service.ts:L47-L52` states that it deliberately does not navigate,
 * because where a caller should end up differs by caller — "the shell sends the operator
 * to the sign-in screen", while the bearer interceptor is mid-way through re-throwing the
 * server's own response and must not have that outcome displaced by a routing failure.
 *
 * MIGRATION: the legacy root was `Website/Default.aspx`, a single Web Forms page whose
 * code-behind (`Website/Default.aspx.vb`) resolved the tenant, selected a skin, injected
 * that skin's stylesheets and instantiated the requested page's modules — all per
 * request, on the server. Every one of those responsibilities has moved: tenant
 * resolution to the API's alias-resolution middleware, skin selection out of scope with
 * skinning itself, stylesheet composition to the build-time entry point
 * `src/styles.scss`, and page composition to the router. What remains on the client is
 * the mount point this component provides and the session it hands the chrome.
 *
 * MIGRATION: signing out changes mechanism entirely. `FormsAuthentication.SignOut`
 * cleared one cookie and the next request rebuilt every page from scratch on the server,
 * so there was no client-held state to discard. A signed bearer token cannot be recalled,
 * so signing out here is revocation plus client-side discard, and the discard is the part
 * that has no legacy counterpart at all.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ShellComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent {
  /** The session store, read for the signed-in identity and the sign-out phase. */
  private readonly authStore = inject(AuthStore);

  /**
   * The one place a session ends.
   *
   * Injected rather than reached through {@link AuthStore.logout} directly, and the
   * distinction is the whole point of the coordinator: the store's own sign-out discards
   * the credentials and the identity and knows nothing about the portals, accounts, roles
   * or exported module documents the domain stores are still holding. Calling the store
   * would leave every one of those slices resident and legible to whoever signs in next.
   */
  private readonly session = inject(SessionLifecycleService);

  /** Used to send the operator to the sign-in screen once the session has ended. */
  private readonly router = inject(Router);

  /**
   * Bounds the sign-out subscription to this component's lifetime.
   *
   * The coordinator's command is cold and root-provided, so it would never end the
   * subscription itself. In practice the root outlives every navigation and is destroyed
   * only when the application is, but binding the subscription to a lifetime that is
   * actually shorter than the store's is what makes the leak impossible rather than
   * merely unlikely.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The name the banner renders for the signed-in account, or `undefined` when no
   * account is signed in.
   *
   * ⚠ THE FALLBACK TO THE ACCOUNT KEY IS A PUBLISHED CONTRACT, NOT A CONVENIENCE.
   * `core/models/auth.model.ts:L509-L518` documents `displayName` as never null and never
   * absent because the column is `nvarchar(128) NOT NULL` defaulting to the empty string,
   * and states outright that "a caller with no display name has `""`, and the shell
   * renders `username` in its place". Implementing that substitution is this component's
   * obligation as the container that supplies the session.
   *
   * It also prevents a concrete defect rather than tidying a cosmetic one. The banner
   * decides whether an account is signed in by testing the name it was given
   * (`header.component.ts:L285-L287`), so a blank name renders no session cluster — and
   * the sign-out control lives inside that cluster. Passing `""` straight through would
   * therefore leave an operator whose display name happens to be empty signed in with no
   * way to sign out.
   *
   * ⚠ SENTINEL DISCIPLINE. Presence is decided by an explicit comparison against `null`
   * and by an explicit length test after trimming, never by truthiness. The empty string
   * is exactly what the legacy null contract returns for a missing string
   * (`Library/Components/Shared/Null.vb:L71-L75`), so `""` means "not recorded" here and
   * has to be treated as absence — while remaining a value the API genuinely sends rather
   * than one this client may reject.
   *
   * Returns `undefined` rather than `null` for the absent case because that is what the
   * shell's optional input declares, and the banner tests for `undefined` specifically.
   */
  protected readonly userName: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    if (user === null) {
      return undefined;
    }

    const displayName: string = user.displayName.trim();

    if (displayName.length > 0) {
      return displayName;
    }

    // The documented substitution. The account key is `NOT NULL` and unique, so this is a
    // value rather than a second fallback — but it is trimmed and length-tested on the
    // same terms, because a container that trusted one field and not the other would
    // reintroduce the blank-name defect one level down.
    const username: string = user.username.trim();

    return username.length > 0 ? username : undefined;
  });

  /**
   * Whether a sign-out is in flight.
   *
   * Read straight from the store's phase rather than mirrored into a local flag, so there
   * is no second source of truth to fall out of step with the request it describes. The
   * banner disables its sign-out control while this is true.
   */
  protected readonly signingOut: Signal<boolean> = this.authStore.isSigningOut;

  /**
   * Ends the session the operator asked to end, then sends them to the sign-in screen.
   *
   * ⚠ SUBSCRIBED EXACTLY ONCE, AND THAT IS WHAT ISSUES THE REQUEST. The coordinator's
   * command is cold by design — `session-lifecycle.service.ts` documents its return as
   * "COLD: it must be subscribed for the request to be issued, exactly once" — so a
   * command nobody subscribes to revokes nothing, and a command subscribed twice revokes
   * twice.
   *
   * THE RE-ENTRY GUARD CLOSES A WINDOW THE MARKUP CANNOT. The banner already guards the
   * gesture twice over, through its template's `[disabled]="signingOut"` binding and
   * again in its own handler, so this is a third check — and it is not redundant with
   * either. Both of the banner's guards read the flag as it stood at the last change
   * detection, whereas {@link AuthStore.logout} sets the phase SYNCHRONOUSLY at subscribe
   * time (it opens with `defer`), so this test sees the update immediately. Two emissions
   * within a single tick — a synthetic pair from a specification, or a second affordance
   * added later — would pass both of the banner's guards and are stopped here.
   *
   * NAVIGATION HAPPENS ON BOTH EXITS, DELIBERATELY. The coordinator discards the session
   * in a `finalize`, so the session is gone whether the revocation succeeded or failed;
   * leaving the operator on an administration screen with no credentials would strand them
   * on a view whose every request is about to be refused. An observable cannot both error
   * and complete, so exactly one of the two handlers below runs.
   *
   * ⚠ WHAT ACTUALLY REACHES THE ERROR HANDLER IS NOT A FAILED REQUEST, and stating that
   * precisely matters more than the handler itself. {@link AuthStore.logout} absorbs the
   * revocation's own failure — the server answers 204 whatever it finds, a network fault is
   * treated the same way, and local sign-out has already happened unconditionally — so a
   * refused or unreachable endpoint arrives here as a COMPLETION, and the navigation below
   * happens through the `complete` handler. The `error` handler covers the remaining case: a
   * throw from inside the teardown itself, since both the store's discard and the
   * coordinator's slice clearing run in a `finalize` whose exception would propagate to
   * this subscriber. It is present because the coordinator PUBLISHES an error exit — its
   * own documentation names "a failed one" as one of three — and coding to a dependency's
   * published contract rather than to its current implementation is what keeps this
   * component correct if that absorption is ever removed.
   *
   * MIGRATION: the absorption used to sit one layer lower, on
   *   `core/services/auth.service.ts`, which discarded the failure as well as absorbing it and
   *   so reported a clean sign-out while the renewal credential was still live on the server.
   *   The transport now propagates the refusal and the store absorbs it DELIBERATELY, recording
   *   it in {@link AuthStore.revocationOutstanding} and announcing it. Nothing about this
   *   component's two exits changes: a refused revocation still arrives as a completion, which
   *   is what a person who asked to sign out is owed.
   *
   * THE ERROR IS ABSORBED RATHER THAN SURFACED, and this is the one place in the
   * application where that is the right call. The session has already been discarded by
   * the time the handler runs, so there is nothing left for the operator to act on and
   * nothing to retry — a person who asked to sign out has ended up signed out. Rethrowing
   * would raise an unhandled error on a path that succeeded from the operator's point of
   * view, and would do it while the application is mid-teardown of its own session.
   *
   * The navigation itself is not awaited and its rejection is swallowed, matching how the
   * rest of the application navigates: a navigation the router refuses is not this
   * component's failure to report, and the session has already ended either way.
   */
  protected onSignOut(): void {
    if (this.signingOut()) {
      return;
    }

    this.session
      .signOut()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        complete: () => this.goToSignIn(),
        error: () => this.goToSignIn(),
      });
  }

  /**
   * Sends the operator to the sign-in screen.
   *
   * No `returnUrl` is carried, and the omission is the point. The two route gates attach
   * one when they INTERRUPT a navigation, so that a caller who is sent to sign in is
   * returned to the address they asked for. Signing out is not an interruption: the
   * operator chose to leave, and returning them to the screen they deliberately left —
   * or worse, restoring an address that named a record the next operator has no right to
   * know exists — would defeat the discard that just happened.
   */
  private goToSignIn(): void {
    void this.router.navigate([SIGN_IN_ROUTE]).catch(() => false);
  }
}
