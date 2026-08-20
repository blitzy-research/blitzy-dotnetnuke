/**
 * ⚠ THE STATIC DUPLICATE-METADATA CHECK, AND THE DEFECT THAT MADE IT NECESSARY.
 *
 * `FocusFirstInvalidDirective` was listed TWICE in the `imports` array of every one of the
 * seventeen standalone components that use it, and `RouterLink` was listed twice on the portal
 * settings screen — eighteen duplicated entries across the workspace. Nothing reported them.
 * Angular resolves a duplicated dependency to the same directive definition and renders
 * identically, the production build emits no diagnostic, and there is no linter configured in
 * this workspace, so the only thing standing between a merge artefact of this shape and the
 * delivered code was a reader noticing it in a fifteen-line array.
 *
 * A duplicate is not merely untidy. It states that a component has two reasons to depend on one
 * symbol, so a later author removing one usage removes one entry and leaves the other, and the
 * array stops describing the template. That is precisely how these arose: the entry was added
 * once at the head of the array and once at its tail, in two separate revisions, and neither
 * author saw the other's.
 *
 * WHAT THIS READS, AND WHY IT CAN. Angular's compiled component definition exposes the
 * `dependencies` the `imports` array declared, as a factory returning the list VERBATIM —
 * duplicates included, in source order, with no de-duplication. That is what makes the check
 * possible from a specification rather than from a lint rule: the assertion reads the same list
 * the compiler read.
 *
 * WHY THE CLASSES ARE ENUMERATED BY HAND. The enumeration IS half the value. A component absent
 * from the list below is visible as an omission to anybody reading it, whereas a reflective sweep
 * over the source tree would silently cover whatever happened to exist and would quietly stop
 * covering a file that moved. The list is also the workspace's own inventory of standalone
 * declarables, which nothing else states in one place.
 */
import { AppComponent } from './app.component';
import { LoginComponent } from './features/auth/login/login.component';
import { ModuleExportComponent } from './features/module/module-export/module-export.component';
import { ModuleFormComponent } from './features/module/module-form/module-form.component';
import { ModuleImportComponent } from './features/module/module-import/module-import.component';
import { ModuleListComponent } from './features/module/module-list/module-list.component';
import { ModuleSettingsComponent } from './features/module/module-settings/module-settings.component';
import { NotFoundComponent } from './features/not-found/not-found.component';
import { PortalAliasListComponent } from './features/portal/portal-alias-list/portal-alias-list.component';
import { PortalFormComponent } from './features/portal/portal-form/portal-form.component';
import { PortalListComponent } from './features/portal/portal-list/portal-list.component';
import { PortalSettingsComponent } from './features/portal/portal-settings/portal-settings.component';
import { RoleAssignmentComponent } from './features/role/role-assignment/role-assignment.component';
import { RoleFormComponent } from './features/role/role-form/role-form.component';
import { RoleGroupFormComponent } from './features/role/role-group-form/role-group-form.component';
import { RoleListComponent } from './features/role/role-list/role-list.component';
import { MembershipSettingsComponent } from './features/user/membership-settings/membership-settings.component';
import { ProfileDefinitionListComponent } from './features/user/profile-definition-list/profile-definition-list.component';
import { UserFormComponent } from './features/user/user-form/user-form.component';
import { UserListComponent } from './features/user/user-list/user-list.component';
import { UserPasswordComponent } from './features/user/user-password/user-password.component';
import { UserProfileComponent } from './features/user/user-profile/user-profile.component';
import { FooterComponent } from './layout/footer/footer.component';
import { HeaderComponent } from './layout/header/header.component';
import { NotificationListComponent } from './layout/notifications/notification-list.component';
import { ShellComponent } from './layout/shell/shell.component';
import { SidebarComponent } from './layout/sidebar/sidebar.component';
import { ConfirmDialogComponent } from './shared/components/confirm-dialog/confirm-dialog.component';
import { DataTableComponent } from './shared/components/data-table/data-table.component';
import { EmptyStateComponent } from './shared/components/empty-state/empty-state.component';
import { ErrorBannerComponent } from './shared/components/error-banner/error-banner.component';
import { FormFieldComponent } from './shared/components/form-field/form-field.component';
import { LoadingSpinnerComponent } from './shared/components/loading-spinner/loading-spinner.component';
import { PageHeaderComponent } from './shared/components/page-header/page-header.component';
import { PaginationComponent } from './shared/components/pagination/pagination.component';
import { SearchInputComponent } from './shared/components/search-input/search-input.component';

