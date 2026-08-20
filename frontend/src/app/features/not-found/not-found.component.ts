import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../shared/components/page-header/page-header.component';

/** The heading the fallback view states. */
const NOT_FOUND_TITLE = 'Not Found';

/**
 * The recovery affordance's caption. Names the destination rather than the action — "Administration Home"
 * rather than "Go back" — because a link's accessible name is its text, and a reader arriving at this
 * view out of context needs to know WHERE the one link leads, not merely that it leads somewhere.
 */
const RECOVERY_LABEL = 'Return to Administration Home';

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
   * The sentence explaining what happened, supplied by the route. Bound from the wildcard route's
   * `data.message` by `withComponentInputBinding()`, which `app.config.ts` enables and which matches data
   * keys to input NAMES — so this name is a contract with the route table and must stay spelled as the
   * route spells it.
   */
  @Input() message = 'No administration screen is available at this address.';

  /** The level-one heading. */
  protected readonly title = NOT_FOUND_TITLE;

  /** The caption of the single recovery link. */
  protected readonly recoveryLabel = RECOVERY_LABEL;
}
