//
// The labelled-field wrapper for the administration screens.
//
// ---------------------------------------------------------------------------
// WHAT THIS COMPONENT IS, AND WHAT IT DELIBERATELY IS NOT
// ---------------------------------------------------------------------------
// It renders four things around a control it does not own: the label, an
// optional help disclosure, the projected control (or controls), and the
// validation messages. It renders NO control of its own, holds NO user-facing
// wording of its own, performs NO request, reads NO route, injects NO service
// and owns NO domain state. Everything it shows arrives through an input.
//
// That is not minimalism for its own sake - it is what the legacy control it
// replaces actually did, and it is verifiable. `Website/controls/` ships a
// resource file for Address, DualListControl, Help, ModuleAuditControl,
// SkinControl, TextEditor, URLControl, UrlTrackingControl and User, and there is
// NO `LabelControl.ascx.resx` among them: the legacy label control owned zero
// resources, and every string it displayed came from the page consuming it. The
// same is true here.
//
// ---------------------------------------------------------------------------
// LEGACY PROVENANCE - `Website/controls/labelcontrol.ascx`, all 11 content lines
// ---------------------------------------------------------------------------
//   L2  <label id=label runat="server">
//   L3    <asp:linkbutton id=cmdHelp tabindex="-1" CausesValidation="False" …>
//   L4      <asp:image id="imgHelp" tabindex="-1" imageurl="~/images/help.gif" …>
//   L5    </asp:linkbutton>
//   L6    <asp:label id=lblLabel …></asp:label>
//   L7  </label>
//   L8  <br />
//   L9  <asp:panel id=pnlHelp cssClass="Help" …>
//   L10   <asp:label id=lblHelp …></asp:label>
//   L11 </asp:panel>
//
// Five structural facts are carried across. A REAL `<label>` gives native
// association, so the legacy `ControlName` attribute becomes `for` here. The help
// affordance TOGGLES A BLOCK that sits below the label in normal flow - the
// literal `<br />` on L8 is the proof, and `default.css:L424-L439` styles
// `.Help` as a bordered, background-filled box rather than a tool tip, so it is a
// disclosure and never a pop-up. `CausesValidation="False"` on L3 means the
// affordance could not submit, which is exactly `<button type="button">`.
// `enableviewstate="False"` on all four server controls means none of this state
// was ever round-tripped, and here it is held in signals instead. And the help
// panel started collapsed, which is why {@link FormFieldComponent} starts
// collapsed too.
//
// ---------------------------------------------------------------------------
// EVERY STRING THIS COMPONENT RENDERS IS PLAIN TEXT. THAT IS A SECURITY BOUNDARY.
// ---------------------------------------------------------------------------
// Measured, not assumed. Across the 37 in-scope `App_LocalResources` resource
// files there are 1111 entries, of which 76 carry an HTML tag - `br` 39, `p` 24,
// `h1` 21, `b` 10, `a` 5, `li` 3, `span` 2, `ul` 2, `script` 1, `h3` 1, `h4` 1,
// `strong` 1 - and the single `script` is a live remote advertising block in the
// `Advertising.Text` entry of
// `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx`. The tags are
// stored XML-escaped, so a naive search finds nothing and would wrongly clear the
// risk.
//
// Consequently this component exposes no pre-escaped member, no trusted-markup
// member, and no member whose name hints at either. The paired template
// interpolates text and the framework escapes it. The legacy application reached
// the same conclusion by hand: `Website/admin/Security/AccessDenied.ascx.vb:L43`
// renders its untrusted query-string message through
// `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))` before display.
//
// MIGRATION: rendering as plain text creates one problem that must be solved
// here rather than left to the caller. The legacy wording carries break markup
// INSIDE the message: `EditRoles.ascx.resx` holds
// `valRoleName.Text = '<br>You Must Enter a Valid Name'`, and all nine validator
// entries in that one file open the same way; 21 of the 33 `ErrorMessage`
// attributes across the in-scope markup do too. Interpolated verbatim, a person
// would literally see `<br>You Must Enter a Valid Name`.
//
// Every string this component displays is therefore passed through
// `stripLegacyBreakTags` from `core/utils/form-errors.util`, which is the
// workspace's SINGLE break-tag normaliser. It is imported rather than reimplemented
// here on purpose: the cleaning rule is one rule, and a second local copy of it
// would be free to drift from the canonical one - the same two spellings would
// then be handled two ways depending on which layer noticed the message first.
//

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Input,
  computed,
  inject,
  signal,
  type AfterContentChecked,
  type Signal,
} from '@angular/core';

import { stripLegacyBreakTags } from '../../../core/utils/form-errors.util';

