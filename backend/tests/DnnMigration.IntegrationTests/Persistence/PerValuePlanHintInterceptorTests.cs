using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DnnMigration.Infrastructure.Persistence;
using DnnMigration.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>Covers the query hint that keeps a value-sensitive search costing the same amount every time.</summary>
/// <remarks>
/// <para>
/// The mechanism under test has two halves that must agree, and a disagreement between them fails SILENTLY -
/// the hint simply never appears, the search keeps whichever cached plan it was given, and nothing reports a
/// problem until a broad search runs on a plan compiled for a selective one. A repository tags the query; the
/// interceptor recognises the rendered tag and appends the hint. These assertions pin both halves and the
/// contract between them.
/// </para>
/// <para>
/// The end-to-end assertions run a real query against the test database rather than inspecting a string,
/// because the thing most worth proving is that the rewritten statement is still valid T-SQL: the hint has to
/// land after the paging clause Entity Framework emits last, and a rewrite that put it anywhere else would
/// turn every filtered search into a syntax error.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PerValuePlanHintInterceptorTests
{
    /// <summary>A prefix chosen to match nothing, so the assertions never depend on seeded rows.</summary>
    private const string UnmatchablePrefix = "no-account-carries-this-prefix";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PerValuePlanHintInterceptorTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public PerValuePlanHintInterceptorTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The tag a repository attaches renders as the comment the interceptor matches.</summary>
    /// <remarks>
    /// The one assertion that catches the silent failure mode. Were the two constants to drift apart, every
    /// other assertion here would still pass on its own terms while the hint stopped being applied in
    /// production.
    /// </remarks>
    [Fact]
    public void TheRenderedTag_IsTheCommentTheInterceptorMatches()
    {
        QueryTags.PerValuePlanComment.Should().Be("-- " + QueryTags.PerValuePlan);
    }

    /// <summary>A tagged query reaches the provider with the hint appended, and the provider accepts it.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Paging and ordering are applied so that the statement carries the <c>OFFSET</c> and <c>FETCH NEXT</c>
    /// clauses the real listing carries, which is where a misplaced hint would break.
    /// </remarks>
    [Fact]
    public async Task ATaggedQuery_ReachesTheProviderHintedAndExecutes()
    {
        CapturingCommandInterceptor capture = new CapturingCommandInterceptor();

        await using DnnDbContext context = CreateContext(capture);

        // Both paging arguments are held in locals so that Entity Framework parameterises them and emits the
        // OFFSET and FETCH NEXT clauses a real listing carries, which is where a misplaced hint would break.
        int skip = 0;
        int take = 20;

        List<string> matches = await context.Users
            .AsNoTracking()
            .Where(u => u.Username.StartsWith(UnmatchablePrefix))
            .TagWith(QueryTags.PerValuePlan)
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.UserId)
            .Skip(skip)
            .Take(take)
            .Select(u => u.Username)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(true);

        matches.Should().BeEmpty("the prefix was chosen to match no account, so only the statement matters");

        capture.LastCommandText.Should().NotBeNull("the query has to have reached the provider");
        string sql = capture.LastCommandText!;

        sql.Should().Contain(QueryTags.PerValuePlanComment, "the repository's tag must survive to the provider");
        sql.Should().Contain("FETCH NEXT", "the paging clause is what the hint has to follow");
        sql.TrimEnd().Should().EndWith(PerValuePlanHintInterceptor.Hint);
        CountOccurrences(sql, PerValuePlanHintInterceptor.Hint).Should().Be(1, "the hint is appended once");
    }

    /// <summary>An untagged query reaches the provider exactly as Entity Framework wrote it.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The other half of the narrow scope: an unfiltered listing keeps its cached plan, so it must not be
    /// handed a hint that would discard the plan on every execution.
    /// </remarks>
    [Fact]
    public async Task AnUntaggedQuery_ReachesTheProviderUnchanged()
    {
        CapturingCommandInterceptor capture = new CapturingCommandInterceptor();

        await using DnnDbContext context = CreateContext(capture);

        _ = await context.Users
            .AsNoTracking()
            .OrderBy(u => u.UserId)
            .Take(1)
            .Select(u => u.UserId)
            .ToListAsync(CancellationToken.None)
            .ConfigureAwait(true);

        capture.LastCommandText.Should().NotBeNull("the query has to have reached the provider");
        string sql = capture.LastCommandText!;

        sql.Should().NotContain(QueryTags.PerValuePlan);
        sql.Should().NotContain(PerValuePlanHintInterceptor.Hint);
    }

    /// <summary>A statement that already carries the hint is left exactly as it is.</summary>
    /// <remarks>
    /// Guards against a second interceptor pass, or a hand-written statement, accumulating the hint.
    /// </remarks>
    [Fact]
    public void AStatementAlreadyCarryingTheHint_IsNotRewritten()
    {
        using SqlConnection connection = new SqlConnection(_fixture.Database.ConnectionString);
        using SqlCommand command = connection.CreateCommand();

        command.CommandText = QueryTags.PerValuePlanComment
            + Environment.NewLine
            + "SELECT 1"
            + Environment.NewLine
            + PerValuePlanHintInterceptor.Hint;

        string before = command.CommandText;

        PerValuePlanHintInterceptor.ApplyHint(command).Should().BeFalse();
        command.CommandText.Should().Be(before);
    }

    /// <summary>A trailing terminator is removed so that the appended hint remains valid T-SQL.</summary>
    /// <remarks>
    /// <c>OPTION</c> after a statement terminator is a syntax error rather than a hint, so this is the
    /// difference between a search that runs and a search that returns a server error.
    /// </remarks>
    [Fact]
    public void ATrailingTerminator_IsRemovedBeforeTheHintIsAppended()
    {
        using SqlConnection connection = new SqlConnection(_fixture.Database.ConnectionString);
        using SqlCommand command = connection.CreateCommand();

        command.CommandText = QueryTags.PerValuePlanComment + Environment.NewLine + "SELECT 1;" + Environment.NewLine;

        PerValuePlanHintInterceptor.ApplyHint(command).Should().BeTrue();

        command.CommandText.Should().Be(
            QueryTags.PerValuePlanComment
            + Environment.NewLine
            + "SELECT 1"
            + Environment.NewLine
            + PerValuePlanHintInterceptor.Hint);
    }

    /// <summary>A command belonging to another provider is passed through untouched.</summary>
    /// <remarks>
    /// <c>OPTION (RECOMPILE)</c> is SQL Server's spelling of this instruction and no other provider parses
    /// it, so the hint is withheld from anything that is not a SQL Server connection even when the tag is
    /// present. The double is a command from a foreign provider; nothing executes it.
    /// </remarks>
    [Fact]
    public void ACommandFromAnotherProvider_IsPassedThroughUnchanged()
    {
        using ForeignProviderCommand command = new ForeignProviderCommand
        {
            CommandText = QueryTags.PerValuePlanComment + Environment.NewLine + "SELECT 1",
        };

        string before = command.CommandText;

        PerValuePlanHintInterceptor.ApplyHint(command).Should().BeFalse();
        command.CommandText.Should().Be(before);
    }

    /// <summary>Counts non-overlapping occurrences of a value in a string.</summary>
    /// <param name="text">The text to search.</param>
    /// <param name="value">The value to count.</param>
    /// <returns>The number of occurrences.</returns>
    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.OrdinalIgnoreCase);
        }

        return count;
    }

    /// <summary>Builds a context against the test database with the hint interceptor ahead of a capture.</summary>
    /// <param name="capture">The interceptor that records what the provider was asked to run.</param>
    /// <returns>The context.</returns>
    /// <remarks>
    /// ORDER IS THE POINT OF THIS METHOD. Entity Framework invokes interceptors in registration order, so the
    /// hint interceptor is registered first and the capture second - which is what makes the captured text
    /// the text the provider actually receives rather than the text before the rewrite.
    /// </remarks>
    private DnnDbContext CreateContext(CapturingCommandInterceptor capture)
    {
        DbContextOptions<DnnDbContext> options = new DbContextOptionsBuilder<DnnDbContext>()
            .UseSqlServer(_fixture.Database.ConnectionString)
            .AddInterceptors(PerValuePlanHintInterceptor.Instance, capture)
            .Options;

        return new DnnDbContext(options);
    }

    /// <summary>Records the command text of the last reader the context executed.</summary>
    private sealed class CapturingCommandInterceptor : DbCommandInterceptor
    {
        /// <summary>The command text seen most recently, or <see langword="null"/> before the first read.</summary>
        internal string? LastCommandText { get; private set; }

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            LastCommandText = command.CommandText;
            return base.ReaderExecuting(command, eventData, result);
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            LastCommandText = command.CommandText;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>A command that belongs to no SQL Server connection, standing in for another provider.</summary>
    /// <remarks>
    /// Only the members the interceptor reads are meaningful. The execution members are unreachable from this
    /// test - the interceptor inspects and rewrites text and never runs anything - and they say so rather
    /// than pretending to succeed, so a future test that tried to execute this double would fail loudly
    /// instead of silently asserting against a fabricated result.
    /// </remarks>
    private sealed class ForeignProviderCommand : DbCommand
    {
        private string _commandText = string.Empty;

        /// <inheritdoc />
        /// <remarks>
        /// The base declaration allows null on the way in and forbids it on the way out - the annotation and
        /// the coalesce here reproduce that contract rather than narrowing it, which is what a provider's own
        /// command type does.
        /// </remarks>
        [AllowNull]
        public override string CommandText
        {
            get => _commandText;
            set => _commandText = value ?? string.Empty;
        }

        /// <inheritdoc />
        public override int CommandTimeout { get; set; }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; } = CommandType.Text;

        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection { get; } = new ForeignProviderParameterCollection();

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        public override void Cancel() =>
            throw new NotSupportedException("This double is never executed.");

        /// <inheritdoc />
        public override int ExecuteNonQuery() =>
            throw new NotSupportedException("This double is never executed.");

        /// <inheritdoc />
        public override object? ExecuteScalar() =>
            throw new NotSupportedException("This double is never executed.");

        /// <inheritdoc />
        public override void Prepare() =>
            throw new NotSupportedException("This double is never executed.");

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() =>
            throw new NotSupportedException("This double takes no parameters.");

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException("This double is never executed.");
    }

    /// <summary>An empty parameter collection for <see cref="ForeignProviderCommand"/>.</summary>
    private sealed class ForeignProviderParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;

        /// <inheritdoc />
        public override object SyncRoot { get; } = new object();

        /// <inheritdoc />
        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        /// <inheritdoc />
        public override void AddRange(Array values)
        {
            foreach (object value in values)
            {
                Add(value);
            }
        }

        /// <inheritdoc />
        public override void Clear() => _items.Clear();

        /// <inheritdoc />
        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        /// <inheritdoc />
        public override bool Contains(string value) => IndexOf(value) >= 0;

        /// <inheritdoc />
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);

        /// <inheritdoc />
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();

        /// <inheritdoc />
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        /// <inheritdoc />
        public override int IndexOf(string parameterName) =>
            _items.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));

        /// <inheritdoc />
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        /// <inheritdoc />
        public override void Remove(object value) => _items.Remove((DbParameter)value);

        /// <inheritdoc />
        public override void RemoveAt(int index) => _items.RemoveAt(index);

        /// <inheritdoc />
        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

        /// <inheritdoc />
        protected override DbParameter GetParameter(int index) => _items[index];

        /// <inheritdoc />
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        /// <inheritdoc />
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        /// <inheritdoc />
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }
}
