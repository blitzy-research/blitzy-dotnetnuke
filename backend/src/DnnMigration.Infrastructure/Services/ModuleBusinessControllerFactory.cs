using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Implemented by a module's business controller that can serialise and restore its own content.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the legacy <c>IPortable</c> contract, which the legacy code discovered by
/// activating an untyped reference and applying a run-time type test to it - at
/// <c>ModuleController.vb:L231</c> for export and <c>:L431</c> for import. Here the contract is a
/// compile-time fact about a registered type, so a module that cannot export says so by not implementing
/// this interface rather than by failing a cast.
/// </remarks>
internal interface IModuleContentPortability
{
    /// <summary>Serialises the content held by one module instance.</summary>
    /// <param name="moduleId">
    /// The module instance whose content is wanted. Passed through verbatim: <c>Modules.ModuleID</c> is
    /// <c>IDENTITY(0, 1)</c>, so 0 identifies a real module and must not be read as absent.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// The serialised payload, or an empty string when the module holds nothing worth exporting. An
    /// empty result is an answer, not a failure.
    /// </returns>
    Task<string> ExportAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Restores previously serialised content into one module instance.</summary>
    /// <param name="moduleId">The module instance to restore into, passed through verbatim.</param>
    /// <param name="content">The payload to restore, exactly as a previous export produced it.</param>
    /// <param name="version">
    /// The version stamp recorded alongside the payload, so the module can interpret a payload written
    /// by an older release of itself.
    /// </param>
    /// <param name="userId">
    /// The principal on whose behalf the content is restored, recorded by the module as the author of
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
/// A marker with no operations, and deliberately so. MIGRATION: the legacy <c>ISearchable</c> contract
/// depended on two types this migration eliminates, and search is deferred in its entirety, so no
/// searchable operation is exposed anywhere. The capability flag is still reported, because the legacy
/// probe wrote all three flags in one assignment to <c>DesktopModules.SupportedFeatures</c> - omitting
/// one would silently change the number a caller persists. Implementing this interface therefore
/// declares the capability for that stored value and promises nothing that is called today.
/// </remarks>
internal interface IModuleSearchContribution
{
}

/// <summary>
/// Implemented by a module's business controller that can migrate its stored content between versions.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the legacy <c>IUpgradeable</c> contract, discovered by run-time type test at
/// <c>EventMessageProcessor.vb:L52</c>.
/// </remarks>
internal interface IModuleContentUpgrade
{
    /// <summary>Migrates the module's stored content to one named version.</summary>
    /// <param name="version">
    /// The single version to migrate to. The legacy path derived a comma-separated list of applicable
    /// versions and looped it; a caller reproduces that by invoking this once per version in ascending
    /// order.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// The module's own account of what it did, which the caller records. An empty string means the
    /// migration succeeded with nothing to report.
    /// </returns>
    Task<string> UpgradeAsync(string version, CancellationToken cancellationToken = default);
}

/// <summary>
/// The closed set of business controllers this installation recognises, keyed by the value stored in
/// <c>Modules.BusinessControllerClass</c>.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this replaces late-bound activation from a stored class name. Five legacy sites called
/// <c>Framework.Reflection.CreateObject(objModule.BusinessControllerClass)</c> -
/// <c>ModuleController.vb:L231</c> and <c>:L431</c>, and
/// <c>EventMessageProcessor.vb:L32</c>, <c>:L52</c> and <c>:L77</c> - which turned a database column
/// into an instruction to load an assembly and construct an arbitrary type. A row a tenant
/// administrator can edit therefore decided which code ran. Nothing here loads, enumerates or scans an
/// assembly, and no type is ever resolved from a string at run time: the map is fixed in code before the
/// first request is served, and a key it does not contain resolves to nothing at all.
/// </para>
/// <para>
/// The map holds <em>registrations</em>, never instances - see the remarks on
/// <see cref="ModuleBusinessControllerFactory"/> for why that distinction matters. It is immutable once
/// built and therefore safe to share as a singleton. Keys are matched case-insensitively, because the
/// stored column is free text that was hand-entered in module manifests.
/// </para>
/// <para>
/// An empty map is the expected state of this installation, and it is not a gap. Every bundled module is
/// out of scope for this migration, and most modules declare no business controller in any case, so
/// "no registration covers this key" is the ordinary answer and is reported as a success carrying an
/// advisory reason. Registering a controller is a single call - see the service-collection extension in
/// this layer's dependency-injection module.
/// </para>
/// </remarks>
internal sealed class ModuleBusinessControllerRegistry
{
    private readonly Dictionary<string, Type> _controllers;

