//
// Role-group creation for the dnn-migration administration front end.
//
// Create-only, by route rather than by preference. The legacy control served creation and editing from one
// file, discriminating on a query-string identifier; the route table declares a single address for this
// component, `role-groups/new`, with no identifier segment, so an edit branch here would have no way in.
// Renaming and deleting a group are reached inline from the roles list, which is where the legacy put them
// too. The consequence is exact: two actions, Update and Cancel, and no delete method, no manage method and no
// confirmation dialogue.
//
// Failure is presented in the page, through the shared error banner, and NOT through the notification queue.
// `core/interceptors/error.interceptor.ts` already queues exactly one notification for every failed response,
// so a component that queued its own would say the same thing twice, and the second would carry generic status
// wording rather than the legacy sentence this migration has to preserve. The banner is also the faithful
// mechanism, since the legacy presented both of these outcomes as an in-page block beside the form. Severity
// comes out right without this file choosing it - the banner derives it from the problem document's status,
// mapping 409 to the danger band the legacy used for a duplicate name and 403 to the warning band it used for
// a refusal. Success is the one outcome the interceptor never sees, so success IS announced through the queue.
//
// Every string below is plain text, and that is a security boundary. Legacy resource values are untrusted
// markup, stored XML-escaped so that a naive search finds none and would wrongly conclude the risk is absent;
// unescaping them reveals HTML tags, script tags among them. So this file declares strings and nothing else,
// each bound as text by the framework's default interpolation, which escapes by construction. There is no
// trusted-markup value here and no member whose name suggests one.
//
// MIGRATION: localisation is NOT ported. That mechanism was Web Forms specific and the framework's own
// translation package is outside the closed dependency surface. The legacy resource files are the reference
// for phrasing, so the surface stays recognisable to an existing operator, and nothing more.
//

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

// Wording. Exported constants rather than inline literals, so that the template and the
// specification read the same string instead of two copies that can drift apart. Each is
// transcribed from the legacy resource value for the corresponding control property, under
// `Website/admin/Security/App_LocalResources`.

/**
 * The screen's heading.
 *
 * MIGRATION: taken from a different resource file than the rest, deliberately. The only
 * title this screen's own resource file declares is the edit-mode wording, and this
 * component only ever creates, so the heading is the wording the legacy itself put on the
 * link that reaches creation - the one registered with no identifier argument.
 */
export const ROLE_GROUP_FORM_TITLE = 'Add New Role Group';

/**
 * Label for the name field.
 *
 * The resource value embeds a trailing colon and it is transcribed here without one,
 * because punctuation belongs to the shared field component: its `normaliseLabelText`
 * strips one trailing colon and applies none of its own. The same applies to
 * {@link DESCRIPTION_LABEL}.
 */
export const ROLE_GROUP_NAME_LABEL = 'Group Name';

export const ROLE_GROUP_NAME_HELP = 'Enter the name of the role group.';

export const DESCRIPTION_LABEL = 'Description';

export const DESCRIPTION_HELP = 'Enter a description of the role group.';

/**
 * Shown when the name is missing.
 *
 * MIGRATION: the resource value unescapes to a leading `<br>`, a Web Forms layout device
 * for a validator that rendered inline after the input. It is transcribed already stripped
 * because the shared field component owns that placement now and the message is bound as
 * text, where a literal `<br>` would be shown to a person as four characters.
 */
export const ROLE_GROUP_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/**
 * Shown when the portal already holds a group of that name, verbatim from the legacy.
 *
 * The legacy presented this sentence in its danger band and then stopped, leaving the
 * operator on the form with what they typed intact. Both halves of that behaviour are
 * preserved: the sentence, and staying put.
 */
export const DUPLICATE_ROLE_GROUP_MESSAGE =
  'A role group with the same name already exists. The new group was not added.';

/**
 * Shown when the server refuses the write, verbatim from the legacy denial page.
 *
 * Presented as a refusal rather than a fault, which is the band the legacy used for it in
 * both of its branches.
 */
export const ACCESS_DENIED_MESSAGE =
  'Either you are not currently logged in, or you do not have access to this content.';

/**
 * Announced after a successful creation.
 *
 * MIGRATION: this sentence has no legacy antecedent - the legacy redirected without saying
 * anything, and the screen's resource file declares no success key, so the redirect was the
 * only feedback the operator got. It is phrased as the exact inverse of the duplicate
 * sentence above, so the two outcomes read as one another's opposite.
 */
