import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import type { Signal } from '@angular/core';
import type { ValidationErrors } from '@angular/forms';

import { problemDetailsFieldErrors } from '../../../core/models/problem-details.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
} from '../../../core/models/problem-details.model';
import type { CreateRoleGroupRequest } from '../../../core/models/role.model';
import { NotificationService } from '../../../core/services/notification.service';
import { RoleStore } from '../../../core/state/role.store';
import type { RoleStoreFailure } from '../../../core/state/role.store';
import { requiredText } from '../../../core/utils/required-text.validator';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

// Wording. Exported constants rather than inline literals, so that the template and the specification read
// the same string instead of two copies that can drift apart.

/** The screen's heading. */
export const ROLE_GROUP_FORM_TITLE = 'Add New Role Group';

/** Label for the name field. */
export const ROLE_GROUP_NAME_LABEL = 'Group Name';

export const ROLE_GROUP_NAME_HELP = 'Enter the name of the role group.';

export const DESCRIPTION_LABEL = 'Description';

export const DESCRIPTION_HELP = 'Enter a description of the role group.';

/**
 * Shown when the name is missing. the resource value unescapes to a leading `<br>`, a Web Forms layout
 * device for a validator that rendered inline after the input.
 */
export const ROLE_GROUP_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/** Shown when the portal already holds a group of that name, verbatim from the legacy. */
export const DUPLICATE_ROLE_GROUP_MESSAGE =
  'A role group with the same name already exists. The new group was not added.';

/** Shown when the server refuses the write, verbatim from the legacy denial page. */
export const ACCESS_DENIED_MESSAGE =
  'Either you are not currently logged in, or you do not have access to this content.';

/**
 * Announced after a successful creation. This sentence has no legacy antecedent - the legacy redirected
 * without saying anything, and the screen's resource file declares no success key, so the redirect was
 * the only feedback the operator got.
 *
 * ⚠ "ADDED" RATHER THAN "CREATED" IS DELIBERATE, AND IT WAS RE-EXAMINED - QA-10. Measured against the other
 * five confirmations on this feature, this sentence uses a different VERB from the role's own "The role was
 * created." for what is the same kind of operation, and it was re-worded to match - then reverted, because
 * {@link DUPLICATE_ROLE_GROUP_MESSAGE} immediately below is LEGACY VERBATIM and reads "...The new group was
 * not added." Aligning the success sentence to a sibling SCREEN would therefore have made the success and
 * failure sentences on THIS screen disagree with each other, and an operator sees one or the other of those
 * two on the same screen, whereas they never see a role confirmation and a group confirmation together. The
 * same-screen pair is the one that has to agree, and the legacy message is the half that cannot move. The
 * loose entity label ("group" beside "role group") is the legacy's own: its single sentence uses both.
 *
 * ⚠ THE NAME-OMISSION IS NOT AN INCONSISTENCY EITHER, AND MUST NOT BE "FIXED". QA observed that create and
 * update
 * confirmations omit the record's name while delete confirmations quote it. That follows a rule rather than
 * an oversight: a create or an update happens on the record's OWN FORM, where the subject is on screen and
 * naming it again says nothing; a delete is invoked FROM A LIST, where the operator has to be told which of
 * many records went. Every one of the six confirmations obeys that rule, including the group update, which
 * is invoked from an inline panel on the listing and does quote the name.
 */
export const ROLE_GROUP_CREATED_MESSAGE = 'The new group was added.';

/**
 * Shown when nothing came back at all. MIGRATION: also a net addition - a full-page postback either
 * returned a page or failed in the browser, so the legacy had no vocabulary for a request that never
 * arrived.
 */
export const NETWORK_UNAVAILABLE_MESSAGE =
  'The server could not be reached. Check your connection and try again.';

/**
 * Shown when the failure is not a response at all. Reachable, and not merely defensive: an interceptor or
 * an operator in the pipeline can throw any value, and such a value carries no status and no problem
 * document.
 */
export const UNEXPECTED_FAILURE_MESSAGE = 'The request could not be completed.';

