/**
 * Specification for the shared page-choice vocabulary.
 *
 * ⚠ WHY THIS FILE EXISTS AT ALL. These rules were previously private to the portal settings screen and
 * exercised only through it. A second screen — the membership settings redirect destinations — now reads
 * them, so the rules are a contract between two screens rather than one screen's internal detail, and a
 * change to them can break a screen whose own specification does not mention pages.
 */

import {
  CURRENT_PAGE_LABEL,
  DELETED_PAGE_SUFFIX,
  NO_PAGE_SELECTED,
  buildPageChoices,
  buildPageOptions,
  indentFor,
} from './page-options.util';

import type { TabListItem } from '../models/tab.model';

/**
 * One page of a portal, with only the members a picker reads carrying meaning.
 *
 * @param overrides The members this row differs from the default in.
 * @returns A page listing row.
 */
function pageRow(overrides: Partial<TabListItem> = {}): TabListItem {
  return {
    tabId: 1,
    tabName: 'Page',
    title: null,
    tabOrder: 1,
    parentId: null,
    level: 0,
    tabPath: null,
    isVisible: true,
    disableLink: false,
    isDeleted: false,
    hasChildren: false,
    isSecure: false,
    url: null,
    iconFile: null,
    ...overrides,
  };
}

describe('indentFor', () => {
  it('draws nothing at the root, and one step per level below it', () => {
    expect(indentFor(0)).toBe('');
    expect(indentFor(1)).toBe('...');
    expect(indentFor(3)).toBe('.........');
  });

  it('draws nothing for a level that cannot be one', () => {
    // A negative or non-finite level is corrupt input rather than a deep page, and an indent computed from
    // it would be either meaningless or enormous.
    expect(indentFor(-1)).toBe('');
    expect(indentFor(Number.NaN)).toBe('');
    expect(indentFor(Number.POSITIVE_INFINITY)).toBe('');
  });

  it('is bounded, so a corrupt level cannot make one option megabytes wide', () => {
    expect(indentFor(1_000_000).length).toBe(127 * 3);
  });
});

