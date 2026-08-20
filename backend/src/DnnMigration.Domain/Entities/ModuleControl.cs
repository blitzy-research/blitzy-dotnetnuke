using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// Legacy type DotNetNuke.Entities.Modules.ModuleControlInfo becomes this entity.

/// <summary>
/// One user-interface entry point that a module definition publishes - the row an administrator picks when
/// choosing which view, edit or settings surface of a module to reach.
/// </summary>
/// <remarks>
/// <b>Terminal mapping contract for <c>ModuleControlConfiguration</c>.</b> The legacy schema is immutable
/// for this migration, so the table below is the measured terminal state of <c>dbo.ModuleControls</c> after
/// all eighty-eight upgrade scripts in <c>Website/Providers/DataProviders/SqlDataProvider/</c> have been
/// replayed in order - not the table as first created, which is missing two columns and gets a third one's
/// width wrong.
/// </remarks>
public sealed class ModuleControl : Entity<int>
{
    /// <summary>Gets or sets the installation-wide identity of this control row.</summary>
    /// <value>The value of the <c>ModuleControlID</c> column: a database-generated identity seeded at 1.</value>
    /// <remarks>
    /// The property keeps the column's meaning while spelling the suffix as <c>Id</c>, which is the casing
    /// the rest of the target uses; the column name itself is unchanged, and
    /// <c>ModuleControlConfiguration</c> binds this property to <c>ModuleControlID</c> explicitly and names
    /// it in its key declaration.
    /// </remarks>
    public int ModuleControlId { get; set; }

    /// <summary>Gets the value that identifies this entity for the purposes of equality.</summary>
    /// <value>The value of <see cref="ModuleControlId"/>.</value>
    public override int Identity => ModuleControlId;

    /// <summary>
    /// Gets or sets the identity of the module definition that publishes this control, or <see
    /// langword="null"/> when the control belongs to no definition.
    /// </summary>
    /// <value>
    /// The value of the nullable <c>ModuleDefID</c> column: the <c>ModuleDefID</c> of the owning <see
    /// cref="Entities.ModuleDefinition"/>, or <see langword="null"/> for a host-level control.
    /// </value>
    /// <remarks>
    /// The foreign key introduced by the 02.00.00 split, and the property that makes this entity the
    /// dependent half of the definition-to-control relationship.
    /// </remarks>
    public int? ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the key that selects this control among the ones its definition publishes, such as
    /// <c>Edit</c> or <c>Settings</c>.
    /// </summary>
    /// <value>The value of the <c>ControlKey</c> column.</value>
    /// <remarks>
    /// One of the three columns in the unique index, so a definition may publish the same key from a
    /// different source, and the same source under a different key, but not the same pair twice.
    /// </remarks>
    public string? ControlKey { get; set; }

    /// <summary>Gets or sets the title presented for this control, such as <c>Account Logout</c>.</summary>
    /// <value>The value of the <c>ControlTitle</c> column.</value>
    /// <remarks>
    /// MIGRATION: absent is <see langword="null"/> rather than the empty string the legacy sentinel
    /// produced. The wording itself is data and is passed through untouched - the localisation mechanism
    /// the legacy screens used is not ported, so nothing here rewrites, trims or defaults it.
    /// </remarks>
    public string? ControlTitle { get; set; }

    /// <summary>
    /// Gets or sets the source path recorded for this control, such as
    /// <c>Admin/Authentication/Logoff.ascx</c>.
    /// </summary>
    /// <value>The value of the <c>ControlSrc</c> column.</value>
    /// <remarks>
    /// A legacy Web Forms control path, stored and returned verbatim. It is data to this migration and
    /// nothing more: the target never resolves, loads or validates it, because the presentation layer is
    /// Angular and the Web Forms surface is out of scope.
    /// </remarks>
    public string? ControlSrc { get; set; }

    /// <summary>Gets or sets the path of the icon shown for this control.</summary>
    /// <value>The value of the <c>IconFile</c> column.</value>
    /// <remarks>
    /// Absent is <see langword="null"/> rather than the empty string the legacy sentinel produced, and the
    /// value is passed through unresolved - turning a stored relative path into a reachable asset is a
    /// presentation concern.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets the persisted access-level ordinal that says how privileged a caller must be to reach
    /// this control.
    /// </summary>
    /// <value>
    /// The value of the <c>ControlType</c> column: a required integer, stored and returned exactly as the
    /// database holds it.
    /// </value>
    /// <remarks>
    /// Consequently no value here is reserved, checked or interpreted, and none may be. Code that needs to
    /// act on the ordinal belongs in the layer that owns authorisation, which can map it deliberately and
    /// in one place; putting a named-member contract in front of it here would misrepresent a column the
    /// database is free to fill with any integer.
    /// </remarks>
    public int ControlType { get; set; }

    /// <summary>
    /// Gets or sets the ordinal that sequences the controls of one definition, or <see langword="null"/>
    /// when no order was recorded.
    /// </summary>
    /// <value>The value of the nullable <c>ViewOrder</c> column.</value>
    public int? ViewOrder { get; set; }

    /// <summary>Gets or sets the address of the help document offered beside this control.</summary>
    /// <value>The value of the <c>HelpUrl</c> column.</value>
    /// <remarks>
    /// Named with the idiomatic casing, which is also the casing of the SQL identifier added by
    /// <c>02.02.00.SqlDataProvider</c> line 455; the legacy VB property was the only place that spelled the
    /// suffix in capitals, and the column binding in <c>ModuleControlConfiguration</c> is an identity
    /// mapping either way.
    /// </remarks>
    public string? HelpUrl { get; set; }

    /// <summary>Gets or sets whether the legacy control declared support for partial page rendering.</summary>
    /// <value>
    /// The value of the <c>SupportsPartialRendering</c> column: required, defaulting to <see
    /// langword="false"/>.
    /// </value>
    /// <remarks>
    /// <see langword="false"/> here is an ordinary Boolean default and not a domain sentinel. The column is
    /// <c>NOT NULL</c> with a default of <c>0</c>, and true is genuine data -
    /// <c>04.05.00.SqlDataProvider</c> line 1832 sets it for one shipped control immediately after adding
    /// the column.
    /// </remarks>
    public bool SupportsPartialRendering { get; set; }

    /// <summary>Gets or sets the module definition that publishes this control.</summary>
    /// <value>
    /// The owning <see cref="Entities.ModuleDefinition"/>, or <see langword="null"/> when <see
    /// cref="ModuleDefinitionId"/> is <see langword="null"/> or the relationship has not been loaded.
    /// </value>
    public ModuleDefinition? ModuleDefinition { get; set; }
}