/**
 * The label on the commit action.
 *
 * ⚠ THIS SCREEN ONLY EVER CREATES, SO THE LABEL SAYS SO. The legacy `EditGroups.ascx` was a dual-mode
 * screen - it carried `cmdUpdate`, `cmdCancel` AND `cmdDelete` under the title "Edit Role Group" - and its
 * commit link was declared `text="Update"` with no resource override, one label serving both the create and
 * the edit path. Only the create path was migrated: `role-groups/new` is the sole role-group route, editing
 * a group happens inline on the listing instead, and this screen's own title reads "Add New Role Group"
 * while its progress spinner reads "Adding role group…". Carrying "Update" across therefore left the
 * primary button contradicting the heading above it and the spinner beside it, and telling the operator
 * they were about to modify something that does not yet exist.
 */
export const CREATE_ACTION_LABEL = 'Add Role Group';

export const CANCEL_ACTION_LABEL = 'Cancel';

/**
 * Longest accepted group name, from the legacy input's `maxlength`. Matches `dbo.RoleGroups.RoleGroupName
 * nvarchar(50)` and the length the API's own request contract documents, so all three agree.
 */
export const ROLE_GROUP_NAME_MAX_LENGTH = 50;

/** Longest accepted description, from the legacy input's `maxlength`. */
export const DESCRIPTION_MAX_LENGTH = 1000;

/**
 * The name control's key inside the form group. The spelling is load-bearing in two places at once, which
 * is why it is a constant.
 */
export const ROLE_GROUP_NAME_CONTROL = 'roleGroupName';

export const DESCRIPTION_CONTROL = 'description';

/** The name input's DOM identifier, used as the field label's `for`. */
export const ROLE_GROUP_NAME_INPUT_ID = 'role-group-name';

export const DESCRIPTION_INPUT_ID = 'role-group-description';

/** The address both actions navigate to. */
export const ROLES_PATH = '/roles';

/** The key server-reported field messages are stored under on a control. */
const SERVER_ERROR_KEY = 'server';

/** The error key Angular's own length validator reports under. */
const MAX_LENGTH_ERROR_KEY = 'maxlength';

const REQUIRED_ERROR_KEY = 'required';

/** The shared empty result, so an unchanged derivation keeps a stable identity. */
const NO_MESSAGES: readonly string[] = Object.freeze([]);

// The statuses this endpoint can produce, named rather than written as bare numbers at the comparison
// sites: 0 for no response at all, 400 for a model-state or domain refusal, 403 for a caller who does not
// administer the resolved tenant, 409 for a name the portal already holds, and 422 - handled beside 400,
// because both can carry a per-field dictionary and the API is free to choose either.
const TRANSPORT_FAILURE_STATUS = 0;

const BAD_REQUEST_STATUS = 400;

const FORBIDDEN_STATUS = 403;

const CONFLICT_STATUS = 409;

const UNPROCESSABLE_ENTITY_STATUS = 422;

// Deliberately NOT declared: 429. The server's global limiter places a request in the credential partition
// from ENDPOINT METADATA - the `[CredentialEndpoint]` marker - rather than from its path, and the
// role-group write actions carry no such marker, so this endpoint does not draw that budget.

/**
 * The shape of the creation form: the group's two editable facts, and nothing else. Declared as an
 * interface and used as the type argument to `FormGroup`, which is what makes `form.value` fully typed
 * rather than a `Partial<...>`.
 */
export interface RoleGroupFormModel {
  roleGroupName: FormControl<string>;

  description: FormControl<string>;
}

/**
 * Builds the creation form in its initial state. A free function rather than inline construction, so the
 * same shape can be built by a specification without reaching into the component.
 *
 * @returns A form group whose controls both hold the empty string.
 */
export function createRoleGroupForm(): FormGroup<RoleGroupFormModel> {
  return new FormGroup<RoleGroupFormModel>({
    roleGroupName: new FormControl<string>('', {
      nonNullable: true,
      validators: [requiredText, Validators.maxLength(ROLE_GROUP_NAME_MAX_LENGTH)],
    }),
    description: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.maxLength(DESCRIPTION_MAX_LENGTH)],
    }),
  });
}

/**
 * Returns the server's own document, with the transport status filled in when it is absent.
 *
 * @param document The document the response carried, or null when it carried none.
 * @param status The transport status, which is always present.
 * @returns A document carrying a status.
 */
function withResolvedStatus(document: ProblemDetails | null, status: number): ProblemDetails {
  if (document === null) {
    return { status };
  }

  return document.status === undefined ? { ...document, status } : document;
}

