using DnnMigration.Application.Dtos.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the bounds of <see cref="PagedRequest"/>, the paging, sorting and filtering query
/// contract that every collection endpoint accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Page indexing is zero-based.</b> A <see cref="PagedRequest.PageIndex"/> of 0 addresses the
/// first page, so the smallest legal index is 0 and every negative index is refused - including the
/// legacy integer absence sentinel of minus one. The base is restated here rather than merely
/// referenced because it is a three-way contract: the request contract states it on
/// <see cref="PagedRequest.PageIndex"/>, the reply envelope states the same base on
/// <c>PagedResult&lt;T&gt;.PageIndex</c>, and this type is what enforces it. A disagreement between
/// the three would not announce itself as a failure - it would quietly serve the neighbouring page -
/// so the base is stated at all three ends and no reader has to open another file to learn it.
/// </para>
/// <para>
/// <b>This type exists because the request contract deliberately enforces nothing.</b>
/// <see cref="PagedRequest"/> holds plain auto-properties and no clamping setter, precisely so that a
/// validator can observe what a caller actually sent: a setter that silently rewrote a page size of
/// zero to ten would leave nothing here to report. Every coordinate therefore arrives raw, and the
/// bounds below are not defence in depth - they are the only enforcement that exists.
/// </para>
/// <para>
/// <b>Bounds are rejections, never substitutions.</b> No rule below rewrites, trims, coerces or
/// clamps a value, and this type has no correcting path at all. A caller who asks for an impossible
/// page learns that the request was malformed instead of silently receiving a page they never asked
/// for.
/// </para>
/// <para>
/// <b>An unchecked coordinate was not merely unbounded, it was a fault.</b> The reply envelope
/// refuses negative coordinates by throwing rather than by reporting - <c>PagedResult&lt;T&gt;.Create</c>
/// guards its page index and page size with <c>ArgumentOutOfRangeException.ThrowIfNegative</c> - so a
/// negative index that got past the boundary became an unhandled invariant failure, which the API
/// edge can only render as a server fault. Rejecting it here turns a 500 into the field-level 400 it
/// always was.
/// </para>
/// <para>
/// <b>The sortable vocabulary has one owner, and the narrow set is what binds.</b> The permitted
/// field names are declared once in <c>SortableFields</c>, which holds a set per collection and the
/// union built from them. The set THIS instance applies is supplied to its constructor, so each
/// collection's endpoint is bounded by its own set rather than by the union: naming a portal field
/// while reading accounts is a field-level rejection instead of a parameter the listing silently
/// discards. That is a correction. FluentValidation resolves one validator per request TYPE, every
/// collection endpoint used to bind the one shared <see cref="PagedRequest"/>, and the union was
/// therefore the only bound anything applied - so the per-collection sets had no consumer and a name
/// belonging to another collection was accepted and then ignored. Each collection now binds its own
/// derived request type, which is what gives its set somewhere to be enforced.
/// </para>
/// <para>
/// <b>This type is generic so that one set of rules serves every derived request.</b> The paging,
/// direction and filter bounds are identical for every collection and are declared once here; only
/// the sortable set differs, and it arrives as a constructor argument. The non-generic
/// <see cref="PagedRequestValidator"/> derived from it applies the union to a bare
/// <see cref="PagedRequest"/> - a shape no registered endpoint binds any longer, but one the
/// contract still permits - so even an unspecialised request is refused an unrecognised name.
/// </para>
/// <para>
/// Rules are declarative and stateless. Nothing here performs a lookup, reaches a store or takes a
/// collaborator, which is why the constructor is parameterless. Turning a failure declared here into
/// an RFC 7807 response is the API layer's work, so no status code or response shape appears in this
/// file.
/// </para>
/// </remarks>
// MIGRATION: PAGING BOUNDS VALIDATION IS NET-NEW. The legacy application validated no paging
// coordinate anywhere - there is no declarative validator and no resource key for paging in the
// legacy tree - because the absence of a bound WAS the legacy contract. Nothing here translates a
// legacy rule; every rule below is a new boundary over a surface that previously had none, adopted
// because the caller of this contract is an anonymous HTTP client rather than a trusted operator.
//
// MIGRATION: THE LEGACY "RETURN EVERYTHING, UNPAGED" SENTINEL IS DELIBERATELY NOT REPRODUCED. The
// legacy overloads expressed it by passing the integer absence sentinel of minus one as the page
// index, the page size and the total alike - Library/Components/Users/UserController.vb line 687 and
// line 706 - and that sentinel is minus one exactly (Library/Components/Shared/Null.vb line 41),
// which the legacy absence test also reports as absent. A negative page coordinate was therefore
// indistinguishable from a missing one. No negative value is accepted here. "Everything" has its own
// named representation on the reply envelope - PagedResult<T>.Unpaged - so no caller ever needs a
// magic number to ask for every record.
//
// MIGRATION: THE ZERO-BASED BASE IS THE LEGACY DATA LAYER'S, NOT THE LEGACY SCREEN'S, and the legacy
// stack never wrote it down because the two halves disagreed. The account administration screen
// counted pages from one (Website/admin/Users/Users.ascx.vb line 51) and subtracted one on every
// call down to the provider (lines 265, 269, 271 and 274), so the provider index was zero-based
// while the number shown to the operator was one-based. Naming the base on the wire contract and
// enforcing it here removes the subtraction, and with it the off-by-one that an undocumented split
// invites.
//
// MIGRATION: THE MAXIMUM PAGE SIZE IS A NEW BOUND WITH NO LEGACY PREDECESSOR, and its absence in the
// legacy application was measured rather than assumed. The legacy page size was an
// operator-configured module setting seeded with ten (Library/Components/Users/UserModuleBase.vb
// lines 134 and 135), no screen constrained it, and no configuration key bounded it either - there is
// no paging key of any kind in Website/release.config. The value is a named constant rather than a
// configured option because this project cannot express a configured option: IOptions<T> is not
// available to it, as DnnMigration.Application.csproj records against a verified CS0234 and CS0246,
// and the constructor is parameterless by design. A constant read by every consumer is the next best
// thing to a configured one, and strictly better than a literal repeated at each site.
public class PagedRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : PagedRequest
{
    /// <summary>
    /// The largest page size a caller may ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten times the measured legacy default of ten, so every page size the legacy module setting
    /// could plausibly have carried remains expressible while a single request cannot ask the server
    /// to materialise an unbounded result set.
    /// </para>
    /// <para>
    /// Public so that the API layer can publish the figure in its generated OpenAPI document, the
    /// application services can refuse the same bound on the paths that reach them without a
    /// validator, and a test can assert against it - each without restating the number. One
    /// definition and many readers is the point: a bound copied into a second place is a bound that
    /// can drift.
    /// </para>
    /// </remarks>
    public const int MaximumPageSize = 100;

