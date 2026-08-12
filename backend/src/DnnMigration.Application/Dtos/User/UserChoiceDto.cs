namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One selectable account as an account picker renders it: the key to submit, and the two values that
/// caption it.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this is the payload of <c>cboUsers</c>, the account drop-down on
/// <c>Website/admin/Security/securityroles.ascx</c>. <c>UserModuleBase.vb:L178-L186</c> filled that control
/// from a tenant-wide account read and bound exactly three values to it - the identifier behind each
/// option, the display name it showed, and the login name shown in brackets beside it. Nothing else on that
/// control was read, and nothing else travels here.
/// </para>
/// <para>
/// ⚠ THIS IS A DELIBERATELY NARROWER CONTRACT THAN <see cref="UserListItemDto"/> AND MUST STAY THAT WAY. A
/// performance and privacy review measured the role-assignment screen filling its picker from the account
/// listing, whose row carries a postal address, a telephone number, an electronic-mail address, a creation
/// instant, a last-login instant and the approval, lockout, online and super-user flags. Those fields left
/// the database, crossed the wire and sat in browser memory so that three of them could be rendered - and
/// on a tenant the picker is permitted to enumerate, that is up to a thousand accounts' worth of personal
/// detail transferred to draw a drop-down. Being authorised to read the account grid does not make it right
/// to receive fields the asking screen has no use for; RFC-shaped minimisation is the rule, and this type
/// is where it is enforced.
/// </para>
/// <para>
/// ⚠ DO NOT ADD A MEMBER TO THIS TYPE. A screen that needs more than a key and a caption is not choosing an
/// account, it is reading one, and <c>GET /api/v1/users/{userId}</c> is the address for that. Widening this
/// type would re-open the exposure it was created to close, and would do so silently, because every existing
/// caller would keep compiling.
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
    /// Presence is decided by the envelope containing the row, never by inspecting this value.
    /// <para>
    /// IDENTIFIER TRAP: <c>Users.UserID</c> seeds at one, but the surrounding tables do not -
    /// <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> and <c>Roles.RoleID</c> is
    /// <c>IDENTITY (0, 1)</c> - so neither <c>id &lt;= 0</c> nor <c>id == -1</c> is a valid absence test
    /// anywhere in this schema, and none may be written against this member either. Absence is expressed by
    /// an empty page, never by a magic number.
    /// </para>
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// Shown beside <see cref="DisplayName"/> in the option's caption, because two accounts may share a
    /// display name and the login name is what tells them apart. That is why the legacy caption carried
    /// both.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical display name, from <c>Users.DisplayName</c>
    /// (<c>nvarchar(128) NOT NULL</c> defaulting to the empty string - which is why that is the value this
    /// member carries when unset).
    /// </summary>
    /// <remarks>
    /// THE EMPTY STRING IS A CONFORMING VALUE AND IS NOT ABSENCE. The legacy absent-string sentinel is
    /// <c>""</c> literally (<c>Library/Components/Shared/Null.vb:L71-L75</c>), so an account whose display
    /// name was never set has an empty one rather than a missing one. A client captioning an option falls
    /// back to <see cref="Username"/>; it does not treat the row as unusable.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;
}
