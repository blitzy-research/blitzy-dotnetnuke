/**
 * Specification for {@link ProfileDefinitionListComponent} — the profile-property catalogue at
 * `/settings/profile-definitions`.
 *
 * It is THREE things sharing one surface: an unpaged grid, a batch of staged edits that are only written
 * when the operator says so, and an inline editor that both creates and replaces. The risk lives in the
 * seams between them — an edit staged in the grid must survive until Apply, must be discarded by Refresh,
 * and must not be lost when a sibling write in the same batch is refused.
 *
 * - Mounted as the standalone unit it is, with the REAL {@link UserStore} pinned to each case's injector,
 *   and every request answered through `HttpTestingController`.
 * - `NotificationService.notify` is spied and called through. No router is spied: this screen navigates
 *   nowhere at all, which is itself asserted.
 *
 * The read is unpaged and takes no parameter. `GET /api/v1/profile-definitions` carries no page coordinate
 * and no tenant argument — the API resolves the tenant from the request — and the body is an envelope around
 * a PLAIN ARRAY. A fixture shaped as a paged listing flushes successfully and unwraps to no rows at all.
 *
 * There is no reorder endpoint and no bulk endpoint. Moving a row is a swap of two positions and therefore
 * TWO replaces; setting a flag across the catalogue is one replace per affected row. Every case that
 * exercises either asserts the request COUNT as well as the bodies.
 *
 * A staged edit is derived, not held as a flag. "Unapplied" means "differs from what the server last
 * reported", so a row stops being unapplied the moment the server agrees with it — which is what makes a
 * partially refused batch recoverable by pressing Apply again.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

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

/**
 * Authored wording for the two numeric fields that had no absence rule at all.
 *
 * Restated here rather than imported, as every other sentence in this file is, so that a change to
 * either is detected HERE rather than silently agreed to.
 */
const LENGTH_REQUIRED_MESSAGE = 'The Length is required';
const VIEW_ORDER_REQUIRED_MESSAGE = 'The View Order is required';

/** The whole-number sentence, restated so a case can prove it is NOT shown for an empty box. */
const WHOLE_NUMBER_MESSAGE = 'Enter a whole number.';

/**
 * The API's own sentence for an over-long expression, reproduced by the client.
 *
 * The one expression rule that lives on both sides: a character count is a fact no regular
 * expression engine disagrees about, and the API applies the identical 512-character limit,
 * so the client can refuse it without pre-empting the server. The expression's SYNTAX is
 * deliberately not judged here — see the cross-dialect cases below.
 */
const EXPRESSION_TOO_LONG_MESSAGE = 'Validation Expression must be 512 characters or fewer';

/** `DuplicateName.Text`, double spaces included. A single space here fails the case, correctly. */
const DUPLICATE_NAME_MESSAGE =
  'This Property already exists.  Property Names must be unique.  Please select a different ' +
  'name for this property.';

/**
 * The eight headings the legacy published, plus the four it left empty.
 */
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

/**
 * The four command columns, whose headings the legacy resource file left as `<value />`.
 */
const COMMAND_HEADINGS = ['Edit', 'Delete', 'Move Down', 'Move Up'];

/**
 * The four names whose delete command the legacy hid.
 */
const UNDELETABLE = ['LastName', 'FirstName', 'TimeZone', 'PreferredLocale'];

/**
 * The string `app-data-table` projects into its own `<caption>`.
 *
 * NOT the page title, and the difference is deliberate rather than an oversight. The legacy grid had NO
 * caption at all, so the table reached a screen reader unnamed; the page heading is
 * `ControlTitle_manageprofile.Text` ("Manage Profile Properties") and is rendered by `app-page-header`.
 * Naming the table with the page's own heading would have announced the same words twice in the
 * accessibility tree while still not saying what the table contains, so the caption is its own sentence.
 * Both are asserted, each in its own place.
 */
const GRID_CAPTION = 'Profile properties declared for this site';

/**
 * The nine field labels, verbatim from the `ProfilePropertyDefinition_<member>.Text` values, in the
 * `SortOrder` order the form renders them.
 */
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
 * `ProfilePropertyDefinition_PropertyCategory.Help`, reproduced byte for byte.
 *
 * The misspelling is the product's and is preserved (mcc-1). "dislayed" is what the legacy resource value
 * reads; correcting it here would be an unrequested content change in a migration whose whole discipline is
 * behavioural equivalence. Both double spaces are load-bearing too.
 */
