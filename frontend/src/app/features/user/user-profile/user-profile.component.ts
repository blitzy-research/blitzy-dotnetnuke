import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, booleanAttribute } from '@angular/core';
import {
  FormControl,
  FormGroup,
  FormRecord,
  ReactiveFormsModule,
  ValidatorFn,
  Validators,
} from '@angular/forms';

import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import {
  PROFILE_VISIBILITY,
  UserProfile,
  UserProfileSubmission,
  UserProfileValue,
  type ProfileVisibilityCode,
} from '../../../core/models/profile.model';

/**
 * Which of the screen's two modes is being rendered.
 *
 * MIGRATION: the legacy application had two pages for this, not one.
 * `Website/admin/Users/ViewProfile.ascx` is five lines long and its entire content is
 * the same editor control with `EditorMode="View"` and `ShowUpdate="False"`, so the
 * two pages were one control in two configurations. They are one component in two
 * modes here for the same reason.
 */
export type UserProfileMode = 'edit' | 'view';

/**
 * The heading used for properties a tenant declared without a category.
 *
 * MIGRATION: the legacy editor grouped by `PropertyCategory` and had no defined
 * behaviour for a blank one — the group simply rendered with an empty heading, which
 * left a bordered box with no accessible name. A named fallback is used instead: a
 * `<fieldset>` whose `<legend>` is empty is worse than one with a generic name,
 * because assistive technology announces the group and then says nothing about it.
 */
const UNCATEGORISED_HEADING = 'General';

/**
 * The declared length above which a property is rendered as a multi-line control.
 *
 * Chosen rather than measured: the legacy editor derived its control from the
 * property's data type through a registry that is out of scope, so there is no legacy
 * threshold to preserve. Two hundred and fifty characters is the point beyond which a
 * single-line control stops being reviewable.
 */
const MULTILINE_LENGTH_THRESHOLD = 250;

/**
 * One category of profile properties, as the template consumes it.
 */
interface ProfileCategory {
  /** A stable key for tracking and for the collapse state. */
  readonly key: string;

  /** The heading rendered in the group's legend. */
  readonly heading: string;

  /** The properties in this category, in the tenant's declared order. */
  readonly values: readonly UserProfileValue[];
}

/**
 * One selectable visibility, as the template consumes it.
 */
interface VisibilityOption {
  /** The code written back to the API. */
  readonly code: ProfileVisibilityCode;

  /** The wording shown to the operator. */
  readonly label: string;
}

/**
 * A validator that rejects a value consisting only of white space.
 *
 * `Validators.required` accepts a string of spaces, and the API does not: a required
 * property submitted blank is refused server-side. Without this the operator would
 * see the form accept the value and the request fail afterwards, which is the worst
 * of both checks.
 *
 * @param control The control to check.
 * @returns A `required` error when the value is blank, otherwise `null`.
 */
const nonBlank: ValidatorFn = (control) => {
  const value = control.value;

  if (typeof value !== 'string') {
    return null;
  }

  return value.trim().length === 0 ? { required: true } : null;
};

/**
 * Builds the validator set a single declared property demands.
 *
 * @param definition The tenant's declaration for the property.
 * @returns The validators to attach to that property's control.
 */
function validatorsFor(definition: UserProfileValue['definition']): ValidatorFn[] {
  const validators: ValidatorFn[] = [];

  if (definition.required) {
    validators.push(Validators.required, nonBlank);
  }

  if (definition.length > 0) {
    validators.push(Validators.maxLength(definition.length));
  }

  const expression = definition.validationExpression;
  if (expression !== null && expression.trim().length > 0 && isUsableExpression(expression)) {
    validators.push(Validators.pattern(expression));
  }

  return validators;
}

/**
 * Reports whether a stored validation expression can be applied in the browser.
 *
 * The expressions are authored by an operator and stored in the database, and the
 * legacy application evaluated them with the .NET regular-expression engine. Some
 * .NET constructs — a named group written `(?<name>…)` in older engines, an inline
 * comment, a conditional — are rejected by the browser's engine, and an unusable
 * expression must not take the whole screen down. It is skipped instead, leaving the
 * server as the authority, which it is in every case anyway.
 *
 * @param expression The stored expression.
 * @returns `true` when the browser can compile it.
 */
