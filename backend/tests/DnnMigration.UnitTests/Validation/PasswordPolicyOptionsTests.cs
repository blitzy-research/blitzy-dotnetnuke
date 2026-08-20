using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

// WHY THIS FILE EXISTS. PasswordPolicyOptions.Validate is a START-UP guard, and a start-up guard that is
// never exercised is indistinguishable from one that does not work: nothing else in the solution reaches
// its failure branches, because every other suite hands the validator a policy that is already coherent.

/// <summary>
/// Covers <see cref="PasswordPolicyOptions.Validate"/>: every configuration it refuses, the boundary of
/// each refusal, the accumulation of several at once, and the deviations it deliberately permits.
/// </summary>
public class PasswordPolicyOptionsTests
{
    /// <summary>The configuration path each failure has to name, built from the production constants.</summary>
    /// <param name="propertyName">The offending property.</param>
    /// <returns>The path an operator would edit.</returns>
    private static string Path(string propertyName) =>
        PasswordPolicyOptions.SectionName + ":" + propertyName;

    /// <summary>The shipped policy is coherent, so a deployment that configures nothing starts.</summary>
    /// <remarks>
    /// The measured legacy policy must never be refused as a misconfiguration - a start-up guard that
    /// rejected the very configuration the migration is carrying forward would block every deployment on
    /// day one. This is the anchor case: every refusal below is a deviation from a policy that is proved
    /// acceptable here.
    /// </remarks>
    [Fact]
    public void TheShippedDefaults_AreAccepted()
    {
        new PasswordPolicyOptions().Validate().Should().BeEmpty();
    }

    /// <summary>A minimum length below one is refused, because it admits an empty password.</summary>
    /// <param name="minimumLength">The configured minimum.</param>
    /// <remarks>
    /// Zero and every negative value mean the same thing here - no length rule at all - and no legacy
    /// configuration could express either: the attribute was present and set to seven. A policy that admits
    /// an empty password is not a weak policy, it is the absence of one.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AMinimumLengthBelowOne_IsRefused(int minimumLength)
    {
        PasswordPolicyOptions policy = new() { MinRequiredPasswordLength = minimumLength };

        IReadOnlyList<string> failures = policy.Validate();

        failures.Should().ContainSingle(
            "a minimum below one is one fault, and the symbol rule must not pile a second onto it")
            .Which.Should().Contain(Path(nameof(PasswordPolicyOptions.MinRequiredPasswordLength)))
            .And.Contain("at least 1 is required");
    }

    /// <summary>
    /// A minimum length of exactly one is accepted by this contract, which is where its scope ends.
    /// </summary>
    [Fact]
    public void AMinimumLengthOfOne_IsAcceptedByTheContractItself()
    {
        PasswordPolicyOptions policy = new() { MinRequiredPasswordLength = 1 };

        policy.Validate().Should().BeEmpty();
        PasswordPolicyOptions.MinimumConfigurablePasswordLength.Should().Be(
            7,
            "the measured legacy minimum is published for the host's deployment rule to enforce");
    }

