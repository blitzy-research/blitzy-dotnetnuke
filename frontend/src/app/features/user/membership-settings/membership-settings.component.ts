import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  type OnInit,
  type Signal,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  MembershipSettings,
  MembershipSettingsUpdateResult,
} from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import { fieldErrorMessage } from '../../../core/utils/form-errors.util';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';

// ---------------------------------------------------------------------------
// WHERE EVERY MEMBER OF THIS POLICY TAKES EFFECT
// ---------------------------------------------------------------------------
//
// A settings screen that stores values nothing reads is a screen that lies, so the
// consumer of each of the twenty-three members is named here and can be checked. The
// list is exhaustive on purpose: a member added later with no entry is the omission
// this block exists to make visible.
//
//   * The nine listing-column switches, and `displaySuppressPager` and
//     `recordsPerPage` — the account listing. The columns are withheld SERVER-SIDE,
//     by the account resource, so a hidden column never crosses the boundary at all;
//     the pager rule and the page size are read by `core/state/user.store.ts`.
//   * `displayMode` — the account listing's OPENING VIEW, applied by
//     `core/state/user.store.ts` when it brings the screen up: every account, the
//     first letter of the alphabet strip, or no query at all.
//     (`Website/admin/Users/Users.ascx.vb` L494-L506.)
//   * `profileDefaultVisibility` — the visibility a profile value that has never been
//     set reads as. Applied SERVER-SIDE by the account resource onto every unset value
//     and every declaration it projects, reproducing
//     `Library/Components/Users/Profile/ProfilePropertyDefinition.vb` L348-L359 and
//     `Library/Components/Users/Profile/ProfileController.vb` L109-L112.
//   * `profileDisplayVisibility` — whether the profile screen offers the per-property
//     visibility control, resolved by `features/user/user-profile` together with the
//     caller being the subject of the profile, which is the second half of the legacy
//     predicate at `Website/admin/Users/Profile.ascx.vb` L58-L63.
//   * `profileManageServices` — whether an account may manage its own service
//     subscriptions. Enforced SERVER-SIDE on all five member-service endpoints, as a
//     refusal rather than as a hidden affordance; see the note further down.
//   * `securityUsersControl` — which account picker the role-assignment screen offers,
//     read by `features/role/role-assignment`.
//   * `securityDisplayNameFormat` — applied to every account on creation and on update,
//     and adopting a NEW format renames every account in the tenant. Both are
//     server-side and inside one transaction; the count comes back on the write.
//   * `securityEmailValidation`, `securityRequireValidProfile` and
//     `securityRequireValidProfileAtLogin` — enforced server-side by the account and
//     authentication resources.
//   * `redirectAfterLogin`, `redirectAfterRegistration` and `redirectAfterLogout` —
//     ⚠ MAINTAINED HERE AND ACTED ON ELSEWHERE. Each names a DOTNETNUKE PAGE, and this
//     application renders none: page rendering is excluded by AAP 0.2.2.2 and 0.2.2.4,
//     and the page resource offers a list and one page's detail and nothing that renders
//     one. The migration is side by side (AAP 0.1.1), so the DotNetNuke application
//     remains deployed and reads all three —
//     `Website/admin/Authentication/Login.ascx.vb` L147-L177,
//     `Website/admin/Users/ManageUsers.ascx.vb` L80-L98 and
//     `Library/Components/Authentication/AuthenticationController.vb` L251-L270. This
//     console maintains the policy and does not act on it, and says so on the fields
//     themselves rather than implying an effect it cannot have. The after-registration
//     destination has no local consumer even in principle: there is no
//     self-registration flow in this application and no register route in its closed
//     route set. Recorded in `MIGRATION_NOTES.md`.
//
// ---------------------------------------------------------------------------
// WHAT THIS SCREEN IS, AND WHAT IT DELIBERATELY IS NOT
// ---------------------------------------------------------------------------
//
// The tenant-wide account-administration policy: which columns the account
// listing shows, how many rows a page holds, how profiles default their
// visibility, where a person lands after signing in, out or registering, and how
// display names are composed.
//
// MIGRATION: the transformation plan maps this screen to three legacy controls -
// `Website/admin/Users/UserSettings.ascx`, `Website/admin/Users/Membership.ascx`
// and `Website/admin/Users/MemberServices.ascx`. NEITHER OF THE OTHER TWO IS
// RENDERED HERE, for two different reasons, and both are recorded rather than
// absorbed silently because a later reader would otherwise look for nine membership
// fields, four account actions and a services grid that deliberately are not here:
//
//   * `Membership.ascx` is twenty-eight lines and is a read-only PER-ACCOUNT
//     panel, not a tenant policy screen. Its editor carries `editmode="View"`
//     (L5) and its four command buttons - authorise, unauthorise, unlock and
//     force a password change (L14-L27, every one of them declared
//     `causesvalidation="False"`) - act on ONE account. The decisive proof is
//     `Website/admin/Users/manageusers.ascx` L59-L63, which hosts `dnn:user
//     ctlUser` and `dnn:membership ctlMembership` SIDE BY SIDE inside a single
//     `UserRow`: the membership panel is part of the account detail screen. Its
//     fields and actions belong on the account form, and rendering them here
//     would put per-account operations on a tenant-wide settings page.
//   * `MemberServices.ascx` is seventy-seven lines of role subscription: a help
//     paragraph, an invitation-code box with a subscribe command, and a
//     seven-column services grid carrying a trial command. IT IS IMPLEMENTED - by
//     `member-services.component.ts`, the sibling component in this folder, routed
//     at `/users/{userId}/services`.
//
//     ⚠ AN EARLIER REVISION OF THIS NOTE RECORDED IT AS OMITTED, on the grounds
//     that "role subscription, invitation codes, trials and billing transactions
//     have no endpoint in this API at all, so there is nothing for a screen to
//     call". That was an accurate statement about the API as it then stood and is
//     WITHDRAWN: the account resource now publishes five self-service endpoints -
//     the catalogue, subscribe or renew, cancel, trial and invitation-code
//     redemption - and the screen calls them. What remains excluded is the payment
//     processor redirect the legacy screen performed for a fee-bearing role, which
//     AAP 0.2.2.4 places out of scope; that divergence is recorded in
//     `MIGRATION_NOTES.md` and stated on the screen itself.
//
//     It is not rendered on THIS screen because the two differ in whose data they
//     show and in who may see it. This screen configures the TENANT and is reached
//     by a portal administrator; that one shows ONE account's personal
//     subscriptions and is gated on account ownership with no administrator arm,
//     because the legacy panel operated on the signed-in account
//     (`PortalModuleBase.vb` L319-L323) and its container hid the tab from an
//     administrator outright (`ManageUsers.ascx.vb` L61-L66). Rendering them
//     together would put an administrator's own subscriptions on a tenant
//     configuration page and would need one route to satisfy two authorisation
//     policies.
//
// This component therefore derives from `UserSettings.ascx` and the account-policy
// contract ALONE. Its own field set comes from that contract rather than from the
// legacy markup, and the reason is measurable: `UserSettings.ascx` renders all
// three of its sections through the DotNetNuke property and settings editors,
// which are among the excluded control library, so the markup names not one field.
// The file even registers the shared label control on L2 and then never emits a
// single label tag in its seventy-four lines. The wording below is recovered from
// the eighty-four-entry resource file instead, which is the only surviving record
// of what each field was called.
//
// MIGRATION: of the three sections the legacy screen showed, ONE survives.
//
//   1. "Membership Provider Settings" is DROPPED. Its ten provider fields
//      (password format, question-and-answer requirement, minimum length, minimum
//      non-alphanumeric count, strength expression, invalid-attempt ceiling,
//      attempt window, reset enabled, retrieval enabled, unique-address
//      requirement) are absent from the policy contract, and the legacy screen's
//      own help text explains why they could never be edited: it states that the
//      default provider "requires you to edit web.config, so the settings can not
//      be updated here". That file does not exist in the target - provider
//      declarations became typed server-side options - so the legacy sentence is
//      not merely unhelpful, it is false. Host-level administration is also out
//      of scope.
//   2. "Password Aging Settings" is DROPPED, and this is a REVERSE-DRIFT report
//      rather than a preference: the policy contract carries no password-expiry
//      member and no expiry-reminder member, so there is nothing to render. The
//      legacy pair was read-only for anyone but a superuser anyway (its editor was
//      switched to view mode at `UserSettings.ascx.vb` L68-L72) and its data came
//      from a configuration object rather than from tenant data. Consequence worth
//      stating: the two legacy help strings for those fields contain the
//      misspellings "measn" and "recieve", which a faithful port would have had to
//      preserve; because the fields cannot be rendered at all, the question does
//      not arise here. The zero-means-never-expires rule they encoded likewise has
//      no field to attach to.
//   3. "User Accounts Settings" SURVIVES and is the whole of this screen. It was
//      the only section the legacy markup left editable (`editmode="Edit"`,
//      `UserSettings.ascx` L64).
//
// MIGRATION: password RETRIEVAL is not carried forward, and dropping the provider
// section is not what removed it - it was already gone. The legacy installation
// stored credentials reversibly and enabled retrieval, which required a decryption
// key to be committed to the repository in plain sight. Credentials are one-way
// hashed in the target, so a stored password cannot be produced on request by
// anybody, and no screen offers to. Administrative RESET survives; retrieval does
// not. That legacy key is not reproduced anywhere in this file, not as a value and
// not as an illustration in a comment.
//
// MIGRATION: the legacy credential POLICY is preserved and is deliberately NOT
// tightened - as shipped it required seven characters, zero non-alphanumeric ones
// and no unique address. None of it is restated here, because none of it crosses
// the boundary: it is enforced server-side, and a copy in the browser would be free
// to contradict what is actually applied. Tightening it during a migration would
// lock out accounts that are valid today.
//
// MIGRATION: the legacy screen hid TWELVE of these fields when it was reached
// through the host menu rather than a tenant's own (`UserSettings.ascx.vb`
// L87-L100) - the visibility settings, all three redirects, both challenge
// toggles, the address expression, both profile requirements and the
// account-selection control. That rule is NOT reproduced, because the context it
// applied to is out of scope: host-level administration is excluded, this screen is
// reached only as a tenant administrator, and in that context the legacy screen
// showed everything. Nothing is conditionally hidden here.
//
// MIGRATION: two legacy keys in that surviving section have no member on the
// policy contract and are therefore omitted rather than invented -
// `Security_CaptchaLogin` and `Security_CaptchaRegister`, both of which defaulted
// to false. They went with the excluded challenge control. The compensating
// control is server-side and stronger than a challenge image: the API's global
// limiter places every request it classifies as credential-bearing - from the
// `[CredentialEndpoint]` marker on the action, falling back to a whole credential
// path segment on a body-carrying method - into an address-partitioned window of
// thirty requests a minute, and refuses beyond it. No screen participates in
// that, and none should.
//
// MIGRATION: state lives in signals, and the legacy round-trip disappears
// entirely. The legacy screen rebuilt its editors on every postback and carried
// control state in the page's hidden view state; nothing here is posted back,
// nothing is serialised into the document, and the policy is held once in the
// account store.
//
// MIGRATION: no wording on this screen is resolved at run time. The legacy control
// looked every string up through the page's localised resource file - the measured
// in-scope legacy code performs forty-eight such lookups - and none of that is
// reproduced. The strings below are authored directly from the recovered resource
// values, and no translation runtime is installed.
//
// MIGRATION: every legacy string is treated as PLAIN TEXT. Across the in-scope
// legacy resource files, seventy-six values carry a raw markup tag and one carries
// a live script element, so legacy wording is untrusted markup by measurement. The
// legacy code knew it: `Website/admin/Security/AccessDenied.ascx.vb` L43 encoded a
// message before showing it. Nothing here is bound as trusted markup, no sanitiser
// is involved, and no value is handed to a raw-markup binding. Leading break tags
// are stripped by the shared failure utility, upstream of every message this
// screen surfaces, so no local stripping is written - duplicating it would create
// a second cleaner free to disagree with the canonical one.

