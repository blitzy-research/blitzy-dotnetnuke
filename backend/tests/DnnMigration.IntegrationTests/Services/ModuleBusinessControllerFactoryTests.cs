using DnnMigration.Domain.Common;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>
/// Exercises the module lifecycle factory against a CONCRETE, REGISTERED business controller resolved
/// through a real dependency-injection container.
/// </summary>
/// <remarks>
/// WHY THE PROOF LIVES HERE AND NOT IN THE INTEGRATION SUITE. The three capability contracts are nested
/// inside the factory, which is <c>internal sealed</c>, so only this assembly can implement them: the
/// infrastructure project grants internals visibility to this test project and to no other, and its project
/// file states plainly that the integration project is excluded on purpose so that an integration test
/// cannot bypass the composition it exists to exercise.
/// </remarks>
[Trait("Category", "Integration")]
public class ModuleBusinessControllerFactoryTests
{
    /// <summary>The stored controller name, spelled as a module manifest would spell it.</summary>
    /// <remarks>
    /// Deliberately mixed-case and padded with white space at both ends. The legacy column was hand-entered
    /// in module manifests and matched case-insensitively, whereas keyed resolution compares keys with
    /// ordinary string equality, so the factory normalises both sides through one method.
    /// </remarks>
    private const string StoredControllerName = "  Measured.Modules.AnnouncementsController  ";

    /// <summary>The module instance the content operations name.</summary>
    private const int ModuleId = 0;

    /// <summary>The principal a restore is attributed to.</summary>
    private const int CallerId = 7;

    /// <summary>A payload with enough structure that an accidental substitution would be visible.</summary>
    private const string Payload = "<announcements><item>Measured</item></announcements>";

    /// <summary>
    /// A registered controller reports every contract it implements, using the legacy bit encoding.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SupportedFeatures_ForARegisteredController_ReportsEveryContractItImplements()
    {
        using ServiceProvider provider = Register<MeasuredController>();
        ModuleBusinessControllerFactory factory = Factory(provider);

        Result<int?> features = await factory.GetSupportedFeaturesAsync(StoredControllerName);

        features.IsSuccess.Should().BeTrue(features.Reason?.ToString());

        // 1 | 2 | 4. The values reproduce the legacy DesktopModules.SupportedFeatures encoding exactly, so
        // the number reported here is storable in that column and readable by anything that still reads it.
        features.Value.Should().Be(7);
    }

    /// <summary>
    /// A controller implementing only one contract reports only that one, and zero is a real answer.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SupportedFeatures_ReportsOnlyTheContractsImplemented()
    {
        using ServiceProvider portableOnly = Register<PortableOnlyController>();
        using ServiceProvider inertProvider = Register<InertController>();

        Result<int?> portable = await Factory(portableOnly).GetSupportedFeaturesAsync(StoredControllerName);
        Result<int?> inert = await Factory(inertProvider).GetSupportedFeaturesAsync(StoredControllerName);

        portable.Value.Should().Be(1, "the portable bit alone");

        // Zero is the positive statement "this controller supports none of the three", and is a different
        // answer from the null that means no controller was found. Both are successes; only one has a value.
        inert.Value.Should().Be(0);
    }

    /// <summary>A registered controller's content is exported verbatim.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Export_ForARegisteredController_ReturnsThePayloadVerbatim()
    {
        using ServiceProvider provider = Register<MeasuredController>();

        Result<string?> exported = await Factory(provider)
            .ExportModuleContentAsync(StoredControllerName, ModuleId);

        exported.IsSuccess.Should().BeTrue(exported.Reason?.ToString());
        exported.Value.Should().Be(Payload, "the payload is opaque to the factory and is not reshaped");
        exported.Reason.Should().BeNull("a controller that answered needs no advisory");
    }

    /// <summary>A registered controller restores content, and the outcome is a bare success.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Import_ForARegisteredController_RestoresTheContentAndSucceedsWithNoAdvisory()
    {
        using ServiceProvider provider = Register<MeasuredController>();
        var controller = provider.GetRequiredKeyedService<object>(
            StoredControllerName.Trim().ToLowerInvariant()) as MeasuredController;

        Result imported = await Factory(provider).ImportModuleContentAsync(
            StoredControllerName,
            ModuleId,
            Payload,
            "04.09.00",
            CallerId);

        imported.IsSuccess.Should().BeTrue(imported.Reason?.ToString());
        imported.Reason.Should().BeNull(
            "a success qualified by an advisory saying nothing was restored is the ambiguity this member "
            + "no longer produces");

        // Every argument reaches the controller unaltered, including the module key of zero and the version
        // stamp, because the module interprets a payload written by an older release of itself using them.
        controller.Should().NotBeNull();
        controller!.ImportedModuleId.Should().Be(ModuleId);
        controller.ImportedContent.Should().Be(Payload);
        controller.ImportedVersion.Should().Be("04.09.00");
        controller.ImportedUserId.Should().Be(CallerId);
    }

