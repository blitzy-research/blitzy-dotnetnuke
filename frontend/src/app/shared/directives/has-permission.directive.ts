import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';

import {
  isPermissionKey,
  toPermissionKeys,
  type PermissionKey,
} from '../../core/models/permission.model';
import { AuthStore } from '../../core/state/auth.store';

/**
 * Renders its content only while the signed-in caller holds the named permission key,
 * and removes that content from the document entirely when they do not.
 *
 * ## ⚠ This is an affordance, never the sole enforcement mechanism
 *
 * The client-side `hasPermission` directive is NEVER the sole enforcement mechanism.
 * The permission list it reads is advisory: it exists so the client can hide affordances
 * the caller cannot exercise. Authoritative enforcement is server-side policy-based
 * authorisation, which re-evaluates every request against stored state and answers
 * HTTP 403. Hiding a button therefore removes a pointless affordance rather than
 * protecting a resource — a caller who edits the delivered script, replays a response or
 * types the address directly arrives at the same server check either way.
 *
 * The wording above is deliberately the wording carried by the permission member of the
 * current-user contract in `core/models/auth.model.ts`, so the two files cannot drift
 * into disagreeing about what this directive is for.
 *
 * ## Where the answer comes from, and why nothing is recomputed here
 *
 * The key list is read from the signed-in identity that the auth store projects. The
 * server resolved that list for this account in this tenant, so this directive performs
 * a single membership test and applies no rules of its own:
 *
 * - The server owns the precedence rules and evaluates them in one place. A second
 *   implementation on the client could disagree with it, and a client that offers a
 *   control the server refuses is worse than one that offers nothing.
 * - A host, or super-user, account gets no special case here. That flag widens the set of
 *   tenants reachable, not the set of operations permitted within one, so treating it as a
 *   blanket grant would invent an entitlement the server never issued.
 * - No role name is inspected and no permission is derived from one. Roles and permission
 *   keys are different vocabularies, and mapping between them is the server's job.
 *
 * ## ⚠ Comparison is exact, and normalising it would fail OPEN
 *
 * The required key is compared to the held keys with ordinal, case-sensitive equality.
 * Nothing is upper-cased, lower-cased, trimmed, collated or matched loosely on either
 * side. That is not fussiness: case-folding both sides would let an unrecognised spelling
 * collide with a real key and silently ADMIT content, which is the one failure mode this
 * directive must never have. Exact comparison is also the faithful behaviour, because the
 * database stores the key as `varchar(20)` and the server compares it ordinally.
 *
 * ## ⚠ Every abnormal input denies
 *
 * An unrecognised key, a differently cased key, the empty string, and an unresolved or
 * absent key list all resolve to "not granted". There is no input for which this
 * directive invents a permissive default, and it never throws to report a denial — a
 * thrown error would take down the surrounding screen over a hidden button.
 *
 * ## Guarantees
 *
 * Makes no HTTP request of any kind, at no point in its lifetime. Performs no navigation
 * and no redirect. Holds no ambient state, reads no browser storage, and decodes no
 * token. Its only side effect is creating or clearing its own embedded view.
 *
 * ## ⚠ Apply it to the WRAPPER, not to the bare control
 *
 * Because the subject is removed rather than hidden, apply this directive to the whole
 * labelled unit — the `app-form-field` element, the table row, the toolbar group — and
 * never to a control nested inside one. Applied to the inner control it would leave a
 * label whose `for` points at an element that no longer exists, and would strand any
 * `aria-labelledby`, `aria-describedby`, `aria-controls` or `aria-owns` attribute that
 * referenced the removed id. Removing the whole unit leaves nothing dangling and nothing
 * to receive focus, which is exactly why removal is preferable to hiding: a merely
 * hidden element can still be reachable, and a disabled one still occupies the
 * accessibility tree.
 *
 * @example
 * ```html
 * <app-form-field *hasPermission="'EDIT'" label="Site name" for="site-name">
 *   <input id="site-name" type="text" [formControl]="control" />
 * </app-form-field>
 *
 * <button *hasPermission="'EDIT'" type="button" (click)="remove()">Delete</button>
 * ```
 */
