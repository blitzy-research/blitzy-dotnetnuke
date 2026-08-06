import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { MembershipSettings, UserListItem } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import { MembershipSettingsComponent } from './membership-settings.component';

/**
 * Specification for the tenant's account-administration policy screen.
 *
 * This screen edits ONE record with twenty-three members and no identifier, which makes its
 * failure modes unusual enough to name up front. Every case below exists because getting one
 * of these wrong produces a screen that looks correct and quietly corrupts a live policy:
 *
 *   - ⚠ THE WHOLE POLICY IS SENT, ALWAYS. The write is a REPLACE, not a patch, so a member
 *     dropped for "looking empty" is not an omission the server tolerates — it is a value
 *     being asserted. This contract is dense with legitimate falsy values: `false` for each
 *     of the nine listing columns and the three profile switches, `0` for a display mode,
 *     `null` for each of the three landing pages. A body assembled by filtering would
 *     silently re-show a column an administrator had hidden.
 *   - ⚠ SUBMISSION IS REFUSED UNTIL THE POLICY HAS ARRIVED. The form is seated with the
 *     measured legacy defaults before the server answers, so submitting in that state would
 *     overwrite a live policy with values nobody chose. That is why `canSubmit` consults the
 *     read as well as the write.
 *   - ⚠ A SUCCESSFUL WRITE IS THREE REQUESTS, NOT ONE. The response carries no body, so the
 *     store re-reads the policy, and because the policy declares the size of a page it then
 *     re-reads the account listing too. A specification that answers only the write leaves
 *     two requests outstanding and `verify` reports them.
 *   - ⚠ ZERO IS A REAL PAGE IDENTIFIER. The page table's identity seeds at zero, which is
 *     why the server's floor is zero rather than one and why `null` — not `-1`, the legacy
 *     whole-codebase marker for "no integer" — is the only expression of "no redirect".
 *   - ⚠ TWO WHOLE LEGACY SECTIONS ARE DELIBERATELY ABSENT. "Membership Provider Settings"
 *     and "Password Aging Settings" have no member on this contract, so this screen must
 *     render no provider field and no expiry field at all. Their absence is asserted, not
 *     assumed: a later reader looking for them needs the specification to say they are gone
 *     on purpose.
 *
 * MIGRATION: the legacy screen (`Website/admin/Users/UserSettings.ascx` and its code-behind)
 * wrote one stored setting at a time for whichever editor reported itself dirty (L172) and
 * coerced every value to text on the way (L174). Both behaviours are gone; the cases below
 * assert the replacement rather than the original.
 */
