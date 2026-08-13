using DnnMigration.Domain.Abstractions.Services;

namespace DnnMigration.Infrastructure.Services;

/// <summary>Immutable holder for the portal (tenant) facts of the single inbound call being served.</summary>
/// <remarks>
/// <para>
/// One instance belongs to one inbound call, and <c>PortalContextHolder</c> in this same namespace is what
/// builds it - after resolving the alias through <c>IPortalAliasRepository</c> and after checking that all
/// eight facts are actually present.
/// </para>
/// <para>
/// Every value arrives through the one constructor. There is no parameterless constructor, no settable
/// member, no loader, no refresh path and no post-construction population step, because a tenant snapshot
/// that could be rewritten midway through handling a call would defeat the reason for having one.
/// </para>
/// </remarks>
internal sealed class PortalContextAccessor : IPortalContext
{
    /// <summary>
    /// Creates the tenant snapshot for one inbound call from the eight values that the Api boundary has
    /// already resolved.
    /// </summary>
    /// <param name="portalId">Numeric key of the resolved portal.</param>
    /// <param name="portalName">Display name of the resolved portal.</param>
    /// <param name="portalAlias">The exact stored alias that this call resolved by, as a plain string.</param>
    /// <param name="portalAliasId">
    /// Surrogate key of the <c>dbo.PortalAlias</c> row this call resolved through.
    /// </param>
    /// <param name="administratorId">
    /// Numeric key of the account designated administrator, or <see langword="null"/> when the portal
    /// designates none.
    /// </param>
    /// <param name="administratorRoleId">
    /// Numeric key of the role conferring portal administration rights, or <see langword="null"/> when the
    /// portal names no such role.
    /// </param>
    /// <param name="administratorRoleName">
    /// Name of the role conferring portal administration rights, already joined by the caller because no
    /// such column exists.
    /// </param>
    /// <param name="registeredRoleId">
    /// Numeric key of the role granted to every signed-in member, or <see langword="null"/> when the portal
    /// names no such role.
    /// </param>
    /// <param name="registeredRoleName">
    /// Name of the role granted to every signed-in member, already joined by the caller because no such
    /// column exists.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="portalName"/> or <paramref name="portalAlias"/> is <see
    /// langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="portalName"/> or <paramref name="portalAlias"/> is an empty string -
    /// both are contract-guaranteed to be populated, so a blank one is a defect in the resolution flow and
    /// is refused here rather than trimmed, defaulted or carried onward as a blank tenant fact - and thrown
    /// when a role key and its name disagree about being present, for the reason given in migration note 7
    /// below.
    /// </exception>
    public PortalContextAccessor(
        int portalId,
        string portalName,
        string portalAlias,
        int portalAliasId,
        int? administratorId,
        int? administratorRoleId,
        string? administratorRoleName,
        int? registeredRoleId,
        string? registeredRoleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(portalName);
        ArgumentException.ThrowIfNullOrEmpty(portalAlias);

        ThrowIfRolePairIncoherent(
            administratorRoleId,
            administratorRoleName,
            nameof(administratorRoleId),
            nameof(administratorRoleName));

        ThrowIfRolePairIncoherent(
            registeredRoleId,
            registeredRoleName,
            nameof(registeredRoleId),
            nameof(registeredRoleName));

        PortalId = portalId;
        PortalName = portalName;
        PortalAlias = portalAlias;
        PortalAliasId = portalAliasId;
        AdministratorId = administratorId;
        AdministratorRoleId = administratorRoleId;
        AdministratorRoleName = administratorRoleName;
        RegisteredRoleId = registeredRoleId;
        RegisteredRoleName = registeredRoleName;
    }

    /// <summary>Numeric key of the portal (tenant) that this call resolved to.</summary>
    public int PortalId { get; }

    /// <summary>Display name of the resolved portal.</summary>
    public string PortalName { get; }

    /// <summary>The alias that this call was resolved by, as a plain string.</summary>
    public string PortalAlias { get; }

    /// <summary>Surrogate key of the <c>dbo.PortalAlias</c> row that this call was resolved by.</summary>
    public int PortalAliasId { get; }

    /// <summary>
    /// Numeric key of the account designated administrator of the resolved portal, or <see
    /// langword="null"/> when the portal designates none.
    /// </summary>
    public int? AdministratorId { get; }

    /// <summary>Numeric key of the role that confers portal administration rights in the resolved portal.</summary>
    public int? AdministratorRoleId { get; }

    /// <summary>Name of the role that confers portal administration rights in the resolved portal.</summary>
    public string? AdministratorRoleName { get; }

    /// <summary>Numeric key of the role granted to every signed-in member of the resolved portal.</summary>
    public int? RegisteredRoleId { get; }

    /// <summary>Name of the role granted to every signed-in member of the resolved portal.</summary>
    public string? RegisteredRoleName { get; }

    /// <summary>
    /// Refuses a role key and role name that disagree about being present, or a name that is present but
    /// blank.
    /// </summary>
    /// <param name="roleId">The role key supplied for the pair, or <see langword="null"/>.</param>
    /// <param name="roleName">The role name supplied for the pair, or <see langword="null"/>.</param>
    /// <param name="roleIdParameterName">Parameter name of the key, for the thrown message.</param>
    /// <param name="roleNameParameterName">Parameter name of the name, for the thrown message.</param>
    /// <exception cref="ArgumentException">
    /// The pair disagrees about being present, or the name is present and blank.
    /// </exception>
    /// <remarks>
    /// Deliberately reports which pair is inconsistent and in which direction, and nothing more: no message
    /// here reproduces a portal name, an alias, an account key or a role key, because this type's own
    /// migration notes make it a holder of authorisation facts rather than a diagnostic surface.
    /// </remarks>
    private static void ThrowIfRolePairIncoherent(
        int? roleId,
        string? roleName,
        string roleIdParameterName,
        string roleNameParameterName)
    {
        if (roleId.HasValue && string.IsNullOrWhiteSpace(roleName))
        {
            throw new ArgumentException(
                $"{roleIdParameterName} was supplied without a populated {roleNameParameterName}. "
                + "The name is a joined projection of the key, so one cannot exist without the "
                + "other, and a blank name would be a value capable of matching a role-name "
                + "comparison.",
                roleNameParameterName);
        }

        if (!roleId.HasValue && roleName is not null)
        {
            throw new ArgumentException(
                $"{roleNameParameterName} was supplied without {roleIdParameterName}. A portal that "
                + "names no role has no name to carry either, so the pair must be absent together.",
                roleNameParameterName);
        }
    }
}
