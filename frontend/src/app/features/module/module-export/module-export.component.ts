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

/**
 * The shape of this screen's one form control.
 *
 * Declared locally and never shared. Each screen in this feature folder owns its own form model, so a
 * field added to a neighbouring screen cannot silently widen this one, and there is no barrel through
 * which such a model could travel.
 *
 * One control, because there is exactly one thing left to ask for - see the note on the dropped folder
 * picker in {@link ModuleExportComponent}.
 */
interface ModuleExportFormModel {
  /** The base name the operator wants the exported document labelled with. */
  readonly fileName: FormControl<string>;
}

// =======================================================================================================
// WORDING
// =======================================================================================================
//
// Every operator-facing string on this screen is taken from the VALUE of a legacy resource entry, never
// from a markup attribute. The two disagree on this screen, which is what makes the rule worth stating:
// see the note on EXPORT_ACTION_LABEL.
//
// MIGRATION: RESOURCE TEXT IS UNTRUSTED HTML AND IS RENDERED AS PLAIN TEXT. Across the in-scope legacy
//   resource files 76 values carry an HTML tag and four carry a live script element, and this screen's own
//   `ModuleHelp.Text` carries `<h1>` and `<p>`. Nothing below is bound as markup: the template
//   interpolates these as text, and no sanitiser, trusted-value wrapper or inner-HTML binding appears
//   anywhere in this component. Where the legacy wording genuinely needed a heading or a paragraph it is
//   re-authored as real template markup instead. The legacy precedent for encoding rather than trusting is
//   `Website/admin/Security/AccessDenied.ascx.vb:L43`, which HTML-encoded its message before display.
//
// MIGRATION: LOCALISATION IS NOT PORTED. The legacy screen resolved all of these through its resource
//   file at run time, falling back from the control's local file to the shared global one. That mechanism
//   is specific to the legacy page framework and no translation runtime is part of this workspace, so the
//   English values are authored directly here and the resource files are read for wording only.

/**
 * The page heading, from `Export.ascx.resx` key `ControlTitle_exportmodule.Text`.
 *
 * MIGRATION: legacy titles were keyed by the screen's MODE - the key name embeds `exportmodule` - so the
 *   title was resolved per mode at run time. There is one mode here, so the resolved string is the
 *   constant, and the shared page header takes it already resolved rather than mapping a key.
 */
const PAGE_TITLE = 'Export Module';

/**
 * The supporting sentence beneath the heading.
 *
 * Taken from the paragraph inside `ModuleHelp.Text`, whose full value is
 * `<h1>Export Module</h1><p>Administrators can export content for the specified module.</p>`. Only the
 * sentence is carried across: the heading element in that value duplicates the title above, and the
 * shared page header already emits the page's single `<h1>`, so reproducing it would put two competing
 * headings in the document outline.
 */
const PAGE_SUBTITLE = 'Administrators can export content for the specified module.';

/** The field label, from `Export.ascx.resx` key `plFile.Text`. */
const FILE_LABEL = 'File';

/**
 * The field help text, from `Export.ascx.resx` key `plFile.Help`.
 *
 * Handed to the shared form field, which owns the help affordance entirely. The legacy affordance had
 * three defects the shared component already resolves - it was keyboard-unreachable, it lived inside the
 * label so activating it also focused the input, and its icon carried no alternate text - and none of
 * that is re-implemented here.
 */
const FILE_HELP = 'Enter the export filename';

/**
 * The DOM id tying the label to the input, so the shared form field can associate the two.
 *
 * A constant rather than a generated value: there is exactly one of this screen mounted at a time, and a
 * generated id would need a random or counter source, neither of which this component is permitted.
 */
const FILE_CONTROL_ID = 'module-export-file-name';

/**
 * The filename control's name within the form group.
 *
 * Held once because it is used twice - to look the control up, and to ask which of a problem document's
 * per-field messages belong to it. The lookup on the second of those is case-insensitive and tolerant of
 * the prefixes a model-state key can carry, and it lives in the shared utility rather than here, because
 * the keys are the server's spelling rather than this form's.
 */
const FILE_NAME_CONTROL = 'fileName';

/**
 * The label on the confirming action, from `Export.ascx.resx` key `cmdExport.Text`.
 *
 * MIGRATION: D10 - THE MARKUP AND THE RESOURCE FILE DISAGREE, AND THE RESOURCE FILE WINS.
 *   `Website/admin/Modules/export.ascx:L15` declares the export link with the inline attribute
 *   `text="Import"`, a copy-paste defect carried over from the near-identical import screen. The control
 *   also carries `resourcekey="cmdExport"`, and at run time the legacy page framework overwrote the
 *   inline text with the resource value, so an operator always read "Export" and never saw the defect.
 *   The corrected value is therefore not a change in behaviour - it is the behaviour, stated once instead
 *   of twice. This is the clearest available proof that a markup attribute is not a source of wording.
 */
const EXPORT_ACTION_LABEL = 'Export';

/**
 * The label on the dismissing action.
 *
 * Absent from this screen's own resource file: it resolves from the shared global file, key
 * `cmdCancel.Text`, which the legacy framework consulted when a local entry was missing. The wording rule
 * is unchanged by the fallback - the value still comes from a resource entry rather than from the
 * `text="Cancel"` attribute on `export.ascx:L16`.
 */
const CANCEL_ACTION_LABEL = 'Cancel';

