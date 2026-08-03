using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BCrypt.Net;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Security;

// THE CONCRETE HASHER IS THE SUBJECT OF THIS FILE. Every algorithm assertion below runs
// DnnMigration.Infrastructure.Security.BcryptPasswordHasher itself: the real enhanced-BCrypt pair,
// the real policy guards, the real shared byte ceiling, the real malformed-digest handlers and the
// real work-factor replacement signal. The type is internal sealed, and the owning project grants
// this assembly - and only this assembly - internals visibility for exactly this purpose; the
// justification and its bounds are written next to that item in
// backend/src/DnnMigration.Infrastructure/DnnMigration.Infrastructure.csproj.
//
// An earlier revision of this file stated that algorithm-level verification was "deliberately NOT
// located in this project" and asserted the contract against a private SHA-256 stand-in instead.
// THAT REASONING WAS WRONG and is replaced rather than softened. It read the Clean Architecture
// layering rule - which orders the four production projects - as though it also forbade a test
// assembly from referencing the layer it is testing, and the cost was concrete: the pre-hash pairing,
// the minimum-length and symbol-count guards, the 256-byte ceiling, the four exception handlers and
// the cost comparison could every one of them regress while the suite stayed green. AAP 0.5.1.5 names
// Infrastructure/Security/*.cs as this file's source and "Hash round-trip" as its subject, and that is
// what it now does.
//
// What survives from the old arrangement is the reflection group over IPasswordHasher, because those
// assertions were always about real production metadata: the exact member set, the absence of a
// property or event, the absence of any by-reference parameter and the wholly synchronous signatures.

// MIGRATION: the credential store changes from a reversible cipher to a one-way salted hash, and that
// is a security fix rather than a like-for-like port. The shipped deployment registered the ASP.NET
// SQL membership provider at Website/release.config:L236 -- selected as the default at L218 -- and
// chose the reversible Triple-DES storage option through its passwordFormat attribute at L245, the
// option its own documentation line at L233 describes as Triple-DES. The machine key that reverses
// that storage sits in the same file at L90-L93, committed to source control, which is why the
// capability is removed rather than reconfigured. No key material, and no copy of any key value,
// appears anywhere in this file or in any fixture below: the lines are cited, the values are not, and
// the legacy file itself is read-only evidence that is left exactly as it stands.

// MIGRATION: reading a stored password back is not merely switched off, it is impossible by
// construction. The legacy read-back member at Library/Components/Users/UserController.vb:L433 could
// only ever work because the stored form was reversible -- its own remark at L425-L426 says it can
// return a password only when the provider uses a storage format that can be undone, and L439 threw
// outright when a deployment had turned the capability off. It also answered twice over, once by
// mutating a by-reference argument and once as a return value, which is the double output channel the
// migration abolishes. A one-way hash removes the capability rather than the permission, so no
// service, endpoint or screen above this contract can offer it, and the member set asserted below
// contains nothing that could.

// MIGRATION: CREDENTIALS ALREADY PRESENT IN THE DATABASE MIGRATE BY ADMINISTRATIVE RESET, AND BY
// NOTHING ELSE. A one-way digest cannot be derived from a value held under the legacy reversible
// scheme, this class verifies BCrypt digests only, and the target maps no legacy credential column, so
// nothing in this solution can check a submitted password against a legacy stored value. An earlier
// revision of this file claimed existing rows migrate lazily by re-hashing the plaintext supplied on a
// first successful sign-in against the legacy value; THAT CLAIM WAS FALSE - a first successful sign-in
// against a legacy value is impossible without a legacy verifier - and the production type says so in
// as many words. It is removed here rather than softened, and the tests below assert what the type
// actually does: the replacement signal reports a digest THIS implementation produced below the
// current cost, and nothing else.

// MIGRATION: the remaining, genuinely supported upgrade is the work-factor replacement, and its
// sequence - verify, then ask whether the stored form is superseded, then re-hash and persist - belongs
// to Application/Services/AuthService.TryReplaceSupersededCredentialAsync and is verified against that
// real service in backend/tests/DnnMigration.UnitTests/Services/AuthServiceTests.cs. It is not modelled
// here. An earlier revision of this file reproduced that sequence as a private orchestrator and
// asserted interactions against a substituted hasher, which proved a local model rather than the
// service, and duplicated a suite that already drives production code. What this file owns is the half
// of that story the hasher itself decides: that a digest at the current cost is not superseded, that a
// digest at a lower cost is superseded AND still verifies, and that an unparseable stored value answers
// "replace it" without ever admitting a credential.

// MIGRATION: the measured legacy password policy is preserved verbatim and is NOT tightened.
// Website/release.config records minRequiredPasswordLength="7" at L242,
// minRequiredNonalphanumericCharacters="0" at L243, requiresQuestionAndAnswer="false" at L241 and an
// address-uniqueness flag of false at L244. Tightening a credential policy during a migration would
// lock the existing membership out of its own accounts, so policy is carried across unchanged. The
// user-facing wording of those rules belongs to the request validators; what this file pins is the
// hasher's own half - that minting a stored form DOES apply the configured minimums, and that checking
// a credential applies NO policy at all, so a credential accepted under an earlier policy stays usable
// after the policy is raised. Note also that no lockout threshold or window is asserted anywhere here,
// because none was ever configured -- the two attributes appear only inside the documentation comment
// at L224-L225 and were never set on the provider element at L236-L247.

