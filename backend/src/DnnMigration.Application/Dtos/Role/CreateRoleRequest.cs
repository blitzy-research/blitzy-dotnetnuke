using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/roles</c>: everything a caller may
/// supply when creating a security role, including its paid-membership terms.
/// </summary>
/// <remarks>
/// <para>
/// The member set is the legacy CREATE SCREEN's, not the legacy entity's. Each of the thirteen
/// members below is backed by an input control on <c>Website/admin/Security/editroles.ascx</c> - the
/// markup file name is lower-case while the code-behind beside it is capitalised - and the legacy
/// save path corroborates the set exactly: <c>EditRoles.ascx.vb</c> L232-L248 assigns these
/// thirteen values, and only these, onto a freshly constructed legacy role before handing it to
/// <c>RoleController.vb</c> L100. Declaration order mirrors <see cref="UpdateRoleRequest"/> so the
/// two contracts read side by side; order carries no meaning on the wire.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no guard. Field rules live in
/// <c>Application/Validation/CreateRoleRequestValidator.cs</c>, which reproduces the legacy screen's
/// nine declarative validators - one required-field and eight comparison - so a malformed field is a
/// field-level 400 rather than a failure raised from here. Translation onto the persisted model lives
/// in <c>Application/Mapping/RoleMappings.cs</c>. The two rules that need a database read - the
/// portal-scoped uniqueness of the name, and the existence of the nominated group within the owning
/// portal - live in <c>Application/Services/RoleService.cs</c> and are reported as the expected
/// failures <c>role.name_duplicate</c> and <c>role_group.not_found</c>. Both frequency members
/// travel as the legacy single character rather than as a member name or a number, applied centrally
/// by a converter registered at the API edge, so no serialisation attribute appears on any member.
/// </para>
/// </remarks>
// MIGRATION: the role's own identifier is absent, deliberately, and must not be added even as a
// nullable member. The legacy screen carried one because a single postback served both creating and
// editing, and it overloaded -1 as its add-versus-edit switch (EditRoles.ascx.vb L131 and L251); the
// migrated design has no such ambiguity, because creating and editing are distinct routed endpoints.
// The column is an identity column the store assigns and the legacy creating member RETURNS
// (RoleController.vb L100, L107), so accepting one from a caller would let it nominate an identifier
// the store owns - an identity-injection vector with no legacy precedent.
//
// MIGRATION: the owning portal is absent for the same class of reason, and this one is a tenant
// boundary. RoleInfo.PortalID was assigned from ambient page state at EditRoles.ascx.vb L234 and
// no control posted it. Here the portal is resolved from the request host into the scoped portal
// context before the flat /api/v1/roles action runs. Accepting it in the body as
// well would create a second, contradictable source of truth for the tenant, which is the one
// value a multi-tenant write must never let a caller restate - a cross-tenant write vector.
//
// MIGRATION: no assignment members of any kind, and no derived membership classification. A role
// definition and a user's membership OF a role are different things: the membership's validity
// window, trial-consumed flag, subscription flag, own identifier and owning user are all columns of
// dbo.UserRoles, none of them is among the fifteen properties of RoleInfo.vb, and no control on
// editroles.ascx posted one. They belong to the sibling assignment contract in this folder. A
// classification would additionally oblige this type to compute, which it must never do.
//
// MIGRATION: the legacy XML serialisation attributes are all dropped. RoleInfo.vb decorated its class
// and twelve of its fifteen properties for the portal-template export, a mechanism this migration
// does not carry. Member names alone express the wire contract now.
//
// MIGRATION: no inheritance and no shared base type, even though the sibling update contract declares
// a near-identical member set. The legacy tree demonstrates the cost of the alternative: UserRoleInfo
// inherits RoleInfo, so a type describing an eight-column assignment presents twenty-three effective
// properties and a reader cannot tell which the assignment owns. Every contract in this folder
// declares its own members outright, and the duplication is intentional.
public sealed class CreateRoleRequest
{
    /// <summary>
    /// Name of the role. Required, and unique within the owning portal.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleName</c>, capped at fifty characters by its
    /// <c>MaxLength</c> and carrying the screen's one required-field validator <c>valRoleName</c>
    /// (<c>editroles.ascx</c> L27, L29). Terminal column
    /// <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117), covered
    /// together with the portal column by the uniqueness constraint <c>IX_RoleName</c>
    /// (<c>03.00.09.SqlDataProvider</c> L304).
    /// </remarks>
    // MIGRATION: non-nullable, and initialised to the empty string rather than to a null-forgiving
    // default, because the column is NOT NULL and a request that omits the name is a MISSING
    // required field rather than a null one. Neither rule on this member is enforced here: the
    // fifty-character ceiling is a schema fact the validator reproduces declaratively, and
    // uniqueness needs a read, so the service reports it - reproducing the legacy
    // lookup-then-insert guard at EditRoles.ascx.vb L252 and its DuplicateRole message at L256.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at a thousand
    /// characters, with no validator (<c>editroles.ascx</c> L39). Terminal column
    /// <c>Roles.Description nvarchar(1000) NULL</c> (<c>01.00.00.SqlDataProvider</c> L118).
    /// </remarks>
    // MIGRATION: null and the empty string stay distinct on this contract, and nothing here converts
    // either into the other. The legacy absent value for a text member was the EMPTY STRING, not
    // null - Null.vb L71-L73 returns "" from its string sentinel - so a legacy write could not
    // express "no description" at all, and a legacy reader turned a SQL null into "" on the way past.
    // Which of the two reaches the column is the mapper's decision, and no attribute or coercion here
    // pre-empts it.
    public string? Description { get; set; }

