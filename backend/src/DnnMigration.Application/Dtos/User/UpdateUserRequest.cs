namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for <c>PUT /api/v1/users/{userId}</c>, carrying the complete set of fields an
/// administrator may change on an existing DotNetNuke user account.
/// </summary>
/// <remarks>
/// <para>
/// Full-replacement semantics: this expresses PUT, not PATCH. No member carries an "unspecified" marker,
/// so an omitted field deserialises to the empty string and overwrites the stored value, and callers must
/// send the whole field set. The legacy string sentinel for "absent" is itself the empty string, so no
/// member converts between empty and null in either direction and serialisation never skips null or
/// default values.
/// </para>
/// <para>
/// Validation lives in <c>Application/Validation/UpdateUserRequestValidator.cs</c>; this type carries no
/// attribute, rule or normalising accessor, so nothing here can rewrite what the caller sent. Two
/// whole-request rules for that author: email uniqueness must NOT be enforced (the table's only unique
/// constraint is on <c>Username</c> and the legacy membership provider was registered with
/// <c>requiresUniqueEmail="false"</c>), and the email pattern is overridable per portal through the
/// tenant's <c>Security_EmailValidation</c> setting.
/// </para>
/// <para>
/// Unlike <c>CreateUserRequest</c> and <c>ChangePasswordRequest</c> this request carries no credential
/// material, so an instance is safe to write to a structured request log in full.
/// </para>
/// </remarks>
// MIGRATION: the four members are the intersection of the legacy edit screen's rendered field set and the
// terminal UpdateUser procedure's assignments, which agree exactly. Everything else the legacy screens showed
// is absent for a specific reason. The user identifier comes from the route and the tenant from the resolved
// portal context: the Users table has no portal column at all, and accepting one here would let a caller move
// a user between tenants. Username is the unique natural key the ASP.NET membership store also keys on, and
// renaming a user is not an operation the legacy application offers. AffiliateId and IsSuperUser are
// create-time columns that UpdateUser never assigns, and honouring an inbound IsSuperUser would hand a caller
// host rights through an ordinary profile edit. Credentials, the membership flags (approved, lockout,
// force-change-on-next-signin) and role assignments are state TRANSITIONS or resources of their own, each
// gated on preconditions a general PUT would discard, so each is a route-only sub-resource action. Profile
// values, including the address and telephone the legacy grid showed beside these fields, are written through
// PUT /api/v1/users/{userId}/profile. The presence, sign-in, activity, lockout, credential-change and
// creation stamps are all server-computed.
public class UpdateUserRequest
{
    /// <summary>Gets or sets the user's given name.</summary>
    /// <value>The given name, defaulting to the empty string - the legacy representation of an absent
    /// string.</value>
    /// <remarks>Backed by <c>Users.FirstName</c>, <c>nvarchar(50) NOT NULL</c>. Validator: required, maximum
    /// length 50.</remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's family name.</summary>
    /// <value>The family name, defaulting to the empty string, not <c>null</c>.</value>
    /// <remarks>Backed by <c>Users.LastName</c>, whose terminal declaration is <c>nvarchar(50) NOT NULL</c> -
    /// the baseline script's <c>NULL</c> does not survive the upgrade chain. Validator: required, maximum
    /// length 50.</remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown to other users in place of the sign-in name.</summary>
    /// <value>The display name, defaulting to the empty string.</value>
    /// <remarks>Backed by <c>Users.DisplayName</c>, <c>nvarchar(128) NOT NULL DEFAULT ''</c>, so an empty
    /// display name is valid stored state; the application service may derive one from the portal's
    /// display-name format and this member performs no derivation. Validator: maximum length 128, and
    /// requiring a value is a policy decision rather than a schema requirement.</remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's email address.</summary>
    /// <value>The email address, defaulting to the empty string, not <c>null</c>.</value>
    /// <remarks>Backed by <c>Users.Email</c>, <c>nvarchar(256) NULL</c> - the width is 256, not the 100 of the
    /// baseline column that the upgrade chain dropped. Validator: required (an API-level rule, since the
    /// column permits an absent address), maximum length 256, and not unique. The address shape rule lives
    /// once, in <see cref="Domain.ValueObjects.EmailAddress"/>.</remarks>
    public string Email { get; set; } = string.Empty;
}
