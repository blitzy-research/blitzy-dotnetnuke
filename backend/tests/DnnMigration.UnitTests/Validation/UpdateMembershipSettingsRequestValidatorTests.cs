using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves that a membership-settings write admits only the closed legacy vocabularies and bounded values
/// its consumers can safely use.
/// </summary>
/// <remarks>
/// MIGRATION: THESE FACTS WERE WRITTEN AGAINST A VALIDATOR ON THE READ SHAPE AND ARE RE-POINTED AT THE
/// WRITE SHAPE. One revision declared these bounds on <c>MembershipSettingsDto</c>, which is the shape this
/// surface RETURNS; another had already split the write into <c>UpdateMembershipSettingsRequest</c>, which
/// is the shape a caller actually submits and the only one an endpoint binds.
/// </remarks>
public sealed class UpdateMembershipSettingsRequestValidatorTests
{
    private readonly UpdateMembershipSettingsRequestValidator _validator = new();

    /// <summary>The measured legacy defaults form a valid request.</summary>
    [Fact]
    public void Defaults_AreValid()
    {
        ValidationResult result = _validator.Validate(new UpdateMembershipSettingsRequest());

        result.IsValid.Should().BeTrue();
    }

    /// <summary>The users-grid display mode admits exactly the three legacy enum values.</summary>
    /// <param name="value">The discriminator to validate.</param>
    /// <param name="accepted">Whether the value belongs to the closed set.</param>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void DisplayMode_AdmitsOnlyTheLegacyVocabulary(int value, bool accepted)
        => ShouldAccept(
            settings => settings.DisplayMode = value,
            nameof(UpdateMembershipSettingsRequest.DisplayMode),
            accepted);

    /// <summary>The profile-visibility default admits exactly the three legacy enum values.</summary>
    /// <param name="value">The discriminator to validate.</param>
    /// <param name="accepted">Whether the value belongs to the closed set.</param>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void ProfileDefaultVisibility_AdmitsOnlyTheLegacyVocabulary(int value, bool accepted)
        => ShouldAccept(
            settings => settings.ProfileDefaultVisibility = value,
            nameof(UpdateMembershipSettingsRequest.ProfileDefaultVisibility),
            accepted);

    /// <summary>The role-management user picker admits exactly its two legacy control modes.</summary>
    /// <param name="value">The discriminator to validate.</param>
    /// <param name="accepted">Whether the value belongs to the closed set.</param>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void SecurityUsersControl_AdmitsOnlyTheLegacyVocabulary(int value, bool accepted)
        => ShouldAccept(
            settings => settings.SecurityUsersControl = value,
            nameof(UpdateMembershipSettingsRequest.SecurityUsersControl),
            accepted);

    /// <summary>The presentation page size is positive and shares the collection endpoint's ceiling.</summary>
    /// <param name="value">The page size to validate.</param>
    /// <param name="accepted">Whether the value is usable.</param>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(UpdateMembershipSettingsRequestValidator.MaximumRecordsPerPage, true)]
    [InlineData(UpdateMembershipSettingsRequestValidator.MaximumRecordsPerPage + 1, false)]
    public void RecordsPerPage_IsPositiveAndBounded(int value, bool accepted)
        => ShouldAccept(
            settings => settings.RecordsPerPage = value,
            nameof(UpdateMembershipSettingsRequest.RecordsPerPage),
            accepted);

    /// <summary>
    /// The display-name format permits the empty-string sentinel and the exact storage boundary, but not
    /// null or a value guaranteed to exceed the raw bound.
    /// </summary>
    [Fact]
    public void SecurityDisplayNameFormat_IsPresentAndBounded()
    {
        ShouldAccept(
            settings => settings.SecurityDisplayNameFormat = string.Empty,
            nameof(UpdateMembershipSettingsRequest.SecurityDisplayNameFormat),
            accepted: true);
        ShouldAccept(
            settings => settings.SecurityDisplayNameFormat =
                new string('x', UpdateMembershipSettingsRequestValidator.MaximumSettingValueLength),
            nameof(UpdateMembershipSettingsRequest.SecurityDisplayNameFormat),
            accepted: true);
        ShouldAccept(
            settings => settings.SecurityDisplayNameFormat =
                new string('x', UpdateMembershipSettingsRequestValidator.MaximumSettingValueLength + 1),
            nameof(UpdateMembershipSettingsRequest.SecurityDisplayNameFormat),
            accepted: false);
        ShouldAccept(
            settings => settings.SecurityDisplayNameFormat = null!,
            nameof(UpdateMembershipSettingsRequest.SecurityDisplayNameFormat),
            accepted: false);
    }

    /// <summary>
    /// The email expression permits an explicit empty value, compiles valid patterns under a timeout, and
    /// refuses malformed, absent, or oversized pattern text.
    /// </summary>
    [Fact]
    public void SecurityEmailValidation_IsPresentBoundedAndCompilable()
    {
        // RE-ORACLED. The revision these facts came from admitted an explicitly empty expression as "no
        // address rule"; the surviving validator refuses it, and the reasoning is recorded on the rule
        // itself: an empty pattern matches nothing, so a tenant that saved one would reject EVERY address
        // at registration - a lock-out configured by accident.
        ShouldAccept(
            settings => settings.SecurityEmailValidation = string.Empty,
            nameof(UpdateMembershipSettingsRequest.SecurityEmailValidation),
            accepted: false);
        ShouldAccept(
            settings => settings.SecurityEmailValidation =
                MembershipSettingsDto.DefaultEmailValidationExpression,
            nameof(UpdateMembershipSettingsRequest.SecurityEmailValidation),
            accepted: true);
        ShouldAccept(
            settings => settings.SecurityEmailValidation = "[",
            nameof(UpdateMembershipSettingsRequest.SecurityEmailValidation),
            accepted: false);
        ShouldAccept(
            settings => settings.SecurityEmailValidation =
                new string('x', UpdateMembershipSettingsRequestValidator.MaximumSettingValueLength + 1),
            nameof(UpdateMembershipSettingsRequest.SecurityEmailValidation),
            accepted: false);
        ShouldAccept(
            settings => settings.SecurityEmailValidation = null!,
            nameof(UpdateMembershipSettingsRequest.SecurityEmailValidation),
            accepted: false);
    }

    /// <summary>Validates one mutated default request and checks the named field.</summary>
    /// <param name="mutate">Applies the one value under test.</param>
    /// <param name="propertyName">The field the validator must name when it refuses.</param>
    /// <param name="accepted">Whether the request should be accepted.</param>
    private void ShouldAccept(
        Action<UpdateMembershipSettingsRequest> mutate,
        string propertyName,
        bool accepted)
    {
        var settings = new UpdateMembershipSettingsRequest();
        mutate(settings);

        ValidationResult result = _validator.Validate(settings);

        if (accepted)
        {
            result.Errors.Should().NotContain(error => error.PropertyName == propertyName);
        }
        else
        {
            result.Errors.Should().Contain(error => error.PropertyName == propertyName);
        }
    }
}
