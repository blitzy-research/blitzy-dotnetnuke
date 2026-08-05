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
 * How forcefully the banner presents a failure.
 *
 * Three bands, and the vocabulary is presentational rather than diagnostic: it names
 * how the failure is SHOWN, not what it was. The domain classification is
 * {@link ProblemSeverity}, which this component imports rather than restates — see
 * {@link ErrorBannerComponent.severity} for how one maps onto the other.
 *
 * A string-literal union rather than an enumeration. The workspace compiles with
 * `isolatedModules`, which rules out the compile-time-inlined form of an enumeration,
 * and a union is the better fit regardless: the three members are the whole
 * vocabulary, they need no numeric identity, and they survive serialisation as the
 * words they are.
 */
export type ErrorBannerSeverity = 'danger' | 'warning' | 'calm';

/**
 * One label and its messages, flattened for iteration in a template.
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
   * The empty string for a message that belongs to the request as a whole rather
   * than to any one field.
   */
  readonly field: string;

  /** The messages for that field, never empty. */
  readonly messages: readonly string[];
}

/** Used for a message that belongs to the form rather than to one field. */
const FORM_LEVEL_LABEL = 'This form';

/**
 * The status the credential rate limiter refuses with.
 *
 * Named rather than written inline because it is the single value that separates the
 * calm band from the warning band, and a bare `429` at that comparison would read as
 * arbitrary.
 */
const TOO_MANY_REQUESTS = 429;

/**
 * Wording for each band, shown as text beside the message.
 *
 * Present because the severity must NOT be carried by colour alone. That is a
 * requirement with a measured cause rather than a stylistic preference: the
 * `--color-danger` token is annotated in `styles/_tokens.scss` with its own contrast
 * measurement — 4.0:1 on the background, below the 4.5:1 needed for normal text —
 * and the instruction that error states "must therefore carry a non-colour cue as
 * well - an icon or the word itself - never colour alone". This is the word itself.
 *
 * It also restores a legacy affordance. The excluded legacy renderer paired every
 * message type with a distinct icon — `~/images/red-error.gif`,
 * `~/images/yellow-warning.gif` and `~/images/green-ok.gif` — so severity was
 * legible without reading the colour. Text carries that further than an image did:
 * it survives a missing asset and it is announced.
 *
 * The first two words are the legacy message types' own names, minus their colour
 * prefix: `RedError` and `YellowWarning` become "Error" and "Warning". Naming them
 * anything else would invent vocabulary where the legacy already supplied it, and a
 * more specific word would be wrong for part of its band — "Not allowed" reads
 * correctly for a refusal but not for a missing record, and both share the warning
 * band. The third band has no legacy name, because the legacy enum has no third
 * non-success member; see {@link ErrorBannerComponent.severity}.
 */
const SEVERITY_LABEL: Readonly<Record<ErrorBannerSeverity, string>> = Object.freeze({
  danger: 'Error',
  warning: 'Warning',
  calm: 'Please wait',
});

