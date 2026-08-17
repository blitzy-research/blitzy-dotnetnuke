// WHAT THIS COMPONENT IS, AND WHAT IT DELIBERATELY IS NOT

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
import { NgTemplateOutlet } from '@angular/common';

import { stripLegacyBreakTags } from '../../../core/utils/form-errors.util';

// THE ONLY STRINGS THIS COMPONENT AUTHORS
// Three, and each is an accessibility affordance rather than content. They are named constants rather than
// template literals so that a specification can assert the exact characters, and so that the template
// compiler's collapsing of whitespace runs in template text cannot silently alter them.

/**
 * The visible required marker. MIGRATION: a NET ADDITION, stated plainly because it would be easy to
 * present it as a translation and it is not one.
 */
/**
 * The opening of the sentence stating a field's typing bound, and the close that follows the number. Split
 * around the number rather than written as a template so the number is never embedded in a translated
 * string, and authored ONCE so every bounded field in the application states its bound the same way.
 */
const LIMIT_SENTENCE_LEAD = 'At most';

/** @see LIMIT_SENTENCE_LEAD */
const LIMIT_SENTENCE_TAIL = 'characters.';

const REQUIRED_MARKER = '*';

/** The visually-hidden word that gives {@link REQUIRED_MARKER} a meaning for a reader who cannot see it. */
const REQUIRED_TEXT = 'required';

/**
 * The accessible name of the help disclosure button. Deliberately not "Show help" or "Hide help": the
 * button's state is carried by `aria-expanded`, and a name that changed with the state would announce the
 * state twice and in two vocabularies.
 */
const HELP_TOGGLE_TEXT = 'Help';

/** The single empty message list, frozen and shared. */
const EMPTY_MESSAGES: readonly string[] = Object.freeze([]);

/**
 * One trailing ASCII colon, with any whitespace immediately before it. Unanchored at the front and
 * without the global flag, so it removes exactly ONE colon from the end and nothing else anywhere.
 */
const TRAILING_COLON = /\s*:$/;

/** The stem of every generated identifier, matching the component's selector. */
const ID_PREFIX = 'app-form-field';

/**
 * Native and ARIA controls that can be projected into the field. The query is deliberately scoped to the
 * projection wrapper before it is used, so it never reaches the help disclosure button that this
 * component owns.
 */
/**
 * The projected controls that may carry this field's DESCRIPTION. Narrower than {@link
 * PROJECTED_CONTROL_SELECTOR} on purpose, and the difference is the point. The naming pass has to reach
 * every interactive thing a caller projects, including the edit and delete affordances a legacy composite
 * field placed beside its control, because an unnamed command is a defect wherever it sits.
 */
const DESCRIBABLE_CONTROL_SELECTOR = [
  'input:not([type="hidden"],[type="button"],[type="submit"],[type="reset"],[type="image"])',
  'select',
  'textarea',
  '[contenteditable="true"]',
  '[role="checkbox"]',
  '[role="combobox"]',
  '[role="listbox"]',
  '[role="radio"]',
  '[role="slider"]',
  '[role="spinbutton"]',
  '[role="switch"]',
  '[role="textbox"]',
].join(',');

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

/** The value `aria-invalid` takes while a field is reporting a failure. */
const INVALID_STATE = 'true';

/**
 * Splits a space-separated reference list into its individual identifiers. ARIA reference lists are
 * whitespace-separated with no ordering or uniqueness guarantee, and a consumer may legitimately write
 * one with any run of spaces or a leading or trailing space.
 *
 * @param value The attribute value, or null when the attribute is absent.
 * @returns The identifiers it names, in order, with no empty entries.
 */
function referenceTokens(value: string | null): readonly string[] {
  if (value === null) {
    return [];
  }

  return value.split(/\s+/).filter((token) => token.length > 0);
}

/** A labelable element's browser-provided list of associated labels. */
type LabelableElement = HTMLElement & {
  readonly labels?: NodeListOf<HTMLLabelElement> | null;
};

/** Instances created so far, used only to make an identifier unique. Module-scoped and monotonic. */
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

