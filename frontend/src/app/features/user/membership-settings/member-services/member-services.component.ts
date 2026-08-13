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

/** The panel's heading. */
const SECTION_TITLE = 'Manage Services';

/** The explanatory paragraph above the code field. `ServicesHelp.Text`, with ONE documented change. */
const SERVICES_HELP =
  'This section allows you to manage your subscriptions on the site. You can subscribe to ' +
  'some Services by entering an RSVP Code. If you have been given a special RSVP code you ' +
  'can subscribe to these Services by entering the code in the RSVP Code field and clicking ' +
  'the "Subscribe" button next to the field. To manage the other subscription services ' +
  'provided by this site you can use the grid below. A service that charges a fee is listed ' +
  'here for reference but cannot be subscribed to or cancelled on this site.';

/** `plRSVPCode.Text`. */
const CODE_LABEL = 'Enter RSVP Code:';

/** `plRSVPCode.Help`, verbatim. */
const CODE_HELP =
  'You can subscribe to some Services by entering an RSVP Code.  If you have been given a ' +
  'code please enter it and click Subscribe';

/** `cmdRSVP.Text`. */
const CODE_SUBMIT = 'Subscribe';

/**
 * `RSVPSuccess.Text`, verbatim. ⚠ ITS CLOSING INSTRUCTION IS STILL TRUE HERE, WHICH IS WHY IT IS KEPT.
 * The sentence tells the operator to sign out and back in before the new services take effect.
 */
const CODE_SUCCESS =
  'You have been successfully added to the role(s) associated with the RSVP Code entered. In ' +
  'order to get access to the new services you will need to Logout and then Login to the site ' +
  'again.';

const CODE_REQUIRED_MESSAGE = 'An RSVP Code is required.';

/** The maximum length of an invitation code. */
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
 * The phrase shown where the legacy rendered a link to a payment site. NET-NEW WORDING, and unavoidably
 * so: the legacy screen had no such state because it had somewhere to send the operator.
 */
const PAYMENT_REQUIRED_LABEL = 'Payment required';

/**
 * The wording shown when the address names an account that is not the caller's. NET-NEW: the legacy
 * container expressed this by hiding the tab, which is not available to a routed screen that has already
 * been navigated to.
 */
const NOT_OWN_ACCOUNT_MESSAGE =
  'Subscriptions are managed by their own account holder, so this panel shows your own ' +
  'services only. Role membership for another account is managed from that role.';

/**
 * The wording shown while the host has not resolved which account the caller holds. NET-NEW, and it
 * describes a transient state rather than a refusal: the host derives the subject account from the
 * session, so an absent value means the session has not resolved yet or resolved without an account.
 */
const ACCOUNT_UNKNOWN_MESSAGE =
  'Your subscriptions will be shown here once your account has been identified.';

/**
 * The frequency vocabulary, resolved to the wording the legacy resource file declared.
 * `Frequency_D.Text`, `Frequency_W.Text`, `Frequency_M.Text` and `Frequency_Y.Text`.
 */
const FREQUENCY_UNITS: Readonly<Record<string, string>> = {
  D: 'Day(s)',
  W: 'Week(s)',
  M: 'Month(s)',
  Y: 'Year(s)',
};

