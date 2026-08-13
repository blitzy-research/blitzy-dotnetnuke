// =============================================================================
//  portal-alias-list.component.ts - the portal HTTP-alias list, WITH the create,
//  edit and delete form on the same screen.
// =============================================================================
//
//  ROUTE
//  -----
//  `/portals/:portalId/aliases`, reached by `loadComponent` from the portal
//  feature's own route table. `withComponentInputBinding()` is configured in
//  `app/app.config.ts`, which delivers the `:portalId` path parameter into the
//  input of the SAME NAME below - so that input's name is part of the route
//  contract and a rename breaks the binding silently, with no compile error and
//  no runtime error. Navigation guarding is declared on the route, never here.
//
//  WHY THIS SCREEN OWNS A FORM
//  ---------------------------
//  The legacy application split the work across two controls - a listing
//  (`Website/admin/Portal/PortalAlias.ascx.vb`) and a separate edit page
//  (`Website/admin/Portal/EditPortalAlias.ascx.vb`) reached by a query string.
//  The target feature has no alias-form route, so the listing, the create
//  affordance and the edit affordance are all here. See MIGRATION 10.
//
//  WHERE STATE LIVES
//  -----------------
//  In `core/state/portal.store.ts`. This component holds only PRESENTATION state
//  - whether the form is open, whether a deletion is being confirmed, whether a
//  submit has been attempted and which field messages to show - and reads every
//  data slice through the store's `asReadonly()` projections. It never writes a
//  store signal, never injects a transport, never composes a URL or a query
//  string, and declares no providers of its own.
//
//  TEMPLATE CONTRACT
//  -----------------
//  The sibling stylesheet publishes a markup contract and this component's
//  template must honour it exactly, because emulated view encapsulation makes a
//  drifted class name match nothing rather than error:
//
//      .portal-alias-list             the component root
//      .portal-alias-list__status     the failure surface and the wait indicator
//      .portal-alias-list__edit       the inline create / edit form
//      .portal-alias-list__commands   the form's command row
//
//  The template binds these members, all `protected` so the template may read
//  them and nothing outside the component can:
//
//    wording   heading, addActionLabel, aliasLabel, aliasHelp, cancelLabel,
//              deleteLabel, editLabel, deleteConfirmMessage, emptyMessage,
//              unusableRouteMessage, aliasInputMaxLength, aliasControlId,
//              submitLabel()
//    listing   columns(), rows(), loading(), listReady(), isListEmpty(),
//              routeUsable()
//    form      form, formOpen(), editing(), editingCurrentAlias(), aliasError(),
//              bannerProblem(), deleteAffordanceVisible(), confirmingDelete()
//    commands  startCreate(), rowEditable(row), editAlias(row), submit(),
//              cancelEdit(), requestDelete(), confirmDelete(), cancelDelete(),
//              onAliasInput(), onAliasBlur()
//
//  THE SIBLING TEMPLATE IS A SEPARATELY OWNED FILE. `portal-alias-list.component.html`
//  is planned as its own artefact and is authored alongside this one rather than by
//  it, which is why this component names it through `templateUrl` instead of carrying
//  an inline template - an inline template would leave that planned file with nothing
//  to do. The reference below is therefore not a sketch: it was extracted from this
//  comment verbatim, written to that path and compiled by the Angular compiler with
//  strict template checking enabled, which reported no diagnostic; the same content
//  then drove the whole behavioural suite for this screen in a real headless browser.
//  A template that follows it is known to compile and known to work.
//
//  A worked reference implementation of the template, which the sibling file may
//  follow verbatim:
//
//      <div class="portal-alias-list">
//        <app-page-header [title]="heading">
//          <button type="button" (click)="startCreate()">
//            {{ addActionLabel }}
//          </button>
//          <a [routerLink]="settingsLink()">{{ settingsLinkLabel }}</a>
//        </app-page-header>
//
//        <div class="portal-alias-list__status">
//          @if (bannerProblem() !== null) {
//            <app-error-banner [problem]="bannerProblem()" />
//          }
//          @if (loading()) {
//            <app-loading-spinner />
//          }
//        </div>
//
//        @if (routeUsable() === false) {
//          <app-empty-state [message]="unusableRouteMessage" />
//        } @else if (isListEmpty()) {
//          <app-empty-state [message]="emptyMessage">
//            <button type="button" (click)="startCreate()">
//              {{ addActionLabel }}
//            </button>
//          </app-empty-state>
//        } @else {
//          <app-data-table
//            [columns]="columns()"
//            [rows]="rows()"
//            [loading]="loading()"
//            (rowSelect)="editAlias($event)"
//          >
//            <span dataTableCaption>{{ heading }}</span>
//          </app-data-table>
//        }
//
//        @if (formOpen()) {
//          <form class="portal-alias-list__edit" [formGroup]="form"
//                (ngSubmit)="submit()">
//            <app-form-field
//              [label]="aliasLabel"
//              [for]="aliasControlId"
//              [required]="true"
//              [help]="aliasHelp"
//              [error]="aliasError()"
//            >
//              <input
//                type="text"
//                [id]="aliasControlId"
//                [formControl]="form.controls.httpAlias"
//                [maxlength]="aliasInputMaxLength"
//                [attr.aria-invalid]="aliasError().length > 0"
//                (input)="onAliasInput()"
//                (blur)="onAliasBlur()"
//              />
//            </app-form-field>
//
//            <div class="portal-alias-list__commands">
//              <button type="submit">{{ submitLabel() }}</button>
//              <button type="button" (click)="cancelEdit()">
//                {{ cancelLabel }}
//              </button>
//              @if (deleteAffordanceVisible()) {
//                <button type="button" (click)="requestDelete()">
//                  {{ deleteLabel }}
//                </button>
//              }
//            </div>
//          </form>
//        }
//
//        @if (confirmingDelete()) {
//          <app-confirm-dialog
//            [message]="deleteConfirmMessage"
//            [confirmLabel]="deleteLabel"
//            [danger]="true"
//            (confirm)="confirmDelete()"
//            (cancel)="cancelDelete()"
//          />
//        }
//
//        <ng-template #aliasCommands let-row="row">
//          @if (rowEditable(row)) {
//            <button type="button" (click)="editAlias(row)">
//              {{ editLabel }}
//            </button>
//          }
//        </ng-template>
//      </div>
//
//  Note what the reference does NOT do: it declares no landmark element, because
//  the application shell owns each of those exactly once; it declares no pager,
//  because this collection is unpaged; it declares no filter, because the legacy
//  screen had none; it binds no ordering, because the legacy grid declared no
//  `AllowSorting`; and it carries no inline style attribute and no markup-injecting
//  binding at all.
//
//  Two structural requirements the template must respect:
//
//    * `<ng-template #aliasCommands let-row="row">` - the per-row command cell -
//      must sit at the TOP LEVEL of the template, outside every `@if` and
//      `@for`. The static view query below resolves it before the first change
//      detection pass, which is what keeps the column list a stable reference.
//    * The alias input must carry `id="{{ aliasControlId }}"` so the shared form
//      field's `for` binding names it, and `[formControl]` so the typed control
//      drives it.
//
//  A NOTE ON WORDING, WHICH IS MEASURED AND NOT AUTHORED
//  ----------------------------------------------------
//  Every user-facing string below was taken from a legacy resource file's VALUE,
//  never from a markup attribute, and the local file wins over the global one.
//  Each constant records where it was measured. Resource text in this
//  application's legacy is UNTRUSTED MARKUP - 76 values across the in-scope
//  resource files carry an HTML tag and one carries a live third-party script -
//  so every string here is PLAIN TEXT, bound by interpolation only. No markup
//  binding, no sanitiser and no sanitiser bypass appears in this file or in its
//  template, and the one resource value that carried markup is re-authored as
//  prose. See MIGRATION 7.
//
//  MIGRATION: localisation is NOT ported. The legacy screens resolved every
//  label through `Localization.GetString` against a per-control resource file
//  and, for the grid, through `Localization.LocalizeDataGrid` at
//  `Website/admin/Portal/PortalAlias.ascx.vb:L69`, which overwrote the markup's
//  own `HeaderText` at run time - which is exactly why the column heading below
//  comes from the resource key `HTTP Alias.Header` and not from the markup
//  attribute. No translation runtime is added, no message identifier is emitted
//  and no localisation call is reproduced.
//
//  MIGRATION: `ViewState("UrlReferrer")`
//  (`Website/admin/Portal/EditPortalAlias.ascx.vb:L132-L136`) has NO successor
//  here and is deliberately not modelled as state. It existed so that
//  `cmdCancel_Click` (L158-L160) and `cmdDelete_Click` (L189) could
//  `Response.Redirect` back to wherever the operator came from - return
//  navigation, which is a router concern rather than screen state, and which
//  this screen does not need at all because the form is inline: cancelling
//  closes it and leaves the operator exactly where they were. The legacy
//  behaviour also carried a defect worth recording, since the referrer was
//  stored as the empty string when absent (L135) and the redirect was then
//  issued to `""`.
//
//  MIGRATION: rate limiting does not apply to these endpoints, so this screen
//  models no throttled state and no retry-after handling. The limiter is global
//  and classifies each request from endpoint metadata - the
//  `[CredentialEndpoint]` marker - falling back to a whole credential path
//  segment on a body-carrying method; the alias actions carry no marker and no
//  such segment, so `429` cannot arise from a portal-alias request. The
//  exemption is per action rather than per area: `POST /api/v1/portals` IS
//  marked.
//
// =============================================================================
//  MIGRATION INDEX - SEVENTEEN DELIBERATE DIFFERENCES, EACH CITED AND EACH
//  DEVELOPED IN FULL AT ITS OWN SITE BELOW
// =============================================================================
//
//   1. MIGRATION: validation is declarative where the legacy screen had NONE.
//      `Website/admin/Portal/editportalalias.ascx` declares no validator of any
//      kind and `EditPortalAlias.ascx.vb:L209` made a blank entry a silent
//      no-op. Developed at the form's construction and at ALIAS_REQUIRED_MESSAGE.
//
//   2. MIGRATION: the BARE, BROAD `Catch` is gone. The legacy update path caught
//      every exception and reported a duplicate host name for all of them
//      (`EditPortalAlias.ascx.vb:L223-L228`); only a real refusal produces that
//      message now. Developed at `serverAliasMessage`.
//
//   3. MIGRATION: a permission denial is a WARNING, not an error. Legacy used
//      `RedError` at `EditPortalAlias.ascx.vb:L67` and L183; the canonical legacy
//      access-denied screen uses `YellowWarning`
//      (`Website/admin/Security/AccessDenied.ascx.vb:L43`). Developed on the
//      class and at `announceRefusal`.
//
//   4. MIGRATION: host-name resolution is an EXACT match on the server, replacing
//      the legacy `like '%' + @PortalAlias + '%'` with `min(PortalID)`
//      (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600`).
//      No host-name matching of any kind is performed here. Developed on the class.
//
//   5. MIGRATION: the legacy abbreviated query key does not survive. The listing
//      composed an edit address from it (`Website/admin/Portal/portalalias.ascx:L8`)
//      and the edit control read it back
//      (`Website/admin/Portal/EditPortalAlias.ascx.vb:L55`, coerced at L57); the
//      successor is the `portalAliasId` path segment. Developed on the class.
//
//   6. MIGRATION: the last-alias guard is CLIENT-SIDE ONLY. Legacy hid the button
//      at `EditPortalAlias.ascx.vb:L107` (`Count <= 1`) and the delete endpoint
//      declares no conflict response for it - removing a portal's last address is
//      permitted deliberately. NOT to be confused with MIGRATION 17, which is a
//      different removal rule and IS enforced. Developed at
//      `deleteAffordanceVisible`.
//
//   7. MIGRATION: `plAlias.Help` is RE-AUTHORED AS PROSE because resource text is
//      untrusted markup and the measured value carries five upper-case `<BR>`
//      tags. Developed at ALIAS_HELP.
//
//   8. MIGRATION: NEITHER legacy normalisation path is reproduced any more, and the
//      deviation is deliberate. The legacy screens stripped a protocol prefix and a
//      share prefix before saving (`EditPortalAlias.ascx.vb:L210-L215`; the
//      site-settings helper at `SiteSettings.ascx.vb:L824` stripped only the first),
//      but the target server refuses both with `400` - measured, not inferred - so
//      stripping concealed a refusal and submitted a value the operator had not
//      entered on a host-binding field. The entry is now judged and sent verbatim.
//      Developed at `httpAliasValidator` and at ALIAS_ENTRY_MAX_LENGTH.
//
//   9. MIGRATION: the help affordance is KEYBOARD REACHABLE, reversing the legacy
//      `tabindex="-1"` carried by both help controls
//      (`Website/controls/labelcontrol.ascx:L3-L4`). Developed at ALIAS_HELP.
//
//  10. MIGRATION: the listing and the form are ONE component, because the target
//      feature declares no alias-form route for the legacy second control
//      (`Website/admin/Portal/EditPortalAlias.ascx.vb`) to become. Developed on
//      the class.
//
//  11. MIGRATION: there are TWO denial sentences, and the read one is a hard-coded
//      English literal with no resource entry (`EditPortalAlias.ascx.vb:L67`)
//      while the delete one is resourced (L183). Developed at
//      VIEW_DENIED_MESSAGE and at `announceRefusal`.
//
//  12. MIGRATION: LEGACY DEFECT, annotated and not fixed -
//      `EditPortalAlias.ascx.vb:L78-L80` left the owning portal unset for a
//      non-superuser, so L234 targeted portal nought, which is REAL under an
//      identity seeded at minus one. Developed at `submit`.
//
//  13. MIGRATION: LEGACY DEFECT, annotated and not fixed -
//      `EditPortalAlias.ascx.vb:L231` converted a value known to be absent on
//      that branch, scoping the duplicate pre-check to the wrong tenant.
//      Developed at `submit`.
//
//  14. MIGRATION: deletion now ASKS FIRST. `editportalalias.ascx:L13` declares no
//      client-side confirmation and `EditPortalAlias.ascx.vb:L187` removed the row
//      on the first click. Developed at `requestDelete`.
//
//  15. MIGRATION: the legacy success message was UNREACHABLE - added at
//      `EditPortalAlias.ascx.vb:L242` and immediately followed by
//      `Response.Redirect` at L243. Developed at `onSaved`.
//
//  16. MIGRATION: the legacy share-prefix strip is NOT reproduced. Its literal `"\\"`
//      is TWO characters - VB string literals have no escape sequences and the legacy
//      `+ 2` offset proves it (`EditPortalAlias.ascx.vb:L213-L215`) - and getting that
//      reading wrong would have shifted the strip by one position. The strip itself is
//      now gone: the backslash is among the characters the shape rule refuses, so such
//      an entry is reported rather than silently rewritten. Developed at annotation 8.
//
//  17. MIGRATION: the legacy `IsNotCurrent` rule is RESTORED, and it now withholds
//      the DELETE command as well as the edit one. `PortalAlias.ascx.vb:L51-L60`
//      compared each row against the request's own alias and `portalalias.ascx:L8`
//      bound the answer to the edit hyperlink's visibility; legacy left removal to
//      the count rule alone, so an operator could unbind the very address they had
//      arrived through. The alias contract now publishes the answer per row,
//      computed server-side per request, and the server refuses both writes with a
//      `409` and its own reason code so the withheld affordance is not the only
//      thing enforcing it. Developed at `rowEditable`, at `editAlias`, at
//      `editingCurrentAlias`, at `deleteAffordanceVisible`, at `confirmDelete`, at
//      `isActiveAliasRefusal` and at CURRENT_ALIAS_MESSAGE.
//
// =============================================================================
//  IMPLICIT COERCIONS MADE EXPLICIT (the Option Strict asymmetry)
// =============================================================================
//
//  The administration pages compiled with strictness switched OFF -
//  `Website/release.config:L125` declares
//  `<compilation debug="false" strict="false">` - while the class library compiled
//  with it on. Every implicit coercion the two ported screens relied on is
//  itemised here with its successor, because each one is a place a value could
//  change meaning silently:
//
//    * `Int32.Parse(Id)` on a `String` parameter
//      (`Website/admin/Portal/PortalAlias.ascx.vb:L53`) - part of the
//      `IsNotCurrent` rule, which IS reproduced (MIGRATION 17). The coercion has no
//      successor because the comparison moved to the server, which holds both sides
//      as integers already: the alias contract reports the outcome as a boolean and
//      nothing here parses a row identifier out of text.
//    * `Int32.Parse(Request.QueryString("pid"))`
//      (`Website/admin/Portal/PortalAlias.ascx.vb:L85`) - becomes
//      {@link toPortalIdentifier}, a total parse that refuses anything which is
//      not a whole decimal integer rather than throwing on it.
//    * `CType(…, Integer)` applied to the abbreviated row-identifier query key
//      (`Website/admin/Portal/EditPortalAlias.ascx.vb:L57`) - the row identifier
//      now arrives as a number on the alias contract and is never parsed here.
//    * `CType(Request.QueryString("pid"), Integer)` twice
//      (`EditPortalAlias.ascx.vb:L79` and L81) - same successor as above; the
//      owning portal is read once, from the address.
//    * `CType(ViewState("PortalAliasID"), Integer)`
//      (`EditPortalAlias.ascx.vb:L176`) - becomes the store's alias selection,
//      typed `number | undefined`, tested with `!== undefined`.
//    * `Convert.ToInt32(ViewState("PortalAliasID"))` and
//      `Convert.ToInt32(ViewState("PortalID"))` (`EditPortalAlias.ascx.vb:L220`
//      and L221) - same successors; no conversion occurs on the edit path.
//    * `Convert.ToInt32(ViewState("PortalAliasID"))` at L231 and
//      `Convert.ToInt32(ViewState("PortalID"))` at L234 - the two DEFECTIVE
//      conversions, each of a value that was provably absent on its branch and
//      each therefore yielding nought. Recorded as MIGRATION 13 and 12; neither is
//      reproducible here.
//    * `Convert.ToString(Request.UrlReferrer)` and `Convert.ToString(ViewState(
//      "UrlReferrer"))` (`EditPortalAlias.ascx.vb:L133`, L159, L189, L243) - return
//      navigation, which has no successor as state; see the note above.
//    * `objPortalAliasInfo.HTTPAlias` assigned into a text box and back
//      (`EditPortalAlias.ascx.vb:L72` and L208) - the contract reports the host
//      name as nullable, and the conversion to text is written as an explicit null
//      test in {@link aliasText} rather than as a coalescing default.
// =============================================================================

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  Input,
  TemplateRef,
  ViewChild,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';

