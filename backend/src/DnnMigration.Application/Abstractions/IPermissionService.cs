// MIGRATION: three legacy controllers collapse into this one application contract.
// Library/Components/Security/Permissions/PermissionController.vb declares 9 public
// members across 71 lines, ModulePermissionController.vb declares 18 across 389 lines and
// TabPermissionController.vb declares 15 across 349 lines - 42 measured public members,
// counted against the declaration form of a public function, sub or property. Seven
// members remain here, and the 42-to-7 reduction is accounted for exactly rather than
// trimmed by taste. Thirteen of the 42 carry an explicit deprecation attribute in the
// legacy source itself, 7 in the module controller and 6 in the tab controller, and most
// of those are near-identical read overloads differing only in whether the caller had
// already materialised the rows. The module-scoped reads at
// ModulePermissionController.vb L239, L254, L308, L327, L345 and L350 collapse into one
// member, and the tab-scoped reads at TabPermissionController.vb L214, L229, L298, L303,
// L321, L334 and L339 collapse into one member. Those variants existed only to dodge a
// second query, which is the caching abstraction's responsibility now, orchestrated
// inside the implementing service and deliberately invisible on this surface.
//
// MIGRATION: access decisions are NOT on this contract, and that boundary is the single
// most important property of this file. Five of the 42 legacy members answered the
// question "may this caller do X?" - ModulePermissionController.vb:L33 over an
// already-materialised set, L52 by module and page identifier, and the deprecated L377 by
// module identifier alone; TabPermissionController.vb:L33 reading the current page from
// ambient request state, and L38 over an already-materialised set. None of the five is
// reproduced here. The decision logic belongs to
// Infrastructure/Security/PermissionEvaluator.cs and is surfaced declaratively by the
// policy types that live together under Api/Authorization/, namely the requirement type
// Api/Authorization/PermissionRequirement.cs, the policy-name catalogue
// Api/Authorization/PolicyNames.cs and the policy handler that sits beside them. This
// contract supplies data only. Two independent evaluators, one in the application layer
// and one in the api layer, is the worst available result of this migration, so no member
// below returns a decision and no member below accepts a caller identity.
//
// MIGRATION: the semicolon-delimited permission string is gone. Three legacy members
// flattened a set of grants into a single string value:
// ModulePermissionController.vb:L239-L252 and TabPermissionController.vb:L214-L227 each
// emit a leading semicolon followed by every granted role name and then every granted
// user identifier, each entry terminated by a further semicolon, and the deprecated
// TabPermissionController.vb:L303-L318 does the same from a pre-materialised set. That
// encoding existed to push one value into one server-rendered control property; it was
// never a data contract. Every read member here returns a structured, strongly typed,
// read-only sequence, so a consumer never parses a string to learn who was granted what.
//
// MIGRATION: the bracketed pseudo-role encoding is gone. A grant is either role-scoped or
// user-scoped, discriminated in the legacy reader by testing the user identifier against
// the shared integer sentinel, and a user-scoped grant was then checked by synthesising
// the pseudo-role literal "[" + user identifier + "]" and passing it to the very same
// role-membership helper a real role name was passed to - see
// ModulePermissionController.vb:L42 and TabPermissionController.vb:L47. That is precisely
// why the legacy delimited value is not a pure sequence of role names. Here a user-scoped
// grant carries a first-class nullable user identifier and a role-scoped grant carries a
// nullable role identifier. The terminal schema agrees: 04.05.00.SqlDataProvider drops
// and recreates RoleID as a nullable column and adds a nullable UserID with a foreign key
// to the users table, on both grant tables - L624, L628 and L642 for the module table,
// L465, L469 and L483 for the page table. Exactly one of the two identifiers is populated
// on any single grant, and that invariant is stated on every member that carries one.
//
// MIGRATION: the ambient page read is gone. TabPermissionController.vb:L33-L36 accepted
// no scope argument at all: it reached into the current request's portal settings object
// and used whichever page that object happened to be pointing at, and its sibling at L39
// re-read the same ambient object without using it. Every member below states its scope
// explicitly, as a portal, page or module identifier supplied by the caller, and no
// member infers scope from request state. The scoped, immutable portal-context
// abstraction owned by the domain layer remains available to the implementing service,
// but no member here omits an identifier on the assumption that it will supply one.
//
// MIGRATION: two members the legacy author had already retired are deliberately not
// ported. ModulePermissionController.vb:L355-L374 is decorated as obsoleted, with the
// note that it existed to swap sequences of role identifiers for sequences of role names,
// and ModulePermissionController.vb:L376-L381 is decorated as deprecated in favour of the
// three-argument access check. Re-creating either would resurrect API that its own author
// withdrew. The second is doubly excluded, being an access decision as well.
//
// MIGRATION: PermissionController.vb:L43, GetPermissionsByFolder, is not ported.
// The file management subsystem it serves is excluded from this migration, so the
// folder-scoped lookup and the folder grant table have no target feature. The catalogue
// rows that subsystem relies on still exist, and a catalogue read here may legitimately
// return them; only the lookup keyed by a folder path disappears. The omission is
// recorded rather than silently dropped so a later reader cannot mistake it for an
// oversight.
//
// MIGRATION: entity arguments become identifiers. ModulePermissionController.vb:L218 and
// TabPermissionController.vb:L209 each accept a whole legacy user object and then read
// exactly two fields from it, the portal identifier and the user identifier, before
// handing both to the data layer. No domain entity crosses this contract in either
// direction, so the consolidated cascade member below accepts those two identifiers
// directly. Reading the legacy call rather than the legacy signature matters here: the
// parameter list looks user-scoped, but the work performed is portal-scoped as well, and
// a contract accepting only a user identifier would silently widen the blast radius of
// the deletion to every portal that user belongs to.
//
// MIGRATION: an absent identifier is a null nullable, never a numeric sentinel. The
// legacy integer sentinel is defined at Library/Components/Shared/Null.vb:L41-L45 with
// the value -1, and ModulePermissionController.vb L266, L346 and L379 together with
// TabPermissionController.vb L241, L299 and L335 all pass -1 to mean "no page". That
// value is not free. 01.00.00.SqlDataProvider:L77 declares Portals.PortalID as an
// identity column seeded at -1, so -1 identifies the first real portal; Roles.RoleID
// (L115), Tabs.TabID (L140) and Modules.ModuleID (L221) all seed at 0, making 0 a real
// identifier too; and Users.UserID (L98) seeds at 1. Every optional identifier on this
// contract is therefore a nullable integer. An implementation must never coalesce -1 or 0
// into absence, and must never emit either value to signal it.
//
// MIGRATION: the legacy pre-generics wrapper types produce no target type at all. The
// module and tab permission collection classes, and the untyped sequence and dictionary
// types they were built over, are replaced by read-only generic sequences on this
// surface. There is no target counterpart to either wrapper file and none should be
// created: a caller needing indexed or keyed access composes it from the sequence
// returned here.

