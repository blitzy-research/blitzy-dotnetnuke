import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  type Signal,
} from '@angular/core';
import { Router } from '@angular/router';
import {
  FormControl,
  FormGroup,
  FormRecord,
  ReactiveFormsModule,
  Validators,
  type ValidatorFn,
} from '@angular/forms';

import {
  problemDetailsFieldErrors,
  type ProblemDetails,
} from '../../../core/models/problem-details.model';
import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type ProfileVisibilityCode,
  type UserProfile,
  type UserProfileSubmission,
  type UserProfileValue,
} from '../../../core/models/profile.model';
import type { UserDetail } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { UserStore, type UserFailure, type UserMutation } from '../../../core/state/user.store';
import { parseRouteId, readRouteId } from '../../../core/utils/route-id.util';
import { requiredText } from '../../../core/utils/required-text.validator';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

/**
 * Which of the screen's two modes is rendered.
 *
 * MIGRATION: the legacy application presented this as two pages and it is ONE
 * component here. `Website/admin/Users/ViewProfile.ascx` is five lines in total and its
 * entire body registers and instantiates `Profile.ascx` with `EditorMode="View"` and
 * `ShowUpdate="False"` — neither attribute exists on `Profile.ascx` itself, they are
 * properties of the hosted control (`Profile.ascx.vb` L77-L84 and L114-L121). Two pages
 * were therefore one control in two configurations, and they are one component in two
 * modes for exactly that reason. No second route is invented.
 */
export type UserProfileMode = 'edit' | 'view';

/** The mode the admin route opens in: this is the edit path. */
const DEFAULT_MODE: UserProfileMode = 'edit';

/**
 * The heading used for a property whose category is blank.
 *
 * The column is `PropertyCategory nvarchar(50) NOT NULL`
 * (`04.00.04.SqlDataProvider` L1107+) so it is never absent, but `NOT NULL` does not
 * forbid the empty string and the legacy null contract spelled absent text AS the
 * empty string (`Library/Components/Shared/Null.vb` L71-L73, `NullString` returns
 * `""`). A `<fieldset>` whose `<legend>` renders nothing is announced as a group and
 * then says nothing about itself, so a named fallback is used.
 */
const UNCATEGORISED_HEADING = 'General';

/**
 * The declared length above which a property is rendered multi-line.
 *
 * Chosen, not measured, and labelled as such. The legacy editor picked its control
 * from the property's data type through the excluded `Library/Controls/PropertyEditor`
 * registry, so there is no legacy threshold to preserve — see the note on
 * {@link UserProfileComponent} about the unresolved data type. Two hundred and fifty
 * characters is the point beyond which a single-line control stops being reviewable.
 * The threshold is presentation only: the same string is submitted either way.
 */
const MULTILINE_LENGTH_THRESHOLD = 250;

/**
 * The fewest rows a multi-line control ever renders — U-M6.
 *
 * Four is what every multi-line control used to render REGARDLESS of how much text it could hold, and
 * that fixed figure is the defect. Measured against a declaration bounded at four thousand characters:
 * four rows showed 2.88 per cent of what the field could contain at a 320-unit viewport, so an operator
 * reviewing a stored answer was reading it through a slot. It is retained as the FLOOR because it is
 * the right size for a declaration just over the multi-line threshold, where the alternative is a
 * control mostly full of empty rows.
 */
const MULTILINE_MINIMUM_ROWS = 4;

/**
 * The most rows a multi-line control ever renders — U-M6.
 *
 * A ceiling is necessary, because the declared length has no upper bound of its own: a declaration
 * bounded at forty thousand characters would otherwise render a control taller than any screen, pushing
 * every property beneath it — and the save command — arbitrarily far down. Twelve rows is roughly a
 * third of a 900-unit viewport, which leaves the surrounding form navigable. Beyond it, the control's
 * own internal scroll and the vertical resize handle the shared form vocabulary already gives every
 * text area are the right affordances.
 */
const MULTILINE_MAXIMUM_ROWS = 12;

/**
 * Declared characters per rendered row, for sizing a multi-line control — U-M6.
 *
 * An ESTIMATE and labelled as one: the real figure depends on the control's resolved width, the type
 * step and the text itself, none of which is knowable when the attribute is written. A hundred and
 * twenty is deliberately generous against the measured control — roughly 45 to 70 characters fit a line
 * at the widths this screen resolves to — because the alternative errs towards a control that fills the
 * viewport. The consequence is that a declaration is sized by ORDER OF MAGNITUDE rather than exactly,
 * which is all a starting height needs to be: the resize handle and the internal scroll settle the rest.
 */
const DECLARED_CHARACTERS_PER_ROW = 120;

/**
 * The legacy-seeded properties the installer gave the RICH-TEXT data type.
 *
 * ONE MEMBER, AND THE COUNT IS MEASURED RATHER THAN ASSUMED. The installer's
 * `AddDefaultPropertyDefinitions` seeds nineteen properties and passes
 * `@RichTextDataType` to exactly one of them:
 * `Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider` L1330
 * (`… @RichTextDataType, '', 'Preferences','Biography' ,0, '', 33, 1, 0`), which the
 * earlier `03.02.03.SqlDataProvider` L1284 seeds identically. Every other seeded
 * property is text, country, region, time-zone or locale.
 *
 * WHY THIS EXISTS AT ALL, GIVEN {@link MULTILINE_LENGTH_THRESHOLD}
 * ---------------------------------------------------------------
 * The length rule cannot reach this property. The installer passes `@Length = 0` for
 * every property it gave a specialised data type, Biography included, and zero is the
 * ABSENCE of a bound rather than a large one — so a purely length-driven choice renders
 * the one property that is unambiguously prose as a single-line box, which is the
 * opposite of what a biography needs and contradicts this migration's own recorded
 * decision that rich text is reduced to a plain multi-line control.
 *
 * WHY IT IS KEYED ON THE NAME AND NOT ON `dataType`
 * -------------------------------------------------
 * Because `dataType` cannot be read. It is a foreign key into the excluded `Lists`
 * table, resolved BY NAME by the installer, so the stored integer is database-assigned
 * and differs between installations; an integer-to-control map would silently render the
 * wrong control wherever the list seeded in a different order. See the note on
 * {@link UserProfileComponent}. The property NAME is the stable identifier for a seeded
 * property, and this file already depends on that fact for every label, help string and
 * required message in {@link LEGACY_PROFILE_WORDING} — including Biography's own help,
 * which reads "Use the Editor to enter a short Biography".
 *
 * WHY "LENGTH ZERO MEANS PROSE" WAS REJECTED as the rule instead. Region, Country,
 * TimeZone and PreferredLocale are all seeded with `@Length = 0` too, so that reading
 * would put a country and a time zone in four-row text areas. The set is deliberately
 * narrow: it names the properties measured as rich text, and nothing else.
 *
 * A tenant-added property is unaffected, and a tenant that RENAMES Biography simply
 * falls back to the length rule — the same fallback its label and help text already
 * take.
 */
const LEGACY_RICH_TEXT_PROPERTIES: ReadonlySet<string> = new Set(['Biography']);

/**
 * What is shown when the tenant has declared no profile property at all.
 *
 * A legitimate configuration rather than a failure, so it is an empty state and not an
 * error. Held here rather than inline in the template so that all of this screen's
 * authored wording sits in one file, beside the legacy wording it accompanies.
 *
 * It names the screen that fixes the situation in prose. The route is reached by a
 * `routerLink` URL where a link is wanted; nothing here imports from a sibling feature
 * folder to obtain it.
 */
const NO_PROPERTIES_MESSAGE =
  'This site declares no profile properties, so there is nothing to show. ' +
  'Define one under Profile Properties to begin.';

/**
 * Shown once a profile write succeeds.
 *
 * MIGRATION: net-new wording, with no legacy counterpart, and the absence of one is explained by the
 * legacy mechanism rather than by an omission. `Website/admin/Users/Profile.ascx.vb` raises no module
 * message on its success path and its resources declare none, because a full-page postback was itself
 * the confirmation - the page visibly reloaded carrying the stored values. Nothing reloads here.
 *
 * Phrased to match the sibling screens that already confirm their writes, so one voice is used across
 * the console rather than one per screen.
 */
const PROFILE_SAVED_MESSAGE = 'The profile was saved.';

/**
 * The sentence shown when the address names no readable account.
 *
 * MEASURED LEGACY WORDING, not authored: `ManageUsers.ascx.vb` L207/L221 pairs this warning with
 * `DisableForm()`, which sets `Visible = False` on every panel — so the legacy answer to a missing
 * account was this sentence and no form, which is what this screen now does too.
 *
 * ⚠ RESTATED HERE RATHER THAN IMPORTED FROM `user-form.component.ts`, WHICH ALSO EXPORTS IT. That
 * import would reach into a sibling component module for one string and drag its component class
 * into this screen's lazily-loaded chunk, which is a real cost paid on every visit for nothing.
 * The two copies are held identical deliberately: one situation must not be described two ways
 * inside one feature, and an operator following a stale link from the account screen to its
 * profile must be told the same thing twice rather than two different things.
 */
const NO_USER_MESSAGE = "This account doesn't exist";

/**
 * The greatest number of properties one profile write may carry.
 *
 * THE API'S OWN BOUND, reproduced from `UserService.ProfilePropertySubmissionMaximum`, where a
 * submission carrying more is refused whole with `user.profile.too-many-properties`. The limit exists
 * because the server does per-property work — it resolves each declaration, evaluates the tenant's own
 * validation expression against a match timeout and reconciles two storage columns — so the amount of
 * that work per request is deliberately bounded.
 *
 * ⚠ IT IS A LIMIT ON THE WRITE, WHICH MAKES IT A LIMIT ON THIS SCREEN. This screen submits EVERY
 * declared property on every save, because the write replaces rather than merges — a property omitted
 * from the payload is a property cleared. So a tenant declaring more than this many properties could not
 * save a profile at all, and before this bound was stated here the operator discovered that by filling
 * the form in and being refused, with no indication of which of the two ends was at fault.
 */