const CATEGORY_HELP_WITH_LEGACY_TYPO =
  'Enter the category for this property.  This will allow the related properties to be ' +
  'grouped when dislayed to the user.';

/**
 * The sentence the component publishes for a declaration that has already been removed.
 */
const DEFINITION_GONE_MESSAGE = 'That profile property no longer exists. The list has been refreshed.';

/**
 * The four members `Browsable(False)` kept out of the legacy property editor.
 */
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

  /**
   * Three declarations in position order, none of them one of the four undeletable names.
   */
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

  /**
   * Creates the component and runs the first change detection.
   */
  function create(): void {
    fixture = TestBed.createComponent(ProfileDefinitionListComponent);
    fixture.detectChanges();
  }

  /**
   * Consumes exactly one pending request, asserted by verb AND address.
   */
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

  /**
   * Consumes every pending write against a definition, in the order they were issued.
   */
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
   * Drives a whole Apply batch to completion, answering each row in turn.
   *
   * ⚠ APPLY WRITES ONE ROW AT A TIME, SO A BATCH CANNOT BE MATCHED IN ONE CALL. The screen keeps
   * exactly one replace outstanding and dispatches the next only once that one has settled, which is
   * what makes every row's outcome observable: the store publishes ONE settled result at a time, so
   * two answers landing in the same turn would coalesce and the earlier row's outcome would never
   * reach the reporting effect. Serialising also stops n simultaneous writes renumbering the same
   * display-position column against each other.
   *
   * Each row is recorded BEFORE it is answered, because answering it releases the request and the
   * assertions in these cases are about what was asked for.
   *
   * @param answer Answers one row. Called once per row, in dispatch order.
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
   * Drains every catalogue re-read outstanding, tolerating there being none.
   *
   * A batch whose every row was refused provokes no re-read at all, so unlike {@link settleReReads}
   * this makes no claim that one was made. All but the last are cancelled — the store abandons an
   * outstanding read before dispatching another — and a cancelled request cannot be flushed.
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
    expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(envelope(rows));
    fixture.detectChanges();
  }

  /**
   * Flushes the ONE catalogue re-read a settled batch provokes.
   *
   * ⚠ EXACTLY ONE, AND THE COUNT IS THE ASSERTION. The batch used to be n independent writes, each
   * refreshing the whole catalogue when it landed, so n staged rows produced n reads of which n-1
   * were cancelled by the next - a read/write storm to apply a handful of check-box edits. The store
   * now reads the catalogue once, after the last row has settled, exactly as the legacy Apply
   * handler rebound its grid once after its sequential loop (`ProfileDefinitions.ascx.vb`
   * L446-L448). More than one read here is that defect returning.
   *
   * @param rows The catalogue to answer the read with.
   */
  function settleReReads(rows: readonly ProfilePropertyDefinition[] = catalogue()): void {
    const reads = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
    );

    expect(reads.length).withContext('exactly one catalogue re-read per batch').toBe(1);

    reads[0].flush(envelope(rows));
    fixture.detectChanges();
  }

  /**
   * Drives a staged batch to completion, one replace at a time.
   *
   * ⚠ THE BOUND IS WHAT THIS HELPER PROVES. A batch issues its next replace only once the previous
   * one has settled, so at every step there is EXACTLY ONE write in flight however many rows were
   * staged. It returns the addresses in the order they were written, so a case can assert the order
   * as well as the count.
   *
   * @param expected How many replaces the batch should consist of.
   * @param answer Answers one replace, defaulting to acceptance. Receives the zero-based step.
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

  /**
   * Every button whose rendered text contains the wording, in document order.
   */
  function buttonsNamed(wording: string): readonly HTMLButtonElement[] {
    return query<HTMLButtonElement>('button').filter((button) =>
      (button.textContent ?? '').includes(wording),
    );
  }

  /**
   * Exactly one button by its rendered wording.
   */
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

  /**
   * A button inside the removal dialog, by its rendered wording.
   *
   * Scoped to the dialog deliberately: the grid also renders one "Delete" command per removable row, so an
   * unscoped search for that wording finds four buttons and cannot say which is the confirmation.
   */
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

  /**
   * The property names in the order the grid renders them.
   */
  function renderedNames(): readonly string[] {
    return query<HTMLTableRowElement>('tbody tr').map((row) => {
      const cells = Array.from(row.querySelectorAll('td'));

      // Column four is the name; the four commands come first.
      return (cells.at(4)?.textContent ?? '').trim();
    });
  }

  /**
   * The grid's own check box for a column, one per row, in rendered order.
   */
  function rowCheckboxes(column: 'required' | 'visible'): readonly HTMLInputElement[] {
    const offset = column === 'required' ? 10 : 11;

    return query<HTMLTableRowElement>('tbody tr').flatMap((row) => {
      const cell = Array.from(row.querySelectorAll('td')).at(offset);
      const box = cell?.querySelector<HTMLInputElement>('input[type="checkbox"]');

      return box === null || box === undefined ? [] : [box];
    });
  }

  /**
   * The two bulk toggles, which sit outside the table.
   */
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

  /**
   * Fills the create form with a valid declaration.
   */
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

    it('renders an unresolved data type as empty rather than as the sentinel', () => {
      arrive([definition({ propertyDefinitionId: 41, propertyName: 'Unknown', dataType: -1 })]);

      const cells = query<HTMLTableCellElement>('tbody tr td');

      expect((cells.at(6)?.textContent ?? '').trim()).toBe('');
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

    it('stages two rows for one move, and applies them as two replaces IN TURN', () => {
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      const bodies = new Map<string, unknown>();

      // ⚠ ONE AT A TIME, AND THE HELPER ASSERTS IT AT EVERY STEP. A move stages two rows because a
      // position swap is two replaces, and the two used to be issued together; the batch now issues
      // the second only once the first has settled.
      const written = settleBatch(2, (write) => {
        bodies.set(write.request.url, write.request.body);
        write.flush(envelope(definition()));
      });

      expect(written).toEqual([definitionUrl(11), definitionUrl(12)]);
      expect(bodies.get(definitionUrl(11))).toEqual(
        jasmine.objectContaining({ propertyName: 'Nickname', viewOrder: 1 }),
      );
      expect(bodies.get(definitionUrl(12))).toEqual(
        jasmine.objectContaining({ propertyName: 'Website', viewOrder: 0 }),
      );

      settleReReads();
    });

    it('carries all nine writable members on a move, because the verb replaces', () => {
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

      let firstBody: unknown = null;

      settleBatch(2, (write, step) => {
        if (step === 0) {
          expect(write.request.url).toBe(definitionUrl(91));
          firstBody = write.request.body;
        }

        write.flush(envelope(definition()));
      });

      expect(firstBody).toEqual({
        propertyName: 'Alpha',
        propertyCategory: 'Group',
        dataType: 350,
        defaultValue: 'x',
        length: 40,
        required: true,
        validationExpression: '^a$',
        viewOrder: 1,
        visible: true,
      });

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

  // =============================================================================================
  // ROW IDENTITY ACROSS A STAGED EDIT
  // =============================================================================================
  //
  // ⚠⚠ THE DEFECT THESE CASES EXIST FOR WAS A LOST FOCUS ON EVERY SINGLE EDIT. The shared table
  // tracks its rows by OBJECT REFERENCE — it reads no identifier member, its column descriptor
  // names no key field, and adding one would widen a closed component surface — so a row handed
  // over as a new object has its `<tr>` destroyed and rebuilt, taking every control inside it with
  // it. This screen re-projected a row on every staged change, so:
  //
  //   * PRESSING A ROW CHECKBOX removed that very checkbox from the document. Focus fell back to
  //     the document body, and an operator working down the grid by keyboard was returned to the
  //     top of the page on every toggle.
  //   * PRESSING MOVE UP OR MOVE DOWN was worse, because a move restages TWO rows and the button
  //     pressed is on one of them. The reader lost their place at the exact moment they were
  //     working through the order — and the two rows are adjacent, so the correct outcome is that
  //     focus FOLLOWS the record to its new position.
  //
  // The screen now keeps one stable instance per `propertyDefinitionId` and refreshes its members
  // in place, publishing a NEW ARRAY of those stable instances: the array identity is what makes
  // the grid re-project the cells and show the staged value, and the instance identity is what
  // makes the row element and the focused control survive that re-projection.
  //
  // ⚠ THESE CASES ASSERT ON THE LIVE ELEMENT AND ON `document.activeElement`, never on the
  // component's internals. A cache asserted through its own field would pass for a cache that
  // worked and for one whose stability never reached the DOM.
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
      // ⚠ THE OTHER HALF, AND THE ONE A NAIVE FIX BREAKS. Holding a row object stable is only
      // correct if the grid still re-projects its cells; a fix that also returned the same ARRAY
      // would keep focus and show stale values, which is a worse defect than the one it replaced.
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
      // ⚠ IDENTITY IS KEYED BY THE DECLARATION, NOT BY THE POSITION, and a move is what tells the
      // two apart. Keying by position would hand each of the two exchanged rows the OTHER row's
      // instance, so the row element would stay put while its contents swapped — and the reader's
      // focus would follow the POSITION rather than the record they had just moved, which is the
      // opposite of what pressing Move Down means.
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

    it('replaces every row element when the catalogue itself is re-read', () => {
      // ⚠ THE STABILITY IS PER DECLARATION AND NOT PER SCREEN, so a genuinely different catalogue
      // must not be forced into the previous one's elements. A re-read reporting a DIFFERENT set
      // of declarations is a new set of records, and each gets its own row.
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

    it('announces nothing on a successful batch, as the legacy did not either', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      press(APPLY_LABEL);

      settleReplaces();
      // The batch reads the catalogue once when its last row has settled, so that read has to be
      // answered or verification fails on an outstanding request rather than on anything this case
      // is about.
      drainReReads();

      expect(notify).not.toHaveBeenCalled();
    });

    it('keeps a refused row staged while the row that landed drops out', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(1));

      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      // The first write is accepted and the second refused, which is exactly the partial outcome
      // independent writes and no transaction make possible. ⚠ A REFUSAL DOES NOT ABANDON THE ROWS
      // BEHIND IT - here it is the last row, and the companion case below proves the continuation -
      // and the catalogue is still read once afterwards.
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

      // ⚠ THE MIDDLE ROW IS REFUSED AND THE THIRD IS STILL WRITTEN. Abandoning the remainder would
      // strand work the operator asked for; the rows are independent, so each is attempted on its own
      // merits exactly as the server applies them.
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

      // Claimed rather than merely counted, because claiming a request is what takes it out of the
      // backend's open set - which is exactly what makes the assertion below meaningful: anything
      // still open afterwards was issued by the second press.
      const first = pendingWrites('PUT');

      expect(first.length).withContext('one replace in flight').toBe(1);

      // ⚠ THE DEFECT THIS PINS DOWN. The shared saving flag used to fall on the FIRST write to land,
      // so Apply became pressable again while the rest of the batch was still in flight and a second
      // batch could be started on top of the first. The flag is now held for the whole batch, so the
      // command is withheld and pressing it again adds NOTHING to the wire.
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
    it('is closed on arrival and opens in place without navigating', () => {
      arrive();

      expect(text()).not.toContain(CREATE_HEADING);

      press(ADD_LABEL);

      expect(text()).toContain(CREATE_HEADING);
      expect(button(CREATE_SUBMIT_LABEL)).toBeTruthy();
      // No detail route exists, so nothing may be requested by opening the editor.
      httpMock.expectNone(() => true);
    });

    it('opens with the legacy field initialisers, including visible false', () => {
      arrive();
      press(ADD_LABEL);

      expect(control('profile-definition-name').value).toBe('');
      expect(control('profile-definition-data-type').value).toBe('-1');
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
     * ⚠ THE CASE THAT KEEPS THE MEMOISED MESSAGE MAP HONEST.
     *
     * Each field's messages are bound twice — to the shared field's `error` input and to the
     * control's own `aria-invalid` — so they are derived ONCE per change into a map the template
     * reads, rather than recomputed per binding. The risk that trade brings is staleness: a reactive
     * form is not a signal, so without a bridge from the form's own events the map would keep
     * reporting the complaint after the operator had corrected the field. This case corrects one
     * field WITHOUT submitting again and requires the message to go, and requires the pair of
     * bindings to agree at every step — which they can only do if both read the same value.
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

    // ⚠ THE EXPRESSION'S SYNTAX IS THE SERVER'S TO JUDGE, AND THESE FOUR CASES ARE WHY.
    //
    // The stored pattern is compiled and run by .NET — `UserService` builds it with the
    // linear-time engine, falls back to the backtracking one for the constructs that engine
    // refuses, and bounds every evaluation with a short timeout — so the only opinion that
    // decides whether a pattern is usable belongs to the server. A previous revision compiled
    // the operator's text with the browser's `new RegExp` and refused the form when it threw,
    // and the specification asserted that refusal, which locked in the wrong engine.
    //
    // The dialects are not the same language, and these cases pin both directions: a pattern
    // the browser cannot parse must still reach the API, and a pattern the API refuses must be
    // surfaced from the API's own answer rather than pre-empted.

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

      // Typing it must still not throw — an uncaught SyntaxError inside change detection would
      // take the screen down mid-keystroke, which is the one thing the removed validator did
      // guard against. Nothing compiles the text at all now, so there is nothing to throw.
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

      // The sentence and the failure code are the server's own, from
      // `UserService.GetValidationExpression`. The client reproduces neither: it renders what
      // arrived, beneath the control the server named and through the notification service.
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

      // Faithful: the legacy pattern validator ran against the raw posted value too, so a padded name was
      // refused rather than quietly accepted. The trim on submit is what makes the CATEGORY forgiving, and
      // the category carries no pattern.
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

      // The legacy assigned the failed call's return value into its view-state identifier before testing it,
      // so the next save took the update branch with a nonsense key. This asserts the defect is not
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
      expect(text()).toContain(REMOVAL_MESSAGE);
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

    it('reports a declaration in use distinctly from one that is already gone', () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(problem(409), {
        status: 409,
        statusText: 'Conflict',
      });
      fixture.detectChanges();

      expect(notify).toHaveBeenCalledWith(
        'error',
        'That profile property is in use, so it was not deleted. Remove the recorded values first.',
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

      // Awaited, because the move is deferred to the next render on purpose. Every control is disabled while
      // the write is in flight, and the reporting effect is flushed BEFORE the view that clears `disabled`
      // is refreshed, so a focus call made inline would land on a still-disabled button and be ignored in
      // silence. The component schedules the move with `afterNextRender`; settling the fixture is what lets
      // that callback run here. This was measured, not assumed: the inline version left focus on the body.
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
  //
  //  Both belong to `app-data-table`, which is why neither the shared spinner nor the shared empty state is
  //  declared in this screen's own template. Asserted here rather than assumed, because "the table owns it"
  //  is only true while the table is actually given the `loading` input.

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

      // A `PagedResult` body is not merely unnecessary here — it is a CONTRACT VIOLATION. The response
      // decoder demands an array and rejects the object a paged listing would send, so no
      // `items`/`totalCount` unwrapping exists anywhere on this path and none can be added without this case
      // failing.
      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(
        envelope({ items: catalogue(), page: 1, pageSize: 10, totalCount: 3 }),
      );
      fixture.detectChanges();

      expect(query('app-empty-state').length).withContext('nothing was unwrapped').toBe(1);
      expect(text()).not.toContain('Nickname');
      expect(text()).not.toContain('Biography');
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
      // unique when the set is bound. Two columns sharing a key would make the framework reuse one column's
      // DOM for the other with no error raised anywhere, so the observable proof of distinct keys is that
      // twelve headings still yield twelve cells in every row.
      for (const row of query<HTMLTableRowElement>('tbody tr')) {
        expect(row.querySelectorAll('td').length).withContext('cells in a row').toBe(12);
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

  // The four row commands — the most of any grid in this application

  describe('the row commands', () => {
    /**
     * The commands of one row, in document order, as their ACCESSIBLE names.
     *
     * Read from the visually hidden span rather than from the button's whole text, because the button also
     * carries a decorative glyph that is hidden from assistive technology. Reading the whole text would
     * assert what a sighted reader sees and miss what a screen reader is actually told, which is the
     * property this case is about.
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
      // because neither has a neighbour to exchange positions with. Withheld rather than disabled, matching
      // the treatment of the delete command.
      expect(commands.length).withContext('commands across three rows').toBe(10);
      expect(glyphs.length).withContext('one decorative glyph per command').toBe(commands.length);
    });

    it('does not let a command double as selecting its row', () => {
      arrive();

      // This screen binds no `rowSelect`, so the shared table offers no selection affordance on
      // its rows at all — a command therefore cannot double as one. Selection would be observable
      // through `aria-selected`, so its absence is too.
      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(query('tbody tr[aria-selected="true"]').length)
        .withContext('a command must not select the row')
        .toBe(0);

      // And nothing was written: a move is staged locally and applied later.
      httpMock.expectNone(() => true);
    });

    it('offers NO row-selection affordance at all, this screen listening for none', () => {
      // ⚠ THE CORRECTED BEHAVIOUR, AND THIS CASE USED TO ASSERT THE DEFECT. It previously pressed
      // an ordinary cell and required a row to become selected — proving, as it thought, that the
      // command suppression above was real rather than vacuous. But this screen binds no
      // `rowSelect`, so that selection was reported to nobody and existed only as an announced
      // state and a set of affordances with nothing behind them: every one of these rows was a tab
      // stop that led nowhere and carried a pointer cursor promising an action that never happened.
      //
      // The shared table now derives the affordance from whether anything is listening, so the
      // correct assertion is that the whole vocabulary is ABSENT here: no announced selection
      // state, no tab stop, and a press on an ordinary cell changing nothing.
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

      const nameCell = rows.at(0)?.querySelectorAll('td').item(4);

      nameCell?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      fixture.detectChanges();

      expect(query('tbody tr[aria-selected="true"]').length).toBe(0);
      httpMock.expectNone(() => true);
    });

    it('keeps every row command reachable by keyboard, the rows themselves not being stops', () => {
      // Withdrawing the row from the tab order must not withdraw anything INSIDE it: the commands
      // are what this grid exists to offer, and they are native controls, so each is a stop in its
      // own right. Before the correction a reader had to pass through every row to reach them.
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

    it('name each box by its row as well as its column', () => {
      arrive();

      const cell = query<HTMLTableRowElement>('tbody tr')
        .at(0)
        ?.querySelectorAll('td')
        .item(10);

      expect((cell?.querySelector('[data-visually-hidden]')?.textContent ?? '').trim()).toBe(
        'Nickname',
      );
    });

    it('publish the state as a word beside the box, and treat false as data', () => {
      arrive([
        definition({ propertyDefinitionId: 61, propertyName: 'Quiet', required: false, visible: false }),
      ]);

      const cells = query<HTMLTableCellElement>('tbody tr td');

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
    /**
     * Opens the editor on one declaration and submits it, leaving the replace pending.
     */
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

      // Distinct from the conflict on both axes. The component substitutes its own sentence for the server's
      // generic one, because "no longer exists" is actionable where "not found" is not; and the severity is
      // a WARNING, because nothing is broken — someone else got there first. Asserting only the message
      // would let a regression collapse the two into one outcome while still passing.
      expect(notify).toHaveBeenCalledWith('warning', DEFINITION_GONE_MESSAGE, null);
      expect(notify).not.toHaveBeenCalledWith('error', DEFINITION_GONE_MESSAGE, null);
    });
  });

  // Field-level refusals the server reports

  describe('a rejected create', () => {
    /**
     * Submits a valid-looking create and answers it with a per-field refusal.
     */
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

      // MIGRATION: the legacy access-denied screen used `ModuleMessageType.YellowWarning` in BOTH of its
      // branches, never the red error. A refusal to act is the system working as configured, so it is
      // reported at warning severity here too.
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

      // A NATIVE `<dialog>`, so the focus trap and the top layer are the platform's rather than hand-rolled.
      // `alertdialog` rather than `dialog`: this interrupts to ask about a destructive act, which is exactly
      // the distinction the role draws.
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
      //
      // The CONTROLS are enumerated rather than every element carrying the prefix, because the shared field
      // also gives its label an identifier derived from the control's.
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

      // `IsDirty`, `ModuleDefId`, `PortalId`, `PropertyDefinitionId`, `PropertyValue` and `Visibility` all
      // carry `Browsable(False)`, so the legacy editor never painted them. The identifier and the module
      // association travel on the wire; neither is typed by an operator.
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
    /**
     * The wording of one label, without the required marker the shared field appends.
     *
     * Only the label's own TEXT NODES are read. The marker is an element child, so taking the whole
     * subtree's text would assert the shared field's decoration as though it were part of the legacy
     * wording.
     */
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

      // The trailing colon is supplied and then normalised away, which is why this asserts the displayed
      // form rather than the resource form. The screen passes the resource value verbatim —
      // `ProfilePropertyDefinition_PropertyName.Text` really is "Property Name:" — and `app-form-field`
      // strips ONE trailing colon for display, once, for all nine screens that use it. So the wording is the
      // legacy's and the punctuation is the shared field's. Both halves are asserted: the words here, the
      // absent colon in the next case.
      expect(rendered).toEqual(FIELD_LABELS.map((label) => label.replace(/:$/u, '')));
    });

    it('marks the four members the legacy declared Required(True), and the length as well', () => {
      arrive();
      press(ADD_LABEL);

      const marked = query<HTMLLabelElement>('form label.form-field__label')
        .filter((label) => label.querySelector('.form-field__required-text') !== null)
        .map(labelWording);

      // ⚠ DL-11 — FOUR FROM THE LEGACY, NOT TWO. `PropertyName` (L228), `DataType` (L88-L91),
      // `PropertyCategory` (L193) and `ViewOrder` (L300) all carry `Required(True)`; only the first
      // two have a resource sentence, which is why a census of the messages alone finds two and
      // undercounts the rule.
      //
      // ⚠ FIVE MARKERS, NOT FOUR, AND THE FIFTH IS A CORRECTION RATHER THAN AN ADDITION. `Length`
      // carries no legacy `Required` attribute, but the write contract declares it as a NON-NULLABLE
      // integer — as it does the data type and the view order — and a `<input type="number">` writes
      // `null` into its control whenever its box is cleared. The three numeric fields therefore all
      // had the same defect: an emptied box produced a form the screen believed valid, `null` reached
      // a non-nullable server integer, and the server answered a generic 400 that named no field, so
      // the operator saw a refusal with nothing to correct. All three now refuse absence, and a field
      // that refuses to be left empty must SAY so — to a sighted reader through the marker and to
      // assistive technology through `aria-required`, or the rule is a trap.
      //
      // Note also that the view order's marker was previously the only evidence of its rule: the
      // reasoning was that `nonNullable` made presence structural. It does not — that option governs
      // what `reset()` returns a control to — so the field was announced as required and then accepted
      // being left empty. It now carries a real rule, and the marker states something true.
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
      // ⚠ THE DEFECT THIS PINS, END TO END. Clearing any of the three numeric boxes used to produce a
      // form the screen believed valid, whose request carried `null` into a non-nullable server
      // integer. Each is now refused HERE, with a sentence naming the field, and nothing is
      // dispatched — which is what the request-count assertion at the end proves.
      arrive();
      press(ADD_LABEL);
      fillValidForm('Nickname');

      // Each of the three numeric boxes is emptied, which is what a `<input type="number">` writes
      // `null` for. The data type additionally starts at the legacy sentinel, so emptying it proves
      // absence is refused on its own terms rather than by the sentinel rule that already covered it.
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

      // ⚠ ONE SENTENCE PER EMPTY FIELD, NOT TWO. The whole-number rule passes an absent value on
      // purpose, so an empty box reports "required" alone; reporting "must be a whole number"
      // alongside it would bury the actionable half.
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

      // MCC-1. "dislayed" is the resource value's own misspelling. Correcting it would reword the product
      // during a migration whose discipline is behavioural equivalence, so it is reproduced and reported
      // instead. Both double spaces are asserted with it.
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
      // Submit an expression the client itself refuses, so the expression field is reporting a
      // fault while no request leaves — which is what keeps this case about WORDING.
      //
      // The rule used is the LENGTH one, deliberately. An unparseable pattern would no longer
      // be refused here at all: expression syntax is judged by the .NET engine that runs it, so
      // a malformed pattern is now sent and answered by the server, and this case would have
      // been asserting about a screen with a write in flight.
      type('profile-definition-expression', 'a'.repeat(513));
      press(CREATE_SUBMIT_LABEL);

      // The heading survives, and the LABEL is the field's name with its colon — but the heading text is not
      // pressed into service as the failure sentence. It is the single documented exclusion from this
      // folder's validator-message census.
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

  // =============================================================================================
  // THE KEYED STRINGS ARE JUDGED AFTER THEY ARE TIDIED
  // =============================================================================================
  //
  // The two keyed strings used to be trimmed on their way INTO THE REQUEST, which meant the value that
  // was validated and the value that was sent were different strings. `propertyCategory` is where that
  // bites: its rules are `required` and `maxLength` alone, so a whitespace-only entry is a non-empty
  // string that satisfies both — and was then trimmed to the empty string on its way out. The request
  // went to an endpoint whose `NotEmpty` rule treats a whitespace-only string as empty, so the server
  // refused what the screen had just declared valid and the operator was shown a server rejection for a
  // field the form had raised no complaint about.
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
      // ⚠ ONLY THE KEYED STRINGS ARE TIDIED. A default value or a validation expression may
      // legitimately begin or end with a space, so trimming either would silently change stored data
      // for every property an operator merely re-saved.
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
    /**
     * Attempts a create with one candidate name and reports whether the pattern refused it.
     */
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
      // The resource sentence says only "cannot contain spaces", which UNDERSTATES the rule it
      // describes: a slash, a hash and a comma are refused too. The pattern is authoritative for the RULE
      // and the resource value for the MESSAGE, and the divergence is reported rather than resolved by
      // rewording the product.
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
      expect((query<HTMLTableCellElement>('tbody tr td').at(7)?.textContent ?? '').trim()).toBe('0');

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

      const cells = query<HTMLTableCellElement>('tbody tr td');

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
        (row.querySelectorAll('td').item(9)?.textContent ?? '').trim(),
      );

      // `Null.NullString` IS the empty string, so "" means "no rule" and is NOT the same as a rule that
      // refuses everything. Rendering both as blank would erase a real distinction.
      expect(cells.at(0)).toBe('');
      expect(cells.at(1)).toBe('(?!)');
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
  // =============================================================================================
  // THE BATCH, THE BANNER AND WHO OWNS A WRITE REFUSAL
  //
  // Three corrections, and each one produced a screen that reported the wrong thing rather than
  // nothing.
  //
  // WRITE IDENTITY IN A BATCH. Apply dispatches ONE WRITE PER EDITED ROW, in parallel. The store
  // published one write flag and one failure slot, so on every multi-row apply: the flag fell when
  // the first row landed and the screen concluded the whole batch was done; the slot held at most
  // ONE refusal however many rows were refused; and that one refusal was cleared by whichever
  // sibling write dispatched next. An operator applying five rows of which two were refused could
  // legitimately be told nothing at all.
  //
  // BANNER SCOPE. The banner bound the store's failure slot verbatim, and the store is provided at
  // the application root — so it reported failures of operations this screen does not perform and
  // cannot explain.
  //
  // ONE SURFACE PER WRITE REFUSAL. A refusal appeared in the banner AND in a queued notification, so
  // one refusal was reported twice, in two registers.
  // =============================================================================================

  describe('the batch, the banner and who owns a refusal', () => {
    it('reports every refused row, each naming its own property', () => {
      // Two rows edited, both refused. Two messages, and each says which property it is about.
      arrive([
        definition({ propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0 }),
        definition({ propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1 }),
      ]);

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();
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
      // ⚠ THE CASE THE AGGREGATE FLAG COULD NOT EXPRESS. The successful row settles first; under the
      // old mechanism that lowered the flag and the screen treated the batch as finished, so the
      // refusal arriving afterwards had nothing left to attribute itself to.
      arrive([
        definition({ propertyDefinitionId: 11, propertyName: 'Nickname', viewOrder: 0 }),
        definition({ propertyDefinitionId: 12, propertyName: 'Website', viewOrder: 1 }),
      ]);

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();
      press(APPLY_LABEL);

      // The batch is written in the order the declarations were READ, which is the order the store
      // holds them in — so the Nickname row is written first and the Website row second. Accepting
      // the first and refusing the second is what puts the refusal AFTER a sibling row has already
      // settled, which is the ordering the old aggregate flag could not survive.
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

      driveBatch((write) => write.flush(envelope(definition())));

      expect(notify).withContext('a working apply says nothing').not.toHaveBeenCalled();
    });

    it('keeps a batch refusal out of the banner, which is one surface too many', () => {
      // ⚠ ONE REFUSAL, ONE SURFACE. The notification is the one that can name which property was
      // refused; a shared banner cannot, so reporting in both said the same thing twice and the less
      // useful of the two stayed on screen afterwards.
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
      // ⚠ THE PROPERTY THAT MAKES EVERY OUTCOME OBSERVABLE. The store publishes ONE settled result at
      // a time, so two rows answered in the same turn would coalesce and the earlier one would never
      // reach the reporting effect — the identifier would be right and the effect would simply never
      // be handed it. Serialising removes the race rather than racing to observe it. It also stops
      // three simultaneous writes renumbering the same display-position column against each other.
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
      // operator had just discarded and that are no longer anywhere on screen. Both commands are
      // unavailable for as long as a write is outstanding, and the operator never gets a window
      // between rows: the pending count falls in the settling write's teardown and the next row raises
      // it again inside the SAME effect flush, before the view is refreshed. So the batch is
      // uninterruptible from the screen by construction rather than by a guard.
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
      // The store is application-scoped, so its failure slot carries whatever failed most recently
      // anywhere. A credential change refused on a different screen is not something this screen can
      // explain, and it used to appear here as though it were.
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
      // response against its published contract run inside the service's own mapping, DOWNSTREAM of
      // the interceptor's error handling — so a `200` whose body does not match its contract throws a
      // plain error with no document, no status and no support reference. The banner bound only a
      // document, so it rendered nothing: the grid stayed empty, every command that needs a
      // declaration stayed unusable, and no surface said why.
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
