import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';

import type { Signal } from '@angular/core';
import type { PermissionPolicy } from '../../core/guards/permission.guard';

/**
 * The authority a rail entry requires, expressed as an authorisation POLICY name. ⚠ NARROWED FROM THE
 * GATE'S OWN EIGHT-NAME UNION, AND DERIVED FROM IT RATHER THAN RESTATED. `Extract` keeps this type bound
 * to `PermissionPolicy`, so a rename or removal in the gate's vocabulary is a compile error here instead
 * of a silent divergence — and the two files provably speak the same vocabulary, which is the whole
 * reason the type is imported rather than re-declared as two string literals. ⚠ ONLY THE TWO MEMBERSHIP
 * POLICIES, AND THE OMISSION IS THE POINT. The other six are decided against ONE RECORD — a specific
 * module, a specific page, a specific account — and a rail rendered once for the whole application holds
 * no record to decide them against.
 */
export type SidebarPolicy = Extract<
  PermissionPolicy,
  'PortalAdministrator' | 'HostAdministrator' | 'PortalContentEditor'
>;

/**
 * One addressable destination in the navigation rail. Every member is `readonly`, and the whole model
 * below is a compile-time constant rather than anything resolved at run time.
 */
export interface SidebarNavItem {
  /**
   * The absolute in-application address, exactly as the route table declares it. Absolute — leading slash
   * — deliberately.
   */
  readonly path: string;

  /** The visible text, in the legacy application's own wording. A plain string, rendered as text. */
  readonly label: string;

  /**
   * The authority the destination's own PRIMARY API READ requires. ⚠ READ FROM THE CONTROLLER, NOT FROM
   * THE ROUTE TABLE, and the distinction matters exactly once. A rail entry answers "can this operator
   * use this screen", and the only authority on that is the endpoint the screen reads on arrival.
   */
  readonly policy: SidebarPolicy;
}

/** A titled cluster of related destinations. MIGRATION: the grouping itself is net-new. */
export interface SidebarNavGroup {
  /**
   * A stable, unique key for this group. Exists so the template can iterate with a `track` expression
   * that is genuinely invariant.
   */
  readonly id: string;

  /** The group heading, in the legacy application's own vocabulary. */
  readonly label: string;

  /** The destinations in this group, in the order they are presented. */
  readonly items: readonly SidebarNavItem[];
}

/**
 * The identifier of the collapsible region. Held as a named constant, not written twice, because it is
 * referenced from two places in the paired template — the region's own `id` and the toggle's
 * `aria-controls` — and a value that drifted between them would produce a control that claims to operate
 * an element that does not exist.
 */
const NAVIGATION_REGION_ID = 'sidebar-navigation';

/**
 * The navigation model: four domain groups, eight destinations. ## Why this lives here and not in a
 * companion file The model is declared inline, in the same file as the component that renders it, because
 * the folder is specified as exactly four files — component, template, stylesheet, specification — and
 * nothing else.
 */
const NAVIGATION = [
  {
    id: 'portal',
    label: 'Portal',
    // `GET /api/v1/portals` requires host authority (`PortalsController.cs:L241`), because the tenant
    // COLLECTION addresses no single tenant. A tenant administrator is therefore not offered this entry:
    // the API would refuse the listing, and offering a link to a refusal is the defect, not the courtesy.
    items: [{ path: '/portals', label: 'Portals', policy: 'HostAdministrator' }],
  },
  {
    id: 'module',
    label: 'Module',
    items: [
      // MIGRATION: this label is genuinely net-new, and the proof is an absence.
      { path: '/modules', label: 'Modules', policy: 'PortalAdministrator' },
      { path: '/modules/new', label: 'Add Module', policy: 'PortalContentEditor' },
      { path: '/modules/import', label: 'Import Module', policy: 'PortalAdministrator' },
    ],
  },
  {
    id: 'user',
    label: 'User',
    // `UsersController.cs:L350` for the listing and `:L827` for the tenant's membership policy;
    // `ProfileDefinitionsController.cs:L165` gates its whole class. All three are tenant administration.
    items: [
      { path: '/users', label: 'User Accounts', policy: 'PortalAdministrator' },
      { path: '/settings/membership', label: 'User Settings', policy: 'PortalAdministrator' },
      {
        path: '/settings/profile-definitions',
        label: 'Manage Profile Properties',
        policy: 'PortalAdministrator',
      },
    ],
  },
  {
    id: 'role',
    label: 'Role',
    // `RolesController.cs:L284` gates the whole class on tenant administration, so both
    // entries name it.
    items: [
      { path: '/roles', label: 'Security Roles', policy: 'PortalAdministrator' },
      { path: '/role-groups/new', label: 'Add New Role Group', policy: 'PortalAdministrator' },
    ],
  },
] as const satisfies readonly SidebarNavGroup[];

