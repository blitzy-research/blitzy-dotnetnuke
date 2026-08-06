//
// Role-group creation for the dnn-migration administration front end.
//
// ---------------------------------------------------------------------------
// WHAT THIS SCREEN IS, AND WHY IT IS CREATE-ONLY
// ---------------------------------------------------------------------------
// It replaces `Website/admin/Security/EditGroups.ascx` and its 194-line code-behind,
// reduced to creation alone. The reduction is measured rather than chosen:
//
//   * `EditGroups.ascx.vb:L42` seeds its private field to -1 and `:L61-L62` overwrites
//     it from the query string, so the legacy control served BOTH modes from one file.
//   * `:L83-L84` is the create branch, and the whole of its body is
//     `cmdDelete.Visible = False` - creation hid the delete affordance outright.
//   * `:L113` chooses between them, and `:L120` sends a successful creation to the
//     roles list while `:L121-L124` sends a successful update back to this same
//     screen.
//
// The route table declares one address for this component, `role-groups/new`, with no
// identifier segment. So the edit branch has no way in, and reproducing it here would
// author two thirds of a screen nothing can reach. Group renaming and group deletion
// are reached inline from the roles list instead, which is where the legacy put them
// too: `roles.ascx:L5-L16` carries a group selector, an inline edit link and a delete
// image button in one row, driven by `Roles.ascx.vb:L79-L86`.
//
// The consequence for this file is exact: TWO actions, Update and Cancel. There is no
// delete method, no manage method and no confirmation dialogue. `EditGroups.ascx`
// declares three link buttons against the role editor's four, and the third of them is
// the delete this screen never renders.
//
// ---------------------------------------------------------------------------
// WHERE FAILURE IS PRESENTED, AND WHY NOT THROUGH THE NOTIFICATION QUEUE
// ---------------------------------------------------------------------------
// This is the one design decision in the file that is not obvious from the legacy
// source, so it is recorded here rather than left to be rediscovered.
//
// `core/interceptors/error.interceptor.ts` already queues EXACTLY ONE notification for
// every failed response, at a severity derived by the single rule the workspace owns.
// Its branches were read rather than assumed: a 401 is silent because the
// authentication interceptor owns that lifecycle; a transport failure announces its own
// sentence; a validation document whose per-field dictionary carries anything is
// SILENT, because the form is expected to render those messages against the fields; and
// everything else is announced.
//
// A component that also queued a notification for the refusals it recognises would
// therefore produce TWO messages for one failure, and the second would be the generic
// status wording rather than the legacy sentence this migration has to preserve. So the
// component-owned surface is the shared error banner, which is also the FAITHFUL
// mechanism: the legacy presented both of these outcomes through
// `UI.Skins.Skin.AddModuleMessage`, an in-page block beside the form, never a
// transient global message.
//
// The severity comes out right without this file choosing it. The banner derives its
// presentation from the problem document's status through `problemSeverity`, which maps
// 409 to a fault and 403 to a refusal, so a duplicate name is painted in the danger
// band that `ModuleMessageType.RedError` was (`EditGroups.ascx.vb:L117`) and a
// permission refusal in the warning band that `ModuleMessageType.YellowWarning` was
// (`AccessDenied.ascx.vb:L43` and `:L45`, which use it in BOTH branches). Painting a
// refusal as a fault would tell an operator something is broken when the system is
// working exactly as configured.
//
// Success is the one outcome the interceptor never sees, so success IS announced
// through the notification queue.
//
// ---------------------------------------------------------------------------
// EVERY STRING IS PLAIN TEXT, AND THAT IS A SECURITY BOUNDARY
// ---------------------------------------------------------------------------
// The wording below is transcribed from the legacy resource files, and legacy resource
// values are untrusted markup. Across the 37 in-scope files under
// `Website/admin/*/App_LocalResources`, 76 values carry an HTML tag once the XML
// entities are unescaped - a naive search finds none, because the markup is stored
// escaped, and would wrongly conclude the risk is absent - and four of those are
// SCRIPT tags. This screen's own `ModuleHelp.Text` carries `<h1>` and `<p>`; it is read
// for context and deliberately not rendered, because module help has no home in the
// shared component set.
//
// So this file declares STRINGS and nothing else. Two of them are transcribed with
// their legacy markup already removed - the required message drops the `<br>` its
// resource value opens with - and every one is bound as text by the framework's default
// interpolation, which escapes by construction. There is no trusted-markup value here
// and no member whose name suggests one. The legacy reached the same conclusion by
// hand: `AccessDenied.ascx.vb:L43` passes its untrusted query-string message through
// `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))` before display.
//
// MIGRATION: localisation is NOT ported. The legacy resolved all of this through
// resource files keyed `<ControlID>.<Property>`; that mechanism is Web Forms specific, the
// framework's own translation package is outside the closed dependency surface, and none
// of the legacy localisation calls is reproduced. The resource files are the reference for
// phrasing so the surface stays recognisable to an existing operator, and nothing more.
//

import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { finalize } from 'rxjs';

import type { Signal } from '@angular/core';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