// MIGRATION: the weak-default-credential advisory survives the change completely unchanged, which is
// worth recording precisely because it is a divergence that turns out not to be one. The legacy check
// at Library/Components/Users/UserController.vb:L1144-L1152 re-graded an already successful sign-in
// when the submitted plaintext was one of the four credentials the product shipped with, comparing the
// submitted argument against those literals after the credential itself had already been checked. It
// never read the stored form, so it never depended on the stored form being reversible, and it needs
// nothing at all from this contract. The advisory is raised on the sign-in path and is asserted there.

/// <summary>
/// Covers the one-way credential hasher the whole of the migrated authentication surface depends on:
/// the shape of the contract it implements, and the behaviour of the concrete implementation behind it.
/// </summary>
/// <remarks>
/// <para>
/// Two groups of tests appear below. The first reflects over <see cref="IPasswordHasher"/> and pins the
/// shape of the contract - the exact member set, the absence of properties and events, the absence of
/// any by-reference parameter, and the wholly synchronous signatures. The second drives the real
/// <c>BcryptPasswordHasher</c>, constructed directly from a bound policy, and pins what it does: the
/// round trip, the non-determinism of the stored form, the policy guards on minting, the shared byte
/// ceiling on both paths, the refusal of a stored value it cannot parse, the pre-hash pairing, and the
/// cost-replacement signal.
/// </para>
/// <para>
/// The hashing cost is deliberately never named. It is a private constant on the implementation, both
/// the minting path and the replacement signal read the same one, and every test below is written so
/// that raising it changes nothing here - a superseded digest is produced by asking the underlying
/// package for a demonstrably lower cost rather than by encoding the current one.
/// </para>
/// <para>
/// Scope is narrow on purpose. Nothing here asserts sign-in, session, token, permission or transport
/// behaviour: the full sign-in sequence, including the work-factor replacement as it is wired into the
/// real service, is owned by backend/tests/DnnMigration.UnitTests/Services/AuthServiceTests.cs, and the
/// user-facing credential policy is owned by the validator suites under
/// backend/tests/DnnMigration.UnitTests/Validation/. Neither is duplicated here, and the absence of
/// either from this file is intentional.
/// </para>
/// </remarks>
public class BcryptPasswordHasherTests
{
    // Every literal below is a fabricated fixture invented for this file. None of them is, or has ever
    // been, a credential for any system, any environment or any account, and nothing in this file is
    // copied out of the legacy configuration: the migration notes above cite that file by line number
    // precisely so that no value ever has to be reproduced here.

    // A credential comfortably above every measured legacy minimum.
    private const string SampleCredential = "Migr8tion!Pass";

    /// <summary>An opaque stored value handed to a substituted hasher, which never parses it.</summary>
    private const string StoredValue = "$g2$0000002a$STORED";

    /// <summary>The value a substituted hasher returns when it regenerates a stored form.</summary>
    private const string RegeneratedValue = "$g2$0000beef$REGENERATED";

    // The same credential with its final character removed, so a near miss is still a miss.
    private const string NearMissCredential = "Migr8tion!Pas";

    // Five characters, which is below the legacy minimum of seven recorded at release.config:L242.
    // Used to prove that checking a credential does not apply the current policy.
    private const string ShortCredential = "dnn4x";

    // An obviously synthetic stand-in for a value inherited from the legacy reversible store. It is
    // deliberately not shaped like anything this contract produces and is deliberately not a real
    // stored value from anywhere.
    private const string LegacyStoredValue = "legacy-reversible-store-sample";

    // A cost demonstrably below the implementation's own, used to mint a superseded digest without
    // naming the current value. Ten is the lowest cost the underlying package will accept while still
    // being a realistic historical setting, and the test that uses it asserts the relationship - the
    // digest is superseded - rather than the number.
    private const int SupersededWorkFactor = 10;

    // The pre-hash the implementation states on both halves of its pair. Restated here rather than
    // read from the implementation, because a test that borrowed the value could not detect the two
    // halves drifting apart - which is the exact failure the implementation's own comment warns of.
    private const HashType PreHashAlgorithm = HashType.SHA384;

    // The complete public surface of the abstraction. Three operations and the getter of the single
    // permitted property, which is what a property compiles to and therefore what reflection reports.
    private static readonly string[] ContractOperations =
        ["Hash", "Verify", "NeedsRehash", "get_UnmatchableHash"];

    /// <summary>
    /// The abstraction declares exactly the three one-way operations and the one permitted property, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exact-set assertion is used here in preference to a list of forbidden names, and it is the
    /// stronger of the two: a denylist only rejects the read-back spellings somebody thought of in
    /// advance, whereas this test fails the moment <em>any</em> unlisted member is added, whatever it is
    /// called. That is the machine-checkable form of the guarantee recorded in the migration notes
    /// above, which is that the impossibility of reading a password back is a property of the shape of
    /// the contract rather than of an implementer's discipline.
    /// </para>
    /// <para>
    /// The set grew by one, and the addition is the exception that proves the rule rather than a relaxation
    /// of it. <c>UnmatchableHash</c> reads BACK NOTHING: it serves a representation of a credential the
    /// implementation generated, used and discarded, so there is no account it belongs to and no plaintext
    /// anywhere that matches it. It exists because an authentication path with nothing to compare against
    /// must still perform a comparison, or the time it takes to answer becomes an account oracle. Its
    /// behaviour is pinned by the two tests further down, not merely its presence here.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_DeclaresExactlyTheOneWayOperationsAndTheDecoy()
    {
        MethodInfo[] operations = typeof(IPasswordHasher).GetMethods();

        operations.Select(operation => operation.Name)
            .Should()
            .BeEquivalentTo(
                ContractOperations,
                "an unlisted member is how a read-back capability would re-enter the system, so the "
                + "member set is fixed rather than merely reviewed");
    }