    /// <summary>A negative symbol minimum is refused, because a count cannot fall below zero.</summary>
    /// <param name="minimumSymbols">The configured minimum count of non-alphanumeric characters.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANegativeSymbolMinimum_IsRefused(int minimumSymbols)
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredNonAlphanumericCharacters = minimumSymbols,
        };

        IReadOnlyList<string> failures = policy.Validate();

        failures.Should().ContainSingle()
            .Which.Should()
            .Contain(Path(nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)))
            .And.Contain("negative");
    }

    /// <summary>
    /// A symbol minimum above the length minimum is refused at its exact boundary, because no password
    /// could satisfy the pair.
    /// </summary>
    /// <param name="minimumLength">The configured minimum length.</param>
    /// <param name="minimumSymbols">The configured minimum count of non-alphanumeric characters.</param>
    /// <param name="expectedToBeAccepted">Whether the pair is satisfiable.</param>
    /// <remarks>
    /// Each setting is individually legal here; only the combination is impossible, which is precisely the
    /// class of fault a per-property check cannot catch. Equality is the interesting case and is asserted
    /// on both sides: a password made entirely of symbols satisfies "as many symbols as characters", so
    /// eight-and-eight must be accepted while eight-and-nine must not.
    /// </remarks>
    [Theory]
    [InlineData(8, 7, true)]
    [InlineData(8, 8, true)]
    [InlineData(8, 9, false)]
    [InlineData(1, 2, false)]
    public void ASymbolMinimumAboveTheLengthMinimum_IsRefusedAtItsBoundary(
        int minimumLength,
        int minimumSymbols,
        bool expectedToBeAccepted)
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = minimumLength,
            MinRequiredNonAlphanumericCharacters = minimumSymbols,
        };

        IReadOnlyList<string> failures = policy.Validate();

        if (expectedToBeAccepted)
        {
            failures.Should().BeEmpty();
            return;
        }

        failures.Should().ContainSingle()
            .Which.Should()
            .Contain(Path(nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)))
            .And.Contain(Path(nameof(PasswordPolicyOptions.MinRequiredPasswordLength)))
            .And.Contain("no password could satisfy this policy");
    }

    /// <summary>
    /// The unsatisfiable-pair check is skipped when the length minimum is itself invalid, so one mistake
    /// produces one message.
    /// </summary>
    /// <remarks>
    /// A length minimum of zero is already refused above, and comparing a symbol count against it would add
    /// a second message describing a consequence of the first. An operator fixing one setting per restart
    /// is the outcome a noisy result produces, so the guard is deliberately conditional on the length being
    /// usable.
    /// </remarks>
    [Fact]
    public void TheUnsatisfiablePairCheck_IsSkippedWhenTheLengthMinimumIsItselfInvalid()
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = 0,
            MinRequiredNonAlphanumericCharacters = 5,
        };

        IReadOnlyList<string> failures = policy.Validate();

        failures.Should().ContainSingle()
            .Which.Should().Contain(Path(nameof(PasswordPolicyOptions.MinRequiredPasswordLength)));
    }

    /// <summary>A minimum length above the shared credential ceiling is refused at its exact boundary.</summary>
    /// <param name="offsetFromCeiling">How far the configured minimum sits from the ceiling.</param>
    /// <param name="expectedToBeAccepted">Whether the resulting policy is satisfiable.</param>
    /// <remarks>
    /// This is the second combination fault, and it is the subtler of the two because the two numbers are
    /// declared in different places.
    /// </remarks>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void AMinimumLengthAboveTheSharedCeiling_IsRefusedAtItsBoundary(
        int offsetFromCeiling,
        bool expectedToBeAccepted)
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = CredentialBounds.MaximumByteLength + offsetFromCeiling,
        };

        IReadOnlyList<string> failures = policy.Validate();

        if (expectedToBeAccepted)
        {
            failures.Should().BeEmpty();
            return;
        }

        failures.Should().ContainSingle()
            .Which.Should()
            .Contain(Path(nameof(PasswordPolicyOptions.MinRequiredPasswordLength)))
            .And.Contain("byte ceiling")
            .And.Contain("no password could satisfy this policy");
    }

    /// <summary>
    /// Switching the recovery question-and-answer requirement on is refused, because nothing in the target
    /// could ever satisfy it.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the recovery pair is not carried forward at all. No request in the target accepts a
    /// question or an answer, and the hashing contract holds no member that could check one, so switching
    /// this on cannot cause anything to be collected, stored or verified - it would merely make an operator
    /// believe a requirement was in force.
    /// </remarks>
    [Fact]
    public void RequiringTheRecoveryPair_IsRefusedBecauseNothingCanSatisfyIt()
    {
        PasswordPolicyOptions policy = new() { RequiresQuestionAndAnswer = true };

        IReadOnlyList<string> failures = policy.Validate();

        failures.Should().ContainSingle()
            .Which.Should()
            .Contain(Path(nameof(PasswordPolicyOptions.RequiresQuestionAndAnswer)))
            .And.Contain("not carried forward");

        new PasswordPolicyOptions().RequiresQuestionAndAnswer.Should().BeFalse(
            "the shipped value is the measured legacy one, so this guard refuses no real installation");
    }

    /// <summary>Demanding a unique address is NOT refused, which is the boundary of this method's scope.</summary>
    /// <remarks>
    /// The distinction against the recovery pair above is the one this method is scoped by: it refuses what
    /// is UNSATISFIABLE, and a unique-address requirement is merely UNIMPLEMENTED. The address it governs
    /// is on the wire, so a future uniqueness rule has something to check, and this contract is not
    /// entitled to forbid a configuration that will one day be legitimate.
    /// </remarks>
    [Fact]
    public void DemandingAUniqueAddress_IsNotRefusedByTheContractItself()
    {
        PasswordPolicyOptions policy = new() { RequiresUniqueEmail = true };

        policy.Validate().Should().BeEmpty(
            "an unimplemented rule is not an unsatisfiable one, and this method refuses only the second");
    }

    /// <summary>
    /// A strength pattern that cannot be parsed is refused at start-up rather than at the first sign-up.
    /// </summary>
    /// <param name="pattern">The malformed pattern.</param>
    /// <remarks>
    /// The pattern is compiled here purely to prove it is well formed and the compiled instance is
    /// discarded; caching a matcher for reuse belongs to the validator that applies the rule. Leaving the
    /// parse unproven would defer the defect to the first password-setting request, which is a run-time
    /// failure on an unauthenticated path rather than a start-up failure an operator sees immediately.
    /// </remarks>
    [Theory]
    [InlineData("([")]
    [InlineData("(?<")]
    [InlineData("a{3,2}")]
    [InlineData("[z-a]")]
    public void AnUnparseableStrengthPattern_IsRefused(string pattern)
    {
        PasswordPolicyOptions policy = new() { PasswordStrengthRegularExpression = pattern };

        IReadOnlyList<string> failures = policy.Validate();

        failures.Should().ContainSingle()
            .Which.Should()
            .Contain(Path(nameof(PasswordPolicyOptions.PasswordStrengthRegularExpression)))
            .And.Contain("not a valid");
    }

    /// <summary>A well-formed strength pattern is accepted, and an absent one is not compiled at all.</summary>
    /// <param name="pattern">The configured pattern, which may be absent.</param>
    /// <remarks>
    /// The empty case is the shipped configuration - the setting appears in neither legacy configuration
    /// file - and it must not be compiled, because an empty pattern matches every input and applying it
    /// would be a rule that can never fail dressed as a rule. Accepting a well-formed pattern alongside it
    /// is what proves the guard discriminates rather than refuses everything.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("^[A-Za-z0-9]+$")]
    [InlineData(@"^(?=.*\d)(?=.*[a-z])(?=.*[A-Z]).{8,}$")]
    public void AWellFormedOrAbsentStrengthPattern_IsAccepted(string pattern)
    {
        PasswordPolicyOptions policy = new() { PasswordStrengthRegularExpression = pattern };

        policy.Validate().Should().BeEmpty();
    }

    /// <summary>A hardened but coherent policy is accepted, because tightening is an operator's decision.</summary>
    /// <remarks>
    /// This method does not enforce the legacy figures as minimums, and that is deliberate.
    /// </remarks>
    [Fact]
    public void AHardenedButCoherentPolicy_IsAccepted()
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = 12,
            MinRequiredNonAlphanumericCharacters = 2,
            PasswordStrengthRegularExpression = "^(?=.*[^0-9A-Za-z]).{12,}$",
            PasswordResetEnabled = false,
        };

        policy.Validate().Should().BeEmpty();
    }

    /// <summary>Every fault in one configuration is reported together, rather than one per restart.</summary>
    [Fact]
    public void EveryFault_IsReportedTogether()
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = 0,
            MinRequiredNonAlphanumericCharacters = -3,
            RequiresQuestionAndAnswer = true,
            PasswordStrengthRegularExpression = "([",
        };

        IReadOnlyList<string> failures = policy.Validate();

        failures.Should().HaveCount(4);
        failures.Should().ContainSingle(failure =>
            failure.Contains(Path(nameof(PasswordPolicyOptions.MinRequiredPasswordLength)), StringComparison.Ordinal));
        failures.Should().ContainSingle(failure =>
            failure.Contains(
                Path(nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)),
                StringComparison.Ordinal));
        failures.Should().ContainSingle(failure =>
            failure.Contains(Path(nameof(PasswordPolicyOptions.RequiresQuestionAndAnswer)), StringComparison.Ordinal));
        failures.Should().ContainSingle(failure =>
            failure.Contains(
                Path(nameof(PasswordPolicyOptions.PasswordStrengthRegularExpression)),
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Validation reads the policy without changing it, so a refused configuration is reported exactly as
    /// the operator wrote it.
    /// </summary>
    /// <remarks>
    /// A guard that silently corrected what it found would be worse than one that refused, because the
    /// deployment would then run under a policy nobody chose. Calling it twice must also produce the same
    /// answer, which is what makes it safe for a host to validate once and log once.
    /// </remarks>
    [Fact]
    public void Validation_LeavesThePolicyUnchangedAndIsRepeatable()
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = 0,
            MinRequiredNonAlphanumericCharacters = -3,
        };

        IReadOnlyList<string> first = policy.Validate();
        IReadOnlyList<string> second = policy.Validate();

        policy.MinRequiredPasswordLength.Should().Be(0, "nothing was corrected on the way through");
        policy.MinRequiredNonAlphanumericCharacters.Should().Be(-3);
        second.Should().Equal(first);
    }

    /// <summary>The section name is the configuration key a deployment binds, and every message quotes it.</summary>
    /// <remarks>
    /// The name is the first half of every path an operator has to edit, so it is pinned rather than left
    /// implicit: renaming it would silently point every start-up failure at a section that does not exist.
    /// </remarks>
    [Fact]
    public void TheSectionName_IsTheKeyEveryMessageQuotes()
    {
        PasswordPolicyOptions.SectionName.Should().Be("PasswordPolicy");

        PasswordPolicyOptions policy = new() { MinRequiredPasswordLength = 0 };

        policy.Validate().Should().OnlyContain(
            failure => failure.StartsWith(PasswordPolicyOptions.SectionName, StringComparison.Ordinal),
            "a failure that does not begin with the section is a failure an operator cannot act on");
    }
}