import {
  isProblemDetails,
  problemDetailsFieldErrors,
} from '../../../core/models/problem-details.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
} from '../../../core/models/problem-details.model';
import type { CreateRoleGroupRequest } from '../../../core/models/role.model';
import { NotificationService } from '../../../core/services/notification.service';
import { RoleService } from '../../../core/services/role.service';
import { RoleStore } from '../../../core/state/role.store';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

// ---------------------------------------------------------------------------
// WORDING
// ---------------------------------------------------------------------------
// Exported constants rather than inline literals, so that the template renders and the
// specification asserts the SAME string instead of two copies that can drift apart.
// Each carries the resource key it was transcribed from.

/**
 * The screen's heading.
 *
 * MIGRATION: taken from a DIFFERENT resource file than the rest, deliberately. The only
 * title this screen's own resource file declares is
 * `EditGroups.ascx.resx` → `ControlTitle_editgroup.Text` = 'Edit Role Group', which is
 * the EDIT-mode wording, and this component only ever creates. So the heading comes
 * from the wording the legacy itself put on the link that reaches creation:
 * `Roles.ascx.resx` → `AddGroup.Action` = 'Add New Role Group'. That link is the
 * create-mode entry point rather than a guess -
 * `Roles.ascx.vb:L308` registers the action with `EditUrl("EditGroup")` and NO
 * identifier argument, against `:L84` which passes one for the edit link.
 */
export const ROLE_GROUP_FORM_TITLE = 'Add New Role Group';

/**
 * Label for the name field, from `EditGroups.ascx.resx` → `plRoleGroupName.Text`.
 *
 * The resource value is 'Group Name:', with the colon EMBEDDED. It is transcribed here
 * without it, because punctuation belongs to the shared field component: its
 * `normaliseLabelText` strips one trailing colon and applies none of its own, so a
 * label carrying one would be stripped anyway and a second colon could never appear.
 * Passing the bare noun makes the intent explicit at the call site.
 */
export const ROLE_GROUP_NAME_LABEL = 'Group Name';

/** Help text for the name field, from `EditGroups.ascx.resx` → `plRoleGroupName.Help`. */
export const ROLE_GROUP_NAME_HELP = 'Enter the name of the role group.';

/**
 * Label for the description field, from `EditGroups.ascx.resx` → `plDescription.Text`.
 *
 * The resource value is 'Description:'; the colon is dropped for the reason given on
 * {@link ROLE_GROUP_NAME_LABEL}.
 */
export const DESCRIPTION_LABEL = 'Description';

/** Help text for the description field, from `EditGroups.ascx.resx` → `plDescription.Help`. */
export const DESCRIPTION_HELP = 'Enter a description of the role group.';

/**
 * Shown when the name is missing, from `EditGroups.ascx.resx` → `valRoleGroupName.Text`.
 *
 * MIGRATION: the resource value is stored as `&lt;br&gt;You Must Enter a Valid Name`,
 * which unescapes to a leading `<br>`. The break is a Web Forms layout device - the
 * validator rendered inline, immediately after the input, so the markup pushed the
 * message onto its own line - and it is dropped rather than carried, because the shared
 * field component owns that placement now and the message is bound as TEXT, where a
 * literal `<br>` would be displayed to a person as four characters.
 *
 * The already-stripped form is transcribed rather than stripped at run time so that
 * nothing has to be resolved to know what this screen says. Stripping is nonetheless
 * idempotent downstream: the shared field component runs its own break normalisation
 * over every message. Note the legacy corpus uses both spacings - `<br>` immediately
 * followed by the sentence here, and `<br>` followed by a space in
 * `SecurityRoles.ascx.resx` → `valEffectiveDate.Text` - so any run-time stripping has
 * to absorb the whitespace as well.
 */
export const ROLE_GROUP_NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';

/**
 * Shown when the portal already holds a group of that name, from
 * `EditGroups.ascx.resx` → `DuplicateRoleGroup.Text`, verbatim.
 *
 * `EditGroups.ascx.vb:L115-L119` wraps the create call in its own `Try`, presents this
 * sentence as `ModuleMessageType.RedError` and then `Exit Sub`, leaving the operator on
 * the form with what they typed intact. Both halves of that behaviour are preserved:
 * the sentence, and staying put.
 */
export const DUPLICATE_ROLE_GROUP_MESSAGE =
  'A role group with the same name already exists. The new group was not added.';

/**
 * Shown when the server refuses the write, from
 * `AccessDenied.ascx.resx` → `AccessDenied.Text`, verbatim.
 *
 * Presented as a REFUSAL rather than a fault. `AccessDenied.ascx.vb` is 50 lines, its
 * `Page_Load` performs no permission check of its own - it only presents a denial - and
 * both of its branches pass `ModuleMessageType.YellowWarning`.
 */
export const ACCESS_DENIED_MESSAGE =
  'Either you are not currently logged in, or you do not have access to this content.';

