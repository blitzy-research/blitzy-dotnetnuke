namespace DnnMigration.Domain.Enums;

/// <summary>
/// The banner-advertising mode of a DotNetNuke portal: whether the portal shows no
/// banners at all, serves its own, or defers to host-managed advertising.
/// </summary>
/// <remarks>
/// <para>
/// Persistence contract. These ordinals are the values stored in the
/// <c>Portals.BannerAdvertising</c> column, so they must never be renumbered and never
/// be reordered; either change would silently reinterpret every existing row. The
/// column is created nullable by the baseline script
/// (Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L85),
/// tightened during the table rebuild in 01.00.05.SqlDataProvider:L1373, and
/// re-asserted in qualifier-templated form in 03.01.01.SqlDataProvider:L1117, where
/// L1127 also gives it a database default of zero. Its terminal state is therefore
/// <c>int NOT NULL</c> defaulting to zero, which is why no absent-value member appears
/// below: there is no null case to represent.
/// </para>
/// <para>
/// Provenance. The legacy codebase declared no enumeration for this setting; it
/// exposed the raw discriminator as a bare <c>Integer</c>
/// (Library/Components/Portal/PortalInfo.vb:L39 backing field, L133 property). The
/// member set was therefore recovered from the only place the legacy system ever named
/// these values: the administration option list at
/// Website/admin/Portal/sitesettings.ascx:L123-L125, whose three items are labelled
/// None, Site and Host for the values 0, 1 and 2 respectively. Note the lower-case
/// markup filename; the PascalCase code-behind beside it is a different file.
/// </para>
/// <para>
/// Declaration order is load-bearing as well as the values themselves. The legacy
/// administration screen assigned the persisted integer straight to the option list's
/// zero-based selected index (Website/admin/Portal/SiteSettings.ascx.vb:L291) and wrote
/// that same index straight back on save (L774). Each member's value is consequently
/// also its declaration position, and the two must not be allowed to drift apart.
/// </para>
/// <para>
/// Deliberately absent. This type carries no attributes: the legacy XML serialisation
/// decoration on the originating property is dropped, the wire contract belongs to the
/// Application-layer DTOs, the column binding belongs to the Infrastructure entity
/// configuration, and the display labels belong to the Angular templates. Nothing is
/// declared here beyond the three members below.
/// </para>
/// </remarks>
// MIGRATION: the legacy representation was a bare Integer (PortalInfo.vb:L39 field,
//   L133 property) with NO enum type. The three named members are derived from the admin
//   option list at Website/admin/Portal/sitesettings.ascx:L123-L125 (lower-case filename).
//   The magic literal 2 at SiteSettings.ascx.vb:L295 (optBanners.Enabled =
//   objPortal.BannerAdvertising <> 2) becomes BannerAdvertisingMode.Host in the
//   Application layer. Ordinals are persisted in Portals.BannerAdvertising and must
//   never be renumbered or reordered.
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
    /// Banner advertising is managed by the host rather than by the portal
    /// administrator.
    /// </summary>
    /// <remarks>
    /// This member carries a live legacy business rule. At
    /// Website/admin/Portal/SiteSettings.ascx.vb:L295 the administration screen
    /// evaluates <c>optBanners.Enabled = objPortal.BannerAdvertising &lt;&gt; 2</c>,
    /// disabling portal-level banner editing whenever the stored mode is this one, and
    /// the line after it makes a companion explanatory label visible for the same mode.
    /// Both checks are taken only when the caller is not a super user, because a super
    /// user is the host and edits the setting freely. That rule is preserved as a
    /// comparison against <see cref="Host"/> in the Application layer
    /// (DnnMigration.Application.Services.PortalService) and in the Angular
    /// portal-settings feature. It is deliberately not implemented in this file, which
    /// declares an enumeration and nothing more.
    /// </remarks>
    Host = 2
}
