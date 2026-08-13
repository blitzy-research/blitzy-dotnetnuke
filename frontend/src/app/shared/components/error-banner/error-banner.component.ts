import { ChangeDetectionStrategy, Component, Signal, computed, input } from '@angular/core';

import { ProblemDetails } from '../../../core/models/problem-details.model';
import {
  FieldMessages,
  ProblemSeverity,
  ProblemSummary,
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
  readonly hasSupportReference: Signal<boolean> =
    computed(() => this.supportReference() !== null);

  /**
   * The label shown for a group of messages.
   *
   * @param field The field name from the problem document.
   * @returns The label to show.
   */
  labelFor(field: string): string {
    return field.length === 0 ? FORM_LEVEL_LABEL : field;
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
