using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Abstractions;

/// <summary>Reads the permission catalogue and resolves what a caller may do within a portal.</summary>
/// <remarks>
/// <para>
/// This contract answers three questions and no others: <em>which permission keys exist?</em>, <em>which of
/// them does this caller hold here?</em> and <em>does this caller hold this one?</em> The migration notes
/// at the head of this file account for all 42 measured legacy members against the 5 declared below,
/// including the members that are deliberately not ported and why.
/// </para>
/// <para>
/// <strong>Paging is deliberately absent.</strong> The catalogue is small, bounded reference data seeded by
/// the upgrade scripts, and a caller's effective key set is bounded by the number of keys the catalogue
/// defines. Every read returns a complete read-only sequence rather than a page.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>Reads the permission keys the catalogue defines, optionally narrowed.</summary>
    /// <param name="permissionCode">
    /// Scope code a permission definition belongs to, matched exactly and case-insensitively, or <see
    /// langword="null"/> to place no restriction.
    /// </param>
    /// <param name="moduleDefinitionId">
    /// Module definition whose declared permissions are wanted, or <see langword="null"/> to place no
    /// restriction.
    /// </param>
    /// <param name="permissionKey">
    /// The one key the answer is narrowed to, or <see langword="null"/> to place no restriction.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the distinct, upper-cased keys
    /// in a stable order, and an empty sequence when nothing matches - an empty catalogue is a legitimate
    /// answer, never a failure.
    /// </returns>
    Task<Result<IReadOnlyList<string>>> GetPermissionKeysAsync(
        string? permissionCode = null,
        int? moduleDefinitionId = null,
        PermissionKey? permissionKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves every permission key a caller holds within a portal, optionally narrowed to one module or
    /// one page.
    /// </summary>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <param name="userId">
    /// The caller, or <see langword="null"/> for an anonymous caller, who is evaluated against the portal's
    /// unauthenticated pseudo-role.
    /// </param>
    /// <param name="moduleId">
    /// Module to restrict the answer to, or <see langword="null"/> to ignore module scope.
    /// </param>
    /// <param name="tabId">Page to restrict the answer to, or <see langword="null"/> to ignore page scope.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the distinct, upper-cased keys
    /// the caller holds, after deny rows have suppressed the keys they deny.
    /// </returns>
    /// <remarks>
    /// This is what populates the permission claims of an issued access token and the <c>Permissions</c>
    /// member of the current-user contract, and it is what the Angular directive mirrors. Supplying neither
    /// scope answers at portal level, which is the set an administration shell needs to decide which
    /// sections to offer.
    /// </remarks>
    Task<Result<IReadOnlyList<string>>> GetEffectivePermissionKeysAsync(
        int portalId,
        int? userId,
        int? moduleId = null,
        int? tabId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Decides whether a caller holds one permission on one module.</summary>
    /// <param name="portalId">The portal the module belongs to.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="moduleId">The module. <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so 0 is genuine.</param>
    /// <param name="permissionKey">The permission being tested.</param>
    /// <param name="placementTabId">
    /// The page the request addresses the module ON, when the request addresses one.
    /// </param>
    /// <param name="placementTabModuleId">
    /// The placement the request addresses, named by its own key - <c>TabModules.TabModuleID</c> - rather
    /// than by the page it sits on.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>A task producing a successful <see cref="Result{T}"/> whose value is the decision.</returns>
    /// <remarks>
    /// Delegates precedence evaluation to the single evaluator reached through the permission repository.
    /// Honours the module's inherit-view-permissions flag exactly as the legacy check did, so a module
    /// configured to take its view permission from its page is answered from the page's grants.
    /// </remarks>
    Task<Result<bool>> HasModulePermissionAsync(
        int portalId,
        int? userId,
        int moduleId,
        PermissionKey permissionKey,
        int? placementTabId = null,
        int? placementTabModuleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Decides whether a caller holds one permission on one page.</summary>
    /// <param name="portalId">The portal the page belongs to.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="tabId">The page. <c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>, so 0 is genuine.</param>
    /// <param name="permissionKey">The permission being tested.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>A task producing a successful <see cref="Result{T}"/> whose value is the decision.</returns>
    Task<Result<bool>> HasTabPermissionAsync(
        int portalId,
        int? userId,
        int tabId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default);

    /// <summary>Decides whether a caller holds one permission key ANYWHERE in a tenant's pages.</summary>
    /// <param name="portalId">The tenant whose pages are judged.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="cancellationToken">Token that cancels the reads.</param>
    /// <returns>A task producing a successful <see cref="Result{T}"/> whose value is the decision.</returns>
    /// <remarks>
    /// A CAPABILITY QUESTION, NOT A RESOURCE ONE, and it exists because some operations cannot be scoped to
    /// a resource that does not exist yet. Module placement is the case: the create action names no page,
    /// because the target arrives in the request body, so a caller has to be admitted to the FORM before
    /// any page identifier exists to scope a permission to.
    /// </remarks>
    Task<Result<bool>> HasAnyTabPermissionInPortalAsync(
        int portalId,
        int? userId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default);

    /// <summary>Reports which of the named pages a caller holds one permission key on.</summary>
    /// <param name="portalId">The tenant the question is asked within.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="tabIds">The pages to judge.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="cancellationToken">Token that cancels the reads.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the granting page identifiers.
    /// </returns>
    /// <remarks>
    /// FOR NARROWING A PROJECTION, NOT FOR DECIDING ADMISSION. Its purpose is to let a listing offered as a
    /// choice contain only the entries the caller may choose, so that the choice cannot be made and then
    /// refused. Admission remains the policy's answer.
    /// </remarks>
    Task<Result<IReadOnlyList<int>>> ListTabsWithPermissionAsync(
        int portalId,
        int? userId,
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default);

    /// <summary>Decides whether a caller administers one portal.</summary>
    /// <param name="portalId">The portal whose administration is in question.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="cancellationToken">Token that cancels the reads.</param>
    /// <returns>A task producing a successful <see cref="Result{T}"/> whose value is the decision.</returns>
    /// <remarks>
    /// <b>Decided from STORED STATE, never from a claim.</b> A host account is admitted first, because a
    /// host account is installation-wide and the legacy security test short-circuited on it before
    /// examining any role.
    /// </remarks>
    Task<Result<bool>> IsPortalAdministratorAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Reports whether the caller is an installation-wide host account, read from stored state.</summary>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>A task producing a successful <see cref="Result{T}"/> whose value is the decision.</returns>
    /// <remarks>
    /// WHY THIS IS PUBLISHED SEPARATELY FROM THE ADMINISTRATION QUESTION, which already admits a host
    /// account first.
    /// </remarks>
    Task<Result<bool>> IsHostAccountAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every module- and page-scoped grant made directly to one user within one portal, as a
    /// self-contained operation that commits on its own.
    /// </summary>
    /// <param name="portalId">The portal to clean up within.</param>
    /// <param name="userId">The user whose direct grants are removed.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the user held no direct grants -
    /// removing nothing is a legitimate outcome.
    /// </returns>
    Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages - and only stages - the removal of every module- and page-scoped grant made directly to one
    /// user within one portal, leaving the commit to the operation that called it.
    /// </summary>
    /// <param name="portalId">The portal to clean up within.</param>
    /// <param name="userId">The user whose direct grants are staged for removal.</param>
    /// <param name="cancellationToken">Token that cancels the reads this staging performs.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the user held no direct grants.
    /// </returns>
    /// <remarks>
    /// WHY THIS MEMBER EXISTS SEPARATELY FROM <see cref="DeleteUserPermissionsAsync"/>. Revoking a user's
    /// direct grants is one step of deleting the account that holds them, and the enclosing operation also
    /// removes role assignments, tenant membership, the external credential and the account row.
    /// </remarks>
    Task<Result> StageUserPermissionRemovalAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every grant addressed to one role, across all three grant families, leaving the commit to
    /// the operation that called it.
    /// </summary>
    /// <param name="portalId">The portal that owns the role.</param>
    /// <param name="roleId">The role whose grants are removed.</param>
    /// <param name="cancellationToken">Token that cancels the reads and the removals.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the role held no grant at all -
    /// removing nothing is a legitimate outcome.
    /// </returns>
    /// <remarks>
    /// THREE FAMILIES, NOT TWO. The folder family has no entity in this migration because file management
    /// is out of scope, but the table exists in every upgraded DotNetNuke database, so its rows are removed
    /// where it is present and the removal is a no-op where it is not. An implementer must not create,
    /// alter or drop anything to achieve that.
    /// </remarks>
    Task<Result> StageRolePermissionRemovalAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts the cache entries invalidated by removing a user's direct grants, once the removal has been
    /// committed.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="StageUserPermissionRemovalAsync"/>, for the caller that owned the
    /// commit. It is separate because it MUST run after that commit: a warm entry would otherwise keep
    /// answering with grants that no longer exist, and grants are what authorisation is decided from, so
    /// the staleness is a security matter rather than a display one.
    /// </remarks>
    /// <remarks>
    /// SYNCHRONOUS, INFALLIBLE AND ARGUMENT-FREE, because a post-commit step must not be able to fail and
    /// this one no longer needs to ask the database anything.
    /// </remarks>
    void InvalidateUserPermissionCaches();

    /// <summary>Reads one catalogue definition by its identifier.</summary>
    /// <param name="permissionId">The definition wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definition, or whose value is
    /// <see langword="null"/> when no definition bears that identifier - which lets the API layer answer
    /// <c>404</c> without this member raising a failure for an ordinary "not there".
    /// </returns>
    /// <remarks>
    /// Catalogue rows are installation-wide reference data with no portal column, so this read takes no
    /// tenant argument - which is the same reason the catalogue read above takes none.
    /// </remarks>
    Task<Result<PermissionDto?>> GetPermissionAsync(
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the catalogue definitions that apply to one module placement.</summary>
    /// <param name="portalId">The portal whose catalogue is being administered.</param>
    /// <param name="moduleId">The module placement whose applicable definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in a stable order,
    /// each definition once, or <c>permission.module_not_found</c> when the placement does not belong to
    /// <paramref name="portalId"/>.
    /// </returns>
    /// <remarks>
    /// the placement is resolved before the catalogue is read and must belong to the addressed portal. A
    /// missing placement and one owned by another portal report the same failure, so this read cannot be
    /// used as a cross-tenant identifier oracle.
    /// </remarks>
    Task<Result<IReadOnlyList<PermissionDto>>> GetModulePermissionDefinitionsAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the catalogue definitions that apply to one page.</summary>
    /// <param name="portalId">The portal whose catalogue is being administered.</param>
    /// <param name="tabId">The page whose applicable definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in a stable order,
    /// each definition once, or <c>permission.tab_not_found</c> when the page does not belong to <paramref
    /// name="portalId"/>.
    /// </returns>
    /// <remarks>
    /// MIGRATION: as with the module-scoped read, catalogue definitions are returned rather than grant
    /// rows; page grants belong to the grant-management surface deliberately absent from this contract.
    /// </remarks>
    Task<Result<IReadOnlyList<PermissionDto>>> GetTabPermissionDefinitionsAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One catalogue definition: the scope it belongs to, the key it declares and the module definition that
/// declared it.
/// </summary>
/// <remarks>
/// The catalogue LIST endpoint deliberately continues to publish bare keys rather than these records: a
/// catalogue of keys is what the permission vocabulary is, it is what the token carries and what the
/// client-side permission directive tests, and widening it would change an established contract for no
/// caller's benefit.
/// </remarks>
public sealed class PermissionDto
{
    /// <summary>Identifier of the definition, from <c>Permission.PermissionID</c>.</summary>
    /// <remarks>
    /// <c>IDENTITY (1, 1)</c> in the terminal schema, so no value here is a sentinel; it is nevertheless
    /// never compared against a bound anywhere in this solution.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <summary>Scope code the definition belongs to, from <c>Permission.PermissionCode</c>.</summary>
    public string PermissionCode { get; set; } = string.Empty;

    /// <summary>Module definition the permission was declared under, from <c>Permission.ModuleDefID</c>.</summary>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The key itself - <c>VIEW</c>, <c>EDIT</c>, <c>READ</c> or <c>WRITE</c> - from
    /// <c>Permission.PermissionKey</c>.
    /// </summary>
    /// <remarks>
    /// Travels as the member NAME rather than as an integer, because the name is what the column stores and
    /// what every other permission-shaped value in this API carries: the token's permission claims and the
    /// catalogue listing both publish these same spellings, so a numeric form here would make one concept
    /// travel two ways.
    /// </remarks>
    public string PermissionKey { get; set; } = string.Empty;

    /// <summary>Display name of the definition, from <c>Permission.PermissionName</c>.</summary>
    public string PermissionName { get; set; } = string.Empty;
}
