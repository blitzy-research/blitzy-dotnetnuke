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

  // The consequence is a refusal that arrives from the server rather than beside the box as it is typed.
  // That is the correct trade: the rule is still enforced, still reported per field through the server's
  // model-state message, and a tenant can no longer author a declaration that hangs an operator's browser.
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
 * Groups a profile's properties into the sections the screen renders. EVERY DECLARED PROPERTY IS
 * RENDERED, INCLUDING ONE MARKED NOT VISIBLE, and that is the legacy behaviour rather than a relaxation
 * of it.
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
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form().dirty && this.saving() === false,
  );
  private readonly store = inject(UserStore);
  private readonly notifications = inject(NotificationService);

  /**
   * The signed-in session, read for ONE fact: whether the caller is the account on screen. `core/state`
   * rather than another feature's state, so this is a shared dependency rather than a layering violation
   * - the credential screen beside this one reads the same store for the same predicate.
   */
  private readonly auth = inject(AuthStore);

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
    toSections(this.profile()),
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

    return formatProfileTitle(this.store.selectedUser(), this.resolvedUserId());
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
    return LEGACY_PROFILE_WORDING[value.definition.propertyName]?.help ?? '';
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

  /** Restores every control to the value the profile arrived carrying. */
  protected onReset(): void {
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

    return { userId, properties };
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

    if (control.hasError('maxlength')) {
      return `${name} must be ${value.definition.length} characters or fewer`;
    }

    return `${name} is not valid`;
  }

  /**
   * The server's messages for one property, from the current problem document. BOTH SPELLINGS OF THE KEY
   * ARE PROBED, because the two sides of the boundary disagree about casing and neither is wrong.
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