/**
 * The message shown when the filename is missing, from `Export.ascx.resx` key `Validation.Text`.
 *
 * MIGRATION: PRESERVED VERBATIM EVEN THOUGH THE RULE BEHIND IT IS NARROWER. The legacy sentence names two
 *   things, because the legacy gate at `Export.ascx.vb:L121` tested two -
 *   `cboFolders.SelectedIndex <> 0 And txtFile.Text <> ""`. The folder half of that gate is gone with the
 *   folder picker, so only the filename half is enforced here. The sentence is nonetheless carried across
 *   unaltered rather than quietly rewritten, because the operator-facing wording is part of the contract
 *   this migration is required to preserve and re-authoring it would be an undocumented change to what an
 *   operator reads. The narrowing is the documented divergence; the words are not.
 *
 * MIGRATION: the rule is also now DECLARATIVE. The legacy screen carried no validator markup at all - no
 *   required-field validator, no validation summary, not even a form element in its seventeen lines - and
 *   enforced the condition imperatively inside the click handler. Here it is a validator on the control,
 *   so the condition is stated once, next to the field it governs.
 *
 * MIGRATION: the legacy test was UNTRIMMED. `txtFile.Text <> ""` admitted a value of a single space, which
 *   then reached the name sanitiser and was reduced to nothing, because a space is one of the characters
 *   that sanitiser strips. That is a defect and it is not reproduced: the validator below rejects a blank
 *   value, which is what the legacy sentence already claimed to require.
 */
const VALIDATION_MESSAGE = 'You must specify a folder and file for export';

/**
 * The greatest number of characters the filename field accepts.
 *
 * From `maxlength="200"` on the input at `Website/admin/Modules/export.ascx:L11`, and independently the
 * bound the API's own request contract states for this member.
 */
const FILE_NAME_MAX_LENGTH = 200;

/**
 * The message shown when the filename is longer than the field accepts.
 *
 * MIGRATION: NET-NEW WORDING, because the legacy condition was unreachable rather than unhandled. The
 *   legacy input carried a length attribute, so a browser refused the two-hundred-and-first character and
 *   no message was ever needed. The same attribute is present on this screen's input, so this message is
 *   likewise a second line of defence - reachable only when a value arrives by some route other than
 *   typing - and it exists because a rule enforced with no explanation is worse than one explained.
 */
const FILE_NAME_TOO_LONG_MESSAGE = `The export filename may be at most ${FILE_NAME_MAX_LENGTH} characters.`;

/**
 * The message shown when the module has no content to export, from `Export.ascx.resx` key
 * `NoContent.Text`.
 *
 * MIGRATION: THE CONDITION MOVED SIDES, AND THE WORDING IS RETAINED FOR IT ANYWAY. The legacy branch at
 *   `Export.ascx.vb:L159` tested the module's raw payload with `Content <> ""` BEFORE wrapping it, and
 *   reported this sentence when it was empty. The API instead wraps an empty payload and answers
 *   successfully with an envelope that is not itself empty, so it publishes no failure code for this
 *   condition and the refusal is no longer reachable from a status code. The distinction is nevertheless
 *   preserved on the wire and in the store, both of which deliberately keep an empty document distinct
 *   from an absent one, so the legacy outcome is honoured here: an empty document is reported with this
 *   sentence and is not offered as a download. A file containing nothing is not an export.
 */
const NO_CONTENT_MESSAGE = 'The module specified does not have any content';

/**
 * The message shown when the module cannot export, from `Export.ascx.resx` key
 * `ExportNotSupported.Text`.
 *
 * The legacy screen emitted this one sentence from TWO places - `Export.ascx.vb:L201` when the module
 * declared no business controller class or was not portable, and `:L195` when the resolved object turned
 * out not to implement the portability contract after all. The API collapses both into one code, which is
 * the same consolidation stated once.
 *
 * MIGRATION: THE "specified" AND "selected" ASYMMETRY IS REPRODUCED, NOT HARMONISED. The sibling import
 *   screen's equivalent entry reads "The module SELECTED does not support the IMPORTING of content"
 *   (`Import.ascx.resx`), while this one reads "specified" - and both screens' help text says
 *   "specified". The inconsistency is in the legacy wording itself. Both sentences this screen owns say
 *   "specified", and no attempt is made to reconcile them with the neighbouring screen, because
 *   equivalent wording means the wording that was there.
 *
 * MIGRATION: THE SHARED REFUSAL TABLE CANNOT SUPPLY THIS SENTENCE. `core/utils/form-errors.util.ts` maps
 *   the same server code to the sibling screen's wording, which is correct there and wrong here. That is
 *   precisely why this wording is owned locally rather than resolved through the shared table.
 */
const EXPORT_NOT_SUPPORTED_MESSAGE =
  'The module specified does not support the exporting of content';

/**
 * The failure code the API publishes when a module cannot export its content.
 *
 * The server's own spelling, taken from the export operation's published contract, and lower-case by that
 * contract's convention. It is deliberately NOT the legacy resource key: the legacy names never appeared
 * on the wire, so matching on one would match nothing. The two remaining codes this operation can
 * publish - a module that does not exist, and a document the format cannot carry - are left to the shared
 * problem-details surface, which already words both.
 */
const NOT_PORTABLE_CODE = 'module.not_portable';

/**
 * The message shown when this address does not name a module.
 *
 * MIGRATION: D-M6 CORRECTED - A MISSING MODULE IS A FAILURE, NOT A SUCCESS. The legacy helper at
 *   `Export.ascx.vb:L149` opened `If Not objModule Is Nothing Then` and never wrote an `Else`, so a
 *   module that could not be read left the status string empty - and `:L126` treated an empty status
 *   string as success and navigated away. An operator therefore saw a successful export of a module that
 *   did not exist. The condition is surfaced here instead of being swallowed.
 *
 * MIGRATION: the legacy also had no message for a MALFORMED identifier. `Export.ascx.vb:L66` parsed the
 *   request value with a conversion that throws on anything non-numeric, and the surrounding handler at
 *   `:L90` swallowed the exception into the generic page-load failure path. That is one of the coercions
 *   the legacy compiler settings permitted and strict typing forbids, so it is made explicit and given
 *   this sentence.
 */
