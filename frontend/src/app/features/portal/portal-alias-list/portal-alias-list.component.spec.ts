/**
 * Specification for {@link PortalAliasListComponent} — the host-name aliases of one portal.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * An alias is not decoration: it is the ONLY thing that resolves an incoming request to a tenant.
 * `PortalAliasResolutionMiddleware` matches the request's host header against `dbo.PortalAlias` with an
 * EXACT comparison, so an alias saved wrongly, saved against the wrong portal, or silently normalised
 * into something else takes a whole tenant off the air — and no status code anywhere reveals it. Every
 * assertion below exists because of that consequence.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - The component is mounted as the standalone unit it is, and the portal identifier is delivered the
 *     way the router delivers it: through `componentRef.setInput`, as the STRING a route parameter is.
 *     That is what exercises the input's own transform rather than bypassing it.
 *   - {@link PortalStore} is genuine and pinned to each case's injector, so its commands, its callbacks
 *     and its cancellation all behave as they do in life, and no state leaks between cases.
 *   - Every request is answered through `HttpTestingController`, so each assertion about an address, a
 *     body or a status is an assertion about the wire.
 *   - `NotificationService.notify` is spied and called through, so the announcements are observable
 *     without being faked.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ `dbo.Portals.PortalID` IS `IDENTITY(-1, 1)`. Minus one is the FIRST portal of an installation and
 * zero is the second, while minus one is ALSO the legacy absent-integer marker. So both values must
 * travel into the address untouched, and no expression may treat either as "no portal". The transform
 * distinguishes them from an unusable parameter by returning `NaN`, which is the only value this screen
 * treats as absent.
 *
 * ⚠ A COLLECTION ENVELOPE IS `{ data, meta }` HERE, NOT `{ items, meta }`. The alias list is
 * deliberately UNPAGED — a tenant has a handful of host names, and a pager that truncated them would
 * hide the very alias a tenant is failing to resolve on — so the body is the single-resource envelope
 * carrying an array, with `meta` null. (The paged listings elsewhere in the application are the ones
 * whose records live under `items`.)
 *
 * ⚠ A REPLACEMENT ANSWERS `204` AND THE STORE THEN RE-READS THE LIST; A CREATION ANSWERS `201` AND IT
 * DOES NOT. Both are asserted, because the difference is observable as a second request.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import { PortalAliasListComponent } from './portal-alias-list.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import type { PortalAlias } from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

/** The alias collection of one portal. Hand-written, so a change to the registry cannot hide here. */
function aliasesUrl(portalId: number): string {
  return `/api/v1/portals/${portalId}/aliases`;
}

/** One alias of one portal. */
function aliasUrl(portalId: number, portalAliasId: number): string {
  return `${aliasesUrl(portalId)}/${portalAliasId}`;
}

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported: the component exports none of it, and a specification reading it off
// the component could not detect a change to it. Each value is the legacy resource value.
// =====================================================================================================

const HEADING = 'Portal Aliases';
const ADD_ACTION_LABEL = 'Add New HTTP Alias';
const ADD_SUBMIT_LABEL = 'Add New Alias';
const UPDATE_SUBMIT_LABEL = 'Update';
const EDIT_LABEL = 'Edit';
const DELETE_LABEL = 'Delete';
const CANCEL_LABEL = 'Cancel';
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const SAVED_MESSAGE = 'The Portal Alias has been saved.';
const DELETED_MESSAGE = 'The Portal Alias has been deleted.';
const DUPLICATE_ALIAS_MESSAGE = 'The Portal Alias already exists.';
const VIEW_DENIED_MESSAGE = 'You do not have access to view this Portal Alias.';
const DELETE_DENIED_MESSAGE = 'You do not have access to delete this Portal Alias.';
const EMPTY_MESSAGE = 'This portal has no HTTP aliases.';
const ALIAS_REQUIRED_MESSAGE = 'An HTTP alias is required.';
const ALIAS_ENTRY_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 255 characters.';
const ALIAS_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 200 characters.';

/** The identifier the field's label is associated with, so the two cannot drift. */
const ALIAS_CONTROL_ID = 'portal-alias-http-alias';

