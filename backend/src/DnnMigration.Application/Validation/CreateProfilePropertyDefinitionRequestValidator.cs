using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="CreateProfilePropertyDefinitionRequest"/>, the payload submitted
/// to <c>POST /api/v1/profile-definitions</c> and to its portal-nested equivalent to declare one new profile
/// property a portal collects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where these rules come from.</b> Every rule below traces to an attribute on
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c> or to a terminal column width; the
/// provenance of each, and the reason the legacy markup declares no validator of its own, is recorded once
/// on <see cref="ProfileDefinitionTermsRules"/> rather than duplicated here and on the update validator.
/// Nothing below was invented from the shape of the request.
/// </para>
/// <para>
/// <b>Parameterless by design, and public by requirement.</b> The composition root discovers validators by
/// scanning this assembly for public implementations, so an internal type would register as nothing at all
/// and the boundary would silently accept anything. Registration is by that scan; nothing needs listing.
/// </para>
/// <para>
/// <b>Why a separate validator from the update path.</b> The two verbs no longer share a request type,
/// because the terminal procedures do not honour the same member set: <c>AddPropertyDefinition</c>
/// (<c>04.06.00:L1101</c>) takes a module-definition key that <c>UpdatePropertyDefinition</c>
/// (<c>04.05.00:L1685</c>) does not. The rules that apply to the members the two verbs DO share are
/// identical, and they stay identical by both validators reading the same constants - a rule that held on
/// one verb and not the other would be a rule a caller could bypass by choosing the other verb.
/// </para>
/// </remarks>
// MIGRATION: an ASP.NET RegularExpressionValidator SUCCEEDS against an empty control by design - it
// validates format, not presence, and defers presence to the RequiredFieldValidator beside it. The pattern
// rule is therefore reached only after the presence rule has passed, which rule-level cascade achieves
// exactly: an omitted name reports "required" alone rather than stacking a format complaint on top of it.
//
// MIGRATION: NO RULE ON ViewOrder, and its Required(True) attribute is not a gap. The member is a
// non-nullable int on the request contract, so presence is structural and unfalsifiable at this boundary.
// A lower bound would be actively WRONG here: -1 is an INSTRUCTION rather than an absence marker, because
// the terminal create procedure branches on "IF @vieworder = -1" (04.06.00:L1112) and substitutes the
// current maximum order plus one. Refusing -1 would remove the only way a caller can say "append to the
// end".
//
// MIGRATION: NO RULE ON DataType. It is a foreign key into the shared Lists lookup table restricted to the
// entries named DataType, and that subsystem is excluded from this migration, so there is no set to check
// membership of. The legacy editor bound a drop-down to that list, which constrained the CHOICE without
// declaring a validator; reproducing the constraint would require the excluded lookup, and inventing a
// numeric range for it would refuse whichever identifiers a given installation's list happens to carry.
// Whether the key names a row is a question about stored state, and it belongs to the service.
//
// MIGRATION: NO RULE ON Length, Required or Visible. The first is a plain int with a schema default of
// zero and no legacy validator - zero legitimately means "unbounded" for a text property - and the other
// two are booleans rendered as check boxes, which cannot be submitted malformed.
//
// MIGRATION: NO IDENTIFIER RULE ON ModuleDefId, on this contract or any other in this solution. The column
// is nullable in the terminal schema and the legacy class seeded the member with the -1 null-integer
// sentinel, so neither a negative nor a zero value means "unset" in a way this layer could correctly
// refuse. Whether the key names a row is a question about stored state. A future reader must not add a
// positivity rule here on the assumption that a non-positive key is absent.
//
// MIGRATION: NO UNIQUENESS RULE. The schema enforces one through IX_ProfilePropertyDefinition on
// (PortalID, ModuleDefID, PropertyName), and the legacy screen discovered a collision by attempting the
// write. Answering it here would require a persistence read this layer cannot reach, and would introduce a
// check-then-write race that the index settles authoritatively. The service reports it as a conflict.
public sealed class CreateProfilePropertyDefinitionRequestValidator
    : AbstractValidator<CreateProfilePropertyDefinitionRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CreateProfilePropertyDefinitionRequestValidator"/> class
    /// and declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, which is what reproduces the legacy
    /// pairing of a presence check with a format check: an omitted name reports one actionable message
    /// rather than two. Class-level cascade continues, so one malformed submission names every bad field in
    /// a single response instead of forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public CreateProfilePropertyDefinitionRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // Required(True) and RegularExpressionValidator from ProfilePropertyDefinition.vb:L228, plus the
        // terminal column width. Presence first, then width, then pattern: the width complaint is the more
        // actionable of the two for an over-long value that also happens to contain a stray character.
        RuleFor(request => request.PropertyName)
            .NotEmpty().WithMessage(ProfileDefinitionTermsRules.PropertyNameRequiredMessage)
            .MaximumLength(ProfileDefinitionTermsRules.PropertyNameMaximumLength)
                .WithMessage(ProfileDefinitionTermsRules.PropertyNameTooLongMessage)
            .Matches(ProfileDefinitionTermsRules.PropertyNamePattern)
                .WithMessage(ProfileDefinitionTermsRules.PropertyNameInvalidMessage);

        // Required(True) from ProfilePropertyDefinition.vb:L193, plus the terminal column width.
        RuleFor(request => request.PropertyCategory)
            .NotEmpty().WithMessage(ProfileDefinitionTermsRules.PropertyCategoryRequiredMessage)
            .MaximumLength(ProfileDefinitionTermsRules.PropertyCategoryMaximumLength)
                .WithMessage(ProfileDefinitionTermsRules.PropertyCategoryTooLongMessage);

        // No legacy validator, but a NULLABLE column with a terminal width: an over-long expression would
        // be truncated or refused by the store rather than reported to the caller. MaximumLength passes a
        // null unchanged, so an absent expression - the normal case for an unconstrained property - is not
        // affected by this rule and needs no presence guard beside it.
        RuleFor(request => request.ValidationExpression)
            .MaximumLength(ProfileDefinitionTermsRules.ValidationExpressionMaximumLength)
            .WithMessage(ProfileDefinitionTermsRules.ValidationExpressionTooLongMessage);
    }
}
