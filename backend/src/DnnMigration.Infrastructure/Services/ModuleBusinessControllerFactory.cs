// MIGRATION: Five legacy sites bound a module's companion class late, from the free-text name held in
// the DesktopModules.BusinessControllerClass column, and then applied a run-time type test to the
// untyped reference they were handed: ModuleController.vb:L231 (content export),
// ModuleController.vb:L431 (content import), and the module event-message processor at :L32 (queued
// import), :L52 (version migration) and :L77 (capability re-probe). All five collapse into a single
// lookup against a closed set of registrations fixed in code before the first request is served. The
// legacy deployment leaned further on the private probing paths configured at
// Website/release.config:L253, which have no target equivalent and are not reintroduced: nothing here
// searches a directory for code to run, and a stored name this installation does not recognise
// resolves to nothing at all.
//
// MIGRATION: Source analysis found no true native interop at any of those five sites. The helper they
// called was a managed-runtime late-binding utility, so what this file replaces is managed late binding
// only. No interop subsystem existed here, and none is claimed to have been removed.
//
// MIGRATION: Three swallowed-failure paths are deliberately not reproduced - the bare handler around
// the export site, the handler commented "ignore errors" around the import site, and the handler
// wrapping the queued import in the event-message processor. A swallowed export writes an incomplete
// portal template and a swallowed import loses content, so each now surfaces as a failed outcome
// carrying a stable code and a fixed message, while the module's own explanation is written to the log
// with the full exception. Not swallowing the failure and not disclosing its text are two separate
// obligations, and the split below satisfies both rather than trading one for the other.
//
// MIGRATION: The deferred post-restart path is omitted. Legacy import tested whether the stored
// capability bitmask still held the "not yet determined" marker of -1 (ModuleController.vb:L422) and,
// when it did, queued the payload for processing after an application restart, because a freshly
// deployed module's capabilities could not be established during the request that installed it. That
// column was introduced defaulting to 0 (03.01.00.SqlDataProvider:L14), forced non-null with the same
// default (03.01.01.SqlDataProvider:L912-L914) and finally normalised so that -1 and NULL both became 0
// (04.06.00.SqlDataProvider:L14-L16). A closed startup set is complete before the first request, so the
// marker cannot arise here and -1 is never reported.
//
// MIGRATION: The legacy searchable lifecycle contract is not ported. Its single operation accepted a
// legacy module entity and returned a pre-generics collection type, both of which this migration
// eliminates, and search execution is deferred in its entirety. Searchable therefore survives as a
// capability bit only, because the legacy probe wrote all three bits in one assignment and dropping one
// would silently change the number a caller persists.
//
// MIGRATION: The payload crosses this boundary as opaque text. The legacy sites escaped it for
// transport when writing and unescaped it when reading, using request-pipeline helpers this project
// cannot reference, then wrapped it in a template-document element. Deciding whether a payload needs
// escaping, and assembling any document around it, belongs entirely to the caller.
//
// MIGRATION: Installing executable module behaviour now means a deployment-time registration in the
// closed set, never loading code named by a database row. Enabling or removing a module for one portal
// stays persisted association data owned by the module service and the domain persistence abstractions;
// it neither adds to nor removes from the startup set, and it never causes code to be loaded.
//
// MIGRATION: The scheduler-driven purge job for the users-online feature (its legacy source spans
// L43-L119) is intentionally omitted, in line with the plan's exclusion of the legacy scheduling
// subsystem. No hosted background-service registration is added here, and this layer starts no
// recurring work of its own.

