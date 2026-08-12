using System.Net.Sockets;
using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DnnMigration.UnitTests.Infrastructure;

/// <summary>
/// Covers the rule that decides whether a failure means the store could not serve - which the transport
/// answers 503 with a retry hint - or means this application has a defect, which stays 500.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the condition these facts guard was measured with the delivered container topology and the
/// database stopped: every data endpoint, sign-in included, answered <c>500 Internal Server Error</c>. The
/// payload disclosed nothing and the readiness view already reported <c>503</c> to an orchestrator, so what
/// was wrong was the instruction the status carried - a caller was told to report a server defect when the
/// truth was that a dependency was down and the request would succeed on a retry.
/// </para>
/// <para>
/// <strong>Both directions matter, and the false direction matters more.</strong> Answering 503 for a defect
/// tells a caller to retry a request that can never succeed and hides the fault behind a retry loop, which is
/// strictly worse than the 500 being replaced. Every fact below that asserts <see langword="false"/> is
/// therefore load-bearing rather than padding: a constraint violation, an invalid object name, a permission
/// refusal and an ordinary application fault must all keep their existing answer.
/// </para>
/// <para>
/// <strong>Severity is the decision, and that is a measured conclusion.</strong> A stopped SQL Server was
/// probed through a raw connection open, an Entity Framework query with retrying enabled and one with it
/// disabled; all three produced one <see cref="SqlException"/> with <c>Number = 0</c>, <c>Class = 20</c>,
/// <c>IsTransient</c> <see langword="false"/> and no inner exception. Keying on the client's transient flag
/// or on error numbers would have closed nothing. The fabrication below therefore parameterises SEVERITY,
/// which is what the sibling <see cref="DuplicateKeyTranslationTests"/> fabrication does not need to do -
/// severity is no part of that decision, and it is the whole of this one.
/// </para>
/// <para>
/// The real-world half is asserted in the integration project, where an unreachable server is dialled for
/// real and the container-resolved classifier is asked about the exception it actually raises. That is what
/// keeps the fabrication here honest: if the pinned client ever stopped reporting an unreachable server at
/// severity 20, that test would fail while these would not.
/// </para>
/// <para>
/// The classifier is <c>internal</c> to the Infrastructure assembly and is reached through the
/// <c>InternalsVisibleTo</c> that assembly already declares for this one. Nothing is made public for a
/// test's benefit.
/// </para>
/// </remarks>
public class StoreFailureClassificationTests
{
    /// <summary>Severity at and above which SQL Server abandons the statement and closes the connection.</summary>
    private const byte FatalSeverity = 20;

    /// <summary>Severity of an ordinary caller-visible refusal, which must NOT be read as an outage.</summary>
    private const byte RefusalSeverity = 16;

    /// <summary>A failure whose severity means the connection is gone is an availability failure.</summary>
    /// <param name="severity">The severity the store reported.</param>
    [Theory]
    [InlineData(FatalSeverity)]
    [InlineData((byte)21)]
    [InlineData((byte)24)]
    public void AConnectionTerminatingSeverity_IsReadAsTheStoreBeingUnavailable(byte severity)
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        // Number 0 is not a placeholder: it is the number the client actually reports for a server it could
        // not reach, measured through three separate call paths. A rule that needed a recognisable number
        // would have classified the commonest outage of all as a defect.
        SqlException outage = Fabricate(severity, (Number: 0, Message: "network-related or instance-specific error"));

