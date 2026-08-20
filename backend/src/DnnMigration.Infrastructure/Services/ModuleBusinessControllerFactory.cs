// MIGRATION: Three swallowed-failure paths are deliberately not reproduced - the bare handler around the
// export site, the handler commented "ignore errors" around the import site, and the handler wrapping the
// queued import in the event-message processor.

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
/// <strong>A facade, not a locator.</strong> No member hands a controller back to its caller. The legacy
/// defect being retired was precisely that: a late-bound, untyped reference was produced and then
/// interrogated with a run-time type test before being cast, so neither the compiler nor the caller could
/// tell which operations were genuinely available.
/// </para>
/// <para>
/// <strong>Closed set, current scope, no cached instance.</strong> The only way a controller becomes
/// reachable is a keyed registration written in code, so a database change alone can never cause code to
/// run; a name no registration covers resolves to nothing.
/// </para>
/// </remarks>
internal sealed class ModuleBusinessControllerFactory : IModuleBusinessControllerFactory
{
    /// <summary>Capability bit meaning the controller can serialise and restore its own content.</summary>
    /// <remarks>
    /// The three bit values reproduce the legacy DesktopModules.SupportedFeatures encoding exactly
    /// (portable 1, searchable 2, upgradeable 4), so a value reported here can be stored in that column and
    /// read by anything that still understands it.
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

    /// <summary>The fixed detail a caller is told when a controller could not be brought into existence.</summary>
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

    /// <summary>Largest number of links followed when describing an exception chain for the log.</summary>
    private const int MaximumDescribedChainDepth = 8;

    private readonly IServiceProvider _provider;
    private readonly ILogger<ModuleBusinessControllerFactory> _logger;

    /// <summary>Initialises a new instance of the <see cref="ModuleBusinessControllerFactory"/> class.</summary>
    /// <param name="provider">The CURRENT scope's service provider.</param>
    /// <param name="logger">Records a module fault privately.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Injecting <c>IServiceProvider</c> is service location, and that is the point: this type exists
    /// precisely to resolve a service selected by a stored NAME, which no constructor signature can
    /// express.
    /// </remarks>
    public ModuleBusinessControllerFactory(
        IServiceProvider provider,
        ILogger<ModuleBusinessControllerFactory> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Normalises a stored controller name into the key its registration is filed under.</summary>
    /// <param name="businessControllerClass">The name as stored in the column, possibly blank.</param>
    /// <returns>The lookup key, or <see langword="null"/> when the module declares no controller.</returns>
    /// <remarks>
    /// Trimmed and lowered with the invariant culture. Keyed resolution compares keys with ordinary
    /// equality, which for a string is case-SENSITIVE, whereas the legacy lookup matched the column
    /// case-insensitively - the value was hand-entered in module manifests.
    /// </remarks>
    internal static string? RegistrationKey(string? businessControllerClass)
    {
        return string.IsNullOrWhiteSpace(businessControllerClass)
            ? null
            : businessControllerClass.Trim().ToLowerInvariant();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Capabilities come from the live registration rather than from a stored column or a directory search,
    /// and the bitmask is reported rather than persisted: the write-back belongs to the caller.
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

            // Zero is a positive statement - "this controller supports none of the three" - and is distinct
            // from the null that means no answer exists. the legacy "not yet determined" state was stored
            // as -1 and screened for before any bit was tested; a closed startup set makes that state
            // unreachable, so -1 is never reported as "unknown".
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
    /// The empty string and <see langword="null"/> are distinct answers and are never substituted for one
    /// another: the first says the controller was asked and had nothing to give, the second says it was
    /// never asked. Assembling a portal template around the payload remains the caller's work, because this
    /// member exchanges text and touches no document type.
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

            if (controller is null)
            {
                return Result.Failure(AbsenceReason(businessControllerClass));
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return Result.Failure(
                    ContentNotSuppliedCode,
                    "No content was supplied, so nothing was restored.");
            }

            IModuleContentPortability? portable = controller as IModuleContentPortability;

            if (portable is null)
            {
                return Result.Failure(
                    ContractNotSupportedCode,
                    "The registered business controller cannot restore content.");
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
    /// One version per call, matching the single-version operation the module itself exposes. the legacy
    /// path split a comma-separated set of applicable versions and looped it, writing one audit entry per
    /// version; a caller reproduces the sequence by invoking this member once per version in the order it
    /// chooses and stopping at the first failed outcome, and it owns the audit entry, which is why the
    /// module's own account of what it did is returned rather than logged here.
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

            return Result<string?>.Success(account ?? string.Empty);
        }
        catch (Exception error) when (IsModuleFault(error))
        {
            return Result<string?>.Failure(
                UpgradeFailedCode,
                Describe(UpgradeOperation, businessControllerClass, error));
        }
    }

    /// <summary>Resolves the controller a stored name selects, from the current scope.</summary>
    /// <param name="businessControllerClass">
    /// The name as stored in the DesktopModules.BusinessControllerClass column.
    /// </param>
    /// <returns>
    /// The controller, or <see langword="null"/> when the name is blank or no registration is filed under
    /// it.
    /// </returns>
    /// <remarks>
    /// The non-generic keyed lookup is used deliberately. The registered service type is not known here -
    /// only the name is - so the request is for the object filed under that key, which the extension method
    /// in this layer's dependency-injection module registers as a factory over the concrete registration.
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
    /// alternative is one module's fault ending an operation that spans several.
    /// </remarks>
    private static bool IsModuleFault(Exception error)
    {
        return error is not (OperationCanceledException or StackOverflowException or OutOfMemoryException);
    }

    /// <summary>Records a module fault privately and returns the fixed detail the caller may be told.</summary>
    /// <param name="operation">Which lifecycle operation was being performed, for the log record.</param>
    /// <param name="businessControllerClass">The controller name, for the log record only.</param>
    /// <param name="error">The exception raised by the module's own code.</param>
    /// <returns>The fixed, caller-safe detail for the failure reason.</returns>
    private string Describe(string operation, string? businessControllerClass, Exception error)
    {
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
    internal interface IModuleContentPortability
    {
        /// <summary>Serialises the content held by one module instance.</summary>
        /// <param name="moduleId">
        /// The module instance whose content is wanted, passed through verbatim: the module key is seeded
        /// at 0, so 0 identifies a real module and must never be read as absent.
        /// </param>
        /// <param name="cancellationToken">Token that cancels the operation.</param>
        /// <returns>
        /// The serialised payload, or an empty string when the module holds nothing worth exporting.
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
        /// The principal on whose behalf the content is restored, which the module records as the author of
        /// whatever it creates.
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

    /// <summary>Implemented by a module's business controller that can contribute items to a search index.</summary>
    internal interface IModuleSearchContribution
    {
    }

    /// <summary>
    /// Implemented by a module's business controller that can migrate its stored content between versions.
    /// </summary>
    /// <remarks>
    /// Replaces the legacy IUpgradeable contract, which the legacy code discovered by run-time type test in
    /// the module event-message processor at:L52 before looping the applicable versions.
    /// </remarks>
    internal interface IModuleContentUpgrade
    {
        /// <summary>Migrates the module's stored content to one named version.</summary>
        /// <param name="version">The single version to migrate to.</param>
        /// <param name="cancellationToken">Token that cancels the operation.</param>
        /// <returns>The module's own account of what it did, which the caller records.</returns>
        Task<string> UpgradeAsync(string version, CancellationToken cancellationToken = default);
    }
}