// ---------------------------------------------------------------------------
// THE LEGACY DISPLAY VOCABULARIES
// ---------------------------------------------------------------------------
//
// MIGRATION: three of the values this screen edits are integers drawn from legacy
// enumerations, and NONE of the three was ported to the server as a named
// enumeration - the contract publishes them as plain integers, so no client
// enumeration exists to import. They are declared here as frozen object literals
// used for PRESENTATION ONLY: the ordinals are the legacy ones, so the numbers
// that reach the server are unchanged, and nothing here validates them. A
// compile-time-inlined enumeration is not an option either way, because this
// workspace compiles with isolated modules, which forbids that form outright.
//
// The ordinals are transcribed from `Library/Components/Users/UserModuleBase.vb`
// L36-L45 and `Library/Components/Users/UserVisibilityMode.vb` L23-L27. They are
// written out explicitly rather than left to declaration order, because they are
// persisted values: a reordering that shifted them would silently repoint every
// tenant's stored setting at a different meaning.

/**
 * How the account listing presents its rows, as the legacy display mode.
 *
 * The server accepts nothing outside zero to two, which is why the option list
 * built from this covers exactly these three.
 */
export const DISPLAY_MODE = Object.freeze({
  /** Every account at once. */
  all: 0,
  /** Grouped behind an initial-letter selector. */
  firstLetter: 1,
  /** Nothing until a search is performed. This is the shipped default. */
  none: 2,
} as const);

/**
 * How widely a profile value may be seen when the account has not chosen.
 *
 * The same ordinals the profile contract publishes, transcribed rather than
 * imported: that contract is not among this file's declared dependencies, and the
 * values are needed here only to label a selector.
 */
export const PROFILE_VISIBILITY_MODE = Object.freeze({
  /** Visible to anyone who can view the profile. */
  allUsers: 0,
  /** Visible to members of the tenant. */
  membersOnly: 1,
  /** Visible to the account itself and to administrators. This is the shipped default. */
  adminOnly: 2,
} as const);

/**
 * Which control the role-management screen uses to choose an account.
 *
 * MIGRATION: the legacy default for this one was DATA-DEPENDENT - it chose the
 * text box over the picker once a tenant held more than a thousand accounts
 * (`Library/Components/Users/UserModuleBase.vb` L179-L183). That threshold is NOT
 * reproduced here, and deliberately not: it requires counting the tenant's
 * accounts, which is a server-side question. The server settled it the same way,
 * defaulting the member to the picker without counting anything.
 */
export const USERS_CONTROL = Object.freeze({
  /** A picker listing the accounts. */
  combo: 0,
  /** A plain text box, for tenants too large to list. */
  textBox: 1,
} as const);

/**
 * One choice in a selector: the integer the server stores and the words shown for it.
 */
export interface MembershipSettingsOption {
  /** The integer the server stores. */
  readonly value: number;

  /** The words shown for it. */
  readonly label: string;
}

// ---------------------------------------------------------------------------
// SELECTOR OPTIONS
// ---------------------------------------------------------------------------
//
// MIGRATION: FOUR of the eight enumeration members these selectors offer have NO
// label in the legacy resource file, and two of the four are the shipped DEFAULTS.
// The file supplies "First Letter", "Combo Box", "Text Box" and "Members Only" and
// stops there: there is no entry for the display mode's `All` member, none for its
// `None` member, none for the visibility `AllUsers` member and none for the
// visibility `AdminOnly` member. A tenant that had never changed either setting was
// therefore looking at a selector whose selected row the resource file could not
// name. Wording is authored below for all four, phrased to describe the behaviour
// rather than to restate the member name, and all four gaps are reported.
//
// MIGRATION: "Combo Box" and "Text Box" are SHARED resource keys. They label the
// account-selection control here, and the legacy file gave them no owner, so the
// same two strings served wherever a picker-or-box choice appeared. "First Letter"
// belongs to the display mode alone.

/** The account-listing display modes, in the order the legacy selector offered them. */
export const DISPLAY_MODE_OPTIONS: readonly MembershipSettingsOption[] = Object.freeze([
  // Authored: the legacy resource file has no entry for this member.
  { value: DISPLAY_MODE.all, label: 'All accounts' },
  { value: DISPLAY_MODE.firstLetter, label: 'First Letter' },
  // Authored: no entry either, and this is the shipped default.
  { value: DISPLAY_MODE.none, label: 'None until searched' },
]);

/** The profile visibility modes, least private first, as the legacy editor ordered them. */
export const PROFILE_VISIBILITY_OPTIONS: readonly MembershipSettingsOption[] = Object.freeze([
  // Authored: the legacy resource file has no entry for this member.
  { value: PROFILE_VISIBILITY_MODE.allUsers, label: 'All Users' },
  { value: PROFILE_VISIBILITY_MODE.membersOnly, label: 'Members Only' },
  // Authored: no entry either, and this is the shipped default.
  { value: PROFILE_VISIBILITY_MODE.adminOnly, label: 'Administrators Only' },
]);

/** The account-selection controls the role-management screen can use. */
export const USERS_CONTROL_OPTIONS: readonly MembershipSettingsOption[] = Object.freeze([
  { value: USERS_CONTROL.combo, label: 'Combo Box' },
  { value: USERS_CONTROL.textBox, label: 'Text Box' },
]);

// ---------------------------------------------------------------------------
// THE MEASURED LEGACY DEFAULTS
// ---------------------------------------------------------------------------

/**
 * The policy a tenant that has never saved one is treated as having.
 *
 * Every value is transcribed from `Library/Components/Users/UserModuleBase.vb`
 * L98-L190, which is the legacy routine that filled in a missing key, and each one
 * was checked against the server contract's own default: the two agree member for
 * member, so this constant restates a value the server already holds rather than
 * inventing one. It is used only to seat the form before the policy arrives, and it
 * is never sent on its own - a save always sends what the form holds after the
 * server's own policy has been applied to it.
 *
 * MIGRATION: several of these defaults are COUNTER-INTUITIVE and none of them is
 * "corrected" here, because preserving behaviour outranks tidiness. The electronic
 * mail column defaults to HIDDEN while the address column defaults to SHOWN; a
 * valid profile is required at sign-in but NOT at registration. A migration that
 * quietly made those consistent would change what every existing tenant sees.
 *
 * MIGRATION: there is no first-class "show the sign-in name" setting and none is
 * invented. The legacy routine defines nine column toggles and the sign-in name is
 * not among them, so that column is always shown and has no switch.
 */