/**
 * Announced after a successful creation.
 *
 * MIGRATION: this sentence has NO legacy antecedent. `EditGroups.ascx.vb:L120` redirects
 * without saying anything, and the screen's resource file declares no success key, so
 * confirmation is a net addition rather than a translation - the redirect was the only
 * feedback the operator got. It is phrased as the exact inverse of the duplicate
 * sentence above, whose closing clause is 'The new group was not added.', so the two
 * outcomes read as one another's opposite. Success messaging is within the legacy
 * vocabulary even though this screen did not use it: `ModuleMessageType.GreenSuccess`
 * occurs 12 times across the in-scope administration pages.
 */
export const ROLE_GROUP_CREATED_MESSAGE = 'The new group was added.';

/**
 * Shown when nothing came back at all.
 *
 * MIGRATION: also a net addition, and for the same reason as
 * {@link ROLE_GROUP_CREATED_MESSAGE} - a full-page postback either returned a page or
 * failed in the browser, so the legacy had no vocabulary for a request that never
 * arrived. Worded as a condition the operator can act on rather than as a fault in the
 * application, because nothing was refused: nothing was received.
 */
export const NETWORK_UNAVAILABLE_MESSAGE =
  'The server could not be reached. Check your connection and try again.';

/**
 * Shown when the failure is not a response at all.
 *
 * Reachable, and not merely defensive: an interceptor or an operator in the pipeline can
 * throw any value, and such a value carries no status and no problem document. The
 * legacy equivalent is the outermost `Catch exc As Exception` that wraps every one of
 * this control's four handlers, each ending in `ProcessModuleLoadException`
 * (`EditGroups.ascx.vb:L88`, `:L126`, `:L147`, `:L170`). That handler made the failure
 * VISIBLE, so a silent swallow here would be a regression rather than a simplification.
 */
export const UNEXPECTED_FAILURE_MESSAGE = 'The request could not be completed.';

/** The Update action, from `App_GlobalResources/SharedResources.resx` → `cmdUpdate.Text`. */
export const UPDATE_ACTION_LABEL = 'Update';

/** The Cancel action, from `App_GlobalResources/SharedResources.resx` → `cmdCancel.Text`. */
export const CANCEL_ACTION_LABEL = 'Cancel';

// ---------------------------------------------------------------------------
// FIELD LIMITS
// ---------------------------------------------------------------------------

/**
 * Longest accepted group name, from `EditGroups.ascx:L11` `maxlength="50"`.
 *
 * Matches `dbo.RoleGroups.RoleGroupName nvarchar(50)` and the length the API's own
 * request contract documents, so all three agree.
 *
 * Exported because the template needs it for the input's `maxlength` attribute as well
 * as for the validator. The attribute is not decoration: it is the whole of what the
 * legacy expressed about length here, and it PREVENTS over-typing rather than reporting
 * it afterwards. That is why no length message is declared above - see
 * {@link ROLE_GROUP_NAME_REQUIRED_MESSAGE}, which is the only message this screen had.
 */
export const ROLE_GROUP_NAME_MAX_LENGTH = 50;

/**
 * Longest accepted description, from `EditGroups.ascx:L17` `maxlength="1000"`.
 *
 * ⚠ The name field carries a validator and this one carries NONE. That asymmetry is the
 * legacy's, read directly off the markup: `:L12` declares a
 * `requiredfieldvalidator` against the name, and `:L17` declares the description's text
 * box with `maxlength`, `textmode="MultiLine"` and a height, and no validator of any
 * kind. So the description is genuinely optional and length is its only bound.
 */
export const DESCRIPTION_MAX_LENGTH = 1000;

// ---------------------------------------------------------------------------
// CONTROL IDENTIFIERS
// ---------------------------------------------------------------------------

/**
 * The name control's key inside the form group.
 *
 * The spelling is load-bearing in two places at once, which is why it is a constant. The
 * template binds it with `formControlName`, and the server's per-field validation
 * dictionary is matched against it: `problemDetailsFieldErrors` lower-cases the first
 * character of each key it returns, so the API's `RoleGroupName` arrives as
 * `roleGroupName` and lands on this control. Renaming this would silently stop server
 * messages reaching the field they describe - no compiler would object.
 */
export const ROLE_GROUP_NAME_CONTROL = 'roleGroupName';

/** The description control's key inside the form group. See {@link ROLE_GROUP_NAME_CONTROL}. */
export const DESCRIPTION_CONTROL = 'description';

/**
 * The name input's DOM identifier, used as the field label's `for`.
 *
 * Kebab-cased rather than reusing the control key, because this is a document identifier
 * and the control key is a form key; keeping them distinct means neither can be changed
 * in the belief that it only affects the other.
 */
export const ROLE_GROUP_NAME_INPUT_ID = 'role-group-name';

/** The description input's DOM identifier. See {@link ROLE_GROUP_NAME_INPUT_ID}. */
export const DESCRIPTION_INPUT_ID = 'role-group-description';

/**
 * The address both actions navigate to.
 *
 * ONE constant for both, because the legacy sent both there: `EditGroups.ascx.vb:L120`
 * redirects a successful creation to the roles list and `:L165-L166` redirects
 * create-mode Cancel to the same place. Absolute, so navigation does not depend on where
 * this screen happens to be mounted.
 */
export const ROLES_PATH = '/roles';