    /// <summary>
    /// A registered controller migrates its content and its own account of what it did is returned.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Upgrade_ForARegisteredController_ReturnsTheControllersOwnAccount()
    {
        using ServiceProvider provider = Register<MeasuredController>();

        Result<string?> upgraded = await Factory(provider)
            .UpgradeModuleAsync(StoredControllerName, "04.09.00");

        upgraded.IsSuccess.Should().BeTrue(upgraded.Reason?.ToString());
        upgraded.Value.Should().Be("migrated to 04.09.00");
    }

    /// <summary>
    /// Every state in which content could not be restored is a FAILURE, each carrying its own code.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Import_WhenTheControllerCouldNotBeAsked_Fails()
    {
        using ServiceProvider registered = Register<MeasuredController>();
        using ServiceProvider unsupported = Register<InertController>();

        // (1) No controller declared at all.
        Result notSpecified = await Factory(registered).ImportModuleContentAsync(
            businessControllerClass: "   ",
            ModuleId,
            Payload,
            version: null,
            CallerId);

        // (2) A name the closed registration set does not hold. This is the state an installation with an
        //     empty set is always in, which is why it is the one that mattered most.
        Result notRegistered = await Factory(registered).ImportModuleContentAsync(
            "Some.Other.Controller",
            ModuleId,
            Payload,
            version: null,
            CallerId);

        // (3) A registered controller that cannot restore content.
        Result contractNotSupported = await Factory(unsupported).ImportModuleContentAsync(
            StoredControllerName,
            ModuleId,
            Payload,
            version: null,
            CallerId);

        // (4) A registered, capable controller handed nothing to restore.
        Result contentNotSupplied = await Factory(registered).ImportModuleContentAsync(
            StoredControllerName,
            ModuleId,
            content: "   ",
            version: null,
            CallerId);

        notSpecified.IsFailure.Should().BeTrue();
        notSpecified.Reason!.Code.Should().Be("module.controller.not_specified");

        notRegistered.IsFailure.Should().BeTrue();
        notRegistered.Reason!.Code.Should().Be("module.controller.not_registered");

        contractNotSupported.IsFailure.Should().BeTrue();
        contractNotSupported.Reason!.Code.Should().Be("module.controller.contract_not_supported");

        contentNotSupplied.IsFailure.Should().BeTrue();
        contentNotSupplied.Reason!.Code.Should().Be("module.content.not_supplied");
    }

    /// <summary>
    /// Controller absence is reported before the empty-payload guard, preserving the original order.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Import_WhenBothTheControllerAndThePayloadAreMissing_ReportsTheController()
    {
        using ServiceProvider provider = Register<MeasuredController>();

        Result outcome = await Factory(provider).ImportModuleContentAsync(
            "Some.Other.Controller",
            ModuleId,
            content: null,
            version: null,
            CallerId);

        outcome.Reason!.Code.Should().Be(
            "module.controller.not_registered",
            "a module this installation cannot ask is told so first, even when its document is also empty");
    }

    /// <summary>
    /// A controller whose own restore throws produces a failure whose detail quotes nothing it wrote.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Import_WhenTheControllerThrows_FailsWithoutQuotingIt()
    {
        using ServiceProvider provider = Register<ThrowingController>();

        Result outcome = await Factory(provider).ImportModuleContentAsync(
            StoredControllerName,
            ModuleId,
            Payload,
            version: null,
            CallerId);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("module.content.import_failed");
        outcome.Reason.Message.Should().NotContain(
            ThrowingController.SecretBearingMessage,
            "the module's own text is recorded in the log and never returned to a caller");
        outcome.Reason.Message.Should().Contain("has been recorded");
    }