/**
 * Whether a caller described by the three facts holds the authority an entry requires. ⚠ AN EXHAUSTIVE
 * SWITCH, NOT A LOOKUP OBJECT OR A BOOLEAN EXPRESSION, so widening {@link SidebarPolicy} fails to compile
 * here until the new policy's answer is stated.
 *
 * @param policy The authority the entry requires.
 * @param host Whether the caller is a host account.
 * @param administersTenant Whether the SERVER reported the caller as an administrator of the tenant it is
 * signed in to.
 * @returns True when the entry may be offered.
 */
function holds(
  policy: SidebarPolicy,
  host: boolean,
  administersTenant: boolean,
  editsContent: boolean,
): boolean {
  switch (policy) {
    case 'HostAdministrator':
      return host;
    case 'PortalAdministrator':
      return host || administersTenant;

    // THREE ARMS, IN THE SAME ORDER THE SERVER EVALUATES THEM.
    // `PermissionService.HasAnyTabPermissionInPortalAsync` asks its administration question FIRST and only
    // then reads grants, and both of the first two arms here are required for that reason rather than for
    // generosity: the advisory permission list this component's third fact derives from is built by
    // `PermissionEvaluator.ListEffectivePortalPermissionKeysAsync` from GRANT ROWS ALONE and has NO
    // administrator arm, so a tenant administrator whose pages carry no explicit grants holds the policy
    // while carrying no `EDIT` key.
    case 'PortalContentEditor':
      return host || administersTenant || editsContent;
  }
}

@Component({
  selector: 'app-sidebar',
  standalone: true,
  // ⚠ `RouterLinkActive` IS DELIBERATELY ABSENT. Current-destination state is derived here instead — see
  // {@link SidebarComponent.activePath} — because the directive can only answer "does this link match" per
  // link, and the question this rail has to answer is "which ONE of these links is current", which no
  // per-link matcher can decide.
  imports: [RouterLink],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // ⚠ #33 — ESCAPE COLLAPSES THE DISCLOSURE. On a handset the expanded rail displaces roughly 470px of the
  // screen a reader was looking at, and the only way back was to find the toggle again and press it a second
  // time; Escape did nothing, even with focus inside the rail. The listener is bound on the HOST rather than
  // on the document, so it is scoped to this component's own subtree - which is exactly the scope the key
  // should have here, because Escape belongs to whatever the reader is currently inside, and a dialog
  // elsewhere on the screen must keep it.
  host: {
    '(keydown.escape)': 'onEscape($event)',
  },
})
export class SidebarComponent {
  /**
   * Whether the caller is a host account. ⚠ SUPPLIED AS AN INPUT RATHER THAN READ FROM THE SESSION STORE,
   * AND THE CHOICE IS DELIBERATE IN BOTH DIRECTIONS. Injecting the store here would have been fewer lines
   * and would have cost this component the one property it is built around — it injects nothing, so it
   * cannot fetch, cannot decide who is signed in and cannot hold session state, and its specification
   * proves all three by constructing it under a provider set that has no HTTP client in it.
   */
  readonly hostAccount = input.required<boolean>();

  /**
   * Whether the SERVER reported the caller as an administrator of the tenant it is signed in to. ⚠ THE
   * SERVER'S VERDICT, NOT A ROLE-NAME MATCH. The API publishes this as
   * `CurrentUserDto.IsPortalAdministrator`, resolved from the tenant's own administrator-role designation
   * and the caller's live assignments — because that role is designated by identifier, may be renamed,
   * and its name may collide with an unrelated role in another tenant.
   */
  readonly administersTenant = input.required<boolean>();

  /**
   * Whether the caller holds the `EDIT` permission key anywhere in the resolved tenant. ⚠ ADVISORY, AND
   * THE ONLY FACT IN THIS COMPONENT THAT IS. The other two are the server's own verdicts.
   */
  readonly editsContent = input.required<boolean>();

  /**
   * Whether the rail is collapsed, as writable state. Private, so the only ways to change it are the
   * methods below.
   */
  private readonly collapsedSignal = signal<boolean | null>(null);

