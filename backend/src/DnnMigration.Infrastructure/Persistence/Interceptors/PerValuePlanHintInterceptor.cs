using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DnnMigration.Infrastructure.Persistence.Interceptors;

// MIGRATION: THIS TYPE EXISTS BECAUSE A SEARCH PREDICATE'S COST MUST NOT DEPEND ON WHO SEARCHED FIRST.
// A member search filters an unindexed-for-this-purpose table by a text prefix whose selectivity spans four
// orders of magnitude - two matching accounts for one term, ten thousand for another - and SQL Server caches
// ONE plan per statement, compiled for whichever value happened to arrive first. Measured on a ten-thousand
// account catalogue, the byte-identical parameterised statement cost 40,048 logical reads when the cached
// plan had been compiled for a selective prefix and 219 when it had been compiled for a broad one: the same
// request, the same data, a 183-fold difference decided by plan-cache state alone. The captured plan named
// its own cause - ParameterCompiledValue "perf%" against ParameterRuntimeValue "u%", an index seek feeding
// ten thousand key lookups and ten thousand per-row membership probes that the compiled estimate of one row
// had made look free.
//
// Removing the redundant case fold from those predicates - which this repository also did, and which made
// the index seekable - does not address this by itself, and measurement says so plainly: it made the seek
// available, which if anything sharpens the optimiser's dependence on the sniffed value rather than dulling
// it. The 183-fold swing above was measured AFTER the fold came out.
//
// Compiling per execution is the remedy the shape actually calls for, and it is narrowly applied: only the
// queries a repository has tagged - those carrying a caller-supplied text filter - opt in. An unfiltered
// listing keeps its cached plan, because its cost does not vary with its parameters and there is nothing to
// protect it from.

/// <summary>
/// Appends the SQL Server per-execution compilation hint to the statements EF Core generates for queries
/// tagged <see cref="QueryTags.PerValuePlan"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stateless by construction.</strong> The interceptor holds no field, so the single shared
/// <see cref="Instance"/> is safe for every context in the process, including concurrent ones. It reads and
/// rewrites only the command it is handed.
/// </para>
/// <para>
/// <strong>Three conditions, all required.</strong> The hint is appended only when the tag is present, when
/// the statement does not already carry it, and when the connection is a SQL Server connection - the last so
/// that a provider which does not understand <c>OPTION</c> is never handed it. A command failing any
/// condition is passed through byte-for-byte.
/// </para>
/// <para>
/// <strong>Mutating the command text is safe here.</strong> EF Core assigns
/// <see cref="DbCommand.CommandText"/> from its own cached SQL each time it materialises a command, so a
/// rewrite applies to one execution and cannot accumulate across executions or leak into the query cache.
/// </para>
/// </remarks>
internal sealed class PerValuePlanHintInterceptor : DbCommandInterceptor
{
    /// <summary>The T-SQL query hint that plans the statement for the values of this execution.</summary>
    internal const string Hint = "OPTION (RECOMPILE)";

    /// <summary>Initialises the shared instance.</summary>
    private PerValuePlanHintInterceptor()
    {
    }

    /// <summary>The single shared, stateless instance registered with the context options.</summary>
    internal static PerValuePlanHintInterceptor Instance { get; } = new PerValuePlanHintInterceptor();

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ApplyHint(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ApplyHint(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ApplyHint(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ApplyHint(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>Appends the hint to a tagged SQL Server statement that does not already carry it.</summary>
    /// <param name="command">The command about to be executed.</param>
    /// <returns><see langword="true"/> when the command text was rewritten; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// The hint belongs at the very end of the statement, after the paging clause EF Core emits last, which
    /// is why any trailing whitespace or terminator is removed before it is appended: <c>OPTION</c> after a
    /// statement terminator is a syntax error rather than a hint, and a caller would see the search fail
    /// outright instead of running with a stale plan.
    /// </remarks>
    internal static bool ApplyHint(DbCommand command)
    {
        string text = command.CommandText;

        if (string.IsNullOrEmpty(text)
            || !text.Contains(QueryTags.PerValuePlanComment, StringComparison.Ordinal)
            || text.Contains(Hint, StringComparison.OrdinalIgnoreCase)
            || command.Connection is not SqlConnection)
        {
            return false;
        }

        string statement = text.TrimEnd().TrimEnd(';').TrimEnd();

        if (statement.Length == 0)
        {
            return false;
        }

        command.CommandText = statement + Environment.NewLine + Hint;
        return true;
    }
}
