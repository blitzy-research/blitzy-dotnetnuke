namespace DnnMigration.Domain.Common;

/// <summary>
/// One configured host name as TENANT RESOLUTION needs it: the alias row that matched, the portal it binds
/// to, and the six portal facts a request's tenant snapshot is built from.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THIS EXISTS SO THAT RESOLVING A TENANT DOES NOT REQUIRE READING ONE. A performance review measured the
/// per-request alias lookup loading the matched portal's ENTIRE role collection - one result row per role,
/// with all thirty <c>Portals</c> columns repeated on each - because the two role NAMES below are not
/// columns on <c>Portals</c> and the collection was the only route to them. On a tenant holding fifteen
/// hundred roles that single lookup returned fifteen hundred rows and scanned the whole <c>Roles</c> table,
/// on every authenticated request, to answer a question with exactly one row in it.
/// </para>
/// <para>
/// The two role names are resolved BY KEY instead, which is a seek on <c>Roles</c>' primary key rather than
/// a scan of the tenant's collection, and the cost of resolving a tenant therefore no longer grows with the
/// number of roles that tenant happens to hold.
/// </para>
/// <para>
/// A record rather than a class, and positional rather than property-initialised, because it is a value:
/// two resolutions naming the same alias with the same facts are the same resolution, and there is no state
/// to mutate. It is deliberately NOT an entity - it has no identity of its own, is never tracked, and is
/// never written. Every nullable member below is nullable because the column or the joined row it projects
/// may legitimately be absent, and absence is reported rather than defaulted so that the caller can refuse a
/// half-configured tenant and say which fact is missing.
/// </para>
/// </remarks>
/// <param name="PortalAliasId">
/// Surrogate key of the matched <c>dbo.PortalAlias</c> row, from <c>PortalAlias.PortalAliasID</c>.
/// </param>
/// <param name="HttpAlias">
/// The STORED host name, from <c>PortalAlias.HTTPAlias</c> (<c>nvarchar(200) NULL</c>) - not the value the
/// caller supplied, which is equal to it under the lookup's own comparison but is not authoritative.
/// </param>
/// <param name="PortalId">
/// Numeric key of the portal the alias binds to, from <c>PortalAlias.PortalID</c>. <c>Portals.PortalID</c>
/// is <c>IDENTITY (-1, 1)</c>, so <c>-1</c> and <c>0</c> are real tenants here and neither may be read as
/// "absent".
/// </param>
/// <param name="PortalName">Display name of that portal, from <c>Portals.PortalName</c>.</param>
/// <param name="AdministratorId">
/// Numeric key of the account designated administrator, from <c>Portals.AdministratorId</c>, or <see
/// langword="null"/> when the portal designates none.
/// </param>
/// <param name="AdministratorRoleId">
/// Numeric key of the role conferring portal administration rights, from
/// <c>Portals.AdministratorRoleId</c>, or <see langword="null"/> when the portal names no such role.
/// </param>
/// <param name="AdministratorRoleName">
/// Name of the role named by <paramref name="AdministratorRoleId"/>, joined from <c>Roles.RoleName</c> by
/// key because no such column exists on <c>Portals</c>; <see langword="null"/> when the portal names no such
/// role or when no role bears the key it names.
/// </param>
/// <param name="RegisteredRoleId">
/// Numeric key of the role granted to every signed-in member, from <c>Portals.RegisteredRoleId</c>, or <see
/// langword="null"/> when the portal names no such role.
/// </param>
/// <param name="RegisteredRoleName">
/// Name of the role named by <paramref name="RegisteredRoleId"/>, joined from <c>Roles.RoleName</c> by key;
/// <see langword="null"/> when the portal names no such role or when no role bears the key it names.
/// </param>
public sealed record TenantResolution(
    int PortalAliasId,
    string? HttpAlias,
    int PortalId,
    string? PortalName,
    int? AdministratorId,
    int? AdministratorRoleId,
    string? AdministratorRoleName,
    int? RegisteredRoleId,
    string? RegisteredRoleName);
