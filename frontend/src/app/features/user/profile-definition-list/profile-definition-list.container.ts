import { ChangeDetectionStrategy, Component, computed, inject, type OnInit } from '@angular/core';
import type { Signal } from '@angular/core';

import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type {
  CreateProfilePropertyDefinitionRequest,
  ProfilePropertyDefinition,
  UpdateProfilePropertyDefinitionRequest,
} from '../../../core/models/profile.model';
import { UserStore } from '../../../core/state/user.store';
import {
  ProfileDefinitionListComponent,
  type ProfileDefinitionBulkFlag,
  type ProfileDefinitionReorder,
  type ProfileDefinitionUpdate,
} from './profile-definition-list.component';

/**
 * Copies the writable members of a declaration, optionally replacing some of them.
 *
 * The replace contract carries exactly the members a `PUT` accepts — nine of them — and
 * deliberately not the three the API refuses to be told: the identity, the tenant and the
 * module association are decided by the server, and `visibility` is written through the
 * per-account profile rather than the catalogue. Building the request by naming those nine
 * members explicitly, rather than spreading the whole record and deleting what does not
 * belong, is what makes a contract change a compile error here instead of a rejected request
 * at run time.
 *
 * ⚠ EVERY MEMBER MUST BE CARRIED, not just the one being changed. The endpoint replaces the
 * declaration rather than patching it, so a member omitted from the request is a member
 * cleared in the database. This is why a reorder rewrites the whole declaration to move it one
 * place: there is no narrower verb to use.
 *
 * @param definition The declaration as the server last reported it.
 * @param overrides The members to replace. Absent members keep the reported value.
 * @returns A complete replace request.
 */
function toWriteMembers(
  definition: ProfilePropertyDefinition,
  overrides: Partial<UpdateProfilePropertyDefinitionRequest> = {},
): UpdateProfilePropertyDefinitionRequest {
  return {
    propertyName: definition.propertyName,
    propertyCategory: definition.propertyCategory,
    dataType: definition.dataType,
    defaultValue: definition.defaultValue,
    length: definition.length,
    required: definition.required,
    validationExpression: definition.validationExpression,
    viewOrder: definition.viewOrder,
    visible: definition.visible,
    ...overrides,
  };
}

/**
 * The routed owner of the profile-property catalogue.
 *
 * WHY THIS CLASS EXISTS. `ProfileDefinitionListComponent` is deliberately presentational: it
 * injects nothing, holds no store, takes the catalogue and two busy flags as inputs, and emits
 * five intents as outputs. That is the right shape for a screen that must be constructible in
 * a specification without a transport, but it means the component CANNOT be mounted on a route
 * directly — nothing would supply its inputs and nothing would listen to its outputs, so the
 * catalogue would render permanently empty and every affordance on it would be a no-op that
 * looked like a working button. This container is the missing half: it supplies the state from
 * the account store and turns each emitted intent into the store command that carries it out.
 *
 * WHAT IT DELIBERATELY DOES NOT DO. It holds no state of its own — every value it binds is
 * derived from the store, so there is exactly one copy of the catalogue in the application and
 * no possibility of the screen and the store disagreeing. It performs no validation: the form
 * validates its own shape and the API validates the request. And it decides nothing about
 * presentation; the child owns all of it.
 *
 * MIGRATION: the legacy screen was `Website/admin/Users/ProfileDefinitions.ascx` together with
 * `EditProfileDefinition.ascx` — a listing with an inline command column and a separate edit
 * page. Both are collapsed into one screen here, which is why the container answers five
 * intents rather than navigating to a second address for two of them.
 */