/**
 * Returns the server's document with the legacy sentence substituted for its `detail`.
 *
 * @param document The document the response carried, or null when it carried none.
 * @param status The transport status the branch was selected on.
 * @param detail The legacy sentence to present.
 * @returns A document carrying the legacy wording and the branch's status.
 */
function withLegacyWording(
  document: ProblemDetails | null,
  status: number,
  detail: string,
): ProblemDetails {
  if (document === null) {
    return { status, detail };
  }

  return { ...document, status, detail };
}

/**
 * Collects the messages to display beneath one control. Two sources, in a deliberate order:
 * server-reported messages first, because they describe the submission that was just refused, then the
 * locally-derived required message, because it describes what is missing now.
 *
 * @param control The control to read.
 * @param requiredMessage The sentence for a missing value, or null when the control has no required rule.
 * @returns The messages, or the shared empty list.
 */
function controlMessages(
  control: FormControl<string>,
  requiredMessage: string | null,
): readonly string[] {
  const errors: ValidationErrors | null = control.errors;

  if (errors === null) {
    return NO_MESSAGES;
  }

  const messages: string[] = [];

  // Indexed reads, because `ValidationErrors` is an index-signature type and this workspace enables
  // `noPropertyAccessFromIndexSignature`. Widened to `unknown` on the way out so that nothing loosely typed
  // escapes into the rest of this function.
  const reported: unknown = errors[SERVER_ERROR_KEY];

  if (Array.isArray(reported)) {
    for (const message of reported) {
      if (typeof message === 'string' && message.trim().length > 0) {
        messages.push(message);
      }
    }
  }

  const missing: unknown = errors[REQUIRED_ERROR_KEY];

  if (requiredMessage !== null && missing === true && control.touched) {
    messages.push(requiredMessage);
  }

  // Reported unconditionally rather than only once touched, which is deliberate and differs from the
  // required rule above.
  const overlong: unknown = errors[MAX_LENGTH_ERROR_KEY];

  if (typeof overlong === 'object' && overlong !== null) {
    const bound: unknown = (overlong as { requiredLength?: unknown }).requiredLength;

    if (typeof bound === 'number') {
      // Wording identical to the sibling role form's, so one violation reads the same way on both
      // screens of this feature rather than being described two ways.
      messages.push(`Enter at most ${String(bound)} characters.`);
    }
  }

  return messages.length === 0 ? NO_MESSAGES : messages;
}

/** Creates a role group. Reached at `role-groups/new` and nowhere else. */
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE. Every screen's header carries exactly one
 * subtitle stating that screen's SCOPE: the record it acts on when the title does not already name it,
 * and otherwise what the screen is for, in one line. It never carries a status, a count or a progress
 * readout - those belong to the live region that owns them, and a count in two places is two owners of
 * one fact. Measured finding: subtitles appeared on ten of the twenty screens and carried three
 * different kinds of thing, so a reader could not tell what the slot was for.
 */
const PAGE_SUBTITLE =
  'A group collects related security roles under one name.';

