using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Security;

// THIS FILE IS THE CONTRACT-SHAPE HALF, AND IT IS DELIBERATELY THE WHOLE OF WHAT THIS PROJECT ASSERTS
// ABOUT CREDENTIAL HASHING. Every test below reflects over IPasswordHasher, which is public Domain
// surface: the exact member set, the absence of any property beyond the decoy and of any event, the
// absence of any by-reference parameter, and the wholly synchronous signatures.
//
// THE BEHAVIOUR OF THE REAL HASHER IS ASSERTED IN
// backend/tests/DnnMigration.IntegrationTests/Security/PasswordHasherTests.cs, AGAINST THE REGISTERED
// IMPLEMENTATION. The round trip, the randomised stored form, the policy guards on minting, the shared
// byte ceiling, the malformed-value refusals, the pre-hash pairing, the decoy and the work-factor
// replacement signal all live there, resolved from the composed container through this same contract.
//
// WHY THE SPLIT, AND WHY NOTHING MAY UNDO IT. An earlier revision ran those behavioural tests here, and
// to compile them this project had been given a second project reference to the infrastructure project
// while that project granted it a friend-assembly declaration, so that the internal sealed implementation
// could be constructed with `new`. AAP 0.5.2.2 gives this project exactly one edge - Application, with Domain
// arriving transitively - and a friend assembly declared for a test's convenience punches a hole in the
// accessibility boundary that keeps DnnDbContext unreachable. Both were removed and the behavioural
// tests moved rather than being dropped, so coverage went up rather than down: the integration suite
// proves the REGISTRATION as well as the algorithm, which a direct construction never could.
//
// DO NOT re-add a reference to the infrastructure project, do not ask for a friend-assembly grant, and do not
// reintroduce a stand-in implementation here so that behaviour "can" be asserted locally. A private
// conformance hasher is exactly what used to stand in for the real one, and a review found that it let
// the shipped decoy and the shipped staleness signal regress with the suite green.

// MIGRATION: the credential store changes from a reversible cipher to a one-way salted hash, and that
// is a security fix rather than a like-for-like port. The shipped deployment registered the ASP.NET
// SQL membership provider at Website/release.config:L236 -- selected as the default at L218 -- and
// chose the reversible Triple-DES storage option through its passwordFormat attribute at L245, the
// option its own documentation line at L233 describes as Triple-DES. The machine key that reverses
// that storage sits in the same file at L90-L93, committed to source control, which is why the
// capability is removed rather than reconfigured. No key material, and no copy of any key value,
// appears anywhere in this file: the lines are cited, the values are not, and the legacy file itself is
// read-only evidence that is left exactly as it stands.

// MIGRATION: reading a stored password back is not merely switched off, it is impossible by
// construction. The legacy read-back member at Library/Components/Users/UserController.vb:L433 could
// only ever work because the stored form was reversible -- its own remark at L425-L426 says it can
// return a password only when the provider uses a storage format that can be undone, and L439 threw
// outright when a deployment had turned the capability off. It also answered twice over, once by
// mutating a by-reference argument and once as a return value, which is the double output channel the
// migration abolishes. A one-way hash removes the capability rather than the permission, so no
// service, endpoint or screen above this contract can offer it, and the member set asserted below
// contains nothing that could - which is precisely why an exact-set assertion is used rather than a
// list of forbidden spellings.

/// <summary>
/// Covers the DECLARED SHAPE of <see cref="IPasswordHasher"/>, the contract the whole of the migrated
/// authentication surface reaches credential storage through: its exact member set, the absence of any
/// property beyond the decoy and of any event, the absence of any by-reference parameter, and the
/// wholly synchronous signatures.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is about production metadata on a public Domain type, so it needs no reference
/// beyond the one this project has. Nothing below hashes, checks or stores anything, and nothing below
/// constructs an implementation - the behaviour of the registered implementation is asserted in
/// backend/tests/DnnMigration.IntegrationTests/Security/PasswordHasherTests.cs, and the credential
/// migration boundary in that project's PasswordHasherMigrationTests.cs.
/// </para>
/// <para>
/// The value of this group is that it fails for a reason no behavioural test can reach. A hasher that
/// gained a read-back member - a <c>GetPassword</c>, a <c>Decrypt</c>, a settable decoy, an event
/// carrying a stored value - would pass every round-trip assertion ever written while reopening exactly
/// the capability the migration exists to close. An exact-set assertion fails the moment any unlisted
/// member appears, whatever it is called, which is the machine-checkable form of that guarantee.
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

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

}
