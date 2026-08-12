import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import type { OnInit, Signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import type { ProblemDetails } from '../../../../core/models/problem-details.model';
import type { MemberService } from '../../../../core/models/user.model';
import { AuthStore } from '../../../../core/state/auth.store';
import type { UserOperation } from '../../../../core/state/user.store';
import { UserStore } from '../../../../core/state/user.store';
import { fieldErrorMessage } from '../../../../core/utils/form-errors.util';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../../shared/components/data-table/data-table.component';
import { DataTableComponent } from '../../../../shared/components/data-table/data-table.component';
import { ErrorBannerComponent } from '../../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../../shared/components/form-field/form-field.component';
import { DateDisplayPipe } from '../../../../shared/pipes/date-display.pipe';
import { FocusFirstInvalidDirective } from '../../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../../shared/directives/submit-guard.directive';

// ---------------------------------------------------------------------------
// WHAT THIS SCREEN IS
// ---------------------------------------------------------------------------
//
// The account holder's own subscriptions: the services this site offers, what the
// account already holds, and the four gestures the legacy panel offered - subscribe,
// unsubscribe or renew, take a trial, and redeem an invitation code.
//
// MIGRATION: this replaces `Website/admin/Users/MemberServices.ascx` (77 lines) and its
// 530-line code-behind, which the transformation plan maps into this feature folder. An
// earlier revision of the sibling settings screen recorded the workflow as omitted on
// the grounds that "role subscription, invitation codes, trials and billing transactions
// have no endpoint in this API at all"; that was true of the API as it then stood and is
// no longer, so the omission notice is withdrawn and this screen is the workflow.
//
// ⚠ WHERE THIS PANEL LIVES, AND WHY IT HAS NO ADDRESS OF ITS OWN. It is MOUNTED BY the
// tenant's account-policy screen at `/settings/membership` and is not routed. The
// migration plan is the authority for both halves of that: AAP 0.5.1.8 maps
// `Website/admin/Users/MemberServices.ascx.vb` into THIS folder as one of three legacy
// screens consolidated into the membership-settings feature, and AAP 0.4.4 freezes the
// console's route table at twenty-five addresses and names no member-services address
// among the seven it enumerates for accounts. An intermediate revision published this
// panel at `/users/:userId/services` and a later one deleted the panel outright; the
// first added a twenty-sixth address the plan does not carry and the second withdrew a
// capability the plan requires, so neither is what the plan describes. Mounting it inside
// the mapped screen satisfies both constraints at once, and no route is added.
//
// ⚠ THE CONSEQUENCE IS RECORDED RATHER THAN ABSORBED: every address in the closed route
// table that could host this panel is gated on tenant administration, so in this console
// the panel is reachable by a caller admitted to `/settings/membership` and by nobody
// else. The five endpoints stay gated on ACCOUNT OWNERSHIP server-side, so what such a
// caller manages is strictly its OWN subscriptions - an administrator cannot reach
// somebody else's through this panel. A self-service surface for ordinary members is a
// different product surface from an administration console, and it is not in the plan.
// `MIGRATION_NOTES.md` carries this as a deliberate divergence.
//
// ⚠ THE ACCOUNT IS ALWAYS THE CALLER'S OWN, AND THAT IS MEASURED RATHER THAN CHOSEN. The
// legacy container assigned the panel a user identifier (`ManageUsers.ascx.vb:L517`) and
// the panel never read it: subscribe, cancel, trial and redemption each passed
// `UserInfo.UserID`, which `PortalModuleBase.vb:L319-L323` resolves as the SIGNED-IN
// account. The container agreed - `DisplayServices` at `ManageUsers.ascx.vb:L61-L66` is
// the tenant's `Profile_ManageServices` switch AND NOT (`IsEdit` OR `User.IsSuperUser`),
// and `IsEdit` (`UserModuleBase.vb:L329-L340`) is the administrative `ctl=Edit` entry
// point - so the tab appeared on an account's OWN screen and never on the administrative
// edit of somebody else's. The subject account therefore arrives as an INPUT from the
// host, which derives it from the session, and this panel additionally declines to issue
// a request for an account the caller does not hold rather than provoking five refusals
// it can predict.
//
// MIGRATION: the tenant's self-service switch is enforced by the SERVER, not by hiding
// this screen. The legacy container folded that setting into the tab's visibility; here
// every one of the five endpoints checks it and refuses with its own reason, which the
// failure banner renders in the server's words. The screen cannot read the setting
// itself - the account-policy endpoint is gated on tenant administration, so the very
// caller this screen exists for would be refused it - and a client-side copy of a
// server-side rule would be a second opinion about it in any case.
//
// MIGRATION: EVERY STRING HERE IS INTERPOLATED PLAIN TEXT. Measured across the 37
// in-scope resource files, 76 values carry raw markup and four carry a live script
// element, so legacy wording is untrusted markup by measurement. No trusted-markup
// binding, no sanitiser and no bypass appears in this component or its template.
//
// MIGRATION: localisation is not ported; there is no translation runtime in the pinned
// dependency set. The wording below is recovered from
// `Website/admin/Users/App_LocalResources/MemberServices.ascx.resx` so that labels stay
// recognisable, and every deviation from it is annotated where it occurs.
// ---------------------------------------------------------------------------

/**
 * The panel's heading.
 *
 * `cmdServices.Text` in `Website/admin/Users/App_LocalResources/ManageUsers.ascx.resx`, which
 * is the caption of the tab this panel replaces. The panel's own resource file declares no
 * title at all - it was a tab inside a container that supplied one - so the tab's caption is
 * the authoritative wording rather than an invention.
 *
 * Rendered as a level-two heading inside the host screen rather than through the shared page
 * header: the host already emits the page's level-one heading, and a second page header would
 * both duplicate that landmark and skip no level while claiming to be a page of its own.
 */
const SECTION_TITLE = 'Manage Services';

/**
 * The explanatory paragraph above the code field.
 *
 * `ServicesHelp.Text`, with ONE documented change. The legacy value ends "Some services may
 * require payment. If this is the case you will be redirected to a payment site. When you
 * return to this site, you can check back here to view your subscription." There is no payment
 * site to be redirected to: the transformation plan excludes sales administration, so the API
 * refuses a fee-bearing service rather than handing it to a processor. Reproducing the legacy
 * sentence would therefore promise a redirection that cannot happen, so the last two sentences
 * are replaced by an accurate statement of what does happen. The rest is the legacy wording,
 * with its two embedded break tags dropped because this is plain text in a paragraph element.
 */
const SERVICES_HELP =
  'This section allows you to manage your subscriptions on the site. You can subscribe to ' +
  'some Services by entering an RSVP Code. If you have been given a special RSVP code you ' +
  'can subscribe to these Services by entering the code in the RSVP Code field and clicking ' +
  'the "Subscribe" button next to the field. To manage the other subscription services ' +
  'provided by this site you can use the grid below. A service that charges a fee is listed ' +
  'here for reference but cannot be subscribed to or cancelled on this site.';

/** `plRSVPCode.Text`. The shared field component strips the trailing colon. */
const CODE_LABEL = 'Enter RSVP Code:';

/** `plRSVPCode.Help`, verbatim. */
const CODE_HELP =
  'You can subscribe to some Services by entering an RSVP Code.  If you have been given a ' +
  'code please enter it and click Subscribe';

/** `cmdRSVP.Text`. The submit caption beside the code field, which is not the row command. */
const CODE_SUBMIT = 'Subscribe';

/**
 * `RSVPSuccess.Text`, verbatim.
 *
 * ⚠ ITS CLOSING INSTRUCTION IS STILL TRUE HERE, WHICH IS WHY IT IS KEPT. The sentence tells
 * the operator to sign out and back in before the new services take effect. Role membership
 * reaches this client as claims on the access token, so a role joined a moment ago is not in
 * the token in hand; the advice is as accurate for a bearer token as it was for a forms
 * authentication cookie.
 */
const CODE_SUCCESS =
  'You have been successfully added to the role(s) associated with the RSVP Code entered. In ' +
  'order to get access to the new services you will need to Logout and then Login to the site ' +
  'again.';

/**
 * The wording for a code field left empty.
 *
 * Reads the way the API's own validator reads - `RedeemServiceCodeRequestValidator`'s
 * `CodeRequiredMessage` - because the same situation should not be described two different ways
 * depending on which side noticed it. The legacy screen had no message for this case at all: it
 * silently ignored an empty submission, which is the behaviour the API deliberately does not
 * reproduce.
 *
 * MIGRATION: the sentence NAMES THE FIELD THE WAY THE FIELD IS LABELLED. Both this message and the
 * server's read "An invitation code is required." while the control immediately above them read
 * "Enter RSVP Code:", so one value carried two names and an operator had to infer that the refusal
 * was even about the box they had just filled in. Nothing in the legacy forced either wording - its
 * guard was silent - so the field's own label is the only authority, and both sides now follow it.
 */
const CODE_REQUIRED_MESSAGE = 'An RSVP Code is required.';

/**
 * The maximum length of an invitation code.
 *
 * `MemberServices.ascx:L14` declares `maxlength="50"` and the API's own validator bounds the
 * same value at fifty, which is the width of `Roles.RSVPCode`. Stated here so the field
 * refuses locally what the server would refuse remotely, rather than as a substitute for it.
 */
const CODE_MAX_LENGTH = 50;

/** Column headings, exactly as the panel's resource file declares them. */
const NAME_HEADING = 'Name';
const DESCRIPTION_HEADING = 'Description';
const FEE_HEADING = 'Service Fee';
const TRIAL_HEADING = 'Trial Fee';
const EXPIRY_HEADING = 'Expiry Date';

/** `Subscribe.Text`, `Unsubscribe.Text` and `Renew.Text` - the three row commands. */
const SUBSCRIBE_LABEL = 'Subscribe';
const UNSUBSCRIBE_LABEL = 'Unsubscribe';
const RENEW_LABEL = 'Renew';

/** `UseTrial.Text`. */
const TRIAL_LABEL = 'Use Trial';

/** `Expired.Text`, rendered in the expiry column in place of a date. */
const EXPIRED_LABEL = 'Expired';

/** `NoFee.Text`. */
const NO_FEE_LABEL = 'Free';

/**
 * The phrase shown where the legacy rendered a link to a payment site.
 *
 * NET-NEW WORDING, and unavoidably so: the legacy screen had no such state because it had
 * somewhere to send the operator. It is deliberately short, because the paragraph above the
 * grid explains it once for every row.
 */
const PAYMENT_REQUIRED_LABEL = 'Payment required';

/**
 * The wording shown when the address names an account that is not the caller's.
 *
 * NET-NEW: the legacy container expressed this by hiding the tab, which is not available to a
 * routed screen that has already been navigated to. Saying which screen serves the case is
 * more useful than a bare refusal.
 */
const NOT_OWN_ACCOUNT_MESSAGE =
  'Subscriptions are managed by their own account holder, so this panel shows your own ' +
  'services only. Role membership for another account is managed from that role.';

/**
 * The wording shown while the host has not resolved which account the caller holds.
 *
 * NET-NEW, and it describes a transient state rather than a refusal: the host derives the
 * subject account from the session, so an absent value means the session has not resolved yet
 * or resolved without an account. Nothing is read in that state, because there is no account
 * key to read for.
 */
const ACCOUNT_UNKNOWN_MESSAGE =
  'Your subscriptions will be shown here once your account has been identified.';

/**
 * The frequency vocabulary, resolved to the wording the legacy resource file declared.
 *
 * `Frequency_D.Text`, `Frequency_W.Text`, `Frequency_M.Text` and `Frequency_Y.Text`. Held as a
 * record rather than a switch so that an unknown stored code resolves to nothing and the
 * period is rendered without a unit, which is what the legacy did: `Localization.GetString`
 * answered the empty string for a key it did not hold, and the composed sentence still
 * rendered.
 *
 * ⚠ `N` AND `O` ARE ABSENT ON PURPOSE. They are not units: `N` means no charge and `O` means a
 * one-off charge, and both are handled before the unit is ever consulted.
 */
const FREQUENCY_UNITS: Readonly<Record<string, string>> = {
  D: 'Day(s)',
  W: 'Week(s)',
  M: 'Month(s)',
  Y: 'Year(s)',
};

/**
 * The five store operations this panel performs, and the only failures its banner renders.
 *
 * ⚠ WHY A CLOSED LIST RATHER THAN "WHATEVER FAILED LAST". The store holds ONE failure slot for
 * every account command, and this panel is mounted inside a screen that renders that slot too.
 * Filtering on the operation is what keeps one failure to one announcement: the host announces
 * its own policy save, this panel announces its own five, and neither speaks for the other.
 * Typed as the store's own operation union, so an operation renamed there stops compiling here
 * rather than silently dropping out of the filter.
 *
 * EXPORTED so the host screen filters on the SAME list rather than a second copy of it. Two
 * copies would let one drift, and the drift would be silent in the direction that matters: an
 * operation removed from the host's copy would have its failure announced twice.
 */
export const MEMBER_SERVICE_OPERATIONS: readonly UserOperation[] = [
  'loadMemberServices',
  'subscribeToService',
  'cancelService',
  'startServiceTrial',
  'redeemServiceCode',
];

/** The shape of the invitation-code form, declared so its value is fully typed. */
interface CodeFormModel {
  readonly code: FormControl<string>;
}

@Component({
  selector: 'app-member-services',
  standalone: true,
  // ⚠ EVERY SELECTOR AND PIPE THE PAIRED TEMPLATE USES MUST APPEAR HERE. Strict template
  // checking turns an element matching an unlisted component into a compilation error rather
  // than a silent unknown element.
  // The shared progress indicator is deliberately ABSENT: the grid renders its own waiting row
  // and lets waiting win over empty, so a second indicator here would either duplicate it or
  // flash an empty state before the first response. The delegation is a compile-time fact
  // rather than a convention, because a component not listed here cannot be rendered.
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    FormFieldComponent,
    ErrorBannerComponent,
    DataTableComponent,
    DateDisplayPipe,
    FocusFirstInvalidDirective,
  ],
  templateUrl: './member-services.component.html',
  styleUrl: './member-services.component.scss',
  // MANDATORY. Every value rendered here is a signal or a member of the form's own model,
  // both of which mark the view for checking, so the default strategy would only add
  // whole-tree traversals that can change nothing.
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MemberServicesComponent implements OnInit {
  /** The shared store. It owns every request, every held value and every failure. */
  private readonly store = inject(UserStore);

  /**
   * The session, read for ONE question: whether the supplied account is the caller's own.
   *
   * Never used to decide whether an operation is permitted - the server decides that, and
   * refuses with its own status and problem document. This is the same advisory reading the
   * credential screen performs, for the same reason: navigating an operator to a screen whose
   * every request would be refused is worse than saying so.
   */
  private readonly auth = inject(AuthStore);

  // -------------------------------------------------------------------------
  // THE HOST INPUT
  // -------------------------------------------------------------------------

  /**
   * The account whose services to manage, supplied by the host screen, or `null` when the
   * host has not resolved one.
   *
   * A SIGNAL input rather than a decorated field, because {@link accountKey} and
   * {@link ownership} derive from it and a derivation over a decorated field cannot know when
   * to recompute - it would resolve once and then ignore every later change. The host derives
   * the value from the session, which resolves asynchronously, so the input must be able to
   * change after the first render.
   *
   * ⚠ NULLABLE RATHER THAN REQUIRED-AND-NON-NULL, because "not yet known" is a real state and
   * the alternative encodings are both worse: a sentinel would collide with a genuine
   * identifier - zero and minus one are both real account keys somewhere in this schema - and
   * an absent binding would leave a required input unset and throw on the host's first render.
   */
  readonly accountId = input.required<number | null>();

  // -------------------------------------------------------------------------
  // CELL TEMPLATES
  // -------------------------------------------------------------------------
  //
  // Captured statically because the columns are built once, in the constructor, before the
  // first change-detection pass. Each is declared at the TOP LEVEL of the paired template,
  // outside every control-flow block, which is what makes a static capture possible.

  /** The subscribe, unsubscribe or renew command. Legacy column one. */
  @ViewChild('subscribeCommand', { static: true })
  private readonly subscribeCommandTemplate?: TemplateRef<DataTableCellContext<MemberService>>;

  /** The trial command. Legacy column two. */
  @ViewChild('trialCommand', { static: true })
  private readonly trialCommandTemplate?: TemplateRef<DataTableCellContext<MemberService>>;

  /** The expiry cell, which renders either a date or the lapsed word. Legacy column seven. */
  @ViewChild('expiryCell', { static: true })
  private readonly expiryCellTemplate?: TemplateRef<DataTableCellContext<MemberService>>;

  // -------------------------------------------------------------------------
  // LOCAL STATE
  // -------------------------------------------------------------------------

  /**
   * The seven columns, built once.
   *
   * Held in a signal so the table's input is a reactive source, and populated from the
   * constructor rather than at field initialisation because the templates above are only
   * available once the view has been created.
   */
  private readonly columnSet = signal<readonly DataTableColumn<MemberService>[]>([]);

  /**
   * Whether a redemption has been submitted and not yet settled.
   *
   * Held locally because the store's saving flag is shared by all four commands, and the code
   * field is cleared only for a redemption. Cleared on both outcomes.
   */
  private readonly redemptionSubmitted = signal<boolean>(false);

  // -------------------------------------------------------------------------
  // THE INVITATION-CODE FORM
  // -------------------------------------------------------------------------

  /**
   * The one-field form the legacy screen expressed as a text box and a link button.
   *
   * ⚠ THE CONTROL IS NON-NULLABLE AND ITS VALUE IS SENT AS TYPED. `nonNullable` makes the
   * value a `string` rather than `string | null`, so nothing downstream has to consider an
   * absent value; and nothing here trims or folds it, because the legacy comparison was
   * ordinary string equality against the stored code (`MemberServices.ascx.vb:L410`) and
   * trimming would admit codes the legacy application refused.
   *
   * Both validators mirror a server rule rather than inventing one: the code is required and
   * is bounded at fifty characters, which is the width of the column and the bound the API's
   * own validator applies.
   *
   * ⚠ NO UNSAVED-ENTRY PROBE IS REGISTERED FOR IT, AND THAT IS A DECISION RATHER THAN AN
   * OMISSION. The screens that register one with `UnsavedChangesTracker` all EDIT A RECORD, and
   * what the gate protects is a half-finished edit that navigation would discard in silence. The
   * only entry here is an invitation code on its way to being submitted: asking a reader to
   * confirm leaving because they typed a code and thought better of it would be new behaviour,
   * and the legacy screen protected nothing at all. The host screen registers its own probe over
   * the tenant policy form and its route declares the gate, so the entry that warrants
   * protection has it.
   */
  protected readonly codeForm = new FormGroup<CodeFormModel>({
    code: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(CODE_MAX_LENGTH)],
    }),
  });

  // -------------------------------------------------------------------------
  // WORDING, EXPOSED FOR THE TEMPLATE
  // -------------------------------------------------------------------------

  protected readonly sectionTitle = SECTION_TITLE;
  protected readonly servicesHelp = SERVICES_HELP;
  protected readonly codeLabel = CODE_LABEL;
  protected readonly codeHelp = CODE_HELP;
  protected readonly codeSubmit = CODE_SUBMIT;
  protected readonly codeSuccess = CODE_SUCCESS;
  protected readonly codeMaxLength = CODE_MAX_LENGTH;
  protected readonly trialLabel = TRIAL_LABEL;
  protected readonly expiredLabel = EXPIRED_LABEL;
  protected readonly paymentRequiredLabel = PAYMENT_REQUIRED_LABEL;
  protected readonly notOwnAccountMessage = NOT_OWN_ACCOUNT_MESSAGE;
  protected readonly accountUnknownMessage = ACCOUNT_UNKNOWN_MESSAGE;

  // -------------------------------------------------------------------------
  // DERIVED STATE
  // -------------------------------------------------------------------------

  /**
   * The account key the host supplied, or `null` when it supplied none.
   *
   * The one place an absent or unusable value becomes an explicit absence, so no consumer
   * repeats the test. A non-finite number is treated as absent for the same reason a null is:
   * there is no account to read for either way.
   */
  readonly accountKey: Signal<number | null> = computed(() => {
    const bound = this.accountId();

    return bound !== null && Number.isFinite(bound) ? bound : null;
  });

  /**
   * Whether the supplied account is the caller's own, and whether that is yet knowable.
   *
   * THREE STATES RATHER THAN A BOOLEAN, because the identity read is asynchronous: treating
   * "not yet resolved" as "not the owner" would flash the wrong-account notice on every
   * arrival, and treating it as "the owner" would issue a request for an account the caller
   * may not hold. The comparison is an explicit equality on the account key and never a
   * truthiness test.
   *
   * ⚠ THIS IS DEFENCE IN DEPTH AND NOT THE RULE. The host supplies the caller's own account,
   * and the server gates all five endpoints on account ownership; this test exists so that a
   * host binding some other account issues NO request rather than five doomed ones.
   */
  readonly ownership: Signal<'unknown' | 'owner' | 'other'> = computed(() => {
    const key = this.accountKey();
    const caller = this.auth.currentUser();

    if (key === null) {
      return 'unknown';
    }

    if (caller === null) {
      return 'unknown';
    }

    return caller.userId === key ? 'owner' : 'other';
  });

  /** The services offered to the account, as the store holds them. */
  /**
   * How a row identifies itself to the shared grid, so a re-read of the rows already shown reuses their
   * row elements instead of rebuilding them.
   *
   * ⚠ THE RECORD'S OWN KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT. The grid's fallback is the row
   * OBJECT, which is a correct key only while the same objects stay in play; every read from the server
   * decodes fresh objects, so without this a refetch presents entirely new keys and the whole body is
   * rebuilt to display records that never changed. `roleId` is unique by definition, being the
   * record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly serviceRowKey = (row: MemberService): number => row.roleId;

  protected readonly services: Signal<readonly MemberService[]> = computed(() => {
    // Guarded on the account the store's catalogue BELONGS TO, so a catalogue read for a
    // previous address can never be rendered under this one. The store publishes the account
    // alongside the rows precisely so this test is possible.
    const key = this.accountKey();

    return this.store.memberServicesAccountId() === key ? this.store.memberServices() : [];
  });

  /** The seven columns, for the table's input. */
  protected readonly columns = this.columnSet.asReadonly();

  /** Whether the catalogue read is outstanding. */
  protected readonly loading: Signal<boolean> = this.store.memberServicesLoading;

  /** Whether any command is in flight. Every command control is disabled while it is. */
  protected readonly busy: Signal<boolean> = computed(
    () => this.store.saving() || this.store.memberServicesLoading(),
  );

  /**
   * The failure the banner renders, in the server's own words - and ONLY a failure of this
   * panel's own five operations.
   *
   * ⚠ THE OPERATION FILTER IS LOAD-BEARING NOW THAT THIS PANEL IS MOUNTED INSIDE ANOTHER
   * SCREEN. The store holds ONE failure slot shared by every account command, and the host
   * screen renders it too; without this filter a refused policy save would be announced twice,
   * once by the host's banner and once here, and a refused redemption would appear at the top
   * of a screen that did not perform it. Each surface therefore renders the failures of its
   * own operations, which keeps exactly one announcement per failure.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null || !MEMBER_SERVICE_OPERATIONS.includes(failure.operation)) {
      return null;
    }

    return failure.problem;
  });

  /**
   * The roles the last redeemed code admitted the account to, or `null`.
   *
   * Rendered as the success half of the legacy screen's two-message report. The failure half
   * is the banner above, which shows the server's own wording rather than the legacy sentence
   * - the server states which of the two refusals occurred, and an empty code and an unknown
   * code are genuinely different mistakes.
   */
  protected readonly redeemedRoleNames: Signal<readonly string[] | null> = computed(() => {
    const joined = this.store.lastRedemption();

    return joined === null ? null : joined.roles.map((role) => role.roleName);
  });

  /**
   * Assembles the column set once the cell templates exist.
   *
   * ⚠ NOT IN THE CONSTRUCTOR. The three view queries above are declared static, which means
   * they resolve before this hook runs and NOT before the constructor does — building the
   * columns there raises the missing-template diagnostic on every mount. The sibling listing
   * screens assemble their columns in exactly this hook for exactly this reason.
   *
   * No data is read here: the constructor's first effect owns the read, because it has to
   * re-run when the address or the session changes and a lifecycle hook runs once.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());
  }

  constructor() {
    // 1. THE ADDRESS AND THE SESSION TOGETHER DRIVE THE READ. Re-runs when the address names
    //    a different account and when the caller's identity resolves, which is what makes an
    //    arrival before the identity has loaded read exactly once, and correctly.
    effect(() => {
      const key = this.accountKey();
      const held = this.ownership();

      if (key === null || held !== 'owner') {
        // Nothing is read for an address that names no account, and nothing is read for an
        // account the caller does not hold: every endpoint here would refuse it, and the
        // notice explains the case instead.
        return;
      }

      untracked(() => {
        this.store.loadMemberServices(key);
      });
    });

    // 2. THE SETTLING OF A REDEMPTION CLEARS THE FIELD. Observed rather than returned,
    //    because the store's commands report their outcome in shared slices.
    effect(() => {
      const submitted = this.redemptionSubmitted();
      const settled = !this.store.saving();
      const joined = this.store.lastRedemption();

      if (!submitted || !settled) {
        return;
      }

      untracked(() => {
        this.redemptionSubmitted.set(false);

        if (joined !== null) {
          // Cleared only on SUCCESS. A refused code stays in the field, because the operator
          // may have mistyped one character and retyping fifty is not a correction.
          this.codeForm.reset();
        }
      });
    });
  }

  // -------------------------------------------------------------------------
  // PRESENTATION
  // -------------------------------------------------------------------------

  /**
   * The command word one row offers.
   *
   * Reads the server's decision rather than deriving one: `subscriptionAction` reproduces
   * `ServiceText(Subscribed, ExpiryDate)` (`MemberServices.ascx.vb:L288-L305`), which needed
   * the stored assignment dates and the server's own clock. Mapping the three codes to the
   * three resource values is all that is left for a client to do.
   *
   * @param row The catalogue row.
   * @returns The visible command word.
   */
  protected commandLabel(row: MemberService): string {
    switch (row.subscriptionAction) {
      case 'Unsubscribe':
        return UNSUBSCRIBE_LABEL;
      case 'Renew':
        return RENEW_LABEL;
      default:
        return SUBSCRIBE_LABEL;
    }
  }

  /**
   * The accessible name of one row's command.
   *
   * The visible word repeated once per row tells a reader moving between controls nothing
   * about which service they are on, so the name carries the service's name - and it CONTAINS
   * the visible word, which keeps a spoken command matching what is seen.
   *
   * @param row The catalogue row.
   * @returns The accessible name.
   */
  protected commandName(row: MemberService): string {
    return `${this.commandLabel(row)} ${row.roleName}`;
  }

  /** The accessible name of one row's trial command. See {@link commandName}. */
  protected trialName(row: MemberService): string {
    return `${TRIAL_LABEL} ${row.roleName}`;
  }

  // -------------------------------------------------------------------------
  // COMMANDS
  // -------------------------------------------------------------------------

  /**
   * Submits the invitation code.
   *
   * The local validity check is a convenience, not the rule: the API refuses an empty code
   * with a reason of its own, and this screen does not restate the rest of the server's
   * validation. An invalid form is marked touched so the field renders its message.
   */
  protected onRedeem(): void {
    const key = this.accountKey();

    if (key === null || this.ownership() !== 'owner' || this.busy()) {
      return;
    }

    if (this.codeForm.invalid) {
      this.codeForm.markAllAsTouched();

      return;
    }

    this.redemptionSubmitted.set(true);
    this.store.redeemServiceCode(key, this.codeForm.controls.code.value);
  }

  /**
   * Runs one row's subscription command.
   *
   * ONE handler for both directions, because the legacy screen had one link for both and
   * dispatched on the caption it had just rendered. The direction is read from the server's
   * decision rather than from what the operator saw.
   *
   * ⚠ A ROW THAT REQUIRES PAYMENT OFFERS NO CONTROL AT ALL, so this is never reached for one.
   * The guard is repeated here anyway: a disabled control stops a pointer and a keyboard, but
   * not a direct call.
   *
   * @param row The catalogue row whose command was activated.
   */
  protected onCommand(row: MemberService): void {
    const key = this.accountKey();

    if (key === null || this.ownership() !== 'owner' || this.busy()) {
      return;
    }

    if (!row.subscriptionOffered || row.subscriptionRequiresPayment) {
      return;
    }

    if (row.subscriptionAction === 'Unsubscribe') {
      this.store.cancelService(key, row.roleId);

      return;
    }

    // Subscribe AND renew. The legacy screen ran one handler for both.
    this.store.subscribeToService(key, row.roleId);
  }

  /**
   * Takes one row's trial period.
   *
   * @param row The catalogue row whose trial was activated.
   */
  protected onTrial(row: MemberService): void {
    const key = this.accountKey();

    if (key === null || this.ownership() !== 'owner' || this.busy() || !row.trialOffered) {
      return;
    }

    this.store.startServiceTrial(key, row.roleId);
  }

  /** Dismisses the redemption report. The catalogue is untouched. */
  protected onDismissReport(): void {
    this.store.clearRedemption();
  }

  /**
   * The validation message for the code field, or `null` when it has none.
   *
   * THE SERVER'S MESSAGE WINS. Its validator names this field, so a refusal that arrives with
   * a per-field message is rendered in the server's own words; only when there is none does
   * the local rule speak, and the local rules are the two the server also applies. This is the
   * same precedence the tenant's settings screen uses, for the same reason: one situation must
   * not be described two different ways depending on which side noticed it.
   *
   * @returns The message to show beside the field, or `null`.
   */
  protected codeMessage(): string | null {
    const fromServer = fieldErrorMessage(this.problem(), 'code');

    if (fromServer !== null) {
      return fromServer;
    }

    const control = this.codeForm.controls.code;

    if (control.valid || control.untouched) {
      return null;
    }

    if (control.hasError('required')) {
      return CODE_REQUIRED_MESSAGE;
    }

    if (control.hasError('maxlength')) {
      return `An RSVP Code may not exceed ${String(CODE_MAX_LENGTH)} characters.`;
    }

    return null;
  }

  // -------------------------------------------------------------------------
  // INTERNALS
  // -------------------------------------------------------------------------

  /**
   * Builds the seven columns, in the legacy grid's own order.
   *
   * `MemberServices.ascx:L29-L70` declares, in order: the subscription command, the trial
   * command, `RoleName`, `Description`, the composed fee, the composed trial fee and the
   * expiry. The two command columns declare no heading text; each still carries a hidden
   * label here, which keeps the column named in the accessibility tree while painting
   * nothing.
   *
   * No width is declared on any column, because the legacy grid declared none.
   *
   * @returns The seven columns.
   * @throws Error if a required cell template is missing from the sibling template file.
   */
  private buildColumns(): readonly DataTableColumn<MemberService>[] {
    return [
      {
        key: 'subscribeCommand',
        label: SUBSCRIBE_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.subscribeCommandTemplate, 'subscribeCommand'),
      },
      {
        key: 'trialCommand',
        label: TRIAL_LABEL,
        headerHidden: true,
        headerAlign: 'center',
        bodyAlign: 'center',
        kind: 'actions',
        cellTemplate: this.requireTemplate(this.trialCommandTemplate, 'trialCommand'),
      },
      {
        key: 'roleName',
        // The row's NAME. Emitted as `<th scope="row">` so a screen reader announces which record
        // each cell belongs to - without it, traversing a row gives the column name and the value
        // and never the record's identity. This column is the one a person would read aloud to say
        // which row they mean. No visual change: the shared stylesheet restores a body row
        // header's normal weight.
        rowHeader: true,
        label: NAME_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'roleName',
      },
      {
        key: 'description',
        label: DESCRIPTION_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        field: 'description',
      },
      {
        key: 'fee',
        label: FEE_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        value: (row: MemberService): string =>
          formatRecurring(row.serviceFee, row.billingPeriod, row.billingFrequency),
      },
      {
        key: 'trialFee',
        label: TRIAL_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        value: (row: MemberService): string =>
          formatTrial(row.trialFee, row.trialPeriod, row.trialFrequency),
      },
      {
        key: 'expiry',
        label: EXPIRY_HEADING,
        headerAlign: 'center',
        bodyAlign: 'start',
        kind: 'template',
        cellTemplate: this.requireTemplate(this.expiryCellTemplate, 'expiryCell'),
      },
    ];
  }

  /**
   * Narrows a captured template, raising a located diagnostic when it is absent.
   *
   * The table declares `cellTemplate` as required on both column kinds that use one, so a
   * missing template is a real failure rather than a value to substitute for. A non-null
   * assertion would compile and then fail inside the table with no indication of which
   * template was missing.
   *
   * @param captured The captured template, or `undefined`.
   * @param reference The template's reference name, for the diagnostic.
   * @returns The template.
   * @throws Error when the template is absent.
   */
  private requireTemplate(
    captured: TemplateRef<DataTableCellContext<MemberService>> | undefined,
    reference: string,
  ): TemplateRef<DataTableCellContext<MemberService>> {
    if (captured === undefined) {
      throw new Error(
        `member-services.component.html must declare an ng-template named "#${reference}" at ` +
          'the top level of the template, outside any control-flow block.',
      );
    }

    return captured;
  }
}