    /// <summary>
    /// Recurring fee charged for membership of the role, or <see langword="null"/> when the role is
    /// free.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtServiceFee</c>, validated as a currency and constrained to
    /// zero or more (<c>editroles.ascx</c> L89-L96). Terminal column
    /// <c>Roles.ServiceFee money NULL</c> with the store default <c>DEFAULT (0)</c>
    /// (<c>03.01.01.SqlDataProvider</c> L1173, L1177). A free role leaves this member and the two
    /// billing terms below meaningless: the legacy screen wrote a billing term only when a fee had
    /// been entered (<c>EditRoles.ascx.vb</c> L216), and the terminal projection returns one only
    /// when the stored fee is non-zero.
    /// </remarks>
    // MIGRATION: decimal, not a floating-point type, and this member is the plainest demonstration in
    // this folder of why only the schema's TERMINAL state may be consulted. Three sources disagree:
    // the legacy property was declared As Single (RoleInfo.vb L164) and the save path parsed the box
    // with Single.Parse (EditRoles.ascx.vb L217), while the BASELINE column was decimal(5, 2)
    // (01.00.00.SqlDataProvider L119), capping a fee at 999.99. Neither survives - the destructive
    // chain retypes the column to money twice (01.00.04 L1326 converting values at L1341, and
    // 01.00.05 L2752) and settles it terminally at 03.01.01 L1173. SQL money is fixed-point, so
    // decimal is its faithful CLR counterpart, whereas a float would reintroduce binary rounding into
    // a currency amount; stopping at the baseline would have yielded both the wrong type and a
    // precision ceiling that has not applied for many versions. The user interface corroborates the
    // fixed-point reading independently, rendering the fee to exactly two decimals.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing-frequency units between charges, or <see langword="null"/> when the role
    /// carries no recurring billing term.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtBillingPeriod</c>, validated as an integer and constrained to
    /// strictly more than zero (<c>editroles.ascx</c> L104-L114). Terminal column
    /// <c>Roles.BillingPeriod int NULL</c>, added by <c>01.00.08.SqlDataProvider</c> L6829 with
    /// existing rows backfilled to one at L6900.
    /// </remarks>
    // MIGRATION: int, not text, resolving a three-against-one disagreement - the legacy property is
    // As Integer (RoleInfo.vb L218), the save path parses with Integer.Parse, the column is int NULL,
    // and only the legacy provider signature spelled the value as text.
    //
    // MIGRATION: null is load-bearing here and is NOT interchangeable with zero. It is the LITERAL
    // legacy value for a free role rather than a modernisation, because the terminal projection emits
    // it: 'BillingPeriod' = case when convert(int,Roles.ServiceFee) <> 0 then Roles.BillingPeriod
    // else null end (01.00.08.SqlDataProvider L7024, L7054). A caller sending null asks for no
    // billing term; a caller sending zero asks for a term of zero units, which cannot advance an
    // expiry. The legacy expiry derivation short-circuits to no expiry when the period equals the
    // integer null sentinel of -1 (RoleController.vb L537 against Null.vb L41-L43), and reproducing
    // that arithmetic belongs to the Application service, never to this type.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Unit of the recurring billing term, or <see langword="null"/> when the role carries none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboBillingFrequency</c>, bound to the portal's frequency
    /// list and pre-selected to the never code (<c>editroles.ascx</c> L106,
    /// <c>EditRoles.ascx.vb</c> L119-L121). Terminal column
    /// <c>Roles.BillingFrequency char(1) NULL</c> (<c>01.00.05.SqlDataProvider</c> L2753).
    /// </remarks>
    // MIGRATION: the single-character codes are LOAD-BEARING DATA and are never renamed. The legacy
    // property surfaced the column as text (RoleInfo.vb L149) over a char(1) column; this contract
    // uses the shared Domain enumeration, whose members carry those very characters as their
    // underlying values, so a member converts to and from the persisted character without loss. There
    // are SIX codes, not four - N, O, D, W, M and Y - on three independent measurements: the legacy
    // frequency switch enumerates all six (RoleController.vb L540-L546), the upgrade chain rewrites
    // the lookup table's six numeric codes onto exactly those letters (01.00.08 L6842-L6889), and the
    // legacy property's own documentation lists all six.
    //
    // MIGRATION: ONE enumeration serves this member and the trial frequency below. The legacy queries
    // resolved both columns against a SINGLE frequency lookup, joined twice from one role row, and
    // still twice against the generic list table after that lookup was retired. Note that the
    // terminal schema carries NO foreign key and no check constraint on either frequency column -
    // 03.00.01.SqlDataProvider L1297 drops the constraint and L1300 drops the lookup table - so
    // confining the two members to the six codes is the application's responsibility, discharged by
    // the validator.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Fee charged for the trial period, or <see langword="null"/> when the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialFee</c>, validated as a currency and constrained to zero
    /// or more (<c>editroles.ascx</c> L122-L128). Terminal column <c>Roles.TrialFee money NULL</c>,
    /// added already typed as money by <c>01.00.08.SqlDataProvider</c> L6830.
    /// </remarks>
    // MIGRATION: decimal for the same reason as the service fee, though from a shorter history: this
    // column was money from birth, yet the legacy property was still As Single (RoleInfo.vb L233).
    // The terminal projection returns this member only for a role whose stored trial frequency is not
    // the never code (01.00.08.SqlDataProvider L7026), so null is the value a role without a trial
    // both stores and reports.
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial-frequency units the trial runs for, or <see langword="null"/> when the role
    /// offers no trial.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialPeriod</c>, validated as an integer and constrained to
    /// strictly more than zero (<c>editroles.ascx</c> L136-L146). Terminal column
    /// <c>Roles.TrialPeriod int NULL</c> (<c>01.00.05.SqlDataProvider</c> L2754).
    /// </remarks>
    // MIGRATION: int on the same three-against-one evidence as the billing period (legacy property
    // RoleInfo.vb L203), and null is equally load-bearing, because the terminal projection returns
    // this member only when the stored trial frequency is not the never code (01.00.08 L7027).
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Unit of the trial term, or <see langword="null"/> when the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboTrialFrequency</c>, bound to the same frequency list as
    /// the billing dropdown and pre-selected to the never code (<c>editroles.ascx</c> L138,
    /// <c>EditRoles.ascx.vb</c> L123-L125). Terminal column
    /// <c>Roles.TrialFrequency char(1) NULL</c> (<c>01.00.05.SqlDataProvider</c> L2755).
    /// </remarks>
    // MIGRATION: the never code is a REAL STORED VALUE meaning "no trial", not an unset marker, and
    // the two must stay distinct in a reader's mind. The legacy assignment path decides whether the
    // trial terms or the billing terms govern an expiry by testing this column against that code
    // (RoleController.vb L521), and the terminal projections gate the trial fee, the trial period and
    // this member behind the same test (01.00.08 L7026-L7028).
    //
    // MIGRATION: this member deliberately carries NO initialiser, so an omitted trial frequency
    // arrives as null - stated here because the legacy default was NOT null: the screen pre-selected
    // the never code in both frequency dropdowns and its save path substituted that code, a zero fee
    // and a period of one whenever a block was left empty (EditRoles.ascx.vb L212-L214, L222-L224).
    // Three reasons prefer null even so. It keeps this member symmetrical with the billing frequency
    // above. It keeps the type inert, where an initialiser would additionally make an OMITTED member
    // behave differently from an explicitly null one. And it costs nothing observable, because null
    // and the never code are already treated identically downstream: the service lets the trial terms
    // govern an expiry only when this member holds a code other than the never code, and in SQL the
    // projections' TrialFrequency <> 'N' test is UNKNOWN for a null and falls to the same else branch.
    // Applying the legacy default, should a later reader want the stored character in place of a SQL
    // null, is the service's decision and not this type's.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// <see langword="true"/> when users may subscribe to the role themselves.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkIsPublic</c>, with no validator (<c>editroles.ascx</c> L56).
    /// Terminal column <c>Roles.IsPublic bit NOT NULL</c> with the store default zero
    /// (<c>01.00.08.SqlDataProvider</c> L6831, retyped and re-defaulted at
    /// <c>03.01.01.SqlDataProvider</c> L1174 and L1179).
    /// </remarks>
    // MIGRATION: plainly non-nullable, because the column is NOT NULL, a checkbox always posts a
    // definite state, and the legacy property was likewise As Boolean (RoleInfo.vb L248). The CLR
    // default of false agrees with the store default of zero, and that agreement is what makes a
    // non-nullable member safe: a request that omits it creates a private role, exactly as the legacy
    // screen did from an unchecked box.
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every existing user of the portal in the role as it is
    /// created.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkAutoAssignment</c>, with no validator
    /// (<c>editroles.ascx</c> L64). Terminal column <c>Roles.AutoAssignment bit NOT NULL</c> with the
    /// store default zero (<c>01.00.08.SqlDataProvider</c> L6832, retyped and re-defaulted at
    /// <c>03.01.01.SqlDataProvider</c> L1175 and L1181).
    /// </remarks>
    // MIGRATION: non-nullable on the same reasoning as the public flag (RoleInfo.vb L263). Its
    // CONSEQUENCE, however, is unlike that of any other member here: setting it makes the service
    // enrol the portal's existing members as part of the same create, so the operation writes
    // membership rows as well as the role itself. That reproduces the legacy creating member, which
    // invoked its auto-assign helper immediately after a successful insert (RoleController.vb L106).
    // The behaviour is the service's; this member only carries the caller's intent.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Identifier of the role group to file the role under, or <see langword="null"/> to leave it
    /// ungrouped - the case the legacy screen presented as "Global Roles".
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboRoleGroups</c>, with no validator
    /// (<c>editroles.ascx</c> L47). Terminal column <c>Roles.RoleGroupID int NULL</c>, added with a
    /// foreign key to <c>RoleGroups</c> by <c>03.02.03.SqlDataProvider</c> L34 and L37 - and again by
    /// <c>04.00.04.SqlDataProvider</c> L67 and L70, for an installation upgrading from a different
    /// baseline. <see langword="null"/> means ungrouped, and nothing else.
    /// </remarks>
    // MIGRATION: nullable, which reconciles what looks like a contradiction between the legacy user
    // interface and the legacy schema but is not one. The interface treated -1 as a real, selectable
    // choice - the group-binding helper adds it as the drop-down's first entry, labelled from the
    // localised "GlobalRoles" resource, and the save path parses the selection straight onto the
    // legacy property (EditRoles.ascx.vb L236) - yet -1 can never reach the column, because
    // RoleGroups.RoleGroupID is an identity column seeded at zero and NOT NULL
    // (03.02.03.SqlDataProvider L18) and the foreign key at L37 would reject the value. The two facts
    // are one fact seen from two layers: the legacy reader turned a SQL null into -1 on the way past
    // (Null.vb L41-L43) and the provider turned -1 back into a SQL null on the way in. So -1, "Global
    // Roles" and SQL null are a single value, and null is its honest representation here.
    //
    // Three consequences, each a real defect if ignored.
    //   * ZERO IS A LEGITIMATE GROUP, because RoleGroups.RoleGroupID is seeded at zero. Never test
    //     this member for absence by comparing it against zero or a non-positive range; ask whether
    //     it has a value.
    //   * THE ALL-ROLES FILTER VALUE MUST NEVER APPEAR HERE. The LISTING screen's group filter adds a
    //     further, more negative entry labelled from the localised "AllRoles" resource
    //     (Roles.ascx.vb L112). It is transient query state meaning "do not filter", it is never
    //     stored, and it belongs to the listing query contract.
    //   * DO NOT CONFLATE THIS WITH THE PERMISSION PSEUDO-PRINCIPALS. In the module and tab permission
    //     tables a ROLE identifier of -1 means all users, and the two further negative values mean a
    //     superuser and an unauthenticated visitor; there those negatives are real principals and must
    //     NEVER be mapped to null. That is a separate rule about a different column, and this folder
    //     declares no permission-bearing contract. Both rules hold, and merging them is a bug.
    //
    // Normalising a submitted -1 into a null, should a legacy client send one, is the service's
    // decision and not this type's. The service additionally reports a group that does not exist in
    // the owning portal as role_group.not_found.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Code a user may redeem to be granted the role, or <see langword="null"/> when the role has
    /// none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRSVPCode</c>, capped at fifty characters and with no
    /// validator (<c>editroles.ascx</c> L153). Terminal column
    /// <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45). Nothing in the
    /// schema makes the code unique, so it is not an identifier and a clash is not a conflict.
    /// </remarks>
    // MIGRATION: the derived invitation URL that sat beside this box on the legacy screen is
    // deliberately absent. The markup declared a companion read-only box, txtRSVPLink, which the
    // code-behind filled FOR DISPLAY ONLY from the request's own domain name, the default page and
    // this code (EditRoles.ascx.vb L165-L167); the save path at L247 writes only the code, and no
    // corresponding column exists in the eighty-eight upgrade scripts. It could not be carried here
    // in any case: composing it needs ambient request state this layer must never see, and computing
    // it in a getter would make an inert data carrier do work. The client composes the URL from this
    // code and its own origin.
    //
    // MIGRATION: the legacy absent value for this member was the EMPTY STRING rather than null - the
    // code-behind tests it with <> "" at EditRoles.ascx.vb L166, the string sentinel of Null.vb
    // L71-L73 in action - so a legacy row could not distinguish "no code" from "empty code". This
    // contract can; which form reaches the column is the mapper's decision. The member is spelled in
    // the target's casing convention while the column keeps its legacy spelling.
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Relative path of the image that represents the role, or <see langword="null"/> when it has
    /// none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>dnn:Url ctlIcon</c>, restricted by the code-behind to the portal's image
    /// file types and carrying no validator (<c>editroles.ascx</c> L169,
    /// <c>EditRoles.ascx.vb</c> L129). Terminal column <c>Roles.IconFile nvarchar(100) NULL</c>,
    /// added alongside the invitation code (<c>03.02.03.SqlDataProvider</c> L45).
    /// </remarks>
    // MIGRATION: carried as the supplied relative path, exactly as the legacy screen stored it -
    // EditRoles.ascx.vb L248 assigns the control's raw value with no transformation. The
    // hundred-character ceiling and the rejection of rooted and traversing forms are the validator's
    // rules, and resolving the path against the portal's home directory is the client's presentation
    // concern; neither is done here. As with the other two text members, the legacy absent value was
    // the empty string and the mapper owns the choice of what reaches the column.
    public string? IconFile { get; set; }
}
