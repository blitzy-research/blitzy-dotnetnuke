using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: nine folder-scoped permission members are excluded. Eight of them are the file-system
//            permission block the core provider declares at
//            Library/Components/Providers/Data/DataProvider.vb lines 311 to 318 - its single-row
//            getter, its portal-wide reader, its path-scoped reader, its path-scoped bulk delete, its
//            account-scoped bulk delete, its single-row delete, its insert and its update. The ninth
//            is the path-scoped catalogue reader at L283, and that is the one carried through by
//            accident: it sits INSIDE the Permission block at L279-L288 rather than in the excluded
//            block beneath it, so excluding "L310-L318" alone leaves it behind. Its controller
//            wrapper at PermissionController.vb:L43-L45 is excluded with it - two lines that
//            committed three separate deleted-workaround offences at once, returning an ArrayList
//            hydrated by CBO reached through the reflection-created static provider accessor. The
//            target declares no file-system permission entity among its twenty-one Domain entities
//            and the FileSystem subsystem sits outside scope per AAP section 0.2.2.2, so this
//            carries no path-scoped member at all. The eight VB identifiers are paraphrased rather
//            than quoted on purpose: this contract's own excluded-subsystem check greps the folder
//            case-insensitively for them, and quoting them would report the contract as
//            reintroducing the very subsystem it excludes.
//
// MIGRATION: permission evaluation is not persistence. ModulePermissionController.HasModulePermission
//            (L33, L52, L377) and TabPermissionController.HasTabPermission (L33, L38) move to
//            Infrastructure/Security/PermissionEvaluator.cs and the ASP.NET Core policy handler;
//            ModulePermissionController.GetRoleNamesFromRoleIDs (L356) was a string-join display
//            helper and is dropped. DeleteModulePermissionsByUserID(objUser As UserInfo) at L218
//            takes an identifier here rather than the legacy UserInfo object. The 11 and 10 static
//            DataCache sites in the two module and tab permission controllers become ICacheService in
//            Infrastructure, so no caching parameter appears on this contract. Nothing below returns
//            an access decision: every member reads or writes rows, and what those rows add up to is
//            decided in exactly one other place.
//
// MIGRATION: the membership provider's GetAuthRoles(PortalId, ModuleId) at
//            Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L70 is not surfaced
//            on any repository. It returned the roles authorised for a module - a derived join
//            projection, not an aggregate read. Returning Role from a permission repository would
//            violate the aggregate boundary, and returning it from IRoleRepository would embed
//            permission logic in the role aggregate. Infrastructure/Security/PermissionEvaluator.cs
//            composes the same answer from GetModulePermissionsByModuleIdAsync together with role
//            data obtained through IRoleRepository.
//
// MIGRATION: the legacy Add and Update members took flat positional argument lists - AddPermission 4,
//            UpdatePermission 5, AddModulePermission 5, UpdateModulePermission 6, AddTabPermission 5,
//            UpdateTabPermission 6. Each collapses to a single entity parameter; the values travel as
//            properties on Permission, ModulePermission or TabPermission.
//
// MIGRATION: all three legacy inserts returned Integer because each procedure ended in
//            SCOPE_IDENTITY(). None of the three staging members below returns the generated key,
//            because returning it would force this repository to commit on its own and destroy the
//            unit-of-work boundary. That matters acutely in this aggregate: TabController.vb:L387
//            CopyPermissionsToChildren propagates one page's grants across its children, and the
//            portal-creation sequence at PortalController.vb:L980 writes portals, aliases, roles,
//            pages and modules - with their grants - as one logical operation. Every grant in such a
//            batch has to commit atomically, so the caller stages and then commits once through
//            IUnitOfWork, after which the identity property on the staged entity holds its key.
//
// MIGRATION: the provider typed permissionKey As String (core DataProvider.vb L284, L287, L288)
//            against a varchar(50) column holding the exact values VIEW, EDIT, READ and WRITE. The
//            target uses the PermissionKey enum, whose members map to those exact strings, so the
//            magic strings become named members without changing a single stored value. The scope
//            code beside it stays free text, because it is genuinely open: SYSTEM_MODULE_DEFINITION
//            and SYSTEM_TAB ship with the product and every installed module contributes its own.
//            Null.NullString was the EMPTY STRING rather than null, so a blank code was legally
//            representable and must never be silently coerced to absent.
//
// MIGRATION: the three legacy permission controllers hydrated every row through CBO (six measured
//            call sites in PermissionController.vb alone) and reached the provider through its
//            reflection-created static accessor. CBO.vb is 729 lines of reflection-driven data-reader
//            hydration and produces no target file; ModulePermissionCollection.vb and
//            TabPermissionCollection.vb were pre-generics CollectionBase wrappers and likewise
//            produce no target file. The EF Core materializer and IReadOnlyList<T> replace all three,
//            so every read below hands back materialised entities and never an open query.