const NO_MODULE_ADDRESSED_MESSAGE = 'This address does not name a module to export.';

/**
 * Reported when the loaded module does not describe the module this address names.
 *
 * The store is shared, so it can be holding another screen's module. Refusing is correct because the
 * composed filename is derived from the loaded module's name, and proceeding would label one module's
 * document with another module's name.
 */
const STALE_MODULE_MESSAGE =
  'The module on screen no longer matches this address, so nothing was exported. Please try again.';

/**
 * Reported when an exported document arrives after the address has moved to a different module.
 *
 * The document is discarded rather than delivered. An export is tenant data leaving the application for
 * the operator's device, and a file that claims to be one module while containing another cannot be
 * corrected after the fact.
 */
const STALE_EXPORT_MESSAGE =
  'The export finished after you moved to a different module, so the file was not downloaded. Please export again.';

/**
 * What the progress indicator announces while the module is being read.
 *
 * MIGRATION: NET-NEW, because the legacy screen had nothing to announce. It read the module during its own
 *   server-side render (`Export.ascx.vb:L83-L84`), so the field was already populated by the time an
 *   operator saw the page and there was no interval to describe. Reading it over the network creates that
 *   interval, and an indicator that announces nothing is an accessibility defect.
 */
const LOADING_MODULE_LABEL = 'Loading module…';

/**
 * What the progress indicator announces while an export is in flight.
 *
 * MIGRATION: NET-NEW for the same reason as {@link LOADING_MODULE_LABEL}. The legacy export happened inside
 *   a full page postback, during which the browser showed its own progress and the page was simply gone.
 */
const EXPORTING_LABEL = 'Exporting module content…';

/**
 * The message announced when an export completes.
 *
 * MIGRATION: SUCCESS FEEDBACK IS A NET ADDITION. `Export.ascx.vb:L126-L127` navigated away on success and
 *   raised no message of any kind, so an operator's only evidence that anything happened was a file
 *   appearing in a folder on the server. There is no such folder now, so silence would leave the outcome
 *   entirely unreported. The sentence names the file, which is safe to interpolate as text: every
 *   character that could open a tag or close an attribute - the angle brackets, the ampersand, the double
 *   quote and the apostrophe - is removed by the name sanitiser before it can reach here.
 *
 * @param fileName The composed document name, already sanitised.
 * @returns One plain-text sentence.
 */
function exportCompleteMessage(fileName: string): string {
  return `Export complete. ${fileName} has been downloaded.`;
}

/**
 * The message announced when the document was produced but could not be handed to the browser.
 *
 * MIGRATION: NET-NEW, because the failure it describes did not exist. The legacy wrote the document to a
 *   server path and catalogued it; nothing was ever handed to a browser, so there was no save step to
 *   fail. See the note on {@link ModuleExportComponent.save} for why the download itself is net-new.
 */
const DOWNLOAD_FAILED_MESSAGE = 'The export document could not be saved to your device.';

/**
 * The media type the exported document is offered under.
 *
 * The payload is an XML document - the API composes an `<?xml ... ?>` declaration and a `content` root
 * around the module's own markup - so this states what the bytes are.
 *
 * MIGRATION: DELIBERATELY NOT THE LEGACY VALUE, AND THE LEGACY VALUE WAS NOT A MEDIA TYPE FOR A RESPONSE.
 *   `Export.ascx.vb:L184` and `:L186` pass `application/octet-stream` to the calls that insert or update
 *   the catalogue row for the written file: it is a COLUMN VALUE recorded in a table, not a header on
 *   anything sent to a browser, because the legacy screen sent nothing to a browser at all. Reusing it
 *   here on the strength of that appearance would be reasoning from a misread source, and it would also
 *   describe the payload less accurately than the type below.
 */
const EXPORT_MEDIA_TYPE = 'application/xml';

/**
 * The characters the legacy name sanitiser removes, in the order it removed them.
 *
 * Thirty-three characters, transcribed from `Export.ascx.vb:L212`, where the set is written as a
 * thirty-one character literal followed by two characters supplied by code point:
 *
 * ```vb
 * Dim strBadChars As String = ". ~`!@#$%^&*()-_+={[}]|\:;<,>?/" & Chr(34) & Chr(39)
 * ```
 *
 * Enumerated so a reviewer can verify the set without decoding anything:
 *
 *   1 `.`   2 SPACE  3 `~`   4 `` ` ``  5 `!`   6 `@`   7 `#`   8 `$`   9 `%`  10 `^`  11 `&`
 *  12 `*`  13 `(`   14 `)`  15 `-`      16 `_`  17 `+`  18 `=`  19 `{`  20 `[`  21 `}`  22 `]`
 *  23 `|`  24 `\`   25 `:`  26 `;`      27 `<`  28 `,`  29 `>`  30 `?`  31 `/`
 *  32 `"`  (code point 34)   33 `'`  (code point 39)
 *
 * THE SET INCLUDES THE SPACE, THE FULL STOP, THE HYPHEN AND THE UNDERSCORE. Those four are the ones a
 * reader is most likely to assume are safe and preserve, and preserving any of them would change the
 * composed name. It is stated as a character list rather than a pattern so that no escaping question
 * arises: four of these characters are significant inside a regular-expression character class, and a set
 * this load-bearing should not depend on getting that right.
 *
 * The set is NOT extended. Letters, digits, accented and other non-ASCII characters and control
 * characters all survive, exactly as they did.
 */
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