export const ROLE_GROUP_CREATED_MESSAGE = 'The new group was added.';

/**
 * Shown when nothing came back at all.
 *
 * MIGRATION: also a net addition - a full-page postback either returned a page or failed in
 * the browser, so the legacy had no vocabulary for a request that never arrived. Worded as
 * a condition the operator can act on rather than as a fault in the application, because
 * nothing was refused: nothing was received.
 */
export const NETWORK_UNAVAILABLE_MESSAGE =
  'The server could not be reached. Check your connection and try again.';

/**
 * Shown when the failure is not a response at all.
 *
 * Reachable, and not merely defensive: an interceptor or an operator in the pipeline can
 * throw any value, and such a value carries no status and no problem document. The legacy
 * wrapped every one of its handlers in an outermost catch that made the failure visible, so
 * a silent swallow here would be a regression rather than a simplification.
 */
export const UNEXPECTED_FAILURE_MESSAGE = 'The request could not be completed.';

export const UPDATE_ACTION_LABEL = 'Update';

export const CANCEL_ACTION_LABEL = 'Cancel';

/**
 * Longest accepted group name, from the legacy input's `maxlength`.
 *
 * Matches `dbo.RoleGroups.RoleGroupName nvarchar(50)` and the length the API's own request
 * contract documents, so all three agree. Exported because the template needs it for the
 * input's `maxlength` attribute as well as for the validator: the attribute PREVENTS
 * over-typing rather than reporting it afterwards, which is why no length message is
 * declared - the required sentence is the only message this screen had.
 */
export const ROLE_GROUP_NAME_MAX_LENGTH = 50;

/**
 * Longest accepted description, from the legacy input's `maxlength`.
 *
 * The name field carries a validator and this one carries none. That asymmetry is the
 * legacy's, read off its markup: a required-field validator against the name, and a
 * multi-line text box with a length bound and no validator of any kind for the description.
 * So the description is genuinely optional and length is its only bound.
 */
export const DESCRIPTION_MAX_LENGTH = 1000;

/**
 * The name control's key inside the form group.
 *
 * The spelling is load-bearing in two places at once, which is why it is a constant. The
 * template binds it with `formControlName`, and the server's per-field validation dictionary
 * is matched against it: `problemDetailsFieldErrors` lower-cases the first character of each
 * key it returns, so the API's `RoleGroupName` arrives as `roleGroupName` and lands on this
 * control. Renaming this would silently stop server messages reaching the field they
 * describe, and no compiler would object.
 */
export const ROLE_GROUP_NAME_CONTROL = 'roleGroupName';

export const DESCRIPTION_CONTROL = 'description';

/**
 * The name input's DOM identifier, used as the field label's `for`.
 *
 * Kebab-cased rather than reusing the control key, because this is a document identifier and
 * the control key is a form key; keeping them distinct means neither can be changed in the
 * belief that it only affects the other. The same holds for {@link DESCRIPTION_INPUT_ID}.
 */
export const ROLE_GROUP_NAME_INPUT_ID = 'role-group-name';

export const DESCRIPTION_INPUT_ID = 'role-group-description';

/**
 * The address both actions navigate to.
 *
 * One constant for both, because the legacy sent both there - a successful creation and a
 * create-mode Cancel went to the roles list alike. Absolute, so navigation does not depend
 * on where this screen happens to be mounted.
 */
export const ROLES_PATH = '/roles';

/**
 * The key server-reported field messages are stored under on a control.
 *
 * Distinct from every framework key on purpose, so that a server message can be told apart
 * from a locally-derived one and cleared independently. It holds an array, because the API's
 * per-field dictionary maps one field to a list.
 */
const SERVER_ERROR_KEY = 'server';

/**
 * The error key Angular's own length validator reports under.
 *
 * Named here rather than written inline for the same reason the two keys around it are: this
 * workspace enables `noPropertyAccessFromIndexSignature`, so every read out of a
 * `ValidationErrors` bag is an indexed read and a bare string literal at the read site is how a
 * key comes to be misspelled with nothing to catch it.
 */
const MAX_LENGTH_ERROR_KEY = 'maxlength';

const REQUIRED_ERROR_KEY = 'required';

/** The shared empty result, so an unchanged derivation keeps a stable identity. */
const NO_MESSAGES: readonly string[] = Object.freeze([]);

