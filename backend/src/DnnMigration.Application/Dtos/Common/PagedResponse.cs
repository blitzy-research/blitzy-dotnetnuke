using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Dtos.Common;

// MIGRATION: this envelope retires the legacy `ByRef totalRecords As Integer` status argument, which
// handed a grand total back to its caller through the argument list beside an untyped return value.
// Direct measurement of Library/Components/Users/UserController.vb found EIGHT such overloads - L725
// and L746 (GetUsers), L769 and L793 (GetUsersByEmail), L816 and L840 (GetUsersByUserName), L864 and
// L889 (GetUsersByProfileProperty) - where the Agent Action Plan section 0.7.4 records three. The
// larger figure is reported here as a refinement of the plan rather than as a correction to it: the
// directive the plan states is unchanged, and nothing in this file was decided on the strength of the
// count. A status argument is in any case not expressible over HTTP, so the total has to travel inside
// the response body or not at all.
//
// MIGRATION: it equally supersedes the untyped, pre-generics System.Collections returns that the
// legacy listing members produced - `GetPortals` at PortalController.vb L1263, whose whole body is
// `Return FillPortalInfoCollection(DataProvider.Instance().GetPortals())`, and `GetUsers` at
// UserController.vb L685 - neither of which declared an element type, a total, or an envelope of any
// kind. A caller could not learn from either what it was holding or how much more there was.
//
// MIGRATION: page indexing is ZERO-BASED. That base mirrors PagedResult<T> in the domain layer, which
// documents it as a fixed part of its contract, and agrees with PagedRequest beside this file, so the
// request, the domain envelope and this reply all count from the same place. The legacy stack was split
// on the question and never wrote either base down: the administration screen counted from one
// (Website/admin/Users/Users.ascx.vb L51 seeds its page counter to 1) and subtracted one on every call
// down to the provider (L265, L269, L271 and L274). The shipped schema settles it independently,
// because the paging procedures set a lower bound to the page size multiplied by the page index
// (Website/Providers/DataProviders/SqlDataProvider/03.01.01.SqlDataProvider L38), so index zero
// addresses the first row. The data layer's base is therefore the one carried here, and the one-based
// counter survives only inside the client pagination component. A disagreement between the request and
// this reply would neither fail to compile nor fail a test asserting a successful response code; it
// would quietly serve the neighbouring page, which is precisely why the base is restated on the
// member itself instead of being left for a reader to infer.
//
// MIGRATION: the legacy "return everything, unpaged" call shape is deliberately NOT reproduced. It was
// expressed by handing the integer null sentinel of minus one to the page index, the page size and the
// total alike - UserController.vb L687 and L706 both read `GetUsers(portalId, False, ...)` with that
// sentinel repeated three times - where the sentinel is the value defined at
// Library/Components/Shared/Null.vb L41 and reported as absent by that module's own absence test. No
// negative coordinate is accepted or emitted anywhere on this type: the unpaged case is projected onto
// real, non-negative coordinates by `From`, and the two `Empty` factories reject a negative argument
// outright, so no consumer of this contract ever has to recognise a sentinel.
//
// MIGRATION: pairing the records with their grand total on a single value removes a second database
// round trip. The legacy membership surface could not answer "which page, and how many altogether?" in
// one call, so it shipped an independent count member - `GetUserCountByPortal` at
// Library/Providers/MembershipProviders/DataProvider/DataProvider.vb L82 - beside paged readers at L76,
// L83, L84 and L86 that each returned a forward-only reader carrying no total whatsoever. Both facts
// now arrive together, which is also what lets a client decide pager visibility the way the legacy
// screen did at Website/admin/Users/Users.ascx.vb L279, by comparing page size against the total.
//
// MIGRATION: the total is `int`, not `long`, because every legacy declaration of it is `Integer` - the
// eight status arguments named above and `GetUserCountByPortal` alike. Widening it would be an
// opportunistic change to a measured contract, which the minimal-change directive forbids.
//
// MIGRATION: the payload is carried through untouched. No member rewrites an item, no serialisation
// attribute is applied to any member, and no conditional-omission policy is declared, because the
// legacy null contract represents an absent string as the empty string - Null.vb L71-L75 returns `""`
// literally, not a null reference - and an absent integer as minus one. Either value quietly turned
// into null would change what an existing consumer reads, and an omission policy keyed on default
// values would additionally erase a legitimate total of zero and a legitimately empty page. Minus one
// is doubly load-bearing: `Portals.PortalID` seeds its identity there while `Roles.RoleID`,
// `Tabs.TabID` and `Modules.ModuleID` seed at zero
// (Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider L77, L115, L140 and L221).
// Accordingly no value on this envelope is ever tested for absence by comparing it against a number.
//
// MIGRATION: the split between PagedResult<T> and this type is deliberate and is not duplication.
// PagedResult<T> is the domain envelope an application service returns; this is the boundary contract a
// controller serialises. Keeping them apart is what stops a domain type from becoming a published JSON
// contract, so the domain paging type can change without breaking a client, and the wire metadata can
// be shaped for the wire - here as the shared `ApiMeta` companion - without the domain having to know
// that a wire exists. The two are never bridged by a conversion operator: projection is an explicit,
// visible step through `From`, so a reader can always see where a domain value became a published one.
//
// MIGRATION: paging is expressed as an offset - a page index and a page size - because that is the
// measured legacy contract, whose procedures compute a row lower bound by multiplication and whose
// screens rendered a numbered pager. No opaque continuation value or navigation hyperlink is carried,
// since either would be an unrequested change to how a client addresses a page.

