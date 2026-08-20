using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The paging, sorting and search contract bound from the REQUEST BODY by <c>POST /api/v1/users/search</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS, AND WHY THE SEARCH IS A POST. The account listing accepts four filters that carry
/// personal data: a user name, an email address, and an arbitrary profile-property name paired with the
/// value to match.
/// </para>
/// <para>
/// THE PAGING-ONLY LISTING REMAINS A <c>GET</c>, DELIBERATELY. <c>GET /api/v1/users</c> is retained and is
/// what a caller placing no personal filter uses: it carries a page index, a page size, an ordering and an
/// approval state, none of which identifies a person, and keeping it a <c>GET</c> keeps it cacheable and
/// idempotent.
/// </para>
/// </remarks>
public sealed class UserSearchRequest : PagedRequest
{
    /// <summary>
    /// Gets or sets the text a user name must BEGIN WITH, or <see langword="null"/> to place no user-name
    /// restriction.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the text an email address must BEGIN WITH, or <see langword="null"/> to place no
    /// address restriction.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets the profile property to match on, or <see langword="null"/> to place no profile
    /// restriction.
    /// </summary>
    public string? ProfilePropertyName { get; set; }

    /// <summary>
    /// Gets or sets the value <see cref="ProfilePropertyName"/> must BEGIN WITH, or <see langword="null"/>
    /// when no profile restriction is being placed.
    /// </summary>
    /// <remarks>
    /// Supplied raw and matched as a prefix, as its two siblings are. This is the member that most
    /// justifies the whole type: a tenant defines its own profile properties, so this value is arbitrary
    /// personal data whose meaning the server does not know.
    /// </remarks>
    public string? ProfilePropertyValue { get; set; }

    /// <summary>
    /// Gets or sets the approval state to restrict the result to, or <see langword="null"/> to include both
    /// states.
    /// </summary>
    public bool? IsApproved { get; set; }
}
