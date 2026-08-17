import { ChangeDetectionStrategy, Component, Signal, computed, input } from '@angular/core';

import { ProblemDetails } from '../../../core/models/problem-details.model';
import {
  FieldMessages,
  ProblemSeverity,
  ProblemSummary,
  SUPPORT_REFERENCE_LEAD,
  problemSeverity,
  summarizeProblem,
} from '../../../core/utils/form-errors.util';

/**
 * How forcefully the banner presents a failure. Three bands, and the vocabulary is presentational rather
 * than diagnostic: it names how the failure is SHOWN, not what it was.
 */
export type ErrorBannerSeverity = 'danger' | 'warning' | 'calm';

/**
 * One label and its messages, flattened for iteration in a template. The problem document delivers
 * per-field messages as a dictionary, and a template cannot iterate a dictionary without a pipe.
 */
export interface FieldErrorGroup {
  /**
   * The field name, already lower-cased on its first character so it matches the client's control naming.
   * The empty string for a message that belongs to the request as a whole rather than to any one field.
   */
  readonly field: string;

  /** The messages for that field, never empty. */
  readonly messages: readonly string[];
}

/** Used for a message that belongs to the form rather than to one field. */
const FORM_LEVEL_LABEL = 'This form';

/**
 * Wording for each band, shown as text beside the message. Present because the severity must NOT be
 * carried by colour alone.
 */
const SEVERITY_LABEL: Readonly<Record<ErrorBannerSeverity, string>> = Object.freeze({
  danger: 'Error',
  warning: 'Warning',
  calm: 'Please wait',
});

