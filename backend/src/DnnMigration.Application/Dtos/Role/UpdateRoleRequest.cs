using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>PUT /api/v1/roles/{roleId}</c>: the writable state of
/// an existing security role, including its paid-membership terms.
/// </summary>
/// <remarks>
/// <para>
/// The member set is the role's name plus the twelve values the legacy update path persisted. The
/// authority for those twelve is the <em>terminal</em> stored procedure rather than any screen or
/// class, because the destructive eighty-eight-script chain recreates <c>UpdateRole</c> seven times:
/// its final form at <c>04.00.04.SqlDataProvider</c> line 454 declares the role identifier plus
/// exactly those twelve, and its <c>UPDATE dbo.Roles SET ...</c> list names twelve columns. Each
/// member below names the input control on <c>Website/admin/Security/editroles.ascx</c> that posted
/// it - the markup file name is lower-case while the code-behind beside it is capitalised.
/// </para>
/// <para>
/// <b><c>RoleName</c> is carried, which is a documented behavioural difference from the legacy edit
/// screen.</b> That screen made the name read-only and its save path assigned a hidden textbox, so
/// the value posted on every edit was the empty string - dead in any case, because the legacy
/// membership contract declares no name parameter, the provider passes none, and the terminal
/// procedure omits the column from its assignment list. The name belongs here regardless: the
/// library-level contract this service replaces takes the whole role including its name on update
/// (<c>RoleController.vb</c> line 254), and the terminal schema constrains the pair with
/// <c>UNIQUE (PortalID, RoleName)</c> (<c>03.00.09.SqlDataProvider</c> line 304), so a collision has
/// to be a reportable outcome rather than a provider violation surfacing as a server fault. Sending
/// the stored name back unchanged is a no-op, so nothing the legacy screen could submit behaves
/// differently; only a rename is newly expressible, and that is itemised in
/// <c>MIGRATION_NOTES.md</c>.
/// </para>
/// <para>
/// <b>Neither identifier travels in the body.</b> The portal and the role both arrive in the route,
/// which is what makes them authoritative: a body value cannot contradict the route, and the tenant
/// in particular is never restatable by a caller. The role identifier is an ordinary <c>int</c> whose
/// value may legitimately be zero, because <c>dbo.Roles.RoleID</c> is seeded <c>IDENTITY (0, 1)</c>
/// (<c>01.00.00.SqlDataProvider</c> line 115); zero identifies the first role ever created and never
/// marks an absent one.
/// </para>
/// <para>
/// <b>This is a full replacement, not a partial edit.</b> Every member is applied as supplied, so
/// omitting a nullable member clears the stored value and omitting a non-nullable flag clears it to
/// false. A caller amending one field must read the role and resubmit the rest - the discipline the
/// legacy screen followed by loading the role into its form before posting it back. The name is no
/// exception: it is required, so a request that omits it is refused rather than silently leaving the
/// stored name in place.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no guard, no constructor. Field rules are
/// declared once by <c>Application/Validation/UpdateRoleRequestValidator.cs</c>, which shares every
/// rule with the creation validator through <c>Application/Validation/RoleTermsRules.cs</c> so the
/// two verbs cannot drift; a breach is reported as an RFC 7807 validation document naming the member.
/// <c>Application/Services/RoleService.cs</c> re-asserts the same shape for callers that do not
/// arrive over HTTP, raising <see cref="DnnMigration.Domain.Common.DomainException"/>, and owns the
/// two questions no field rule can answer because they need a read: whether the role exists, and
/// whether the submitted name already belongs to a different role in the same portal. The
/// cross-field gating the legacy screen performed - revealing the billing block only for a non-zero
/// fee and the trial block only for a trial frequency other than none - was a rendering decision
/// about which inputs were reachable, so no member here is conditional on another. Translation onto
/// the persisted model, including every decision about whether an empty string and a null are
/// interchangeable on a given column, lives in <c>Application/Mapping/RoleMappings.cs</c>. Both
/// frequency members travel as the legacy single character rather than as a member name or a number,
/// applied centrally by a converter registered at the API edge, so no serialisation attribute appears
/// on any member here.
/// </para>
/// </remarks>
// MIGRATION - DOCUMENTED BEHAVIOURAL DIFFERENCE: this contract can express a rename, and that places
// an obligation on the layers beneath it. The legacy screen guarded portal-scoped name uniqueness on
// its INSERT branch alone, which was coherent only for as long as the name could not change. A
// writable name therefore obliges the service to apply the same guard on this path, excluding the
// role being edited, and to report a collision as the same duplicate-name outcome the creation path
// reports - published as 409 by the API. That is not a tightening invented here: the terminal schema
// declares UNIQUE (PortalID, RoleName) at 03.00.09.SqlDataProvider L304, so without the guard a
// rename onto an existing name would surface as a server fault naming no field, which is strictly
// worse for a caller than a conflict it can correct.
//
// MIGRATION: the role's own identifier is absent and must not be added, not even as a nullable
// member. The legacy screen carried one because a single postback served both creating and editing,
// overloading minus one as its add-versus-edit switch; the migrated design has no such ambiguity
// because the two operations are distinct routed endpoints. Duplicating the identifier in the body
// would introduce a class of defect where the body disagrees with the route, and would hand a caller
// an identity-tampering vector no control on editroles.ascx ever offered. The owning portal is absent
// for the same class of reason, and that one is a tenant boundary: it arrives in the route and is
// resolved once per request into the scoped portal context by the alias-resolution middleware, so
// accepting it in the body as well would create a second, contradictable source of truth for the
// tenant - a cross-tenant write vector.
//
// MIGRATION: four groups of members are absent by design. The redemption LINK was never an input -
// the screen displayed it read-only, composed from the redemption code and the request's domain name,
// and no such column exists in the eighty-eight scripts; composing it needs the ambient web request
// this layer cannot see, and deriving it in a getter would breach the inertness rule, so the client
// composes it from the code and its own origin. ASSIGNMENT members belong to the sibling assignment
// contract in this folder, because a role definition and a user's membership of a role are different
// things: the validity window, trial-consumed flag, subscription flag and owning user are all columns
// of dbo.UserRoles. An OPTIMISTIC-CONCURRENCY member would be scope creep, since dbo.Roles carries no
// row version, entity tag or last-modified column in any script. The legacy XML SERIALISATION
// attributes are dropped with the portal-template export they served.
//
// MIGRATION: no inheritance and no shared base type, even though the sibling creation contract
// declares a near-identical member set. Deriving from it would smuggle in the very member this
// contract must not expose, and the legacy tree shows the wider cost: UserRoleInfo inherits RoleInfo,
// so a type describing an eight-column assignment presents twenty-three effective properties and a
// reader cannot tell which it owns. Every contract in this folder declares its own members outright.
public sealed class UpdateRoleRequest
{
    /// <summary>
    /// Replacement name of the role. Required, and unique within the owning portal.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleName</c>, capped at fifty characters by its
    /// <c>MaxLength</c> (<c>editroles.ascx</c> L27). Terminal column
    /// <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117), covered
    /// together with the portal column by the uniqueness constraint <c>IX_RoleName</c>
    /// (<c>03.00.09.SqlDataProvider</c> L304).
    /// </remarks>
    // MIGRATION: non-nullable, and initialised to the empty string rather than to a null-forgiving
    // default, because the column is NOT NULL and a request that omits the name is a MISSING required
    // field rather than a null one. Neither rule on this member is enforced here: the fifty-character
    // ceiling is a schema fact the validator reproduces declaratively, and uniqueness needs a read,
    // so the service reports it - excluding the role being edited from the comparison.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Replacement description of the role, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at one thousand
    /// characters (<c>editroles.ascx</c> L39). Terminal column
    /// <c>Roles.Description nvarchar(1000) NULL</c> (<c>01.00.00.SqlDataProvider</c> line 118).
    /// </remarks>
    // MIGRATION: nullable, and null is not normalised. The legacy absent value for a text column was
    // the empty string rather than null - the sentinel helper in Library/Components/Shared/Null.vb
    // L70-L74 returns "" for a null string, so a database null and an empty string were
    // indistinguishable once read. This contract keeps the two distinct and forces neither into the
    // other; the mapper owns that decision for every column at once.
    public string? Description { get; set; }

