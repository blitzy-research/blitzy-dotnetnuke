import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { ModuleStore } from '../../../core/state/module.store';
import { NotificationService } from '../../../core/services/notification.service';
import { conflictMessage, fieldErrorMessage } from '../../../core/utils/form-errors.util';
import { OperationGeneration } from '../../../core/utils/operation-generation.util';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import { MODULE_IMPORT_MAX_FILE_BYTES } from '../../../core/models/module.model';

import type { ModuleImportRequest, ModuleListItem } from '../../../core/models/module.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { ModuleStoreOperation } from '../../../core/state/module.store';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// =======================================================================================
// SCREEN-LOCAL SHAPES
// =======================================================================================

/** The typed shape of this screen's form. */
interface ModuleImportFormModel {
  /**
   * The module the document will be loaded into. `number | null` rather than `number`, and the
   * distinction is load-bearing: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so ZERO names a real module
   * and cannot double as "nothing selected".
   */
  moduleId: FormControl<number | null>;

  /** The document the operator chose from their own machine. */
  file: FormControl<File | null>;
}

/** One selectable module, reduced to what a picker needs. */
interface ModuleImportChoice {
  /** The module identifier, carried through verbatim. */
  readonly value: number;

  /** Text guaranteed to be visible, so the option is always selectable. */
  readonly label: string;
}

// WORDING

/** The screen title. */
const IMPORT_TITLE = 'Import Module';

/** The lead sentence, re-authored from the paragraph inside `ModuleHelp.Text`. */
const IMPORT_SUBTITLE = 'Administrators can import content for the specified module.';

/**
 * The module field's label. MIGRATION: NET-NEW, BECAUSE THE FIELD IS NET-NEW. The legacy screen had no
 * module field at all: it read its target out of band, from a request value, at `Import.ascx.vb` L67-L68.
 */
const MODULE_FIELD_LABEL = 'Module';

/** Guidance for the module field. */
const MODULE_FIELD_HELP =
  'Select the module to import content into. Content can only be imported into a module whose ' +
  'package supports content transfer; any other module is refused when the import is submitted.';

const MODULE_PLACEHOLDER_LABEL = '<None Specified>';

/** The document field's label. */
const FILE_FIELD_LABEL = 'File';

/**
 * Renders a byte count the way an operator reads one.
 *
 * @param bytes The count to render.
 * @returns The count in the largest binary unit that divides it exactly.
 */
function formatBytes(bytes: number): string {
  const mebibyte = 1024 * 1024;

  return bytes % mebibyte === 0 ? `${bytes / mebibyte} MB` : `${Math.floor(bytes / 1024)} KB`;
}

/**
 * Guidance for the document field. The first clause is `plFile.Help` verbatim; the second states the
 * published byte limit, because a limit an operator cannot see is one they can only meet by accident.
 */
const FILE_FIELD_HELP = `Select the import file (maximum ${formatBytes(MODULE_IMPORT_MAX_FILE_BYTES)})`;

/**
 * The message shown when the chosen document is larger than the transfer contract accepts. MIGRATION:
 * NET-NEW, AND IT EXISTS BECAUSE THE READ MOVED SIDES. `Import.ascx.vb` L184 read the document on the
 * SERVER from a folder the screen had listed, so the operator never chose a file the browser had to hold
 * and no client-side size question arose.
 */
const FILE_TOO_LARGE_MESSAGE =
  `The selected file is larger than ${formatBytes(MODULE_IMPORT_MAX_FILE_BYTES)} and was not read. `
  + 'Choose a smaller file.';

/** The submit action's label. */
const IMPORT_ACTION_LABEL = 'Import';

const CANCEL_ACTION_LABEL = 'Cancel';

const FILE_REQUIRED_MESSAGE = 'Please specify the file to import';

/**
 * The message shown when no module has been chosen. MIGRATION: NET-NEW. The legacy screen could not
 * produce this message because it never asked for a module.
 */
