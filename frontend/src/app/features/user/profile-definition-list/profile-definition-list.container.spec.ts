import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { API_ENDPOINTS } from '../../../core/config/api-endpoints';
import { PROFILE_VISIBILITY } from '../../../core/models/profile.model';
import type {
  CreateProfilePropertyDefinitionRequest,
  ProfilePropertyDefinition,
  UpdateProfilePropertyDefinitionRequest,
} from '../../../core/models/profile.model';
import { ProfileDefinitionListComponent } from './profile-definition-list.component';
import { ProfileDefinitionListContainerComponent } from './profile-definition-list.container';

/**
 * Specification for the routed owner of the profile-property catalogue.
 *
 * The cases are chosen around what breaks if the container is absent or wrong, because that is
 * precisely the defect it was written to fix: a presentational screen mounted straight onto a
 * route renders permanently empty and every control on it is a no-op that LOOKS like a working
 * control. So the first thing asserted is that the catalogue is fetched at all, and the rest is
 * that each of the five intents reaches the wire.
 *
 * Two behaviours are worth more than the others and are asserted in detail:
 *
 *   - A REORDER IS TWO WRITES WITH SWAPPED POSITIONS. It is the one intent the store refuses to
 *     own, on the stated grounds that only the feature knows which row is the neighbour. If the
 *     container gets the arithmetic wrong the row appears to move and the stored order does not
 *     change, or worse, a row the operator did not touch moves.
 *   - A REPLACE CARRIES EVERY MEMBER. The endpoint replaces rather than patches, so a member
 *     omitted from a reorder or a bulk flag is a member CLEARED in the database. The assertions
 *     therefore check the whole request body, not just the member being changed.
 */
