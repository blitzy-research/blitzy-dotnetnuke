using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Security;

// MIGRATION: the credential store changes from a reversible cipher to a one-way salted hash, and that is a
// security fix rather than a like-for-like port.

/// <summary>
/// Covers the DECLARED SHAPE of <see cref="IPasswordHasher"/>, the contract the whole of the migrated
/// authentication surface reaches credential storage through: its exact member set, the absence of any
/// property beyond the decoy and of any event, the absence of any by-reference parameter, and the wholly
/// synchronous signatures.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is about production metadata on a public Domain type, so it needs no reference
/// beyond the one this project has.
/// </para>
/// <para>
/// The value of this group is that it fails for a reason no behavioural test can reach. A hasher that
/// gained a read-back member - a <c>GetPassword</c>, a <c>Decrypt</c>, a settable decoy, an event carrying
/// a stored value - would pass every round-trip assertion ever written while reopening exactly the
/// capability the migration exists to close.
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
    /// The set grew by one, and the addition is the exception that proves the rule rather than a relaxation
    /// of it. <c>UnmatchableHash</c> reads BACK NOTHING: it serves a representation of a credential the
    /// implementation generated, used and discarded, so there is no account it belongs to and no plaintext
    /// anywhere that matches it.
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

    /// <summary>The abstraction declares exactly one property - the read-only decoy - and no event.</summary>
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

    /// <summary>No parameter of any operation is passed by reference.</summary>
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

    /// <summary>Every argument the contract accepts is a plain string.</summary>
    /// <remarks>
    /// This is the positive form of two separate guarantees, and it is stronger than asserting either of
    /// them individually. It fixes the argument shape to the plaintext and the stored form, and because a
    /// string is the only permitted parameter type it simultaneously establishes that no operation accepts
    /// a cooperative-cancellation argument.
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

    /// <summary>Every operation is synchronous, returning either the stored form or a plain decision.</summary>
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
