namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// One row of the portals administration grid, as returned inside a page by
/// <c>GET /api/v1/portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// The member set is taken from the legacy grid rather than invented, so the replacement screen
/// renders the columns an administrator already knows.
/// <c>Website/admin/Portal/portals.ascx</c> declares its column block at lines 20 to 55, holding ten
/// column elements. Two are affordances rather than data - the edit and delete image commands at
/// lines 21 and 22, which the replacement expresses through the endpoints themselves - and the
/// remaining <b>eight are data columns</b>, carried here one member each, in the legacy display
/// order: the portal identifier (lines 23 to 29), the title bound to <c>PortalName</c> (line 34), the
/// portal aliases (lines 37 to 43), the member tally (line 44), the page tally (line 45), the
/// disk-space allowance (line 46), the hosting fee (line 47) and the expiry instant (lines 48 to 54).
/// </para>
/// <para>
/// Everything the grid does not render is deliberately absent: the payment-processor credentials, the
/// special-page identifiers, and the localisation and skinning settings all belong to the portal
/// detail and settings contracts. A listing endpoint that returned them would publish a portal's
/// whole configuration to any caller merely permitted to see that the portal exists.
/// </para>
/// <para>
/// <b>Paging lives on the envelope, never on the row.</b> A page is returned as
/// <c>PagedResponse&lt;PortalListItemDto&gt;</c>, so the wire shape is
/// <c>{ "items": [...], "meta": {...} }</c>: the rows under <c>items</c>, and the total, page index
/// and page size under <c>meta</c>. The API edge projects the Application layer's
/// <c>PagedResult&lt;PortalListItemDto&gt;</c> into it, so the domain paging type never crosses the
/// boundary. None of the paging facts is restated on the row: two copies of one fact on a single
/// response give a pager two sources of truth and no way to choose between them.
/// </para>
/// <para>
/// <b>The page index is zero-based</b>, so index 0 addresses the first page. That is the shipped data
/// layer's own convention, not a preference: the paging procedure sets its row lower bound to the page
/// size multiplied by the page index
/// (<c>GetPortalsByName</c>, <c>04.04.00.SqlDataProvider</c> line 254), so index zero addresses the
/// first row. The legacy stack was split on the question and wrote neither base down - the
/// administration screen counted from one and subtracted one on every call into the provider
/// (<c>Portals.ascx.vb</c> line 142, corroborated across <c>Website/admin/Users/Users.ascx.vb</c>
/// line 51 against lines 265, 269, 271 and 274) - so the base is stated here rather than left to be
/// inferred. Choosing the other base fails neither the compiler nor a test asserting a successful
/// response code; it quietly serves the neighbouring page.
/// </para>
/// <para>
/// <b>The grand total is an <c>int</c></b>, because the procedure computes it as a row count
/// (<c>04.04.00.SqlDataProvider</c> line 277) and a T-SQL row count is an <c>int</c>. Widening it
/// would be an opportunistic change to a measured contract.
/// </para>
/// <para>
/// <b>The name filter carries no pattern syntax of its own.</b> The procedure matches
/// <c>PortalName</c> against a caller-supplied pattern (line 267), and the legacy screen appended the
/// trailing wildcard itself (<c>Portals.ascx.vb</c> line 142). The replacement therefore accepts
/// literal text on the request contract and escapes pattern metacharacters, which is a documented
/// deliberate divergence recorded against the portal service. Nothing about the filter belongs on
/// this row type, and no filter, sort or query member is declared here.
/// </para>
/// <para>
/// The type is an inert data carrier: no behaviour, no validation, no mapping and no data-store
/// access. Translating a persisted record into this shape belongs to <c>Application/Mapping/</c>,
/// bounding the page coordinates belongs to <c>Application/Validation/</c>, and serialiser
/// configuration belongs to the API layer.
/// </para>
/// </remarks>
// MIGRATION: the eight-column reading above is a direct measurement, and it is recorded here because
// the obvious way to re-derive it is wrong. Extracting the grid's column captions with a pattern that
// accepts only letters silently drops the third data column, whose caption is the two words "Portal
// Aliases" and therefore contains a space; such a pattern reports seven columns and invites a contract
// that omits the aliases altogether. Counting the column ELEMENTS in the block, rather than matching
// their captions, is the reliable measurement and is what yields ten elements, two of them actions.
//
// MIGRATION: three members here are non-nullable where the sibling detail and settings contracts
// declare their counterparts nullable - PortalName, HostFee and HostSpace. The divergence is
// deliberate and this contract is the schema-faithful one, because a list row is a pure read
// projection whereas those contracts additionally model a caller omitting a field. The terminal state
// of all three columns is NOT NULL: PortalName has been nvarchar(128) NOT NULL since
// 01.00.00.SqlDataProvider line 79, while HostFee and HostSpace were tightened from their nullable
// baseline (01.00.00.SqlDataProvider lines 89 and 90) to money NOT NULL and int NOT NULL by
// 03.01.01.SqlDataProvider lines 1118 and 1119, each acquiring a DEFAULT (0) constraint at lines 1129
// and 1131. The Domain entity agrees, declaring string PortalName, decimal HostFee and int HostSpace.
// Declaring them nullable here would assert that the database can report an absent value for a column
// that cannot hold one.
public sealed class PortalListItemDto
{
    /// <summary>
    /// Identifier of the portal.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.PortalID int NOT NULL IDENTITY (-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 77). Rendered by the legacy grid's first data column,
    /// <c>portals.ascx</c> lines 23 to 29.
    /// </remarks>
    // MIGRATION: the column's identity seed is negative, so the first identifier the column generates
    // is minus one, while the shipped "_default" portal row is inserted with an explicit identifier of
    // nought at 01.00.00.SqlDataProvider line 7125. Both are legitimate, addressable tenants. This
    // is the sharpest sentinel collision in the schema, because the legacy integer absence sentinel
    // (Library/Components/Shared/Null.vb lines 41 to 45) is the very same negative value, and that
    // module's absence test reports any integer equal to it as missing (lines 210 and 211).
    // Consequently a test for absence against that negative value, against nought, against a
    // non-positive range or against the type's own default would each silently exclude a real tenant.
    // Absence is never expressed on this member: it is non-nullable and no such test appears anywhere
    // in this file. Sibling seeds differ again - Users starts at one (line 98) while Roles, Tabs and
    // Modules start at nought (lines 115, 140 and 221) - so no single rule covers them all.
    public int PortalId { get; set; }