import type { OnInit } from '@angular/core';
import type { AbstractControl, ValidationErrors } from '@angular/forms';

import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import {
  fieldErrorMessage,
  isAliasInUseCode,
  isDuplicateAliasCode,
} from '../../../core/utils/form-errors.util';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from '../../../shared/components/data-table/data-table.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from '../../../shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import {
  LoadingSpinnerComponent,
} from '../../../shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';

import type {
  CreatePortalAliasRequest,
  PortalAlias,
  UpdatePortalAliasRequest,
} from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { PortalFailure } from '../../../core/state/portal.store';
import { isRouteId, parseRouteId } from '../../../core/utils/route-id.util';
import { FocusFirstInvalidDirective } from '../../../shared/directives/focus-first-invalid.directive';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/components/data-table/data-table.component';
import { SubmitGuardDirective } from '../../../shared/directives/submit-guard.directive';

// -----------------------------------------------------------------------------
//  MEASURED WORDING
// -----------------------------------------------------------------------------

/**
 * The screen heading.
 *
 * `ControlTitle_.Text` in
 * `Website/admin/Portal/App_LocalResources/PortalAlias.ascx.resx` and
 * `ControlTitle_edit.Text` in the edit screen's own resource file both read
 * `Portal Aliases`, so one heading serves the whole screen.
 *
 * MIGRATION: the legacy title key was MODE-DEPENDENT - the platform appended the
 * control mode to `ControlTitle_`, which is why the edit screen's key carries the
 * `edit` suffix - and there is NO `ControlTitle_add.Text` in either file at all.
 * Both measured values happen to be identical, so collapsing them to one heading
 * changes nothing an operator would see, and the absent add-mode key means no
 * wording was lost.
 */
const HEADING = 'Portal Aliases';

/**
 * The wording of the link back to this portal's configuration screen.
 *
 * `ControlTitle_.Text` in `SiteSettings.ascx.resx`, which is also the title that route declares,
 * so the link and its destination are named with one word.
 */
const SETTINGS_LINK_LABEL = 'Site Settings';

/**
 * The create affordance.
 *
 * `AddContent.Action` in `PortalAlias.ascx.resx`. The legacy screen published it
 * as a module action rather than as a button
 * (`Website/admin/Portal/PortalAlias.ascx.vb:L90`, an `EditUrl` to the separate
 * edit control); here it opens the inline form.
 */
const ADD_ACTION_LABEL = 'Add New HTTP Alias';

/**
 * The grid column heading.
 *
 * The resource key is `HTTP Alias.Header` - note the SPACE inside the key, which
 * the legacy resource files use freely - and its value is the authority over the
 * `HeaderText="HTTP Alias"` attribute on
 * `Website/admin/Portal/portalalias.ascx:L14`, because
 * `Localization.LocalizeDataGrid` replaced the attribute at run time.
 */
const ALIAS_COLUMN_HEADING = 'HTTP Alias';

/**
 * The form control's label.
 *
 * `plAlias.Text` in `EditPortalAlias.ascx.resx`. The legacy label control
 * appended a colon through its own `suffix=":"` attribute
 * (`Website/admin/Portal/editportalalias.ascx:L6`); the shared form field
 * normalises label punctuation, so no colon is added here and none is doubled.
 */
const ALIAS_LABEL = 'HTTP Alias';

/**
 * The command column's heading and the per-row command's accessible name.
 *
 * `Edit.Text` in `Website/App_GlobalResources/SharedResources.resx`, which is
 * where the legacy image's `resourcekey="Edit"` resolved
 * (`Website/admin/Portal/portalalias.ascx:L9-L10`, whose `AlternateText` read
 * `Edit` as well). This is one of the few legacy commands that DID carry an
 * accessible name, and it is honoured rather than re-authored.
 */
const EDIT_LABEL = 'Edit';

/**
 * How the row command's ACCESSIBLE name is qualified with the row it acts on.
 *
 * MIGRATION: the visible text stays the bare resource value and only the accessible name grows, which is
 * the same treatment the portal listing's own row commands received and for the same measured reason. The
 * legacy affordance was an unlabelled image whose only name was the global edit key
 * (`Website/admin/Portal/portalalias.ascx:L8-L11`), so every row's command reached assistive technology
 * with the identical context-free name `Edit` - runtime testing measured fifteen of them on one screen -
 * and on a row whose host name is absent there was nothing at all to distinguish it by. Qualifying the
 * name costs nothing visually and is what lets a screen-reader user moving between rows tell which host
 * name each command acts on.
 *
 * @param alias The host name as stored, or the absent-host wording when it holds none.
 * @returns The accessible name.
 */
function editCommandName(alias: string): string {
  return `${EDIT_LABEL} ${alias}`;
}

/**
 * What a host-name cell paints when the stored value is absent or empty.
 *
 * MIGRATION: the legacy cell painted NOTHING for both states and the two were indistinguishable.
 * `PortalAliasController.vb` hydrated the column with `Convert.ToString(dr("HTTPAlias")).ToLower`, and
 * `Convert.ToString` of `DBNull` yields the empty string, so a row holding no host name and a row holding
 * an empty one both rendered as an empty cell - which reads as a rendering failure rather than as data.
 * Runtime testing measured exactly that: two rows whose entire text content was two spaces.
 *
 * The MARK is a display-layer answer only, in the same discipline Rule T7 asks for and the same one the
 * portal listing's tally columns and expiry column already apply: the contract still distinguishes null
 * from the empty string, and only the cell stops painting both as nothing. No value is rewritten, and the
 * row is not filtered out - a row that exists is shown, because an operator cannot delete a row they
 * cannot see.
 */
const ABSENT_HOST_NAME_MARK = '\u2014';

/**
 * The words behind {@link ABSENT_HOST_NAME_MARK}, announced but not painted.
 */
const ABSENT_HOST_NAME_DESCRIPTION = 'no host name recorded';

/**
 * Shown in the command cell of the row this request arrived through.
 *
 * MIGRATION: the ABSENCE of a command on that row is the legacy behaviour and is preserved -
 * `portalalias.ascx:L8` bound the hyperlink's `Visible` property and an invisible server control emits
 * nothing. What is added is the REASON, in words. Runtime testing measured that row as the only one of
 * sixteen with no control at all and no explanation for it, and 11px shorter than its neighbours because
 * an entirely empty cell collapses the row - so the one row an operator most needs explained read as a
 * rendering fault. This wording has no resource provenance; it is authored, and it is deliberately short
 * because the full explanation is already announced when the row itself is pressed.
 */
const CURRENT_ALIAS_ROW_NOTE = 'In use';

/**
 * The submit command's label in EDIT mode.
 *
 * `cmdUpdate.Text` in the GLOBAL `SharedResources.resx`. Measured ABSENT from
 * `EditPortalAlias.ascx.resx`, so it resolves globally - and the markup carried
 * only an inline `text="Update"` with no resource key at all
 * (`editportalalias.ascx:L11`), which is precisely why the resource value rather
 * than the attribute is the authority.
 */
const UPDATE_SUBMIT_LABEL = 'Update';

/**
 * The submit command's label in CREATE mode.
 *
 * `cmdAdd.Text` in `EditPortalAlias.ascx.resx`. Measured ABSENT from the global
 * file, so this wording exists only locally.
 *
 * MIGRATION: there is NO `cmdAdd` control in the legacy markup. `cmdAdd` is a
 * resource KEY read into the ONE submit control, `cmdUpdate`, whose label was
 * chosen by mode - `EditPortalAlias.ascx.vb:L83` and L88 for the two add
 * branches, L75 for the edit branch. One control, two labels, reproduced here by
 * {@link PortalAliasListComponent.submitLabel}.
 */
const ADD_SUBMIT_LABEL = 'Add New Alias';

/**
 * The cancel command's label. `cmdCancel.Text` in the GLOBAL
 * `SharedResources.resx`; the markup's `resourcekey="cmdCancel"`
 * (`editportalalias.ascx:L12`) resolved there.
 */
const CANCEL_LABEL = 'Cancel';

/**
 * The delete command's label. `cmdDelete.Text` in the GLOBAL
 * `SharedResources.resx`; the markup's `resourcekey="cmdDelete"`
 * (`editportalalias.ascx:L13`) resolved there.
 */
const DELETE_LABEL = 'Delete';

/**
 * The deletion confirmation prompt.
 *
 * `DeleteItem.Text` in the GLOBAL `SharedResources.resx`, measured ABSENT from
 * both local files. See MIGRATION 14 - the legacy screen asked nothing before
 * deleting, and this wording is the platform's own confirmation prompt rather
 * than an invention.
 */
const DELETE_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/**
 * Announced after a successful create or update.
 *
 * `Success.Text` in `EditPortalAlias.ascx.resx`, measured ABSENT from the global
 * file. See MIGRATION 15 for why the legacy screen could not actually show it.
 */
const SAVED_MESSAGE = 'The Portal Alias has been saved.';

/**
 * Shown beside the control when the server refuses a host name already in use.
 *
 * `DuplicateAlias.Text` in `EditPortalAlias.ascx.resx` - THIS screen's own
 * wording, read by the legacy handler at `EditPortalAlias.ascx.vb:L226` on the
 * update path and again at L238 on the add path.
 *
 * MIGRATION: the shared failure vocabulary in `core/utils/form-errors`
 * DELIBERATELY collapses this legacy key onto the same published refusal code as
 * the sibling create-portal screen's `DuplicatePortalAlias`, and keeps only that
 * sibling's longer wording, on the ground that the server reports one code
 * however the collision was reached. That is the right decision for a shared
 * code-to-message map and the wrong one for THIS screen, whose parity obligation
 * is to its own resource file. So the code vocabulary is consulted and never
 * duplicated - no refusal-code string is written in this file - while the message
 * shown is the one this screen measured. Recorded rather than absorbed.
 */
const DUPLICATE_ALIAS_MESSAGE = 'The Portal Alias already exists.';

/**
 * Why the host name this request arrived through offers neither command, shown beside
 * the control when the server refuses a write to it and announced when an operator
 * presses the row it belongs to.
 *
 * MIGRATION 17: THIS SENTENCE HAS NO LEGACY ANTECEDENT, and it could not have one. The
 * legacy listing did not refuse the write - it HID the affordance, at
 * `Website/admin/Portal/PortalAlias.ascx.vb:L51-L60`, so the situation was unreachable
 * from the console and no resource key was ever needed. The rule is now enforced by the
 * server as well, which is what makes the refusal reachable at all, so it needs words.
 *
 * The wording matches the shared vocabulary's sentence for the same refusal rather than
 * diverging from it: unlike the duplicate refusal above, this screen has no measured
 * wording of its own to be faithful to, so a second spelling would be invention for its
 * own sake. It states the recovery because the refusal is actionable - the same change
 * succeeds from a request that reached the portal through another of its host names.
 */
const CURRENT_ALIAS_MESSAGE =
  'This is the host name your request reached this portal through, so it cannot be ' +
  'changed or removed. Reach the portal through one of its other host names and try again.';

/**
 * Announced when the server refuses a READ of this portal's host names.
 *
 * MIGRATION 11: this string has NO resource provenance whatsoever. It is a
 * hard-coded English literal in the legacy code-behind at
 * `Website/admin/Portal/EditPortalAlias.ascx.vb:L67`, reached when a
 * non-superuser addressed an alias belonging to another portal. It is a DIFFERENT
 * sentence from the delete denial below, which is resourced, and the two are
 * carried separately rather than merged.
 */
const VIEW_DENIED_MESSAGE = 'You do not have access to view this Portal Alias.';

/**
 * Announced when the server refuses a DELETE.
 *
 * `AccessDenied.Text` in `EditPortalAlias.ascx.resx`, measured ABSENT from the
 * global file, read by the legacy handler at
 * `Website/admin/Portal/EditPortalAlias.ascx.vb:L183`.
 */
const DELETE_DENIED_MESSAGE = 'You do not have access to delete this Portal Alias.';

