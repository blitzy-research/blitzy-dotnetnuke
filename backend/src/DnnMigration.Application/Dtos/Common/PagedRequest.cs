namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering query contract that a collection endpoint accepts from its
/// caller.
/// </summary>
/// <remarks>
/// <para>
/// Page indexing is zero-based, mirroring the index that <c>PagedResult&lt;T&gt;</c> in the domain
/// layer reports back. The two halves of the round trip have to agree, because a caller sends this
/// contract and then reads that envelope; a disagreement would quietly serve the neighbouring page
/// instead of failing, so the base is restated on <see cref="PageIndex"/> and neither end has to open
/// the other file to learn it.
/// </para>
/// <para>
/// This is the request half of the pair that retires the legacy <c>ByRef totalRecords</c> status
/// argument. It owns the sort field, the sort direction and the free-text filter precisely because
/// the response envelope refuses them: what was asked for is a property of the asking, and echoing it
/// back on the reply would create a second place for it to disagree.
/// </para>
/// <para>
/// It is an inert carrier and enforces nothing. Bounds belong to a request validator under
/// <c>Application/Validation/</c>, which is why no property below corrects, coerces or clamps what a
/// caller supplied: a validator cannot report a page size of zero as invalid once a setter has
/// silently rewritten it to ten. Composing an offset-and-limit read is likewise data-access work done
/// behind the repository interfaces. Every property is a plain settable auto-property so that
/// query-string model binding and JSON deserialisation can each populate it, and the binding-source
/// attribute is declared by the controller rather than here, which keeps the application layer free of
/// any web-framework reference.
/// </para>
/// <para>
/// <b>IT IS DELIBERATELY NOT SEALED, AND EVERY REGISTERED ENDPOINT BINDS A DERIVATION OF IT RATHER
/// THAN THIS TYPE.</b> The derivations - <see cref="PortalPagedRequest"/>,
/// <see cref="RolePagedRequest"/>, <see cref="UserPagedRequest"/> and
/// <see cref="ModulePagedRequest"/> - add no member and change no behaviour. They exist solely to give
/// each collection a request TYPE of its own, because a type is the only thing a validator registry
/// dispatches on: with one shared type, a single validator serves every endpoint and the only sortable
/// vocabulary it can apply is the union of every collection's field names, so ordering the account
/// listing by a portal field would answer <c>200 OK</c> having silently discarded the parameter.
/// Unsealing is what lets each collection's narrow sortable set be enforced at the boundary. A
/// derivation that added a property would be adding a query parameter, which belongs on the action
/// that declares it, so the four are empty and are expected to stay so.
/// </para>
/// </remarks>
// MIGRATION: the legacy "return everything, unpaged" call shape is deliberately NOT reproduced. It
// was expressed by handing the integer null sentinel of minus one to the page index, the page size
// and the total alike (Library/Components/Users/UserController.vb L687 and L706), so a negative page
// coordinate was indistinguishable from a missing one. No negative value is accepted here, and the
// unpaged case has its own named representation on PagedResult, so no caller needs a sentinel to ask
// for every record.
//
// MIGRATION: naming the page coordinates on the request, and the records with their grand total on
// the reply, removes a second database round trip. The legacy membership surface could not answer
// "which page, and how many altogether?" in one call, so it shipped an independent count member
// (GetUserCountByPortal) alongside paged readers that returned a forward-only reader and no total.
public class PagedRequest
{
    /// <summary>
    /// Gets or sets the zero-based index of the page of records to return, defaulting to 0 and
    /// therefore to the first page - which is what a caller who supplied no page meant.
    /// </summary>
    /// <remarks>
    /// Zero-based is a contract rather than a note: the value travels to the server on this type and
    /// comes back on <c>PagedResult&lt;T&gt;</c>, and both count from zero, so a client may echo one
    /// into the other without arithmetic. A negative index is invalid, including the legacy integer
    /// null sentinel of minus one; it is rejected by the request validator rather than reinterpreted,
    /// so a sentinel cannot arrive disguised as a page address.
    /// </remarks>
    // MIGRATION: the zero-based base is the legacy DATA layer's, not the legacy screen's, and the
    // legacy stack never wrote it down. The administration screen counted from one
    // (Website/admin/Users/Users.ascx.vb L51) and subtracted one on every call down to the provider
    // (L265, L269, L271, L274); the shipped schema settles it independently, because the paging
    // procedures set a lower bound to the page size multiplied by the page index
    // (03.01.01.SqlDataProvider L38), so index zero addresses the first row.
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of records the caller is willing to receive, defaulting to 10.
    /// </summary>
    /// <remarks>
    /// Zero and negative sizes are invalid and are reported by the request validator rather than
    /// silently replaced here, so a caller learns that a request was malformed instead of receiving a
    /// page it never asked for. An upper bound belongs to the validator for the same reason: capping a
    /// page size protects the server, but it has to be visible as a rejection or an echoed-back size,
    /// never as a quiet substitution inside a setter.
    /// </remarks>
    // MIGRATION: the default of ten is the measured legacy default, not a convention.
    // Library/Components/Users/UserModuleBase.vb L134-L135 seed the Records_PerPage module setting
    // with ten whenever it is unset, and the administration screen read its page size from exactly
    // that setting, so an unparameterised list request still returns the number of rows it always did.
    public int PageSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the name of the field to sort by, or <see langword="null"/> when the caller
    /// expresses no preference and the server's own ordering applies.
    /// </summary>
    /// <remarks>
    /// A field name, not an expression: the set of sortable names is decided by the repository serving
    /// the endpoint, and an unrecognised name is a validation failure rather than something passed
    /// through to a store. Both <see langword="null"/> and blank text mean "no preference", as
    /// <see cref="HasSort"/> reports, and neither is rewritten into the other here.
    /// </remarks>
    // MIGRATION: caller-chosen ordering has no legacy counterpart - no markup under Website/admin/Users
    // declares a sorting affordance, so the legacy grids rendered whatever order the stored procedure
    // produced. Exposing the choice adds a capability without altering a business rule, because the
    // server still decides which names are sortable and how each one orders.
    public string? SortBy { get; set; }

