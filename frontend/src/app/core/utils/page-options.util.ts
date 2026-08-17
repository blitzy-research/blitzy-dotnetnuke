/**
 * The vocabulary every page selector in the application shares: which of a portal's pages may be offered
 * as a choice, in what order, and how a page's depth is shown.
 *
 * ⚠ EXTRACTED FROM THE PORTAL SETTINGS SCREEN RATHER THAN WRITTEN AFRESH, AND EXTRACTED BECAUSE A SECOND
 * SCREEN NEEDED IT. The membership settings screen offered its three redirect destinations as bare numeric
 * page identifiers - measured, three spinners the operator could type any number into, beside help text
 * promising they could "select a page" - while the portal settings screen four rooms away had already
 * worked out exactly which pages are legal choices, how a stored choice that has since been recycled must
 * still be shown, and how hierarchy is indicated. Copying that reasoning into a second file would have left
 * two answers to one question, free to drift apart; this file is the one answer.
 *
 * The admission rules are the legacy ones, taken from the arguments the DotNetNuke page selector was
 * constructed with: invisible pages ARE offered, recycled pages are NOT, only pages of the Normal type are
 * (which the legacy `GetURLType` reports exactly when the page's URL is empty), no role filtering is
 * applied, and administration pages are excluded unconditionally.
 */

/**
 * The module screens' "Move To Page" picker shares this file too, and its rules are stated with it below.
 *
 * ⚠ TWO SEPARATE VOCABULARIES LIVE IN ONE FILE, ON PURPOSE. `buildPageChoices` answers "which pages may a
 * SETTING point at", and `buildPageOptions` answers "which pages may a MODULE be placed on". The admission
 * rules genuinely differ - a setting may point at a page the operator cannot administer, while a placement
 * on a recycled page orphans the module invisibly - so the two are kept as distinct functions rather than
 * collapsed into one with a mode flag. They are co-located because both are the page-selector vocabulary and
 * a reader looking for either expects to find it here.
 */

/**
 * Builds the page-picker options every "Move To Page" control offers, from the tenant's page list.
 *
 * ⚠ SHARED ON PURPOSE, BECAUSE THE RULES WERE ONCE DUPLICATED AND DIVERGED. Two module screens offered the
 * same picker and built it differently: the settings screen excluded soft-deleted pages, and the creation
 * form bound the raw page list straight into a `@for`, so it offered pages in the recycle bin as placement
 * targets — a module placed on one is orphaned invisibly. Neither screen used the disambiguating title the
 * page contract carries, so two pages named `Home` rendered as two identical options.
 */

import type { SelectOption } from '../models/select-option.model';
import type { TabListItem } from '../models/tab.model';

/** One option in a page selector. */
export interface PageOption {
  /** The page identifier this option stores. */
  readonly value: number;

  /** The wording shown to the operator, already carrying its hierarchy indent. */
  readonly label: string;
}

/**
 * The identifier the legacy page selector carried on its synthetic "nothing chosen" option.
 *
 * ⚠ NOT A NULL AND NOT A ZERO. Zero is a real page - the page table's identity seeds at zero - so absence
 * cannot be spelled with it, and a native select cannot hold a null. The legacy handler sent this value and
 * the server rewrote it to a database null, so it never reached a column.
 */
export const NO_PAGE_SELECTED = -1;

/**
 * The wording of that option: `"<" + None_Specified + ">"`, where the shared legacy resource value of
 * `None_Specified.Text` is `None Specified`.
 */
export const NO_PAGE_SELECTED_LABEL = '<None Specified>';

/**
 * The indent one level of page depth contributes. Three full stops, which is what the legacy selector
 * used - not whitespace, because a native `option` collapses leading whitespace and the indent would
 * disappear.
 */
const INDENT_STEP = '...';

/**
 * The deepest indent that will be drawn. A page hierarchy cannot exceed the level column's range, and a
 * bound is kept here so a corrupt level cannot make one option megabytes wide.
 */
const MAX_INDENT_LEVELS = 127;

/** Reports whether a page is of the legacy Normal type. */
function isNormalPage(row: TabListItem): boolean {
  return row.url === null || row.url.trim().length === 0;
}

/** Reports whether a page sits in the administration band. */
function isAdministrationPage(row: TabListItem, adminTabId: number | null): boolean {
  if (adminTabId === null) {
    return false;
  }

  return row.tabId === adminTabId || row.parentId === adminTabId;
}

/**
 * Produces the indent prefix for a page at the given level.
 *
 * @param level The page's depth in the hierarchy.
 * @returns The prefix, which is empty for a top-level page.
 */
export function indentFor(level: number): string {
  if (!Number.isFinite(level) || level <= 0) {
    return '';
  }

  const steps = Math.min(Math.trunc(level), MAX_INDENT_LEVELS);

  return INDENT_STEP.repeat(steps);
}

/**
 * Builds the offerable page choices, WITHOUT any "nothing chosen" entry — each screen prepends its own,
 * because the value absence is spelled with differs between a selector that stores a sentinel and one that
 * stores a null.
 *
 * The received order is preserved rather than re-sorted: the listing already arrives in hierarchy order by
 * page order, which is the sequence the legacy iteration relied on, and re-sorting it here would put the
 * indents out of step with their parents.
 *
 * @param rows Every page of the portal, as received.
 * @param adminTabId The portal's administration page, or `null` when it is not yet known.
 * @param retain Page references the screen currently holds, so a stored choice the filter would otherwise
 * hide still appears. A page that has since been recycled, turned into a link, or moved under
 * administration must remain visible AS THE CURRENT VALUE rather than silently resetting the selector to
 * something the operator never chose.
 * @returns The choices, in hierarchy order, with any retained-but-unlisted reference appended.
 */