/**
 * Announced after a successful delete.
 *
 * MIGRATION: AUTHORED WORDING WITH NO RESOURCE PROVENANCE, and recorded as such.
 * The legacy handler emitted no message at all - it deleted at
 * `EditPortalAlias.ascx.vb:L187` and redirected at L189 - so an operator learned
 * of the outcome only by seeing the reloaded page. Here the row simply vanishes
 * from a live grid, which announces nothing to a screen reader, so an
 * informational message is added. Phrased to parallel the measured
 * {@link SAVED_MESSAGE} so it reads as part of the same vocabulary.
 */
const DELETED_MESSAGE = 'The Portal Alias has been deleted.';

/**
 * Shown in place of the grid when the portal has no host names.
 *
 * MIGRATION: AUTHORED WORDING WITH NO RESOURCE PROVENANCE. The legacy grid simply
 * rendered its heading row with no rows beneath it and neither resource file
 * declares an empty-state key. A deliberate, documented addition.
 */
const EMPTY_MESSAGE = 'This portal has no HTTP aliases.';

/**
 * Shown when the address does not carry a usable portal identifier.
 *
 * MIGRATION: AUTHORED WORDING WITH NO RESOURCE PROVENANCE. The legacy screen had
 * no equivalent state: it read the portal from ambient page state and fell back
 * to the current portal (`PortalAlias.ascx.vb:L84-L88`), so an unusable value was
 * not expressible. Here the identifier arrives from the address, which can be
 * mistyped, and refusing to guess is the only safe answer - guessing would target
 * a real tenant, because every integer is a legitimate portal identifier.
 */
const UNUSABLE_ROUTE_MESSAGE =
  'This address does not identify a portal, so no HTTP aliases can be shown.';

/**
 * The help text for the alias control.
 *
 * MIGRATION 7: RE-AUTHORED AS PROSE. The measured `plAlias.Help` value in
 * `EditPortalAlias.ascx.resx` carries FIVE upper-case `<BR>` tags and a pair of
 * embedded double quotes around the protocol prefix it warns against. Resource
 * text is untrusted markup in this application, so the tags are not carried into
 * the DOM under any binding - the shared form field renders help as plain text in
 * a single paragraph, which is the composition this library dictates - and the
 * five visual line breaks become sentence boundaries instead. Every fact the
 * legacy value stated survives: the four accepted forms with their examples, and
 * the instruction to omit the protocol prefix. The quotation marks around the
 * prefix are dropped because prose punctuation carries the emphasis and a quoted
 * fragment inside a plain-text paragraph reads as part of the value to type.
 *
 * MIGRATION 9: THE HELP AFFORDANCE IS NOW KEYBOARD REACHABLE. The legacy label
 * control put this text behind a control that removed itself from the tab order -
 * `Website/controls/labelcontrol.ascx:L3-L4` sets `tabindex="-1"` on BOTH the help
 * link button and the image nested inside it - so a keyboard-only operator could not
 * reach the guidance at all, on this screen or on any other that used the control.
 * The shared form field's help toggle is an ordinary button in the tab order that
 * announces its expanded state and names the region it controls. The guidance
 * therefore costs a keyboard user nothing it did not already cost a mouse user, and
 * the change has no visual effect at rest.

 */
const ALIAS_HELP =
  'Please enter the domain name used to navigate to this portal. This could be a local ' +
  'address (ie. localhost), an IP address (ie. 127.0.0.1), a full URL (ie. ' +
  'www.mydomain.com), or a server name (ie. MYSERVER). Please do not include the http:// ' +
  'protocol prefix in your specification.';

// -----------------------------------------------------------------------------
//  VALIDATION WORDING
// -----------------------------------------------------------------------------
//
// MIGRATION 1: the legacy markup declared NO VALIDATOR OF ANY KIND -
// `Website/admin/Portal/editportalalias.ascx` has no `RequiredFieldValidator`, no
// `RegularExpressionValidator` and no `ValidationSummary` - and the consequence,
// measured at `EditPortalAlias.ascx.vb:L209`, is that a blank box made the whole
// handler body a NO-OP: `If strAlias <> "" Then` wraps everything and there is no
// `Else`, so nothing was saved and nothing was said. Validation here is therefore
// declarative and it TELLS the operator, which is more than the legacy screen did.
// The accepted input set is unchanged: each rule below reproduces a rule the
// server already enforces, so nothing an operator could previously store is
// refused now.
//
// The three messages are the SERVER'S OWN, reproduced verbatim from
// `Application/Validation/PortalAliasRules.cs` (the
// required message at L72, the length message composed at L113 and the shape
// message at L85-L87). Pre-empting a round trip must not describe the same
// refusal in different words depending on which side noticed it.

/** Mirrors the server's `NotEmpty` refusal. */
const ALIAS_REQUIRED_MESSAGE = 'An HTTP alias is required.';

/** Mirrors the server's maximum-length refusal, whose limit is 200. */
const ALIAS_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 200 characters.';

/**
 * Shown when the RAW entry exceeds the legacy input cap of 255 characters.
 *
 * Phrased on the server's length-message pattern. Reachable only through a
 * programmatic write, because the rendered control carries the legacy cap as its
 * own `maxlength` attribute, which bounds typing and pasting alike.
 */
const ALIAS_ENTRY_TOO_LONG_MESSAGE = 'An HTTP alias may not exceed 255 characters.';

/** Mirrors the server's shape refusal. */
const ALIAS_INVALID_MESSAGE =
  'An HTTP alias must be a host name, an IP address or a server name, optionally followed by ' +
  'a port and a path, and must not include a protocol prefix.';

/**
 * Mirrors the server's topology refusal, character for character.
 *
 * ⚠ THE SERVER COMPOSES THIS SENTENCE FROM `PortalAliasTopology`, so it cannot be edited on one side
 * alone. `PortalAliasContractTests` asserts that the server's composed value equals this literal, so
 * adding a reserved word or widening the depth on the server fails a backend test until this line
 * follows. The reserved words are listed in the server's own ordinal order for the same reason.
 */
const ALIAS_UNSUPPORTED_PATH_MESSAGE =
  'An HTTP alias may carry at most 1 path segment beneath its host name; that segment may ' +
  'contain only letters, digits, hyphens and underscores, and may not be one of the addresses ' +
  'this application reserves for itself (api, health, login, modules, openapi, portals, ' +
  'role-groups, roles, settings, swagger, users).';

// -----------------------------------------------------------------------------
//  MEASURED LIMITS
// -----------------------------------------------------------------------------

/**
 * The rendered control's own character cap.
 *
 * `MaxLength="255"` on `Website/admin/Portal/editportalalias.ascx:L7`, preserved
 * exactly so the typing experience is the legacy one.
 *
 * MIGRATION 8 (second half): this cap OVERSHOT the storage column by 55
 * characters, and that is a newly measured legacy inconsistency rather than a
 * decision. `PortalAlias.HTTPAlias` is declared `[nvarchar] (200)` at
 * `Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3807`,
 * so a 201-to-255 character entry was accepted by the browser and could not be
 * stored. The legacy attribute is kept because it is what the screen did, and the
 * real limit is enforced below on the value that is actually sent.
 */
const ALIAS_ENTRY_MAX_LENGTH = 255;

/**
 * The authoritative maximum, applied to THE ENTRY AS TYPED.
 *
 * 200, which is simultaneously the storage column's width (see above) and the
 * server's own `MaximumLength` (`PortalAliasRules.cs:L62`).
 *
 * It used to be measured against a normalised value, on the reasoning that an entry of `https://`
 * plus 195 characters transmits 195 and must be accepted. That reasoning was correct only while a
 * prefix was stripped before transmission, and it no longer is: the entry is now sent verbatim, so
 * the raw entry IS the transmitted string and is the one the bound applies to. Measuring anything
 * else would accept a request the server answers with `400`. The scheme-bearing entry the old
 * reasoning protected is refused outright now, by the shape rule, which is the point of the change.
 */
const ALIAS_MAX_LENGTH = 200;

// -----------------------------------------------------------------------------
//  TRANSPORT STATUSES THIS SCREEN INTERPRETS
// -----------------------------------------------------------------------------
//
// Read from `Api/Controllers/PortalAliasesController.cs`,
// which declares every response it can produce. Two findings are recorded because
// they contradict the brief this screen was written from:
//
//   * the update action answers 204 NO CONTENT (L406), not 200. The store's
//     update command already accounts for it by re-reading rather than
//     reconstructing, so nothing here depends on a body.
//   * the delete action DOES now declare a 409, and this note used to record that
//     it declared none. The active-alias refusal introduced it - see MIGRATION 6
//     and MIGRATION 17 - so both write actions and the removal can each answer a
//     conflict, for two different reasons.

/** A refusal on grounds of permission. The successor of both legacy denials. */
const FORBIDDEN_STATUS = 403;

/**
 * Whether a failure is the server refusing a host name already in use.
 *
 * NO REFUSAL-CODE STRING IS WRITTEN HERE, deliberately. The shared failure
 * vocabulary in `core/utils/form-errors` owns the code-to-meaning mapping and
 * the store has already applied it, so a recognised refusal arrives with its code
 * narrowed and an unrecognised one arrives with none. Comparing against a literal
 * here would duplicate that mapping and could silently drift from it, which is
 * exactly the kind of divergence no compiler reports.
 *
 * MIGRATION 17: THE TRANSPORT STATUS IS NO LONGER ACCEPTED AS CORROBORATION, and
 * removing it is a correction rather than a tightening. This test used to read
 * `conflictCode !== null || status === 409`, on the stated ground that these endpoints
 * declared exactly ONE cause of conflict. That ground no longer holds: the active-alias
 * refusal answers `409` from the update AND from the delete, so a status of 409 would
 * now claim a duplicate for a refusal that is not one, and would show an operator the
 * wrong sentence beside the wrong field. The narrowed code is the only sound
 * discriminator, and the comparison against it lives in the shared vocabulary - see
 * `isDuplicateAliasCode` - so this file still writes no code string.
 *
 * @param failure The classified failure the store published.
 * @returns True when the failure is the duplicate-host-name refusal.
 */
function isDuplicateRefusal(failure: PortalFailure): boolean {
  return isDuplicateAliasCode(failure.conflictCode);
}

/**
 * Whether a failure is the server refusing a write to the host name the request
 * arrived through.
 *
 * MIGRATION 17: the enforced half of the restored legacy affordance. The flag on each
 * row is what WITHHOLDS the two commands; this is what happens when the withholding is
 * bypassed - a crafted call, or a row set read before the operator's own address
 * changed - and it exists so that the screen can say which of the two conflicts it
 * received rather than guessing from the status.
 *
 * @param failure The classified failure the store published.
 * @returns True when the failure is the active-alias refusal.
 */
function isActiveAliasRefusal(failure: PortalFailure): boolean {
  return isAliasInUseCode(failure.conflictCode);
}

/**
 * The host name of one alias row, as text.
 *
 * RULE T7 AT THE BOUNDARY. The contract reports the host name as nullable because the
 * column is nullable, while the legacy absent-string sentinel was the EMPTY STRING and
 * not a null reference (`Library/Components/Shared/Null.vb` returns `""`). The two
 * therefore mean the same thing and are rendered the same way, and the conversion is
 * written as an explicit null test rather than as a coalescing default so that it is
 * visible as a decision rather than hidden in an operator.
 *
 * @param alias One alias row.
 * @returns The host name, or the empty string when the row carries none.
 */
function aliasText(alias: PortalAlias): string {
  const held: string | null = alias.httpAlias;

  return held === null ? '' : held;
}

// -----------------------------------------------------------------------------
//  NORMALISATION - DELIBERATELY ABSENT
// -----------------------------------------------------------------------------
//
// ⚠ THIS SECTION HELD A `normaliseHttpAlias` HELPER AND THE TWO PREFIX CONSTANTS IT STRIPPED
// THROUGH, AND ALL THREE ARE REMOVED RATHER THAN LEFT UNUSED. The helper was a faithful port of
// `EditPortalAlias.ascx.vb:L209-L215`, which removed a `://` prefix and then a `\\` prefix before
// saving. It ran before validation and its result was written back into the control and transmitted.
//
// It is gone because the target's server refuses both prefixes. Measured against the running API:
// `POST /api/v1/portals/-1/aliases` with `http://blitzy-p4-scheme.example.com` answered `400` with
// `HttpAlias` set to the same sentence this screen renders for a shape failure; a `\\`-prefixed
// value answered `400` with that sentence too; a plain host answered `201`. So stripping did not
// preserve legacy behaviour - it concealed a server refusal and submitted a value the operator had
// not entered, on the field that binds a host name to a tenant. See {@link httpAliasValidator} for
// the full reasoning and the AAP clause that licenses the deviation.
//
// Nothing replaces it. The raw entry is judged by the shape rule below and transmitted verbatim, so
// a prefix is now refused with the message the screen already promised - and no code path remains
// that can alter an operator's entry on its way to the server.

// -----------------------------------------------------------------------------
//  SHAPE RULE
// -----------------------------------------------------------------------------
//
// A faithful port of `PortalAliasRules.IsAcceptable` and its private helpers in
// `Application/Validation/PortalAliasRules.cs`, together with the path rules that
// type now delegates to `PortalAliasTopology` in
// `backend/src/DnnMigration.Domain/Common/PortalAliasTopology.cs` - the one
// definition five components mirror, this screen among them. It is
// reproduced rather than approximated with a pattern because an approximation that
// admits something the server refuses moves the refusal from a field message to a
// failed request, and one that refuses something the server admits breaks parity
// outright. Where the two could ever disagree, the SERVER decides: its answer
// arrives as a per-field message and is shown alongside these.
//
// The rule is evaluated against the NORMALISED value, which is what will be sent.

/**
 * The protocol separator, which the server refuses anywhere in an alias.
 *
 * Retained after the normalisation helper that also used it was removed, because the SHAPE RULE has
 * an independent need for it: `ContainsOnlyPermittedCharacters` refuses any entry containing it. Its
 * role has changed from "the prefix we strip through" to "the sequence we refuse", which is the whole
 * of this screen's correction - see {@link httpAliasValidator}.
 */
const SCHEME_SEPARATOR = '://';

/** Characters the server refuses outright, beyond whitespace and control codes. */
const FORBIDDEN_ALIAS_CHARACTERS: readonly string[] = Object.freeze([
  '\\',
  '@',
  '?',
  '#',
]);

/** Separates the authority from the optional child path. */
const PATH_SEPARATOR = '/';

/** Separates the host from the optional port. */
const PORT_SEPARATOR = ':';

/** Separates host labels. */
const LABEL_SEPARATOR = '.';

/** The hyphen, permitted inside a host label but never at its edges. */
const HYPHEN = '-';

/**
 * Characters permitted in a path segment in addition to letters and digits.
 *
 * ⚠ THE DOT IS ABSENT, mirroring `PortalAliasTopology.SegmentExtraCharacters`. That is what makes
 * the current-directory and parent-directory segments - `.` and `..` - unspellable rather than
 * separately refused, which is why the two constants this file used to carry for them are gone.
 */
const PATH_SEGMENT_EXTRAS: readonly string[] = Object.freeze([HYPHEN, '_']);

/**
 * The greatest number of path segments an alias may carry beneath its authority.
 *
 * Mirrors `PortalAliasTopology.MaximumPathSegments`. One, because one is what the legacy signup
 * screen composed and one is what the reverse proxy in `docker/api-proxy.conf` can deliver.
 */
const MAXIMUM_PATH_SEGMENTS = 1;

/**
 * Path segments the deployment owns, which no alias may use.
 *
 * Mirrors `PortalAliasTopology.ReservedPathSegments`: the console's own seven top-level routes and
 * the four roots the API answers. A stored alias spelling one of them would be unreachable, and
 * would make the console unreachable for the tenant that owned it. Compared without regard to case.
 */