const MODULE_REQUIRED_MESSAGE = 'Please specify the module to import into';

/**
 * The message shown when the chosen document cannot be read from the operator's machine. MIGRATION:
 * NET-NEW, BECAUSE THE READ MOVED SIDES. `Import.ascx.vb` L184 read the document on the SERVER, from the
 * portal's home directory, so a read failure there surfaced through the screen's catch-all `Error`
 * message.
 */
const FILE_UNREADABLE_MESSAGE = 'The selected file could not be read. Choose the file again.';

/**
 * The wording for a chosen document that holds nothing. The API'S OWN SENTENCE, reproduced verbatim,
 * because its import rule refuses exactly this condition: `Content` must not be null, empty OR whitespace
 * only.
 */
const FILE_EMPTY_MESSAGE = 'The submitted document is empty.';

const IMPORT_SUCCEEDED_MESSAGE = 'Content was imported into the module.';

/** Shown in place of the picker when the tenant has no modules to offer. */
const NO_MODULES_MESSAGE = 'There are no modules available to import content into.';

/** Announced while the module list is being read. */
const LOADING_MODULES_LABEL = 'Loading modules…';

/** Announced while the import is in flight. */
const IMPORTING_LABEL = 'Importing…';

// =======================================================================================
// FIXED VALUES
// =======================================================================================

/** Where the abandon action goes. */
const MODULE_LIST_ROUTE = '/modules';

// MIGRATION: the page size a picker needs is no longer declared here. The listing defaults to ten rows,
// which would hide most of a tenant's modules behind paging a picker has no way to expose, and this screen
// used to widen the SHARED listing query to compensate — resizing a sibling listing under its own operator.

const IMPORT_FILE_ACCEPT = '.xml,text/xml,application/xml';

/** The element identifier tying the module field's label to its control. */
const MODULE_FIELD_ID = 'module-import-module';

/** The element identifier tying the document field's label to its control. */
const FILE_FIELD_ID = 'module-import-file';

/** The status a refused request carries when the caller lacks authority for it. */
const FORBIDDEN_STATUS = 403;

/** The store operations whose refusals this screen is answerable for. */
/** The store operations this screen is answerable for. */
const OWN_OPERATIONS: readonly ModuleStoreOperation[] = ['importModule', 'loadChoices'];

/**
 * The refusal codes that describe the DOCUMENT, and so belong beside the document field. THESE ARE THE
 * SERVER'S SPELLINGS, NOT THE LEGACY ENUMERATION MEMBER NAMES, AND THE DISTINCTION IS THE WHOLE POINT.
 * The legacy screen selected its wording with the resource keys `NotValidXml` and `NotCorrectType`.
 */
const DOCUMENT_REFUSAL_CODES: readonly string[] = [
  /** Legacy `NotValidXml`, raised where L190-L193 failed to load the document. */
  'module.content_invalid',
  /** Legacy `NotCorrectType`, raised where L176 or L196-L197 rejected the declared type. */
  'module.content_type_mismatch',
];

/** The refusal codes that describe the MODULE, and so belong beside the module field. */
const MODULE_REFUSAL_CODES: readonly string[] = ['module.not_portable'];

/**
 * The request members the server names when it refuses one of them. .NET model-state keys, which are NOT
 * camel-cased — hence the leading capital.
 */
const CONTENT_FIELD_KEYS: readonly string[] = ['Content', 'FileName'];

/** The request member naming the target module. */
const MODULE_FIELD_KEY = 'ModuleId';

// =======================================================================================
// PURE HELPERS
// =======================================================================================

/**
 * The first of several candidates that carries visible text.
 *
 * @param candidates Values in order of preference; `null` and blank values are passed over.
 * @returns The first candidate with a non-blank trimmed value, or `null` when none has one.
 */
