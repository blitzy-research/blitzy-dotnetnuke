namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Wire contract for a single portal alias: one host name, optionally with a virtual directory, through
/// which a portal is reached.
/// </summary>
/// <remarks>
/// <para>
/// No persisted record type is exposed here. Keeping the transported shape distinct from the stored one is
/// what allows the legacy sentinel semantics noted on the members below to be honoured at the API edge
/// without contaminating the model behind it.
/// </para>
/// <para>
/// The legacy list screen at <c>Website/admin/Portal/portalalias.ascx</c> bound a single grid column,
/// <c>HTTPAlias</c> (line 14), and the edit screen at <c>Website/admin/Portal/editportalalias.ascx</c>
/// exposed a single text box (line 7), so these three members are sufficient for full parity with both
/// screens.
/// </para>
/// </remarks>
public sealed class PortalAliasDto
{
    // MIGRATION: All three member names are modernised from the legacy all-capitals acronym spelling to the
    // Pascal-cased .NET form, so every name on this contract differs from its legacy counterpart and from
    // the underlying column name.

    // The legacy "every alias, across every portal" query was expressed by passing a negative integer
    // sentinel in place of a portal filter.

    // Legacy tenant resolution compared an incoming host name against stored aliases with a substring
    // predicate, in the GetPortalSettings procedure at
    // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider lines 4569 to 4600, which
    // wrapped the supplied alias in leading and trailing wildcards.

    /// <summary>Gets or sets the surrogate key identifying this alias row.</summary>
    public int PortalAliasId { get; set; }

    /// <summary>Gets or sets the identifier of the portal that owns this alias.</summary>
    /// <remarks>
    /// Every value this member can hold is a meaningful portal identifier.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the alias by which the portal is reached, exactly as stored.</summary>
    /// <remarks>
    /// The value is the alias itself. It conveys nothing about how an incoming request is matched against
    /// it; that rule belongs to the resolution middleware named on this type.
    /// </remarks>
    public string? HttpAlias { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this alias is the one the current request resolved through,
    /// and therefore must not be renamed or removed.
    /// </summary>
    /// <remarks>
    /// Computed per request rather than stored — see the migration note immediately above. Reported as <see
    /// langword="false"/> on every row whenever the request resolved no tenant at all, which is the correct
    /// answer rather than a fallback: with no resolved alias, no row is the one being browsed through.
    /// </remarks>
    public bool IsCurrent { get; set; }
}
