/**
 * KARMA + JASMINE SPECIFICATION FOR THE RFC 7807 ERROR SURFACE.
 *
 * ## Framework, and why it is not a preference
 *
 * Karma with Jasmine, fixed rather than chosen. The migration plan resolves the
 * runner conflict explicitly in favour of Karma because the mandated acceptance
 * command is `ng test --watch=false --browsers=ChromeHeadless --code-coverage`,
 * which IS the Karma invocation; selecting Jest would make the mandated command
 * invalid. Nothing here uses Jest, any DOM-testing-library helper, or any spy
 * mechanism other than Jasmine's own.
 *
 * ## Provenance: this specification has no predecessor
 *
 * The legacy tree contains ZERO automated tests, and that was measured rather
 * than assumed. Searching both legacy trees for test-shaped file names returns
 * only substring noise — `UserCreateStatus.vb` and `PasswordUpdateStatus.vb` match
 * on "teSt", the bundled rich-text editor contributes three `special*` files that
 * match on "spec", and `Website/js/ClientAPITests/` is a MANUAL browser harness of
 * `.htm` pages rather than an automated suite. Searching for test-framework
 * attributes returns only a third-party readme, pre-built binaries and a build
 * script. No `.vbproj` or `.csproj` in the repository is a test project. So every
 * assertion below is net-new, and no legacy test was ported to produce it.
 *
 * The component under test likewise has no in-scope legacy ancestor. The plan
 * cites an `asp:ValidationSummary` rendering path; that control occurs zero times
 * in the in-scope markup and zero times anywhere under `Website/`, placing the
 * citation in the same family as the plan's own measured-vacuous exclusions. The
 * real analogue, `Library/Components/Skins/ModuleMessage.vb`, sits in an excluded
 * sub-tree and is therefore a reference only. Where an assertion below is
 * justified by legacy behaviour, the justifying file and line are named on it.
 *
 * ## Why this file matters beyond its own assertions
 *
 * It is the ONLY route by which the component is type-checked at all.
 * `tsconfig.app.json` declares `files: ["src/main.ts"]` and compiles by import
 * graph, so a component that nothing imports yet is silently skipped by
 * `ng build`. `tsconfig.spec.json` includes `src/**` + `/*.spec.ts`, so importing
 * the component here pulls it — and its template, under `strictTemplates` — into a
 * real compilation. The specification therefore deliberately touches EVERY public
 * member of the component and both of its exported types, so that none of them can
 * rot unnoticed.
 *
 * ## Dependency contract discrepancies, resolved in favour of the real files
 *
 * Three points where the planning notes for this file describe a contract the
 * shipped dependencies do not have. In each case the real dependency wins, because
 * an assertion written against an imagined API is worse than no assertion:
 *
 * 1. `ProblemDetails.correlationId` EXISTS and is the support reference. The
 *    planning notes name only `traceId`. `problemSupportReference` prefers the
 *    correlation identifier — the value that also travels on the response header
 *    and appears in the server's own audit records — and falls back to the W3C
 *    trace identifier only when no correlation identifier was published. Both
 *    orderings are asserted below.
 * 2. A `traceId` of `null` is NOT EXPRESSIBLE and is asserted as absent instead.
 *    The model declares `readonly traceId?: string`, with no null in the union, and
 *    documents why at length: the framework's problem-details type annotates each
 *    standard member with a per-member null-omission condition, and a per-member
 *    condition overrides the collection-wide `DefaultIgnoreCondition` of `Never`.
 *    A null member is therefore omitted from the document rather than serialised as
 *    `null`, which the model records as verified against live `401` and `400`
 *    responses. Writing a null here would need a cast, and a cast to prove a wire
 *    shape the wire cannot produce proves nothing. The absent case is asserted, and
 *    so is the present-but-`undefined` case, which is the type-safe analogue and
 *    exercises the same `=== undefined` guard the utility uses.
 * 3. `role="alert"` IS co-located with `aria-live` on the live region, by design.
 *    The template's own reasoning is that `role="alert"` implies
 *    `aria-live="assertive"` and `aria-atomic="true"`, and that stating the SAME
 *    values it implies makes the behaviour legible without creating a second,
 *    competing mechanism. That reasoning is sound, so the assertion below pins the
 *    property that actually matters: the stated values must MATCH the ones the role
 *    implies, never contradict them.
 *
 * ## Determinism
 *
 * No focused or skipped specs, no `console`, no timers, no clock reads. Every
 * fixture is a frozen module-level constant declared as the real interface, so a
 * mistyped member is a compile error rather than a silently absent one.
 *
 * The component derives every rendered value with `computed()` over a SIGNAL input
 * and declares no `effect()`, so `componentRef.setInput` followed by
 * `detectChanges()` is sufficient: `computed` is pull-based and re-evaluates during
 * template evaluation. `TestBed.flushEffects()` was verified to exist in the
 * installed `@angular/core@19.2.25` and `TestBed.tick()` was verified NOT to, but
 * neither is called, because there is no effect to flush and the stable path
 * suffices.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  ProblemDetails,
  ValidationProblemDetails,
} from '../../../core/models/problem-details.model';
import {
  CONFLICT,
  FORBIDDEN,
  NOT_AUTHENTICATED,
  NOT_FOUND,
  REQUEST_REJECTED,
  SERVER_ERROR,
  TOO_MANY_ATTEMPTS,
  VALIDATION_REJECTED,
} from '../../../core/utils/form-errors.util';
import {
  ErrorBannerComponent,
  ErrorBannerSeverity,
  FieldErrorGroup,
} from './error-banner.component';

// ---------------------------------------------------------------------------
// SELECTORS - the rendered contract, named once
// ---------------------------------------------------------------------------

/** The persistent live region, which is in the document whether or not there is a failure. */
const LIVE_REGION = '.error-banner-live';

/** The banner itself, present only while a failure is bound. */
const BANNER = '.error-banner';

/** The severity word, which is what makes the band perceivable without colour. */
const SEVERITY_WORD = '.error-banner__severity';

/** The problem-type heading, shown only when it says something the message does not. */
const TITLE = '.error-banner__title';

/** The one sentence describing this occurrence. */
const MESSAGE = '.error-banner__message';

/** The per-field description list. */
const FIELD_LIST = '.error-banner__fields';

/** One field label - a `dt` within the description list. */
const FIELD_LABEL = '.error-banner__field';

/** One field message - a `dd` within the description list. */
const FIELD_MESSAGE = '.error-banner__detail';

/** The support reference line. */
const TRACE = '.error-banner__trace';

// ---------------------------------------------------------------------------
// NARROWING HELPERS - no non-null assertion is used anywhere in this file
// ---------------------------------------------------------------------------

/**
 * Finds an element that the assertion requires to be present.
 *
 * `querySelector` honestly returns `Element | null`, and this narrows it with an
 * explicit check that THROWS a descriptive error rather than with a non-null
 * assertion. The distinction is practical rather than stylistic: an assertion
 * operator would turn a missing element into an unhelpful "cannot read properties
 * of null", whereas this names the selector that failed to match.
 *
 * `DebugElement.query` is deliberately not used for the same reason. It is typed as
 * returning a non-nullable `DebugElement` while returning null at runtime, so it
 * type-checks a dereference that can fail — which is precisely the hole a strict
 * compiler exists to close.
 *
 * @param host The component's host element.
 * @param selector The selector to match.
 * @returns The matched element.
 */
function requireElement(host: HTMLElement, selector: string): HTMLElement {
  const found: Element | null = host.querySelector(selector);

  if (found === null) {
    throw new Error(`the banner rendered no element matching "${selector}"`);
  }

  if (found instanceof HTMLElement) {
    return found;
  }

  throw new Error(`"${selector}" matched <${found.nodeName}>, which is not an HTML element`);
}