    /// <summary>
    /// Gets or sets the direction in which <see cref="SortBy"/> is applied, ascending by default.
    /// </summary>
    /// <remarks>
    /// Consulted only when <see cref="HasSort"/> is <see langword="true"/>, but retained rather than
    /// discarded otherwise, so a caller toggling a direction before choosing a field does not lose the
    /// setting.
    /// </remarks>
    public SortDirection SortDir { get; set; }

    /// <summary>
    /// Gets or sets the caller's free-text filter, raw and exactly as typed, or
    /// <see langword="null"/> when no filter applies.
    /// </summary>
    /// <remarks>
    /// No wildcard, escape or pattern syntax is added here and none is expected from the caller;
    /// turning this text into a match pattern is the repository's work. Both <see langword="null"/>
    /// and blank text mean "no filter", as <see cref="HasQuery"/> reports.
    /// </remarks>
    // MIGRATION: the legacy prefix match was composed at the call site - the administration screen
    // appended a trailing per-cent wildcard to the search text before calling down
    // (Website/admin/Users/Users.ascx.vb L269, L271, L274). Pattern composition moves behind the
    // repository interfaces and this property deliberately does not participate: pre-decorating the
    // text would leave the repository unable to distinguish a literal per-cent typed by a user from a
    // wildcard added by a caller, and would decorate the value twice as soon as a second caller did
    // the same thing.
    public string? Query { get; set; }

    /// <summary>
    /// Gets a value indicating whether the caller asked for a particular ordering:
    /// <see langword="false"/> when <see cref="SortBy"/> is <see langword="null"/>, empty or entirely
    /// white space.
    /// </summary>
    /// <remarks>
    /// Derived, and therefore without a setter: the answer follows from <see cref="SortBy"/> and must
    /// not be able to contradict it. Its purpose is to settle the absent-versus-blank question once,
    /// here at the boundary, instead of leaving every service and repository to decide it again and
    /// disagree.
    /// </remarks>
    public bool HasSort => !string.IsNullOrWhiteSpace(SortBy);

    /// <summary>
    /// Gets a value indicating whether the caller supplied a filter: <see langword="false"/> when
    /// <see cref="Query"/> is <see langword="null"/>, empty or entirely white space.
    /// </summary>
    /// <remarks>
    /// Derived, and therefore without a setter, for the same reason as <see cref="HasSort"/>: the
    /// interpretation is stated in one place while <see cref="Query"/> keeps the caller's value
    /// untouched, because normalising one form into the other inside a request contract would erase
    /// the caller's actual input before a validator could observe it.
    /// </remarks>
    // MIGRATION: an omitted filter and an empty filter are the same request, because the legacy string
    // sentinel is the empty string and the legacy absence test reports an empty string as absent, so a
    // legacy caller that passed empty text meant "no filter". This test additionally treats
    // all-white-space text as absent, which widens that sentinel very slightly; the divergence is
    // deliberate and harmless, since the legacy value went straight into a prefix match and blank text
    // could only ever have matched records beginning with white space.
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
}

/// <summary>
/// The direction in which a sort field is applied.
/// </summary>
/// <remarks>
/// Declared alongside <see cref="PagedRequest"/> on purpose: <see cref="PagedRequest.SortDir"/> is its
/// only consumer, and a separate file for two members would add a file without adding a boundary.
/// <see cref="Ascending"/> is deliberately the zero value, so the default of an unset field and of an
/// omitted query-string parameter is a real, sensible direction rather than an unnamed state every
/// consumer would have to second-guess.
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