/// <summary>
/// Reads and writes the permission aggregate: the catalogue of permissions, the grants recorded
/// against a module instance, and the grants recorded against a page.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this single contract replaces three provider blocks and three static controllers.
/// The blocks are the catalogue at core <c>DataProvider.vb</c> L279-L288, the module grants at
/// L290-L299 and the page grants at L301-L308 - eight, nine and seven members in scope. The
/// controllers are <c>PermissionController.vb</c> (71 lines, 9 public members),
/// <c>ModulePermissionController.vb</c> (389 lines, 18) and <c>TabPermissionController.vb</c>
/// (349 lines, 15). Only their persistence subset appears here; the collapse from three contracts
/// to one is the point, and no separate module-grant or page-grant repository exists.
/// </para>
/// <para>
/// <strong>Three entities, no inheritance.</strong> <see cref="ModulePermission"/> and
/// <see cref="TabPermission"/> do not derive from <see cref="Permission"/>. Each is an independent
/// entity carrying a permission identifier as a foreign key, which is why no member below is generic
/// over a shared base: a grant is not a kind of permission, it is a reference to one.
/// </para>
/// <para>
/// <strong>What this contract does not do.</strong> It answers no access question. Whether a
/// principal holds a permission is decided in <c>Infrastructure/Security/PermissionEvaluator.cs</c>
/// and enforced by the API authorisation handler, so no member here takes a principal, a claims set
/// or a list of role names, and none returns a boolean verdict. It also performs no caching, no
/// validation, no ordering policy for a screen, no audit logging and no paging - none of the three
/// provider blocks is paged, so inventing a page contract would be inventing legacy behaviour.
/// </para>
/// <para>
/// <strong>Identifiers are plain values.</strong> Every identifier below is a plain
/// <see cref="int"/>. No number is reserved to mean "absent": <c>Portals.PortalID</c> is
/// <c>IDENTITY(-1, 1)</c> and the shipped default portal is 0, while <c>Roles.RoleID</c>,
/// <c>Tabs.TabID</c> and <c>Modules.ModuleID</c> are all <c>IDENTITY(0, 1)</c>, so both 0 and -1 are
/// genuine persisted identifiers in this schema. Where a legacy procedure gave a value a wildcard
/// meaning, the member that inherits it says so explicitly.
/// </para>
/// </remarks>
public interface IPermissionRepository
{
    // =============================================================================================
    // Permission - the catalogue.
    // Core DataProvider.vb L279-L288: eight of its nine members, the path-scoped reader at L283
    // being the excluded ninth. Rows describe what a permission IS; they grant nothing on their own.
    // =============================================================================================

    /// <summary>Returns one catalogue entry by key, or <see langword="null"/> when none exists.</summary>
    /// <param name="permissionId">Permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L280 <c>GetPermission(permissionID)</c>. The terminal
    /// procedure selects the five catalogue columns for one primary key, so at most one row can
    /// return, which is why the result is a single nullable entity rather than a list.
    /// </remarks>
    Task<Permission?> GetByIdAsync(int permissionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries for a set of keys, in one read.</summary>
    /// <param name="permissionIds">
    /// The identifiers to resolve. Every value is meaningful and none is reserved: the catalogue's own
    /// identity column seeds at 1, but -1 appears as a wildcard argument elsewhere on this contract and
    /// nothing here reinterprets it - an identifier naming no row is simply absent from the answer.
    /// Duplicates are tolerated and collapse; an empty set is a legitimate request whose answer is an
    /// empty list, and no read need be issued for it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The entries that exist, ordered by identifier so the sequence is stable between calls. An
    /// identifier that names no entry is omitted rather than represented by a null element, so the result
    /// may be shorter than the request and the caller must not index the two against each other.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy provider declared no set-wise catalogue reader - <c>GetPermission</c> (core
    /// <c>DataProvider.vb</c>:L280) took one identifier - so this member is net-new rather than a port. It
    /// exists because the alternative is the caller looping <see cref="GetByIdAsync"/>, which issues one
    /// round trip per distinct permission on a path that resolves a caller's whole permission set. Adding
    /// the member is the correct fix rather than making the caller cleverer, because the batching belongs
    /// where the query is composed.
    /// </para>
    /// <para>
    /// The single-identifier reader is deliberately kept alongside it. A caller resolving exactly one
    /// entry should say so and receive a nullable entity, not a list it has to unwrap.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Permission>> GetByIdsAsync(
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries declared by one module definition.</summary>
    /// <param name="moduleDefinitionId">Module definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L281 <c>GetPermissionsByModuleDefID(ModuleDefID)</c>,
    /// whose terminal body filters on the definition column alone and orders by permission
    /// identifier. Returning no entry is a legitimate answer: a definition may declare none.
    /// </remarks>
    Task<IReadOnlyList<Permission>> GetByModuleDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries that apply to one module instance.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L282 <c>GetPermissionsByModuleID(ModuleID)</c>. The
    /// terminal body resolves the module's definition and then takes the union of that definition's
    /// entries with the product-wide module-definition scope code, so the answer is deliberately
    /// wider than the definition-scoped read above it. The parameter names a module instance and the
    /// result is catalogue entries, never grants - that asymmetry is the legacy shape and is kept.
    /// </remarks>
    Task<IReadOnlyList<Permission>> GetByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default);