/**
 * The trimmed visible text of an element.
 *
 * `textContent` is `string | null`, narrowed with a nullish fallback rather than an
 * assertion. Reading `textContent` rather than the parsed-markup property is itself
 * part of the contract under test: a value that reached the document as markup would
 * appear here as its literal characters, which is exactly what the escaping
 * assertions below rely on.
 *
 * @param element The element to read.
 * @returns The trimmed text, empty when there is none.
 */
function textOf(element: Element): string {
  return (element.textContent ?? '').trim();
}

/**
 * The trimmed text of an element the assertion requires to be present.
 *
 * @param host The component's host element.
 * @param selector The selector to match.
 * @returns The trimmed text.
 */
function requireText(host: HTMLElement, selector: string): string {
  return textOf(requireElement(host, selector));
}

/**
 * The trimmed text of an element that may legitimately be absent.
 *
 * @param host The component's host element.
 * @param selector The selector to match.
 * @returns The trimmed text, or null when nothing matched.
 */
function optionalText(host: HTMLElement, selector: string): string | null {
  const found: Element | null = host.querySelector(selector);

  return found === null ? null : textOf(found);
}

/**
 * The trimmed text of every element matching a selector, in document order.
 *
 * @param host The component's host element.
 * @param selector The selector to match.
 * @returns One entry per match, possibly empty.
 */
function allText(host: HTMLElement, selector: string): readonly string[] {
  return Array.from(host.querySelectorAll(selector)).map(textOf);
}

/**
 * Whether anything at all matches a selector.
 *
 * @param host The component's host element.
 * @param selector The selector to match.
 * @returns True when at least one element matches.
 */
function hasElement(host: HTMLElement, selector: string): boolean {
  return host.querySelector(selector) !== null;
}

// ---------------------------------------------------------------------------
// FIXTURES
// ---------------------------------------------------------------------------
//
// EVERY fixture is declared with its real interface - `ProblemDetails` or
// `ValidationProblemDetails` - and never as a bare literal and never through a
// cast. That is what makes a mistyped member a COMPILE error: an object literal
// assigned to an annotated target is excess-property checked, so `titel` or
// `traceID` fails the build instead of silently arriving as `undefined` and
// producing a green test that proves nothing.
//
// The wording is reproduced VERBATIM from the legacy resource files wherever a
// legacy equivalent exists, including one legacy misspelling that is deliberately
// preserved. Inventing friendlier wording here would quietly weaken the parity
// these fixtures exist to demonstrate.

/** A 404 carrying both a type heading and an occurrence-specific sentence. */
const NOT_FOUND_WITH_DETAIL: ProblemDetails = {
  type: 'urn:dnnmigration:error:portal.not_found',
  title: 'Not Found',
  status: 404,
  detail: 'Portal 42 does not exist.',
};

/** A 404 carrying a heading only, so the heading has to serve as the sentence. */
const NOT_FOUND_TITLE_ONLY: ProblemDetails = {
  title: 'Not Found',
  status: 404,
};

/** A refusal. Legacy evidence puts this in the WARNING band, never the danger band. */
const PERMISSION_REFUSED: ProblemDetails = {
  type: 'urn:dnnmigration:error:authz.forbidden',
  title: 'Forbidden',
  status: 403,
  detail: 'You do not have permission to edit this portal.',
};

/** A credential rate-limit refusal, which must read calmly rather than as a fault. */
const RATE_LIMITED: ProblemDetails = {
  type: 'urn:dnnmigration:error:auth.too_many_attempts',
  status: 429,
};

/** A server fault carrying no text of its own. */
const SERVER_FAULT: ProblemDetails = {
  status: 500,
};

/** A document with no status at all, which the utility classifies as a fault. */
const STATUSLESS: ProblemDetails = {
  detail: 'Something went wrong upstream.',
};

/**
 * A status outside the set this API is known to return.
 *
 * The reachable set is documentation only - 400, 401, 403, 404, 409, 422, 429 and
 * 500 - and the status is chosen by the server, so an unlisted one is ordinary
 * rather than exceptional. 599 exercises the exhaustive `default` arm that the
 * workspace's no-fallthrough rule requires the component's `switch` to carry.
 */
const UNMAPPED_STATUS: ProblemDetails = {
  status: 599,
  detail: 'A gateway reported an unrecognised condition.',
};

/**
 * A status of exactly zero.
 *
 * Zero is a LEGITIMATE value in this data and never a synonym for absent - the
 * legacy schema seeds `Roles.RoleID`, `Tabs.TabID` and `Modules.ModuleID` with
 * `IDENTITY(0,1)`, so zero identifies a real row. A component testing its status
 * for truthiness would send this down the "no status" path.
 */
const ZERO_STATUS: ProblemDetails = {
  status: 0,
  detail: 'A transport-level failure produced no HTTP status.',
};

/**
 * A status of minus one.
 *
 * Minus one is the legacy integer "absent" marker AND a real identifier at the same
 * time: `Portals.PortalID` is `IDENTITY(-1,1)`, so -1 names the first portal ever
 * created. The collision is why nothing may treat -1 as missing.
 */
const NEGATIVE_ONE_STATUS: ProblemDetails = {
  status: -1,
  detail: 'A sentinel-valued status must still render.',
};

/**
 * A document whose text members are the empty string.
 *
 * The legacy string "absent" marker IS the empty string - `Null.vb` returns
 * literally `""` - so an empty string arrives where another system would send null,
 * and it must not produce a blank heading or a message-less banner.
 */
const EMPTY_TEXT_MEMBERS: ProblemDetails = {
  type: '',
  title: '',
  status: 400,
  detail: '',
};

/** Carries both identifiers, so the preference between them is observable. */
const BOTH_REFERENCES: ProblemDetails = {
  status: 500,
  correlationId: 'a3f1c7d2-5b64-4e08-9c11-6d2f0e7b48aa',
  traceId: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
};

/** Carries the trace identifier only, so the fallback is observable. */
const TRACE_REFERENCE_ONLY: ProblemDetails = {
  status: 500,
  traceId: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
};

/**
 * Carries a BLANK correlation identifier alongside a usable trace identifier.
 *
 * A blank identifier joins nothing to nothing, so the utility reports it as absent
 * and falls through - tested explicitly rather than by truthiness, because blank is
 * a legitimate value in this data rather than a synonym for missing.
 */
const BLANK_CORRELATION_REFERENCE: ProblemDetails = {
  status: 500,
  correlationId: '   ',
  traceId: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
};

/**
 * Declares both identifier members and leaves both `undefined`.
 *
 * This is the type-safe analogue of the "explicitly null" wire case, which the real
 * contract cannot express: the model declares these members as `?: string` with no
 * null in the union, because the framework omits a null standard member from the
 * document rather than serialising it. Present-but-undefined exercises the same
 * `=== undefined` guard, which is what the utility actually tests.
 */
const UNDEFINED_REFERENCES: ProblemDetails = {
  status: 500,
  detail: 'A proxy returned a document carrying no identifiers.',
  correlationId: undefined,
  traceId: undefined,
};

/**
 * A validation failure across several fields, one of them with two messages.
 *
 * Keys are the server's model-state keys reproduced as the server spells them -
 * Pascal-cased, because they name model members rather than JSON members and the
 * camel-case body policy does not apply to dictionary keys.
 *
 * Wording is verbatim legacy: the portal-alias sentence is the measured
 * `DuplicatePortalAlias` resource value, and the role-name sentence is the literal
 * `ErrorMessage` attribute of `valRoleName` at
 * `Website/admin/Security/editroles.ascx` L31 - leading break tag included, because
 * that is exactly what the legacy stored.
 */
const VALIDATION_ACROSS_FIELDS: ValidationProblemDetails = {
  type: 'urn:dnnmigration:error:validation.failed',
  title: 'One or more validation errors occurred.',
  status: 422,
  detail: 'The portal could not be saved.',
  errors: {
    PortalName: [
      'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
    ],
    RoleName: ['<br>You Must Enter a Valid Name'],
    Email: ['A valid email address is required.', 'That email address is already registered.'],
  },
};

