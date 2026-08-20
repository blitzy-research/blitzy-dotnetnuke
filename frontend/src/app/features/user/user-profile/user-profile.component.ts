import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  type Signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { Router } from '@angular/router';
import {
  FormControl,
  FormGroup,
  FormRecord,
  ReactiveFormsModule,
  Validators,
  type AbstractControl,
  type ValidationErrors,
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
import {
  compileTenantPattern,
  matchesTenantPattern,
} from '../../../core/utils/tenant-pattern.util';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import {
  UnsavedChangesTracker,
  confirmDiscardUnsavedChanges,
} from '../../../core/guards/unsaved-changes.guard';

/** Which of the screen's two modes is rendered. */
export type UserProfileMode = 'edit' | 'view';

/** The mode the admin route opens in: this is the edit path. */
const DEFAULT_MODE: UserProfileMode = 'edit';

/**
 * The heading used for a property whose category is blank. The column is `PropertyCategory nvarchar(50)
 * NOT NULL` (`04.00.04.SqlDataProvider` L1107+) so it is never absent, but `NOT NULL` does not forbid the
 * empty string and the legacy null contract spelled absent text AS the empty string
 * (`Library/Components/Shared/Null.vb` L71-L73, `NullString` returns `""`).
 */
const UNCATEGORISED_HEADING = 'General';

/** The declared length above which a property is rendered multi-line. */
const MULTILINE_LENGTH_THRESHOLD = 250;

const MULTILINE_MINIMUM_ROWS = 4;

/** The most rows a multi-line control ever renders — U-M6. */
const MULTILINE_MAXIMUM_ROWS = 12;

/**
 * Declared characters per rendered row, for sizing a multi-line control — U-M6. An ESTIMATE and labelled
 * as one: the real figure depends on the control's resolved width, the type step and the text itself,
 * none of which is knowable when the attribute is written.
 */
const DECLARED_CHARACTERS_PER_ROW = 120;

/** The legacy-seeded properties the installer gave the RICH-TEXT data type. */
const LEGACY_RICH_TEXT_PROPERTIES: ReadonlySet<string> = new Set(['Biography']);

/**
 * What is shown when the tenant has declared no profile property at all. A legitimate configuration
 * rather than a failure, so it is an empty state and not an error.
 */
const NO_PROPERTIES_MESSAGE =
  'This site declares no profile properties, so there is nothing to show. ' +
  'Define one under Profile Properties to begin.';

/**
 * Shown once a profile write succeeds. MIGRATION: net-new wording, with no legacy counterpart, and the
 * absence of one is explained by the legacy mechanism rather than by an omission.
 */
const PROFILE_SAVED_MESSAGE = 'The profile was saved.';

/** Stated beside the actions while the write itself is outstanding. */
const SAVING_MESSAGE = 'Saving the profile…';

/**
 * Stated while the read that CONFIRMS a save is outstanding. Worded as a confirmation rather than as a load,
 * because the fields are still on screen and still hold what was submitted: a "Loading profile…" sentence
 * beside visible, populated fields would describe a screen the operator is not looking at.
 */
const CONFIRMING_MESSAGE = 'Confirming the stored profile…';

/** The sentence shown when the address names no readable account. */
const NO_USER_MESSAGE = "This account doesn't exist";

/**
 * The greatest number of properties one profile write may carry. THE API'S OWN BOUND, reproduced from
 * `UserService.ProfilePropertySubmissionMaximum`, where a submission carrying more is refused whole with
 * `user.profile.too-many-properties`.
 */
const MAXIMUM_SUBMITTED_PROPERTIES = 64;

/**
 * What is shown when the tenant declares more properties than one write may carry.
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

/** The wording a single legacy-seeded property carried. */
interface LegacyWording {
  /** The label, exactly as the resource file spelled it, colon included. */
  readonly label: string;

  /** The help text, exactly as the resource file spelled it. */
  readonly help: string;

  /** The message shown when a required value is missing. */
  readonly required: string;
}

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
    help: 'Use the Editor to enter a short Biography',
    required: 'Biography is required',
  },
});

/** The empty-option wording a list-backed property would carry. */
export const NOT_SPECIFIED_OPTION_TEXT = 'Not Specified';

/**
 * The serialised forms of the legacy null-date sentinel. `Library/Components/Shared/Null.vb` L66-L68
 * defines `NullDate` as `Date.MinValue`, and that sentinel SURVIVES ON THE WIRE: the API serialises with
 * the ignore condition set to never, so a date that was never set arrives as a real value rather than
 * being omitted.
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

/** One category of profile properties, as the template consumes it. */
export interface ProfileSection {
  /** A stable key, used for tracking and for the collapse state. */
  readonly key: string;

  /** The heading rendered in the group's legend. */
  readonly heading: string;

  /** The properties in this category, ordered by their declared view order. */
  readonly values: readonly UserProfileValue[];
}

