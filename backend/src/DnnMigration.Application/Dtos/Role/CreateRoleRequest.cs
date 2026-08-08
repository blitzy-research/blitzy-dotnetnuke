using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/roles</c>: everything a caller may supply when creating a
/// security role, including its paid-membership terms.
/// </summary>
/// <remarks>
/// The member set is the legacy CREATE SCREEN's, not the legacy entity's - the thirteen values that
/// screen assigned onto a freshly constructed role, and only those. The type is inert: no behaviour,
/// no derived member, no guard. Field rules live in
/// <c>Application/Validation/CreateRoleRequestValidator.cs</c>, which reproduces the legacy screen's
/// nine declarative validators, so a malformed field is a field-level 400 rather than a failure
/// raised from here.
/// </remarks>
// MIGRATION: three groups of members are deliberately absent and must not be added. The role's own
// identifier is gone even as a nullable member: the legacy screen carried one because a single postback
// served both creating and editing, and it overloaded -1 as its add-versus-edit switch, whereas creating and
// editing are now distinct routed endpoints. The owning portal is gone because it is a tenant boundary - the
// legacy assigned it from ambient page state and no control posted it. And there are no assignment members
// or derived membership classification, because a role definition and a user's membership OF a role are
// different things: the validity window, trial-consumed flag, subscription flag, own identifier and owning
// user are all columns of dbo.UserRoles and none of them is a property of the legacy role entity.
public sealed class CreateRoleRequest
{
    /// <summary>Name of the role. Required, and unique within the owning portal.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleName</c>, capped at fifty characters by its
    /// <c>MaxLength</c> and carrying the screen's one required-field validator <c>valRoleName</c>
    /// (<c>editroles.ascx</c>). Terminal column <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c>), covered together with the portal column by the uniqueness
    /// constraint <c>IX_RoleName</c> (<c>03.00.09.SqlDataProvider</c>).
    /// </remarks>
    // MIGRATION: non-nullable, and initialised to the empty string rather than to a null-forgiving default,
    // because the column is NOT NULL and a request that omits the name is a MISSING required field rather
    // than a null one. Neither rule on this member is enforced here: the fifty-character ceiling is a schema
    // fact the validator reproduces declaratively, and uniqueness needs a read, so the service reports it -
    // reproducing the legacy lookup-then-insert guard at EditRoles.ascx.vb and its DuplicateRole message.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at a thousand
    /// characters, with no validator (<c>editroles.ascx</c>).
    /// </remarks>
    // MIGRATION: null and the empty string stay distinct on this contract, and nothing here converts either
    // into the other. The legacy absent value for a text member was the EMPTY STRING, not null - Null.vb
    // returns "" from its string sentinel - so a legacy write could not express "no description" at all, and
    // a legacy reader turned a SQL null into "" on the way past.
    public string? Description { get; set; }

