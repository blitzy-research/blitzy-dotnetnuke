namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>Immutable portal (tenant) facts for the single inbound call being served.</summary>
/// <remarks>
/// <para>
/// Exactly one object implementing this contract exists per inbound call, and it is complete from the
/// moment it exists.
/// </para>
/// <para>
/// THE INVARIANT: the snapshot is settled before the first consumer resolves this contract, and it is never
/// mutated afterwards.
/// </para>
/// </remarks>
public interface IPortalContext
{
    /// <summary>Numeric key of the portal (tenant) that this call resolved to.</summary>
    int PortalId { get; }

    /// <summary>Display name of the resolved portal.</summary>
    string PortalName { get; }

    /// <summary>The alias that this call was resolved by, as a plain string.</summary>
    string PortalAlias { get; }

    /// <summary>Surrogate key of the <c>dbo.PortalAlias</c> row that this call was resolved by.</summary>
    /// <remarks>
    /// Non-nullable, because a call that has reached a consumer resolved through exactly one alias row.
    /// </remarks>
    int PortalAliasId { get; }

    /// <summary>
    /// Numeric key of the account designated administrator of the resolved portal, or <see
    /// langword="null"/> when the portal designates none.
    /// </summary>
    /// <remarks>
    /// Nullable because the column is: <c>[AdministratorId] [int] NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> L86, modelled as
    /// <c>int?</c> on <c>Domain/Entities/Portal.cs</c>.
    /// </remarks>
    int? AdministratorId { get; }

    /// <summary>
    /// Numeric key of the role that confers portal administration rights in the resolved portal, or <see
    /// langword="null"/> when the portal names no such role.
    /// </summary>
    int? AdministratorRoleId { get; }

    /// <summary>
    /// Name of the role that confers portal administration rights in the resolved portal, or <see
    /// langword="null"/> when the portal has no such role.
    /// </summary>
    string? AdministratorRoleName { get; }

    /// <summary>
    /// Numeric key of the role granted to every signed-in member of the resolved portal, or <see
    /// langword="null"/> when the portal has no such role.
    /// </summary>
    int? RegisteredRoleId { get; }

    /// <summary>
    /// Name of the role granted to every signed-in member of the resolved portal, or <see langword="null"/>
    /// when the portal has no such role.
    /// </summary>
    string? RegisteredRoleName { get; }
}
