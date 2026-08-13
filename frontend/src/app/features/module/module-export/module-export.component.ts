import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { fieldErrorMessage, isValidationProblemDetails } from '../../../core/utils/form-errors.util';
import { NotificationService } from '../../../core/services/notification.service';
import { ModuleStore } from '../../../core/state/module.store';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import type { AbstractControl, ValidationErrors } from '@angular/forms';
import type { ModuleExportRequest } from '../../../core/models/module.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { ModuleStoreFailure } from '../../../core/state/module.store';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

/** The shape of this screen's one form control. */
interface ModuleExportFormModel {
  /** The base name the operator wants the exported document labelled with. */
  readonly fileName: FormControl<string>;
}

// WORDING

/** The page heading, from `Export.ascx.resx` key `ControlTitle_exportmodule.Text`. */
const PAGE_TITLE = 'Export Module';

/** The supporting sentence beneath the heading. */
const PAGE_SUBTITLE = 'Administrators can export content for the specified module.';

/** The field label, from `Export.ascx.resx` key `plFile.Text`. */
const FILE_LABEL = 'File';

/**
 * The field help text, from `Export.ascx.resx` key `plFile.Help`. Handed to the shared form field, which
 * owns the help affordance entirely.
 */
const FILE_HELP = 'Enter the export filename';

/** The DOM id tying the label to the input, so the shared form field can associate the two. */
const FILE_CONTROL_ID = 'module-export-file-name';

/** The filename control's name within the form group. */
const FILE_NAME_CONTROL = 'fileName';

const EXPORT_ACTION_LABEL = 'Export';

/** The label on the dismissing action. */
const CANCEL_ACTION_LABEL = 'Cancel';

/**
 * The message shown when the filename is missing, from `Export.ascx.resx` key `Validation.Text`.
 * preserved verbatim even though the rule behind it is narrower.
 */
const VALIDATION_MESSAGE = 'You must specify a folder and file for export';

/** The greatest number of characters the filename field accepts. */
const FILE_NAME_MAX_LENGTH = 200;

/**
 * The message shown when the filename is longer than the field accepts. MIGRATION: NET-NEW WORDING,
 * because the legacy condition was unreachable rather than unhandled.
 */
const FILE_NAME_TOO_LONG_MESSAGE = `The export filename may be at most ${FILE_NAME_MAX_LENGTH} characters.`;

/**
 * The message shown when the module has no content to export, from `Export.ascx.resx` key
 * `NoContent.Text`. the condition moved sides, and the wording is retained for it anyway.
 */
const NO_CONTENT_MESSAGE = 'The module specified does not have any content';

/**
 * The message shown when the module cannot export, from `Export.ascx.resx` key `ExportNotSupported.Text`.
 * The legacy screen emitted this one sentence from TWO places - `Export.ascx.vb` when the module declared
 * no business controller class or was not portable, and when the resolved object turned out not to
 * implement the portability contract after all.
 */
const EXPORT_NOT_SUPPORTED_MESSAGE =
  'The module specified does not support the exporting of content';

/** The failure code the API publishes when a module cannot export its content. */
const NOT_PORTABLE_CODE = 'module.not_portable';

const NO_MODULE_ADDRESSED_MESSAGE = 'This address does not name a module to export.';

/** Reported when the loaded module does not describe the module this address names. */
const STALE_MODULE_MESSAGE =
  'The module on screen no longer matches this address, so nothing was exported. Please try again.';

/**
 * Reported when an exported document arrives after the address has moved to a different module. The
 * document is discarded rather than delivered.
 */
const STALE_EXPORT_MESSAGE =
  'The export finished after you moved to a different module, so the file was not downloaded. Please export again.';

/**
 * What the progress indicator announces while the module is being read. MIGRATION: NET-NEW, because the
 * legacy screen had nothing to announce.
 */
const LOADING_MODULE_LABEL = 'Loading module…';

/**
 * What the progress indicator announces while an export is in flight. MIGRATION: NET-NEW for the same
 * reason as {@link LOADING_MODULE_LABEL}.
 */
const EXPORTING_LABEL = 'Exporting module content…';

/**
 * The message announced when an export completes. MIGRATION: success feedback is a net addition.
 *
 * @param fileName The composed document name, already sanitised.
 * @returns One plain-text sentence.
 */
function exportCompleteMessage(fileName: string): string {
  return `Export complete. ${fileName} has been downloaded.`;
}

/**
 * The message announced when the document was produced but could not be handed to the browser. MIGRATION:
 * NET-NEW, because the failure it describes did not exist.
 */
const DOWNLOAD_FAILED_MESSAGE = 'The export document could not be saved to your device.';

/**
 * The media type the exported document is offered under. The payload is an XML document - the API
 * composes an `<?xml ... ?>` declaration and a `content` root around the module's own markup - so this
 * states what the bytes are.
 */
const EXPORT_MEDIA_TYPE = 'application/xml';

/** The characters the legacy name sanitiser removes, in the order it removed them. */
const SANITISER_REMOVED_CHARACTERS: readonly string[] = Object.freeze([
  '.',
  ' ',
  '~',
  '`',
  '!',
  '@',
  '#',
  '$',
  '%',
  '^',
  '&',
  '*',
  '(',
  ')',
  '-',
  '_',
  '+',
  '=',
  '{',
  '[',
  '}',
  ']',
  '|',
  '\\',
  ':',
  ';',
  '<',
  ',',
  '>',
  '?',
  '/',
  '"',
  "'",
]);

/**
 * The leading segment of every composed document name, from `Export.ascx.vb`.
 */
const NAME_PREFIX = 'content';

/**
 * The trailing segment of every composed document name, from `Export.ascx.vb`.
 */
const NAME_SUFFIX = 'xml';

/**
 * The separator between the composed name's segments, from `Export.ascx.vb`.
 */
const NAME_SEPARATOR = '.';

