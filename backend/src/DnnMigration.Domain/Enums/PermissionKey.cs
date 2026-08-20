namespace DnnMigration.Domain.Enums;

/// <summary>
/// The DotNetNuke permission keys: the discrete access rights that a row in the <c>Permission</c> catalogue
/// table can name. Each member's <i>name</i> is the value persisted in the database and carried over the
/// wire.
/// </summary>
/// <remarks>
/// <para>
/// THE MEMBER NAME IS THE CONTRACT. <c>Permission.PermissionKey</c> is <c>varchar(50) NOT NULL</c> in the
/// terminal schema — widened from the <c>varchar(20)</c> baseline by
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.06.00.SqlDataProvider</c> L398, which is also the
/// width its procedures accept from L407 onward. Members are spelled in upper case exactly as the stored
/// literals are, so <c>nameof(PermissionKey.VIEW)</c> yields <c>"VIEW"</c> with no casing step in between;
/// these names are data a production database already holds, so never rename and never re-case them.
/// </para>
/// <para>
/// THE ORDINAL IS MEANINGLESS. No number for this concept is stored anywhere, so no member carries an
/// explicit value and the Infrastructure mapping converts by name over the <c>varchar(50)</c> column, never
/// to an integer. These members are not bit flags either: a <c>Permission</c> row names exactly one key, so
/// they must never be combined.
/// </para>
/// </remarks>
public enum PermissionKey
{
    /// <summary>
    /// Permission to view a module or a tab. One of the two keys used by the module and tab permission
    /// triads, and the key whose inheritance is special-cased when a module defers its view rights to its
    /// tab.
    /// </summary>
    VIEW,

    /// <summary>
    /// Permission to edit a module or a tab, which in DotNetNuke also denotes administrative control of the
    /// item.
    /// </summary>
    EDIT,

    /// <summary>
    /// Permission to read a folder. Seeded against the <c>SYSTEM_FOLDER</c> scope by the upgrade scripts at
    /// <c>03.00.11:L15</c>, <c>03.02.04:L28</c>, <c>03.03.03:L31</c> and <c>04.00.04:L2584</c>.
    /// </summary>
    READ,

    /// <summary>
    /// Permission to write to a folder. Seeded against the <c>SYSTEM_FOLDER</c> scope by the upgrade
    /// scripts at <c>03.02.04:L33</c>, <c>03.03.03:L36</c> and <c>04.00.04:L2589</c>.
    /// </summary>
    WRITE
}