const MAXIMUM_SUBMITTED_PROPERTIES = 64;

/**
 * What is shown when the tenant declares more properties than one write may carry.
 *
 * States the measured numbers on both sides, because "too many" without them tells the operator nothing
 * they can act on, and names the screen where the declarations are managed — in prose, so that this
 * file imports no route from a sibling feature folder.
 *
 * ⚠ THE FORM IS WITHHELD RATHER THAN OFFERED-AND-REFUSED. Offering it would invite an operator to type
 * into something that cannot be saved, which is the same reasoning the account editor applies to an
 * account that does not exist.
 *
 * @param declared How many properties the tenant declares.
 * @returns The sentence to show in place of the form.
 */
function tooManyPropertiesMessage(declared: number): string {
  return (
    `This site declares ${String(declared)} profile properties, and a profile can be saved with at ` +
    `most ${String(MAXIMUM_SUBMITTED_PROPERTIES)}. Reduce the number of declared properties under ` +
    'Profile Properties before editing this profile.'
  );
}

/**
 * The wording a single legacy-seeded property carried.
 */
interface LegacyWording {
  /** The label, exactly as the resource file spelled it, colon included. */
  readonly label: string;

  /** The help text, exactly as the resource file spelled it. */
  readonly help: string;

  /** The message shown when a required value is missing. */
  readonly required: string;
}

/**
 * The wording of the nineteen properties DotNetNuke seeded, verbatim from
 * `Website/admin/Users/App_LocalResources/Profile.ascx.resx`.
 *
 * WHY THIS TABLE EXISTS AT ALL, GIVEN THE PROPERTY SET IS DYNAMIC
 * ---------------------------------------------------------------
 * The runtime property list comes from the API and nothing here constrains it. This
 * table supplies the LABEL, HELP and REQUIRED MESSAGE for the properties a legacy
 * installation already has, so that an operator who used the old screen sees the same
 * words on the new one. That is the Minimal Change Clause's UI-parity item: "error
 * messages EQUIVALENT". A property absent from this table — anything a tenant added —
 * falls back to its own `PropertyName`, so the table constrains nothing and is purely
 * additive.
 *
 * MEASUREMENT
 * -----------
 * The resource file holds 67 `<data>` nodes. Four are the inert designer placeholders
 * `Bitmap1`, `Color1`, `Icon1` and `Name1`, leaving 63 keyed entries, which the entries
 * below account for exactly: 19 properties x 3 keys = 57, plus 4 category headers, plus
 * `ProfileProperties_Country.Not Specified`, plus `ProfileTitle.Text` = 63.
 *
 * MIGRATION: THE WORDING IS PRESERVED VERBATIM, DEFECTS AND ALL, because the
 * domain-logic-preservation item forbids opportunistic correction. Four measured
 * oddities are carried across rather than tidied:
 *   - `MiddleName` help DUPLICATES its own label, colon included. Not a transcription
 *     slip on this side: the resource value is literally `Middle Name:`.
 *   - `Unit` help misspells "apartment" as "appartment".
 *   - `Cell` is labelled "Cell/Mobile:" but its message says "Cell Phone", and `Fax` is
 *     labelled "Fax:" but its message says "Fax number".
 *   - `PreferredLocale` says "Preferred locale is required" with a lower-case "locale"
 *     while its label capitalises it.
 * Every one of these is what the operator read before and is what they read now.
 *
 * Only the four `.Header` values are NOT reproduced as a table: measured, they are
 * byte-identical to the `PropertyCategory` keys they head — "Name", "Address",
 * "Contact Info" and "Preferences" — so the category string is rendered directly and a
 * mapping table would be pure redundancy that could only drift.
 */
const LEGACY_PROFILE_WORDING: Readonly<Record<string, LegacyWording>> = Object.freeze({
  Prefix: {
    label: 'Prefix:',
    help: 'Enter a prefix (eg Mr. Dr.)',
    required: 'Prefix is required',
  },
  FirstName: {
    label: 'First Name:',
    help: 'Enter a first name',
    required: 'First Name is required',
  },
  MiddleName: {
    label: 'Middle Name:',
    // MIGRATION: the help text genuinely duplicates the label in the resource file,
    // colon and all. Reproduced verbatim, never corrected.
    help: 'Middle Name:',
    required: 'Middle Name is required',
  },
  LastName: {
    label: 'Last Name:',
    help: 'Enter a Last Name',
    required: 'Last Name is required',
  },
  Suffix: {
    label: 'Suffix:',
    help: 'Enter a suffix (eg BSc, PhD)',
    required: 'Suffix is required',
  },
  Unit: {
    label: 'Unit:',
    // MIGRATION: "appartment" is the legacy spelling. Preserved.
    help: 'Enter a unit number for an appartment or condominium',
    required: 'Unit is required',
  },
  Street: {
    label: 'Street:',
    help: 'Enter the street part of the address (eg. 123 Main Street)',
    required: 'Street is required',
  },
  City: {
    label: 'City:',
    help: 'Enter the city part of the address',
    required: 'City is required',
  },
  Region: {
    label: 'Region:',
    help: 'Enter the region (state, province or county) part of the address',
    required: 'Region is required',
  },
  Country: {
    label: 'Country:',
    help: 'Select the Country part of the address',
    required: 'Country is required',
  },
  PostalCode: {
    label: 'Postal Code:',
    help: 'Enter the Postal/Zip Code part of the address',
    required: 'Postal Code is required',
  },
  Telephone: {
    label: 'Telephone:',
    help: 'Enter a telephone number',
    required: 'Telephone is required',
  },
  Cell: {
    label: 'Cell/Mobile:',
    help: 'Enter a cell/mobile phone number',
    // MIGRATION: the message says "Cell Phone" where the label says "Cell/Mobile".
    required: 'Cell Phone is required',
  },
  Fax: {
    label: 'Fax:',
    help: 'Enter a fax number',
    // MIGRATION: the message says "Fax number" where the label says "Fax".
    required: 'Fax number is required',
  },
  Website: {
    label: 'Website:',
    help: 'Enter a web site url.',
    required: 'Website is required',
  },
  IM: {
    label: 'IM:',
    help: 'Enter an instant messenger id (AOL, MSN, Yahoo etc)',
    required: 'IM handle is required',
  },
  TimeZone: {
    label: 'Time Zone:',
    help: 'Select your time zone from the drop-down list',
    required: 'Time Zone is required',
  },
  PreferredLocale: {
    label: 'Preferred Locale:',
    help: 'Choose your preferred locale.',
    // MIGRATION: lower-case "locale" in the message, capitalised in the label.
    required: 'Preferred locale is required',
  },
  Biography: {
    label: 'Biography:',
    // MIGRATION: the help says "Use the Editor", meaning the rich-text editor. The FCK
    // editor provider is out of scope, so the control is a plain multi-line text box and
    // the wording is still the wording the operator knows.
    help: 'Use the Editor to enter a short Biography',
    required: 'Biography is required',
  },
});

/**
 * The empty-option wording a list-backed property would carry.
 *
 * MIGRATION: measured as `ProfileProperties_Country.Not Specified` — the only key in the
 * entire in-scope resource census whose suffix contains a space, which is why any
 * resource lookup has to tolerate a non-identifier suffix. It labels the blank entry of
 * a list-backed control, and this screen renders no list-backed control for a profile
 * value because the declared data type cannot be resolved to one. It is published here
 * so the wording survives the migration and is reachable the moment data-type
 * resolution lands. See {@link UserProfileComponent} for that gap.
 */
export const NOT_SPECIFIED_OPTION_TEXT = 'Not Specified';

/**
 * The serialised forms of the legacy null-date sentinel.
 *
 * `Library/Components/Shared/Null.vb` L66-L68 defines `NullDate` as `Date.MinValue`, and
 * that sentinel SURVIVES ON THE WIRE: the API serialises with the ignore condition set
 * to never, so a date that was never set arrives as a real value rather than being
 * omitted. Rendering it would put `01/01/0001` in front of an operator as though it were
 * a date somebody chose.
 *
 * Matched by prefix rather than by equality because the tail varies with how the value
 * was serialised — a bare date, a date and time, a time zone offset, or a
 * culture-formatted string all name the same sentinel.
 */
const NULL_DATE_PREFIXES: readonly string[] = Object.freeze(['0001-01-01', '1/1/0001', '01/01/0001']);

/**
 * Reports whether a stored value is the legacy null-date sentinel rather than a date.
 *
 * @param value The stored value.
 * @returns `true` when the value is the sentinel and must render as nothing.
 */
function isNullDateSentinel(value: string): boolean {
  const trimmed = value.trim();

  return NULL_DATE_PREFIXES.some((prefix) => trimmed.startsWith(prefix));
}

/**
 * One category of profile properties, as the template consumes it.
 */
export interface ProfileSection {
  /** A stable key, used for tracking and for the collapse state. */
  readonly key: string;

  /** The heading rendered in the group's legend. */
  readonly heading: string;

  /** The properties in this category, ordered by their declared view order. */
  readonly values: readonly UserProfileValue[];
}

/**
 * One selectable visibility, as the template consumes it.
 */
export interface VisibilityOption {
  /** The code written back to the API. */
  readonly code: ProfileVisibilityCode;

  /** The wording shown to the operator. */
  readonly label: string;
}

/** The two records that back the dynamic form. */
interface ProfileFormModel {
  /** One control per property, holding the value as a string. */
  readonly values: FormRecord<FormControl<string>>;

  /** One control per property, holding the visibility as a code. */
  readonly visibilities: FormRecord<FormControl<ProfileVisibilityCode>>;
}

