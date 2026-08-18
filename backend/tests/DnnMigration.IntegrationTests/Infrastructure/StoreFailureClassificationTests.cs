using System.Net.Sockets;
using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Covers the rule that decides whether a failure means the store could not serve - which the transport
/// answers 503 with a retry hint - or means this application has a defect, which stays 500.
/// </summary>
[Trait("Category", "Integration")]
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

    /// <summary>
    /// A transport fault reached THROUGH the database client or the mapper is an availability failure.
    /// </summary>
    /// <param name="failure">A chain whose outer link places a transport fault on the path to the store.</param>
    [Theory]
    [MemberData(nameof(TransportFailuresInStoreContext))]
    public void ATransportFaultOnThePathToTheStore_IsReadAsUnavailability(Exception failure)
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        classifier.IsStoreUnavailable(failure).Should().BeTrue(
            "the store need not be at fault for it to be unreachable - a refused connection, a reset and an "
            + "exhausted connection pool all arrive as a transport fault beneath a client or mapper wrapper - "
            + "and that wrapper is what says the transport in question was the one to the store");
    }

    /// <summary>A transport fault with NO data-access link anywhere in its chain keeps the answer it had.</summary>
    /// <param name="failure">A transport or timing fault raised by something other than the store.</param>
    /// <remarks>
    /// THIS IS THE FACT THAT WAS INVERTED, and it is the reason the two arms above stopped being
    /// unconditional. <see cref="SocketException"/> and <see cref="TimeoutException"/> are raised by every
    /// outbound socket and every waited-upon asynchronous primitive in the process, not only by the
    /// database client: an in-process cache whose single-flight budget expired, a <see
    /// cref="System.Threading.SemaphoreSlim"/> wait that timed out, an <see
    /// cref="System.Net.Http.HttpClient"/> call to some other dependency.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TransportFailuresOutsideStoreContext))]
    public void ATransportFaultRaisedOutsideTheStorePath_IsNotReadAsUnavailability(Exception failure)
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        classifier.IsStoreUnavailable(failure).Should().BeFalse(
            "a socket and a timeout are raised all over a process, so the bare type cannot mean the database "
            + "is down; without a provider or mapper link in the chain there is nothing to say WHICH "
            + "dependency failed, and guessing the database sends a retry hint to a caller whose retry "
            + "cannot succeed");
    }

    /// <summary>
    /// A sibling branch of a parallel failure cannot lend its store context to an unrelated transport
    /// fault.
    /// </summary>
    [Fact]
    public void ASiblingBranchDoesNotLendItsStoreContextToATransportFault()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        // Left branch: an in-process wait that expired, with no data-access link of any kind. Right branch:
        // a mapper wrapper around an ordinary constraint-shaped refusal, which is a data-access link but is
        // not itself an outage.
        AggregateException parallel = new(
            new TimeoutException("a single-flight cache budget expired"),
            new DbUpdateException(
                "An error occurred while saving the entity changes.",
                Fabricate(RefusalSeverity, (Number: 2627, Message: "Violation of PRIMARY KEY constraint"))));

        classifier.IsStoreUnavailable(parallel).Should().BeFalse(
            "the data-access context of one branch says nothing about a transport fault raised in another, "
            + "and letting it carry across would classify a cache defect as a database outage whenever the "
            + "two happened to fail in the same parallel operation");
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

        classifier.IsStoreUnavailable(null).Should().BeFalse(
            "an error path must never be handed a second failure while handling the first");
    }

    /// <summary>A pathologically deep or cyclic chain terminates instead of spinning.</summary>
    [Fact]
    public void ACyclicChain_TerminatesInsteadOfSpinning()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

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

    /// <summary>
    /// Transport and timing faults whose chain positively identifies the store path, and which are
    /// therefore availability conditions.
    /// </summary>
    /// <returns>One chain per row.</returns>
    public static TheoryData<Exception> TransportFailuresInStoreContext() =>
        new()
        {
            new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new SocketException((int)SocketError.ConnectionRefused)),
            new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new SocketException((int)SocketError.ConnectionReset)),
            new RetryLimitExceededException(
                "The maximum number of retries was exceeded.",
                new TimeoutException("The connection pool timed out.")),

            // Three deep, and the admitting link is neither the outermost nor the innermost. The context is
            // carried DOWNWARDS from wherever it is found, so a wrapper above it is no obstacle.
            new InvalidOperationException(
                "The operation could not be completed.",
                new RetryLimitExceededException(
                    "The maximum number of retries was exceeded.",
                    new SocketException((int)SocketError.HostNotFound))),
        };

    /// <summary>
    /// Transport and timing faults with no data-access link anywhere in the chain, which must keep the
    /// answer they had.
    /// </summary>
    /// <returns>One failure per row.</returns>
    public static TheoryData<Exception> TransportFailuresOutsideStoreContext() =>
        new()
        {
            new SocketException((int)SocketError.ConnectionRefused),
            new TimeoutException("The connection pool timed out."),

            // What the in-process cache raises when its single-flight budget expires. Before the narrowing,
            // this alone answered a caller 503 with a retry hint naming the database.
            new CacheProductionTimeoutException(
                "A cached value took longer than the shared production budget to produce."),

            // A wrapper that is not a data-access type, around a fault that is not one either.
            new InvalidOperationException(
                "The operation could not be completed.",
                new TimeoutException("an unrelated dependency did not answer")),
        };

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

    // ---- Caller cancellation, added for M12 -----------------------------------------------------------
    //
    // The condition these cover is the one the QA run caught: a request the caller abandoned surfaced as a
    // provider exception, was not recognised as an abandonment, and was logged and answered as an unhandled
    // 500. Every case below is about IsCallerCancellation, which is the test the exception handler consults
    // before it faults anything.

    /// <summary>The ordinary abandonment shape is recognised.</summary>
    /// <remarks>
    /// <see cref="TaskCanceledException"/> is included because it derives from
    /// <see cref="OperationCanceledException"/> and is what an awaited provider call actually throws - if the
    /// rule tested the base type by name rather than by assignability, this case is where it would show.
    /// </remarks>
    [Fact]
    public void AnAbandonedOperation_IsRecognisedAsCallerCancellation()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        classifier.IsCallerCancellation(new OperationCanceledException("the caller went away"))
            .Should().BeTrue("this is the shape a cancelled token produces directly");

        classifier.IsCallerCancellation(new TaskCanceledException("the awaited call was abandoned"))
            .Should().BeTrue("TaskCanceledException derives from OperationCanceledException");
    }

    /// <summary>A cancellation wrapped by the provider or by the task machinery is still found.</summary>
    [Fact]
    public void ACancellationBeneathAWrapper_IsStillFound()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        var wrapped = new DbUpdateException(
            "An error occurred while saving",
            new OperationCanceledException("the caller went away"));

        classifier.IsCallerCancellation(wrapped).Should().BeTrue(
            "the walk follows inner exceptions, because the provider wraps what it catches");

        var aggregated = new AggregateException(
            new InvalidOperationException("an unrelated link"),
            new OperationCanceledException("the caller went away"));

        classifier.IsCallerCancellation(aggregated).Should().BeTrue(
            "an aggregate is flattened and every branch examined, not just the first");
    }

    /// <summary>
    /// The provider's own client-raised error, carrying no server error, is recognised as an abandonment.
    /// </summary>
    /// <remarks>
    /// This is the arm that matters for the finding: a command torn down mid-flight can surface as a
    /// <see cref="SqlException"/> rather than an <see cref="OperationCanceledException"/>, and that shape is
    /// exactly what went out as a 500. Note the collection carries ONE error numbered 0 - the real shape -
    /// rather than none, which is what an earlier spelling of this rule incorrectly required.
    /// </remarks>
    [Fact]
    public void AClientRaisedErrorWithNoServerError_IsRecognisedAsCallerCancellation()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException cancelled = Fabricate(
            RefusalSeverity,
            (Number: 0, Message: "Operation cancelled by user."));

        classifier.IsCallerCancellation(cancelled).Should().BeTrue(
            "number 0 with no server error accompanying it is the client tearing its own command down");
    }

    /// <summary>An aborted batch is recognised as an abandonment.</summary>
    [Fact]
    public void AnAbortedBatch_IsRecognisedAsCallerCancellation()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException aborted = Fabricate(
            RefusalSeverity,
            (Number: 3980, Message: "The request failed to run because the batch is aborted"));

        classifier.IsCallerCancellation(aborted).Should().BeTrue(
            "a batch abandoned mid-flight is the caller's doing, not the store failing to serve");
    }

    /// <summary>
    /// ⚠ A COMMAND TIMEOUT IS NOT A CANCELLATION, and this is the most important case in the group.
    /// </summary>
    /// <remarks>
    /// The two look alike - both end a statement early, and both can arrive numbered -2 - but they differ in
    /// the only way that matters here: the caller who timed out is STILL WAITING and is owed an answer, so
    /// classifying it as an abandonment would swallow a real outage and answer nothing at all. The exclusion is
    /// tested both as the exception's own number and as an error inside the collection, because the rule has to
    /// hold whichever way the provider reports it.
    /// </remarks>
    [Fact]
    public void ACommandTimeout_IsNotTreatedAsCallerCancellation()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException timedOut = Fabricate(
            RefusalSeverity,
            (Number: -2, Message: "Execution Timeout Expired."));

        classifier.IsCallerCancellation(timedOut).Should().BeFalse(
            "the caller is still waiting for an answer, so this must not be recorded as an abandonment");

        SqlException timedOutBehindAnotherError = Fabricate(
            RefusalSeverity,
            (Number: 0, Message: "a client-raised entry"),
            (Number: -2, Message: "Execution Timeout Expired."));

        classifier.IsCallerCancellation(timedOutBehindAnotherError).Should().BeFalse(
            "the timeout is excluded wherever in the collection it appears, not only when it is first");
    }

    /// <summary>A genuine store failure is not reclassified as an abandonment.</summary>
    /// <remarks>
    /// The guarantee this protects is that the new test took nothing away from the existing one: an outage must
    /// still be an outage, so it is asserted here to be a cancellation of nothing AND an availability failure.
    /// </remarks>
    [Fact]
    public void AGenuineOutage_IsNotReclassifiedAsCallerCancellation()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException outage = Fabricate(
            FatalSeverity,
            (Number: 10054, Message: "network-related or instance-specific error"));

        classifier.IsCallerCancellation(outage).Should().BeFalse(
            "a fatal severity is a connection condition, which the availability test owns");

        classifier.IsStoreUnavailable(outage).Should().BeTrue(
            "and it must still be answered 503 exactly as before this rule was added");
    }

    /// <summary>An ordinary refusal by the store is neither a cancellation nor an outage.</summary>
    [Fact]
    public void AnOrdinaryStoreRefusal_IsNeitherCancellationNorOutage()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        SqlException refusal = Fabricate(
            RefusalSeverity,
            (Number: 2627, Message: "Violation of PRIMARY KEY constraint"));

        classifier.IsCallerCancellation(refusal).Should().BeFalse(
            "a constraint violation names a server error, so it is not the client tearing a command down");

        classifier.IsStoreUnavailable(refusal).Should().BeFalse(
            "and a refusal the store issued deliberately is not the store being unavailable");
    }

    /// <summary>Nothing at all is not a cancellation.</summary>
    [Fact]
    public void NoException_IsNotACancellation()
    {
        IStoreFailureClassifier classifier = new SqlStoreFailureClassifier();

        classifier.IsCallerCancellation(null).Should().BeFalse(
            "the member is total, so the absent case answers rather than throwing");
    }

    /// <summary>Builds a <see cref="SqlException"/> carrying a chosen severity and error numbers.</summary>
    /// <param name="severity">The severity class every fabricated error carries.</param>
    /// <param name="errors">The error numbers and messages to attach.</param>
    /// <returns>The fabricated exception.</returns>
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