/** The leading segment of every composed document name, from `Export.ascx.vb:L124`. */
const NAME_PREFIX = 'content';

/** The trailing segment of every composed document name, from `Export.ascx.vb:L124`. */
const NAME_SUFFIX = 'xml';

/** The separator between the composed name's segments, from `Export.ascx.vb:L124`. */
const NAME_SEPARATOR = '.';

/**
 * Reads a route parameter as a module identifier.
 *
 * The router supplies a path parameter as a STRING, so a component that declared its input as a number
 * without converting would hold a string while its type said otherwise - the one class of untruth strict
 * typing cannot catch, because the value arrives from the framework rather than from typed code. A number
 * is accepted as well so the component can be constructed directly, in a specification or by a parent.
 *
 * NOT-A-NUMBER IS THE ONLY MARKER FOR AN UNUSABLE PARAMETER, AND THAT CHOICE IS FORCED. Module
 * identifiers seed at zero, so 0 names the first module ever created; and minus one is the legacy
 * integer absence marker, which travels on this API's contracts as an ordinary transmitted value. Neither
 * can be borrowed to mean "no module". Not-a-number can, because it is not an identifier at all and
 * cannot compare equal to one.
 *
 * MIGRATION: D-M5 - THE PARAMETER'S SPELLING IS NORMALISED TO ONE NAME. `Export.ascx.vb:L65-L66` read the
 *   request value `"moduleid"` in lower case while the sibling settings screen read `"ModuleId"` in
 *   mixed case; both worked only because the legacy request collection compared keys case-insensitively.
 *   There is one spelling here, it is the route parameter's, and the framework binds it by matching that
 *   name to this component's input name - so the two cannot drift apart without the binding simply not
 *   happening.
 *
 * MIGRATION: the conversion is GUARDED, where the legacy conversion was not. `Export.ascx.vb:L66` used a
 *   parse that raises on any non-numeric text, and the enclosing handler at `:L90` absorbed the result
 *   into a generic page-load failure - so a mistyped address produced a page-level error rather than an
 *   explanation. Anything that is not a plain optionally-signed run of digits is rejected here, and so is
 *   a value too large to be held exactly, which is the overflow the legacy parse would have raised on.
 *
 * @param value The route parameter, or a number supplied directly.
 * @returns The identifier, or not-a-number when the parameter names none.
 */
function toModuleId(value: string | number): number {
  if (typeof value === 'number') {
    return Number.isSafeInteger(value) ? value : Number.NaN;
  }

  const trimmed = value.trim();

  // An optionally-signed run of decimal digits and nothing else. This rejects the empty string, white
  // space, a decimal point, exponent notation and any trailing text - all of which a lenient conversion
  // would otherwise turn into a plausible-looking identifier.
  if (!/^[+-]?\d+$/.test(trimmed)) {
    return Number.NaN;
  }

  const parsed = Number.parseInt(trimmed, 10);

  return Number.isSafeInteger(parsed) ? parsed : Number.NaN;
}

