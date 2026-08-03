using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="ProfilePropertyDefinitionDto"/>, the payload submitted to
/// <c>POST /api/v1/profile-definitions</c> and <c>PUT /api/v1/profile-definitions/{id}</c> - and to their
/// portal-nested equivalents - to declare or amend one profile property a portal collects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where these rules come from, and why not from markup.</b> The legacy editor
/// <c>Website/admin/Users/EditProfileDefinition.ascx</c> declares NO validator of its own. Its whole field
/// set is a single <c>&lt;dnn:propertyeditorcontrol id="Properties"&gt;</c> at L23, a reflective editor from
/// the excluded control library that RENDERS its validators from attributes on the object being edited. The
/// authoritative rules are therefore the attributes on
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>, and there are exactly three:
/// </para>
/// <para>
/// <c>&lt;Required(True), SortOrder(2)&gt;</c> on <c>PropertyCategory</c> (L193);
/// <c>&lt;Required(True), IsReadOnly(True), SortOrder(0),
/// RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")&gt;</c> on <c>PropertyName</c> (L228); and
/// <c>&lt;Required(True), SortOrder(8)&gt;</c> on <c>ViewOrder</c> (L300). Every other member of that class
/// - <c>DataType</c>, <c>DefaultValue</c>, <c>Length</c>, <c>Required</c>, <c>ValidationExpression</c>,
/// <c>Visible</c> and <c>Visibility</c> - carries a sort order and nothing else, so the legacy editor
/// rendered no rule for any of them. Nothing below was invented from the shape of the request.
/// </para>
/// <para>
/// <b>The widths come from the TERMINAL schema, never from the creating script.</b> Two of this table's
/// columns were widened after creation, and taking the creating width would refuse values an upgraded
/// database already holds - which is why the request contract itself records the terminal number against
/// each member and defers the rule to this layer.
/// </para>
/// <para>
/// <b>Parameterless by design, and public by requirement.</b> The composition root discovers validators by
/// scanning this assembly for public implementations, so an internal type would register as nothing at all
/// and the boundary would silently accept anything. Registration is by that scan; nothing needs listing.
/// </para>
/// <para>
/// <b>One validator serves both the create and the update path</b>, because one projection type serves as
/// both request and response for this resource. A caller that reads a definition, edits one field and sends
/// it back is submitting the same shape it received, and that round trip is what the rules below have to
/// keep possible - which is the reason several plausible-looking rules are deliberately absent.
/// </para>
/// </remarks>
// MIGRATION: provenance of every width, measured across all 88 upgrade scripts case-insensitively and
// across the bare, owner-qualified, bracketed and templated naming forms.
//   PropertyName         nvarchar(50)   NOT NULL   created 03.02.03:L1071, never altered - terminal
//   PropertyCategory     nvarchar(50)   NOT NULL   created 03.02.03:L1070, never altered - terminal
//   ValidationExpression nvarchar(100)  NULL       created 03.02.03:L1074,
//                        nvarchar(2000)            WIDENED by 04.03.05:L17 - 2000 is terminal
//   DefaultValue         nvarchar(50)   NULL       created 03.02.03:L1069,
//                        ntext                     WIDENED by 04.05.00:L1593 - effectively unbounded
// A limit of 100 on the expression, or of 50 on the default value, would refuse rows that any database
// upgraded past 4.3.5 or 4.5.0 respectively already stores. Both numbers are taken from the terminal
// state, and the default value consequently carries no length rule at all.
//
// MIGRATION: the regular expression is carried across CHARACTER FOR CHARACTER from the legacy attribute,
// including its anchors and its escaped hyphen. It admits letters, digits, dot, underscore, percent,
// hyphen, plus and apostrophe, and requires at least one character. It is NOT relaxed to allow a space:
// the legacy pattern rejected one, the column is indexed for uniqueness per portal and module, and the
// name is the key by which a profile value is addressed.
//
// MIGRATION: an ASP.NET RegularExpressionValidator SUCCEEDS against an empty control by design - it
// validates format, not presence, and defers presence to the RequiredFieldValidator beside it. The pattern
// rule is therefore reached only after the presence rule has passed, which rule-level cascade achieves
// exactly: an omitted name reports "required" alone rather than stacking a format complaint on top of it.
//
// MIGRATION: NO RULE ON ViewOrder, and its Required(True) attribute is not a gap. The member is a
// non-nullable int on the request contract, so presence is structural and unfalsifiable at this boundary.
// A lower bound would be actively WRONG here: -1 is an INSTRUCTION rather than an absence marker, because
// the terminal upsert procedure branches on "IF @vieworder = -1" and substitutes the current maximum order
// plus one. Refusing -1 would remove the only way a caller can say "append to the end".
//
// MIGRATION: NO RULE ON DataType. It is a foreign key into the shared Lists lookup table restricted to the
// entries named DataType, and that subsystem is excluded from this migration, so there is no set to check
// membership of. The legacy editor bound a drop-down to that list, which constrained the CHOICE without
// declaring a validator; reproducing the constraint would require the excluded lookup, and inventing a
// numeric range for it would refuse whichever identifiers a given installation's list happens to carry.
// Whether the key names a row is a question about stored state, and it belongs to the service.
//
// MIGRATION: NO RANGE RULE ON Visibility, and this is a deliberate decision rather than an omission. The
// legacy class typed the member as the three-member UserVisibilityMode enumeration, which looks like a
// closed set but is not one at run time: a VB - like a C# - enumeration is an integer, and the legacy
// collection loader ASSIGNED this member by converting the "User Accounts" module setting
// Profile_DefaultVisibility, so whatever integer that setting held travelled through unchecked. The member
// is additionally NOT PERSISTED on this table - there is no Visibility column on
// ProfilePropertyDefinition, verified across all 88 scripts; the stored per-user counterpart lives on
// UserProfile - so this contract carries a default hint rather than a stored value. A 0-to-2 rule would
// refuse a caller that read a definition carrying such a setting-derived value and sent it back unchanged,
// turning a round trip the legacy application permitted into a rejected request. The three meanings are
// documented on the request contract instead.
//
// MIGRATION: NO RULE ON Length, Required or Visible. The first is a plain int with a schema default of
// zero and no legacy validator - zero legitimately means "unbounded" for a text property - and the other
// two are booleans rendered as check boxes, which cannot be submitted malformed.
//
// MIGRATION: NO IDENTIFIER RULES, on this contract or any other in this solution. PortalID is nullable in
// the terminal schema - 04.03.03:L77 altered it to NULL and rewrote the -1 host rows to null - and the
// portal table is IDENTITY (-1, 1), so neither a negative nor a zero portal key means "unset".
// PropertyDefinitionID is assigned by the store on a create and taken from the route on an update, and
// ModuleDefID is nullable. There is nothing here a bound test could correctly refuse, and a future reader
// must not add one on the assumption that a non-positive key is absent.
//
// MIGRATION: NO UNIQUENESS RULE. The schema enforces one through
// IX_ProfilePropertyDefinition on (PortalID, ModuleDefID, PropertyName), and the legacy screen discovered a
// collision by attempting the write. Answering it here would require a persistence read this layer cannot
// reach, and would introduce a check-then-write race that the index settles authoritatively. The service
// reports it as a conflict.
//
// MIGRATION: the messages carry the legacy WORDING and drop the presentation. Legacy validator messages
// began with a literal <br> tag because they were written into page markup; an HTML tag is neither markup
// nor data in a machine-readable problem document, so it is stripped while the wording is preserved. The
// property editor composed its own presence and pattern text from the localised member label rather than
// from a fixed string - there is no resource entry to transcribe, because there was no declared validator
// to key one to - so the messages below name the field and the rule plainly and are recorded here as
// authored wording rather than as transcribed wording.
public sealed class ProfilePropertyDefinitionDtoValidator : AbstractValidator<ProfilePropertyDefinitionDto>
{
    /// <summary>Reported when the property name is missing.</summary>
    private const string PropertyNameRequiredMessage = "You Must Enter a Property Name";

