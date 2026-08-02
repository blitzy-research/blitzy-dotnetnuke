using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Covers permission evaluation: the catalogue projection, the scope cascade, the pseudo-roles an
/// unidentified caller is evaluated under, the one composition rule this layer owns, and the deliberate
/// difference between an unanswerable question and a refused one.
/// </summary>
/// <remarks>
/// <para>
/// Allow-and-deny precedence itself is a single set-based reduction performed where the grants are, inside
/// the infrastructure assembly, which this project deliberately does not reference. That is the right place
/// for it: reducing thousands of grant rows in memory would be both slower and a second implementation of a
/// rule that must have exactly one. It is proved end to end by the integration project, where a page-level
/// denial is shown to suppress a role-level allowance over real HTTP against real rows.
/// </para>
/// <para>
/// What this suite owns is everything the reduction cannot see. The service decides which scope is being
/// asked about, which roles the caller is evaluated under, whether a host account short-circuits the
/// question entirely, and - the one place it composes rather than delegates - how a module that inherits its
/// view permission from the pages it sits on is resolved. That last rule is genuine deny-over-allow
/// behaviour at this layer: a view allowance recorded against the module is withheld unless some page the
/// module sits on also grants view, so a page-level refusal suppresses a module-level allowance.
/// </para>
/// <para>
/// The suite also pins the six failure codes. They are the sole input to the status code the caller sees, so
/// a renamed code silently turns a not-found into a bad-request without any other test noticing.
/// </para>
/// </remarks>
public class PermissionEvaluatorTests
{
    private const int PortalId = -1;

    private const int OtherPortalId = 3;

    private const int UserId = 7;

    private const int HostUserId = 1;

    private const int ModuleId = 0;

    private const int TabId = 12;

    private const int SecondTabId = 13;

    private const string FilterInvalidCode = "permission.filter_invalid";

    private const string PortalNotFoundCode = "permission.portal_not_found";

    private const string ModuleNotFoundCode = "permission.module_not_found";

    private const string TabNotFoundCode = "permission.tab_not_found";

    private const string UserNotFoundCode = "permission.user_not_found";

    private const string KeyInvalidCode = "permission.key_invalid";

    private const string AllUsersRoleName = "All Users";