/// <summary>
/// The wire shape of one page of results: the rows themselves, plus the paging metadata describing
/// where that page sits within the whole match set.
/// </summary>
/// <typeparam name="T">
/// The row contract carried on the page. Always a data transfer object from <c>Application/Dtos/</c>,
/// and never a persisted domain entity - keeping entities off the wire is what allows the legacy
/// sentinel semantics noted above to be honoured at the API edge without contaminating the model
/// behind it. The parameter is deliberately unconstrained, so this envelope can never require, and can
/// never accidentally accept, a type drawn from the domain's entity hierarchy.
/// </typeparam>
/// <remarks>
/// <para>
/// This is the wire shape of every paged endpoint: <c>{ "items": [...], "meta": {...} }</c>. The API
/// edge applies it in one place - the shared paging result helper under <c>Api/ErrorHandling/</c>
/// projects the Application layer's <see cref="PagedResult{T}"/> through
/// <see cref="From(PagedResult{T})"/> - so the domain paging type never crosses the boundary, there is
/// one deserialisation path per client rather than one per resource, and a pager is renderable without
/// a second request. The two members are named plainly so they mirror the client model member for
/// member. A page is never additionally wrapped in <c>ApiResponse&lt;T&gt;</c>: that would nest two
/// envelopes and put the row array one level deeper than the contract states.
/// </para>
/// <para>
/// <b>Page indexing is zero-based.</b> A page index of 0 identifies the first page, 1 the second, and
/// so on. This is a contract rather than an implementation note: it mirrors the zero-based index that
/// <see cref="PagedResult{T}"/> reports in the domain layer and agrees with the zero-based index that
/// <see cref="PagedRequest"/> accepts from a caller, so a client may echo the index it sent into the
/// index it reads back without arithmetic. The coordinates themselves live on <see cref="Meta"/>.
/// </para>
/// <para>
/// Prefer <see cref="From(PagedResult{T})"/> to assembling an instance by hand. Projecting a domain
/// envelope keeps the total, the page index and the page size consistent with the query that actually
/// produced them, whereas hand-assembly invites a pager that describes a page nobody read.
/// </para>
/// <para>
/// The type is an inert data carrier. It holds no behaviour beyond projection and argument validation:
/// no clamping, no normalisation, no data-store access and no member that could read one. Composing an
/// offset-and-limit query is data-access work done behind the repository interfaces; request validation
/// belongs to <c>Application/Validation/</c>; translating a persisted record into a row contract
/// belongs to <c>Application/Mapping/</c>; and serialiser configuration, including the naming policy
/// applied to these member names, belongs to the API layer.
/// </para>
/// <para>
/// It describes success and nothing else. An expected failure is carried inside the application by the
/// domain result types, and an unexpected one is shaped into an RFC 7807 problem document at the API
/// edge, so there is no outcome flag, no failure member, no per-field validation map and no
/// transport-level code here. A body claiming failure alongside a successful response code is the exact
/// ambiguity that a standard problem document exists to remove.
/// </para>
/// <para>
/// Both members are initialise-only, so an envelope cannot be repointed once built, and both carry a
/// default so that a caller and a deserialiser alike always observe a usable instance rather than a
/// null reference. That is SHALLOW immutability and it is not thread safety: <see cref="Meta"/> points
/// at an <see cref="ApiMeta"/> whose own three members are settable, so a shared envelope can still
/// have its coordinates changed underneath a reader. The rows are the exception - an envelope produced
/// by <see cref="From(PagedResult{T})"/> takes them from the domain envelope's already-immutable
/// snapshot - so treat the metadata, not the rows, as the mutable part, and either keep an instance to
/// one thread or never touch its metadata after construction.
/// </para>
/// </remarks>
public sealed class PagedResponse<T>
{
    /// <summary>
    /// Gets the rows on this page, in the order the query produced them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never null, and never absent from a serialised body: an empty page is a legitimate answer, not
    /// an error, and a client distinguishes "past the end of the set" from "nothing matched at all" by
    /// consulting <see cref="ApiMeta.TotalCount"/> rather than by finding this member missing.
    /// </para>
    /// <para>
    /// Exposed as a read-only list, and deliberately not as a deferred sequence. A deferred sequence
    /// would hide the very count this envelope exists to publish, could be walked twice and answer
    /// differently each time, and could keep a live database connection open past the repository that
    /// opened it. A mutable collection is equally unsuitable on a response, because it would let a
    /// caller alter an envelope that has already been described as final.
    /// </para>
    /// <para>
    /// The default is an empty list rather than null, which is also what makes this member safe to
    /// materialise from a response body: a serialiser that encounters no rows leaves a usable empty
    /// list in place.
    /// </para>
    /// </remarks>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// Gets the paging metadata locating this page within the whole match set: the total number of
    /// records that satisfy the criteria, the zero-based page index, the page size applied, and the
    /// page count derived from them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The paging facts live here and only here. They are not restated as members of this envelope,
    /// because two copies of one fact on a single response give a pager two sources of truth and no way
    /// to choose between them when they disagree. Reusing the shared companion also keeps a collection
    /// response describing its page exactly as every other response in the contract does.
    /// </para>
    /// <para>
    /// Note that the total on <see cref="ApiMeta.TotalCount"/> is "the total no of records that satisfy
    /// the criteria" across <b>all</b> pages - the wording is the legacy contract's own, repeated on
    /// every paged read it declared. It is not the number of entries in <see cref="Items"/>, and the
    /// two coincide only when the whole match set fits on the page in hand.
    /// </para>
    /// </remarks>
    public ApiMeta Meta { get; init; } = new();