// ---------------------------------------------------------------------------
// ERROR KEYS
// ---------------------------------------------------------------------------

/**
 * The key server-reported field messages are stored under on a control.
 *
 * Distinct from every framework key on purpose, so that a server message can be told
 * apart from a locally-derived one and cleared independently. It holds an ARRAY, because
 * the API's per-field dictionary maps one field to a list.
 */
const SERVER_ERROR_KEY = 'server';

/** The framework's key for a missing required value, produced by `Validators.required`. */
const REQUIRED_ERROR_KEY = 'required';

/** The shared empty result, so an unchanged derivation keeps a stable identity. */
const NO_MESSAGES: readonly string[] = Object.freeze([]);

// ---------------------------------------------------------------------------
// STATUS CODES
// ---------------------------------------------------------------------------
// Named rather than written as bare numbers at the comparison sites. Only the statuses
// this endpoint can actually produce are declared; see the note on the absence of 429.

/** No response arrived: the network is unavailable, or the request was blocked or aborted. */
const TRANSPORT_FAILURE_STATUS = 0;

/** The request was refused by model-state validation, or by a domain rule. */
const BAD_REQUEST_STATUS = 400;

/** The caller does not administer the resolved tenant. */
const FORBIDDEN_STATUS = 403;

/** The portal already holds a group of that name. */
const CONFLICT_STATUS = 409;

/**
 * The request was well-formed but semantically refused.
 *
 * Handled beside 400 because both can carry a per-field dictionary and the API is free
 * to choose either.
 */
const UNPROCESSABLE_ENTITY_STATUS = 422;

// Deliberately NOT declared: 429. The credential rate limiter is bound to the `auth`
// policy and applies to `/api/v1/auth/*` alone, so this endpoint cannot produce it. A
// branch for it would be unreachable code presenting a refusal that cannot happen.
//
// Deliberately NOT handled: 401. `core/interceptors/auth.interceptor.ts` owns that whole
// lifecycle - detection, the single refresh, the single retry and discarding the session
// when the refresh fails - and this component would otherwise present a session that is
// about to be renewed without the operator noticing as a failure.

// ---------------------------------------------------------------------------
// THE FORM
// ---------------------------------------------------------------------------

/**
 * The shape of the creation form: the group's two editable facts, and nothing else.
 *
 * Declared as an interface and used as the type argument to `FormGroup`, which is what
 * makes `form.value` fully typed rather than a `Partial<...>`. Both controls are
 * constructed non-nullable, so `.value` is `string` rather than `string | null` and
 * `reset()` returns to the empty string rather than to `null`.
 *
 * TWO members, matching the four the legacy assembled at
 * `EditGroups.ascx.vb:L107-L111` less the two the operator never supplied:
 *
 *   * `PortalID` came from the module's own server-side context (`:L108`). The tenant is
 *     now resolved server-side from the request host and the caller's claims, so it is
 *     neither a field nor a payload member.
 *   * `RoleGroupID` was the mode discriminator (`:L109`, seeded -1 at `:L42`). The server
 *     assigns the identifier, and this screen has no edit mode to discriminate.
 *
 * The API's own creation contract carries exactly these two members, so the form, the
 * payload and the endpoint agree without anything being dropped in between.
 */
export interface RoleGroupFormModel {
  /** The group's name. Required, and bounded by {@link ROLE_GROUP_NAME_MAX_LENGTH}. */
  roleGroupName: FormControl<string>;

  /** The group's description. Optional, and bounded by {@link DESCRIPTION_MAX_LENGTH}. */
  description: FormControl<string>;
}

/**
 * Reports a required value that is present but blank once trimmed.
 *
 * MIGRATION: this closes a genuine behavioural gap between the two frameworks rather
 * than adding a rule. `Validators.required` rejects only the empty string, so a value of
 * three spaces passes it; the legacy `RequiredFieldValidator` compares the TRIMMED value
 * against its `InitialValue`, which defaults to the empty string, so three spaces failed
 * it. Without this the migrated screen would accept a name the legacy screen refused.
 *
 * It reports under the framework's own `required` key on purpose. The two rules describe
 * one condition - no name was given - so sharing the key means exactly one message is
 * ever outstanding and the template needs no second branch. `Validators.required` is
 * still applied alongside it, so the framework rule remains the primary statement of
 * intent and this validator reads as the trim it adds.
 *
 * A pure function of the control's value: it inspects nothing else, mutates nothing, and
 * never rewrites the value. Trimming the stored value instead would silently edit what
 * the operator typed, which the legacy did not do either - it posted the raw text.
 *
 * @param control The control to inspect.
 * @returns The `required` error when the value is blank once trimmed, otherwise null.
 */
export const requiredAfterTrim: ValidatorFn = (
  control: AbstractControl,
): ValidationErrors | null => {
  // Widened at the boundary rather than trusted. A validator is reachable from any
  // control, and this one must not throw when handed a value that is not a string.
  const value: unknown = control.value;

  if (typeof value !== 'string') {
    return null;
  }

  return value.trim().length === 0 ? { [REQUIRED_ERROR_KEY]: true } : null;
};

