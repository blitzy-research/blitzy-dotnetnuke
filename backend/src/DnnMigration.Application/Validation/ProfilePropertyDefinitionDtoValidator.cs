using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="ProfilePropertyDefinitionDto"/>, the body of both
/// <c>POST /api/v1/portals/{portalId}/profile-definitions</c> and
/// <c>PUT /api/v1/portals/{portalId}/profile-definitions/{propertyDefinitionId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Both profile-definition write actions advertised a field-error
/// response but no <see cref="IValidator{T}"/> resolved for their body. An absent category or name
/// therefore reached a <c>NOT NULL</c> column, an over-long one reached a fifty-character column, and
/// a name containing characters the legacy screen forbade was stored and then travelled onward into
/// every profile form that renders it - each producing a persistence fault or a silent oddity rather
/// than the declared per-field response.
/// </para>
/// <para>
/// <b>Where these rules come from.</b> The legacy edit screen declares no validators in its markup,
/// and that is not an absence of rules. <c>Website/admin/Users/EditProfileDefinition.ascx</c> L23
/// hosts a single <c>dnn:propertyeditorcontrol</c> bound to the definition object itself, so the
/// control generated its editors and its validators from the attributes decorating
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>. That class is the authority,
/// and it is where every rule below was measured: four members carry <c>Required(True)</c> - the data
/// type at L90, the category at L193, the name at L228 and the view order at L300 - and the name
/// additionally carries a <c>RegularExpressionValidator</c> at L228. Nothing else on that class is
/// decorated with a rule.
/// </para>
/// <para>
/// <b>One shape, two verbs, one validator.</b> The contract serves reads and both writes with a single
/// type, so a single validator governs both verbs by construction and the two paths cannot diverge.
/// </para>
/// <para>
/// <b>What this type deliberately does not do.</b> It reads no store and resolves no lookup, so it
/// asserts neither that the data-type key names a real list entry nor that the module-definition
/// reference exists; the <c>Lists</c> subsystem is outside this migration's scope, which is exactly
/// why the data-type key travels as an opaque integer. It says nothing about the composite uniqueness
/// of <c>(PortalID, ModuleDefID, PropertyName)</c>, which is a question about stored state and an
/// expected failure owned by the service - and which the terminal insert procedure itself answers by
/// looking the name up before deciding whether to insert. And it does not interpret the contents of
/// <c>ValidationExpression</c>: that string is a rule to be applied to a profile submission later, not
/// a rule about this field, so only its width is asserted.
/// </para>
/// </remarks>
// MIGRATION: the terminal schema is the authority for every width, and the destructive upgrade chain
// makes the baseline actively misleading. ProfilePropertyDefinition does not exist in
// 01.00.00.SqlDataProvider at all; it is created at 03.02.03.SqlDataProvider L1062 and recreated at
// 04.00.04.SqlDataProvider L1107. PortalID is then widened to NULL at 03.03.03.SqlDataProvider L78 and
// again at 04.03.03.SqlDataProvider L78, ValidationExpression is widened from nvarchar(100) to
// nvarchar(2000) at 04.03.05.SqlDataProvider L17, and DefaultValue is retyped to ntext at
// 04.05.00.SqlDataProvider L1594. The widths below are the terminal ones, and they agree with
// Infrastructure/Persistence/Configurations/ProfilePropertyDefinitionConfiguration.cs, which is this
// repository's authoritative statement of that terminal state.
//
// MIGRATION: the two terminal stored procedures disagree with each other about two of those widths,
// and the disagreement is reported rather than reconciled into a narrower rule. The terminal insert,
// AddPropertyDefinition at 04.06.00.SqlDataProvider L1101, declares @DefaultValue ntext and
// @ValidationExpression nvarchar(2000), matching the columns. The terminal update,
// UpdatePropertyDefinition at 04.05.00.SqlDataProvider L1685, still declares @DefaultValue
// nvarchar(50) and @ValidationExpression nvarchar(100), because it was last recreated one script
// earlier than the widening. Its narrower parameters would silently truncate a value an upgraded
// database already holds - a real latent defect in the legacy application. It is annotated here rather
// than inherited: this migration writes through EF Core and not through either procedure, so the
// columns' own widths are the only limits that apply, and adopting the update procedure's narrower
// numbers would refuse values the store legitimately contains.
//
// MIGRATION: NotEmpty rather than NotNull on the two mandatory text members, matching how the operator
// experienced the rule. Both columns are NOT NULL and NOT NULL is satisfied by the empty string, but
// the generated required-field editor refused a blank control before any postback ran, so no legacy
// submission could carry one. NotNull alone would admit a value the legacy screen could not produce.
//
// MIGRATION: the name pattern is reproduced character for character from the RegularExpressionValidator
// attribute at ProfilePropertyDefinition.vb L228 and is neither broadened nor tightened. It admits
// letters, digits, full stop, underscore, percent, hyphen, plus and apostrophe. Two consequences are
// worth stating because they look like defects and are not. It admits no space, which is deliberate:
// the property name is an identifier used as a resource key for the localised label, and the legacy
// screen carried a separate localisation step for the human-readable text
// (EditProfileDefinition.ascx L53, txtPropertyName on the localisation wizard step). And it is
// anchored at both ends, so a name that merely contains a permitted run is still refused.
//
// MIGRATION: -1 IS ADMITTED on the view order, and this is the single most important rule in this file
// to get right. It is not an absence marker to be refused, clamped or normalised: the terminal insert
// procedure branches on IF @vieworder=-1 at 04.06.00.SqlDataProvider L1122 and substitutes
// MAX(ViewOrder) + 1, so -1 is the caller's instruction to append. The bound is therefore
// GreaterThanOrEqualTo(-1), which admits both that instruction and every real order while refusing
// values that neither terminal procedure can act on. Note the asymmetry: the terminal update procedure
// at 04.05.00.SqlDataProvider L1685 has no such branch and writes the value verbatim, so the sentinel
// means "append" on one verb and is a literal order on the other. That asymmetry is legacy behaviour
// and is preserved; interpreting the sentinel is the service's business, never this validator's.
//
// MIGRATION: the data-type bound admits ZERO, and the decision is measured rather than lenient. In a
// real DotNetNuke database the value is always a positive key: it names a row of dbo.Lists whose EntryID
// is seeded IDENTITY(1,1) (03.00.01.SqlDataProvider L842), and the seeding procedure
// AddDefaultPropertyDefinitions resolves each definition's type by looking the name up in that table
// (03.02.03.SqlDataProvider L1247-L1257) before passing the resulting key to AddPropertyDefinition from
// L1262 onwards. A strictly positive bound would therefore look correct. It is refused for two reasons.
// First, the Lists subsystem is outside this migration's scope, so no lookup exists to enumerate valid
// keys and no endpoint can offer one - a caller has nothing from which to obtain a positive key, and a
// rule demanding one would make both write verbs unusable rather than safer, which Minimal Change Clause
// item 4 forbids. Second, the legacy rule genuinely was weaker than it appears: Required(True) makes the
// property editor emit an ASP.NET RequiredFieldValidator against the generated control
// (Library/Controls/PropertyEditor/FieldEditorControl.vb L842-L852), and a required-field validator bound
// to a drop-down list only refuses the empty initial value - it could not refuse a key whose magnitude
// was wrong, because the list offered no such entry to choose. So the bound refuses only what can name
// nothing under any reading, a negative key, and the missing lookup is itemised in the repository
// migration notes rather than approximated by a rule.
//
// MIGRATION: the lower bound of zero on the length is net-new, and it is the only rule here whose
// operator has no legacy ancestor at all. The generated integer editor would have accepted a negative number, but the member
// is a character count whose column carries DEFAULT 0 (03.02.03.SqlDataProvider L1072) and whose zero
// means "no explicit length" - so a negative value has no meaning that any consumer could act on, and
// no legacy workflow could produce one deliberately. Record in the repository migration notes.
//
// MIGRATION: the visibility range is faithful rather than net-new. The legacy member is typed as the
// three-member UserVisibilityMode enumeration (Library/Components/Users/UserVisibilityMode.vb L23-L27:
// AllUsers = 0, MembersOnly = 1, AdminOnly = 2), so a fourth value was unrepresentable in the legacy
// type system and refusing it preserves that. The member travels as an int here only because the domain
// layer deliberately declares no counterpart enumeration, which is why the range has to be asserted
// explicitly instead of by IsInEnum. Note the trap recorded on the contract: this member has no column
// at all - it is a default hint whose persisted home is UserProfile.Visibility - and the hint's legacy
// default is 2 while that column's default is 0. The rule asserts the range and takes no position on
// either default.
//
// MIGRATION: neither identifier is bounded. PropertyDefinitionID is seeded IDENTITY(1,1)
// (03.02.03.SqlDataProvider L1064) but is route-authoritative on the update verb and meaningless on
// the create verb, and the portal is resolved from the route once per request - dbo.Portals.PortalID is
// itself seeded IDENTITY(-1,1), so no positive bound could be correct for it even if the member were
// authoritative. The module-definition reference is likewise unbounded: the column is nullable, a null
// meaning a portal-wide rather than module-scoped definition.
public class ProfilePropertyDefinitionDtoValidator : AbstractValidator<ProfilePropertyDefinitionDto>
{
    /// <summary>
    /// Reported when the data-type key is negative and so can name nothing. The legacy screen
    /// expressed its requirement through a generated list editor that could only offer real entries,
    /// so this wording is net-new; no <c>errormessage</c> attribute exists to reproduce.
    /// </summary>
    private const string DataTypeNegativeMessage = "Data Type must not be negative.";

