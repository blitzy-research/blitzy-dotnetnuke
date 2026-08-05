using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// The complete persisted state of one security role, served by
/// <c>GET /api/v1/roles/{roleId}</c> and returned by the create and update
/// endpoints so a caller observes the stored result of its own write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member set is the legacy entity's, narrowed by one.</b> <c>RoleInfo.vb</c> declares exactly
/// fifteen public properties (L43-L57); fourteen appear below, and the owning-portal identifier is
/// the single omission. Nothing is added: every member here is a real terminal column on
/// <c>dbo.Roles</c>, and no member is a join denormalisation, a computed value or a count. That
/// fourteen-member boundary is deliberate, and the omission notes below record why each rejected
/// candidate stays out so a later reader does not reintroduce one as an apparent gap.
/// </para>
/// <para>
/// Contrast with the sibling contracts. <see cref="RoleListItemDto"/> is the grid projection and
/// carries eleven members, dropping the group identifier, the invitation code and the icon path
/// because the legacy listing screen showed none of them; this type is the editor projection and
/// carries all of those, because <c>Website/admin/Security/editroles.ascx</c> did. The editable
/// subsets are <see cref="CreateRoleRequest"/> and <see cref="UpdateRoleRequest"/>. This type is
/// declared independently of all three rather than inheriting from any of them.
/// </para>
/// <para>
/// The type is inert: no behaviour, no navigation property, no tracked state, no lazily evaluated
/// getter, no asynchronous member. Field rules belong to <c>CreateRoleRequestValidator</c> and
/// <c>UpdateRoleRequestValidator</c>, which share one rule definition in <c>RoleTermsRules.cs</c> so
/// the two write verbs cannot drift, and to <c>Application/Services/RoleService.cs</c> for the
/// questions a field rule cannot answer; between them they reproduce the one required-field and eight
/// compare validators the legacy screen declared. Translation to and from the persisted model belongs
/// to <c>Application/Mapping/RoleMappings.cs</c>.
/// </para>
/// </remarks>
// MIGRATION: every XML serialisation attribute is dropped. Legacy RoleInfo was decorated
// <XmlRoot("role", IsNullable:=False)> at RoleInfo.vb L42, with <XmlIgnore()> on the three identifier
// properties and a lower-case <XmlElement("...")> name on the remaining twelve; those attributes
// served the portal-template export, not an HTTP contract. Note that <XmlIgnore()> did NOT mean "not
// part of the contract": RoleID and RoleGroupID are both exchanged here, and only the portal
// identifier is withheld, for the separate reason below.
//
// MIGRATION: the owning-portal identifier is omitted. RoleInfo.vb L80 declares PortalID, and the
// column exists as Roles.PortalID int NULL (01.00.00.SqlDataProvider L115), but it was never a
// field of the legacy editor: EditRoles.ascx.vb L232 assigns it from ambient page state
// (objRoleInfo.PortalID = PortalId) and no control on editroles.ascx posts it. The migrated
// request resolves the portal from its host into the scoped portal context before
// /api/v1/roles/{roleId} runs, so echoing it back into the payload would add a redundant field that
// a client could be tempted to trust in place of the resolved context. The
// grid projection omits it for the same reason, so omitting it here also keeps the two role
// contracts consistent. A caller that needs to prove which portal owns a role should assert
// against the route that returned it, which is a stronger check than a self-reported field.
//
// MIGRATION: four further groups of members are absent by design.
//   * ASSIGNMENT members of any kind. Expiry date, effective date, trial-used flag, subscription flag
//     and the assignment identifier all describe one user's membership OF a role - they are columns of
//     dbo.UserRoles - and none is among RoleInfo's fifteen properties.
//   * A role STATUS. A role definition has none: the Pending/Active/Expired classification in
//     Domain/Enums/RoleStatus.cs has no legacy ancestor (no Status column exists on UserRoles) and is
//     computed from an assignment's date window against an injected clock. Carrying it here would
//     misplace the concept and oblige this type to compute, which it must never do.
//   * A GROUP NAME and a MEMBER TALLY. Both are join denormalisations with no column on dbo.Roles and
//     no precedent on the editor screen, which bound a drop-down of the portal's groups, so a client
//     holding that list resolves the name from RoleGroupId. Excluding them is also what keeps a
//     single-role read to a single-row query.
//   * AUDIT members. dbo.Roles has no created-by, created-date or last-modified column in the
//     terminal schema, so none is invented.
//
// MIGRATION: no inheritance, even though three sibling contracts overlap this one substantially. The
// legacy tree demonstrates the cost of the alternative: UserRoleInfo inherits RoleInfo, so a type
// describing an eight-column assignment presents twenty-three effective properties and a reader
// cannot tell which the assignment owns. Each contract in this folder declares its own members
// outright, and the duplication is intentional.
public sealed class RoleDetailDto
{
    /// <summary>
    /// Primary key of the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleID int IDENTITY (0, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L114); legacy member <c>RoleInfo.RoleID</c>
    /// (<c>RoleInfo.vb</c> L65). Non-nullable, because the column is: this contract only ever
    /// describes a persisted role.
    /// </remarks>
    // MIGRATION: identifier trap - never test this member for absence. The column is seeded
    // IDENTITY (0, 1), so the first role inserted carries identifier ZERO and zero is a perfectly
    // legitimate, addressable role; any guard treating zero, a non-positive range or the type default
    // as "absent" rejects a real role. Negative one is equally unusable: it is the legacy integer null
    // sentinel (Null.vb L41-L45) which the legacy editor additionally overloaded as its
    // add-versus-edit switch (EditRoles.ascx.vb L131, L251), yet the same schema seeds
    // Portals.PortalID IDENTITY (-1, 1), so -1 is a genuine identifier elsewhere in this very
    // database. No sentinel is carried forward: creation and update are distinct routed endpoints, so
    // the identifier never has to encode which operation is in flight.
    public int RoleId { get; set; }

