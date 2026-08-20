using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using BCrypt.Net;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

// MIGRATION: the credential store changes from a reversible cipher to a one-way salted hash, and that is a
// security fix rather than a like-for-like port.

// MIGRATION: the weak-default-credential advisory survives the change completely unchanged, which is worth
// recording precisely because it is a divergence that turns out not to be one.

/// <summary>
/// Covers what the registered one-way credential hasher DOES: the round trip, the non-determinism of the
/// stored form, the policy guards on minting, the shared byte ceiling on both paths, the refusal of a
/// stored value it cannot parse, the pre-hash pairing, the decoy, and the cost-replacement signal.
/// </summary>
/// <remarks>
/// <para>
/// The implementation is obtained two ways and no third way exists. <see cref="ShippedHasher"/> resolves
/// <see cref="IPasswordHasher"/> from the composed host, so the shipped-policy tests exercise the instance
/// a request would get. <see cref="HasherWith"/> activates the same registered type over a deviating
/// policy, because a policy is a constructor argument and the container binds only the configured one; it
/// discovers the type through the registration rather than naming it, so a rename of the implementation
/// cannot leave this file asserting against something else.
/// </para>
/// <para>
/// Scope is narrow on purpose.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PasswordHasherTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PasswordHasherTests"/> class.</summary>
    /// <param name="fixture">The shared composed host, used here purely as the composition root.</param>
    public PasswordHasherTests(ApiTestFixture fixture) => _fixture = fixture;

    // Every literal below is a fabricated fixture invented for this file.

    // A credential comfortably above every measured legacy minimum.
    private const string SampleCredential = "Migr8tion!Pass";

    // The same credential with its final character removed, so a near miss is still a miss.
    private const string NearMissCredential = "Migr8tion!Pas";

    private const string ShortCredential = "dnn4x";

    private const string LegacyStoredValue = "legacy-reversible-store-sample";

    private const int SupersededWorkFactor = 10;

    // The pre-hash the implementation states on both halves of its pair. Restated here rather than read
    // from the implementation, because a test that borrowed the value could not detect the two halves
    // drifting apart - which is the exact failure the implementation's own comment warns of.
    private const HashType PreHashAlgorithm = HashType.SHA384;

    /// <summary>The implementation refuses to be constructed without a bound policy.</summary>
    [Fact]
    public void Constructor_WithoutABoundPolicy_Throws()
    {
        Type implementation = RegisteredHasherType();

        // The argument array is stated explicitly rather than written as a bare null, which would pass a
        // null ARRAY - that is, no arguments at all - and select a constructor overload that does not exist
        // instead of handing the one that does a null policy.
        Action construct = () => _ = Activate(implementation, new object?[] { null });

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// The contract is registered, and it is registered as a singleton over one concrete implementation.
    /// </summary>
    /// <remarks>
    /// This is the assertion a direct construction cannot make, and it is why the rest of this file
    /// resolves rather than constructs.
    /// </remarks>
    [Fact]
    public void Contract_IsRegisteredAsASingletonOverOneInfrastructureImplementation()
    {
        IPasswordHasher first = ShippedHasher();
        IPasswordHasher second = ShippedHasher();

        second.Should().BeSameAs(
            first,
            "hashing holds no per-request state, and the decoy is computed once per instance, so the "
            + "registration has to be a singleton for that guarantee to reach a caller");

        Type implementation = first.GetType();

        implementation.Assembly.Should().BeSameAs(
            typeof(DependencyInjection).Assembly,
            "the implementation belongs to the infrastructure layer, which is the only layer permitted "
            + "to know how a credential is stored");
        implementation.IsSealed.Should().BeTrue(
            "a credential hasher that can be subclassed can have its comparison overridden");
        implementation.IsPublic.Should().BeFalse(
            "the implementation is internal so that no layer above it can name it, which is what makes "
            + "the contract the only way to reach it");
    }

    /// <summary>A credential turned into a stored form is accepted when it is presented again.</summary>
    [Fact]
    public void Hash_ThenVerify_AcceptsTheOriginalPassword()
    {
        IPasswordHasher hasher = ShippedHasher();

        string stored = hasher.Hash(SampleCredential);

        hasher.Verify(SampleCredential, stored).Should().BeTrue();
    }

    /// <summary>A different credential is refused against the same stored form, even a near miss.</summary>
    [Fact]
    public void Verify_RejectsADifferentPassword()
    {
        IPasswordHasher hasher = ShippedHasher();

        string stored = hasher.Hash(SampleCredential);

        hasher.Verify(NearMissCredential, stored).Should().BeFalse();
    }

    /// <summary>
    /// Hashing one credential twice yields two different stored forms, and both of them are accepted.
    /// </summary>
    /// <remarks>
    /// This is the assertion that separates the new store from the one it replaces, and it is the single
    /// most consequential test in this file. A reversible cipher over a fixed deployment key produces the
    /// same output for the same input every time, so equal stored values betrayed equal credentials across
    /// every account in every tenant and the store could be attacked wholesale.
    /// </remarks>
    [Fact]
    public void Hash_SameInputTwice_ProducesDifferentStoredValuesThatBothVerify()
    {
        IPasswordHasher hasher = ShippedHasher();

        string first = hasher.Hash(SampleCredential);
        string second = hasher.Hash(SampleCredential);

        second.Should().NotBe(
            first,
            "identical stored values would reveal that two accounts share a credential, which is the "
            + "weakness the reversible legacy store had and this one must not");
        hasher.Verify(SampleCredential, first).Should().BeTrue();
        hasher.Verify(SampleCredential, second).Should().BeTrue(
            "randomising the stored form is worthless if the second form stops being checkable");
    }

    /// <summary>A stored form neither equals the credential nor contains it.</summary>
    /// <remarks>
    /// The original store made this failure literal: the first schema declared the credential column as
    /// plain text at 01.00.00.SqlDataProvider:L106, capped at twenty characters.
    /// </remarks>
    [Fact]
    public void Hash_ProducesAStoredValueThatNeitherEqualsNorContainsThePlaintext()
    {
        string stored = ShippedHasher().Hash(SampleCredential);

        stored.Should().NotBe(SampleCredential);
        stored.Should().NotContain(
            SampleCredential,
            "a stored form that carries the credential inside it is a plaintext store wearing a "
            + "disguise");
    }

    /// <summary>
    /// Checking a credential does not apply the current policy, so a credential that predates a policy
    /// change stays usable.
    /// </summary>
    /// <remarks>
    /// This behaviour is load-bearing rather than incidental, and it is asserted across a genuine policy
    /// change rather than within one policy. A credential of five characters is minted under a deployment
    /// whose minimum is four, and is then presented to a hasher whose minimum is the shipped seven.
    /// </remarks>
    [Fact]
    public void Verify_DoesNotApplyTheCurrentPolicy()
    {
        IPasswordHasher lenientDeployment = HasherWith(policy => policy.MinRequiredPasswordLength = 4);
        IPasswordHasher stricterDeployment = ShippedHasher();

        string stored = lenientDeployment.Hash(ShortCredential);

        ShortCredential.Length.Should().BeLessThan(
            new PasswordPolicyOptions().MinRequiredPasswordLength,
            "the premise of this test is that the stored credential could not be minted under the "
            + "stricter policy");
        stricterDeployment.Verify(ShortCredential, stored).Should().BeTrue(
            "a stored credential shorter than the configured minimum must stay verifiable, or "
            + "tightening the policy locks out the existing membership");
    }

    /// <summary>Surrounding white space is part of the credential and is never trimmed away.</summary>
    [Fact]
    public void Hash_DoesNotTrimTheSubmittedValue()
    {
        IPasswordHasher hasher = ShippedHasher();
        string padded = $" {SampleCredential} ";

        string stored = hasher.Hash(padded);

        hasher.Verify(padded, stored).Should().BeTrue();
        hasher.Verify(SampleCredential, stored).Should().BeFalse(
            "trimming would silently accept a credential the account holder never chose, and would "
            + "quietly shrink the space an attacker has to search");
    }

    /// <summary>Case is part of the credential and is never folded.</summary>
    [Fact]
    public void Hash_DoesNotFoldCase()
    {
        IPasswordHasher hasher = ShippedHasher();

        string stored = hasher.Hash(SampleCredential);

        hasher.Verify(SampleCredential.ToLowerInvariant(), stored).Should().BeFalse();
        hasher.Verify(SampleCredential.ToUpperInvariant(), stored).Should().BeFalse(
            "case folding, like trimming or any other normalising of the bytes, throws away part of "
            + "the credential the account holder actually typed");
    }

    /// <summary>An absent, empty or white-space-only credential is refused rather than stored.</summary>
    /// <param name="password">The unusable credential offered for hashing.</param>
    /// <remarks>
    /// The contract states this on the parameter itself: an absent credential is not modelled as an empty
    /// value, so an empty value is an argument fault rather than a credential. White space <em>within</em>
    /// a credential remains significant - the test above proves padding is preserved - so this guard is
    /// about a value that is nothing but white space.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Hash_RejectsAnAbsentEmptyOrWhitespaceOnlyPassword(string? password)
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<string> rejected = () => hasher.Hash(password!);

        rejected.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The configured minimum length is enforced at its exact boundary when a stored form is minted.
    /// </summary>
    /// <param name="length">The length of the credential offered for hashing.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to be accepted.</param>
    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void Hash_AppliesTheConfiguredMinimumLengthAtItsBoundary(int length, bool expectedToBeAccepted)
    {
        IPasswordHasher hasher = ShippedHasher();
        string candidate = new('a', length);

        Func<string> mint = () => hasher.Hash(candidate);

        if (expectedToBeAccepted)
        {
            mint.Should().NotThrow();
            return;
        }

        mint.Should().Throw<ArgumentException>()
            .WithMessage("*at least 7 characters*", "the message quotes the policy, never the value");
    }

    /// <summary>
    /// The shipped policy demands no character outside the alphanumeric ranges, so a purely alphanumeric
    /// credential is minted without complaint.
    /// </summary>
    [Fact]
    public void Hash_UnderTheShippedPolicy_DemandsNoNonAlphanumericCharacter()
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<string> mint = () => hasher.Hash("abc1234");

        mint.Should().NotThrow();
    }

    /// <summary>A configured non-alphanumeric minimum is enforced at its exact boundary.</summary>
    /// <param name="candidate">The credential offered for hashing.</param>
    /// <param name="expectedToBeAccepted">Whether it carries enough such characters.</param>
    [Theory]
    [InlineData("abcdefgh", false)]
    [InlineData("abcdefg!", false)]
    [InlineData("abcdef!!", true)]
    [InlineData("abcdef!?", true)]
    [InlineData("abcde\u00e9!", true)]
    public void Hash_AppliesAConfiguredNonAlphanumericMinimumAtItsBoundary(
        string candidate,
        bool expectedToBeAccepted)
    {
        IPasswordHasher hasher = HasherWith(policy => policy.MinRequiredNonAlphanumericCharacters = 2);

        Func<string> mint = () => hasher.Hash(candidate);

        if (expectedToBeAccepted)
        {
            mint.Should().NotThrow();
            return;
        }

        mint.Should().Throw<ArgumentException>()
            .WithMessage("*at least 2 character(s) outside the ranges 0-9, A-Z and a-z*");
    }

    /// <summary>
    /// The shared credential ceiling is enforced when a stored form is minted, and it is measured in bytes.
    /// </summary>
    /// <remarks>
    /// The bound is the same constant every request validator applies, so a credential that reaches the
    /// hasher has already been measured once; the check here is defence in depth for a future caller that
    /// arrives without passing through a validator. It is asserted rather than assumed because the
    /// underlying package refuses nothing of its own accord - it will hash an input of any length.
    /// </remarks>
    [Fact]
    public void Hash_RefusesACredentialBeyondTheSharedByteCeiling()
    {
        IPasswordHasher hasher = ShippedHasher();
        string atTheCeiling = new('a', CredentialBounds.MaximumByteLength);
        string pastTheCeiling = new('a', CredentialBounds.MaximumByteLength + 1);

        Func<string> withinBound = () => hasher.Hash(atTheCeiling);
        Func<string> beyondBound = () => hasher.Hash(pastTheCeiling);

        withinBound.Should().NotThrow("256 bytes is the ceiling, not one past it");
        beyondBound.Should().Throw<ArgumentException>()
            .WithMessage($"*no longer than {CredentialBounds.MaximumByteLength} bytes*");
    }

    /// <summary>
    /// The ceiling counts UTF-8 bytes rather than characters, so a multibyte credential is bounded by its
    /// encoded size.
    /// </summary>
    /// <remarks>
    /// This is the case a character-counting bound gets wrong, and it gets it wrong in the permissive
    /// direction. Each of these characters occupies three bytes when encoded, so a credential of 86 of them
    /// is 258 bytes - comfortably within any character-based reading of a 256 limit and past the real one.
    /// </remarks>
    [Fact]
    public void Hash_MeasuresTheCeilingInBytesNotCharacters()
    {
        IPasswordHasher hasher = ShippedHasher();
        string within = new('\u4e2d', 85);
        string beyond = new('\u4e2d', 86);

        Encoding.UTF8.GetByteCount(within).Should().Be(255, "the premise of this test is the encoding");
        Encoding.UTF8.GetByteCount(beyond).Should().Be(258);
        beyond.Length.Should().BeLessThan(
            CredentialBounds.MaximumByteLength,
            "a character count would admit this value, which is precisely why bytes are counted");

        Func<string> withinBound = () => hasher.Hash(within);
        Func<string> beyondBound = () => hasher.Hash(beyond);

        withinBound.Should().NotThrow();
        beyondBound.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A candidate beyond the shared ceiling is reported as a non-match rather than thrown at the caller.
    /// </summary>
    /// <remarks>
    /// The asymmetry with the minting path is deliberate and is a security property. This member answers a
    /// sign-in attempt, so an over-long candidate is a failed attempt rather than a fault: throwing would
    /// turn a submission into a server error and would let a caller distinguish "too long" from "wrong",
    /// which is a difference an attacker has no business being able to observe.
    /// </remarks>
    [Fact]
    public void Verify_ReportsACandidateBeyondTheCeilingAsFalseInsteadOfThrowing()
    {
        IPasswordHasher hasher = ShippedHasher();
        string stored = hasher.Hash(SampleCredential);
        string pastTheCeiling = new('a', CredentialBounds.MaximumByteLength + 1);

        Func<bool> probe = () => hasher.Verify(pastTheCeiling, stored);

        probe.Should().NotThrow().Which.Should().BeFalse();
    }

    /// <summary>Both arguments to the checking path are required, and an absent one is a caller defect.</summary>
    /// <param name="password">The candidate credential.</param>
    /// <param name="passwordHash">The stored form to check against.</param>
    [Theory]
    [InlineData(null, "$2a$12$anything")]
    [InlineData("candidate", null)]
    [InlineData(null, null)]
    public void Verify_RefusesAnAbsentArgument(string? password, string? passwordHash)
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<bool> probe = () => hasher.Verify(password!, passwordHash!);

        probe.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// A stored form that cannot be parsed is reported as a refusal rather than thrown at the caller.
    /// </summary>
    /// <param name="storedValue">A damaged or foreign stored form.</param>
    /// <remarks>
    /// The contract is explicit that a mismatch and a malformed stored form are both simply false. That is
    /// a security property, not a convenience: an exception would let a caller distinguish a damaged stored
    /// value from a wrong credential, and a damaged value must never become a lever for admitting an
    /// arbitrary credential.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-stored-value")]
    [InlineData("$$$")]
    [InlineData("$9z$12$vI8aWBnW3fID.ZQ4/zo1G.q1lRps.9cGLcZEiGDMVr5yUP1KUOYTa")]
    [InlineData("$2a$zz$vI8aWBnW3fID.ZQ4/zo1G.q1lRps.9cGLcZEiGDMVr5yUP1KUOYTa")]
    [InlineData("$2a$12$tooshort")]
    [InlineData("$2a$12$")]
    public void Verify_ReportsAMalformedStoredValueAsFalseInsteadOfThrowing(string storedValue)
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<bool> probe = () => hasher.Verify(SampleCredential, storedValue);

        probe.Should().NotThrow().Which.Should().BeFalse(
            "a damaged stored form is a refusal, and refusing is the only safe answer");
    }

    /// <summary>A value inherited from the legacy reversible store cannot be checked at all.</summary>
    /// <remarks>
    /// This is the whole reason existing accounts need an administrative reset. A one-way hash has no way
    /// to confirm a value produced by a reversible cipher, so such an account cannot be admitted by
    /// presenting the right credential.
    /// </remarks>
    [Fact]
    public void Verify_CannotCheckAValueCarriedOverFromTheLegacyStore()
    {
        ShippedHasher().Verify(SampleCredential, LegacyStoredValue).Should().BeFalse(
            "the legacy value was never a BCrypt digest, so no credential can match it");
    }

    /// <summary>
    /// The hashing and checking halves are the pre-hashing pair, and a mismatched half fails in both
    /// directions.
    /// </summary>
    /// <remarks>
    /// The plain algorithm ignores every byte of its input past the first seventy-two, so two distinct
    /// passwords sharing a seventy-two-byte prefix would authenticate interchangeably. The pre-hashing pair
    /// digests the credential before hashing, so the whole of the input contributes and that equivalence
    /// disappears - which is why the implementation uses it.
    /// </remarks>
    [Fact]
    public void HashAndVerify_AreThePreHashingPairAndAMismatchedHalfFailsSilently()
    {
        IPasswordHasher hasher = ShippedHasher();

        string preHashed = hasher.Hash(SampleCredential);
        string plain = BCrypt.Net.BCrypt.HashPassword(SampleCredential, SupersededWorkFactor);

        BCrypt.Net.BCrypt.Verify(SampleCredential, preHashed).Should().BeFalse(
            "the plain checker cannot confirm a pre-hashed digest, and says so without complaint");
        hasher.Verify(SampleCredential, plain).Should().BeFalse(
            "and the implementation cannot confirm a plain digest either, which is why the two halves "
            + "must always be changed together");
        BCrypt.Net.BCrypt.EnhancedVerify(SampleCredential, preHashed, PreHashAlgorithm).Should().BeTrue(
            "the matching half does confirm it, which is what makes the two refusals above meaningful");
    }

    /// <summary>The whole of a long credential contributes to the stored form.</summary>
    /// <remarks>
    /// The direct consequence of the pre-hashing pair, and the reason it was chosen. Two credentials
    /// sharing a seventy-two-byte prefix and differing only after it must not authenticate interchangeably;
    /// under the plain algorithm they would.
    /// </remarks>
    [Fact]
    public void Hash_ConsidersEveryByteOfALongCredential()
    {
        IPasswordHasher hasher = ShippedHasher();
        string sharedPrefix = new('a', 72);
        string first = sharedPrefix + "-one";
        string second = sharedPrefix + "-two";

        string stored = hasher.Hash(first);

        hasher.Verify(first, stored).Should().BeTrue();
        hasher.Verify(second, stored).Should().BeFalse(
            "the plain algorithm stops reading at seventy-two bytes and would accept both, which is "
            + "exactly the equivalence the pre-hash removes");
    }

    /// <summary>
    /// The decoy is a well-formed stored form that the contract's own checking operation accepts as input
    /// and refuses for every credential.
    /// </summary>
    /// <remarks>
    /// Several unrelated candidates are tried, including the empty-ish and the credential this suite uses
    /// everywhere else, because a decoy that happened to match one specific input would be a credential
    /// rather than a decoy.
    /// </remarks>
    [Fact]
    public void UnmatchableHash_IsAWellFormedStoredFormThatNoCredentialMatches()
    {
        IPasswordHasher hasher = ShippedHasher();

        string decoy = hasher.UnmatchableHash;

        decoy.Should().NotBeNullOrWhiteSpace();
        hasher.NeedsRehash(decoy).Should().BeFalse(
            "the decoy is produced at the current generation, so a comparison against it costs what a "
            + "comparison against a current stored form costs");

        foreach (string candidate in new[] { SampleCredential, ShortCredential, "x", decoy })
        {
            hasher.Verify(candidate, decoy).Should().BeFalse(
                "no credential may match a representation of a value that was discarded");
        }
    }

    /// <summary>The decoy is computed once and the same value is served on every read.</summary>
    /// <remarks>
    /// This is a cost guarantee rather than an aesthetic one, and it is why the contract declares a
    /// property rather than a method.
    /// </remarks>
    [Fact]
    public void UnmatchableHash_IsComputedOnceAndServedRepeatedly()
    {
        IPasswordHasher hasher = ShippedHasher();

        hasher.UnmatchableHash.Should().Be(hasher.UnmatchableHash);
    }

    /// <summary>
    /// A value inherited from the legacy reversible store is NOT reported as needing regeneration, because
    /// there is no regeneration this scheme could perform on one.
    /// </summary>
    [Fact]
    public void NeedsRehash_IsFalseForAValueCarriedOverFromTheLegacyStore()
    {
        ShippedHasher().NeedsRehash(LegacyStoredValue).Should().BeFalse(
            "an upgrade that cannot be performed must not be promised");
    }

    /// <summary>A value this implementation has just produced does not need regenerating.</summary>
    [Fact]
    public void NeedsRehash_IsFalseForAValueTheImplementationJustProduced()
    {
        IPasswordHasher hasher = ShippedHasher();

        string stored = hasher.Hash(SampleCredential);

        hasher.NeedsRehash(stored).Should().BeFalse(
            "regenerating a current stored form on every sign-in would be pure waste");
    }

    /// <summary>A value produced at a lower cost still checks out, and is reported as needing regeneration.</summary>
    [Fact]
    public void NeedsRehash_IsTrueForALowerCostValueThatStillVerifies()
    {
        IPasswordHasher hasher = ShippedHasher();
        string supersededStored = BCrypt.Net.BCrypt.EnhancedHashPassword(
            SampleCredential,
            SupersededWorkFactor,
            PreHashAlgorithm);

        hasher.Verify(SampleCredential, supersededStored).Should().BeTrue(
            "an account must not be locked out merely because its stored form is out of date");
        hasher.NeedsRehash(supersededStored).Should().BeTrue(
            "and it must not be left out of date once the credential has been presented correctly");
    }

    /// <summary>
    /// The replacement the signal supports is a real one: re-hashing the presented credential produces a
    /// stored form that is current, checkable, and different from the one it replaces.
    /// </summary>
    [Fact]
    public void NeedsRehash_TheReplacementItAsksForProducesACurrentCheckableStoredForm()
    {
        IPasswordHasher hasher = ShippedHasher();
        string supersededStored = BCrypt.Net.BCrypt.EnhancedHashPassword(
            SampleCredential,
            SupersededWorkFactor,
            PreHashAlgorithm);

        string replacement = hasher.Hash(SampleCredential);

        replacement.Should().NotBe(supersededStored);
        hasher.Verify(SampleCredential, replacement).Should().BeTrue();
        hasher.NeedsRehash(replacement).Should().BeFalse(
            "the replacement must satisfy the very signal that asked for it, or the upgrade would "
            + "repeat on every sign-in for ever");
    }

    /// <summary>A stored value this implementation cannot parse answers "no".</summary>
    /// <param name="storedValue">A damaged, foreign or legacy stored form.</param>
    /// <remarks>
    /// The set enumerated here deliberately mirrors the set the checking member rejects, and the two
    /// members now <em>agree</em>: neither claims to understand a value the other does not. That agreement
    /// is asserted below rather than assumed, so the pair cannot drift apart.
    /// </remarks>
    [Theory]
    [InlineData(LegacyStoredValue)]
    [InlineData("")]
    [InlineData("not-a-stored-value")]
    [InlineData("$2a$zz$vI8aWBnW3fID.ZQ4/zo1G.q1lRps.9cGLcZEiGDMVr5yUP1KUOYTa")]
    [InlineData("$2a$12$tooshort")]
    public void NeedsRehash_AnswersNoForAValueItCannotParse(string storedValue)
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<bool> probe = () => hasher.NeedsRehash(storedValue);

        probe.Should().NotThrow().Which.Should().BeFalse(
            "an upgrade cannot be performed for a value whose plaintext can never arrive here");
        hasher.Verify(SampleCredential, storedValue).Should().BeFalse(
            "and the checking member agrees that the value is not understood, so the two members "
            + "cannot disagree about which stored forms this implementation handles");
    }

    /// <summary>The replacement signal requires a stored form, and an absent one is a caller defect.</summary>
    [Fact]
    public void NeedsRehash_RefusesAnAbsentStoredValue()
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<bool> probe = () => hasher.NeedsRehash(null!);

        probe.Should().Throw<ArgumentNullException>();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>The hasher the composed host registers, running under the shipped policy.</summary>
    /// <returns>The registered hasher, typed as the contract its callers depend on.</returns>
    /// <remarks>
    /// Resolved rather than constructed, so "the shipped hasher" means the instance a request actually
    /// gets. That covers a class of defect a direct construction cannot see at all: a component removed
    /// from <c>AddInfrastructure</c>, registered against a different contract, or bound to the wrong
    /// options section would still construct perfectly and still pass every behavioural assertion below.
    /// </remarks>
    private IPasswordHasher ShippedHasher() => _fixture.Services.GetRequiredService<IPasswordHasher>();

    /// <summary>
    /// Builds the registered hasher type over a policy that differs from the shipped one in a stated way.
    /// </summary>
    /// <param name="configure">Applies the deviation, and only the deviation.</param>
    /// <returns>The hasher, typed as the contract its callers depend on.</returns>
    /// <remarks>
    /// Deviations start from the shipped defaults so that each test states one difference rather than a
    /// whole policy, which keeps a failure attributable to the setting under test.
    /// </remarks>
    private IPasswordHasher HasherWith(Action<PasswordPolicyOptions> configure)
    {
        PasswordPolicyOptions policy = new();
        configure(policy);

        return Activate(RegisteredHasherType(), Options.Create(policy));
    }

    /// <summary>The concrete type the composed host registers for <see cref="IPasswordHasher"/>.</summary>
    /// <returns>The registered implementation type.</returns>
    /// <remarks>
    /// Read off the resolved instance rather than searched for by name or by scanning the assembly, so the
    /// type activated for a policy deviation is provably the same one the application uses. If the
    /// registration is ever repointed, every deviation test follows it automatically instead of silently
    /// continuing to assert against the abandoned implementation.
    /// </remarks>
    private Type RegisteredHasherType() => ShippedHasher().GetType();

    /// <summary>
    /// Activates an implementation type, surfacing a constructor failure as the constructor threw it.
    /// </summary>
    /// <param name="implementation">The type to construct.</param>
    /// <param name="arguments">The constructor arguments.</param>
    /// <returns>The constructed hasher.</returns>
    private static IPasswordHasher Activate(Type implementation, params object?[] arguments)
    {
        try
        {
            return (IPasswordHasher)Activator.CreateInstance(implementation, arguments)!;
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
            throw;
        }
    }
}