    private const string UnauthenticatedRoleName = "Unauthenticated Users";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The catalogue is projected upper-cased, without duplicates, and in ordinal order.
    /// </summary>
    [Fact]
    public async Task Catalogue_IsUpperCasedDistinctAndOrdinallySorted()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            Entry("view"),
            Entry(" Edit "),
            Entry("WRITE"),
            Entry("read"),
            Entry("VIEW"),
        ];

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "READ", "VIEW", "WRITE" },
            "the stored rows carry mixed case and padding, and a caller comparing keys must not have to "
            + "know that");
    }

    /// <summary>
    /// A stored row with no usable key contributes nothing.
    /// </summary>
    /// <param name="stored">The stored key text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Catalogue_DropsAnUnusableKey(string stored)
    {
        Harness harness = Harness.Ready();
        harness.Catalogue = [Entry(stored), Entry("VIEW")];

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// Both catalogue filters reach the store exactly as supplied.
    /// </summary>
    [Fact]
    public async Task Catalogue_PassesBothFiltersToTheStoreUnchanged()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetPermissionKeysAsync("SYSTEM_MODULE_DEFINITION", 42, CancellationToken.None);

        harness.Permissions.Verify(
            permissions => permissions.ListAsync(
                "SYSTEM_MODULE_DEFINITION",
                42,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Omitting the code filter places no restriction, whereas supplying a blank one is a mistake.
    /// </summary>
    /// <param name="supplied">The supplied filter text.</param>
    /// <remarks>
    /// The distinction is deliberate. A caller that omits the filter is asking for everything; a caller that
    /// sends an empty one has almost certainly bound an empty form field and is asking for rows whose code
    /// is blank, of which there are none. Answering the second with an empty list would look like a working
    /// query that found nothing.
    /// </remarks>
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
            permissions => permissions.ListAsync(
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Omitting the code filter is accepted.
    /// </summary>
    [Fact]
    public async Task Catalogue_AcceptsAnAbsentCodeFilter()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Permissions.Verify(
            permissions => permissions.ListAsync(null, null, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A definition identifier that cannot name a row is refused rather than queried.
    /// </summary>
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
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(FilterInvalidCode);
        result.Reason!.Message.Should().Contain("identifiers start at 1");
    }

    /// <summary>
    /// A tenant-wide question is answered by the tenant-wide query alone.
    /// </summary>
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
        harness.Permissions.Verify(
            permissions => permissions.ListEffectivePortalPermissionKeysAsync(
                PortalId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.ListEffectiveModulePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.ListEffectiveTabPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// An unknown tenant is reported as missing.
    /// </summary>
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

    /// <summary>
    /// An unidentified caller is evaluated under the two pseudo-roles and no account is read.
    /// </summary>
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
    /// <remarks>
    /// The instant matters because a role assignment carries effective and expiry dates. Reading the
    /// assignments as of the clock rather than as of no particular time is what makes a lapsed assignment
    /// stop granting anything.
    /// </remarks>
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
    /// The pseudo-role names come from configuration rather than from a literal.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_UseTheConfiguredPseudoRoleNames()
    {
        Harness harness = Harness.Ready();
        harness.PortalOptions.AllUsersRoleName = "Tout le monde";
        harness.PortalOptions.UnauthenticatedRoleName = "Visiteurs";

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            userId: null,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(new[] { "Tout le monde", "Visiteurs" });
    }

    /// <summary>
    /// A host account holds the whole catalogue and no scope is consulted.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAHostAccountAreTheWholeCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue = [Entry("VIEW"), Entry("EDIT"), Entry("READ"), Entry("WRITE")];
        harness.Account.IsSuperUser = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });
        harness.Permissions.Verify(
            permissions => permissions.ListEffectivePortalPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A host account is answered before any scope is resolved, so an unresolvable scope is not reported.
    /// </summary>
    /// <remarks>
    /// This is a consequence of the order in which the checks run, and it is the desired one: the answer for
    /// a host account is the same for every scope, so resolving the scope could only turn a correct answer
    /// into a not-found. It also means the endpoint cannot be used by a host account to discover which
    /// module identifiers exist, which is consistent with the refusal-not-absence rule the protected routes
    /// follow.
    /// </remarks>
    [Fact]
    public async Task EffectiveKeys_ForAHostAccountIgnoreAnUnresolvableScope()
    {
        Harness harness = Harness.Ready();
        harness.Account.IsSuperUser = true;
        harness.Catalogue = [Entry("VIEW")];
        harness.Modules
            .Setup(modules => modules.GetAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            moduleId: 987654,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// A module-scoped question is answered by the module query.
    /// </summary>
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
        harness.Permissions.Verify(
            permissions => permissions.ListEffectiveModulePermissionKeysAsync(
                ModuleId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A module belonging to another tenant is reported as missing.
    /// </summary>
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

    /// <summary>
    /// A module owned by the installation rather than a tenant is evaluable in any tenant.
    /// </summary>
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

    /// <summary>
    /// A page-scoped question is answered by the page query.
    /// </summary>
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
        harness.Permissions.Verify(
            permissions => permissions.ListEffectiveTabPermissionKeysAsync(
                TabId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A page belonging to another tenant is reported as missing.
    /// </summary>
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

    /// <summary>
    /// A page owned by the installation rather than a tenant is evaluable in any tenant.
    /// </summary>
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

    /// <summary>
    /// Asking about a module on a page unions both scopes.
    /// </summary>
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
    /// <remarks>
    /// This is the deny-over-allow rule this layer owns. The module's own grants say view is allowed; the
    /// module is nevertheless not viewable, because a module that inherits defers the decision to the pages
    /// it sits on and none of them allows it. Every other key the module grants is unaffected.
    /// </remarks>
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
    /// A module that inherits its view permission is viewable when a page it sits on grants view.
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

    /// <summary>
    /// A module that does not inherit keeps its own view grant and no page is consulted.
    /// </summary>
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
        harness.Permissions.Verify(
            permissions => permissions.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Inheritance stops asking as soon as one page grants view.
    /// </summary>
    [Fact]
    public async Task InheritedView_StopsAtTheFirstGrantingPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = [];
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Permissions.Verify(
            permissions => permissions.HasTabPermissionAsync(
                SecondTabId,
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "one allowing page is enough, so the rest are not worth a round trip");
    }

    /// <summary>
    /// Inheritance considers every page until one allows.
    /// </summary>
    [Fact]
    public async Task InheritedView_ConsidersEveryPageUntilOneAllows()
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

        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// Inheritance reads the placements when the module was loaded without them.
    /// </summary>
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
            modules => modules.ListPlacementsAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A module with no placement at all inherits nothing.
    /// </summary>
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

    /// <summary>
    /// Asking whether an inheriting module is viewable is answered by the page.
    /// </summary>
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
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Be(pageGrantsView);
        harness.Permissions.Verify(
            permissions => permissions.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "an inheriting module's own view grant is not the answer, so it is not even read");
    }

    /// <summary>
    /// Inheritance applies to the view permission only.
    /// </summary>
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
            CancellationToken.None);

        result.Value.Should().BeTrue(
            "only the view permission is inherited from the page; editing is decided by the module");
        harness.Permissions.Verify(
            permissions => permissions.HasModulePermissionAsync(
                ModuleId,
                key,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A permission key that names no member of the vocabulary is refused.
    /// </summary>
    /// <remarks>
    /// The keys arrive as an enumeration, and an enumeration in this language accepts any integer of its
    /// underlying type. Without this check an unmapped value would be passed to the store, match nothing,
    /// and be reported as a legitimate refusal rather than as a malformed request.
    /// </remarks>
    [Fact]
    public async Task HasModulePermission_RefusesAKeyThatNamesNoMember()
    {
        Harness harness = Harness.Ready();

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            (PermissionKey)99,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(KeyInvalidCode);
        result.Reason!.Message.Should().Contain("99");
        harness.Modules.Verify(
            modules => modules.GetAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "a malformed request is refused before anything is read");
    }

    /// <summary>
    /// A page question refuses an unrecognised key too.
    /// </summary>
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

    /// <summary>
    /// Every defined key is accepted.
    /// </summary>
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
        harness.Permissions.Verify(
            permissions => permissions.HasTabPermissionAsync(
                TabId,
                key,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// An unknown module is reported as missing.
    /// </summary>
    [Fact]
    public async Task HasModulePermission_RefusesAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.Modules
            .Setup(modules => modules.GetAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(ModuleNotFoundCode);
    }

    /// <summary>
    /// An unknown page is reported as missing.
    /// </summary>
    [Fact]
    public async Task HasTabPermission_RefusesAnUnknownPage()
    {
        Harness harness = Harness.Ready();
        harness.Tabs
            .Setup(tabs => tabs.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
    /// <remarks>
    /// The asymmetry with the effective-keys question is deliberate. "May this account do this?" always has
    /// an answer, and for a non-member the answer is no; turning it into a failure would make the gate
    /// harder to use and would leak the fact that the identifier names nobody. "What may this account do?"
    /// has no answer at all for a non-member, so it fails.
    /// </remarks>
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

    /// <summary>
    /// A host account is allowed without the grants being read at all.
    /// </summary>
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
            CancellationToken.None);

        Result<bool> tabAnswer = await harness.Service.HasTabPermissionAsync(
            PortalId,
            HostUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        moduleAnswer.Value.Should().BeTrue();
        tabAnswer.Value.Should().BeTrue();
        harness.Permissions.Verify(
            permissions => permissions.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The decision itself is delegated, and the caller's roles travel with the question.
    /// </summary>
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
            CancellationToken.None);

        result.Value.Should().Be(
            storedAnswer,
            "precedence between an allowance and a denial is reduced where the grants are, and is not "
            + "second-guessed here");
        harness.CapturedRoleNames.Should().Equal(new[] { "Subscribers", AllUsersRoleName });
    }

    /// <summary>
    /// A permission question does not verify the tenant separately.
    /// </summary>
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

    /// <summary>
    /// Removing an account's grants refuses an unknown tenant and touches nothing.
    /// </summary>
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
            permissions => permissions.DeleteUserPermissionsAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Removing an account's grants refuses an account that is not a member, and deletes nothing.
    /// </summary>
    /// <remarks>
    /// Here a non-member is a failure rather than a no-op, which is the opposite of the permission question
    /// above. A caller asking to remove grants believes the account exists; silently succeeding would report
    /// that grants had been cleaned up when nothing had been examined, which is exactly the kind of quiet
    /// success that leaves stale grants behind.
    /// </remarks>
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
            permissions => permissions.DeleteUserPermissionsAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Removing an account's grants delegates the removal.
    /// </summary>
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
            permissions => permissions.DeleteUserPermissionsAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A host account is resolvable outside the tenant, and an ordinary account is not.
    /// </summary>
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
        host.Catalogue = [Entry("VIEW")];

        Result<IReadOnlyList<string>> hostAnswer = await host.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        hostAnswer.IsSuccess.Should().BeTrue(hostAnswer.Reason?.ToString());
        hostAnswer.Value.Should().Equal(new[] { "VIEW" });

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

    /// <summary>
    /// Every collaborator is required.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        Mock<IPermissionRepository> permissions = new();
        Mock<IPortalRepository> portals = new();
        Mock<IModuleRepository> modules = new();
        Mock<ITabRepository> tabs = new();
        Mock<IUserRepository> users = new();
        Mock<IClock> clock = new();
        PortalOptions options = new();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                null!, portals.Object, modules.Object, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, null!, modules.Object, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, portals.Object, null!, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, portals.Object, modules.Object, null!, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, portals.Object, modules.Object, tabs.Object, null!, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, portals.Object, modules.Object, tabs.Object, users.Object, null!, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object,
                portals.Object,
                modules.Object,
                tabs.Object,
                users.Object,
                clock.Object,
                null!);
        });
    }

    /// <summary>
    /// Builds a catalogue row carrying a stored key.
    /// </summary>
    /// <param name="permissionKey">The stored key text.</param>
    /// <returns>The catalogue row.</returns>
    private static Permission Entry(string permissionKey) => new()
    {
        PermissionId = 1,
        PermissionCode = "SYSTEM_MODULE_DEFINITION",
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey,
        PermissionName = permissionKey,
    };

    /// <summary>
    /// Builds a placement of the module under test on a page.
    /// </summary>
    /// <param name="tabId">The page the module sits on.</param>
    /// <returns>The placement.</returns>
    private static TabModule Placement(int tabId) => new()
    {
        TabModuleId = 1000 + tabId,
        TabId = tabId,
        ModuleId = ModuleId,
        PaneName = "ContentPane",
    };

    /// <summary>
    /// Builds a permission service over substituted collaborators.
    /// </summary>
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

            Catalogue = [];
            AssignedRoles = [];
            PortalKeys = [];
            ModuleKeys = [];
            TabKeys = [];
            StoredPlacements = [];
            PageViewGrants = [];
            CapturedRoleNames = [];
            PortalOptions = new PortalOptions();

            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);

            Service = new PermissionService(
                Permissions.Object,
                Portals.Object,
                Modules.Object,
                Tabs.Object,
                Users.Object,
                Clock.Object,
                PortalOptions);
        }

        public User Account { get; }

        public Module Module { get; }

        public Tab Tab { get; }

        public PortalOptions PortalOptions { get; }

        public IReadOnlyList<Permission> Catalogue { get; set; }

        public IReadOnlyList<string> AssignedRoles { get; set; }

        public IReadOnlyList<string> PortalKeys { get; set; }

        public IReadOnlyList<string> ModuleKeys { get; set; }

        public IReadOnlyList<string> TabKeys { get; set; }

        public IReadOnlyList<TabModule> StoredPlacements { get; set; }

        public Dictionary<int, bool> PageViewGrants { get; }

        public List<string> CapturedRoleNames { get; }

        public bool ModuleGrant { get; set; }

        public bool TabGrant { get; set; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IClock> Clock { get; }

        public PermissionService Service { get; }

        /// <summary>
        /// Builds a harness whose collaborators can answer every question asked of them.
        /// </summary>
        /// <returns>The harness.</returns>
        public static Harness Ready()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

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
                .Setup(modules => modules.GetAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Module);

            harness.Modules
                .Setup(modules => modules.ListPlacementsAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.StoredPlacements);

            harness.Tabs
                .Setup(tabs => tabs.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Tab);

            harness.Permissions
                .Setup(permissions => permissions.ListAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue);

            harness.Permissions
                .Setup(permissions => permissions.ListEffectivePortalPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.PortalKeys);

            harness.Permissions
                .Setup(permissions => permissions.ListEffectiveModulePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.ModuleKeys);

            harness.Permissions
                .Setup(permissions => permissions.ListEffectiveTabPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.TabKeys);

            harness.Permissions
                .Setup(permissions => permissions.HasModulePermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.ModuleGrant);

            harness.Permissions
                .Setup(permissions => permissions.HasTabPermissionAsync(
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
                    CancellationToken token) => harness.AnswerPage(tabId, key));

            harness.Permissions
                .Setup(permissions => permissions.DeleteUserPermissionsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            return harness;
        }

        /// <summary>
        /// Records the roles the caller was evaluated under.
        /// </summary>
        /// <param name="roleNames">The roles supplied to the store.</param>
        public void Capture(IReadOnlyCollection<string> roleNames)
        {
            CapturedRoleNames.Clear();
            CapturedRoleNames.AddRange(roleNames);
        }

        /// <summary>
        /// Answers a page-level question from the configured page grants.
        /// </summary>
        /// <param name="tabId">The page asked about.</param>
        /// <param name="permissionKey">The permission asked about.</param>
        /// <returns>Whether the page allows it.</returns>
        private bool AnswerPage(int tabId, PermissionKey permissionKey)
            => permissionKey == PermissionKey.VIEW && PageViewGrants.TryGetValue(tabId, out bool granted)
                ? granted
                : TabGrant;
    }
}