using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Performs a module's lifecycle operations - content export, content import, version migration and
/// capability reporting - through a business controller resolved from the closed set registered at
/// application start-up.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A facade, not a locator.</strong> No member hands a controller back to its caller. The
/// legacy defect being retired was precisely that: a late-bound, untyped reference was produced and
/// then interrogated with a run-time type test before being cast, so neither the compiler nor the
/// caller could tell which operations were genuinely available. Here the operation is performed and its
/// outcome reported, which keeps every controller instance - and the whole question of how one is
/// built - inside the layer that owns registration.
/// </para>
/// <para>
/// <strong>Closed set, current scope, no cached instance.</strong> The only way a controller becomes
/// reachable is a keyed registration written in code, so a database change alone can never cause code to
/// run; a name no registration covers resolves to nothing. Each member resolves its controller from the
/// CURRENT scope's provider, once per call, so a controller that depends on scoped services - most
/// importantly on the request's database context - is given the same ones the caller is using. Storing a
/// resolved controller in a field would pin whichever scope happened to be active first and then serve it
/// to every later caller, corrupting data across tenants and requests, and no compiler catches that;
/// nothing here holds one beyond the call that resolved it.
/// </para>
/// <para>
/// <strong>Absence is never failure.</strong> An unsupplied name, a name the set does not hold, and a
/// registered controller that does not implement the lifecycle contract a member needs are all
/// successful outcomes carrying an advisory reason. A failed outcome means the controller was genuinely
/// asked and its own code threw.
/// </para>
/// <para>
/// <strong>A module's own explanation is logged, never returned.</strong> The reason on a failed outcome
/// carries a stable code and a fixed message naming the operation that failed, and nothing else - not the
/// exception text, not the supplied name, not the payload, not a stack trace. The exception itself is
/// written to the log in full, at error severity, inside the request's correlation scope, so the
/// actionable detail is preserved for whoever operates the system rather than for whoever called it. This
/// is a security boundary rather than a stylistic choice: a business controller is third-party code, the
/// fault classification below admits anything it raises, and a failed outcome from this type reaches the
/// HTTP client verbatim as the problem-details explanation.
/// </para>
/// <para>
/// <strong>What this type deliberately does not do.</strong> It never reads or writes a module, never
/// caches and never clears a cache - the legacy cache refresh that followed a queued import belongs to
/// the caller and the cache abstraction - never writes an audit entry, and never persists the capability
/// bitmask it computes. It opens no transaction either: whether the surrounding work commits is the
/// caller's decision, taken through the domain unit-of-work abstraction.
/// </para>
/// </remarks>
internal sealed class ModuleBusinessControllerFactory : IModuleBusinessControllerFactory
{
    /// <summary>Capability bit meaning the controller can serialise and restore its own content.</summary>
    /// <remarks>
    /// The three bit values reproduce the legacy DesktopModules.SupportedFeatures encoding exactly
    /// (portable 1, searchable 2, upgradeable 4), so a value reported here can be stored in that column
    /// and read by anything that still understands it.
    /// </remarks>
    private const int PortableFeatureBit = 1;

    /// <summary>Capability bit meaning the controller can contribute items to a search index.</summary>
    private const int SearchableFeatureBit = 2;

    /// <summary>Capability bit meaning the controller can migrate its content between versions.</summary>
    private const int UpgradeableFeatureBit = 4;

    /// <summary>Advisory code for a module that declares no business controller at all.</summary>
    private const string NotSpecifiedCode = "module.controller.not_specified";

    /// <summary>Advisory code for a name the closed registration set does not hold.</summary>
    private const string NotRegisteredCode = "module.controller.not_registered";

    /// <summary>Advisory code for a registered controller lacking the lifecycle contract needed.</summary>
    private const string ContractNotSupportedCode = "module.controller.contract_not_supported";

    /// <summary>Advisory code for an import called with an empty payload.</summary>
    private const string ContentNotSuppliedCode = "module.content.not_supplied";

    /// <summary>Failure code for a controller that could not be brought into existence to examine.</summary>
    private const string CapabilityProbeFailedCode = "module.controller.capability_probe_failed";

    /// <summary>Failure code for a controller whose own serialisation threw.</summary>
    private const string ExportFailedCode = "module.content.export_failed";

    /// <summary>Failure code for a controller whose own restore threw.</summary>
    private const string ImportFailedCode = "module.content.import_failed";

