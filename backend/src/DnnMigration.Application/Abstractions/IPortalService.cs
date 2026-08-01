// MIGRATION: This contract replaces the two legacy classes that between them owned the portal
// aggregate and its aliases. PortalController, at Library/Components/Portal/PortalController.vb,
// is 1,632 lines carrying 21 public members - 6 of them Shared (static) and 15 instance members.
// PortalAliasController, at Library/Components/Portal/PortalAliasController.vb, is 102 lines
// carrying a further 9, none of them Shared. Neither class was substitutable: an instance member
// was reached through a directly constructed object and a Shared member through the type itself,
// so no consumer could stand either aside for a test double. Both collapse into this single
// injected abstraction, registered with a scoped lifetime as one of the seven services the
// application layer contributes.
//
// MIGRATION: There is deliberately no separate alias service. An alias has no existence
// independent of the portal that owns it - the owning column is declared NOT NULL with a
// cascading foreign key - and the nested route these members serve,
// /api/v1/portals/{portalId}/aliases, mirrors that ownership. Splitting the two apart would let a
// caller modify an alias without ever naming the tenant it belongs to.
//
// MIGRATION: 1 of 15. Positional parameter lists become named request objects. CreatePortal at
// PortalController.vb:L980 declared FIFTEEN positional parameters; UpdatePortalInfo at
// PortalController.vb:L1568 declared TWENTY-SEVEN. Both counts were verified by reading the
// signatures rather than taken on trust. Neither shape survives: creation takes
// CreatePortalRequest and modification takes UpdatePortalRequest, so every value is named at the
// call site, argument order stops being load-bearing, and adding a column becomes an additive
// change to one type instead of a breaking change to every caller. A second legacy overload,
// UpdatePortalInfo at PortalController.vb:L1524, accepted the persisted record itself and merely
// unpacked it into the twenty-seven-argument form; it is folded into the same request object,
// because no persisted record crosses this boundary in either direction.
//
// MIGRATION: 2 of 15. DeletePortal at PortalController.vb:L162 reported its outcome as a String -
// empty meaning success, non-empty being a human-readable message already localised for display.
// A caller therefore had to compare against text to learn whether the delete happened. That is
// replaced by a Result. The rule the string carried is a genuine business rule and is preserved:
// the legacy body first reads how many portals exist, proceeds only when more than one does, and
// otherwise yields the shared message keyed LastPortal, whose wording is "You Can Not Delete The
// Last Portal In Your Database".
// MIGRATION: that wording is at Website/App_GlobalResources/SharedResources.resx:942. The rule
// survives as the documented failure code portal.last_remaining. Per Rule T2 the decision is taken
// inside the service; no member here reports a portal tally for a controller to interpret.
//
// MIGRATION: 3 of 15. Two untyped listing contracts become one paged envelope. GetPortals at
// PortalController.vb:L1263 returned the non-generic collection type of the era, declaring no
// element type and no grand total. GetPortalsByName at PortalController.vb:L262 returned the same
// non-generic type and smuggled the grand total back through a by-reference Integer argument, so a
// single call produced two answers that no type tied together. Both become
// PagedResult<PortalListItemDto>, which carries the records and the total on one immutable value.
// No by-reference and no output argument appears anywhere on this contract.
//
// MIGRATION: 4 of 15. The legacy "return everything, unpaged" convention was a negative page
// index: GetPortalsByName at PortalController.vb:L262 tested for -1 and then rewrote its own
// arguments to page 0 with a page size of Integer.MaxValue. That sentinel is not carried forward.
// The unpaged case has its own named factory on the paged envelope, so the intent is stated rather
// than encoded in a magic number, and negative page coordinates are rejected outright.
//
// MIGRATION: 5 of 15. Absence is a nullable type, never a numeric sentinel. The legacy null
// contract at Library/Components/Shared/Null.vb:L41 defines its absent-Integer marker as -1, and
// CreatePortal at PortalController.vb:L980 documents that same value as its failure return,
// tested at L990. But the schema declares Portals.PortalID as IDENTITY (-1, 1) at
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77, so -1 is the
// identifier of the first portal an installation creates, and the shipped default portal occupies
// the next value, 0. Both are real, addressable identifiers. Every optional identifier on this
// contract is therefore a nullable Integer whose null means "not supplied"; no implementation may
// coalesce -1 or 0 to null, and no consumer may read either value as meaning absent.
//
// MIGRATION: 6 of 15. GetCurrentPortalSettings at PortalController.vb:L1209 read the tenant from
// MIGRATION: ambient per-request state, as one cast of HttpContext.Current.Items("PortalSettings"),
// and is not ported. Tenant identity is supplied instead by the domain layer's immutable scoped
// portal context, resolved once per request by the API layer's alias-resolution middleware.
// Consequently no member below omits a portal identifier on the assumption that ambient state will
// supply one.
//
// MIGRATION: 7 of 15. Four disc-measurement members are not ported, because file management is
// beyond the migrated scope.
// MIGRATION: GetPortalSpaceUsed at PortalController.vb:L1596 is additionally marked <Obsolete> in
// MIGRATION: the source, superseded there by GetPortalSpaceUsedBytes, so omission rather than
// translation of its optional parameter is the correct treatment.
// MIGRATION: GetPortalSpaceUsedBytes at L1278 and L1296, and HasSpaceAvailable at L1323, all walk
// the portal's files on disc.
// The quota columns are a separate matter and DO survive: HostSpace, PageQuota and UserQuota are
// stored values on the portal record - arguments 11, 12 and 13 of the twenty-seven-argument
// update - and remain on the portal transfer objects. The stored limit is kept; only its
// measurement against a file system is dropped.
//
// MIGRATION: 8 of 15. ProcessResourceFile at PortalController.vb:L1483 read a portal template's
// MIGRATION: companion .resx resource file and is not ported, because the localisation mechanism
// is not migrated. The legacy resource files are read as the authority for English wording and are
// never executed as a resource pipeline, so no member here accepts or returns a resource
// identifier.
//
// MIGRATION: 9 of 15. ParsePanes at PortalController.vb:L1624 took a document-object-model node
// together with the untyped key-value collection type of the era. It is a template-parsing helper
// that was public by accident rather than by design, and it does not appear on this contract: no
// document type and no untyped collection is exposed here, and template parsing stays inside the
// service implementation. What is deliberately deferred is named precisely - the standalone
// re-application of a template to an EXISTING portal, the behaviour behind the legacy
// Website/admin/Portal/Template.ascx.vb screen and the template step of SiteWizard.ascx.vb.
// Template application at CREATION time is preserved and reachable, because CreatePortalRequest
// already carries the template selection that ParseTemplate at PortalController.vb:L1360 consumed
// on the one path the legacy signup screen ever triggered. A standalone member would additionally
// require a template-merge mode type that the migration plan does not name and that no sibling
// file is assigned to author, so declaring it here would leave a reference nothing can resolve.
//
// MIGRATION: 10 of 15. Portal creation becomes atomic. CreatePortal at PortalController.vb:L980
// writes across the Portals, PortalAlias, Roles, Tabs and Modules tables in a sequence that is not
// transactional across statements: a failure part-way through left a partially built tenant behind,
// which is exactly why the legacy body accumulates a message string as it goes. The single create
// member below commits once, through the domain layer's unit of work, so the tenant either exists
// completely or not at all.
//
// MIGRATION: 11 of 15. Alias matching becomes exact. The legacy tenant-resolution procedure
// GetPortalSettings, at 01.00.00.SqlDataProvider:L4569-L4600, selected the lowest matching
// identifier using a substring predicate that wrapped the supplied alias in wildcards, so an alias
// that was a substring of a different tenant's alias could resolve to the wrong tenant. Every
// alias comparison behind this contract - the duplicate check on add, and lookup - compares
// exactly. This is a deliberate correction of a latent multi-tenant defect, not an incidental
// change.
//
// MIGRATION: 12 of 15. The pre-generics alias collection wrapper produces no target type.
// GetPortalAliasByPortalID at PortalAliasController.vb:L67 and GetPortalAliases at L86 both
// returned it, and it is subsumed by a read-only generic sequence of alias transfer objects. Its
// keyed-lookup behaviour is not reproduced either: it keyed entries by the lower-cased alias, which
// is a caching concern rather than a contract.
//
// MIGRATION: 13 of 15. The fee clamps inside the role-creation step of portal creation, at
// PortalController.vb:L395 and L398, are recorded here so the implementer cannot lose them. Each
// reads CType(IIf(fee < 0, 0, fee), Single) and becomes Math.Max(fee, 0f). The substitution is
// exact rather than merely close: IIf is a function and evaluates BOTH arms, whereas the C#
// conditional operator short-circuits - but both arms here are side-effect-free literals, so no
// observable behaviour changes. The clamp is a detail of the implementation, so it is documented
// rather than exposed as a member.
//
// MIGRATION: 14 of 15. Expiry maintenance is omitted. DeleteExpiredPortals at
// PortalController.vb:L156, GetExpiredPortals at L273 and UpdatePortalExpiry at L1495 exist to
// service a background sweep; the scheduling subsystem they belong to is beyond the migrated
// scope, and no endpoint or screen in the plan invokes them. Two further observations justify
// dropping rather than porting them: DeleteExpiredPortals discards the outcome string of every
// delete it attempts, so the last-portal rule was silently swallowed there; and UpdatePortalExpiry
// inverts its own null test at L1502, converting the value it has just found to be null and
// falling back to the current time only when a real date is present. That is a discovered legacy
// defect, recorded rather than repaired. ExpiryDate itself survives as a stored value - argument 5
// of the twenty-seven-argument update - and remains on the portal transfer objects.

