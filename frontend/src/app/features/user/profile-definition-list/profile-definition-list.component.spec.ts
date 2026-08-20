/**
 * Specification for {@link ProfileDefinitionListComponent} — the profile-property catalogue at
 * `/settings/profile-definitions`. It is THREE things sharing one surface: an unpaged grid, a batch of
 * staged edits that are only written when the operator says so, and an inline editor that both creates
 * and replaces.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import { ProfileDefinitionListComponent } from './profile-definition-list.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { ProfilePropertyDefinition } from '../../../core/models/profile.model';

// ADDRESSES

const DEFINITIONS_URL = '/api/v1/profile-definitions';

function definitionUrl(propertyDefinitionId: number): string {
  return `${DEFINITIONS_URL}/${propertyDefinitionId}`;
}

/**
 * Where an ORDERING change is submitted.
 *
 * ⚠ NOT A DECLARATION ROUTE. A move EXCHANGES the stored positions of two declarations, so the two writes
 * are only correct together — land one and lose the other and both rows claim the same position. The set is
 * therefore submitted here and committed as one unit of work.
 */
const ORDER_URL = `${DEFINITIONS_URL}/order`;

// The wording this screen publishes

const PAGE_TITLE = 'Manage Profile Properties';
const ADD_LABEL = 'Add New Profile Property';
const APPLY_LABEL = 'Apply Changes';
const REFRESH_LABEL = 'Refresh Grid';
const CANCEL_LABEL = 'Return to Profile Properties List';
const CREATE_HEADING = 'Add New Property Details';
const EDIT_HEADING = 'Edit Property Details';
const CREATE_SUBMIT_LABEL = 'Create New Property';
const EDIT_SUBMIT_LABEL = 'Update Property';
const REMOVAL_MESSAGE = 'Are You Sure You Wish To Delete This Item?';
const NAME_REQUIRED_MESSAGE = 'The Property Name is required';
const NAME_PATTERN_MESSAGE = 'The property name cannot contain spaces';
const DATA_TYPE_REQUIRED_MESSAGE = 'The Data Type is required';

/** Authored wording for the two numeric fields that had no absence rule at all. */
const LENGTH_REQUIRED_MESSAGE = 'The Length is required';
const VIEW_ORDER_REQUIRED_MESSAGE = 'The View Order is required';

/** The whole-number sentence, restated so a case can prove it is NOT shown for an empty box. */
const WHOLE_NUMBER_MESSAGE = 'Enter a whole number.';

/** The API's own sentence for an over-long expression, reproduced by the client. */
const EXPRESSION_TOO_LONG_MESSAGE = 'Validation Expression must be 512 characters or fewer';

/** `DuplicateName.Text`, double spaces included. */
const DUPLICATE_NAME_MESSAGE =
  'This Property already exists.  Property Names must be unique.  Please select a different ' +
  'name for this property.';

/** The eight headings the legacy published, plus the four it left empty. */
const DATA_HEADINGS = [
  'Name',
  'Category',
  'DataType',
  'Length',
  'Default Value',
  'Validation Expression',
  'Required',
  'Visible',
];

/** The four command columns, whose headings the legacy resource file left as `<value />`. */
const COMMAND_HEADINGS = ['Edit', 'Delete', 'Move Down', 'Move Up'];

/** The four names whose delete command the legacy hid. */
const UNDELETABLE = ['LastName', 'FirstName', 'TimeZone', 'PreferredLocale'];

const GRID_CAPTION = 'Profile properties declared for this site';

const FIELD_LABELS = [
  'Property Name:',
  'Data Type:',
  'Property Category:',
  'Length:',
  'Default Value:',
  'Validation Expression:',
  'Required:',
  'Visible:',
  'View Order:',
];

/**
 * The nine control identifiers in the SAME order, so the rendered order can be asserted against the
 * authoritative `SortOrder` sequence — name 0, data type 1, category 2, length 3, default value 4,
 * expression 5, required 6, visible 7, view order 8.
 */
const FIELD_CONTROL_IDS = [
  'profile-definition-name',
  'profile-definition-data-type',
  'profile-definition-category',
  'profile-definition-length',
  'profile-definition-default-value',
  'profile-definition-expression',
  'profile-definition-required',
  'profile-definition-visible',
  'profile-definition-view-order',
];

/**
 * `ProfilePropertyDefinition_PropertyCategory.Help`, reproduced byte for byte. The misspelling is the
 * product's and is preserved (mcc-1).
 */
const CATEGORY_HELP_WITH_LEGACY_TYPO =
  'Enter the category for this property.  This will allow the related properties to be ' +
  'grouped when dislayed to the user.';

/** The sentence the component publishes for a declaration that has already been removed. */
const DEFINITION_GONE_MESSAGE = 'That profile property no longer exists. The list has been refreshed.';

const NON_BROWSABLE_LABELS = ['Is Dirty', 'Module Def', 'Portal', 'Visibility'];

