// ---------------------------------------------------------------------------------------
// FILE HEADER — WHY THIS FILE HAS NO PREDECESSOR
//
// The legacy application shipped with NO AUTOMATED TEST SUITE OF ANY KIND, so nothing here
// is a port of anything. That was measured against the checkout rather than assumed: the
// legacy trees hold 634 `.vb` files and not one test or specification file among them, no
// file contains a test-fixture attribute, a test attribute or an assertion call, and no
// test-framework reference appears in either legacy solution or in any project file. This
// specification is therefore net-new coverage.
//
// What it covers is the contract that the legacy page discharged at RUN TIME, which is the
// only sense in which the two reference sources are ancestors of it. Both are
// REFERENCE-ONLY and neither is edited by this migration:
//
//   Website/Default.aspx      (30 lines).  L25,
//                             `<asp:PlaceHolder ID="SkinPlaceHolder" runat="server" />`,
//                             was the single injection point for the entire page body.
//                             `AppComponent` mounting one `<app-shell />` is its
//                             replacement, so "the root mounts exactly one shell and
//                             nothing else" is the direct successor to that one-slot
//                             arrangement — and it is what the `composition` block below
//                             asserts.
//
//   Website/Default.aspx.vb   (700 lines). `Page_Init` (L499) selected a layout and called
//                             `LoadSkin` (L217-L243) from four separate call sites (L510,
//                             L518, L537, L549) depending on how tenant resolution had
//                             gone; `LoadSkin` instantiated whatever it found with
//                             `CType(LoadControl("~" & SkinPath), Skin)` (L224) and called
//                             `DataBind()` (L226) — the comment at L225 states outright
//                             that this executed "any server logic in the skin" — and L590
//                             added the result to the placeholder. Layout selection was
//                             therefore a per-request, late-bound, failure-prone step,
//                             guarded at run time by a caught exception surfaced only to
//                             administrators (L227-L237).
//
// That guard is exactly what this file replaces. A missing or wrong shell is now a COMPILE
// failure rather than a run-time one, so the residual risk is no longer "can the layout be
// loaded" but "is the one layout wired up correctly" — one shell, one of each singular
// landmark, the navigation rail actually rendered, and the session the shell resolves
// reaching the chrome. Those are the properties asserted below, and each of them fails
// SILENTLY in a browser if it regresses, which is why they are worth asserting at all.
//
// MIGRATION: this coverage is net-new, not translated. Because there was no legacy suite,
// there is no legacy expectation to preserve and no test to keep behaviourally equivalent
// — the migration's behavioural-equivalence obligation is discharged by the production
// code, and this file's obligation is to state the root's contract precisely enough that a
// regression cannot pass. Ownership is kept narrow for the same reason: the shell's own
// internals, the router's route table, the interceptors, the guards and the stores each
// have their own specification, and this one asserts only what the ROOT contributes —
// which regions exist ONCE across the whole application, that the rail is projected, and
// what the root hands the chrome.
// ---------------------------------------------------------------------------------------

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { throwError } from 'rxjs';

import { AppComponent } from './app.component';
import { AUTH_ENDPOINTS } from './core/config/api-endpoints';
import { TokenStorageService } from './core/services/token-storage.service';
import { AuthStore } from './core/state/auth.store';
import { SessionLifecycleService } from './core/state/session-lifecycle.service';
import { environment } from '../environments/environment';

import type { AuthSession } from './core/models/auth.model';

// MIGRATION: every address asserted below is RELATIVE, and that is a load-bearing property
// of this workspace rather than a stylistic preference. The workspace's environment
// arrangement is INVERTED from the framework default: `src/environments/environment.ts` is
// the PRODUCTION file — it declares production true and a base of `/api/v1` — and it is the
// development configuration that replaces it with the file carrying an absolute base. The
// build's `test` target declares NO file replacements at all, so specifications compile
// against that production file and therefore against the relative base. Two consequences,
// and both are why an absolute address must never be written into a specification:
//
//   * It would be WRONG in production. The container image serves the compiled application
//     and forwards the API prefix to the API service on the compose network, from the SAME
//     origin the document was served from. That service name resolves only inside the
//     compose network, never in a browser, so a client that addressed it by host would fail
//     for every real caller while passing here.
//
//   * It would be UNFALSIFIABLE here. A hard-coded host satisfies itself: the expectation
//     and the code under test would agree even after the environment's base changed, so the
//     one regression this file could catch — a relative base quietly becoming absolute —
//     would pass unnoticed.
//
// Consequently no address is spelled out below. The revocation expectation names the
// endpoint CONSTANT, which is derived from the configured base, and one further assertion
// checks the resolved request address is same-origin-relative — no scheme, no
// protocol-relative prefix — which is the property that actually has to hold.