/** One selectable visibility, as the template consumes it. */
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
 * Builds the validators one declared property demands. The three rules are the three the declaration
 * carries, and they are applied in the order the legacy editor applied them.
 *
 * @param definition The tenant's declaration for the property.
 * @returns The validators to attach to that property's value control.
 */
function validatorsFor(definition: ProfilePropertyDefinition): ValidatorFn[] {
  const validators: ValidatorFn[] = [];

  if (definition.required) {
    validators.push(requiredText);
  }

  if (definition.length > 0) {
    validators.push(Validators.maxLength(definition.length));
  }

  // THE DECLARED FORMAT, CHECKED HERE WHENEVER IT IS SAFE TO DO SO.
  //
  // ⚠ THIS USED TO BE SKIPPED ENTIRELY, and the comment that stood here justified the omission by saying the
  // rule was "still reported per field through the server's model-state message". Measured, it was not: the
  // server's refusal was published as a FLAT problem document with no `errors` member, so there was nothing
  // for the screen to attach to a control — no field was marked invalid, no message appeared beside a box and
  // focus never moved. An operator typing `not a url` into a Website property was told nothing at all.
  //
  // Both halves of that are now fixed, and this is the half that gives immediate feedback. The safety concern
  // the omission was protecting against is real and is NOT waved away — `compileTenantPattern` returns `null`
  // for any expression that could backtrack catastrophically, and those are left to the server, which runs
  // them under a real timeout on a linear-time engine. So the browser checks the expressions administrators
  // actually write, and declines to run the ones that could freeze a tab.
  const pattern =
    definition.validationExpression === null ? null : compileTenantPattern(definition.validationExpression);

  if (pattern !== null) {
    validators.push((control: AbstractControl): ValidationErrors | null =>
      matchesTenantPattern(pattern, typeof control.value === 'string' ? control.value : '')
        ? null
        : { tenantPattern: true },
    );
  }

  return validators;
}

/**
 * The value a property's control starts at. MIGRATION: THIS IS WHERE "NEVER SET" IS DISTINGUISHED FROM
 * "SET TO EMPTY", which the legacy code could not do.
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
  if (value.definition.length > 0 && seeded.length > value.definition.length) {
    return '';
  }

  return seeded;
}

/**
 * Whether one property is withheld from the account's own owner - U13. `required` is the exception, and
 * it is the ONE case where the declaration is deliberately overruled.
 *
 * ⚠ WITHOUT THE EXCEPTION THIS FIX WOULD LOCK AN ACCOUNT OUT PERMANENTLY. The remediation gate is
 * `ProfileController.ValidateProfile` (`Library/Components/Users/Profile/ProfileController.vb` L305-L319),
 * reproduced faithfully by `UserService.RequiresProfileCompletionAsync`, and BOTH test `Required` alone
 * with no reference to `Visible`. So a property declared required and not visible made
 * `ValidateProfile` return `False` for ever while `Profile.ascx.vb` rendered no control to satisfy it -
 * a deadlock legacy shipped. Honouring the declaration without this exception would reproduce it.
 *
 * @param value The property and its declaration.
 * @returns `true` when the owner is not shown the property.
 */
function isWithheldFromOwner(value: UserProfileValue): boolean {
  if (value.definition.visible) {
    return false;
  }

  // The site is gating on this value, so it is shown whatever the visibility says.
  if (value.definition.required && initialValueFor(value).trim().length === 0) {
    return false;
  }

  return true;
}

/**
 * States the rules a declaration carries, so they are known BEFORE a save is refused for breaking them -
 * U12 and U15. A tenant-declared property carries no curated wording, so it previously rendered no help
 * affordance at all even though its declaration bounded its length and could demand a format.
 *
 * The format rule is stated rather than applied here. `UserService.ValidateProfileValue` enforces it
 * server-side through a length-bounded, well-formedness-checked, {@link RegExp}-cached matcher with a
 * 50ms timeout, and a tenant-authored expression must not be handed to the browser's own matcher where
 * no such timeout exists.
 *
 * @param definition The tenant's declaration.
 * @returns The sentence, or the empty string when the declaration bounds nothing.
 */
function declaredConstraintsSentence(definition: ProfilePropertyDefinition): string {
  // ⚠ THE DECLARED LENGTH IS DELIBERATELY NO LONGER STATED HERE, AND MOVING IT IS THE FIX RATHER THAN A
  // TIDY-UP. This sentence reaches the reader through the field's HELP text, which is collapsed behind a
  // disclosure - so the one constraint that bites while typing was the one constraint a person had to go
  // looking for. `maxlength` refuses keystrokes and keeps only the head of a longer pasted value without
  // saying anything, so a reader who never opened help lost the tail of an address and was told nothing.
  // The shared field's `limit` input exists for exactly this: it renders the bound as a permanently
  // present sentence that the control's own `aria-describedby` names, announced when the box takes focus,
  // and repeats it inside the disclosure anyway. The length therefore travels through `[limit]`, and what
  // remains here is the FORMAT requirement, which no native attribute enforces and which nothing else
  // would state.
  if ((definition.validationExpression ?? '').trim().length === 0) {
    return '';
  }

  return 'Accepts a specific format this site requires.';
}