export const LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS: MembershipSettings = Object.freeze({
  // ⚠ #6 — FALSE, AND SAYING SO IS THE WHOLE POINT OF THIS CONSTANT.
  // These values are defaults, not decisions, and the flag is how a screen tells the two
  // apart. The server publishes the same flag on the same member for the same reason, so a
  // document that arrives with `isStored: false` and this constant are the same claim made
  // by two authorities rather than two different things.
  isStored: false,
  columnFirstName: false,
  columnLastName: false,
  columnDisplayName: true,
  columnAddress: true,
  columnTelephone: true,
  // Hidden by default. Measured, not a transcription slip - see the note above.
  columnEmail: false,
  columnCreatedDate: true,
  columnLastLogin: false,
  columnAuthorized: true,
  displayMode: DISPLAY_MODE.none,
  displaySuppressPager: false,
  // The one place a page size may be written down, and it is imported rather than
  // spelled: the shared constant is the single home of the value, and the legacy
  // default it carries is the same ten this routine set.
  recordsPerPage: DEFAULT_PAGE_SIZE,
  profileDefaultVisibility: PROFILE_VISIBILITY_MODE.adminOnly,
  profileDisplayVisibility: true,
  profileManageServices: true,

  // MIGRATION: the legacy routine defaulted all three of these to MINUS ONE, its
  // whole-codebase marker for "no integer". They are null here, and that is a
  // translation of the same state rather than a loss of it: the server models them
  // as nullable page identifiers and REFUSES a negative one outright - its
  // validator requires each to be zero or greater whenever a value is supplied.
  // Sending minus one would be rejected. Nothing in this file compares against it.
  //
  // ZERO IS A REAL PAGE. The page table's identity seeds at zero, which is exactly
  // why the server's floor is zero rather than one, so zero must never be read as
  // "unset". Null is the only expression of "unset" on this contract.
  redirectAfterLogin: null,
  redirectAfterRegistration: null,
  redirectAfterLogout: null,

  // MIGRATION: the legacy default was a constant on the excluded globals module, so
  // it cannot be transcribed and is deliberately not guessed at. Empty here means
  // "the server has not told us yet"; the server supplies its own expression with
  // the policy, and this seat is overwritten the moment it arrives.
  securityEmailValidation: '',

  securityRequireValidProfile: false,
  // True while the registration counterpart above is false. Measured, not a slip.
  securityRequireValidProfileAtLogin: true,
  // The picker, matching the server. The legacy thousand-account rule is not here.
  securityUsersControl: USERS_CONTROL.combo,
  securityDisplayNameFormat: '',
});

// ---------------------------------------------------------------------------
// RECOVERED WORDING
// ---------------------------------------------------------------------------
//
// Every string below is the VALUE from `Website/admin/Users/App_LocalResources/
// UserSettings.ascx.resx`, taken verbatim. The resource value is the authority and
// the markup attribute is not, which this very screen proves twice over: the
// section headings written into `UserSettings.ascx` read "Provider Settings" (L10)
// and "Password Settings" (L33), while the resource values the page actually
// rendered read "Membership Provider Settings" and "Password Aging Settings". A
// port that trusted the markup would have shipped two wrong headings.
//
// MIGRATION: three legacy spellings are PRESERVED verbatim below and must not be
// tidied. The sign-in profile requirement reads "before be logged in", which is
// ungrammatical and is the shipped wording. "Suppress Pager?" keeps its question
// mark. The heading "User Accounts Settings" keeps its plural. Correcting any of
// them would change text an existing administrator recognises, for no behavioural
// gain.
//
// MIGRATION: the two button labels are NOT authored. The screen's own resource file
// has no entry for either, because the legacy buttons resolved theirs from the
// shared global resource file, and that file supplies "Update" and "Cancel". They
// are taken from there rather than invented.

/** The headings, help text and button labels this screen renders as chrome. */
export const MEMBERSHIP_SETTINGS_TEXT = Object.freeze({
  /** The screen title. Recovered from the control-title resource entry. */
  title: 'User Settings',

  /**
   * The subtitle.
   *
   * AUTHORED, and reported as such: the legacy screen had no subtitle, because the
   * DotNetNuke page chrome gave it none to render. It names the scope of what is
   * being edited, which matters on a screen whose settings are tenant-wide rather
   * than per-account.
   */
  subtitle: 'Account administration settings for this site',

  /** The one surviving section heading. */
  sectionHeading: 'User Accounts Settings',

  /** The heading for the nine listing-column switches. */
  columnsHeading: 'Account Listing Columns',

  /** The submit button. From the shared global resource file. */
  submitLabel: 'Update',

  /** The cancel button. From the shared global resource file. */
  cancelLabel: 'Cancel',

  /** Announced while the policy is being read. */
  loadingLabel: 'Loading user settings…',

  /** Announced while the policy is being written. */
  savingLabel: 'Saving user settings…',

  /**
   * Shown once the policy has been written.
   *
   * AUTHORED. The legacy screen showed nothing at all on success - it redirected
   * straight back to the account listing - so there is no legacy wording to
   * recover. It is surfaced at the success severity, which is the target equivalent
   * of the legacy green confirmation style.
   */
  savedMessage: 'User settings saved.',

  /**
   * Shown once the policy has been written AND the new display-name format has been applied
   * across the tenant.
   *
   * AUTHORED, because the legacy screen reported nothing here either — and in this case the
   * silence was the defect. `Website/admin/Users/UserSettings.ascx.vb:L175-L182` spawned
   * `UserController.UpdateDisplayNames` on a background thread and redirected immediately, so
   * an operator who changed the format saw the same blank confirmation whether the sweep
   * rewrote nothing, rewrote the whole tenant, or died halfway through. The count is now part
   * of the write's answer, and this is where it is said.
   *
   * `{count}` is substituted with the number of accounts rewritten, and `{accounts}` with the
   * singular or plural noun, so the sentence reads naturally for one account as well as for
   * many. See {@link membershipSettingsSavedMessage}.
   */
  savedWithRewriteMessage: 'User settings saved. {count} {accounts} renamed to match the new display name format.',

  /**
   * Shown when the display-name format changed but no account's name changed with it.
   *
   * ⚠ DISTINCT FROM {@link savedMessage}, AND THE DISTINCTION IS THE POINT. "The sweep ran and
   * found nothing to alter" is a different answer from "no sweep ran", and an operator who has
   * just changed the format is looking for exactly that difference — being shown the plain
   * confirmation would leave them unable to tell whether the change took effect at all.
   */
  savedWithNoRewriteMessage:
    'User settings saved. No account names needed to change under the new display name format.',

  /**
   * Shown while this tenant has NO STORED POLICY OF ITS OWN and the form is therefore seated from
   * the platform defaults.
   *
   * ⚠ #6 — THIS SENTENCE IS THE WHOLE FIX, AND WITHOUT IT THE SCREEN LIES BY OMISSION. Every
   * control below renders a value whether the tenant stored one or not, and a rendered value looks
   * identical either way: an operator reading `Records Per Page 10` cannot tell whether somebody
   * chose ten or whether ten is simply what this platform falls back to. The distinction is not
   * cosmetic — it decides whether pressing Update CHANGES a policy or CREATES one, and it decides
   * whether a value an operator disagrees with was somebody's decision or nobody's.
   *
   * AUTHORED, and reported as such. There is no legacy wording to recover because the legacy
   * screen could not reach this state and therefore had nothing to say about it:
   * `Website/admin/Users/UserSettings.ascx.vb` read every value through
   * `UserModuleBase.GetSetting(PortalId, key)` (L98-L190), which returned the hard-coded default
   * for an absent key WITHOUT reporting that it had done so, so the legacy screen was
   * structurally incapable of distinguishing the two states. That silence is the defect this
   * sentence closes, so it is a documented net addition rather than a port.
   */
  defaultsNotice:
    'This site has no stored user settings, so the values below are this platform\u2019s defaults. Press Update to store them for this site.',

  /**
   * Shown while the tenant DOES hold a stored policy, so the values are somebody's decision.
   *
   * The counterpart of {@link defaultsNotice} and rendered on the same terms. Stating only the
   * defaults case would leave the stored case reading as the absence of a notice, which is exactly
   * the ambiguity being closed — a reader cannot distinguish "no notice because a policy is
   * stored" from "no notice because this screen does not say".
   */
  storedNotice: 'These user settings are stored for this site.',
} as const);

/**
 * Composes the confirmation for a written policy from what the write reported.
 *
 * Three sentences rather than one, because the write has three genuinely different outcomes and
 * collapsing them would withhold from the operator the one fact they cannot obtain any other
 * way: whether adopting a new display-name format actually renamed anything.
 *
 * Exported so a specification can assert the wording without reaching into the component, and
 * so the substitution is verified in one place rather than at each call site.
 *
 * @param report What the write did beyond storing its values, or `null` when the server sent no
 * report at all — in which case the plain confirmation is the honest answer, because nothing is
 * known about a sweep.
 * @returns The sentence to show at the success severity.
 */
export function membershipSettingsSavedMessage(
  report: MembershipSettingsUpdateResult | null,
): string {
  if (report === null || !report.displayNameFormatChanged) {
    return MEMBERSHIP_SETTINGS_TEXT.savedMessage;
  }

  if (report.displayNamesRewritten === 0) {
    return MEMBERSHIP_SETTINGS_TEXT.savedWithNoRewriteMessage;
  }

  // ⚠ COMPARED AGAINST ONE RATHER THAN TESTED FOR TRUTHINESS. The zero case is handled above
  // and is a real answer, so the only question left here is singular against plural.
  return MEMBERSHIP_SETTINGS_TEXT.savedWithRewriteMessage
    .replace('{count}', String(report.displayNamesRewritten))
    .replace('{accounts}', report.displayNamesRewritten === 1 ? 'account was' : 'accounts were');
}

/**
 * The label and help text for one field.
 *
 * `help` is deliberately allowed to be empty, and empty is meaningful: the shared
 * field component treats a blank help string as "no help" and renders no
 * disclosure for it, so a uniform binding in the template still produces a field
 * with nothing but a label.
 */
export interface MembershipSettingsFieldText {
  /** The field label, colon and question mark exactly as the legacy file wrote them. */
  readonly label: string;

  /** The help text, or empty where the legacy file offered nothing worth showing. */
  readonly help: string;
}

/**
 * The recovered wording for all twenty-three editable fields, keyed by control name.
 *
 * MIGRATION: the nine listing-column switches carry NO help text, and the omission
 * is a measured reduction rather than an oversight. Each of their legacy help
 * values is its own label with the trailing colon removed - "Show Address Column"
 * against the label "Show Address Column:" - and that holds for all nine without
 * exception. Rendering a disclosure whose contents repeat the label it sits beside
 * adds a control to operate and tells a reader nothing, so the nine are labelled
 * only. No information is lost, because none was ever carried.
 */
