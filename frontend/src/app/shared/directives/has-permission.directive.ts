import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';

import {
  isPermissionKey,
  toPermissionKeys,
  type PermissionKey,
} from '../../core/models/permission.model';
import { AuthStore } from '../../core/state/auth.store';

/**
 * Renders its content only while the signed-in caller holds the named permission key, and removes that
 * content from the document entirely when they do not. ## ⚠ This is an affordance, never the sole
 * enforcement mechanism The client-side `hasPermission` directive is NEVER the sole enforcement
 * mechanism.
 */
@Directive({
  selector: '[hasPermission]',
  standalone: true,
})
export class HasPermissionDirective {
  /** The content being gated. */
  private readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);

  /** The location the content is created in and cleared from. */
  private readonly viewContainer = inject(ViewContainerRef);

  /**
   * The signed-in caller's already-resolved permission keys. The auth store is the single owner of that
   * list, and it prefers the identity last read from the server over the copy captured when the
   * credentials were issued — so a permission granted after sign-in becomes visible on the next identity
   * refresh without the session being renewed.
   */
  private readonly authStore = inject(AuthStore);

  /**
   * The permission key that admits the content. A single required key, deliberately: one member of the
   * closed vocabulary, not a list. ⚠ TYPED AS {@link PermissionKey}, NOT AS `string`, AND THE DISTINCTION
   * IS THE WHOLE SAFETY PROPERTY. The value on this input is something the APPLICATION decides — the key
   * a screen requires — so it belongs to the closed set of keys this codebase evaluates.
   */
  readonly hasPermission = input.required<PermissionKey>();

  /** Whether the content is currently in the document. The transition guard. */
  private rendered = false;

  constructor() {
    // An effect rather than a subscription, because adding and removing DOM is a genuine side effect and
    // this is the only kind of work an effect is for. Created in the injection context, so it is destroyed
    // with the directive and there is nothing to unsubscribe and no lifecycle hook to add.
    effect(() => {
      // Both sources are narrowed to the closed vocabulary BEFORE they meet. The required key is re-tested
      // at run time even though it is statically typed, because a template expression can widen and route
      // data arrives untyped; the held list is narrowed because its producer is genuinely open.
      const required = this.hasPermission();
      const held = toPermissionKeys(this.authStore.permissions());

      this.render(isGranted(required, held));
    });
  }

  /**
   * Adds the content when it is admitted and removes it when it is not.
   *
   * @param granted Whether the content should be present in the document.
   */
  private render(granted: boolean): void {
    if (granted === this.rendered) {
      return;
    }

    if (granted === true) {
      this.viewContainer.createEmbeddedView(this.templateRef);
    } else {
      // Removal, not concealment. A hidden element is still in the document, may still be in the
      // accessibility tree depending on how it was hidden, and is still reachable by anything that clears
      // the style — none of which is what "you cannot do this" should mean.
      this.viewContainer.clear();
    }

    this.rendered = granted;
  }
}

/**
 * Whether a required permission key appears in the keys the caller holds. ⚠ FAILS CLOSED, WITHOUT
 * EXCEPTION. Four abnormal inputs all reach `false`: - a key this application does not recognise, because
 * non-membership never grants; - a key spelled in a different case, which is the outcome that proves no
 * case-folding crept in; - the empty string, which is refused explicitly.
 *
 * @param required The key the call site asks for, re-tested against the closed vocabulary.
 * @param held The recognised subset of the keys the caller holds, already narrowed.
 * @returns True only when `required` is a recognised key and `held` contains it identically.
 */
function isGranted(required: PermissionKey, held: readonly PermissionKey[]): boolean {
  if (isPermissionKey(required) === false) {
    return false;
  }

  if (held.length === 0) {
    return false;
  }

  return held.includes(required);
}

// Element-level permission gating is now DECLARED rather than performed.
