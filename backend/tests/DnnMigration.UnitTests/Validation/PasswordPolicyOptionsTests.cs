using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

// WHY THIS FILE EXISTS. PasswordPolicyOptions.Validate is a START-UP guard, and a start-up guard that is
// never exercised is indistinguishable from one that does not work: nothing else in the solution reaches
// its failure branches, because every other suite hands the validator a policy that is already coherent.
// Each branch below therefore refuses a configuration that no legacy installation could express and that
// no credential could ever satisfy, and the whole point of failing at start-up is that the alternative is
// failing on the first sign-up attempt - by which time the deployment is live and the operator is
// debugging a rejected registration rather than reading a message that names the setting.
//
// MIGRATION: the legacy application had no equivalent. Its membership provider read the same six
// attributes straight off the configuration element (Website/release.config:L236-L247) and validated none
// of them, so a nonsensical combination - a minimum length of zero, or more required symbols than
// characters - was accepted silently and then applied to every registration. This whole surface is
// therefore net-new behaviour rather than a translation, which is exactly why it needs its own coverage:
// there is no legacy behaviour to fall back on if it is wrong.
//
// MIGRATION: what Validate refuses is scoped deliberately, and the scope is the reason two settings
// appear here as ACCEPTED rather than refused. It rejects a policy that is UNSATISFIABLE - one no
// password could meet, or one that would admit an empty password. It does NOT reject a policy that is
// merely stricter than the measured legacy figures, because hardening is a decision an operator is
// entitled to take, and it does not reject a flag whose enforcement is merely unimplemented. The host
// applies its own DEPLOYMENT rules on top of these - refusing a minimum below the measured legacy seven,
// bounding the strength pattern's length, and refusing the unique-address flag while nothing enforces it -
// and those rules live with the host because they are properties of a deployment rather than of this
// contract. Tests for them belong with the host's own registration code, and the two accepted cases below
// say so in place rather than leaving a reader to conclude the settings are unchecked everywhere.

/// <summary>
/// Covers <see cref="PasswordPolicyOptions.Validate"/>: every configuration it refuses, the boundary of
/// each refusal, the accumulation of several at once, and the deviations it deliberately permits.
/// </summary>
/// <remarks>
/// <para>
/// Each test states exactly one deviation from the shipped defaults, so a failure names the setting under
/// test rather than an interaction between several. The one exception is the accumulation test, which
/// exists precisely to break that rule.
/// </para>
/// <para>
/// Messages are asserted by their configuration path - the section name and the property name, taken from
/// the production type rather than spelled as literals - because that path is the only part of a start-up
/// failure an operator can act on. Asserting the whole sentence would make every message a wording test;
/// asserting only the exception type would let a message name the wrong setting.
/// </para>
/// </remarks>
public class PasswordPolicyOptionsTests
{
    /// <summary>
    /// The configuration path each failure has to name, built from the production constants.
    /// </summary>
    /// <param name="propertyName">The offending property.</param>
    /// <returns>The path an operator would edit.</returns>
    private static string Path(string propertyName) =>
        PasswordPolicyOptions.SectionName + ":" + propertyName;

    /// <summary>
    /// The shipped policy is coherent, so a deployment that configures nothing starts.
    /// </summary>
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

    /// <summary>
    /// A minimum length below one is refused, because it admits an empty password.
    /// </summary>
    /// <param name="minimumLength">The configured minimum.</param>
    /// <remarks>
    /// Zero and every negative value mean the same thing here - no length rule at all - and no legacy
    /// configuration could express either: the attribute was present and set to seven. A policy that
    /// admits an empty password is not a weak policy, it is the absence of one.
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
    /// <remarks>
    /// One character is satisfiable, so this method has nothing to object to: it refuses the
    /// unsatisfiable, not the unwise. A deployment minimum below the measured legacy seven is a separate,
    /// stricter rule the host applies on top, and it is asserted with the host's registration code rather
    /// than here - which is why the production type publishes the legacy figure as a constant for the host
    /// to read.
    /// </remarks>
    [Fact]
    public void AMinimumLengthOfOne_IsAcceptedByTheContractItself()
    {
        PasswordPolicyOptions policy = new() { MinRequiredPasswordLength = 1 };

        policy.Validate().Should().BeEmpty();
        PasswordPolicyOptions.MinimumConfigurablePasswordLength.Should().Be(
            7,
            "the measured legacy minimum is published for the host's deployment rule to enforce");
    }