    /// <summary>
    /// The longest free-text filter a caller may supply, taken from the terminal width of the widest
    /// column any collection endpoint filters on, which is <c>dbo.Users.Email nvarchar(256) NULL</c>.
    /// </summary>
    /// <remarks>
    /// A filter longer than the widest filterable column cannot match any stored value, so the bound
    /// rejects only requests that were already incapable of returning a row. It is a length bound and
    /// nothing more: the text itself passes through exactly as the caller typed it, because composing
    /// it into a match pattern is work that happens behind the repository interfaces and not here.
    /// </remarks>
    public const int QueryMaximumLength = 256;

    /// <summary>
    /// Message reported when a caller supplies a negative page index.
    /// </summary>
    /// <remarks>
    /// The message states the base explicitly, because the commonest cause of a negative index is a
    /// client that counted from one and subtracted twice.
    /// </remarks>
    private const string PageIndexNegativeMessage =
        "The page index may not be negative. Page indexes are zero-based, so the first page is 0.";

    /// <summary>
    /// Message reported when a caller asks for a page of zero or fewer records.
    /// </summary>
    private const string PageSizeTooSmallMessage =
        "The page size must be at least 1.";

    /// <summary>
    /// Message reported when a caller supplies a sort direction that is not one of the two declared
    /// members.
    /// </summary>
    private const string SortDirectionUnknownMessage =
        "The sort direction must be either Ascending or Descending.";

