using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>Declares request-shape and bounded-work rules for a complete profile replacement.</summary>
public sealed class UserProfileDtoValidator : AbstractValidator<UserProfileDto>
{
    private const int PropertyMaximum = 64;

    /// <summary>Initialises a new validator and declares the profile submission rules.</summary>
    public UserProfileDtoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(profile => profile.Properties)
            .NotNull()
            .WithMessage("The profile properties collection is required.")
            .Must(properties => properties.Count <= PropertyMaximum)
            .WithMessage($"A profile may contain no more than {PropertyMaximum} properties.")
            .Must(properties => properties.All(property => property is not null))
            .WithMessage("A profile property entry must not be null.")
            .Must(HaveUniqueDefinitionIdentifiers)
            .WithMessage("Each profile property definition may be submitted only once.");

        RuleForEach(profile => profile.Properties)
            .Where(property => property is not null)
            .ChildRules(property =>
            {
                property.RuleFor(value => value.PropertyValue)
                    .NotNull()
                    .WithMessage("A profile property value is required; use an empty string to clear it.");

                property.RuleFor(value => value.Visibility)
                    .InclusiveBetween(0, 2)
                    .WithMessage("Profile visibility must be between 0 and 2.");
            });
    }

    /// <summary>Reports whether every submitted property definition identifier appears once.</summary>
    /// <param name="properties">The non-null collection being validated.</param>
    /// <returns><see langword="true"/> when the identifiers are unique.</returns>
    private static bool HaveUniqueDefinitionIdentifiers(IReadOnlyList<UserProfileValueDto> properties)
    {
        var identifiers = new HashSet<int>();

        foreach (UserProfileValueDto? property in properties)
        {
            if (property is not null && !identifiers.Add(property.PropertyDefinitionId))
            {
                return false;
            }
        }

        return true;
    }
}