@Component({
  selector: 'app-role-group-form',
  standalone: true,
  // No confirmation dialogue is imported, because this screen has no destructive action to
  // confirm.
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
  ],
  templateUrl: './role-group-form.component.html',
  styleUrl: './role-group-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleGroupFormComponent {
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
    () => this.form.dirty,
  );
  /**
   * The shared role state, and the only route to the API from this screen. The transport is deliberately
   * not injected.
   */
  private readonly store = inject(RoleStore);

  private readonly router = inject(Router);

  private readonly notifications = inject(NotificationService);

  /**
   * Ties the per-control event subscriptions in the constructor to this component's lifetime. Captured as
   * a field because `inject` requires an injection context.
   */
  private readonly destroyRef = inject(DestroyRef);

  /** The creation form. */
  readonly form: FormGroup<RoleGroupFormModel> = createRoleGroupForm();

  /**
   * Whether a creation issued by THIS screen is still outstanding. A marker of our own rather than the
   * store's shared `saving` flag, and the distinction is what keeps the outcome bridge honest.
   */
  private readonly creationOutstanding = signal(false);

  /** The failure to present, or null when there is none. */
  private readonly _problem = signal<ProblemDetails | null>(null);

  /**
   * Bumped whenever either control's validity or touched state changes. The bridge that makes the two
   * message derivations below genuinely reactive.
   */
  private readonly controlRevision = signal(0);

  readonly submitting: Signal<boolean> = this.creationOutstanding.asReadonly();

  /** The failure to present, or null when there is none. */
  readonly problem: Signal<ProblemDetails | null> = this._problem.asReadonly();

  readonly roleGroupNameError: Signal<readonly string[]> = computed(() => {
    this.controlRevision();

    return controlMessages(this.form.controls.roleGroupName, ROLE_GROUP_NAME_REQUIRED_MESSAGE);
  });

  /** Messages for the description field. */
  readonly descriptionError: Signal<readonly string[]> = computed(() => {
    this.controlRevision();

    return controlMessages(this.form.controls.description, null);
  });

  // The wording and limits the template binds, exposed rather than repeated as literals so
  // that the rendered screen and the specification read the same constants.

  /** The one-line scope statement shown beneath the title. */
  readonly pageSubtitle = PAGE_SUBTITLE;

  readonly pageTitle = ROLE_GROUP_FORM_TITLE;

  readonly roleGroupNameLabel = ROLE_GROUP_NAME_LABEL;

  readonly roleGroupNameHelp = ROLE_GROUP_NAME_HELP;

  readonly roleGroupNameMaxLength = ROLE_GROUP_NAME_MAX_LENGTH;

  readonly roleGroupNameControl = ROLE_GROUP_NAME_CONTROL;

  readonly roleGroupNameInputId = ROLE_GROUP_NAME_INPUT_ID;

  readonly descriptionLabel = DESCRIPTION_LABEL;

  readonly descriptionHelp = DESCRIPTION_HELP;

  readonly descriptionMaxLength = DESCRIPTION_MAX_LENGTH;

  readonly descriptionControl = DESCRIPTION_CONTROL;

  readonly descriptionInputId = DESCRIPTION_INPUT_ID;

  readonly createActionLabel = CREATE_ACTION_LABEL;

  readonly cancelActionLabel = CANCEL_ACTION_LABEL;

  constructor() {
    for (const control of [this.form.controls.roleGroupName, this.form.controls.description]) {
      control.events.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
        this.controlRevision.update((revision) => revision + 1);
      });
    }

    // Three conditions decide, and all three are necessary. The marker proves the creation was ours; the
    // flag falling proves it has settled; and the recorded failure - matched on the operation, because the
    // store clears any earlier failure before each write - proves which way it settled.
    effect(() => {
      const outstanding: boolean = this.creationOutstanding();
      const inFlight: boolean = this.store.saving();
      const failure: RoleStoreFailure | null = this.store.failure();

      if (!outstanding || inFlight) {
        return;
      }

      untracked(() => {
        this.creationOutstanding.set(false);

        if (failure !== null && failure.operation === 'createRoleGroup') {
          this.present(failure);

          return;
        }

        this.onCreated();
      });
    });
  }

  /** Creates the group, then returns to the roles list. */
  submit(): void {
    // An in-flight creation is not repeated: this is a create, so a duplicated request would attempt a
    // duplicated row. The template also disables the action while a request is in flight; this guard is
    // what makes the invariant hold regardless of what the template does.
    if (this.creationOutstanding()) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    // ⚠ THE NAME IS TIDIED INTO ITS OWN CONTROL, NOT ON THE WAY INTO THE REQUEST, so the value that was
    // validated and the value that is sent are the same string, and the operator is never left looking at
    // an entry that differs from the one that was accepted.
    this.normaliseRoleGroupName();

    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    this.creationOutstanding.set(true);
    this._problem.set(null);

    // Exactly the two members the API's creation contract declares. The tenant is not sent the server
    // resolves it from the request host and the caller's claims, which is where the legacy read it from too
    // - and the identifier is not sent either, because the server assigns it.
    const request: CreateRoleGroupRequest = {
      roleGroupName: this.form.controls.roleGroupName.value,
      description: this.form.controls.description.value,
    };

    this.store.createRoleGroup(request);
  }

  /**
   * Trims the padding off the group name, in its own control. `emitEvent: false` because this is a
   * DISPLAY CORRECTION rather than an operator edit, so it must not start a cascade through any listener
   * on this form.
   */
  private normaliseRoleGroupName(): void {
    const control = this.form.controls.roleGroupName;
    const trimmed: string = control.value.trim();

    if (trimmed === control.value) {
      return;
    }

    control.setValue(trimmed, { emitEvent: false });
  }

  /** Abandons the form and returns to the roles list. */
  cancel(): void {
    this.goToRoles();
  }

  /** Announces the creation and leaves for the roles list. */
  private onCreated(): void {
    // No re-read is asked for here. The store's own creation action re-reads the group listing when the
    // write succeeds, so asking again would issue a second identical request and race the first: whichever
    // answered last would decide what the roles list showed.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
    // retires notifications on a completed navigation, and this one is raised in the same task as the
    // navigation on the next line, so it was swept before it could be painted.
    this.notifications.notify('success', ROLE_GROUP_CREATED_MESSAGE, null, true);
    this.goToRoles(true);
  }

  /**
   * Presents a failure in the page, beside the form, and keeps the operator on it. Staying put is
   * behaviour rather than a default: the legacy stopped immediately after presenting the duplicate-name
   * message, leaving the operator on the screen with what they had typed intact.
   *
   * @param failure The failure the store recorded for this screen's own creation.
   */
  private present(failure: RoleStoreFailure): void {
    const status: number | null = failure.status;
    const document: ProblemDetails | null = failure.problem;

    if (status === null) {
      // Nothing to work with at all. Presented rather than swallowed, because the legacy's
      // outermost handler made every failure visible.
      this._problem.set({ detail: UNEXPECTED_FAILURE_MESSAGE });

      return;
    }

    if (status === TRANSPORT_FAILURE_STATUS) {
      this._problem.set({ status, detail: NETWORK_UNAVAILABLE_MESSAGE });

      return;
    }

    if (status === CONFLICT_STATUS) {
      this._problem.set(withLegacyWording(document, status, DUPLICATE_ROLE_GROUP_MESSAGE));

      return;
    }

    if (status === FORBIDDEN_STATUS) {
      this._problem.set(withLegacyWording(document, status, ACCESS_DENIED_MESSAGE));

      return;
    }

    if (status === BAD_REQUEST_STATUS || status === UNPROCESSABLE_ENTITY_STATUS) {
      this.applyReportedFieldErrors(document);
      this._problem.set(withResolvedStatus(document, status));

      return;
    }

    // Any other status, including one this application never produced. Presented with the
    // server's own wording, or with wording derived from the status when it supplied none.
    this._problem.set(withResolvedStatus(document, status));
  }

  /**
   * Puts the server's per-field messages onto the controls they describe. `problemDetailsFieldErrors`
   * lower-cases the first character of every key it returns, so the API's Pascal-cased model-state keys
   * arrive in the casing a form control is named in, and a compound name survives intact.
   *
   * @param document The document the response carried, or null when it carried none.
   */
  private applyReportedFieldErrors(document: ProblemDetails | null): void {
    const reported: ProblemDetailsErrors = problemDetailsFieldErrors(document);

    this.report(this.form.controls.roleGroupName, reported[ROLE_GROUP_NAME_CONTROL]);
    this.report(this.form.controls.description, reported[DESCRIPTION_CONTROL]);
  }

  /**
   * Records the server's messages for one control. Merged into the control's existing errors rather than
   * replacing them, because setting errors outright would discard a locally-derived one that is still
   * true - a name can be both missing and reported on.
   *
   * @param control The control the messages belong to.
   * @param messages The messages, or undefined when the server reported none.
   */
  private report(control: FormControl<string>, messages: readonly string[] | undefined): void {
    // The parameter admits `undefined` even though an indexed read of the dictionary is typed as present.
    // That is not belt-and-braces: this workspace does not enable unchecked-index reporting, so the
    // compiler types a missing key as though it were there while the run time hands back `undefined`.
    if (messages === undefined || messages.length === 0) {
      return;
    }

    control.setErrors({ ...control.errors, [SERVER_ERROR_KEY]: messages });
    control.markAsTouched();
  }

  /**
   * Navigates to the roles list. ⚠ THE ROLES LIST IS THE LEGACY DESTINATION FOR BOTH OUTCOMES, and it is
   * not a stand-in for a role-group listing that this application declines to build.
   */
  private goToRoles(replace = false): void {
    // ⚠ THE PUSHED CALL CARRIES NO OPTIONS AT ALL, rather than `{ replaceUrl: false }`.
    const departure = replace
      ? this.router.navigate([ROLES_PATH], { replaceUrl: true })
      : this.router.navigate([ROLES_PATH]);

    void departure.catch(() => false);
  }
}