    /// <summary>
    /// Recurring fee charged for membership of the role, or <see langword="null"/> when the role is
    /// free.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtServiceFee</c>, validated as a currency and constrained to
    /// zero or more (<c>editroles.ascx</c>).
    /// </remarks>
    // MIGRATION: decimal, not a floating-point type, and this member is the plainest demonstration in this
    // folder of why only the schema's TERMINAL state may be consulted. Three sources disagree: the legacy
    // property was declared As Single (RoleInfo.vb) and the save path parsed the box with Single.Parse
    // (EditRoles.ascx.vb), while the BASELINE column was decimal(5, 2) (01.00.00.SqlDataProvider), capping a
    // fee at 999.99.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing-frequency units between charges, or <see langword="null"/> when the role
    /// carries no recurring billing term.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtBillingPeriod</c>, validated as an integer and constrained
    /// to strictly more than zero (<c>editroles.ascx</c>).
    /// </remarks>
    // MIGRATION: int, not text, resolving a three-against-one disagreement - the legacy property is As
    // Integer (RoleInfo.vb), the save path parses with Integer.Parse, the column is int NULL, and only the
    // legacy provider signature spelled the value as text.
    //
    // MIGRATION: null is load-bearing here and is NOT interchangeable with zero. It is the LITERAL legacy
    // value for a free role rather than a modernisation, because the terminal projection emits it:
    // 'BillingPeriod' = case when convert(int,Roles.ServiceFee) <> 0 then Roles.BillingPeriod else null end
    // (01.00.08.SqlDataProvider).
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Unit of the recurring billing term, or <see langword="null"/> when the role carries none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboBillingFrequency</c>, bound to the portal's frequency
    /// list and pre-selected to the never code (<c>editroles.ascx</c>, <c>EditRoles.ascx.vb</c>).
    /// </remarks>
    //  MIGRATION: the single-character codes are LOAD-BEARING DATA and are never renamed. The legacy property
    //  surfaced the column as text (RoleInfo.vb) over a char(1) column; this contract uses the shared Domain
    //  enumeration, whose members carry those very characters as their underlying values, so a member
    //  converts to and from the persisted character without loss.
    //
    // MIGRATION: ONE enumeration serves this member and the trial frequency below.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Fee charged for the trial period, or <see langword="null"/> when the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialFee</c>, validated as a currency and constrained to
    /// zero or more (<c>editroles.ascx</c>).
    /// </remarks>
    // MIGRATION: decimal for the same reason as the service fee, though from a shorter history: this column
    // was money from birth, yet the legacy property was still As Single (RoleInfo.vb). The terminal
    // projection returns this member only for a role whose stored trial frequency is not the never code
    // (01.00.08.SqlDataProvider), so null is the value a role without a trial both stores and reports.
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial-frequency units the trial runs for, or <see langword="null"/> when the role
    /// offers no trial.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialPeriod</c>, validated as an integer and constrained to
    /// strictly more than zero (<c>editroles.ascx</c>).
    /// </remarks>
    // MIGRATION: int on the same three-against-one evidence as the billing period (legacy property
    // RoleInfo.vb), and null is equally load-bearing, because the terminal projection returns this member
    // only when the stored trial frequency is not the never code (01.00.08).
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Unit of the trial term, or <see langword="null"/> when the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboTrialFrequency</c>, bound to the same frequency list
    /// as the billing dropdown and pre-selected to the never code (<c>editroles.ascx</c>,
    /// <c>EditRoles.ascx.vb</c>).
    /// </remarks>
    // MIGRATION: the never code is a REAL STORED VALUE meaning "no trial", not an unset marker, and the two
    // must stay distinct in a reader's mind. The legacy assignment path decides whether the trial terms or
    // the billing terms govern an expiry by testing this column against that code (RoleController.vb), and
    // the terminal projections gate the trial fee, the trial period and this member behind the same test
    // (01.00.08).
    //
    // MIGRATION: this member deliberately carries NO initialiser, so an omitted trial frequency arrives as
    // null - stated here because the legacy default was NOT null: the screen pre-selected the never code in
    // both frequency dropdowns and its save path substituted that code, a zero fee and a period of one
    // whenever a block was left empty (EditRoles.ascx.vb). Three reasons prefer null even so.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary><see langword="true"/> when users may subscribe to the role themselves.</summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkIsPublic</c>, with no validator (<c>editroles.ascx</c>).
    /// Terminal column <c>Roles.IsPublic bit NOT NULL</c> with the store default zero
    /// (<c>01.00.08.SqlDataProvider</c>, retyped and re-defaulted at
    /// <c>03.01.01.SqlDataProvider</c>).
    /// </remarks>
    // MIGRATION: plainly non-nullable, because the column is NOT NULL, a checkbox always posts a definite
    // state, and the legacy property was likewise As Boolean (RoleInfo.vb). The CLR default of false agrees
    // with the store default of zero, and that agreement is what makes a non-nullable member safe: a request
    // that omits it creates a private role, exactly as the legacy screen did from an unchecked box.
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every existing user of the portal in the role as it is
    /// created.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkAutoAssignment</c>, with no validator
    /// (<c>editroles.ascx</c>). Terminal column <c>Roles.AutoAssignment bit NOT NULL</c> with the
    /// store default zero (<c>01.00.08.SqlDataProvider</c>, retyped and re-defaulted at
    /// <c>03.01.01.SqlDataProvider</c>).
    /// </remarks>
    // MIGRATION: non-nullable on the same reasoning as the public flag (RoleInfo.vb). Its CONSEQUENCE,
    // however, is unlike that of any other member here: setting it makes the service enrol the portal's
    // existing members as part of the same create, so the operation writes membership rows as well as the
    // role itself.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Identifier of the role group to file the role under, or <see langword="null"/> to leave it
    /// ungrouped - the case the legacy screen presented as "Global Roles".
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboRoleGroups</c>, with no validator
    /// (<c>editroles.ascx</c>).
    /// </remarks>
    // MIGRATION: nullable, which reconciles what looks like a contradiction between the legacy user
    // interface and the legacy schema but is not one. The interface treated -1 as a real, selectable choice
    // - the group-binding helper adds it as the drop-down's first entry, labelled from the localised
    // "GlobalRoles" resource, and the save path parses the selection straight onto the legacy property
    // (EditRoles.ascx.vb) - yet -1 can never reach the column, because RoleGroups.RoleGroupID is an identity
    // column seeded at zero and NOT NULL (03.02.03.SqlDataProvider) and the foreign key would reject the
    // value.
    //
    // Two consequences, each a real defect if ignored:
    //
    // * ZERO IS A LEGITIMATE GROUP, because RoleGroups.RoleGroupID is seeded at zero. Never test this member
    //   for absence by comparing it against zero or a non-positive range; ask whether it has a value.
    // * THE ALL-ROLES FILTER VALUE MUST NEVER APPEAR HERE.
    //
    // Normalising a submitted -1 into a null, should a legacy client send one, is the service's decision and
    // not this type's. The service additionally reports a group that does not exist in the owning portal as
    // role_group.not_found.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Code a user may redeem to be granted the role, or <see langword="null"/> when the role has
    /// none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRSVPCode</c>, capped at fifty characters and with no
    /// validator (<c>editroles.ascx</c>). Nothing in the schema makes the code unique, so it is not
    /// an identifier and a clash is not a conflict.
    /// </remarks>
    // MIGRATION: the derived invitation URL that sat beside this box on the legacy screen is deliberately
    // absent. The markup declared a companion read-only box, txtRSVPLink, which the code-behind filled FOR
    // DISPLAY ONLY from the request's own domain name, the default page and this code (EditRoles.ascx.vb);
    // the save path at writes only the code, and no corresponding column exists in the eighty-eight upgrade
    // scripts.
    //
    // MIGRATION: the legacy absent value for this member was the EMPTY STRING rather than null - the
    // code-behind tests it with <> "" at EditRoles.ascx.vb, the string sentinel of Null.vb in action - so a
    // legacy row could not distinguish "no code" from "empty code". This contract can; which form reaches
    // the column is the mapper's decision.
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Relative path of the image that represents the role, or <see langword="null"/> when it has
    /// none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>dnn:Url ctlIcon</c>, restricted by the code-behind to the portal's image
    /// file types and carrying no validator (<c>editroles.ascx</c>, <c>EditRoles.ascx.vb</c>).
    /// </remarks>
    // MIGRATION: carried as the supplied relative path, exactly as the legacy screen stored it -
    // EditRoles.ascx.vb assigns the control's raw value with no transformation. The hundred-character
    // ceiling and the rejection of rooted and traversing forms are the validator's rules, and resolving the
    // path against the portal's home directory is the client's presentation concern; neither is done here.
    public string? IconFile { get; set; }
}