/**
 * Renders a failed request's RFC 7807 problem document as a single error surface.
 *
 * Shows a severity word, the one-sentence summary, then the per-field messages when
 * the failure was a validation failure, then the server's trace identifier when it
 * supplied one. Emits nothing at all when there is no problem, so a screen can bind
 * it unconditionally and let it disappear rather than guarding it with its own
 * conditional block.
 *
 * ## Where the work happens
 *
 * Almost none of it happens here. `core/utils/form-errors.util.ts` resolves a problem
 * document into everything a presentation layer needs — severity, title, message,
 * per-field messages, form-level messages and trace identifier — and its own
 * documentation names this component as the intended caller of
 * {@link summarizeProblem}, describing it as the single entry point a banner should
 * use. This class therefore calls it ONCE and projects the result. It re-derives
 * nothing the utility already derives: not the break-tag normalisation, not the
 * validation-document discrimination, not the dictionary iteration, and not the
 * severity rule. Restating any of them would give one rule two definitions free to
 * drift apart, which is the exact failure mode that utility warns against.
 *
 * ## Why the live region is persistent
 *
 * The outer wrapper is always in the document and only its contents change. That is
 * deliberate and is the reason the component is structured this way rather than being
 * wrapped in a conditional by its consumer. A live region that is inserted into the
 * document at the same moment as its content is announced inconsistently — some
 * screen readers register the region only after insertion and so miss the very first
 * message, which is the one that matters. A region that is already present and whose
 * content changes is announced reliably. The wrapper carries no padding, border or
 * background of its own, so while it is empty it occupies no space and paints
 * nothing.
 *
 * ## MIGRATION notes
 *
 * MIGRATION: this component is a NET-NEW affordance, and the reason is worth stating
 * precisely rather than as "no ancestor exists". The migration plan cites an
 * `asp:ValidationSummary` rendering path as the legacy source; that path does not
 * exist. Measured, `asp:ValidationSummary` occurs ZERO times in the in-scope markup,
 * zero times anywhere under `Website/`, and zero times in the repository. It belongs
 * to the same family as the plan's own vacuous exclusions — Telerik controls, COM
 * interop and `On Error GoTo`, each independently measured at zero occurrences. The
 * plan corroborates the direction from the other side: exception logging likewise has
 * zero in-scope call sites, so the target error surface is new behaviour rather than a
 * translation.
 *
 * MIGRATION: the REAL ancestor exists and is named here rather than left unfound. It
 * is `Library/Components/Skins/ModuleMessage.vb` together with
 * `Library/Components/Skins/Skin.vb`'s `AddModuleMessage` and
 * `GetModuleMessageControl`. That subtree is deliberately out of scope — `Skins` is
 * one of the twenty-three excluded sub-trees — so it is a reference, never a port.
 * Its structure does map: the three `AddModuleMessage` overloads take a Heading and a
 * Message separately, and those are the two halves this component shows as the
 * document's `title` and `detail`.
 *
 * MIGRATION: a permission refusal is presented as a WARNING, not as an error, and the
 * legacy application is the authority for that twice over.
 * `Website/admin/Security/AccessDenied.ascx.vb` performs no permission check at all —
 * it only presents a denial — and BOTH of its branches render at `YellowWarning`: the
 * message passed through the query string, and the localised default. Independently,
 * the legacy renderer itself styled the two differently: `YellowWarning` set its
 * heading to the ordinary `Normal` class while `RedError` set `NormalRed`, whose only
 * declaration in the legacy stylesheet sits under the comment "text style used for
 * error messages" and is the measured origin of the `--color-danger` token. So the
 * legacy distinguished a refusal from a fault, and so does this.
 *
 * MIGRATION: the calm band has NO legacy precedent and is new. The legacy
 * `ModuleMessageType` enum declares exactly three members — `GreenSuccess`,
 * `YellowWarning` and `RedError` — with no informational or calm member, so nothing
 * was carried across and nothing could be. It exists because the credential rate
 * limiter is the compensating control for the legacy CAPTCHA that this migration
 * deliberately removed: a refusal that means "you are early" must not be dressed as a
 * failure, or it reports a fault where the system is working exactly as configured.
 * Success is the fourth band that is absent by design — `GreenSuccess` has no
 * counterpart here, because a success is not a problem document and belongs to the
 * notification service.
 *
 * MIGRATION: every string this component exposes is plain text, and that closes a
 * measured vulnerability rather than expressing a preference. The legacy renderer
 * assigned message text straight to a Web Forms label — `lblMessage.Text =
 * strMessage` — which renders unencoded, and that file contains no encoding call of
 * any kind. Against it sits the real payload surface: across the in-scope resource
 * files, 76 of 1182 values carry an HTML tag, and one carries a live advertising
 * script whose second tag loads from a remote host. Some call sites encoded
 * defensively and the renderer never did, which is precisely the shape of a
 * defence-in-depth failure. Here the encoding is structural: every value is
 * interpolated as text, and no member of this class is markup.
 *
 * MIGRATION: legacy line-break markup is normalised to text by the utility, and it
 * has to be, in both spellings and in both places. `<br/>` was prefixed to a
 * validator message in the account screen and appended to the heading by the legacy
 * renderer itself; `<br>` was appended by the portal signup screen INSIDE a
 * per-character validation loop, so one message accumulated one break per invalid
 * character. Breaks also arrive in the PER-FIELD dictionary rather than only in the
 * summary: nine validator `ErrorMessage` attributes in the role editor markup open
 * with one. The utility strips leading and trailing breaks and turns interior ones
 * into newline characters for every field message as well as for the summary, so no
 * value reaching this component still carries them.
 *
 * MIGRATION: the trace identifier is a new diagnostic with no legacy counterpart, and
 * it is the ONLY diagnostic surfaced. The legacy error container branched on whether
 * the caller was a super-user and, for those who were, passed the exception's full
 * string form — the entire stack trace — through as the displayed message. The
 * problem-document contract carries no stack trace, no exception, no inner exception
 * and no developer message, and its shape does not vary by environment, so that
 * exposure is now closed for EVERY caller including super-users. What replaces it
 * joins a person's report to the server's own record of the request and reveals
 * nothing about the server's internals.
 *
 * MIGRATION: localisation is not ported. The legacy screens resolved every label
 * through resource files keyed by control and property; that mechanism is Web Forms
 * specific and out of scope. The few static words here are authored inline, with the
 * legacy resource wording used as the reference for phrasing so the surface stays
 * recognisable. No translation runtime is introduced.
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
   * The failure to display, or null to display nothing.
   *
   * A SIGNAL input rather than a decorated field, and the choice is load-bearing
   * rather than stylistic. Every derived member below is a `computed()` reading this
   * one source, which is only correct if the source is itself reactive: a plain
   * `@Input()` field is not, so a `computed()` over one would never recompute and the
   * banner would render its first failure and then silently ignore every later one
   * under `OnPush`. A signal input is reactive by construction, so it needs no
   * private backing signal and no setter to write one, and it satisfies the
   * workspace's strict input access rule for free because it cannot be anything but
   * public.
   *
   * Accepts a plain {@link ProblemDetails}. A validation document is assignable to it
   * — the two differ only in whether the per-field dictionary is optional — so the
   * looser type is the correct parameter type, and the utility performs the narrowing
   * when it reads the dictionary.
   *
   * Null is the empty state rather than an error state. It is the default, so a
   * consumer can bind a store slice that starts empty and the banner simply shows
   * nothing until there is something to show.
   */
  readonly problem = input<ProblemDetails | null>(null);

  /**
   * Everything resolved about the current failure, computed exactly once per change.
   *
   * The single call into the resolution utility. Every public member below reads this
   * rather than calling the utility again, so a redraw that changes nothing costs one
   * signal read instead of re-resolving the document once per member.
   */
  private readonly summary: Signal<ProblemSummary> = computed(() =>
    summarizeProblem(this.problem()),
  );

  /** Whether there is anything to show. */
  readonly hasProblem: Signal<boolean> = computed(() => this.problem() !== null);

  /**
   * How forcefully to present the failure.
   *
   * Two steps, and the split is the point. The DOMAIN classification — is this status
   * a fault or a refusal — is imported from {@link problemSeverity} and never
   * re-derived here, because that function owns the rule and states so: it maps a
   * refusal to a warning on the legacy evidence recorded in this class's migration
   * notes, and it treats an unrecognised status as a fault because a failure nobody
   * anticipated is the one most worth showing. The PRESENTATIONAL band is chosen here,
   * because how loudly to paint something is this component's business and not the
   * utility's.
   *
   * One refinement is applied on top of the imported classification: a rate-limit
   * refusal takes the calm band rather than the warning band. The utility groups it
   * with the other refusals, which is right for a domain classification — nothing has
   * failed — and its own wording for that status is annotated "Calm on purpose". This
   * component has a third band available to honour that intent literally, so it does.
   *
   * The status is read as the plain number it is and is deliberately NOT narrowed to a
   * union of the statuses this API is known to return. The status is chosen by the
   * server, so a union would turn an unlisted status into a compile error at the
   * consumer and force a cast — strict typing producing exactly the unsafety it exists
   * to remove. An unrecognised status is therefore ordinary rather than exceptional,
   * and it is absorbed by the imported classification's own catch-all rather than by
   * the `switch` below — which sees a three-member union, not a status. What the arms
   * of that `switch` are for is stated on each of them.
   *
   * A null document resolves to the danger band. It is unreachable through the
   * template, which renders nothing at all in that case, but the member is public and
   * must answer coherently when read directly: the utility's own contract is that an
   * absent status is a fault.
   */
  readonly severity: Signal<ErrorBannerSeverity> = computed(() => {
    const status: number | null = this.summary().status;

    if (status === TOO_MANY_REQUESTS) {
      return 'calm';
    }

    const domain: ProblemSeverity = problemSeverity(status);

    switch (domain) {
      case 'warning':
        // A refusal: the system is working as configured. This is the arm the legacy
        // YellowWarning evidence in this class's migration notes speaks to.
        return 'warning';
      case 'info':
        // Declared by the imported severity type but never currently returned by the
        // function that produces it - the rate-limit case, which is the one that would
        // most plausibly earn it, is already intercepted above. Mapped rather than left
        // to the catch-all so that the day it IS returned, an informational
        // classification lands in the calm band rather than being painted as a fault.
        return 'calm';
      case 'error':
        return 'danger';
      default:
        // Unreachable while the imported severity type has three members, and kept
        // because the workspace forbids an unhandled case and because that type is
        // free to grow a fourth without this file being edited. A new member must not
        // silently fall through to nothing.
        return 'danger';
    }
  });

  /**
   * The severity as a word, so the band is never carried by colour alone.
   *
   * See {@link SEVERITY_LABEL} for why this exists and where its wording comes from.
   */
  readonly severityLabel: Signal<string> = computed(() => SEVERITY_LABEL[this.severity()]);

  /**
   * The short summary of the problem TYPE, as the document's title.
   *
   * Shown above the message when the document carried one and the message is not
   * simply repeating it. The utility resolves the message by preferring the
   * occurrence-specific detail and falling back to the title, so a document carrying
   * only a title would otherwise render the same sentence twice — once as the heading
   * and once as the body. Comparing the two resolved values is what prevents that, and
   * it is compared rather than assumed because either can be absent.
   */
  readonly title: Signal<string> = computed(() => {
    const { title, message } = this.summary();

    return title === message ? '' : title;
  });

  /** Whether a distinct title is worth showing above the message. */
  readonly hasTitle: Signal<boolean> = computed(() => this.title().length > 0);

  /**
   * The single sentence shown as the body of the banner.
   *
   * Never blank. The utility resolves it as the occurrence-specific detail, then the
   * problem-type title, then wording chosen from the status itself — so a document
   * carrying no text of its own still produces a readable sentence, and a rate-limit
   * refusal in particular produces its own calm wording rather than a generic failure
   * message.
   */
  readonly message: Signal<string> = computed(() => this.summary().message);

  /**
   * The per-field messages, empty when the failure carried none.
   *
   * Two sources are combined into one list, because both belong in the same place on
   * screen. The utility separates them on purpose — a message keyed to a field can sit
   * beside that field's control, whereas a message the server keyed to the request as
   * a whole has no control to sit beside — and it records that the second kind
   * "belong beside the summary". This banner IS beside the summary, so it shows them
   * together, form-level messages first, immediately under the sentence they qualify.
   *
   * Field keys arrive already lower-cased on their first character only, which is what
   * lets a key match a form control without turning `PageSize` into something that
   * matches nothing. Messages arrive already normalised to plain text with break
   * markup resolved, and any entry left with nothing renderable has already been
   * dropped, so the template can render every group unconditionally.
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

  /**
   * The identifier a person quotes when reporting this failure, or null when the
   * document carried none.
   *
   * Surfaced rather than discarded because it is the only value that joins something a
   * person saw in the browser to the request as the server recorded it — quotable in a
   * support report, and useless to anyone who does not already have the server's logs.
   * It is the server's correlation identifier, which is the value that appears on the
   * response header and in the server's own records; the utility falls back to the W3C
   * trace identifier only when no correlation identifier was published.
   * Absence is ordinary: the utility reports a missing or blank identifier as null,
   * which is tested for explicitly rather than by truthiness, because a blank string
   * is a legitimate value in this data and not a synonym for absent.
   */
  readonly supportReference: Signal<string | null> =
    computed(() => this.summary().supportReference);

  /** Whether there is an identifier worth quoting. */
  readonly hasSupportReference: Signal<boolean> =
    computed(() => this.supportReference() !== null);

  /**
   * The label shown for a group of messages.
   *
   * A message that belongs to the request as a whole — the one the server keys with
   * the empty string — has no field to name, so its group is labelled generically
   * rather than with a blank heading. Other groups are labelled with the field name as
   * the server spelled it, because inventing a friendly label here would require this
   * component to know every screen's vocabulary.
   *
   * The key is used whole and is never split. That matters: the failure codes this API
   * reports include one that genuinely contains a dot, and any general rule that broke
   * a key at its dots would corrupt it.
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
 * The two shapes are structurally identical today. The conversion is written out
 * anyway, because the utility's type is a contract this component consumes rather than
 * one it owns, and passing its instances straight through would make any future
 * addition to that type part of this component's rendered surface without anyone
 * choosing it.
 *
 * @param entry One field's resolved messages.
 * @returns The group to render.
 */
function toGroup(entry: FieldMessages): FieldErrorGroup {
  return { field: entry.field, messages: entry.messages };
}
