import {
  Directive,
  Input,
  TemplateRef,
  ViewContainerRef,
  effect,
  inject,
  signal,
} from '@angular/core';

import type { PermissionKey } from '../../core/models/permission.model';
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
 * KEYS ARE COMPARED EXACTLY, AND THE VOCABULARY IS CLOSED AT FOUR. The bound key must
 * be one of the {@link PermissionKey} values, spelled in the upper case the server
 * stores and sends. Nothing is case-folded, trimmed or otherwise normalised on either
 * side, and a value outside the vocabulary is refused rather than compared. The reason
 * is that lenient matching here fails OPEN: were both sides lower-cased, an unrecognised
 * key that merely happened to lower-case onto a held one would silently admit the
 * content, and a misspelling would be indistinguishable from a real grant. Exact
 * comparison also matches the server and the database, where the key is stored as
 * `varchar(20)` and compared with ordinal equality.
 *
 * MIGRATION: the legacy screens performed this check imperatively in the page's own
 * code-behind, calling into `PermissionController`, `ModulePermissionController` or
 * `TabPermissionController` and then setting a control's visibility. Three of those
 * checks lived in the page, which is why the same rule was written slightly differently
 * on different screens. Declaring it in the template puts one rule at every call site.
 *
 * MIGRATION: the legacy comparison was case-sensitive, and so is this one. The class
 * library was compiled with `<OptionCompare>Binary</OptionCompare>`
 * (`Library/DotNetNuke.Library.vbproj:L22`), under which VB string equality is ordinal,
 * and every legacy comparison site relies on it — `ModulePermissionController.vb:L36`,
 * `:L243` and `:L333`, `TabPermissionController.vb:L41`, `:L218` and `:L309`, and
 * `PortalController.vb:L1416`. Case-folding the comparison would therefore have been a
 * behavioural change dressed as a convenience, and one that widened access.
 *
 * @example
 * ```html
 * <button *appHasPermission="'EDIT'" type="button">Edit</button>
 * <a *appHasPermission="['VIEW', 'EDIT']" [routerLink]="link">Settings</a>
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
   * The keys being required, already validated against the closed vocabulary.
   *
   * A signal so the effect below re-evaluates when the binding changes as well as when
   * the session changes — a screen that swaps the required key on the same element must
   * not keep rendering against the previous one.
   */
  private readonly requiredKeys = signal<readonly PermissionKey[]>([]);

  /** Whether the content is currently in the document, so it is created only once. */
  private rendered = false;

  /**
   * The permission key, or keys, that admit the content.
   *
   * A list is satisfied by holding ANY ONE of its keys, not all of them. That is the
   * useful semantic for the administration screens — a control that more than one
   * permission can reach — and the alternative is expressible by nesting the directive,
   * whereas "any" is not expressible from "all".
   *
   * Typed as {@link PermissionKey} rather than `string`, so a misspelling, an invented
   * key or the wrong case is a COMPILE error at the call site instead of a control that
   * silently never appears. The keys the caller holds stay a plain string list, because
   * that is what the server sends; it is only the key being ASKED FOR that is constrained,
   * and it is constrained because it is an authored constant rather than data.
   *
   * @throws Error if no key is supplied, or if a supplied value is not a recognised
   *   permission key.
   */
  @Input({ required: true })
  set appHasPermission(value: PermissionKey | readonly PermissionKey[] | null | undefined) {
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
 * The permission vocabulary, as a membership lookup.
 *
 * Declared as a record keyed by {@link PermissionKey} rather than as an array so that the
 * compiler enforces the set in BOTH directions: a key added to the vocabulary leaves this
 * object incomplete, and a key that is not in the vocabulary is an excess property.
 * Neither compiles. `core/models/permission.model.ts` therefore remains the single
 * definition of the vocabulary — this object cannot drift away from it and still build,
 * which is what makes restating the four values here safe rather than a second source of
 * truth.
 *
 * A record rather than a `Set` because the membership test is then a plain property check
 * with no construction cost, and because a `Set` of string literals would have to be
 * declared as `Set<string>` and would lose exactly the exhaustiveness this buys.
 */
const RECOGNISED_KEYS: Readonly<Record<PermissionKey, true>> = {
  VIEW: true,
  EDIT: true,
  READ: true,
  WRITE: true,
};

/**
 * Whether a value is one of the four recognised permission keys.
 *
 * Uses an own-property test rather than an `in` check or a direct index, so an inherited
 * member such as `constructor` or `toString` cannot masquerade as a permission key.
 *
 * @param value The candidate spelling.
 * @returns True when the value is a recognised key, narrowing it to {@link PermissionKey}.
 */
function isRecognisedKey(value: string): value is PermissionKey {
  return Object.prototype.hasOwnProperty.call(RECOGNISED_KEYS, value);
}

/**
 * Validates the bound permission specification into a comparable key list.
 *
 * NOTHING IS NORMALISED. Keys are neither case-folded nor trimmed, and a value that is
 * not exactly one of the four recognised keys is refused rather than adjusted. Lenient
 * matching here would fail OPEN, which is the one failure mode this directive must not
 * have: lower-casing both sides would let an unrecognised key that merely happened to
 * lower-case onto a held one admit the content. The server and the database compare these
 * keys with ordinal equality, so exact comparison is also the faithful behaviour rather
 * than merely the safe one.
 *
 * A missing or unrecognised specification throws rather than quietly denying. Permission
 * keys in this application are authored constants, never data, so a bad one is a typo, an
 * invented key or a binding to an absent member — and a directive that responded by hiding
 * the control would make that mistake look exactly like a correctly denied permission,
 * which is the single hardest version of this bug to find. Failing loudly at binding time
 * surfaces it on the first render instead. Refusing is also never permissive: no input
 * reaches a state where content is shown that should not be.
 *
 * Takes `unknown` deliberately. The directive's input is typed, so a template gets
 * compile-time protection; this function is the runtime boundary behind it, and a
 * validator that only accepted already-valid values could not do its job.
 *
 * @param value The bound specification.
 * @returns The recognised keys, in the order supplied, never empty.
 * @throws Error if no key is present, or if any value is not a recognised permission key.
 */
export function normaliseRequiredKeys(value: unknown): readonly PermissionKey[] {
  const candidates: readonly unknown[] =
    value === null || value === undefined
      ? []
      : typeof value === 'string'
        ? [value]
        : Array.isArray(value)
          ? value
          : [value];

  if (candidates.length === 0) {
    throw new Error(
      'HasPermissionDirective: at least one permission key is required. ' +
        'An absent key would hide the content in a way indistinguishable from a ' +
        'correctly denied permission, so it is refused instead. Bind a key such as ' +
        "'EDIT', or remove the directive if the content is unconditional.",
    );
  }

  const keys: PermissionKey[] = [];

  for (const candidate of candidates) {
    if (typeof candidate !== 'string' || !isRecognisedKey(candidate)) {
      throw new Error(
        `HasPermissionDirective: ${describeCandidate(candidate)} is not a recognised ` +
          "permission key. The vocabulary is closed at 'VIEW', 'EDIT', 'READ' and " +
          "'WRITE', spelled in upper case exactly as the server stores and sends them. " +
          'Keys are compared exactly, so a differently cased or invented key would ' +
          'never match and the content would silently never appear.',
      );
    }

    keys.push(candidate);
  }

  return keys;
}

/**
 * Renders a rejected candidate for the error message.
 *
 * Quotes a string so that a blank or whitespace-only key is visible in the message rather
 * than vanishing into it, and names the type of anything that is not a string.
 *
 * @param candidate The rejected value.
 * @returns A short description safe to concatenate into an error message.
 */
function describeCandidate(candidate: unknown): string {
  return typeof candidate === 'string' ? `'${candidate}'` : `a value of type ${typeof candidate}`;
}

/**
 * Whether any required key is present in the granted set.
 *
 * COMPARISON IS EXACT ON BOTH SIDES. Neither list is case-folded or trimmed. The granted
 * list arrives already canonical — the server upper-cases, trims and de-duplicates it
 * before issuing it — so there is nothing left to normalise, and normalising anyway would
 * be the fail-open defect described on {@link normaliseRequiredKeys}: it would make an
 * unrecognised spelling match a held key. Anything the server sends that is not an exact
 * key simply grants nothing, which is the same outcome the server itself would reach.
 *
 * The granted side stays a plain string list rather than a typed one because it is wire
 * data: it is whatever arrived, and narrowing it here would assert a guarantee this side
 * of the boundary cannot make.
 *
 * @param required The keys that admit the content.
 * @param granted The keys the signed-in account holds, exactly as the server sent them.
 * @returns True when the content should be rendered.
 */
export function isPermitted(
  required: readonly PermissionKey[],
  granted: readonly string[],
): boolean {
  if (required.length === 0 || granted.length === 0) {
    return false;
  }

  const held = new Set<string>(granted);

  return required.some((key) => held.has(key));
}