    /// <summary>
    /// The role group this role is filed under, or <see langword="null"/> when the role belongs to
    /// no group - the case the legacy editor presented as "Global Roles".
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleGroupID int NULL</c>, added with a foreign key to
    /// <c>RoleGroups</c> by <c>03.02.03.SqlDataProvider</c> (and again by
    /// <c>04.00.04.SqlDataProvider</c> for an installation upgrading from a different baseline);
    /// legacy member <c>RoleInfo.RoleGroupID</c> (<c>RoleInfo.vb</c> L95). <see langword="null"/>
    /// means ungrouped, and nothing else.
    /// </remarks>
    // MIGRATION: nullable, resolving what looks like a contradiction between the legacy user
    // interface and the legacy schema. The editor treated -1 as a real, selectable choice - BindGroups
    // adds it as the drop-down's first entry, labelled from the localised "GlobalRoles" resource
    // (EditRoles.ascx.vb L75), and the save path parses it straight onto the property at L235 - yet -1
    // can never reach the column, because RoleGroups.RoleGroupID is itself IDENTITY(0,1) NOT NULL
    // (03.02.03.SqlDataProvider L18) and the foreign key referencing it would reject -1 outright. The
    // two facts are the same fact seen from two layers: the legacy reader turned database NULL into -1
    // on the way out and the provider turned -1 back into NULL on the way in. So -1, "Global Roles"
    // and SQL NULL are one value, and null is its honest representation here.
    //
    // Three consequences, each a live defect if ignored:
    //   * ZERO IS A LEGITIMATE GROUP, because RoleGroups.RoleGroupID is seeded IDENTITY(0,1). Never
    //     treat 0 as absence - use HasValue.
    //   * -2 MUST NEVER APPEAR HERE. Roles.ascx.vb L112 adds an "AllRoles" entry valued -2 to the
    //     LISTING screen's group filter: a transient query-side sentinel meaning "do not filter by
    //     group", never stored, belonging to the listing request contract rather than this response.
    //   * DO NOT CONFLATE THIS WITH THE PERMISSION PSEUDO-PRINCIPALS. In ModulePermissions and
    //     TabPermissions a RoleID of -1 means All Users, -2 Superuser and -3 Unauthenticated Users;
    //     there those negatives are real principals and must NEVER be mapped to null. That rule
    //     governs permission-bearing contracts and is entirely separate. Both rules are correct
    //     simultaneously, and collapsing them into one is a live bug.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Display name of the role, unique within its portal.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L117); legacy member <c>RoleInfo.RoleName</c>
    /// (<c>RoleInfo.vb</c> L110). Non-nullable to match the column, and initialised to the empty
    /// string so a freshly constructed instance is valid without a null-forgiving operator. The
    /// fifty-character ceiling and the mandatory-value rule are reproduced by the validators and the
    /// service - the legacy screen enforced them with <c>valRoleName</c> - and this contract asserts
    /// neither.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.Description nvarchar(1000) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L118); legacy member <c>RoleInfo.Description</c>
    /// (<c>RoleInfo.vb</c> L125).
    /// </remarks>
    // MIGRATION: the legacy read path could not produce null here, and that difference is deliberately
    // exposed rather than papered over. Null.vb L70-L74 defines the string sentinel as the EMPTY
    // STRING - literally `Return ""`, not Nothing - and every read funnelled through Null.SetNull, so a
    // database NULL and a stored empty description were indistinguishable once loaded and the screen
    // rendered an empty box for both. Modelling the nullable column honestly makes them
    // distinguishable for the first time. Deciding which of "" and null stands for an absent value on
    // the wire is RoleMappings' obligation - the same applies to RsvpCode and IconFile below - and no
    // serialisation attribute is placed here to force either, because coercing it silently at the
    // boundary is exactly what preserving a sentinel explicitly is meant to prevent.
    public string? Description { get; set; }