function firstVisible(candidates: readonly (string | null)[]): string | null {
  for (const candidate of candidates) {
    if (candidate !== null && candidate.trim().length > 0) {
      return candidate;
    }
  }

  return null;
}

/**
 * A readable, never-blank label for one module. The operator-facing title is preferred, then the
 * definition's friendly name, then its programmatic name — the same order of familiarity the legacy
 * screens presented them in.
 *
 * @param module One row of the module listing.
 * @returns Text guaranteed to be visible.
 */
function moduleChoiceLabel(module: ModuleListItem): string {
  const named = firstVisible([module.moduleTitle, module.friendlyName, module.moduleName]);

  return named ?? `${MODULE_FIELD_LABEL} ${module.moduleId}`;
}

/**
 * Reduces the listing to one option per module, in the order the server returned them. MIGRATION: THE
 * LEGACY DUPLICATE-ENTRY DEFECT IS ANNOTATED, NOT REPRODUCED. `Import.ascx.vb` L98-L117 built its file
 * picker from two independent `If` blocks — L107-L109 matching the module's programmatic name and
 * L111-L113 matching its friendly name — each of which added unconditionally.
 *
 * @param modules The listing rows, in server order.
 * @returns One option per distinct module, first occurrence winning.
 */
function toModuleChoices(modules: readonly ModuleListItem[]): readonly ModuleImportChoice[] {
  const seen = new Set<number>();
  const choices: ModuleImportChoice[] = [];

  for (const module of modules) {
    if (seen.has(module.moduleId)) {
      continue;
    }

    seen.add(module.moduleId);
    choices.push({ value: module.moduleId, label: moduleChoiceLabel(module) });
  }

  return choices;
}

// =======================================================================================
// THE SCREEN
// =======================================================================================

