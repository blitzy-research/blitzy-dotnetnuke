import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import type { Signal, WritableSignal } from '@angular/core';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import { CREDENTIAL_MAX_LENGTH } from '../../../core/utils/credential-bounds.util';
import {
  conflictMessage,
  fieldErrorMessage,
  isDuplicateAliasCode,
  problemSupportReference,
  summarizeProblem,
} from '../../../core/utils/form-errors.util';
import { readRouteId } from '../../../core/utils/route-id.util';
import type { RouteIdReading } from '../../../core/utils/route-id.util';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import type {
  CreatePortalRequest,
  PortalDetail,
  UpdatePortalRequest,
} from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { NotificationSeverity } from '../../../core/services/notification.service';
import type { PortalFailure } from '../../../core/state/portal.store';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

// =============================================================================
//  PORTAL TYPE
// =============================================================================

/** The two portal kinds the legacy signup screen offered. */
export type PortalType = 'P' | 'C';

/** Parent portal: reached through a host name of its own. */
const PARENT_PORTAL_TYPE: PortalType = 'P';

/** Child portal: reached through a path beneath an existing host name. */
const CHILD_PORTAL_TYPE: PortalType = 'C';

// MEASURED VALIDATION LIMITS
// Applied consistently that principle produces both of the divergences recorded here, so neither is an
// ad-hoc choice.

/** Portal alias, 128 characters. */
const ALIAS_MAX_LENGTH = 128;

/** Portal title, 128 characters. */
const TITLE_MAX_LENGTH = 128;

/** Description and keywords, 500 characters each. */
const METADATA_MAX_LENGTH = 500;

/** Administrator given and family name, 50 characters. 50, NOT the markup's 100. */
const PERSON_NAME_MAX_LENGTH = 50;

/**
 * Administrator sign-in name, 100 characters. `signup.ascx:L89` declares `maxlength="100"` and the
 * terminal column is `Username nvarchar(100) NOT NULL` (`01.00.06.SqlDataProvider:L197`).
 */
const USERNAME_MAX_LENGTH = 100;

/**
 * Administrator password and its confirmation. THE LEGACY FIGURE OF 20 IS DELIBERATELY NOT PRESERVED.
 * `signup.ascx:L94` and `L100` declare `maxlength="20"`, but that figure mirrored the legacy STORAGE
 * width `Users.Password nvarchar(20)` rather than any rule the legacy applied to a credential.
 */
const PASSWORD_MAX_LENGTH = CREDENTIAL_MAX_LENGTH;

/**
 * The shortest administrator password the API accepts. The MEASURED LEGACY POLICY and not a tightening of
 * it: `Website/release.config:L241` declares `minRequiredPasswordLength="7"`, the API's creation rule
 * binds that same policy value, and the legacy screen enforced it only by letting the server refuse.
 */
const PASSWORD_MIN_LENGTH = 7;

/**
 * The pattern an administrator mail address must match. THE AUTHORITY IS THE DOMAIN ATTRIBUTE, NOT THIS
 * SCREEN'S MARKUP, because the markup has nothing to say: `signup.ascx:L106` declares a
 * `requiredfieldvalidator` on the address and NO regular-expression validator at all, so the legacy
 * screen accepted any non-empty text and let the write refuse it.
 */