/**
 * Refuses a filename that is blank once surrounding white space is discounted.
 *
 * Reports under the `required` key rather than a key of its own, so that the field's one message covers
 * both an untouched field and a field holding nothing but spaces. A reader checking for a missing value
 * checks one condition, not two.
 *
 * MIGRATION: THE LEGACY TEST WAS UNTRIMMED, AND THAT IS NOT REPRODUCED. `Export.ascx.vb:L121` tested
 *   `txtFile.Text <> ""`, which a single space satisfies - and the name sanitiser then removed that space,
 *   leaving an empty segment in the middle of the composed name. The API refuses a blank name for the same
 *   reason, so enforcing it here as well states ONE rule at both ends rather than two rules that happen to
 *   agree; sending a value the server is certain to refuse would spend a round trip to learn what is
 *   already known. This is a fix required for the operation to succeed, and it is documented rather than
 *   made quietly.
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
 * Replaces the legacy administration screen `Website/admin/Modules/Export.ascx.vb` (227 lines) and its
 * markup `export.ascx` (17 lines). Two of the legacy screen's three responsibilities survive - naming the
 * document and asking the module for its content - and the third, choosing a server folder to write it
 * into, has no counterpart and is gone. What replaces it is described below, because the replacement is
 * not a translation of anything.
 *
 * WHAT THE LEGACY SCREEN DID, AND WHERE EACH PART WENT
 * ---------------------------------------------------
 * The legacy screen offered a folder picker and a filename box. On the confirming click it composed a
 * name, asked the module's own portability behaviour for its content, wrapped that content in a `content`
 * element, WROTE THE RESULT TO A FILE beneath the portal's home directory and then catalogued the file in
 * a database table. Composition and wrapping are the API's now. Writing and cataloguing are nobody's: no
 * folder-listing, file-listing, upload or disk-space endpoint exists in this API, by design, because the
 * container topology has no portal home directory under a web root to write into.
 *
 * MIGRATION: THE FOLDER PICKER IS DROPPED ENTIRELY AND THE DOCUMENT IS DOWNLOADED INSTEAD. There is no
 *   folder control, no folder signal and no folder browser on this screen. Three pieces of legacy wording
 *   go with the picker - the folder label, its help text, and the localised name of the portal root - as
 *   does the `<None Specified>` placeholder the picker opened on. The disk-space refusal goes too: it
 *   described a limit on a filesystem this screen no longer touches, and its wording is not reproduced.
 *   The bare catch-all handler at `Export.ascx.vb:L197-L198`, which reported one generic sentence for
 *   every unanticipated fault, is likewise not reproduced - the API answers with a structured problem
 *   document and the shared error banner presents it, which says more than the generic sentence could.
 *   Between them these two omissions are why a ladder of six legacy resource keys collapses to the two
 *   this screen owns.
 *
 * MIGRATION: THE DOWNLOAD IS ENTIRELY NEW AND IS NOT A PORTED STREAM. It is worth being exact about this,
 *   because the legacy source contains something that looks like a stream and is not. The value
 *   `application/octet-stream` appears at `Export.ascx.vb:L184` and `:L186`, but both are arguments to the
 *   calls that insert or update the catalogue row for the written file - a column value in a table. The
 *   legacy screen set no response header, wrote no bytes to the response and offered the operator nothing
 *   to save; it redirected away and left the file on the server. Handing the document to the browser is
 *   therefore a new capability this screen adds, and it is the reason the object-URL handling below exists
 *   at all.
 *
 * WHY THIS SCREEN HOLDS NO DATA OF ITS OWN
 * ---------------------------------------
 * Reading the module, performing the export and recording a failure all belong to the shared module store,
 * which exposes each as a signal. This component reads those signals, composes a name, and turns the
 * document the store holds into a file. It builds no URL, sets no header, and speaks to no endpoint - the
 * store's service does that - and it holds no copy of the exported document.
 *
 * MIGRATION: THE PAGE'S OWN STATE MECHANISM IS GONE. The legacy screen distinguished a first render from a
 *   postback at `Export.ascx.vb:L69` and rebuilt its controls from the round-tripped page state on every
 *   click. Nothing round-trips here: the form holds the operator's text, the store holds everything else,
 *   and there is no hidden field carrying serialised control state.
 *
 * MIGRATION: `permissionGuard` WITH THE MODULE EDIT POLICY IS ADDED, AND IT GUARDS A SCREEN THAT WAS
 *   PREVIOUSLY UNGUARDED. `Export.ascx.vb` contains no authorisation check of any kind - no role test, no
 *   security helper, no redirect to the access-denied screen - so any caller who could reach the address
 *   could export a module's content. The sibling settings screen shows what the check should have been,
 *   with the comment "Verify that the current user has access to edit this module" above the gate at
 *   `ModuleSettings.ascx.vb:L191-L193`. The hardening is declared on the route rather than in this
 *   component, which declares no route and attaches no guard; and it is an affordance rather than an
 *   enforcement point, because the server is authoritative and answers a denied request with a refusal
 *   this screen then presents.
 *
 * MIGRATION: THE ACTIONS ARE REAL BUTTONS. Both legacy actions were link buttons, which rendered as
 *   anchors - elements that navigate rather than act, are not activated by the space key, and are
 *   announced as links. They are buttons here, which costs nothing visually and makes both actions
 *   properly operable from the keyboard. The dismissing action also carried an attribute suppressing
 *   validation, and that behaviour is preserved exactly: it neither validates nor submits.
 */