    /// <summary>
    /// Reported when the mandatory property category is absent. Wording matches the sibling
    /// role contracts' presence message, which is the phrasing this application uses throughout.
    /// </summary>
    private const string PropertyCategoryRequiredMessage = "You Must Enter a Valid Category";

    /// <summary>
    /// Reported when the mandatory property name is absent.
    /// </summary>
    private const string PropertyNameRequiredMessage = "You Must Enter a Valid Name";

    /// <summary>
    /// Reported when the property name contains a character the legacy pattern forbids.
    /// </summary>
    private const string PropertyNameInvalidMessage =
        "Property Name may contain only letters, digits and the characters . _ % - + '";

    /// <summary>
    /// Reported when the submitted length is negative.
    /// </summary>
    private const string LengthNegativeMessage = "Length must be greater than or equal to zero.";

    /// <summary>
    /// Reported when the submitted view order is below the append instruction.
    /// </summary>
    private const string ViewOrderBelowAppendMessage =
        "View Order must be greater than or equal to zero, or -1 to append.";

    /// <summary>
    /// Reported when the submitted visibility is outside the three legacy modes.
    /// </summary>
    private const string VisibilityOutOfRangeMessage =
        "Visibility must be 0 for all users, 1 for members only or 2 for administrators only.";

    /// <summary>
    /// The pattern decorating <c>ProfilePropertyDefinition.PropertyName</c> at
    /// <c>ProfilePropertyDefinition.vb</c> L228, reproduced character for character.
    /// </summary>
    private const string PropertyNamePattern = @"^[a-zA-Z0-9._%\-+']+$";

