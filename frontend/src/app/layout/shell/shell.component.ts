import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Input,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Location } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';

import { environment } from '../../../environments/environment';
import { SIGN_IN_ROUTE as SHARED_SIGN_IN_ROUTE } from '../../core/config/app-routes.config';
import { AuthStore } from '../../core/state/auth.store';
import { SessionLifecycleService } from '../../core/state/session-lifecycle.service';
import { FooterComponent } from '../footer/footer.component';
import { HeaderComponent } from '../header/header.component';
import { NotificationListComponent } from '../notifications/notification-list.component';
import { SidebarComponent } from '../sidebar/sidebar.component';

import type { Signal } from '@angular/core';

/** The fragment identifier the skip link targets, and therefore the identifier the main region must carry. */
/**
 * The permission key that answers "may this caller edit content anywhere in the tenant". The server's own
 * spelling, from the `PermissionKey` enumeration the API publishes as plain strings
 * (`CurrentUserDto.Permissions`).
 */
const CONTENT_EDIT_PERMISSION_KEY = 'EDIT';

const MAIN_REGION_ID = 'main-content';

/**
 * The fragment the skip link's `href` ends with. ⚠ A FRAGMENT ALONE IS NOT A SAFE `href` IN THIS
 * APPLICATION, which is why this is only part of one.
 */
const SKIP_LINK_FRAGMENT = `#${MAIN_REGION_ID}`;

/** Where an operator is sent once their session has ended. */
const SIGN_IN_ROUTE = SHARED_SIGN_IN_ROUTE;

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    HeaderComponent,
    SidebarComponent,
    FooterComponent,
    NotificationListComponent,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'shell' },
})
export class ShellComponent {
  /**
   * The banner's identity text, forwarded to the banner component. Defaults to the build-time application
   * name so that the shell is correct with no binding at all.
   */
  @Input() applicationName: string = environment.applicationName;

  /** The session store, read for the signed-in identity and the sign-out phase. */
  private readonly authStore = inject(AuthStore);

  /**
   * The one place a session ends. Injected rather than reached through {@link AuthStore.logout} directly,
   * and the distinction is the whole point of the service: the store's own sign-out discards the
   * credentials and the identity and knows nothing about the portals, accounts, roles or exported module
   * documents the domain stores are still holding.
   */
  private readonly session = inject(SessionLifecycleService);

  private readonly router = inject(Router);

  /**
   * Turns a router-internal address into the address a browser can be handed. ⚠ THE TWO ARE NOT THE SAME
   * ADDRESS WHEN THIS TENANT IS ADDRESSED BENEATH A PATH SEGMENT, and that is the whole reason this is
   * injected. `Router.url` is INTERNAL — it is expressed relative to the application's base href, so a
   * child portal served at `/acme/` reports `/roles` for the screen whose real address is `/acme/roles`.
   */
  private readonly location = inject(Location);

  /** Bounds the sign-out subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Retires notifications that a change of screen has made stale. ⚠ THIS BELONGS TO THE SHELL AND NOWHERE
   * ELSE, because the shell is the one component that both outlives every route and renders the queue.
   */
  /** The skip link's `href`, path-qualified against the address currently showing. */
  private readonly skipLinkHref = signal<string>(this.composeSkipLinkHref());

  private readonly notificationSweep = this.router.events
    .pipe(takeUntilDestroyed(this.destroyRef))
    .subscribe((event) => {
      if (event instanceof NavigationEnd) {
        // ⚠ THE NOTIFICATION SWEEP IS NOT DRIVEN FROM HERE, AND THAT IS A CORRECTION. This handler ran on
        // every completed navigation, including one that changed only the query string - and paging,
        // filtering and ordering are all carried in the query string on all four listings, so turning a
        // page or pressing a column heading swept a refusal the operator was still reading.
        this.skipLinkHref.set(this.composeSkipLinkHref());
      }
    });

  // ⚠ THE BROWSER'S OWN EXITS ARE STILL COVERED, AND THIS COMPONENT NO LONGER COVERS THEM. A reload, a tab
  // close or a navigation to another address bypasses the router completely, so a route guard cannot see
  // them and only the browser's own unload prompt can — runtime testing measured four edits across two tabs
  // of the site-settings screen destroyed by exactly that, with `window.onbeforeunload` reading `null`, so
  // the browser had not even been asked to help.

