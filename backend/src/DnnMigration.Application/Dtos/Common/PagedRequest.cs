namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering query contract that every collection endpoint
/// accepts from its caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Page indexing is zero-based.</b> A <see cref="PageIndex"/> of 0 addresses the
/// first page, 1 the second, and so on. That base is not invented here: it is the
/// base documented by <c>PagedResult&lt;T&gt;</c> in the domain layer for the index
/// it reports back, and this type mirrors it exactly. The two halves of the round
/// trip have to agree, because a caller sends this contract and then reads that
/// envelope; a disagreement would quietly serve the neighbouring page instead of
/// failing, so the base is restated on <see cref="PageIndex"/> and neither end has
/// to open the other file to learn it.
/// </para>
/// <para>
/// This is the request half of the pair that retires the legacy
/// <c>ByRef totalRecords</c> status argument; <c>PagedResult&lt;T&gt;</c> and
/// <c>PagedResponse</c> are the response half. It owns the sort field, the sort
/// direction and the free-text filter precisely because the response envelope
/// refuses them: what was asked for is a property of the asking, and echoing it back
/// on the reply would create a second place for it to disagree.
/// </para>
/// <para>
/// It is an inert carrier. It holds no behaviour beyond two derived flags, performs
/// no I/O, reaches no store, and deliberately offers no helper that would turn it
/// into a query. Composing an offset-and-limit read is data-access work, done behind
/// the repository interfaces in the infrastructure layer, and a helper here would
/// drag a persistence dependency into a layer that must not have one.
/// </para>
/// <para>
/// It also enforces nothing. Bounds belong to
/// <c>Application/Validation/PagedRequestValidator</c>, which is why no property
/// below corrects, coerces or clamps what a caller supplied: a validator cannot
/// report a page size of zero as invalid once a setter has silently rewritten it to
/// ten. Every property is a plain settable auto-property so that query-string model
/// binding and JSON serialisation can each populate it, and the binding-source
/// attribute is declared by the controller on its own parameter rather than by this
/// contract, which keeps the application layer free of any web-framework reference.
/// </para>
/// <para>
/// Instances are plain mutable data with no invariants of their own, so they are
/// neither thread-safe nor intended to be shared. One is bound per request, read,
/// and discarded.
/// </para>
/// </remarks>
// MIGRATION: the legacy "return everything, unpaged" call shape is deliberately NOT
// reproduced. It was expressed by handing the integer null sentinel of minus one to
// the page index, the page size and the total alike, at
// Library/Components/Users/UserController.vb lines 687 and 706. That sentinel is the
// minus one returned by Library/Components/Shared/Null.vb line 43, and the same
// file's absence test at line 211 compares an integer against it, so a negative page
// coordinate is indistinguishable from a missing one. It is therefore never
// meaningful on this type: no negative value is accepted, and the unpaged case has
// its own named representation on PagedResult, so no caller needs a sentinel to ask
// for every record.
//
// MIGRATION: the status argument this pair retires was counted directly rather than
// taken on trust. Library/Components/Users/UserController.vb declares EIGHT paged
// overloads carrying a ByRef totalRecords out-parameter, at lines 725, 746, 769,
// 793, 816, 840, 864 and 889, being GetUsers, GetUsersByEmail, GetUsersByUserName
// and GetUsersByProfileProperty twice each, where action plan section 0.7.4 records
// three. The larger figure is reported as a refinement of the count and not as a
// correction to the plan; the directive it supports, that no out-parameter appears
// in any target public API, is unchanged and is honoured here.
//
// MIGRATION: naming the page coordinates on the request, and the records with their
// grand total on the reply, removes a second database round trip. The legacy
// membership surface could not answer "which page, and how many altogether?" in one
// call, so it shipped an independent count member, GetUserCountByPortal at
// Library/Providers/MembershipProviders/DataProvider/DataProvider.vb line 82,
// alongside paged readers at lines 76, 83, 84 and 86 that returned a forward-only
// reader and no total whatsoever.
public sealed class PagedRequest
{
    /// <summary>
    /// Gets or sets the zero-based index of the page of records to return. 0 is the
    /// first page, 1 the second, and so on.
    /// </summary>
    /// <value>
    /// A zero-based page index, defaulting to 0 and therefore to the first page. The
    /// default is the natural one: an omitted query-string parameter leaves this at
    /// the first page, which is what a caller who supplied no page meant.
    /// </value>
    /// <remarks>
    /// <para>
    /// <b>Zero-based, mirroring <c>PagedResult&lt;T&gt;</c>.</b> This is a contract,
    /// not a note. The value travels to the server on this type and comes back on
    /// that envelope, and both count from zero, so a client may echo one into the
    /// other without arithmetic. The legacy wording for the corresponding argument
    /// was "The page of records to return.", which is retained above.
    /// </para>
    /// <para>
    /// A negative index is invalid, including the legacy integer null sentinel of
    /// minus one. It is rejected rather than reinterpreted, so a sentinel cannot
    /// arrive disguised as a page address. Rejection happens in
    /// <c>PagedRequestValidator</c>; this property records what the caller sent.
    /// </para>
    /// </remarks>
    // MIGRATION: the zero-based base is the legacy DATA layer's, not the legacy
    // screen's, and the legacy stack never wrote it down. The administration screen
    // counted from one, seeding its own page counter at
    // Website/admin/Users/Users.ascx.vb line 51, then subtracted one on every call
    // down to the provider, at lines 265, 269, 271 and 274. That subtraction is the
    // proof that the layer beneath counted from zero, and the shipped schema settles
    // it independently: the paging procedures set a lower bound to the page size
    // multiplied by the page index, at
    // Website/Providers/DataProviders/SqlDataProvider/03.01.01.SqlDataProvider line
    // 38, so index zero addresses the first row. The wire contract adopts the data
    // layer's base; the one-based presentation counter now lives only inside the
    // Angular pagination component, which converts in exactly one place.
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the size of the page, meaning the maximum number of records the
    /// caller is willing to receive.
    /// </summary>
    /// <value>
    /// A positive page size, defaulting to 10. Zero and negative sizes are invalid
    /// and are reported by <c>PagedRequestValidator</c> rather than silently replaced
    /// here, so a caller learns that a request was malformed instead of receiving a
    /// page it never asked for.
    /// </value>
    /// <remarks>
    /// The legacy wording for the corresponding argument was "The size of the page",
    /// which is retained above. An upper bound also belongs to the validator: capping
    /// a page size protects the server, but it is a policy decision that has to be
    /// visible as a rejection or an echoed-back size, never as a quiet substitution
    /// inside a setter.
    /// </remarks>
    // MIGRATION: the default of ten is the measured legacy default, not a
    // convention. Library/Components/Users/UserModuleBase.vb lines 134 and 135 seed
    // the Records_PerPage module setting with ten whenever it is unset, and the
    // administration screen read its page size from exactly that setting, at
    // Website/admin/Users/Users.ascx.vb lines 114 to 119. Keeping the number means an
    // unparameterised list request still returns the number of rows it always did.
    public int PageSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the name of the field to sort by, or <see langword="null"/> when
    /// the caller expresses no preference and the server's own ordering applies.
    /// </summary>
    /// <remarks>
    /// A field name, not an expression: the set of sortable names is decided by the
    /// repository that serves the endpoint, and an unrecognised name is a validation
    /// failure rather than something to be passed through to a store. Both
    /// <see langword="null"/> and blank text mean "no preference", as
    /// <see cref="HasSort"/> reports, and neither is rewritten into the other here.
    /// </remarks>
    // MIGRATION: caller-chosen ordering is net-new request state with no legacy
    // predecessor, and the absence was verified rather than assumed: no markup under
    // Website/admin/Users declares a sorting affordance of any kind, so the legacy
    // grids rendered whatever order the stored procedure produced and offered the
    // user no way to change it. Exposing the choice adds a capability without
    // altering any business rule, because the server still decides which names are
    // sortable and how each one orders.
    public string? SortBy { get; set; }

