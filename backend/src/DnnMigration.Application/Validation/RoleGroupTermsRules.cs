namespace DnnMigration.Application.Validation;

/// <summary>
/// The field rules shared by the two role-group write contracts - the two column widths and the measured
/// legacy wording of the screen's single validator - declared once and applied by both the create and the
/// update role-group validators.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two callers.</b> A group name submitted on create and a group name submitted on
/// update are the same value bound for the same column, so a rule that held on one path and not the other
/// would be a rule a caller could bypass simply by choosing the other verb.
/// </para>
/// <para>
/// <b>The widths are this table's own, even though they coincide with the sibling role table's.</b>
/// <c>RoleGroups.RoleGroupName</c> and <c>Roles.RoleName</c> are both fifty characters today, and
/// <c>RoleGroups.Description</c> and <c>Roles.Description</c> are both a thousand; they are declared here
/// from the role-group table's own columns because they are a different table's constraints and must remain
/// free to differ.
/// </para>
/// </remarks>
// Both rules are measured from Website/admin/Security/EditGroups.ascx, and the file must be read
// case-insensitively to find them - the markup writes its tags in lower case, so a search for
// "RequiredFieldValidator" returns nothing while "requiredfieldvalidator" returns the rule.
internal static class RoleGroupTermsRules
{
    /// <summary>
    /// Width of <c>RoleGroups.RoleGroupName nvarchar(50) NOT NULL</c> (<c>03.02.03.SqlDataProvider</c> L20,
    /// recreated identically at <c>04.00.04.SqlDataProvider</c> L53), which the legacy screen mirrored as
    /// <c>maxlength="50"</c> on its name text box at <c>EditGroups.ascx</c> L11.
    /// </summary>
    internal const int RoleGroupNameMaximumLength = 50;

    /// <summary>
    /// Width of <c>RoleGroups.Description nvarchar(1000) NULL</c> (<c>03.02.03.SqlDataProvider</c> L21),
    /// which the legacy screen mirrored as <c>maxlength="1000"</c> on its description text box at
    /// <c>EditGroups.ascx</c> L17.
    /// </summary>
    internal const int DescriptionMaximumLength = 1000;

    /// <summary>Wording of <c>valRoleGroupName</c>, with its leading markup tag removed.</summary>
    internal const string RoleGroupNameRequiredMessage = RoleTermsRules.RoleNameRequiredMessage;
}