/**
 * Formats a fee as the legacy screen formatted one.
 *
 * `FormatPrice(price)` (`MemberServices.ascx.vb:L80-L92`) rendered `##0.00` and answered the
 * empty string for the null sentinel, so an absent fee renders as nothing rather than as zero.
 *
 * @param price The stored fee, or `null`.
 * @returns The formatted amount, or the empty string.
 */
function formatAmount(price: number | null): string {
  return price === null ? '' : price.toFixed(2);
}

/**
 * Formats a recurring fee.
 *
 * `FormatPrice(price, period, frequency)` (`MemberServices.ascx.vb:L200-L214`), branch for
 * branch: `N` or the empty string renders "Free", `O` renders the bare amount, and anything
 * else composes `Fee.Text` - "{0} Every {1} {2}" - from the amount, the period and the unit.
 *
 * ⚠ AN ABSENT FREQUENCY RENDERS "FREE", WHICH IS THE LEGACY OUTCOME RATHER THAN A CHOICE. The
 * legacy read the column through a non-nullable string, so a stored null arrived as the empty
 * string and took the first branch. The contract here is nullable, so the null is mapped onto
 * the same branch explicitly.
 *
 * @param price The stored fee, or `null`.
 * @param period How many units one cycle spans, or `null`.
 * @param frequency The stored one-character code, or `null`.
 * @returns The composed text.
 */
