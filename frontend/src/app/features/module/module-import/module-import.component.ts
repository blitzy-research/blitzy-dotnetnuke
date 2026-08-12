/**
 * The module content import screen, mounted at `/modules/import`.
 *
 * Replaces `Website/admin/Modules/Import.ascx` and its 245-line code-behind
 * `Website/admin/Modules/Import.ascx.vb`. The legacy screen offered two cascading server-side
 * dropdowns — a folder picker and a file picker — over documents that already had to be sitting on the
 * web server's disk, and posted back to read one of them with `File.OpenText`. Neither picker survives,
 * because the migrated API exposes no folder listing, no file listing, no upload browse and no
 * disk-space resource; the operator now chooses a document from their OWN machine and its text travels
 * inside the request body. The full reasoning is in the annotations below.
 *
 * WHAT THIS CLASS OWNS
 * -------------------
 * The form, the file-to-text read, and the routing of the server's refusals onto the field they belong
 * beside. It owns no transport and no state slice: the request is issued by `ModuleStore`, whose
 * `importing`, `importCompleted` and `failure` signals this screen renders rather than duplicating.
 *
 * WHAT IT DELIBERATELY DOES NOT DO
 * -------------------------------
 * It does not parse the document, does not read its declared type, does not unwrap its root element and
 * does not decide whether the chosen module can accept it. Every one of those was legacy client-side
 * work (`Import.ascx.vb` L188-L200) and every one of them is now the server's, so reproducing any of it
 * here would put one decision in two places and let the two disagree.
 *
 * @see ../../../core/state/module.store.ts for the command and the state slice
 * @see ../../../core/models/module.model.ts for the request contract
 */

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

/**
 * The typed shape of this screen's form.
 *
 * Declared here rather than in a shared location on purpose. A form's shape is a property of ONE
 * screen — the sibling settings, export and list screens each carry their own — so a shared
 * declaration would couple four screens that have no reason to move together. There is no barrel
 * inside this feature folder and no sibling's interface is imported.
 *
 * Both controls are constructed non-nullable, so `form.value` is fully typed rather than a
 * `Partial<…>` and `reset()` returns each control to its declared initial value instead of to `null`.
 * The declared TYPE still admits `null`, because `null` is what "nothing chosen yet" honestly is on
 * both fields — see the note on {@link ModuleImportComponent.form}.
 */
interface ModuleImportFormModel {
  /**
   * The module the document will be loaded into.
   *
   * `number | null` rather than `number`, and the distinction is load-bearing:
   * `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so ZERO names a real module and cannot double as
   * "nothing selected". `null` is the only value free to mean that.
   */
  moduleId: FormControl<number | null>;

  /**
   * The document the operator chose from their own machine.
   *
   * Holds the browser's file handle rather than its text: the text is read once, at submit, so that
   * choosing a document costs nothing and re-choosing does not read the previous one for nothing.
   */
  file: FormControl<File | null>;
}

/**
 * One selectable module, reduced to what a picker needs.
 *
 * A screen-local view shape rather than a transported one. It is deliberately not the listing
 * contract: a picker needs one identifier and one readable label, and passing the whole 17-member
 * listing row into a template would invite it to render facts this screen has no business showing.
 */
interface ModuleImportChoice {
  /** The module identifier, carried through verbatim. Zero and minus one are both real values here. */
  readonly value: number;

  /** Text guaranteed to be visible, so the option is always selectable. Never blank. */
  readonly label: string;
}

// =======================================================================================
// WORDING
// =======================================================================================
//
// Every string a person reads on this screen is authored here as a named constant, so that a
// specification can assert the rendered text against the same value the screen supplies instead of
// restating it and letting the two drift.
//
// MIGRATION: LOCALISATION IS NOT PORTED, SO THE RESOURCE FILE IS A SOURCE OF WORDING RATHER THAN A
//   RUNTIME. `Website/admin/Modules/App_LocalResources/Import.ascx.resx` holds exactly twelve
//   entries and the legacy screen resolved each through the framework's localisation API at render
//   time. The translation runtime is outside the pinned dependency set, so the wording below is taken
//   from the resource VALUES and authored directly. No localisation call is reproduced.
//
// MIGRATION: RESOURCE TEXT IS UNTRUSTED MARKUP AND IS RE-AUTHORED AS TEXT. Across the in-scope
//   resource files 76 values carry an HTML tag and four carry a script element - one of them a live
//   remote-sourced advertising block in the portal settings resources - so binding any resource value
//   as markup would be an injection vector rather than a formatting convenience. This screen's own
//   `ModuleHelp.Text` is `'<h1>Import Module</h1><p>Administrators can import content for the
//   specified module.</p>'`; its heading is already the page title, so only its sentence is carried
//   over, as plain text. The same rule governs the chosen document's own name and anything the server
//   echoes back: both are untrusted input, both are bound as text, and neither is ever markup.

/** The screen title. Taken from `ControlTitle_importmodule.Text`. */
const IMPORT_TITLE = 'Import Module';

/** The lead sentence, re-authored from the paragraph inside `ModuleHelp.Text`. */
const IMPORT_SUBTITLE = 'Administrators can import content for the specified module.';

/**
 * The module field's label.
 *
 * MIGRATION: NET-NEW, BECAUSE THE FIELD IS NET-NEW. The legacy screen had no module field at all: it
 *   read its target out of band, from a request value, at `Import.ascx.vb` L67-L68. The migrated
 *   route carries no parameter, so the target is chosen here instead — see the note on
 *   {@link ModuleImportComponent.moduleChoices}.
 */
const MODULE_FIELD_LABEL = 'Module';

/**
 * Guidance for the module field. Net-new alongside the field itself.
 *
 * ⚠ THE SECOND SENTENCE STATES A CONSTRAINT THE PICKER CANNOT ENFORCE, AND STATING IT IS THE FIX.
 *   `Import.ascx.vb:L177` guarded the whole transfer on
 *   `objModule.BusinessControllerClass <> "" And objModule.IsPortable` and `L214` answered a target
 *   failing that guard with the `ImportNotSupported` wording - a refusal at the moment of submission,
 *   because the legacy screen had no module field to withhold anything from. The migrated endpoint
 *   reproduces exactly that refusal (`module.not_portable`, measured live as "Module 2 does not support
 *   content import."), so the behaviour is preserved; what an operator lacked was any warning BEFORE
 *   choosing. Withholding such modules from the picker instead was considered and REFUSED: the
 *   definition catalogue this screen could consult publishes `isPortable` but NOT
 *   `businessControllerClass`, so a client-side test would evaluate half the server's predicate, and a
 *   half-evaluated predicate either withholds a legitimate destination silently - which this screen's
 *   own contract forbids, see `moduleChoiceSummary` - or labels a usable one as unusable. The server
 *   stays the single authority on the predicate and this sentence is what makes it predictable.
 */
const MODULE_FIELD_HELP =
  'Select the module to import content into. Content can only be imported into a module whose ' +
  'package supports content transfer; any other module is refused when the import is submitted.';

