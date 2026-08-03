namespace DnnMigration.Application.Validation;

/// <summary>
/// The field rules shared by the two role-group write contracts - the two column widths and the
/// measured legacy wording of the screen's single validator - declared once and applied by both the
/// create and the update role-group validators.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two callers.</b> A group name submitted on create and a group name submitted on
/// update are the same value bound for the same column, so a rule that held on one path and not the
/// other would be a rule a caller could bypass simply by choosing the other verb. The sibling role
/// contracts had exactly that defect on their icon-path rule before it was hoisted, which is why the
/// pattern established by <c>Application/Validation/RoleTermsRules.cs</c> is followed here from the
/// outset rather than after the fact.
/// </para>
/// <para>
/// <b>The widths are this table's own, even though they coincide with the sibling role table's.</b>
/// <c>RoleGroups.RoleGroupName</c> and <c>Roles.RoleName</c> are both fifty characters today, and
/// <c>RoleGroups.Description</c> and <c>Roles.Description</c> are both a thousand; they are declared
/// here from the role-group table's own columns because they are a different table's constraints and
/// must remain free to differ. The MESSAGE is taken from the shared role rules instead, because the two
/// screens genuinely declared identical wording and sharing the constant reproduces the legacy text
/// rather than harmonising two different texts into one.
/// </para>
/// <para>
/// <b>Nothing here reads state, configuration or a clock.</b> Every member is a compile-time constant,
/// which is what keeps both validators parameterless. The composite uniqueness constraint over
/// <c>(PortalID, RoleGroupName)</c> is a question about what is already stored, so a clash is an
/// expected failure raised by <c>Application/Services/RoleService.cs</c> and reported as a conflict.
/// </para>
/// </remarks>
// MIGRATION: both rules are measured from Website/admin/Security/EditGroups.ascx, and the file must be
// read case-insensitively to find them - the markup writes its tags in lower case, so a search for
// "RequiredFieldValidator" returns nothing while "requiredfieldvalidator" returns the rule. L11 declares
// the name text box with maxlength="50" and L12 declares valRoleGroupName beside it, a
// requiredfieldvalidator whose errormessage is "<br>You Must Enter a Valid Name". L17 declares the
// description text box with maxlength="1000" and NO validator of any kind. That is the entire field rule
// set for this screen: presence plus width on the name, width alone on the description.
internal static class RoleGroupTermsRules
{
    /// <summary>
    /// Width of <c>RoleGroups.RoleGroupName nvarchar(50) NOT NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L20, recreated identically at
    /// <c>04.00.04.SqlDataProvider</c> L53), which the legacy screen mirrored as
    /// <c>maxlength="50"</c> on its name text box at <c>EditGroups.ascx</c> L11.
    /// </summary>
    internal const int RoleGroupNameMaximumLength = 50;

    /// <summary>
    /// Width of <c>RoleGroups.Description nvarchar(1000) NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L21), which the legacy screen mirrored as
    /// <c>maxlength="1000"</c> on its description text box at <c>EditGroups.ascx</c> L17.
    /// </summary>
    internal const int DescriptionMaximumLength = 1000;

    /// <summary>
    /// Wording of <c>valRoleGroupName</c> (<c>EditGroups.ascx</c> L12), with its leading markup tag
    /// removed.
    /// </summary>
    /// <remarks>
    /// Aliased to <see cref="RoleTermsRules.RoleNameRequiredMessage"/> rather than restated, because the
    /// sibling role editor declared the identical sentence at <c>editroles.ascx</c> L31 and two copies of
    /// one measured string can drift apart.
    /// </remarks>
    internal const string RoleGroupNameRequiredMessage = RoleTermsRules.RoleNameRequiredMessage;
}
