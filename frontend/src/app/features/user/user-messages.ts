/**
 * Wording shared by more than one screen in the account feature.
 *
 * ⚠ WHY THIS FILE EXISTS RATHER THAN ONE SCREEN IMPORTING FROM ANOTHER. The account listing and the account
 * form both report the removal of an account, and they must report it with the SAME sentence: before this,
 * the form announced nothing at all, so whether an operator learned that their deletion had succeeded
 * depended on which screen they had started from. The obvious repair is for one component to import the
 * other's constant, and that is exactly what this file avoids.
 *
 * Both screens are lazily loaded on their own routes. A component class cannot be tree-shaken away, because
 * its decorator is a side effect, so importing the listing component into the form component in order to
 * reach one string would pull the whole listing - its template, its styles and its dependency graph - into
 * the form's chunk. A module of plain constants has no side effects and costs nothing to share.
 *
 * Only wording that genuinely belongs to more than one screen goes here. A string used by a single screen
 * stays with that screen, where it can be read next to the markup it describes.
 */

/**
 * `UserDeleted.Text` — the confirmation shown when an account has been removed.
 *
 * Used by the account listing, which removes a row in place, and by the account form, which removes the
 * record it is editing and returns to the listing.
 */
export const USER_DELETED_MESSAGE = 'User Deleted Successfully';