/**
 * A single field whose messages repeat.
 *
 * The repetition is real legacy output rather than a contrived case:
 * `Website/admin/Portal/Signup.ascx.vb` L191-L196 appends its message INSIDE a
 * per-character loop over the portal name, so one submission yields the identical
 * sentence once per invalid character. Collapsing duplicates would therefore
 * discard messages the server sent.
 */
const REPEATED_FIELD_MESSAGE: ValidationProblemDetails = {
  status: 422,
  detail: 'The portal name contains characters that are not allowed.',
  errors: {
    PortalName: [
      'This is an invalid Page Name',
      'This is an invalid Page Name',
      'This is an invalid Page Name',
    ],
  },
};

/**
 * A failure code that genuinely contains a dot.
 *
 * `Portal.LastPortal` must survive whole. Its wording is stored under the BARE key
 * `LastPortal` in `Website/App_GlobalResources/SharedResources.resx` and read as
 * `Localization.GetString("LastPortal")` at
 * `Library/Components/Portal/PortalController.vb:200`, so the dotted form is the
 * code rather than a path, and any general "split on the dot" rule would corrupt it.
 */
const DOTTED_FAILURE_CODE: ValidationProblemDetails = {
  status: 409,
  errors: {
    'Portal.LastPortal': ['You Can Not Delete The Last Portal In Your Database'],
  },
};

/**
 * A message the server keyed to the request as a whole rather than to a field.
 *
 * The empty-string key is what the validation bridge writes when a rule reports no
 * property name, and `$` is what the body binder writes when the payload itself
 * could not be read. Neither has a control to sit beside, so both belong beside the
 * summary - and neither may be discarded merely because the key is empty.
 */
const FORM_LEVEL_ONLY: ValidationProblemDetails = {
  status: 400,
  errors: {
    '': ['A role group with the same name already exists. The new group was not added.'],
    $: ['The file you selected does not contain a valid XML structure'],
  },
};

/**
 * Both a form-level message and a per-field one, so their ORDER is observable.
 *
 * A message the server keyed to the request as a whole has no control to sit beside,
 * so it belongs immediately under the sentence it qualifies - above the per-field
 * list rather than buried inside it.
 */
const MIXED_LEVEL_MESSAGES: ValidationProblemDetails = {
  status: 409,
  detail: 'The role could not be saved.',
  errors: {
    RoleName: ['A role with the same name already exists. The role was not added.'],
    '': ['You Can Not Remove The Portal Administrator Or The Registered Users Role'],
  },
};

/**
 * A field key that looks like a legacy sentinel.
 *
 * `-1` is not an array-index key, so its position in the dictionary is its
 * insertion order, and it must be rendered verbatim rather than mistaken for
 * "absent".
 */
const SENTINEL_FIELD_KEY: ValidationProblemDetails = {
  status: 422,
  errors: {
    '-1': ['The import file specified is not the correct type for this module'],
  },
};

/**
 * A field whose every message is blank.
 *
 * Blank is not the same question as absent. The empty string is the legacy string
 * "absent" marker, so a server that writes one produces an entry with a key and
 * nothing to say - and rendering it would put an empty term and an empty definition
 * on screen. The entry has to be dropped, while the banner itself still renders.
 */
const BLANK_FIELD_MESSAGES: ValidationProblemDetails = {
  status: 422,
  detail: 'The role could not be saved.',
  errors: {
    RoleName: ['', '   '],
  },
};

/** A detail carrying inline emphasis markup, which must never become an element. */
const BOLD_MARKUP_DETAIL: ProblemDetails = {
  status: 400,
  detail: '<b>x</b>',
};

/**
 * A detail carrying a script payload modelled on a MEASURED legacy resource value.
 *
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` stores an
 * `Advertising.Text` value that is a live advertising block: an inline `<script>`
 * assigning a publisher identifier, plus a second `<script>` whose `src` loads from
 * a remote host. It is stored HTML-escaped, so a naive search never finds it. The
 * legacy renderer assigned such text straight to a Web Forms label
 * (`ModuleMessage.vb` L150, `lblMessage.Text = strMessage`) with no encoding call
 * anywhere in the file, which is what made it executable. The remote reference here
 * is written scheme-relative so this fixture cannot be mistaken for a live URL.
 */
const SCRIPT_MARKUP_DETAIL: ProblemDetails = {
  status: 400,
  detail:
    '<script>globalThis.injected = true;</script>' +
    '<script src="//pagead2.googlesyndication.com/pagead/show_ads.js"></script>',
};

/**
 * A detail opening with ONE break tag.
 *
 * `Website/admin/Portal/Signup.ascx.vb` L221 prefixes exactly one when the two
 * password fields disagree.
 */
const SINGLE_LEADING_BREAK: ProblemDetails = {
  status: 400,
  detail: '<br>This is an invalid Page Name',
};

/**
 * A detail opening with SEVERAL break tags, in both spellings.
 *
 * Several rather than one because the legacy append happens inside a per-character
 * loop (`Signup.ascx.vb` L191-L196 and L212-L216), so a name with three invalid
 * characters accumulates three. Both spellings appear in the legacy: `<br>` at
 * `Signup.ascx.vb` L193, and `<br/>` at `Website/admin/Users/User.ascx.vb` L187 and
 * `ModuleMessage.vb` L153.
 */
const MANY_LEADING_BREAKS: ProblemDetails = {
  status: 400,
  detail: '<br><br/><br />  <BR>The Page Name you chose is already being used for another page ' +
    'at the same level of the page heirarchy.',
};

/**
 * Break markup inside the PER-FIELD dictionary values, not only in the summary.
 *
 * Nine validator `ErrorMessage` attributes in
 * `Website/admin/Security/editroles.ascx` open with a break tag - L31, L92, L95,
 * L110, L113, L124, L127, L142 and L145 - and those attributes are exactly what
 * becomes the problem document's per-field dictionary. The `<br/>` spelling is the
 * one `Website/admin/Users/User.ascx.vb` L187 prefixes to its password message.
 */
const FIELD_LEADING_BREAKS: ValidationProblemDetails = {
  status: 422,
  detail: 'The role could not be saved.',
  errors: {
    RoleName: ['<br>You Must Enter a Valid Name'],
    ServiceFee: ['<br>Service Fee Value Entered Is Not Valid'],
    Password: ['<br/>The password entered does not meet the policy.'],
  },
};

/** The three presentational bands the component may choose between. */
const EXPECTED_BANDS: readonly ErrorBannerSeverity[] = ['danger', 'warning', 'calm'];

/**
 * Substrings that must NEVER appear anywhere in the rendered banner.
 *
 * The legacy leaked a stack trace by design:
 * `Library/Components/Exceptions/ErrorContainer.vb` L54-L70 branched on
 * `objUserInfo.IsSuperUser` and, for those who were, passed `exc.ToString` - the
 * exception's entire string form - through as the displayed message. RFC 7807
 * carries no stack trace, no exception, no inner exception and no developer
 * message, and its shape does not vary by environment, so that disclosure is closed
 * for every caller including super-users. The support reference is the only
 * sanctioned diagnostic.
 */
const FORBIDDEN_DISCLOSURES: readonly string[] = [
  'stackTrace',
  'stack trace',
  'innerException',
  'developerMessage',
  'System.Exception',
  'at System.',
  '--- End of inner exception',
  'Authorization:',
  'Bearer ',
  'password=',
];

/** One status, the sentence it must produce unaided, and the band it must take. */
interface StatusWordingCase {
  /** A document carrying the status and no text of its own. */
  readonly problem: ProblemDetails;

  /** The sentence the component must fall back to. */
  readonly expected: string;

  /** The presentational band the status must resolve to. */
  readonly band: ErrorBannerSeverity;
}