/**
 * The wording of the picker's opening option: `"<" + None_Specified + ">"` where the shared resource
 * value of `None_Specified.Text` is `None Specified`. The angle brackets are part of the legacy display
 * string, not markup.
 *
 * ⚠ THIS OPTION EXISTS BECAUSE ITS ABSENCE LEFT THE CONTROL WITH NO SELECTED OPTION AT ALL, AND THE
 *   LEGACY SCREEN SEEDED THE SAME WORDING INTO THE SAME CONTROL. An earlier revision of this file
 *   argued that a placeholder should not be invented because no wording was published for one and
 *   opening with nothing selected was the honest representation of "nothing chosen yet". Both halves
 *   were wrong. The wording IS published - `Import.ascx.vb:L72` inserts
 *   `"<" + Localization.GetString("None_Specified") + ">"` at index 0 of THIS screen's own picker, and
 *   two sibling screens render the identical string - and a select whose value matches no option
 *   renders with `selectedIndex` -1, which is not "nothing chosen" but a control in a state no
 *   keyboard or assistive-technology user can read back: the collapsed control shows blank, arrowing
 *   from it jumps to the first module rather than stepping from a known position, and nothing names
 *   the state. Binding this option to `null` makes the initial state a REAL, selectable, named option
 *   whose value is exactly what the control already held, so the requirement below still reports an
 *   unmade choice and choosing it again is how an operator retracts one.
 */
const MODULE_PLACEHOLDER_LABEL = '<None Specified>';

/** The document field's label. Taken from `plFile.Text`. */
const FILE_FIELD_LABEL = 'File';

/**
 * Renders a byte count the way an operator reads one.
 *
 * Declared here, immediately above its only two call sites, rather than among the helpers at the foot of
 * this file: both of those call sites are module-level message constants, so a reader meets the formatter
 * at the point it matters. Whole mebibytes are rendered as such and anything else as kibibytes, which
 * covers every value the published limit can take without introducing a general-purpose formatter that
 * would then need a general-purpose set of cases.
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
 * The message shown when the chosen document is larger than the transfer contract accepts.
 *
 * MIGRATION: NET-NEW, AND IT EXISTS BECAUSE THE READ MOVED SIDES. `Import.ascx.vb` L184 read the document
 *   on the SERVER from a folder the screen had listed, so the operator never chose a file the browser had
 *   to hold and no client-side size question arose. Reading now happens in the browser, where an
 *   arbitrary local file can be chosen, so the size is decided from `File.size` - metadata the browser
 *   already has - BEFORE any decoding. The sentence names the limit for the same reason the field's help
 *   text does.
 */
const FILE_TOO_LARGE_MESSAGE =
  `The selected file is larger than ${formatBytes(MODULE_IMPORT_MAX_FILE_BYTES)} and was not read. `
  + 'Choose a smaller file.';

/** The submit action's label. Taken from `cmdImport.Text`. */
const IMPORT_ACTION_LABEL = 'Import';

/**
 * The abandon action's label.
 *
 * `import.ascx` L16 carries `resourcekey="cmdCancel"`, but `cmdCancel.Text` is NOT among the twelve
 * entries of this screen's own resource file: it resolves against the shared global resources, where
 * `cmdCancel.Text` is `'Cancel'`. That is a fifth resource-key convention beyond the four previously
 * catalogued — a local key that falls through to the global table — and it is reported rather than
 * assumed.
 */
const CANCEL_ACTION_LABEL = 'Cancel';

/**
 * The message shown when no document has been chosen.
 *
 * MIGRATION: A HARD-CODED, UNLOCALISED ENGLISH LITERAL IS REPRODUCED VERBATIM, AND IT IS NOT THE ONE
 *   THE SIBLING SCREEN USES. `Import.ascx.vb` L157 passes the bare string
 *   `"Please specify the file to import"` straight to the message renderer — not a resource lookup,
 *   which is a defect in the legacy screen and is annotated as one. It is emphatically NOT the export
 *   screen's `Validation.Text`: that key lives in `Export.ascx.resx` alone, and there is no
 *   `Validation` key anywhere among this screen's twelve entries. The literal below is the measured
 *   value for THIS screen.
 *
 * MIGRATION: THE REQUIREMENT IS NARROWED TO THE DOCUMENT ALONE. L145's guard tested the file
 *   dropdown, and L149 then read the folder dropdown's value as well, so the legacy message stood in
 *   for both pickers. The folder picker is gone entirely, so the surviving requirement is the
 *   document — and the wording, which never named a folder, needs no adjustment to say so.
 */
const FILE_REQUIRED_MESSAGE = 'Please specify the file to import';

/**
 * The message shown when no module has been chosen.
 *
 * MIGRATION: NET-NEW. The legacy screen could not produce this message because it never asked for a
 *   module. The construction follows L157's, so the two read as one voice.
 */
const MODULE_REQUIRED_MESSAGE = 'Please specify the module to import into';

/**
 * The message shown when the chosen document cannot be read from the operator's machine.
 *
 * MIGRATION: NET-NEW, BECAUSE THE READ MOVED SIDES. `Import.ascx.vb` L184 read the document on the
 *   SERVER, from the portal's home directory, so a read failure there surfaced through the screen's
 *   catch-all `Error` message. Reading now happens in the browser and can fail on its own terms — the
 *   document was moved, renamed or made unreadable between being chosen and being submitted — which
 *   is a distinct situation and says so rather than borrowing the server's wording.
 */
const FILE_UNREADABLE_MESSAGE = 'The selected file could not be read. Choose the file again.';

/**
 * The wording for a chosen document that holds nothing.
 *
 * The API'S OWN SENTENCE, reproduced verbatim, because its import rule refuses exactly this condition:
 * `Content` must not be null, empty OR whitespace only. A person who chooses an empty document therefore
 * reads the same sentence whether the screen or the server noticed.
 */
const FILE_EMPTY_MESSAGE = 'The submitted document is empty.';

/**
 * Confirmation of a completed import.
 *
 * MIGRATION: SUCCESS FEEDBACK IS ADDED. `Import.ascx.vb` L151, and L202 inside the helper, both
 *   redirected on success and said nothing at all, so an operator could not tell a completed import
 *   from a navigation that had simply lost their input. The measured legacy message vocabulary is
 *   three-valued — 27 error, 21 warning and 12 success sites across the in-scope screens — so a
 *   completed import belongs at success severity rather than being announced as information.
 */
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

/**
 * Where the abandon action goes.
 *
 * MIGRATION: `import.ascx` L16 marks the abandon action `causesvalidation="False"` and L127-L129
 *   handles it with nothing but `Response.Redirect(NavigateURL(), True)` — no read, no write and no
 *   validation. {@link ModuleImportComponent.cancel} preserves exactly that: it navigates and touches
 *   neither the form's validity nor its touched state.
 */
const MODULE_LIST_ROUTE = '/modules';

/*
 * MIGRATION: the page size a picker needs is no longer declared here. The listing defaults to ten
 * rows, which would hide most of a tenant's modules behind paging a picker has no way to expose, and
 * this screen used to widen the SHARED listing query to compensate — resizing a sibling listing under
 * its own operator. The width is now the store's own decision, taken once inside its dedicated picker
 * command against the paging contract's published ceiling, so no screen re-states it and no screen can
 * disagree with another about it. A tenant holding more placements than one page remains a documented
 * limit of a picker rather than a silent truncation.
 */

/**
 * The document types the picker suggests.
 *
 * An affordance only. `Import.ascx.vb` L104 listed candidates with the framework's file helper
 * restricted to the `xml` extension, and this carries that intent into the browser's own picker. It is
 * NOT validation: the attribute is a hint a user can defeat, and the server adjudicates the document
 * regardless — which is exactly why no client-side check duplicates it.
 */
