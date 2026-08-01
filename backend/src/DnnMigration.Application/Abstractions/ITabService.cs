// MIGRATION: This contract is DELIBERATELY NARROW, and that is its single most
// important property. The legacy page controller,
// Library/Components/Tabs/TabController.vb, is 1,302 lines and exposes 34 public
// members, measured directly. Exactly three of those concerns survive here.
// The migration plan fixes the API surface for pages as "deliberately narrow:
// GET /api/v1/portals/{id}/tabs and GET/PUT /api/v1/tabs/{id} only", and three
// independent facts corroborate that the narrowness is intentional rather than an
// omission: the planned data-transfer folder for pages holds exactly three types
// and contains no create request; the planned client application declares feature
// folders for portals, modules, users, roles and authentication but NONE for
// pages; and the twenty-five-route client route table contains no page route at
// all. Pages exist in this migration only because module placement, page
// permissions and portal navigation are inseparable from them, and they are
// consumed as a LOOKUP by the module screens.
//
// MIGRATION: Do not "complete" this surface. Adding a create, delete, copy,
// recycle-bin, restore, serialise, deserialise, count, path-lookup or
// design-propagation member would contradict the plan, not extend it. "The legacy
// controller had it" is explicitly NOT a justification: the 31 members omitted
// below were each considered and each rejected for a reason recorded here. A new
// member requires a named endpoint in the migration plan first.
//
// MIGRATION: FOUR legacy update members collapse into the single update member
// declared below. They are UpdateTab at L780; UpdateTabOrder at L816;
// UpdateTabOrder at L1287, a five-argument positional variant; and
// UpdatePortalTabOrder at L550, a SEVEN-argument positional signature whose tail
// argument is an optional Boolean defaulted to False. A seven-argument positional
// signature is itself the defect, so the replacement is not a defaulted argument
// and not a set of overloads: the ordering inputs the legacy signature carried
// positionally - parent, level, order and visibility - become named properties on
// a single request object, and the legacy optional tail argument becomes an
// explicit property on that same request rather than an implicit default a caller
// cannot see. Ordering is therefore always stated, never inferred.
//
// MIGRATION: The migration plan cites two optional-argument conversion sites for
// the legacy page controller, at L243 and L550. Direct measurement refines that:
// only L550 is public. The member at L243, MoveTab, is declared Private and is
// invoked solely from inside the body of UpdatePortalTabOrder, at L636, L708 and
// L746. It therefore never appeared on any public contract and cannot be a
// contract-level conversion. This is REPORTED as a refinement, not corrected: the
// plan's binding directive is unchanged, and no public move or reorder member is
// invented here to honour the citation. The tree-reordering behaviour that member
// implements - sibling renumbering, level recalculation and cycle rejection - is
// preserved INSIDE the implementing service, which is where it belongs.
//
// MIGRATION: NINE legacy members performed portal-template XML serialisation and
// are not ported: DeserializePanes at L984; five DeserializeTab overloads at
// L1009, L1013, L1017, L1021 and L1025; and three SerializeTab overloads at
// L1141, L1153 and L1166. They traded in the framework XML node and document
// types, the legacy hash table type and legacy entity types. Portal-template
// handling is scoped by the migration plan to the portal service and only as far
// as template parsing requires, so no XML type appears on this contract in either
// direction. That is also why this file imports no XML namespace.
//
// MIGRATION: CopyDesignToChildren at L375 is not ported. It propagated a skin
// source and a container source down a page subtree, and the migration plan
// excludes DotNetNuke skinning and containers entirely, together with every
// container and skin object. There is no target concept for it to serve.
//
// MIGRATION: CopyPermissionsToChildren at L387 is not ported HERE. Page
// permissions are the permission service's responsibility, and the legacy
// permission collection wrapper it accepted produces no target type at all,
// being superseded by read-only generic collections. Splitting permissions away
// from page metadata is deliberate: it keeps this contract free of any
// access-control decision.
//
// MIGRATION: AddTab at L326 and L330, DeleteTab at L446, the static DeleteTab at
// L936 and CopyTab at L414 are not ported. No create, delete or copy endpoint for
// pages exists in the migration plan, and the planned data-transfer folder
// accordingly declares no create request. The static overload at L936 is doubly
// disqualified: it accepted the legacy per-request portal composite as an
// argument, and that ambient composite is replaced by an immutable request-scoped
// tenant context owned by the domain layer. Soft-delete and recycle-bin
// semantics, which the legacy recycle-bin screen provided, likewise have no
// endpoint in the plan.
//
// MIGRATION: The legacy single-page reader at L467 took a third argument that
// bypassed the cache. No equivalent flag crosses this contract. Caching is the
// implementing service's internal concern, expressed through the domain layer's
// cache abstraction; the legacy controller contains 12 measured cache call sites,
// at L60, L63, L370, L382, L409, L456, L529, L532, L536, L543, L1113 and L1128,
// and all 12 are absorbed there. A caller cannot and must not steer caching.
//
// MIGRATION: Two legacy members returned keyed maps used purely as cache
// structures and are not ported: GetTabsByPortal at L528, which returned a map of
// legacy entities keyed by identifier, and the static GetTabPathDictionary at
// L1111, which returned a path-to-identifier map. Neither a keyed map nor an
// entity-valued collection appears on this surface. Every legacy member that
// returned the framework's untyped, non-generic list type - GetAllTabs at L459
// and L463, GetTabs at L516, GetTabsByParentId at L524 and L1282 - is served
// instead by a single read-only, strongly typed sequence.
//
// MIGRATION: Numeric sentinels do NOT cross this contract; absence is carried by
// nullable types. The legacy null contract used minus one as its integer
// sentinel, but that value collides with real data twice over in this schema.
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider
// declares Tabs.TabID as IDENTITY (0, 1) at L140, so ZERO is a legitimate,
// persisted page identifier and the first page ever created carries it; and it
// declares Portals.PortalID as IDENTITY (-1, 1) at L77, so minus one is
// simultaneously the legacy "absent" marker AND the identifier of the first real
// portal. Roles.RoleID at L115 and Modules.ModuleID at L221 also seed at zero.
// Consequently a root-level page's parent is null, never minus one and never
// zero, and no member here accepts or returns a magic number meaning "absent".
// The legacy reordering routine's local markers of minus one and minus two are
// loop bookkeeping inside one method body, never contract values, and are not
// reproduced. An implementer or consumer that treats zero as "unset" reintroduces
// precisely the defect this paragraph exists to prevent.
//
// MIGRATION: The page list is returned as a read-only sequence and is NOT paged.
// Pages form a navigation tree that the module screens consume as a lookup, so a
// partial answer is the wrong answer: a parent-page picker built from page one of
// a paged response silently omits candidates, and a tree flattened into rows
// cannot be split across pages without severing parents from their children. The
// per-row parent, level, order and has-children fields are only coherent over the
// complete set. The migration plan reinforces this by naming the endpoint with no
// paging query argument and by declaring no paging request among the three
// planned page transfer types, in contrast to portals and users, which do get
// paged contracts.
//
// MIGRATION: Three further legacy read members are deliberately absent.
// GetTabCount at L512 is omitted because a bare count member invites a caller to
// make a decision that belongs in the service layer, and because the complete
// sequence returned below already answers it. GetTabByTabPath at L1101 is omitted
// because no endpoint or screen in the migration plan resolves a page by path;
// the path is carried as a field on the list rows instead. GetTabByName at L504
// and L508 is omitted because identity-based retrieval is what the planned
// endpoint exposes, and the name-scoped lookup those overloads provided existed
// to serve the create path, which is itself excluded.

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
/// exactly three members, mirroring the only three page endpoints the migration
/// plan defines: list the pages of one portal, read one page, and update one page.
/// The legacy page controller it replaces
/// (<c>Library/Components/Tabs/TabController.vb</c>) exposes 34 public members, and
/// 31 of them are intentionally not represented here. The header comments in this
/// file record, member by member and line by line, which legacy member went where
/// and why. Read them before proposing an addition: every omission is a decision,
/// not a gap.
/// </para>
/// <para>
/// <b>Why pages are in scope at all.</b> The page aggregate is a <em>supporting</em>
/// aggregate in this migration. It is present because module placement, page
/// permissions and portal navigation are inseparable from it - the permission
/// tables are keyed by page identifier - and because the module administration
/// screens consume pages as a lookup when a module has to be placed. It is not
/// present to provide page administration in its own right, which is why there is
/// no page feature area and no page route in the planned client application.
/// </para>
/// <para>
/// <b>What this contract deliberately does not do.</b> It creates, deletes, copies
/// and restores nothing; it propagates no skin or container design; it serialises
/// and deserialises no portal template; it exposes no bare count and no
/// path-resolution lookup; and it settles no permission question. Page permissions
/// belong to the permission service, and the legacy page-permission collection
/// wrapper has no target type. Nothing here answers "may the caller do this?" -
/// that is an access-control decision, adjudicated by the authorisation policy in
/// the API layer and the permission evaluator in the infrastructure layer.
/// </para>
/// <para>
/// <b>Absence is nullable, never a magic number.</b> No member accepts or returns a
/// sentinel integer standing for "missing". This matters more for pages than
/// anywhere else in the migration, because a root-level page genuinely has no
/// parent while <c>0</c> is simultaneously a real, persisted page identifier:
/// <c>Tabs.TabID</c> is declared <c>IDENTITY(0, 1)</c>. The legacy
/// <c>Null.NullInteger</c> sentinel of <c>-1</c> is equally unusable, because
/// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c> and <c>-1</c> is
/// therefore a real portal identifier as well as the legacy "absent" marker. Never
/// compare an identifier against <c>0</c>, <c>-1</c>, <c>-2</c>, <c>default</c> or
/// <c>int.MinValue</c> to decide whether it is present; test the nullable value
/// with <c>is null</c>.
/// </para>
/// <para>
/// <b>Expected failure is returned, never thrown.</b> Every member reports outcomes
/// through <see cref="Result{T}"/>. An expected, caller-handleable failure - a
/// missing page, a parent that would create a cycle - arrives as a failed result
/// carrying a stable reason code, and each member documents the codes it may
/// produce. Genuinely unexpected conditions are left to surface as exceptions and
/// are translated once, at the API edge, into a problem-details response. The
/// reason codes named on each member are part of this contract precisely because
/// the API layer maps them onto HTTP status codes; renaming one silently is a
/// breaking change.
/// </para>
/// <para>
/// <b>Asynchronous throughout.</b> Every member performs input and output, so every
/// member returns a task, carries the <c>Async</c> suffix, and accepts a
/// cancellation token as its final argument. No member blocks, and no member uses
/// an out-parameter or a ref-parameter - the legacy mutate-and-report-status idiom
/// is replaced entirely by the returned result.
/// </para>
/// <para>
/// <b>Registration and lifetime.</b> The application layer's <c>AddApplication()</c>
/// extension registers this abstraction with a scoped lifetime, as one of exactly
/// seven application services. The implementation is <c>Services/TabService.cs</c>,
/// which reaches persistence only through the domain layer's page repository
/// abstraction and commits through the unit of work; it never sees a database
/// context. All tree-reordering logic - sibling renumbering, level recalculation
/// and cycle rejection - lives inside that implementation and is never delegated to
/// a caller.
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
    /// <b>The answer is complete and unpaged, by design.</b> The sequence carries
    /// every page of the portal, so a caller can build the whole navigation tree
    /// from one response. Each row carries its own parent, level, order and
    /// has-children fields, which is what makes the flat shape sufficient: the tree
    /// is reconstructed client-side without further requests. This is strictly more
    /// capable than the legacy per-parent query it replaces, which required one
    /// round trip per branch.
    /// </para>
    /// <para>
    /// <b>Why there is no parent filter.</b> A nullable parent argument was
    /// considered and rejected as ambiguous: <c>null</c> cannot distinguish "apply
    /// no filter" from "return only root pages, whose parent is null", and
    /// disambiguating it would need either a forbidden magic number or an extra
    /// argument widening a surface that is deliberately narrow. Because every row
    /// already carries its parent, filtering by parent is a trivial client-side
    /// projection over a complete answer.
    /// </para>
    /// <para>
    /// A portal that exists but has no pages is a <em>success</em> carrying an empty
    /// sequence. It is never reported as a failure, and the returned sequence is
    /// never <see langword="null"/>.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// Identifier of the portal whose pages are requested, taken from the route.
    /// Every value is meaningful: <c>0</c> is the first portal ever created and
    /// <c>-1</c> is also a real portal identifier, because
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>. Neither may be
    /// treated as "unspecified".
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// A task producing a successful result whose value is the portal's pages -
    /// possibly an empty sequence, never <see langword="null"/> - or a failed
    /// result carrying the reason code <c>tab.portal_not_found</c> when no portal
    /// bears <paramref name="portalId"/>, which the API layer maps to
    /// <c>404 Not Found</c>. An empty portal is deliberately not this failure.
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
    /// carried forward, because the planned endpoint is identity-based and the
    /// name-scoped overloads existed to support the excluded create path.
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
    /// <b>No cache argument.</b> The legacy reader at L467 accepted a third argument
    /// that bypassed the cache. It is not reproduced. Caching is entirely internal to
    /// the implementation, which absorbs the 12 cache call sites measured in the
    /// legacy controller, and a caller has no way to steer it.
    /// </para>
    /// <para>
    /// <b>No portal argument, and this is not an ambient-context shortcut.</b> A page
    /// identifier is globally unique, the planned route is not portal-scoped, and the
    /// returned detail shape itself carries the owning portal identifier - so the
    /// caller learns the tenant from the response rather than having to assert it in
    /// the request. Nothing here is inferred from request-scoped state.
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
    /// The legacy seven-argument signature carried parent, level, order and
    /// visibility positionally and ended in an optional Boolean defaulted to
    /// <see langword="false"/>, so a caller could not see what it was accepting. All
    /// of those inputs are now named properties on <paramref name="request"/>,
    /// including the one that was the implicit optional tail. Ordering is always
    /// stated explicitly.
    /// </para>
    /// <para>
    /// <b>Tree recomputation belongs to the implementation.</b> Sibling renumbering,
    /// level recalculation and cycle rejection all happen inside the implementing
    /// service. No part of that computation is handed to a caller: this member never
    /// returns a page sequence for a controller to renumber, and it never accepts
    /// pre-computed sibling orders. The legacy private reordering helper embodied
    /// exactly this logic and stays internal.
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