    /// <summary>Reported when the property name carries a character the legacy pattern refused.</summary>
    private const string PropertyNameInvalidMessage =
        "Property Name may contain only letters, numbers and the characters . _ % - + '";

    /// <summary>Reported when the property name exceeds the width of its column.</summary>
    private const string PropertyNameTooLongMessage =
        "Property Name must be 50 characters or fewer";

    /// <summary>Reported when the property category is missing.</summary>
    private const string PropertyCategoryRequiredMessage = "You Must Enter a Property Category";

    /// <summary>Reported when the property category exceeds the width of its column.</summary>
    private const string PropertyCategoryTooLongMessage =
        "Property Category must be 50 characters or fewer";

    /// <summary>Reported when the validation expression exceeds the width of its column.</summary>
    private const string ValidationExpressionTooLongMessage =
        "Validation Expression must be 2000 characters or fewer";

    /// <summary>
    /// The legacy pattern, reproduced character for character from
    /// <c>ProfilePropertyDefinition.vb:L228</c>.
    /// </summary>
    private const string PropertyNamePattern = @"^[a-zA-Z0-9._%\-+']+$";

    /// <summary>Terminal width of <c>PropertyName nvarchar(50) NOT NULL</c>.</summary>
    private const int PropertyNameMaximumLength = 50;

    /// <summary>Terminal width of <c>PropertyCategory nvarchar(50) NOT NULL</c>.</summary>
    private const int PropertyCategoryMaximumLength = 50;

    /// <summary>Terminal width of <c>ValidationExpression nvarchar(2000) NULL</c>.</summary>
    private const int ValidationExpressionMaximumLength = 2000;

    /// <summary>
    /// Initialises a new instance of the <see cref="ProfilePropertyDefinitionDtoValidator"/> class and
    /// declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, which is what reproduces the legacy
    /// pairing of a presence check with a format check: an omitted name reports one actionable message
    /// rather than two. Class-level cascade continues, so one malformed submission names every bad field in
    /// a single response instead of forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public ProfilePropertyDefinitionDtoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // Required(True) and RegularExpressionValidator from ProfilePropertyDefinition.vb:L228, plus the
        // terminal column width. Presence first, then width, then pattern: the width complaint is the more
        // actionable of the two for an over-long value that also happens to contain a stray character.
        RuleFor(definition => definition.PropertyName)
            .NotEmpty().WithMessage(PropertyNameRequiredMessage)
            .MaximumLength(PropertyNameMaximumLength).WithMessage(PropertyNameTooLongMessage)
            .Matches(PropertyNamePattern).WithMessage(PropertyNameInvalidMessage);

        // Required(True) from ProfilePropertyDefinition.vb:L193, plus the terminal column width.
        RuleFor(definition => definition.PropertyCategory)
            .NotEmpty().WithMessage(PropertyCategoryRequiredMessage)
            .MaximumLength(PropertyCategoryMaximumLength).WithMessage(PropertyCategoryTooLongMessage);

        // No legacy validator, but a NULLABLE column with a terminal width: an over-long expression would
        // be truncated or refused by the store rather than reported to the caller. MaximumLength passes a
        // null unchanged, so an absent expression - the normal case for an unconstrained property - is not
        // affected by this rule and needs no presence guard beside it.
        RuleFor(definition => definition.ValidationExpression)
            .MaximumLength(ValidationExpressionMaximumLength)
            .WithMessage(ValidationExpressionTooLongMessage);
    }
}
