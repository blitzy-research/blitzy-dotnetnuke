// MIGRATION: this controller replaces the two legacy profile-property-definition screens in full -
// Website/admin/Users/ProfileDefinitions.ascx.vb (609 lines, the management grid) and
// Website/admin/Users/EditProfileDefinition.ascx.vb (473 lines, the add-and-edit wizard). Every
// workflow reachable from those two screens is accounted for below, either as one of the five
// actions or as a deliberate non-port with its reason attached. This is the one definition-shaped
// resource in the API that is FULL CRUD, and that is measured rather than assumed: a legacy
// administration screen exists and offers add, edit, delete and reorder. Contrast
// ModuleDefinitionsController and PermissionsController, which stay read-only precisely because no
// such screen exists for them - module definitions come from the excluded installer and permission
// definitions come from the upgrade scripts.
//
// MIGRATION: the legacy add-versus-update DISCRIMINATOR IS NOT REPRODUCED, and its removal is the
// single most consequential change in this file. EditProfileDefinition.ascx.vb decided which
// operation to perform by testing the identifier against an integer sentinel: L250 and L256 read
// `If PropertyDefinitionID = Null.NullInteger Then IsAddMode = True`, and L449 through L460 branched
// `If PropertyDefinitionID = Null.NullInteger Then ... AddPropertyDefinition(...) Else
// UpdatePropertyDefinition(...)`. Null.NullInteger is -1
// (Library/Components/Shared/Null.vb:L41-L45). Here the HTTP METHOD is the discriminator: POST to the
// collection creates, PUT to a member updates. No action accepts -1 as an identifier, none emits -1
// as an identifier, and none tests an identifier against -1 or 0. That prohibition is not fastidious:
// -1 is genuinely overloaded - the portal table is IDENTITY (-1, 1), so -1 is its seed and first
// generated value while the shipped default portal row is inserted explicitly with PortalID 0, making
// BOTH real portal keys, and Globals.vb:L95-L98 spends -1 through -4 on the pseudo-role names - while
// the role, page and module tables all seed at 0, so 0 is a real identifier there too. Consequently no lower bound and no
// range constraint is placed on any identifier below.
//
// MIGRATION: the same sentinel also carried the DUPLICATE-NAME outcome, and that channel is replaced
// too. EditProfileDefinition.ascx.vb:L451 assigned the add's return value to the identifier and L453
// tested `If PropertyDefinitionID < Null.NullInteger` - a value below -1 - to raise its DuplicateName
// message at L454. Encoding a failure inside a returned identifier is exactly the idiom a result
// carrying a reason code replaces: the application contract reports
// `profile-definition.duplicate-name` and the shared translator turns it into 409 Conflict. The
// caller-facing wording stays the legacy wording, which lives with the rule in the application layer
// rather than here.
//
// MIGRATION: REORDERING IS A PROPERTY, NOT AN ACTION, and there is deliberately no endpoint of its
// own for shifting a definition up or down the list. The legacy grid's Up and Dn commands - the two
// arrow columns declared at ProfileDefinitions.ascx:L19-L20 - were not a distinct operation on the
// server: MoveProperty in ProfileDefinitions.ascx.vb swapped the ViewOrder values of two adjacent
// definitions in memory and then persisted whatever had become dirty through the ordinary update call
// at L295. ViewOrder is a first-class member of the definition, so PUTting a definition with a new
// ViewOrder is the same operation the legacy screen performed, expressed once instead of twice.
// Inventing a dedicated shift endpoint would add a contract the legacy application never had and
// would give the display order two writers that could disagree.
//
// MIGRATION: the BULK "apply changes" post-back is not reproduced as a bulk endpoint. The legacy
// Apply button walked the whole collection and called the single-definition update for each dirty row
// (ProfileDefinitions.ascx.vb:L295), including the inline Required and Visible checkbox columns
// declared at ProfileDefinitions.ascx:L32-L33. That is a client-side loop over a single-record
// operation, and it stays one: the client issues one PUT per changed definition. No batch action
// exists here to hide a partial failure behind a single status code.
//
// MIGRATION: DataType is CARRIED THROUGH UNTOUCHED. EditProfileDefinition.ascx.vb:L69-L81 resolved
// the data type through `ListController.GetListEntryInfo(PropertyDefinition.DataType)` - the Lists
// subsystem under Library/Components/Lists, which this migration excludes - and used the resolved
// entry only to decide whether to show a list-entry editing step. The stored value is a foreign key
// into that table and is transported verbatim: this controller does not resolve it, does not
// enumerate it, does not validate it against a closed set, and no data-type enumeration exists for it
// to be validated against. An installation carrying a data type this codebase has never seen still
// round-trips intact.
//
// MIGRATION: LOCALISATION IS NOT PORTED. The legacy screens resolved every user-facing string, and
// the per-definition display name and help text, from resource keys of the form
// `ProfileProperties_<PropertyName>`, `...Help` and `...Header` against the portal resource files -
// read at EditProfileDefinition.ascx.vb:L171-L173 and written back at L327-L329. That mechanism is
// Web Forms specific, the localisation subsystem is excluded from scope, and the client takes no
// translation runtime. Wording is authored in the client, sourced from the legacy resource files for
// continuity. No resource key is constructed here and no localisation endpoint exists.
//
// MIGRATION: the wizard's third step is gone with it. EditProfileDefinition.ascx:L36-L75 declared a
// Localization step whose only purpose was editing those resource entries, and L33-L35 declared a
// List step that reached into the excluded Lists subsystem. Neither has a target endpoint. The first
// step - the definition itself - is what POST and PUT carry.
//
// MIGRATION: reflection-based data access is gone. The legacy screens reached their data through the
// static ProfileController, which reached DataProvider.Instance() - a reflection-instantiated
// singleton - and hydrated rows through the reflection helper CBO. Both are replaced by the single
// constructor-injected application-layer contract below. This controller performs no data access of
// its own, holds no persistence type, and names no repository.

