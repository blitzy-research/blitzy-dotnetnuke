namespace DnnMigration.Domain.Enums;

/// <summary>
/// The banner-advertising mode of a DotNetNuke portal: no banners at all, the portal's own, or host-managed
/// advertising.
/// </summary>
/// <remarks>
/// <para>
/// These ordinals are the values stored in the <c>Portals.BannerAdvertising</c> column, so they must never
/// be renumbered or reordered; either change would silently reinterpret existing rows. The column's
/// terminal state is <c>int NOT NULL</c> defaulting to zero, which is why no absent-value member appears:
/// there is no null case to represent.
/// </para>
/// <para>
/// No attribute is declared here: the wire contract belongs to the Application-layer DTOs, the column
/// binding to the Infrastructure entity configuration, and the display labels to the client templates.
/// </para>
/// </remarks>
public enum BannerAdvertisingMode
{
    /// <summary>
    /// Banner advertising is disabled for the portal. This is the database default for the column and a
    /// genuine mode in its own right, not a marker for a missing value.
    /// </summary>
    None = 0,

    /// <summary>The portal serves its own banners, administered at portal level.</summary>
    Site = 1,

    /// <summary>Banner advertising is managed by the host rather than by the portal administrator.</summary>
    Host = 2
}