    /// <summary>Initialises a new instance of the <see cref="ModuleBusinessControllerRegistry"/> class.</summary>
    /// <param name="registrations">
    /// The controllers this installation recognises. A later registration of the same key replaces an
    /// earlier one, which is what lets a host override a bundled registration without having to remove
    /// it first.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A registration carries a blank key.</exception>
    public ModuleBusinessControllerRegistry(IEnumerable<ModuleBusinessControllerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _controllers = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (ModuleBusinessControllerRegistration registration in registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.BusinessControllerClass))
            {
                throw new ArgumentException(
                    "A business-controller registration must carry a non-blank key, because the key is "
                    + "what a stored Modules.BusinessControllerClass value is matched against.",
                    nameof(registrations));
            }

            _controllers[registration.BusinessControllerClass.Trim()] = registration.ControllerType;
        }
    }

    /// <summary>Gets the number of registered controllers.</summary>
    /// <remarks>
    /// Exposed so that a diagnostic can report the map's size without being able to enumerate or mutate
    /// it. Zero is a legitimate and expected value.
    /// </remarks>
    public int Count => _controllers.Count;

    /// <summary>Finds the registered type for one stored controller key.</summary>
    /// <param name="businessControllerClass">
    /// The key as stored in <c>Modules.BusinessControllerClass</c>. A blank value means the module
    /// declares no controller.
    /// </param>
    /// <param name="controllerType">The registered type, when one is registered.</param>
    /// <returns><see langword="true"/> when a registration covers the key.</returns>
    public bool TryResolve(string? businessControllerClass, out Type? controllerType)
    {
        if (string.IsNullOrWhiteSpace(businessControllerClass))
        {
            controllerType = null;
            return false;
        }

        return _controllers.TryGetValue(businessControllerClass.Trim(), out controllerType);
    }
}

/// <summary>
/// One entry in the closed business-controller map.
/// </summary>
/// <param name="BusinessControllerClass">
/// The key this controller answers to, matched against the stored <c>Modules.BusinessControllerClass</c>
/// value case-insensitively.
/// </param>
/// <param name="ControllerType">
/// The registered service type. It must also be registered with the container, because the factory
/// resolves it from the current scope rather than constructing it.
/// </param>
internal sealed record ModuleBusinessControllerRegistration(string BusinessControllerClass, Type ControllerType);

/// <summary>
/// Performs a module's lifecycle operations through a business controller resolved from a closed set.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A facade, not a locator.</strong> No member hands a controller back to its caller. The legacy
/// defect being retired was exactly that: an untyped reference was produced and then interrogated with a
/// run-time type test before being cast, so neither the compiler nor the caller could tell which
/// operations were actually available. Here the operation is performed and its outcome reported, which
/// keeps every controller instance - and the whole question of how one is built - inside this layer.
/// </para>
/// <para>
/// <strong>Cache the registration, never the instance.</strong> This service is a singleton, because its
/// map is fixed at start-up. A business controller, however, may legitimately depend on scoped services -
/// a unit of work, a per-request tenant context - so every member resolves its controller from a scope
/// created for that call and disposes the scope on the way out. Storing a resolved controller in a field
/// would capture whichever scope happened to be active first and then serve it to every later caller,
/// which corrupts data across tenants and requests. No compiler catches that, and the container's own
/// validation does not either once the instance has been obtained through a factory.
/// </para>
/// <para>
/// <strong>Absence is never failure.</strong> An unsupplied key, an unregistered key and a registered
/// controller that does not implement the contract a member needs are all successful outcomes carrying
/// an advisory reason. A failed outcome means the controller was genuinely asked and its own code threw,
/// and the message preserves the underlying explanation so the caller can log something actionable.
/// Nothing is swallowed: content lost during an import is reported as a failure precisely so the
/// surrounding operation can be treated as incomplete.
/// </para>
/// <para>
/// <strong>What this type deliberately does not do.</strong> It never reads or writes a module, never
/// caches or invalidates - the legacy cache synchronisation that followed a queued import at
/// <c>EventMessageProcessor.vb:L43</c> belongs to the caller and the cache abstraction - never writes an
/// audit entry, and never persists the capability bitmask it computes. It opens no transaction either:
/// whether the surrounding work commits is the caller's decision, taken through the unit of work.
/// </para>
/// </remarks>
internal sealed class ModuleBusinessControllerFactory : IModuleBusinessControllerFactory
{
    /// <summary>Capability bit meaning the controller can serialise and restore its own content.</summary>
    /// <remarks>
    /// The three bit values reproduce the legacy <c>DesktopModules.SupportedFeatures</c> encoding
    /// exactly, so a value this service reports can be stored in that column and read by anything that
    /// still understands it.
    /// </remarks>
    private const int PortableFeatureBit = 1;