    /// <summary>
    /// The unit in which the billing cycle is measured, or <see langword="null"/> when no billing
    /// frequency is recorded.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.BillingFrequency char(1) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L120); legacy member <c>RoleInfo.BillingFrequency</c>,
    /// declared <c>As String</c> (<c>RoleInfo.vb</c> L149). Note that
    /// <see cref="BillingFrequency.None"/> is a real stored code meaning "never expires" and is
    /// emphatically not the same thing as <see langword="null"/>; the two must not be collapsed.
    /// </remarks>
    // MIGRATION: the legacy System.String is replaced by the shared domain enumeration. The single
    // characters are load-bearing DATA, not member names - they are the literal bytes in a char(1)
    // column, and RoleController.vb L540-L547 switched directly on them to advance an expiry date - so
    // the enumeration carries those exact code points and no member is ever renamed or renumbered.
    //
    // ONE enumeration serves BOTH this member and TrialFrequency, with deliberately no separate
    // trial-frequency type: the legacy SQL proves the columns share a single code set by resolving
    // both against the same lookup from one role row (`join CodeFrequency C1 on
    // Roles.BillingFrequency = C1.Code` alongside `left outer join CodeFrequency C2 on
    // Roles.TrialFrequency = C2.Code`).
    //
    // The measured code set has SIX members, not the four named in the plan: RoleController.vb
    // L540-L547 handles "N" (no expiry), "O" (one-time, perpetual - 9999-12-31), "D" days, "W" weeks,
    // "M" months and "Y" years. The two additional codes are reported as a refinement rather than
    // silently absorbed, and both are load-bearing; "N" in particular is a guard as well as a display
    // code, tested at RoleController.vb L521 to choose the trial period over the billing period and
    // again at EditRoles.ascx.vb L154 to decide whether to render the trial block at all.
    //
    // Expiry arithmetic is NOT performed here and must never be. The legacy calls came from the Visual
    // Basic runtime's date-advancing helper, imported exactly once in the whole in-scope tree
    // (RoleController.vb L25); that import is dropped and the migrated per-code arithmetic belongs to
    // Application/Services/RoleService.cs, where the outcome each code must reproduce is specified on
    // the domain enumeration itself. Note that the legacy weeks case advanced by a DAY interval
    // multiplied by seven, because the runtime offered no week interval, so the migrated form must
    // multiply days to stay equivalent.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// The recurring subscription fee for the role, or <see langword="null"/> when the role is
    /// free.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.ServiceFee money NULL DEFAULT (0)</c> - the terminal declaration,
    /// established by <c>03.01.01.SqlDataProvider</c> L1173 with its default at L1177; legacy member
    /// <c>RoleInfo.ServiceFee</c>, declared <c>As Single</c> (<c>RoleInfo.vb</c> L164).
    /// </remarks>
    // MIGRATION: decimal, and the type is resolved from the TERMINAL schema rather than from the
    // legacy property or the baseline column - three sources that disagree. The legacy property says
    // Single, which cannot represent a currency amount exactly and so cannot be carried forward
    // regardless of anything else. The BASELINE column says decimal(5, 2) (01.00.00.SqlDataProvider
    // L119), capping a fee at 999.99: stopping there would have produced both the wrong type and a
    // wrong precision claim - the destructive-chain trap in miniature. The TERMINAL column says money:
    // the table is rebuilt with `ServiceFee money NULL` at 01.00.04.SqlDataProvider L1326, explicitly
    // migrating existing data with CONVERT(money, ServiceFee) at L1341, again at 01.00.05 L2752, and
    // is finally fixed by ALTER COLUMN [ServiceFee] [money] NULL at 03.01.01 L1173, with every later
    // procedure signature agreeing. SQL money is fixed-point and maps to decimal, so the baseline
    // scale ceiling never applies; the interface corroborates it twice, validating the box as a
    // currency and formatting the value to two decimal places.
    //
    // Null is faithful, not a modernisation: the legacy Single sentinel was Single.MinValue (Null.vb
    // L51-L55), and the grid's FormatPrice helper (Roles.ascx.vb L175) rendered an absent fee as a
    // blank cell. No sentinel value is carried into this contract.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// The unit in which the trial period is measured, or <see langword="null"/> when no trial
    /// frequency is recorded.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialFrequency char(1) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L122); legacy member <c>RoleInfo.TrialFrequency</c>, declared
    /// <c>As String</c> (<c>RoleInfo.vb</c> L188). Typed with the very same
    /// <see cref="Domain.Enums.BillingFrequency"/> enumeration as <see cref="BillingFrequency"/> -
    /// see the note there for why one type serves both columns and why
    /// <see cref="BillingFrequency.None"/> must never be read as <see langword="null"/>.
    /// </remarks>
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// How many <see cref="TrialFrequency"/> units the trial spans, or <see langword="null"/> when
    /// the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialPeriod int NULL</c> (<c>01.00.00.SqlDataProvider</c> L121);
    /// legacy member <c>RoleInfo.TrialPeriod</c>, declared <c>As Integer</c> (<c>RoleInfo.vb</c>
    /// L203).
    /// </remarks>
    // MIGRATION: nullable integer - see the fuller note on BillingPeriod, which the same terminal
    // projection gates in the same way. The legacy screen agrees twice over: valTrialPeriod1 checks
    // the box as an integer, and EditRoles.ascx.vb L154 renders the trial block only when the trial
    // frequency is not the "N" code. A period equal to the legacy null-integer sentinel of -1 meant
    // "no expiry" outright (RoleController.vb L537), which null now expresses without colliding with
    // any real period.
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// How many <see cref="BillingFrequency"/> units one billing cycle spans, or
    /// <see langword="null"/> when the role is free.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.BillingPeriod int NULL</c>, added by
    /// <c>01.00.08.SqlDataProvider</c> L6829 and backfilled to 1 for existing rows at L6900; legacy
    /// member <c>RoleInfo.BillingPeriod</c>, declared <c>As Integer</c> (<c>RoleInfo.vb</c> L218).
    /// </remarks>
    // MIGRATION: nullable integer, resolving a genuine three-way disagreement - the legacy property
    // says Integer, a legacy provider signature says String, and the column says int NULL - four ways
    // against the string reading, which loses: the column is int NULL; the legacy property is As
    // Integer (RoleInfo.vb L218), as is TrialPeriod at L203; the legacy screen validates the box as an
    // integer; and, decisively, THE LEGACY DATA LAYER ITSELF PROJECTS SQL NULL HERE -
    //     'BillingPeriod' = case when convert(int,R.ServiceFee) <> 0 then R.BillingPeriod else null end
    // - so a free role reports a null billing period whatever the column holds. That projection is not
    // transitional: it appears from 01.00.08.SqlDataProvider L7024 and L7054, survives every
    // recreation of the procedure, and is still present terminally at 04.00.04 L286 and L394. Null is
    // therefore the LITERAL legacy wire value for a free role, not a modernisation of one. Corroborated
    // by the display gate at EditRoles.ascx.vb L146, which hides the billing block unless the fee
    // formats to something other than "0.00", and by the grid's FormatPeriod helper
    // (Roles.ascx.vb L152-L162), which rendered a -1 period as an empty cell.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// The fee charged for the trial period, or <see langword="null"/> when the trial is free or
    /// the role offers none.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.TrialFee money NULL</c>, added by
    /// <c>01.00.08.SqlDataProvider</c> L6830; legacy member <c>RoleInfo.TrialFee</c>, declared
    /// <c>As Single</c> (<c>RoleInfo.vb</c> L233).
    /// </remarks>
    // MIGRATION: decimal rather than the legacy Single, for the currency-exactness reason given on
    // ServiceFee. This column needed no correction along the way - declared money at birth and never
    // altered - so unlike ServiceFee there is no superseded baseline to discount.
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Whether users may subscribe themselves to the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.IsPublic bit NOT NULL DEFAULT 0</c>, added by
    /// <c>01.00.08.SqlDataProvider</c> L6831 and restated terminally by
    /// <c>03.01.01.SqlDataProvider</c> L1174 with its qualifier-templated default at L1179; legacy
    /// member <c>RoleInfo.IsPublic</c>, declared <c>As Boolean</c> (<c>RoleInfo.vb</c> L248).
    /// </remarks>
    // MIGRATION: non-nullable, and deliberately NOT bool?. The column is bit NOT NULL with a default
    // of 0 in the terminal schema, the legacy property is a plain Boolean, and the legacy boolean
    // sentinel was False rather than a third state (Null.vb L76-L80) - so there has never been an
    // "unknown" here to represent, and making it nullable would invent a state the schema forbids.
    public bool IsPublic { get; set; }

