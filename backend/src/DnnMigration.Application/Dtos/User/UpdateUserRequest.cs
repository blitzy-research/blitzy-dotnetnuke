namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for <c>PUT /api/v1/users/{userId}</c>, carrying the complete set of fields an
/// administrator may change on an existing DotNetNuke user account.
/// </summary>
/// <remarks>
/// Full-replacement semantics: this expresses PUT, not PATCH. No member carries an "unspecified" marker, so
/// an omitted field deserialises to the empty string and overwrites the stored value, and callers must send
/// the whole field set.
/// </remarks>
// The four members are the intersection of the legacy edit screen's rendered field set and the terminal
// UpdateUser procedure's assignments, which agree exactly. Everything else the legacy screens showed is
// absent for a specific reason.
public class UpdateUserRequest
{
    /// <summary>Gets or sets the user's given name.</summary>
    /// <value>The given name, defaulting to the empty string - the legacy representation of an absent string.</value>
    /// <remarks>
    /// Backed by <c>Users.FirstName</c>, <c>nvarchar(50) NOT NULL</c>. Validator: required, maximum length
    /// 50.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's family name.</summary>
    /// <value>The family name, defaulting to the empty string, not <c>null</c>.</value>
    /// <remarks>
    /// Backed by <c>Users.LastName</c>, whose terminal declaration is <c>nvarchar(50) NOT NULL</c> - the
    /// baseline script's <c>NULL</c> does not survive the upgrade chain. Validator: required, maximum
    /// length 50.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown to other users in place of the sign-in name.</summary>
    /// <value>The display name, defaulting to the empty string.</value>
    /// <remarks>
    /// Backed by <c>Users.DisplayName</c>, <c>nvarchar(128) NOT NULL DEFAULT ''</c>, so an empty display
    /// name is valid stored state; the application service may derive one from the portal's display-name
    /// format and this member performs no derivation. Validator: maximum length 128, and requiring a value
    /// is a policy decision rather than a schema requirement.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's email address.</summary>
    /// <value>The email address, defaulting to the empty string, not <c>null</c>.</value>
    /// <remarks>
    /// Backed by <c>Users.Email</c>, <c>nvarchar(256) NULL</c> - the width is 256, not the 100 of the
    /// baseline column that the upgrade chain dropped. Validator: required (an API-level rule, since the
    /// column permits an absent address), maximum length 256, and not unique.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The optimistic-concurrency token the caller received when it read this account, or <see
    /// langword="null"/> to apply the update unconditionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OPTIONAL, and permissive when omitted, so a caller that predates the token still works exactly as it
    /// did. When supplied and no longer current, the write is refused with
    /// <c>user.concurrency_conflict</c> and nothing is written.
    /// </para>
    /// <para>
    /// Read from <c>UserDetailDto.ConcurrencyToken</c> and sent back unmodified. It is compared for
    /// equality, never written, so it needs neither a column nor a validator.
    /// </para>
    /// </remarks>
    public string? ConcurrencyToken { get; set; }
}
