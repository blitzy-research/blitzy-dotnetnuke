using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DnnMigration.Domain.Abstractions.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Security;

// MIGRATION: algorithm-level verification of the concrete BcryptPasswordHasher type is deliberately
// NOT located in this project, and that placement is an architectural decision rather than a coverage
// gap. The concrete type is internal to the infrastructure assembly, and this project references the
// application assembly and nothing else -- its own project file states in writing that an
// infrastructure reference must not be added. Layering here is enforced by the compiler rather than by
// review, so the boundary IS the requirement, and weakening it to reach the class would defeat the
// property the boundary exists to guarantee. The real algorithm is proved end to end by
// DnnMigration.IntegrationTests, which resolves the public IPasswordHasher registration from the
// running host's service provider and drives it through a real credential store. What this file proves
// instead is the contract every caller is written against, which is precisely what a round trip
// against the concrete class could never prove: that no read-back member exists at all, that the
// stored form is non-deterministic, that checking a credential ignores policy, and that the credential
// upgrade path runs in the right order and only after a successful check.

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

// MIGRATION: existing rows migrate lazily, because a one-way hash cannot check a value that was
// stored under the legacy reversible scheme. On the first sign-in that succeeds, the correct plaintext
// is hashed again and the new stored form replaces the old one; an account that never signs in again
// is left to an administrative reset, whose member belongs to the user service and is verified
// alongside that service. The upgrade is invisible to the caller -- it adds no member, no response
// field and no outcome code -- so the tests below pin it as an interaction on the hasher and
// deliberately never look for a flag.

// MIGRATION: the measured legacy password policy is preserved verbatim and is NOT tightened.
// Website/release.config records minRequiredPasswordLength="7" at L242,
// minRequiredNonalphanumericCharacters="0" at L243, requiresQuestionAndAnswer="false" at L241 and an
// address-uniqueness flag of false at L244. Tightening a credential policy during a migration would
// lock the existing membership out of its own accounts, so policy is carried across unchanged, and it
// is asserted with the request validators because it is an application-layer concern rather than a
// property of this contract. What this file does pin is the consequence for the hasher: checking a
// credential must not consult policy at all, or every stored credential shorter than some future
// minimum would silently stop working. Note also that no lockout threshold or window is asserted
// anywhere here, because none was ever configured -- the two attributes appear only inside the
// documentation comment at L224-L225 and were never set on the provider element at L236-L247.

// MIGRATION: the weak-default-credential advisory survives the change completely unchanged, which is
// worth recording precisely because it is a divergence that turns out not to be one. The legacy check
// at Library/Components/Users/UserController.vb:L1144-L1152 re-graded an already successful sign-in
// when the submitted plaintext was one of the four credentials the product shipped with, comparing the
// submitted argument against those literals after the credential itself had already been checked. It
// never read the stored form, so it never depended on the stored form being reversible, and it needs
// nothing at all from this contract. The advisory is raised on the sign-in path and is asserted there.

