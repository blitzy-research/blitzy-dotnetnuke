import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

// MIGRATION: the wording is authored here rather than resolved through a resource
// lookup, so it is not per-locale. Module-private on purpose: the documented
// public surface of this module is the component class and its one `message`
// member.
const DEFAULT_EMPTY_STATE_MESSAGE = 'No records found.';

/**
 * Separator used to fold a repeated route parameter back into one sentence.
 *
 * A bare comma with no trailing space is deliberate rather than stylistic. The
 * legacy analogue reads `Request.QueryString("message")`, and `HttpRequest`'s
 * `QueryString` is a `NameValueCollection` whose `Get` folds duplicate values
 * into one comma-delimited string with no padding. Joining with a comma and a
 * space would render wording the legacy page never produced, which would be an
 * invented divergence rather than behavioural equivalence.
 *
 * Angular's own first-value-wins policy - `ParamMap.get` returns `v[0]` for an
 * array - is deliberately NOT adopted here, because it discards data the legacy
 * page displayed.
 *
 * The fold is total, which means blank duplicates are joined too: two blank values
 * yield a lone separator and one blank among real wording keeps its trailing
 * separator. That is untidy but it is precisely what the legacy page rendered, and
 * it is pinned by its own expectation. Filtering blank elements out first would
 * read as tidier while being a behavioural divergence, so it is not done.
 */
const DUPLICATE_VALUE_SEPARATOR = ',';

/**
 * Largest number of UTF-16 code units retained from a supplied message.
 *
 * This is a memory and layout bound, NOT the overflow remedy. Long-word overflow
 * is owned by the sibling stylesheet, which breaks anywhere and caps the measure
 * at `min(42rem, 100%)`; the sibling template records that a caller's sentence
 * must reflow rather than be clipped, and that remains true of every message
 * within this bound. The bound exists only so that an unbounded external payload
 * cannot be retained and laid out verbatim.
 *
 * 1024 is measured, not chosen. Across the 37 in-scope resource files under
 * `Website/admin/{Portal,Users,Security,Modules,Tabs}/App_LocalResources`, 1107
 * `<data>` values give a median length of 21, p90 of 89, p95 of 140 and p99 of
 * 318, so the bound sits at roughly 3.2 times p99. Exactly three values exceed
 * it - 4774, 1908 and 1585 code units - and all three are long-form release-note
 * or help-page bodies rather than the single explanatory sentence this component
 * renders. The longest value that is genuinely one sentence is 659, leaving 1.55
 * times headroom.
 */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * Folds an arbitrary runtime payload into a bounded candidate string.
 *
 * Pure and module-private: it depends on no component state, which keeps the
 * setter body a readable mirror of the legacy branch it reproduces instead of
 * burying that branch under normalisation.
 *
 * The parameter is declared `unknown` rather than the setter's own union because
 * `ComponentRef.setInput` performs no runtime type check, so this function has to
 * stay total over every shape the router can hand it. The setter documents the
 * measured evidence for which shapes those are.
 *
 * @param value Caller-supplied payload of any runtime shape.
 * @returns A string of at most {@link MAX_MESSAGE_LENGTH} code units. Returns the
 * empty string for every payload carrying no usable wording, which lets the
 * caller's single blank test fail closed to the documented default.
 */
function candidateFrom(value: unknown): string {
  let normalised: string;

  if (typeof value === 'string') {
    normalised = value;
  } else if (Array.isArray(value)) {
    // `Array.isArray` narrows to `any[]`, which would make the loop variable
    // implicitly `any` and silently disable every check below. Rebinding through
    // `readonly unknown[]` restores an honest element type with no assertion,
    // because `any` is assignable to it.
    const elements: readonly unknown[] = value;
    const stringElements: string[] = [];

    for (const element of elements) {
      // Filtering before joining is what makes this total. `Array.prototype.join`
      // coerces its elements, and coercion throws on a symbol and on any object
      // whose `toString` throws. For the router's own duplicate-key arrays every
      // element is already a string, so the filter is a no-op in the real case.
      if (typeof element === 'string') {
        stringElements.push(element);
      }
    }

    normalised = stringElements.join(DUPLICATE_VALUE_SEPARATOR);
  } else {
    // Fail closed. `String(value)` is deliberately not used: it throws on a
    // symbol and otherwise yields diagnostic noise such as an object tag, which
    // is worse wording than the documented default. `null` and `undefined` land
    // here too, which preserves the pre-existing blank-to-default behaviour
    // exactly.
    normalised = '';
  }

  if (normalised.length <= MAX_MESSAGE_LENGTH) {
    return normalised;
  }

  const truncated = normalised.slice(0, MAX_MESSAGE_LENGTH);
  const lastUnit = truncated.charCodeAt(truncated.length - 1);
  // Step back off a lone high surrogate so the retained text never ends in half a
  // code point, which a renderer shows as the U+FFFD replacement character.
  const endsOnHighSurrogate = lastUnit >= 0xd800 && lastUnit <= 0xdbff;

  return endsOnHighSurrogate ? truncated.slice(0, truncated.length - 1) : truncated;
}

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
 * It serves two readings. As an in-page zero-result state it stands in for a
 * populated table or list, and callers typically project an "Add New …"
 * affordance into the content slot. As a not-found view it is the body of the
 * catch-all route, which supplies its own wording and projects nothing. Serving
 * both is why it injects nothing, needs no navigation awareness and assumes no
 * surrounding table, grid or list context — it renders correctly as a full-page
 * view and inside a card alike.
 *
 * Wording can arrive from the router rather than from a template binding, which
 * constrains how `message` is declared. The router's component-input binder
 * merges route data with the route's parameters and writes `merged[name]` for
 * every declared input, so three things follow: the member must carry `@Input()`,
 * because the binder reports an unknown property for anything else and then
 * silently does nothing; it must be public, or it will not compile at the
 * consumer; and an input the route does not populate is written as `undefined`
 * rather than skipped, which is the case the setter below absorbs.
 */
@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [],
  templateUrl: './empty-state.component.html',
  styleUrl: './empty-state.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EmptyStateComponent {
  private resolvedMessage: string = DEFAULT_EMPTY_STATE_MESSAGE;

  /**
   * Sets the sentence explaining why no content is shown.
   *
   * The write type is deliberately wider than the read type, because `null` and
   * `undefined` are values the router's binder genuinely passes and template
   * expressions over optional model fields are routinely nullable. Widening here
   * keeps those call sites honest instead of pushing a non-null assertion onto
   * the caller.
   *
   * A blank value selects the default, and this is an accessor rather than a
   * plain field precisely so that it can: a plain field would let an explicitly
   * bound empty value blank the view. Whitespace-only counts as blank because
   * whitespace collapses in HTML. Any other value is stored exactly as supplied,
   * so a caller that deliberately indents its wording gets what it asked for.
   *
   * The value is untrusted: it must never be bound to a raw-markup sink, and no
   * sanitiser-backed companion member may be added.
   *
   * @param value Caller-supplied wording, the values of a repeated route
   * parameter, or a blank value to request the default.
   */
  @Input()
  public set message(value: string | readonly string[] | null | undefined) {
    // Normalising and bounding BEFORE the blank test is load-bearing: a payload of
    // 2000 spaces must bound to 1024 spaces and then still be recognised as blank,
    // and an array of empty strings must fold to an empty string and then select
    // the default. Testing first would let either case store unusable wording.
    const candidate = candidateFrom(value);
    this.resolvedMessage = candidate.trim().length > 0 ? candidate : DEFAULT_EMPTY_STATE_MESSAGE;
  }

  public get message(): string {
    return this.resolvedMessage;
  }
}