/**
 * Reads a route parameter as a module identifier.
 *
 * The router supplies a path parameter as a STRING, so a component that declared its input as a number
 * without converting would hold a string while its type said otherwise - the one class of untruth strict
 * typing cannot catch, because the value arrives from the framework rather than from typed code. A number is
 * accepted as well so the component can be constructed directly, in a specification or by a parent.
 *
 * Not-a-number is the only marker for an unusable parameter, and that choice is forced. Module identifiers
 * seed at zero, so 0 names the first module ever created; and minus one is the legacy integer absence
 * marker, which travels on this API's contracts as an ordinary transmitted value. Neither can be borrowed to
 * mean "no module". Not-a-number can, because it is not an identifier at all and cannot compare equal to
 * one.
 *
 * MIGRATION: the parameter's spelling is normalised to one name. `Export.ascx.vb` read the request
 * value `"moduleid"` in lower case while the sibling settings screen read `"ModuleId"` in mixed case; both
 * worked only because the legacy request collection compared keys case-insensitively. There is one spelling
 * here, it is the route parameter's, and the framework binds it by matching that name to this component's
 * input name - so the two cannot drift apart without the binding simply not happening.
 *
 * MIGRATION: the conversion is GUARDED, where the legacy conversion was not. `Export.ascx.vb` used a parse
 * that raises on any non-numeric text, and the enclosing handler absorbed the result into a generic
 * page-load failure - so a mistyped address produced a page-level error rather than an explanation. Anything
 * that is not a plain optionally-signed run of digits is rejected here, and so is a value too large to be
 * held exactly, which is the overflow the legacy parse would have raised on.
 *
 * @param value The route parameter, or a number supplied directly.
 * @returns The identifier, or not-a-number when the parameter names none.
 */
function toModuleId(value: string | number): number {
  if (typeof value === 'number') {
    return Number.isSafeInteger(value) ? value : Number.NaN;
  }

  const trimmed = value.trim();

  // An optionally-signed run of decimal digits and nothing else. This rejects the empty string, white space,
  // a decimal point, exponent notation and any trailing text - all of which a lenient conversion would
  // otherwise turn into a plausible-looking identifier.
  if (!/^[+-]?\d+$/.test(trimmed)) {
    return Number.NaN;
  }

  const parsed = Number.parseInt(trimmed, 10);

  return Number.isSafeInteger(parsed) ? parsed : Number.NaN;
}

/**
 * Refuses a filename that is blank once surrounding white space is discounted.
 *
 * Reports under the `required` key rather than a key of its own, so that the field's one message covers both
 * an untouched field and a field holding nothing but spaces. A reader checking for a missing value checks
 * one condition, not two.
 *
 * MIGRATION: the legacy test was untrimmed, and that is not reproduced. `Export.ascx.vb` tested
 * `txtFile.Text <> ""`, which a single space satisfies - and the name sanitiser then removed that space,
 * leaving an empty segment in the middle of the composed name. The API refuses a blank name for the same
 * reason, so enforcing it here as well states ONE rule at both ends rather than two rules that happen to
 * agree; sending a value the server is certain to refuse would spend a round trip to learn what is already
 * known. This is a fix required for the operation to succeed, and it is documented rather than made quietly.
 *
 * @param control The filename control.
 * @returns The `required` error, or null when the value carries visible text.
 */
function nonBlankFileName(control: AbstractControl<string>): ValidationErrors | null {
  return control.value.trim().length === 0 ? { required: true } : null;
}

/**
 * The module content export screen, reached at `modules/:moduleId/export`.
 *
 * Two of the legacy screen's three responsibilities survive - naming the document and asking the module for
 * its content - and the third, choosing a server folder to write it into, has no counterpart at all. The
 * legacy screen composed a name, asked the module's own portability behaviour for its content, wrapped that
 * content in a `content` element, WROTE THE RESULT TO A FILE beneath the portal's home directory and then
 * catalogued the file in a database table. Composition and wrapping are the API's now; writing and
 * cataloguing are nobody's, because no folder-listing, file-listing, upload or disk-space endpoint exists in
 * this API and the container topology has no portal home directory under a web root to write into.
 *
 * MIGRATION: the folder picker is dropped entirely and the document is downloaded instead. There is no folder
 * control, no folder signal and no folder browser on this screen, and the disk-space refusal goes with them -
 * it described a limit on a filesystem this screen no longer touches. The legacy bare catch-all handler,
 * which reported one generic sentence for every unanticipated fault, is likewise not reproduced: the API
 * answers with a structured problem document and the shared error banner presents it.
 *
 * MIGRATION: the download is entirely new and is NOT a ported stream, which is worth stating because the
 * legacy source contains something that looks like one. Its `application/octet-stream` occurrences are
 * arguments to the calls that insert or update the catalogue row - a column value in a table. The legacy
 * screen set no response header, wrote no bytes to the response and offered the operator nothing to save; it
 * redirected away and left the file on the server. Handing the document to the browser is therefore a new
 * capability, and it is the reason the object-URL handling below exists at all.
 *
 * This screen holds no data of its own. Reading the module, performing the export and recording a failure all
 * belong to the shared module store, which exposes each as a signal; this component reads those signals,
 * composes a name, and turns the document the store holds into a file. It builds no URL, sets no header,
 * speaks to no endpoint, and holds no copy of the exported document.
 *
 * MIGRATION: the module edit policy is ADDED here, guarding a screen the legacy application left open:
 * that screen contained no authorisation check of any kind, so any caller who could reach the address could
 * export a module's content. The hardening is declared on the route rather than in this component, which
 * declares no route and attaches no guard, and it is an affordance rather than an enforcement point: the
 * server is authoritative and answers a denied request with a refusal this screen then presents.
 *
 * MIGRATION: the actions are real buttons. Both legacy actions were link buttons, which rendered as anchors -
 * elements that navigate rather than act, are not activated by the space key, and are announced as links.
 * They are buttons here, which costs nothing visually and makes both properly operable from the keyboard. The
 * dismissing action also carried an attribute suppressing validation, and that behaviour is preserved
 * exactly: it neither validates nor submits.
 */