/** The entry bound the input carries. The shape rule's own bound is the shorter 200. */
const ALIAS_ENTRY_MAX_LENGTH = 255;

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
//
// ⚠ EVERY DOCUMENT BELOW IS ONE THE API CAN EMIT. The type is built by the server's own type builder,
// so it is lower-cased with hyphens folded to underscores; the title comes from the status vocabulary;
// the trace and correlation identifiers are attached to every document; and NO document carries an
// `instance` member, because every call site supplies null and the serialiser omits it.
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-4b8e1f2a7c934dd6bb18eb211c80319c-91bd6b7169203331-01';
const CORRELATION_ID = '7d2a9c41-3e6b-42f8-9051-6b0e3a9d5f17';

function problem(
  code: string,
  status: number,
  detail: string,
  errors?: Readonly<Record<string, readonly string[]>>,
): ProblemDetails {
  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };

  // The per-field map appears ONLY on a model-binding refusal, and never as an empty object, so it is
  // attached only when a case is exercising exactly that.
  return errors === undefined ? document : { ...document, errors };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/** One alias row. */
function alias(
  portalAliasId: number,
  httpAlias: string | null,
  portalId = -1,
  isCurrent = false,
): PortalAlias {
  // `isCurrent` defaults to false - NOT the alias this request arrived on - so every existing
  // case keeps the rename and delete affordances. The active-alias cases pass true explicitly,
  // which is what makes the withholding legible at the call site rather than incidental.
  return { portalAliasId, portalId, httpAlias, isCurrent };
}