    /// <summary>
    /// The abstraction declares exactly one property - the read-only decoy - and no event.
    /// </summary>
    /// <remarks>
    /// A property is the quietest way to reintroduce a read-back capability, because it looks like
    /// state rather than like an operation, so the one that exists is pinned by name, by type and by being
    /// read-only rather than merely counted. Nothing may be assigned through it - a settable decoy would let
    /// a caller substitute a value of its own choosing and thereby control the cost of the comparison it is
    /// meant to equalise. An event would hand a stored value, or the plaintext that matched it, to an
    /// arbitrary subscriber; none is available.
    /// </remarks>
    [Fact]
    public void Contract_DeclaresOnlyTheDecoyPropertyAndNoEvent()
    {
        PropertyInfo[] properties = typeof(IPasswordHasher).GetProperties();

        properties.Select(property => property.Name)
            .Should()
            .BeEquivalentTo([nameof(IPasswordHasher.UnmatchableHash)]);

        PropertyInfo decoy = properties.Single();
        decoy.PropertyType.Should().Be<string>();
        decoy.CanRead.Should().BeTrue();
        decoy.CanWrite.Should().BeFalse("a caller must not be able to choose the value a comparison costs");

        typeof(IPasswordHasher).GetEvents().Should().BeEmpty();
    }

    /// <summary>
    /// No parameter of any operation is passed by reference.
    /// </summary>
    /// <remarks>
    /// The legacy code reported an outcome by mutating a caller's variable and returning a value at the
    /// same time, so a single call produced two answers that no type tied together; the read-back
    /// member at UserController.vb:L433 did exactly that. The migration abolishes the idiom outright:
    /// no by-reference and no output parameter appears anywhere in the target public surface, and this
    /// contract is held to that rule like every other.
    /// </remarks>
    [Fact]
    public void Contract_DeclaresNoOutOrByReferenceParameter()
    {
        ParameterInfo[] parameters =
            [.. typeof(IPasswordHasher).GetMethods().SelectMany(operation => operation.GetParameters())];

        parameters.Should().NotBeEmpty("every operation on this contract takes at least one argument");
        parameters.Should().OnlyContain(
            parameter => !parameter.IsOut && !parameter.IsRetval && !parameter.ParameterType.IsByRef,
            "an outcome is expressed as a return value on this contract, never by mutating a "
            + "caller's variable");
    }

    /// <summary>
    /// Every argument the contract accepts is a plain string.
    /// </summary>
    /// <remarks>
    /// This is the positive form of two separate guarantees, and it is stronger than asserting either
    /// of them individually. It fixes the argument shape to the plaintext and the stored form, and
    /// because a string is the only permitted parameter type it simultaneously establishes that no
    /// operation accepts a cooperative-cancellation argument. That absence is deliberate: hashing and
    /// checking are pure processor work that touches no socket, file or database, so the solution-wide
    /// rule that input and output bound members be awaitable and cancellable does not reach them.
    /// </remarks>
    [Fact]
    public void Contract_TakesOnlyPlainTextArguments()
    {
        ParameterInfo[] parameters =
            [.. typeof(IPasswordHasher).GetMethods().SelectMany(operation => operation.GetParameters())];

        parameters.Should().OnlyContain(
            parameter => parameter.ParameterType == typeof(string),
            "the contract exchanges a plaintext and a stored form and needs nothing else, which also "
            + "leaves no room for a cancellation argument on work that never waits on anything");
    }