describe('MembershipSettingsComponent', () => {
  let fixture: ComponentFixture<MembershipSettingsComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let successSpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  // ---------------------------------------------------------------------------------------------------
  // ADDRESSES
  // ---------------------------------------------------------------------------------------------------
  //
  // Written out as relative literals rather than composed from the endpoint table, so that a
  // change to that table shows up here as a failure instead of being silently agreed with.

  const SETTINGS_URL = '/api/v1/users/settings';
  const USERS_URL = '/api/v1/users';

  /** Where both commands land. */
  const ACCOUNT_LISTING_PATH = '/users';

  /** The companion screen the header action links to. */
  const PROFILE_DEFINITIONS_PATH = '/settings/profile-definitions';

  // ---------------------------------------------------------------------------------------------------
  // WORDING
  // ---------------------------------------------------------------------------------------------------

  const TITLE = 'User Settings';
  const SUBTITLE = 'Account administration settings for this site';
  const SECTION_HEADING = 'User Accounts Settings';
  const COLUMNS_HEADING = 'Account Listing Columns';
  const SUBMIT_LABEL = 'Update';
  const CANCEL_LABEL = 'Cancel';
  const LOADING_LABEL = 'Loading user settings…';
  const SAVING_LABEL = 'Saving user settings…';
  const SAVED_MESSAGE = 'User settings saved.';
  const REQUIRED_MESSAGE = 'This setting is required.';
  const PAGE_SIZE_RANGE_MESSAGE = 'The number of accounts per page must be between 1 and 100.';
  const NEGATIVE_PAGE_MESSAGE =
    'A page identifier may not be negative. Leave the field empty for no redirect.';
  const LENGTH_MESSAGE = 'This setting may not exceed 2000 characters.';

  /** The bounds the server itself applies, mirrored in the browser. */
  const MINIMUM_RECORDS_PER_PAGE = 1;
  const MAXIMUM_RECORDS_PER_PAGE = 100;
  const MAXIMUM_SETTING_LENGTH = 2000;

  /**
   * The reason phrase the API publishes as a problem `title`, keyed by status.
   *
   * ⚠ NOT FREE TEXT. Every refusal reaches the wire through one shared problem factory that
   * fills the title from this status-keyed vocabulary, so a fixture carrying a bespoke title
   * describes no response this server can produce.
   */
  const PROBLEM_TITLE: Readonly<Record<number, string>> = Object.freeze({
    400: 'Bad Request',
    401: 'Unauthorized',
    403: 'Forbidden',
    404: 'Not Found',
    409: 'Conflict',
    429: 'Too Many Requests',
    500: 'Internal Server Error',
    503: 'Service Unavailable',
  });

  /**
   * A live problem document, complete in every member the API actually emits.
   *
   * ⚠ A LIVE DOCUMENT ALWAYS CARRIES `type` AND NEVER CARRIES `instance`, and it carries BOTH
   * a trace identifier and a correlation identifier. The shared reference reader prefers the
   * correlation identifier, so a fixture carrying only a trace identifier exercises a branch
   * no operator reaches.
   */
  function problem(
    code: string,
    status: number,
    detail: string,
    errors?: Readonly<Record<string, readonly string[]>>,
  ): ProblemDetails {
    const document: ProblemDetails = {
      type: `urn:dnnmigration:error:${code}`,
      title: PROBLEM_TITLE[status] ?? 'Bad Request',
      status,
      detail,
      traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      correlationId: '8c7f0f5e-4a52-4f7a-9a3f-1f1c0d7b6a55',
    };

    return errors === undefined ? document : { ...document, errors };
  }

  // ---------------------------------------------------------------------------------------------------
  // FIXTURES
  // ---------------------------------------------------------------------------------------------------

  /**
   * A policy as the server sends it, with every member present.
   *
   * The defaults below are NOT the measured legacy ones: several are deliberately the opposite,
   * so that a case asserting the form was seated from the server cannot pass against a form
   * that merely kept its own seated defaults.
   */
  function settings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
    return {
      columnFirstName: true,
      columnLastName: true,
      columnDisplayName: false,
      columnAddress: false,
      columnTelephone: false,
      columnEmail: true,
      columnCreatedDate: false,
      columnLastLogin: true,
      columnAuthorized: false,
      displayMode: 1,
      displaySuppressPager: true,
      recordsPerPage: 25,
      profileDefaultVisibility: 0,
      profileDisplayVisibility: false,
      profileManageServices: false,
      redirectAfterLogin: 0,
      redirectAfterRegistration: null,
      redirectAfterLogout: 42,
      securityEmailValidation: '^\\S+@\\S+$',
      securityRequireValidProfile: true,
      securityRequireValidProfileAtLogin: false,
      securityUsersControl: 1,
      securityDisplayNameFormat: '[FIRSTNAME] [LASTNAME]',
      ...overrides,
    };
  }

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  /**
   * A page of accounts.
   *
   * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture spelling it
   * otherwise flushes successfully and unwraps to no rows at all.
   */
  function emptyPage(pageSize: number): PagedResponse<UserListItem> {
    return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize, totalPages: 0 } };
  }

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces
    // it. Reversing the two leaves the live backend in place and every expectation times out.
    await TestBed.configureTestingModule({
      imports: [MembershipSettingsComponent],
      // The store is listed so each case gets its own instance. It is declared
      // `providedIn: 'root'`, so without this every case would share one policy and one
      // failure slot, and a case would observe state another had recorded.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), UserStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    const notifications = TestBed.inject(NotificationService);

    notifySpy = spyOn(notifications, 'notify').and.callThrough();
    successSpy = spyOn(notifications, 'success').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. The policy read is issued from `ngOnInit`, during this first pass. */
  function create(): void {
    fixture = TestBed.createComponent(MembershipSettingsComponent);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(
    method: string,
    url: string,
    description?: string,
  ): ReturnType<HttpTestingController['expectOne']> {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Mounts the screen and answers its opening read. */
  function arrive(policy: MembershipSettings | null = settings()): void {
    create();
    expectRequest('GET', SETTINGS_URL, 'the policy read').flush(envelope(policy));
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

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(name: string): E {
    const element = query<E>(`#membership-setting-${name}`);

    expect(element).withContext(`#membership-setting-${name} is rendered`).not.toBeNull();

    return element as E;
  }

  /** Types into a text or numeric control. */
  function type(name: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(name);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    control.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  /** Sets a switch to a state, only dispatching when the state actually changes. */
  function toggle(name: string, checked: boolean): void {
    const control = field<HTMLInputElement>(name);

    if (control.checked === checked) {
      return;
    }

    control.checked = checked;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /**
   * Chooses a selector option by its RENDERED LABEL.
   *
   * ⚠ THE OPTIONS BIND `[value]` HERE, NOT `[ngValue]`, so the DOM value is the integer as
   * text. The label is used regardless, because a label is what an operator sees and it
   * survives a reordering of the option list.
   */
  function choose(name: string, label: string): void {
    const control = field<HTMLSelectElement>(name);
    const option: HTMLOptionElement | undefined = Array.from(control.options).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );

    expect(option).withContext(`the option labelled "${label}" is offered`).not.toBeUndefined();

    control.value = (option as HTMLOptionElement).value;
    control.dispatchEvent(new Event('change'));
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

  /** Submits the form through its own submit event, as pressing the command does. */
  function submitForm(): void {
    press(SUBMIT_LABEL);
  }

  /** The messages the shared field component is rendering, in document order. */
  function fieldErrors(): readonly string[] {
    return queryAll<Element>('.form-field__error').map((node) => (node.textContent ?? '').trim());
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  /**
   * Answers a successful write in full.
   *
   * ⚠ THREE REQUESTS, IN THIS ORDER. The write answers with no body, so the store re-reads the
   * policy, and because the policy declares the size of a page it then re-reads the listing.
   */
  function answerWriteFollowUp(policy: MembershipSettings): void {
    expectRequest('GET', SETTINGS_URL, 'the re-read policy').flush(envelope(policy));
    fixture.detectChanges();

    const listing = expectRequest('GET', USERS_URL, 'the re-read listing');

    expect(listing.request.params.get('pageIndex'))
      .withContext('the listing returns to the first page')
      .toBe('0');
    expect(listing.request.params.get('pageSize'))
      .withContext('the listing is fetched at the size the policy declares')
      .toBe(String(policy.recordsPerPage));

    listing.flush(emptyPage(policy.recordsPerPage));
    fixture.detectChanges();
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVAL
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads the tenant policy from its own relative address, with no query at all', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL, 'the policy read');

      // The policy is tenant-wide and the tenant is resolved by the server from the request,
      // so there is nothing to name in the address and nothing to narrow with.
      expect(read.request.params.keys()).withContext('no query parameters').toHaveSize(0);
      expect(read.request.body).withContext('a read carries no body').toBeNull();

      read.flush(envelope(settings()));
      fixture.detectChanges();
    });

    it('announces the wait and withholds the form until the policy has arrived', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL);

      // ⚠ THE FORM IS WITHHELD RATHER THAN DISABLED. Until the server's policy has been
      // applied the controls hold seated defaults, and showing them would invite an operator
      // to submit values nobody chose.
      expect(query('form.membership-settings')).withContext('no form yet').toBeNull();

      const spinner = query('app-loading-spinner');

      expect(spinner).withContext('the wait is drawn').not.toBeNull();
      expect((spinner as Element).textContent ?? '').toContain(LOADING_LABEL);

      read.flush(envelope(settings()));
      fixture.detectChanges();

      expect(query('form.membership-settings')).withContext('the form replaces the wait').not.toBeNull();
      expect(query('app-loading-spinner')).withContext('the wait is gone').toBeNull();
    });

    it('paints the recovered title, subtitle, section heading and column legend', () => {
      arrive();

      const markup = host().textContent ?? '';

      expect(markup).toContain(TITLE);
      expect(markup).toContain(SUBTITLE);
      // "User Accounts Settings" keeps its legacy plural; correcting it would change text an
      // existing administrator recognises for no behavioural gain.
      expect(query('.membership-settings__section-heading')?.textContent?.trim()).toBe(
        SECTION_HEADING,
      );
      expect(query('.membership-settings__columns-legend')?.textContent?.trim()).toBe(
        COLUMNS_HEADING,
      );
    });

    it('links to the companion profile-declaration screen from the header action slot', () => {
      arrive();

      const link = query<HTMLAnchorElement>('a.membership-settings__header-action');

      expect(link).withContext('the header action is offered').not.toBeNull();
      expect((link as HTMLAnchorElement).textContent?.trim()).toBe('Manage Profile Properties');
      expect((link as HTMLAnchorElement).getAttribute('href')).toBe(PROFILE_DEFINITIONS_PATH);
    });

    it('seats every one of the twenty-three controls from the policy the server sent', () => {
      const policy = settings();

      arrive(policy);

      // The nine listing switches, each asserted individually: the fixture deliberately
      // alternates them, so a form that kept its own seated defaults cannot pass this.
      expect(field<HTMLInputElement>('columnFirstName').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnLastName').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnDisplayName').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnAddress').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnTelephone').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnEmail').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnCreatedDate').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnLastLogin').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnAuthorized').checked).toBeFalse();

      expect(field<HTMLSelectElement>('displayMode').value).toContain('1');
      expect(field<HTMLInputElement>('displaySuppressPager').checked).toBeTrue();
      expect(field<HTMLInputElement>('recordsPerPage').value).toBe('25');

      expect(field<HTMLSelectElement>('profileDefaultVisibility').value).toContain('0');
      expect(field<HTMLInputElement>('profileDisplayVisibility').checked).toBeFalse();
      expect(field<HTMLInputElement>('profileManageServices').checked).toBeFalse();

      // ⚠ ZERO IS A REAL PAGE. It must be seated as the digit zero, never as an empty field.
      expect(field<HTMLInputElement>('redirectAfterLogin').value)
        .withContext('page zero is a page')
        .toBe('0');
      expect(field<HTMLInputElement>('redirectAfterRegistration').value)
        .withContext('null is the only expression of "no redirect"')
        .toBe('');
      expect(field<HTMLInputElement>('redirectAfterLogout').value).toBe('42');

      expect(field<HTMLTextAreaElement>('securityEmailValidation').value).toBe(
        policy.securityEmailValidation,
      );
      expect(field<HTMLInputElement>('securityRequireValidProfile').checked).toBeTrue();
      expect(field<HTMLInputElement>('securityRequireValidProfileAtLogin').checked).toBeFalse();
      expect(field<HTMLSelectElement>('securityUsersControl').value).toContain('1');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toBe(
        policy.securityDisplayNameFormat,
      );
    });

    it('stands on the measured legacy defaults when the tenant has recorded no policy', () => {
      // A success carrying nothing in the envelope is the contract's way of saying the tenant
      // has never saved a policy. The legacy routine filled in a missing key the same way.
      arrive(null);

      expect(query('form.membership-settings')).withContext('the form is still offered').not.toBeNull();

      // The measured legacy defaults, transcribed from `UserModuleBase.vb` L98-L190. Two are
      // counter-intuitive and are asserted precisely because of it: the electronic mail column
      // defaults to HIDDEN while the address column defaults to SHOWN.
      expect(field<HTMLInputElement>('columnEmail').checked)
        .withContext('hidden by default — measured, not a slip')
        .toBeFalse();
      expect(field<HTMLInputElement>('columnAddress').checked)
        .withContext('shown by default')
        .toBeTrue();
      expect(field<HTMLInputElement>('recordsPerPage').value).toBe('10');
      // A valid profile is required at sign-in but NOT at registration. Also measured.
      expect(field<HTMLInputElement>('securityRequireValidProfile').checked).toBeFalse();
      expect(field<HTMLInputElement>('securityRequireValidProfileAtLogin').checked).toBeTrue();
      // The three redirects are empty rather than minus one: the server refuses a negative
      // identifier outright, so the legacy marker would turn a valid policy into a rejection.
      expect(field<HTMLInputElement>('redirectAfterLogin').value).toBe('');
      expect(field<HTMLInputElement>('redirectAfterRegistration').value).toBe('');
      expect(field<HTMLInputElement>('redirectAfterLogout').value).toBe('');
    });

    it('does not overwrite edits in hand when a policy arrives late', () => {
      arrive(settings());

      type('recordsPerPage', '7');

      // A second read lands — a redraw provoked by another screen, say — and must not discard
      // work the operator can see.
      TestBed.inject(UserStore).loadMembershipSettings();
      fixture.detectChanges();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settings({ recordsPerPage: 90 })));
      fixture.detectChanges();

      expect(field<HTMLInputElement>('recordsPerPage').value)
        .withContext('edits in hand outrank a late arrival')
        .toBe('7');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE TWO CLOSED LEGACY SECTIONS
  // ---------------------------------------------------------------------------------------------------

  describe('the sections that are deliberately closed', () => {
    /**
     * ⚠ PROVIDER CLOSURE. Ten provider fields and two password-aging fields existed on the
     * legacy screen and have NO member on this contract, so there is nothing for a control to
     * bind to. Their absence is asserted rather than assumed, because a later reader searching
     * for them needs to find a case saying they are gone on purpose.
     */
    it('renders no membership-provider field, because the contract carries none', () => {
      arrive();

      const absent: readonly string[] = [
        'passwordFormat',
        'requiresQuestionAndAnswer',
        'minRequiredPasswordLength',
        'minRequiredNonAlphanumericCharacters',
        'passwordStrengthRegularExpression',
        'maxInvalidPasswordAttempts',
        'passwordAttemptWindow',
        'enablePasswordReset',
        'enablePasswordRetrieval',
        'requiresUniqueEmail',
      ];

      for (const name of absent) {
        expect(query(`#membership-setting-${name}`))
          .withContext(`no provider control for ${name}`)
          .toBeNull();
      }
    });

    it('renders no password-aging field, because the contract carries none', () => {
      arrive();

      expect(query('#membership-setting-passwordExpiry')).toBeNull();
      expect(query('#membership-setting-passwordExpiryReminder')).toBeNull();

      const markup = host().textContent ?? '';

      // The two dropped legacy headings must not appear anywhere, and neither must the legacy
      // provider sentence claiming a configuration file has to be edited — that file does not
      // exist in the target, so the sentence is not merely unhelpful, it is false.
      expect(markup).not.toContain('Membership Provider Settings');
      expect(markup).not.toContain('Password Aging Settings');
      expect(markup).not.toContain('web.config');
    });

    it('offers exactly one section, open on arrival, whose state is announced', () => {
      arrive();

      const sections = queryAll<HTMLDetailsElement>('details.membership-settings__section');

      expect(sections).withContext('one surviving section').toHaveSize(1);
      expect(sections[0]?.open).withContext('open on arrival, as all three legacy sections were').toBeTrue();

      const summary = query('.membership-settings__section-summary');

      // The announced state is bound from the element itself, so it cannot drift from what is
      // rendered. The legacy toggle was withdrawn from the tab order; a native summary is in it.
      expect(summary?.getAttribute('aria-expanded')).toBe('true');
      expect(summary?.querySelector('h2')).withContext('the heading keeps its outline position').not.toBeNull();
    });

    it('offers no credential-policy control, because none of that policy crosses the boundary', () => {
      arrive();

      // Minimum length, the non-alphanumeric requirement and address uniqueness are server-side
      // options. A copy in the browser would be free to contradict what is actually enforced.
      const markup = host().textContent ?? '';

      expect(markup).not.toContain('Minimum Password Length');
      expect(markup).not.toContain('Password Retrieval');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE ENTRY RULES
  // ---------------------------------------------------------------------------------------------------

  describe('the entry rules', () => {
    it('bounds the page size in the document as well as in the rule', () => {
      arrive();

      const control = field<HTMLInputElement>('recordsPerPage');

      // The browser's own bounds mirror the server's validator, which turns a round trip into
      // an immediate answer without moving the authority.
      expect(control.getAttribute('type')).toBe('number');
      expect(control.getAttribute('min')).toBe(String(MINIMUM_RECORDS_PER_PAGE));
      expect(control.getAttribute('max')).toBe(String(MAXIMUM_RECORDS_PER_PAGE));
      expect(control.getAttribute('aria-required')).withContext('required is announced').toBe('true');
    });

    it('refuses an emptied page size in its own wording and sends nothing', () => {
      arrive();

      type('recordsPerPage', '');
      submitForm();

      expect(fieldErrors()).toContain(REQUIRED_MESSAGE);
      expect(field<HTMLInputElement>('recordsPerPage').getAttribute('aria-invalid')).toBe('true');
      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
    });

    it('refuses a page size below the floor and names the range', () => {
      arrive();

      type('recordsPerPage', '0');
      submitForm();

      // The sentence deliberately reads the way the server's own does: one situation should not
      // be described two different ways depending on which side noticed it.
      expect(fieldErrors()).toContain(PAGE_SIZE_RANGE_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('refuses a page size above the ceiling on the same rule', () => {
      arrive();

      type('recordsPerPage', '101');
      submitForm();

      expect(fieldErrors()).toContain(PAGE_SIZE_RANGE_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('accepts both ends of the permitted page-size range', () => {
      arrive();

      type('recordsPerPage', String(MINIMUM_RECORDS_PER_PAGE));

      expect(fieldErrors()).withContext('the floor is permitted').not.toContain(PAGE_SIZE_RANGE_MESSAGE);

      type('recordsPerPage', String(MAXIMUM_RECORDS_PER_PAGE));

      expect(fieldErrors()).withContext('the ceiling is permitted').not.toContain(PAGE_SIZE_RANGE_MESSAGE);
    });

    it('refuses a negative page identifier on each of the three redirects', () => {
      arrive();

      for (const name of [
        'redirectAfterLogin',
        'redirectAfterRegistration',
        'redirectAfterLogout',
      ]) {
        expect(field<HTMLInputElement>(name).getAttribute('min'))
          .withContext(`${name} declares the floor`)
          .toBe('0');

        type(name, '-1');
        submitForm();

        // ⚠ MINUS ONE IS THE LEGACY MARKER FOR "NO INTEGER" AND IS REFUSED HERE. The server's
        // floor is zero because the page table's identity seeds at zero.
        expect(fieldErrors()).withContext(`${name} is refused`).toContain(NEGATIVE_PAGE_MESSAGE);
        expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);

        type(name, '');
      }
    });

    it('accepts page zero on a redirect, because zero is a real page', () => {
      arrive(settings({ redirectAfterLogin: null }));

      type('redirectAfterLogin', '0');

      expect(fieldErrors()).not.toContain(NEGATIVE_PAGE_MESSAGE);

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      expect((write.request.body as MembershipSettings).redirectAfterLogin)
        .withContext('page zero travels as zero, never as null')
        .toBe(0);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });

    it('bounds both text settings in the document at the length the server stores', () => {
      arrive();

      expect(field<HTMLTextAreaElement>('securityEmailValidation').getAttribute('maxlength')).toBe(
        String(MAXIMUM_SETTING_LENGTH),
      );
      expect(field<HTMLInputElement>('securityDisplayNameFormat').getAttribute('maxlength')).toBe(
        String(MAXIMUM_SETTING_LENGTH),
      );
    });

    it('refuses an over-long setting when one reaches the control past the document bound', () => {
      arrive();

      // ⚠ THE DOCUMENT BOUND MAKES THIS UNREACHABLE BY TYPING, so the value is written through
      // the control itself. The rule still has to exist, because a value can arrive by paste
      // handling, by autofill or from a policy the server already holds.
      const control = field<HTMLTextAreaElement>('securityEmailValidation');
      const overLong = 'x'.repeat(MAXIMUM_SETTING_LENGTH + 1);

      control.value = overLong;
      control.dispatchEvent(new Event('input'));
      control.dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      submitForm();

      expect(fieldErrors()).toContain(LENGTH_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('says nothing about validity before a person has acted', () => {
      arrive(settings({ recordsPerPage: 25 }));

      // Every message is withheld while its control is untouched, so a screen that has just
      // opened does not accuse an operator of anything.
      expect(fieldErrors()).toHaveSize(0);
      expect(queryAll('[aria-invalid="true"]')).toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — THE WRITE
  // ---------------------------------------------------------------------------------------------------

  describe('writing the policy', () => {
    it('replaces the whole policy with all twenty-three members and answers 204', () => {
      const policy = settings();

      arrive(policy);
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL, 'the policy write');
      const body = write.request.body as MembershipSettings;

      // ⚠ TWENTY-THREE MEMBERS, EVERY TIME. "Unchanged settings keep their value" is expressed
      // by sending them all, so a zero, a false and an empty string are values being asserted
      // rather than absences to be filtered out.
      expect(Object.keys(body as unknown as Record<string, unknown>).sort()).toEqual(
        Object.keys(policy as unknown as Record<string, unknown>).sort(),
      );
      expect(body).toEqual(policy);

      // Each member is sent as its own type. The legacy handler coerced every value to text on
      // the way to storage, so a switch was stored as the word for true and a count as digits.
      expect(typeof body.columnEmail).withContext('a switch is a switch').toBe('boolean');
      expect(typeof body.recordsPerPage).withContext('a count is a number').toBe('number');
      expect(typeof body.displayMode).withContext('a mode is a number').toBe('number');

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });

    it('sends every falsy value as the value it is, never as an omission', () => {
      const policy = settings();

      arrive(policy);

      // Turn every switch off, zero the display mode, empty both text settings and clear all
      // three redirects. A body assembled by filtering would drop the lot.
      for (const name of [
        'columnFirstName',
        'columnLastName',
        'columnDisplayName',
        'columnAddress',
        'columnTelephone',
        'columnEmail',
        'columnCreatedDate',
        'columnLastLogin',
        'columnAuthorized',
        'displaySuppressPager',
        'profileDisplayVisibility',
        'profileManageServices',
        'securityRequireValidProfile',
        'securityRequireValidProfileAtLogin',
      ]) {
        toggle(name, false);
      }

      choose('displayMode', 'All accounts');
      type('securityEmailValidation', '');
      type('securityDisplayNameFormat', '');
      type('redirectAfterLogin', '');
      type('redirectAfterRegistration', '');
      type('redirectAfterLogout', '');

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);
      const body = write.request.body as MembershipSettings;

      expect(body.columnEmail).withContext('false is asserted, not omitted').toBeFalse();
      expect(body.columnAddress).toBeFalse();
      expect(body.displaySuppressPager).toBeFalse();
      expect(body.profileDisplayVisibility).toBeFalse();
      expect(body.securityRequireValidProfileAtLogin).toBeFalse();
      expect(body.displayMode).withContext('zero is a display mode').toBe(0);
      expect(body.securityEmailValidation).withContext('empty text is a value').toBe('');
      expect(body.securityDisplayNameFormat).toBe('');
      expect(body.redirectAfterLogin).withContext('null is "no redirect"').toBeNull();
      expect(body.redirectAfterRegistration).toBeNull();
      expect(body.redirectAfterLogout).toBeNull();
      expect(Object.keys(body as unknown as Record<string, unknown>)).toHaveSize(23);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings({ displayMode: 0 }));
    });

    it('re-reads the policy and the listing once the write has landed', () => {
      arrive(settings({ recordsPerPage: 25 }));

      type('recordsPerPage', '50');
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // ⚠ THE LISTING FOLLOWS BECAUSE THE POLICY DECLARES THE SIZE OF A PAGE. Leaving it alone
      // would show a page whose size contradicts the setting that was just saved.
      answerWriteFollowUp(settings({ recordsPerPage: 50 }));

      expect(TestBed.inject(UserStore).effectivePageSize())
        .withContext('the store now prefers the saved size')
        .toBe(50);
    });

    it('announces success once and leaves for the account listing', () => {
      const policy = settings();

      arrive(policy);
      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(SAVED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH]);
    });

    it('announces the write in flight and withholds the submit command while it runs', () => {
      arrive();
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      fixture.detectChanges();

      const submit = button(SUBMIT_LABEL);

      expect(submit?.disabled).withContext('withheld while saving').toBeTrue();

      const spinner = query('app-loading-spinner');

      expect(spinner).withContext('the write is announced').not.toBeNull();
      expect((spinner as Element).textContent ?? '').toContain(SAVING_LABEL);

      // The way out stays operable while a write is in flight, as the legacy command was.
      expect(button(CANCEL_LABEL)?.disabled).withContext('the way out is never withheld').toBeFalse();

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });

    it('sends nothing at all while the policy is still being read', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL);

      // The submit command is not even rendered yet, so the refusal is proved by driving the
      // form's own submit event — which is the only way an operator could reach it early.
      expect(button(SUBMIT_LABEL)).withContext('not offered while reading').toBeUndefined();

      read.flush(envelope(settings()));
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).withContext('offered once the policy is in hand').toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — A REFUSED READ OR WRITE
  // ---------------------------------------------------------------------------------------------------

  describe('a refused read', () => {
    it('shows the refusal in the shared banner and withholds submission entirely', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(
        problem('user.membership_settings.source_missing', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      const banner = query('app-error-banner');

      expect(banner).withContext('the banner is mounted').not.toBeNull();
      expect(banner?.textContent ?? '').toContain('The requested resource does not exist.');
      // A refusal reads as a WARNING rather than an error, which the shared banner decides from
      // the status. Nothing here classifies it.
      expect(query('.error-banner__severity')?.textContent?.trim().toLowerCase()).toContain('warning');
      // The reference line shows the CORRELATION identifier, which the shared reader prefers.
      expect(query('.error-banner__trace')?.textContent ?? '').toContain(
        '8c7f0f5e-4a52-4f7a-9a3f-1f1c0d7b6a55',
      );
    });

    it('refuses submission after an unreadable policy, so a live policy cannot be overwritten', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // ⚠ THE FORM IS DRAWN — the read has finished, unsuccessfully — SO THE COMMAND MUST BE
      // WITHHELD. Testing only the in-flight flag would satisfy the rule for exactly as long as
      // the request lasted: a refused read clears that flag without ever applying a policy,
      // leaving the seated defaults on screen and submittable over whatever the tenant has.
      const submit = button(SUBMIT_LABEL);

      expect(submit).withContext('the form is drawn').not.toBeUndefined();
      expect((submit as HTMLButtonElement).disabled)
        .withContext('withheld after a refused read')
        .toBeTrue();

      (submit as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(httpMock.match(() => true))
        .withContext('a policy nobody could read is not overwritten')
        .toHaveSize(0);
    });

    it('reopens the command once a retried read succeeds', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).toBeTrue();

      // The store clears the failure slot at the start of every read, so a retry reopens the
      // command with nothing here resetting it.
      TestBed.inject(UserStore).loadMembershipSettings();
      fixture.detectChanges();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settings()));
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).withContext('reopened').toBeFalse();
    });

    it('presents a transport failure in the banner, at the forceful band, with a real sentence', () => {
      create();

      expectRequest('GET', SETTINGS_URL).error(new ProgressEvent('error'), { status: 0, statusText: '' });
      fixture.detectChanges();

      // ⚠ A RESPONSE THAT NEVER ARRIVED STILL CARRIES A STATUS — zero — so the store attaches it
      // to a synthesised document and the BANNER carries the failure. The separate summary
      // paragraph is reserved for a failure with no status at all, which is a value thrown
      // outside a response rather than anything a request can produce; it is therefore absent
      // here, and asserting that is the point of this case.
      expect(query('.membership-settings__transport-failure'))
        .withContext('the paragraph is for a statusless failure, not for status zero')
        .toBeNull();

      const banner = query('app-error-banner');

      expect(banner).withContext('the banner carries it').not.toBeNull();
      expect((banner?.textContent ?? '').length).toBeGreaterThan(0);
      // Status zero is nobody's documented refusal, so it is judged at the most forceful band.
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Error');
    });
  });

  describe('a refused write', () => {
    it('leaves the typed values in place and releases the command for another attempt', () => {
      arrive(settings({ recordsPerPage: 25 }));

      type('recordsPerPage', '50');
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(
        problem('user.membership_settings.invalid', 400, 'The request could not be processed as submitted.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // Nothing follows a refusal: the policy is NOT re-read, so a rejected save cannot silently
      // discard what the operator typed.
      expect(httpMock.match(() => true)).withContext('no follow-up read').toHaveSize(0);
      expect(field<HTMLInputElement>('recordsPerPage').value)
        .withContext('the entry survives for correction')
        .toBe('50');
      expect(button(SUBMIT_LABEL)?.disabled).withContext('released for another attempt').toBeFalse();
      expect(successSpy).withContext('nothing succeeded').not.toHaveBeenCalled();
      expect(navigateSpy).withContext('the operator stays put').not.toHaveBeenCalled();
      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'The request could not be processed as submitted.',
      );
    });

    it('pins a per-field refusal to the control the server named', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem(
          'user.membership_settings.redirect_invalid',
          400,
          'The request could not be processed as submitted.',
          { RedirectAfterLogin: ['That page does not belong to this site.'] },
        ),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // ⚠ THE SERVER'S KEY IS ITS OWN MODEL-STATE SPELLING, NOT CAMEL-CASED. The shared reader
      // matches case-insensitively, which is what makes this land on the right control.
      expect(fieldErrors()).toContain('That page does not belong to this site.');
      expect(field<HTMLInputElement>('redirectAfterLogin').getAttribute('aria-invalid')).toBe('true');
    });

    it('reads a refusal of authority as a warning rather than an error', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(query('.error-banner__severity')?.textContent?.trim().toLowerCase()).toContain('warning');
      expect(notifications()).withContext('the banner carries it, not a transient').toHaveSize(0);
    });

    it('reads a rate-limit refusal at its calmest band', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('request.rate_limited', 429, 'Too many requests have been submitted. Retry after a short delay.'),
        { status: 429, statusText: 'Too Many Requests' },
      );
      fixture.detectChanges();

      // ⚠ THE BANNER INTERCEPTS 429 BEFORE THE DOMAIN RULE, so the word shown is "Please wait"
      // rather than "Warning". The domain severity for 429 IS warning; the banner refines it to
      // its own calm band because a retryable delay is not a refusal to report as one.
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Please wait');
    });

    it('reads a server failure as an error', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('server.unexpected_failure', 500, 'An unexpected error occurred while processing the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(query('.error-banner__severity')?.textContent?.trim().toLowerCase()).toContain('error');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — ABANDONING THE FORM
  // ---------------------------------------------------------------------------------------------------

  describe('abandoning the form', () => {
    it('leaves for the account listing without writing or validating anything', () => {
      arrive();

      // Empty a required field first: abandoning a half-filled form must not first be told the
      // form is half-filled. The legacy cancel command declared validation switched off.
      type('recordsPerPage', '');
      const before: number = fieldErrors().length;

      press(CANCEL_LABEL);

      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
      expect(fieldErrors().length)
        .withContext('no new accusation is raised on the way out')
        .toBeLessThanOrEqual(before);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH]);
    });

    it('clears a recorded failure so the next screen does not inherit this banner', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('user.membership_settings.invalid', 400, 'The request could not be processed as submitted.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(query('app-error-banner')?.textContent ?? '').toContain('could not be processed');

      press(CANCEL_LABEL);

      expect(TestBed.inject(UserStore).failure()).withContext('the slot is cleared').toBeNull();
    });

    it('stays operable while a write is outstanding, because it is the way out', () => {
      arrive();
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH]);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading level below the page title', () => {
      arrive();

      // The shell owns the landmarks; a screen emitting its own would nest one inside another.
      expect(queryAll('main')).toHaveSize(0);
      expect(queryAll('nav')).toHaveSize(0);
      // One section heading, at level two, directly under the page heading with no level skipped.
      expect(queryAll('h2')).toHaveSize(1);
      expect(queryAll('h4')).toHaveSize(0);
    });

    it('names every one of the twenty-three controls with a real label pointing at it', () => {
      arrive();

      const labels = queryAll<HTMLLabelElement>('label.form-field__label');

      expect(labels.length).withContext('a label per field').toBeGreaterThanOrEqual(23);

      for (const label of labels) {
        const target: string | null = label.getAttribute('for');

        expect(target).withContext('every label points somewhere').not.toBeNull();
        expect(query(`#${target}`))
          .withContext(`the control ${String(target)} exists`)
          .not.toBeNull();
      }
    });

    it('renders the recovered legacy wording verbatim, misspellings and punctuation included', () => {
      arrive();

      const markup = host().textContent ?? '';

      // The question mark is the legacy wording. Preserved.
      expect(markup).toContain('Suppress Pager?');
      // Labelled "Name", not "Display Name", in the legacy file. Preserved.
      expect(markup).toContain('Show Name Column');
      expect(markup).toContain('Users per Page');
      expect(markup).toContain('Email Address Validation');
      expect(markup).toContain('Display Name Format');
    });

    it('offers the three selectors with exactly the options the server accepts', () => {
      arrive();

      // The server accepts nothing outside zero to two for the display mode, which is why the
      // list built from the legacy enumeration covers exactly three.
      expect(field<HTMLSelectElement>('displayMode').options).toHaveSize(3);
      expect(field<HTMLSelectElement>('profileDefaultVisibility').options).toHaveSize(3);
      expect(field<HTMLSelectElement>('securityUsersControl').options).toHaveSize(2);

      const usersControl = Array.from(field<HTMLSelectElement>('securityUsersControl').options).map(
        (option) => (option.textContent ?? '').trim(),
      );

      // "Combo Box" and "Text Box" are the legacy resource values, shared keys in the original.
      expect(usersControl).toEqual(['Combo Box', 'Text Box']);
    });

    it('declares the type of both commands so neither submits by accident', () => {
      arrive();

      expect(button(SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      // The legacy cancel command declared validation switched off, so it is never a submit.
      expect(button(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
    });

    it('renders a hostile stored setting as text, with no element parsed out of it', () => {
      const hostile = '<img src=x onerror="window.__dnnSentinel = true">';

      arrive(settings({ securityDisplayNameFormat: hostile }));

      // ⚠ LEGACY WORDING AND STORED SETTINGS ARE UNTRUSTED MARKUP BY MEASUREMENT. Nothing here
      // is bound as trusted markup and no sanitiser is involved.
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value)
        .withContext('carried verbatim as a value')
        .toBe(hostile);
      expect(host().querySelectorAll('img')).withContext('nothing was parsed').toHaveSize(0);
      expect((window as unknown as Record<string, unknown>)['__dnnSentinel']).toBeUndefined();
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      arrive();

      // The live region must exist before it has anything to say, or the first message it
      // carries is not announced at all.
      const live = query('.error-banner-live');

      expect(live).withContext('the region is mounted').not.toBeNull();
      expect(live?.getAttribute('role')).toBe('alert');
      expect(live?.getAttribute('aria-live')).toBe('assertive');
      expect(query('.error-banner__title')).withContext('and says nothing').toBeNull();
    });
  });
});