    /// <summary>
    /// Width of <c>ProfilePropertyDefinition.PropertyCategory nvarchar(50) NOT NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L1070, unchanged by any later script).
    /// </summary>
    private const int PropertyCategoryMaximumLength = 50;

    /// <summary>
    /// Width of <c>ProfilePropertyDefinition.PropertyName nvarchar(50) NOT NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L1071, unchanged by any later script).
    /// </summary>
    private const int PropertyNameMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>ProfilePropertyDefinition.ValidationExpression</c>, widened from
    /// <c>nvarchar(100)</c> to <c>nvarchar(2000)</c> by <c>04.03.05.SqlDataProvider</c> L17.
    /// </summary>
    private const int ValidationExpressionMaximumLength = 2000;

    /// <summary>
    /// The view-order value that instructs the terminal insert procedure to append
    /// (<c>04.06.00.SqlDataProvider</c> L1122).
    /// </summary>
    private const int ViewOrderAppendSentinel = -1;

    /// <summary>
    /// Highest member of the legacy <c>UserVisibilityMode</c> enumeration, administrators only
    /// (<c>UserVisibilityMode.vb</c> L26).
    /// </summary>
    private const int HighestVisibilityMode = 2;

    /// <summary>
    /// Initialises a new instance of the <see cref="ProfilePropertyDefinitionDtoValidator"/> class and
    /// declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable
    /// message per field rather than a pattern complaint stacked on a presence complaint. Class-level
    /// cascade continues, so a submission wrong in several members reports all of them in a single
    /// response.
    /// </remarks>
    public ProfilePropertyDefinitionDtoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // Required(True) at ProfilePropertyDefinition.vb L90, expressed as a non-negative bound. Zero is
        // admitted deliberately and the bound is NOT tightened to strictly positive - see the annotation
        // above, which measures why. No declared foreign key enforces the reference, so this is the only
        // place a nonsensical key is refused at all.
        RuleFor(definition => definition.DataType)
            .GreaterThanOrEqualTo(0)
            .WithMessage(DataTypeNegativeMessage);

