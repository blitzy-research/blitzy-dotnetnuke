using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: This contract realises the portal slice of the legacy data surface -
//            Library/Components/Providers/Data/DataProvider.vb lines 92 to 107, fifteen
//            members among the 269 MustOverride members declared on one 397-line
//            abstract class. Only the portal aggregate appears here. The god-interface is
//            deliberately not reproduced: each aggregate owns a narrow contract of its
//            own, so a consumer depends on the portal surface alone rather than on every
//            table in the installation.
// MIGRATION: The legacy accessor was a reflection-resolved static singleton - a private
//            provider-type constant, a shared constructor and a shadowed Instance
//            function at DataProvider.vb lines 29 to 50 - which bound every caller to a
//            configuration-driven type lookup resolved at first use. Implementations of
//            this interface are supplied by constructor injection instead, so the
//            dependency is declared, substitutable and verifiable at compile time.
// MIGRATION: Legacy AddPortalInfo (DataProvider.vb:L93) also created the administrator
//            user; that responsibility moves to Application PortalService, which stages
//            both aggregates and commits via IUnitOfWork.
// MIGRATION: Legacy UpdatePortalInfo (27 positional arguments, DataProvider.vb:L104) and
//            UpdatePortalSetup (9 positional arguments, L105) both wrote the same Portals
//            row and collapse into a single entity-oriented UpdateAsync.
// MIGRATION: GetPortalSpaceUsed (DataProvider.vb:L103, PortalController.vb:L1596 marked
//            obsolete) is omitted - it aggregates file storage and the FileSystem
//            subsystem is out of scope; there is no File entity.
// MIGRATION: GetExpiredPortals (DataProvider.vb:L96) is omitted - host-level super-user
//            administration is out of scope.
// MIGRATION: No portal-settings member exists here, and none is missing. There is no
//            PortalSetting entity and no such table anywhere in the 88-script schema
//            chain, which defines only ModuleSettings, HostSettings, TabModuleSettings
//            and ScheduleItemSettings. The 269-member legacy provider declares no
//            settings member for a portal either. What the legacy code called
//            "PortalSettings" was a per-request ambient composite read back from the
//            request item bag at PortalController.vb lines 1209 to 1210, not a persisted
//            aggregate. Portal configuration is held as columns on the Portals table and
//            therefore as properties on Portal.
// MIGRATION: Every read that returned a raw forward-only reader, and the untyped
//            non-generic list returned by PortalController.vb:L1263, are replaced by
//            materialised results - a nullable entity, a read-only list or a paged
//            envelope. Nothing deferred crosses this boundary, so no query can be
//            enumerated after the session that produced it has gone.
// MIGRATION: The legacy row-by-row hydration performed by the reflection-based filler in
//            Library/Components/Shared/CBO.vb, and the hand-written column assignments
//            that shadowed it, produce no member and no target type. Object
//            materialisation belongs to the persistence technology in the outer layer.
// MIGRATION: Caching produces no member and no parameter here. The legacy portal
//            controller interleaved cache reads, a performance-multiplier expiry
//            calculation and portal-scoped and host-scoped invalidations with its data
//            access (PortalController.vb lines 211, 218, 240, 916 and 1128). Caching is a
//            separate concern behind its own abstraction, so no read on this contract
//            accepts a cache hint and no member clears a cache.