    /// <summary>
    /// Gets or sets the direction in which <see cref="SortBy"/> is applied.
    /// </summary>
    /// <value>
    /// <see cref="SortDirection.Ascending"/> by default, which is also the value a
    /// request that omits the parameter carries. The value is only consulted when
    /// <see cref="HasSort"/> is <see langword="true"/>; it is retained rather than
    /// discarded otherwise, so a caller toggling a direction before choosing a field
    /// does not lose the setting.
    /// </value>
    public SortDirection SortDir { get; set; }

    /// <summary>
    /// Gets or sets the caller's free-text filter, or <see langword="null"/> when no
    /// filter applies.
    /// </summary>
    /// <remarks>
    /// <b>Raw text exactly as the caller typed it.</b> No wildcard, escape or pattern
    /// syntax is added here and none is expected from the caller; turning this text
    /// into a match pattern is the repository's work. Both <see langword="null"/> and
    /// blank text mean "no filter", as <see cref="HasQuery"/> reports, and the
    /// property preserves whichever form arrived.
    /// </remarks>
    // MIGRATION: the legacy prefix match was composed at the call site. The
    // administration screen appended a trailing per-cent wildcard to the search text
    // before calling down, at Website/admin/Users/Users.ascx.vb lines 269, 271 and
    // 274, so the pattern reached the data layer already built by the presentation
    // layer. Pattern composition moves behind the repository interfaces, and this
    // property deliberately does not participate: a request contract that
    // pre-decorated the text would leave the repository unable to distinguish a
    // literal per-cent typed by a user from a wildcard added by a caller, and would
    // decorate the value twice the moment a second caller did the same thing.
    public string? Query { get; set; }

