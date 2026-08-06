// MIGRATION: three legacy controllers collapse into this one application contract.
// Library/Components/Security/Permissions/PermissionController.vb declares 9 public members across 71
// lines, ModulePermissionController.vb declares 18 across 389 lines and TabPermissionController.vb
// declares 15 across 349 lines - 42 measured public members, counted against the declaration form of a
// public function, sub or property. Eight members remain here, and the 42-to-8 reduction is accounted
// for exactly rather than trimmed by taste:
//
//   13 of the 42 carry an explicit deprecation attribute in the legacy source itself - 7 in the module
//      controller and 6 in the tab controller - and most are near-identical read overloads differing
//      only in whether the caller had already materialised the rows. Re-creating API that its own
//      author withdrew would be a regression, so none is ported. Two are worth naming because they are
//      not merely redundant: ModulePermissionController.vb:L355-L374 existed to swap sequences of role
//      identifiers for sequences of role names, and L376-L381 was deprecated in favour of the
//      three-argument access check.
//    6 module-scoped reads (ModulePermissionController.vb L239, L254, L308, L327, L345, L350) and
//      7 tab-scoped reads (TabPermissionController.vb L214, L229, L298, L303, L321, L334, L339)
//      existed only to dodge a second query, which is the caching abstraction's responsibility now.
//      They collapse into the two effective-key reads below.
//    5 answered "may this caller do X?" and are represented by the two decision members below rather
//      than by five overloads: ModulePermissionController.vb L33, L52 and the deprecated L377, and
//      TabPermissionController.vb L33 and L38.
//    3 write members on the catalogue itself (PermissionController.vb L55, L59, L63) are not ported:
//      catalogue rows are reference data seeded by the upgrade scripts and written only during module
//      installation, which is outside the scope of this migration.
//    1 folder-scoped lookup (PermissionController.vb:L43, GetPermissionsByFolder) is not ported: the
//      file-management subsystem it serves is excluded, so the folder grant table has no target
//      feature. The catalogue rows that subsystem relies on still exist and a catalogue read here may
//      legitimately return them; only the lookup keyed by a folder path disappears.
//    5 are catalogue reads and ALL FIVE are published, across four members. PermissionController.vb:L34
//      (by module definition) and :L47 (by scope code and key) select on columns of the catalogue row
//      itself, so they compose conjunctively and become the filters on the key read below. :L30 (by
//      identifier), :L38 (by module placement) and :L51 (by page) identify their subject some other way
//      and so become the three definition reads at the end of this contract.
//    2 remove a single user's direct grants - ModulePermissionController.vb:L218 and
//      TabPermissionController.vb:L209 - and consolidate into the one cascade member below, because a
//      caller deleting an account must never be able to clear one grant table and forget the other.
//
// 13 + 13 + 5 + 3 + 1 + 5 + 2 = 42. Every legacy member is placed.
//
// MIGRATION: the three identifying catalogue reads were absent from an earlier revision of this
// contract, which recorded as its reason that a module or page identifier "selects grants rather than
// catalogue definitions". That reason was measured and found false - both terminal procedure bodies
// select the five catalogue columns from the Permission table, and both legacy members hydrate
// PermissionInfo, the catalogue type - so the reads are published here and the corrected reasoning is
// kept on each member rather than the mistaken one being quietly dropped.
//
// MIGRATION: the grant-management surface is deliberately NOT on this contract, and its absence is a
// scope decision rather than an omission. The planned API surface gives permissions exactly one
// controller - a read-only catalogue at /api/v1/permissions - and the administration screens in scope
// are portal, module, user and role management; no endpoint and no screen replaces the legacy
// permission grid. Publishing a replace-the-grid member here would therefore be application surface
// that nothing calls, and a contract member that nothing calls is a contract member nobody validates.
// The grant rows themselves remain fully modelled - the module and page grant entities and their
// repository reads exist in the domain and infrastructure layers - so a later phase that adds the grid
// screen adds an endpoint over data that is already there.
//
// MIGRATION: the access decision IS on this contract, and it is here because there is exactly one
// authorised route by which the api layer can reach it. The policy handler under Api/Authorization/
// must ask something whether a caller holds a permission. The evaluation itself is centralised in
// Infrastructure/Security/PermissionEvaluator.cs, exactly as planned, and it is reached from here
// through the domain layer's permission repository - so there is ONE evaluator, not two, and this
// contract delegates to it rather than re-deriving it. The prohibition that matters is therefore not
// "no decisions here" but "no second evaluator anywhere": an implementer of this interface must not
// re-implement allow-and-deny precedence, and must call the repository members that do.
//
// MIGRATION: no DTO type appears on this contract. Permissions cross this boundary as their persisted
// key strings, which is the representation every consumer already uses: the token issuer takes
// IReadOnlyList<string> permissionKeys, the current-user contract publishes
// IReadOnlyList<string> Permissions, and the Angular hasPermission directive matches on the same
// strings. Introducing a permission DTO group would add contracts outside the planned inventory to
// describe data that is already fully described by the closed PermissionKey enumeration and the
// catalogue's own key column.
//
// MIGRATION: the semicolon-delimited permission string is gone. Three legacy members flattened a set
// of grants into one string value - ModulePermissionController.vb:L239-L252 and
// TabPermissionController.vb:L214-L227 each emit a leading semicolon followed by every granted role
// name and then every granted user identifier, and the deprecated TabPermissionController.vb:L303-L318
// does the same from a pre-materialised set. That encoding existed to push one value into one
// server-rendered control property; it was never a data contract. Every read here returns a structured
// read-only sequence, so a consumer never parses a string to learn who was granted what.
//
// MIGRATION: the bracketed pseudo-role encoding is gone. A legacy user-scoped grant was checked by
// synthesising the literal "[" + user identifier + "]" and passing it to the very same role-membership
// helper a real role name was passed to - ModulePermissionController.vb:L42 and
// TabPermissionController.vb:L47. In the terminal schema both identifier columns are nullable -
// 04.05.00.SqlDataProvider drops and recreates RoleID as nullable and adds a nullable UserID with a
// foreign key to the users table, on both grant tables - so a grant names a role or a single user, and
// the caller identity crosses this contract as a nullable user identifier rather than as a synthesised
// role name.
//
// MIGRATION: the ambient page read is gone. TabPermissionController.vb:L33-L36 accepted no scope
// argument at all: it reached into the current request's portal settings object and used whichever page
// that object happened to be pointing at, and its sibling at L39 re-read the same ambient object
// without using it. Every member below states its scope explicitly as a portal, page or module
// identifier supplied by the caller. The scoped, immutable portal-context abstraction owned by the
// domain layer remains available to the implementing service, but no member here infers scope from
// request state.
//
// MIGRATION: entity arguments become identifiers. ModulePermissionController.vb:L218 and
// TabPermissionController.vb:L209 each accept a whole legacy user object and then read exactly two
// fields from it, the portal identifier and the user identifier, before handing both to the data layer.
// No domain entity crosses this contract in either direction, so the consolidated cascade member below
// accepts those two identifiers directly. Reading the legacy call rather than the legacy signature
// matters here: the parameter list looks user-scoped, but the work performed is portal-scoped as well,
// and a contract accepting only a user identifier would silently widen the blast radius of the deletion
// to every portal that user belongs to.
//
// MIGRATION: an absent identifier is a null nullable, never a numeric sentinel. The legacy integer
// sentinel is defined at Library/Components/Shared/Null.vb:L41-L45 with the value -1, and
// ModulePermissionController.vb L266, L346 and L379 together with TabPermissionController.vb L241, L299
// and L335 all pass -1 to mean "no page". That value is not free: 01.00.00.SqlDataProvider:L77 declares
// Portals.PortalID as an identity column seeded at -1, so -1 is its first generated value and the
// shipped default portal row carries an explicit 0, both real portal keys;
// Roles.RoleID (L115), Tabs.TabID (L140) and Modules.ModuleID (L221) all seed at 0, making 0 a real
// identifier too; and Users.UserID (L98) seeds at 1. Every optional identifier here is therefore a
// nullable integer, and an implementation must never coalesce -1 or 0 into absence nor emit either to
// signal it.
//
// MIGRATION: the legacy pre-generics wrapper types produce no target type at all. The module and tab
// permission collection classes, and the untyped sequence and dictionary types they were built over,
// are replaced by read-only generic sequences. There is no target counterpart to either wrapper file and
// none should be created.