using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for the portal aggregate - the multi-tenant site container - and for
/// the aliases through which each tenant is reached.
/// </summary>
/// <remarks>
/// <para>
/// Scope. This contract owns the tenant container itself: listing, reading, creating, modifying and
/// removing a portal, projecting its configuration for display, and managing the host names bound
/// to it. It is consumed by <c>PortalsController</c> at <c>/api/v1/portals</c> and by
/// <c>PortalAliasesController</c> at <c>/api/v1/portals/{portalId}/aliases</c>, and it is
/// implemented by <c>Application/Services/PortalService.cs</c>.
/// </para>
/// <para>
/// One outcome shape, everywhere. Every member returns <see cref="Result"/> or
/// <see cref="Result{T}"/>. That uniformity is deliberate: a controller's whole job is to translate
/// an outcome into a status code, and a surface that mixed a bare payload with a wrapped one would
/// force the controller to branch on shape before it could even begin translating. It also gives a
/// listing operation somewhere to report an invalid paging request, since the paged envelope's own
/// factory throws on negative coordinates rather than reporting them.
/// </para>
/// <para>
/// Absent is not failed. Every single-item member is typed <c>Result&lt;TDto?&gt;</c> with a
/// nullable payload. A successful outcome carrying a <see langword="null"/> value means the record
/// does not exist - which a controller renders as 404 - whereas a failed outcome means the
/// operation could not be attempted. Collapsing the two would change branch outcomes, because the
/// legacy readers signalled a missing row by returning nothing at all while signalling a refused
/// operation through a message. A successful outcome may additionally carry an advisory reason
/// without that implying failure.
/// </para>
/// <para>
/// Failure codes. Expected failures are returned, never thrown, and each carries one of the
/// following stable codes: <c>portal.not_found</c>, the named portal does not exist;
/// <c>portal.last_remaining</c>, the delete was refused because one portal must survive;
/// <c>portal.creation_failed</c>, creation could not be completed and nothing was committed;
/// <c>portal.paging_invalid</c>, the supplied page coordinates are not usable;
/// <c>portal.alias_not_found</c>, the named alias does not exist; and
/// <c>portal.alias_duplicate</c>, the submitted host name is already bound. Unexpected faults are
/// left to surface and are translated once, at the API edge.
/// </para>
/// <para>
/// A route-supplied identifier is authoritative. Where a member takes both an identifier and a
/// payload, the identifier is the one bound from the route and it wins. An implementation must
/// never retarget a write using an identifier carried in the payload: doing so would let a caller
/// edit another tenant's record by editing the body of a request it is otherwise entitled to make.
/// </para>
/// <para>
/// Configuration is columns, not a key-value bag. There is no <c>PortalSettings</c> table anywhere
/// in the shipped schema and no key-value setting entity for a portal; <c>PortalSettingsDto</c> is
/// a projection of stored columns on the portal record. This contract therefore exposes no
/// setting-by-key reader or writer and no untyped string map of settings, and an implementer must
/// not reintroduce one.
/// </para>
/// <para>
/// Paging semantics are defined elsewhere. The meaning of a page index, the base it counts from,
/// and how an unpaged answer is expressed all belong to <c>Domain/Common/PagedResult.cs</c> and are
/// documented there. Nothing here restates them, so there is exactly one place to read them.
/// </para>
/// <para>
/// Deliberately absent, with reasons. No member measures disc consumption, accepts a file-system
/// path, processes a resource file, exposes a document node or an untyped collection, reports a
/// portal tally, reads ambient request state, or services an expiry sweep. Each omission is
/// recorded against the legacy member it retires in the migration notes at the head of this file.
/// No member is synchronous, and none takes an output or by-reference argument.
/// </para>
/// <para>
/// Implementer's checklist. Reach data only through the domain layer's repository abstractions -
/// never a database context, a query root or SQL text. Commit a multi-table write once, through the
/// unit of work. Compare aliases exactly. Keep the fee clamp described in the migration notes
/// above. Cache behind the domain layer's cache abstraction, keeping the legacy key names, rather
/// than exposing anything cache-shaped here. Return an empty sequence, never
/// <see langword="null"/>, for a collection payload. Honour cancellation on every path.
/// </para>
/// </remarks>
public interface IPortalService
{
    // MIGRATION: 15 of 15. The legacy name filter was a raw pattern. The GetPortalsByName
    // procedure, added at 04.04.00.SqlDataProvider, matches with "WHERE PortalName LIKE
    // @NameToMatch" and adds no wildcards of its own, so whatever the caller supplied was
    // interpreted as a pattern by the database. Accepting a pattern from an HTTP caller is not
    // carried forward: the filter below is a literal substring to be found, with pattern
    // metacharacters escaped by the implementation. This is a deliberate divergence - it removes a
    // caller's ability to inject matching syntax or to force a scan with a leading wildcard - and
    // it is distinct from the alias change in note 11, which concerns exact host-name matching and
    // not this search box.