/** Narrows an input value to a string, treating anything else as absent. */
function asText(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/**
 * Resolves a label as it will be displayed. the legacy screens punctuated their labels SIX different
 * ways, and all six collapse to one here.
 */
function normaliseLabelText(value: unknown): string {
  return stripLegacyBreakTags(asText(value)).replace(TRAILING_COLON, '');
}

/**
 * Resolves the supplied error into the list of messages to display. The rules are total, so every input
 * has a defined outcome: absent yields no messages; a string yields one message, or none when it is blank
 * once cleaned; a list yields its non-blank entries in the order given.
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

@Component({
  selector: 'app-form-field',
  standalone: true,
  imports: [NgTemplateOutlet],
  templateUrl: './form-field.component.html',
  styleUrl: './form-field.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The unavailable state is published as a host class so the stylesheet's existing
  // `:host(.form-field--disabled)` rule can finally apply. Nothing set this class before, which is why a
  // disabled field was visually identical to an enabled one.
  host: {
    '[class.form-field--disabled]': 'projectedControlDisabled()',
  },
})
export class FormFieldComponent implements AfterContentChecked {
  // STATE
  // Signals rather than plain fields, for one concrete reason: every value the template shows is DERIVED -
  // a label with its punctuation resolved, help text with its break markup resolved, a message list
  // filtered of blanks - and a derivation over a plain field cannot know when to recompute.

  /** The label exactly as supplied, before punctuation is resolved. */
  private readonly labelValue = signal('');

  /** The projected control's `id` exactly as supplied, trimmed. */
  private readonly controlIdValue = signal('');

  /** Whether every projected control is unavailable, read from the DOM on each content check. */
  protected readonly projectedControlDisabled = signal(false);

  /** The help text exactly as supplied, before break markup is resolved. */
  private readonly helpValue = signal('');

  /** The projected control's typing bound, or `null`. @see FormFieldComponent.limit */
  private readonly limitValue = signal<number | null>(null);

  /** The normalised messages. */
  private readonly messages = signal<readonly string[]>(EMPTY_MESSAGES);

  /** The component host, used only to wire accessible names onto projected controls. */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Controls whose `aria-labelledby` value this component owns. A weak map neither retains controls
   * removed by conditional projection nor exposes an implementation marker in the DOM. The stored value
   * lets the component distinguish its own fallback from a consumer that subsequently supplies a more
   * specific name.
   */
  private readonly automaticallyLabelledControls = new WeakMap<HTMLElement, string>();

  /**
   * Controls whose `aria-describedby` references this component contributed, and which ones. ⚠ THE STORED
   * VALUE IS WHAT MAKES THE WIRING NON-DESTRUCTIVE. A consumer is free to describe its own control - a
   * character counter, a format hint - and this component must add the help and error regions ALONGSIDE
   * that rather than over it.
   */
  private readonly contributedDescriptions = new WeakMap<HTMLElement, readonly string[]>();

  /**
   * Controls whose `aria-invalid` state this component set. A consumer that states invalidity itself owns
   * it, and its value is never overwritten or removed; membership here distinguishes the two cases
   * without writing a marker attribute into the DOM.
   */
  private readonly contributedInvalidity = new WeakSet<HTMLElement>();

  private readonly contributedErrorReferences = new WeakSet<HTMLElement>();

  private readonly helpOpen = signal(false);

  /**
   * The identifier stem used when {@link for} is empty. Allocated once per instance at construction
   * rather than lazily, so an identifier never changes underneath a reference that has already been
   * rendered into an `aria-describedby`.
   */
  private readonly fallbackId = nextFormFieldId();

  // -------------------------------------------------------------------------
  // INPUTS - the fixed public surface: label, for, required, error, help
  // -------------------------------------------------------------------------

  @Input()
  public set label(value: string) {
    this.labelValue.set(asText(value));
  }

  public get label(): string {
    return this.labelValue();
  }

  @Input()
  public set for(value: string) {
    this.controlIdValue.set(asText(value).trim());
  }

  public get for(): string {
    return this.controlIdValue();
  }

  /**
   * Whether to show the required marker. PRESENTATIONAL ONLY. It cannot make a field required and must
   * not be read as doing so: the authoritative state belongs to the projected control and to the form's
   * validators, and the caller sets it there as well.
   */
  @Input() public required = false;

  /**
   * The validation messages for this field: one message, several, or none. the shape is WIDENED from a
   * single string, and the widening is forced by measurement rather than chosen for convenience.
   */
  @Input()
  public set error(value: string | readonly string[] | null | undefined) {
    this.messages.set(normaliseMessages(value));
  }

  /** The messages as this component resolved them: cleaned, blank-free, frozen. */
  public get error(): readonly string[] {
    return this.messages();
  }

  /**
   * Supporting text for the field, revealed by the help disclosure. Load-bearing rather than decorative:
   * the `.Help` resource suffix occurs 274 times across the in-scope resource files, second only to the
   * 692 `.Text` label entries, so most legacy fields had help text and the affordance that revealed it is
   * part of the field, not an extra.
   */
  @Input()
  public set help(value: string) {
    this.helpValue.set(asText(value));
  }

  public get help(): string {
    return this.helpValue();
  }

  /**
   * The typing bound the projected control declares, or `null` when it declares none. ⚠ THIS EXISTS SO A
   * BOUND IS STATED BEFORE IT BITES, AND THE MEASURED BEHAVIOUR IT ANSWERS IS SILENT LOSS. A native
   * `maxlength` simply stops accepting keystrokes: nothing is said, nothing is marked invalid, and a reader
   * pasting a longer value keeps only its head - measured on the portal creation form, where several boxes
   * truncate without a word.
   *
   * Rendered as a permanently present, visually hidden sentence referenced by `aria-describedby`, so it is
   * announced when the box takes focus - BEFORE anything is typed - at no visual cost, and repeated inside
   * the help disclosure for a reader who opens it. It does NOT count characters as they are typed: a
   * live counter on every bounded box in the application would announce on every keystroke, and the bound
   * itself is the fact a reader needs.
   */
  @Input()
  public set limit(value: number | null | undefined) {
    const usable: boolean = typeof value === 'number' && Number.isFinite(value) && value > 0;

    this.limitValue.set(usable ? Math.floor(value as number) : null);
  }

  public get limit(): number | null {
    return this.limitValue();
  }

  // IDENTIFIERS
  // Referenced, never displayed. Each is derived from {@link for} when the caller supplied one, so the
  // identifiers a caller sees are predictable from the value it already holds, and from the per-instance
  // counter otherwise, so a field with no control association still has stable, collision-free references.

  /** `for` when it is non-empty, otherwise this instance's unique stem. */
  private readonly idBase: Signal<string> = computed(() => {
    const controlId = this.controlIdValue();

    return controlId.length > 0 ? controlId : this.fallbackId;
  });

  /** The `id` of the rendered label element. */
  public readonly labelId: Signal<string> = computed(() => `${this.idBase()}-label`);

  /**
   * The `id` of the help region. Public for the caller that wants finer-grained wiring than the group
   * provides: putting `[attr.aria-describedby]` on ONE projected control rather than describing the whole
   * field.
   */
  public readonly helpId: Signal<string> = computed(() => `${this.idBase()}-help`);

  /** The `id` of the error region. */
  public readonly errorId: Signal<string> = computed(() => `${this.idBase()}-error`);

  /** The identifier of the region stating the field's typing bound. */
  public readonly limitId: Signal<string> = computed(() => `${this.idBase()}-limit`);

  /**
   * The identifier for one message inside the error region. Derived from the region's own identifier and
   * the message's position, so it is stable for as long as the message list is, and unique across fields
   * because the base is.
   *
   * @param index The message's position in the rendered list, counted from nought.
   * @returns The identifier for that message's paragraph.
   */
  public errorMessageId(index: number): string {
    return `${this.errorId()}-${index}`;
  }

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

  /** Whether there is label text to name anything with. */
  protected readonly hasLabel: Signal<boolean> = computed(() => this.labelText().length > 0);

  /** The value for the caption's `for` attribute, or null when there is no control to point at. */
  protected readonly labelFor: Signal<string | null> = computed(() => {
    const controlId = this.controlIdValue();

    return controlId.length > 0 ? controlId : null;
  });

  /** The help text as displayed, with break markup resolved. */
  protected readonly helpText: Signal<string> = computed(() => {
    const supplied: string = stripLegacyBreakTags(this.helpValue());
    const bound: string = this.limitText();

    // The bound is appended rather than replacing anything, and the join is on a single space so the two
    // read as one paragraph. A field with a bound and no help text still gets a disclosure, which is how
    // the bound becomes visible as well as announced.
    if (supplied.length === 0) {
      return bound;
    }

    return bound.length === 0 ? supplied : `${supplied} ${bound}`;
  });

  /** The sentence stating the field's typing bound, or the empty string when there is no bound. */
  protected readonly limitText: Signal<string> = computed(() => {
    const bound: number | null = this.limitValue();

    return bound === null ? '' : `${LIMIT_SENTENCE_LEAD} ${String(bound)} ${LIMIT_SENTENCE_TAIL}`;
  });

  /** Whether a usable typing bound was supplied. */
  protected readonly hasLimit: Signal<boolean> = computed(() => this.limitValue() !== null);

  /** Whether help text was supplied and survived cleaning. */
  protected readonly hasHelp: Signal<boolean> = computed(() => this.helpText().length > 0);

  /**
   * The messages to render, in the order supplied. Exposed read-only so the template can render the list
   * while nothing outside the input setter can write it.
   */
  protected readonly errorMessages: Signal<readonly string[]> = this.messages.asReadonly();

  /** Whether there is at least one message to render. */
  protected readonly hasError: Signal<boolean> = computed(() => this.messages().length > 0);

  /** Whether the person has opened the help disclosure. */
  protected readonly helpVisible: Signal<boolean> = this.helpOpen.asReadonly();

  /** Whether the help region is actually rendered. Both conditions matter. */
  protected readonly helpExpanded: Signal<boolean> = computed(
    () => this.hasHelp() && this.helpVisible(),
  );

  /**
   * The region the help button controls, or null while no region is rendered. Null rather than the
   * region's identifier when the disclosure is closed, because the region is removed from the document
   * rather than hidden inside it, and a control reference that resolves to nothing is worse than no
   * reference at all.
   */
  protected readonly helpControls: Signal<string | null> = computed(() =>
    this.helpExpanded() ? this.helpId() : null,
  );

  /**
   * The label reference that names the projection group, or null when there is no label to name it with.
   * This names the composite group only.
   */
  protected readonly groupLabelledBy: Signal<string | null> = computed(() =>
    this.hasLabel() ? this.labelId() : null,
  );

  /**
   * The regions that describe this field, or null when there are none. Only regions that are RENDERED are
   * referenced - a description pointing at an absent element is silently dropped by some assistive
   * technology and read as an empty description by others, so a dangling reference is never emitted.
   */
  protected readonly describedBy: Signal<string | null> = computed(() => {
    const references: string[] = [];

    // Always referenced when there is a bound, because it is always rendered when there is a bound - the
    // point of it is to be heard on focus, before a keystroke is lost.
    if (this.hasLimit()) {
      references.push(this.limitId());
    }

    if (this.helpExpanded()) {
      references.push(this.helpId());
    }

    if (this.hasError()) {
      references.push(this.errorId());
    }

    return references.length > 0 ? references.join(' ') : null;
  });

  /**
   * The name of the help button, composed from its own text and the field's label. Composed from existing
   * text rather than authored as a sentence, so no wording is invented and nothing needs translating.
   */
  protected readonly helpToggleLabelledBy: Signal<string> = computed(() => {
    const own = this.helpToggleTextId();

    return this.hasLabel() ? `${own} ${this.labelId()}` : own;
  });

  // TEMPLATE CONSTANTS

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
   * Reconciles direct accessible-name references after projected content changes. Content may be added or
   * removed by built-in control flow, and the field's `for` input may change after initial rendering.
   */
  public ngAfterContentChecked(): void {
    this.ensureProjectedControlsAreNamed();
    this.ensureProjectedControlsAreDescribed();
    this.reflectProjectedDisabledState();
  }

  /**
   * Publishes whether the projected control is unavailable, so the host can be styled for it. ⚠ THE
   * STYLING FOR THIS STATE ALREADY EXISTED AND WAS UNREACHABLE. The stylesheet declares a
   * `:host(.form-field--disabled)` rule that mutes the label and shows a not-allowed cursor, but NOTHING
   * EVER SET THAT CLASS - the component contained no reference to a disabled state at all.
   */
  private reflectProjectedDisabledState(): void {
    const projection = this.hostElement.nativeElement.querySelector<HTMLElement>(
      '.form-field__control',
    );

    if (projection === null) {
      return;
    }

    const controls = Array.from(
      projection.querySelectorAll<HTMLElement>(PROJECTED_CONTROL_SELECTOR),
    );

    // EVERY control, not the first: a field may project a related pair, and it is only unavailable as a
    // whole when none of them can be operated. A field projecting no control at all - a read-only value,
    // which several screens use this component for - is not disabled.
    const unavailable: boolean =
      controls.length > 0 &&
      controls.every(
        (control) =>
          (control as HTMLElement & { disabled?: boolean }).disabled === true ||
          control.getAttribute('aria-disabled') === 'true',
      );

    if (this.projectedControlDisabled() !== unavailable) {
      this.projectedControlDisabled.set(unavailable);
    }
  }

  /**
   * Gives each otherwise unnamed projected control a direct accessible name. A group label supplies
   * context for the composite but cannot name a child control.
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
   * Points each projected control at the help and error regions, and states its validity. ⚠ THIS IS NOT A
   * DUPLICATE OF THE GROUP'S OWN `aria-describedby`. ARIA descriptions are NOT inherited: a description
   * on the composite group is announced when the group is entered and is not announced when the control
   * itself is reached, which is the moment a person needs to be told what is wrong with it.
   */
  private ensureProjectedControlsAreDescribed(): void {
    const projection = this.hostElement.nativeElement.querySelector<HTMLElement>(
      '.form-field__control',
    );

    if (projection === null) {
      return;
    }

    const invalid = this.hasError();
    const contributions: string[] = [];

    // The bound is contributed FIRST, and whenever one exists rather than only while something is
    // expanded, for the same reason the group references it: it has to be heard when the control is
    // REACHED, before a keystroke is lost to a truncation that gives no feedback of its own. ⚠ OMITTING
    // IT HERE IS NOT HARMLESS. An ARIA description on the composite group is not inherited by the
    // control, so with the reference only on the group the bound was announced on entering the field and
    // was silent on the box being typed into - Chrome computed NO accessible description for the control
    // at all, which a runtime accessibility-tree read confirmed against a sibling that does carry one.
    if (this.hasLimit()) {
      contributions.push(this.limitId());
    }

    if (this.helpExpanded()) {
      contributions.push(this.helpId());
    }

    if (invalid) {
      contributions.push(this.errorId());
    }

    const controls = Array.from(
      projection.querySelectorAll<HTMLElement>(DESCRIBABLE_CONTROL_SELECTOR),
    );

    for (const control of controls) {
      this.reconcileDescription(control, contributions);
      this.reconcileInvalidity(control, invalid);
    }
  }

  /**
   * Adds this field's description references to one control without disturbing the consumer's.
   *
   * @param control The projected control.
   * @param contributions The identifiers this field currently has rendered.
   */
  private reconcileDescription(control: HTMLElement, contributions: readonly string[]): void {
    const previous = this.contributedDescriptions.get(control) ?? [];
    const declared = referenceTokens(control.getAttribute('aria-describedby')).filter(
      (token) => !previous.includes(token),
    );
    const next = [...declared, ...contributions];
    const value = next.join(' ');

    if (value.length === 0) {
      // Removing rather than writing an empty string: an empty reference list is read by some
      // assistive technology as a description that exists and says nothing.
      if (control.hasAttribute('aria-describedby')) {
        control.removeAttribute('aria-describedby');
      }
    } else if (control.getAttribute('aria-describedby') !== value) {
      control.setAttribute('aria-describedby', value);
    }

    if (contributions.length === 0) {
      this.contributedDescriptions.delete(control);
    } else {
      this.contributedDescriptions.set(control, [...contributions]);
    }
  }

  /**
   * States or withdraws the validity of one control, leaving a consumer's own statement alone.
   *
   * @param control The projected control.
   * @param invalid Whether this field is currently reporting a failure.
   */
  private reconcileInvalidity(control: HTMLElement, invalid: boolean): void {
    const contributed = this.contributedInvalidity.has(control);

    if (invalid) {
      // ⚠ `aria-invalid` AND `aria-errormessage` ARE OWNED SEPARATELY, and conflating them loses the
      // message. Seventy consumer templates in this application bind `aria-invalid` on their own control -
      // a habit from before this wiring existed - and none binds `aria-errormessage`.
      const consumerOwnsState = !contributed && control.hasAttribute('aria-invalid');

      if (!consumerOwnsState) {
        if (control.getAttribute('aria-invalid') !== INVALID_STATE) {
          control.setAttribute('aria-invalid', INVALID_STATE);
        }

        this.contributedInvalidity.add(control);
      }

      const errorReference = this.errorId();
      const consumerOwnsReference =
        !this.contributedErrorReferences.has(control) &&
        control.hasAttribute('aria-errormessage');

      if (!consumerOwnsReference) {
        if (control.getAttribute('aria-errormessage') !== errorReference) {
          control.setAttribute('aria-errormessage', errorReference);
        }

        this.contributedErrorReferences.add(control);
      }

      return;
    }

    // The reference is withdrawn whenever the region it names has left the document, whoever owns the
    // STATE, because a dangling reference is worse than no reference at all - but only for a reference this
    // component wrote. See `contributedErrorReferences`.
    if (this.contributedErrorReferences.has(control)) {
      control.removeAttribute('aria-errormessage');
      this.contributedErrorReferences.delete(control);
    }

    if (!contributed) {
      return;
    }

    control.removeAttribute('aria-invalid');
    this.contributedInvalidity.delete(control);
  }

  protected toggleHelp(): void {
    this.helpOpen.update((open) => open === false);
  }
}
