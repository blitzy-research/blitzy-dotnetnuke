using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Wire contract for one row of the security-roles listing served by <c>GET
/// /api/v1/roles</c> and rendered by the Angular role-list screen through the
/// shared data-table component.
/// </summary>
/// <remarks>
/// <para>
/// THIS SHAPE IS THE LEGACY GRID, NOT THE LEGACY ENTITY. The eleven members below
/// are the exact eleven the legacy screen bound, measured column by column from
/// the grid declaration in <c>Website/admin/Security/roles.ascx</c> - note the
/// lower-case file name, whose code-behind <c>Roles.ascx.vb</c> is capitalised.
/// The legacy <c>RoleInfo</c> class carries fifteen public members; four of them
/// are deliberately absent here because that screen never rendered them, and a
/// response shape is derived from what the legacy screen actually rendered rather
/// than from the shape of the persisted model. Widening this contract towards the
/// entity would leak columns the listing has no column for. The four are: the
/// owning-portal identifier, which every row in a response already shares because
/// the request is tenant-scoped, and which the grid has no column for; the
/// role-group identifier, which that screen used as a FILTER through an
/// auto-posting drop-down at <c>roles.ascx</c> line 9 feeding the group-scoped
/// query at <c>Roles.ascx.vb</c> line 75, so it belongs to the query contract and
/// not to a row; and the invitation code together with the icon path, two real
/// columns added by <c>03.02.03.SqlDataProvider</c> lines 44 to 45 that this grid
/// simply does not show, and that consequently belong to the detail contract. A
/// per-user assignment status is absent for a different reason: that
/// classification describes one user's membership of a role, derived from the
/// effective and expiry dates on the assignment table, so a role definition has no
/// such status at all.
/// </para>
/// <para>
/// THE TYPE IS INERT. It holds no behaviour, computes nothing, reaches no database
/// and performs no validation. Field rules live in the FluentValidation validators
/// under <c>Application/Validation</c>, and translation to and from the persisted
/// model lives in <c>Application/Mapping/RoleMappings.cs</c>, which is the single
/// place that owns every sentinel decision described against the individual
/// members below. Nothing here carries serialisation metadata either: the legacy
/// class decorated most of its members with element-name serialisation attributes,
/// and those are dropped because the property names alone express the wire shape.
/// </para>
/// <para>
/// THE PAGING ENVELOPE IS NOT PART OF THIS TYPE. A row is a row. The controller
/// wraps a sequence of these in the <c>PagedResponse</c> envelope declared in the
/// sibling <c>Dtos/Common</c> folder, which is the only place that carries the
/// collection, the total and the page coordinates. No positional, ordering,
/// filtering, navigation or link member appears here.
/// </para>
/// </remarks>
// MIGRATION: paging is a NET ADDITION, not a translation. The legacy grid was
// unpaged and unsorted: its declaration at roles.ascx lines 22 to 25 sets only
// AutoGenerateColumns and EnableViewState, and declares no paging attribute, no
// sorting attribute and no page-size attribute at all. Behind it, the role query
// at RoleController.vb line 208 returns an untyped, pre-generic collection of
// every matching role in one shot. Serving this contract inside a paged envelope
// therefore adds a capability the legacy screen did not have. It is additive and
// cannot change any existing outcome - an unpaged caller receives the same rows in
// the same order - but it is a deliberate enhancement and is stated as such rather
// than presented as fidelity.
// MIGRATION: the wire representation of the two frequency members changes from a
// human-readable description to the stable stored code, and that is intentional.
// The terminal listing procedure, recreated for the last time in
// 04.05.05.SqlDataProvider and never dropped afterwards, resolved each frequency
// through a lookup join and projected the lookup's display text, not the code:
// "case when convert(int,R.ServiceFee) <> 0 then L1.Text else '' end" for billing
// and the matching expression over the trial column. Projecting display text made
// the payload a presentation artefact - unsortable, unfilterable, and unstable
// under any change of wording. This contract carries the code as an enumeration
// member instead and leaves the wording to the client, which is what lets the same
// payload serve a grid, a filter and a future translation. The legacy wording
// itself stays recoverable from the resource files, which remain the authority for
// user-facing text.
public sealed class RoleListItemDto
{
    /// <summary>
    /// Identifier of the role, and the row key the listing acts on.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleID int IDENTITY (0, 1) NOT NULL</c>, declared
    /// in the baseline table at <c>01.00.00.SqlDataProvider</c> lines 114 to 115.
    /// Legacy member <c>RoleInfo.RoleID</c>, an <c>Integer</c> at
    /// <c>RoleInfo.vb</c> line 65. The legacy grid did not render it as a column;
    /// it carried it as the key of both action columns, declared
    /// <c>keyfield="RoleID"</c> at <c>roles.ascx</c> lines 34 and 35, which are
    /// the edit and role-membership affordances now addressed by the routed detail
    /// and membership screens.
    /// </remarks>
    // MIGRATION: the column is seeded IDENTITY(0,1), so the very FIRST role ever
    // inserted carries the identifier zero, and zero is therefore a perfectly
    // legitimate, addressable role rather than a missing one. Never probe this
    // property for absence by comparing it against zero, against a
    // negative-or-zero range, or against the type's default value: each of those
    // tests would silently exclude the first row in the table. Nor may negative
    // one be treated as absence here. That value is the legacy integer null
    // sentinel from Null.vb line 41, and the legacy editor did overload it as its
    // own add-versus-edit switch, but the same schema also seeds a neighbouring
    // table IDENTITY(-1,1) at 01.00.00.SqlDataProvider line 77, which makes
    // negative one a genuine identifier elsewhere and the sentinel unsafe to
    // generalise. The routed endpoints already distinguish creation from update,
    // so no magic value is carried forward, and the property stays a plain
    // non-nullable int faithful to the NOT NULL column.
    public int RoleId { get; set; }

