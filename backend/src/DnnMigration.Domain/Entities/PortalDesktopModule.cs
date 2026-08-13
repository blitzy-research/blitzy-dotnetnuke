using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One grant entitling a portal to use an installed module package: a row of the join table between <see
/// cref="Entities.Portal"/> and <see cref="Entities.DesktopModule"/>.
/// </summary>
/// <remarks>
/// <para>
/// The entity carries no attribute and takes no dependency, so nothing here states how it is stored. Table
/// and column naming, the identity column, the unique pair constraint and both cascading relationships are
/// bound explicitly by <c>PortalDesktopModuleConfiguration</c> in the Infrastructure layer, because the
/// legacy installation runs with an empty object qualifier and <c>dbo</c> as its database owner.
/// </para>
/// <para>
/// Two obligations belong to the type as a whole rather than to any single member, and both are statements
/// about schema that already exists - they are honoured, never invented. <c>IX_PortalDesktopModules</c>
/// (02.02.02 line 3044) is <c>UNIQUE NONCLUSTERED</c> over <c>(PortalID, DesktopModuleID)</c> in that
/// column order, so one portal can never be granted the same package twice and the Infrastructure
/// configuration must declare the index under its legacy name; and both relationships cascade exactly as
/// the schema has them.
/// </para>
/// </remarks>
public sealed class PortalDesktopModule : Entity<int>
{
    /// <summary>
    /// Gets or sets the <c>PortalDesktopModuleID</c> column: <c>int NOT NULL IDENTITY (1, 1)</c>, the
    /// clustered primary key named <c>PK_PortalDesktopModules</c>.
    /// </summary>
    /// <remarks>
    /// The database assigns this value, so the Infrastructure configuration must map it as generated on add
    /// with a seed and increment of one; the <c>AddPortalDesktopModule</c> procedure at 02.02.02 line 3111
    /// confirms as much by returning <c>SCOPE_IDENTITY()</c> after its insert.
    /// </remarks>
    public int PortalDesktopModuleId { get; set; }

    /// <summary>Gets the value equality is based on, which is always <see cref="PortalDesktopModuleId"/>.</summary>
    /// <remarks>
    /// Not a column and not part of the natural key, so the entity configuration must ignore it or the
    /// model builder discovers it by convention and the provider then fails on a column the table does not
    /// have.
    /// </remarks>
    public override int Identity => PortalDesktopModuleId;

    /// <summary>
    /// Gets or sets the <c>PortalID</c> column: <c>int NOT NULL</c>, the portal being granted the
    /// entitlement.
    /// </summary>
    /// <remarks>
    /// Required, and required at the database rather than only in code: the column is <c>NOT NULL</c> and
    /// <c>FK_PortalDesktopModules_Portals</c> constrains it to <c>dbo.Portals.PortalID</c>.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the <c>DesktopModuleID</c> column: <c>int NOT NULL</c>, the installed module package
    /// the entitlement covers.
    /// </summary>
    /// <remarks>
    /// Required, and required at the database rather than only in code: the column is <c>NOT NULL</c> and
    /// <c>FK_PortalDesktopModules_DesktopModules</c> constrains it to
    /// <c>dbo.DesktopModules.DesktopModuleID</c>. Read this member, not <see cref="DesktopModule"/>, when
    /// only the package's identity is needed.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    // The two references below replace the flattened join that the legacy model used in place of a
    // relationship.

    /// <summary>
    /// Gets or sets the portal granted the entitlement - the principal identified by <see
    /// cref="PortalId"/>, and the inverse of <see cref="Entities.Portal.PortalDesktopModules"/>.
    /// </summary>
    public Portal Portal { get; set; }

    /// <summary>
    /// Gets or sets the installed module package the entitlement covers - the principal identified by <see
    /// cref="DesktopModuleId"/>, and the inverse of <see
    /// cref="Entities.DesktopModule.PortalDesktopModules"/>.
    /// </summary>
    public DesktopModule DesktopModule { get; set; }
}
