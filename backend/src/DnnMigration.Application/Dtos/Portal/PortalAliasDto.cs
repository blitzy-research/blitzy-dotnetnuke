namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Wire contract for a single portal alias: one host name, optionally with a virtual directory,
/// through which a portal is reached.
/// </summary>
/// <remarks>
/// <para>
/// Served by <c>PortalAliasesController</c> under <c>/api/v1/portals/{id}/aliases</c>. Ported from
/// the legacy <c>PortalAliasInfo</c> class at <c>Library/Components/Portal/PortalAliasInfo.vb</c>
/// lines 29 to 63, which declared exactly three properties over three private fields and carried
/// no constructor, no validation and no persistence logic. This contract keeps that shape: three
/// members, and nothing else.
/// </para>
/// <para>
/// The type is an inert data carrier. It holds values and exposes no behaviour: no validation, no
/// clamping, no normalisation, no computed member and no access to any data store. The legacy
/// behaviour that surrounded these values is implemented elsewhere by design. Trimming a scheme or
/// UNC prefix from a submitted alias and rejecting a blank one, which the legacy edit screen did
/// inline at <c>Website/admin/Portal/EditPortalAlias.ascx.vb</c> lines 208 to 215, belongs to the
/// request validators under <c>Application/Validation/</c>. Translating between this contract and
/// the persisted record belongs to <c>Application/Mapping/PortalMappings.cs</c>. Resolving an
/// incoming host name to a portal belongs to
/// <c>Api/Middleware/PortalAliasResolutionMiddleware.cs</c>.
/// </para>
/// <para>
/// No persisted record type is exposed here. Keeping the transported shape distinct from the stored
/// one is what allows the legacy sentinel semantics noted on the members below to be honoured at
/// the API edge without contaminating the model behind it.
/// </para>
/// <para>
/// The legacy list screen at <c>Website/admin/Portal/portalalias.ascx</c> bound a single grid
/// column, <c>HTTPAlias</c> (line 14), and the edit screen at
/// <c>Website/admin/Portal/editportalalias.ascx</c> exposed a single text box (line 7), so these
/// three members are sufficient for full parity with both screens.
/// </para>
/// </remarks>
public sealed class PortalAliasDto
{
    // MIGRATION: All three member names are modernised from the legacy all-capitals acronym
    // spelling to the Pascal-cased .NET form, so every name on this contract differs from its
    // legacy counterpart and from the underlying column name:
    //
    //     HTTPAlias      (PortalAliasInfo.vb line 53) becomes HttpAlias
    //     PortalAliasID  (PortalAliasInfo.vb line 45) becomes PortalAliasId
    //     PortalID       (PortalAliasInfo.vb line 37) becomes PortalId
    //
    // These are real, externally observable contract changes: a consumer that reads a legacy
    // spelling from a JSON payload will not find it. The renames are intentional and are left
    // visible rather than masked by a serialisation-name attribute, so a client model can reuse
    // these identifiers verbatim. Stored column names are untouched; the entity configuration in
    // the infrastructure layer continues to bind the legacy spellings.

    // MIGRATION: The legacy "every alias, across every portal" query was expressed by passing a
    // negative integer sentinel in place of a portal filter. GetPortalAliases at
    // Library/Components/Portal/PortalAliasController.vb lines 86 to 88 delegated to
    // GetPortalAliasByPortalID supplying that sentinel, and the stored procedure at
    // Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider line 3869 admitted
    // every row when it was given. That sentinel is deliberately absent from this contract: there
    // is no wildcard member, and no value of PortalId carries a special meaning. An unfiltered
    // alias query is expressed in the repository by omitting the portal predicate, never by
    // transmitting a magic number.

    // MIGRATION: Legacy tenant resolution compared an incoming host name against stored aliases
    // with a substring predicate, in the GetPortalSettings procedure at
    // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider lines 4569 to 4600,
    // which wrapped the supplied alias in leading and trailing wildcards. An alias that was a
    // substring of a different portal's alias could therefore resolve to the wrong tenant. The
    // replacement compares exactly, in Api/Middleware/PortalAliasResolutionMiddleware.cs. None of
    // that is implied by this contract, which transports an alias value and no matching rule.

    /// <summary>
    /// Gets or sets the surrogate key identifying this alias row.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>PortalAliasID</c> property
    /// (<c>Library/Components/Portal/PortalAliasInfo.vb</c> line 45) and to the
    /// <c>PortalAlias.PortalAliasID</c> column, declared <c>[int] IDENTITY (1, 1) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider</c> line 3805.
    /// The column is not nullable, so this member is a plain integer. The database assigns the
    /// value on insert, so a create request leaves it at its default and the created resource
    /// reports the assigned key.
    /// </remarks>
    public int PortalAliasId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal that owns this alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>PortalID</c> property
    /// (<c>Library/Components/Portal/PortalAliasInfo.vb</c> line 37) and to the
    /// <c>PortalAlias.PortalID</c> column, declared <c>[int] NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider</c> line 3806,
    /// with a foreign key to <c>Portals.PortalID</c> that cascades on delete (lines 3811 to 3818).
    /// An alias row always belongs to a portal, which is why this member is a plain integer rather
    /// than a nullable one.
    /// </para>
    /// <para>
    /// Every value this member can hold is a meaningful portal identifier. The <c>Portals</c> table
    /// seeds its identity column with a negative number
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 77)
    /// and the installation ships a portal whose identifier is zero (line 7125), so neither a
    /// negative number nor zero indicates a missing portal. Consumers must not infer absence from
    /// any particular value; on a response this member is always populated.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    // MIGRATION: An empty alias and an absent one are not distinguishable in legacy data. The
    // shared sentinel helper at Library/Components/Shared/Null.vb lines 71 to 75 returned the
    // empty string to represent an absent string, and the alias reader at
    // Library/Components/Portal/PortalAliasController.vb line 75 materialised the column through
    // Convert.ToString, which also yields the empty string for a database null. An empty value may
    // therefore legitimately appear where a modern consumer would expect none. This contract
    // transports whichever of the two it is handed and converts in neither direction; any decision
    // to reconcile them belongs to Application/Mapping/PortalMappings.cs and is recorded there.

    // MIGRATION: The legacy write path lower-cased the alias before persisting it, on insert at
    // Library/Components/Portal/PortalAliasController.vb line 31 and on update at line 97, while
    // the legacy reader keyed its collection by the lower-cased value (line 76) yet assigned the
    // property unchanged (line 75). Stored casing consequently need not match what a caller
    // submitted. This member reports the stored value unaltered and applies no casing rule of its
    // own.

    /// <summary>
    /// Gets or sets the alias by which the portal is reached, exactly as stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>HTTPAlias</c> property
    /// (<c>Library/Components/Portal/PortalAliasInfo.vb</c> line 53) and to the
    /// <c>PortalAlias.HTTPAlias</c> column, declared <c>[nvarchar] (200)</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider</c> line 3807.
    /// That declaration states no nullability and Transact-SQL treats an unqualified column as
    /// nullable, so this member is a nullable string: the faithful representation of the measured
    /// schema rather than a convenience.
    /// </para>
    /// <para>
    /// The value is the alias itself. It conveys nothing about how an incoming request is matched
    /// against it; that rule belongs to the resolution middleware named on this type.
    /// </para>
    /// </remarks>
    public string? HttpAlias { get; set; }
}