const EMAIL_PATTERN = /^[a-zA-Z0-9._%+'-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,63}$/;

/**
 * Administrator mail address, 100 characters. PRESERVED AT THE LEGACY SCREEN'S FIGURE, WHICH IS THE
 * STRICTER OF THE TWO. `signup.ascx:L106` declares `maxlength="100"`.
 */
const EMAIL_MAX_LENGTH = 100;

// =============================================================================
//  MEASURED ALIAS CHARACTER SETS
// =============================================================================

const CHILD_ALIAS_CHARACTERS = 'abcdefghijklmnopqrstuvwxyz0123456789-';

const PARENT_ALIAS_CHARACTERS = `${CHILD_ALIAS_CHARACTERS}./:`;

/**
 * The scheme prefix the legacy screen stripped before inspecting alias characters. `Signup.ascx.vb:L184`
 * reads `Replace(txtPortalName.Text, "http://", "")`.
 */
const LEGACY_SCHEME_PREFIX = 'http://';

/** The alias separator whose LAST occurrence bounds a child portal's own segment. */
const ALIAS_SEGMENT_SEPARATOR = '/';

// MEASURED WORDING

/** `valPortalName.ErrorMessage` — `signup.ascx:L41` and the resx entry of the same name. */
const ALIAS_REQUIRED_MESSAGE = 'Portal Name Is Required.';

const ALIAS_REPLACED_MESSAGE =
  'Changing the Portal Type reset the Portal Alias. Re-enter the alias you want for this portal.';

/** `valFirstName.ErrorMessage` — `signup.ascx:L80`. */
const FIRST_NAME_REQUIRED_MESSAGE = 'First Name Is Required.';

/** `valLastName.ErrorMessage` — `signup.ascx:L85`. */
const LAST_NAME_REQUIRED_MESSAGE = 'Last Name Is Required.';

/** `valUsername.ErrorMessage` — `signup.ascx:L90`. */
const USERNAME_REQUIRED_MESSAGE = 'Username Is Required.';

/** `valPassword.ErrorMessage` — `signup.ascx:L96`. */
const PASSWORD_REQUIRED_MESSAGE = 'Password Is Required.';

/** `valConfirm.ErrorMessage` — `signup.ascx:L102`. */
const CONFIRM_REQUIRED_MESSAGE = 'Password Confirmation Is Required.';

/** `valEmail.ErrorMessage` — `signup.ascx:L107`. */
const EMAIL_REQUIRED_MESSAGE = 'Email Is Required.';

/**
 * The wording for a credential shorter than the configured minimum. The API'S OWN SENTENCE, reproduced
 * with the configured length substituted into it, because the API composes it from the same legacy
 * template — `InvalidPassword.Text` from the shared resource file, whose two bracketed tokens it fills
 * from the bound policy.
 */
const PASSWORD_TOO_SHORT_MESSAGE =
  'The password specified is invalid.  Please specify a valid password.  Passwords must be at ' +
  `least ${String(PASSWORD_MIN_LENGTH)} characters in length and contain at least 0 ` +
  'non-alphanumeric characters.';

/** The wording for a malformed mail address. */
const EMAIL_INVALID_MESSAGE =
  'The email address specified is invalid.  Please specify a valid email address.';

/** `InvalidName.Text` — `Signup.ascx.resx:L234-L236`, verbatim. */
const INVALID_ALIAS_MESSAGE = 'The Portal Name Must Not Contain Spaces Or Punctuation.';

/** `InvalidPassword.Text` — `Signup.ascx.resx:L237-L239`, verbatim. */
const PASSWORD_MISMATCH_MESSAGE = 'The Password Values Entered Do Not Match.';

/** `CreateError.Text` — `Signup.ascx.resx:L243-L245`, verbatim. */
const CREATE_ERROR_MESSAGE =
  'An Error Was Encountered During The Creation Of Your Portal. This May Have Been ' +
  'Caused By Specifying An Incorrect Password For An Existing User Account. Please ' +
  'Verify Your Details Before You Try Again.';

/** The sentence shown when the address does not name a readable portal. */
const UNREADABLE_ADDRESS_MESSAGE =
  'This address does not name a portal that can be read. Return to the portal list and try again.';

/** The last-resort wording for a failed UPDATE, as distinct from a failed creation. */
const UPDATE_ERROR_MESSAGE =
  'The portal could not be updated. Nothing was changed. Check the connection and try ' +
  'again.';

/** The refusal wording for a change to a host-administered term. */
const HOST_FIELD_REFUSED_MESSAGE =
  'Only a host account may change this portal’s host-administered terms, so the save ' +
  'was refused. Nothing was changed.';

/** The wording shown when the addressed portal is not on the server. */
const PORTAL_NOT_FOUND_MESSAGE = 'That portal no longer exists, so nothing could be loaded.';

/**
 * Confirmation shown after a successful write. MIGRATION: NET-NEW. The legacy screen reported success by
 * NAVIGATING — L316 reads `Response.Redirect(webUrl, True)` — and emitted no confirmation of any kind, so
 * there is no legacy wording to reproduce.
 */
const CREATE_SUCCEEDED_MESSAGE = 'The portal was created.';

/** Confirmation shown after a successful update. See {@link CREATE_SUCCEEDED_MESSAGE}. */
const UPDATE_SUCCEEDED_MESSAGE = 'The portal was updated.';

/**
 * The wording for a rule whose own message this screen cannot name. Reachable only from a rule added
 * after this file was written, or from a length rule whose reported bound could not be read.
 */
const GENERIC_FIELD_MESSAGE = 'Correct this field and try again.';

/** `AddPortal.Text` — `Signup.ascx.resx:L285-L287`. */
const CREATE_HEADING = 'Add New Portal';

/** `ControlTitle_edit.Text` — `SiteSettings.ascx.resx:L480-L482`. */
const EDIT_HEADING = 'Edit Portals';

/** Local `cmdUpdate.Text` — `Signup.ascx.resx:L225-L227`. */
const CREATE_SUBMIT_LABEL = 'Create Portal';

/** Global `cmdUpdate.Text` — `SharedResources.resx:L150-L152`. */
const EDIT_SUBMIT_LABEL = 'Update';

/** Global `cmdCancel.Text` — `SharedResources.resx:L138-L140`. */
const CANCEL_LABEL = 'Cancel';

/** Where both buttons and both success paths lead. */
const PORTAL_LIST_ROUTE = '/portals';

/** The wording of the link to this portal's configuration screen. */
const SETTINGS_LINK_LABEL = 'Site Settings';

/** The wording of the link to this portal's host names. */
const ALIASES_LINK_LABEL = 'Portal Aliases';

/** The template name submitted with every creation. */
const DEFAULT_TEMPLATE_FILE = 'Default Website.template';

// =============================================================================
//  FORM SHAPES
// =============================================================================

/**
 * The creation form: eleven controls, one per field the legacy signup screen showed that still has
 * somewhere to go. ANNOTATION 1 — THE SEMANTIC INVERSION, AND WHY NO CONTROL IS NAMED AFTER A LEGACY
 * CONTROL ID. The legacy `txtPortalName` box collected the ALIAS and the legacy `txtTitle` box collected
 * the NAME. Four independent measurements agree: 1.
 */
export interface PortalCreateFormModel {
  readonly portalType: FormControl<PortalType>;

  /** The first host name the portal answers on. Legacy `txtPortalName`. */
  readonly alias: FormControl<string>;

  /** The portal's title. */
  readonly title: FormControl<string>;

  /** Free-text description offered to search engines. */
  readonly description: FormControl<string>;

  /** Comma-separated search keywords. */
  readonly keywords: FormControl<string>;

  /** First administrator's given name. Legacy `txtFirstName`. */
  readonly firstName: FormControl<string>;

  /** First administrator's family name. Legacy `txtLastName`. */
  readonly lastName: FormControl<string>;

  /** First administrator's sign-in name. Legacy `txtUsername`. */
  readonly username: FormControl<string>;

  /** First administrator's password. Legacy `txtPassword`. */
  readonly password: FormControl<string>;

  /** Confirmation of the password. */
  readonly confirm: FormControl<string>;

  /** First administrator's mail address. Legacy `txtEmail`. */
  readonly email: FormControl<string>;
}

/** The edit form: three controls, and the count is the point. */
export interface PortalEditFormModel {
  /** The portal's title. */
  readonly title: FormControl<string>;

  /** Free-text description offered to search engines. */
  readonly description: FormControl<string>;

  /** Comma-separated search keywords. */
  readonly keywords: FormControl<string>;
}

/** Every control name the creation form publishes. */
export type PortalCreateField = keyof PortalCreateFormModel;

/** Every control name the edit form publishes. */
export type PortalEditField = keyof PortalEditFormModel;

/** The group-level error key raised when the password and its confirmation differ. */
const PASSWORD_MISMATCH_ERROR = 'passwordMismatch';

/** The alias control's error key for a character outside the permitted set. */
const INVALID_ALIAS_ERROR = 'invalidAliasCharacters';

// =============================================================================
//  CONTROL NAME  ->  REQUEST MEMBER NAME
// =============================================================================

/**
 * The request member each creation control is reported against by the API. This map exists because the
 * two vocabularies are deliberately different: the controls are named for what they hold (see {@link
 * PortalCreateFormModel}) while the API reports validation failures against the member names of {@link
 * CreatePortalRequest}.
 */
/**
 * The `id` of the host-name entry control, as the template renders it. ⚠ Pf-M8 — named here rather than
 * written into the effect as a literal, so the constant and the template's own `id`/`for` pair can be
 * checked against each other in one place. A drift here would make the focus move silently do nothing.
 */
const ALIAS_CONTROL_ELEMENT_ID = 'portal-form-alias';

const CREATE_FIELD_MEMBER: Readonly<Record<PortalCreateField, string | null>> = Object.freeze({
  portalType: 'isChildPortal',
  alias: 'portalAlias',
  title: 'portalName',
  description: 'description',
  keywords: 'keyWords',
  firstName: 'administratorFirstName',
  lastName: 'administratorLastName',
  username: 'administratorUsername',
  password: 'administratorPassword',
  confirm: null,
  email: 'administratorEmail',
});

/**
 * The request member each edit control is reported against by the API. Note `keyWords` — an interior
 * capital that the API's own contract carries and that the camel-case naming policy therefore preserves
 * on the wire. It is spelled here exactly as {@link UpdatePortalRequest} spells it.
 */
const EDIT_FIELD_MEMBER: Readonly<Record<PortalEditField, string>> = Object.freeze({
  title: 'portalName',
  description: 'description',
  keywords: 'keyWords',
});

/**
 * The measured requiredness wording for each creation control. Seven entries carry a sentence and four
 * carry `null`, and the split is the measured validator set rather than a judgement: `signup.ascx`
 * declares a required-field validator for the alias, the four administrator identity fields, the password
 * and its confirmation, and for NONE of the title, the description, the keywords or the portal type.
 */
const CREATE_REQUIRED_MESSAGE: Readonly<Record<PortalCreateField, string | null>> = Object.freeze({
  // A radio group always holds one of its two values, so requiredness cannot fail.
  portalType: null,
  alias: ALIAS_REQUIRED_MESSAGE,
  title: null,
  description: null,
  keywords: null,
  firstName: FIRST_NAME_REQUIRED_MESSAGE,
  lastName: LAST_NAME_REQUIRED_MESSAGE,
  username: USERNAME_REQUIRED_MESSAGE,
  password: PASSWORD_REQUIRED_MESSAGE,
  confirm: CONFIRM_REQUIRED_MESSAGE,
  email: EMAIL_REQUIRED_MESSAGE,
});

const EDIT_REQUIRED_MESSAGE: Readonly<Record<PortalEditField, string | null>> = Object.freeze({
  title: null,
  description: null,
  keywords: null,
});

// =============================================================================
//  PURE HELPERS
// =============================================================================

/**
 * Normalises an alias exactly as the legacy screen did, in the legacy order.
 *
 * @param alias The alias as typed.
 * @returns The normalised alias.
 */
export function normaliseAlias(alias: string): string {
  return alias.toLowerCase().replaceAll(LEGACY_SCHEME_PREFIX, '');
}

/**
 * The portion of a normalised alias whose characters are inspected. `Signup.ascx.vb:L201-L205`: a child
 * portal is measured on the segment AFTER ITS LAST separator, because the part in front of it is the
 * parent's host name and is not the child's to constrain; a parent portal is measured whole.
 *
 * @param normalisedAlias An alias already through {@link normaliseAlias}.
 * @param isChildPortal Whether the child rule applies.
 * @returns The substring to inspect.
 */
export function measuredAliasSegment(normalisedAlias: string, isChildPortal: boolean): string {
  if (!isChildPortal) {
    return normalisedAlias;
  }

  return normalisedAlias.slice(normalisedAlias.lastIndexOf(ALIAS_SEGMENT_SEPARATOR) + 1);
}

/**
 * Builds the alias character rule for one alias control. The rule is SYNCHRONOUS and depends on the
 * current portal type, which is why it is produced by a factory taking an accessor rather than reading a
 * sibling control through `parent`: the accessor closes over the type control itself, so the dependency
 * is established at construction and cannot be broken by the group being reshaped.
 *
 * @param isChildPortal Reads the currently selected portal type.
 * @returns A validator reporting {@link INVALID_ALIAS_ERROR} for a disallowed character.
 */
export function aliasCharactersValidator(isChildPortal: () => boolean): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    if (typeof raw !== 'string' || raw.length === 0) {
      return null;
    }

    const child = isChildPortal();
    const permitted = child ? CHILD_ALIAS_CHARACTERS : PARENT_ALIAS_CHARACTERS;
    const measured = measuredAliasSegment(normaliseAlias(raw), child);

    for (const character of measured) {
      if (!permitted.includes(character)) {
        // ONE report, not one per character. See this function's migration note.
        return { [INVALID_ALIAS_ERROR]: INVALID_ALIAS_MESSAGE };
      }
    }

    return null;
  };
}

