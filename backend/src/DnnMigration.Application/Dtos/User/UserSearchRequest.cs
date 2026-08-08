using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The paging, sorting and search contract bound from the REQUEST BODY by
/// <c>POST /api/v1/users/search</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS, AND WHY THE SEARCH IS A POST. The account listing accepts four filters that
/// carry personal data: a user name, an email address, and an arbitrary profile-property name paired
/// with the value to match. On <c>GET /api/v1/users</c> those four travel in the REQUEST TARGET, and a
/// request target is the single most widely recorded part of an HTTP exchange - it is written to the
/// browser's own history, to every forward and reverse proxy's access log, to the server's access log,
/// and to any telemetry pipeline that samples URLs. That is CWE-598, <i>Use of GET Request Method With
/// Sensitive Query Strings</i>, and no amount of transport encryption addresses it, because every one
/// of those recorders sits at an endpoint of the encrypted channel rather than in the middle of it.
/// A profile property is the sharpest case: a tenant may define whatever properties it likes, so the
/// value being matched is arbitrary tenant data and may be a national identifier or a telephone
/// number.
/// </para>
/// <para>
/// A request body is not logged by any of those recorders by default, so moving the four filters into
/// one closes the exposure at its source rather than asking every deployment to configure redaction
/// correctly - which is a control that has to be re-applied at every hop and silently stops working
/// when one is added.
/// </para>
/// <para>
/// THE PAGING-ONLY LISTING REMAINS A <c>GET</c>, DELIBERATELY. <c>GET /api/v1/users</c> is retained and
/// is what a caller placing no personal filter uses: it carries a page index, a page size, an ordering
/// and an approval state, none of which identifies a person, and keeping it a <c>GET</c> keeps it
/// cacheable and idempotent. Only a request that would have put personal data in the target is moved,
/// so the change is confined to exactly the requests that needed it.
/// </para>
/// <para>
/// WHY IT DERIVES FROM <see cref="PagedRequest"/> AND NOT FROM <see cref="UserPagedRequest"/>. The four
/// existing derivations of the paging contract are empty by design and are documented as being expected
/// to stay so, because a member added to one of them would be adding a QUERY PARAMETER to the endpoint
/// that binds it. That reasoning does not extend to this type - its members are body members and the
/// entire point of the type is that they are not query parameters - but the invariant is left intact
/// rather than reinterpreted, so this derives from the base directly and the four stay empty.
/// </para>
/// <para>
/// It is an inert carrier and enforces nothing, exactly like its base. Which combinations of filter are
/// legal - in particular that a profile-property name supplied without a value is a refusal rather than
/// a wildcard - is stated by <c>UserSearchRequestValidator</c> and by the application service, never by
/// a property setter: a validator cannot report a value as invalid once a setter has silently rewritten
/// it. Every property is a plain settable auto-property so that JSON deserialisation can populate it,
/// and no binding-source attribute is declared here, which keeps the application layer free of any
/// web-framework reference.
/// </para>
/// </remarks>
public sealed class UserSearchRequest : PagedRequest
{
    /// <summary>
    /// Gets or sets the text a user name must BEGIN WITH, or <see langword="null"/> to place no user-name
    /// restriction.
    /// </summary>
    /// <remarks>
    /// The legacy <c>"Username"</c> search mode (<c>Website/admin/Users/Users.ascx.vb</c> L271). The match
    /// is a PREFIX match and the trailing wildcard is the server's, so the text is supplied raw: appending
    /// one here would produce a doubled pattern and leading with one would silently turn a starts-with
    /// into a contains. Empty text is a legitimate value on this contract and is not the same as absence -
    /// absence is expressed by omitting the member.
    /// </remarks>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the text an email address must BEGIN WITH, or <see langword="null"/> to place no
    /// address restriction.
    /// </summary>
    /// <remarks>
    /// The legacy <c>"Email"</c> search mode (<c>Users.ascx.vb</c> L269), and a prefix match for the same
    /// reason as <see cref="UserName"/>.
    /// </remarks>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets the profile property to match on, or <see langword="null"/> to place no
    /// profile restriction.
    /// </summary>
    /// <remarks>
    /// The legacy arbitrary-property search mode (<c>Users.ascx.vb</c> L274). A name supplied without a
    /// value is refused rather than treated as a wildcard, because a wildcard over an arbitrary tenant
    /// property would return every account that has ever populated it.
    /// </remarks>
    public string? ProfilePropertyName { get; set; }

    /// <summary>
    /// Gets or sets the value <see cref="ProfilePropertyName"/> must BEGIN WITH, or
    /// <see langword="null"/> when no profile restriction is being placed.
    /// </summary>
    /// <remarks>
    /// Supplied raw and matched as a prefix, as its two siblings are. This is the member that most
    /// justifies the whole type: a tenant defines its own profile properties, so this value is arbitrary
    /// personal data whose meaning the server does not know.
    /// </remarks>
    public string? ProfilePropertyValue { get; set; }

    /// <summary>
    /// Gets or sets the approval state to restrict the result to, or <see langword="null"/> to include
    /// both states.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> is A REQUEST FOR THE UNAPPROVED ACCOUNTS and is transmitted as such; it is
    /// not an absence. The member is carried here so that a caller does not have to split one search
    /// across a body and a query string, even though an approval state identifies nobody on its own.
    /// </remarks>
    public bool? IsApproved { get; set; }
}