    /// <summary>
    /// Gets a value indicating whether the caller asked for a particular ordering.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="SortBy"/> holds text that names a
    /// field; <see langword="false"/> when it is <see langword="null"/>, empty or
    /// entirely white space.
    /// </value>
    /// <remarks>
    /// Derived, and therefore without a setter: the answer follows from
    /// <see cref="SortBy"/> and must not be able to contradict it. Its purpose is to
    /// settle the absent-versus-blank question once, here at the boundary, instead of
    /// leaving every service and repository to decide it again and disagree.
    /// </remarks>
    public bool HasSort => !string.IsNullOrWhiteSpace(SortBy);

    /// <summary>
    /// Gets a value indicating whether the caller supplied a filter.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="Query"/> holds text to filter on;
    /// <see langword="false"/> when it is <see langword="null"/>, empty or entirely
    /// white space.
    /// </value>
    /// <remarks>
    /// Derived, and therefore without a setter, for the same reason as
    /// <see cref="HasSort"/>: the interpretation is stated in one place while
    /// <see cref="Query"/> keeps the caller's value untouched.
    /// </remarks>
    // MIGRATION: an omitted filter and an empty filter are the same request, and the
    // legacy null contract is what settles it. Library/Components/Shared/Null.vb line
    // 73 returns the empty string as the string sentinel, not a null reference, and
    // the same file's absence test at line 226 reports an empty string as absent, so
    // a legacy caller that passed empty text meant "no filter". That equivalence is
    // stated here rather than applied to the data: the property still holds whichever
    // form arrived, because normalising one into the other inside a request contract
    // would erase the caller's actual input before a validator could observe it.
    //
    // MIGRATION: this test also treats text that is entirely white space as absent,
    // which widens the legacy empty-string sentinel very slightly. The divergence is
    // deliberate and harmless: the legacy value went straight into a prefix match, so
    // blank text could only ever have matched records beginning with white space.
    // Reading it as "no filter" is the behaviour a caller intends.
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
}

/// <summary>
/// The direction in which a sort field is applied.
/// </summary>
/// <remarks>
/// <para>
/// Declared alongside <see cref="PagedRequest"/> in the same file on purpose.
/// <see cref="PagedRequest.SortDir"/> is its only consumer, the folder holds exactly
/// the four contracts the design calls for, and a fifth file for two members would
/// add a file without adding a boundary. Anything needing this type reaches it
/// through this namespace; nothing should redeclare it elsewhere.
/// </para>
/// <para>
/// <see cref="Ascending"/> is deliberately the zero value, so the default of an
/// unset field and of an omitted query-string parameter is a real, sensible
/// direction rather than an unnamed state that every consumer would have to
/// second-guess.
/// </para>
/// </remarks>
public enum SortDirection
{
    /// <summary>
    /// Order from lowest to highest: A before Z, earlier before later, smaller before
    /// larger. The default direction.
    /// </summary>
    Ascending = 0,

    /// <summary>
    /// Order from highest to lowest: Z before A, later before earlier, larger before
    /// smaller.
    /// </summary>
    Descending = 1,
}