@Component({
  selector: 'app-profile-definition-list-container',
  standalone: true,
  imports: [ErrorBannerComponent, ProfileDefinitionListComponent],
  templateUrl: './profile-definition-list.container.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfileDefinitionListContainerComponent implements OnInit {
  /** The account store, which owns the catalogue and every command against it. */
  private readonly store = inject(UserStore);

  /**
   * The tenant's declarations, in the order the server reports them.
   *
   * Passed through untouched. The display order is a server decision — a reorder is an intent
   * and the refreshed list is the outcome — so sorting here would make the screen disagree
   * with the store the moment two operators reorder at once.
   */
  protected readonly definitions: Signal<readonly ProfilePropertyDefinition[]> =
    this.store.profileDefinitions;

  /** Whether the catalogue is still being fetched. */
  protected readonly loading: Signal<boolean> = this.store.profileDefinitionsLoading;

  /** Whether a write is in flight. Every affordance that would start a second one is disabled. */
  protected readonly saving: Signal<boolean> = this.store.saving;

  /**
   * The failure document to surface, or `null`.
   *
   * Derived from the store's failure record rather than stored, so a screen cannot go on
   * showing a message the store has already cleared. The record also carries an operation name
   * and a summary; only the document is bound, because the shared banner renders the title, the
   * detail and the per-field messages from it and adding a second rendering of the same failure
   * would report it twice.
   */
  protected readonly problem: Signal<ProblemDetails | null> = computed<ProblemDetails | null>(
    () => this.store.failure()?.problem ?? null,
  );

  /**
   * Loads the catalogue once, on entry.
   *
   * In `ngOnInit` rather than the constructor so that the request is not issued while the
   * component is still being constructed — a specification that only wants to inspect the
   * rendered shape can construct the class without a fetch escaping. The store's own load
   * command clears any failure left by another screen before it dispatches, so entering this
   * screen never inherits someone else's message.
   */
  ngOnInit(): void {
    this.store.loadProfileDefinitions();
  }

  /**
   * Adds a declaration.
   *
   * @param request The create contract the form assembled, passed on exactly as received.
   */
  protected onCreate(request: CreateProfilePropertyDefinitionRequest): void {
    this.store.createProfileDefinition(request);
  }

  /**
   * Replaces a declaration.
   *
   * @param intent The declaration's identity and its new shape, both as the form assembled
   * them.
   */
  protected onUpdate(intent: ProfileDefinitionUpdate): void {
    this.store.updateProfileDefinition(intent.propertyDefinitionId, intent.submission);
  }

  /**
   * Removes a declaration.
   *
   * @param definition The declaration the screen confirmed removal of. Only its identity is
   * used; the rest is the child's copy of what the server last reported.
   */
  protected onRemove(definition: ProfilePropertyDefinition): void {
    this.store.deleteProfileDefinition(definition.propertyDefinitionId);
  }

  /**
   * Moves a declaration one place through the display order.
   *
   * MIGRATION: implemented as a SWAP OF TWO POSITIONS, which is what the legacy screen did.
   * `Website/admin/Users/ProfileDefinitions.ascx.vb` L182-L187 read the neighbouring
   * declaration's position and exchanged the two, then wrote both. The store explicitly
   * declines to own this arithmetic — "deciding which positions to write belongs to the
   * feature, because only the feature knows the set it is looking at" — and this container is
   * that feature: it holds the ordered list the operator is looking at, so it is the only party
   * that knows which declaration is the neighbour.
   *
   * Two writes are issued, one per row, and they are independent: they address different
   * declarations, so the server applies both, and each completion refreshes the catalogue so
   * the final order is the one the server reports rather than one this screen predicted. The
   * pair is NOT atomic and is not presented as though it were — a failure on the second write
   * surfaces in the banner with the first already applied, exactly as the legacy pair of writes
   * behaved.
   *
   * MIGRATION: a caveat inherited rather than introduced. When two declarations hold the SAME
   * position — which the legacy schema permits, since the column carries no uniqueness
   * constraint — exchanging their positions changes nothing and the row does not appear to
   * move. The legacy screen had the identical outcome for the identical reason. Assigning a
   * synthetic position instead would renumber rows the operator did not touch, so the faithful
   * behaviour is kept and recorded.
   *
   * An intent naming a declaration that is no longer in the list, or asking to move the first
   * row up or the last row down, is discarded rather than clamped: there is no neighbour to
   * exchange positions with, and inventing one would move a row the operator did not name.
   *
   * @param intent Which declaration is moving, and which way.
   */
  protected onReorder(intent: ProfileDefinitionReorder): void {
    const held: readonly ProfilePropertyDefinition[] = this.definitions();
    const index: number = held.findIndex(
      (candidate) => candidate.propertyDefinitionId === intent.propertyDefinitionId,
    );

    if (index < 0) {
      return;
    }

    const neighbourIndex: number = intent.direction === 'up' ? index - 1 : index + 1;
    const moved: ProfilePropertyDefinition | undefined = held.at(index);
    const neighbour: ProfilePropertyDefinition | undefined =
      neighbourIndex < 0 ? undefined : held.at(neighbourIndex);

    if (moved === undefined || neighbour === undefined) {
      return;
    }

    this.store.updateProfileDefinition(
      moved.propertyDefinitionId,
      toWriteMembers(moved, { viewOrder: neighbour.viewOrder }),
    );
    this.store.updateProfileDefinition(
      neighbour.propertyDefinitionId,
      toWriteMembers(neighbour, { viewOrder: moved.viewOrder }),
    );
  }

  /**
   * Sets one flag across every declaration that does not already hold the requested value.
   *
   * MIGRATION: the legacy screen offered exactly this, and carried it out the same way — a pass
   * over the set writing each declaration in turn, the shape recorded at
   * `Website/admin/Users/ProfileDefinitions.ascx.vb` L326. There is no bulk endpoint to call
   * instead: the API exposes one replace verb per declaration, so a bulk change is n writes and
   * is honest about it.
   *
   * Declarations that already hold the requested value are SKIPPED rather than rewritten. That
   * is not merely an optimisation: rewriting a row to the value it already holds would move its
   * modification stamp and, on a tenant with many declarations, would spend requests to change
   * nothing. Skipping them means a flag that is already set everywhere issues no request at
   * all, which is the correct outcome for an operator who clicks it twice.
   *
   * @param intent Which flag to set, and to what.
   */
  protected onBulkFlag(intent: ProfileDefinitionBulkFlag): void {
    const affected: readonly ProfilePropertyDefinition[] = this.definitions().filter(
      (candidate) =>
        (intent.flag === 'required' ? candidate.required : candidate.visible) !== intent.value,
    );

    for (const definition of affected) {
      this.store.updateProfileDefinition(
        definition.propertyDefinitionId,
        toWriteMembers(
          definition,
          intent.flag === 'required' ? { required: intent.value } : { visible: intent.value },
        ),
      );
    }
  }
}