    /// <summary>Capability bit meaning the controller can contribute searchable items.</summary>
    private const int SearchableFeatureBit = 2;

    /// <summary>Capability bit meaning the controller can migrate its content between versions.</summary>
    private const int UpgradeableFeatureBit = 4;

    private const string NotSpecifiedCode = "module.controller.not_specified";
    private const string NotRegisteredCode = "module.controller.not_registered";
    private const string ContractNotSupportedCode = "module.controller.contract_not_supported";
    private const string ContentNotSuppliedCode = "module.content.not_supplied";
    private const string CapabilityProbeFailedCode = "module.controller.capability_probe_failed";
    private const string ExportFailedCode = "module.content.export_failed";
    private const string ImportFailedCode = "module.content.import_failed";
    private const string UpgradeFailedCode = "module.upgrade_failed";

    private readonly ModuleBusinessControllerRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Initialises a new instance of the <see cref="ModuleBusinessControllerFactory"/> class.</summary>
    /// <param name="registry">The closed registration map, immutable once built.</param>
    /// <param name="scopeFactory">
    /// Used to create one scope per call so that a controller depending on scoped services is built
    /// correctly. A factory rather than a provider, and used per call rather than held, for the reason
    /// given in the type remarks.
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
    public Task<Result<int?>> GetSupportedFeaturesAsync(
        string? businessControllerClass,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryResolve(businessControllerClass, out Type? controllerType) || controllerType is null)
        {
            return Task.FromResult(Absent<int?>(businessControllerClass));
        }

