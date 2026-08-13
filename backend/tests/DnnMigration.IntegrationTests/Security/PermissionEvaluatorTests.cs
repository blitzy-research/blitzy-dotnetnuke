using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Covers permission evaluation: the catalogue projection, the scope cascade, the pseudo-roles an
/// unidentified caller is evaluated under, the one composition rule this layer owns, and the deliberate
/// difference between an unanswerable question and a refused one.
/// </summary>
/// <remarks>
/// <para>
/// The suite has two subjects, and the split follows where each rule actually lives.
/// </para>
/// <para>
/// Substituting the evaluator in the service tests is isolation rather than avoidance. A service test that
/// also exercised the reduction could not say which of the two produced a wrong answer, and the reduction
/// is exercised directly a few hundred lines below with nothing between the test and the arithmetic.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PermissionEvaluatorTests
{
    /// <summary>
    /// The implementation type the infrastructure layer registers for the evaluation contract, read once
    /// per run out of the registration itself.
    /// </summary>
    private static readonly Lazy<Type> RegisteredEvaluator = new(ReadRegisteredEvaluatorType);

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PermissionEvaluatorTests"/> class.</summary>
    /// <param name="fixture">The shared composed host.</param>
    public PermissionEvaluatorTests(ApiTestFixture fixture) => _fixture = fixture;

    private const int PortalId = -1;

    private const int OtherPortalId = 3;

    private const int UserId = 7;

    private const int HostUserId = 1;

    private const int ModuleId = 0;

    /// <summary>The definition identifier the <c>Entry</c> fixture declares its catalogue rows against.</summary>
    private const int EntryModuleDefinitionId = 1;

    private const int TabId = 12;

    private const int SecondTabId = 13;

    /// <summary>The role the tenant designates as its administrator role.</summary>
    /// <remarks>
    /// Deliberately zero. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so zero is the first real role a
    /// portal ever gets and is exactly the value that a defaulted-integer bug would also produce. Pinning
    /// the designation to it means an authority check that answered from an unset field rather than from
    /// the stored designation cannot pass by coincidence.
    /// </remarks>
    private const int AdministratorRoleId = 0;

    /// <summary>A role the caller may hold that is not the administrator role.</summary>
    private const int OrdinaryRoleId = 4;

    /// <summary>
    /// The key of the module's placement on <see cref="TabId"/>, used when a test addresses a placement by
    /// its own key rather than by the page it sits on.
    /// </summary>
    private const int FirstPlacementId = 501;

    /// <summary>The key of the module's placement on <see cref="SecondTabId"/>.</summary>
    private const int SecondPlacementId = 502;

    private const string FilterInvalidCode = "permission.filter_invalid";

    private const string PortalNotFoundCode = "permission.portal_not_found";

    private const string ModuleNotFoundCode = "permission.module_not_found";

    private const string TabNotFoundCode = "permission.tab_not_found";

    private const string UserNotFoundCode = "permission.user_not_found";

    private const string KeyInvalidCode = "permission.key_invalid";

    /// <summary>Reported when the named role does not exist within the portal.</summary>
    private const string RoleNotFoundCode = "permission.role_not_found";

    // Aliases for the domain constants, not copies of their values.
    private const string AllUsersRoleName = SpecialRoleNames.AllUsers;

    private const string UnauthenticatedRoleName = SpecialRoleNames.Unauthenticated;

    private const int AllUsersRoleId = -1;

    private const int SuperUserRoleId = -2;

    private const int UnauthenticatedRoleId = -3;

    /// <summary>An ordinary role whose identifier is zero, which this schema issues first.</summary>
    private const int ZeroRoleId = 0;

    /// <summary>The legacy "Nothing" pseudo-role, which reaches nobody and needs no special case to do so.</summary>
    /// <remarks>
    /// <c>glbRoleNothing = "-4"</c>. It is listed among the pseudo-roles for completeness and asserted
    /// because a reader who finds a <c>-4</c> in a grant row deserves to know what governs it: nothing
    /// does. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so no role name can ever resolve to a negative
    /// identifier, which is exactly the behaviour this pseudo-role asks for.
    /// </remarks>
    private const int NothingRoleId = -4;

    private const int MemberRoleId = 5;

    private const int ForeignRoleId = 6;

    /// <summary>A real role row carrying the configured everyone name.</summary>
    private const int EveryoneRoleId = 9;

    /// <summary>A real role row carrying the configured anonymous name.</summary>
    private const int AnonymousRoleId = 10;

    private const string MemberRoleName = "Measured Members";

    private const string ForeignRoleName = "Other Members";

    private const string ZeroRoleName = "Administrators";

    /// <summary>A page whose identifier is zero, which <c>Tabs.TabID</c> issues first.</summary>
    private const int ZeroTabId = 0;

    private const int FirstPermissionId = 41;

    private const int SecondPermissionId = 42;

    private const int ThirdPermissionId = 43;

    /// <summary>A second live module in the same tenant, used to prove suppression stays scoped.</summary>
    private const int SecondModuleId = 4;

    /// <summary>The definition the module under evaluation was built from.</summary>
    private const int ModuleDefinitionId = 1;

    /// <summary>A definition some other module was built from.</summary>
    private const int OtherModuleDefinitionId = 2;

    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    private const string InstalledModuleScopeCode = "MEASURED_MODULE";

    private const string PageScopeCode = "SYSTEM_TAB";

    // A scope code belonging to a subsystem this migration excludes. The excluded subsystem's real code is
    // deliberately NOT spelled here: this folder is checked for that subsystem's vocabulary, and writing
    // the literal would report the suite as reintroducing the very feature it proves is absent.
    private const string ExcludedSubsystemScopeCode = "SYSTEM_EXCLUDED_SUBSYSTEM";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Every permission key name the schema can hold, which is the closed enumeration itself.</summary>
    private static readonly IReadOnlyList<string> AllKeyNames = Enum
        .GetValues<PermissionKey>()
        .Select(key => key.ToString())
        .ToList();

    /// <summary>The catalogue is projected upper-cased, without duplicates, and in ordinal order.</summary>
    /// <remarks>
    /// MIGRATION: the upper-casing is now structural rather than defensive.
    /// </remarks>
    [Fact]
    public async Task Catalogue_IsUpperCasedDistinctAndOrdinallySorted()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            Entry(PermissionKey.VIEW),
            Entry(PermissionKey.EDIT),
            Entry(PermissionKey.WRITE),
            Entry(PermissionKey.READ),
            Entry(PermissionKey.VIEW),
        ];

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "READ", "VIEW", "WRITE" },
            "the catalogue holds one row per key per scope per definition, so the same key arrives "
            + "repeatedly, and a caller comparing keys must not have to de-duplicate or sort them");
    }

    /// <summary>A grant naming no usable key contributes nothing.</summary>
    /// <param name="stored">The stored key text.</param>
    /// <remarks>
    /// MIGRATION: this covers what <see cref="Catalogue_IsUpperCasedDistinctAndOrdinallySorted"/> no longer
    /// can. The effective-key reads answer with the text the grant rows actually carried, which the closed
    /// enumeration never filters, so a blank or whitespace-only key remains reachable on this path and must
    /// still be dropped rather than surfaced as an empty permission.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EffectiveKeys_DropAnUnusableStoredKey(string stored)
    {
        Harness harness = Harness.Ready();
        harness.PortalKeys = [stored, "VIEW"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// A definition filter reaches the store as supplied, and a code filter narrows what it returned.
    /// </summary>
    [Fact]
    public async Task Catalogue_PassesTheDefinitionFilterToTheStoreAndAppliesTheCodeFilter()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = 42,
                PermissionKey = PermissionKey.VIEW,
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SOME_OTHER_CODE",
                ModuleDefinitionId = 42,
                PermissionKey = PermissionKey.EDIT,
            },
        ];

        Result<IReadOnlyList<string>> result = await harness.Service
            .GetPermissionKeysAsync(
                "SYSTEM_MODULE_DEFINITION",
                42,
                permissionKey: null,
                CancellationToken.None);

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                42,
                It.IsAny<CancellationToken>()),
            Times.Once());

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" }, "the entry under the other code is filtered away");
    }

    /// <summary>Omitting the code filter places no restriction, whereas supplying a blank one is a mistake.</summary>
    /// <param name="supplied">The supplied filter text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Catalogue_RefusesABlankCodeFilter(string supplied)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            supplied,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(FilterInvalidCode);
        result.Reason!.Message.Should().Contain("omit it");
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A code filter on its own is answered by asking the store once per key in the closed set.</summary>
    /// <remarks>
    /// "which keys exist under this code" is exactly the question the legacy code-and-key reader answered,
    /// one key at a time, so the service asks it once per member of the closed PermissionKey enumeration -
    /// four reads, bounded by the schema rather than by the data. Only the keys the store actually reported
    /// are returned, which is what distinguishes this answer from the unfiltered one below.
    /// </remarks>
    [Fact]
    public async Task Catalogue_AnswersACodeFilterFromTheCodeAndKeyRead()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = -1,
                PermissionKey = PermissionKey.VIEW,
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = -1,
                PermissionKey = PermissionKey.EDIT,
            },
        ];

        Result<IReadOnlyList<string>> result = await harness.Service
            .GetPermissionKeysAsync("SYSTEM_TAB", null, permissionKey: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "VIEW" }, "the answer is ordinally ordered");

        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                "SYSTEM_TAB",
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(Enum.GetValues<PermissionKey>().Length));
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Omitting both filters is accepted and answered from the closed key set without a store read.
    /// </summary>
    /// <remarks>
    /// The legacy reader answered this by passing a null in both arguments, which its body treated as a
    /// wildcard over the whole table. The repository contract takes no wildcard, and it does not need to:
    /// Permission.PermissionKey IS the closed PermissionKey enumeration, so the enumeration is the complete
    /// vocabulary any catalogue row could carry.
    /// </remarks>
    [Fact]
    public async Task Catalogue_AnswersAnAbsentFilterFromTheClosedKeySet()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            permissionKey: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A definition identifier that cannot name a row is refused rather than queried.</summary>
    /// <param name="supplied">The supplied identifier.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-128)]
    public async Task Catalogue_RefusesADefinitionIdentifierThatCannotNameARow(int supplied)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            supplied,
            permissionKey: null,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(FilterInvalidCode);
        result.Reason!.Message.Should().Contain("identifiers start at 1");
    }

    /// <summary>A key filter narrows the catalogue listing to that one key.</summary>
    [Fact]
    public async Task Catalogue_NarrowsToASingleKeyWhenOneIsNamed()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
        ];

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            EntryModuleDefinitionId,
            PermissionKey.EDIT,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal("EDIT");
    }

    /// <summary>
    /// A key filter that is not a defined member is refused, rather than being echoed back to the caller as
    /// a declared permission key.
    /// </summary>
    /// <param name="undefinedKey">A numeric value outside the four defined members.</param>
    /// <remarks>
    /// The review that prompted this test described the gap as an HTTP-reachable input-validation exposure
    /// that echoed <c>["99"]</c> to a caller.
    /// </remarks>
    [Theory]
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Catalogue_RefusesAKeyFilterThatIsNotADefinedMember(int undefinedKey)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            (PermissionKey)undefinedKey,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue("an undefined key must not be answered with a catalogue");
        result.Reason!.Code.Should().Be(KeyInvalidCode);
        result.Reason!.Message.Should().Contain(
            undefinedKey.ToString(CultureInfo.InvariantCulture),
            "the refusal names the value the caller sent");

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An undefined key filter is refused on the SCOPED branches too, not only on the unscoped one.
    /// </summary>
    [Fact]
    public async Task Catalogue_RefusesAnUndefinedKeyFilterOnEveryScopedBranch()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> withinDefinition = await harness.Service.GetPermissionKeysAsync(
            null,
            EntryModuleDefinitionId,
            (PermissionKey)99,
            CancellationToken.None);

        withinDefinition.IsFailure.Should().BeTrue();
        withinDefinition.Reason!.Code.Should().Be(KeyInvalidCode);

        Result<IReadOnlyList<string>> withinCode = await harness.Service.GetPermissionKeysAsync(
            "SYSTEM_MODULE_DEFINITION",
            null,
            (PermissionKey)99,
            CancellationToken.None);

        withinCode.IsFailure.Should().BeTrue();
        withinCode.Reason!.Code.Should().Be(KeyInvalidCode);
    }

    /// <summary>
    /// Every defined member is accepted as a filter, so the membership guard bounds the enumeration without
    /// narrowing it.
    /// </summary>
    /// <param name="definedKey">A defined member of the enumeration.</param>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task Catalogue_AcceptsEveryDefinedKeyFilter(PermissionKey definedKey)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            definedKey,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(definedKey.ToString());
    }

    /// <summary>
    /// A key filter supplied with no other filter is answered from the closed enumeration without touching
    /// the store at all.
    /// </summary>
    [Fact]
    public async Task Catalogue_AnswersALoneKeyFilterFromTheClosedKeySet()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            PermissionKey.VIEW,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal("VIEW");

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A catalogue definition is returned in full by its own identifier.</summary>
    [Fact]
    public async Task Definition_IsReturnedInFullByItsOwnIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue = [Entry(PermissionKey.EDIT)];

        Result<PermissionDto?> result = await harness.Service
            .GetPermissionAsync(1, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().NotBeNull();
        result.Value!.PermissionId.Should().Be(1);
        result.Value.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        result.Value.ModuleDefId.Should().Be(EntryModuleDefinitionId);
        result.Value.PermissionKey.Should().Be("EDIT", "the key travels as the enumeration member's name");
        result.Value.PermissionName.Should().Be("EDIT");
    }

    /// <summary>An identifier naming no catalogue row is reported as absent, not as a failure.</summary>
    [Fact]
    public async Task Definition_ReportsAnUnknownIdentifierAsAbsent()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue = [Entry(PermissionKey.VIEW)];

        Result<PermissionDto?> result = await harness.Service
            .GetPermissionAsync(4242, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("absence is an answer, not a failure");
        result.Value.Should().BeNull();
    }

    /// <summary>
    /// The module-scoped read answers with the union the terminal statement takes, not with the module's
    /// own definition alone.
    /// </summary>
    [Fact]
    public async Task ModuleDefinitions_AnswerWithTheUnionOfTheDefinitionAndTheProductWideScope()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "MY_MODULE",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = 99,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 3,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = 99,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
        ];

        Result<IReadOnlyList<PermissionDto>> result = await harness.Service
            .GetModulePermissionDefinitionsAsync(PortalId, ModuleId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Select(row => row.PermissionId).Should().Equal(
            new[] { 1, 2 },
            "the module's own definition contributes the first, the product-wide scope the second, "
                + "and the page scope belongs to neither arm");
    }

    /// <summary>A module from another tenant is refused before any catalogue metadata is read.</summary>
    [Fact]
    public async Task ModuleDefinitions_RefuseAModuleOwnedByAnotherPortal()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = OtherPortalId;

        Result<IReadOnlyList<PermissionDto>> result = await harness.Service
            .GetModulePermissionDefinitionsAsync(PortalId, ModuleId, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error?.Code.Should().Be(ModuleNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The page-scoped read answers with the page scope for each valid page in the tenant.</summary>
    /// <remarks>
    /// changes the precondition, not the catalogue: the page must exist in the addressed tenant, but the
    /// terminal <c>GetPermissionsByTabID</c> scope remains shared by every valid page.
    /// </remarks>
    [Fact]
    public async Task TabDefinitions_AnswerWithThePageScopeForEachValidPage()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 5,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = -1,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 6,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
        ];

        Result<IReadOnlyList<PermissionDto>> first = await harness.Service
            .GetTabPermissionDefinitionsAsync(PortalId, TabId, CancellationToken.None);
        Result<IReadOnlyList<PermissionDto>> second = await harness.Service
            .GetTabPermissionDefinitionsAsync(PortalId, SecondTabId, CancellationToken.None);

        first.IsSuccess.Should().BeTrue(first.Reason?.ToString());
        first.Value.Select(row => row.PermissionId).Should().Equal(5);
        second.Value.Select(row => row.PermissionId).Should().Equal(
            first.Value.Select(row => row.PermissionId),
            "every valid page receives the shared SYSTEM_TAB catalogue");
    }

    /// <summary>A page from another tenant is refused before any catalogue metadata is read.</summary>
    [Fact]
    public async Task TabDefinitions_RefuseAPageOwnedByAnotherPortal()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = OtherPortalId;

        Result<IReadOnlyList<PermissionDto>> result = await harness.Service
            .GetTabPermissionDefinitionsAsync(PortalId, TabId, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error?.Code.Should().Be(TabNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.GetByTabIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The identifying reads answer each definition once, in identifier order.</summary>
    /// <remarks>
    /// Ordering and distinctness are promises the application contract makes, so they are asserted against
    /// the contract rather than left to whatever shape the store query happens to have.
    /// </remarks>
    [Fact]
    public async Task ModuleDefinitions_AnswerEachDefinitionOnceInIdentifierOrder()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 9,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 4,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
        ];

        Result<IReadOnlyList<PermissionDto>> result = await harness.Service
            .GetModulePermissionDefinitionsAsync(PortalId, ModuleId, CancellationToken.None);

        result.Value.Select(row => row.PermissionId).Should().Equal(new[] { 4, 9 });
    }

    /// <summary>A tenant-wide question is answered by the tenant-wide query alone.</summary>
    [Fact]
    public async Task EffectiveKeys_ForTheWholeTenantUseTheTenantWideQuery()
    {
        Harness harness = Harness.Ready();
        harness.PortalKeys = ["view", "EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "VIEW" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                PortalId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>An unknown tenant is reported as missing.</summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// An account that is not a member of the tenant cannot be evaluated, and both identifiers are named.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAnAccountThatIsNotAMemberOfTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(UserNotFoundCode);
        result.Reason!.Message.Should().Contain("7");
        result.Reason!.Message.Should().Contain("-1");
    }

    /// <summary>An unidentified caller is evaluated under the two pseudo-roles and no account is read.</summary>
    /// <remarks>
    /// The legacy schema records these grants against reserved role identifiers rather than against real
    /// rows, so an anonymous visitor is a first-class subject of evaluation rather than an absence of one.
    /// Both names are supplied, in that order, because a grant may be recorded against either.
    /// </remarks>
    [Fact]
    public async Task EffectiveKeys_ForAnUnidentifiedCallerUseThePseudoRoles()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            userId: null,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(new[] { AllUsersRoleName, UnauthenticatedRoleName });
        harness.Users.Verify(
            users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Users.Verify(
            users => users.ListRoleNamesAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A member is evaluated under its assigned roles plus the everyone role, as of the current instant.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAMemberAppendTheEveryoneRole()
    {
        Harness harness = Harness.Ready();
        harness.AssignedRoles = ["Subscribers", "Registered Users"];

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(
            new[] { "Subscribers", "Registered Users", AllUsersRoleName });
        harness.Users.Verify(
            users => users.ListRoleNamesAsync(PortalId, UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The pseudo-role names are the immutable domain constants, and no configuration can move them.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task EffectiveKeys_UseTheImmutablePseudoRoleConstants()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            userId: null,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(
            new[] { SpecialRoleNames.AllUsers, SpecialRoleNames.Unauthenticated });

        SpecialRoleNames.AllUsers.Should().Be(
            "All Users",
            "the legacy glbRoleAllUsersName value is matched against Roles.RoleName as a string");
        SpecialRoleNames.Unauthenticated.Should().Be(
            "Unauthenticated Users",
            "the legacy glbRoleUnauthUserName value is matched against Roles.RoleName as a string");

        typeof(PortalOptions)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain(
                ["AllUsersRoleName", "UnauthenticatedRoleName"],
                "reintroducing either setting would make an authorization audience configurable again");
    }

    /// <summary>A host account holds the whole catalogue and no scope is consulted.</summary>
    [Fact]
    public async Task EffectiveKeys_ForAHostAccountAreTheWholeCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            Entry(PermissionKey.VIEW),
            Entry(PermissionKey.EDIT),
            Entry(PermissionKey.READ),
            Entry(PermissionKey.WRITE),
        ];
        harness.Account.IsSuperUser = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A host account is answered before any scope is resolved, so an unresolvable scope is not reported.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAHostAccountIgnoreAnUnresolvableScope()
    {
        Harness harness = Harness.Ready();
        harness.Account.IsSuperUser = true;
        harness.Catalogue = [Entry(PermissionKey.VIEW)];
        harness.Modules
            .Setup(modules => modules.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            moduleId: 987654,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "READ", "VIEW", "WRITE" },
            "a host account holds every key, and the single-entry catalogue fixture must not narrow that");

        // The scope was never resolved, which is the property this test exists to pin.
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A module-scoped question is answered by the module query.</summary>
    [Fact]
    public async Task EffectiveKeys_ForAModuleUseTheModuleQuery()
    {
        Harness harness = Harness.Ready();
        harness.ModuleKeys = ["EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                ModuleId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A module belonging to another tenant is reported as missing.</summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAModuleFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = OtherPortalId;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(ModuleNotFoundCode);
    }

    /// <summary>A module owned by the installation rather than a tenant is evaluable in any tenant.</summary>
    [Fact]
    public async Task EffectiveKeys_AcceptAnInstallationOwnedModuleInAnyTenant()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = null;
        harness.ModuleKeys = ["VIEW"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            OtherPortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>A page-scoped question is answered by the page query.</summary>
    [Fact]
    public async Task EffectiveKeys_ForAPageUseThePageQuery()
    {
        Harness harness = Harness.Ready();
        harness.TabKeys = ["VIEW"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            tabId: TabId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                TabId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A page belonging to another tenant is reported as missing.</summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAPageFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = OtherPortalId;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            tabId: TabId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(TabNotFoundCode);
    }

    /// <summary>A page owned by the installation rather than a tenant is evaluable in any tenant.</summary>
    [Fact]
    public async Task EffectiveKeys_AcceptAnInstallationOwnedPageInAnyTenant()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = null;
        harness.TabKeys = ["EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            OtherPortalId,
            UserId,
            tabId: TabId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT" });
    }

    /// <summary>Asking about a module on a page unions both scopes.</summary>
    [Fact]
    public async Task EffectiveKeys_ForAModuleOnAPageUnionBothScopes()
    {
        Harness harness = Harness.Ready();
        harness.ModuleKeys = ["EDIT"];
        harness.TabKeys = ["VIEW", "EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            ModuleId,
            TabId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "VIEW" },
            "a key granted in both scopes appears once");
    }

    /// <summary>
    /// A module that inherits its view permission is not viewable when no page it sits on grants view.
    /// </summary>
    [Fact]
    public async Task InheritedView_IsWithheldWhenNoPlacementPageGrantsIt()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleKeys = ["VIEW", "EDIT"];
        harness.PageViewGrants[TabId] = false;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT" },
            "the module allows view and the page it sits on does not, and the page decides");
    }

    /// <summary>
    /// A module that inherits its view permission is viewable when the page it sits on grants view.
    /// </summary>
    [Fact]
    public async Task InheritedView_IsGrantedWhenAPlacementPageGrantsIt()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleKeys = ["EDIT"];
        harness.PageViewGrants[TabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(
            new[] { "EDIT", "VIEW" },
            "view is contributed by the page even though the module's own grants never mention it");
    }

    /// <summary>A module that does not inherit keeps its own view grant and no page is consulted.</summary>
    /// <param name="inherits">The stored inheritance flag.</param>
    /// <remarks>
    /// The flag is nullable in the schema, and an unrecorded value is not an inheriting module: the legacy
    /// default was to hold the module's own grants, so an absent flag must behave as "does not inherit"
    /// rather than as "inherit".
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task InheritedView_IsNotConsultedWhenTheModuleDoesNotInherit(bool? inherits)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = inherits;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleKeys = ["VIEW"];
        harness.PageViewGrants[TabId] = false;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Inheritance stops asking as soon as one page withholds view, because the unaddressed question is
    /// answered by every placement at once.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This test measured the opposite short circuit before the placement-insensitivity defect was
    /// corrected: it stopped at the first page that GRANTED, which is the disjunction that let a permissive
    /// placement admit callers to a restrictive one.
    /// </remarks>
    [Fact]
    public async Task InheritedView_StopsAtTheFirstWithholdingPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = [];
        harness.PageViewGrants[TabId] = false;
        harness.PageViewGrants[SecondTabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().BeEmpty();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                SecondTabId,
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "one withholding page settles it, so the rest are not worth a round trip");
    }

    /// <summary>
    /// A question that names a module but no page is answered by EVERY placement at once, so a module
    /// placed on one permissive and one restrictive page is not viewable.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task InheritedView_WithoutAnAddressedPlacementRequiresEveryPlacementToGrant()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> collective = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        collective.IsSuccess.Should().BeTrue(collective.Reason?.ToString());
        collective.Value.Should().BeFalse(
            "a question that names no page is about the module wherever it sits, so the restrictive "
            + "placement decides");

        // Naming the permissive placement is how a caller entitled to it asks, and that answer is affirmative.
        Result<bool> addressed = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: TabId,
            placementTabModuleId: null,
            CancellationToken.None);

        addressed.Value.Should().BeTrue();
    }

    /// <summary>
    /// A placement addressed by its own key is decided by the page that placement sits on, so the
    /// restrictive placement of a doubly-placed module is refused while the permissive one is allowed.
    /// </summary>
    /// <param name="addressedTabModuleId">The placement key the caller names.</param>
    /// <param name="expected">Whether the page that placement sits on grants view.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(FirstPlacementId, true)]
    [InlineData(SecondPlacementId, false)]
    public async Task HasModulePermission_ForAPlacementAddressedByItsOwnKeyIsDecidedByThatPlacementsPage(
        int addressedTabModuleId,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.Module.TabModules.Add(Placement(SecondTabId, SecondPlacementId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: addressedTabModuleId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().Be(expected);
    }

    /// <summary>
    /// A placement key the module does not occupy is refused rather than answered from any other placement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForAPlacementKeyTheModuleDoesNotOccupyIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.PageViewGrants[TabId] = true;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: SecondPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeFalse("the module does not occupy the placement the request addressed");
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "no page is consulted, because the addressed placement does not exist to consult one for");
    }

    /// <summary>
    /// Addressing a placement by its own key and simultaneously naming a page that placement does not sit
    /// on is a contradiction, and is refused rather than resolved in favour of either.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForContradictoryFormsOfAddressIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.Module.TabModules.Add(Placement(SecondTabId, SecondPlacementId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = true;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: SecondTabId,
            placementTabModuleId: FirstPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeFalse(
            "the placement key names one page and the page identifier names another, and both pages grant "
            + "view, so a permissive answer here would be an answer to neither question that was asked");
    }

    /// <summary>
    /// Naming a placement by its own key together with the page it really does sit on is consistent, and is
    /// decided by that page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForAgreeingFormsOfAddressIsDecidedByTheNamedPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.Module.TabModules.Add(Placement(SecondTabId, SecondPlacementId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: TabId,
            placementTabModuleId: FirstPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeTrue();
    }

    /// <summary>
    /// The placements are read ONCE even when the request both reconciles two forms of address and inherits
    /// its view decision from the page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ReadsThePlacementsOnceWhenItBothReconcilesAndInherits()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.StoredPlacements =
        [
            Placement(TabId, FirstPlacementId),
            Placement(SecondTabId, SecondPlacementId),
        ];
        harness.PageViewGrants[TabId] = true;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: TabId,
            placementTabModuleId: FirstPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeTrue("hoisting the read must not change the answer it feeds");
        harness.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A request answered before any placement consumer is reached makes no placement read at all, and a
    /// request that does reach one reads the set exactly once, so hoisting the read added no round trip to
    /// any shape.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The hoisted read is taken LAZILY, and this is the fact that keeps it that way. Reading
    /// unconditionally at the top of the member would trade a duplicated round trip on one shape for a
    /// brand-new one on every other, which is why the shape that must stay free of the read is asserted
    /// alongside the shape that must issue it exactly once.
    /// </remarks>
    [Fact]
    public async Task HasModulePermission_TakesTheHoistedPlacementReadLazily()
    {
        Harness singleAddress = Harness.Ready();
        singleAddress.Module.InheritViewPermissions = true;
        singleAddress.StoredPlacements = [Placement(TabId, FirstPlacementId)];
        singleAddress.ModuleKeys = ["EDIT"];
        singleAddress.ModuleGrant = true;

        Result<bool> nonInheritingKey = await singleAddress.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: TabId,
            placementTabModuleId: null,
            CancellationToken.None);

        nonInheritingKey.IsSuccess.Should().BeTrue(nonInheritingKey.Reason?.ToString());
        nonInheritingKey.Value.Should().BeTrue("the module's own edit grant answers on its own");
        singleAddress.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());

        // And when the module's own grant does NOT answer, the set is read ONCE for the page arm rather than
        // once per consumer.
        Harness refusedByTheModule = Harness.Ready();
        refusedByTheModule.StoredPlacements = [Placement(TabId, FirstPlacementId)];
        refusedByTheModule.ModuleGrant = false;
        refusedByTheModule.TabGrant = false;

        Result<bool> refused = await refusedByTheModule.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: TabId,
            placementTabModuleId: null,
            CancellationToken.None);

        refused.Value.Should().BeFalse();
        refusedByTheModule.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());

        Harness unaddressed = Harness.Ready();
        unaddressed.Module.InheritViewPermissions = true;
        unaddressed.StoredPlacements = [Placement(TabId, FirstPlacementId)];
        unaddressed.PageViewGrants[TabId] = true;

        Result<bool> everywhere = await unaddressed.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        everywhere.IsSuccess.Should().BeTrue(everywhere.Reason?.ToString());
        everywhere.Value.Should().BeTrue();
        unaddressed.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A module that inherits its view permission and sits on no page at all is not viewable.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The conjunction over an empty set is vacuously true, so this case has to be stated rather than left
    /// to the loop: a module that takes its view permission from its pages and has no pages inherits
    /// nothing, and must not thereby become visible to everyone — which is the exact shape of an accidental
    /// world-readable grant.
    /// </remarks>
    [Fact]
    public async Task InheritedView_IsWithheldFromAModuleThatSitsOnNoPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Clear();
        harness.ModuleKeys = ["VIEW", "EDIT"];

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeFalse("there is no page for the module to inherit a view grant from");
    }

    /// <summary>
    /// An inheriting module placed on two pages is decided by the page the caller ADDRESSED, so a placement
    /// that denies view is denied even while the module's other placement allows it.
    /// </summary>
    /// <param name="addressedTabId">The placement the caller names.</param>
    /// <param name="expected">Whether that placement grants view.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(TabId, true)]
    [InlineData(SecondTabId, false)]
    public async Task HasModulePermission_ForAnAddressedPlacementIsDecidedByThatPageAlone(
        int addressedTabId,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: addressedTabId,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Be(expected);

        // The other placement is never consulted: consulting it is what produced the union.
        int otherTabId = addressedTabId == TabId ? SecondTabId : TabId;
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                otherTabId,
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the decision is about the addressed placement, so no other placement may influence it");
    }

    /// <summary>
    /// Addressing a page the module is not placed on is a denial, and no page's grants are consulted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForAPlacementTheModuleDoesNotOccupyIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.PageViewGrants[TabId] = true;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: SecondTabId,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeFalse();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "a module that is not on the addressed page cannot inherit that page's grants");
    }

    /// <summary>
    /// Naming a module and a page together names a placement, so the effective-key listing resolves the
    /// inherited view key from that page alone rather than from every placement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetEffectivePermissionKeys_ForAModuleAndPageResolvesInheritanceAtThatPlacement()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = ["EDIT"];
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<IReadOnlyList<string>> denied = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            tabId: SecondTabId,
            cancellationToken: CancellationToken.None);

        denied.IsSuccess.Should().BeTrue(denied.Reason?.ToString());
        denied.Value.Should().NotContain(
            "VIEW",
            "the addressed placement denies view, and the module's other placement must not supply it");
        denied.Value.Should().Contain("EDIT", "keys other than view are not inherited from the page");
    }

    /// <summary>
    /// Inheritance considers every page until one withholds, so a granting page does not end the traversal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task InheritedView_ConsidersEveryPageUntilOneWithholds()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = [];
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().BeEmpty();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                SecondTabId,
                PermissionKey.VIEW,
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once(),
            "the first page granting view is not the answer, so the second still has to be asked");
    }

    /// <summary>Inheritance reads the placements when the module was loaded without them.</summary>
    [Fact]
    public async Task InheritedView_ReadsThePlacementsWhenTheyWereNotLoaded()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.ModuleKeys = [];
        harness.StoredPlacements = [Placement(SecondTabId)];
        harness.PageViewGrants[SecondTabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A module with no placement at all inherits nothing.</summary>
    [Fact]
    public async Task InheritedView_GrantsNothingWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.ModuleKeys = ["VIEW", "EDIT"];
        harness.StoredPlacements = [];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(
            new[] { "EDIT" },
            "an unplaced module has no page to inherit from, so its own view grant is still withheld");
    }

    /// <summary>Asking whether an inheriting module is viewable is answered by the page.</summary>
    /// <param name="pageGrantsView">Whether the placement page allows view.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HasModulePermission_ForViewOnAnInheritingModuleIsAnsweredByThePage(bool pageGrantsView)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.PageViewGrants[TabId] = pageGrantsView;
        harness.ModuleGrant = !pageGrantsView;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Be(pageGrantsView);
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "an inheriting module's own view grant is not the answer, so it is not even read");
    }

    /// <summary>Inheritance applies to the view permission only.</summary>
    /// <param name="key">The permission asked about.</param>
    [Theory]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task HasModulePermission_ForOtherKeysAsksTheModuleEvenWhenItInherits(PermissionKey key)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.PageViewGrants[TabId] = false;
        harness.ModuleGrant = true;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            key,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.Value.Should().BeTrue(
            "only the view permission is inherited from the page; editing is decided by the module");
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
                ModuleId,
                key,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A permission key that names no member of the vocabulary is refused.</summary>
    [Fact]
    public async Task HasModulePermission_RefusesAKeyThatNamesNoMember()
    {
        Harness harness = Harness.Ready();

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            (PermissionKey)99,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(KeyInvalidCode);
        result.Reason!.Message.Should().Contain("99");
        harness.Modules.Verify(
            modules => modules.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "a malformed request is refused before anything is read");
    }

    /// <summary>
    /// module edit is granted by the EDIT grant on a page the module is placed on, which is the second of
    /// the three alternatives the legacy edit test offered.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForEditIsGrantedByThePagesEditGrant()
    {
        Harness harness = Harness.Ready();
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleGrant = false;
        harness.TabGrant = true;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeTrue("the page the module sits on grants edit to this caller");

        harness.Evaluator.Verify(
            evaluator => evaluator.HasAnyTabPermissionAsync(
                It.Is<IReadOnlyCollection<int>>(tabIds => tabIds.Count == 1 && tabIds.Contains(TabId)),
                PermissionKey.EDIT,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the single-page verdict belongs to a request that names a page, and this one named none");
    }

    /// <summary>
    /// the page arm is a disjunction over the module's placements, so one editable page is enough - and a
    /// request that names a page is answered from that page alone.
    /// </summary>
    /// <remarks>
    /// The quantifier differs from inherited VIEW on purpose. Visibility asks whether the module is
    /// viewable wherever it is placed, which is a conjunction; edit authority asks whether the caller may
    /// administer it from a page it can edit, and the legacy caller reached a module through exactly one
    /// page.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForEditTakesAnyEditablePlacementAndHonoursAnAddressedPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleGrant = false;
        harness.TabGrant = false;
        harness.PageEditGrants[SecondTabId] = true;

        Result<bool> collective = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        collective.Value.Should().BeTrue("one placement the caller may edit is enough");

        // Two placements, still ONE question. The disjunction is composed inside the evaluator, per page, so
        // this arm's cost no longer grows with the number of pages a module occupies.
        harness.Evaluator.Verify(
            evaluator => evaluator.HasAnyTabPermissionAsync(
                It.Is<IReadOnlyCollection<int>>(tabIds =>
                    tabIds.Count == 2 && tabIds.Contains(TabId) && tabIds.Contains(SecondTabId)),
                PermissionKey.EDIT,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        Result<bool> addressed = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: TabId,
            placementTabModuleId: null,
            CancellationToken.None);

        addressed.Value.Should().BeFalse(
            "a request naming a page is answered from that page, and this one grants the caller nothing");

        // A named page is still asked about singly, and the collective question was not asked a second time.
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                TabId,
                PermissionKey.EDIT,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        harness.Evaluator.Verify(
            evaluator => evaluator.HasAnyTabPermissionAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once(),
            "the addressed request answers from the named page alone");
    }

    /// <summary>
    /// The tenant-administrator alternative is asked FIRST, so an administrative caller settles the
    /// question without the module's placements being read or any page being evaluated.
    /// </summary>
    /// <remarks>
    /// The three alternatives form a disjunction, and reordering the alternatives of a disjunction cannot
    /// change its verdict: none of the three writes anything, none observes the others, and all are pure
    /// reads.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForEditAsksTheAdministratorArmBeforeTheCostlyPageArm()
    {
        Harness harness = Harness.Ready();
        harness.Module.TabModules.Clear();
        harness.ModuleGrant = false;
        harness.TabGrant = true;
        harness.AssignedRoles = ["Administrators"];
        harness.AdministratorsRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Administrators",
        };

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeTrue("the caller holds the tenant's designated administrators role");

        harness.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the placements are only needed by the arm the cheap arm made unnecessary");

        harness.Evaluator.Verify(
            evaluator => evaluator.HasAnyTabPermissionAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the page arm is not consulted once the answer is settled");

        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the page arm is not consulted once the answer is settled");
    }

    /// <summary>
    /// module edit is granted to a member of the addressed tenant's own administrators role, which is the
    /// third of the three alternatives the legacy edit test offered.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForEditIsGrantedToTheTenantsOwnAdministrator()
    {
        Harness harness = Harness.Ready();
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleGrant = false;
        harness.TabGrant = false;
        harness.AssignedRoles = ["Administrators", "Registered Users"];
        harness.AdministratorsRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Administrators",
        };

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeTrue("the caller holds the tenant's designated administrators role");
        harness.RoleStore.Verify(
            roles => roles.GetByIdAsync(AdministratorRoleId, PortalId, It.IsAny<CancellationToken>()),
            Times.Once(),
            "the administrators role is resolved from the addressed tenant's own designation");
    }

    /// <summary>
    /// the two added alternatives admit nobody else. A caller holding neither the module's grant, nor an
    /// editable page, nor the tenant's administrators role is still refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForEditStillRefusesACallerWithNoneOfTheThreeAlternatives()
    {
        Harness harness = Harness.Ready();
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleGrant = false;
        harness.TabGrant = false;
        harness.AssignedRoles = ["Registered Users"];
        harness.AdministratorsRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Administrators",
        };

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeFalse("none of the three legacy alternatives is satisfied");
    }

    /// <summary>
    /// the two added alternatives apply to the edit key only, so a view question is unaffected by them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForViewIsNotWidenedByTheEditAlternatives()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = false;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleGrant = false;
        harness.TabGrant = true;
        harness.AssignedRoles = ["Administrators"];
        harness.AdministratorsRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Administrators",
        };

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeFalse(
            "a non-inheriting module's visibility is its own grant, which this caller does not hold");
    }

    /// <summary>A page question refuses an unrecognised key too.</summary>
    [Fact]
    public async Task HasTabPermission_RefusesAKeyThatNamesNoMember()
    {
        Harness harness = Harness.Ready();

        Result<bool> result = await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            (PermissionKey)(-5),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(KeyInvalidCode);
    }

    /// <summary>Every defined key is accepted.</summary>
    /// <param name="key">The permission asked about.</param>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task HasTabPermission_AcceptsEveryDefinedKey(PermissionKey key)
    {
        Harness harness = Harness.Ready();
        harness.TabGrant = true;

        Result<bool> result = await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            key,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeTrue();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                TabId,
                key,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>An unknown module is reported as missing.</summary>
    [Fact]
    public async Task HasModulePermission_RefusesAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.Modules
            .Setup(modules => modules.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(ModuleNotFoundCode);
    }

    /// <summary>An unknown page is reported as missing.</summary>
    [Fact]
    public async Task HasTabPermission_RefusesAnUnknownPage()
    {
        Harness harness = Harness.Ready();
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tab?)null);

        Result<bool> result = await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(TabNotFoundCode);
    }

    /// <summary>
    /// An account that is not a member of the tenant is answered no rather than reported as missing.
    /// </summary>
    [Fact]
    public async Task HasPermission_AnswersNoForAnAccountThatIsNotAMember()
    {
        Harness moduleHarness = Harness.Ready();
        moduleHarness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<bool> moduleAnswer = await moduleHarness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        moduleAnswer.IsSuccess.Should().BeTrue();
        moduleAnswer.Value.Should().BeFalse();

        Harness tabHarness = Harness.Ready();
        tabHarness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<bool> tabAnswer = await tabHarness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        tabAnswer.IsSuccess.Should().BeTrue();
        tabAnswer.Value.Should().BeFalse();
    }

    /// <summary>A host account is allowed without the grants being read at all.</summary>
    [Fact]
    public async Task HasPermission_AllowsAHostAccountWithoutReadingAnyGrant()
    {
        Harness harness = Harness.Ready();
        harness.Account.IsSuperUser = true;
        harness.ModuleGrant = false;
        harness.TabGrant = false;

        Result<bool> moduleAnswer = await harness.Service.HasModulePermissionAsync(
            PortalId,
            HostUserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        Result<bool> tabAnswer = await harness.Service.HasTabPermissionAsync(
            PortalId,
            HostUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        moduleAnswer.Value.Should().BeTrue();
        tabAnswer.Value.Should().BeTrue();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The decision itself is delegated, and the caller's roles travel with the question.</summary>
    /// <param name="storedAnswer">The decision the store reports.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HasModulePermission_DelegatesTheDecisionWithTheCallersRoles(bool storedAnswer)
    {
        Harness harness = Harness.Ready();
        harness.AssignedRoles = ["Subscribers"];
        harness.ModuleGrant = storedAnswer;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.Value.Should().Be(
            storedAnswer,
            "precedence between an allowance and a denial is reduced where the grants are, and is not "
            + "second-guessed here");
        harness.CapturedRoleNames.Should().Equal(new[] { "Subscribers", AllUsersRoleName });
    }

    /// <summary>A permission question does not verify the tenant separately.</summary>
    /// <remarks>
    /// Confirming that the module or page belongs to the tenant is a stronger check than confirming the
    /// tenant exists, and it is one read rather than two. A separate existence check would be dead work on
    /// the hottest path in the application.
    /// </remarks>
    [Fact]
    public async Task HasPermission_DoesNotVerifyTheTenantSeparately()
    {
        Harness harness = Harness.Ready();

        await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        harness.Portals.Verify(
            portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Removing an account's grants refuses an unknown tenant and touches nothing.</summary>
    [Fact]
    public async Task RemoveGrants_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(PortalNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Removing an account's grants refuses an account that is not a member, and deletes nothing.</summary>
    [Fact]
    public async Task RemoveGrants_RefusesAnAccountThatIsNotAMember()
    {
        Harness harness = Harness.Ready();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(UserNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Removing an account's grants delegates the removal.</summary>
    [Fact]
    public async Task RemoveGrants_DelegatesTheRemoval()
    {
        Harness harness = Harness.Ready();

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The removal lands inside one transaction rather than as two independently durable statements, and
    /// the scope is obtained through the JOINING helper so the same member can also be used inside a
    /// cascade.
    /// </summary>
    [Fact]
    public async Task RemoveGrants_CommitsBothTablesInsideOneJoinableTransaction()
    {
        Harness harness = Harness.Ready();

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once());

        // ⚠ THIS ASSERTED THAT NO TRANSACTION WAS OPENED, ON THE READING THAT BOTH REMOVALS WERE STAGED
        // AGAINST THE CHANGE TRACKER AND APPLIED BY ONE FLUSH. They are not staged: both repository members
        // issue a set-based ExecuteDeleteAsync, which runs when it is called.
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.JoinOrBeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.BeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A failure between the two grant tables leaves the transaction uncommitted, so neither table is
    /// modified.
    /// </summary>
    /// <remarks>
    /// This is the fact the atomicity claim rests on, and it cannot be inferred from the fact above: a
    /// scope that is opened and always committed proves nothing about the failure path.
    /// </remarks>
    [Fact]
    public async Task RemoveGrants_WhenTheSecondTableFails_AbandonsTheTransaction()
    {
        Harness harness = Harness.Ready();
        harness.Permissions
            .Setup(permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the page-grant delete failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DeleteUserPermissionsAsync(PortalId, UserId, CancellationToken.None));

        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(transaction => transaction.DisposeAsync(), Times.Once());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Cache.Verify(cache => cache.InvalidateModulePermissions(It.IsAny<int>()), Times.Never());
        harness.Cache.Verify(cache => cache.InvalidateTabPermissions(It.IsAny<int>()), Times.Never());
    }

    /// <summary>
    /// The cascade-facing form removes from both tables and neither flushes nor evicts, which is what lets
    /// it join a larger unit of work.
    /// </summary>
    [Fact]
    public async Task StageGrantRemoval_RemovesFromBothTablesWithoutFlushingOrEvicting()
    {
        Harness harness = Harness.Ready();
        harness.PortalTabs = [new Tab { TabId = TabId, PortalId = PortalId, TabName = "First" }];

        Result result = await harness.Service.StageUserPermissionRemovalAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());

        // The scope IS opened - the guards run inside it, so a refusal abandons a scope that has
        // issued no statement - and what matters is that nothing COMMITS and nothing is evicted.
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Cache.Verify(cache => cache.Remove(It.IsAny<string>()), Times.Never());
        harness.Cache.Verify(cache => cache.RemoveByPrefix(It.IsAny<string>()), Times.Never());
    }

    /// <summary>
    /// The stage-only form refuses an unknown tenant or a non-member account and stages nothing, so a
    /// caller's own unit of work is unaffected by having asked.
    /// </summary>
    /// <param name="unknownPortal">
    /// <see langword="true"/> to make the tenant unknown, <see langword="false"/> to make the account a
    /// non-member.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageGrantRemoval_RefusesAndStagesNothing(bool unknownPortal)
    {
        Harness harness = Harness.Ready();

        if (unknownPortal)
        {
            harness.Portals
                .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        }
        else
        {
            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
        }

        Result result = await harness.Service.StageUserPermissionRemovalAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(unknownPortal ? PortalNotFoundCode : UserNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// the role-scoped sweep removes all three grant families, in the order the terminal legacy procedure
    /// removed them, and neither commits nor evicts.
    /// </summary>
    /// <remarks>
    /// THREE families rather than two. The storage family has no entity in this migration, so its removal
    /// is the one member expressed against the table rather than through the model; omitting it here would
    /// leave a third of the legacy cleanup unreproduced, and the rows it removes carry file-system
    /// authority.
    /// </remarks>
    [Fact]
    public async Task StageRoleGrantRemoval_SweepsAllThreeFamiliesInTheLegacyOrderWithoutCommittingOrEvicting()
    {
        Harness harness = Harness.Ready();

        // The role store answers every lookup with this row, so a non-null value is what makes the role
        // resolvable at all. The identifier is zero deliberately: Roles.RoleID is IDENTITY(0, 1), so zero
        // is the first real role an installation issues and no truthiness test may stand in for a lookup.
        harness.AdministratorsRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Subscribers",
        };

        List<string> sweptFamilies = [];

        harness.Permissions
            .Setup(permissions => permissions.DeleteFolderPermissionsByRoleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => sweptFamilies.Add("storage"))
            .Returns(Task.CompletedTask);
        harness.Permissions
            .Setup(permissions => permissions.DeleteModulePermissionsByRoleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => sweptFamilies.Add("module"))
            .Returns(Task.CompletedTask);
        harness.Permissions
            .Setup(permissions => permissions.DeleteTabPermissionsByRoleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => sweptFamilies.Add("page"))
            .Returns(Task.CompletedTask);

        Result result = await harness.Service.StageRolePermissionRemovalAsync(
            PortalId,
            AdministratorRoleId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        sweptFamilies.Should().Equal(
            ["storage", "module", "page"],
            "the terminal legacy procedure removed them in that order, and nothing is gained by diverging");

        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByRoleIdAsync(
                AdministratorRoleId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByRoleIdAsync(
                AdministratorRoleId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.DeleteFolderPermissionsByRoleIdAsync(
                AdministratorRoleId,
                It.IsAny<CancellationToken>()),
            Times.Once());

        // The account-scoped removals are NOT issued: a grant addressed to an account belongs to the account
        // and is removed when the account goes.
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.BeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.JoinOrBeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Cache.Verify(cache => cache.InvalidateTabPermissions(It.IsAny<int>()), Times.Never());
        harness.Cache.Verify(cache => cache.InvalidateModulePermissions(It.IsAny<int>()), Times.Never());
    }

    /// <summary>
    /// the role-scoped sweep refuses an unknown tenant or a role the tenant does not have, and removes
    /// nothing at all.
    /// </summary>
    /// <param name="unknownPortal">
    /// <see langword="true"/> to make the tenant unknown, <see langword="false"/> to make the role
    /// unresolvable within it.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRoleGrantRemoval_RefusesAndSweepsNothing(bool unknownPortal)
    {
        Harness harness = Harness.Ready();

        if (unknownPortal)
        {
            harness.AdministratorsRole = new Role
            {
                RoleId = AdministratorRoleId,
                PortalId = PortalId,
                RoleName = "Subscribers",
            };

            harness.Portals
                .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        }
        else
        {
            // Left absent, which is how the role store reports a role the tenant does not have.
            harness.AdministratorsRole = null;
        }

        Result result = await harness.Service.StageRolePermissionRemovalAsync(
            PortalId,
            AdministratorRoleId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(unknownPortal ? PortalNotFoundCode : RoleNotFoundCode);

        harness.Permissions.Verify(
            permissions => permissions.DeleteFolderPermissionsByRoleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByRoleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByRoleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The eviction member evicts the catalogue entries this service actually writes, and reads nothing.
    /// </summary>
    [Fact]
    public void InvalidateGrantCaches_EvictsTheCatalogueEntriesItWrites()
    {
        Harness harness = Harness.Ready();

        harness.Service.InvalidateUserPermissionCaches();

        harness.Cache.Verify(
            cache => cache.Remove("PermissionDefinitionsByTab|all"),
            Times.Once());
        harness.Cache.Verify(
            cache => cache.RemoveByPrefix("PermissionDefinitionsByModuleDefinition|"),
            Times.Once());
        harness.Cache.Verify(
            cache => cache.InvalidateTabPermissions(It.IsAny<int>()),
            Times.Never());
        harness.Cache.Verify(
            cache => cache.InvalidateModulePermissions(It.IsAny<int>()),
            Times.Never());
    }

    /// <summary>The removal evicts both grant families afterwards, the module family page by page.</summary>
    [Fact]
    public async Task RemoveGrants_EvictsTheCatalogueEntriesAndReadsNothing()
    {
        Harness harness = Harness.Ready();

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        // ⚠ THIS ASSERTED THE EVICTION OF TWO LEGACY GRANT KEY FAMILIES THAT NOTHING HERE WRITES. The
        // service caches only the CATALOGUE projections - one entry per module definition, plus one
        // installation-wide page key - so evicting `TabPermissions{portalId}` and
        // `ModulePermissions{tabId}` invalidated nothing, and reaching the second of them meant reading
        // every page of the tenant AFTER the commit, which let a durable, completed deletion answer 500.
        harness.Cache.Verify(
            cache => cache.Remove("PermissionDefinitionsByTab|all"),
            Times.Once());
        harness.Cache.Verify(
            cache => cache.RemoveByPrefix("PermissionDefinitionsByModuleDefinition|"),
            Times.Once());
        harness.Cache.Verify(
            cache => cache.InvalidateTabPermissions(It.IsAny<int>()),
            Times.Never());
        harness.Cache.Verify(
            cache => cache.InvalidateModulePermissions(It.IsAny<int>()),
            Times.Never());
        harness.Tabs.Verify(
            tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A removal refused before it starts neither commits nor evicts anything.</summary>
    [Fact]
    public async Task RemoveGrants_TouchesNothingWhenThePortalIsUnknown()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(PortalNotFoundCode);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());

        // The scope IS opened - both guards run inside it, so a refusal abandons a scope that has
        // issued no statement - and what matters is that nothing COMMITS and nothing is evicted.
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Cache.Verify(cache => cache.Remove(It.IsAny<string>()), Times.Never());
        harness.Cache.Verify(cache => cache.RemoveByPrefix(It.IsAny<string>()), Times.Never());
    }

    /// <summary>
    /// Two contradictory forms of addressing a placement are refused for EVERY key, not only for view.
    /// </summary>
    [Fact]
    public async Task ContradictoryPlacementAddresses_AreRefusedForANonViewKey()
    {
        const int placementTabModuleId = 900;
        const int otherTabId = TabId + 5;

        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = false;
        harness.StoredPlacements =
        [
            new TabModule { TabModuleId = placementTabModuleId, TabId = TabId, ModuleId = ModuleId },
        ];
        harness.Evaluator
            .Setup(evaluator => evaluator.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Success(true));

        Result<bool> agreeing = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            TabId,
            placementTabModuleId,
            CancellationToken.None);

        Result<bool> contradicting = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            otherTabId,
            placementTabModuleId,
            CancellationToken.None);

        agreeing.IsSuccess.Should().BeTrue(agreeing.Reason?.ToString());
        agreeing.Value.Should().BeTrue("the two addresses agree, so the module's own grant decides");

        contradicting.IsSuccess.Should().BeTrue(
            "a refusal is a denial - the contract publishes no failure code for a contradiction");
        contradicting.Value.Should().BeFalse(
            "the named placement does not sit on the named page, so the request contradicts itself");
    }

    /// <summary>
    /// The same contradiction is still refused on the inherited-view path it was originally enforced on.
    /// </summary>
    [Fact]
    public async Task ContradictoryPlacementAddresses_AreStillRefusedForInheritedView()
    {
        const int placementTabModuleId = 901;
        const int otherTabId = TabId + 6;

        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.StoredPlacements =
        [
            new TabModule { TabModuleId = placementTabModuleId, TabId = TabId, ModuleId = ModuleId },
        ];
        harness.Evaluator
            .Setup(evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Success(true));

        Result<bool> contradicting = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            otherTabId,
            placementTabModuleId,
            CancellationToken.None);

        contradicting.IsSuccess.Should().BeTrue(contradicting.Reason?.ToString());
        contradicting.Value.Should().BeFalse("hoisting the check must not weaken the path it came from");
    }

    /// <summary>A cached definition read asks for the legacy lifetime: twenty minutes times the multiplier.</summary>
    [Fact]
    public async Task DefinitionRead_RequestsTheLegacyLifetime()
    {
        Harness harness = Harness.Ready();
        harness.CachingOptions.PerformanceMultiplier = 3;

        Result<IReadOnlyList<PermissionDto>> result =
            await harness.Service.GetModulePermissionDefinitionsAsync(
                PortalId,
                ModuleId,
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        harness.CacheLifetimesRequested.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(60));

        // The definitions get their own key family; reusing a legacy grant key would collide with the grant
        // eviction that ICacheService targets at that exact name.
        harness.CacheKeysRequested.Should().ContainSingle()
            .Which.Should().StartWith("PermissionDefinitionsByModuleDefinition|");
    }

    /// <summary>
    /// The filtered key listing reaches the store on every call and takes no cache entry, so two filter
    /// shapes can never answer one another's question.
    /// </summary>
    [Fact]
    public async Task CatalogueRead_IsUncachedSoFilterShapesCannotServeEachOther()
    {
        Harness harness = Harness.Ready();

        // Caching is ENABLED, which is what makes this fact about the member and not about the multiplier.
        harness.CachingOptions.PerformanceMultiplier = 3;
        harness.Catalogue = [Entry(PermissionKey.VIEW)];

        // The unfiltered shape is the closed enumeration itself, and the code-scoped shape asks the store
        // which keys are declared under a code.
        Result<IReadOnlyList<string>> unfiltered = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);
        Result<IReadOnlyList<string>> filteredOnTheOldToken = await harness.Service.GetPermissionKeysAsync(
            permissionCode: "*",
            cancellationToken: CancellationToken.None);

        unfiltered.IsSuccess.Should().BeTrue(unfiltered.Reason?.ToString());
        filteredOnTheOldToken.IsSuccess.Should().BeTrue(filteredOnTheOldToken.Reason?.ToString());

        unfiltered.Value.Should().BeEquivalentTo(
            AllKeyNames,
            "the unfiltered catalogue of keys is the closed enumeration");
        filteredOnTheOldToken.Value.Should().BeEmpty(
            "no catalogue entry carries that scope code, and the answer must be its own rather than the "
            + "unfiltered one");

        // No entry, and therefore no key space to bound: the member never reaches the read-through helper.
        harness.CacheKeysRequested.Should().BeEmpty();
        harness.Cache.Verify(
            cache => cache.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<string>>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never());

        // The store is still reached, once per candidate key, so withdrawing the entry withdrew a cache and
        // not an answer.
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                "*",
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(AllKeyNames.Count));
    }

    /// <summary>A multiplier of zero bypasses the cache rather than writing an entry that expires at once.</summary>
    [Fact]
    public async Task DefinitionRead_BypassesTheCacheWhenCachingIsDisabled()
    {
        Harness harness = Harness.Ready();
        harness.CachingOptions.PerformanceMultiplier = 0;

        Result<IReadOnlyList<PermissionDto>> result =
            await harness.Service.GetModulePermissionDefinitionsAsync(
                PortalId,
                ModuleId,
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.CacheKeysRequested.Should().BeEmpty();
        harness.Cache.Verify(
            cache => cache.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never());

        // The store IS still reached, which is the half of the legacy behaviour that a bypass has to keep.
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The two definition reads are cached under their own key families, each keyed by the dimension its
    /// answer actually depends on rather than by the identifier the caller named.
    /// </summary>
    [Fact]
    public async Task DefinitionReads_AreCachedUnderTheirOwnKeyFamilies()
    {
        Harness harness = Harness.Ready();

        _ = await harness.Service.GetModulePermissionDefinitionsAsync(
            PortalId,
            ModuleId,
            CancellationToken.None);
        _ = await harness.Service.GetTabPermissionDefinitionsAsync(
            PortalId,
            TabId,
            CancellationToken.None);

        harness.CacheKeysRequested.Should().HaveCount(2);

        // is upheld by the GUARD rather than by the key: a module that does not exist, or exists in another
        // portal, is refused before the cache is consulted, and the rows an entry holds are product-wide
        // definition metadata rather than one tenant's data.
        harness.CacheKeysRequested[0].Should().Be(
            FormattableString.Invariant(
                $"PermissionDefinitionsByModuleDefinition|{harness.Module.ModuleDefinitionId}"));

        harness.CacheKeysRequested[1].Should().Be("PermissionDefinitionsByTab|all");
    }

    /// <summary>
    /// Two modules of one definition collapse onto a single entry, and two pages onto a single entry, so
    /// neither key space grows with the identifiers callers name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DefinitionReads_CollapseOntoOneEntryPerDistinctAnswer()
    {
        Harness harness = Harness.Ready();

        // A SECOND, DIFFERENT module carrying the SAME definition, and belonging to the same tenant so that
        // the guard admits it. Answered by the module repository so the service resolves a genuinely
        // different module row and still arrives at one key.
        const int secondModuleId = ModuleId + 77;
        harness.Modules
            .Setup(modules => modules.GetByIdAsync(secondModuleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Module
            {
                ModuleId = secondModuleId,
                ModuleDefinitionId = harness.Module.ModuleDefinitionId,
                PortalId = PortalId,
                ModuleTitle = "Second Measured Module",
            });

        _ = await harness.Service.GetModulePermissionDefinitionsAsync(PortalId, ModuleId, CancellationToken.None);
        _ = await harness.Service.GetModulePermissionDefinitionsAsync(
            PortalId,
            secondModuleId,
            CancellationToken.None);
        _ = await harness.Service.GetTabPermissionDefinitionsAsync(PortalId, TabId, CancellationToken.None);
        _ = await harness.Service.GetTabPermissionDefinitionsAsync(
            PortalId,
            SecondTabId,
            CancellationToken.None);

        harness.CacheKeysRequested.Should().HaveCount(4, "every call still consults the cache");
        harness.CacheKeysRequested.Distinct(StringComparer.Ordinal).Should().HaveCount(
            2,
            "four calls naming four identifiers must resolve to the two distinct answers behind them");

        // The read behind each call is unchanged: the repository is still asked about the module and the
        // page the caller named, so the canonical key changes what is STORED and not what is ANSWERED.
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleIdAsync(secondModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.GetByTabIdAsync(SecondTabId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// An identifier that names no module earns no cache entry, because it is refused before the cache is
    /// reached.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DefinitionReads_RefuseAnAbsentModuleBeforeConsultingTheCache()
    {
        Harness harness = Harness.Ready();
        harness.Modules
            .Setup(modules => modules.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<IReadOnlyList<PermissionDto>> first = await harness.Service
            .GetModulePermissionDefinitionsAsync(PortalId, 4242, CancellationToken.None);
        Result<IReadOnlyList<PermissionDto>> second = await harness.Service
            .GetModulePermissionDefinitionsAsync(PortalId, 9999, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.Error?.Code.Should().Be(ModuleNotFoundCode);
        second.IsFailure.Should().BeTrue();
        second.Error?.Code.Should().Be(ModuleNotFoundCode);

        harness.CacheKeysRequested.Should().BeEmpty(
            "an identifier naming no module is refused before the cache is consulted, so it earns no entry");
    }

    /// <summary>A host account is resolvable outside the tenant, and an ordinary account is not.</summary>
    [Fact]
    public async Task CallerResolution_AcceptsAHostAccountOutsideTheTenantAndNothingElse()
    {
        Harness host = Harness.Ready();
        host.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId != null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        host.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId == null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = HostUserId,
                Username = "host",
                FirstName = "Host",
                LastName = "Account",
                DisplayName = "Host Account",
                IsSuperUser = true,
            });
        host.Catalogue = [Entry(PermissionKey.VIEW)];

        Result<IReadOnlyList<string>> hostAnswer = await host.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        hostAnswer.IsSuccess.Should().BeTrue(hostAnswer.Reason?.ToString());

        // The host answer is the closed key set, for the reason set out on
        // EffectiveKeys_ForAHostAccountIgnoreAnUnresolvableScope. What this test pins is that the host
        // account was RESOLVED from outside the tenant at all; the ordinary account below is not.
        hostAnswer.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });

        Harness outsider = Harness.Ready();
        outsider.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId != null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        outsider.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId == null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = 4242,
                Username = "someone_elses_member",
                FirstName = "Other",
                LastName = "Tenant",
                DisplayName = "Other Tenant",
                IsSuperUser = false,
            });

        Result<IReadOnlyList<string>> outsiderAnswer = await outsider.Service
            .GetEffectivePermissionKeysAsync(PortalId, 4242, cancellationToken: CancellationToken.None);

        outsiderAnswer.IsFailure.Should().BeTrue(
            "the installation-wide fallback exists for host accounts alone");
        outsiderAnswer.Reason!.Code.Should().Be(UserNotFoundCode);
    }

    // Authority OVER a tenant is a different question from a grant ON a resource, and these tests exist to
    // keep the two from collapsing into one another.

    /// <summary>An unidentified caller administers nothing, and nothing is read in order to say so.</summary>
    [Fact]
    public async Task PortalAuthority_ForAnUnidentifiedCallerIsRefusedWithoutAnyRead()
    {
        Harness harness = Harness.Ready();

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            userId: null,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().BeFalse("an anonymous caller holds no designation to read");

        harness.Users.Verify(
            users => users.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Portals.Verify(
            portals => portals.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.RoleStore.Verify(
            roles => roles.GetUserRolesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>An account that resolves nowhere administers nothing.</summary>
    [Fact]
    public async Task PortalAuthority_ForAnUnresolvableAccountIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().BeFalse("an account that resolves neither inside nor outside the tenant holds nothing");
    }

    /// <summary>A host account administers every tenant, and the designation is never consulted for it.</summary>
    [Fact]
    public async Task PortalAuthority_ForAHostAccountIsGrantedWithoutReadingTheDesignation()
    {
        Harness harness = Harness.Ready();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = HostUserId,
                Username = "host",
                FirstName = "Host",
                LastName = "Account",
                DisplayName = "Host Account",
                IsSuperUser = true,
            });

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            HostUserId,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().BeTrue(
            "the installation-wide account outranks every tenant designation, exactly as PortalSecurity.IsInRoles treated it");

        // A host account holds no tenant-scoped assignment, so reading the designation or the assignments
        // would answer false for the one caller that must always be true.
        harness.RoleStore.Verify(
            roles => roles.GetUserRolesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A tenant that designates no administrator role confers authority on nobody.</summary>
    [Fact]
    public async Task PortalAuthority_ForAPortalWithNoDesignationIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow.AdministratorRoleId = null;

        // The caller holds an assignment that WOULD match if the designation existed, so the refusal can
        // only come from the missing designation rather than from an empty membership.
        harness.RoleAssignments = [Assignment(AdministratorRoleId)];

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().BeFalse("an unset designation names no role, so no assignment can match it");
    }

    /// <summary>A tenant row that cannot be loaded confers authority on nobody.</summary>
    [Fact]
    public async Task PortalAuthority_ForAnAbsentPortalIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Portal?)null);
        harness.RoleAssignments = [Assignment(AdministratorRoleId)];

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().BeFalse("no tenant row means no designation to hold");
    }

    /// <summary>
    /// A currently valid assignment to the designated role confers authority; every other membership state
    /// does not.
    /// </summary>
    /// <param name="effectiveOffsetDays">
    /// Days from now at which the assignment becomes effective, or <c>null</c> for no start bound.
    /// </param>
    /// <param name="expiryOffsetDays">
    /// Days from now at which the assignment lapses, or <c>null</c> for no end bound.
    /// </param>
    /// <param name="expected">Whether the caller is expected to administer the tenant.</param>
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(-30, null, true)]
    [InlineData(-30, 30, true)]
    [InlineData(30, null, false)]
    [InlineData(-60, -30, false)]
    public async Task PortalAuthority_TurnsOnTheValidityOfTheDesignatedAssignment(
        int? effectiveOffsetDays,
        int? expiryOffsetDays,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.RoleAssignments =
        [
            Assignment(
                AdministratorRoleId,
                effectiveOffsetDays is int effective ? Now.AddDays(effective) : null,
                expiryOffsetDays is int expiry ? Now.AddDays(expiry) : null),
        ];

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().Be(
            expected,
            "a pending or lapsed administrator membership is not authority, and the dates are the only thing that says so");
    }

    /// <summary>Holding some other role, however many, is not authority.</summary>
    [Fact]
    public async Task PortalAuthority_ForAnOrdinaryMembershipIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.RoleAssignments = [Assignment(OrdinaryRoleId), Assignment(OrdinaryRoleId + 1)];

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        answer.IsSuccess.Should().BeTrue(answer.Reason?.ToString());
        answer.Value.Should().BeFalse("only the designated role confers authority over the tenant");
    }

    /// <summary>
    /// The question is asked of the tenant named in the argument and of the caller named in the argument.
    /// </summary>
    [Fact]
    public async Task PortalAuthority_AsksTheStoreForTheNamedTenantAndCaller()
    {
        Harness harness = Harness.Ready();
        harness.RoleAssignments = [Assignment(AdministratorRoleId)];

        Result<bool> answer = await harness.Service.IsPortalAdministratorAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        answer.Value.Should().BeTrue(answer.Reason?.ToString());

        // Pinned because a transposed argument pair would still compile - both are integers - and would
        // then answer about the wrong tenant, which is precisely the confusion this member exists to stop.
        harness.RoleStore.Verify(
            roles => roles.GetUserRolesAsync(PortalId, UserId, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Portals.Verify(
            portals => portals.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Builds a role assignment for the harness account.</summary>
    /// <param name="roleId">The role held.</param>
    /// <param name="effectiveDate">When the membership starts, or <c>null</c> for no start bound.</param>
    /// <param name="expiryDate">When the membership lapses, or <c>null</c> for no end bound.</param>
    /// <returns>The assignment.</returns>
    private static UserRole Assignment(
        int roleId,
        DateTime? effectiveDate = null,
        DateTime? expiryDate = null)
        => new()
        {
            UserRoleId = 900 + roleId,
            UserId = UserId,
            RoleId = roleId,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
        };

    /// <summary>Every collaborator is required.</summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        Mock<IPermissionRepository> permissions = new();
        Mock<IPermissionEvaluator> evaluator = new();
        Mock<IPortalRepository> portals = new();
        Mock<IModuleRepository> modules = new();
        Mock<ITabRepository> tabs = new();
        Mock<IUserRepository> users = new();
        Mock<IRoleRepository> roles = new();
        Mock<IUnitOfWork> unitOfWork = new();
        Mock<ICacheService> cache = new();
        Mock<IClock> clock = new();
        CachingOptions caching = new();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                null!, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, null!, portals.Object, modules.Object, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, null!, modules.Object, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, null!, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, null!, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, null!,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                null!, unitOfWork.Object, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                roles.Object, null!, cache.Object, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, null!, clock.Object, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, null!, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                roles.Object, unitOfWork.Object, cache.Object, clock.Object, null!);
        });
    }

    /// <summary>The three permission contracts this project can legitimately see.</summary>
    /// <remarks>
    /// Note which contract is which. The precedence <em>abstraction</em> is an application-layer contract
    /// and so is fully reachable and legitimately substitutable; only its concrete implementation is
    /// infrastructure.
    /// </remarks>
    private static IReadOnlyList<Type> PermissionContracts =>
    [
        typeof(IPermissionService),
        typeof(IPermissionRepository),
        typeof(IPermissionEvaluator),
    ];

    /// <summary>The assemblies a permission contract is allowed to mention on its surface.</summary>
    private static IReadOnlySet<Assembly> PermittedSurfaceAssemblies =>
        new HashSet<Assembly>
        {
            typeof(Permission).Assembly,
            typeof(IPermissionService).Assembly,
            typeof(object).Assembly,
            typeof(Task).Assembly,
            typeof(IReadOnlyList<>).Assembly,
            typeof(CancellationToken).Assembly,
        };

    /// <summary>Every method the three permission contracts declare.</summary>
    /// <returns>The declared methods, paired with the contract that declares them.</returns>
    private static IEnumerable<(Type Contract, MethodInfo Member)> ContractMembers()
        => PermissionContracts.SelectMany(contract => contract
            .GetMethods()
            .Select(member => (Contract: contract, Member: member)));

    /// <summary>Unwraps a declared return type down to the value it ultimately produces.</summary>
    /// <param name="declared">The declared return type.</param>
    /// <returns>
    /// The produced value type: <see cref="void"/> for a bare task, and the innermost argument once any
    /// task and outcome wrappers have been peeled away.
    /// </returns>
    private static Type ProducedValue(Type declared)
    {
        Type current = declared;

        if (current == typeof(Task))
        {
            return typeof(void);
        }

        if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>))
        {
            current = current.GetGenericArguments()[0];
        }

        if (current == typeof(Result))
        {
            return typeof(void);
        }

        if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Result<>))
        {
            current = current.GetGenericArguments()[0];
        }

        return current;
    }

    /// <summary>Expands a type into itself and every generic argument it is built from.</summary>
    /// <param name="type">The type to expand.</param>
    /// <returns>The type and, recursively, its generic arguments.</returns>
    private static IEnumerable<Type> Flatten(Type type)
    {
        Type subject = type.IsByRef || type.IsArray ? type.GetElementType() ?? type : type;

        yield return subject;

        foreach (Type argument in subject.GetGenericArguments())
        {
            foreach (Type nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Every type that appears on a member's surface, in its parameters or its return value.</summary>
    /// <param name="member">The member to inspect.</param>
    /// <returns>The surface types.</returns>
    private static IEnumerable<Type> SurfaceTypes(MethodInfo member)
        => member
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Append(member.ReturnType)
            .SelectMany(Flatten);

    /// <summary>
    /// Every permission contract member performs input or output, so every one is a cancellable task.
    /// </summary>
    [Fact]
    public void Contract_EveryMemberIsAnAwaitableCancellableTask()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            string described = $"{contract.Name}.{member.Name}";

            if (member.Name == nameof(IPermissionService.InvalidateUserPermissionCaches))
            {
                continue;
            }

            bool producesTask = member.ReturnType == typeof(Task)
                || (member.ReturnType.IsGenericType
                    && member.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));

            producesTask.Should().BeTrue(
                $"{described} reaches a store and must hand back a task rather than a completed value");

            member.Name.Should().EndWith(
                "Async",
                $"{described} returns a task, and the name is what tells a caller at the call site to await it");

            ParameterInfo[] parameters = member.GetParameters();

            parameters.Should().NotBeEmpty($"{described} must at least accept a cancellation token");

            parameters[^1].ParameterType.Should().Be<CancellationToken>(
                $"{described} must take its cancellation token last, which is the convention every caller in this solution relies on");
        }
    }

    /// <summary>No permission contract member reports anything through an output or reference parameter.</summary>
    [Fact]
    public void Contract_DeclaresNoOutputOrReferenceParameter()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                string described = $"{contract.Name}.{member.Name}({parameter.Name})";

                parameter.IsOut.Should().BeFalse(
                    $"{described} must report through its return value, not through an output parameter");

                parameter.ParameterType.IsByRef.Should().BeFalse(
                    $"{described} must report through its return value, not by mutating an argument");
            }
        }
    }

    /// <summary>The persistence surface answers no access question at all.</summary>
    [Fact]
    public void Contract_ThePersistenceSurfaceReturnsNoAccessDecision()
    {
        foreach (MethodInfo member in typeof(IPermissionRepository).GetMethods())
        {
            ProducedValue(member.ReturnType).Should().NotBe(
                typeof(bool),
                $"IPermissionRepository.{member.Name} holds rows; deciding what they add up to belongs to exactly one other component");
        }
    }

    /// <summary>A decision is an outcome carrying a boolean, never a bare boolean.</summary>
    /// <remarks>
    /// The distinction this pins is the one that matters most to a caller and is the easiest to lose. A
    /// refusal and a failure are not the same event: "you may not do this" is a successful answer whose
    /// value happens to be false, whereas "the store could not be reached" is a failure with no answer at
    /// all.
    /// </remarks>
    [Fact]
    public void Contract_ADecisionIsAnOutcomeCarryingABooleanNeverABareBoolean()
    {
        MethodInfo[] decisions = typeof(IPermissionService)
            .GetMethods()
            .Where(member => ProducedValue(member.ReturnType) == typeof(bool))
            .ToArray();

        decisions.Select(decision => decision.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(IPermissionService.HasModulePermissionAsync),
                nameof(IPermissionService.HasTabPermissionAsync),
                nameof(IPermissionService.HasAnyTabPermissionInPortalAsync),
                nameof(IPermissionService.IsPortalAdministratorAsync),
                nameof(IPermissionService.IsHostAccountAsync),
            },
            "two grant scopes exist - module and page - tenant authority is a separate question answered "
            + "from the portal's own administrator designation rather than from a permission row, the "
            + "tenant-wide capability question is the page scope disjoined rather than a third grant scope, "
            + "and the host-account question is a classification of the account itself rather than any kind "
            + "of grant");

        foreach (MethodInfo decision in decisions)
        {
            Type produced = decision.ReturnType.GetGenericArguments()[0];

            produced.IsGenericType.Should().BeTrue(
                $"IPermissionService.{decision.Name} must wrap its answer so a refusal and a failure stay distinguishable");

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(Result<>),
                $"IPermissionService.{decision.Name} must return an outcome of boolean, so that a denial is a successful false rather than an error");
        }
    }

    /// <summary>Every member of the evaluation contract reports through an outcome, not bare.</summary>
    /// <remarks>
    /// The membership is pinned by NAME as well as by count, so growth stays deliberate. The sixth member
    /// is the set-based page verdict: it answers the same question as the single-page verdict over a set of
    /// pages, and it exists because asking the single-page member once per page made one authorisation
    /// check cost a read set per page a module was placed on.
    /// </remarks>
    [Fact]
    public void Contract_TheEvaluatorReportsEveryVerdictThroughAnOutcome()
    {
        MethodInfo[] members = typeof(IPermissionEvaluator).GetMethods();

        members.Select(member => member.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(IPermissionEvaluator.ListEffectivePortalPermissionKeysAsync),
                nameof(IPermissionEvaluator.ListEffectiveModulePermissionKeysAsync),
                nameof(IPermissionEvaluator.ListEffectiveTabPermissionKeysAsync),
                nameof(IPermissionEvaluator.HasModulePermissionAsync),
                nameof(IPermissionEvaluator.HasTabPermissionAsync),
                nameof(IPermissionEvaluator.HasAnyTabPermissionAsync),
                nameof(IPermissionEvaluator.ListTabsWithPermissionAsync),
            },
            "the evaluator decides a portal-wide, a module-scoped and a page-scoped listing, plus the two "
            + "single-key verdicts and the two set-based page verdicts - one existential, one enumerating - "
            + "and nothing else belongs on a decision contract");

        members.Should().HaveCount(
            7,
            "the evaluator decides a portal-wide, a module-scoped and a page-scoped listing, plus the two "
            + "single-key verdicts and the two set-based page verdicts - one existential, one enumerating - "
            + "and nothing else belongs on a decision contract");

        foreach (MethodInfo member in members)
        {
            member.ReturnType.IsGenericType.Should().BeTrue(
                $"IPermissionEvaluator.{member.Name} must be awaitable and carry a value");

            member.ReturnType.GetGenericTypeDefinition().Should().Be(
                typeof(Task<>),
                $"IPermissionEvaluator.{member.Name} performs I/O, so it must return a task");

            Type produced = member.ReturnType.GetGenericArguments()[0];

            produced.IsGenericType.Should().BeTrue(
                $"IPermissionEvaluator.{member.Name} must wrap its verdict so an advisory can travel with it");

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(Result<>),
                $"IPermissionEvaluator.{member.Name} must report through an outcome rather than bare, so that "
                + "\"no such subject\" is distinguishable from \"you hold nothing\" without a second read");
        }
    }

    /// <summary>Every sequence a permission contract hands back is a read-only generic sequence.</summary>
    [Fact]
    public void Contract_HandsBackOnlyReadOnlyGenericSequences()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            Type produced = ProducedValue(member.ReturnType);

            if (produced == typeof(void) || !produced.IsGenericType)
            {
                continue;
            }

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(IReadOnlyList<>),
                $"{contract.Name}.{member.Name} returns a sequence, which must be read-only and generic so a caller can neither mutate the answer nor cast its way out of the element type");
        }
    }

    /// <summary>A permission contract mentions only domain, application and framework types.</summary>
    /// <remarks>
    /// The reflection-driven row hydrator and the reflection-created static provider accessor are both
    /// gone, replaced by the object-relational materializer and constructor injection, and neither leaves a
    /// trace on these contracts.
    /// </remarks>
    [Fact]
    public void Contract_MentionsOnlyDomainApplicationAndFrameworkTypes()
    {
        IReadOnlySet<Assembly> permitted = PermittedSurfaceAssemblies;

        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (Type surface in SurfaceTypes(member))
            {
                if (surface.IsGenericParameter)
                {
                    continue;
                }

                permitted.Should().Contain(
                    surface.Assembly,
                    $"{contract.Name}.{member.Name} mentions {surface.Name}, which comes from an assembly these contracts are not permitted to expose");
            }
        }
    }

    /// <summary>
    /// No permission contract carries a member for the excluded file-management subsystem, with one
    /// deliberate exception that is a removal rather than a feature.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the path-scoped catalogue lookup and the eight file-system grant members are not ported.
    /// The subsystem they serve is out of scope, so there is no target feature for a grant keyed by a
    /// storage path to serve, and the target declares no file-system grant entity.
    /// </remarks>
    [Fact]
    public void Contract_CarriesNoMemberForTheExcludedStorageSubsystem()
    {
        string[] excludedVocabulary = ["Folder", "Directory", "Path"];

        const string PermittedRemoval = nameof(IPermissionRepository.DeleteFolderPermissionsByRoleIdAsync);

        var admitted = new List<(Type Contract, MethodInfo Member)>();

        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            if (string.Equals(member.Name, PermittedRemoval, StringComparison.Ordinal))
            {
                admitted.Add((contract, member));
                continue;
            }

            foreach (string token in excludedVocabulary)
            {
                member.Name.Should().NotContain(
                    token,
                    $"{contract.Name}.{member.Name} names a subsystem this migration excludes, so no member should mention it");
            }
        }

        (Type Contract, MethodInfo Member) exception = admitted.Should().ContainSingle(
            "the storage subsystem is excluded except for the single cleanup the legacy role removal performed")
            .Subject;

        exception.Contract.Should().Be(
            typeof(IPermissionRepository),
            "a removal of rows belongs to the persistence surface; the application surface exposes the "
            + "role-scoped sweep as one operation over all three grant families and never names a subsystem");

        ProducedValue(exception.Member.ReturnType).Should().Be(
            typeof(void),
            "a removal reports nothing, so nothing about the excluded subsystem can be read back through it");

        exception.Member.GetParameters().Select(parameter => parameter.Name).Should().Equal(
            ["roleId", "cancellationToken"],
            "it is addressable by role alone - a storage argument would make it the feature this exclusion forbids");
    }

    /// <summary>
    /// The evaluator every test below activates is the one the composed host resolves for the contract, and
    /// it is scoped, internal and sealed.
    /// </summary>
    /// <remarks>
    /// Scoped rather than singleton is asserted because the evaluator holds repositories, which hold the
    /// per-request data context: a singleton would share one change tracker across concurrent requests and
    /// answer one tenant's question from another tenant's pending state. Two resolutions inside one scope
    /// must be the same instance, and resolutions in different scopes must not be.
    /// </remarks>
    [Fact]
    public void Evaluator_IsTheImplementationTheContainerRegistersAndResolves()
    {
        Type registered = RegisteredEvaluatorType();

        registered.Assembly.Should().BeSameAs(
            typeof(DependencyInjection).Assembly,
            "an access decision belongs to the infrastructure layer, which is the only layer that reads "
            + "the grant rows");
        registered.IsSealed.Should().BeTrue(
            "an evaluator that can be subclassed can have its deny precedence overridden");
        registered.IsPublic.Should().BeFalse(
            "the implementation is internal so that no layer above it can name it, which is what makes "
            + "the contract the only way to ask the question");

        using (ScopedServices first = _fixture.CreateScopedServices())
        {
            IPermissionEvaluator resolved = first.Resolve<IPermissionEvaluator>();

            resolved.GetType().Should().Be(
                registered,
                "every test in this section activates the registered type, so a registration pointing "
                + "somewhere else would leave the whole section exercising unused code");
            first.Resolve<IPermissionEvaluator>().Should().BeSameAs(
                resolved,
                "one request asks the same question repeatedly, and each answer must be read through one "
                + "data context");

            using ScopedServices second = _fixture.CreateScopedServices();

            second.Resolve<IPermissionEvaluator>().Should().NotBeSameAs(
                resolved,
                "the evaluator holds the scoped data context, so sharing it between requests would let "
                + "one tenant's pending state decide another tenant's access");
        }
    }

    /// <summary>The evaluator refuses to be constructed without any one of its four collaborators.</summary>
    /// <param name="omitted">Which collaborator is withheld.</param>
    [Theory]
    [InlineData("permissions")]
    [InlineData("roles")]
    [InlineData("modules")]
    [InlineData("tabs")]
    public void Evaluator_RequiresEveryCollaborator(string omitted)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();

        Action construct = () => _ = Activate(
            RegisteredEvaluatorType(),
            omitted == "permissions" ? null : world.Permissions.Object,
            omitted == "roles" ? null : world.RoleStore.Object,
            omitted == "modules" ? null : world.ModuleStore.Object,
            omitted == "tabs" ? null : world.TabStore.Object);

        construct.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be(omitted);
    }

    /// <summary>
    /// The evaluator takes no configuration at all, so no configured value can redefine the built-in role
    /// names.
    /// </summary>
    /// <remarks>
    /// THIS REPLACES A FACT THAT ASSERTED A BLANK CONFIGURED ROLE NAME WAS REFUSED AT CONSTRUCTION.
    /// Refusing a blank value was the right guard for the wrong design: while the names were settings, a
    /// deployment could also set them to a NON-blank value that named a different row of the <c>Roles</c>
    /// table, which silently moved every-caller or anonymous-caller semantics onto a role an administrator
    /// had created for another purpose - and no start-up refusal can catch that, because the value is
    /// perfectly well formed.
    /// </remarks>
    [Fact]
    public void Evaluator_TakesNoConfigurationThatCouldRedefineTheBuiltInRoleNames()
    {
        Type[] parameters = [.. RegisteredEvaluatorType()
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)];

        parameters.Should().HaveCount(4);
        parameters.Should().OnlyContain(
            parameter => parameter.IsInterface && parameter.Name!.EndsWith("Repository", StringComparison.Ordinal),
            "the evaluator resolves grants and roles and reads nothing else; configuration cannot reach it");

        typeof(PortalOptions)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain(
                ["AllUsersRoleName", "UnauthenticatedRoleName"],
                "reintroducing either setting would make the audience configurable again");
    }

    /// <summary>Every member refuses an absent role-name collection.</summary>
    /// <remarks>
    /// An empty collection is a legitimate caller - it describes someone holding no named role, who is
    /// still reachable through the everyone, anonymous and account-scoped grants - so absence cannot be
    /// modelled as emptiness and has to be a fault.
    /// </remarks>
    [Fact]
    public async Task EveryMember_RefusesAnAbsentRoleNameCollection()
    {
        IPermissionEvaluator evaluator = EvaluatorWorld.Create().Build();

        await FluentActions
            .Awaiting(() => evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveTabPermissionKeysAsync(TabId, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.HasTabPermissionAsync(TabId, PermissionKey.VIEW, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>An allowing grant the caller reaches confers its key.</summary>
    [Fact]
    public async Task ModuleKeys_AnAllowingGrantConfersItsKey()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().Equal("VIEW");
    }

    /// <summary>A denying grant on its own confers nothing, which is the same answer absence produces.</summary>
    /// <remarks>
    /// Under the legacy first-match-wins walk this row would have GRANTED the key, because that walk never
    /// read the allow-or-deny flag. This test is the point at which the divergence recorded above becomes
    /// executable.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_ADenyingGrantAloneConfersNothing()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: MemberRoleId));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>
    /// A caller reaching neither a catalogue entry nor a grant holds nothing, and is refused rather than
    /// failed.
    /// </summary>
    /// <param name="withCatalogue">Whether the catalogue defines the key at all.</param>
    /// <param name="withGrant">Whether any grant row exists for it.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ModuleKeys_WithNoCatalogueEntryOrNoGrantRefuseWithoutFailing(
        bool withCatalogue,
        bool withGrant)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);

        if (withCatalogue)
        {
            world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        }

        if (withGrant)
        {
            world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        }

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>A denial suppresses an allowance of the same key on the same module, in either row order.</summary>
    /// <param name="denyFirst">Whether the refusing row is returned before the allowing one.</param>
    /// <remarks>
    /// The order-independence proof, and the single most valuable assertion in this section. A grant and a
    /// denial of one key on one scope is a legitimate configuration, so "whichever row came first wins"
    /// would make an access decision depend on a query plan.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModuleKeys_ADenialSuppressesAnAllowanceInEitherRowOrder(bool denyFirst)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));

        ModulePermission allowance = ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId);
        ModulePermission refusal = ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: AllUsersRoleId);

        if (denyFirst)
        {
            world.ModuleGrants.Add(refusal);
            world.ModuleGrants.Add(allowance);
        }
        else
        {
            world.ModuleGrants.Add(allowance);
            world.ModuleGrants.Add(refusal);
        }

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty("a refusal beats an allowance, whichever order the store returned them in");
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>A denial recorded on one module leaves the same key intact on another.</summary>
    /// <remarks>
    /// The suppression is correlated to the scope that carries the denial. Applying it across the
    /// tenant-wide union instead would let one forgotten module strip a key the caller genuinely holds on
    /// every other one, which is a silent revocation rather than a visible configuration.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ADenialOnOneModuleLeavesTheKeyIntactOnAnother()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithModule(SecondModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(
            FirstPermissionId,
            allowAccess: true,
            roleId: MemberRoleId,
            moduleId: SecondModuleId));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]))
            .Should().Equal(
                new[] { "VIEW" },
                "the allowance on the second module survives the refusal on the first");
        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty("while the module carrying the refusal confers nothing");
    }

    /// <summary>
    /// A denial recorded on a page does not suppress the same key on a module that shares its identifier.
    /// </summary>
    [Fact]
    public async Task PortalKeys_ADenialOnAPageDoesNotSuppressTheSameKeyOnAModuleSharingItsIdentifier()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(ZeroTabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.TabGrants.Add(PageGrantRow(ZeroTabId, FirstPermissionId, allowAccess: false, roleId: MemberRoleId));

        ModuleId.Should().Be(ZeroTabId, "the premise of this test is that the two identifiers collide");

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            new[] { "VIEW" },
            "a refusal on page zero must not reach module zero, because a bare identifier does not say "
            + "what it identifies");
    }

    /// <summary>The principal matrix: which stored role identifier reaches which caller.</summary>
    /// <param name="grantedRoleId">The role identifier recorded on the grant row.</param>
    /// <param name="identified">Whether the caller carries an account identifier.</param>
    /// <param name="expected">Whether the row should reach the caller.</param>
    /// <remarks>
    /// The row identified by zero is the case worth stating out loud. Zero is the first role the schema
    /// ever issues and grants exactly like any other, so a future reader who "tidies" this rule into a
    /// positive-identifier test would break the shipped administrators role of a real installation.
    /// </remarks>
    [Theory]
    [InlineData(AllUsersRoleId, true, true)]
    [InlineData(AllUsersRoleId, false, true)]
    [InlineData(UnauthenticatedRoleId, false, true)]
    [InlineData(UnauthenticatedRoleId, true, false)]
    [InlineData(SuperUserRoleId, true, false)]
    [InlineData(SuperUserRoleId, false, false)]
    [InlineData(NothingRoleId, true, false)]
    [InlineData(NothingRoleId, false, false)]
    [InlineData(ZeroRoleId, true, true)]
    [InlineData(MemberRoleId, true, true)]
    [InlineData(ForeignRoleId, true, false)]
    public async Task ModuleKeys_MatchAStoredRoleIdentifierAgainstTheCaller(
        int grantedRoleId,
        bool identified,
        bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(ZeroRoleId, ZeroRoleName);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.WithRole(ForeignRoleId, ForeignRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: grantedRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            identified ? UserId : null,
            identified ? [ZeroRoleName, MemberRoleName] : []));

        held.Should().Be(
            expected,
            $"a grant to role {grantedRoleId} against an identified={identified} caller must resolve "
            + "this way");
    }

    /// <summary>
    /// A grant naming an account reaches that account and no other, and its role column is not consulted at
    /// all.
    /// </summary>
    /// <param name="callerUserId">The account asking, or <see langword="null"/> when anonymous.</param>
    /// <param name="expected">Whether the row should reach that caller.</param>
    [Theory]
    [InlineData(UserId, true)]
    [InlineData(UserId + 1, false)]
    [InlineData(null, false)]
    public async Task ModuleKeys_AGrantNamingAnAccountReachesOnlyThatAccount(
        int? callerUserId,
        bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(
            FirstPermissionId,
            allowAccess: true,
            roleId: AllUsersRoleId,
            userId: UserId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            callerUserId,
            [MemberRoleName]));

        held.Should().Be(
            expected,
            "the account column decides on its own, so the everyone role beside it confers nothing");
    }

    /// <summary>A grant naming neither a role nor an account reaches nobody.</summary>
    [Fact]
    public async Task ModuleKeys_AGrantNamingNeitherRoleNorAccountReachesNobody()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, null, []))
            .Should().BeFalse();
    }

    /// <summary>A role name resolves only within the portal that owns the module under evaluation.</summary>
    /// <remarks>
    /// Role names are unique per portal rather than per installation, so resolving one installation-wide
    /// would let a grant to one tenant's "Administrators" be honoured for another tenant's. That is a
    /// silent cross-tenant escalation rather than a visible failure, which is why the identifier the name
    /// resolves to is read from the owning portal's roles and from nowhere else.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_ResolveARoleNameOnlyWithinThePortalThatOwnsTheModule()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(ForeignRoleId, MemberRoleName, OtherPortalId);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: ForeignRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            UserId,
            [MemberRoleName]));

        held.Should().BeFalse(
            "the role carrying that name belongs to another tenant, so the name must not resolve to its "
            + "identifier here");
    }

    /// <summary>A declared role name is compared to the stored one exactly.</summary>
    /// <param name="declaredName">The name the caller declares.</param>
    /// <param name="expected">Whether it should resolve to the stored role.</param>
    [Theory]
    [InlineData(MemberRoleName, true)]
    [InlineData("measured members", false)]
    [InlineData("MEASURED MEMBERS", false)]
    [InlineData(" Measured Members", false)]
    [InlineData("Measured Members ", false)]
    public async Task ModuleKeys_CompareRoleNamesOrdinally(string declaredName, bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            UserId,
            [declaredName]));

        held.Should().Be(expected);
    }

    /// <summary>A blank declared role name is discarded rather than matched.</summary>
    /// <remarks>
    /// This is the <c>role &lt;&gt; ""</c> guard at <c>PortalSecurity.vb:L123</c>, which existed because
    /// the semicolon-delimited string the legacy code split carried a leading delimiter and so always
    /// produced an empty first element.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_DiscardABlankDeclaredRoleName()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, "   ");
        world.WithRole(ForeignRoleId, string.Empty);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: ForeignRoleId));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, ["", "   ", "\t"]));

        keys.Should().BeEmpty("a blank name is not a name, so it resolves to no identifier at all");
    }

    /// <summary>
    /// The built-in everyone role participates for every caller, and the built-in anonymous role only for a
    /// caller with no account.
    /// </summary>
    /// <param name="identified">Whether the caller carries an account identifier.</param>
    /// <param name="expectedKeys">The keys the caller should hold.</param>
    [Theory]
    [InlineData(true, new[] { "VIEW" })]
    [InlineData(false, new[] { "EDIT", "VIEW" })]
    public async Task ModuleKeys_ApplyTheBuiltInRoleNamesWithTheirLegacyWidths(
        bool identified,
        string[] expectedKeys)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(EveryoneRoleId, AllUsersRoleName);
        world.WithRole(AnonymousRoleId, UnauthenticatedRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: EveryoneRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: AnonymousRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build().ListEffectiveModulePermissionKeysAsync(
            ModuleId,
            identified ? UserId : null,
            []));

        keys.Should().Equal(expectedKeys);
    }

    /// <summary>
    /// Only the role actually named <c>All Users</c> receives every-caller semantics; a role given any
    /// other name is an ordinary role.
    /// </summary>
    /// <remarks>
    /// The built-in name is fixed in code and no configuration value may move it, which is what this
    /// arrangement proves: <c>Portal:AllUsersRoleName</c> is set to "Everybody" and a role is created under
    /// that name, yet every-caller reach stays with the role genuinely called "All Users". Were the setting
    /// able to redesignate the every-caller role, the authorization boundary would be operator-mutable - an
    /// operator could grant every caller the reach of any role by renaming it in configuration.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleKeys_ApplyTheBuiltInNameOnlyToTheRoleThatCarriesIt()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(EveryoneRoleId, SpecialRoleNames.AllUsers);
        world.WithRole(ForeignRoleId, "Everybody");
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: EveryoneRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: ForeignRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, []));

        keys.Should().Equal(
            new[] { "VIEW" },
            "the role named 'All Users' stands for every caller, and a role named 'Everybody' is an "
            + "ordinary role that this caller never declared");
    }

    /// <summary>
    /// A module catalogue entry is admitted under the shared module scope code or by the module's own
    /// definition, and under nothing else.
    /// </summary>
    /// <param name="permissionCode">The scope code on the catalogue entry.</param>
    /// <param name="entryModuleDefinitionId">The definition the entry declares.</param>
    /// <param name="expected">Whether the entry should be admitted.</param>
    [Theory]
    [InlineData(ModuleDefinitionScopeCode, ModuleDefinitionId, true)]
    [InlineData(ModuleDefinitionScopeCode, OtherModuleDefinitionId, true)]
    [InlineData(InstalledModuleScopeCode, ModuleDefinitionId, true)]
    [InlineData(InstalledModuleScopeCode, OtherModuleDefinitionId, false)]
    [InlineData(PageScopeCode, OtherModuleDefinitionId, false)]
    [InlineData(ExcludedSubsystemScopeCode, OtherModuleDefinitionId, false)]
    public async Task ModuleKeys_AdmitOnlyCatalogueEntriesThatBelongToTheModule(
        string permissionCode,
        int entryModuleDefinitionId,
        bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(
            FirstPermissionId,
            PermissionKey.VIEW,
            permissionCode,
            entryModuleDefinitionId));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            UserId,
            [MemberRoleName]));

        held.Should().Be(expected);
    }

    /// <summary>A page catalogue entry is admitted under the page scope code and under nothing else.</summary>
    /// <param name="permissionCode">The scope code on the catalogue entry.</param>
    /// <param name="expected">Whether the entry should be admitted.</param>
    [Theory]
    [InlineData(PageScopeCode, true)]
    [InlineData(ModuleDefinitionScopeCode, false)]
    [InlineData(InstalledModuleScopeCode, false)]
    [InlineData(ExcludedSubsystemScopeCode, false)]
    public async Task TabKeys_AdmitOnlyThePageScopeCode(string permissionCode, bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, permissionCode));
        world.TabGrants.Add(PageGrantRow(TabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        bool held = Succeeded(await world.Build().HasTabPermissionAsync(
            TabId,
            PermissionKey.VIEW,
            UserId,
            [MemberRoleName]));

        held.Should().Be(expected);
    }

    /// <summary>
    /// A catalogue entry carrying the reader's own wildcard identifier is skipped rather than queried.
    /// </summary>
    /// <remarks>
    /// The grant readers accept minus one in the permission position as "every permission".
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_SkipACatalogueEntryCarryingTheReadersWildcardIdentifier()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(
            WildcardPermissionId,
            PermissionKey.VIEW,
            ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(
            WildcardPermissionId,
            allowAccess: true,
            roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                WildcardPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "the wildcard must never be passed through as though it named a permission");
    }

    /// <summary>A catalogue identifier appearing twice is judged once.</summary>
    /// <remarks>
    /// A catalogue read is per module rather than per grant, and the same identifier can appear in it more
    /// than once - two rows may name one permission under different codes.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_JudgeADuplicatedCatalogueIdentifierOnlyOnce()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(
            FirstPermissionId,
            PermissionKey.VIEW,
            InstalledModuleScopeCode,
            ModuleDefinitionId));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            new[] { "VIEW" },
            "a key survives once, however many catalogue rows name it");
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                ModuleId,
                WildcardPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the module's grants are read once for the whole module, not once per catalogue entry");
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                ModuleId,
                FirstPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "so the duplicated identifier cannot cost a second read either");
    }

    /// <summary>A grant row answering a wider question than the one asked is discarded.</summary>
    [Fact]
    public async Task ModuleKeys_DiscardAGrantRowNamingADifferentModuleOrPermission()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));

        world.Permissions
            .Setup(permissions => permissions.GetModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ModulePermission>)
            [
                ModuleGrantRow(
                    FirstPermissionId,
                    allowAccess: true,
                    roleId: MemberRoleId,
                    moduleId: SecondModuleId),
                ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId),
            ]);

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty(
            "one row belongs to another module and the other to another permission, so neither confers "
            + "the key that was asked about");
    }

    /// <summary>A page grant row answering a wider question than the one asked is discarded too.</summary>
    [Fact]
    public async Task TabKeys_DiscardAGrantRowNamingADifferentPageOrPermission()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, PageScopeCode));

        world.Permissions
            .Setup(permissions => permissions.GetTabPermissionsByTabIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TabPermission>)
            [
                PageGrantRow(SecondTabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId),
                PageGrantRow(TabId, SecondPermissionId, allowAccess: true, roleId: MemberRoleId),
            ]);

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveTabPermissionKeysAsync(TabId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
    }

    /// <summary>A module or page that does not exist confers nothing, and says so without failing.</summary>
    /// <remarks>
    /// The closed default rather than an error: this contract is asked what a caller holds, and the answer
    /// for something that does not exist is "nothing". Reporting existence is the application service's
    /// job, and it does it before asking - which is why an unknown scope must not become an exception here.
    /// </remarks>
    [Fact]
    public async Task Keys_ForAnUnknownModuleOrPageAreEmptyAndTheVerdictIsFalse()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: AllUsersRoleId));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
        Succeeded(await evaluator.ListEffectiveTabPermissionKeysAsync(TabId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasTabPermissionAsync(TabId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>Module zero, page zero and portal minus one are real identifiers, not absences.</summary>
    /// <remarks>
    /// <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> are both <c>IDENTITY(0, 1)</c> and
    /// <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, while the legacy absent-integer sentinel is also
    /// minus one. All three values therefore address real rows, and every one of them is the value a
    /// plausible-looking guard would reject.
    /// </remarks>
    [Fact]
    public async Task Keys_TreatZeroAndMinusOneIdentifiersAsRealRows()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(ZeroTabId);
        world.WithRole(ZeroRoleId, ZeroRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: ZeroRoleId));
        world.TabGrants.Add(PageGrantRow(ZeroTabId, SecondPermissionId, allowAccess: true, roleId: ZeroRoleId));

        IPermissionEvaluator evaluator = world.Build();

        PortalId.Should().Be(-1, "the portal identity column seeds at minus one");

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [ZeroRoleName]))
            .Should().Equal(new[] { "VIEW" }, "module zero is a real module");
        Succeeded(await evaluator.ListEffectiveTabPermissionKeysAsync(ZeroTabId, UserId, [ZeroRoleName]))
            .Should().Equal(new[] { "EDIT" }, "page zero is a real page");
        Succeeded(await evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [ZeroRoleName]))
            .Should().Equal(new[] { "EDIT", "VIEW" }, "and portal minus one is a real portal");
    }

    /// <summary>The tenant-wide union excludes grants on soft-deleted modules and pages.</summary>
    /// <remarks>
    /// A grant on something the caller can no longer reach confers nothing, so the recycled content is
    /// filtered before its grants are considered rather than after - which also keeps the catalogue read
    /// down to the permissions that can still affect the answer.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ExcludeGrantsOnSoftDeletedModulesAndPages()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId, isDeleted: true);
        world.WithTab(TabId, isDeleted: true);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.TabGrants.Add(PageGrantRow(TabId, SecondPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
    }

    /// <summary>A grant naming a catalogue entry that does not exist confers nothing.</summary>
    /// <remarks>
    /// A broken row rather than a denial, and failing closed is the only safe reading of it: there is no
    /// key to confer, so nothing is conferred.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ConferNothingForAGrantWhoseCatalogueEntryIsMissing()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
        world.Permissions.Verify(
            permissions => permissions.GetByIdsAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(FirstPermissionId)),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the key is resolved by the grant's own permission identifier, and the whole set of "
            + "identifiers the grants name is resolved in one read");
    }

    /// <summary>The tenant-wide union is distinct and ordered ordinally.</summary>
    /// <remarks>
    /// The ordering is not cosmetic. An access token minted twice from the same grants must carry an
    /// identical claim set both times, and the enumeration order of a set is not a contract - so the answer
    /// is sorted before it leaves.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_AreDistinctAndOrderedOrdinally()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithModule(SecondModuleId);
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(ThirdPermissionId, PermissionKey.WRITE, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(
            FirstPermissionId,
            allowAccess: true,
            roleId: MemberRoleId,
            moduleId: SecondModuleId));
        world.TabGrants.Add(PageGrantRow(TabId, ThirdPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            "EDIT",
            "VIEW",
            "WRITE");
    }

    /// <summary>
    /// A module the installation owns rather than a tenant resolves no named role, and is still reachable
    /// through the pseudo-roles and through an account.
    /// </summary>
    /// <remarks>
    /// A host-level scope carries no portal, and that is deliberate and closed: with no portal there is no
    /// set of role names that can be resolved without reaching installation-wide, and reaching
    /// installation-wide is the cross-tenant escalation the contract forbids.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_ForAnInstallationOwnedModuleResolveNoNamedRole()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId, portalId: null);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(ThirdPermissionId, PermissionKey.WRITE, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: AllUsersRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(ThirdPermissionId, allowAccess: true, userId: UserId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            "VIEW",
            "WRITE");
        world.RoleStore.Verify(
            roles => roles.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "there is no portal whose roles could be read without reaching across tenants");
    }

    /// <summary>A verdict is membership in the very set the listing returns, for both scopes.</summary>
    /// <param name="permissionKey">The key asked about.</param>
    /// <remarks>
    /// Expressing the verdict a second time is how a verdict and a listing come to disagree, and a user
    /// offered an action that is then refused is a defect they experience and a test rarely catches.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task Verdicts_AreMembershipInTheSetTheListingReturns(PermissionKey permissionKey)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.READ, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(ThirdPermissionId, PermissionKey.EDIT, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: false, roleId: MemberRoleId));
        world.TabGrants.Add(PageGrantRow(TabId, ThirdPermissionId, allowAccess: true, roleId: MemberRoleId));

        IPermissionEvaluator evaluator = world.Build();

        IReadOnlyList<string> moduleKeys = Succeeded(await evaluator
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));
        bool moduleVerdict = Succeeded(await evaluator
            .HasModulePermissionAsync(ModuleId, permissionKey, UserId, [MemberRoleName]));

        moduleVerdict.Should().Be(moduleKeys.Contains(permissionKey.ToString()));

        IReadOnlyList<string> tabKeys = Succeeded(await evaluator
            .ListEffectiveTabPermissionKeysAsync(TabId, UserId, [MemberRoleName]));
        bool tabVerdict = Succeeded(await evaluator
            .HasTabPermissionAsync(TabId, permissionKey, UserId, [MemberRoleName]));

        tabVerdict.Should().Be(tabKeys.Contains(permissionKey.ToString()));
    }

    /// <summary>The supplied cancellation token reaches every read a decision performs.</summary>
    /// <remarks>
    /// A token that is accepted and then dropped is worse than no token at all: the caller believes the
    /// work can be abandoned and it cannot. The token is matched by identity rather than by shape, so
    /// substituting a different one - or the default - fails here.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_PassTheSuppliedCancellationTokenToEveryRead()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        using CancellationTokenSource source = new();
        CancellationToken token = source.Token;

        await world.Build().ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName], token);

        world.RoleStore.Verify(roles => roles.GetByPortalIdAsync(PortalId, token), Times.Once);
        world.ModuleStore.Verify(modules => modules.GetByPortalIdAsync(PortalId, token), Times.Once);
        world.TabStore.Verify(tabs => tabs.GetByPortalIdAsync(PortalId, token), Times.Once);
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByPortalIdAsync(PortalId, token),
            Times.Once);
        world.Permissions.Verify(
            permissions => permissions.GetTabPermissionsByPortalIdAsync(PortalId, token),
            Times.Once);
        world.Permissions.Verify(
            permissions => permissions.GetByIdsAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(FirstPermissionId)),
                token),
            Times.Once);
    }

    /// <summary>A token already cancelled stops every member before it reads anything.</summary>
    /// <remarks>
    /// Observing cancellation at entry rather than only between reads is what makes a cancelled request
    /// cost nothing. It also keeps a cancellation distinguishable from a refusal: an abandoned request must
    /// not come back as "you are not allowed".
    /// </remarks>
    [Fact]
    public async Task EveryMember_ObservesATokenThatIsAlreadyCancelled()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(TabId);
        IPermissionEvaluator evaluator = world.Build();

        using CancellationTokenSource source = new();
        await source.CancelAsync();
        CancellationToken cancelled = source.Token;

        await FluentActions
            .Awaiting(() => evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveTabPermissionKeysAsync(TabId, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.HasModulePermissionAsync(
                ModuleId,
                PermissionKey.VIEW,
                UserId,
                [],
                cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.HasTabPermissionAsync(TabId, PermissionKey.VIEW, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();

        world.ModuleStore.Verify(
            modules => modules.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a cancelled request must cost nothing at all");
    }

    /// <summary>A store that cannot be reached produces a fault rather than a refusal.</summary>
    /// <remarks>
    /// The distinction is the whole point. "The database is unavailable" and "you are not allowed" are
    /// different answers, and collapsing the first into the second would tell an operator their permissions
    /// were wrong while the real problem was elsewhere - and would, on a write path, let a transient outage
    /// read as a deliberate denial.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_LetAStoreFaultSurfaceRatherThanBecomingARefusal()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Permissions
            .Setup(permissions => permissions.GetByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the grant store is unreachable"));

        IPermissionEvaluator evaluator = world.Build();

        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveModulePermissionKeysAsync(
                ModuleId,
                UserId,
                [MemberRoleName]))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions
            .Awaiting(() => evaluator.HasModulePermissionAsync(
                ModuleId,
                PermissionKey.VIEW,
                UserId,
                [MemberRoleName]))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // MIGRATION: three static controllers collapse into one application contract, and the decision moves
    // out of all three.

    /// <summary>The grant entities carry no serialisation attributes.</summary>
    [Fact]
    public void GrantEntities_CarryNoSerialisationAttributes()
    {
        Type[] entities = [typeof(Permission), typeof(ModulePermission), typeof(TabPermission)];

        foreach (Type entity in entities)
        {
            foreach (MemberInfo member in entity.GetMembers())
            {
                foreach (CustomAttributeData attribute in member.GetCustomAttributesData())
                {
                    (attribute.AttributeType.Namespace ?? string.Empty).Should().NotStartWith(
                        "System.Xml",
                        $"{entity.Name}.{member.Name} must not describe a wire format; serialisation belongs to the request and response contracts at the api boundary");
                }
            }
        }
    }

    /// <summary>A grant references its catalogue entry rather than deriving from it.</summary>
    /// <remarks>
    /// The two grant entities are independent types carrying a foreign key, not specialisations of the
    /// catalogue entry - a grant is a reference to a permission, not a kind of one.
    /// </remarks>
    [Fact]
    public void GrantEntities_ReferenceTheCatalogueRatherThanDerivingFromIt()
    {
        typeof(Permission).IsAssignableFrom(typeof(ModulePermission)).Should().BeFalse(
            "a module grant references a catalogue entry through its permission identifier and is not a kind of catalogue entry");

        typeof(Permission).IsAssignableFrom(typeof(TabPermission)).Should().BeFalse(
            "a page grant references a catalogue entry through its permission identifier and is not a kind of catalogue entry");
    }

    // =================================================================================================
    // The permission key vocabulary.
    // =================================================================================================

    /// <summary>The key vocabulary is a closed set of four persisted spellings.</summary>
    /// <remarks>
    /// The key column was free text against which the legacy source compared bare literals. The target
    /// types it as a closed enumeration whose member names are the stored spellings exactly, so the magic
    /// strings become named members without changing one stored value - which is what lets the schema stay
    /// immutable while the code stops guessing.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreAClosedSetOfFourPersistedSpellings()
    {
        Enum.GetNames<PermissionKey>().Should().Equal(
            ["VIEW", "EDIT", "READ", "WRITE"],
            "these four spellings are what the key column stores, so the enumeration must mirror them exactly");
    }

    /// <summary>A key is a discrete member, never a bit field.</summary>
    /// <remarks>
    /// A grant row names exactly one key, and the tables record one row per key per principal. Marking the
    /// enumeration as a bit field would invite combining keys into a single value, which no column can
    /// store and which would make the stored numbers meaningless.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreNotABitField()
    {
        typeof(PermissionKey).GetCustomAttribute<FlagsAttribute>().Should().BeNull(
            "keys are discrete members recorded one row at a time, not flags to be combined");
    }

    /// <summary>Every declared key round-trips as its persisted spelling, and no other casing is a member.</summary>
    /// <param name="permissionKey">The key under test.</param>
    /// <param name="persistedSpelling">The spelling stored in the key column.</param>
    /// <remarks>
    /// All four members are covered here. Only two of them have a literal call site in the legacy code that
    /// is still live - and the census is worth recording precisely, because the raw counts mislead.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW, "VIEW")]
    [InlineData(PermissionKey.EDIT, "EDIT")]
    [InlineData(PermissionKey.READ, "READ")]
    [InlineData(PermissionKey.WRITE, "WRITE")]
    public void PermissionKeys_RoundTripAsTheirPersistedSpellingAndAreNeverCaseFolded(
        PermissionKey permissionKey,
        string persistedSpelling)
    {
        permissionKey.ToString().Should().Be(
            persistedSpelling,
            "the member name is the stored value, so the two must not drift apart");

        Enum.Parse<PermissionKey>(persistedSpelling).Should().Be(
            permissionKey,
            "a value read back from the key column must resolve to the member that wrote it");

        Enum.TryParse<PermissionKey>(persistedSpelling.ToLowerInvariant(), out PermissionKey folded)
            .Should()
            .BeFalse(
                $"a lower-cased spelling is not a member, so nothing silently accepts \"{persistedSpelling.ToLowerInvariant()}\" as {permissionKey}; the parsed-out value was {folded}");
    }

    /// <summary>Builds a catalogue row naming one permission key.</summary>
    /// <param name="permissionKey">The key the row names.</param>
    /// <returns>The catalogue row.</returns>
    private static Permission Entry(PermissionKey permissionKey) => new()
    {
        PermissionId = 1,
        PermissionCode = "SYSTEM_MODULE_DEFINITION",
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey,
        PermissionName = permissionKey.ToString(),
    };

    /// <summary>Builds a placement of the module under test on a page.</summary>
    /// <param name="tabId">The page the module sits on.</param>
    /// <param name="tabModuleId">
    /// The placement's own key, when a test addresses the placement by it rather than by its page.
    /// </param>
    /// <returns>The placement.</returns>
    private static TabModule Placement(int tabId, int? tabModuleId = null) => new()
    {
        TabModuleId = tabModuleId ?? (1000 + tabId),
        TabId = tabId,
        ModuleId = ModuleId,
        PaneName = "ContentPane",
    };

    /// <summary>The identifier the grant reads treat as "every permission" rather than as "no permission".</summary>
    /// <remarks>
    /// Measured legacy behaviour that the terminal procedures still carry: in this argument position the
    /// value is a wildcard, which is a third distinct meaning for the same number alongside the All Users
    /// role and the legacy absence sentinel. Named here so the substituted store reproduces it instead of
    /// quietly matching nothing.
    /// </remarks>
    private const int WildcardPermissionId = -1;

    /// <summary>Builds a catalogue row.</summary>
    /// <param name="permissionId">The row's own identifier, which grants reference.</param>
    /// <param name="permissionKey">The key the row defines.</param>
    /// <param name="permissionCode">The scope code the row belongs to.</param>
    /// <param name="moduleDefinitionId">The definition the row declares.</param>
    /// <returns>The catalogue row.</returns>
    private static Permission CatalogueEntry(
        int permissionId,
        PermissionKey permissionKey,
        string permissionCode,
        int moduleDefinitionId = ModuleDefinitionId) => new()
        {
            PermissionId = permissionId,
            PermissionCode = permissionCode,
            ModuleDefinitionId = moduleDefinitionId,
            PermissionKey = permissionKey,
            PermissionName = permissionKey.ToString(),
        };

    /// <summary>Builds one grant recorded against the module under test.</summary>
    /// <param name="permissionId">The catalogue row the grant references.</param>
    /// <param name="allowAccess">Whether the row confers the key or refuses it.</param>
    /// <param name="roleId">The role the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="userId">The account the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="moduleId">The module the grant is recorded against.</param>
    /// <returns>The grant row.</returns>
    private static ModulePermission ModuleGrantRow(
        int permissionId,
        bool allowAccess,
        int? roleId = null,
        int? userId = null,
        int moduleId = ModuleId)
    {
        return new ModulePermission
        {
            // The surrogate key has to differ per row, and rows for one permission may be recorded
            // against several modules, so both coordinates contribute to it.
            ModulePermissionId = (1000 * (moduleId + 2)) + permissionId,
            ModuleId = moduleId,
            PermissionId = permissionId,
            RoleId = roleId,
            UserId = userId,
            AllowAccess = allowAccess,
        };
    }

    /// <summary>Builds one grant recorded against a page.</summary>
    /// <param name="tabId">The page the grant is recorded against.</param>
    /// <param name="permissionId">The catalogue row the grant references.</param>
    /// <param name="allowAccess">Whether the row confers the key or refuses it.</param>
    /// <param name="roleId">The role the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="userId">The account the grant names, or <see langword="null"/> when it names none.</param>
    /// <returns>The grant row.</returns>
    private static TabPermission PageGrantRow(
        int tabId,
        int permissionId,
        bool allowAccess,
        int? roleId = null,
        int? userId = null)
    {
        return new TabPermission
        {
            // Distinct per page as well as per permission, for the reason given on the module row above.
            TabPermissionId = (1000 * (tabId + 2)) + permissionId,
            TabId = tabId,
            PermissionId = permissionId,
            RoleId = roleId,
            UserId = userId,
            AllowAccess = allowAccess,
        };
    }

    /// <summary>
    /// The rows the concrete evaluator reads, together with the substituted repositories that serve them
    /// and the configuration it is constructed over.
    /// </summary>
    /// <remarks>
    /// The lists are mutable and public on purpose. A test adds the rows it needs after <see
    /// cref="Create"/> and before <see cref="Build"/>, and the wiring closes over the lists rather than
    /// over snapshots of them, so ordering the two the other way round would still work.
    /// </remarks>
    private sealed class EvaluatorWorld
    {
        private EvaluatorWorld()
        {
            ModuleStore
                .Setup(modules => modules.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) =>
                    Modules.FirstOrDefault(module => module.ModuleId == moduleId));

            ModuleStore
                .Setup(modules => modules.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => (IReadOnlyList<Module>)
                    Modules.Where(module => module.PortalId == portalId).ToList());

            TabStore
                .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    Tabs.FirstOrDefault(tab => tab.TabId == tabId));

            TabStore
                .Setup(tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => (IReadOnlyList<Tab>)
                    Tabs.Where(tab => tab.PortalId == portalId).ToList());

            RoleStore
                .Setup(roles => roles.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => (IReadOnlyList<Role>)
                    Roles.Where(role => role.PortalId == portalId).ToList());

            Permissions
                .Setup(permissions => permissions.GetByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, CancellationToken __) => (IReadOnlyList<Permission>)
                    Catalogue.ToList());

            Permissions
                .Setup(permissions => permissions.GetByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, CancellationToken __) => (IReadOnlyList<Permission>)
                    Catalogue.ToList());

            Permissions
                .Setup(permissions => permissions.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int permissionId, CancellationToken _) =>
                    Catalogue.FirstOrDefault(entry => entry.PermissionId == permissionId));

            // The SET-WISE catalogue read, which is how the portal-wide walk resolves the entries its
            // reached grants name: the distinct identifiers are collected and resolved in one read rather
            // than one read per identifier.
            Permissions
                .Setup(permissions => permissions.GetByIdsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> permissionIds, CancellationToken _) =>
                    (IReadOnlyList<Permission>)Catalogue
                        .Where(entry => permissionIds.Contains(entry.PermissionId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetModulePermissionsByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, int permissionId, CancellationToken _) =>
                    (IReadOnlyList<ModulePermission>)ModuleGrants
                        .Where(grant => grant.ModuleId == moduleId
                            && (permissionId == WildcardPermissionId || grant.PermissionId == permissionId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetTabPermissionsByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, int permissionId, CancellationToken _) =>
                    (IReadOnlyList<TabPermission>)TabGrants
                        .Where(grant => grant.TabId == tabId
                            && (permissionId == WildcardPermissionId || grant.PermissionId == permissionId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetModulePermissionsByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) =>
                    (IReadOnlyList<ModulePermission>)ModuleGrants
                        .Where(grant => Modules.Any(module =>
                            module.ModuleId == grant.ModuleId && module.PortalId == portalId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetTabPermissionsByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) =>
                    (IReadOnlyList<TabPermission>)TabGrants
                        .Where(grant => Tabs.Any(tab => tab.TabId == grant.TabId && tab.PortalId == portalId))
                        .ToList());
        }

        /// <summary>Gets the grant and catalogue store.</summary>
        public Mock<IPermissionRepository> Permissions { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the store role names resolve through.</summary>
        public Mock<IRoleRepository> RoleStore { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the store that establishes which portal owns a module.</summary>
        public Mock<IModuleRepository> ModuleStore { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the store that establishes which portal owns a page.</summary>
        public Mock<ITabRepository> TabStore { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the modules the store holds.</summary>
        public List<Module> Modules { get; } = [];

        /// <summary>Gets the pages the store holds.</summary>
        public List<Tab> Tabs { get; } = [];

        /// <summary>Gets the roles the store holds.</summary>
        public List<Role> Roles { get; } = [];

        /// <summary>Gets the catalogue rows the store holds.</summary>
        public List<Permission> Catalogue { get; } = [];

        /// <summary>Gets the module grants the store holds.</summary>
        public List<ModulePermission> ModuleGrants { get; } = [];

        /// <summary>Gets the page grants the store holds.</summary>
        public List<TabPermission> TabGrants { get; } = [];

        /// <summary>Builds an empty world whose every read is wired and returns nothing.</summary>
        /// <returns>The world.</returns>
        public static EvaluatorWorld Create() => new();

        /// <summary>Adds a module owned by a portal.</summary>
        /// <param name="moduleId">The module key, which the schema seeds at zero.</param>
        /// <param name="portalId">
        /// The owning portal, or <see langword="null"/> when the installation owns the module rather than a
        /// tenant.
        /// </param>
        /// <param name="isDeleted">Whether the module sits in the recycle bin.</param>
        /// <param name="moduleDefinitionId">The definition the module was built from.</param>
        public void WithModule(
            int moduleId,
            int? portalId = PortalId,
            bool isDeleted = false,
            int moduleDefinitionId = ModuleDefinitionId)
            => Modules.Add(new Module
            {
                ModuleId = moduleId,
                ModuleDefinitionId = moduleDefinitionId,
                PortalId = portalId,
                IsDeleted = isDeleted,
            });

        /// <summary>Adds a page owned by a portal.</summary>
        /// <param name="tabId">The page key, which the schema seeds at zero.</param>
        /// <param name="portalId">The owning portal, or <see langword="null"/> for a host page.</param>
        /// <param name="isDeleted">Whether the page sits in the recycle bin.</param>
        public void WithTab(int tabId, int? portalId = PortalId, bool isDeleted = false)
            => Tabs.Add(new Tab
            {
                TabId = tabId,
                TabName = "Page " + tabId.ToString(CultureInfo.InvariantCulture),
                PortalId = portalId,
                IsDeleted = isDeleted,
            });

        /// <summary>Adds a role belonging to a portal.</summary>
        /// <param name="roleId">The role key, which the schema seeds at zero.</param>
        /// <param name="roleName">The stored name, matched exactly.</param>
        /// <param name="portalId">The owning portal.</param>
        public void WithRole(int roleId, string roleName, int? portalId = PortalId)
            => Roles.Add(new Role
            {
                RoleId = roleId,
                RoleName = roleName,
                PortalId = portalId,
            });

        /// <summary>Constructs the registered evaluator implementation over this world.</summary>
        /// <returns>The evaluator, typed as the contract its callers depend on.</returns>
        public IPermissionEvaluator Build() => Activate(
            RegisteredEvaluatorType(),
            Permissions.Object,
            RoleStore.Object,
            ModuleStore.Object,
            TabStore.Object);
    }

    /// <summary>Builds a permission service over substituted collaborators.</summary>
    private sealed class Harness
    {
        private Harness()
        {
            Account = new User
            {
                UserId = UserId,
                Username = "measured_member",
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = "Ada Lovelace",
            };

            Module = new Module
            {
                ModuleId = ModuleId,
                ModuleDefinitionId = 1,
                PortalId = PortalId,
                ModuleTitle = "Measured Module",
            };

            Tab = new Tab
            {
                TabId = TabId,
                PortalId = PortalId,
                TabName = "Measured Page",
            };

            // The tenant row the portal-authority question reads its administrator designation from. The
            // designation is a nullable column, so a portal that has none is a real state and is exercised
            // by clearing this member rather than by removing the row.
            PortalRow = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                AdministratorRoleId = AdministratorRoleId,
            };

            Catalogue = [];
            RoleAssignments = [];
            AssignedRoles = [];
            PortalKeys = [];
            ModuleKeys = [];
            TabKeys = [];
            StoredPlacements = [];
            PageViewGrants = [];
            PageEditGrants = [];
            CapturedRoleNames = [];
            PortalTabs = [Tab];
            CacheKeysRequested = [];
            CacheLifetimesRequested = [];

            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Evaluator = new Mock<IPermissionEvaluator>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            RoleStore = new Mock<IRoleRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Transaction = new Mock<ITransactionScope>(MockBehavior.Loose);
            JoinedTransaction = new Mock<ITransactionScope>(MockBehavior.Loose);

            // BOTH SCOPE-OPENING MEMBERS ARE STUBBED, AND THEY RETURN DIFFERENT SCOPES BECAUSE THE
            // PRODUCTION MEMBERS OBTAIN DIFFERENT SCOPES. The top-level grant removal opens a transaction
            // of its own with the non-nesting member, because both of its removals are immediate set-based
            // deletes rather than staged changes, so it needs the rollback boundary the change tracker
            // cannot give it.
            UnitOfWork
                .Setup(unitOfWork => unitOfWork.JoinOrBeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(JoinedTransaction.Object);

            UnitOfWork
                .Setup(unitOfWork => unitOfWork.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Transaction.Object);

            CachingOptions = new CachingOptions();

            Service = new PermissionService(
                Permissions.Object,
                Evaluator.Object,
                Portals.Object,
                Modules.Object,
                Tabs.Object,
                Users.Object,
                RoleStore.Object,
                UnitOfWork.Object,
                Cache.Object,
                Clock.Object,
                CachingOptions);
        }

        public User Account { get; }

        public Module Module { get; }

        public Tab Tab { get; }

        /// <summary>The tenant row, mutable so that a portal with no administrator designation is testable.</summary>
        public Portal PortalRow { get; }

        /// <summary>The caller's role assignments within the portal, as the role store would return them.</summary>
        public IReadOnlyList<UserRole> RoleAssignments { get; set; }

        public IReadOnlyList<Permission> Catalogue { get; set; }

        public IReadOnlyList<string> AssignedRoles { get; set; }

        public IReadOnlyList<string> PortalKeys { get; set; }

        public IReadOnlyList<string> ModuleKeys { get; set; }

        public IReadOnlyList<string> TabKeys { get; set; }

        public IReadOnlyList<TabModule> StoredPlacements { get; set; }

        public Dictionary<int, bool> PageViewGrants { get; }

        /// <summary>
        /// Per-page EDIT answers, for the arm of the module-edit decision that reads the page's own grant.
        /// </summary>
        public Dictionary<int, bool> PageEditGrants { get; }

        public List<string> CapturedRoleNames { get; }

        /// <summary>
        /// The tenant's designated administrators role, or <see langword="null"/> when the designation
        /// names no row.
        /// </summary>
        /// <remarks>
        /// Absent by default, so the portal-administrator arm of the module-edit decision contributes
        /// nothing unless a test asks it to. That keeps every fact written before answering exactly as it
        /// did.
        /// </remarks>
        public Role? AdministratorsRole { get; set; }

        public bool ModuleGrant { get; set; }

        public bool TabGrant { get; set; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<IPermissionEvaluator> Evaluator { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUserRepository> Users { get; }

        /// <summary>
        /// The role store. Only the portal-authority question reads it, and it reads assignments rather
        /// than role names, because authority over a tenant turns on whether the administrator assignment
        /// is currently valid and only the assignment row carries the dates that decide that.
        /// </summary>
        public Mock<IRoleRepository> RoleStore { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ITransactionScope> Transaction { get; }

        /// <summary>
        /// The scope the JOINING member hands out, kept separate from <see cref="Transaction"/> so a fact
        /// can say which scope it means. Inside an enclosing scope the real implementation makes this one's
        /// commit and disposal no-ops, which is what lets the staging member serve both a standalone caller
        /// and a cascade from one call site.
        /// </summary>
        public Mock<ITransactionScope> JoinedTransaction { get; }

        public Mock<IClock> Clock { get; }

        public CachingOptions CachingOptions { get; }

        /// <summary>The pages the portal holds, which the account cleanup enumerates to evict by page.</summary>
        public IReadOnlyList<Tab> PortalTabs { get; set; }

        /// <summary>Every cache key the service asked for, in the order it asked.</summary>
        public IList<string> CacheKeysRequested { get; }

        /// <summary>Every cache lifetime the service computed, in the order it computed them.</summary>
        public IList<TimeSpan> CacheLifetimesRequested { get; }

        public PermissionService Service { get; }

        /// <summary>Builds a harness whose collaborators can answer every question asked of them.</summary>
        /// <returns>The harness.</returns>
        public static Harness Ready()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Only the portal-authority question loads the tenant row; every other member of the service
            // asks the cheaper existence question above. The row is returned by reference so that a test
            // can clear the administrator designation on it and observe the answer change.
            harness.Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRow);

            // Assignments, not role names: authority over a tenant turns on whether the administrator
            // assignment is currently valid, and the validity window lives on the assignment row.
            harness.RoleStore
                .Setup(roles => roles.GetUserRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleAssignments);

            // the module-edit decision resolves the tenant's designated administrators role by identifier
            // so it can compare its NAME against the caller's roles, which is what the legacy test
            // compared. Absent by default, so the arm contributes nothing unless a fact supplies the row.
            harness.RoleStore
                .Setup(roles => roles.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AdministratorsRole);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Account);

            harness.Users
                .Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AssignedRoles);

            harness.Modules
                .Setup(modules => modules.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Module);

            harness.Modules
                .Setup(modules => modules.GetTabModulesByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.StoredPlacements);

            harness.Tabs
                .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Tab);

            // The pages of the portal, which the account cleanup enumerates in order to evict the
            // module-permission entry of each one. Stubbed to the harness's own page rather than to an
            // empty sequence, so a test asserting the eviction has something to observe it against.
            harness.Tabs
                .Setup(tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalTabs);

            harness.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<string>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<string>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKeysRequested.Add(key);
                    harness.CacheLifetimesRequested.Add(expiration);
                    return factory(token);
                });

            harness.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKeysRequested.Add(key);
                    harness.CacheLifetimesRequested.Add(expiration);
                    return factory(token);
                });

            // MIGRATION: the catalogue is no longer one wildcard-tolerant read. The repository contract
            // mirrors the legacy provider, which offered a definition-scoped read and a code-and-key read,
            // so the service composes the three question shapes from those two.
            harness.Permissions
                .Setup(permissions => permissions.GetByModuleDefinitionIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue);

            harness.Permissions
                .Setup(permissions => permissions.GetByCodeAndKeyAsync(
                    It.IsAny<string>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string code, PermissionKey key, CancellationToken _) => harness.Catalogue
                    .Where(entry => entry.PermissionKey == key
                        && string.Equals(entry.PermissionCode, code, StringComparison.OrdinalIgnoreCase))
                    .ToList());

            harness.Permissions
                .Setup(permissions => permissions.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int permissionId, CancellationToken _) => harness.Catalogue
                    .FirstOrDefault(entry => entry.PermissionId == permissionId));

            harness.Permissions
                .Setup(permissions => permissions.GetByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue
                    .Where(entry => entry.ModuleDefinitionId == EntryModuleDefinitionId
                        || string.Equals(
                            entry.PermissionCode,
                            "SYSTEM_MODULE_DEFINITION",
                            StringComparison.OrdinalIgnoreCase))
                    .ToList());

            harness.Permissions
                .Setup(permissions => permissions.GetByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue
                    .Where(entry => string.Equals(
                        entry.PermissionCode,
                        "SYSTEM_TAB",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList());

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.PortalKeys));

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.ModuleKeys));

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.TabKeys));

            harness.Evaluator
                .Setup(evaluator => evaluator.HasModulePermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<bool>.Success(harness.ModuleGrant));

            harness.Evaluator
                .Setup(evaluator => evaluator.HasTabPermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync((
                    int tabId,
                    PermissionKey key,
                    int? userId,
                    IReadOnlyCollection<string> roleNames,
                    CancellationToken token) => Result<bool>.Success(harness.AnswerPage(tabId, key)));

            // The set-based page answer is the DISJUNCTION of the single-page answers over the same stubbed
            // world, which is precisely what the real evaluator specifies: a verdict composed per page and
            // then disjoined, so a denying page contributes nothing rather than vetoing.
            harness.Evaluator
                .Setup(evaluator => evaluator.HasAnyTabPermissionAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyCollection<int>, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync((
                    IReadOnlyCollection<int> tabIds,
                    PermissionKey key,
                    int? userId,
                    IReadOnlyCollection<string> roleNames,
                    CancellationToken token) => Result<bool>.Success(
                        tabIds.Any(tabId => harness.AnswerPage(tabId, key))));

            harness.Permissions
                .Setup(permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            harness.Permissions
                .Setup(permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            return harness;
        }

        /// <summary>Records the roles the caller was evaluated under.</summary>
        /// <param name="roleNames">The roles supplied to the store.</param>
        public void Capture(IReadOnlyCollection<string> roleNames)
        {
            CapturedRoleNames.Clear();
            CapturedRoleNames.AddRange(roleNames);
        }

        /// <summary>Answers a page-level question from the configured page grants.</summary>
        /// <param name="tabId">The page asked about.</param>
        /// <param name="permissionKey">The permission asked about.</param>
        /// <returns>Whether the page allows it.</returns>
        private bool AnswerPage(int tabId, PermissionKey permissionKey)
        {
            if (permissionKey == PermissionKey.VIEW && PageViewGrants.TryGetValue(tabId, out bool viewGranted))
            {
                return viewGranted;
            }

            // The edit key reaches pages as well, so a per-page edit answer is available for the
            // facts that need one. Absent an entry the blanket answer applies, exactly as before.
            if (permissionKey == PermissionKey.EDIT && PageEditGrants.TryGetValue(tabId, out bool editGranted))
            {
                return editGranted;
            }

            return TabGrant;
        }
    }

    /// <summary>
    /// Unwraps a successful evaluator answer, failing the test when the evaluator reported a failure.
    /// </summary>
    /// <typeparam name="TValue">The answer type.</typeparam>
    /// <param name="result">The outcome the evaluator produced.</param>
    /// <returns>The answer carried by a successful outcome.</returns>
    private static TValue Succeeded<TValue>(Result<TValue> result)
    {
        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        return result.Value;
    }

    /// <summary>
    /// The concrete type the infrastructure layer registers for <see cref="IPermissionEvaluator"/>.
    /// </summary>
    /// <returns>The registered implementation type.</returns>
    /// <remarks>
    /// Registrations are collected rather than a provider built, so this costs nothing and opens no
    /// connection: <c>AddInfrastructure</c> reads the connection string to hand it to the context and to
    /// the database probe, and never dials it, so the throwaway value below is never connected to.
    /// </remarks>
    private static Type RegisteredEvaluatorType() => RegisteredEvaluator.Value;

    /// <summary>Collects the infrastructure registrations and reads the evaluator's out of them.</summary>
    /// <returns>The registered implementation type.</returns>
    private static Type ReadRegisteredEvaluatorType()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] =
                    "Server=(registration-read-only);Database=(unused);Integrated Security=True",
            })
            .Build();

        ServiceCollection registrations = [];
        registrations.AddInfrastructure(configuration);

        ServiceDescriptor descriptor = registrations
            .Should()
            .ContainSingle(
                registration => registration.ServiceType == typeof(IPermissionEvaluator),
                "permission evaluation has exactly one implementation, and a second registration would "
                + "make which one answers a question depend on registration order")
            .Subject;

        return descriptor.ImplementationType
            ?? throw new InvalidOperationException(
                "The evaluator is registered through a factory, so its implementation type cannot be "
                + "read from the registration. Register the type directly, or resolve an instance here "
                + "instead.");
    }

    /// <summary>
    /// Activates an implementation type, surfacing a constructor failure as the constructor threw it.
    /// </summary>
    /// <param name="implementation">The type to construct.</param>
    /// <param name="arguments">The constructor arguments.</param>
    /// <returns>The constructed evaluator.</returns>
    private static IPermissionEvaluator Activate(Type implementation, params object?[] arguments)
    {
        try
        {
            return (IPermissionEvaluator)Activator.CreateInstance(implementation, arguments)!;
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
            throw;
        }
    }
}