const RESERVED_PATH_SEGMENTS: readonly string[] = Object.freeze([
  'api',
  'health',
  'login',
  'modules',
  'openapi',
  'portals',
  'role-groups',
  'roles',
  'settings',
  'swagger',
  'users',
]);

/** The highest port number the server accepts. */
const MAX_PORT_NUMBER = 65535;

/** The greatest number of digits a port may carry. */
const MAX_PORT_DIGITS = 5;


/** Matches one ASCII letter or digit, and nothing else - no Unicode letters. */
const ASCII_ALPHANUMERIC = /^[0-9A-Za-z]$/u;

/** Matches one ASCII digit. */
const ASCII_DIGIT = /^[0-9]$/u;

/** Matches any whitespace or C0/C1 control character. */
const WHITESPACE_OR_CONTROL = /[\s\u0000-\u001F\u007F-\u009F]/u;

/**
 * Whether a single character is an ASCII letter or digit.
 *
 * Deliberately ASCII-only, matching `char.IsAsciiLetterOrDigit` on the server. A
 * broader Unicode test would admit host names the server refuses.
 *
 * @param character Exactly one character.
 * @returns True when it is `0`-`9`, `A`-`Z` or `a`-`z`.
 */
function isAsciiAlphanumeric(character: string): boolean {
  return ASCII_ALPHANUMERIC.test(character);
}

/**
 * Whether the value carries only characters the server permits.
 *
 * Ports `ContainsOnlyPermittedCharacters` (`PortalAliasRules.cs:L170-L197`),
 * including its final check that no protocol separator is present. That check is now REACHABLE FOR
 * ITS OWN SAKE: while a normalisation step stripped the prefix before this ran, the only entry that
 * could still fail it was one carrying a second separator, so the ordinary `http://x` case never
 * reached it. The entry is judged verbatim now, so this is what refuses a protocol prefix and
 * produces the message the screen shows.
 *
 * @param alias The entry, exactly as the operator typed it.
 * @returns True when every character is permitted.
 */
function containsOnlyPermittedCharacters(alias: string): boolean {
  if (WHITESPACE_OR_CONTROL.test(alias)) {
    return false;
  }

  for (const character of alias) {
    if (FORBIDDEN_ALIAS_CHARACTERS.includes(character)) {
      return false;
    }
  }

  return alias.includes(SCHEME_SEPARATOR) === false;
}

/**
 * Whether the host portion is well formed.
 *
 * Ports `IsAcceptableHost` (`PortalAliasRules.cs:L253-L291`) character for
 * character: a label may not be empty, may not begin with a hyphen and may not end
 * with one, and the whole host may not end with a hyphen. Indexing is used rather
 * than iteration because the rule inspects the PRECEDING character when it meets a
 * separator.
 *
 * @param host The host portion, with any port already removed.
 * @returns True when the host is acceptable.
 */
function isAcceptableHost(host: string): boolean {
  if (host.length === 0) {
    return false;
  }

  let labelLength = 0;

  for (let index = 0; index < host.length; index += 1) {
    const character = host.charAt(index);

    if (character === LABEL_SEPARATOR) {
      // An empty label, or a label ending in a hyphen. The emptiness test comes
      // first, which is also what keeps the look-behind in range at index nought.
      if (labelLength === 0 || host.charAt(index - 1) === HYPHEN) {
        return false;
      }

      labelLength = 0;
      continue;
    }

    if (isAsciiAlphanumeric(character) === false && character !== HYPHEN) {
      return false;
    }

    if (labelLength === 0 && character === HYPHEN) {
      return false;
    }

    labelLength += 1;
  }

  return labelLength > 0 && host.charAt(host.length - 1) !== HYPHEN;
}

/**
 * Whether the port portion is well formed.
 *
 * Ports the port half of `IsAcceptableAuthority`
 * (`PortalAliasRules.cs:L215-L237`): one to five ASCII digits denoting a number
 * from one to sixty-five thousand five hundred and thirty-five. Note that nought
 * is refused, which is the server's rule and not a sentinel judgement - port nought
 * is not addressable.
 *
 * @param port The text after the port separator.
 * @returns True when the port is acceptable.
 */
function isAcceptablePort(port: string): boolean {
  if (port.length === 0 || port.length > MAX_PORT_DIGITS) {
    return false;
  }

  for (const digit of port) {
    if (ASCII_DIGIT.test(digit) === false) {
      return false;
    }
  }

  const parsed = Number.parseInt(port, 10);

  return parsed > 0 && parsed <= MAX_PORT_NUMBER;
}

/**
 * Whether the authority portion is well formed.
 *
 * Ports `IsAcceptableAuthority` (`PortalAliasRules.cs:L205-L238`). The port
 * separator is located by FIRST occurrence, exactly as the server does, so a second
 * colon lands inside the port text and is refused there.
 *
 * @param authority The value up to the first path separator.
 * @returns True when the authority is acceptable.
 */
function isAcceptableAuthority(authority: string): boolean {
  const separator = authority.indexOf(PORT_SEPARATOR);
  const host = separator === -1 ? authority : authority.slice(0, separator);

  if (isAcceptableHost(host) === false) {
    return false;
  }

  if (separator === -1) {
    return true;
  }

  return isAcceptablePort(authority.slice(separator + PORT_SEPARATOR.length));
}

/**
 * Whether one path segment could name a tenant.
 *
 * Ports `PortalAliasTopology.IsAddressableSegment`: non-empty, ASCII letters, digits, hyphens and
 * underscores only, and not one of {@link RESERVED_PATH_SEGMENTS}.
 *
 * @param segment One path segment, without separators.
 * @returns True when the segment is one the server could store.
 */
function isAddressableSegment(segment: string): boolean {
  if (segment.length === 0) {
    return false;
  }

  for (const character of segment) {
    if (
      isAsciiAlphanumeric(character) === false &&
      PATH_SEGMENT_EXTRAS.includes(character) === false
    ) {
      return false;
    }
  }

  return RESERVED_PATH_SEGMENTS.includes(segment.toLowerCase()) === false;
}

/**
 * Whether the child path is well formed.
 *
 * Ports `PortalAliasTopology.IsAcceptablePath`: the path may not be empty, may carry no more than
 * {@link MAXIMUM_PATH_SEGMENTS} segments, and every segment must be addressable. A trailing
 * separator therefore refuses the whole value, which is the server's behaviour.
 *
 * @param path The value after the first path separator.
 * @returns True when the path is acceptable.
 */
function isAcceptablePath(path: string): boolean {
  if (path.length === 0) {
    return false;
  }

  const segments = path.split(PATH_SEPARATOR);

  if (segments.length > MAXIMUM_PATH_SEGMENTS) {
    return false;
  }

  return segments.every((segment) => isAddressableSegment(segment));
}

/**
 * Whether an alias names a path this deployment can deliver a request to.
 *
 * Ports `PortalAliasTopology.IsSupportedAddress`: it judges DEPTH and path segments only, and says
 * nothing about the grammar of the authority. Separated from {@link isAcceptableHttpAlias} so the
 * screen can report the server's precise topology sentence rather than only its general one - the
 * server attaches both rules to the same field and so does this screen.
 *
 * @param alias The entry exactly as typed.
 * @returns True when the alias carries no path, or carries one this deployment can address.
 */
function isWithinSupportedTopology(alias: string): boolean {
  if (alias.trim().length === 0) {
    return true;
  }

  const pathStart = alias.indexOf(PATH_SEPARATOR);

  return (
    pathStart === -1 || isAcceptablePath(alias.slice(pathStart + PATH_SEPARATOR.length))
  );
}

/**
 * Whether a normalised host name is one the server will accept.
 *
 * Ports `IsAcceptable` (`PortalAliasRules.cs:L141-L162`), with ONE deliberate
 * difference: the server returns true for a blank value because its own
 * emptiness rule refuses it separately, whereas here emptiness is refused
 * explicitly. Keeping the server's permissive answer would let a value made
 * entirely of spaces reach the wire, since a whitespace-only entry is not the
 * empty string and the framework's required rule admits it.
 *
 * Exported so a specification can exercise the rule directly, without a form.
 *
 * @param alias The normalised value.
 * @returns True when the value is acceptable.
 */
export function isAcceptableHttpAlias(alias: string): boolean {
  if (alias.trim().length === 0) {
    return false;
  }

  if (containsOnlyPermittedCharacters(alias) === false) {
    return false;
  }

  const pathStart = alias.indexOf(PATH_SEPARATOR);
  const authority = pathStart === -1 ? alias : alias.slice(0, pathStart);

  if (isAcceptableAuthority(authority) === false) {
    return false;
  }

  return (
    pathStart === -1 || isAcceptablePath(alias.slice(pathStart + PATH_SEPARATOR.length))
  );
}


// -----------------------------------------------------------------------------
//  ROUTE PARAMETER READING
// -----------------------------------------------------------------------------

/**
 * Parses the `:portalId` path segment into a portal identifier.
 *
 * EVERY INTEGER IS A LEGITIMATE PORTAL IDENTIFIER, so there is no in-band value
 * this function can return to mean "the address did not carry one". `Portals` seeds
 * its identity at minus one
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`), so
 * the FIRST portal ever created is numbered minus one and the second is numbered
 * nought - and minus one is simultaneously the legacy integer null sentinel. A
 * fallback of nought or of minus one would therefore silently address a real
 * tenant. `NaN` is used instead: it is not an integer, it can never be a row
 * identifier, and the only test performed on it is `Number.isNaN`, which cannot be
 * confused by nought or by a negative value the way a truthiness test, a positivity
 * test or a null-coalescing default all would be.
 *
 * The legacy precedent for testing absence EXPLICITLY is exact - every page-state
 * read in the two screens being ported guards with `Is Nothing`, never with
 * truthiness, for example `EditPortalAlias.ascx.vb:L218`
 * `If Not ViewState("PortalAliasID") Is Nothing Then`. A row numbered nought
 * survives `Is Nothing`; it would not survive truthiness.
 *
 * MIGRATION, and an S11 coercion made explicit: the legacy screens coerced this
 * value with strictness switched off (`Website/release.config:L125` declares
 * `<compilation debug="false" strict="false">`), through `Int32.Parse` on a
 * `String` parameter at `PortalAlias.ascx.vb:L53` and L85, and through
 * `CType(Request.QueryString("pid"), Integer)` at `EditPortalAlias.ascx.vb:L79` and
 * L81. Each of those throws or coerces silently on a malformed value. The parse
 * below is total: it rejects anything that is not a whole decimal integer -
 * including a fractional value, a hexadecimal literal, an exponent, whitespace-only
 * text and the empty string - rather than converting it.
 *
 * @param value The raw route parameter, which arrives as text from the router.
 * @returns The identifier, or `NaN` when the segment does not carry one.
 */
export function toPortalIdentifier(value: unknown): number {
  // DELEGATES TO core/utils/route-id.util.ts RATHER THAN RESTATING THE GRAMMAR. The five screens
  // that parse a route identifier each carried their own version and they disagreed with one
  // another; the grammar now lives in one place, and it bounds the result to the range the schema
  // columns permit as well as refusing every spelling that is not a plain signed decimal integer.
  //
  // The rejection that used to be written out here is preserved by that parser and then some: it
  // refuses a numeric prefix with a tail (the reason this test existed), and also the surrounding
  // whitespace and the leading plus this body used to tolerate — two spellings of one key are two
  // ways to name one record, which is what a single grammar exists to prevent.
  //
  // A NUMBER is still accepted so a programmatic binding need not stringify one, but it is
  // VALIDATED against the same bounds rather than merely tested for finiteness.
  if (typeof value === 'number') {
    return isRouteId(value) ? value : Number.NaN;
  }
  if (typeof value !== 'string') {
    return Number.NaN;
  }
  return parseRouteId(value) ?? Number.NaN;
}

// -----------------------------------------------------------------------------
//  FORM
// -----------------------------------------------------------------------------

/** The name of the one control, which is also the wire member and the label target. */
const ALIAS_CONTROL_NAME = 'httpAlias';

/**
 * The rendered control's element identifier, so the shared form field's `for`
 * binding names it and the label is programmatically associated.
 */
const ALIAS_CONTROL_ELEMENT_ID = 'portal-alias-http-alias';

/** Error key: the normalised value is blank. Mirrors the server's `NotEmpty`. */
const ALIAS_BLANK_ERROR = 'httpAliasBlank';

/** Error key: the normalised value exceeds the storage and server limit of 200. */
const ALIAS_TOO_LONG_ERROR = 'httpAliasTooLong';

/** Error key: the normalised value is not a host name the server accepts. */
const ALIAS_INVALID_ERROR = 'httpAliasInvalid';

/**
 * Error key: the value names a path deeper than this deployment can route, or a segment it
 * reserves for itself.
 *
 * Held apart from {@link ALIAS_INVALID_ERROR} because the two carry different sentences and the
 * server reports them as two rules on the same field.
 */
const ALIAS_UNSUPPORTED_PATH_ERROR = 'httpAliasUnsupportedPath';

/**
 * The shape of the inline create and edit form.
 *
 * ONE control, because the legacy screen collected exactly one value
 * (`Website/admin/Portal/editportalalias.ascx:L7`) and the write contracts carry
 * exactly one member. The owning portal is NOT a member: it comes from the address
 * and is authoritative there, so there is no second copy to disagree with it - which
 * also makes the legacy defect recorded in MIGRATION 12 structurally impossible to
 * reproduce.
 *
 * Declared as an interface and used as the generic argument to `FormGroup`, so
 * `form.value` is fully typed rather than a partial and every control is reached by
 * name with compiler support.
 */
export interface PortalAliasFormModel {
  /** The host name, as entered. Never null - see the control's construction. */
  readonly httpAlias: FormControl<string>;
}

/**
 * Judges one entry EXACTLY AS TYPED, because that is exactly what will be sent.
 *
 * ⚠ THE ENTRY IS NO LONGER NORMALISED BEFORE JUDGEMENT, AND THE PREFIX STRIP IS GONE. This
 * validator used to strip a protocol prefix and a share prefix first - a faithful port of
 * `EditPortalAlias.ascx.vb:L209-L215` - and submission then wrote the stripped value back into the
 * control and transmitted it. The consequence was measured and is not acceptable on this field: an
 * operator who entered `http://x` saw a control that stayed valid with `aria-invalid="false"` and no
 * message, while the outbound body carried `{"httpAlias":"x"}`. A DIFFERENT VALUE FROM THE ONE THEY
 * ENTERED was submitted, silently, on the field that binds a host name to a tenant.
 *
 * WHY THE LEGACY PORT IS NOT THE AUTHORITY HERE. The target's server rejects both prefixes outright.
 * Verified against the running API rather than inferred from the rules: `POST` with
 * `http://blitzy-p4-scheme.example.com` answered `400` with `HttpAlias` = the very sentence
 * {@link ALIAS_INVALID_MESSAGE} carries, a `\\`-prefixed value answered `400` with the same sentence,
 * and a plain host answered `201`. So the accepted input set had already changed on the server, and
 * the client strip was not preserving legacy behaviour - it was CONCEALING a server refusal, which is
 * the one thing the shape rules in this file are documented never to do ("where the two could ever
 * disagree, the SERVER decides"). It also made this screen state one contract and honour another: the
 * message the operator is shown says an alias "must not include a protocol prefix".
 *
 * AAP §0.9.1 Minimal Change Clause item 1 permits exactly this deviation and gives this exact class of
 * reason as its own worked example - the substring alias match, changed because leaving it would carry
 * a multi-tenant mis-resolution into new code. Silent value rewriting on a host-binding field is the
 * same hazard. The divergence is recorded in `MIGRATION_NOTES.md` rather than absorbed.
 *
 * NOTHING AN OPERATOR COULD PREVIOUSLY STORE IS REFUSED NOW: the server already refused these entries,
 * so the only change is where the refusal appears - as a field message before the request, instead of
 * a rewritten value after it. Length is measured on the raw entry for the same reason: the raw entry
 * is what is transmitted, so it is the string the bound applies to.
 *
 * The empty entry returns no error of its own: the framework's required rule owns emptiness, and
 * reporting it twice would show the same message twice.
 *
 * @param control The alias control.
 * @returns The failures found, or null when the entry is acceptable.
 */