    /// <summary>
    /// Message reported when a caller asks for more records than <see cref="MaximumPageSize"/>
    /// permits.
    /// </summary>
    /// <remarks>
    /// Composed from the bound rather than restating it, and composed culture-invariantly so the
    /// figure reads identically for every caller.
    /// </remarks>
    private static readonly string PageSizeTooLargeMessage =
        FormattableString.Invariant($"The page size may not exceed {MaximumPageSize}.");

    /// <summary>
    /// Reported when the addressed page lies so far into the sequence that the number of records to skip
    /// past it cannot be represented.
    /// </summary>
    /// <remarks>
    /// It names both fields because neither is wrong on its own: the offset is their product, so a caller
    /// can correct either one. It gives the bound rather than an adjective, so the correction is arithmetic
    /// rather than guesswork.
    /// </remarks>
    private static readonly string PageOffsetUnrepresentableMessage =
        FormattableString.Invariant($"The page index multiplied by the page size may not exceed {int.MaxValue}.")
        + " That is the furthest position a paged read can address.";

    /// <summary>
    /// Message reported when a caller supplies a filter longer than
    /// <see cref="QueryMaximumLength"/>.
    /// </summary>
    private static readonly string QueryTooLongMessage =
        FormattableString.Invariant($"The filter text may not exceed {QueryMaximumLength} characters.");

    /// <summary>
    /// Message reported when a caller names a field that nothing can be ordered by, listing the
    /// names that are accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built once from <see cref="SortableFields.All"/> rather than transcribed, so widening a
    /// collection's sortable set cannot leave this message behind. The names are ordered so that the
    /// text is identical on every run and in every process, which a set's own enumeration order does
    /// not guarantee.
    /// </para>
    /// <para>
    /// <b>The rejected value is deliberately not echoed.</b> Naming what is accepted tells a caller
    /// how to correct the request, whereas repeating what they sent tells them nothing they did not
    /// already know and reflects caller-supplied text back into a response body.
    /// </para>
    /// </remarks>
    private readonly IReadOnlySet<string> _sortableFields;

    private readonly string _sortFieldUnknownMessage;

