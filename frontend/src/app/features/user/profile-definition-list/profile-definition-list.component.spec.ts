/**
 * Specification for {@link ProfileDefinitionListComponent} — the profile-property catalogue at
 * `/settings/profile-definitions`.
 *
 * ## WHY THIS SCREEN NEEDS ITS OWN SPECIFICATION
 *
 * It is THREE things sharing one surface: an unpaged grid, a batch of staged edits that are only
 * written when the operator says so, and an inline editor that both creates and replaces. The risk
 * lives in the seams between them — an edit staged in the grid must survive until Apply, must be
 * discarded by Refresh, and must not be lost when a sibling write in the same batch is refused.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - Mounted as the standalone unit it is, with the REAL {@link UserStore} pinned to each case's
 *     injector, and every request answered through `HttpTestingController`.
 *   - `NotificationService.notify` is spied and called through. No router is spied: this screen
 *     navigates nowhere at all, which is itself asserted.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ THE READ IS UNPAGED AND TAKES NO PARAMETER. `GET /api/v1/profile-definitions` carries no page
 * coordinate and no tenant argument — the API resolves the tenant from the request — and the body
 * is an envelope around a PLAIN ARRAY. A fixture shaped as a paged listing flushes successfully
 * and unwraps to no rows at all.
 *
 * ⚠ THERE IS NO REORDER ENDPOINT AND NO BULK ENDPOINT. Moving a row is a swap of two positions and
 * therefore TWO replaces; setting a flag across the catalogue is one replace per affected row.
 * Every case that exercises either asserts the request COUNT as well as the bodies.
 *
 * ⚠ A STAGED EDIT IS DERIVED, NOT HELD AS A FLAG. "Unapplied" means "differs from what the server
 * last reported", so a row stops being unapplied the moment the server agrees with it — which is
 * what makes a partially refused batch recoverable by pressing Apply again.
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

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

const DEFINITIONS_URL = '/api/v1/profile-definitions';

function definitionUrl(propertyDefinitionId: number): string {
  return `${DEFINITIONS_URL}/${propertyDefinitionId}`;
}

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
// =====================================================================================================

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

/** `DuplicateName.Text`, double spaces included. A single space here fails the case, correctly. */
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

/**
 * The string `app-data-table` projects into its own `<caption>`.
 *
 * ⚠ NOT the page title, and the difference is deliberate rather than an oversight. The legacy
 * grid had NO caption at all, so the table reached a screen reader unnamed; the page heading is
 * `ControlTitle_manageprofile.Text` ("Manage Profile Properties") and is rendered by
 * `app-page-header`. Naming the table with the page's own heading would have announced the same
 * words twice in the accessibility tree while still not saying what the table contains, so the
 * caption is its own sentence. Both are asserted, each in its own place.
 */
const GRID_CAPTION = 'Profile properties declared for this site';

/**
 * The nine field labels, verbatim from the `ProfilePropertyDefinition_<member>.Text` values, in
 * the `SortOrder` order the form renders them.
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
 * The nine control identifiers in the SAME order, so the rendered order can be asserted against
 * the authoritative `SortOrder` sequence — name 0, data type 1, category 2, length 3, default
 * value 4, expression 5, required 6, visible 7, view order 8.
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
 * ⚠ THE MISSPELLING IS THE PRODUCT'S AND IS PRESERVED (MCC-1). "dislayed" is what the legacy
 * resource value reads; correcting it here would be an unrequested content change in a migration
 * whose whole discipline is behavioural equivalence. Both double spaces are load-bearing too.
 */
const CATEGORY_HELP_WITH_LEGACY_TYPO =
  'Enter the category for this property.  This will allow the related properties to be ' +
  'grouped when dislayed to the user.';

/** The sentence the component publishes for a declaration that has already been removed. */
const DEFINITION_GONE_MESSAGE = 'That profile property no longer exists. The list has been refreshed.';

/** The four members `Browsable(False)` kept out of the legacy property editor. */
const NON_BROWSABLE_LABELS = ['Is Dirty', 'Module Def', 'Portal', 'Visibility'];

