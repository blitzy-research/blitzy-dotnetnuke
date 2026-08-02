// MIGRATION: Replaces the static, ambiently reached property
// DotNetNuke.Common.Globals.PerformanceSetting (Library/Components/Shared/Globals.vb:L227-L231)
// with strongly typed configuration bound from the "Caching" section. The whole
// DotNetNuke.Common.Globals module is excluded from this migration; only the handful of its
// members that in-scope legacy code actually reaches are reimplemented, and the cache
// performance multiplier is one of them.
namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly typed configuration governing how long the Application layer keeps cached data,
/// bound from the configuration section named by <see cref="CachingOptions.SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is a deliberately minimal, dependency-free plain object: it declares primitives
/// only and carries no attributes. The Application project references the
/// Domain project and nothing else, so nothing declared here may reach the configuration,
/// hosting or dependency-injection abstractions that perform the binding. Those live in the Api
/// layer, which owns the binding call; the Application layer's own service-registration entry
/// point deliberately accepts no configuration object and therefore cannot bind this class.
/// Declaring the shape here and binding it there is the arrangement the migration plan mandates.
/// </para>
/// <para>
/// The one exception to "no behaviour" is <see cref="Validate"/>, which reports this object's own
/// invariants and reaches nothing outside the base class library. It is declared here rather than
/// at either consumer because there are TWO consumers -- the Api's start-up options validation and
/// the Infrastructure cache service -- and an earlier arrangement in which each stated its own rule
/// let the two disagree: the documentation on the multiplier below described any integer as
/// legitimate while the cache service rejected everything outside the legacy four. One rule,
/// declared beside the value it governs and consumed by both, is what prevents that recurring.
/// </para>
/// <para>
/// Scope note - legacy cache keys and per-entity base lifetimes are deliberately absent.
/// Library/Components/Providers/Caching/DataCache.vb:L42-L79 declares thirteen cache keys and
/// twelve paired base lifetimes (every one of them 20 minutes, except the user entry at 1, and
/// the tab-path key has no paired lifetime at all). Those values belong beside the concrete
/// cache implementation in the Infrastructure layer, not in Application configuration, so this
/// class carries none of them.
/// </para>
/// <para>
/// Scope note - the legacy EnableCachePersistence switch is deliberately excluded too. It is
/// declared at Website/release.config:L46 and read by DataCache.CachePersistenceEnabled at
/// Library/Components/Providers/Caching/DataCache.vb:L94 to decide whether cache entries are
/// also written to a persistent store. That is a concern of the concrete cache implementation
/// in the Infrastructure layer and is read by no Application service, so surfacing it here
/// would place a setting in front of code that cannot act on it. The omission is a recorded
/// decision, not an oversight.
/// </para>
/// </remarks>
public sealed class CachingOptions
{
    /// <summary>
    /// Name of the configuration section this class binds from.
    /// </summary>
    /// <remarks>
    /// The Api layer resolves this class from the section this constant names, so it binds
    /// against a shared constant rather than a repeated literal. Because the configuration
    /// providers translate a double underscore into a section separator, the container and
    /// process-environment override for the multiplier below is
    /// <c>Caching__PerformanceMultiplier</c>, and the equivalent settings-file path is
    /// <c>Caching:PerformanceMultiplier</c>.
    /// </remarks>
    public const string SectionName = "Caching";

    // ------------------------------------------------------------------------
    // Bounds on the multiplier below, held here beside the setting and const
    // rather than configurable. Enforcement lives in
    // Api/Extensions/ServiceCollectionExtensions.cs and runs at startup.
    // ------------------------------------------------------------------------

    /// <summary>
    /// Smallest acceptable <see cref="PerformanceMultiplier"/> value: <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Zero rather than one, and deliberately so: zero is a legitimate,
    /// legacy-sanctioned setting that disables caching, as the guidance on
    /// <see cref="PerformanceMultiplier"/> records. What zero must not become is
    /// negative - a negative multiplier yields a negative expiry, which no cache
    /// entry can carry and which several measured legacy call sites would read as
    /// "caching disabled" only by accident of a greater-than-zero guard.
    /// </remarks>
    public const int MinimumPerformanceMultiplier = 0;

    /// <summary>
    /// Largest acceptable <see cref="PerformanceMultiplier"/> value: 1440.
    /// </summary>
    /// <remarks>
    /// Read it as "at most one day of extra lifetime for every minute of base
    /// lifetime". That is already two hundred and forty times the largest value
    /// the legacy enumeration offered, so it constrains nothing a deployment would
    /// plausibly choose, while making it impossible for the product of a base
    /// lifetime and this multiplier to overflow the interval type the callers
    /// build from it. An unbounded multiplier would turn a mistyped configuration
    /// value into either indefinitely stale data or an arithmetic failure on the
    /// first cache write.
    /// </remarks>
    public const int MaximumPerformanceMultiplier = 1440;

    // MIGRATION: One legacy site uses this value as the WHOLE expiry rather than as a
    // multiplier - Library/Components/Users/UserController.vb:L665. That behaviour is preserved
    // rather than corrected, because the Minimal Change Clause forbids opportunistic
    // optimisation of ported logic; the divergence is recorded in MIGRATION_NOTES.md.
    // MIGRATION: Two legacy sites multiply a hardcoded literal 20 instead of a named base
    // constant - Library/Components/Modules/ModuleController.vb:L1264 and L1355 - so a service
    // author must not assume every site derives its base lifetime from a named constant.