using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Reads the permission catalogue and resolves what a caller may do within a portal.
/// </summary>
/// <remarks>
/// <para>
/// This contract answers three questions and no others: <em>which permission keys exist?</em>,
/// <em>which of them does this caller hold here?</em> and <em>does this caller hold this one?</em> The
/// migration notes at the head of this file account for all 42 measured legacy members against the 5
/// declared below, including the members that are deliberately not ported and why.
/// </para>
/// <para>
/// <strong>One evaluator, not two.</strong> Allow-and-deny precedence is evaluated in exactly one place -
/// <c>Infrastructure/Security/PermissionEvaluator.cs</c>, reached from an implementation of this
/// interface through the domain layer's <c>IPermissionRepository</c>. An implementer must not re-derive
/// that precedence: two evaluators that can disagree is the single worst result available in this area,
/// so the decision members below delegate rather than compute. The api layer's
/// <c>Api/Authorization/PermissionAuthorizationHandler</c> depends on this interface, and the policy
/// catalogue in <c>Api/Authorization/PolicyNames.cs</c> names the policies it satisfies.
/// </para>
/// <para>
/// <strong>Deny beats allow.</strong> A grant row carries an allow-or-deny flag backed by a genuine
/// <c>bit NOT NULL</c> column, and a deny row suppresses a key even when another row allows it. The
/// legacy string builders at <c>ModulePermissionController.vb:L243</c> and
/// <c>TabPermissionController.vb:L218</c> filtered on that flag being set and so discarded deny rows
/// entirely, but they were building a control value rather than reading data. The effective-key reads
/// below return only what the caller genuinely holds after deny rows have been applied.
/// </para>
/// <para>
/// <strong>The client mirrors, the server enforces.</strong> The Angular <c>hasPermission</c> directive
/// consumes the same key strings to show or hide affordances, but that is presentation only and is never
/// the sole enforcement mechanism. Every endpoint decides independently of whatever the browser chose to
/// render.
/// </para>
/// <para>
/// <strong>Anonymous callers are a supported case.</strong> Every member that takes a caller identity
/// takes it as a nullable user identifier, and <see langword="null"/> means "not signed in". An
/// anonymous caller is evaluated against the portal's unauthenticated pseudo-role rather than being
/// rejected outright, which is what the legacy permission grids expressed by granting that role
/// directly. Anonymous is therefore a legitimate question with a legitimate answer, usually
/// <see langword="false"/>.
/// </para>
/// <para>
/// <strong>Result semantics.</strong> Every member returns a <see cref="Result"/> or a
/// <see cref="Result{T}"/>, so an <em>expected</em> outcome - a scope that does not exist, a malformed
/// filter, a key that is not in the catalogue - is reported as a failure reason rather than thrown.
/// Unexpected faults propagate as exceptions and are translated once at the api boundary. Failure codes
/// are lower-case, dotted and stable, in the form <c>permission.condition</c>; each member documents the
/// codes it can produce. A denial is <em>not</em> a failure: a decision member that succeeds with the
/// value <see langword="false"/> has answered the question correctly.
/// </para>
/// <para>
/// <strong>Paging is deliberately absent.</strong> The catalogue is small, bounded reference data seeded
/// by the upgrade scripts, and a caller's effective key set is bounded by the number of keys the
/// catalogue defines. Every read returns a complete read-only sequence rather than a page.
/// </para>
/// <para>
/// <strong>Registration and lifetime.</strong> One of the seven services registered by the application
/// layer's <c>AddApplication()</c> extension, as
/// <c>AddScoped&lt;IPermissionService, PermissionService&gt;()</c>, implemented by
/// <c>Application/Services/PermissionService.cs</c>. Scoped is required rather than convenient: the
/// implementation depends on the scoped permission repository and unit of work owned by the domain
/// layer, and a singleton would capture them across requests.
/// </para>
/// <para>
/// <strong>Implementer's checklist.</strong> Perform every read and write asynchronously and honour the
/// supplied cancellation token; never block on a task. Reach the database only through the domain
/// layer's repository and unit-of-work abstractions, never through a persistence context. Apply caching
/// through the domain layer's cache abstraction, using stable keys and explicit invalidation, and never
/// expose a cache concern on this surface. Never treat -1 or 0 as an absent identifier, never return a
/// domain entity, and never re-implement allow-and-deny precedence.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// Reads the permission keys the catalogue defines, optionally narrowed.
    /// </summary>
    /// <param name="permissionCode">
    /// Scope code a permission definition belongs to, matched exactly and case-insensitively, or
    /// <see langword="null"/> to place no restriction. The underlying column is free text rather than a
    /// closed set, so no enumeration exists for it and an installation carrying a code this codebase has
    /// never seen still round-trips intact.
    /// </param>
    /// <param name="moduleDefinitionId">
    /// Module definition whose declared permissions are wanted, or <see langword="null"/> to place no
    /// restriction. Definitions that belong to no module definition are matched only when this argument
    /// is <see langword="null"/>.
    /// </param>
    /// <param name="permissionKey">
    /// The one key the answer is narrowed to, or <see langword="null"/> to place no restriction. No value
    /// outside the enumeration reaches this member over HTTP - the parameter is typed as the enumeration and
    /// MVC's binder tests defined membership, unlike <paramref name="permissionCode"/>, whose column is free
    /// text. A CLR enumeration is nonetheless an integer at run time, so an undefined value is constructible
    /// by a caller that does not arrive over MVC, and this member tests membership itself for that reason.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the distinct, upper-cased
    /// keys in a stable order, and an empty sequence when nothing matches - an empty catalogue is a
    /// legitimate answer, never a failure. Fails with <c>permission.filter_invalid</c> when
    /// <paramref name="permissionCode"/> is supplied but blank, or when
    /// <paramref name="moduleDefinitionId"/> cannot be a valid key for its table; and with
    /// <c>permission.key_invalid</c> when <paramref name="permissionKey"/> is supplied but is not a
    /// defined member, which is the same code and the same treatment the two permission-evaluation
    /// members of this contract already give their non-nullable key parameters.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The filters compose conjunctively: each supplied filter narrows the result, and supplying none
    /// returns the whole catalogue, which is what backs the read-only permissions endpoint. This member
    /// replaces the four legacy catalogue reads that differed only in which single column they filtered
    /// on - <c>PermissionController.vb</c> at L34, L38, L47 and L51 - so nothing is lost and no new
    /// behaviour is invented.
    /// </para>
    /// <para>
    /// Supplying the code AND the key together is the exact shape of
    /// <c>PermissionController.GetPermissionByCodeAndKey</c> (<c>PermissionController.vb:L47</c>), which
    /// asked whether a particular key is declared within a particular scope. The answer is a sequence
    /// containing that key when it is, and an empty sequence when it is not, so a caller reads presence
    /// from the shape of the answer rather than from a boolean.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<string>>> GetPermissionKeysAsync(
        string? permissionCode = null,
        int? moduleDefinitionId = null,
        PermissionKey? permissionKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves every permission key a caller holds within a portal, optionally narrowed to one module
    /// or one page.
    /// </summary>
    /// <param name="portalId">
    /// The portal the question is asked within. Because <c>Portals.PortalID</c> is
    /// <c>IDENTITY (-1, 1)</c>, both -1 and 0 are genuine portals.
    /// </param>
    /// <param name="userId">
    /// The caller, or <see langword="null"/> for an anonymous caller, who is evaluated against the
    /// portal's unauthenticated pseudo-role.
    /// </param>
    /// <param name="moduleId">Module to restrict the answer to, or <see langword="null"/> to ignore module scope.</param>
    /// <param name="tabId">Page to restrict the answer to, or <see langword="null"/> to ignore page scope.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the distinct, upper-cased
    /// keys the caller holds, after deny rows have suppressed the keys they deny. An empty sequence is a
    /// legitimate answer and means the caller holds nothing in the requested scope. Fails with
    /// <c>permission.portal_not_found</c>, <c>permission.module_not_found</c>,
    /// <c>permission.tab_not_found</c> or <c>permission.user_not_found</c> when a named scope or caller
    /// does not exist.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is what populates the permission claims of an issued access token and the
    /// <c>Permissions</c> member of the current-user contract, and it is what the Angular directive
    /// mirrors. Supplying neither scope answers at portal level, which is the set an administration
    /// shell needs to decide which sections to offer.
    /// </para>
    /// <para>
    /// SUPPLYING BOTH SCOPES NAMES A PLACEMENT, and that is significant rather than merely additive.
    /// When a module inherits its view permission, its view key comes from the page it sits on; a module
    /// may sit on several pages with different grants, so "which page" is part of the question. With both
    /// supplied, the page is taken as the placement and the module's inherited view key is decided from
    /// that page alone. With the module supplied and the page omitted, the inherited view key is decided
    /// from the module's placements collectively, which means it is listed only when every page the module
    /// sits on grants it - a strictly narrower statement that therefore cannot over-report what a
    /// particular placement allows.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<string>>> GetEffectivePermissionKeysAsync(
        int portalId,
        int? userId,
        int? moduleId = null,
        int? tabId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides whether a caller holds one permission on one module.
    /// </summary>
    /// <param name="portalId">The portal the module belongs to.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="moduleId">The module. <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so 0 is genuine.</param>
    /// <param name="permissionKey">The permission being tested. The key set is closed, so it is a domain enumeration whose member names are the persisted values.</param>
    /// <param name="placementTabId">
    /// The page the request addresses the module ON, when the request addresses one. It is not an
    /// alternative way of naming the module and it is not a filter: it identifies WHICH PLACEMENT of the
    /// module the decision is about, which matters whenever the module inherits its view permission,
    /// because then the answer is the page's answer and a module may sit on several pages with different
    /// grants. Supply it whenever the caller knows which placement it means; omit it only when the
    /// question genuinely is not about a placement. See the remarks for what omitting it means.
    /// </param>
    /// <param name="placementTabModuleId">
    /// The placement the request addresses, named by its own key - <c>TabModules.TabModuleID</c> - rather than
    /// by the page it sits on. This is the more precise of the two forms of address and is the one the module
    /// resource itself uses, because a module may be placed on the same page more than once and the page
    /// identifier alone would then be ambiguous. Supplying it together with <paramref name="placementTabId"/>
    /// is permitted and the two must agree; a contradiction is refused rather than resolved in favour of
    /// either.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the decision. A denial is
    /// <see langword="false"/> on a <em>successful</em> result, never a failure. Fails with
    /// <c>permission.module_not_found</c> when the module does not exist in the portal, or with
    /// <c>permission.key_invalid</c> when <paramref name="permissionKey"/> is not a defined member.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Delegates precedence evaluation to the single evaluator reached through the permission
    /// repository. Honours the module's inherit-view-permissions flag exactly as the legacy check did,
    /// so a module configured to take its view permission from its page is answered from the page's
    /// grants.
    /// </para>
    /// <para>
    /// WHY THE PLACEMENT IS PART OF THE QUESTION. The legacy check answered this for one module INSTANCE
    /// ON ONE PAGE, because the object it hydrated was a flattened module-and-placement join that always
    /// carried a page identifier. An earlier revision of this contract named only the module, and
    /// resolved the inherited case by granting when ANY page the module sits on granted the view key.
    /// That is strictly wider than the legacy answer and it is exploitable: a module placed on a public
    /// page and again on a restricted one becomes viewable through the restricted placement, because the
    /// public placement satisfied the test. Naming the placement restores the legacy question.
    /// </para>
    /// <para>
    /// WHEN THE PLACEMENT IS OMITTED, the inherited case is answered from the module's placements
    /// collectively, and the collective answer is the CONJUNCTION: the view key is granted only when
    /// every page the module sits on grants it, and a module that sits on no page is not viewable at all.
    /// That is the only collective reading which cannot exceed the answer for an individual placement, so
    /// omitting the placement is always at least as strict as naming one - never laxer. It coincides with
    /// the legacy answer for a singly-placed module, which is the overwhelming common case. When the
    /// placement IS supplied and the module does not actually occupy it, the answer is a denial
    /// rather than a fallback to any other placement.
    /// </para>
    /// <para>
    /// THE TWO FORMS OF ADDRESS MUST AGREE WHEN BOTH ARE GIVEN. Naming a placement by its own key and naming a
    /// page that is not the one that placement sits on is a contradiction, and a contradiction is refused. It is
    /// not resolved in favour of either, because whichever were chosen would be chosen for its permissions
    /// rather than for what the request meant.
    /// </para>
    /// </remarks>
    Task<Result<bool>> HasModulePermissionAsync(
        int portalId,
        int? userId,
        int moduleId,
        PermissionKey permissionKey,
        int? placementTabId = null,
        int? placementTabModuleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides whether a caller holds one permission on one page.
    /// </summary>
    /// <param name="portalId">The portal the page belongs to.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="tabId">The page. <c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>, so 0 is genuine.</param>
    /// <param name="permissionKey">The permission being tested.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the decision. Fails with
    /// <c>permission.tab_not_found</c> when the page does not exist in the portal, or with
    /// <c>permission.key_invalid</c> when <paramref name="permissionKey"/> is not a defined member.
    /// </returns>
    /// <remarks>
    /// Replaces the two legacy tab-scoped checks, one of which read the page from ambient request state
    /// rather than from an argument. The page is always named explicitly here.
    /// </remarks>
    Task<Result<bool>> HasTabPermissionAsync(
        int portalId,
        int? userId,
        int tabId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides whether a caller administers one portal.
    /// </summary>
    /// <param name="portalId">The portal whose administration is in question.</param>
    /// <param name="userId">The caller, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="cancellationToken">Token that cancels the reads.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the decision. An anonymous
    /// caller, an unknown caller, an unknown portal and a portal that designates no administrator role all
    /// answer <see langword="false"/> rather than failing, because each is a legitimate question with a
    /// negative answer rather than a fault.
    /// </returns>
    /// <remarks>
    /// <para>
    /// WHY THIS IS A SEPARATE QUESTION FROM A PERMISSION. Every other member here asks whether a grant
    /// exists on a resource. This asks about AUTHORITY OVER A TENANT, which the legacy application
    /// expressed not as a grant but as membership of the role named on the portal's own
    /// <c>AdministratorRoleId</c> column - and which it used to gate the settings that reach beyond the
    /// page in front of the caller. A field on a request body can require that authority even though no
    /// route-reading policy can see the field, which is exactly the case
    /// <c>IModuleService.UpdateModuleAsync</c> presents, and it is why the question has to be answerable
    /// from the Application layer rather than only from an authorisation handler.
    /// </para>
    /// <para>
    /// <b>Decided from STORED STATE, never from a claim.</b> A host account is admitted first, because a
    /// host account is installation-wide and the legacy security test short-circuited on it before
    /// examining any role. Otherwise the portal's own administrator role identifier is read from the
    /// portal being asked about - not from the tenant the caller happened to arrive through, which is a
    /// different question - and the caller's assignments are examined for an ACTIVE membership of exactly
    /// that role. Every assignment is examined rather than the first, so a duplicated pair cannot hide a
    /// valid grant behind a lapsed one, and validity is the role's effective and expiry window evaluated
    /// in coordinated universal time.
    /// </para>
    /// <para>
    /// A portal that designates no administrator role answers <see langword="false"/>: an unset
    /// designation is a configuration gap, and a gap must never grant. The designation is never widened
    /// to any other role, and the role is never matched by NAME - the name "Administrators" identifies a
    /// different row in every portal, so a name-based test is satisfied by an unrelated role in another
    /// tenant.
    /// </para>
    /// </remarks>
    Task<Result<bool>> IsPortalAdministratorAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every module- and page-scoped grant made directly to one user within one portal, as a
    /// self-contained operation that commits on its own.
    /// </summary>
    /// <param name="portalId">The portal to clean up within. Grants the user holds in other portals are untouched.</param>
    /// <param name="userId">The user whose direct grants are removed. Grants the user receives through a role are unaffected, because they belong to the role rather than to the user.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the user held no direct grants
    /// - removing nothing is a legitimate outcome. Fails with <c>permission.portal_not_found</c> or
    /// <c>permission.user_not_found</c> when either identifier names nothing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the TOP-LEVEL form: it stages the removals, commits them, and then evicts the affected
    /// cache entries. It is therefore the member to call when revoking a user's own grants is the whole
    /// of the operation, and the WRONG member to call from inside a larger business operation - use
    /// <see cref="StageUserPermissionRemovalAsync"/> for that, because a suboperation that commits on
    /// its own creates a partial-commit boundary inside the operation that encloses it.
    /// </para>
    /// <para>
    /// Both grant tables land together. This member consolidates the legacy cascade that
    /// <c>ModulePermissionController.vb:L218</c> and <c>TabPermissionController.vb:L209</c> performed
    /// separately, and it takes the portal identifier deliberately: those legacy members read it off the
    /// user object they were handed, so a user-only contract would widen the deletion to every portal the
    /// user belongs to.
    /// </para>
    /// </remarks>
    Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages - and only stages - the removal of every module- and page-scoped grant made directly to one
    /// user within one portal, leaving the commit to the operation that called it.
    /// </summary>
    /// <param name="portalId">The portal to clean up within. Grants the user holds in other portals are untouched.</param>
    /// <param name="userId">The user whose direct grants are staged for removal. Grants received through a role are unaffected.</param>
    /// <param name="cancellationToken">Token that cancels the reads this staging performs.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the user held no direct grants.
    /// Fails with <c>permission.portal_not_found</c> or <c>permission.user_not_found</c> when either
    /// identifier names nothing, in which case nothing has been staged.
    /// </returns>
    /// <remarks>
    /// <para>
    /// WHY THIS MEMBER EXISTS SEPARATELY FROM <see cref="DeleteUserPermissionsAsync"/>. Revoking a user's
    /// direct grants is one step of deleting the account that holds them, and the enclosing operation also
    /// removes role assignments, tenant membership, the external credential and the account row. If this
    /// step committed on its own, a later step failing - or the caller cancelling - would leave the grants
    /// durably gone while the account survived, which is the one outcome the cascade exists to prevent.
    /// So the two responsibilities are separated: this member decides WHAT is removed, and the caller
    /// decides WHEN that becomes durable.
    /// </para>
    /// <para>
    /// AN IMPLEMENTER MUST NOT COMMIT, FLUSH OR OPEN A TRANSACTION HERE, and must not evict a cache entry
    /// either. Eviction belongs after the commit - evicting before it opens a window in which a concurrent
    /// reader repopulates the entry from rows that are about to disappear, and discards a valid entry for
    /// nothing if the enclosing operation is abandoned. The caller performs it by calling
    /// <see cref="InvalidateUserPermissionCachesAsync"/> once its own commit has succeeded.
    /// </para>
    /// <para>
    /// The removal rule itself is unchanged and is defined in one place only: grants made DIRECTLY to the
    /// account are removed from both grant tables, and grants the account receives through a role are left
    /// alone because they belong to the role rather than to the account.
    /// </para>
    /// </remarks>
    Task<Result> StageUserPermissionRemovalAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every grant addressed to one role, across all three grant families, leaving the commit to
    /// the operation that called it.
    /// </summary>
    /// <param name="portalId">The portal that owns the role. The role must belong to it.</param>
    /// <param name="roleId">The role whose grants are removed. Grants addressed to an account are unaffected, because they belong to the account rather than to the role.</param>
    /// <param name="cancellationToken">Token that cancels the reads and the removals.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the role held no grant at all -
    /// removing nothing is a legitimate outcome. Fails with <c>permission.portal_not_found</c> or
    /// <c>permission.role_not_found</c> when either identifier names nothing, or when the role belongs to
    /// another tenant, in which case nothing has been removed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// WHY THIS MEMBER EXISTS. Removing a role and leaving its grants behind is a data-integrity fault
    /// rather than an untidiness: the grant tables carry no cascading foreign key to the role table in the
    /// schema this migration binds to, so the rows simply survive their principal, and role identifiers are
    /// reissued - <c>Roles.RoleID</c> is an identity column - so a later role can take a vacated identifier
    /// and inherit authority nobody granted it. The terminal legacy procedure removed all three families
    /// first for exactly this reason: <c>03.00.10.SqlDataProvider</c> deletes from the folder, module and
    /// page grant tables by role identifier before deleting the role row.
    /// </para>
    /// <para>
    /// THREE FAMILIES, NOT TWO. The folder family has no entity in this migration because file management
    /// is out of scope, but the table exists in every upgraded DotNetNuke database, so its rows are removed
    /// where it is present and the removal is a no-op where it is not. An implementer must not create,
    /// alter or drop anything to achieve that.
    /// </para>
    /// <para>
    /// AN IMPLEMENTER MUST NOT COMMIT, FLUSH OR OPEN A TRANSACTION HERE, and must not evict a cache entry
    /// either - the same division of responsibility that
    /// <see cref="StageUserPermissionRemovalAsync"/> observes, and for the same reasons. The removals reach
    /// the store as set-based statements the moment they are issued, so the CALLER MUST ALREADY HAVE A
    /// TRANSACTION OPEN: without one the grants become durable on their own, and a role removal that then
    /// failed would leave the role intact but stripped of every grant, while reporting that it had left the
    /// role alone. Eviction belongs after the caller's commit, through
    /// <see cref="InvalidateUserPermissionCachesAsync"/>.
    /// </para>
    /// <para>
    /// The tenant is taken as an argument so that a role identifier belonging to another portal is refused
    /// rather than acted on, which keeps a caller acting for one tenant from removing another tenant's
    /// grants through a mistyped identifier.
    /// </para>
    /// </remarks>
    Task<Result> StageRolePermissionRemovalAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts the cache entries invalidated by removing a user's direct grants, once the removal has been
    /// committed.
    /// </summary>
    /// <param name="portalId">The portal whose permission entries are evicted.</param>
    /// <param name="cancellationToken">Token that cancels the read of the portal's pages.</param>
    /// <returns>A task that completes once every affected entry has been evicted.</returns>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="StageUserPermissionRemovalAsync"/>, for the caller that owned the
    /// commit. It is separate because it MUST run after that commit: a warm entry would otherwise keep
    /// answering with grants that no longer exist, and grants are what authorisation is decided from, so
    /// the staleness is a security matter rather than a display one.
    /// </para>
    /// <para>
    /// MIGRATION: reproduces the two evictions the legacy cascade performed immediately after the same two
    /// deletes - <c>ModulePermissionController.vb:L220</c> cleared the module-permission entries of every
    /// page in the portal and <c>TabPermissionController.vb:L211</c> cleared the portal's page-permission
    /// entry. The page-permission entry is portal-keyed and is evicted directly; the module-permission
    /// entry is PAGE-keyed, so the portal-wide clear is expressed by naming each of the portal's pages in
    /// turn. No new cache member is invented for this.
    /// </para>
    /// <para>
    /// Calling it when nothing was staged is harmless: eviction is idempotent and costs at most a read of
    /// the portal's pages.
    /// </para>
    /// <para>
    /// THE EVICTION SET DOES NOT DEPEND ON WHICH KIND OF PRINCIPAL LOST ITS GRANTS, which is why
    /// <see cref="StageRolePermissionRemovalAsync"/> shares this member rather than acquiring one of its
    /// own. Both cached entries are keyed by tenant and page and hold the grants of every principal at
    /// once, so removing a role's grants staled precisely the same entries as removing an account's. The
    /// member is named for the caller it was written for, not for a restriction on who may call it; a
    /// second eviction member would be a second definition of the same set, free to drift from this one.
    /// </para>
    /// </remarks>
    Task InvalidateUserPermissionCachesAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one catalogue definition by its identifier.
    /// </summary>
    /// <param name="permissionId">The definition wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definition, or whose value is
    /// <see langword="null"/> when no definition bears that identifier - which lets the API layer answer
    /// <c>404</c> without this member raising a failure for an ordinary "not there".
    /// </returns>
    /// <remarks>
    /// <para>
    /// Reproduces <c>PermissionController.GetPermission(permissionID)</c>
    /// (<c>PermissionController.vb:L30</c>), which returned the whole record. The record rather than the key
    /// alone is what a caller needs here: a key on its own cannot say which scope code or which module
    /// definition it was declared under, and the same key is declared repeatedly across scopes.
    /// </para>
    /// <para>
    /// Catalogue rows are installation-wide reference data with no portal column, so this read takes no
    /// tenant argument - which is the same reason the catalogue read above takes none.
    /// </para>
    /// </remarks>
    Task<Result<PermissionDto?>> GetPermissionAsync(
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the catalogue definitions that apply to one module placement.
    /// </summary>
    /// <param name="portalId">The portal whose catalogue is being administered.</param>
    /// <param name="moduleId">The module placement whose applicable definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in a stable order,
    /// each definition once, or <c>permission.module_not_found</c> when the placement does not belong to
    /// <paramref name="portalId"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Reproduces <c>PermissionController.GetPermissionsByModuleID(ModuleID)</c>
    /// (<c>PermissionController.vb:L38</c>), whose terminal procedure body takes the UNION of the entries
    /// declared by the definition this placement was created from - resolved through the module row - with
    /// every entry carrying the product-wide module-definition scope code. Both halves are reproduced.
    /// </para>
    /// <para>
    /// Distinct from the module-DEFINITION filter on the catalogue read above, and the distinction is easy
    /// to lose. That one takes a definition identifier and answers with exactly that definition's entries.
    /// This one takes a PLACEMENT identifier, resolves the definition behind it, and then widens the answer
    /// with the product-wide scope, so it is a deliberately wider question rather than the same question
    /// reached differently.
    /// </para>
    /// <para>
    /// SEC-033: the placement is resolved before the catalogue is read and must belong to the addressed
    /// portal. A missing placement and one owned by another portal report the same failure, so this read
    /// cannot be used as a cross-tenant identifier oracle. The legacy statement returned the product-wide
    /// scope for an unknown identifier, but preserving that quirk at an HTTP boundary would disclose
    /// metadata for a resource the caller has not proved belongs to the tenant they administer.
    /// </para>
    /// <para>
    /// MIGRATION: catalogue definitions are returned rather than grant rows, which is also what the legacy
    /// member returned - measured, not assumed: its terminal body selects the five catalogue columns from
    /// the <c>Permission</c> table and the member hydrates <c>PermissionInfo</c>, the catalogue type. Which
    /// role or account HOLDS a grant belongs to the grant-management surface that is deliberately absent
    /// from this contract, for the reason recorded at the head of this file.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<PermissionDto>>> GetModulePermissionDefinitionsAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the catalogue definitions that apply to one page.
    /// </summary>
    /// <param name="portalId">The portal whose catalogue is being administered.</param>
    /// <param name="tabId">The page whose applicable definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in a stable order,
    /// each definition once, or <c>permission.tab_not_found</c> when the page does not belong to
    /// <paramref name="portalId"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Reproduces <c>PermissionController.GetPermissionsByTabID(TabID)</c>
    /// (<c>PermissionController.vb:L51</c>), and is the page-scoped counterpart of the module-scoped read
    /// above.
    /// </para>
    /// <para>
    /// SEC-033: the terminal legacy procedure returned the same <c>SYSTEM_TAB</c> definitions for every
    /// page, but the target still proves that <paramref name="tabId"/> exists in the addressed portal before
    /// returning that shared set. A missing page and a page owned by another portal report the same failure,
    /// so the identifier cannot be used to infer another tenant's resources.
    /// </para>
    /// <para>
    /// MIGRATION: as with the module-scoped read, catalogue definitions are returned rather than grant
    /// rows; page grants belong to the grant-management surface deliberately absent from this contract.
    /// </para>
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
/// <para>
/// Declared beside the contract that returns it rather than in a projection folder of its own, following the
/// precedent this solution already sets for a small type whose only reason to exist is one contract's return
/// shape. Five members, which is exactly the shape of the legacy <c>PermissionInfo</c>
/// (<c>Library/Components/Security/Permissions/Permission.vb</c>) and of the terminal
/// <c>dbo.Permission</c> row.
/// </para>
/// <para>
/// The catalogue LIST endpoint deliberately continues to publish bare keys rather than these records: a
/// catalogue of keys is what the permission vocabulary is, it is what the token carries and what the
/// client-side permission directive tests, and widening it would change an established contract for no
/// caller's benefit. These records are returned only by the three reads that identify a particular
/// definition or a particular grant target, where a key alone would be ambiguous.
/// </para>
/// </remarks>
public sealed class PermissionDto
{
    /// <summary>Identifier of the definition, from <c>Permission.PermissionID</c>.</summary>
    /// <remarks>
    /// <c>IDENTITY (1, 1)</c> in the terminal schema, so no value here is a sentinel; it is nevertheless
    /// never compared against a bound anywhere in this solution.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <summary>
    /// Scope code the definition belongs to, from <c>Permission.PermissionCode</c>.
    /// </summary>
    /// <remarks>
    /// Free text rather than a closed set - <c>SYSTEM_MODULE_DEFINITION</c>, <c>SYSTEM_TAB</c> and
    /// installation-specific codes all occur - so it travels as the stored string and no enumeration is
    /// invented for it. Initialised to the empty string so the non-nullable annotation holds without a
    /// suppression, matching the entity it projects.
    /// </remarks>
    public string PermissionCode { get; set; } = string.Empty;

    /// <summary>
    /// Module definition the permission was declared under, from <c>Permission.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// Zero for a definition that belongs to no module definition, which is how the page-scoped rows are
    /// stored. Not nullable, because the entity it projects is not.
    /// </remarks>
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