/**
 * Builds the creation form in its initial state.
 *
 * A free function rather than inline construction, so the same shape can be built by a
 * specification without reaching into the component.
 *
 * The validator sets are the legacy's, exactly: the name carries the required rule and
 * its length bound, and the description carries its length bound ALONE. Nothing else is
 * added - no pattern, no minimum length, no required rule on the description - because
 * `EditGroups.ascx` declares nothing else.
 *
 * @returns A form group whose controls both hold the empty string.
 */
export function createRoleGroupForm(): FormGroup<RoleGroupFormModel> {
  return new FormGroup<RoleGroupFormModel>({
    roleGroupName: new FormControl<string>('', {
      nonNullable: true,
      validators: [
        Validators.required,
        requiredAfterTrim,
        Validators.maxLength(ROLE_GROUP_NAME_MAX_LENGTH),
      ],
    }),
    description: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.maxLength(DESCRIPTION_MAX_LENGTH)],
    }),
  });
}

// ---------------------------------------------------------------------------
// PROBLEM-DOCUMENT COMPOSITION
// ---------------------------------------------------------------------------
// Both helpers below GUARANTEE a status on the document they return, and that guarantee
// is what makes the banner paint the right band. The banner derives its severity from the
// status carried in the BODY, and RFC 7807 makes every member optional - a document
// written by a proxy rather than by this API can carry none at all - so a refusal whose
// body omitted its status would otherwise be painted as a fault.

/**
 * Returns the server's own document, with the transport status filled in when it is
 * absent.
 *
 * Everything else is preserved untouched, including `type`, `title`, `detail`, the
 * per-field dictionary and - the member that matters for a support conversation - the
 * trace and correlation identifiers a person is asked to quote.
 *
 * @param document The document the response carried, or null when it carried none.
 * @param status The transport status, which is always present.
 * @returns A document carrying a status.
 */
