import { Injectable, signal } from '@angular/core';
import type { Params } from '@angular/router';

/**
 * WHAT THIS SERVICE IS
 *
 * Remembers where a listing was when the operator left it for a form, so returning from that form lands on
 * the same page, filter and ordering rather than on the first page of an unfiltered list.
 *
 * ⚠ THE FAULT THIS CLOSES IS A LOST PLACE, AND IT IS WORSE THE MORE DATA THERE IS. Every form on this
 * console returned with a bare `router.navigate([LIST_ROUTE])`, carrying no query parameters at all - so an
 * operator who filtered the accounts to `M`, paged to the fourth page, edited one record and saved it was
 * returned to page one of the unfiltered list. The row they had just written was not on screen, and neither
 * was the place they had been working. On the largest reachable dataset that is thirteen pages of scrolling
 * to get back to where a single save threw them out of.
 *
 * WHY IN MEMORY RATHER THAN IN THE ADDRESS
 *
 * The alternative is to carry the listing's parameters through the form's own URL with
 * `queryParamsHandling: 'preserve'`, which survives a reload. That is the right choice in an application
 * where a reload preserves the session - and it is the wrong one here, because this application holds its
 * access token in memory ONLY. A reload signs the operator out and returns them to the sign-in screen, so
 * there is no reachable state in which a remembered listing coordinate would have outlived the session that
 * produced it. Keeping it in memory therefore loses nothing, and it keeps five listing parameters out of
 * every form's address, where they would mean nothing and would be copied into shared links.
 *
 * WHY KEYED BY ROUTE
 *
 * One store serves every listing. The key is the listing's own route, so the portal, user, role and module
 * listings cannot read one another's coordinates - which matters because they do not share a parameter
 * vocabulary: the account listing carries a `searchby` the portal listing has never heard of.
 *
 * DELIBERATELY ABSENT
 *
 * No expiry, no size bound and no persistence. The map holds at most one small object per listing route,
 * every value is a plain query-parameter bag the router already produced, and the whole thing dies with the
 * tab. A bound would add a failure mode - a silently forgotten coordinate - to guard against a few dozen
 * bytes.
 */
@Injectable({ providedIn: 'root' })
export class ListReturnStore {
  /**
   * The remembered coordinate per listing route.
   *
   * A signal rather than a plain map so a consumer may react to it, and replaced wholesale on every write
   * rather than mutated, so a reader holding a previous value never observes it change underneath.
   */
  private readonly coordinates = signal<Readonly<Record<string, Params>>>({});

  /**
   * Records where a listing currently stands.
   *
   * Called by the listing itself, which is the only place that knows its own parameter vocabulary. Passing
   * an empty bag is meaningful and is stored as such: it says the listing was at its unfiltered first page,
   * which must overwrite an earlier coordinate rather than leave it standing.
   *
   * @param route The listing's route, used as the key.
   * @param params The query parameters describing where it stands.
   */
  remember(route: string, params: Params): void {
    // Copied rather than referenced. The caller frequently hands over the router's own live parameter
    // object, and storing that reference would let a later navigation change what this store reports.
    this.coordinates.update((held) => ({ ...held, [route]: { ...params } }));
  }

  /**
   * The coordinate to return a listing to.
   *
   * @param route The listing's route.
   * @returns The remembered parameters, or an empty bag when that listing has not been visited. An empty bag
   *   is the correct answer rather than a null: it produces a navigation to the listing's default state,
   *   which is exactly where an operator who arrived at a form directly should land.
   */
  coordinateFor(route: string): Params {
    const held: Params | undefined = this.coordinates()[route];

    // ⚠ COPIED ON THE WAY OUT AS WELL AS ON THE WAY IN, and a specification exists for this because the
    // first implementation returned the stored object itself. Every caller hands the result straight to
    // `Router.navigate` as its `queryParams`, and the router is entitled to read that object later or to
    // annotate it - so handing out the live reference let a consumer rewrite what this store reports for the
    // next form that asks.
    return held === undefined ? {} : { ...held };
  }

  /**
   * Forgets a listing's coordinate.
   *
   * Provided for the case where a listing's state can no longer be meaningful - most concretely, a sign-out,
   * after which the next operator must not inherit the previous one's filter.
   *
   * @param route The listing's route, or omitted to forget every listing.
   */
  forget(route?: string): void {
    if (route === undefined) {
      this.coordinates.set({});

      return;
    }

    this.coordinates.update((held) => {
      const remaining = { ...held };
      delete remaining[route];

      return remaining;
    });
  }
}