function isUsableExpression(expression: string): boolean {
  try {
    // Constructing the expression is the only way to find out whether this engine
    // accepts it. The result is deliberately discarded: `Validators.pattern` compiles
    // its own, and caching one here would tie the validator to this call site.
    new RegExp(expression);
    return true;
  } catch {
    return false;
  }
}

/**
 * Groups a profile's properties by category, preserving the tenant's declared order.
 *
 * A property the tenant has marked as not visible is omitted from BOTH modes, which
 * is what the legacy editor did: `Visible` governed whether the property appeared at
 * all, not merely whether it was read-only. A category left with no visible property
 * is omitted too, rather than rendered as an empty bordered box.
 *
 * @param profile The profile to group, or `null`.
 * @returns The categories to render, in order.
 */
function groupByCategory(profile: UserProfile | null): readonly ProfileCategory[] {
  if (profile === null) {
    return [];
  }

  const order: string[] = [];
  const grouped = new Map<string, UserProfileValue[]>();

  for (const value of profile.properties) {
    if (!value.definition.visible) {
      continue;
    }

    const raw = value.definition.propertyCategory.trim();
    const heading = raw.length === 0 ? UNCATEGORISED_HEADING : raw;
    const existing = grouped.get(heading);

    if (existing === undefined) {
      order.push(heading);
      grouped.set(heading, [value]);
      continue;
    }

    existing.push(value);
  }

  return order.map((heading) => ({
    key: heading,
    heading,
    values: grouped.get(heading) ?? [],
  }));
}

/**
 * The dynamic profile editor, in both of its modes.
 *
 * Renders one collapsible group per declared property category, and within each group
 * either an editable control per property or a name-and-value pair, according to the
 * mode. The property set is entirely tenant-defined — there is no fixed field list —
 * so every label, bound, requirement and validation pattern comes from the
 * declarations the API sends rather than from anything in this file.
 *
 * SELECTOR CONTRACT
 * -----------------
 * The paired stylesheet documents precisely which surfaces it owns, and the template
 * renders exactly those: `.user-profile` for the root rhythm, `.user-profile__groups`
 * for the group stack and the screen's one measure, `.user-profile__group` for a
 * category's inner track, `.user-profile__toggle` with `.user-profile__toggle-icon`
 * for the collapse affordance inside each `<legend>`, `.user-profile__fields` for the
 * edit-mode stack, `.user-profile__visibility` for the per-property visibility row,
 * `.user-profile__values` with `.user-profile__value` for the view-mode list, and
 * `.user-profile__actions` for the action row. Nothing else is styled and nothing
 * else is rendered.
 *
 * WHY THE FIELDS ARE PLAIN MARKUP
 * -------------------------------
 * The stylesheet's commentary describes each child of the field stack as "a shared
 * form field", and `shared/components/form-field` does exist: it renders a real
 * `<label>` with a caller-supplied `for` target, projects the control through
 * `<ng-content />`, and adds the help and error paragraphs. This screen nonetheless
 * renders a plain `<label>` and control pair per property. The reason is structural
 * rather than a claim about what is available: adopting the wrapper inserts an extra
 * element between `.user-profile__fields` and each control, which is the grid the
 * paired stylesheet measures and the paired specification queries, so the swap is a
 * deliberate change to both rather than a side effect of building this screen. Plain
 * markup is not a downgrade in the meantime: the global form partial styles bare
 * `label`, `input`, `select` and `textarea` elements and publishes `.form-required`,
 * `.form-error` and `.form-help`, with unscoped element selectors that reach inside
 * every component regardless of view encapsulation, so the fields already pick up
 * the intended appearance.
 *
 * PRESENTATIONAL BY CONSTRUCTION
 * ------------------------------
 * The component injects nothing. The profile arrives as an input and a submission
 * leaves as an output. That is a deliberate scope decision: `core/services` carries
 * the notification, authentication and token-storage services and no user service, so
 * a screen that fetched its own data would have to introduce that service and its
 * specification as a side effect — the unaccompanied surface the review flagged.
 * Because the screen forwards a fully-formed submission, wiring it to the
 * API later is a change to whichever component routes to it and to nothing here.
 *
 * COLLAPSE IS RENDERED, NOT HIDDEN
 * --------------------------------
 * A collapsed group's field stack is removed from the DOM rather than given the
 * `hidden` attribute. The user agent's `[hidden] { display: none }` rule is weaker
 * than any author rule, and `.user-profile__fields` declares `display: grid` — so a
 * hidden stack would remain visible. Removing it is both correct and cheaper.
 *
 * MIGRATION: the legacy collapse control carried `tabIndex="-1"`, so a keyboard user
 * could not expand or collapse a category at all. It is a real `<button>` here, which
 * restores keyboard operability at no visual cost.
 *
 * MIGRATION: the legacy screen's update affordance was an image link pointing at
 * `~/images/save.gif`. No legacy raster asset is carried across — the only static
 * asset this migration ships is the favicon — so the action is text-only.
 */
