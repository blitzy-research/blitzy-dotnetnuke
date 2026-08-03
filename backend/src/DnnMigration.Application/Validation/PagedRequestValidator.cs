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
/// <b>The sortable vocabulary has one owner.</b> The permitted field names are
/// <see cref="SortableFields"/>, which declares the per-collection sets and the union built from
/// them. This validator applies the union, because <see cref="PagedRequest"/> is one shared contract
/// bound by every collection endpoint while FluentValidation resolves one validator per request
/// type: applying the union is what makes an unrecognised name a rejection at the boundary instead
/// of text handed to a store. Whether a name is meaningful for the particular collection being read
/// is the narrower question the per-collection sets answer for the listing that knows which
/// collection it is.
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
public class PagedRequestValidator : AbstractValidator<PagedRequest>
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
    private static readonly string SortFieldUnknownMessage = BuildSortFieldUnknownMessage();

    /// <summary>
    /// Initialises a new instance of the <see cref="PagedRequestValidator"/> class and declares the
    /// bounds that apply to every collection endpoint.
    /// </summary>
    /// <remarks>
    /// Parameterless by design. Every rule declared here asks a question about the shape of the
    /// request and none asks a question about state, so there is nothing to inject and nothing to
    /// configure. Page indexing is zero-based throughout, mirroring both
    /// <see cref="PagedRequest"/> and the <c>PagedResult&lt;T&gt;</c> envelope the caller reads back.
    /// </remarks>
    public PagedRequestValidator()
    {
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
        // Guarded by the request contract's own absent-versus-blank test rather than by a second
        // interpretation of it, so a caller who expressed no preference is never asked to justify a
        // field they did not name. The predicate independently tolerates absence, which keeps the
        // rule correct if the guard is ever read in isolation.
        RuleFor(request => request.SortBy)
            .Must(SortableFields.IsPermitted)
            .WithMessage(SortFieldUnknownMessage)
            .When(request => request.HasSort);
    }

    /// <summary>
    /// Composes the message that names every field a caller may sort by.
    /// </summary>
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
    private static string BuildSortFieldUnknownMessage()
    {
        IEnumerable<string> accepted = SortableFields.All.OrderBy(name => name, StringComparer.Ordinal);

        return "The sort field is not one that can be ordered by. Accepted fields: "
            + string.Join(", ", accepted)
            + ".";
    }
}