/// <summary>
/// The persistence contract for the <see cref="Portal"/> aggregate: DotNetNuke's
/// multi-tenant site container and the root tenant boundary of the application.
/// </summary>
/// <remarks>
/// <para>
/// This is the only route from the application layer to persisted portal state. It
/// exposes no persistence session, no transaction handle, no deferred query surface and
/// no provider type, so a consumer cannot name - and therefore cannot depend upon - the
/// technology that stores a portal.
/// </para>
/// <para>
/// <b>Writes stage; they do not commit.</b> The three write members record an intention
/// against the current unit of work and return once it is recorded. Nothing is durable
/// until <see cref="IUnitOfWork.SaveChangesAsync"/> is called, which is what allows a
/// portal, its aliases, its stock roles, its pages and its modules to be written as one
/// indivisible batch instead of the five independent statement sequences the legacy
/// controller issued at PortalController.vb line 980.
/// </para>
/// <para>
/// <b>Both minus one and zero are legitimate portal identifiers.</b> The Portals table
/// declares <c>PortalID</c> as <c>IDENTITY (-1, 1)</c>
/// (01.00.00.SqlDataProvider:L77), so the first generated tenant bears minus one, and the
/// shipped default portal is inserted under an explicit identity override as portal zero
/// (01.00.00.SqlDataProvider:L7123-L7126). The legacy null helper set its integer
/// sentinel to minus one and reported that value as absent, so it could not tell a real
/// tenant from a missing one; the shipped code then passed that same sentinel as a
/// genuine portal argument. No member of this contract treats either value as absent.
/// Absence is expressed only by a <see langword="null"/> entity or an empty result, never
/// by a numeric sentinel, and no member applies a range or sign constraint to an
/// identifier.
/// </para>
/// <para>
/// Identifiers cross this boundary as plain framework integers and strings rather than as
/// identifier value objects. The value objects exist for application-facing boundaries;
/// the entities declare plain scalars for their own identity, and the conversion between
/// the two belongs to the persistence configuration in the outer layer.
/// </para>
/// </remarks>
public interface IPortalRepository
{
    /// <summary>
    /// Returns one page of portals, optionally narrowed by name and ordered by a named
    /// property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>GetPortalsByName</c> (DataProvider.vb:L102), which accepted a name
    /// fragment and a page coordinate pair, and supersedes the untyped non-generic list
    /// returned by <c>GetPortals</c> (PortalController.vb:L1263). Ordering is carried as
    /// well, because paging is applied here: a page taken from an unordered relational
    /// read has no defined row assignment, so ordering cannot be deferred to the caller
    /// without making successive pages repeat or omit rows.
    /// </para>
    /// <para>
    /// The five inputs are one cohesive query descriptor - a filter, an order and a page
    /// coordinate pair - and are not an entity field list. This member is deliberately
    /// the widest on the contract, and it remains far removed from the legacy parameter
    /// explosions that motivated collapsing writes onto entities.
    /// </para>
    /// </remarks>
    /// <param name="pageIndex">
    /// The zero-based index of the page to return, matching the base fixed by
    /// <see cref="PagedResult{T}"/>. Page zero is the first page.
    /// </param>
    /// <param name="pageSize">
    /// The maximum number of records on the page. A page size of zero requests every
    /// match as a single unpaged result, which is how a caller asks for a complete set
    /// without inventing a sentinel coordinate.
    /// </param>
    /// <param name="nameFilter">
    /// A case-insensitive fragment matched anywhere within the portal name, or
    /// <see langword="null"/> to match every portal.
    /// </param>
    /// <param name="sortBy">
    /// The name of the property to order by, or <see langword="null"/> for the default
    /// order. An implementer must resolve this against a closed set of properties it
    /// recognises and fall back to the default order otherwise, so that a caller-supplied
    /// value can never reach the store as an ordering expression.
    /// </param>
    /// <param name="descending">Whether the resolved order is reversed.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The requested page together with the total number of matches across every page.
    /// </returns>
    Task<PagedResult<Portal>> ListAsync(
        int pageIndex,
        int pageSize,
        string? nameFilter,
        string? sortBy,
        bool descending,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every portal in the installation.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetPortals</c> (DataProvider.vb:L101) and to its controller-level
    /// counterpart at PortalController.vb:L1263, which returned an untyped non-generic
    /// list. This is the named way to request an unpaged set: a caller must never express
    /// "everything" by passing a negative page coordinate, which is the legacy convention
    /// this contract removes and which would in any case collide with minus one being a
    /// real portal identifier.
    /// </remarks>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// Every portal, materialised, in a deterministic order. An installation with no
    /// portal yields an empty list rather than <see langword="null"/>.
    /// </returns>
    Task<IReadOnlyList<Portal>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal bearing the supplied identifier, or <see langword="null"/> when
    /// no portal bears it.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetPortal</c> (DataProvider.vb:L97). A missing portal is reported as
    /// <see langword="null"/>; it is never reported as a sentinel identifier, because
    /// both minus one and zero are real identifiers in this schema.
    /// </remarks>
    /// <param name="portalId">
    /// The portal identifier. Minus one and zero are legitimate values and are looked up
    /// like any other.
    /// </param>
    /// <param name="includeAliases">
    /// Whether the portal's aliases are loaded alongside it. This selects the shape of a
    /// single read so a caller that needs the aliases does not issue a second one; it
    /// carries no business meaning.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The portal, or <see langword="null"/>.</returns>
    Task<Portal?> GetByIdAsync(
        int portalId,
        bool includeAliases = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the portal that owns the supplied host alias, or <see langword="null"/>
    /// when no portal claims it.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetPortalByAlias</c> (DataProvider.vb:L98). This is tenant
    /// resolution, and it is the portal lookup keyed by an alias string rather than any
    /// part of alias management, which belongs to the alias aggregate's own contract.
    /// <para>
    /// The alias must be matched <b>exactly</b>. The legacy resolution procedure selected
    /// the lowest matching identifier using a leading-and-trailing wildcard comparison
    /// against the alias column, so one tenant's alias that happened to be a substring of
    /// another's could resolve a request to the wrong tenant. That is a deliberate
    /// behavioural correction rather than an incidental one, and it is recorded in the
    /// migration notes.
    /// </para>
    /// </remarks>
    /// <param name="httpAlias">
    /// The host alias to resolve, as it is stored - a host name with an optional port and
    /// path. An implementer must compare it case-insensitively and must not widen the
    /// comparison to a partial match.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The owning portal, or <see langword="null"/>.</returns>
    Task<Portal?> GetByAliasAsync(string httpAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the portal reached by the supplied page under the supplied host alias, or
    /// <see langword="null"/> when the pairing does not resolve.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetPortalByTab</c> (DataProvider.vb:L99). The legacy procedure
    /// resolved the alias to a tenant and then confirmed that the requested page belonged
    /// to it, so a page identifier from one tenant presented under another tenant's alias
    /// resolved to nothing. Both halves of that check are required: the pairing must
    /// resolve as a unit, which is what stops a page identifier being used to read across
    /// a tenant boundary.
    /// </remarks>
    /// <param name="tabId">
    /// The page identifier. Zero is a legitimate value, because the page table also seeds
    /// its identity from zero.
    /// </param>
    /// <param name="httpAlias">The host alias the page was requested under.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The portal that owns both the alias and the page, or <see langword="null"/> when
    /// the alias is unknown or the page belongs to a different portal.
    /// </returns>
    Task<Portal?> GetByTabAsync(int tabId, string httpAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a portal bearing the supplied identifier exists.
    /// </summary>
    /// <remarks>
    /// Traces to <c>VerifyPortal</c> (DataProvider.vb:L107), which reported existence by
    /// returning a reader the caller had to test for a row. Existence is a boolean, so it
    /// is answered as one, and the record itself is not materialised.
    /// </remarks>
    /// <param name="portalId">The portal identifier. Minus one and zero are legitimate values.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the portal exists; otherwise <see langword="false"/>.
    /// </returns>
    Task<bool> ExistsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether the supplied page belongs to the supplied portal.
    /// </summary>
    /// <remarks>
    /// Traces to <c>VerifyPortalTab</c> (DataProvider.vb:L106), which likewise reported a
    /// boolean fact through a reader. This is the tenant-isolation check a caller makes
    /// before acting on a page it was handed an identifier for, so a caller scoped to one
    /// portal cannot be induced to operate on another portal's page.
    /// </remarks>
    /// <param name="portalId">The portal the page is expected to belong to.</param>
    /// <param name="tabId">The page identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the page exists and belongs to that portal; otherwise
    /// <see langword="false"/>, including when neither exists.
    /// </returns>
    Task<bool> TabBelongsToPortalAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the portals in the installation.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetPortalCount</c> (DataProvider.vb:L100). The legacy surface needed a
    /// standalone count because its paged read could not report a grand total; that need
    /// is met within a page by <see cref="PagedResult{T}"/>. This member is retained for
    /// the callers that want the installation-wide tally on its own, without reading a
    /// page of records to obtain it.
    /// </remarks>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The number of portals, which may be zero.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the users who are members of the supplied portal.
    /// </summary>
    /// <remarks>
    /// Membership of a tenant is a row in the portal-membership table rather than a
    /// column on the user, so the tally is taken there. The legacy administration grid
    /// obtained this figure from a correlated sub-select inside the portal view, so it was
    /// computed per row there as well; it is a projection over related data and is
    /// therefore not a stored column on <see cref="Portal"/>.
    /// </remarks>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The number of member accounts, which is zero for a portal with no members and for
    /// an identifier no portal bears.
    /// </returns>
    Task<int> CountUsersAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the supplied portal's pages the way the legacy administration grid counted them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE TALLY IS THE TERMINAL <c>GetTabCount</c> AND NOTHING ELSE, because that procedure
    /// is what the legacy grid actually displayed: <c>PortalInfo.Pages</c>
    /// (<c>PortalInfo.vb</c> lines 320-325) resolved its value through
    /// <c>TabController.GetTabCount(PortalID)</c>. The terminal definition
    /// (<c>04.04.00.SqlDataProvider</c> lines 511-527) reads the portal's
    /// <c>AdminTabId</c> and then issues
    /// <c>SELECT COUNT(*) - 1 FROM Tabs WHERE PortalID = @PortalID AND TabID &lt;&gt;
    /// @AdminTabId AND (ParentId &lt;&gt; @AdminTabId OR ParentId IS NULL)</c>.
    /// </para>
    /// <para>
    /// Three parts of that are counter-intuitive, and all three are preserved under Rule T5
    /// rather than corrected. FIRST, the administration page and its DIRECT children are
    /// excluded - only direct children, because the predicate tests <c>ParentId</c> and
    /// nothing deeper, so a grandchild of the administration page is counted. SECOND, a page
    /// in the recycle bin IS counted: the procedure states no soft-delete condition at all,
    /// so an implementation that excluded soft-deleted pages would report a figure the
    /// legacy grid never showed. THIRD, one is subtracted from the total, which is what the
    /// legacy figure meant by "pages" and is not an off-by-one.
    /// </para>
    /// <para>
    /// Like the member tally, this is a projection over related data rather than a stored
    /// column. <see cref="ITabRepository.CountByPortalIdAsync"/> answers the same legacy
    /// question from the page side and must agree with this member exactly.
    /// </para>
    /// </remarks>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The legacy page tally. MINUS ONE for a portal that does not exist and for a portal
    /// that records no administration page - the two are indistinguishable, as they were to
    /// the legacy statement, where a null <c>@AdminTabId</c> made every row's predicate
    /// unknown and <c>COUNT(*) - 1</c> evaluated to <c>0 - 1</c>. The value is an arithmetic
    /// consequence and NOT the legacy <c>Null.NullInteger</c> sentinel, so a caller must not
    /// read it as "unknown".
    /// </returns>
    Task<int> CountPagesAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the member accounts of every supplied portal in one read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answers the same question as <see cref="CountUsersAsync"/> for a whole page of
    /// portals at once. A listing screen needs one tally per row, and asking the
    /// single-portal member once per row issues a query per row: a page of fifty portals
    /// costs fifty round trips for a figure the store can produce in one grouped read.
    /// This member exists so that the cost of a listing is independent of its page size.
    /// </para>
    /// <para>
    /// The result is TOTAL over the supplied identifiers: every identifier that was asked
    /// for is present as a key, carrying zero where no membership row exists and where no
    /// portal bears the identifier. A caller therefore never has to distinguish "absent
    /// because it has no members" from "absent because the grouped read returned no row
    /// for it", which is the mistake a grouped projection invites. Duplicate identifiers
    /// in the request collapse to one entry.
    /// </para>
    /// </remarks>
    /// <param name="portalIds">
    /// The portal identifiers to tally. An empty collection is answered with an empty
    /// result and issues no query.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// One entry per distinct supplied identifier, mapping it to its member count.
    /// </returns>
    Task<IReadOnlyDictionary<int, int>> CountUsersForPortalsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the pages of every supplied portal in one read, on the same terms as
    /// <see cref="CountPagesAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The batched counterpart of <see cref="CountPagesAsync"/>, and it exists for the same
    /// reason as <see cref="CountUsersForPortalsAsync"/>: a listing needs one tally per row
    /// and must not pay a round trip per row to obtain it. The result is TOTAL over the
    /// supplied identifiers on the same terms.
    /// </para>
    /// <para>
    /// EVERY PART OF THE <c>GetTabCount</c> SEMANTICS APPLIES HERE TOO - the administration
    /// page and its direct children excluded, recycled pages included, one subtracted, and
    /// minus one for a portal with no administration page - so the two members cannot
    /// disagree. That agreement is the whole point of the member's existence: an
    /// implementation whose batched predicate drifts from its single one reports two
    /// different figures for the same tenant depending on which screen asked.
    /// </para>
    /// </remarks>
    /// <param name="portalIds">
    /// The portal identifiers to tally. An empty collection is answered with an empty
    /// result and issues no query.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// One entry per distinct supplied identifier, mapping it to its legacy page tally -
    /// minus one where the portal does not exist or records no administration page.
    /// </returns>
    Task<IReadOnlyDictionary<int, int>> CountPagesForPortalsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the names of the roles the supplied portal nominates as its administrator
    /// and registered-user roles.
    /// </summary>
    /// <remarks>
    /// The portal stores the two role identifiers, not their names; the legacy portal view
    /// resolved the names through correlated sub-selects. Only roles that actually exist
    /// are reported, which is what lets a caller distinguish a nomination that was never
    /// made from one pointing at a role that has since been deleted. Role identity seeds
    /// from zero, so a role identifier of zero is a legitimate key here.
    /// </remarks>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// A read-only map from role identifier to role name, holding at most the two
    /// nominated roles and holding only those that exist. An empty map is returned rather
    /// than <see langword="null"/>.
    /// </returns>
    Task<IReadOnlyDictionary<int, string>> GetRoleNamesAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new portal for insertion on the next unit-of-work commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>CreatePortal</c> (DataProvider.vb:L94) - the nine-argument member that
    /// wrote the Portals row and nothing else. It does <b>not</b> trace to the
    /// fourteen-argument <c>AddPortalInfo</c> at L93, which also created the portal's
    /// administrator from the given name, surname, username, password and address it was
    /// handed. Composing two aggregates in one call is exactly what this contract
    /// declines to reproduce: the application service stages the portal here, stages the
    /// administrator through the user contract, and commits both together.
    /// </para>
    /// <para>
    /// <b>This member yields no identifier, deliberately.</b> Each legacy add member
    /// returned a generated key because its procedure ended by reading back the scope
    /// identity. The store assigns the key when the batch is written, so returning one
    /// here would force this repository to commit on its own behalf - which would
    /// dissolve the single commit boundary and split the multi-table portal creation at
    /// PortalController.vb line 980 into independently durable statements, the precise
    /// defect that left a half-created portal unrecoverable. The generated key is instead
    /// observed on <see cref="Portal.PortalId"/> once
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> has returned.
    /// </para>
    /// </remarks>
    /// <param name="portal">
    /// The portal to insert. An implementer must reject <see langword="null"/> rather than
    /// stage nothing silently.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the operation. Staging is not a durable act, so an abandoned call leaves
    /// the store untouched.
    /// </param>
    /// <returns>
    /// A task that completes once the insertion is staged. It carries no value: see the
    /// remarks for why no identifier is yielded.
    /// </returns>
    Task AddAsync(Portal portal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the supplied portal's modified state for update on the next unit-of-work
    /// commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This one member replaces two legacy procedures that wrote the very same Portals
    /// row from opposite ends: <c>UpdatePortalInfo</c> (DataProvider.vb:L104), which took
    /// <b>27 positional arguments</b> covering the descriptive and configuration columns,
    /// and <c>UpdatePortalSetup</c> (L105), which took nine covering the administrator and
    /// the well-known page assignments. Splitting one row across two positional argument
    /// lists made every caller responsible for supplying every column in the right order,
    /// and made a partial update indistinguishable from an intentional overwrite with
    /// defaults.
    /// </para>
    /// <para>
    /// The entity carries its own modified state instead, so a caller reads a portal,
    /// changes what it means to change, and stages the result. Nothing is durable until
    /// the unit of work commits.
    /// </para>
    /// </remarks>
    /// <param name="portal">
    /// The portal whose modified state is staged. An implementer must reject
    /// <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation, leaving the store untouched.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    Task UpdateAsync(Portal portal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the portal bearing the supplied identifier for deletion on the next
    /// unit-of-work commit.
    /// </summary>
    /// <remarks>
    /// Traces to <c>DeletePortalInfo</c> (DataProvider.vb:L95), which likewise identified
    /// its target by identifier alone. Removing a portal removes the tenant, so an
    /// implementer is expected to let the schema's own referential rules carry the
    /// dependent rows rather than reproducing a deletion order here; sequencing dependent
    /// writes is not a decision a persistence contract should encode. When no portal bears
    /// the identifier, nothing is staged and the call succeeds, so a caller that has
    /// already established absence need not distinguish the two cases.
    /// </remarks>
    /// <param name="portalId">
    /// The identifier of the portal to delete. Minus one and zero are legitimate values
    /// and are treated as ordinary identifiers.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation, leaving the store untouched.</param>
    /// <returns>A task that completes once the deletion is staged, or once it is
    /// determined that there is nothing to stage.</returns>
    Task DeleteAsync(int portalId, CancellationToken cancellationToken = default);
}
