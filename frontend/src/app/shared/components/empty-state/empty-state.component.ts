//
// EmptyStateComponent - the shared "zero-result state" of the dnn-migration
// administration front end, an Angular 19 single-page application.
//
// This component is member 9 of the ten-member in-repository shared component
// library defined by the project plan (data-table, page-header, confirm-dialog,
// pagination, form-field, search-input, loading-spinner, error-banner,
// empty-state, plus the hasPermission structural directive). That inventory is
// deliberately CLOSED, so this file adds no sibling component and no variant.
//
// Deliberate divergences from the legacy DotNetNuke 4.9.0 VB.NET Web Forms
// behaviour are annotated inline below, as the Minimal Change Clause (item 6)
// and transformation Rule T5 require. Nothing is silently absorbed.
//

import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

// MIGRATION: localisation is deliberately not ported. Legacy screens resolved
// every caption through `Services.Localization.Localization.GetString(key,
// LocalResourceFile)` against 37 in-scope `App_LocalResources/*.resx` files. The
// Angular localisation package sits outside this workspace's pinned dependency
// set, so default wording is authored directly in TypeScript and in the sibling
// template. The legacy resource files were read for wording only, and they hold
// no in-scope zero-result caption at all: the single such caption anywhere in the
// repository is `SQL.ascx.resx -> NoDataReturned.Text` ("The query did not return
// any data") on the out-of-scope host-level SQL admin page. This wording is
// therefore authored, not ported.
/**
 * Wording rendered when the caller supplies no usable message.
 *
 * Kept module-private on purpose: this module's documented public surface is the
 * single exported component class, whose only public member is `message`.
 * Exporting this constant would widen that surface beyond the specification.
 */
const DEFAULT_EMPTY_STATE_MESSAGE = 'No records found.';

// MIGRATION: this component is effectively a NET ADDITION. The project plan cites
// it as replacing the legacy `.DNNEmptyPane` affordance, but that citation
// supplies a name and no design. `Website/Portals/_default/default.css:958-962`
// declares `.DNNEmptyPane { width: 0px; }` and nothing else - no background,
// border, typography, spacing or messaging - and the class is reachable from
// exactly one code site repository-wide, `CollapsePane` in
// `Library/Components/Skins/Skin.vb:103-112`, which appends it to a layout pane
// holding no modules. `Library/Components/Skins/**` is explicitly excluded from
// this migration, so the class is referenced from zero in-scope markup or
// code-behind and is semantically a layout-collapse utility rather than a
// user-facing zero-result state. The legacy admin grids reinforce the point:
// `Website/admin/Portal/portals.ascx:13-19` declares the full `DataGrid_Container`
// / `DataGrid_Header` / `DataGrid_Item` / `DataGrid_AlternatingItem` /
// `DataGrid_Footer` / `DataGrid_Pager` vocabulary and no `EmptyDataTemplate`, so
// an empty result set rendered silently. Explaining the absence instead is a
// deliberate, documented improvement.
//
// MIGRATION: serving two readings from one component is why no separate
// not-found component exists in this workspace, and why the shared library stays
// closed at ten members rather than gaining an eleventh. Two consequences are
// load-bearing and must not be "tidied up" later. First, this component takes no
// constructor parameters and no injected dependencies whatsoever: it needs zero
// navigation awareness, because its wording arrives purely through input binding.
// Second, it assumes no surrounding table, grid or list context, so it renders
// correctly as a full-page view as well as inside a card.
/**
 * Presentational component that explains the absence of content, then optionally
 * offers one projected call to action.
 *
 * ## Two readings, one component
 *
 * 1. **In-page zero-result state** - rendered in place of a populated data table
 *    or list when a query returns no rows. Callers typically project an
 *    "Add New ..." affordance into the content slot.
 * 2. **Not-found view** - used as the body of the application's catch-all route,
 *    which supplies its own wording through route `data` and projects nothing.
 *
 * ## How the catch-all route reaches `message`
 *
 * The application configures the router with `withComponentInputBinding()`. On
 * every navigation the router's component-input binder merges query parameters,
 * path parameters and static route `data` into one object, reflects the routed
 * component's declared inputs, and calls
 * `ComponentRef.setInput(name, merged[name])` for **each** of them. Three
 * properties of that mechanism shape this file:
 *
 * - The member must be declared with `@Input()`. A plain public field is not
 *   enough: `setInput` reports an unknown-property error for anything that is not
 *   a declared input, and the route binding then silently does nothing.
 * - The member must be **public**. `strictInputAccessModifiers` is enabled in
 *   this workspace, so a `private` or `protected` input is a compile error at the
 *   consumer.
 * - Because the binder assigns `merged[name]` unconditionally, an input absent
 *   from the merged object is explicitly set to `undefined`. The setter below
 *   absorbs exactly that case, so reusing this component on a route that supplies
 *   no wording degrades to the default rather than to a blank screen.
 *
 * `setInput` also marks the view dirty, so `OnPush` change detection refreshes
 * without any manual `markForCheck` call.
 *
 * ## Rendering contract with the sibling template
 *
 * The sibling `empty-state.component.html` interpolates `{{ message }}` and
 * declares one `<ng-content>` slot for the optional action, which collapses to
 * nothing when the caller projects nothing. The getter below guarantees a
 * non-blank string on every read, which is what makes it impossible for the
 * rendered output to read "undefined" or "null", and impossible for a truthiness
 * guard in the template to collapse the view.
 *
 * Semantic markup, ARIA and styling belong to the sibling template and stylesheet
 * respectively; this class deliberately declares no host bindings, so it cannot
 * conflict with the semantics those files establish.
 *
 * @example Zero-result state with a projected primary action
 * ```html
 * <app-empty-state [message]="'No roles match the current filter.'">
 *   <button type="button" (click)="createRole()">Add New Role</button>
 * </app-empty-state>
 * ```
 *
 * @example Zero-result state relying on the default wording
 * ```html
 * <app-empty-state />
 * ```
 */