/**
 * Reports whether the password and its confirmation differ.
 *
 * @param group The creation form group.
 * @returns {@link PASSWORD_MISMATCH_ERROR} when the two differ, otherwise `null`.
 */
export const passwordsMatchValidator: ValidatorFn = (
  group: AbstractControl,
): ValidationErrors | null => {
  const password: unknown = group.get('password')?.value;
  const confirm: unknown = group.get('confirm')?.value;

  if (typeof password !== 'string' || typeof confirm !== 'string') {
    return null;
  }

  // Nothing is reported while the confirmation is still empty: requiredness owns that state, and reporting
  // a mismatch against an untouched field would accuse the operator of an error before they had a chance to
  // make one.
  if (confirm.length === 0) {
    return null;
  }

  return password === confirm ? null : { [PASSWORD_MISMATCH_ERROR]: PASSWORD_MISMATCH_MESSAGE };
};

// STATUS CODES THIS SCREEN DISTINGUISHES

/** A validation refusal. */
const BAD_REQUEST_STATUS = 400;

/** A refusal to change a host-administered term. */
const FORBIDDEN_STATUS = 403;

/** The addressed portal is not on the server. */
const NOT_FOUND_STATUS = 404;

// =============================================================================
//  COMPONENT
// =============================================================================

@Component({
  selector: 'app-portal-form',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
  ],
  templateUrl: './portal-form.component.html',
  styleUrl: './portal-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalFormComponent {
  /**
   * Registers this screen's unsaved-entry probe with the application's tracker. ⚠ WHY A REGISTRATION
   * RATHER THAN A ROUTE-LEVEL READ. Leaving a screen happens two ways and only one of them is a router
   * navigation: Cancel, an in-application link and the browser's Back button are navigations a route
   * guard can refuse, while closing or reloading the tab is not, and only the browser's own unload prompt
   * covers that - which needs the dirty state at an arbitrary moment rather than at a navigation.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => (this.createForm.dirty || this.editForm.dirty) && this.saving() === false,
  );
  // ---------------------------------------------------------------------------
  //  COLLABORATORS
  // ---------------------------------------------------------------------------

  /** The single, root-provided portal store — the ONLY route to the API from here. */
  private readonly portalStore = inject(PortalStore);

  /** The application's notification queue. */
  private readonly notifications = inject(NotificationService);

  /** Navigation away from this screen: on success, and on cancel. */
  private readonly router = inject(Router);

  /**
   * Binds this screen's write subscriptions to its own lifetime. ⚠ THE EXPLICIT REFERENCE IS REQUIRED,
   * not a stylistic choice over the argument-less form used in the field initialiser above.
   * `takeUntilDestroyed()` with no argument resolves its reference from the ambient INJECTION CONTEXT,
   * which exists while fields and the constructor run and NOT inside an event handler.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The host name a child portal's alias is prefixed with. Read from the injected document rather than
   * from the global object, so the value is substitutable in a specification and this class touches no
   * ambient global.
   */
  private readonly hostName: string = inject(DOCUMENT).location.host;

  /**
   * The document, for the one focus move a server refusal causes. ⚠ Pf-M8 — injected rather than reached
   * as a global, so this screen has no ambient dependency and remains testable. See the constructor for
   * why the move exists and why only one refusal makes it.
   */
  private readonly document = inject(DOCUMENT);

  // ---------------------------------------------------------------------------
  //  ROUTE INPUT
  // ---------------------------------------------------------------------------

  readonly portalId = input<string | undefined>(undefined);

  private readonly portalKey = computed<RouteIdReading>(() => readRouteId(this.portalId()));

  /**
   * The addressed portal's identifier, or `undefined` when the address names none. ⚠ `undefined` HERE
   * MEANS "NO IDENTIFIER TO FETCH" AND NOTHING MORE - it is deliberately NOT the mode test, which {@link
   * isEditMode} owns, and not the not-found test, which {@link addressUnreadable} owns.
   */
  private readonly resolvedPortalId = computed<number | undefined>(() => {
    const reading = this.portalKey();

    return reading.kind === 'identifier' ? reading.id : undefined;
  });

  /**
   * Whether the address carries something that is not a portal identifier. Rendered by the template as a
   * plain statement INSTEAD of either form.
   */
  protected readonly addressUnreadable = computed<boolean>(
    () => this.portalKey().kind === 'unreadable',
  );

  /** The sentence shown when the address does not name a readable portal. */
  protected readonly unreadableAddressMessage = UNREADABLE_ADDRESS_MESSAGE;

  // ---------------------------------------------------------------------------
  //  MODE
  // ---------------------------------------------------------------------------

  /** Whether the screen is editing an existing portal rather than creating one. */
  readonly isEditMode: Signal<boolean> = computed(
    () => this.portalKey().kind === 'identifier',
  );

  // THE TWO FORMS
  // BOTH ARE BUILT ONCE AND EACH IS ITS OWN TYPE, which is what makes the mode boundary structural instead
  // of conditional.

  /** The creation form. Bound only while {@link isEditMode} is false. */
  protected readonly createForm: FormGroup<PortalCreateFormModel> =
    PortalFormComponent.buildCreateForm();

  /** The edit form. */
  protected readonly editForm: FormGroup<PortalEditFormModel> = new FormGroup<PortalEditFormModel>({
    title: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.maxLength(TITLE_MAX_LENGTH)],
    }),
    description: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.maxLength(METADATA_MAX_LENGTH)],
    }),
    keywords: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.maxLength(METADATA_MAX_LENGTH)],
    }),
  });

  /**
   * Assembles the creation form. Static, and the two locals in front of the group are the reason: the
   * alias rule depends on the portal type, so the type control must exist before the alias control is
   * constructed.
   *
   * @returns The creation group, with the cross-field password rule attached.
   */
  private static buildCreateForm(): FormGroup<PortalCreateFormModel> {
    const portalType = new FormControl<PortalType>(PARENT_PORTAL_TYPE, { nonNullable: true });

    const alias = new FormControl<string>('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(ALIAS_MAX_LENGTH),
        aliasCharactersValidator(() => portalType.value === CHILD_PORTAL_TYPE),
      ],
    });

    return new FormGroup<PortalCreateFormModel>(
      {
        portalType,
        alias,
        title: new FormControl<string>('', {
          nonNullable: true,
          validators: [Validators.maxLength(TITLE_MAX_LENGTH)],
        }),
        description: new FormControl<string>('', {
          nonNullable: true,
          validators: [Validators.maxLength(METADATA_MAX_LENGTH)],
        }),
        keywords: new FormControl<string>('', {
          nonNullable: true,
          validators: [Validators.maxLength(METADATA_MAX_LENGTH)],
        }),
        firstName: new FormControl<string>('', {
          nonNullable: true,
          validators: [Validators.required, Validators.maxLength(PERSON_NAME_MAX_LENGTH)],
        }),
        lastName: new FormControl<string>('', {
          nonNullable: true,
          validators: [Validators.required, Validators.maxLength(PERSON_NAME_MAX_LENGTH)],
        }),
        username: new FormControl<string>('', {
          nonNullable: true,
          validators: [Validators.required, Validators.maxLength(USERNAME_MAX_LENGTH)],
        }),
        password: new FormControl<string>('', {
          nonNullable: true,
          validators: [
            Validators.required,
            Validators.minLength(PASSWORD_MIN_LENGTH),
            Validators.maxLength(PASSWORD_MAX_LENGTH),
          ],
        }),
        // The confirmation carries the SAME bounds as the credential it confirms. Omitting the minimum here
        // would let the two boxes disagree about what is acceptable, so a credential of six characters
        // typed identically twice would report the fault against one box and not the other.
        confirm: new FormControl<string>('', {
          nonNullable: true,
          validators: [
            Validators.required,
            Validators.minLength(PASSWORD_MIN_LENGTH),
            Validators.maxLength(PASSWORD_MAX_LENGTH),
          ],
        }),
        email: new FormControl<string>('', {
          nonNullable: true,
          validators: [
            Validators.required,
            Validators.maxLength(EMAIL_MAX_LENGTH),
            Validators.pattern(EMAIL_PATTERN),
          ],
        }),
      },
      { validators: [passwordsMatchValidator] },
    );
  }

  // ---------------------------------------------------------------------------
  //  WRITE LIFECYCLE
  // ---------------------------------------------------------------------------

  /** Whether this screen has asked the store to write. */
  private readonly saveRequested: WritableSignal<boolean> = signal(false);

  /**
   * The failure the store already held when this screen was constructed. Captured untracked, as an
   * identity rather than a value, and suppressed by {@link failure} below.
   */
  private readonly baselineFailure: PortalFailure | null = untracked(() =>
    this.portalStore.detailFailure(),
  );

  /**
   * The record the edit form was last filled from. A plain field rather than a signal: nothing observes
   * it, and it exists solely so the hydration step can tell "the portal arrived" from "the portal is
   * still the one I already filled from", and therefore not overwrite an operator's unsaved edits every
   * time an unrelated signal changes.
   */
  private hydratedFrom: PortalDetail | null = null;

  // ---------------------------------------------------------------------------
  //  DERIVED STATE
  // ---------------------------------------------------------------------------

  /** The failure to report, with any pre-existing one suppressed. See {@link baselineFailure}. */
  readonly failure: Signal<PortalFailure | null> = computed(() => {
    const current: PortalFailure | null = this.portalStore.detailFailure();

    return current === this.baselineFailure ? null : current;
  });

  /** The RFC 7807 document for the shared error banner, or `null` for nothing to show. */
  protected readonly problem: Signal<ProblemDetails | null> = computed(
    () => this.failure()?.problem ?? null,
  );

  /** The portal being edited, once it has actually been read, or `null`. */
  readonly portal: Signal<PortalDetail | null> = computed(() => {
    const id: number | undefined = this.resolvedPortalId();

    if (id === undefined) {
      return null;
    }

    const held: PortalDetail | null = this.portalStore.selectedPortal();

    return held !== null && held.portalId === id ? held : null;
  });

  /** True while the portal is being read, which can only happen in edit mode. */
  readonly loading: Signal<boolean> = computed(
    () => this.isEditMode() && this.portalStore.detailLoading() && !this.saveRequested(),
  );

  /**
   * True while a create or an update is in flight. A conjunction rather than a flag of its own, so it
   * cannot get stuck: the store lowers its loading slice on BOTH the success and the failure path, and
   * this member follows it down either way.
   */
  readonly saving: Signal<boolean> = computed(
    () => this.saveRequested() && this.portalStore.detailLoading(),
  );

  /** Whether the edit form has something real to edit. */
  protected readonly canEdit: Signal<boolean> = computed(
    () => this.isEditMode() && this.portal() !== null,
  );

  /** Whether the submit control is available. */
  protected readonly canSubmit: Signal<boolean> = computed(() => {
    if (this.saving()) {
      return false;
    }

    return this.isEditMode() ? this.canEdit() : true;
  });

  protected readonly heading: Signal<string> = computed(() =>
    this.isEditMode() || this.addressUnreadable() ? EDIT_HEADING : CREATE_HEADING,
  );

  /**
   * The name of the portal being edited, shown beside the heading, or `undefined`. ⚠ THE HEADING ALONE
   * IDENTIFIES NOTHING. It reads "Edit Portals" on every portal, and the three controls beneath it are a
   * title, a description and a keyword list — none of which says WHICH tenant is being changed.
   */
  protected readonly portalName: Signal<string | undefined> = computed(() => {
    const held: PortalDetail | null = this.portal();

    if (held === null) {
      return undefined;
    }

    const name: string | null = held.portalName;

    return name === null || name.trim().length === 0 ? undefined : name;
  });

  /**
   * The address of this portal's settings screen, or `null` when there is no portal. ⚠ THE SIBLING
   * SCREENS WERE UNREACHABLE FROM HERE, AND ONE OF THEM WAS UNREACHABLE ALTOGETHER. Every anchor the
   * application renders was enumerated: nothing anywhere linked to `:portalId/aliases`, and the listing's
   * single row command targets `:portalId/settings`, so a portal's host names could only be reached by
   * typing the address.
   */
  protected readonly settingsLink: Signal<(string | number)[] | null> = computed(() => {
    const id: number | undefined = this.resolvedPortalId();

    return id === undefined ? null : ['/portals', id, 'settings'];
  });

  /** The address of this portal's host names, or `null` when there is no portal. */
  protected readonly aliasesLink: Signal<(string | number)[] | null> = computed(() => {
    const id: number | undefined = this.resolvedPortalId();

    return id === undefined ? null : ['/portals', id, 'aliases'];
  });

  /** The submit control's wording. */
  protected readonly submitLabel: Signal<string> = computed(() =>
    this.isEditMode() ? EDIT_SUBMIT_LABEL : CREATE_SUBMIT_LABEL,
  );

  /** The cancel control's wording. */
  protected readonly cancelLabel: string = CANCEL_LABEL;

  /** The wording of the two sibling-screen links. */
  protected readonly settingsLinkLabel: string = SETTINGS_LINK_LABEL;

  protected readonly aliasesLinkLabel: string = ALIASES_LINK_LABEL;

  /**
   * The one sentence describing the current failure, or `null` when there is none. The precedence is
   * measured rather than arbitrary, and each arm has a reason: 1. a STATE REFUSAL is worded from the
   * shared conflict vocabulary, which already holds the legacy `DuplicatePortalAlias.Text` verbatim
   * against the code the server publishes for it.
   */
  protected readonly failureMessage: Signal<string | null> = computed(() => {
    const failure: PortalFailure | null = this.failure();

    if (failure === null) {
      return null;
    }

    const refusal: string | null = conflictMessage(failure.conflictCode);

    if (refusal !== null) {
      return refusal;
    }

    if (failure.status === FORBIDDEN_STATUS) {
      return HOST_FIELD_REFUSED_MESSAGE;
    }

    if (failure.status === NOT_FOUND_STATUS) {
      return PORTAL_NOT_FOUND_MESSAGE;
    }

    const modeFallback: string = this.isEditMode() ? UPDATE_ERROR_MESSAGE : CREATE_ERROR_MESSAGE;
    const fallback: string | null =
      failure.status === BAD_REQUEST_STATUS ? null : modeFallback;

    return summarizeProblem(failure.synthesised ? null : failure.problem, fallback).message;
  });

  // ---------------------------------------------------------------------------
  //  VALUES THE TEMPLATE NEEDS BUT MUST NOT RESTATE
  // ---------------------------------------------------------------------------

  protected readonly limits = Object.freeze({
    alias: ALIAS_MAX_LENGTH,
    title: TITLE_MAX_LENGTH,
    description: METADATA_MAX_LENGTH,
    keywords: METADATA_MAX_LENGTH,
    firstName: PERSON_NAME_MAX_LENGTH,
    lastName: PERSON_NAME_MAX_LENGTH,
    username: USERNAME_MAX_LENGTH,
    password: PASSWORD_MAX_LENGTH,
    confirm: PASSWORD_MAX_LENGTH,
    email: EMAIL_MAX_LENGTH,
  });

  /**
   * The portal-type choices, in the legacy declaration order. Published as typed pairs because the VALUES
   * are the load-bearing part — see {@link PortalType} — and a template that wrote `value="P"` as a bare
   * string attribute would not be checked against the control's type.
   */
  protected readonly portalTypeChoices: readonly { readonly value: PortalType; readonly label: string }[] =
    Object.freeze([
      Object.freeze({ value: PARENT_PORTAL_TYPE, label: 'Parent' }),
      Object.freeze({ value: CHILD_PORTAL_TYPE, label: 'Child' }),
    ]);

  // SIDE EFFECTS

  /** Reads the addressed portal whenever the route names one. */
  private readonly readEffect = effect((): void => {
    const id: number | undefined = this.resolvedPortalId();

    if (id === undefined) {
      return;
    }

    untracked(() => {
      this.portalStore.loadPortal(id);
    });
  });

  /**
   * Fills the edit form once the portal has been read. Guarded on the record's IDENTITY, so this runs
   * once per record rather than once per unrelated signal change — which is what stops it overwriting an
   * operator's unsaved edits.
   */
  private readonly hydrateEffect = effect((): void => {
    const detail: PortalDetail | null = this.portal();

    if (detail === null || detail === this.hydratedFrom) {
      return;
    }

    this.hydratedFrom = detail;

    untracked(() => {
      this.editForm.reset({
        title: detail.portalName ?? '',
        description: detail.description ?? '',
        keywords: detail.keyWords ?? '',
      });
    });
  });

  /**
   * Announces a failure once, at the measured severity. The notification carries the OUTCOME; the banner
   * carries the document and the form carries the per-field messages, so the same sentence is never
   * rendered twice by two mechanisms.
   */
  private readonly failureEffect = effect((): void => {
    const failure: PortalFailure | null = this.failure();

    if (failure === null) {
      return;
    }

    untracked(() => {
      const message: string | null = this.failureMessage();

      // The queue refuses a blank message, so nothing is gained by sending one; the
      // banner still shows the document either way.
      if (message !== null && message.length > 0) {
        const severity: NotificationSeverity = failure.severity;

        this.notifications.notify(severity, message, problemSupportReference(failure.problem));
      }

      this.saveRequested.set(false);
    });
  });

  /**
   * Wires the portal-type reaction. A subscription rather than an effect, because the trigger is a form
   * control and a control's value is not a signal in this framework version.
   */
  constructor() {
    this.createForm.controls.portalType.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe((selected: PortalType): void => {
        this.onPortalTypeSelected(selected);
      });

    // ⚠ Pf-M8 — MOVE FOCUS TO THE FIELD A SERVER REFUSAL NAMES.
    effect((): void => {
      const failure: PortalFailure | null = this.failure();

      if (failure === null) {
        this.refusalFocusMoved = false;

        return;
      }

      if (isDuplicateAliasCode(failure.conflictCode) === false) {
        return;
      }

      untracked((): void => {
        // Once per refusal. A resubmission clears the failure first, which resets this flag, so a second
        // collision moves focus again - but a re-render for any other reason does not fight an operator who
        // has already started correcting the entry.
        if (this.refusalFocusMoved) {
          return;
        }

        const control = this.document.getElementById(ALIAS_CONTROL_ELEMENT_ID);

        if (control === null) {
          return;
        }

        this.refusalFocusMoved = true;
        control.focus();
      });
    });
  }

  /**
   * Whether focus has already been moved for the refusal currently in hand. ⚠ Pf-M8 — see the
   * constructor. Plain state rather than a signal: nothing renders from it, and a signal read inside the
   * effect that writes it would be a cycle.
   */
  private refusalFocusMoved = false;

  // ---------------------------------------------------------------------------
  //  FIELD MESSAGES
  // ---------------------------------------------------------------------------

  /**
   * The message to show beside one creation field, or `null` when there is nothing to say. The order is:
   * the client's own rule first, then the cross-field password rule for the confirmation box, then
   * whatever the server reported against the corresponding request member.
   *
   * @param field The control to describe.
   * @returns The message, or `null`.
   */
  protected createMessageFor(field: PortalCreateField): string | null {
    const own: string | null = PortalFormComponent.controlMessage(
      this.createForm.controls[field],
      CREATE_REQUIRED_MESSAGE[field],
    );

    if (own !== null) {
      return own;
    }

    if (field === 'confirm') {
      const mismatch: string | null = this.mismatchMessage();

      if (mismatch !== null) {
        return mismatch;
      }
    }

    const reported: string | null = fieldErrorMessage(
      this.problem(),
      CREATE_FIELD_MEMBER[field],
    );

    if (reported !== null) {
      return reported;
    }

    // The finding reads "the POST-400 path dead-ends", and runtime testing found that the recovery path
    // itself is sound - every typed value survives a refusal, every control stays editable, the submit
    // stays enabled, and correcting the entry and resubmitting works without a reload.
    if (field === 'alias' && isDuplicateAliasCode(this.failure()?.conflictCode ?? null)) {
      return conflictMessage(this.failure()?.conflictCode);
    }

    return null;
  }

  /**
   * @param field The control to describe.
   * @returns The message, or `null`.
   */
  protected editMessageFor(field: PortalEditField): string | null {
    const own: string | null = PortalFormComponent.controlMessage(
      this.editForm.controls[field],
      EDIT_REQUIRED_MESSAGE[field],
    );

    if (own !== null) {
      return own;
    }

    return fieldErrorMessage(this.problem(), EDIT_FIELD_MEMBER[field]);
  }

  /**
   * Resolves one control's own validation message. Nothing is reported until the control has been edited
   * or visited, so a form that is opened and not yet used shows no messages at all — which is what makes
   * pressing submit, and the `markAllAsTouched` that follows an invalid one, the moment every message
   * becomes visible.
   *
   * @param control The control to describe.
   * @param requiredMessage The measured wording for this control's requiredness rule, or `null` when the
   * control has no such rule.
   * @returns The message, or `null` when the control is valid or has not been used.
   */
  private static controlMessage(
    control: AbstractControl,
    requiredMessage: string | null,
  ): string | null {
    if (!control.invalid || !(control.dirty || control.touched)) {
      return null;
    }

    const errors: ValidationErrors | null = control.errors;

    if (errors === null) {
      return null;
    }

    if (errors['required'] !== undefined) {
      return requiredMessage ?? GENERIC_FIELD_MESSAGE;
    }

    const invalidAlias: unknown = errors[INVALID_ALIAS_ERROR];

    if (typeof invalidAlias === 'string') {
      return invalidAlias;
    }

    const overlong: unknown = errors['maxlength'];

    if (typeof overlong === 'object' && overlong !== null) {
      // The framework reports the configured bound on this member. Narrowed rather than
      // trusted, so a future change to the payload cannot put `undefined` in a sentence.
      const requested: unknown = (overlong as { requiredLength?: number }).requiredLength;

      if (typeof requested === 'number') {
        return `Enter at most ${requested} characters.`;
      }
    }

    // The credential minimum. Reported by its OWN sentence rather than by a length template, because
    // the API answers this rule with the legacy policy sentence and the two must not differ.
    if (errors['minlength'] !== undefined) {
      return PASSWORD_TOO_SHORT_MESSAGE;
    }

    // The mail-address format. Only one control on either form carries a pattern rule, so no
    // per-control discrimination is needed and none is invented.
    if (errors['pattern'] !== undefined) {
      return EMAIL_INVALID_MESSAGE;
    }

    return GENERIC_FIELD_MESSAGE;
  }

  /**
   * The cross-field password message, shown beside the confirmation box.
   *
   * @returns The measured mismatch wording, or `null`.
   */
  private mismatchMessage(): string | null {
    const confirm = this.createForm.controls.confirm;

    if (!(confirm.dirty || confirm.touched)) {
      return null;
    }

    const errors: ValidationErrors | null = this.createForm.errors;

    if (errors === null) {
      return null;
    }

    const mismatch: unknown = errors[PASSWORD_MISMATCH_ERROR];

    return typeof mismatch === 'string' ? mismatch : null;
  }

  // ---------------------------------------------------------------------------
  //  INTERACTION
  // ---------------------------------------------------------------------------

  /**
   * Reacts to the portal type changing.
   *
   * @param selected The newly chosen portal type.
   */
  private onPortalTypeSelected(selected: PortalType): void {
    if (this.isEditMode()) {
      return;
    }

    const alias = this.createForm.controls.alias;
    const replaced: string = alias.value;
    const derived: string = selected === CHILD_PORTAL_TYPE ? `${this.hostName}${ALIAS_SEGMENT_SEPARATOR}` : '';

    alias.setValue(derived, {
      emitEvent: false,
    });
    alias.updateValueAndValidity({ emitEvent: false });

    if (replaced.length > 0 && replaced !== derived) {
      this.notifications.warning(ALIAS_REPLACED_MESSAGE);
    }
  }

  /**
   * Submits the form for the current mode.
   *
   * @returns Nothing. The outcome arrives on the store's slices, never as a return value.
   */
  protected onSubmit(): void {
    if (this.isEditMode()) {
      this.submitUpdate();

      return;
    }

    this.submitCreate();
  }

  protected onCancel(): void {
    void this.router.navigate([PORTAL_LIST_ROUTE]);
  }

  // ---------------------------------------------------------------------------
  //  WRITES
  // ---------------------------------------------------------------------------

  private submitCreate(): void {
    if (this.saving()) {
      return;
    }

    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();

      return;
    }

    this.normaliseAliasControl();
    this.normaliseTitleControl(this.createForm.controls.title);

    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();

      return;
    }

    this.saveRequested.set(true);
    this.portalStore
      .createPortal(this.toCreateRequest())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.afterWrite(CREATE_SUCCEEDED_MESSAGE);
      });
  }

  /**
   * Writes the edited portal. REFUSES TO PROCEED WITHOUT A READ RECORD, and that guard is the most
   * important line in this method.
   */
  private submitUpdate(): void {
    if (this.saving()) {
      return;
    }

    const id: number | undefined = this.resolvedPortalId();
    const detail: PortalDetail | null = this.portal();

    if (id === undefined || detail === null) {
      return;
    }

    if (this.editForm.invalid) {
      this.editForm.markAllAsTouched();

      return;
    }

    // R-M20 — see `normaliseTitleControl`. Applied AFTER the first gate and followed by a second, exactly
    // as the creation path above does it, because trimming can shorten a value to nothing and requiredness
    // therefore has to be re-asked rather than assumed settled.
    this.normaliseTitleControl(this.editForm.controls.title);

    if (this.editForm.invalid) {
      this.editForm.markAllAsTouched();

      return;
    }

    const request: UpdatePortalRequest = PortalFormComponent.toUpdateRequest(
      id,
      detail,
      this.editForm.getRawValue(),
    );

    this.saveRequested.set(true);
    this.portalStore
      .updatePortal(id, request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.afterWrite(UPDATE_SUCCEEDED_MESSAGE);
      });
  }

  /**
   * Writes the normalised alias back into its control. `emitEvent: false` because the only listener on
   * this form is the portal-type reaction, and although that reaction watches a different control,
   * suppressing the event states the intent: this write is a display correction rather than an operator
   * edit, and it must not be able to start a cascade.
   */
  private normaliseAliasControl(): void {
    const alias = this.createForm.controls.alias;
    const normalised: string = normaliseAlias(alias.value);

    if (normalised === alias.value) {
      return;
    }

    alias.setValue(normalised, { emitEvent: false });
  }

  /**
   * Trims the site title into its own control — R-M20. ⚠ THIS SCREEN WAS THE ONE THAT DID NOT DO IT, AND
   * THAT WAS THE FINDING. Runtime testing measured three screens against one another: the role editor
   * trims its name into the control before judging it, the site-settings screen trims its title into the
   * control before judging it, and this screen sent whatever was typed.
   *
   * @param control The title control of whichever form is being submitted.
   */
  private normaliseTitleControl(control: FormControl<string>): void {
    const trimmed: string = control.value.trim();

    if (trimmed === control.value) {
      return;
    }

    control.setValue(trimmed, { emitEvent: false });
  }

  /**
   * Composes the creation request from the creation form. Twelve members, one per member the contract
   * declares, each named rather than positional — see annotation 8 on the class.
   *
   * @returns The request to post.
   */
  private toCreateRequest(): CreatePortalRequest {
    const value = this.createForm.getRawValue();

    return {
      portalName: value.title,
      portalAlias: normaliseAlias(value.alias),
      description: value.description,
      keyWords: value.keywords,
      homeDirectory: null,
      templateFile: DEFAULT_TEMPLATE_FILE,
      isChildPortal: value.portalType === CHILD_PORTAL_TYPE,
      administratorFirstName: value.firstName,
      administratorLastName: value.lastName,
      administratorUsername: value.username,
      administratorPassword: value.password,
      administratorEmail: value.email,
    };
  }

  /**
   * Composes the update request from the read record and the three edited fields. TWENTY-SEVEN MEMBERS,
   * AND EVERY ONE OF THEM IS WRITTEN. The endpoint replaces the whole row and the server substitutes
   * nought for an omitted numeric, so a request that mentioned only what this screen edits would silently
   * rewrite everything it did not mention.
   *
   * @param portalId The portal addressed by the route.
   * @param detail The record as it was read from the server.
   * @param edited The three edited values, from the edit form.
   * @returns The request to put.
   */
  private static toUpdateRequest(
    portalId: number,
    detail: PortalDetail,
    edited: { readonly title: string; readonly description: string; readonly keywords: string },
  ): UpdatePortalRequest {
    return {
      // From the route. Both `0` and `-1` are real identifiers and neither is adjusted.
      portalId,

      // The three this screen edits.
      portalName: edited.title,
      description: edited.description,
      keyWords: edited.keywords,

      // Everything else, carried forward verbatim.
      logoFile: detail.logoFile,
      footerText: detail.footerText,
      expiryDate: detail.expiryDate,
      userRegistration: detail.userRegistration,
      bannerAdvertising: detail.bannerAdvertising,
      currency: detail.currency,
      administratorId: detail.administratorId,
      hostFee: detail.hostFee,
      hostSpace: detail.hostSpace,
      pageQuota: detail.pageQuota,
      userQuota: detail.userQuota,
      paymentProcessor: detail.paymentProcessor,
      processorUserId: detail.processorUserId,
      processorCredentialReference: null,
      backgroundFile: detail.backgroundFile,
      siteLogHistory: detail.siteLogHistory,
      splashTabId: detail.splashTabId,
      homeTabId: detail.homeTabId,
      loginTabId: detail.loginTabId,
      userTabId: detail.userTabId,
      defaultLanguage: detail.defaultLanguage,
      timeZoneOffset: detail.timeZoneOffset,
      homeDirectory: detail.homeDirectory,

      // ⚠ THE REVISION THIS SUBMISSION WAS COMPOSED AGAINST, ROUND-TRIPPED VERBATIM, AND THE ONE MEMBER
      // HERE THAT IS NOT A PORTAL ATTRIBUTE. Every member above is either edited by this screen or carried
      // forward from `detail`, so the payload REPLACES the whole record - and roughly twenty of those
      // carried-forward values are ones this screen never displays.
      concurrencyToken: detail.concurrencyToken,
    };
  }

  /**
   * Reports a successful write and leaves the screen. the destination is the portal list, not the new
   * portal's own address.
   *
   * @param message The confirmation to queue.
   */
  private afterWrite(message: string): void {
    this.saveRequested.set(false);

    // ⚠ THE FORMS ARE SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS ALREADY
    // SAVED. Clearing `saveRequested` on the line above is precisely what makes `saving()` false, so from
    // this point on the guard's probe sees a dirty form with no save in flight - and the navigation below
    // is the one the save itself triggers.
    this.createForm.markAsPristine();
    this.createForm.markAsUntouched();
    this.editForm.markAsPristine();
    this.editForm.markAsUntouched();

    // BOTH controls, and `reset` rather than `setValue('')`, so the value, the dirty flag and the touched
    // flag all go together and no validation message about a credential that no longer exists is left on
    // the screen.
    this.createForm.controls.password.reset('');
    this.createForm.controls.confirm.reset('');

    // `true`: the confirmation is raised immediately before a deliberate redirect and is meant to be
    // read at the destination - see the redirect below.
    this.notifications.success(message, true);

    // ⚠ REPLACED, NOT PUSHED, AND THE CONFIRMATION IS MARKED TO SURVIVE THE TRIP. The shell retires
    // notifications on a completed navigation, and this one is raised in the same task as the navigation on
    // the next line, so an unmarked confirmation was swept before it could be painted and the comment above
    // this method says the list 'is where the written row is visible', which is exactly why it has to
    // survive the trip there.
    void this.router.navigate([PORTAL_LIST_ROUTE], { replaceUrl: true }).catch(() => false);
  }
}
