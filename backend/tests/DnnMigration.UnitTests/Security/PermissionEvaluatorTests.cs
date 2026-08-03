using System.Reflection;
using DnnMigration.Application.Abstractions;
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

// The reflection namespace and the domain both declare a type called Module. The contract-shape tests
// below need the reflection namespace, and every Module in this file means the domain entity, so the
// ambiguity is resolved once here rather than by qualifying each of its use sites.
using Module = DnnMigration.Domain.Entities.Module;

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

    // The three negative role identifiers below are REAL PERSISTED PRINCIPALS, not absence markers, and
    // that is the single most dangerous thing about this table. Roles.RoleID is IDENTITY (0, 1)
    // (01.00.00.SqlDataProvider:L115), so 0 is an ordinary role, and the shipped pseudo-roles occupy the
    // negative range beneath it. Seeded Tabs.AuthorizedRoles values in the same script include '-1;',
    // '0;' and '-2;', which is direct evidence that both zero and the negatives are stored and matched.
    // Library/Components/Shared/Null.vb:L41-L45 separately defines the legacy integer absence sentinel as
    // -1, so -1 carried two incompatible meanings in the legacy source at once. The target keeps them
    // apart structurally: absence is a null nullable, and -1 in a role column is All Users.
    private const int AllUsersRoleId = -1;

    private const int SuperUserRoleId = -2;

    private const int UnauthenticatedRoleId = -3;

    /// <summary>An ordinary role whose identifier is zero, which this schema issues first.</summary>
    private const int ZeroRoleId = 0;

    private const int MemberRoleId = 5;

    private const int ForeignRoleId = 6;

    private const string MemberRoleName = "Measured Members";

    /// <summary>A page whose identifier is zero, which <c>Tabs.TabID</c> issues first.</summary>
    private const int ZeroTabId = 0;

    private const int FirstPermissionId = 41;

    private const int SecondPermissionId = 42;

    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    private const string PageScopeCode = "SYSTEM_TAB";

    // A scope code belonging to a subsystem this migration excludes. The excluded subsystem's real code
    // is deliberately NOT spelled here: this folder is checked for that subsystem's vocabulary, and
    // writing the literal would report the suite as reintroducing the very feature it proves is absent.
    // What matters to the assertion is only that the code differs from the one being asked for.
    private const string ExcludedSubsystemScopeCode = "SYSTEM_EXCLUDED_SUBSYSTEM";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The catalogue is projected upper-cased, without duplicates, and in ordinal order.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the upper-casing is now structural rather than defensive. Permission.PermissionKey is
    /// the closed PermissionKey enumeration, whose member names are the stored spellings, so a catalogue
    /// row carrying mixed case or surrounding whitespace is no longer representable at the entity
    /// boundary at all - the persistence mapping resolves the stored text to a member on the way in and
    /// re-emits the canonical name on the way out. What this test still pins is the part the service owns
    /// and a closed vocabulary does not give away for free: duplicate rows collapse, and the answer is
    /// ordered ordinally rather than in whatever order the store returned. The equivalent hazard for text
    /// that never passes through the enumeration is covered by
    /// <see cref="EffectiveKeys_DropAnUnusableStoredKey"/> on the grant path.
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

    /// <summary>
    /// A grant naming no usable key contributes nothing.
    /// </summary>
    /// <param name="stored">The stored key text.</param>
    /// <remarks>
    /// MIGRATION: this covers what <see cref="Catalogue_IsUpperCasedDistinctAndOrdinallySorted"/> no
    /// longer can. The effective-key reads answer with the text the grant rows actually carried, which
    /// the closed enumeration never filters, so a blank or whitespace-only key remains reachable on this
    /// path and must still be dropped rather than surfaced as an empty permission.
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
    /// <remarks>
    /// MIGRATION: the legacy reader took both filters in one call because a null in either argument was
    /// a wildcard. The repository contract that replaces it declares the definition-scoped read the
    /// provider actually had, so the definition goes to the store and the code narrows the result. The
    /// narrowing is asserted rather than assumed: a definition declares a handful of entries, and a
    /// filter that reached the store but was then ignored would look like a working query.
    /// </remarks>
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
            .GetPermissionKeysAsync("SYSTEM_MODULE_DEFINITION", 42, CancellationToken.None);

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                42,
                It.IsAny<CancellationToken>()),
            Times.Once());

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" }, "the entry under the other code is filtered away");
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

    /// <summary>
    /// A code filter on its own is answered by asking the store once per key in the closed set.
    /// </summary>
    /// <remarks>
    /// MIGRATION: "which keys exist under this code" is exactly the question the legacy code-and-key
    /// reader answered, one key at a time, so the service asks it once per member of the closed
    /// PermissionKey enumeration - four reads, bounded by the schema rather than by the data. Only the
    /// keys the store actually reported are returned, which is what distinguishes this answer from the
    /// unfiltered one below.
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
            .GetPermissionKeysAsync("SYSTEM_TAB", null, CancellationToken.None);

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
    /// MIGRATION: the legacy reader answered this by passing a null in both arguments, which its body
    /// treated as a wildcard over the whole table. The repository contract takes no wildcard, and it
    /// does not need to: Permission.PermissionKey IS the closed PermissionKey enumeration, so the
    /// enumeration is the complete vocabulary any catalogue row could carry. Asking the store would put
    /// a question whose answer the schema already fixes, and would answer "everything" with less than
    /// everything on an installation whose catalogue is missing a row.
    /// </remarks>
    [Fact]
    public async Task Catalogue_AnswersAnAbsentFilterFromTheClosedKeySet()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
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
    /// <remarks>
    /// This is a consequence of the order in which the checks run, and it is the desired one: the answer for
    /// a host account is the same for every scope, so resolving the scope could only turn a correct answer
    /// into a not-found. It also means the endpoint cannot be used by a host account to discover which
    /// module identifiers exist, which is consistent with the refusal-not-absence rule the protected routes
    /// follow.
    /// <para>
    /// MIGRATION: a host account is answered with the whole closed key set rather than with whichever
    /// keys the catalogue table happens to contain. That reproduces the legacy rule rather than departing
    /// from it: PortalSecurity.IsInRoles returned true for a host account at PortalSecurity.vb:L123
    /// before examining a single grant, so the legacy answer was "everything" and was never derived from
    /// the catalogue at all. Since Permission.PermissionKey IS the closed enumeration, the enumeration is
    /// what "everything" means, and it is also the only answer the permission repository's contract can
    /// support - that contract mirrors the legacy provider blocks, which offered no unfiltered catalogue
    /// read. A catalogue-derived answer would additionally under-report on any installation whose
    /// catalogue is missing a row, which is the one case where the two answers differ and the one case
    /// where "everything" must not shrink.
    /// </para>
    /// </remarks>
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
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
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
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
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
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
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
            modules => modules.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
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
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
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
            modules => modules.GetByIdAsync(
                It.IsAny<int>(),
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
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
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
            .Setup(modules => modules.GetByIdAsync(
                It.IsAny<int>(),
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
        // MIGRATION: both grant tables are cleaned, because the legacy provider declared the removal
        //            as two members over two tables - DeleteModulePermissionsByUserID at core
        //            DataProvider.vb:L296 and DeleteTabPermissionsByUserID at L305. A grant left
        //            behind on either table would outlive the account that held it.
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
        host.Catalogue = [Entry(PermissionKey.VIEW)];

        Result<IReadOnlyList<string>> hostAnswer = await host.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        hostAnswer.IsSuccess.Should().BeTrue(hostAnswer.Reason?.ToString());

        // MIGRATION: the host answer is the closed key set, for the reason set out on
        //            EffectiveKeys_ForAHostAccountIgnoreAnUnresolvableScope. What this test pins is that
        //            the host account was RESOLVED from outside the tenant at all; the ordinary account
        //            below is not.
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

    /// <summary>
    /// Every collaborator is required.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        Mock<IPermissionRepository> permissions = new();
        Mock<IPermissionEvaluator> evaluator = new();
        Mock<IPortalRepository> portals = new();
        Mock<IModuleRepository> modules = new();
        Mock<ITabRepository> tabs = new();
        Mock<IUserRepository> users = new();
        Mock<IClock> clock = new();
        PortalOptions options = new();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                null!, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, null!, portals.Object, modules.Object, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, null!, modules.Object, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, null!, tabs.Object, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, null!, users.Object, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, null!, clock.Object, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object, null!, options);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object,
                evaluator.Object,
                portals.Object,
                modules.Object,
                tabs.Object,
                users.Object,
                clock.Object,
                null!);
        });
    }

    // =================================================================================================
    // Contract shape.
    //
    // These tests read the three permission contracts through reflection and never construct anything.
    // Reflection over an abstraction is inspection, not reach: it asks what a contract declares without
    // naming a single implementation. The concrete precedence reducer is deliberately NOT reachable from
    // this project and is not reached here by any means - see the coverage-placement note below.
    // =================================================================================================

    /// <summary>
    /// The three permission contracts this project can legitimately see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: coverage placement is a deliberate architectural choice, not an oversight. The concrete
    /// precedence reducer lives in the infrastructure assembly, which this test project does not
    /// reference and must never reference - AAP Rule T1 makes the dependency direction a compile error
    /// rather than a review finding, and AAP section 0.8.3 baseline B1 states the layering is enforced by
    /// the compiler rather than by convention. That class is additionally declared internal to its own
    /// assembly, so it is unreachable twice over: the assembly is off the reference graph, and the type is
    /// not public within it. Algorithm-level verification of the concrete reducer therefore belongs to the
    /// integration project, which reaches it legitimately through its api reference and the container, and
    /// proves it over real rows and real HTTP.
    /// </para>
    /// <para>
    /// What lives here instead is the executable <em>specification</em> that reducer must satisfy, written
    /// against the domain repository contract and the real grant entities. A specification a future
    /// implementer can run is worth more than a description of one, and it costs no layering violation.
    /// </para>
    /// <para>
    /// Note which contract is which. The precedence <em>abstraction</em> is an application-layer contract
    /// and so is fully reachable and legitimately substitutable; only its concrete implementation is
    /// infrastructure. Conflating the two would either forbid a legal test or excuse an illegal one.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Type> PermissionContracts =>
    [
        typeof(IPermissionService),
        typeof(IPermissionRepository),
        typeof(IPermissionEvaluator),
    ];

    /// <summary>
    /// The assemblies a permission contract is allowed to mention on its surface.
    /// </summary>
    /// <remarks>
    /// Described by example rather than by name so the set cannot drift from reality: the two assemblies
    /// this project references, plus the framework assemblies that supply the primitives, tasks and
    /// read-only sequence types the contracts are built from. Anything else - a persistence library, a
    /// mapping library, a web framework, or an infrastructure type - is absent by construction rather
    /// than by a blacklist that a new dependency could slip past.
    /// </remarks>
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

    /// <summary>
    /// Every method the three permission contracts declare.
    /// </summary>
    /// <returns>The declared methods, paired with the contract that declares them.</returns>
    private static IEnumerable<(Type Contract, MethodInfo Member)> ContractMembers()
        => PermissionContracts.SelectMany(contract => contract
            .GetMethods()
            .Select(member => (Contract: contract, Member: member)));

    /// <summary>
    /// Unwraps a declared return type down to the value it ultimately produces.
    /// </summary>
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

    /// <summary>
    /// Expands a type into itself and every generic argument it is built from.
    /// </summary>
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

    /// <summary>
    /// Every type that appears on a member's surface, in its parameters or its return value.
    /// </summary>
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
    /// <remarks>
    /// AAP Rule T6 and section 0.8.3 baseline B4: asynchronous throughout, with no synchronous-over-
    /// asynchronous bridging anywhere in the request path. This pins all three halves of that - the task
    /// return, the naming that tells a caller to await it, and the token that lets a caller abandon it.
    /// The token is required to be last because a trailing optional token is the convention the whole
    /// solution is written to; a token buried mid-list is the shape that gets silently dropped at a call
    /// site.
    /// </remarks>
    [Fact]
    public void Contract_EveryMemberIsAnAwaitableCancellableTask()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            string described = $"{contract.Name}.{member.Name}";

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

    /// <summary>
    /// No permission contract member reports anything through an output or reference parameter.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy status-by-reference idiom is gone. AAP section 0.7.4 measured 30 in-scope
    /// mutate-and-report-by-reference signatures and states flatly that no output or reference parameter
    /// appears in any target public api. The replacement is visible in the return types the previous test
    /// pins: an outcome wrapper carries both the produced value and the reason it could not be produced,
    /// so a caller can no longer read a status it forgot to pass a variable for.
    /// </remarks>
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

    /// <summary>
    /// The persistence surface answers no access question at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single-reducer guard, and it is the assertion that actually protects the architecture.
    /// AAP section 0.5.1.2 relocates permission evaluation out of the three legacy controllers and into a
    /// dedicated component; the repository contract's own documentation states that nothing on it returns
    /// an access decision. Two reducers that can disagree about whether a caller may act is the worst
    /// available outcome in this area, so the way to prevent a second one appearing is to keep the shape
    /// of a decision off the layer that holds the rows. A repository member that started returning a
    /// verdict would be exactly that second reducer, and this test fails the moment one does.
    /// </para>
    /// <para>
    /// Stated as a property of the produced value rather than of the member name, so it cannot be evaded
    /// by choosing a name the guard does not recognise.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A decision is an outcome carrying a boolean, never a bare boolean.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction this pins is the one that matters most to a caller and is the easiest to lose. A
    /// refusal and a failure are not the same event: "you may not do this" is a successful answer whose
    /// value happens to be false, whereas "the store could not be reached" is a failure with no answer at
    /// all. A bare boolean cannot express the difference and would force the second case to masquerade as
    /// the first, which reads to an operator as a permissions problem when it is an availability problem.
    /// </para>
    /// <para>
    /// This test also replaces a weaker check that could not be written honestly. The decision members do
    /// live on the application contract - deliberately, because the api authorisation handler must have a
    /// single authorised route to ask the question - so asserting that no decision exists there would be
    /// asserting something false. The prohibition that carries the architectural weight is "no second
    /// reducer anywhere", which the previous test enforces where it can actually be broken.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_ADecisionIsAnOutcomeCarryingABooleanNeverABareBoolean()
    {
        MethodInfo[] decisions = typeof(IPermissionService)
            .GetMethods()
            .Where(member => ProducedValue(member.ReturnType) == typeof(bool))
            .ToArray();

        decisions.Should().HaveCount(
            2,
            "a caller asks about a module or about a page, and those are the only two scopes a grant is recorded against");

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

    /// <summary>
    /// Every sequence a permission contract hands back is a read-only generic sequence.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the untyped and pre-generics collection types are gone, and so is any bespoke wrapper.
    /// Six of the nine legacy catalogue members returned an untyped list, and the two grant controllers
    /// were fronted by hand-written pre-generics wrapper classes; AAP section 0.5.1.10 records that those
    /// wrappers produce no target type at all. Asserting the positive shape rather than listing the
    /// banished type names is both stronger and self-maintaining: a newly invented wrapper, a mutable
    /// list, or an untyped sequence all fail this, including ones nobody thought to blacklist.
    /// </remarks>
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

    /// <summary>
    /// A permission contract mentions only domain, application and framework types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the reflection-driven row hydrator and the reflection-created static provider accessor
    /// are both gone, replaced by the object-relational materializer and constructor injection, and
    /// neither leaves a trace on these contracts. This test is what makes that verifiable rather than
    /// merely asserted: it proves no persistence type, no query type, no mapping library type and no
    /// infrastructure type can appear on the surface, because every surface type must come from an
    /// assembly this project already legitimately depends on.
    /// </para>
    /// <para>
    /// Expressed as an allow-list of assemblies rather than a deny-list of names on purpose. A deny-list
    /// only rejects what its author remembered; this rejects everything that was not explicitly permitted,
    /// which includes dependencies that do not exist yet.
    /// </para>
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
    /// No permission contract carries a member for the excluded file-management subsystem.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the path-scoped catalogue lookup and the eight file-system grant members are not
    /// ported. The subsystem they serve is out of scope, so there is no target feature for a grant keyed
    /// by a storage path to serve, and the target declares no file-system grant entity. The excluded
    /// vocabulary is checked as separate short tokens rather than as the subsystem's full type names,
    /// because this folder is itself checked for those names and quoting them would report the suite as
    /// reintroducing what it proves is absent.
    /// </remarks>
    [Fact]
    public void Contract_CarriesNoMemberForTheExcludedStorageSubsystem()
    {
        string[] excludedVocabulary = ["Folder", "Directory", "Path"];

        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (string token in excludedVocabulary)
            {
                member.Name.Should().NotContain(
                    token,
                    $"{contract.Name}.{member.Name} names a subsystem this migration excludes, so no member should mention it");
            }
        }
    }

    // =================================================================================================
    // The precedence specification.
    //
    // MIGRATION: DENY PRECEDENCE UNIFIES TWO INCONSISTENT LEGACY PATHS. THIS IS A DELIBERATE
    //            BEHAVIOURAL DIVERGENCE, AND IT IS THE HEADLINE FINDING OF THIS FILE.
    //
    //            The legacy source decided the same question two different ways and did not reconcile
    //            them. TabPermissionController.vb:L38-L54 and its module twin walked the grant rows and
    //            returned True on the FIRST row whose principal the caller matched. Neither one tested
    //            the allow-or-deny flag at all, so a row recorded specifically to REFUSE a role the
    //            caller held would GRANT that caller access - the refusal was not merely ignored, it was
    //            read as permission. Meanwhile ModulePermissionController.vb:L239-L252 and its tab twin,
    //            which flattened grants into a delimited string, DID filter on that flag being set.
    //
    //            The two paths met inside one method. PortalSecurity.vb:L521 computed view access from
    //            the flag-FILTERED delimited string, while L522 computed edit access from the UNFILTERED
    //            first-match-wins walk. The measured consequence is that in the legacy application VIEW
    //            honoured the allow-or-deny flag and EDIT silently did not.
    //
    //            The target unifies both paths under one rule: deny beats allow, for every key. A stored
    //            refusal now refuses. Every test in this section pins the TARGET rule, never the legacy
    //            first-match-wins behaviour - so a reader who expects legacy parity here should read this
    //            note as the explanation rather than these tests as a defect.
    //
    // These tests exercise a specification written against the domain repository contract and the real
    // grant entities. They construct no infrastructure type and reach none.
    // =================================================================================================

    /// <summary>
    /// A caller holding no grant at all is refused rather than failed.
    /// </summary>
    /// <remarks>
    /// Two distinct absences, one answer. An empty catalogue means the permission is not defined for this
    /// scope; an empty grant set means it is defined but conferred on nobody. Neither is an error, and
    /// both must resolve to a successful refusal: a caller asking "may I?" is entitled to be told "no"
    /// rather than handed an exception to interpret.
    /// </remarks>
    [Fact]
    public async Task Precedence_WithNoCatalogueEntryAndNoGrantRefusesWithoutFailing()
    {
        GrantPrecedenceSpecification withoutCatalogue = new(StoreWith([], [ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId)]).Object);

        Result<bool> noCatalogue = await withoutCatalogue.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        noCatalogue.IsSuccess.Should().BeTrue("an undefined permission is a question with an answer, not a fault");
        noCatalogue.Value.Should().BeFalse("a permission the catalogue does not define cannot be held by anyone");

        GrantPrecedenceSpecification withoutGrants = new(StoreWith([CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode)]).Object);

        Result<bool> noGrants = await withoutGrants.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        noGrants.IsSuccess.Should().BeTrue("a permission conferred on nobody is still a question with an answer");
        noGrants.Value.Should().BeFalse("a defined permission with no grant row confers nothing");
    }

    /// <summary>
    /// The principal matrix: which stored role identifier matches which caller.
    /// </summary>
    /// <param name="grantedRoleId">The role identifier recorded on the grant row.</param>
    /// <param name="callerIsAuthenticated">Whether the caller has been identified.</param>
    /// <param name="callerIsHost">Whether the caller is an installation-wide account.</param>
    /// <param name="expected">Whether the row should match the caller.</param>
    /// <remarks>
    /// <para>
    /// Ported from the legacy membership loop at <c>PortalSecurity.vb:L115-L136</c>, which tested the
    /// installation-wide flag FIRST inside the loop and then, for an ordinary row, admitted the
    /// unauthenticated pseudo-role only when the request was in fact unauthenticated and the all-users
    /// pseudo-role unconditionally.
    /// </para>
    /// <para>
    /// The row identified by zero is the case worth stating out loud. <c>Roles.RoleID</c> is
    /// <c>IDENTITY (0, 1)</c>, so zero is the first role the schema ever issues and grants exactly like
    /// any other. A future reader who "tidies" this rule into a positive-identifier test would break this
    /// case and, with it, the shipped administrators role of a real installation - which is precisely why
    /// it is pinned here rather than left implicit.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(AllUsersRoleId, true, false, true)]
    [InlineData(AllUsersRoleId, false, false, true)]
    [InlineData(UnauthenticatedRoleId, false, false, true)]
    [InlineData(UnauthenticatedRoleId, true, false, false)]
    [InlineData(SuperUserRoleId, true, true, true)]
    [InlineData(SuperUserRoleId, true, false, false)]
    [InlineData(ZeroRoleId, true, false, true)]
    [InlineData(MemberRoleId, true, false, true)]
    [InlineData(ForeignRoleId, true, false, false)]
    public async Task Precedence_MatchesAStoredRoleIdentifierAgainstTheCaller(
        int grantedRoleId,
        bool callerIsAuthenticated,
        bool callerIsHost,
        bool expected)
    {
        CallerIdentity caller = new(
            callerIsAuthenticated ? UserId : null,
            new HashSet<int> { ZeroRoleId, MemberRoleId },
            [MemberRoleName],
            callerIsAuthenticated,
            callerIsHost);

        GrantPrecedenceSpecification specification = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: grantedRoleId)]).Object);

        Result<bool> result = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            caller,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Be(
            expected,
            $"a grant to role {grantedRoleId} against an authenticated={callerIsAuthenticated}, host={callerIsHost} caller must resolve this way");
    }

    /// <summary>
    /// A grant naming an account matches that account and no other.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the bracketed pseudo-role encoding is gone. A legacy account-scoped grant was tested by
    /// synthesising the literal text <c>"["</c>, the account identifier and <c>"]"</c> and passing that
    /// through the very same role-name membership helper a real role name went through, so an account and
    /// a role were indistinguishable to the matcher. The target records an account-scoped grant in its own
    /// nullable account column, so the two principals are different columns rather than different string
    /// shapes, and no encoding has to be parsed to tell them apart.
    /// </remarks>
    [Fact]
    public async Task Precedence_AGrantNamingAnAccountMatchesOnlyThatAccount()
    {
        Mock<IPermissionRepository> store = StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: true, userId: UserId)]);

        GrantPrecedenceSpecification specification = new(store.Object);

        Result<bool> named = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            Member(MemberRoleId),
            CancellationToken.None);

        named.Value.Should().BeTrue("the grant names this account");

        Result<bool> other = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            new CallerIdentity(UserId + 1, new HashSet<int> { MemberRoleId }, [MemberRoleName], true, false),
            CancellationToken.None);

        other.Value.Should().BeFalse(
            "an account-scoped grant confers nothing on a different account, however many roles that account shares with the named one");
    }

    /// <summary>
    /// A row naming neither a role nor an account matches nobody.
    /// </summary>
    /// <remarks>
    /// Both principal columns are nullable, so a row naming neither is representable and someone will
    /// eventually store one. It has to fail closed. The alternative - treating an unspecified principal as
    /// unrestricted - would turn a data-entry mistake into an open door, and it would do so silently.
    /// </remarks>
    [Fact]
    public async Task Precedence_ARowNamingNeitherRoleNorAccountFailsClosed()
    {
        GrantPrecedenceSpecification specification = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: true)]).Object);

        Result<bool> forMember = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        Result<bool> forHost = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            HostAccount(),
            CancellationToken.None);

        forMember.Value.Should().BeFalse("a grant that names no principal confers nothing on anyone");
        forHost.Value.Should().BeFalse("not even an installation-wide account benefits from a row that names nobody");
    }

    /// <summary>
    /// A stored refusal refuses, and it does so whichever order the rows arrive in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion the whole section exists for. Under the legacy first-match-wins walk the
    /// answer depended on which row the store happened to return first, so the same data could grant or
    /// refuse across two runs. The target reduces the entire matching set before answering, which makes
    /// the outcome a property of the data rather than of the row order - and the only honest way to prove
    /// that is to assert the same answer from both orderings of the same two rows.
    /// </para>
    /// <para>
    /// Read the arrangement carefully: the allowance and the refusal both match this caller, and the
    /// refusal wins. That is the divergence recorded in the section note above, asserted deliberately.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Precedence_AnAllowanceAndARefusalResolveToRefusalInEitherRowOrder()
    {
        ModulePermission allowance = ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId);
        ModulePermission refusal = ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: AllUsersRoleId);

        IReadOnlyList<Permission> catalogue = [CatalogueEntry(FirstPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode)];

        GrantPrecedenceSpecification allowanceFirst = new(StoreWith(catalogue, [allowance, refusal]).Object);
        GrantPrecedenceSpecification refusalFirst = new(StoreWith(catalogue, [refusal, allowance]).Object);

        Result<bool> withAllowanceFirst = await allowanceFirst.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            Member(MemberRoleId),
            CancellationToken.None);

        Result<bool> withRefusalFirst = await refusalFirst.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            Member(MemberRoleId),
            CancellationToken.None);

        withAllowanceFirst.Value.Should().BeFalse(
            "a matching refusal suppresses a matching allowance even when the allowance is encountered first");
        withRefusalFirst.Value.Should().BeFalse(
            "the same data must produce the same answer, so the reversed ordering resolves identically");
        withRefusalFirst.Value.Should().Be(
            withAllowanceFirst.Value,
            "the decision is a property of the grant set and must not depend on the order the store returned it in");
    }

    /// <summary>
    /// A refusal recorded on its own refuses.
    /// </summary>
    [Fact]
    public async Task Precedence_ARefusalOnItsOwnRefuses()
    {
        GrantPrecedenceSpecification specification = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: MemberRoleId)]).Object);

        Result<bool> result = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            Member(MemberRoleId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeFalse(
            "the legacy walk read this row as permission because it never looked at the flag; the target reads it as the refusal it was recorded to be");
    }

    /// <summary>
    /// An installation-wide account is bound by a refusal and cannot invent a permission.
    /// </summary>
    /// <remarks>
    /// The legacy loop tested the installation-wide flag first, which made such an account match any row
    /// it encountered - but it still had to encounter a row, because the loop over an empty set returned
    /// False. Both halves of that are preserved: the account matches rows it would not otherwise match,
    /// and it conjures nothing where nothing is recorded. It also does not outrank a refusal, because
    /// matching a row is not the same as overriding what the row says.
    /// </remarks>
    [Fact]
    public async Task Precedence_AnInstallationWideAccountIsStillBoundByARefusalAndByAnEmptySet()
    {
        GrantPrecedenceSpecification refused = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: ForeignRoleId)]).Object);

        Result<bool> againstRefusal = await refused.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            HostAccount(),
            CancellationToken.None);

        againstRefusal.Value.Should().BeFalse(
            "matching every row is not the same as overruling one, so an explicit refusal still refuses");

        GrantPrecedenceSpecification empty = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode)]).Object);

        Result<bool> againstNothing = await empty.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.EDIT,
            HostAccount(),
            CancellationToken.None);

        againstNothing.Value.Should().BeFalse(
            "an account that matches any row it finds still finds none here, and cannot create a permission that was never conferred");
    }

    /// <summary>
    /// Several catalogue entries naming one key are all consulted, and a refusal under any of them wins.
    /// </summary>
    /// <remarks>
    /// The catalogue can define one key more than once - the code-and-key read is documented as able to
    /// match many rows, which is why it returns a sequence and not a single entry. Grants hang off a
    /// specific entry, so a key defined twice has two independent grant sets and both have to be reduced
    /// together. Consulting only the first entry would let a refusal hide behind whichever definition the
    /// store happened to order first.
    /// </remarks>
    [Fact]
    public async Task Precedence_ReducesEveryCatalogueEntryNamingTheSameKey()
    {
        IReadOnlyList<Permission> catalogue =
        [
            CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode),
            CatalogueEntry(SecondPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode),
        ];

        GrantPrecedenceSpecification bothAllow = new(StoreWith(
            catalogue,
            [
                ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId),
                ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId),
            ]).Object);

        Result<bool> allowed = await bothAllow.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        allowed.Value.Should().BeTrue("both definitions confer the key and neither refuses it");

        GrantPrecedenceSpecification secondRefuses = new(StoreWith(
            catalogue,
            [
                ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId),
                ModuleGrantRow(SecondPermissionId, allowAccess: false, roleId: MemberRoleId),
            ]).Object);

        Result<bool> refused = await secondRefuses.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        refused.Value.Should().BeFalse(
            "a refusal recorded against the second definition of the same key is not hidden by an allowance under the first");
    }

    /// <summary>
    /// A catalogue entry belonging to another scope code is never admitted.
    /// </summary>
    /// <remarks>
    /// The scope code is deliberately free text - the product ships its own codes and every installed
    /// module contributes more - so the read is filtered on the code rather than on a closed set, and the
    /// comparison is exact. This matters beyond tidiness: one of the codes present in a real installation
    /// belongs to a subsystem this migration excludes, and admitting an entry from it would resurrect a
    /// feature that has no target implementation to enforce it. That subsystem's own code is not written
    /// here on purpose, as the constant's comment explains; what the test needs is only that the code
    /// differs.
    /// </remarks>
    [Fact]
    public async Task Precedence_NeverAdmitsACatalogueEntryFromAnotherScopeCode()
    {
        GrantPrecedenceSpecification specification = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ExcludedSubsystemScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: AllUsersRoleId)]).Object);

        Result<bool> result = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeFalse(
            "the entry belongs to a different scope code, so its grants say nothing about the code that was asked about");
    }

    /// <summary>
    /// A catalogue entry naming a different key is never admitted.
    /// </summary>
    [Fact]
    public async Task Precedence_NeverAdmitsACatalogueEntryNamingADifferentKey()
    {
        GrantPrecedenceSpecification specification = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: AllUsersRoleId)]).Object);

        Result<bool> result = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        result.Value.Should().BeFalse(
            "an unrestricted grant of one key confers nothing about a different key");
    }

    /// <summary>
    /// Page-scoped resolution answers both live keys from the page's own grants.
    /// </summary>
    /// <param name="permissionKey">The key being tested.</param>
    /// <remarks>
    /// The grant tables are keyed by page identifier, which is the whole reason the page aggregate is in
    /// scope at all. Both keys are exercised because the legacy source resolved them through two different
    /// code paths - the one that honoured the allow-or-deny flag and the one that did not - and the target
    /// resolves them through one.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    public async Task Precedence_ResolvesAPageScopedGrantForBothLiveKeys(PermissionKey permissionKey)
    {
        GrantPrecedenceSpecification allowed = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, permissionKey, PageScopeCode)],
            pageGrants: [PageGrantRow(TabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId)]).Object);

        Result<bool> granted = await allowed.HasPageGrantAsync(
            TabId,
            PageScopeCode,
            permissionKey,
            Member(MemberRoleId),
            CancellationToken.None);

        granted.Value.Should().BeTrue($"the page confers {permissionKey} on a role the caller holds");

        GrantPrecedenceSpecification refused = new(StoreWith(
            [CatalogueEntry(FirstPermissionId, permissionKey, PageScopeCode)],
            pageGrants:
            [
                PageGrantRow(TabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId),
                PageGrantRow(TabId, FirstPermissionId, allowAccess: false, roleId: AllUsersRoleId),
            ]).Object);

        Result<bool> denied = await refused.HasPageGrantAsync(
            TabId,
            PageScopeCode,
            permissionKey,
            Member(MemberRoleId),
            CancellationToken.None);

        denied.Value.Should().BeFalse(
            $"deny precedence applies to {permissionKey} on a page exactly as it does on a module, which is the unification this migration performs");
    }

    /// <summary>
    /// The page whose identifier is zero is a real page.
    /// </summary>
    /// <remarks>
    /// <c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>, so zero is the first page a portal ever gets and is
    /// frequently the home page of a real installation. Treating it as "no page" - which any truthiness or
    /// positive-identifier test would do - would silently drop every grant recorded against it. Asserted
    /// both that the answer is correct and that the store was genuinely asked about page zero.
    /// </remarks>
    [Fact]
    public async Task Precedence_TreatsThePageIdentifiedByZeroAsARealPage()
    {
        Mock<IPermissionRepository> store = StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, PageScopeCode)],
            pageGrants: [PageGrantRow(ZeroTabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId)]);

        GrantPrecedenceSpecification specification = new(store.Object);

        Result<bool> result = await specification.HasPageGrantAsync(
            ZeroTabId,
            PageScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeTrue("page zero is an ordinary page and its grants confer exactly as any other page's do");

        store.Verify(
            permissions => permissions.GetTabPermissionsByTabIdAsync(
                ZeroTabId,
                FirstPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the store must be asked about page zero rather than have the question skipped as though no page were named");
    }

    /// <summary>
    /// The supplied cancellation token reaches every read a decision performs.
    /// </summary>
    /// <remarks>
    /// AAP Rule T6 and baseline B4 are not satisfied by returning a task; a token that is accepted and then
    /// dropped leaves a cancelled request still reading. Verified against the exact token instance rather
    /// than against any token, which is the only form of this assertion that can actually fail.
    /// </remarks>
    [Fact]
    public async Task Precedence_PassesTheSuppliedCancellationTokenToEveryRead()
    {
        Mock<IPermissionRepository> store = StoreWith(
            [CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode)],
            [ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId)]);

        GrantPrecedenceSpecification specification = new(store.Object);

        using CancellationTokenSource source = new();

        Result<bool> result = await specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            source.Token);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        store.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                ModuleDefinitionScopeCode,
                PermissionKey.VIEW,
                source.Token),
            Times.Once,
            "the catalogue read must carry the caller's token");

        store.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                ModuleId,
                FirstPermissionId,
                source.Token),
            Times.Once,
            "the grant read must carry the same token, so cancelling the request abandons both reads");
    }

    /// <summary>
    /// A store that cannot be reached produces a fault, never a refusal.
    /// </summary>
    /// <remarks>
    /// The most consequential failure mode in this area, and the easiest to get wrong by being defensive in
    /// the wrong direction. Swallowing the fault and answering false would tell the caller they are not
    /// allowed, when the truth is that nobody currently knows - which sends an operator to the permission
    /// grid to debug an outage. Unexpected faults propagate and are translated once at the api boundary;
    /// only <em>expected</em> conditions are reported through the outcome type.
    /// </remarks>
    [Fact]
    public async Task Precedence_WhenTheStoreCannotBeReachedTheFaultSurfacesRatherThanARefusal()
    {
        Mock<IPermissionRepository> store = new(MockBehavior.Strict);

        store
            .Setup(permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("the grant store did not respond"));

        GrantPrecedenceSpecification specification = new(store.Object);

        Func<Task> asking = () => specification.HasModuleGrantAsync(
            ModuleId,
            ModuleDefinitionScopeCode,
            PermissionKey.VIEW,
            Member(MemberRoleId),
            CancellationToken.None);

        await asking.Should().ThrowAsync<TimeoutException>(
            "an unreachable store is an availability fault, and reporting it as a denial would misdiagnose an outage as a permissions problem");
    }

    /// <summary>
    /// Role names are compared ordinally, so a differently cased name is a different role.
    /// </summary>
    /// <param name="storedRoleName">The role name recorded against the grant.</param>
    /// <param name="expected">Whether the caller's own role should match it.</param>
    /// <remarks>
    /// The legacy comparison was Visual Basic string equality, which is binary rather than culture-aware,
    /// so ordinal comparison is behavioural parity rather than a hardening choice. It also has to stay
    /// ordinal: a culture-sensitive or case-insensitive comparison would make role matching depend on the
    /// server's locale, which is how a permission check starts behaving differently in one deployment than
    /// in another. Trimming would be just as wrong, because a stored name with a trailing space is a
    /// distinct row that an administrator can see and delete.
    /// </remarks>
    [Theory]
    [InlineData(MemberRoleName, true)]
    [InlineData("measured members", false)]
    [InlineData("MEASURED MEMBERS", false)]
    [InlineData("Measured Members ", false)]
    [InlineData("", false)]
    public void Precedence_ComparesRoleNamesOrdinally(string storedRoleName, bool expected)
    {
        bool matched = GrantPrecedenceSpecification.IsInRoles(
            [storedRoleName],
            Member(MemberRoleId),
            AllUsersRoleName,
            UnauthenticatedRoleName);

        matched.Should().Be(
            expected,
            $"the stored name \"{storedRoleName}\" must be compared to the caller's roles as raw text, without folding case, trimming, or applying a culture");
    }

    /// <summary>
    /// The pseudo-role names keep their legacy admission rules.
    /// </summary>
    /// <remarks>
    /// The named pseudo-roles are the string-keyed counterpart of the numeric principals covered above, and
    /// they are still reachable because the delimited role columns in the schema store names rather than
    /// identifiers. The all-users name admits everyone; the unauthenticated name admits only a caller who
    /// has not been identified, which is what makes an anonymous caller a supported question rather than a
    /// rejected one; and an empty entry admits nobody, because the legacy loop skipped empty entries and a
    /// trailing delimiter produces one on almost every stored value.
    /// </remarks>
    [Fact]
    public void Precedence_AppliesTheLegacyAdmissionRulesToThePseudoRoleNames()
    {
        GrantPrecedenceSpecification.IsInRoles(
            [AllUsersRoleName],
            Anonymous(),
            AllUsersRoleName,
            UnauthenticatedRoleName).Should().BeTrue("the all-users name admits an unidentified caller");

        GrantPrecedenceSpecification.IsInRoles(
            [AllUsersRoleName],
            Member(MemberRoleId),
            AllUsersRoleName,
            UnauthenticatedRoleName).Should().BeTrue("the all-users name admits an identified caller too, unconditionally");

        GrantPrecedenceSpecification.IsInRoles(
            [UnauthenticatedRoleName],
            Anonymous(),
            AllUsersRoleName,
            UnauthenticatedRoleName).Should().BeTrue("the unauthenticated name admits a caller who has not been identified");

        GrantPrecedenceSpecification.IsInRoles(
            [UnauthenticatedRoleName],
            Member(MemberRoleId),
            AllUsersRoleName,
            UnauthenticatedRoleName).Should().BeFalse("an identified caller is not unauthenticated, however the row is worded");

        GrantPrecedenceSpecification.IsInRoles(
            [],
            HostAccount(),
            AllUsersRoleName,
            UnauthenticatedRoleName).Should().BeFalse("an installation-wide account matches rows it finds and conjures none, so an empty set admits nobody");
    }

    /// <summary>
    /// An absent page scope means "not scoped to a page", and is never read as page zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two states are genuinely different questions and this is the collision that makes them easy to
    /// confuse: an absent scope asks about the whole portal, while page zero asks about one specific page
    /// that happens to carry the first identifier the schema issues. Because a nullable integer defaults to
    /// zero when it is coalesced carelessly, the failure mode is silent - the caller asks a portal-wide
    /// question and receives one page's answer.
    /// </para>
    /// <para>
    /// MIGRATION: an absent identifier is a null nullable, never a numeric sentinel. The legacy source
    /// passed the integer absence sentinel to mean "no page", and that value is not free: it identifies the
    /// first portal in this schema and, in a role column, the all-users principal. Zero is not free either,
    /// because the page, role and module identity columns all seed at zero. This test is the executable form
    /// of that rule for the page argument.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PageScope_AbsentIsNotTheSameQuestionAsPageZero()
    {
        Harness withoutPageScope = Harness.Ready();
        withoutPageScope.PortalKeys = ["VIEW"];

        Result<IReadOnlyList<string>> portalWide = await withoutPageScope.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: null,
            tabId: null,
            cancellationToken: CancellationToken.None);

        portalWide.IsSuccess.Should().BeTrue(portalWide.Reason?.ToString());

        withoutPageScope.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "an absent page scope must ask no page-scoped question at all, rather than quietly asking about page zero");

        Harness withPageZero = Harness.Ready();
        withPageZero.Tab.TabId = ZeroTabId;
        withPageZero.TabKeys = ["VIEW"];

        Result<IReadOnlyList<string>> pageZero = await withPageZero.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: null,
            tabId: ZeroTabId,
            cancellationToken: CancellationToken.None);

        pageZero.IsSuccess.Should().BeTrue(pageZero.Reason?.ToString());

        withPageZero.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                ZeroTabId,
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "page zero is a real page and must be asked about by its own identifier");
    }

    // =================================================================================================
    // The write side of inherited view permissions.
    //
    // The read side is covered above against the real service. The write side is a separate rule that the
    // legacy source applied when persisting a module's grants, and it is specified here because the
    // application contract deliberately publishes no grant-replacement member: the planned api gives
    // permissions a read-only catalogue endpoint only, and a contract member nothing calls is a contract
    // member nobody validates. The rule is nevertheless real and will bind whoever adds the grid screen.
    // =================================================================================================

    /// <summary>
    /// Replacing a module's grants clears the whole set before writing, rather than merging into it.
    /// </summary>
    /// <remarks>
    /// The legacy sequence deleted every grant recorded against the module and then re-added the survivors,
    /// so replacement is genuinely replacement: a row absent from the incoming set is gone afterwards.
    /// Merging instead would make a removal impossible to express, which for a refusal row is a security
    /// difference rather than a convenience one. The clear must also happen before any write, or a
    /// re-added row would be deleted by its own clear.
    /// </remarks>
    [Fact]
    public async Task Replacement_ClearsTheWholeSetBeforeWritingAnySurvivor()
    {
        Mock<IPermissionRepository> store = ReplacementStore();
        GrantReplacementSpecification specification = new(store.Object);

        await specification.ReplaceModuleGrantsAsync(
            ModuleId,
            inheritViewPermissions: false,
            [ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId)],
            KeyByPermissionId(),
            CancellationToken.None);

        store.Verify(
            permissions => permissions.DeleteModulePermissionsByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the existing set is cleared exactly once, so replacement cannot silently become a merge");

        specification.Written.Should().HaveCount(1, "the one incoming row survives and is written");
        specification.ClearedBeforeFirstWrite.Should().BeTrue(
            "the clear must precede every write, or a surviving row would be removed by its own clear");
    }

    /// <summary>
    /// An inheriting module does not persist an explicit view grant of its own.
    /// </summary>
    /// <param name="inheritViewPermissions">Whether the module takes its view permission from its pages.</param>
    /// <param name="viewRowIsPersisted">Whether the incoming view row should survive.</param>
    /// <remarks>
    /// The legacy write discarded an explicit view row when the module was configured to inherit, and it
    /// discarded only that key. The reason it matters is consistency with the read side: a module that
    /// inherits is answered for view from its pages, so a stored module-level view row could never be
    /// consulted and would sit in the table contradicting the answer the application gives. Every other key
    /// is unaffected in both states, because only view is inherited.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Replacement_PersistsAnExplicitViewRowOnlyWhenTheModuleDoesNotInherit(
        bool inheritViewPermissions,
        bool viewRowIsPersisted)
    {
        Mock<IPermissionRepository> store = ReplacementStore();
        GrantReplacementSpecification specification = new(store.Object);

        await specification.ReplaceModuleGrantsAsync(
            ModuleId,
            inheritViewPermissions,
            [
                ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId),
                ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId),
            ],
            KeyByPermissionId(),
            CancellationToken.None);

        specification.Written
            .Any(grant => grant.PermissionId == FirstPermissionId)
            .Should()
            .Be(
                viewRowIsPersisted,
                $"with inheritance {inheritViewPermissions} an explicit view row must {(viewRowIsPersisted ? "survive" : "be discarded")}, because an inheriting module is answered for view from its pages");

        specification.Written
            .Should()
            .Contain(grant => grant.PermissionId == SecondPermissionId,
                "only view is inherited, so an edit row survives in either state");
    }

    /// <summary>
    /// A refusal row is not written, in either inheritance state.
    /// </summary>
    /// <param name="inheritViewPermissions">Whether the module takes its view permission from its pages.</param>
    /// <remarks>
    /// <para>
    /// The legacy write gated the persist on the allow-or-deny flag being set, so a refusal row was dropped
    /// rather than stored. That is measured legacy behaviour and is preserved here.
    /// </para>
    /// <para>
    /// MIGRATION: note the asymmetry this creates with the read side, because it is genuine and not an
    /// error in either place. Reading applies deny precedence to whatever refusal rows exist, while writing
    /// through this legacy path never creates one. The rows the reader honours are therefore the ones the
    /// upgrade scripts and older versions of the product left behind - which is exactly why the reader must
    /// honour them, and why silently ignoring the flag on the read side was the defect this migration fixes.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replacement_DoesNotWriteARefusalRow(bool inheritViewPermissions)
    {
        Mock<IPermissionRepository> store = ReplacementStore();
        GrantReplacementSpecification specification = new(store.Object);

        await specification.ReplaceModuleGrantsAsync(
            ModuleId,
            inheritViewPermissions,
            [ModuleGrantRow(SecondPermissionId, allowAccess: false, roleId: MemberRoleId)],
            KeyByPermissionId(),
            CancellationToken.None);

        specification.Written.Should().BeEmpty(
            "the legacy write persisted a row only when it conferred access, so a refusal row is dropped rather than stored");
    }

    /// <summary>
    /// Every surviving row is stamped with the module it is being recorded against.
    /// </summary>
    /// <remarks>
    /// The legacy write assigned the module identifier onto each row before persisting it, which mattered
    /// because the incoming rows came from a screen that had not necessarily set it. Zero is used here
    /// deliberately: it is the module identifier under test throughout this file and a genuine value, so a
    /// stamp that silently skipped it would pass a test written against any other number.
    /// </remarks>
    [Fact]
    public async Task Replacement_StampsEverySurvivingRowWithTheModuleIdentifier()
    {
        Mock<IPermissionRepository> store = ReplacementStore();
        GrantReplacementSpecification specification = new(store.Object);

        ModulePermission incoming = ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId);
        incoming.ModuleId = ForeignRoleId;

        await specification.ReplaceModuleGrantsAsync(
            ModuleId,
            inheritViewPermissions: false,
            [incoming],
            KeyByPermissionId(),
            CancellationToken.None);

        specification.Written.Should().OnlyContain(
            grant => grant.ModuleId == ModuleId,
            "each row is stamped with the module it is recorded against before it is written");
    }

    // =================================================================================================
    // The grant entities.
    //
    // MIGRATION: three static controllers collapse into one application contract, and the decision moves
    //            out of all three. The legacy source spread this aggregate across a 71-line catalogue
    //            controller with 9 public members, a 389-line module-grant controller with 18 and a
    //            349-line page-grant controller with 15 - 42 measured members, of which 13 were already
    //            marked obsolete by their own author and are not ported, and many of the rest were
    //            near-duplicate overloads differing only in whether the caller had already materialised
    //            the rows. The persistence subset becomes one repository contract, the orchestration
    //            becomes one application service, precedence becomes one reducer, and enforcement becomes
    //            an authorisation handler at the api boundary. The five obsolete access-check and
    //            edit-permission members are not ported, and neither is the path-scoped catalogue lookup.
    //
    // MIGRATION: the legacy tables are singular and their text columns are narrow. The terminal schema
    //            names them in the singular, and the four catalogue text columns are each fifty
    //            characters, with the catalogue key obtained from an identity column. Rule T4 makes that
    //            schema immutable, so the mapping binds to those names and widths rather than redefining
    //            them - which is exactly why the key enumeration's member names had to match the stored
    //            spellings rather than the other way round. The mapping itself is asserted by the
    //            persistence suite in the integration project, which is the only place a mapping can be
    //            proved against a real database; what is provable here is the entity shape those mappings
    //            bind to, which is what the test below pins.
    // =================================================================================================

    /// <summary>
    /// The grant entities carry no serialisation attributes.
    /// </summary>
    /// <remarks>
    /// MIGRATION: every xml serialisation attribute is dropped from the domain. The legacy catalogue type
    /// decorated three of its five properties with element names and marked the other two as ignored,
    /// because the type doubled as the wire format for portal templates. In the target the domain entity is
    /// a plain persisted type with no opinion about transport: serialisation happens once, at the api
    /// boundary, over the request and response contracts. An attribute reappearing here would mean an entity
    /// had started crossing the wire directly, which baseline B6 forbids - so this is a boundary test rather
    /// than a tidiness test.
    /// </remarks>
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

    /// <summary>
    /// A grant references its catalogue entry rather than deriving from it.
    /// </summary>
    /// <remarks>
    /// The two grant entities are independent types carrying a foreign key, not specialisations of the
    /// catalogue entry - a grant is a reference to a permission, not a kind of one. Worth pinning because
    /// the legacy types were denormalised joins that repeated the catalogue's key and name inline, so a
    /// reader coming from that source reasonably expects an inheritance relationship, and code written on
    /// that expectation would compile against a base type that does not exist.
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

    /// <summary>
    /// The key vocabulary is a closed set of four persisted spellings.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the key column was free text against which the legacy source compared bare literals. The
    /// target types it as a closed enumeration whose member names are the stored spellings exactly, so the
    /// magic strings become named members without changing one stored value - which is what lets the schema
    /// stay immutable while the code stops guessing. The order is the declaration order, and it is pinned
    /// because these members are persisted data: renaming one, re-casing one, or inserting one is a schema
    /// change disguised as a refactor.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreAClosedSetOfFourPersistedSpellings()
    {
        Enum.GetNames<PermissionKey>().Should().Equal(
            ["VIEW", "EDIT", "READ", "WRITE"],
            "these four spellings are what the key column stores, so the enumeration must mirror them exactly");
    }

    /// <summary>
    /// A key is a discrete member, never a bit field.
    /// </summary>
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

    /// <summary>
    /// Every declared key round-trips as its persisted spelling, and no other casing is a member.
    /// </summary>
    /// <param name="permissionKey">The key under test.</param>
    /// <param name="persistedSpelling">The spelling stored in the key column.</param>
    /// <remarks>
    /// All four members are covered here. Only two of them have a literal call site in the legacy code that
    /// is still live - and the census is worth recording precisely, because the raw counts mislead. Of the
    /// six edit literals, three sit inside a region the legacy author explicitly marked as obsolete and
    /// retained only for binary compatibility, and one of those three calls an overload that was itself
    /// already marked obsolete. Only three edit sites and four view sites are live. Separately, one view
    /// literal elsewhere in the legacy source is not a permission key at all: it is a control-panel display
    /// mode compared against a site setting, and treating it as a key would invent a permission that never
    /// existed. The read and write members carry no in-scope legacy literal, and they are still pinned here
    /// because they are stored values that the schema and installed modules use.
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

    /// <summary>
    /// Builds a catalogue row naming one permission key.
    /// </summary>
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
    /// The identifier the grant reads treat as "every permission" rather than as "no permission".
    /// </summary>
    /// <remarks>
    /// Measured legacy behaviour that the terminal procedures still carry: in this argument position the
    /// value is a wildcard, which is a third distinct meaning for the same number alongside the All Users
    /// role and the legacy absence sentinel. Named here so the substituted store reproduces it instead of
    /// quietly matching nothing.
    /// </remarks>
    private const int WildcardPermissionId = -1;

    /// <summary>
    /// Builds a catalogue row.
    /// </summary>
    /// <param name="permissionId">The row's own identifier, which grants reference.</param>
    /// <param name="permissionKey">The key the row defines.</param>
    /// <param name="permissionCode">The scope code the row belongs to.</param>
    /// <returns>The catalogue row.</returns>
    private static Permission CatalogueEntry(int permissionId, PermissionKey permissionKey, string permissionCode) => new()
    {
        PermissionId = permissionId,
        PermissionCode = permissionCode,
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey,
        PermissionName = permissionKey.ToString(),
    };

    /// <summary>
    /// Builds one grant recorded against the module under test.
    /// </summary>
    /// <param name="permissionId">The catalogue row the grant references.</param>
    /// <param name="allowAccess">Whether the row confers the key or refuses it.</param>
    /// <param name="roleId">The role the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="userId">The account the grant names, or <see langword="null"/> when it names none.</param>
    /// <returns>The grant row.</returns>
    private static ModulePermission ModuleGrantRow(
        int permissionId,
        bool allowAccess,
        int? roleId = null,
        int? userId = null)
    {
        return new ModulePermission
        {
            ModulePermissionId = 500 + permissionId,
            ModuleId = ModuleId,
            PermissionId = permissionId,
            RoleId = roleId,
            UserId = userId,
            AllowAccess = allowAccess,
        };
    }

    /// <summary>
    /// Builds one grant recorded against a page.
    /// </summary>
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
            TabPermissionId = 700 + permissionId,
            TabId = tabId,
            PermissionId = permissionId,
            RoleId = roleId,
            UserId = userId,
            AllowAccess = allowAccess,
        };
    }

    /// <summary>
    /// Builds an identified caller holding the supplied roles.
    /// </summary>
    /// <param name="roleIds">The roles the caller holds.</param>
    /// <returns>The caller.</returns>
    private static CallerIdentity Member(params int[] roleIds)
        => new(UserId, roleIds.ToHashSet(), [MemberRoleName], true, false);

    /// <summary>
    /// Builds an unidentified caller, which is a supported case rather than a rejected one.
    /// </summary>
    /// <returns>The caller.</returns>
    private static CallerIdentity Anonymous()
        => new(null, new HashSet<int>(), [], false, false);

    /// <summary>
    /// Builds an installation-wide account.
    /// </summary>
    /// <returns>The caller.</returns>
    private static CallerIdentity HostAccount()
        => new(HostUserId, new HashSet<int>(), [], true, true);

    /// <summary>
    /// Builds a substituted grant store holding the supplied catalogue and grant rows.
    /// </summary>
    /// <param name="catalogue">The catalogue rows the store returns for any code-and-key read.</param>
    /// <param name="moduleGrants">The module grants the store holds.</param>
    /// <param name="pageGrants">The page grants the store holds.</param>
    /// <returns>The substituted store.</returns>
    /// <remarks>
    /// <para>
    /// Substituted strictly, which buys a second guarantee for free: the specification under test cannot
    /// touch a repository member that was not set up here without failing, so these tests also pin how
    /// narrow the read surface of a decision is.
    /// </para>
    /// <para>
    /// The code-and-key read deliberately returns the catalogue unfiltered. The real read filters, but a
    /// substitute that also filtered would hide whether the specification applies the scope-code and key
    /// checks itself, and those checks are exactly what the admission tests assert.
    /// </para>
    /// </remarks>
    private static Mock<IPermissionRepository> StoreWith(
        IReadOnlyList<Permission> catalogue,
        IReadOnlyList<ModulePermission>? moduleGrants = null,
        IReadOnlyList<TabPermission>? pageGrants = null)
    {
        IReadOnlyList<ModulePermission> modules = moduleGrants ?? [];
        IReadOnlyList<TabPermission> pages = pageGrants ?? [];

        Mock<IPermissionRepository> store = new(MockBehavior.Strict);

        store
            .Setup(permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalogue);

        store
            .Setup(permissions => permissions.GetModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int moduleId, int permissionId, CancellationToken _) => modules
                .Where(grant => grant.ModuleId == moduleId
                    && (permissionId == WildcardPermissionId || grant.PermissionId == permissionId))
                .ToList());

        store
            .Setup(permissions => permissions.GetTabPermissionsByTabIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int tabId, int permissionId, CancellationToken _) => pages
                .Where(grant => grant.TabId == tabId
                    && (permissionId == WildcardPermissionId || grant.PermissionId == permissionId))
                .ToList());

        return store;
    }

    /// <summary>
    /// Maps the two catalogue identifiers used by the replacement tests to the keys they define.
    /// </summary>
    /// <returns>The key each catalogue identifier defines.</returns>
    /// <remarks>
    /// MIGRATION: a grant row no longer carries the key it grants. The legacy grant type was a denormalised
    /// join that repeated the catalogue's key and name alongside the grant, so the write rule could read the
    /// key straight off the row. The target row carries only a foreign key to the catalogue, which is the
    /// normalised shape the terminal schema actually has, so a rule that needs the key resolves it from the
    /// catalogue instead. Passing that resolution in explicitly keeps this specification honest about the
    /// extra read the real implementation owes.
    /// </remarks>
    private static IReadOnlyDictionary<int, PermissionKey> KeyByPermissionId()
        => new Dictionary<int, PermissionKey>
        {
            [FirstPermissionId] = PermissionKey.VIEW,
            [SecondPermissionId] = PermissionKey.EDIT,
        };

    /// <summary>
    /// Builds a substituted store that accepts a clear followed by any number of writes.
    /// </summary>
    /// <returns>The substituted store.</returns>
    private static Mock<IPermissionRepository> ReplacementStore()
    {
        Mock<IPermissionRepository> store = new(MockBehavior.Strict);

        store
            .Setup(permissions => permissions.DeleteModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        store
            .Setup(permissions => permissions.AddModulePermissionAsync(
                It.IsAny<ModulePermission>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return store;
    }

    /// <summary>
    /// The rule the legacy source applied when persisting a module's grants, written as runnable code.
    /// </summary>
    /// <remarks>
    /// Private and nested for the same reason as the precedence specification: it records a rule so that it
    /// is executable rather than only described, and it must never be mistaken for production code.
    /// </remarks>
    private sealed class GrantReplacementSpecification
    {
        private readonly IPermissionRepository _permissions;

        private readonly List<ModulePermission> _written = [];

        private bool _cleared;

        private bool _clearedBeforeFirstWrite = true;

        /// <summary>
        /// Initialises the specification over a grant store.
        /// </summary>
        /// <param name="permissions">The grant store.</param>
        public GrantReplacementSpecification(IPermissionRepository permissions)
            => _permissions = permissions;

        /// <summary>The rows that were actually written, in the order they were written.</summary>
        public IReadOnlyList<ModulePermission> Written => _written;

        /// <summary>Whether the existing set was cleared before the first row was written.</summary>
        public bool ClearedBeforeFirstWrite => _clearedBeforeFirstWrite;

        /// <summary>
        /// Replaces every grant recorded against one module with the supplied set.
        /// </summary>
        /// <param name="moduleId">The module whose grants are being replaced.</param>
        /// <param name="inheritViewPermissions">Whether the module takes its view permission from its pages.</param>
        /// <param name="desired">The grants the caller wants recorded.</param>
        /// <param name="keyByPermissionId">The key each referenced catalogue row defines.</param>
        /// <param name="cancellationToken">Token that cancels the writes.</param>
        /// <returns>A task that completes when the replacement has been staged.</returns>
        public async Task ReplaceModuleGrantsAsync(
            int moduleId,
            bool inheritViewPermissions,
            IReadOnlyList<ModulePermission> desired,
            IReadOnlyDictionary<int, PermissionKey> keyByPermissionId,
            CancellationToken cancellationToken)
        {
            await _permissions.DeleteModulePermissionsByModuleIdAsync(moduleId, cancellationToken);
            _cleared = true;

            foreach (ModulePermission grant in desired)
            {
                grant.ModuleId = moduleId;

                bool isInheritedViewRow = inheritViewPermissions
                    && keyByPermissionId.TryGetValue(grant.PermissionId, out PermissionKey key)
                    && key == PermissionKey.VIEW;

                if (isInheritedViewRow)
                {
                    // An inheriting module is answered for view from its pages, so storing a module-level
                    // view row would leave a row in the table that no read can ever consult.
                    continue;
                }

                if (!grant.AllowAccess)
                {
                    // Measured legacy behaviour: the persist was gated on the row conferring access.
                    continue;
                }

                if (!_cleared)
                {
                    _clearedBeforeFirstWrite = false;
                }

                await _permissions.AddModulePermissionAsync(grant, cancellationToken);
                _written.Add(grant);
            }
        }
    }

    /// <summary>
    /// The caller a grant row is matched against.
    /// </summary>
    /// <param name="UserId">The caller's account, or <see langword="null"/> when unidentified.</param>
    /// <param name="RoleIds">The role identifiers the caller holds.</param>
    /// <param name="RoleNames">The role names the caller holds.</param>
    /// <param name="IsAuthenticated">Whether the caller has been identified.</param>
    /// <param name="IsSuperUser">Whether the caller is an installation-wide account.</param>
    /// <remarks>
    /// MIGRATION: the ambient page and settings read is gone. The legacy no-argument access check reached
    /// into the current request's portal settings object and used whichever page that object happened to be
    /// pointing at, and the membership helper beside it read the current request to discover whether the
    /// caller was authenticated. Both facts are now arguments: an immutable scoped context resolved once
    /// per request in api middleware supplies them, so a decision is reproducible from its inputs alone
    /// and this specification can be exercised without any request at all.
    /// </remarks>
    private sealed record CallerIdentity(
        int? UserId,
        IReadOnlySet<int> RoleIds,
        IReadOnlyList<string> RoleNames,
        bool IsAuthenticated,
        bool IsSuperUser);

    /// <summary>
    /// The allow-and-deny precedence rule the concrete reducer must satisfy, written as runnable code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a specification, not a second implementation: nothing in production calls it, and it exists
    /// so the rule the concrete reducer implements is stated somewhere executable rather than only in
    /// prose. Keeping it private and nested makes that impossible to misuse - it cannot be referenced from
    /// another file, let alone registered in a container.
    /// </para>
    /// <para>
    /// MIGRATION: the reflection-driven row hydrator and the reflection-created static provider accessor
    /// are both gone. Every read below is a plain call on an injected repository contract that hands back
    /// materialised entities, where the legacy path reached a static accessor built by reflection and then
    /// hydrated each row by reflecting over its properties. The untyped sequences those reads returned are
    /// now read-only generic sequences, and the hand-written pre-generics wrapper classes that fronted
    /// them produce no target type at all.
    /// </para>
    /// </remarks>
    private sealed class GrantPrecedenceSpecification
    {
        private readonly IPermissionRepository _permissions;

        /// <summary>
        /// Initialises the specification over a grant store.
        /// </summary>
        /// <param name="permissions">The grant store.</param>
        public GrantPrecedenceSpecification(IPermissionRepository permissions)
            => _permissions = permissions;

        /// <summary>
        /// Decides whether a caller holds one key on one module.
        /// </summary>
        /// <param name="moduleId">The module. Zero is a genuine module identifier.</param>
        /// <param name="permissionCode">The scope code the key is defined under.</param>
        /// <param name="permissionKey">The key being tested.</param>
        /// <param name="caller">The caller.</param>
        /// <param name="cancellationToken">Token that cancels the reads.</param>
        /// <returns>A successful outcome carrying the decision; a refusal is a successful false.</returns>
        public async Task<Result<bool>> HasModuleGrantAsync(
            int moduleId,
            string permissionCode,
            PermissionKey permissionKey,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<int> permissionIds =
                await AdmittedPermissionIdsAsync(permissionCode, permissionKey, cancellationToken);

            List<bool> matching = [];

            foreach (int permissionId in permissionIds)
            {
                IReadOnlyList<ModulePermission> grants = await _permissions
                    .GetModulePermissionsByModuleIdAsync(moduleId, permissionId, cancellationToken);

                matching.AddRange(grants
                    .Where(grant => grant.ModuleId == moduleId && grant.PermissionId == permissionId)
                    .Where(grant => Matches(grant.RoleId, grant.UserId, caller))
                    .Select(grant => grant.AllowAccess));
            }

            return Reduce(matching);
        }

        /// <summary>
        /// Decides whether a caller holds one key on one page.
        /// </summary>
        /// <param name="tabId">The page. Zero is a genuine page identifier.</param>
        /// <param name="permissionCode">The scope code the key is defined under.</param>
        /// <param name="permissionKey">The key being tested.</param>
        /// <param name="caller">The caller.</param>
        /// <param name="cancellationToken">Token that cancels the reads.</param>
        /// <returns>A successful outcome carrying the decision.</returns>
        public async Task<Result<bool>> HasPageGrantAsync(
            int tabId,
            string permissionCode,
            PermissionKey permissionKey,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<int> permissionIds =
                await AdmittedPermissionIdsAsync(permissionCode, permissionKey, cancellationToken);

            List<bool> matching = [];

            foreach (int permissionId in permissionIds)
            {
                IReadOnlyList<TabPermission> grants = await _permissions
                    .GetTabPermissionsByTabIdAsync(tabId, permissionId, cancellationToken);

                matching.AddRange(grants
                    .Where(grant => grant.TabId == tabId && grant.PermissionId == permissionId)
                    .Where(grant => Matches(grant.RoleId, grant.UserId, caller))
                    .Select(grant => grant.AllowAccess));
            }

            return Reduce(matching);
        }

        /// <summary>
        /// Decides whether a caller belongs to any of a set of named roles.
        /// </summary>
        /// <param name="grantedRoleNames">The role names recorded against the grant.</param>
        /// <param name="caller">The caller.</param>
        /// <param name="allUsersRoleName">The configured name of the all-users pseudo-role.</param>
        /// <param name="unauthenticatedRoleName">The configured name of the unauthenticated pseudo-role.</param>
        /// <returns>Whether any name admits the caller.</returns>
        /// <remarks>
        /// <para>
        /// The name-keyed counterpart of <see cref="Matches"/>, ported from the legacy membership loop. It
        /// is still needed because the schema stores delimited role <em>names</em> in the authorised-roles
        /// columns, so names remain a real currency alongside the identifiers on the grant tables.
        /// </para>
        /// <para>
        /// MIGRATION: the delimited permission string is gone from every contract. Three legacy members
        /// flattened a grant set into one value - a leading delimiter, then every granted role name, then
        /// every granted account in a bracketed pseudo-role form - purely to push one string into one
        /// server-rendered control property. It was never a data contract, and nothing here parses one:
        /// this predicate takes an already-separated sequence, so the empty entry a trailing delimiter used
        /// to produce is handled as data rather than as a parsing quirk.
        /// </para>
        /// </remarks>
        public static bool IsInRoles(
            IReadOnlyList<string> grantedRoleNames,
            CallerIdentity caller,
            string allUsersRoleName,
            string unauthenticatedRoleName)
        {
            foreach (string granted in grantedRoleNames)
            {
                // The installation-wide flag was tested first inside the legacy loop, so such an account
                // matches any entry it reaches - but an empty sequence never reaches one.
                if (caller.IsSuperUser)
                {
                    return true;
                }

                if (granted.Length == 0)
                {
                    continue;
                }

                if (!caller.IsAuthenticated
                    && string.Equals(granted, unauthenticatedRoleName, StringComparison.Ordinal))
                {
                    return true;
                }

                if (string.Equals(granted, allUsersRoleName, StringComparison.Ordinal))
                {
                    return true;
                }

                if (caller.RoleNames.Contains(granted, StringComparer.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reduces every matching row to one decision.
        /// </summary>
        /// <param name="matching">The allow-or-deny flag of each row that matched the caller.</param>
        /// <returns>A successful outcome carrying the decision.</returns>
        /// <remarks>
        /// Deliberately a reduction over the whole set rather than a walk that returns early. That is what
        /// makes the answer a property of the data instead of a property of the row order, and it is the
        /// single structural difference from the legacy first-match-wins predicate. An empty set reduces to
        /// a refusal, which is why no caller - including an installation-wide account - can hold a key that
        /// was never conferred.
        /// </remarks>
        private static Result<bool> Reduce(IReadOnlyList<bool> matching)
        {
            bool refused = matching.Any(allowAccess => !allowAccess);
            bool allowed = matching.Any(allowAccess => allowAccess);

            return Result<bool>.Success(allowed && !refused);
        }

        /// <summary>
        /// Decides whether one grant row names this caller.
        /// </summary>
        /// <param name="roleId">The role the row names, if any.</param>
        /// <param name="userId">The account the row names, if any.</param>
        /// <param name="caller">The caller.</param>
        /// <returns>Whether the row applies to the caller.</returns>
        /// <remarks>
        /// Ported from the legacy membership loop, which tested the installation-wide flag first and then
        /// admitted the unauthenticated pseudo-role only for an unauthenticated request and the all-users
        /// pseudo-role unconditionally. Note what is deliberately absent: no arithmetic on the identifier,
        /// no sign test, and no absolute value. Every branch compares against a named principal or asks the
        /// caller's own role set, because in this schema zero is an ordinary role and the negatives are
        /// shipped principals rather than absence markers.
        /// </remarks>
        private static bool Matches(int? roleId, int? userId, CallerIdentity caller)
        {
            if (userId.HasValue)
            {
                return caller.IsSuperUser
                    || (caller.UserId.HasValue && caller.UserId.Value == userId.Value);
            }

            if (!roleId.HasValue)
            {
                return false;
            }

            return roleId.Value switch
            {
                AllUsersRoleId => true,
                UnauthenticatedRoleId => !caller.IsAuthenticated,
                SuperUserRoleId => caller.IsSuperUser,
                _ => caller.IsSuperUser || caller.RoleIds.Contains(roleId.Value),
            };
        }

        /// <summary>
        /// Reads the catalogue and keeps only the entries that genuinely answer the question asked.
        /// </summary>
        /// <param name="permissionCode">The scope code asked about.</param>
        /// <param name="permissionKey">The key asked about.</param>
        /// <param name="cancellationToken">Token that cancels the read.</param>
        /// <returns>The distinct catalogue identifiers whose grants may be consulted.</returns>
        /// <remarks>
        /// The scope code is compared with ordinal string equality, matching the exact comparison the
        /// repository read documents, and the key is compared as an enumeration member so there is nothing
        /// to case-fold or parse. The key set being closed is what removes the whole class of
        /// mixed-case and whitespace hazards that a free-text key column would carry.
        /// </remarks>
        private async Task<IReadOnlyList<int>> AdmittedPermissionIdsAsync(
            string permissionCode,
            PermissionKey permissionKey,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<Permission> catalogue =
                await _permissions.GetByCodeAndKeyAsync(permissionCode, permissionKey, cancellationToken);

            return catalogue
                .Where(entry => string.Equals(entry.PermissionCode, permissionCode, StringComparison.Ordinal))
                .Where(entry => entry.PermissionKey == permissionKey)
                .Select(entry => entry.PermissionId)
                .Distinct()
                .ToList();
        }
    }

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
            Evaluator = new Mock<IPermissionEvaluator>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);

            Service = new PermissionService(
                Permissions.Object,
                Evaluator.Object,
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

        public Mock<IPermissionEvaluator> Evaluator { get; }

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

            // MIGRATION: the catalogue is no longer one wildcard-tolerant read. The repository
            //            contract mirrors the legacy provider, which offered a definition-scoped read
            //            and a code-and-key read, so the service composes the three question shapes
            //            from those two. Both are stubbed from the same Catalogue fixture.
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

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.PortalKeys);

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.ModuleKeys);

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.TabKeys);

            harness.Evaluator
                .Setup(evaluator => evaluator.HasModulePermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => harness.ModuleGrant);

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
                    CancellationToken token) => harness.AnswerPage(tabId, key));

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