    /// <summary>
    /// Name of the role, unique within its portal, and the column the legacy
    /// listing ordered by.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleName nvarchar(50) NOT NULL</c>, declared at
    /// <c>01.00.00.SqlDataProvider</c> line 117. Legacy member
    /// <c>RoleInfo.RoleName</c>, a <c>String</c> at <c>RoleInfo.vb</c> line 110,
    /// bound as the first data column at <c>roles.ascx</c> line 36 under the
    /// header "Name". The terminal listing procedure sorts on this column, so it
    /// is the natural default ordering for the paged endpoint. The length ceiling
    /// and the mandatory-value rule are reproduced declaratively by the
    /// validators; this contract asserts neither.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role, or <see langword="null"/> when the role
    /// has none.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.Description nvarchar(1000) NULL</c>, declared at
    /// <c>01.00.00.SqlDataProvider</c> line 118. Legacy member
    /// <c>RoleInfo.Description</c>, a <c>String</c> at <c>RoleInfo.vb</c> line
    /// 125, bound as the second data column at <c>roles.ascx</c> line 38 under the
    /// header "Description".
    /// </remarks>
    // MIGRATION: the legacy read path could never yield null here, because every
    // read funnelled through Null.SetNull and its string sentinel -
    // Null.NullString at Null.vb line 70 - is the EMPTY STRING and not null. A
    // database NULL and an empty description were therefore indistinguishable once
    // loaded, and the grid rendered an empty cell for both. This contract models
    // the nullable column honestly with a nullable string rather than importing
    // that sentinel, which makes the two distinguishable on the wire for the first
    // time. Which of them stands for an absent description is settled in
    // Application/Mapping/RoleMappings.cs, the one place that translates between
    // this contract and the persisted model, and is stated here in prose rather
    // than imposed by a serialisation attribute or a custom converter so that the
    // divergence stays visible instead of being applied silently while the payload
    // is written.
    public string? Description { get; set; }