  /** Whether the shell has placed the rail BESIDE the content rather than above it. */
  private readonly sideBySideSignal = signal(true);

  /**
   * Whether the rail is presented beside the content, as a readable signal. Escape reads it to tell a
   * handset disclosure - which a reader is inside, and can be escaped from - apart from a desktop rail,
   * which is simply part of the page.
   */
  public readonly sideBySide: Signal<boolean> = this.sideBySideSignal.asReadonly();

  /** The disclosure control, so focus can be returned to it when Escape collapses the rail. */
  @ViewChild('navToggle') private toggleControl?: ElementRef<HTMLButtonElement>;

  /**
   * Whether the rail is currently collapsed. ⚠ DERIVED FROM THE LAYOUT UNTIL SOMEBODY SAYS OTHERWISE,
   * which is what lets one control serve two layouts honestly.
   */
  public readonly collapsed: Signal<boolean> = computed(() => {
    const chosen = this.collapsedSignal();

    return chosen ?? this.sideBySideSignal() === false;
  });

  /** The router, read for the address currently showing. */
  private readonly router = inject(Router);

  /** This component's own element, read for the arrangement the stylesheet publishes on it. */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Seeds and maintains {@link sideBySideSignal} from the stylesheet's published arrangement. A resize
   * listener rather than a media-query listener, for the reason recorded on {@link sideBySideSignal}: the
   * threshold belongs to `_mixins.scss` and is read back as a RESOLVED value, so this class never names a
   * width.
   */
  public constructor() {
    this.readArrangement();

    const onResize = (): void => {
      this.readArrangement();
    };

    globalThis.addEventListener('resize', onResize, { passive: true });

    inject(DestroyRef).onDestroy(() => {
      globalThis.removeEventListener('resize', onResize);
    });
  }

  /** Reads `--app-sidebar-side-by-side` off this component's own element and publishes it. */
  private readArrangement(): void {
    const published: string = globalThis
      .getComputedStyle(this.hostElement.nativeElement)
      .getPropertyValue('--app-sidebar-side-by-side')
      .trim();

    this.sideBySideSignal.set(published !== '0');
  }