describe('ProfileDefinitionListComponent', () => {
  let fixture: ComponentFixture<ProfileDefinitionListComponent>;
  let httpMock: HttpTestingController;
  let notify: jasmine.Spy;

  // FIXTURES

  function definition(
    overrides: Partial<ProfilePropertyDefinition> = {},
  ): ProfilePropertyDefinition {
    return {
      propertyDefinitionId: 1,
      portalId: 0,
      moduleDefId: null,
      dataType: 349,
      defaultValue: '',
      propertyCategory: 'Name',
      propertyName: 'FirstName',
      length: 0,
      required: false,
      validationExpression: '',
      viewOrder: 0,
      visible: true,
      visibility: 2,
      ...overrides,
    };
  }

  /** Three declarations in position order, none of them one of the four undeletable names. */
  function catalogue(): readonly ProfilePropertyDefinition[] {
    return [
      definition({ propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0 }),
      definition({ propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1 }),
      definition({ propertyDefinitionId: 13, propertyName: 'Biography', viewOrder: 2 }),
    ];
  }

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  function problem(status: number, type?: string): ProblemDetails {
    return {
      type,
      title: 'Request refused',
      status,
      detail: 'The server refused the request.',
    };
  }

  // HARNESS

  beforeEach(async () => {
    // Order is load-bearing: the real client FIRST, then the testing backend that displaces it. Reversing
    // the two leaves the live backend in place and every expectation times out.
    await TestBed.configureTestingModule({
      imports: [ProfileDefinitionListComponent],
      // The store is listed so each case gets its own instance. It is `providedIn: 'root'`, so without this
      // every case would share one catalogue and one failure slot.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), UserStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notify = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** Creates the component and runs the first change detection. */
  function create(): void {
    fixture = TestBed.createComponent(ProfileDefinitionListComponent);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, label = 'a request'): TestRequest {
    const matches = httpMock.match((candidate) => candidate.url === url);
    const wanted = matches.filter((candidate) => candidate.request.method === method);

    expect(wanted.length)
      .withContext(`expected exactly one ${method} ${url} for ${label}`)
      .toBe(1);

    const found = wanted.at(0);

    if (found === undefined) {
      throw new Error(`no ${method} ${url} was pending for ${label}`);
    }

    return found;
  }

  /** Consumes every pending write against a definition, in the order they were issued. */
  function pendingWrites(method: string): readonly TestRequest[] {
    return httpMock.match(
      (candidate) =>
        candidate.method === method && candidate.url.startsWith(`${DEFINITIONS_URL}/`),
    );
  }

  /** What one row of a driven Apply batch asked for, recorded before it was answered. */
  interface DrivenBatchWrite {
    readonly url: string;
    readonly body: unknown;
  }

  /**
   * Drives a whole Apply batch to completion, answering each row in turn. ⚠ APPLY WRITES ONE ROW AT A
   * TIME, SO A BATCH CANNOT BE MATCHED IN ONE CALL. The screen keeps exactly one replace outstanding and
   * dispatches the next only once that one has settled, which is what makes every row's outcome
   * observable: the store publishes ONE settled result at a time, so two answers landing in the same turn
   * would coalesce and the earlier row's outcome would never reach the reporting effect.
   *
   * @param answer Answers one row.
   * @param rows The catalogue to answer the surviving re-read with, when the batch provoked one.
   * @returns What each row asked for, in dispatch order.
   */
  function driveBatch(
    answer: (write: TestRequest, index: number) => void,
    rows: readonly ProfilePropertyDefinition[] = catalogue(),
  ): readonly DrivenBatchWrite[] {
    const driven: DrivenBatchWrite[] = [];

    for (;;) {
      const outstanding: readonly TestRequest[] = pendingWrites('PUT');

      if (outstanding.length === 0) {
        break;
      }

      expect(outstanding.length)
        .withContext('a serialised batch keeps exactly one replace outstanding')
        .toBe(1);

      const write: TestRequest = outstanding[0];

      driven.push({ url: write.request.url, body: write.request.body });
      answer(write, driven.length - 1);
      fixture.detectChanges();
    }

    drainReReads(rows);

    return driven;
  }

  /**
   * Drains every catalogue re-read outstanding, tolerating there being none. A batch whose every row was
   * refused provokes no re-read at all, so unlike {@link settleReReads} this makes no claim that one was
   * made.
   *
   * @param rows The catalogue to answer the surviving read with.
   */
  function drainReReads(rows: readonly ProfilePropertyDefinition[] = catalogue()): void {
    const reads = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
    );

    for (const read of reads) {
      if (!read.cancelled) {
        read.flush(envelope(rows));
      }
    }

    fixture.detectChanges();
  }

  /** Mounts the screen and answers its one read with the supplied catalogue. */
  function arrive(rows: readonly ProfilePropertyDefinition[] = catalogue()): void {
    create();

    // ⚠ THE CATALOGUE READ IS OPTIONAL ON ARRIVAL, AND THAT IS THE BEHAVIOUR UNDER TEST ELSEWHERE. The
    // declarations are a tenant-wide catalogue that changes only when an operator edits it, and re-asking for
    // them on every arrival was measured as a defect. The store outlives the component, so a case that
    // arrives more than once - the pattern probes below arrive once per candidate - sees the read on the
    // first arrival only. Where a case needs the catalogue to change, it drives the explicit refresh.
    const reads = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
    );

    expect(reads.length)
      .withContext('at most one catalogue read per arrival, and none once it is already held')
      .toBeLessThanOrEqual(1);

    reads.at(0)?.flush(envelope(rows));
    fixture.detectChanges();
  }

  /** @param rows The catalogue to answer the read with. */
  function settleReReads(rows: readonly ProfilePropertyDefinition[] = catalogue()): void {
    const reads = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
    );

    expect(reads.length).withContext('exactly one catalogue re-read per batch').toBe(1);

    reads[0].flush(envelope(rows));
    fixture.detectChanges();
  }

  /**
   * Drives a staged batch to completion, one replace at a time. ⚠ THE BOUND IS WHAT THIS HELPER PROVES. A
   * batch issues its next replace only once the previous one has settled, so at every step there is
   * EXACTLY ONE write in flight however many rows were staged. It returns the addresses in the order they
   * were written, so a case can assert the order as well as the count.
   *
   * @param expected How many replaces the batch should consist of.
   * @param answer Answers one replace, defaulting to acceptance.
   * @returns The addresses written, in order.
   */
  function settleBatch(
    expected: number,
    answer: (write: TestRequest, step: number) => void = (write) =>
      write.flush(envelope(definition())),
  ): readonly string[] {
    const written: string[] = [];

    for (let step = 0; step < expected; step += 1) {
      const inFlight = pendingWrites('PUT');

      expect(inFlight.length)
        .withContext(`exactly one replace in flight at step ${String(step)}`)
        .toBe(1);

      written.push(inFlight[0].request.url);
      answer(inFlight[0], step);
      fixture.detectChanges();
    }

    // Nothing beyond the staged rows is written: the batch is the pending set and no more.
    expect(pendingWrites('PUT').length).withContext('no further replace follows the batch').toBe(0);

    return written;
  }

  /** Accepts every replace of a batch, without inspecting any of them. */
  function settleReplaces(): void {
    let inFlight = pendingWrites('PUT');

    while (inFlight.length > 0) {
      for (const write of inFlight) {
        write.flush(envelope(definition()));
      }

      fixture.detectChanges();
      inFlight = pendingWrites('PUT');
    }
  }

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  function query<T extends Element>(selector: string): readonly T[] {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<T>(selector));
  }

  /** Every button whose rendered text contains the wording, in document order. */
  function buttonsNamed(wording: string): readonly HTMLButtonElement[] {
    return query<HTMLButtonElement>('button').filter((button) =>
      (button.textContent ?? '').includes(wording),
    );
  }

  /** Exactly one button by its rendered wording. */
  function button(wording: string): HTMLButtonElement {
    const found = buttonsNamed(wording);

    expect(found.length).withContext(`expected exactly one "${wording}" button`).toBe(1);

    const first = found.at(0);

    if (first === undefined) {
      throw new Error(`no button named "${wording}"`);
    }

    return first;
  }

  function press(wording: string): void {
    button(wording).click();
    fixture.detectChanges();
  }

  /** A button inside the removal dialog, by its rendered wording. */
  function dialogButton(wording: string): HTMLButtonElement {
    const found = query<HTMLButtonElement>('app-confirm-dialog button').filter((candidate) =>
      (candidate.textContent ?? '').includes(wording),
    );

    expect(found.length).withContext(`exactly one dialog button named "${wording}"`).toBe(1);

    const first = found.at(0);

    if (first === undefined) {
      throw new Error(`the dialog has no button named "${wording}"`);
    }

    return first;
  }

  function pressDialog(wording: string): void {
    dialogButton(wording).click();
    fixture.detectChanges();
  }

  /** The property names in the order the grid renders them. */
  /**
   * The catalogue as a server that ACCEPTED the given edits would report it back. ⚠ THIS MATTERS FOR ANY
   * CASE THAT ASSERTS ON THE STAGED COUNT AFTER A BATCH. The count is derived from the DIFFERENCE between
   * what is staged and what the server holds, so answering the re-read with the untouched catalogue
   * leaves every applied row still counting as outstanding - the fake server has to have honoured the
   * write for the screen to be able to tell that it landed.
   */
  function catalogueWith(
    applied: Readonly<Record<number, Partial<ProfilePropertyDefinition>>>,
  ): readonly ProfilePropertyDefinition[] {
    return catalogue().map((row) => ({ ...row, ...(applied[row.propertyDefinitionId] ?? {}) }));
  }

  /** Where the screen's own polite status region lives, beside the Apply command it reports on. */
  const STATUS_REGION = 'p.profile-definitions__grid-actions [role="status"][aria-live="polite"]';

  /** The screen's permanently mounted polite status region. */
  function statusRegion(): HTMLElement {
    const found = query<HTMLElement>(STATUS_REGION);

    expect(found.length).withContext('exactly one polite status region in the action row').toBe(1);

    return found[0];
  }

  function renderedNames(): readonly string[] {
    return query<HTMLTableRowElement>('tbody tr').map((row) => {
      const cells = Array.from(row.querySelectorAll('td,th'));

      // Column four is the name; the four commands come first.
      return (cells.at(4)?.textContent ?? '').trim();
    });
  }

  /** The grid's own check box for a column, one per row, in rendered order. */
  function rowCheckboxes(column: 'required' | 'visible'): readonly HTMLInputElement[] {
    const offset = column === 'required' ? 10 : 11;

    return query<HTMLTableRowElement>('tbody tr').flatMap((row) => {
      const cell = Array.from(row.querySelectorAll('td,th')).at(offset);
      const box = cell?.querySelector<HTMLInputElement>('input[type="checkbox"]');

      return box === null || box === undefined ? [] : [box];
    });
  }

  /** The two bulk toggles, which sit outside the table. */
  function bulkToggles(): readonly HTMLInputElement[] {
    return query<HTMLInputElement>('.profile-definitions__bulk input[type="checkbox"]');
  }

  function toggle(box: HTMLInputElement | undefined): void {
    if (box === undefined) {
      throw new Error('the expected check box was not rendered');
    }

    box.click();
    fixture.detectChanges();
  }

  function control(id: string): HTMLInputElement | HTMLTextAreaElement {
    const found = (fixture.nativeElement as HTMLElement).querySelector<
      HTMLInputElement | HTMLTextAreaElement
    >(`#${id}`);

    if (found === null) {
      throw new Error(`the control #${id} was not rendered`);
    }

    return found;
  }

  function type(id: string, value: string): void {
    const field = control(id);
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function check(id: string, value: boolean): void {
    const field = control(id);

    if (!(field instanceof HTMLInputElement)) {
      throw new Error(`#${id} is not a check box`);
    }

    if (field.checked !== value) {
      field.click();
      fixture.detectChanges();
    }
  }

  /** Fills the create form with a valid declaration. */
  function fillValidForm(name = 'Twitter'): void {
    type('profile-definition-name', name);
    type('profile-definition-data-type', '349');
    type('profile-definition-category', 'Contact');
    fixture.detectChanges();
  }

  // ARRIVAL

  describe('arrival', () => {
    it('issues one unpaged read carrying no page coordinate and no tenant argument', () => {
      create();

      const read = expectRequest('GET', DEFINITIONS_URL, 'the catalogue read');

      expect(read.request.params.keys().length).toBe(0);
      read.flush(envelope(catalogue()));
      fixture.detectChanges();

      expect(text()).toContain(PAGE_TITLE);
    });

    it('renders the eight data headings the legacy published', () => {
      arrive();

      const headings = query<HTMLTableCellElement>('thead th').map((cell) =>
        (cell.textContent ?? '').trim(),
      );

      for (const heading of DATA_HEADINGS) {
        expect(headings.some((rendered) => rendered.includes(heading)))
          .withContext(`heading "${heading}"`)
          .toBeTrue();
      }
    });

    // ⚠ A PROPERTY NAME IS ONE TOKEN, AND THIS GRID GIVES EVERY COLUMN THE SAME NARROW SHARE. No percentage is
    // declared anywhere here and the four command columns ask for `min-content`, which wins nothing under a
    // fixed table layout - so all twelve tracks collapse to an even one-twelfth, 80px at a 768 viewport.
    // Measured before this: `PostalCode` painted `PostalCod` + `e`. The nine-character names survived; the
    // ten-character one did not.
    it('keeps the property name whole instead of breaking it mid-word', () => {
      arrive();

      const headers: readonly HTMLTableCellElement[] = query<HTMLTableCellElement>('thead th');

      expect(headers[4]?.getAttribute('data-atomic'))
        .withContext('a property name is one token')
        .toBe('true');

      // ⚠ THE COUNTERPART. The category beside it is a short controlled word that never fractured, and the
      // columns after it hold values a reader may want wrapped rather than shortened, so the flag is applied
      // to the name alone rather than across the grid.
      expect(headers[5]?.getAttribute('data-atomic'))
        .withContext('the category was measured clean and is left to wrap')
        .toBeNull();
    });

    it('declares twelve columns, four of them commands whose heading the legacy left empty', () => {
      arrive();

      // The count is the legacy's own, proven three ways in the component's own notes.
      expect(query<HTMLTableCellElement>('thead th').length).toBe(12);

      // Each command column keeps a real accessible name while painting nothing, so the heading text
      // is present in the document but visually hidden.
      const hidden = query<HTMLTableCellElement>('thead th')
        .filter((cell) => cell.querySelector('.data-table__label--hidden') !== null)
        .map((cell) => (cell.textContent ?? '').trim());

      for (const heading of COMMAND_HEADINGS) {
        expect(hidden.some((rendered) => rendered.includes(heading)))
          .withContext(`hidden command heading "${heading}"`)
          .toBeTrue();
      }
    });

    it('renders the help paragraph verbatim, double spaces included', () => {
      arrive();

      expect(text()).toContain(
        'You can change the order of the profile fields, and whether they are Required or ' +
          'Visible on this screen.  Click on the "Apply Changes" button to save any changes ' +
          'you make.',
      );
    });

    it('orders the rows by position, not by the order the array arrived in', () => {
      arrive([
        definition({ propertyDefinitionId: 21, propertyName: 'Third', viewOrder: 7 }),
        definition({ propertyDefinitionId: 22, propertyName: 'First', viewOrder: 0 }),
        definition({ propertyDefinitionId: 23, propertyName: 'Second', viewOrder: 3 }),
      ]);

      expect(renderedNames()).toEqual(['First', 'Second', 'Third']);
    });

    it('treats a position of zero as a real value rather than as unset', () => {
      arrive([
        definition({ propertyDefinitionId: 31, propertyName: 'Zeroth', viewOrder: 0 }),
        definition({ propertyDefinitionId: 32, propertyName: 'Next', viewOrder: 1 }),
      ]);

      // A truthiness test on the position would have sorted the zero row to the far end.
      expect(renderedNames()).toEqual(['Zeroth', 'Next']);
    });

    it('never paints the null-integer sentinel in the data-type cell, and says instead that no type was chosen', () => {
      arrive([definition({ propertyDefinitionId: 41, propertyName: 'Unknown', dataType: -1 })]);

      const cell = query<HTMLTableCellElement>('tbody tr td,tbody tr th').at(6);
      const painted = cell?.querySelector('[aria-hidden="true"]');
      const announced = cell?.querySelector('[data-visually-hidden]');

      // The original point of this test, kept: the sentinel must never reach the user.
      expect((cell?.textContent ?? '')).not.toContain('-1');
      // `DisplayDataType` L339-L351 returns `Null.NullString` here, so no type name is asserted. The
      // mark stands in for it, matching how this application renders every other absent value.
      expect(painted?.textContent?.trim()).toBe('\u2014');
      expect(announced?.textContent?.trim()).toBe('no data type chosen');
    });

    it('paints the stored data-type key as a REFERENCE, never bare and never withheld', () => {
      arrive([definition({ propertyDefinitionId: 42, propertyName: 'Typed', dataType: 349 })]);

      const cell = query<HTMLTableCellElement>('tbody tr td,tbody tr th').at(6);
      const painted = cell?.querySelector('[aria-hidden="true"]');
      const announced = cell?.querySelector('[data-visually-hidden]');

      expect(painted?.textContent?.trim())
        .withContext('the reference is visible, prefixed so it cannot be read as a name')
        .toBe('#349');
      expect(painted?.textContent?.trim())
        .withContext('and it is never the bare number the first finding rejected')
        .not.toBe('349');
      expect(painted?.textContent?.trim())
        .withContext('nor the mark, which now means only "nothing stored"')
        .not.toBe('\u2014');
      // The words are unchanged: the reference was always named in the accessibility tree, and still is.
      expect(announced?.textContent?.trim()).toBe('data type reference 349, name unavailable');
      // The hover affordance and the announced sentence are generated from one accessor, so they agree
      // by construction rather than by coincidence.
      expect(painted?.getAttribute('title')).toBe(announced?.textContent?.trim());
    });

    it('never paints the word null for an absent default value or expression', () => {
      arrive([
        definition({
          propertyDefinitionId: 51,
          propertyName: 'Sparse',
          defaultValue: null,
          validationExpression: null,
        }),
      ]);

      expect(text()).not.toContain('null');
      expect(text()).not.toContain('undefined');
    });

    it('renders no pagination, because the read is unpaged', () => {
      arrive();

      expect(query('app-pagination').length).toBe(0);
    });

    it('offers no sortable heading, because the legacy grid declared none', () => {
      arrive();

      expect(query('thead th button').length).toBe(0);
    });
  });

  // The DELETE command's visibility — dl-7

  describe('the delete command', () => {
    it('is withheld for the four properties the platform depends on', () => {
      arrive(
        UNDELETABLE.map((propertyName, index) =>
          definition({ propertyDefinitionId: 60 + index, propertyName, viewOrder: index }),
        ),
      );

      expect(buttonsNamed('Delete').length).toBe(0);
    });

    it('matches those four names without regard to case, as the legacy did', () => {
      arrive([definition({ propertyDefinitionId: 70, propertyName: 'lAsTnAmE', viewOrder: 0 })]);

      expect(buttonsNamed('Delete').length).toBe(0);
    });

    it('is offered for every other property', () => {
      arrive();

      expect(buttonsNamed('Delete').length).toBe(3);
    });

    it('is withheld rather than disabled, so no unavailable action is announced', () => {
      arrive([
        definition({ propertyDefinitionId: 80, propertyName: 'FirstName', viewOrder: 0 }),
        definition({ propertyDefinitionId: 81, propertyName: 'Nickname', viewOrder: 1 }),
      ]);

      const commands = buttonsNamed('Delete');

      expect(commands.length).toBe(1);
      expect(commands.at(0)?.disabled).toBeFalse();
    });
  });

  // REORDERING

  describe('reordering', () => {
    it('withholds move up on the first row and move down on the last', () => {
      arrive();

      expect(buttonsNamed('Move Up').length).toBe(2);
      expect(buttonsNamed('Move Down').length).toBe(2);
    });

    it('exchanges two positions locally and requests nothing at all', () => {
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(renderedNames()).toEqual(['Website', 'Nickname', 'Biography']);
      // No move endpoint exists, so nothing may be requested until Apply.
      httpMock.expectNone(() => true);
    });

    it('stages two rows for one move, and applies them as ONE atomic position write', () => {
      // MIGRATION: THIS TEST USED TO REQUIRE TWO INDEPENDENT REPLACES, and requiring them was the defect.
      // A move EXCHANGES the stored positions of two declarations, so the two writes are only correct
      // together: land the first and lose the second and both rows claim position 1, which is neither the
      // order the operator started from nor the one they asked for. The exchange is now submitted as one
      // request the server commits as one unit of work.
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      // The operator still changed TWO declarations, and the count speaks their terms rather than the
      // wire's: one request does not make it one change.
      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      const inFlight: readonly TestRequest[] = pendingWrites('PUT');

      expect(inFlight.length)
        .withContext('a move is ONE request, not one per moved declaration')
        .toBe(1);
      expect(inFlight[0].request.url)
        .withContext('and it addresses the ordering route, not either declaration')
        .toBe(ORDER_URL);
      expect(inFlight[0].request.body).toEqual({
        positions: [
          { propertyDefinitionId: 11, viewOrder: 1 },
          { propertyDefinitionId: 12, viewOrder: 0 },
        ],
      });

      inFlight[0].flush(envelope(catalogue()));
      fixture.detectChanges();

      expect(pendingWrites('PUT').length)
        .withContext('and no per-declaration replace follows it')
        .toBe(0);

      settleReReads();
    });

    it('carries POSITIONS ONLY on a move, so ordering cannot alter anything else', () => {
      // MIGRATION: THIS TEST USED TO REQUIRE ALL NINE WRITABLE MEMBERS, on the reasoning that the verb
      // replaces. The verb no longer replaces: an ordering request writes exactly one column, and carrying
      // the whole declaration would let a Move Down rename a property, change its data type or drop its
      // validation expression as a side effect of a keystroke — from a body the screen assembled out of
      // whatever it last read rather than out of anything the operator touched.
      arrive([
        definition({
          propertyDefinitionId: 91,
          propertyName: 'Alpha',
          propertyCategory: 'Group',
          dataType: 350,
          defaultValue: 'x',
          length: 40,
          required: true,
          validationExpression: '^a$',
          viewOrder: 0,
          visible: true,
        }),
        definition({ propertyDefinitionId: 92, propertyName: 'Beta', viewOrder: 1 }),
      ]);

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();
      press(APPLY_LABEL);

      const inFlight: readonly TestRequest[] = pendingWrites('PUT');

      expect(inFlight.length).toBe(1);
      expect(inFlight[0].request.url).toBe(ORDER_URL);

      // Two pairs and nothing else. No property name, no data type, no validation expression.
      expect(inFlight[0].request.body).toEqual({
        positions: [
          { propertyDefinitionId: 91, viewOrder: 1 },
          { propertyDefinitionId: 92, viewOrder: 0 },
        ],
      });

      inFlight[0].flush(envelope([
        definition({ propertyDefinitionId: 92, propertyName: 'Beta', viewOrder: 0 }),
        definition({ propertyDefinitionId: 91, propertyName: 'Alpha', viewOrder: 1 }),
      ]));
      fixture.detectChanges();

      settleReReads();
    });

    it('leaves two rows holding the same position unmoved, as the legacy did', () => {
      arrive([
        definition({ propertyDefinitionId: 101, propertyName: 'Same A', viewOrder: 4 }),
        definition({ propertyDefinitionId: 102, propertyName: 'Same B', viewOrder: 4 }),
      ]);

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      // Exchanging equal positions changes nothing, so the order holds and nothing is staged.
      expect(renderedNames()).toEqual(['Same A', 'Same B']);
      expect(button(APPLY_LABEL).disabled).toBeTrue();
    });
  });

  // THE STAGED BATCH

  // ROW IDENTITY ACROSS A STAGED EDIT
  describe('row identity across a staged edit', () => {
    it('keeps the same row element when a checkbox in it is staged', () => {
      arrive();

      const before: HTMLTableRowElement | undefined = query<HTMLTableRowElement>('tbody tr').at(1);

      toggle(rowCheckboxes('required').at(1));
      fixture.detectChanges();

      const after: HTMLTableRowElement | undefined = query<HTMLTableRowElement>('tbody tr').at(1);

      expect(before).toBeDefined();
      expect(after)
        .withContext('the row element must survive a staged edit, or its controls go with it')
        .toBe(before);
    });

    it('keeps focus on the checkbox that was just pressed', () => {
      arrive();

      const box: HTMLInputElement | undefined = rowCheckboxes('required').at(1);

      box?.focus();
      toggle(box);
      fixture.detectChanges();

      expect(document.activeElement)
        .withContext('the operator must not be returned to the top of the page on every toggle')
        .toBe(box ?? null);
    });

    it('shows the staged value even though the row object was not replaced', () => {
      // ⚠ THE OTHER HALF, AND THE ONE A NAIVE FIX BREAKS. Holding a row object stable is only correct if
      // the grid still re-projects its cells; a fix that also returned the same ARRAY would keep focus and
      // show stale values, which is a worse defect than the one it replaced.
      arrive();

      const before: boolean | undefined = rowCheckboxes('required').at(1)?.checked;

      toggle(rowCheckboxes('required').at(1));
      fixture.detectChanges();

      expect(rowCheckboxes('required').at(1)?.checked)
        .withContext('the staged value must reach the rendered control')
        .toBe(before === true ? false : true);
      expect(text()).toContain('1 unapplied change(s)');
    });

    it('carries a row\u2019s identity WITH IT when a move changes its position', () => {
      // ⚠ IDENTITY IS KEYED BY THE DECLARATION, NOT BY THE POSITION, and a move is what tells the two
      // apart.
      arrive();

      const rowsBefore: readonly HTMLTableRowElement[] = query<HTMLTableRowElement>('tbody tr');
      const moved: HTMLTableRowElement | undefined = rowsBefore.at(0);

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      const rowsAfter: readonly HTMLTableRowElement[] = query<HTMLTableRowElement>('tbody tr');

      // The record that moved is now second, and it is the SAME element it was when first.
      expect(rowsAfter.at(1))
        .withContext('the moved record keeps its element, so focus inside it survives the move')
        .toBe(moved);

      // And the one it exchanged with keeps its own element, now first.
      expect(rowsAfter.at(0))
        .withContext('the exchanged record keeps its element too')
        .toBe(rowsBefore.at(1));
    });

    it('keeps focus on the move command after the move it performed', () => {
      arrive();

      const command: HTMLButtonElement | undefined = buttonsNamed('Move Down').at(0);

      command?.focus();
      command?.click();
      fixture.detectChanges();

      expect(document.activeElement)
        .withContext('an operator reordering a list must not lose their place on every press')
        .toBe(command ?? null);
    });

    // =======================================================================
    // THE MOVE THAT DESTROYS THE CONTROL THAT PERFORMED IT
    //
    // ⚠ THE CASE ABOVE PASSES ON ITS OWN, AND PASSING IT WAS NOT ENOUGH. A row keeps its element across
    // a move, so a command that is still rendered afterwards still holds focus - which is every move
    // EXCEPT the one that reaches an end of the list. A declaration moved into last place no longer renders
    // a Move Down at all: the control the operator just pressed is destroyed, and measured at runtime focus
    // fell to BODY. The cost is paid on exactly the press an operator is most likely to make, because
    // moving something to the end is the ordinary reason to press the same command repeatedly, and
    // recovering meant tabbing in from the top of the document through every preceding row.
    // =======================================================================

    it('moves focus to the opposite command when the one pressed is destroyed', async () => {
      arrive();

      // Nickname, Website, Biography. Two presses of Nickname's Move Down puts it last, and its Move Down
      // is then not rendered.
      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(renderedNames()).toEqual(['Website', 'Nickname', 'Biography']);

      const second: HTMLButtonElement | undefined = buttonsNamed('Move Down').at(1);

      second?.focus();
      second?.click();
      fixture.detectChanges();
      // Awaited, because the restore is deferred to the next render on purpose - the destination does not
      // exist until the grid has re-rendered in the new order.
      await fixture.whenStable();

      expect(renderedNames()).toEqual(['Website', 'Biography', 'Nickname']);

      const moved: HTMLTableRowElement | undefined = query<HTMLTableRowElement>('tbody tr').at(2);
      const upOnMovedRow: HTMLButtonElement | null =
        moved?.querySelector<HTMLButtonElement>('button[data-reorder^="up:"]') ?? null;

      expect(document.activeElement)
        .withContext('not BODY: the control that can still move it, on the row it moved')
        .toBe(upOnMovedRow);
      expect(moved?.querySelector('button[data-reorder^="down:"]'))
        .withContext('the precondition - the pressed command really is gone')
        .toBeNull();
    });

    it('keeps focus on the SAME command when it survives, so repeated presses work', async () => {
      arrive();

      const command: HTMLButtonElement | undefined = buttonsNamed('Move Down').at(0);
      const identifier: string | null = command?.getAttribute('data-reorder') ?? null;

      command?.focus();
      command?.click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect((document.activeElement as HTMLElement | null)?.getAttribute('data-reorder'))
        .withContext('the same declaration, the same direction - found by identity, not by position')
        .toBe(identifier);
    });

    it('replaces every row element when the catalogue itself is re-read', () => {
      // ⚠ THE STABILITY IS PER DECLARATION AND NOT PER SCREEN, so a genuinely different catalogue must not
      // be forced into the previous one's elements. A re-read reporting a DIFFERENT set of declarations is
      // a new set of records, and each gets its own row.
      arrive();

      const before: readonly HTMLTableRowElement[] = query<HTMLTableRowElement>('tbody tr');

      expect(before.length).toBe(3);

      // A re-read carrying one declaration none of the three shares an identifier with.
      press(REFRESH_LABEL);

      expectRequest('GET', DEFINITIONS_URL, 'the refresh read').flush(
        envelope([definition({ propertyDefinitionId: 21, propertyName: 'Department', viewOrder: 0 })]),
      );
      fixture.detectChanges();

      const after: readonly HTMLTableRowElement[] = query<HTMLTableRowElement>('tbody tr');

      expect(after.length).toBe(1);
      expect(before)
        .withContext('a different set of declarations must not be forced into the old elements')
        .not.toContain(after[0]);
    });
  });

  // ROW IDENTITY ACROSS A STAGED EDIT

  describe('the staged batch', () => {
    it('disables Apply until something is staged', () => {
      arrive();

      expect(button(APPLY_LABEL).disabled).toBeTrue();

      toggle(rowCheckboxes('required').at(0));

      expect(button(APPLY_LABEL).disabled).toBeFalse();
      expect(text()).toContain('1 unapplied change(s)');
    });

    it('disables Apply again when an edit is reversed, without requesting anything', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(0));

      expect(button(APPLY_LABEL).disabled).toBeTrue();
      httpMock.expectNone(() => true);
    });

    it('writes only the staged row', () => {
      arrive();

      toggle(rowCheckboxes('visible').at(1));
      press(APPLY_LABEL);

      const writes = driveBatch((write) => write.flush(envelope(definition())));

      expect(writes.length).toBe(1);
      expect(writes.at(0)?.url).toBe(definitionUrl(12));
      expect(writes.at(0)?.body).toEqual(
        jasmine.objectContaining({ propertyName: 'Website', visible: false }),
      );
    });

    it('discards every staged edit when the grid is refreshed', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));

      expect(button(APPLY_LABEL).disabled).toBeFalse();

      press(REFRESH_LABEL);

      expectRequest('GET', DEFINITIONS_URL, 'the refresh read').flush(envelope(catalogue()));
      fixture.detectChanges();

      expect(button(APPLY_LABEL).disabled).toBeTrue();
      expect(rowCheckboxes('required').at(0)?.checked).toBeFalse();
    });

    it('raises no notification on a successful batch, as the legacy set no message label', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      press(APPLY_LABEL);

      settleReplaces();
      // The batch reads the catalogue once when its last row has settled, so that read has to be answered
      // or verification fails on an outstanding request rather than on anything this case is about.
      settleReReads(catalogueWith({ 11: { required: true } }));

      expect(notify).not.toHaveBeenCalled();
      expect(statusRegion().textContent?.trim()).toBe('1 change(s) applied.');
    });

    it('states the staged count in the status region, and drops it once the batch lands', () => {
      arrive();

      // Empty at rest: there is nothing to say, and a region that starts out holding text is a
      // region whose first change assistive technology never hears.
      expect(statusRegion().textContent?.trim()).toBe('');

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(1));

      expect(statusRegion().textContent?.trim()).toBe('2 unapplied change(s).');

      press(APPLY_LABEL);
      settleReplaces();
      settleReReads(catalogueWith({ 11: { required: true }, 12: { required: true } }));

      expect(statusRegion().textContent?.trim()).toBe('2 change(s) applied.');
    });

    it('mounts the status region permanently, and keeps aria-live off the visible hint', () => {
      arrive();

      // Mounted before anything is staged, which is the property that makes it announce at all.
      expect(query(STATUS_REGION).length).toBe(1);
      expect(query('.profile-definitions__form-hint').length).toBe(0);

      toggle(rowCheckboxes('required').at(0));

      const hints = query<HTMLElement>('.profile-definitions__form-hint');

      expect(hints.length).toBe(1);
      // The hint is visible and silent; the region is invisible and speaks. Declaring `aria-live` on
      // both would announce the same count twice.
      expect(hints.at(0)?.getAttribute('aria-live')).toBeNull();
      expect(statusRegion().hasAttribute('data-visually-hidden')).toBeTrue();
    });

    it('does not leave the previous confirmation readable while the next batch is in flight', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      press(APPLY_LABEL);
      settleReplaces();
      settleReReads(catalogueWith({ 11: { required: true } }));

      expect(statusRegion().textContent?.trim()).toBe('1 change(s) applied.');

      toggle(rowCheckboxes('visible').at(1));
      press(APPLY_LABEL);

      // Staged again, so the count is what is outstanding - never the stale success of the batch
      // before it.
      expect(statusRegion().textContent?.trim()).toBe('1 unapplied change(s).');

      settleReplaces();
      settleReReads(catalogueWith({ 11: { required: true }, 12: { visible: false } }));
    });

    it('says nothing about a batch in which a row was refused, leaving that to the refusal', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      press(APPLY_LABEL);

      settleBatch(1, (write) =>
        write.flush(problem(409), { status: 409, statusText: 'Conflict' }),
      );
      drainReReads();

      // The refusal is announced through the notification service, naming the property, and the
      // status region is not made to repeat it.
      expect(notify).toHaveBeenCalled();
      expect(statusRegion().textContent?.trim()).toBe('1 unapplied change(s).');
    });

    it('keeps a refused row staged while the row that landed drops out', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(1));

      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      // The first write is accepted and the second refused, which is exactly the partial outcome
      // independent writes and no transaction make possible. ⚠ A REFUSAL DOES NOT ABANDON THE ROWS BEHIND
      // IT - here it is the last row, and the companion case below proves the continuation and the
      // catalogue is still read once afterwards.
      const written = settleBatch(2, (write, step) => {
        if (step === 0) {
          write.flush(envelope(definition({ propertyDefinitionId: 11, required: true })));
          return;
        }

        write.flush(problem(409), { status: 409, statusText: 'Conflict' });
      });

      expect(written).toEqual([definitionUrl(11), definitionUrl(12)]);

      settleReReads([
        definition({
          propertyDefinitionId: 11,
          propertyName: 'Nickname',
          viewOrder: 0,
          required: true,
        }),
        definition({ propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1 }),
        definition({ propertyDefinitionId: 13, propertyName: 'Biography', viewOrder: 2 }),
      ]);

      // One change remains outstanding — the refused row — and Apply is still offered for it.
      expect(text()).toContain('1 unapplied change(s)');
      expect(button(APPLY_LABEL).disabled).toBeFalse();
    });

    it('attempts every row after a refusal, and still reads the catalogue exactly once', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(1));
      toggle(rowCheckboxes('required').at(2));

      expect(text()).toContain('3 unapplied change(s)');

      press(APPLY_LABEL);

      const written = settleBatch(3, (write, step) => {
        if (step === 1) {
          write.flush(problem(409), { status: 409, statusText: 'Conflict' });
          return;
        }

        write.flush(envelope(definition()));
      });

      expect(written).toEqual([definitionUrl(11), definitionUrl(12), definitionUrl(13)]);

      // ONE read, after the last row settled - not one per landed write.
      settleReReads();
    });

    it('refuses a second batch while one is still running, and asks the server nothing', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(1));

      press(APPLY_LABEL);

      const first = pendingWrites('PUT');

      expect(first.length).withContext('one replace in flight').toBe(1);

      press(APPLY_LABEL);

      expect(pendingWrites('PUT').length).withContext('the second press asked nothing').toBe(0);

      first[0].flush(envelope(definition()));
      fixture.detectChanges();

      // The batch carries on to its second row by itself, and reads the catalogue once at the end.
      settleBatch(1);
      settleReReads();
    });
  });

  // The bulk toggles — dl-6

  describe('the bulk toggles', () => {
    it('are rendered outside the table, because the shared grid has no header template', () => {
      arrive();

      expect(bulkToggles().length).toBe(2);
      expect(query('.profile-definitions__bulk table').length).toBe(0);
    });

    it('report the all-true state and not merely the first row', () => {
      arrive([
        definition({ propertyDefinitionId: 111, propertyName: 'A', viewOrder: 0, visible: true }),
        definition({ propertyDefinitionId: 112, propertyName: 'B', viewOrder: 1, visible: false }),
      ]);

      // Visible is the second toggle. One row false is enough to clear it.
      expect(bulkToggles().at(1)?.checked).toBeFalse();
    });

    it('stage every row that does not already hold the value, and no others', () => {
      arrive([
        definition({ propertyDefinitionId: 121, propertyName: 'A', viewOrder: 0, required: true }),
        definition({ propertyDefinitionId: 122, propertyName: 'B', viewOrder: 1, required: false }),
        definition({ propertyDefinitionId: 123, propertyName: 'C', viewOrder: 2, required: false }),
      ]);

      toggle(bulkToggles().at(0));

      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      // Two staged rows, two replaces, one in flight at a time, and one catalogue read afterwards.
      expect(settleBatch(2)).toEqual([definitionUrl(122), definitionUrl(123)]);

      settleReReads();
    });

    it('are not rendered at all when the catalogue is empty', () => {
      arrive([]);

      expect(bulkToggles().length).toBe(0);
    });
  });

  // THE INLINE FORM

  describe('the inline create form', () => {
    it('registers an unsaved-entry probe that a closed editor leaves silent', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      arrive();

      // THE CONTROL, and on this screen it is also a real claim: a closed editor holds nothing, so
      // the bulk visibility affordances and the reorder commands cannot make this screen warn.
      expect(tracker.isDirty()).withContext('a closed editor is not unsaved entry').toBeFalse();

      press(ADD_LABEL);
      type('profile-definition-name', 'Nickname');

      expect(tracker.isDirty())
        .withContext('a typed definition with no write in flight is what the guard catches')
        .toBeTrue();

      press(CANCEL_LABEL);

      expect(tracker.isDirty())
        .withContext('an abandoned editor must not warn on every later departure')
        .toBeFalse();
    });

    // ⚠ A CLOSED EDITOR IS NOT THE ONLY WAY THIS SCREEN HOLDS UNSAVED ENTRY, and the probe above is
    // satisfied by a screen that still loses work. Measured on this screen: staging visibility changes with
    // the bulk toggles and then leaving - by Cancel, by the sidebar, or by the browser's Back button - left
    // immediately and discarded every staged row in silence, while the six other dirty editing routes all
    // refused. The staged rows live in `pendingRows`, not in the create form, so a probe reading only
    // `form.dirty` cannot see them however dirty the screen looks to the operator.
    it('reports staged visibility changes as unsaved entry, with the editor closed', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      // One row short of all-required, so the first bulk toggle stages exactly that row - the same
      // arrangement the bulk specification above uses, for the same reason: a toggle that changes nothing
      // stages nothing.
      arrive([
        definition({ propertyDefinitionId: 121, propertyName: 'A', viewOrder: 0, required: true }),
        definition({ propertyDefinitionId: 122, propertyName: 'B', viewOrder: 1, required: false }),
      ]);

      expect(tracker.isDirty())
        .withContext('the control: nothing is staged yet, and the editor was never opened')
        .toBeFalse();

      toggle(bulkToggles().at(0));

      expect(text())
        .withContext('the screen itself reports the staged row, so the guard must see it too')
        .toContain('1 unapplied change(s)');
      expect(tracker.isDirty())
        .withContext('a staged row with no write in flight is exactly what the guard must catch')
        .toBeTrue();

      press(APPLY_LABEL);
      settleBatch(1);
      settleReReads();

      expect(tracker.isDirty())
        .withContext('applied work is not unsaved work, so later departures must not be challenged')
        .toBeFalse();
    });

    it('is closed on arrival and opens in place without navigating', () => {
      arrive();

      expect(text()).not.toContain(CREATE_HEADING);

      press(ADD_LABEL);

      expect(text()).toContain(CREATE_HEADING);
      expect(button(CREATE_SUBMIT_LABEL)).toBeTruthy();
      // No detail route exists, so nothing may be requested by opening the editor.
      httpMock.expectNone(() => true);
    });

    it('opens with the legacy field initialisers, except the data type, which opens empty', () => {
      arrive();
      press(ADD_LABEL);

      expect(control('profile-definition-name').value).toBe('');
      // ⚠ THIS EXPECTATION MOVED, AND THE OLD ONE WAS THE DEFECT IT ASSERTED - QA-10. It required the box to
      // open holding the literal text "-1" - the legacy `Null.NullInteger` field initialiser rendered into a
      // `type="number"` input. That presented a REQUIRED field already containing a real-looking number that
      // the form's own `notNullInteger` validator then refused, so the field both offered and rejected the
      // same value. -1 means "nothing chosen yet", and an empty box is what that looks like. Nothing
      // downstream moved: -1 was never submittable, and `apply()` already refused a null data type before
      // composing a request, so the sentinel reached the wire under neither spelling.
      expect(control('profile-definition-data-type').value)
        .withContext('nothing is chosen yet, and the box says so instead of offering -1')
        .toBe('');
      expect(control('profile-definition-view-order').value).toBe('0');

      const visible = control('profile-definition-visible');

      expect(visible instanceof HTMLInputElement ? visible.checked : true).toBeFalse();
    });

    it('shows no validation message before a submission is attempted', () => {
      arrive();
      press(ADD_LABEL);

      expect(text()).not.toContain(NAME_REQUIRED_MESSAGE);
    });

    it('reports every failing rule on submit and requests nothing', () => {
      arrive();
      press(ADD_LABEL);
      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain(NAME_REQUIRED_MESSAGE);
      expect(text()).toContain(DATA_TYPE_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    /**
     * ⚠ THE CASE THAT KEEPS THE MEMOISED MESSAGE MAP HONEST. Each field's messages are bound twice — to
     * the shared field's `error` input and to the control's own `aria-invalid` — so they are derived ONCE
     * per change into a map the template reads, rather than recomputed per binding.
     */
    it('drops a field\'s message as soon as it is corrected, and keeps aria-invalid in step', () => {
      arrive();
      press(ADD_LABEL);
      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain(NAME_REQUIRED_MESSAGE);
      expect(control('profile-definition-name').getAttribute('aria-invalid'))
        .withContext('the control agrees with the message beneath it')
        .toBe('true');

      // No second submission: the form's own value change is what must invalidate the derivation.
      type('profile-definition-name', 'Twitter');

      expect(text()).not.toContain(NAME_REQUIRED_MESSAGE);
      expect(control('profile-definition-name').getAttribute('aria-invalid'))
        .withContext('and the attribute is withdrawn with it')
        .toBeNull();

      // The field that is still failing is untouched by the correction of its neighbour.
      expect(text()).toContain(DATA_TYPE_REQUIRED_MESSAGE);
      expect(control('profile-definition-data-type').getAttribute('aria-invalid')).toBe('true');

      httpMock.expectNone(() => true);
    });

    it('refuses a name carrying a space, with the legacy sentence', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm('Twitter Handle');
      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain(NAME_PATTERN_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses the data type while it still holds the legacy sentinel', () => {
      arrive();
      press(ADD_LABEL);
      type('profile-definition-name', 'Twitter');
      type('profile-definition-category', 'Contact');
      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain(DATA_TYPE_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('sends a .NET expression the browser itself cannot compile, rather than refusing it', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();

      // Inline options are ordinary .NET; JavaScript throws "Invalid group" on the same text.
      // Asserted here so the case cannot be read as an arbitrary choice of string.
      expect(() => new RegExp('(?i)^abc$')).toThrow();

      expect(() => type('profile-definition-expression', '(?i)^abc$')).not.toThrow();

      press(CREATE_SUBMIT_LABEL);

      expect(expectRequest('POST', DEFINITIONS_URL, 'the create').request.body).toEqual(
        jasmine.objectContaining({ validationExpression: '(?i)^abc$' }),
      );
    });

    it('sends a malformed expression too, and lets the authoritative engine refuse it', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();

      expect(() => type('profile-definition-expression', '([')).not.toThrow();

      press(CREATE_SUBMIT_LABEL);

      expect(text()).not.toContain('not a valid regular expression');
      expect(expectRequest('POST', DEFINITIONS_URL, 'the create').request.body).toEqual(
        jasmine.objectContaining({ validationExpression: '([' }),
      );
    });

    it("surfaces the server's refusal of an expression it will not run", () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      type('profile-definition-expression', '([');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', DEFINITIONS_URL, 'the create').flush(
        {
          type: 'urn:dnnmigration:error:profile-definition.invalid-validation-expression',
          title: 'One or more validation errors occurred.',
          status: 400,
          detail: 'The validation expression is not a valid regular expression.',
          errors: {
            validationExpression: ['The validation expression is not a valid regular expression.'],
          },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(text()).toContain('The validation expression is not a valid regular expression.');
      expect(notify).toHaveBeenCalledWith(
        'error',
        'The validation expression is not a valid regular expression.',
        null,
      );
      // The form stays open with the value in place, so the pattern can be corrected.
      expect(text()).toContain(CREATE_HEADING);
      expect(control('profile-definition-expression').value).toBe('([');
    });

    it('still refuses an expression longer than the column, which both sides agree on', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();

      // A character count is a fact no engine disagrees about, and the API applies the same
      // 512-character limit, so this is the one expression rule the client keeps.
      type('profile-definition-expression', 'a'.repeat(513));
      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain(EXPRESSION_TOO_LONG_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('accepts an expression that deliberately matches nothing', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      type('profile-definition-expression', '(?!)');
      press(CREATE_SUBMIT_LABEL);

      expect(expectRequest('POST', DEFINITIONS_URL).request.body).toEqual(
        jasmine.objectContaining({ validationExpression: '(?!)' }),
      );
    });

    it('posts the nine members plus the module association, sending the empty-string sentinel', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      check('profile-definition-required', true);
      press(CREATE_SUBMIT_LABEL);

      const write = expectRequest('POST', DEFINITIONS_URL, 'the create');

      expect(write.request.body).toEqual({
        propertyName: 'Twitter',
        propertyCategory: 'Contact',
        dataType: 349,
        defaultValue: '',
        length: 0,
        required: true,
        validationExpression: '',
        viewOrder: 0,
        visible: false,
        moduleDefId: null,
      });

      write.flush(envelope(definition({ propertyDefinitionId: 99, propertyName: 'Twitter' })), {
        status: 201,
        statusText: 'Created',
      });
      settleReReads();

      expect(text()).not.toContain(CREATE_HEADING);
      expect(notify).toHaveBeenCalledWith('success', 'The profile property was created.');
    });

    it('trims the keyed category and leaves the free-text members untouched', () => {
      arrive();
      press(ADD_LABEL);
      type('profile-definition-name', 'Twitter');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', '  Contact  ');
      type('profile-definition-default-value', ' spaced ');
      press(CREATE_SUBMIT_LABEL);

      expect(expectRequest('POST', DEFINITIONS_URL).request.body).toEqual(
        jasmine.objectContaining({
          propertyName: 'Twitter',
          propertyCategory: 'Contact',
          defaultValue: ' spaced ',
        }),
      );
    });

    it('refuses a padded name outright, because the pattern admits no space at all', () => {
      arrive();
      press(ADD_LABEL);
      type('profile-definition-name', '  Twitter  ');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', 'Contact');
      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain(NAME_PATTERN_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('closes without writing when the operator returns to the list', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      press(CANCEL_LABEL);

      expect(text()).not.toContain(CREATE_HEADING);
      httpMock.expectNone(() => true);
    });
  });

  // =========================================================================
  // WHAT THE EDITOR OWES THE KEYBOARD AND THE ACCESSIBILITY TREE
  //
  // Two defects of the same shape: information and orientation both existed on screen, and neither was
  // reachable by the people who most needed them.
  // =========================================================================

  describe('the editor disclosure and its guidance', () => {
    /** Every hint span the editor renders, with the id it publishes. */
    function hints(): readonly { readonly id: string; readonly text: string }[] {
      return query<HTMLElement>('.profile-definitions__form-hint')
        .filter((node) => node.id !== '')
        .map((node) => ({ id: node.id, text: (node.textContent ?? '').trim() }));
    }

    // ⚠ A SENTENCE BESIDE A CONTROL IS NOT A SENTENCE ABOUT IT. Nine spans explained what each field
    // accepts, and every one of them was an unassociated neighbour: a screen reader announced the control,
    // its name and its state, and stopped - there is no way for it to know that the span alongside was
    // meant as guidance. Reaching them meant leaving the field, reading forward and coming back.
    it('associates every hint with the control it explains', () => {
      arrive();
      press(ADD_LABEL);

      const published: readonly { readonly id: string; readonly text: string }[] = hints();

      expect(published.length)
        .withContext('one identified hint per field the editor guides')
        .toBe(9);

      for (const hint of published) {
        expect(hint.text.length).withContext(`${hint.id} says something`).toBeGreaterThan(0);

        const owner: HTMLElement | undefined = query<HTMLElement>(
          `[aria-describedby~="${hint.id}"]`,
        ).at(0);

        expect(owner)
          .withContext(`${hint.id} is named by the control it explains`)
          .toBeDefined();
      }
    });

    it('publishes no dangling description, which is worse than none at all', () => {
      arrive();
      press(ADD_LABEL);

      for (const control of query<HTMLElement>('[aria-describedby]')) {
        for (const reference of (control.getAttribute('aria-describedby') ?? '').trim().split(/\s+/)) {
          expect(query<HTMLElement>(`#${reference}`).length)
            .withContext(`${reference} names an element that exists`)
            .toBe(1);
        }
      }
    });

    // ⚠ CLOSING THE EDITOR USED TO PUT THE OPERATOR AT THE TOP OF THE DOCUMENT. The editor holds focus,
    // and closing it destroys the element holding it - measured, focus fell to BODY. The destination is
    // the control that OPENED it, so an operator returns to where they were rather than to the top of a
    // list of any length.
    it('returns focus to the row command that opened it', async () => {
      arrive();

      const edit: HTMLButtonElement | undefined = buttonsNamed('Edit').at(1);
      const identifier: string | null = edit?.getAttribute('data-edit') ?? null;

      expect(identifier).withContext('the command names the declaration it opens').not.toBeNull();

      edit?.focus();
      edit?.click();
      fixture.detectChanges();

      press(CANCEL_LABEL);
      await fixture.whenStable();

      expect((document.activeElement as HTMLElement | null)?.getAttribute('data-edit'))
        .withContext('the row the operator was working on, not the top of the screen')
        .toBe(identifier);
    });

    it('returns focus to the primary action when THAT is what opened it', async () => {
      // The destination follows the invoker rather than a fixed anchor: sending an edit back to the
      // primary action would be its own kind of displacement.
      arrive();

      press(ADD_LABEL);
      press(CANCEL_LABEL);
      await fixture.whenStable();

      expect(document.activeElement)
        .toBe(buttonsNamed(ADD_LABEL).at(0) ?? null);
    });
  });

  // The duplicate-name refusal, and the defect around it

  describe('a refused create', () => {
    function submitDuplicate(): void {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', DEFINITIONS_URL, 'the create').flush(
        problem(409, 'urn:dnnmigration:error:profile-definition.duplicate-name'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();
    }

    it('announces the legacy sentence verbatim, at the error severity the legacy chose', () => {
      submitDuplicate();

      expect(notify).toHaveBeenCalledWith('error', DUPLICATE_NAME_MESSAGE, null);
    });

    it('leaves the form open so the name can be corrected', () => {
      submitDuplicate();

      expect(text()).toContain(CREATE_HEADING);
      expect(control('profile-definition-name').value).toBe('Twitter');
    });

    it('does NOT adopt an edit identifier, so a second attempt is still a create', () => {
      submitDuplicate();

      // The legacy assigned the failed call's return value into its view-state identifier before testing
      // it, so the next save took the update branch with a nonsense key. This asserts the defect is not
      // reproduced.
      type('profile-definition-name', 'TwitterHandle');
      press(CREATE_SUBMIT_LABEL);

      expect(pendingWrites('PUT').length).toBe(0);
      expectRequest('POST', DEFINITIONS_URL, 'the second create').flush(
        envelope(definition({ propertyDefinitionId: 98 })),
        { status: 201, statusText: 'Created' },
      );
      settleReReads();
    });
  });

  // The inline edit form — dl-12

  describe('the inline edit form', () => {
    function openEditor(): void {
      arrive([
        definition({
          propertyDefinitionId: 131,
          propertyName: 'Nickname',
          propertyCategory: 'Contact',
          dataType: 349,
          defaultValue: 'none',
          length: 25,
          required: true,
          validationExpression: '^\\w+$',
          viewOrder: 3,
          visible: true,
        }),
      ]);

      buttonsNamed('Edit').at(0)?.click();
      fixture.detectChanges();
    }

    it('opens pre-populated in place rather than navigating to a detail address', () => {
      openEditor();

      expect(text()).toContain(EDIT_HEADING);
      expect(control('profile-definition-name').value).toBe('Nickname');
      expect(control('profile-definition-view-order').value).toBe('3');
      expect(button(EDIT_SUBMIT_LABEL)).toBeTruthy();
      httpMock.expectNone(() => true);
    });

    it('marks the name and the data type read-only rather than disabling them', () => {
      openEditor();

      const name = control('profile-definition-name');
      const dataType = control('profile-definition-data-type');

      expect(name.hasAttribute('readonly')).toBeTrue();
      expect(dataType.hasAttribute('readonly')).toBeTrue();
      // A disabled control is excluded from the form's value, which would drop two required members from
      // every replace request.
      expect(name instanceof HTMLInputElement ? name.disabled : true).toBeFalse();
      expect(dataType instanceof HTMLInputElement ? dataType.disabled : true).toBeFalse();
    });

    it('leaves both editable while creating', () => {
      arrive();
      press(ADD_LABEL);

      expect(control('profile-definition-name').hasAttribute('readonly')).toBeFalse();
      expect(control('profile-definition-data-type').hasAttribute('readonly')).toBeFalse();
    });

    it('replaces with all nine members, the read-only pair included', () => {
      openEditor();
      type('profile-definition-category', 'Social');
      press(EDIT_SUBMIT_LABEL);

      const write = expectRequest('PUT', definitionUrl(131), 'the replace');

      expect(write.request.body).toEqual({
        propertyName: 'Nickname',
        propertyCategory: 'Social',
        dataType: 349,
        defaultValue: 'none',
        length: 25,
        required: true,
        validationExpression: '^\\w+$',
        viewOrder: 3,
        visible: true,
      });

      write.flush(envelope(definition({ propertyDefinitionId: 131 })));
      settleReReads();

      expect(text()).not.toContain(EDIT_HEADING);
      expect(notify).toHaveBeenCalledWith('success', 'The profile property was updated.');
    });

    it('shows the position the operator staged in the grid, not the stored one', () => {
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      // Nickname moved to position one; the editor must agree with the grid.
      buttonsNamed('Edit').at(1)?.click();
      fixture.detectChanges();

      expect(control('profile-definition-name').value).toBe('Nickname');
      expect(control('profile-definition-view-order').value).toBe('1');
    });
  });

  // REMOVAL

  describe('removal', () => {
    function confirmRemoval(): void {
      arrive();
      buttonsNamed('Delete').at(0)?.click();
      fixture.detectChanges();

      pressDialog('Delete');
    }

    it('asks first, with the legacy confirmation wording', () => {
      arrive();

      expect(query('app-confirm-dialog').length).toBe(0);

      buttonsNamed('Delete').at(0)?.click();
      fixture.detectChanges();

      expect(query('app-confirm-dialog').length).toBe(1);

      // ⚠ THE QUESTION AND THE RECORD, BOTH. The body used to be the bare legacy sentence and named nothing
      // at all, while the dialog is a real modal that covers the grid behind it - so an operator had no way
      // to check which declaration was about to be destroyed. The sentence is still asserted verbatim, and
      // the property's own name is asserted beside it.
      const body: string = (query('.confirm-dialog__message')[0]?.textContent ?? '').trim();

      expect(body.startsWith(REMOVAL_MESSAGE))
        .withContext(`the measured question, verbatim, at the front of: ${body}`)
        .toBeTrue();
      // Self-validating: the name is taken from the catalogue's first declaration in position order rather
      // than restated, so a fixture reordering cannot make this pass against the wrong record.
      const firstDeclaration: string = catalogue()[0].propertyName;

      expect(firstDeclaration).withContext('the row whose Delete was pressed').toBe('Nickname');
      expect(body).toContain(firstDeclaration);
      // The confirmation must carry a real accessible name: the legacy overwrote its own "Delete" text with
      // a resource key that does not exist, leaving it empty.
      expect(dialogButton('Delete').textContent ?? '').toContain('Delete');
      httpMock.expectNone(() => true);
    });

    it('requests nothing when the operator backs out', () => {
      arrive();
      buttonsNamed('Delete').at(0)?.click();
      fixture.detectChanges();

      pressDialog('Cancel');

      expect(query('app-confirm-dialog').length).toBe(0);
      httpMock.expectNone(() => true);
    });

    it('removes immediately once confirmed, and is not part of the batch', () => {
      confirmRemoval();

      const write = expectRequest('DELETE', definitionUrl(11), 'the removal');

      write.flush(null, { status: 204, statusText: 'No Content' });
      settleReReads();

      expect(notify).toHaveBeenCalledWith(
        'success',
        'Profile property "Nickname" was deleted.',
      );
    });

    // MIGRATION: THIS ASSERTION USED TO ENCODE A SENTENCE THAT WAS SIMPLY WRONG. A bare 409 on a removal was
    // reported as the property being "in use", telling the operator to "remove the recorded values first" -
    // advice for a condition the screen was guessing at, naming no way to carry it out. The API issues two
    // specific refusals here, each recognised by its own code and each reporting what the server said, so a
    // 409 carrying NEITHER code is a concurrent writer. The expectation now states that.
    it('reports an unrecognised conflict as a concurrent change rather than guessing at a cause', () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(problem(409), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      expect(notify).toHaveBeenCalledWith(
        'error',
        'That profile property was changed by someone else, so it was not deleted. Read the list again and retry.',
        null,
      );
    });

    // ---- The cascade consent, added for Area of Concern A1 ------------------------------------------
    //
    // Removing a profile property takes every answer the portal's accounts recorded against it. The API
    // refuses the first attempt so the operator can be shown HOW MANY, and only a second call carrying
    // consent performs it. These cases prove the screen asks, carries the server's own count while asking,
    // sends the flag only when told to, and leaves the declaration alone when the operator declines.

    /** The refusal the API issues for a removal that would cascade, carrying its count. */
    const CASCADE_DETAIL =
      'Withdrawing "Nickname" will permanently delete 6 recorded profile answer(s) held by this '
      + "portal's accounts, and that cannot be undone. Back up the UserProfile table first, then repeat "
      + 'this request with confirmValueDeletion=true to proceed.';

    /** Drives a first removal to the cascade refusal, proving the first attempt carries no consent. */
    function refuseWithCascade(): void {
      confirmRemoval();

      const first = expectRequest('DELETE', definitionUrl(11), 'the unconsented removal');

      // ⚠ THE WHOLE PROTECTION RESTS ON THIS. If the screen ever sent consent on a first attempt, the API
      // would delete the answers without anyone being shown the count, and every other case in this group
      // would still pass.
      expect(first.request.params.has('confirmValueDeletion')).toBeFalse();
      expect(first.request.urlWithParams).toBe(definitionUrl(11));

      first.flush(
        {
          type: 'urn:dnnmigration:error:profile-definition.value-deletion-unacknowledged',
          title: 'Conflict',
          status: 409,
          detail: CASCADE_DETAIL,
        },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();
    }

    it('asks again, quoting the server\'s own count, when removal would destroy recorded answers', () => {
      refuseWithCascade();

      // The dialog is REOPENED rather than the refusal being announced and left, because the refusal is a
      // question the operator can answer.
      expect(query('app-confirm-dialog').length).toBe(1);
      expect(text()).toContain('6 recorded profile answer(s)');
      expect(text()).toContain('Back up the UserProfile table first');

      // ⚠ AND IT IS NOT ALSO ANNOUNCED. The dialog is modal and holds focus, so notifying the same sentence
      // would state one event twice in two places.
      expect(notify).not.toHaveBeenCalled();
    });

    it('sends the consent flag only on the second attempt', () => {
      refuseWithCascade();

      pressDialog('Delete permanently');

      // The retry addresses the SAME resource, with consent carried as a query parameter. The parameter is
      // asserted separately from the path because the harness matches on the path alone - which is what makes
      // the pair of assertions meaningful: the first attempt above matched this same path and must NOT have
      // carried the flag, and this one must.
      const retry = expectRequest('DELETE', definitionUrl(11), 'the consented removal');

      expect(retry.request.params.get('confirmValueDeletion')).toBe('true');
      expect(retry.request.urlWithParams).toBe(`${definitionUrl(11)}?confirmValueDeletion=true`);

      retry.flush(null, { status: 204, statusText: 'No Content' });
      settleReReads();

      expect(notify).toHaveBeenCalledWith(
        'success',
        'Profile property "Nickname" was deleted.',
      );
    });

    it('leaves the declaration alone when the operator declines the cascade', () => {
      refuseWithCascade();

      pressDialog('Cancel');

      expect(query('app-confirm-dialog').length).toBe(0);

      // Nothing further is sent, so declining is the same outcome as never having asked.
      httpMock.expectNone(() => true);
    });

    it('reports a reserved property as refused outright, with no second ask', () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(
        {
          type: 'urn:dnnmigration:error:profile-definition.protected',
          title: 'Conflict',
          status: 409,
          detail:
            '"FirstName" is one of the profile properties this platform reserves, so it cannot be withdrawn.',
        },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // Terminal: there is no parameter that performs it, so no dialog reopens and the reason is announced.
      expect(query('app-confirm-dialog').length).toBe(0);
      expect(notify).toHaveBeenCalledWith(
        'error',
        '"FirstName" is one of the profile properties this platform reserves, so it cannot be withdrawn.',
        null,
      );
    });

    it('reports a declaration that has already been removed', () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(
        problem(404, 'urn:dnnmigration:error:profile-definition.not-found'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(notify).toHaveBeenCalledWith(
        'warning',
        'That profile property no longer exists. The list has been refreshed.',
        null,
      );
    });

    // A confirmed delete destroys the button that opened the dialog, so the dialog's own focus-return has
    // nothing to return to and focus collapses to the document body. That was measured in a browser, and
    // these two cases pin the fix so it cannot silently regress.
    it('moves focus to the primary action once a removal succeeds', async () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      settleReReads();

      // Awaited, because the move is deferred to the next render on purpose.
      await fixture.whenStable();

      const anchor: HTMLButtonElement | undefined = buttonsNamed(ADD_LABEL).at(0);

      expect(anchor).toBeDefined();
      // The anchor being enabled is a PRECONDITION of the assertion below, not an incidental detail: a
      // disabled control cannot hold focus.
      expect(anchor?.disabled).toBe(false);
      expect(document.activeElement).toBe(anchor ?? null);
    });

    it('leaves focus alone when the removal was refused', async () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(problem(409), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      // Settled the same way the success case is, so the two are compared on equal terms: this asserts that
      // NO deferred move was scheduled, not merely that one has yet to run.
      await fixture.whenStable();

      // The row survives a refusal, so its own command still exists and the anchor must NOT be stolen:
      // moving focus here would displace the operator for no reason.
      expect(document.activeElement).not.toBe(buttonsNamed(ADD_LABEL).at(0) ?? null);
    });
  });

  // A FAILED READ

  describe('a failed read', () => {
    it('surfaces the refusal without leaving the screen blank of explanation', () => {
      create();

      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(problem(500), {
        status: 500,
        statusText: 'Server Error',
      });
      fixture.detectChanges();

      expect(query('app-error-banner [role="alert"]').length).toBeGreaterThan(0);
      expect(text()).toContain(PAGE_TITLE);
    });
  });

  // The waiting and empty states
  // Both belong to `app-data-table`, which is why neither the shared spinner nor the shared empty state is
  // declared in this screen's own template. Asserted here rather than assumed, because "the table owns it"
  // is only true while the table is actually given the `loading` input.

  describe('the grid before it has rows', () => {
    it('shows the shared spinner while the read is in flight and drops it once answered', () => {
      create();

      expect(query('app-loading-spinner').length).withContext('while in flight').toBe(1);
      // Waiting WINS over empty: there are no rows yet either, and announcing "no records" to a reader
      // before the answer has arrived would state something untrue.
      expect(query('app-empty-state').length).withContext('not empty while waiting').toBe(0);

      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(envelope(catalogue()));
      fixture.detectChanges();

      expect(query('app-loading-spinner').length).withContext('once answered').toBe(0);
      expect(renderedNames().length).toBe(3);
    });

    it('shows the shared empty state, with its own default wording, for an empty catalogue', () => {
      arrive([]);

      expect(query('app-empty-state').length).toBe(1);
      expect(query('app-loading-spinner').length).toBe(0);
      // The table binds no `message`, so the shared default is what a reader sees. Asserted because this
      // screen deliberately does not override it.
      expect(text()).toContain('No records found.');
    });

    it('refuses a paged body outright, because this read is not paged', () => {
      create();

      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(
        envelope({ items: catalogue(), page: 1, pageSize: 10, totalCount: 3 }),
      );
      fixture.detectChanges();

      // ⚠ THE PLACEHOLDER CHANGED, AND THE OLD ONE ASSERTED A FALSEHOOD. A paged body where a bare list
      // belongs is a response this client cannot read, and it used to fall through to the shared empty
      // state - which asserts that the site declares no profile properties. That exact conflation was
      // measured on a real refusal: a `403` on an account's own profile rendered as "This site declares no
      // profile properties, so there is nothing to show." while thirteen were declared.
      expect(query('app-empty-state').length)
        .withContext('a failure is never presented as an empty catalogue')
        .toBe(0);
      expect(text()).withContext('nothing was unwrapped').not.toContain('Nickname');
      expect(text()).not.toContain('Biography');
      expect(text()).toContain('could not be read');
    });
  });

  // The table's accessibility and structural contract

  describe('the table structure', () => {
    it('names itself in the accessibility tree with a real caption element', () => {
      arrive();

      const captions = query<HTMLTableCaptionElement>('table caption');

      expect(captions.length).withContext('exactly one caption').toBe(1);
      expect((captions.at(0)?.textContent ?? '').trim()).toBe(GRID_CAPTION);
      // The page heading is a separate string in a separate place; see GRID_CAPTION's note.
      expect(text()).toContain(PAGE_TITLE);
    });

    it('scopes every heading to its column, including the four that paint nothing', () => {
      arrive();

      const headers = query<HTMLTableCellElement>('thead th');

      expect(headers.length).toBe(12);

      for (const header of headers) {
        expect(header.getAttribute('scope')).withContext('every heading is column-scoped').toBe('col');
      }
    });

    it('keeps all twelve columns distinct, collapsing none of them into another', () => {
      arrive();

      // The shared table tracks its heading and cell loops by column KEY and validates that the keys are
      // unique when the set is bound.
      for (const row of query<HTMLTableRowElement>('tbody tr')) {
        expect(row.querySelectorAll('td,th').length).withContext('cells in a row').toBe(12);
      }

      // And distinct from the LABELS: the eight data headings sit in the last eight positions, in the
      // legacy's own order, behind the four command columns.
      const headings = query<HTMLTableCellElement>('thead th').map((cell) =>
        (cell.textContent ?? '').trim(),
      );

      expect(headings.slice(4)).toEqual(DATA_HEADINGS);
    });

    it('emits no semantic landmark, because the shell owns each of them exactly once', () => {
      arrive();

      expect(query('header, main, nav, footer').length).toBe(0);
    });
  });

  // ===================================================================================================
  // A VALUE THE DECLARATION SIMPLY DOES NOT CARRY — QA-20
  // ===================================================================================================

  describe('the two nullable columns', () => {
    /** The cells of one column, by its heading position among the eight data headings. */
    function cellsOf(heading: string): readonly HTMLTableCellElement[] {
      const headings = query<HTMLTableCellElement>('thead th').map((cell) =>
        (cell.textContent ?? '').trim(),
      );
      const index = headings.indexOf(heading);

      expect(index).withContext(`the ${heading} column is rendered`).toBeGreaterThan(-1);

      return query<HTMLTableRowElement>('tbody tr').map(
        (row) => Array.from(row.querySelectorAll<HTMLTableCellElement>('th,td'))[index],
      );
    }

    it('marks an absent default value instead of painting an empty box', () => {
      // ⚠ THE MEASURED DEFECT. Both columns rendered through a helper that collapses `null` to the empty
      // string and then paints it, so every cell of both columns was an empty `<code>` box: no text, no
      // marker, and nothing in the accessibility tree. Sixteen of them on the live catalogue.
      arrive([definition({ propertyDefinitionId: 21, propertyName: 'Nickname', defaultValue: '' })]);

      const cell = cellsOf('Default Value')[0];

      expect(cell.querySelector('app-absent-value'))
        .withContext('the shared marker, not an empty box')
        .not.toBeNull();
      expect((cell.textContent ?? '').trim().length)
        .withContext('and it paints something a reader can see')
        .toBeGreaterThan(0);
    });

    it('marks an absent validation expression the same way', () => {
      arrive([
        definition({ propertyDefinitionId: 22, propertyName: 'Nickname', validationExpression: null }),
      ]);

      expect(cellsOf('Validation Expression')[0].querySelector('app-absent-value')).not.toBeNull();
    });

    it('treats the EMPTY STRING as absent, not only null', () => {
      // ⚠ THE DISTINCTION THE API ACTUALLY DRAWS, AND TESTING NULLNESS ALONE WOULD HAVE MISSED HALF OF IT.
      // Measured against the live endpoint: `validationExpression` arrives as `null` and `defaultValue` arrives
      // as `''`. Neither is a value a reader can act on.
      arrive([
        definition({
          propertyDefinitionId: 23,
          propertyName: 'Nickname',
          defaultValue: '',
          validationExpression: '',
        }),
      ]);

      expect(cellsOf('Default Value')[0].querySelector('app-absent-value')).not.toBeNull();
      expect(cellsOf('Validation Expression')[0].querySelector('app-absent-value')).not.toBeNull();
    });

    it('treats a value of nothing but whitespace as absent, because it paints as an empty box too', () => {
      arrive([
        definition({ propertyDefinitionId: 24, propertyName: 'Nickname', defaultValue: '   ' }),
      ]);

      expect(cellsOf('Default Value')[0].querySelector('app-absent-value')).not.toBeNull();
    });

    it('paints a RECORDED value in its code box and adds no marker', () => {
      // The counterpart, and the regression that matters: a real value must not acquire the marker.
      arrive([
        definition({
          propertyDefinitionId: 25,
          propertyName: 'Nickname',
          defaultValue: 'none',
          validationExpression: '^\\w+$',
        }),
      ]);

      const value = cellsOf('Default Value')[0];
      const expression = cellsOf('Validation Expression')[0];

      expect(value.querySelector('app-absent-value')).toBeNull();
      expect((value.querySelector('code')?.textContent ?? '').trim()).toBe('none');
      expect(expression.querySelector('app-absent-value')).toBeNull();
      expect((expression.querySelector('code')?.textContent ?? '').trim()).toBe('^\\w+$');
    });
  });

  // ===================================================================================================
  // THE COLUMN TRACKS — QA-4b/4c
  //
  // ⚠ EVERY FIGURE HERE WAS MEASURED IN A BROWSER, NOT CHOSEN. Before these weights existed, the four command
  // columns declared `min-content`, which a fixed table layout DISCARDS — so all twelve columns resolved to an
  // even twelfth: 99.83px at a 1440 viewport and 80px at the 960px floor. That funded four columns holding a
  // 44px icon button each out of the columns carrying text, and the row's own identifier was the casualty:
  // `PostalCode` needs 85.56px and had a 72px content box at every width from 320 up to about 1366.
  // ===================================================================================================

  describe('the column tracks', () => {
    /** The shared floor every listing is at or above, so a track's narrowest real size is a percentage of it. */
    const FLOOR_PX = 960;

    /** The command track token: one interactive target plus the cell's own inline padding. */
    const COMMAND_TRACK = 'var(--table-command-column-inline-size)';

    /**
     * The widest content each weighted column has to hold, measured in Chrome at a 1198px table with a
     * uniform 8.00px of cell chrome (4px padding each side, zero border under `border-collapse: collapse`).
     */
    const MEASURED_NEED_PX: Readonly<Record<string, number>> = {
      '11.5%': 85.56, // Name — driven by `PostalCode`, the longest built-in property name
      '8%': 74.41, // Category 73.03 and DataType 74.41 share a weight; the larger governs
      '6.25%': 58.0, // Length — heading-driven
      '11%': 102.19, // Default Value — heading-driven
      '7.75%': 73.0, // Required — heading-driven
      '6.75%': 62.84, // Visible — checkbox plus gap plus "Yes"
    };

    /** The declared `inline-size` of every rendered column track, in column order. */
    function tracks(): readonly string[] {
      return query<HTMLTableColElement>('colgroup col').map((col) => col.style.inlineSize);
    }

    it('sizes the four command columns with the shared token, never an intrinsic keyword', () => {
      arrive();

      const declared = tracks();

      expect(declared.length).toBe(12);
      expect(declared.slice(0, 4))
        .withContext('Edit, Delete, Move Down and Move Up')
        .toEqual([COMMAND_TRACK, COMMAND_TRACK, COMMAND_TRACK, COMMAND_TRACK]);

      // The regression that matters: `min-content` and `max-content` are not lengths, so the fixed table
      // algorithm ignores them and falls back to the automatic share. Neither may reappear on any track here.
      for (const track of declared) {
        expect(track)
          .withContext('an intrinsic keyword resolves to nothing under a fixed table layout')
          .not.toMatch(/min-content|max-content/);
      }
    });

    it('leaves EXACTLY ONE column unweighted, so the command tracks are honoured at all', () => {
      arrive();

      // Under `table-layout: fixed` the percentages resolve against the table width and the remainder goes to
      // whichever columns declared nothing. With every column weighted there is no remainder to give, and a
      // declared length on a command column is silently renegotiated. With more than one unweighted column the
      // remainder is split and the widest value's column no longer receives it.
      const unweighted = tracks().filter((track) => track === '');

      expect(unweighted.length)
        .withContext('the single slack absorber — the validation expression column')
        .toBe(1);
      expect(tracks().at(-3))
        .withContext('and it is that column, third from the end, ahead of Required and Visible')
        .toBe('');
    });

    it('gives every weighted column at least its measured requirement at the shared floor', () => {
      arrive();

      for (const track of tracks()) {
        if (track === '' || track === COMMAND_TRACK) {
          continue;
        }

        const need = MEASURED_NEED_PX[track];

        expect(need)
          .withContext(`every declared weight is one this case knows the requirement for: ${track}`)
          .toBeDefined();

        const resolved = (Number.parseFloat(track) / 100) * FLOOR_PX;

        expect(resolved)
          .withContext(`${track} resolves to ${String(resolved)}px, and needs ${String(need)}px`)
          .toBeGreaterThanOrEqual(need);
      }
    });

    it('leaves the unweighted column more than its own requirement once the rest have taken theirs', () => {
      arrive();

      const declared = tracks();
      const commands = declared.filter((track) => track === COMMAND_TRACK).length;

      // The token is `--table-command-column-inline-size`, 3.25rem against a 16px root.
      const commandPx = commands * 3.25 * 16;
      const weightedPx = declared
        .filter((track) => track.endsWith('%'))
        .reduce((total, track) => total + (Number.parseFloat(track) / 100) * FLOOR_PX, 0);

      const absorbed = FLOOR_PX - commandPx - weightedPx;

      // The validation expression was the worst-starved column before this change: 163.91px of regular
      // expression against a 99.83px share, its heading wrapping onto two lines at 1440 and clipping outright
      // below that.
      expect(absorbed)
        .withContext(`the slack absorber receives ${String(absorbed)}px at the floor, and needs 163.91px`)
        .toBeGreaterThanOrEqual(163.91);
    });

    it('makes the identity column the widest weighted track on the grid', () => {
      arrive();

      const percentages = tracks()
        .filter((track) => track.endsWith('%'))
        .map((track) => Number.parseFloat(track));

      // A reader identifies a row by its property name, so no column that merely describes the property may
      // out-weigh it. The unweighted expression column is excluded by construction — it takes the remainder.
      //
      // ⚠ THIS CAUGHT A REAL MISALLOCATION. At the first pass the name was weighted 9.5% purely to its measured
      // requirement, which put it BEHIND the 11% that the `Default Value` heading needs — so the widest track on
      // the grid belonged to a column describing the property rather than to the property itself. Sizing an
      // extensible identifier to the longest value the built-in set happens to contain is the narrower mistake
      // underneath that: a tenant may declare a far longer name than `PostalCode`.
      expect(Math.max(...percentages))
        .withContext('the property name carries the largest declared weight')
        .toBe(11.5);
      expect(percentages.filter((weight) => weight === 11.5).length)
        .withContext('and carries it alone, so the identity column is unambiguously the widest')
        .toBe(1);
    });
  });

  // The four row commands — the most of any grid in this application

  describe('the row commands', () => {
    it('paints the delete command in the danger hue, as every sibling listing does', () => {
      // ⚠ THE ONE DESTRUCTIVE CONTROL ACROSS SIX LISTINGS THAT WAS NOT MARKED AS ONE. Measured at runtime, this
      // grid's delete command computed the PRIMARY hue while all five siblings paint the danger token. The cause
      // was structural: all four commands here share one class, so the delete button had no class of its own and
      // fell through to the generic button chrome. Its glyph is a bare multiplication sign, so hue was the only
      // signal separating "remove this property permanently" from "move it up one place".
      arrive();

      const remove = query<HTMLButtonElement>('.profile-definitions__row-action--danger');

      expect(remove.length)
        .withContext('one destructive command per deletable row, and three of these rows are deletable')
        .toBe(3);

      for (const button of remove) {
        expect(button.classList.contains('profile-definitions__row-action'))
          .withContext('the danger class is additive, so the shared target sizing still applies')
          .toBeTrue();
        expect((button.textContent ?? '').trim()).toContain('Delete');
      }

      // And it is only the destructive one: the other three commands must not borrow the hue.
      for (const button of query<HTMLButtonElement>('.profile-definitions__row-action')) {
        const label = (button.textContent ?? '').trim();

        expect(button.classList.contains('profile-definitions__row-action--danger'))
          .withContext(`${label} is destructive only if it deletes`)
          .toBe(label.includes('Delete'));
      }
    });

    /**
     * The commands of one row, in document order, as their ACCESSIBLE names. Read from the visually
     * hidden span rather than from the button's whole text, because the button also carries a decorative
     * glyph that is hidden from assistive technology.
     */
    function commandNames(rowIndex: number): readonly string[] {
      const row = query<HTMLTableRowElement>('tbody tr').at(rowIndex);

      if (row === undefined) {
        throw new Error(`row ${rowIndex} was not rendered`);
      }

      return Array.from(row.querySelectorAll<HTMLButtonElement>('button')).map((command) =>
        (command.querySelector('[data-visually-hidden]')?.textContent ?? '')
          .replace(/\s+/gu, ' ')
          .trim(),
      );
    }

    it('offers four commands on a middle row, in the legacy column order', () => {
      arrive();

      // Edit, Delete, Move Down, Move Up — down BEFORE up, which reads backwards and is exactly why it is
      // asserted: `COLUMN_MOVE_DOWN = 2` and `COLUMN_MOVE_UP = 3`.
      expect(commandNames(1)).toEqual([
        'Edit Website',
        'Delete Website',
        'Move Down Website',
        'Move Up Website',
      ]);
    });

    it('gives every command a real button and a full-word accessible name', () => {
      arrive();

      const commands = query<HTMLButtonElement>('tbody tr button');

      expect(commands.length).toBeGreaterThan(0);

      for (const command of commands) {
        // A real button is keyboard-reachable and announced as operable with no key handling written
        // anywhere. `type="button"` keeps it from submitting the inline form.
        expect(command.tagName).toBe('BUTTON');
        expect(command.getAttribute('type')).toBe('button');

        // The name is the FULL WORD, never the legacy's abbreviated "Del", "Dn" or "Up", and never
        // the empty string the legacy delete command actually reached readers with.
        const name = (command.querySelector('[data-visually-hidden]')?.textContent ?? '').trim();

        expect(name.length).withContext('every command carries a name').toBeGreaterThan(0);
        expect(['Del', 'Dn', 'Up']).not.toContain(name);
      }
    });

    it('hides the glyph from assistive technology, so the name is announced once', () => {
      arrive();

      const commands = query<HTMLButtonElement>('tbody tr button');
      const glyphs = query<HTMLElement>('tbody tr button [aria-hidden="true"]');

      // TEN, not twelve, and the arithmetic is the point: three rows would carry twelve commands if every
      // command were offered on every row, but the first row has no Move Up and the last has no Move Down,
      // because neither has a neighbour to exchange positions with.
      expect(commands.length).withContext('commands across three rows').toBe(10);
      expect(glyphs.length).withContext('one decorative glyph per command').toBe(commands.length);
    });

    it('does not let a command double as selecting its row', () => {
      arrive();

      // This screen binds no `rowSelect`, so the shared table offers no selection affordance on its rows at
      // all — a command therefore cannot double as one. Selection would be observable through
      // `aria-selected`, so its absence is too.
      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(query('tbody tr[aria-selected="true"]').length)
        .withContext('a command must not select the row')
        .toBe(0);

      // And nothing was written: a move is staged locally and applied later.
      httpMock.expectNone(() => true);
    });

    it('offers NO row-selection affordance at all, this screen listening for none', () => {
      arrive();

      const rows: readonly HTMLTableRowElement[] = query<HTMLTableRowElement>('tbody tr');

      expect(rows.length).toBeGreaterThan(0);

      for (const row of rows) {
        expect(row.hasAttribute('aria-selected'))
          .withContext('a row nothing listens to must announce no selection state')
          .toBeFalse();
        expect(row.getAttribute('tabindex'))
          .withContext('a row nothing listens to must not be a tab stop')
          .toBeNull();
      }

      const nameCell = rows.at(0)?.querySelectorAll('td,th').item(4);

      nameCell?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      fixture.detectChanges();

      expect(query('tbody tr[aria-selected="true"]').length).toBe(0);
      httpMock.expectNone(() => true);
    });

    it('keeps every row command reachable by keyboard, the rows themselves not being stops', () => {
      // Withdrawing the row from the tab order must not withdraw anything INSIDE it: the commands are what
      // this grid exists to offer, and they are native controls, so each is a stop in its own right. Were
      // the row itself a stop, a reader would pass through every row to reach them.
      arrive();

      const commands: readonly HTMLButtonElement[] = query<HTMLButtonElement>('tbody button');

      expect(commands.length).toBeGreaterThan(0);

      for (const command of commands) {
        expect(command.getAttribute('tabindex'))
          .withContext('a native control needs no explicit index, and must not be removed from the order')
          .not.toBe('-1');
        expect(command.hasAttribute('disabled'))
          .withContext('a reachable command must not be inert')
          .toBeFalse();
      }
    });
  });

  // The two boolean columns

  describe('the boolean columns', () => {
    it('render a real check box per row, never a bare icon', () => {
      arrive();

      expect(rowCheckboxes('required').length).toBe(3);
      expect(rowCheckboxes('visible').length).toBe(3);
    });

    it('name each box by its row as well as its column, so the two are never the same name', () => {
      arrive();

      const cells = query<HTMLTableRowElement>('tbody tr').at(0)?.querySelectorAll('td,th');
      const requiredName = (
        cells?.item(10)?.querySelector('[data-visually-hidden]')?.textContent ?? ''
      ).trim();
      const visibleName = (
        cells?.item(11)?.querySelector('[data-visually-hidden]')?.textContent ?? ''
      ).trim();

      expect(requiredName).toBe('Required \u2014 Nickname');
      expect(visibleName).toBe('Visible \u2014 Nickname');

      // The row is named in both, so neither reads as a column heading detached from its subject.
      expect(requiredName).withContext('names its row').toContain('Nickname');
      expect(visibleName).withContext('names its row').toContain('Nickname');

      // And the whole point: the two are distinguishable.
      expect(requiredName).withContext('the two boxes are told apart').not.toBe(visibleName);
    });

    it('publish the state as a word beside the box, and treat false as data', () => {
      arrive([
        definition({ propertyDefinitionId: 61, propertyName: 'Quiet', required: false, visible: false }),
      ]);

      const cells = query<HTMLTableCellElement>('tbody tr td,tbody tr th');

      // `Null.NullBoolean` is literally `False`, so `false` is a VALUE: it reads as the negative word and as
      // an unchecked box, never as blank.
      expect((cells.at(10)?.textContent ?? '')).toContain('No');
      expect((cells.at(11)?.textContent ?? '')).toContain('No');
      expect(rowCheckboxes('required').at(0)?.checked).toBeFalse();
      expect(rowCheckboxes('visible').at(0)?.checked).toBeFalse();
    });
  });

  // A REFUSED REPLACE — a conflict and a disappearance are DIFFERENT OUTCOMES

  describe('a refused replace', () => {
    /** Opens the editor on one declaration and submits it, leaving the replace pending. */
    function submitReplace(): TestRequest {
      arrive([definition({ propertyDefinitionId: 141, propertyName: 'Nickname', viewOrder: 0 })]);

      buttonsNamed('Edit').at(0)?.click();
      fixture.detectChanges();
      type('profile-definition-category', 'Social');
      press(EDIT_SUBMIT_LABEL);

      return expectRequest('PUT', definitionUrl(141), 'the replace');
    }

    it('reports a conflict at error severity, in the words the server chose', () => {
      submitReplace().flush(
        {
          type: 'about:blank',
          title: 'Conflict',
          status: 409,
          detail: 'This property was changed by someone else.',
        },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // 409 is a fault on the caller's terms, so it is an ERROR — the same severity the legacy
      // duplicate-name refusal chose (`ModuleMessageType.RedError`).
      expect(notify).toHaveBeenCalledWith('error', 'This property was changed by someone else.', null);
    });

    it('reports a declaration that has already gone in its OWN words, and more quietly', () => {
      submitReplace().flush(problem(404), { status: 404, statusText: 'Not Found' });
      fixture.detectChanges();

      // Distinct from the conflict on both axes. The component substitutes its own sentence for the
      // server's generic one, because "no longer exists" is actionable where "not found" is not; and the
      // severity is a WARNING, because nothing is broken — someone else got there first.
      expect(notify).toHaveBeenCalledWith('warning', DEFINITION_GONE_MESSAGE, null);
      expect(notify).not.toHaveBeenCalledWith('error', DEFINITION_GONE_MESSAGE, null);
    });
  });

  // Field-level refusals the server reports

  describe('a rejected create', () => {
    /** Submits a valid-looking create and answers it with a per-field refusal. */
    function submitAndReject(messages: readonly string[]): void {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', DEFINITIONS_URL, 'the create').flush(
        {
          type: 'about:blank',
          title: 'One or more validation errors occurred.',
          status: 400,
          detail: 'The request was not valid.',
          // The per-field dictionary is an index-signature map on the contract, which is why the component
          // reads it with a bracket rather than a property. Property access on it is a compilation error in
          // this workspace by design.
          errors: { propertyName: messages },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();
    }

    it('renders the field messages the server reported beneath the control', () => {
      submitAndReject(['Property Name may contain only letters, numbers and the characters . _ % - +']);

      expect(text()).toContain('Property Name may contain only letters, numbers');
      // The form stays open on a refusal, so the value can be corrected rather than retyped.
      expect(text()).toContain(CREATE_HEADING);
      expect(control('profile-definition-name').value).toBe('Twitter');
    });

    it('strips the legacy leading break tag from a reported message', () => {
      // 28 of the 34 in-scope legacy validator messages begin with a `<br>`, because the legacy rendered
      // them inline after the control. Carried through verbatim it would paint as literal text, since
      // nothing here treats a message as markup.
      submitAndReject(['<br>The Property Name is required']);

      expect(text()).toContain(NAME_REQUIRED_MESSAGE);
      expect(text()).not.toContain('<br>');
    });

    it('does not adopt an edit identifier, so the retry is still a create', () => {
      submitAndReject(['The Property Name is required']);

      type('profile-definition-name', 'TwitterHandle');
      press(CREATE_SUBMIT_LABEL);

      expect(pendingWrites('PUT').length).toBe(0);
      expectRequest('POST', DEFINITIONS_URL, 'the retry').flush(
        envelope(definition({ propertyDefinitionId: 97 })),
        { status: 201, statusText: 'Created' },
      );
      settleReReads();
    });
  });

  // A PERMISSION REFUSAL — a warning, not a fault

  describe('a permission refusal', () => {
    it('is announced as a warning rather than as an error', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', DEFINITIONS_URL, 'the create').flush(problem(403), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      const severities = notify.calls.allArgs().map((args) => args.at(0));

      expect(severities).toContain('warning');
      expect(severities).not.toContain('error');
    });
  });

  // The removal dialog's keyboard contract

  describe('the removal dialog', () => {
    function openDialog(): void {
      arrive();
      buttonsNamed('Delete').at(0)?.click();
      fixture.detectChanges();
    }

    it('is a modal alert dialog, which the legacy browser confirm never was', () => {
      openDialog();

      const dialog = query<HTMLElement>('app-confirm-dialog dialog[role="alertdialog"]');

      expect(dialog.length).toBe(1);
      expect(dialog.at(0)?.getAttribute('aria-modal')).toBe('true');
      expect(dialog.at(0)?.getAttribute('aria-labelledby')).toBeTruthy();
      expect(dialog.at(0)?.getAttribute('aria-describedby')).toBeTruthy();

      // Two focusable commands inside it, which is what the trap cycles between.
      expect(query<HTMLElement>('app-confirm-dialog dialog button').length).toBe(2);
    });

    it('closes on Escape without removing anything', () => {
      openDialog();

      const dialog = query<HTMLElement>('app-confirm-dialog dialog').at(0);

      dialog?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();

      expect(query('app-confirm-dialog dialog').length).withContext('the dialog closed').toBe(0);
      // Escape is a dismissal, not a confirmation. a regression that routed it to `confirm` would delete a
      // row on a key press meant to back out.
      httpMock.expectNone(() => true);
    });
  });

  // THE FORM'S SHAPE — field order, and what is deliberately NOT a field

  describe('the form layout', () => {
    it('renders the nine fields in the authoritative SortOrder sequence', () => {
      arrive();
      press(ADD_LABEL);

      // MCC-4: the order is the `SortOrder` attributes on the legacy definition class, which is what the
      // excluded property editor reflected over under `SortMode="SortOrderAttribute"`. It is deliberately
      // neither alphabetical nor the order the wire contract lists.
      const rendered = query<HTMLElement>('form input, form select, form textarea').map(
        (field) => field.id,
      );

      expect(rendered).toEqual(FIELD_CONTROL_IDS);
    });

    it('renders each field inside a shared field wrapper that labels its control', () => {
      arrive();
      press(ADD_LABEL);

      expect(query('form app-form-field').length).toBe(9);

      for (const id of FIELD_CONTROL_IDS) {
        const labels = query<HTMLLabelElement>(`form label[for="${id}"]`);

        expect(labels.length).withContext(`a label bound to #${id}`).toBe(1);
      }
    });

    it('renders the form as a fieldset with a legend, not as a tenth shared component', () => {
      arrive();
      press(ADD_LABEL);

      const legends = query<HTMLLegendElement>('form fieldset legend');

      expect(legends.length).toBe(1);
      expect((legends.at(0)?.textContent ?? '').trim()).toBe(CREATE_HEADING);
    });

    it('offers no field for the members the legacy marked unbrowsable', () => {
      arrive();
      press(ADD_LABEL);

      for (const label of NON_BROWSABLE_LABELS) {
        expect(text()).withContext(`no field labelled "${label}"`).not.toContain(label);
      }

      expect(query('form [id="profile-definition-portal"]').length).toBe(0);
      expect(query('form [id="profile-definition-visibility"]').length).toBe(0);
    });

    it('carries no delete affordance, because the legacy handler was dead code', () => {
      arrive();
      buttonsNamed('Edit').at(0)?.click();
      fixture.detectChanges();

      // `cmdDelete_Click` existed but had NO `Handles` clause and no control to raise it, so the legacy
      // editor could not delete anything. Removal lives on the grid row.
      expect(query('form button').length).withContext('submit and cancel only').toBe(2);
      expect(query<HTMLButtonElement>('form button').map((each) => (each.textContent ?? '').trim()))
        .toEqual([EDIT_SUBMIT_LABEL, CANCEL_LABEL]);
    });
  });

  // WORDING PARITY — every string is a resource VALUE, never a markup attribute

  describe('the published wording', () => {
    /** The wording of one label, without the required marker the shared field appends. */
    function labelWording(label: HTMLLabelElement): string {
      return Array.from(label.childNodes)
        .filter((node) => node.nodeType === Node.TEXT_NODE)
        .map((node) => node.textContent ?? '')
        .join('')
        .trim();
    }

    it('renders the nine field labels in the legacy wording, in the legacy order', () => {
      arrive();
      press(ADD_LABEL);

      const rendered = query<HTMLLabelElement>('form label.form-field__label').map(labelWording);

      expect(rendered).toEqual(FIELD_LABELS.map((label) => label.replace(/:$/u, '')));
    });

    it('marks the four members the legacy declared Required(True), and the length as well', () => {
      arrive();
      press(ADD_LABEL);

      const marked = query<HTMLLabelElement>('form label.form-field__label')
        .filter((label) => label.querySelector('.form-field__required-text') !== null)
        .map(labelWording);

      expect(marked).toEqual([
        'Property Name',
        'Data Type',
        'Property Category',
        'Length',
        'View Order',
      ]);
      expect(query('form [aria-required="true"]').length).toBe(5);
    });

    it('refuses an emptied numeric field by name instead of sending an absence', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm('Nickname');

      for (const field of [
        'profile-definition-length',
        'profile-definition-view-order',
        'profile-definition-data-type',
      ]) {
        type(field, '');
      }

      press(CREATE_SUBMIT_LABEL);

      const rendered = text();

      expect(rendered).toContain(LENGTH_REQUIRED_MESSAGE);
      expect(rendered).toContain(VIEW_ORDER_REQUIRED_MESSAGE);
      expect(rendered).toContain(DATA_TYPE_REQUIRED_MESSAGE);

      expect(rendered)
        .withContext('an empty box is not also reported as a non-integer')
        .not.toContain(WHOLE_NUMBER_MESSAGE);

      httpMock.expectNone(() => true);
    });

    it('paints no trailing colon, because the shared field normalises it exactly once', () => {
      arrive();
      press(ADD_LABEL);

      for (const label of query<HTMLLabelElement>('form label.form-field__label')) {
        expect((label.textContent ?? '').trim())
          .withContext('a label must not end in a colon')
          .not.toMatch(/:$/u);
      }
    });

    it('preserves the legacy misspelling in the category hint', () => {
      arrive();
      press(ADD_LABEL);

      expect(text()).toContain(CATEGORY_HELP_WITH_LEGACY_TYPO);
      expect(text()).not.toContain('grouped when displayed to the user');
    });

    it('names the grid action Refresh Grid, which is the resource value and not the attribute', () => {
      arrive();

      // The markup attribute read only "Refresh"; `cmdRefresh.Text` reads "Refresh Grid", and
      // `LocalizeDataGrid` replaced the attribute with the resource value before anything painted.
      expect(button(REFRESH_LABEL)).toBeTruthy();
      expect(button(APPLY_LABEL)).toBeTruthy();
      expect(buttonsNamed(ADD_LABEL).length).toBe(1);
    });

    it('uses Validation Expression as a column heading and never as an error message', () => {
      arrive();
      press(ADD_LABEL);
      // The rule used is the LENGTH one, deliberately.
      type('profile-definition-expression', 'a'.repeat(513));
      press(CREATE_SUBMIT_LABEL);

      const headings = query<HTMLTableCellElement>('thead th').map((cell) =>
        (cell.textContent ?? '').trim(),
      );

      expect(headings).toContain('Validation Expression');

      const messages = query<HTMLElement>('form app-form-field [role="alert"], form .form-field__error')
        .map((each) => (each.textContent ?? '').trim());

      for (const message of messages) {
        expect(message).withContext('a heading is not an error sentence').not.toBe('Validation Expression');
      }
    });
  });

  describe('tidying the keyed strings before judging them', () => {
    /** The wording of every field message the form is currently showing. */
    function formMessages(): readonly string[] {
      return query<HTMLElement>('form app-form-field [role="alert"], form .form-field__error').map(
        (each) => (each.textContent ?? '').trim(),
      );
    }

    it('REFUSES a whitespace-only category rather than sending an empty one', () => {
      arrive();
      press(ADD_LABEL);

      type('profile-definition-name', 'Twitter');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', '   ');

      press(CREATE_SUBMIT_LABEL);

      // ⚠ NOTHING IS SENT. This is the assertion the defect failed: a `POST` went out carrying
      // `propertyCategory: ""`, and the server refused it.
      expect(pendingWrites('POST')).toHaveSize(0);

      // And the requirement is reported, beside a field the operator did fill in — they typed
      // something, so they are owed an explanation of why it amounts to nothing.
      expect(formMessages().length)
        .withContext('the form now complains rather than deferring to the server')
        .toBeGreaterThan(0);
    });

    it('TIDIES a padded category into the control, so what is shown is what is sent', () => {
      arrive();
      press(ADD_LABEL);

      type('profile-definition-name', 'Twitter');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', '  Contact  ');

      press(CREATE_SUBMIT_LABEL);

      const written = expectRequest('POST', DEFINITIONS_URL, 'the create');
      const body = written.request.body as { readonly propertyCategory: string };

      expect(body.propertyCategory).toBe('Contact');

      // The control agrees with the payload, so nobody is left looking at an entry that differs from
      // the one that was accepted.
      expect(control('profile-definition-category').value).toBe('Contact');

      written.flush(envelope(definition({ propertyDefinitionId: 9 })), {
        status: 201,
        statusText: 'Created',
      });
      settleReReads();
    });

    it('still ACCEPTS a category that tidying merely shortens, which must not regress', () => {
      // ⚠ THE BOUNDARY. A guard that refused everything tidying touched would refuse the case above,
      // so this pins that tidying to a NON-EMPTY value is unaffected by the re-judgement.
      arrive();
      press(ADD_LABEL);

      type('profile-definition-name', 'Twitter');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', 'Contact ');

      press(CREATE_SUBMIT_LABEL);

      const written = expectRequest('POST', DEFINITIONS_URL, 'the create');

      expect(formMessages()).toEqual([]);

      written.flush(envelope(definition({ propertyDefinitionId: 9 })), {
        status: 201,
        statusText: 'Created',
      });
      settleReReads();
    });

    it('lets a corrected category through immediately, so the refusal does not latch', () => {
      arrive();
      press(ADD_LABEL);

      type('profile-definition-name', 'Twitter');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', '   ');
      press(CREATE_SUBMIT_LABEL);

      expect(pendingWrites('POST')).toHaveSize(0);

      // The operator reads the message and types a real category. Nothing else is re-entered.
      type('profile-definition-category', 'Contact');
      press(CREATE_SUBMIT_LABEL);

      const written = expectRequest('POST', DEFINITIONS_URL, 'the create');
      const body = written.request.body as { readonly propertyCategory: string };

      expect(body.propertyCategory).toBe('Contact');

      written.flush(envelope(definition({ propertyDefinitionId: 9 })), {
        status: 201,
        statusText: 'Created',
      });
      settleReReads();
    });

    it('leaves the two FREE-TEXT members untouched, spaces and all', () => {
      arrive();
      press(ADD_LABEL);

      type('profile-definition-name', 'Twitter');
      type('profile-definition-data-type', '349');
      type('profile-definition-category', 'Contact');
      type('profile-definition-default-value', '  padded  ');

      press(CREATE_SUBMIT_LABEL);

      const written = expectRequest('POST', DEFINITIONS_URL, 'the create');
      const body = written.request.body as { readonly defaultValue: string };

      expect(body.defaultValue).toBe('  padded  ');

      written.flush(envelope(definition({ propertyDefinitionId: 9 })), {
        status: 201,
        statusText: 'Created',
      });
      settleReReads();
    });
  });

  // =============================================================================================
  // THE NAME PATTERN — DL-10: the rule is far narrower than the sentence admits
  // =============================================================================================

  describe('the property-name rule', () => {
    /** Attempts a create with one candidate name and reports whether the pattern refused it. */
    function refusedForPattern(candidate: string): boolean {
      arrive();
      press(ADD_LABEL);
      type('profile-definition-name', candidate);
      type('profile-definition-data-type', '349');
      type('profile-definition-category', 'Contact');
      press(CREATE_SUBMIT_LABEL);

      const refused = text().includes(NAME_PATTERN_MESSAGE);

      if (!refused) {
        // Consume the write the acceptance produced, so the verification in `afterEach` is asserting about
        // unexpected requests rather than about this one.
        expectRequest('POST', DEFINITIONS_URL, `the create of "${candidate}"`).flush(
          envelope(definition({ propertyDefinitionId: 96 })),
          { status: 201, statusText: 'Created' },
        );
        settleReReads();
      }

      return refused;
    }

    it("admits every character the legacy pattern admits: . _ % - + and an apostrophe", () => {
      // The rule really is `^[a-zA-Z0-9._%\-+']+$`, so each of these is legitimate.
      for (const candidate of ['first_name', 'a.b', 'a%b', 'a-b', 'a+b', "o'brien", 'Twitter2']) {
        expect(refusedForPattern(candidate)).withContext(`"${candidate}" is valid`).toBeFalse();
      }
    });

    it('refuses every character outside that class, not merely a space', () => {
      // The resource sentence says only "cannot contain spaces", which UNDERSTATES the rule it describes: a
      // slash, a hash and a comma are refused too.
      for (const candidate of ['a/b', 'a#b', 'a b', 'a,b', 'a:b', 'a(b']) {
        expect(refusedForPattern(candidate)).withContext(`"${candidate}" is invalid`).toBeTrue();
      }
    });

    it('reports the understated sentence the legacy published, not a reworded one', () => {
      expect(refusedForPattern('First Name')).toBeTrue();
      expect(text()).toContain(NAME_PATTERN_MESSAGE);
    });
  });

  // Zeros are real values

  describe('zero-valued members', () => {
    it('treats a length of zero as data, and round-trips it through a replace', () => {
      arrive([
        definition({
          propertyDefinitionId: 151,
          propertyName: 'Nickname',
          length: 0,
          viewOrder: 0,
        }),
      ]);

      // Rendered as the digit, never as blank: `Null.NullInteger` is -1, so 0 is not the sentinel and a
      // truthiness test on it would have painted an empty cell.
      expect((query<HTMLTableCellElement>('tbody tr td,tbody tr th').at(7)?.textContent ?? '').trim()).toBe('0');

      buttonsNamed('Edit').at(0)?.click();
      fixture.detectChanges();

      expect(control('profile-definition-length').value).toBe('0');
      expect(control('profile-definition-view-order').value).toBe('0');

      type('profile-definition-category', 'Social');
      press(EDIT_SUBMIT_LABEL);

      const write = expectRequest('PUT', definitionUrl(151), 'the replace');

      // Present and zero — never omitted, never coerced to null and never widened to one.
      expect(write.request.body).toEqual(
        jasmine.objectContaining({ length: 0, viewOrder: 0 }),
      );

      write.flush(envelope(definition({ propertyDefinitionId: 151 })));
      settleReReads();
    });

    it('accepts a view order of zero as present, because required admits zero', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();
      type('profile-definition-view-order', '0');
      press(CREATE_SUBMIT_LABEL);

      // Angular's emptiness test is `value == null || ((string | array) && length === 0)`, so a numeric 0
      // PASSES. This case exists to lock out a hand-rolled truthiness check, which would refuse the first
      // row's legitimate position.
      expect(text()).not.toContain('The View Order is required');

      expectRequest('POST', DEFINITIONS_URL, 'the create').flush(
        envelope(definition({ propertyDefinitionId: 95 })),
        { status: 201, statusText: 'Created' },
      );
      settleReReads();
    });

    it('carries a portal identifier of zero as a real tenant, never as unset', () => {
      // `Portals.PortalID` is `IDENTITY(-1,1)`, so the FIRST real portal is 0 and -1 is simultaneously a
      // real row and the `NullInteger` sentinel. Both must survive as data.
      arrive([
        definition({ propertyDefinitionId: 161, propertyName: 'Zeroth', portalId: 0 }),
        definition({ propertyDefinitionId: 162, propertyName: 'Hosted', portalId: -1, viewOrder: 1 }),
      ]);

      expect(renderedNames()).toEqual(['Zeroth', 'Hosted']);

      // Reported gap, asserted as the absence it is: the tenant is NOT a query parameter on this read. The
      // API resolves it from the request, so there is no `portalId` argument to carry — and a regression
      // that started sending one would be silently scoping the read.
      const reread = httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
      );

      expect(reread.length).toBe(0);
    });
  });

  // Tenant-authored text is text, never markup

  describe('untrusted values', () => {
    it('renders markup in a default value and an expression as escaped text', () => {
      arrive([
        definition({
          propertyDefinitionId: 171,
          propertyName: 'Risky',
          defaultValue: '<b>bold</b>',
          validationExpression: '<script>alert(1)</script>',
        }),
      ]);

      const cells = query<HTMLTableCellElement>('tbody tr td,tbody tr th');

      // The angle brackets survive as CHARACTERS, which is only possible if the value was interpolated.
      // Nothing on this screen binds `innerHTML` or reaches for a sanitiser.
      expect((cells.at(8)?.textContent ?? '').trim()).toBe('<b>bold</b>');
      expect((cells.at(9)?.textContent ?? '').trim()).toBe('<script>alert(1)</script>');

      // And no element was created from either value.
      expect(query('tbody tr b').length).toBe(0);
      expect(query('tbody tr script').length).toBe(0);
    });

    it('distinguishes no expression at all from one that deliberately matches nothing', () => {
      arrive([
        definition({ propertyDefinitionId: 181, propertyName: 'Open', validationExpression: '' }),
        definition({
          propertyDefinitionId: 182,
          propertyName: 'Closed',
          validationExpression: '(?!)',
          viewOrder: 1,
        }),
      ]);

      const cells = query<HTMLTableCellElement>('tbody tr').map((row) =>
        (row.querySelectorAll('td,th').item(9)?.textContent ?? '').trim(),
      );

      // `Null.NullString` IS the empty string, so "" means "no rule" and is NOT the same as a rule that
      // refuses everything. Rendering both as blank would erase a real distinction.
      //
      // ⚠ AND "NO RULE" IS NOW SAID RATHER THAN LEFT BLANK, WHICH IS THE SAME DISTINCTION DRAWN BETTER. This
      // case previously required an empty cell for the absent rule, which made the distinction rest entirely on
      // one cell being empty — and an empty cell reads as a rendering failure, not as a fact. It carries the
      // shared marker now: the two rows are still unmistakably different, and the difference is legible instead
      // of inferred.
      expect(cells.at(0))
        .withContext('no rule at all, stated')
        .toContain('not recorded');
      expect(cells.at(0)).not.toBe('(?!)');
      expect(cells.at(1))
        .withContext('a rule that refuses everything is a real value and is painted verbatim')
        .toBe('(?!)');
    });

    it('renders a help string containing markup as plain text', () => {
      arrive();
      press(ADD_LABEL);

      // This folder's `Introduction_Add.Help` provably carries an escaped `<b>Note:</b>`. Whatever a hint
      // holds, it is projected as text.
      expect(query('form b').length).toBe(0);
      expect(query('form strong').length).toBe(0);
    });
  });

  // THE LIVE REGIONS

  describe('the announcements', () => {
    it('announces a failure assertively through the shared banner', () => {
      create();

      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(problem(500), {
        status: 500,
        statusText: 'Server Error',
      });
      fixture.detectChanges();

      const live = query<HTMLElement>('app-error-banner [aria-live]');

      expect(live.length).toBeGreaterThan(0);
      expect(live.at(0)?.getAttribute('aria-live')).toBe('assertive');
      expect(live.at(0)?.getAttribute('role')).toBe('alert');
    });

    it('announces the staged-edit count politely, so it does not interrupt', () => {
      arrive();
      toggle(rowCheckboxes('required').at(0));

      const polite = query<HTMLElement>('[aria-live="polite"]');

      expect(polite.length).toBeGreaterThan(0);
      expect(text()).toContain('1 unapplied change(s).');
    });
  });
  // THE BATCH, THE BANNER AND WHO OWNS A WRITE REFUSAL
  // WRITE IDENTITY IN A BATCH. Apply dispatches ONE WRITE PER EDITED ROW, in parallel.

  describe('the batch, the banner and who owns a refusal', () => {
    it('reports every refused row, each naming its own property', () => {
      // Two rows edited, both refused. Two messages, and each says which property it is about.
      //
      // MIGRATION: DRIVEN BY FLAG EDITS RATHER THAN BY A MOVE, and the difference is the point of the
      // split. A flag is a fact about ONE declaration and holds or fails on its own, so it stays one write
      // per row and per-row attribution is exactly what is wanted. Positions are not: a move exchanges two
      // of them, so they are written as one unit of work and a refusal there belongs to the set — which is
      // covered by its own case below.
      arrive([
        definition({
          propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0, required: false,
        }),
        definition({
          propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1, required: false,
        }),
      ]);

      toggle(bulkToggles().at(0));
      press(APPLY_LABEL);

      const writes = driveBatch((write) =>
        write.flush(problem(409), { status: 409, statusText: 'Conflict' }),
      );

      expect(writes.length).withContext('one write per edited row').toBe(2);

      const announced: readonly string[] = notify.calls
        .allArgs()
        .map((args: readonly unknown[]) => String(args[1]));

      expect(announced.length)
        .withContext('one message per refused row, not one for the whole batch')
        .toBe(2);
      expect(announced.some((message) => message.startsWith('Nickname:'))).toBeTrue();
      expect(announced.some((message) => message.startsWith('Website:'))).toBeTrue();
    });

    it('reports a refused row even when another row of the same batch succeeded first', () => {
      // ⚠ THE CASE THE AGGREGATE FLAG COULD NOT EXPRESS. The successful row settles first; under the old
      // mechanism that lowered the flag and the screen treated the batch as finished, so the refusal
      // arriving afterwards had nothing left to attribute itself to.
      //
      // MIGRATION: DRIVEN BY FLAG EDITS RATHER THAN BY A MOVE, for the reason recorded on the case above:
      // the field dimension of the batch is per-row and this property is about the field dimension.
      arrive([
        definition({
          propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0, required: false,
        }),
        definition({
          propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1, required: false,
        }),
      ]);

      toggle(bulkToggles().at(0));
      press(APPLY_LABEL);

      // The batch is written in the order the declarations were READ, which is the order the store holds
      // them in — so the Nickname row is written first and the Website row second.
      const writes = driveBatch((write) =>
        write.request.url === definitionUrl(11)
          ? write.flush(envelope(definition({ propertyDefinitionId: 11 })))
          : write.flush(problem(409), { status: 409, statusText: 'Conflict' }),
      );

      expect(writes.length).withContext('one write per edited row').toBe(2);
      expect(writes.at(0)?.url)
        .withContext('the accepted row is the one written first')
        .toBe(definitionUrl(11));

      const announced: readonly string[] = notify.calls
        .allArgs()
        .map((args: readonly unknown[]) => String(args[1]));

      expect(announced.filter((message) => message.startsWith('Website:')).length)
        .withContext('the refusal is reported although a sibling row landed first')
        .toBe(1);
      expect(announced.length)
        .withContext('and the row that landed announces nothing')
        .toBe(1);
    });

    it('announces nothing for a batch that wholly succeeds', () => {
      // Parity: the legacy Apply reported nothing on success, and the dirty-row count already falls
      // by itself because it is derived from the difference between the staged and stored values.
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();
      press(APPLY_LABEL);

      // The ordering route answers with the whole catalogue in its new order, so the answer is a LIST
      // envelope. Answering it with a single declaration would fail the response contract and be reported
      // as a refusal, which is the opposite of what this case is about.
      driveBatch((write) => write.flush(envelope(catalogue())));

      expect(notify).withContext('a working apply says nothing').not.toHaveBeenCalled();
    });

    it('attributes an ORDER refusal to the order, not to either declaration, and applies no flag', () => {
      // ⚠ THE CASE THE SPLIT EXISTS FOR. A move exchanges two positions and is written as one unit of
      // work, so a refusal there is neither declaration's fault — naming one would tell the operator that
      // row was at fault when neither was, and would imply the other row's move landed. It did not.
      arrive([
        definition({
          propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0, required: false,
        }),
        definition({
          propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1, required: false,
        }),
      ]);

      // BOTH dimensions staged: the first row moves down AND every row's required flag is set.
      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();
      toggle(bulkToggles().at(0));
      press(APPLY_LABEL);

      const order: readonly TestRequest[] = pendingWrites('PUT');

      expect(order.length).withContext('the order write goes first, alone').toBe(1);
      expect(order[0].request.url).toBe(ORDER_URL);

      order[0].flush(problem(409), { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      // ⚠ NO FIELD WRITE FOLLOWS A REFUSED ORDER. Applying flags over an order the server has just
      // declined would leave the screen reporting a partial success it did not have.
      expect(pendingWrites('PUT').length)
        .withContext('a refused order abandons the field writes behind it')
        .toBe(0);

      const announced: readonly string[] = notify.calls
        .allArgs()
        .map((args: readonly unknown[]) => String(args[1]));

      expect(announced.length).withContext('one message, for the order').toBe(1);
      expect(announced[0]).toContain('Display order:');
      expect(announced[0])
        .withContext('and it says plainly that nothing at all was applied')
        .toContain('No display order was changed, and no other change was applied.');
      expect(announced.some((message) => message.startsWith('Nickname:')))
        .withContext('neither declaration is blamed for the set')
        .toBeFalse();
      expect(announced.some((message) => message.startsWith('Website:'))).toBeFalse();

      // The catalogue is re-read on BOTH outcomes — legacy parity, and it is also what puts the screen back
      // on the stored order after a refused move.
      settleReReads([
        definition({
          propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0, required: false,
        }),
        definition({
          propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1, required: false,
        }),
      ]);
    });

    it('keeps a batch refusal out of the banner, which is one surface too many', () => {
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();
      press(APPLY_LABEL);

      driveBatch((write) => write.flush(problem(409), { status: 409, statusText: 'Conflict' }));

      expect(notify).withContext('the notification reports it').toHaveBeenCalled();
      expect(query('.error-banner').length)
        .withContext('and the banner does not report it a second time')
        .toBe(0);
    });

    it('keeps exactly one row of the batch in flight at a time', () => {
      // ⚠ THE PROPERTY THAT MAKES EVERY OUTCOME OBSERVABLE. The store publishes ONE settled result at a
      // time, so two rows answered in the same turn would coalesce and the earlier one would never reach
      // the reporting effect — the identifier would be right and the effect would simply never be handed
      // it.
      arrive([
        definition({ propertyDefinitionId: 21, propertyName: 'A', viewOrder: 0, required: false }),
        definition({ propertyDefinitionId: 22, propertyName: 'B', viewOrder: 1, required: false }),
        definition({ propertyDefinitionId: 23, propertyName: 'C', viewOrder: 2, required: false }),
      ]);

      toggle(bulkToggles().at(0));

      expect(text()).toContain('3 unapplied change(s)');

      press(APPLY_LABEL);

      const observed: number[] = [];

      for (let guard = 0; guard < 4; guard += 1) {
        const outstanding = pendingWrites('PUT');

        observed.push(outstanding.length);

        if (outstanding.length === 0) {
          break;
        }

        outstanding[0].flush(envelope(definition()));
        fixture.detectChanges();
      }

      expect(observed)
        .withContext('one, then one, then one, then none — never two at once')
        .toEqual([1, 1, 1, 0]);

      drainReReads();
    });

    it('offers no way to discard the staged edits while the batch is still writing them', () => {
      // ⚠ THIS IS WHAT MAKES THE SNAPSHOT SAFE. Refresh is this screen's REVERT affordance — it throws
      // every staged edit away — so a queue that outlived those edits would go on storing values the
      // operator had just discarded and that are no longer anywhere on screen.
      arrive([
        definition({ propertyDefinitionId: 31, propertyName: 'A', viewOrder: 0, required: false }),
        definition({ propertyDefinitionId: 32, propertyName: 'B', viewOrder: 1, required: false }),
        definition({ propertyDefinitionId: 33, propertyName: 'C', viewOrder: 2, required: false }),
      ]);

      toggle(bulkToggles().at(0));
      press(APPLY_LABEL);

      for (let guard = 0; guard < 4; guard += 1) {
        const outstanding = pendingWrites('PUT');

        if (outstanding.length === 0) {
          break;
        }

        expect(button(REFRESH_LABEL).disabled)
          .withContext('the revert command is unavailable while a row is in flight')
          .toBeTrue();
        expect(button(APPLY_LABEL).disabled)
          .withContext('and so is a second batch on top of the first')
          .toBeTrue();

        outstanding[0].flush(envelope(definition()));
        fixture.detectChanges();
      }

      drainReReads();

      expect(button(REFRESH_LABEL).disabled)
        .withContext('and it is available again once the whole batch has settled')
        .toBeFalse();
    });

    it('keeps another screen\u2019s failure out of the banner entirely', () => {
      arrive();

      const store = TestBed.inject(UserStore);

      store.changePassword(7, {
        operation: 'change',
        currentPassword: 'old-secret-value',
        newPassword: 'new-secret-value',
        confirmPassword: 'new-secret-value',
      });
      httpMock
        .expectOne(
          (candidate) => candidate.method === 'POST' && candidate.url.endsWith('/7/password'),
          "a sibling screen's write",
        )
        .flush(problem(403), { status: 403, statusText: 'Forbidden' });
      fixture.detectChanges();

      expect(query('.error-banner').length)
        .withContext('this screen reports only what it can explain')
        .toBe(0);
    });

    it('shows the store\u2019s authored summary when a catalogue read fails with no document', () => {
      // ⚠ THE FAILURE THIS MAKES VISIBLE WAS COMPLETELY SILENT. The runtime decoders that check each
      // response against its published contract run inside the service's own mapping, DOWNSTREAM of the
      // interceptor's error handling — so a `200` whose body does not match its contract throws a plain
      // error with no document, no status and no support reference.
      create();
      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush({ unexpected: true });
      fixture.detectChanges();

      expect(query('.error-banner').length)
        .withContext('the failure is visible at all')
        .toBe(1);
      expect(text())
        .withContext('and the Refresh Grid command remains available as the retry path')
        .toContain(REFRESH_LABEL);
    });
  });
});