/**
 * The shape of a compiled component definition, narrowed to the one member this file reads.
 *
 * Declared locally rather than imported: `ComponentDef` is not part of Angular's public surface,
 * and the member is read through a bracket access on a widened type for the same reason the
 * sibling on-push assertions in `app.component.spec.ts` do.
 */
interface CompiledDependencies {
  readonly dependencies?: unknown;
}

/**
 * Every standalone component in the workspace, paired with the name to report it by.
 *
 * THIRTY-SIX entries, which is every file declaring an `imports` array. Ordered as the source
 * tree is, so a reader comparing this against the tree can see a gap.
 */
const STANDALONE_COMPONENTS: readonly (readonly [string, unknown])[] = Object.freeze([
  ['AppComponent', AppComponent],
  ['LoginComponent', LoginComponent],
  ['ModuleExportComponent', ModuleExportComponent],
  ['ModuleFormComponent', ModuleFormComponent],
  ['ModuleImportComponent', ModuleImportComponent],
  ['ModuleListComponent', ModuleListComponent],
  ['ModuleSettingsComponent', ModuleSettingsComponent],
  ['NotFoundComponent', NotFoundComponent],
  ['PortalAliasListComponent', PortalAliasListComponent],
  ['PortalFormComponent', PortalFormComponent],
  ['PortalListComponent', PortalListComponent],
  ['PortalSettingsComponent', PortalSettingsComponent],
  ['RoleAssignmentComponent', RoleAssignmentComponent],
  ['RoleFormComponent', RoleFormComponent],
  ['RoleGroupFormComponent', RoleGroupFormComponent],
  ['RoleListComponent', RoleListComponent],
  ['MembershipSettingsComponent', MembershipSettingsComponent],
  ['ProfileDefinitionListComponent', ProfileDefinitionListComponent],
  ['UserFormComponent', UserFormComponent],
  ['UserListComponent', UserListComponent],
  ['UserPasswordComponent', UserPasswordComponent],
  ['UserProfileComponent', UserProfileComponent],
  ['FooterComponent', FooterComponent],
  ['HeaderComponent', HeaderComponent],
  ['NotificationListComponent', NotificationListComponent],
  ['ShellComponent', ShellComponent],
  ['SidebarComponent', SidebarComponent],
  ['ConfirmDialogComponent', ConfirmDialogComponent],
  ['DataTableComponent', DataTableComponent],
  ['EmptyStateComponent', EmptyStateComponent],
  ['ErrorBannerComponent', ErrorBannerComponent],
  ['FormFieldComponent', FormFieldComponent],
  ['LoadingSpinnerComponent', LoadingSpinnerComponent],
  ['PageHeaderComponent', PageHeaderComponent],
  ['PaginationComponent', PaginationComponent],
  ['SearchInputComponent', SearchInputComponent],
]);

/**
 * Reads the dependency list a component's `imports` array declared.
 *
 * Resolved through the factory when the compiler emitted one, which it does in this workspace's
 * just-in-time test compilation, and read directly when it emitted an array. Both shapes are
 * handled because which one appears is a compilation detail rather than a property of the
 * component, and a check that understood only one would pass vacuously under the other.
 *
 * @param declarable The component class to inspect.
 * @param name The name to report the component by when nothing can be read.
 * @returns The declared dependencies, in source order and with duplicates intact.
 */
function declaredDependencies(declarable: unknown, name: string): readonly unknown[] {
  const definition = (declarable as Record<string, unknown>)['ɵcmp'] as
    | CompiledDependencies
    | undefined;

  if (definition === undefined) {
    throw new Error(`${name} has no compiled component definition, so its imports cannot be read.`);
  }

  const declared: unknown = definition.dependencies;

  if (typeof declared === 'function') {
    return (declared as () => readonly unknown[])();
  }

  if (Array.isArray(declared)) {
    return declared;
  }

  // A component that imports nothing declares no dependencies at all, which is legitimate.
  return [];
}

/**
 * Names the entries a list holds more than once.
 *
 * Compared by REFERENCE and reported by name. Reference is what matters — two distinct symbols
 * that happen to share a name are not a duplicate, and the same symbol reached through two import
 * paths is — and the name is what makes the failure message actionable.
 *
 * @param dependencies The declared dependency list.
 * @returns The names of the repeated entries, each named once however often it repeats.
 */