using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The profile property definition resource - which profile fields a portal collects, and how - exposed at
/// <c>/api/v1/profile-definitions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Definitions are schema, not data.</strong> A user's profile is a set of values keyed by
/// definition, so changing a definition changes the shape of every profile in the portal and
/// removing one discards what every account recorded against it. That is why this is an
/// administrative resource in its own right, addressed separately from the profiles it governs, and
/// why deleting a definition is a different act from clearing a value.
/// </para>
/// <para>
/// <strong>Tenant-scoped always, and never by a query value.</strong> Definitions belong to a portal -
/// the legacy read was tenant-scoped at <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L133 and
/// the legacy editor passed the tenant alongside the definition identifier - so every operation here
/// requires a tenant and none of them accepts one in the query string.
/// </para>
/// <para>
/// <strong>One canonical address.</strong> All five actions use
/// <c>/api/v1/profile-definitions</c> and the tenant resolved from the request host. No route or query
/// member can substitute another portal. A host account administers another tenant by addressing that
/// tenant's own alias.
/// </para>
/// <para>
/// <strong>Distinct from a user's profile VALUES.</strong> Reading or writing what one account holds
/// is <c>/api/v1/users/{userId}/profile</c>, on the account resource. Nothing here touches
/// a value, and no endpoint here duplicates that sub-resource: a caller rendering a profile form
/// receives each value together with its definition, so the form needs no second request to this
/// controller.
/// </para>
/// <para>
/// <strong>Asks, never decides.</strong> Each action below validates nothing of its own, delegates to
/// the application contract, and translates the outcome into a status code through the one shared
/// translator this API uses. There is no display-order arithmetic here, no judgement about whether a
/// property ought to be required, no data-type resolution and no resource-key construction - every
/// such rule lives with the contract that owns it, so none of them can drift apart from a second
/// copy kept here.
/// </para>
/// <para>
/// <strong>Administrator-gated at the class, and it fails closed.</strong> The policy is
/// <see cref="PolicyNames.PortalAdministrator"/> and it guards the reads as well as the writes,
/// because the legacy screens were guarded whole: reaching either one at all required
/// <c>PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)</c>, so the grid read was as
/// privileged as the Apply button. Leaving the reads open to any authenticated caller would let a
/// member enumerate the portal's profile schema, which the legacy application never permitted. It is
/// also the only correct policy available: the module and page policies resolve their scope from a
/// module or page identifier in route data, and this route carries a profile-definition identifier
/// instead, so either of those could only ever refuse.
/// </para>
/// <para>
/// MIGRATION: the legacy gate answered a refusal with
/// <c>Response.Redirect(NavigateURL("Access Denied"), True)</c>. A JSON API has no page to redirect
/// to, so the target answers a plain <c>403 Forbidden</c> from the authorisation middleware and the
/// redirect is not reproduced. A caller presenting no credential at all receives
/// <c>401 Unauthorized</c> - a distinction the single legacy redirect could not express.
/// </para>
/// <para>
/// <strong>How outcomes become statuses.</strong> The shared translator reads a successful outcome
/// whose value is <see langword="null"/> as "asked, and it is not there" and answers <c>404</c>,
/// which is how the by-identifier read reports an unknown definition without the contract having to
/// raise a failure for it. Failure reason codes map by their final segment, so
/// <c>profile-definition.not-found</c> becomes <c>404</c> and both
/// <c>profile-definition.duplicate-name</c> and <c>persistence.conflict</c> become <c>409</c>. The
/// mapping lives in one place for the whole API rather than in a table written here, so no two
/// endpoints can answer the same code differently.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/profile-definitions")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
[Produces("application/json")]
public sealed class ProfileDefinitionsController : ControllerBase
{
    /// <summary>
    /// Failure code carried as the problem type when the request reached this action without a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A distinct code from the middleware's generic refusal, so an operator reading a support log can tell
    /// "this host resolves to no portal" apart from "this caller lacks the grant" - while the caller reads
    /// the same fixed wording either way and learns nothing from the difference. The same constant is
    /// declared by <c>PortalAliasResolutionMiddleware</c> and by the other controllers that guard on tenant
    /// resolution, because the two paths must be indistinguishable to a client.
    /// </para>
    /// <para>
    /// MIGRATION: the tenant guards in this controller answered with a bare <c>Forbid()</c>. A controller's
    /// <c>Forbid()</c> does NOT pass through the authorisation middleware's result handler, so it produced a
    /// 403 with an EMPTY BODY while every action here declares a problem document for 403 - the response
    /// contradicted its own declaration, and it was distinguishable from the middleware's refusal for the
    /// identical cause. Routing the refusal through the shared helper closes both gaps at once.
    /// </para>
    /// </remarks>
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>The application contract that owns profile definitions.</summary>
    /// <remarks>
    /// Definitions live on the account contract rather than on one of their own, because they are
    /// meaningless apart from the profiles they shape and every rule about them - name uniqueness,
    /// length, the cascade that discards values when a definition is removed - is a rule about
    /// accounts. One dependency, and it is an application-layer interface: there is no repository, no
    /// persistence context and no evaluator here. The first two are unreachable by ACCESSIBILITY rather
    /// than by the reference graph - this project does reference <c>DnnMigration.Infrastructure</c> so
    /// that composition can register its services, but <c>DnnDbContext</c> and every repository
    /// implementation are <see langword="internal"/> to that assembly, so naming one here would not
    /// compile.
    /// </remarks>
    private readonly IUserService _users;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    /// <remarks>
    /// The flat address carries no tenant identifier and therefore reads the one the alias-resolution
    /// middleware already resolved. It is an abstraction over that resolution
    /// rather than a reach for the ambient HTTP context: this file performs no alias lookup and touches no
    /// request feature bag.
    /// </remarks>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="ProfileDefinitionsController"/> class.</summary>
    /// <param name="users">The account service, which owns profile definitions.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host, which is the
    /// tenant the resource acts on.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public ProfileDefinitionsController(IUserService users, IPortalContextHolder portalContext)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>
    /// Returns the tenant the request resolved to.
    /// </summary>
    /// <returns>
    /// The tenant identifier, or <see langword="null"/> when the request resolved to no tenant.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The holder throws rather than yielding a placeholder tenant, so resolution is tested before the
    /// tenant is read, and the null answer here is a precondition rather than a state a caller can steer
    /// into.
    /// </para>
    /// <para>
    /// MIGRATION: WHAT REFUSES FIRST DEPENDS ON THE CALLER. The tenant-resolution middleware records an
    /// unresolved host and continues. A portal administrator is then refused by the policy with
    /// <c>auth.not_permitted</c>; a superuser passes that policy because host authority is installation-wide
    /// and is refused by this guard with <c>portal.tenant_unresolved</c>. Either way the
    /// action never runs, so this guard is defence in depth and is expected to be unreachable; it stays
    /// because the alternative to an unreachable refusal is the holder throwing, and a <c>500</c> is a worse
    /// answer than a <c>403</c> for a condition that is not the caller's fault.
    /// </para>
    /// <para>
    /// MIGRATION: THE REFUSAL IS NOT A BARE <c>403</c>. A controller's <c>Forbid()</c> does not pass through
    /// the authorisation middleware's result handler, so it produces an EMPTY body while every action here
    /// declares a problem document for <c>403</c>. The guard therefore answers through the shared
    /// problem-details path carrying the same failure code the middleware uses, so the two refusals are
    /// indistinguishable to a client keying on that code.
    /// </para>
    /// </remarks>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>Lists a portal's profile property definitions, in display order.</summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal's definitions, ordered by display order.</returns>
    /// <response code="200">
    /// The definitions, inside the shared success envelope: the sequence is the envelope's payload rather
    /// than the whole body. An empty payload is a legitimate answer and means the portal declares none; it
    /// is never reported as a failure, which matches the legacy read at
    /// <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L133 returning an empty collection rather
    /// than a missing one.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <remarks>
    /// <para>
    /// The sequence is ordered by the display-order column on the way out, so the client never sorts
    /// it and the order shown always matches the order stored.
    /// </para>
    /// <para>
    /// MIGRATION: deliberately unpaged, and the <c>404</c> the shared translator can produce is
    /// unreachable here. The legacy grid listed every definition on one page with no pager at all, a
    /// portal declares definitions in the tens, and the contract promises an empty sequence rather
    /// than a null one - so nothing matched is <c>200</c> with an empty payload. Introducing paging
    /// would add a contract the legacy application never had.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProfilePropertyDefinitionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProfilePropertyDefinitionDto>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await _users
            .ListProfilePropertyDefinitionsAsync(scopedPortalId, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its
        // value touched, and maps any failure code through the one table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Retrieves one profile property definition.</summary>
    /// <param name="propertyDefinitionId">
    /// Identifier of the definition to read. Forwarded exactly as bound and never compared against a
    /// sentinel; see the note at the head of this file on why no range constraint appears here.
    /// </param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    /// <response code="200">The definition.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">
    /// The portal declares no definition with that identifier. Tenant scoping is part of the
    /// question, so a definition that exists in another portal is reported as absent here rather than
    /// returned.
    /// </response>
    /// <remarks>
    /// The contract reports an unknown definition as a SUCCESSFUL outcome carrying no value rather
    /// than as a failure, because being asked for something that is not there is not an error on the
    /// server's part. The shared translator reads that absent value as <c>404 Not Found</c>, which is
    /// the whole of how this action produces that status - there is no null test written here.
    /// </remarks>
    [HttpGet("{propertyDefinitionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ProfilePropertyDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ProfilePropertyDefinitionDto?>>> GetAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ProfilePropertyDefinitionDto?> outcome = await _users
            .GetProfilePropertyDefinitionAsync(scopedPortalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Declares a new profile property definition for a portal.</summary>
    /// <param name="request">
    /// The definition to declare. It carries NO identifier member - the store assigns one, which is
    /// precisely why the method rather than a sentinel distinguishes this from an update - and no
    /// owning-portal member, because the tenant is resolved from the request host.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created definition, addressed by the location header.</returns>
    /// <response code="201">
    /// The definition as persisted, including its assigned identifier, with its address in the
    /// location header.
    /// </response>
    /// <response code="400">
    /// The body failed validation, or a value could not be bound. Both arrive as RFC 7807 documents:
    /// the automatic model-state check and the declarative rule set both report through the one shared
    /// problem-details factory, so a caller sees the same shape either way.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="409">
    /// The portal already declares a property of that name. Names must be unique within a portal.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: this action is the whole of the legacy add path. The screen chose to add by testing
    /// its identifier against -1 at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L449 and reported a name collision by
    /// testing the returned identifier for a value below -1 at L453; here POST expresses the intent
    /// and the contract's <c>profile-definition.duplicate-name</c> code expresses the collision,
    /// which the shared translator answers with <c>409</c>. Neither the intent nor the failure travels
    /// inside an integer any more.
    /// </para>
    /// <para>
    /// MIGRATION: this action binds a REQUEST contract rather than the response projection it returns.
    /// The terminal insert procedure <c>AddPropertyDefinition</c> (<c>04.06.00:L1101</c>) declares
    /// eleven parameters, ten of which are body members, and the module definition key among them is
    /// the member the update verb does not have - which is why one shared shape could not describe both
    /// verbs honestly. The response projection is not bound here, because it advertises an identifier, an
    /// owning portal and a visibility hint that this action does not read - members better absent from the
    /// request schema than present and ignored.
    /// </para>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ProfilePropertyDefinitionDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<ProfilePropertyDefinitionDto>>> CreateAsync(
        [FromBody] CreateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ProfilePropertyDefinitionDto> outcome = await _users
            .CreateProfilePropertyDefinitionAsync(scopedPortalId, request, cancellationToken)
            .ConfigureAwait(false);

        // The location header is built from this request's own path plus the assigned identifier,
        // which is the member address of the collection just posted to. The identifier is read from
        // the persisted representation rather than from the submitted body, because the store assigns
        // it.
        return this.Created(outcome, created => created.PropertyDefinitionId);
    }

    /// <summary>Updates an existing profile property definition, including its display order.</summary>
    /// <param name="propertyDefinitionId">Identifier of the definition to update.</param>
    /// <param name="request">
    /// The new state of the definition. It carries no identifier member - the definition is named by the
    /// route - and no module definition key, because the terminal update procedure does not write that
    /// column.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The definition as persisted.</returns>
    /// <response code="200">The definition as persisted.</response>
    /// <response code="400">The body failed validation, or a value could not be bound.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">The portal declares no definition with that identifier.</response>
    /// <response code="409">
    /// The portal already declares a different property of that name, or the definition was changed
    /// by someone else after it was read.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: this action is also the whole of the legacy REORDER path, which is why no move
    /// endpoint exists. The grid's Up and Dn commands swapped the display-order values of two
    /// adjacent definitions and then persisted each through this same operation
    /// (<c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L295), so moving a property is a PUT
    /// carrying a new display order - one call per definition whose order changed, exactly as the
    /// legacy screen issued one update per dirty row.
    /// </para>
    /// <para>
    /// MIGRATION: the same is true of the grid's inline Required and Visible checkboxes
    /// (<c>ProfileDefinitions.ascx</c> L32-L33), which posted back on change. They are ordinary
    /// members of the definition, so toggling one is this action too. No partial-update endpoint is
    /// introduced for them: the definition is small and a whole-representation PUT cannot leave two
    /// writers disagreeing about which members a request meant to change.
    /// </para>
    /// <para>
    /// MIGRATION: this action binds a REQUEST contract distinct from the create verb's, because the
    /// terminal procedures differ. <c>UpdatePropertyDefinition</c> (<c>04.05.00:L1685</c>) declares ten
    /// parameters, nine of which are body members, and it declares NO module definition key - so the
    /// request contract for this verb does not carry one rather than accepting and discarding it. It does,
    /// however,
    /// assign <c>PropertyName</c>, so renaming a definition is a supported edit and a rename onto a name
    /// another definition of the same portal already holds is reported as <c>409</c>.
    /// </para>
    /// </remarks>
    [HttpPut("{propertyDefinitionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ProfilePropertyDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<ProfilePropertyDefinitionDto>>> UpdateAsync(
        int propertyDefinitionId,
        [FromBody] UpdateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // The identifier travels as a route value and the state travels in the body, and the body
        // carries no identifier member for the two to disagree about - which is one of the reasons the
        // write contract is separate from the projection this action returns. The pair is forwarded as
        // received and no member of the body is rewritten here.
        Result<ProfilePropertyDefinitionDto> outcome = await _users
            .UpdateProfilePropertyDefinitionAsync(
                scopedPortalId,
                propertyDefinitionId,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a profile property definition from a portal.</summary>
    /// <param name="propertyDefinitionId">Identifier of the definition to remove.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> once the definition has been removed.</returns>
    /// <response code="204">The definition has been removed.</response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but is not an administrator of the portal this request resolves to.
    /// </response>
    /// <response code="404">The portal declares no definition with that identifier.</response>
    /// <response code="409">
    /// A concurrent request changed or removed the definition between this request's read and its commit,
    /// so the deletion affected no rows (<c>persistence.conflict</c>). The caller lost a race rather than
    /// submitting anything wrong, and the outcome it asked for may already have happened, so the correct
    /// answer is to reload and decide - not to retry blindly.
    /// </response>
    /// <remarks>
    /// <para>
    /// Removal discards the values accounts hold against the definition in the same unit of work, so
    /// no value is left referencing a definition that no longer exists. That cascade is the contract's
    /// responsibility and is deliberately not staged from here as a sequence of calls, which could
    /// leave the two halves apart if the second failed.
    /// </para>
    /// <para>
    /// MIGRATION: the conflict above was UNDECLARED. The application contract documents
    /// <c>persistence.conflict</c> for this operation and the service returns it from a guarded commit, and
    /// the shared translator answers it as <c>409</c> - but this action advertised only 204, 401, 403 and
    /// 404, so a real, reachable response was absent from the published contract and a generated client had
    /// no case for it. A comment in the service asserted that "the endpoint's declared conflict response
    /// becomes reachable rather than notional", which was true of the reachability and false about the
    /// declaration; both are corrected.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy application offered this from two places - the grid's row command at
    /// <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L161 and the editor's delete button at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L302 - both calling the same
    /// underlying operation. One endpoint serves both, and the client's confirmation prompt replaces
    /// the post-back confirmation.
    /// </para>
    /// </remarks>
    [HttpDelete("{propertyDefinitionId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    // The guarded commit in the application contract returns persistence.conflict when a concurrent request
    // changed or removed the definition first, and the shared translator answers that code as 409. Declaring
    // it is not optional: an undeclared reachable status is a case a generated client has no branch for.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } scopedPortalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _users
            .DeleteProfilePropertyDefinitionAsync(scopedPortalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // The valueless overload answers 204 on success, which is the documented contract for a
        // removal: the resource is gone, so there is nothing to return in its place.
        return this.Complete(outcome);
    }
}