using DnnMigration.Application.Dtos.Permission;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Reads the permission catalogue and reads or replaces the permission grants attached to
/// a module or a page. This contract supplies permission <em>data</em>; it never decides
/// whether a caller is allowed to do something.
/// </summary>
/// <remarks>
/// <para>
/// Scope of this contract. It consolidates the three legacy permission controllers under
/// <c>Library/Components/Security/Permissions/</c> into one application service, and
/// answers exactly two questions: <em>which permissions exist?</em> and <em>who has been
/// granted which of them on this module or on this page?</em> Everything else that those
/// controllers did is either an access decision (see the next paragraph), an artefact of
/// pre-generics collections, a caching short-cut, or API the legacy author had already
/// deprecated. The migration notes at the head of this file account for all 42 measured
/// legacy members against the 7 declared below.
/// </para>
/// <para>
/// THE ACCESS DECISION IS NOT HERE. No member of this interface returns a decision, and
/// none may be added. Evaluating whether a caller satisfies a permission is centralised
/// in <c>Infrastructure/Security/PermissionEvaluator.cs</c>, and it is surfaced
/// declaratively through the policy types under <c>Api/Authorization/</c>: the requirement
/// type in <c>Api/Authorization/PermissionRequirement.cs</c>, the policy-name catalogue in
/// <c>Api/Authorization/PolicyNames.cs</c>, and the policy handler registered beside them.
/// The api layer can reach both the application layer and the infrastructure layer, which
/// is what makes that arrangement legal; this project references the domain layer alone
/// and therefore cannot express an authorisation type even accidentally. An implementer
/// who re-derives evaluation logic inside the implementing service creates two evaluators
/// that can disagree, which is the single worst result available in this area, so the
/// prohibition is stated here rather than left to review.
/// </para>
/// <para>
/// The client mirrors, the server enforces. The Angular <c>hasPermission</c> directive
/// consumes the same permission keys to show or hide affordances, but that is presentation
/// only and is never the sole enforcement mechanism. Every endpoint decides independently
/// of whatever the browser chose to render, and the data this contract returns is what
/// populates the administrative permission grids, not what secures them.
/// </para>
/// <para>
/// Grants are role-scoped or user-scoped, never both. Every grant returned or accepted
/// here names exactly one principal: a role, identified by a role identifier, or a single
/// user, identified by a user identifier. The legacy reader at
/// <c>ModulePermissionController.vb:L148-L154</c> makes the discrimination explicit - when
/// the user identifier is the sentinel it reads the role fields, and otherwise it forces
/// the role identifier to the reserved no-role value and blanks the role name. In the
/// terminal schema both columns are nullable, so the invariant is expressed directly:
/// exactly one of the two identifiers is populated, and an implementation rejects a grant
/// that populates both or neither with the failure code
/// <c>permission.invalid_assignment</c>.
/// </para>
/// <para>
/// Deny grants are real and must survive a read. Each grant carries an allow-or-deny flag
/// backed by a genuine <c>bit NOT NULL</c> column. The legacy string builders at
/// <c>ModulePermissionController.vb:L243</c> and <c>TabPermissionController.vb:L218</c>
/// filtered on that flag being set and therefore discarded deny rows, but they were
/// building a control value rather than reading data. Every read member here returns the
/// complete grant set for the requested scope, deny rows included, and a consumer that
/// wants only the allow rows filters them itself. Dropping a deny row on read would let a
/// replace operation silently resurrect access that an administrator had removed.
/// </para>
/// <para>
/// Mutation is replace-the-set, never a granular triad. The legacy screens posted an
/// entire permission grid and the controllers then applied it through separate add, update
/// and delete members. Exposing that triad here would force the calling controller to
/// compute the difference between the submitted grid and the stored rows, and choosing
/// which rows to insert, update or delete is a business decision, which no controller may
/// take. The two replace members below therefore accept the complete desired set for one
/// scope and make the stored rows match it, computing the difference and committing it as
/// one unit of work. Passing an empty set is the supported way to clear a scope, and is
/// the exact equivalent of the legacy <c>DeleteModulePermissionsByModuleID</c> and
/// <c>DeleteTabPermissionsByTabID</c> members.
/// </para>
/// <para>
/// Result semantics. Every member returns a <see cref="Result"/> or a
/// <see cref="Result{T}"/>, so an <em>expected</em> outcome - a scope that does not exist,
/// a malformed grant, a permission key that is not in the catalogue - is reported as a
/// failure reason rather than thrown. Unexpected faults propagate as exceptions and are
/// translated once at the api boundary. On a single-item lookup, a
/// <see cref="Result{T}"/> that <em>succeeded</em> and carries a <see langword="null"/>
/// value means the row is absent, which is not a failure; callers must test
/// <see cref="Result.IsSuccess"/> first and then the value for
/// <see langword="null"/>. Failure codes are lower-case, dotted and stable, in the form
/// <c>permission.condition</c>; each member documents the codes it can produce, and the
/// api layer maps them to status codes. A successful outcome may also carry an advisory
/// reason, which callers may surface but must not treat as an error.
/// </para>
/// <para>
/// Paging is deliberately absent. The catalogue is small, bounded reference data seeded by
/// the upgrade scripts, and a grant set is bounded by the number of roles and named users
/// configured for one scope. Every read below therefore returns a complete read-only
/// sequence rather than a page, and no member restates a page-index convention.
/// </para>
/// <para>
/// Registration and lifetime. This is one of the seven services registered by the
/// application layer's <c>AddApplication()</c> extension, as
/// <c>AddScoped&lt;IPermissionService, PermissionService&gt;()</c>, implemented by
/// <c>Application/Services/PermissionService.cs</c>. Scoped is required rather than
/// convenient: the implementation depends on the scoped permission repository and unit of
/// work owned by the domain layer, and a singleton would capture them across requests.
/// </para>
/// <para>
/// Implementer's checklist. Perform every read and write asynchronously and honour the
/// supplied cancellation token; never block on a task. Reach the database only through the
/// domain layer's repository and unit-of-work abstractions, never through a persistence
/// context. Apply caching through the domain layer's cache abstraction, using stable keys
/// and explicit invalidation after every successful replace, and never expose a cache
/// concern on this surface. Treat every replace as one transaction, so a partially applied
/// grid is impossible. Never treat -1 or 0 as an absent identifier, and never return a
/// domain entity.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// Reads the permission catalogue, optionally narrowed by any combination of the
    /// supplied filters.
    /// </summary>
    /// <param name="permissionCode">
    /// Scope code a permission definition belongs to, matched exactly and
    /// case-insensitively, or <see langword="null"/> to place no restriction on the code.
    /// The underlying column is free text rather than a closed set, so no enumeration
    /// exists for it and an installation carrying a code this codebase has never seen
    /// still round-trips intact.
    /// </param>
    /// <param name="permissionKey">
    /// Permission key a definition declares, or <see langword="null"/> to place no
    /// restriction on the key. The key set is closed, so it is expressed as a domain
    /// enumeration whose member names are the persisted values.
    /// </param>
    /// <param name="moduleDefinitionId">
    /// Module definition whose permission definitions are wanted, or
    /// <see langword="null"/> to place no restriction. Definitions that belong to no
    /// module definition are matched only when this argument is <see langword="null"/>.
    /// </param>
    /// <param name="moduleId">
    /// Module instance whose applicable permission definitions are wanted, or
    /// <see langword="null"/> to place no restriction. The module instance is resolved to
    /// its definition before matching, which is what the legacy member of the same shape
    /// did.
    /// </param>
    /// <param name="tabId">
    /// Page whose applicable permission definitions are wanted, or <see langword="null"/>
    /// to place no restriction.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the matching
    /// definitions, in a stable order, and an empty sequence when nothing matches - an
    /// empty catalogue is a legitimate answer, never a failure. Fails with
    /// <c>permission.filter_invalid</c> when <paramref name="permissionCode"/> is supplied
    /// but blank, when <paramref name="permissionKey"/> is supplied but is not a defined
    /// member of the enumeration, or when a supplied identifier cannot be a valid key for
    /// its table.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The filters compose conjunctively: every supplied filter narrows the result, and
    /// supplying none returns the complete catalogue, which is what backs the read-only
    /// permissions endpoint.
    /// </para>
    /// <para>
    /// This one member replaces four legacy members that differed only in which single
    /// column they filtered on - <c>PermissionController.vb</c> at L34 by module
    /// definition, L38 by module, L47 by scope code together with key, and L51 by page.
    /// Each optional argument corresponds to exactly one of them, so nothing is lost and
    /// combinations the legacy could not express become available without new behaviour
    /// being invented.
    /// </para>
    /// <para>
    /// The catalogue is reference data. Rows are seeded by the upgrade scripts and written
    /// only during module installation, which is outside the scope of this migration, so
    /// no member here creates, updates or deletes a definition. The legacy write members
    /// at <c>PermissionController.vb</c> L55, L59 and L63 are deliberately not ported for
    /// that reason.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<PermissionDto>>> GetPermissionsAsync(
        string? permissionCode = null,
        PermissionKey? permissionKey = null,
        int? moduleDefinitionId = null,
        int? moduleId = null,
        int? tabId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single permission definition by its identifier.
    /// </summary>
    /// <param name="permissionId">
    /// Identifier of the definition. The backing column is an identity column seeded at 1,
    /// so a value below 1 cannot identify a row.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the definition,
    /// or a successful result whose value is <see langword="null"/> when no definition
    /// carries that identifier. An absent row is deliberately modelled as success with no
    /// value rather than as a failure, because "this identifier does not exist" is an
    /// answer to the question asked, not a fault; the api layer is what turns it into a
    /// not-found status. Fails with <c>permission.filter_invalid</c> when
    /// <paramref name="permissionId"/> is below 1.
    /// </returns>
    /// <remarks>
    /// Replaces <c>PermissionController.vb:L30</c>, which returned a hydrated object and
    /// gave the caller no way to distinguish an absent row from a failed read.
    /// </remarks>
    Task<Result<PermissionDto?>> GetPermissionAsync(
        int permissionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the complete set of permission grants attached to one module instance,
    /// including deny grants.
    /// </summary>
    /// <param name="moduleId">
    /// Identifier of the module instance. The backing column is an identity column seeded
    /// at 0, so 0 is a legitimate module and must never be read as "no module".
    /// </param>
    /// <param name="tabId">
    /// Page the module instance is placed on, or <see langword="null"/> when the caller
    /// does not know it. A module instance is page-scoped and the legacy read took both
    /// identifiers, so supplying the page lets the implementation resolve the grants
    /// without first looking the placement up. The backing column is an identity column
    /// seeded at 0, so 0 is a legitimate page: absence is carried by
    /// <see langword="null"/> and by nothing else.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is every grant
    /// recorded for that module, in a stable order, and an empty sequence when the module
    /// exists but carries no grants - which is a meaningful state, not a failure. Fails
    /// with <c>permission.module_not_found</c> when no module carries
    /// <paramref name="moduleId"/>, with <c>permission.tab_not_found</c> when
    /// <paramref name="tabId"/> is supplied and no page carries it, and with
    /// <c>permission.filter_invalid</c> when either identifier is below the seed of its
    /// table.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This one member replaces six legacy members. <c>ModulePermissionController.vb:L254</c>
    /// is the current form and takes both identifiers; L345 and L350 are deprecated
    /// single-identifier variants, the second of which merely re-filtered a set the caller
    /// had already materialised; L308 is a deprecated per-page bulk read; L239 and the
    /// deprecated L327 returned a delimited string filtered to one key rather than the
    /// grant rows themselves.
    /// </para>
    /// <para>
    /// No key filter is offered, deliberately. The legacy key-filtered forms existed to
    /// populate a single control with the principals holding one key, and the
    /// administrative grid edits every key at once. Returning the whole set once, deny
    /// grants included, is both fewer round trips and the only shape from which the
    /// replace member's input can be safely derived.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<ModulePermissionDto>>> GetModulePermissionsAsync(
        int moduleId,
        int? tabId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the permission grants stored for one module instance match the supplied set
    /// exactly, inserting, updating and removing rows as required, as a single unit of
    /// work.
    /// </summary>
    /// <param name="moduleId">
    /// Identifier of the module instance whose grants are being replaced. The backing
    /// column is an identity column seeded at 0, so 0 is a legitimate module.
    /// </param>
    /// <param name="tabId">
    /// Page the module instance is placed on, or <see langword="null"/> when the caller
    /// does not know it. Required positionally but permitted to be
    /// <see langword="null"/>, so that a caller has to make the placement explicit instead
    /// of inheriting a default. The legacy add member took the page identifier for the
    /// sole purpose of evicting the right cache partition, and the implementation uses it
    /// the same way.
    /// </param>
    /// <param name="assignments">
    /// The complete desired set of grants for that module. An empty sequence clears every
    /// grant and is the supported equivalent of the legacy
    /// <c>DeleteModulePermissionsByModuleID</c>. Each entry names exactly one principal, a
    /// role or a single user, and carries its own allow-or-deny flag, so a deny grant is
    /// expressed by an entry rather than by omission - omitting an entry removes the grant
    /// entirely, which is not the same thing.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the replacement.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once the stored grants match
    /// <paramref name="assignments"/> and the unit of work has been committed. Fails with
    /// <c>permission.module_not_found</c> when no module carries
    /// <paramref name="moduleId"/>; <c>permission.tab_not_found</c> when
    /// <paramref name="tabId"/> is supplied and no page carries it;
    /// <c>permission.unknown_permission_key</c> when an entry names a permission that is
    /// not in the catalogue or is not applicable to that module's definition;
    /// <c>permission.invalid_assignment</c> when an entry names both a role and a user or
    /// neither, or names a role or user that does not exist in the owning portal; and
    /// <c>permission.duplicate_assignment</c> when two entries name the same principal for
    /// the same permission. Nothing is committed when any entry is rejected, so a caller
    /// never has to reason about a partially applied grid.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces five legacy members - <c>ModulePermissionController.vb</c> L197 and L203
    /// (add), L272 (update), L209 (delete one) and L213 (delete all for a module) - with a
    /// single declarative operation.
    /// </para>
    /// <para>
    /// The granular triad is deliberately not exposed. The legacy screens posted an entire
    /// grid, so a triad would oblige the calling controller to diff the submitted grid
    /// against the stored rows and decide which to insert, update or delete. That is a
    /// business decision, and controllers may not take one. Replacing the set keeps the
    /// decision inside the application layer where the change can also be committed
    /// atomically, which the legacy sequence of individual calls never was.
    /// </para>
    /// </remarks>
    Task<Result> ReplaceModulePermissionsAsync(
        int moduleId,
        int? tabId,
        IReadOnlyList<ModulePermissionAssignmentRequest> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the complete set of permission grants attached to one page, including deny
    /// grants.
    /// </summary>
    /// <param name="tabId">
    /// Identifier of the page. The backing column is an identity column seeded at 0, so 0
    /// is a legitimate page and must never be read as "no page".
    /// </param>
    /// <param name="portalId">
    /// Portal the page is expected to belong to, or <see langword="null"/> to skip the
    /// check. When supplied it is verified, and a page belonging to a different portal is
    /// refused rather than returned. The backing column is an identity column seeded at
    /// -1, so -1 identifies the first real portal and is never a marker for absence.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is every grant
    /// recorded for that page, in a stable order, and an empty sequence when the page
    /// exists but carries no grants. Fails with <c>permission.tab_not_found</c> when no
    /// page carries <paramref name="tabId"/>, with
    /// <c>permission.tab_portal_mismatch</c> when <paramref name="portalId"/> is supplied
    /// and the page belongs to a different portal, with
    /// <c>permission.portal_not_found</c> when <paramref name="portalId"/> is supplied and
    /// no portal carries it, and with <c>permission.filter_invalid</c> when
    /// <paramref name="tabId"/> is below the seed of its table.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This one member replaces seven legacy members.
    /// <c>TabPermissionController.vb:L229</c> is the current form and takes both
    /// identifiers; L298, L321, L334 and L339 are deprecated variants that differ only in
    /// whether the caller pre-materialised the rows; L214 and the deprecated L303 returned
    /// a delimited string filtered to one key instead of the grant rows.
    /// </para>
    /// <para>
    /// The portal argument is optional here although the legacy form required it, because
    /// the legacy used it purely to select a per-portal cache partition and fell back to a
    /// query keyed by the page alone when the partition missed. A page identifier is
    /// globally unique, so it is sufficient on its own. The argument is kept because
    /// verifying that a page belongs to the portal the caller believes it does closes a
    /// genuine cross-tenant hazard: the legacy tenant-resolution procedure matched a
    /// portal alias with a wildcard comparison and could resolve one portal's request
    /// against another's row, and refusing a mismatch here is the countermeasure at this
    /// layer.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<TabPermissionDto>>> GetTabPermissionsAsync(
        int tabId,
        int? portalId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the permission grants stored for one page match the supplied set exactly,
    /// inserting, updating and removing rows as required, as a single unit of work.
    /// </summary>
    /// <param name="tabId">
    /// Identifier of the page whose grants are being replaced. The backing column is an
    /// identity column seeded at 0, so 0 is a legitimate page.
    /// </param>
    /// <param name="assignments">
    /// The complete desired set of grants for that page. An empty sequence clears every
    /// grant and is the supported equivalent of the legacy
    /// <c>DeleteTabPermissionsByTabID</c>. Each entry names exactly one principal, a role
    /// or a single user, and carries its own allow-or-deny flag.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the replacement.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once the stored grants match
    /// <paramref name="assignments"/> and the unit of work has been committed. Fails with
    /// <c>permission.tab_not_found</c> when no page carries <paramref name="tabId"/>;
    /// <c>permission.unknown_permission_key</c> when an entry names a permission that is
    /// not in the catalogue or is not applicable to a page;
    /// <c>permission.invalid_assignment</c> when an entry names both a role and a user or
    /// neither, or names a role or user that does not exist in the owning portal; and
    /// <c>permission.duplicate_assignment</c> when two entries name the same principal for
    /// the same permission. Nothing is committed when any entry is rejected.
    /// </returns>
    /// <remarks>
    /// Replaces four legacy members - <c>TabPermissionController.vb</c> L194 (add), L247
    /// (update), L200 (delete one) and L204 (delete all for a page) - for the same
    /// reasons, and with the same atomicity guarantee, as the module replacement above.
    /// </remarks>
    Task<Result> ReplaceTabPermissionsAsync(
        int tabId,
        IReadOnlyList<TabPermissionAssignmentRequest> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every module and page permission grant held by one user within one portal,
    /// as a single unit of work.
    /// </summary>
    /// <param name="portalId">
    /// Portal the grants are removed within. Required, and the reason this member is not
    /// purely user-scoped: a user may hold grants in several portals and only the named
    /// portal's grants are affected. The backing column is an identity column seeded at
    /// -1, so -1 identifies the first real portal.
    /// </param>
    /// <param name="userId">
    /// Identifier of the user whose grants are removed. The backing column is an identity
    /// column seeded at 1, so a value below 1 cannot identify a user. Role-scoped grants
    /// are untouched: only grants naming this user directly are removed.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the removal.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/> once every matching grant has
    /// been removed and the unit of work has been committed. Succeeding when the user held
    /// no grants at all is correct and intended, because the operation is idempotent and
    /// its post-condition - the user holds no direct grants in that portal - is already
    /// satisfied. Fails with <c>permission.portal_not_found</c> when no portal carries
    /// <paramref name="portalId"/>, with <c>permission.user_not_found</c> when no user
    /// carries <paramref name="userId"/>, and with <c>permission.filter_invalid</c> when
    /// <paramref name="userId"/> is below 1.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Consolidates <c>ModulePermissionController.vb:L218</c> and
    /// <c>TabPermissionController.vb:L209</c>, which performed the two halves separately
    /// and non-atomically, each accepting a whole legacy user object and then reading the
    /// portal and user identifiers from it.
    /// </para>
    /// <para>
    /// This is a cascade step in the user-deletion workflow, invoked by the user service
    /// while it removes a user, and it is not exposed as an endpoint of its own. Removing
    /// a user's permission grants is not something an administrator asks for in isolation;
    /// leaving them behind, however, would strand rows referencing an identifier that no
    /// longer resolves. The behaviour is reachable and load-bearing, which is why it
    /// survives the migration as its own member rather than being folded into a repository
    /// detail.
    /// </para>
    /// </remarks>
    Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);
}