// ---------------------------------------------------------------------------
// THE ONLY STRINGS THIS COMPONENT AUTHORS
// ---------------------------------------------------------------------------
// Three, and each is an accessibility affordance rather than content. They are
// named constants rather than template literals so that a specification can
// assert the exact characters, and so that the template compiler's collapsing of
// whitespace runs in template text cannot silently alter them.

/**
 * The visible required marker.
 *
 * MIGRATION: a NET ADDITION, stated plainly because it would be easy to present
 * it as a translation and it is not one. The legacy markup expresses requiredness
 * only through `asp:RequiredFieldValidator` - 16 declarations across the in-scope
 * screens - which rendered nothing at all until a postback failed. No visual
 * required-marker convention was found in the legacy label markup to carry
 * across, so this marker is new, and it is paired with {@link REQUIRED_TEXT} so
 * that it is never colour alone that carries the meaning.
 */
const REQUIRED_MARKER = '*';

/**
 * The visually-hidden word that gives {@link REQUIRED_MARKER} a meaning for a
 * reader who cannot see it.
 *
 * A marker glyph in the error colour is perceivable by sighted readers and
 * inaudible to everyone else. Pairing the two is what makes requiredness
 * available through more than one sense.
 */
const REQUIRED_TEXT = 'required';

/**
 * The accessible name of the help disclosure button.
 *
 * Deliberately not "Show help" or "Hide help": the button's state is carried by
 * `aria-expanded`, and a name that changed with the state would announce the
 * state twice and in two vocabularies. The name is composed with the field's own
 * label through {@link FormFieldComponent.helpToggleLabelledBy}, so a screen
 * reader hears "Help, Role Name" rather than the twentieth undifferentiated
 * "Help" on a long form.
 */
const HELP_TOGGLE_TEXT = 'Help';

/**
 * The single empty message list, frozen and shared.
 *
 * One instance rather than a fresh `[]` per normalisation, so the "no messages"
 * state is reference-stable and a signal holding it does not notify its readers
 * when one absent value replaces another.
 */
const EMPTY_MESSAGES: readonly string[] = Object.freeze([]);

/**
 * One trailing ASCII colon, with any whitespace immediately before it.
 *
 * Unanchored at the front and without the global flag, so it removes exactly ONE
 * colon from the end and nothing else anywhere.
 */
const TRAILING_COLON = /\s*:$/;

/** The stem of every generated identifier, matching the component's selector. */
const ID_PREFIX = 'app-form-field';

/**
 * Native and ARIA controls that can be projected into the field.
 *
 * The query is deliberately scoped to the projection wrapper before it is used,
 * so it never reaches the help disclosure button that this component owns.
 */
const PROJECTED_CONTROL_SELECTOR = [
  'a[href]',
  'button',
  'input:not([type="hidden"])',
  'select',
  'textarea',
  '[contenteditable="true"]',
  '[role="button"]',
  '[role="checkbox"]',
  '[role="combobox"]',
  '[role="listbox"]',
  '[role="radio"]',
  '[role="slider"]',
  '[role="spinbutton"]',
  '[role="switch"]',
  '[role="textbox"]',
].join(',');

/** A labelable element's browser-provided list of associated labels. */
type LabelableElement = HTMLElement & {
  readonly labels?: NodeListOf<HTMLLabelElement> | null;
};

/**
 * Instances created so far, used only to make an identifier unique.
 *
 * Module-scoped and monotonic. A component cannot derive a unique identifier from
 * its inputs, because two fields may legitimately carry the same label and 5 of
 * the 186 legacy labels carry no control association at all, so a counter is the
 * only source that cannot collide. It is never rendered as content and never
 * asserted against by number - the identifiers exist to be referenced, not read.
 */
let instanceCount = 0;

/** Returns an identifier stem that no other instance of this component holds. */
function nextFormFieldId(): string {
  instanceCount += 1;

  return `${ID_PREFIX}-${instanceCount}`;
}

/** Whether the supplied text contains an accessible-name token. */
function hasText(value: string | null | undefined): boolean {
  return typeof value === 'string' && value.trim().length > 0;
}

/**
 * Whether a projected control already owns an accessible name.
 *
 * Explicit ARIA naming and native `<label>` association take precedence over the
 * field-level fallback. Buttons, links and custom role-based controls may also be
 * named by their own visible text; inputs whose accessible name comes from a
 * value or alternative text are handled explicitly.
 */
