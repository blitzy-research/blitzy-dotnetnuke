namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for assigning a user to a security role: the user to assign, the optional dates that
/// bound the membership, and whether the assignment should notify that user.
/// </summary>
/// <remarks>
/// The portal and the role are absent because both arrive in the route, which is what keeps them
/// authoritative: a tenant a caller can restate in the body is a tenant a caller can contradict, and the
/// identifier of the role being modified must have exactly one source. <c>UserRoleID</c> is absent because
/// the store assigns it and nothing needs it on the wire - the legacy grid's static
/// <c>datakeyfield="UserRoleID"</c> is overwritten at runtime with either <c>UserId</c> or <c>RoleId</c>
/// (lines 244 and 251 of the code-behind), so even the legacy delete path identified an assignment by the
/// user-and-role pair rather than by the surrogate key.
/// </remarks>
public sealed class RoleAssignmentRequest
{
    /// <summary>Identifier of the user to assign to the role. Required.</summary>
    /// <remarks>
    /// Target column <c>UserRoles.UserID int NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> line 240, table
    /// declared at line 238).
    /// </remarks>
    // Non-nullable, because an assignment without a user has no meaning and the column is NOT NULL. Absence
    // is therefore a validation failure, never a sentinel, and this type performs no absence test of its
    // own.
    public int UserId { get; set; }

    /// <summary>Instant from which the membership takes effect, or <see langword="null"/> for immediately.</summary>
    /// <remarks>
    /// Target column <c>UserRoles.EffectiveDate datetime NULL</c>.
    /// </remarks>
    // Only the terminal schema counts (Rule T4).
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Instant at which the membership lapses, or <see langword="null"/> to let the service derive one from
    /// the role's trial and billing terms.
    /// </summary>
    /// <remarks>
    /// Target column <c>UserRoles.ExpiryDate datetime NULL</c> (<c>01.00.00.SqlDataProvider</c> line 242).
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Whether the assigned user should be notified of the membership. Not persisted: no column backs this
    /// member.
    /// </summary>
    public bool NotifyUser { get; set; }
}
