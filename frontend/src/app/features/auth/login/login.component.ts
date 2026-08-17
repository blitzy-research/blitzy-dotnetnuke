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
  untracked,
} from '@angular/core';
import type { OnInit, Signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import type { AbstractControl, ValidationErrors } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { merge } from 'rxjs';

import { SIGN_IN_ROUTE } from '../../../core/config/app-routes.config';
import type { LoginRequest } from '../../../core/models/auth.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { LoginPortalSelector } from '../../../core/utils/http-params.util';
// This message moved from `core/services/auth.service` to the store when the store took ownership of the
// sign-out policy. The transport now propagates a refused revocation instead of absorbing it, so the
// wording belongs beside the state that records the refusal.
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

// THE QUERY PARAMETERS THIS SCREEN READS

export const RETURN_URL_QUERY_KEY = SHARED_RETURN_URL_QUERY_KEY;

export const USERNAME_QUERY_KEY = 'username';

/**
 * The query key naming the tenant being signed in to. ⚠ SPELLED AS THE SERVER SPELLS IT, because the
 * value is forwarded to the endpoint under this exact name. The sign-in endpoint binds the tenant from an
 * optional query parameter called `portalId`, and it consults it ONLY when the request host matched no
 * configured alias — the host takes precedence, so naming a tenant does not override one that resolved.
 */
export const PORTAL_ID_QUERY_KEY = 'portalId';

/**
 * The complete grammar a tenant selector must satisfy, anchored at both ends. An optional single leading
 * sign followed by one or more decimal digits, and NOTHING ELSE — no surrounding whitespace, no radix
 * prefix, no exponent, no decimal point, no digit separator and no trailing text.
 */
export const SIGNED_DECIMAL_INTEGER = /^[+-]?\d+$/;

export const VERIFICATION_CODE_QUERY_KEY = 'verificationcode';

export const DEFAULT_SIGNED_IN_ROUTE = '/';

// ---------------------------------------------------------------------------
// THE CONTROL IDENTIFIERS
// ---------------------------------------------------------------------------

/**
 * The `id` attribute of each control on this form. Declared here rather than written inline in the
 * template because THREE separate consumers must agree on them and only one of the three is the template.
 */
export const LOGIN_CONTROL_IDS = Object.freeze({
  /** The account-name box. */
  username: 'login-username',

  /** The password box. */
  password: 'login-password',

  /** The verification-code box. */
  verificationCode: 'login-verification-code',
});

// ---------------------------------------------------------------------------
// THE SERVER'S FIELD KEYS
// ---------------------------------------------------------------------------

/**
 * The model-state key the server reports each field's validation failures under. Pascal-cased, because
 * these name model members on the server rather than members of the serialised request body, and the
 * serialiser's camel-casing policy does not apply to them.
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
 * The message shown beside a control the person left empty. MIGRATION: these three sentences are NET-NEW,
 * and that is stated plainly rather than dressed up as a port.
 */
export const LOGIN_REQUIRED_MESSAGES = Object.freeze({
  /** Shown when the account-name box is empty. */
  username: 'User Name is required.',

  /** Shown when the password box is empty. */
  password: 'Password is required.',

  /** Shown when the verification-code box is empty while a code is being asked for. */
  verificationCode: 'Verification Code is required.',
});

/** The longest account name the sign-in contract accepts. */
export const LOGIN_USERNAME_MAX_LENGTH = 100;

/**
 * The largest credential the sign-in contract accepts, counted in UTF-8 BYTES. ⚠ BYTES, NOT CHARACTERS,
 * AND THE DIFFERENCE IS OBSERVABLE. The server measures with `Encoding.UTF8.GetByteCount`, so a
 * credential of emoji costs four bytes a character and 65 of them exceed this bound while numbering 65
 * characters.
 */
export const LOGIN_PASSWORD_MAX_BYTES = 256;

/** The error key the account-name length rule reports. */
const USERNAME_TOO_LONG_ERROR = 'usernameTooLong';

/** The error key the blank-account-name rule reports. */
const USERNAME_BLANK_ERROR = 'usernameBlank';

/** The error key the credential byte-length rule reports. */
const PASSWORD_TOO_LONG_ERROR = 'passwordTooLong';

/**
 * Wording for each bound this screen enforces ahead of the server. The two length sentences are the
 * SERVER'S OWN, reproduced verbatim, so that a person who trips the bound before the request leaves reads
 * exactly what they would have read had it left — the alternative is two sentences for one rule,
 * differing by which layer noticed first.
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
 * @param control The account-name control.
 * @returns The length error, or `null`.
 */
function usernameLengthValidator(control: AbstractControl<string>): ValidationErrors | null {
  return control.value.length > LOGIN_USERNAME_MAX_LENGTH
    ? { [USERNAME_TOO_LONG_ERROR]: true }
    : null;
}

/**
 * Refuses a credential exceeding the contract's UTF-8 byte ceiling. Counted with `TextEncoder`, which is
 * the platform's UTF-8 encoder and therefore agrees with `Encoding.UTF8.GetByteCount` by construction.
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

/**
 * The refusals that stop being true the moment the credential boxes change.
 *
 * ⚠ A CLOSED SET, AND THE CLOSURE IS THE POINT. A refused sign-in leaves an assertive banner on screen,
 * and it was left there while the person typed a correction - so the screen went on asserting that the
 * credentials were wrong about a pair of boxes that no longer held them. But only SOME refusals go stale
 * that way. These two describe THE VALUES THAT WERE SUBMITTED, so changing a value withdraws the evidence
 * for them. Every other refusal this screen can receive describes something an edit cannot alter:
 *
 * - `auth.locked_out` and `auth.account_not_approved` are about the ACCOUNT, and their advisories are
 *   standing information the person still needs while they retype.
 * - the verification rungs are about the CODE, and their sentence doubles as that field's own instruction -
 *   dismissing it would strip the label off the box being filled in.
 * - a rate-limited refusal is about the LIMITER, and dismissing it would stop the cool-off countdown early
 *   and re-enable a button the server is still refusing.
 * - a refusal carrying no document at all is a transport failure, which no keystroke repairs.
 */
/**
 * How much of the requested address is echoed back to the reader.
 *
 * ⚠ A BOUND ON ATTACKER-SUPPLIED TEXT. `returnUrl` is query text anyone can compose, and echoing an
 * unbounded string into the page would let a crafted link push a paragraph of the attacker's choosing onto a
 * sign-in screen. Interpolation makes it inert as MARKUP; this makes it inert as PROSE. The value is only
 * ever echoed after it has passed the same safety test that decides whether it is navigated to at all.
 */
export const ECHOED_ADDRESS_MAX_LENGTH = 80;

/**
 * The sentence explaining that the caller did not choose to be here.
 *
 * ⚠ THE EJECTION WAS COMPLETELY SILENT, AND THAT IS THE DEFECT THIS CLOSES. A caller who asked for a
 * screen that needs a signed-in account was moved to this one and told NOTHING - measured as both live
 * regions empty. The address they asked for was preserved in the query string and honoured after a
 * successful sign-in, so the behaviour was right; it was simply never explained, and a sign-in form
 * appearing unbidden reads as an error rather than as a step.
 *
 * @param address The address the caller asked for, already judged safe and already truncated.
 * @returns The sentence to render.
 */
export function ejectionNoticeFor(address: string): string {
  return (
    `You need to sign in to reach ${address}. Sign in below and you will be taken straight there.`
  );
}

/** The same explanation for an ejection whose requested address cannot be shown. */
export const EJECTION_NOTICE_WITHOUT_ADDRESS =
  'You need to sign in to reach the screen you asked for. Sign in below and you will be taken there.';

export const EDIT_INVALIDATED_FAILURE_CODES: readonly string[] = Object.freeze([
  /** The credential pair was rejected. */
  'auth.invalid_credentials',
  /** The sign-in service judged the request itself malformed - a statement about the submitted values. */
  'auth.request_invalid',
  /**
   * A validator refused the request, per field. ⚠ EDITING EITHER BOX WITHDRAWS THE WHOLE DOCUMENT, AND
   * THAT IS DELIBERATE ON THIS FORM SPECIFICALLY. A credential is the PAIR, not two values judged apart, so
   * a per-field message about one box stops being a finding about the submitted credential the moment the
   * other changes. A form of independent fields would not be treated this way, and is not.
   */
  'request.invalid',
]);

// ---------------------------------------------------------------------------
// THE FORM MODEL
// ---------------------------------------------------------------------------

/** The shape of the sign-in form. */
export interface LoginFormModel {
  /** The account name. */
  readonly username: FormControl<string>;

  /** The password. */
  readonly password: FormControl<string>;

  /**
   * The verification code. ⚠ EMPTY IS A VALUE HERE, NOT AN ABSENCE, and the distinction is measured. The
   * legacy branch at `Login.ascx.vb:L177` reads `If txtVerification.Text <> ""` to tell a wrong code from
   * a missing one, and the legacy absent-text sentinel IS the empty string —
   * `Library/Components/Shared/Null.vb:L71-L75` has the body `Return ""`, not `Return Nothing`.
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
 * The sign-in screen. Standalone, because the target declares no modules anywhere, and rendered with the
 * on-push strategy, because every value it publishes is a signal or a form and neither needs the default
 * check.
 */
/**
 * THE SUBTITLE, UNDER THE APPLICATION'S ONE SUBTITLE RULE. Every screen's header carries exactly one
 * subtitle stating that screen's SCOPE: the record it acts on when the title does not already name it,
 * and otherwise what the screen is for, in one line. It never carries a status, a count or a progress
 * readout - those belong to the live region that owns them, and a count in two places is two owners of
 * one fact. Measured finding: subtitles appeared on ten of the twenty screens and carried three
 * different kinds of thing, so a reader could not tell what the slot was for.
 */
const PAGE_SUBTITLE =
  'Sign in with your account for this site.';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    // The reactive-forms surface supplies the group and control directives. The four shared components are
    // the whole of this screen's design-system consumption: the page title, the labelled field wrapper, the
    // failure surface and the progress indicator.
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    ErrorBannerComponent,
    LoadingSpinnerComponent,
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
   * The session store: the authority for every piece of authentication state this screen shows, and the
   * only route by which it reaches the server.
   */
  private readonly store = inject(AuthStore);

  private readonly router = inject(Router);

  /** Read once, for the three query parameters this screen honours. */
  private readonly route = inject(ActivatedRoute);

  /**
   * Whether the sign-out that sent the operator here failed to end the session on the server. Shown as a
   * calm notice rather than as a refusal: nothing the operator did was rejected, and the local sign-out
   * did succeed.
   */
  /** The one-line scope statement shown beneath the title. */
  protected readonly pageSubtitle = PAGE_SUBTITLE;

  protected readonly revocationOutstanding: Signal<boolean> = this.store.revocationOutstanding;

  /**
   * The sentence shown when {@link revocationOutstanding} is true. Imported rather than authored here,
   * for the same reason the rate-limiter sentence is: one situation must not be described two different
   * ways depending on which layer noticed it.
   */
  protected readonly revocationMessage = REVOCATION_FAILED_MESSAGE;

  /**
   * This component's own element, used only to locate a control for focus. Scoping the search to the host
   * is what keeps focus management from reaching a same-named control elsewhere on the page.
   */
  private readonly hostElement = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * Supplied to {@link afterNextRender} when it is called outside an injection context. Required rather
   * than decorative: a control revealed by a template condition does not exist in the document at the
   * moment the state that reveals it changes, so focusing it has to wait for the render that follows.
   */
  private readonly injector = inject(Injector);

  /** Bounds the sign-in subscription to this component's lifetime. */
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------

  /** The sign-in form. */
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

  protected readonly usernameMaxLength = LOGIN_USERNAME_MAX_LENGTH;

  /**
   * The calm sentence shown when the rate limiter refuses the attempt. Taken from the shared form-errors
   * utility rather than authored here, because that module owns every user-facing failure sentence in the
   * workspace and the same situation must not be described two different ways depending on which layer
   * noticed it.
   */
  protected readonly rateLimitMessage = TOO_MANY_ATTEMPTS;

  /**
   * Seconds still to wait before another attempt is allowed, or `0` when none.
   *
   * ⚠ A REAL, ENFORCED COOL-OFF, WHICH DID NOT EXIST. The server answers a rate-limited sign-in with
   * `Retry-After`, and the screen discarded it: it said "Wait a moment and try again" without saying how
   * long, and left the button live, so a person could keep submitting - each attempt refused, and on a
   * server whose limiter counts refused attempts, each one pushing their own window further out. Counting
   * the window down and withholding the button for its duration turns an unquantified instruction into one
   * the screen actually helps with.
   */
  private readonly _coolOffRemaining = signal(0);

  protected readonly coolOffRemaining = this._coolOffRemaining.asReadonly();

  /** Whether a cool-off is currently in force. */
  protected readonly coolingOff: Signal<boolean> = computed(
    () => this._coolOffRemaining() > 0,
  );

  /**
   * The rate-limit sentence, with the remaining window when one is known.
   *
   * Falls back to the shared unquantified sentence when the server named no window - which is honest: this
   * screen must not invent a duration the server did not state.
   */
  protected readonly rateLimitNotice: Signal<string> = computed(() => {
    const remaining: number = this._coolOffRemaining();

    if (remaining <= 0) {
      return TOO_MANY_ATTEMPTS;
    }

    const unit: string = remaining === 1 ? 'second' : 'seconds';

    return `Too many attempts. Try again in ${remaining} ${unit}.`;
  });

  /** The handle of the running countdown, so a second refusal replaces it rather than racing it. */
  private coolOffTimer: ReturnType<typeof setInterval> | null = null;

  // STATE READ FROM THE STORE
  // Every member below is a read-only signal or a derivation over one. None is writable, so the template is
  // structurally unable to mutate authentication state, and the store remains the only thing that can.

  /**
   * Whether a sign-in request is in flight. The store's own phase projection for this command
   * specifically, rather than its general busy flag: a renewal running in the background must not disable
   * this form.
   */
  protected readonly submitting: Signal<boolean> = this.store.isAuthenticating;

  /**
   * The problem document from the last refused attempt, or null. Bound straight to the shared failure
   * banner, which takes the whole document and resolves its own title, sentence, per-field list and
   * support reference from it.
   */
  /**
   * The document handed to the shared banner, or `null` to leave the banner silent. ⚠ ONE OWNER PER
   * CATEGORY, AND THIS SCREEN HAD THREE. The banner is `role="alert"`, and beside it this screen rendered a
   * `role="status"` paragraph for a rate-limited attempt and a `role="alert"` paragraph for the credential
   * ladder - so a refusal carrying a problem document was announced twice, in two politeness levels, from
   * two regions.
   *
   * Ownership is decided by WHO HAS THE BETTER SENTENCE, not by who is nearer the top of the page. Where
   * this screen has wording of its own - the rate-limit sentence, or a rung of the measured credential
   * ladder - that wording is what a person needs and the paragraph beside the form is what states it, so
   * the banner is handed nothing. Where the screen has no sentence for the failure, {@link signInMessage}
   * is null, no paragraph renders, and the banner is the owner. The two are complementary by construction:
   * exactly one of them speaks.
   */
  protected readonly bannerProblem: Signal<ProblemDetails | null> = computed(() => {
    if (this.store.rateLimited() || this.signInMessage() !== null) {
      return null;
    }

    return this.store.problem();
  });

  protected readonly problem: Signal<ProblemDetails | null> = this.store.problem;

  /**
   * Whether the last attempt failed. Read instead of testing {@link LoginComponent.problem} for null,
   * because a failure can carry no document at all — a transport failure, or an intermediary answering on
   * its own behalf — and inferring failure from the presence of its details would report those cases as
   * success.
   */
  protected readonly failed: Signal<boolean> = this.store.hasFailure;

  /**
   * Whether the rate limiter refused the attempt because the caller is early. A DISTINCT, NON-ALARMING
   * STATE, kept apart from a refused credential deliberately: nothing has broken, the caller has simply
   * attempted too often.
   */
  protected readonly rateLimited: Signal<boolean> = this.store.rateLimited;

  /**
   * Whether the verification-code field has been revealed. ⚠ READ FROM THE STORE, NEVER MIRRORED.
   * `Login.ascx.vb:L171` branched on `If Not rowVerification1.Visible`, so the legacy ladder turned on
   * whether the field had ALREADY been revealed — state that survived the postback in Web Forms control
   * state.
   */
  protected readonly verificationRequired: Signal<boolean> = this.store.verificationRequired;

  /**
   * The form-level sentence for the last refusal, or null when there is none to add. ⚠ A REFUSAL IS NEVER
   * SILENT, and that is the whole reason this member exists.
   */
  protected readonly signInMessage: Signal<string | null> = computed(() => {
    if (this.store.hasFailure() === false) {
      return null;
    }

    if (this.store.rateLimited()) {
      return this.rateLimitMessage;
    }

    const ladderMessage = authFailureMessage(this.store.failureCode());

    // ⚠ THE BANNER IS CONSULTED BEFORE THE LADDER, AND THE ORDER IS THE FIX. It used to be the other way
    // round, which meant that whenever the server sent a problem document AND the code had ladder wording,
    // the same refusal was stated twice - and runtime testing measured the worst case: an unapproved account
    // produced the server's sentence in the banner's assertive region and the ladder's sentence in a second
    // assertive region, so the refusal was ANNOUNCED TWICE to a screen-reader user, in two different sets of
    // words for one fact.
    //
    // The verification rungs are deliberately exempt, and they are not an exception to "state it once". Their
    // sentence has a SECOND job: it labels the box the person must now fill in. The template renders that
    // copy `aria-hidden` with no role precisely because the field's own message is what gets announced, so
    // returning it here adds a visual echo rather than a second announcement.
    if (this.store.problem() !== null) {
      return this.signInMessageEchoedAtField() ? ladderMessage : null;
    }

    if (ladderMessage !== null) {
      return ladderMessage;
    }

    return statusMessage(this.store.failureStatus());
  });

  /**
   * Whether the form-level sentence is ALSO rendered beside the verification field. ⚠ THIS EXISTS SO THAT
   * ONE SENTENCE IS ANNOUNCED ONCE. Both copies are required and neither is a slip: the form-level line
   * is what the legacy screen showed, and the field-level copy is beside the control the person must
   * actually use, which is why {@link LoginComponent.verificationMessages} puts the ladder sentence at
   * the head of that field's list.
   */
  protected readonly signInMessageEchoedAtField: Signal<boolean> = computed(() => {
    if (this.store.rateLimited() || this.store.verificationRequired() === false) {
      return false;
    }

    return authFailureMessage(this.store.failureCode()) !== null;
  });

  /**
   * The identifier a person should quote when reporting the last failure, or null. Surfaced because it is
   * the only join key between something seen in the browser and the request as the server recorded it.
   */
  protected readonly supportReference: Signal<string | null> = this.store.supportReference;

  // -------------------------------------------------------------------------
  // WHY THE CALLER IS HERE
  // -------------------------------------------------------------------------

  /**
   * The address a gate ejected the caller from, or null when they came here of their own accord.
   *
   * A signal rather than the plain {@link LoginComponent.requestedReturnUrl} field beside it, because the
   * derivation below has to recompute when it is seated during initialisation. The plain field stays as the
   * unvalidated record the navigation logic reads; this holds only what is fit to SHOW.
   */
  private readonly _ejectedFrom = signal<string | null>(null);

  /**
   * The sentence explaining an ejection, or null when there is nothing to explain.
   *
   * Withheld once a refusal is on screen: the refusal is the more recent and more urgent fact, and stating
   * both would put two explanations of two different things in one slot. Withheld too when a withdrawal is
   * outstanding, whose own notice occupies the same position.
   */
  protected readonly ejectionNotice: Signal<string | null> = computed(() => {
    if (this.store.hasFailure() || this.store.revocationOutstanding()) {
      return null;
    }

    const address: string | null = this._ejectedFrom();

    if (address === null) {
      return null;
    }

    return address.length === 0 ? EJECTION_NOTICE_WITHOUT_ADDRESS : ejectionNoticeFor(address);
  });

  // -------------------------------------------------------------------------
  // THE FORM'S OWN STATE, MADE OBSERVABLE
  // -------------------------------------------------------------------------

  /**
   * The most recent state change reported by the form, as a signal. ⚠ THIS EXISTS SO THAT THE DERIVATIONS
   * BELOW ARE ACTUALLY REACTIVE, and leaving it out would produce a bug with no compile error.
   */
  private readonly formStateChanged = toSignal(this.form.events, { initialValue: null });

  /**
   * Whether a sign-in has been attempted from this screen. Gates the client-side required messages so
   * nothing is reported before the person has asked for anything, which matches the legacy screen: it
   * showed no validation feedback at all until the button was pressed, having no validator controls to
   * show it with.
   */
  private readonly submitAttempted = signal(false);

  // PER-FIELD MESSAGES

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
   * Messages for the verification-code box: the client's own, the ladder's, then the server's. The ladder
   * sentence belongs HERE rather than only above the form, because it is about this one field — it asks
   * for a code, or reports that the code given was wrong — and a message about a field belongs beside it.
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
        collected.unshift(ladderMessage);
      }
    }

    return collected;
  });

  // -------------------------------------------------------------------------
  // REACTIVE-TO-IMPERATIVE BRIDGE
  // -------------------------------------------------------------------------

  /**
   * Keeps the verification control's validator in step with the store's revealed state. THE ONE GENUINE
   * SIDE EFFECT IN THIS COMPONENT, and the reason it is an effect rather than a line in the failure
   * handler is worth stating.
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
   * Withdraws a refusal that the person has just invalidated by editing.
   *
   * Subscribed rather than derived because the outcome is an IMPERATIVE state change in the store, not a
   * value the template reads; and bound to the two credential controls specifically, so that typing into
   * the verification box - whose instruction lives in the very document being withdrawn - cannot trigger it.
   *
   * ⚠ THE PROGRAMMATIC CLEAR MUST NOT REACH THIS. {@link LoginComponent.discardRejectedPassword} empties
   * the box with `emitEvent: false` for exactly that reason: were it to emit, the screen would dismiss the
   * refusal in the same turn it was received and the person would never learn why the attempt failed.
   */
  private readonly staleRefusalWithdrawal = merge(
    this.form.controls.username.valueChanges,
    this.form.controls.password.valueChanges,
  )
    .pipe(takeUntilDestroyed())
    .subscribe(() => this.withdrawStaleRefusal());

  /**
   * Starts, restarts or stops the cool-off as the store's rate-limit state changes.
   *
   * An effect rather than a line in the failure handler for the same reason the validator sync above is
   * one: the fact lives in the store, and every path that can produce it - a submit, a retried submit -
   * must get the same treatment without each having to remember.
   */
  private readonly coolOffSync = effect(() => {
    const limited: boolean = this.store.rateLimited();
    const window: number | null = this.store.retryAfterSeconds();

    // ⚠ `untracked` IS LOAD-BEARING, NOT DEFENSIVE, AND OMITTING IT HANGS THE BROWSER. The helpers below
    // both READ `_coolOffRemaining`, so calling them in the effect's reactive context makes this effect
    // depend on the very signal the countdown writes each second. Every tick then re-runs the effect, which
    // restarts the window at its full length and replaces the interval - a countdown that never counts down
    // and a timer that is torn down and rebuilt forever. Reading the store above and doing the imperative
    // work outside the graph is the pattern the rest of this workspace uses for exactly this reason.
    untracked(() => {
      if (!limited || window === null || window <= 0) {
        // Either the refusal is gone or the server named no window. Either way there is nothing to count,
        // and a countdown left running from an earlier refusal must not outlive it.
        this.stopCoolOff();

        return;
      }

      this.startCoolOff(window);
    });
  });

  /**
   * Begins a countdown of the given length, replacing any already running.
   *
   * @param seconds The window the server advertised.
   */
  private startCoolOff(seconds: number): void {
    this.stopCoolOff();
    this._coolOffRemaining.set(seconds);

    this.coolOffTimer = setInterval(() => {
      const remaining: number = this._coolOffRemaining() - 1;

      if (remaining <= 0) {
        this.stopCoolOff();

        return;
      }

      this._coolOffRemaining.set(remaining);
    }, 1_000);

    // Registered per countdown rather than once in the constructor, so a component torn down mid-window
    // cannot leave an interval running against a destroyed signal.
    this.destroyRef.onDestroy(() => this.stopCoolOff());
  }

  /** Ends any countdown and clears the remaining time. */
  private stopCoolOff(): void {
    if (this.coolOffTimer !== null) {
      clearInterval(this.coolOffTimer);
      this.coolOffTimer = null;
    }

    if (this._coolOffRemaining() !== 0) {
      this._coolOffRemaining.set(0);
    }
  }

  /**
   * The return address exactly as it arrived, before it is judged safe. Held unvalidated on purpose, so
   * that the validation happens at the point of use and a specification can exercise the guard directly.
   */
  private requestedReturnUrl: string | null = null;

  // -------------------------------------------------------------------------
  // LIFECYCLE
  // -------------------------------------------------------------------------

  /**
   * Prepares the screen: honours an existing session, seeds the legacy query parameters and places the
   * initial focus. A faithful reproduction of `Login.ascx.vb:L103-L135`, in the legacy order.
   */
  ngOnInit(): void {
    this.driveOutstandingWithdrawals();

    this.requestedReturnUrl = this.readQueryParameter(RETURN_URL_QUERY_KEY);
    this._ejectedFrom.set(this.resolveEchoableAddress());

    if (this.store.isAuthenticated()) {
      this.leaveForReturnUrl();

      return;
    }

    this.seedFromQueryParameters();

    afterNextRender(() => this.focusFirstCredentialField(), { injector: this.injector });
  }

  /**
   * Drives any sign-out withdrawal the server never acknowledged. ⚠ THIS SCREEN IS THE PRODUCTION CALLER
   * OF A MECHANISM THAT HAD NONE. Signing out discards the session immediately, so a withdrawal refused
   * by a rate limit, an outage or a dropped connection leaves the renewal credential live on the server;
   * the store retains that credential precisely so a later attempt is possible, and a security review
   * found that no part of the application ever made one.
   */
  private driveOutstandingWithdrawals(): void {
    this.store.retryOutstandingRevocation().subscribe();
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  /**
   * Submits the credentials. Bound to the paired template's form submission, which is what carries the
   * legacy return-key behaviour across: `Login.ascx.vb:L101` had to register a key capture because a Web
   * Forms button needed one, whereas a real form with a submit-typed button does it natively.
   */
  protected submit(): void {
    // The in-flight guard comes first, so a repeat submission cannot even mark the form.
    if (this.store.isAuthenticating()) {
      return;
    }

    this.submitAttempted.set(true);

    this.form.markAllAsTouched();

    if (this.form.invalid) {
      this.focusFirstInvalidField();

      return;
    }

    const request: LoginRequest = {
      username: this.form.controls.username.value,
      password: this.form.controls.password.value,
      verificationCode: this.form.controls.verificationCode.value,
    };

    // Subscribed exactly once, which is what issues the request: the store's commands are cold by design so
    // that a command nobody subscribed to changes no state.
    this.store
      .login(request, this.resolvePortalSelector())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.discardCredential();
          this.leaveForReturnUrl();
        },
        error: () => this.handleRefusedAttempt(),
      });
  }

  /**
   * Dismisses the failure currently on screen. Offered so the paired template can give the banner a
   * dismiss affordance.
   */
  protected dismissFailure(): void {
    this.store.clearError();

    afterNextRender(() => this.focusFirstCredentialField(), { injector: this.injector });
  }

  /**
   * Whether a control should be reported to assistive technology as rejected. Exposed for the paired
   * template to bind to `[attr.aria-invalid]` on the control it projects.
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
   * Collects every message that applies to one control, client-side first. Reads {@link
   * LoginComponent.formStateChanged} so that the calling derivation has a dependency which genuinely
   * changes when the form does — see that member for why omitting it would silently freeze the result.
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
      // ⚠ THE MESSAGE IS CHOSEN BY WHICH RULE FAILED, not by the control being invalid.
      collected.push(this.boundMessageFor(control) ?? requiredMessage);
    }

    collected.push(...fieldErrorMessages(this.store.problem(), serverFieldKey));

    return collected;
  }

  /**
   * The sentence for whichever bound the control has broken, or `null` when none has.
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
   * Leaves this screen for the requested address, falling back to the default one when the requested
   * address turns out to be one this account may not enter. ⚠ THE OUTCOME OF THE NAVIGATION IS NOW READ,
   * AND THAT CLOSES A MEASURED DEFECT. The return address is supplied by whichever gate INTERRUPTED the
   * operator, and that gate only establishes that they were not signed in — not that they are permitted
   * the address once they are.
   */
  private leaveForReturnUrl(): void {
    const requested = this.resolveReturnUrl();

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
   * Empties the credential controls the moment a session is held. ⚠ THE PASSWORD MUST NOT OUTLIVE ITS
   * USE, and it did. A review measured the account name and sixteen masked characters still present in
   * the DOM after a SUCCESSFUL authentication, on a screen the operator had no further use for.
   */
  private discardCredential(): void {
    this.form.controls.password.reset('');
    this.form.controls.username.reset('');
    this.submitAttempted.set(false);
  }

  /**
   * The address to navigate to after signing in.
   *
   * @returns The requested address when it is safe, and the default landing address otherwise.
   */
  private resolveReturnUrl(): string {
    const requested = this.requestedReturnUrl;

    return this.isSafeReturnUrl(requested) ? requested : DEFAULT_SIGNED_IN_ROUTE;
  }

  /**
   * Whether a requested return address may be navigated to. ⚠ THIS IS AN OPEN-REDIRECT GUARD AND IT IS
   * MANDATORY. The value arrives in a query parameter, so anyone can choose it — a link in an e-mail, a
   * message, another site — and an unchecked value would let this screen send a person who has just
   * signed in to an attacker's page carrying a convincing copy of it.
   *
   * @param candidate The value as it arrived, or null when the parameter was absent.
   * @returns True when the value is an address inside this application.
   */
  /**
   * The requested address in a form fit to show the reader, or null when there was no ejection.
   *
   * ⚠ REUSES {@link LoginComponent.isSafeReturnUrl} RATHER THAN RE-DECIDING SAFETY. Two independent
   * judgements of the same attacker-supplied string is how a screen ends up displaying an address it would
   * refuse to navigate to. An address that fails the test yields the empty string - which the derivation
   * reads as "ejected, but say so without quoting it" - so a crafted value still produces an explanation
   * rather than silence.
   *
   * @returns The address to echo, the empty string for an unshowable one, or null for no ejection at all.
   */
  private resolveEchoableAddress(): string | null {
    const requested = this.requestedReturnUrl;

    if (requested === null || requested.length === 0) {
      return null;
    }

    if (this.isSafeReturnUrl(requested) === false) {
      return '';
    }

    // Landing on the default address is not an ejection worth narrating: it is where an unprompted sign-in
    // leads anyway, so explaining it would state the obvious to someone who chose to be here.
    if (requested === DEFAULT_SIGNED_IN_ROUTE) {
      return null;
    }

    return requested.length > ECHOED_ADDRESS_MAX_LENGTH ? '' : requested;
  }

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

  /** Seeds the form from the two legacy query parameters. Both are optional and both are untrusted. */
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
   * Removes the seeded account name and verification code from the browser's address. ⚠ AN ADDRESS IS NOT
   * A PRIVATE CHANNEL, WHICH IS WHY THIS EXISTS. A query string is persisted in the browser's own
   * history, is offered by the address bar's autocomplete to whoever next uses the machine, is handed to
   * third-party origins in the `Referer` header of any subsequent request, and is the single most
   * commonly recorded part of a request in proxy and server access logs.
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
   * Reads one query parameter from the activation snapshot. ⚠ PRESENCE, NOT EMPTINESS. The legacy guards
   * are `If Not Request.QueryString("…") Is Nothing` at `Login.ascx.vb:L106` and L109, so a parameter
   * that is present but empty was still present — and it still is here. A test for a non-empty value
   * would be a different test with a different outcome, and this one is deliberately the legacy one.
   *
   * @param key The query key, spelled as the producer spells it.
   * @returns The value, or null when the parameter is absent.
   */
  private readQueryParameter(key: string): string | null {
    return this.route.snapshot.queryParamMap.get(key);
  }

  /** @returns The selector, or null to send none. */
  private resolvePortalSelector(): LoginPortalSelector | null {
    const raw: string | null = this.readQueryParameter(PORTAL_ID_QUERY_KEY);

    // An explicit presence test. Never `if (raw)`, which would discard the string '0'.
    if (raw === null) {
      return null;
    }

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
   * Places the initial focus, reproducing `Login.ascx.vb:L126-L130`. The account-name box when it is
   * empty, and the password box otherwise — which is the behaviour that makes a link carrying a seeded
   * account name useful, because the person lands on the only box still to fill in.
   */
  private focusFirstCredentialField(): void {
    if (this.form.controls.username.value.length === 0) {
      this.focusControl(LOGIN_CONTROL_IDS.username, 'username');

      return;
    }

    this.focusControl(LOGIN_CONTROL_IDS.password, 'password');
  }

  /**
   * Moves focus to the first control the person still has to correct. Used when a submission is refused
   * before anything is sent.
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
   * Responds to a refusal from the server. The store has already recorded the problem document and
   * advanced the verification ladder, and the template renders both; the only work left is where the
   * caret goes.
   */
  private handleRefusedAttempt(): void {
    this.discardRejectedPassword();

    if (this.store.verificationRequired()) {
      this.focusControl(LOGIN_CONTROL_IDS.verificationCode, 'verificationCode');

      return;
    }

    this.focusControl(LOGIN_CONTROL_IDS.password, 'password');
  }

  /**
   * Empties the password box after a refusal, as the legacy screen did.
   *
   * ⚠ THIS IS NOT A NEW BEHAVIOUR, IT IS A RESTORED ONE. `Login.ascx` declared the box as
   * `<asp:TextBox TextMode="Password">`, and ASP.NET deliberately never re-renders such a control's value
   * across a postback - so every refused legacy sign-in came back with the box empty. The SPA holds the
   * value client-side, so the value survived by default: the rejected password sat there, with the caret
   * placed in it, and a person correcting a typo had to clear it first. Restoring the clear also stops a
   * rejected password lingering in the DOM of an unattended screen.
   *
   * `emitEvent: false` keeps this out of {@link LoginComponent.staleRefusalWithdrawal}, and lowering
   * {@link LoginComponent.submitAttempted} keeps the client's `required` rule from reporting an emptiness
   * THE SCREEN caused as something the person omitted. The next submission raises it again.
   */
  private discardRejectedPassword(): void {
    this.form.controls.password.setValue('', { emitEvent: false });
    this.form.controls.password.markAsUntouched();
    this.submitAttempted.set(false);
  }

  /**
   * Discards the recorded refusal when the edit just made invalidates it, leaving the caret where it is.
   *
   * Never routed through {@link LoginComponent.dismissFailure}, which moves focus: this runs while the
   * person is typing, and taking the caret out of the box mid-word would be worse than the stale banner.
   */
  private withdrawStaleRefusal(): void {
    if (this.store.hasFailure() === false) {
      return;
    }

    // An attempt in flight owns the banner; withdrawing it here would race the response.
    if (this.store.isAuthenticating()) {
      return;
    }

    const code: string | null = this.store.failureCode();

    if (code === null || EDIT_INVALIDATED_FAILURE_CODES.includes(code) === false) {
      return;
    }

    this.store.clearError();
  }

  /**
   * Focuses one of this screen's controls, if it is currently rendered.
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