    /// <summary>
    /// Name of the portal, as the grid's title column shows it.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.PortalName nvarchar(128) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 79). Rendered by the legacy grid's title column, bound to
    /// <c>PortalName</c> at <c>portals.ascx</c> line 34 under the caption "Title".
    /// </remarks>
    // MIGRATION: the stored column name is kept and the legacy caption "Title" is not adopted as the
    // member name. The caption is display wording, and its localised form belongs in the client
    // template sourced from Website/admin/Portal/App_LocalResources/Portals.ascx.resx.
    //
    // MIGRATION: non-nullable, matching the column and the Domain entity, and initialised so that a
    // partially populated projection cannot present a null reference through a non-nullable member.
    // The initialiser is safe here precisely because this type is only ever read: it is never mapped
    // back to a row, so an empty string can never be written to a required column through it.
    //
    // MIGRATION: an EMPTY STRING may legitimately arrive here where a modern reader would expect a
    // null. The legacy null contract represents an absent string as the empty string literally - the
    // sentinel property at Library/Components/Shared/Null.vb lines 71 to 75 returns "" rather than a
    // null reference - and its absence test compares against that same empty string (line 226). In
    // legacy data the two states are therefore INDISTINGUISHABLE, and this contract preserves what is
    // stored rather than reinterpreting one as the other. The shipped seed row demonstrates the same
    // habit on neighbouring columns, storing "" for a value it has none of
    // (01.00.00.SqlDataProvider line 7125).
    public string PortalName { get; set; } = string.Empty;