  /**
   * The name the banner renders for the signed-in account, or `undefined` when no account is signed in. ⚠
   * THE FALLBACK TO THE ACCOUNT KEY IS A PUBLISHED CONTRACT, NOT A CONVENIENCE.
   * `core/models/auth.model.ts` documents `displayName` as never null and never absent because the column
   * is `nvarchar(128) NOT NULL` defaulting to the empty string, and states outright that "a caller with
   * no display name has `""`, and the shell renders `username` in its place".
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

    // The documented substitution. The account key is `NOT NULL` and unique, so this is a value rather than
    // a second fallback — but it is trimmed and length-tested on the same terms, because a boundary that
    // trusted one field and not the other would reintroduce the blank-name defect one level down.
    const username: string = user.username.trim();

    return username.length > 0 ? username : undefined;
  });

  /** Whether a sign-out is in flight. */
  protected readonly signingOut: Signal<boolean> = this.authStore.isSigningOut;

  protected readonly accountProfileLink: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    return user === null ? undefined : `/users/${String(user.userId)}/profile`;
  });

  /** The address of the signed-in account's own change-password screen, or `undefined`. */
  protected readonly accountPasswordLink: Signal<string | undefined> = computed(() => {
    const user = this.authStore.currentUser();

    return user === null ? undefined : `/users/${String(user.userId)}/password`;
  });

  /** Whether the signed-in account is a host (super user), as the SERVER reported it. */
  protected readonly hostAccount: Signal<boolean> = this.authStore.isSuperUser;

  /**
   * Whether the server reported the signed-in account as an administrator of the current tenant. ⚠ THE
   * SERVER'S OWN VERDICT, NEVER A ROLE NAME. The administrator role is renameable, so a name test would
   * admit a colliding role and refuse a renamed one; this is the projection of
   * `CurrentUserDto.IsPortalAdministrator`.
   */
  protected readonly administersTenant: Signal<boolean> = this.authStore.holdsPortalAdministration;

  /**
   * Whether the caller holds the `EDIT` permission key anywhere in the resolved tenant. The rail's third
   * required authority fact, and the only ADVISORY one - the two above are the server's own verdicts,
   * while this is derived from `CurrentUserDto.Permissions`, which the API publishes expressly so a
   * console can hide affordances the caller cannot exercise and which unlocks nothing.
   */
  protected readonly editsContent: Signal<boolean> = computed(() =>
    this.authStore.permissions().includes(CONTENT_EDIT_PERMISSION_KEY),
  );

  /** The identifier the main region carries, and the target the skip link names. */
  readonly mainRegionId: string = MAIN_REGION_ID;

  /**
   * The skip link's `href` — the path currently showing, plus the main region's fragment. ⚠
   * PATH-QUALIFIED, NOT A BARE FRAGMENT, AND THAT REMOVES A LATENT HAZARD RATHER THAN TIDYING ONE. A bare
   * `#main-content` resolves against the root base href this document declares, so from any address below
   * the root it named a DIFFERENT PATH and following it would have reloaded the application and discarded
   * the in-memory session.
   */
  readonly skipLinkTarget: Signal<string> = this.skipLinkHref.asReadonly();

  private readonly hostElement: ElementRef<HTMLElement> = inject(ElementRef);

  /**
   * Ends the session the operator asked to end, then sends them to the sign-in screen. ⚠ SUBSCRIBED
   * EXACTLY ONCE, AND THAT IS WHAT ISSUES THE REQUEST. The command is cold by design —
   * `session-lifecycle.service.ts` documents its return as "COLD: it must be subscribed for the request
   * to be issued, exactly once" — so a command nobody subscribes to revokes nothing, and a command
   * subscribed twice revokes twice.
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
   * Builds the skip link's `href` from the address currently showing. Any fragment already on the address
   * is dropped rather than appended to, because two fragments in one address name nothing.
   *
   * @returns The current path as a browser may be handed it — base href applied, query string included —
   * ending in the main region's fragment.
   */
  private composeSkipLinkHref(): string {
    const [pathAndQuery] = this.router.url.split('#');

    return `${this.location.prepareExternalUrl(pathAndQuery)}${SKIP_LINK_FRAGMENT}`;
  }

  protected focusMainRegion(event: Event): void {
    event.preventDefault();

    const mainRegion = this.hostElement.nativeElement.querySelector<HTMLElement>(
      `#${MAIN_REGION_ID}`,
    );

    // `tabindex="-1"` on the region is what makes this call effective; without it a
    // `<main>` is not focusable and focus would stay on the link.
    mainRegion?.focus();
  }

  /** Sends the operator to the sign-in screen. */
  private goToSignIn(): void {
    void this.router.navigate([SIGN_IN_ROUTE]).catch(() => false);
  }
}