function repeatedEntries(dependencies: readonly unknown[]): readonly string[] {
  const seen = new Set<unknown>();
  const repeated = new Set<string>();

  for (const dependency of dependencies) {
    if (seen.has(dependency)) {
      const named = dependency as { readonly name?: string };

      repeated.add(typeof named.name === 'string' ? named.name : String(dependency));
      continue;
    }

    seen.add(dependency);
  }

  return Array.from(repeated);
}

describe('standalone component metadata', () => {
  it('covers every component in the workspace that declares an imports array', () => {
    // The list's own guard. It is hand-maintained on purpose, so its SIZE is asserted: a component
    // added to the tree and not to the list fails here rather than going unchecked, and the number
    // is the count of files declaring `imports: [` under `src/app`.
    expect(STANDALONE_COMPONENTS.length)
      .withContext('one entry per component declaring an imports array')
      .toBe(36);

    const names: readonly string[] = STANDALONE_COMPONENTS.map(([name]) => name);

    expect(new Set(names).size).withContext('no component is listed twice').toBe(names.length);
  });

  it('declares every compiled component definition it enumerates', () => {
    // A class that failed to compile as a component would make the duplicate check below pass
    // vacuously, so the readability of every definition is asserted first and separately.
    for (const [name, declarable] of STANDALONE_COMPONENTS) {
      expect(() => declaredDependencies(declarable, name))
        .withContext(`${name} exposes a compiled definition`)
        .not.toThrow();
    }
  });

  it('lists each imported symbol EXACTLY ONCE, in every component', () => {
    // ⚠ MINOR (merge cleanup) — THE CHECK THE EIGHTEEN DUPLICATES GOT PAST. Reported as one
    // message naming every offender rather than one failure per component, because the defect
    // arrives in batches: a directive introduced across seventeen screens duplicates across
    // seventeen screens, and a per-component failure would bury that under seventeen identical
    // messages.
    const offenders: string[] = [];

    for (const [name, declarable] of STANDALONE_COMPONENTS) {
      const repeated: readonly string[] = repeatedEntries(declaredDependencies(declarable, name));

      if (repeated.length > 0) {
        offenders.push(`${name}: ${repeated.join(', ')}`);
      }
    }

    expect(offenders)
      .withContext('a symbol listed twice states two reasons to depend on it, and there is one')
      .toEqual([]);
  });

  it('still imports the two symbols the duplicates were of, so the fix removed a copy and not a use', () => {
    // The counterpart assertion, and it is what keeps the fix honest: removing BOTH entries would
    // also satisfy the check above, and would break the very behaviour the entries exist for -
    // strict template checking would then refuse the template, but only for a component whose
    // template actually applies the selector, so a silent partial removal is possible.
    const focusFirstInvalidUsers: readonly string[] = [
      'LoginComponent',
      'ModuleExportComponent',
      'ModuleFormComponent',
      'ModuleImportComponent',
      'ModuleSettingsComponent',
      'PortalAliasListComponent',
      'PortalFormComponent',
      'PortalSettingsComponent',
      'RoleAssignmentComponent',
      'RoleFormComponent',
      'RoleGroupFormComponent',
      'RoleListComponent',
      'MembershipSettingsComponent',
      'ProfileDefinitionListComponent',
      'UserFormComponent',
      'UserPasswordComponent',
      'UserProfileComponent',
    ];

    expect(focusFirstInvalidUsers.length)
      .withContext('the seventeen screens the duplicate appeared on')
      .toBe(17);

    for (const name of focusFirstInvalidUsers) {
      const entry = STANDALONE_COMPONENTS.find(([candidate]) => candidate === name);

      expect(entry).withContext(`${name} is enumerated`).toBeDefined();

      const dependencies: readonly unknown[] = declaredDependencies(entry![1], name);
      const names: readonly string[] = dependencies.map((dependency) => {
        const named = dependency as { readonly name?: string };

        return typeof named.name === 'string' ? named.name : String(dependency);
      });

      expect(names.filter((candidate) => candidate === 'FocusFirstInvalidDirective').length)
        .withContext(`${name} imports the focus directive exactly once`)
        .toBe(1);
    }

    // And the portal settings screen's own second duplicate, asserted by name for the same reason.
    const settings: readonly unknown[] = declaredDependencies(
      PortalSettingsComponent,
      'PortalSettingsComponent',
    );
    const settingsNames: readonly string[] = settings.map((dependency) => {
      const named = dependency as { readonly name?: string };

      return typeof named.name === 'string' ? named.name : String(dependency);
    });

    expect(settingsNames.filter((candidate) => candidate === 'RouterLink').length)
      .withContext('the host-name listing link still has its directive, once')
      .toBe(1);
  });
});
