/**
 * The sign-in screen: the only authentication view in the administration console,
 * reached at `/login`.
 *
 * Ported from `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx` (37 lines
 * of markup) and `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb`
 * (205 lines of code-behind), both read in full. Supporting sources are
 * `Library/Components/Users/Membership/UserLoginStatus.vb` for the outcome
 * vocabulary, `Library/Components/Shared/Null.vb` for the sentinel contract that
 * governs the verification code, `Website/release.config` for the credential policy
 * and session lifetime, `Website/admin/Authentication/App_LocalResources/Login.ascx.resx`
 * for the failure wording, and `Website/admin/Security/SendPassword.ascx.vb` as
 * context for a flow that is deliberately NOT carried forward.
 *
 * ⚠ THE EXPORTED CLASS NAME IS A TWO-SIDED CONTRACT. The sibling route file resolves
 * this module with `loadComponent: () => import('./login/login.component').then((m) =>
 * m.LoginComponent)`. A different class name yields `undefined` at run time with no
 * compile error, no build warning and no lint failure — only a blank screen on
 * navigation. It is `LoginComponent`, exactly.
 *
 * ## What this component does
 *
 * It owns four things and nothing else: the typed sign-in form, the first-render
 * seeding of the legacy query parameters, the navigation that follows a completed
 * sign-in, and the rendering of a refused one.
 *
 * That last responsibility is not shared, and the measurement is why. The bearer-token
 * interceptor lists the sign-in endpoint among the anonymous ones, so no credential is
 * attached to the request and no renewal is attempted when it is refused — a 401 there
 * is a wrong password, not a lapsed token. The error interceptor then returns early on
 * exactly 401, deliberately, because a sign-in refusal is this screen's to explain and
 * announcing it globally as well would report it twice. **If this component does not
 * render the failure, nobody does and the person sees nothing happen at all.**
 *
 * ## What this component deliberately does NOT do
 *
 * - **It builds no HTTP.** No transport type is imported, no URL is constructed, and
 *   neither the endpoint table nor an environment file is read. Every request is made
 *   by the injected store, which delegates to the authentication service.
 * - **It holds no token.** Custody belongs to the memory-only storage service. Nothing
 *   here reads, copies, stores or logs a credential of any kind, and no browser
 *   storage — local, session, cookie or indexed — is touched.
 * - **It keeps no state the store owns.** The verification ladder's revealed state is a
 *   signal on the store precisely because a field on a component is lost the moment
 *   navigation destroys it, which would silently restart the ladder at its first rung.
 *   This component READS that signal; it declares no competing flag.
 * - **It words no failure.** The three verification messages, the rate-limit sentence
 *   and the refusal sentence all come from the shared form-errors utility, which is the
 *   single owner of legacy message wording.
 * - **It notifies nothing.** The global notification service is never called from here;
 *   see {@link LoginComponent.submit} for the double-report reasoning.
 * - **It retries nothing.** A rate-limited refusal is surfaced and left alone. An
 *   automatic resubmission would defeat the very control that produced it.
 * - **It renders no markup from data.** Every string this component publishes is plain
 *   text bound through interpolation by the paired template. Legacy resource values
 *   contain raw HTML — including script tags — so no raw-markup sink, sanitiser bypass
 *   or trusted-HTML wrapper is reachable from here.
 *
 * MIGRATION: the human-verification challenge is removed. `Login.ascx.vb:L162` gated
 * the entire handler on `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha)`,
 * and that was the ONLY anti-automation control in the legacy sign-in path. Its control
 * lives under `Library/Controls/**`, a tree this migration excludes wholesale, so its
 * removal is a documented functional reduction rather than an oversight. It is not
 * uncompensated: the server applies a request rate limiter to the credential endpoints,
 * partitioned by calling address, whose refusal this screen surfaces as its own calm
 * state — see {@link LoginComponent.rateLimited}. The limiter's permit count and window
 * are deployment settings and are deliberately not restated here.
 *
 * MIGRATION: there is no keep-me-signed-in checkbox, no forgotten-password link and no
 * registration link, because the 37 lines of legacy markup contain none of the three.
 * The temptation is real and is recorded so it is not mistaken for an omission: the
 * resource file of the OUT-OF-SCOPE multi-provider container screen does carry
 * `cmdForgotPassword.Text` (L138), `cmdRegister.Text` (L147) and `Remember.Text`
 * (L213). Those keys belong to a screen this migration does not port, and the
 * authentication surface is closed at four endpoints — sign in, renew, sign out and
 * read one's own identity — so no endpoint exists for any of them.
 *
 * MIGRATION: password retrieval is abolished rather than ported, so no recovery
 * affordance appears on this screen. The legacy membership provider was registered with
 * `enablePasswordRetrieval="true"` (`Website/release.config:L239`) and
 * `passwordFormat="Encrypted"` (L245) against a reversible key committed to source
 * control, and `Website/admin/Security/SendPassword.ascx.vb` decrypted the stored
 * credential and mailed it. Credentials are now held as a one-way adaptive hash, so an
 * administrative reset is the only remedy for a forgotten one. No key or secret value
 * is reproduced anywhere in this file.
 *
 * MIGRATION: `Login.ascx.vb:L123` is NOT carried forward. It read
 * `txtPassword.Attributes.Add("value", txtPassword.Text)`, deliberately re-populating
 * the password box across a postback by writing the submitted credential into an HTML
 * attribute — a credential-exposure smell that a single-page application makes moot in
 * any case, because there is no postback to survive. Nothing here writes a password
 * into an attribute, persists it, or logs it.
 *
 * MIGRATION: `Login.ascx.vb:L101` registered a key capture —
 * `ClientAPI.RegisterKeyCapture(Me.Parent, Me.cmdLogin, Asc(vbCr))` — so that the
 * return key submitted the form. The paired template uses a real `<form>` with a
 * submit-typed button, which does this natively. That is a PORT of the behaviour rather
 * than an addition, and it is why no key listener appears in this file.
 *
 * MIGRATION: control state is eliminated rather than translated. The legacy screen kept
 * the verification rows' visibility in `ViewState` across postbacks; here it is a signal
 * on the store, and no key/value bag, serialise-and-rehydrate round trip or
 * session-state analogue is introduced.
 *
 * MIGRATION: three arguments of the legacy eight-argument sign-in call
 * (`Login.ascx.vb:L164`) disappear from this screen entirely. The authentication-type
 * literal — passed there and again at L191 when constructing the event arguments — goes
 * with the single bearer-token path, because a discriminator that can hold only one
 * value is not a contract member. The caller's network address is observed by the server
 * from the connection rather than posted, since accepting it would let a caller choose
 * the audit trail recorded against its own attempt. And the by-reference status
 * argument is gone: the outcome is the awaited result, and a refusal arrives as an
 * RFC 7807 problem document.
 *
 * MIGRATION: the legacy defect at `Login.ascx.vb:L187` is recorded and is NOT
 * reproduced here. It reads `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`,
 * and because the preceding branch at L168 consumes only the not-approved outcome, every
 * other non-zero outcome fell into that else arm and counted as authenticated —
 * including the locked-out outcome at ordinal 3, so a locked account passed the gate.
 * `UserLoginStatus.vb:L24-L30` declares all seven ordinals explicitly; under the defect,
 * ordinals 3, 5 and 6 all signed in. The seven outcomes are mapped deliberately on the
 * server instead: refusal and lockout become problem documents, the not-approved outcome
 * drives the verification ladder, and the two insecure-default-credential outcomes are
 * successes carrying an informational flag. **This component implements none of that
 * mapping and keys on no numeric ordinal from the network** — the outcome vocabulary is
 * reference-only and a successful response carries no status member. A citation
 * discrepancy is worth naming rather than quietly resolving: the model file beside this
 * one cites the statement at L188, while a full read of the source puts it at L187. L187
 * is what this file cites.
 *
 * MIGRATION: localisation is not ported. No translation runtime is present in the pinned
 * dependency surface, so no message-tagging attribute or helper is used and every
 * user-facing string is authored directly. The legacy resource files informed wording
 * only, and where they had no equivalent the wording is authored here and annotated as
 * such — see {@link LOGIN_REQUIRED_MESSAGES}.
 *
 * MIGRATION: the failure wording lives in a resource file OTHER than the one beside the
 * legacy control, and reading the nearer file would find nothing and invite invented
 * text. `Website/DesktopModules/AuthenticationServices/DNN/App_LocalResources/Login.ascx.resx`
 * holds exactly four real entries — the button caption at L120, the verification help at
 * L123, the verification label at L126 and a title at L129. The five failure messages
 * live in `Website/admin/Authentication/App_LocalResources/Login.ascx.resx`, at L162,
 * L165, L168, L216 and L222. Their reproduction belongs to the shared form-errors
 * utility, not to this file.
 *
 * MIGRATION: one of that file's entries is deliberately unreachable, and it is described
 * here rather than named because naming it is the one thing this screen must never do. At
 * L219 the administrative resource file carries a message reading "Username Does Not
 * Exist", and `Login.ascx.vb` never assigns it — the handler assigns only the three ladder
 * codes. It is a dead key belonging to an out-of-scope screen, and it is a
 * username-enumeration vector: showing it would disclose whether an account exists, a
 * distinction the legacy sign-in flow never drew between an unknown account and a wrong
 * password. **It is never rendered, no failure path can reach it, and there is no fourth
 * failure code.** The identifier itself is deliberately not spelled anywhere in this file,
 * so that a search for it over this screen returns nothing.
 *
 * MIGRATION: the wording the server may return for a locked account references a
 * password-reminder option the target does not have, because retrieval is not carried
 * forward. Composing that sentence belongs to the shared form-errors utility; it is
 * recorded here so the mismatch is traceable from the screen that shows it.
 *
 * MIGRATION: required-ness moves from imperative to declarative with its BEHAVIOUR
 * unchanged. All 37 lines of the legacy markup contain zero validator controls, so the
 * legacy screen simply posted whatever was typed and let the server refuse it. The
 * credential policy measured in `Website/release.config:L241-L245` — minimum length
 * seven, no required non-alphanumeric character, no question-and-answer requirement and
 * no unique-address requirement — governs creating and changing a password, NOT signing
 * in with one. **No length, pattern, complexity or address rule is applied on this form**,
 * because tightening a policy mid-migration locks out existing accounts. Nothing is
 * trimmed, lower-cased or Unicode-normalised either: each of those would change which
 * credentials succeed. The server remains authoritative and these validators are a
 * convenience layer, never the sole gate.
 */

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import type { OnInit, Signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

import { SIGN_IN_ROUTE } from '../../../core/config/app-routes.config';
import type { LoginRequest } from '../../../core/models/auth.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { LoginPortalSelector } from '../../../core/utils/http-params.util';
// MIGRATION: this message moved from `core/services/auth.service` to the store when the store took
//   ownership of the sign-out policy. The transport now propagates a refused revocation instead of
//   absorbing it, so the wording belongs beside the state that records the refusal.
import { RETURN_URL_QUERY_KEY as SHARED_RETURN_URL_QUERY_KEY } from '../../../core/config/app-routes.config';
import { AuthStore, REVOCATION_FAILED_MESSAGE } from '../../../core/state/auth.store';
import {
  TOO_MANY_ATTEMPTS,
  authFailureMessage,
  fieldErrorMessages,
  statusMessage,
} from '../../../core/utils/form-errors.util';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// ---------------------------------------------------------------------------
// THE QUERY PARAMETERS THIS SCREEN READS
// ---------------------------------------------------------------------------
//
// Three arrive, all of them optional and ALL OF THEM UNTRUSTED. Each is read exactly
// once, from the activated route's SNAPSHOT rather than from its observable, and that
// choice is load-bearing rather than stylistic: a snapshot is read once per activation,
// which is the direct analogue of the legacy `If Page.IsPostBack = False Then` guard at
// `Login.ascx.vb:L104`. Reading the observable would re-seed the form on every
// subsequent query change and overwrite what the person had typed.
//
// The snapshot's parameter map is also the exact shape the legacy code tested. Its
// `get` returns `string | null`, which is what `Request.QueryString("…") Is Nothing`
// distinguished at L106 and L109 — PRESENCE, not emptiness. A parameter that is present
// but empty was still present in legacy terms, and it is treated the same way here.

/**
 * The query key naming the address to return to after signing in.
 *
 * Spelled exactly as the route guard that produces it writes it. That guard refuses a
 * protected address with
 * `router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } })`, where
 * the value is the full attempted address including its own query string, preserved
 * byte for byte. **The spelling is the contract** — not `redirect`, `redirectTo`,
 * `next`, `returnURL` or `return_url`.
 *
 * MIGRATION: the literal used to be declared here, under a comment asserting it was "read
 * from nowhere else". That was never true — two route gates and, later, the bearer
 * interceptor all WRITE this key, while this screen is the only reader. It is now sourced
 * from `core/config/app-routes.config.ts`, the module that already owns the sign-in address
 * for precisely this reason, so the writers and the reader agree by construction. The symbol
 * is still exported from here because that is the name this screen's specification imports,
 * and re-exporting costs nothing.
 */
export const RETURN_URL_QUERY_KEY = SHARED_RETURN_URL_QUERY_KEY;

/**
 * The query key that seeds the account name.
 *
 * Pure parity with `Login.ascx.vb:L106-L108`, which read
 * `Request.QueryString("username")` and assigned it to the account-name box.
 */
export const USERNAME_QUERY_KEY = 'username';

/**
 * The query key naming the tenant being signed in to.
 *
 * ⚠ SPELLED AS THE SERVER SPELLS IT, because the value is forwarded to the endpoint under
 * this exact name. The sign-in endpoint binds the tenant from an optional query parameter
 * called `portalId`, and it consults it ONLY when the request host matched no configured
 * alias — the host takes precedence, so naming a tenant does not override one that
 * resolved.
 *
 * WHY THE SCREEN NEEDS IT AT ALL. An account exists within one tenant, so a sign-in that
 * addresses the wrong one is refused with the same generic denial as a wrong password.
 * Where the console is served from an origin that is not itself a configured alias —
 * which is every origin until an operator adds one — the host resolves nothing and there
 * would otherwise be no way to say which tenant was meant, leaving sign-in unreachable
 * with no diagnosable cause.
 *
 * MIGRATION: no legacy counterpart, and the reason is structural rather than an omission.
 * The legacy sign-in was a control hosted INSIDE a portal's own page, so the tenant was
 * whichever portal had rendered it and could not be in doubt. A single-page console served
 * from one origin has no such context, so the fact has to be stated.
 */
export const PORTAL_ID_QUERY_KEY = 'portalId';

/**
 * The complete grammar a tenant selector must satisfy, anchored at both ends.
 *
 * An optional single leading sign followed by one or more decimal digits, and NOTHING ELSE
 * — no surrounding whitespace, no radix prefix, no exponent, no decimal point, no digit
 * separator and no trailing text. Anchoring is the entire point: an unanchored expression
 * would match the numeric part of `'1junk'` and hand it on as though the caller had asked
 * for tenant 1.
 *
 * ⚠ THE SIGN IS ADMITTED DELIBERATELY, and `-1` is why. `Portals.PortalID` is declared
 * `IDENTITY(-1, 1)` at
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77`, so the
 * first tenant in a fresh installation really is -1. A digits-only expression would refuse
 * to sign in to it. A leading `+` is admitted for the same reason it is admitted by every
 * integer grammar — it denotes the same value — and `Number` reads both signs identically.
 *
 * Declared at module scope so the expression is compiled once rather than on every
 * submission, and exported so a specification asserts against the same grammar the
 * component applies.
 */
export const SIGNED_DECIMAL_INTEGER = /^[+-]?\d+$/;

/**
 * The query key that seeds the verification code.
 *
 * Lower-cased with no separator, exactly as `Login.ascx.vb:L109` spelled it. The legacy
 * name is kept rather than modernised because an existing verification e-mail may still
 * carry a link built with it, and renaming the key would silently stop honouring those
 * links.
 */
export const VERIFICATION_CODE_QUERY_KEY = 'verificationcode';

/**
 * Where a completed sign-in goes when no return address was supplied, or when the one
 * supplied was rejected as unsafe.
 *
 * THE APPLICATION ROOT, WHICH RESOLVES THE LANDING SCREEN FROM THE CALLER'S AUTHORITY.
 * It is not a screen address and deliberately not one: `app.routes.ts` answers the root
 * through `rootLandingRedirect`, which sends a host account to the tenant listing, a
 * tenant administrator to the first screen its authority admits, and any other account to
 * its own services. Naming a screen here instead would put a second answer to the same
 * question in the application, and the screen it used to name — the portals list — is
 * host-only (`Website/admin/Portal/Portals.ascx.vb:L339-L341` redirected a non-host to
 * Access Denied), so for every operator who is not a host account this default sent a
 * SUCCESSFUL sign-in straight into a refusal and left them standing on this screen.
 *
 * The invariant the previous revision stated therefore still holds and holds more
 * strongly: this constant and the application's empty path describe the same destination
 * rather than two competing ones, because this constant IS that path.
 */
export const DEFAULT_SIGNED_IN_ROUTE = '/';

// ---------------------------------------------------------------------------
// THE CONTROL IDENTIFIERS
// ---------------------------------------------------------------------------

/**
 * The `id` attribute of each control on this form.
 *
 * Declared here rather than written inline in the template because THREE separate
 * consumers must agree on them and only one of the three is the template. The shared
 * field component receives each value as its `for`, so the rendered label points at the
 * right control; the focus management below locates a control by the same value; and a
 * specification asserts against it. A literal repeated in three places is a literal that
 * will eventually disagree with itself.
 *
 * MIGRATION: label association is a PORT, not an addition. The legacy label control
 * already set `label.Attributes("for") = c.ClientID` — `Library/Controls/LabelControl.vb:L295`
 * — deriving the association from the control it named. A citation discrepancy is worth
 * recording rather than resolving silently: two planning documents place that assignment
 * in a block at L291-L296 and L292-L297 respectively, while a direct read puts the
 * statement itself at L295. Nothing is changed either way; the association survives, and
 * here the shared field component renders it.
 *
 * Frozen, so the record cannot be reassigned by accident, and typed by inference so each
 * member is a literal type a specification can compare exactly.
 */
export const LOGIN_CONTROL_IDS = Object.freeze({
  /** The account-name box. Legacy `txtUsername`, `Login.ascx:L10`. */
  username: 'login-username',

  /** The password box. Legacy `txtPassword`, `Login.ascx:L29`, `textmode="password"`. */
  password: 'login-password',

  /** The verification-code box. Legacy `txtVerification`, `Login.ascx:L16`. */
  verificationCode: 'login-verification-code',
});

// ---------------------------------------------------------------------------
// THE SERVER'S FIELD KEYS
// ---------------------------------------------------------------------------

/**
 * The model-state key the server reports each field's validation failures under.
 *
 * Pascal-cased, because these name model members on the server rather than members of
 * the serialised request body, and the serialiser's camel-casing policy does not apply
 * to them. Matching is delegated to the shared form-errors utility, which compares
 * case-insensitively and tolerates the request and dollar prefixes a validation bridge
 * can produce — so these values are the intent rather than a byte-exact requirement, and
 * no matching rule is re-implemented here.
 *
 * The verification key is Pascal-cased from the request member rather than from the
 * legacy control name, because the legacy control was named for a Web Forms box that no
 * longer exists.
 */
const SERVER_FIELD_KEYS = Object.freeze({
  /** The account name. */
  username: 'Username',

  /** The password. */
  password: 'Password',

  /** The verification code. */
  verificationCode: 'VerificationCode',
});

// ---------------------------------------------------------------------------
// CLIENT-SIDE VALIDATION WORDING
// ---------------------------------------------------------------------------

/**
 * The message shown beside a control the person left empty.
 *
 * MIGRATION: these three sentences are NET-NEW, and that is stated plainly rather than
 * dressed up as a port. The legacy screen declared no validator controls at all, so it
 * had no per-field required wording to inherit: an empty box was simply posted and
 * refused by the server. The nearest legacy candidate is a global `Required.Text` entry
 * reading " All fields marked with a red arrow are required." — a form-level notice about
 * a marker convention, not a message about one field — so reusing it would misreport what
 * happened.
 *
 * The wording is therefore derived from the legacy field labels themselves, which ARE
 * measured: "User Name:" and "Enter your User Name below" at
 * `Website/App_GlobalResources/SharedResources.resx:L1023` and L1020, "Password:" and
 * "Enter your Password below" at L1017 and L1014, and "Verification Code:" at
 * `Website/DesktopModules/AuthenticationServices/DNN/App_LocalResources/Login.ascx.resx:L126`.
 * Naming each field as the legacy screen named it keeps the message recognisable to
 * someone who used that screen, which is what parity of error messages means when there
 * is no message to copy.
 *
 * The MECHANISM changed — imperative refusal became a declarative validator — while the
 * BEHAVIOUR did not: an empty box still cannot complete a sign-in. Wording for anything
 * the SERVER decides is not here; it belongs to the shared form-errors utility, which
 * owns every legacy failure sentence.
 */
export const LOGIN_REQUIRED_MESSAGES = Object.freeze({
  /** Shown when the account-name box is empty. */
  username: 'User Name is required.',

  /** Shown when the password box is empty. */
  password: 'Password is required.',

  /** Shown when the verification-code box is empty while a code is being asked for. */
  verificationCode: 'Verification Code is required.',
});

/**
 * The longest account name the sign-in contract accepts.
 *
 * Mirrors `DnnMigration.Application/Validation/LoginRequestValidator.cs`, whose username rule is
 * `NotEmpty().MaximumLength(100)`. Restated rather than imported because no contract member carries
 * it; the number is a boundary the server owns and this screen reproduces.
 */
export const LOGIN_USERNAME_MAX_LENGTH = 100;

/**
 * The largest credential the sign-in contract accepts, counted in UTF-8 BYTES.
 *
 * ⚠ BYTES, NOT CHARACTERS, AND THE DIFFERENCE IS OBSERVABLE. The server measures with
 * `Encoding.UTF8.GetByteCount`, so a credential of emoji costs four bytes a character and 65 of them
 * exceed this bound while numbering 65 characters. A character-counting rule here — which is all
 * `Validators.maxLength` can express — would pass such a credential to a server that refuses it, and
 * would equally refuse a 200-character Latin credential the server accepts. Neither direction is
 * acceptable, so the count is performed the same way the server performs it.
 */
export const LOGIN_PASSWORD_MAX_BYTES = 256;

/** The error key the account-name length rule reports. */
const USERNAME_TOO_LONG_ERROR = 'usernameTooLong';

/** The error key the blank-account-name rule reports. */
const USERNAME_BLANK_ERROR = 'usernameBlank';

/** The error key the credential byte-length rule reports. */
const PASSWORD_TOO_LONG_ERROR = 'passwordTooLong';

/**
 * Wording for each bound this screen enforces ahead of the server.
 *
 * The two length sentences are the SERVER'S OWN, reproduced verbatim, so that a person who trips the
 * bound before the request leaves reads exactly what they would have read had it left — the
 * alternative is two sentences for one rule, differing by which layer noticed first.
 *
 * The blank sentence is the required sentence, because that is what the condition is: the server's
 * `NotEmpty()` rejects a value made only of whitespace, whereas Angular's own required rule accepts
 * it, so a box holding three spaces looked complete here and was refused there. Naming it as
 * "required" describes the situation the person is actually in.
 */
export const LOGIN_BOUND_MESSAGES = Object.freeze({
  /** Reproduces `LoginRequestValidator`'s username length message with its bound substituted. */
  usernameTooLong: `A username cannot be longer than ${String(LOGIN_USERNAME_MAX_LENGTH)} characters.`,

  /** Reproduces `CredentialBounds.MaximumByteLengthMessage` verbatim. */
  passwordTooLong: `The password supplied is too long. A password may be at most ${String(
    LOGIN_PASSWORD_MAX_BYTES,
  )} bytes when encoded as UTF-8.`,
});

/**
 * Refuses an account name made only of whitespace, WITHOUT trimming it.
 *
 * The distinction matters twice over. The server's `NotEmpty()` rejects such a value, so accepting it
 * here spends a request to learn what was already knowable. And trimming it instead of refusing it
 * would change WHICH credentials succeed — the legacy screen passed the box through untouched, so a
 * name whose stored form carries a trailing space must keep it.
 *
 * Silent on an empty value, which is the required rule's business: one condition, one message.
 *
 * @param control The account-name control.
 * @returns The blank error, or `null`.
 */
function nonBlankUsernameValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value;

  if (value.length === 0) {
    return null;
  }

  return value.trim().length === 0 ? { [USERNAME_BLANK_ERROR]: true } : null;
}