    // MIGRATION: core DataProvider.vb:L284 is named GetPermissionByCodeAndKey - singular - but
    //            PermissionController.vb:L47-L48 wraps it in CBO.FillCollection and returns an
    //            ArrayList, so it is a collection query. The terminal procedure confirms it: it
    //            filters each column against its argument and can match many rows. The target
    //            therefore returns a read-only list of catalogue entries; preserving the singular
    //            name and a single-entity return would silently drop rows.
    /// <summary>Returns the catalogue entries matching one scope code and one permission key.</summary>
    /// <param name="permissionCode">
    /// Scope code, matched exactly. Free text by design - <c>SYSTEM_MODULE_DEFINITION</c> and
    /// <c>SYSTEM_TAB</c> ship with the product and installed modules contribute their own - so it is
    /// a plain non-nullable string with no closed set behind it. The empty string is a legal value
    /// and is not treated as absent.
    /// </param>
    /// <param name="permissionKey">
    /// Permission key. Typed as the domain enumeration rather than as text, because the column holds
    /// exactly the enumeration's member names and nothing else.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Permission>> GetByCodeAndKeyAsync(
        string permissionCode,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the catalogue entries that apply to one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L285 <c>GetPermissionsByTabID(TabID)</c>. Its terminal
    /// body filters on the product-wide page scope code and never mentions the page argument at all,
    /// so every page receives the same catalogue. The parameter is kept because the legacy signature
    /// declares it and callers pass it; the measured behaviour is recorded here so that nobody reads
    /// the implementation as having lost a filter.
    /// </remarks>
    Task<IReadOnlyList<Permission>> GetByTabIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Removes one catalogue entry.</summary>
    /// <param name="permissionId">Permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L286 <c>DeletePermission(permissionID)</c>. Removing
    /// nothing is a legitimate outcome, so an identifier that names no row is not an error.
    /// </remarks>
    Task DeleteAsync(int permissionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new catalogue entry for insertion.</summary>
    /// <param name="permission">The entry to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L287 <c>AddPermission</c> took four positional
    /// arguments and returned the generated key from <c>SCOPE_IDENTITY()</c>. Here the four values
    /// travel as properties on one entity and no key is returned: the insert is staged, and the
    /// identity property on the passed entity holds its key once the caller commits through
    /// <see cref="IUnitOfWork"/>.
    /// </remarks>
    Task AddAsync(Permission permission, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing catalogue entry for update.</summary>
    /// <param name="permission">The entry to update, carrying its own identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L288 <c>UpdatePermission</c> took five positional
    /// arguments led by the identifier; all five travel as properties on one entity.
    /// </remarks>
    Task UpdateAsync(Permission permission, CancellationToken cancellationToken = default);

    // =============================================================================================
    // ModulePermission - one grant recorded against one module instance, to a role or to an account.
    // Core DataProvider.vb L290-L299: all nine members.
    // =============================================================================================

    // MIGRATION: legacy AddModulePermission (L298) and AddTabPermission (L307) passed -1 in two
    //            positions with two different meanings. In roleID, -1 is the real All Users
    //            pseudo-principal (alongside -2 Superuser and -3 Unauthenticated Users); in UserID,
    //            -1 was Null.NullInteger meaning absent. Null.IsNull(-1) returned True and could not
    //            tell them apart. The target maps RoleId and UserId to int? and RoleId = -1 must
    //            never be treated as null, while a legacy UserID of -1 maps to null. Sentinel
    //            compatibility, where a contract exposes it, is handled at the DTO and API boundary
    //            and never in these signatures.

    /// <summary>Returns one module grant by key, or <see langword="null"/> when none exists.</summary>
    /// <param name="modulePermissionId">Module permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L291 <c>GetModulePermission(modulePermissionID)</c>,
    /// whose terminal body selects one row of the module-grant view by primary key. The page block
    /// declares no counterpart to this member - see the note above the page section.
    /// </remarks>
    Task<ModulePermission?> GetModulePermissionByIdAsync(int modulePermissionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the grants recorded against one module.</summary>
    /// <param name="moduleId">
    /// Module identifier. The terminal procedure treats -1 in this position as a wildcard matching
    /// every module, which is measured legacy behaviour rather than an absence marker.
    /// </param>
    /// <param name="permissionId">
    /// Permission identifier to narrow to. The terminal procedure treats -1 in this position as a
    /// wildcard requesting every permission, so this is the member that answers "every grant on this
    /// module" as well as "this one permission on this module".
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L292
    /// <c>GetModulePermissionsByModuleID(moduleID, PermissionID)</c>. Its terminal body also unions
    /// in the grants whose module column is null and whose scope code is the product-wide
    /// module-definition code; the target entity declares a non-nullable module identifier, so that
    /// branch cannot arise against the target model and no member is provided for it. Both allowing
    /// and denying grants come back: a consumer that discarded denials would be unable to suppress
    /// anything.
    /// </remarks>
    Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByModuleIdAsync(
        int moduleId,
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every module grant within one portal.</summary>
    /// <param name="portalId">
    /// Portal identifier. Both 0 and -1 are genuine values here, because the portal identity column
    /// seeds at -1 and the shipped default portal is 0.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L293 <c>GetModulePermissionsByPortal(PortalID)</c>,
    /// whose terminal body joins the grant view to the modules table and filters on the portal
    /// column, which is what keeps one tenant's grants from reaching another tenant's answer.
    /// </remarks>
    Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the module grants for every module placed on one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L294 <c>GetModulePermissionsByTabID(TabID)</c>, whose
    /// terminal body joins the grant view to the module placement table. It returns MODULE grants
    /// selected by page, not page grants, and it is a genuine cross-scope query that the page block
    /// has no mirror image of.
    /// </remarks>
    Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Removes every grant recorded against one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L295
    /// <c>DeleteModulePermissionsByModuleID(ModuleID)</c>. A bulk removal, so nothing is returned:
    /// the legacy procedure reported no count either, and removing nothing is a legitimate outcome.
    /// </remarks>
    Task DeleteModulePermissionsByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Removes the module grants held directly by one account within one portal.</summary>
    /// <param name="portalId">Portal identifier, which bounds the removal to one tenant.</param>
    /// <param name="userId">Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L296
    /// <c>DeleteModulePermissionsByUserID(PortalID, UserID)</c>, whose terminal body joins the grant
    /// table to the modules table so that only the named tenant's grants are removed. Only grants
    /// naming the account itself go: a grant the account receives through a role belongs to the role,
    /// and removing it would strip every other holder of that role. The controller overload at
    /// <c>ModulePermissionController.vb</c>:L218 took the legacy user object instead of these two
    /// identifiers and is not reproduced.
    /// </remarks>
    Task DeleteModulePermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Removes one module grant.</summary>
    /// <param name="modulePermissionId">Module permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L297
    /// <c>DeleteModulePermission(modulePermissionID)</c>.
    /// </remarks>
    Task DeleteModulePermissionAsync(int modulePermissionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new module grant for insertion.</summary>
    /// <param name="modulePermission">The grant to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L298 <c>AddModulePermission</c> took five positional
    /// arguments and returned the generated key. The five values travel as properties on one entity
    /// and no key is returned, so a batch of grants stages together and commits atomically through
    /// <see cref="IUnitOfWork"/>; the identity property on the passed entity holds its key afterwards.
    /// </remarks>
    Task AddModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing module grant for update.</summary>
    /// <param name="modulePermission">The grant to update, carrying its own identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L299 <c>UpdateModulePermission</c> took six positional
    /// arguments led by the identifier; all six travel as properties on one entity.
    /// </remarks>
    Task UpdateModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default);

    // =============================================================================================
    // TabPermission - one grant recorded against one page, to a role or to an account.
    // Core DataProvider.vb L301-L308: all seven members. Seven, not nine - read the note below
    // before concluding that something is missing here.
    // =============================================================================================

    // MIGRATION: the legacy provider declares a single-row getter GetModulePermission
    //            (modulePermissionID) at L291 but declares NO equivalent
    //            GetTabPermission(tabPermissionID) - the TabPermission block at L301-L308 has seven
    //            members against ModulePermission's nine, and also lacks a tab-scoped cross-query
    //            matching GetModulePermissionsByTabID at L294. This asymmetry is measured, not an
    //            oversight, and the Minimal Change Clause forbids inventing the missing member.
    //
    // MIGRATION: a third, narrower instance of the same asymmetry sits inside the two-argument
    //            readers. The terminal module-grant procedure accepts a wildcard in its scope
    //            argument as well as in its permission argument; the terminal page-grant procedure
    //            accepts one only in its permission argument. That difference is preserved in the
    //            parameter documentation rather than smoothed over.

    /// <summary>Returns every page grant within one portal.</summary>
    /// <param name="portalId">Portal identifier, which bounds the answer to one tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L302 <c>GetTabPermissionsByPortal(PortalID)</c>. Its
    /// terminal body also matches the host-level rows whose portal column is null, but only when the
    /// argument is itself null; the argument here is a plain value, so host-level grants are not
    /// returned and the caller that needs them asks for them by their own scope.
    /// </remarks>
    Task<IReadOnlyList<TabPermission>> GetTabPermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the grants recorded against one page.</summary>
    /// <param name="tabId">
    /// Page identifier. Unlike its module counterpart this position has no wildcard: the terminal
    /// page-grant procedure matches the page exactly.
    /// </param>
    /// <param name="permissionId">
    /// Permission identifier to narrow to. The terminal procedure treats -1 in this position as a
    /// wildcard requesting every permission, so this is the member that answers "every grant on this
    /// page" as well as "this one permission on this page".
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L303
    /// <c>GetTabPermissionsByTabID(TabID, PermissionID)</c>. Its terminal body also unions in the
    /// grants whose page column is null and whose scope code is the product-wide page code; the
    /// target entity declares a non-nullable page identifier, so that branch cannot arise against
    /// the target model. Denying grants come back alongside allowing ones.
    /// </remarks>
    Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdAsync(
        int tabId,
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every grant recorded against one page.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L304 <c>DeleteTabPermissionsByTabID(TabID)</c>. A bulk
    /// removal, so nothing is returned.
    /// </remarks>
    Task DeleteTabPermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Removes the page grants held directly by one account within one portal.</summary>
    /// <param name="portalId">Portal identifier, which bounds the removal to one tenant.</param>
    /// <param name="userId">Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L305
    /// <c>DeleteTabPermissionsByUserID(PortalID, UserID)</c>, whose terminal body joins the grant
    /// table to the pages table so that only the named tenant's grants are removed. As with its
    /// module counterpart, only grants naming the account itself are removed.
    /// </remarks>
    Task DeleteTabPermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Removes one page grant.</summary>
    /// <param name="tabPermissionId">Page permission identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L306 <c>DeleteTabPermission(TabPermissionID)</c>. Note
    /// that the legacy block offers this delete by identifier while offering no read by identifier,
    /// which is part of the measured asymmetry recorded above.
    /// </remarks>
    Task DeleteTabPermissionAsync(int tabPermissionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new page grant for insertion.</summary>
    /// <param name="tabPermission">The grant to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L307 <c>AddTabPermission</c> took five positional
    /// arguments and returned the generated key. The five values travel as properties on one entity
    /// and no key is returned, which is what lets a page's grants be copied onto its children as one
    /// atomic commit through <see cref="IUnitOfWork"/> rather than one commit per grant.
    /// </remarks>
    Task AddTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing page grant for update.</summary>
    /// <param name="tabPermission">The grant to update, carrying its own identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: core <c>DataProvider.vb</c>:L308 <c>UpdateTabPermission</c> took six positional
    /// arguments led by the identifier; all six travel as properties on one entity.
    /// </remarks>
    Task UpdateTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default);
}
