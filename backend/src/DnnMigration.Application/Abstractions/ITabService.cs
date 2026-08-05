// MIGRATION: This contract is DELIBERATELY NARROW, and that is its single most
// important property. The legacy page controller,
// Library/Components/Tabs/TabController.vb, is 1,302 lines and exposes 34 public
// members; exactly three of those concerns survive here, mirroring the three page
// endpoints this migration exposes - GET /api/v1/portals/{id}/tabs and
// GET/PUT /api/v1/tabs/{id}, and nothing else. Pages are a SUPPORTING aggregate:
// they are present only because module placement, page permissions and portal
// navigation are inseparable from them, and they are consumed as a LOOKUP by the
// module screens. The page transfer folder accordingly holds three types and no
// create request, and the client application declares no page feature area and no
// page route.
//
// MIGRATION: Do not "complete" this surface. "The legacy controller had it" is
// explicitly NOT a justification; a new member requires a named endpoint in the
// migration plan first. Each of the 31 omitted members is absent for a stated
// reason: create, delete, copy and recycle-bin/restore have no endpoint, and the
// static DeleteTab is doubly disqualified because it accepted the legacy per-request
// portal composite that an immutable request-scoped tenant context replaces;
// portal-template XML serialisation is scoped to the portal service and only as far
// as parsing requires, which is why no XML type crosses this contract in either
// direction and this file imports no XML namespace; design propagation dies with the
// excluded skinning and container subsystem; permission propagation belongs to the
// permission service, which keeps this contract free of any access-control decision;
// cache-shaped reads returning a keyed or entity-valued map have no target shape;
// and GetTabCount, GetTabByTabPath and GetTabByName are either answered by what
// survives - the complete sequence supplies the count, and the path is a field on
// every list row - or existed solely to serve the excluded create path.
//
// MIGRATION: FOUR legacy update members collapse into the single update member
// declared below - UpdateTab at L780, UpdateTabOrder at L816, the five-argument
// positional UpdateTabOrder at L1287, and UpdatePortalTabOrder at L550, a
// SEVEN-argument positional signature whose tail argument is an optional Boolean
// defaulted to False. That signature is itself the defect, so the replacement is
// neither a defaulted argument nor a set of overloads: the ordering inputs it
// carried positionally - parent, level, order and visibility - become named
// properties on one request object, and the optional tail becomes an explicit
// property rather than a default the caller cannot see. Ordering is therefore always
// stated, never inferred. The plan cites L243 as a second optional-argument site,
// but MoveTab there is Private and reached only from UpdatePortalTabOrder, so no
// public move or reorder member is invented to honour the citation - sibling
// renumbering, level recalculation and cycle rejection stay INSIDE the implementing
// service, which is where they belong.
//
// MIGRATION: caching is the implementing service's internal concern, expressed
// through the domain layer's cache abstraction, which absorbs the 12 cache call
// sites measured in the legacy controller. The legacy single-page reader at L467
// took a third argument that bypassed the cache; no equivalent flag crosses this
// contract, and a caller cannot and must not steer caching.
//
// MIGRATION: Numeric sentinels do NOT cross this contract; absence is carried by
// nullable types, because the legacy minus-one sentinel collides with real data
// twice over in this schema: 01.00.00.SqlDataProvider declares Tabs.TabID as
// IDENTITY (0, 1) at L140, so ZERO is a legitimate persisted page identifier, and
// Portals.PortalID as IDENTITY (-1, 1) at L77, so minus one is simultaneously the
// legacy "absent" marker AND a real portal identifier. Roles.RoleID and
// Modules.ModuleID also seed at zero. A root-level page's parent is therefore null,
// never minus one and never zero; treating zero as "unset" reintroduces precisely
// the defect this paragraph exists to prevent.
//
// MIGRATION: The page list is returned as a read-only sequence and is NOT paged.
// Pages form a navigation tree consumed as a lookup, so a partial answer is the
// wrong answer: a parent-page picker built from page one of a paged response
// silently omits candidates, and a tree flattened into rows cannot be split across
// pages without severing parents from their children. The per-row parent, level,
// order and has-children fields are only coherent over the complete set, so the
// endpoint carries no paging query argument and the page transfer types declare no
// paging request, in contrast to portals and users.

