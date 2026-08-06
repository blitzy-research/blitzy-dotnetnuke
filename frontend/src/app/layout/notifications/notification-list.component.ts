import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import type { Signal } from '@angular/core';

import { NotificationService } from '../../core/services/notification.service';

import type { AppNotification, NotificationSeverity } from '../../core/services/notification.service';

/**
 * The word each severity is announced and rendered with.
 *
 * ⚠ SEVERITY IS NEVER CARRIED BY COLOUR ALONE. Colour is a reinforcement here, exactly as it
 * is in the shared error banner: the band is stated as a WORD that is both visible and read
 * out, so the message is fully perceivable without perceiving the hue. That is what makes the
 * four bands usable by a person with a colour-vision deficiency and by a person using a screen
 * reader, and it is the reason this map exists rather than the severity being expressed only
 * as a data attribute for the stylesheet.
 *
 * The wording matches the shared error banner's own severity vocabulary so that one
 * application does not describe the same condition two ways: a refusal is a "Warning" in both
 * surfaces, and a fault is an "Error" in both.
 *
 * Frozen, so the map cannot be mutated by a consumer and the object identity is stable across
 * change-detection passes.
 */
const SEVERITY_LABEL: Readonly<Record<NotificationSeverity, string>> = Object.freeze({
  success: 'Success',
  info: 'Information',
  warning: 'Warning',
  error: 'Error',
});

/**
 * The accessible name of the dismissal control.
 *
 * A control whose visible content is a glyph needs a name that says what it does. The name is
 * the same for every entry rather than interpolating the message into it: a message can be a
 * paragraph of remote text, and a several-hundred-character accessible name is worse than a
 * short one, not better.
 */
const DISMISS_LABEL = 'Dismiss this message';

/**
 * The accessible name of the region itself.
 *
 * Named so it is announced as something rather than as an unlabelled run of text, and so it is
 * reachable by landmark navigation. The name is an attribute, so it adds no visible content.
 */
const REGION_LABEL = 'Notifications';

/**
 * Renders the queued notifications and lets a person dismiss them.
 *
 * ## The defect this closes
 *
 * `core/services/notification.service.ts` is the one place an outcome is announced, and it is
 * reached from the error interceptor, from both navigation gates and from a dozen feature
 * screens. Nothing rendered its queue. Every one of those announcements — a permission
 * refusal, a rate-limit refusal, a network failure, a confirmed save, an advisory that a
 * requested notification could not be sent — was appended to a signal that no template read,
 * so the operator saw NOTHING. A screen that correctly refused to act, and correctly said so,
 * was indistinguishable from one that had silently done nothing.
 *
 * ## Where it is mounted, and why there
 *
 * Inside the shell's `main` region, above the routed outlet, so that it is present on every
 * screen and survives navigation: a queue rendered by a routed component would be destroyed by
 * the very navigation an announcement often accompanies. It is deliberately NOT one of the
 * shell's five grid regions — the shell's own specification pins that count, and a sixth
 * region would give an empty queue a grid track to occupy on every screen.
 *
 * ## The live region is PERSISTENT
 *
 * The region element is always in the document and only its contents change. A live region
 * inserted at the same moment as its first message is announced inconsistently across screen
 * readers, and the first message is the one that matters. The loop is therefore INSIDE the
 * region, never wrapped around it. This is the same arrangement, for the same measured reason,
 * as the shared error banner's.
 *
 * `role="status"` with `aria-live="polite"` is correct for this surface and `role="alert"` is
 * not: an announcement here reports the outcome of something the operator just did, so it must
 * not interrupt them mid-sentence. The one surface that IS assertive is the error banner, which
 * carries a validation failure the operator has to act on before proceeding. Two surfaces, two
 * urgencies, stated explicitly in each.
 *
 * ## What it deliberately does not do
 *
 * No auto-dismiss timer, no animation, no de-duplication, no grouping and no severity
 * derivation. The service owns the queue and documents that it holds no presentation
 * behaviour; every entry arrives with its severity already decided and its message already
 * composed, and this component's whole job is to place that text and offer the dismissal. An
 * automatic timer would in particular be an accessibility regression: a person reading with a
 * screen reader, or reading slowly, would lose a message before finishing it, and WCAG treats
 * timed content as something the user must be able to control.
 *
 * MIGRATION: this replaces the legacy skin's module-message band. The legacy renderer
 * `Library/Components/Skins/ModuleMessage.vb` painted one message per page load into the skin,
 * with a per-severity raster icon (L134, L140, L146) and — measured — NO `aria-live`, no
 * `role` and no `aria-` attribute of any kind anywhere in either legacy tree. Three
 * divergences are deliberate: the announcement semantics are net-new; the icons are not
 * carried across, because the design system permits no CSS asset reference and a word carries
 * the cue further than an image (it cannot fail to load and it is read out); and the message
 * is DISMISSIBLE, where a legacy band persisted until the next postback replaced it.
 *
 * MIGRATION: every string is rendered through escaping interpolation and nothing here is bound
 * as markup. That is structural rather than defensive: a message can originate in a server
 * `ProblemDetails` payload whose wording descends from legacy resource files in which raw
 * markup is commonplace — 76 of 1182 in-scope resource values carry an HTML tag and a handful
 * carry `<script>` elements — so a value still carrying markup renders as visible, inert text
 * and creates no element.
 *
 * MIGRATION: localisation is not ported. The three static strings here are authored in English
 * with the legacy resource files read only as the authority for wording.
 */
@Component({
  selector: 'app-notification-list',
  standalone: true,
  // No shared component is imported, and none fits: the shared error banner renders one RFC
  // 7807 problem document with per-field messages, whereas this renders a queue of
  // already-composed sentences with no document behind them. Reaching for it would mean
  // fabricating a problem document per notification.
  imports: [],
  templateUrl: './notification-list.component.html',
  styleUrl: './notification-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationListComponent {
  private readonly notifications = inject(NotificationService);

  /**
   * The queue, oldest entry first, exactly as the service publishes it.
   *
   * Re-exposed rather than copied or re-ordered: the service's order is append order, which is
   * the order the operator caused the outcomes in, and re-sorting by severity would move a
   * message out from under a reader mid-sentence.
   */
  protected readonly entries: Signal<readonly AppNotification[]> = this.notifications.notifications;

  /** The accessible name of the region. */
  protected readonly regionLabel: string = REGION_LABEL;

  /** The accessible name of every dismissal control. */
  protected readonly dismissLabel: string = DISMISS_LABEL;

  /**
   * The word for one entry's severity.
   *
   * A method rather than a template lookup into the map, because
   * `noPropertyAccessFromIndexSignature` forbids reading a keyed record as a property in a
   * template and an index expression there would be harder to read than this call. The
   * parameter is the closed severity union, so every input has an answer and no fallback
   * branch can exist to be wrong.
   *
   * @param severity The entry's severity.
   * @returns The word to render and announce.
   */
  protected labelFor(severity: NotificationSeverity): string {
    return SEVERITY_LABEL[severity];
  }

  /**
   * Removes one entry from the queue.
   *
   * Delegates without interpreting: the service owns the queue, and an identifier it never
   * issued — or one a clear has already discarded — is a no-op there rather than an error, so
   * no presence test is written here.
   *
   * @param id The entry to remove.
   */
  protected dismiss(id: number): void {
    this.notifications.dismiss(id);
  }
}
