/**
 * Specification for {@link RoleGroupFormComponent} — adding a role group. ## WHAT THIS SCREEN IS, AND
 * WHAT IT DELIBERATELY IS NOT It ADDS a role group and nothing else. There is no edit mode, no identifier
 * in the address and no delete: the legacy screen offered exactly one action, and the migrated screen
 * reproduces that scope rather than inventing a fuller resource editor.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { RoleStore } from '../../../core/state/role.store';
import { CREATE_ACTION_LABEL, RoleGroupFormComponent } from './role-group-form.component';

import type { Type } from '@angular/core';
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
const DESCRIPTION_HELP = 'Enter a description of the role group.';
// ⚠ TAKEN FROM THE COMPONENT'S OWN CONSTANT, NOT RESTATED. It read 'Update' here and in the template,
// two copies of one string - and when the label was corrected the copies disagreed and every specification
// that pressed the control failed to find it. Importing the constant is what the file's own convention
// asks for and makes that class of drift impossible.
const SUBMIT_LABEL = CREATE_ACTION_LABEL;
const CANCEL_LABEL = 'Cancel';

/**
 * Wording this screen must NEVER publish. `EditGroups.ascx.resx` → `ModuleHelp.Text` is the one resource
 * value on this screen that carries live markup (`<h1>About Edit Role Groups</h1><p>…</p>`).
 */
const MODULE_HELP_HEADING = 'About Edit Role Groups';

/** The edit-mode title from this screen's own resource file, `ControlTitle_editgroup.Text`. */
const EDIT_MODE_TITLE = 'Edit Role Group';

// THE SEVERITY BANDS, AS THE SHARED BANNER PAINTS THEM

/** The band a FAULT is painted in — the migrated `ModuleMessageType.RedError`. */
const DANGER_BAND = 'danger';

/** The band a REFUSAL is painted in — the migrated `ModuleMessageType.YellowWarning`. */
const WARNING_BAND = 'warning';

/** The caption rendered for {@link DANGER_BAND}. */
const DANGER_CAPTION = 'Error';

/** The caption rendered for {@link WARNING_BAND}. */
const WARNING_CAPTION = 'Warning';

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
 * One role group. ⚠ THE DEFAULT IDENTIFIER IS ZERO, because `dbo.RoleGroups.RoleGroupID` is seeded from
 * zero: group zero is the first group of an installation and is not an absence.
 */
function roleGroup(roleGroupId = 0, overrides: Partial<RoleGroup> = {}): RoleGroup {
  return {
    roleGroupId,
    portalId: -1,
    roleGroupName: 'Paid Services',
    description: null,
    classifiedRoleCount: 0,
    ...overrides,
  };
}

function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

// =====================================================================================================
// NARROWING WITHOUT ASSERTING
// =====================================================================================================

/**
 * Narrows away `null` and `undefined` by THROWING when the value is absent. ⚠ THIS EXISTS SO THAT NO
 * NON-NULL ASSERTION APPEARS IN THIS FILE. An assertion silences the compiler and then fails later as a
 * `TypeError` raised deep inside an expectation, naming a property rather than the thing that was
 * missing.
 *
 * @param value The value to narrow.
 * @param what What was expected, named in the failure message.
 * @returns The value, guaranteed present.
 */
function present<T>(value: T | null | undefined, what: string): T {
  if (value === null || value === undefined) {
    throw new Error(`Expected ${what} to be present.`);
  }

  return value;
}

/**
 * Reads a request body as a dictionary of unknown members. ⚠ A REQUEST BODY IS GENUINELY UNKNOWN, so it
 * is inspected rather than asserted into a shape.
 *
 * @param body The body the request carried.
 * @returns The body's members, keyed by name.
 */
