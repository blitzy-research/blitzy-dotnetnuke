using System.Reflection;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the dedicated portal-settings request to the legacy Site Settings rule set and to the shared update
/// vocabulary used by the general portal write.
/// </summary>
public sealed class UpdatePortalSettingsRequestValidatorTests
{
    private readonly UpdatePortalSettingsRequestValidator _validator = new();

    /// <summary>
    /// The settings body carries exactly the editable values and no route-owned or immutable identifier.
    /// </summary>
    [Fact]
    public void SettingsRequest_CarriesExactlyTheSharedEditableMembers()
    {
        string[] settingsMembers = typeof(UpdatePortalSettingsRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] sharedMembers = typeof(IPortalSettingsUpdateRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] generalMembers = typeof(UpdatePortalRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != nameof(UpdatePortalRequest.PortalId))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        settingsMembers.Should().Equal(sharedMembers);
        settingsMembers.Should().Equal(generalMembers);

        // TWENTY-SEVEN: the twenty-six shared editable members, plus the optimistic-concurrency token.
        settingsMembers.Should().HaveCount(27);
        settingsMembers.Should().Contain(nameof(UpdatePortalSettingsRequest.ConcurrencyToken));
        settingsMembers.Should().NotContain(nameof(UpdatePortalRequest.PortalId));
        settingsMembers.Should().NotContain(nameof(PortalSettingsDto.Guid));
    }

    /// <summary>A blank portal title is ACCEPTED, because the legacy screen accepted one and stored it.</summary>
    /// <remarks>
    /// This case asserts the absence of a rule, which is why it exists: a NotEmpty() on this member was
    /// declared here and refused all three of these inputs, defended on the grounds that the legacy null
    /// contract rewrote the empty string to a database null and the NOT NULL column then rejected it.
    /// </remarks>
    /// <param name="portalName">The blank form under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPortalName_IsAcceptedBecauseTheLegacyScreenAcceptedOne(string? portalName)
    {
        UpdatePortalSettingsRequest request = Valid();
        request.PortalName = portalName;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// The title's only rule is the width the legacy control declared: accepted at the bound and refused
    /// one character later.
    /// </summary>
    [Fact]
    public void PortalName_UsesTheTerminalHundredAndTwentyEightCharacterWidth()
    {
        UpdatePortalSettingsRequest atLimit = Valid();
        atLimit.PortalName = new string('a', 128);
        _validator.Validate(atLimit).IsValid.Should().BeTrue();

        UpdatePortalSettingsRequest beyondLimit = Valid();
        beyondLimit.PortalName = new string('a', 129);
        ValidationResult tooLong = _validator.Validate(beyondLimit);

        tooLong.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdatePortalSettingsRequest.PortalName));
    }

    /// <summary>
    /// The terminal ten-character language width is accepted exactly and refused one character later.
    /// </summary>
    [Fact]
    public void DefaultLanguage_UsesTheTerminalTenCharacterWidth()
    {
        UpdatePortalSettingsRequest atLimit = Valid();
        atLimit.DefaultLanguage = new string('a', 10);
        _validator.Validate(atLimit).IsValid.Should().BeTrue();

        UpdatePortalSettingsRequest beyondLimit = Valid();
        beyondLimit.DefaultLanguage = new string('a', 11);
        ValidationResult tooLong = _validator.Validate(beyondLimit);

        tooLong.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdatePortalSettingsRequest.DefaultLanguage));
    }

    /// <summary>A date accepted by the CLR but not by SQL Server's legacy datetime column is a field error.</summary>
    [Fact]
    public void ExpiryDate_MustBeRepresentableByTheStoredColumn()
    {
        UpdatePortalSettingsRequest request = Valid();
        request.ExpiryDate = new DateTime(1600, 1, 1);

        ValidationResult result = _validator.Validate(request);

        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdatePortalSettingsRequest.ExpiryDate));
    }

    /// <summary>The validator does not invent a non-negative rule the legacy screen never declared.</summary>
    [Fact]
    public void NegativeHostTerms_AreNotRejectedByShapeValidation()
    {
        UpdatePortalSettingsRequest request = Valid();
        request.HostFee = -1m;
        request.HostSpace = -2;
        request.PageQuota = -3;
        request.UserQuota = -4;
        request.SiteLogHistory = -5;

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// Both ends of the legacy zone range are accepted, and one minute past either end is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bounds are measured, not chosen.</b> <c>Website/App_GlobalResources/TimeZones.xml</c> is the
    /// file the legacy <c>cboTimeZone</c> selector was filled from, and its entries run from
    /// <c>key="-720"</c> to <c>key="780"</c>.
    /// </para>
    /// <para>
    /// <b>Why this rule had to exist at all.</b> The legacy control was a CLOSED selector, so legality lived
    /// in the list and no validator was needed. Replacing it with a free-text box moved legality to the
    /// validator, and none was written - so <c>99999</c> was accepted and stored, and a portal's whole
    /// notion of local time is derived from this column.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(-720, true)]
    [InlineData(780, true)]
    [InlineData(-480, true)]
    [InlineData(0, true)]
    [InlineData(-721, false)]
    [InlineData(781, false)]
    [InlineData(99999, false)]
    [InlineData(-99999, false)]
    public void TimeZoneOffset_HoldsTheRangeTheLegacyZoneListOffered(int minutes, bool accepted)
    {
        UpdatePortalSettingsRequest request = Valid();
        request.TimeZoneOffset = minutes;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().Be(accepted);

        if (!accepted)
        {
            result.Errors.Should().ContainSingle()
                .Which.PropertyName.Should().Be(nameof(UpdatePortalSettingsRequest.TimeZoneOffset));

            // BOTH bounds in one sentence, so satisfying the one that was crossed cannot reveal the other
            // for the first time afterwards.
            result.Errors[0].ErrorMessage.Should().Contain("-720");
            result.Errors[0].ErrorMessage.Should().Contain("780");
        }
    }

    /// <summary>
    /// An omitted offset is not refused for a range it never stated. The member is nullable and a portal is
    /// permitted to keep no explicit offset.
    /// </summary>
    [Fact]
    public void TimeZoneOffset_WhenOmitted_IsNotRefused()
    {
        UpdatePortalSettingsRequest request = Valid();
        request.TimeZoneOffset = null;

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    private static UpdatePortalSettingsRequest Valid() => new()
    {
        PortalName = "Portal",
    };
}