    /// <summary>
    /// Host names by which the portal is reachable, empty when it has none.
    /// </summary>
    /// <remarks>
    /// Projected from <c>PortalAlias.HTTPAlias</c> for the portal's alias rows. Rendered by the legacy
    /// grid's third data column, <c>portals.ascx</c> lines 37 to 43, under the caption
    /// "Portal Aliases".
    /// </remarks>
    // MIGRATION: this member is mandatory, not optional. It carries the third of the eight data
    // columns the legacy grid rendered, so omitting it would drop a column the administrator can see
    // today and would breach the functional-parity requirement that governs this migration.
    //
    // MIGRATION: projected into the row, which removes a per-row round trip. The legacy grid resolved
    // the aliases inside its item template by calling a helper once per row (portals.ascx line 41,
    // implemented at Portals.ascx.vb line 273), and that helper performed its own lookup per call
    // (line 277), so rendering a page of ten portals issued ten additional queries. Carrying the host
    // names in the row eliminates that without changing what is displayed - a performance consequence
    // of the architecture rather than an opportunistic optimisation, since the rendered output is
    // identical.
    //
    // MIGRATION: a sequence of host names rather than of full alias contracts, because the legacy
    // helper emitted only the host names, as hyperlink text built from HTTPAlias (Portals.ascx.vb
    // line 282). A caller administering aliases uses the dedicated alias endpoints, which additionally
    // report the identifiers. Exposed as a read-only sequence and never as a deferred one: a deferred
    // sequence could be walked twice and answer differently each time.
    //
    // MIGRATION: initialised empty and never null. A portal with no alias is a real and reportable
    // state, and an empty sequence expresses it without obliging every caller to test for a null
    // before enumerating. The legacy helper behaved the same way, returning an empty string when the
    // untyped collection it read held nothing.
    public IReadOnlyList<string> Aliases { get; set; } = [];

    /// <summary>
    /// Number of users registered in the portal. Populated by the application service; it is not a
    /// column on <c>Portals</c>.
    /// </summary>
    /// <remarks>
    /// Rendered by the legacy grid's member-tally column, bound to <c>Users</c> at
    /// <c>portals.ascx</c> line 44.
    /// </remarks>
    // MIGRATION: a plain settable value, deliberately NOT a computed or lazily-loading property. The
    // legacy member it replaces was declared on PortalInfo.vb line 309 and read the count from the
    // database on first access, inside its own getter, guarding on whether the backing field still
    // held a negative marker (line 311) before issuing the query (line 312). That shape cannot survive
    // the move: every database-bound operation in the replacement returns an awaitable, and a property
    // getter cannot be awaited, so a getter that performs input or output is structurally impossible
    // here. The count is supplied from outside instead, by PortalService, which awaits the portal
    // repository's user-counting member (CountUsersAsync) and hands the result to the mapper.
    //
    // MIGRATION: three independent facts establish that this is not a persisted column, and they are
    // recorded because a reader may reasonably assume otherwise. First, the legacy getter queried for
    // it rather than reading it, as cited above. Second, it is absent from the view the legacy readers
    // actually selected from: vw_Portals is defined at 04.05.00.SqlDataProvider lines 1530 to 1587 and
    // projects no such tally, and it is that view which the portal readers query
    // (04.04.00.SqlDataProvider lines 199 and 230). Third, the count was declared as a query in its
    // own right on the membership data surface, at
    // Library/Providers/MembershipProviders/DataProvider/DataProvider.vb line 82.
    //
    // MIGRATION: the legacy negative "not yet loaded" marker is GONE from the wire, and this member is
    // never negative. That marker was an artefact of the lazy getter's internal bookkeeping, sharing
    // its value with the integer absence sentinel; emitting it would tell a client that a portal has a
    // negative number of users. The member is therefore left at its natural default until the service
    // sets the real value, and it is deliberately not seeded with that marker.
    //
    // MIGRATION: non-nullable, because a count always exists and a row count is never absent. Nought
    // is a real answer rather than a missing one. The value is deliberately not compared against the
    // portal's user quota here: whether a portal exceeds its quota is a policy judgement, and this row
    // reports facts.
    //
    // MIGRATION: the legacy grid bound its columns straight to the two lazily-loading tallies, so
    // rendering a page of N portals issued N member-count queries and N page-count queries - a
    // per-row query pattern on top of the per-row alias lookup noted above. Populating the tallies
    // explicitly preserves the displayed output exactly while removing that pattern.
    public int Users { get; set; }

