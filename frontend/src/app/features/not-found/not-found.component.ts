import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../shared/components/page-header/page-header.component';

/**
 * The heading the fallback view states.
 *
 * Authored rather than measured, and that is unavoidable: the legacy application had no
 * not-found screen, so none of the thirty-seven in-scope `App_LocalResources` files carries a
 * heading for one. It is kept to the plainest possible statement and deliberately matches the
 * leading words of the document title the route already sets, so the tab and the heading agree.
 */
const NOT_FOUND_TITLE = 'Not Found';

/**
 * The recovery affordance's caption.
 *
 * Names the destination rather than the action — "Administration Home" rather than "Go back" —
 * because a link's accessible name is its text, and a reader arriving at this view out of
 * context needs to know WHERE the one link leads, not merely that it leads somewhere.
 */
const RECOVERY_LABEL = 'Return to Administration Home';

/**
 * THE FALLBACK VIEW FOR AN ADDRESS THAT MATCHES NO ROUTE.
 *
 * WHY THIS COMPONENT EXISTS. The wildcard route previously loaded the shared `empty-state`
 * component DIRECTLY, and loading it directly is what produced two defects that could not be
 * fixed from the route table:
 *
 *   • NO `<h1>`. `empty-state` states its heading as an `<h2>`, correctly — it is a region-level
 *     component that eight screens embed BENEATH their own page heading. Rendered as an entire
 *     routed view it left the address with no level-one heading at all, so the document outline
 *     opened at level two and the main heading sat at the same level as the navigation rail's
 *     group headings.
 *
 *   • AN EMPTY ACTION SLOT. `empty-state` ends in `<div class="empty-state__actions">` wrapping
 *     an `<ng-content />`. A component loaded by the router has NO HOST TEMPLATE projecting into
 *     it, so that slot was structurally guaranteed to be empty and the view offered the reader
 *     no way out of the dead end.
 *
 * ⚠ THIS ADDS NO ELEVENTH SHARED COMPONENT, which is the constraint the route table was
 * respecting when it chose direct reuse. The shared inventory is closed at ten members and this
 * is not one of them: it is a ROUTED VIEW that COMPOSES two of the ten, exactly as every feature
 * screen does, and the specification's own route table anticipates the fallback "resolving to a
 * not-found view". The earlier note on the wildcard route reasoned that giving the view a proper
 * heading "would mean editing a shared component that eight other screens rely on". That cost is
 * real and it is also avoidable — composing the two components leaves both untouched, so
 * `empty-state` keeps its `<h2>` for the eight screens that embed it while this address gets the
 * `<h1>` a routed view owes.
 *
 * WHY THE RECOVERY LINK POINTS AT THE ROOT rather than at any named screen. The root already
 * resolves to a landing appropriate to the caller's authority, so a reader who cannot reach the
 * tenant listing is not sent to it. Naming a screen here would duplicate that resolution and
 * could send a member to an address a guard then refuses — trading one dead end for another.
 */
@Component({
  selector: 'app-not-found',
  standalone: true,
  imports: [RouterLink, PageHeaderComponent, EmptyStateComponent],
  templateUrl: './not-found.component.html',
  styleUrl: './not-found.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotFoundComponent {
  /**
   * The sentence explaining what happened, supplied by the route.
   *
   * Bound from the wildcard route's `data.message` by `withComponentInputBinding()`, which
   * `app.config.ts` enables and which matches data keys to input NAMES — so this name is a
   * contract with the route table and must stay spelled as the route spells it. It is declared
   * here rather than hardcoded so the wording stays where the route that causes it lives.
   *
   * A default is supplied so the component is renderable on its own — a route that forgot the
   * key would otherwise show an empty sentence rather than an explanation.
   */
  @Input() message = 'No administration screen is available at this address.';

  /** The level-one heading. */
  protected readonly title = NOT_FOUND_TITLE;

  /** The caption of the single recovery link. */
  protected readonly recoveryLabel = RECOVERY_LABEL;
}