  /**
   * The address currently showing, as a signal. ⚠ SEEDED FROM `router.url` AND UPDATED ON COMPLETED
   * NAVIGATIONS ONLY. The seed matters because a rail rendered after the first navigation has already
   * missed the event that announced it, and an unseeded signal would leave no entry current until the
   * operator navigated again.
   */
  private readonly currentUrl: Signal<string> = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event: NavigationEnd) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /**
   * The one entry that is current for the address showing, or `null` when none is. ⚠ LONGEST MATCH,
   * MEASURED IN PATH SEGMENTS, AND EXACTLY ONE WINNER. Two properties have to hold at once and no
   * per-link matcher delivers both: * A DESCENDANT KEEPS ITS PARENT CURRENT. `/portals/3/settings` is
   * reached from the Portals entry, so that entry stays marked while the operator is on it.
   */
  public readonly activePath: Signal<string | null> = computed(() => {
    const showing: readonly string[] = pathSegments(this.currentUrl());
    let winner: string | null = null;
    let winningLength = 0;

    for (const group of this.visibleNavigation()) {
      for (const item of group.items) {
        const candidate: readonly string[] = pathSegments(item.path);

        if (candidate.length <= winningLength || isDescendantOf(showing, candidate) === false) {
          continue;
        }

        winner = item.path;
        winningLength = candidate.length;
      }
    }

    return winner;
  });

  /**
   * The four navigation groups as DECLARED, before any authority is considered. ⚠ NOT WHAT THE TEMPLATE
   * RENDERS. The template reads {@link SidebarComponent.visibleNavigation}.
   */
  public readonly navigation: readonly SidebarNavGroup[] = NAVIGATION;

  /**
   * The groups and entries this caller may actually use. ⚠ ENTRIES ARE FILTERED FIRST AND EMPTIED GROUPS
   * ARE THEN DROPPED, in that order.
   */
  public readonly visibleNavigation: Signal<readonly SidebarNavGroup[]> = computed(() => {
    const host: boolean = this.hostAccount();
    const administersTenant: boolean = this.administersTenant();
    const editsContent: boolean = this.editsContent();

    return NAVIGATION.map((group) => ({
      ...group,
      items: group.items.filter((item) =>
        holds(item.policy, host, administersTenant, editsContent),
      ),
    })).filter((group) => group.items.length > 0);
  });

  /**
   * The identifier carried by the collapsible region. Read by the template twice — once as the region's
   * `id`, once as the toggle's `aria-controls` — so that the control and the region it operates are
   * provably the same element.
   */
  public readonly navigationRegionId: string = NAVIGATION_REGION_ID;

  /**
   * Whether one navigation entry is the current destination. Read by the template for BOTH the active
   * class and `aria-current`, so the visual state and the announced state are the same decision and
   * cannot disagree.
   *
   * @param item The entry to test.
   * @returns True when this entry is the single current destination.
   */
  public isCurrent(item: SidebarNavItem): boolean {
    return this.activePath() === item.path;
  }

  /**
   * The `aria-current` value for one navigation entry: `'page'`, `'true'`, or `null`. ⚠ TWO VALUES RATHER
   * THAN ONE, AND THE DISTINCTION IS THE WHOLE POINT. The rail marks an entry while the operator is on a
   * DESCENDANT of it, because exact matching left every descendant screen with nothing marked at all —
   * see {@link SidebarComponent.activePath}, where that measured defect is recorded.
   *
   * @param item The entry to describe.
   * @returns The attribute value, or null when the attribute must not be present.
   */
  public ariaCurrent(item: SidebarNavItem): 'page' | 'true' | null {
    if (!this.isCurrent(item)) {
      return null;
    }

    const showing: readonly string[] = pathSegments(this.currentUrl());
    const entry: readonly string[] = pathSegments(item.path);

    return showing.length === entry.length ? 'page' : 'true';
  }

  /**
   * Collapses an expanded disclosure and returns focus to the control that opened it.
   *
   * ⚠ #33 — FOCUS COMES BACK WITH IT, which is the half that is easy to miss. Collapsing the rail hides
   * every link inside it, and hiding the element that holds focus drops focus to `body` - so a reader who
   * pressed Escape to get out of the rail would find their next Tab starting again from the top of the
   * document. Returning focus to the toggle leaves them exactly where they were before they opened it.
   *
   * Does nothing when the rail is already collapsed, and nothing when the rail is presented BESIDE the
   * content rather than over it: at a desktop width the rail is not a disclosure a reader is inside, and
   * collapsing it on Escape would be a surprise rather than an escape.
   *
   * @param event The key press, stopped only when this handler acts on it.
   */
  public onEscape(event: KeyboardEvent): void {
    if (this.collapsed() || this.sideBySide()) {
      return;
    }

    this.collapsedSignal.set(true);
    event.preventDefault();
    event.stopPropagation();

    this.toggleControl?.nativeElement.focus();
  }

  /** Collapses the rail if it is expanded, expands it if it is collapsed. */
  public toggleCollapsed(): void {
    // Derived from the EFFECTIVE state rather than from the writable slot, because the slot opens as null
    // and negating null would resolve to the same value twice in a row: on a handset the rail is
    // effectively collapsed, `!null` is true, and the first press would have collapsed an already-collapsed
    // rail and appeared to do nothing.
    const next: boolean = this.collapsed() === false;

    this.collapsedSignal.set(next);
  }
}

/**
 * Splits an in-application address into its path segments. Query and fragment are removed first, because
 * neither participates in identifying a destination: `/users?filter=A` and `/users` are the same entry,
 * and marking the entry current only for one of them would make the rail flicker as an operator filtered
 * a listing.
 *
 * @param address An absolute in-application address, with or without query or fragment.
 * @returns The address's path segments, in order.
 */
function pathSegments(address: string): readonly string[] {
  const path: string = address.split('?')[0].split('#')[0];

  return path.split('/').filter((segment) => segment.length > 0);
}

/**
 * Whether `showing` is the same destination as `candidate` or one below it. ⚠ SEGMENT-WISE, WHICH IS THE
 * WHOLE POINT. A string prefix test would report `/role-groups/new` as being below `/roles`, marking
 * Security Roles current on the role-group screen; comparing segments makes `roles` and `role-groups` the
 * distinct segments they are.
 *
 * @param showing The segments of the address currently showing.
 * @param candidate The segments of a navigation entry's address.
 * @returns True when the address showing is the candidate itself or a descendant of it.
 */
function isDescendantOf(showing: readonly string[], candidate: readonly string[]): boolean {
  if (candidate.length === 0 || showing.length < candidate.length) {
    return false;
  }

  return candidate.every((segment, index) => showing[index] === segment);
}
