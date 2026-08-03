// MIGRATION: this controller replaces the READ surface of the three legacy permission controllers and
// nothing else. Measured against the source rather than estimated: PermissionController.vb is 72 lines
// with 9 public members, ModulePermissionController.vb is 389 lines with 18, and
// TabPermissionController.vb is 349 lines with 15 - 42 members, of which exactly ONE endpoint survives
// here. The reduction is accounted for rather than trimmed by taste, and each of the five groups below
// names where the behaviour went so that nothing looks lost when it is merely elsewhere.
//
// MIGRATION: the three catalogue MUTATORS are not ported - PermissionController.vb:L55
// (DeletePermission), :L59 (AddPermission) and :L63 (UpdatePermission). This is evidence-based, not an
// assumption: there is no administration screen anywhere under Website/admin/ for the permission
// catalogue. Catalogue rows are reference data seeded by the upgrade scripts and written only by the
// module installer, and the installer subsystem is out of scope for this migration. Contrast the
// profile-definition surface, which IS full CRUD precisely because a legacy screen provides add, edit,
// delete and reorder. Consequently this controller declares no POST, PUT, PATCH or DELETE action, and
// none may be added here: a write endpoint over installer-owned reference data would invite a partial
// reimplementation of the installer.
//
// MIGRATION: the FOLDER-scoped catalogue read is not ported - PermissionController.vb:L43
// (GetPermissionsByFolder, which called GetPermissionsByFolderPath). The file-management subsystem it
// served is excluded, so the folder grant table has no target feature and a lookup keyed by a folder
// path has no caller. The catalogue rows that subsystem relies on still exist and the unfiltered read
// below may legitimately return their keys - READ and WRITE are the folder-scope keys - so only the
// folder-path lookup disappears, not the data.
//
// MIGRATION: the four remaining legacy catalogue reads COLLAPSE into the single action below.
// PermissionController.vb:L34 (by module definition), :L38 (by module), :L47 (by code and key) and :L51
// (by tab) differed only in which single column they filtered on, and each returned an untyped
// ArrayList hydrated by reflection through CBO.FillCollection. The application contract exposes one
// read whose filters compose conjunctively, so one endpoint covers all four without inventing
// behaviour. Two of the four legacy filters - by module and by tab - have no counterpart on that
// contract because a module or page identifier selects GRANTS rather than catalogue definitions, and
// grants are not this controller's subject.
//
// MIGRATION: permission EVALUATION is deliberately absent from this controller. Deciding whether a
// caller holds a key happens in exactly one place - Infrastructure/Security/PermissionEvaluator.cs,
// reached through Api/Authorization/PermissionAuthorizationHandler.cs - and a second evaluator that
// could disagree with the first is the worst outcome available in this area, because the two would
// agree throughout testing and diverge on the single case that mattered. The application contract's
// decision members therefore keep their real callers and lose only their HTTP exposure:
// HasModulePermissionAsync and HasTabPermissionAsync are consumed by that authorisation handler,
// GetEffectivePermissionKeysAsync by AuthService and JwtTokenService when access-token claims are
// minted, and DeleteUserPermissionsAsync by UserService as part of the user-deletion cascade. Nothing
// is orphaned by their absence here, and no probe endpoint reports one user's reach to another caller.
//
// MIGRATION: reflection-based provider access is gone. Every legacy member reached its data through
// DataProvider.Instance(), a reflection-instantiated singleton, and hydrated rows through the
// reflection helper CBO. Both are replaced by the constructor-injected application service below; this
// controller performs no data access of its own and holds no persistence type.