    /// <summary>
    /// A negative symbol minimum is refused, because a count cannot fall below zero.
    /// </summary>
    /// <param name="minimumSymbols">The configured minimum count of non-alphanumeric characters.</param>
    /// <remarks>
    /// Zero already expresses "no rule", which is the shipped value, so a negative value cannot mean
    /// anything a legal value does not already mean - it can only be a mistake.
    /// </remarks>
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
    /// A length minimum of zero is already refused above, and comparing a symbol count against it would
    /// add a second message describing a consequence of the first. An operator fixing one setting per
    /// restart is the outcome a noisy result produces, so the guard is deliberately conditional on the
    /// length being usable.
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

    /// <summary>
    /// A minimum length above the shared credential ceiling is refused at its exact boundary.
    /// </summary>
    /// <param name="offsetFromCeiling">How far the configured minimum sits from the ceiling.</param>
    /// <param name="expectedToBeAccepted">Whether the resulting policy is satisfiable.</param>
    /// <remarks>
    /// <para>
    /// This is the second combination fault, and it is the subtler of the two because the two numbers are
    /// declared in different places. Every credential entry point refuses a submission longer than
    /// <see cref="CredentialBounds.MaximumByteLength"/> UTF-8 bytes, so a configured minimum above that
    /// ceiling would reject every candidate a caller could possibly send - the policy and the bound would
    /// each be internally consistent while jointly admitting nothing at all.
    /// </para>
    /// <para>
    /// The comparison is sound across encodings rather than only for plain ASCII: UTF-8 spends at least one
    /// byte on every UTF-16 code unit, so a value whose length exceeds the ceiling in code units
    /// necessarily exceeds it in bytes too. The ceiling is read from the production constant here, so the
    /// two cannot drift apart.
    /// </para>
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
    /// this on cannot cause anything to be collected, stored or verified - it would merely make an
    /// operator believe a requirement was in force. Failing at start-up is the only way that belief can be
    /// corrected, and it is the same reasoning that removed the request members and the validator rules
    /// rather than leaving them inert. The measured legacy value is false
    /// (<c>Website/release.config:L241</c>), so no legacy installation is refused by this guard.
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

    /// <summary>
    /// Demanding a unique address is NOT refused, which is the boundary of this method's scope.
    /// </summary>
    /// <remarks>
    /// The distinction against the recovery pair above is the one this method is scoped by: it refuses
    /// what is UNSATISFIABLE, and a unique-address requirement is merely UNIMPLEMENTED. The address it
    /// governs is on the wire, so a future uniqueness rule has something to check, and this contract is
    /// not entitled to forbid a configuration that will one day be legitimate. The host additionally
    /// refuses a true value on the narrower ground that a flag reading as a requirement and enforced
    /// nowhere misleads whoever set it, and that refusal is asserted with the host's registration code.
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
    /// The four cases below are four different parse faults, so the guard is shown to catch the family
    /// rather than one spelling.
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

    /// <summary>
    /// A well-formed strength pattern is accepted, and an absent one is not compiled at all.
    /// </summary>
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

    /// <summary>
    /// A hardened but coherent policy is accepted, because tightening is an operator's decision.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this method does not enforce the legacy figures as minimums, and that is deliberate. The
    /// migration asks a deployment not to harden the policy while existing accounts are being carried
    /// across - raising the minimum would deny access to accounts the legacy installation accepted - but
    /// asking is different from making it impossible, and a policy this contract refuses is a policy no
    /// deployment can ever adopt. What is refused is a policy no password can satisfy, which is a
    /// different thing entirely.
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

    /// <summary>
    /// Every fault in one configuration is reported together, rather than one per restart.
    /// </summary>
    /// <remarks>
    /// An operator fixing one setting, restarting, and discovering the next is the outcome a
    /// single-failure result produces, so the accumulation is a genuine requirement rather than a
    /// convenience. Four independent faults are arranged here and all four must appear, each naming its
    /// own setting.
    /// </remarks>
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

    /// <summary>
    /// The section name is the configuration key a deployment binds, and every message quotes it.
    /// </summary>
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