@Component({
  selector: 'app-module-export',
  standalone: true,
  // The typed form, and the four shared components this screen presents through. Nothing else: no folder
  // picker exists to import, and the shared library is consumed rather than extended.
  imports: [
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
   * THE NAME IS LOAD-BEARING AND MUST STAY EXACTLY `moduleId`. The router is configured with component
   * input binding, which delivers a route parameter to an input by MATCHING ITS NAME. Any other spelling -
   * a different case, an abbreviation, a rename - compiles cleanly, raises no warning, and simply never
   * receives the parameter, leaving the screen permanently unaddressed. It is the one identifier in this
   * file whose correctness no compiler can check.
   *
   * Declared with a transform because the parameter arrives as a string; see {@link toModuleId}, which
   * also explains why not-a-number is the only usable marker for an unusable parameter.
   *
   * Public because the strict input access check requires it - a non-public input fails to compile at
   * every consumer.
   */
  readonly moduleId = input.required<number, string | number>({ transform: toModuleId });

  /**
   * The one field this screen asks for.
   *
   * The control is non-nullable, so its value is a `string` rather than a `string | null`, the group's
   * value is fully typed rather than partial, and a reset returns it to the empty string it started at
   * instead of to null. The maximum length mirrors the attribute the legacy input carried
   * (`export.ascx:L11`), which is also the bound the API's request contract states.
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

  /** Whether the module is being read. */
  protected readonly moduleLoading = this.store.moduleLoading;

  /** Whether an export is in flight. */
  protected readonly exporting = this.store.exporting;

  /**
   * A screen-level sentence this component raises itself, or null when it has nothing to say.
   *
   * Carries only the outcomes the shared error banner cannot, because neither arrives as a problem
   * document: a document that came back empty, and an address that names no module. Everything the server
   * refuses structurally goes to the banner instead.
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
   * wording for the two screens genuinely differs and that difference is preserved, so the sentence is
   * owned locally. See {@link EXPORT_NOT_SUPPORTED_MESSAGE}.
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
   * empty on a successful response, and an address that resolved to no module. Everything the server
   * refuses structurally goes to the banner below instead, which is a richer surface.
   */
  protected readonly notice = this._notice.asReadonly();

  /**
   * The problem document to present in the shared error banner, or null when there is nothing to present.
   *
   * Narrowed to the two operations this screen performs, because the store holds one failure slot shared
   * across every module command: without the filter, a failure raised by a listing on another screen would
   * surface here.
   *
   * THE OWNED WORDING IS SUBSTITUTED INTO THE DOCUMENT RATHER THAN RENDERED BESIDE IT. For the one code
   * this screen words itself, the server's own `detail` sentence is replaced by the legacy sentence and
   * everything else about the document is passed through untouched. Routing it through the banner rather
   * than into a plain paragraph of its own is what keeps three things that would otherwise be lost:
   *
   *   * the severity classification and its visual treatment, so a refusal is presented as a refusal
   *     rather than as an indistinguishable line of body text;
   *   * the problem's title, which names the class of failure;
   *   * the support reference, which is the ONLY value joining what an operator saw to what the server
   *     logged - and therefore the one thing they can usefully quote in a report.
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

  /** The page heading. */
  protected readonly title = PAGE_TITLE;

  /** The supporting sentence beneath the heading. */
  protected readonly subtitle = PAGE_SUBTITLE;

  /** The filename field's label. */
  protected readonly fileLabel = FILE_LABEL;

  /** The filename field's help text. */
  protected readonly fileHelp = FILE_HELP;

  /** The DOM id tying the filename label to its input. */
  protected readonly fileControlId = FILE_CONTROL_ID;

  /** The greatest number of characters the filename input accepts. */
  protected readonly fileNameMaxLength = FILE_NAME_MAX_LENGTH;

  /** The confirming action's label. */
  protected readonly exportLabel = EXPORT_ACTION_LABEL;

  /** The dismissing action's label. */
  protected readonly cancelLabel = CANCEL_ACTION_LABEL;

  /** What the progress indicator announces while the module is being read. */
  protected readonly loadingModuleLabel = LOADING_MODULE_LABEL;

  /** What the progress indicator announces while an export is in flight. */
  protected readonly exportingLabel = EXPORTING_LABEL;

  /**
   * The composed name of the export this screen is waiting for, or null when it is waiting for none.
   *
   * A plain field rather than a signal, deliberately. It is written when a request is sent and read when
   * the document arrives, and nothing renders it - so making it reactive would add a dependency the
   * download effect must then be careful not to re-run on.
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
   * underneath it. The name alone cannot detect that: it would still be a perfectly well-formed name,
   * just for the wrong module.
   */
  private pendingExportModuleId: number | null = null;

  /**
   * The object URL currently backing a downloaded document, or null when none is outstanding.
   *
   * OWNED RATHER THAN LEAKED, AND DELIBERATELY NOT REVOKED THE INSTANT THE CLICK RETURNS. An object URL
   * revoked in the same task as the click can be withdrawn before the browser has finished resolving it,
   * which turns a working download into a silent failure on some engines.
   *
   * ⚠ IT IS RELEASED ON THE NEXT TASK TURN INSTEAD, WHICH IS AS PROMPT AS IS SAFE. The earlier arrangement
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
    // Teardown. The revocation here is what makes the deliberate deferral described above safe: whatever
    // is outstanding when the screen goes away is released, whether an export succeeded, failed or was
    // never attempted. The exported document is discarded from the shared store at the same time, so a
    // later visit cannot open onto a document produced for a different module.
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

    // THE MODULE READ, IN EITHER DIRECTION. Both outcomes are handled in one place because both are the
    // same event - the read settling - and splitting them invites the case where neither branch fires.
    // While the read is still in flight nothing is concluded from an absent module: null means "not yet".
    effect(() => {
      const loading: boolean = this.store.moduleLoading();
      const detail = this.store.module();

      untracked(() => {
        if (loading) {
          return;
        }

        if (detail !== null) {
          // ⚠ THE STORE IS SHARED AND ROOT-PROVIDED, so what it publishes is whatever was read LAST -
          // by this screen or by any other module screen. A detail describing a different module must
          // not seed this form: the composed filename is built from `moduleName`, so module A's name
          // would end up labelling module B's exported document, and nothing downstream could detect
          // the substitution. Treated as "not yet read" rather than as an absent module, because the
          // read for THIS module may still be in flight.
          if (detail.moduleId !== this.moduleId()) {
            return;
          }

          this.seedFileName(detail.moduleTitle);

          return;
        }

        this.reportAbsentModule();
      });
    });

    // THE EXPORTED DOCUMENT. The store sets this to null when a request goes out and to the document when
    // it returns, so a null carries no information and is ignored.
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
    // no other trace: no file arrives, and an operator who has looked away from the form would otherwise
    // see nothing at all.
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
   * Replaces `Export.ascx.vb:L119-L137`, the legacy confirming click handler.
   *
   * MIGRATION: THE POSITIONAL CONTRACT IS GONE. The legacy handler called a private helper declared as
   *   `ExportModule(ModuleID As Integer, FileName As String, Folder As String) As String` at
   *   `Export.ascx.vb:L143`, whose return value was a status string with the empty string overloaded to
   *   mean success (`:L145`, `:L126`). Three positional arguments become one identifier and one request
   *   object, and the overloaded status string becomes an HTTP status with a published failure code -
   *   which is what allows a missing module to be told apart from a successful export at all.
   *
   * MIGRATION: PORTABILITY IS NOT TESTED HERE, AND THE ATTEMPT IS ALWAYS ALLOWED. The legacy handler's
   *   helper gated on the module declaring a business controller class and being portable
   *   (`Export.ascx.vb:L150`), where portability was a read-only property derived from an integer bit field
   *   (`Library/Components/Modules/ModuleInfo.vb:L608-L612`) that was excluded from serialisation and so
   *   never travelled. The contract this screen reads carries no resolved portability flag - it exists only
   *   on the separate definition contract, which this screen does not fetch, because fetching it would add
   *   a second round trip the legacy screen never made in order to pre-empt an answer the server already
   *   gives. So the confirming action is never disabled on portability grounds, the request is always sent,
   *   and a module that cannot export is reported through the server's own refusal. The bit field is not
   *   interpreted here under any circumstances.
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

    // MIGRATION: D-M6 CORRECTED, AND CHECKED BEFORE THE FIELD RULE RATHER THAN AFTER IT. The legacy helper's
    //   outer test at `Export.ascx.vb:L149` had no `Else`, so a module that could not be read left the
    //   status string empty - and an empty status string was the success signal, so the screen navigated
    //   away as though the export had worked.
    //
    //   THE ORDER HERE IS PART OF THE CORRECTION, NOT AN INCIDENTAL DETAIL. The legacy gate tested the
    //   fields first (`:L121`) and only then reached the helper, and reproducing that order re-introduces a
    //   smaller version of the same dishonesty: the filename is never prepopulated for a module that was not
    //   read, so the fields fail first and the operator is told to supply a filename - an instruction that
    //   cannot possibly help, for a module that does not exist. A condition the operator cannot repair
    //   outranks one they can.
    if (detail === null) {
      this._notice.set(NO_MODULE_ADDRESSED_MESSAGE);

      return;
    }

    // ⚠ THE DETAIL MUST DESCRIBE THE MODULE THIS SCREEN ADDRESSES. It comes from a shared store, so a
    // detail left there by another module screen - or by a read for the module this route used to name -
    // would otherwise supply `moduleName` for the composed filename while the request below carries a
    // different identifier. The result is module B's data delivered under module A's filename.
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
    // unambiguously to this request: the module could in principle be re-read while the export is in
    // flight, and a name derived after the fact could then describe a different module.
    this.pendingFileName = this.composeFileName(detail.moduleName, typedName);

    // Captured alongside the name, for the same reason and checked at delivery: the route can move to
    // another module while an export is in flight, and a document must never be handed over unless it
    // is still the module the operator is looking at.
    this.pendingExportModuleId = id;

    // The operator's text travels exactly as typed. The API neither derives a name from it nor stores
    // anything under it - it labels the response and is validated for presence - so sanitising it before
    // transmission would discard the operator's intent without gaining anything. The composed name above
    // is what the sanitiser applies to, because that is the value that becomes a filename.
    //
    // The folder member is present because the contract declares it, and it is null because this screen
    // has no folder to name: the contract accepts it purely so a caller migrating from the legacy screen
    // is not forced to discard a value, and the API resolves it against nothing. No folder control, signal
    // or field exists on this screen to populate it from.
    const request: ModuleExportRequest = { fileName: typedName, folder: null };

    this.store.exportModule(id, request);
  }

  /**
   * Leaves the screen without exporting anything.
   *
   * MIGRATION: NEITHER VALIDATES NOR SUBMITS, PRESERVING THE LEGACY BEHAVIOUR EXACTLY. The legacy
   *   dismissing action carried `causesvalidation="False"` (`export.ascx:L16`) and its handler did nothing
   *   but redirect (`Export.ascx.vb:L103-L109`). Nothing here touches the form, so a half-typed or invalid
   *   filename is abandoned silently rather than being marked up on the way out.
   *
   * MIGRATION: THE DESTINATION IS THE NEAREST EQUIVALENT RATHER THAN THE SAME ONE. The legacy handler
   *   redirected to the current page's own address, which returned the operator to the portal page that
   *   was hosting the module - a page assembled by the server from skins and containers, and a concept with
   *   no counterpart here. The module's administration screen is the closest destination that exists, so
   *   that is where this goes; when the address named no module there is no module-scoped destination at
   *   all and it returns to the application root. The destination is expressed as a path, not as an import
   *   of a neighbouring screen.
   */
  protected cancel(): void {
    const id: number = this.moduleId();

    void this.router.navigate(Number.isInteger(id) ? ['/modules', id, 'settings'] : ['/']);
  }

  /**
   * Returns every piece of per-address state to its starting point.
   *
   * Called whenever the route parameter changes, including on the first render. The store is shared and
   * application-wide, so until this screen replaces them the module, the last exported document and the
   * last failure all still belong to whichever screen ran before it.
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
   * Reproduces `Export.ascx.vb:L83-L87`, including its tolerance of a module that could not be read: the
   * legacy screen guarded the assignment with `If Not objModule Is Nothing` and left the box empty rather
   * than failing, and an absent title is treated the same way here.
   *
   * MIGRATION: THE SUGGESTION AND THE FILENAME COME FROM DIFFERENT FIELDS, AND THE ASYMMETRY IS
   *   REPRODUCED. This seeds from the module's TITLE - the administrator-supplied heading, `:L86` - while
   *   the composed document name is built from the module's programmatic NAME, `:L124`. They are distinct
   *   fields with distinct purposes and a third, the definition's display name, sits alongside them; the
   *   legacy class declared all three separately and this screen keeps them separate. The consequence is
   *   visible and intended: what an operator sees suggested in the field is not the first segment of the
   *   file they receive. Collapsing the two would be tidier and would change the produced filename, so it
   *   is left exactly as it was.
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
   * MIGRATION: D-M6 CORRECTED, AND CORRECTED AT THE EARLIEST POINT RATHER THAN AT THE LATEST. The legacy
   *   screen learned nothing from a module it could not read: the helper's outer test at
   *   `Export.ascx.vb:L149` had no `Else`, the status string stayed empty, and `:L126` read an empty status
   *   string as success and navigated away. Reporting the condition only when the operator finally attempts
   *   an export would be a smaller version of the same problem, because the filename is never prepopulated
   *   for a module that was not read - so the attempt fails the field rule first and the operator is told
   *   to supply a filename for a module that does not exist. Saying it as soon as the read settles is the
   *   only ordering that describes the actual situation. It is the same treatment a malformed address gets,
   *   for the same reason: nothing on this screen can repair either one.
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
   * reformatted or searched: it is module-authored markup, which makes it the least trustworthy string
   * this screen handles, and it travels from the store to a binary container without being read.
   */
  private deliver(content: string): void {
    const fileName: string | null = this.pendingFileName;
    const exportedModuleId: number | null = this.pendingExportModuleId;

    // Not this screen's document. The store is shared and may already hold an export performed elsewhere,
    // and pushing a file at an operator who merely navigated here would be a surprise at best.
    if (fileName === null) {
      return;
    }

    // ⚠ THE EXFILTRATION GUARD. This is the one sink on this screen that puts tenant data onto the
    // operator's device, and it cannot be taken back once it fires. The route can move from one module
    // to another WITHOUT this component being recreated - both visits resolve to the same route
    // configuration - so an export requested for module A can settle after the screen has moved to
    // module B. Delivering it then writes A's data to a file the operator will read as B's.
    //
    // The captured identifier is compared against the route rather than against the store, because the
    // route is what the operator is actually looking at.
    if (exportedModuleId !== this.moduleId()) {
      this.pendingFileName = null;
      this.pendingExportModuleId = null;
      this._notice.set(STALE_EXPORT_MESSAGE);

      return;
    }

    this.pendingFileName = null;
    this.pendingExportModuleId = null;

    // MIGRATION: AN EMPTY DOCUMENT IS THE LEGACY "no content" OUTCOME, PRESERVED ON THIS SIDE. The legacy
    //   branch at `Export.ascx.vb:L159` tested the module's payload before wrapping it and reported the
    //   no-content sentence when it was empty. The API wraps an empty payload and answers successfully, so
    //   it publishes no code for this condition - but both the client service and the store deliberately
    //   keep an empty document distinct from an absent one, and that distinction is what this branch
    //   consumes. A file containing nothing is reported rather than downloaded.
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
    // A refusal the server keyed to this field is already shown beside the field, so repeating it here
    // would state the same thing twice in two places. The narrowing guard and the key matching both belong
    // to the shared utility: the keys are the server's model-state spelling, are not camel-cased, and are
    // read from an index signature, so the matching is deliberately not attempted here.
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
    this.notifications.notify(failure.summary.severity, message);
  }

  /**
   * Composes the document's filename.
   *
   * Reproduces `Export.ascx.vb:L124` exactly:
   *
   * ```vb
   * Dim strFile As String = "content." & CleanName(objModule.ModuleName) & "." & CleanName(txtFile.Text) & ".xml"
   * ```
   *
   * Four segments joined by full stops: a fixed prefix, the module's programmatic name, the operator's
   * text, and the extension. Both middle segments pass through the sanitiser, which is what removes the
   * full stops that would otherwise turn either segment into several.
   *
   * MIGRATION: THE FIRST SEGMENT IS THE MODULE'S PROGRAMMATIC NAME, NOT ITS TITLE. See the note on
   *   {@link ModuleExportComponent.seedFileName} for the asymmetry this creates and why it is kept.
   *
   * MIGRATION: THE NAME REFERENCE IS GUARDED, WHERE THE LEGACY REFERENCE WAS NOT. `Export.ascx.vb:L124`
   *   read the module's name straight off an object it had not checked, so a module that could not be read
   *   raised a null reference here - swallowed by the handler at `:L134` and reported as the generic page
   *   failure. The contract this screen reads declares the name as possibly absent, and strict typing makes
   *   handling that mandatory rather than optional. An absent name contributes an empty segment, which is
   *   what the legacy produced for a name that sanitised away to nothing, so the shape of the result is
   *   unchanged.
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
   * Reproduces `Export.ascx.vb:L209-L221`, which walked its bad-character set one character at a time and
   * replaced each occurrence with nothing. The loop below is that loop, with the two runtime helpers the
   * legacy used for length and for a character code replaced by the language's own facilities - the runtime
   * library those helpers came from is not carried into this migration at all.
   *
   * MIGRATION: NO EMPTY-RESULT GUARD, NO LENGTH CAP AND NO TRIM - THE LEGACY DEFECT IS REPRODUCED, NOT
   *   REPAIRED. A value consisting only of characters in the set sanitises to nothing, and the legacy
   *   produced a name with an empty segment in it, such as a document called `content.<name>..xml`. That
   *   behaviour is deliberately preserved: the result is still a valid, downloadable filename, so nothing
   *   about the operation breaks, and inventing a substitute segment would change a name an operator may
   *   have scripted against. The one adjacent condition that WOULD have broken the operation - a filename
   *   that is blank before sanitising, which the API refuses outright - is handled by a validator on the
   *   field instead, and that fix is documented where it is made. A trim is unnecessary in any case,
   *   because the space is itself one of the characters removed.
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
   * MIGRATION: THIS ENTIRE METHOD IS NEW AND REPLACES A SERVER-SIDE FILE WRITE. The legacy screen created a
   *   text file beneath the portal's home directory (`Export.ascx.vb:L171-L174`) and then recorded it in a
   *   catalogue table (`:L177-L187`); it sent nothing to the browser and gave the operator nothing to save.
   *   There is no such directory in the target topology and no endpoint that would write to one, so the
   *   document is handed to the operator instead. Nothing here is a translation of legacy code.
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

      // Attached before activation because a detached element is not reliably actionable across engines,
      // and removed again in the cleanup below so no trace is left in the document either way.
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

  /** Cancels a pending release, if one is scheduled. Idempotent. */
  private cancelScheduledRelease(): void {
    if (this.revocationTimer === null) {
      return;
    }

    clearTimeout(this.revocationTimer);
    this.revocationTimer = null;
  }
}
