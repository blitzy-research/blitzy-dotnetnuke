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
// carrying a stable code and the underlying explanation for the caller to log.
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

using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

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
/// <strong>Closed set, current scope, no cached instance.</strong> The only way into
/// <see cref="ModuleBusinessControllerRegistry"/> is a registration written in code, so a database
/// change alone can never cause code to run; a name the set does not hold resolves to nothing. Each
/// member resolves its controller through <see cref="IServiceScopeFactory"/> for the duration of that
/// call and releases it on the way back, because a business controller may legitimately depend on
/// scoped services. The registration is shared; the instance never is. Storing a resolved controller in
/// a field would pin whichever scope happened to be active first and then serve it to every later
/// caller, corrupting data across tenants and requests, and no compiler catches that.
/// </para>
/// <para>
/// <strong>Absence is never failure.</strong> An unsupplied name, a name the set does not hold, and a
/// registered controller that does not implement the lifecycle contract a member needs are all
/// successful outcomes carrying an advisory reason. A failed outcome means the controller was genuinely
/// asked and its own code threw; the message preserves the underlying explanation, as the contract
/// requires, so the caller can log something actionable, and it never repeats the supplied name or the
/// payload.
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

    /// <summary>Message substituted when a module's exception carries no explanation of its own.</summary>
    private const string UnexplainedFaultMessage =
        "The module's business controller failed without supplying an explanation.";

    private readonly ModuleBusinessControllerRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Initialises a new instance of the <see cref="ModuleBusinessControllerFactory"/> class.
    /// </summary>
    /// <param name="registry">
    /// The closed registration set, immutable once built. It is the only source of a controller in this
    /// type; there is no secondary path and no fallback that could turn an unrecognised name into
    /// something executable.
    /// </param>
    /// <param name="scopeFactory">
    /// Supplies one scope per call so that a controller depending on scoped services is built correctly.
    /// Requested as a factory rather than as a service provider because this type is registered as a
    /// singleton by this layer's dependency-injection module, and a singleton that held a provider would
    /// hold the root one.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public ModuleBusinessControllerFactory(
        ModuleBusinessControllerRegistry registry,
        IServiceScopeFactory scopeFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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

        Type? controllerType = _registry.Find(businessControllerClass);

        if (controllerType is null)
        {
            return Task.FromResult(Absent<int?>(businessControllerClass));
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            int features = 0;

            if (Narrow<IModuleContentPortability>(scope, controllerType) is not null)
            {
                features |= PortableFeatureBit;
            }

            if (Narrow<IModuleSearchContribution>(scope, controllerType) is not null)
            {
                features |= SearchableFeatureBit;
            }

            if (Narrow<IModuleContentUpgrade>(scope, controllerType) is not null)
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
            return Task.FromResult(Result<int?>.Failure(CapabilityProbeFailedCode, Describe(error)));
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

        Type? controllerType = _registry.Find(businessControllerClass);

        if (controllerType is null)
        {
            return Absent<string?>(businessControllerClass);
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            IModuleContentPortability? portable = Narrow<IModuleContentPortability>(scope, controllerType);

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
            return Result<string?>.Failure(ExportFailedCode, Describe(error));
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

        Type? controllerType = _registry.Find(businessControllerClass);

        if (controllerType is null)
        {
            return Result.Success(AbsenceReason(businessControllerClass));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Result.Success(new ResultReason(
                ContentNotSuppliedCode,
                "No content was supplied, so nothing was restored."));
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            IModuleContentPortability? portable = Narrow<IModuleContentPortability>(scope, controllerType);

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
            return Result.Failure(ImportFailedCode, Describe(error));
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

        Type? controllerType = _registry.Find(businessControllerClass);

        if (controllerType is null)
        {
            return Absent<string?>(businessControllerClass);
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            IModuleContentUpgrade? upgradeable = Narrow<IModuleContentUpgrade>(scope, controllerType);

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
            return Result<string?>.Failure(UpgradeFailedCode, Describe(error));
        }
    }

    /// <summary>
    /// Resolves the registered controller from one scope and narrows it to a single lifecycle contract.
    /// </summary>
    /// <typeparam name="TContract">The lifecycle contract the calling member needs.</typeparam>
    /// <param name="scope">The scope created for the current call.</param>
    /// <param name="controllerType">The registered service type held by the closed set.</param>
    /// <returns>
    /// The narrowed controller, or <see langword="null"/> when the registered controller does not
    /// implement <typeparamref name="TContract"/> - an expected absence rather than a fault.
    /// </returns>
    /// <remarks>
    /// The registered entry carries the service type the container knows, so the container is asked for
    /// exactly that service and the answer is narrowed once to a contract known at compile time. The
    /// supplied name is never treated as a type name, never converted into one and never used to
    /// construct anything; it only selects an entry that a deployment already put in place. A resolution
    /// failure is left to propagate, so the calling member can report it under its own documented
    /// failure code rather than pretending the controller was absent.
    /// </remarks>
    private static TContract? Narrow<TContract>(IServiceScope scope, Type controllerType)
        where TContract : class
    {
        return scope.ServiceProvider.GetRequiredService(controllerType) as TContract;
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

    /// <summary>Produces the failure message for a module fault.</summary>
    /// <param name="error">The exception raised by the module's own code.</param>
    /// <returns>The module's explanation, or a stand-in when it supplied none.</returns>
    /// <remarks>
    /// The contract requires the underlying explanation to survive so the caller can log something
    /// actionable, and a reason refuses a blank message, so an exception that carries none is given a
    /// fixed stand-in. Nothing else is added: not the supplied name, not the payload, not a stack trace.
    /// </remarks>
    private static string Describe(Exception error)
    {
        return string.IsNullOrWhiteSpace(error.Message) ? UnexplainedFaultMessage : error.Message;
    }
}

// The remaining types in this file exist because this layer's dependency-injection module already
// consumes them - it registers the set and constructs an entry - and because the migration plan
// allocated no separate home for a module lifecycle contract. They are kept deliberately minimal: three
// contracts a module author implements, one entry and the immutable set itself. Nothing here is a
// registration helper; the single way to add an entry stays the extension method in this layer's
// dependency-injection module, which is a code change by construction.

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

/// <summary>
/// One entry in the closed business-controller set.
/// </summary>
/// <param name="BusinessControllerClass">
/// The name this controller answers to, matched case-insensitively against the value stored in the
/// DesktopModules.BusinessControllerClass column - free text of at most 200 characters, and nullable,
/// which is why a blank value has to mean "this module declares no controller".
/// </param>
/// <param name="ControllerType">
/// The registered service type. It must also be registered with the container, because a controller is
/// resolved from the current scope rather than constructed here.
/// </param>
internal sealed record ModuleBusinessControllerRegistration(string BusinessControllerClass, Type ControllerType);

/// <summary>
/// The closed set of business controllers this installation recognises, keyed by the value stored in the
/// DesktopModules.BusinessControllerClass column.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this is what replaces late binding from a stored name. Five legacy sites turned a database
/// column into an instruction to bring an arbitrary type into existence, so a row an administrator could
/// edit decided which code ran in the server process. Nothing here searches for code, and a name is
/// never converted into a type: the set is fixed in code before the first request is served, and a name
/// it does not hold resolves to nothing at all.
/// </para>
/// <para>
/// The set holds entries, never instances - see the remarks on
/// <see cref="ModuleBusinessControllerFactory"/> for why that distinction matters. It is immutable once
/// built and therefore safe to share for the lifetime of the application. Names are matched with
/// <see cref="StringComparer.OrdinalIgnoreCase"/> and trimmed, which preserves the case-insensitive
/// matching the legacy lookup performed on a column that module manifests hand-entered, without
/// admitting the partial or fuzzy matching that would let one module's behaviour answer for another's.
/// </para>
/// <para>
/// An empty set is the expected state of this installation rather than a gap: every bundled module falls
/// outside this migration's scope, and most modules declare no business controller in any case, so "no
/// registration covers this name" is the ordinary answer and is reported as a success carrying an
/// advisory reason.
/// </para>
/// </remarks>
internal sealed class ModuleBusinessControllerRegistry
{
    private readonly Dictionary<string, Type> _controllers;

    /// <summary>
    /// Initialises a new instance of the <see cref="ModuleBusinessControllerRegistry"/> class.
    /// </summary>
    /// <param name="provider">
    /// The container, read exactly once here to gather every entry a deployment registered. It is not
    /// retained, so this type cannot resolve anything afterwards; all that survives construction is an
    /// immutable snapshot of names and service types.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry carries a blank name.</exception>
    /// <remarks>
    /// A later entry for the same name replaces an earlier one, which is what lets a host override a
    /// bundled registration without having to remove it first.
    /// </remarks>
    public ModuleBusinessControllerRegistry(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _controllers = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (ModuleBusinessControllerRegistration entry in
            provider.GetServices<ModuleBusinessControllerRegistration>())
        {
            if (string.IsNullOrWhiteSpace(entry.BusinessControllerClass))
            {
                throw new ArgumentException(
                    "A business-controller entry must carry a non-blank name, because the name is what a "
                    + "stored DesktopModules.BusinessControllerClass value is matched against.",
                    nameof(provider));
            }

            _controllers[entry.BusinessControllerClass.Trim()] = entry.ControllerType;
        }
    }

    /// <summary>Finds the registered service type for one stored controller name.</summary>
    /// <param name="businessControllerClass">
    /// The name as stored in the DesktopModules.BusinessControllerClass column. A blank value means the
    /// module declares no business controller.
    /// </param>
    /// <returns>
    /// The registered service type, or <see langword="null"/> when the name is blank or the closed set
    /// does not hold it. Both are expected answers, which is why absence is a value rather than an
    /// exception.
    /// </returns>
    public Type? Find(string? businessControllerClass)
    {
        if (string.IsNullOrWhiteSpace(businessControllerClass))
        {
            return null;
        }

        return _controllers.GetValueOrDefault(businessControllerClass.Trim());
    }
}