export const MEMBERSHIP_SETTINGS_FIELD_TEXT: Readonly<
  Record<MembershipSettingsFieldName, MembershipSettingsFieldText>
> = Object.freeze({
  columnFirstName: { label: 'Show First Name Column:', help: '' },
  columnLastName: { label: 'Show Last Name Column:', help: '' },
  // Labelled "Name", not "Display Name", in the legacy file. Preserved.
  columnDisplayName: { label: 'Show Name Column:', help: '' },
  columnAddress: { label: 'Show Address Column:', help: '' },
  columnTelephone: { label: 'Show Telephone Column:', help: '' },
  columnEmail: { label: 'Show Email Column:', help: '' },
  columnCreatedDate: { label: 'Show Created Date Column:', help: '' },
  columnLastLogin: { label: 'Show Last Login Column:', help: '' },
  columnAuthorized: { label: 'Show Authorized Column:', help: '' },

  displayMode: {
    // No trailing colon in the legacy file. Preserved.
    label: 'Default Display Mode',
    help: 'Select the default display mode for the Users Grid',
  },
  displaySuppressPager: {
    // The question mark is the legacy wording. Preserved.
    label: 'Suppress Pager?',
    help: 'Check to hide the pager if only one page of records',
  },
  recordsPerPage: {
    label: 'Users per Page:',
    help: 'Enter the number of users to display on a page',
  },

  profileDefaultVisibility: {
    // No trailing colon in the legacy file. Preserved.
    label: 'Default Profile Visibility Mode',
    help: 'Select the default Profile Visibility Mode for the Users Profile',
  },
  profileDisplayVisibility: {
    label: 'Display Profile Visibility',
    help: 'Check to display the Profile Visibility control in the Users profile',
  },
  profileManageServices: {
    label: 'Display Manage Services',
    help: 'Check to display the Manage Services section in the Users profile',
  },

  redirectAfterLogin: {
    label: 'Redirect After Login:',
    help: 'You can select a page to redirect to after successful login',
  },
  redirectAfterRegistration: {
    label: 'Redirect After Registration:',
    help: 'You can select a page to redirect the user to, on successful registration.',
  },
  redirectAfterLogout: {
    label: 'Redirect After Logout:',
    help: 'You can select a page to redirect the user to, on logout.',
  },

  securityEmailValidation: {
    label: 'Email Address Validation:',
    help:
      'You can modify the provided Email Validation Expression, which is used to ' +
      'check the validity of email addresses provided.',
  },
  securityRequireValidProfile: {
    label: 'Require a valid Profile for Registration:',
    help: 'You can optionally require that a new user enters a valid profile during registration.',
  },
  securityRequireValidProfileAtLogin: {
    label: 'Require a valid Profile for Login:',
    // "before be logged in" is the legacy grammar. Preserved verbatim.
    help:
      'You can optionally require a user to update their Profile before be logged ' +
      'in, if their Profile is no longer Valid.',
  },
  securityUsersControl: {
    // No trailing colon in the legacy file. Preserved.
    label: 'Users display mode in Manage Roles',
    help: 'Select the Users Control to use in the Manage Roles module control',
  },
  securityDisplayNameFormat: {
    label: 'Display Name Format:',
    // MIGRATION: the legacy help text names bracketed substitution tokens, and they
    // are described here rather than made to work: token replacement is an excluded
    // subsystem, so the value is stored and forwarded as opaque text and this screen
    // expands nothing. The final legacy sentence is a genuine cross-screen constraint -
    // a tenant that sets a format makes the display name uneditable on the account
    // form - and it is reported as affecting that screen.
    //
    // ⚠ THE LAST SENTENCE IS AUTHORED AND IS NOT IN THE RESOURCE FILE. The legacy screen
    // never warned that saving a changed format renames every account in the tenant, even
    // though that is exactly what it did (L175-L182, on a background thread, silently).
    // The warning is stated BEFORE the value is edited, because afterwards is too late:
    // the sweep is transactional with the policy write and there is no undo.
    help:
      'You can optionally specify a format for the users display name. The format ' +
      'can include tokens for dynamic substitution such as [FIRSTNAME] [LASTNAME]. ' +
      'If a display name format is specified, the display name will no longer be ' +
      'editable through the user interface. Changing this value renames every ' +
      'account in this site to match it.',
  },
} as const);

// ---------------------------------------------------------------------------
// THE FORM
// ---------------------------------------------------------------------------

/**
 * The form behind this screen, one control per member of the account policy.
 *
 * MIGRATION: this interface is what REPLACES the legacy contract, and the
 * replacement is the point of the exercise. The legacy screen read its settings out
 * of an untyped hash table (`Library/Components/Users/UserController.vb` L656) whose
 * keys were bare strings and whose values were bare objects, so every read was a
 * guess checked at run time and a misspelled key produced nothing rather than an
 * error. Every control below is named and typed, so a misspelling is a compilation
 * failure. It mirrors the policy contract member for member - no wider, no
 * narrower, and never an open bag of unknown values.
 *
 * MIGRATION: no control carries a legacy status flag or a by-reference outcome
 * argument. The legacy save reported what happened by mutating arguments the caller
 * had passed in; a failure here arrives as a response status and a machine-readable
 * code, which the store records and the shared banner presents.
 *
 * EVERY control is constructed non-nullable, which is what makes the raw value
 * fully typed instead of partial and what makes a reset return to the value the
 * control started with rather than to nothing. Two groups nevertheless admit null
 * INSIDE their value type, and both are deliberate:
 *
 *   * the three redirect controls, because the contract itself declares a nullable
 *     page identifier and null is the only way it expresses "no redirect"; and
 *   * the page-size control, because a number input that has been cleared writes
 *     null through its value accessor whatever the declared type says. Declaring
 *     that honestly forces the coercion on submission to be written rather than
 *     assumed, which is why the type says so.
 */
export interface MembershipSettingsFormModel {
  readonly columnFirstName: FormControl<boolean>;
  readonly columnLastName: FormControl<boolean>;
  readonly columnDisplayName: FormControl<boolean>;
  readonly columnAddress: FormControl<boolean>;
  readonly columnTelephone: FormControl<boolean>;
  readonly columnEmail: FormControl<boolean>;
  readonly columnCreatedDate: FormControl<boolean>;
  readonly columnLastLogin: FormControl<boolean>;
  readonly columnAuthorized: FormControl<boolean>;
  readonly displayMode: FormControl<number>;
  readonly displaySuppressPager: FormControl<boolean>;
  readonly recordsPerPage: FormControl<number | null>;
  readonly profileDefaultVisibility: FormControl<number>;
  readonly profileDisplayVisibility: FormControl<boolean>;
  readonly profileManageServices: FormControl<boolean>;
  readonly redirectAfterLogin: FormControl<number | null>;
  readonly redirectAfterRegistration: FormControl<number | null>;
  readonly redirectAfterLogout: FormControl<number | null>;
  readonly securityEmailValidation: FormControl<string>;
  readonly securityRequireValidProfile: FormControl<boolean>;
  readonly securityRequireValidProfileAtLogin: FormControl<boolean>;
  readonly securityUsersControl: FormControl<number>;
  readonly securityDisplayNameFormat: FormControl<string>;
}

/** The name of one control on this screen's form. */
export type MembershipSettingsFieldName = keyof MembershipSettingsFormModel;

/** The name of one of the nine listing-column switches. */
export type ColumnToggleFieldName = Extract<
  MembershipSettingsFieldName,
  | 'columnFirstName'
  | 'columnLastName'
  | 'columnDisplayName'
  | 'columnAddress'
  | 'columnTelephone'
  | 'columnEmail'
  | 'columnCreatedDate'
  | 'columnLastLogin'
  | 'columnAuthorized'
>;

/**
 * The nine listing-column switches, in the order the legacy routine declared them.
 *
 * Published as a list so the template renders them in one pass instead of repeating
 * a near-identical block nine times, and ordered as
 * `Library/Components/Users/UserModuleBase.vb` L98-L124 ordered them so that an
 * administrator finds the switches where they have always been.
 *
 * These nine are what make the account listing's columns tenant-configurable, so
 * they are the group with the most reach beyond this screen: the listing reads the
 * same policy.
 */
export const COLUMN_TOGGLE_FIELDS: readonly ColumnToggleFieldName[] = Object.freeze([
  'columnFirstName',
  'columnLastName',
  'columnDisplayName',
  'columnAddress',
  'columnTelephone',
  'columnEmail',
  'columnCreatedDate',
  'columnLastLogin',
  'columnAuthorized',
] as const);

/**
 * The fields whose value is a page identifier rather than a plain number.
 *
 * Grouped so the template can render the three redirect pickers together, which is
 * how the legacy screen presented them: it attached a page picker to each of the
 * three (`Website/admin/Users/UserSettings.ascx.vb` L80-L82).
 *
 * MIGRATION: a page PICKER is not offered here and a numeric page identifier is
 * taken instead. The reason is a dependency boundary rather than a preference - the
 * page lookup this screen would need is not among its declared dependencies, so
 * there is no list to populate a picker from without reaching outside them. The
 * value is the same page identifier either way, an empty field means no redirect,
 * and the server rejects a negative one. This is reported as a deliberate
 * reduction.
 */
export const REDIRECT_FIELDS: readonly MembershipSettingsFieldName[] = Object.freeze([
  'redirectAfterLogin',
  'redirectAfterRegistration',
  'redirectAfterLogout',
] as const);

/**
 * The form's value with every member present, as the group itself types it.
 *
 * Derived from the group rather than restated, so a control added to or removed from
 * the model above cannot leave a stale duplicate of its shape compiling here.
 */
export type MembershipSettingsFormValue = ReturnType<
  FormGroup<MembershipSettingsFormModel>['getRawValue']