// The statuses this endpoint can produce, named rather than written as bare numbers at the
// comparison sites: 0 for no response at all, 400 for a model-state or domain refusal, 403
// for a caller who does not administer the resolved tenant, 409 for a name the portal
// already holds, and 422 - handled beside 400, because both can carry a per-field dictionary
// and the API is free to choose either.
const TRANSPORT_FAILURE_STATUS = 0;

const BAD_REQUEST_STATUS = 400;

const FORBIDDEN_STATUS = 403;

const CONFLICT_STATUS = 409;

const UNPROCESSABLE_ENTITY_STATUS = 422;

// Deliberately NOT declared: 429. The server's global limiter places a request in the
// credential partition from ENDPOINT METADATA - the `[CredentialEndpoint]` marker - rather
// than from its path, and the role-group write actions carry no such marker, so this
// endpoint does not draw that budget. Verify the marker before adding a branch: if one is
// ever applied here, 429 becomes reachable and this note is what must change first.
//
// Deliberately NOT handled: 401. `core/interceptors/auth.interceptor.ts` owns that whole
// lifecycle - detection, the single refresh, the single retry and discarding the session
// when the refresh fails - and this component would otherwise present a session that is
// about to be renewed without the operator noticing as a failure.

/**
 * The shape of the creation form: the group's two editable facts, and nothing else.
 *
 * Declared as an interface and used as the type argument to `FormGroup`, which is what makes
 * `form.value` fully typed rather than a `Partial<...>`. Both controls are constructed
 * non-nullable, so `.value` is `string` rather than `string | null` and `reset()` returns to
 * the empty string rather than to `null`.
 *
 * Two members, where the legacy assembled four. The tenant is now resolved server-side from
 * the request host and the caller's claims, so it is neither a field nor a payload member,
 * and the identifier the legacy used as its mode discriminator is assigned by the server.
 * The API's own creation contract carries exactly these two, so the form, the payload and
 * the endpoint agree without anything being dropped in between.
 */
export interface RoleGroupFormModel {
  roleGroupName: FormControl<string>;

  description: FormControl<string>;
}

/**
 * Builds the creation form in its initial state.
 *
 * A free function rather than inline construction, so the same shape can be built by a
 * specification without reaching into the component. The validator sets are the legacy's
 * exactly: the name carries the presence rule and its length bound, and the description
 * carries its length bound alone. Nothing else is added - no pattern, no minimum length, no
 * required rule on the description - because the legacy markup declares nothing else.
 *
 * ⚠ NO PATTERN RULE, AND THAT IS MEASURED RATHER THAN OVERLOOKED. `EditGroups.ascx:L12` declares
 * ONE validator on this field, a `RequiredFieldValidator`, and no in-scope administration screen
 * declares a pattern-matching validator of any kind. Adding a character restriction here would
 * refuse names the legacy accepted and that may already be stored, and it would disagree with
 * `CreateRoleGroupRequestValidator`, which declares presence and a length bound and nothing else.
 * Markup-bearing text is safe by a different mechanism: every value this application renders goes
 * through Angular interpolation, which escapes it, so a name containing angle brackets is
 * displayed as text and never parsed as markup.
 *
 * ⚠ THE PRESENCE RULE IS THE SHARED TRIM-AWARE ONE. `Validators.required` rejects only the empty
 * string, so three spaces satisfy it - whereas an ASP.NET `RequiredFieldValidator` trimmed the
 * value before comparing it to its initial value and refused exactly that, and the API's
 * `NotEmpty` treats a whitespace-only string as empty. It reports under the framework's own
 * `required` key, so exactly one message is outstanding and the template needs no second branch,
 * and `Validators.required` is NOT declared alongside it: the shared rule refuses everything the
 * framework rule refuses and more, so a second rule would only ever duplicate the same key.
 *
 * It is the SAME rule the sibling role form carries, which is the point - the two screens refuse a
 * whitespace-only name at the same moment and in the same words.
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

// Both helpers below guarantee a status on the document they return, and that guarantee is
// what makes the banner paint the right band. The banner derives its severity from the status
// carried in the BODY, and RFC 7807 makes every member optional - a document written by a
// proxy rather than by this API can carry none at all - so a refusal whose body omitted its
// status would otherwise be painted as a fault.

/**
 * Returns the server's own document, with the transport status filled in when it is absent.
 *
 * Everything else is preserved untouched, including the trace and correlation identifiers a
 * person is asked to quote in a support conversation.
 *
 * @param document The document the response carried, or null when it carried none.
 * @param status The transport status, which is always present.
 * @returns A document carrying a status.
 */