    /// <summary>A stored name is matched case-insensitively and after trimming, as the legacy column was.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Import_MatchesTheStoredNameCaseInsensitivelyAndTrimmed()
    {
        using ServiceProvider provider = Register<MeasuredController>();

        Result outcome = await Factory(provider).ImportModuleContentAsync(
            "\tMEASURED.MODULES.ANNOUNCEMENTSCONTROLLER  ",
            ModuleId,
            Payload,
            version: null,
            CallerId);

        outcome.IsSuccess.Should().BeTrue(
            outcome.Reason?.ToString() ?? "the stored name was hand-entered in a manifest and is folded");
    }

    /// <summary>A partial name matches nothing, so one module's behaviour can never answer for another's.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Import_DoesNotMatchAPartialName()
    {
        using ServiceProvider provider = Register<MeasuredController>();

        Result outcome = await Factory(provider).ImportModuleContentAsync(
            "Measured.Modules",
            ModuleId,
            Payload,
            version: null,
            CallerId);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(
            "module.controller.not_registered",
            "normalisation folds case and trims; it never admits prefix or fuzzy matching");
    }

    /// <summary>Registers one controller under the normalised stored name, in a real container.</summary>
    /// <typeparam name="TController">The controller implementation to register.</typeparam>
    /// <returns>The built provider, which the caller disposes.</returns>
    private static ServiceProvider Register<TController>()
        where TController : class
    {
        ServiceCollection services = new();
        services.AddKeyedScoped<object, TController>(StoredControllerName.Trim().ToLowerInvariant());

        return services.BuildServiceProvider();
    }

    /// <summary>Builds the concrete factory over a real provider and a silent logger.</summary>
    /// <param name="provider">The container holding the keyed registrations.</param>
    /// <returns>The factory under test.</returns>
    private static ModuleBusinessControllerFactory Factory(IServiceProvider provider)
        => new(provider, NullLogger<ModuleBusinessControllerFactory>.Instance);

    /// <summary>A concrete business controller implementing all three lifecycle contracts.</summary>
    private sealed class MeasuredController
        : ModuleBusinessControllerFactory.IModuleContentPortability,
          ModuleBusinessControllerFactory.IModuleSearchContribution,
          ModuleBusinessControllerFactory.IModuleContentUpgrade
    {
        /// <summary>Gets the module key the restore was asked for, or <see langword="null"/>.</summary>
        public int? ImportedModuleId { get; private set; }

        /// <summary>Gets the payload the restore received.</summary>
        public string? ImportedContent { get; private set; }

        /// <summary>Gets the version stamp the restore received.</summary>
        public string? ImportedVersion { get; private set; }

        /// <summary>Gets the principal the restore was attributed to.</summary>
        public int? ImportedUserId { get; private set; }

        /// <inheritdoc />
        public Task<string> ExportAsync(int moduleId, CancellationToken cancellationToken = default)
            => Task.FromResult(Payload);

        /// <inheritdoc />
        public Task ImportAsync(
            int moduleId,
            string content,
            string? version,
            int userId,
            CancellationToken cancellationToken = default)
        {
            ImportedModuleId = moduleId;
            ImportedContent = content;
            ImportedVersion = version;
            ImportedUserId = userId;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<string> UpgradeAsync(string version, CancellationToken cancellationToken = default)
            => Task.FromResult($"migrated to {version}");
    }

    /// <summary>A controller that can carry content but neither indexes nor migrates.</summary>
    private sealed class PortableOnlyController : ModuleBusinessControllerFactory.IModuleContentPortability
    {
        /// <inheritdoc />
        public Task<string> ExportAsync(int moduleId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        /// <inheritdoc />
        public Task ImportAsync(
            int moduleId,
            string content,
            string? version,
            int userId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>A registered controller implementing none of the three contracts.</summary>
    private sealed class InertController
    {
    }

    /// <summary>A controller whose restore throws, carrying text that must not reach a caller.</summary>
    private sealed class ThrowingController : ModuleBusinessControllerFactory.IModuleContentPortability
    {
        /// <summary>Text standing in for whatever a third-party module might quote in a message.</summary>
        internal const string SecretBearingMessage =
            "Server=db;User Id=sa;Password=DoNotPublishThis;";

        /// <inheritdoc />
        public Task<string> ExportAsync(int moduleId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(SecretBearingMessage);

        /// <inheritdoc />
        public Task ImportAsync(
            int moduleId,
            string content,
            string? version,
            int userId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(SecretBearingMessage);
    }
}