@Directive({
  selector: '[hasPermission]',
  standalone: true,
})
export class HasPermissionDirective {
  /**
   * The content being gated.
   *
   * Typed `TemplateRef<unknown>` rather than with a context type, because this directive
   * publishes no template context: there is nothing useful to expose to the content, and
   * declaring a context would invite call sites to depend on one.
   */
  private readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);

  /** The location the content is created in and cleared from. */
  private readonly viewContainer = inject(ViewContainerRef);

  /**
   * The signed-in caller's already-resolved permission keys.
   *
   * The auth store is the single owner of that list, and it prefers the identity last
   * read from the server over the copy captured when the credentials were issued — so a
   * permission granted after sign-in becomes visible on the next identity refresh
   * without the session being renewed. Reading the store rather than the session
   * custodian directly is what buys that.
   */
  private readonly authStore = inject(AuthStore);

  /**
   * The permission key that admits the content.
   *
   * A single required key, deliberately: one member of the closed vocabulary, not a list.
   *
   * ⚠ TYPED AS {@link PermissionKey}, NOT AS `string`, AND THE DISTINCTION IS THE WHOLE
   * SAFETY PROPERTY. The value on this input is something the APPLICATION decides — the
   * key a screen requires — so it belongs to the closed set of keys this codebase
   * evaluates. It is NOT a value the server supplies. Typing it as a bare `string` made a
   * whole class of confusion expressible: an authorisation POLICY name, a permission
   * CODE, a role name or an installation-specific key could all be written here, and each
   * would have been treated as a render grant the moment a matching string appeared in the
   * caller's granted list. Three different vocabularies exist in this system and only one
   * of them belongs here; the compiler now says so.
   *
   * The static type is not the only defence, because a template expression can produce a
   * widened value and route data is `unknown` at the edges. The verdict below therefore
   * re-tests the received value at RUN TIME through {@link isPermissionKey} and refuses
   * anything unrecognised — so an unknown key fails CLOSED whether it slipped past the
   * compiler or not.
   *
   * A single key because no call site needs more. Two keys on one element is expressible
   * by nesting, whereas an "any of" input would have to define — and then defend —
   * whether it means any or all. It is not added ahead of a consumer that wants it.
   *
   * A signal input, so the one effect below re-evaluates both when the session changes
   * and when a screen swaps the required key on the same element.
   */
  readonly hasPermission = input.required<PermissionKey>();

  /**
   * Whether the content is currently in the document.
   *
   * The transition guard. Without it, every re-run of the effect would tear the content
   * down and rebuild it, resetting state inside it and moving focus out of it, whenever
   * anything the effect happens to read changes. Mutable and private, because it mirrors
   * DOM reality rather than modelling application state — nothing outside this directive
   * may observe or set it.
   */
  private rendered = false;

  constructor() {
    // An effect rather than a subscription, because adding and removing DOM is a genuine
    // side effect and this is the only kind of work an effect is for. Created in the
    // injection context, so it is destroyed with the directive and there is nothing to
    // unsubscribe and no lifecycle hook to add. Both sources are tracked by the single
    // read of each below: signing out withdraws the affordance immediately instead of
    // leaving it on screen until the next navigation.
    effect(() => {
      // Both sources are narrowed to the closed vocabulary BEFORE they meet. The required
      // key is re-tested at run time even though it is statically typed, because a
      // template expression can widen and route data arrives untyped; the held list is
      // narrowed because its producer is genuinely open. Whichever side carries an
      // unrecognised value, the verdict is a refusal rather than a coincidental match.
      const required = this.hasPermission();
      const held = toPermissionKeys(this.authStore.permissions());

      this.render(isGranted(required, held));
    });
  }

  /**
   * Adds the content when it is admitted and removes it when it is not.
   *
   * Acts only on a CHANGE of verdict. A re-evaluation that reaches the same conclusion
   * does nothing at all, which is what makes repeated runs of the effect harmless.
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
      // Removal, not concealment. A hidden element is still in the document, may still be
      // in the accessibility tree depending on how it was hidden, and is still reachable
      // by anything that clears the style — none of which is what "you cannot do this"
      // should mean.
      this.viewContainer.clear();
    }

    this.rendered = granted;
  }
}

/**
 * Whether a required permission key appears in the keys the caller holds.
 *
 * ⚠ FAILS CLOSED, WITHOUT EXCEPTION. Four abnormal inputs all reach `false`:
 *
 * - a key this application does not recognise, because non-membership never grants;
 * - a key spelled in a different case, which is the outcome that proves no case-folding
 *   crept in;
 * - the empty string, which is refused explicitly. The legacy catalogue read the empty
 *   key as a WILDCARD meaning "any key" — `PortalController.vb:L1413` passes it to
 *   `GetPermissionByCodeAndKey("SYSTEM_FOLDER", "")` and then narrows the result by
 *   comparing against a real key at `:L1416`. That wildcard is a server-side query
 *   convenience and is deliberately NOT honoured here: on this side the empty string
 *   means "no key", never "every key";
 * - an empty key list, whether that is a caller holding none or a session not yet
 *   resolved. Both deny. They are separate facts and neither is decided by testing the
 *   list for truthiness — the length is compared explicitly, so an empty list is treated
 *   as the answer it is rather than as a missing one.
 *
 * Comparison is a plain `===` between two strings. No normalisation, no pattern, no
 * splitting on a separator, no bracket form, no negation prefix and no bitwise
 * combination: none of those encodings survives the server boundary, so parsing one here
 * would be inventing a contract.
 *
 * A module-level function rather than a method, so it is a pure decision over its two
 * arguments with no access to the injector, the DOM or the store — which is what makes it
 * trivially reviewable, the property that matters most in a permission check.
 *
 * @param required The key the call site asks for, re-tested against the closed vocabulary.
 * @param held The recognised subset of the keys the caller holds, already narrowed.
 * @returns True only when `required` is a recognised key and `held` contains it identically.
 */