export function buildPageChoices(
  rows: readonly TabListItem[],
  adminTabId: number | null,
  retain: readonly number[],
): readonly PageOption[] {
  const options: PageOption[] = [];

  // Guards against a duplicated identifier in the response. Two options sharing a value would make a
  // native select ambiguous about which one is chosen.
  const seen = new Set<number>([NO_PAGE_SELECTED]);
  const wanted = new Set<number>(retain);

  for (const row of rows) {
    if (seen.has(row.tabId)) {
      continue;
    }

    const held = wanted.has(row.tabId);
    const admitted =
      row.isDeleted === false && isNormalPage(row) && !isAdministrationPage(row, adminTabId);

    if (!admitted && !held) {
      continue;
    }

    seen.add(row.tabId);
    options.push({ value: row.tabId, label: indentFor(row.level) + row.tabName });
  }

  // A reference the listing does not carry at all - a page read failure, or one deleted outright. Shown by
  // its identifier, which is the only thing known about it, rather than dropped: dropping it would change
  // the stored setting the moment the operator saved anything else on the screen.
  for (const tabId of retain) {
    if (!seen.has(tabId)) {
      seen.add(tabId);
      options.push({ value: tabId, label: String(tabId) });
    }
  }

  return options;
}

/**
 * What the option for the module's own page says when that page is not in the supplied list.
 *
 * The list is the tenant's pages; a placement can still name a page the list does not carry — one the
 * caller may not administer, for instance — and the picker must not silently move the module off it.
 */
export const CURRENT_PAGE_LABEL = 'This page';

/**
 * Appended to a soft-deleted page's option.
 *
 * MIGRATION: the legacy pickers never showed one. `GetPortalTabs` was called with `blnDeleted:=False` and
 * `Globals.vb:L830` filtered on `(objTab.IsDeleted = False Or blnDeleted = True)`, so a page in the recycle
 * bin was simply absent. A deleted page is still excluded as a DESTINATION here; this marker exists for the
 * one case exclusion cannot cover — the module already sits on a deleted page, so the option has to be
 * offered or the picker would silently propose moving it.
 */
export const DELETED_PAGE_SUFFIX = ' (in the recycle bin)';

/** The indent one hierarchy level contributes to an option's label. */
const LEVEL_INDENT = '\u00a0\u00a0';

/**
 * Builds the page-picker options.
 *
 * @param tabs The tenant's pages, unpaged and in the server's order.
 * @param currentTabId The page the module occupies, or `undefined` when none has resolved.
 * @returns The options, in list order, with the current page guaranteed present.
 */
export function buildPageOptions(
  tabs: readonly TabListItem[],
  currentTabId: number | undefined,
): readonly SelectOption<number>[] {
  // ⚠ NO LIST MEANS NO OPTIONS, INCLUDING NO CURRENT-PAGE FALLBACK. An empty list is not a tenant with no
  // pages - it is a list that was never read, which happens on the creation route before the read answers and
  // for a host-owned module whose tenant is deliberately not guessed. Synthesising a single option there would
  // put a destination in front of the operator that no read supports.
  if (tabs.length === 0) {
    return [];
  }

  const options: SelectOption<number>[] = [];
  let currentPresent = false;

  // Counted in one pass so that a name is only disambiguated when it is genuinely ambiguous: appending a
  // title to every option would make the common case noisier to read for no benefit.
  const nameCounts = new Map<string, number>();

  for (const tab of tabs) {
    if (tab.isDeleted && !(currentTabId !== undefined && tab.tabId === currentTabId)) {
      continue;
    }

    nameCounts.set(tab.tabName, (nameCounts.get(tab.tabName) ?? 0) + 1);
  }

  for (const tab of tabs) {
    const isCurrent = currentTabId !== undefined && tab.tabId === currentTabId;

    if (isCurrent) {
      currentPresent = true;
    } else if (tab.isDeleted) {
      // A soft-deleted page is not a destination. Placing a module on one orphans it invisibly: the
      // recycle bin does not list modules, and the page is not navigable.
      continue;
    }

    options.push({
      value: tab.tabId,
      label: pageOptionLabel(tab, (nameCounts.get(tab.tabName) ?? 0) > 1),
    });
  }

  if (currentTabId !== undefined && !currentPresent) {
    options.unshift({ value: currentTabId, label: CURRENT_PAGE_LABEL });
  }

  return options;
}

/**
 * Composes one option's label: its indent, its name, its disambiguating title and its recycle-bin marker.
 *
 * @param tab The page.
 * @param ambiguous Whether another offered page carries the same name.
 * @returns The label.
 */
function pageOptionLabel(tab: TabListItem, ambiguous: boolean): string {
  // `level` is a non-negative depth on the list contract; repeat() on 0 yields an empty prefix.
  const indent: string = LEVEL_INDENT.repeat(Math.max(tab.level, 0));

  const title: string = tab.title === null ? '' : tab.title.trim();

  // The title is only appended when it adds something: a title equal to the name disambiguates nothing, and
  // a blank one is stored on most pages.
  const qualifier: string = ambiguous && title !== '' && title !== tab.tabName ? ` — ${title}` : '';

  const deleted: string = tab.isDeleted ? DELETED_PAGE_SUFFIX : '';

  return `${indent}${tab.tabName}${qualifier}${deleted}`;
}
