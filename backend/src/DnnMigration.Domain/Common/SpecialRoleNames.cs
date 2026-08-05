namespace DnnMigration.Domain.Common;

/// <summary>
/// The two built-in role names whose values carry authorization meaning rather than being data an
/// installation chooses.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: these replace the excluded static constants <c>glbRoleAllUsersName</c> and
/// <c>glbRoleUnauthUserName</c>, declared at <c>Library/Components/Shared/Globals.vb:L100</c> and
/// <c>:L102</c>. AAP section 0.2.2.1 records the replacement for the excluded <c>Globals</c> module as
/// "the unauthenticated-role name becomes a domain constant", and AAP section 0.7.6 repeats it; this
/// file is that constant, and its partner, in the layer the plan names.
/// </para>
/// <para>
/// <strong>THEY ARE CONSTANTS AND NOT SETTINGS, AND THE DIFFERENCE IS AN AUTHORIZATION BOUNDARY.</strong>
/// An earlier revision exposed both as writable properties on the Application layer's
/// <c>PortalOptions</c>, bound from <c>Portal:AllUsersRoleName</c> and
/// <c>Portal:UnauthenticatedRoleName</c>. That made a deploy-time string decide which stored role
/// receives every-caller semantics and which receives anonymous-caller semantics: editing one setting
/// silently moved those semantics onto a different row of the <c>Roles</c> table, so a role an
/// administrator created for an ordinary purpose could be granted the reach of "All Users" without any
/// permission grant changing. Access-control meaning does not belong in configuration, and expressing it
/// here removes the possibility rather than validating against it.
/// </para>
/// <para>
/// The values are the legacy values, byte for byte, because they are matched against the
/// <c>Roles.RoleName</c> column as strings - see the comparison at <c>PortalSecurity.vb:L124</c> and
/// <c>:L125</c> and the role-name-to-identifier switch that reads both arms at
/// <c>PortalController.vb:L555</c> and <c>:L557</c>, <c>ModuleController.vb:L354</c> and <c>:L356</c>,
/// and <c>TabController.vb:L901</c> and <c>:L903</c>. Changing either string here would stop matching
/// the row a DotNetNuke installation already holds, which is exactly why it is a source change under
/// review and not a deployment knob.
/// </para>
/// <para>
/// An installation that renamed one of these rows is not silently accommodated. That was the earlier
/// justification for configurability, and it is answered instead by the fact that renaming them is not
/// something DotNetNuke supported either: both names are written by the installation scripts and read
/// back by string comparison throughout the legacy product, so a rename already broke the legacy
/// product's own permission checks. Preserving legacy behaviour faithfully therefore means preserving
/// the constant, not making the constant editable.
/// </para>
/// </remarks>
public static class SpecialRoleNames
{
    /// <summary>
    /// Display name of the built-in role standing for every caller, authenticated or not: <c>All Users</c>.
    /// </summary>
    /// <remarks>
    /// Legacy <c>glbRoleAllUsersName</c>. Read by in-scope legacy code at
    /// <c>PortalController.vb:L555</c>, <c>ModuleController.vb:L354</c>, <c>TabController.vb:L901</c>,
    /// <c>UserInfo.vb:L330</c> and <c>PortalSecurity.vb:L125</c>.
    /// </remarks>
    public const string AllUsers = "All Users";

    /// <summary>
    /// Display name of the built-in role standing for callers who are not authenticated:
    /// <c>Unauthenticated Users</c>.
    /// </summary>
    /// <remarks>
    /// Legacy <c>glbRoleUnauthUserName</c>. Read by in-scope legacy code at
    /// <c>PortalController.vb:L557</c>, <c>ModuleController.vb:L356</c>, <c>TabController.vb:L903</c>
    /// and <c>PortalSecurity.vb:L108</c> and <c>:L124</c>.
    /// </remarks>
    public const string Unauthenticated = "Unauthenticated Users";

    /// <summary>
    /// Width of the <c>Roles.RoleName</c> column, which both names above fit inside.
    /// </summary>
    /// <remarks>
    /// Measured as <c>[RoleName] [nvarchar] (50) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L117</c> and carried
    /// forward unchanged by both table rebuilds (<c>01.00.04:L1324</c> and <c>01.00.05:L2750</c>), with no
    /// later altering statement in any of the 88 scripts. Published here because the names above are
    /// matched against that column and a caller composing or validating a role name needs the same bound.
    /// </remarks>
    public const int RoleNameMaximumLength = 50;
}