describe('buildPageChoices', () => {
  it('offers the pages in the order received, with depth shown', () => {
    const choices = buildPageChoices(
      [
        pageRow({ tabId: 10, tabName: 'Home' }),
        pageRow({ tabId: 11, tabName: 'About', parentId: 10, level: 1 }),
        pageRow({ tabId: 12, tabName: 'Contact' }),
      ],
      null,
      [],
    );

    // ORDER IS PRESERVED RATHER THAN SORTED: the listing already arrives in hierarchy order, and re-sorting
    // here would put each indent out of step with its parent.
    expect(choices.map((choice) => choice.label)).toEqual(['Home', '...About', 'Contact']);
    expect(choices.map((choice) => choice.value)).toEqual([10, 11, 12]);
  });

  it('carries no "nothing chosen" entry, because absence is spelled differently per screen', () => {
    const choices = buildPageChoices([pageRow({ tabId: 10 })], null, []);

    expect(choices).toHaveSize(1);
    expect(choices.map((choice) => choice.value)).not.toContain(NO_PAGE_SELECTED);
  });

  it('offers a hidden page, and refuses a recycled one, a link, and administration', () => {
    const choices = buildPageChoices(
      [
        pageRow({ tabId: 10, tabName: 'Hidden', isVisible: false }),
        pageRow({ tabId: 11, tabName: 'Recycled', isDeleted: true }),
        pageRow({ tabId: 12, tabName: 'Link', url: 'https://example.test' }),
        pageRow({ tabId: 13, tabName: 'Admin' }),
        pageRow({ tabId: 14, tabName: 'Under admin', parentId: 13 }),
        pageRow({ tabId: 15, tabName: 'Ordinary' }),
      ],
      13,
      [],
    );

    // The legacy selector's own arguments: hidden pages ARE offered, recycled ones are not, only pages of
    // the Normal type are - which is exactly a page whose URL is empty - and administration is excluded.
    expect(choices.map((choice) => choice.label)).toEqual(['Hidden', 'Ordinary']);
  });

  it('offers a page with a whitespace-only URL, which is not a link', () => {
    const choices = buildPageChoices([pageRow({ tabId: 10, tabName: 'Blank', url: '   ' })], null, []);

    expect(choices.map((choice) => choice.label)).toEqual(['Blank']);
  });

  it('keeps a stored choice the rules would otherwise hide', () => {
    const choices = buildPageChoices(
      [
        pageRow({ tabId: 10, tabName: 'Recycled', isDeleted: true }),
        pageRow({ tabId: 11, tabName: 'Ordinary' }),
      ],
      null,
      [10],
    );

    // ⚠ THE CONSEQUENCE OF NOT DOING THIS IS SILENT DATA LOSS. A selector that dropped the page it currently
    // holds would show something the operator never chose, and saving anything else on the screen would then
    // store that substitute over their setting.
    expect(choices.map((choice) => choice.label)).toEqual(['Recycled', 'Ordinary']);
  });

  it('names a retained reference the listing does not carry at all by its identifier', () => {
    const choices = buildPageChoices([pageRow({ tabId: 11, tabName: 'Ordinary' })], null, [42]);

    // Nothing is known about page 42 beyond its number - it may have been deleted outright, or the listing
    // may have failed - so the number is what is shown, rather than the reference being dropped.
    expect(choices.map((choice) => choice.label)).toEqual(['Ordinary', '42']);
    expect(choices.map((choice) => choice.value)).toEqual([11, 42]);
  });

  it('offers a duplicated identifier once, so a native select cannot be ambiguous', () => {
    const choices = buildPageChoices(
      [pageRow({ tabId: 10, tabName: 'First' }), pageRow({ tabId: 10, tabName: 'Second' })],
      null,
      [],
    );

    expect(choices).toHaveSize(1);
    expect(choices[0]?.label).toBe('First');
  });

  it('never offers the legacy sentinel as a page, even when the listing carries it', () => {
    const choices = buildPageChoices(
      [pageRow({ tabId: NO_PAGE_SELECTED, tabName: 'Impossible' }), pageRow({ tabId: 11, tabName: 'Real' })],
      null,
      [NO_PAGE_SELECTED],
    );

    // The sentinel means "nothing chosen" to every caller, so a row carrying it as a real page identifier
    // must not become a choosable option that would then be indistinguishable from absence.
    expect(choices.map((choice) => choice.value)).toEqual([11]);
  });

  it('offers nothing at all for an empty listing with nothing retained', () => {
    expect(buildPageChoices([], null, [])).toEqual([]);
  });
});

/**
 * Pins the page-picker builder every "Move To Page" control shares.
 *
 * ⚠ TWO REPORTED DEFECTS LIVE HERE. The module creation form bound the raw page list into its `@for`, so it
 * offered soft-deleted pages as placement targets - a module placed on one is orphaned invisibly, because the
 * recycle bin does not list modules and the page is not navigable. And neither module screen used the
 * disambiguating title the page contract carries, so two pages named `Home` rendered as two identical
 * options with nothing to choose between them. The legacy pickers had neither problem: `GetPortalTabs` was
 * called with `blnDeleted:=False`, and `Globals.vb:L830` filtered on `(objTab.IsDeleted = False Or blnDeleted
 * = True)`.
 */