/**
 * Refuses an account name longer than the contract accepts.
 *
 * Expressed here rather than with `Validators.maxLength` so that both length rules on this screen read
 * the same way and report keys this file owns, which is what lets one message be chosen per failing
 * rule rather than one message per control.
 *
 * @param control The account-name control.
 * @returns The length error, or `null`.
 */
function usernameLengthValidator(control: AbstractControl<string>): ValidationErrors | null {
  return control.value.length > LOGIN_USERNAME_MAX_LENGTH
    ? { [USERNAME_TOO_LONG_ERROR]: true }
    : null;
}

/**
 * Refuses a credential exceeding the contract's UTF-8 byte ceiling.
 *
 * Counted with `TextEncoder`, which is the platform's UTF-8 encoder and therefore agrees with
 * `Encoding.UTF8.GetByteCount` by construction. The value is neither logged nor echoed; only its
 * length leaves this function.
 *
 * @param control The credential control.
 * @returns The length error, or `null`.
 */
function passwordByteLengthValidator(control: AbstractControl<string>): ValidationErrors | null {
  if (control.value.length === 0) {
    return null;
  }

  const bytes = new TextEncoder().encode(control.value).length;

  return bytes > LOGIN_PASSWORD_MAX_BYTES ? { [PASSWORD_TOO_LONG_ERROR]: true } : null;
}

