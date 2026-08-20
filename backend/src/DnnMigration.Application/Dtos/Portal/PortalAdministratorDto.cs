namespace DnnMigration.Application.Dtos.Portal;

/// <summary>One account a portal may designate as its administrator.</summary>
public sealed class PortalAdministratorDto
{
    /// <summary>Gets or sets the account identifier, which is the value the selector submits.</summary>
    /// <remarks>
    /// The legacy list item's value, and the value written to <c>Portals.AdministratorId</c>. The user
    /// identity column seeds from one (<c>01.00.00.SqlDataProvider:L98</c>), so every value this can hold
    /// is a real account and absence must not be inferred from any number.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the account's login name.</summary>
    /// <remarks>
    /// NOT part of the legacy list item, and added deliberately. The legacy entry carried the display name
    /// alone, so two administrators sharing a display name were indistinguishable in the selector - and the
    /// display name is the one account field a tenant may compose from a format string, which makes
    /// collisions likelier rather than merely possible.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the account's display name, which is the text the legacy selector showed.</summary>
    public string DisplayName { get; set; } = string.Empty;
}