export function httpAliasValidator(control: AbstractControl<string, string>): ValidationErrors | null {
  const entry = control.value;

  if (entry.length === 0) {
    return null;
  }

  if (entry.trim().length === 0) {
    return { [ALIAS_BLANK_ERROR]: true };
  }

  if (entry.length > ALIAS_MAX_LENGTH) {
    return { [ALIAS_TOO_LONG_ERROR]: true };
  }

  // BOTH RULES ARE REPORTED, mirroring the server, which attaches the shape rule and the topology
  // rule to the same field and answers with every message that applies. A value that breaches the
  // topology also fails the shape rule, because the shape rule folds the topology in so that no
  // caller can reach the store through a predicate that omits part of the contract; reporting only
  // the general sentence would leave an operator who typed `host/a/b` or `host/api` unable to tell
  // which part of their entry was refused.
  const failures: ValidationErrors = {};

  if (isAcceptableHttpAlias(entry) === false) {
    failures[ALIAS_INVALID_ERROR] = true;
  }

  if (isWithinSupportedTopology(entry) === false) {
    failures[ALIAS_UNSUPPORTED_PATH_ERROR] = true;
  }

  return Object.keys(failures).length === 0 ? null : failures;
}

// -----------------------------------------------------------------------------
//  SHARED IMMUTABLE SEEDS
// -----------------------------------------------------------------------------
//
// Frozen constants rather than fresh literals, because the shared grid and the
// shared form field both take their inputs through setters that write a signal: a
// new array on every change-detection pass would notify a consumer on every pass
// even when nothing had changed.

/** The empty row set. */
const NO_ALIASES: readonly PortalAlias[] = Object.freeze([]);

/** The empty message set. */
const NO_MESSAGES: readonly string[] = Object.freeze([]);

/**
 * Which command is awaiting an answer.
 *
 * Recorded so that a permission refusal can be described in the words the legacy
 * screen used for THAT operation - the two denials are different sentences with
 * different provenance, and the transport reports the same status for both.
 */
type PendingOperation = 'none' | 'list' | 'create' | 'update' | 'delete';


// =============================================================================
//  COMPONENT
// =============================================================================

/**
 * The portal HTTP-alias screen: a listing, plus an inline form that creates, edits
 * and removes one host name.
 *
 * MIGRATION 10: THE LISTING AND THE FORM ARE ONE COMPONENT. The legacy application
 * used two controls reached by a query string -
 * `Website/admin/Portal/PortalAlias.ascx.vb` listed, and
 * `Website/admin/Portal/EditPortalAlias.ascx.vb` added, edited and removed - and the
 * target feature declares no alias-form route for the second one to become. So this
 * screen carries both, and it navigates nowhere: every command acts on state and on
 * the interface, and no address is composed for a form.
 *
 * MIGRATION 5: THE LEGACY ABBREVIATED QUERY KEY DOES NOT SURVIVE, and it is not even
 * quoted here. The listing built each row's edit address from a four-letter
 * query-string key through the page base class's edit-URL helper
 * (`Website/admin/Portal/portalalias.ascx:L8`), and the edit control read the same key
 * back out of the query string at `Website/admin/Portal/EditPortalAlias.ascx.vb:L55`
 * before coercing it at L57. The successor names the row in full, as the
 * `portalAliasId` path segment the transport declares - so the abbreviation appears
 * nowhere in this feature, not in a route, not in an identifier and not in a comment,
 * which is what stops it being reintroduced by someone reading this file for guidance.
 *
 * MIGRATION 4: HOST-NAME MATCHING IS THE SERVER'S AND IS NOT PERFORMED HERE AT ALL.
 * Legacy tenant resolution matched an incoming host name against stored aliases with
 * wildcards on both sides and then took the lowest identifier -
 * `select @PortalID = min(PortalID) from Portals where PortalAlias like '%' +
 * @PortalAlias + '%'`, created as `GetPortalSettings` at
 * `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600`
 * - so one portal's host name being a fragment of another's could resolve a request
 * to the WRONG TENANT, silently. Resolution is now an exact match performed by the
 * portal-alias resolution middleware. This screen therefore contains no fragment
 * search, no case-folding comparison and no derived projection that filters the
 * collection by host name: a client-side answer to a question the server owns could
 * disagree with it.
 *
 * MIGRATION 3, and the reason a refusal is not styled as a fault: a permission
 * denial surfaces at WARNING severity. The legacy handlers used
 * `ModuleMessage.ModuleMessageType.RedError` for both denials
 * (`EditPortalAlias.ascx.vb:L67` and L183), but the legacy vocabulary was
 * three-valued and its canonical access-denied screen chose the middle value:
 * `Website/admin/Security/AccessDenied.ascx.vb:L43` renders an externally supplied
 * denial at `YellowWarning`, and its else branch does the same. The denial is
 * therefore announced through `core/services/notification.service.ts` at `'warning'`,
 * and the shared error banner independently classifies the same status as a warning
 * rather than as danger, so nothing on this screen paints a working authorisation
 * decision as a system fault.
 */