/**
 * The five store operations this panel performs, and the only failures its banner renders. ⚠ WHY A CLOSED
 * LIST RATHER THAN "WHATEVER FAILED LAST". The store holds ONE failure slot for every account command,
 * and this panel is mounted inside a screen that renders that slot too.
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
  // ⚠ EVERY SELECTOR AND PIPE THE PAIRED TEMPLATE USES MUST APPEAR HERE. Strict template checking turns an
  // element matching an unlisted component into a compilation error rather than a silent unknown element.
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
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MemberServicesComponent implements OnInit {
  /** The shared store. */
  private readonly store = inject(UserStore);

  /**
   * The session, read for ONE question: whether the supplied account is the caller's own. Never used to
   * decide whether an operation is permitted - the server decides that, and refuses with its own status
   * and problem document.
   */
  private readonly auth = inject(AuthStore);

  // -------------------------------------------------------------------------
  // THE HOST INPUT
  // -------------------------------------------------------------------------

  /**
   * The account whose services to manage, supplied by the host screen, or `null` when the host has not
   * resolved one. A SIGNAL input rather than a decorated field, because {@link accountKey} and {@link
   * ownership} derive from it and a derivation over a decorated field cannot know when to recompute - it
   * would resolve once and then ignore every later change.
   */
  readonly accountId = input.required<number | null>();

  // CELL TEMPLATES
  // Captured statically because the columns are built once, in the constructor, before the first
  // change-detection pass. Each is declared at the TOP LEVEL of the paired template, outside every
  // control-flow block, which is what makes a static capture possible.

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
   * The seven columns, built once. Held in a signal so the table's input is a reactive source, and
   * populated from the constructor rather than at field initialisation because the templates above are
   * only available once the view has been created.
   */
  private readonly columnSet = signal<readonly DataTableColumn<MemberService>[]>([]);

  /** Whether a redemption has been submitted and not yet settled. */
  private readonly redemptionSubmitted = signal<boolean>(false);

  // -------------------------------------------------------------------------
  // THE INVITATION-CODE FORM
  // -------------------------------------------------------------------------

  /**
   * The one-field form the legacy screen expressed as a text box and a link button. ⚠ THE CONTROL IS
   * NON-NULLABLE AND ITS VALUE IS SENT AS TYPED. `nonNullable` makes the value a `string` rather than
   * `string | null`, so nothing downstream has to consider an absent value; and nothing here trims or
   * folds it, because the legacy comparison was ordinary string equality against the stored code and
   * trimming would admit codes the legacy application refused.
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

  /** The account key the host supplied, or `null` when it supplied none. */
  readonly accountKey: Signal<number | null> = computed(() => {
    const bound = this.accountId();

    return bound !== null && Number.isFinite(bound) ? bound : null;
  });

  /**
   * Whether the supplied account is the caller's own, and whether that is yet knowable. THREE STATES
   * RATHER THAN A BOOLEAN, because the identity read is asynchronous: treating "not yet resolved" as "not
   * the owner" would flash the wrong-account notice on every arrival, and treating it as "the owner"
   * would issue a request for an account the caller may not hold.
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
   * row elements instead of rebuilding them. ⚠ THE RECORD'S OWN KEY, NOT THE ARRAY POSITION AND NOT THE
   * OBJECT. The grid's fallback is the row OBJECT, which is a correct key only while the same objects
   * stay in play; every read from the server decodes fresh objects, so without this a refetch presents
   * entirely new keys and the whole body is rebuilt to display records that never changed.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly serviceRowKey = (row: MemberService): number => row.roleId;

  protected readonly services: Signal<readonly MemberService[]> = computed(() => {
    const key = this.accountKey();

    return this.store.memberServicesAccountId() === key ? this.store.memberServices() : [];
  });

  /** The seven columns, for the table's input. */
  protected readonly columns = this.columnSet.asReadonly();

  /** Whether the catalogue read is outstanding. */
  protected readonly loading: Signal<boolean> = this.store.memberServicesLoading;

  /** Whether any command is in flight. */
  protected readonly busy: Signal<boolean> = computed(
    () => this.store.saving() || this.store.memberServicesLoading(),
  );

  /**
   * The failure the banner renders, in the server's own words - and ONLY a failure of this panel's own
   * five operations. ⚠ THE OPERATION FILTER IS LOAD-BEARING NOW THAT THIS PANEL IS MOUNTED INSIDE ANOTHER
   * SCREEN. The store holds ONE failure slot shared by every account command, and the host screen renders
   * it too; without this filter a refused policy save would be announced twice, once by the host's banner
   * and once here, and a refused redemption would appear at the top of a screen that did not perform it.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed(() => {
    const failure = this.store.failure();

    if (failure === null || !MEMBER_SERVICE_OPERATIONS.includes(failure.operation)) {
      return null;
    }

    return failure.problem;
  });

  /**
   * The roles the last redeemed code admitted the account to, or `null`. Rendered as the success half of
   * the legacy screen's two-message report.
   */
  protected readonly redeemedRoleNames: Signal<readonly string[] | null> = computed(() => {
    const joined = this.store.lastRedemption();

    return joined === null ? null : joined.roles.map((role) => role.roleName);
  });

  /**
   * Assembles the column set once the cell templates exist. ⚠ NOT IN THE CONSTRUCTOR. The three view
   * queries above are declared static, which means they resolve before this hook runs and NOT before the
   * constructor does — building the columns there raises the missing-template diagnostic on every mount.
   * The sibling listing screens assemble their columns in exactly this hook for exactly this reason.
   */
  ngOnInit(): void {
    this.columnSet.set(this.buildColumns());
  }

  constructor() {
    // 1. THE ADDRESS AND THE SESSION TOGETHER DRIVE THE READ. Re-runs when the address names a different
    // account and when the caller's identity resolves, which is what makes an arrival before the identity
    // has loaded read exactly once, and correctly.
    effect(() => {
      const key = this.accountKey();
      const held = this.ownership();

      if (key === null || held !== 'owner') {
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
   * The accessible name of one row's command. The visible word repeated once per row tells a reader
   * moving between controls nothing about which service they are on, so the name carries the service's
   * name - and it CONTAINS the visible word, which keeps a spoken command matching what is seen.
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

  /** Submits the invitation code. */
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
   * Runs one row's subscription command. ONE handler for both directions, because the legacy screen had
   * one link for both and dispatched on the caption it had just rendered.
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

  /** Dismisses the redemption report. */
  protected onDismissReport(): void {
    this.store.clearRedemption();
  }

  /**
   * The validation message for the code field, or `null` when it has none. THE SERVER'S MESSAGE WINS. Its
   * validator names this field, so a refusal that arrives with a per-field message is rendered in the
   * server's own words; only when there is none does the local rule speak, and the local rules are the
   * two the server also applies.
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
   * Builds the seven columns, in the legacy grid's own order. `MemberServices.ascx:L29-L70` declares, in
   * order: the subscription command, the trial command, `RoleName`, `Description`, the composed fee, the
   * composed trial fee and the expiry.
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
   * Narrows a captured template, raising a located diagnostic when it is absent. The table declares
   * `cellTemplate` as required on both column kinds that use one, so a missing template is a real failure
   * rather than a value to substitute for.
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
 * Formats a fee as the legacy screen formatted one. `FormatPrice(price)` rendered `##0.00` and answered
 * the empty string for the null sentinel, so an absent fee renders as nothing rather than as zero.
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