    /// <summary>
    /// Lists portals, optionally narrowed by name, as one page of a larger set.
    /// </summary>
    /// <param name="request">
    /// The page of records being asked for. Page coordinates that cannot be honoured are reported
    /// as a failure rather than silently adjusted.
    /// </param>
    /// <param name="nameFilter">
    /// An optional literal fragment of a portal's name. When <see langword="null"/>, empty or
    /// white-space, no name narrowing is applied and every portal the caller may see is eligible.
    /// The value is matched as literal text, not as a pattern.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying one page of portals, which is empty when nothing matched.
    /// Fails with <c>portal.paging_invalid</c> when the requested page coordinates are not usable.
    /// </returns>
    /// <remarks>
    /// Consolidates the two legacy listing members: <c>GetPortals</c>
    /// (<c>PortalController.vb:L1263</c>), which returned every portal, and
    /// <c>GetPortalsByName</c> (<c>PortalController.vb:L262</c>), which returned a page and
    /// reported the grand total through a by-reference argument. Omitting
    /// <paramref name="nameFilter"/> reproduces the first; supplying it reproduces the second. An
    /// empty page is a success, not a failure - a filter that matches nothing is a legitimate
    /// answer.
    /// </remarks>
    Task<Result<PagedResult<PortalListItemDto>>> ListPortalsAsync(
        PagedRequest request,
        string? nameFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one portal in full.
    /// </summary>
    /// <param name="portalId">Identifier of the portal to read.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome whose value is the portal, or a successful outcome whose value is
    /// <see langword="null"/> when no portal carries that identifier. Absence is reported as
    /// success with no value, never as a failure, so a caller can distinguish "there is no such
    /// tenant" from "the lookup could not be performed".
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetPortal</c> (<c>PortalController.vb:L1224</c>), which returned nothing at all
    /// for a missing row. Every value of <paramref name="portalId"/> is meaningful, including
    /// negative values and zero, so an implementation must not short-circuit on a particular number
    /// and must not treat one as a request for "no portal".
    /// </remarks>
    Task<Result<PortalDetailDto?>> GetPortalAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a portal together with the records a working tenant requires.
    /// </summary>
    /// <param name="request">The portal to create.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the created portal, including the identifier the database
    /// assigned, so that a caller can answer 201 Created with a location for the new resource.
    /// Fails with <c>portal.creation_failed</c> when the tenant could not be built, and with
    /// <c>portal.alias_duplicate</c> when the requested host name is already bound to a portal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the fifteen-argument <c>CreatePortal</c> (<c>PortalController.vb:L980</c>). The
    /// value is never <see langword="null"/> on success: a created portal always exists, so unlike
    /// the read members this payload is non-nullable.
    /// </para>
    /// <para>
    /// The write spans the <c>Portals</c>, <c>PortalAlias</c>, <c>Roles</c>, <c>Tabs</c> and
    /// <c>Modules</c> tables and is committed exactly once through the domain layer's unit of work,
    /// so a partially built tenant cannot be left behind. On failure nothing is committed, and the
    /// failure code - not a returned identifier - reports what happened.
    /// </para>
    /// </remarks>
    Task<Result<PortalDetailDto>> CreatePortalAsync(
        CreatePortalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies an existing portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal to modify. This value is authoritative: if
    /// <paramref name="request"/> also carries an identifier, it is ignored, so a caller cannot
    /// retarget the write at another tenant.
    /// </param>
    /// <param name="request">The values to store.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the stored portal, so that a caller can answer 200 OK with the
    /// current state; or a successful outcome whose value is <see langword="null"/> when no portal
    /// carries that identifier, which a caller renders as 404.
    /// </returns>
    /// <remarks>
    /// Replaces both legacy update overloads: the twenty-seven-argument
    /// <c>UpdatePortalInfo</c> (<c>PortalController.vb:L1568</c>) and the record-taking overload at
    /// <c>L1524</c> that merely unpacked into it. This is the single write path for every settable
    /// column on a portal, which is why no separate settings-writing member exists; two write paths
    /// over the same columns could diverge.
    /// </remarks>
    Task<Result<PortalDetailDto?>> UpdatePortalAsync(
        int portalId,
        UpdatePortalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a portal and the records that depend on it.
    /// </summary>
    /// <param name="portalId">Identifier of the portal to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome when the portal was removed, which a caller answers as 204 No Content.
    /// Fails with <c>portal.not_found</c> when no portal carries that identifier, and with
    /// <c>portal.last_remaining</c> when removal was refused because an installation must retain at
    /// least one portal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>DeletePortal</c> (<c>PortalController.vb:L162</c>), which reported an error
    /// message string, and <c>DeletePortalInfo</c> (<c>PortalController.vb:L1191</c>), which
    /// performed the database half of the same operation.
    /// </para>
    /// <para>
    /// The last-portal rule is decided inside the implementation and surfaced as
    /// <c>portal.last_remaining</c>. This contract deliberately exposes no way to ask how many
    /// portals exist, because that would move the decision into the caller.
    /// </para>
    /// <para>
    /// No file-system location is accepted. The legacy member took a server path so it could delete
    /// the tenant's folders; file management is beyond the migrated scope, so only the stored
    /// records are removed and any on-disc residue is left for an operator to clear.
    /// </para>
    /// </remarks>
    Task<Result> DeletePortalAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a portal's configuration, projected for display.
    /// </summary>
    /// <param name="portalId">Identifier of the portal whose configuration is wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the configuration, or a successful outcome whose value is
    /// <see langword="null"/> when no portal carries that identifier.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Serves the dedicated configuration screen at <c>/portals/{portalId}/settings</c>, whose
    /// legacy counterparts are <c>Website/admin/Portal/SiteSettings.ascx.vb</c> and the
    /// configuration step of <c>SiteWizard.ascx.vb</c>.
    /// </para>
    /// <para>
    /// This is a projection of stored columns on the portal record, not a bag of keyed values.
    /// There is no <c>PortalSettings</c> table anywhere in the shipped schema - the schema defines
    /// <c>ModuleSettings</c>, <c>HostSettings</c>, <c>TabModuleSettings</c> and
    /// <c>ScheduleItemSettings</c>, and none for a portal - and the legacy type of the same name was
    /// a per-request composite assembled in memory rather than a persisted record. An implementer
    /// must therefore not add a setting-by-key reader or writer here.
    /// </para>
    /// <para>
    /// Reading is separated from writing on purpose. There is no matching configuration-writing
    /// member: every settable column is written through
    /// <see cref="UpdatePortalAsync(int, UpdatePortalRequest, CancellationToken)"/>, which is the
    /// single successor to the one legacy write path for all of them. This member exists because the
    /// configuration screen needs a projection shaped for it, not because configuration is stored
    /// separately.
    /// </para>
    /// </remarks>
    Task<Result<PortalSettingsDto?>> GetPortalSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the aliases bound to one portal, or every alias in the installation.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal whose aliases are wanted, or <see langword="null"/> to list every
    /// alias across every portal.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the aliases, which is an empty sequence - never
    /// <see langword="null"/> - when there are none. Fails with <c>portal.not_found</c> when a
    /// portal identifier is supplied and no portal carries it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Consolidates three legacy readers: <c>GetPortalAliasArrayByPortalID</c>
    /// (<c>PortalAliasController.vb:L44</c>), <c>GetPortalAliasByPortalID</c> (<c>L67</c>) and
    /// <c>GetPortalAliases</c> (<c>L86</c>). The first two differed only in the collection type they
    /// built from the same query, and the pre-generics wrapper the second returned has no successor
    /// type.
    /// </para>
    /// <para>
    /// The wildcard sentinel is gone. <c>GetPortalAliases</c> asked for every alias by delegating to
    /// the by-portal reader with -1 as the portal identifier, a value the underlying procedure
    /// treated as "match every row" - yet -1 is also a real portal identifier, because
    /// <c>Portals.PortalID</c> seeds its identity at that value. Here the unfiltered case is
    /// <see langword="null"/>, so it cannot collide with a genuine identifier.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<PortalAliasDto>>> ListPortalAliasesAsync(
        int? portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one alias.
    /// </summary>
    /// <param name="portalAliasId">Identifier of the alias to read.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the alias, or a successful outcome whose value is
    /// <see langword="null"/> when no alias carries that identifier.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetPortalAliasByPortalAliasID</c> (<c>PortalAliasController.vb:L63</c>) and
    /// serves the single-alias edit screen, whose legacy counterpart is
    /// <c>Website/admin/Portal/EditPortalAlias.ascx.vb</c>. The alias identifier is a surrogate key
    /// declared IDENTITY (1, 1), so it is unique across the installation and identifies the record
    /// without a portal identifier alongside it.
    /// </remarks>
    Task<Result<PortalAliasDto?>> GetPortalAliasAsync(
        int portalAliasId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds a new alias to a portal.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the portal to bind the alias to. This value is authoritative and the portal
    /// identifier carried in <paramref name="alias"/> is ignored, so a caller cannot bind a host
    /// name to a tenant other than the one addressed by the route.
    /// </param>
    /// <param name="alias">
    /// The alias to bind. Only the host name is read; the alias identifier is assigned by the
    /// database and any value supplied for it is ignored.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the stored alias, including the identifier the database
    /// assigned, so that a caller can answer 201 Created with a location for the new resource. Fails
    /// with <c>portal.not_found</c> when no portal carries that identifier, and with
    /// <c>portal.alias_duplicate</c> when the host name is already bound.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two legacy entry points collapse here: <c>PortalAliasController.AddPortalAlias</c>
    /// (<c>PortalAliasController.vb:L28</c>), which inserted unconditionally, and
    /// <c>PortalController.AddPortalAlias</c> (<c>PortalController.vb:L935</c>), which first looked
    /// the alias up and did nothing at all if it already existed.
    /// </para>
    /// <para>
    /// That silent skip is not carried forward. A duplicate is reported as
    /// <c>portal.alias_duplicate</c> rather than being absorbed, because a caller that asks to bind
    /// a host name and receives success is entitled to conclude that its own request took effect.
    /// The duplicate test compares host names exactly.
    /// </para>
    /// </remarks>
    Task<Result<PortalAliasDto>> AddPortalAliasAsync(
        int portalId,
        PortalAliasDto alias,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies an existing alias.
    /// </summary>
    /// <param name="portalAliasId">
    /// Identifier of the alias to modify. This value is authoritative and any identifier carried in
    /// <paramref name="alias"/> is ignored.
    /// </param>
    /// <param name="alias">The values to store. Only the host name is read.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome when the alias was stored. Fails with <c>portal.alias_not_found</c> when
    /// no alias carries that identifier, and with <c>portal.alias_duplicate</c> when the new host
    /// name is already bound to another alias.
    /// </returns>
    /// <remarks>
    /// Replaces <c>UpdatePortalAliasInfo</c> (<c>PortalAliasController.vb:L94</c>). An alias cannot
    /// be moved between portals through this member: the owning portal is fixed when the alias is
    /// bound, matching the legacy screen, which offered only the host name for editing.
    /// </remarks>
    Task<Result> UpdatePortalAliasAsync(
        int portalAliasId,
        PortalAliasDto alias,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unbinds an alias from its portal.
    /// </summary>
    /// <param name="portalAliasId">Identifier of the alias to unbind.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome when the alias was removed, which a caller answers as 204 No Content.
    /// Fails with <c>portal.alias_not_found</c> when no alias carries that identifier.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeletePortalAlias</c> (<c>PortalAliasController.vb:L34</c>), which removed the
    /// record whether or not it existed and reported nothing either way. Reporting
    /// <c>portal.alias_not_found</c> lets a caller distinguish a removal it caused from one that had
    /// already happened.
    /// </remarks>
    Task<Result> DeletePortalAliasAsync(
        int portalAliasId,
        CancellationToken cancellationToken = default);
}
