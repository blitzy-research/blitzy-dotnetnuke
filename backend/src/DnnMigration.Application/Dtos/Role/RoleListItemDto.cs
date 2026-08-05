using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// One row of the security-roles listing served by
/// <c>GET /api/v1/roles</c>.
/// </summary>
/// <remarks>
/// <para>
/// The member set is the legacy grid, not the legacy entity. The eleven members and their
/// declaration order are measured from <c>Website/admin/Security/roles.ascx</c>, so rendering
/// them in declaration order reproduces the legacy column order. Legacy <c>RoleInfo</c> carries
/// fifteen public members; four are deliberately absent. The owning-portal identifier is absent
/// because the request is already tenant-scoped and the grid had no such column. The role-group
/// identifier is absent because that screen used it as a <i>filter</i> - an auto-posting
/// drop-down at <c>roles.ascx</c> L9 feeding the group-scoped query at <c>Roles.ascx.vb</c> L75 -
/// so it belongs to the query contract, not to a row. The invitation code and the icon path are
/// real columns (added by <c>03.02.03.SqlDataProvider</c> L44-L45) that the grid never showed. A
/// per-user assignment status is absent for a different reason: that classification describes one
/// user's membership, derived from the effective and expiry dates on the assignment table, so a
/// role definition has none.
/// </para>
/// <para>
/// The type is inert: no validation, no persistence and no serialisation attributes, and the
/// legacy element-name serialisation attributes are dropped. It is a RESPONSE shape, so no rule
/// applies to it in either direction; the write contracts it mirrors are governed by
/// <c>CreateRoleRequestValidator</c> and <c>UpdateRoleRequestValidator</c>, which share one rule
/// definition in <c>Application/Validation/RoleTermsRules.cs</c> so the two verbs cannot diverge, and
/// by the checks in <c>Application/Services/RoleService.cs</c> for the questions a field rule cannot
/// answer because they need a read. Translation to and from
/// the persisted model belongs to the hand-written role mapper under <c>Application/Mapping/</c>.
/// </para>
/// <para>
/// Paging is not part of this type: an implementer is obliged to return a sequence of these
/// inside the paged response envelope, which alone owns the item list, total count, page index
/// and page size. Paging and sorting have no legacy counterpart - <c>roles.ascx</c> L22-L25 set
/// only <c>AutoGenerateColumns</c> and <c>EnableViewState</c>, and the role query at
/// <c>RoleController.vb</c> L208 returned every matching role in one untyped pre-generic
/// collection.
/// </para>
/// <para>
/// Every optional money and period member arrives <see langword="null"/> for a role that is free
/// or offers no trial, which is faithful rather than a modernisation: the terminal listing
/// procedure already projected SQL NULL for those cases, gating the billing pair on
/// <c>convert(int,R.ServiceFee) &lt;&gt; 0</c> and the trial pair on
/// <c>R.TrialFrequency &lt;&gt; 'N'</c>; the legacy reader substituted its null sentinel; and the
/// <c>FormatPrice</c> and <c>FormatPeriod</c> helpers at <c>Roles.ascx.vb</c> L175 and L152
/// rendered that sentinel as an empty cell. No sentinel value is carried into this contract.
/// </para>
/// </remarks>
// MIGRATION: the two frequency members carry the stored CODE, not the lookup's display text. The
// terminal listing procedure (last recreated in 04.05.05.SqlDataProvider and never dropped
// afterwards) resolved each frequency through a lookup join and projected the lookup's Text
// column - "case when convert(int,R.ServiceFee) <> 0 then L1.Text else '' end", with a matching
// expression over the trial column - which made the payload a presentation artefact: unsortable,
// unfilterable, and unstable under any wording change. The legacy wording stays recoverable from
// the resource files.
public sealed class RoleListItemDto
{
    /// <summary>
    /// Primary key of the role, carried by the legacy grid as the key of both action affordances
    /// rather than as a rendered column.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleID int IDENTITY (0, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L114-L115); legacy member <c>RoleInfo.RoleID</c>
    /// (<c>RoleInfo.vb</c> L65), used as <c>keyfield="RoleID"</c> on the edit and role-membership
    /// columns at <c>roles.ascx</c> L34-L35.
    /// </remarks>
    // MIGRATION: identifier trap. The column is seeded IDENTITY(0,1), so the first role ever
    // inserted carries identifier zero and zero is a legitimate addressable role. Never probe for
    // absence by comparing against zero, a negative-or-zero range, or the type default. -1 is
    // equally unsafe: it is the legacy integer null sentinel (Null.vb L41) which the legacy editor
    // overloaded as its own add-versus-edit switch, but the same schema seeds Portals.PortalID
    // IDENTITY(-1,1) at 01.00.00.SqlDataProvider L77, so -1 is a genuine identifier elsewhere. The
    // routed endpoints distinguish creation from update, so no magic value is carried forward.
    public int RoleId { get; set; }