function isGranted(required: PermissionKey, held: readonly PermissionKey[]): boolean {
  // FAIL CLOSED ON AN UNRECOGNISED REQUIREMENT. `required` is statically a
  // `PermissionKey`, but a static type is a claim about the source and not a fact about
  // the value: a template expression can widen, and a key threaded through route data or
  // a loosely typed view model arrives as whatever it arrives as. Re-testing it here means
  // an unknown value — a policy name, a permission code, a lower-case spelling, a padded
  // string, the empty string — refuses instead of being carried into the membership test
  // where a matching entry in the granted list would admit it. The empty-string case that
  // used to be spelled out separately is subsumed: it is not a recognised key.
  if (isPermissionKey(required) === false) {
    return false;
  }

  if (held.length === 0) {
    return false;
  }

  return held.includes(required);
}

/*
 * MIGRATION: element-level permission gating is now DECLARED rather than performed.
 * The legacy screens ran this check imperatively in each page's own code-behind — the 39
 * administration code-behinds under `Website/admin/{Portal,Users,Security,Modules,Tabs}/`
 * each called into `PermissionController`, `ModulePermissionController` or
 * `TabPermissionController` and then set a control's visibility, which is why the same
 * rule was written slightly differently from screen to screen. Declaring it in the
 * template puts one rule at every call site.
 *
 * MIGRATION: the authorisation DECISION moved to the server, which answers HTTP 403, and
 * this directive is reduced to an affordance. That is deliberately unlike
 * `Library/Components/Users/UserModuleBase.vb:L466-L505`, where reading a page property
 * ran an own-record test (L474), a super-user test (L476) and an administrator-role test
 * (L479), issued a database query (L481) and could NAVIGATE (L494,
 * `Response.Redirect(NavigateURL("Access Denied"), True)`). None of that is reproduced:
 * this file performs no request, no navigation and no lookup. The legacy separation of
 * concerns is in fact restored rather than invented — `PermissionController.vb` exposes
 * nine members (L30-L63) and not one of them is a decision function.
 *
 * MIGRATION: comparison stays ordinal and case-sensitive, matching the legacy semantics
 * exactly. `Library/DotNetNuke.Library.vbproj:L22` declares
 * `<OptionCompare>Binary</OptionCompare>`, under which VB string equality is ordinal, and
 * every legacy comparison site depended on it: `ModulePermissionController.vb:L36`, `:L243`
 * and `:L333`, `TabPermissionController.vb:L41`, `:L218` and `:L309`,
 * `FolderPermissionController.vb:L33` and `:L275`, and `PortalController.vb:L1416`. The
 * persisted vocabulary is closed at four upper-case keys — `VIEW`, `EDIT`, `READ` and
 * `WRITE`, stored as `varchar(20)` per `02.02.00.SqlDataProvider:L688` — in which `EDIT`
 * covers create, update AND delete, there being no separate delete key. Case-folding the
 * comparison would have widened access under the guise of convenience.
 *
 * MIGRATION: no deny, or negation, semantics are implemented, because this generation of
 * the product has none to carry forward. Searching `Library/Components/Security` for a
 * negation prefix, a prefix-stripping `Substring(1)`, an inverted role test, or the words
 * `Deny`/`deny` returns nothing in every case. The legacy grant mechanism was a boolean
 * column, `AllowAccess bit NOT NULL` (`02.02.00.SqlDataProvider:L680`), honoured at
 * `ModulePermissionController.vb:L243` and `:L333` and `TabPermissionController.vb:L218`
 * and `:L309`. Note that the legacy decision path was internally INCONSISTENT about it —
 * `HasModulePermission` (L36) and `HasTabPermission` (L41) never tested that column at all
 * while the projection functions did — which is a further reason the client must not
 * attempt to re-derive any of it.
 *
 * MIGRATION: the authorisation POLICY vocabulary is a separate, closed set of NINE names
 * registered by the API — the eight route-scoped and membership policies plus
 * `PortalContentEditor`, which decides at portal scope and needs no route identifier — and
 * is NOT what this directive takes. Policy names are the server's
 * route-level contract, enumerated once in `core/guards/permission.guard.ts`; the
 * permission keys above are the persisted data. A third axis exists as well, the
 * permission CODE that scopes a key to folders, module definitions or pages — `READ` and
 * `WRITE` are folder-scoped keys, which is why they never reach a policy and why file
 * management is outside this migration. Passing a value from one of those vocabularies
 * where another is expected is exactly the confusion this input's {@link PermissionKey}
 * type now refuses to legitimise — the compiler rejects it, and
 * {@link isPermissionKey} refuses it again at run time for the cases a static type cannot
 * reach. Nothing here restates a policy name or a code.
 *
 * MIGRATION: the legacy role encodings are deliberately not carried forward — not because
 * they never existed, but because the server flattens them before anything reaches the
 * browser. Both encodings are real: the semicolon-delimited role string is built in
 * `ModulePermissionController.vb:L245`, `:L247` and `:L251` and parsed at
 * `PortalSecurity.vb:L122`, and the bracketed pseudo-role naming a single user is
 * constructed at eight sites under `Library/Components/Security/Permissions/` and matched
 * at `UserInfo.vb:L333`. The rest of the legacy algorithm was ambient by nature —
 * `PortalSecurity.vb:L118-L119` reached for the current request and the current user, and
 * `UserInfo.vb:L261` hydrated the caller's roles with a database query from inside a
 * property getter. Strip everything the server has already resolved and the client's
 * entire remaining job is one exact-string membership test over a flat list, which is all
 * this file does.
 */