@Component({
  selector: 'app-user-profile',
  standalone: true,
  imports: [ReactiveFormsModule, PageHeaderComponent, LoadingSpinnerComponent, EmptyStateComponent],
  templateUrl: './user-profile.component.html',
  styleUrl: './user-profile.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserProfileComponent {
  /**
   * The categories currently rendered, derived from the profile input.
   */
  protected categories: readonly ProfileCategory[] = [];

  /**
   * The form backing edit mode.
   *
   * Two records rather than one: a value is a string and a visibility is a number, and
   * `FormRecord` requires a single control type. Keying both by the declaration
   * identifier means a property's value and its visibility are always looked up the
   * same way.
   */
  protected readonly form = new FormGroup({
    values: new FormRecord<FormControl<string>>({}),
    visibilities: new FormRecord<FormControl<ProfileVisibilityCode>>({}),
  });

  /**
   * The visibility choices offered, in the order the legacy editor offered them.
   */
  protected readonly visibilityOptions: readonly VisibilityOption[] = [
    { code: PROFILE_VISIBILITY.allUsers, label: 'All users' },
    { code: PROFILE_VISIBILITY.membersOnly, label: 'Members only' },
    { code: PROFILE_VISIBILITY.adminOnly, label: 'Administrators only' },
  ];

  /**
   * The categories the operator has collapsed.
   *
   * A plain set rather than a signal: every mutation originates in a DOM event handled
   * by this component, which already marks it for checking, so a reactive primitive
   * would add a dependency graph with nothing to propagate through.
   */
  private readonly collapsed = new Set<string>();

  /**
   * The profile currently held.
   */
  private held: UserProfile | null = null;

  /**
   * The profile to render.
   *
   * Rebuilding the form here rather than in a lifecycle hook keeps the controls and
   * the rendered categories derived from one assignment, so they cannot disagree.
   * Collapse state is reset on a new profile: the categories may be entirely
   * different, and carrying a stale key over would collapse a group the operator never
   * touched.
   */
  @Input()
  set profile(value: UserProfile | null) {
    this.held = value;
    this.categories = groupByCategory(value);
    this.collapsed.clear();
    this.rebuildControls();
  }

  get profile(): UserProfile | null {
    return this.held;
  }

  /**
   * Which mode to render.
   */
  @Input() mode: UserProfileMode = 'edit';

  /**
   * The screen's title, rendered by the shared page header.
   *
   * The stylesheet names the shared page header as the owner of the screen title and
   * its action bar, and explicitly disclaims styling either, so the title is rendered
   * through that component rather than as an ad-hoc heading here.
   */
  @Input() heading = 'Profile';

  /**
   * The screen's supporting line, rendered by the shared page header.
   */
  @Input() subheading?: string;

  /**
   * Whether the profile is still being fetched.
   */
  @Input({ transform: booleanAttribute }) loading = false;

  /**
   * Whether a submission is in flight.
   */
  @Input({ transform: booleanAttribute }) saving = false;

  /**
   * Whether the per-property visibility control is offered.
   *
   * Mirrors the tenant's membership setting of the same meaning, which the API reports
   * as `profileDisplayVisibility`. When the tenant has switched it off the row is not
   * rendered and each property keeps whatever visibility it already had — the
   * submission still carries it, so switching the setting off does not silently reset
   * every property to the tenant default.
   */
  @Input({ transform: booleanAttribute }) manageVisibility = true;

  /**
   * Emitted when the operator submits a valid profile.
   */
  @Output() readonly save = new EventEmitter<UserProfileSubmission>();

  /**
   * Whether the screen has a profile with at least one visible property.
   */
  protected get hasProperties(): boolean {
    return this.categories.length > 0;
  }

  /**
   * Whether the action row is rendered.
   *
   * View mode renders none, which is what `ShowUpdate="False"` asked for on the legacy
   * view page.
   */
  protected get showActions(): boolean {
    return this.mode === 'edit' && this.hasProperties;
  }

  /**
   * Reports whether a category is collapsed.
   *
   * @param key The category key.
   * @returns `true` when the category's fields are hidden.
   */
  protected isCollapsed(key: string): boolean {
    return this.collapsed.has(key);
  }

  /**
   * Collapses an expanded category, or expands a collapsed one.
   *
   * @param key The category key.
   */
  protected toggleCategory(key: string): void {
    if (this.collapsed.has(key)) {
      this.collapsed.delete(key);
      return;
    }

    this.collapsed.add(key);
  }

  /**
   * Returns the control holding a property's value.
   *
   * @param value The property.
   * @returns The control, or `null` when the form has not been built for it.
   */
  protected valueControl(value: UserProfileValue): FormControl<string> | null {
    return this.form.controls.values.controls[this.keyFor(value)] ?? null;
  }

  /**
   * Returns the control holding a property's visibility.
   *
   * @param value The property.
   * @returns The control, or `null` when the form has not been built for it.
   */
  protected visibilityControl(value: UserProfileValue): FormControl<ProfileVisibilityCode> | null {
    return this.form.controls.visibilities.controls[this.keyFor(value)] ?? null;
  }

  /**
   * The identifier given to a property's control, used to associate its label,
   * its validation message and its visibility control.
   *
   * @param value The property.
   * @returns A document-unique identifier.
   */
  protected controlId(value: UserProfileValue): string {
    return `profile-property-${this.keyFor(value)}`;
  }

  /**
   * The identifier given to a property's validation message.
   *
   * @param value The property.
   * @returns A document-unique identifier.
   */
  protected messageId(value: UserProfileValue): string {
    return `${this.controlId(value)}-message`;
  }

  /**
   * The identifier given to a property's visibility control.
   *
   * @param value The property.
   * @returns A document-unique identifier.
   */
  protected visibilityId(value: UserProfileValue): string {
    return `${this.controlId(value)}-visibility`;
  }

  /**
   * Whether a property is rendered as a multi-line control.
   *
   * MIGRATION: the legacy editor selected a control from the property's data type
   * through the excluded property-editor control's own type-to-editor registry, which
   * has no equivalent here. Every type is rendered as text, and the DECLARED LENGTH is
   * used to decide between a single-line and a multi-line control: a property a tenant
   * has allowed several hundred characters for is prose, and squeezing prose into a
   * single-line control makes it unreviewable. The threshold is a presentation choice
   * with no server consequence — the same string is submitted either way.
   *
   * @param value The property.
   * @returns `true` when a multi-line control is used.
   */
  protected isMultiline(value: UserProfileValue): boolean {
    return value.definition.length > MULTILINE_LENGTH_THRESHOLD;
  }

  /**
   * The validation message to show for a property, or `null` when there is none to
   * show yet.
   *
   * A message appears only once the operator has interacted with the control, so a
   * freshly opened form is not covered in errors for values the operator has not been
   * given a chance to supply.
   *
   * MIGRATION: the wording is authored rather than ported. The legacy screen's
   * messages came from the excluded property-editor control's own resource files, not
   * from the profile page, so there is no legacy string to preserve. The server remains
   * the authority on every one of these rules; these messages exist to say the same
   * thing sooner.
   *
   * @param value The property.
   * @returns The message, or `null`.
   */
  protected messageFor(value: UserProfileValue): string | null {
    const control = this.valueControl(value);

    if (control === null || control.valid || !(control.dirty || control.touched)) {
      return null;
    }

    if (control.hasError('required')) {
      return `${value.definition.propertyName} is required.`;
    }

    if (control.hasError('maxlength')) {
      return `${value.definition.propertyName} must be ${value.definition.length} characters or fewer.`;
    }

    if (control.hasError('pattern')) {
      return `${value.definition.propertyName} is not in the expected format.`;
    }

    return `${value.definition.propertyName} is not valid.`;
  }

  /**
   * The text shown for a property in view mode.
   *
   * A recorded value wins; when none was recorded the tenant's declared default is
   * shown, which is what the account will be treated as having. When neither exists the
   * value renders as nothing rather than as a placeholder glyph, because an em dash
   * would be announced as content.
   *
   * @param value The property.
   * @returns The text to render.
   */
  protected displayValue(value: UserProfileValue): string {
    if (value.propertyValue.trim().length > 0) {
      return value.propertyValue;
    }

    return value.definition.defaultValue ?? '';
  }

  /**
   * Handles submission of the edit form.
   *
   * An invalid form emits nothing and instead marks every control as touched, which is
   * what makes the messages appear for controls the operator never visited. Emitting
   * and letting the server refuse it would cost a round trip to say something already
   * known.
   */
  protected onSubmit(): void {
    if (this.saving) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const profile = this.held;
    if (profile === null) {
      return;
    }

    this.save.emit({
      userId: profile.userId,
      values: this.categories.flatMap((category) =>
        category.values.map((value) => ({
          propertyDefinitionId: value.definition.propertyDefinitionId,
          propertyValue: this.valueControl(value)?.value ?? '',
          visibility: this.visibilityControl(value)?.value ?? value.visibility,
        })),
      ),
    });
  }

  /**
   * Restores every control to the value the profile arrived with.
   */
  protected onReset(): void {
    this.rebuildControls();
  }

  /**
   * The key a property's controls are registered under.
   *
   * @param value The property.
   * @returns The record key.
   */
  private keyFor(value: UserProfileValue): string {
    return String(value.definition.propertyDefinitionId);
  }

  /**
   * Replaces every control with one built from the currently held profile.
   *
   * Controls are removed and re-added rather than patched because the declaration set
   * itself may have changed: a property that no longer exists must lose its control,
   * and a property whose declared bound changed must lose its old validator.
   */
  private rebuildControls(): void {
    const values = this.form.controls.values;
    const visibilities = this.form.controls.visibilities;

    for (const key of Object.keys(values.controls)) {
      values.removeControl(key);
    }

    for (const key of Object.keys(visibilities.controls)) {
      visibilities.removeControl(key);
    }

    for (const category of this.categories) {
      for (const value of category.values) {
        const key = this.keyFor(value);

        values.addControl(
          key,
          new FormControl<string>(this.initialValue(value), {
            nonNullable: true,
            validators: validatorsFor(value.definition),
          }),
        );

        visibilities.addControl(
          key,
          new FormControl<ProfileVisibilityCode>(value.visibility, { nonNullable: true }),
        );
      }
    }
  }

  /**
   * The value a property's control starts at.
   *
   * A recorded value is used as-is, including a blank one: a property the account
   * deliberately cleared must not be silently repopulated with the tenant default the
   * next time the screen is opened. The default is used only when nothing has ever been
   * recorded, which the API reports as an empty value with a declared default.
   *
   * @param value The property.
   * @returns The starting value.
   */
  private initialValue(value: UserProfileValue): string {
    if (value.lastUpdatedDate !== null) {
      return value.propertyValue;
    }

    return value.propertyValue.length > 0
      ? value.propertyValue
      : (value.definition.defaultValue ?? '');
  }
}