        // The controller is brought into existence and examined, rather than having its registered type
        // inspected. Two reasons: a registration may be satisfied by a factory whose concrete type
        // differs from the registered service type, in which case only the resolved instance tells the
        // truth; and resolution can genuinely fail when a dependency is unsatisfiable, which is the
        // condition the documented probe failure exists to report.
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            object controller = scope.ServiceProvider.GetRequiredService(controllerType);

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
            // distinct from the null that means no answer exists. The legacy "not yet determined" state
            // was stored as -1 and cannot arise here, because a closed map is complete before the first
            // request; -1 must never be returned to mean unknown.
            return Task.FromResult(Result<int?>.Success(features));
        }
        catch (InvalidOperationException error)
        {
            return Task.FromResult(Result<int?>.Failure(CapabilityProbeFailedCode, error.Message));
        }
    }

    /// <inheritdoc />
    public async Task<Result<string?>> ExportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryResolve(businessControllerClass, out Type? controllerType) || controllerType is null)
        {
            return Absent<string?>(businessControllerClass);
        }

        using IServiceScope scope = _scopeFactory.CreateScope();

        if (!TryResolveController(scope, controllerType, out IModuleContentPortability? portable, out string? probeError))
        {
            return probeError is null
                ? Result<string?>.Success(null, new ResultReason(
                    ContractNotSupportedCode,
                    "The registered business controller cannot serialise its own content."))
                : Result<string?>.Failure(CapabilityProbeFailedCode, probeError);
        }

        try
        {
            string payload = await portable!.ExportAsync(moduleId, cancellationToken).ConfigureAwait(false);

            // An empty payload means the controller ran and had nothing to give, which is the legacy
            // non-empty guard restated and emphatically not a failure. It stays an empty string and is
            // never converted to null, because null on this member means "was never asked" - collapsing
            // the two would lose the distinction the caller branches on.
            return Result<string?>.Success(payload ?? string.Empty);
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            return Result<string?>.Failure(ExportFailedCode, error.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> ImportModuleContentAsync(
        string? businessControllerClass,
        int moduleId,
        string? content,
        string? version,
        int userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryResolve(businessControllerClass, out Type? controllerType) || controllerType is null)
        {
            return Result.Success(AbsenceReason(businessControllerClass));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Result.Success(new ResultReason(
                ContentNotSuppliedCode,
                "No content was supplied, so nothing was restored."));
        }

        using IServiceScope scope = _scopeFactory.CreateScope();

        if (!TryResolveController(scope, controllerType, out IModuleContentPortability? portable, out string? probeError))
        {
            return probeError is null
                ? Result.Success(new ResultReason(
                    ContractNotSupportedCode,
                    "The registered business controller cannot restore content."))
                : Result.Failure(CapabilityProbeFailedCode, probeError);
        }

        try
        {
            await portable!
                .ImportAsync(moduleId, content, version, userId, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            // A failure here means content was lost, so it is reported rather than absorbed: the caller
            // is expected to log it and to treat the surrounding operation as incomplete.
            return Result.Failure(ImportFailedCode, error.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<string?>> UpgradeModuleAsync(
        string? businessControllerClass,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryResolve(businessControllerClass, out Type? controllerType) || controllerType is null)
        {
            return Absent<string?>(businessControllerClass);
        }

        using IServiceScope scope = _scopeFactory.CreateScope();

        if (!TryResolveController(scope, controllerType, out IModuleContentUpgrade? upgradeable, out string? probeError))
        {
            return probeError is null
                ? Result<string?>.Success(null, new ResultReason(
                    ContractNotSupportedCode,
                    "The registered business controller cannot migrate its content."))
                : Result<string?>.Failure(CapabilityProbeFailedCode, probeError);
        }

        try
        {
            string account = await upgradeable!.UpgradeAsync(version, cancellationToken).ConfigureAwait(false);

            // An empty account means the migration succeeded quietly, which the legacy path logged
            // without a result line. Null would mean the controller was never asked, so the two stay
            // distinct here as well.
            return Result<string?>.Success(account ?? string.Empty);
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            return Result<string?>.Failure(UpgradeFailedCode, error.Message);
        }
    }

    /// <summary>Resolves a controller from a scope and narrows it to one lifecycle contract.</summary>
    /// <typeparam name="TContract">The lifecycle contract the calling member needs.</typeparam>
    /// <param name="scope">The scope created for this call.</param>
    /// <param name="controllerType">The registered service type.</param>
    /// <param name="contract">The narrowed controller, when it implements the contract.</param>
    /// <param name="probeError">
    /// The explanation when the controller could not be brought into existence at all; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> when a usable controller was obtained.</returns>
    /// <remarks>
    /// Distinguishes the two ways this can come to nothing, because they are different answers: a
    /// controller that exists but does not implement the contract is an expected absence, while a
    /// controller that could not be constructed is a fault worth reporting. Returning one flag for both
    /// would force the caller to guess which had happened.
    /// </remarks>
    private static bool TryResolveController<TContract>(
        IServiceScope scope,
        Type controllerType,
        out TContract? contract,
        out string? probeError)
        where TContract : class
    {
        try
        {
            object controller = scope.ServiceProvider.GetRequiredService(controllerType);

            contract = controller as TContract;
            probeError = null;

            return contract is not null;
        }
        catch (InvalidOperationException error)
        {
            contract = null;
            probeError = error.Message;

            return false;
        }
    }

    /// <summary>Builds the successful "nothing was done" outcome for a value-returning member.</summary>
    /// <typeparam name="TValue">The member's value type.</typeparam>
    /// <param name="businessControllerClass">The key the caller supplied, or a blank value.</param>
    /// <returns>A successful outcome carrying no value and the advisory reason that applies.</returns>
    private static Result<TValue?> Absent<TValue>(string? businessControllerClass)
    {
        return Result<TValue?>.Success(default, AbsenceReason(businessControllerClass));
    }

    /// <summary>Chooses between the two reasons a controller was never asked.</summary>
    /// <param name="businessControllerClass">The key the caller supplied, or a blank value.</param>
    /// <returns>The advisory reason.</returns>
    /// <remarks>
    /// The two are kept apart because they mean different things operationally: an unsupplied key is the
    /// ordinary state of a module that declares no controller, whereas an unregistered key means a module
    /// names a controller this installation does not recognise - which is worth noticing.
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

    /// <summary>Decides whether an exception came from a module's own code.</summary>
    /// <param name="error">The exception to classify.</param>
    /// <returns><see langword="true"/> when it should be reported as a module fault.</returns>
    /// <remarks>
    /// A module is third-party code and may throw anything, so the outer boundary has to be wide - the
    /// alternative is an unhandled exception from one module taking down an operation that spans several.
    /// Three kinds are deliberately let through instead of being reported as module faults:
    /// cancellation, because the caller asked for it and it is not a fault at all; and the two conditions
    /// that indicate the process itself is no longer sound, where converting the exception into a
    /// return value would hide a fault that must not be hidden.
    /// </remarks>
    private static bool IsModuleFault(Exception error)
    {
        return error is not (OperationCanceledException or StackOverflowException or OutOfMemoryException);
    }
}
