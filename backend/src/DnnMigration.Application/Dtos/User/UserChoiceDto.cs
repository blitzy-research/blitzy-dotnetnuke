namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One selectable account as an account picker renders it: the key to submit, and the two values that
/// caption it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THIS IS A DELIBERATELY NARROWER CONTRACT THAN <see cref="UserListItemDto"/> AND MUST STAY THAT WAY. A
/// performance and privacy review measured the role-assignment screen filling its picker from the account
/// listing, whose row carries a postal address, a telephone number, an electronic-mail address, a creation
/// instant, a last-login instant and the approval, lockout, online and super-user flags.
/// </para>
/// <para>
/// No portal identifier is carried, unlike <see cref="UserListItemDto"/>. The endpoint is tenant-scoped by
/// the resolved request context, so every row in a page belongs to the same portal by construction and
/// repeating that fact per row would be noise a client could disagree with.
/// </para>
/// </remarks>
public sealed class UserChoiceDto
{
    /// <summary>
    /// Gets or sets the account's key, from <c>Users.UserID</c> (<c>int IDENTITY (1, 1) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// IDENTIFIER TRAP: <c>Users.UserID</c> seeds at one, but the surrounding tables do not -
    /// <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> and <c>Roles.RoleID</c> is <c>IDENTITY (0, 1)</c>
    /// - so neither <c>id &lt;= 0</c> nor <c>id == -1</c> is a valid absence test anywhere in this schema,
    /// and none may be written against this member either.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical display name, from <c>Users.DisplayName</c> (<c>nvarchar(128) NOT
    /// NULL</c> defaulting to the empty string - which is why that is the value this member carries when
    /// unset).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
}
