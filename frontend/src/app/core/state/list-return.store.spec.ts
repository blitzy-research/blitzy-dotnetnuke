import { TestBed } from '@angular/core/testing';

import { ListReturnStore } from './list-return.store';

/**
 * WHAT THIS SPECIFICATION PROVES
 *
 * That a listing's place survives the trip to a form and back, and that one listing can never be returned to
 * another listing's place.
 *
 * The fault being guarded against is a lost place: every form returned with a bare navigation carrying no
 * query parameters, so a save from the fourth page of a filtered listing landed the operator on page one of
 * an unfiltered one, with the row they had just written nowhere on screen.
 */
describe('ListReturnStore', () => {
  const PORTALS = '/portals';
  const USERS = '/users';

  let store: ListReturnStore;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    store = TestBed.inject(ListReturnStore);
  });

  describe('remembering a place', () => {
    it('returns an empty bag for a listing that has not been visited', () => {
      // An empty bag rather than a null, because it is directly usable as `queryParams` and produces the
      // listing's default state - which is exactly where an operator who opened a form directly should land.
      expect(store.coordinateFor(PORTALS)).toEqual({});
    });

    it('hands back what a listing recorded', () => {
      store.remember(PORTALS, { currentpage: '4', filter: 'Q', sortby: 'portalName', sortdir: 'desc' });

      expect(store.coordinateFor(PORTALS)).toEqual({
        currentpage: '4',
        filter: 'Q',
        sortby: 'portalName',
        sortdir: 'desc',
      });
    });

    it('replaces a place rather than merging into it', () => {
      store.remember(PORTALS, { currentpage: '4', filter: 'Q' });
      store.remember(PORTALS, { currentpage: '2' });

      // ⚠ MERGING WOULD RESURRECT AN ABANDONED FILTER. An operator who clears a filter and pages to two must
      // not be returned to page two OF THE OLD FILTER, so the later record wins outright.
      expect(store.coordinateFor(PORTALS)).toEqual({ currentpage: '2' });
    });

    it('treats an empty bag as a meaningful record, not as a no-op', () => {
      store.remember(PORTALS, { currentpage: '4', filter: 'Q' });
      store.remember(PORTALS, {});

      // An empty bag says the listing is at its unfiltered first page. Ignoring it would leave the previous
      // coordinate standing and return the operator to a filter they had just cleared.
      expect(store.coordinateFor(PORTALS)).toEqual({});
    });
  });

  describe('keeping listings apart', () => {
    it('does not let one listing read another listing\u2019s place', () => {
      // The vocabularies genuinely differ - the account listing carries a `searchby` the portal listing has
      // never heard of - so a shared coordinate would put an unrecognised parameter in the address.
      store.remember(PORTALS, { currentpage: '4', filter: 'Q' });
      store.remember(USERS, { searchby: 'all', currentpage: '2' });

      expect(store.coordinateFor(PORTALS)).toEqual({ currentpage: '4', filter: 'Q' });
      expect(store.coordinateFor(USERS)).toEqual({ searchby: 'all', currentpage: '2' });
    });
  });

  describe('isolation from the caller', () => {
    it('copies what it is given, so a later change to the caller\u2019s object cannot rewrite it', () => {
      // Callers hand over the router's own live parameter object. Holding that reference would let a
      // subsequent navigation silently change what this store reports.
      const live: Record<string, string> = { currentpage: '4' };

      store.remember(PORTALS, live);
      live['currentpage'] = '9';
      live['filter'] = 'Z';

      expect(store.coordinateFor(PORTALS)).toEqual({ currentpage: '4' });
    });

    it('does not expose the stored object for mutation', () => {
      store.remember(PORTALS, { currentpage: '4' });

      const handedOut = store.coordinateFor(PORTALS) as Record<string, string>;
      handedOut['currentpage'] = '9';

      expect(store.coordinateFor(PORTALS)).toEqual({ currentpage: '4' });
    });
  });

  describe('forgetting', () => {
    it('forgets one listing without disturbing the others', () => {
      store.remember(PORTALS, { currentpage: '4' });
      store.remember(USERS, { searchby: 'all' });

      store.forget(PORTALS);

      expect(store.coordinateFor(PORTALS)).toEqual({});
      expect(store.coordinateFor(USERS)).toEqual({ searchby: 'all' });
    });

    it('forgets every listing when asked for no particular one', () => {
      // The concrete need is a sign-out: the next operator must not inherit the previous one's filter.
      store.remember(PORTALS, { currentpage: '4' });
      store.remember(USERS, { searchby: 'all' });

      store.forget();

      expect(store.coordinateFor(PORTALS)).toEqual({});
      expect(store.coordinateFor(USERS)).toEqual({});
    });

    it('forgetting a listing that was never recorded is a no-op rather than an error', () => {
      expect(() => store.forget('/never-visited')).not.toThrow();
      expect(store.coordinateFor('/never-visited')).toEqual({});
    });
  });

  describe('lifetime', () => {
    it('is a single instance for the whole application', () => {
      // Keyed by route in ONE store, so the listing that records a place and the form that reads it are
      // guaranteed to be talking about the same map.
      expect(TestBed.inject(ListReturnStore)).toBe(store);
    });
  });
});