    /// <summary>
    /// Identifier of the role group to place the role in, or <see langword="null"/> to leave it
    /// ungrouped - the state the legacy screen labelled "Global Roles".
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboRoleGroups</c> (<c>editroles.ascx</c> L47). Terminal
    /// column <c>Roles.RoleGroupID int NULL</c>, added at <c>03.02.03.SqlDataProvider</c> line 34 and
    /// constrained by the foreign key at line 37 against <c>RoleGroups.RoleGroupID</c>, itself seeded
    /// <c>IDENTITY(0,1) NOT NULL</c> at line 18 of the same script.
    /// </remarks>
    // MIGRATION: nullable, and null carries the meaning the legacy screen encoded as minus one. The
    // group list offered that value under the caption "Global Roles" and the save path parsed it
    // straight onto the column, yet it could never be stored, because the referenced key is seeded
    // from zero and the foreign key would reject it: minus one was the presentation encoding of a
    // database null, translated in both directions by the legacy provider layer. This contract
    // expresses it once, as null.
    //
    // Two traps follow. Zero is a LEGITIMATE group identifier, because the referenced key is seeded
    // IDENTITY(0,1), so a non-positive test must never stand in for an absence test on this member.
    // And the roles list screen used a further negative value as a transient "all roles" list filter
    // (Roles.ascx.vb L112) that was never stored and must never reach this member; normalising a
    // legacy-encoded value a client may still send is the service's responsibility, not this type's.
    //
    // This rule must not be confused with the unrelated one governing permissions: in
    // dbo.ModulePermissions and dbo.TabPermissions a negative role identifier denotes a real
    // pseudo-principal - all users, superusers, unauthenticated users - which must NEVER be mapped to
    // null. Here, on Roles.RoleGroupID alone, the legacy encoding genuinely is a database null.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// <see langword="true"/> when users of the portal may subscribe to the role themselves.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkIsPublic</c> (<c>editroles.ascx</c> L56). Terminal column
    /// <c>Roles.IsPublic bit NOT NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6831), re-asserted
    /// as not-nullable with a store default of zero at <c>03.01.01.SqlDataProvider</c> lines 1174 and
    /// 1179.
    /// </remarks>
    // MIGRATION: not a nullable boolean, because the column is NOT NULL and the legacy checkbox
    // always posted a definite state. Since this contract replaces rather than patches, a request
    // that omits the member sets the role private rather than leaving it as it was; that is the
    // conservative direction, but it is still a change, so a partial edit must resubmit the value.
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every user of the portal in the role automatically.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkAutoAssignment</c> (<c>editroles.ascx</c> L64). Terminal
    /// column <c>Roles.AutoAssignment bit NOT NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6832),
    /// re-asserted as not-nullable with a store default of zero at
    /// <c>03.01.01.SqlDataProvider</c> lines 1175 and 1181.
    /// </remarks>
    // MIGRATION: not a nullable boolean, for the same reason as the flag above, so omitting it clears
    // it. The legacy asymmetry is preserved rather than smoothed over: enabling the flag during an
    // edit did not retrospectively enrol existing users, and disabling it did not remove those
    // already enrolled. Existing memberships are managed through the assignment contract.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Replacement recurring fee for membership of the role, or <see langword="null"/> when the role
    /// carries no fee.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtServiceFee</c>, carrying a currency type check and a
    /// not-negative comparison (<c>editroles.ascx</c> L89 to L96). Terminal column
    /// <c>Roles.ServiceFee money NULL</c> with store default zero
    /// (<c>03.01.01.SqlDataProvider</c> lines 1173 and 1177).
    /// </remarks>
    // MIGRATION: a fixed-point decimal, not a floating-point type, and the terminal schema decides
    // that. The legacy property was a single-precision float (RoleInfo.vb L164) and the screen parsed
    // the field as one, so both legacy sources agree with each other and are both superseded: the
    // baseline decimal(5, 2) column was widened by the destructive chain to money, which is
    // fixed-point and maps to decimal. Reproducing either legacy type would silently reintroduce
    // binary rounding into a monetary value.
    //
    // A free role leaves the billing members below meaningless - the legacy screen revealed its
    // billing block only for a non-zero fee, and the terminal projection blanks those members for a
    // role whose fee converts to zero - but that gating is the validator's and the service's concern,
    // so no member here is conditional on another.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Replacement count of billing-frequency units between charges, or <see langword="null"/> for
    /// no recurring billing term.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtBillingPeriod</c>, carrying an integer type check and a
    /// strictly-positive comparison (<c>editroles.ascx</c> L104 to L114). Terminal column
    /// <c>Roles.BillingPeriod int NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6829, with existing
    /// rows backfilled to one at lines 6900 to 6901).
    /// </remarks>
    // MIGRATION: an integer, not the string the legacy provider signature declared for this one
    // argument, and the evidence is three-to-one: the column is int, the legacy property was an
    // Integer (RoleInfo.vb L218), and the screen validated and parsed the field as one. Null is the
    // literal legacy value rather than a modernisation, because the terminal projection emits SQL
    // null for this column whenever the fee converts to zero - a free role genuinely carried no
    // period.
    //
    // Null and zero are different instructions. The legacy expiry derivation short-circuited to no
    // expiry when the period equalled the null-integer sentinel, before the frequency code was
    // examined at all (RoleController.vb L537-L538), whereas zero would have been carried into the
    // arithmetic. That arithmetic belongs to the service layer.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Replacement unit of the recurring billing term, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboBillingFrequency</c> (<c>editroles.ascx</c> L106).
    /// Terminal column <c>Roles.BillingFrequency char(1) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2753).
    /// </remarks>
    // MIGRATION: the legacy property surfaced this column as a bare string (RoleInfo.vb L149), which
    // left every caller free to submit an unrecognised character; the shared Domain enumeration
    // replaces it, carrying each legacy single-character code as its underlying value so a member
    // converts to and from the persisted character without loss. Those characters ARE the contract
    // and are never renamed: they are the literal bytes already sitting in this column in every
    // existing database, so a renumbered member would not fail a build, it would silently mis-read
    // live rows. Enforcing the closed set is now the application's duty, because the lookup table and
    // the foreign key that once guaranteed it were both dropped by 03.00.01.SqlDataProvider, leaving
    // the terminal column with no constraint at all.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Replacement one-off fee for the trial period, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialFee</c>, carrying a currency type check and a
    /// not-negative comparison (<c>editroles.ascx</c> L122 to L128). Terminal column
    /// <c>Roles.TrialFee money NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6830).
    /// </remarks>
    // MIGRATION: a fixed-point decimal for the same reason as the recurring fee, though this column
    // was money from the moment it was introduced, so only the legacy single-precision property
    // (RoleInfo.vb L233) is superseded.
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Replacement count of trial-frequency units the trial runs for, or <see langword="null"/> for
    /// no trial term.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialPeriod</c>, carrying an integer type check and a
    /// strictly-positive comparison (<c>editroles.ascx</c> L136 to L146). Terminal column
    /// <c>Roles.TrialPeriod int NULL</c> (<c>01.00.05.SqlDataProvider</c> line 2754).
    /// </remarks>
    // MIGRATION: an integer on the same evidence as the billing period - column, legacy property
    // (RoleInfo.vb L203) and screen validator all agree - and null again means no term rather than a
    // zero-length one.
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Replacement unit of the trial term, or <see langword="null"/> for none. The enumeration's
    /// "none" member is a real stored code meaning the role has no trial, and is not the same thing
    /// as this member being <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboTrialFrequency</c> (<c>editroles.ascx</c> L138).
    /// Terminal column <c>Roles.TrialFrequency char(1) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2755).
    /// </remarks>
    // MIGRATION: typed with the SAME enumeration as the billing unit above, because the schema proves
    // the two columns share one code set by joining the same frequency lookup twice from a single
    // role row. A second enumeration would duplicate a single source of truth and could drift.
    //
    // MIGRATION: no property initialiser, unlike the creation contract. The legacy screen defaulted
    // this list to the "none" code when ADDING a role, but when editing one it loaded the stored code
    // and gated the trial block's visibility on it, so defaulting here would overwrite a stored choice
    // whenever a caller omitted the member - on a replacement contract, exactly the wrong direction.
    //
    // The "none" code is load-bearing and must never be conflated with null: the legacy assignment
    // path tests this value against it to decide whether the trial term or the billing term governs
    // expiry (RoleController.vb L521), and the terminal projection applies the same test when blanking
    // a role's trial members.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// Replacement code a user may redeem to join the role, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRSVPCode</c>, capped at fifty characters and carrying no
    /// validator (<c>editroles.ascx</c> L153). Terminal column
    /// <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    // MIGRATION: nullable, with the legacy absent value again being the empty string rather than
    // null; the mapper owns that translation. The screen's companion read-only link field is not
    // represented here, for the reasons given on the class above.
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Replacement relative path of the role's icon, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Screen control <c>dnn:Url ctlIcon</c>, restricted by the screen to image file types
    /// (<c>editroles.ascx</c> L169, filter applied at <c>EditRoles.ascx.vb</c> L129). Terminal column
    /// <c>Roles.IconFile nvarchar(100) NULL</c> (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    // MIGRATION: carried as the stored relative path exactly as the legacy column held it, with
    // neither resolution nor rooting applied here. Length and the rejection of rooted or traversing
    // forms are the validator's rules, and turning the stored path into something a browser can
    // request is the client's concern.
    public string? IconFile { get; set; }
}