    /// <summary>
    /// Number of pages defined in the portal. Populated by the application service; it is not a column
    /// on <c>Portals</c>.
    /// </summary>
    /// <remarks>
    /// Rendered by the legacy grid's page-tally column, bound to <c>Pages</c> at <c>portals.ascx</c>
    /// line 45.
    /// </remarks>
    // MIGRATION: a plain settable value, on the same reasoning as the member tally above. The legacy
    // member was declared on PortalInfo.vb line 320, guarded on a negative marker in the backing field
    // (line 322) and constructed a controller to issue the query in its getter (lines 323 and 324),
    // which resolved to TabController.GetTabCount(PortalID). The replacement is supplied from outside by
    // PortalService, which awaits the portal repository's page-counting member (CountPagesAsync). It is
    // likewise absent from vw_Portals and is never seeded with the legacy marker.
    //
    // MIGRATION: the value CAN be negative on the wire, and that is preserved legacy arithmetic. The
    // terminal GetTabCount (04.04.00.SqlDataProvider lines 511 to 527) is SELECT COUNT(*) - 1 with the
    // portal's administration page and its direct children excluded, so a portal that records no
    // administration page yields 0 - 1 and the legacy grid displayed minus one. Clamping it here would
    // report a figure the legacy application never showed. It is NOT the legacy Null.NullInteger
    // sentinel and must not be read as "unknown".
    //
    // MIGRATION: note that this generation of the product deletes pages softly, by flagging the row
    // rather than removing it, so whether a soft-deleted page is counted is a decision the counting
    // query makes - and the legacy answer is that it IS counted, because GetTabCount states no
    // soft-delete condition at all. The tally is reported exactly as that query produces it and this
    // contract asserts no interpretation of its own, which is what keeps the displayed figure equal to
    // the legacy one.
    //
    // MIGRATION: reported as a settable value even though the legacy declaration is often described as
    // read-only. Direct measurement contradicts that description: both legacy tallies expose a setter,
    // the member tally at PortalInfo.vb line 316 and this one at line 328. The measurement is recorded
    // rather than quietly corrected, and it reinforces the design here - a settable value is exactly
    // what an externally populated tally needs.
    public int Pages { get; set; }

    /// <summary>
    /// Disk-space allowance for the portal in megabytes, where nought carries the legacy meaning of no
    /// imposed limit.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.HostSpace int NOT NULL</c> with a
    /// <c>DF_{objectQualifier}Portals_HostSpace DEFAULT (0)</c> constraint
    /// (<c>03.01.01.SqlDataProvider</c> lines 1119 and 1131, tightened from the nullable baseline at
    /// <c>01.00.00.SqlDataProvider</c> line 90). Rendered by the legacy grid's disk-space column,
    /// bound to <c>HostSpace</c> at <c>portals.ascx</c> line 46 under the caption "DiskSpace".
    /// </remarks>
    // MIGRATION: three legacy sources disagree on this value's type and the schema decides, because
    // the schema is the immutable artefact this migration binds to. PortalInfo.vb line 165 declares it
    // Integer; the twenty-seven-parameter setter at PortalController.vb line 1568 declares the same
    // value Double, as does the local it is assembled through at line 345; and the terminal column is
    // int NOT NULL. int is therefore carried here, matching the column and the Domain entity. It is
    // NOT widened to a floating-point type merely because one legacy caller passed one, and it is not
    // a monetary type - despite the neighbouring fee being one - because the column is a whole-number
    // allowance rather than an amount of money.
    //
    // MIGRATION: non-nullable, because the column has been NOT NULL with a zero default since
    // 03.01.01 and the Domain entity declares it non-nullable too. Absence is therefore expressed by
    // the defaulted nought rather than by a null, and this contract preserves that vocabulary at the
    // boundary instead of reinterpreting nought as "unset".
    //
    // MIGRATION: the legacy caption reads "DiskSpace" while the column and the Domain entity call it
    // HostSpace. The stored vocabulary is kept so that a client field name maps to a column without a
    // translation table, and the caption remains a client concern. The value is an allowance, not a
    // measurement of space consumed.
    public int HostSpace { get; set; }