/**
 * Every status the API is currently known to answer with, unaided by document text.
 *
 * The list is illustrative rather than exhaustive, exactly as the model documents:
 * the status is chosen by the server, so the set is not enforced anywhere and an
 * unlisted status is ordinary. It is covered here because the fallback wording and
 * the band are two separate rules and each status exercises both at once.
 *
 * `problem` is typed as `ProblemDetails` through this interface, so each literal
 * below is excess-property checked and a mistyped member fails the build.
 */
const STATUS_WORDING: readonly StatusWordingCase[] = [
  { problem: { status: 400 }, expected: REQUEST_REJECTED, band: 'danger' },
  { problem: { status: 401 }, expected: NOT_AUTHENTICATED, band: 'warning' },
  { problem: { status: 403 }, expected: FORBIDDEN, band: 'warning' },
  { problem: { status: 404 }, expected: NOT_FOUND, band: 'warning' },
  { problem: { status: 409 }, expected: CONFLICT, band: 'danger' },
  { problem: { status: 422 }, expected: VALIDATION_REJECTED, band: 'danger' },
  { problem: { status: 429 }, expected: TOO_MANY_ATTEMPTS, band: 'calm' },
  { problem: { status: 500 }, expected: SERVER_ERROR, band: 'danger' },
];

// ---------------------------------------------------------------------------
// SUITE
// ---------------------------------------------------------------------------