        // Required(True) at L193, plus the NOT NULL column's own width.
        RuleFor(definition => definition.PropertyCategory)
            .NotEmpty()
            .WithMessage(PropertyCategoryRequiredMessage)
            .MaximumLength(PropertyCategoryMaximumLength);

        // Required(True) and RegularExpressionValidator at L228, plus the NOT NULL column's width.
        // Rule-level cascade stops at the first of the three, so an empty name reports the presence
        // failure alone rather than a pattern failure alongside it.
        RuleFor(definition => definition.PropertyName)
            .NotEmpty()
            .WithMessage(PropertyNameRequiredMessage)
            .MaximumLength(PropertyNameMaximumLength)
            .Matches(PropertyNamePattern)
            .WithMessage(PropertyNameInvalidMessage);

        // Net-new lower bound, annotated above. Zero is the column's own default and means
        // "unconstrained", so it is admitted.
        RuleFor(definition => definition.Length)
            .GreaterThanOrEqualTo(0)
            .WithMessage(LengthNegativeMessage);

        // Required(True) at L300. The bound admits the append instruction as well as every real
        // order; see the annotation above, which explains why -1 must never be refused here.
        RuleFor(definition => definition.ViewOrder)
            .GreaterThanOrEqualTo(ViewOrderAppendSentinel)
            .WithMessage(ViewOrderBelowAppendMessage);

        // The three legacy visibility modes, asserted as a range because the domain layer declares no
        // enumeration for them.
        RuleFor(definition => definition.Visibility)
            .InclusiveBetween(0, HighestVisibilityMode)
            .WithMessage(VisibilityOutOfRangeMessage);

        // Width only: the expression is a rule to be applied to profile input later, not a rule about
        // this field, so its contents are not interpreted here. The rule is inert for an absent
        // value.
        RuleFor(definition => definition.ValidationExpression)
            .MaximumLength(ValidationExpressionMaximumLength);

        // No rule on DefaultValue: the terminal column is ntext, so it carries no width, and no
        // legacy validator constrained it.
        //
        // No rule on the Required or Visible flags: a boolean is its own constraint, and false is a
        // legitimate state for both.
    }
}
