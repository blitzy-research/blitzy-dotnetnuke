namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// Envelope metadata companion to <c>ApiResponse</c>, describing a successful API
/// response rather than the payload it carries.
/// </summary>
/// <remarks>
/// <para>
/// A collection endpoint populates every member; a single-item endpoint omits the
/// metadata altogether, because a scalar payload has no page to describe.
/// </para>
/// <para>
/// The member set is deliberately confined to the three facts the legacy pager
/// consumed, plus the one value derived from them. The Web Forms user
/// administration screen handed its pager exactly three values, total records,
/// page size and current page, at <c>Website/admin/Users/Users.ascx.vb</c> lines
/// 285 to 287, and the Angular pagination component that replaces it takes the
/// same three as inputs. Nothing beyond that trio is metadata a collection
/// response owes its consumer, so nothing beyond it appears here.
/// </para>
/// <para>
/// This type describes success only. Failure never travels in this envelope:
/// expected failures are carried by the domain result types, and unexpected ones
/// are shaped into an RFC 7807 problem document at the API edge. Hence there is
/// no outcome flag, no message, no per-field validation map and no
/// transport-level code anywhere below.
/// </para>
/// <para>
/// Instances are plain mutable data with no behaviour beyond the derived page
/// count, so they are neither thread-safe nor intended to be shared. The API
/// layer builds one per response, serialises it and discards it.
/// </para>
/// </remarks>
// MIGRATION: net-new type with no legacy predecessor. DotNetNuke 4.9.0 had no
// response envelope at all; every page rendered its own markup, so response
// metadata was assigned straight onto a Web Forms pager control
// (Website/admin/Users/Users.ascx.vb lines 285 to 287) and never crossed a
// serialisation boundary. Introducing an envelope follows from replacing
// server-rendered pages with a JSON API and changes no business rule.
//
// MIGRATION: no correlation identifier member is present, and the omission is
// deliberate. The request correlation value travels in the X-Correlation-Id HTTP
// header, written by the API-layer correlation middleware and read by the
// matching Angular HTTP interceptor. Repeating it in the response body would
// create a second source of truth that no client consults.
public sealed class ApiMeta
{
    /// <summary>
    /// Gets or sets the total number of records that satisfy the criteria,
    /// counted across every page and not only the page returned.
    /// </summary>
    /// <remarks>
    /// The wording is the legacy contract's own, which documented this value as
    /// "The total no of records that satisfy the criteria." on every one of its
    /// paged reads.
    /// </remarks>
    // MIGRATION: this member retires the ByRef totalRecords out-parameter. A
    // direct reading of Library/Components/Users/UserController.vb found EIGHT
    // such overloads, at lines 725, 746, 769, 793, 816, 840, 864 and 889, being
    // GetUsers, GetUsersByEmail, GetUsersByUserName and GetUsersByProfileProperty
    // twice each, where the action plan records three. The larger figure is a
    // refinement of the count, reported rather than silently corrected; the
    // directive it supports is unchanged. All eight returned an untyped,
    // pre-generics collection with the total handed back through the argument
    // list.
    //
    // MIGRATION: the total is int and not long. Both legacy declarations are
    // Integer: the ByRef totalRecords argument at all eight sites above, and
    // GetUserCountByPortal at
    // Library/Providers/MembershipProviders/DataProvider/DataProvider.vb line 82.
    // The legacy contract type wins, so the width is preserved exactly instead of
    // being widened for no measured reason.
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the page of records returned, counted from zero.
    /// </summary>
    /// <remarks>
    /// The legacy contract documented the corresponding argument as "The page of
    /// records to return." and treated it as zero-based, which is the convention
    /// carried here.
    /// </remarks>
    // MIGRATION: the zero-based convention is the legacy DATA layer's, not the
    // legacy screen's. Website/admin/Users/Users.ascx.vb initialised its own page
    // counter to one at line 51 and subtracted one before calling down, at line
    // 265. This envelope carries the data-layer convention so that the wire
    // contract and the repository agree; the one-based presentation counter now
    // exists only inside the Angular pagination component.
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the size of the page that produced the payload.
    /// </summary>
    /// <remarks>
    /// The size the server actually applied, which can differ from the size a
    /// caller asked for once request validation has clamped it. The legacy screen
    /// read its page size from the Records_PerPage portal setting and echoed that
    /// same value back to the pager, which is the behaviour this member preserves.
    /// The legacy contract documented the argument as "The size of the page".
    /// </remarks>
    public int PageSize { get; set; }

    /// <summary>
    /// Gets the number of pages that <see cref="TotalCount"/> divides into at the
    /// current <see cref="PageSize"/>, or zero when there is nothing to page.
    /// </summary>
    /// <value>
    /// A ceiling division of <see cref="TotalCount"/> by <see cref="PageSize"/>.
    /// Both guards are arithmetic rather than absence tests: a page size of zero
    /// would divide by zero, and a page count is a cardinality that can never be
    /// negative. No identifier is involved, so no value here is ever read as
    /// missing.
    /// </value>
    /// <remarks>
    /// <para>
    /// Derived, and therefore deliberately without a setter: the value follows
    /// from <see cref="TotalCount"/> and <see cref="PageSize"/> and must not be
    /// able to contradict them. A serialiser emits it alongside them and
    /// recomputes it when reading the envelope back, so the round trip is
    /// lossless.
    /// </para>
    /// <para>
    /// The division is expressed as a quotient plus a remainder test rather than
    /// by adding the page size to the total first, because the latter overflows as
    /// the total approaches <c>int.MaxValue</c>. Reading this property costs one
    /// divide and one modulo over values already held and never reaches a store.
    /// </para>
    /// </remarks>
    // MIGRATION: computed here, and never computed at all in the legacy code. The
    // administration screen declared a TotalPages field at
    // Website/admin/Users/Users.ascx.vb line 58, seeded it with the legacy integer
    // null sentinel of minus one, then neither assigned nor read it anywhere else
    // in the file. This member replaces that dead field with a deterministic
    // derivation and never emits the sentinel: the legacy null contract in
    // Library/Components/Shared/Null.vb treats minus one as absent for integers,
    // which a genuine page count must never be confused with.
    public int TotalPages => PageSize > 0 && TotalCount > 0
        ? (TotalCount / PageSize) + ((TotalCount % PageSize) > 0 ? 1 : 0)
        : 0;
}
