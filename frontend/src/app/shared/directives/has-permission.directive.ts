import {
  Directive,
  Input,
  TemplateRef,
  ViewContainerRef,
  effect,
  inject,
  signal,
} from '@angular/core';

import { TokenStorageService } from '../../core/services/token-storage.service';

/**
 * Renders its content only when the signed-in account holds one of the named
 * permission keys.
 *
 * NEVER THE ENFORCEMENT MECHANISM, and the distinction is the whole point. This
 * decides what to RENDER. Every operation is authorised again by the server on every
 * request, through the policy-based authorisation handler and the single permission
 * evaluator behind it. A person who edits the delivered script, replays a response, or
 * simply types the route directly reaches the same server check either way — so hiding
 * a control here removes a pointless affordance rather than protecting a resource.
 *
 * The permission source is the key list on the signed-in identity, which the server
 * resolved for that account in that tenant at sign-in. Consulting that list rather than
 * re-deriving anything is deliberate, and it is why this directive contains no rules:
 *
 * - The server already applies the allow-and-deny precedence in one place. A second
 *   implementation here could disagree with it, and a client that shows a control the
 *   server refuses is worse than one that shows nothing.
 * - A host account needs no special case. The server returns the ENTIRE permission
 *   catalogue for a host account, so the list already contains every key. Hard-coding a
 *   host bypass here would be a second, divergent rule that would then have to be kept
 *   in step with the server's own.
 * - When the server cannot resolve the key list it issues an empty one rather than
 *   refusing the sign-in. This directive therefore renders nothing in that case, which
 *   mirrors the server exactly: no claims means no affordances, and the person still
 *   reaches every screen the server admits them to.
 *
 * MIGRATION: the legacy screens performed this check imperatively in the page's own
 * code-behind, calling into `PermissionController`, `ModulePermissionController` or
 * `TabPermissionController` and then setting a control's visibility. Three of those
 * checks lived in the page, which is why the same rule was written slightly differently
 * on different screens. Declaring it in the template puts one rule at every call site.
 *
 * @example
 * ```html
 * <button *appHasPermission="'EDIT'" type="button">Edit</button>
 * <a *appHasPermission="['EDIT', 'MANAGE']" [routerLink]="link">Settings</a>
 * ```
 */
@Directive({
  selector: '[appHasPermission]',
  standalone: true,
})
export class HasPermissionDirective {
  private readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly tokenStorage = inject(TokenStorageService);

  /**
   * The keys being required, already lower-cased for comparison.
   *
   * A signal so the effect below re-evaluates when the binding changes as well as when
   * the session changes — a screen that swaps the required key on the same element must
   * not keep rendering against the previous one.
   */
  private readonly requiredKeys = signal<readonly string[]>([]);

  /** Whether the content is currently in the document, so it is created only once. */
  private rendered = false;

  /**
   * The permission key, or keys, that admit the content.
   *
   * A list is satisfied by holding ANY ONE of its keys, not all of them. That is the
   * useful semantic for the administration screens — a control that several permissions
   * can reach, such as a settings link open to both editors and managers — and the
   * alternative is expressible by nesting the directive, whereas "any" is not
   * expressible from "all".
   *
   * @throws Error if no non-blank key is supplied.
   */
  @Input({ required: true })
  set appHasPermission(value: string | readonly string[] | null | undefined) {
    this.requiredKeys.set(normaliseRequiredKeys(value));
  }

  constructor() {
    // An effect rather than a subscription: the permission list is a signal, so the
    // effect re-runs when the session is stored, refreshed or cleared, and it is torn
    // down with the directive without anything to unsubscribe. Sign-out therefore
    // withdraws the affordance immediately rather than leaving it on screen until the
    // next navigation.
    effect(() => {
      const granted = this.tokenStorage.permissions();
      const required = this.requiredKeys();

      this.render(isPermitted(required, granted));
    });
  }

  /**
   * Adds or removes the content.
   *
   * The view is REMOVED rather than hidden. A hidden control is still in the document,
   * still in the accessibility tree depending on how it was hidden, and still reachable
   * by a script that clears the style — none of which is what "you cannot do this" should
   * mean.
   *
   * Guarded by {@link rendered} so a re-evaluation that reaches the same conclusion does
   * not destroy and rebuild the content, which would reset any state inside it and move
   * focus out of it.
   *
   * @param permitted Whether the content should be present.
   */
  private render(permitted: boolean): void {
    if (permitted === this.rendered) {
      return;
    }

    if (permitted) {
      this.viewContainer.createEmbeddedView(this.templateRef);
    } else {
      this.viewContainer.clear();
    }

    this.rendered = permitted;
  }
}

/**
 * Normalises the bound permission specification into a comparable key list.
 *
 * Keys are lower-cased so that a template may spell a key in the case that reads best
 * while still matching the server's canonical spelling. Case cannot change WHICH key is
 * being asked for, so this widens nothing: `EDIT` and `edit` name one permission, and
 * treating them as two would produce a control that silently never appears.
 *
 * A blank specification throws rather than quietly denying. Permission keys in this
 * application are authored constants, never data, so a blank one is a typo or a binding
 * to an absent member — and a directive that responded by hiding the control would make
 * that typo look exactly like a correctly denied permission, which is the single hardest
 * version of this bug to find. Failing loudly at binding time surfaces it on the first
 * render instead.
 *
 * @param value The bound specification.
 * @returns The lower-cased keys, never empty.
 * @throws Error if no non-blank key is present.
 */
export function normaliseRequiredKeys(
  value: string | readonly string[] | null | undefined,
): readonly string[] {
  const candidates = typeof value === 'string' ? [value] : (value ?? []);

  const keys = candidates
    .filter((key): key is string => typeof key === 'string')
    .map((key) => key.trim().toLowerCase())
    .filter((key) => key.length > 0);

  if (keys.length === 0) {
    throw new Error(
      'HasPermissionDirective: at least one non-blank permission key is required. ' +
        'A blank key would hide the content in a way indistinguishable from a ' +
        'correctly denied permission, so it is refused instead. Bind a key such as ' +
        "'EDIT', or remove the directive if the content is unconditional.",
    );
  }

  return keys;
}

/**
 * Whether any required key is present in the granted set.
 *
 * The granted list arrives from the server in its own casing, so both sides are compared
 * lower-cased. The required side is already normalised by
 * {@link normaliseRequiredKeys}; the granted side is normalised here, at the point of
 * comparison, because it is not this application's value to reshape.
 *
 * @param required The lower-cased keys that admit the content.
 * @param granted The keys the signed-in account holds.
 * @returns True when the content should be rendered.
 */
export function isPermitted(
  required: readonly string[],
  granted: readonly string[],
): boolean {
  if (required.length === 0 || granted.length === 0) {
    return false;
  }

  const held = new Set(granted.map((key) => key.trim().toLowerCase()));

  return required.some((key) => held.has(key));
}
