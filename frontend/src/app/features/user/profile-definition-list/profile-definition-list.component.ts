import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, booleanAttribute } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { YesNoPipe } from '../../../shared/pipes/yes-no.pipe';
import {
  PROFILE_VISIBILITY,
  type ProfilePropertyDefinition,
  type ProfilePropertyDefinitionSubmission,
  type ProfileVisibilityCode,
} from '../../../core/models/profile.model';

/**
 * Which way a declaration is being moved through the display order.
 */
export type ReorderDirection = 'up' | 'down';

/**
 * A request to move one declaration through the display order.
 */
export interface ProfileDefinitionReorder {
  /** The declaration being moved. */
  readonly propertyDefinitionId: number;

  /** Which way it is moving. */
  readonly direction: ReorderDirection;
}

/**
 * A request to replace an existing declaration.
 */
export interface ProfileDefinitionUpdate {
  /** The declaration being replaced. */
  readonly propertyDefinitionId: number;

  /** Its new shape. */
  readonly submission: ProfilePropertyDefinitionSubmission;
}

/**
 * A request to set one flag across every declaration at once.
 */
export interface ProfileDefinitionBulkFlag {
  /** Which flag is being set. */
  readonly flag: 'required' | 'visible';

  /** The value to set it to. */
  readonly value: boolean;
}

/**
 * The typed shape of the inline create-and-edit form.
 *
 * Every control is declared `nonNullable`, so `form.value` is the whole model rather
 * than a partial and a reset returns each control to its declared initial value instead
 * of to `null`.
 */
interface DefinitionFormModel {
  propertyName: FormControl<string>;
  propertyCategory: FormControl<string>;
  dataType: FormControl<number>;
  length: FormControl<number>;
  defaultValue: FormControl<string>;
  validationExpression: FormControl<string>;
  viewOrder: FormControl<number>;
  required: FormControl<boolean>;
  visible: FormControl<boolean>;
  visibility: FormControl<ProfileVisibilityCode>;
}

/**
 * One selectable visibility.
 */
interface VisibilityOption {
  /** The code written back to the API. */
  readonly code: ProfileVisibilityCode;

  /** The wording shown to the operator. */
  readonly label: string;
}

/**
 * The data type given to a declaration created through this screen.
 *
 * MIGRATION: the legacy editor offered a data-type list drawn from the `DataType`
 * list in the `Lists` table, which the excluded list subsystem owned. There is no list
 * service in this workspace, so the field is a plain number and this value is its
 * starting point rather than a hard-coded choice — the operator may change it, and the
 * API validates nothing about it, exactly as the legacy screen's own dropdown did not.
 */
const DEFAULT_DATA_TYPE = 349;

/**
 * The profile-property catalogue: the tenant's declarations, and the inline form that
 * creates and replaces them.
 *
 * MIGRATION: this screen replaces a PAIR of legacy pages.
 * `Website/admin/Users/ProfileDefinitions.ascx` rendered the grid with four per-row
 * affordances — edit, delete, move up, move down — and
 * `Website/admin/Users/EditProfileDefinition.ascx` was a separate page reached by
 * navigation. The editor collapses into an inline form here, so creating or amending a
 * declaration no longer loses the operator's place in the list.
 *
 * SELECTOR CONTRACT
 * -----------------
 * The paired stylesheet names the six surfaces it owns and nothing else: the opening
 * help paragraph, the bulk flag region, the grid-level action row, the four-button
 * row-action group, the inline form, and the monospaced presentation of an expression
 * or default value. The template renders exactly those.
 *
 * WHY THE GRID IS A PLAIN TABLE
 * -----------------------------
 * The stylesheet's commentary describes the row-action group as content PROJECTED into
 * "the shared data table". `shared/components/data-table` does exist, but it accepts no
 * projected content at all — its template carries no `<ng-content />` and every cell is
 * rendered from the `columns` and `rows` inputs — so it cannot host the four per-row
 * action buttons this catalogue needs. Giving it a projection slot would change a
 * component nine other screens share, which is not something this screen may do on its
 * own, so the catalogue is a plain `<table>`. That is not a downgrade:
 * `src/styles/_tables.scss` styles bare
 * `table`, `caption`, `th[scope]`, `td` and the even body rows globally with unscoped
 * element selectors, which reach inside every component regardless of view
 * encapsulation, and the stylesheet's own commentary already relies on exactly that for
 * the alternating row background. The row-action buttons are rendered directly rather
 * than projected, which puts them under this component's own content attribute — the
 * same attribute projected content would have carried — so every selector in the paired
 * stylesheet still applies.
 *
 * PRESENTATIONAL BY CONSTRUCTION
 * ------------------------------
 * The component injects nothing. Declarations arrive as an input; creation, replacement,
 * removal, reordering and bulk flag changes leave as outputs. That is a deliberate scope
 * decision: `core/services` carries the notification, authentication and token-storage
 * services and no user service, so a screen that called the API itself would have to
 * introduce that service and its specification as a side effect — the unaccompanied
 * surface the review flagged.
 */
