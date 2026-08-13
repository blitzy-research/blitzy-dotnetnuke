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