const IMPORT_FILE_ACCEPT = '.xml,text/xml,application/xml';

/** The element identifier tying the module field's label to its control. */
const MODULE_FIELD_ID = 'module-import-module';

/** The element identifier tying the document field's label to its control. */
const FILE_FIELD_ID = 'module-import-file';

/** The status a refused request carries when the caller lacks authority for it. */
const FORBIDDEN_STATUS = 403;

/**
 * The store operations whose refusals this screen is answerable for.
 *
 * The store is application-scoped, so its failure slice can hold a refusal raised by a different
 * screen. Filtering on the operation is what stops this screen presenting one, and it is a closed
 * list of exactly the two commands this screen issues: it reads the module list, and it imports.
 */
/**
 * The store operations this screen is answerable for.
 *
 * MIGRATION: the second entry was `listModules` — the BROWSABLE LISTING, which this screen never
 * asks for. It was there because the store's picker-choice command recorded its own failures under
 * that name, so filtering for the choice read meant claiming every grid read in the application.
 * Both halves are corrected: the store attributes the picker read to `loadChoices`, and this list
 * names that operation instead. The consequence of the old pairing was symmetrical and both
 * directions were wrong — a refusal raised by somebody else's listing appeared on this screen's
 * refusal surface, and a refusal raised by this screen's picker appeared on theirs.
 */
const OWN_OPERATIONS: readonly ModuleStoreOperation[] = ['importModule', 'loadChoices'];

/**
 * The refusal codes that describe the DOCUMENT, and so belong beside the document field.
 *
 * MIGRATION: THESE ARE THE SERVER'S SPELLINGS, NOT THE LEGACY ENUMERATION MEMBER NAMES, AND THE
 *   DISTINCTION IS THE WHOLE POINT. The legacy screen selected its wording with the resource keys
 *   `NotValidXml` (assigned at `Import.ascx.vb` L192) and `NotCorrectType` (L204 and L217). Those
 *   names never travel: the API publishes its own vocabulary inside the problem document's `type`
 *   member, and a table keyed on the legacy names could match nothing taken off the wire. The WORDING
 *   is what has to survive, and it does — `conflictMessage` in `core/utils/form-errors.util.ts` holds
 *   it verbatim from the resource files, including the "selected" and "specified" asymmetry between
 *   the two sentences, which is measured rather than tidied.
 */
const DOCUMENT_REFUSAL_CODES: readonly string[] = [
  /** Legacy `NotValidXml`, raised where L190-L193 failed to load the document. */
  'module.content_invalid',
  /** Legacy `NotCorrectType`, raised where L176 or L196-L197 rejected the declared type. */
  'module.content_type_mismatch',
];

/**
 * The refusal codes that describe the MODULE, and so belong beside the module field.
 *
 * MIGRATION: legacy `ImportNotSupported`, which L208 and L214 both produced — L214 when the module
 *   carried no business controller or was not portable, L208 when the resolved controller turned out
 *   not to implement the portability contract. Both arrive here as one code, which is correct: the
 *   distinction was about how the server discovered the module could not accept content, not about
 *   anything the operator can act on.
 */
const MODULE_REFUSAL_CODES: readonly string[] = ['module.not_portable'];

/**
 * The request members the server names when it refuses one of them.
 *
 * .NET model-state keys, which are NOT camel-cased — hence the leading capital. They are matched
 * case-insensitively by `fieldErrorMessage`, so these are the spellings the server actually sends
 * rather than a guess at what a client would prefer.
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
 * MIGRATION: THE EMPTY STRING IS DATA HERE, NOT ABSENCE — WHICH IS PRECISELY WHY THIS FUNCTION EXISTS
 *   RATHER THAN A COALESCING CHAIN. `Library/Components/Shared/Null.vb` L71-L75 returns the empty
 *   string as its string absence marker, so the legacy schema stores `''` and `null` interchangeably
 *   in name columns and neither the domain model nor the wire is free to fold one into the other. A
 *   `??` chain would consequently stop at a stored `''` and produce a picker option with no visible
 *   text, which is unselectable. Choosing on VISIBILITY rather than on nullity keeps the option
 *   usable without asserting anywhere that `''` means "missing".
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
 * A readable, never-blank label for one module.
 *
 * The operator-facing title is preferred, then the definition's friendly name, then its programmatic
 * name — the same order of familiarity the legacy screens presented them in. When a module carries no
 * visible name at all, it is named by its identifier rather than rendered as an empty option: an
 * unnamed module is still a real module and must stay selectable.
 *
 * @param module One row of the module listing.
 * @returns Text guaranteed to be visible.
 */
function moduleChoiceLabel(module: ModuleListItem): string {
  const named = firstVisible([module.moduleTitle, module.friendlyName, module.moduleName]);

  return named ?? `${MODULE_FIELD_LABEL} ${module.moduleId}`;
}