    /// <summary>
    /// Initialises a new instance of the <see cref="PagedRequestValidator{TRequest}"/> class and
    /// declares the bounds that apply to every collection endpoint.
    /// </summary>
    /// <param name="sortableFields">
    /// The closed set of field names this endpoint's collection can be ordered by, taken from
    /// <c>SortableFields</c>. It is a constructor argument rather than a virtual member so that the
    /// rule below is declared once and cannot be reached before a derived constructor has run.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sortableFields"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The ONE argument is the sortable set. Every rule declared here asks a question about the shape
    /// of the request and none asks a question about state, so nothing else is injected and nothing
    /// else is configurable. Page indexing is zero-based throughout, mirroring both
    /// <see cref="PagedRequest"/> and the <c>PagedResult&lt;T&gt;</c> envelope the caller reads back.
    /// </remarks>
    protected PagedRequestValidator(IReadOnlySet<string> sortableFields)
    {
        ArgumentNullException.ThrowIfNull(sortableFields);

        _sortableFields = sortableFields;
        _sortFieldUnknownMessage = BuildSortFieldUnknownMessage(sortableFields);

        // Zero-based, so 0 is the first page and the smallest legal value. A negative index is
        // rejected rather than reinterpreted, which is what stops the legacy absence sentinel of
        // minus one arriving disguised as a page address. The asymmetry is deliberate and must not
        // be generalised: a page index is a position, not an identifier, and the legacy schema
        // seeds real identifiers at minus one and at zero - Portals.PortalID is IDENTITY(-1,1) and
        // Roles.RoleID is IDENTITY(0,1) - so refusing a negative identifier elsewhere would refuse
        // a legitimate row.
        RuleFor(request => request.PageIndex)
            .GreaterThanOrEqualTo(0)
            .WithMessage(PageIndexNegativeMessage);

        // Strictly positive, so zero is refused alongside every negative. Zero is refused on the
        // REQUEST even though it is meaningful on the REPLY, where the envelope reads a page size of
        // zero as its unpaged, all-records state: the two are not in conflict and neither should be
        // bent to resemble the other. An unpaged reply is produced by the server through the
        // envelope's own factory; it is not something a caller may ask for by sending a page size of
        // nothing. Refusing zero also keeps any derived page count away from a division by zero.
        RuleFor(request => request.PageSize)
            .GreaterThan(0)
            .WithMessage(PageSizeTooSmallMessage)
            .LessThanOrEqualTo(MaximumPageSize)
            .WithMessage(PageSizeTooLargeMessage);

        // THE PRODUCT IS BOUNDED, NOT ONLY THE TWO FACTORS. Each field alone can be within its own bound
        // while their product is not: with the largest permitted page size, any index above roughly
        // twenty-one million overflows the signed 32-bit offset a paged read skips by. Unchecked, that
        // multiplication wrapped to a NEGATIVE offset, which the query provider refuses deep inside the
        // read - so a request a caller could correct arrived as an unhandled server fault with no field
        // named. Refusing it here is what makes it a 400 that says which fields to change.
        //
        // The rule is declared on the request rather than on either field because the fault is the
        // relationship between them, and FluentValidation reports it against the whole request accordingly.
        // The arithmetic widens to 64 bits BEFORE multiplying, so the test itself cannot overflow - writing
        // it as "PageIndex > int.MaxValue / PageSize" would avoid overflow too but would silently truncate
        // the division and admit a band of offsets just past the bound.
        //
        // The clamp in the domain's paging helper is the second half of this defence and not a substitute
        // for it: the helper keeps a read total for any caller that reaches a repository without passing
        // through request validation, while this rule is what tells an actual caller what to fix.
        RuleFor(request => request)
            .Must(request => (long)request.PageIndex * request.PageSize <= int.MaxValue)
            .When(request => request.PageIndex > 0 && request.PageSize > 0)
            .WithName(nameof(PagedRequest.PageIndex))
            .WithMessage(PageOffsetUnrepresentableMessage);

        // Model binding will place an undeclared integer into an enum-typed property without
        // complaint, so membership is checked rather than assumed. Only the two declared directions
        // can order anything; a third value would reach a repository with no defined meaning.
        RuleFor(request => request.SortDir)
            .IsInEnum()
            .WithMessage(SortDirectionUnknownMessage);

        // MIGRATION: A LENGTH BOUND AND NOTHING ELSE. The filter is not trimmed, unescaped,
        // decorated or otherwise rewritten, because the legacy prefix match was composed at the CALL
        // SITE - the account screen appended a trailing per-cent wildcard to the search text before
        // calling down (Website/admin/Users/Users.ascx.vb lines 269, 271 and 274) - and pattern
        // composition now belongs behind the repository interfaces. Decorating the text here would
        // leave a repository unable to tell a literal per-cent typed by a user from a wildcard added
        // by a caller, and would decorate it twice as soon as a second caller did the same.
        //
        // MIGRATION: AN ABSENT FILTER AND AN EMPTY FILTER ARE THE SAME REQUEST, because the legacy
        // string sentinel is the empty string rather than null (Library/Components/Shared/Null.vb
        // line 71) and the legacy absence test reports an empty string as absent. A length rule
        // satisfies both by construction - null and empty text alike have nothing to measure against
        // the bound - so the two are treated identically without a guard that would have to decide
        // between them. The rule is left unguarded on purpose: a guard keyed to "a filter is present"
        // would let entirely blank text of any length past the bound.
        RuleFor(request => request.Query)
            .MaximumLength(QueryMaximumLength)
            .WithMessage(QueryTooLongMessage);

        // MIGRATION: CALLER-CHOSEN ORDERING IS NET-NEW, so this rule reproduces no legacy rule. No
        // markup under Website/admin declares a sorting affordance of any kind - the legacy grids
        // rendered whatever order the stored procedure produced and offered the operator no way to
        // change it - so there is no legacy set of sortable columns to transcribe. The permitted
        // names are therefore derived from a stated rule and declared once, in SortableFields, so
        // that this boundary and the listings that read the narrower per-collection sets cannot
        // drift apart.
        //
        // MIGRATION: THIS RULE IS THE OUTER BOUND, NOT THE WHOLE ENFORCEMENT, and it cannot be
        // narrowed to the collection being read. FluentValidation resolves one validator per request
        // TYPE, and PagedRequest is the single request type every collection endpoint binds, so the
        // narrowest vocabulary this rule can possibly apply is the union of every collection's set -
        // it has no way to learn which collection the caller addressed. Applying the union here is
        // what turns an entirely unrecognised name into a boundary rejection instead of text handed
        // to a store. The remaining question - whether a recognised name means anything for THIS
        // collection - is answered by SortableFields.IsPermittedFor, called by each listing service
        // against its own set before it dispatches to a repository. Enforcing it there rather than
        // here also binds callers that never pass through validation at all, such as another service
        // or a test. Neither half is redundant: without this rule an unknown name reaches a store,
        // and without the service-side check a role-only name would be accepted for a portal listing
        // and then silently ordered by that listing's default.
        //
        // Guarded by the request contract's own absent-versus-blank test rather than by a second
        // interpretation of it, so a caller who expressed no preference is never asked to justify a
        // field they did not name. The predicate independently tolerates absence, which keeps the
        // rule correct if the guard is ever read in isolation.
        RuleFor(request => request.SortBy)
            .Must(name => SortableFields.IsPermitted(_sortableFields, name))
            .WithMessage(_sortFieldUnknownMessage)
            .When(request => request.HasSort);
    }