describe('ProfileDefinitionListComponent', () => {
  let fixture: ComponentFixture<ProfileDefinitionListComponent>;
  let httpMock: HttpTestingController;
  let notify: jasmine.Spy;

  // ---------------------------------------------------------------------------------------------
  // FIXTURES
  // ---------------------------------------------------------------------------------------------

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

  // ---------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    // Reversing the two leaves the live backend in place and every expectation times out.
    await TestBed.configureTestingModule({
      imports: [ProfileDefinitionListComponent],
      // The store is listed so each case gets its own instance. It is `providedIn: 'root'`, so
      // without this every case would share one catalogue and one failure slot.
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

  /** Mounts the screen and answers its one read with the supplied catalogue. */
  function arrive(rows: readonly ProfilePropertyDefinition[] = catalogue()): void {
    create();
    expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(envelope(rows));
    fixture.detectChanges();
  }

  /**
   * Flushes every catalogue re-read a batch of writes provoked.
   *
   * ⚠ A BATCH PROVOKES ONE RE-READ PER SUCCESSFUL WRITE, AND ALL BUT THE LAST ARE CANCELLED. The
   * store abandons an outstanding read before dispatching another, so with n successful writes
   * there are n reads of which n-1 are cancelled. A cancelled request cannot be flushed, so each
   * is consumed and skipped rather than answered.
   *
   * @param rows The catalogue to answer the surviving read with.
   */
  function settleReReads(rows: readonly ProfilePropertyDefinition[] = catalogue()): void {
    const reads = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
    );

    expect(reads.length).withContext('at least one catalogue re-read').toBeGreaterThan(0);

    for (const read of reads) {
      if (!read.cancelled) {
        read.flush(envelope(rows));
      }
    }

    fixture.detectChanges();
  }

  /** Accepts every pending replace, without inspecting any of them. */
  function settleReplaces(): void {
    for (const write of pendingWrites('PUT')) {
      write.flush(envelope(definition()));
    }

    fixture.detectChanges();
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

  /**
   * A button inside the removal dialog, by its rendered wording.
   *
   * Scoped to the dialog deliberately: the grid also renders one "Delete" command per removable
   * row, so an unscoped search for that wording finds four buttons and cannot say which is the
   * confirmation.
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

  /** The property names in the order the grid renders them. */
  function renderedNames(): readonly string[] {
    return query<HTMLTableRowElement>('tbody tr').map((row) => {
      const cells = Array.from(row.querySelectorAll('td'));

      // Column four is the name; the four commands come first.
      return (cells.at(4)?.textContent ?? '').trim();
    });
  }

  /** The grid's own check box for a column, one per row, in rendered order. */
  function rowCheckboxes(column: 'required' | 'visible'): readonly HTMLInputElement[] {
    const offset = column === 'required' ? 10 : 11;

    return query<HTMLTableRowElement>('tbody tr').flatMap((row) => {
      const cell = Array.from(row.querySelectorAll('td')).at(offset);
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

  // =============================================================================================
  // ARRIVAL
  // =============================================================================================

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

      // DL-1: the count is the legacy's own, proven three ways in the component's own notes.
      expect(query<HTMLTableCellElement>('thead th').length).toBe(12);

      // DL-9: each command column keeps a real accessible name while painting nothing, so the
      // heading text is present in the document but visually hidden.
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

  // =============================================================================================
  // THE DELETE COMMAND'S VISIBILITY — DL-7
  // =============================================================================================

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

  // =============================================================================================
  // REORDERING — DL-2
  // =============================================================================================

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

    it('stages two rows for one move, and applies them as two replaces', () => {
      arrive();

      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      const writes = pendingWrites('PUT');

      expect(writes.length).toBe(2);

      const byUrl = new Map(writes.map((write) => [write.request.url, write.request.body]));

      expect(byUrl.get(definitionUrl(11))).toEqual(
        jasmine.objectContaining({ propertyName: 'Nickname', viewOrder: 1 }),
      );
      expect(byUrl.get(definitionUrl(12))).toEqual(
        jasmine.objectContaining({ propertyName: 'Website', viewOrder: 0 }),
      );

      for (const write of writes) {
        write.flush(envelope(definition()));
      }

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

      const writes = pendingWrites('PUT');
      const first = writes.find((write) => write.request.url === definitionUrl(91));

      expect(first?.request.body).toEqual({
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

      for (const write of writes) {
        write.flush(envelope(definition()));
      }

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

  // =============================================================================================
  // THE STAGED BATCH
  // =============================================================================================

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

      const writes = pendingWrites('PUT');

      expect(writes.length).toBe(1);
      expect(writes.at(0)?.request.url).toBe(definitionUrl(12));
      expect(writes.at(0)?.request.body).toEqual(
        jasmine.objectContaining({ propertyName: 'Website', visible: false }),
      );

      writes.at(0)?.flush(envelope(definition()));
      settleReReads();
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
      settleReReads();

      expect(notify).not.toHaveBeenCalled();
    });

    it('keeps a refused row staged while the row that landed drops out', () => {
      arrive();

      toggle(rowCheckboxes('required').at(0));
      toggle(rowCheckboxes('required').at(1));

      expect(text()).toContain('2 unapplied change(s)');

      press(APPLY_LABEL);

      const writes = pendingWrites('PUT');

      expect(writes.length).toBe(2);

      const landed = writes.find((write) => write.request.url === definitionUrl(11));
      const refused = writes.find((write) => write.request.url === definitionUrl(12));

      // The first write is accepted and the second refused, which is exactly the partial outcome
      // n independent writes and no transaction make possible.
      landed?.flush(envelope(definition({ propertyDefinitionId: 11, required: true })));
      refused?.flush(problem(409), { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

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
  });

  // =============================================================================================
  // THE BULK TOGGLES — DL-6
  // =============================================================================================

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

      const writes = pendingWrites('PUT');

      expect(writes.length).toBe(2);
      expect(writes.map((write) => write.request.url).sort()).toEqual(
        [definitionUrl(122), definitionUrl(123)].sort(),
      );

      for (const write of writes) {
        write.flush(envelope(definition()));
      }

      fixture.detectChanges();
      settleReReads();
    });

    it('are not rendered at all when the catalogue is empty', () => {
      arrive([]);

      expect(bulkToggles().length).toBe(0);
    });
  });

  // =============================================================================================
  // THE INLINE FORM
  // =============================================================================================

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

    it('refuses an expression that cannot be compiled, without throwing', () => {
      arrive();
      press(ADD_LABEL);
      fillValidForm();

      expect(() => type('profile-definition-expression', '([')).not.toThrow();

      press(CREATE_SUBMIT_LABEL);

      expect(text()).toContain('not a valid regular expression');
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

      // Faithful: the legacy pattern validator ran against the raw posted value too, so a padded
      // name was refused rather than quietly accepted. The trim on submit is what makes the
      // CATEGORY forgiving, and the category carries no pattern.
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

  // =============================================================================================
  // THE DUPLICATE-NAME REFUSAL, AND THE DEFECT AROUND IT
  // =============================================================================================

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

      // The legacy assigned the failed call's return value into its view-state identifier before
      // testing it, so the next save took the update branch with a nonsense key. This asserts the
      // defect is not reproduced.
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

  // =============================================================================================
  // THE INLINE EDIT FORM — DL-12
  // =============================================================================================

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
      // ⚠ A disabled control is excluded from the form's value, which would drop two required
      // members from every replace request.
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

  // =============================================================================================
  // REMOVAL — DL-13
  // =============================================================================================

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
      // The confirmation must carry a real accessible name: the legacy overwrote its own
      // "Delete" text with a resource key that does not exist, leaving it empty.
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

    // A confirmed delete destroys the button that opened the dialog, so the dialog's own
    // focus-return has nothing to return to and focus collapses to the document body. That was
    // measured in a browser, and these two cases pin the fix so it cannot silently regress.
    it('moves focus to the primary action once a removal succeeds', async () => {
      confirmRemoval();

      expectRequest('DELETE', definitionUrl(11)).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      settleReReads();

      // ⚠ AWAITED, BECAUSE THE MOVE IS DEFERRED TO THE NEXT RENDER ON PURPOSE. Every control is
      // disabled while the write is in flight, and the reporting effect is flushed BEFORE the
      // view that clears `disabled` is refreshed, so a focus call made inline would land on a
      // still-disabled button and be ignored in silence. The component schedules the move with
      // `afterNextRender`; settling the fixture is what lets that callback run here. This was
      // measured, not assumed: the inline version left focus on the body.
      await fixture.whenStable();

      const anchor: HTMLButtonElement | undefined = buttonsNamed(ADD_LABEL).at(0);

      expect(anchor).toBeDefined();
      // The anchor being enabled is a PRECONDITION of the assertion below, not an incidental
      // detail: a disabled control cannot hold focus.
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

      // Settled the same way the success case is, so the two are compared on equal terms: this
      // asserts that NO deferred move was scheduled, not merely that one has yet to run.
      await fixture.whenStable();

      // The row survives a refusal, so its own command still exists and the anchor must NOT be
      // stolen: moving focus here would displace the operator for no reason.
      expect(document.activeElement).not.toBe(buttonsNamed(ADD_LABEL).at(0) ?? null);
    });
  });

  // =============================================================================================
  // A FAILED READ
  // =============================================================================================

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

  // =============================================================================================
  // THE WAITING AND EMPTY STATES
  //
  // Both belong to `app-data-table`, which is why neither the shared spinner nor the shared empty
  // state is declared in this screen's own template. Asserted here rather than assumed, because
  // "the table owns it" is only true while the table is actually given the `loading` input.
  // =============================================================================================

  describe('the grid before it has rows', () => {
    it('shows the shared spinner while the read is in flight and drops it once answered', () => {
      create();

      expect(query('app-loading-spinner').length).withContext('while in flight').toBe(1);
      // Waiting WINS over empty: there are no rows yet either, and announcing "no records" to a
      // reader before the answer has arrived would state something untrue.
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
      // The table binds no `message`, so the shared default is what a reader sees. Asserted
      // because this screen deliberately does not override it.
      expect(text()).toContain('No records found.');
    });

    it('refuses a paged body outright, because this read is not paged', () => {
      create();

      // ⚠ A `PagedResult` body is not merely unnecessary here — it is a CONTRACT VIOLATION. The
      // response decoder demands an array and rejects the object a paged listing would send, so
      // no `items`/`totalCount` unwrapping exists anywhere on this path and none can be added
      // without this case failing.
      expectRequest('GET', DEFINITIONS_URL, 'the catalogue read').flush(
        envelope({ items: catalogue(), page: 1, pageSize: 10, totalCount: 3 }),
      );
      fixture.detectChanges();

      expect(query('app-empty-state').length).withContext('nothing was unwrapped').toBe(1);
      expect(text()).not.toContain('Nickname');
      expect(text()).not.toContain('Biography');
    });
  });

  // =============================================================================================
  // THE TABLE'S ACCESSIBILITY AND STRUCTURAL CONTRACT
  // =============================================================================================

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

      // The shared table tracks its heading and cell loops by column KEY and validates that the
      // keys are unique when the set is bound. Two columns sharing a key would make the framework
      // reuse one column's DOM for the other with no error raised anywhere, so the observable
      // proof of distinct keys is that twelve headings still yield twelve cells in every row.
      for (const row of query<HTMLTableRowElement>('tbody tr')) {
        expect(row.querySelectorAll('td').length).withContext('cells in a row').toBe(12);
      }

      // And distinct from the LABELS: the eight data headings sit in the last eight positions, in
      // the legacy's own order, behind the four command columns.
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

  // =============================================================================================
  // THE FOUR ROW COMMANDS — the most of any grid in this application
  // =============================================================================================

  describe('the row commands', () => {
    /**
     * The commands of one row, in document order, as their ACCESSIBLE names.
     *
     * Read from the visually hidden span rather than from the button's whole text, because the
     * button also carries a decorative glyph that is hidden from assistive technology. Reading
     * the whole text would assert what a sighted reader sees and miss what a screen reader is
     * actually told, which is the property DL-13 is about.
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

      // Edit, Delete, Move Down, Move Up — down BEFORE up, which reads backwards and is exactly
      // why it is asserted: `COLUMN_MOVE_DOWN = 2` and `COLUMN_MOVE_UP = 3`.
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
        // A real button is keyboard-reachable and announced as operable with no key handling
        // written anywhere. `type="button"` keeps it from submitting the inline form.
        expect(command.tagName).toBe('BUTTON');
        expect(command.getAttribute('type')).toBe('button');

        // DL-13: the name is the FULL WORD, never the legacy's abbreviated "Del", "Dn" or "Up",
        // and never the empty string the legacy delete command actually reached readers with.
        const name = (command.querySelector('[data-visually-hidden]')?.textContent ?? '').trim();

        expect(name.length).withContext('every command carries a name').toBeGreaterThan(0);
        expect(['Del', 'Dn', 'Up']).not.toContain(name);
      }
    });

    it('hides the glyph from assistive technology, so the name is announced once', () => {
      arrive();

      const commands = query<HTMLButtonElement>('tbody tr button');
      const glyphs = query<HTMLElement>('tbody tr button [aria-hidden="true"]');

      // TEN, not twelve, and the arithmetic is the point: three rows would carry twelve commands
      // if every command were offered on every row, but the first row has no Move Up and the last
      // has no Move Down, because neither has a neighbour to exchange positions with. Withheld
      // rather than disabled, matching the treatment of the delete command.
      expect(commands.length).withContext('commands across three rows').toBe(10);
      expect(glyphs.length).withContext('one decorative glyph per command').toBe(commands.length);
    });

    it('does not let a command double as selecting its row', () => {
      arrive();

      // The shared table makes every row activatable and this screen leaves `rowSelect` unbound,
      // so the guarantee that matters is that a command SUPPRESSES the row activation underneath
      // it. Selection is observable through `aria-selected`, so the suppression is too.
      buttonsNamed('Move Down').at(0)?.click();
      fixture.detectChanges();

      expect(query('tbody tr[aria-selected="true"]').length)
        .withContext('a command must not select the row')
        .toBe(0);

      // And nothing was written: a move is staged locally and applied later.
      httpMock.expectNone(() => true);
    });

    it('activates the row itself only when the press did not come from a command', () => {
      arrive();

      // The contrast case, which is what proves the assertion above is a real suppression rather
      // than a table that never selects anything at all.
      const nameCell = query<HTMLTableRowElement>('tbody tr').at(0)?.querySelectorAll('td').item(4);

      nameCell?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      fixture.detectChanges();

      expect(query('tbody tr[aria-selected="true"]').length).toBe(1);
      httpMock.expectNone(() => true);
    });
  });

  // =============================================================================================
  // THE TWO BOOLEAN COLUMNS
  // =============================================================================================

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

      // `Null.NullBoolean` is literally `False`, so `false` is a VALUE: it reads as the negative
      // word and as an unchecked box, never as blank.
      expect((cells.at(10)?.textContent ?? '')).toContain('No');
      expect((cells.at(11)?.textContent ?? '')).toContain('No');
      expect(rowCheckboxes('required').at(0)?.checked).toBeFalse();
      expect(rowCheckboxes('visible').at(0)?.checked).toBeFalse();
    });
  });

  // =============================================================================================
  // A REFUSED REPLACE — a conflict and a disappearance are DIFFERENT OUTCOMES
  // =============================================================================================

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

      // ⚠ DISTINCT FROM THE CONFLICT ON BOTH AXES. The component substitutes its own sentence for
      // the server's generic one, because "no longer exists" is actionable where "not found" is
      // not; and the severity is a WARNING, because nothing is broken — someone else got there
      // first. Asserting only the message would let a regression collapse the two into one
      // outcome while still passing.
      expect(notify).toHaveBeenCalledWith('warning', DEFINITION_GONE_MESSAGE, null);
      expect(notify).not.toHaveBeenCalledWith('error', DEFINITION_GONE_MESSAGE, null);
    });
  });

  // =============================================================================================
  // FIELD-LEVEL REFUSALS THE SERVER REPORTS
  // =============================================================================================

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
          // The per-field dictionary is an index-signature map on the contract, which is why the
          // component reads it with a bracket rather than a property. Property access on it is a
          // compilation error in this workspace by design.
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
      // 28 of the 34 in-scope legacy validator messages begin with a `<br>`, because the legacy
      // rendered them inline after the control. Carried through verbatim it would paint as
      // literal text, since nothing here treats a message as markup.
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

  // =============================================================================================
  // A PERMISSION REFUSAL — a warning, not a fault
  // =============================================================================================

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

      // MIGRATION: the legacy access-denied screen used `ModuleMessageType.YellowWarning` in BOTH
      // of its branches, never the red error. A refusal to act is the system working as
      // configured, so it is reported at warning severity here too.
      const severities = notify.calls.allArgs().map((args) => args.at(0));

      expect(severities).toContain('warning');
      expect(severities).not.toContain('error');
    });
  });

  // =============================================================================================
  // THE REMOVAL DIALOG'S KEYBOARD CONTRACT
  // =============================================================================================

  describe('the removal dialog', () => {
    function openDialog(): void {
      arrive();
      buttonsNamed('Delete').at(0)?.click();
      fixture.detectChanges();
    }

    it('is a modal alert dialog, which the legacy browser confirm never was', () => {
      openDialog();

      // A NATIVE `<dialog>`, so the focus trap and the top layer are the platform's rather than
      // hand-rolled. `alertdialog` rather than `dialog`: this interrupts to ask about a
      // destructive act, which is exactly the distinction the role draws.
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
      // ⚠ ESCAPE IS A DISMISSAL, NOT A CONFIRMATION. A regression that routed it to `confirm`
      // would delete a row on a key press meant to back out.
      httpMock.expectNone(() => true);
    });
  });

  // =============================================================================================
  // THE FORM'S SHAPE — field order, and what is deliberately NOT a field
  // =============================================================================================

  describe('the form layout', () => {
    it('renders the nine fields in the authoritative SortOrder sequence', () => {
      arrive();
      press(ADD_LABEL);

      // MCC-4: the order is the `SortOrder` attributes on the legacy definition class, which is
      // what the excluded property editor reflected over under `SortMode="SortOrderAttribute"`.
      // It is deliberately neither alphabetical nor the order the wire contract lists.
      //
      // The CONTROLS are enumerated rather than every element carrying the prefix, because the
      // shared field also gives its label an identifier derived from the control's.
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

      // `IsDirty`, `ModuleDefId`, `PortalId`, `PropertyDefinitionId`, `PropertyValue` and
      // `Visibility` all carry `Browsable(False)`, so the legacy editor never painted them. The
      // identifier and the module association travel on the wire; neither is typed by an operator.
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

      // `cmdDelete_Click` L294-L310 existed but had NO `Handles` clause and no control to raise
      // it, so the legacy editor could not delete anything. Removal lives on the grid row.
      expect(query('form button').length).withContext('submit and cancel only').toBe(2);
      expect(query<HTMLButtonElement>('form button').map((each) => (each.textContent ?? '').trim()))
        .toEqual([EDIT_SUBMIT_LABEL, CANCEL_LABEL]);
    });
  });

  // =============================================================================================
  // WORDING PARITY — every string is a resource VALUE, never a markup attribute
  // =============================================================================================

  describe('the published wording', () => {
    /**
     * The wording of one label, without the required marker the shared field appends.
     *
     * Only the label's own TEXT NODES are read. The marker is an element child, so taking the
     * whole subtree's text would assert the shared field's decoration as though it were part of
     * the legacy wording.
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

      // ⚠ THE TRAILING COLON IS SUPPLIED AND THEN NORMALISED AWAY, WHICH IS WHY THIS ASSERTS THE
      // DISPLAYED FORM RATHER THAN THE RESOURCE FORM. The screen passes the resource value
      // verbatim — `ProfilePropertyDefinition_PropertyName.Text` really is "Property Name:" — and
      // `app-form-field` strips ONE trailing colon for display, once, for all nine screens that
      // use it. So the wording is the legacy's and the punctuation is the shared field's. Both
      // halves are asserted: the words here, the absent colon in the next case.
      expect(rendered).toEqual(FIELD_LABELS.map((label) => label.replace(/:$/u, '')));
    });

    it('marks exactly the four members the legacy declared Required(True)', () => {
      arrive();
      press(ADD_LABEL);

      const marked = query<HTMLLabelElement>('form label.form-field__label')
        .filter((label) => label.querySelector('.form-field__required-text') !== null)
        .map(labelWording);

      // ⚠ DL-11 — FOUR, NOT TWO. `PropertyName` (L228), `DataType` (L88-L91), `PropertyCategory`
      // (L193) and `ViewOrder` (L300) all carry `Required(True)`; only the first two have a
      // resource sentence, which is why a census of the messages alone finds two and undercounts
      // the rule. The view order's presence is enforced STRUCTURALLY rather than by
      // `Validators.required` — its control is `nonNullable` over a number — so the marker is what
      // makes the fourth rule observable, and it is asserted here rather than inferred.
      expect(marked).toEqual(['Property Name', 'Data Type', 'Property Category', 'View Order']);
      expect(query('form [aria-required="true"]').length).toBe(4);
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

      // ⚠ MCC-1. "dislayed" is the resource value's own misspelling. Correcting it would reword
      // the product during a migration whose discipline is behavioural equivalence, so it is
      // reproduced and reported instead. Both double spaces are asserted with it.
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
      // Submit an expression that cannot compile, so the expression field is reporting a fault.
      type('profile-definition-expression', '[unclosed');
      press(CREATE_SUBMIT_LABEL);

      // The heading survives, and the LABEL is the field's name with its colon — but the
      // heading text is not pressed into service as the failure sentence. It is the single
      // documented exclusion from this folder's validator-message census.
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
        // Consume the write the acceptance produced, so the verification in `afterEach` is
        // asserting about unexpected requests rather than about this one.
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
      // ⚠ DL-10. The resource sentence says only "cannot contain spaces", which UNDERSTATES the
      // rule it describes: a slash, a hash and a comma are refused too. The pattern is
      // authoritative for the RULE and the resource value for the MESSAGE, and the divergence is
      // reported rather than resolved by rewording the product.
      for (const candidate of ['a/b', 'a#b', 'a b', 'a,b', 'a:b', 'a(b']) {
        expect(refusedForPattern(candidate)).withContext(`"${candidate}" is invalid`).toBeTrue();
      }
    });

    it('reports the understated sentence the legacy published, not a reworded one', () => {
      expect(refusedForPattern('First Name')).toBeTrue();
      expect(text()).toContain(NAME_PATTERN_MESSAGE);
    });
  });

  // =============================================================================================
  // ZEROS ARE REAL VALUES
  // =============================================================================================

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

      // Rendered as the digit, never as blank: `Null.NullInteger` is -1, so 0 is not the sentinel
      // and a truthiness test on it would have painted an empty cell.
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

      // ⚠ Angular's emptiness test is `value == null || ((string | array) && length === 0)`, so a
      // numeric 0 PASSES. This case exists to lock out a hand-rolled truthiness check, which
      // would refuse the first row's legitimate position.
      expect(text()).not.toContain('The View Order is required');

      expectRequest('POST', DEFINITIONS_URL, 'the create').flush(
        envelope(definition({ propertyDefinitionId: 95 })),
        { status: 201, statusText: 'Created' },
      );
      settleReReads();
    });

    it('carries a portal identifier of zero as a real tenant, never as unset', () => {
      // `Portals.PortalID` is `IDENTITY(-1,1)`, so the FIRST real portal is 0 and -1 is
      // simultaneously a real row and the `NullInteger` sentinel. Both must survive as data.
      arrive([
        definition({ propertyDefinitionId: 161, propertyName: 'Zeroth', portalId: 0 }),
        definition({ propertyDefinitionId: 162, propertyName: 'Hosted', portalId: -1, viewOrder: 1 }),
      ]);

      expect(renderedNames()).toEqual(['Zeroth', 'Hosted']);

      // ⚠ REPORTED GAP, ASSERTED AS THE ABSENCE IT IS: the tenant is NOT a query parameter on
      // this read. The API resolves it from the request, so there is no `portalId` argument to
      // carry — and a regression that started sending one would be silently scoping the read.
      const reread = httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === DEFINITIONS_URL,
      );

      expect(reread.length).toBe(0);
    });
  });

  // =============================================================================================
  // TENANT-AUTHORED TEXT IS TEXT, NEVER MARKUP
  // =============================================================================================

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

      // The angle brackets survive as CHARACTERS, which is only possible if the value was
      // interpolated. Nothing on this screen binds `innerHTML` or reaches for a sanitiser.
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

      // ⚠ `Null.NullString` IS the empty string, so "" means "no rule" and is NOT the same as a
      // rule that refuses everything. Rendering both as blank would erase a real distinction.
      expect(cells.at(0)).toBe('');
      expect(cells.at(1)).toBe('(?!)');
    });

    it('renders a help string containing markup as plain text', () => {
      arrive();
      press(ADD_LABEL);

      // This folder's `Introduction_Add.Help` provably carries an escaped `<b>Note:</b>`. Whatever
      // a hint holds, it is projected as text.
      expect(query('form b').length).toBe(0);
      expect(query('form strong').length).toBe(0);
    });
  });

  // =============================================================================================
  // THE LIVE REGIONS
  // =============================================================================================

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
});