@Component({
  selector: 'app-module-import',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    PageHeaderComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    ErrorBannerComponent,
    EmptyStateComponent,
  ],
  templateUrl: './module-import.component.html',
  styleUrl: './module-import.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModuleImportComponent {
  // COLLABORATORS
  // Obtained by injection, never constructed, and never registered on this component: there is no
  // `providers` array here, so each of these resolves to the one application-scoped instance the
  // application root already configured. That is what lets a specification substitute any of them.

  /**
   * The command surface and the state slice for everything module-shaped. THE REFLECTION-RESOLVED
   * SINGLETON IS GONE. The legacy screen reached its data by constructing a controller inline — `Dim
   * objModules As New ModuleController` at `Import.ascx.vb` L146, L173 and again at L101 — which in turn
   * resolved its provider through a static reflection factory.
   */
  private readonly store = inject(ModuleStore);

  /** Where confirmations and authority refusals are announced. */
  private readonly notifications = inject(NotificationService);

  /** Used for both the abandon action and the post-import return. */
  private readonly router = inject(Router);

  /**
   * Used to notice that the operator left while a document was being read. Reading a document is the one
   * genuinely awaited step on this screen, so it is the one place where work can complete after the
   * screen has gone.
   */
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------------------
  // WORDING, EXPOSED TO THE TEMPLATE
  // -------------------------------------------------------------------------------------

  protected readonly title = IMPORT_TITLE;

  protected readonly subtitle = IMPORT_SUBTITLE;

  protected readonly moduleFieldLabel = MODULE_FIELD_LABEL;

  protected readonly moduleFieldHelp = MODULE_FIELD_HELP;

  protected readonly modulePlaceholderLabel = MODULE_PLACEHOLDER_LABEL;

  protected readonly fileFieldLabel = FILE_FIELD_LABEL;

  protected readonly fileFieldHelp = FILE_FIELD_HELP;

  protected readonly importActionLabel = IMPORT_ACTION_LABEL;

  protected readonly cancelActionLabel = CANCEL_ACTION_LABEL;

  protected readonly noModulesMessage = NO_MODULES_MESSAGE;

  protected readonly loadingModulesLabel = LOADING_MODULES_LABEL;

  protected readonly importingLabel = IMPORTING_LABEL;

  protected readonly moduleFieldId = MODULE_FIELD_ID;

  protected readonly fileFieldId = FILE_FIELD_ID;

  protected readonly fileAccept = IMPORT_FILE_ACCEPT;

  // -------------------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------------------

  /**
   * Reports this screen's unsaved entry to the tracker that guards both ways of leaving it. ⚠ THE ROUTE
   * DECLARES `unsavedChangesGuard` AND THIS SCREEN USED TO REGISTER NOTHING, so the gate was answered by
   * a reflective sweep over this component's fields.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.busy() === false,
  );

  protected readonly form = new FormGroup<ModuleImportFormModel>({
    moduleId: new FormControl<number | null>(null, {
      nonNullable: true,
      validators: [Validators.required],
    }),
    file: new FormControl<File | null>(null, {
      nonNullable: true,
      validators: [Validators.required],
    }),
  });

  // SCREEN-LOCAL STATE

  /** The document the operator chose, or `null` before they have chosen one. */
  private readonly _selectedFile = signal<File | null>(null);

  /** Whether the chosen document's text is being read right now. */
  private readonly _readingFile = signal(false);

  /** Whether the most recent read attempt failed. */
  private readonly _fileReadFailed = signal(false);

  /**
   * Whether the document the operator last chose exceeded the published byte limit. Held separately from
   * {@link _fileReadFailed} because the two are different events with different remedies: an unreadable
   * document can be chosen again, while an oversized one has to be replaced by a smaller one.
   */
  private readonly _fileTooLarge = signal(false);

  /** Whether the document that was read holds nothing. */
  private readonly _fileEmpty = signal(false);

  /** Whether submit has been attempted. */
  private readonly _submitAttempted = signal(false);

  private readonly _failureSuperseded = signal(false);

  /**
   * Latched when the screen is torn down, so an awaited read cannot outlive it. A plain field rather than
   * a signal: nothing renders it, and a signal that no template reads would claim a reactive relationship
   * that does not exist.
   */
  private isDestroyed = false;

  private readonly importAttempts = new OperationGeneration();

  // DERIVED VIEWS

  /** Whether the choice read is in flight. */
  protected readonly loadingModules = this.store.choicesLoading;

  protected readonly moduleChoices = computed<readonly ModuleImportChoice[]>(() =>
    toModuleChoices(this.store.choices()),
  );

  /** Whether there is at least one module to choose from. */
  protected readonly hasModuleChoices = computed<boolean>(() => this.moduleChoices().length > 0);

  protected readonly moduleChoiceSummary = computed<string | null>(() => {
    if (this.store.choicesLoading()) {
      return null;
    }

    const options = this.moduleChoices().length;

    if (options === 0) {
      return null;
    }

    const placements = this.store.choicesTotal();
    const modulePart = options === 1 ? '1 module' : `${String(options)} modules`;

    if (placements <= options) {
      return `Choosing among ${modulePart}.`;
    }

    const placementPart = placements === 1 ? '1 placement' : `${String(placements)} placements`;

    return `Choosing among ${modulePart} across ${placementPart}.`;
  });

  /** The chosen document's own name, or `null` before one is chosen. Bound as TEXT, never as markup. */
  protected readonly selectedFileName = computed<string | null>(() => {
    const file = this._selectedFile();

    return file === null ? null : file.name;
  });

  /**
   * Whether anything this screen is waiting on is outstanding. Covers the awaited read as well as the
   * request, because the read happens before the request is issued and the store cannot know about it.
   */
  protected readonly busy = computed<boolean>(() => this._readingFile() || this.store.importing());

  /** Whether the submit action should be offered. */
  protected readonly canSubmit = computed<boolean>(
    () => !this.busy() && this.hasModuleChoices() && !this.loadingModules(),
  );

  private readonly ownFailure = computed(() => {
    const failure = this.store.failure();

    if (failure === null || !OWN_OPERATIONS.includes(failure.operation)) {
      return null;
    }

    if (failure.operation === 'importModule' && this._failureSuperseded()) {
      return null;
    }

    return failure;
  });

  /** The problem document to surface, or `null`. */
  protected readonly problem = computed<ProblemDetails | null>(
    () => this.ownFailure()?.problem ?? null,
  );

  /**
   * The sentence to show when a failure this screen owns carried NO problem document. ⚠ THE FAILURE THIS
   * MAKES VISIBLE WAS COMPLETELY SILENT, AND ON THIS SCREEN IT DISABLED THE WHOLE OPERATION. The runtime
   * decoders that check each response against its published contract run inside the service's own
   * mapping, DOWNSTREAM of the interceptor's error handling — so a `200` whose body does not match its
   * contract throws a plain error with no document, no status and no support reference. {@link problem}
   * is `null` for it, and the picker's own listing read is one of the two operations this screen owns: a
   * malformed listing left the choices empty, which leaves {@link canSubmit} false forever, and the
   * banner said nothing at all.
   */
  protected readonly failureSummary = computed<string | null>(() => {
    const failure = this.ownFailure();

    if (failure === null || failure.problem !== null) {
      return null;
    }

    return failure.summary.message;
  });

  /** The failure code the server published for this screen's refusal, or `null`. */
  private readonly refusalCode = computed<string | null>(() => this.ownFailure()?.code ?? null);

  // -------------------------------------------------------------------------------------
  // WIRING
  // -------------------------------------------------------------------------------------

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.isDestroyed = true;

      this.store.cancelChoices();
    });

    // The store outlives this screen, so a previous visit's outcome and refusal are discarded before
    // anything is rendered. Without this, re-entering the screen would open onto a confirmation that has
    // already been acknowledged, or a refusal about a document that is no longer chosen.
    this.store.clearTransferOutcome();
    this.store.clearFailure();

    this.store.loadChoices();

    effect(() => {
      if (!this.store.importCompleted()) {
        return;
      }

      // `true`: the confirmation is raised immediately before a deliberate redirect and is meant to be read at the destination - the module listing.
      this.notifications.success(IMPORT_SUCCEEDED_MESSAGE, true);

      // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The shell
      // retires notifications on a completed navigation, and this one is raised in the same task as the
      // navigation below, so it was queued and swept before it could be painted.
      this.notifications.retainAcrossNavigation();

      void this.router.navigate([MODULE_LIST_ROUTE], { replaceUrl: true });
    });

    effect(() => {
      const failure = this.ownFailure();

      if (failure === null || failure.summary.status !== FORBIDDEN_STATUS) {
        return;
      }

      // ⚠ `notify` RATHER THAN THE `warning` CONVENIENCE, BECAUSE THAT HELPER CANNOT CARRY A REFERENCE. Its
      // signature takes only a message and a survives-navigation flag - the queue's own note claims a
      // failure "is the only outcome that has one to quote", which the shared classifier contradicts: it
      // resolves 403 to WARNING, and a 403 carries a correlation identifier like any other refusal.
      this.notifications.notify(
        'warning',
        failure.summary.message,
        failure.summary.supportReference,
      );
    });
  }

  // FIELD MESSAGES

  /**
   * The sentence to show beside the module field, or `null` when it has nothing to say. Precedence is
   * deliberate and runs from most specific to least: the server's own per-field message first, because it
   * describes the request that was actually rejected; then the refusal that names the module as the
   * reason; then this screen's own requirement.
   */
  protected moduleError(): string | null {
    const problem = this.problem();
    const reported = fieldErrorMessage(problem, MODULE_FIELD_KEY);

    if (reported !== null) {
      return reported;
    }

    const refusal = this.refusalFor(MODULE_REFUSAL_CODES);

    if (refusal !== null) {
      return refusal;
    }

    return this.shouldShowRequired(this.form.controls.moduleId) ? MODULE_REQUIRED_MESSAGE : null;
  }

  protected fileError(): string | null {
    if (this._fileTooLarge()) {
      return FILE_TOO_LARGE_MESSAGE;
    }

    if (this._fileReadFailed()) {
      return FILE_UNREADABLE_MESSAGE;
    }

    if (this._fileEmpty()) {
      return FILE_EMPTY_MESSAGE;
    }

    const problem = this.problem();

    for (const key of CONTENT_FIELD_KEYS) {
      const reported = fieldErrorMessage(problem, key);

      if (reported !== null) {
        return reported;
      }
    }

    const refusal = this.refusalFor(DOCUMENT_REFUSAL_CODES);

    if (refusal !== null) {
      return refusal;
    }

    return this.shouldShowRequired(this.form.controls.file) ? FILE_REQUIRED_MESSAGE : null;
  }

  // -------------------------------------------------------------------------------------
  // INTERACTION
  // -------------------------------------------------------------------------------------

  /**
   * Records the document the operator chose. MIGRATION: THE TWO FILESYSTEM PICKERS ARE DROPPED AND
   * REPLACED BY A REAL UPLOAD. This is the single largest functional change on the screen, and it is
   * forced rather than chosen.
   *
   * @param event The change event raised by the document input.
   */
  protected onFileSelected(event: Event): void {
    // Narrowed rather than asserted. The event's target is typed as the general event target, and a
    // non-null assertion or a cast here would be a claim the compiler cannot check; `instanceof` is a claim
    // it can.
    const target = event.target;
    const chosen = target instanceof HTMLInputElement ? (target.files?.item(0) ?? null) : null;

    // THE SIZE IS DECIDED HERE, FROM METADATA, AND NOTHING IS READ. `File.size` is a byte count the browser
    // already holds; deciding from it costs nothing and happens before a single byte is decoded.
    const tooLarge = chosen !== null && chosen.size > MODULE_IMPORT_MAX_FILE_BYTES;

    this.importAttempts.invalidate();

    this._selectedFile.set(tooLarge ? null : chosen);
    this._fileTooLarge.set(tooLarge);
    this._fileReadFailed.set(false);
    this._fileEmpty.set(false);
    this.supersedeFailure();

    const control = this.form.controls.file;

    // The control follows the same discard, so the form is invalid for a refused document rather than
    // valid-with-something-that-cannot-be-sent, and the required-field rule is what reports it.
    control.setValue(tooLarge ? null : chosen);
    control.markAsDirty();
    control.markAsTouched();

    if (tooLarge) {
      this.notifications.error(FILE_TOO_LARGE_MESSAGE);
    }
  }

  /**
   * Notes that the operator changed their choice of module. The control's own value is written by the
   * form directive; this exists for two reasons of its own: so that a refusal held against the previous
   * choice stops being shown beside the new one, and so that changing the target abandons an attempt that
   * is still reading a document for the PREVIOUS target.
   */
  protected onModuleSelected(): void {
    this.importAttempts.invalidate();
    this.supersedeFailure();
  }

  /**
   * Reads the chosen document and submits it. THE PAYLOAD IS JSON AND THE TARGET TRAVELS IN THE BODY. The
   * endpoint is `POST /api/v1/modules/import`, whose route carries no identifier; the request contract
   * declares exactly four members — the target module, the document as text, and a descriptive folder and
   * name — and declares no upload primitive, no stream and no multipart form.
   */
  protected async submit(): Promise<void> {
    // ⚠ RE-ENTRY IS REFUSED BEFORE ANY STATE IS TOUCHED, and the ordering is the point: this returns having
    // changed NOTHING, so a refused attempt cannot alter a message, clear a flag or mark the form attempted
    // on behalf of an operator whose earlier attempt is still running.
    if (this.busy()) {
      return;
    }

    // Issued HERE — before the first await and beside nothing else that can suspend — so that every edit
    // made from this moment on is detectable by the continuation.
    const attempt = this.importAttempts.begin();

    this._submitAttempted.set(true);
    this._fileReadFailed.set(false);
    this._fileTooLarge.set(false);
    this._fileEmpty.set(false);

    // Declarative validation replaces the imperative guard at L145. Marking first is what makes the
    // messages visible for fields the operator never reached.
    this.form.markAllAsTouched();

    if (this.form.invalid) {
      return;
    }

    const file = this._selectedFile();
    const moduleId = this.form.controls.moduleId.value;

    // Presence is tested EXPLICITLY against null and never by truthiness, and this is the sentinel rule at
    // its sharpest: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so a module identifier of ZERO is an
    // ordinary module.
    if (file === null || moduleId === null) {
      return;
    }

    // RE-TESTED IMMEDIATELY BEFORE THE READ, and not because the check above is unreliable. A file
    // reference is a live handle: the document on disk can be replaced between being chosen and being
    // submitted, and the replacement's size is whatever it is.
    if (file.size > MODULE_IMPORT_MAX_FILE_BYTES) {
      this._fileTooLarge.set(true);
      this._selectedFile.set(null);
      this.form.controls.file.setValue(null);
      this.notifications.error(FILE_TOO_LARGE_MESSAGE);

      return;
    }

    this._readingFile.set(true);

    let content: string;

    try {
      content = await file.text();
    } catch {
      this._readingFile.set(false);
      this._fileReadFailed.set(true);
      this.notifications.error(FILE_UNREADABLE_MESSAGE);

      return;
    }

    this._readingFile.set(false);

    // The read is the one awaited step, so it is the one place the screen can have gone in the meantime.
    if (this.isDestroyed) {
      return;
    }

    // The VALUE COMPARISON refuses an attempt whose captured pair no longer describes what is on screen. It
    // is what makes the guarantee independent of the invalidation call sites: were a third edit path ever
    // added and its invalidation forgotten, this comparison would still refuse the stale dispatch.
    if (!this.importAttempts.isCurrent(attempt)) {
      return;
    }

    // Reference identity is the right comparison for the document and not a weaker one.
    if (this._selectedFile() !== file || this.form.controls.moduleId.value !== moduleId) {
      return;
    }

    if (content.trim().length === 0) {
      this._fileEmpty.set(true);
      this.notifications.error(FILE_EMPTY_MESSAGE);

      return;
    }

    const request: ModuleImportRequest = {
      moduleId,
      content,
      folder: null,
      fileName: file.name,
    };

    this._failureSuperseded.set(false);
    this.store.importModule(request);
  }

  /** Abandons the screen without validating anything. */
  protected cancel(): void {
    void this.router.navigate([MODULE_LIST_ROUTE]);
  }

  // -------------------------------------------------------------------------------------
  // PRIVATE HELPERS
  // -------------------------------------------------------------------------------------

  /**
   * The legacy wording for the current refusal, when it is one of the supplied codes. The WORDING is
   * never declared on this screen.
   *
   * @param codes The refusal codes this field answers for.
   * @returns The sentence, or `null` when the current refusal is not one of them.
   */
  private refusalFor(codes: readonly string[]): string | null {
    const code = this.refusalCode();

    if (code === null || !codes.includes(code)) {
      return null;
    }

    return conflictMessage(code);
  }

  /**
   * Whether a control's unmet requirement should be shown yet.
   *
   * @param control The control to test.
   * @returns True once the operator has reached the field or attempted to submit.
   */
  private shouldShowRequired(control: FormControl<number | null> | FormControl<File | null>): boolean {
    return control.hasError('required') && (control.touched || this._submitAttempted());
  }

  private supersedeFailure(): void {
    this._failureSuperseded.set(true);
  }
}
