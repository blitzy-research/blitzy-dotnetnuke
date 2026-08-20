/**
 * The application addresses that more than one module has to name, declared once. ## Why this module
 * exists `app.routes.ts` declares the route TABLE — which addresses exist, in which order, behind which
 * gate. This module declares the small set of address LITERALS that code outside the route table has to
 * spell in order to navigate to one of them.
 */

/**
 * Where a caller with no usable session is sent. ⚠ MUST MATCH AN UNGATED ENTRY IN `app.routes.ts`, and it
 * must stay ungated. The sign-in screen is the one destination an expired session is sent to, so guarding
 * it would bounce that session between the guard and whatever redirected it.
 */
export const SIGN_IN_ROUTE = '/login';

/**
 * The query parameter the sign-in address carries the refused destination under. ⚠ EVERY PARTY THAT
 * WRITES THIS VALUE AND THE ONE PARTY THAT READS IT MUST AGREE, and until this constant existed nothing
 * made them.
 */
export const RETURN_URL_QUERY_KEY = 'returnUrl';

/**
 * The listing routes, each declared ONCE.
 *
 * ⚠ THESE WERE SIX SEPARATE PRIVATE LITERALS BEFORE, and the duplication became load-bearing the moment
 * `ListReturnStore` began keying remembered listing coordinates by route: `PORTAL_LIST_ROUTE` in the portal
 * form and `PORTAL_LIST_PATH` in the portal settings screen were two spellings of the same string, and a
 * future edit to one of them would have silently split one listing's coordinate into two entries, so a save
 * from one screen would no longer restore what the other recorded. A single constant makes that class of
 * divergence unrepresentable.
 */
export const PORTAL_LIST_ROUTE = '/portals';

/** The module listing. */
export const MODULE_LIST_ROUTE = '/modules';

/** The account listing. */
export const USER_LIST_ROUTE = '/users';

/** The role listing. */
export const ROLE_LIST_ROUTE = '/roles';

/**
 * The query parameter the role listing narrows itself by. Published here rather than kept private to the
 * listing because the role FORM has to read it too: the form returns the operator to the listing coordinate
 * it remembers, and a coordinate naming a group that has since been deleted is a request the server answers
 * 404 to - so the form validates that one parameter before navigating. Two spellings of the same name in two
 * files would let that guard silently stop matching.
 */
export const ROLE_LIST_GROUP_PARAM = 'group';

/**
 * The portal's membership settings screen - the legacy `UserSettings.Action` destination, labelled "User
 * Settings" in the sidebar.
 *
 * ⚠ NOT A LISTING, AND IT IS DECLARED HERE FOR THE SAME REASON THE LISTINGS ARE: more than one module has to
 * spell it. The role listing offers it as a header action, and the module listing now routes an
 * ADMINISTRATIVE module's settings affordance to it - because the settings of a module created from the
 * `User Accounts` package ARE this screen, and the generic module settings endpoint refuses that module
 * outright with `module.settings_protected`.
 */
export const MEMBERSHIP_SETTINGS_ROUTE = '/settings/membership';

/**
 * @param userId The signed-in account, which is also the only account this address may name — the
 * server's allowance requires the route's account to BE the caller.
 * @returns The absolute address of that account's password screen.
 */
export function credentialRemediationRoute(userId: number): string {
  return `/users/${String(userId)}/password`;
}

/**
 * Where a caller with an outstanding MANDATORY PROFILE COMPLETION is sent.
 *
 * @param userId The signed-in account, which is also the only account this address may name.
 * @returns The absolute address of that account's profile screen.
 */
export function profileRemediationRoute(userId: number): string {
  return `/users/${String(userId)}/profile`;
}