    /// <summary>
    /// Multiplier applied to a per-entity base cache lifetime, in minutes, to produce the
    /// effective cache expiry. Replaces the excluded static property
    /// <c>DotNetNuke.Common.Globals.PerformanceSetting</c>, which in-scope legacy code reaches
    /// from fifteen distinct call sites.
    /// </summary>
    /// <value>
    /// Defaults to <c>3</c>. The legacy <c>PerformanceSettings</c> enumeration at
    /// Library/Components/Shared/Globals.vb:L66-L75 declared exactly four values -
    /// <c>NoCaching = 0</c>, <c>LightCaching = 1</c>, <c>ModerateCaching = 3</c> and
    /// <c>HeavyCaching = 6</c> - chosen, in the words of the source comment, so that the scaling
    /// stays linear for all caching. The default of <c>3</c> is not a preference: it is the
    /// measured implicit legacy default from Library/Components/Shared/Globals.vb:L227-L231,
    /// where an absent <c>PerformanceSetting</c> host-settings row is substituted with
    /// <c>3</c> - that is, <c>ModerateCaching</c>. Any other default would silently change cache
    /// lifetimes across the whole application.
    /// </value>
    /// <remarks>
    /// <para>
    /// Units. The product of a base lifetime and this multiplier is interpreted in MINUTES,
    /// matching every legacy site, each of which converts the product with
    /// <c>TimeSpan.FromMinutes</c>. At the default multiplier a 20-minute base yields 60
    /// minutes, and the 1-minute user base yields 3 minutes.
    /// </para>
    /// <para>
    /// Zero disables caching, and does more than skip the cache write. At
    /// Library/Components/Portal/PortalController.vb:L220-L221 a zero product short-circuits the
    /// database read as well, because the legacy comment there records that the query is too
    /// expensive to run on every request. Two of the measured sites place that guard
    /// differently: PortalController.vb:L1232 computes the same product but reads the database
    /// unconditionally and guards only the cache write at L1245. That divergence is a genuine
    /// legacy inconsistency and is preserved rather than harmonised, so each caller must apply
    /// the guard its own legacy site applied.
    /// </para>
    /// <para>
    /// The caller computes the expiry. This class exposes no computed expiry and no helper that
    /// turns a base lifetime into a duration, because such a helper would both duplicate logic
    /// that legitimately differs between the measured call sites and hide the guard those sites
    /// apply differently. The cache abstraction likewise takes an already-computed duration.
    /// </para>
    /// <para>
    /// Deliberately an integer rather than an enumeration, and the accepted range is genuinely
    /// wider than the legacy four. The legacy reader cast an arbitrary host-settings integer
    /// straight to the enumeration -
    /// <c>CType(Convert.ToInt32(...), PerformanceSettings)</c> at
    /// Library/Components/Shared/Globals.vb:L229 - and a Visual Basic conversion to an
    /// enumeration is unchecked, so a stored <c>4</c> produced a multiplier of <c>4</c> and
    /// scaled every lifetime accordingly. Rejecting such a value would therefore be a
    /// TIGHTENING of a configuration the legacy installation accepted, which the Minimal Change
    /// Clause forbids. An enumeration would either reject it or silently admit an undefined
    /// member, so the primitive is modelled directly.
    /// </para>
    /// <para>
    /// The single boundary is that the value may not be NEGATIVE, and
    /// <see cref="Validate"/> is the one place that boundary is stated. A negative multiplier
    /// yields a negative product, which every guarded legacy site
    /// (<c>If timeOut &gt; 0</c>) could only ever have interpreted as "no caching", while an
    /// unguarded target site would hand a negative duration to the cache and fail mid-request.
    /// Turning that into a start-up failure loses no legacy outcome, because <c>0</c> expresses
    /// "no caching" exactly and unambiguously; the divergence is recorded in
    /// MIGRATION_NOTES.md rather than absorbed silently.
    /// </para>
    /// <para>
    /// Not every legacy cache write applied this multiplier - the host-settings cache in
    /// Library/Components/Host/HostSettings.vb is written with no expiry at all - so a caller
    /// should confirm against its own legacy site rather than assume the multiplier is
    /// universal. In particular, Library/Components/Users/UserController.vb:L665 uses this value
    /// as the entire expiry, with no base lifetime and no guard, as noted above.
    /// </para>
    /// </remarks>
    /// <example>
    /// An Application service computes the expiry itself and applies the legacy guard before it
    /// touches the cache:
    /// <code>
    /// int minutes = PortalDictionaryBaseMinutes * _caching.PerformanceMultiplier;
    /// if (minutes > 0)
    /// {
    ///     portals = await _cache.GetOrCreateAsync(
    ///         key, LoadPortalsAsync, TimeSpan.FromMinutes(minutes), cancellationToken);
    /// }
    /// </code>
    /// The multiplied legacy sites this shape reproduces are
    /// Library/Components/Portal/PortalController.vb:L218 and L1232, and
    /// Library/Components/Modules/ModuleController.vb:L998, L1052, L1264 and L1355.
    /// </example>
    public int PerformanceMultiplier { get; set; } = 3;

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a
    /// misconfigured deployment fails while the host is starting rather than midway through a
    /// request.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or
    /// an empty collection when the instance is usable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The collection shape is deliberate: options validation has to report EVERY failure at once,
    /// because an operator fixing one setting per restart is the outcome a single-failure result
    /// produces. It also keeps this class free of any dependency - the return type and the
    /// messages use base-class-library types only, so binding, hosting and validation packages all
    /// stay in the Api layer where the migration plan places them.
    /// </para>
    /// <para>
    /// This method deliberately does NOT reject a multiplier outside the legacy four. The reason,
    /// with the measurement behind it, is recorded on
    /// <see cref="PerformanceMultiplier"/>; enforcing the closed set here would tighten a
    /// configuration the legacy installation accepted.
    /// </para>
    /// </remarks>
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
