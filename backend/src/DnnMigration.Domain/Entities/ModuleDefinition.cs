using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One published definition within an installed module package: the unit a portal actually places on a
/// page, and the unit that owns the controls, permissions and profile properties belonging to that
/// placement.
/// </summary>
/// <remarks>
/// <b>Terminal mapping contract for <c>ModuleDefinitionConfiguration</c>.</b> The legacy schema is
/// immutable for this migration, so the table below is the measured terminal state of
/// <c>dbo.ModuleDefinitions</c> after all eighty-eight upgrade scripts in
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c> have been replayed in order - not the baseline,
/// which carries seven columns of which only two survive.
/// </remarks>
public sealed class ModuleDefinition : Entity<int>
{
    /// <summary>Gets or sets the installation-wide identity of this module definition.</summary>
    /// <value>The value of the <c>ModuleDefID</c> column: a database-generated identity seeded at 1.</value>
    /// <remarks>
    /// The property keeps the legacy column's meaning while spelling the name in full and the suffix as
    /// <c>Id</c>, which is the casing the rest of the target uses; the column name itself is unchanged, and
    /// <c>ModuleDefinitionConfiguration</c> binds this property to <c>ModuleDefID</c> explicitly with
    /// <c>HasColumnName</c> and names it in its <c>HasKey</c> call.
    /// </remarks>
    public int ModuleDefinitionId { get; set; }

    /// <summary>Gets the value that identifies this entity for the purposes of equality.</summary>
    /// <value>The value of <see cref="ModuleDefinitionId"/>.</value>
    /// <remarks>
    /// Required by <see cref="Entity{TId}"/> and used for equality alone.
    /// </remarks>
    public override int Identity => ModuleDefinitionId;

    /// <summary>
    /// Gets or sets the display name presented to administrators when they choose which definition to place
    /// - such as <c>Announcements</c>.
    /// </summary>
    /// <value>The value of the <c>FriendlyName</c> column.</value>
    public string FriendlyName { get; set; }

    /// <summary>Gets or sets the identity of the installed package that publishes this definition.</summary>
    /// <value>
    /// The value of the <c>DesktopModuleID</c> column: the <c>DesktopModuleID</c> of the owning <see
    /// cref="Entities.DesktopModule"/>.
    /// </value>
    /// <remarks>
    /// The foreign key that the 02.00.00 split introduced, and the property that makes this entity the
    /// dependent half of the package-to-definition relationship. <c>ModuleDefinitionConfiguration</c> binds
    /// it to <c>DesktopModuleID</c> with <c>HasColumnName</c> and uses it as the foreign key behind <see
    /// cref="Entities.DesktopModule"/>, describing the relationship's <c>ON DELETE CASCADE</c> rule as the
    /// existing schema declares it rather than softening it: deleting a package already removes its
    /// definitions in the legacy database.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    /// <summary>
    /// Gets or sets the number of seconds a placed instance of this definition may serve cached output by
    /// default - the legacy "Cache Time (secs):" setting.
    /// </summary>
    /// <value>The value of the <c>DefaultCacheTime</c> column.</value>
    /// <remarks>
    /// The default of zero needs no code. The column is <c>NOT NULL</c> with a database default of
    /// <c>0</c>, and a freshly constructed <see cref="int"/> is already zero, so the two agree without a
    /// constructor, an initialiser or a nullable wrapper - which is exactly why the legacy constructor that
    /// assigned zero explicitly could be deleted rather than translated.
    /// </remarks>
    public int DefaultCacheTime { get; set; }

    /// <summary>Gets or sets the installed package that publishes this definition.</summary>
    /// <value>
    /// The <see cref="Entities.DesktopModule"/> whose <c>DesktopModuleID</c> equals <see
    /// cref="DesktopModuleId"/>, or <see langword="null"/> when the reference has not been loaded.
    /// </value>
    /// <remarks>
    /// The inverse of <see cref="Entities.DesktopModule.ModuleDefinitions"/>, and the replacement for the
    /// flattened join that the legacy model used instead of a reference.
    /// </remarks>
    public DesktopModule? DesktopModule { get; set; }

    /// <summary>Gets or sets the user-interface controls this definition publishes.</summary>
    /// <value>The control rows whose <c>ModuleDefID</c> refers to this row.</value>
    /// <remarks>
    /// A real relationship rather than an inferred one: <c>ModuleControls.ModuleDefID</c> is constrained by
    /// <c>FK_ModuleControls_ModuleDefinitions</c>, declared <c>ON DELETE CASCADE</c> in <c>02.00.00</c>
    /// line 5026, and the same script constrains the triple <c>(ModuleDefID, ControlKey, ControlSrc)</c> to
    /// be unique through <c>IX_ModuleControls</c> at line 5017 - so one definition never publishes the same
    /// key and source twice.
    /// </remarks>
    public ICollection<ModuleControl> ModuleControls { get; set; } = [];

    /// <summary>Gets or sets the placed module instances created from this definition.</summary>
    /// <value>The module rows whose <c>ModuleDefID</c> refers to this row.</value>
    /// <remarks>
    /// The oldest relationship on this table and the reason it exists: <c>FK_Modules_ModuleDefinitions</c>
    /// is declared in the baseline schema at <c>01.00.00</c> line 674, likewise <c>ON DELETE CASCADE</c>. A
    /// definition describes what may be placed; each row here is one actual placement, carrying its own
    /// title, permissions and configured cache timeout.
    /// </remarks>
    public ICollection<Module> Modules { get; set; } = [];

    // THERE IS DELIBERATELY NO Permissions COLLECTION ON THIS ENTITY. The catalogue rows in dbo.Permission
    // do carry a ModuleDefID, and the legacy code did cascade by hand - the terminal DeleteModuleDefinition
    // procedure (04.06.00:L507) runs "DELETE FROM Permission WHERE moduledefid = @ModuleDefId" under the
    // comment "delete custom permissions" before deleting the definition row - but that association must
    // not be modelled as a relationship, and the reason is arithmetic rather than stylistic.

    /// <summary>Gets or sets the user-profile properties this definition contributes.</summary>
    /// <value>The profile-property definition rows whose <c>ModuleDefID</c> refers to this row.</value>
    /// <remarks>
    /// The mechanism by which a module definition extends a portal's user profile with properties of its
    /// own. <c>dbo.ProfilePropertyDefinition</c> declares <c>ModuleDefID</c> as <c>int NULL</c>
    /// (<c>04.00.04</c> line 1111, first created by <c>03.02.03</c> line 1066) and includes it in the
    /// unique index over <c>(PortalID, ModuleDefID, PropertyName)</c> at line 1127, so a property name is
    /// unique per portal per contributing definition.
    /// </remarks>
    public ICollection<ProfilePropertyDefinition> ProfilePropertyDefinitions { get; set; } = [];
}