// ---------------------------------------------------------------------------
// THE FORM MODEL
// ---------------------------------------------------------------------------

/**
 * The shape of the sign-in form.
 *
 * Declared explicitly, and used as the type argument of the group, so that reading the
 * group's value yields a fully typed object rather than a partial one. Every control is
 * non-nullable, which is what makes that guarantee hold and which also means resetting a
 * control returns it to the empty string rather than to null.
 *
 * Exported so a specification can name the shape without restating it.
 *
 * Three controls, matching the three caller-supplied inputs the legacy markup actually
 * declared — the account name at `Login.ascx:L10`, the verification code at L16 and the
 * password at L29. There is no fourth, and the request contract has exactly three
 * members for the same reason.
 */
export interface LoginFormModel {
  /** The account name. Legacy `txtUsername`. */
  readonly username: FormControl<string>;

  /** The password. Legacy `txtPassword`. */
  readonly password: FormControl<string>;

  /**
   * The verification code.
   *
   * ⚠ EMPTY IS A VALUE HERE, NOT AN ABSENCE, and the distinction is measured. The legacy
   * branch at `Login.ascx.vb:L177` reads `If txtVerification.Text <> ""` to tell a wrong
   * code from a missing one, and the legacy absent-text sentinel IS the empty string —
   * `Library/Components/Shared/Null.vb:L71-L75` has the body `Return ""`, not
   * `Return Nothing`. The control is therefore non-nullable and its empty value travels
   * to the server AS the empty string: never coalesced to null, never dropped from the
   * request object, never trimmed away. The judgement about what empty means is kept in
   * exactly one place, on the server.
   */
  readonly verificationCode: FormControl<string>;
}

// ---------------------------------------------------------------------------
// CHARACTER BOUNDS USED BY THE RETURN-ADDRESS GUARD
// ---------------------------------------------------------------------------

/** The first printable code unit. Anything below it is a control character. */
const FIRST_PRINTABLE_CODE_UNIT = 0x20;

/** The delete code unit, which is a control character above the printable range. */
const DELETE_CODE_UNIT = 0x7f;

/**
 * The sign-in screen.
 *
 * Standalone, because the target declares no modules anywhere, and rendered with the
 * on-push strategy, because every value it publishes is a signal or a form and neither
 * needs the default check.
 *
 * Dependencies are taken through {@link inject} in private fields rather than as
 * constructor parameters, which keeps the class's own surface free of them and lets the
 * field initialisers below read them in declaration order.
 *
 * NO PROVIDER IS DECLARED ON THIS COMPONENT. Every dependency it uses is root-provided
 * or router-supplied, and the session store in particular MUST be the shared instance:
 * a component-scoped copy would be destroyed with the screen and would take the
 * verification ladder's progress with it.
 */