@Component({
  selector: 'app-portal-alias-list',
  standalone: true,
  imports: [
    FocusFirstInvalidDirective,
    SubmitGuardDirective,
    ReactiveFormsModule,
    RouterLink,
    ConfirmDialogComponent,
    DataTableComponent,
    EmptyStateComponent,
    ErrorBannerComponent,
    FormFieldComponent,
    LoadingSpinnerComponent,
    PageHeaderComponent,
  ],
  templateUrl: './portal-alias-list.component.html',
  styleUrl: './portal-alias-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalAliasListComponent implements OnInit {
  // ---------------------------------------------------------------------------
  //  COLLABORATORS
  // ---------------------------------------------------------------------------

  /**
   * The one source of alias data and the one place alias writes are issued.
   *
   * Root-provided, so it is injected and never listed in a `providers` array here -
   * all provider wiring for this application lives in `app/app.config.ts`. Every
   * slice read below is one of the store's `asReadonly()` projections, so this
   * component cannot write one even by mistake.
   *
   * ONE BREADTH OF REACH IS RECORDED RATHER THAN WORKED AROUND. The store's
   * failure-discarding command clears every concern's failure, not the alias one
   * alone, and it publishes no alias-only equivalent. It is called here before each
   * command so that a stale report cannot be mistaken for the outcome of the command
   * just issued, and the breadth is harmless in this arrangement because the router
   * mounts one screen at a time - but it is the store's surface to narrow, not this
   * screen's, so nothing is reached around here and no private slice is touched.
   */
  private readonly store = inject(PortalStore);

  /** The announcement queue. Used for outcomes, never for field-level failures. */
  private readonly notifications = inject(NotificationService);

  /**
   * Binds this screen's write subscriptions to its own lifetime.
   *
   * The store is provided at the root and therefore outlives this screen, so an outcome
   * ticket left subscribed across a teardown would run this screen's continuation — closing
   * a form that no longer exists and announcing a success into a route the operator has
   * left. The reference is passed explicitly because these subscriptions are opened from
   * event handlers, where no ambient injection context exists.
   */
  private readonly destroyRef = inject(DestroyRef);

  // Held solely so `afterNextRender` can be reached from outside the constructor. Its only
  // consumers are `focusEntry` and `restoreInvokerFocus`, both of which must run AFTER the
  // conditional form block has been created or destroyed - which is a render, not a signal write.
  private readonly injector = inject(Injector);

  // The screen's own root element. Used only to resolve the create action as a focus fallback -
  // see `restoreInvokerFocus` - so the lookup is scoped to this screen rather than the document.
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  // ---------------------------------------------------------------------------
  //  ROUTE INPUT
  // ---------------------------------------------------------------------------

  /**
   * Which portal's host names to show, delivered from the `:portalId` path segment.
   *
   * THE NAME IS PART OF THE ROUTE CONTRACT. `withComponentInputBinding()` matches a
   * route parameter to an input of the same name, so renaming this member stops the
   * binding silently - the component still compiles, still renders and simply never
   * receives a portal.
   *
   * Declared as a setter so that a change of parameter on an already-mounted instance
   * issues a fresh read. A one-shot read in the lifecycle hook would leave the screen
   * showing the previous portal's host names after a navigation between two portals,
   * because the router reuses the component when only the parameter differs.
   *
   * The transform is total and yields `NaN` for anything that is not a whole integer -
   * see {@link toPortalIdentifier} for why no in-band fallback exists.
   */
  @Input({ required: true, transform: toPortalIdentifier })
  public set portalId(value: number) {
    const previous = this.portalIdValue();

    this.portalIdValue.set(value);

    if (Number.isNaN(value)) {
      return;
    }

    // An explicit equality test, so re-delivering the same parameter does not
    // re-request. `NaN` never reaches here, so the one value that is not equal to
    // itself cannot make this test misbehave.
    if (previous === value) {
      return;
    }

    this.closeForm();
    this.pending = 'list';
    this.store.clearFailures();
    this.store.loadAliases(value);
  }

  public get portalId(): number {
    return this.portalIdValue();
  }

  // ---------------------------------------------------------------------------
  //  VIEW QUERY
  // ---------------------------------------------------------------------------

  /**
   * The per-row command cell, supplied by this component's own template.
   *
   * `static: true` resolves it during view creation, before the lifecycle hook runs,
   * which is what lets the column list be built once with a stable reference instead
   * of re-forming after the first render. It is optional rather than asserted, because
   * a definite-assignment assertion would be a claim the compiler cannot check and the
   * lifecycle hook tests for it explicitly instead.
   *
   * Requires the template to declare `<ng-template #aliasCommands>` at its TOP LEVEL:
   * a static query does not descend into a conditional or repeated block.
   */
  @ViewChild('aliasCommands', { static: true })
  protected commandCell?: TemplateRef<DataTableCellContext<PortalAlias>>;

  /**
   * The entry box inside the inline form.
   *
   * NOT static: the form is inside a conditional block, so the query cannot resolve before the
   * block is created. It is read only from an `afterNextRender` callback, which runs after the
   * creation that resolves it.
   */
  @ViewChild('entryBox')
  private entryBox?: ElementRef<HTMLInputElement>;

  /**
   * The control that opened the form, so focus can be handed back to it.
   *
   * ⚠ CAPTURED FROM THE LIVE FOCUS RATHER THAN PASSED IN. The form is opened from three places -
   * the page-level create action, a row's Edit command, and a press on the row itself - and the
   * row press arrives through the shared grid's own output, which carries the ROW and not the
   * element that was pressed. Reading the active element at the moment the command runs covers
   * all three without any of them having to know it is being remembered.
   *
   * Held as a plain field and not a signal: nothing renders from it, and a signal write here
   * would schedule a change-detection pass for a value no template reads.
   */
  private formInvoker: HTMLElement | null = null;

  /**
   * The host-name cell's template.
   *
   * Pf-M1: the column was a plain field column, so a row holding no host name - or an
   * empty one - painted a cell whose entire content was whitespace, indistinguishable
   * from a rendering failure and offering nothing to press or read. Rendering the cell
   * through a template is what lets the absent state carry a visible mark and an
   * announced description without rewriting the value the contract published.
   *
   * Resolved statically for the same reason as {@link commandCell}, and requires
   * `<ng-template #aliasHostName>` at the template's TOP LEVEL.
   */
  @ViewChild('aliasHostName', { static: true })
  protected hostNameCell?: TemplateRef<DataTableCellContext<PortalAlias>>;

  // ---------------------------------------------------------------------------
  //  PRESENTATION STATE
  // ---------------------------------------------------------------------------

  /** The parsed route parameter. `NaN` when the address carries no usable identifier. */
  private readonly portalIdValue = signal<number>(Number.NaN);

  /** Whether the inline form is on screen. */
  private readonly formVisible = signal<boolean>(false);

  /** Whether the deletion prompt is on screen. */
  private readonly deletePrompt = signal<boolean>(false);

  /** Whether a submit has been attempted, which is when client messages appear. */
  private readonly submitAttempted = signal<boolean>(false);

  /** The client-side field messages currently to show. Refreshed explicitly. */
  private readonly clientAliasMessages = signal<readonly string[]>(NO_MESSAGES);

  /** The command column, once its cell template has been resolved. */
  private readonly commandColumn = signal<DataTableColumn<PortalAlias> | null>(null);

  /**
   * Which command is awaiting an answer.
   *
   * A plain field rather than a signal, deliberately: nothing renders it, and a signal
   * read from inside the failure reaction below would make that reaction depend on it
   * and re-run when it changed.
   */
  private pending: PendingOperation = 'none';

  /**
   * The last failure already announced, held by REFERENCE.
   *
   * The store replaces the whole classified failure object on each failure, so
   * reference inequality is exactly "this is a new failure" - including a second
   * refusal identical in every field to the first, which a value comparison would
   * swallow and which an operator does need to hear about again.
   */
  private announcedFailure: PortalFailure | null = null;

  // ---------------------------------------------------------------------------
  //  THE FORM
  // ---------------------------------------------------------------------------

  /**
   * Reports this screen's unsaved entry to the tracker that guards both ways of leaving it.
   *
   * ⚠ THE ROUTE DECLARES `unsavedChangesGuard` AND THIS SCREEN USED TO REGISTER NOTHING, so the gate
   * was answered by a reflective sweep over this component's fields. That sweep is gone, because it
   * pulled `@angular/forms` into the eagerly loaded bundle for an application whose every form screen
   * is lazily loaded, and this registration is what replaces it. Without it the declaration on
   * `portals/:portalId/aliases` would be inert and a half-typed alias would be discarded silently.
   *
   * ⚠ `loading()` COVERS ALL FOUR REQUESTS THIS SCREEN MAKES, not just the write, and that is the
   * right reading rather than an approximation. It is the store's single alias in-flight flag, raised
   * by the list read as well as by create, update and delete — and during a list read the form is
   * either closed or untouched, so the extra coverage costs nothing and the term does what it is
   * here for: an alias already on its way to the server is not unsaved entry, and prompting about it
   * would ask the operator to confirm discarding work they have already committed.
   *
   * A cancelled editor cannot warn: {@link closeForm} resets the one control this group holds, and a
   * group whose every control is pristine is itself pristine. The grid's own affordances are not a
   * form, so removing a row is untouched by this.
   */
  private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(
    () => this.form.dirty && this.loading() === false,
  );

  /**
   * The inline create and edit form: one typed, non-nullable control.
   *
   * `nonNullable: true` is what makes `value` a `string` rather than `string | null`
   * and makes a reset return to the initial value rather than to null - so the
   * validator below can read `control.value.length` without a null check and a
   * cancelled form returns to empty rather than to a state the model cannot express.
   *
   * MIGRATION 1: three rules where the legacy markup declared none. The framework's
   * required rule reproduces what `EditPortalAlias.ascx.vb:L209` achieved by silently
   * doing nothing; the maximum-length rule reproduces the legacy control's own
   * `MaxLength="255"` attribute for a programmatic write that bypasses the rendered
   * attribute; and the alias rule reproduces the server's shape and its authoritative
   * 200-character limit, measured against the normalised value. The accepted input set
   * is unchanged - every rule already existed on the server - and what changes is that
   * the operator is told.
   */
  protected readonly form = new FormGroup<PortalAliasFormModel>({
    httpAlias: new FormControl<string>('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(ALIAS_ENTRY_MAX_LENGTH),
        httpAliasValidator,
      ],
    }),
  });

  // ---------------------------------------------------------------------------
  //  STATIC WORDING FOR THE TEMPLATE
  // ---------------------------------------------------------------------------

  /** @see HEADING */
  protected readonly heading = HEADING;

  /** @see ADD_ACTION_LABEL */
  protected readonly addActionLabel = ADD_ACTION_LABEL;

  /** @see ALIAS_LABEL */
  protected readonly aliasLabel = ALIAS_LABEL;

  /** @see ALIAS_HELP */
  protected readonly aliasHelp = ALIAS_HELP;

  /** @see CANCEL_LABEL */
  protected readonly cancelLabel = CANCEL_LABEL;

  /** @see DELETE_LABEL */
  protected readonly deleteLabel = DELETE_LABEL;

  /** @see EDIT_LABEL */
  protected readonly editLabel = EDIT_LABEL;

  /**
   * The confirmation body: the platform's question, then the host name it means.
   *
   * ⚠ THE MEASURED DEFECT, AND THIS SCREEN IS WHERE IT BITES HARDEST. The body was the bare
   * sentence "Are You Sure You Wish To Delete This Item?" and named nothing, while the dialog is
   * a real modal that covers the table it was raised from - measured obscuring four alias rows,
   * including the row being destroyed. A portal here holds ten host names differing by a port or
   * a digit: `localhost:4200`, `localhost:4203`, `127.0.0.1:4203`. Confirming which of those is
   * about to be unbound was impossible from the prompt, and unbinding the wrong one takes an
   * address out of service.
   *
   * MIGRATION: the question is the measured global `DeleteItem.Text` value, unchanged. Only the
   * identity is added, and it is added because the legacy screen asked NOTHING at all before
   * deleting - so there is no legacy prompt whose wording this could be diverging from. The
   * confirmation is itself an addition of this migration; naming its target is part of making
   * that addition worth having.
   *
   * Falls back to the bare question when no row is pending, which the template makes unreachable
   * - the dialog renders only while a removal is pending.
   */
  protected readonly deleteConfirmMessage = computed<string>(() => {
    const target = this.selectedAlias();

    return target === null
      ? DELETE_CONFIRM_MESSAGE
      : `${DELETE_CONFIRM_MESSAGE} ${target.httpAlias}`;
  });

  /** @see EMPTY_MESSAGE */
  protected readonly emptyMessage = EMPTY_MESSAGE;

  /** @see UNUSABLE_ROUTE_MESSAGE */
  protected readonly unusableRouteMessage = UNUSABLE_ROUTE_MESSAGE;

  /** @see ALIAS_ENTRY_MAX_LENGTH */
  protected readonly aliasInputMaxLength = ALIAS_ENTRY_MAX_LENGTH;

  /** @see ALIAS_CONTROL_ELEMENT_ID */
  protected readonly aliasControlId = ALIAS_CONTROL_ELEMENT_ID;

  /** @see ABSENT_HOST_NAME_DESCRIPTION */
  protected readonly absentHostNameDescription = ABSENT_HOST_NAME_DESCRIPTION;

  /** @see CURRENT_ALIAS_ROW_NOTE */
  protected readonly currentAliasRowNote = CURRENT_ALIAS_ROW_NOTE;


  // ---------------------------------------------------------------------------
  //  DERIVED VIEWS
  // ---------------------------------------------------------------------------

  /** Whether the address carries a usable portal identifier. */
  protected readonly routeUsable = computed<boolean>(
    () => Number.isNaN(this.portalIdValue()) === false,
  );

  /**
   * The address of this portal's configuration screen, or `null` when the route named no portal.
   *
   * ⚠ THE WAY BACK, AND IT DID NOT EXIST. This screen was reachable by typing an address and by
   * nothing else, and it offered no route onwards or back either — so an operator who arrived
   * here left the portal feature entirely and came in again through the listing. The settings
   * screen now links here and this links there, which makes the pair navigable in both
   * directions from one place.
   *
   * MIGRATION: `SiteSettings.ascx.vb:L484-L489` inspects the referring address specifically to
   * recognise arrival FROM the Portal Aliases module, which is direct evidence that the legacy
   * console had operators moving between these two destinations. It reached them through its
   * administration menu; this console's rail carries collection entries only, so the movement
   * lives in the screens.
   *
   * An array rather than an interpolated string, so the router composes the segments: `0` and
   * `-1` are both real tenants.
   */
  protected readonly settingsLink = computed<(string | number)[] | null>(() => {
    const target = this.portalIdValue();

    return Number.isNaN(target) ? null : ['/portals', target, 'settings'];
  });

  /** The wording of that link: `ControlTitle_.Text` in `SiteSettings.ascx.resx`. */
  protected readonly settingsLinkLabel: string = SETTINGS_LINK_LABEL;

  /**
   * The rows to render: the store's collection, but ONLY when it belongs to the
   * portal this screen is showing.
   *
   * The store is application-scoped, so the collection in hand may have been read for
   * a different portal - during a navigation between two portals it certainly has. The
   * store publishes which portal it belongs to for exactly this test, and rendering
   * another tenant's host names, even for one frame, is the kind of leak the exact-match
   * resolution change described above exists to prevent.
   *
   * Falls back to ONE frozen empty array rather than a fresh literal, so an unchanged
   * empty state does not look like a change to the grid's input setter.
   */
  protected readonly rows = computed<readonly PortalAlias[]>(() => {
    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return NO_ALIASES;
    }

    if (this.store.aliasesPortalId() !== target) {
      return NO_ALIASES;
    }

    const held = this.store.aliases();

    return held === null ? NO_ALIASES : held;
  });

  /** Whether an alias read or write is in flight. */
  protected readonly loading = computed<boolean>(() => this.store.aliasLoading());

  /**
   * Whether the collection has been read FOR THIS PORTAL.
   *
   * `false` while the collection is unread and `true` for a collection that is read and
   * empty, which is the distinction that stops the empty state appearing during a
   * request.
   */
  protected readonly listReady = computed<boolean>(() => {
    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return false;
    }

    return this.store.aliasesPortalId() === target && this.store.aliases() !== null;
  });

  /** Whether the portal has been read and has no host names at all. */
  protected readonly isListEmpty = computed<boolean>(
    () => this.listReady() && this.rows().length === 0,
  );

  /**
   * The columns of the listing.
   *
   * TWO, reproducing the legacy grid exactly: a command column and the host-name
   * column, in that order (`Website/admin/Portal/portalalias.ascx:L5-L17`).
   *
   * The command column appears only once its cell template has been resolved, which
   * happens during view creation, so in practice both are present from the first
   * render. Building it conditionally rather than asserting the template is what keeps
   * this file free of a definite-assignment claim the compiler cannot verify.
   *
   * NEITHER COLUMN IS SORTABLE, and that is measured rather than assumed: the legacy
   * grid declares no `AllowSorting`, binds a plain unpaged collection
   * (`PortalAlias.ascx.vb:L42`) and offers no ordering control anywhere. No ordering is
   * requested, no ordering is held and the grid's ordering inputs are left unbound.
   */
  protected readonly columns = computed<readonly DataTableColumn<PortalAlias>[]>(() => {
    // MIGRATION: the heading text comes from the resource key `HTTP Alias.Header`
    // rather than from the markup's `HeaderText` attribute, because
    // `Localization.LocalizeDataGrid` (`PortalAlias.ascx.vb:L69`) replaced the
    // attribute at run time. The key is distinct from the label, so the grid's
    // per-column identity is not the text an operator reads.
    //
    // Pf-M1: rendered through a TEMPLATE rather than bound straight to the field, so an
    // absent or empty host name reaches the operator as a mark and a description instead
    // of as an empty cell. The field form is kept as the fallback for the one frame in
    // which the view query has not resolved, so the column never disappears and the
    // grid never renders a single column - which is the failure mode a missing template
    // would otherwise produce silently.
    //
    const hostNameTemplate = this.hostNameCell;

    const aliasColumn: DataTableColumn<PortalAlias> =
      hostNameTemplate === undefined
        ? {
            key: ALIAS_CONTROL_NAME,
            rowHeader: true,
            label: ALIAS_COLUMN_HEADING,
            field: 'httpAlias',
            sortable: false,
          }
        : {
            key: ALIAS_CONTROL_NAME,
            rowHeader: true,
            label: ALIAS_COLUMN_HEADING,
            kind: 'template',
            cellTemplate: hostNameTemplate,
            sortable: false,
          };

    const commands = this.commandColumn();

    return commands === null ? [aliasColumn] : [commands, aliasColumn];
  });

  /** Whether the inline form is on screen. */
  protected readonly formOpen = computed<boolean>(() => this.formVisible());

  /**
   * Whether the form is editing an existing row rather than adding a new one.
   *
   * MIGRATION: the successor of the legacy mode test. `EditPortalAlias.ascx.vb:L218`
   * branched on `If Not ViewState("PortalAliasID") Is Nothing Then` - an explicit
   * absence test, because a row numbered nought is real and would not survive a
   * truthiness test. The store's alias selection is that page-state entry's successor
   * and it uses `undefined` for absence for the same reason.
   */
  protected readonly editing = computed<boolean>(
    () => this.store.selectedAliasId() !== undefined,
  );

  /**
   * The row the form is open on, or null when it is open to add one.
   *
   * Resolved from the GUARDED row set rather than from the store's collection, so a row
   * belonging to a different portal can never be the answer for the same reason
   * {@link rows} states. Null therefore covers three distinct situations - nothing is
   * selected, the selection names a row this portal does not hold, and the collection
   * has not been read - and every one of them means "no row to reason about".
   */
  private readonly selectedAlias = computed<PortalAlias | null>(() => {
    const chosen = this.store.selectedAliasId();

    if (chosen === undefined) {
      return null;
    }

    const found = this.rows().find((alias) => alias.portalAliasId === chosen);

    return found === undefined ? null : found;
  });

  /**
   * Whether the row the form is open on is the one this request arrived through.
   *
   * MIGRATION 17: FAIL CLOSED. Absence of a row answers FALSE, because the add path has
   * no row to be current and a create is never refused on these grounds - the alias
   * being bound does not exist yet, so resolution cannot have used it. That is a
   * decided answer rather than a default, and it is the only situation in which "no
   * row" and "not current" mean the same thing on this screen.
   */
  protected readonly editingCurrentAlias = computed<boolean>(() => {
    const chosen = this.selectedAlias();

    return chosen !== null && chosen.isCurrent;
  });

  /**
   * The submit command's label, chosen by mode.
   *
   * ONE control with TWO labels, exactly as the legacy screen had it: the markup
   * declares a single `cmdUpdate` (`editportalalias.ascx:L11`) whose text the handler
   * set from the resource key `cmdAdd` on both add branches
   * (`EditPortalAlias.ascx.vb:L83` and L88) and from `cmdUpdate` on the edit branch
   * (L75). There is no separate add control to reproduce.
   */
  protected readonly submitLabel = computed<string>(() =>
    this.editing() ? UPDATE_SUBMIT_LABEL : ADD_SUBMIT_LABEL,
  );

  /**
   * Whether the delete command is offered.
   *
   * MIGRATION 6: THREE CONDITIONS, and only one of them is the legacy count rule.
   *
   * The legacy rule is the count: `SetDeleteVisibility` read the portal's aliases and
   * hid the button when there were not more than one -
   * `EditPortalAlias.ascx.vb:L107` reads `If colPortalAlias.Count <= 1 Then
   * cmdDelete.Visible = False`, so nought and one both hid it. It is reproduced here
   * from the collection this screen already holds, which needs no second request, and
   * it is measured on the GUARDED row set so it counts this portal's aliases and not
   * whichever collection the store last read.
   *
   * THE SERVER STILL DOES NOT ENFORCE THE COUNT RULE, and that is worth keeping
   * straight now that it enforces a different one. Unbinding a portal's LAST host name
   * is answered normally - the service declares no such refusal and an integration test
   * asserts the permission deliberately, on the ground that inventing a rule the legacy
   * console did not have would refuse a save an operator could previously make. So this
   * first condition remains an AFFORDANCE rather than a constraint, and it is the only
   * thing standing between an operator and a portal with no way to reach it.
   *
   * The second condition is a DELIBERATE DIVERGENCE. Legacy called
   * `SetDeleteVisibility` on the add paths too (L81 and L86), so a brand-new unsaved
   * alias showed a Delete button whenever the portal already had two or more - a button
   * that acted on `ViewState("PortalAliasID")`, which on that path was absent. Here the
   * command is offered only for a row that exists.
   *
   * MIGRATION 17: THE THIRD CONDITION IS THE ONE THE SERVER DOES ENFORCE. The row this
   * request arrived through offers no delete command, and a call that reaches the
   * endpoint anyway is refused with `409` and the active-alias code. This is the
   * stronger of the two removal rules and it is worth seeing why they are different: the
   * count rule guards a portal from having no address at all, which a host-level
   * operator can repair from another tenant; this one guards the address the operator is
   * STANDING ON, whose loss leaves them no route back to the screen that would repair
   * it. Only the second has no in-application recovery, which is why only the second is
   * refused server-side.
   */
  protected readonly deleteAffordanceVisible = computed<boolean>(() => {
    if (this.editing() === false) {
      return false;
    }

    if (this.editingCurrentAlias()) {
      return false;
    }

    return this.rows().length > 1;
  });

  /** Whether the deletion prompt is on screen. */
  protected readonly confirmingDelete = computed<boolean>(() => this.deletePrompt());

  /**
   * The server's message for the alias field, or null when it has none.
   *
   * Two sources, in precedence order:
   *
   *   * a state refusal, which for these endpoints can only be the duplicate host
   *     name. Presented in THIS screen's measured wording - see
   *     {@link DUPLICATE_ALIAS_MESSAGE} for why that differs from the shared
   *     vocabulary's wording, and note that no refusal-code string is written in this
   *     file: the classification arrives already made.
   *   * a per-field validation message, matched by control name through the shared
   *     reader, which folds case and tolerates the prefixes and dotted paths a model
   *     binder produces. The dictionary is read there and never here, so no member
   *     access on an index signature appears in this file.
   *
   * MIGRATION 2: the legacy update path wrapped its write in a BARE, BROAD `Catch`
   * with no exception variable and no filter (`EditPortalAlias.ascx.vb:L223-L228`) and
   * announced a duplicate host name for ANY failure it caught - a lost connection, a
   * permission refusal, a timeout, all reported as a name collision. Here only an
   * actual refusal produces that message, and every other failure surfaces as its own
   * problem document with the server's own wording.
   */
  protected readonly serverAliasMessage = computed<string | null>(() => {
    const failure = this.store.aliasFailure();

    if (failure === null) {
      return null;
    }

    if (isDuplicateRefusal(failure)) {
      return DUPLICATE_ALIAS_MESSAGE;
    }

    // MIGRATION 17: the second state refusal these endpoints can answer. Shown beside
    // the control rather than in the banner because the operator's next action is on
    // this very field - or on the row they chose - and because the screen's rule is one
    // message per outcome.
    if (isActiveAliasRefusal(failure)) {
      return CURRENT_ALIAS_MESSAGE;
    }

    return fieldErrorMessage(failure.problem, ALIAS_CONTROL_NAME);
  });

  /**
   * Every message to show beside the alias control: this screen's own rules first,
   * then the server's.
   *
   * A computed over two signals, so its result changes only when one of them does -
   * which matters because the shared form field takes its messages through a setter
   * that writes a signal, and a fresh array on every change-detection pass would
   * notify it on every pass.
   */
  protected readonly aliasError = computed<readonly string[]>(() => {
    const client = this.clientAliasMessages();
    const server = this.serverAliasMessage();

    if (server === null) {
      return client;
    }

    return client.length === 0 ? [server] : [...client, server];
  });

  /**
   * The problem document to render in the banner, or null when there is no failure.
   *
   * ⚠ EVERY FAILURE REACHES THE BANNER, AND AN EARLIER REVISION SUPPRESSED SOME OF
   * THEM. That revision withheld the banner whenever the failure had already produced
   * a message beside the alias control - the duplicate host name and the active-alias
   * refusal - on the grounds that the legacy screen showed exactly one message per
   * outcome. The consequence was measured rather than theoretical: on a real duplicate
   * rejection the screen showed its own short sentence and DISCARDED both the server's
   * `detail` and its correlation identifier, so the one refusal an operator is most
   * likely to hit was also the only one they could not report. The server's sentence
   * for that case is more specific than this screen's wording, naming the host name it
   * refused, and the reference is the handle a support conversation is conducted
   * through.
   *
   * THE TWO SURFACES ARE COMPLEMENTARY, NOT CONTRADICTORY, which is the distinction
   * the earlier reasoning missed. The field message attributes the refusal to the
   * control the operator must change; the banner states the occurrence and carries the
   * reference. They never disagree, because both are derived from the same failure -
   * and they do not repeat each other either, since the field carries this screen's own
   * resource wording while the banner carries the server's sentence. This is also what
   * every other screen in the application already does: each renders its banner from
   * `failure.problem` unconditionally and renders field messages beside its controls at
   * the same time, so the suppression made this ONE screen the outlier.
   *
   * The legacy precedent supports the banner rather than opposing it: the legacy
   * duplicate-alias message was announced through the module message renderer - a
   * banner - and had no field-level surface at all, because the accessibility contract
   * that puts a message beside a control is net-new here. Rendering both keeps the
   * legacy surface AND the added one.
   *
   * The banner classifies severity from the status itself, so a permission refusal is
   * presented as a warning rather than as danger without this screen deciding
   * anything. See MIGRATION 3.
   */
  protected readonly bannerProblem = computed<ProblemDetails | null>(
    () => this.store.aliasFailure()?.problem ?? null,
  );


  // ---------------------------------------------------------------------------
  //  CONSTRUCTION AND LIFECYCLE
  // ---------------------------------------------------------------------------

  /**
   * Registers the one reaction this screen needs.
   *
   * THE ONLY `effect()` IN THIS FILE, and it exists because announcing a permission
   * refusal is a genuine side effect on a queue outside this component, reached from an
   * asynchronous outcome the store reports as state rather than as a callback: the
   * store's write commands take a continuation for success and none for failure. Every
   * other reaction on this screen is a derivation and is written as one.
   *
   * It depends on the failure slice ALONE. The operation that produced the failure is
   * held in a plain field rather than a signal precisely so that reading it here does
   * not make this reaction re-run when it changes.
   */
  public constructor() {
    effect(() => {
      const failure = this.store.aliasFailure();

      if (failure === null) {
        this.announcedFailure = null;

        return;
      }

      if (this.announcedFailure === failure) {
        return;
      }

      this.announcedFailure = failure;
      this.announceRefusal(failure);
    });
  }

  /**
   * Adopts the command cell template and issues the first read if the input setter has
   * not already.
   *
   * The static view query has resolved by now, so the command column is formed once and
   * keeps one reference for the life of the component.
   *
   * The read is guarded rather than unconditional. The router delivers the input before
   * this hook runs, so in a routed screen the setter has already issued it; the guard
   * covers a component constructed directly - in a specification, say - whose input was
   * set to the value the signal already held.
   */
  public ngOnInit(): void {
    const template = this.commandCell;

    if (template !== undefined) {
      this.commandColumn.set({
        key: 'commands',
        // The legacy command column carried no heading text at all, so the heading is
        // hidden rather than invented; the label is still supplied because it is the
        // column's accessible name, and it is the wording the legacy image already
        // carried through `resourcekey="Edit"`.
        label: EDIT_LABEL,
        headerHidden: true,
        // MIGRATION: the legacy `ItemStyle Width="15px"`
        // (`Website/admin/Portal/portalalias.ascx:L6`) SNAPS to the shared grid's
        // narrowest declared track. That contract admits a percentage, a token or an
        // intrinsic keyword and deliberately admits no pixel value, so an exact match
        // is not expressible; the intrinsic keyword is the nearest, and it sizes the
        // column to the command rather than to a fixed measure.
        width: 'min-content',
        kind: 'actions',
        cellTemplate: template,
      });
    }

    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return;
    }

    if (this.listReady() === false && this.loading() === false) {
      this.pending = 'list';
      this.store.loadAliases(target);
    }
  }

  // ---------------------------------------------------------------------------
  //  COMMANDS - THE FORM
  // ---------------------------------------------------------------------------

  /**
   * Opens the form to add a host name.
   *
   * The successor of the legacy module action, which navigated to the separate edit
   * control with the portal in the query string
   * (`Website/admin/Portal/PortalAlias.ascx.vb:L90`). Clearing the store's alias
   * selection is how "adding" is expressed, and it returns the selection to `undefined`
   * rather than to nought, which would name a real row.
   */
  protected startCreate(): void {
    this.store.clearAliasSelection();
    this.store.clearFailures();
    this.resetEntry('');
    this.formVisible.set(true);
    this.focusEntry();
  }

  /**
   * Whether one row offers the edit command.
   *
   * MIGRATION 17: THE LEGACY `IsNotCurrent` RULE IS REPRODUCED.
   * `Website/admin/Portal/PortalAlias.ascx.vb:L51-L60` hid the edit affordance for the
   * alias through which the site was BEING BROWSED, comparing each row's identifier
   * against `Me.PortalAlias.PortalAliasID()` - the current request's own alias, as the
   * page base class had resolved it server-side - and `portalalias.ascx:L8` bound the
   * answer to the hyperlink's visibility.
   *
   * The comparison is the SERVER'S, and that is what changed to make this possible. The
   * alias contract now carries the answer per row, computed for each request from the
   * resolved portal context, so this screen reads a published fact instead of deriving
   * one. It is still deliberately NOT inferred from the browser's own address: the SPA
   * is served through a proxy, so the address in the location bar need not be the host
   * name the API matched, and the legacy write path folded case on insert while its
   * reader did not, so two spellings of one host name are both legitimate stored values.
   * A client-side comparison would need a casing rule of its own and would hide the
   * right row on one deployment and the wrong one on another.
   *
   * @param alias One row of the listing.
   * @returns True when the row may be renamed.
   */
  protected rowEditable(alias: PortalAlias): boolean {
    return alias.isCurrent === false;
  }

  /**
   * Whether one row's stored host name is absent or empty.
   *
   * Pf-M1: the two states are DELIBERATELY answered together, because the display answer
   * for both is the same and the contract still distinguishes them. `null` is a stored
   * SQL null and `''` is a stored empty string; the legacy reader collapsed the first
   * into the second before it ever reached the grid (`PortalAliasController.vb`, through
   * `Convert.ToString`), so no legacy screen ever told them apart either.
   *
   * @param alias One row of the listing.
   * @returns True when there is no host name to paint.
   */
  protected isHostNameAbsent(alias: PortalAlias): boolean {
    return aliasText(alias).length === 0;
  }

  /**
   * The text of one row's host-name cell.
   *
   * Answers the stored value verbatim when there is one - no folding, no trimming, no
   * normalisation, because the operator must see what is stored in order to correct it -
   * and the absent mark when there is not.
   *
   * @param alias One row of the listing.
   * @returns What the cell paints.
   */
  protected hostNameText(alias: PortalAlias): string {
    const held = aliasText(alias);

    return held.length === 0 ? ABSENT_HOST_NAME_MARK : held;
  }

  /**
   * The ACCESSIBLE name of one row's edit command.
   *
   * Pf-M3: qualified with the row, so fifteen commands on one screen no longer reach
   * assistive technology under one indistinguishable name. On a row with no host name
   * the qualifier is the absent-host wording rather than an empty string, which is the
   * only way the two blank rows measured at runtime can be told apart at all.
   *
   * @param alias The row the command acts on.
   * @returns The accessible name.
   */
  protected editCommandLabel(alias: PortalAlias): string {
    const held = aliasText(alias);

    return editCommandName(held.length === 0 ? ABSENT_HOST_NAME_DESCRIPTION : held);
  }

  /**
   * Opens the form to edit one existing host name.
   *
   * MIGRATION 17: REFUSES THE ROW THIS REQUEST ARRIVED THROUGH, and the guard is not
   * redundant with the withheld button. The listing offers TWO ways to reach this
   * command - the per-row button, which is withheld, and pressing the row itself, which
   * is a net addition of this screen and reaches every row - so hiding the button alone
   * would leave the affordance fully available by the other path. The server refuses the
   * write regardless, so the guard exists to keep the screen from opening a form whose
   * only possible outcome is a refusal.
   *
   * The attempt is ANNOUNCED rather than silently dropped. The legacy row had no press
   * behaviour at all, so an empty command cell was the whole of the feedback it needed;
   * here a press that did nothing would read as a broken screen. The announcement names
   * the recovery, which is the same sentence the server's own refusal carries.
   *
   * @param alias The row to edit.
   */
  protected editAlias(alias: PortalAlias): void {
    if (this.rowEditable(alias) === false) {
      this.notifications.warning(CURRENT_ALIAS_MESSAGE);

      return;
    }

    this.store.clearFailures();
    this.store.selectAlias(alias.portalAliasId);
    this.resetEntry(aliasText(alias));
    this.formVisible.set(true);
    this.focusEntry();
  }

  /**
   * Normalises the entry, judges it and, if it stands, writes it.
   *
   * The order is the legacy order and it matters:
   *
   *   1. normalise, reproducing `EditPortalAlias.ascx.vb:L210-L215`, and write the
   *      result back into the control so the operator sees what will be saved;
   *   2. judge the normalised value - see {@link httpAliasValidator};
   *   3. write, choosing create or update from the presence of a selected row exactly
   *      as `EditPortalAlias.ascx.vb:L218` chose its branch.
   *
   * MIGRATION 13: the legacy add branch carried a defect that cannot be reproduced here.
   * At `EditPortalAlias.ascx.vb:L231` it pre-checked for a duplicate by calling
   * `p.GetPortalAlias(strAlias, Convert.ToInt32(ViewState("PortalAliasID")))` - inside
   * the branch reached only BECAUSE that page-state entry was absent, so the conversion
   * necessarily produced NOUGHT, and nought is a real portal under an identity seeded at
   * minus one. The pre-check therefore excluded a row belonging to the WRONG TENANT, and
   * strictness being switched off for the administration pages
   * (`Website/release.config:L125`) is why no compiler said so. Annotated and NOT fixed
   * in place, because it is not reproducible: there is no local pre-check at all here -
   * the server answers a collision with a refusal, which is a question about stored state
   * and belongs to the server.
   *
   * MIGRATION 12: the same page-state entry carried a second defect. On the add-with-
   * portal branch, `ViewState("PortalID")` was set only for a superuser
   * (`EditPortalAlias.ascx.vb:L78-L80`), so for anyone else the later
   * `Convert.ToInt32(ViewState("PortalID"))` at L234 also produced nought and the alias
   * was created against portal nought - again a real tenant. Annotated and not fixed:
   * it is structurally impossible here, because the owning portal comes from the address
   * and the write contract carries no portal member for a second copy to disagree with.
   */
  protected submit(): void {
    const target = this.portalIdValue();

    if (Number.isNaN(target)) {
      return;
    }

    this.submitAttempted.set(true);

    const control = this.form.controls.httpAlias;
    const entry = control.value;

    // ⚠ THE ENTRY IS TRANSMITTED VERBATIM. There is no write-back step here any more: this used to
    // strip a protocol or share prefix, set the stripped value into the control and send that. See
    // {@link httpAliasValidator} for the measurement that removed it - the server refuses both
    // prefixes with `400`, so stripping concealed a refusal and submitted a value the operator had
    // not entered on a field that binds a host to a tenant. What the operator sees is now what is
    // judged and what is sent, and a prefix is refused with the message this screen already shows.
    control.markAsTouched();
    this.refreshAliasMessages();

    if (this.form.invalid) {
      return;
    }

    this.store.clearFailures();

    const chosen = this.store.selectedAliasId();

    if (chosen === undefined) {
      this.pending = 'create';

      const request: CreatePortalAliasRequest = { httpAlias: entry };

      this.store
        .createAlias(target, request)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          this.onSaved();
        });

      return;
    }

    this.pending = 'update';

    const request: UpdatePortalAliasRequest = { httpAlias: entry };

    this.store
      .updateAlias(target, chosen, request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.onSaved();
      });
  }

  /**
   * Abandons the form.
   *
   * MIGRATION: the legacy cancel command redirected to the stored referrer
   * (`EditPortalAlias.ascx.vb:L158-L160`), and to the empty string when there was none.
   * Here the form is inline, so cancelling closes it and restores the pristine listing
   * without leaving the screen.
   *
   * IT MUST NOT JUDGE THE ENTRY. The legacy cancel control declared
   * `causesvalidation="False"` (`editportalalias.ascx:L12`), so the reset below returns
   * the control to its initial value and clears the attempt marker rather than reporting
   * whatever was half-typed.
   */
  protected cancelEdit(): void {
    this.store.clearFailures();
    this.closeForm();
  }

  // ---------------------------------------------------------------------------
  //  COMMANDS - DELETION
  // ---------------------------------------------------------------------------

  /**
   * Asks before removing the selected host name.
   *
   * MIGRATION 14: THE PROMPT IS AN ADDITION. The legacy delete command carried no
   * client-side confirmation of any kind - `editportalalias.ascx:L13` declares no
   * `OnClientClick` - and the handler removed the row on the first click
   * (`EditPortalAlias.ascx.vb:L187`). The shared dialogue supplies the platform's own
   * confirmation wording, a focus trap and dismissal on `Escape`, so an irreversible
   * command now takes two deliberate actions. Recorded as a deliberate safety
   * affordance rather than presented as parity.
   *
   * Like the legacy command it declares `causesvalidation="False"`
   * (`editportalalias.ascx:L13`), so it neither judges nor reports the entry.
   */
  protected requestDelete(): void {
    if (this.deleteAffordanceVisible() === false) {
      return;
    }

    this.deletePrompt.set(true);
  }

  /** Dismisses the prompt without removing anything. */
  protected cancelDelete(): void {
    this.deletePrompt.set(false);
  }

  /**
   * Removes the selected host name.
   *
   * The store removes the row from the collection in hand rather than re-reading, which
   * is exact for an unpaged collection, and clears the selection when the removed row
   * was the selected one.
   */
  protected confirmDelete(): void {
    this.deletePrompt.set(false);

    const target = this.portalIdValue();
    const chosen = this.store.selectedAliasId();

    if (Number.isNaN(target) || chosen === undefined) {
      return;
    }

    // MIGRATION 17: re-tested at the moment of the write rather than trusted from the
    // moment the prompt opened. The prompt is a second deliberate action, and the row
    // set can be re-read between the two - by a concurrent navigation, or by the
    // operator's own address changing - so a check made only at `requestDelete` would
    // act on a fact that had since stopped being true.
    if (this.editingCurrentAlias()) {
      this.notifications.warning(CURRENT_ALIAS_MESSAGE);

      return;
    }

    this.pending = 'delete';
    this.store.clearFailures();

    this.store
      .deleteAlias(target, chosen)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.onDeleted();
      });
  }

  // ---------------------------------------------------------------------------
  //  COMMANDS - FIELD FEEDBACK
  // ---------------------------------------------------------------------------

  /**
   * Refreshes the field messages after a keystroke.
   *
   * Bound from the template alongside the control binding, so validity changes reach a
   * signal at the moment they happen. Reactive-forms validity is not itself reactive, so
   * a derivation cannot observe it, and a template-invoked reader would hand the shared
   * form field a fresh array on every change-detection pass.
   */
  protected onAliasInput(): void {
    this.refreshAliasMessages();
  }

  /**
   * Refreshes the field messages when the control loses focus.
   *
   * Messages appear once the control has been visited or a submit has been attempted,
   * so an operator is not told a field is required before they have reached it.
   */
  protected onAliasBlur(): void {
    this.refreshAliasMessages();
  }

  // ---------------------------------------------------------------------------
  //  PRIVATE
  // ---------------------------------------------------------------------------

  /**
   * Completes a successful create or update.
   *
   * MIGRATION 15: the legacy success message was effectively UNREACHABLE. The handler
   * added it at `EditPortalAlias.ascx.vb:L242` and then issued
   * `Response.Redirect(…, True)` on the very next line, which ends the response, so the
   * message was rendered into a page nobody saw. Here the measured wording is announced
   * and the listing is refreshed in place - the store appends the created row to the
   * collection in hand and re-reads the collection after an update, because that write
   * is answered with no body and the stored value must not be guessed from the request.
   */
  private onSaved(): void {
    // ⚠ THE FAILURE SURFACE IS CLEARED HERE BECAUSE SUCCESS AND FAILURE FOR ONE ACTION ARE
    // MUTUALLY EXCLUSIVE, and this screen was observed asserting both at once. A double press
    // produced two writes whose answers disagreed — `409` on the first arm, `201` on the
    // second — and because this method only ADDED a success notification, the operator was
    // left facing a green "the Portal Alias has been saved" notice and a red Conflict banner
    // describing the same single action, with nothing on screen to say which had prevailed.
    //
    // The double press itself is stopped upstream by `SubmitGuardDirective`, so the two-arm
    // race that exposed this is gone. This line is NOT therefore redundant: it makes the two
    // surfaces exclusive BY CONSTRUCTION rather than by the absence of concurrency, so no
    // future overlap — a retry, a second screen, a slow arm answering late — can reinstate a
    // contradiction. `submit` already clears failures on the way IN; this closes the pair on
    // the way out.
    this.store.clearFailures();

    this.notifications.success(SAVED_MESSAGE);

    // ⚠ THE REMEMBERED INVOKER IS DISCARDED ON THIS PATH, AND THAT IS THE POINT. A save re-reads
    // the collection, so the row that was edited is re-rendered and the element that opened the
    // form is replaced. Focusing it would appear to work at the moment the form closes and then be
    // undone by the response that arrives afterwards - measured, focus ended on `document.body`,
    // which is exactly the outcome the restoration exists to prevent. The guard has to be anchored
    // to what CAUSES the loss, and on this path the cause is known in advance: the row is going to
    // be replaced, so the invoker is not a valid target and the header's create action is.
    this.discardFormInvoker();
    this.closeForm();
  }

  /**
   * Completes a successful delete. @see DELETED_MESSAGE
   *
   * ⚠ ANNOUNCED AS A SUCCESS, NOT AS INFORMATION, AND THE CHANGE IS FOR CONSISTENCY.
   * This was the ONLY completed mutation in the console announced at the informational
   * severity. Every other one - including the two other deletions, the portal deletion on
   * the listing screen and the settings deletion - uses the success severity, and this
   * screen's own save does too. One operation dressed differently from its siblings reads
   * as a different KIND of outcome: the informational band is the quiet band, reserved for
   * something that is neither a completed action nor a refusal, and a removal the operator
   * asked for and the server performed is neither of those. It is a completed action.
   *
   * The severity is not cosmetic - it selects the band, and the bands differ in surface as
   * well as in the word they state - so leaving this one at the quiet band made a successful
   * deletion look less conclusive than a successful save of the same record.
   */
  private onDeleted(): void {
    this.notifications.success(DELETED_MESSAGE);

    // The row the operator was standing on no longer exists, so there is nothing to hand focus back
    // to. See `discardFormInvoker`.
    this.discardFormInvoker();
    this.closeForm();
  }

  /**
   * Returns the form to its closed, pristine state.
   *
   * The store's alias selection is cleared as well, so the next opening of the form
   * starts in add mode rather than inheriting whichever row was last selected.
   */
  private closeForm(): void {
    this.formVisible.set(false);
    this.deletePrompt.set(false);
    this.store.clearAliasSelection();
    this.resetEntry('');
    this.pending = 'none';
    this.restoreInvokerFocus();
  }

  /**
   * Remembers what is focused now, then moves focus into the form's entry box.
   *
   * ⚠ TWO MEASURED DEFECTS, ONE CAUSE: NOTHING MANAGED FOCUS ACROSS THIS FORM AT ALL. Opening the
   * editor left focus on the control that opened it, so a keyboard operator pressed Edit, was
   * shown a form, and then had to tab forwards through the REST OF THE TABLE to reach it -
   * measured at twenty-two stops, because the form renders after the whole grid. On closing, focus
   * was dropped to `document.body`, so the next Tab restarted from the top of the document and the
   * operator lost their place in the listing entirely.
   *
   * The deferral is unavoidable rather than stylistic. The form lives in a conditional block, so
   * the box does not exist in the document at the moment the command that opens it runs; the view
   * query resolves during the render that creates it. `afterNextRender` is the point at which the
   * element is both present and laid out.
   *
   * `preventScroll` is deliberately NOT passed here. Unlike a dismissal - where the page must not
   * jump under a reader who has just closed something - the point of this move is to bring the
   * operator to a control they are about to type into, so scrolling it into view is the correct
   * behaviour rather than a side effect.
   */
  private focusEntry(): void {
    this.formInvoker =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;

    afterNextRender(
      () => {
        // Re-read rather than captured: the query resolves during the render this callback follows,
        // and a form closed again before the callback ran would leave it unresolved. A presence
        // test, never a truthiness test on a node, and no non-null assertion.
        const box: ElementRef<HTMLInputElement> | undefined = this.entryBox;

        if (box === undefined || !box.nativeElement.isConnected) {
          return;
        }

        box.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  /**
   * Hands focus back to whatever opened the form, if it is still there to take it.
   *
   * ⚠ THE INVOKER IS NOT ALWAYS STILL THERE, and that is why this is not a bare `focus()` call.
   * Cancelling leaves the listing untouched, so the row's Edit command is still connected and
   * receives focus back - the ordinary case. A SAVE re-reads the collection, so the row that was
   * edited is replaced by a new element and the remembered one is detached: focusing a detached
   * element is a silent no-op that leaves focus on `body`, which is the very outcome this exists
   * to prevent. A REMOVAL destroys the row outright.
   *
   * So the fallback is the create action in the page header, which is present on every state of
   * this screen and sits immediately before the listing in the tab order - close to where the
   * operator was, and never detached. Resolved by query rather than held as a second view child,
   * because it lives in projected content.
   *
   * Deferred for the same reason as {@link focusEntry}: the invoker's fate is decided by the
   * render that follows the close, not by the signal write that requests it.
   *
   * `preventScroll` IS passed here. A dismissal must not move the page under the reader - measured
   * on the notification surface at a seventeen-pixel jump before it was passed there - and the
   * element being restored is one the operator was already at.
   */
  /**
   * Forgets the control that opened the form, so the close falls back rather than restoring.
   *
   * Used on the paths that DESTROY or REPLACE the invoker - a save, which re-reads the collection,
   * and a removal, which drops the row outright. See {@link restoreInvokerFocus} for why focusing a
   * detached element is worse than not trying: it is a silent no-op that leaves focus on the
   * document body.
   */
  private discardFormInvoker(): void {
    this.formInvoker = null;
  }

  private restoreInvokerFocus(): void {
    const remembered: HTMLElement | null = this.formInvoker;

    this.formInvoker = null;

    afterNextRender(
      () => {
        if (remembered !== null && remembered.isConnected) {
          remembered.focus({ preventScroll: true });

          return;
        }

        const fallback: HTMLElement | null = this.host.nativeElement.querySelector<HTMLElement>(
          'app-page-header button',
        );

        fallback?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /**
   * Puts one value into the control and returns it to a pristine, unreported state.
   *
   * `reset` with an explicit value rather than a bare `reset`, which would return the
   * non-nullable control to its INITIAL value - the empty string - and would therefore
   * discard the row being edited.
   *
   * @param value The value to hold.
   */
  private resetEntry(value: string): void {
    this.submitAttempted.set(false);
    this.form.controls.httpAlias.reset(value);
    this.clientAliasMessages.set(NO_MESSAGES);
  }

  /**
   * Recomputes which of this screen's own rules to report.
   *
   * Nothing is reported until the control has been visited or a submit attempted, and at
   * most ONE message is reported at a time: the rules form a precedence chain -
   * emptiness, then the entry cap, then the transmitted length, then the shape - and
   * stating that a value is both blank and malformed says nothing useful twice.
   *
   * The framework's error bag is interrogated through its own predicate rather than by
   * reading the bag as a dictionary, so no member access on an index signature appears
   * here.
   */
  private refreshAliasMessages(): void {
    const control = this.form.controls.httpAlias;

    // ⚠ `causesvalidation="False"`, STATED AS A RULE BECAUSE IT STOPPED BEING FREE. The legacy
    // delete command declared it (`editportalalias.ascx:L13`), so a removal neither judges nor
    // reports the entry - the value is about to cease to exist. That used to hold by accident:
    // nothing ever focused the entry box, so no blur reached this method and the control stayed
    // untouched.
    //
    // Focusing the box on open changed that, and the guard has to be anchored to what CAUSES the
    // report rather than to the command that leads to it. Pressing Delete only opens the prompt;
    // the blur arrives one render later, when the confirmation opens MODALLY and the platform moves
    // focus onto its Cancel button. The reactive-forms accessor marks the control touched from that
    // blur and the gate below opens - so an operator asking to destroy a row was told the box they
    // had never typed in was required. Clearing inside the command was measured and was too early
    // for exactly this reason.
    //
    // While a removal is pending, this screen reports nothing about the entry.
    if (this.deletePrompt()) {
      this.clientAliasMessages.set(NO_MESSAGES);

      return;
    }

    if (this.submitAttempted() === false && control.touched === false) {
      this.clientAliasMessages.set(NO_MESSAGES);

      return;
    }

    if (control.hasError('required') || control.hasError(ALIAS_BLANK_ERROR)) {
      this.clientAliasMessages.set([ALIAS_REQUIRED_MESSAGE]);

      return;
    }

    if (control.hasError('maxlength')) {
      this.clientAliasMessages.set([ALIAS_ENTRY_TOO_LONG_MESSAGE]);

      return;
    }

    if (control.hasError(ALIAS_TOO_LONG_ERROR)) {
      this.clientAliasMessages.set([ALIAS_TOO_LONG_MESSAGE]);

      return;
    }

    // Both sentences when both rules refused the entry, in the order the server declares them, so
    // the operator reads the general refusal and then the specific reason for it.
    const shapeMessages: string[] = [];

    if (control.hasError(ALIAS_INVALID_ERROR)) {
      shapeMessages.push(ALIAS_INVALID_MESSAGE);
    }

    if (control.hasError(ALIAS_UNSUPPORTED_PATH_ERROR)) {
      shapeMessages.push(ALIAS_UNSUPPORTED_PATH_MESSAGE);
    }

    if (shapeMessages.length > 0) {
      this.clientAliasMessages.set(shapeMessages);

      return;
    }

    this.clientAliasMessages.set(NO_MESSAGES);
  }

  /**
   * Announces a permission refusal in the words the legacy screen used for the command
   * that was refused.
   *
   * MIGRATION 11: THERE ARE TWO DENIAL SENTENCES, not one, and they have different
   * provenance. The read denial is a hard-coded English literal in the legacy
   * code-behind at `EditPortalAlias.ascx.vb:L67` with no resource entry anywhere; the
   * delete denial is the resource key `AccessDenied` read at L183. Both are carried, and
   * both are announced at WARNING severity for the reason recorded on the class.
   *
   * A refusal of a create or an update is NOT worded here, and the omission is measured:
   * the legacy save handler declared no permission guard at all
   * (`EditPortalAlias.ascx.vb:L206-L248`), so there is no legacy sentence to reproduce.
   * Such a refusal reaches the operator through the banner instead, carrying the server's
   * own wording, which is better than inventing one.
   *
   * Nothing but a permission refusal is announced here. A state refusal is shown beside
   * the control it concerns, and every other failure is shown in the banner; announcing
   * any of them a second time on the queue would report one outcome twice.
   *
   * @param failure The classified failure the store published.
   */
  private announceRefusal(failure: PortalFailure): void {
    if (failure.status !== FORBIDDEN_STATUS) {
      return;
    }

    switch (this.pending) {
      case 'delete':
        this.notifications.warning(DELETE_DENIED_MESSAGE);
        break;
      case 'list':
        this.notifications.warning(VIEW_DENIED_MESSAGE);
        break;
      case 'create':
      case 'update':
      case 'none':
        break;
      default:
        break;
    }
  }
  /**
   * How a row identifies itself to the shared grid, so a re-read of the page already shown reuses its row
   * elements instead of rebuilding them.
   *
   * ⚠ THE DATABASE KEY, NOT THE ARRAY POSITION AND NOT THE OBJECT. The grid's own fallback is the row
   * OBJECT, which is a correct key only while the same objects stay in play; every read from the server
   * decodes fresh objects, so without this a refetch of the same page presents entirely new keys and the
   * whole body is rebuilt to display records that never changed. `portalAliasId` is unique by definition, being
   * the record's own identifier, which is what `@for` requires - a repeated key is an error there.
   *
   * Declared as a bound field rather than an inline arrow so the reference is stable across change
   * detection; a new function each redraw would set the grid's input every time and defeat its purpose.
   *
   * @param row The row about to be rendered.
   * @returns The record's identifier.
   */
  protected readonly aliasRowKey = (row: PortalAlias): number => row.portalAliasId;

}