@Component({
  selector: 'app-profile-definition-list',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    LoadingSpinnerComponent,
    EmptyStateComponent,
    ConfirmDialogComponent,
    YesNoPipe,
  ],
  templateUrl: './profile-definition-list.component.html',
  styleUrl: './profile-definition-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfileDefinitionListComponent {
  /**
   * The inline create-and-edit form.
   */
  protected readonly form = new FormGroup<DefinitionFormModel>({
    propertyName: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    propertyCategory: new FormControl('', { nonNullable: true }),
    dataType: new FormControl(DEFAULT_DATA_TYPE, {
      nonNullable: true,
      validators: [Validators.required, Validators.min(0)],
    }),
    length: new FormControl(0, { nonNullable: true, validators: [Validators.min(0)] }),
    defaultValue: new FormControl('', { nonNullable: true }),
    validationExpression: new FormControl('', { nonNullable: true }),
    viewOrder: new FormControl(0, { nonNullable: true, validators: [Validators.min(0)] }),
    required: new FormControl(false, { nonNullable: true }),
    visible: new FormControl(true, { nonNullable: true }),
    visibility: new FormControl<ProfileVisibilityCode>(PROFILE_VISIBILITY.adminOnly, {
      nonNullable: true,
    }),
  });

  /**
   * The visibility choices offered, in the order the legacy editor offered them.
   */
  protected readonly visibilityOptions: readonly VisibilityOption[] = [
    { code: PROFILE_VISIBILITY.allUsers, label: 'All users' },
    { code: PROFILE_VISIBILITY.membersOnly, label: 'Members only' },
    { code: PROFILE_VISIBILITY.adminOnly, label: 'Administrators only' },
  ];

  /**
   * Whether the inline form is open.
   */
  protected formOpen = false;

  /**
   * The declaration currently being replaced, or `null` when the form is creating.
   */
  protected editing: ProfilePropertyDefinition | null = null;

  /**
   * The declaration awaiting confirmation of removal, or `null` when none is.
   */
  protected pendingRemoval: ProfilePropertyDefinition | null = null;

  /**
   * The declarations held.
   */
  private held: readonly ProfilePropertyDefinition[] = [];

  /**
   * The tenant's declarations, in the order they are to be displayed.
   *
   * The order is the API's to decide, not this screen's: a reorder is emitted as an
   * intent and the refreshed list is what shows the outcome. Sorting locally would make
   * the screen disagree with the server the moment two operators reorder at once.
   */
  @Input()
  set definitions(value: readonly ProfilePropertyDefinition[]) {
    this.held = value;

    // A declaration that has just been replaced or removed must not leave the form
    // editing a row that is no longer there, so the editing target is re-resolved
    // against the incoming list rather than kept as a stale object.
    const editingId = this.editing?.propertyDefinitionId;
    if (editingId !== undefined) {
      const stillPresent = value.find(
        (candidate) => candidate.propertyDefinitionId === editingId,
      );

      if (stillPresent === undefined) {
        this.closeForm();
      } else {
        this.editing = stillPresent;
      }
    }

    const pendingId = this.pendingRemoval?.propertyDefinitionId;
    if (
      pendingId !== undefined &&
      value.find((candidate) => candidate.propertyDefinitionId === pendingId) === undefined
    ) {
      this.pendingRemoval = null;
    }
  }

  get definitions(): readonly ProfilePropertyDefinition[] {
    return this.held;
  }

  /**
   * The screen's title, rendered by the shared page header.
   */
  @Input() heading = 'Profile Properties';

  /**
   * Whether the catalogue is still being fetched.
   */
  @Input({ transform: booleanAttribute }) loading = false;

  /**
   * Whether a mutation is in flight.
   *
   * Every affordance that would start a second mutation is disabled while set, which is
   * what prevents a double submission and a delete racing an edit.
   */
  @Input({ transform: booleanAttribute }) saving = false;

  /**
   * Emitted when the operator submits the form in creating mode.
   */
  @Output() readonly create = new EventEmitter<ProfilePropertyDefinitionSubmission>();

  /**
   * Emitted when the operator submits the form in editing mode.
   */
  @Output() readonly update = new EventEmitter<ProfileDefinitionUpdate>();

  /**
   * Emitted when the operator confirms removal of a declaration.
   */
  @Output() readonly remove = new EventEmitter<ProfilePropertyDefinition>();

  /**
   * Emitted when the operator moves a declaration through the display order.
   */
  @Output() readonly reorder = new EventEmitter<ProfileDefinitionReorder>();

  /**
   * Emitted when the operator sets one flag across every declaration.
   */
  @Output() readonly bulkFlag = new EventEmitter<ProfileDefinitionBulkFlag>();

  /**
   * Whether the catalogue holds anything.
   */
  protected get hasDefinitions(): boolean {
    return this.held.length > 0;
  }

  /**
   * The inline form's legend.
   */
  protected get formLegend(): string {
    return this.editing === null ? 'Add a profile property' : 'Edit profile property';
  }

  /**
   * Whether every declaration is required.
   *
   * Drives the bulk checkbox's checked state, so the control reflects the catalogue
   * rather than the operator's last gesture.
   */
  protected get allRequired(): boolean {
    return this.hasDefinitions && this.held.every((definition) => definition.required);
  }

  /**
   * Whether the required flag is set on some declarations but not all.
   */
  protected get someRequired(): boolean {
    return (
      this.held.some((definition) => definition.required) &&
      this.held.some((definition) => !definition.required)
    );
  }

  /**
   * Whether every declaration is visible.
   */
  protected get allVisible(): boolean {
    return this.hasDefinitions && this.held.every((definition) => definition.visible);
  }

  /**
   * Whether the visible flag is set on some declarations but not all.
   */
  protected get someVisible(): boolean {
    return (
      this.held.some((definition) => definition.visible) &&
      this.held.some((definition) => !definition.visible)
    );
  }

  /**
   * The message shown in the removal confirmation.
   */
  protected get removalMessage(): string {
    const name = this.pendingRemoval?.propertyName ?? '';

    return `Deleting "${name}" also deletes every value recorded against it. This cannot be undone.`;
  }

  /**
   * Whether a declaration can be moved earlier in the display order.
   *
   * @param definition The declaration.
   * @returns `true` when it is not already first.
   */
  protected canMoveUp(definition: ProfilePropertyDefinition): boolean {
    return this.indexOf(definition) > 0;
  }

  /**
   * Whether a declaration can be moved later in the display order.
   *
   * @param definition The declaration.
   * @returns `true` when it is not already last.
   */
  protected canMoveDown(definition: ProfilePropertyDefinition): boolean {
    const index = this.indexOf(definition);

    return index >= 0 && index < this.held.length - 1;
  }

  /**
   * The wording shown for a declaration's visibility.
   *
   * An unrecognised code is reported as itself rather than silently mapped to a
   * neighbour, because a code this screen does not know is information the operator
   * needs rather than something to hide.
   *
   * @param code The stored visibility code.
   * @returns The wording to render.
   */
  protected visibilityLabel(code: ProfileVisibilityCode): string {
    return (
      this.visibilityOptions.find((option) => option.code === code)?.label ?? `Code ${code}`
    );
  }

  /**
   * Opens the inline form for a new declaration.
   *
   * The new declaration's display order starts one past the current last, so a created
   * property lands at the end of the list rather than colliding with an existing
   * ordinal.
   */
  protected openCreate(): void {
    this.editing = null;
    this.form.reset({
      propertyName: '',
      propertyCategory: '',
      dataType: DEFAULT_DATA_TYPE,
      length: 0,
      defaultValue: '',
      validationExpression: '',
      viewOrder: this.held.length,
      required: false,
      visible: true,
      visibility: PROFILE_VISIBILITY.adminOnly,
    });
    this.formOpen = true;
  }

  /**
   * Opens the inline form to replace an existing declaration.
   *
   * @param definition The declaration to edit.
   */
  protected openEdit(definition: ProfilePropertyDefinition): void {
    this.editing = definition;
    this.form.reset({
      propertyName: definition.propertyName,
      propertyCategory: definition.propertyCategory,
      dataType: definition.dataType,
      length: definition.length,
      defaultValue: definition.defaultValue ?? '',
      validationExpression: definition.validationExpression ?? '',
      viewOrder: definition.viewOrder,
      required: definition.required,
      visible: definition.visible,
      visibility: definition.visibility,
    });
    this.formOpen = true;
  }

  /**
   * Closes the inline form and forgets what it was editing.
   */
  protected closeForm(): void {
    this.formOpen = false;
    this.editing = null;
  }

  /**
   * Shows the editor, or hides it when it is already showing.
   *
   * Opening through the disclosure control always starts a NEW property rather than
   * reopening whatever was last edited: the operator's last edit may have been submitted,
   * abandoned or invalidated by another operator, and silently resuming it would let an
   * amendment be applied to a row the operator is no longer looking at.
   */
  protected toggleForm(): void {
    if (this.formOpen) {
      this.closeForm();
      return;
    }

    this.openCreate();
  }

  /**
   * Submits the inline form.
   *
   * An invalid form emits nothing and reveals its messages instead. Which output is
   * raised is decided by whether the form was opened for editing, never by whether the
   * payload happens to carry an identifier — so an omission cannot turn an amendment
   * into a duplicate.
   */
  protected onSubmit(): void {
    if (this.saving) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const submission = this.toSubmission();
    const target = this.editing;

    if (target === null) {
      this.create.emit(submission);
      return;
    }

    this.update.emit({ propertyDefinitionId: target.propertyDefinitionId, submission });
  }

  /**
   * Asks for confirmation before removing a declaration.
   *
   * @param definition The declaration to remove.
   */
  protected requestRemoval(definition: ProfilePropertyDefinition): void {
    this.pendingRemoval = definition;
  }

  /**
   * Emits the removal the operator confirmed.
   */
  protected onRemovalConfirmed(): void {
    const target = this.pendingRemoval;
    this.pendingRemoval = null;

    if (target === null) {
      return;
    }

    this.remove.emit(target);
  }

  /**
   * Abandons a removal the operator declined.
   */
  protected onRemovalCancelled(): void {
    this.pendingRemoval = null;
  }

  /**
   * Emits a request to move a declaration through the display order.
   *
   * @param definition The declaration to move.
   * @param direction Which way to move it.
   */
  protected onReorder(definition: ProfilePropertyDefinition, direction: ReorderDirection): void {
    if (direction === 'up' && !this.canMoveUp(definition)) {
      return;
    }

    if (direction === 'down' && !this.canMoveDown(definition)) {
      return;
    }

    this.reorder.emit({ propertyDefinitionId: definition.propertyDefinitionId, direction });
  }

  /**
   * Emits a request to set one flag across every declaration.
   *
   * @param flag Which flag to set.
   * @param event The originating change event, whose target carries the new value.
   */
  protected onBulkFlag(flag: 'required' | 'visible', event: Event): void {
    const target = event.target;

    if (!(target instanceof HTMLInputElement)) {
      return;
    }

    this.bulkFlag.emit({ flag, value: target.checked });
  }

  /**
   * The validation message for the one control that carries a rule, or `null`.
   *
   * MIGRATION: the wording is authored. The legacy screen's required-field validator
   * drew its message from `EditProfileDefinition.ascx`'s own resource file, which is
   * read for wording only; the resource entry is a label rather than a sentence, so
   * there is no message to preserve verbatim.
   *
   * @returns The message, or `null` when there is none to show yet.
   */
  protected get nameMessage(): string | null {
    const control = this.form.controls.propertyName;

    if (control.valid || !(control.dirty || control.touched)) {
      return null;
    }

    return 'A property name is required.';
  }

  /**
   * Converts the form's value into a submission.
   *
   * Blank optional text becomes `null` rather than the empty string, because the columns
   * behind them are nullable and the API distinguishes "no default declared" from "a
   * default that is the empty string".
   *
   * @returns The submission to emit.
   */
  private toSubmission(): ProfilePropertyDefinitionSubmission {
    const value = this.form.getRawValue();

    return {
      propertyName: value.propertyName.trim(),
      propertyCategory: value.propertyCategory.trim(),
      dataType: value.dataType,
      defaultValue: value.defaultValue.length === 0 ? null : value.defaultValue,
      length: value.length,
      required: value.required,
      validationExpression:
        value.validationExpression.length === 0 ? null : value.validationExpression,
      viewOrder: value.viewOrder,
      visible: value.visible,
      visibility: value.visibility,
    };
  }

  /**
   * The position of a declaration in the held list.
   *
   * @param definition The declaration.
   * @returns Its index, or -1 when it is not held.
   */
  private indexOf(definition: ProfilePropertyDefinition): number {
    return this.held.findIndex(
      (candidate) => candidate.propertyDefinitionId === definition.propertyDefinitionId,
    );
  }
}