@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    // Every selector, directive and pipe the paired template uses. Strict template
    // checking turns an unlisted selector into a compile error rather than a silently
    // unrendered element, so this list is exhaustive by necessity.
    //
    // The reactive-forms surface supplies the group and control directives. The four
    // shared components are the whole of this screen's design-system consumption: the
    // page title, the labelled field wrapper, the failure surface and the progress
    // indicator. The permission directive is deliberately absent — the caller of this
    // screen is unauthenticated by definition, so there is no entitlement to test.
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
    FocusFirstInvalidDirective,
  ],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginComponent implements OnInit {
  // -------------------------------------------------------------------------
  // DEPENDENCIES
  // -------------------------------------------------------------------------

  /**
   * The session store: the authority for every piece of authentication state this
   * screen shows, and the only route by which it reaches the server.
   */
  private readonly store = inject(AuthStore);

  /** Used to navigate away once a sign-in completes. */
  private readonly router = inject(Router);

  /** Read once, for the three query parameters this screen honours. */
  private readonly route = inject(ActivatedRoute);

  /**
   * Whether the sign-out that sent the operator here failed to end the session on the server.
   *
   * Shown as a calm notice rather than as a refusal: nothing the operator did was rejected, and
   * the local sign-out did succeed. What it reports is that the renewal credential may still be
   * live on the server, together with the action that genuinely exists for that.
   */
  protected readonly revocationOutstanding: Signal<boolean> = this.store.revocationOutstanding;

  /**
   * The sentence shown when {@link revocationOutstanding} is true.
   *
   * Imported rather than authored here, for the same reason the rate-limiter sentence is: one
   * situation must not be described two different ways depending on which layer noticed it. The
   * authentication client raises it through the notification channel as well, and both readers
   * therefore say exactly the same thing.
   */
  protected readonly revocationMessage = REVOCATION_FAILED_MESSAGE;

  /**
   * This component's own element, used only to locate a control for focus.
   *
   * Scoping the search to the host is what keeps focus management from reaching a
   * same-named control elsewhere on the page.
   */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Supplied to {@link afterNextRender} when it is called outside an injection context.
   *
   * Required rather than decorative: a control revealed by a template condition does not
   * exist in the document at the moment the state that reveals it changes, so focusing it
   * has to wait for the render that follows.
   */
  private readonly injector = inject(Injector);

  /**
   * Bounds the sign-in subscription to this component's lifetime.
   *
   * The store's commands are cold observables that must be subscribed exactly once, and
   * the store itself is root-provided and never destroyed — so nothing else would end a
   * subscription taken here if the person navigated away mid-request.
   */
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /**
   * The sign-in form.
   *
   * Every control is constructed non-nullable with an empty initial value, so the group's
   * value is fully typed rather than partial and a reset returns each control to the empty
   * string rather than to null.
   *
   * ⚠ FIELD ORDER IS NOT A DETAIL. The legacy markup ordered its rows account name,
   * verification code, human-verification challenge, password, submit — the challenge
   * genuinely sat BEFORE the password. With the challenge removed, the surviving order is
   * account name, then the conditional verification code, then password, then submit,
   * which is the order declared here and the order the paired template must render.
   *
   * MIGRATION: the account name and password carry a required validator; all 37 lines of
   * the legacy markup declared none, and the reasoning for adding these two and nothing
   * else is on {@link LOGIN_REQUIRED_MESSAGES}. The verification code is validated only
   * while a code is actually being asked for — see
   * {@link LoginComponent.verificationValidatorSync}. Leaving it permanently required
   * would make the very first attempt unsubmittable, which no legacy behaviour justifies.
   */
  protected readonly form = new FormGroup<LoginFormModel>({
    username: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.required, nonBlankUsernameValidator, usernameLengthValidator],
    }),
    password: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.required, passwordByteLengthValidator],
    }),
    verificationCode: new FormControl<string>('', { nonNullable: true }),
  });

  // -------------------------------------------------------------------------
  // TEMPLATE CONSTANTS
  // -------------------------------------------------------------------------

  /** The `id` of each control, so the template and the label association cannot diverge. */
  protected readonly controlIds = LOGIN_CONTROL_IDS;

  /** @see LOGIN_USERNAME_MAX_LENGTH — bound to the account-name box's native attribute. */
  protected readonly usernameMaxLength = LOGIN_USERNAME_MAX_LENGTH;

  /**
   * The calm sentence shown when the rate limiter refuses the attempt.
   *
   * Taken from the shared form-errors utility rather than authored here, because that
   * module owns every user-facing failure sentence in the workspace and the same
   * situation must not be described two different ways depending on which layer noticed
   * it.
   */
  protected readonly rateLimitMessage = TOO_MANY_ATTEMPTS;

  // -------------------------------------------------------------------------
  // STATE READ FROM THE STORE
  // -------------------------------------------------------------------------
  //
  // Every member below is a read-only signal or a derivation over one. None is writable,
  // so the template is structurally unable to mutate authentication state, and the store
  // remains the only thing that can.

  /**
   * Whether a sign-in request is in flight.
   *
   * The store's own phase projection for this command specifically, rather than its
   * general busy flag: a renewal running in the background must not disable this form.
   * The paired template disables the submit control from this signal, which is the
   * double-submission guard's visible half; {@link LoginComponent.submit} enforces the
   * same condition in code, because a disabled control is a courtesy and not a lock.
   */
  protected readonly submitting: Signal<boolean> = this.store.isAuthenticating;

  /**
   * The problem document from the last refused attempt, or null.
   *
   * Bound straight to the shared failure banner, which takes the whole document and
   * resolves its own title, sentence, per-field list and support reference from it. It is
   * deliberately not reduced to a string first: the document carries the failure code and
   * the support reference an operator needs, and collapsing it would discard both.
   */
  protected readonly problem: Signal<ProblemDetails | null> = this.store.problem;

  /**
   * Whether the last attempt failed.
   *
   * Read instead of testing {@link LoginComponent.problem} for null, because a failure can
   * carry no document at all — a transport failure, or an intermediary answering on its
   * own behalf — and inferring failure from the presence of its details would report those
   * cases as success.
   */
  protected readonly failed: Signal<boolean> = this.store.hasFailure;

  /**
   * Whether the rate limiter refused the attempt because the caller is early.
   *
   * A DISTINCT, NON-ALARMING STATE, kept apart from a refused credential deliberately:
   * nothing has broken, the caller has simply attempted too often. This is the
   * compensating control for the removed human-verification challenge, so the person has
   * to be able to understand it rather than read a generic error.
   *
   * The store derives it from the TRANSPORT status, which matters here: the shared banner
   * classifies from the status repeated inside the document body, so a refusal that
   * arrives with no body would render nothing at all. This signal is what lets the
   * template say something regardless.
   *
   * ⚠ NOTHING RETRIES. There is no retry operator, no backoff and no timed
   * resubmission anywhere in this file. Automatically re-attempting would defeat the
   * control that produced this state.
   */
  protected readonly rateLimited: Signal<boolean> = this.store.rateLimited;

  /**
   * Whether the verification-code field has been revealed.
   *
   * ⚠ READ FROM THE STORE, NEVER MIRRORED. `Login.ascx.vb:L171` branched on
   * `If Not rowVerification1.Visible`, so the legacy ladder turned on whether the field
   * had ALREADY been revealed — state that survived the postback in Web Forms control
   * state. The first refusal revealed the field and asked for a code; only a subsequent
   * refusal could judge what had been typed into it. A flag on this component would be
   * lost the moment navigation destroyed it and the ladder would silently restart at its
   * first rung, which is precisely why the signal lives on the root-provided store and
   * why this member is an alias rather than a copy.
   */
  protected readonly verificationRequired: Signal<boolean> = this.store.verificationRequired;

  /**
   * The form-level sentence for the last refusal, or null when there is none to add.
   *
   * ⚠ A REFUSAL IS NEVER SILENT, and that is the whole reason this member exists. No global
   * announcement is made for a sign-in refusal — the error interceptor returns early on 401
   * precisely because this screen owns the explanation — so a failure that produced no
   * sentence here and no document for the banner would leave the person watching a form
   * that visibly did nothing.
   *
   * Resolves in a fixed precedence, and each arm is deliberate:
   *
   * 1. nothing failed, so there is nothing to say;
   * 2. the rate limiter refused, so say that calmly and say nothing else;
   * 3. the refusal is one of the three verification outcomes, so use its wording;
   * 4. the shared banner was given a document, so let it speak rather than duplicating it;
   * 5. otherwise — a transport failure, or an intermediary answering with no body — fall
   *    back to the status wording, so something is always said.
   *

   * ⚠ RESOLVED FROM THE CURRENT FAILURE CODE, NOT FROM THE STORE'S LADDER PROMPT, and
   * that is a correctness fix rather than a preference. The store sets its ladder prompt
   * only on a turn the ladder actually applies to and deliberately leaves it in place
   * otherwise, so after a verification refusal followed by an ordinary wrong-password
   * refusal the prompt still holds the earlier verification sentence. Reading it would
   * show a stale message against a different failure. The code, by contrast, is recorded
   * fresh on every failure.
   *
   * The wording itself comes from the shared utility, which reproduces it verbatim from
   * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx`. There are EXACTLY
   * THREE verification outcomes, because `Login.ascx.vb` assigns exactly three message
   * codes — at L175 and L180, at L178, and at L184. No fourth exists and none is invented.
   */
  protected readonly signInMessage: Signal<string | null> = computed(() => {
    if (this.store.hasFailure() === false) {
      return null;
    }

    if (this.store.rateLimited()) {
      return this.rateLimitMessage;
    }

    const ladderMessage = authFailureMessage(this.store.failureCode());

    if (ladderMessage !== null) {
      return ladderMessage;
    }

    if (this.store.problem() !== null) {
      return null;
    }

    return statusMessage(this.store.failureStatus());
  });

  /**
   * Whether the form-level sentence is ALSO rendered beside the verification field.
   *
   * ⚠ THIS EXISTS SO THAT ONE SENTENCE IS ANNOUNCED ONCE. Both copies are required and neither is
   * a slip: the form-level line is what the legacy screen showed, and the field-level copy is
   * beside the control the person must actually use, which is why
   * {@link LoginComponent.verificationMessages} puts the ladder sentence at the head of that
   * field's list. But the shared field wraps its messages in an assertive region of its own, so on
   * a verification rung the identical sentence was announced TWICE — once from each region.
   *
   * The FIELD's region is kept as the single assertive source, for two reasons. It is beside the
   * control the person has to act on, and focus is moved there on the rung that first reveals the
   * field. And the region belongs to the shared component, so silencing it would silence it for
   * every other screen that uses it.
   *
   * The form-level copy is therefore rendered VISIBLY and announced by nothing on exactly this
   * path. It stays fully assertive on every other path — a wrong credential, a rate limit, a fault
   * carrying no document — because none of those appears at field level and the form-level line is
   * then the only thing that says anything.
   *
   * Both halves of the test are needed and they ask different questions. The ladder sentence must
   * be the sentence the form level is showing, which the rate-limited arm of
   * {@link LoginComponent.signInMessage} takes precedence over; and the field must actually be on
   * screen, because before the first refusal reveals it there is no field region to announce
   * anything.
   *
   * ⚠ A NARROWER MECHANISM WAS CONSIDERED AND DELIBERATELY NOT TAKEN: withholding only the
   * form-level region's LIVENESS, leaving the duplicate sentence in the accessibility tree as
   * ordinary text. It resolves the double announcement equally well, and it is worth recording why
   * this one is kept instead. Suppressing liveness alone leaves a screen-reader user reading the
   * identical sentence twice while browsing the form linearly, which is the same redundancy one
   * step removed; hiding the duplicate outright is defensible here, and only here, because the
   * very same words remain in the tree beside the control, so nothing is withheld from anybody.
   * The predicate is what has to be right either way — it must judge whether the FIELD is showing
   * this sentence, not merely whether the field exists — which is why it is re-derived from the
   * current failure code, the same code that puts the sentence at the head of
   * {@link LoginComponent.verificationMessages}, rather than from the field's visibility.
   */
  protected readonly signInMessageEchoedAtField: Signal<boolean> = computed(() => {
    if (this.store.rateLimited() || this.store.verificationRequired() === false) {
      return false;
    }

    return authFailureMessage(this.store.failureCode()) !== null;
  });

  /**
   * The identifier a person should quote when reporting the last failure, or null.
   *
   * Surfaced because it is the only join key between something seen in the browser and the
   * request as the server recorded it. The store prefers the correlation identifier it
   * validated for the request and falls back to the framework's trace identifier, which is
   * the right precedence: only the former appears in the server's own records.
   *
   * Offered here as well as inside the shared banner because the banner renders nothing
   * when the refusal carried no document, and a server fault is exactly the case where the
   * reference matters most. ⚠ IT IS A REFERENCE AND NOTHING MORE — no server internal, no
   * stack, no message the server did not intend for a reader.
   */
  protected readonly supportReference: Signal<string | null> = this.store.supportReference;

  // -------------------------------------------------------------------------
  // THE FORM'S OWN STATE, MADE OBSERVABLE
  // -------------------------------------------------------------------------

  /**
   * The most recent state change reported by the form, as a signal.
   *
   * ⚠ THIS EXISTS SO THAT THE DERIVATIONS BELOW ARE ACTUALLY REACTIVE, and leaving it out
   * would produce a bug with no compile error. A form control is an imperative object:
   * `invalid` and `touched` are plain properties, not signals, so a derivation that read
   * them alone would compute once, cache the result and never recompute — the person would
   * fix an empty box and the message would stay on screen. Bridging the form's own event
   * stream into a signal gives those derivations a dependency that genuinely changes when
   * the form does.
   *
   * The stream reports value, validity, touched and pristine transitions alike, which is
   * exactly the set that can change what a field's message should say. Its payload is
   * never inspected — only the fact that it changed matters — and it is subscribed with
   * an initial value so the first read never blocks.
   *
   * Private, because it is a mechanism rather than state a template should show.
   */
  private readonly formStateChanged = toSignal(this.form.events, { initialValue: null });

  /**
   * Whether a sign-in has been attempted from this screen.
   *
   * Gates the client-side required messages so nothing is reported before the person has
   * asked for anything, which matches the legacy screen: it showed no validation feedback
   * at all until the button was pressed, having no validator controls to show it with.
   *
   * A signal rather than a plain field so the derivations above recompute the moment it
   * flips. Writable and therefore PRIVATE; the template never sees it.
   */
  private readonly submitAttempted = signal(false);

  // -------------------------------------------------------------------------
  // PER-FIELD MESSAGES
  // -------------------------------------------------------------------------
  //
  // Each of the three derivations below returns an ARRAY, and the array is passed to the
  // shared field component unchanged.
  //
  // ⚠ NO REDUCTION IS PERFORMED, and this corrects a documented expectation rather than
  // taking a shortcut. That component's message input is deliberately widened to accept a
  // single string OR a read-only array, with a normalising setter that drops blank
  // entries, because two validation messages can legitimately be outstanding on one
  // control at once — measured four times over in the legacy role editor, where two
  // comparison validators sit on a single control — and because there are zero validation
  // summaries in the in-scope legacy screens, making the per-field region the only error
  // surface there is. Joining or truncating here would be a message the person never sees.
  //
  // ⚠ NOTHING IS PRE-CLEANED EITHER. That same component strips a leading break tag and
  // renders as plain text internally, so passing it already-processed strings would apply
  // the treatment twice.
  //
  // Server messages are matched by the shared utility, which compares case-insensitively
  // and tolerates the prefixes and nested paths a validation bridge produces. That is why
  // no key comparison is written here: the rule belongs in one place.

  /** Messages for the account-name box: the client's own, then the server's. */
  protected readonly usernameMessages: Signal<readonly string[]> = computed(() =>
    this.collectMessages(
      this.form.controls.username,
      LOGIN_REQUIRED_MESSAGES.username,
      SERVER_FIELD_KEYS.username,
    ),
  );

  /** Messages for the password box: the client's own, then the server's. */
  protected readonly passwordMessages: Signal<readonly string[]> = computed(() =>
    this.collectMessages(
      this.form.controls.password,
      LOGIN_REQUIRED_MESSAGES.password,
      SERVER_FIELD_KEYS.password,
    ),
  );

  /**
   * Messages for the verification-code box: the client's own, the ladder's, then the
   * server's.
   *
   * The ladder sentence belongs HERE rather than only above the form, because it is about
   * this one field — it asks for a code, or reports that the code given was wrong — and a
   * message about a field belongs beside it. It is added only while the field is actually
   * on screen: before the first refusal reveals it there is no field to attach a message
   * to, and the store has recorded no ladder turn.
   *
   * ⚠ THIS IS THE SURFACE THAT ANNOUNCES THE LADDER SENTENCE, and the form-level line
   * deliberately yields to it — see {@link signInMessageEchoedAtField} for the whole reasoning and
   * for why the yielding is done there rather than by withholding the sentence here. In
   * short: the field's region is the one that carries the sentence TOGETHER WITH the label
   * of the box the person must type into, and it is also what tells a screen-reader user
   * that a new box has appeared at all. Withholding the sentence from here would have
   * removed the field's only message, leaving the revealed box to appear silently — trading
   * one accessibility defect for another.
   */
  protected readonly verificationMessages: Signal<readonly string[]> = computed(() => {
    const collected = [
      ...this.collectMessages(
        this.form.controls.verificationCode,
        LOGIN_REQUIRED_MESSAGES.verificationCode,
        SERVER_FIELD_KEYS.verificationCode,
      ),
    ];

    if (this.store.verificationRequired()) {
      const ladderMessage = authFailureMessage(this.store.failureCode());

      if (ladderMessage !== null) {
        // Ahead of the server's own per-field text, because the ladder sentence is the
        // one the legacy screen showed and the one that says what to do next.
        collected.unshift(ladderMessage);
      }
    }

    return collected;
  });

  // -------------------------------------------------------------------------
  // REACTIVE-TO-IMPERATIVE BRIDGE
  // -------------------------------------------------------------------------

  /**
   * Keeps the verification control's validator in step with the store's revealed state.
   *
   * THE ONE GENUINE SIDE EFFECT IN THIS COMPONENT, and the reason it is an effect rather
   * than a line in the failure handler is worth stating. A form control is an imperative
   * object that cannot observe a signal, so something has to carry the change across, and
   * doing it at each call site would mean a future call site could forget. It also handles
   * a case no call site covers: the store is root-provided, so its revealed state survives
   * this screen being destroyed and recreated by navigation, and an effect declared here
   * applies the validator on the very first run whether the ladder was reached in this
   * visit or an earlier one.
   *
   * `updateValueAndValidity` is called immediately afterwards so the group's validity is
   * correct the moment the field appears, rather than staying stale until the person types.
   * The change notification is suppressed because nothing here subscribes to the form's
   * streams and re-running dependent validation for a validator swap would be work with no
   * observer.
   *
   * The revealed field is then focused once the render that shows it has happened, which
   * is both the legacy focus intent at `Login.ascx.vb:L126-L130` and how a person using a
   * screen reader learns that a new field has appeared at all. Moving focus announces the
   * field and its label without any visual change whatsoever.
   *
   * No signal is written from here, so the effect creates no feedback loop.
   */
  private readonly verificationValidatorSync = effect(() => {
    const required = this.store.verificationRequired();
    const control = this.form.controls.verificationCode;

    if (required) {
      control.setValidators([Validators.required]);
    } else {
      control.clearValidators();
    }

    control.updateValueAndValidity({ emitEvent: false });

    if (required) {
      afterNextRender(() => this.focusControl(LOGIN_CONTROL_IDS.verificationCode, 'verificationCode'), {
        injector: this.injector,
      });
    }
  });

  /**
   * The return address exactly as it arrived, before it is judged safe.
   *
   * Held unvalidated on purpose, so that the validation happens at the point of use and a
   * specification can exercise the guard directly. It is never navigated to without
   * passing through {@link LoginComponent.resolveReturnUrl} first.
   */
  private requestedReturnUrl: string | null = null;

  // -------------------------------------------------------------------------
  // LIFECYCLE
  // -------------------------------------------------------------------------

  /**
   * Prepares the screen: honours an existing session, seeds the legacy query parameters
   * and places the initial focus.
   *
   * A faithful reproduction of `Login.ascx.vb:L103-L135`, in the legacy order. The outer
   * guard there is `If Not Request.IsAuthenticated Then`, so seeding and focusing happened
   * only for a caller who was not already signed in, and the same is true here: the
   * early return below is that guard.
   *
   * MIGRATION: the already-signed-in case is a real one rather than a theoretical one,
   * because no route guard is attached to this screen — guarding the sign-in screen would
   * deadlock the application, and the bearer-token interceptor navigates HERE when a
   * session cannot be renewed, so the address has to be reachable with no credentials at
   * all. A signed-in visitor can therefore still arrive, and the legacy screen sent them
   * on rather than asking them to sign in again.
   *
   * ⚠ THE REDIRECT IS GATED ON THE STORE'S OWN VIEW OF THE SESSION, never on the mere
   * presence of a token, and that was verified rather than assumed before being written.
   * The interceptor's terminal path clears the token custodian and then navigates here; the
   * custodian's authentication signal is derived from the session it just discarded, and the
   * store's signal IS that same signal rather than a copy. Clearing therefore reports
   * "not signed in" here too, so this redirect cannot bounce a person straight back out of
   * the screen they were just sent to.
   */
  ngOnInit(): void {
    this.requestedReturnUrl = this.readQueryParameter(RETURN_URL_QUERY_KEY);

    if (this.store.isAuthenticated()) {
      this.leaveForReturnUrl();

      return;
    }

    this.seedFromQueryParameters();

    // MIGRATION: `Login.ascx.vb:L126-L130` focused the account-name box when it was empty
    // and the password box otherwise, and this is a PORT of that rather than an addition —
    // it costs nothing visually and it is what lets a person start typing without reaching
    // for the pointer. It waits for the render because the controls do not exist in the
    // document while this hook runs.
    afterNextRender(() => this.focusFirstCredentialField(), { injector: this.injector });
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  /**
   * Submits the credentials.
   *
   * Bound to the paired template's form submission, which is what carries the legacy
   * return-key behaviour across: `Login.ascx.vb:L101` had to register a key capture
   * because a Web Forms button needed one, whereas a real form with a submit-typed button
   * does it natively.
   *
   * MIGRATION: the legacy handler opened with a human-verification gate at
   * `Login.ascx.vb:L162` and seeded its outcome variable to failure at L163 so that a path
   * which forgot to assign a result failed closed. The gate is gone with the challenge, but
   * the fail-closed intent is kept exactly: this method refuses to send anything unless the
   * form is valid, and it treats only an explicit success as a success. An absent or
   * unrecognised outcome is never taken for one.
   *
   * ⚠ DOUBLE SUBMISSION IS REFUSED HERE, not merely discouraged in the template. The
   * template disables the submit control while a request is in flight, but a disabled
   * control is a courtesy — a resubmission can still arrive by return key or by script —
   * so the condition is enforced in code as well. No attempt counter and no lockout
   * heuristic is implemented: the server owns both, and the legacy screen had neither.
   *
   * ⚠ THE GLOBAL NOTIFICATION SERVICE IS NEVER CALLED FROM HERE. The error interceptor
   * already announces 403, 404, 409, 422, 429 and server faults, so notifying as well
   * would report the same refusal twice — and the verification sentences must sit beside
   * the field they concern in any case, which a transient global message cannot do. The
   * failure is rendered inline, unconditionally, by the shared banner and the per-field
   * messages above.
   */
  protected submit(): void {
    // The in-flight guard comes first, so a repeat submission cannot even mark the form.
    if (this.store.isAuthenticating()) {
      return;
    }

    this.submitAttempted.set(true);

    // Marks every control so the shared field components render their required messages.
    // The submission flag above is what the derivations gate on, but marking keeps the
    // form's own state honest for anything that inspects it.
    this.form.markAllAsTouched();

    if (this.form.invalid) {
      this.focusFirstInvalidField();

      return;
    }

    const request: LoginRequest = {
      // Passed through EXACTLY as typed. Nothing is trimmed, lower-cased or
      // Unicode-normalised, because each of those would change which credentials succeed
      // and the legacy screen did none of them.
      username: this.form.controls.username.value,
      password: this.form.controls.password.value,
      // ⚠ THE EMPTY STRING IS TRANSMITTED AS THE EMPTY STRING. `Login.ascx.vb:L177` tells a
      // wrong code from a missing one with `If txtVerification.Text <> ""`, and the legacy
      // absent-text sentinel IS the empty string (`Null.vb:L71-L75` returns `""`), so the
      // two were always one branch. Coalescing to null, omitting the member or trimming
      // would move that decision boundary silently. The contract admits all three forms and
      // treats them alike; nothing here rewrites one into another.
      verificationCode: this.form.controls.verificationCode.value,
    };

    // Subscribed exactly once, which is what issues the request: the store's commands are
    // cold by design so that a command nobody subscribed to changes no state. Bounded to
    // this component's lifetime, because the store is root-provided and would never end
    // the subscription itself if the person navigated away mid-request.
    // The tenant selector is resolved at SUBMIT time rather than on activation, unlike the three
    // parameters that seed the form. Those seed a control the person may then edit, so reading them
    // once is the point; this one is not presentation at all - it is part of the request - so it is
    // read where the request is built and is never held in form state a submission could stale.
    this.store
      .login(request, this.resolvePortalSelector())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.discardCredential();
          this.leaveForReturnUrl();
        },
        // The store has already recorded the failure and advanced the ladder by the time
        // this runs, and the template renders both. What is left to do is the part no
        // signal can express: put the caret back where the person has to act.
        error: () => this.handleRefusedAttempt(),
      });
  }

  /**
   * Dismisses the failure currently on screen.
   *
   * Offered so the paired template can give the banner a dismiss affordance. It clears the
   * recorded failure and deliberately does NOT retract the verification field: dismissing a
   * message must not withdraw a box the person is being asked to fill in. That distinction
   * is the store's, and this method simply defers to it.
   *
   * ⚠ FOCUS IS RE-HOMED, AND THAT IS NOT OPTIONAL POLISH. A browser audit of this screen
   * caught the omission: whatever affordance calls this method is itself inside the region
   * being removed, so clearing the failure destroys the very element that had focus and the
   * browser drops focus to the document body. A person using a keyboard or a screen reader
   * is then silently returned to the top of the document and has to traverse the whole page
   * again to reach the form they were in the middle of. Moving focus to the field they still
   * have to correct costs nothing visually and keeps them where they were.
   *
   * The move is deferred to after the next render because the banner is still in the
   * document at the moment this runs; focusing before it is removed would let the removal
   * take focus away again.
   */
  protected dismissFailure(): void {
    this.store.clearError();

    afterNextRender(() => this.focusFirstCredentialField(), { injector: this.injector });
  }

  /**
   * Whether a control should be reported to assistive technology as rejected.
   *
   * Exposed for the paired template to bind to `[attr.aria-invalid]` on the control it
   * projects. It exists here rather than being left to the template because the condition
   * is the same one the message derivations use, and two expressions of it would eventually
   * disagree — a field could then announce itself as valid while a message sat beneath it.
   *
   * The shared field component owns the label association and the described-by wiring, but
   * it deliberately does not touch the control that is projected into it, so the invalid
   * state has to be set by whoever owns the control. That is this screen.
   *
   * Returns a boolean rather than the string the attribute takes, so the template decides
   * whether to render the attribute at all — an `aria-invalid="false"` on every field is
   * noise, whereas omitting the attribute is the default state.
   *
   * @param controlName The control's name within {@link LoginFormModel}.
   * @returns True when the control has a message to report.
   */
  protected isRejected(controlName: keyof LoginFormModel): boolean {
    switch (controlName) {
      case 'username':
        return this.usernameMessages().length > 0;
      case 'password':
        return this.passwordMessages().length > 0;
      case 'verificationCode':
        return this.verificationMessages().length > 0;
    }
  }

  // -------------------------------------------------------------------------
  // MESSAGE COLLECTION
  // -------------------------------------------------------------------------

  /**
   * Collects every message that applies to one control, client-side first.
   *
   * Reads {@link LoginComponent.formStateChanged} so that the calling derivation has a
   * dependency which genuinely changes when the form does — see that member for why
   * omitting it would silently freeze the result.
   *
   * The server's messages are matched by the shared utility rather than by a comparison
   * written here, so the case-insensitive, prefix-tolerant rule exists in exactly one
   * place. The result is a plain array and is handed to the shared field component
   * untouched: that component accepts several messages and normalises them itself, so
   * joining or truncating here would discard one the person needs to read.
   *
   * @param control The control whose messages are wanted.
   * @param requiredMessage The client-side wording for an empty value.
   * @param serverFieldKey The model-state key the server reports this field under.
   * @returns The messages, client-side first, then the server's, in document order.
   */
  private collectMessages(
    control: FormControl<string>,
    requiredMessage: string,
    serverFieldKey: string,
  ): readonly string[] {
    // Read for its reactivity, not for its payload.
    this.formStateChanged();

    const collected: string[] = [];

    if (this.submitAttempted() && control.invalid) {
      // ⚠ THE MESSAGE IS CHOSEN BY WHICH RULE FAILED, not by the control being invalid. Each box now
      // carries more than one rule — an account name must be present, non-blank AND within the
      // contract's length, and a credential must be present AND within its byte ceiling — so a single
      // sentence per control would describe a full box as empty the moment it grew too long.
      collected.push(this.boundMessageFor(control) ?? requiredMessage);
    }

    collected.push(...fieldErrorMessages(this.store.problem(), serverFieldKey));

    return collected;
  }

  /**
   * The sentence for whichever bound the control has broken, or `null` when none has.
   *
   * Returns `null` for an empty or blank box so that the caller falls back to the required sentence,
   * which is the wording the legacy field labels supply for exactly that condition.
   *
   * @param control The control whose errors are being described.
   * @returns The bound's own sentence, or `null`.
   */
  private boundMessageFor(control: FormControl<string>): string | null {
    if (control.hasError(USERNAME_TOO_LONG_ERROR)) {
      return LOGIN_BOUND_MESSAGES.usernameTooLong;
    }

    if (control.hasError(PASSWORD_TOO_LONG_ERROR)) {
      return LOGIN_BOUND_MESSAGES.passwordTooLong;
    }

    return null;
  }

  // -------------------------------------------------------------------------
  // NAVIGATION
  // -------------------------------------------------------------------------

  /**
   * Leaves this screen for the requested address, falling back to the default one when the
   * requested address turns out to be one this account may not enter.
   *
   * ⚠ THE OUTCOME OF THE NAVIGATION IS NOW READ, AND THAT CLOSES A MEASURED DEFECT. The
   * return address is supplied by whichever gate INTERRUPTED the operator, and that gate only
   * establishes that they were not signed in — not that they are permitted the address once
   * they are. So a tenant administrator sent to `/login?returnUrl=%2Fportals` signed in
   * successfully, the permission gate then refused the host-only listing, and because a
   * refusal CANCELS a navigation rather than redirecting it, the router stayed where it was:
   * on the sign-in screen. Measured in a browser — the header rendered the signed-in identity
   * and the rail rendered the full administration map, while the main region still showed the
   * sign-in form with the credentials filled in and the document title still read "User Log
   * In". The operator was signed in and looking at a sign-in form.
   *
   * The fallback is the default landing address, and that constant is the right answer for
   * exactly the reason it records about itself: it is the application root, which resolves the
   * landing screen FROM THE CALLER'S AUTHORITY, so it cannot repeat the problem by naming a
   * screen some accounts may not enter.
   *
   * ⚠ THREE CONDITIONS GUARD THE FALLBACK, AND EACH RULES OUT A WAY OF MAKING THINGS WORSE.
   *   * The requested address must not already BE the default, or a refusal of the default
   *     would send this into a loop against itself.
   *   * No navigation may be in flight. A gate that redirects returns an address rather than a
   *     refusal, and the router expresses that by resolving THIS navigation `false` and
   *     scheduling the redirect — so acting on `false` alone would race the gate's own
   *     destination and could override it.
   *   * The router must still be showing the sign-in screen. That is the whole condition being
   *     repaired; anywhere else, something took the operator somewhere and it is not this
   *     screen's business to second-guess it.
   *
   * The promise chain is not awaited and a rejection is absorbed, matching how the rest of the
   * application navigates: a navigation the router refuses outright is not something this
   * screen can act on, and an unhandled rejection would be reported as an application fault
   * when nothing is faulty.
   */
  private leaveForReturnUrl(): void {
    const requested = this.resolveReturnUrl();

    // ⚠ THE ADDRESS IS REPLACED RATHER THAN PUSHED. The sign-in screen has served its purpose the
    // moment a session is held, so leaving a history entry for it offers the browser's Back button as
    // a route back to a form nobody needs - and the unsaved-entry gate reads a replacement as an
    // application-initiated departure, so it does not question a navigation nobody chose.
    void this.router
      .navigateByUrl(requested, { replaceUrl: true })
      .then((arrived) => {
        if (arrived || requested === DEFAULT_SIGNED_IN_ROUTE) {
          return false;
        }

        if (this.router.getCurrentNavigation() !== null) {
          return false;
        }

        if (this.router.url.startsWith(SIGN_IN_ROUTE) === false) {
          return false;
        }

        return this.router.navigateByUrl(DEFAULT_SIGNED_IN_ROUTE, { replaceUrl: true });
      })
      .catch(() => false);
  }

  /**
   * Empties the credential controls the moment a session is held.
   *
   * ⚠ THE PASSWORD MUST NOT OUTLIVE ITS USE, and it did. A review measured the account name and
   * sixteen masked characters still present in the DOM after a SUCCESSFUL authentication, on a
   * screen the operator had no further use for. Ordinarily the navigation that follows destroys
   * this component and takes the value with it, which is why the exposure is easy to miss - but
   * that navigation is not guaranteed: its rejection is deliberately absorbed just above, so a
   * refused route leaves this form standing, still holding the credential it just used.
   *
   * Cleared explicitly rather than left to teardown for that reason, and cleared BEFORE the
   * navigation is requested so no ordering of the two can leave the value behind.
   *
   * `reset('')` rather than `reset()`: both controls are `nonNullable`, so a bare reset restores
   * the declared initial value - which is the empty string here and would be correct - but naming
   * it makes the intent legible and survives a future change to that initial value. The
   * verification code is deliberately left alone: it is not a credential, it is seeded from the
   * address, and a caller who returns to this screen should not have to fetch it again.
   */
  private discardCredential(): void {
    this.form.controls.password.reset('');
    this.form.controls.username.reset('');
  }

  /**
   * The address to navigate to after signing in.
   *
   * @returns The requested address when it is safe, and the default landing address
   * otherwise.
   */
  private resolveReturnUrl(): string {
    const requested = this.requestedReturnUrl;

    // The guard is a type predicate, so a value it accepts is used exactly as it arrived
    // rather than rebuilt — the route guard that produced it preserved the attempted
    // address byte for byte, including its own query string, and re-serialising it here
    // could only lose something.
    return this.isSafeReturnUrl(requested) ? requested : DEFAULT_SIGNED_IN_ROUTE;
  }

  /**
   * Whether a requested return address may be navigated to.
   *
   * ⚠ THIS IS AN OPEN-REDIRECT GUARD AND IT IS MANDATORY. The value arrives in a query
   * parameter, so anyone can choose it — a link in an e-mail, a message, another site — and
   * an unchecked value would let this screen send a person who has just signed in to an
   * attacker's page carrying a convincing copy of it. Only an address WITHIN this
   * application is accepted.
   *
   * Each rejection covers a distinct way a value can leave the application:
   *
   * - not beginning with a slash at all, which is either a bare host or a scheme;
   * - beginning with two slashes, which is a scheme-relative address and reaches any host;
   * - containing a scheme separator anywhere, which catches an absolute address hidden
   *   behind a leading slash;
   * - containing a backslash, which some browsers normalise to a forward slash and which
   *   therefore turns a single leading slash into the scheme-relative case above;
   * - containing a control character, which browsers strip from an address before resolving
   *   it and which can therefore be used to break up any of the sequences already listed.
   *
   * The control-character test walks code units rather than applying a pattern, because a
   * pattern carrying literal control characters is unreadable and easy to get subtly wrong.
   *
   * A rejected value fails SILENTLY to the default address. No message is shown, because a
   * hostile parameter is not the person's mistake and reporting it would only confuse them.
   *
   * Declared as a type predicate so a caller that accepts a value may use it directly
   * without a further null test, which keeps the single decision about safety in one place
   * instead of leaving a second, weaker test at the call site.
   *
   * @param candidate The value as it arrived, or null when the parameter was absent.
   * @returns True when the value is an address inside this application.
   */
  private isSafeReturnUrl(candidate: string | null): candidate is string {
    if (candidate === null) {
      return false;
    }

    if (candidate.startsWith('/') === false) {
      return false;
    }

    if (candidate.startsWith('//')) {
      return false;
    }

    if (candidate.includes('://')) {
      return false;
    }

    if (candidate.includes('\\')) {
      return false;
    }

    for (let index = 0; index < candidate.length; index += 1) {
      const codeUnit = candidate.charCodeAt(index);

      if (codeUnit < FIRST_PRINTABLE_CODE_UNIT || codeUnit === DELETE_CODE_UNIT) {
        return false;
      }
    }

    return true;
  }

  // -------------------------------------------------------------------------
  // QUERY PARAMETERS
  // -------------------------------------------------------------------------

  /**
   * Seeds the form from the two legacy query parameters.
   *
   * Both are optional and both are untrusted. Neither is validated beyond being present,
   * because neither is acted upon: a seeded value is text in a box that the person can see
   * and change, and the server judges it in either case.
   *
   * MIGRATION: the account name is seeded exactly as `Login.ascx.vb:L106-L108` seeded it.
   *
   * MIGRATION: ⚠ THE VERIFICATION CODE SEEDS A VALUE AND NEVER REVEALS THE FIELD, which
   * is a deliberate divergence from `Login.ascx.vb:L109-L116`. The legacy code revealed the
   * verification rows here as well, but only inside
   * `If PortalSettings.UserRegistration = PortalRegistrationType.VerifiedRegistration` —
   * a per-request server-side setting. That test is UNOBTAINABLE from this screen: the
   * caller is unauthenticated by definition and reading a portal's settings requires a
   * session, so there is nothing to test against. Guessing would be worse than not asking,
   * and a second client-side path to reveal the field would fork state the store owns. The
   * value is therefore seeded so that it travels on the first attempt — which is what the
   * link in a verification e-mail is for — while visibility remains governed solely by the
   * store's revealed-state signal, i.e. by the server's own answer.
   */
  private seedFromQueryParameters(): void {
    const seededUsername = this.readQueryParameter(USERNAME_QUERY_KEY);

    if (seededUsername !== null) {
      this.form.controls.username.setValue(seededUsername);
    }

    const seededCode = this.readQueryParameter(VERIFICATION_CODE_QUERY_KEY);

    if (seededCode !== null) {
      this.form.controls.verificationCode.setValue(seededCode);
    }

    // Consumed above, and now removed from the address. Both values are in the form, which is
    // the only place they are needed.
    if (seededUsername !== null || seededCode !== null) {
      this.scrubSeededQueryParameters();
    }
  }

  /**
   * Removes the seeded account name and verification code from the browser's address.
   *
   * ⚠ AN ADDRESS IS NOT A PRIVATE CHANNEL, WHICH IS WHY THIS EXISTS. A query string is
   * persisted in the browser's own history, is offered by the address bar's autocomplete to
   * whoever next uses the machine, is handed to third-party origins in the `Referer` header
   * of any subsequent request, and is the single most commonly recorded part of a request in
   * proxy and server access logs. A verification code is single-use authentication material
   * and an account name identifies its holder, so neither belongs in any of those places one
   * moment longer than it takes to read it.
   *
   * The legacy screen could not have done this. `Login.ascx.vb:L104-L116` read the values
   * during a full page render, and the only address the browser ever held was the one the
   * verification e-mail supplied; there was no client-side history to rewrite. This is
   * therefore a DELIBERATE DIVERGENCE rather than a port, and it is recorded as one.
   *
   * ⚠ `replaceUrl` IS THE LOAD-BEARING OPTION. Without it the router PUSHES a second entry
   * and the original address — material and all — stays one press of Back away, and stays in
   * session history for as long as the tab lives. Replacing consumes the entry instead.
   *
   * Only the two sensitive keys are dropped. `queryParamsHandling: 'merge'` preserves
   * everything else, which matters because {@link RETURN_URL_QUERY_KEY} may be present and is
   * still needed after a successful attempt — this scrub must not become a redirect bug.
   *
   * The form is untouched. Re-seeding cannot undo it either: {@link readQueryParameter} reads
   * the ACTIVATION SNAPSHOT and is reached only from `ngOnInit`, so a query change does not
   * re-run the seed and cannot blank a field the operator has since typed into.
   */
  private scrubSeededQueryParameters(): void {
    void this.router
      .navigate([], {
        relativeTo: this.route,
        queryParams: {
          [USERNAME_QUERY_KEY]: null,
          [VERIFICATION_CODE_QUERY_KEY]: null,
        },
        queryParamsHandling: 'merge',
        replaceUrl: true,
      })
      .catch(() => false);
  }

  /**
   * Reads one query parameter from the activation snapshot.
   *
   * ⚠ PRESENCE, NOT EMPTINESS. The legacy guards are `If Not Request.QueryString("…") Is
   * Nothing` at `Login.ascx.vb:L106` and L109, so a parameter that is present but empty was
   * still present — and it still is here. A test for a non-empty value would be a different
   * test with a different outcome, and this one is deliberately the legacy one.
   *
   * The SNAPSHOT is read rather than the observable, which is what makes this a
   * first-activation-only read and therefore the analogue of the legacy
   * `If Page.IsPostBack = False Then` guard at L104. Subscribing would re-seed the form on
   * every later query change and overwrite whatever had been typed.
   *
   * @param key The query key, spelled as the producer spells it.
   * @returns The value, or null when the parameter is absent.
   */
  private readQueryParameter(key: string): string | null {
    return this.route.snapshot.queryParamMap.get(key);
  }

  /**
   * The tenant selector to send with the credentials, or null when the request host resolves it.
   *
   * ⚠ THIS IS A SECURITY BOUNDARY, NOT A CONVENIENCE PARSE. The value is attacker-controlled —
   * anyone can put anything in a query string and send the resulting link to somebody else — and
   * what it selects is WHICH TENANT the credentials are offered to. So the grammar is validated
   * on the WHOLE string BEFORE any conversion happens, and only a complete signed decimal integer
   * literal is accepted.
   *
   * ⚠ WHY `Number.parseInt` IS NOT USED, stated plainly because a previous revision did use it and
   * the defect it caused was real rather than theoretical. `parseInt` is PREFIX-TOLERANT: it
   * consumes the longest leading run that looks numeric and silently discards the rest, so it
   * answers `1` for `'1junk'`, `1` for `'1e3'`, `1` for `'1.9'` and `12` for `'12 34'`. Paired with
   * an `isInteger` test — which only asks whether the RESULT is a whole number, never whether the
   * INPUT was one — every one of those malformed strings became a confident selection of a
   * DIFFERENT, REAL tenant. `'0x10'` is worse still: the ten-radix argument makes `parseInt` stop
   * at the `x` and answer `0`, which is a real portal under the seeding described below. The
   * conversion here is {@link Number} over an already-validated literal instead, which is total and
   * has no prefix behaviour at all.
   *
   * ⚠ AND `Number.isSafeInteger` RATHER THAN `Number.isInteger`. Beyond 2^53 the double-precision
   * grid is coarser than the integers, so a longer digit run does not merely overflow — it ROUNDS
   * to a nearby representable value and still passes `isInteger`. `'9007199254740993'` becomes
   * 9007199254740992. A key the server could never have issued would then be transmitted as though
   * it were exact, so anything outside the safe range is refused rather than rounded.
   *
   * ⚠ SENTINEL DISCIPLINE, AND IT IS LOAD-BEARING HERE RATHER THAN CEREMONIAL. `Portals.PortalID`
   * is declared `IDENTITY(-1, 1)`, so MINUS ONE names the first tenant and ZERO the second — and
   * minus one is also the legacy stand-in for an absent integer
   * (`Library/Components/Shared/Null.vb:L41`). Presence is therefore tested EXPLICITLY: a
   * truthiness test would discard tenant zero, a `> 0` test would discard both real seeds, and a
   * `?? -1` fallback would manufacture the very value that causes the confusion. The grammar below
   * admits a leading sign for exactly this reason, so `-1` and `0` both survive.
   *
   * A malformed value yields NO selector rather than a refusal or a guessed tenant. The server's own
   * precedence already covers that case: with no selector it resolves the tenant from the host
   * exactly as it does for a sign-in that named none, and if that resolves nothing the sign-in is
   * refused by the server with its own message. Refusing here instead would invent a client-side
   * error the server does not have, and forwarding a malformed value would earn a 400 that says
   * nothing useful to the person. What must never happen — and what this method now prevents — is
   * SUBSTITUTING a different tenant for the one nobody asked for.
   *
   * @returns The selector, or null to send none.
   */
  private resolvePortalSelector(): LoginPortalSelector | null {
    const raw: string | null = this.readQueryParameter(PORTAL_ID_QUERY_KEY);

    // An explicit presence test. Never `if (raw)`, which would discard the string '0'.
    if (raw === null) {
      return null;
    }

    // MIGRATION: an explicit coercion the Option Strict asymmetry forces, written as a whole-string
    // grammar check rather than a parse. The legacy pages compiled with strict conversion OFF
    // (`Website/release.config:L125`) and read request values as numbers with no conversion written
    // down at all; the target states the accepted language instead. Anchored at both ends, so no
    // prefix, suffix, surrounding space, radix marker, exponent, separator or decimal point can
    // reach the conversion below.
    if (!SIGNED_DECIMAL_INTEGER.test(raw)) {
      return null;
    }

    // Total on this input by construction: the string is known to be a sign followed by digits, so
    // `Number` cannot answer NaN here. The safe-integer test is what remains to be decided.
    const parsed: number = Number(raw);

    if (!Number.isSafeInteger(parsed)) {
      return null;
    }

    return { portalId: parsed };
  }

  // -------------------------------------------------------------------------
  // FOCUS MANAGEMENT
  // -------------------------------------------------------------------------

  /**
   * Places the initial focus, reproducing `Login.ascx.vb:L126-L130`.
   *
   * The account-name box when it is empty, and the password box otherwise — which is the
   * behaviour that makes a link carrying a seeded account name useful, because the person
   * lands on the only box still to fill in.
   */
  private focusFirstCredentialField(): void {
    if (this.form.controls.username.value.length === 0) {
      this.focusControl(LOGIN_CONTROL_IDS.username, 'username');

      return;
    }

    this.focusControl(LOGIN_CONTROL_IDS.password, 'password');
  }

  /**
   * Moves focus to the first control the person still has to correct.
   *
   * Used when a submission is refused before anything is sent. It costs nothing visually
   * and it is the difference between a form that reports a problem and a form that helps
   * fix it, particularly for someone who cannot see the message appear.
   */
  private focusFirstInvalidField(): void {
    if (this.form.controls.username.invalid) {
      this.focusControl(LOGIN_CONTROL_IDS.username, 'username');

      return;
    }

    if (this.store.verificationRequired() && this.form.controls.verificationCode.invalid) {
      this.focusControl(LOGIN_CONTROL_IDS.verificationCode, 'verificationCode');

      return;
    }

    if (this.form.controls.password.invalid) {
      this.focusControl(LOGIN_CONTROL_IDS.password, 'password');
    }
  }

  /**
   * Responds to a refusal from the server.
   *
   * The store has already recorded the problem document and advanced the verification
   * ladder, and the template renders both; the only work left is where the caret goes.
   *
   * When a code is being asked for, focus goes to the verification box — including on a
   * SECOND refusal, where the revealed-state signal has not changed and so the validator
   * effect does not run again. Otherwise it goes to the password box, which is the legacy
   * choice for a screen whose account name is already filled in.
   *
   * ⚠ THE PASSWORD IS NOT CLEARED and it is not re-populated either. The legacy screen kept
   * it across the postback by writing it into an HTML attribute
   * (`Login.ascx.vb:L123`); that mechanism is a credential-exposure smell and is not
   * carried forward, but the OUTCOME the person experienced — the value still being there —
   * is simply what a single-page form does without any help. Nothing here reads, copies or
   * logs the value.
   */
  private handleRefusedAttempt(): void {
    if (this.store.verificationRequired()) {
      this.focusControl(LOGIN_CONTROL_IDS.verificationCode, 'verificationCode');

      return;
    }

    this.focusControl(LOGIN_CONTROL_IDS.password, 'password');
  }

  /**
   * Focuses one of this screen's controls, if it is currently rendered.
   *
   * The search is scoped to this component's own element, so it cannot reach a same-named
   * control elsewhere on the page, and it matches on either the control's identifier or its
   * form-control name — both of which the paired template sets on the same element — so it
   * keeps working whichever binding form that template uses.
   *
   * ⚠ THE RESULT IS NARROWED, NEVER ASSERTED. A document query returns an element or null,
   * and a control behind a template condition genuinely may not be rendered. The instance
   * test both proves presence and gives access to the focus method; a non-null assertion
   * would compile and then fail at run time on exactly the case this handles.
   *
   * @param controlId The control's `id`, from {@link LOGIN_CONTROL_IDS}.
   * @param controlName The control's name within {@link LoginFormModel}.
   */
  private focusControl(controlId: string, controlName: keyof LoginFormModel): void {
    const candidate = this.hostElement.nativeElement.querySelector(
      `#${controlId}, [formControlName="${controlName}"]`,
    );

    if (candidate instanceof HTMLElement) {
      candidate.focus();
    }
  }
}