/**
 * Builds the validators one declared property demands.
 *
 * The three rules are the three the declaration carries, and they are applied in the
 * order the legacy editor applied them.
 *
 * MIGRATION: A DECLARED LENGTH OF ZERO MEANS "NO MAXIMUM" AND MUST NOT BECOME A BOUND.
 * The terminal schema declares `Length int NOT NULL CONSTRAINT DF_..._Length DEFAULT 0`
 * (`04.00.04.SqlDataProvider` L1107+), so zero is what every property gets when nobody
 * chose a bound — it is the absence of a bound, not a bound of nothing. Handing zero to
 * a maximum-length validator would mark EVERY control invalid and make the screen
 * unusable, so the validator is attached only when the declared length is positive. The
 * legacy help text for the field says the same thing: the length "will only be
 * applicable for specific data types".
 *
 * @param definition The tenant's declaration for the property.
 * @returns The validators to attach to that property's value control.
 */
function validatorsFor(definition: ProfilePropertyDefinition): ValidatorFn[] {
  const validators: ValidatorFn[] = [];

  if (definition.required) {
    // One validator, not two. `requiredText` from `core/utils` covers absence AND blankness and
    // reports both under the `required` key, which is what this screen's local pair did between
    // them. It replaced a feature-local copy of the same idea: the blankness rule had been written
    // out independently here, on the login screen and on the module export screen, and a shared
    // home is what stops three copies drifting apart from the one server rule they all mirror.
    validators.push(requiredText);
  }

  if (definition.length > 0) {
    validators.push(Validators.maxLength(definition.length));
  }

  // ⚠ THE TENANT'S VALIDATION EXPRESSION IS DELIBERATELY NOT EVALUATED HERE, AND THIS IS A SECURITY
  // DECISION RATHER THAN A SIMPLIFICATION. The expression is administrator-authored data stored in
  // `ValidationExpression`, so it is untrusted input to whatever engine runs it, and a catastrophically
  // backtracking pattern — the classic nested-quantifier shape — takes exponential time on an ordinary
  // input. Running it here meant running it on the UI thread, synchronously, on EVERY keystroke of a
  // control whose length is frequently unbounded (a declared length of zero means no maximum), so one
  // such declaration froze the browser tab with no way out.
  //
  // The server is not merely the fallback authority, it is the only party that can run this safely: it
  // compiles the expression with a fifty-millisecond match timeout, a length ceiling and a bounded
  // compiled-expression cache. The browser's engine offers no timeout of any kind — there is no API for
  // one — so a client-side evaluation cannot be bounded at all, only avoided.
  //
  // The consequence is a refusal that arrives from the server rather than beside the box as it is typed.
  // That is the correct trade: the rule is still enforced, still reported per field through the server's
  // model-state message, and a tenant can no longer author a declaration that hangs an operator's browser.
  return validators;
}

/**
 * The value a property's control starts at.
 *
 * MIGRATION: THIS IS WHERE "NEVER SET" IS DISTINGUISHED FROM "SET TO EMPTY", which the
 * legacy code could not do. `UserProfile.vb` L507-L517 returns `Null.NullString` — the
 * empty string — both when a property is missing and when its value is empty, so the two
 * were indistinguishable. The API preserves the distinction through
 * `lastUpdatedDate`, which is `null` for a value that has never been written:
 *   - never written  -> seed from the declaration's default, reproducing
 *     `InitialiseProfile(portalId, useDefaults := True)` (L545-L554), which copied
 *     `DefaultValue` into every property.
 *   - written        -> use the recorded value verbatim, INCLUDING the empty string, so
 *     a value an operator deliberately cleared is not silently repopulated with the
 *     default the next time the screen opens.
 *
 * ⚠ A SUPPLIED VALUE WINS OVER THE AUDIT METADATA, AND THAT ORDER IS THE POINT. The
 * timestamp is metadata ABOUT a value; the value itself is the stronger evidence that
 * something is stored. Deciding on the timestamp alone means that a property arriving
 * with content and no timestamp is rendered as the declaration's DEFAULT — and because
 * this screen submits every property it renders, the operator's next save writes that
 * default straight over the content. Silent data loss, on a screen that looked like it
 * had loaded correctly.
 *
 * That combination cannot arise from the current contract: the entity's own column is
 * `LastUpdatedDate datetime NOT NULL` and the projection emits `stored?.LastUpdatedDate`,
 * so a null timestamp means precisely "no row exists" and a row with no row has no value
 * either. This ordering is therefore DEFENCE rather than a change of behaviour — every
 * case the contract can produce today is decided identically — and it is written this way
 * because the cost is nothing and the failure it forecloses is unrecoverable. It also
 * survives the contract changing shape underneath it, which a check on metadata alone
 * would not.
 *
 * The remaining ambiguity — no timestamp AND no value — is seeded from the default, which
 * is both the legacy behaviour and the only sensible reading: there is nothing to lose.
 *
 * MIGRATION: the null-date sentinel is blanked here rather than rendered. See
 * {@link isNullDateSentinel}.
 *
 * @param value The property and its declaration.
 * @returns The starting value, always a string.
 */
function initialValueFor(value: UserProfileValue): string {
  const supplied = value.propertyValue;

  // Anything actually carried is used verbatim, whatever the metadata beside it says.
  if (supplied.length > 0) {
    return isNullDateSentinel(supplied) ? '' : supplied;
  }

  // Nothing carried: the timestamp decides between a row that holds the empty string and no row at all.
  if (value.lastUpdatedDate !== null) {
    return '';
  }

  const seeded = value.definition.defaultValue ?? '';

  if (isNullDateSentinel(seeded)) {
    return '';
  }

  // ⚠ #9 — A DEFAULT THAT ITS OWN DECLARATION WOULD REJECT IS NOT SEEDED, AND THIS ONE GUARD IS WHAT
  // STOPPED A SINGLE DECLARATION BLOCKING PROFILE EDITING FOR EVERY ACCOUNT IN THE TENANT.
  //
  // Measured: one declaration carried a sixty-one character default under a declared length of fifty. The
  // control was seeded from it, the length validator this file attaches from the SAME declaration rejected
  // it, and the form was therefore invalid the instant it painted — for every account that had recorded
  // nothing for that property, which is every account. Nothing on screen could be corrected to fix it,
  // because the offending value was not the account's: deleting it from the box worked only until the
  // screen was next opened, and the real correction was to the declaration on a different screen entirely.
  // The account's own profile was collateral.
  //
  // ⚠ IT APPLIES TO THE DEFAULT ALONE AND CAN NEVER REACH A RECORDED VALUE. Every branch above has already
  // returned by this point: a supplied value returns verbatim, and a written-but-empty value returns empty.
  // So the only value this guard can withhold is one that came from the declaration rather than from the
  // account, which is exactly the value nobody entered and nobody can be asked to correct here. Applying
  // the same test to a recorded value would DESTROY DATA — an over-long stored value is still the
  // operator's value, and the correct answer for it is the refusal the length rule already raises.
  //
  // ⚠ WHY WITHHOLDING RATHER THAN RELAXING THE RULE. The alternative is to stop applying the declared
  // length when the default violates it, which weakens a rule the server enforces regardless and would
  // make the client disagree with the server — the exact fault the administrator-bypass note on
  // {@link UserProfileComponent.onSubmit} explains at length. A default is a SUGGESTION the declaration
  // offers; a suggestion the declaration itself rejects is not a usable one, so it is not offered. The
  // field then opens in its natural unset state, and if the property is required the operator is correctly
  // asked for a valid value instead of being handed an invalid one.
  //
  // Only the declared LENGTH is tested, because it is the only declared rule evaluated in the browser. The
  // tenant's validation expression is deliberately never run here — see {@link validatorsFor} — so a
  // default that violates the PATTERN cannot make this form invalid on load, and the server's refusal
  // remains the authority on it.
  if (value.definition.length > 0 && seeded.length > value.definition.length) {
    return '';
  }

  return seeded;
}


/**
 * Orders two properties by their declared view order.
 *
 * `ViewOrder int NOT NULL` is the tenant's chosen display order and the declaration
 * marks it required (`ProfilePropertyDefinition.vb` L300). It is not unique, so the
 * declaration identifier breaks a tie: `PropertyDefinitionID int IDENTITY(1,1)` is
 * unique by definition, which makes the ordering total and therefore stable across
 * renders. Without a tie-break, two properties sharing a view order could swap places
 * between renders and move a control out from under the operator's cursor.
 *
 * @param left The first property.
 * @param right The second property.
 * @returns A negative, zero or positive ordering result.
 */
function compareByViewOrder(left: UserProfileValue, right: UserProfileValue): number {
  const byOrder = left.definition.viewOrder - right.definition.viewOrder;

  if (byOrder !== 0) {
    return byOrder;
  }

  return left.definition.propertyDefinitionId - right.definition.propertyDefinitionId;
}

/**
 * Groups a profile's properties into the sections the screen renders.
 *
 * MIGRATION: EVERY DECLARED PROPERTY IS RENDERED, INCLUDING ONE MARKED NOT VISIBLE, and
 * that is the legacy behaviour rather than a relaxation of it. `Profile.ascx.vb`
 * L162-L168 walks the property collection immediately before binding and executes
 * `If IsAdmin Then profProperty.Visible = True`, so an administrator saw every property
 * regardless of the declaration's `Visible` flag. The view page's own filter
 * (`ViewProfile.ascx.vb` L84-L97) reaches the same conclusion on this route: it narrows
 * an `AdminOnly` property to `(IsAdmin Or IsUser)` and a `MembersOnly` property to
 * `Request.IsAuthenticated`, and on an administrator-only, authenticated route all three
 * branches evaluate to visible. The filter therefore COLLAPSES to "show everything", and
 * a client-side filter is not implemented rather than being implemented and then always
 * passing.
 *
 * MIGRATION: no soft-delete filter is applied either. `Deleted bit NOT NULL` exists on
 * the table but is absent from the fifteen properties of the legacy class, because
 * `ProfileController.GetPropertyDefinitionsByPortal(portalId, True)` filtered it
 * SERVER-SIDE. Filtering again here would duplicate a decision this screen cannot see
 * the inputs to.
 *
 * Grouping is by `PropertyCategory` because the legacy editor was configured
 * `groupByMode="Section"` with `groupHeaderIncludeRule="True"` (`Profile.ascx` L18 and
 * L25). Sections appear in the order their earliest property appears, which follows from
 * ordering the whole set by view order first: the tenant's ordering choice therefore
 * decides both the order of the sections and the order within them, from one field.
 *
 * @param profile The profile to group, or `null` when none has been read.
 * @returns The sections to render, in order.
 */