    /// <summary>
    /// Projects a domain paging envelope onto the wire, carrying the same rows and the same paging
    /// facts.
    /// </summary>
    /// <param name="result">The domain envelope an application service produced.</param>
    /// <returns>A wire envelope reporting the rows and coordinates of <paramref name="result"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="result"/> is <see langword="null"/>. A missing envelope is a programming defect
    /// at the call site rather than an expected outcome, so it is signalled by the framework exception
    /// the domain envelope's own factories use, and never as a domain failure or a failed result value.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The rows are taken directly rather than copied. That is safe precisely because
    /// <see cref="PagedResult{T}"/> guarantees it: its factories snapshot whatever they are given and
    /// publish the snapshot through a genuinely read-only view, so there is no collection a caller
    /// could still be holding and still change. Copying again here would allocate a second array per
    /// response and buy nothing.
    /// </para>
    /// <para>
    /// The page index is taken directly too, without an unpaged special case, because the domain type
    /// already guarantees one: its paged factory rejects a non-zero page index whenever the page size
    /// is zero, and its unpaged factory supplies zero for both. Re-testing that invariant here would
    /// suggest it might not hold.
    /// </para>
    /// <para>
    /// An unpaged envelope - one produced by a query that returned every match rather than a window -
    /// is the one place a value is reshaped, and the reshaping is confined to the page size. The domain
    /// type signals "unpaged" with a page size of zero, which is meaningful inside the application
    /// because a named property reports it, but on the wire a page size of zero would drive the derived
    /// page count to zero for a response that plainly contains records. Reporting the page size as the
    /// total instead describes the truthful shape of what was sent - one page holding everything - and
    /// keeps every derived value self-consistent for a client that divides. An unpaged, zero-record
    /// answer still reports zero for both, which is correct.
    /// </para>
    /// </remarks>
    public static PagedResponse<T> From(PagedResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PagedResponse<T>
        {
            Items = result.Items,
            Meta = new ApiMeta
            {
                TotalCount = result.TotalCount,
                PageIndex = result.PageIndex,
                PageSize = result.IsUnpaged ? result.TotalCount : result.PageSize,
            },
        };
    }