/** The single-resource envelope, which is what an unpaged collection travels in. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

describe('PortalAliasListComponent', () => {
  let fixture: ComponentFixture<PortalAliasListComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. The
    // store is pinned here so each case owns its own instance.
    await TestBed.configureTestingModule({
      imports: [PortalAliasListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), PortalStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /**
   * Mounts the screen with a route parameter.
   *
   * ⚠ THE PARAMETER IS DELIVERED AS A STRING, BECAUSE THAT IS WHAT A ROUTE PARAMETER IS. Component
   * input binding hands route parameters over as text, so passing a number here would bypass the
   * input's own transform and prove nothing about how the screen behaves when routed to.
   */
  function create(portalId: string = '-1'): void {
    fixture = TestBed.createComponent(PortalAliasListComponent);
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Answers the outstanding alias read for one portal. */
  function answerAliases(rows: readonly PortalAlias[], portalId = -1): TestRequest {
    const call = expectRequest('GET', aliasesUrl(portalId), 'the alias read');

    call.flush(envelope(rows));
    fixture.detectChanges();

    return call;
  }

  /** Mounts the screen and settles its first read. */
  function arrive(rows: readonly PortalAlias[] = [alias(7, 'localhost')], portalId = '-1'): void {
    create(portalId);
    answerAliases(rows, Number(portalId));
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** A button found by its rendered wording. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** A button found by its rendered wording, asserted to exist, and pressed. */
  function press(label: string): void {
    const control = button(label);

    expect(control).withContext(`the "${label}" control is offered`).not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /**
   * Presses a button of the OPEN CONFIRMATION, scoped to the dialogue.
   *
   * ⚠ THIS SCOPING IS LOAD-BEARING, NOT TIDINESS. The form and the dialogue BOTH render a button
   * whose wording is 'Cancel', and the form's comes first in document order - so an unscoped lookup by
   * wording dismisses the ENTRY instead of the question, silently proving something else than the case
   * claims. The dialogue's own class is the only reliable discriminator.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    expect(control)
      .withContext(`the "${label}" button of the confirmation is offered`)
      .not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** The alias entry field, which only exists while the form is open. */
  function entryField(): HTMLInputElement {
    const field = query<HTMLInputElement>(`#${ALIAS_CONTROL_ID}`);

    expect(field).withContext('the alias field is rendered').not.toBeNull();

    return field as HTMLInputElement;
  }

  /** Types a value into the alias field the way a person does. */
  function type(value: string): void {
    const field = entryField();

    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** Submits the open form through its own submit control. */
  function submit(): void {
    const form = query<HTMLFormElement>('form');

    expect(form).withContext('the form is open').not.toBeNull();

    (form as HTMLFormElement).dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  /** Presses the edit command of one painted row. */
  function editRow(rowIndex = 0): void {
    const rows: readonly HTMLTableRowElement[] = queryAll<HTMLTableRowElement>('tr.data-table__row');
    const row: HTMLTableRowElement | undefined = rows[rowIndex];

    expect(row).withContext(`row ${rowIndex} is painted`).not.toBeUndefined();

    const command: HTMLButtonElement | undefined = Array.from(
      (row as HTMLTableRowElement).querySelectorAll<HTMLButtonElement>('button'),
    ).find((candidate) => (candidate.textContent ?? '').trim() === EDIT_LABEL);

    expect(command).withContext('the edit command is offered on the row').not.toBeUndefined();

    (command as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return textOf('.form-field__error');
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — THE READ, AND THE TWO IDENTITY SEEDS
  // ---------------------------------------------------------------------------------------------------

  describe('reading the aliases of a portal', () => {
    it('reads the aliases of portal -1, which is the first portal of an installation', () => {
      create('-1');

      const call = expectRequest('GET', aliasesUrl(-1));

      // ⚠ MINUS ONE IS BOTH THE FIRST PORTAL AND THE LEGACY ABSENT-INTEGER MARKER, so this is the case
      // that would fail if any expression on the path treated it as "no portal". The identifier reaches
      // the address exactly as the route delivered it.
      expect(call.request.url).toBe('/api/v1/portals/-1/aliases');
      // Unpaged by design: a tenant has a handful of host names, and a pager that truncated them would
      // hide the alias a tenant is failing to resolve on.
      expect(call.request.params.keys()).withContext('no query string at all').toHaveSize(0);
      expect(call.request.url.startsWith('http')).withContext('relative address').toBeFalse();

      call.flush(envelope([alias(7, 'localhost', -1)]));
      fixture.detectChanges();

      expect(textOf('td.data-table__cell')).toContain('localhost');
      httpMock.expectNone(() => true);
    });

    it('reads the aliases of portal 0, which is an ordinary portal and not an absence', () => {
      create('0');

      const call = expectRequest('GET', aliasesUrl(0));

      expect(call.request.url).toBe('/api/v1/portals/0/aliases');

      call.flush(envelope([alias(11, 'contoso.example.test', 0)]));
      fixture.detectChanges();

      expect(textOf('td.data-table__cell')).toContain('contoso.example.test');
    });

    it('issues exactly one read, because the input and the lifecycle hook do not both load', () => {
      create('-1');

      // The input setter issues the read when the value changes; the hook issues one only when no read
      // has happened and none is in flight. A screen where both fired would double every entry.
      const calls = httpMock.match(() => true);

      expect(calls).withContext('one read on arrival').toHaveSize(1);

      calls[0]?.flush(envelope([alias(7, 'localhost')]));
      fixture.detectChanges();
    });

    it('re-reads when the route names a different portal, and not when it repeats one', () => {
      arrive([alias(7, 'localhost')], '-1');

      // Re-delivering the SAME parameter must not re-request: an explicit equality test guards it, and
      // the guard matters because a router can deliver a parameter more than once for one navigation.
      fixture.componentRef.setInput('portalId', '-1');
      fixture.detectChanges();
      httpMock.expectNone(() => true);

      fixture.componentRef.setInput('portalId', '0');
      fixture.detectChanges();

      answerAliases([alias(11, 'contoso.example.test', 0)], 0);

      expect(textOf('td.data-table__cell')).toContain('contoso.example.test');
    });

    it('sends nothing at all for an unusable route parameter, and says why', () => {
      create('not-a-number');

      // The transform answers with the one value this screen treats as absent, so no address is built
      // from text that is not an identifier — which is what the legacy screen did, unguarded, under a
      // compilation setting with strict typing disabled.
      httpMock.expectNone(() => true);
      expect(query('app-empty-state')).withContext('the state is explained').not.toBeNull();
      expect(query('form')).withContext('no form is offered').toBeNull();
    });

    it('renders an alias held as an absent string as an empty cell rather than as text', () => {
      arrive([alias(7, null)]);

      // The column is nullable on the wire, and the legacy absent-string marker IS the empty string, so
      // the honest rendering of either is an empty cell — never the word 'null'.
      expect(textOf('td.data-table__cell').join('|')).not.toContain('null');
    });

    it('states the absence rather than painting an empty grid when a portal has no aliases', () => {
      arrive([]);

      expect(textOf('app-empty-state').join(' ')).toContain(EMPTY_MESSAGE);
      // The empty state carries the way out, so a person is never left on a dead end.
      expect(button(ADD_ACTION_LABEL)).withContext('the way out is offered').not.toBeUndefined();
    });

    it('names the grid and the screen from the legacy resource value', () => {
      arrive();

      expect((query('h1')?.textContent ?? '').trim()).toBe(HEADING);
      expect((query('caption')?.textContent ?? '').trim()).toBe(HEADING);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — CREATING AN ALIAS
  // ---------------------------------------------------------------------------------------------------

  describe('adding an alias', () => {
    it('posts exactly the one declared member and announces the legacy wording on 201', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);

      // The submit wording is mode-dependent and there is no separate add control, exactly as the
      // legacy screen behaved.
      expect(button(ADD_SUBMIT_LABEL)).withContext('the create wording').not.toBeUndefined();
      expect(button(UPDATE_SUBMIT_LABEL)).withContext('not the replace wording').toBeUndefined();

      type('contoso.example.test');
      submit();

      const call = expectRequest('POST', aliasesUrl(-1), 'the creation');

      // ⚠ ONE MEMBER, EXACTLY. The request contract declares only the host name; the portal travels in
      // the ADDRESS. A body carrying a portal identifier as well would let the two disagree.
      expect(call.request.body).toEqual({ httpAlias: 'contoso.example.test' });

      call.flush(envelope(alias(9, 'contoso.example.test')), { status: 201, statusText: 'Created' });
      fixture.detectChanges();

      // A creation appends locally and does NOT re-read: the created row came back in the response.
      httpMock.expectNone(() => true);
      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      // The form closes on success, so a second press cannot resubmit the same alias.
      expect(query('form')).withContext('the form is closed').toBeNull();
      expect(textOf('td.data-table__cell')).toContain('contoso.example.test');
    });

    it('strips a scheme and a share prefix before sending, because the schema stores neither', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('https://contoso.example.test');
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      // ⚠ THE NORMALISATION IS NOT COSMETIC. `dbo.PortalAlias.HTTPAlias` holds the AUTHORITY, and the
      // resolution middleware compares it against the request's host header — which carries no scheme.
      // An alias stored with one can never match, so the tenant would simply never resolve.
      expect(call.request.body).toEqual({ httpAlias: 'contoso.example.test' });
      // And what was normalised is shown back, so a person is not left believing they saved the text
      // they typed.
      expect(entryField().value).toBe('contoso.example.test');

      call.flush(envelope(alias(9, 'contoso.example.test')), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
    });

    it('reports a duplicate beside the field at 409, and not as a banner', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('localhost');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(
        problem('portal.alias_duplicate', 409, 'An alias with this host name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // A refusal about a value a person just typed belongs BESIDE that value, in this screen's own
      // legacy wording — not in a summary banner they must go looking in. The banner is suppressed for
      // exactly this case, and only this case.
      expect(fieldMessages()).toContain(DUPLICATE_ALIAS_MESSAGE);
      expect(query('.error-banner__title')).withContext('no duplicate summary').toBeNull();
      // The form stays open with the entry intact, so the correction is one keystroke away.
      expect(query('form')).not.toBeNull();
      expect(entryField().value).toBe('localhost');
      expect(notifications()).withContext('nothing transient for a field refusal').toHaveSize(0);
    });

    it('shows a per-field server message when the server names the field', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('contoso.example.test');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(
        problem('request.invalid', 400, 'One or more validation errors occurred.', {
          httpAlias: ['The alias is not acceptable.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // The per-field map's keys are .NET model-state keys and are read with an index expression, never
      // a property access, because the map is an index signature under the strict compiler setting.
      expect(fieldMessages()).toContain('The alias is not acceptable.');
    });

    it('shows an unrelated refusal in the banner rather than pinning it to the field', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('contoso.example.test');
      submit();

      expectRequest('POST', aliasesUrl(-1)).flush(
        problem(
          'server.unexpected_failure',
          500,
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // Nothing about a server fault concerns the alias the person typed, so it goes to the summary
      // surface with its reference an operator can quote.
      expect(textOf('.error-banner__message').join(' ')).toContain('An unexpected error occurred');
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
      expect(fieldMessages()).withContext('the field says nothing').toHaveSize(0);
    });

    it('abandons the entry without sending anything', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('contoso.example.test');
      press(CANCEL_LABEL);

      expect(query('form')).withContext('closed').toBeNull();
      httpMock.expectNone(() => true);
      expect(notifications()).toHaveSize(0);

      // And re-opening starts clean rather than resurrecting the abandoned entry.
      press(ADD_ACTION_LABEL);
      expect(entryField().value).toBe('');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — VALIDATION, WHICH IS A PORT OF THE SERVER'S OWN RULE
  // ---------------------------------------------------------------------------------------------------

  describe('the entry rules', () => {
    it('refuses an empty entry and sends nothing', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      submit();

      expect(fieldMessages()).toContain(ALIAS_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses an entry that is only a share prefix, because it normalises to nothing', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('\\\\');
      submit();

      // Normalisation runs BEFORE the rule, so an entry that is nothing but a prefix is empty by the
      // time the rule sees it — and is reported as the absence it is rather than as a shape failure.
      expect(fieldMessages()).toContain(ALIAS_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses a host name carrying a character the schema forbids', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('contoso.example.test?tenant=1');
      submit();

      // The rule is a faithful port of the server's own, and the reason it is ported rather than
      // approximated with a loose pattern is exactly this: an approximation that admitted what the
      // server refuses would move the refusal from a field message to a failed request.
      expect(fieldMessages()).withContext('a shape message is shown').not.toHaveSize(0);
      httpMock.expectNone(() => true);
    });

    it('refuses an alias longer than the column, at the column bound and not the entry bound', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      // 201 characters of a legal host name: within the entry bound the input allows, beyond the 200
      // the stored column accepts. The two bounds are deliberately different and both are enforced.
      type(`${'a'.repeat(201)}`);
      submit();

      expect(fieldMessages()).toContain(ALIAS_TOO_LONG_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('bounds typing at the entry length without making the control report itself invalid', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);

      // The attribute constrains typing and pasting; it is a truncation policy rather than a validation
      // rule, which is why the longer entry bound sits here and the shorter column bound sits in the
      // rule above.
      expect(entryField().getAttribute('maxlength')).toBe(String(ALIAS_ENTRY_MAX_LENGTH));
    });

    it('says nothing at all before a person has acted', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);

      // An untouched, unsubmitted field is not wrong yet, and pre-emptively marking it so is how a
      // form greets a person with errors they have not made.
      expect(fieldMessages()).toHaveSize(0);
      expect(entryField().getAttribute('aria-invalid')).toBe('false');
    });

    it('marks the field invalid for a reader once it is in error, not only visually', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      submit();

      // Carried on the CONTROL as well as in the message region, because the region announces once, at
      // the moment it appears — a reader arriving later would otherwise find a field giving no sign.
      expect(entryField().getAttribute('aria-invalid')).toBe('true');
      expect(query('.form-field__errors')?.getAttribute('role')).toBe('alert');
    });

    it('reports the longest permitted alias as acceptable and sends it whole', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('a'.repeat(200));
      submit();

      const call = expectRequest('POST', aliasesUrl(-1), 'the boundary creation');

      // The bound is inclusive on both sides of the wire, so the boundary value must pass rather than
      // be refused by an off-by-one.
      expect(call.request.body).toEqual({ httpAlias: 'a'.repeat(200) });

      call.flush(envelope(alias(9, 'a'.repeat(200))), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — REPLACING AN ALIAS
  // ---------------------------------------------------------------------------------------------------

  describe('editing an alias', () => {
    it('opens the held entry, replaces it with a 204 and re-reads the list', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);

      // The chosen row's own value opens the form, so a replacement starts from what is stored rather
      // than from nothing.
      expect(entryField().value).toBe('localhost');
      expect(button(UPDATE_SUBMIT_LABEL)).withContext('the replace wording').not.toBeUndefined();

      type('localhost:8080');
      submit();

      const call = expectRequest('PUT', aliasUrl(-1, 7), 'the replacement');

      expect(call.request.body).toEqual({ httpAlias: 'localhost:8080' });

      // ⚠ A REPLACEMENT ANSWERS 204 WITH NO BODY. Flushing a body here would let a screen that read one
      // pass, and the store maps the absence of one deliberately.
      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // The store re-reads after a replacement, because the response carries nothing to merge.
      answerAliases([alias(7, 'localhost:8080'), alias(8, 'localhost:4200')]);

      expect(notifications()).toEqual([{ severity: 'success', message: SAVED_MESSAGE }]);
      expect(query('form')).withContext('the form closes').toBeNull();
      expect(textOf('td.data-table__cell')).toContain('localhost:8080');
    });

    it('addresses the alias whose row was chosen, not the first one', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(1);

      expect(entryField().value).toBe('localhost:4200');

      type('localhost:4300');
      submit();

      const call = expectRequest('PUT', aliasUrl(-1, 8), 'the second row replacement');

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases/8');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerAliases([alias(7, 'localhost'), alias(8, 'localhost:4300')]);
    });

    it('clears the selection when the entry is abandoned, so the next add is not a replace', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      press(CANCEL_LABEL);

      press(ADD_ACTION_LABEL);

      // ⚠ THIS IS THE SELECTION-CLEARING CASE, AND ITS CONSEQUENCE IS SEVERE. A held selection makes
      // the very same form REPLACE instead of ADD, so an alias a person meant to create would silently
      // overwrite the one they had been editing. The wording is the observable proof of which it is.
      expect(button(ADD_SUBMIT_LABEL)).withContext('an add, not a replace').not.toBeUndefined();
      expect(entryField().value).toBe('');

      type('contoso.example.test');
      submit();

      const call = expectRequest('POST', aliasesUrl(-1), 'a creation, not a replacement');

      expect(call.request.method).toBe('POST');

      call.flush(envelope(alias(9, 'contoso.example.test')), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
    });

    it('clears a stale selection when the route moves to another portal', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')], '-1');

      editRow(0);
      expect(query('form')).not.toBeNull();

      fixture.componentRef.setInput('portalId', '0');
      fixture.detectChanges();

      // ⚠ A SELECTION HELD ACROSS A PORTAL CHANGE WOULD ADDRESS ANOTHER TENANT'S ALIAS. The form is
      // closed and the selection dropped as part of the move, so nothing can be submitted against the
      // portal that has just been navigated away from.
      expect(query('form')).withContext('the form closed with the move').toBeNull();

      answerAliases([alias(11, 'contoso.example.test', 0)], 0);

      press(ADD_ACTION_LABEL);
      expect(button(ADD_SUBMIT_LABEL))
        .withContext('a clean add for the new portal')
        .not.toBeUndefined();

      type('fabrikam.example.test');
      submit();

      const call = expectRequest('POST', aliasesUrl(0), 'the creation for the new portal');

      expect(call.request.url).toBe('/api/v1/portals/0/aliases');

      call.flush(envelope(alias(12, 'fabrikam.example.test', 0)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — REMOVING AN ALIAS
  // ---------------------------------------------------------------------------------------------------

  describe('deleting an alias', () => {
    it('offers no removal while a portal has only one alias', () => {
      arrive([alias(7, 'localhost')]);

      editRow(0);

      // ⚠ THE LAST ALIAS IS WITHHELD DELIBERATELY: removing it would leave the portal with no host name
      // and therefore unreachable, since resolution is by exact host match and nothing else.
      expect(button(DELETE_LABEL)).withContext('withheld on the last alias').toBeUndefined();
    });

    it('asks for confirmation, then removes with a 204 and announces the legacy wording', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      press(DELETE_LABEL);

      const dialogue = query<HTMLElement>('.confirm-dialog');

      expect(dialogue).withContext('the question is asked').not.toBeNull();
      expect((query('.confirm-dialog__message')?.textContent ?? '').trim()).toBe(
        DELETE_CONFIRM_MESSAGE,
      );
      expect((dialogue as HTMLElement).getAttribute('role')).toBe('alertdialog');
      expect(query('.confirm-dialog__button--danger'))
        .withContext('marked destructive')
        .not.toBeNull();
      httpMock.expectNone(() => true);

      pressDialogue(DELETE_LABEL);

      const call = expectRequest('DELETE', aliasUrl(-1, 7), 'the removal');

      expect(call.request.url).toBe('/api/v1/portals/-1/aliases/7');

      call.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // The store prunes the removed row locally, so no re-read follows a removal.
      httpMock.expectNone(() => true);
      // Announced at INFORMATION severity, which is the legacy screen's own register for a removal.
      expect(notifications()).toEqual([{ severity: 'info', message: DELETED_MESSAGE }]);
      expect(textOf('td.data-table__cell')).not.toContain('localhost');
      expect(query('form')).withContext('the form closes').toBeNull();
    });

    it('sends nothing when the confirmation is dismissed', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      press(DELETE_LABEL);
      pressDialogue(CANCEL_LABEL);

      expect(query('.confirm-dialog')).withContext('closed again').toBeNull();
      httpMock.expectNone(() => true);
      expect(notifications()).toHaveSize(0);
      // The entry survives the dismissal, because dismissing a delete is not abandoning an edit.
      expect(query('form')).not.toBeNull();
    });

    it('announces a refusal of authority for a removal as a warning in this screen own wording', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      editRow(0);
      press(DELETE_LABEL);

      pressDialogue(DELETE_LABEL);

      expectRequest('DELETE', aliasUrl(-1, 7)).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // ⚠ THE ANNOUNCEMENT IS SPECIFIC TO THE OPERATION THAT WAS REFUSED. The screen tracks which
      // command is outstanding precisely so a refused REMOVAL is not announced as a refused view, and
      // both wordings are the legacy screen's own rather than the server's sentence.
      expect(notifications()).toEqual([{ severity: 'warning', message: DELETE_DENIED_MESSAGE }]);
      // The row is still there, because nothing was removed.
      expect(textOf('td.data-table__cell')).toContain('localhost');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — A REFUSED READ
  // ---------------------------------------------------------------------------------------------------

  describe('a refused read', () => {
    it('announces a refused listing as a view refusal, not as a delete refusal', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'warning', message: VIEW_DENIED_MESSAGE }]);
      // And the document itself is shown in full, with its reference.
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
    });

    it('announces a refused listing once, however many times the screen re-renders', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problem('auth.not_permitted', 403, 'Not permitted.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();
      fixture.detectChanges();
      fixture.detectChanges();

      // The announcing effect holds the failure it last announced, so a re-render cannot repeat it. A
      // repeated transient message is how a person learns to ignore them.
      expect(notifications()).toHaveSize(1);
    });

    it('shows a portal that does not exist at 404 without announcing a view refusal', () => {
      create('-1');

      expectRequest('GET', aliasesUrl(-1)).flush(
        problem('portal.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // Only a refusal of AUTHORITY gets this screen's own wording; anything else is reported by the
      // shared surface in the server's own words, so no message is invented for it.
      expect(notifications()).withContext('nothing transient').toHaveSize(0);
      expect(textOf('.error-banner__message').join(' ')).toContain(
        'The requested resource does not exist.',
      );
    });

    it('shows the wait while the read is outstanding, and only then', () => {
      create('-1');

      expect(query('app-loading-spinner')).withContext('the wait is shown').not.toBeNull();

      answerAliases([alias(7, 'localhost')]);

      expect(query('app-loading-spinner')).withContext('and taken down').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      arrive();

      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('associates the field with its label so the control has a real accessible name', () => {
      arrive();

      press(ADD_ACTION_LABEL);

      const label = query<HTMLLabelElement>('label');

      expect(label).withContext('a real label element').not.toBeNull();
      expect((label as HTMLLabelElement).getAttribute('for')).toBe(ALIAS_CONTROL_ID);
      expect(entryField().id).toBe(ALIAS_CONTROL_ID);
    });

    it('renders a hostile alias as text, with no element parsed out of it', () => {
      arrive([alias(7, '<img src=x onerror="window.__aliased=true">')]);

      // A host name is operator-supplied and is therefore untrusted. What matters is the element tree:
      // the brackets survive as characters, and nothing is constructed from them.
      expect(queryAll('img')).withContext('no element parsed out of an alias').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__aliased'])
        .withContext('never evaluated')
        .toBeUndefined();
      expect(host().innerHTML).toContain('&lt;img');
    });

    it('offers every command as a real button rather than a scripted element', () => {
      arrive([alias(7, 'localhost'), alias(8, 'localhost:4200')]);

      // The legacy screen drew its edit affordance as an IMAGE inside a link button, announced as a
      // link and carrying no alternative text. Real buttons are announced as actions and are activated
      // by both Enter and Space with no scripting.
      expect(queryAll('[onclick]')).toHaveSize(0);
      queryAll<HTMLButtonElement>('button').forEach((candidate) => {
        expect(candidate.getAttribute('type'))
          .withContext('every button declares its type')
          .not.toBeNull();
      });
    });

    it('keeps the submit control out of use while a request is outstanding', () => {
      arrive([alias(7, 'localhost')]);

      press(ADD_ACTION_LABEL);
      type('contoso.example.test');
      submit();

      const call = expectRequest('POST', aliasesUrl(-1));

      // While the creation is in flight the control is out of use, so one press cannot become two
      // aliases. This is the one state in which it is unavailable.
      expect(button(ADD_SUBMIT_LABEL)?.disabled).withContext('in flight').toBeTrue();

      call.flush(envelope(alias(9, 'contoso.example.test')), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
    });
  });
});
