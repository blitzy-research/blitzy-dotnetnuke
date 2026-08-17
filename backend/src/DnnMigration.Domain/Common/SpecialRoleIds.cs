namespace DnnMigration.Domain.Common;

/// <summary>
/// The three role identifiers whose values carry authorization meaning rather than naming a row an
/// installation created. They are negative because <c>Roles.RoleID</c> is <c>IDENTITY (0, 1)</c>, so no
/// stored role can ever collide with one of them.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: these are the legacy <c>Globals.vb</c> pseudo-role constants, which were declared there as
/// <em>strings</em> and parsed at every use - <c>glbRoleUnauthUser = "-3"</c>, <c>glbRoleAllUsers =
/// "-1"</c>, <c>glbRoleSuperUser = "-2"</c> at <c>Library/Components/Shared/Globals.vb:L95-L102</c>. The
/// names that accompany them are in <see cref="SpecialRoleNames"/>; only the numbers live here.
/// </para>
/// <para>
/// ⚠ ONE SOURCE OF TRUTH ON PURPOSE. These values are read by the permission evaluator, which decides
/// whether a grant admits a caller, and by the permission administration surface, which decides which rows
/// a grant grid offers. Two private copies of the same number in two layers is exactly the arrangement in
/// which a grid can offer a row the evaluator will never honour, so both layers read these members.
/// </para>
/// </remarks>
public static class SpecialRoleIds
{
    /// <summary>
    /// The role that admits every caller, authenticated or not: <c>-1</c>, named <see
    /// cref="SpecialRoleNames.AllUsers"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ THIS VALUE IS ALSO THE LEGACY ABSENT-INTEGER SENTINEL. <c>Null.NullInteger</c> is <c>-1</c> too, so
    /// a <c>-1</c> in a role position must never be read as "no role"; here it is a real, addressable role
    /// whose grants apply to everybody.
    /// </remarks>
    public const int AllUsers = -1;

    /// <summary>
    /// The role reserved for host accounts: <c>-2</c>. It matches no portal principal, because a host
    /// account's authority is established by its account flag rather than by a grant.
    /// </summary>
    public const int SuperUser = -2;

    /// <summary>
    /// The role that admits only callers who have not signed in: <c>-3</c>, named <see
    /// cref="SpecialRoleNames.Unauthenticated"/>.
    /// </summary>
    public const int Unauthenticated = -3;

    /// <summary>
    /// The value the legacy grid wrote into the role position of a grant made to one named account rather
    /// than to a role: <c>-4</c>, from <c>glbRoleNothing</c>.
    /// </summary>
    /// <remarks>
    /// Not written by this solution. A user-scoped grant carries <c>UserId</c> and leaves <c>RoleId</c>
    /// null, which is what the evaluator tests first; the constant is retained so the legacy value can be
    /// recognised in data an upgraded installation already holds.
    /// </remarks>
    public const int None = -4;

    /// <summary>
    /// Determines whether a role identifier names one of the built-in pseudo-roles rather than a row in
    /// <c>dbo.Roles</c>.
    /// </summary>
    /// <param name="roleId">The identifier to classify.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="roleId"/> is <see cref="AllUsers"/>, <see
    /// cref="SuperUser"/> or <see cref="Unauthenticated"/>; otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsPseudoRole(int roleId) =>
        roleId is AllUsers or SuperUser or Unauthenticated;
}