using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The permission catalogue: which permission keys this installation defines.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and that is a boundary rather than a phase.</strong> The catalogue describes what
/// permissions <em>exist</em>; it never describes who holds them and it is never written through this
/// API. The provenance comments at the head of this file account for all forty-two measured legacy
/// members, including the four that are deliberately not ported and the reason for each.
/// </para>
/// <para>
/// <strong>Asks, never decides.</strong> The single action here poses a question to the application
/// service and translates the answer into a status code. It applies no filtering, sorting, grouping or
/// precedence of its own, so there is no rule expressed here that could drift out of step with the rule
/// expressed in the evaluator.
/// </para>
/// <para>
/// <strong>Keys travel as strings, on purpose.</strong> The catalogue's key column is
/// <c>varchar(20)</c> and its code column is <c>varchar(50)</c>, and an installation may legitimately
/// carry a code or key this codebase has never seen - a module installed years ago against the legacy
/// schema still round-trips intact. The closed <see cref="DnnMigration.Domain.Enums.PermissionKey"/>
/// enumeration names the four keys this application reasons about (VIEW, EDIT, READ and WRITE, whose
/// member names are the persisted values); it is not a whitelist for what the catalogue may contain, so
/// this endpoint neither narrows nor rewrites what it reads. <c>PermissionCode</c> has no enumeration
/// for the same reason and none may be created for it.
/// </para>
/// <para>
/// <strong>Administrator-gated, and it fails closed.</strong> The class-level policy is
/// <see cref="PolicyNames.PortalAdministrator"/>, which is the declarative equivalent of the legacy
/// gate <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c> that guarded the
/// administration screens. It is the correct policy specifically because this route carries neither a
/// module nor a page identifier: the module and page policies resolve their scope from route data, so
/// an action with no such identifier can only ever be refused by them.
/// </para>
/// <para>
/// MIGRATION: the legacy gate answered a refusal with
/// <c>Response.Redirect(NavigateURL("Access Denied"), True)</c>, which rendered the localised message
/// at <c>Website/admin/Security/AccessDenied.ascx.vb:L45</c>. A JSON API has no page to redirect to, so
/// the target answers a plain <c>403 Forbidden</c> from the authorisation middleware and the redirect is
/// not reproduced. A caller who has presented no credential at all receives <c>401 Unauthorized</c>
/// instead, a distinction the legacy redirect could not express.
/// </para>
/// <para>
/// MIGRATION: the identifier filter accepted below is a nullable integer, and absence is
/// <see langword="null"/>. It is never a numeric sentinel. The legacy sentinel for a missing integer is
/// -1 (<c>Library/Components/Shared/Null.vb:L41-L45</c>), but that value is not free: the portal table
/// is <c>IDENTITY (-1, 1)</c>, so -1 identifies the first real portal, and the role, page and module
/// tables all seed at 0, so 0 is a real identifier too. Neither value may be read as "absent" nor
/// emitted to signal it, which is why no range constraint is placed on any identifier here.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/permissions")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class PermissionsController : ControllerBase
{
    /// <summary>The catalogue reader this controller delegates to.</summary>
    private readonly IPermissionService _permissions;

    /// <summary>Initialises a new instance of the <see cref="PermissionsController"/> class.</summary>
    /// <param name="permissions">The permission service that reads the catalogue.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="permissions"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// One dependency, and it is an application-layer contract. There is no repository, no persistence
    /// context and no evaluator here: the first two are unreachable from this layer by design and the
    /// third would duplicate an authority that already exists.
    /// </remarks>
    public PermissionsController(IPermissionService permissions)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }

    /// <summary>Lists the permission keys this installation defines.</summary>
    /// <param name="permissionCode">
    /// Restricts the result to one permission code - the scope a definition belongs to, matched exactly
    /// and case-insensitively - or omitted to place no restriction. A blank or whitespace-only value is
    /// bound as omitted rather than as a blank filter, because the framework's string binding converts
    /// whitespace to <see langword="null"/>; such a request therefore returns the whole catalogue instead
    /// of being refused. Verified against the running endpoint, so nobody hunts for a rejection that
    /// cannot arrive.
    /// </param>
    /// <param name="moduleDefinitionId">
    /// Restricts the result to the permissions one module definition declares, or omitted to place no
    /// restriction. Definitions belonging to no module definition are matched only when this is omitted.
    /// The value is forwarded exactly as bound - no lower bound is imposed here - and the application
    /// contract refuses one that cannot name a row in its table. That refusal is table-specific rather
    /// than a general rule about small numbers: the module-definition table is <c>IDENTITY (1, 1)</c>, so
    /// 0 and -1 cannot be keys for it, whereas 0 is a genuine key for the role, page and module tables
    /// and -1 is a genuine portal.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// The distinct keys the catalogue defines, in a stable order. Supplied filters compose
    /// conjunctively, so omitting both returns the whole catalogue.
    /// </returns>
    /// <response code="200">
    /// The keys, as a JSON array of strings. An empty array is a legitimate answer and means nothing
    /// matched the supplied filters; it is never reported as a failure.
    /// </response>
    /// <response code="400">
    /// Either a query value could not be bound to its parameter type - which the shared problem-details
    /// factory reports as a validation document naming the offending parameter - or the application
    /// contract refused a filter it cannot use, such as a <paramref name="moduleDefinitionId"/> that
    /// cannot name a row in its table. Both bodies are RFC 7807 problem documents.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses.
    /// </response>
    /// <remarks>
    /// <para>
    /// This one action replaces the four legacy single-column catalogue reads; the head of this file
    /// records which, and why the module-scoped and page-scoped filters have no counterpart.
    /// </para>
    /// <para>
    /// The outcome is translated by the shared translator rather than by a table written here, so every
    /// endpoint in this API answers the same failure code with the same status. Under that translator a
    /// successful outcome carrying no value at all means "asked, and it is not there" and becomes a
    /// <c>404</c>.
    /// </para>
    /// <para>
    /// MIGRATION: for this endpoint that <c>404</c> is unreachable, and its absence from the declared
    /// responses above is a decision rather than an oversight. A catalogue read answers with a sequence,
    /// and the contract specifies an empty sequence - never a null one - when nothing matches. So the
    /// choice recorded here is: no match is <c>200</c> with an empty array, matching the legacy reads,
    /// which returned an empty collection rather than a missing one. The legacy single-object read at
    /// <c>PermissionController.vb:L30</c> could return <see langword="null"/>, but it has no counterpart
    /// on the application contract and no endpoint here, so it cannot reach this path.
    /// </para>
    /// <para>
    /// Paging is deliberately absent. The catalogue is small, bounded reference data seeded by the
    /// upgrade scripts, so the whole sequence is returned rather than a page of it.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<string>>> ListAsync(
        [FromQuery] string? permissionCode,
        [FromQuery] int? moduleDefinitionId,
        CancellationToken cancellationToken)
    {
        // The filters are passed through exactly as bound. Trimming, upper-casing or discarding a blank
        // code here would be a second, quieter copy of the contract's own validation rule, and the two
        // copies would eventually disagree about what a blank filter means.
        Result<IReadOnlyList<string>> outcome = await _permissions
            .GetPermissionKeysAsync(permissionCode, moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its value
        // touched, and maps the failure code to a status through the one table this API uses.
        return this.Complete(outcome);
    }
}
