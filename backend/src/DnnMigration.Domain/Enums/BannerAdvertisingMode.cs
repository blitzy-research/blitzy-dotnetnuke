namespace DnnMigration.Domain.Enums;

/// <summary>
/// The banner-advertising mode of a DotNetNuke portal: no banners at all, the portal's own, or
/// host-managed advertising.
/// </summary>
/// <remarks>
/// <para>
/// These ordinals are the values stored in the <c>Portals.BannerAdvertising</c> column, so they
/// must never be renumbered or reordered; either change would silently reinterpret existing rows.
/// The column's terminal state is <c>int NOT NULL</c> defaulting to zero, which is why no
/// absent-value member appears: there is no null case to represent.
/// </para>
/// <para>
/// Declaration order is load-bearing as well as the values. The legacy administration screen
/// assigned the persisted integer straight to a zero-based option-list selected index
/// (<c>Website/admin/Portal/SiteSettings.ascx.vb</c> L291, written back at L774), so each member's
/// value is also its declaration position and the two must not drift apart.
/// </para>
/// <para>
/// No attribute is declared here: the wire contract belongs to the Application-layer DTOs, the
/// column binding to the Infrastructure entity configuration, and the display labels to the client
/// templates.
/// </para>
/// </remarks>
// MIGRATION: the legacy representation was a bare Integer with no enum type
// (Library/Components/Portal/PortalInfo.vb L39 field, L133 property). The three member names are
// recovered from the only place the legacy system named these values, the administration option
// list at Website/admin/Portal/sitesettings.ascx L123-L125 - note the lower-case markup filename,
// the PascalCase code-behind beside it is a different file.
public enum BannerAdvertisingMode
{
    /// <summary>
    /// Banner advertising is disabled for the portal. This is the database default for
    /// the column and a genuine mode in its own right, not a marker for a missing value.
    /// </summary>
    None = 0,

    /// <summary>
    /// The portal serves its own banners, administered at portal level.
    /// </summary>
    Site = 1,

    /// <summary>
    /// Banner advertising is managed by the host rather than by the portal administrator.
    /// </summary>
    /// <remarks>
    /// This member carries a live legacy business rule that a consumer must reproduce. At
    /// <c>Website/admin/Portal/SiteSettings.ascx.vb</c> L295 the administration screen evaluates
    /// <c>optBanners.Enabled = objPortal.BannerAdvertising &lt;&gt; 2</c>, disabling portal-level
    /// banner editing whenever the stored mode is this one and revealing a companion explanatory
    /// label; both checks are taken only when the caller is not a super user, because a super user is
    /// the host and edits the setting freely. The Application service and the client portal-settings
    /// feature are each obliged to express that rule as a comparison against <see cref="Host"/>. It
    /// is deliberately not implemented here, in a file that declares an enumeration and nothing more.
    /// </remarks>
    Host = 2
}