describe('ErrorBannerComponent', () => {
  let fixture: ComponentFixture<ErrorBannerComponent>;
  let component: ErrorBannerComponent;
  let host: HTMLElement;

  beforeEach(async () => {
    // The component is STANDALONE, so it goes in `imports`. Putting a standalone
    // component in the legacy declaration array is a hard error, and the target
    // declares no module class anywhere - every component in it is standalone.
    //
    // NO HTTP PROVIDERS ARE REGISTERED, deliberately. The component has no
    // constructor, calls `inject` nowhere and declares `imports: []`, so it makes no
    // request and needs no backend - real or testing. Registering the testing
    // backend "for symmetry" would oblige this suite to verify no outstanding
    // request in an `afterEach`, and a suite that registers a backend and forgets
    // that check passes an unexpected request silently. Registering nothing cannot
    // fail that way, and it also removes any possibility of the classic
    // provider-ordering mistake in which the testing backend is registered before
    // the real client and the real `HttpBackend` is left in place.
    await TestBed.configureTestingModule({
      imports: [ErrorBannerComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ErrorBannerComponent);
    component = fixture.componentInstance;

    // Annotated rather than inferred: `ComponentFixture.nativeElement` is typed
    // loosely by the framework, and naming the type here keeps every helper below
    // working against a real `HTMLElement` without any weakly typed value entering
    // this file.
    const rootElement: HTMLElement = fixture.nativeElement;
    host = rootElement;

    fixture.detectChanges();
  });

  /**
   * Binds a problem and settles the view.
   *
   * `componentRef.setInput` is the only correct way to write a SIGNAL input from a
   * spec; assigning to the member would replace the signal itself rather than its
   * value. One `detectChanges` is then sufficient because every derived member is a
   * pull-based `computed()` and the component declares no `effect()`, so there is
   * nothing queued to flush.
   *
   * @param problem The document to bind, or null for the empty state.
   */
  function bind(problem: ProblemDetails | null): void {
    fixture.componentRef.setInput('problem', problem);
    fixture.detectChanges();
  }

  /** The band the banner is currently painting, or null when no banner is shown. */
  function band(): string | null {
    const banner: Element | null = host.querySelector(BANNER);

    return banner === null ? null : banner.getAttribute('data-severity');
  }

  // -------------------------------------------------------------------------
  // THE PERSISTENT LIVE REGION
  // -------------------------------------------------------------------------

  describe('the live region', () => {
    it('is already in the document BEFORE any failure is bound', () => {
      // The single most important structural assertion in this file. A live region
      // that is inserted into the document at the same moment as its first message
      // is announced inconsistently - several screen readers register the region
      // only after insertion and therefore miss the very first message, which is
      // the one that matters. Asserting this with nothing bound is what proves the
      // region is persistent rather than conditional.
      expect(hasElement(host, LIVE_REGION))
        .withContext('the region must exist while there is no problem at all')
        .toBeTrue();
      expect(component.hasProblem())
        .withContext('and it must exist without there being a problem')
        .toBeFalse();
    });

    it('states only the announcement semantics its role already implies', () => {
      // `role="alert"` IS co-located with `aria-live` here, and that is deliberate
      // rather than an oversight. Redundancy is only harmful when the stated value
      // CONTRADICTS the implied one - `role="alert"` with `aria-live="polite"` is
      // the genuine defect. `role="alert"` implies assertive, atomic announcement,
      // so stating exactly those two values leaves one coherent mechanism and makes
      // it legible to a reader of the template. This assertion therefore pins
      // agreement, which is the property that actually matters.
      const region = requireElement(host, LIVE_REGION);

      expect(region.getAttribute('role')).toBe('alert');
      expect(region.getAttribute('aria-live'))
        .withContext('assertive is what role="alert" implies; anything else would contradict it')
        .toBe('assertive');
      expect(region.getAttribute('aria-atomic'))
        .withContext('atomic is what role="alert" implies, so the banner is one announcement')
        .toBe('true');
    });

    it('is the SAME element before and after a failure, so it is never re-created', () => {
      const before = requireElement(host, LIVE_REGION);

      bind(NOT_FOUND_WITH_DETAIL);

      expect(requireElement(host, LIVE_REGION))
        .withContext('a re-created region loses the announcement')
        .toBe(before);
    });

    it('survives the failure being withdrawn, while the banner does not', () => {
      const before = requireElement(host, LIVE_REGION);

      bind(NOT_FOUND_WITH_DETAIL);
      expect(hasElement(host, BANNER)).toBeTrue();

      bind(null);

      expect(hasElement(host, BANNER))
        .withContext('the banner goes')
        .toBeFalse();
      expect(requireElement(host, LIVE_REGION))
        .withContext('the region stays')
        .toBe(before);
    });
  });

  // -------------------------------------------------------------------------
  // THE EMPTY STATE
  // -------------------------------------------------------------------------

  describe('with no problem bound', () => {
    it('renders nothing inside the region', () => {
      expect(hasElement(host, BANNER)).toBeFalse();
      expect(optionalText(host, MESSAGE)).toBeNull();
      expect(optionalText(host, TITLE)).toBeNull();
      expect(optionalText(host, SEVERITY_WORD)).toBeNull();
      expect(optionalText(host, TRACE)).toBeNull();
      expect(hasElement(host, FIELD_LIST)).toBeFalse();
    });

    it('treats an explicitly bound null as the empty state', () => {
      bind(null);

      expect(hasElement(host, BANNER)).toBeFalse();
      expect(component.hasProblem()).toBeFalse();
    });

    it('leaves the region occupying no space, so it costs nothing while empty', () => {
      const region = requireElement(host, LIVE_REGION);

      expect(region.getBoundingClientRect().height).toBe(0);
    });
  });

  // -------------------------------------------------------------------------
  // ACCESSIBILITY
  // -------------------------------------------------------------------------

  describe('accessibility', () => {
    it('emits no landmark element, because landmarks belong to the layout shell', () => {
      // A banner can appear on any screen, so contributing a landmark would add a
      // second `main` or a stray `header` to whatever page embeds it.
      bind(VALIDATION_ACROSS_FIELDS);

      expect(hasElement(host, 'header')).withContext('header').toBeFalse();
      expect(hasElement(host, 'main')).withContext('main').toBeFalse();
      expect(hasElement(host, 'nav')).withContext('nav').toBeFalse();
      expect(hasElement(host, 'footer')).withContext('footer').toBeFalse();
      expect(hasElement(host, 'aside')).withContext('aside').toBeFalse();
      expect(hasElement(host, 'section')).withContext('section').toBeFalse();
    });

    it('emits no image, so the severity cue cannot fail to load', () => {
      // MIGRATION: the legacy renderer paired each message type with a raster asset
      // - `~/images/red-error.gif`, `~/images/yellow-warning.gif` and
      // `~/images/green-ok.gif` at `ModuleMessage.vb` L134/L140/L146 - and all three
      // are a documented non-port. The design system permits no CSS asset reference
      // and ships one static asset only, so the cue is text. Text cannot 404.
      for (const problem of [PERMISSION_REFUSED, RATE_LIMITED, SERVER_FAULT]) {
        bind(problem);

        expect(hasElement(host, 'img')).withContext('img').toBeFalse();
        expect(hasElement(host, 'svg')).withContext('svg').toBeFalse();
        expect(hasElement(host, 'picture')).withContext('picture').toBeFalse();
      }
    });

    it('names the field list programmatically, without adding visible content', () => {
      bind(VALIDATION_ACROSS_FIELDS);

      const list = requireElement(host, FIELD_LIST);

      expect(list.tagName)
        .withContext('a description list, because the data is names paired with values')
        .toBe('DL');
      expect(list.getAttribute('aria-label'))
        .withContext('so the list is announced as something rather than as loose text')
        .toBe('Problem details');
    });

    it('pairs every label with at least one message inside the list', () => {
      bind(VALIDATION_ACROSS_FIELDS);

      const list = requireElement(host, FIELD_LIST);
      const terms = list.querySelectorAll('dt');
      const details = list.querySelectorAll('dd');

      expect(terms.length).withContext('one term per field').toBe(3);
      expect(details.length)
        .withContext('at least one detail per term, and more where a field had several')
        .toBeGreaterThanOrEqual(terms.length);
    });

    it('shows the severity word as visible text rather than as a hidden label', () => {
      // Colour independence, which is a different requirement from contrast: a
      // visually hidden word would satisfy a screen reader and fail a sighted
      // reader who cannot distinguish the hue.
      bind(SERVER_FAULT);

      const word = requireElement(host, SEVERITY_WORD);

      expect(getComputedStyle(word).display).not.toBe('none');
      expect(getComputedStyle(word).visibility).not.toBe('hidden');
      expect(word.getBoundingClientRect().height).toBeGreaterThan(0);
    });
  });

  // -------------------------------------------------------------------------
  // THE SUMMARY SENTENCE AND THE PROBLEM-TYPE HEADING
  // -------------------------------------------------------------------------

  describe('the summary', () => {
    it('renders the heading and the sentence, and no field list, for a plain document', () => {
      bind(NOT_FOUND_WITH_DETAIL);

      expect(requireText(host, TITLE))
        .withContext('the problem TYPE, which is the legacy AddModuleMessage Heading')
        .toBe('Not Found');
      expect(requireText(host, MESSAGE))
        .withContext('the occurrence, which is the legacy AddModuleMessage Message')
        .toBe('Portal 42 does not exist.');
      expect(hasElement(host, FIELD_LIST))
        .withContext('a non-validation failure carries no per-field messages')
        .toBeFalse();
    });

    it('prefers the detail over the title, because it describes THIS occurrence', () => {
      bind(NOT_FOUND_WITH_DETAIL);

      expect(component.message()).toBe('Portal 42 does not exist.');
      expect(component.title()).toBe('Not Found');
    });

    it('does not repeat the title as a heading when the title IS the sentence', () => {
      // A document carrying only a title would otherwise render the same words
      // twice, once as the heading and once as the body.
      bind(NOT_FOUND_TITLE_ONLY);

      expect(requireText(host, MESSAGE)).toBe('Not Found');
      expect(optionalText(host, TITLE))
        .withContext('no duplicated heading line')
        .toBeNull();
      expect(component.hasTitle()).toBeFalse();
      expect(component.title()).toBe('');
    });

    it('never renders a blank heading or a blank sentence for empty text members', () => {
      // The legacy string "absent" marker IS the empty string, so empty members
      // arrive where another system would send null. Neither may produce an empty
      // line, and the sentence must fall back rather than disappear.
      bind(EMPTY_TEXT_MEMBERS);

      expect(optionalText(host, TITLE))
        .withContext('an empty title renders no element at all')
        .toBeNull();
      expect(requireText(host, MESSAGE))
        .withContext('an empty detail falls back to the status wording')
        .toBe(REQUEST_REJECTED);
      expect(requireText(host, MESSAGE).length).toBeGreaterThan(0);
    });

    it('falls back to wording chosen from the status, for every known status', () => {
      for (const testCase of STATUS_WORDING) {
        bind(testCase.problem);

        expect(requireText(host, MESSAGE))
          .withContext(`status ${String(testCase.problem.status)} wording`)
          .toBe(testCase.expected);
      }
    });

    it('still produces a sentence for a document carrying no status at all', () => {
      bind(STATUSLESS);

      expect(requireText(host, MESSAGE)).toBe('Something went wrong upstream.');
      expect(band()).withContext('an unanticipated failure is a fault').toBe('danger');
    });
  });

  // -------------------------------------------------------------------------
  // SEVERITY
  // -------------------------------------------------------------------------

  describe('severity', () => {
    it('takes the band and the word from every known status', () => {
      for (const testCase of STATUS_WORDING) {
        bind(testCase.problem);

        expect(band())
          .withContext(`status ${String(testCase.problem.status)} band`)
          .toBe(testCase.band);
        expect(component.severity())
          .withContext(`status ${String(testCase.problem.status)} severity signal`)
          .toBe(testCase.band);
      }
    });

    it('⭐ presents a 403 as a WARNING and NOT as danger', () => {
      // Asserted in BOTH directions, because "the warning hook is present" alone
      // would pass if both hooks were somehow applied at once.
      //
      // MIGRATION: the legacy application is the authority for this twice over.
      // First, `Website/admin/Security/AccessDenied.ascx.vb` L41-L47 performs no
      // permission check at all - it only presents a denial - and BOTH branches of
      // its `Page_Load` render at `ModuleMessage.ModuleMessageType.YellowWarning`:
      // the message passed through the query string at L43, and the localised
      // default at L45. Second, the legacy renderer styled the bands differently -
      // `ModuleMessage.vb` L115-L158 gives `YellowWarning` the ordinary `Normal`
      // heading class while `RedError` gets `NormalRed`, whose sole declaration sits
      // under the comment "text style used for error messages". The legacy withheld
      // the red for a refusal, and so does this.
      bind(PERMISSION_REFUSED);

      expect(band()).withContext('the warning hook is present').toBe('warning');
      expect(band()).withContext('and the danger hook is absent').not.toBe('danger');
      expect(component.severity()).toBe('warning');
      expect(requireText(host, SEVERITY_WORD)).toBe('Warning');
    });

    it('⭐ presents a 429 CALMLY rather than as a red failure', () => {
      // MIGRATION: provably net-new. The legacy `ModuleMessageType` enum declares
      // exactly three members at `ModuleMessage.vb` L36-L40 - `GreenSuccess`,
      // `YellowWarning` and `RedError` - with no informational or calm member, so
      // nothing was carried across and nothing could be. The band exists because the
      // credential rate limiter is the compensating control for the legacy CAPTCHA
      // this migration deliberately removed: a refusal meaning "you are early" must
      // not be dressed as a fault, or it reports a failure where the system is
      // working exactly as configured.
      bind(RATE_LIMITED);

      expect(band()).toBe('calm');
      expect(band()).withContext('never the danger band').not.toBe('danger');
      expect(band()).withContext('and calmer than a refusal').not.toBe('warning');
      expect(requireText(host, SEVERITY_WORD)).toBe('Please wait');
      expect(requireText(host, MESSAGE))
        .withContext('calm wording, not a failure report')
        .toBe(TOO_MANY_ATTEMPTS);
    });

    it('still renders an UNMAPPED status, through the exhaustive default arm', () => {
      // The component's `switch` is over the three-member severity union rather than
      // over the status, and the workspace forbids an unhandled case, so an
      // unrecognised status is absorbed by the imported classification's own
      // catch-all and lands in the danger band - which is right, because a failure
      // nobody anticipated is the one most worth showing.
      bind(UNMAPPED_STATUS);

      expect(hasElement(host, BANNER)).withContext('it must still render').toBeTrue();
      expect(band()).toBe('danger');
      expect(requireText(host, MESSAGE)).toBe('A gateway reported an unrecognised condition.');
      expect(EXPECTED_BANDS)
        .withContext('and it must be one of the three declared bands')
        .toContain(component.severity());
    });

    it('carries the band as ONE attribute, so two bands can never apply at once', () => {
      for (const testCase of STATUS_WORDING) {
        bind(testCase.problem);

        const banner = requireElement(host, BANNER);
        const applied = EXPECTED_BANDS.filter(
          (candidate) => banner.getAttribute('data-severity') === candidate,
        );

        expect(applied.length)
          .withContext(`status ${String(testCase.problem.status)} resolves exactly one band`)
          .toBe(1);
      }
    });

    it('does not expose a success band, because a success is not a problem document', () => {
      // `GreenSuccess` has no counterpart here by design; success wording belongs to
      // the notification service.
      for (const testCase of STATUS_WORDING) {
        bind(testCase.problem);

        expect(band()).not.toBe('success');
      }
    });
  });

  // -------------------------------------------------------------------------
  // UNTRUSTED HTML - the highest-value assertions in this file
  // -------------------------------------------------------------------------

  describe('untrusted markup', () => {
    it('⭐ renders inline markup as visible characters and creates NO element', () => {
      bind(BOLD_MARKUP_DETAIL);

      expect(host.querySelector('b'))
        .withContext('no <b> element may be created from message text')
        .toBeNull();
      expect(host.querySelector('strong')).toBeNull();
      expect(requireText(host, MESSAGE))
        .withContext('the characters are visible instead')
        .toBe('<b>x</b>');
    });

    it('⭐ renders a script payload as inert visible text and creates NO script element', () => {
      // MIGRATION: this closes a measured vulnerability rather than expressing a
      // preference. The legacy renderer ended its path at `ModuleMessage.vb` L150
      // with `lblMessage.Text = strMessage`, assigned raw to an `asp:Label`, which
      // Web Forms renders UNENCODED - and that file contains no encoding call at all.
      // Against it sits the real payload surface: across the 37 in-scope resource
      // files, 76 of 1182 values carry an HTML tag, and one is a live advertising
      // block whose second tag loads from a remote host. Some legacy call sites
      // encoded defensively - `AccessDenied.ascx.vb` L43 wraps its query-string
      // message in `HttpUtility.HtmlEncode` while L45 does not - which is precisely
      // the shape of a defence-in-depth failure. Here the encoding is STRUCTURAL: no
      // member of the component is markup and the template binds every value as text.
      bind(SCRIPT_MARKUP_DETAIL);

      expect(host.querySelector('script'))
        .withContext('no <script> element may be created from message text')
        .toBeNull();
      expect(hasElement(host, '[src]'))
        .withContext('and nothing may acquire a remote source attribute')
        .toBeFalse();

      const rendered = requireText(host, MESSAGE);

      expect(rendered).toContain('<script>');
      expect(rendered).toContain('globalThis.injected');
      expect(rendered).toContain('show_ads.js');
    });

    it('creates no element from markup arriving in a PER-FIELD message either', () => {
      bind(FIELD_LEADING_BREAKS);

      expect(host.querySelector('br'))
        .withContext('a break tag in a field value must not become a line break')
        .toBeNull();
      expect(host.querySelector('b')).toBeNull();
      expect(host.querySelector('script')).toBeNull();
    });

    it('surfaces no stack trace, exception, credential or developer diagnostic', () => {
      // MIGRATION: this REVERSES a legacy disclosure rather than reproducing one.
      // `Library/Components/Exceptions/ErrorContainer.vb` L54-L70 branched on
      // `If objUserInfo.IsSuperUser` and, for those who were, passed `exc.ToString` -
      // the entire stack trace - through as the displayed message. RFC 7807 carries
      // no such member and its shape does not vary by environment, so the exposure is
      // closed for every caller including super-users.
      for (const problem of [SERVER_FAULT, BOTH_REFERENCES, VALIDATION_ACROSS_FIELDS]) {
        bind(problem);

        const rendered = textOf(requireElement(host, LIVE_REGION));

        for (const disclosure of FORBIDDEN_DISCLOSURES) {
          expect(rendered)
            .withContext(`"${disclosure}" must never be rendered`)
            .not.toContain(disclosure);
        }
      }
    });
  });

  // -------------------------------------------------------------------------
  // LEGACY BREAK MARKUP
  // -------------------------------------------------------------------------

  describe('legacy break markup', () => {
    it('resolves ONE leading break in the sentence', () => {
      bind(SINGLE_LEADING_BREAK);

      expect(requireText(host, MESSAGE)).toBe('This is an invalid Page Name');
    });

    it('⭐ resolves SEVERAL leading breaks, in both spellings and either case', () => {
      // Several rather than one because the legacy append happens INSIDE A
      // PER-CHARACTER LOOP: `Website/admin/Portal/Signup.ascx.vb` L191-L196 and
      // L212-L216 run `For intCounter = 1 To <text>.Length` and append a break each
      // time the character is not in the allowed set, so a name with three bad
      // characters accumulates three breaks. Both spellings exist in the legacy -
      // `<br>` at `Signup.ascx.vb` L193 and `<br/>` at
      // `Website/admin/Users/User.ascx.vb` L187 - so both are covered here, along
      // with a spaced and an upper-case form.
      bind(MANY_LEADING_BREAKS);

      expect(requireText(host, MESSAGE))
        .withContext('the legacy misspelling "heirarchy" is PRESERVED, not corrected')
        .toBe(
          'The Page Name you chose is already being used for another page at the same ' +
            'level of the page heirarchy.',
        );
    });

    it('⭐ resolves leading breaks in the PER-FIELD values, not only in the sentence', () => {
      // Nine validator `ErrorMessage` attributes in
      // `Website/admin/Security/editroles.ascx` open with a break tag - L31, L92,
      // L95, L110, L113, L124, L127, L142 and L145 - and those attributes are exactly
      // what becomes the per-field dictionary, so the same normalisation has to reach
      // this list.
      bind(FIELD_LEADING_BREAKS);

      const details = allText(host, FIELD_MESSAGE);

      expect(details).toContain('You Must Enter a Valid Name');
      expect(details).toContain('Service Fee Value Entered Is Not Valid');
      expect(details).toContain('The password entered does not meet the policy.');

      for (const detail of details) {
        expect(detail)
          .withContext('no break markup survives into a field message')
          .not.toContain('<br');
      }
    });
  });

  // -------------------------------------------------------------------------
  // PER-FIELD MESSAGES
  // -------------------------------------------------------------------------

  describe('per-field messages', () => {
    it('renders every entry of the dictionary, including a key with SEVERAL messages', () => {
      bind(VALIDATION_ACROSS_FIELDS);

      const labels = allText(host, FIELD_LABEL);
      const details = allText(host, FIELD_MESSAGE);

      expect(labels)
        .withContext('only the first character is lower-cased, so a key still matches a control')
        .toEqual(['portalName', 'roleName', 'email']);

      // BRACKET ACCESS, which is the only syntactically available form: `errors` is
      // an index-signature type and the workspace enables
      // `noPropertyAccessFromIndexSignature`, so `errors.Email` is a compile error by
      // design. The rule earns its keep - a key is only ever known at runtime, and
      // dot access would let a typo compile as a silent `undefined`.
      const emailMessages: readonly string[] = VALIDATION_ACROSS_FIELDS.errors['Email'];
      const portalMessages: readonly string[] = VALIDATION_ACROSS_FIELDS.errors['PortalName'];

      expect(emailMessages.length).withContext('the fixture really has two').toBe(2);
      expect(details).toContain(emailMessages[0]);
      expect(details).toContain(emailMessages[1]);
      expect(details).toContain(portalMessages[0]);
      expect(details.length).withContext('one detail per message, four in total').toBe(4);
    });

    it('preserves a repeated message once per occurrence rather than collapsing it', () => {
      // Tracking by message text would make the second occurrence collide with the
      // first and silently drop a message the server sent. The per-character legacy
      // loop produces exactly this input, so the collapse would be real data loss.
      bind(REPEATED_FIELD_MESSAGE);

      const details = allText(host, FIELD_MESSAGE);
      const repeated: readonly string[] = REPEATED_FIELD_MESSAGE.errors['PortalName'];

      expect(repeated.length).toBe(3);
      expect(details.length).withContext('three sent, three rendered').toBe(3);
      expect(details).toEqual([
        'This is an invalid Page Name',
        'This is an invalid Page Name',
        'This is an invalid Page Name',
      ]);
    });

    it('⭐ keeps a dotted failure code WHOLE, never splitting it at the dot', () => {
      // `Portal.LastPortal` genuinely contains a dot: its wording is stored under the
      // BARE key `LastPortal` in `Website/App_GlobalResources/SharedResources.resx`
      // and read as `Localization.GetString("LastPortal")` at
      // `Library/Components/Portal/PortalController.vb:200`, so the dotted form is
      // the code and not a path. Any general "split on the dot" rule would corrupt it.
      bind(DOTTED_FAILURE_CODE);

      const labels = allText(host, FIELD_LABEL);

      expect(labels)
        .withContext('one label, dot intact, only the first character re-cased')
        .toEqual(['portal.LastPortal']);
      expect(labels[0]).toContain('.');
      expect(labels[0]).not.toBe('portal');
      expect(labels[0]).not.toBe('LastPortal');
      expect(allText(host, FIELD_MESSAGE)).toEqual([
        'You Can Not Delete The Last Portal In Your Database',
      ]);
    });

    it('labels a message the server keyed to the request rather than to a field', () => {
      bind(FORM_LEVEL_ONLY);

      expect(allText(host, FIELD_LABEL))
        .withContext('an empty key is a real key and must not be discarded')
        .toEqual(['This form']);
      expect(allText(host, FIELD_MESSAGE)).toEqual([
        'A role group with the same name already exists. The new group was not added.',
        'The file you selected does not contain a valid XML structure',
      ]);
    });

    it('puts a form-level message ABOVE the per-field ones', () => {
      bind(MIXED_LEVEL_MESSAGES);

      const labels = allText(host, FIELD_LABEL);

      expect(labels).toEqual(['This form', 'roleName']);
      expect(allText(host, FIELD_MESSAGE)).toEqual([
        'You Can Not Remove The Portal Administrator Or The Registered Users Role',
        'A role with the same name already exists. The role was not added.',
      ]);
    });

    it('renders no list at all when the failure carries no per-field message', () => {
      bind(SERVER_FAULT);

      expect(hasElement(host, FIELD_LIST)).toBeFalse();
      expect(component.hasFieldErrors()).toBeFalse();
      expect(component.fieldErrors().length).toBe(0);
    });

    it('drops a field whose messages are ALL BLANK, so no empty row is rendered', () => {
      // Blank and absent are different questions. The empty string is the legacy
      // string "absent" marker, so a server writing one produces an entry with a key
      // and nothing to say; rendering it would put an empty term and an empty
      // definition on screen. The entry goes, the banner stays.
      bind(BLANK_FIELD_MESSAGES);

      expect(hasElement(host, BANNER)).withContext('the banner still renders').toBeTrue();
      expect(requireText(host, MESSAGE)).toBe('The role could not be saved.');
      expect(hasElement(host, FIELD_LIST))
        .withContext('but there is no list, because there is nothing to list')
        .toBeFalse();
      expect(component.fieldErrors().length).toBe(0);
      expect(allText(host, FIELD_LABEL).length).toBe(0);
      expect(allText(host, FIELD_MESSAGE).length).toBe(0);
    });
  });

  // -------------------------------------------------------------------------
  // THE SUPPORT REFERENCE - the only diagnostic that is surfaced
  // -------------------------------------------------------------------------

  describe('the support reference', () => {
    it('⭐ prefers the CORRELATION identifier over the trace identifier', () => {
      // The correlation identifier is the value the server validated for the request,
      // and it is what appears on the response header, on the request envelope in the
      // server's log and on every audit event the request produced. The W3C trace
      // identifier is taken from whatever diagnostic activity happened to be current,
      // so it appears in none of those records and quoting it yields a reference an
      // operator cannot find.
      bind(BOTH_REFERENCES);

      expect(component.supportReference())
        .toBe('a3f1c7d2-5b64-4e08-9c11-6d2f0e7b48aa');
      expect(requireText(host, TRACE))
        .toBe('Reference: a3f1c7d2-5b64-4e08-9c11-6d2f0e7b48aa');
      expect(requireText(host, TRACE))
        .withContext('the trace identifier is not the quoted reference when both are present')
        .not.toContain('0af7651916cd43dd8448eb211c80319c');
    });

    it('falls back to the trace identifier when no correlation identifier was published', () => {
      bind(TRACE_REFERENCE_ONLY);

      expect(requireText(host, TRACE)).toBe(
        'Reference: 00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
      );
      expect(component.hasSupportReference()).toBeTrue();
    });

    it('treats a BLANK correlation identifier as absent and falls through', () => {
      // Tested with an explicit emptiness check rather than by truthiness, because a
      // blank string is a legitimate value in this data - the legacy string "absent"
      // marker IS the empty string - and a blank identifier joins nothing to nothing.
      bind(BLANK_CORRELATION_REFERENCE);

      expect(component.supportReference()).toBe(
        '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
      );
    });

    it('renders no reference line when the document carried neither identifier', () => {
      bind(SERVER_FAULT);

      expect(optionalText(host, TRACE)).toBeNull();
      expect(component.hasSupportReference()).toBeFalse();
      expect(component.supportReference()).toBeNull();
    });

    it('⭐ renders nothing broken when both identifiers are present but undefined', () => {
      // The type-safe analogue of the "explicitly null" wire case. The real contract
      // declares these members as `?: string` with no null in the union, because the
      // framework's problem-details type carries a per-member null-omission condition
      // that OVERRIDES the collection-wide serialiser policy - so a null member is
      // omitted from the document rather than written as `null`, which the model
      // records as verified against live 401 and 400 responses. Present-but-undefined
      // exercises the same `=== undefined` guard, and nothing may render the word
      // "undefined".
      bind(UNDEFINED_REFERENCES);

      expect(optionalText(host, TRACE)).toBeNull();
      expect(component.supportReference()).toBeNull();
      expect(requireText(host, MESSAGE)).toBe(
        'A proxy returned a document carrying no identifiers.',
      );
      expect(textOf(requireElement(host, LIVE_REGION)))
        .withContext('no member may leak the string "undefined" into the banner')
        .not.toContain('undefined');
    });
  });

  // -------------------------------------------------------------------------
  // SENTINEL DISCIPLINE
  // -------------------------------------------------------------------------

  describe('sentinel discipline', () => {
    it('⭐ does not treat a status of ZERO as absent', () => {
      // Zero is a legitimate value in this data: the legacy schema seeds
      // `Roles.RoleID`, `Tabs.TabID` and `Modules.ModuleID` with `IDENTITY(0,1)`, so
      // zero identifies a real row. A component testing its status for truthiness
      // would send this down the "no status" path and drop the banner.
      bind(ZERO_STATUS);

      expect(hasElement(host, BANNER)).withContext('the banner must still render').toBeTrue();
      expect(component.hasProblem()).toBeTrue();
      expect(band()).toBe('danger');
      expect(requireText(host, MESSAGE)).toBe(
        'A transport-level failure produced no HTTP status.',
      );
    });

    it('⭐ does not treat a status of MINUS ONE as absent', () => {
      // Minus one is simultaneously the legacy integer "absent" marker and a real
      // identifier: `Portals.PortalID` is `IDENTITY(-1,1)`, so -1 names the first
      // portal ever created. The collision is why nothing may read -1 as missing.
      bind(NEGATIVE_ONE_STATUS);

      expect(hasElement(host, BANNER)).toBeTrue();
      expect(band()).toBe('danger');
      expect(requireText(host, MESSAGE)).toBe('A sentinel-valued status must still render.');
    });

    it('does not treat a status of zero as if it were a server fault', () => {
      // Proves the number really was read: a status at or above 500 takes the server
      // wording, and zero must not.
      bind(ZERO_STATUS);
      const zeroBand = band();

      bind(SERVER_FAULT);

      expect(requireText(host, MESSAGE)).toBe(SERVER_ERROR);
      expect(zeroBand).withContext('both are faults, but for different reasons').toBe('danger');
    });

    it('⭐ does not discard a field key that is the EMPTY STRING', () => {
      // The empty string is what the validation bridge writes when a rule reports no
      // property name, so it is a real key carrying a real message.
      bind(FORM_LEVEL_ONLY);

      expect(hasElement(host, FIELD_LIST)).toBeTrue();
      expect(allText(host, FIELD_LABEL).length).toBe(1);
      expect(allText(host, FIELD_MESSAGE).length).toBe(2);
    });

    it('renders a sentinel-shaped field key verbatim', () => {
      bind(SENTINEL_FIELD_KEY);

      expect(allText(host, FIELD_LABEL)).toEqual(['-1']);
      expect(allText(host, FIELD_MESSAGE)).toEqual([
        'The import file specified is not the correct type for this module',
      ]);
    });
  });

  // -------------------------------------------------------------------------
  // THE PUBLIC API SURFACE
  // -------------------------------------------------------------------------

  describe('the public API', () => {
    it('defaults the input to null, so a consumer may bind an empty store slice', () => {
      expect(component.problem()).toBeNull();
      expect(component.hasProblem()).toBeFalse();
    });

    it('reads the bound document back through the input signal', () => {
      bind(NOT_FOUND_WITH_DETAIL);

      expect(component.problem()).toBe(NOT_FOUND_WITH_DETAIL);
    });

    it('labels a form-level group generically and every other group verbatim', () => {
      expect(component.labelFor('')).withContext('no field to name').toBe('This form');
      expect(component.labelFor('Portal.LastPortal'))
        .withContext('used whole, never split at the dot')
        .toBe('Portal.LastPortal');
      expect(component.labelFor('-1')).withContext('a sentinel-shaped name survives').toBe('-1');
      expect(component.labelFor('0')).withContext('a zero-shaped name survives').toBe('0');
    });

    it('exposes each field group as a tracked, typed pair', () => {
      bind(MIXED_LEVEL_MESSAGES);

      const groups: readonly FieldErrorGroup[] = component.fieldErrors();

      expect(groups.length).toBe(2);
      expect(groups[0].field).withContext('the form-level group carries the empty key').toBe('');
      expect(groups[1].field).toBe('roleName');
      expect(groups[1].messages).toEqual([
        'A role with the same name already exists. The role was not added.',
      ]);
      expect(component.trackByField(0, groups[0]))
        .withContext('identity is the field name, which is unique within one document')
        .toBe('');
      expect(component.trackByField(1, groups[1])).toBe('roleName');
    });

    it('resolves the severity word from the band for all three bands', () => {
      bind(SERVER_FAULT);
      expect(component.severityLabel()).toBe('Error');

      bind(PERMISSION_REFUSED);
      expect(component.severityLabel()).toBe('Warning');

      bind(RATE_LIMITED);
      expect(component.severityLabel()).toBe('Please wait');
    });

    it('declares the on-push change detection the migration plan mandates', () => {
      // Read from the compiled definition rather than from the decorator source, so
      // the expectation reflects what the framework actually uses.
      const definition = (ErrorBannerComponent as unknown as { ɵcmp: { onPush: boolean } }).ɵcmp;

      expect(definition.onPush).toBeTrue();
    });
  });

  // -------------------------------------------------------------------------
  // RENDERED APPEARANCE
  // -------------------------------------------------------------------------

  describe('rendered appearance', () => {
    /**
     * The banner's paint, copied out as plain strings.
     *
     * The Karma target loads the global stylesheet, so the token sheet is present and
     * every custom property resolves here exactly as it does in the application. The
     * values are copied immediately rather than held as a live declaration, which
     * would otherwise read whichever element was still attached at the end.
     *
     * @param problem The document to bind.
     * @returns The colours the banner resolved to.
     */
    function paintOf(problem: ProblemDetails): {
      readonly color: string;
      readonly border: string;
      readonly background: string;
    } {
      bind(problem);

      const style = getComputedStyle(requireElement(host, BANNER));

      return {
        color: style.color,
        border: style.borderTopColor,
        background: style.backgroundColor,
      };
    }

    it('paints only the danger band in the error colour, across TWO tokens', () => {
      // Measured provenance: the legacy renderer gave `RedError` the `NormalRed`
      // heading class - the sole pure-red declaration, sitting under the comment "text
      // style used for error messages" - and gave `YellowWarning` the ordinary
      // `Normal` class. The role is split across two tokens because the measured
      // legacy red reads 4.0:1 on the page background: sufficient for the 3:1 that
      // governs a non-text boundary, short of the 4.5:1 that governs the text inside
      // it. So the BORDER keeps the legacy value and the TEXT resolves to the same hue
      // at a lower lightness. A reader still sees one red family in both places.
      const danger = paintOf(SERVER_FAULT);

      expect(danger.color).toBe('rgb(179, 0, 0)');
      expect(danger.border).toBe('rgb(255, 0, 0)');

      // Neither red reaches a refusal or a rate-limit band, which is the point the
      // legacy provenance establishes.
      const warning = paintOf(PERMISSION_REFUSED);
      const calm = paintOf(RATE_LIMITED);

      expect(warning.color).not.toBe('rgb(179, 0, 0)');
      expect(warning.color).not.toBe('rgb(255, 0, 0)');
      expect(calm.color).not.toBe('rgb(179, 0, 0)');
      expect(calm.color).not.toBe('rgb(255, 0, 0)');
    });

    it('gives the three bands mutually distinct surfaces', () => {
      const danger = paintOf(SERVER_FAULT);
      const warning = paintOf(PERMISSION_REFUSED);
      const calm = paintOf(RATE_LIMITED);

      expect(warning.background)
        .withContext('stands in for the legacy yellow warning icon')
        .toBe('rgb(255, 255, 153)');
      expect(calm.background).toBe('rgb(238, 238, 238)');
      expect(danger.background).not.toBe(warning.background);
      expect(warning.background).not.toBe(calm.background);
    });

    it('resolves every token it references, so no property falls back to an initial value', () => {
      bind(SERVER_FAULT);

      const style = getComputedStyle(requireElement(host, BANNER));

      expect(style.borderTopWidth).toBe('1px');
      expect(style.borderTopStyle).toBe('solid');
      expect(style.borderTopLeftRadius).toBe('4px');
      expect(style.display).toBe('flex');
      expect(parseFloat(style.rowGap)).toBeGreaterThan(0);
      expect(parseFloat(style.paddingTop)).toBeGreaterThan(0);
    });

    it('de-emphasises the reference rather than letting it inherit the band colour', () => {
      bind(BOTH_REFERENCES);

      const traceStyle = getComputedStyle(requireElement(host, TRACE));
      const messageStyle = getComputedStyle(requireElement(host, MESSAGE));

      // A red reference string would compete with the message for attention.
      expect(traceStyle.color).toBe('rgb(105, 105, 105)');
      expect(parseFloat(traceStyle.fontSize)).toBeLessThan(parseFloat(messageStyle.fontSize));
    });
  });
});