        classifier.IsStoreUnavailable(outage).Should().BeTrue(
            "severity 20 and above means the statement was abandoned and the connection closed, so the same "
            + "request may well succeed once the condition clears - which is what 503 with a retry hint says");
    }

    /// <summary>A refusal the store issued while working correctly is not an availability failure.</summary>
    /// <param name="number">The error number the store reported.</param>
    /// <param name="description">What the number means, for the failure message.</param>
    [Theory]
    [InlineData(2627, "unique constraint violation")]
    [InlineData(2601, "unique index violation")]
    [InlineData(547, "foreign key violation")]
    [InlineData(208, "invalid object name")]
    [InlineData(207, "invalid column name")]
    [InlineData(229, "permission denied")]
    [InlineData(1205, "deadlock victim")]
    [InlineData(8134, "divide by zero")]
    public void AStoreRefusalAtOrdinarySeverity_IsNotReadAsUnavailability(int number, string description)
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException refusal = Fabricate(RefusalSeverity, (number, description));

        classifier.IsStoreUnavailable(refusal).Should().BeFalse(
            $"a {description} is the store answering correctly, and telling the caller to retry it would "
            + "invite an endless loop against a statement that will be refused every time");
    }

    /// <summary>A command timeout is an availability condition even though it closes no connection.</summary>
    [Fact]
    public void ACommandTimeout_IsReadAsUnavailability()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        // Reported below the fatal boundary, which is exactly why the number is named in the rule: the store
        // was reachable and did not answer in time, so the connection is intact and the request is still
        // worth retrying. This is the one arm severity alone would miss.
        SqlException timeout = Fabricate(RefusalSeverity, (Number: -2, Message: "Execution Timeout Expired."));

        classifier.IsStoreUnavailable(timeout).Should().BeTrue(
            "a timeout is a load or lock-contention condition rather than a defect, and it is the case a "
            + "retry hint exists for");
    }

    /// <summary>
    /// The whole chain is examined, because a store fault raised during a flush arrives wrapped by the
    /// object-relational mapper.
    /// </summary>
    [Fact]
    public void AnOutageWrappedByTheMapper_IsStillRecognised()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException outage = Fabricate(FatalSeverity, (Number: 0, Message: "connection forcibly closed"));

        // Two wrappers deep, which is the shape a write produces: the mapper wraps the provider fault, and a
        // retry strategy that exhausted its attempts wraps that. A surface-only test would classify the same
        // outage correctly on a read and as a defect on a write.
        DbUpdateException flushFailure = new("An error occurred while saving the entity changes.", outage);
        InvalidOperationException outermost = new("The operation could not be completed.", flushFailure);

        classifier.IsStoreUnavailable(outermost).Should().BeTrue(
            "the wrapper's own type says nothing about whether the store was reachable, so the cause is what "
            + "has to be classified");
    }

    /// <summary>A failed flush is not an availability failure merely by virtue of having failed.</summary>
    [Fact]
    public void AMapperFailureWithNoStoreFaultBeneathIt_IsNotReadAsUnavailability()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        DbUpdateConcurrencyException lostUpdate = new("The database operation was expected to affect 1 row.");

        classifier.IsStoreUnavailable(lostUpdate).Should().BeFalse(
            "a lost update asks the caller to re-read and retry the WORKFLOW, and answering it as a store "
            + "outage would hide a concurrency conflict behind an infrastructure message");
    }

    /// <summary>A transport fault on the path to the store is an availability failure.</summary>
    [Fact]
    public void ASocketFailureOnThePathToTheStore_IsReadAsUnavailability()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SocketException refused = new((int)SocketError.ConnectionRefused);

        classifier.IsStoreUnavailable(refused).Should().BeTrue(
            "the store need not be at fault for it to be unreachable; a refused connection, a reset and a "
            + "failed name resolution all arrive this way on some platforms");
    }

    /// <summary>A pool-exhaustion timeout is an availability failure.</summary>
    [Fact]
    public void AFrameworkLevelTimeout_IsReadAsUnavailability()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        classifier.IsStoreUnavailable(new TimeoutException("The connection pool timed out.")).Should().BeTrue(
            "waiting for a connection that never became free is a saturation condition, not a defect");
    }

    /// <summary>Every branch of a parallel failure is examined, not just the first.</summary>
    [Fact]
    public void AnAggregateCarryingOneOutage_IsRecognisedWhicheverBranchHoldsIt()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException outage = Fabricate(FatalSeverity, (Number: 0, Message: "server was not found"));

        AggregateException parallel = new(
            new InvalidOperationException("first branch, unrelated"),
            new AggregateException(outage));

        classifier.IsStoreUnavailable(parallel).Should().BeTrue(
            "a parallel operation reports every branch's failure, the store fault may be in any of them, and "
            + "flattening is what makes a nested aggregate no different from a flat one");
    }

    /// <summary>An ordinary application fault keeps the answer it had.</summary>
    /// <param name="failure">A failure that is a defect or a caller mistake rather than an outage.</param>
    [Theory]
    [MemberData(nameof(NonStoreFailures))]
    public void AnApplicationFault_IsNotReadAsUnavailability(Exception failure)
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        classifier.IsStoreUnavailable(failure).Should().BeFalse(
            "everything this classifier cannot positively identify as an availability condition must keep "
            + "the status it had, because a defect reported as a temporary outage is a defect nobody "
            + "investigates");
    }

    /// <summary>Nothing at all is classified as nothing, rather than as a second failure.</summary>
    [Fact]
    public void ANullFailure_IsAnsweredRatherThanThrownOn()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        // The caller is the exception handler, mid-way through composing a response to one failure. A
        // classifier that threw here would replace a well-formed error payload with no payload at all, which
        // is the one outcome worse than the wrong status code.
        classifier.IsStoreUnavailable(null).Should().BeFalse(
            "an error path must never be handed a second failure while handling the first");
    }

    /// <summary>A pathologically deep or cyclic chain terminates instead of spinning.</summary>
    [Fact]
    public void ACyclicChain_TerminatesInsteadOfSpinning()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        // An exception chain is built from arbitrary references and nothing forbids a cycle. This is not a
        // hypothetical guard: the classifier runs while a failure response is being composed, so a spin here
        // costs the caller the response as well as the request.
        Exception deepest = new("innermost");
        Exception chain = deepest;
        for (int depth = 0; depth < 200; depth++)
        {
            chain = new InvalidOperationException($"link {depth}", chain);
        }

        Func<bool> classify = () => classifier.IsStoreUnavailable(chain);

        classify.Should().NotThrow("a deep chain is a shape to survive, not an error to report");
        classify().Should().BeFalse("nothing in the chain is a store fault");
    }

    /// <summary>Failures that must never be read as store unavailability.</summary>
    /// <returns>One failure per row.</returns>
    public static TheoryData<Exception> NonStoreFailures() =>
        new()
        {
            new InvalidOperationException("a defect in this application"),
            new ArgumentOutOfRangeException("paramName"),
            new NullReferenceException("a defect"),
            new UnauthorizedAccessException("a permission refusal"),
            new NotSupportedException("an unsupported operation"),
            new OperationCanceledException("the caller went away"),
        };

    /// <summary>
    /// Builds a <see cref="SqlException"/> carrying a chosen severity and error numbers.
    /// </summary>
    /// <param name="severity">The severity class every fabricated error carries.</param>
    /// <param name="errors">The error numbers and messages to attach.</param>
    /// <returns>The fabricated exception.</returns>
    /// <remarks>
    /// <para>
    /// A <see cref="SqlException"/> cannot be constructed by a consumer - every constructor and factory is
    /// non-public - so reflection over the pinned client is the only way to present the classifier with a
    /// chosen severity. The cost is accepted knowingly, because the alternative is to leave the boundary
    /// between "the connection is gone" and "the store refused this" untested, and that boundary is the whole
    /// rule. Each lookup fails with a message naming the member it could not find, so a client that changes
    /// shape reads as "the fabrication needs updating" rather than as a mysterious null.
    /// </para>
    /// <para>
    /// The severity is a parameter here and a constant in the sibling duplicate-key fabrication, because the
    /// two decisions rest on different facts: that one is decided by the error number alone, this one is
    /// decided primarily by severity.
    /// </para>
    /// </remarks>
    private static SqlException Fabricate(byte severity, params (int Number, string Message)[] errors)
    {
        ConstructorInfo? collectionConstructor = typeof(SqlErrorCollection)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [], null);

        collectionConstructor.Should().NotBeNull(
            "the pinned client must still expose a parameterless SqlErrorCollection constructor; if it does "
            + "not, this fabrication needs updating for the new client shape");

        object collection = collectionConstructor!.Invoke([]);

        MethodInfo? add = typeof(SqlErrorCollection).GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(SqlError)],
            null);

        add.Should().NotBeNull("SqlErrorCollection.Add(SqlError) is how errors are attached");

        ConstructorInfo? errorConstructor = typeof(SqlError).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [
                typeof(int),
                typeof(byte),
                typeof(byte),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(int),
                typeof(Exception),
            ],
            null);

        errorConstructor.Should().NotBeNull("SqlError's eight-argument constructor is how one is built");

        foreach ((int number, string message) in errors)
        {
            object error = errorConstructor!.Invoke(
                [number, (byte)0, severity, "fabricated", message, string.Empty, 0, null]);

            add!.Invoke(collection, [error]);
        }

        MethodInfo? create = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(SqlErrorCollection), typeof(string)],
            null);

        create.Should().NotBeNull(
            "SqlException.CreateException(SqlErrorCollection, string) is the only way to build one");

        object? built = create!.Invoke(null, [collection, "16.00.4215"]);

        built.Should().BeOfType<SqlException>();

        return (SqlException)built!;
    }
}
