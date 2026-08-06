/**
 * Specification for {@link RoleGroupFormComponent} — adding a role group.
 *
 * ## WHAT THIS SCREEN IS, AND WHAT IT DELIBERATELY IS NOT
 *
 * It ADDS a role group and nothing else. There is no edit mode, no identifier in the address and no
 * delete: the legacy screen offered exactly one action, and the migrated screen reproduces that scope
 * rather than inventing a fuller resource editor. Every case below is therefore about a single `POST`
 * and about the rules that decide whether it happens at all.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - Mounted as the standalone unit it is, with the REAL {@link RoleService} and {@link RoleStore}
 *     resolved from the injector and every request answered through `HttpTestingController`.
 *   - `Router.navigate` is spied, because the screen navigates with an ARRAY of commands.
 *   - `NotificationService.notify` is spied and called through.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ THE NAME RULE IS TRIMMED-REQUIRED, NOT MERELY REQUIRED. A name of nothing but spaces is refused,
 * because a group named `'   '` is indistinguishable from an unnamed one in every listing that shows it
 * while comparing as a distinct value in the database.
 *
 * ⚠ NOTHING IS TRIMMED ON THE WAY OUT. The value travels byte for byte as typed, so a case must not
 * assume the request "tidies" a name that passed the rule.
 *
 * ⚠ A SUCCESSFUL CREATION RE-READS THE GROUP CATALOGUE AND THEN LEAVES, so `GET /role-groups` follows
 * the `POST` and must be answered.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { RoleStore } from '../../../core/state/role.store';
import { RoleGroupFormComponent } from './role-group-form.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { RoleGroup } from '../../../core/models/role.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

const ROLE_GROUPS_URL = '/api/v1/role-groups';
const ROLES_ROUTE = '/roles';

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
// =====================================================================================================

const PAGE_TITLE = 'Add New Role Group';
const NAME_LABEL = 'Group Name';
const NAME_HELP = 'Enter the name of the role group.';
const DESCRIPTION_LABEL = 'Description';
const SUBMIT_LABEL = 'Update';
const CANCEL_LABEL = 'Cancel';

const NAME_REQUIRED_MESSAGE = 'You Must Enter a Valid Name';
const CREATED_MESSAGE = 'The new group was added.';
const DUPLICATE_MESSAGE =
  'A role group with the same name already exists. The new group was not added.';
const ACCESS_DENIED_MESSAGE =
  'Either you are not currently logged in, or you do not have access to this content.';
const NETWORK_UNAVAILABLE_MESSAGE =
  'The server could not be reached. Check your connection and try again.';
const UNEXPECTED_FAILURE_MESSAGE = 'The request could not be completed.';

const NAME_CONTROL_ID = 'role-group-name';
const DESCRIPTION_CONTROL_ID = 'role-group-description';

const NAME_MAX_LENGTH = 50;
const DESCRIPTION_MAX_LENGTH = 1000;

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  409: 'Conflict',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-6f3b2e1a9c934dd6bb18eb211c80319c-22bd6b7169203331-01';
const CORRELATION_ID = 'd47e2b95-1c68-4a30-9f52-8b6a0d3e7c19';

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

  return errors === undefined ? document : { ...document, errors };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * One role group.
 *
 * ⚠ THE DEFAULT IDENTIFIER IS ZERO, because `dbo.RoleGroups.RoleGroupID` is seeded from zero: group
 * zero is the first group of an installation and is not an absence.
 */
function roleGroup(roleGroupId = 0, overrides: Partial<RoleGroup> = {}): RoleGroup {
  return {
    roleGroupId,
    portalId: -1,
    roleGroupName: 'Paid Services',
    description: null,
    ...overrides,
  };
}

function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