    /// <summary>
    /// Recurring subscription fee for the role, or <see langword="null"/> when the
    /// role is free and the legacy listing showed a blank fee cell.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.ServiceFee money NULL DEFAULT (0)</c>. Legacy
    /// member <c>RoleInfo.ServiceFee</c>, declared <c>As Single</c> at
    /// <c>RoleInfo.vb</c> line 164, bound through the <c>FormatPrice</c> helper by
    /// the "Fee" template column at <c>roles.ascx</c> lines 40 and 42.
    /// </remarks>
    // MIGRATION: the type is resolved from the TERMINAL schema and not from the
    // legacy property, which declared a single-precision floating-point value. A
    // binary floating-point type cannot represent a decimal currency amount
    // exactly and must never carry a fee, so this member is a decimal.
    // MIGRATION: the terminal schema also overrides the BASELINE schema, and this
    // column is the clearest demonstration of why only the terminal state of the
    // upgrade chain is meaningful. Reading the baseline alone gives the wrong
    // answer twice over. The chronology, verified case-insensitively across all
    // four naming forms the chain uses: 01.00.00.SqlDataProvider line 119 declares
    // "[ServiceFee] [decimal](5, 2) NULL", which caps a fee at 999.99; the table
    // is then recreated as "money" at 01.00.04.SqlDataProvider line 1326 and again
    // at 01.00.05.SqlDataProvider line 2752; and 03.01.01.SqlDataProvider line
    // 1173 issues the terminal "ALTER COLUMN [ServiceFee] [money] NULL", with line
    // 1177 adding a default of zero. That ALTER COLUMN is the ONLY one applied to
    // this table anywhere in the 88-script chain, so nothing supersedes it, and
    // every later procedure signature agrees - the last of them declares the
    // parameter "money" at 04.00.04.SqlDataProvider line 459. The independent
    // third witness is the legacy editor, which validated this field as a currency
    // data type. Money is a fixed-point type that maps to a decimal, so the scale
    // ceiling implied by the baseline is not merely superseded, it never applied
    // to a live database.
    // MIGRATION: null is the FAITHFUL representation of a free role, not a
    // modernisation. The legacy pipeline already blanked this cell end to end: the
    // terminal listing procedure projects "case when convert(int,R.ServiceFee) <>
    // 0 then R.ServiceFee else null end", the reader then substituted the legacy
    // single-precision null sentinel, and FormatPrice at Roles.ascx.vb line 175
    // returned the empty string for exactly that sentinel. A blank cell was
    // therefore what an administrator saw for a free role, and null is what that
    // blank cell means. Nothing in this contract represents the sentinel value
    // itself; converting between the two is settled in
    // Application/Mapping/RoleMappings.cs.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing units between charges, or <see langword="null"/> when the
    /// role is free and the legacy listing showed a blank period cell. It is a
    /// multiplier over <see cref="BillingFrequency"/> and is meaningless alone.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.BillingPeriod int NULL</c>, added at
    /// <c>01.00.08.SqlDataProvider</c> line 6829 and never altered afterwards;
    /// that same script backfills existing rows to one at lines 6900 to 6901.
    /// Legacy member <c>RoleInfo.BillingPeriod</c>, declared <c>As Integer</c> at
    /// <c>RoleInfo.vb</c> line 218, bound through the <c>FormatPeriod</c> helper
    /// by the first "Every" template column at <c>roles.ascx</c> lines 45 and 47.
    /// </remarks>
    // MIGRATION: a genuine three-way type conflict, resolved four-to-one in favour
    // of a nullable integer. The legacy entity property is an integer, the schema
    // column is int NULL, and the legacy editor validated the field as an integer
    // data type; only one legacy data-provider signature disagreed by passing the
    // value as text, and a textual period would make every arithmetic and ordering
    // operation over this column a parse. The decisive witness is the terminal
    // listing procedure itself, which emits SQL NULL for a free role - "case when
    // convert(int,R.ServiceFee) <> 0 then R.BillingPeriod else null end" -
    // regardless of what is actually stored in the column. A nullable integer
    // carrying null is therefore not a modernisation of the legacy contract; it is
    // the literal value the legacy data layer produced.
    // MIGRATION: null here is faithful for the same reason it is faithful on the
    // fee. FormatPeriod at Roles.ascx.vb line 152 returned the empty string
    // whenever the value equalled the legacy integer null sentinel, so the legacy
    // grid already rendered a blank period cell. The sentinel value is not carried
    // into this contract.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Unit the billing period is counted in, or <see langword="null"/> when no
    /// code is stored against the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.BillingFrequency char(1) NULL</c>, declared at
    /// <c>01.00.00.SqlDataProvider</c> line 120 and never altered afterwards; the
    /// baseline constrained it with <c>FK_Roles_CodeFrequency</c> at line 583.
    /// Legacy member <c>RoleInfo.BillingFrequency</c>, declared <c>As String</c>
    /// at <c>RoleInfo.vb</c> line 149, bound as a plain data column at
    /// <c>roles.ascx</c> line 50 under the header "Period".
    /// </remarks>
    // MIGRATION: the legacy single-character text value becomes the shared domain
    // enumeration, whose members are explicitly valued at the code points of those
    // very characters. The codes are LOAD-BEARING DATA - they are the literal
    // bytes already sitting in this column in every existing database - so no
    // member is ever renamed or renumbered. Typing the member list is a real
    // strengthening and not decoration: the constraint that once policed this
    // column was dropped when the dedicated lookup table was folded into the
    // generic list table, so the terminal column accepts any single character, and
    // confining it to the declared members restores a guarantee the schema itself
    // stopped providing.
    // MIGRATION: null means NO CODE IS STORED, and it must never be conflated with
    // the None member. That member is the real stored code 'N', meaning "does not
    // expire", and the legacy SQL relies on it as a live discriminator: the
    // terminal listing procedure gates the whole trial group behind "case when
    // R.TrialFrequency <> 'N'", which only works because 'N' is a value present in
    // the column rather than its absence. Collapsing the two would break
    // trial-period selection.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Fee charged for the trial period, or <see langword="null"/> when the role
    /// offers no trial and the legacy listing showed a blank trial cell.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialFee money NULL</c>, added at
    /// <c>01.00.08.SqlDataProvider</c> line 6830 and never altered afterwards, so
    /// unlike its billing counterpart it was fixed-point from birth and never
    /// passed through a superseded decimal declaration. Every later procedure
    /// signature agrees, the last declaring the parameter <c>money</c> at
    /// <c>04.00.04.SqlDataProvider</c> line 462. Legacy member
    /// <c>RoleInfo.TrialFee</c>, declared <c>As Single</c> at <c>RoleInfo.vb</c>
    /// line 233, bound through the <c>FormatPrice</c> helper by the "Trial"
    /// template column at <c>roles.ascx</c> lines 53 and 55.
    /// </remarks>
    // MIGRATION: a decimal for the same two reasons as the billing fee - a binary
    // floating-point type cannot hold a currency amount exactly, and the terminal
    // schema type is fixed-point - and null for the same reason as well, except
    // that the terminal listing procedure gates this member on the trial code
    // rather than on the fee: "case when R.TrialFrequency <> 'N' then R.TrialFee
    // else null end". A role with no trial therefore arrived as SQL NULL, became
    // the legacy single-precision null sentinel on read, and was rendered as a
    // blank cell by FormatPrice. Null preserves that outcome exactly.
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial units before the first charge, or <see langword="null"/>
    /// when the role offers no trial and the legacy listing showed a blank period
    /// cell. It is a multiplier over <see cref="TrialFrequency"/> and is
    /// meaningless alone.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialPeriod int NULL</c>, present in the baseline
    /// table at <c>01.00.00.SqlDataProvider</c> line 121, restated when the table
    /// was recreated at <c>01.00.04.SqlDataProvider</c> line 1328 and
    /// <c>01.00.05.SqlDataProvider</c> line 2754, and never altered afterwards.
    /// Legacy member <c>RoleInfo.TrialPeriod</c>, declared <c>As Integer</c> at
    /// <c>RoleInfo.vb</c> line 203, bound through the <c>FormatPeriod</c> helper
    /// by the second "Every" template column at <c>roles.ascx</c> lines 58 and 60.
    /// </remarks>
    // MIGRATION: a nullable integer on the same four-to-one evidence as the
    // billing period, and never text. The terminal listing procedure emits "case
    // when R.TrialFrequency <> 'N' then R.TrialPeriod else null end", so a role
    // with no trial was already delivered as SQL NULL, and FormatPeriod already
    // rendered the resulting legacy integer null sentinel as a blank cell. The
    // sentinel value is not carried into this contract.
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Unit the trial period is counted in, or <see langword="null"/> when no code
    /// is stored against the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialFrequency char(1) NULL</c>, declared at
    /// <c>01.00.00.SqlDataProvider</c> line 122 and never altered afterwards.
    /// Legacy member <c>RoleInfo.TrialFrequency</c>, declared <c>As String</c> at
    /// <c>RoleInfo.vb</c> line 188, bound as a plain data column at
    /// <c>roles.ascx</c> line 63 under the header "Period".
    /// </remarks>
    // MIGRATION: this member deliberately reuses the SAME shared domain
    // enumeration as its billing counterpart, and no separate trial-specific type
    // is created anywhere. The schema settles it: one frequency lookup is joined
    // TWICE from a single role row, once per column, in both eras of the chain -
    // early on against the dedicated lookup table's code, and terminally as two
    // joins onto the same generic 'Frequency' list, one keyed on this column and
    // one on the billing column. Both columns therefore draw from one shared code
    // set, and one type covers them both. Declaring a second enumeration, or a
    // local copy of the shared one, would fork a single source of truth for no
    // gain.
    // MIGRATION: as on the billing counterpart, null means no code is stored and
    // is never the same thing as the None member, which is the real stored code
    // 'N' meaning "no trial" and is precisely the value the terminal listing
    // procedure tests against to decide whether the trial group applies at all.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// Whether the role is publicly visible, so that a user may subscribe to it
    /// themselves rather than being assigned to it by an administrator.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.IsPublic bit NOT NULL DEFAULT 0</c>, added with its
    /// default constraint at <c>01.00.08.SqlDataProvider</c> line 6831 and
    /// re-asserted under qualifier templating at <c>03.01.01.SqlDataProvider</c>
    /// line 1174, whose line 1179 restores the default of zero. Legacy member
    /// <c>RoleInfo.IsPublic</c>, a <c>Boolean</c> at <c>RoleInfo.vb</c> line 248,
    /// bound by the "Public" template column at <c>roles.ascx</c> lines 66 to 69,
    /// which showed one of two images according to the value.
    /// </remarks>
    // MIGRATION: non-nullable on purpose. The column is NOT NULL with a default of
    // zero, so the value is always present and a nullable boolean would misstate
    // the schema and invent a third state the database cannot hold.
    // MIGRATION: the wire type changes from text to a real boolean. The terminal
    // listing procedure projected this column as one of the two words True and
    // False - "case when R.IsPublic = 1 then 'True' else 'False' end" - and the
    // legacy grid then compared that text against a lower-case literal to pick
    // which image to show, at roles.ascx lines 68 and 69. That round trip through
    // text was a workaround for an untyped binding pipeline, and its
    // case-sensitivity was a latent defect rather than a feature. The primitive
    // replaces the workaround: this contract carries a boolean and the client
    // renders it, which removes the comparison entirely instead of preserving a
    // fragile one.
    public bool IsPublic { get; set; }

    /// <summary>
    /// Whether the role is granted automatically to every new user of the portal.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.AutoAssignment bit NOT NULL DEFAULT 0</c>, added
    /// with its default constraint at <c>01.00.08.SqlDataProvider</c> line 6832
    /// and re-asserted under qualifier templating at
    /// <c>03.01.01.SqlDataProvider</c> line 1175, whose line 1181 restores the
    /// default of zero. Legacy member <c>RoleInfo.AutoAssignment</c>, a
    /// <c>Boolean</c> at <c>RoleInfo.vb</c> line 263, bound by the "Auto" template
    /// column at <c>roles.ascx</c> lines 72 to 75, which showed one of two images
    /// according to the value.
    /// </remarks>
    // MIGRATION: non-nullable, and text-to-boolean, for exactly the reasons given
    // on the neighbouring flag. The column is NOT NULL with a default of zero, and
    // the terminal listing procedure projected it as the words True and False in
    // the same manner, which the legacy grid string-compared in the same way at
    // roles.ascx lines 74 and 75.
    public bool AutoAssignment { get; set; }
}