@Component({
  selector: 'app-error-banner',
  standalone: true,
  imports: [],
  templateUrl: './error-banner.component.html',
  styleUrl: './error-banner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ErrorBannerComponent {
  /**
   * The failure to display, or null to display nothing. A SIGNAL input rather than a decorated field, and
   * the choice is load-bearing rather than stylistic.
   */
  readonly problem = input<ProblemDetails | null>(null);

  /**
   * Rendered labels for the field names the server reports, keyed by the wire property name.
   *
   * ⚠ WITHOUT THIS THE BANNER QUOTES RAW WIRE PROPERTY NAMES AT AN OPERATOR. A validation failure arrives
   * keyed by the contract's own property names - `portalName`, `hostFee`, `expiryDate`, `processorUserId`
   * - and listing those verbatim asks a person to map an identifier they have never seen onto a field
   * they can see. Three of those four are not even recognisable: the screen calls them Title, Hosting Fee
   * and Expiry Date. A screen that already owns a label dictionary for its own fields passes it here, and
   * the banner names the same field the same way the form does.
   *
   * Defaults to empty, so a screen that supplies nothing keeps the previous behaviour and no caller is
   * obliged to change. A name absent from the dictionary falls back to the wire name, which is strictly
   * better than showing nothing.
   */
  readonly fieldLabels = input<Readonly<Record<string, string>>>({});

  /**
   * A sentence to show when a failure carries NO problem document at all, or null to stay silent. ⚠ THIS
   * EXISTS FOR A CLASS OF FAILURE THAT WAS ENTIRELY INVISIBLE, AND THAT IS WORTH STATING PRECISELY. The
   * runtime decoders that check each response against its published contract run INSIDE the service's own
   * mapping — downstream of the interceptor's error handling — so a successful response whose body does
   * not match its contract throws a plain error carrying no problem document, no status and no support
   * reference.
   */
  readonly fallbackMessage = input<string | null>(null);

  /** Everything resolved about the current failure, computed exactly once per change. */
  private readonly summary: Signal<ProblemSummary> = computed(() =>
    summarizeProblem(this.problem(), this.fallbackMessage()),
  );

  /**
   * Whether there is anything to show. ⚠ TRUE FOR A FALLBACK SENTENCE WITH NO DOCUMENT, WHICH IS THE
   * WHOLE POINT OF THE INPUT. A document-only test would leave the banner empty for exactly the failures
   * that carry no document, so the input would be bindable and have no effect.
   */
  readonly hasProblem: Signal<boolean> = computed(() => {
    if (this.problem() !== null) {
      return true;
    }

    const fallback: string | null = this.fallbackMessage();

    return fallback !== null && fallback.length > 0;
  });

  readonly severity: Signal<ErrorBannerSeverity> = computed(() => {
    const domain: ProblemSeverity = problemSeverity(this.summary().status);

    switch (domain) {
      case 'warning':
        // A refusal: the system is working as configured. This is the arm the legacy
        // YellowWarning evidence in this class's migration notes speaks to.
        return 'warning';
      case 'info':
        return 'calm';
      case 'error':
        return 'danger';
      default:
        // Unreachable while the imported severity type has three members, and kept because the workspace
        // forbids an unhandled case and because that type is free to grow a fourth without this file being
        // edited. A new member must not silently fall through to nothing.
        return 'danger';
    }
  });

  /** The severity as a word, so the band is never carried by colour alone. */
  readonly severityLabel: Signal<string> = computed(() => SEVERITY_LABEL[this.severity()]);

  /** The short summary of the problem TYPE, as the document's title. */
  readonly title: Signal<string> = computed(() => {
    const { title, message } = this.summary();

    return title === message ? '' : title;
  });

  /** Whether a distinct title is worth showing above the message. */
  readonly hasTitle: Signal<boolean> = computed(() => this.title().length > 0);

  /** The single sentence shown as the body of the banner. */
  readonly message: Signal<string> = computed(() => this.summary().message);

  /**
   * The per-field messages, empty when the failure carried none. Two sources are combined into one list,
   * because both belong in the same place on screen.
   */
  readonly fieldErrors: Signal<readonly FieldErrorGroup[]> = computed(() => {
    const { fieldMessages, formMessages } = this.summary();
    const groups: FieldErrorGroup[] = [];

    if (formMessages.length > 0) {
      groups.push({ field: '', messages: formMessages });
    }

    for (const entry of fieldMessages) {
      groups.push(toGroup(entry));
    }

    return groups;
  });

  /** Whether any per-field or form-level message is present. */
  readonly hasFieldErrors: Signal<boolean> = computed(() => this.fieldErrors().length > 0);

  /** The identifier a person quotes when reporting this failure, or null when the document carried none. */
  readonly supportReference: Signal<string | null> =
    computed(() => this.summary().supportReference);

  /** Whether there is an identifier worth quoting. */
  /** Introduces the support reference and explains what a reader is meant to do with it. */
  readonly referenceLead: string = SUPPORT_REFERENCE_LEAD;

  readonly hasSupportReference: Signal<boolean> =
    computed(() => this.supportReference() !== null);

  /**
   * The label shown for a group of messages. Resolved in three steps, so the best available name always
   * wins: the caller's dictionary, then the same name with its first character lower-cased - which is the
   * form `fieldMessages` normalises keys into, and therefore the form a dictionary keyed by control name
   * will match - and finally the wire name itself.
   *
   * @param field The field name from the problem document.
   * @returns The label to show.
   */
  labelFor(field: string): string {
    if (field.length === 0) {
      return FORM_LEVEL_LABEL;
    }

    const labels: Readonly<Record<string, string>> = this.fieldLabels();
    const exact: string | undefined = labels[field];

    if (exact !== undefined && exact.length > 0) {
      return exact;
    }

    const camelCased = `${field.charAt(0).toLowerCase()}${field.slice(1)}`;
    const byCamelCase: string | undefined = labels[camelCased];

    return byCamelCase !== undefined && byCamelCase.length > 0 ? byCamelCase : field;
  }

  /**
   * Identity for the per-field iteration.
   *
   * @param _index Unused positional index.
   * @param group The group being tracked.
   * @returns The field name, which is unique within one document.
   */
  trackByField(_index: number, group: FieldErrorGroup): string {
    return group.field;
  }
}

/**
 * Re-shapes one resolved field entry as a template group.
 *
 * @param entry One field's resolved messages.
 * @returns The group to render.
 */
function toGroup(entry: FieldMessages): FieldErrorGroup {
  return { field: entry.field, messages: entry.messages };
}