>;

// ---------------------------------------------------------------------------
// BOUNDS AND DESTINATIONS
// ---------------------------------------------------------------------------
//
// The three bounds below MIRROR the server's own validator and are not invented
// here. The server requires a page size between one and one hundred, requires each
// redirect identifier to be zero or greater when one is supplied, and caps a stored
// setting's text at two thousand characters. Applying them in the browser turns a
// round trip into an immediate answer; the server remains the authority, and its
// message is what is shown once it has spoken.
//
// The upper page-size bound is IMPORTED rather than written, because the shared
// constant is where it is defined. The other two are declared here because no
// shared constant carries them.

/** The smallest page size the server accepts. */
const MINIMUM_RECORDS_PER_PAGE = 1;

/** The smallest page identifier that can exist. The page table's identity seeds at zero. */
const MINIMUM_PAGE_IDENTIFIER = 0;

/** The longest a stored setting's text may be. */
const MAXIMUM_SETTING_LENGTH = 2000;

/**
 * Where both buttons go.
 *
 * MIGRATION: the legacy screen redirected on BOTH outcomes - the cancel handler at
 * `Website/admin/Users/UserSettings.ascx.vb` L140-L151 and the update handler at
 * L163-L192 both ended in a redirect back to the account listing - so leaving the
 * reader on this screen after either would be the divergence, not the navigation.
 *
 * MIGRATION: the legacy redirects carried the listing's search text forward through
 * the query string, and that is NOT reproduced. It cannot be: the legacy handler
 * read it from the request that had just posted back, and there is no postback here
 * to read. The listing holds its own search in the account store and still has it.
 * Worth recording as the reason the original code is unsafe to transliterate: L141
 * spells the parameter in lower case while L143 and L147 spell it in mixed case, and
 * only the case-insensitivity of the legacy language made all three the same key.
 *
 * A plain path, not an import from a sibling feature: one screen must never reach
 * into another's folder.
 */
const ACCOUNT_LISTING_PATH = '/users';

/**
 * The companion screen for profile property declarations.
 *
 * The legacy control offered this as a module action alongside its own; it is a link
 * in the header action slot here. A path rather than an import, for the same reason.
 */
const PROFILE_DEFINITIONS_PATH = '/settings/profile-definitions';

/** Shown when a required field has been left empty. */
const REQUIRED_MESSAGE = 'This setting is required.';

/**
 * The tenant's account-administration settings screen.
 *
 * MIGRATION: the class name and this file's location are a RUNTIME contract, not a
 * convention. The route table reaches this screen through a dynamic import naming
 * both, and neither the compiler nor the bundler can report a mismatch as anything
 * other than a failed navigation at run time. Renaming either breaks the screen
 * silently.
 *
 * MIGRATION: no route input is declared, and the omission is deliberate. The router
 * is configured to bind route parameters AND route data onto same-named component
 * inputs, so an input named after a key this route supplies would be written by the
 * router without anything in this file asking for it. There is nothing to bind in
 * any case: this policy is tenant-wide, the tenant is resolved by the server from
 * the request rather than named in the address, and no account is in play.
 */