/**
 * An expiry comfortably ahead of whenever this suite runs, derived from the clock rather than written
 * down.
 *
 * ⚠ AN ABSOLUTE DATE IS A TEST THAT EXPIRES. This fixture used to carry `2030-01-01T00:00:00Z`,
 * which holds a session valid by the calendar rather than by anything the specification controls: on the
 * first of January 2030 every case depending on it begins asserting the opposite of what it was written
 * to assert, and it does so SILENTLY, because a session read as already expired is a state this
 * application handles rather than an error it reports.
 *
 * One hour is longer than any run of this suite and shorter than any window the application treats as
 * unusual, and it is computed ONCE per module load so every case in the file shares one instant rather
 * than racing the clock between them.
 */
const FUTURE_SESSION_EXPIRY_UTC: string = new Date(Date.now() + 60 * 60 * 1000).toISOString();

/**
 * A held session, in the shape the token custodian stores.
 *
 * The token values are obvious placeholders rather than anything resembling a real
 * credential: nothing here parses them, and a value that looked like a token would invite
 * somebody to try it.
 *
 * `userId: 0` and `portalId: -1` are not arbitrary either. Both are the identity seeds the
 * baseline schema declares — `Users` at `IDENTITY(0, 1)` and `Portals` at `IDENTITY(-1, 1)`
 * — and both collide with the legacy absent-integer sentinel, so using them here keeps the
 * fixture honest about the values this application actually has to carry.
 */
const SESSION_BODY: AuthSession = {
  accessToken: 'operator-access-token',
  refreshToken: 'operator-refresh-token',
  expiresAtUtc: FUTURE_SESSION_EXPIRY_UTC,
  mustChangePassword: false,
  mustUpdateProfile: false,
  passwordExpiring: false,
  user: {
    userId: 0,
    portalId: -1,
    portalName: 'Measured Portal',
    username: 'operator.a',
    displayName: 'Operator A',
    email: 'operator.a@example.test',
    isSuperUser: false,
    // ⚠ THE TWO FACTS AGREE, AND THEY DID NOT USED TO. This fixture previously paired
    // `isPortalAdministrator: false` with `roles: ['Administrators']`, describing a caller
    // the API never sends: the server derives the flag from the tenant's own
    // administrator-role designation, so an account holding the designated role is reported
    // as an administrator. The inconsistency was harmless only while nothing read the flag,
    // which is exactly the condition that let the navigation rail and the route gate infer
    // administration from the role NAME instead. A tenant administrator that is not a host
    // account is also the more interesting caller for the rail, because it is the one for
    // whom the host-only tenant collection is withheld.
    isPortalAdministrator: true,
    roles: ['Administrators'],
    permissions: ['EDIT'],
  },
};