/**
 * Reduces the listing to one option per module, in the order the server returned them.
 *
 * MIGRATION: THE LEGACY DUPLICATE-ENTRY DEFECT IS ANNOTATED, NOT REPRODUCED. `Import.ascx.vb`
 *   L98-L117 built its file picker from two independent `If` blocks — L107-L109 matching the module's
 *   programmatic name and L111-L113 matching its friendly name — each of which added unconditionally.
 *   A document whose name satisfied both tests was therefore listed TWICE, with two identical labels
 *   and no way to tell them apart. This picker has the same exposure for a different reason: a listing
 *   row is a PLACEMENT, and a module flagged for every page has one row per page, so the same
 *   identifier recurs. Collapsing on the identifier is what stops a new form growing the old defect.
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
  // Exactly what the template binds to, and nothing else. `ReactiveFormsModule` supplies the typed
  // form directives; the five shared components are the design system's members for a page heading, a
  // labelled control, a busy indicator, a refusal surface and a zero-result state. The framework's
  // common-directive bundle is deliberately absent: the built-in control-flow blocks need no import at
  // all, and pulling that bundle in would re-admit the superseded structural directives beside them.
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
  // -------------------------------------------------------------------------------------
  // COLLABORATORS
  // -------------------------------------------------------------------------------------
  //
  // Obtained by injection, never constructed, and never registered on this component: there is no
  // `providers` array here, so each of these resolves to the one application-scoped instance the
  // application root already configured. That is what lets a specification substitute any of them.

  /**
   * The command surface and the state slice for everything module-shaped.
   *
   * MIGRATION: THE REFLECTION-RESOLVED SINGLETON IS GONE. The legacy screen reached its data by
   *   constructing a controller inline — `Dim objModules As New ModuleController` at
   *   `Import.ascx.vb` L146, L173 and again at L101 — which in turn resolved its provider through a
   *   static reflection factory. Nothing on this screen can be substituted while that is true. Here
   *   the collaborator arrives by injection, so the screen is testable without a database, a portal or
   *   a request.
   */
  private readonly store = inject(ModuleStore);

  /** Where confirmations and authority refusals are announced. */
  private readonly notifications = inject(NotificationService);

  /** Used for both the abandon action and the post-import return. */
  private readonly router = inject(Router);

  /**
   * Used to notice that the operator left while a document was being read.
   *
   * Reading a document is the one genuinely awaited step on this screen, so it is the one place where
   * work can complete after the screen has gone. Without this latch an operator who abandoned the
   * screen mid-read would still have their import submitted.
   */
  private readonly destroyRef = inject(DestroyRef);

  // -------------------------------------------------------------------------------------
  // WORDING, EXPOSED TO THE TEMPLATE
  // -------------------------------------------------------------------------------------

  /** @see IMPORT_TITLE */
  protected readonly title = IMPORT_TITLE;

  /** @see IMPORT_SUBTITLE */
  protected readonly subtitle = IMPORT_SUBTITLE;

  /** @see MODULE_FIELD_LABEL */
  protected readonly moduleFieldLabel = MODULE_FIELD_LABEL;

  /** @see MODULE_FIELD_HELP */
  protected readonly moduleFieldHelp = MODULE_FIELD_HELP;

  /** @see MODULE_PLACEHOLDER_LABEL */
  protected readonly modulePlaceholderLabel = MODULE_PLACEHOLDER_LABEL;

  /** @see FILE_FIELD_LABEL */
  protected readonly fileFieldLabel = FILE_FIELD_LABEL;

  /** @see FILE_FIELD_HELP */
  protected readonly fileFieldHelp = FILE_FIELD_HELP;

  /** @see IMPORT_ACTION_LABEL */
  protected readonly importActionLabel = IMPORT_ACTION_LABEL;

  /** @see CANCEL_ACTION_LABEL */
  protected readonly cancelActionLabel = CANCEL_ACTION_LABEL;

  /** @see NO_MODULES_MESSAGE */
  protected readonly noModulesMessage = NO_MODULES_MESSAGE;

  /** @see LOADING_MODULES_LABEL */
  protected readonly loadingModulesLabel = LOADING_MODULES_LABEL;

  /** @see IMPORTING_LABEL */
  protected readonly importingLabel = IMPORTING_LABEL;

  /** @see MODULE_FIELD_ID */
  protected readonly moduleFieldId = MODULE_FIELD_ID;

  /** @see FILE_FIELD_ID */
  protected readonly fileFieldId = FILE_FIELD_ID;

  /** @see IMPORT_FILE_ACCEPT */
  protected readonly fileAccept = IMPORT_FILE_ACCEPT;

  // -------------------------------------------------------------------------------------
  // THE FORM
  // -------------------------------------------------------------------------------------

  /**
   * Reports this screen's unsaved entry to the tracker that guards both ways of leaving it.
   *
   * ⚠ THE ROUTE DECLARES `unsavedChangesGuard` AND THIS SCREEN USED TO REGISTER NOTHING, so the gate
   * was answered by a reflective sweep over this component's fields. The sweep is gone — it pulled
   * `@angular/forms` into the eagerly loaded bundle for an application whose every form is lazily
   * loaded — and this registration replaces it. Without it the declaration on `modules/import` would
   * be inert.
   *
   * ⚠ THE LOSS THIS PROTECTS IS UNUSUALLY EXPENSIVE ON THIS SCREEN, which is why it is worth stating
   * rather than treating as one more form. The chosen document is held in memory as a `File` and
   * NOTHING on the server knows about it yet, so leaving discards a selection the operator has to
   * make again from their own file system — and on a large export that is a second read as well as a
   * second search. The module choice goes the same way.
   *
   * `busy()` rather than the store's flag alone: it also covers the local read of the document, which
   * happens before any request is issued and which the store therefore cannot see. Entry that is
   * already on its way to the server is not unsaved entry.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.busy() === false,
  );

  /**
   * The typed form backing both fields.
   *
   * Each control is `nonNullable`, which is what makes `form.value` fully typed and makes `reset()`
   * return to the declared initial value rather than to `null`. Both initial values ARE `null`, and
   * that is the point: on this screen `null` is the honest representation of "nothing chosen yet", and
   * it is the only value free to mean it.
   *
   * MIGRATION: IMPERATIVE VALIDATION BECOMES DECLARATIVE, AND THERE WAS NOTHING TO TRANSLATE. Measured
   *   across `Website/admin/Modules/`, the declarative validator census is: zero required-field
   *   validators, zero regular-expression validators, zero custom validators, zero range validators,
   *   zero validation summaries, and four comparison validators — all four in the settings screen.
   *   `import.ascx` has none whatsoever, and its 17 content lines contain no validator markup of any
   *   kind. The single check the legacy screen performed was the imperative test at
   *   `Import.ascx.vb` L145, whose failure branch at L157 rendered a hard-coded literal. Both controls
   *   below therefore carry a real validator where the legacy carried an `If`.
   *
   * MIGRATION: THE MINUS-ONE ABSENCE MARKER IS NOT REINTRODUCED. `Import.ascx.vb` L51 declared its
   *   target field as `Private Shadows ModuleId As Integer = -1` — an identifier seeded with the
   *   integer absence marker from `Null.vb` L41-L45. Nothing here initialises an identifier to minus
   *   one, tests one against minus one, or coalesces one to it. Minus one nevertheless remains a
   *   LEGITIMATE value to hold and to transmit, and if the form holds it, it is sent unaltered.
   */
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

  // -------------------------------------------------------------------------------------
  // SCREEN-LOCAL STATE
  // -------------------------------------------------------------------------------------
  //
  // MIGRATION: THE POSTBACK STATE MACHINE IS DELETED RATHER THAN TRANSLATED. The legacy screen's
  //   picker cascade existed only because the folder dropdown carried `AutoPostBack="true"`
  //   (`import.ascx` L7): choosing a folder round-tripped to the server so that
  //   `cboFolders_SelectedIndexChanged` (L98-L117) could rebuild the file list. The whole mechanism —
  //   the round trip, the control-state envelope that made it survivable, and the session state beside
  //   it — has a first-class replacement, so none of it is ported. For the record, the measured legacy
  //   count under `Website/admin/Modules/` is zero control-state sites and zero session sites, so
  //   nothing was lost in the deletion. State below is held in signals and updated by replacement.

  /** The document the operator chose, or `null` before they have chosen one. */
  private readonly _selectedFile = signal<File | null>(null);

  /** Whether the chosen document's text is being read right now. */
  private readonly _readingFile = signal(false);

  /** Whether the most recent read attempt failed. Cleared by choosing again. */
  private readonly _fileReadFailed = signal(false);

  /**
   * Whether the document the operator last chose exceeded the published byte limit.
   *
   * Held separately from {@link _fileReadFailed} because the two are different events with different
   * remedies: an unreadable document can be chosen again, while an oversized one has to be replaced by a
   * smaller one. Cleared by choosing again, like every other per-attempt state on this screen.
   */
  private readonly _fileTooLarge = signal(false);

  /**
   * Whether the document that was read holds nothing.
   *
   * Held APART from the read failure, because the two are different situations with different
   * sentences: one document could not be read at all, the other was read perfectly and turned out to be
   * empty. Telling somebody their readable file is unreadable would send them looking for a fault that
   * is not there. Cleared by choosing again, on the same terms as the read failure.
   */
  private readonly _fileEmpty = signal(false);

  /**
   * Whether submit has been attempted.
   *
   * Field-level messages stay hidden until either the field has been touched or submit has been
   * attempted, so a form that has only just opened is not already covered in complaints about fields
   * nobody has reached yet.
   */
  private readonly _submitAttempted = signal(false);

  /**
   * Whether the refusal currently held by the store has been superseded by a fresh edit.
   *
   * A refusal describes ONE attempt. Once the operator changes either field, the sentence beside that
   * field is about a document or a module they have already moved on from, and continuing to show it
   * is worse than showing nothing. The store's failure slice is application-scoped and offers only an
   * all-or-nothing reset, so suppressing here is preferable to clearing a slice another screen may be
   * relying on.
   */
  private readonly _failureSuperseded = signal(false);

  /**
   * Latched when the screen is torn down, so an awaited read cannot outlive it.
   *
   * A plain field rather than a signal: nothing renders it, and a signal that no template reads would
   * claim a reactive relationship that does not exist.
   */
  private isDestroyed = false;

  /**
   * Distinguishes the import attempt now completing from one the operator has already moved on from.
   *
   * ⚠ WHY A LATCH ON `isDestroyed` WAS NOT ENOUGH. {@link ModuleImportComponent.submit} captures the
   * document and the target module, then AWAITS the document's text. That await is a real suspension
   * point at which the operator remains free to use the form, so by the time the continuation resumes
   * the visible selection may name a DIFFERENT document, a DIFFERENT target module, or both — while the
   * captured locals still hold the old pair. The screen used to check only that it had not been
   * destroyed, so it went on to dispatch the captured pair regardless.
   *
   * The consequence is a cross-record write executed with full authority: the request carries the OLD
   * document's content under the OLD module's identifier, the form on screen says something else
   * entirely, and the success notification that follows appears to confirm the import the operator can
   * see. Nothing about either request is malformed, so no server-side check catches it and the audit
   * trail records a legitimate import into a module the operator never chose.
   *
   * Held per attempt. {@link ModuleImportComponent.onFileSelected} and
   * {@link ModuleImportComponent.onModuleSelected} invalidate it, so any edit made during the read
   * abandons the attempt in progress rather than letting it complete against stale values.
   */
  private readonly importAttempts = new OperationGeneration();

  // -------------------------------------------------------------------------------------
  // DERIVED VIEWS
  // -------------------------------------------------------------------------------------
  //
  // Every member below is read-only. No writable signal is exposed, so the template and a
  // specification can observe this screen's state but cannot reach in and change it.

  /**
   * Whether the choice read is in flight.
   *
   * The CHOICES flag, not the listing flag. The two are separate members of the store precisely so that
   * this screen's picker read and a sibling listing's read are distinguishable — see
   * `ModuleStore.loadChoices`.
   */
  protected readonly loadingModules = this.store.choicesLoading;

  /**
   * The selectable modules, one option per distinct module.
   *
   * MIGRATION: THIS IS HOW THE TARGET MODULE IS IDENTIFIED, AND IT IS A DELIBERATE CHANGE OF
   *   MECHANISM. The legacy screen took its target from a request value — `Import.ascx.vb` L67-L68
   *   read `Request.QueryString("moduleid")` — and the migrated endpoint is `POST /modules/import`,
   *   whose route carries NO identifier at all: the target is a member of the request body, which is
   *   why the sibling export path is `POST /modules/{moduleId}/export` and this one is not. The Angular
   *   route is likewise the bare `/modules/import` with no parameter, so nothing is bound into this
   *   screen from the address and no parameter is invented to fake one. The target is instead CHOSEN
   *   here, from the module listing, which is the only mechanism available that does not oblige the
   *   operator to hand-edit a URL.
   *
   * MIGRATION: THE QUERY-STRING CASING SPLIT IS RESOLVED BY REMOVING THE QUERY STRING. L67-L68 read
   *   the key in lower case as `"moduleid"` while the sibling settings screen reads it in Pascal case
   *   as `"ModuleId"` at `ModuleSettings.ascx.vb` L448-L449 — two spellings of one concept, tolerated
   *   only because the request-value lookup was case-insensitive. Neither spelling survives.
   *
   * MIGRATION: AN UNGUARDED IMPLICIT CONVERSION IS REPLACED BY A TYPED VALUE. The admin screens
   *   compiled with strict typing DISABLED, so L68's `Int32.Parse` of a caller-supplied string was
   *   written with no guard at all and threw on any non-numeric input, landing in the screen's
   *   catch-all. Here the identifier is never parsed from text: it is a `number` taken from the listing
   *   contract and carried through without conversion.
   */
  protected readonly moduleChoices = computed<readonly ModuleImportChoice[]>(() =>
    toModuleChoices(this.store.choices()),
  );

  /** Whether there is at least one module to choose from. */
  protected readonly hasModuleChoices = computed<boolean>(() => this.moduleChoices().length > 0);

  /**
   * How many placements the picker is choosing among, stated for the operator.
   *
   * ⚠ THIS EXISTS BECAUSE A PICKER THAT MIGHT BE INCOMPLETE IS UNUSABLE, AND AN EARLIER REVISION
   *   OF THIS SCREEN WAS EXACTLY THAT. It read the store's picker command when that command issued
   *   ONE request at the endpoint's maximum page size, so a tenant holding more than a hundred
   *   placements had every one after the hundredth silently absent from this control — and an
   *   operator importing content into one of them had no way to reach it and nothing on the screen
   *   telling them why. The store now walks every page and refuses rather than truncating, so the
   *   set really is complete; this line is what says so out loud.
   *
   * Two numbers are reported rather than one, and the pair is deliberate. The store publishes the
   * SERVER's placement total; this screen collapses placements to one option per distinct module,
   * because a module flagged for every page has one listing row per page. So a tenant can honestly
   * see "4 modules across 9 placements", and the two figures differing is information rather than
   * a discrepancy. When they agree, the simpler sentence is used.
   *
   * Null while the read is in flight and when nothing was found: the empty state and the indicator
   * are the right surfaces for those, and a count of zero rendered beside a picker that is not
   * there would be noise.
   */
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
   * Whether anything this screen is waiting on is outstanding.
   *
   * Covers the awaited read as well as the request, because the read happens before the request is
   * issued and the store cannot know about it. Without the read, the interface would appear idle for
   * the duration of a large document.
   */
  protected readonly busy = computed<boolean>(() => this._readingFile() || this.store.importing());

  /** Whether the submit action should be offered. */
  protected readonly canSubmit = computed<boolean>(
    () => !this.busy() && this.hasModuleChoices() && !this.loadingModules(),
  );

  /**
   * The refusal this screen is answerable for, or `null`.
   *
   * Filtered on the operation because the store is application-scoped: a refusal raised by another
   * screen's command must not appear here. Suppressed once superseded by a fresh edit.
   */
  private readonly ownFailure = computed(() => {
    const failure = this.store.failure();

    if (failure === null || !OWN_OPERATIONS.includes(failure.operation)) {
      return null;
    }

    // Only an IMPORT refusal is superseded by a fresh edit, and the asymmetry is deliberate. A refusal
    // of the import describes one attempt, so once the operator changes a field it is about a document
    // or a module they have moved on from. A LISTING failure describes whether this screen can function
    // at all, and editing a field neither addresses it nor makes it less true, so it stays until the
    // listing is read again.
    if (failure.operation === 'importModule' && this._failureSuperseded()) {
      return null;
    }

    return failure;
  });

  /**
   * The problem document to surface, or `null`.
   *
   * MIGRATION: THE CATCH-ALL MESSAGE IS SUBSUMED BY THE PROBLEM-DOCUMENT CONTRACT. `Import.ascx.vb`
   *   wrapped every handler in `Try … Catch exc As Exception` and funnelled anything unexpected through
   *   `ProcessModuleLoadException` (L86, L131, L161), while the helper's own bare `Catch` at L210-L211
   *   flattened every remaining fault to the single sentence `'An error occurred during the import'`.
   *   Neither is reproduced. Faults now arrive as RFC 7807 documents from one server-side handler, and
   *   the shared refusal surface renders their title, their sentence, their per-field messages and the
   *   support reference an operator can quote — none of which the flattened sentence could carry.
   *
   * MIGRATION: THE SILENT NO-OP IS CORRECTED. L148 tested `If Not objModule Is Nothing` with NO `Else`
   *   branch, so an import aimed at a module that no longer existed did nothing at all and reported
   *   nothing at all. A module the server cannot find is now a not-found response, and it surfaces
   *   here as a refusal like any other.
   *
   * MIGRATION: THE EMPTY-STRING-MEANS-SUCCESS TEST IS CORRECTED. L150 read `If strMessage = ""` as
   *   proof of success, which is the string absence marker of `Null.vb` L71-L75 pressed into service as
   *   a status flag — so any path that failed to set a message was indistinguishable from one that
   *   succeeded. Success is now the transport's own completion signal and nothing else; no string is
   *   compared against the empty string to determine an outcome anywhere on this screen.
   */
  protected readonly problem = computed<ProblemDetails | null>(
    () => this.ownFailure()?.problem ?? null,
  );

  /**
   * The sentence to show when a failure this screen owns carried NO problem document.
   *
   * ⚠ THE FAILURE THIS MAKES VISIBLE WAS COMPLETELY SILENT, AND ON THIS SCREEN IT DISABLED THE
   * WHOLE OPERATION. The runtime decoders that check each response against its published contract run
   * inside the service's own mapping, DOWNSTREAM of the interceptor's error handling — so a `200`
   * whose body does not match its contract throws a plain error with no document, no status and no
   * support reference. {@link problem} is `null` for it, and the picker's own listing read is one of
   * the two operations this screen owns: a malformed listing left the choices empty, which leaves
   * {@link canSubmit} false forever, and the banner said nothing at all. The operator was looking at
   * an import screen with no modules and no explanation.
   *
   * The store's own authored summary is read out rather than a second sentence being invented here.
   * Null whenever a document IS present, so the server's own explanation always wins. The supersession
   * rule is inherited from {@link ownFailure} rather than restated, so a stale import refusal is
   * dropped here on exactly the same terms.
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

      // ⚠ THE LEASE ON THE PICKER READ, RELEASED HERE AND NOWHERE ELSE.
      //
      // The store is provided at the root, so it outlives this screen; the choice read is started by
      // this screen and wanted by nothing else. Leaving it outstanding cost three things, all of them
      // observable on the screen that REPLACED this one rather than on this one: the store's busy
      // projection stayed true, because the choice loading flag is one of its terms, so a sibling
      // screen's affordances were disabled on account of a read belonging to a destroyed component; a
      // late refusal landed in the failure slot addressed to a screen that had gone; and the request
      // itself continued to be paid for with nobody to receive it.
      //
      // The store's own command is used rather than a handle held here, because the handle belongs to
      // the store and a component reaching for it would be a second owner of one subscription. It
      // releases ONLY the choice slice — never the whole store, which would abandon reads the
      // siblings are waiting on.
      this.store.cancelChoices();
    });

    // The store outlives this screen, so a previous visit's outcome and refusal are discarded before
    // anything is rendered. Without this, re-entering the screen would open onto a confirmation that
    // has already been acknowledged, or a refusal about a document that is no longer chosen. The store
    // publishes a reset for exactly this situation.
    this.store.clearTransferOutcome();
    this.store.clearFailure();

    // The choices are read through the store's DEDICATED PICKER COMMAND, which walks EVERY page of the
    // module listing and lands in a slice of its own.
    //
    // ⚠ COMPLETENESS IS THE STORE'S GUARANTEE AND THIS SCREEN RELIES ON IT RATHER THAN PAGING ITSELF.
    // The command follows every page the server reports and REFUSES — publishing nothing and recording
    // the reason — if it cannot finish. So this screen has exactly two states to present and no third:
    // a complete set, or a refusal in the shared banner. It deliberately does not add a pager or a
    // search box of its own, because either would imply the set on screen might be a window, which is
    // the ambiguity the store's refusal exists to eliminate; what it does add is the count, so the
    // completeness is visible rather than merely promised — see `moduleChoiceSummary`.
    //
    // MIGRATION: this screen previously widened the SHARED listing page size and re-read the shared
    // listing, which is a defect rather than a shortcut: the module listing is provided at the root, so
    // merely opening this screen resized a sibling listing under its own operator, discarded the page
    // they were on and replaced its rows. Stating the need as its own read leaves the browsable listing
    // untouched — nothing this screen does is observable on `ModuleStore.page` or `ModuleStore.query`.
    this.store.loadChoices();

    // A genuine side effect: confirm, then leave. Reading `importCompleted` is the only way a screen
    // learns the outcome, because the store's command reports through its state slice rather than
    // returning anything.
    effect(() => {
      if (!this.store.importCompleted()) {
        return;
      }

      // `true`: the confirmation is raised immediately before a deliberate redirect and is meant to be read at the destination - the module listing.
      this.notifications.success(IMPORT_SUCCEEDED_MESSAGE, true);

      // ⚠ EXEMPTED FROM THE NAVIGATION SWEEP, WITHOUT WHICH THIS CONFIRMATION IS NEVER SEEN. The
      //   shell retires notifications on a completed navigation, and this one is raised in the same
      //   task as the navigation below, so it was queued and swept before it could be painted. An
      //   import that reports nothing at all is indistinguishable from an import that did nothing.
      this.notifications.retainAcrossNavigation();

      // MIGRATION: the legacy screen redirected on success at L151, and again at L202 from inside its
      //   helper — a redirect issued mid-computation, which is why the helper's remaining branches
      //   could never be reached once it fired. The return below is the same intent expressed once, at
      //   the one place that knows the import finished.
      // Replaced, not pushed: the transfer is complete, so BACK must not return to the picker.
      void this.router.navigate([MODULE_LIST_ROUTE], { replaceUrl: true });
    });

    // A refusal on grounds of authority is a different kind of event from a fault: the system is
    // working exactly as configured and the operator simply may not do this. It is announced at warning
    // severity rather than error, and the shared refusal surface independently reaches the same
    // classification for this status, so the two never disagree.
    //
    // MIGRATION: the legacy access-denied screen rendered BOTH of its branches at warning severity
    //   (`Website/admin/Security/AccessDenied.ascx.vb` L41-L47) and performed no check of its own, and
    //   the legacy message renderer gave warning the ordinary heading style while reserving the red one
    //   for errors (`Library/Components/Skins/ModuleMessage.vb` L115-L158). The legacy application
    //   itself therefore treated a refusal as distinct from a fault, and that distinction is preserved.
    effect(() => {
      const failure = this.ownFailure();

      if (failure === null || failure.summary.status !== FORBIDDEN_STATUS) {
        return;
      }

      // ⚠ `notify` RATHER THAN THE `warning` CONVENIENCE, BECAUSE THAT HELPER CANNOT CARRY A
      // REFERENCE. Its signature takes only a message and a survives-navigation flag - the queue's own
      // note claims a failure "is the only outcome that has one to quote", which the shared classifier
      // contradicts: it resolves 403 to WARNING, and a 403 carries a correlation identifier like any
      // other refusal. This is such a 403, so the helper's shape would have silently discarded the one
      // thing an operator needs in order to escalate a refusal they cannot resolve themselves.
      this.notifications.notify(
        'warning',
        failure.summary.message,
        failure.summary.supportReference,
      );
    });
  }

  // -------------------------------------------------------------------------------------
  // FIELD MESSAGES
  // -------------------------------------------------------------------------------------
  //
  // Plain methods rather than computed signals, because a reactive form control's validity and touched
  // state are not signals: a computed over either would be evaluated once and never recomputed. Read
  // from the template they are re-evaluated whenever this view is checked, which covers the form state,
  // and the signals they also read register normally, which covers everything else.

  /**
   * The sentence to show beside the module field, or `null` when it has nothing to say.
   *
   * Precedence is deliberate and runs from most specific to least: the server's own per-field message
   * first, because it describes the request that was actually rejected; then the refusal that names the
   * module as the reason; then this screen's own requirement.
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

  /**
   * The sentence to show beside the document field, or `null` when it has nothing to say.
   *
   * The unreadable-document case is ranked above the server's messages because it is the more recent
   * event: a read that failed means nothing was sent, so any message still held from an earlier attempt
   * describes a request that has since been superseded.
   */
  protected fileError(): string | null {
    // Ranked above the unreadable case for the same reason that one is ranked above the server's
    // messages: it is the more recent event, and it is the only one of the two that describes a document
    // this screen refused on its own terms without sending anything.
    if (this._fileTooLarge()) {
      return FILE_TOO_LARGE_MESSAGE;
    }

    if (this._fileReadFailed()) {
      return FILE_UNREADABLE_MESSAGE;
    }

    // Ranked with the read failure above and for the same reason: an emptiness refusal means nothing was
    // sent, so any message still held from an earlier attempt describes a superseded request.
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
   * Records the document the operator chose.
   *
   * MIGRATION: THE TWO FILESYSTEM PICKERS ARE DROPPED AND REPLACED BY A REAL UPLOAD. This is the single
   *   largest functional change on the screen, and it is forced rather than chosen. The legacy screen
   *   listed SERVER-SIDE folders and files: `Import.ascx.vb` L72 seeded the folder dropdown with a
   *   placeholder whose stored value was the bare hyphen `"-"`, L73 filled it from the framework's
   *   folder helper restricted to the read and write permission keys, and L76-L77 displayed the root
   *   folder — whose stored path is the empty string, the string absence marker of `Null.vb` L71-L75 —
   *   under the localised label `Root`. L184 then opened the chosen document from the portal's home
   *   directory. The migrated API exposes NO folder listing, NO file listing, NO upload browse and NO
   *   disk-space resource, so there is nothing for either dropdown to read. The folder dropdown
   *   therefore disappears outright — no folder field is modelled and no folder value is composed — and
   *   the file dropdown becomes a document chosen from the operator's own machine.
   *
   * MIGRATION: THE DOCUMENT'S OWN NAME IS UNTRUSTED INPUT. It comes from the operator's filesystem, it
   *   is carried on the request as descriptive metadata that no decision depends on, and it is never
   *   resolved as a path and never rendered as markup.
   *
   * @param event The change event raised by the document input.
   */
  protected onFileSelected(event: Event): void {
    // Narrowed rather than asserted. The event's target is typed as the general event target, and a
    // non-null assertion or a cast here would be a claim the compiler cannot check; `instanceof` is a
    // claim it can. `item(0)` yields the file or null, and the optional chain covers an input that
    // carries no selection collection at all.
    const target = event.target;
    const chosen = target instanceof HTMLInputElement ? (target.files?.item(0) ?? null) : null;

    // THE SIZE IS DECIDED HERE, FROM METADATA, AND NOTHING IS READ. `File.size` is a byte count the
    // browser already holds; deciding from it costs nothing and happens before a single byte is decoded.
    // An oversized choice is DISCARDED rather than held, so the component never retains a reference to a
    // file it has already refused and a later submit has nothing oversized to find.
    const tooLarge = chosen !== null && chosen.size > MODULE_IMPORT_MAX_FILE_BYTES;

    // Choosing a document ABANDONS any attempt still reading one. Without this the read already in
    // flight would resume holding the previous document and dispatch it, so the operator's newer choice
    // would be discarded in favour of the one they had just replaced.
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
   * Notes that the operator changed their choice of module.
   *
   * The control's own value is written by the form directive; this exists for two reasons of its own: so
   * that a refusal held against the previous choice stops being shown beside the new one, and so that
   * changing the target abandons an attempt that is still reading a document for the PREVIOUS target.
   *
   * The second is not a lesser concern than changing the document. The captured target is what the
   * request's `moduleId` becomes, so an attempt allowed to complete after the target changed would load
   * the document into a module the operator had just navigated away from — see
   * {@link ModuleImportComponent.importAttempts}.
   */
  protected onModuleSelected(): void {
    this.importAttempts.invalidate();
    this.supersedeFailure();
  }

  /**
   * Reads the chosen document and submits it.
   *
   * MIGRATION: THE PAYLOAD IS JSON AND THE TARGET TRAVELS IN THE BODY. The endpoint is
   *   `POST /api/v1/modules/import`, whose route carries no identifier; the request contract declares
   *   exactly four members — the target module, the document as text, and a descriptive folder and name
   *   — and declares no upload primitive, no stream and no multipart form. This screen accordingly
   *   composes a plain object: it reads the document as TEXT and sends that string. No multipart body is
   *   built anywhere on this screen.
   *
   * MIGRATION: NEITHER THE DOCUMENT NOR ITS WRAPPER IS INSPECTED HERE. `Import.ascx.vb` L188-L193 built
   *   a document object and treated a load failure as the invalid-structure refusal, L196-L197 compared
   *   the declared type attribute against the module's own names, and L200 handed the ROOT ELEMENT'S
   *   INNER MARKUP — the wrapper stripped — to the module's portability contract along with the declared
   *   version and the acting account. Every one of those steps is now server-side, so this screen reads
   *   text and nothing more: it does not construct a document, does not read an attribute, does not
   *   unwrap a root element and does not pre-judge the structure. Duplicating any of it would put one
   *   decision in two places, and the acting account in particular is taken from the authenticated
   *   caller rather than from the request, so that no caller can attribute an import to somebody else.
   *
   * MIGRATION: THE PORTABILITY FLAG IS NEVER COMPUTED HERE. The legacy gate at L177 read
   *   `objModule.IsPortable`, which `Library/Components/Modules/ModuleInfo.vb` L608 derives by masking
   *   a bit out of a feature word (L641-L647). The listing contract this picker reads exposes no
   *   resolved portability flag — only the definition contract does, and that is a different resource —
   *   so the attempt is always allowed and the server's own refusal is what an operator sees. Masking
   *   the feature word in the browser would be re-deriving a server-side decision from data this screen
   *   does not hold.
   */
  protected async submit(): Promise<void> {
    // ⚠ RE-ENTRY IS REFUSED BEFORE ANY STATE IS TOUCHED, and the ordering is the point: this returns
    // having changed NOTHING, so a refused attempt cannot alter a message, clear a flag or mark the form
    // attempted on behalf of an operator whose earlier attempt is still running.
    //
    // The disabled submit button is not sufficient on its own. It is a rendered affordance, and this
    // method is reachable without it: the form's submit event fires on the Enter key from within either
    // field, and a re-entrant call arriving in the window before change detection has repainted the
    // button would find it still enabled. Two concurrent attempts would each read a document and each
    // dispatch, so the server would receive two imports and the second success notification would
    // navigate away from a screen whose first request was still outstanding.
    if (this.busy()) {
      return;
    }

    // Issued HERE — before the first await and beside nothing else that can suspend — so that every edit
    // made from this moment on is detectable by the continuation. Held in a local rather than a field,
    // which is what the mechanism requires: a field would be overwritten by the next attempt and the
    // earlier continuation would then compare against the newer value and admit itself.
    const attempt = this.importAttempts.begin();

    this._submitAttempted.set(true);
    this._fileReadFailed.set(false);
    this._fileTooLarge.set(false);
    this._fileEmpty.set(false);

    // The supersede latch is deliberately NOT cleared here. Clearing it up front would re-expose the
    // previous attempt's refusal on every submit that never reaches the transport - an invalid form, or
    // a document that could not be read - and would announce an authority refusal a second time for a
    // request that was never re-sent. It is cleared at the one point a new attempt genuinely begins,
    // immediately before the command is issued.

    // Declarative validation replaces the imperative guard at L145. Marking first is what makes the
    // messages visible for fields the operator never reached.
    this.form.markAllAsTouched();

    if (this.form.invalid) {
      return;
    }

    const file = this._selectedFile();
    const moduleId = this.form.controls.moduleId.value;

    // Presence is tested EXPLICITLY against null and never by truthiness, and this is the sentinel rule
    // at its sharpest: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`, so a module identifier of ZERO is an
    // ordinary module. `if (moduleId)` would silently refuse it, and so would a comparison against
    // zero, a positivity test, or a coalesce to zero or to minus one. None of those appears here. The
    // validators above have already made this branch unreachable; it stands because the compiler cannot
    // know that, and narrowing it here is what keeps the request members honestly typed.
    if (file === null || moduleId === null) {
      return;
    }

    // RE-TESTED IMMEDIATELY BEFORE THE READ, and not because the check above is unreliable. A file
    // reference is a live handle: the document on disk can be replaced between being chosen and being
    // submitted, and the replacement's size is whatever it is. This is the last instant at which the
    // question can be asked without having already decoded the answer, so it is asked here too.
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
      // A read can genuinely fail: the document may have been moved, renamed or made unreadable between
      // being chosen and being submitted. The operator is told, the choice is left in place so they can
      // re-pick, and nothing is sent.
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

    // ⚠ TWO INDEPENDENT CHECKS ACROSS THE AWAIT, AND NEITHER IS REDUNDANT.
    //
    // The TICKET refuses an attempt the operator has abandoned. It is the only one of the two that can
    // catch a re-pick of the SAME document or the SAME module, because both edits leave the compared
    // values identical while genuinely being a newer intent — and it is also the only one that answers
    // for an edit which the handler recorded but which left the control's committed value unchanged.
    //
    // The VALUE COMPARISON refuses an attempt whose captured pair no longer describes what is on screen.
    // It is what makes the guarantee independent of the invalidation call sites: were a third edit path
    // ever added and its invalidation forgotten, this comparison would still refuse the stale dispatch.
    // Together they mean the request can only carry the pair the form is displaying at the instant it is
    // issued.
    //
    // Nothing is announced when either refuses. An abandoned attempt is not a failure — the operator
    // changed their mind, and the state they changed it to is already on screen — so a message here would
    // report an error about work nobody is waiting for. The choice is left exactly as they left it.
    if (!this.importAttempts.isCurrent(attempt)) {
      return;
    }

    // Reference identity is the right comparison for the document and not a weaker one. A `File` is an
    // opaque handle, two selections of the same path yield two distinct handles, and comparing names or
    // sizes would treat a re-pick as the same choice — which is exactly the case the ticket above exists
    // to catch, so the two checks must not be made to overlap by loosening this one.
    //
    // The module is compared with `!==` against the control's CURRENT value, never by truthiness: module
    // zero is an ordinary module, so `if (!currentModuleId)` would refuse a legitimate target.
    if (this._selectedFile() !== file || this.form.controls.moduleId.value !== moduleId) {
      return;
    }

    // MIGRATION: AN EMPTY DOCUMENT IS REFUSED HERE RATHER THAN SENT. The API's import rule refuses
    // content that is null, empty or whitespace only, so transmitting it spends a request - and an upload
    // of the whole document - to learn what was already knowable the moment it was read. The condition is
    // tested exactly as the server tests it, whitespace included, so the two cannot disagree about which
    // documents are empty.
    //
    // The choice is LEFT IN PLACE and the failure flag is raised, which is the same treatment an
    // unreadable document gets: both are situations the operator resolves by choosing a different file,
    // and clearing the field would hide which file they had just tried.
    //
    // This does not weaken the note below about the empty string being data. That note is about the WIRE
    // - nothing rewrites an empty string into null on its way out - and it still holds: no empty content
    // reaches the request at all now, so no coercion of one is possible.
    if (content.trim().length === 0) {
      this._fileEmpty.set(true);
      this.notifications.error(FILE_EMPTY_MESSAGE);

      return;
    }

    // MIGRATION: EVERY MEMBER IS TRANSMITTED VERBATIM. The contract declares all four members, so all
    //   four are supplied. `folder` is sent as null because the target has no server-side folder concept
    //   left to name — the contract accepts it for parity and resolves nothing from it — and `fileName`
    //   carries the chosen document's own name as the descriptive metadata the contract documents it to
    //   be. The identifier is passed through untouched: minus one, if the form somehow holds it, is a
    //   legitimate transmitted value and is NOT rewritten to null, and the text is sent exactly as read.
    //   The guard above means an empty document never reaches here, so no coercion of one is possible;
    //   what the read produced is what travels, byte for byte, whitespace and all.
    const request: ModuleImportRequest = {
      moduleId,
      content,
      folder: null,
      fileName: file.name,
    };

    // A new attempt begins here and nowhere earlier, so this is where the previous one stops being
    // suppressed. The command clears the store's failure slice synchronously as its first act, so the
    // superseded refusal cannot reappear in between.
    this._failureSuperseded.set(false);
    this.store.importModule(request);
  }

  /**
   * Abandons the screen without validating anything.
   *
   * MIGRATION: `import.ascx` L16 marks the abandon action `causesvalidation="False"`, and its handler at
   *   L127-L129 does nothing but redirect. That is reproduced exactly: nothing below marks a control
   *   touched, re-evaluates a validator, submits, or reads the form at all.
   */
  protected cancel(): void {
    void this.router.navigate([MODULE_LIST_ROUTE]);
  }

  // -------------------------------------------------------------------------------------
  // PRIVATE HELPERS
  // -------------------------------------------------------------------------------------

  /**
   * The legacy wording for the current refusal, when it is one of the supplied codes.
   *
   * The WORDING is never declared on this screen. It is held, verbatim from the legacy resource files,
   * by the shared problem-document utility, which also normalises the code before matching it. Keeping
   * the sentences in one place is what makes the parity claim checkable in one place.
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

  /** Marks the refusal currently held by the store as describing a superseded attempt. */
  private supersedeFailure(): void {
    this._failureSuperseded.set(true);
  }
}
