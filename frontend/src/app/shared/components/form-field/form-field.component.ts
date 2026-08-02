import { ChangeDetectionStrategy, Component, Input } from '@angular/core';

/**
 * Wraps one form control with its label, its optional help text and its validation
 * message.
 *
 * The control itself is PROJECTED rather than rendered here, and that is the whole
 * design. A component that rendered the control would have to model every control
 * type the administration screens use — text, number, date, select, checkbox, radio,
 * textarea — and would have to proxy the reactive-forms binding through itself.
 * Projecting keeps the control owned by the feature, which is where its form group,
 * its validators and its value live.
 *
 * The label is associated by `for`, which requires the consumer to supply the
 * projected control's `id` through {@link for}. That association is the one thing
 * this component cannot do for the consumer and cannot verify: projected content
 * keeps the style and structure scoping of the component that declares it, so this
 * component can neither read the control's id nor write one onto it. The input is
 * therefore required.
 *
 * MIGRATION: this replaces the legacy label control, which rendered a label
 * alongside a help affordance. Three differences are deliberate:
 *
 * - The legacy help affordance was a raster image with a pop-up. The only static
 *   asset this workspace ships is a favicon, and no component may reference an
 *   image asset, so help text is rendered inline as text instead.
 * - The legacy help strings frequently contained HTML, including strings that open
 *   with a line-break tag. Both the label and the help text are interpolated here
 *   and therefore escaped, so markup in a resource string is shown as text rather
 *   than executed.
 * - The legacy screens marked required fields only by the presence of a validator
 *   that fired after a postback. The required marker here is visible before the
 *   person submits, and is announced, because the projected control carries its own
 *   `required` attribute and this marker is hidden from assistive technology to
 *   avoid saying it twice.
 */
@Component({
  selector: 'app-form-field',
  standalone: true,
  imports: [],
  templateUrl: './form-field.component.html',
  styleUrl: './form-field.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormFieldComponent {
  /**
   * The visible label for the projected control.
   *
   * Required, because a control without a label is a specific, tool-detectable
   * accessibility defect: assistive technology announces the control's type and
   * then has nothing to name it with.
   */
  @Input({ required: true }) label!: string;

  /**
   * The `id` of the projected control, used as the label's `for` target.
   *
   * Required and named `for` to mirror the attribute it becomes. The consumer must
   * put the same value on the control it projects; nothing here can enforce that,
   * because a component cannot reach into content projected into it.
   *
   * MIGRATION: the legacy markup associated its labels the same way, through a
   * control-to-render identifier, so this is a translation of existing behaviour
   * rather than a new obligation on consumers.
   */
  @Input({ required: true }) for!: string;

  /**
   * Whether to show the required marker.
   *
   * Presentational only. The authoritative `required` state belongs to the projected
   * control and to the form's validators — this flag cannot make a field required and
   * must not be mistaken for doing so. Two sources of one truth is the risk here, and
   * it is accepted deliberately because the marker has to be rendered outside the
   * control, next to the label, where the control cannot reach.
   */
  @Input() required: boolean = false;

  /**
   * Supporting text shown beneath the control.
   *
   * Optional rather than defaulted, so "absent" is `undefined` and a consumer may
   * bind a value that is itself absent.
   */
  @Input() help?: string;

  /**
   * The validation message to show, or absent when the field is valid.
   *
   * A single resolved string rather than a validator-errors object: turning a
   * validator key into a sentence needs the field's own vocabulary and bounds, which
   * live in the feature. Resolving it there keeps the legacy wording — which this
   * migration preserves character for character, including its spacing — out of a
   * shared component that has no way to know it.
   */
  @Input() error?: string;

  /**
   * The `id` given to the help text, referenced by the control's `aria-describedby`.
   *
   * Derived from {@link for} so a consumer does not have to invent and thread a
   * second identifier. Consumers add `[attr.aria-describedby]` to the projected
   * control pointing at this value when help text is present.
   */
  get helpId(): string {
    return `${this.for}-help`;
  }

  /**
   * The `id` given to the error message, referenced by the control's
   * `aria-describedby`.
   */
  get errorId(): string {
    return `${this.for}-error`;
  }

  /** Whether help text was supplied and is not blank. */
  get hasHelp(): boolean {
    return (this.help ?? '').trim().length > 0;
  }

  /** Whether a validation message was supplied and is not blank. */
  get hasError(): boolean {
    return (this.error ?? '').trim().length > 0;
  }

  /** The marker appended to the label of a required field. */
  readonly requiredMarker = REQUIRED_MARKER;
}

/**
 * The visible required marker.
 *
 * Bound from a constant rather than written into the template, because the template
 * compiler collapses runs of whitespace in template text; a marker authored inline
 * beside an interpolation is exactly the case where that collapsing is visible.
 */
const REQUIRED_MARKER = '*';