    /// <summary>
    /// Whether every new member of the portal receives the role automatically.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.AutoAssignment bit NOT NULL DEFAULT 0</c>, added by
    /// <c>01.00.08.SqlDataProvider</c> L6832 and restated terminally by
    /// <c>03.01.01.SqlDataProvider</c> L1175 with its qualifier-templated default at L1181; legacy
    /// member <c>RoleInfo.AutoAssignment</c>, declared <c>As Boolean</c> (<c>RoleInfo.vb</c> L263).
    /// The legacy concept name is preserved exactly.
    /// </remarks>
    // MIGRATION: non-nullable, for the same reason as IsPublic. The column is not merely decorative:
    // 01.00.08.SqlDataProvider sets it to 1 for the role named "Registered Users" immediately after
    // adding it, so a migrated installation arrives with the flag already meaningful on a well-known
    // role.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// The invitation code that lets a user self-assign the role.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.RSVPCode nvarchar(50) NULL</c>, added alongside <c>IconFile</c> by
    /// <c>03.02.03.SqlDataProvider</c>; legacy member <c>RoleInfo.RSVPCode</c> (<c>RoleInfo.vb</c>
    /// L278). The acronym is cased as a word to match the surrounding convention; the concept is
    /// unchanged, and the empty-string-versus-null caveat recorded on <see cref="Description"/>
    /// applies here too.
    /// </remarks>
    // MIGRATION: the derived invitation LINK is not carried, and this is the only member it concerned.
    // The legacy editor showed both: editroles.ascx L158 and L161 declare a plRSVPLink label and a
    // ReadOnly txtRSVPLink box, which EditRoles.ascx.vb L165-L167 composed for DISPLAY ONLY from the
    // request's host name, the default page and this code; the save path at L247 writes only the code,
    // and no RSVPLink column exists anywhere in the eighty-eight schema scripts. It is omitted for two
    // reasons beyond that absence: composing it requires the request's own host name, which is HTTP
    // state this layer must not touch, and computing it in a getter would break this type's guarantee
    // of being inert. The client composes it from this code and its own origin.
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Path of the role's icon.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Roles.IconFile nvarchar(100) NULL</c>, added alongside <c>RSVPCode</c> by
    /// <c>03.02.03.SqlDataProvider</c>; legacy member <c>RoleInfo.IconFile</c> (<c>RoleInfo.vb</c>
    /// L293). Bound on the legacy screen by the <c>ctlIcon</c> file picker, whose selected URL was
    /// assigned straight to the property at <c>EditRoles.ascx.vb</c> L248.
    /// </remarks>
    // MIGRATION: nullable string, with the same empty-string-versus-null caveat as Description,
    // resolved by RoleMappings rather than here. The legacy file picker was restricted to image types
    // by a host-level file-type list, which is configuration rather than a property of this contract,
    // so no such constraint is expressed on this member; the validator owns any rule about the value's
    // shape.
    public string? IconFile { get; set; }
}