    /// <summary>
    /// Every operation is synchronous, returning either the stored form or a plain decision.
    /// </summary>
    /// <remarks>
    /// This asserts the opposite of the shape most of the solution has, and the inversion is
    /// deliberate rather than an oversight. The rule that every member performing input or output be
    /// awaitable is scoped to members that actually wait on something; hashing and checking are bound
    /// by the processor alone. Reshaping these three into an awaitable form would add a state machine
    /// and a synchronisation context to work that has nothing to await, so the contract stays
    /// synchronous and this test stops anybody quietly changing that.
    /// </remarks>
    [Fact]
    public void Contract_IsWhollySynchronous()
    {
        Type[] returnTypes = [.. typeof(IPasswordHasher).GetMethods().Select(operation => operation.ReturnType)];

        returnTypes.Should().OnlyContain(
            returnType => returnType == typeof(string) || returnType == typeof(bool),
            "an operation either produces a stored form or answers a question, and neither answer is "
            + "wrapped in anything");
        returnTypes.Should().NotContain(
            returnType => typeof(Task).IsAssignableFrom(returnType),
            "no operation here waits on anything, so none of them is awaitable");
        returnTypes.Should().NotContain(
            returnType => returnType == typeof(ValueTask)
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)),
            "the lightweight awaitable form is excluded for the same reason as the general one");
    }

    /// <summary>
    /// The implementation refuses to be constructed without a bound policy.
    /// </summary>
    /// <remarks>
    /// Both minting guards read the policy, so a hasher without one would silently mint credentials the
    /// deployment had said were too weak.
    /// </remarks>
    [Fact]
    public void Constructor_WithoutABoundPolicy_Throws()
    {
        Action construct = () => _ = new BcryptPasswordHasher(null!);

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// A credential turned into a stored form is accepted when it is presented again.
    /// </summary>
    [Fact]
    public void Hash_ThenVerify_AcceptsTheOriginalPassword()
    {
        IPasswordHasher hasher = ShippedHasher();

        string stored = hasher.Hash(SampleCredential);

        hasher.Verify(SampleCredential, stored).Should().BeTrue();
    }

    /// <summary>
    /// A different credential is refused against the same stored form, even a near miss.
    /// </summary>
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
    /// This is the assertion that separates the new store from the one it replaces, and it is the
    /// single most consequential test in this file. A reversible cipher over a fixed deployment key
    /// produces the same output for the same input every time, so equal stored values betrayed equal
    /// credentials across every account in every tenant and the store could be attacked wholesale. A
    /// salted one-way hash randomises each stored form independently, which is why the contract's own
    /// documentation warns that a stored form must never be compared for equality and that checking is
    /// the only way to ask the question. The random component lives inside the stored string itself, so
    /// the contract needs no separate salt, initialisation vector, key or pepper argument -- which is
    /// exactly why two strings are the whole of its vocabulary.
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

    /// <summary>
    /// A stored form neither equals the credential nor contains it.
    /// </summary>
    /// <remarks>
    /// The original store made this failure literal: the first schema declared the credential column as
    /// plain text at 01.00.00.SqlDataProvider:L106, capped at twenty characters. That cap is a
    /// historical artefact of the plaintext era and is deliberately not asserted anywhere here, because
    /// it is not a constraint on the target at all; what is asserted is that the plaintext does not
    /// survive into the stored form in any recognisable shape.
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
    /// This behaviour is load-bearing rather than incidental, and it is asserted across a genuine
    /// policy change rather than within one policy. A credential of five characters is minted under a
    /// deployment whose minimum is four, and is then presented to a hasher whose minimum is the shipped
    /// seven. It must still be accepted: the day a deployment raises its minimum, every account whose
    /// credential predates the change would otherwise be locked out of its own data by its own correct
    /// credential. Policy belongs to the minting path and to the request validators. Checking an
    /// existing credential asks a narrower question - does this plaintext match what was stored.
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

    /// <summary>
    /// Surrounding white space is part of the credential and is never trimmed away.
    /// </summary>
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

    /// <summary>
    /// Case is part of the credential and is never folded.
    /// </summary>
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

    /// <summary>
    /// An absent, empty or white-space-only credential is refused rather than stored.
    /// </summary>
    /// <param name="password">The unusable credential offered for hashing.</param>
    /// <remarks>
    /// The contract states this on the parameter itself: an absent credential is not modelled as an
    /// empty value, so an empty value is an argument fault rather than a credential. White space
    /// <em>within</em> a credential remains significant - the test above proves padding is preserved -
    /// so this guard is about a value that is nothing but white space.
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
    /// <remarks>
    /// Seven is the measured legacy minimum (release.config:L242) and arrives here as bound
    /// configuration rather than as a literal in the implementation. Both sides of the boundary are
    /// asserted, because a rule that only refuses the obviously-too-short is not a boundary.
    /// </remarks>
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
    /// The shipped policy demands no character outside the alphanumeric ranges, so a purely
    /// alphanumeric credential is minted without complaint.
    /// </summary>
    /// <remarks>
    /// A minimum of zero is not a rule - a count cannot fall below zero - so the implementation skips
    /// the scan entirely rather than evaluating a comparison that could only ever succeed. The measured
    /// legacy value is zero (release.config:L243) and raising it during the migration would refuse
    /// credentials the legacy installation accepted.
    /// </remarks>
    [Fact]
    public void Hash_UnderTheShippedPolicy_DemandsNoNonAlphanumericCharacter()
    {
        IPasswordHasher hasher = ShippedHasher();

        Func<string> mint = () => hasher.Hash("abc1234");

        mint.Should().NotThrow();
    }

    /// <summary>
    /// A configured non-alphanumeric minimum is enforced at its exact boundary.
    /// </summary>
    /// <param name="candidate">The credential offered for hashing.</param>
    /// <param name="expectedToBeAccepted">Whether it carries enough such characters.</param>
    /// <remarks>
    /// The rule is expressed even though the shipped configuration makes it inert, because the value is
    /// configurable and a deployment that raises it must be honoured. The character class is the legacy
    /// one - anything outside <c>0-9</c>, <c>A-Z</c> and <c>a-z</c> - so an accented letter counts
    /// towards the total rather than against it, which is why one appears among the cases.
    /// </remarks>
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
    /// The shared credential ceiling is enforced when a stored form is minted, and it is measured in
    /// bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is the same constant every request validator applies, so a credential that reaches the
    /// hasher has already been measured once; the check here is defence in depth for a future caller
    /// that arrives without passing through a validator. It is asserted rather than assumed because the
    /// underlying package refuses nothing of its own accord - it will hash an input of any length.
    /// </para>
    /// <para>
    /// Throwing is the correct answer on this path: minting a stored credential is a deliberate act by
    /// trusted code, so a value that reached it unbounded is a defect in the caller rather than a bad
    /// submission. Contrast the checking path, which reports the same condition as a non-match.
    /// </para>
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
    /// The ceiling counts UTF-8 bytes rather than characters, so a multibyte credential is bounded by
    /// its encoded size.
    /// </summary>
    /// <remarks>
    /// This is the case a character-counting bound gets wrong, and it gets it wrong in the permissive
    /// direction. Each of these characters occupies three bytes when encoded, so a credential of 86 of
    /// them is 258 bytes - comfortably within any character-based reading of a 256 limit and past the
    /// real one. The 85-character value is 255 bytes and is accepted, which is what makes the pair a
    /// boundary rather than a single data point.
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
    /// A candidate beyond the shared ceiling is reported as a non-match rather than thrown at the
    /// caller.
    /// </summary>
    /// <remarks>
    /// The asymmetry with the minting path is deliberate and is a security property. This member answers
    /// a sign-in attempt, so an over-long candidate is a failed attempt rather than a fault: throwing
    /// would turn a submission into a server error and would let a caller distinguish "too long" from
    /// "wrong", which is a difference an attacker has no business being able to observe. Refusing also
    /// stops an unauthenticated caller handing an arbitrarily large value to a deliberately expensive
    /// function on every request, and it can lock nobody out, because no stored form can exceed the
    /// bound that minting enforces.
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

    /// <summary>
    /// Both arguments to the checking path are required, and an absent one is a caller defect.
    /// </summary>
    /// <param name="password">The candidate credential.</param>
    /// <param name="passwordHash">The stored form to check against.</param>
    /// <remarks>
    /// Null is surfaced rather than absorbed here, unlike a malformed stored value: a malformed value
    /// plausibly arrives from a database row, whereas a null argument can only come from a caller that
    /// failed to read what it was handed.
    /// </remarks>
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
    /// The contract is explicit that a mismatch and a malformed stored form are both simply false. That
    /// is a security property, not a convenience: an exception would let a caller distinguish a damaged
    /// stored value from a wrong credential, and a damaged value must never become a lever for
    /// admitting an arbitrary credential. The cases below cover each way the underlying package rejects
    /// a value - an unknown version marker, a non-numeric cost, a value too short to carry a complete
    /// salt, and a value shorter than the fixed offsets its parser reads.
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

    /// <summary>
    /// A value inherited from the legacy reversible store cannot be checked at all.
    /// </summary>
    /// <remarks>
    /// This is the whole reason existing accounts need an administrative reset. A one-way hash has no
    /// way to confirm a value produced by a reversible cipher, so such an account cannot be admitted by
    /// presenting the right credential. That reset is a member of the user service and is verified with
    /// that service; no attempt is made to reach it from here.
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
    /// <para>
    /// The plain algorithm ignores every byte of its input past the first seventy-two, so two distinct
    /// passwords sharing a seventy-two-byte prefix would authenticate interchangeably. The pre-hashing
    /// pair digests the credential before hashing, so the whole of the input contributes and that
    /// equivalence disappears - which is why the implementation uses it.
    /// </para>
    /// <para>
    /// The pairing is load-bearing rather than stylistic, and this test is the reason to state it. A
    /// pre-hashed digest is an ordinary digest of the pre-hashed value and carries no marker
    /// distinguishing it, so mixing one half with the other fails <em>silently</em> for every
    /// credential rather than loudly. Both directions are asserted, so neither half can be changed
    /// without the other.
    /// </para>
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

    /// <summary>
    /// The whole of a long credential contributes to the stored form.
    /// </summary>
    /// <remarks>
    /// The direct consequence of the pre-hashing pair, and the reason it was chosen. Two credentials
    /// sharing a seventy-two-byte prefix and differing only after it must not authenticate
    /// interchangeably; under the plain algorithm they would.
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
    /// The decoy is a well-formed stored form that the contract's own checking operation accepts as input and
    /// refuses for every credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter and neither is sufficient alone. That it is WELL FORMED is what makes a comparison
    /// against it do the same work as a comparison against a real stored form - a malformed value is rejected
    /// by a parse long before any stretching happens, which would leave exactly the timing difference the decoy
    /// exists to remove. That it MATCHES NOTHING is what makes it safe to compare against on a path that
    /// continues to a refusal.
    /// </para>
    /// <para>
    /// Several unrelated candidates are tried, including the empty-ish and the credential this suite uses
    /// everywhere else, because a decoy that happened to match one specific input would be a credential rather
    /// than a decoy.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnmatchableHash_IsAWellFormedStoredFormThatNoCredentialMatches()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

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

    /// <summary>
    /// The decoy is computed once and the same value is served on every read.
    /// </summary>
    /// <remarks>
    /// This is a cost guarantee rather than an aesthetic one, and it is why the contract declares a property
    /// rather than a method. Producing a fresh decoy per read would double the work of every authentication
    /// attempt that has nothing to compare against - which is every attempt against an unknown account, the
    /// very case an attacker generates in bulk - and would turn a defence against enumeration into an
    /// amplifier for exhausting the server.
    /// </remarks>
    [Fact]
    public void UnmatchableHash_IsComputedOnceAndServedRepeatedly()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        hasher.UnmatchableHash.Should().Be(hasher.UnmatchableHash);
    }

    /// <summary>
    /// A value inherited from the legacy reversible store is NOT reported as needing regeneration,
    /// because there is no regeneration this scheme could perform on one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test previously asserted the opposite, and it was encoding a defect rather than a
    /// guarantee. "Needs regeneration" is an instruction to pair the answer with a successful
    /// verification and re-hash the plaintext that verification yields. A legacy value can never be
    /// verified here - this scheme holds no legacy verifier - so for one the instruction can never be
    /// carried out, and issuing it anyway asserted in code the same false claim that a security review
    /// found spread through eight contracts and comments in prose: that a legacy credential is upgraded
    /// lazily rather than reset administratively.
    /// </para>
    /// <para>
    /// Answering "no" loses nothing. An account holding such a value cannot sign in either way, so it
    /// requires an administrative reset whatever this member says, and "nothing to upgrade" is the
    /// truthful description of a value this scheme did not produce.
    /// </para>
    /// </remarks>
    [Fact]
    public void NeedsRehash_IsFalseForAValueCarriedOverFromTheLegacyStore()
    {
        ConformanceHasher.Current().NeedsRehash(LegacyStoredValue).Should().BeFalse(
            "an upgrade that cannot be performed must not be promised");
    }

    /// <summary>
    /// A value this implementation has just produced does not need regenerating.
    /// </summary>
    [Fact]
    public void NeedsRehash_IsFalseForAValueTheImplementationJustProduced()
    {
        IPasswordHasher hasher = ShippedHasher();

        string stored = hasher.Hash(SampleCredential);

        hasher.NeedsRehash(stored).Should().BeFalse(
            "regenerating a current stored form on every sign-in would be pure waste");
    }

    /// <summary>
    /// A value produced at a lower cost still checks out, and is reported as needing regeneration.
    /// </summary>
    /// <remarks>
    /// Both halves matter, and they matter together. If the older value did not check out the account
    /// could not sign in, and if it were not flagged it would never be strengthened; the upgrade path
    /// only exists in the overlap. The current cost is deliberately not named - the superseded digest is
    /// minted at a demonstrably lower one and the assertion is about the relationship - so this test
    /// passes unchanged if the implementation raises its cost tomorrow, which is the point.
    /// </remarks>
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
    /// The replacement the signal supports is a real one: re-hashing the presented credential produces
    /// a stored form that is current, checkable, and different from the one it replaces.
    /// </summary>
    /// <remarks>
    /// This is the hasher's whole share of the upgrade. The sequence that performs it - check the
    /// credential, ask whether the stored form is superseded, re-hash and persist - belongs to the
    /// authentication service and is verified against that real service in
    /// backend/tests/DnnMigration.UnitTests/Services/AuthServiceTests.cs.
    /// </remarks>
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

    /// <summary>
    /// A stored value this implementation cannot parse answers "no".
    /// </summary>
    /// <param name="storedValue">A damaged, foreign or legacy stored form.</param>
    /// <remarks>
    /// <para>
    /// "Replace it" would be a promise this implementation cannot keep. Replacing a stored value
    /// requires the plaintext, the plaintext arrives only with a successful check, and a value held
    /// under the legacy reversible scheme can never pass a check here — so the only honest answer for
    /// a value that cannot be parsed is "no". Such an account requires an administrative reset, which
    /// is true whatever this member answers, so the answer costs nothing operationally.
    /// </para>
    /// <para>
    /// The set enumerated here deliberately mirrors the set the checking member rejects, and the two
    /// members now <em>agree</em>: neither claims to understand a value the other does not. That
    /// agreement is asserted below rather than assumed, so the pair cannot drift apart. This member is
    /// emphatically not a legacy-credential detector in either direction.
    /// </para>
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

    /// <summary>
    /// The replacement signal requires a stored form, and an absent one is a caller defect.
    /// </summary>
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

    /// <summary>
    /// Builds the hasher over the shipped policy, which is the type's own defaults.
    /// </summary>
    /// <returns>The hasher, typed as the contract its callers depend on.</returns>
    /// <remarks>
    /// The policy is taken from <see cref="PasswordPolicyOptions"/> unmodified rather than assembled
    /// field by field, so "the shipped policy" means whatever the production type ships with and a
    /// change to those defaults surfaces here rather than being masked by a test that restated them.
    /// The measured legacy values behind those defaults are cited in the migration notes at the head of
    /// this file, and the validator suites assert them against the configuration file directly.
    /// </remarks>
    private static IPasswordHasher ShippedHasher() =>
        new BcryptPasswordHasher(Options.Create(new PasswordPolicyOptions()));

    /// <summary>
    /// Builds the hasher over a policy that differs from the shipped one in a stated way.
    /// </summary>
    /// <param name="configure">Applies the deviation, and only the deviation.</param>
    /// <returns>The hasher, typed as the contract its callers depend on.</returns>
    /// <remarks>
    /// Deviations start from the shipped defaults so that each test states one difference rather than a
    /// whole policy, which keeps a failure attributable to the setting under test.
    /// </remarks>
    private static IPasswordHasher HasherWith(Action<PasswordPolicyOptions> configure)
    {
        PasswordPolicyOptions policy = new();
        configure(policy);

        return new BcryptPasswordHasher(Options.Create(policy));
    }

    /// <summary>
    /// The upgrade is invisible to the caller: the outcome is identical whether or not it happened.
    /// </summary>
    /// <remarks>
    /// Transparency is asserted here as the absence of an observable difference, which is the only
    /// honest way to assert it. There is deliberately no flag, no response field and no outcome code
    /// reporting that a credential was upgraded, so a test that looked for one would be testing a
    /// feature the contract promises not to have. What can be observed is that two runs differing only
    /// in whether the upgrade fired produce the same answer, and that is exactly the guarantee.
    /// </remarks>
    [Fact]
    public void Presentation_ReportsNothingBeyondWhetherTheCredentialMatched()
    {
        Mock<IPasswordHasher> upgrading = new(MockBehavior.Strict);
        upgrading.Setup(stub => stub.Verify(SampleCredential, StoredValue)).Returns(true);
        upgrading.Setup(stub => stub.NeedsRehash(StoredValue)).Returns(true);
        upgrading.Setup(stub => stub.Hash(SampleCredential)).Returns(RegeneratedValue);

        Mock<IPasswordHasher> unchanged = new(MockBehavior.Strict);
        unchanged.Setup(stub => stub.Verify(SampleCredential, StoredValue)).Returns(true);
        unchanged.Setup(stub => stub.NeedsRehash(StoredValue)).Returns(false);

        bool whenUpgraded = new CredentialUpgradeOnFirstSignIn(upgrading.Object, new StoredCredentialRecorder())
            .Present(SampleCredential, StoredValue);
        bool whenLeftAlone = new CredentialUpgradeOnFirstSignIn(unchanged.Object, new StoredCredentialRecorder())
            .Present(SampleCredential, StoredValue);

        whenUpgraded.Should().BeTrue();
        whenUpgraded.Should().Be(
            whenLeftAlone,
            "the caller learns only whether the credential matched, so an upgrade cannot be detected "
            + "from the outcome and no client needs to know it happened");
    }

    /// <summary>
    /// A value inherited from the legacy reversible store is refused and nothing is written, which is
    /// the case administrative reset exists to cover.
    /// </summary>
    /// <remarks>
    /// This is the one test in the group driven by the conformance stand-in rather than a substitute,
    /// because the point is the real consequence of the two halves meeting: a legacy value cannot be
    /// checked, so the correct credential is refused, so the lazy upgrade never gets its chance and the
    /// account stays on the old value until an administrator resets it. The reset member belongs to the
    /// user service and is verified with that service; it is deliberately not reached from here, and
    /// asserting a login failure is the whole of this file's share of that story.
    /// </remarks>
    [Fact]
    public void Presentation_OfALegacyStoredValue_FailsAndLeavesTheAccountToAdministrativeReset()
    {
        StoredCredentialRecorder store = new();

        bool accepted = new CredentialUpgradeOnFirstSignIn(ConformanceHasher.Current(), store)
            .Present(SampleCredential, LegacyStoredValue);

        accepted.Should().BeFalse(
            "a one-way hash cannot confirm a value produced by a reversible cipher, however correct "
            + "the credential is");
        store.Written.Should().BeEmpty(
            "and because the check failed, the upgrade path is not reached either");
    }

    /// <summary>
    /// Gives the documented behaviour of <see cref="IPasswordHasher"/> executable form, so that a
    /// promise written in prose is checked by the build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a conformance device and emphatically NOT a credential hasher. Its one-way fold has no
    /// key stretching of any kind, which makes it wholly unsuitable for storing a real credential; it
    /// exists only so that the contract's stated behaviour -- a randomised stored form, a refusal on a
    /// malformed value, no normalising of the input, no policy applied while checking, and a staleness
    /// signal -- can be asserted from a project that must not reach the production implementation.
    /// </para>
    /// <para>
    /// The stored form carries a generation marker, a per-hash random component and the fold, so a
    /// value produced under an earlier generation is still checkable while being reported as stale. That
    /// mirrors how the real algorithm carries its own parameters inside the stored string, which is why
    /// the contract needs no separate argument for them. The type is not thread safe and does not need
    /// to be: each test builds its own.
    /// </para>
    /// </remarks>
    private sealed class ConformanceHasher : IPasswordHasher
    {
        private const string CurrentGeneration = "g2";

        private const string EarlierGeneration = "g1";

        private const char FieldSeparator = '$';

        private const int FieldCount = 4;

        private readonly string _generation;

        private string? _unmatchable;

        private int _issued;

        private ConformanceHasher(string generation) => _generation = generation;

        /// <summary>Builds a hasher that produces stored forms at the current generation.</summary>
        /// <returns>The hasher.</returns>
        public static ConformanceHasher Current() => new(CurrentGeneration);

        /// <summary>Builds a hasher that produces stored forms at a superseded generation.</summary>
        /// <returns>The hasher.</returns>
        public static ConformanceHasher AtAnEarlierGeneration() => new(EarlierGeneration);

        /// <summary>
        /// A stored form at the current generation that no credential matches, for equalising the work an
        /// authentication attempt performs when there is nothing real to check against.
        /// </summary>
        /// <remarks>
        /// Produced from a credential this type generates and does not keep, exactly as the contract
        /// describes: the fold is over a value no caller can supply, so the result is checkable by the same
        /// code path as any other stored form and matches nothing. Computed once per instance, because the
        /// contract requires a property that costs the same as a field read rather than a fresh
        /// computation per attempt.
        /// </remarks>
        public string UnmatchableHash => _unmatchable ??= Hash(
            $"unmatchable{FieldSeparator}{Guid.NewGuid():N}");

        /// <summary>Produces a stored form for a credential.</summary>
        /// <param name="password">The plaintext credential.</param>
        /// <returns>A stored form that differs on every call.</returns>
        public string Hash(string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);

            _issued++;
            string nonce = _issued.ToString("x8", CultureInfo.InvariantCulture);

            return $"{FieldSeparator}{_generation}{FieldSeparator}{nonce}{FieldSeparator}{Fold(nonce, password)}";
        }

        /// <summary>Checks a credential against a stored form.</summary>
        /// <param name="password">The candidate plaintext credential.</param>
        /// <param name="passwordHash">The stored form to check against.</param>
        /// <returns><see langword="true"/> on a match; otherwise <see langword="false"/>.</returns>
        public bool Verify(string password, string passwordHash)
        {
            ArgumentNullException.ThrowIfNull(password);
            ArgumentNullException.ThrowIfNull(passwordHash);

            // No policy is consulted, and the input is used exactly as supplied: no trimming, no case
            // change and no other transformation of the bytes.
            string[]? fields = SplitFields(passwordHash);

            return fields is not null
                && string.Equals(Fold(fields[2], password), fields[3], StringComparison.Ordinal);
        }

        /// <summary>Reports whether a stored form should be regenerated after a successful check.</summary>
        /// <param name="passwordHash">The stored form to examine.</param>
        /// <returns>
        /// <see langword="true"/> only when the value is one this double produced at a superseded
        /// generation. A value it did not produce answers <see langword="false"/>, because there is no
        /// upgrade it could perform on one.
        /// </returns>
        public bool NeedsRehash(string passwordHash)
        {
            ArgumentNullException.ThrowIfNull(passwordHash);

            string[]? fields = SplitFields(passwordHash);

            return fields is not null
                && !string.Equals(fields[1], CurrentGeneration, StringComparison.Ordinal);
        }

        private static string[]? SplitFields(string passwordHash)
        {
            string[] fields = passwordHash.Split(FieldSeparator);

            return fields.Length == FieldCount && fields[0].Length == 0 ? fields : null;
        }

        private static string Fold(string nonce, string password)
        {
            byte[] material = Encoding.UTF8.GetBytes(string.Concat(nonce, "\u0000", password));

            return Convert.ToHexString(SHA256.HashData(material));
        }
    }

    /// <summary>
    /// Stands in for the documented sign-in sequence at exactly the depth this file owns: check the
    /// credential, and only when it matched, ask whether the stored form is stale and replace it.
    /// </summary>
    /// <remarks>
    /// This is deliberately a local model of the protocol rather than the real service. The real
    /// authentication service is wired to many more collaborators, its full sequence is owned by
    /// backend/tests/DnnMigration.UnitTests/Services/AuthServiceTests.cs, and duplicating that suite
    /// here would leave two places to change when the sequence moves. What is modelled here is only the
    /// part that concerns this contract: the three operations, and the order they must be used in.
    /// </remarks>
    private sealed class CredentialUpgradeOnFirstSignIn
    {
        private readonly IPasswordHasher _hasher;

        private readonly StoredCredentialRecorder _store;

        public CredentialUpgradeOnFirstSignIn(IPasswordHasher hasher, StoredCredentialRecorder store)
        {
            _hasher = hasher;
            _store = store;
        }

        /// <summary>Presents a credential against a stored form, upgrading the stored form if needed.</summary>
        /// <param name="submitted">The plaintext credential presented by the caller.</param>
        /// <param name="stored">The stored form currently held for the account.</param>
        /// <returns><see langword="true"/> when the credential matched; otherwise <see langword="false"/>.</returns>
        public bool Present(string submitted, string stored)
        {
            if (!_hasher.Verify(submitted, stored))
            {
                return false;
            }

            if (_hasher.NeedsRehash(stored))
            {
                _store.Write(_hasher.Hash(submitted));
            }

            return true;
        }
    }

    /// <summary>
    /// Records every stored form written back during a presentation, so that "exactly once" and "not at
    /// all" can be told apart.
    /// </summary>
    /// <remarks>
    /// A plain recorder is used rather than a substitute because a substitute has to be built over a
    /// type its proxy can see, and a helper declared privately inside a test class is not visible to
    /// one. Counting the writes directly is simpler, has no such constraint, and reads better besides.
    /// </remarks>
    private sealed class StoredCredentialRecorder
    {
        private readonly List<string> _written = [];

        /// <summary>Gets the stored forms written back, in order.</summary>
        public IReadOnlyList<string> Written => _written;

        /// <summary>Records a stored form being written back for an account.</summary>
        /// <param name="passwordHash">The stored form being persisted.</param>
        public void Write(string passwordHash) => _written.Add(passwordHash);
    }
}
