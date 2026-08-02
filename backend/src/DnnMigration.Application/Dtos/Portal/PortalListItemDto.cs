namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// One row of the portals administration grid, as returned inside a page by
/// <c>GET /api/v1/portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// The member set is taken from the legacy grid rather than invented, so the replacement screen
/// renders the same columns. <c>Website/admin/Portal/portals.ascx</c> declares its column block at
/// lines 20 to 55, of which lines 21 and 22 are the edit and delete affordances - actions rather
/// than data, expressed by the endpoints themselves - and lines 23 to 54 are exactly eight data
/// columns: portal identifier, title, portal aliases, users, pages, disk space, hosting fee and
/// expiry. This contract carries one member for each.
/// </para>
/// <para>
/// It is the list counterpart of <see cref="PortalDetailDto"/> and carries eight of its members
/// rather than all thirty-six. Everything a grid does not render - the payment processor
/// credentials, the special-page identifiers, the localisation and skinning settings - is
/// deliberately absent, because a listing endpoint that returned them would publish a portal's
/// configuration to any caller permitted merely to see that the portal exists.
/// </para>
/// <para>
/// <b>The aliases are projected into the row, which fixes a legacy round-trip defect.</b> The legacy
/// grid resolved them per row by calling <c>FormatPortalAliases(PortalID)</c> from its item template
/// (<c>portals.ascx</c> line 41, implemented at <c>Portals.ascx.vb</c> line 273), so rendering a page
/// of ten portals issued ten additional queries. Carrying the host names in the row eliminates that
/// without changing what is displayed.
/// </para>
/// <para>
/// The type is an inert data carrier: no behaviour, no validation, no database access. The paging
/// envelope is supplied by the shared result type, and the sort and filter arguments live in the
/// request contract, so this row type declares neither.
/// </para>
/// </remarks>
// MIGRATION: three members here are non-nullable where the sibling PortalDetailDto declares its
// counterparts nullable - PortalName, HostFee and HostSpace. The divergence is deliberate and this
// contract is the schema-faithful one. The terminal state of all three columns is NOT NULL:
// PortalName has been nvarchar(128) NOT NULL since 01.00.00.SqlDataProvider line 79 and was rebuilt
// unchanged at 01.00.05.SqlDataProvider line 1368, while HostFee and HostSpace were widened from
// their nullable baseline (01.00.00.SqlDataProvider lines 89 and 90) to money NOT NULL and int NOT
// NULL by 03.01.01.SqlDataProvider lines 1118 and 1119, each acquiring a DEFAULT (0) constraint at
// lines 1129 and 1131. The Domain entity agrees, declaring string PortalName, decimal HostFee and
// int HostSpace. Declaring them nullable here would assert that the database can report an absent
// value for a column that cannot hold one, which is the schema-fidelity defect this review raised
// against other contracts; it is not reproduced.
public sealed class PortalListItemDto
{
    /// <summary>
    /// Identifier of the portal.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.PortalID int NOT NULL IDENTITY(-1,1)</c>. Rendered by the legacy
    /// grid's first data column, <c>portals.ascx</c> lines 23 to 29.
    /// </remarks>
    // MIGRATION: the column is seeded IDENTITY(-1,1), which makes -1 the identifier of the FIRST
    // real portal and 0 the identifier of the second. Both are legitimate, addressable portals.
    // This is the sharpest sentinel collision in the schema, because -1 is simultaneously the
    // legacy Null.NullInteger sentinel, so a test for absence against -1, against zero, against a
    // non-positive range or against the type's default value would each silently exclude a real
    // tenant. Absence is never expressed on this member; it is non-nullable.
    public int PortalId { get; set; }

    /// <summary>
    /// Name of the portal, as the grid's title column shows it.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.PortalName nvarchar(128) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 79, rebuilt unchanged at
    /// <c>01.00.05.SqlDataProvider</c> line 1368). Rendered by the legacy grid's title column, bound
    /// to <c>PortalName</c> at <c>portals.ascx</c> line 34.
    /// </remarks>
    // MIGRATION: non-nullable, matching the column and the Domain entity. Initialised so that a
    // partially populated projection cannot present a null through a non-nullable member; the
    // initialiser is the DTO-layer convention and is safe here precisely because this type is only
    // ever read - it is never mapped back to a row, so an empty string can never be written to a
    // required column through it.
    //
    // MIGRATION: this is the member the list endpoint's name filter narrows on, and the filter is
    // matched as LITERAL text. The legacy screen appended a wildcard to the caller's input
    // (Portals.ascx.vb line 142 passes Filter + "%"), which let a caller inject matching syntax and
    // force a scan; that is a documented deliberate divergence recorded on IPortalService, and the
    // implementation escapes pattern metacharacters instead.
    public string PortalName { get; set; } = string.Empty;