function withResolvedStatus(document: ProblemDetails | null, status: number): ProblemDetails {
  if (document === null) {
    return { status };
  }

  // Compared against `undefined` explicitly rather than coalesced. A status is a number,
  // and a coalescing operator on a number is the habit that turns a legitimate zero into
  // a substituted default elsewhere in this codebase.
  return document.status === undefined ? { ...document, status } : document;
}

/**
 * Returns the server's document with the legacy sentence substituted for its `detail`.
 *
 * Used for the two outcomes whose wording this migration has to preserve. The server's own
 * `detail` is replaced rather than appended to, because the legacy showed exactly one
 * sentence for each of them; every other member survives. The transport status is imposed
 * rather than merely defaulted, because these two branches are selected on it - substituting
 * wording for one status while presenting another would be incoherent.
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
 * Collects the messages to display beneath one control.
 *
 * Two sources, in a deliberate order: server-reported messages first, because they describe
 * the submission that was just refused, then the locally-derived required message, because
 * it describes what is missing now.
 *
 * The required message is gated on the control being TOUCHED, reproducing the legacy
 * validator's dynamic display, which rendered nothing until there was something to report.
 * Server messages are NOT gated: a refusal has already happened, and withholding its
 * explanation until the operator happens to focus and blur the field would hide the only
 * account of what went wrong.
 *
 * @param control The control to read.
 * @param requiredMessage The sentence for a missing value, or null when the control has no
 * required rule.
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

  // Indexed reads, because `ValidationErrors` is an index-signature type and this
  // workspace enables `noPropertyAccessFromIndexSignature`. Widened to `unknown` on the
  // way out so that nothing loosely typed escapes into the rest of this function.
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

  // THE LENGTH BOUND, WHICH THIS FUNCTION USED TO IGNORE ENTIRELY. Both controls on this screen
  // carry `Validators.maxLength`, and neither reported it: an over-length value therefore made the
  // control `ng-invalid` while this function returned NO messages, so the field rendered no error
  // text, its error container was never created, `aria-invalid` stayed absent - the template derives
  // it from this array being non-empty - the border kept its ordinary colour, and pressing Update
  // did nothing at all because the form was invalid with nothing on screen saying so. A silent dead
  // end, and it was reported as one. The sibling role form has always reported the same violation.
  //
  // Reported unconditionally rather than only once touched, which is deliberate and differs from the
  // required rule above. A required message before the person has typed anything would scold them
  // for a field they have not reached yet; a length message can only exist because they have ALREADY
  // typed too much, so withholding it until blur hides the explanation at exactly the moment it is
  // needed. This also matches the sibling form, which gates on neither touch nor submission.
  //
  // The number comes from what the framework REPORTS rather than from a constant looked up here, so
  // the sentence and the rule can never name different bounds. Narrowed rather than trusted: a shape
  // change must put no `undefined` into a sentence shown to a person.
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

/**
 * Creates a role group.
 *
 * Reached at `role-groups/new` and nowhere else. The class name and the file path are both
 * load-bearing: the route table imports this module dynamically and reads this export by
 * name, so renaming either resolves to `undefined` at run time and produces a blank screen
 * with no compile error to warn anybody.
 *
 * Declares no inputs, and that is the contract rather than an omission. The address has no
 * identifier segment, so there is nothing for `withComponentInputBinding()` to bind; an input
 * named for the group's identifier would never receive a value and would misinform the next
 * reader into thinking an edit mode exists.
 *
 * It attaches no guard and re-implements none. The route table owns that, and a route guard
 * is a navigation affordance in any case - the endpoint this screen calls is policy-protected
 * server-side, which is why the 403 branch below is a real branch rather than a theoretical
 * one.
 *
 * MIGRATION: view state is gone, with nothing to port; state here is held in signals and in
 * the form. The legacy navigation asymmetry is resolved rather than reproduced - a successful
 * creation went to the roles list while a successful update came back to the same screen, so
 * one screen had two notions of "done", and being create-only this component has one. The
 * description remains a plain multi-line text area: it was never rich text on this screen,
 * and the rich-text provider is out of scope in any case.
 */
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
    FocusFirstInvalidDirective,
  ],
  templateUrl: './role-group-form.component.html',
  styleUrl: './role-group-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleGroupFormComponent {

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
    () => this.form.dirty && this.store.saving() === false,
  );
  /**
   * The shared role state, and the only route to the API from this screen.
   *
   * The transport is deliberately not injected. A store whose write is driven from a component
   * subscription holds one copy of the outcome while the component holds another, and the two
   * are free to disagree - the group list refreshed by one path, the navigation decided by the
   * other. The outcome bridge in the constructor supplies the completion point the store's
   * `void`-returning command does not, without a second copy of anything.
   *
   * The refresh remains the store's: its creation action re-reads the group listing on
   * success, so the group is immediately selectable in the roles list's group filter and
   * nothing here asks for that read again. No URL is composed here either - the service owns
   * every endpoint template, and a path assembled in a component would be a second, divergent
   * statement of the same address.
   */
  private readonly store = inject(RoleStore);

  private readonly router = inject(Router);

  private readonly notifications = inject(NotificationService);

  /**
   * Ties the per-control event subscriptions in the constructor to this component's lifetime.
   *
   * Captured as a field because `inject` requires an injection context. It retires the
   * form-revision bridge below and nothing else: the store owns every request this screen
   * issues and cancels them itself.
   */
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The creation form.
   *
   * Public because the template binds it and a specification fills it; reaching a protected
   * member from a specification would mean subverting the access modifier at every assertion.
   */
  readonly form: FormGroup<RoleGroupFormModel> = createRoleGroupForm();

  /**
   * Whether a creation issued by THIS screen is still outstanding.
   *
   * A marker of our own rather than the store's shared `saving` flag, and the distinction is
   * what keeps the outcome bridge honest. The store raises `saving` for every role write in
   * the application, so reading it alone would let a write started elsewhere settle this
   * screen's form, announce a creation nobody performed and navigate away from a form the
   * operator was still filling in. Set immediately before the command is issued and cleared
   * by the bridge when it settles.
   */
  private readonly creationOutstanding = signal(false);

  /** The failure to present, or null when there is none. */
  private readonly _problem = signal<ProblemDetails | null>(null);

  /**
   * Bumped whenever either control's validity or touched state changes.
   *
   * The bridge that makes the two message derivations below genuinely reactive. A form control
   * is not a signal, so a `computed()` reading `control.errors` directly would memoise its
   * first answer and never recompute - and under `OnPush` the field would then show the first
   * message it ever resolved and silently ignore every later one. Reading this counter inside
   * those derivations establishes the dependency, so they recompute exactly when the controls
   * change and at no other time.
   */
  private readonly controlRevision = signal(0);

  readonly submitting: Signal<boolean> = this.creationOutstanding.asReadonly();

  /**
   * The failure to present, or null when there is none.
   *
   * Bound to the shared error banner, which carries the live region that announces it and
   * derives its own severity from the status this document carries.
   */
  readonly problem: Signal<ProblemDetails | null> = this._problem.asReadonly();

  readonly roleGroupNameError: Signal<readonly string[]> = computed(() => {
    this.controlRevision();

    return controlMessages(this.form.controls.roleGroupName, ROLE_GROUP_NAME_REQUIRED_MESSAGE);
  });

  /**
   * Messages for the description field.
   *
   * Server-reported messages only, and the null argument below is the whole point: the legacy
   * declared no validator on this field, so there is no local sentence to show and none is
   * invented. Its length bound is enforced by the control and prevented by the input's
   * `maxlength`, neither of which the legacy reported in words.
   */
  readonly descriptionError: Signal<readonly string[]> = computed(() => {
    this.controlRevision();

    return controlMessages(this.form.controls.description, null);
  });

  // The wording and limits the template binds, exposed rather than repeated as literals so
  // that the rendered screen and the specification read the same constants.

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

  readonly updateActionLabel = UPDATE_ACTION_LABEL;

  readonly cancelActionLabel = CANCEL_ACTION_LABEL;

  constructor() {
    // Subscribed per control rather than to the group, so the bridge depends on nothing about
    // how events propagate up a control tree: these two controls are exactly what the
    // derivations read, and both `markAllAsTouched` and a validity change on a child emit
    // through the child's own stream.
    for (const control of [this.form.controls.roleGroupName, this.form.controls.description]) {
      control.events.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
        this.controlRevision.update((revision) => revision + 1);
      });
    }

    /*
     * The outcome bridge: the completion point the store's `void`-returning command does not
     * provide. An effect rather than a subscription, because announcing an outcome and
     * navigating away are both genuine side effects; it loads nothing.
     *
     * Three conditions decide, and all three are necessary. The marker proves the creation was
     * ours; the flag falling proves it has settled; and the recorded failure - matched on the
     * operation, because the store clears any earlier failure before each write - proves which
     * way it settled. Dropping the operation match would let the group re-read that follows a
     * successful creation report its own failure as a failed creation, and the store performs
     * exactly that re-read.
     *
     * The marker is cleared inside `untracked` so the write stays out of this effect's own
     * dependency set, and it is cleared BEFORE the outcome is acted on so a navigation cannot
     * re-enter this effect with the marker still set.
     */
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

  /**
   * Creates the group, then returns to the roles list.
   *
   * The Update equivalent, and it validates: the legacy link button took the framework's
   * default of causing validation and its handler was guarded on the page being valid. An
   * invalid form marks every control touched and stops, which is what reproduces the legacy
   * validator's dynamic display - nothing until the page was submitted, then beside the
   * field. Nothing is sent, so an invalid form costs no request.
   */
  submit(): void {
    // An in-flight creation is not repeated: this is a create, so a duplicated request would
    // attempt a duplicated row. The template also disables the action while a request is in
    // flight; this guard is what makes the invariant hold regardless of what the template
    // does.
    if (this.creationOutstanding()) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    // ⚠ THE NAME IS TIDIED INTO ITS OWN CONTROL, NOT ON THE WAY INTO THE REQUEST, so the value
    // that was validated and the value that is sent are the same string, and the operator is never
    // left looking at an entry that differs from the one that was accepted.
    //
    // Only padding can reach here. The presence rule on the control trims before judging, so a
    // whitespace-only name was already refused by the check above and the tidy cannot change
    // whether the value is acceptable - only what is stored.
    //
    // MIGRATION: trimming the padding is a deliberate divergence, and it reverses an earlier
    // decision recorded on this screen that the value should never be rewritten. The legacy stored
    // what was posted, padding and all. It is trimmed because THE NAME CANNOT BE CORRECTED
    // AFTERWARDS: the closed route set carries no role-group edit screen, so a group created with
    // invisible leading spaces would keep them for good, sorting ahead of every other group in the
    // scope filter this form exists to populate. The sibling role form applies the same rule for
    // the same reason, which is what makes the two screens agree.
    this.normaliseRoleGroupName();

    // Defence in depth rather than a live branch. Trimming can only shorten, so the length bound
    // cannot be newly breached, and emptiness was settled above by a rule that already trimmed.
    // It stays because that argument rests entirely on the presence rule remaining trim-aware.
    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    this.creationOutstanding.set(true);
    this._problem.set(null);

    // Exactly the two members the API's creation contract declares. The tenant is not sent -
    // the server resolves it from the request host and the caller's claims, which is where the
    // legacy read it from too - and the identifier is not sent either, because the server
    // assigns it.
    //
    // The description is forwarded exactly as typed. An empty one travels as the EMPTY STRING and
    // is neither coalesced to null nor omitted: the API serialises with its null-omission
    // condition set to never, so a member is always present, and the legacy null contract made the
    // empty string - not nothing - the absent-string marker. Turning `''` into `null` here would
    // change what is stored. IT IS NOT TRIMMED EITHER, and the asymmetry with the name above is
    // deliberate: a description is free text whose leading indentation may be intended, and it can
    // be nothing at all, whereas the name is an identifier the operator can never correct.
    //
    // The name is read from the control AFTER the tidy above, so what is sent is what the control
    // now shows.
    const request: CreateRoleGroupRequest = {
      roleGroupName: this.form.controls.roleGroupName.value,
      description: this.form.controls.description.value,
    };

    // The created group is deliberately not read back: this screen navigates away, and the
    // store's own creation action re-reads the group listing the destination needs. The
    // outcome arrives through the bridge in the constructor, not through a subscription here.
    this.store.createRoleGroup(request);
  }

  /**
   * Trims the padding off the group name, in its own control.
   *
   * `emitEvent: false` because this is a DISPLAY CORRECTION rather than an operator edit, so it
   * must not start a cascade through any listener on this form. `setValue` re-runs the control's
   * validators regardless of that flag, which is what lets the caller re-read validity immediately
   * afterwards and find it reflecting the tidied value.
   *
   * A control whose value is already trimmed is left completely alone, so an unnecessary write
   * cannot mark a pristine form dirty.
   *
   * ⚠ ONLY THE NAME. The description keeps what was typed; see the note at the request above.
   */
  private normaliseRoleGroupName(): void {
    const control = this.form.controls.roleGroupName;
    const trimmed: string = control.value.trim();

    if (trimmed === control.value) {
      return;
    }

    control.setValue(trimmed, { emitEvent: false });
  }

  /**
   * Abandons the form and returns to the roles list.
   *
   * The Cancel equivalent, and it deliberately does NOT validate - the legacy link button
   * declared that it caused no validation. So nothing is marked touched, no message appears,
   * and an incomplete form is abandoned rather than argued with: Cancel navigates even when
   * the name is missing. There is no confirmation prompt, because the legacy attached its only
   * confirmation to the delete button that creation hid and this component does not render.
   */
  cancel(): void {
    this.goToRoles();
  }

  /**
   * Announces the creation and leaves for the roles list.
   *
   * The store's re-read is what makes the new group usable at the destination: the roles list
   * filters by group, and the legacy returned the operator there precisely so the group they
   * had just created was available to select.
   */
  private onCreated(): void {
    // No re-read is asked for here. The store's own creation action re-reads the group listing
    // when the write succeeds, so asking again would issue a second identical request and race
    // the first: whichever answered last would decide what the roles list showed. Reached only
    // from the outcome bridge, so it cannot run before the creation has actually succeeded.
    // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS
    // ALREADY SAVED. The probe registered above reads `dirty && saving() === false`, and by the time
    // this method runs the write has SETTLED - so `saving()` has already returned to false while the
    // controls are still dirty from the operator's typing. Measured in a real browser: creating a
    // group succeeded, the confirmation painted at the listing, and the operator was then asked
    // 'You have unsaved changes on this page. Leave without saving and discard them?' about the very
    // entry that had just been stored. Marking the form settled is the honest statement of what
    // happened, and it is what the role form does on the same path; the guard is left exactly as it
    // is, because it is right about every case except a completed save.
    this.form.markAsPristine();
    this.form.markAsUntouched();

    // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The
    // shell retires notifications on a completed navigation, and this one is raised in the same task
    // as the navigation on the next line, so it was swept before it could be painted. The legacy
    // announced and then redirected too, so the destination is where this message belongs.
    this.notifications.notify('success', ROLE_GROUP_CREATED_MESSAGE, null, true);
    this.goToRoles(true);
  }

  /**
   * Presents a failure in the page, beside the form, and keeps the operator on it.
   *
   * Staying put is behaviour rather than a default: the legacy stopped immediately after
   * presenting the duplicate-name message, leaving the operator on the screen with what they
   * had typed intact. Nothing queues a notification here, because
   * `core/interceptors/error.interceptor.ts` has already queued exactly one for this response -
   * see the note at the head of this file.
   *
   * @param failure The failure the store recorded for this screen's own creation.
   */
  private present(failure: RoleStoreFailure): void {
    // Read from the store's record, not from an `HttpErrorResponse`. The two facts this method
    // branches on go missing independently, which is why the store carries them separately: a
    // transport failure has a status and no document, and a document may omit its own status
    // member. Reading the status off the document would collapse that distinction.
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
      // The one conflict this endpoint can report: the portal already holds a group of that
      // name. The legacy sentence is substituted for the server's own wording, and the 409
      // status carries it into the banner's danger band.
      this._problem.set(withLegacyWording(document, status, DUPLICATE_ROLE_GROUP_MESSAGE));

      return;
    }

    if (status === FORBIDDEN_STATUS) {
      this._problem.set(withLegacyWording(document, status, ACCESS_DENIED_MESSAGE));

      return;
    }

    if (status === BAD_REQUEST_STATUS || status === UNPROCESSABLE_ENTITY_STATUS) {
      // Field messages onto the fields, and the document to the banner as well. Both are
      // needed: the banner is the only place a message keyed to the request as a whole,
      // rather than to a field, can appear.
      this.applyReportedFieldErrors(document);
      this._problem.set(withResolvedStatus(document, status));

      return;
    }

    // Any other status, including one this application never produced. Presented with the
    // server's own wording, or with wording derived from the status when it supplied none.
    this._problem.set(withResolvedStatus(document, status));
  }

  /**
   * Puts the server's per-field messages onto the controls they describe.
   *
   * `problemDetailsFieldErrors` lower-cases the first character of every key it returns, so the
   * API's Pascal-cased model-state keys arrive in the casing a form control is named in, and a
   * compound name survives intact. A key that matches no control - the empty key a form-level
   * failure uses, or a key for a member this form does not carry - is deliberately not forced
   * onto a field; those reach the operator through the banner, which renders them.
   *
   * @param document The document the response carried, or null when it carried none.
   */
  private applyReportedFieldErrors(document: ProblemDetails | null): void {
    const reported: ProblemDetailsErrors = problemDetailsFieldErrors(document);

    this.report(this.form.controls.roleGroupName, reported[ROLE_GROUP_NAME_CONTROL]);
    this.report(this.form.controls.description, reported[DESCRIPTION_CONTROL]);
  }

  /**
   * Records the server's messages for one control.
   *
   * Merged into the control's existing errors rather than replacing them, because setting
   * errors outright would discard a locally-derived one that is still true - a name can be both
   * missing and reported on. The messages clear themselves the moment the operator edits the
   * field, because re-validating replaces the error object: a stale refusal must not outlive
   * the value it was about. The control is marked touched so the messages are displayed
   * immediately, without waiting for a focus and blur the operator has no reason to perform.
   *
   * @param control The control the messages belong to.
   * @param messages The messages, or undefined when the server reported none.
   */
  private report(control: FormControl<string>, messages: readonly string[] | undefined): void {
    // The parameter admits `undefined` even though an indexed read of the dictionary is
    // typed as present. That is not belt-and-braces: this workspace does not enable
    // unchecked-index reporting, so the compiler types a missing key as though it were
    // there while the run time hands back `undefined`. The check is what closes that gap.
    if (messages === undefined || messages.length === 0) {
      return;
    }

    control.setErrors({ ...control.errors, [SERVER_ERROR_KEY]: messages });
    control.markAsTouched();
  }

  /**
   * Navigates to the roles list.
   *
   * ⚠ THE ROLES LIST IS THE LEGACY DESTINATION FOR BOTH OUTCOMES, and it is not a stand-in for a
   * role-group listing that this application declines to build. `EditGroups.ascx.vb:L163-L169` is
   * the whole of `cmdCancel_Click`: when `RoleGroupID = -1` - which is the creation state, and the
   * only state this screen has, since the closed route set carries no group edit route - it
   * redirects to `NavigateURL(TabId, "")`, the same tab with no control key. That tab is the
   * Security Roles page. THERE WAS NEVER A ROLE-GROUP LISTING TO RETURN TO: groups appear in the
   * roles page's own scope filter (`Roles.ascx.vb:L127-L129` hides that filter outright when no
   * group exists), which is exactly why the legacy sent the operator there and why the successful
   * case lands in the same place - the group just created is immediately selectable.
   *
   * The rejection is absorbed rather than left unhandled. A navigation settles to false when a
   * guard refuses it and rejects only when a guard or resolver throws; neither is something
   * this screen can recover from, and an unhandled rejection would surface as console noise
   * instead of anywhere an operator could act on it.
   */
  private goToRoles(replace = false): void {
    /*
     * ⚠ THE TWO CALLERS WANT DIFFERENT HISTORY BEHAVIOUR, so the choice is a parameter rather
     * than a fixed option. {@link cancel} PUSHES: an operator who changed their mind may change
     * it back, and Back returning them to the form they abandoned is the useful answer.
     * {@link onCreated} REPLACES: the group exists now, so Back onto its creation form would
     * offer to create it a second time.
     *
     * The distinction is also read by `core/guards/unsaved-changes.guard.ts`, which treats a
     * replacing departure as application-initiated and lets it through unchallenged. That is why
     * the successful path must replace: a form still dirty from the values that were just saved
     * would otherwise be met with a prompt offering to discard them.
     *
     * Cancel being a push is what lets the gate challenge it, which is correct — Cancel sits one
     * click from Update, and silently discarding a filled-in form was the measured defect.
     *
     * @param replace Whether to overwrite the current history entry instead of adding one.
     */
    /*
     * ⚠ THE PUSHED CALL CARRIES NO OPTIONS AT ALL, rather than `{ replaceUrl: false }`. The two
     * are identical to the router, but not to a reader or to a specification: passing an explicit
     * `false` would rewrite the call every cancel path makes, so a diff of this change would
     * claim to have touched the cancel behaviour when it did not. Only the departure that
     * actually changed says anything new.
     */
    const departure = replace
      ? this.router.navigate([ROLES_PATH], { replaceUrl: true })
      : this.router.navigate([ROLES_PATH]);

    void departure.catch(() => false);
  }
}