function withResolvedStatus(document: ProblemDetails | null, status: number): ProblemDetails {
  if (document === null) {
    // Nothing to preserve. A bare status is enough: the banner words an absent `detail`
    // and `title` from the status itself.
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
 * Used for the two outcomes whose wording this migration has to preserve. The server's
 * own `detail` is replaced rather than appended to, because the legacy showed exactly one
 * sentence for each of them; every other member survives, so the trace identifier is
 * still available to quote and the per-field dictionary is still available to render.
 *
 * The transport status is imposed rather than merely defaulted here, because these two
 * branches are SELECTED on it - substituting wording for one status while presenting
 * another would be incoherent.
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
 * Two sources, in a deliberate order. Server-reported messages come first because they
 * describe the submission that was just refused, and the locally-derived required message
 * follows because it describes what is missing now.
 *
 * The required message is gated on the control being TOUCHED, reproducing the legacy
 * validator's `display="Dynamic"` (`EditGroups.ascx:L12`), which rendered nothing until
 * there was something to report. Server messages are NOT gated: a refusal has already
 * happened, and withholding its explanation until the operator happens to focus and blur
 * the field would hide the only account of what went wrong.
 *
 * @param control The control to read.
 * @param requiredMessage The sentence for a missing value, or null when the control has
 * no required rule.
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

  // The shared empty list rather than a fresh one, so a derivation that resolves to
  // nothing keeps a stable identity and the field component is not written to on every
  // change-detection pass.
  return messages.length === 0 ? NO_MESSAGES : messages;
}

/**
 * Creates a role group.
 *
 * Reached at `role-groups/new` and nowhere else. The class name and the file path are
 * both load-bearing: the route table imports this module dynamically and reads this
 * export by name, so renaming either resolves to `undefined` at run time and produces a
 * blank screen with NO compile error to warn anybody.
 *
 * ⚠ DECLARES NO INPUTS, and that is the contract rather than an omission. The address has
 * no identifier segment, so there is nothing for `withComponentInputBinding()` to bind;
 * an input named for the group's identifier would never receive a value and would
 * misinform the next reader into thinking an edit mode exists.
 *
 * It attaches no guard and re-implements none. The route table owns that, and a route
 * guard is a navigation affordance in any case - the endpoint this screen calls is
 * policy-protected server-side, which is why the 403 branch below is a real branch rather
 * than a theoretical one.
 *
 * MIGRATION: view state is gone entirely, with nothing to port. The legacy control round
 * tripped its state through the postback lifecycle; searching this screen's directory for
 * view-state or session access finds none, because this control kept its state in the
 * rendered controls themselves. State here is held in signals and in the form.
 *
 * MIGRATION: the legacy navigation asymmetry is resolved rather than reproduced. A
 * successful creation went to the roles list (`EditGroups.ascx.vb:L120`) while a
 * successful update came back to this same screen with the identifier appended
 * (`:L121-L124`), so one screen had two different notions of "done". Being create-only,
 * this component has one: create, then go to the roles list.
 *
 * MIGRATION: a latent defect is recorded here and eliminated by construction rather than
 * fixed in place. The legacy passed the group's identifier between screens as a query
 * string, and the two ends disagreed about its spelling: `Roles.ascx.vb:L84` WRITES
 * `EditUrl("RoleGroupId", ...)` with a lower-case `d`, while `EditGroups.ascx.vb:L61-L62`
 * READS `Request.QueryString("RoleGroupID")` with a capital one - and `Roles.ascx.vb:L253`
 * reads the capital form too, so that one file writes one spelling and reads the other.
 * It worked only because query-string lookup in that framework is case-insensitive; any
 * case-sensitive consumer would have found nothing. The same disagreement runs through the
 * private fields, declared `RoleGroupID` at `EditGroups.ascx.vb:L42` and `RoleGroupId` at
 * `Roles.ascx.vb:L48`. This component reads no query string and no route parameter at
 * all, so the class of defect cannot recur; it is reported rather than repaired, because
 * repairing the legacy is not this migration's business.
 *
 * MIGRATION: outcomes arrive as an HTTP status and a failure code instead of through a
 * `ByRef` status argument, the idiom the legacy used at 30 sites across the migrated
 * domains. Nothing in this file is mutated by a callee to report what happened.
 *
 * MIGRATION: the description is a plain multi-line text area. The legacy rendered it with
 * `textmode="MultiLine"`, so it was never rich text on this screen, and the rich-text
 * provider is out of scope for the migration in any case.
 */
@Component({
  selector: 'app-role-group-form',
  standalone: true,
  // The reactive-forms directives for the typed form, and four shared components: the
  // heading, the two labelled fields, the in-page failure surface and the in-flight
  // affordance. Nothing else is imported. There is no confirmation dialogue here, because
  // this screen has no destructive action to confirm - see the note on the class above.
  imports: [
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
  // -------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------

  /**
   * The only route to the API from this screen.
   *
   * Injected rather than reached through the store, because the store's own creation
   * action returns nothing: it holds the outcome in its own state and gives a caller no
   * point at which to navigate. This screen has to navigate on success, so it subscribes
   * to the call itself.
   *
   * No URL is composed here and the HTTP client is not injected: the service owns every
   * endpoint template, and a path assembled in a component would be a second, divergent
   * statement of the same address.
   */
  private readonly roles = inject(RoleService);

  /**
   * The shared role state, refreshed after a successful creation.
   *
   * The refresh is the reason this is injected. `EditGroups.ascx.vb:L120` returns the
   * operator to the roles list, where the group they just created is immediately
   * selectable in the group filter; re-reading the listing here is what makes that true
   * of the destination screen, whatever it happens to hold already.
   */
  private readonly store = inject(RoleStore);

  /** Navigation for both actions. */
  private readonly router = inject(Router);

  /** The queue a successful creation is announced through. */
  private readonly notifications = inject(NotificationService);

  /**
   * Ties the in-flight request to this component's lifetime.
   *
   * Captured as a field because `inject` requires an injection context, which a method
   * called from the template does not have.
   */
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The creation form.
   *
   * Public because the template binds it and a specification fills it; those are the two
   * consumers, and reaching a protected member from a specification would mean subverting
   * the access modifier at every assertion.
   */
  readonly form: FormGroup<RoleGroupFormModel> = createRoleGroupForm();

  // -------------------------------------------------------------------------
  // VIEW STATE
  // -------------------------------------------------------------------------

  /** Whether a creation is in flight. Written only by {@link submit}. */
  private readonly _submitting = signal(false);

  /** The failure to present, or null when there is none. */
  private readonly _problem = signal<ProblemDetails | null>(null);

  /**
   * Bumped whenever either control's validity or touched state changes.
   *
   * The bridge that makes the two message derivations below genuinely reactive. A form
   * control is not a signal, so a `computed()` reading `control.errors` directly would
   * memoise its first answer and never recompute - and under `OnPush` the field would then
   * show the first message it ever resolved and silently ignore every later one. Reading
   * this counter inside those derivations establishes the dependency, so they recompute
   * exactly when the controls change and at no other time; between changes they return a
   * memoised array, which keeps the field component from being written to on every
   * change-detection pass.
   */
  private readonly controlRevision = signal(0);

  /** Whether a creation is in flight. */
  readonly submitting: Signal<boolean> = this._submitting.asReadonly();

  /**
   * The failure to present, or null when there is none.
   *
   * Bound to the shared error banner, which carries the live region that announces it and
   * derives its own severity from the status this document carries.
   */
  readonly problem: Signal<ProblemDetails | null> = this._problem.asReadonly();

  /**
   * Messages for the name field.
   *
   * Carries the required sentence, gated on the control having been touched, and any
   * message the server reported against the field.
   */
  readonly roleGroupNameError: Signal<readonly string[]> = computed(() => {
    this.controlRevision();

    return controlMessages(this.form.controls.roleGroupName, ROLE_GROUP_NAME_REQUIRED_MESSAGE);
  });

  /**
   * Messages for the description field.
   *
   * Server-reported messages ONLY, and the null argument below is the whole point: the
   * legacy declared no validator on this field, so there is no local sentence to show and
   * none is invented. Its length bound is enforced by the control and prevented by the
   * input's `maxlength`, neither of which the legacy reported in words.
   */
  readonly descriptionError: Signal<readonly string[]> = computed(() => {
    this.controlRevision();

    return controlMessages(this.form.controls.description, null);
  });

  // -------------------------------------------------------------------------
  // WORDING AND LIMITS, FOR THE TEMPLATE
  // -------------------------------------------------------------------------
  // Bound rather than repeated as literals in the template, so the rendered screen and
  // the specification read the same constants.

  /** @see ROLE_GROUP_FORM_TITLE */
  readonly pageTitle = ROLE_GROUP_FORM_TITLE;

  /** @see ROLE_GROUP_NAME_LABEL */
  readonly roleGroupNameLabel = ROLE_GROUP_NAME_LABEL;

  /** @see ROLE_GROUP_NAME_HELP */
  readonly roleGroupNameHelp = ROLE_GROUP_NAME_HELP;

  /** @see ROLE_GROUP_NAME_MAX_LENGTH */
  readonly roleGroupNameMaxLength = ROLE_GROUP_NAME_MAX_LENGTH;

  /** @see ROLE_GROUP_NAME_CONTROL */
  readonly roleGroupNameControl = ROLE_GROUP_NAME_CONTROL;

  /** @see ROLE_GROUP_NAME_INPUT_ID */
  readonly roleGroupNameInputId = ROLE_GROUP_NAME_INPUT_ID;

  /** @see DESCRIPTION_LABEL */
  readonly descriptionLabel = DESCRIPTION_LABEL;

  /** @see DESCRIPTION_HELP */
  readonly descriptionHelp = DESCRIPTION_HELP;

  /** @see DESCRIPTION_MAX_LENGTH */
  readonly descriptionMaxLength = DESCRIPTION_MAX_LENGTH;

  /** @see DESCRIPTION_CONTROL */
  readonly descriptionControl = DESCRIPTION_CONTROL;

  /** @see DESCRIPTION_INPUT_ID */
  readonly descriptionInputId = DESCRIPTION_INPUT_ID;

  /** @see UPDATE_ACTION_LABEL */
  readonly updateActionLabel = UPDATE_ACTION_LABEL;

  /** @see CANCEL_ACTION_LABEL */
  readonly cancelActionLabel = CANCEL_ACTION_LABEL;

  constructor() {
    // Subscribed per control rather than to the group, so the bridge depends on nothing
    // about how events propagate up a control tree: these two controls are exactly what
    // the derivations read. Both `markAllAsTouched` and a validity change on a child emit
    // through the child's own stream, so both reach this.
    for (const control of [this.form.controls.roleGroupName, this.form.controls.description]) {
      control.events.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
        this.controlRevision.update((revision) => revision + 1);
      });
    }
  }

  // -------------------------------------------------------------------------
  // ACTIONS
  // -------------------------------------------------------------------------

  /**
   * Creates the group, then returns to the roles list.
   *
   * The `cmdUpdate` equivalent, and it VALIDATES: `EditGroups.ascx:L23` declares that link
   * button with no `causesvalidation` attribute, so it took the framework default of true,
   * and `EditGroups.ascx.vb:L106` guards the whole handler behind `If Page.IsValid`.
   *
   * An invalid form marks every control touched and stops. Marking touched is what
   * reproduces `display="Dynamic"`: the legacy validator rendered nothing until the page
   * was submitted, and then rendered beside the field. Nothing is sent, so an invalid form
   * costs no request.
   */
  submit(): void {
    // An in-flight creation is not repeated. The legacy could not be double-submitted the
    // same way - the postback replaced the whole page - and this is a create, so a
    // duplicated request would attempt a duplicated row. The template also disables the
    // action while a request is in flight; this guard is what makes the invariant hold
    // regardless of what the template does.
    if (this._submitting()) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();

      return;
    }

    this._submitting.set(true);
    this._problem.set(null);

    // Exactly the two members the API's creation contract declares. The tenant is NOT
    // sent: the server resolves it from the request host and the caller's claims, which is
    // where the legacy read it from too - `EditGroups.ascx.vb:L108` took it from the
    // module's server-side context, not from anything the operator supplied. The
    // identifier is not sent either; the server assigns it.
    //
    // The values are forwarded exactly as typed. An empty description travels as the EMPTY
    // STRING and is neither coalesced to null nor omitted: the API serialises with its
    // null-omission condition set to never, so a member is always present, and the legacy
    // null contract made the empty string - not nothing - the absent-string marker. Turning
    // `''` into `null` here would change what is stored.
    const request: CreateRoleGroupRequest = {
      roleGroupName: this.form.controls.roleGroupName.value,
      description: this.form.controls.description.value,
    };

    this.roles
      .createRoleGroup(request)
      .pipe(
        // Clears the in-flight state on every outcome, including an unsubscribe, so the
        // action cannot be left permanently disabled by a path nobody anticipated.
        finalize(() => {
          this._submitting.set(false);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        // The created group is deliberately not read. This screen navigates away, so it has
        // nothing to display it in, and the destination re-reads the listing below.
        next: () => {
          this.onCreated();
        },
        // Widened rather than typed as a response. A failure reaching a subscriber is not
        // necessarily one: an interceptor or an operator can throw any value at all, and
        // reading a status off such a value would yield `undefined` and match no branch.
        error: (error: unknown) => {
          this.present(error);
        },
      });
  }

  /**
   * Abandons the form and returns to the roles list.
   *
   * The `cmdCancel` equivalent, and it deliberately does NOT validate:
   * `EditGroups.ascx:L25` declares that link button with `causesvalidation="False"`. So
   * nothing is marked touched, no message appears, and an incomplete form is abandoned
   * rather than argued with - Cancel navigates even when the name is missing.
   *
   * There is no confirmation prompt, because the legacy attached none to this action. The
   * only confirmation on the screen was bound to the delete button
   * (`EditGroups.ascx.vb:L66`), which creation hid and this component does not render.
   *
   * The destination is the roles list, from `:L165-L166`, which is the create-mode branch
   * of the legacy handler.
   */
  cancel(): void {
    this.goToRoles();
  }

  // -------------------------------------------------------------------------
  // OUTCOMES
  // -------------------------------------------------------------------------

  /**
   * Announces the creation and leaves for the roles list.
   *
   * The re-read is what makes the new group usable at the destination: the roles list
   * filters by group, and `EditGroups.ascx.vb:L120` returned the operator there precisely
   * so the group they had just created was available to select.
   */
  private onCreated(): void {
    this.store.loadRoleGroups();
    this.notifications.notify('success', ROLE_GROUP_CREATED_MESSAGE);
    this.goToRoles();
  }

  /**
   * Presents a failure in the page, beside the form, and keeps the operator on it.
   *
   * Staying put is behaviour rather than a default: `EditGroups.ascx.vb:L118` is an
   * `Exit Sub` immediately after presenting the duplicate-name message, so the legacy left
   * the operator on the screen with what they had typed intact.
   *
   * Nothing queues a notification here. `core/interceptors/error.interceptor.ts` has
   * already queued exactly one for this response, so a second would say the same thing
   * twice - see the note at the head of this file, which also explains why an in-page
   * block is the faithful reproduction of `AddModuleMessage` in the first place.
   *
   * @param error Whatever the subscriber was handed.
   */
  private present(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      // No status and no document to work with. Presented rather than swallowed, because
      // the legacy's outermost handler made every failure visible.
      this._problem.set({ detail: UNEXPECTED_FAILURE_MESSAGE });

      return;
    }

    const status: number = error.status;

    // Resolved BEFORE the body is read, and the ordering is load-bearing rather than tidy.
    // The framework puts a DOM progress event in the body slot for this condition, and
    // that value satisfies the deliberately permissive problem-document predicate, so
    // reading the body first would mistake a transport failure for a document that says
    // nothing.
    if (status === TRANSPORT_FAILURE_STATUS) {
      this._problem.set({ status, detail: NETWORK_UNAVAILABLE_MESSAGE });

      return;
    }

    // Widened at the boundary. The framework types this member loosely, so every read of
    // it has to be guarded, and the predicate is the guard.
    const body: unknown = error.error;
    const document: ProblemDetails | null = isProblemDetails(body) ? body : null;

    if (status === CONFLICT_STATUS) {
      // The one conflict this endpoint can report: the portal already holds a group of
      // that name. The legacy sentence is substituted for the server's own wording, and
      // the 409 status carries it into the banner's danger band, which is what
      // `ModuleMessageType.RedError` was.
      this._problem.set(withLegacyWording(document, status, DUPLICATE_ROLE_GROUP_MESSAGE));

      return;
    }

    if (status === FORBIDDEN_STATUS) {
      // A refusal, not a fault. The 403 status carries the legacy sentence into the
      // banner's WARNING band, matching the `YellowWarning` that
      // `AccessDenied.ascx.vb` uses in both of its branches.
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
   * The key matching is not this component's invention and is not restated here:
   * `problemDetailsFieldErrors` lower-cases the first character of every key it returns,
   * so the API's Pascal-cased model-state keys arrive in the casing a form control is
   * named in. Only the first character is changed, so a compound name survives intact.
   *
   * A key that matches no control - the empty key a form-level model-state failure uses,
   * or a key for a member this form does not carry - is deliberately not forced onto a
   * field. Those reach the operator through the banner instead, which renders them.
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
   * errors outright would discard a locally-derived one that is still true - a name can be
   * both missing and reported on. The messages clear themselves the moment the operator
   * edits the field, because re-validating replaces the error object, which is the
   * behaviour wanted: a stale refusal must not outlive the value it was about.
   *
   * The control is marked touched so the messages are displayed immediately, without
   * waiting for a focus and blur the operator has no reason to perform.
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
   * The rejection is absorbed rather than left unhandled, matching the one existing
   * navigation in this workspace. A navigation settles to false when a guard refuses it,
   * and rejects only when a guard or resolver throws; neither is something this screen can
   * recover from, and an unhandled rejection would surface as noise in the console instead
   * of anywhere an operator could act on it.
   */
  private goToRoles(): void {
    void this.router.navigate([ROLES_PATH]).catch(() => false);
  }
}
