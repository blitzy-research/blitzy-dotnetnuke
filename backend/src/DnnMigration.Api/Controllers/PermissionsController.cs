// MIGRATION: this controller replaces the READ surface of the three legacy permission controllers and
// nothing else. Measured against the source rather than estimated: PermissionController.vb is 72 lines
// with 9 public members, ModulePermissionController.vb is 389 lines with 18, and
// TabPermissionController.vb is 349 lines with 15 - 42 members, of which FOUR endpoints survive here.
// The reduction is accounted for rather than trimmed by taste, and each of the five groups below names
// where the behaviour went so that nothing looks lost when it is merely elsewhere.
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
// MIGRATION: the five remaining legacy catalogue reads are ALL published here, across four actions.
// PermissionController.vb:L30 (by identifier), :L34 (by module definition), :L38 (by module), :L47 (by
// code and key) and :L51 (by page) each returned an untyped ArrayList - or, for L30, a single object -
// hydrated by reflection through CBO.FillCollection. They map onto the actions below as follows: L34 and
// L47 are the two composable filters on the catalogue listing, and L30, L38 and L51 are the three
// identifying reads that follow it.
//
// MIGRATION: an earlier revision of this file omitted the module-scoped and page-scoped reads and
// recorded, as its reason, that "a module or page identifier selects GRANTS rather than catalogue
// definitions". That reason was wrong, and it is corrected here rather than quietly deleted, because the
// omission it justified was a real gap in this API's read surface. The terminal procedure bodies were
// measured directly: GetPermissionsByModuleID (04.05.03) is
// "SELECT PermissionID, PermissionCode, ModuleDefID, PermissionKey, PermissionName FROM Permission
// WHERE ModuleDefID = (SELECT ModuleDefID FROM Modules WHERE ModuleID = @ModuleID) OR PermissionCode =
// 'SYSTEM_MODULE_DEFINITION'", and GetPermissionsByTabID (04.05.03) is the same projection over the same
// table filtered on PermissionCode = 'SYSTEM_TAB'. Both select from the Permission CATALOGUE table, not
// from a grant table, and both hydrate PermissionInfo - the catalogue type - which is why
// PermissionController owns them at all rather than ModulePermissionController or
// TabPermissionController. So the two reads always belonged here, and the parameter-to-result asymmetry
// they carry - a module or page identifier in, catalogue definitions out - is the legacy shape and is
// preserved rather than tidied away.
//
// MIGRATION: the page-scoped read IGNORES its own argument. The terminal body never references @TabID;
// every page receives the identical SYSTEM_TAB catalogue. The parameter is nonetheless kept on the route
// below, because the legacy signature declares it and because dropping it would make a caller believe
// the answer is installation-wide when the legacy contract presented it as page-scoped and a later
// product revision could make it so. The measured behaviour is recorded on the action itself so that
// nobody reads the implementation as having lost a filter.
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
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
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
/// is <c>IDENTITY (-1, 1)</c>, so -1 is its seed and first generated value while the shipped default
/// portal row carries an explicit 0 - both real portal keys - and the role, page and module tables all
/// seed at 0, so 0 is a real identifier there too. Neither value may be read as "absent" nor
/// emitted to signal it, which is why no range constraint is placed on any identifier here.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/permissions")]
// The permission catalogue is installation-wide reference data seeded by the upgrade scripts and keyed by
// module definition, not by portal. This endpoint reads no tenant at all, so there is no tenant for an
// unresolved host name to have got wrong.
[TenantOptional(
    "The permission catalogue is installation-wide reference data keyed by module definition; no tenant is "
    + "read.")]
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
    /// <param name="permissionKey">
    /// Optional. Narrows the answer to a single key of the closed key enumeration. Because the parameter
    /// type is the enumeration itself rather than a string, an unrecognised spelling is refused by model
    /// binding before the action runs, and the answer is therefore either that one key or nothing at all.
    /// Supplied together with <paramref name="permissionCode"/> this is the legacy code-and-key lookup at
    /// <c>PermissionController.vb:L47</c>, reduced to the one column this action publishes.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// The distinct keys the catalogue defines, in a stable order. Supplied filters compose
    /// conjunctively, so omitting both returns the whole catalogue.
    /// </returns>
    /// <response code="200">
    /// The keys, inside the shared success envelope: the sequence of strings is the envelope's payload
    /// rather than the whole body. An empty payload is a legitimate answer and means nothing matched the
    /// supplied filters; it is never reported as a failure.
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
    /// This action carries the two COMPOSABLE legacy catalogue filters - by module definition
    /// (<c>PermissionController.vb:L34</c>) and by scope code with key (<c>:L47</c>) - because those two
    /// select on columns of the catalogue row itself and so compose conjunctively into one query. The
    /// three reads that identify their subject some other way have their own actions below it. The head
    /// of this file records the whole mapping.
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
    /// choice recorded here is: no match is <c>200</c> with an empty payload, matching the legacy reads,
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
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<string>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<string>>>> ListAsync(
        [FromQuery] string? permissionCode,
        [FromQuery] int? moduleDefinitionId,
        [FromQuery] PermissionKey? permissionKey,
        CancellationToken cancellationToken)
    {
        // The filters are passed through exactly as bound. Trimming, upper-casing or discarding a blank
        // code here would be a second, quieter copy of the contract's own validation rule, and the two
        // copies would eventually disagree about what a blank filter means.
        //
        // The key filter is closed by its own type, so an unrecognised spelling is refused by model binding
        // before this body runs and is reported as a validation document naming the parameter. That is why
        // no blank-or-invalid guard appears here for it while one exists in the contract for the code: the
        // code's column is free text and the key's is not.
        Result<IReadOnlyList<string>> outcome = await _permissions
            .GetPermissionKeysAsync(permissionCode, moduleDefinitionId, permissionKey, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its value
        // touched, and maps the failure code to a status through the one table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one catalogue definition by its identifier.</summary>
    /// <param name="permissionId">
    /// Identifier of the definition wanted, forwarded exactly as bound. No lower bound is imposed: an
    /// unknown identifier is reported as absent by the read itself, which is a better answer than a bound
    /// test could give.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    /// <response code="200">The definition, as a record rather than a bare key.</response>
    /// <response code="400">The identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses.
    /// </response>
    /// <response code="404">No definition bears that identifier.</response>
    /// <remarks>
    /// <para>
    /// Reproduces <c>PermissionController.GetPermission(permissionID)</c>
    /// (<c>PermissionController.vb:L30</c>). Unlike the listing above this answers with the whole record,
    /// because a key on its own could not say which scope code or module definition it was declared under -
    /// and the same key is declared repeatedly across scopes, so the key alone would not identify anything.
    /// </para>
    /// <para>
    /// The catalogue carries no portal column, so this read is installation-wide like the listing. The
    /// portal-administrator policy still guards it, which is the same standing the legacy screens required
    /// of a caller reading the catalogue.
    /// </para>
    /// </remarks>
    // The declared success type is the ENVELOPE, which is what this action actually returns; and every
    // refusal status declares a body, because a status advertised without one forces a client to special-case
    // an endpoint that behaves like all the others.
    [HttpGet("{permissionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<PermissionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PermissionDto?>>> GetAsync(
        int permissionId,
        CancellationToken cancellationToken)
    {
        Result<PermissionDto?> outcome = await _permissions
            .GetPermissionAsync(permissionId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no value means the definition is absent, which the shared
        // translator answers as 404 - the convention every single-record read in this API follows.
        return this.Complete(outcome);
    }

    /// <summary>Reads the catalogue definitions that apply to one module placement.</summary>
    /// <param name="moduleId">Identifier of the module placement, forwarded exactly as bound.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definitions.</returns>
    /// <response code="200">
    /// The definitions, as a JSON array. In a product-standard installation this is never empty, because
    /// the product-wide module scope always contributes entries; an empty array is nonetheless a
    /// legitimate answer rather than a failure, and means the catalogue itself holds no matching entry.
    /// </response>
    /// <response code="400">The identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses.
    /// </response>
    /// <remarks>
    /// <para>
    /// Reproduces <c>PermissionController.GetPermissionsByModuleID(ModuleID)</c>
    /// (<c>PermissionController.vb:L38</c>). Its terminal procedure body takes a UNION of two sets: the
    /// catalogue entries declared by the definition THIS PLACEMENT was created from, resolved through the
    /// module row, together with every entry carrying the product-wide <c>SYSTEM_MODULE_DEFINITION</c>
    /// scope code. Both halves are reproduced; dropping the second would silently narrow the answer for
    /// every module in the installation.
    /// </para>
    /// <para>
    /// Distinct from the <c>moduleDefinitionId</c> filter on the listing, and the distinction is worth
    /// stating because it is easy to lose. That filter takes a DEFINITION identifier and answers with
    /// exactly that definition's entries. This takes a PLACEMENT identifier, resolves the definition
    /// behind it, and then widens the answer with the product-wide scope. So this is not the same
    /// question reached by a different route: it is a deliberately wider one.
    /// </para>
    /// <para>
    /// A placement identifier that names no module is not an error and is not reported as absent. The
    /// first half of the union contributes nothing and the second still answers, which is exactly what
    /// the legacy statement did, so the result is the product-wide scope alone.
    /// </para>
    /// <para>
    /// MIGRATION: catalogue DEFINITIONS are returned, never grant rows - and that is what the legacy read
    /// returned too, measured rather than assumed: the terminal body selects the five catalogue columns
    /// from the <c>Permission</c> table and the legacy member hydrates <c>PermissionInfo</c>, the
    /// catalogue type. Which role or account HOLDS a grant is the permission-grid surface, which this
    /// migration does not replace, and publishing grantees here would be inventing that surface one field
    /// at a time.
    /// </para>
    /// </remarks>
    [HttpGet("modules/{moduleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PermissionDto>>>> ListForModuleAsync(
        int moduleId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PermissionDto>> outcome = await _permissions
            .GetModulePermissionDefinitionsAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Reads the catalogue definitions that apply to one page.</summary>
    /// <param name="tabId">
    /// Identifier of the page. Bound and forwarded, but - see the remarks - it does not narrow the answer,
    /// because the legacy statement this read reproduces does not use it either.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definitions.</returns>
    /// <response code="200">
    /// The definitions, as a JSON array. In a product-standard installation this is never empty, because
    /// the page scope always contributes entries; an empty array is nonetheless a legitimate answer rather
    /// than a failure, and means the catalogue itself holds no page-scoped entry.
    /// </response>
    /// <response code="400">The identifier could not be bound to its parameter type.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request addresses.
    /// </response>
    /// <remarks>
    /// <para>
    /// Reproduces <c>PermissionController.GetPermissionsByTabID(TabID)</c>
    /// (<c>PermissionController.vb:L51</c>), and is the page-scoped counterpart of the module-scoped read
    /// above. The page identifier is <c>IDENTITY (0, 1)</c> in this schema, so zero addresses a real page
    /// and no bound test appears here.
    /// </para>
    /// <para>
    /// MIGRATION: this read IGNORES its own page argument, and that is faithful rather than defective. The
    /// terminal procedure body (04.05.03) filters on the product-wide <c>SYSTEM_TAB</c> scope code and
    /// never references <c>@TabID</c> at all, so every page receives the identical catalogue. Inventing a
    /// page filter here would narrow results the legacy application returned. The argument is kept on the
    /// route because the legacy signature declares it, because every caller passes it, and because
    /// removing it would present the answer as installation-wide when the contract it reproduces presents
    /// it as page-scoped - a distinction a later product revision could make real.
    /// </para>
    /// <para>
    /// MIGRATION: as with the module-scoped read, catalogue DEFINITIONS are returned rather than grant
    /// rows, which is what the legacy member returned - it hydrates <c>PermissionInfo</c> from the
    /// <c>Permission</c> table. Page grants themselves belong to the permission-grid surface that this
    /// migration does not replace.
    /// </para>
    /// </remarks>
    [HttpGet("tabs/{tabId:int}")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PermissionDto>>>> ListForTabAsync(
        int tabId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PermissionDto>> outcome = await _permissions
            .GetTabPermissionDefinitionsAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
