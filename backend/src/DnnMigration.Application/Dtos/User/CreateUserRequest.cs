namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for user creation: the request body bound by <c>UsersController</c> on <c>POST
/// /api/v1/users</c>, which answers <c>201 Created</c> with a <c>UserDetailDto</c>.
/// </summary>
/// <remarks>
/// <para>
/// FOUR LEGACY CREATE CONTROLS ARE DELIBERATELY NOT REPRODUCED, and their absence is a contract decision
/// rather than an omission.
/// </para>
/// <para>
/// MEASURED PASSWORD POLICY, preserved verbatim from the legacy membership provider registration in
/// <c>Website/release.config</c> lines 217 to 249: minimum length 7; minimum non-alphanumeric characters 0;
/// question and answer NOT required; unique email NOT required; application name DotNetNuke.
/// </para>
/// </remarks>
public sealed class CreateUserRequest
{
    // DELIBERATE OMISSIONS
    // No tenant identifier is accepted in the body. The legacy user object exposed a portal identifier, but
    // the Users table has no such column - per-portal facts live on the UserPortals table, which is why the
    // target splits UserPortal into its own entity.

    /// <summary>The login name. Required.</summary>
    /// <remarks>
    /// Terminal schema: nvarchar(100) NOT NULL, and the single UNIQUE NONCLUSTERED constraint on the Users
    /// table. Uniqueness is enforced by the service against the data store, not by this type.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>The given name. Required.</summary>
    /// <remarks>
    /// Terminal schema: nvarchar(50) NOT NULL. The legacy editor attributes for this property agree with
    /// the column, so there is nothing to reconcile.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>The family name. Required.</summary>
    /// <remarks>
    /// MEASURED SCHEMA FINDING - IMPORTANT FOR THE VALIDATOR AUTHOR. The baseline script declares this
    /// column nullable, but the baseline is not the terminal state: scripts 01.00.05 and 01.00.06 rebuild
    /// the table through a temporary copy, drop the original and rename the copy into place, and the
    /// rebuilt column is nvarchar(50) NOT NULL (01.00.05:L18, 01.00.06:L186).
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>The name shown to other users.</summary>
    /// <remarks>
    /// Terminal schema: nvarchar(128) NOT NULL with a default of the empty string, added by script
    /// 03.02.03. Because the column defaults to empty, omitting a display name is legitimate and the
    /// service derives one.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The email address. Required.</summary>
    public string Email { get; set; } = string.Empty;

    // The credential members below are UNCONDITIONALLY required, because the random-password branch is not
    // part of this contract.

    /// <summary>The plaintext password, inbound only. Required.</summary>
    /// <remarks>
    /// Measured strength rules are minimum length 7 and minimum non-alphanumeric characters 0.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>The repeated password used to catch typing errors. Required.</summary>
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>When true, the new account is approved immediately.</summary>
    /// <remarks>
    /// Maps to the approval assignment the legacy page drove from its authorise checkbox. The service
    /// decides what an unapproved new account means.
    /// </remarks>
    public bool Authorize { get; set; }

    // MIGRATION: THE NOTIFY FLAG IS NOT A MEMBER OF THIS CONTRACT. The legacy page read a pre-checked
    // notify checkbox when it raised its created event, but the mail subsystem that switch drove is
    // excluded from this migration, and the owning service contract states the switch is dropped with it.

    // THE RECOVERY QUESTION AND ANSWER ARE NOT MEMBERS OF THIS CONTRACT either, and the reason is the same
    // shape as the notify flag's.

    // MIGRATION: THE RANDOM-PASSWORD FLAG IS NOT A MEMBER OF THIS CONTRACT. The legacy page took a
    // genuinely separate branch that bypassed both credential inputs and called the controller's generator,
    // which produced a credential of the configured minimum length plus four characters.
}
