namespace DnnMigration.Application.Validation;

/// <summary>The closed sets of field names a caller may name in <c>PagedRequest.SortBy</c>.</summary>
/// <remarks>
/// <b>Closed by default.</b> A name that appears in no set below is rejected at the boundary. The point of
/// an allowlist rather than a pattern is that no caller-supplied text ever reaches an <c>ORDER BY</c>
/// clause: what reaches the repository is a member of a set fixed at compile time, so the ordering clause
/// is composed from constants this assembly owns rather than from anything that arrived on the wire.
/// </remarks>
internal static class SortableFields
{
    /// <summary>Field names that may order the portal listing served by <c>GET /api/v1/portals</c>.</summary>
    /// <remarks>
    /// Projected by <c>PortalListItemDto</c> and backed by columns on <c>Portals</c>: <c>PortalID</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), <c>PortalName</c> (<c>:L79</c>), <c>ExpiryDate</c>
    /// (<c>:L83</c>), <c>HostFee</c> - terminal type <c>money NOT NULL</c> after
    /// <c>03.01.01.SqlDataProvider:L1118</c> - and <c>HostSpace</c> (<c>int NOT NULL</c> after
    /// <c>:L1119</c>).
    /// </remarks>
    // MIGRATION: three members of the projection are deliberately absent. - Users and Pages are COMPUTED
    // COUNTS, not columns.
    internal static readonly IReadOnlySet<string> Portals =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PortalId",
            "PortalName",
            "ExpiryDate",
            "HostFee",
            "HostSpace",
        };

    /// <summary>Field names that may order the role listing served by <c>GET /api/v1/roles</c>.</summary>
    /// <remarks>
    /// Every member of <c>RoleListItemDto</c> is included, because every one is backed by a column on
    /// <c>Roles</c>: <c>RoleID</c> and <c>RoleName</c> (<c>01.00.00.SqlDataProvider:L114</c> onwards),
    /// <c>Description</c>, <c>ServiceFee</c> (<c>money NULL</c>, <c>03.01.01.SqlDataProvider:L1173</c>),
    /// <c>BillingFrequency</c> and <c>TrialFrequency</c> (<c>char(1) NULL</c>), <c>BillingPeriod</c> and
    /// <c>TrialPeriod</c> (<c>int NULL</c>), <c>TrialFee</c>, <c>IsPublic</c> and <c>AutoAssignment</c>
    /// (<c>01.00.08.SqlDataProvider:L6828</c>).
    /// </remarks>
    internal static readonly IReadOnlySet<string> Roles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RoleId",
            "RoleName",
            "Description",
            "ServiceFee",
            "BillingFrequency",
            "BillingPeriod",
            "TrialFee",
            "TrialFrequency",
            "TrialPeriod",
            "IsPublic",
            "AutoAssignment",
        };

    /// <summary>Field names that may order the account listing served by <c>GET /api/v1/users</c>.</summary>
    /// <remarks>
    /// Every member is a mapped column on <c>Users</c> that the listing query reads directly:
    /// <c>UserID</c>, <c>Username</c>, <c>FirstName</c>, <c>LastName</c>, <c>DisplayName</c> and
    /// <c>Email</c>, together with <c>IsSuperUser</c> (<c>01.00.02.SqlDataProvider:L242-L243</c>).
    /// </remarks>
    internal static readonly IReadOnlySet<string> Users =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UserId",
            "Username",
            "FirstName",
            "LastName",
            "DisplayName",
            "Email",
            "IsSuperUser",
        };

    /// <summary>Field names that may order the module listing served by <c>GET /api/v1/modules</c>.</summary>
    /// <remarks>
    /// This set is NET-NEW, and its absence was itself part of the defect: the module listing bound the
    /// shared request contract and was therefore bounded only by the union, so it accepted every portal and
    /// role field name and discarded all of them.
    /// </remarks>
    // MIGRATION: eight members of the projection are deliberately absent. - TabModuleId, TabId,
    // ModuleOrder, AllTabs, Visibility and DisplayTitle belong to the PLACEMENT rather than to the module.
    internal static readonly IReadOnlySet<string> Modules =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ModuleId",
            "ModuleTitle",
            "IsDeleted",
            "StartDate",
            "EndDate",
        };

    /// <summary>
    /// Field names that may order the role-membership listing served by <c>GET
    /// /api/v1/roles/{roleId}/users</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ THREE NAMES WERE WITHDRAWN FROM THIS SET, AND THE COMMENT THAT JUSTIFIED THEM WAS WRONG.
    /// <c>CreatedDate</c>, <c>LastLoginDate</c> and <c>IsApproved</c> were admitted on the reading that
    /// this listing materialises the role's assignments and pages them IN MEMORY, so the three values the
    /// external <c>aspnet_*</c> membership objects supply would be present on every row before a page was
    /// cut.
    /// </remarks>
    internal static readonly IReadOnlySet<string> RoleUsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UserId",
            "Username",
            "FirstName",
            "LastName",
            "DisplayName",
            "Email",
            "IsSuperUser",
        };

    /// <summary>Field names that may order the account picker served by <c>GET /api/v1/users/choices</c>.</summary>
    /// <remarks>
    /// THE NARROWEST SET IN THIS FILE, AND NARROW FOR ONE REASON: it is exactly the projection. That
    /// endpoint returns a key and the two captions an option shows - <c>Users.Username</c> and
    /// <c>Users.DisplayName</c> - so those two are the only values a caller could see the effect of
    /// ordering by.
    /// </remarks>
    internal static readonly IReadOnlySet<string> UserChoices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Username",
            "DisplayName",
        };

    /// <summary>
    /// The union of every per-collection set: the outer bound that <c>PagedRequestValidator</c> applies to
    /// any request, whichever collection it addresses.
    /// </summary>
    /// <remarks>
    /// Built from the sets above rather than restated, so adding a field to a collection cannot leave the
    /// outer bound behind. A name in this set is guaranteed to be a constant declared in this file and
    /// therefore safe to compose into an ordering clause; whether it is meaningful for the collection
    /// actually being read is settled by the per-collection set.
    /// </remarks>
    internal static readonly IReadOnlySet<string> All = BuildUnion();

    /// <summary>Reports whether the supplied name is a member of the union.</summary>
    /// <param name="fieldName">
    /// The name a caller supplied, which may be <see langword="null"/>, empty or entirely white space.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="fieldName"/> is absent - because an absent sort field
    /// expresses no preference and is not an error - or when it names a field in <see cref="All"/>;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Absence is reported as acceptable so that a caller who supplies no ordering is not forced to reason
    /// about the allowlist at all. The absent-versus-blank question is settled the same way
    /// <c>PagedRequest.HasSort</c> settles it, so the validator and the contract cannot disagree about what
    /// "no preference" means.
    /// </remarks>
    internal static bool IsPermitted(string? fieldName) =>
        string.IsNullOrWhiteSpace(fieldName) || All.Contains(fieldName);

    /// <summary>Reports whether the supplied name is a member of one particular collection's set.</summary>
    /// <param name="permitted">The collection's set, one of the members declared above.</param>
    /// <param name="fieldName">
    /// The name a caller supplied, which may be <see langword="null"/>, empty or entirely white space.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="fieldName"/> is absent, or when it names a field in
    /// <paramref name="permitted"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Absence is tolerated on exactly the same terms as in the union overload, so a caller who expressed
    /// no preference is never asked to justify a field they did not name. This overload is what the
    /// per-collection validators apply, and it is the reason the narrow sets are no longer declared without
    /// a consumer.
    /// </remarks>
    internal static bool IsPermitted(IReadOnlySet<string> permitted, string? fieldName)
    {
        ArgumentNullException.ThrowIfNull(permitted);

        return string.IsNullOrWhiteSpace(fieldName) || permitted.Contains(fieldName);
    }

    /// <summary>Reports whether the supplied name is a member of one specific collection's set.</summary>
    /// <param name="fieldName">
    /// The name a caller supplied, which may be <see langword="null"/>, empty or entirely white space.
    /// </param>
    /// <param name="permitted">The collection's set, one of the members declared above.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="fieldName"/> is absent - an absent sort field expresses
    /// no preference and is never an error, whatever the collection - or when it is a member of <paramref
    /// name="permitted"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This is the test each listing applies before it reads anything, and it is the narrower authority:
    /// <see cref="IsPermitted(string?)"/> only proves the name is a constant declared in this file, which
    /// is what keeps caller text out of an ordering clause, while this proves the name means something for
    /// the collection actually being read.
    /// </remarks>
    internal static bool IsPermittedFor(string? fieldName, IReadOnlySet<string> permitted) =>
        IsPermitted(permitted, fieldName);

    /// <summary>Combines the per-collection sets into the union exposed by <see cref="All"/>.</summary>
    /// <returns>An immutable, case-insensitive set holding every permitted field name.</returns>
    private static IReadOnlySet<string> BuildUnion()
    {
        HashSet<string> union = new(StringComparer.OrdinalIgnoreCase);

        union.UnionWith(Portals);
        union.UnionWith(Roles);
        union.UnionWith(Users);
        union.UnionWith(UserChoices);
        union.UnionWith(Modules);
        union.UnionWith(RoleUsers);

        return union;
    }
}