function toSections(profile: UserProfile | null): readonly ProfileSection[] {
  if (profile === null) {
    return [];
  }

  const ordered = [...profile.properties].sort(compareByViewOrder);
  const headings: string[] = [];
  const grouped = new Map<string, UserProfileValue[]>();

  for (const value of ordered) {
    const declared = value.definition.propertyCategory.trim();
    const heading = declared.length === 0 ? UNCATEGORISED_HEADING : declared;
    const existing = grouped.get(heading);

    if (existing === undefined) {
      headings.push(heading);
      grouped.set(heading, [value]);
      continue;
    }

    existing.push(value);
  }

  return headings.map((heading) => ({
    key: heading,
    heading,
    values: grouped.get(heading) ?? [],
  }));
}

/**
 * Resolves the account identifier the route supplied.
 *
 * MIGRATION: SENTINEL DISCIPLINE. `Library/Components/Shared/Null.vb` L41-L43 defines
 * `NullInteger` as `-1`, and `ViewProfile.ascx.vb` L60 opened by assigning exactly that
 * to `UserId` so that a request carrying no ticket failed closed. Two consequences are
 * honoured here rather than assumed away:
 *   - Presence is tested EXPLICITLY. There is no truthiness test, no `> 0`, no `<= 0` and
 *     no coalescing to a sentinel. `dbo.Users.UserID` is `IDENTITY(1,1)` so zero does not
 *     occur naturally, but neighbouring tables seed at zero and at minus one
 *     (`Roles.RoleID` is `IDENTITY(0,1)`, `Portals.PortalID` is `IDENTITY(-1,1)`), so
 *     treating any particular integer as "absent" is exactly the mistake that makes a
 *     real row unreachable. Zero is accepted as a real identifier — defensively, and the
 *     nuance is stated rather than silently relied upon.
 *   - The route delivers a STRING. `withComponentInputBinding()` binds the raw parameter,
 *     so the conversion is performed here, once, and a value that is not a whole number
 *     resolves to `null` and loads nothing rather than dispatching a request for `NaN`.
 *
 * @param raw The value the route or a caller supplied.
 * @returns The identifier, or `null` when none was supplied or it was not a whole number.
 */
function resolveUserId(raw: string | number | undefined): number | null {
  // Delegated to the one parser in the workspace that performs this conversion. This
  // function used to use `Number`, which accepts a hexadecimal literal, an exponent and a
  // decimal point — `'0x10'` resolved to 16 — and checked neither the safe-integer ceiling
  // nor the API's 32-bit range. The shared parser reproduces exactly what `int.TryParse`
  // under `NumberStyles.Integer` accepts server-side, and it already answers `null` for
  // absence, which is this screen's own representation, so nothing is adapted.
  return parseRouteId(raw);
}


/**
 * The dynamic profile editor, in both of its modes — the screen behind
 * `users/:userId/profile`.
 *
 * Renders one collapsible group per declared property category and, within each group,
 * either an editable field per property or a name-and-value pair, according to the mode.
 * The property set is ENTIRELY TENANT-DEFINED: there is no fixed field list anywhere in
 * this file, and every label, bound, requirement and validation pattern comes from the
 * declarations the API sends. The legacy markup could not have supplied a field list
 * even in principle — `Profile.ascx` L13-L25 declares a single
 * `<dnn:ProfileEditorControl>` whose twelve attributes are the entire specification, and
 * the control itself lives in the excluded `Library/Controls/PropertyEditor` tree.
 *
 * WHY THIS COMPONENT LOADS ITS OWN DATA
 * ------------------------------------
 * `app.routes.ts` maps `users/:userId/profile` straight to this component through
 * `loadComponent`, so there is no parent to hand it a profile. A presentational-only
 * version of this screen is not merely incomplete, it is unreachable: it would render its
 * empty state forever, for every account, with no way for an operator to get data into
 * it. It therefore obtains its own state from `core/state/user.store.ts`, which is where
 * this feature's state is specified to live, and reaches the API only through that store's
 * `core/services/user.service.ts` transport. No `HttpClient` is touched here and no
 * business rule is duplicated here.
 *
 * ONE REQUEST, NOT TWO
 * --------------------
 * The declarations arrive EMBEDDED in the profile response — `UserProfileValue` carries
 * its own `definition` — so `GET /api/v1/users/{userId}/profile` alone is enough to render
 * a field for a property the account has no value for. The separate declaration list is
 * deliberately NOT fetched: it would be a redundant round trip for data already in hand.
 * A second request is made, but for a different reason: the account itself is read so the
 * heading can name it, which is what the legacy title needed.
 *
 * THE DECLARED DATA TYPE CANNOT BE RESOLVED, SO EVERY FIELD IS TEXT
 * ----------------------------------------------------------------
 * `ProfilePropertyDefinition.dataType` is a plain integer on both sides of the boundary.
 * In the legacy schema it is a foreign key into the `Lists` table, resolved BY NAME
 * (`... WHERE ListName='DataType' AND Value='Text'`), and `Library/Components/Lists` is
 * out of scope, so the integer arrives unresolved and its value is a database-assigned
 * `EntryID` that is not stable between installations. Hard-coding an identifier-to-control
 * map would therefore be a guess that silently renders the wrong control on any
 * installation whose list seeded in a different order. Every property is consequently
 * rendered as a plain text field, which is what the legacy editor did for every type it
 * had no specialised control for. Whether that field is single-line or multi-line is
 * decided by two things and never by the type: the declared LENGTH, and membership of the
 * measured set of legacy-seeded RICH-TEXT properties in
 * {@link LEGACY_RICH_TEXT_PROPERTIES}. This is a known, reported functional reduction:
 * the thirteen legacy control kinds (time zone, locale, country, region, list, date,
 * date-time, true/false, integer, rich text, page) are not offered.
 *
 * MIGRATION: rich text is reduced to a plain multi-line text box. The legacy
 * `Biography` property used the FCK editor provider, which is out of scope, so it is
 * rendered as a `<textarea>` and no rich-text control, toolbar or trusted-HTML path is
 * substituted for it. The seeded set is what carries that decision, because the
 * installer declares Biography's length as zero — the ABSENCE of a bound — so a
 * length-driven choice alone would have given the one property that is unambiguously
 * prose a single-line box.
 *
 * MIGRATION: the legacy view page identified its subject through an ENCRYPTED query
 * parameter — `ViewProfile.ascx.vb` L61-L63 read `userticket` and passed it through
 * `UrlUtils.DecryptParameter` before `Int32.Parse`. `UrlUtils` is out of scope and the
 * target addresses the account through a plain `:userId` route segment instead. The
 * identifier is consequently visible in the address bar where it previously was not;
 * this is not the access control, which is the server's 403, and never was — the legacy
 * ticket was obfuscation, not authorisation.
 *
 * MIGRATION: the legacy update affordance was an image link pointing at
 * `~/images/save.gif` (`Profile.ascx` L33). No legacy raster asset is carried across, so
 * the action is text-only. Its wording is "Update", which resolves from the global
 * fallback `Website/App_GlobalResources/SharedResources.resx` rather than from this
 * screen's own resource file — `cmdUpdate.Text` is absent from `Profile.ascx.resx`.
 *
 * MIGRATION: the legacy collapse control carried `tabindex="-1"`, so a keyboard user
 * could not expand or collapse a category at all. It is a real `<button>` here, which
 * restores keyboard operability at no visual cost.
 */
