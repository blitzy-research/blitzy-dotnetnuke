namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Wire contract for one DotNetNuke role membership: the assignment that joins an account to a security
/// role for a period, together with the account and role identification the legacy membership grid
/// displayed alongside it.
/// </summary>
/// <remarks>
/// <para>
/// Why this shape exists. The legacy role-membership screen renders five columns, measured at
/// <c>Website/admin/Security/securityroles.ascx:L71-L86</c>: the account (through <c>FormatUser(UserID,
/// FullName)</c>), the role name as a bound column, and then <c>FormatDate(EffectiveDate)</c> and
/// <c>FormatDate(ExpiryDate)</c>.
/// </para>
/// <para>
/// What it carries, and why each member is here rather than merely available. The account key and display
/// name are the first rendered column.
/// </para>
/// </remarks>
public sealed class RoleMembershipDto
{
    /// <summary>Gets or sets the identifier of the membership itself.</summary>
    public int UserRoleId { get; set; }

    /// <summary>Gets or sets the identifier of the account holding the membership.</summary>
    /// <remarks>
    /// The first value the legacy grid bound, at <c>securityroles.ascx:L73</c>. No lower bound is implied:
    /// the account table is <c>IDENTITY (1, 1)</c>, but this contract states a key and tests nothing.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the login name of the account holding the membership.</summary>
    /// <remarks>
    /// Unique within a portal rather than across the installation, which is why every read of it is
    /// portal-scoped. Never empty in practice, but declared as a plain string with an empty default so that
    /// the contract has no null state for a value the column declares NOT NULL.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the account holding the membership.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the identifier of the role the membership grants.</summary>
    /// <remarks>
    /// Zero is a real key here: <c>Roles.RoleID</c> is seeded <c>IDENTITY (0, 1)</c>, so no consumer may
    /// treat zero as absent.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>Gets or sets the name of the role the membership grants.</summary>
    /// <remarks>
    /// The legacy grid's second column, a bound column on <c>RoleName</c> at <c>securityroles.ascx:L76</c>.
    /// Carried rather than left to a second lookup because the row is meaningless without it.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the moment the membership takes effect, or <see langword="null"/> when it has no start
    /// bound and is effective immediately.
    /// </summary>
    /// <remarks>
    /// The legacy grid's third column. Absence is genuine absence, not a sentinel date: see the remarks on
    /// the type for why the legacy <c>Date.MinValue</c> marker does not travel.
    /// </remarks>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Gets or sets the moment the membership ceases, or <see langword="null"/> when it does not expire.
    /// </summary>
    /// <remarks>
    /// As with the effective date, absence is the member's OMISSION rather than a written <c>null</c>.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }
}