function formatRecurring(
  price: number | null,
  period: number | null,
  frequency: string | null,
): string {
  if (frequency === null || frequency === '' || frequency === 'N') {
    return NO_FEE_LABEL;
  }

  if (frequency === 'O') {
    return formatAmount(price);
  }

  return `${formatAmount(price)} Every ${period ?? ''} ${FREQUENCY_UNITS[frequency] ?? ''}`.trim();
}

/**
 * Formats a trial fee.
 *
 * `FormatTrial` (`MemberServices.ascx.vb:L229-L243`) is `FormatPrice` with one difference: its
 * composed branch uses `TrialFee.Text` - "{0} for {1} {2}" - rather than the recurring wording.
 * The two are kept as separate functions rather than one parameterised by a format, because
 * the legacy carried two resource values and a shared helper would invite a caller to pass the
 * wrong one.
 *
 * @param price The stored trial fee, or `null`.
 * @param period How many units the trial spans, or `null`.
 * @param frequency The stored one-character code, or `null`.
 * @returns The composed text.
 */
function formatTrial(
  price: number | null,
  period: number | null,
  frequency: string | null,
): string {
  if (frequency === null || frequency === '' || frequency === 'N') {
    return NO_FEE_LABEL;
  }

  if (frequency === 'O') {
    return formatAmount(price);
  }

  return `${formatAmount(price)} for ${period ?? ''} ${FREQUENCY_UNITS[frequency] ?? ''}`.trim();
}