@Component({
  selector: 'app-user-profile',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    LoadingSpinnerComponent,
    EmptyStateComponent,
    ErrorBannerComponent,
    FormFieldComponent,
  ],
  templateUrl: './user-profile.component.html',
  styleUrl: './user-profile.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserProfileComponent {

  /**
   * Registers this screen's unsaved-entry probe with the application's tracker.
   *
   * ⚠ WHY A REGISTRATION RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and
   * only one of them is a router navigation: Cancel, an in-application link and the browser's Back
   * button are navigations a route guard can refuse, while closing or reloading the tab is not, and
   * only the browser's own unload prompt covers that - which needs the dirty state at an arbitrary
   * moment rather than at a navigation. One tracker holding probes answers both, and the probe is
   * released automatically when this screen is destroyed, so a screen that has gone can never hold
   * a navigation up. Measured before this existed: a dirty form was discarded in silence by all
   * four exits, with instrumented `confirm`, `alert` and `beforeunload` recording nothing at all.
   *
   * A form that is being SAVED is not dirty in the sense that matters here - the entry is on its
   * way to the server, and prompting about it would ask the operator to confirm discarding work
   * they have already committed.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form().dirty && this.saving() === false,
  );
  private readonly store = inject(UserStore);
  private readonly notifications = inject(NotificationService);

  /**
   * The signed-in session, read for ONE fact: whether the caller is the account on screen.
   *
   * `core/state` rather than another feature's state, so this is a shared dependency rather than a
   * layering violation - the credential screen beside this one reads the same store for the same
   * predicate.
   */
  private readonly auth = inject(AuthStore);

  /**
   * The router, used for exactly one navigation: leaving this screen once a MANDATORY profile
   * completion has been written. Nothing else here navigates.
   */
  private readonly router = inject(Router);

  /**
   * The identifier of the save whose settling concludes a mandatory profile completion, or zero
   * when no such save is outstanding.
   *
   * Zero is safe as "none of mine": the store's counter pre-increments, so no real write is ever
   * issued that identifier.
   */
  private readonly awaitedSaveId = signal(0);

  /**
   * The account whose profile is edited.
   *
   * NAMED `userId` BECAUSE THE ROUTE PARAMETER IS NAMED `userId`. `app.config.ts`
   * configures the router with `withComponentInputBinding()`, which binds a route
   * parameter to a component input BY NAME, and `app.routes.ts` declares the path
   * `users/:userId/profile`. Renaming this member breaks that binding with no compile
   * error and no runtime error whatsoever — the screen would simply render with no
   * account, forever. Accepts a string because that is what a route parameter is, and a
   * number so a caller holding one need not stringify it; see {@link resolveUserId} for
   * the single normalisation point.
   */
  readonly userId = input<string | number>();

  /**
   * Which mode to render. Defaults to editing, because this route is the edit path.
   *
   * Component state rather than a route concern: there is one profile route and no
   * separate view route, so no second route is invented to carry a mode.
   */
  readonly mode = input<UserProfileMode>(DEFAULT_MODE);

  /**
   * Forces the per-property visibility control ON, whatever the tenant's policy says.
   *
   * ⚠ AN OVERRIDE FOR AN EMBEDDING CALLER, NOT THE ROUTED BEHAVIOUR. On the routed path the
   * affordance is resolved from the tenant's policy and the caller's identity — see
   * {@link visibilityOffered}, which is what the template binds — because a route supplies
   * neither. This input exists so a caller that embeds this component in a context of its own
   * can still assert the affordance, and it can only ever turn it ON: a false here defers to
   * the resolved answer rather than suppressing it.
   *
   * MIGRATION: `CType(setting, Boolean)` over an `Object` is an Option Strict OFF
   * coercion, legal only because `Website/release.config` L125 compiled the admin pages
   * with `strict="false"`. It is made explicit here: the value is a boolean input,
   * coerced by `booleanAttribute` so an attribute written without a value still means
   * true.
   *
   * The submitted payload carries each property's visibility WHETHER OR NOT the control
   * is offered, so switching the affordance off never silently rewrites a visibility the
   * account already chose. That matters because the write replaces rather than merges.
   */
  readonly manageVisibility = input(false, { transform: booleanAttribute });

  /**
   * An explicit heading, overriding the one derived from the account.
   *
   * Present so the screen can be titled by a caller that already knows the account;
   * when absent the legacy title format is used. See {@link pageTitle}.
   */
  readonly heading = input<string>();

  /** An optional supporting line for the shared page header. */
  readonly subheading = input<string>();

  /** The categories the operator has collapsed, keyed by heading. */
  private readonly collapsedKeys = signal<ReadonlySet<string>>(new Set<string>());

  /**
   * The most recent failure already announced, so one refusal is announced once.
   *
   * A plain field rather than a signal: it is written from inside an effect purely to
   * suppress a repeat, and nothing renders from it, so making it reactive would add a
   * dependency edge with nothing on the other end.
   */
  private lastAnnouncedFailure: ProblemDetails | null = null;

  /** The identifier resolved from the route, or `null` when none was supplied. */
  protected readonly resolvedUserId: Signal<number | null> = computed(() =>
    resolveUserId(this.userId()),
  );

  /**
   * Whether the address supplied a parameter that names no readable account.
   *
   * ⚠ THIS EXISTS TO STOP THE SCREEN MAKING A FALSE STATEMENT. Runtime testing reached
   * `/users/abc/profile` and was told "This site declares no profile properties, so there is
   * nothing to show" — on a tenant that declares TWELVE. The sentence is not merely unhelpful, it
   * is factually wrong about the tenant's configuration, and it sends an operator to the Profile
   * Properties screen to fix something that is not broken.
   *
   * The cause is that `hasProperties()` is false in two entirely different situations: the tenant
   * genuinely declares none, and nothing was ever FETCHED. An unreadable identifier produces the
   * second — correctly, since no request is issued for a key that cannot be parsed — and the empty
   * branch then spoke for it. Distinguishing the two is the whole fix; the empty-state branch
   * itself is right for the case it was written for.
   *
   * Presence is tested on the RAW input and readability on the parsed value, which is the same
   * three-way reading the sibling account screens use. Absent still means "no account addressed",
   * which is a different situation again and is left to the branches that already handle it.
   */
  protected readonly addressUnreadable: Signal<boolean> = computed(
    () => readRouteId(this.userId()).kind === 'unreadable',
  );

  /**
   * The sentence shown when the address names no readable account.
   *
   * ⚠ DELIBERATELY THE SAME SENTENCE `/users/abc` AND `/users/0` ALREADY SHOW, and it is the
   * measured legacy wording rather than an authored one — `ManageUsers.ascx.vb` L207/L221 pair
   * this warning with `DisableForm()`, which sets `Visible = False` on every panel. One situation
   * gets one wording across the whole account feature: an operator who follows a stale link to an
   * account and then to its profile should not be told two different stories about the same
   * missing record.
   */
  protected readonly unreadableAddressMessage: string = NO_USER_MESSAGE;

  /**
   * Whether the CALLER is the account whose profile is on screen.
   *
   * Reproduces `UserModuleBase.IsUser` (`Library/Components/Users/UserModuleBase.vb` L399-L406)
   * including its behaviour for an unauthenticated caller: the legacy property returned false
   * without comparing, and an unresolved identity resolves to false here for the same reason.
   *
   * ⚠ COMPARED WITH STRICT EQUALITY AGAINST AN EXPLICITLY RESOLVED KEY. A truthiness test would
   * report false for the account key zero, and a `?? -1` fallback would make an absent route
   * parameter match a real key.
   */
  protected readonly isSelf: Signal<boolean> = computed(() => {
    const key = this.resolvedUserId();
    const caller = this.auth.currentUser();

    if (key === null || caller === null) {
      return false;
    }

    return caller.userId === key;
  });

  /**
   * Whether the per-property visibility control is offered.
   *
   * MIGRATION: THIS IS `Profile.ascx.vb` L58-L63, REPRODUCED RATHER THAN DEFERRED. That property
   * computed `CType(UserModuleBase.GetSetting(PortalId, "Profile_DisplayVisibility"), Boolean) And
   * IsUser` — the tenant's policy AND the viewer being the subject of the profile — and both halves
   * are resolved here. An earlier revision of this component took the affordance from an input
   * alone, which no route supplies, so the routed screen never offered it whatever the tenant had
   * configured: the setting was stored, published and inert.
   *
   * ⚠ BOTH HALVES ARE REQUIRED, AND THE SECOND IS WHY AN ADMINISTRATOR NEVER SEES THE CONTROL.
   * Visibility is a choice the account holder makes about their OWN data; an administrator editing
   * somebody else's profile could otherwise silently change who can see it. The legacy hid the
   * affordance for exactly that caller, however the tenant had set the policy.
   *
   * The policy defaults to enabled when the tenant has stored nothing, which is the default the
   * server publishes (`UserModuleBase.vb` L143-L145), and it is read as FALSE while the profile is
   * still unresolved — the conservative posture, since offering a control that then disappears is
   * worse than offering it a moment late.
   *
   * ⚠ THE POLICY IS TAKEN FROM THE PROFILE, NOT FROM THE TENANT'S ACCOUNT SETTINGS, AND THAT IS A
   * FIX RATHER THAN A REARRANGEMENT. It used to be read from `store.membershipSettings()`, which is
   * fetched from `GET api/v1/users/settings` — an endpoint the server declares
   * `PolicyNames.PortalAdministrator`. The legacy rule offers this control to the SUBJECT of the
   * profile, who is by construction not an administrator, so that read was refused for precisely
   * the caller the affordance exists for: measured against the running API, an ordinary account
   * holder was answered `403 auth.not_permitted`, the policy stayed null, and this derivation could
   * never return true however the tenant had configured it. The screen rendered its own inertness.
   *
   * The fact now travels on `GET api/v1/users/{userId}/profile`, which the same caller is already
   * entitled to read and which is also exempted during mandatory remediation — so one change serves
   * the ordinary owner and the remediating caller, and it REMOVES a request rather than adding one.
   * The alternative, widening the settings endpoint, would have handed an account holder the
   * tenant's entire account policy to obtain one boolean.
   *
   * {@link manageVisibility} can force it on for an embedding caller and can never force it off.
   */
  protected readonly visibilityOffered: Signal<boolean> = computed(() => {
    if (this.manageVisibility()) {
      return true;
    }

    const profile = this.profile();

    // Presence is tested EXPLICITLY against null rather than by truthiness: an unresolved profile
    // and a profile whose policy is off are different facts, and only the second is the tenant's
    // answer. `displayVisibilityEnabled` is declared `boolean` on the contract and is decoded as a
    // required member, so reading it directly is exact rather than a truthiness test over a value
    // that could also be absent.
    return profile !== null && profile.displayVisibilityEnabled && this.isSelf();
  });

  /** The profile as the store holds it. */
  protected readonly profile: Signal<UserProfile | null> = this.store.profile;

  /** Whether the profile is still being read. */
  protected readonly loading: Signal<boolean> = this.store.profileLoading;

  /** Whether a write is in flight. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * The problem document to surface, or `null` when this screen has nothing to report.
   *
   * SCOPED TO THIS SCREEN'S OWN COMMANDS. The store records one failure at a time across
   * every command it offers, and it tags each with the command that produced it. Without
   * the tag check this screen would display a failure raised by an unrelated command —
   * a listing, a credential change — as though the profile had failed.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null) {
      return null;
    }

    if (failure.operation !== 'loadProfile' && failure.operation !== 'saveProfile') {
      return null;
    }

    return failure.problem;
  });

  /** The sections to render, grouped and ordered. */
  protected readonly sections: Signal<readonly ProfileSection[]> = computed(() =>
    toSections(this.profile()),
  );

  /** Whether there is anything at all to render. */
  protected readonly hasProperties: Signal<boolean> = computed(() => this.sections().length > 0);

  /** How many properties this profile carries, which is how many a save would submit. */
  private readonly declaredPropertyCount: Signal<number> = computed(() =>
    this.sections().reduce((total, section) => total + section.values.length, 0),
  );

  /**
   * Whether the tenant declares more properties than one write may carry.
   *
   * @see MAXIMUM_SUBMITTED_PROPERTIES
   */
  protected readonly exceedsSubmissionLimit: Signal<boolean> = computed(
    () => this.declaredPropertyCount() > MAXIMUM_SUBMITTED_PROPERTIES,
  );

  /** The sentence shown in place of the form when the declaration count cannot be saved. */
  protected readonly tooManyPropertiesNotice: Signal<string> = computed(() =>
    tooManyPropertiesMessage(this.declaredPropertyCount()),
  );

  /**
   * The screen's heading.
   *
   * MIGRATION: reproduces `ProfileTitle.Text`, measured verbatim as
   * `Edit Profile - {0} (Id: {1})` with the account name and then the identifier. The
   * legacy screen rendered this row ONLY for an administrator and hid it otherwise
   * (`Profile.ascx.vb` L155-L159), and on this administrator-only route that condition is
   * always met, so the title always renders.
   *
   * MIGRATION: `User.UserID.ToString` in the legacy format call is an implicit
   * conversion under Option Strict OFF. It is explicit here.
   *
   * Falls back to a bare heading while the account is still being read, because the
   * shared page header requires a non-blank title and an empty one would throw.
   */
  protected readonly pageTitle: Signal<string> = computed(() => {
    const supplied = this.heading()?.trim();

    if (supplied !== undefined && supplied.length > 0) {
      return supplied;
    }

    return formatProfileTitle(this.store.selectedUser(), this.resolvedUserId());
  });

  /** Whether the action row is rendered: view mode offers none. */
  protected readonly showActions: Signal<boolean> = computed(
    () => this.mode() === 'edit' && this.hasProperties(),
  );

  /**
   * The visibility choices, in the order the legacy editor offered them.
   *
   * The codes come from the shared contract rather than being spelled as bare numbers, so
   * a template never contains a magic integer. `UserVisibilityMode` itself is not among
   * the ported enumerations, which is why the shared constant object is the source.
   */
  protected readonly visibilityOptions: readonly VisibilityOption[] = Object.freeze([
    { code: PROFILE_VISIBILITY.allUsers, label: 'All users' },
    { code: PROFILE_VISIBILITY.membersOnly, label: 'Members only' },
    { code: PROFILE_VISIBILITY.adminOnly, label: 'Administrators only' },
  ]);

  /**
   * The form backing edit mode, rebuilt whenever the property set changes.
   *
   * A `computed()` rather than a field mutated from a lifecycle hook, for a change
   * detection reason rather than a stylistic one: a form rebuilt imperatively after the
   * template had already rendered would leave this on-push component with no reason to
   * check again, so the controls would exist and never appear. Deriving the form places it
   * in the same dependency graph as the markup that binds it.
   *
   * TWO RECORDS RATHER THAN ONE, because a value is a string and a visibility is a code,
   * and one `FormRecord` admits one control type. Both are keyed by the declaration
   * identifier, so a property's value and its visibility are always found the same way.
   *
   * Every control is `nonNullable`, which is what makes {@link onReset} work: resetting a
   * non-nullable control returns it to the value it was CONSTRUCTED with — the value the
   * profile arrived carrying — rather than to `null`.
   */
  protected readonly form: Signal<FormGroup<ProfileFormModel>> = computed(() => {
    const values = new FormRecord<FormControl<string>>({});
    const visibilities = new FormRecord<FormControl<ProfileVisibilityCode>>({});

    for (const section of this.sections()) {
      for (const value of section.values) {
        const key = controlKey(value);

        values.addControl(
          key,
          new FormControl<string>(initialValueFor(value), {
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

    return new FormGroup<ProfileFormModel>({ values, visibilities });
  });

  constructor() {
    // A genuine side effect: dispatching the account read, which is the HEADING's alone.
    //
    // ⚠ A SEPARATE EFFECT FROM THE PROFILE READ BECAUSE IT HAS A DEPENDENCY THE PROFILE READ
    // DOES NOT, and folding the two together made that dependency re-issue a read that was
    // already current. GET api/v1/users/{id} is NOT exempted from the remediation refusal -
    // measured, 403 auth.remediation_required - where GET api/v1/users/{id}/profile is. So this
    // read is withheld while an advisory is outstanding, and dispatched the moment one clears;
    // the profile read has no interest in that transition and must not be repeated by it.
    //
    // Dispatching it anyway used to put a refusal in the shared failure slot and an error banner
    // across the one screen the server was requiring the caller to complete. The heading falls
    // back to its neutral form in that state, which `formatProfileTitle` already resolves for an
    // absent account.
    effect(() => {
      const userId = this.resolvedUserId();

      if (userId === null || this.auth.sessionRestricted()) {
        return;
      }

      untracked(() => {
        this.store.selectUser(userId);
      });
    });

    // A genuine side effect: dispatching the profile read, which supplies the FIELDS. Re-runs when
    // the route supplies a different account, which is what makes navigating from one profile to
    // another load the second one.
    effect(() => {
      const userId = this.resolvedUserId();

      if (userId === null) {
        return;
      }

      // UNTRACKED, AND THE SCREEN IS BROKEN WITHOUT IT. The command reads store slices
      // on its way to writing them — the reads that clear the failure and raise the
      // loading flag both do, and `selectUser` in the sibling effect above compares the
      // incoming identifier against the held one. Performed inside the reactive
      // context those reads become dependencies OF THIS EFFECT, and the writes that
      // follow immediately invalidate them, so the effect re-runs and dispatches every
      // request a second time. Reading the route identifier is the only dependency this
      // effect should have, so everything else is executed outside the tracking context.
      untracked(() => {
        // The profile is read for the fields, and is read unconditionally: it is the one
        // account-scoped read the API keeps open to a caller owing mandatory remediation,
        // because this screen is where such a caller clears it.
        this.store.loadProfile(userId);
      });
    });

    // A genuine side effect: announcing a refusal the banner alone would under-state.
    effect(() => {
      const failure = this.store.failure();

      untracked(() => this.announce(failure === null ? null : failure));
    });

    // A genuine side effect: leaving this screen once a MANDATORY profile completion has been
    // written. Settled through the write's OWN identifier rather than through the shared
    // failure slot, which is the only way to settle a write in this store - see UserMutation.
    effect(() => {
      const awaited: number = this.awaitedSaveId();
      const settled: UserMutation | null = this.store.mutation();

      if (awaited === 0 || settled === null || settled.id !== awaited) {
        return;
      }

      untracked(() => {
        this.awaitedSaveId.set(0);

        // The operation is asserted as well as the identifier: a successful save re-reads the
        // profile, and that re-read has its own failure path which must not be mistaken for a
        // failed save.
        if (settled.failure !== null && settled.operation === 'saveProfile') {
          return;
        }

        // The save kept the values, so say so. Announced through the shared channel at the success
        // severity, which is where every other write in this application confirms itself.
        //
        // MIGRATION: honestly net-new. The legacy screen carried no success wording for this
        // action - `Website/admin/Users/Profile.ascx.vb` shows no module message on the success
        // path and its resources declare none - because a full-page postback was itself the
        // feedback: the page visibly reloaded carrying the stored values. Nothing reloads here, so
        // without this the only difference between a save that worked and one that was ignored was
        // the absence of an error.
        // ⚠ THE FORM IS SETTLED HERE EXPLICITLY, AND NOT LEFT TO THE REBUILD. The form is a `computed()`
        // over {@link sections}, and a successful save re-reads the profile - so a NEW, pristine form is
        // normally built when that read lands, and marking here is then a no-op. Three cases are not
        // covered by the rebuild, and the third is the serious one. The re-read is asynchronous while this
        // block is synchronous, so for the interval between them the old, dirty form is still the current
        // one. A re-read that fails leaves it current permanently, so a SAVED profile would keep
        // advertising unsaved work for the rest of the screen's life. And the mandatory-completion path
        // below navigates in THIS task, before any re-read can have landed - so the unsaved-entry guard,
        // whose probe reads `form().dirty && saving() === false` and whose flag has already fallen, would
        // refuse that navigation with a prompt asking the operator to discard the very entry that had just
        // been stored. `window.confirm` blocks the JavaScript thread, so the auto-dismiss timer on the
        // confirmation raised below would become due while the dialog stood and fire the instant it was
        // accepted, which is how a redundant prompt swallows the answer to itself.
        //
        // Depending on a value CHANGING to clear a flag is the same mistake the portal settings screen
        // records on its own save path; the honest statement is made here, where the outcome is known.
        const settledForm = this.form();
        settledForm.markAsPristine();
        settledForm.markAsUntouched();

        this.notifications.success(PROFILE_SAVED_MESSAGE);

        // ⚠ ONLY A MANDATORY COMPLETION OF THE CALLER'S OWN PROFILE NAVIGATES. Re-read at settle
        // time rather than remembered from dispatch, which is sound because the restriction is
        // held in the session and is cleared by `concludeRemediation` itself - so it is still
        // standing at this point for exactly the callers it applies to.
        if (this.auth.sessionRestricted() && this.isSelf()) {
          this.concludeRemediation();
        }
      });
    });
  }

  /**
   * Reports whether a section is collapsed.
   *
   * @param key The section key.
   * @returns `true` when the section's fields are hidden.
   */
  protected isCollapsed(key: string): boolean {
    return this.collapsedKeys().has(key);
  }

  /**
   * Collapses an expanded section, or expands a collapsed one.
   *
   * The set is REPLACED rather than mutated, so the signal notifies its readers.
   *
   * @param key The section key.
   */

  /**
   * The DOM id of one section's BODY, for the toggle's `aria-controls`.
   *
   * Distinct from {@link the toggle itself}, which names the toggle itself; a control cannot
   * point `aria-controls` at its own id and expect assistive technology to find the region.
   * The reference is published ONLY while the section is open — the body is removed from the
   * document when collapsed, so a constant attribute would leave a dangling IDREF that an
   * auditing tool reports as an error. Binding it to `null` in the closed state removes the
   * attribute outright, so the id is asserted exactly when it resolves.
   *
   * @param section The section whose body is being named.
   * @returns A stable id, unique within the screen.
   */
  protected sectionBodyId(section: string): string {
    return `user-profile-body-${section}`;
  }

  protected toggleSection(key: string): void {
    const next = new Set(this.collapsedKeys());

    if (next.has(key)) {
      next.delete(key);
    } else {
      next.add(key);
    }

    this.collapsedKeys.set(next);
  }

  /**
   * Returns the control holding a property's value.
   *
   * @param value The property.
   * @returns The control, or `null` when the form holds none for it.
   */
  protected valueControl(value: UserProfileValue): FormControl<string> | null {
    return this.form().controls.values.controls[controlKey(value)] ?? null;
  }

  /**
   * Returns the control holding a property's visibility.
   *
   * @param value The property.
   * @returns The control, or `null` when the form holds none for it.
   */
  protected visibilityControl(value: UserProfileValue): FormControl<ProfileVisibilityCode> | null {
    return this.form().controls.visibilities.controls[controlKey(value)] ?? null;
  }

  /**
   * The document-unique identifier of a property's control.
   *
   * @param value The property.
   * @returns The identifier.
   */
  protected controlId(value: UserProfileValue): string {
    return `profile-property-${controlKey(value)}`;
  }

  /**
   * The document-unique identifier of a property's visibility control.
   *
   * @param value The property.
   * @returns The identifier.
   */
  protected visibilityId(value: UserProfileValue): string {
    return `${this.controlId(value)}-visibility`;
  }

  /**
   * The label for a property.
   *
   * A legacy-seeded property gets the wording the operator already knows; anything a
   * tenant added gets its own name. The trailing colon the resource values carry is left
   * in place deliberately — the shared form field strips exactly one trailing colon
   * itself, so passing the value through unedited keeps this file's copy byte-identical
   * to the resource file it was measured from.
   *
   * @param value The property.
   * @returns The label text.
   */
  protected labelFor(value: UserProfileValue): string {
    return LEGACY_PROFILE_WORDING[value.definition.propertyName]?.label ?? value.definition.propertyName;
  }

  /**
   * The help text for a property, or the empty string when there is none.
   *
   * @param value The property.
   * @returns The help text.
   */
  protected helpFor(value: UserProfileValue): string {
    return LEGACY_PROFILE_WORDING[value.definition.propertyName]?.help ?? '';
  }

  /**
   * Whether a property is rendered with a multi-line control.
   *
   * TWO INPUTS, AND THEY ANSWER TWO DIFFERENT QUESTIONS. A property the installer seeded
   * as RICH TEXT is prose by declaration — see {@link LEGACY_RICH_TEXT_PROPERTIES} — and a
   * property the tenant gave a large declared bound is prose by size. Either is enough.
   *
   * The seeded test comes first because it is the one the length test cannot answer: the
   * installer passes a declared length of zero for every specialised data type, so the
   * length rule alone renders the seeded rich-text property single-line. Both remain
   * PRESENTATION ONLY — the same string is submitted either way, and the same validators
   * apply.
   *
   * @param value The property.
   * @returns `true` when a multi-line control is used.
   */
  protected isMultiline(value: UserProfileValue): boolean {
    return (
      LEGACY_RICH_TEXT_PROPERTIES.has(value.definition.propertyName) ||
      value.definition.length > MULTILINE_LENGTH_THRESHOLD
    );
  }

  /** The number of rows a multi-line control is given. */
  /**
   * How many rows one multi-line control renders — U-M6.
   *
   * ⚠ SIZED FROM THE DECLARATION RATHER THAN FIXED AT FOUR, which is the whole of this fix. Every
   * multi-line control used to render exactly four rows whatever it could hold, so a declaration bounded
   * at four thousand characters showed 2.88 per cent of its capacity at a 320-unit viewport — a stored
   * answer read through a slot, with the operator's only recourse being to scroll inside a control four
   * lines tall.
   *
   * The result is clamped at both ends and the reasons differ: the floor keeps a declaration just over
   * the multi-line threshold from rendering a control that is mostly empty, and the ceiling keeps an
   * unbounded declaration from rendering a control taller than the screen and pushing the save command
   * out of reach. The declared length has no maximum of its own, so the ceiling is not optional.
   *
   * A declared length of ZERO means no bound at all — the schema defaults it to zero and that is the
   * absence of a bound, not a bound of nothing, which is the same rule the length validator and the
   * published attribute both follow — so it takes the ceiling rather than the floor: a field that can
   * hold anything is the case that most needs the room.
   *
   * @param value The property being rendered.
   * @returns The row count for its control.
   */
  protected multilineRowsFor(value: UserProfileValue): number {
    const declared: number = value.definition.length;

    if (declared <= 0) {
      return MULTILINE_MAXIMUM_ROWS;
    }

    const wanted: number = Math.ceil(declared / DECLARED_CHARACTERS_PER_ROW);

    return Math.min(MULTILINE_MAXIMUM_ROWS, Math.max(MULTILINE_MINIMUM_ROWS, wanted));
  }

  /** The wording shown when the tenant has declared no profile property. */
  protected readonly noPropertiesMessage: string = NO_PROPERTIES_MESSAGE;

  /**
   * The maximum length to publish to the browser, or `null` when none is declared.
   *
   * The same zero-means-no-bound rule that governs the validator governs the attribute:
   * emitting `maxlength="0"` would stop the operator typing at all.
   *
   * @param value The property.
   * @returns The bound, or `null`.
   */
  protected maxLengthFor(value: UserProfileValue): number | null {
    return value.definition.length > 0 ? value.definition.length : null;
  }

  /**
   * Every message to show beneath a property, client-side and server-side together.
   *
   * The shared form field accepts a list and renders each entry, so the two sources are
   * concatenated rather than one being made to win. Client messages come first because
   * they describe what the operator can fix without another request.
   *
   * A client message appears only once the operator has touched or changed the control, so
   * a freshly opened form is not covered in complaints about values nobody has been given
   * a chance to supply. A server message appears as soon as it arrives, because by then a
   * request has already been refused.
   *
   * @param value The property.
   * @returns The messages, empty when there are none.
   */
  protected errorsFor(value: UserProfileValue): readonly string[] {
    const messages: string[] = [];
    const control = this.valueControl(value);

    if (control !== null && control.invalid && (control.dirty || control.touched)) {
      messages.push(this.clientMessageFor(value, control));
    }

    messages.push(...this.serverMessagesFor(value));

    return messages;
  }

  /**
   * Whether a property currently has a message against it.
   *
   * Drives `aria-invalid` on the control. The shared form field owns the error REGION and
   * gives it `role="alert"`, but it cannot mark the control itself: the control is
   * projected content and only its owner knows whether it is valid. Without this, a
   * screen-reader user hears the message announced and then finds nothing on the field
   * saying it is the one at fault.
   *
   * @param value The property.
   * @returns `true` when at least one message is showing.
   */
  protected hasError(value: UserProfileValue): boolean {
    return this.errorsFor(value).length > 0;
  }

  /**
   * The text shown for a property in view mode.
   *
   * A recorded value wins; when nothing has ever been recorded the declared default is
   * shown, because that is what the account is treated as having. When neither exists the
   * field renders as nothing rather than as a placeholder glyph, which would be announced
   * as content.
   *
   * @param value The property.
   * @returns The text to render.
   */
  protected displayValue(value: UserProfileValue): string {
    return initialValueFor(value);
  }

  /**
   * Submits the form.
   *
   * MIGRATION: THE LEGACY ADMINISTRATOR VALIDATION BYPASS IS DELIBERATELY NOT
   * REPRODUCED. `Profile.ascx.vb` L94-L104 reads
   * `If ProfileProperties.IsValid Or IsAdmin Then _IsValid = True`, so an administrator
   * was considered valid unconditionally and every declared rule — required, length,
   * pattern — was skipped for them. Because THIS route is administrator-only, reproducing
   * that would make client-side validation entirely vacuous on the only screen that has
   * it, which directly contradicts the Minimal Change Clause's requirement that
   * validation rules must match. The declared rules are therefore applied to every
   * operator and an invalid form is not submitted. Nothing is weakened by this: the server
   * validates independently and answers `400` with a problem document regardless, so the
   * change makes the client agree with the server rather than disagree with it.
   *
   * An invalid form marks every control as touched instead of submitting, which is what
   * makes messages appear for controls the operator never visited.
   */
  protected onSubmit(): void {
    if (this.saving()) {
      return;
    }

    // ⚠ CHECKED HERE AS WELL AS IN THE TEMPLATE, and not because the template's branch is untrusted: a
    // submission can be raised by the return key on a form the branch is not currently withholding —
    // during a re-read, for instance — and the write replaces the whole profile, so a refusal is the only
    // safe answer. Stated at both ends because the cost is one comparison and the failure is a whole
    // profile the operator believes they saved.
    if (this.exceedsSubmissionLimit()) {
      this.notifications.warning(this.tooManyPropertiesNotice());

      return;
    }

    const form = this.form();

    if (form.invalid) {
      form.markAllAsTouched();

      return;
    }

    const userId = this.resolvedUserId();
    const profile = this.profile();

    if (userId === null || profile === null) {
      return;
    }

    const dispatched: number = this.store.saveProfile(userId, this.toSubmission(userId));

    // ⚠ EVERY SAVE IS NOW AWAITED, NOT ONLY A MANDATORY COMPLETION, and that is what fixes a
    // silent success. Awaiting only the remediation case meant an ordinary save settled with
    // nothing watching: measured, `PUT /api/v1/users/1/profile` answered `204`, the value DID
    // persist, and the notification region stayed empty for a polled six seconds with no
    // `[role=alert]` anywhere - so the operator received no confirmation whatsoever that their
    // change had been kept. The identical action on the site-settings screen does confirm.
    //
    // What remains conditional is the NAVIGATION, not the awaiting: concluding a remediation is
    // still reserved for a caller completing their OWN mandatory profile, which is decided at
    // settle time. An administrator editing somebody else's profile has nothing of their own to
    // conclude, and an ordinary caller editing their own profile stays where they are - which is
    // what the legacy screen did.
    this.awaitedSaveId.set(dispatched);
  }

  /**
   * Concludes a MANDATORY profile completion and hands the caller onward.
   *
   * ⚠ WITHOUT THIS THE JOURNEY NEVER ENDS. The advisory the caller has just satisfied is
   * carried in the HELD SESSION rather than recomputed by the client, so the server stops
   * requiring the completion the moment the required values are written while this client goes
   * on believing it is outstanding — and the root redirect goes on resolving back to this
   * screen.
   *
   * ⚠ THE ADVISORY IS CLEARED LOCALLY AND THE SESSION IS DELIBERATELY *NOT* RENEWED, even
   * though this screen's own write revokes nothing. A caller can owe both advisories, in which
   * case the credential change came first and has already revoked every refresh token the
   * account holds, so a renewal here would answer `401` and sign the caller out at the end of a
   * journey they had just completed. The full reasoning, and why the write's own success makes
   * the local assertion sound rather than a guess, is on
   * {@link AuthStore.noteProfileRemediated}.
   *
   * The destination is the application ROOT rather than a screen, so the root redirect keeps
   * sole ownership of where a remediated caller goes next.
   */
  private concludeRemediation(): void {
    this.auth.noteProfileRemediated();

    /*
     * ⚠ REPLACES RATHER THAN PUSHES, for two reasons that agree.
     *
     * The first is the convention every post-success departure in this application follows:
     * leaving BACK pointing at a screen whose work is already done invites the operator to
     * return to it and repeat the submission.
     *
     * The second is that `core/guards/unsaved-changes.guard.ts` reads exactly this flag to tell
     * a departure the APPLICATION initiated from one the OPERATOR initiated. Without it, the
     * gate would see a dirty form leaving on an operator-initiated navigation and ask whether to
     * discard changes that had just been saved successfully — turning a completed remediation
     * into a prompt suggesting the work was about to be lost.
     */
    void this.router.navigateByUrl('/', { replaceUrl: true }).catch(() => false);
  }

  /**
   * Restores every control to the value the profile arrived carrying.
   *
   * Possible in one call precisely because every control is `nonNullable`: resetting one
   * returns it to its construction value rather than to `null`.
   */
  protected onReset(): void {
    this.form().reset();
  }

  /**
   * Builds the payload for a write.
   *
   * EVERY declared property is carried, not merely the changed ones. The write replaces
   * rather than merges, so a property omitted from the payload is a property cleared —
   * sending a difference would erase everything left out of it.
   *
   * MIGRATION: each property's visibility is carried whether or not the visibility control
   * was offered, taken from the control when there is one and from the value the profile
   * arrived with otherwise. The alternative — defaulting it — would silently reset every
   * property's visibility to the tenant default on any save made while the affordance was
   * switched off.
   *
   * @param userId The account being written.
   * @returns The submission.
   */
  private toSubmission(userId: number): UserProfileSubmission {
    const properties = this.sections().flatMap((section) =>
      section.values.map((value) => ({
        propertyDefinitionId: value.definition.propertyDefinitionId,
        propertyValue: this.valueControl(value)?.value ?? '',
        visibility: this.visibilityControl(value)?.value ?? value.visibility,
      })),
    );

    return { userId, properties };
  }

  /**
   * The client-side message for an invalid control.
   *
   * The required message is the one the legacy resource file carried for that property,
   * which is why the three measured divergences between a label and its message survive.
   * The length and pattern messages are authored: the legacy ones came from the excluded
   * property-editor control's own resource files, not from this screen's, so there is no
   * legacy string to preserve and saying so is more honest than inventing a provenance.
   *
   * @param value The property.
   * @param control The property's control.
   * @returns One message.
   */
  private clientMessageFor(value: UserProfileValue, control: FormControl<string>): string {
    const name = value.definition.propertyName;

    if (control.hasError('required')) {
      return LEGACY_PROFILE_WORDING[name]?.required ?? `${name} is required`;
    }

    if (control.hasError('maxlength')) {
      return `${name} must be ${value.definition.length} characters or fewer`;
    }

    return `${name} is not valid`;
  }

  /**
   * The server's messages for one property, from the current problem document.
   *
   * BOTH SPELLINGS OF THE KEY ARE PROBED, because the two sides of the boundary disagree
   * about casing and neither is wrong. A .NET model-state key is Pascal-cased on the wire
   * — `FirstName` — while the shared extractor lower-cases the first character so a key
   * matches a client control name, yielding `firstName`. Probing both means a message is
   * found whichever spelling the server used, and the raw document is read with a bracket
   * because property access on an index signature is a compile error in this workspace.
   *
   * @param value The property.
   * @returns The messages, empty when the document names no error for it.
   */
  private serverMessagesFor(value: UserProfileValue): readonly string[] {
    const problem = this.problem();

    if (problem === null) {
      return [];
    }

    const name = value.definition.propertyName;
    const camelCased = name.length === 0 ? name : name.charAt(0).toLowerCase() + name.slice(1);
    const normalised = problemDetailsFieldErrors(problem);
    const raw = problem.errors;

    return (
      normalised[camelCased] ??
      normalised[name] ??
      raw?.[name] ??
      raw?.[camelCased] ??
      []
    );
  }

  /**
   * Announces a failure once, at the severity the shared summariser decided.
   *
   * MIGRATION: A PERMISSION REFUSAL IS A WARNING, NOT AN ERROR, and the severity is taken
   * from the summary rather than decided again here. `Website/admin/Security/AccessDenied.ascx.vb`
   * renders at `ModuleMessageType.YellowWarning` in BOTH branches of its load handler
   * (L43 and L45), and the shared summariser already returns the warning severity for that
   * status, so asking it is what keeps the two from drifting apart.
   *
   * The message is passed through as plain text and is never treated as markup. That is a
   * safety property, not a preference: measured across the thirty-seven in-scope resource
   * files, seventy-six values carry a raw HTML tag and four carry a live `script` element,
   * so legacy message text is untrusted markup by measurement. The legacy code knew it —
   * `AccessDenied.ascx.vb` L43 encoded the message it had just decoded before showing it.
   *
   * @param failure The failure to announce, or `null` when there is none.
   */
  private announce(failure: UserFailure | null): void {
    if (failure === null) {
      this.lastAnnouncedFailure = null;

      return;
    }

    if (failure.operation !== 'loadProfile' && failure.operation !== 'saveProfile') {
      return;
    }

    // Only a refusal is announced. A validation failure is already shown beside the field
    // it belongs to, and announcing it again would say the same thing twice.
    //
    // The test is for the ERROR severity rather than for the warning one, and the
    // difference is not cosmetic. The shared classifier resolves a rate-limit refusal to
    // the informational severity — quieter than the other refusals, because the caller is
    // early rather than wrong — so a test that admitted only warnings would say nothing at
    // all when a save was throttled, which is the one case where the operator most needs
    // to be told to wait. Every validation failure is an error and is still excluded.
    if (failure.summary.severity === 'error') {
      this.lastAnnouncedFailure = failure.problem;

      return;
    }

    if (this.lastAnnouncedFailure === failure.problem) {
      return;
    }

    this.lastAnnouncedFailure = failure.problem;

    // Announced at the severity the shared classifier decided, never at a severity chosen
    // here: a second choice is a second authority, and the two would drift.
    // ⚠ THE SUPPORT REFERENCE TRAVELS WITH IT. The summary has carried a `supportReference` member all
    // along, and dropping it here threw away the only join key between what an operator saw in the
    // browser and the request as the server recorded it - the correlation identifier the server
    // validated, which is what appears on the response header, on the request envelope in its log and on
    // every audit event the request produced. A browser audit measured the asymmetry: a refusal presented
    // through the shared banner read `Reference: <id>`, while the same class of refusal presented as a
    // notification read nothing an operator could quote. The notification surface appends it AFTER its own
    // message bound, so a long server sentence cannot truncate the identifier away, and a document that
    // carried none resolves to null and is simply not quoted.
    this.notifications.notify(
      failure.summary.severity,
      failure.summary.message,
      failure.summary.supportReference,
    );
  }
}

/**
 * The key a property's controls are registered under.
 *
 * The declaration identifier rather than the property name: `PropertyDefinitionID` is
 * `IDENTITY(1,1)` and therefore unique, whereas a name is unique only per tenant and per
 * module definition under the schema's `(PortalID, ModuleDefID, PropertyName)` index, so
 * two declarations could legitimately share a name.
 *
 * @param value The property.
 * @returns The record key.
 */
function controlKey(value: UserProfileValue): string {
  return String(value.definition.propertyDefinitionId);
}

/**
 * Formats the screen heading in the legacy title's shape.
 *
 * @param user The account, or `null` while it is still being read.
 * @param userId The identifier resolved from the route, or `null`.
 * @returns A non-blank heading.
 */
function formatProfileTitle(user: UserDetail | null, userId: number | null): string {
  if (user === null || userId === null || user.userId !== userId) {
    return 'Edit Profile';
  }

  return `Edit Profile - ${user.username} (Id: ${String(user.userId)})`;
}