    /// <summary>Failure code for a controller whose own migration threw.</summary>
    private const string UpgradeFailedCode = "module.upgrade_failed";

    /// <summary>Names the capability probe in a log record and selects its caller-safe detail.</summary>
    private const string CapabilityProbeOperation = "capability probe";

    /// <summary>Names the content export in a log record and selects its caller-safe detail.</summary>
    private const string ExportOperation = "content export";

    /// <summary>Names the content import in a log record and selects its caller-safe detail.</summary>
    private const string ImportOperation = "content import";

    /// <summary>Names the content migration in a log record and selects its caller-safe detail.</summary>
    private const string UpgradeOperation = "content migration";

    /// <summary>
    /// The fixed detail a caller is told when a controller could not be brought into existence.
    /// </summary>
    /// <remarks>
    /// M-09: each of these four is a COMPLETE sentence written by this application, containing nothing a
    /// module supplied. Each says what failed, and that the explanation has been recorded, so an operator
    /// knows where to look without the caller being shown anything a third-party component wrote.
    /// </remarks>
    private const string CapabilityProbeFailedDetail =
        "The module's business controller could not be brought into existence, so its capabilities could "
        + "not be determined. The underlying fault has been recorded.";

    /// <summary>The fixed detail a caller is told when a controller's own export threw.</summary>
    private const string ExportFailedDetail =
        "The module's business controller failed while serialising its content. The underlying fault has "
        + "been recorded.";

    /// <summary>The fixed detail a caller is told when a controller's own import threw.</summary>
    private const string ImportFailedDetail =
        "The module's business controller failed while restoring its content. The underlying fault has "
        + "been recorded.";

    /// <summary>The fixed detail a caller is told when a controller's own migration threw.</summary>
    private const string UpgradeFailedDetail =
        "The module's business controller failed while migrating its content. The underlying fault has "
        + "been recorded.";

    /// <summary>
    /// Largest number of links followed when describing an exception chain for the log.
    /// </summary>
    /// <remarks>
    /// A bound rather than a preference: a cyclic or deeply nested chain would otherwise let one module
    /// fault write an unbounded log entry, which is a denial-of-service vector against the log itself.
    /// </remarks>
    private const int MaximumDescribedChainDepth = 8;

