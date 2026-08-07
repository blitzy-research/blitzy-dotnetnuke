import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import type { Signal, WritableSignal } from '@angular/core';
import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import { CREDENTIAL_MAX_LENGTH } from '../../../core/utils/credential-bounds.util';
import { conflictMessage, fieldErrorMessage, summarizeProblem } from '../../../core/utils/form-errors.util';
import { parseRouteId } from '../../../core/utils/route-id.util';
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

// =============================================================================
//  PORTAL TYPE
// =============================================================================

/**
 * The two portal kinds the legacy signup screen offered.
 *
 * The codes are the legacy list-item values verbatim — `signup.ascx:L34-L35`
 * declares `<asp:listitem value="P">Parent</asp:listitem>` and
 * `<asp:listitem value="C">Child</asp:listitem>` — and they are load-bearing rather
 * than incidental, because every branch in the legacy handler tests them literally:
 * `Signup.ascx.vb:L199` reads `blnChild = (optType.SelectedValue = "C")` and L344
 * repeats the same comparison. Renaming them to a friendlier vocabulary would break
 * the correspondence a reviewer needs to check the two branches against each other.
 *
 * The single place either code crosses the wire is
 * {@link CreatePortalRequest.isChildPortal}, which is a boolean: the request contract
 * carries the DECISION, not the code.
 */
export type PortalType = 'P' | 'C';

/** Parent portal: reached through a host name of its own. The legacy default. */
const PARENT_PORTAL_TYPE: PortalType = 'P';

/** Child portal: reached through a path beneath an existing host name. */
const CHILD_PORTAL_TYPE: PortalType = 'C';

// =============================================================================
//  MEASURED VALIDATION LIMITS
// =============================================================================
//
// ONE PRINCIPLE governs every number below, and it resolves the two directions in
// which the legacy screen and the API disagree:
//
//   the form NEVER accepts what the API rejects, and where the API is the more
//   permissive of the two the legacy screen's own limit is preserved.
//
// Applied consistently that principle produces both of the divergences recorded
// here, so neither is an ad-hoc choice:
//
//   * an administrator's given or family name is capped at the API's 50 rather than
//     the markup's 100, because a 51-character name would be accepted by the form
//     and refused by the API — and would have failed in the legacy system too, whose
//     terminal column is nvarchar(50);
//   * an administrator's mail address is capped at the legacy screen's 100 even
//     though the column and the API both permit 256, because rejecting nothing the
//     legacy screen accepted is exactly what functional parity means.

/**
 * Portal alias, 128 characters.
 *
 * `signup.ascx:L40` declares `maxlength="128"` on `txtPortalName`, which — see the
 * semantic-inversion note on {@link PortalFormComponent} — is the ALIAS box, and the
 * API's create rule caps the same field at 128.
 */
const ALIAS_MAX_LENGTH = 128;

/**
 * Portal title, 128 characters.
 *
 * `signup.ascx:L52` declares `maxlength="128"` on `txtTitle`; the update contract
 * documents `portalName` as "at most 128 characters".
 */
const TITLE_MAX_LENGTH = 128;

/**
 * Description and keywords, 500 characters each.
 *
 * `signup.ascx:L56` and `L61` both declare `maxlength="500"`, and both create and
 * update rules cap the pair at 500.
 */
const METADATA_MAX_LENGTH = 500;

/**
 * Administrator given and family name, 50 characters.
 *
 * MIGRATION: 50, NOT the markup's 100. `signup.ascx:L79` and `L84` declare
 * `maxlength="100"`, but the terminal column is `nvarchar(50)`
 * (`01.00.06.SqlDataProvider:L185-L186`, and no `ALTER COLUMN` in any of the
 * eighty-eight upgrade scripts widens either one), so the markup's figure admits a
 * value the database cannot store. The API's create rule caps both at 50 for that
 * reason, and a form that accepted 100 would produce a submission the API refuses
 * with a field-level message the operator can do nothing about except retype. The
 * markup's figure is recorded here so the divergence stays checkable.
 */
const PERSON_NAME_MAX_LENGTH = 50;

/**
 * Administrator sign-in name, 100 characters.
 *
 * `signup.ascx:L89` declares `maxlength="100"` and the terminal column is
 * `Username nvarchar(100) NOT NULL` (`01.00.06.SqlDataProvider:L197`). Markup,
 * schema and API rule all agree, so no divergence arises.
 */
const USERNAME_MAX_LENGTH = 100;

/**
 * Administrator password and its confirmation.
 *
 * MIGRATION: THE LEGACY FIGURE OF 20 IS DELIBERATELY NOT PRESERVED. `signup.ascx:L94`
 * and `L100` declare `maxlength="20"`, but that figure mirrored the legacy STORAGE
 * width `Users.Password nvarchar(20)` rather than any rule the legacy applied to a
 * credential. The measured legacy password policy is the three settings in
 * `Website/release.config` — `minRequiredPasswordLength="7"`,
 * `minRequiredNonalphanumericCharacters="0"` and `requiresUniqueEmail="false"` — and
 * it declares NO MAXIMUM. The successor stores a one-way BCrypt hash, so the column
 * that produced the 20 no longer exists.
 *
 * Reproducing it would therefore preserve an artefact of a deleted constraint, not a
 * behaviour, while capping the entropy of every administrator credential this screen
 * creates at twenty characters. The ceiling is instead the API's own bound, shared
 * from `core/utils/credential-bounds.util.ts`, which documents why a limit counted in
 * UTF-16 code units can never refuse a credential the API's 256-BYTE rule accepts.
 */
const PASSWORD_MAX_LENGTH = CREDENTIAL_MAX_LENGTH;



/**
 * The shortest administrator password the API accepts.
 *
 * The MEASURED LEGACY POLICY and not a tightening of it: `Website/release.config:L241` declares
 * `minRequiredPasswordLength="7"`, the API's creation rule binds that same policy value, and the
 * legacy screen enforced it only by letting the server refuse. Stating it here spends no request to
 * learn what is already knowable, and it is deliberately NOT raised — a stronger requirement would
 * refuse credentials the legacy application accepted, which the migration discipline forbids.
 *
 * The complementary non-alphanumeric requirement is NOT expressed, because the measured policy sets it
 * to zero (`Website/release.config:L243`) and a rule that can never fail is a rule that can only
 * mislead. The API expresses it for the case where a deployment configures it; the client would then
 * simply let that refusal arrive, exactly as the legacy screen did for the length.
 */
const PASSWORD_MIN_LENGTH = 7;

/**
 * The pattern an administrator mail address must match.
 *
 * MIGRATION: THE AUTHORITY IS THE DOMAIN ATTRIBUTE, NOT THIS SCREEN'S MARKUP, because the markup has
 * nothing to say: `signup.ascx:L106` declares a `requiredfieldvalidator` on the address and NO
 * regular-expression validator at all, so the legacy screen accepted any non-empty text and let the
 * write refuse it. The rule that refused it is the one attached to the property being written —
 * `UserInfo.vb:L122` carries `RegularExpressionValidator(glbEmailRegEx)`, whose expression is declared
 * once at `Library/Components/Shared/Globals.vb:L132` as
 * `\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b`.
 *
 * Three deliberate differences from that measured string, each of which would otherwise be a defect:
 *
 * - ANCHORED with `^` and `$` in place of the word boundaries. Angular anchors a pattern supplied as a
 *   string but leaves a `RegExp` exactly as written, and `\b` matches a position rather than the ends
 *   of the value — so the expression as measured would accept any text merely CONTAINING an address.
 * - the final label's upper bound is 63 rather than 4. The legacy `{2,4}` predates every long
 *   top-level domain, so reproducing it would refuse addresses the API accepts — its own value object
 *   preserves the lower half and replaces the upper half in exactly this way, and a client stricter
 *   than the server produces a refusal the operator has no way to work around.
 * - the redundant escapes on `-` inside the character classes are dropped, which does not change the
 *   language matched: a hyphen at the end of a class is already literal.
 *
 * Anything narrower is not attempted. The API's value object applies further rules — a leading
 * character class, per-label lengths, a letters-only final label — and reproducing them here would put
 * the same rule in two places with two chances of drifting. The client refuses what is obviously
 * malformed; the server remains the authority.
 */