@Component({
  selector: 'app-module-export',
  standalone: true,
  // The typed form, and the four shared components this screen presents through. Nothing else: no folder
  // picker exists to import, and the shared library is consumed rather than extended.
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    ErrorBannerComponent,
  ],
  templateUrl: './module-export.component.html',
  styleUrl: './module-export.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleExportComponent {
  private readonly store = inject(ModuleStore);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The module to export.
   *
   * The name is load-bearing and must stay exactly `moduleId`. The router is configured with component input
   * binding, which delivers a route parameter to an input by MATCHING ITS NAME. Any other spelling - a
   * different case, an abbreviation, a rename - compiles cleanly, raises no warning, and simply never
   * receives the parameter, leaving the screen permanently unaddressed. It is the one identifier in this
   * file whose correctness no compiler can check.
   *
   * Declared with a transform because the parameter arrives as a string; see {@link toModuleId}, which also
   * explains why not-a-number is the only usable marker for an unusable parameter.
   *
   * Public because the strict input access check requires it - a non-public input fails to compile at every
   * consumer.
   */
  readonly moduleId = input.required<number, string | number>({ transform: toModuleId });

  /**
   * The one field this screen asks for.
   *
   * The control is non-nullable, so its value is a `string` rather than a `string | null`, the group's value
   * is fully typed rather than partial, and a reset returns it to the empty string it started at instead of
   * to null. The maximum length mirrors the attribute the legacy input carried, which is also the bound the
   * API's request contract states.
   */
  protected readonly form = new FormGroup<ModuleExportFormModel>({
    fileName: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        nonBlankFileName,
        Validators.maxLength(FILE_NAME_MAX_LENGTH),
      ],
    }),
  });

  /**
   * Whether the module is being read.
   */
  protected readonly moduleLoading = this.store.moduleLoading;

  /**
   * Whether an export is in flight.
   */
  protected readonly exporting = this.store.exporting;

  /**
   * A screen-level sentence this component raises itself, or null when it has nothing to say.
   *
   * Carries only the outcomes the shared error banner cannot, because neither arrives as a problem document:
   * a document that came back empty, and an address that names no module. Everything the server refuses
   * structurally goes to the banner instead.
   *
   * Private, and reaches the template only through {@link ModuleExportComponent.statusMessage}, so the
   * template can neither write it nor render it ahead of a refusal that should outrank it.
   */
  private readonly _notice = signal<string | null>(null);

  /**
   * The refusal whose wording this screen owns, or null when there is none.
   *
   * Exactly one code is handled here, and the reason is specific rather than stylistic: the shared refusal
   * table maps this code to the sibling screen's wording, which is the wrong sentence here. The legacy
   * wording for the two screens genuinely differs and that difference is preserved, so the sentence is owned
   * locally. See {@link EXPORT_NOT_SUPPORTED_MESSAGE}.
   */
  private readonly ownedRefusal = computed<string | null>(() => {
    const failure: ModuleStoreFailure | null = this.store.failure();

    if (failure === null || failure.operation !== 'exportModule') {
      return null;
    }

    return failure.code === NOT_PORTABLE_CODE ? EXPORT_NOT_SUPPORTED_MESSAGE : null;
  });

  /**
   * The screen-level sentence, exposed without permitting the template to write it.
   *
   * Carries only the two outcomes for which no problem document exists at all: a document that came back
   * empty on a successful response, and an address that resolved to no module. Everything the server refuses
   * structurally goes to the banner below instead, which is a richer surface.
   */
  protected readonly notice = this._notice.asReadonly();

  /**
   * The problem document to present in the shared error banner, or null when there is nothing to present.
   *
   * Narrowed to the two operations this screen performs, because the store holds one failure slot shared
   * across every module command: without the filter, a failure raised by a listing on another screen would
   * surface here.
   *
   * The owned wording is substituted into the document rather than rendered beside it. For the one code this
   * screen words itself, the server's own `detail` sentence is replaced by the legacy sentence and
   * everything else about the document is passed through untouched. Routing it through the banner rather
   * than into a plain paragraph of its own is what keeps three things that would otherwise be lost:
   *
   * * the severity classification and its visual treatment, so a refusal is presented as a refusal rather
   *   than as an indistinguishable line of body text;
   * * the problem's title, which names the class of failure;
   * * the support reference, which is the ONLY value joining what an operator saw to what the server logged
   * - and therefore the one thing they can usefully quote in a report.
   *
   * Substituting the sentence is precisely what owning the wording means here; discarding the document to
   * get the sentence would have been paying for it with the operator's ability to report the problem.
   */
  protected readonly problem = computed<ProblemDetails | null>(() => {
    const failure: ModuleStoreFailure | null = this.store.failure();

    if (failure === null) {
      return null;
    }

    if (failure.operation !== 'exportModule' && failure.operation !== 'loadModule') {
      return null;
    }

    const owned: string | null = this.ownedRefusal();

    if (owned === null) {
      return failure.problem;
    }

    // The document as the server sent it, with one member replaced. The status is taken from the resolved
    // summary when the response carried no document at all, so the banner can still classify the severity;
    // it is read as an explicit null test rather than coalesced, because a status is never defaulted.
    const status: number | undefined =
      failure.summary.status === null ? undefined : failure.summary.status;

    return { status, ...failure.problem, detail: owned };
  });

  /**
   * The page heading.
   */
  protected readonly title = PAGE_TITLE;

  /**
   * The supporting sentence beneath the heading.
   */
  protected readonly subtitle = PAGE_SUBTITLE;

  /**
   * The filename field's label.
   */
  protected readonly fileLabel = FILE_LABEL;

  /**
   * The filename field's help text.
   */
  protected readonly fileHelp = FILE_HELP;

  /**
   * The DOM id tying the filename label to its input.
   */
  protected readonly fileControlId = FILE_CONTROL_ID;

  /**
   * The greatest number of characters the filename input accepts.
   */
  protected readonly fileNameMaxLength = FILE_NAME_MAX_LENGTH;

  /**
   * The confirming action's label.
   */
  protected readonly exportLabel = EXPORT_ACTION_LABEL;

  /**
   * The dismissing action's label.
   */
  protected readonly cancelLabel = CANCEL_ACTION_LABEL;

  /**
   * What the progress indicator announces while the module is being read.
   */
  protected readonly loadingModuleLabel = LOADING_MODULE_LABEL;

  /**
   * What the progress indicator announces while an export is in flight.
   */
  protected readonly exportingLabel = EXPORTING_LABEL;

  /**
   * The composed name of the export this screen is waiting for, or null when it is waiting for none.
   *
   * A plain field rather than a signal, deliberately. It is written when a request is sent and read when the
   * document arrives, and nothing renders it - so making it reactive would add a dependency the download
   * effect must then be careful not to re-run on.
   *
   * It is also the guard that stops this screen downloading a document it did not ask for. The store is
   * shared and holds the last export performed anywhere in the application, so a screen that downloaded
   * whatever the store happened to contain would push a file at an operator who had merely navigated here.
   * While this is null, an exported document is ignored.
   */
  private pendingFileName: string | null = null;

  /**
   * The module the outstanding export was requested for, or null when none is outstanding.
   *
   * Captured at dispatch beside {@link pendingFileName} and checked again at delivery. The pair exists
   * because this screen is NOT recreated when the route moves from one module to another - both visits
   * resolve to the same route configuration - so an export can settle after the address has changed
   * underneath it. The name alone cannot detect that: it would still be a perfectly well-formed name, just
   * for the wrong module.
   */
  private pendingExportModuleId: number | null = null;

  /**
   * The object URL currently backing a downloaded document, or null when none is outstanding.
   *
   * Owned rather than leaked, and deliberately not revoked the instant the click returns. An object URL
   * revoked in the same task as the click can be withdrawn before the browser has finished resolving it,
   * which turns a working download into a silent failure on some engines.
   *
   * It is released on the next task turn instead, which is as prompt as is safe. The earlier arrangement
   * held the entry until the next export or until teardown, so a screen left open after one export kept a
   * whole exported document pinned in memory indefinitely — for as long as the operator stayed, which on an
   * administration screen can be all day. Waiting one turn clears the race (the browser has begun resolving
   * the URL by then, synchronously, during the click) without keeping anything.
   *
   * Every path still ends in a revocation, and that is what makes the deferral safe rather than hopeful: the
   * scheduled release, a new export releasing the previous entry first, an immediate release on failure, and
   * teardown releasing whatever is outstanding.
   */
  private liveObjectUrl: string | null = null;

  /**
   * The pending release of {@link liveObjectUrl}, or null when none is scheduled.
   *
   * Held so that teardown can cancel it. Without that, a screen destroyed within the turn would leave a
   * callback to run against a component that no longer exists — harmless in effect, because the release is
   * idempotent and teardown has already performed it, but a stray timer nonetheless.
   */
  private revocationTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Teardown. The revocation here is what makes the deliberate deferral described above safe: whatever is
    // outstanding when the screen goes away is released, whether an export succeeded, failed or was never
    // attempted. The exported document is discarded from the shared store at the same time, so a later visit
    // cannot open onto a document produced for a different module.
    this.destroyRef.onDestroy(() => {
      this.releaseObjectUrl();
      this.store.clearTransferOutcome();
    });

    // THE ADDRESS. Re-runs whenever the route parameter changes, which is the only thing this screen keys
    // off: there is no separate initialisation step, so navigating from one module's export screen to
    // another's resets everything rather than carrying state across.
    effect(() => {
      const id: number = this.moduleId();

      untracked(() => {
        this.resetForAddress();

        // Number.isInteger is a well-formedness test, NOT a sentinel test. Zero is the first module ever
        // created and minus one is a value this API's contracts legitimately transmit, so neither is
        // filtered here; only a parameter that named no integer at all is.
        if (Number.isInteger(id)) {
          this.store.loadModule(id);

          return;
        }

        // An address that names no module is reported immediately rather than on the first attempt to
        // export, because there is nothing the operator can do on this screen to repair it.
        this._notice.set(NO_MODULE_ADDRESSED_MESSAGE);
      });
    });

    // The module read, in either direction. Both outcomes are handled in one place because both are the same
    // event - the read settling - and splitting them invites the case where neither branch fires. While the
    // read is still in flight nothing is concluded from an absent module: null means "not yet".
    effect(() => {
      const loading: boolean = this.store.moduleLoading();
      const detail = this.store.module();

      untracked(() => {
        if (loading) {
          return;
        }

        if (detail !== null) {
          // The store is shared and root-provided, so what it publishes is whatever was read LAST - by this
          // screen or by any other module screen. A detail describing a different module must not seed this
          // form: the composed filename is built from `moduleName`, so module A's name would end up
          // labelling module B's exported document, and nothing downstream could detect the substitution.
          // Treated as "not yet read" rather than as an absent module, because the read for THIS module may
          // still be in flight.
          if (detail.moduleId !== this.moduleId()) {
            return;
          }

          this.seedFileName(detail.moduleTitle);

          return;
        }

        this.reportAbsentModule();
      });
    });

    // The exported document. The store sets this to null when a request goes out and to the document when it
    // returns, so a null carries no information and is ignored.
    effect(() => {
      const content: string | null = this.store.exportedContent();

      untracked(() => {
        if (content === null) {
          return;
        }

        this.deliver(content);
      });
    });

    // A REFUSED REQUEST. Reported here rather than only in the banner because an export that failed leaves
    // no other trace: no file arrives, and an operator who has looked away from the form would otherwise see
    // nothing at all.
    effect(() => {
      const failure: ModuleStoreFailure | null = this.store.failure();

      untracked(() => {
        if (failure === null || failure.operation !== 'exportModule') {
          return;
        }

        this.pendingFileName = null;
        this.pendingExportModuleId = null;
        this.announceFailure(failure);
      });
    });
  }

  /**
   * Whether this address names a module at all.
   *
   * A getter rather than a derived signal so it is available regardless of where it appears in the field
   * order, and reactive all the same: reading the input's signal from a template registers the dependency.
   *
   * The test is for well-formedness, not for a sentinel. Zero passes, because zero is the first module ever
   * created; minus one passes, because it is a value this API's contracts legitimately transmit.
   */
  protected get addressesModule(): boolean {
    return Number.isInteger(this.moduleId());
  }

  /**
   * Whether there is actually a module in hand to export.
   *
   * This is the confirming action's gate, and it is a stricter test than {@link addressesModule} on
   * purpose: a well-formed address is not the same thing as a module that was successfully read. The
   * server refuses `GET /api/v1/modules/{id}` with 403 when the caller may not see the module, and with
   * 404 when there is none; either way this screen ends up holding nothing, and pressing Export in that
   * state could never succeed. Worse, it USED to make the screen lie: {@link submit} clears the store
   * failure as it begins, so the click erased the banner that was accurately reporting the refusal and
   * replaced it with "This address does not name a module to export" - a sentence about the address,
   * for a condition that has nothing to do with the address.
   *
   * The conditions are exactly {@link submit}'s own preconditions, so a press is offered when and only
   * when it can be carried out. A refusal is therefore presented by the shared banner alone, which is
   * the same presentation the sibling module form and settings screens give the same status.
   *
   * The identifier comparison is part of the gate rather than an extra: the store is shared and
   * root-provided, so it may hold a detail read by another module screen, and a press in that state
   * would send THIS address while composing the filename from THAT module's name.
   *
   * A getter rather than a derived signal, for the reason {@link addressesModule} documents: reading
   * the input's signal and the store's signals from a template registers the dependencies either way.
   *
   * ⚠ THIS IS NOT A PORTABILITY TEST, and it must never become one. Whether a module supports exporting
   * is the server's answer, given in words, and the request is deliberately always sent for a module
   * that was read - see {@link submit}, which records why the legacy portability bit is not
   * interpreted here.
   */
  protected get hasModuleToExport(): boolean {
    if (!this.addressesModule) {
      return false;
    }

    const detail = this.store.module();

    return detail !== null && detail.moduleId === this.moduleId();
  }

  /**
   * The single message to show beside the filename field, or null when it has none.
   *
   * A getter rather than a derived signal, and that is required rather than preferred: two of the three
   * conditions below are read from a form control, which is not reactive, so a derived signal would cache
   * its first answer and never recompute when the field changed. A getter is re-read on each check, and the
   * change detection that a keystroke in this component's own template triggers is what causes that check.
   *
   * The shared form field takes ONE message per field, so the order here is the priority: the client's own
   * rules first, because they describe something the operator can fix immediately, and the server's field
   * message only when the form itself is satisfied.
   */
  protected get fileNameError(): string | null {
    const control = this.form.controls.fileName;

    // Messages appear once the operator has engaged with the field, or once a submission has marked
    // everything touched - not while the field is still untouched and empty by nature.
    if (control.touched || control.dirty) {
      if (control.hasError('required')) {
        return VALIDATION_MESSAGE;
      }

      if (control.hasError('maxlength')) {
        return FILE_NAME_TOO_LONG_MESSAGE;
      }
    }

    const failure: ModuleStoreFailure | null = this.store.failure();

    if (failure !== null && failure.operation === 'exportModule') {
      return fieldErrorMessage(failure.problem, FILE_NAME_CONTROL);
    }

    return null;
  }

  /**
   * Asks the API for the module's content and, when it arrives, hands it to the browser as a file.
   *
   * Replaces `Export.ascx.vb`, the legacy confirming click handler.
   *
   * MIGRATION: the positional contract is gone. Three positional arguments become one identifier and one
   * request object, and the overloaded status string becomes an HTTP status with a published failure code -
   * which is what allows a missing module to be told apart from a successful export at all.
   *
   * MIGRATION: portability is not tested here, and the attempt is always allowed. The legacy handler's
   * helper gated on the module declaring a business controller class and being portable, where portability
   * was a read-only property derived from an integer bit field that was excluded from serialisation and so
   * never travelled. The contract this screen reads carries no resolved portability flag - it exists only on
   * the separate definition contract, which this screen does not fetch, because fetching it would add a
   * second round trip the legacy screen never made in order to pre-empt an answer the server already gives.
   * So the confirming action is never disabled on portability grounds, the request is always sent, and a
   * module that cannot export is reported through the server's own refusal. The bit field is not interpreted
   * here under any circumstances.
   */
  protected submit(): void {
    this._notice.set(null);
    this.store.clearFailure();

    const id: number = this.moduleId();

    if (!Number.isInteger(id)) {
      this._notice.set(NO_MODULE_ADDRESSED_MESSAGE);

      return;
    }

    const detail = this.store.module();

    // MIGRATION: a missing module is a failure, checked before the field rule rather than after it. The
    // legacy helper's outer test at `Export.ascx.vb` had no `Else`, so a module that could not be read left
    // the status string empty - and an empty status string was the success signal, so the screen navigated
    // away as though the export had worked.
    //
    //   The order here is deliberate, not an incidental detail. The legacy gate tested the
    //   fields first and only then reached the helper, and reproducing that order re-introduces a smaller
    //   version of the same dishonesty: the filename is never prepopulated for a module that was not read,
    //   so the fields fail first and the operator is told to supply a filename - an instruction that cannot
    //   possibly help, for a module that does not exist. A condition the operator cannot repair outranks one
    //   they can.
    if (detail === null) {
      this._notice.set(NO_MODULE_ADDRESSED_MESSAGE);

      return;
    }

    // The detail must describe the module this screen addresses. It comes from a shared store, so a detail
    // left there by another module screen - or by a read for the module this route used to name - would
    // otherwise supply `moduleName` for the composed filename while the request below carries a different
    // identifier. The result is module B's data delivered under module A's filename.
    if (detail.moduleId !== id) {
      this._notice.set(STALE_MODULE_MESSAGE);

      return;
    }

    if (this.form.invalid) {
      // Marking everything touched makes every field-level message visible at once, rather than revealing
      // them one at a time as the operator visits each field.
      this.form.markAllAsTouched();

      return;
    }

    const typedName: string = this.form.getRawValue().fileName;

    // Composed BEFORE the request is sent, and held rather than recomputed later, so the name belongs
    // unambiguously to this request: the module could in principle be re-read while the export is in flight,
    // and a name derived after the fact could then describe a different module.
    this.pendingFileName = this.composeFileName(detail.moduleName, typedName);

    // Captured alongside the name, for the same reason and checked at delivery: the route can move to
    // another module while an export is in flight, and a document must never be handed over unless it is
    // still the module the operator is looking at.
    this.pendingExportModuleId = id;

    // The operator's text travels exactly as typed. The API neither derives a name from it nor stores
    // anything under it - it labels the response and is validated for presence - so sanitising it before
    // transmission would discard the operator's intent without gaining anything. The composed name above is
    // what the sanitiser applies to, because that is the value that becomes a filename.
    //
    // The folder member is present because the contract declares it, and it is null because this screen has
    // no folder to name: the contract accepts it purely so a caller migrating from the legacy screen is not
    // forced to discard a value, and the API resolves it against nothing. No folder control, signal or field
    // exists on this screen to populate it from.
    const request: ModuleExportRequest = { fileName: typedName, folder: null };

    this.store.exportModule(id, request);
  }

  /**
   * Leaves the screen without exporting anything.
   *
   * MIGRATION: neither validates nor submits, preserving the legacy behaviour exactly. The legacy dismissing
   * action carried `causesvalidation="False"` and its handler did nothing but redirect. Nothing here touches
   * the form, so a half-typed or invalid filename is abandoned silently rather than being marked up on the
   * way out.
   *
   * MIGRATION: the destination is the nearest equivalent rather than the same one. The legacy handler
   * redirected to the current page's own address, which returned the operator to the portal page that was
   * hosting the module - a page assembled by the server from skins and containers, and a concept with no
   * counterpart here. The module's administration screen is the closest destination that exists, so that is
   * where this goes; when the address named no module there is no module-scoped destination at all and it
   * returns to the application root. The destination is expressed as a path, not as an import of a
   * neighbouring screen.
   */
  protected cancel(): void {
    const id: number = this.moduleId();

    void this.router.navigate(Number.isInteger(id) ? ['/modules', id, 'settings'] : ['/']);
  }

  /**
   * Returns every piece of per-address state to its starting point.
   *
   * Called whenever the route parameter changes, including on the first render. The store is shared and
   * application-wide, so until this screen replaces them the module, the last exported document and the last
   * failure all still belong to whichever screen ran before it.
   *
   * Discarding the loaded module is the part that is easy to omit and matters most: without it the
   * prepopulation path would seed the filename from the PREVIOUS module's title during the window before
   * this module's read returns, and the operator would export one module under another's name.
   */
  private resetForAddress(): void {
    this.releaseObjectUrl();
    this.pendingFileName = null;
    this.pendingExportModuleId = null;
    this._notice.set(null);

    this.store.clearFailure();
    this.store.clearTransferOutcome();
    this.store.clearModule();

    // A non-nullable control resets to the empty string it was declared with rather than to null, and the
    // group returns to pristine and untouched - which is what makes the pristine test in seedFileName mean
    // "the operator has not typed here yet".
    this.form.reset();
  }

  /**
   * Suggests a filename from the module's title.
   *
   * Reproduces `Export.ascx.vb`, including its tolerance of a module that could not be read: the legacy
   * screen guarded the assignment with `If Not objModule Is Nothing` and left the box empty rather than
   * failing, and an absent title is treated the same way here.
   *
   * MIGRATION: the suggestion and the filename come from different fields, and the asymmetry is reproduced.
   * They are distinct fields with distinct purposes and a third, the definition's display name, sits
   * alongside them; the legacy class declared all three separately and this screen keeps them separate. The
   * consequence is visible and intended: what an operator sees suggested in the field is not the first
   * segment of the file they receive. Collapsing the two would be tidier and would change the produced
   * filename, so it is left exactly as it was.
   *
   * @param moduleTitle The module's heading, or null when it has none.
   */
  private seedFileName(moduleTitle: string | null): void {
    const control = this.form.controls.fileName;

    // Only while the operator has not engaged with the field. The legacy screen prepopulated on a first
    // render only and left the value untouched on every postback; a pristine, untouched control expresses
    // the same condition against a form instead of against a page lifecycle.
    if (control.dirty || control.touched) {
      return;
    }

    control.setValue(moduleTitle === null ? '' : this.cleanName(moduleTitle));
  }

  /**
   * Reports that the address resolved to no module, once the read has settled.
   *
   * MIGRATION: a missing module is a failure, reported at the earliest point rather than the latest. The
   * legacy screen learned nothing from a module it could not read: the helper's outer test at
   * `Export.ascx.vb` had no `Else`, the status string stayed empty, and read an empty status string as
   * success and navigated away. Reporting the condition only when the operator finally attempts an export
   * would be a smaller version of the same problem, because the filename is never prepopulated for a module
   * that was not read - so the attempt fails the field rule first and the operator is told to supply a
   * filename for a module that does not exist. Saying it as soon as the read settles is the only ordering
   * that describes the actual situation. It is the same treatment a malformed address gets, for the same
   * reason: nothing on this screen can repair either one.
   */
  private reportAbsentModule(): void {
    // A malformed address has already been reported with this same sentence by the address path, and
    // repeating it would be the only way to end up saying it twice.
    if (!Number.isInteger(this.moduleId())) {
      return;
    }

    // A read that FAILED is already presented structurally by the shared banner, which carries the server's
    // own sentence, its severity and a support reference - strictly more than this sentence conveys. This
    // branch is for a read that SUCCEEDED and carried no module.
    const failure: ModuleStoreFailure | null = this.store.failure();

    if (failure !== null && failure.operation === 'loadModule') {
      return;
    }

    this._notice.set(NO_MODULE_ADDRESSED_MESSAGE);
  }

  /**
   * Turns an exported document into a file on the operator's device.
   *
   * @param content The document as the API returned it, held opaquely. It is never parsed, inspected,
   * reformatted or searched: it is module-authored markup, which makes it the least trustworthy string this
   * screen handles, and it travels from the store to a binary container without being read.
   */
  private deliver(content: string): void {
    const fileName: string | null = this.pendingFileName;
    const exportedModuleId: number | null = this.pendingExportModuleId;

    // Not this screen's document. The store is shared and may already hold an export performed elsewhere,
    // and pushing a file at an operator who merely navigated here would be a surprise at best.
    if (fileName === null) {
      return;
    }

    // The exfiltration guard. This is the one sink on this screen that puts tenant data onto the operator's
    // device, and it cannot be taken back once it fires. The route can move from one module to another
    // WITHOUT this component being recreated - both visits resolve to the same route configuration - so an
    // export requested for module a can settle after the screen has moved to module B. Delivering it then
    // writes A's data to a file the operator will read as B's.
    //
    // The captured identifier is compared against the route rather than against the store, because the route
    // is what the operator is actually looking at.
    if (exportedModuleId !== this.moduleId()) {
      this.pendingFileName = null;
      this.pendingExportModuleId = null;
      this._notice.set(STALE_EXPORT_MESSAGE);

      return;
    }

    this.pendingFileName = null;
    this.pendingExportModuleId = null;

    // MIGRATION: an empty document is the legacy "no content" OUTCOME, PRESERVED ON THIS SIDE. The legacy
    // branch at `Export.ascx.vb` tested the module's payload before wrapping it and reported the no-content
    // sentence when it was empty. The API wraps an empty payload and answers successfully, so it publishes
    // no code for this condition - but both the client service and the store deliberately keep an empty
    // document distinct from an absent one, and that distinction is what this branch consumes. A file
    // containing nothing is reported rather than downloaded.
    if (content.length === 0) {
      this._notice.set(NO_CONTENT_MESSAGE);

      return;
    }

    this.save(fileName, content);
  }

  /**
   * Reports a refused export.
   *
   * @param failure The refusal as the store resolved it.
   */
  private announceFailure(failure: ModuleStoreFailure): void {
    // A refusal the server keyed to this field is already shown beside the field, so repeating it here would
    // state the same thing twice in two places. The narrowing guard and the key matching both belong to the
    // shared utility: the keys are the server's model-state spelling, are not camel-cased, and are read from
    // an index signature, so the matching is deliberately not attempted here.
    if (
      isValidationProblemDetails(failure.problem) &&
      fieldErrorMessage(failure.problem, FILE_NAME_CONTROL) !== null
    ) {
      return;
    }

    const message: string =
      failure.code === NOT_PORTABLE_CODE ? EXPORT_NOT_SUPPORTED_MESSAGE : failure.summary.message;

    // The severity is the shared resolution's rather than this screen's. It classifies a denied request and
    // a missing module as warnings rather than as faults, which matches the legacy access-denied screen's
    // own use of the warning presentation in both of its branches. Its three values are all notification
    // severities, so nothing is remapped and no fault severity can be reached by accident.
    // ⚠ THE SUPPORT REFERENCE TRAVELS WITH IT. The summary has carried a `supportReference` member all
    // along, and dropping it here threw away the only join key between what an operator saw in the
    // browser and the request as the server recorded it - the correlation identifier the server
    // validated, which is what appears on the response header, on the request envelope in its log and on
    // every audit event the request produced. A browser audit measured the asymmetry: a refusal presented
    // through the shared banner read `Reference: <id>`, while the same class of refusal presented as a
    // notification read nothing an operator could quote. The notification surface appends it AFTER its own
    // message bound, so a long server sentence cannot truncate the identifier away, and a document that
    // carried none resolves to null and is simply not quoted.
    this.notifications.notify(failure.summary.severity, message, failure.summary.supportReference);
  }

  /**
   * Composes the document's filename.
   *
   * Reproduces `Export.ascx.vb` exactly:
   *
   * ```vb
   * Dim strFile As String = "content." & CleanName(objModule.ModuleName) & "." & CleanName(txtFile.Text) & ".xml"
   * ```
   *
   * Four segments joined by full stops: a fixed prefix, the module's programmatic name, the operator's text,
   * and the extension. Both middle segments pass through the sanitiser, which is what removes the full stops
   * that would otherwise turn either segment into several.
   *
   * MIGRATION: THE FIRST SEGMENT IS THE MODULE'S PROGRAMMATIC NAME, NOT ITS TITLE. See the note on {@link
   * ModuleExportComponent.seedFileName} for the asymmetry this creates and why it is kept.
   *
   * MIGRATION: THE NAME REFERENCE IS GUARDED, WHERE THE LEGACY REFERENCE WAS NOT. `Export.ascx.vb` read the
   * module's name straight off an object it had not checked, so a module that could not be read raised a
   * null reference here - swallowed by its handler and reported as the generic page failure. The
   * contract this screen reads declares the name as possibly absent, and strict typing makes handling that
   * mandatory rather than optional. An absent name contributes an empty segment, which is what the legacy
   * produced for a name that sanitised away to nothing, so the shape of the result is unchanged.
   *
   * @param moduleName The module package's programmatic name, or null when it could not be resolved.
   * @param typedName The operator's text, exactly as typed.
   * @returns The filename to offer the document under.
   */
  private composeFileName(moduleName: string | null, typedName: string): string {
    const packageSegment: string = moduleName === null ? '' : this.cleanName(moduleName);
    const operatorSegment: string = this.cleanName(typedName);

    return [NAME_PREFIX, packageSegment, operatorSegment, NAME_SUFFIX].join(NAME_SEPARATOR);
  }

  /**
   * Removes every character the legacy name sanitiser removed, and no others.
   *
   * Reproduces `Export.ascx.vb`, which walked its bad-character set one character at a time and replaced
   * each occurrence with nothing. The loop below is that loop, with the two runtime helpers the legacy used
   * for length and for a character code replaced by the language's own facilities - the runtime library
   * those helpers came from is not carried into this migration at all.
   *
   * MIGRATION: no empty-result guard, no length cap and no trim - THE LEGACY DEFECT IS REPRODUCED, NOT
   * REPAIRED. A value consisting only of characters in the set sanitises to nothing, and the legacy produced
   * a name with an empty segment in it, such as a document called `content.<name>..xml`. That behaviour is
   * deliberately preserved: the result is still a valid, downloadable filename, so nothing about the
   * operation breaks, and inventing a substitute segment would change a name an operator may have scripted
   * against. The one adjacent condition that WOULD have broken the operation - a filename that is blank
   * before sanitising, which the API refuses outright - is handled by a validator on the field instead, and
   * that fix is documented where it is made. A trim is unnecessary in any case, because the space is itself
   * one of the characters removed.
   *
   * @param value The text to sanitise.
   * @returns The text with all thirty-three characters removed.
   */
  private cleanName(value: string): string {
    let cleaned: string = value;

    for (const character of SANITISER_REMOVED_CHARACTERS) {
      cleaned = cleaned.replaceAll(character, '');
    }

    return cleaned;
  }

  /**
   * Offers the document to the browser as a download.
   *
   * MIGRATION: this entire method is new and replaces a server-side file write. The legacy screen created a
   * text file beneath the portal's home directory and then recorded it in a catalogue table; it sent
   * nothing to the browser and gave the operator nothing to save. There is no such directory in the target
   * topology and no endpoint that would write to one, so the document is handed to the operator instead.
   * Nothing here is a translation of legacy code.
   *
   * The document is placed in a binary container as-is and is never parsed or re-serialised: no document
   * parser, no serialiser and no pattern matching over markup appears anywhere in this file.
   *
   * @param fileName The composed filename to offer the document under.
   * @param content The document, held opaquely.
   */
  private save(fileName: string, content: string): void {
    // At most one object URL is ever alive. Releasing the previous one first is what bounds that, and it
    // runs before anything that can fail.
    this.releaseObjectUrl();

    const anchor: HTMLAnchorElement = document.createElement('a');

    try {
      const objectUrl: string = URL.createObjectURL(
        new Blob([content], { type: EXPORT_MEDIA_TYPE }),
      );

      this.liveObjectUrl = objectUrl;

      anchor.href = objectUrl;
      anchor.download = fileName;
      anchor.rel = 'noopener';
      anchor.hidden = true;

      // Attached before activation because a detached element is not reliably actionable across engines, and
      // removed again in the cleanup below so no trace is left in the document either way.
      document.body.appendChild(anchor);
      anchor.click();

      this.notifications.success(exportCompleteMessage(fileName));

      // Released on the next turn rather than held: see the note on `liveObjectUrl`. Scheduled AFTER the
      // announcement so a throw from the announcement cannot leave the entry unscheduled — the catch below
      // releases it at once in that case.
      this.scheduleObjectUrlRelease(objectUrl);
    } catch {
      // Nothing was handed to the browser, so there is no download left racing this and the object URL can
      // be released at once. The document itself is not lost: it is still in the store, and activating the
      // action again retries only this step.
      this.releaseObjectUrl();
      this.notifications.error(DOWNLOAD_FAILED_MESSAGE);
    } finally {
      // Runs on every path, including a failure part-way through, so no element is ever left behind.
      // Removing an element that was never attached is a no-op.
      anchor.remove();
    }
  }

  /**
   * Releases the outstanding object URL, if there is one.
   *
   * Idempotent, so every path can call it without first checking whether it is needed. The field is cleared
   * BEFORE the revocation so that a revocation which somehow throws cannot leave a stale entry recorded as
   * live and be attempted a second time.
   */
  private releaseObjectUrl(): void {
    // Cancelled first, so a release that has already happened cannot be attempted again by a timer that was
    // still pending. Clearing a timer that is not set is a no-op.
    this.cancelScheduledRelease();

    const objectUrl: string | null = this.liveObjectUrl;

    if (objectUrl === null) {
      return;
    }

    this.liveObjectUrl = null;

    URL.revokeObjectURL(objectUrl);
  }

  /**
   * Schedules the release of one object URL for the next task turn.
   *
   * The URL is compared before releasing, so a release scheduled for one document cannot revoke a different
   * document's entry — which is what would happen if a second export began within the turn. In that case the
   * second export has already released the first entry itself, and this callback correctly does nothing.
   *
   * @param objectUrl The entry this release belongs to.
   */
  private scheduleObjectUrlRelease(objectUrl: string): void {
    this.cancelScheduledRelease();

    this.revocationTimer = setTimeout(() => {
      this.revocationTimer = null;

      if (this.liveObjectUrl === objectUrl) {
        this.releaseObjectUrl();
      }
    }, 0);
  }

  /**
   * Cancels a pending release, if one is scheduled. Idempotent.
   */
  private cancelScheduledRelease(): void {
    if (this.revocationTimer === null) {
      return;
    }

    clearTimeout(this.revocationTimer);
    this.revocationTimer = null;
  }
}