function hasOwnAccessibleName(control: HTMLElement): boolean {
  if (
    hasText(control.getAttribute('aria-label')) ||
    hasText(control.getAttribute('aria-labelledby')) ||
    hasText(control.getAttribute('title'))
  ) {
    return true;
  }

  const labels = (control as LabelableElement).labels;

  if (
    labels !== undefined &&
    labels !== null &&
    Array.from(labels).some((label) => hasText(label.textContent))
  ) {
    return true;
  }

  if (control instanceof HTMLInputElement) {
    const inputType = control.type.toLowerCase();

    if (inputType === 'image') {
      return hasText(control.alt);
    }

    if (inputType === 'button' || inputType === 'reset' || inputType === 'submit') {
      return hasText(control.value);
    }

    return false;
  }

  if (
    control instanceof HTMLSelectElement ||
    control instanceof HTMLTextAreaElement ||
    control.isContentEditable
  ) {
    return false;
  }

  const role = control.getAttribute('role');
  const canNameFromContents =
    control instanceof HTMLAnchorElement ||
    control instanceof HTMLButtonElement ||
    role === 'button' ||
    role === 'checkbox' ||
    role === 'radio' ||
    role === 'switch';

  return canNameFromContents && hasText(control.textContent);
}

/**
 * Narrows an input value to a string, treating anything else as absent.
 *
 * Every input here is typed, so this cannot fire from a type-checked template.
 * It exists for the two paths that bypass the type system: a value that arrived
 * as JSON and was typed optimistically, and a consumer compiled against a
 * different declaration. Returning the empty string keeps the component
 * rendering rather than throwing inside a template expression, where the failure
 * would take out the whole view.
 */