    /// <summary>
    /// Host names by which the portal is reachable, empty when it has none.
    /// </summary>
    /// <remarks>
    /// Projected from <c>PortalAlias.HTTPAlias nvarchar(200) NULL</c> for the portal's alias rows.
    /// Replaces the per-row lookup the legacy grid performed at <c>portals.ascx</c> line 41.
    /// </remarks>
    // MIGRATION: a list of host names rather than a list of full alias contracts, because the legacy
    // grid rendered only the delimited host names and a listing row needs nothing more. A caller
    // managing aliases uses the dedicated alias endpoints, which return the identifiers too.
    //
    // MIGRATION: initialised to an empty sequence and never null. A portal with no alias is a real
    // and reportable state - the column itself is nullable - and an empty sequence expresses it
    // without obliging every caller to null-check before enumerating. This is the collection
    // convention established across the DTO layer.
    //
    // MIGRATION: the legacy tenant-resolution procedure matched an alias with a wildcard comparison
    // (GetPortalSettings at 01.00.00.SqlDataProvider line 4569 used a LIKE against the alias
    // surrounded by percent signs) and could resolve one portal's request against another portal's
    // row. The replacement resolves aliases by exact match. That divergence is recorded in
    // MIGRATION_NOTES.md and affects resolution, not this projection, which lists what is stored.
    public IReadOnlyList<string> Aliases { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Number of users registered in the portal.
    /// </summary>
    /// <remarks>
    /// A derived count, not a column. Rendered by the legacy grid's users column, bound to
    /// <c>Users</c> at <c>portals.ascx</c> line 44.
    /// </remarks>
    // MIGRATION: computed by the query rather than stored, exactly as the legacy listing procedure
    // computed it, so it is a snapshot at read time and is not transactionally consistent with a
    // concurrent registration. Non-nullable because a count always exists, and zero is a real
    // answer rather than a missing one. It is deliberately not compared against the portal's user
    // quota here: whether a portal is over quota is a policy judgement, and this row reports facts.
    public int Users { get; set; }

    /// <summary>
    /// Number of pages defined in the portal.
    /// </summary>
    /// <remarks>
    /// A derived count, not a column. Rendered by the legacy grid's pages column, bound to
    /// <c>Pages</c> at <c>portals.ascx</c> line 45.
    /// </remarks>
    // MIGRATION: computed by the query, as the legacy did. Note that DotNetNuke deletes pages
    // softly by setting Tabs.IsDeleted, so whether a soft-deleted page is counted is a decision the
    // query makes; the count is reported as the query produces it and this contract asserts no
    // interpretation of its own.
    public int Pages { get; set; }

    /// <summary>
    /// Disk-space allowance for the portal in megabytes, where zero carries the legacy meaning of
    /// no imposed limit.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.HostSpace int NOT NULL</c> with
    /// <c>DF_{objectQualifier}Portals_HostSpace DEFAULT (0)</c>
    /// (<c>03.01.01.SqlDataProvider</c> lines 1119 and 1131, widened from the nullable baseline at
    /// <c>01.00.00.SqlDataProvider</c> line 90). Rendered by the legacy grid's disk-space column,
    /// bound to <c>HostSpace</c> at <c>portals.ascx</c> line 46 under the header "DiskSpace".
    /// </remarks>
    // MIGRATION: non-nullable, because the column has been NOT NULL with a zero default since
    // 03.01.01 and the Domain entity declares it non-nullable too. Absence is therefore expressed
    // by the defaulted zero rather than by a null, and this contract preserves that rather than
    // reinterpreting zero as "unset" - Rule T7 keeps the stored vocabulary observable at the
    // boundary instead of converting it.
    //
    // MIGRATION: the legacy header labels this "DiskSpace" while the column and the Domain entity
    // call it HostSpace; the stored vocabulary is kept and the display label is a client concern.
    // The value is an allowance, not a measurement of space consumed.
    public int HostSpace { get; set; }

    /// <summary>
    /// Recurring hosting fee charged for the portal, zero when none is charged.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.HostFee money NOT NULL</c> with
    /// <c>DF_{objectQualifier}Portals_HostFee DEFAULT (0)</c>
    /// (<c>03.01.01.SqlDataProvider</c> lines 1118 and 1129, widened from the nullable
    /// <c>nvarchar(10)</c> baseline at <c>01.00.00.SqlDataProvider</c> line 89). Rendered by the
    /// legacy grid's hosting-fee column, bound at <c>portals.ascx</c> line 47 with the display
    /// format <c>{0:0.00}</c>.
    /// </remarks>
    // MIGRATION: decimal rather than float, because the value is monetary and the column's terminal
    // type is money; the same reasoning recorded in MIGRATION_NOTES.md for the role fees applies
    // here. Non-nullable, matching the column's NOT NULL and DEFAULT (0) and matching the Domain
    // entity, so an unbilled portal reports zero rather than a null.
    //
    // MIGRATION: the value is reported unformatted. The legacy grid's two-decimal format string is
    // a presentation concern, and emitting a formatted string would impose one culture's
    // conventions on every client and make the number unusable for arithmetic.
    //
    // MIGRATION: the baseline typed this column nvarchar(10), so a legacy installation upgraded from
    // a pre-03.01.01 release holds fees that were once free text. The 03.01.01 conversion is what
    // guarantees they are numeric by the terminal state; no parsing or fallback is performed here.
    public decimal HostFee { get; set; }

    /// <summary>
    /// Instant at which the portal's hosting subscription lapses, or <see langword="null"/> when it
    /// does not expire.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.ExpiryDate datetime NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 83). Rendered by the legacy grid's expires column,
    /// which passed the value through <c>FormatExpiryDate</c> at <c>portals.ascx</c> line 51,
    /// implemented at <c>Portals.ascx.vb</c> line 250.
    /// </remarks>
    // MIGRATION: genuinely nullable, unlike the two members above - this column really is NULL in
    // the terminal state and was never tightened.
    //
    // MIGRATION: null means "does not expire", and the legacy minimum-date sentinel is never written
    // here. The legacy helper existed precisely because the value reaching it could be that
    // sentinel: it guarded with a null-sentinel test before formatting (Portals.ascx.vb lines 253
    // and 254) and returned an empty string otherwise. Representing absence as a null removes the
    // need for that helper rather than porting it.
    //
    // MIGRATION: reported as an instant, unformatted. The legacy helper returned a culture-formatted
    // short date string, which is a presentation concern the client now owns.
    public DateTime? ExpiryDate { get; set; }
}
