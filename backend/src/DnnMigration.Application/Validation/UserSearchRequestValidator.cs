using DnnMigration.Application.Dtos.User;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="UserSearchRequest"/>, narrowing the sortable vocabulary to the account
/// collection's own set.
/// </summary>
/// <remarks>
/// <para>
/// The body-bound account search and the query-bound account listing must accept and refuse EXACTLY the
/// same things, because they are one capability reached two ways: a client moves a request from the
/// target to the body to keep personal data out of access logs, and a client must not discover that the
/// move also changed which page sizes or orderings are legal. So this applies the same paging,
/// direction and filter bounds as <see cref="UserPagedRequestValidator"/> and the same narrow sortable
/// set, and it does so by deriving from the same base rather than by restating any rule.
/// </para>
/// <para>
/// A SEPARATE VALIDATOR TYPE IS UNAVOIDABLE RATHER THAN CHOSEN. FluentValidation resolves a validator by
/// the request's declared TYPE, so a body of a new type has no validator until one is registered for it -
/// and an unvalidated paging contract accepts a negative page index and an unbounded page size, both of
/// which reach the repository. The registration is by assembly scan, so declaring the type here is the
/// whole of the wiring.
/// </para>
/// <para>
/// NO FILTER-COMBINATION RULE IS DECLARED HERE, DELIBERATELY. That a profile-property name supplied
/// without a value is a refusal rather than a wildcard is the application service's rule, and the
/// listing action's own documentation records why it is not re-implemented at the boundary: a boundary
/// copy would give HTTP callers a different answer from every other caller of the service, and the two
/// copies would eventually disagree. Placing it here would additionally make the body-bound search
/// stricter than the query-bound listing, which is precisely the divergence the paragraph above exists
/// to prevent.
/// </para>
/// </remarks>
public sealed class UserSearchRequestValidator : PagedRequestValidator<UserSearchRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UserSearchRequestValidator"/> class.
    /// </summary>
    public UserSearchRequestValidator()
        : base(SortableFields.Users)
    {
    }
}