describe('RoleGroupFormComponent', () => {
  let fixture: ComponentFixture<RoleGroupFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    await TestBed.configureTestingModule({
      imports: [RoleGroupFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), RoleStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Mounts the screen. It reads nothing on arrival, so no request is outstanding afterwards. */
  function create(): void {
    fixture = TestBed.createComponent(RoleGroupFormComponent);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Answers the catalogue re-read that follows a successful creation. */
  function answerCatalogueReread(): void {
    expectRequest('GET', ROLE_GROUPS_URL, 'the catalogue re-read').flush(
      envelope([roleGroup()]),
    );
    fixture.detectChanges();
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

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(controlId: string): E {
    const element = query<E>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as E;
  }

  /** Types into a control the way a person does. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** A button by its rendered wording. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** Presses a button by its rendered wording. */
  function press(label: string): void {
    const control = button(label);

    expect(control).withContext(`the "${label}" control is offered`).not.toBeUndefined();

    (control as HTMLButtonElement).click();
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
  // PROOF 1 — ARRIVAL
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads nothing at all, because there is nothing to read', () => {
      create();

      // ⚠ NO OPENING REQUEST. This screen only ever ADDS: it has no identifier in its address and no
      // record to hydrate, so a read here would be a round trip with nothing to learn.
      expect(httpMock.match(() => true)).withContext('nothing is requested').toHaveSize(0);
      expect((query('h1')?.textContent ?? '').trim()).toBe(PAGE_TITLE);
    });

    it('offers both fields, empty, with their guidance behind the shared help affordance', () => {
      create();

      expect(field<HTMLInputElement>(NAME_CONTROL_ID).value).toBe('');
      expect(field<HTMLTextAreaElement>(DESCRIPTION_CONTROL_ID).value).toBe('');

      // ⚠ THE GUIDANCE IS COLLAPSED UNTIL ASKED FOR, and that is the shared field's arrangement rather
      // than this screen's: it reproduces the legacy help BUTTON, which revealed its text on demand, and
      // it is a real toggle rather than a tooltip so it is operable from the keyboard and readable by a
      // screen reader. A case expecting the sentence to be present on arrival would be asserting a
      // different design.
      const toggle: HTMLButtonElement | null = query<HTMLButtonElement>('.form-field__help-toggle');

      expect(toggle).withContext('the help affordance is offered').not.toBeNull();
      expect(host().textContent ?? '')
        .withContext('and says nothing until asked')
        .not.toContain(NAME_HELP);

      (toggle as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(host().textContent ?? '').withContext('revealed on demand').toContain(NAME_HELP);
      expect((toggle as HTMLButtonElement).getAttribute('aria-expanded'))
        .withContext('and the state is announced')
        .toBe('true');
    });

    it('says nothing about validity before a person has acted', () => {
      create();

      // An untouched form is not wrong yet; greeting somebody with errors they have not made is how a
      // form teaches them to ignore its messages.
      expect(fieldMessages()).toHaveSize(0);
    });

    it('bounds typing at each column length without making either control invalid', () => {
      create();

      // A truncation policy rather than a validation rule: the attribute stops over-long typing at the
      // source while the rule below remains the actual guarantee.
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).getAttribute('maxlength')).toBe(
        String(NAME_MAX_LENGTH),
      );
      expect(field<HTMLTextAreaElement>(DESCRIPTION_CONTROL_ID).getAttribute('maxlength')).toBe(
        String(DESCRIPTION_MAX_LENGTH),
      );
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE RULES
  // ---------------------------------------------------------------------------------------------------

  describe('the entry rules', () => {
    it('refuses an empty name and sends nothing', () => {
      create();

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toContain(NAME_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('refuses a name of nothing but spaces, which is the trimmed rule', () => {
      create();

      type(NAME_CONTROL_ID, '   ');

      press(SUBMIT_LABEL);

      // ⚠ THIS IS WHY THE RULE IS TRIMMED-REQUIRED RATHER THAN MERELY REQUIRED. A group named with
      // spaces looks unnamed in every listing that shows it, yet compares as a distinct value in the
      // database — so two of them can coexist and neither can be told apart.
      expect(fieldMessages()).toContain(NAME_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
    });

    it('sends nothing for a name longer than the column, silently and by design', () => {
      create();

      type(NAME_CONTROL_ID, 'a'.repeat(NAME_MAX_LENGTH + 1));

      press(SUBMIT_LABEL);

      // ⚠ THE REFUSAL IS SILENT, AND DELIBERATELY SO. This screen publishes wording for exactly ONE
      // rule - the name requirement - plus whatever the server reports per field. There is no authored
      // length sentence because the length state is UNREACHABLE THROUGH THE INTERFACE: the control's own
      // bound stops typing and pasting at the column length, so a person cannot produce this value.
      // Authoring a message for it would put a string on screen that no interaction can ever show, and
      // asserting one here would demand that. What matters is that the over-long value is not SENT.
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(fieldMessages())
        .withContext('and no sentence is invented for a state a person cannot reach')
        .toHaveSize(0);
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).getAttribute('maxlength'))
        .withContext('because the control bounds the entry instead')
        .toBe(String(NAME_MAX_LENGTH));
    });

    it('accepts the longest permitted name and sends it whole', () => {
      create();

      const longest: string = 'a'.repeat(NAME_MAX_LENGTH);

      type(NAME_CONTROL_ID, longest);

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL, 'the boundary creation');

      // The bound is inclusive on both sides of the wire, so the boundary value must pass rather than be
      // refused by an off-by-one.
      expect((call.request.body as { roleGroupName: string }).roleGroupName).toBe(longest);

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerCatalogueReread();
    });

    it('sends nothing for a description longer than its own column, on the same rule', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      type(DESCRIPTION_CONTROL_ID, 'a'.repeat(DESCRIPTION_MAX_LENGTH + 1));

      press(SUBMIT_LABEL);

      // Same arrangement as the name: bounded at the control, enforced by the rule, unsent when
      // exceeded, and no sentence authored for a state the interface cannot produce.
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(field<HTMLTextAreaElement>(DESCRIPTION_CONTROL_ID).getAttribute('maxlength')).toBe(
        String(DESCRIPTION_MAX_LENGTH),
      );
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE REQUEST
  // ---------------------------------------------------------------------------------------------------

  describe('the creation request', () => {
    it('posts exactly the two declared members and answers 201', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      type(DESCRIPTION_CONTROL_ID, 'Groups that carry a fee');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL, 'the creation');

      // Asserted as a whole object, so a member added, renamed or dropped by either side fails here.
      // The portal is NOT a member: the tenant is resolved from the request, not carried in the body.
      expect(call.request.body).toEqual({
        roleGroupName: 'Paid Services',
        description: 'Groups that carry a fee',
      });
      expect(call.request.url.startsWith('http')).withContext('relative address').toBeFalse();
      expect(call.request.params.keys()).withContext('no query string').toHaveSize(0);

      call.flush(envelope(roleGroup(0, { roleGroupName: 'Paid Services' })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      // The catalogue is re-read so every screen that offers a grouping picker sees the new entry.
      answerCatalogueReread();

      expect(notifications()).toEqual([{ severity: 'success', message: CREATED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLES_ROUTE]);
    });

    it('sends an omitted description as the empty string, not as null', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      // ⚠ THE EMPTY STRING IS WHAT THIS CONTRACT CARRIES, and it is deliberate: the control is declared
      // non-nullable so it can never hold null, and the legacy absent-string marker WAS the empty
      // string — `Null.vb` returns `""` — so `''` is precisely how this schema spells "no description".
      expect(call.request.body).toEqual({
        roleGroupName: 'Paid Services',
        description: '',
      });

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerCatalogueReread();
    });

    it('sends the name exactly as typed, because nothing is trimmed on the way out', () => {
      create();

      // Interior spacing is meaningful in a name a person chose, and the rule only requires that a name
      // is not ENTIRELY space — so a name that passed it travels byte for byte.
      type(NAME_CONTROL_ID, 'Paid  Services');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      expect((call.request.body as { roleGroupName: string }).roleGroupName).toBe('Paid  Services');

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerCatalogueReread();
    });

    it('locks the submit control while the creation is outstanding', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      // ⚠ THE LOCK IS THE ONLY THING BETWEEN ONE PRESS AND TWO GROUPS. There is no identifier to make a
      // second attempt idempotent, so a double press would create a duplicate the server would accept.
      expect(button(SUBMIT_LABEL)?.disabled).withContext('locked in flight').toBeTrue();

      // And pressing it again while locked sends nothing.
      (button(SUBMIT_LABEL) as HTMLButtonElement).click();
      fixture.detectChanges();

      const seconds: readonly TestRequest[] = httpMock.match(
        (candidate) => candidate.method === 'POST' && candidate.url === ROLE_GROUPS_URL,
      );

      expect(seconds).withContext('no second creation').toHaveSize(0);

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).withContext('released once settled').toBeFalse();
      answerCatalogueReread();
    });

    it('releases the lock after a refusal so the entry can be corrected and retried', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('role_group.name_duplicate', 409, 'A group with that name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).withContext('released').toBeFalse();
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).value)
        .withContext('the entry survives')
        .toBe('Paid Services');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — REFUSALS
  // ---------------------------------------------------------------------------------------------------

  describe('a refused creation', () => {
    it('shows the legacy duplicate sentence at 409 rather than the server own', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('role_group.name_duplicate', 409, 'A group with that name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // ⚠ THE LEGACY WORDING WINS FOR THIS ONE STATUS, and it says more than the server's sentence does:
      // it states that the new group was NOT added, which is the fact an operator needs.
      expect(textOf('.error-banner__message').join(' ')).toContain(DUPLICATE_MESSAGE);
      expect(navigateSpy).withContext('nobody is moved').not.toHaveBeenCalled();
    });

    it('shows the legacy access sentence at 403', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // The legacy sentence covers both causes a person can act on — not signed in, or not permitted —
      // which the server's single sentence does not distinguish either.
      expect(textOf('.error-banner__message').join(' ')).toContain(ACCESS_DENIED_MESSAGE);
    });

    it('pins a per-field refusal to the control the server named', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('request.invalid', 400, 'One or more validation errors occurred.', {
          roleGroupName: ['That name is not acceptable.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // The map's keys are .NET model-state keys matching the CONTROL names, are not camel-cased by the
      // client, and are read with an index expression because the map is an index signature.
      expect(fieldMessages()).toContain('That name is not acceptable.');
    });

    it('pins a per-field refusal about the description to the description', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('request.invalid', 400, 'One or more validation errors occurred.', {
          description: ['That description is too long.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(fieldMessages()).toContain('That description is too long.');
    });

    it('says the server could not be reached when the transport itself fails', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      // A transport failure carries status zero and no document at all, and telling a person that a
      // request "was rejected" would be wrong: nothing ever reached the server to reject it.
      expectRequest('POST', ROLE_GROUPS_URL).error(new ProgressEvent('error'));
      fixture.detectChanges();

      expect(textOf('.error-banner__message').join(' ')).toContain(NETWORK_UNAVAILABLE_MESSAGE);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('falls back to its own sentence for a failure that is not a response at all', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      // Something threw that is not an HTTP response — a serialisation fault, say. The screen still says
      // something true rather than rendering an empty banner.
      expectRequest('POST', ROLE_GROUPS_URL).flush('not json at all', {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      expect(textOf('.error-banner__message').join(' ')).not.toBe('');
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('shows a document that carries no coded type in the server own words', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      // Not every refusal carries an application code: a framework-level one carries a foreign type
      // anchored in the HTTP specification, and it must still be readable.
      expectRequest('POST', ROLE_GROUPS_URL).flush(
        {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
          title: 'Bad Request',
          status: 400,
          detail: 'The request body could not be read.',
          traceId: TRACE_ID,
          correlationId: CORRELATION_ID,
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(textOf('.error-banner__message').join(' ')).toContain(
        'The request body could not be read.',
      );
    });

    it('reports nothing transiently for a refusal the banner already carries', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('role_group.name_duplicate', 409, 'A group with that name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // One event, one surface. The banner shows it in full and stays until the next attempt, so a
      // transient message for the same event would be the same thing said twice.
      expect(notifications()).toHaveSize(0);
      expect(query('.error-banner__title')).not.toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — LEAVING
  // ---------------------------------------------------------------------------------------------------

  describe('leaving the screen', () => {
    it('abandons without sending or validating anything', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      press(CANCEL_LABEL);

      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLES_ROUTE]);
      // Nothing was validated on the way out, which is what the legacy abandon action declared.
      expect(fieldMessages()).toHaveSize(0);
      expect(notifications()).toHaveSize(0);
    });

    it('lets an outstanding creation finish when the screen goes away, and announces nothing', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      fixture.destroy();

      // ⚠ THE WRITE IS THE STORE'S, NOT THE SCREEN'S, AND SO IT SURVIVES THE SCREEN. Cancelling it
      // here would cancel nothing that matters: the server has already been asked and will create the
      // group whatever this browser does next, so abandoning the request would only mean the
      // application never LEARNS about it — leaving the shared group listing stale and the operator
      // with no way to know whether the thing they asked for happened. This screen used to own the
      // request and did cancel it, and that was the defect: one copy of the outcome in the component
      // and another in the store, free to disagree about whether a group exists.
      expect(call.cancelled).withContext('the creation is not thrown away').toBeFalse();

      // The answer is adopted by the store, which re-reads the group listing on success so the group
      // is immediately selectable in the roles screen's group filter.
      call.flush(envelope(roleGroup()));

      const refresh = expectRequest('GET', ROLE_GROUPS_URL);
      refresh.flush(envelope([roleGroup()]));

      // ⚠ AND NOTHING IS ANNOUNCED OR NAVIGATED. The completion bridge is an effect created in this
      // component's own injection context, so it dies with the component: a success reaching a
      // destroyed screen cannot queue a notice nobody asked for, and cannot navigate a person who has
      // already navigated somewhere else.
      expect(notifications()).withContext('nothing is announced').toHaveSize(0);
      expect(navigateSpy).withContext('and nobody is moved').not.toHaveBeenCalled();

      httpMock.verify();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      create();

      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('names both controls with real labels pointing at them', () => {
      create();

      const labels: readonly HTMLLabelElement[] = queryAll<HTMLLabelElement>('label[for]');
      const targets: readonly string[] = labels.map((label) => label.getAttribute('for') ?? '');

      expect(targets).toContain(NAME_CONTROL_ID);
      expect(targets).toContain(DESCRIPTION_CONTROL_ID);
      // The shared field strips one trailing colon from a label, so the rendered text carries none —
      // which is the legacy majority arrangement rather than an omission.
      labels.forEach((label) => {
        expect((label.textContent ?? '').trim()).not.toContain(':');
      });
      expect(labels.map((label) => (label.textContent ?? '').trim()).join('|')).toContain(NAME_LABEL);
      expect(labels.map((label) => (label.textContent ?? '').trim()).join('|')).toContain(
        DESCRIPTION_LABEL,
      );
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      create();

      expect(query('.error-banner-live')).withContext('the region exists').not.toBeNull();
      expect(query('.error-banner__title')).withContext('but says nothing').toBeNull();
    });

    it('declares the type of both commands so neither submits by accident', () => {
      create();

      expect(button(SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      expect(button(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
      expect(queryAll('[onclick]')).toHaveSize(0);
    });

    it('suppresses the browser own validation bubble so it cannot compete with the field message', () => {
      create();

      // Two mechanisms reporting the same requirement in two places, one of which cannot be styled or
      // read by a screen reader, is worse than one.
      expect(query('form')?.hasAttribute('novalidate')).toBeTrue();
    });
  });
});
