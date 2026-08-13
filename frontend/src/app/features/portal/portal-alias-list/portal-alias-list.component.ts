// ROUTE
// `/portals/:portalId/aliases`, reached by `loadComponent` from the portal feature's own route table.

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  Input,
  TemplateRef,
  ViewChild,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';

import type { OnInit } from '@angular/core';
import type { AbstractControl, ValidationErrors } from '@angular/forms';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import {
  fieldErrorMessage,
  isAliasInUseCode,
  isDuplicateAliasCode,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import {
  LoadingSpinnerComponent,
} from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import type {
  CreatePortalAliasRequest,
  PortalAlias,
  UpdatePortalAliasRequest,
} from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { PortalFailure } from '../../../core/state/portal.store';
import { isRouteId, parseRouteId } from '../../../core/utils/route-id.util';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// -----------------------------------------------------------------------------
//  MEASURED WORDING
// -----------------------------------------------------------------------------

/** The screen heading. */
const HEADING = 'Portal Aliases';

/** The wording of the link back to this portal's configuration screen. */
const SETTINGS_LINK_LABEL = 'Site Settings';

const ADD_ACTION_LABEL = 'Add New HTTP Alias';

/**
 * The grid column heading. The resource key is `HTTP Alias.Header` - note the SPACE inside the key, which
 * the legacy resource files use freely - and its value is the authority over the `HeaderText="HTTP
 * Alias"` attribute on `Website/admin/Portal/portalalias.ascx:L14`, because
 * `Localization.LocalizeDataGrid` replaced the attribute at run time.
 */
const ALIAS_COLUMN_HEADING = 'HTTP Alias';

/** The form control's label. */
const ALIAS_LABEL = 'HTTP Alias';

/**
 * The command column's heading and the per-row command's accessible name. `Edit.Text` in
 * `Website/App_GlobalResources/SharedResources.resx`, which is where the legacy image's
 * `resourcekey="Edit"` resolved.
 */
const EDIT_LABEL = 'Edit';

/**
 * How the row command's ACCESSIBLE name is qualified with the row it acts on. the visible text stays the
 * bare resource value and only the accessible name grows, which is the same treatment the portal
 * listing's own row commands received and for the same measured reason.
 *
 * @param alias The host name as stored, or the absent-host wording when it holds none.
 * @returns The accessible name.
 */
function editCommandName(alias: string): string {
  return `${EDIT_LABEL} ${alias}`;
}

/**
 * What a host-name cell paints when the stored value is absent or empty. the legacy cell painted NOTHING
 * for both states and the two were indistinguishable.
 */
const ABSENT_HOST_NAME_MARK = '\u2014';

/** The words behind {@link ABSENT_HOST_NAME_MARK}, announced but not painted. */
const ABSENT_HOST_NAME_DESCRIPTION = 'no host name recorded';

/** Shown in the command cell of the row this request arrived through. */
const CURRENT_ALIAS_ROW_NOTE = 'In use';

/** The submit command's label in EDIT mode. */
const UPDATE_SUBMIT_LABEL = 'Update';

const ADD_SUBMIT_LABEL = 'Add New Alias';

/** The cancel command's label. */
const CANCEL_LABEL = 'Cancel';

/** The delete command's label. */
const DELETE_LABEL = 'Delete';

/**
 * The deletion confirmation prompt. `DeleteItem.Text` in the GLOBAL `SharedResources.resx`, measured
 * ABSENT from both local files.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * Announced after a successful create or update. `Success.Text` in `EditPortalAlias.ascx.resx`, measured
 * ABSENT from the global file.
 */
const SAVED_MESSAGE = 'The Portal Alias has been saved.';

/**
 * Shown beside the control when the server refuses a host name already in use. `DuplicateAlias.Text` in
 * `EditPortalAlias.ascx.resx` - THIS screen's own wording, read by the legacy handler at
 * `EditPortalAlias.ascx.vb:L226` on the update path and again at L238 on the add path.
 */
const DUPLICATE_ALIAS_MESSAGE = 'The Portal Alias already exists.';

/**
 * Why the host name this request arrived through offers neither command, shown beside the control when
 * the server refuses a write to it and announced when an operator presses the row it belongs to. 17: THIS
 * SENTENCE HAS NO LEGACY ANTECEDENT, and it could not have one.
 */
const CURRENT_ALIAS_MESSAGE =
  'This is the host name your request reached this portal through, so it cannot be ' +
  'changed or removed. Reach the portal through one of its other host names and try again.';

const VIEW_DENIED_MESSAGE = 'You do not have access to view this Portal Alias.';

const DELETE_DENIED_MESSAGE = 'You do not have access to delete this Portal Alias.';

/**
 * Announced after a successful delete. AUTHORED WORDING WITH NO RESOURCE PROVENANCE, and recorded as
 * such.
 */
const DELETED_MESSAGE = 'The Portal Alias has been deleted.';

/** Shown in place of the grid when the portal has no host names. */
const EMPTY_MESSAGE = 'This portal has no HTTP aliases.';

/**
 * Shown when the address does not carry a usable portal identifier. AUTHORED WORDING WITH NO RESOURCE
 * PROVENANCE. The legacy screen had no equivalent state: it read the portal from ambient page state and
 * fell back to the current portal, so an unusable value was not expressible.
 */
const UNUSABLE_ROUTE_MESSAGE =
  'This address does not identify a portal, so no HTTP aliases can be shown.';

/**
 * The help text for the alias control. 7: RE-AUTHORED AS PROSE. The measured `plAlias.Help` value in
 * `EditPortalAlias.ascx.resx` carries FIVE upper-case `<BR>` tags and a pair of embedded double quotes
 * around the protocol prefix it warns against.
 */
const ALIAS_HELP =
  'Please enter the domain name used to navigate to this portal. This could be a local ' +
  'address (ie. localhost), an IP address (ie. 127.0.0.1), a full URL (ie. ' +
  'www.mydomain.com), or a server name (ie. MYSERVER). Please do not include the http:// ' +
  'protocol prefix in your specification.';

/** Mirrors the server's `NotEmpty` refusal. */
const ALIAS_REQUIRED_MESSAGE = 'An HTTP alias is required.';

/** Mirrors the server's maximum-length refusal, whose limit is 200. */
const ALIAS_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 200 characters.';

/** Shown when the RAW entry exceeds the legacy input cap of 255 characters. */
const ALIAS_ENTRY_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 255 characters.';

/** Mirrors the server's shape refusal. */
const ALIAS_INVALID_MESSAGE =
  'An HTTP alias must be a host name, an IP address or a server name, optionally followed by ' +
  'a port and a path, and must not include a protocol prefix.';

/**
 * Mirrors the server's topology refusal, character for character. ⚠ THE SERVER COMPOSES THIS SENTENCE
 * FROM `PortalAliasTopology`, so it cannot be edited on one side alone. `PortalAliasContractTests`
 * asserts that the server's composed value equals this literal, so adding a reserved word or widening the
 * depth on the server fails a backend test until this line follows.
 */
const ALIAS_UNSUPPORTED_PATH_MESSAGE =
  'An HTTP alias may carry at most 1 path segment beneath its host name; that segment may ' +
  'contain only letters, digits, hyphens and underscores, and may not be one of the addresses ' +
  'this application reserves for itself (api, health, login, modules, openapi, portals, ' +
  'role-groups, roles, settings, swagger, users).';

// -----------------------------------------------------------------------------
//  MEASURED LIMITS
// -----------------------------------------------------------------------------

/**
 * The rendered control's own character cap. `MaxLength="255"` on
 * `Website/admin/Portal/editportalalias.ascx:L7`, preserved exactly so the typing experience is the
 * legacy one.
 */
const ALIAS_ENTRY_MAX_LENGTH = 255;

const ALIAS_MAX_LENGTH = 200;

// TRANSPORT STATUSES THIS SCREEN INTERPRETS

/** A refusal on grounds of permission. */
const FORBIDDEN_STATUS = 403;

/**
 * @param failure The classified failure the store published.
 * @returns True when the failure is the duplicate-host-name refusal.
 */
function isDuplicateRefusal(failure: PortalFailure): boolean {
  return isDuplicateAliasCode(failure.conflictCode);
}

/**
 * Whether a failure is the server refusing a write to the host name the request arrived through. 17: the
 * enforced half of the restored legacy affordance.
 *
 * @param failure The classified failure the store published.
 * @returns True when the failure is the active-alias refusal.
 */
function isActiveAliasRefusal(failure: PortalFailure): boolean {
  return isAliasInUseCode(failure.conflictCode);
}

/**
 * The host name of one alias row, as text. RULE T7 AT THE BOUNDARY. The contract reports the host name as
 * nullable because the column is nullable, while the legacy absent-string sentinel was the EMPTY STRING
 * and not a null reference.
 *
 * @param alias One alias row.
 * @returns The host name, or the empty string when the row carries none.
 */
function aliasText(alias: PortalAlias): string {
  const held: string | null = alias.httpAlias;

  return held === null ? '' : held;
}

// SHAPE RULE

/**
 * The protocol separator, which the server refuses anywhere in an alias. Retained after the normalisation
 * helper that also used it was removed, because the SHAPE RULE has an independent need for it:
 * `ContainsOnlyPermittedCharacters` refuses any entry containing it.
 */
const SCHEME_SEPARATOR = '://';

/** Characters the server refuses outright, beyond whitespace and control codes. */
const FORBIDDEN_ALIAS_CHARACTERS: readonly string[] = Object.freeze([
  '\\',
  '@',
  '?',
  '#',
]);

/** Separates the authority from the optional child path. */
const PATH_SEPARATOR = '/';

/** Separates the host from the optional port. */
const PORT_SEPARATOR = ':';

/** Separates host labels. */
const LABEL_SEPARATOR = '.';

/** The hyphen, permitted inside a host label but never at its edges. */
const HYPHEN = '-';

const PATH_SEGMENT_EXTRAS: readonly string[] = Object.freeze([HYPHEN, '_']);

/** The greatest number of path segments an alias may carry beneath its authority. */
const MAXIMUM_PATH_SEGMENTS = 1;

/**
 * Path segments the deployment owns, which no alias may use. Mirrors
 * `PortalAliasTopology.ReservedPathSegments`: the console's own seven top-level routes and the four roots
 * the API answers.
 */
const RESERVED_PATH_SEGMENTS: readonly string[] = Object.freeze([
  'api',
  'health',
  'login',
  'modules',
  'openapi',
  'portals',
  'role-groups',
  'roles',
  'settings',
  'swagger',
  'users',
]);

/** The highest port number the server accepts. */
const MAX_PORT_NUMBER = 65535;

/** The greatest number of digits a port may carry. */
const MAX_PORT_DIGITS = 5;

/** Matches one ASCII letter or digit, and nothing else - no Unicode letters. */
const ASCII_ALPHANUMERIC = /^[0-9A-Za-z]$/u;

/** Matches one ASCII digit. */
const ASCII_DIGIT = /^[0-9]$/u;

/** Matches any whitespace or C0/C1 control character. */
const WHITESPACE_OR_CONTROL = /[\s\u0000-\u001F\u007F-\u009F]/u;

/**
 * Whether a single character is an ASCII letter or digit.
 *
 * @param character Exactly one character.
 * @returns True when it is `0`-`9`, `A`-`Z` or `a`-`z`.
 */
function isAsciiAlphanumeric(character: string): boolean {
  return ASCII_ALPHANUMERIC.test(character);
}

/**
 * Whether the value carries only characters the server permits. Ports `ContainsOnlyPermittedCharacters`
 * (`PortalAliasRules.cs:L170-L197`), including its final check that no protocol separator is present.
 *
 * @param alias The entry, exactly as the operator typed it.
 * @returns True when every character is permitted.
 */
function containsOnlyPermittedCharacters(alias: string): boolean {
  if (WHITESPACE_OR_CONTROL.test(alias)) {
    return false;
  }

  for (const character of alias) {
    if (FORBIDDEN_ALIAS_CHARACTERS.includes(character)) {
      return false;
    }
  }

  return alias.includes(SCHEME_SEPARATOR) === false;
}

/**
 * Whether the host portion is well formed.
 *
 * @param host The host portion, with any port already removed.
 * @returns True when the host is acceptable.
 */
function isAcceptableHost(host: string): boolean {
  if (host.length === 0) {
    return false;
  }

  let labelLength = 0;

  for (let index = 0; index < host.length; index += 1) {
    const character = host.charAt(index);

    if (character === LABEL_SEPARATOR) {
      // An empty label, or a label ending in a hyphen. The emptiness test comes
      // first, which is also what keeps the look-behind in range at index nought.
      if (labelLength === 0 || host.charAt(index - 1) === HYPHEN) {
        return false;
      }

      labelLength = 0;
      continue;
    }

    if (isAsciiAlphanumeric(character) === false && character !== HYPHEN) {
      return false;
    }

    if (labelLength === 0 && character === HYPHEN) {
      return false;
    }

    labelLength += 1;
  }

  return labelLength > 0 && host.charAt(host.length - 1) !== HYPHEN;
}

/**
 * Whether the port portion is well formed. Ports the port half of `IsAcceptableAuthority`
 * (`PortalAliasRules.cs:L215-L237`): one to five ASCII digits denoting a number from one to sixty-five
 * thousand five hundred and thirty-five.
 *
 * @param port The text after the port separator.
 * @returns True when the port is acceptable.
 */
function isAcceptablePort(port: string): boolean {
  if (port.length === 0 || port.length > MAX_PORT_DIGITS) {
    return false;
  }

  for (const digit of port) {
    if (ASCII_DIGIT.test(digit) === false) {
      return false;
    }
  }

  const parsed = Number.parseInt(port, 10);

  return parsed > 0 && parsed <= MAX_PORT_NUMBER;
}

/**
 * Whether the authority portion is well formed. Ports `IsAcceptableAuthority`
 * (`PortalAliasRules.cs:L205-L238`).
 *
 * @param authority The value up to the first path separator.
 * @returns True when the authority is acceptable.
 */
function isAcceptableAuthority(authority: string): boolean {
  const separator = authority.indexOf(PORT_SEPARATOR);
  const host = separator === -1 ? authority : authority.slice(0, separator);

  if (isAcceptableHost(host) === false) {
    return false;
  }

  if (separator === -1) {
    return true;
  }

  return isAcceptablePort(authority.slice(separator + PORT_SEPARATOR.length));
}

/**
 * Whether one path segment could name a tenant. Ports `PortalAliasTopology.IsAddressableSegment`:
 * non-empty, ASCII letters, digits, hyphens and underscores only, and not one of {@link
 * RESERVED_PATH_SEGMENTS}.
 *
 * @param segment One path segment, without separators.
 * @returns True when the segment is one the server could store.
 */
function isAddressableSegment(segment: string): boolean {
  if (segment.length === 0) {
    return false;
  }

  for (const character of segment) {
    if (
      isAsciiAlphanumeric(character) === false &&
      PATH_SEGMENT_EXTRAS.includes(character) === false
    ) {
      return false;
    }
  }

  return RESERVED_PATH_SEGMENTS.includes(segment.toLowerCase()) === false;
}

/**
 * Whether the child path is well formed.
 *
 * @param path The value after the first path separator.
 * @returns True when the path is acceptable.
 */
function isAcceptablePath(path: string): boolean {
  if (path.length === 0) {
    return false;
  }

  const segments = path.split(PATH_SEPARATOR);

  if (segments.length > MAXIMUM_PATH_SEGMENTS) {
    return false;
  }

  return segments.every((segment) => isAddressableSegment(segment));
}

/**
 * Whether an alias names a path this deployment can deliver a request to.
 *
 * @param alias The entry exactly as typed.
 * @returns True when the alias carries no path, or carries one this deployment can address.
 */
function isWithinSupportedTopology(alias: string): boolean {
  if (alias.trim().length === 0) {
    return true;
  }

  const pathStart = alias.indexOf(PATH_SEPARATOR);

  return (
    pathStart === -1 || isAcceptablePath(alias.slice(pathStart + PATH_SEPARATOR.length))
  );
}

/**
 * Whether a normalised host name is one the server will accept.
 *
 * @param alias The normalised value.
 * @returns True when the value is acceptable.
 */
export function isAcceptableHttpAlias(alias: string): boolean {
  if (alias.trim().length === 0) {
    return false;
  }

  if (containsOnlyPermittedCharacters(alias) === false) {
    return false;
  }

  const pathStart = alias.indexOf(PATH_SEPARATOR);
  const authority = pathStart === -1 ? alias : alias.slice(0, pathStart);

  if (isAcceptableAuthority(authority) === false) {
    return false;
  }

  return (
    pathStart === -1 || isAcceptablePath(alias.slice(pathStart + PATH_SEPARATOR.length))
  );
}

// -----------------------------------------------------------------------------
//  ROUTE PARAMETER READING
// -----------------------------------------------------------------------------

/**
 * Parses the `:portalId` path segment into a portal identifier. EVERY INTEGER IS A LEGITIMATE PORTAL
 * IDENTIFIER, so there is no in-band value this function can return to mean "the address did not carry
 * one".
 *
 * @param value The raw route parameter, which arrives as text from the router.
 * @returns The identifier, or `NaN` when the segment does not carry one.
 */
export function toPortalIdentifier(value: unknown): number {
  if (typeof value === 'number') {
    return isRouteId(value) ? value : Number.NaN;
  }
  if (typeof value !== 'string') {
    return Number.NaN;
  }
  return parseRouteId(value) ?? Number.NaN;
}

// -----------------------------------------------------------------------------
//  FORM
// -----------------------------------------------------------------------------

/** The name of the one control, which is also the wire member and the label target. */
const ALIAS_CONTROL_NAME = 'httpAlias';

/**
 * The rendered control's element identifier, so the shared form field's `for` binding names it and the
 * label is programmatically associated.
 */
const ALIAS_CONTROL_ELEMENT_ID = 'portal-alias-http-alias';

/** Error key: the normalised value is blank. */
const ALIAS_BLANK_ERROR = 'httpAliasBlank';

/** Error key: the normalised value exceeds the storage and server limit of 200. */
const ALIAS_TOO_LONG_ERROR = 'httpAliasTooLong';

/** Error key: the normalised value is not a host name the server accepts. */
const ALIAS_INVALID_ERROR = 'httpAliasInvalid';

/**
 * Error key: the value names a path deeper than this deployment can route, or a segment it reserves for
 * itself.
 */
const ALIAS_UNSUPPORTED_PATH_ERROR = 'httpAliasUnsupportedPath';

export interface PortalAliasFormModel {
  /** The host name, as entered. */
  readonly httpAlias: FormControl<string>;
}

/**
 * @param control The alias control.
 * @returns The failures found, or null when the entry is acceptable.
 */
export function httpAliasValidator(control: AbstractControl<string, string>): ValidationErrors | null {
  const entry = control.value;

  if (entry.length === 0) {
    return null;
  }

  if (entry.trim().length === 0) {
    return { [ALIAS_BLANK_ERROR]: true };
  }

  if (entry.length > ALIAS_MAX_LENGTH) {
    return { [ALIAS_TOO_LONG_ERROR]: true };
  }

  const failures: ValidationErrors = {};

  if (isAcceptableHttpAlias(entry) === false) {
    failures[ALIAS_INVALID_ERROR] = true;
  }

  if (isWithinSupportedTopology(entry) === false) {
    failures[ALIAS_UNSUPPORTED_PATH_ERROR] = true;
  }

  return Object.keys(failures).length === 0 ? null : failures;
}

// SHARED IMMUTABLE SEEDS

/** The empty row set. */
const NO_ALIASES: readonly PortalAlias[] = Object.freeze([]);

/** The empty message set. */
const NO_MESSAGES: readonly string[] = Object.freeze([]);

type PendingOperation = 'none' | 'list' | 'create' | 'update' | 'delete';

// =============================================================================
//  COMPONENT
// =============================================================================

/**
 * The portal HTTP-alias screen: a listing, plus an inline form that creates, edits and removes one host
 * name. 10: THE LISTING AND THE FORM ARE ONE COMPONENT. The legacy application used two controls reached
 * by a query string - `Website/admin/Portal/PortalAlias.ascx.vb` listed, and
 * `Website/admin/Portal/EditPortalAlias.ascx.vb` added, edited and removed - and the target feature
 * declares no alias-form route for the second one to become.
 */
@Component({
  selector: 'app-portal-alias-list',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    ConfirmDialogComponent,
    DataTableComponent,
    EmptyStateComponent,
    ErrorBannerComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    PageHeaderComponent,
  ],
  templateUrl: './portal-alias-list.component.html',
  styleUrl: './portal-alias-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalAliasListComponent implements OnInit {
  // ---------------------------------------------------------------------------
  //  COLLABORATORS
  // ---------------------------------------------------------------------------

  /**
   * The one source of alias data and the one place alias writes are issued. Root-provided, so it is
   * injected and never listed in a `providers` array here - all provider wiring for this application
   * lives in `app/app.config.ts`.
   */
  private readonly store = inject(PortalStore);

  /** The announcement queue. */
  private readonly notifications = inject(NotificationService);

  /**
   * Binds this screen's write subscriptions to its own lifetime. The store is provided at the root and
   * therefore outlives this screen, so an outcome ticket left subscribed across a teardown would run this
   * screen's continuation — closing a form that no longer exists and announcing a success into a route
   * the operator has left.
   */
  private readonly destroyRef = inject(DestroyRef);

  // Held solely so `afterNextRender` can be reached from outside the constructor. Its only consumers are
  // `focusEntry` and `restoreInvokerFocus`, both of which must run AFTER the conditional form block has
  // been created or destroyed - which is a render, not a signal write.
  private readonly injector = inject(Injector);

  // The screen's own root element. Used only to resolve the create action as a focus fallback -
  // see `restoreInvokerFocus` - so the lookup is scoped to this screen rather than the document.
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  // ---------------------------------------------------------------------------
  //  ROUTE INPUT
  // ---------------------------------------------------------------------------

  /**
   * Which portal's host names to show, delivered from the `:portalId` path segment. THE NAME IS PART OF
   * THE ROUTE CONTRACT. `withComponentInputBinding()` matches a route parameter to an input of the same
   * name, so renaming this member stops the binding silently - the component still compiles, still
   * renders and simply never receives a portal.
   */
  @Input({ required: true, transform: toPortalIdentifier })
  public set portalId(value: number) {
    const previous = this.portalIdValue();

    this.portalIdValue.set(value);

    if (Number.isNaN(value)) {
      return;
    }

    if (previous === value) {
      return;
    }

    this.closeForm();
    this.pending = 'list';
    this.store.clearFailures();
    this.store.loadAliases(value);
  }

  public get portalId(): number {
    return this.portalIdValue();
  }

  // ---------------------------------------------------------------------------
  //  VIEW QUERY
  // ---------------------------------------------------------------------------

  /**
   * The per-row command cell, supplied by this component's own template. `static: true` resolves it
   * during view creation, before the lifecycle hook runs, which is what lets the column list be built
   * once with a stable reference instead of re-forming after the first render.
   */
  @ViewChild('aliasCommands', { static: true })
  protected commandCell?: TemplateRef<DataTableCellContext<PortalAlias>>;

  /**
   * The entry box inside the inline form. NOT static: the form is inside a conditional block, so the
   * query cannot resolve before the block is created.
   */
  @ViewChild('entryBox')
  private entryBox?: ElementRef<HTMLInputElement>;

  /**
   * The control that opened the form, so focus can be handed back to it. ⚠ CAPTURED FROM THE LIVE FOCUS
   * RATHER THAN PASSED IN. The form is opened from three places - the page-level create action, a row's
   * Edit command, and a press on the row itself - and the row press arrives through the shared grid's own
   * output, which carries the ROW and not the element that was pressed.
   */
  private formInvoker: HTMLElement | null = null;

  /**
   * The host-name cell's template. Pf-M1: the column was a plain field column, so a row holding no host
   * name - or an empty one - painted a cell whose entire content was whitespace, indistinguishable from a
   * rendering failure and offering nothing to press or read.
   */
  @ViewChild('aliasHostName', { static: true })
  protected hostNameCell?: TemplateRef<DataTableCellContext<PortalAlias>>;

  // ---------------------------------------------------------------------------
  //  PRESENTATION STATE
  // ---------------------------------------------------------------------------

  /** The parsed route parameter. */
  private readonly portalIdValue = signal<number>(Number.NaN);

  /** Whether the inline form is on screen. */
  private readonly formVisible = signal<boolean>(false);

  /** Whether the deletion prompt is on screen. */
  private readonly deletePrompt = signal<boolean>(false);

  /** Whether a submit has been attempted, which is when client messages appear. */
  private readonly submitAttempted = signal<boolean>(false);

  /** The client-side field messages currently to show. */
  private readonly clientAliasMessages = signal<readonly string[]>(NO_MESSAGES);

  /** The command column, once its cell template has been resolved. */
  private readonly commandColumn = signal<DataTableColumn<PortalAlias> | null>(null);

  /** Which command is awaiting an answer. */
  private pending: PendingOperation = 'none';

  /**
   * The last failure already announced, held by REFERENCE. The store replaces the whole classified
   * failure object on each failure, so reference inequality is exactly "this is a new failure" -
   * including a second refusal identical in every field to the first, which a value comparison would
   * swallow and which an operator does need to hear about again.
   */
  private announcedFailure: PortalFailure | null = null;

  // ---------------------------------------------------------------------------
  //  THE FORM
  // ---------------------------------------------------------------------------

  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.loading() === false,
  );

  protected readonly form = new FormGroup<PortalAliasFormModel>({
    httpAlias: new FormControl<string>('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(ALIAS_ENTRY_MAX_LENGTH),
        httpAliasValidator,
      ],
    }),
  });

  // ---------------------------------------------------------------------------
  //  STATIC WORDING FOR THE TEMPLATE
  // ---------------------------------------------------------------------------

  protected readonly heading = HEADING;

  protected readonly addActionLabel = ADD_ACTION_LABEL;

  protected readonly aliasLabel = ALIAS_LABEL;

  protected readonly aliasHelp = ALIAS_HELP;

  protected readonly cancelLabel = CANCEL_LABEL;

  protected readonly deleteLabel = DELETE_LABEL;

  protected readonly editLabel = EDIT_LABEL;

  /**
   * The confirmation body: the platform's question, then the host name it means. ⚠ THE MEASURED DEFECT,
   * AND THIS SCREEN IS WHERE IT BITES HARDEST. The body was the bare sentence "Are You Sure You Wish To
   * Delete This Item?" and named nothing, while the dialog is a real modal that covers the table it was
   * raised from - measured obscuring four alias rows, including the row being destroyed.
   */
  protected readonly deleteConfirmMessage = computed<string>(() => {
    const target = this.selectedAlias();

    return target === null
      ? DELETE_CONFIRM_MESSAGE
      : `${DELETE_CONFIRM_MESSAGE} ${target.httpAlias}`;
  });

  protected readonly emptyMessage = EMPTY_MESSAGE;

  protected readonly unusableRouteMessage = UNUSABLE_ROUTE_MESSAGE;

  protected readonly aliasInputMaxLength = ALIAS_ENTRY_MAX_LENGTH;

  protected readonly aliasControlId = ALIAS_CONTROL_ELEMENT_ID;

  protected readonly absentHostNameDescription = ABSENT_HOST_NAME_DESCRIPTION;

  protected readonly currentAliasRowNote = CURRENT_ALIAS_ROW_NOTE;

  // ---------------------------------------------------------------------------
  //  DERIVED VIEWS
  // ---------------------------------------------------------------------------

  /** Whether the address carries a usable portal identifier. */
  protected readonly routeUsable = computed<boolean>(
    () => Number.isNaN(this.portalIdValue()) === false,
  );

  protected readonly settingsLink = computed<(string | number)[] | null>(() => {
    const target = this.portalIdValue();

    return Number.isNaN(target) ? null : ['/portals', target, 'settings'];
  });

  /** The wording of that link: `ControlTitle_.Text` in `SiteSettings.ascx.resx`. */
  protected readonly settingsLinkLabel: string = SETTINGS_LINK_LABEL;

  /**
   * The rows to render: the store's collection, but ONLY when it belongs to the portal this screen is
   * showing. The store is application-scoped, so the collection in hand may have been read for a
   * different portal - during a navigation between two portals it certainly has.
   */
  protected readonly rows = computed<readonly PortalAlias[]>(() => {
    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return NO_ALIASES;
    }

    if (this.store.aliasesPortalId() !== target) {
      return NO_ALIASES;
    }

    const held = this.store.aliases();

    return held === null ? NO_ALIASES : held;
  });

  /** Whether an alias read or write is in flight. */
  protected readonly loading = computed<boolean>(() => this.store.aliasLoading());

  /**
   * Whether the collection has been read FOR THIS PORTAL. `false` while the collection is unread and
   * `true` for a collection that is read and empty, which is the distinction that stops the empty state
   * appearing during a request.
   */
  protected readonly listReady = computed<boolean>(() => {
    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return false;
    }

    return this.store.aliasesPortalId() === target && this.store.aliases() !== null;
  });

  /** Whether the portal has been read and has no host names at all. */
  protected readonly isListEmpty = computed<boolean>(
    () => this.listReady() && this.rows().length === 0,
  );

  /**
   * The columns of the listing. TWO, reproducing the legacy grid exactly: a command column and the
   * host-name column, in that order.
   */
  protected readonly columns = computed<readonly DataTableColumn<PortalAlias>[]>(() => {
    // The heading text comes from the resource key `HTTP Alias.Header` rather than from the markup's
    // `HeaderText` attribute, because `Localization.LocalizeDataGrid` replaced the attribute at run time.
    const hostNameTemplate = this.hostNameCell;

    const aliasColumn: DataTableColumn<PortalAlias> =
      hostNameTemplate === undefined
        ? {
            key: ALIAS_CONTROL_NAME,
            rowHeader: true,
            label: ALIAS_COLUMN_HEADING,
            field: 'httpAlias',
            sortable: false,
          }
        : {
            key: ALIAS_CONTROL_NAME,
            rowHeader: true,
            label: ALIAS_COLUMN_HEADING,
            kind: 'template',
            cellTemplate: hostNameTemplate,
            sortable: false,
          };

    const commands = this.commandColumn();

    return commands === null ? [aliasColumn] : [commands, aliasColumn];
  });

  /** Whether the inline form is on screen. */
  protected readonly formOpen = computed<boolean>(() => this.formVisible());

  protected readonly editing = computed<boolean>(
    () => this.store.selectedAliasId() !== undefined,
  );

  /** The row the form is open on, or null when it is open to add one. */
  private readonly selectedAlias = computed<PortalAlias | null>(() => {
    const chosen = this.store.selectedAliasId();

    if (chosen === undefined) {
      return null;
    }

    const found = this.rows().find((alias) => alias.portalAliasId === chosen);

    return found === undefined ? null : found;
  });

  /** Whether the row the form is open on is the one this request arrived through. */
  protected readonly editingCurrentAlias = computed<boolean>(() => {
    const chosen = this.selectedAlias();

    return chosen !== null && chosen.isCurrent;
  });

  protected readonly submitLabel = computed<string>(() =>
    this.editing() ? UPDATE_SUBMIT_LABEL : ADD_SUBMIT_LABEL,
  );

  protected readonly deleteAffordanceVisible = computed<boolean>(() => {
    if (this.editing() === false) {
      return false;
    }

    if (this.editingCurrentAlias()) {
      return false;
    }

    return this.rows().length > 1;
  });

  /** Whether the deletion prompt is on screen. */
  protected readonly confirmingDelete = computed<boolean>(() => this.deletePrompt());

  /**
   * The server's message for the alias field, or null when it has none. Two sources, in precedence order:
   * * a state refusal, which for these endpoints can only be the duplicate host name.
   */
  protected readonly serverAliasMessage = computed<string | null>(() => {
    const failure = this.store.aliasFailure();

    if (failure === null) {
      return null;
    }

    if (isDuplicateRefusal(failure)) {
      return DUPLICATE_ALIAS_MESSAGE;
    }

    if (isActiveAliasRefusal(failure)) {
      return CURRENT_ALIAS_MESSAGE;
    }

    return fieldErrorMessage(failure.problem, ALIAS_CONTROL_NAME);
  });

  /**
   * Every message to show beside the alias control: this screen's own rules first, then the server's. A
   * computed over two signals, so its result changes only when one of them does - which matters because
   * the shared form field takes its messages through a setter that writes a signal, and a fresh array on
   * every change-detection pass would notify it on every pass.
   */
  protected readonly aliasError = computed<readonly string[]>(() => {
    const client = this.clientAliasMessages();
    const server = this.serverAliasMessage();

    if (server === null) {
      return client;
    }

    return client.length === 0 ? [server] : [...client, server];
  });

  protected readonly bannerProblem = computed<ProblemDetails | null>(
    () => this.store.aliasFailure()?.problem ?? null,
  );

  // ---------------------------------------------------------------------------
  //  CONSTRUCTION AND LIFECYCLE
  // ---------------------------------------------------------------------------

  /** Registers the one reaction this screen needs. */
  public constructor() {
    effect(() => {
      const failure = this.store.aliasFailure();

      if (failure === null) {
        this.announcedFailure = null;

        return;
      }

      if (this.announcedFailure === failure) {
        return;
      }

      this.announcedFailure = failure;
      this.announceRefusal(failure);
    });
  }

  /**
   * Adopts the command cell template and issues the first read if the input setter has not already. The
   * static view query has resolved by now, so the command column is formed once and keeps one reference
   * for the life of the component.
   */
  public ngOnInit(): void {
    const template = this.commandCell;

    if (template !== undefined) {
      this.commandColumn.set({
        key: 'commands',
        // The legacy command column carried no heading text at all, so the heading is hidden rather than
        // invented; the label is still supplied because it is the column's accessible name, and it is the
        // wording the legacy image already carried through `resourcekey="Edit"`.
        label: EDIT_LABEL,
        headerHidden: true,
        // The legacy `ItemStyle Width="15px"` SNAPS to the shared grid's narrowest declared track.
        width: 'min-content',
        kind: 'actions',
        cellTemplate: template,
      });
    }

    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return;
    }

    if (this.listReady() === false && this.loading() === false) {
      this.pending = 'list';
      this.store.loadAliases(target);
    }
  }

  // ---------------------------------------------------------------------------
  //  COMMANDS - THE FORM
  // ---------------------------------------------------------------------------

  /** Opens the form to add a host name. */
  protected startCreate(): void {
    this.store.clearAliasSelection();
    this.store.clearFailures();
    this.resetEntry('');
    this.formVisible.set(true);
    this.focusEntry();
  }

  /**
   * @param alias One row of the listing.
   * @returns True when the row may be renamed.
   */
  protected rowEditable(alias: PortalAlias): boolean {
    return alias.isCurrent === false;
  }

  /**
   * Whether one row's stored host name is absent or empty. Pf-M1: the two states are DELIBERATELY
   * answered together, because the display answer for both is the same and the contract still
   * distinguishes them.
   *
   * @param alias One row of the listing.
   * @returns True when there is no host name to paint.
   */
  protected isHostNameAbsent(alias: PortalAlias): boolean {
    return aliasText(alias).length === 0;
  }

  /**
   * The text of one row's host-name cell. Answers the stored value verbatim when there is one - no
   * folding, no trimming, no normalisation, because the operator must see what is stored in order to
   * correct it - and the absent mark when there is not.
   *
   * @param alias One row of the listing.
   * @returns What the cell paints.
   */
  protected hostNameText(alias: PortalAlias): string {
    const held = aliasText(alias);

    return held.length === 0 ? ABSENT_HOST_NAME_MARK : held;
  }

  /**
   * The ACCESSIBLE name of one row's edit command. Pf-M3: qualified with the row, so fifteen commands on
   * one screen no longer reach assistive technology under one indistinguishable name.
   *
   * @param alias The row the command acts on.
   * @returns The accessible name.
   */
  protected editCommandLabel(alias: PortalAlias): string {
    const held = aliasText(alias);

    return editCommandName(held.length === 0 ? ABSENT_HOST_NAME_DESCRIPTION : held);
  }

  /**
   * Opens the form to edit one existing host name. 17: REFUSES THE ROW THIS REQUEST ARRIVED THROUGH, and
   * the guard is not redundant with the withheld button.
   *
   * @param alias The row to edit.
   */
  protected editAlias(alias: PortalAlias): void {
    if (this.rowEditable(alias) === false) {
      this.notifications.warning(CURRENT_ALIAS_MESSAGE);

      return;
    }

    this.store.clearFailures();
    this.store.selectAlias(alias.portalAliasId);
    this.resetEntry(aliasText(alias));
    this.formVisible.set(true);
    this.focusEntry();
  }

  /** Normalises the entry, judges it and, if it stands, writes it. */
  protected submit(): void {
    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return;
    }

    this.submitAttempted.set(true);

    const control = this.form.controls.httpAlias;
    const entry = control.value;

    control.markAsTouched();
    this.refreshAliasMessages();

    if (this.form.invalid) {
      return;
    }

    this.store.clearFailures();

    const chosen = this.store.selectedAliasId();

    if (chosen === undefined) {
      this.pending = 'create';

      const request: CreatePortalAliasRequest = { httpAlias: entry };

      this.store
        .createAlias(target, request)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          this.onSaved();
        });

      return;
    }

    this.pending = 'update';

    const request: UpdatePortalAliasRequest = { httpAlias: entry };

    this.store
      .updateAlias(target, chosen, request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.onSaved();
      });
  }

  /**
   * Abandons the form. the legacy cancel command redirected to the stored referrer, and to the empty
   * string when there was none.
   */
  protected cancelEdit(): void {
    this.store.clearFailures();
    this.closeForm();
  }

  // ---------------------------------------------------------------------------
  //  COMMANDS - DELETION
  // ---------------------------------------------------------------------------

  /**
   * Asks before removing the selected host name. 14: THE PROMPT IS AN ADDITION. The legacy delete command
   * carried no client-side confirmation of any kind - `editportalalias.ascx:L13` declares no
   * `OnClientClick` - and the handler removed the row on the first click.
   */
  protected requestDelete(): void {
    if (this.deleteAffordanceVisible() === false) {
      return;
    }

    this.deletePrompt.set(true);
  }

  /** Dismisses the prompt without removing anything. */
  protected cancelDelete(): void {
    this.deletePrompt.set(false);
  }

  /** Removes the selected host name. */
  protected confirmDelete(): void {
    this.deletePrompt.set(false);

    const target = this.portalIdValue();
    const chosen = this.store.selectedAliasId();

    if (Number.isNaN(target) || chosen === undefined) {
      return;
    }

    if (this.editingCurrentAlias()) {
      this.notifications.warning(CURRENT_ALIAS_MESSAGE);

      return;
    }

    this.pending = 'delete';
    this.store.clearFailures();

    this.store
      .deleteAlias(target, chosen)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.onDeleted();
      });
  }

  // ---------------------------------------------------------------------------
  //  COMMANDS - FIELD FEEDBACK
  // ---------------------------------------------------------------------------

  /**
   * Refreshes the field messages after a keystroke. Bound from the template alongside the control
   * binding, so validity changes reach a signal at the moment they happen.
   */
  protected onAliasInput(): void {
    this.refreshAliasMessages();
  }

  /**
   * Refreshes the field messages when the control loses focus. Messages appear once the control has been
   * visited or a submit has been attempted, so an operator is not told a field is required before they
   * have reached it.
   */
  protected onAliasBlur(): void {
    this.refreshAliasMessages();
  }

  // ---------------------------------------------------------------------------
  //  PRIVATE
  // ---------------------------------------------------------------------------

  private onSaved(): void {
    // ⚠ THE FAILURE SURFACE IS CLEARED HERE BECAUSE SUCCESS AND FAILURE FOR ONE ACTION ARE MUTUALLY
    // EXCLUSIVE, and this screen was observed asserting both at once.
    this.store.clearFailures();

    this.notifications.success(SAVED_MESSAGE);

    // ⚠ THE REMEMBERED INVOKER IS DISCARDED ON THIS PATH, AND THAT IS THE POINT. A save re-reads the
    // collection, so the row that was edited is re-rendered and the element that opened the form is
    // replaced.
    this.discardFormInvoker();
    this.closeForm();
  }

  /**
   * Completes a successful delete. @see DELETED_MESSAGE ⚠ ANNOUNCED AS A SUCCESS, NOT AS INFORMATION, AND
   * THE CHANGE IS FOR CONSISTENCY. This was the ONLY completed mutation in the console announced at the
   * informational severity.
   */
  private onDeleted(): void {
    this.notifications.success(DELETED_MESSAGE);

    // The row the operator was standing on no longer exists, so there is nothing to hand focus back
    // to. See `discardFormInvoker`.
    this.discardFormInvoker();
    this.closeForm();
  }

  /**
   * Returns the form to its closed, pristine state. The store's alias selection is cleared as well, so
   * the next opening of the form starts in add mode rather than inheriting whichever row was last
   * selected.
   */
  private closeForm(): void {
    this.formVisible.set(false);
    this.deletePrompt.set(false);
    this.store.clearAliasSelection();
    this.resetEntry('');
    this.pending = 'none';
    this.restoreInvokerFocus();
  }

  /**
   * Remembers what is focused now, then moves focus into the form's entry box. ⚠ TWO MEASURED DEFECTS,
   * ONE CAUSE: NOTHING MANAGED FOCUS ACROSS THIS FORM AT ALL. Opening the editor left focus on the
   * control that opened it, so a keyboard operator pressed Edit, was shown a form, and then had to tab
   * forwards through the REST OF THE TABLE to reach it - measured at twenty-two stops, because the form
   * renders after the whole grid.
   */
  private focusEntry(): void {
    this.formInvoker =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;

    afterNextRender(
      () => {
        // Re-read rather than captured: the query resolves during the render this callback follows, and a
        // form closed again before the callback ran would leave it unresolved. A presence test, never a
        // truthiness test on a node, and no non-null assertion.
        const box: ElementRef<HTMLInputElement> | undefined = this.entryBox;

        if (box === undefined || !box.nativeElement.isConnected) {
          return;
        }

        box.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  /**
   * Hands focus back to whatever opened the form, if it is still there to take it. ⚠ THE INVOKER IS NOT
   * ALWAYS STILL THERE, and that is why this is not a bare `focus()` call. Cancelling leaves the listing
   * untouched, so the row's Edit command is still connected and receives focus back - the ordinary case.
   */
  /**
   * Forgets the control that opened the form, so the close falls back rather than restoring. Used on the
   * paths that DESTROY or REPLACE the invoker - a save, which re-reads the collection, and a removal,
   * which drops the row outright.
   */
  private discardFormInvoker(): void {
    this.formInvoker = null;
  }

  private restoreInvokerFocus(): void {
    const remembered: HTMLElement | null = this.formInvoker;

    this.formInvoker = null;

    afterNextRender(
      () => {
        if (remembered !== null && remembered.isConnected) {
          remembered.focus({ preventScroll: true });

          return;
        }

        const fallback: HTMLElement | null = this.host.nativeElement.querySelector<HTMLElement>(
          'app-page-header button',
        );

        fallback?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /**
   * Puts one value into the control and returns it to a pristine, unreported state.
   *
   * @param value The value to hold.
   */
  private resetEntry(value: string): void {
    this.submitAttempted.set(false);
    this.form.controls.httpAlias.reset(value);
    this.clientAliasMessages.set(NO_MESSAGES);
  }

  /**
   * Recomputes which of this screen's own rules to report. Nothing is reported until the control has been
   * visited or a submit attempted, and at most ONE message is reported at a time: the rules form a
   * precedence chain - emptiness, then the entry cap, then the transmitted length, then the shape - and
   * stating that a value is both blank and malformed says nothing useful twice.
   */
  private refreshAliasMessages(): void {
    const control = this.form.controls.httpAlias;

    if (this.deletePrompt()) {
      this.clientAliasMessages.set(NO_MESSAGES);

      return;
    }

    if (this.submitAttempted() === false && control.touched === false) {
      this.clientAliasMessages.set(NO_MESSAGES);

      return;
    }

    if (control.hasError('required') || control.hasError(ALIAS_BLANK_ERROR)) {
      this.clientAliasMessages.set([ALIAS_REQUIRED_MESSAGE]);

      return;
    }

    if (control.hasError('maxlength')) {
      this.clientAliasMessages.set([ALIAS_ENTRY_TOO_LONG_MESSAGE]);

      return;
    }

    if (control.hasError(ALIAS_TOO_LONG_ERROR)) {
      this.clientAliasMessages.set([ALIAS_TOO_LONG_MESSAGE]);

      return;
    }

    // Both sentences when both rules refused the entry, in the order the server declares them, so
    // the operator reads the general refusal and then the specific reason for it.
    const shapeMessages: string[] = [];

    if (control.hasError(ALIAS_INVALID_ERROR)) {
      shapeMessages.push(ALIAS_INVALID_MESSAGE);
    }

    if (control.hasError(ALIAS_UNSUPPORTED_PATH_ERROR)) {
      shapeMessages.push(ALIAS_UNSUPPORTED_PATH_MESSAGE);
    }

    if (shapeMessages.length > 0) {
      this.clientAliasMessages.set(shapeMessages);

      return;
    }

    this.clientAliasMessages.set(NO_MESSAGES);
  }

  /** @param failure The classified failure the store published. */
  private announceRefusal(failure: PortalFailure): void {
    if (failure.status !== FORBIDDEN_STATUS) {
      return;
    }

    switch (this.pending) {
      case 'delete':
        this.notifications.warning(DELETE_DENIED_MESSAGE);
        break;
      case 'list':
        this.notifications.warning(VIEW_DENIED_MESSAGE);
        break;
      case 'create':
      case 'update':
      case 'none':
        break;
      default:
        break;
    }
  }
  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them. ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT.
   * The grid's own fallback is the row OBJECT, which is a correct key only while the same objects stay in
   * play; every read from the server decodes fresh objects, so without this a refetch of the same page
   * presents entirely new keys and the whole body is rebuilt to display records that never changed.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly aliasRowKey = (row: PortalAlias): number => row.portalAliasId;
}