/// <summary>
/// Pins the one-way credential-hashing contract that the whole of the migrated authentication surface
/// is written against: its shape, the behaviour its documentation promises, and the order in which a
/// caller is required to use it when upgrading a credential that predates the migration.
/// </summary>
/// <remarks>
/// <para>
/// Three groups of tests appear below, and they are deliberately different in kind. The first group
/// reflects over the abstraction itself and therefore asserts facts about real production code: the
/// exact member set, the absence of properties and events, the absence of any by-reference parameter,
/// and the wholly synchronous signatures. The second group gives the documented behaviour executable
/// form through a conformance stand-in, so that a promise written in prose becomes a promise a build
/// can check. The third group asserts the interaction protocol a caller must follow, using a
/// substituted hasher, which is the only place the ordering guarantee can be expressed at all.
/// </para>
/// <para>
/// The stand-in is not, and must never be mistaken for, a credential hasher. It exists to state the
/// contract, and its one-way fold has no key stretching whatsoever, which makes it entirely unsuitable
/// for storing a real credential. The production implementation is a singleton registered by the
/// infrastructure assembly and is exercised for real by the integration suite.
/// </para>
/// <para>
/// Scope is narrow on purpose. Nothing here asserts sign-in, session, permission or transport
/// behaviour: the full sign-in sequence, including the very upgrade interaction modelled below as it is
/// wired into the real service, is owned by
/// backend/tests/DnnMigration.UnitTests/Services/AuthServiceTests.cs, and the credential policy itself
/// is owned by the validator suites under backend/tests/DnnMigration.UnitTests/Validation/. Neither is
/// duplicated here, and the absence of either from this file is intentional.
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

    // The same credential with its final character removed, so a near miss is still a miss.
    private const string NearMissCredential = "Migr8tion!Pas";

    // Five characters, which is below the legacy minimum of seven recorded at release.config:L242.
    // Used to prove that checking a credential does not apply the current policy.
    private const string ShortCredential = "dnn4x";

    // An obviously synthetic stand-in for a value inherited from the legacy reversible store. It is
    // deliberately not shaped like anything this contract produces and is deliberately not a real
    // stored value from anywhere.
    private const string LegacyStoredValue = "legacy-reversible-store-sample";

    // An opaque stored value handed to a substituted hasher, which never parses it.
    private const string StoredValue = "$g2$0000002a$STORED";

    // The value a substituted hasher returns when it regenerates a stored form.
    private const string RegeneratedValue = "$g2$0000beef$REGENERATED";

    // The complete public surface of the abstraction, in the order it is declared.
    private static readonly string[] ContractOperations = ["Hash", "Verify", "NeedsRehash"];

    /// <summary>
    /// The abstraction declares exactly three operations and nothing else.
    /// </summary>
    /// <remarks>
    /// An exact-set assertion is used here in preference to a list of forbidden names, and it is the
    /// stronger of the two: a denylist only rejects the read-back spellings somebody thought of in
    /// advance, whereas this test fails the moment <em>any</em> fourth member is added, whatever it is
    /// called. That is the machine-checkable form of the guarantee recorded in the migration notes
    /// above, which is that the impossibility of reading a password back is a property of the shape of
    /// the contract rather than of an implementer's discipline.
    /// </remarks>
    [Fact]
    public void Contract_DeclaresExactlyTheThreeOneWayOperations()
    {
        MethodInfo[] operations = typeof(IPasswordHasher).GetMethods();

        operations.Select(operation => operation.Name)
            .Should()
            .BeEquivalentTo(
                ContractOperations,
                "a fourth member is how a read-back capability would re-enter the system, so the "
                + "member set is fixed rather than merely reviewed");
    }

    /// <summary>
    /// The abstraction declares no property and no event.
    /// </summary>
    /// <remarks>
    /// A property is the quietest way to reintroduce a read-back capability, because it looks like
    /// state rather than like an operation. An event would hand a stored value, or the plaintext that
    /// matched it, to an arbitrary subscriber. Neither is available.
    /// </remarks>
    [Fact]
    public void Contract_DeclaresNoPropertyAndNoEvent()
    {
        typeof(IPasswordHasher).GetProperties().Should().BeEmpty();
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
    /// A credential turned into a stored form is accepted when it is presented again.
    /// </summary>
    [Fact]
    public void Hash_ThenVerify_AcceptsTheOriginalPassword()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        string stored = hasher.Hash(SampleCredential);

        hasher.Verify(SampleCredential, stored).Should().BeTrue();
    }

    /// <summary>
    /// A different credential is refused against the same stored form, even a near miss.
    /// </summary>
    [Fact]
    public void Verify_RejectsADifferentPassword()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

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
        ConformanceHasher hasher = ConformanceHasher.Current();

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
        string stored = ConformanceHasher.Current().Hash(SampleCredential);

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
    /// This behaviour is load-bearing rather than incidental. The legacy deployment required seven
    /// characters at release.config:L242, and the migration keeps that figure rather than raising it;
    /// but even a policy that never changes must not be consulted while checking, because the day it
    /// does change every account whose credential predates the change would be locked out of its own
    /// data by its own correct credential. Policy belongs to the request validators, which reject a new
    /// credential that falls short. Checking an existing one asks a narrower question: does this
    /// plaintext match what was stored.
    /// </remarks>
    [Fact]
    public void Verify_DoesNotApplyTheCurrentPolicy()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        string stored = hasher.Hash(ShortCredential);

        hasher.Verify(ShortCredential, stored).Should().BeTrue(
            "a stored credential shorter than the configured minimum must stay verifiable, or "
            + "tightening the policy locks out the existing membership");
    }

    /// <summary>
    /// Surrounding white space is part of the credential and is never trimmed away.
    /// </summary>
    [Fact]
    public void Hash_DoesNotTrimTheSubmittedValue()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();
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
        ConformanceHasher hasher = ConformanceHasher.Current();

        string stored = hasher.Hash(SampleCredential);

        hasher.Verify(SampleCredential.ToLowerInvariant(), stored).Should().BeFalse();
        hasher.Verify(SampleCredential.ToUpperInvariant(), stored).Should().BeFalse(
            "case folding, like trimming or any other normalising of the bytes, throws away part of "
            + "the credential the account holder actually typed");
    }

    /// <summary>
    /// An empty or white-space-only credential is refused rather than stored.
    /// </summary>
    /// <param name="password">The unusable credential offered for hashing.</param>
    /// <remarks>
    /// The contract states this on the parameter itself: an absent credential is not modelled as an
    /// empty value, so an empty value is an argument fault rather than a credential. The exception
    /// family is the framework-conventional way to express that rejection.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Hash_RejectsAnEmptyOrWhitespaceOnlyPassword(string password)
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        Func<string> rejected = () => hasher.Hash(password);

        rejected.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A stored form that cannot be parsed is reported as a refusal rather than thrown at the caller.
    /// </summary>
    /// <param name="storedValue">A damaged or foreign stored form.</param>
    /// <remarks>
    /// The contract is explicit that a mismatch and a malformed stored form are both simply false. That
    /// is a security property, not a convenience: an exception would let a caller distinguish a damaged
    /// stored value from a wrong credential, and a damaged value must never become a lever for
    /// admitting an arbitrary credential.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-stored-value")]
    [InlineData("$g2$missing-the-fold")]
    [InlineData("$$$")]
    public void Verify_ReportsAMalformedStoredValueAsFalseInsteadOfThrowing(string storedValue)
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        Func<bool> probe = () => hasher.Verify(SampleCredential, storedValue);

        probe.Should().NotThrow().Which.Should().BeFalse(
            "a damaged stored form is a refusal, and refusing is the only safe answer");
    }

    /// <summary>
    /// A value inherited from the legacy reversible store cannot be checked at all.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the migration needs a credential-upgrade path. A one-way hash has no
    /// way to confirm a value that was produced by a reversible cipher, so such an account cannot be
    /// admitted by presenting the right credential and is instead left to an administrative reset. That
    /// reset is a member of the user service and is verified with that service; no attempt is made to
    /// reach it from here.
    /// </remarks>
    [Fact]
    public void Verify_CannotCheckAValueCarriedOverFromTheLegacyStore()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        hasher.Verify(SampleCredential, LegacyStoredValue).Should().BeFalse(
            "the legacy value was never a hash, so no credential can match it");
    }

    /// <summary>
    /// A value inherited from the legacy reversible store is reported as needing regeneration.
    /// </summary>
    [Fact]
    public void NeedsRehash_IsTrueForAValueCarriedOverFromTheLegacyStore()
    {
        ConformanceHasher.Current().NeedsRehash(LegacyStoredValue).Should().BeTrue();
    }

    /// <summary>
    /// A value this implementation has just produced does not need regenerating.
    /// </summary>
    [Fact]
    public void NeedsRehash_IsFalseForAValueTheImplementationJustProduced()
    {
        ConformanceHasher hasher = ConformanceHasher.Current();

        string stored = hasher.Hash(SampleCredential);

        hasher.NeedsRehash(stored).Should().BeFalse(
            "regenerating a current stored form on every sign-in would be pure waste");
    }

    /// <summary>
    /// A value produced under weaker settings still checks out, and is reported as needing
    /// regeneration.
    /// </summary>
    /// <remarks>
    /// Both halves matter, and they matter together. If the older value did not check out the account
    /// could not sign in, and if it were not flagged it would never be strengthened; the upgrade path
    /// only exists in the overlap. The strength setting itself is intentionally invisible here -- it is
    /// private to the implementation, no number is named, and this test would pass unchanged if that
    /// number were raised tomorrow, which is precisely the point.
    /// </remarks>
    [Fact]
    public void NeedsRehash_IsTrueForAnEarlierGenerationValueThatStillVerifies()
    {
        ConformanceHasher earlier = ConformanceHasher.AtAnEarlierGeneration();
        ConformanceHasher current = ConformanceHasher.Current();

        string stored = earlier.Hash(SampleCredential);

        current.Verify(SampleCredential, stored).Should().BeTrue(
            "an account must not be locked out merely because its stored form is out of date");
        current.NeedsRehash(stored).Should().BeTrue(
            "and it must not be left out of date once the credential has been presented correctly");
    }

    /// <summary>
    /// An out-of-date stored form is regenerated once, and stored once, when the credential is
    /// presented correctly.
    /// </summary>
    /// <remarks>
    /// The three operations have an order, and the order is the whole of the migration path: check
    /// first, ask whether the stored form is out of date second, regenerate third. A substituted hasher
    /// is the only way to assert an order, which is why this group uses one; the strict substitute also
    /// fails the test if the caller reaches for an operation it was never meant to touch.
    /// </remarks>
    [Fact]
    public void FirstSuccessfulPresentation_OfAnOutOfDateValue_RegeneratesAndStoresItExactlyOnce()
    {
        Mock<IPasswordHasher> hasher = new(MockBehavior.Strict);
        hasher.Setup(stub => stub.Verify(SampleCredential, StoredValue)).Returns(true);
        hasher.Setup(stub => stub.NeedsRehash(StoredValue)).Returns(true);
        hasher.Setup(stub => stub.Hash(SampleCredential)).Returns(RegeneratedValue);

        StoredCredentialRecorder store = new();

        bool accepted = new CredentialUpgradeOnFirstSignIn(hasher.Object, store)
            .Present(SampleCredential, StoredValue);

        accepted.Should().BeTrue();
        hasher.Verify(stub => stub.Hash(SampleCredential), Times.Once());
        store.Written.Should().ContainSingle(
            "the upgrade replaces the stored form once, not once per read and not repeatedly");
    }

    /// <summary>
    /// A stored form that is already current is left completely alone.
    /// </summary>
    [Fact]
    public void SuccessfulPresentation_OfACurrentValue_RegeneratesNothingAndStoresNothing()
    {
        Mock<IPasswordHasher> hasher = new(MockBehavior.Strict);
        hasher.Setup(stub => stub.Verify(SampleCredential, StoredValue)).Returns(true);
        hasher.Setup(stub => stub.NeedsRehash(StoredValue)).Returns(false);

        StoredCredentialRecorder store = new();

        bool accepted = new CredentialUpgradeOnFirstSignIn(hasher.Object, store)
            .Present(SampleCredential, StoredValue);

        accepted.Should().BeTrue();
        hasher.Verify(stub => stub.Hash(It.IsAny<string>()), Times.Never());
        store.Written.Should().BeEmpty(
            "a current stored form is already correct, so rewriting it would cost a database round "
            + "trip on every single sign-in for no benefit at all");
    }

    /// <summary>
    /// A credential that does not match is neither examined for staleness nor regenerated, and nothing
    /// is written.
    /// </summary>
    /// <remarks>
    /// This is the security-critical half of the ordering guarantee. Regenerating before, or without, a
    /// successful check would let anybody overwrite any account's stored credential with a hash of
    /// whatever they typed, which is an account takeover dressed as a maintenance routine. The upgrade
    /// therefore fires only after the credential has been confirmed, and this test fails loudly if that
    /// ever stops being true.
    /// </remarks>
    [Fact]
    public void FailedPresentation_NeitherInspectsNorRegeneratesTheStoredValue()
    {
        Mock<IPasswordHasher> hasher = new(MockBehavior.Strict);
        hasher.Setup(stub => stub.Verify(NearMissCredential, StoredValue)).Returns(false);

        StoredCredentialRecorder store = new();

        bool accepted = new CredentialUpgradeOnFirstSignIn(hasher.Object, store)
            .Present(NearMissCredential, StoredValue);

        accepted.Should().BeFalse();
        hasher.Verify(stub => stub.NeedsRehash(It.IsAny<string>()), Times.Never());
        hasher.Verify(stub => stub.Hash(It.IsAny<string>()), Times.Never());
        store.Written.Should().BeEmpty(
            "a wrong credential must never be able to change what is stored for an account");
    }

    /// <summary>
    /// The regenerated form is built from the plaintext just presented, and is stored exactly as the
    /// hasher produced it.
    /// </summary>
    /// <remarks>
    /// Both details are load-bearing. Regenerating from the plaintext is the only option available,
    /// since the old stored form cannot be undone; and storing the hasher's output verbatim matters
    /// because any adjustment on the way to the database -- a trim, a case change, a truncation to some
    /// legacy column width -- would produce a stored form that the hasher can no longer check.
    /// </remarks>
    [Fact]
    public void SuccessfulPresentation_RegeneratesFromTheSubmittedPlaintextAndStoresItVerbatim()
    {
        Mock<IPasswordHasher> hasher = new(MockBehavior.Strict);
        hasher.Setup(stub => stub.Verify(SampleCredential, StoredValue)).Returns(true);
        hasher.Setup(stub => stub.NeedsRehash(StoredValue)).Returns(true);
        hasher.Setup(stub => stub.Hash(SampleCredential)).Returns(RegeneratedValue);

        StoredCredentialRecorder store = new();

        new CredentialUpgradeOnFirstSignIn(hasher.Object, store).Present(SampleCredential, StoredValue);

        hasher.Verify(
            stub => stub.Hash(SampleCredential),
            Times.Once(),
            "the new stored form can only come from the plaintext that was just confirmed");
        store.Written.Should().ContainSingle().Which.Should().Be(
            RegeneratedValue,
            "what the hasher produced is what must be stored, character for character");
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

        private int _issued;

        private ConformanceHasher(string generation) => _generation = generation;

        /// <summary>Builds a hasher that produces stored forms at the current generation.</summary>
        /// <returns>The hasher.</returns>
        public static ConformanceHasher Current() => new(CurrentGeneration);

        /// <summary>Builds a hasher that produces stored forms at a superseded generation.</summary>
        /// <returns>The hasher.</returns>
        public static ConformanceHasher AtAnEarlierGeneration() => new(EarlierGeneration);

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
        /// <returns><see langword="true"/> when the value is legacy or superseded.</returns>
        public bool NeedsRehash(string passwordHash)
        {
            ArgumentNullException.ThrowIfNull(passwordHash);

            string[]? fields = SplitFields(passwordHash);

            return fields is null
                || !string.Equals(fields[1], CurrentGeneration, StringComparison.Ordinal);
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
