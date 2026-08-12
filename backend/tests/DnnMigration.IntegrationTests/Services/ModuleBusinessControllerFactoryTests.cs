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
/// <para>
/// WHY THIS FILE EXISTS. The factory advertises content export and import, and this installation registers
/// no business controller - correctly, because AAP section 0.2.2.2 places every bundled module out of scope,
/// so there is no in-scope module whose controller could be registered and inventing one would ship dead
/// production code. That left the advertised feature never once exercised along its working path: every
/// existing test of export and import observed a refusal, and a refusal is what an empty registration set
/// produces whether or not the resolution mechanism works at all. A mechanism that is only ever seen failing
/// is a mechanism nobody has verified.
/// </para>
/// <para>
/// WHAT MAKES THIS A REAL PROOF RATHER THAN A MOCK. Nothing here is substituted. The subject is the concrete
/// <see cref="ModuleBusinessControllerFactory"/>; the container is a real
/// <see cref="ServiceCollection"/> built into a real provider; the controller is a real class registered
/// with <c>AddKeyedScoped</c> exactly as the composition root's own commentary documents; and the key is the
/// stored controller name normalised by the factory's own rule. What is proven is therefore the whole
/// closed-set path - stored name to registration key to resolved instance to contract type test to
/// invocation - which is the path a deployment admitting a module would take.
/// </para>
/// <para>
/// WHY THE PROOF LIVES HERE AND NOT IN THE INTEGRATION SUITE. The three capability contracts are nested
/// inside the factory, which is <c>internal sealed</c>, so only this assembly can implement them: the
/// infrastructure project grants internals visibility to this test project and to no other, and its project
/// file states plainly that the integration project is excluded on purpose so that an integration test
/// cannot bypass the composition it exists to exercise. Making the contracts public in order to reach them
/// from there would widen a layer's published surface for the sake of a test, which is the opposite of what
/// that bound is for. The integration suite instead pins the behaviour it CAN observe honestly: that a
/// stored controller name the closed set does not cover is refused over HTTP and writes nothing.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class ModuleBusinessControllerFactoryTests
{
    /// <summary>The stored controller name, spelled as a module manifest would spell it.</summary>
    /// <remarks>
    /// Deliberately mixed-case and padded with white space at both ends. The legacy column was hand-entered
    /// in module manifests and matched case-insensitively, whereas keyed resolution compares keys with
    /// ordinary string equality, so the factory normalises both sides through one method. Spelling the
    /// stored name differently from the registration key is what proves that normalisation is applied to the
    /// lookup rather than merely documented.
    /// </remarks>
    private const string StoredControllerName = "  Measured.Modules.AnnouncementsController  ";

    /// <summary>The module instance the content operations name.</summary>
    /// <remarks>Zero, because the module key is seeded at zero and must never be read as absent.</remarks>
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

    /// <summary>
    /// A registered controller's content is exported verbatim.
    /// </summary>
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

    /// <summary>
    /// A registered controller restores content, and the outcome is a bare success.
    /// </summary>
    /// <remarks>
    /// THIS IS THE SUCCESS PATH THE ADVERTISED FEATURE DEPENDS ON, and it is the one the suite previously
    /// never walked. The bare-success assertion is the load-bearing half: the module service commits its unit
    /// of work, evicts the placement caches and writes an <c>Operation=Import</c> audit record on the strength
    /// of a successful outcome, so success here must mean content actually arrived and nothing weaker.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// These four were previously successes carrying an advisory, and the shape was wrong for a measured
    /// reason rather than a stylistic one: the module service reads a successful import as licence to commit,
    /// to evict caches and to write an audit record saying content was imported. An installation whose closed
    /// set did not cover the stored name therefore answered its caller 200 and recorded an import that had
    /// not happened. Refusing makes that unreachable rather than leaving it to a caller to inspect an
    /// advisory it is not obliged to read.
    /// </para>
    /// <para>
    /// The codes stay distinct because an operator acts differently on each: an unregistered name is an
    /// installation that needs code, an unsupported contract is a module that will never do this, a
    /// declared-nothing module is a package question, and an empty payload is the document's problem.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The module is third-party code and its exception message can legitimately carry a connection string, a
    /// path, a statement or the content itself, and the API edge publishes a failure's message verbatim as the
    /// problem document's detail. So the message must be the factory's own fixed sentence, and the module's
    /// explanation belongs in the log alone.
    /// </remarks>
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

    /// <summary>
    /// A stored name is matched case-insensitively and after trimming, as the legacy column was.
    /// </summary>
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

    /// <summary>
    /// A partial name matches nothing, so one module's behaviour can never answer for another's.
    /// </summary>
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

    /// <summary>
    /// Registers one controller under the normalised stored name, in a real container.
    /// </summary>
    /// <typeparam name="TController">The controller implementation to register.</typeparam>
    /// <returns>The built provider, which the caller disposes.</returns>
    /// <remarks>
    /// The registration is exactly the one the composition root documents for admitting a controller: a keyed
    /// registration of <see cref="object"/> filed under the trimmed, lower-cased stored name. Scoped rather
    /// than singleton, matching the production lifetime, so that a controller shares the caller's unit of work.
    /// </remarks>
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

    /// <summary>
    /// A concrete business controller implementing all three lifecycle contracts.
    /// </summary>
    /// <remarks>
    /// This is what a migrated module's companion class looks like: a plain class that implements the
    /// contracts for the capabilities it has and nothing else. It records what it was handed so that the
    /// arguments crossing the boundary can be asserted rather than assumed.
    /// </remarks>
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

    /// <summary>
    /// A registered controller implementing none of the three contracts.
    /// </summary>
    /// <remarks>
    /// A real state rather than a contrived one: a module may register a companion class for its own reasons
    /// while supporting no platform lifecycle contract, and the factory must then say the contract is not
    /// supported instead of failing a cast.
    /// </remarks>
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