function bodyRecord(body: unknown): Readonly<Record<string, unknown>> {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    throw new Error(`Expected an object request body, received ${typeof body}.`);
  }

  // Copied member by member rather than cast. A cast would assert a shape this function cannot
  // verify; enumerating the real keys produces the same dictionary as a FACT about what was sent.
  const members: Record<string, unknown> = {};

  for (const key of Object.keys(body)) {
    members[key] = Reflect.get(body, key);
  }

  return members;
}

describe('RoleGroupFormComponent', () => {
  let fixture: ComponentFixture<RoleGroupFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // Standalone, so it is IMPORTED. There is nothing to declare.
      imports: [RoleGroupFormComponent],
      providers: [
        // ⚠ ORDER IS LOAD-BEARING, AND THESE TWO ARE ON SEPARATE LINES SO IT IS VISIBLE. The real client is
        // registered FIRST and the testing backend SECOND, because the backend displaces the real handler
        // already in place.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // Provided at this level rather than taken from the root injector, so each case gets a store with
        // no state carried over from the one before it.
        RoleStore,
      ],
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

  /** Mounts the screen. */
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
    return present(query<E>(`#${controlId}`), `#${controlId}`);
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
    present(button(label), `the "${label}" control`).click();
    fixture.detectChanges();
  }

  /**
   * The action buttons this screen owns, and ONLY those. ⚠ SCOPED TO THE ACTION ROW ON PURPOSE. A
   * document-wide `button` query does NOT answer "how many actions does this screen offer": the shared
   * field component contributes a help-disclosure button per field that carries help, so both fields add
   * one and a bare count is FOUR. Those two are affordances belonging to the fields, not commands
   * belonging to the form, and conflating them would make the count meaningless in either direction — it
   * would pass with a Delete button added and fail with a help sentence removed.
   */
  function actions(): readonly HTMLButtonElement[] {
    return queryAll<HTMLButtonElement>('.role-group-form__actions button');
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

  /** The mounted screen, so a case can read the form the template binds. */
  function screen(): RoleGroupFormComponent {
    return fixture.componentInstance;
  }

  /** The band the shared banner is painting, or null when it is painting nothing. */
  function severityBand(): string | null {
    const banner: Element | null = query('.error-banner');

    return banner === null ? null : banner.getAttribute('data-severity');
  }

  /** The severity as the WORD a person reads, or null when nothing is shown. */
  function severityCaption(): string | null {
    const caption: string | undefined = textOf('.error-banner__severity')[0];

    return caption === undefined ? null : caption;
  }

  /** The whole rendered text of the screen, for absence assertions. */
  function screenText(): string {
    return host().textContent ?? '';
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

      // ⚠ THE GUIDANCE IS COLLAPSED UNTIL ASKED FOR, and that is the shared field's arrangement rather than
      // this screen's: it reproduces the legacy help BUTTON, which revealed its text on demand, and it is a
      // real toggle rather than a tooltip so it is operable from the keyboard and readable by a screen
      // reader.
      const toggle: HTMLButtonElement = present(
        query<HTMLButtonElement>('.form-field__help-toggle'),
        'the help affordance',
      );

      expect(screenText()).withContext('and says nothing until asked').not.toContain(NAME_HELP);
      expect(toggle.getAttribute('aria-expanded'))
        .withContext('collapsed to begin with')
        .toBe('false');

      toggle.click();
      fixture.detectChanges();

      expect(screenText()).withContext('revealed on demand').toContain(NAME_HELP);
      expect(toggle.getAttribute('aria-expanded'))
        .withContext('and the state is announced')
        .toBe('true');
    });

    it('reveals BOTH guidance sentences as plain text, injecting no markup from resource wording', () => {
      create();

      const toggles: readonly HTMLButtonElement[] =
        queryAll<HTMLButtonElement>('.form-field__help-toggle');

      // One disclosure per field, because both fields carry guidance in the legacy resource file.
      expect(toggles).withContext('one disclosure per field').toHaveSize(2);

      toggles.forEach((toggle) => {
        toggle.click();
      });
      fixture.detectChanges();

      const revealed: readonly string[] = textOf('.form-field__help');

      expect(revealed).withContext('the name guidance').toContain(NAME_HELP);
      expect(revealed).withContext('the description guidance').toContain(DESCRIPTION_HELP);

      queryAll<HTMLElement>('.form-field__help').forEach((paragraph) => {
        expect(paragraph.children.length)
          .withContext('guidance is text, never markup')
          .toBe(0);
      });
    });

    it('never renders the module help, which is the one resource value carrying live markup', () => {
      create();

      // Read for context and deliberately not rendered: module help has no home in the shared component
      // set, and binding it would have to bypass escaping to look as intended.
      expect(screenText()).not.toContain(MODULE_HELP_HEADING);

      // Asserted with every disclosure OPEN, so the check covers the state in which resource-derived
      // wording is actually on screen. Closed, this would pass without inspecting anything.
      queryAll<HTMLButtonElement>('.form-field__help-toggle').forEach((toggle) => {
        toggle.click();
      });
      fixture.detectChanges();

      const revealed: readonly HTMLElement[] = queryAll<HTMLElement>('.form-field__help');

      expect(revealed).withContext('both disclosures are open').toHaveSize(2);
      expect(screenText())
        .withContext('and the module help is still nowhere')
        .not.toContain(MODULE_HELP_HEADING);

      // The heading and paragraph elements that `ModuleHelp.Text` would have introduced. Exactly one
      // heading exists on this screen — the page title — and no resource value contributed it.
      expect(queryAll('h1')).toHaveSize(1);
      expect(queryAll('.form-field__help h1, .form-field__help p, .form-field__help br')).toHaveSize(
        0,
      );
    });

    it('takes its heading from the create-mode wording, not the edit title its resource file declares', () => {
      create();

      expect((query('h1')?.textContent ?? '').trim()).toBe(PAGE_TITLE);
      expect(screenText()).not.toContain(EDIT_MODE_TITLE);
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
    it('holds the name invalid while it is empty, and the form with it', () => {
      create();

      // The rule as the FORM sees it, before any interaction.
      expect(screen().form.controls.roleGroupName.hasError('required'))
        .withContext('the required rule is armed')
        .toBeTrue();
      expect(screen().form.controls.roleGroupName.invalid).toBeTrue();
      expect(screen().form.invalid).withContext('and the form is invalid with it').toBeTrue();

      // ⚠ INVALID BUT SILENT. The legacy validator declared `display="Dynamic"`, which rendered nothing
      // until there was something to report, so an untouched control must show no message.
      expect(screen().form.controls.roleGroupName.touched).toBeFalse();
      expect(fieldMessages()).toHaveSize(0);
    });

    it('refuses an empty name, sends nothing and shows exactly the legacy sentence', () => {
      create();

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toEqual([NAME_REQUIRED_MESSAGE]);
      expect(screen().form.controls.roleGroupName.touched)
        .withContext('submitting touches the control, which is what reveals the message')
        .toBeTrue();
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('bounds the name by a real validator and not by the attribute alone', () => {
      create();

      screen().form.controls.roleGroupName.setValue('a'.repeat(NAME_MAX_LENGTH + 1));
      fixture.detectChanges();

      expect(screen().form.controls.roleGroupName.hasError('maxlength'))
        .withContext('the reactive bound')
        .toBeTrue();
      expect(screen().form.invalid).toBeTrue();

      screen().form.controls.roleGroupName.setValue('a'.repeat(NAME_MAX_LENGTH));
      fixture.detectChanges();

      // Inclusive: the boundary value itself is permitted, which is what `maxlength="50"` meant.
      expect(screen().form.controls.roleGroupName.hasError('maxlength')).toBeFalse();
      expect(screen().form.valid).toBeTrue();
    });

    it('treats the description as OPTIONAL, so a named group with no description is valid', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      expect(screen().form.controls.description.value).toBe('');
      expect(screen().form.valid).withContext('valid with no description at all').toBeTrue();
    });

    it('never puts ANY error on the description, because the legacy declared no rule for it', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      expect(screen().form.controls.description.errors)
        .withContext('no rule at all, not merely no required rule')
        .toBeNull();

      // Touching it changes nothing, which is the point: there is no state in which this field complains.
      screen().form.controls.description.markAsTouched();
      fixture.detectChanges();

      expect(screen().form.controls.description.errors).toBeNull();
      expect(fieldMessages()).withContext('and nothing is rendered against it').toHaveSize(0);

      // Its ONE bound is length, and it is a real validator rather than the attribute alone.
      screen().form.controls.description.setValue('a'.repeat(DESCRIPTION_MAX_LENGTH + 1));
      fixture.detectChanges();

      expect(screen().form.controls.description.hasError('maxlength')).toBeTrue();

      screen().form.controls.description.setValue('a'.repeat(DESCRIPTION_MAX_LENGTH));
      fixture.detectChanges();

      expect(screen().form.controls.description.errors)
        .withContext('the boundary length is permitted')
        .toBeNull();
    });

    it('refuses a name of nothing but spaces, which is the trimmed rule', () => {
      create();

      type(NAME_CONTROL_ID, '   ');

      press(SUBMIT_LABEL);

      expect(fieldMessages()).toContain(NAME_REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
    });

    it('explains a name longer than the column instead of refusing it silently', () => {
      create();

      type(NAME_CONTROL_ID, 'a'.repeat(NAME_MAX_LENGTH + 1));

      press(SUBMIT_LABEL);

      expect(fieldMessages())
        .withContext('the length bound is now stated in words')
        .toContain(`Enter at most ${String(NAME_MAX_LENGTH)} characters.`);

      // Unchanged and still the load-bearing half: an over-long value is never SENT.
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);

      // The control still bounds ordinary entry, so the message is a backstop rather than a substitute
      // for the cap.
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).getAttribute('maxlength'))
        .withContext('because the control bounds the entry as well')
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
      expect(bodyRecord(call.request.body)['roleGroupName']).toBe(longest);

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

      const body: Readonly<Record<string, unknown>> = bodyRecord(call.request.body);

      expect(Object.keys(body)).withContext('exactly the two declared members').toEqual([
        'roleGroupName',
        'description',
      ]);
      expect(Object.keys(body)).withContext('the tenant is not sent').not.toContain('portalId');
      expect(Object.keys(body))
        .withContext('nor the identifier')
        .not.toContain('roleGroupId');

      expect(call.request.url).withContext('the exact relative address').toBe(ROLE_GROUPS_URL);
      expect(call.request.url.startsWith('http')).withContext('relative address').toBeFalse();
      expect(call.request.params.keys()).withContext('no query string').toHaveSize(0);
      expect(call.request.params.has('portalId'))
        .withContext('and the tenant is not smuggled into the query string either')
        .toBeFalse();

      call.flush(envelope(roleGroup(0, { roleGroupName: 'Paid Services' })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      // The catalogue is re-read so every screen that offers a grouping picker sees the new entry.
      answerCatalogueReread();

      expect(notifications()).toEqual([{ severity: 'success', message: CREATED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ROLES_ROUTE], { replaceUrl: true });

      // ⚠ AND IT SURVIVES THE NAVIGATION IT IS RAISED WITH. The shell retires notifications on a completed
      // navigation, so a confirmation announced in the same task as the departure was swept before it could
      // be painted - the group was created and the operator was returned to the listing with nothing said.
      const notificationService = TestBed.inject(NotificationService);
      notificationService.clearOnNavigation();

      expect(notificationService.notifications().map((entry) => entry.message))
        .withContext('the confirmation belongs at the destination, as the legacy showed it')
        .toEqual([CREATED_MESSAGE]);
    });

    it('settles the form on success, so nobody is asked to discard work that was saved', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      press(SUBMIT_LABEL);

      const tracker = TestBed.inject(UnsavedChangesTracker);

      expect(screen().form.dirty).withContext('typing made it dirty').toBeTrue();

      expectRequest('POST', ROLE_GROUPS_URL).flush(envelope(roleGroup()));
      fixture.detectChanges();
      answerCatalogueReread();

      expect(screen().form.pristine).withContext('the entry is stored, so it is not unsaved').toBeTrue();
      expect(screen().form.untouched).toBeTrue();

      expect(tracker.isDirty())
        .withContext('the guard has nothing to ask about after a completed save')
        .toBeFalse();
    });

    it('sends an omitted description as the empty string, not as null', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      expect(call.request.body).toEqual({
        roleGroupName: 'Paid Services',
        description: '',
      });

      // ⚠ AND ASSERTED MEMBER BY MEMBER AGAINST EVERY NEAR MISS, because they are not interchangeable on
      // the wire and a single deep comparison reads as though they might be.
      const body: Readonly<Record<string, unknown>> = bodyRecord(call.request.body);

      expect(body['description']).withContext('present and empty').toBe('');
      expect(body['description']).withContext('not null').not.toBeNull();
      expect(body['description']).withContext('not the string "null"').not.toBe('null');
      expect(Object.keys(body))
        .withContext('the member is present rather than omitted')
        .toContain('description');
      expect(typeof body['description']).withContext('and it is a string').toBe('string');

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerCatalogueReread();
    });

    it('keeps the interior spacing of a name, which is the operator\u2019s own', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid  Services');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      expect(bodyRecord(call.request.body)['roleGroupName']).toBe('Paid  Services');

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerCatalogueReread();
    });

    it('tidies the padding off a name into the control, so what is shown is what is sent', () => {
      create();

      // MIGRATION: a deliberate divergence, and the same one the sibling role form makes. The legacy stored
      // what was posted.
      type(NAME_CONTROL_ID, '  Paid Services  ');

      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      expect(bodyRecord(call.request.body)['roleGroupName']).toBe('Paid Services');

      // The control agrees with the payload, so nobody is left looking at an entry that differs
      // from the one that was accepted.
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).value).toBe('Paid Services');

      call.flush(envelope(roleGroup()), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerCatalogueReread();
    });

    it('leaves a refused whitespace-only name exactly as typed, rather than blanking the box', () => {
      create();

      // The refusal comes from the presence rule, which trims before judging and does not rewrite the
      // value. A field that empties itself as you are told it is required reads as the screen having eaten
      // the entry.
      type(NAME_CONTROL_ID, '   ');

      press(SUBMIT_LABEL);

      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).value).toBe('   ');
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
      present(button(SUBMIT_LABEL), 'the submit control').click();
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

      expect(severityBand()).withContext('the fault band').toBe(DANGER_BAND);
      expect(severityCaption()).withContext('and it says so in words').toBe(DANGER_CAPTION);
      expect(severityBand()).withContext('emphatically not a refusal').not.toBe(WARNING_BAND);
    });

    it('keeps the operator on the form after a duplicate, with the entry intact', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      type(DESCRIPTION_CONTROL_ID, 'Groups that carry a fee');

      press(SUBMIT_LABEL);

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('role_group.name_duplicate', 409, 'A group with that name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(navigateSpy).not.toHaveBeenCalled();
      expect(field<HTMLInputElement>(NAME_CONTROL_ID).value).toBe('Paid Services');
      expect(field<HTMLTextAreaElement>(DESCRIPTION_CONTROL_ID).value).toBe(
        'Groups that carry a fee',
      );
      expect(httpMock.match(() => true))
        .withContext('and nothing is retried behind their back')
        .toHaveSize(0);
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

    it('paints a permission refusal as a WARNING and never as an error', () => {
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

      expect(severityBand()).withContext('the refusal band').toBe(WARNING_BAND);
      expect(severityCaption()).withContext('and it says so in words').toBe(WARNING_CAPTION);
      expect(severityBand()).withContext('NOT the fault band').not.toBe(DANGER_BAND);
      expect(severityCaption()).not.toBe(DANGER_CAPTION);

      // Nobody is moved, and nothing is announced transiently: the banner carries it in full.
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(notifications()).toHaveSize(0);
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

    it('pins a per-field refusal reported as 422 as readily as one reported as 400', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      // ⚠ BOTH STATUSES CARRY THE SAME DICTIONARY, and the API is free to choose either — 400 is the
      // automatic model-state refusal while 422 is a semantic one.
      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('request.invalid', 422, 'The request was understood but refused.', {
          roleGroupName: ['That name is reserved.'],
        }),
        { status: 422, statusText: 'Unprocessable Content' },
      );
      fixture.detectChanges();

      expect(fieldMessages()).toContain('That name is reserved.');
    });

    it('accepts the server model-state casing, which is not the casing a control is named in', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');

      press(SUBMIT_LABEL);

      // ⚠ THE KEY ARRIVES PASCAL-CASED, because .NET model-state keys are named after the request property.
      // The control is named `roleGroupName`, so something has to reconcile the two, and the shared reader
      // does it by lower-casing the FIRST CHARACTER only — which is why a compound name survives intact.
      expectRequest('POST', ROLE_GROUPS_URL).flush(
        problem('request.invalid', 400, 'One or more validation errors occurred.', {
          RoleGroupName: ['The server refused this name.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(fieldMessages()).toContain('The server refused this name.');
    });

    it('keeps the support reference so a person has something to quote', () => {
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

      // ⚠ SUBSTITUTING WORDING MUST NOT COST THE DIAGNOSTIC. This screen replaces the server's sentence for
      // two statuses and leaves every other member of the document alone, and the reference is the member
      // that matters: it is the operator's ONLY join key between what they saw in a browser and what the
      // server recorded.
      const reference: string = textOf('.error-banner__trace').join(' ');

      expect(reference).withContext('a reference is offered').not.toBe('');
      expect(reference === CORRELATION_ID || reference.includes(CORRELATION_ID))
        .withContext('and it is the correlation identifier the server sent')
        .toBeTrue();
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

      expectRequest('POST', ROLE_GROUPS_URL).flush(
        {
          type: 'urn:ietf:rfc:9110#section-15.5.1',
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

    it('leaves from an INVALID form without arguing about it, touching nothing', () => {
      create();

      expect(screen().form.invalid).withContext('nothing has been entered').toBeTrue();

      press(CANCEL_LABEL);

      expect(navigateSpy)
        .withContext('an incomplete form is abandoned, not argued with')
        .toHaveBeenCalledOnceWith([ROLES_ROUTE]);
      expect(httpMock.match(() => true)).withContext('nothing sent').toHaveSize(0);

      // ⚠ AND NOTHING WAS MARKED TOUCHED. This is what distinguishes "did not validate" from "validated and
      // happened to navigate anyway": the submit path marks every control touched, so an untouched control
      // after Cancel is positive proof the validation path was never entered.
      expect(screen().form.controls.roleGroupName.touched).toBeFalse();
      expect(screen().form.controls.description.touched).toBeFalse();
      expect(screen().form.touched).toBeFalse();
      expect(fieldMessages()).withContext('so no message was ever rendered').toHaveSize(0);
      expect(notifications()).toHaveSize(0);
      expect(query('.error-banner')).withContext('and no banner either').toBeNull();
    });

    it('lets an outstanding creation finish when the screen goes away, and announces nothing', () => {
      create();

      type(NAME_CONTROL_ID, 'Paid Services');
      press(SUBMIT_LABEL);

      const call = expectRequest('POST', ROLE_GROUPS_URL);

      fixture.destroy();

      expect(call.cancelled).withContext('the creation is not thrown away').toBeFalse();

      // The answer is adopted by the store, which re-reads the group listing on success so the group
      // is immediately selectable in the roles screen's group filter.
      call.flush(envelope(roleGroup()));

      const refresh = expectRequest('GET', ROLE_GROUPS_URL);
      refresh.flush(envelope([roleGroup()]));

      // ⚠ AND NOTHING IS ANNOUNCED OR NAVIGATED. The completion bridge is an effect created in this
      // component's own injection context, so it dies with the component: a success reaching a destroyed
      // screen cannot queue a notice nobody asked for, and cannot navigate a person who has already
      // navigated somewhere else.
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

    it('offers EXACTLY TWO commands — Update and Cancel — and no Delete or Manage', () => {
      create();

      const commands: readonly HTMLButtonElement[] = actions();

      expect(commands).withContext('two commands, no more').toHaveSize(2);
      expect(commands.map((command) => (command.textContent ?? '').trim())).toEqual([
        SUBMIT_LABEL,
        CANCEL_LABEL,
      ]);

      // The two absent affordances, named. Asserted over the WHOLE rendered text rather than over the
      // action row, so a delete offered anywhere on the screen — in a menu, beside a field — still fails.
      expect(screenText()).withContext('no delete anywhere').not.toContain('Delete');
      expect(screenText()).withContext('no manage anywhere').not.toContain('Manage');

      const everyButton: readonly HTMLButtonElement[] = queryAll<HTMLButtonElement>('button');
      const disclosures: readonly HTMLButtonElement[] =
        queryAll<HTMLButtonElement>('.form-field__help-toggle');

      expect(disclosures).withContext('one disclosure per field').toHaveSize(2);
      expect(everyButton)
        .withContext('and nothing else claims to be a button')
        .toHaveSize(commands.length + disclosures.length);
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        RoleGroupFormComponent as Type<RoleGroupFormComponent> & {
          readonly ɵcmp?: { readonly onPush?: boolean; readonly standalone?: boolean };
        }
      ).ɵcmp;

      const compiled = present(definition, 'the compiled component definition');

      expect(compiled.onPush).withContext('checks on push').toBeTrue();
      expect(compiled.standalone)
        .withContext('and is standalone, which is why it is imported rather than declared')
        .toBeTrue();
    });

    it('renders no table at all, the legacy layout tables having become a grid', () => {
      create();

      // The legacy laid this screen out as TWO nested layout tables — `EditGroups.ascx:L4` and `:L7` — and
      // the outer one carried a table-description reading "Edit Roles Design Table", wording copy-pasted
      // from a DIFFERENT screen.
      expect(queryAll('table, thead, tbody, tr, th, td')).toHaveSize(0);
    });

    it('carries no collapsible section, the legacy registration having gone unused', () => {
      create();

      // ⚠ ASSERTED PRECISELY RATHER THAN AS A BLANKET ZERO. The document does contain `aria-expanded`,
      // because the shared field's help disclosure legitimately carries it — that is a per-field
      // affordance, not the legacy section.
      const expandable: readonly Element[] = queryAll('[aria-expanded]');

      // Sized FIRST, so the per-element assertion below cannot pass by iterating nothing. A `forEach` over
      // an empty list registers no expectation at all and reports success, which would make this case agree
      // with a screen that had lost both disclosures.
      expect(expandable).withContext('exactly the two help disclosures').toHaveSize(2);

      expandable.forEach((element) => {
        expect(element.classList.contains('form-field__help-toggle'))
          .withContext('the only expandable thing here is a help disclosure')
          .toBeTrue();
      });

      expect(queryAll('details, summary'))
        .withContext('and no native disclosure either')
        .toHaveSize(0);
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
