using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>PUT /api/v1/portals/{portalId}/roles/{roleId}</c>: the writable state of
/// an existing security role, including its paid-membership terms.
/// </summary>
/// <remarks>
/// <para>
/// The member set is the role's name plus the twelve values the legacy update path persisted. The
/// authority for those twelve is the terminal stored procedure rather than any single screen or class:
/// the destructive eighty-eight-script chain recreates <c>UpdateRole</c> seven times, and only its
/// final form is meaningful - at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider</c> line 454 it declares
/// thirteen parameters, the role identifier plus exactly those twelve, and its
/// <c>UPDATE dbo.Roles SET ...</c> list names twelve columns. Each member's documentation also names
/// the input control on <c>Website/admin/Security/editroles.ascx</c> that posted it - note the
/// lower-case markup file name; the code-behind beside it is capitalised - and the save block at
/// <c>Website/admin/Security/EditRoles.ascx.vb</c> lines L231 to L248 assigns the same set.
/// </para>
/// <para>
/// <b><c>RoleName</c> is carried, and this is a documented behavioural difference from the legacy
/// screen rather than an accident.</b> Five measurements establish what the legacy edit path did, and
/// they are recorded here so the difference is legible instead of implied. The edit screen made the
/// name read-only: at <c>EditRoles.ascx.vb</c> lines L131 to L134 it reveals a display label, hides
/// the name textbox and disables the screen's single required-field validator, then fills the label
/// from the stored value at L140 - yet its save block still assigned the hidden box at L237, and a
/// hidden Web Forms control posts nothing, so the value assigned on every edit was the empty string.
/// That store was dead because the layers beneath had nowhere to put it: the legacy membership data
/// contract declares no parameter for the name
/// (<c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c> line 97), the provider
/// implementing it never passes one
/// (<c>Library/Providers/MembershipProviders/DNNMembershipProvider/DNNRoleProvider.vb</c> line 325),
/// and the terminal procedure cited above omits the column from its assignment list, having carried it
/// as recently as <c>02.00.00.SqlDataProvider</c> line 4317 before the later recreation dropped it.
/// The screen's own portal-scoped uniqueness guard was consequently applied on the insert branch only:
/// at <c>EditRoles.ascx.vb</c> lines L251 to L257 the add branch looks the name up first, while the
/// edit branch updates with no such check.
/// </para>
/// <para>
/// Two facts settle why the member belongs here nonetheless. The library-level contract this
/// application service replaces takes the whole role INCLUDING its name on update -
/// <c>Library/Components/Security/Roles/RoleController.vb</c> line 254 is
/// <c>Public Sub UpdateRole(ByVal objRoleInfo As RoleInfo)</c> - so a name has always travelled at the
/// boundary the service layer occupies. And the terminal schema itself constrains the pair: the
/// uniqueness constraint over <c>(PortalID, RoleName)</c> added at
/// <c>03.00.09.SqlDataProvider</c> line 304 is a data-model fact this migration is required to honour,
/// so the collision it describes has to be a reportable outcome rather than a provider violation
/// surfacing as a server fault. A replacement contract that could not express the resource's own name
/// would also be dishonest about being a replacement. Sending the stored name back unchanged is a
/// no-op, so no submission the legacy screen could produce behaves differently; the difference is only
/// that a rename is now expressible, and it is itemised in <c>MIGRATION_NOTES.md</c>.
/// </para>
/// <para>
/// <b>Neither identifier travels in the body.</b> The portal and the role both arrive in the route,
/// which is what makes them authoritative: a value that does not exist cannot contradict the route,
/// so no reconciliation check is needed, and the tenant in particular is never restatable by a
/// caller. Note that the role identifier is an ordinary <c>int</c> whose value may legitimately be
/// zero: <c>dbo.Roles.RoleID</c> is seeded <c>IDENTITY (0, 1)</c> at
/// <c>01.00.00.SqlDataProvider</c> line 115, so zero identifies the first role ever created and is
/// never a marker for an absent one.
/// </para>
/// <para>
/// This is a full replacement, not a partial edit. Every member is applied as supplied, so omitting
/// a nullable member clears the stored value rather than preserving it, and omitting a non-nullable
/// flag clears it to false. A caller amending one field must read the role first and resubmit the
/// rest - the same discipline the legacy screen followed by loading the role into its form before
/// posting it back. The name is no exception: it is required, so a request that omits it is refused
/// rather than silently leaving the stored name in place.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no lazily evaluated getter, no guard and no
/// constructor. Field rules are declared once, at the boundary, by
/// <c>Application/Validation/UpdateRoleRequestValidator.cs</c>, which is public and is discovered by
/// the assembly scan in <c>Application/DependencyInjection.cs</c>, so the API's validation filter
/// resolves it and reports a breach as an RFC 7807 validation document naming the member. It shares
/// every rule with the creation validator through <c>Application/Validation/RoleTermsRules.cs</c>, so
/// the two verbs cannot drift apart. <c>Application/Services/RoleService.cs</c> re-asserts the same
/// shape for callers that do not arrive over HTTP, raising
/// <see cref="DnnMigration.Domain.Common.DomainException"/>, and owns the two questions no field rule
/// can answer because they need a read: whether the role exists, and whether the submitted name
/// already belongs to a different role in the same portal. The cross-field gating the legacy screen
/// performed - revealing the billing block only for a non-zero fee, and the trial block only for a
/// trial frequency other than none - was a rendering decision about which inputs were reachable, so no
/// member here is conditional on another. Translation onto the persisted model lives in
/// <c>Application/Mapping/RoleMappings.cs</c>, which also owns every decision about whether an empty
/// string and a null are interchangeable on a given column.
/// </para>
/// <para>
/// Wire form of the two frequency members. Both travel as the legacy single character rather than as
/// a member name or a number, applied centrally by a converter registered at the API edge, so no
/// serialisation attribute appears on any member here.
/// </para>
/// </remarks>
// MIGRATION - DOCUMENTED BEHAVIOURAL DIFFERENCE: this contract can express a rename, where the
// legacy edit screen could not. The measurements are recorded on the class summary above and are not
// repeated; what matters here is the obligation the member places on the layers beneath it, because
// the legacy asymmetry it removes is a real one. The legacy screen guarded portal-scoped name
// uniqueness on its INSERT branch alone (EditRoles.ascx.vb L251-L257, with no equivalent at
// L259-L261), which was coherent only for as long as the name could not change. Making the name
// writable therefore obliges the service to apply the same guard on this path, excluding the role
// being edited from the comparison, and to report a collision as the same duplicate-name outcome the
// creation path reports. That is not a tightening invented here: the terminal schema declares
// UNIQUE (PortalID, RoleName) at 03.00.09.SqlDataProvider L304, so without the guard a rename onto
// an existing name would reach the provider and surface as a server fault naming no field, which is
// strictly worse for a caller than a conflict it can correct. The obligation is discharged in
// Application/Services/RoleService.cs and the outcome is published as 409 by the API.
//
// MIGRATION: the role's own identifier is absent and must not be added, not even as a nullable
// member. The legacy screen carried one because a single postback served both creating and editing,
// and it overloaded the value minus one as its add-versus-edit switch - tested at
// EditRoles.ascx.vb L131 and again at L251. The migrated design has no such ambiguity, because
// creating and editing are distinct routed endpoints, so the operation is known from the route.
// Duplicating the identifier in the body would introduce a class of defect where the body disagrees
// with the route, and would hand a caller an identity-tampering vector that no control on
// editroles.ascx ever offered.
//
// MIGRATION: the owning portal is absent for the same class of reason, and this one is a tenant
// boundary. The legacy screen read it from ambient page state at EditRoles.ascx.vb L232 and no
// control posted it. Here it arrives in the route and is resolved once per request into the scoped
// portal context by the alias-resolution middleware. Accepting it in the body as well would create a
// second, contradictable source of truth for the tenant, which is a cross-tenant write vector.
//
// MIGRATION: the redemption link is absent, because it was never an input. The screen declared a
// read-only display field for it at editroles.ascx L161 with no validator, and the code-behind
// composed it at L165-L167 from the redemption code and the current request's domain name purely for
// display; the save path at L247 wrote only the code. No such column exists anywhere in the
// eighty-eight scripts. Composing it would require the ambient web request, which this layer has no
// access to by design, and deriving it in a getter would breach the rule that this type stays inert,
// so the client composes it from the code and its own origin.
//
// MIGRATION: no assignment members of any kind. A role definition and a user's membership OF a role
// are different things: the membership's validity window, its trial-consumed flag, its subscription
// flag, its own identifier and the user it belongs to are all columns of dbo.UserRoles, none of them
// appears among the fifteen properties of RoleInfo.vb, and none was posted by editroles.ascx. They
// belong to the sibling assignment contract in this folder.
//
// MIGRATION: no optimistic-concurrency member. Neither a row version, an entity tag nor a
// last-modified column exists on dbo.Roles in any of the eighty-eight scripts, so there is no legacy
// concurrency behaviour to preserve and inventing one would be scope creep.
//
// MIGRATION: the legacy XML serialisation attributes are all dropped. RoleInfo.vb decorated its
// class and most of its properties for the portal-template export, a mechanism this migration does
// not carry. Member names alone express the wire contract now.
//
// MIGRATION: no inheritance and no shared base type, even though the sibling creation contract
// declares a near-identical member set. Deriving from it would smuggle in the very member this
// contract must not expose, and the legacy tree shows the wider cost of that shortcut: UserRoleInfo
// inherits RoleInfo, so a type describing an eight-column assignment presents twenty-three effective
// properties and a reader cannot tell which of them it actually owns. Every contract in this folder
// declares its own members outright, and the duplication is intentional.
public sealed class UpdateRoleRequest
{
    /// <summary>
    /// Replacement name of the role. Required, and unique within the owning portal.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleName</c>, itself capped at fifty characters by its
    /// <c>MaxLength</c> (<c>editroles.ascx</c> L27), carrying the screen's one required-field
    /// validator <c>valRoleName</c> (L29) - which the edit path disabled, for the reason recorded on
    /// this class. Terminal column <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L117, carried unchanged through the table recreate at
    /// <c>01.00.05.SqlDataProvider</c> L2750), covered together with the portal column by the
    /// uniqueness constraint <c>IX_RoleName</c> (<c>03.00.09.SqlDataProvider</c> L304).
    /// </remarks>
    // MIGRATION: non-nullable, and initialised to the empty string rather than to a null-forgiving
    // default, because the column is NOT NULL and a request that omits the name is a MISSING required
    // field rather than a null one. Neither rule on this member is enforced here: the fifty-character
    // ceiling is a schema fact the validator reproduces declaratively, and uniqueness cannot be
    // decided at the boundary at all because it needs a read, so the service reports it - applying the
    // same lookup the legacy insert branch applied at EditRoles.ascx.vb L252, excluding the role being
    // edited, and reporting the duplicate the same way the creation path does.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Replacement description of the role, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at one thousand
    /// characters by its own <c>MaxLength</c> (<c>editroles.ascx</c> L39). Terminal column
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
    /// column <c>Roles.RoleGroupID int NULL</c>, added at
    /// <c>03.02.03.SqlDataProvider</c> line 34 and constrained by the foreign key added at line 37
    /// against <c>RoleGroups.RoleGroupID</c>, itself seeded <c>IDENTITY(0,1) NOT NULL</c> at line 18
    /// of the same script.
    /// </remarks>
    // MIGRATION: nullable, and null carries the meaning the legacy screen encoded as minus one. Two
    // facts about that value look contradictory and are not. The group list offered an entry whose
    // value was minus one under the caption "Global Roles", and the save path parsed the selection
    // straight onto the column - so the screen genuinely posted it. Yet it could never be stored:
    // the referenced key is seeded from zero and the foreign key added at 03.02.03 line 37 would
    // reject it. The resolution is that minus one was the presentation encoding of a database null,
    // translated in both directions by the legacy provider layer, so the same fact simply wore two
    // forms. This contract expresses it once, as null.
    //
    // Two traps follow, and both matter. Zero is a LEGITIMATE group identifier, because the
    // referenced key is seeded IDENTITY(0,1) - so a non-positive test must never stand in for an
    // absence test on this member. And the roles list screen offered a further negative value as a
    // transient "all roles" list filter at Roles.ascx.vb L112, which was never stored and must never
    // reach this member. Normalising a legacy-encoded value a client may still send is the service's
    // responsibility, not this type's.
    //
    // Finally, this rule must not be confused with the unrelated one governing permissions. In
    // dbo.ModulePermissions and dbo.TabPermissions a negative role identifier denotes a real
    // pseudo-principal - all users, superusers, unauthenticated users - which must NEVER be mapped
    // to null. That rule applies to permission-bearing contracts, which this folder does not declare.
    // Here, on Roles.RoleGroupID alone, the legacy encoding genuinely is a database null.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// <see langword="true"/> when users of the portal may subscribe to the role themselves.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkIsPublic</c> (<c>editroles.ascx</c> L56). Terminal column
    /// <c>Roles.IsPublic bit NOT NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6831), re-asserted
    /// as not-nullable at <c>03.01.01.SqlDataProvider</c> line 1174 with the store default zero
    /// re-applied at line 1179.
    /// </remarks>
    // MIGRATION: not a nullable boolean, because the column is NOT NULL and the legacy checkbox
    // always posted a definite state. Since this contract replaces rather than patches, a request
    // that omits the member sets the role private rather than leaving it as it was; that is the
    // conservative direction, but it is still a change, so a caller performing a partial edit must
    // resubmit the current value.
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every user of the portal in the role automatically.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkAutoAssignment</c> (<c>editroles.ascx</c> L64). Terminal
    /// column <c>Roles.AutoAssignment bit NOT NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6832),
    /// re-asserted as not-nullable at <c>03.01.01.SqlDataProvider</c> line 1175 with the store
    /// default zero re-applied at line 1181.
    /// </remarks>
    // MIGRATION: not a nullable boolean, for the same reason as the flag above, so omitting it
    // clears it. The legacy asymmetry around this flag is preserved rather than smoothed over:
    // enabling it during an edit did not retrospectively enrol the portal's existing users, and
    // disabling it did not remove those already enrolled. Existing memberships are managed through
    // the assignment contract.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Replacement recurring fee for membership of the role, or <see langword="null"/> when the role
    /// carries no fee.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtServiceFee</c>, carrying a currency type check and a
    /// not-negative comparison (<c>editroles.ascx</c> L89 to L96). Terminal column
    /// <c>Roles.ServiceFee money NULL</c> with store default zero
    /// (<c>03.01.01.SqlDataProvider</c> line 1173, default at line 1177).
    /// </remarks>
    // MIGRATION: a fixed-point decimal, not a floating-point type, and the terminal schema is what
    // decides that. The legacy property was a single-precision float (RoleInfo.vb L164) and the
    // screen parsed the field as one (EditRoles.ascx.vb L226), so both legacy sources agree with
    // each other and are both superseded. The baseline column was decimal(5, 2) at
    // 01.00.00.SqlDataProvider line 119 - a ceiling of 999.99 - and the destructive chain widened it
    // to money, which is fixed-point and maps to decimal. Reproducing either legacy type would
    // silently reintroduce binary rounding into a monetary value.
    //
    // A free role leaves the billing members below meaningless: the legacy screen revealed its
    // billing block only when this value formatted to something other than zero, and the terminal
    // projection blanks the billing members for a role whose fee converts to zero. That gating is
    // the validator's and the service's concern, so no member here is conditional on another.
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
    // argument, and four sources settle it three-to-one. The column is int; the legacy property was
    // an Integer (RoleInfo.vb L218); the screen validated the field as an integer and parsed it as
    // one (EditRoles.ascx.vb L227). Decisively, null is the literal legacy value here rather than a
    // modernisation: the terminal projection emits SQL null for this column itself whenever the fee
    // converts to zero, so a free role genuinely carried no period.
    //
    // Null and zero are different instructions and are not interchangeable. The legacy expiry
    // derivation short-circuited to no expiry when the period equalled the null-integer sentinel,
    // before the frequency code was examined at all (RoleController.vb L537-L538), whereas zero
    // would have been carried into the arithmetic. That arithmetic belongs to the service layer.
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
    // left every caller free to submit an unrecognised character. The shared Domain enumeration
    // replaces it, carrying each legacy single-character code as its underlying value so a member
    // converts to and from the persisted character without loss. Those characters are the contract
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
    // MIGRATION: a fixed-point decimal for the same reason as the recurring fee, though the
    // chronology is simpler: this column was money from the moment it was introduced and was never
    // widened, so only the legacy single-precision property (RoleInfo.vb L233) is superseded.
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
    // MIGRATION: this member is typed with the SAME enumeration as the billing unit above, and there
    // is deliberately no separate trial-frequency type. The schema proves they share one code set by
    // joining the same frequency lookup twice from a single role row - once per column - in both the
    // early and the terminal era, so a second enumeration would duplicate a single source of truth
    // and could drift from it.
    //
    // MIGRATION: no property initialiser, and this is a deliberate difference from the creation
    // contract's situation. The legacy screen defaulted this list to the "none" code when adding a
    // role (EditRoles.ascx.vb L125), but when editing one it LOADED the stored code and gated the
    // trial block's visibility on it (L154). Defaulting here would therefore overwrite a stored
    // choice whenever a caller omitted the member, which on a replacement contract is exactly the
    // wrong direction. The client sends the current or newly chosen value and the service applies no
    // default of its own.
    //
    // The "none" code is load-bearing rather than decorative, which is why it must never be
    // conflated with null: the legacy assignment path tests this value against it to decide whether
    // the trial term or the billing term governs expiry (RoleController.vb L521), and the terminal
    // projection applies the same test when blanking the trial members of a role.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// Replacement code a user may redeem to join the role, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRSVPCode</c>, capped at fifty characters by its own
    /// <c>MaxLength</c> and carrying no validator (<c>editroles.ascx</c> L153). Terminal column
    /// <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    // MIGRATION: nullable, with the legacy absent value again being the empty string rather than
    // null; the mapper owns that translation. The screen's companion read-only link field is not
    // represented here at all, for the reasons given on the class above: it was display-only, it
    // backs no column, and the client now composes it from this code and its own origin.
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
