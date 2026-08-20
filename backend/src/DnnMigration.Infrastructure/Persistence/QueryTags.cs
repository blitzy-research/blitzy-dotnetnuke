namespace DnnMigration.Infrastructure.Persistence;

// WHY A TAG RATHER THAN A HINT AT THE CALL SITE: EF Core 8 has no first-class query-hint API, so the
// documented way to attach a provider hint to ONE composed LINQ query - rather than to every statement the
// context emits - is to tag the query and act on the tag as the command leaves for the provider. The tag is
// the contract between the repository that knows the predicate is value-sensitive and the interceptor that
// knows how to say so in T-SQL; nothing else in the assembly may append hints.

/// <summary>The EF Core query tags this assembly attaches to queries, and recognises on the way out.</summary>
/// <remarks>
/// A tag reaches the provider as a SQL line comment ahead of the statement, so it is inert to the database
/// and observable to an interceptor. Both forms are declared here because the repository tags with the bare
/// value while the interceptor matches the rendered comment, and a mismatch between the two would fail
/// silently - the hint would simply never be applied, restoring the pathology it exists to prevent.
/// </remarks>
internal static class QueryTags
{
    /// <summary>
    /// Marks a query whose cardinality depends so strongly on its parameter values that a plan compiled for
    /// one value is unsafe for another, and which must therefore be planned per execution.
    /// </summary>
    internal const string PerValuePlan = "dnn:per-value-plan";

    /// <summary>The rendered form of <see cref="PerValuePlan"/> as it appears in a generated statement.</summary>
    internal const string PerValuePlanComment = "-- " + PerValuePlan;
}
