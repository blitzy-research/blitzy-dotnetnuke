using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/roles</c>: everything a caller may supply when creating a security
/// role, including its paid-membership terms.
/// </summary>
// MIGRATION: three groups of members are deliberately absent and must not be added.
public sealed class CreateRoleRequest
{
    /// <summary>Name of the role. Required, and unique within the owning portal.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleName</c>, capped at fifty characters by its <c>MaxLength</c>
    /// and carrying the screen's one required-field validator <c>valRoleName</c>. Terminal column
    /// <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c>), covered together with
    /// the portal column by the uniqueness constraint <c>IX_RoleName</c> (<c>03.00.09.SqlDataProvider</c>).
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>Free-text description of the role, or <see langword="null"/> when it has none.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Recurring fee charged for membership of the role, or <see langword="null"/> when the role is free.
    /// </summary>
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing-frequency units between charges, or <see langword="null"/> when the role carries
    /// no recurring billing term.
    /// </summary>
    // Null is load-bearing here and is NOT interchangeable with zero.
    public int? BillingPeriod { get; set; }

    /// <summary>Unit of the recurring billing term, or <see langword="null"/> when the role carries none.</summary>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>Fee charged for the trial period, or <see langword="null"/> when the role offers no trial.</summary>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial-frequency units the trial runs for, or <see langword="null"/> when the role offers
    /// no trial.
    /// </summary>
    // Int on the same three-against-one evidence as the billing period, and null is equally load-bearing,
    // because the terminal projection returns this member only when the stored trial frequency is not the
    // never code (01.00.08).
    public int? TrialPeriod { get; set; }

    /// <summary>Unit of the trial term, or <see langword="null"/> when the role offers no trial.</summary>
    // The never code is a REAL STORED VALUE meaning "no trial", not an unset marker, and the two must stay
    // distinct in a reader's mind.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary><see langword="true"/> when users may subscribe to the role themselves.</summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkIsPublic</c>, with no validator. Terminal column <c>Roles.IsPublic
    /// bit NOT NULL</c> with the store default zero (<c>01.00.08.SqlDataProvider</c>, retyped and
    /// re-defaulted at <c>03.01.01.SqlDataProvider</c>).
    /// </remarks>
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every existing user of the portal in the role as it is created.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkAutoAssignment</c>, with no validator. Terminal column
    /// <c>Roles.AutoAssignment bit NOT NULL</c> with the store default zero
    /// (<c>01.00.08.SqlDataProvider</c>, retyped and re-defaulted at <c>03.01.01.SqlDataProvider</c>).
    /// </remarks>
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Identifier of the role group to file the role under, or <see langword="null"/> to leave it ungrouped
    /// - the case the legacy screen presented as "Global Roles".
    /// </summary>
    // Two consequences, each a real defect if ignored:
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Code a user may redeem to be granted the role, or <see langword="null"/> when the role has none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRSVPCode</c>, capped at fifty characters and with no validator.
    /// Nothing in the schema makes the code unique, so it is not an identifier and a clash is not a
    /// conflict.
    /// </remarks>
    // MIGRATION: the derived invitation URL that sat beside this box on the legacy screen is deliberately
    // absent.
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Relative path of the image that represents the role, or <see langword="null"/> when it has none.
    /// </summary>
    public string? IconFile { get; set; }
}