/**
 * Orders two properties by their declared view order. `ViewOrder int NOT NULL` is the tenant's chosen
 * display order and the declaration marks it required.
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
 * ⚠ U13 - WHICH PROPERTIES ARE RENDERED DEPENDS ON WHO IS LOOKING, and that is the legacy rule rather
 * than a relaxation of it. `Profile.ascx.vb` L164-L168 forced `profProperty.Visible = True` for an
 * administrator and left the declaration alone for everyone else, and the declaration then reached
 * `FieldEditorControl.Visible` (L963), which is an ASP.NET server control property - so a `Visible =
 * False` property rendered NOTHING for the account's own owner. `PropertyEditorControl` L270 and L293
 * confirm the rest of the contract: an invisible editor was neither considered dirty nor validated.
 *
 * @param profile The profile to group, or `null` when none has been read.
 * @returns The sections to render, in order.
 */
function toSections(
  profile: UserProfile | null,
  withheld: ReadonlySet<number>,
): readonly ProfileSection[] {
  if (profile === null) {
    return [];
  }

  const ordered = [...profile.properties]
    .filter((value) => !withheld.has(value.definition.propertyDefinitionId))
    .sort(compareByViewOrder);
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
 * Resolves the account identifier the route supplied. SENTINEL DISCIPLINE.
 * `Library/Components/Shared/Null.vb` L41-L43 defines `NullInteger` as `-1`, and `ViewProfile.ascx.vb`
 * L60 opened by assigning exactly that to `UserId` so that a request carrying no ticket failed closed.
 *
 * @param raw The value the route or a caller supplied.
 * @returns The identifier, or `null` when none was supplied or it was not a whole number.
 */
function resolveUserId(raw: string | number | undefined): number | null {
  return parseRouteId(raw);
}

/**
 * Why the caller is on this screen when they did not choose to be. Authored inline in English, like every
 * other user-facing string in this workspace.
 *
 * It names the obligation, states that the rest of the site is waiting on it, and says what happens when it
 * is met - which is what makes the state legible as a TASK rather than as a permanent condition.
 */
export const PROFILE_REMEDIATION_EXPLANATION =
  'This site requires some profile details before you can use the rest of it. Fill in the fields marked as'
  + ' required and save; everything else becomes available straight away.';

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
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   *
   * ⚠ THE BUSY EXCLUSION WAS REMOVED, AND ITS REMOVAL CLOSES A MEASURED HOLE. This predicate used to read
   * `dirty && busy === false`, which reported the screen CLEAN for exactly as long as a write was in flight -
   * so navigating away mid-save was admitted in silence, the departure destroyed the component, and
   * `takeUntilDestroyed` cancelled the request. The operator lost the write and was told nothing. A form
   * holding an unfinished write is the LEAST safe moment to leave, not the safest.
   *
   * The exclusion was written to stop the application's OWN post-save navigation being challenged, and that
   * case is already covered properly: every success path replaces the address imperatively, which
   * `unsavedChangesGuard` admits explicitly. Nothing here has to approximate it a second time.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form().dirty,
  );
  private readonly store = inject(UserStore);
  private readonly notifications = inject(NotificationService);

  /** This screen's own element, so a focus move can be scoped to the controls it actually rendered. */
  private readonly host = inject(ElementRef);

  /**
   * The signed-in session, read for ONE fact: whether the caller is the account on screen. `core/state`
   * rather than another feature's state, so this is a shared dependency rather than a layering violation
   * - the credential screen beside this one reads the same store for the same predicate.
   */
  private readonly auth = inject(AuthStore);

  /** The document, used only to bring one named control into view - U12. */
  private readonly document = inject(DOCUMENT);

  /**
   * The router, used for exactly one navigation: leaving this screen once a MANDATORY profile completion
   * has been written.
   */
  private readonly router = inject(Router);

  /**
   * The identifier of the save whose settling concludes a mandatory profile completion, or zero when no
   * such save is outstanding.
   */
  private readonly awaitedSaveId = signal(0);

  /** The account whose profile is edited. */
  readonly userId = input<string | number>();

  /** Which mode to render. */
  readonly mode = input<UserProfileMode>(DEFAULT_MODE);

  /**
   * Forces the per-property visibility control ON, whatever the tenant's policy says. ⚠ AN OVERRIDE FOR
   * AN EMBEDDING CALLER, NOT THE ROUTED BEHAVIOUR. On the routed path the affordance is resolved from the
   * tenant's policy and the caller's identity — see {@link visibilityOffered}, which is what the template
   * binds — because a route supplies neither.
   */
  readonly manageVisibility = input(false, { transform: booleanAttribute });

  /** An explicit heading, overriding the one derived from the account. */
  readonly heading = input<string>();

  /** An optional supporting line for the shared page header. */
  readonly subheading = input<string>();

  /** The categories the operator has collapsed, keyed by heading. */
  private readonly collapsedKeys = signal<ReadonlySet<string>>(new Set<string>());

  /** The most recent failure already announced, so one refusal is announced once. */
  private lastAnnouncedFailure: ProblemDetails | null = null;

  /** The identifier resolved from the route, or `null` when none was supplied. */
  protected readonly resolvedUserId: Signal<number | null> = computed(() =>
    resolveUserId(this.userId()),
  );

  /**
   * Whether the address supplied a parameter that names no readable account. ⚠ THIS EXISTS TO STOP THE
   * SCREEN MAKING A FALSE STATEMENT. Runtime testing reached `/users/abc/profile` and was told "This site
   * declares no profile properties, so there is nothing to show" — on a tenant that declares TWELVE. The
   * sentence is not merely unhelpful, it is factually wrong about the tenant's configuration, and it
   * sends an operator to the Profile Properties screen to fix something that is not broken.
   */
  protected readonly addressUnreadable: Signal<boolean> = computed(
    () => readRouteId(this.userId()).kind === 'unreadable',
  );

  protected readonly unreadableAddressMessage: string = NO_USER_MESSAGE;

  /** Whether the CALLER is the account whose profile is on screen. */
  /**
   * Whether this screen is being shown BECAUSE the caller's own session cannot proceed without it, as
   * opposed to being opened deliberately.
   *
   * ⚠ THE SCREEN USED TO EXPLAIN NOTHING, AND THAT WAS HALF OF A MEASURED DEFECT. A caller with unfilled
   * required properties was landed here with no text of any kind saying why, so the screen read as an
   * ordinary profile page they had chosen to visit - while every other address they tried was refused. The
   * refusal and the landing were both silent, so nothing connected the two.
   */
  protected readonly landedForRemediation: Signal<boolean> = computed(
    () => this.auth.mustUpdateProfile() && this.isSelf(),
  );

  /** The sentence that explains the landing. */
  protected readonly remediationExplanation = PROFILE_REMEDIATION_EXPLANATION;

  protected readonly isSelf: Signal<boolean> = computed(() => {
    const key = this.resolvedUserId();
    const caller = this.auth.currentUser();

    if (key === null || caller === null) {
      return false;
    }

    return caller.userId === key;
  });

  protected readonly visibilityOffered: Signal<boolean> = computed(() => {
    if (this.manageVisibility()) {
      return true;
    }

    const profile = this.profile();

    // Presence is tested EXPLICITLY against null rather than by truthiness: an unresolved profile and a
    // profile whose policy is off are different facts, and only the second is the tenant's answer.
    return profile !== null && profile.displayVisibilityEnabled && this.isSelf();
  });

  /** The profile as the store holds it. */
  protected readonly profile: Signal<UserProfile | null> = this.store.profile;

  /** Whether the profile is still being read. */
  protected readonly loading: Signal<boolean> = this.store.profileLoading;

  /** Whether a write is in flight. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * Whether the WAITING INDICATOR replaces the screen's content, which is narrower than "a read is in
   * flight".
   *
   * ⚠ THE PLACEHOLDER REPLACES THE FIELDS ONLY WHEN THERE ARE NO FIELDS TO REPLACE, and the measured defect
   * this closes is that it did not. A successful save RE-READS the profile, so `loading` went true again
   * immediately afterwards - and because the indicator was the screen's first content branch, every save
   * blanked the entire form: every label, every value the operator had just typed, the visibility selectors
   * and both actions were replaced by "Loading profile…" until the confirming read landed. The same
   * discipline the shared grid states for its own placeholder applies here: a read that arrives while
   * content is on screen keeps it and reports progress through `aria-busy` instead.
   */
  protected readonly showLoadingPlaceholder: Signal<boolean> = computed(
    () => this.loading() && !this.hasProperties(),
  );

  /**
   * Whether the screen is working - a write in flight, or the read that confirms one. Bound to `aria-busy`
   * on the form and used to withhold both actions, so the form an operator can still see is not one they can
   * submit twice or edit against a state that is about to be replaced.
   */
  protected readonly busy: Signal<boolean> = computed(() => this.saving() || this.loading());

  /**
   * The sentence stating what the screen is doing, or `null` when it is at rest. Carried by the surrounding
   * status region rather than by the indicator: the shared spinner renders its OWN live region when given a
   * label, and nesting one live region inside another is how one action came to be announced twice.
   */
  protected readonly busyMessage: Signal<string | null> = computed(() => {
    if (this.saving()) {
      return SAVING_MESSAGE;
    }

    return this.loading() ? CONFIRMING_MESSAGE : null;
  });

  /**
   * The problem document to surface, or `null` when this screen has nothing to report. SCOPED TO THIS
   * SCREEN'S OWN COMMANDS. The store records one failure at a time across every command it offers, and it
   * tags each with the command that produced it.
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
    toSections(this.profile(), this.withheldIds()),
  );

  /** Whether there is anything at all to render. */
  protected readonly hasProperties: Signal<boolean> = computed(() => this.sections().length > 0);

  /** How many properties this profile carries, which is how many a save would submit. */
  private readonly declaredPropertyCount: Signal<number> = computed(() =>
    this.sections().reduce((total, section) => total + section.values.length, 0),
  );

  /** Whether the tenant declares more properties than one write may carry. */
  protected readonly exceedsSubmissionLimit: Signal<boolean> = computed(
    () => this.declaredPropertyCount() > MAXIMUM_SUBMITTED_PROPERTIES,
  );

  /** The sentence shown in place of the form when the declaration count cannot be saved. */
  protected readonly tooManyPropertiesNotice: Signal<string> = computed(() =>
    tooManyPropertiesMessage(this.declaredPropertyCount()),
  );

  protected readonly pageTitle: Signal<string> = computed(() => {
    const supplied = this.heading()?.trim();

    if (supplied !== undefined && supplied.length > 0) {
      return supplied;
    }

    return formatProfileTitle(this.store.selectedUser(), this.resolvedUserId(), this.isSelf());
  });

  /** Whether the action row is rendered: view mode offers none. */
  protected readonly showActions: Signal<boolean> = computed(
    () => this.mode() === 'edit' && this.hasProperties(),
  );

  /**
   * The visibility choices, in the order the legacy editor offered them. The codes come from the shared
   * contract rather than being spelled as bare numbers, so a template never contains a magic integer.
   */
  protected readonly visibilityOptions: readonly VisibilityOption[] = Object.freeze([
    { code: PROFILE_VISIBILITY.allUsers, label: 'All users' },
    { code: PROFILE_VISIBILITY.membersOnly, label: 'Members only' },
    { code: PROFILE_VISIBILITY.adminOnly, label: 'Administrators only' },
  ]);

  /**
   * The form backing edit mode, rebuilt whenever the property set changes. A `computed()` rather than a
   * field mutated from a lifecycle hook, for a change detection reason rather than a stylistic one: a
   * form rebuilt imperatively after the template had already rendered would leave this on-push component
   * with no reason to check again, so the controls would exist and never appear.
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
    effect(() => {
      const userId = this.resolvedUserId();

      if (userId === null || this.auth.sessionRestricted()) {
        return;
      }

      untracked(() => {
        this.store.selectUser(userId);
      });
    });

    effect(() => {
      const userId = this.resolvedUserId();

      if (userId === null) {
        return;
      }

      untracked(() => {
        this.store.loadProfile(userId);
      });
    });

    // A genuine side effect: announcing a refusal the banner alone would under-state.
    effect(() => {
      const failure = this.store.failure();

      untracked(() => this.announce(failure === null ? null : failure));
    });

    effect(() => {
      const awaited: number = this.awaitedSaveId();
      const settled: UserMutation | null = this.store.mutation();

      if (awaited === 0 || settled === null || settled.id !== awaited) {
        return;
      }

      untracked(() => {
        this.awaitedSaveId.set(0);

        // The operation is asserted as well as the identifier: a successful save re-reads the profile, and
        // that re-read has its own failure path which must not be mistaken for a failed save.
        if (settled.failure !== null && settled.operation === 'saveProfile') {
          this.focusFirstServerRefusal();

          return;
        }

        // MIGRATION: honestly net-new.
        const settledForm = this.form();
        settledForm.markAsPristine();
        settledForm.markAsUntouched();

        this.notifications.success(PROFILE_SAVED_MESSAGE);

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
   * The DOM id of one section's BODY, for the toggle's `aria-controls`. A control cannot point
   * `aria-controls` at its own id and expect assistive technology to find the region, so the body
   * carries an id of its own.
   *
   * @param section The section whose body is being named.
   * @returns A stable id, unique within the screen.
   */
  protected sectionBodyId(section: string): string {
    return `user-profile-body-${section}`;
  }

  /**
   * Collapses an expanded section, or expands a collapsed one.
   *
   * @param key The section key.
   */
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
   * The label for a property. A legacy-seeded property gets the wording the operator already knows;
   * anything a tenant added gets its own name.
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
    const curated = LEGACY_PROFILE_WORDING[value.definition.propertyName]?.help ?? '';
    const declared = declaredConstraintsSentence(value.definition);

    if (curated.length === 0) {
      return declared;
    }

    return declared.length === 0 ? curated : `${curated}. ${declared}`;
  }

  /**
   * Whether this property's value was seeded from the tenant's declared default rather than chosen by the
   * account - U14. Two accounts whose stored answer serialises identically as the empty string DID render
   * differently, because {@link initialValueFor} distinguishes a stored blank from a row that was never
   * written, and only the second is seeded. That distinction is correct and is kept; what was missing was
   * any way for the operator to SEE which of the two they were looking at, so a value they never typed
   * read as one they had.
   *
   * @param value The property.
   * @returns `true` when the rendered value came from the declaration.
   */
  protected isSeededFromDefault(value: UserProfileValue): boolean {
    const seeded = initialValueFor(value);

    if (seeded.length === 0 || value.propertyValue.length > 0 || value.lastUpdatedDate !== null) {
      return false;
    }

    const control = this.valueControl(value);

    // Only while the control still holds the seeded value: once the operator edits it, the value is
    // theirs and the remark would be false.
    return control !== null && control.value === seeded;
  }

  /**
   * The properties this viewer is not shown - U13. Withheld rather than absent: their stored values are
   * still carried by every write this screen performs, which is what stops honouring the declaration from
   * destroying data.
   */
  protected readonly withheldProperties: Signal<readonly UserProfileValue[]> = computed(() => {
    const profile = this.profile();

    if (profile === null || this.viewerAdministers()) {
      return [];
    }

    return profile.properties.filter((value) => isWithheldFromOwner(value));
  });

  /** The identifiers of the withheld properties, for the section builder. */
  private readonly withheldIds: Signal<ReadonlySet<number>> = computed(
    () => new Set(this.withheldProperties().map((value) => value.definition.propertyDefinitionId)),
  );

  /**
   * Whether the caller administers this tenant, which is the predicate legacy branched on.
   * `Website/admin/Users/Profile.ascx.vb` L164-L168 is `For Each ... If IsAdmin Then profProperty.Visible
   * = True`, with NO else arm - so an administrator saw every declared property and everyone else saw the
   * declaration honoured.
   */
  protected readonly viewerAdministers: Signal<boolean> = this.auth.administersCurrentPortal;

  /**
   * The required properties still unmet, so they can be REACHED - U12. A mandatory property declared last
   * sits far below the fold, and before any submit there was nothing at all pointing to it.
   *
   * ⚠ A METHOD RATHER THAN A `computed()`, DELIBERATELY. A reactive-form control is not a signal, so a
   * computed reading `control.value` never invalidates and the list went on naming a requirement the
   * operator had already met. {@link fieldError} is control-derived in the same way and for the same
   * reason.
   *
   * @returns The unmet required properties, in rendered order.
   */
  protected unmetRequired(): readonly UserProfileValue[] {
    const form = this.form();

    return this.sections().flatMap((section) =>
      section.values.filter((value) => {
        if (!value.definition.required) {
          return false;
        }

        const control = form.controls.values.controls[controlKey(value)];

        return control === undefined || control.value.trim().length === 0;
      }),
    );
  }

  /**
   * The label a reach entry carries. The curated labels end in a colon because the legacy resource files
   * spelled them that way and {@link labelFor} preserves that verbatim, but a colon reads as a stray mark
   * on a standalone link rather than as punctuation introducing a control.
   *
   * @param value The property.
   * @returns The label without its trailing colon.
   */
  protected reachLabel(value: UserProfileValue): string {
    // One implementation of "the caption, as a sentence would name it", shared with the validation
    // messages. Two copies of the same de-punctuation would be two places for the wording to drift.
    return this.messageSubjectFor(value);
  }

  /**
   * Moves the caller to one named control. The scroll is requested explicitly rather than left to the
   * focus call, because a control below the fold must be BROUGHT INTO VIEW as well as focused.
   *
   * @param value The property to move to.
   */
  protected reach(value: UserProfileValue): void {
    const element = this.document.getElementById(this.controlId(value));

    if (element === null) {
      return;
    }

    element.scrollIntoView({ block: 'center' });
    element.focus();
  }

  /**
   * Whether a property is rendered with a multi-line control. TWO INPUTS, AND THEY ANSWER TWO DIFFERENT
   * QUESTIONS. A property the installer seeded as RICH TEXT is prose by declaration — see {@link
   * LEGACY_RICH_TEXT_PROPERTIES} — and a property the tenant gave a large declared bound is prose by
   * size.
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
   * How many rows one multi-line control renders — U-M6. ⚠ SIZED FROM THE DECLARATION RATHER THAN FIXED
   * AT FOUR, which is the whole of this fix.
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
   * @param value The property.
   * @returns The bound, or `null`.
   */
  protected maxLengthFor(value: UserProfileValue): number | null {
    return value.definition.length > 0 ? value.definition.length : null;
  }

  /**
   * Every message to show beneath a property, client-side and server-side together. The shared form field
   * accepts a list and renders each entry, so the two sources are concatenated rather than one being made
   * to win.
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
   * Whether a property currently has a message against it. Drives `aria-invalid` on the control.
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
   * @param value The property.
   * @returns The text to render.
   */
  protected displayValue(value: UserProfileValue): string {
    return initialValueFor(value);
  }

  protected onSubmit(): void {
    if (this.saving()) {
      return;
    }

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

    this.awaitedSaveId.set(dispatched);
  }

  /**
   * Concludes a MANDATORY profile completion and hands the caller onward. ⚠ WITHOUT THIS THE JOURNEY
   * NEVER ENDS. The advisory the caller has just satisfied is carried in the HELD SESSION rather than
   * recomputed by the client, so the server stops requiring the completion the moment the required values
   * are written while this client goes on believing it is outstanding — and the root redirect goes on
   * resolving back to this screen. ⚠ THE ADVISORY IS CLEARED LOCALLY AND THE SESSION IS DELIBERATELY
   * *NOT* RENEWED, even though this screen's own write revokes nothing.
   */
  private concludeRemediation(): void {
    this.auth.noteProfileRemediated();

    void this.router.navigateByUrl('/', { replaceUrl: true }).catch(() => false);
  }

  /**
   * Restores every control to the value the profile arrived carrying, ASKING FIRST when that would throw away
   * unsaved entry.
   *
   * ⚠ THIS CONTROL USED TO DISCARD IN SILENCE, and it was the only way out of this screen that did. Leaving
   * through the sidebar or the browser's Back button has always been refused by the shared gate until the
   * operator confirms — the same gate, the same sentence — so Cancel was bypassing the application's own
   * protection while sitting a few pixels away from the button that honours it. Measured: the button stayed on
   * the same address, flipped the form from dirty to pristine and wiped the typed value, with no confirm, no
   * dialog, no toast and no banner.
   *
   * The question is asked through the shared helper rather than a `confirm` written here, so there is exactly
   * one sentence and one call site for it; a pristine form is not worth interrupting anyone over and is reset
   * without a question.
   */
  protected onReset(): void {
    if (this.form().dirty && confirmDiscardUnsavedChanges() === false) {
      return;
    }

    this.form().reset();
  }

  /**
   * Builds the payload for a write. EVERY declared property is carried, not merely the changed ones.
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

    // ⚠ A WITHHELD PROPERTY IS STILL CARRIED, VERBATIM. `UserService.UpdateProfileAsync` replaces the
    // whole answer set and writes `Cleared(value)` for every stored value the submission omits, so
    // leaving a property out DESTROYS it. Legacy had no equivalent hazard because it bound and saved one
    // collection whose invisible members were simply never overwritten; carrying the stored value forward
    // reproduces that outcome over a replace-all contract.
    const carried = this.withheldProperties().map((value) => ({
      propertyDefinitionId: value.definition.propertyDefinitionId,
      propertyValue: value.propertyValue,
      visibility: value.visibility,
    }));

    return { userId, properties: [...properties, ...carried] };
  }

  /**
   * The client-side message for an invalid control. The required message is the one the legacy resource
   * file carried for that property, which is why the three measured divergences between a label and its
   * message survive.
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

    // ⚠ THE REMAINING THREE MESSAGES NAME THE FIELD THE READER CAN SEE, AND THEY USED NOT TO. Only the
    // required branch above consulted the curated wording; every other branch interpolated the RAW stored
    // property name, so a reader looking at a field captioned "Postal Code" was told "PostalCode must be
    // 40 characters or fewer" - a name that appears nowhere on the screen, and for seven of the nineteen
    // seeded properties a name that differs from the caption. The subject of a message has to be the thing
    // it is about, spelled the way the screen spells it.
    const subject = this.messageSubjectFor(value);

    if (control.hasError('maxlength')) {
      return `${subject} must be ${value.definition.length} characters or fewer`;
    }

    // Worded to match the server's own refusal for the same rule — `Profile property "X" does not match the
    // format it requires.` — so an operator who trips the rule in the browser and an operator who trips it on
    // the server are told the same thing rather than being left to wonder whether they are two different
    // problems.
    if (control.hasError('tenantPattern')) {
      return `${subject} does not match the format it requires`;
    }

    return `${subject} is not valid`;
  }

  /**
   * How a message should NAME one property: the caption the reader can see, without its trailing colon.
   *
   * The curated labels are authored as captions - every one of the nineteen ends in a colon, because that
   * is how the legacy screens punctuated a field caption - and a colon inside a sentence reads as a
   * mistake. Stripping it here rather than storing two spellings keeps one source of wording, so a caption
   * and the message about it cannot drift apart. It also matches what the reader actually sees: the shared
   * form field normalises the same trailing colon out of every caption it displays (the legacy screens
   * punctuated their labels six different ways), so the rendered label is `Postal Code` and a message
   * naming `Postal Code:` would be quoting a spelling that appears nowhere on the screen.
   *
   * ⚠ THE REQUIRED BRANCH DELIBERATELY DOES NOT USE THIS. Its wording comes from the legacy resource file
   * rather than from the label, and three of those sentences knowingly disagree with their caption - most
   * visibly `Cell`, captioned "Cell/Mobile:" and required as "Cell Phone is required". Those divergences
   * are measured legacy behaviour that this port preserves; routing the required branch through here would
   * silently correct wording the migration notes record as intentional.
   *
   * @param value The property.
   * @returns The subject to name in a message.
   */
  private messageSubjectFor(value: UserProfileValue): string {
    const curated: string | undefined =
      LEGACY_PROFILE_WORDING[value.definition.propertyName]?.label;

    if (curated === undefined) {
      return value.definition.propertyName;
    }

    const trimmed: string = curated.trim();

    return trimmed.endsWith(':') ? trimmed.slice(0, -1) : trimmed;
  }

  /**
   * The server's messages for one property, from the current problem document. BOTH SPELLINGS OF THE KEY
   * ARE PROBED, because the two sides of the boundary disagree about casing and neither is wrong.
   *
   * @param value The property.
   * @returns The messages, empty when the document names no error for it.
   */
  /**
   * Moves focus to the first control the SERVER has just complained about.
   *
   * ⚠ WHY THE SHARED DIRECTIVE DOES NOT ALREADY COVER THIS. `FocusFirstInvalidDirective` is attached to this
   * form and does exactly the right thing — but it selects on `.ng-invalid`, which only a CLIENT-side validator
   * can produce. A refusal that only the server can reach, such as a format expression too dangerous to run in
   * the browser, leaves every control perfectly valid as far as Angular is concerned, so the directive
   * correctly finds nothing and focus stays where it was. Measured on a rejected write, focus was on `BODY`.
   *
   * Sections are walked in render order so the control chosen is the first one an operator reading down the
   * screen would reach, not merely the first the server happened to mention.
   */
  private focusFirstServerRefusal(): void {
    for (const section of this.sections()) {
      for (const value of section.values) {
        if (this.serverMessagesFor(value).length === 0) {
          continue;
        }

        // Scoped to this component's own element, so a matching identifier elsewhere in the document cannot
        // steal the focus. `CSS.escape` because the identifier embeds a tenant-authored property name.
        const target = (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(
          `#${CSS.escape(this.controlId(value))}`,
        );

        if (target !== null) {
          target.focus();
        }

        return;
      }
    }
  }

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
   * Announces a failure once, at the severity the shared summariser decided. A PERMISSION REFUSAL IS A
   * WARNING, NOT AN ERROR, and the severity is taken from the summary rather than decided again here.
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

    if (failure.summary.severity === 'error') {
      this.lastAnnouncedFailure = failure.problem;

      return;
    }

    if (this.lastAnnouncedFailure === failure.problem) {
      return;
    }

    this.lastAnnouncedFailure = failure.problem;

    this.notifications.notify(
      failure.summary.severity,
      failure.summary.message,
      failure.summary.supportReference,
    );
  }
}