const EMAIL_PATTERN = /^[a-zA-Z0-9._%+'-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,63}$/;

/**
 * Administrator mail address, 100 characters.
 *
 * MIGRATION: PRESERVED AT THE LEGACY SCREEN'S FIGURE, WHICH IS THE STRICTER OF THE
 * TWO. `signup.ascx:L106` declares `maxlength="100"`. The terminal column is
 * `Email nvarchar(256) NULL` — created at `nvarchar(100) NOT NULL`
 * (`01.00.00.SqlDataProvider:L107`), dropped at `02.02.01.SqlDataProvider:L50-L51`
 * and re-added wider at `03.00.13.SqlDataProvider:L109-L110` — and the API's create
 * rule caps the field at 256 accordingly. Retaining 100 is therefore a functional
 * reduction against the API and exact parity against the screen being replaced; the
 * parity reading governs, because the legacy operator could not enter a longer
 * address either. The API's figure is recorded here so a later decision to widen it
 * needs no re-measurement.
 */
const EMAIL_MAX_LENGTH = 100;

// =============================================================================
//  MEASURED ALIAS CHARACTER SETS
// =============================================================================

/**
 * The characters a CHILD portal's alias may contain.
 *
 * Copied verbatim from `Signup.ascx.vb:L207` (and repeated at L192 in the branch
 * that has no client counterpart). Lower case only, which is precisely what makes
 * the lower-casing at L183 lossless rather than destructive.
 */
const CHILD_ALIAS_CHARACTERS = 'abcdefghijklmnopqrstuvwxyz0123456789-';

/**
 * The characters a PARENT portal's alias may contain.
 *
 * `Signup.ascx.vb:L208-L210` widens the child set with three punctuation characters
 * when the portal is NOT a child, which is what lets a parent alias carry a host
 * name, a port and a path segment.
 *
 * NOTE THE POLARITY: the PARENT case is the PERMISSIVE one. Reading it the other way
 * round rejects every legitimate parent alias — `example.com:8080/site` contains
 * three characters the child set does not hold.
 */
const PARENT_ALIAS_CHARACTERS = `${CHILD_ALIAS_CHARACTERS}./:`;

/**
 * The scheme prefix the legacy screen stripped before inspecting alias characters.
 *
 * `Signup.ascx.vb:L184` reads `Replace(txtPortalName.Text, "http://", "")`.
 */
const LEGACY_SCHEME_PREFIX = 'http://';

/** The alias separator whose LAST occurrence bounds a child portal's own segment. */
const ALIAS_SEGMENT_SEPARATOR = '/';

// =============================================================================
//  MEASURED WORDING
// =============================================================================
//
// MIGRATION: LOCALISATION IS NOT PORTED. The legacy screen resolved every string
// through `Website/admin/Portal/App_LocalResources/Signup.ascx.resx` and
// `Website/App_GlobalResources/SharedResources.resx` under the key convention
// `<ControlID>.<Property>`; that mechanism is Web Forms specific, no translation
// runtime is introduced, and none of the legacy localisation calls is reproduced.
// The resource files are read as the AUTHORITY FOR WORDING only, and each constant
// below names the entry it reproduces so the parity claim stays checkable.

/** `valPortalName.ErrorMessage` — `signup.ascx:L41` and the resx entry of the same name. */
const ALIAS_REQUIRED_MESSAGE = 'Portal Name Is Required.';

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
 * The wording for a credential shorter than the configured minimum.
 *
 * The API'S OWN SENTENCE, reproduced with the configured length substituted into it, because the API
 * composes it from the same legacy template — `InvalidPassword.Text` from the shared resource file,
 * whose two bracketed tokens it fills from the bound policy. A person who trips the rule before the
 * request leaves therefore reads exactly what they would have read had it left.
 *
 * The non-alphanumeric half of the sentence is kept even though the measured policy sets that
 * requirement to zero: the sentence is the legacy sentence, the API says the same, and rewording it
 * here would put two descriptions of one rule in front of the same operator.
 */
const PASSWORD_TOO_SHORT_MESSAGE =
  'The password specified is invalid.  Please specify a valid password.  Passwords must be at ' +
  `least ${String(PASSWORD_MIN_LENGTH)} characters in length and contain at least 0 ` +
  'non-alphanumeric characters.';

/**
 * The wording for a malformed mail address.
 *
 * `EmailValidation.Text` from the shared resource file, which is the sentence the API reports for the
 * same rule. Taken verbatim, including its double space, so client and server describe one rule with
 * one sentence.
 */
const EMAIL_INVALID_MESSAGE =
  'The email address specified is invalid.  Please specify a valid email address.';

/**
 * `InvalidName.Text` — `Signup.ascx.resx:L234-L236`, verbatim.
 *
 * The wording says "Portal Name" while the field it guards is the ALIAS. That is the
 * same inversion recorded on {@link PortalFormComponent}, it is what the operator
 * read, and the API reproduces the identical sentence, so it is preserved unaltered
 * rather than corrected.
 */
const INVALID_ALIAS_MESSAGE = 'The Portal Name Must Not Contain Spaces Or Punctuation.';

/** `InvalidPassword.Text` — `Signup.ascx.resx:L237-L239`, verbatim. */
const PASSWORD_MISMATCH_MESSAGE = 'The Password Values Entered Do Not Match.';

/**
 * `CreateError.Text` — `Signup.ascx.resx:L243-L245`, verbatim.
 *
 * MIGRATION: THIS ENTRY IS ORPHANED IN THE LEGACY SCREEN AND IS PUT BACK TO WORK
 * HERE. `Signup.ascx.vb:L275-L278` catches the provisioning exception and assigns
 * `strMessage = ex.Message`, then L323 renders that raw text into `lblMessage` — so
 * the resource was authored, shipped and never reached, while internal exception
 * text was shown to the operator instead. Using it is simultaneously a divergence
 * from the legacy behaviour and the removal of an information leak, and it is only a
 * FALLBACK: the API fills the problem document's `detail` unconditionally, so this
 * sentence appears only when a response carries no text of its own — a proxy or
 * gateway answering on its own behalf.
 */
const CREATE_ERROR_MESSAGE =
  'An Error Was Encountered During The Creation Of Your Portal. This May Have Been ' +
  'Caused By Specifying An Incorrect Password For An Existing User Account. Please ' +
  'Verify Your Details Before You Try Again.';

/**
 * The refusal wording for a change to a host-administered term.
 *
 * MIGRATION: AUTHORED HERE, BECAUSE THE LEGACY GUARD CARRIES NO WORDING AT ALL.
 * `Website/admin/Portal/SiteSettings.ascx.vb:L759-L770` compares the submitted
 * hosting fee, disk-space allowance, page quota, account quota, activity-history
 * retention and expiry date against the stored portal and, for a caller who is not a
 * host operator, ends with a bare `Throw New System.Exception` — no message, no
 * resource key, nothing to reproduce. The API enforces the same rule and answers
 * `403`, so this sentence is net-new and is written to say exactly what happened.
 *
 * Two things it deliberately does NOT say: that a session expired, and anything that
 * invites a sign-in. A refusal is the system working as configured.
 */
const HOST_FIELD_REFUSED_MESSAGE =
  'Only a host account may change this portal’s host-administered terms, so the save ' +
  'was refused. Nothing was changed.';

/** The wording shown when the addressed portal is not on the server. */
const PORTAL_NOT_FOUND_MESSAGE = 'That portal no longer exists, so nothing could be loaded.';

/**
 * Confirmation shown after a successful write.
 *
 * MIGRATION: NET-NEW. The legacy screen reported success by NAVIGATING — L316 reads
 * `Response.Redirect(webUrl, True)` — and emitted no confirmation of any kind, so
 * there is no legacy wording to reproduce. The legacy severity vocabulary is
 * three-valued and its success band is `GreenSuccess`, which the notification
 * service spells `success`.
 */
const CREATE_SUCCEEDED_MESSAGE = 'The portal was created.';

/** Confirmation shown after a successful update. See {@link CREATE_SUCCEEDED_MESSAGE}. */
const UPDATE_SUCCEEDED_MESSAGE = 'The portal was updated.';

/**
 * The wording for a rule whose own message this screen cannot name.
 *
 * Reachable only from a rule added after this file was written, or from a length rule
 * whose reported bound could not be read. Matched to the wording the sibling settings
 * screen already uses for the same situation, so one condition is not described two ways
 * in one feature.
 */
const GENERIC_FIELD_MESSAGE = 'Correct this field and try again.';

/** `AddPortal.Text` — `Signup.ascx.resx:L285-L287`. Set as the module title at `Page_Init` L47. */
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

/**
 * The template name submitted with every creation.
 *
 * MIGRATION: THE TEMPLATE SELECTOR IS DROPPED, THE CONTRACT MEMBER IS NOT.
 * `signup.ascx:L66-L69` declared a drop-down list `cboTemplate` with
 * `AutoPostBack="True"`, a required-field validator `valTemplate` carrying
 * `InitialValue="-1"`, and a description label driven by
 * `cboTemplate_SelectedIndexChanged` (`Signup.ascx.vb:L370-L393`) which loaded the
 * chosen file and read `//portal/description` out of it. `Page_Load` L76-L97
 * populated the list by ENUMERATING THE FILE SYSTEM for `*.template`. None of that
 * has a counterpart: the endpoint catalogue publishes no portal-template route, and
 * the filesystem subsystem is out of scope, so there is nothing to enumerate and
 * nothing to select from.
 *
 * The member is nonetheless REQUIRED by the API's create rule, which preserves
 * `valTemplate`'s requiredness as a non-empty-string test and additionally insists
 * the value name a file rather than a path — while the service that receives it
 * performs no template parsing at all and records the value only. Sending `null`
 * would therefore be refused with a field-level message about a control this screen
 * does not render, which is the worst of the available outcomes.
 *
 * The value is the stock template name of the generation being migrated, and it is
 * the spelling this workspace already uses for the same field in
 * `core/services/portal.service.spec.ts`. The legacy `"-1"` placeholder is
 * deliberately NOT carried across: it stood for "no list item selected", and that
 * negative one is the same value the legacy null contract uses as its absent-integer
 * sentinel AND the seed of the `Portals` primary key, so reproducing it anywhere in
 * this file would be a defect rather than fidelity.
 */
const DEFAULT_TEMPLATE_FILE = 'Default Website.template';

// =============================================================================
//  FORM SHAPES
// =============================================================================

/**
 * The creation form: eleven controls, one per field the legacy signup screen showed
 * that still has somewhere to go.
 *
 * MIGRATION ANNOTATION 1 — THE SEMANTIC INVERSION, AND WHY NO CONTROL IS NAMED AFTER
 * A LEGACY CONTROL ID. The legacy `txtPortalName` box collected the ALIAS and the
 * legacy `txtTitle` box collected the NAME. Four independent measurements agree:
 *
 *   1. `Library/Components/Portal/PortalController.vb:L980` declares `CreatePortal`
 *      with `PortalName` as its FIRST parameter;
 *   2. `Website/admin/Portal/Signup.ascx.vb:L274` passes `txtTitle.Text` into that
 *      first position and `strPortalAlias` — derived from `txtPortalName` — into the
 *      `PortalAlias` position;
 *   3. `Signup.ascx.resx` labels `txtPortalName` through `plPortalAlias.Text` =
 *      "Portal Alias:" (`signup.ascx:L39` binds the two) and labels `txtTitle`
 *      through `plTitle.Text` = "Title:";
 *   4. corroborating from the other screen, `SiteSettings.ascx.resx` labels
 *      `txtPortalName` as `plPortalName.Text` = "Title:", and
 *      `SiteSettings.ascx.vb:L772` passes `txtPortalName.Text` into
 *      `UpdatePortalInfo`'s second position, which L1568 declares as `PortalName`.
 *
 * So the controls below are named for WHAT THEY HOLD — `alias` and `title` — and the
 * mapping onto the request contract's `portalAlias` and `portalName` is written once,
 * explicitly, in {@link PortalFormComponent}. A control named `portalName` for the
 * alias box would compile, submit and put the alias in the title column.
 *
 * A related trap sits in the resource file and is deliberately unused:
 * `Signup.ascx.resx` also carries `plPortalName.Text` = "Portal Name:", but
 * `signup.ascx` declares no `plPortalName` control at all, so that entry is orphaned
 * and reproducing it would label a field the legacy screen never labelled that way.
 */
export interface PortalCreateFormModel {
  /** Parent or child. Defaults to parent, per `Signup.ascx.vb:L101`. */
  readonly portalType: FormControl<PortalType>;

  /** The first host name the portal answers on. Legacy `txtPortalName`. */
  readonly alias: FormControl<string>;

  /** The portal's title. Legacy `txtTitle`. Optional, per the measured validator set. */
  readonly title: FormControl<string>;

  /** Free-text description offered to search engines. Legacy `txtDescription`. */
  readonly description: FormControl<string>;

  /** Comma-separated search keywords. Legacy `txtKeyWords`. */
  readonly keywords: FormControl<string>;

  /** First administrator's given name. Legacy `txtFirstName`. */
  readonly firstName: FormControl<string>;

  /** First administrator's family name. Legacy `txtLastName`. */
  readonly lastName: FormControl<string>;

  /** First administrator's sign-in name. Legacy `txtUsername`. */
  readonly username: FormControl<string>;

  /** First administrator's password. Legacy `txtPassword`. */
  readonly password: FormControl<string>;

  /**
   * Confirmation of the password. Legacy `txtConfirm`.
   *
   * CLIENT-ONLY, and provably so from both ends: `CreatePortal`'s fifteen positional
   * parameters (`PortalController.vb:L980`) include no confirmation argument, and
   * neither does {@link CreatePortalRequest}. It exists to be compared, never sent.
   */
  readonly confirm: FormControl<string>;

  /** First administrator's mail address. Legacy `txtEmail`. */
  readonly email: FormControl<string>;
}

/**
 * The edit form: three controls, and the count is the point.
 *
 * EDIT MODE RENDERS THE INTERSECTION OF WHAT THIS SCREEN OWNS AND WHAT THE UPDATE
 * CONTRACT DECLARES, WHICH IS EXACTLY THE TITLE, THE DESCRIPTION AND THE KEYWORDS.
 * Everything else is excluded for a stated reason rather than by omission:
 *
 *   * the ALIAS is absent because {@link UpdatePortalRequest} declares no alias
 *     member at all — a portal's host names are a sub-resource with their own screen,
 *     and an alias has no identity apart from the portal it resolves to;
 *   * the PORTAL TYPE is absent because it is not editable and the update contract
 *     declares nothing for it. It is decided once, at provisioning;
 *   * the ADMINISTRATOR BLOCK is absent because the legacy edit screen never had it —
 *     `SiteSettings.ascx.vb` carries no name, username, password or mail field — and
 *     because the update contract has no member any of them could reach. Declaring
 *     them on a separate shape is what makes it STRUCTURALLY IMPOSSIBLE for an
 *     administrator credential to appear in an update request: there is no control to
 *     read and no member to write;
 *   * the wide operational surface `SiteSettings.ascx.vb` also carried — hosting fee,
 *     disk space, page and account quota, activity history, expiry date, skins,
 *     pages, payment processor, advertising — belongs to the settings screen, which
 *     writes the settings projection. Those members still travel on every update this
 *     screen sends; see {@link PortalFormComponent} for how, and why that is
 *     mandatory rather than optional.
 */
export interface PortalEditFormModel {
  /** The portal's title. Legacy `SiteSettings` `txtPortalName`, labelled "Title:". */
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

/**
 * The group-level error key raised when the password and its confirmation differ.
 *
 * Held on the GROUP because the rule spans two controls, and surfaced beside the
 * confirmation field, which is where the legacy message appeared.
 */
const PASSWORD_MISMATCH_ERROR = 'passwordMismatch';

/** The alias control's error key for a character outside the permitted set. */
const INVALID_ALIAS_ERROR = 'invalidAliasCharacters';

// =============================================================================
//  CONTROL NAME  ->  REQUEST MEMBER NAME
// =============================================================================

/**
 * The request member each creation control is reported against by the API.
 *
 * This map exists because the two vocabularies are deliberately different: the
 * controls are named for what they hold (see {@link PortalCreateFormModel}) while the
 * API reports validation failures against the member names of
 * {@link CreatePortalRequest}. Without the translation, `alias` would never match a
 * failure reported against `PortalAlias` and the operator would see a banner listing
 * a problem with no field beside it.
 *
 * The lookup itself is performed by `fieldErrorMessage` in
 * `core/utils/form-errors.util.ts`, which folds case and strips the `$.` and
 * `request.` binder prefixes, so each value below need only be the member's own name
 * in any casing — `PortalAlias`, `portalAlias`, `$.portalAlias` and
 * `request.PortalAlias` all resolve to the same entry.
 *
 * `confirm` maps to `null` because it is client-only and no member exists for the API
 * to report against; the mapping is written out rather than left absent so that a
 * reader can see the omission is intentional.
 */
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
 * The request member each edit control is reported against by the API.
 *
 * Note `keyWords` — an interior capital that the API's own contract carries and that
 * the camel-case naming policy therefore preserves on the wire. It is spelled here
 * exactly as {@link UpdatePortalRequest} spells it.
 */
const EDIT_FIELD_MEMBER: Readonly<Record<PortalEditField, string>> = Object.freeze({
  title: 'portalName',
  description: 'description',
  keywords: 'keyWords',
});

/**
 * The measured requiredness wording for each creation control.
 *
 * Seven entries carry a sentence and four carry `null`, and the split is the measured
 * validator set rather than a judgement: `signup.ascx` declares a required-field
 * validator for the alias, the four administrator identity fields, the password and its
 * confirmation, and for NONE of the title, the description, the keywords or the portal
 * type. Written as a total map so that adding a control forces a decision about its
 * requiredness rather than letting one be inherited by silence.
 */
const CREATE_REQUIRED_MESSAGE: Readonly<Record<PortalCreateField, string | null>> = Object.freeze({
  // A radio group always holds one of its two values, so requiredness cannot fail.
  portalType: null,
  alias: ALIAS_REQUIRED_MESSAGE,
  // No `valTitle` exists in the markup. Adding one would refuse a submission the
  // legacy screen accepted.
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

// =============================================================================
//  PURE HELPERS
// =============================================================================

/**
 * Converts a route parameter into an optional portal identifier.
 *
 * ROUTE PARAMETERS ARRIVE AS STRINGS, so the conversion is performed here rather than
 * left implicit. Three properties of it are load-bearing:
 *
 *   * `'0'` becomes `0` and `'-1'` becomes `-1`, faithfully. Both are REAL portal
 *     identifiers — `Portals.PortalID` is declared `IDENTITY(-1,1)`
 *     (`01.00.00.SqlDataProvider:L77`), so the first portal ever created has the
 *     identifier `0` and `-1` is simultaneously a legitimate row key and the value
 *     the legacy null contract used for "absent". Nothing in this function, and
 *     nothing in this file, may treat either as absence;
 *   * ABSENCE IS REPRESENTED BY `undefined` AND BY NOTHING ELSE. The creation route
 *     supplies no `portalId` segment at all, so the input keeps its initial value;
 *     that distinct absence is what {@link PortalFormComponent.isEditMode} tests;
 *   * a MALFORMED segment also becomes `undefined` rather than `NaN`, so no arithmetic
 *     or comparison downstream can encounter one.
 *
 * ⚠ THE PARSE ITSELF IS DELEGATED, and that closed a real gap rather than tidying one.
 * This function used to convert with `Number`, which accepts far more than a decimal
 * integer: `'0x10'` became `16`, `'1e3'` became `1000` and `'1.0'` became `1`, so a
 * malformed address silently addressed a tenant the operator never named. It also
 * checked neither the safe-integer ceiling nor the API's 32-bit range. `parseRouteId` is
 * the one parser in the workspace that makes this conversion, and it reproduces exactly
 * what `int.TryParse` under `NumberStyles.Integer` accepts server-side. This function
 * remains because the ABSENCE REPRESENTATION is this screen's own — the creation route
 * supplies no segment, and `isEditMode` tests `undefined` — and because its name is part
 * of the input contract the route table binds through.
 *
 * @param value The raw route parameter, or a value bound programmatically.
 * @returns The identifier, or `undefined` when none was supplied or the value does not
 * denote a whole number.
 */
export function toOptionalPortalId(value: string | number | null | undefined): number | undefined {
  // Coalescing on `null` alone, so `0` and `-1` — both real portal identifiers — pass
  // through untouched. A `||` here would erase identifier zero.
  return parseRouteId(value) ?? undefined;
}

/**
 * Normalises an alias exactly as the legacy screen did, in the legacy order.
 *
 * `Signup.ascx.vb:L183-L184`, both statements, both before any inspection of the
 * value's content:
 *
 * ```vb
 * txtPortalName.Text = LCase(txtPortalName.Text)
 * txtPortalName.Text = Replace(txtPortalName.Text, "http://", "")
 * ```
 *
 * MIGRATION: `Replace` IS A GLOBAL REPLACEMENT IN VISUAL BASIC. It removes EVERY
 * occurrence of the prefix, not merely a leading one, so `.replaceAll` is the exact
 * equivalent; a leading-prefix strip or a single `.replace(...)` without a global flag
 * would leave a second occurrence in place and change what is stored. The API's own
 * alias inspection performs the identical pair of steps before measuring characters,
 * so client and server normalise to the same string.
 *
 * This is NORMALISATION, NOT VALIDATION: it never reports a failure, and the
 * lower-casing is lossless precisely because the permitted character sets are lower
 * case only.
 *
 * @param alias The alias as typed.
 * @returns The normalised alias.
 */
export function normaliseAlias(alias: string): string {
  return alias.toLowerCase().replaceAll(LEGACY_SCHEME_PREFIX, '');
}

/**
 * The portion of a normalised alias whose characters are inspected.
 *
 * `Signup.ascx.vb:L201-L205`: a child portal is measured on the segment AFTER ITS
 * LAST separator, because the part in front of it is the parent's host name and is
 * not the child's to constrain; a parent portal is measured whole.
 *
 * `InStrRev` is one-based and returns `0` when the separator is absent, so
 * `Mid(text, 0 + 1)` yields the whole string — which `lastIndexOf` reproduces
 * exactly, since it returns `-1` and `-1 + 1` is `0`.
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
 * Builds the alias character rule for one alias control.
 *
 * The rule is SYNCHRONOUS and depends on the current portal type, which is why it is
 * produced by a factory taking an accessor rather than reading a sibling control
 * through `parent`: the accessor closes over the type control itself, so the
 * dependency is established at construction and cannot be broken by the group being
 * reshaped. Because the verdict depends on a value outside the control being
 * validated, the alias must be revalidated whenever the type changes — see
 * {@link PortalFormComponent} — otherwise switching type leaves a stale verdict.
 *
 * MIGRATION ANNOTATION 3 — A LEGACY DEFECT, ANNOTATED AND DELIBERATELY NOT
 * REPRODUCED. The legacy loops at `Signup.ascx.vb:L191-L195` and `L212-L216` appended
 * the message ONCE PER OFFENDING CHARACTER:
 *
 * ```vb
 * For intCounter = 1 To strPortalAlias.Length
 *     If InStr(1, strValidChars, Mid(strPortalAlias, intCounter, 1)) = 0 Then
 *         strMessage &= "<br>" & Localization.GetString("InvalidName", …)
 *     End If
 * Next intCounter
 * ```
 *
 * so an alias such as `my portal name!` produced the same sentence four times in one
 * message block. This rule reports it EXACTLY ONCE however many characters offend,
 * and stops at the first: the migration discipline requires a discovered defect to be
 * annotated rather than silently carried across, and repeating one sentence four
 * times conveys nothing the first does not.
 *
 * MIGRATION: LEGACY BRANCH A HAS NO CLIENT COUNTERPART. `Signup.ascx.vb:L187-L197`
 * ran when `PortalSettings.ActiveTab.ParentId <> PortalSettings.SuperTabId` — that is,
 * when the screen was reached from a portal's own administration rather than from the
 * host menu — and behaved differently in two ways: it FORCED child mode regardless of
 * the radio button, and it measured the WHOLE name against the child set rather than
 * the trailing segment. The single-page application has one administration context and
 * no super-tab notion, so only branch B (L198-L217) is reproduced. The API's create
 * rule likewise implements branch B only, so client and server agree.
 *
 * @param isChildPortal Reads the currently selected portal type.
 * @returns A validator reporting {@link INVALID_ALIAS_ERROR} for a disallowed character.
 */
export function aliasCharactersValidator(isChildPortal: () => boolean): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const raw: unknown = control.value;

    // A non-string or an empty value is not this rule's business. Requiredness is
    // reported once, by the rule that owns it, and reporting an absent value here
    // would put two sentences beside one empty field.
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
 * MIGRATION ANNOTATION 2 — THE MECHANISM CHANGED, THE BEHAVIOUR DID NOT. There is no
 * `asp:CompareValidator` anywhere in `signup.ascx` — measured occurrences: zero — and
 * the check was performed imperatively, inside the click handler, after declarative
 * validation had already passed, at `Signup.ascx.vb:L219-L222`:
 *
 * ```vb
 * If txtPassword.Text <> txtConfirm.Text Then
 *     strMessage &= "<br>" & Localization.GetString("InvalidPassword", …)
 * End If
 * ```
 *
 * Declaring it as a group validator instead makes the same comparison continuous
 * rather than deferred to a submission, which is a change in WHEN the operator learns
 * of it and not in WHAT is accepted: the comparison is the same equality, the wording
 * is the measured resource value verbatim, and no submission that the legacy screen
 * accepted is refused here.
 *
 * The comparison is exact and case-sensitive, and neither value is trimmed — the
 * legacy `<>` on two `String` values was exact too, and trimming would silently
 * accept a pair the legacy screen rejected.
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

  // Nothing is reported while the confirmation is still empty: requiredness owns that
  // state, and reporting a mismatch against an untouched field would accuse the
  // operator of an error before they had a chance to make one.
  if (confirm.length === 0) {
    return null;
  }

  return password === confirm ? null : { [PASSWORD_MISMATCH_ERROR]: PASSWORD_MISMATCH_MESSAGE };
};

// =============================================================================
//  STATUS CODES THIS SCREEN DISTINGUISHES
// =============================================================================
//
// Named rather than written inline so each one carries the reason it is singled out,
// and so the specification can assert the mapping. Every other status falls through
// to the shared summary, which words it.

/** A validation refusal. Field messages travel with it; the group sentence is the server's. */
const BAD_REQUEST_STATUS = 400;

/** A refusal to change a host-administered term. NOT a session failure. */
const FORBIDDEN_STATUS = 403;

/** The addressed portal is not on the server. */
const NOT_FOUND_STATUS = 404;

// =============================================================================
//  COMPONENT
// =============================================================================

/**
 * The portal creation and edit screen.
 *
 * ONE COMPONENT, TWO ROUTES: `/portals/new` renders the creation form and
 * `/portals/:portalId` renders the edit form. Which one is decided solely by whether
 * the `portalId` input carries a value — see {@link PortalFormComponent.isEditMode} —
 * and each mode binds a form of its own, so a field that belongs to one mode cannot
 * physically appear in the other's request. Both routes are declared with
 * `loadComponent`, so this class is never in the initial bundle.
 *
 * ## The sentinel hazard, and why no truthiness test appears anywhere below
 *
 * `Portals.PortalID` is declared `IDENTITY(-1,1)`
 * (`01.00.00.SqlDataProvider:L77`). The first portal ever created therefore has the
 * identifier `0`, and `-1` is simultaneously a legitimate row key AND the value the
 * legacy null contract used to mean "absent"
 * (`Library/Components/Shared/Null.vb:L41-L45`). A `portalId` of `0` and a `portalId`
 * of `-1` are BOTH valid and both mean edit. Consequently this file contains no
 * `if (portalId)`, no `portalId > 0`, no `portalId ?? -1` and no truthiness test of
 * any kind on an identifier: absence is `undefined` and is tested as `undefined`.
 *
 * The related string and date sentinels are honoured on the same principle.
 * `Null.NullString` is the EMPTY STRING rather than `null`
 * (`Null.vb:L71-L75`), so a blank field is submitted as `''` and is never converted
 * to `null` on the way out; and `Null.NullDate` is `DateTime.MinValue`, which arrives
 * as `0001-01-01T00:00:00` — this screen edits no date, and the one date on the
 * update contract is carried straight through without being parsed or reformatted, so
 * it cannot be rendered as a nonsensical calendar date here.
 *
 * ## MIGRATION ANNOTATION 8 — positional contracts become request contracts
 *
 * `Library/Components/Portal/PortalController.vb:L980` declares `CreatePortal` with
 * FIFTEEN positional parameters — `PortalName, FirstName, LastName, Username,
 * Password, Email, Description, KeyWords, TemplatePath, TemplateFile, HomeDirectory,
 * PortalAlias, ServerPath, ChildPath, IsChildPortal` — eleven of them strings, so
 * any two adjacent arguments were interchangeable to the compiler and a transposed
 * pair produced a portal with its description in its keywords and no diagnostic
 * anywhere. `UpdatePortalInfo` at L1568 declares TWENTY-SEVEN. Both are replaced by
 * named request contracts, which cannot be transposed.
 *
 * The same annotation covers the outcome channel. The legacy tree reports status
 * through arguments passed by reference in thirty in-scope places, and this screen's
 * own variant is worse than that: `Signup.ascx.vb:L280` tests `If intPortalId <> -1`,
 * using `-1` as a FAILURE sentinel, while L275-L278 assigns
 * `intPortalId = Null.NullInteger` on exception — so a genuine portal whose
 * identifier is `-1` is indistinguishable from a failure, and
 * `PortalController.vb:L990` repeats the same test one layer down. Here, success and
 * failure are decided SOLELY by the HTTP status and the failure code the problem
 * document publishes. No returned identifier is inspected, compared or tested for
 * anything. That is migration annotation 4.
 *
 * ## MIGRATION — what an update sends, and why it must send all of it
 *
 * `PUT /api/v1/portals/{portalId}` is a WHOLE-ROW REPLACEMENT: the contract's own
 * documentation states that omitting a numeric term does not mean "leave it alone",
 * because the backing columns cannot hold null and the server substitutes nought. So
 * an update composed from this screen's three controls alone would waive the hosting
 * fee, zero every quota and discard the expiry date.
 *
 * The screen therefore refuses to compose an update at all until the portal has been
 * READ, and then carries every member it does not edit forward from that read,
 * unchanged and uncoalesced. That is what {@link PortalFormComponent.portal} gates and
 * what `toUpdateRequest` performs. It also has a second, load-bearing consequence:
 * `Website/admin/Portal/SiteSettings.ascx.vb:L759-L770` refuses the ENTIRE save when a
 * caller who is not a host operator has altered the hosting fee, the disk-space
 * allowance, the page quota, the account quota the activity-history retention or the
 * expiry date, and the API enforces the same rule with a `403`. Carrying those six
 * members through byte-for-byte is precisely what stops this screen from tripping that
 * guard by accident.
 *
 * ## MIGRATION — fields the legacy screen showed that are deliberately gone
 *
 * Each is a documented functional reduction, not an oversight, and none is rendered or
 * sent:
 *
 *   * ANNOTATION 5 — the TEMPLATE selector, its description label and its required
 *     validator. See {@link DEFAULT_TEMPLATE_FILE} for the whole reasoning, including
 *     why the contract member still has to be filled;
 *   * ANNOTATION 6 — the HOME DIRECTORY box (`signup.ascx:L45-L48`, `maxlength="100"`,
 *     pre-filled with the literal `Portals/[PortalID]` at `Signup.ascx.vb:L109-L110`)
 *     and its Customize/Auto-Generate toggle (L354-L368). The filesystem subsystem is
 *     out of scope, so there is nothing to browse and no directory to create. The
 *     contract member is sent as `null`, which the API's create rule documents as a
 *     request for the server-side default — the legacy default was
 *     `"Portals/" + intPortalId` (`PortalController.vb:L991-L992`), which cannot be
 *     computed before the row exists and therefore never belonged on the client;
 *   * ANNOTATION 7 — the DEMO-SIGNUP mode and its instruction label
 *     (`Signup.ascx.vb:L104-L106`), which read the excluded host-settings subsystem;
 *     the CHILD-PATH COLLISION check (L227-L241, `ChildExists.Text`), which tested a
 *     physical directory; the HOME-FOLDER resolution check (L251-L257); and the
 *     CONFIRMATION MAIL (L282-L319), for which no mail endpoint exists. The two
 *     mail-failure resources both embed anchor markup, which is a second reason not to
 *     carry them into a template that renders text;
 *   * the DUPLICATE-ALIAS PRE-CHECK (L259-L265, which called
 *     `PortalSettings.GetPortalAliasLookup`) is not reproduced client-side. It is the
 *     server's `409`, and a client-side pre-check could only ever be a guess about
 *     data it does not hold;
 *   * the `AdminMissing` and `PortalMissing` diagnostics, which belonged to the
 *     dropped template enumeration;
 *   * the audit write (`Signup.ascx.vb:L311-L312`,
 *     `EventLogController.AddLog(… PORTAL_CREATED)`). Auditing is server-side.
 *
 * ## MIGRATION — the post-create redirect
 *
 * The legacy screen navigated to the NEW PORTAL'S OWN URL: L292 computes
 * `AddHTTP(strPortalAlias)` and L316 issues `Response.Redirect(webUrl, True)`. A
 * single-page application cannot follow that — the new tenant answers on a different
 * host name, so the redirect would leave the application entirely, and the alias may
 * not even resolve yet. Both modes navigate to the portal list instead, which is the
 * one place the newly written row is immediately visible.
 *
 * ## MIGRATION — presentation decisions this class records but does not render
 *
 *   * ANNOTATION 9 — `Note.Text` is re-authored as template markup rather than bound
 *     from a resource. The stored value is HTML (`&lt;b&gt;*Note:&lt;/b&gt; Once your
 *     portal is created, you will need to login using the Administrator information
 *     specified above.`) and the legacy renderer assigned resource text straight to a
 *     Web Forms label, which emits it unencoded. Nothing in this class exposes markup,
 *     and no member below is anything but plain text;
 *   * ANNOTATION 10 — the description and keywords fields are plain multi-line text
 *     areas. `signup.ascx:L56` and `L61` declare `textmode="MultiLine" rows="3"`, and
 *     the rich-text editor provider is excluded, so no editor is substituted. A
 *     deliberate functional reduction;
 *   * ANNOTATION 11 — the help affordance is keyboard reachable. `controls/labelcontrol.ascx`
 *     declares `tabindex="-1"` on BOTH `cmdHelp` (L3) and `imgHelp` (L4), which removed
 *     the only route to a field's help text from a keyboard. The shared form-field
 *     component takes help text as an input and renders it without such an attribute,
 *     so the defect is not carried across;
 *   * the `IsHostMenu` visibility gate on the portal-type row
 *     (`Signup.ascx.vb:L99-L107`, which hid the row and switched the screen into demo
 *     mode when it was reached from a portal rather than from the host menu) has no
 *     client counterpart: host administration is out of scope and the application has a
 *     single administration context. The row is always rendered in creation mode, and
 *     the type defaults to Parent exactly as L101 set it.
 *
 * ## What this class does NOT do
 *
 * No URL is built and no query string is assembled: every request goes through the
 * portal store, which goes through the typed transport, which reads the endpoint
 * catalogue. The HTTP client is not injected, the configured base is not read, and no
 * business rule is evaluated here beyond the validation the legacy screen itself
 * performed.
 */
@Component({
  selector: 'app-portal-form',
  standalone: true,
  // ReactiveFormsModule for the two typed forms, and four shared components: the page
  // title, the labelled-field wrapper that replaces `controls/labelcontrol.ascx` and
  // the per-field validator spans, the RFC 7807 surface, and the fetching affordance.
  // Nothing else is imported. The collapsible `dnn:SectionHead` affordance
  // (`signup.ascx:L8` and `L73`) has no shared component and none is added: the two
  // groups it delimited are semantic field sets in the paired template.
  imports: [
    ReactiveFormsModule,
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
  // ---------------------------------------------------------------------------
  //  COLLABORATORS
  // ---------------------------------------------------------------------------

  /**
   * The single, root-provided portal store — the ONLY route to the API from here.
   *
   * No provider is declared for it, by this class or by either route. Two instances
   * would give two screens two different answers about which portal is selected, and
   * this screen would then edit one portal while the list showed another.
   */
  private readonly portalStore = inject(PortalStore);

  /** The application's notification queue. Used for outcomes, never for field messages. */
  private readonly notifications = inject(NotificationService);

  /** Navigation away from this screen: on success, and on cancel. */
  private readonly router = inject(Router);

  /**
   * The host name a child portal's alias is prefixed with.
   *
   * Read from the injected document rather than from the global object, so the value
   * is substitutable in a specification and this class touches no ambient global. It is
   * read once: a page's host cannot change without a reload.
   *
   * MIGRATION ANNOTATION 12 — this replaces a SERVER ROUND TRIP.
   * `signup.ascx:L33` declares `optType` with `AutoPostBack="True"`, so selecting a
   * portal type posted the whole page back so that `Signup.ascx.vb:L342-L352` could
   * compute `GetDomainName(Request) & "/"` on the server and push it into the alias
   * box. The browser already knows its own host, so the same prefill happens locally
   * and no request is made. See {@link PortalFormComponent.onPortalTypeSelected}.
   */
  private readonly hostName: string = inject(DOCUMENT).location.host;

  // ---------------------------------------------------------------------------
  //  ROUTE INPUT
  // ---------------------------------------------------------------------------

  /**
   * The portal being edited, or `undefined` on the creation route.
   *
   * THE NAME IS PART OF THE CONTRACT AND MUST NOT CHANGE. Component input binding is
   * enabled in `app.config.ts`, which binds a route parameter onto an input OF THE
   * SAME NAME, and the route declares its segment as `:portalId`. Renaming this member
   * to `id`, `portalID` or anything else breaks the binding SILENTLY — no compile
   * error, no runtime warning — and the edit route then renders an empty creation
   * form.
   *
   * A SIGNAL input rather than a decorated field, for the same load-bearing reason the
   * shared error banner records: every derived member below is a `computed()` over this
   * one source, and a `computed()` over a plain field would never recompute, so under
   * change-detection-on-push the screen would render whichever mode it saw first and
   * ignore every later navigation. The transform is written out and specified rather
   * than taken from the framework's numeric attribute helper, because the behaviour
   * that matters here is what happens to an ABSENT value and to `'0'` and `'-1'` — see
   * {@link toOptionalPortalId}.
   */
  readonly portalId = input<number | undefined, string | number | null | undefined>(undefined, {
    transform: toOptionalPortalId,
  });

  // ---------------------------------------------------------------------------
  //  MODE
  // ---------------------------------------------------------------------------

  /**
   * Whether the screen is editing an existing portal rather than creating one.
   *
   * An EXPLICIT PRESENCE TEST, never a truthiness test. `Number.isFinite` is a guard
   * against a malformed parameter arriving as `NaN`; it accepts `0` and `-1`, which is
   * the whole point, and it is unreachable through the declared transform — which
   * already maps a malformed value to `undefined` — so it defends against a future
   * widening of the input's write type rather than against today's router.
   *
   * The `null` comparison is likewise deliberate. The read type cannot be `null`
   * today, but stating absence as "neither null nor undefined" keeps the contract
   * legible and survives that type being widened.
   */
  readonly isEditMode: Signal<boolean> = computed(() => {
    const id: number | undefined = this.portalId();

    return id !== null && id !== undefined && Number.isFinite(id);
  });

  // ---------------------------------------------------------------------------
  //  THE TWO FORMS
  // ---------------------------------------------------------------------------
  //
  // BOTH ARE BUILT ONCE AND EACH IS ITS OWN TYPE, which is what makes the mode
  // boundary structural instead of conditional. The alternative — one group whose
  // controls are added and removed as the mode changes — would type every control as
  // possibly absent, would need a non-null assertion at every use, and would leave the
  // administrator credential reachable from an update path by a single mistake. Here
  // there is no control to reach: `PortalEditFormModel` declares three members and the
  // update request is composed from those three and a read record, so a password
  // cannot appear in an update however the code is later edited.
  //
  // Every control is constructed `nonNullable`, so `.value` is fully typed rather than
  // a partial and `reset()` returns to the declared initial value rather than to
  // `null`.
  //
  // THE VALIDATOR SET IS MEASURED, NOT ASSUMED. `signup.ascx` declares exactly eight
  // `asp:requiredfieldvalidator` controls — `valPortalName`, `valTemplate`,
  // `valFirstName`, `valLastName`, `valUsername`, `valPassword`, `valConfirm` and
  // `valEmail` — and NOTHING ELSE: measured occurrences of `asp:CompareValidator`,
  // `asp:RegularExpressionValidator`, `asp:RangeValidator` and `asp:ValidationSummary`
  // are each ZERO. `valTemplate` leaves with the field it guarded, so seven survive,
  // and the fields with NO validator in the legacy markup get none here: the TITLE, the
  // DESCRIPTION and the KEYWORDS are optional, and adding a requiredness rule to the
  // title would refuse a submission the legacy screen accepted.
  //
  // Two rules are absent for the same measured reason and their absence is deliberate:
  // there is NO mail-address FORMAT rule, because the legacy screen carried only a
  // required-field validator on the mail box and no pattern of any kind, and adding one
  // would refuse addresses the legacy operator could enter. The API applies its own
  // format check, and a rejection it raises is reported against the field by the
  // server-message path below — which is the honest place for a rule this screen did
  // not have. There is likewise no password STRENGTH rule here: the policy is
  // configured server-side and its numbers are not published to the client, so
  // duplicating a guess at it would be worse than reporting the server's own verdict.

  /**
   * The creation form. Bound only while {@link isEditMode} is false.
   *
   * Built by a static factory so that the alias character rule can close over the
   * portal-type CONTROL rather than reach for it through `parent` after the fact.
   */
  protected readonly createForm: FormGroup<PortalCreateFormModel> =
    PortalFormComponent.buildCreateForm();

  /** The edit form. Bound only while {@link isEditMode} is true. */
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
   * Assembles the creation form.
   *
   * Static, and the two locals in front of the group are the reason: the alias rule
   * depends on the portal type, so the type control must exist before the alias control
   * is constructed. Closing over the control itself makes that dependency permanent —
   * it cannot be broken by the group being reshaped, and it needs no traversal that
   * could return `null`.
   *
   * @returns The creation group, with the cross-field password rule attached.
   */
  private static buildCreateForm(): FormGroup<PortalCreateFormModel> {
    // Parent by default, exactly as `Signup.ascx.vb:L101` set `optType.SelectedValue`.
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
        // The confirmation carries the SAME bounds as the credential it confirms. Omitting the minimum
        // here would let the two boxes disagree about what is acceptable, so a credential of six
        // characters typed identically twice would report the fault against one box and not the other.
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

  /**
   * Whether this screen has asked the store to write.
   *
   * Needed because the store's detail slices serve BOTH the read and the write — a
   * create and an update drive the same loading and failure signals a hydration does —
   * so this flag is what separates "fetching the portal" from "saving it" for the two
   * affordances the template shows.
   */
  private readonly saveRequested: WritableSignal<boolean> = signal(false);

  /**
   * The failure the store already held when this screen was constructed.
   *
   * Captured untracked, as an identity rather than a value, and suppressed by
   * {@link failure} below. The store is application-wide and its failure slice outlives
   * a screen, so without this a refusal reported to an entirely different screen would
   * be waiting in the banner the moment this one opened. Every fresh failure is a new
   * object, so identity is a sufficient and exact test.
   */
  private readonly baselineFailure: PortalFailure | null = untracked(() =>
    this.portalStore.detailFailure(),
  );

  /**
   * The record the edit form was last filled from.
   *
   * A plain field rather than a signal: nothing observes it, and it exists solely so
   * the hydration step can tell "the portal arrived" from "the portal is still the
   * one I already filled from", and therefore not overwrite an operator's unsaved
   * edits every time an unrelated signal changes.
   */
  private hydratedFrom: PortalDetail | null = null;

  // ---------------------------------------------------------------------------
  //  DERIVED STATE
  // ---------------------------------------------------------------------------

  /**
   * The failure to report, with any pre-existing one suppressed.
   *
   * See {@link baselineFailure}. Once this screen has issued a read or a write the
   * store's slice is its own, because both paths clear the slice before starting.
   */
  readonly failure: Signal<PortalFailure | null> = computed(() => {
    const current: PortalFailure | null = this.portalStore.detailFailure();

    return current === this.baselineFailure ? null : current;
  });

  /**
   * The RFC 7807 document for the shared error banner, or `null` for nothing to show.
   *
   * Handed over UNRESOLVED and unaltered: the banner resolves severity, title, sentence,
   * per-field messages and support reference itself, through the same shared utility
   * this class uses, so the two cannot describe one failure two different ways.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed(
    () => this.failure()?.problem ?? null,
  );

  /**
   * The portal being edited, once it has actually been read, or `null`.
   *
   * THE IDENTIFIER IS COMPARED, not merely tested for presence. The store's selection
   * is application-wide, so without the comparison a record left over from a previous
   * screen could be treated as this portal's — and since an update is a whole-row
   * replacement, that would write one portal's terms over another's. The comparison is
   * `===` on two numbers and is correct for `0` and `-1` alike.
   */
  readonly portal: Signal<PortalDetail | null> = computed(() => {
    const id: number | undefined = this.portalId();

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
   * True while a create or an update is in flight.
   *
   * A conjunction rather than a flag of its own, so it cannot get stuck: the store
   * lowers its loading slice on BOTH the success and the failure path, and this member
   * follows it down either way. The store offers no failure continuation, so a flag
   * cleared only by a success callback would leave the submit button disabled forever
   * after a single refused save.
   */
  readonly saving: Signal<boolean> = computed(
    () => this.saveRequested() && this.portalStore.detailLoading(),
  );

  /**
   * Whether the edit form has something real to edit.
   *
   * False while the read is in flight and false when it failed, so the template can
   * show the fetching affordance or the banner instead of an empty form that would
   * compose a destructive whole-row replacement if submitted.
   */
  protected readonly canEdit: Signal<boolean> = computed(
    () => this.isEditMode() && this.portal() !== null,
  );

  /**
   * Whether the submit control is available.
   *
   * Deliberately NOT gated on the form being valid. The legacy buttons were always
   * enabled and validation ran on click (`Signup.ascx.vb:L155`), so pressing submit is
   * how an operator asks to be told what is wrong; disabling the control would hide
   * every field message behind a control that cannot be pressed.
   */
  protected readonly canSubmit: Signal<boolean> = computed(() => {
    if (this.saving()) {
      return false;
    }

    return this.isEditMode() ? this.canEdit() : true;
  });

  /** The page heading. `Add New Portal` when creating, `Edit Portals` when editing. */
  protected readonly heading: Signal<string> = computed(() =>
    this.isEditMode() ? EDIT_HEADING : CREATE_HEADING,
  );

  /** The submit control's wording. `Create Portal` when creating, `Update` when editing. */
  protected readonly submitLabel: Signal<string> = computed(() =>
    this.isEditMode() ? EDIT_SUBMIT_LABEL : CREATE_SUBMIT_LABEL,
  );

  /** The cancel control's wording. One value in both modes. */
  protected readonly cancelLabel: string = CANCEL_LABEL;

  /**
   * The one sentence describing the current failure, or `null` when there is none.
   *
   * The precedence is measured rather than arbitrary, and each arm has a reason:
   *
   *   1. a STATE REFUSAL is worded from the shared conflict vocabulary, which already
   *      holds the legacy `DuplicatePortalAlias.Text` verbatim against the code the
   *      server publishes for it. Wording it again here would create a second copy of a
   *      sentence that must not drift;
   *   2. a `403` is worded as a refusal to change a host-administered term. It is NOT a
   *      session failure, nothing here treats it as one, and no sign-in is offered;
   *   3. a `404` says the portal is gone;
   *   4. a `400` is left to the server's own sentence, because the useful information
   *      is in the per-field messages the form shows beside the fields;
   *   5. anything else falls back to the legacy creation-failure wording — see
   *      {@link CREATE_ERROR_MESSAGE}, which is annotation 13.
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

    const fallback: string | null =
      failure.status === BAD_REQUEST_STATUS ? null : CREATE_ERROR_MESSAGE;

    return summarizeProblem(failure.problem, fallback).message;
  });

  // ---------------------------------------------------------------------------
  //  VALUES THE TEMPLATE NEEDS BUT MUST NOT RESTATE
  // ---------------------------------------------------------------------------

  /**
   * The measured character limits, published so the rendered `maxlength` attributes and
   * the validators above are ONE source rather than two.
   *
   * This is the only presentation detail this class publishes, and it is published for a
   * specific reason: a `maxlength` attribute and a `Validators.maxLength` rule that
   * disagree produce a field that either silently truncates what the operator typed or
   * accepts what the API refuses. Field LABELS and HELP TEXT are deliberately NOT here —
   * they carry no rule, the template owns its own wording, and duplicating them would
   * create two places for one sentence to drift.
   */
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
   * The portal-type choices, in the legacy declaration order.
   *
   * Published as typed pairs because the VALUES are the load-bearing part — see
   * {@link PortalType} — and a template that wrote `value="P"` as a bare string
   * attribute would not be checked against the control's type. The wording is
   * `Parent.Text` and `Child.Text` from `Signup.ascx.resx:L270-L275`, which match the
   * list-item text in the markup.
   *
   * TWO NOTES FOR THE PAIRED TEMPLATE, both established by rendering this screen in a
   * browser rather than by reading the framework's source:
   *
   *   * bind the value with `[value]="choice.value"`, which the framework's radio
   *     value accessor CONSUMES as a directive input and does NOT reflect onto the
   *     rendered element. The chosen value therefore reaches the control correctly —
   *     verified: choosing Child prefilled the alias and the submitted request carried
   *     `isChildPortal: true` — but no `value` attribute appears in the document, so
   *     an automated check must select a radio by its `id` and not by `[value="C"]`;
   *   * give both radios the SAME `name` attribute. The framework groups them by the
   *     form control regardless, so selection works without it, but a native radio
   *     group is what makes the pair ONE stop in the tab order with arrow-key
   *     selection between the options; without it each radio is its own stop.
   */
  protected readonly portalTypeChoices: readonly { readonly value: PortalType; readonly label: string }[] =
    Object.freeze([
      Object.freeze({ value: PARENT_PORTAL_TYPE, label: 'Parent' }),
      Object.freeze({ value: CHILD_PORTAL_TYPE, label: 'Child' }),
    ]);

  // ---------------------------------------------------------------------------
  //  SIDE EFFECTS
  // ---------------------------------------------------------------------------
  //
  // THREE, AND EACH PERFORMS A GENUINE SIDE EFFECT rather than deriving a value: one
  // issues a request, one writes into an imperative subsystem the form controls are, and
  // one raises a notification. Nothing that could be a `computed()` is an effect, and
  // every body that touches a collaborator runs UNTRACKED so that reading the store
  // inside it cannot make the effect its own trigger.

  /**
   * Reads the addressed portal whenever the route names one.
   *
   * The body is untracked, and that is not a stylistic choice: the store's own
   * selection command reads its selection slice, so a tracked body would take a
   * dependency on a signal the very same call then writes, and the effect would re-run
   * and issue a SECOND request for the same portal. Untracked, the only trigger is the
   * input — which is exactly the intent, and which also handles a navigation from one
   * portal to another on the same component instance, where no lifecycle hook runs.
   *
   * Nothing happens on the creation route. The selection is deliberately NOT cleared
   * there: it is application-wide state that the listing screen also reads, and
   * {@link portal} already refuses to use a record belonging to another portal, so
   * clearing it would take something away from another screen to solve a problem that
   * is already solved.
   */
  private readonly readEffect = effect((): void => {
    const id: number | undefined = this.portalId();

    if (id === undefined) {
      return;
    }

    untracked(() => {
      this.portalStore.loadPortal(id);
    });
  });

  /**
   * Fills the edit form once the portal has been read.
   *
   * Guarded on the record's IDENTITY, so this runs once per record rather than once per
   * unrelated signal change — which is what stops it overwriting an operator's unsaved
   * edits. `reset` rather than `patchValue`, so the form returns to pristine and
   * untouched and no field message is shown against a value the operator has not seen
   * yet.
   *
   * MIGRATION: an absent string hydrates as the EMPTY STRING and never as the word
   * "null" or as a placeholder. `Null.NullString` is `''` (`Null.vb:L71-L75`), so empty
   * and absent were never distinguished in this data, and the coalescing below is the
   * faithful reading rather than a defensive one.
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
   * Announces a failure once, at the measured severity.
   *
   * The notification carries the OUTCOME; the banner carries the document and the form
   * carries the per-field messages, so the same sentence is never rendered twice by two
   * mechanisms.
   *
   * MIGRATION: THE SEVERITY VOCABULARY IS THREE-VALUED AND A REFUSAL IS A WARNING, NOT
   * AN ERROR. The legacy vocabulary is `RedError`, `YellowWarning` and `GreenSuccess`,
   * and `Website/admin/Security/AccessDenied.ascx.vb` used `YellowWarning` for a
   * permission denial. The shared severity rule already encodes that — it maps `401`,
   * `403`, `404` and `429` to the warning band and everything else to the error band —
   * so the store's classification is forwarded unchanged rather than re-derived here.
   * The notification vocabulary's fourth member, `success`, has no failure counterpart
   * and is reached only from {@link afterWrite}.
   *
   * The whole body is untracked because appending to the notification queue writes a
   * signal the queue owns; a tracked body could observe its own append and announce
   * the same failure again.
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

        this.notifications.notify(severity, message);
      }

      this.saveRequested.set(false);
    });
  });

  /**
   * Wires the portal-type reaction.
   *
   * A subscription rather than an effect, because the trigger is a form control and a
   * control's value is not a signal in this framework version. It is torn down with the
   * component, and the injection context the constructor provides is what lets the
   * teardown operator find the destroy reference without one being passed.
   */
  constructor() {
    this.createForm.controls.portalType.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe((selected: PortalType): void => {
        this.onPortalTypeSelected(selected);
      });
  }

  // ---------------------------------------------------------------------------
  //  FIELD MESSAGES
  // ---------------------------------------------------------------------------

  /**
   * The message to show beside one creation field, or `null` when there is nothing
   * to say.
   *
   * The order is: the client's own rule first, then the cross-field password rule for
   * the confirmation box, then whatever the server reported against the corresponding
   * request member. Client first because it is the more immediate — an operator who has
   * just emptied a field wants to read about that, not about the submission before it —
   * and the server's message survives underneath because a rule this screen does not
   * have (a mail-address format, a password policy) can only be reported by the server.
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

    return fieldErrorMessage(this.problem(), CREATE_FIELD_MEMBER[field]);
  }

  /**
   * The message to show beside one edit field, or `null` when there is nothing to say.
   *
   * No control on the edit form is required — the legacy edit screen declared no
   * required-field validator for the title, the description or the keywords either — so
   * only a length rule and the server's own report can produce a message here.
   *
   * @param field The control to describe.
   * @returns The message, or `null`.
   */
  protected editMessageFor(field: PortalEditField): string | null {
    const own: string | null = PortalFormComponent.controlMessage(this.editForm.controls[field], null);

    if (own !== null) {
      return own;
    }

    return fieldErrorMessage(this.problem(), EDIT_FIELD_MEMBER[field]);
  }

  /**
   * Resolves one control's own validation message.
   *
   * Nothing is reported until the control has been edited or visited, so a form that is
   * opened and not yet used shows no messages at all — which is what makes pressing
   * submit, and the `markAllAsTouched` that follows an invalid one, the moment every
   * message becomes visible.
   *
   * Every read of the error dictionary uses BRACKET ACCESS. It is an index-signature
   * type and the workspace forbids property access on one, so `errors.required` would
   * not compile; each value is taken as `unknown` and narrowed, so nothing untyped
   * escapes into the returned string.
   *
   * @param control The control to describe.
   * @param requiredMessage The measured wording for this control's requiredness rule, or
   * `null` when the control has no such rule.
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
   * Held on the group, because the rule spans two controls, but withheld until the
   * confirmation itself has been edited or visited — the same courtesy every other
   * field gets.
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
   * MIGRATION ANNOTATION 12 — reproduces `Signup.ascx.vb:L342-L352` without the round
   * trip the legacy `AutoPostBack="True"` forced:
   *
   * ```vb
   * If optType.SelectedValue = "C" Then
   *     txtPortalName.Text = GetDomainName(Request) & "/"
   * Else
   *     txtPortalName.Text = ""
   * End If
   * ```
   *
   * so choosing Child pre-fills the alias with this page's own host and a separator,
   * ready for the child segment, and choosing Parent clears it. The host comes from the
   * injected document — see {@link hostName} — rather than from a request the server
   * would have had to answer.
   *
   * The edit-mode guard is redundant by construction, because the portal type exists
   * only on the creation form and the edit form is a different object with no such
   * control, and it is written anyway: it states the invariant that a hydrated alias
   * must never be wiped, and it is what a specification can assert against.
   *
   * The alias is revalidated explicitly. Its character rule depends on this value, so
   * without the revalidation a verdict reached under the previous type would survive the
   * switch — an alias containing a dot is legitimate for a parent and not for a child,
   * and the operator would be told the opposite of the truth.
   *
   * @param selected The newly chosen portal type.
   */
  private onPortalTypeSelected(selected: PortalType): void {
    if (this.isEditMode()) {
      return;
    }

    const alias = this.createForm.controls.alias;

    alias.setValue(selected === CHILD_PORTAL_TYPE ? `${this.hostName}${ALIAS_SEGMENT_SEPARATOR}` : '', {
      emitEvent: false,
    });
    alias.updateValueAndValidity({ emitEvent: false });
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

  /**
   * Abandons the edit.
   *
   * CARRIES NO VALIDATION, exactly as `signup.ascx:L114` declared
   * `causesvalidation="False"` on `cmdCancel` and `Signup.ascx.vb:L130-L140` did nothing
   * but redirect. Nothing is marked touched, nothing is submitted, and no field message
   * appears on the way out.
   *
   * MIGRATION: the destination is the portal list rather than the legacy screen's two
   * conditional destinations — `NavigateURL()` from the host menu (L133) or the current
   * portal's own domain (L135) — because host administration is out of scope and the
   * application has one place a portal list lives.
   */
  protected onCancel(): void {
    void this.router.navigate([PORTAL_LIST_ROUTE]);
  }

  // ---------------------------------------------------------------------------
  //  WRITES
  // ---------------------------------------------------------------------------

  /**
   * Creates the portal.
   *
   * THE ORDER OF THE STEPS IS THE LEGACY ORDER, and it is reproduced deliberately
   * because normalising and validating in the other order changes what is accepted:
   *
   *   1. `Signup.ascx.vb:L155` opens the whole handler with `If Page.IsValid Then`, so
   *      NOTHING below it ran until declarative validation had passed. An invalid form
   *      here is marked touched — which is what makes every field message visible at
   *      once — and the method returns;
   *   2. L183-L184 then normalised the alias, BEFORE any inspection of its content. The
   *      normalised value is written back into the control so the operator can see what
   *      will be submitted, with the change event suppressed so the write cannot
   *      re-enter the reaction that produced it;
   *   3. L186-L217 inspected the alias's characters and L219-L222 compared the two
   *      password boxes. Both are declarative rules here, so both have already been
   *      evaluated by step 1 — a change in WHEN the operator is told, not in WHAT is
   *      accepted;
   *   4. L227-L265 performed a directory-collision test, a home-folder resolution and a
   *      duplicate-alias lookup. All three are gone: the first two touch a file system
   *      that is out of scope, and the third is the server's `409`.
   *
   * Normalisation cannot turn a valid alias invalid, so no second validity test is
   * needed after step 2: lower-casing is lossless against character sets that are lower
   * case only, and removing the scheme prefix can only shorten the value.
   */
  private submitCreate(): void {
    if (this.saving()) {
      return;
    }

    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();

      return;
    }

    this.normaliseAliasControl();

    this.saveRequested.set(true);
    this.portalStore.createPortal(this.toCreateRequest(), (): void => {
      this.afterWrite(CREATE_SUCCEEDED_MESSAGE);
    });
  }

  /**
   * Writes the edited portal.
   *
   * REFUSES TO PROCEED WITHOUT A READ RECORD, and that guard is the most important line
   * in this method. The update endpoint replaces the whole row, and the server writes
   * nought for an omitted numeric term, so an update composed while the portal had not
   * been read would waive its hosting fee, zero its quotas and discard its expiry date —
   * a destructive write that would look like a successful save. The identifier on the
   * record is compared with the addressed identifier by {@link portal} before the record
   * is offered here at all.
   */
  private submitUpdate(): void {
    if (this.saving()) {
      return;
    }

    const id: number | undefined = this.portalId();
    const detail: PortalDetail | null = this.portal();

    if (id === undefined || detail === null) {
      return;
    }

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
    this.portalStore.updatePortal(id, request, (): void => {
      this.afterWrite(UPDATE_SUCCEEDED_MESSAGE);
    });
  }

  /**
   * Writes the normalised alias back into its control.
   *
   * `emitEvent: false` because the only listener on this form is the portal-type
   * reaction, and although that reaction watches a different control, suppressing the
   * event states the intent: this write is a display correction rather than an operator
   * edit, and it must not be able to start a cascade. The control is left dirty, which it
   * already is by the time a submission is attempted.
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
   * Composes the creation request from the creation form.
   *
   * Twelve members, one per member the contract declares, each named rather than
   * positional — see annotation 8 on the class. Three of them deserve a note:
   *
   *   * `portalName` receives the TITLE and `portalAlias` receives the ALIAS. That is
   *      the inversion recorded on {@link PortalCreateFormModel}, and this is the single
   *      place the two vocabularies meet;
   *   * `homeDirectory` is `null`, which the API documents as a request for the
   *      server-side default. See annotation 6 on the class;
   *   * `templateFile` carries a constant. See {@link DEFAULT_TEMPLATE_FILE}.
   *
   * The confirmation control is deliberately NOT read: no member exists for it, on this
   * contract or on the fifteen-parameter call it replaces, so it is compared and
   * discarded.
   *
   * MIGRATION: A BLANK FIELD IS SUBMITTED AS THE EMPTY STRING, NEVER CONVERTED TO
   * `null`. `Null.NullString` is `''` (`Null.vb:L71-L75`), so the legacy screen and the
   * legacy data layer never distinguished the two, and coalescing `''` to `null` here
   * would invent a distinction the schema cannot represent. Nothing is trimmed either,
   * for the same reason the legacy code trimmed nothing.
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
   * Composes the update request from the read record and the three edited fields.
   *
   * TWENTY-SEVEN MEMBERS, AND EVERY ONE OF THEM IS WRITTEN. The endpoint replaces the
   * whole row and the server substitutes nought for an omitted numeric, so a request
   * that mentioned only what this screen edits would silently rewrite everything it did
   * not mention. Three members come from the form; the identifier comes from the route;
   * the remaining twenty-three are carried forward from the record EXACTLY as it was
   * read — not parsed, not clamped, not rounded, not coalesced and not defaulted.
   *
   * That literal carry-forward is what protects the six HOST-ADMINISTERED terms. The
   * legacy screen compared the submitted hosting fee, disk-space allowance, page quota,
   * account quota, activity-history retention and expiry date against the stored portal
   * and threw when a non-host caller had altered any of them
   * (`SiteSettings.ascx.vb:L759-L770`); the API enforces the same rule and answers
   * `403`. Passing each value through unchanged means this screen cannot trip that guard,
   * and it is also why coalescing a `null` quota to `0` here would be a defect rather
   * than a tidy-up: nought means UNLIMITED for the account quota and the disk-space
   * allowance, so the substitution would change the portal's terms and earn the refusal.
   *
   * MIGRATION: THE OPTION-STRICT ASYMMETRY IS RESOLVED BY THE CONTRACT, NOT BY A CAST
   * HERE. The legacy web pages compiled with strict typing disabled
   * (`Website/release.config:L125` sets `strict="false"`) while the class library did
   * not, and two coercions in that gap changed stored values rather than merely syntax:
   * `PortalInfo.HostFee` is a `Single` and `PortalInfo.HostSpace` an `Integer`, yet
   * `UpdatePortalInfo` (`PortalController.vb:L1568`) declares BOTH as `Double`, and the
   * screen's own account-quota local was floating point despite naming an integer. Every
   * one of those members is a single declared numeric type on both contracts here, so no
   * widening, narrowing or truncation happens anywhere in this method — the values are
   * moved, not converted.
   *
   * MIGRATION: `processorCredentialReference` IS SENT AS `null`, WHICH KEEPS THE
   * CURRENT REFERENCE. It is write-only and appears on no read contract, so there is
   * nothing to carry forward; the contract documents `null` as "keep" and `''` as
   * "clear", and this screen has no business clearing a payment credential.
   *
   * Static, and given both the identifier and the record explicitly, so that it can be
   * exercised without a component instance and so that it cannot silently read a signal
   * that has moved on since the submission began.
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
    };
  }

  /**
   * Reports a successful write and leaves the screen.
   *
   * MIGRATION: the destination is the portal list, not the new portal's own address.
   * `Signup.ascx.vb:L292` computed `AddHTTP(strPortalAlias)` and L316 issued
   * `Response.Redirect(webUrl, True)`, sending the operator's browser to the tenant that
   * had just been provisioned. A single-page application cannot follow that: the new
   * tenant answers on a different host name, so the navigation would leave the
   * application, and the alias may not resolve at all until it is published. Both modes
   * therefore land on the list, which is where the written row is visible.
   *
   * MIGRATION: the confirmation itself is net-new at the SUCCESS band of the measured
   * three-valued vocabulary. The legacy screen reported success by navigating and said
   * nothing.
   *
   * @param message The confirmation to queue.
   */
  private afterWrite(message: string): void {
    this.saveRequested.set(false);
    this.notifications.success(message);

    void this.router.navigate([PORTAL_LIST_ROUTE]);
  }
}
