using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Role"/>, <see cref="RoleGroup"/> and
/// <see cref="UserRole"/> aggregates and the six role transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// Every member here is a pure, synchronous function of the arguments it is handed. Nothing in this
/// file reads a clock, touches a store, validates a submission or decides a business question, so a
/// mapping call can never provoke a further read and a caller never has to load a graph the contract
/// does not expose. The two behaviours that look like exceptions to that rule are documented at the
/// point they occur: the monetary floor in <see cref="ApplyCore"/>, and the assignment bounds that
/// arrive as parameters precisely so the arithmetic that produces them stays in the service.
/// </para>
/// <para>
/// MIGRATION: the five paid-membership members of the legacy role entity - the service fee, the
/// billing period and frequency, and the trial fee, period and frequency - are carried through
/// unchanged. They are stored data with live rules behind them, not decoration: the frequency column
/// is a single character and its values drive the expiry arithmetic that the assignment path performs,
/// so the enumeration that models it keeps the legacy character values rather than renumbering them.
/// </para>
/// <para>
/// Neither role projection carries a join denormalisation. The group's name is not a column on the
/// role - it belongs to the group the role points at, and therefore to <see cref="RoleGroupDto"/> -
/// and a member tally is a count of assignment rows that no legacy role screen displayed. Excluding
/// both is what keeps every projection here a pure function of the aggregate it is handed.
/// </para>
/// </remarks>
// =================================================================================================
// MIGRATION: Rule T8 - the two legacy hydration paths this file replaces, and ports NEITHER.
//
//   1. The reflection hydrator Library/Components/Shared/CBO.vb (729 lines): CloneObject L208,
//      seven FillCollection overloads (L278, L300, L334, L370, L404 with a ByRef totalRecords,
//      L435, L459), two FillDictionary overloads constrained to IHydratable (L495, L533), four
//      FillObject overloads (L557, L561, L608, L628), GetPropertyInfo L661, InitializeObject L679
//      and Serialize L701.
//   2. The hand-rolled sentinel reads, of which ModuleController.vb FillModuleInfo (declared L53,
//      assigning from L66 via Convert.ToInt32(Null.SetNull(dr("PortalID"), ...)) and roughly forty
//      eight siblings) is the pattern.
//
// Both are superseded by the EF Core materialiser in Infrastructure, so nothing here is named
// Fill, FillObject, FillCollection, FillDictionary, CloneObject, GetPropertyInfo, InitializeObject
// or Serialize; no IDataReader, DataTable or DataSet appears; no ArrayList, Hashtable, CollectionBase
// or DictionaryBase appears; and no *Collection wrapper type is introduced. IHydratable is not
// implemented either - Library/Components/Modules/IHydratable.vb declares only
// "Property KeyID() As Integer" and "Sub Fill(ByVal dr As IDataReader)", and a search across both
// legacy trees finds ZERO files implementing it, so there is no hydration contract to port. "Not
// ported" is not "deleted": every legacy file remains byte-identical.
//
// MIGRATION: RoleInfo.vb's serialisation attributes are dropped and replaced by NOTHING - the
// XmlRoot("role", IsNullable:=False) on the class at L42, the three XmlIgnore attributes on RoleID
// L65, PortalID L80 and RoleGroupID L95, and the twelve XmlElement attributes on the remaining
// members. The DTOs own the wire contract, so no [JsonPropertyName], [JsonIgnore], [Key], [Column],
// [Required] or [MaxLength] takes their place here. RoleGroupInfo.vb carried no attribute at all -
// not even an XmlRoot - even though it imports System.Xml.Serialization at L25 without using it, so
// on that side of the map there was nothing to drop.
//
// MIGRATION: a line-number correction, because an incorrect figure is in circulation. In
// RoleInfo.vb, L149 is BillingFrequency (declared As String) and L164 is ServiceFee (declared As
// Single). ServiceFee is NOT at L149. Verified by reading the file rather than by citation.
//
// -------------------------------------------------------------------------------------------------
// MIGRATION: the billing-frequency vocabulary has SIX codes, not four. This is the single most
// mis-specified fact in the role domain, so the measurement is recorded in full.
//
// The terminal set is N, O, D, W, M and Y, established three independent ways:
//   1. RoleController.vb selects on all six at L540-L547: "N" (never expires), "O" (perpetual,
//      9999-12-31), "D" (days), "W" (days x 7), "M" (months) and "Y" (years).
//   2. RoleInfo.vb documents all six ITSELF, twice - the doc block above BillingFrequency at
//      L138-L147 and the one above TrialFrequency at L177-L186 both list
//      "N - None / O - One time fee / D - Daily / W - Weekly / M - Monthly / Y - Yearly".
//   3. The terminal seed is 01.00.08.SqlDataProvider L6840-L6891, which inserts exactly those six
//      letters ('N' L6840, 'O' L6849, 'D' L6858, 'W' L6867, 'M' L6876, 'Y' L6885).
//
// A plan citing only D/W/M/Y is therefore incomplete, and so is any conversion covering four codes.
// The enumeration is Domain.Enums.BillingFrequency, ushort-backed with each member's VALUE being its
// legacy character, and all six members are declared. The codes are stored data and are NEVER
// renamed or renumbered.
//
// MIGRATION: the DATABASE DOES NOT ENFORCE THIS VOCABULARY, and the obvious reading of the early
// scripts is the opposite, so the correction is recorded rather than left to be rediscovered. The
// codes began as the key of dbo.CodeFrequency and were enforced by FK_Roles_CodeFrequency
// (01.00.00.SqlDataProvider L584) - but note that the constraint covered the BillingFrequency
// column ONLY (L586-L588); TrialFrequency was never constrained by it. The constraint was dropped
// and recreated across the rebuilds (01.00.04 L1313/L1366, 01.00.05 L2735/L2802), renamed to a
// templated form by 02.00.00 L150, and then dropped FOR GOOD by 03.00.01 L1297 - which also drops
// the CodeFrequency table itself at L1300 and both GetBillingFrequencyCode accessor procedures.
// Nothing recreates any of them, and no CHECK constraint ever replaced the foreign key (a search
// across all 88 scripts finds none). From 03.00.01 the codes live as Lists rows with
// ListName = 'Frequency' (copied at L1269) and are resolved by join, e.g.
// "Lists L1 on R.BillingFrequency = L1.Value ... and L1.ListName='Frequency'" (L1355, L1382).
// CONSEQUENCE: the terminal columns accept any single character, so the exactness of this
// enumeration is the ONLY thing standing between the vocabulary and the column - which is precisely
// why the six codes are enumerated exhaustively here rather than trusted to the schema.
//
// MIGRATION: the DDL hazard that hid the drop above is worth naming, because it is the reason a
// casual search reports the constraint as still live. By the time 03.00.01 drops it, the 02.00.00
// L150 sp_rename has TEMPLATED the name to FK_{objectQualifier}Roles_{objectQualifier}CodeFrequency,
// so a search for the literal "FK_Roles_CodeFrequency" finds the CREATE and misses the DROP. Any
// inspection of these scripts must be case-insensitive and must cover all four naming forms - bare,
// dbo.-qualified, [dbo].[...]-bracketed and {databaseOwner}{objectQualifier}-templated.
//
// MIGRATION: the superseded numeric codes are deliberately NOT accepted. The baseline seeded '0'
// to '5' (01.00.00.SqlDataProvider L6889-L6899); 01.00.08 migrates each to its letter and then
// DELETEs the numeric row (L6846, L6855, L6864, L6873, L6882, L6891). The terminal schema contains
// no numeric code, so no numeric fallback is added anywhere - a value outside the vocabulary is left
// unmapped rather than guessed at.
//
// MIGRATION: ONE enumeration serves BOTH columns. Roles.BillingFrequency and Roles.TrialFrequency
// are both char(1) NULL (01.00.05.SqlDataProvider L2753 and L2755) and both join the SAME lookup -
// "left outer join CodeFrequency C1 on Roles.BillingFrequency = C1.Code" alongside
// "left outer join CodeFrequency C2 on Roles.TrialFrequency = C2.Code" (01.00.08 L3144-L3145, and
// again at L7032-L7033 and L7062-L7063) - and, after that table is dropped, the same pairing
// survives against the replacement Lists rows: "Lists L1 on R.BillingFrequency = L1.Value" beside
// "Lists L2 on R.TrialFrequency = L2.Value", both filtered to ListName='Frequency'
// (03.00.01 L1355-L1360). So the two columns shared one vocabulary before AND after the schema
// change. The legacy type agreed, backing both properties with a plain String, and RoleInfo.vb
// documents the identical six-code list above each of them. No separate trial-specific enumeration
// exists or may be invented, and every projection below carries the two members through the
// identical type.
//
// MIGRATION: the wire form is the CHARACTER, never the member NAME and never the ordinal. "M" is
// correct; "Month" and 4 are both wrong, and the trap is easy to fall into backwards because
// BillingFrequency.Month.ToString() yields precisely the wrong string. This file performs no such
// conversion, because it needs none: all six role contracts type the two members as
// BillingFrequency? and the enum crosses the mapper unchanged. The character conversion is owned by
// two dedicated classes instead, each at the boundary that actually needs it -
// Application/Serialization/BillingFrequencyJsonConverter.cs at the wire (it writes (char)value and
// reads (BillingFrequency)char.ToUpperInvariant(text[0]), and refuses any value that is not exactly
// one character so "Monthly" can never resolve as "M"), and
// Infrastructure/Persistence/ValueConverters/BillingFrequencyToStringConverter.cs at the store.
// Adding a third copy here would duplicate both and invite the three to drift apart.
//
// MIGRATION: None ('N') is a STORED VALUE and is NOT the same fact as null. 'N' means "this
// membership never expires"; null means "no frequency was ever recorded". Both columns are nullable,
// so BillingFrequency? genuinely carries three states, and nothing here defaults a null to None or
// collapses None to null.
//
// -------------------------------------------------------------------------------------------------
// MIGRATION: RoleStatus is COMPUTED and NEVER PERSISTED, and it appears nowhere in this file.
// UserRole.GetStatus(DateTime asOfUtc) derives it from the two bounds - treating a null, and only a
// null, as unset, reporting Expired when the expiry bound is already past, Pending when the start
// bound is still future, Active otherwise, and Active for equality at either bound - and
// dbo.UserRoles has no column for it. No role contract exposes it, which is asserted rather than
// assumed: RoleDetailDto declares exactly fourteen members and none of them is a status. It is
// consumed where an authorisation question is actually being answered, in
// Api/Authorization/PortalAdministratorAuthorizationHandler.cs, which passes an instant in. Because
// no contract here exposes it, no method below takes an asOfUtc and none calls GetStatus, so this
// file reads no clock: there is no DateTime.Now, no DateTime.UtcNow, no DateTime.Today and no
// IClock anywhere in it, and every method is deterministic for a given set of arguments.
// =================================================================================================
public static class RoleMappings
{
    /// <summary>
    /// Projects a role onto the row shape the role list screen renders.
    /// </summary>
    /// <param name="role">The role to project.</param>
    /// <returns>The list row.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="role"/> is null.</exception>
    // MIGRATION: RoleId is copied verbatim and is never tested for a sign. dbo.Roles.RoleID is
    // declared IDENTITY(0, 1) (01.00.00.SqlDataProvider L115), so ZERO identifies the first role a
    // DotNetNuke database ever created and is a wholly ordinary key. A "<= 0 means absent" test -
    // or a Math.Max(id, 0), or an ArgumentOutOfRangeException.ThrowIfNegativeOrZero - would
    // therefore discard a real row. The legacy helper could not make this distinction: Null.vb
    // defines NullInteger as -1 (L41) and IsNull returns True for it (L208-L211), which is exactly
    // why the sign test is absent here and the nullability of the CLR type carries the meaning
    // instead.
    public static RoleListItemDto ToListItem(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleListItemDto
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
            Description = role.Description,
            ServiceFee = role.ServiceFee,
            BillingPeriod = role.BillingPeriod,
            BillingFrequency = role.BillingFrequency,
            TrialFee = role.TrialFee,
            TrialPeriod = role.TrialPeriod,
            TrialFrequency = role.TrialFrequency,
            IsPublic = role.IsPublic,
            AutoAssignment = role.AutoAssignment,
        };
    }

    /// <summary>
    /// Projects a role onto the full detail contract.
    /// </summary>
    /// <param name="role">The role to project.</param>
    /// <returns>The detail contract.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="role"/> is null.</exception>
    /// <remarks>
    /// Every one of the detail contract's fourteen members is drawn from a column on this one
    /// aggregate, so the projection needs no argument beyond the role itself and can never trigger a
    /// further read.
    /// </remarks>
    // MIGRATION: this projection deliberately carries NEITHER the owning portal identifier NOR any
    // join denormalisation, matching the fourteen members RoleDetailDto actually declares.
    //   * The portal identifier is omitted because the legacy editor never posted it either -
    //     EditRoles.ascx.vb L232 assigned it from ambient page state - and the migrated request
    //     resolves it from the host before /api/v1/roles/{roleId} runs. The list projection above
    //     omits it for the same reason, so the two role contracts stay consistent.
    //   * The group's NAME and a member TALLY are both deliberately absent, and neither may be
    //     added as an argument - neither is a column on dbo.Roles. Their
    //     absence is a behavioural improvement rather than a loss: the group name belongs to
    //     RoleGroupDto and the legacy editor resolved it client-side from the group drop-down it had
    //     already bound (BindGroups, EditRoles.ascx.vb L75-L78), while no legacy role screen showed
    //     a member tally at all. Supplying them would oblige the caller to issue two further reads
    //     per single-role request - one for the group, one for the total of a one-row page of
    //     assignments consulted purely for its count - whereas a detail read is a single-row query.
    //
    // MIGRATION: RoleGroupId is the ONE member in this file where the legacy -1 means "absent" and
    // therefore becomes null, and the asymmetry with PortalId is worth stating plainly because the
    // literal is identical. PortalController.vb L393 reads
    //     objRoleInfo.RoleGroupID = Null.NullInteger
    // when it creates a role, i.e. -1 was deliberately assigned to mean "belongs to no group", and
    // the value never reached the column because FK_Roles_RoleGroups would have rejected it. The
    // target models that as int? RoleGroupId and a null passes straight through. On PortalId the
    // SAME literal -1 means the HOST PORTAL: dbo.Portals.PortalID is IDENTITY(-1, 1)
    // (01.00.00.SqlDataProvider L77), so -1 is the first real tenant and 0 is the shipped _default
    // portal. Neither is absent, and neither may be nulled. RoleGroupId 0 is likewise a real group,
    // because dbo.RoleGroups.RoleGroupID is IDENTITY(0, 1) (03.02.03 L18, 04.00.04 L51).
    public static RoleDetailDto ToDetail(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleDetailDto
        {
            RoleId = role.RoleId,
            RoleGroupId = role.RoleGroupId,
            RoleName = role.RoleName,
            Description = role.Description,
            BillingFrequency = role.BillingFrequency,
            ServiceFee = role.ServiceFee,
            TrialFrequency = role.TrialFrequency,
            TrialPeriod = role.TrialPeriod,
            BillingPeriod = role.BillingPeriod,
            TrialFee = role.TrialFee,
            IsPublic = role.IsPublic,
            AutoAssignment = role.AutoAssignment,
            RsvpCode = role.RsvpCode,
            IconFile = role.IconFile,
            ConcurrencyToken = ConcurrencyTokenFor(role),
        };
    }

    /// <summary>
    /// Derives the optimistic-concurrency token a caller round-trips to prove it is replacing the record it
    /// read.
    /// </summary>
    /// <param name="role">The role as it currently stands.</param>
    /// <returns>The token.</returns>
    /// <remarks>
    /// ⚠ THE MEMBER ORDER IS PART OF THE CONTRACT. The token published by a read and the token verified by a
    /// write are both produced here, so a reordering changes both together and stays self-consistent - but a
    /// token already in a browser's hands would stop matching, and every open editor would be refused once.
    /// Adding a member has the same effect. That is acceptable on a deployment boundary and must not be done
    /// casually.
    ///
    /// Every mutable column an update can replace contributes, and the identifier does not: the identifier
    /// addresses the record rather than forming part of its state, and including it would only make tokens
    /// from different records differ, which they already do.
    /// </remarks>
    internal static string ConcurrencyTokenFor(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return ConcurrencyToken.From(
            role.RoleGroupId,
            role.RoleName,
            role.Description,
            role.BillingFrequency,
            role.ServiceFee,
            role.TrialFrequency,
            role.TrialPeriod,
            role.BillingPeriod,
            role.TrialFee,
            role.IsPublic,
            role.AutoAssignment,
            role.RsvpCode,
            role.IconFile);
    }

    /// <summary>
    /// Projects a role group onto its transfer contract.
    /// </summary>
    /// <param name="roleGroup">The group to project.</param>
    /// <returns>The group contract.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="roleGroup"/> is null.
    /// </exception>
    // MIGRATION: four members for four columns, matching RoleGroupInfo.vb exactly - RoleGroupID
    // L53, PortalID L68, RoleGroupName L83 and Description L98. That legacy class carried no
    // serialisation attribute on any property, so unlike RoleInfo there is nothing to drop here.
    public static RoleGroupDto ToDto(RoleGroup roleGroup)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);

        return new RoleGroupDto
        {
            RoleGroupId = roleGroup.RoleGroupId,
            PortalId = roleGroup.PortalId,
            RoleGroupName = roleGroup.RoleGroupName,
            Description = roleGroup.Description,
        };
    }

    /// <summary>
    /// Projects one role membership, together with the account and role it joins, into its wire contract.
    /// </summary>
    /// <param name="assignment">
    /// The assignment to project. Its account and role navigations must both be loaded; the repository
    /// read that answers this question materialises both, because the terminal statement projects columns
    /// from all three tables in a single result set.
    /// </param>
    /// <returns>The membership contract.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assignment"/> is null.
    /// </exception>
    /// <remarks>
    /// The two dates are copied straight across with no coercion in either direction. That is the whole
    /// substance of this projection, so it is worth being explicit about what is NOT done: a null is not
    /// turned into a minimum date on the way out, and a minimum date is not turned into a null. The
    /// legacy absence marker for a date was <c>Date.MinValue</c>, and reintroducing it here would hand a
    /// consumer a value it must recognise as magic - the collision AAP Rule T7 exists to prevent. The
    /// legacy screen's own renderer agrees: <c>SecurityRoles.ascx.vb</c> <c>FormatDate</c> answers the
    /// empty string for an absent date rather than printing the sentinel.
    /// </remarks>
    // MIGRATION: the five columns of securityroles.ascx:L71-L86 plus the three keys a caller needs to
    // act on the row. The account's display name is the value the legacy grid showed, projected by the
    // terminal GetUserRolesByUsername statement as "U.DisplayName As FullName"; the login name is
    // carried in addition, because the display name is not an identifier.
    public static RoleMembershipDto ToMembership(UserRole assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // Both navigations are required rather than defaulted. A missing one means the caller used a read
        // that did not compose them, and answering with an empty name would publish a row that looks
        // complete and is not - so the failure is raised here, at the point that can still name the cause.
        ArgumentNullException.ThrowIfNull(assignment.User);
        ArgumentNullException.ThrowIfNull(assignment.Role);

        return new RoleMembershipDto
        {
            UserRoleId = assignment.UserRoleId,
            UserId = assignment.UserId,
            Username = assignment.User.Username,
            DisplayName = assignment.User.DisplayName,
            RoleId = assignment.RoleId,
            RoleName = assignment.Role.RoleName,
            EffectiveDate = assignment.EffectiveDate,
            ExpiryDate = assignment.ExpiryDate,
        };
    }

    /// <summary>
    /// Projects one role membership from parts the caller has already resolved, rather than from the
    /// assignment's navigations.
    /// </summary>
    /// <param name="assignment">The assignment to project.</param>
    /// <param name="role">The role it names.</param>
    /// <param name="account">The account it names.</param>
    /// <returns>The membership contract.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any argument is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Why an explicit overload exists.</b> The sibling overload requires both navigations to be
    /// loaded, which is true of the read behind the membership LISTING - one statement projecting columns
    /// from all three tables. The single-membership read is a different repository member, and the one
    /// that answers it composes only the role: it is also on the assignment WRITE path, where an extra
    /// join would be paid on every enrolment for a projection that path never performs. Passing the
    /// resolved parts in is what lets the read stay as narrow as its other caller needs while this
    /// projection stays complete.
    /// </para>
    /// <para>
    /// The three arguments are asserted to describe ONE membership rather than trusted to, because a
    /// mismatched pairing would publish one account's terms under another's name - the exact disclosure
    /// the caller of this overload exists to avoid.
    /// </para>
    /// <para>
    /// Every value is copied across with no coercion, for the reasons given on the sibling overload: a
    /// null date is not turned into a minimum date and a minimum date is not turned into a null.
    /// </para>
    /// </remarks>
    public static RoleMembershipDto ToMembership(UserRole assignment, Role role, User account)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(account);

        if (assignment.RoleId != role.RoleId)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Assignment {assignment.UserRoleId} names role {assignment.RoleId}, not {role.RoleId}."),
                nameof(role));
        }

        if (assignment.UserId != account.UserId)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Assignment {assignment.UserRoleId} names account {assignment.UserId}, not {account.UserId}."),
                nameof(account));
        }

        return new RoleMembershipDto
        {
            UserRoleId = assignment.UserRoleId,
            UserId = assignment.UserId,
            Username = account.Username,
            DisplayName = account.DisplayName,
            RoleId = assignment.RoleId,
            RoleName = role.RoleName,
            EffectiveDate = assignment.EffectiveDate,
            ExpiryDate = assignment.ExpiryDate,
        };
    }

    /// <summary>
    /// Builds a new role aggregate from a creation request.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the role belongs to.</param>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved role aggregate.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    /// <remarks>
    /// The portal identifier comes from the route, never from the payload, so a caller cannot create a
    /// role inside another tenant.
    /// </remarks>
    // MIGRATION: portalId is assigned exactly as supplied, including -1. That is not a sentinel
    // here: it is the host portal, per the IDENTITY(-1, 1) seed cited on ToDetail above.
    public static Role ToNewRole(int portalId, CreateRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = new Role { PortalId = portalId };
        ApplyCore(
            role,
            request.RoleName,
            request.Description,
            request.RoleGroupId,
            request.IsPublic,
            request.AutoAssignment,
            request.ServiceFee,
            request.BillingPeriod,
            request.BillingFrequency,
            request.TrialFee,
            request.TrialPeriod,
            request.TrialFrequency,
            request.RsvpCode,
            request.IconFile);
        return role;
    }

    /// <summary>
    /// Applies a submitted update to a tracked role aggregate.
    /// </summary>
    /// <param name="role">The tracked role to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="role"/> or <paramref name="request"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Neither the role's own identifier nor its portal is written from the request: both arrive from
    /// the route, and the request contract deliberately carries neither.
    /// </para>
    /// <para>
    /// The role's NAME <em>is</em> written from the request, so an update replaces it like every other
    /// member. Uniqueness of the new name within the portal is not this projection's concern - it needs
    /// a read, so it is settled by <c>Application/Services/RoleService.cs</c> before this member is
    /// called.
    /// </para>
    /// </remarks>
    public static void ApplyUpdate(Role role, UpdateRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION - DOCUMENTED BEHAVIOURAL DIFFERENCE: the submitted name is applied, where the
        // legacy edit path could not change one. The edit screen revealed a read-only label and hid the
        // name textbox whenever it was editing an existing role
        // (Website/admin/Security/EditRoles.ascx.vb L131-L134), the legacy membership data contract
        // declared no parameter for the name on its update member
        // (Library/Providers/MembershipProviders/DataProvider/DataProvider.vb L97), the provider never
        // passed one (DNNRoleProvider.vb L325), and the terminal stored procedure omits the column from
        // its assignment list altogether
        // (Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider L454). The
        // library-level member this service layer replaces nevertheless took the whole role including
        // its name (RoleController.vb L254), and the terminal schema constrains the pair through
        // UNIQUE (PortalID, RoleName) at 03.00.09.SqlDataProvider L304, so a rename is expressible here
        // and its collision is a reportable outcome. Resubmitting the stored name is a no-op, so no
        // legacy submission behaves differently. Itemised in MIGRATION_NOTES.md.
        ApplyCore(
            role,
            request.RoleName,
            request.Description,
            request.RoleGroupId,
            request.IsPublic,
            request.AutoAssignment,
            request.ServiceFee,
            request.BillingPeriod,
            request.BillingFrequency,
            request.TrialFee,
            request.TrialPeriod,
            request.TrialFrequency,
            request.RsvpCode,
            request.IconFile);
    }

    /// <summary>
    /// Builds a new role group aggregate from a submitted contract.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the group belongs to.</param>
    /// <param name="request">The submitted group.</param>
    /// <returns>An unsaved role group aggregate.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    // MIGRATION: the tenant comes from the route, never from the payload, and the request contract no
    // longer carries one to be ignored. Its predecessor bound the RESPONSE projection, which advertised a
    // writable portal and a writable group identifier that this projection read from neither - so a
    // caller could name another tenant, be answered 201, and never learn the value had been discarded.
    public static RoleGroup ToNewGroup(int portalId, CreateRoleGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RoleGroup
        {
            PortalId = portalId,
            RoleGroupName = request.RoleGroupName,
            Description = request.Description,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked role group aggregate.
    /// </summary>
    /// <param name="roleGroup">The tracked group to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="roleGroup"/> or <paramref name="request"/> is null.
    /// </exception>
    // MIGRATION: neither the group's identifier nor its portal is written from the request, and the
    // request contract carries neither: both arrive in the route, so there is nothing for a body to
    // contradict. Its predecessor bound the response projection, which published both as writable while
    // this projection ignored them.
    public static void ApplyGroupUpdate(RoleGroup roleGroup, UpdateRoleGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        ArgumentNullException.ThrowIfNull(request);

        roleGroup.RoleGroupName = request.RoleGroupName;
        roleGroup.Description = request.Description;
    }

    /// <summary>
    /// Builds a new membership row from a submitted assignment, the role the route named and the two
    /// bounds the caller has already derived.
    /// </summary>
    /// <param name="roleId">Identifier of the role being granted, taken from the route.</param>
    /// <param name="request">The submitted assignment.</param>
    /// <param name="effectiveDate">
    /// The moment the membership begins, or <see langword="null"/> when it begins immediately. Already
    /// derived by the caller; this method performs no date arithmetic of its own.
    /// </param>
    /// <param name="expiryDate">
    /// The moment the membership lapses, or <see langword="null"/> when it never does. Already derived
    /// by the caller.
    /// </param>
    /// <returns>An unsaved membership row.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    /// <remarks>
    /// The two bounds are parameters rather than being computed here on purpose. Deriving them means
    /// reading a clock and walking the role's billing terms, and both belong to the service that owns
    /// the rule; see the note below.
    /// </remarks>
    // =============================================================================================
    // MIGRATION: flattening pattern F2 - the legacy UserRoleInfo reached its identity through VB
    // INHERITANCE rather than composition, and unpicking that is what this method exists to do.
    //
    // Library/Components/Users/UserRoleInfo.vb reads, verbatim:
    //     L42  Public Class UserRoleInfo
    //     L43      Inherits RoleInfo
    //
    // so the legacy object was a FLATTENED 23-property thing: the eight properties it declared
    // itself - UserRoleID L53, UserID L62, FullName L71, Email L80, EffectiveDate L89, ExpiryDate
    // L98, IsTrialUsed L107 and Subscribed L116 - plus all fifteen inherited from RoleInfo. It had
    // NO RoleID and NO RoleName OF ITS OWN: any legacy code reading userRole.RoleName was reading
    // RoleInfo.RoleName (L110) through the base class.
    //
    // The target UserRole declares six scalars and deliberately does NOT inherit Role. The role
    // facts therefore arrive by NAVIGATION (UserRole.Role) instead of by inheritance, which is why
    // this method takes a roleId and sets the foreign key rather than copying a role's members onto
    // the membership row. One object that was simultaneously a membership and a role becomes two
    // rows joined by a key, which is what the schema always said: dbo.UserRoles carries UserRoleID
    // IDENTITY(1, 1) (01.00.00.SqlDataProvider L239), UserID, RoleID, the two dates and the
    // trial-used flag, and nothing else.
    //
    // MIGRATION: three of the legacy eight are DROPPED, and a fourth is deliberately not projected.
    //   * FullName (L71) - view-derived. It was populated by the legacy read's join onto the user
    //     record, never stored on dbo.UserRoles, so it is a display concern and belongs to a user
    //     contract. Composing it here would require an already-loaded User navigation and would put
    //     a denormalisation on a write shape.
    //   * Email (L80) - view-derived for the same reason. Its absence is also why no EmailAddress
    //     value object appears anywhere in this file.
    //   * Subscribed (L116) - NOT a dbo.UserRoles column. Worth stating precisely: it was verified
    //     to be a plain settable Boolean (body at L116-L123), not a computed one, so it is dropped
    //     because the schema has nowhere to put it, not because it was derived.
    //   * NotifyUser, on RoleAssignmentRequest, is not projected either. It is not a column at all
    //     but a request for a side effect - the legacy "Send Notification?" checkbox - so it is the
    //     service's to act on and cannot be written to a membership row.
    //
    // MIGRATION: IsTrialUsed is seeded false rather than null on a NEW row. The target member is
    // bool? because the column is nullable, but a membership that has just been created has
    // demonstrably not consumed a trial, so false is the accurate fact and matches the legacy
    // NullBoolean sentinel (Null.vb L76, which is False rather than a distinct third state).
    // Renewals never reset it - see ApplyAssignmentUpdate.
    //
    // MIGRATION: no expiry arithmetic happens here, and that boundary is deliberate rather than
    // incidental. RoleController.vb computes the bound with a six-arm selection at L540-L547 -
    //     Case "N" -> Null.NullDate            (never expires)
    //     Case "O" -> New System.DateTime(9999, 12, 31)
    //     Case "D" -> DateAdd(DateInterval.Day,   Period,       ...)
    //     Case "W" -> DateAdd(DateInterval.Day,   Period * 7,   ...)
    //     Case "M" -> DateAdd(DateInterval.Month, Period,       ...)
    //     Case "Y" -> DateAdd(DateInterval.Year,  Period,       ...)
    // guarded at L537-L538 by "If Period = Null.NullInteger Then ExpiryDate = Null.NullDate", which
    // SHORT-CIRCUITS BEFORE the selection is reached. That whole rule, including the short-circuit,
    // lives in RoleService.DeriveAssignmentDates, which is also where Imports Microsoft.VisualBasic
    // (RoleController.vb L25 - the only in-scope occurrence) is discharged by rewriting DateAdd as
    // AddDays, AddDays(n * 7), AddMonths and AddYears. The service additionally owns the
    // consequence that the legacy read was server-LOCAL (L496 is
    // DateAdd(DateInterval.Day, -1, Date.Today())) while IClock.UtcNow is UTC-only, which can shift
    // a date-only value by one calendar day.
    // =============================================================================================
    public static UserRole ToNewAssignment(
        int roleId,
        RoleAssignmentRequest request,
        DateTime? effectiveDate,
        DateTime? expiryDate)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new UserRole
        {
            UserId = request.UserId,
            RoleId = roleId,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
            IsTrialUsed = false,
        };
    }

    /// <summary>
    /// Rewrites the two bounds of a membership the member already holds.
    /// </summary>
    /// <param name="assignment">The tracked membership to revise.</param>
    /// <param name="effectiveDate">The derived start bound, or <see langword="null"/> for none.</param>
    /// <param name="expiryDate">The derived expiry bound, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assignment"/> is null.
    /// </exception>
    // MIGRATION: exactly two members are writable on a renewal, and the omissions are the point.
    // The legacy assignment member was an upsert - it inserted when the member did not yet hold the
    // role and otherwise revised only the two dates - so UserRoleId, UserId and RoleId are never
    // touched, because a renewal is not a move. IsTrialUsed is never reset either: that fact is what
    // stops a cancelled subscriber restarting a paid trial, and the removal path depends on it
    // surviving (RoleController.vb L495-L496 back-dates the expiry instead of deleting the row
    // precisely so the flag is retained).
    //
    // MIGRATION: NullDate is DateTime.MinValue (Null.vb L66), and Null.IsNull compared DATE PARTS
    // ONLY - "objDate.Date.Equals(NullDate.Date)" at L224 - so any legacy DateTime falling on
    // 0001-01-01 was treated as unset REGARDLESS OF ITS TIME COMPONENT. Both bounds here are
    // DateTime?, so "unset" is expressed by null and by nothing else. Reading the marker as absence is
    // the job of the two boundaries that own it, both of which compare date parts exactly as Null.vb
    // did: RoleService interprets a submitted marker, and RoleRepository discards one before staging a
    // row. The Domain classifier recognises no marker at all. This method neither classifies nor
    // normalises: it assigns.
    public static void ApplyAssignmentUpdate(
        UserRole assignment,
        DateTime? effectiveDate,
        DateTime? expiryDate)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        assignment.EffectiveDate = effectiveDate;
        assignment.ExpiryDate = expiryDate;
    }

    /// <summary>
    /// Writes the member set that the creation and update requests share, so the two paths cannot
    /// drift apart.
    /// </summary>
    /// <remarks>
    /// The two request contracts declare identical member sets on purpose - the legacy edit screen was
    /// one screen serving both operations - so the assignment order and the flooring rule are stated
    /// once here rather than twice.
    /// </remarks>
    // =============================================================================================
    // MIGRATION: the monetary floor. A negative submission is stored as zero on BOTH paths,
    // reproducing the legacy clamp measured verbatim in Library/Components/Portal/PortalController.vb:
    //     L395  objRoleInfo.ServiceFee = CType(IIf(serviceFee < 0, 0, serviceFee), Single)
    //     L398  objRoleInfo.TrialFee   = CType(IIf(trialFee  < 0, 0, trialFee),  Single)
    // The legacy IIf is a FUNCTION and evaluates BOTH arms, whereas a C# conditional short-circuits.
    // The two are equivalent here only because both arms are side-effect-free constants and reads -
    // the equivalence is not general, and PortalMappings.ClampFee is written as a comparison rather
    // than Math.Max for exactly that reason. The legacy edit screen's own comparison validators
    // refused a negative fee before it reached the store, so the floor changes no accepted input and
    // only removes a way to store a nonsensical value.
    //
    // MIGRATION: an ABSENT fee is NOT floored, and that distinction is load-bearing. The guard is
    // "fee is null ? null : ClampFee(...)", so a null passes through untouched: a role with NO
    // charge is not a role charged NOTHING. The legacy encoding is why this matters - Null.vb
    // defines NullSingle as Single.MinValue (L51) and NullDecimal as Decimal.MinValue (L61), so an
    // unset fee arrived as a huge negative magnitude, and a blanket floor would have silently
    // converted "no fee set" into "free". Nothing here coalesces a fee to 0m.
    //
    // MIGRATION: Single -> decimal is a widening WITH a precision-semantics change - binary floating
    // point to base-10 fixed point. It is exactly right for the money columns
    // (Roles.ServiceFee money NULL at 01.00.05 L2752, Roles.TrialFee money NULL at 01.00.08 L6830;
    // the baseline had ServiceFee as decimal(5,2) at 01.00.00 L119) but it IS a real behavioural
    // difference for values that were never exactly representable in Single, so it is recorded
    // rather than absorbed.
    //
    // MIGRATION: BillingPeriod is int?, and three sources disagreed about it. The membership
    // provider declared the parameter As String, RoleInfo.vb L218 declared the property As Integer,
    // and the terminal column is "BillingPeriod int NULL" (01.00.08.SqlDataProvider L6829). Rule T4
    // makes the schema authoritative, so int? wins. TrialPeriod (RoleInfo.vb L203, column int NULL
    // at 01.00.05 L2754) was never in dispute.
    //
    // MIGRATION: RSVPCode -> RsvpCode is a CASING rename only (RoleInfo.vb L278). The column keeps
    // its legacy spelling, RSVPCode; reconciling the two is the entity configuration's job in
    // Infrastructure, not this file's.
    //
    // MIGRATION: NullString was the EMPTY STRING, not null (Null.vb L71), so the legacy layer could
    // not tell a SQL NULL from "". Nothing here converts "" to null or null to "": every string
    // member is assigned exactly as submitted, which is what keeps the two states distinguishable
    // now that the schema's nullability is finally observable.
    //
    // MIGRATION: a latent legacy defect, recorded and NOT fixed, per the preserve-behaviour rule.
    // Null.vb's PropertyInfo overload maps BOTH System.Int32 AND System.Int64 onto the Int32-sized
    // -1 sentinel (L123, "Case "System.Int32", "System.Int64""), so a 64-bit member would have been
    // nulled by a 32-bit marker. No member on this aggregate is 64-bit, so nothing here depends on
    // it; it is noted so that a later widening does not inherit the bug silently.
    // =============================================================================================
    private static void ApplyCore(
        Role role,
        string roleName,
        string? description,
        int? roleGroupId,
        bool isPublic,
        bool autoAssignment,
        decimal? serviceFee,
        int? billingPeriod,
        Domain.Enums.BillingFrequency? billingFrequency,
        decimal? trialFee,
        int? trialPeriod,
        Domain.Enums.BillingFrequency? trialFrequency,
        string? rsvpCode,
        string? iconFile)
    {
        // MIGRATION: THE NAME IS TRIMMED BEFORE IT IS STORED, BECAUSE THE UNIQUENESS RULE ALREADY TRIMS IT.
        // The uniqueness index is UNIQUE (PortalID, RoleName) (03.00.09.SqlDataProvider L304) and is
        // evaluated under the database's collation, which gives trailing whitespace no sort weight - so
        // GetByNameAsync, the duplicate check every write runs first, treats "Editors  " and "Editors" as
        // one name. Storing the padding verbatim while matching on the trimmed form left the grid showing a
        // name that could not be re-created: runtime testing stored a 17-character padded name and was then
        // refused 409 for the 12-character name it appears to be, with no way to reconcile the two by
        // inspection. Trimming here makes the stored value and the value that governs uniqueness the same
        // string.
        //
        // Legacy also stored the padding - an ASP.NET TextBox does not trim - so this is a deliberate
        // divergence rather than a reproduction, recorded in MIGRATION_NOTES.md and taken on the same
        // footing as the invisible-character rule the write validators already apply: a role name is an
        // identifier an operator has to be able to read, retype and tell apart from a visibly identical
        // one. It cannot make a previously accepted name invalid, and it changes no name that carries no
        // surrounding whitespace. Interior whitespace is untouched, so "Content Editors" is unaffected.
        role.RoleName = roleName.Trim();
        role.Description = description;
        role.RoleGroupId = roleGroupId;
        role.IsPublic = isPublic;
        role.AutoAssignment = autoAssignment;

        // MIGRATION: BOTH FEES ARE STAGED AT THE STORED COLUMN'S OWN SCALE, so the aggregate holds what the
        // row will hold. The columns are money [03.01.01:L1173 and 01.00.08:L6830], which keeps four
        // fractional digits, and a submitted fee carrying more used to survive in memory and be rounded by
        // the store - after which the created response, taken from the aggregate, reported a number the
        // installation did not have. The rounding is the column's, stated once on
        // SqlServerRange.ToStoredMoney together with the measurement that prompted it; the floor below it is
        // the legacy role-fee clamp, and the two are applied in that order so a negative submission becomes
        // zero before the scale is applied rather than after.
        role.ServiceFee = serviceFee is null
            ? null
            : SqlServerRange.ToStoredMoney(PortalMappings.ClampFee(serviceFee.Value));
        role.BillingPeriod = billingPeriod;
        role.BillingFrequency = billingFrequency;
        role.TrialFee = trialFee is null
            ? null
            : SqlServerRange.ToStoredMoney(PortalMappings.ClampFee(trialFee.Value));
        role.TrialPeriod = trialPeriod;
        role.TrialFrequency = trialFrequency;
        role.RsvpCode = rsvpCode;
        role.IconFile = iconFile;
    }
}