    /// <summary>
    /// Display name of the role, and the natural default ordering for the paged endpoint because
    /// the terminal listing procedure sorts on this column.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L117); legacy member <c>RoleInfo.RoleName</c>
    /// (<c>RoleInfo.vb</c> L110), the first data column at <c>roles.ascx</c> L36 under the header
    /// "Name". The length ceiling and the mandatory-value rule are reproduced on the create path by
    /// <c>CreateRoleRequestValidator</c> and on every other write path by
    /// <c>RoleService</c>; this contract asserts neither.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.Description nvarchar(1000) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L118); legacy member <c>RoleInfo.Description</c>
    /// (<c>RoleInfo.vb</c> L125), the second data column at <c>roles.ascx</c> L38.
    /// </remarks>
    // MIGRATION: the legacy read path could never yield null, because every read funnelled through
    // Null.SetNull and its string sentinel is the EMPTY STRING (Null.vb L70). A database NULL and
    // an empty description were therefore indistinguishable once loaded, and the grid rendered an
    // empty cell for both. This contract models the nullable column honestly, which makes the two
    // distinguishable for the first time; deciding which of them stands for an absent description
    // is an obligation of the role mapper.
    public string? Description { get; set; }

    /// <summary>
    /// Recurring subscription fee for the role, or <see langword="null"/> when the role is free.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.ServiceFee money NULL DEFAULT (0)</c>; legacy member
    /// <c>RoleInfo.ServiceFee</c>, declared <c>As Single</c> (<c>RoleInfo.vb</c> L164), bound
    /// through <c>FormatPrice</c> by the "Fee" template column at <c>roles.ascx</c> L40 and L42.
    /// </remarks>
    // MIGRATION: decimal, resolved from the TERMINAL schema rather than from the legacy property
    // or the baseline column. A binary floating-point type cannot represent a currency amount
    // exactly, so the single-precision legacy declaration cannot be carried forward; and the
    // baseline "[ServiceFee] [decimal](5, 2) NULL" at 01.00.00.SqlDataProvider L119, which would
    // cap a fee at 999.99, is superseded by the terminal "ALTER COLUMN [ServiceFee] [money] NULL"
    // at 03.01.01.SqlDataProvider L1173, with a default of zero added at L1177. That is the only
    // ALTER COLUMN applied to this table anywhere in the chain, and every later procedure
    // signature agrees, the last declaring the parameter money at 04.00.04.SqlDataProvider L459.
    // Money is a fixed-point type that maps to decimal, so the baseline scale ceiling never
    // applied to a live database.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing units between charges, or <see langword="null"/> when the role is free.
    /// It is a multiplier over <see cref="BillingFrequency"/> and is meaningless alone.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.BillingPeriod int NULL</c>, added at
    /// <c>01.00.08.SqlDataProvider</c> L6829 and never altered afterwards; that same script
    /// backfills existing rows to one at L6900-L6901. Legacy member <c>RoleInfo.BillingPeriod</c>,
    /// declared <c>As Integer</c> (<c>RoleInfo.vb</c> L218), bound through <c>FormatPeriod</c> by
    /// the first "Every" template column at <c>roles.ascx</c> L45 and L47.
    /// </remarks>
    // MIGRATION: a nullable integer, never text. One legacy data-provider signature passed this
    // value as text, which would make every arithmetic and ordering operation over the column a
    // parse; the entity property, the schema column and the legacy editor's validator all agree on
    // an integer, and the terminal listing procedure emits SQL NULL for a free role regardless of
    // what is actually stored in the column.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Unit the billing period is counted in, or <see langword="null"/> when no code is stored
    /// against the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.BillingFrequency char(1) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L120, never altered; the baseline constrained it with
    /// <c>FK_Roles_CodeFrequency</c> at L583). Legacy member <c>RoleInfo.BillingFrequency</c>,
    /// declared <c>As String</c> (<c>RoleInfo.vb</c> L149), a plain data column at
    /// <c>roles.ascx</c> L50 under the header "Period".
    /// </remarks>
    // MIGRATION: the codes are LOAD-BEARING DATA - the literal bytes already sitting in this
    // column in every existing database - so the shared domain enumeration values its members at
    // those code points and no member is ever renamed or renumbered. Typing the member list is a
    // real strengthening: the constraint that once policed this column was dropped when the
    // dedicated lookup table was folded into the generic list table, so the terminal column
    // accepts any single character.
    // MIGRATION: null means NO CODE IS STORED and must never be conflated with the None member,
    // which is the real stored code 'N' meaning "does not expire". The legacy SQL relies on that
    // distinction: the terminal listing procedure gates the whole trial group behind
    // "case when R.TrialFrequency <> 'N'", which only works because 'N' is a value present in the
    // column rather than its absence. Collapsing the two would break trial-period selection.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Fee charged for the trial period, or <see langword="null"/> when the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialFee money NULL</c>, added at <c>01.00.08.SqlDataProvider</c>
    /// L6830 and never altered, so unlike its billing counterpart it was fixed-point from birth
    /// and never passed through a superseded decimal declaration; the last procedure signature
    /// declares the parameter <c>money</c> at <c>04.00.04.SqlDataProvider</c> L462. Legacy member
    /// <c>RoleInfo.TrialFee</c>, declared <c>As Single</c> (<c>RoleInfo.vb</c> L233), bound
    /// through <c>FormatPrice</c> by the "Trial" template column at <c>roles.ascx</c> L53 and L55.
    /// </remarks>
    // MIGRATION: decimal for the same currency reason as the billing fee. Note that the terminal
    // listing procedure gates this member on the trial CODE rather than on the fee -
    // "case when R.TrialFrequency <> 'N' then R.TrialFee else null end" - so null here follows
    // from the absence of a trial, not from the fee being zero.
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial units before the first charge, or <see langword="null"/> when the role
    /// offers no trial. It is a multiplier over <see cref="TrialFrequency"/> and is meaningless
    /// alone.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialPeriod int NULL</c>, present in the baseline table at
    /// <c>01.00.00.SqlDataProvider</c> L121, restated when the table was recreated at
    /// <c>01.00.04.SqlDataProvider</c> L1328 and <c>01.00.05.SqlDataProvider</c> L2754, and never
    /// altered afterwards. Legacy member <c>RoleInfo.TrialPeriod</c>, declared <c>As Integer</c>
    /// (<c>RoleInfo.vb</c> L203), bound through <c>FormatPeriod</c> by the second "Every" template
    /// column at <c>roles.ascx</c> L58 and L60. A nullable integer on the same evidence as the
    /// billing period, and never text.
    /// </remarks>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Unit the trial period is counted in, or <see langword="null"/> when no code is stored
    /// against the role. Null is never the same thing as the <c>None</c> member, for the reason
    /// given on <see cref="BillingFrequency"/>.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialFrequency char(1) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L122, never altered). Legacy member
    /// <c>RoleInfo.TrialFrequency</c>, declared <c>As String</c> (<c>RoleInfo.vb</c> L188), a
    /// plain data column at <c>roles.ascx</c> L63 under the header "Period".
    /// </remarks>
    // MIGRATION: this member deliberately reuses the SAME shared enumeration as its billing
    // counterpart, and no trial-specific type exists. The schema settles it: one frequency lookup
    // is joined twice from a single role row, once per column, in both eras of the chain - early
    // against the dedicated lookup table's code, and terminally as two joins onto the same generic
    // 'Frequency' list. Both columns draw from one shared code set, so one type covers them both.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// Whether the role is publicly visible, so that a user may subscribe to it themselves rather
    /// than being assigned to it by an administrator.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.IsPublic bit NOT NULL DEFAULT 0</c>, added with its default
    /// constraint at <c>01.00.08.SqlDataProvider</c> L6831 and re-asserted under qualifier
    /// templating at <c>03.01.01.SqlDataProvider</c> L1174, whose L1179 restores the default.
    /// Legacy member <c>RoleInfo.IsPublic</c>, a <c>Boolean</c> (<c>RoleInfo.vb</c> L248), bound
    /// by the "Public" template column at <c>roles.ascx</c> L66-L69, which chose between two
    /// images. Non-nullable on purpose: the column is NOT NULL with a default, so a nullable
    /// boolean would invent a third state the database cannot hold.
    /// </remarks>
    // MIGRATION: the wire type changes from text to a real boolean. The terminal listing procedure
    // projected this column as the words True and False, and the legacy grid then compared that
    // text against a lower-case literal to choose an image (roles.ascx L68-L69) - a case-sensitive
    // comparison that was a latent defect rather than a feature. Carrying a boolean and letting
    // the client render it removes the comparison instead of preserving a fragile one.
    public bool IsPublic { get; set; }

    /// <summary>
    /// Whether the role is granted automatically to every new user of the portal.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.AutoAssignment bit NOT NULL DEFAULT 0</c>, added with its default
    /// constraint at <c>01.00.08.SqlDataProvider</c> L6832 and re-asserted under qualifier
    /// templating at <c>03.01.01.SqlDataProvider</c> L1175, whose L1181 restores the default.
    /// Legacy member <c>RoleInfo.AutoAssignment</c>, a <c>Boolean</c> (<c>RoleInfo.vb</c> L263),
    /// bound by the "Auto" template column at <c>roles.ascx</c> L72-L75. Non-nullable, and
    /// text-to-boolean, for exactly the reasons given on <see cref="IsPublic"/>.
    /// </remarks>
    public bool AutoAssignment { get; set; }
}