function asText(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/**
 * Resolves a label as it will be displayed.
 *
 * MIGRATION: the legacy screens punctuated their labels SIX different ways, and
 * all six collapse to one here. Measured across all 186 `<dnn:label>` instances
 * in the 39 in-scope screens: `Suffix=":"` 40 times, the `suffix` attribute
 * ABSENT 133 times - 72%, the dominant case, and no punctuation at all -
 * `Suffix=""` explicitly empty 12 times, and `suffix="?"` once. Two further
 * outcomes are not in the attribute at all: punctuation baked into the resource
 * value (`plRSVPCode.Text` = 'RSVP Code:', `plRSVPLink.Text` = 'RSVP Link:',
 * `PublicRole.Text` = 'Public Role?', `AutoAssignment.Text` = 'Auto Assignment?')
 * and punctuation baked into inline markup (`editroles.ascx:L166` declares
 * `plIcon` with `Text="Icon:"`).
 *
 * The policy is that this component owns the punctuation consistently: it strips
 * ONE trailing colon, whatever the colon's origin, and adds none of its own. That
 * matches the 72% legacy majority, which is also the closest match to the legacy
 * appearance across the screens as a whole.
 *
 * A question mark is PRESERVED - it is meaning rather than decoration, and it
 * arrives through both routes ('Public Role?' in a resource value, `suffix="?"`
 * in markup). Parentheses are PRESERVED for the same reason:
 * `BillingPeriod.Text` = 'Billing Period (Every)' reads as an instruction, and
 * the resource entry beside it says why - 'These two fields are used in
 * conjunction to enter a Billing Period'. Nothing but a single trailing colon is
 * touched.
 */
function normaliseLabelText(value: unknown): string {
  return stripLegacyBreakTags(asText(value)).replace(TRAILING_COLON, '');
}

/**
 * Resolves the supplied error into the list of messages to display.
 *
 * The rules are total, so every input has a defined outcome: absent yields no
 * messages; a string yields one message, or none when it is blank once cleaned;
 * a list yields its non-blank entries in the order given. A blank entry is
 * DROPPED rather than kept, because an empty message is not a message - it would
 * render as an empty error region that occupies space, sits in the accessibility
 * tree and announces nothing.
 *
 * Order is preserved and duplicates are NOT removed. That is deliberate on both
 * counts: the API that produces these messages documents that it does not
 * reorder or deduplicate them, and duplicates are genuinely reachable -
 * `Website/admin/Portal/Signup.ascx.vb:L193` appends the identical fragment once
 * per invalid character in a single submission. A template must therefore track
 * these messages BY INDEX; tracking by value would raise a duplicate-key error
 * the moment two identical messages arrive.
 */
function normaliseMessages(
  value: string | readonly string[] | null | undefined,
): readonly string[] {
  if (value === null || value === undefined) {
    return EMPTY_MESSAGES;
  }

  if (typeof value === 'string') {
    const single = stripLegacyBreakTags(value);

    return single.length === 0 ? EMPTY_MESSAGES : Object.freeze([single]);
  }

  const cleaned: string[] = [];

  for (const entry of value) {
    const message = stripLegacyBreakTags(asText(entry));

    if (message.length > 0) {
      cleaned.push(message);
    }
  }

  return cleaned.length === 0 ? EMPTY_MESSAGES : Object.freeze(cleaned);
}

/**
 * Wraps one field of an administration form: its label, its optional help
 * disclosure, the control the caller projects, and the validation messages that
 * belong to it.
 *
 * This is the most reused member of the shared set. The legacy control it
 * replaces, `Website/controls/labelcontrol.ascx`, is instantiated 186 times
 * across the 39 in-scope administration screens - Portal 67, Users 36, Security
 * 24, Modules 25, Tabs 34 - so every decision here is multiplied 186 times, which
 * is why each one below is measured rather than assumed.
 *
 * WHAT THE CALLER OWNS. The caller keeps three responsibilities that cannot be
 * delegated. It puts the same `id` on the primary control that it passes to
 * {@link for}. It sets each control's own `required` or `aria-required`, because
 * {@link required} here is a presentational marker and cannot make a field
 * required. And it decides WHEN a message exists: every
 * validator in scope declares `Display="Dynamic"` - 35 occurrences, and not one
 * declaring anything else - which means a legacy message appeared only once the
 * field had been submitted and found invalid. The equivalent lives in the
 * caller's template, as `&#64;if (control.touched && control.invalid)`, and the
 * resolved wording is then passed down through {@link error}.
 *
 * MULTI-CONTROL FIELDS ARE NORMAL, NOT EXCEPTIONAL. The slot is a plain,
 * unconstrained projection because the legacy screens routinely put several
 * controls under one label. `editroles.ascx:L98-L115` puts a text box, a literal
 * `&nbsp;&nbsp;`, a drop-down list and two validators under the single
 * `plBillingPeriod` label, and its own resource entry explains the intent -
 * 'These two fields are used in conjunction to enter a Billing Period';
 * `L130-L147` repeats the shape for the trial period. `roles.ascx:L5-L16` puts a
 * drop-down list, an edit link and a delete image button under one label, and
 * `editroles.ascx:L28` adds a hidden read-only twin beside a text box. Nothing
 * here wraps, counts or reorders the projected children.
 *
 * MIGRATION: the second control of such a field was UNLABELLED in the legacy
 * markup, because `ControlName` names exactly one control and the label's `for`
 * can only point at one element. A named `role="group"` provides useful composite
 * context but does NOT give its descendants accessible names. After projected
 * content is checked, this component therefore places the visible label's id
 * directly in `aria-labelledby` on every otherwise unnamed projected control.
 * A caller-supplied native label, `aria-label` or `aria-labelledby` always wins,
 * so composite fields may provide more specific names such as "Frequency". The
 * fallback is invisible: it changes no layout, no colour and no spacing.
 *
 * MIGRATION: the legacy layout hacks are not reproduced. The `<br />` on
 * `labelcontrol.ascx:L8` and the 70 literal `&nbsp;` sequences in the in-scope
 * markup were spacing expressed as content; spacing here comes from the
 * stylesheet's token-based `gap`, and this component emits no `<br>` and no
 * non-breaking space anywhere.
 *
 * MIGRATION: rich-text editing is out of scope, so a field that legacy rendered
 * through the excluded editor provider is a plain `textarea` projected into this
 * component. That is a documented functional reduction, not an oversight, and it
 * affects the 19 in-scope `TextMode="MultiLine"` declarations. Nothing in this
 * component varies by control type; the stylesheet carries the checkbox
 * arrangement, where the legacy screens placed the label beside the control
 * rather than above it - 28 `asp:CheckBox` declarations, only 5 of which carry
 * their own `Text`, so 23 depend entirely on the adjacent label.
 *
 * PERMISSION-GATED FIELDS. The shared `*appHasPermission` directive must be
 * applied to the `<app-form-field>` ELEMENT, never to the control inside it. The
 * directive removes its subject from the DOM; applied to the inner control it
 * would leave this component rendering a label whose `for` points at an element
 * that no longer exists, which is precisely the dangling reference the `for`
 * handling here works to avoid. Applied to the element, the whole labelled unit -
 * label, help, control and messages - leaves together. That directive is not
 * imported here and must not be: it belongs to the feature templates that decide
 * what to gate.
 *
 * @example
 * ```html
 * <app-form-field label="Role Name" for="role-name" [required]="true"
 *                 help="Enter the name of the role."
 *                 [error]="nameMessages()">
 *   <input id="role-name" type="text" [formControl]="form.controls.name" />
 * </app-form-field>
 * ```
 */
@Component({
  selector: 'app-form-field',
  standalone: true,
  imports: [],
  templateUrl: './form-field.component.html',
  styleUrl: './form-field.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormFieldComponent implements AfterContentChecked {
  // -------------------------------------------------------------------------
  // STATE
  // -------------------------------------------------------------------------
  // Signals rather than plain fields, for one concrete reason: every value the
  // template shows is DERIVED - a label with its punctuation resolved, help text
  // with its break markup resolved, a message list filtered of blanks - and a
  // derivation over a plain field cannot know when to recompute. Holding the raw
  // values in signals lets each derived view be a `computed`, which recomputes
  // exactly when its own source changes and never otherwise. Declared before the
  // derivations that read them, because class field initialisers run in order.

  /** The label exactly as supplied, before punctuation is resolved. */
  private readonly labelValue = signal('');

  /** The projected control's `id` exactly as supplied, trimmed. */
  private readonly controlIdValue = signal('');

  /** The help text exactly as supplied, before break markup is resolved. */
  private readonly helpValue = signal('');

  /** The normalised messages. Normalisation happens once, in the setter. */
  private readonly messages = signal<readonly string[]>(EMPTY_MESSAGES);

  /** The component host, used only to wire accessible names onto projected controls. */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Controls whose `aria-labelledby` value this component owns.
   *
   * A weak map neither retains controls removed by conditional projection nor
   * exposes an implementation marker in the DOM. The stored value lets the
   * component distinguish its own fallback from a consumer that subsequently
   * supplies a more specific name.
   */
  private readonly automaticallyLabelledControls = new WeakMap<HTMLElement, string>();

  /**
   * Whether the help disclosure is open.
   *
   * Starts closed, matching the legacy panel, which was hidden until its help
   * affordance was activated. The legacy control declared
   * `enableviewstate="False"` on every part of itself, so this state was never
   * round-tripped to the server there either - it is client state in both
   * designs, and here it is simply held in the client.
   */
  private readonly helpOpen = signal(false);

  /**
   * The identifier stem used when {@link for} is empty.
   *
   * Allocated once per instance at construction rather than lazily, so an
   * identifier never changes underneath a reference that has already been
   * rendered into an `aria-describedby`.
   */
  private readonly fallbackId = nextFormFieldId();

  // -------------------------------------------------------------------------
  // INPUTS - the fixed public surface: label, for, required, error, help
  // -------------------------------------------------------------------------

  /**
   * The field's visible label.
   *
   * Supplied as plain text by the caller, which is where wording lives: this
   * component holds none.
   *
   * MIGRATION: where wording is carried over from a legacy screen it must be taken
   * from the RESOURCE VALUE and never from the markup attribute. The two disagree,
   * and the resource file is right - proven with the
   * validator's own operator as arbiter: `valBillingPeriod2` declares
   * `Operator="GreaterThan"` with a markup message reading 'Greater Than or Equal
   * to Zero' while its resource value reads 'Greater Than Zero', and
   * `valTrialFee2` declares `Operator="GreaterThanEqual"` with a markup message
   * reading 'Greater Than Zero' while its resource value reads 'Greater Than or
   * Equal to Zero'. The two wrong messages are exactly swapped with each other in
   * the markup, so the resource values are the authority for every string handed
   * to this component.
   *
   * Punctuation is normalised on display; see {@link normaliseLabelText}.
   */
  @Input()
  public set label(value: string) {
    this.labelValue.set(asText(value));
  }

  public get label(): string {
    return this.labelValue();
  }

  /**
   * The `id` of the control the caller projects, used as the label's `for`.
   *
   * MAY BE EMPTY, and that is a measured requirement rather than a courtesy: 5 of
   * the 186 legacy labels declare no `controlname` at all. An empty value renders
   * NO `for` attribute, because `for=""` is a dangling reference that names
   * nothing and reports as an error to any auditing tool. When it is empty,
   * unnamed projected controls are still associated directly through the
   * field-label fallback applied by {@link ensureProjectedControlsAreNamed}, while
   * callers remain free to provide a more specific accessible name.
   *
   * Trimmed on the way in, because a value with surrounding whitespace cannot
   * match an element's `id` and would produce exactly the dangling reference this
   * avoids.
   */
  @Input()
  public set for(value: string) {
    this.controlIdValue.set(asText(value).trim());
  }

  public get for(): string {
    return this.controlIdValue();
  }

  /**
   * Whether to show the required marker.
   *
   * PRESENTATIONAL ONLY. It cannot make a field required and must not be read as
   * doing so: the authoritative state belongs to the projected control and to the
   * form's validators, and the caller sets it there as well. Two expressions of
   * one truth is the risk, and it is accepted knowingly, because the marker has
   * to be rendered beside the label - outside the control, where the control
   * cannot reach.
   */
  @Input() public required = false;

  /**
   * The validation messages for this field: one message, several, or none.
   *
   * MIGRATION: the shape is WIDENED from a single string, and the widening is
   * forced by measurement rather than chosen for convenience.
   * `Website/admin/Security/editroles.ascx` puts TWO `CompareValidator`s on one
   * control four separate times - ServiceFee at L89-L96 (`DataTypeCheck` then
   * `GreaterThanEqual` 0), BillingPeriod at L104-L114 (`DataTypeCheck` then
   * `GreaterThan` 0), TrialFee at L122-L128 and TrialPeriod at L136-L146 - so two
   * messages can be outstanding on one field simultaneously, and a single string
   * cannot represent them. The in-scope validator census makes it ordinary rather
   * than exceptional: 16 `RequiredFieldValidator`, 19 `CompareValidator`, 3
   * `RegularExpressionValidator`, 1 `CustomValidator`.
   *
   * The second measurement is what makes the widening mandatory. There are ZERO
   * `<asp:ValidationSummary>` declarations in the 39 in-scope screens: legacy
   * validation feedback was exclusively per-field and inline, so this region is
   * the ONLY error surface the ported administration UI has. A message dropped
   * here is a message the person never sees.
   *
   * The widening is backward compatible - a bare string is still accepted - so a
   * caller may pass either the single resolved message or the full list from the
   * shared error utility. Blank entries are dropped and order is preserved; see
   * {@link normaliseMessages}, including why a template must track by index.
   */
  @Input()
  public set error(value: string | readonly string[] | null | undefined) {
    this.messages.set(normaliseMessages(value));
  }

  /**
   * The messages as this component resolved them: cleaned, blank-free, frozen.
   *
   * The read type is narrower than the write type on purpose. A caller writes
   * whatever it has; reading back returns the one canonical shape, so nothing
   * downstream has to repeat the normalisation or guess which form it received.
   */
  public get error(): readonly string[] {
    return this.messages();
  }

  /**
   * Supporting text for the field, revealed by the help disclosure.
   *
   * Load-bearing rather than decorative: the `.Help` resource suffix occurs 274
   * times across the in-scope resource files, second only to the 692 `.Text`
   * label entries, so most legacy fields had help text and the affordance that
   * revealed it is part of the field, not an extra.
   */
  @Input()
  public set help(value: string) {
    this.helpValue.set(asText(value));
  }

  public get help(): string {
    return this.helpValue();
  }

  // -------------------------------------------------------------------------
  // IDENTIFIERS
  // -------------------------------------------------------------------------
  // Referenced, never displayed. Each is derived from {@link for} when the caller
  // supplied one, so the identifiers a caller sees are predictable from the value
  // it already holds, and from the per-instance counter otherwise, so a field with
  // no control association still has stable, collision-free references.

  /** `for` when it is non-empty, otherwise this instance's unique stem. */
  private readonly idBase: Signal<string> = computed(() => {
    const controlId = this.controlIdValue();

    return controlId.length > 0 ? controlId : this.fallbackId;
  });

  /**
   * The `id` of the rendered label element.
   *
   * Public so a caller that wants to name something of its own from this field -
   * a fieldset, a composite control, a second group - can point at the same text
   * rather than duplicating the wording.
   */
  public readonly labelId: Signal<string> = computed(() => `${this.idBase()}-label`);

  /**
   * The `id` of the help region.
   *
   * Public for the caller that wants finer-grained wiring than the group provides:
   * putting `[attr.aria-describedby]` on ONE projected control rather than
   * describing the whole field. Both routes are supported and they compose - this
   * component already describes the group, and a control that describes itself as
   * well is announced consistently either way.
   *
   * Referencing it is only correct while the region exists. It exists when there
   * is help text AND the disclosure is open, which the caller can mirror; the
   * group's own reference is managed by {@link describedBy}, which never emits a
   * reference to a region that is not rendered.
   */
  public readonly helpId: Signal<string> = computed(() => `${this.idBase()}-help`);

  /**
   * The `id` of the error region.
   *
   * Public for the same reason as {@link helpId}, and subject to the same rule: it
   * is a valid reference only while messages exist.
   */
  public readonly errorId: Signal<string> = computed(() => `${this.idBase()}-error`);

  /**
   * The `id` of the help button's own text, used to compose the button's name.
   *
   * Internal to the template: a caller has no use for it, so it is not part of the
   * public surface.
   */
  protected readonly helpToggleTextId: Signal<string> = computed(
    () => `${this.idBase()}-help-toggle-text`,
  );

  // -------------------------------------------------------------------------
  // DERIVED VIEWS
  // -------------------------------------------------------------------------

  /** The label as displayed, with its punctuation normalised. */
  protected readonly labelText: Signal<string> = computed(() =>
    normaliseLabelText(this.labelValue()),
  );

  /**
   * Whether there is label text to name anything with.
   *
   * Guards the naming references rather than the label element itself. The element
   * is always rendered, exactly as `labelcontrol.ascx:L2` rendered it
   * unconditionally, so the field's shape does not change when a caller supplies
   * no label; but an empty element is not a name, so nothing is pointed at it.
   */
  protected readonly hasLabel: Signal<boolean> = computed(() => this.labelText().length > 0);

  /**
   * The value for the label's `for` attribute, or null to omit it entirely.
   *
   * Null rather than the empty string, because a null attribute binding removes
   * the attribute while an empty one renders `for=""` - a reference that resolves
   * to nothing. This is the mechanism that makes the 5 legacy labels with no
   * control association safe to port unchanged.
   */
  protected readonly labelFor: Signal<string | null> = computed(() => {
    const controlId = this.controlIdValue();

    return controlId.length > 0 ? controlId : null;
  });

  /**
   * The help text as displayed, with break markup resolved.
   *
   * A resolved interior break becomes a newline character, which HTML collapses to
   * a space unless the stylesheet preserves it. That is the stylesheet's decision
   * and is left to it deliberately: this component's obligation is that no reader
   * ever sees the characters `<br>`.
   */
  protected readonly helpText: Signal<string> = computed(() =>
    stripLegacyBreakTags(this.helpValue()),
  );

  /** Whether help text was supplied and survived cleaning. */
  protected readonly hasHelp: Signal<boolean> = computed(() => this.helpText().length > 0);

  /**
   * The messages to render, in the order supplied.
   *
   * Exposed read-only so the template can render the list while nothing outside
   * the input setter can write it.
   */
  protected readonly errorMessages: Signal<readonly string[]> = this.messages.asReadonly();

  /** Whether there is at least one message to render. */
  protected readonly hasError: Signal<boolean> = computed(() => this.messages().length > 0);

  /** Whether the person has opened the help disclosure. */
  protected readonly helpVisible: Signal<boolean> = this.helpOpen.asReadonly();

  /**
   * Whether the help region is actually rendered.
   *
   * Both conditions matter. Help that is open but empty must render nothing, which
   * is the case a caller creates by clearing `help` while the disclosure is open,
   * and it is this signal - not {@link helpVisible} - that the region and the
   * button's `aria-expanded` follow, so the announced state can never contradict
   * what is on screen.
   */
  protected readonly helpExpanded: Signal<boolean> = computed(
    () => this.hasHelp() && this.helpVisible(),
  );

  /**
   * The region the help button controls, or null while no region is rendered.
   *
   * Null rather than the region's identifier when the disclosure is closed,
   * because the region is removed from the document rather than hidden inside it,
   * and a control reference that resolves to nothing is worse than no reference at
   * all. The button's `aria-expanded` carries the state either way, and the
   * disclosure pattern treats the control reference as optional when the revealed
   * content immediately follows - which it does here, exactly as the legacy panel
   * followed its own affordance in normal flow.
   */
  protected readonly helpControls: Signal<string | null> = computed(() =>
    this.helpExpanded() ? this.helpId() : null,
  );

  /**
   * The label reference that names the projection group, or null when there is no
   * label to name it with.
   *
   * This names the composite group only. ARIA naming is not inherited by
   * descendants, so {@link ensureProjectedControlsAreNamed} separately gives each
   * otherwise unnamed projected control a direct reference to the same visible
   * label.
   */
  protected readonly groupLabelledBy: Signal<string | null> = computed(() =>
    this.hasLabel() ? this.labelId() : null,
  );

  /**
   * The regions that describe this field, or null when there are none.
   *
   * Only regions that are RENDERED are referenced - a description pointing at an
   * absent element is silently dropped by some assistive technology and read as an
   * empty description by others, so a dangling reference is never emitted. The
   * order is document order, help then error, so the description is read in the
   * same sequence it is seen.
   */
  protected readonly describedBy: Signal<string | null> = computed(() => {
    const references: string[] = [];

    if (this.helpExpanded()) {
      references.push(this.helpId());
    }

    if (this.hasError()) {
      references.push(this.errorId());
    }

    return references.length > 0 ? references.join(' ') : null;
  });

  /**
   * The name of the help button, composed from its own text and the field's label.
   *
   * Composed from existing text rather than authored as a sentence, so no wording
   * is invented and nothing needs translating. With 186 fields in scope, a page
   * can hold twenty help buttons; named only "Help" they are indistinguishable
   * when listed by a screen reader, and named "Help, Role Name" they are not.
   *
   * One consequence is accepted rather than hidden: on a required field the
   * label's visually-hidden word joins the composed name, so the button resolves
   * to "Help Role Name required" - verified in a browser rather than assumed. The
   * alternative is to duplicate the label text into a second hidden element, which
   * would put the same wording in two places and let them drift; a slightly
   * verbose name built from text already on the page is the better trade.
   */
  protected readonly helpToggleLabelledBy: Signal<string> = computed(() => {
    const own = this.helpToggleTextId();

    return this.hasLabel() ? `${own} ${this.labelId()}` : own;
  });

  // -------------------------------------------------------------------------
  // TEMPLATE CONSTANTS
  // -------------------------------------------------------------------------
  // Bound rather than written into the template, because the template compiler
  // collapses runs of whitespace in template text and a single character sitting
  // beside an interpolation is exactly where that is visible.

  /** See {@link REQUIRED_MARKER}. */
  protected readonly requiredMarker = REQUIRED_MARKER;

  /** See {@link REQUIRED_TEXT}. */
  protected readonly requiredText = REQUIRED_TEXT;

  /** See {@link HELP_TOGGLE_TEXT}. */
  protected readonly helpToggleText = HELP_TOGGLE_TEXT;

  // -------------------------------------------------------------------------
  // BEHAVIOUR
  // -------------------------------------------------------------------------

  /**
   * Reconciles direct accessible-name references after projected content changes.
   *
   * Content may be added or removed by built-in control flow, and the field's
   * `for` input may change after initial rendering. Running after each content
   * check keeps those cases correct while remaining idempotent: a control that
   * already has its own name is untouched, and an owned fallback is written only
   * when its target changes.
   */
  public ngAfterContentChecked(): void {
    this.ensureProjectedControlsAreNamed();
  }

  /**
   * Gives each otherwise unnamed projected control a direct accessible name.
   *
   * A group label supplies context for the composite but cannot name a child
   * control. The fallback therefore writes `aria-labelledby` on the child itself.
   * Consumer-owned naming is never replaced, and a fallback is removed when the
   * field no longer has visible label text so it cannot become a dangling
   * reference.
   */
  private ensureProjectedControlsAreNamed(): void {
    const projection = this.hostElement.nativeElement.querySelector<HTMLElement>(
      '.form-field__control',
    );

    if (projection === null) {
      return;
    }

    const labelReference = this.groupLabelledBy();
    const controls = Array.from(
      projection.querySelectorAll<HTMLElement>(PROJECTED_CONTROL_SELECTOR),
    );

    for (const control of controls) {
      const ownedReference = this.automaticallyLabelledControls.get(control);

      if (ownedReference !== undefined) {
        const currentReference = control.getAttribute('aria-labelledby');

        if (currentReference !== ownedReference) {
          // The consumer changed the attribute after projection. Its explicit
          // decision owns the control from this point unless the name is removed.
          this.automaticallyLabelledControls.delete(control);
        } else if (labelReference === null) {
          control.removeAttribute('aria-labelledby');
          this.automaticallyLabelledControls.delete(control);
          continue;
        } else {
          if (ownedReference !== labelReference) {
            control.setAttribute('aria-labelledby', labelReference);
            this.automaticallyLabelledControls.set(control, labelReference);
          }

          continue;
        }
      }

      if (hasOwnAccessibleName(control) || labelReference === null) {
        continue;
      }

      control.setAttribute('aria-labelledby', labelReference);
      this.automaticallyLabelledControls.set(control, labelReference);
    }
  }

  /**
   * Opens the help disclosure when it is closed and closes it when it is open.
   *
   * MIGRATION: the affordance that calls this is a keyboard-reachable
   * `<button type="button">`, which reverses a legacy accessibility defect rather
   * than reproducing it. `labelcontrol.ascx` declares `tabindex="-1"` TWICE - on
   * the link button at L3 and again on the image inside it at L4 - so the legacy
   * help affordance was operable by pointer only and unreachable by keyboard. The
   * same defect is in `helpbuttoncontrol.ascx` and in
   * `Website/controls/sectionheadcontrol.ascx`. Reversing it costs nothing
   * visually, which is why the migration plan's own precedence order permits it:
   * accessibility outranks visual continuity, and here the two do not even
   * conflict.
   *
   * MIGRATION: the affordance is a SIBLING of the label rather than a child of it.
   * The legacy control nested it inside the `<label>` at L3-L5, which is both
   * invalid - a button may not be a label's descendant unless it is the labelled
   * control - and behaviourally ambiguous, because a click anywhere in a label is
   * forwarded to the control it names. The label-less arrangement is not invented
   * to fix that: the legacy codebase already ships it, in
   * `Website/controls/helpbuttoncontrol.ascx`, which is `labelcontrol.ascx`
   * L3-L11 with the `<label>` wrapper removed.
   *
   * MIGRATION: `~/images/help.gif` is not carried across. It is a 344-byte raster
   * and the only static binary in scope is the favicon, so the affordance is
   * expressed with text, an inline vector and the stylesheet instead. Nothing is
   * lost: the legacy image carried no alternative text, so it named nothing to
   * begin with.
   *
   * The affordance precedes the projected control in both document order and tab
   * order, because it is rendered beside the label and the label sits above or
   * before the control. That is deliberate: focus order follows the order things
   * are seen in, which is the only order that carries the field's meaning.
   * Measured in a browser, the sequence within one field is the help button, then
   * the control - never the control alone, which is what the legacy
   * `tabindex="-1"` produced.
   *
   * `update` rather than a read followed by a write, so the flip is one atomic
   * operation against the signal and cannot interleave with another writer.
   */
  protected toggleHelp(): void {
    this.helpOpen.update((open) => open === false);
  }
}