    /// <summary>
    /// Recurring hosting fee charged for the portal, nought when none is charged.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.HostFee money NOT NULL</c> with a
    /// <c>DF_{objectQualifier}Portals_HostFee DEFAULT (0)</c> constraint
    /// (<c>03.01.01.SqlDataProvider</c> lines 1118 and 1129, converted from the
    /// <c>nvarchar(10)</c> baseline at <c>01.00.00.SqlDataProvider</c> line 89). Rendered by the
    /// legacy grid's hosting-fee column, bound at <c>portals.ascx</c> line 47 under the caption
    /// "HostingFee" with the display format <c>{0:0.00}</c>.
    /// </remarks>
    // MIGRATION: the same three-way type disagreement as the allowance above, resolved the same way
    // and to a different answer. PortalInfo.vb line 157 declares this value Single; the
    // twenty-seven-parameter setter at PortalController.vb line 1568 declares it Double, as does the
    // local at line 340; and the terminal column is money NOT NULL. decimal is therefore carried here,
    // matching the column's exact monetary type and the Domain entity. Neither floating-point
    // candidate is adopted: binary floating point cannot represent ordinary decimal amounts exactly,
    // which is precisely the wrong property for a billed figure.
    //
    // MIGRATION: non-nullable, matching the column's NOT NULL and DEFAULT (0) and matching the Domain
    // entity, so an unbilled portal reports nought rather than a null.
    //
    // MIGRATION: the CURRENCY CODE IS NOT CARRIED ON THIS ROW. Currency is a genuine column
    // (Portals.Currency char(3) NULL, 01.00.00.SqlDataProvider line 88, declared at PortalInfo.vb
    // line 149), and a monetary figure without its unit is arguably incomplete, but the legacy grid
    // did not render it - it is not among the eight measured data columns - so carrying it would add a
    // column the screen never had. A caller needing the unit obtains it from the portal detail or
    // portal settings resource, both of which do carry it. The shipped seed row stores "USD"
    // (01.00.00.SqlDataProvider line 7125).
    //
    // MIGRATION: reported unformatted. The legacy grid's two-decimal format string is a presentation
    // concern; emitting a preformatted string would impose one culture's conventions on every client
    // and make the number useless for arithmetic.
    //
    // MIGRATION: because the baseline typed this column nvarchar(10), an installation upgraded from a
    // pre-03.01.01 release once held fees as free text - the shipped seed row stores an empty string
    // for it (01.00.00.SqlDataProvider line 7125). The 03.01.01 conversion is what guarantees the
    // values are numeric by the terminal state, so no parsing or fallback is performed here.
    public decimal HostFee { get; set; }

    /// <summary>
    /// Instant at which the portal's hosting subscription lapses, or <see langword="null"/> when it
    /// does not expire.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.ExpiryDate datetime NULL</c> (<c>01.00.00.SqlDataProvider</c>
    /// line 83, declared at <c>PortalInfo.vb</c> line 117). Rendered by the legacy grid's expiry
    /// column, <c>portals.ascx</c> lines 48 to 54, which passed the value through a formatting helper
    /// implemented at <c>Portals.ascx.vb</c> line 250.
    /// </remarks>
    // MIGRATION: genuinely nullable, unlike the two members above - this column really is NULL in the
    // terminal state and was never tightened.
    //
    // MIGRATION: the legacy contract expressed "does not expire" with a SENTINEL DATE rather than with
    // a database null. The sentinel is the minimum representable date, 0001-01-01, defined at
    // Library/Components/Shared/Null.vb lines 66 to 70, and the legacy formatting helper existed
    // precisely because the value reaching it could be that sentinel: it tested for absence before
    // formatting and returned an empty string otherwise (Portals.ascx.vb lines 253 and 254).
    //
    // MIGRATION: that absence test compares only the DATE PART. The absence routine converts the value
    // and compares its date component against the sentinel's date component
    // (Library/Components/Shared/Null.vb lines 222 to 224), so ANY instant falling on 0001-01-01 was
    // treated as absent regardless of its time of day. A caller reading legacy data should expect the
    // minimum date to mean "no expiry", and this contract does not silently rewrite such a value into
    // a null - null here means the column itself is null, which keeps the two stored states
    // distinguishable at the boundary instead of collapsing them.
    //
    // MIGRATION: reported as an instant, unformatted. The legacy helper returned a culture-formatted
    // short date string, which is a presentation concern the client now owns.
    public DateTime? ExpiryDate { get; set; }
}