    /// <summary>
    /// Composes the message that names every field a caller may sort by.
    /// </summary>
    /// <param name="sortableFields">The set this validator applies.</param>
    /// <returns>
    /// A message stating that the supplied field cannot be ordered by, followed by the accepted
    /// names in a stable order.
    /// </returns>
    /// <remarks>
    /// Ordered with an ordinal comparison because these are programmatic identifiers rather than
    /// words, which is the same reason <see cref="SortableFields"/> compares them ordinally: a
    /// culture-sensitive ordering of identifiers is how the Turkish dotless-i class of defect
    /// arises.
    /// </remarks>
    private static string BuildSortFieldUnknownMessage(IReadOnlySet<string> sortableFields)
    {
        IEnumerable<string> accepted = sortableFields.OrderBy(name => name, StringComparer.Ordinal);

        return "The sort field is not one that can be ordered by. Accepted fields: "
            + string.Join(", ", accepted)
            + ".";
    }
}

/// <summary>
/// Applies the unspecialised paging bounds, with the UNION of every collection's sortable set, to a
/// bare <see cref="PagedRequest"/>.
/// </summary>
/// <remarks>
/// No registered endpoint binds a bare <see cref="PagedRequest"/> any longer - each collection binds
/// its own derived type so that its own narrow set is the one enforced - but the shared contract is
/// public and a caller of the application layer may still pass one, so it keeps a validator. Applying
/// the union here is the correct outer bound for a request that has not said which collection it
/// addresses: it still guarantees that what reaches an ordering clause is a constant this assembly
/// declared, while leaving the narrower question to the derived validators.
/// </remarks>
public class PagedRequestValidator : PagedRequestValidator<PagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="PagedRequestValidator"/> class.
    /// </summary>
    public PagedRequestValidator()
        : base(SortableFields.All)
    {
    }
}