describe('ProfileDefinitionListContainerComponent', () => {
  let fixture: ComponentFixture<ProfileDefinitionListContainerComponent>;
  let httpMock: HttpTestingController;

  const COLLECTION_URL = API_ENDPOINTS.profileDefinitions.forCurrentPortal.collection();

  /**
   * Builds one stored declaration.
   *
   * The defaults are the shape a freshly seeded tenant produces: no module association, no
   * validation pattern, a zero length meaning "no maximum", and a visibility of "all users".
   */
  function definitionWith(
    overrides: Partial<ProfilePropertyDefinition> & { readonly propertyDefinitionId: number },
  ): ProfilePropertyDefinition {
    return {
      portalId: 0,
      moduleDefId: null,
      dataType: 349,
      defaultValue: null,
      propertyCategory: 'Name',
      propertyName: `Property${overrides.propertyDefinitionId}`,
      length: 0,
      required: false,
      validationExpression: null,
      viewOrder: 0,
      visible: true,
      visibility: PROFILE_VISIBILITY.allUsers,
      ...overrides,
    };
  }

  /**
   * Answers every catalogue refresh a write left outstanding.
   *
   * ⚠ CANCELLED REFRESHES ARE SKIPPED, and the skip is a real behaviour rather than a testing
   * convenience: the store holds ONE handle for the catalogue read and unsubscribes it before
   * starting another, so when two writes complete in the same turn the first refresh is cancelled
   * by the second. Flushing a cancelled request throws, and asserting a fixed count of refreshes
   * would encode the store's internal cancellation as a contract.
   */
  function settleRefreshes(): void {
    httpMock
      .match({ method: 'GET', url: COLLECTION_URL })
      .filter((refresh) => refresh.cancelled === false)
      .forEach((refresh) => {
        refresh.flush({ data: [] });
      });
  }

  /** Answers the catalogue read the container issues on entry. */
  function settleInitialLoad(definitions: readonly ProfilePropertyDefinition[]): void {
    httpMock.expectOne({ method: 'GET', url: COLLECTION_URL }).flush({ data: definitions });
    fixture.detectChanges();
  }

  /** The child the container drives, resolved from the rendered component tree. */
  function child(): ProfileDefinitionListComponent {
    return fixture.debugElement.children[1].componentInstance as ProfileDefinitionListComponent;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ProfileDefinitionListContainerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProfileDefinitionListContainerComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches the catalogue on entry', () => {
    // The defect this container exists to fix, asserted first: without it nothing is ever read.
    settleInitialLoad([definitionWith({ propertyDefinitionId: 1 })]);

    expect(child().definitions.length).toBe(1);
  });

  it('binds the catalogue to the presentational screen in the order the server reported it', () => {
    settleInitialLoad([
      definitionWith({ propertyDefinitionId: 9, propertyName: 'Last', viewOrder: 1 }),
      definitionWith({ propertyDefinitionId: 4, propertyName: 'First', viewOrder: 0 }),
    ]);

    // Unsorted, deliberately: the server owns the order and re-sorting here would make the
    // screen disagree with the store the moment two operators reorder at once.
    expect(child().definitions.map((held) => held.propertyDefinitionId)).toEqual([9, 4]);
  });

  it('reports a failed read through the shared banner', () => {
    httpMock
      .expectOne({ method: 'GET', url: COLLECTION_URL })
      .flush(
        { type: 'about:blank', title: 'Server error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('app-error-banner');

    expect(banner?.textContent).toContain('Server error');
  });

  it('creates a declaration from the form intent', () => {
    settleInitialLoad([]);

    const submission: CreateProfilePropertyDefinitionRequest = {
      propertyName: 'Nickname',
      propertyCategory: 'Name',
      dataType: 349,
      defaultValue: null,
      length: 0,
      required: false,
      validationExpression: null,
      viewOrder: 0,
      visible: true,
      moduleDefId: null,
    };

    child().create.emit(submission);

    const written = httpMock.expectOne({ method: 'POST', url: COLLECTION_URL });

    expect(written.request.body).toEqual(submission);
    written.flush({ data: definitionWith({ propertyDefinitionId: 3, propertyName: 'Nickname' }) });

    // Every write refreshes the catalogue, so the list on screen is the server's and not a
    // prediction assembled here.
    httpMock.expectOne({ method: 'GET', url: COLLECTION_URL }).flush({ data: [] });
  });

  it('replaces a declaration with exactly the submission it was given', () => {
    settleInitialLoad([definitionWith({ propertyDefinitionId: 5 })]);

    const submission: UpdateProfilePropertyDefinitionRequest = {
      propertyName: 'Renamed',
      propertyCategory: 'Contact',
      dataType: 349,
      defaultValue: 'seed',
      length: 40,
      required: true,
      validationExpression: null,
      viewOrder: 2,
      visible: false,
    };

    child().update.emit({ propertyDefinitionId: 5, submission });

    const written = httpMock.expectOne({
      method: 'PUT',
      url: API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(5),
    });

    // Passed on untouched: the container adds nothing to a submission the form already assembled.
    expect(written.request.body).toEqual(submission);
    written.flush({ data: definitionWith({ propertyDefinitionId: 5 }) });
    httpMock.expectOne({ method: 'GET', url: COLLECTION_URL }).flush({ data: [] });
  });

  it('removes a declaration', () => {
    const target = definitionWith({ propertyDefinitionId: 8 });
    settleInitialLoad([target]);

    child().remove.emit(target);

    httpMock
      .expectOne({ method: 'DELETE', url: API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(8) })
      .flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne({ method: 'GET', url: COLLECTION_URL }).flush({ data: [] });
  });

  it('moves a declaration by swapping the two positions, writing both rows in full', () => {
    settleInitialLoad([
      definitionWith({ propertyDefinitionId: 1, propertyName: 'First', viewOrder: 0 }),
      definitionWith({ propertyDefinitionId: 2, propertyName: 'Second', viewOrder: 5 }),
    ]);

    child().reorder.emit({ propertyDefinitionId: 2, direction: 'up' });

    const moved = httpMock.expectOne({
      method: 'PUT',
      url: API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(2),
    });
    const neighbour = httpMock.expectOne({
      method: 'PUT',
      url: API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(1),
    });

    // The positions are exchanged, not decremented: the neighbour's own position is what the
    // moved row takes, which is what the legacy screen did.
    expect((moved.request.body as UpdateProfilePropertyDefinitionRequest).viewOrder).toBe(0);
    expect((neighbour.request.body as UpdateProfilePropertyDefinitionRequest).viewOrder).toBe(5);

    // And every other member survives the move. The endpoint replaces rather than patches, so an
    // omitted member is a cleared column.
    expect((moved.request.body as UpdateProfilePropertyDefinitionRequest).propertyName).toBe(
      'Second',
    );
    expect((neighbour.request.body as UpdateProfilePropertyDefinitionRequest).propertyName).toBe(
      'First',
    );

    moved.flush({ data: definitionWith({ propertyDefinitionId: 2 }) });
    neighbour.flush({ data: definitionWith({ propertyDefinitionId: 1 }) });
    settleRefreshes();
  });

  it('discards a move that has no neighbour to exchange positions with', () => {
    settleInitialLoad([
      definitionWith({ propertyDefinitionId: 1, viewOrder: 0 }),
      definitionWith({ propertyDefinitionId: 2, viewOrder: 1 }),
    ]);

    child().reorder.emit({ propertyDefinitionId: 1, direction: 'up' });
    child().reorder.emit({ propertyDefinitionId: 2, direction: 'down' });

    // Clamping either one would move a row the operator did not name, so neither is written.
    // `afterEach` verifies no request escaped.
    expect(httpMock.match({ method: 'PUT' }).length).toBe(0);
  });

  it('discards a move naming a declaration that is no longer listed', () => {
    settleInitialLoad([definitionWith({ propertyDefinitionId: 1 })]);

    child().reorder.emit({ propertyDefinitionId: 404, direction: 'down' });

    expect(httpMock.match({ method: 'PUT' }).length).toBe(0);
  });

  it('sets a flag only on the declarations that do not already hold the value', () => {
    settleInitialLoad([
      definitionWith({ propertyDefinitionId: 1, required: false }),
      definitionWith({ propertyDefinitionId: 2, required: true }),
      definitionWith({ propertyDefinitionId: 3, required: false }),
    ]);

    child().bulkFlag.emit({ flag: 'required', value: true });

    const written = httpMock.match({ method: 'PUT' });

    // Two of the three, not three: rewriting a row to the value it already holds would move its
    // modification stamp and spend a request to change nothing.
    expect(written.length).toBe(2);
    expect(written.map((request) => request.request.url)).toEqual([
      API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(1),
      API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(3),
    ]);
    expect((written[0].request.body as UpdateProfilePropertyDefinitionRequest).required).toBe(true);

    // The flag being set is the ONLY member that changes.
    expect((written[0].request.body as UpdateProfilePropertyDefinitionRequest).visible).toBe(true);

    written.forEach((request) => {
      request.flush({ data: definitionWith({ propertyDefinitionId: 1 }) });
    });
    settleRefreshes();
  });

  it('issues nothing when a bulk flag is already set everywhere', () => {
    settleInitialLoad([
      definitionWith({ propertyDefinitionId: 1, visible: true }),
      definitionWith({ propertyDefinitionId: 2, visible: true }),
    ]);

    child().bulkFlag.emit({ flag: 'visible', value: true });

    // The correct outcome for an operator who activates the control twice.
    expect(httpMock.match({ method: 'PUT' }).length).toBe(0);
  });

  it('clears the other flag correctly, which proves the flag is selected and not assumed', () => {
    settleInitialLoad([definitionWith({ propertyDefinitionId: 7, visible: true, required: true })]);

    child().bulkFlag.emit({ flag: 'visible', value: false });

    const written = httpMock.expectOne({
      method: 'PUT',
      url: API_ENDPOINTS.profileDefinitions.forCurrentPortal.byId(7),
    });
    const body = written.request.body as UpdateProfilePropertyDefinitionRequest;

    expect(body.visible).toBe(false);
    expect(body.required).toBe(true);

    written.flush({ data: definitionWith({ propertyDefinitionId: 7 }) });
    httpMock.expectOne({ method: 'GET', url: COLLECTION_URL }).flush({ data: [] });
  });
});
