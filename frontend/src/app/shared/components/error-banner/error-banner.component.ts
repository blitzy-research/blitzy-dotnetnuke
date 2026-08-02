import { ChangeDetectionStrategy, Component, Input, Signal, computed, signal } from '@angular/core';

import {
  ProblemDetails,
  problemDetailsFieldErrors,
  problemDetailsMessage,
} from '../../../core/models/problem-details.model';

/**
 * One field's validation messages, flattened for iteration in a template.
 *
 * The problem document delivers per-field messages as a dictionary, and a template
 * cannot iterate a dictionary without a pipe. Projecting to an array in TypeScript
 * keeps the template free of a pipe import and, more usefully, gives the iteration a
 * stable identity to track by — the field name — so re-rendering one changed message
 * does not rebuild every row.
 */
export interface FieldErrorGroup {
  /**
   * The field name, already lower-cased on its first character so it matches the
   * client's control naming.
   *
   * The empty string for a form-level message, which a validation failure uses for
   * an error that belongs to no single field.
   */
  readonly field: string;

  /** The messages for that field, never empty. */
  readonly messages: readonly string[];
}

/**
 * Renders a failed request's RFC 7807 problem document as a single error surface.
 *
 * Shows the one-sentence summary, then the per-field messages when the failure was a
 * validation failure. Emits nothing at all when there is no problem, so a screen can
 * bind it unconditionally and let it disappear rather than guarding it with its own
 * conditional block.
 *
 * The live region is PERSISTENT: the outer wrapper is always in the document and only
 * its contents change. That is deliberate and is the reason the component is
 * structured this way rather than being wrapped in a conditional by its consumer. A
 * live region that is inserted into the document at the same moment as its content is
 * announced inconsistently — some screen readers register the region only after
 * insertion and so miss the very first message, which is the one that matters. A
 * region that is already present and whose content changes is announced reliably.
 *
 * The wrapper carries no padding, border or background of its own, so while it is
 * empty it occupies no space and paints nothing.
 *
 * MIGRATION: this replaces the legacy validation-summary control, which rendered
 * server-side validator messages into the page on postback. Two differences are
 * deliberate. First, the legacy summary was frequently fed messages containing HTML
 * — measured across the in-scope resource files, 76 values carry a tag and 29 of
 * those open with a line break, and one 344-character value carries a live script
 * tag from a remote host. Every string here is interpolated and therefore escaped,
 * so markup in a message is shown as text and cannot execute. Second, the legacy
 * summary was purely visual; this one is announced.
 */
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
   * The problem document to render, or null to render nothing.
   *
   * Held in a signal so the derived members below recompute exactly once per
   * assignment. Reading and projecting the document in template getters instead
   * would repeat the work on every change-detection pass, including passes that
   * changed nothing about this component.
   */
  private readonly _problem = signal<ProblemDetails | null>(null);

  /**
   * The failure to display.
   *
   * A write-only input, because the projected members below are the readable
   * surface and exposing the raw document as well would invite a consumer to
   * re-derive what is already derived here.
   */
  @Input()
  set problem(value: ProblemDetails | null | undefined) {
    this._problem.set(value ?? null);
  }

  /**
   * Fallback text for a problem document that carries neither a detail nor a title.
   *
   * Bound from a constant rather than written into the template, because the
   * template compiler collapses runs of whitespace in template text and would
   * silently rewrite authored punctuation and spacing. Interpolated values are not
   * collapsed.
   */
  @Input() fallbackMessage: string = DEFAULT_FALLBACK_MESSAGE;

  /** Whether there is anything to show. */
  readonly hasProblem: Signal<boolean> = computed(() => this._problem() !== null);

  /**
   * The single sentence shown at the top of the banner.
   *
   * Resolves to the document's detail, then its title, then the fallback — so the
   * banner is never headed by an empty line, whatever the server sent.
   */
  readonly message: Signal<string> = computed(() =>
    problemDetailsMessage(this._problem(), this.fallbackMessage),
  );

  /**
   * The per-field messages, empty when the failure was not a validation failure.
   *
   * Entries whose value is not a non-empty list of strings are already dropped by
   * the projection, so the template can render each group unconditionally.
   */
  readonly fieldErrors: Signal<readonly FieldErrorGroup[]> = computed(() => {
    const errors = problemDetailsFieldErrors(this._problem());

    return Object.entries(errors).map(([field, messages]) => ({ field, messages }));
  });

  /** Whether any per-field message is present. */
  readonly hasFieldErrors: Signal<boolean> = computed(() => this.fieldErrors().length > 0);

  /**
   * The label shown for a group of messages.
   *
   * A form-level message — the one a validation failure keys with the empty string —
   * has no field to name, so its group is labelled generically rather than with a
   * blank heading. Other groups are labelled with the field name as the server
   * spelled it, because inventing a friendly label here would require this component
   * to know every screen's vocabulary.
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

/** Used when the server sent a problem document with no readable text. */
const DEFAULT_FALLBACK_MESSAGE = 'The request could not be completed.';

/** Used for a message that belongs to the form rather than to one field. */
const FORM_LEVEL_LABEL = 'This form';