@Component({
  selector: 'app-membership-settings',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    ErrorBannerComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    PageHeaderComponent,
    FocusFirstInvalidDirective,
  ],
  templateUrl: './membership-settings.component.html',
  styleUrl: './membership-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MembershipSettingsComponent implements OnInit {

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
    () => this.form.dirty && this.saving() === false,
  );
  /**
   * The account store, injected rather than the transport service.
   *
   * MIGRATION: the store, not the service, is the correct collaborator here, and the
   * reason is that the page size this screen edits is read by the account listing
   * too. The store already holds the policy and already prefers its page-size member
   * over the shared fallback when it fetches a page. Reading the policy through the
   * service instead would put a second copy of that member on this screen, free to
   * disagree with the one the listing uses. Composition of that kind belongs in the
   * store rather than in a service or a screen, which is also why the store's own
   * save re-reads the policy and the listing afterwards instead of leaving a page
   * fetched at the previous size on display.
   */
  private readonly store = inject(UserStore);

  /** The confirmation channel. Used on success only; failures are shown in place. */
  private readonly notifications = inject(NotificationService);

  /** Used to leave the screen, which both buttons do. */
  private readonly router = inject(Router);

  /** Chrome wording, for the template. */
  protected readonly text = MEMBERSHIP_SETTINGS_TEXT;

  /** Per-field wording, for the template. */
  protected readonly fieldText = MEMBERSHIP_SETTINGS_FIELD_TEXT;

  /** The nine listing-column switches, in legacy order. */
  protected readonly columnFields = COLUMN_TOGGLE_FIELDS;

  /** The three page-identifier fields, grouped as the legacy screen grouped them. */
  protected readonly redirectFields = REDIRECT_FIELDS;

  /** The display-mode choices. */
  protected readonly displayModeOptions = DISPLAY_MODE_OPTIONS;

  /** The profile-visibility choices. */
  protected readonly profileVisibilityOptions = PROFILE_VISIBILITY_OPTIONS;

  /** The account-selection-control choices. */
  protected readonly usersControlOptions = USERS_CONTROL_OPTIONS;

  /** The page-size floor, so the template's own input can advertise it. */
  protected readonly minimumRecordsPerPage = MINIMUM_RECORDS_PER_PAGE;

  /** The page-size ceiling, from the shared constant. */
  protected readonly maximumRecordsPerPage = MAX_PAGE_SIZE;

  /** The page-identifier floor. Zero, because zero is a real page. */
  protected readonly minimumPageIdentifier = MINIMUM_PAGE_IDENTIFIER;

  /** The stored-text ceiling. */
  protected readonly maximumSettingLength = MAXIMUM_SETTING_LENGTH;

  /** Where the cancel button and the post-save navigation go. */
  protected readonly accountListingPath = ACCOUNT_LISTING_PATH;

  /** Where the companion action link goes. */
  protected readonly profileDefinitionsPath = PROFILE_DEFINITIONS_PATH;

  /**
   * The form, seated with the measured legacy defaults and replaced when the policy
   * arrives.
   *
   * Seating it rather than leaving it empty is what makes the screen render a
   * coherent set of controls on first paint instead of a grid of blanks, and the
   * values are the ones the legacy routine would have filled in for a tenant that
   * had saved nothing.
   */
  protected readonly form = new FormGroup<MembershipSettingsFormModel>({
    columnFirstName: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnFirstName, {
      nonNullable: true,
    }),
    columnLastName: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastName, {
      nonNullable: true,
    }),
    columnDisplayName: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnDisplayName, {
      nonNullable: true,
    }),
    columnAddress: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAddress, {
      nonNullable: true,
    }),
    columnTelephone: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnTelephone, {
      nonNullable: true,
    }),
    columnEmail: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnEmail, {
      nonNullable: true,
    }),
    columnCreatedDate: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnCreatedDate, {
      nonNullable: true,
    }),
    columnLastLogin: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastLogin, {
      nonNullable: true,
    }),
    columnAuthorized: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAuthorized, {
      nonNullable: true,
    }),

    displayMode: new FormControl(LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displayMode, {
      nonNullable: true,
    }),
    displaySuppressPager: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displaySuppressPager,
      { nonNullable: true },
    ),

    // Required, and bounded exactly as the server bounds it. The required rule does
    // NOT reject zero - the framework's own emptiness test is a null-and-length test
    // rather than a truthiness test - so zero reaches the range rule, which is the
    // rule that should refuse it and the rule the server applies.
    recordsPerPage: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.recordsPerPage,
      {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.min(MINIMUM_RECORDS_PER_PAGE),
          Validators.max(MAX_PAGE_SIZE),
        ],
      },
    ),

    profileDefaultVisibility: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDefaultVisibility,
      { nonNullable: true },
    ),
    profileDisplayVisibility: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDisplayVisibility,
      { nonNullable: true },
    ),
    profileManageServices: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileManageServices,
      { nonNullable: true },
    ),

    // Not required: empty means no redirect, which is a legitimate policy and the
    // shipped one. The floor rule mirrors the server, whose own rule applies only
    // when a value is supplied - and the framework's range validators skip an empty
    // value, so the two agree without a condition being written.
    redirectAfterLogin: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.redirectAfterLogin,
      { nonNullable: true, validators: [Validators.min(MINIMUM_PAGE_IDENTIFIER)] },
    ),
    redirectAfterRegistration: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.redirectAfterRegistration,
      { nonNullable: true, validators: [Validators.min(MINIMUM_PAGE_IDENTIFIER)] },
    ),
    redirectAfterLogout: new FormControl<number | null>(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.redirectAfterLogout,
      { nonNullable: true, validators: [Validators.min(MINIMUM_PAGE_IDENTIFIER)] },
    ),

    // MIGRATION: length is the ONLY rule applied to this expression here. Whether it
    // is a usable pattern is checked by the server, which compiles it, and the value
    // is treated as opaque text in the browser: nothing in this file compiles it,
    // executes it or matches anything against it. Evaluating a tenant-supplied
    // pattern client-side would hand a stored setting the ability to hang the page.
    securityEmailValidation: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityEmailValidation,
      { nonNullable: true, validators: [Validators.maxLength(MAXIMUM_SETTING_LENGTH)] },
    ),

    securityRequireValidProfile: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfile,
      { nonNullable: true },
    ),
    securityRequireValidProfileAtLogin: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfileAtLogin,
      { nonNullable: true },
    ),
    securityUsersControl: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityUsersControl,
      { nonNullable: true },
    ),

    // MIGRATION: opaque text here too. The bracketed tokens the help text describes
    // are expanded by an excluded subsystem, so this screen stores the template and
    // substitutes nothing.
    securityDisplayNameFormat: new FormControl(
      LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityDisplayNameFormat,
      { nonNullable: true, validators: [Validators.maxLength(MAXIMUM_SETTING_LENGTH)] },
    ),
  });

  // -------------------------------------------------------------------------
  // PROJECTED STATE
  // -------------------------------------------------------------------------
  //
  // Every slice below is READ-ONLY and derived from the store. This screen owns no
  // writable signal at all, which is the strongest form of the rule that a writable
  // one is never exposed: there is none to expose. The two plain fields further down
  // are guards for the effects rather than state, and nothing renders them.

  /** Whether the policy is being read. */
  protected readonly loading: Signal<boolean> = this.store.membershipSettingsLoading;

  /** Whether the policy is being written. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * Which of the two provenances the values on screen have, or `null` while that is not yet known.
   *
   * ⚠ #6 — THE ONE FACT THAT MAKES THE VALUES ON THIS SCREEN READABLE. `'stored'` means the tenant
   * holds a policy row of its own and every value below is somebody's decision. `'defaults'` means
   * the tenant holds none and every value below is what this platform falls back to, in which case
   * pressing Update CREATES the policy rather than changing it. The two are visually identical
   * without this, because a control renders its value the same way whichever it is.
   *
   * `null` covers the read still being in flight and the read having failed. Neither is a
   * provenance, and asserting one would be a guess: the disclosure is simply withheld, which is
   * correct in both cases — while loading the form is not on screen at all, and after a failed
   * read the banner is the thing that has something to say.
   *
   * Derived from the contract's own `isStored` member rather than inferred by comparing the values
   * against the defaults. Inference would be wrong in the one case that matters most: a tenant
   * that deliberately stored the default value for every key is STORED, and comparison would
   * report it as unset.
   */
  protected readonly valueProvenance: Signal<'stored' | 'defaults' | null> = computed(() => {
    const settings = this.store.membershipSettings();

    if (settings === null) {
      return null;
    }

    return settings.isStored ? 'stored' : 'defaults';
  });

  /**
   * The sentence disclosing that provenance, or `null` when there is none to disclose.
   *
   * Composed here rather than in the template so that the wording, like every other string on
   * this screen, has exactly one home.
   */
  protected readonly provenanceNotice: Signal<string | null> = computed(() => {
    switch (this.valueProvenance()) {
      case 'stored':
        return MEMBERSHIP_SETTINGS_TEXT.storedNotice;
      case 'defaults':
        return MEMBERSHIP_SETTINGS_TEXT.defaultsNotice;
      default:
        return null;
    }
  });

  /**
   * The failure to show, or null.
   *
   * MIGRATION: severity is NOT decided here. A permission refusal must read as a
   * warning rather than an error - the legacy denial page rendered at its yellow
   * warning style in both of its branches - and the shared banner already resolves a
   * refusal to a warning and a rate-limit refusal to its calmest band. Passing the
   * document and letting it decide is what keeps one authority over that rule
   * instead of two that can drift.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    return failure === null ? null : failure.problem;
  });

  /**
   * The failure summarised, for the case where there is no document to hand the
   * banner.
   *
   * A transport failure that never reached the API carries no problem document, so
   * the banner would render nothing and the screen would look as though the save had
   * simply been ignored. Null whenever the banner has something to show, so the two
   * never appear together.
   */
  protected readonly failureMessage: Signal<string | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null) {
      return null;
    }

    return failure.problem === null ? failure.summary.message : null;
  });

  /**
   * Whether the form may be submitted.
   *
   * Refused while the policy is being read or written, and the refusal is a
   * correctness measure rather than a courtesy: until the server's policy has been
   * applied, the form still holds the seated defaults, and submitting those would
   * overwrite a live policy with values nobody chose. That matters most for the
   * electronic-mail expression, whose real default comes from the server and is
   * seated empty here.
   *
   * Refused AS WELL once a read has failed, which is the other half of the same rule.
   * Testing only the in-flight flag would satisfy the sentence above for exactly as
   * long as the request lasted and then stop: a refused read clears the flag without
   * ever applying a policy, leaving the seated defaults on screen and submittable. A
   * tenant whose policy could not be read therefore cannot overwrite it, and the
   * banner says why - the very failure that closes the command is the one it is
   * showing.
   *
   * A refused SAVE deliberately does not close the command. That failure leaves a
   * policy that was read successfully and an entry the operator can correct, so the
   * form must stay submittable; only a failure of the read itself withholds it. The
   * store clears the slot at the start of every read, so a retry reopens the command
   * without anything here resetting it.
   */
  protected readonly canSubmit: Signal<boolean> = computed(() => {
    if (this.loading() || this.saving()) {
      return false;
    }

    // ⚠ WITHHELD FOR A SECOND, DIFFERENT REASON: this tenant has nowhere to store a policy.
    // The reasoning above is about not overwriting a live policy with seated defaults, and it does
    // not apply here because there IS no stored policy to overwrite. What applies instead is that
    // the WRITE cannot succeed - measured against the running API, `PUT /api/v1/users/settings`
    // answers `404 user.membership_settings.source_missing`, "Portal -1 has no \"User Accounts\"
    // module instance to store membership settings against." Opening the command would therefore
    // invite the operator to fill in twenty-three settings and then fail at the last step.
    //
    // ⚠ AND THIS IS WHY THE REFUSAL IS NOW EXPLAINED. Before the store learned to tell absence
    // apart from failure, this screen refused for the branch below AND showed the read's problem
    // document, so the operator at least saw something - the wrong thing, "The requested resource
    // does not exist", but something. That failure is no longer recorded, so a bare refusal here
    // would leave a form full of controls and a dead button with nothing saying why. The template
    // renders {@link unconfigured} as an in-page explanation for exactly that reason; the two
    // changes are one change and must not be separated.
    if (this.unconfigured()) {
      return false;
    }

    const failure = this.store.failure();

    return failure === null || failure.operation !== 'loadMembershipSettings';
  });

  /**
   * Whether this tenant stores no account policy at all, so none can be read or written.
   *
   * Distinct from a read that failed: a failure is transient and retryable and keeps its banner,
   * whereas this state is stable and has nothing to retry. The store publishes the distinction; this
   * screen only presents it.
   */
  protected readonly unconfigured: Signal<boolean> = computed(() =>
    this.store.membershipSettingsUnconfigured(),
  );

  /**
   * The submission awaiting an outcome.
   *
   * A plain field rather than a signal, deliberately: it is written from inside an
   * effect, and writing a signal there would make the effect depend on its own
   * output. Nothing renders it.
   */
  private pendingSubmit = false;

  /**
   * The policy already applied to the form, held by identity.
   *
   * The guard that makes seating the form IDEMPOTENT. The store publishes a new
   * object on every read, so comparing identity is what distinguishes a genuinely
   * new policy from a redraw provoked by something else.
   */
  private appliedSettings: MembershipSettings | null = null;

  constructor() {
    // Seat the form from the policy, once per policy.
    effect(() => {
      const settings = this.store.membershipSettings();

      if (settings === this.appliedSettings) {
        // Nothing has changed. Acting here would fight the person typing.
        return;
      }

      this.appliedSettings = settings;

      if (settings === null) {
        // The tenant has no policy recorded. The seated legacy defaults stand, which
        // is exactly what the legacy routine did for a missing key, so there is
        // nothing to apply.
        return;
      }

      if (this.form.dirty) {
        // Edits in hand outrank a late arrival. Overwriting them would discard work
        // the person can see, and the policy is applied on the next clean load.
        return;
      }

      this.form.setValue(this.toFormValue(settings));
    });

    // Settle a submission exactly once, on the transition out of saving.
    effect(() => {
      // Both slices are read unconditionally so that this effect depends on both
      // however the guards below fall.
      const saving = this.store.saving();
      const failure = this.store.failure();

      if (this.pendingSubmit === false) {
        return;
      }

      if (saving) {
        // Still in flight.
        return;
      }

      // Settled. Cleared before acting, so a redraw cannot settle it twice.
      this.pendingSubmit = false;

      if (failure === null) {
        // ⚠ THE FORM IS SETTLED BEFORE LEAVING, OR THE UNSAVED-ENTRY GUARD ASKS ABOUT WORK THAT IS
        // ALREADY SAVED. This effect fires on the transition OUT of saving, so `store.saving()` is
        // already false by the time these lines run, while the controls are still dirty from the
        // operator's typing - and the probe registered at the top of this class reads exactly
        // `dirty && saving() === false`. The navigation below is the one the save itself triggers.
        this.form.markAsPristine();
        this.form.markAsUntouched();

        // ⚠ THE REPORT IS READ HERE RATHER THAN RENDERED ON THIS SCREEN, because this screen
        // is about to leave. The legacy handler redirected to the account listing on success
        // (`UserSettings.ascx.vb` L184-L187) and that navigation is reproduced below, so a
        // panel raised here would be destroyed before it could be read.
        //
        // ⚠ AND A NOTIFICATION DOES NOT OUTLIVE A ROUTE CHANGE BY ITSELF - THIS COMMENT USED TO
        // CLAIM IT DID. The shell retires transient notifications on every completed navigation, so
        // raising this and navigating in the same task queued it and swept it before it could be
        // painted: the count this screen exists to report reached nobody. It is the exemption on the
        // next line that makes the claim true, and without it the reasoning above collapses.
        this.notifications.success(
          membershipSettingsSavedMessage(this.store.lastSettingsWrite()),
          true,
        );
        this.notifications.retainAcrossNavigation();

        // Discarded once reported, so returning to this screen does not re-announce a sweep
        // that happened during a previous visit.
        this.store.clearSettingsWriteReport();
        // Replaced, not pushed: the settings are saved, so BACK must not return to the form.
        void this.router.navigate([ACCOUNT_LISTING_PATH], { replaceUrl: true });
      }

      // A failure needs nothing done to it. The store has recorded it, the banner is
      // already showing it, and the form still holds what was typed so it can be
      // corrected and sent again.
    });
  }

  /**
   * Reads the policy on arrival.
   *
   * MIGRATION: read unconditionally, as the legacy page did - its load handler
   * fetched the settings on every request, including a postback. The store's own read
   * clears any failure another screen recorded, so the screen opens clean.
   */
  ngOnInit(): void {
    this.store.loadMembershipSettings();
  }

  // -------------------------------------------------------------------------
  // TEMPLATE ACCESSORS
  // -------------------------------------------------------------------------

  /**
   * The element identifier for one field's control.
   *
   * Exists so the shared field component can be given something to point its label
   * at, which is what associates the two for a screen reader. Deterministic, so a
   * specification can address a control by identifier rather than by position.
   *
   * @param field The control's name.
   * @returns A stable, unique element identifier.
   */
  protected controlId(field: MembershipSettingsFieldName): string {
    return `membership-setting-${field}`;
  }

  /**
   * The label for one field, as the legacy resource file wrote it.
   *
   * @param field The control's name.
   * @returns The label, colon and question mark included where the legacy file had them.
   */
  protected labelFor(field: MembershipSettingsFieldName): string {
    return MEMBERSHIP_SETTINGS_FIELD_TEXT[field].label;
  }

  /**
   * The help text for one field, or empty where there is none worth showing.
   *
   * Empty for the nine listing-column switches, whose legacy help repeated their
   * labels. The shared field component renders no disclosure for an empty string.
   *
   * @param field The control's name.
   * @returns The help text, possibly empty.
   */
  protected helpFor(field: MembershipSettingsFieldName): string {
    return MEMBERSHIP_SETTINGS_FIELD_TEXT[field].help;
  }

  /**
   * Whether one field must be supplied.
   *
   * DERIVED from the control's own validators rather than restated as a list, and the
   * derivation is the point: a field's requiredness is decided once, where the control
   * is built, so the marker the reader sees and the rule that actually rejects a
   * submission cannot disagree. A hand-kept list here would be a second copy free to
   * drift, and it would drift silently - the marker would simply be wrong, with
   * nothing failing to reveal it.
   *
   * @param field The control's name.
   * @returns Whether the field is required.
   */
  protected isRequired(field: MembershipSettingsFieldName): boolean {
    return this.form.controls[field].hasValidator(Validators.required);
  }

  /**
   * The message to show beside one field, or null when it has nothing to say.
   *
   * The server's message WINS. It is resolved through the shared failure utility,
   * which matches a document's per-field keys case-insensitively and tolerates the
   * prefixes and dotted paths the server can produce - the keys in a validation
   * document are the server's own model-state keys and are not camel-cased, so
   * matching them by exact spelling would silently find nothing. That utility also
   * reads the document's per-field dictionary with a bracket, which is the only form
   * this workspace permits, and it normalises legacy break tags out of the message on
   * the way through. Nothing of that is repeated here.
   *
   * Falls back to a local message only where the browser refused the value before the
   * server saw it. Those are read through the framework's own rule test rather than
   * by inspecting its error map, which keeps property access off an index signature.
   *
   * MIGRATION: per-field validation display on this screen is a NET ADDITION and is
   * reported as one. The legacy markup declares no validator of any kind across its
   * seventy-four lines and sets no error style on any of its three editors, because
   * the excluded settings editor validated internally and reported however it chose.
   * The rules applied here are the server's own and no business rule is invented; in
   * particular no credential policy is restated, because none of it crosses the
   * boundary and a copy here would be free to contradict what is enforced.
   *
   * @param field The control's name.
   * @returns The message, or null.
   */
  protected errorFor(field: MembershipSettingsFieldName): string | null {
    const fromServer = fieldErrorMessage(this.problem(), field);

    if (fromServer !== null) {
      return fromServer;
    }

    const control = this.form.controls[field];

    if (control.valid) {
      return null;
    }

    // ⚠ ONLY THE EMPTY-FIELD RULE WAITS TO BE VISITED, AND THE DISTINCTION IS DELIBERATE. An
    // untouched control that is merely empty has not been got wrong yet - the operator may simply not
    // have reached it - so scolding it on arrival would be the premature complaint the legacy's
    // `display="Dynamic"` gating existed to avoid. Every rule below is different in kind: a value out
    // of range, or one longer than the store accepts, is only REACHABLE BY TYPING, so the operator has
    // necessarily engaged with the field and there is nothing premature about answering them at once.
    //
    // Measured before this split: typing 101 into "Users per Page" left the field with no message, no
    // red border and no `aria-invalid` until focus moved away, so the operator was told the value was
    // unusable only after they had stopped looking at it. The same reasoning settled the date fields on
    // the role membership screen, where the browser's own unusable-entry states are surfaced without
    // waiting for a blur.
    if (control.hasError('required')) {
      return control.untouched ? null : REQUIRED_MESSAGE;
    }

    if (control.hasError('min') || control.hasError('max')) {
      return this.rangeMessage(field);
    }

    if (control.hasError('maxlength')) {
      return `This setting may not exceed ${MAXIMUM_SETTING_LENGTH} characters.`;
    }

    return null;
  }

  /**
   * The wording for a value outside its permitted range.
   *
   * Composed from the bounds rather than written out, so a bound and the sentence
   * describing it cannot drift apart. The page-size sentence deliberately reads the
   * way the server's own does, because the same situation should not be described two
   * different ways depending on which side noticed it.
   *
   * @param field The control's name.
   * @returns The sentence to show.
   */
  private rangeMessage(field: MembershipSettingsFieldName): string {
    if (field === 'recordsPerPage') {
      return (
        `The number of accounts per page must be between ${MINIMUM_RECORDS_PER_PAGE} ` +
        `and ${MAX_PAGE_SIZE}.`
      );
    }

    return 'A page identifier may not be negative. Leave the field empty for no redirect.';
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  /**
   * Writes the policy, then leaves for the account listing.
   *
   * MIGRATION: the whole policy is sent, not just what changed. The legacy handler
   * walked its editors and wrote only the ones reporting themselves dirty
   * (`Website/admin/Users/UserSettings.ascx.vb` L172), one stored setting at a time.
   * The end state is identical because every member is sent with the value it
   * currently holds - and THAT is precisely why no member may ever be dropped from
   * the body for looking empty. "Unchanged settings keep their value" is expressed
   * here by sending them all, so a zero, a false and an empty string are values being
   * asserted rather than absences to be filtered out. Twenty-three members go out
   * every time.
   *
   * MIGRATION: each member is sent as its own type. The legacy handler coerced every
   * value to text on the way to storage (L174), so a switch was stored as the word
   * for true and a count as its digits; the numbers here are numbers and the switches
   * are switches.
   *
   * MIGRATION: the legacy handler cleared the settings cache itself (L189). Nothing
   * here does: cache lifetime is the server's business and no response is cached in
   * the browser.
   *
   * MIGRATION: the legacy handler ALSO started a background thread to rewrite every
   * account's display name whenever the display-name format changed (L175-L182). That
   * rewrite is NOT attempted from here, and must not be: it is a bulk write over a whole
   * tenant, which belongs behind the endpoint rather than in a browser that can be closed
   * halfway through. It is performed by the SERVER, inside the same transaction as the
   * policy write, and the endpoint answers with the number of accounts it renamed - which
   * is why this one settings write answers `200` with a body where every other answers
   * `204`. This screen's part is to report it, and
   * {@link membershipSettingsSavedMessage} is where that reporting is composed.
   *
   * Three properties of the legacy arrangement are deliberately not reproduced, and all
   * three were defects: the operator was told nothing, a failure part way through left
   * some accounts renamed and the rest not, and a request the operator abandoned took the
   * sweep with it. The divergence is recorded in MIGRATION_NOTES.md.
   */
  protected submit(): void {
    if (this.canSubmit() === false) {
      return;
    }

    this.form.markAllAsTouched();

    if (this.form.invalid) {
      return;
    }

    this.pendingSubmit = true;
    this.store.saveMembershipSettings(this.buildRequest());
  }

  /**
   * Leaves for the account listing without writing anything.
   *
   * MIGRATION: this must NOT provoke validation, and the legacy markup is explicit
   * about it - the cancel button carried `causesvalidation="False"`
   * (`Website/admin/Users/UserSettings.ascx` L73). So nothing here marks a control
   * touched, nothing recomputes a control's validity and nothing consults the form's
   * validity: abandoning a half-filled form must not first be told the form is
   * half-filled.
   *
   * Any recorded failure is cleared on the way out, so the next screen does not
   * inherit this one's banner.
   */
  protected cancel(): void {
    this.store.clearFailure();
    void this.router.navigate([ACCOUNT_LISTING_PATH]);
  }

  // -------------------------------------------------------------------------
  // MAPPING
  // -------------------------------------------------------------------------
  //
  // MIGRATION: the legacy screen compiled with strict type checking DISABLED - the
  // web configuration switches it off for the pages, while the class library it calls
  // into is compiled with it on - so the code it inherited was free to read a missing
  // key out of an untyped table and let it collapse into a zero, an empty string or a
  // false without anyone writing a conversion. Nothing collapses silently here. Every
  // conversion in this section is written out, and each of the legacy narrowings it
  // replaces is named so the correspondence can be checked:
  //
  //   * the untyped table reads throughout `UserModuleBase.GetSettings`, where a
  //     missing key was read as nothing and then used as a zero or an empty string;
  //   * the three casts that routine performed on values it had just read back out of
  //     its own table, at `Library/Components/Users/UserModuleBase.vb` L129 (display
  //     mode), L141 (profile visibility) and L185 (account-selection control) - all
  //     three are handled here by the numeric conversion below, which is why that
  //     conversion exists at all;
  //   * the coercion of every stored value to text at
  //     `Website/admin/Users/UserSettings.ascx.vb` L174, replaced by sending each
  //     member as its own type;
  //   * the conditionally sized array at the same file's L141, whose size came from a
  //     query-string test - it has no counterpart because the query-string carrying
  //     is not reproduced; and
  //   * the cast of a stored setting straight to an integer at
  //     `Website/admin/Users/Users.ascx.vb` L117, replaced by the numeric conversion
  //     below, which answers with the measured default rather than throwing when the
  //     value is not a finite number.
  //
  // MIGRATION: the conversions below never test a value for truthiness, and that is a
  // correctness requirement rather than a style. In this schema ZERO IS A REAL VALUE
  // in several places at once - the page table's identity seeds at zero, so zero is a
  // page; the tenant table's seeds at minus one, so even minus one is a tenant - and
  // the legacy codebase used minus one as its marker for "no integer" at the same
  // time. Reading zero as "unset" would repoint a redirect at the wrong page, and
  // reading a false switch as "unset" would silently re-show a hidden column. Every
  // test below is an explicit comparison against null or undefined.
  //
  // MIGRATION: the wire never omits a member. The server serialises nulls rather than
  // dropping them, so a member is present and possibly null rather than absent - and
  // an unset text setting arrives as an EMPTY STRING rather than as null, which is
  // why empty and null are treated identically for text. The guards accept both.

  /**
   * The policy as the form holds it.
   *
   * @param settings The policy the server sent.
   * @returns Every control's value, ready for the form.
   */
  private toFormValue(settings: MembershipSettings): MembershipSettingsFormValue {
    return {
      columnFirstName: this.asFlag(
        settings.columnFirstName,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnFirstName,
      ),
      columnLastName: this.asFlag(
        settings.columnLastName,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastName,
      ),
      columnDisplayName: this.asFlag(
        settings.columnDisplayName,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnDisplayName,
      ),
      columnAddress: this.asFlag(
        settings.columnAddress,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAddress,
      ),
      columnTelephone: this.asFlag(
        settings.columnTelephone,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnTelephone,
      ),
      columnEmail: this.asFlag(
        settings.columnEmail,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnEmail,
      ),
      columnCreatedDate: this.asFlag(
        settings.columnCreatedDate,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnCreatedDate,
      ),
      columnLastLogin: this.asFlag(
        settings.columnLastLogin,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnLastLogin,
      ),
      columnAuthorized: this.asFlag(
        settings.columnAuthorized,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.columnAuthorized,
      ),

      displayMode: this.asNumber(
        settings.displayMode,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displayMode,
      ),
      displaySuppressPager: this.asFlag(
        settings.displaySuppressPager,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.displaySuppressPager,
      ),
      recordsPerPage: this.asNumber(
        settings.recordsPerPage,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.recordsPerPage,
      ),

      profileDefaultVisibility: this.asNumber(
        settings.profileDefaultVisibility,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDefaultVisibility,
      ),
      profileDisplayVisibility: this.asFlag(
        settings.profileDisplayVisibility,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileDisplayVisibility,
      ),
      profileManageServices: this.asFlag(
        settings.profileManageServices,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.profileManageServices,
      ),

      // Passed through as sent. Null means no redirect; zero is a page like any
      // other. Neither is substituted for the other.
      redirectAfterLogin: this.asPageIdentifier(settings.redirectAfterLogin),
      redirectAfterRegistration: this.asPageIdentifier(settings.redirectAfterRegistration),
      redirectAfterLogout: this.asPageIdentifier(settings.redirectAfterLogout),

      securityEmailValidation: this.asText(settings.securityEmailValidation),
      securityRequireValidProfile: this.asFlag(
        settings.securityRequireValidProfile,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfile,
      ),
      securityRequireValidProfileAtLogin: this.asFlag(
        settings.securityRequireValidProfileAtLogin,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityRequireValidProfileAtLogin,
      ),
      securityUsersControl: this.asNumber(
        settings.securityUsersControl,
        LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.securityUsersControl,
      ),
      securityDisplayNameFormat: this.asText(settings.securityDisplayNameFormat),
    };
  }

  /**
   * The policy as the form now holds it, ready to send.
   *
   * Every member is listed, and listing them is the safeguard: a spread of the raw
   * value would send whatever the form happened to contain, whereas naming all
   * twenty-three makes a member added to the model without being handled here a
   * compilation failure rather than a silently absent field on the wire.
   *
   * @returns The complete policy.
   */
  private buildRequest(): MembershipSettings {
    const raw = this.form.getRawValue();

    return {
      // ⚠ #6 — SENT, AND SENT AS RECEIVED. This is not a form value and the operator cannot
      // change it: it is the server's statement about where the policy it served came from,
      // and it travels back untouched because the API binds request bodies with
      // unmapped-member handling set to disallow, so a member present on the read and absent
      // from the write would make every save `400`. The server declares it on the request as
      // accepted-and-ignored, so what is sent here has no effect on what is stored - the
      // stored answer becomes `true` by virtue of the write itself.
      //
      // Read from the policy in hand rather than from the form, falling back to the defaults'
      // own `false` before a policy has arrived - which is the only state in which a save
      // cannot be attempted anyway.
      isStored:
        this.store.membershipSettings()?.isStored ?? LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.isStored,

      columnFirstName: raw.columnFirstName,
      columnLastName: raw.columnLastName,
      columnDisplayName: raw.columnDisplayName,
      columnAddress: raw.columnAddress,
      columnTelephone: raw.columnTelephone,
      columnEmail: raw.columnEmail,
      columnCreatedDate: raw.columnCreatedDate,
      columnLastLogin: raw.columnLastLogin,
      columnAuthorized: raw.columnAuthorized,

      displayMode: raw.displayMode,
      displaySuppressPager: raw.displaySuppressPager,

      // The contract requires a number here. A cleared numeric input writes null
      // through its value accessor whatever the control's declared type says, and the
      // required rule stops a submission in that state, so this branch is a
      // belt-and-braces conversion rather than the normal path - but it is written
      // rather than asserted away, because an assertion would be a claim about the
      // framework's behaviour instead of a handling of it.
      recordsPerPage:
        raw.recordsPerPage === null
          ? LEGACY_MEMBERSHIP_SETTINGS_DEFAULTS.recordsPerPage
          : raw.recordsPerPage,

      profileDefaultVisibility: raw.profileDefaultVisibility,
      profileDisplayVisibility: raw.profileDisplayVisibility,
      profileManageServices: raw.profileManageServices,

      // Sent exactly as held, null included. Null is the contract's own expression of
      // "no redirect", and the server refuses a negative identifier - so substituting
      // the legacy minus-one marker here would turn a valid policy into a rejection.
      redirectAfterLogin: raw.redirectAfterLogin,
      redirectAfterRegistration: raw.redirectAfterRegistration,
      redirectAfterLogout: raw.redirectAfterLogout,

      securityEmailValidation: raw.securityEmailValidation,
      securityRequireValidProfile: raw.securityRequireValidProfile,
      securityRequireValidProfileAtLogin: raw.securityRequireValidProfileAtLogin,
      securityUsersControl: raw.securityUsersControl,
      securityDisplayNameFormat: raw.securityDisplayNameFormat,
    };
  }

  /**
   * A switch as sent, or the measured legacy default when it was sent as nothing.
   *
   * Both states are tested for explicitly. A truthiness test would map false to the
   * default and re-show a column an administrator had hidden.
   *
   * @param value The value as sent.
   * @param fallback The measured legacy default for this switch.
   * @returns The switch's value.
   */
  private asFlag(value: boolean | null | undefined, fallback: boolean): boolean {
    if (value === true) {
      return true;
    }

    if (value === false) {
      return false;
    }

    return fallback;
  }

  /**
   * A number as sent, or the measured legacy default when it was sent as nothing or
   * as something that is not a finite number.
   *
   * Zero passes through, because zero is a legitimate value for every numeric member
   * on this contract. The finiteness test rejects only what could not be rendered in
   * a numeric field at all.
   *
   * @param value The value as sent.
   * @param fallback The measured legacy default for this member.
   * @returns The member's value.
   */
  private asNumber(value: number | null | undefined, fallback: number): number {
    if (value === null || value === undefined) {
      return fallback;
    }

    return Number.isFinite(value) ? value : fallback;
  }

  /**
   * A page identifier as sent, unchanged.
   *
   * Null stays null, because null is how this contract says "no redirect". Zero stays
   * zero, because the page table's identity seeds at zero and zero is therefore a
   * real page - which the server confirms by setting its own floor at zero rather
   * than one. Undefined is normalised to null, which is the only conversion performed
   * and is needed because the wire cannot express undefined.
   *
   * @param value The identifier as sent.
   * @returns The identifier, or null for no redirect.
   */
  private asPageIdentifier(value: number | null | undefined): number | null {
    if (value === null || value === undefined) {
      return null;
    }

    return Number.isFinite(value) ? value : null;
  }

  /**
   * Text as sent, with nothing normalised to empty.
   *
   * Empty and null are the SAME state on this contract, which the legacy code shows
   * by testing one screen's message against an empty string in one place and against
   * its own empty-string marker in another. Both arrive here as empty, so neither can
   * put the word for nothing into a text box.
   *
   * The value itself is left ALONE. Both members that use this hold opaque text - one
   * a pattern the server compiles, the other a template an excluded subsystem expands
   * - so trimming, unescaping or stripping markup from either would corrupt a stored
   * setting. The break-tag normalisation applied to failure MESSAGES is deliberately
   * not applied to stored VALUES.
   *
   * @param value The text as sent.
   * @returns The text, or empty.
   */
  private asText(value: string | null | undefined): string {
    if (value === null || value === undefined) {
      return '';
    }

    return value;
  }
}