    /// <summary>
    /// Creates a zero-record envelope with no paging coordinates, for a collection endpoint that
    /// legitimately matched nothing and applied no window.
    /// </summary>
    /// <returns>An envelope carrying no rows, a total of zero and zero coordinates.</returns>
    /// <remarks>
    /// A method returning a fresh envelope each time, rather than a cached shared instance. The
    /// distinction is deliberate and is not symmetry for its own sake: <see cref="ApiMeta"/> is
    /// mutable, so a single shared envelope could be altered through <see cref="Meta"/> by one caller
    /// and then observed in that altered state by every later one. <see cref="PagedResult{T}"/> can
    /// safely expose a cached empty value because it is immutable throughout; this type cannot, and
    /// allocating one small envelope per empty response is the correct price for that.
    /// </remarks>
    public static PagedResponse<T> Empty() => new();

    /// <summary>
    /// Creates a zero-record envelope that still reports the page that was asked for, so a client can
    /// render an accurate pager for a requested page that happened to match nothing.
    /// </summary>
    /// <param name="pageIndex">
    /// The zero-based page of records the caller asked for. 0 is the first page.
    /// </param>
    /// <param name="pageSize">The size of the page the caller asked for.</param>
    /// <returns>
    /// An envelope carrying no rows and a total of zero, at the supplied coordinates.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageIndex"/> or <paramref name="pageSize"/> is negative. A negative coordinate
    /// is rejected rather than interpreted, so the legacy integer null sentinel cannot arrive disguised
    /// as a page address and be published as one.
    /// </exception>
    /// <remarks>
    /// Preferred over <see cref="Empty()"/> whenever a window was genuinely applied: an endpoint that
    /// discarded the requested coordinates would leave a client unable to show which page it is looking
    /// at. The rows are left at their empty default, which is the entire point of the factory.
    /// </remarks>
    public static PagedResponse<T> Empty(int pageIndex, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(pageSize);

        return new PagedResponse<T>
        {
            Meta = new ApiMeta
            {
                TotalCount = 0,
                PageIndex = pageIndex,
                PageSize = pageSize,
            },
        };
    }
}