using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for reading and updating pages - the DotNetNuke
/// "tab" abstraction - within a portal.
/// </summary>
/// <remarks>
/// <para>
/// <b>This surface is deliberately narrow, and must stay that way.</b> It declares
/// exactly three members - list the pages of one portal, read one page, update one
/// page - and settles no permission question: page permissions belong to the
/// permission service, and "may the caller do this?" is adjudicated by the
/// authorisation policy in the API layer and the permission evaluator in the
/// infrastructure layer. It creates, deletes, copies and restores nothing, exposes
/// no bare count and no path lookup, propagates no skin or container design, and
/// serialises no portal template. The file header records which of the legacy page
/// controller's 34 public members went where and why; read it before proposing an
/// addition, because every omission is a decision rather than a gap.
/// </para>
/// <para>
/// <b>Absence is nullable, never a magic number.</b> No member accepts or returns a
/// sentinel integer standing for "missing". This matters more for pages than
/// anywhere else in the migration: a root-level page genuinely has no parent, while
/// <c>0</c> is a real persisted page identifier because <c>Tabs.TabID</c> is
/// declared <c>IDENTITY(0, 1)</c>, and <c>-1</c> is a real portal identifier as well
/// as the legacy <c>Null.NullInteger</c> marker because <c>Portals.PortalID</c> is
/// declared <c>IDENTITY(-1, 1)</c>. Test a nullable value with <c>is null</c>; never
/// compare an identifier against <c>0</c>, <c>-1</c>, <c>default</c> or
/// <c>int.MinValue</c> to decide whether it is present.
/// </para>
/// <para>
/// <b>Expected failure is returned, never thrown.</b> Every member reports outcomes
/// through <see cref="Result{T}"/>: an expected, caller-handleable failure - a
/// missing page, a parent that would create a cycle - arrives as a failed result
/// carrying a stable reason code, while genuinely unexpected conditions surface as
/// exceptions and are translated once, at the API edge, into a problem-details
/// response. The reason codes named on each member are part of this contract because
/// the API layer maps them onto HTTP status codes; renaming one silently is a
/// breaking change.
/// </para>
/// <para>
/// <b>Asynchronous, scoped, and free of out-parameters.</b> Every member performs
/// input and output, so every member returns a task, carries the <c>Async</c>
/// suffix and takes a cancellation token as its final argument; the legacy
/// mutate-and-report-status idiom is replaced entirely by the returned result.
/// <c>AddApplication()</c> registers this abstraction with a scoped lifetime, and
/// the implementation (<c>Services/TabService.cs</c>) reaches persistence only
/// through the domain layer's page repository abstraction and commits through the
/// unit of work, never seeing a database context. All tree recomputation lives
/// inside it and is never delegated to a caller.
/// </para>
/// </remarks>
public interface ITabService
{
    /// <summary>
    /// Lists every page belonging to one portal, as a flat sequence ordered for
    /// hierarchical display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serves <c>GET /api/v1/portals/{id}/tabs</c>. This single member replaces six
    /// legacy read members: <c>GetTabs</c> (L516), the two <c>GetAllTabs</c>
    /// overloads (L459, L463), the two <c>GetTabsByParentId</c> overloads (L524,
    /// L1282) and <c>GetTabsByPortal</c> (L528). The first five returned the
    /// framework's untyped, non-generic list type and the sixth returned a keyed map
    /// of legacy entities; both shapes are replaced by one read-only, strongly typed
    /// sequence of transfer objects.
    /// </para>
    /// <para>
    /// <b>The answer is complete and unpaged, by design.</b> Each row carries its own
    /// parent, level, order and has-children fields, which is what makes the flat
    /// shape sufficient: the whole navigation tree is reconstructed client-side from
    /// one response, where the legacy per-parent query needed one round trip per
    /// branch.
    /// </para>
    /// <para>
    /// <b>Why there is no parent filter.</b> A nullable parent argument is ambiguous:
    /// <c>null</c> cannot distinguish "apply no filter" from "return only root pages,
    /// whose parent is null", and disambiguating it would need either a forbidden
    /// magic number or an extra argument widening a deliberately narrow surface.
    /// Because every row already carries its parent, filtering by parent is a trivial
    /// client-side projection over a complete answer.
    /// </para>
    /// <para>
    /// <b>Pages in the recycle bin are included, and no filter suppresses them.</b>
    /// "Every page" is literal, and it matches the terminal legacy read this member
    /// replaces: <c>GetTabs</c> as rewritten at <c>04.04.00.SqlDataProvider</c>
    /// L440-L448 selects every column of <c>vw_Tabs</c> under a portal predicate
    /// alone, over a view whose terminal definition carries no deletion predicate
    /// either, so it returns soft-deleted rows and projects <c>IsDeleted</c> for the
    /// reader to act on. The legacy readers did act differently - the page-management
    /// grid hid recycled pages while the recycle-bin screen listed nothing else - so
    /// suppression was always call-site policy over one complete read. Hard-coding
    /// either policy here would contradict the completeness this member promises and
    /// would put recycled pages out of reach of the only page listing this migration
    /// exposes, silently: a caller could not distinguish a portal with no recycled
    /// pages from one whose recycled pages were removed on its behalf. No boolean
    /// argument is added either, for the same reason a parent filter is declined.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// Identifier of the portal whose pages are requested, taken from the route.
    /// Every value is meaningful: <c>Portals.PortalID</c> is declared
    /// <c>IDENTITY(-1, 1)</c> and the shipped default portal row is inserted
    /// explicitly with <c>PortalID</c> <c>0</c>, so both <c>-1</c> and <c>0</c> are
    /// real portal identifiers and neither may be treated as "unspecified".
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// A task producing a successful result whose value is the portal's pages -
    /// possibly an empty sequence, never <see langword="null"/> - or a failed
    /// result carrying the reason code <c>tab.portal_not_found</c> when no portal
    /// bears <paramref name="portalId"/>, which the API layer maps to
    /// <c>404 Not Found</c>. A portal that exists but has no pages is deliberately a
    /// success carrying an empty sequence, never this failure.
    /// </returns>
    Task<Result<IReadOnlyList<TabListItemDto>>> GetTabsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page by identifier, returning a successful result whose value is
    /// <see langword="null"/> when no such page exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serves <c>GET /api/v1/tabs/{id}</c>. This member replaces four legacy read
    /// members: the two <c>GetTab</c> overloads (L467, L1270) and the two
    /// <c>GetTabByName</c> overloads (L504, L508). Name-scoped retrieval is not
    /// carried forward, because the endpoint is identity-based and the name-scoped
    /// overloads existed to support the excluded create path.
    /// </para>
    /// <para>
    /// <b>Absent is a success, not a failure.</b> A page that does not exist yields
    /// <see cref="Result{T}.Success(T)"/> carrying a <see langword="null"/> value.
    /// That distinction is load-bearing rather than stylistic: "the lookup ran and
    /// found nothing" and "the lookup could not run" are different answers, and the
    /// nullable type argument states the first possibility at every call site. The
    /// API layer still renders a null value as <c>404 Not Found</c>; the difference
    /// is that no failure reason is fabricated to describe an ordinary miss.
    /// </para>
    /// <para>
    /// <b>Neither a cache argument nor a portal argument, and neither omission is an
    /// ambient-context shortcut.</b> The legacy reader at L467 accepted a third
    /// argument that bypassed the cache; caching is entirely internal to the
    /// implementation and a caller has no way to steer it. A page identifier is
    /// globally unique, the route <c>GET /api/v1/tabs/{id}</c> is not portal-scoped,
    /// and the returned detail shape carries the owning portal identifier - so the
    /// caller learns the tenant from the response rather than asserting it in the
    /// request, and nothing here is inferred from request-scoped state.
    /// </para>
    /// </remarks>
    /// <param name="tabId">
    /// Identifier of the page to read. <c>0</c> is a valid identifier belonging to a
    /// real page, because <c>Tabs.TabID</c> is declared <c>IDENTITY(0, 1)</c>; it
    /// must never be interpreted as "unspecified" or "not yet saved".
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// A task producing a successful result whose value is the page, or a successful
    /// result whose value is <see langword="null"/> when no page bears
    /// <paramref name="tabId"/>. This member declares <b>no</b> expected failure
    /// reason code, which is deliberate: its only non-happy outcome is absence, and
    /// absence is modelled as success. A failed result from this member therefore
    /// signals an unexpected condition, translated at the API edge.
    /// </returns>
    Task<Result<TabDetailDto?>> GetTabAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one page - its metadata, its visibility and its position in the page
    /// tree - and returns the page as it stands afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serves <c>PUT /api/v1/tabs/{id}</c> and answers <c>200 OK</c> with the updated
    /// page on success. This is the single member into which FOUR legacy update
    /// members collapse: <c>UpdateTab</c> (L780), <c>UpdateTabOrder</c> (L816), the
    /// five-argument <c>UpdateTabOrder</c> (L1287) and the seven-argument
    /// <c>UpdatePortalTabOrder</c> (L550).
    /// </para>
    /// <para>
    /// <b>Ordering travels as request properties, not as positional arguments.</b>
    /// Parent, level, order and visibility - which the legacy seven-argument
    /// signature carried positionally, ending in an optional Boolean the caller could
    /// not see - are all named properties on <paramref name="request"/>, including
    /// that implicit tail. Ordering is always stated explicitly.
    /// </para>
    /// <para>
    /// <b>Tree recomputation belongs to the implementation.</b> Sibling renumbering,
    /// level recalculation and cycle rejection all happen inside the implementing
    /// service; no part of that computation is handed to a caller. This member never
    /// returns a page sequence for a controller to renumber and never accepts
    /// pre-computed sibling orders.
    /// </para>
    /// <para>
    /// <b>A documented divergence in cycle handling.</b> The legacy page-management
    /// screen guarded reparenting with a self-parent test and a recursive ancestry
    /// walk, and when either tripped it simply skipped the update and displayed
    /// nothing - a silent no-op the user could mistake for success. That is a
    /// discovered legacy defect. It is preserved in intent but not in expression: the
    /// same two conditions are still rejected, and are now reported explicitly as the
    /// failure reason code named below, because an API that silently ignores a
    /// mutation is indefensible.
    /// </para>
    /// <para>
    /// <b>A missing target is a failure here.</b> Unlike the read member above, which
    /// treats absence as a success carrying <see langword="null"/>, updating a page
    /// that does not exist is an expected <em>failure</em>. The asymmetry is
    /// intentional: a read legitimately finds nothing, whereas a mutation names a
    /// resource it expects to act upon.
    /// </para>
    /// </remarks>
    /// <param name="tabId">
    /// Identifier of the page to update, taken from the route, which is the
    /// authoritative target. <c>0</c> is a legitimate identifier and must not be read
    /// as "unspecified".
    /// </param>
    /// <param name="request">
    /// The new state to apply, including the page's position in the tree. A parent
    /// value of <see langword="null"/> means the page becomes a root-level page; the
    /// legacy integer sentinel and <c>0</c> are both invalid ways to express that,
    /// because each is a real identifier in this schema.
    /// </param>
    /// <param name="cancellationToken">Token observed while the update is in flight.</param>
    /// <returns>
    /// A task producing a successful result carrying the page as it stands after the
    /// update, or a failed result carrying one of these reason codes:
    /// <c>tab.not_found</c> when no page bears <paramref name="tabId"/>;
    /// <c>tab.parent_not_found</c> when the requested parent does not exist;
    /// <c>tab.parent_cross_portal</c> when the requested parent belongs to a
    /// different portal, which preserves tenant isolation now that the identifier
    /// arrives in a request body rather than from a portal-filtered picker;
    /// <c>tab.parent_cycle</c> when the requested parent is the page itself or one of
    /// its own descendants; and <c>tab.name_reserved</c> when the page name is a
    /// reserved device name, reproducing the legacy screen's own rejection of names
    /// such as <c>CON</c>, <c>NUL</c>, <c>AUX</c>, <c>COM1</c> and <c>LPT1</c>.
    /// Shape-level violations - a missing or overlong name, an end date preceding a
    /// start date - are reported instead as validation problems by the request
    /// validator, not as reason codes here.
    /// </returns>
    Task<Result<TabDetailDto>> UpdateTabAsync(int tabId, UpdateTabRequest request, CancellationToken cancellationToken = default);
}