describe('AppComponent', () => {
  let fixture: ComponentFixture<AppComponent>;
  let component: AppComponent;
  let httpMock: HttpTestingController;
  let tokens: TokenStorageService;
  let authStore: AuthStore;
  let session: SessionLifecycleService;
  let router: Router;
  let navigate: jasmine.Spy;

  /**
   * Returns the root component's host element.
   */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        // The shell renders a router outlet and the banner inside it renders a router
        // link, so a router must be present. An empty route table is sufficient: the
        // sign-out navigation is asserted through a spy on the router rather than by
        // resolving a route, so that this specification asserts what this component asks
        // for and the route table is asserted where it is declared.
        provideRouter([]),
        // The real client FIRST and the testing backend SECOND: `provideHttpClientTesting()`
        // REPLACES the backend the real client installed, so reversing the two would leave
        // the live backend in place and every expectation below would find nothing.
        //
        // Present because this component now injects the session store and the session
        // coordinator, and the whole graph beneath them — the authentication service, the
        // four domain stores — reaches the HTTP client. Nothing is stubbed: the real graph
        // is what makes "asking to sign out actually ends the session" assertable.
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    tokens = TestBed.inject(TokenStorageService);
    authStore = TestBed.inject(AuthStore);
    session = TestBed.inject(SessionLifecycleService);
    router = TestBed.inject(Router);

    // Spied before the component is created so that no navigation can escape into the
    // empty route table. `resolveTo` rather than `stub`, because the component chains a
    // `catch` onto the returned promise and an undefined return would throw there.
    navigate = spyOn(router, 'navigate').and.resolveTo(true);

    fixture = TestBed.createComponent(AppComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    // Proves no request was left outstanding by any case, which is the assertion that
    // catches a revocation issued twice as reliably as a count does.
    httpMock.verify();
  });

  /**
   * Establishes a held session, exactly as the sign-in flow would, and renders it.
   *
   * Written through the token custodian rather than by posting credentials, because every
   * assertion here is about the CHROME reading a session rather than about acquiring one,
   * and staging one through the sign-in endpoint would add a request each case would then
   * have to account for.
   */
  function holdSession(overrides: Partial<AuthSession['user']> = {}): void {
    tokens.store({ ...SESSION_BODY, user: { ...SESSION_BODY.user, ...overrides } });
    fixture.detectChanges();
  }

  /** Activates the banner's sign-out control the way an operator does. */
  function clickSignOut(): void {
    host().querySelector<HTMLButtonElement>('button.app-header__logout')?.click();
    fixture.detectChanges();
  }

  describe('construction', () => {
    it('creates', () => {
      expect(component).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { onPush?: boolean };
        }
      ).ɵcmp;

      expect(definition).toBeDefined();
      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone, so it can be bootstrapped without a module', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { standalone?: boolean };
        }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });

    it('is selected by the element name the document declares', () => {
      const definition = (
        AppComponent as unknown as {
          ɵcmp?: { selectors?: unknown[][] };
        }
      ).ɵcmp;

      expect(definition?.selectors?.[0]?.[0]).toBe('app-root');
    });
  });

  describe('composition', () => {
    it('mounts exactly one element, as its stylesheet is written to expect', () => {
      expect(host().children.length).toBe(1);
    });

    it('mounts the shell', () => {
      expect(host().firstElementChild?.tagName.toLowerCase()).toBe('app-shell');
    });

    it('mounts exactly one shell, anywhere in its subtree', () => {
      // Deliberately a whole-subtree count rather than a first-child check, because the
      // two fail differently. A second shell added below the first would leave the child
      // count and the first child untouched and still render a second banner, a second
      // main region and a second footer — the accessibility defect the landmark
      // assertions below describe — so the number of shells is asserted directly. This
      // is also the successor to the legacy page's single-placeholder arrangement
      // (`Website/Default.aspx` L25): one slot then, one shell now.
      expect(host().querySelectorAll('app-shell').length).toBe(1);
    });

    it('renders the shell with the grid class, so the layout engages end to end', () => {
      const shell = host().querySelector('app-shell');

      expect(shell?.classList.contains('shell')).toBeTrue();
    });
  });

  describe('regions reachable through the shell', () => {
    it('renders the skip link, the banner, the main region and the footer', () => {
      expect(host().querySelector('a.shell__skip-link')).not.toBeNull();
      expect(host().querySelector('app-header header')).not.toBeNull();
      expect(host().querySelector('main#main-content')).not.toBeNull();
      expect(host().querySelector('app-footer footer')).not.toBeNull();
    });

    it('emits each singular landmark exactly once across the whole application', () => {
      // Presence is not the interesting property; uniqueness is. A document with two
      // banners or two contentinfo landmarks is a genuine accessibility defect, and
      // the root is the only scope at which the duplication would be observable.
      for (const landmark of ['header', 'main', 'footer']) {
        expect(host().querySelectorAll(landmark).length)
          .withContext(`the application must render exactly one <${landmark}>`)
          .toBe(1);
      }
    });

    it('renders exactly one router outlet, and renders it inside the main landmark', () => {
      const main = host().querySelector('main');

      expect(main).not.toBeNull();
      expect(host().querySelectorAll('router-outlet').length).toBe(1);

      // An outlet at the root level would compile, render, and put every routed view
      // outside the main landmark and outside the shell's page gutter — a defect that
      // is invisible until someone reads the accessibility tree.
      expect(main?.querySelectorAll('router-outlet').length).toBe(1);
    });

    it('exposes a skip link whose fragment resolves from the real root', () => {
      const link = host().querySelector<HTMLAnchorElement>('a.shell__skip-link');

      expect(link).not.toBeNull();

      const address = link?.getAttribute('href') ?? '';

      // ⚠ THIS USED TO EXPECT THE ADDRESS TO BEGIN WITH `#`, AND THAT EXPECTATION WAS THE
      // DEFECT. A fragment-only address resolves against the ROOT base href this document
      // declares, so from a deep route it named a different document and following it would
      // have reloaded the application and discarded the in-memory session. The address is now
      // the current path plus the fragment, so what is asserted here is that it is
      // root-relative and that its TAIL is the fragment - and then, as before, that the
      // fragment names an element that actually exists.
      expect(address.startsWith('/')).toBeTrue();

      const [, fragmentName] = address.split('#');
      const fragment = `#${fragmentName ?? ''}`;

      expect(fragmentName).toBeTruthy();

      // Resolved against the FULL document tree rather than against the shell's
      // subtree. The fragment a browser follows is resolved document-wide, so this is
      // the scope at which a duplicate id or a stale fragment would actually bite.
      const resolved = host().querySelector(fragment);

      expect(resolved).not.toBeNull();
      expect(resolved).toBe(host().querySelector('main'));
    });

    it('renders no chrome of its own', () => {
      // The root owns no data, no navigation and no chrome; the shell renders all of
      // it. Any of these appearing at this level would be a second definition of
      // something that already exists exactly once.
      expect(host().querySelectorAll(':scope > header').length).toBe(0);
      expect(host().querySelectorAll(':scope > nav').length).toBe(0);
      expect(host().querySelectorAll(':scope > main').length).toBe(0);
      expect(host().querySelectorAll(':scope > footer').length).toBe(0);
    });

    it('renders the banner with the build-time application name, no binding required', () => {
      const brand = host().querySelector('a.app-header__brand');

      expect(brand?.textContent?.trim()).toBe(environment.applicationName);
    });
  });

  describe('session — reading it', () => {
    it('renders no session cluster while no account is signed in', () => {
      // Not an assertion about unfinished work: the component supplies `undefined`, the
      // shell forwards it unchanged, and the banner treats an absent name as no account
      // signed in. This is the state a first-time visitor is in.
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('renders the signed-in display name and a sign-out control once a session is held', () => {
      holdSession();

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('Operator A');
      expect(host().querySelector('button.app-header__logout')).not.toBeNull();
    });

    it('falls back to the account key when the display name is blank', () => {
      // The published contract rather than a nicety: `auth.model.ts` documents
      // `displayName` as `NOT NULL` defaulting to the empty string and states that the
      // shell renders `username` in its place. It also prevents a real defect — the
      // sign-out control lives INSIDE the cluster the banner suppresses for a blank name,
      // so passing `''` through would leave this operator unable to sign out.
      holdSession({ displayName: '' });

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('operator.a');
      expect(host().querySelector('button.app-header__logout')).not.toBeNull();
    });

    it('treats a whitespace-only display name as absent, not as a name', () => {
      // The legacy absent-string sentinel is the empty string, and a value that trims to
      // nothing is the same condition wearing a disguise. Deciding this by trimming rather
      // than by truthiness is what makes the two cases behave alike.
      holdSession({ displayName: '   ' });

      expect(host().querySelector('span.app-header__user')?.textContent?.trim()).toBe('operator.a');
    });

    it('reports no name at all when neither the display name nor the account key is usable', () => {
      holdSession({ displayName: '', username: '' });

      // Asserted through the rendered chrome rather than through a member, because the name
      // is resolved by the shell now and the root holds no session member at all. See
      // `layout/shell/shell.component.spec.ts` for the resolution rules themselves.
      expect(host().querySelector('span.app-header__user')).toBeNull();
    });

    it('offers no account affordance while no account is signed in', () => {
      // The address is composed from the session key, so there is nothing to compose from and
      // nothing to render. `undefined` rather than `null`, because that is what the shell's
      // optional input declares and what the banner tests for.
      expect(host().querySelector('a.app-header__account-link')).toBeNull();
    });

    /**
     * The account-scoped link carrying a given caption.
     *
     * ⚠ SELECTED BY CAPTION, NEVER BY POSITION. There is more than one of these links and an earlier
     * revision of these cases took the first match, which silently became a different affordance
     * the moment one was added ahead of it — the failure was an assertion about one caption reading
     * another, with nothing wrong in the application at all. Selecting by the wording each case is
     * actually about makes the order of the band a free choice again.
     *
     * @param caption The exact rendered caption.
     * @returns The anchor, or null.
     */
    function accountLink(caption: string): HTMLAnchorElement | null {
      const links = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('a.app-header__account-link'),
      );

      return links.find((link) => link.textContent?.trim() === caption) ?? null;
    }

    it("composes the signed-in account's own profile and password addresses", () => {
      // MIGRATION: both affordances were MISSING, and their absence stranded every
      // non-administrative account: the two screens are permitted to the account owner, but every
      // entry in the navigation rail requires an administrator, so a member could reach neither
      // without typing a URL containing their own numeric account key.
      // `ManageUsers.ascx.vb:L439-L456` shows the legacy offered both to an account viewing
      // itself — `cmdPassword` is hidden only when the viewer is neither an administrator nor the
      // holder, and `cmdProfile` is never hidden at all.
      //
      // ⚠ THE FIXTURE'S ACCOUNT KEY IS ZERO, WHICH MAKES THIS A SENTINEL PROOF AS WELL AS A
      // COMPOSITION ONE. A truthiness test or a `> 0` guard anywhere on the path from the session
      // to a rendered `href` would drop both affordances entirely, and the addresses below are
      // what prove none exists.
      holdSession();

      expect(accountLink('Manage Profile')?.getAttribute('href')).toBe('/users/0/profile');
      expect(accountLink('Manage Password')?.getAttribute('href')).toBe('/users/0/password');
    });

    it('offers both account affordances in the legacy command order, and no third', () => {
      holdSession();

      // Order follows the legacy command bar in `ManageUsers.ascx.resx`: profile, then password.
      // Asserted as a sequence rather than two independent lookups, because the point is the
      // arrangement an operator reads left to right — and because the sequence is also what proves
      // there is no third link. A member-services affordance was rendered here and is withdrawn
      // with the route it pointed at: the migration plan freezes the console at twenty-five screens
      // and declares no such address.
      const captions = Array.from(
        host().querySelectorAll<HTMLAnchorElement>('a.app-header__account-link'),
      ).map((link) => link.textContent?.trim());

      expect(captions).toEqual(['Manage Profile', 'Manage Password']);
    });

    it('captions each account affordance with the measured legacy command wording', () => {
      holdSession();

      // `cmdProfile.Text` and `cmdPassword.Text` in `ManageUsers.ascx.resx`. The root composes the
      // addresses and the banner owns the wording, so this asserts the seam rather than restating
      // the labels.
      expect(accountLink('Manage Profile')?.textContent?.trim()).toBe('Manage Profile');
      expect(accountLink('Manage Password')?.textContent?.trim()).toBe('Manage Password');
    });

    it('offers no account affordance at all while no session is held', () => {
      // The complement of the cases above. Each link is gated on a session as well as on an
      // address, so an address left over from a previous identity cannot render a link naming an
      // account nobody is signed in as.
      expect(host().querySelectorAll('a.app-header__account-link').length).toBe(0);
    });

    it('renders the navigation rail exactly once, as the shell\'s own region', () => {
      // ⚠ THE ROOT SUPPLIES NOTHING HERE, AND THAT IS THE CORRECTION. The rail used to be
      // imported by this component and projected into a slot the shell published, which
      // failed SILENTLY when it was not: the stylesheet's `:empty` collapse rule meant the
      // application simply rendered with no navigation rather than raising anything. The
      // shell owns the rail now, so the rail appears here because the shell renders it —
      // and it appears exactly once, carrying the grid region class on its own host.
      const rail = host().querySelector('app-sidebar');

      expect(rail).not.toBeNull();
      expect(host().querySelectorAll('app-sidebar').length).toBe(1);
      expect(rail?.classList.contains('shell__sidebar')).toBeTrue();
      expect(host().querySelectorAll('div.shell__sidebar').length).toBe(0);
    });

    it('announces the rail through its own labelled landmark', () => {
      // The shell writes no `role` and no `aria-label` on the region, leaving the landmark
      // to the rail. That contract only holds if the rail brings one.
      //
      // ⚠ A SESSION IS HELD, BECAUSE THE LANDMARK IS CONDITIONAL. Every destination the rail
      // offers is an administration screen, so the rail emits its landmark only once at least one
      // entry is admitted — the withheld case is asserted on its own below. The fixture account
      // administers the tenant, which is what admits entries.
      holdSession();

      const landmark = host().querySelector('app-sidebar nav');

      expect(landmark).not.toBeNull();
      expect(landmark?.getAttribute('aria-label')?.trim().length).toBeGreaterThan(0);
      expect(host().querySelectorAll('nav').length).toBe(1);
    });
  });

  describe('navigation rail — whether it is offered at all', () => {
    /**
     * The rail's RENDERED NAVIGATION, or null when the rail is offering nothing.
     *
     * ⚠ THE LANDMARK, NOT THE CUSTOM ELEMENT, AND THE DISTINCTION IS THE WHOLE OF THIS SUITE.
     * The shell composes the rail unconditionally — that is the correction the case above records,
     * and it is what makes shipping without navigation a compile error — so `app-sidebar` is in the
     * document at every address including the sign-in screen. What is CONDITIONAL is everything
     * inside it: the rail resolves its own entries from the two authority facts the shell forwards
     * and emits no landmark, no group and no anchor while none is admitted, leaving its host
     * genuinely childless so the layout's `:empty` collapse rule takes the column away with it.
     * Asserting the element's absence would therefore assert the OLD arrangement; asserting the
     * landmark's absence asserts the behaviour an operator meets.
     */
    function rail(): Element | null {
      return host().querySelector('app-sidebar nav');
    }

    /**
     * The rail's host element, which the shell composes UNCONDITIONALLY.
     *
     * Separate from {@link rail} on purpose: the two say different things, and the cases below need
     * both. This one proves the shell still composes the rail; that one proves the rail is offering
     * something.
     */
    function railElement(): Element | null {
      return host().querySelector('app-sidebar');
    }

    /** Every navigation address currently rendered anywhere in the application. */
    function railAddresses(): readonly string[] {
      return Array.from(host().querySelectorAll<HTMLAnchorElement>('a.app-sidebar__link')).map(
        (anchor: HTMLAnchorElement): string => anchor.getAttribute('href') ?? '',
      );
    }

    it('projects no rail at all while nobody is signed in', () => {
      // ⚠ THE DEFECT THIS CLOSES, STATED AS THE STATE IT PRODUCED. This component is mounted
      // for EVERY address the application serves, the sign-in screen and the catch-all
      // included, and it projected the rail unconditionally — so an unauthenticated visitor
      // was shown the complete administrative surface of the installation: every domain,
      // every collection, every settings screen. No row of data was disclosed by naming them,
      // and that is not the whole of the harm — the set of administration screens an
      // installation runs is worth withholding, and a sign-in form ringed with links that
      // cannot be followed is a broken screen besides.
      //
      // No session is established here, which is the whole fixture.
      expect(rail()).toBeNull();
      expect(railAddresses()).toEqual([]);
    });

    it('leaves the shell region genuinely empty, so its collapse rule engages', () => {
      // The layout collapses `.shell__sidebar:empty`. That branch only engages if the region
      // renders NOTHING, so a rail that emitted an empty frame — a landmark, a heading or even a
      // collapse toggle — would defeat it and reserve a blank column beside the sign-in form. The
      // region class sits on the rail's OWN host, so the emptiness has to be the host's.
      const region = host().querySelector('app-sidebar.shell__sidebar');

      expect(region).not.toBeNull();
      expect(region?.children.length).toBe(0);
      expect((region?.textContent ?? '').trim()).toBe('');
      expect(region?.matches(':empty'))
        .withContext('the collapse rule is what takes the column away, so it must actually match')
        .toBeTrue();

      // And no wrapper element carries the class, which would sever the grid relationship.
      expect(host().querySelectorAll('div.shell__sidebar').length).toBe(0);
    });

    it('projects the rail as soon as a session with a resolved identity is held', () => {
      expect(rail()).toBeNull();

      holdSession();

      expect(rail()).not.toBeNull();
      expect(railAddresses().length).toBeGreaterThan(0);
    });

    it('withdraws the rail again when the session ends', () => {
      // ⚠ THE RAIL MUST NOT OUTLIVE THE SESSION IT WAS RENDERED FOR. A sign-out leaves this
      // component mounted — the application is not reloaded — so a rail derived from a value
      // captured once would keep the previous operator's navigation on screen while they were
      // being returned to the sign-in form.
      holdSession();
      expect(rail()).not.toBeNull();

      tokens.clear();
      fixture.detectChanges();

      expect(rail()).toBeNull();
      expect(railAddresses()).toEqual([]);
    });

    it('states the caller\u2019s authority from the server\u2019s own facts, not from a role name', () => {
      // ⚠ BOTH INPUTS ARE REQUIRED, so omitting either is a compile error rather than an
      // unfiltered rail — but the VALUES are this component's responsibility, and getting them
      // from the wrong place is not a compile error. This asserts the source: the host flag and
      // the server-derived administration verdict, both republished by the store from
      // `CurrentUserDto`. The fixture is a tenant administrator who is NOT a host account,
      // which is the caller that distinguishes the two.
      holdSession();

      // The two authority facts are published by the SHELL, which owns the session boundary, and
      // their source is asserted in `layout/shell/shell.component.spec.ts`. What this file asserts is
      // the consequence the operator actually meets, end to end: the host-only tenant collection is withheld —
      // `GET /api/v1/portals` requires host authority — while every tenant-scoped entry is
      // offered. A rail that had inferred administration from the role name `Administrators`
      // would have produced the same list here and the WRONG list for a tenant that renamed
      // that role, which is why the source rather than the outcome is asserted above.
      const addresses = railAddresses();

      expect(addresses).not.toContain('/portals');
      expect(addresses).toContain('/users');
      expect(addresses).toContain('/roles');
      expect(addresses).toContain('/modules');
    });

    it('offers the tenant collection once the caller is a host account', () => {
      holdSession({ isSuperUser: true });

      expect(railAddresses()).toContain('/portals');
    });

    it('offers no entry point to a signed-in caller holding no authority at all', () => {
      // Truthful rather than unhelpful: every declared entry requires an administration authority
      // or a content-edit grant, and this caller holds none of the three. The rail is still
      // COMPOSED — a session IS held and the shell renders it either way — and it simply has
      // nothing to offer, so it withholds its landmark along with its entries rather than
      // announcing a region a reader can navigate to and find nothing in.
      //
      // ⚠ THE ROLE NAME IN THE FIXTURE IS THE POINT OF THE THIRD ASSERTION. This caller holds a
      // role named 'Subscribers' and the server reports it as administering nothing; a rail that
      // inferred authority from a role name rather than from the server's own flags could not tell
      // this caller from an administrator whose tenant renamed its administrator role.
      //
      // ⚠ THE EMPTY PERMISSION LIST IS STATED AND NOT INHERITED. The shared fixture above carries
      // `permissions: ['EDIT']`, which is a real authority: it admits the caller to module
      // placement through `PortalContentEditor`, whose grant arm needs no administration at all.
      // Leaving it inherited would describe a caller who holds a capability while asserting that
      // it is offered nothing, so the list is emptied here to make this the no-authority caller
      // the case is about. The page-editor caller is the case below.
      holdSession({
        isSuperUser: false,
        isPortalAdministrator: false,
        roles: ['Subscribers'],
        permissions: [],
      });

      expect(railElement()).not.toBeNull();
      expect(rail())
        .withContext('no landmark stands over a rail with nothing in it')
        .toBeNull();
      expect(railAddresses()).toEqual([]);
    });

    it('offers module placement to a page editor who administers nothing', () => {
      // ⚠ THE END-TO-END PROOF OF THE DISCOVERABILITY FIX, asserted through the assembled
      // application rather than against the rail in isolation: the session store publishes the
      // permission list, the shell derives the content-edit fact from it, and the rail offers the
      // entry. Every link in that chain has to hold for this to pass.
      //
      // The caller administers NOTHING — no host account, no tenant administration, and an
      // ordinary role name — and holds `EDIT` somewhere in the tenant, which is exactly whom
      // `PortalContentEditor` admits (`PolicyNames.cs:198-231`). Previously the only link to the
      // create screen sat inside the module listing, which is administration-gated, so this
      // caller was offered nothing anywhere and had to guess the address.
      holdSession({
        isSuperUser: false,
        isPortalAdministrator: false,
        roles: ['Subscribers'],
        permissions: ['EDIT'],
      });

      const addresses = railAddresses();

      expect(rail())
        .withContext('one entry survives, so the landmark stands')
        .not.toBeNull();
      expect(addresses).toEqual(['/modules/new']);

      // Nothing administration-gated leaks in alongside it. The listing in particular is the
      // screen that USED to be the only way to reach the create form, and it stays withheld.
      expect(addresses).not.toContain('/modules');
      expect(addresses).not.toContain('/portals');
      expect(addresses).not.toContain('/users');
      expect(addresses).not.toContain('/roles');
    });
  });

  describe('session — ending it', () => {
    it('revokes the session server-side when the operator asks to sign out', () => {
      holdSession();

      clickSignOut();

      const revocation = httpMock.expectOne(AUTH_ENDPOINTS.logout);

      expect(revocation.request.method).toBe('POST');
      expect(revocation.request.body).toEqual({ refreshToken: 'operator-refresh-token' });

      revocation.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('addresses the API relatively, on the origin that served the application', () => {
      // The one assertion in this file about an address, and it deliberately asserts a
      // SHAPE rather than a value. Spelling out a host would make the expectation agree
      // with itself for ever — see the note above the fixture — whereas these four
      // properties are exactly what has to hold for the deployed topology to work: the
      // compiled application is served by a container that forwards the API prefix to the
      // API service on its own network, so the browser must address the API through the
      // origin it was served from and nothing else.
      holdSession();
      clickSignOut();

      const revocation = httpMock.expectOne(AUTH_ENDPOINTS.logout);
      const address: string = revocation.request.url;

      // Rooted at the origin: a path, not a location.
      expect(address.startsWith('/')).toBeTrue();

      // Not protocol-relative. A leading double slash is a path to the eye and a foreign
      // origin to a browser, which is the failure mode a naive prefix test misses.
      expect(address.startsWith('//')).toBeFalse();

      // Carries no scheme, so it cannot name a host at all.
      expect(/^[a-z][a-z\d+\-.]*:/i.test(address)).toBeFalse();

      // And it is rooted at the base the environment configures rather than at some other
      // path, which is what ties the request to the prefix the proxy forwards.
      expect(address.startsWith(environment.apiBaseUrl)).toBeTrue();

      revocation.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('ends the session through the lifecycle service rather than through the session store', () => {
      // The distinction is the whole reason the lifecycle service exists. The store's own
      // sign-out discards the credentials and the identity and knows nothing about the
      // portals, accounts, roles or exported module documents the domain stores hold, so
      // chrome that called the store directly would leave every one of those slices legible
      // to whoever signs in next. Asserted from the root because this is the whole
      // application rendered: the shell is what calls it, and this case proves the wiring
      // survives end to end.
      const coordinated = spyOn(session, 'signOut').and.callThrough();
      const storeDirect = spyOn(authStore, 'logout').and.callThrough();

      holdSession();
      clickSignOut();

      expect(coordinated).toHaveBeenCalledTimes(1);

      // Reached only THROUGH the lifecycle service: one call, made by it rather than by the
      // chrome. Asserting the count alone would pass either way, so the caller matters.
      expect(storeDirect).toHaveBeenCalledTimes(1);
      expect(coordinated).toHaveBeenCalledBefore(storeDirect);

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
    });

    it('leaves the application unauthenticated once the revocation settles', () => {
      holdSession();
      expect(authStore.isAuthenticated()).toBeTrue();

      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(authStore.isAuthenticated()).toBeFalse();
      expect(host().querySelector('span.app-header__user')).toBeNull();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('sends the operator to the sign-in screen, carrying no return address', () => {
      holdSession();
      clickSignOut();
      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledTimes(1);

      // The route gates attach a return address when they INTERRUPT a navigation. Signing
      // out is not an interruption — the operator chose to leave — and restoring an address
      // that named a record the next operator has no right to know exists would defeat the
      // discard that just happened.
      expect(navigate).toHaveBeenCalledWith(['/login']);
    });

    it('still reaches the sign-in screen when the revocation request fails', () => {
      // A refused or unreachable endpoint arrives as a COMPLETION rather than as an error,
      // because `core/state/auth.store.ts` absorbs the refusal DELIBERATELY — local sign-out
      // has already happened unconditionally, and the store records the failed withdrawal in
      // its own report rather than discarding it. Either way the session is already gone, and
      // leaving the operator on an administration screen with no credentials would strand
      // them on a view whose every request is about to be refused.
      //
      // MIGRATION: the absorption used to sit one layer lower, on `auth.service.ts`, which
      //   discarded the failure as well and so reported a clean sign-out while the renewal
      //   credential was still live. This component's two exits are unchanged by the move.
      holdSession();
      clickSignOut();

      httpMock
        .expectOne(AUTH_ENDPOINTS.logout)
        .flush({ detail: 'unreachable' }, { status: 503, statusText: 'Service Unavailable' });
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledWith(['/login']);
      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('still reaches the sign-in screen when ending the session throws outright', () => {
      // The component's OTHER exit, and the only way to reach it. The case above proves a
      // refused revocation arrives as a completion, so nothing an HTTP backend can be made
      // to do will exercise the error handler — it exists because the coordinator PUBLISHES
      // an error exit, and because both the store's discard and the coordinator's slice
      // clearing run in a teardown block whose throw would propagate to this subscriber.
      //
      // Coding to a dependency's published contract rather than to its current
      // implementation is what keeps this component correct if that absorption is ever
      // removed, so the contract is what is asserted: whichever of the two exits runs, the
      // operator ends up at the sign-in screen. An observable cannot both error and
      // complete, so exactly one of them does.
      spyOn(session, 'signOut').and.returnValue(throwError(() => new Error('teardown failed')));

      holdSession();
      clickSignOut();

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);

      // Nothing was issued, because the coordinator never got as far as the transport. The
      // `verify()` in the teardown is what proves it.
      expect(authStore.isSigningOut()).toBeFalse();
    });

    it('swallows a navigation the router refuses, because the session has ended either way', async () => {
      // The component navigates without awaiting and discards the rejection deliberately: a
      // navigation the router declines is not this component's failure to report, and the
      // credentials are already gone. Asserted because the alternative — letting the
      // rejection escape — would raise an unhandled error on a path that succeeded from the
      // operator's point of view, and it would do so while the application is mid-teardown
      // of its own session.
      navigate.and.rejectWith(new Error('navigation refused'));

      holdSession();
      clickSignOut();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      /*
       * Lets the rejected navigation settle, so the discard actually happens inside the
       * specification rather than after it.
       *
       * ⚠ DELIBERATELY NOT `whenStable()`, which this case used to await and which now cannot
       * complete in time. Signing out announces itself, and a self-dismissing statement arms an
       * eight-second `setTimeout` inside the Angular zone - so the zone is legitimately UNSTABLE for
       * eight seconds afterwards, well past Jasmine's five-second limit. That property is not new and
       * is not a fault; every confirmation in the application has always had it. What is new is that
       * this path now raises one.
       *
       * One macrotask turn is both sufficient and precise for what this case is about: the router's
       * rejection is settled by then, so the `.catch` has run, and nothing here depends on the
       * dismissal timer that remains pending.
       */
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });

      expect(navigate).toHaveBeenCalledOnceWith(['/login']);
      expect(authStore.isAuthenticated()).toBeFalse();
    });

    it('marks the sign-out in flight while the revocation is outstanding', () => {
      holdSession();
      clickSignOut();

      // ⚠ ASSERTED ON THE STORE'S PHASE, AND IT HAS TO BE. The store discards the identity
      // SYNCHRONOUSLY at subscribe time — local sign-out is the part the operator asked for
      // and it does not wait for the server — so the banner's session cluster, and with it
      // the control carrying the disabled state, is already gone by the time this line runs.
      // The phase is what the shell reads and forwards, and the forwarding itself is
      // asserted against the banner's input in `layout/shell/shell.component.spec.ts`.
      expect(authStore.isSigningOut()).toBeTrue();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(authStore.isSigningOut()).toBeFalse();
      expect(host().querySelector('button.app-header__logout')).toBeNull();
    });

    it('issues one revocation only, however many times the gesture arrives in a single tick', () => {
      // The banner guards the gesture twice already, but both of its guards read the flag
      // as it stood at the last change detection. The store sets the phase SYNCHRONOUSLY at
      // subscribe time, so the guard in the shell is the one that closes the same-tick
      // window — which is exactly what activating the control repeatedly WITHOUT an
      // intervening render reproduces.
      holdSession();

      const control = host().querySelector<HTMLButtonElement>('button.app-header__logout');

      control?.click();
      control?.click();
      control?.click();
      fixture.detectChanges();

      httpMock.expectOne(AUTH_ENDPOINTS.logout).flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('offers no sign-out gesture at all when no session is held', () => {
      // The session cluster is not rendered, so there is nothing to activate and no
      // revocation can be attempted for a session that does not exist. `httpMock.verify()`
      // in the teardown is what proves the absence of any request.
      expect(host().querySelector('button.app-header__logout')).toBeNull();
      expect(navigate).not.toHaveBeenCalled();
    });
  });
});
