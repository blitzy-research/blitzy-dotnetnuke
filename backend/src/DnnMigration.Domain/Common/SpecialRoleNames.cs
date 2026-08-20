namespace DnnMigration.Domain.Common;

/// <summary>
/// The two built-in role names whose values carry authorization meaning rather than being data an
/// installation chooses.
/// </summary>
public static class SpecialRoleNames
{
    /// <summary>
    /// Display name of the built-in role standing for every caller, authenticated or not: <c>All Users</c>.
    /// </summary>
    public const string AllUsers = "All Users";

    /// <summary>
    /// Display name of the built-in role standing for callers who are not authenticated: <c>Unauthenticated
    /// Users</c>.
    /// </summary>
    public const string Unauthenticated = "Unauthenticated Users";

    /// <summary>Width of the <c>Roles.RoleName</c> column, which both names above fit inside.</summary>
    /// <remarks>
    /// Measured as <c>[RoleName] [nvarchar] (50) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L117</c> and carried
    /// forward unchanged by both table rebuilds (<c>01.00.04:L1324</c> and <c>01.00.05:L2750</c>), with no
    /// later altering statement in any of the 88 scripts.
    /// </remarks>
    public const int RoleNameMaximumLength = 50;
}