/**
 * The key a property's controls are registered under. The declaration identifier rather than the property
 * name: `PropertyDefinitionID` is `IDENTITY(1,1)` and therefore unique, whereas a name is unique only per
 * tenant and per module definition under the schema's `(PortalID, ModuleDefID, PropertyName)` index, so
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
 * ⚠ THE RECORD IDENTIFIER IS WITHHELD FROM THE ACCOUNT'S OWN OWNER, AND THIS SCREEN IS THE EXACT CASE THE
 * LEGACY RULE WAS WRITTEN FOR. `ManageUsers.ascx.vb` chose its heading in three arms, and the middle one is
 * `If IsUser And IsProfile Then trTitle.Visible = False` (L259-L260) — otherwise it reached
 * `String.Format(UserTitle, User.Username, User.UserID.ToString)` (L262). The two flags come from
 * `UserModuleBase.vb`: `IsUser` is `User.UserID = UserInfo.UserID` (L399-L407), the record being viewed IS
 * the signed-in caller, and `IsProfile` (L350-L371) is `IsUser` AND the profile editor being the panel on
 * screen. So `IsProfile` already implies `IsUser`, and the conjunction reduces to a single fact: THE PROFILE
 * SCREEN, SEEN BY ITS OWN OWNER, RENDERED NO TITLE ROW AT ALL and therefore disclosed no database key.
 *
 * Hiding the heading outright is not reproduced, for the same reason the account form does not reproduce it:
 * a routed screen with no `h1` leaves its main region without an accessible name and breaks the
 * semantic-landmark requirement every other screen here satisfies. Omitting only the identifier achieves
 * what the legacy rule protected — a member is never shown an internal identity value — while keeping the
 * heading the screen needs. The divergence is recorded in `MIGRATION_NOTES.md` alongside the account form's.
 *
 * An administrator viewing somebody else's profile still sees the identifier, because for them it is the
 * administrative detail that distinguishes two accounts sharing a display name.
 *
 * @param user The account, or `null` while it is still being read.
 * @param userId The identifier resolved from the route, or `null`.
 * @param viewedByOwner Whether the caller is the account being shown — the legacy `IsUser`.
 * @returns A non-blank heading.
 */
function formatProfileTitle(
  user: UserDetail | null,
  userId: number | null,
  viewedByOwner: boolean,
): string {
  if (user === null || userId === null || user.userId !== userId) {
    return 'Edit Profile';
  }

  if (viewedByOwner) {
    return `Edit Profile - ${user.username}`;
  }

  return `Edit Profile - ${user.username} (Id: ${String(user.userId)})`;
}
