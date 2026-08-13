namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly typed configuration governing how long the Application layer keeps cached data, bound from the
/// configuration section named by <see cref="CachingOptions.SectionName"/>.
/// </summary>
/// <remarks>
/// The one exception to "no behaviour" is <see cref="Validate"/>, which reports this object's own
/// invariants and reaches nothing outside the base class library.
/// </remarks>
public sealed class CachingOptions
{
    /// <summary>Name of the configuration section this class binds from.</summary>
    public const string SectionName = "Caching";

    /// <summary>Smallest acceptable <see cref="PerformanceMultiplier"/> value: <c>0</c>.</summary>
    /// <remarks>
    /// Zero rather than one, and deliberately so: zero is a legitimate, legacy-sanctioned setting that
    /// disables caching, as the guidance on <see cref="PerformanceMultiplier"/> records.
    /// </remarks>
    public const int MinimumPerformanceMultiplier = 0;

    /// <summary>Largest acceptable <see cref="PerformanceMultiplier"/> value: 1440.</summary>
    /// <remarks>
    /// Read it as "at most one day of extra lifetime for every minute of base lifetime". That is already
    /// two hundred and forty times the largest value the legacy enumeration offered, so it constrains
    /// nothing a deployment would plausibly choose, while making it impossible for the product of a base
    /// lifetime and this multiplier to overflow the interval type the callers build from it.
    /// </remarks>
    public const int MaximumPerformanceMultiplier = 1440;

    /// <summary>
    /// Multiplier applied to a per-entity base cache lifetime, in minutes, to produce the effective cache
    /// expiry. Replaces the excluded static property <c>DotNetNuke.Common.Globals.PerformanceSetting</c>,
    /// which in-scope legacy code reaches from fifteen distinct call sites.
    /// </summary>
    /// <value>Defaults to <c>3</c>.</value>
    /// <remarks>
    /// The single boundary is that the value may not be NEGATIVE, and <see cref="Validate"/> is the one
    /// place that boundary is stated. A negative multiplier yields a negative product, which every guarded
    /// legacy site (<c>If timeOut &gt; 0</c>) could only ever have interpreted as "no caching", while an
    /// unguarded target site would hand a negative duration to the cache and fail mid-request.
    /// </remarks>
    public int PerformanceMultiplier { get; set; } = 3;

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a misconfigured
    /// deployment fails while the host is starting rather than midway through a request.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or an empty
    /// collection when the instance is usable.
    /// </returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> failures = [];

        if (PerformanceMultiplier < 0)
        {
            // Every number reaches the message through an invariant conversion first, so the
            // concatenation below interpolates strings only and cannot pick up a culture.
            string configured = FormattableString.Invariant($"{PerformanceMultiplier}");

            failures.Add(
                $"{SectionName}:{nameof(PerformanceMultiplier)} is {configured}, which is "
                + "negative. A negative multiplier produces a negative cache lifetime, which no "
                + "legacy configuration could produce and which the cache cannot honour. Use 0 to "
                + "disable caching.");
        }

        return failures;
    }
}
