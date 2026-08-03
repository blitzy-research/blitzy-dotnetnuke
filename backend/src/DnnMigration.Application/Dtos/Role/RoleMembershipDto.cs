namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Wire contract for one DotNetNuke role membership: the assignment that joins an account to a security
/// role for a period, together with the account and role identification the legacy membership grid
/// displayed alongside it.
/// </summary>
/// <remarks>
/// <para>
/// Why this shape exists. The legacy role-membership screen renders five columns, measured at
/// <c>Website/admin/Security/securityroles.ascx:L71-L86</c>: the account (through
/// <c>FormatUser(UserID, FullName)</c>), the role name as a bound column, and then
/// <c>FormatDate(EffectiveDate)</c> and <c>FormatDate(ExpiryDate)</c>. The two dates are the whole point
/// of the screen - a DotNetNuke membership is time-bounded, and a paid membership is expressed entirely
/// through them - so a projection that omits them cannot reproduce the screen at all.
/// </para>
/// <para>
/// MIGRATION: an earlier revision of the role-membership listing returned the ACCOUNT projection,
/// <c>UserListItemDto</c>, and recorded the two dates as deliberately absent on the grounds that "no
/// projection named by AAP 0.4.1.1 carries them and this contract may not invent one". That reasoning is
/// superseded here. AAP 0.4.1.2 introduces the same list as "roughly 40 DTOs", so it is indicative rather
/// than a closed set; AAP 0.5.1.2 directs that request and response shapes be "derived from what the
/// legacy screens actually posted and rendered, not from the entity shape"; and AAP 0.9.1 requires that
/// every workflow reachable from the legacy admin pages be supported. A read surface that cannot answer
/// when a membership starts or ends fails all three. The dates travelling on the write path - the
/// assignment request - does not substitute for a read: it lets a caller SET a date it can never
/// afterwards SEE.
/// </para>
/// <para>
/// What it carries, and why each member is here rather than merely available. The account key and display
/// name are the first rendered column. The role name is the second, and the role key accompanies it
/// because the legacy row's delete affordance is governed by <c>DeleteButtonVisible(UserID, RoleID)</c>
/// (<c>securityroles.ascx:L68</c>), which needs both keys - a client that could not identify the role of
/// a row could not offer the action the legacy row offered. The assignment key identifies the membership
/// itself, which is what distinguishes this from a pairing of two other records. The login name is
/// carried because it is the account's stable, human-meaningful identifier and the sibling direction of
/// this same relation is keyed by it; the legacy grid rendered the display name only, but a client
/// linking a row back to an account needs the name the account is addressed by.
/// </para>
/// <para>
/// What it deliberately omits. The account fields the previous projection carried - electronic mail
/// address, approval, lock-out, online and host flags, creation and last-login dates - are gone, because
/// AAP 0.5.1.2 derives the shape from what the screen rendered and this screen rendered none of them.
/// They remain available from the account endpoints, which is where an administrator looking at an
/// account rather than at a membership goes. The trial-used flag is also omitted: the terminal statement
/// does return it, but the screen renders no column for it and it is an input to the paid-membership
/// write path rather than a fact this read exists to publish.
/// </para>
/// <para>
/// Both dates are nullable, and the nullability is the point rather than a convenience. The legacy screen
/// rendered an absent date as the empty string - <c>SecurityRoles.ascx.vb</c> <c>FormatDate</c> tests
/// <c>Null.IsNull</c> and returns <c>""</c> - because the legacy absence marker for a date was
/// <c>Date.MinValue</c>, a sentinel that is indistinguishable from a real date once it reaches a client.
/// AAP Rule T7 keeps the domain honest with a nullable type, and this contract carries that nullability
/// out to the wire so a consumer reads absence as absence and never has to recognise a magic date.
/// </para>
/// <para>
/// The type is an inert data carrier: it holds no behaviour, performs no validation and reaches no
/// database. Translation from the persisted assignment lives in
/// <c>Application/Mapping/RoleMappings.cs</c>.
/// </para>
/// </remarks>
public sealed class RoleMembershipDto
{
    /// <summary>
    /// Gets or sets the identifier of the membership itself.
    /// </summary>
    /// <remarks>
    /// The <c>UserRoles.UserRoleID</c> key, projected by the terminal statement as <c>UR.UserRoleID</c>.
    /// It identifies the assignment as a record in its own right, which the pairing of account and role
    /// keys does not: the same account may hold the same role again after an earlier membership ended.
    /// </remarks>
    public int UserRoleId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the account holding the membership.
    /// </summary>
    /// <remarks>
    /// The first value the legacy grid bound, at <c>securityroles.ascx:L73</c>. No lower bound is implied:
    /// the account table is <c>IDENTITY (1, 1)</c>, but this contract states a key and tests nothing.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the login name of the account holding the membership.
    /// </summary>
    /// <remarks>
    /// Unique within a portal rather than across the installation, which is why every read of it is
    /// portal-scoped. Never empty in practice, but declared as a plain string with an empty default so
    /// that the contract has no null state for a value the column declares NOT NULL.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the account holding the membership.
    /// </summary>
    /// <remarks>
    /// The value the legacy grid actually showed for the account, projected by the terminal statement as
    /// <c>U.DisplayName As FullName</c> and rendered through <c>FormatUser</c>.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identifier of the role the membership grants.
    /// </summary>
    /// <remarks>
    /// Zero is a real key here: <c>Roles.RoleID</c> is seeded <c>IDENTITY (0, 1)</c>, so no consumer may
    /// treat zero as absent.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>
    /// Gets or sets the name of the role the membership grants.
    /// </summary>
    /// <remarks>
    /// The legacy grid's second column, a bound column on <c>RoleName</c> at
    /// <c>securityroles.ascx:L76</c>. Carried rather than left to a second lookup because the row is
    /// meaningless without it.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the moment the membership takes effect, or <see langword="null"/> when it has no
    /// start bound and is effective immediately.
    /// </summary>
    /// <remarks>
    /// The legacy grid's third column. Absence is genuine absence, not a sentinel date: see the remarks
    /// on the type for why the legacy <c>Date.MinValue</c> marker does not travel.
    /// </remarks>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Gets or sets the moment the membership ceases, or <see langword="null"/> when it does not expire.
    /// </summary>
    /// <remarks>
    /// The legacy grid's fourth column, and the field the legacy screen validated against the effective
    /// date - <c>securityroles.ascx:L47</c> requires it to be strictly greater. Absence means the
    /// membership is open-ended, which is the ordinary case for an unpaid role.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }
}
