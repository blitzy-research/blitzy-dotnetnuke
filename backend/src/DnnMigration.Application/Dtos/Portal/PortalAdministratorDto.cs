namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// One account a portal may designate as its administrator.
/// </summary>
/// <remarks>
/// <para>
/// Backs the <c>cboAdministratorId</c> selector on the Site Settings screen. The legacy screen filled
/// that list at <c>Website/admin/Portal/SiteSettings.ascx.vb:L331-L336</c> by asking the role
/// controller for the members of the portal's own administrator role and adding one entry per member
/// as <c>New ListItem(objUser.FullName, objUser.UserID.ToString)</c>; the chosen value was then
/// written as argument nine of the portal update at <c>:L775</c>.
/// </para>
/// <para>
/// MIGRATION: the candidate list is the members of the portal's ADMINISTRATOR ROLE, matching the
/// legacy list exactly, while the write path guards a WIDER rule - it requires only that the
/// designated account belong to the addressed portal. The two are deliberately not reconciled. The
/// list is an affordance and the server is authoritative, so a narrower list can never let through a
/// designation the server would refuse, whereas widening the list to every portal member would offer
/// accounts the legacy screen never offered.
/// </para>
/// <para>
/// MIGRATION: a screen-shaped projection rather than a reuse of the role-membership contract. That
/// contract carries the assignment's own key and its effective and expiry bounds, none of which bears
/// on choosing an administrator, and publishing them here would advertise a membership window this
/// resource neither reads nor writes.
/// </para>
/// </remarks>
public sealed class PortalAdministratorDto
{
    /// <summary>
    /// Gets or sets the account identifier, which is the value the selector submits.
    /// </summary>
    /// <remarks>
    /// The legacy list item's value, and the value written to <c>Portals.AdministratorId</c>. The user
    /// identity column seeds from one (<c>01.00.00.SqlDataProvider:L98</c>), so every value this can
    /// hold is a real account and absence must not be inferred from any number.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the account's login name.
    /// </summary>
    /// <remarks>
    /// MIGRATION: NOT part of the legacy list item, and added deliberately. The legacy entry carried
    /// the display name alone, so two administrators sharing a display name were indistinguishable in
    /// the selector - and the display name is the one account field a tenant may compose from a
    /// format string, which makes collisions likelier rather than merely possible. The login name is
    /// unique within a portal, so it disambiguates them; presenting it is the consumer's choice.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account's display name, which is the text the legacy selector showed.
    /// </summary>
    /// <remarks>
    /// The terminal membership statement projects <c>U.DisplayName As FullName</c>, which is the
    /// member the legacy list item was built from.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;
}