@Component({
  selector: 'app-empty-state',
  standalone: true,
  // The sibling template uses only interpolation and a content slot, so it
  // depends on no external selector, directive or pipe. Under `strictTemplates`
  // an unimported selector is a compile error, so this list stays empty and
  // explicit rather than being speculatively populated.
  imports: [],
  templateUrl: './empty-state.component.html',
  // Singular `styleUrl` (Angular 17 and later). The plural form appears nowhere
  // in this workspace, and neither does an inline stylesheet: the sibling
  // stylesheet consumes the shared design tokens.
  styleUrl: './empty-state.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EmptyStateComponent {
  // MIGRATION: ViewState is eliminated. The legacy Web Forms lifecycle
  // round-tripped control state to the browser and back through ViewState plus
  // the hidden `ScrollTop` and `__dnnVariable` fields declared at
  // `Website/Default.aspx:26-27`. Client state in the target lives in
  // feature-scoped Signal stores, and this component holds no state of any kind:
  // no signal, no stream, no lifecycle hook and no output. It renders exactly
  // what it is given, which is what makes it safe to reuse in both readings above
  // and trivial to test.
  /**
   * Backing store for {@link EmptyStateComponent.message}.
   *
   * Seeded with the default so that an instance created with no binding at all -
   * the `<app-empty-state />` case - still renders meaningful wording. Private
   * because it is an implementation detail; the accessor pair is the public
   * member, and `strictInputAccessModifiers` constrains only that accessor pair.
   */
  private resolvedMessage: string = DEFAULT_EMPTY_STATE_MESSAGE;

  // MIGRATION: substituting the default whenever the supplied value is blank
  // reproduces `Website/admin/Security/AccessDenied.ascx.vb:41-47`, which tests
  // `If Request.QueryString("message") <> "" Then` and otherwise falls back to a
  // localised default. That legacy fallback is precisely why `message` is an
  // accessor rather than a plain field: a plain field would let an explicitly
  // bound empty value blank the view, losing behavioural equivalence. Treating a
  // whitespace-only value as blank is a narrow, deliberate widening of the legacy
  // `<> ""` test, because whitespace collapses in HTML and would render a
  // visually blank state. An accepted value is otherwise stored exactly as
  // supplied, so no trimming or reformatting alters what the caller asked to
  // display.
  //
  // MIGRATION: `message` is UNTRUSTED and reaches the DOM as plain text only, via
  // template interpolation. It is never bound to a raw-markup sink, and this
  // component intentionally exposes no sanitiser-backed companion member. The
  // legacy corpus justifies the policy rather than contradicting it: of 1182
  // `<data>` values across the 37 in-scope resource files, 76 embed an HTML tag
  // (per-value histogram br 39, p 24, h1 21, b 10, a 5, li 3, span 2, ul 2, h3 1,
  // h4 1, script 1, strong 1), 29 open with a leading line break, and
  // `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx ->
  // Advertising.Text` stores a live advertising script block with a remote source.
  // Escaping is legacy CONTINUITY, not invention: the closest legacy analogue,
  // `Website/admin/Security/AccessDenied.ascx.vb:43`, wraps its externally
  // supplied message in `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))`, and
  // `Website/Default.aspx.vb:232` likewise uses `Server.HtmlEncode`. Defensive
  // stripping of a leading line break belongs to the shared form-error utility
  // under `core/utils`, deliberately not here, so this component stays a pure sink.
  /**
   * Sets the sentence explaining why no content is shown.
   *
   * The write type is intentionally wider than the read type. It admits `null`
   * and `undefined` because those are values the router's component-input binder
   * can genuinely pass - it assigns `merged[name]` for every declared input,
   * including inputs a given route does not populate - and because template
   * expressions over optional model fields are routinely nullable. Widening the
   * setter keeps those call sites honest instead of pushing a non-null assertion
   * onto the caller.
   *
   * A blank value - empty, `null`, `undefined`, or whitespace only - selects the
   * default wording. Any other value is stored verbatim.
   *
   * @param value Caller-supplied wording, or a blank value to request the
   * default.
   */
  @Input()
  public set message(value: string | null | undefined) {
    const candidate = value ?? '';
    this.resolvedMessage = candidate.trim().length > 0 ? candidate : DEFAULT_EMPTY_STATE_MESSAGE;
  }

  /**
   * The sentence to display. Never blank, never `null`, never `undefined`.
   *
   * The sibling template interpolates this member directly, so the guarantee is
   * what keeps "undefined" out of the rendered output and keeps a truthiness
   * guard in the template from collapsing the view.
   */
  public get message(): string {
    return this.resolvedMessage;
  }
}