describe('buildPageOptions', () => {
  /**
   * Builds one page row.
   *
   * @param overrides The members this row differs in.
   * @returns The row.
   */
  function page(overrides: Partial<TabListItem>): TabListItem {
    return {
      tabId: 1,
      tabName: 'Home',
      title: null,
      tabOrder: 1,
      parentId: null,
      level: 0,
      tabPath: null,
      isVisible: true,
      disableLink: false,
      isDeleted: false,
      hasChildren: false,
      isSecure: false,
      url: null,
      iconFile: null,
      ...overrides,
    };
  }

  it('excludes a soft-deleted page, because placing a module on one orphans it invisibly', () => {
    const options = buildPageOptions(
      [
        page({ tabId: 1, tabName: 'Live Page' }),
        page({ tabId: 2, tabName: 'Deleted Page', isDeleted: true }),
      ],
      undefined,
    );

    expect(options.map((option) => option.value)).toEqual([1]);
    expect(options.map((option) => option.label.trim())).toEqual(['Live Page']);
  });

  it("keeps the module's OWN page even when it is deleted, and marks it unmistakably", () => {
    // Exclusion cannot cover this case: the module already sits there, so dropping the option would make the
    // picker silently propose a move the operator never asked for.
    const options = buildPageOptions(
      [
        page({ tabId: 1, tabName: 'Live Page' }),
        page({ tabId: 2, tabName: 'Deleted Page', isDeleted: true }),
      ],
      2,
    );

    expect(options.map((option) => option.value)).toEqual([1, 2]);
    expect(options[1]?.label).toContain(DELETED_PAGE_SUFFIX.trim());
  });

  it("names the module's page when the list does not carry it", () => {
    // A placement can name a page the caller may not administer, and the picker must not silently move the
    // module off it.
    const options = buildPageOptions([page({ tabId: 1, tabName: 'Live Page' })], 99);

    expect(options[0]).toEqual({ value: 99, label: CURRENT_PAGE_LABEL });
    expect(options.length).toBe(2);
  });

  it('disambiguates two pages that share a name, using the title the contract supplies', () => {
    const options = buildPageOptions(
      [
        page({ tabId: 1, tabName: 'Home', title: 'Corporate landing page' }),
        page({ tabId: 2, tabName: 'Home', title: 'Member landing page' }),
      ],
      undefined,
    );

    expect(options[0]?.label).toContain('Corporate landing page');
    expect(options[1]?.label).toContain('Member landing page');
    expect(options[0]?.label).not.toBe(options[1]?.label);
  });

  it('leaves an unambiguous name alone, so the common case is not made noisier', () => {
    const options = buildPageOptions(
      [page({ tabId: 1, tabName: 'Home', title: 'Corporate landing page' })],
      undefined,
    );

    expect(options[0]?.label.trim()).toBe('Home');
  });

  it('appends no qualifier when the title says nothing the name does not', () => {
    const options = buildPageOptions(
      [
        page({ tabId: 1, tabName: 'Home', title: 'Home' }),
        page({ tabId: 2, tabName: 'Home', title: '   ' }),
      ],
      undefined,
    );

    expect(options.map((option) => option.label.trim())).toEqual(['Home', 'Home']);
  });

  it('indents by hierarchy depth, and treats a negative depth as root level', () => {
    const options = buildPageOptions(
      [
        page({ tabId: 1, tabName: 'Root', level: 0 }),
        page({ tabId: 2, tabName: 'Child', level: 2 }),
        page({ tabId: 3, tabName: 'Odd', level: -4 }),
      ],
      undefined,
    );

    expect(options[0]?.label).toBe('Root');
    expect(options[1]?.label).toBe('\u00a0\u00a0\u00a0\u00a0Child');
    expect(options[2]?.label).withContext('a negative depth cannot repeat()').toBe('Odd');
  });

  it('offers nothing at all when no page list has been read, current page included', () => {
    // ⚠ AN EMPTY LIST IS AN UNREAD LIST, NOT A TENANT WITH NO PAGES. Measured on the creation form: a
    // host-owned module reports no portal, so no page list is requested at all - deliberately, because
    // falling back to some default tenant would offer pages nobody named. Synthesising a current-page option
    // there put one destination in the picker that no read supported.
    expect(buildPageOptions([], 0)).toEqual([]);
    expect(buildPageOptions([], undefined)).toEqual([]);
  });

  it('treats page 0 as a real page, because Tabs.TabID is an identity seeded at zero', () => {
    const options = buildPageOptions([page({ tabId: 0, tabName: 'First Ever Page' })], 0);

    expect(options.length).withContext('the current page is not duplicated').toBe(1);
    expect(options[0]?.value).toBe(0);
    expect(options[0]?.label).not.toBe(CURRENT_PAGE_LABEL);
  });
});