    private readonly IServiceProvider _provider;
    private readonly ILogger<ModuleBusinessControllerFactory> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="ModuleBusinessControllerFactory"/> class.
    /// </summary>
    /// <param name="provider">
    /// The CURRENT scope's service provider. This type is registered scoped, so the provider injected here
    /// is the one belonging to the request being served, and a controller resolved from it shares that
    /// request's unit of work.
    /// </param>
    /// <param name="logger">
    /// Records a module fault privately. It is what allows the reason returned to the caller to be a fixed
    /// sentence while the underlying explanation still reaches an operator.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// M-08: an earlier revision took a hand-built registry object and an <c>IServiceScopeFactory</c>,
    /// creating a NESTED SCOPE for every call. Both are replaced. The registry is gone because .NET 8's
    /// keyed service registration is the platform primitive for "resolve the service filed under this
    /// name", so a hand-rolled name-to-type dictionary is exactly the workaround Rule T8 says to delete
    /// rather than translate. The scope factory is gone because a nested scope was actively wrong: a
    /// controller resolved inside one would receive a DIFFERENT database context from the request that
    /// invoked it, so content it wrote would commit - or fail to commit - independently of the caller's
    /// unit of work. Taking the current scope's provider is what makes the lifecycle operation part of the
    /// caller's transaction, which is what the legacy in-request execution did.
    /// </para>
    /// <para>
    /// Injecting <c>IServiceProvider</c> is service location, and that is the point: this type exists
    /// precisely to resolve a service selected by a stored NAME, which no constructor signature can
    /// express. What matters is that the name selects among registrations fixed in code before the first
    /// request - a name no registration covers resolves to nothing at all - so a database row still cannot
    /// cause arbitrary code to run.
    /// </para>
    /// </remarks>
    public ModuleBusinessControllerFactory(
        IServiceProvider provider,
        ILogger<ModuleBusinessControllerFactory> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Normalises a stored controller name into the key its registration is filed under.
    /// </summary>
    /// <param name="businessControllerClass">The name as stored in the column, possibly blank.</param>
    /// <returns>The lookup key, or <see langword="null"/> when the module declares no controller.</returns>
    /// <remarks>
    /// Trimmed and lowered with the invariant culture. Keyed resolution compares keys with ordinary
    /// equality, which for a string is case-SENSITIVE, whereas the legacy lookup matched the column
    /// case-insensitively - the value was hand-entered in module manifests. Normalising both the
    /// registration key and the lookup key through this one method restores that behaviour without
    /// admitting the partial or fuzzy matching that would let one module's behaviour answer for
    /// another's. The invariant culture is used because a controller name is a .NET type name rather than
    /// localised text, so no culture-specific casing rule should apply to it.
    /// </remarks>
    internal static string? RegistrationKey(string? businessControllerClass)
    {
        return string.IsNullOrWhiteSpace(businessControllerClass)
            ? null
            : businessControllerClass.Trim().ToLowerInvariant();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Capabilities come from the live registration rather than from a stored column or a directory
    /// search, and the bitmask is reported rather than persisted: the write-back belongs to the caller.
    /// The controller is brought into existence and examined instead of having its registered type
    /// inspected, for two reasons - a registration may be satisfied by a factory whose concrete type
    /// differs from the registered one, in which case only the resolved instance tells the truth, and
    /// resolution can genuinely fail when one of the controller's own dependencies cannot be satisfied,
    /// which is the condition the documented probe failure exists to report. All three contracts are
    /// examined inside one scope, so a scoped registration yields one instance rather than three.
    /// </remarks>
    public Task<Result<int?>> GetSupportedFeaturesAsync(
        string? businessControllerClass,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            object? controller = Resolve(businessControllerClass);

            if (controller is null)
            {
                return Task.FromResult(Absent<int?>(businessControllerClass));
            }

            int features = 0;

            if (controller is IModuleContentPortability)
            {
                features |= PortableFeatureBit;
            }

            if (controller is IModuleSearchContribution)
            {
                features |= SearchableFeatureBit;
            }

            if (controller is IModuleContentUpgrade)
            {
                features |= UpgradeableFeatureBit;
            }

            // Zero is a positive statement - "this controller supports none of the three" - and is
            // distinct from the null that means no answer exists. MIGRATION: the legacy "not yet
            // determined" state was stored as -1 and screened for before any bit was tested; a closed
            // startup set makes that state unreachable, so -1 is never reported as "unknown".
            return Task.FromResult(Result<int?>.Success(features));
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            return Task.FromResult(Result<int?>.Failure(
                CapabilityProbeFailedCode,
                Describe(CapabilityProbeOperation, businessControllerClass, error)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The empty string and <see langword="null"/> are distinct answers and are never substituted for
    /// one another: the first says the controller was asked and had nothing to give, the second says it
    /// was never asked. Assembling a portal template around the payload remains the caller's work,
    /// because this member exchanges text and touches no document type.
    /// </remarks>
    public async Task<Result<string?>> ExportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            object? controller = Resolve(businessControllerClass);

            if (controller is null)
            {
                return Absent<string?>(businessControllerClass);
            }

            IModuleContentPortability? portable = controller as IModuleContentPortability;

            if (portable is null)
            {
                return Result<string?>.Success(
                    null,
                    new ResultReason(
                        ContractNotSupportedCode,
                        "The registered business controller cannot serialise its own content."));
            }

            string? payload = await portable
                .ExportAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);

            // MIGRATION: an empty payload restates the legacy non-empty guard at
            // ModuleController.vb:L234, where a module with nothing to give simply contributed no
            // content element. It is a successful answer, so it stays an empty string.
            return Result<string?>.Success(payload ?? string.Empty);
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            return Result<string?>.Failure(
                ExportFailedCode,
                Describe(ExportOperation, businessControllerClass, error));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The acting user is always the value the caller supplied and is never taken from ambient state,
    /// which is what allows a restore to be attributed correctly when it runs outside a request. No
    /// cache is refreshed here: the legacy queued path cleared the module cache immediately afterwards,
    /// and that orchestration now belongs to the module service and the cache abstraction.
    /// </remarks>
    public async Task<Result> ImportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        string? content,
        string? version,
        int userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            object? controller = Resolve(businessControllerClass);

            // Controller absence is screened before the content guard, preserving the order the two
            // answers were originally reported in: a module that declares no controller, or names one
            // this installation does not carry, is told that first even when its content is also blank.
            if (controller is null)
            {
                return Result.Success(AbsenceReason(businessControllerClass));
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return Result.Success(new ResultReason(
                    ContentNotSuppliedCode,
                    "No content was supplied, so nothing was restored."));
            }

            IModuleContentPortability? portable = controller as IModuleContentPortability;

            if (portable is null)
            {
                return Result.Success(new ResultReason(
                    ContractNotSupportedCode,
                    "The registered business controller cannot restore content."));
            }

            await portable
                .ImportAsync(moduleId, content, version, userId, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            // A failure here means content was lost, so it is reported rather than absorbed: the caller
            // is expected to log it and to treat the surrounding operation as incomplete.
            return Result.Failure(
                ImportFailedCode,
                Describe(ImportOperation, businessControllerClass, error));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One version per call, matching the single-version operation the module itself exposes. MIGRATION:
    /// the legacy path split a comma-separated set of applicable versions and looped it, writing one
    /// audit entry per version; a caller reproduces the sequence by invoking this member once per
    /// version in the order it chooses and stopping at the first failed outcome, and it owns the audit
    /// entry, which is why the module's own account of what it did is returned rather than logged here.
    /// </remarks>
    public async Task<Result<string?>> UpgradeModuleAsync(
        string? businessControllerClass,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            object? controller = Resolve(businessControllerClass);

            if (controller is null)
            {
                return Absent<string?>(businessControllerClass);
            }

            IModuleContentUpgrade? upgradeable = controller as IModuleContentUpgrade;

            if (upgradeable is null)
            {
                return Result<string?>.Success(
                    null,
                    new ResultReason(
                        ContractNotSupportedCode,
                        "The registered business controller cannot migrate its content."));
            }

            string? account = await upgradeable
                .UpgradeAsync(version, cancellationToken)
                .ConfigureAwait(false);

            // An empty account means the migration succeeded with nothing to say, which the legacy path
            // recorded as an audit entry without a result line. Null would mean the controller was never
            // asked, so the two stay distinct on this member as well.
            return Result<string?>.Success(account ?? string.Empty);
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            return Result<string?>.Failure(
                UpgradeFailedCode,
                Describe(UpgradeOperation, businessControllerClass, error));
        }
    }

    /// <summary>
    /// Resolves the controller a stored name selects, from the current scope.
    /// </summary>
    /// <param name="businessControllerClass">
    /// The name as stored in the DesktopModules.BusinessControllerClass column. A blank value means the
    /// module declares no business controller.
    /// </param>
    /// <returns>
    /// The controller, or <see langword="null"/> when the name is blank or no registration is filed under
    /// it. Both are expected answers, which is why absence is a value rather than an exception.
    /// </returns>
    /// <remarks>
    /// <para>
    /// M-08: this is the whole of what the hand-built registry used to do, expressed with the platform
    /// primitive for it. Keyed resolution consults registrations fixed in code before the first request, so
    /// the closed-set guarantee is unchanged: the supplied name is never treated as a type name, never
    /// converted into one and never used to construct anything - it only selects a registration a
    /// deployment already put in place, and a name none covers yields nothing at all.
    /// </para>
    /// <para>
    /// Resolved ONCE per call and narrowed by type test at each use, rather than resolved once per
    /// contract. That is not merely tidier: a controller registered scoped must yield the SAME instance to
    /// all three capability tests within one call, and three separate resolutions of three different
    /// contract types could not guarantee it.
    /// </para>
    /// <para>
    /// The non-generic keyed lookup is used deliberately. The registered service type is not known here -
    /// only the name is - so the request is for the object filed under that key, which the extension method
    /// in this layer's dependency-injection module registers as a factory over the concrete registration.
    /// A resolution failure inside that factory is left to propagate OUT OF THIS METHOD rather than being
    /// converted into absence here, so the calling member can report it under its own documented failure
    /// code instead of pretending the controller was absent. Every caller therefore invokes this from
    /// inside its own guarded region: constructing a controller runs the module's own constructor and its
    /// dependency graph, which is third-party code exactly as its lifecycle members are, so a fault raised
    /// there is reported the same way - the member's failure code with a fixed, caller-safe detail - and
    /// never escapes this layer as an exception.
    /// </para>
    /// </remarks>
    private object? Resolve(string? businessControllerClass)
    {
        string? key = RegistrationKey(businessControllerClass);

        return key is null ? null : _provider.GetKeyedService<object>(key);
    }

    /// <summary>Builds the successful "nothing was done" outcome for a value-returning member.</summary>
    /// <typeparam name="TValue">The member's value type.</typeparam>
    /// <param name="businessControllerClass">The name the caller supplied, possibly blank.</param>
    /// <returns>A successful outcome carrying no value and the advisory reason that applies.</returns>
    private static Result<TValue?> Absent<TValue>(string? businessControllerClass)
    {
        return Result<TValue?>.Success(default, AbsenceReason(businessControllerClass));
    }

    /// <summary>Chooses between the two reasons a controller was never asked.</summary>
    /// <param name="businessControllerClass">The name the caller supplied, possibly blank.</param>
    /// <returns>The advisory reason describing why nothing was done.</returns>
    /// <remarks>
    /// The two are kept apart because they mean different things operationally: a blank name is the
    /// ordinary state of a module that declares no business controller, whereas a name the closed set
    /// does not hold means a module asks for behaviour this installation does not carry, which is worth
    /// noticing.
    /// </remarks>
    private static ResultReason AbsenceReason(string? businessControllerClass)
    {
        return string.IsNullOrWhiteSpace(businessControllerClass)
            ? new ResultReason(
                NotSpecifiedCode,
                "The module declares no business controller, so there was nothing to do.")
            : new ResultReason(
                NotRegisteredCode,
                "No business controller is registered under the name the module declares.");
    }

    /// <summary>Decides whether an exception should be reported as a module fault.</summary>
    /// <param name="error">The exception to classify.</param>
    /// <returns><see langword="true"/> when it belongs in a failed outcome.</returns>
    /// <remarks>
    /// A module is third-party code and may throw anything, so this boundary is deliberately wide - the
    /// alternative is one module's fault ending an operation that spans several. Three kinds are let
    /// through rather than converted into a return value: cancellation, because the caller asked for it
    /// and it is not a fault, and the two conditions that mean the process itself is no longer sound,
    /// where a return value would hide something that must not be hidden.
    /// </remarks>
    private static bool IsModuleFault(Exception error)
    {
        return error is not (OperationCanceledException or StackOverflowException or OutOfMemoryException);
    }

    /// <summary>
    /// Records a module fault privately and returns the fixed detail the caller may be told.
    /// </summary>
    /// <param name="operation">Which lifecycle operation was being performed, for the log record.</param>
    /// <param name="businessControllerClass">The controller name, for the log record only.</param>
    /// <param name="error">The exception raised by the module's own code.</param>
    /// <returns>The fixed, caller-safe detail for the failure reason.</returns>
    /// <remarks>
    /// <para>
    /// M-09: an earlier revision returned <c>error.Message</c> as the reason's message, on the stated
    /// grounds that the contract requires the underlying explanation to survive so the caller can log
    /// something actionable. The requirement is real; the means was wrong. A reason's message does not stay
    /// inside the process: the API edge publishes it as the <c>detail</c> member of the problem document,
    /// so the text became part of an HTTP response. A module is THIRD-PARTY code and its exception message
    /// is entirely outside this application's control - it can legitimately contain a connection string, a
    /// file-system path, a SQL fragment, an account name or a stack-derived type name, and none of that
    /// belongs in a response to a caller who asked only for an export.
    /// </para>
    /// <para>
    /// The explanation still survives, in the place it is actually useful: it is logged here, at error
    /// level, with the operation and the controller name attached as structured fields, and the exception
    /// chain described by <see cref="DescribeForDiagnostics(Exception)"/> - its type names and stack
    /// traces, and none of its messages. What crosses the boundary to the caller is one of four fixed
    /// sentences chosen by the failure code, which tells the caller what failed and that the detail is
    /// recorded, without quoting anything a module wrote.
    /// </para>
    /// <para>
    /// The exception object is not handed to the logger, and that is a SECOND disclosure decision rather
    /// than a stylistic one: the logging framework formats an exception it is given, which would reinstate
    /// the very messages the paragraph above removes - this time into the log sink instead of the
    /// response. The API edge's own unhandled-fault diagnostics apply the same rule by the same means.
    /// </para>
    /// <para>
    /// The controller NAME is logged although it is not returned. It came from the installation's own
    /// registration set rather than from the request, so it discloses nothing about the caller, and
    /// without it a log entry cannot say which module failed.
    /// </para>
    /// </remarks>
    private string Describe(string operation, string? businessControllerClass, Exception error)
    {
        // THE EXCEPTION OBJECT IS DELIBERATELY NOT HANDED TO THE LOGGER, and that is a second disclosure
        // decision beyond the one above. Passing it would reinstate the module's own message through the
        // logging framework's formatting, so the diagnosis is built by
        // DescribeForDiagnostics(Exception) - type chain and stack traces, no messages - and passed as a
        // structured property. This is the same treatment, by the same means, that the API edge applies
        // to an unhandled fault.
        _logger.LogError(
            "Module business controller {BusinessControllerClass} failed during {ModuleOperation}: {Failure}",
            businessControllerClass,
            operation,
            DescribeForDiagnostics(error));

        return operation switch
        {
            CapabilityProbeOperation => CapabilityProbeFailedDetail,
            ExportOperation => ExportFailedDetail,
            ImportOperation => ImportFailedDetail,
            UpgradeOperation => UpgradeFailedDetail,

            // Unreachable: every call site passes one of the four constants above. Naming the case
            // explicitly is better than returning a detail that describes the wrong operation.
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "The lifecycle operation has no caller-safe failure detail."),
        };
    }

    /// <summary>Describes an exception chain for the log without quoting any of its messages.</summary>
    /// <param name="error">The exception to describe.</param>
    /// <returns>The type chain and stack traces, bounded in depth.</returns>
    /// <remarks>
    /// Mirrors the API edge's redacting description rather than sharing it, because that member is private
    /// to a type in a layer this one does not reference and hoisting it into a shared helper would put a
    /// diagnostics utility on a layer boundary for the sake of two callers. The rule it implements is the
    /// one that matters and is restated where it is applied: type names and stack traces are admitted,
    /// messages are not.
    /// </remarks>
    private static string DescribeForDiagnostics(Exception error)
    {
        StringBuilder description = new();
        Exception? current = error;
        int depth = 0;

        while (current is not null && depth < MaximumDescribedChainDepth)
        {
            if (depth > 0)
            {
                description.Append(" ---> ");
            }

            // FullName is null only for a generic parameter type, which an exception cannot be; the
            // fallback keeps the description from ever carrying an empty position.
            description.Append(current.GetType().FullName ?? current.GetType().Name);

            string? stack = current.StackTrace;

            if (!string.IsNullOrWhiteSpace(stack))
            {
                description.Append(' ').Append(stack);
            }

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            description.Append(" ---> (chain truncated)");
        }

        return description.ToString();
    }

    /// <summary>
    /// Implemented by a module's business controller that can serialise and restore its own content.
    /// </summary>
    /// <remarks>
    /// MIGRATION: replaces the legacy IPortable contract, which the legacy code discovered by producing an
    /// untyped reference and applying a run-time type test to it - at ModuleController.vb:L231 for export
    /// and ModuleController.vb:L431 for import. Here the contract is a compile-time fact about a registered
    /// type, so a module that cannot export says so by not implementing this interface rather than by
    /// failing a cast. Both operations are asynchronous and take a cancellation token, because a module's
    /// content operation is I/O bound and a caller must be able to abandon it.
    /// </remarks>
    internal interface IModuleContentPortability
    {
        /// <summary>Serialises the content held by one module instance.</summary>
        /// <param name="moduleId">
        /// The module instance whose content is wanted, passed through verbatim: the module key is seeded at
        /// 0, so 0 identifies a real module and must never be read as absent.
        /// </param>
        /// <param name="cancellationToken">Token that cancels the operation.</param>
        /// <returns>
        /// The serialised payload, or an empty string when the module holds nothing worth exporting. An
        /// empty result is an answer rather than a failure.
        /// </returns>
        Task<string> ExportAsync(int moduleId, CancellationToken cancellationToken = default);

        /// <summary>Restores previously serialised content into one module instance.</summary>
        /// <param name="moduleId">The module instance to restore into, passed through verbatim.</param>
        /// <param name="content">The payload to restore, exactly as a previous export produced it.</param>
        /// <param name="version">
        /// The version stamp recorded alongside the payload, so the module can interpret a payload written by
        /// an older release of itself.
        /// </param>
        /// <param name="userId">
        /// The principal on whose behalf the content is restored, which the module records as the author of
        /// whatever it creates. Passed through verbatim.
        /// </param>
        /// <param name="cancellationToken">Token that cancels the operation.</param>
        /// <returns>A task that completes once the content has been restored.</returns>
        Task ImportAsync(
            int moduleId,
            string content,
            string? version,
            int userId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implemented by a module's business controller that can contribute items to a search index.
    /// </summary>
    /// <remarks>
    /// MIGRATION: a marker with no operations, and deliberately so. The legacy searchable contract accepted a
    /// legacy module entity and returned a pre-generics collection type, both of which this migration
    /// eliminates, and search execution is deferred in its entirety, so no searchable operation is exposed
    /// or invoked anywhere. The capability bit is still reported, because the legacy probe wrote all three
    /// bits in one assignment and omitting one would silently change the number a caller persists.
    /// Implementing this interface therefore declares the capability for that stored value and promises
    /// nothing that is called today.
    /// </remarks>
    internal interface IModuleSearchContribution
    {
    }

    /// <summary>
    /// Implemented by a module's business controller that can migrate its stored content between versions.
    /// </summary>
    /// <remarks>
    /// MIGRATION: replaces the legacy IUpgradeable contract, which the legacy code discovered by run-time
    /// type test in the module event-message processor at :L52 before looping the applicable versions.
    /// </remarks>
    internal interface IModuleContentUpgrade
    {
        /// <summary>Migrates the module's stored content to one named version.</summary>
        /// <param name="version">
        /// The single version to migrate to. A caller reproduces the legacy sequence by invoking this once
        /// per applicable version, in the order it chooses.
        /// </param>
        /// <param name="cancellationToken">Token that cancels the operation.</param>
        /// <returns>
        /// The module's own account of what it did, which the caller records. An empty string means the
        /// migration succeeded with nothing to say.
        /// </returns>
        Task<string> UpgradeAsync(string version, CancellationToken cancellationToken = default);
    }
}
