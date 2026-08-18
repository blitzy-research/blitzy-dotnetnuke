using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;
using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Covers the account workflow: the split between the tracked account row and the external credential
/// store, the membership transitions an administrator may apply, the profile reconciliation, and the
/// settings that are stored against a module instance rather than against the tenant.
/// </summary>
/// <remarks>
/// <para>
/// An account is two things in two places. The row in the tenant's own table is tracked and committed
/// through the unit of work; the credential is held in the external membership store and is reached by
/// explicit statements.
/// </para>
/// <para>
/// Membership settings are not tenant columns. They are module settings held against the tenant's single
/// account-management module instance, which is why reading them can legitimately answer nothing and why
/// writing them can legitimately fail with nowhere to write.
/// </para>
/// </remarks>
public class UserServiceTests
{
    /// <summary>
    /// The first key <c>dbo.Portals.PortalID</c> issues, which is a REAL TENANT and not the host scope.
    /// </summary>
    /// <remarks>
    /// <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c> (<c>01.00.00.SqlDataProvider:L77</c>), so -1
    /// is the first tenant an installation has. The host scope is a SQL <c>NULL</c> portal, which
    /// <c>03.03.03.SqlDataProvider:L74-L83</c> established.
    /// </remarks>
    private const int FirstTenantPortalId = -1;

    private const int PortalId = 0;

    private const int OtherPortalId = 3;

    private const int UserId = 1;

    private const int OtherUserId = 2;

    private const int AdministratorId = 9;

    /// <summary>
    /// The account that ACTS in a test, kept distinct from every account a test acts upon so that a caller
    /// and a subject can never be confused for one another.
    /// </summary>
    private const int CallerId = 77;

    /// <summary>
    /// The role the tenant designates as conferring portal administration, which is what
    /// <c>Portals.AdministratorRoleId</c> keys.
    /// </summary>
    private const int AdministratorRoleId = 78;

    private const int ModuleId = 4;

    private const int AccountsModuleDefinitionId = 11;

    private const int FirstNamePropertyId = 20;

    private const int StreetPropertyId = 21;

    private const int CityPropertyId = 22;

    private const int TelephonePropertyId = 23;

    private const string Username = "grace";

    private const string Email = "grace@example.com";

    private const string CurrentPassword = "Current-Secret-1";

    private const string NewPassword = "Replacement-Secret-2";

    private const string ListFilterInvalidCode = "user.list.filter-invalid";

    private const string ListUnknownProfilePropertyCode = "user.list.unknown-profile-property";

    private const string ListSortUnsupportedCode = "user.list.sort-unsupported";

    private const string NotFoundCode = "user.not-found";

    private const string CreateUsernameAlreadyExistsCode = "user.create.username-already-exists";

    private const string CreateUserAlreadyRegisteredCode = "user.create.user-already-registered";

    private const string CreateDuplicateUsernameCode = "user.create.duplicate-username";

    private const string CreateDuplicateEmailCode = "user.create.duplicate-email";

    private const string CreateInvalidUsernameCode = "user.create.invalid-username";

    private const string CreateInvalidEmailCode = "user.create.invalid-email";

    private const string CreateInvalidPasswordCode = "user.create.invalid-password";

    private const string CreatePasswordMismatchCode = "user.create.password-mismatch";

    private const string CreatePortalAssignmentFailedCode = "user.create.portal-assignment-failed";

    private const string CreateProviderErrorCode = "user.create.provider-error";

    private const string PersistenceConflictCode = "persistence.conflict";

    private const string DeleteAdministratorProtectedCode = "user.delete.administrator-protected";

    private const string DeleteSuperUserProtectedCode = "user.delete.superuser-protected";

    private const string PasswordMissingCode = "user.password.missing";

    private const string PasswordInvalidCode = "user.password.invalid";

    private const string PasswordMismatchCode = "user.password.mismatch";

    private const string PasswordNotDifferentCode = "user.password.not-different";

    private const string PasswordResetFailedCode = "user.password.reset-failed";

    /// <summary>
    /// Reported when the credential changed between this request reading it and writing it, so the store
    /// refused the write on purpose.
    /// </summary>
    private const string PasswordSupersededCode = "user.password.superseded";

    /// <summary>
    /// Reported when an operation that must end an account's sessions could not, so the operation itself
    /// was abandoned. Its reason token ends in <c>store_unavailable</c>, so the Api edge answers
    /// <c>503</c>.
    /// </summary>
    private const string SessionRevocationFailedCode = "user.session.revocation_store_unavailable";

    /// <summary>
    /// Reported when an account's credential could not be removed during deletion, so the deletion was
    /// abandoned rather than committed with the credential left behind.
    /// </summary>
    private const string CredentialRemovalFailedCode = "user.credential.removal_store_unavailable";

    private const string PasswordCurrentIncorrectCode = "user.password.current-incorrect";

    private const string PasswordResetNotEnabledCode = "user.password.reset-not-enabled";

    private const string PasswordUnsupportedOperationCode = "user.password.unsupported-operation";

    /// <summary>The refusal a credential change carries when the caller is not the account that owns it.</summary>
    private const string PasswordChangeSelfOnlyForbiddenCode = "user.password.change-self-only-forbidden";

    /// <summary>The refusal an administrative reset carries when the caller does not administer the tenant.</summary>
    private const string PasswordResetForbiddenCode = "user.password.reset-forbidden";

    private const string MembershipSelfForbiddenCode = "user.membership.self-forbidden";

    private const string ApprovalUnchangedCode = "user.approval.unchanged";

    private const string PasswordChangeAlreadyRequiredCode = "user.password.change-already-required";

    private const string MembershipSettingsSourceMissingCode = "user.membership-settings.storage-conflict";

    private const string MembershipSettingsRedirectNotInPortalCode =
        "user.membership-settings.redirect_not_in_portal";

    private const string DisplayNameTooLongCode = "user.display-name.too-long";

    private const string ProfileUnknownPropertyCode = "user.profile.unknown-property";

    private const string ProfileRequiredPropertyMissingCode = "user.profile.required-property-missing";

    private const string ProfileValueTooLongCode = "user.profile.value-too-long";

    private const string ProfilePropertyValidationFailedCode = "user.profile.property-validation-failed";

    private const string ProfileDefinitionDuplicateNameCode = "profile-definition.duplicate-name";

    private const string ProfileDefinitionNotFoundCode = "profile-definition.not-found";

    /// <summary>The refusal issued for one of the four declarations the platform reserves.</summary>
    private const string ProfileDefinitionProtectedCode = "profile-definition.protected";

    /// <summary>The refusal issued when removal would destroy answers nobody has consented to losing.</summary>
    private const string ProfileDefinitionValueCascadeCode =
        "profile-definition.value-deletion-unacknowledged";

    /// <summary>The tenant has switched self-service subscription off - <c>Profile_ManageServices</c>.</summary>
    private const string ServiceDisabledCode = "user.service.disabled-forbidden";

    /// <summary>The addressed role is not one the tenant publishes for self-service.</summary>
    private const string ServiceNotOfferedCode = "user.service.not-offered-forbidden";

    /// <summary>Completing the operation would require taking payment, which is out of scope.</summary>
    private const string ServicePaymentRequiredCode = "user.service.payment-required-forbidden";

    /// <summary>The addressed role offers this account no free trial, for any of four reasons.</summary>
    private const string ServiceTrialNotOfferedCode = "user.service.trial-not-offered-forbidden";

    /// <summary>The invitation-code submission carries no code.</summary>
    private const string ServiceCodeRequiredCode = "user.service.code-required";

    /// <summary>No role in the tenant bears the submitted invitation code.</summary>
    private const string ServiceCodeNotMatchedCode = "user.service.code-not-matched";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The account contract exposes exactly twenty-seven named asynchronous operations, every one of them
    /// scoped to a tenant.
    /// </summary>
    /// <remarks>
    /// Two of them are recent and both are named deliberately. <c>RequiresProfileCompletionAsync</c> lets
    /// the sign-in path evaluate the legacy profile gate without the completeness rule acquiring a second
    /// implementation. <c>ResetPasswordAsync</c> exists because the self-service credential change and the
    /// administrative reset were once a single member that chose between them by reading a discriminator
    /// out of the caller's own request body, which let the caller decide whether the current credential had
    /// to be proved; they are two members now precisely so the two authorisation policies can differ, and
    /// this inventory asserts that the split is still in place.
    /// </remarks>
    [Fact]
    public void UserContract_OffersExactlyTwentyEightNamedTenantScopedOperations()
    {
        MethodInfo[] members = typeof(IUserService).GetMethods();

        members.Select(member => member.Name).Should().BeEquivalentTo(
        [
            "ListUsersAsync",
            "ListAccountChoicesAsync",
            "GetUserAsync",
            "CreateUserAsync",
            "UpdateUserAsync",
            "DeleteUserAsync",
            "ChangePasswordAsync",
            "ResetPasswordAsync",
            "UnlockUserAsync",
            "SetUserApprovalAsync",
            "RequirePasswordChangeAsync",
            "GetMembershipSettingsAsync",
            "UpdateMembershipSettingsAsync",
            "IsEmailValidAsync",
            "RequiresProfileCompletionAsync",
            "GetProfileAsync",
            "ExportPersonalDataAsync",
            "UpdateProfileAsync",
            "ListProfilePropertyDefinitionsAsync",
            "GetProfilePropertyDefinitionAsync",
            "CreateProfilePropertyDefinitionAsync",
            "UpdateProfilePropertyDefinitionAsync",
            "ReorderProfilePropertyDefinitionsAsync",
            "DeleteProfilePropertyDefinitionAsync",
            "ListMemberServicesAsync",
            "SubscribeToServiceAsync",
            "CancelServiceAsync",
            "StartServiceTrialAsync",
            "RedeemServiceCodeAsync",
        ]);

        foreach (MethodInfo member in members)
        {
            member.Name.Should().EndWith("Async");
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue();
            member.GetParameters()[0].Name.Should().Be("portalId");
            member.GetParameters()[^1].ParameterType.Should().Be(typeof(CancellationToken));
        }
    }

    /// <summary>The service refuses to be constructed without every collaborator it depends on.</summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var users = new Mock<IUserRepository>().Object;
        var profiles = new Mock<IUserProfileRepository>().Object;
        var roles = new Mock<IRoleRepository>().Object;
        var permissions = new Mock<IPermissionService>().Object;
        var roleService = new Mock<IRoleService>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var modules = new Mock<IModuleRepository>().Object;
        var definitions = new Mock<IModuleDefinitionRepository>().Object;
        var tabs = new Mock<ITabRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var hasher = new Mock<IPasswordHasher>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var audit = new Mock<IAuditSink>().Object;
        var tokens = new Mock<ITokenService>().Object;
        var storeFailures = new Mock<IStoreFailureClassifier>().Object;
        var diagnostics = new Mock<ISecurityDiagnostics>().Object;
        var policy = new PasswordPolicyOptions();
        var caching = new CachingOptions();
        var portalContext = new Mock<IPortalContextHolder>().Object;

        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new UserService(null!, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("profiles", () =>
        {
            _ = new UserService(users, null!, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new UserService(users, profiles, null!, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new UserService(users, profiles, roles, null!, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("roleService", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, null!, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, null!, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("modules", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, null!, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("definitions", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, null!, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, null!, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, null!, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("passwordHasher", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, null!, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, null!, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, null!, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, null!, audit, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, null!, tokens, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("tokens", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, null!, storeFailures, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("storeFailures", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, null!, diagnostics, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("diagnostics", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, null!, policy, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("passwordPolicy", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, null!, caching, portalContext);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, null!, portalContext);
        });
        Assert.Throws<ArgumentNullException>("portalContext", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, roleService, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, storeFailures, diagnostics, policy, caching, null!);
        });
    }

    /// <summary>Listing accounts requires a paging request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_RequiresAPagingRequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListUsersAsync(PortalId, null!, cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Malformed paging is refused as a request fault rather than as an outcome, because coordinates
    /// outside the permitted range are a caller error rather than a state the tenant can be in.
    /// </summary>
    /// <param name="pageIndex">The page index to submit.</param>
    /// <param name="pageSize">The page size to submit.</param>
    /// <param name="queryLength">The length of the search text to submit.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(-1, 10, 0, "The page index must not be negative.")]
    [InlineData(0, -1, 0, "The page size must not be negative.")]
    [InlineData(0, 101, 0, "The page size must not exceed 100.")]
    [InlineData(0, 10, 257, "The search text must not exceed 256 characters.")]
    public async Task ListUsers_RefusesMalformedPaging(
        int pageIndex,
        int pageSize,
        int queryLength,
        string expectedMessage)
    {
        Harness harness = Harness.Ready();
        var page = new PagedRequest
        {
            PageIndex = pageIndex,
            PageSize = pageSize,
            Query = queryLength == 0 ? null : new string('q', queryLength),
        };

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.ListUsersAsync(PortalId, page, cancellationToken: CancellationToken.None));

        failure.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// An ordering this listing cannot honour is refused, whichever collection the name belongs to.
    /// </summary>
    /// <param name="field">A field name a caller might reach for.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The shared request validator applies the union of every collection's sortable set, so each of these
    /// names passes it. Each is nonetheless unhonourable HERE, and for a measured reason.
    /// </remarks>
    [Theory]
    [InlineData("CreatedDate")]
    [InlineData("LastLoginDate")]
    [InlineData("IsApproved")]
    [InlineData("IsLockedOut")]
    [InlineData("Address")]
    [InlineData("Telephone")]
    [InlineData("IsOnline")]
    [InlineData("PortalName")]
    [InlineData("RoleName")]
    public async Task ListUsers_RefusesEveryNamedOrdering(string field)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { SortBy = field },
            cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ListSortUnsupportedCode);
        outcome.Reason!.Message.Should().Be($"Accounts cannot be ordered by '{field}'.");
        harness.Users.Verify(
            u => u.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Every ordering the store can honour is accepted and passed through to it verbatim.</summary>
    /// <param name="field">A mapped column of the entity the listing pages over.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion of the refusal above, and the reason the pair exists: the declared set and the
    /// ordering the repository applies are two independently editable places, so widening one without the
    /// other would either advertise an ordering that is silently discarded or refuse one that works.
    /// </remarks>
    [Theory]
    [InlineData("UserId")]
    [InlineData("Username")]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("DisplayName")]
    [InlineData("Email")]
    [InlineData("IsSuperUser")]
    public async Task ListUsers_AcceptsEveryOrderingTheStoreCanHonour(string field)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { SortBy = field },
            cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Users.Verify(
            u => u.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                field,
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A request that names no ordering is answered normally, so the refusal above is scoped to an explicit
    /// preference and does not make the listing unusable.
    /// </summary>
    /// <param name="sortBy">The absent-or-blank sort field to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListUsers_AcceptsARequestThatNamesNoOrdering(string? sortBy)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { SortBy = sortBy },
            cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Users.Verify(
            u => u.ListAsync(
                PortalId,
                0,
                10,
                null,
                null,
                null,
                null,
                null,
                null,
                true,
                false,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A filter that was supplied but holds only white space is refused, because an omitted filter and a
    /// blank one mean different things and only one of them is a search.
    /// </summary>
    /// <param name="which">Which filter to submit blank.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("userNameFilter")]
    [InlineData("emailFilter")]
    [InlineData("profilePropertyName")]
    public async Task ListUsers_RefusesABlankFilter(string which)
    {
        Harness harness = Harness.Ready();

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.ListUsersAsync(
                PortalId,
                new PagedRequest(),
                userNameFilter: which == "userNameFilter" ? "  " : null,
                emailFilter: which == "emailFilter" ? "  " : null,
                profilePropertyName: which == "profilePropertyName" ? "  " : null,
                profilePropertyValue: which == "profilePropertyName" ? "value" : null,
                cancellationToken: CancellationToken.None));

        failure.Message.Should()
            .Be($"The {which} filter must not be blank; omit it to search without it.");
    }

    /// <summary>
    /// A profile property name without a value, or a value without a name, is refused, because half of a
    /// property filter would silently widen the search rather than narrow it.
    /// </summary>
    /// <param name="name">The property name to submit.</param>
    /// <param name="value">The property value to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("Street", null)]
    [InlineData(null, "Fleet Street")]
    public async Task ListUsers_RefusesHalfOfAProfilePropertyFilter(string? name, string? value)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest(),
            profilePropertyName: name,
            profilePropertyValue: value,
            cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ListFilterInvalidCode);
        outcome.Reason!.Message.Should()
            .Be("The profile property name and value must be supplied together.");
    }

    /// <summary>
    /// Two filters at once are refused, because the store applies one and a caller would not be able to
    /// tell which.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_RefusesMoreThanOneFilter()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest(),
            userNameFilter: "gra",
            emailFilter: "gra",
            cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ListFilterInvalidCode);
        outcome.Reason!.Message.Should().Be(
            "Only one of the account name, electronic-mail and profile property filters may be supplied.");
    }

    /// <summary>
    /// A profile property the tenant does not declare is refused by name, so a caller can tell a misspelled
    /// property from a search that simply matched nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_RefusesAnUndeclaredProfileProperty()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest(),
            profilePropertyName: "Nickname",
            profilePropertyValue: "Amazing",
            cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ListUnknownProfilePropertyCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} does not define a profile property named \"Nickname\".");
    }

    /// <summary>
    /// A declared profile property is resolved to its identifier before the store is asked, and matched
    /// without regard to case.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_ResolvesADeclaredProfilePropertyToItsIdentifier()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest(),
            profilePropertyName: "sTrEeT",
            profilePropertyValue: "Fleet Street",
            cancellationToken: CancellationToken.None);

        harness.Users.Verify(
            u => u.ListAsync(
                PortalId,
                0,
                10,
                null,
                null,
                null,
                StreetPropertyId,
                "Fleet Street",
                null,
                true,
                false,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The search includes accounts awaiting authorisation and excludes host accounts, because this listing
    /// exists so that a tenant administrator can find the accounts it may act on.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_IncludesUnauthorisedAccountsAndExcludesHostAccounts()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageIndex = 1, PageSize = 25, Query = "gra" },
            isApproved: false,
            cancellationToken: CancellationToken.None);

        harness.Users.Verify(
            u => u.ListAsync(
                PortalId,
                1,
                25,
                "gra",
                null,
                null,
                null,
                null,
                false,
                true,
                false,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A search term of only white space is treated as absent.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_TreatsAWhitespaceSearchTermAsAbsent()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { Query = "   " },
            cancellationToken: CancellationToken.None);

        harness.Users.Verify(
            u => u.ListAsync(
                PortalId,
                0,
                10,
                null,
                null,
                null,
                null,
                null,
                null,
                true,
                false,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The address column is composed from the declared address parts, in the order the parts are declared
    /// rather than in storage order, and parts the account left blank are left out entirely.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_ComposesTheAddressFromTheDeclaredParts()
    {
        Harness harness = Harness.Ready();
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser()]);
        harness.ValuesByUserId[UserId] =
        [
            Value(2, UserId, CityPropertyId, "London"),
            Value(1, UserId, StreetPropertyId, "Fleet Street"),
            Value(3, UserId, TelephonePropertyId, "555-0100"),
        ];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        UserListItemDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.Address.Should().Be("Fleet Street, London");
        row.Telephone.Should().Be("555-0100");
    }

    /// <summary>
    /// An account that recorded no address part reports no address rather than an empty separator run.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_ReportsNoAddressWhenTheAccountRecordedNone()
    {
        Harness harness = Harness.Ready();
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser()]);
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "   ")];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        UserListItemDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.Address.Should().BeNull();
        row.Telephone.Should().BeNull();
    }

    /// <summary>
    /// A tenant that declares neither an address part nor a telephone property reads no profile values at
    /// all, so a listing costs one query rather than one per account.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_ReadsNoProfileValuesWhenTheTenantDeclaresNoneOfThoseProperties()
    {
        Harness harness = Harness.Ready();
        harness.Definitions.Clear();
        harness.Definitions.Add(Definition(FirstNamePropertyId, "FirstName"));
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser(), StoredUser(OtherUserId, "ada")]);

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        outcome.Value.Items.Should().HaveCount(2);
        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Neither read is issued - not the per-account one this listing no longer uses, and not the batched
        // one it does. A tenant declaring none of these properties has nothing to fetch.
        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A page of accounts costs exactly ONE profile read, issued for the page's own accounts, however many
    /// rows the page holds.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The per-row read this replaces ran AFTER the database had already windowed the page, so the very
    /// windowing that made the listing bounded was followed by a sequence of reads bounded only by the
    /// requested page size.
    /// </remarks>
    [Fact]
    public async Task ListUsers_ReadsTheWholePageProfileValuesInOneCallKeyedOnThePagesOwnAccounts()
    {
        Harness harness = Harness.Ready();
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser(), StoredUser(OtherUserId, "ada")]);
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "Fleet Street")];
        harness.ValuesByUserId[OtherUserId] =
        [
            Value(2, OtherUserId, CityPropertyId, "Cambridge"),
            Value(3, OtherUserId, TelephonePropertyId, "555-0199"),
        ];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        // Each row still resolves its OWN values: batching must not smear one account's answers across the
        // page.
        UserListItemDto first = outcome.Value.Items.Single(row => row.UserId == UserId);
        UserListItemDto second = outcome.Value.Items.Single(row => row.UserId == OtherUserId);
        first.Address.Should().Be("Fleet Street");
        first.Telephone.Should().BeNull();
        second.Address.Should().Be("Cambridge");
        second.Telephone.Should().Be("555-0199");

        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(
                PortalId,
                It.Is<IReadOnlyCollection<int>>(ids =>
                    ids.Count == 2 && ids.Contains(UserId) && ids.Contains(OtherUserId)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A request that asks for no page size receives an unpaged answer; a paged request keeps the store's
    /// own coordinates.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_ReportsThePagingItWasAskedFor()
    {
        Harness harness = Harness.Ready();
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser()]);

        Result<PagedResult<UserListItemDto>> unpaged = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        unpaged.Value.IsUnpaged.Should().BeTrue();

        harness.UserPage = PagedResult<User>.Create([StoredUser()], totalCount: 40, pageIndex: 3, pageSize: 5);

        Result<PagedResult<UserListItemDto>> paged = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageIndex = 3, PageSize = 5 },
            cancellationToken: CancellationToken.None);

        paged.Value.TotalCount.Should().Be(40);
        paged.Value.PageIndex.Should().Be(3);
        paged.Value.PageSize.Should().Be(5);
    }

    /// <summary>
    /// A hidden PROFILE column is withheld by not being read at all, while every ACCOUNT column is projected
    /// exactly as stored.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS TEST USED TO REQUIRE THE ACCOUNT COLUMNS TO BE EMPTIED TOO, and requiring it was the
    /// defect. It reduced a stored first name, last name, display name and electronic-mail address to the
    /// empty string, a creation instant and a last-login instant to absent, and an APPROVED account to
    /// unapproved - so a caller could not tell a member who had never been approved from one whose tenant
    /// simply does not put that column in its grid.
    /// </para>
    /// <para>
    /// The legacy grid honoured a hidden column by DECLINING TO RENDER it, at
    /// <c>UserModuleBase.vb:L98-L115</c>; the value stayed in the row. A rendering setting decides what is
    /// displayed, not what the account is, and reporting an approved account as unapproved is a presentation
    /// setting changing a membership FACT.
    /// </para>
    /// <para>
    /// The two profile members are different in kind and the minimisation there is real, so it is kept: an
    /// address and a telephone number are profile ANSWERS the listing composes from a second, batched read,
    /// and a tenant that renders neither pays for neither read. Their absence means "not requested".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_WithholdsTheProfileReadsAndProjectsEveryAccountColumnAsStored()
    {
        User stored = StoredUser();
        stored.CreatedDate = new DateTime(2019, 4, 7, 9, 30, 0, DateTimeKind.Utc);
        stored.LastLoginDate = new DateTime(2024, 11, 2, 16, 45, 0, DateTimeKind.Utc);
        stored.IsApproved = true;

        Harness harness = Harness.Ready();
        harness.UserPage = PagedResult<User>.Unpaged([stored]);
        harness.AddMembershipSettingsSource();
        harness.StoredModuleSettings[ModuleId].AddRange(
        [
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_FirstName", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_LastName", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_DisplayName", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_Address", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_Telephone", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_Email", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_CreatedDate", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_LastLogin", SettingValue = "False" },
            new ModuleSetting { ModuleId = ModuleId, SettingName = "Column_Authorized", SettingValue = "False" },
        ]);

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        UserListItemDto row = outcome.Value.Items.Should().ContainSingle().Subject;

        // Username carries no visibility flag at all - the legacy grid made it unconditionally visible - and
        // the identifiers a client needs to reach the privileged reads are likewise never withheld.
        row.Username.Should().Be(Username);
        row.UserId.Should().Be(UserId);

        // EVERY ACCOUNT COLUMN IS AS STORED, although this tenant hides all nine of them.
        row.FirstName.Should().Be(
            stored.FirstName,
            "a hidden column is not rendered by a grid; it is not erased from a payload");
        row.LastName.Should().Be(stored.LastName);
        row.DisplayName.Should().Be(stored.DisplayName);
        row.Email.Should().Be(
            stored.Email,
            "an electronic-mail address the store holds is not the empty string because a grid hides it");
        row.CreatedDate.Should().Be(
            stored.CreatedDate,
            "a creation instant is a fact about the account, not a rendering choice");
        row.LastLoginDate.Should().Be(stored.LastLoginDate);
        row.IsApproved.Should().BeTrue(
            "⚠ THE WORST OF THE NINE: an approved account reported as unapproved is a presentation setting "
            + "changing a membership fact, and no caller can tell the two apart");

        // Profile values: withheld a step earlier, by not being fetched at all.
        row.Address.Should().BeNull("the address read is skipped when the tenant hides the column");
        row.Telephone.Should().BeNull("the telephone read is skipped when the tenant hides the column");

        harness.Profiles.Verify(
            profiles => profiles.GetProfileValuesAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());

        harness.Profiles.Verify(
            profiles => profiles.GetProfileValuesAsync(
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Reading one account reports absence rather than a failure, and carries the role names that are in
    /// force at the present moment.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetUser_ReportsAbsenceOrTheDetailWithItsRolesInForce()
    {
        Harness harness = Harness.Ready();
        harness.RoleNamesForUser = ["Administrators", "Registered Users"];

        Result<UserDetailDto?> present = await harness.Service
            .GetUserAsync(PortalId, UserId, CancellationToken.None);

        present.IsSuccess.Should().BeTrue();
        present.Value!.UserId.Should().Be(UserId);
        present.Value.PortalId.Should().Be(PortalId);
        present.Value.Username.Should().Be(Username);
        present.Value.Roles.Should().Equal(new[] { "Administrators", "Registered Users" });
        harness.Users.Verify(
            u => u.ListRoleNamesAsync(PortalId, UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once);

        harness.LookupUser = null;

        Result<UserDetailDto?> absent = await harness.Service
            .GetUserAsync(PortalId, UserId, CancellationToken.None);

        absent.IsSuccess.Should().BeTrue();
        absent.Value.Should().BeNull();
    }

    /// <summary>
    /// Reading one account takes the designated administrator from the tenant facts already settled for the
    /// call, so no second tenant read is issued for one column.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetUser_ReadsTheDesignatedAdministratorFromTheCallScopedTenantFacts()
    {
        Harness harness = Harness.Ready();
        harness.PortalContext.SetupGet(holder => holder.IsResolved).Returns(true);
        harness.PortalContext.SetupGet(holder => holder.Current).Returns(
            TenantFacts(PortalId, administratorId: UserId));

        Result<UserDetailDto?> outcome = await harness.Service
            .GetUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.CanDelete.Should().BeFalse(
            "the snapshot designates this very account, so the value was read rather than lost");

        harness.Portals.Verify(
            p => p.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A snapshot describing a DIFFERENT tenant is not trusted, so the designated administrator is read
    /// from persistence for the tenant actually being acted on.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// ⚠ THE CORRECTNESS HALF OF THE OPTIMISATION, AND THE REASON THE TENANT IS COMPARED AT ALL. The
    /// snapshot describes the tenant the REQUEST was addressed to, which is not always the tenant the call
    /// acts on: a host-level caller may name any portal it administers, which is precisely why the
    /// identifier arrives as an argument.
    /// </remarks>
    [Fact]
    public async Task GetUser_IgnoresASnapshotDescribingAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalContext.SetupGet(holder => holder.IsResolved).Returns(true);
        harness.PortalContext.SetupGet(holder => holder.Current).Returns(
            TenantFacts(PortalId + 1, administratorId: UserId));
        harness.PortalRow!.AdministratorId = AdministratorId;

        Result<UserDetailDto?> outcome = await harness.Service
            .GetUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.CanDelete.Should().BeTrue(
            "the acted-on tenant designates a different account, and its own row is what decides");

        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Outside a request scope the tenant facts are unresolved, so the designated administrator is read
    /// from persistence and the holder is never dereferenced.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The holder's contract states that reading its current snapshot before the tenant has been settled
    /// THROWS, so a background caller - or any unit test - must fall through rather than fault. This is the
    /// default the harness reports, and it is asserted explicitly because it is the path every other case
    /// in this file silently depends on.
    /// </remarks>
    [Fact]
    public async Task GetUser_FallsBackToPersistenceWhenNoTenantHasBeenResolved()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorId = UserId;

        Result<UserDetailDto?> outcome = await harness.Service
            .GetUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.CanDelete.Should().BeFalse("the stored row designates this account");

        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.PortalContext.VerifyGet(holder => holder.Current, Times.Never());
    }

    /// <summary>Creating an account requires a request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateUserAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// An account with no name or no address is refused before anything is read, because both are columns
    /// the row cannot be written without.
    /// </summary>
    /// <param name="omission">Which of the two to leave blank.</param>
    /// <param name="expectedCode">The failure code the service is measured to report.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("username", CreateInvalidUsernameCode, "An account name is required.")]
    [InlineData("email", CreateInvalidEmailCode, "An electronic-mail address is required.")]
    public async Task CreateUser_RefusesAnAccountWithNoNameOrAddress(
        string omission,
        string expectedCode,
        string expectedMessage)
    {
        Harness harness = Harness.Ready();
        CreateUserRequest request = ValidCreateRequest();
        if (omission == "username")
        {
            request.Username = "   ";
        }
        else
        {
            request.Email = "  ";
        }

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(expectedCode);
        outcome.Reason!.Message.Should().Be(expectedMessage);
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>An account cannot be created in a tenant that does not exist.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreatePortalAssignmentFailedCode);
        outcome.Reason!.Message.Should().Be($"Portal {PortalId} does not exist.");
    }

    /// <summary>
    /// A credential is required unless one is to be generated, and a confirmation that does not match is
    /// refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RefusesAMissingOrUnconfirmedCredential()
    {
        Harness harness = Harness.Ready();

        CreateUserRequest missing = ValidCreateRequest();
        missing.Password = string.Empty;

        Result<UserDetailDto> withoutCredential = await harness.Service
            .CreateUserAsync(PortalId, missing, CancellationToken.None);

        withoutCredential.Reason!.Code.Should().Be(CreateInvalidPasswordCode);
        withoutCredential.Reason!.Message.Should().Be("A credential is required.");

        CreateUserRequest unconfirmed = ValidCreateRequest();
        unconfirmed.ConfirmPassword = "something-else";

        Result<UserDetailDto> mismatch = await harness.Service
            .CreateUserAsync(PortalId, unconfirmed, CancellationToken.None);

        mismatch.Reason!.Code.Should().Be(CreatePasswordMismatchCode);
        mismatch.Reason!.Message.Should().Be("The credential and its confirmation do not match.");
    }

    /// <summary>
    /// A confirmation that was not submitted at all is not treated as a mismatch, because the endpoint may
    /// legitimately be called without one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_AcceptsARequestThatSubmitsNoConfirmation()
    {
        Harness harness = Harness.Ready();
        CreateUserRequest request = ValidCreateRequest();
        request.ConfirmPassword = string.Empty;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A credential shorter than the configured minimum is refused, and the message names the bound so the
    /// caller can correct it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RefusesACredentialShorterThanTheConfiguredMinimum()
    {
        Harness harness = Harness.Ready();
        harness.PasswordPolicy.MinRequiredPasswordLength = 12;
        CreateUserRequest request = ValidCreateRequest();
        request.Password = "short";
        request.ConfirmPassword = "short";

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        outcome.Reason!.Code.Should().Be(CreateInvalidPasswordCode);
        outcome.Reason!.Message.Should().Be("The credential must be at least 12 characters long.");
    }

    /// <summary>
    /// A configured requirement for non-alphanumeric characters is enforced, and is skipped entirely when
    /// the requirement is nothing — which is what the legacy deployment configured.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_EnforcesTheConfiguredNonAlphanumericRequirement()
    {
        Harness harness = Harness.Ready();
        harness.PasswordPolicy.MinRequiredNonAlphanumericCharacters = 2;
        CreateUserRequest request = ValidCreateRequest();
        request.Password = "abcdefghij";
        request.ConfirmPassword = "abcdefghij";

        Result<UserDetailDto> refused = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        refused.Reason!.Code.Should().Be(CreateInvalidPasswordCode);
        refused.Reason!.Message.Should()
            .Be("The credential must contain at least 2 non-alphanumeric character(s).");

        request.Password = "abcdefgh-!";
        request.ConfirmPassword = "abcdefgh-!";

        Result<UserDetailDto> accepted = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A configured strength rule is applied, and a rule that cannot be applied refuses the credential
    /// rather than admitting it, because failing open would silently drop the requirement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_FailsClosedWhenTheConfiguredStrengthRuleCannotBeApplied()
    {
        Harness harness = Harness.Ready();
        harness.PasswordPolicy.PasswordStrengthRegularExpression = "^[0-9]+$";
        CreateUserRequest request = ValidCreateRequest();

        Result<UserDetailDto> refused = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        refused.Reason!.Code.Should().Be(CreateInvalidPasswordCode);
        refused.Reason!.Message.Should()
            .Be("The credential does not satisfy the configured strength rule.");

        harness.PasswordPolicy.PasswordStrengthRegularExpression = "([";

        Result<UserDetailDto> unapplicable = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        unapplicable.Reason!.Code.Should().Be(CreateInvalidPasswordCode);
        unapplicable.Reason!.Message.Should()
            .Be("The configured credential strength rule could not be applied.");
    }

    /// <summary>
    /// An account name held elsewhere in the installation is refused, and the reason distinguishes an
    /// account that is already a member of this tenant from one that merely holds the name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_DistinguishesAnExistingMemberFromAHeldName()
    {
        Harness harness = Harness.Ready();
        harness.UserByUsername = StoredUser(OtherUserId, Username);
        harness.Membership = new UserPortal { UserPortalId = 3, UserId = OtherUserId, PortalId = PortalId };

        Result<UserDetailDto> alreadyMember = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        alreadyMember.Reason!.Code.Should().Be(CreateUserAlreadyRegisteredCode);
        alreadyMember.Reason!.Message.Should()
            .Be($"Account \"{Username}\" is already registered in portal {PortalId}.");

        harness.Membership = null;

        Result<UserDetailDto> heldName = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        heldName.Reason!.Code.Should().Be(CreateUsernameAlreadyExistsCode);
        heldName.Reason!.Message.Should().Be($"Account name \"{Username}\" is already in use.");
        harness.AddedUsers.Should().BeEmpty();
    }

    /// <summary>
    /// An account name taken between the checks and the commit is refused with exactly the answer the first
    /// of those checks gives, rather than being reported as a server fault.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Four checks run before this insert and none can close the window in front of it: <c>IX_Users</c> is
    /// unique over the account name, so two requests carrying the same name arriving together all read "not
    /// taken" and the loser's insert is refused by the index.
    /// </remarks>
    [Fact]
    public async Task CreateUser_RefusesAnAccountNameTakenBetweenTheChecksAndTheCommit()
    {
        Harness harness = Harness.Ready();
        harness.CommitFault = DuplicateKeyException.ForConstraint("IX_Users", null);

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue(
            "the store refused the insert, so no account exists and the caller must not be told one does");
        outcome.Reason!.Code.Should().Be(
            CreateUserAlreadyRegisteredCode,
            "the winner of the race has by now created the account and registered it here, which is exactly "
            + "the state the first sequential check describes");
        outcome.Reason!.Message.Should()
            .Be($"Account \"{Username}\" is already registered in portal {PortalId}.");
    }

    /// <summary>
    /// A name the store reports as taken is refused even when no account row was loaded, because sign-in
    /// names are installation-wide and the credential store holds them too.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RefusesANameTheStoreReportsAsTaken()
    {
        Harness harness = Harness.Ready();
        harness.UsernameTaken = true;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.Reason!.Code.Should().Be(CreateUsernameAlreadyExistsCode);
        harness.Users.Verify(
            u => u.UsernameExistsAsync(Username, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A duplicate address is refused only when the deployment requires addresses to be unique, which the
    /// legacy deployment did not.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RefusesADuplicateAddressOnlyWhenUniquenessIsRequired()
    {
        Harness harness = Harness.Ready();
        harness.EmailTaken = true;

        Result<UserDetailDto> permitted = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        permitted.IsSuccess.Should().BeTrue();
        harness.Users.Verify(
            u => u.EmailExistsAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        harness.PasswordPolicy.RequiresUniqueEmail = true;

        Result<UserDetailDto> refused = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        refused.Reason!.Code.Should().Be(CreateDuplicateEmailCode);
        refused.Reason!.Message.Should().Be("The electronic-mail address is already in use.");
    }

    /// <summary>
    /// The new account is enrolled in the tenant with the authorisation the request asked for, and in every
    /// role the tenant assigns automatically.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_EnrolsTheAccountInTheTenantAndEveryAutomaticRole()
    {
        Harness harness = Harness.Ready();
        harness.AutoAssigned.Add(new Role { RoleId = 5, RoleName = "Registered Users", AutoAssignment = true });
        harness.AutoAssigned.Add(new Role { RoleId = 6, RoleName = "Subscribers", AutoAssignment = true });

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        User created = harness.AddedUsers.Should().ContainSingle().Which;
        UserPortal membership = created.UserPortals.Should().ContainSingle().Which;
        membership.PortalId.Should().Be(PortalId);
        membership.CreatedDate.Should().Be(Now);
        membership.IsAuthorised.Should().BeTrue();

        created.UserRoles.Select(assignment => assignment.RoleId).Should().Equal(new[] { 5, 6 });
        outcome.Value.Roles.Should().Equal(new[] { "Registered Users", "Subscribers" });
    }

    /// <summary>
    /// An account created without authorisation is enrolled unauthorised and its credential is written
    /// unapproved, so the two records agree.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_KeepsTheRowAndTheCredentialInAgreementWhenNotAuthorised()
    {
        Harness harness = Harness.Ready();
        CreateUserRequest request = ValidCreateRequest();
        request.Authorize = false;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        harness.AddedUsers.Single().UserPortals.Single().IsAuthorised.Should().BeFalse();
        harness.CreatedCredentials.Should().ContainSingle().Which.IsApproved.Should().BeFalse();
        outcome.Value.IsApproved.Should().BeFalse();
    }

    /// <summary>
    /// The account row commits before the credential is written, because the external store is keyed by the
    /// identifier that commit issues.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_CommitsTheRowBeforeWritingTheCredential()
    {
        Harness harness = Harness.Ready();

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.CommitsBeforeCredential.Should().Be(1);
        harness.CreatedCredentials.Should().ContainSingle();
        harness.CreatedCredentials[0].PasswordHash.Should().Be(StoredHashFor(CurrentPassword));
        harness.CreatedCredentials[0].UtcNow.Should().Be(Now);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A committed creation is recorded on the audit trail under the legacy event name, with the operator
    /// as the actor and the new account as the subject.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RecordsTheLegacyUserCreatedAuditEvent()
    {
        Harness harness = Harness.Ready();

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_CREATED");
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.PortalId.Should().Be(PortalId);
        record.ResourceType.Should().Be("User");
        record.Properties.Should().NotContainKey(
            "Username",
            "the stable subject and resource identifiers replace a retained account name");
        record.Properties.Should().NotContainKey(
            "Password",
            "no credential material of any kind may reach an audit record");
    }

    /// <summary>
    /// A creation whose credential the store refuses records nothing, so a record never describes an
    /// account that was withdrawn again.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RecordsNothingWhenTheCredentialIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.CredentialCreated = false;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A credential the store refuses rolls the open creation transaction back, so no compensation write is
    /// required and no account is left that cannot sign in.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RollsBackWhenTheCredentialIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.AutoAssigned.Add(new Role { RoleId = 5, RoleName = "Registered Users", AutoAssignment = true });
        harness.CredentialCreated = false;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreateDuplicateUsernameCode);
        outcome.Reason!.Message.Should().Be(
            $"The credential store already holds a credential for account name \"{Username}\".");

        harness.RemovedAssignments.Should().BeEmpty();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(transaction => transaction.DisposeAsync(), Times.Once());
        harness.InvalidatedPortalIds.Should().BeEmpty();
    }

    /// <summary>
    /// A credential store failure the classifier attributes to the store rolls the transaction back and is
    /// reported as a provider error in fixed wording, without letting the exception escape as a
    /// five-hundred.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The published message no longer carries the exception type name. A caller can do nothing with a type
    /// name, and it is the exception's own shape rather than authored text, so publishing it disclosed
    /// which client library and which failure mode a write had hit to whoever asked.
    /// </remarks>
    [Fact]
    public async Task CreateUser_RollsBackAndKeepsTheFaultKindOffTheCallerFacingResult()
    {
        Harness harness = Harness.Ready();
        var fault = new TimeoutException("the store did not answer");
        harness.CredentialFault = fault;
        harness.StoreFailures.Setup(classifier => classifier.IsStoreUnavailable(fault)).Returns(true);

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreateProviderErrorCode);
        outcome.Reason!.Message.Should().NotContain(
            nameof(TimeoutException),
            "an exception type name is a diagnostic for the log, not a sentence for a caller");

        // The caller learns that the write did not happen and how to get help, and nothing else. No CLR
        // type name, no provider name, no exception message.
        outcome.Reason!.Message.Should().Be(
            "The credential store could not be written, so the account was not created. Try again, "
            + "and quote the correlation identifier from the response if the problem persists.");
        outcome.Reason!.Message.Should().NotContain("Exception");
        outcome.Reason!.Message.Should().NotContain("the store did not answer");

        // The operator keeps everything the caller lost.
        harness.DiagnosedOccurrences.Should().ContainSingle();
        (SecurityDiagnosticEvent occurrence, int? portalId, int? userId, string? reasonCode) =
            harness.DiagnosedOccurrences[0];
        occurrence.Should().Be(SecurityDiagnosticEvent.CredentialStoreWriteFailed);
        portalId.Should().Be(PortalId);
        userId.Should().NotBeNull();
        reasonCode.Should().Be(nameof(TimeoutException));

        harness.RemovedUsers.Should().BeEmpty();
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(transaction => transaction.DisposeAsync(), Times.Once());
    }

    /// <summary>
    /// A credential failure the classifier does NOT attribute to the store is allowed to surface rather
    /// than being reported to the caller as a store fault.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_LetsAnUnclassifiedFailureSurface()
    {
        Harness harness = Harness.Ready();
        harness.CredentialFault = new InvalidOperationException("a collaborator was misused");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None));

        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(transaction => transaction.DisposeAsync(), Times.Once());
        harness.InvalidatedPortalIds.Should().BeEmpty();
    }

    /// <summary>
    /// A submitted account name carrying surrounding whitespace is canonicalised once, so the row that is
    /// stored is the row every later lookup can find.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The defect this pins was an account nobody could reach.
    /// </remarks>
    [Fact]
    public async Task CreateUser_CanonicalisesTheSubmittedAccountName()
    {
        Harness harness = Harness.Ready();
        CreateUserRequest request = ValidCreateRequest();
        request.Username = $"  {Username}  ";

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedUsers.Should().ContainSingle().Which.Username.Should().Be(
            Username,
            "the stored name is the canonical one, so the reads that normalise their argument can find it");

        harness.Users.Verify(
            users => users.GetByUsernameAsync(null, Username, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Users.Verify(
            users => users.UsernameExistsAsync(Username, null, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Users.Verify(
            users => users.GetByUsernameAsync(
                It.IsAny<int?>(),
                It.Is<string>(name => name != Username),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A cancellation is not absorbed into a failure result, because the caller withdrew the request rather
    /// than the store failing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_LetsACancellationSurface()
    {
        Harness harness = Harness.Ready();
        harness.CredentialFault = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Service.CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None));

        // Nothing is withdrawn and nothing is committed: the scope is disposed as the exception unwinds and
        // the store reverses the flush.
        harness.RemovedUsers.Should().BeEmpty();
        harness.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never());
        harness.Transaction.Verify(t => t.DisposeAsync(), Times.Once());
    }

    /// <summary>
    /// A created account records its creation and credential moments and is not locked, and both caches
    /// that could hold a stale answer are discarded.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RecordsTheMomentsAndDiscardsTheCaches()
    {
        Harness harness = Harness.Ready();

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.Value.CreatedDate.Should().Be(Now);
        outcome.Value.LastPasswordChangeDate.Should().Be(Now);
        outcome.Value.IsLockedOut.Should().BeFalse();
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
    }

    /// <summary>
    /// The listing publishes, per row, whether the removal operation will accept that account - so a client
    /// can withhold the affordance rather than offering a command the server refuses.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_PublishesWhetherEachAccountMayBeRemoved()
    {
        Harness harness = Harness.Ready();
        harness.UserPage = PagedResult<User>.Unpaged(
        [
            StoredUser(),
            StoredUser(AdministratorId, "portal_admin"),
        ]);
        harness.PortalRow!.AdministratorId = AdministratorId;

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        outcome.Value.Items.Single(row => row.UserId == UserId)
            .CanDelete.Should().BeTrue("an ordinary account of the tenant may be removed");
        outcome.Value.Items.Single(row => row.UserId == AdministratorId)
            .CanDelete.Should().BeFalse("the tenant designates this account as its administrator");
    }

    /// <summary>
    /// The tenant is read ONCE for the whole page rather than once per row, and not at all for a page that
    /// matched nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The deletion capability needs one fact about the tenant, and a listing renders up to a hundred rows.
    /// Reading per row would turn one query into a hundred; reading for an empty page would spend a query
    /// to decide nothing.
    /// </remarks>
    [Fact]
    public async Task ListUsers_ReadsTheTenantOncePerPageAndNotAtAllForAnEmptyOne()
    {
        Harness populated = Harness.Ready();
        populated.UserPage = PagedResult<User>.Unpaged(
        [
            StoredUser(),
            StoredUser(OtherUserId, "ada"),
            StoredUser(AdministratorId, "portal_admin"),
        ]);

        await populated.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        populated.Portals.Verify(
            portals => portals.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once());

        Harness empty = Harness.Ready();

        await empty.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        empty.Portals.Verify(
            portals => portals.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A tenant whose row cannot be read, or which designates nobody, still publishes a usable listing and
    /// protects no account.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The capability is advisory - the operation itself re-checks - so an unreadable tenant row must not
    /// fail the listing. It must also not be read as "everybody is protected", which would leave an
    /// operator with a grid on which nothing can be removed and no explanation.
    /// </remarks>
    [Fact]
    public async Task ListUsers_ProtectsNoAccountWhenTheTenantRowIsAbsentOrDesignatesNobody()
    {
        Harness absent = Harness.Ready();
        absent.UserPage = PagedResult<User>.Unpaged([StoredUser()]);
        absent.PortalRow = null;

        Result<PagedResult<UserListItemDto>> unreadable = await absent.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        unreadable.IsSuccess.Should().BeTrue();
        unreadable.Value.Items.Should().ContainSingle().Which.CanDelete.Should().BeTrue();

        Harness undesignated = Harness.Ready();
        undesignated.UserPage = PagedResult<User>.Unpaged([StoredUser()]);
        undesignated.PortalRow!.AdministratorId = null;

        Result<PagedResult<UserListItemDto>> nobody = await undesignated.Service.ListUsersAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            cancellationToken: CancellationToken.None);

        nobody.Value.Items.Should().ContainSingle().Which.CanDelete.Should().BeTrue();
    }

    /// <summary>
    /// A tenant that configured a display-name format has it applied to a NEWLY CREATED account too, not
    /// only to an edited one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_AppliesTheTenantsDisplayNameFormat()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[LASTNAME], [FIRSTNAME] ([USERNAME])");

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.DisplayName.Should().Be($"Hopper, Grace ({Username})");
        harness.AddedUsers.Should().ContainSingle().Which
            .DisplayName.Should().Be($"Hopper, Grace ({Username})");
    }

    /// <summary>
    /// The identifier token resolves to the key the INSERT issued, which is the one divergence this path
    /// takes from the legacy screen.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>UserInfo.UpdateDisplayName</c> substitutes <c>Me.UserID.ToString()</c> for <c>[USERID]</c>, and
    /// <c>User.ascx.vb:L212</c> called it BEFORE <c>AddUser</c> - while the property still held the legacy
    /// absent marker. Every account a tenant created under such a format therefore stored the literal text
    /// "-1" as its display name, for every account.
    /// </remarks>
    [Fact]
    public async Task CreateUser_ResolvesTheIdentifierTokenFromTheKeyTheInsertIssued()
    {
        const int issuedKey = 4242;

        Harness harness = Harness.Ready();
        harness.IssuedUserIdOnCommit = issuedKey;
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[USERNAME]#[USERID]");

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.DisplayName.Should().Be($"{Username}#{issuedKey}");
        outcome.Value.DisplayName.Should().NotContain(
            "-1",
            "the legacy substituted the absent marker here, and that is the defect this position corrects");

        harness.Commits.Should().Be(2, "the row is inserted, then the name the insert made resolvable");
        harness.CommitsBeforeCredential.Should().Be(2);
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A tenant that configured no format leaves the submitted display name alone and issues no second
    /// write, and neither does a format whose tokens resolve to exactly what was submitted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_IssuesNoSecondWriteWhenTheFormatChangesNothing()
    {
        Harness unconfigured = Harness.Ready();

        Result<UserDetailDto> submitted = await unconfigured.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        submitted.IsSuccess.Should().BeTrue();
        submitted.Value.DisplayName.Should().Be("Grace Hopper", "the submitted name stands");
        unconfigured.Commits.Should().Be(1);

        Harness agreeing = Harness.Ready();
        agreeing.AddMembershipSettingsSource();
        agreeing.StoreSetting("Security_DisplayNameFormat", "[FIRSTNAME] [LASTNAME]");

        Result<UserDetailDto> unchanged = await agreeing.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        unchanged.IsSuccess.Should().BeTrue();
        unchanged.Value.DisplayName.Should().Be("Grace Hopper");
        agreeing.Commits.Should().Be(1, "the format resolved to the value already staged");
    }

    /// <summary>
    /// A format that expands past the stored width rolls the whole creation back: no credential is written
    /// and the transaction is never committed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The same guard, the same code and the same message shape as the update path, because it is the same
    /// 128-character column being protected. Refusing rather than truncating matters more here than on an
    /// update: a silently shortened name would be the account's first and only display name.
    /// </remarks>
    [Fact]
    public async Task CreateUser_RollsBackWhenTheConfiguredDisplayNameExpandsPastTheStoredWidth()
    {
        CreateUserRequest request = ValidCreateRequest();
        request.Username = new string('u', 70);

        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[USERNAME][USERNAME]");

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DisplayNameTooLongCode);
        outcome.Reason.Message.Should().Be(
            "The tenant's display-name format produces 140 characters for account 0; the stored limit is 128.");

        harness.CreatedCredentials.Should().BeEmpty("the credential is written after the formatted name");
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.AuditRecords.Should().BeEmpty();
        harness.InvalidatedPortalIds.Should().BeEmpty();
    }

    /// <summary>
    /// Updating an account requires a request, and an unknown account is refused by identifier and tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_RequiresARequestAndRefusesAnUnknownAccount()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateUserAsync(PortalId, UserId, null!, CancellationToken.None));

        harness.LookupUser = null;

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Account {UserId} does not exist in portal {PortalId}.");
    }

    /// <summary>
    /// The submitted names and address are applied, and a display name that was left out is derived from
    /// the two given names.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_AppliesTheSubmittedShape()
    {
        Harness harness = Harness.Ready();
        UpdateUserRequest request = ValidUpdateRequest();
        request.DisplayName = "  ";

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupUser!.FirstName.Should().Be("Grace");
        harness.LookupUser!.LastName.Should().Be("Hopper");
        harness.LookupUser!.DisplayName.Should().Be("Grace Hopper");
        harness.LookupUser!.Email.Should().Be("grace.hopper@example.com");
    }

    /// <summary>
    /// An address the caller merely ECHOED BACK is admitted on the strength of already being stored, even
    /// when the tenant's admission expression would refuse it today.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THE MAINTENANCE DEAD END THIS CLOSES. Two independent rules govern an address. The SHAPE rule lives
    /// in the domain and says what an address may look like at all. The ADMISSION rule is the tenant's own
    /// stored expression, which an operator may set to anything. Only the shape rule was ever asked of the
    /// value already sitting in the column - so re-running the admission rule over an echoed-back address
    /// could refuse an update no submission could have satisfied.
    /// </para>
    /// <para>
    /// It was not hypothetical. DotNetNuke's own default expression ends in <c>[a-zA-Z]{2,4}</c>, this
    /// installation's seeded accounts hold <c>.local</c> addresses whose final label is five letters, and
    /// editing an unrelated field on either account - a surname, a display name - was refused outright.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateUser_AdmitsAnUnchangedAddressTheTenantExpressionWouldNowRefuse()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        // The legacy default, verbatim: a final label of two to four letters.
        harness.StoreSetting(
            "Security_EmailValidation",
            @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b");

        // A stored address whose final label is FIVE letters, so the tenant's expression cannot admit it.
        harness.LookupUser!.Email = "member@setup.local";

        UpdateUserRequest request = ValidUpdateRequest();
        request.Email = "member@setup.local";
        request.LastName = "Hopper-Amended";

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "an address already in the column is admitted by being there; the caller proposed no change to "
            + "it and no submission could have satisfied the rule short of altering untouched data");
        harness.LookupUser!.LastName.Should().Be(
            "Hopper-Amended",
            "the field the caller actually came to change is written");
        harness.LookupUser!.Email.Should().Be("member@setup.local", "and the address is left as it was");
    }

    /// <summary>
    /// The echoed-address admission is case-insensitive, because the store matches an address that way.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_TreatsAnEchoedAddressDifferingOnlyInCaseAsUnchanged()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting(
            "Security_EmailValidation",
            @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b");

        harness.LookupUser!.Email = "Member@Setup.Local";

        UpdateUserRequest request = ValidUpdateRequest();
        request.Email = "member@setup.local";

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "the account read by address matches case-insensitively, so two spellings differing only in case "
            + "name one account and echoing either back is echoing the stored value");
    }

    /// <summary>
    /// A GENUINELY NEW address is still put to the tenant's admission expression, so the grandfathering
    /// cannot be used to smuggle one past it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_StillAppliesTheTenantExpressionToAChangedAddress()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting(
            "Security_EmailValidation",
            @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b");

        harness.LookupUser!.Email = "member@setup.local";

        UpdateUserRequest request = ValidUpdateRequest();

        // A DIFFERENT address, and one the tenant's expression refuses for the same reason as the stored one.
        request.Email = "someone.else@setup.local";

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue(
            "the tenant's rule governs what may be WRITTEN; grandfathering is about what is already stored");
        outcome.Reason!.Code.Should().Be(CreateInvalidEmailCode);
        harness.LookupUser!.Email.Should().Be("member@setup.local", "and nothing is written");
    }

    /// <summary>
    /// A stale concurrency token is reported as staleness BEFORE any field rule runs, so a caller editing an
    /// out-of-date snapshot is told to reload rather than told about a field.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy screen was last-write-wins: two operators editing one account both saved, and the second
    /// silently replaced the first. The token is DERIVED from the members an operator can edit rather than
    /// stored, because the legacy schema is immutable and carries no row version.
    /// </remarks>
    [Fact]
    public async Task UpdateUser_RefusesAStaleSnapshotBeforeAnyFieldRule()
    {
        Harness harness = Harness.Ready();

        UpdateUserRequest request = ValidUpdateRequest();
        request.ConcurrencyToken = "not-the-stored-token";

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("user.concurrency_conflict");
        harness.LookupUser!.LastName.Should().Be(
            "Hopper",
            "a stale snapshot writes nothing at all");
    }

    /// <summary>
    /// The token the single read publishes is the token the update accepts, so a client that echoes what it
    /// was given is never told its snapshot is stale.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_AcceptsTheTokenThePrecedingReadPublished()
    {
        Harness harness = Harness.Ready();

        Result<UserDetailDto?> read = await harness.Service
            .GetUserAsync(PortalId, UserId, CancellationToken.None);

        read.IsSuccess.Should().BeTrue();
        string token = read.Value!.ConcurrencyToken;
        token.Should().NotBeNullOrWhiteSpace("a read publishes the token an update is to echo");

        UpdateUserRequest request = ValidUpdateRequest();
        request.ConcurrencyToken = token;

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("the round trip closes");
        outcome.Value.ConcurrencyToken.Should().NotBe(
            token,
            "the written state is different state, so it publishes a different token");
    }

    /// <summary>
    /// An ABSENT token is permissive, which is what keeps every caller written before the token existed
    /// working.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_TreatsAnAbsentTokenAsUnconditional()
    {
        Harness harness = Harness.Ready();

        UpdateUserRequest request = ValidUpdateRequest();
        request.ConcurrencyToken = null;

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "a caller that supplies no token is not claiming to have read anything, so there is no staleness "
            + "to report");
    }

    /// <summary>
    /// A tenant that configured a display-name format has it applied over whatever was submitted, because
    /// the format is a tenant-wide presentation rule rather than a per-account choice.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_AppliesTheTenantsDisplayNameFormat()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[LASTNAME], [FIRSTNAME] ([USERNAME]/[USERID])");

        await harness.Service.UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        harness.LookupUser!.DisplayName.Should().Be($"Hopper, Grace ({Username}/{UserId})");
    }

    /// <summary>
    /// A short tokenised format can expand beyond the stored display-name width. The service reports that
    /// as a request failure before mutating the tracked account or reaching the commit point.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_WhenTheConfiguredDisplayNameExpandsPastTheStoredWidth_WritesNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = StoredUser(username: new string('u', 70));
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[USERNAME][USERNAME]");

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DisplayNameTooLongCode);
        outcome.Reason.Message.Should().Be(
            $"The tenant's display-name format produces 140 characters for account {UserId}; the stored limit is 128.");

        harness.LookupUser.FirstName.Should().Be("Grace");
        harness.LookupUser.LastName.Should().Be("Hopper");
        harness.LookupUser.DisplayName.Should().Be("Grace B Hopper");
        harness.LookupUser.Email.Should().Be(Email);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.InvalidatedUsers.Should().BeEmpty();
    }

    /// <summary>
    /// The computed-width guard is inclusive: a value that occupies all 128 UTF-16 code units of the column
    /// is valid and is committed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_WhenTheConfiguredDisplayNameFitsTheStoredWidth_Succeeds()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = StoredUser(username: new string('u', 64));
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[USERNAME][USERNAME]");

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupUser.DisplayName.Should().HaveLength(128);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A tenant with no account-management module instance has no display-name format, so the submitted
    /// name stands rather than the update failing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_KeepsTheSubmittedDisplayNameWhenTheTenantConfiguredNoFormat()
    {
        Harness harness = Harness.Ready();

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupUser!.DisplayName.Should().Be("Grace B Hopper");
    }

    /// <summary>
    /// A competing write is reported as a conflict to retry rather than as a fault, because the caller's
    /// remedy is to reload and resubmit.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_ReportsACompetingWriteAsAConflict()
    {
        Harness harness = Harness.Ready();
        harness.CommitFault = new DbUpdateConcurrencyException();

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PersistenceConflictCode);
        outcome.Reason!.Message.Should()
            .Be("The account was changed by another request; reload it and try again.");
    }

    /// <summary>
    /// A conflict wrapped in another exception is still recognised, because the store may surface it
    /// through a wrapper rather than directly.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_RecognisesAWrappedConflict()
    {
        Harness harness = Harness.Ready();
        harness.CommitFault = new InvalidOperationException("wrapped", new DbUpdateConcurrencyException());

        Result<UserDetailDto> outcome = await harness.Service
            .UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        outcome.Reason!.Code.Should().Be(PersistenceConflictCode);
    }

    /// <summary>A fault that is not a conflict is not absorbed, because retrying would not help.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_DoesNotAbsorbAnUnrelatedFault()
    {
        Harness harness = Harness.Ready();
        harness.CommitFault = new InvalidOperationException("something else entirely");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None));
    }

    /// <summary>
    /// Updating the tenant's designated administrator discards the tenant's own cache as well as the
    /// account's, because the tenant detail carries the administrator's address.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateUser_DiscardsTheTenantCacheOnlyForItsDesignatedAdministrator()
    {
        Harness harness = Harness.Ready();

        await harness.Service.UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        harness.InvalidatedPortalIds.Should().BeEmpty();
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });

        harness.PortalRow!.AdministratorId = UserId;

        await harness.Service.UpdateUserAsync(PortalId, UserId, ValidUpdateRequest(), CancellationToken.None);

        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>Deleting an unknown account is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_RefusesAnUnknownAccount()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = null;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
    }

    /// <summary>
    /// A host account cannot be deleted through tenant administration, because a tenant administrator would
    /// otherwise be able to remove the account that governs the whole installation.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_RefusesAHostAccount()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.IsSuperUser = true;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DeleteSuperUserProtectedCode);
        outcome.Reason!.Message.Should().Be(
            $"Account {UserId} is a host account and cannot be deleted through portal administration.");
        harness.RemovedUsers.Should().BeEmpty();
    }

    /// <summary>
    /// The tenant's designated administrator cannot be deleted, because the tenant would be left with a
    /// wiring pointer at nothing and no way to administer itself back.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_RefusesTheTenantsDesignatedAdministrator()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorId = UserId;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DeleteAdministratorProtectedCode);
        outcome.Reason!.Message.Should().Be(
            $"Account {UserId} is the designated administrator of portal {PortalId} and cannot be deleted.");
    }

    /// <summary>
    /// The host-account rule is checked before the administrator rule, so the reason a caller is given
    /// names the stronger protection.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_ChecksTheHostRuleBeforeTheAdministratorRule()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.IsSuperUser = true;
        harness.PortalRow!.AdministratorId = UserId;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.Reason!.Code.Should().Be(DeleteSuperUserProtectedCode);
    }

    /// <summary>
    /// Removing an account releases its permission grants, its role assignments and its tenant membership,
    /// and deletes the account and its credential once it belongs to no other tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_ReleasesEverythingAndRemovesTheAccountWhenItBelongsNowhereElse()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.UserAssignments.Add(new UserRole { UserRoleId = 7, UserId = UserId, RoleId = 5 });
        harness.UserAssignments.Add(new UserRole { UserRoleId = 8, UserId = UserId, RoleId = 6 });

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.CascadedUserPermissions.Should().Equal(new[] { (PortalId, UserId) });
        harness.RemovedAssignments.Should().HaveCount(2);
        harness.RemovedMemberships.Should().ContainSingle();
        harness.DeletedCredentialUserIds.Should().Equal(new[] { UserId });
        harness.RemovedUsers.Should().ContainSingle().Which.Should().BeSameAs(harness.LookupUser);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
    }

    /// <summary>
    /// An account that still belongs to another tenant keeps its row and its credential, because removing
    /// either would sign it out of a tenant this request has no authority over.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_KeepsAnAccountThatStillBelongsToAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 2, UserId = UserId, PortalId = OtherPortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedMemberships.Should().ContainSingle();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
    }

    /// <summary>
    /// A committed removal is recorded under the legacy event name, carrying the two facts the legacy
    /// record carried - the account name and the account identifier - plus which arm of the removal ran.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_RecordsTheLegacyUserDeletedAuditEvent()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_DELETED");
        record.PortalId.Should().Be(PortalId);
        record.SubjectUserId.Should().Be(UserId);
        record.ResourceType.Should().Be("User");
        record.Properties.Should().NotContainKey(
            "Username",
            "deleting an account must not leave its name in an independently retained store");
        record.Properties["AccountRemoved"].Should().Be(
            "True",
            "the account belonged to no other tenant, so the row itself went with the membership");
    }

    /// <summary>
    /// A committed removal is recorded even when the post-commit cache maintenance fails, because the
    /// account is gone either way.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Deleting an account is the least reversible thing this service does and the likeliest subject of a
    /// later question, so its record is the last that may depend on the caller still being connected. The
    /// exception is still allowed to escape, deliberately: cache maintenance that did not happen is a real
    /// condition and swallowing it would leave stale grants served from memory.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_RecordsTheRemovalEvenWhenThePostCommitCacheMaintenanceFails()
    {
        Harness harness = Harness.Ready();
        harness.GrantCacheEvictionFault = new OperationCanceledException("the caller disconnected");

        Func<Task> deletion = () => harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        await deletion.Should().ThrowAsync<OperationCanceledException>(
            "maintenance that did not happen is a real condition and must not be swallowed");

        harness.RemovedUsers.Should().ContainSingle("the deletion was committed before the eviction ran");

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_DELETED");
        record.SubjectUserId.Should().Be(UserId);
    }

    /// <summary>
    /// A membership row the store does not hold is not withdrawn, so the delete does not invent a row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_WithdrawsNoMembershipTheStoreDoesNotHold()
    {
        Harness harness = Harness.Ready();
        harness.Membership = null;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.RemovedUsers.Should().ContainSingle();
    }

    /// <summary>
    /// An operation this entry point does not perform is refused by name rather than silently treated as
    /// the one it does.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("reset")]
    [InlineData("RESET")]
    [InlineData("delete")]
    [InlineData("change-question-and-answer")]
    public async Task ChangePassword_RefusesAnOperationItDoesNotPerform(string operation)
    {
        Harness harness = Harness.Ready();
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;
        request.CurrentPassword = null;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordUnsupportedOperationCode);
        outcome.Reason!.Message.Should()
            .Be($"Operation \"{operation}\" is not supported by this endpoint, which performs \"change\". "
                + "Use the endpoint that performs the operation you intend.");
    }

    /// <summary>
    /// The administrative reset refuses a change discriminator for the mirror-image reason, so neither
    /// operation can be reached through the other's endpoint.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("change")]
    [InlineData("Change")]
    [InlineData("change-question-and-answer")]
    public async Task ResetPassword_RefusesAnOperationItDoesNotPerform(string operation)
    {
        Harness harness = Harness.Ready();
        ActAccordingToTheOperation(harness, operation);
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;

        Result outcome = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordUnsupportedOperationCode);
        outcome.Reason!.Message.Should()
            .Be($"Operation \"{operation}\" is not supported by this endpoint, which performs \"reset\". "
                + "Use the endpoint that performs the operation you intend.");
    }

    /// <summary>
    /// An absent discriminator is accepted on both entry points, because the endpoint the caller chose has
    /// already stated which operation was meant and demanding they say it twice would refuse well-formed
    /// requests.
    /// </summary>
    /// <param name="operation">The absent spelling to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CredentialWrite_AcceptsAnAbsentOperationOnBothEntryPoints(string? operation)
    {
        Harness change = Harness.Ready();
        change.ActAsTheAccountOwner();
        ChangePasswordRequest changeRequest = ValidChangeRequest();
        changeRequest.Operation = operation;

        Result changed = await change.Service
            .ChangePasswordAsync(PortalId, UserId, changeRequest, CancellationToken.None);

        changed.IsSuccess.Should().BeTrue(changed.Reason?.ToString());

        Harness reset = Harness.Ready();
        reset.ActAsAPortalAdministrator();
        ChangePasswordRequest resetRequest = ValidChangeRequest();
        resetRequest.Operation = operation;
        resetRequest.CurrentPassword = null;

        Result wasReset = await reset.Service
            .ResetPasswordAsync(PortalId, UserId, resetRequest, CancellationToken.None);

        wasReset.IsSuccess.Should().BeTrue(wasReset.Reason?.ToString());
    }

    /// <summary>Each entry point matches its own operation without regard to case.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CredentialWrite_MatchesItsOwnOperationWithoutRegardToCase()
    {
        Harness change = Harness.Ready();
        change.ActAsTheAccountOwner();
        ChangePasswordRequest changeRequest = ValidChangeRequest();
        changeRequest.Operation = "CHANGE";

        Result changed = await change.Service
            .ChangePasswordAsync(PortalId, UserId, changeRequest, CancellationToken.None);

        changed.IsSuccess.Should().BeTrue(changed.Reason?.ToString());

        Harness reset = Harness.Ready();
        reset.ActAsAPortalAdministrator();
        ChangePasswordRequest resetRequest = ValidChangeRequest();
        resetRequest.Operation = "Reset";
        resetRequest.CurrentPassword = null;

        Result wasReset = await reset.Service
            .ResetPasswordAsync(PortalId, UserId, resetRequest, CancellationToken.None);

        wasReset.IsSuccess.Should().BeTrue(wasReset.Reason?.ToString());
    }

    /// <summary>A request requires a payload.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ChangePasswordAsync(PortalId, UserId, null!, CancellationToken.None));
    }

    /// <summary>The administrative reset requires a payload for the same reason.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ResetPassword_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ResetPasswordAsync(PortalId, UserId, null!, CancellationToken.None));
    }

    /// <summary>
    /// An administrative reset is refused when the deployment switched resets off, and is refused before
    /// the account is even read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ResetPassword_RefusesAResetWhenResetsAreSwitchedOff()
    {
        Harness harness = Harness.Ready();
        harness.PasswordPolicy.PasswordResetEnabled = false;
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = ChangePasswordRequest.OperationReset;

        Result outcome = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordResetNotEnabledCode);
        outcome.Reason!.Message.Should()
            .Be("Administrative credential reset is switched off for this deployment.");
        harness.Users.Verify(
            u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A credential change against an account that switched resets off is still permitted, because a person
    /// changing their own credential is not an administrative reset.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_PermitsAChangeEvenWhenResetsAreSwitchedOff()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();
        harness.PasswordPolicy.PasswordResetEnabled = false;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// An unknown account, and an account that holds no credential, are both refused — and the second is
    /// distinguishable by its message even though it shares a code.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RefusesAnUnknownAccountAndAnAccountWithNoCredential()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();
        harness.LookupUser = null;

        Result unknown = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        unknown.Reason!.Code.Should().Be(NotFoundCode);
        unknown.Reason!.Message.Should()
            .Be($"Account {UserId} does not exist in portal {PortalId}.");

        harness.LookupUser = StoredUser();
        harness.CredentialExists = false;

        Result noCredential = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        noCredential.Reason!.Code.Should().Be(NotFoundCode);
        noCredential.Reason!.Message.Should().Be($"Account {UserId} holds no credential.");
    }

    /// <summary>
    /// A missing or unconfirmed new credential is refused, and a weak one is refused by the same rule that
    /// governs creation.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RefusesAMissingUnconfirmedOrWeakNewCredential()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();

        ChangePasswordRequest missing = ValidChangeRequest();
        missing.NewPassword = null;

        Result withoutNew = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, missing, CancellationToken.None);

        withoutNew.Reason!.Code.Should().Be(PasswordMissingCode);
        withoutNew.Reason!.Message.Should().Be("A new credential is required.");

        ChangePasswordRequest unconfirmed = ValidChangeRequest();
        unconfirmed.ConfirmPassword = "different";

        Result mismatch = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, unconfirmed, CancellationToken.None);

        mismatch.Reason!.Code.Should().Be(PasswordMismatchCode);
        mismatch.Reason!.Message.Should()
            .Be("The new credential and its confirmation do not match.");

        harness.PasswordPolicy.MinRequiredPasswordLength = 40;

        Result weak = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        weak.Reason!.Code.Should().Be(PasswordInvalidCode);
        weak.Reason!.Message.Should().Be("The credential must be at least 40 characters long.");
    }

    /// <summary>A change requires the current credential and refuses an incorrect one.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RequiresTheCurrentCredential()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();

        ChangePasswordRequest withoutCurrent = ValidChangeRequest();
        withoutCurrent.CurrentPassword = null;

        Result missing = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, withoutCurrent, CancellationToken.None);

        missing.Reason!.Code.Should().Be(PasswordMissingCode);
        missing.Reason!.Message.Should().Be("The current credential is required.");

        ChangePasswordRequest wrongCurrent = ValidChangeRequest();
        wrongCurrent.CurrentPassword = "not-the-current-one";

        Result incorrect = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, wrongCurrent, CancellationToken.None);

        incorrect.Reason!.Code.Should().Be(PasswordCurrentIncorrectCode);
        incorrect.Reason!.Message.Should().Be("The current credential is not correct.");
    }

    /// <summary>
    /// The administrative reset does not ask for the current credential at all, which is what makes it
    /// administrative - and is precisely why its endpoint requires administration of the account's own
    /// portal rather than mere authentication.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ResetPassword_DoesNotRequireTheCurrentCredential()
    {
        Harness harness = Harness.Ready();
        ChangePasswordRequest reset = ValidChangeRequest();
        reset.Operation = ChangePasswordRequest.OperationReset;
        reset.CurrentPassword = null;

        // The reset half is administrative, so the acting caller becomes one; that the SAME caller could not
        // have reached it a moment ago is the point of the two halves sitting in one test.
        harness.ActAsAPortalAdministrator();

        Result administrative = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, reset, CancellationToken.None);

        administrative.IsSuccess.Should().BeTrue();
        harness.SetPasswordHashes.Should().Equal(new[] { StoredHashFor(NewPassword) });
    }

    /// <summary>
    /// An incorrect current credential submitted alongside a reset is IGNORED rather than honoured as a
    /// verification, because the reset never consults it. Asserting this keeps a later change from
    /// reintroducing a body-driven decision about whether the credential is checked.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ResetPassword_IgnoresAnIncorrectCurrentCredential()
    {
        Harness harness = Harness.Ready();
        harness.ActAsAPortalAdministrator();
        ChangePasswordRequest reset = ValidChangeRequest();
        reset.Operation = ChangePasswordRequest.OperationReset;
        reset.CurrentPassword = "not-the-current-one";

        Result administrative = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, reset, CancellationToken.None);

        administrative.IsSuccess.Should().BeTrue(administrative.Reason?.ToString());
    }

    /// <summary>
    /// A new credential that matches the stored one is refused, on both operations, because a change that
    /// changes nothing would report success without improving anything.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(ChangePasswordRequest.OperationChange)]
    [InlineData(ChangePasswordRequest.OperationReset)]
    public async Task CredentialWrite_RefusesANewCredentialThatMatchesTheStoredOne(string operation)
    {
        Harness harness = Harness.Ready();
        ActAccordingToTheOperation(harness, operation);
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;
        request.NewPassword = CurrentPassword;
        request.ConfirmPassword = CurrentPassword;

        Result outcome = operation == ChangePasswordRequest.OperationReset
            ? await harness.Service.ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None)
            : await harness.Service.ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordNotDifferentCode);
        outcome.Reason!.Message.Should()
            .Be("The new credential must differ from the one currently stored.");
    }

    /// <summary>
    /// A store that refuses the write is reported as a refusal rather than as a success, so the caller does
    /// not believe a credential changed when it did not.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_ReportsAStoreThatRefusesTheWrite()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();
        harness.PasswordWritten = CredentialWriteOutcome.NoRecord;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordResetFailedCode);
        outcome.Reason!.Message.Should().Be("The credential store refused the change.");
    }

    /// <summary>
    /// A store that refuses the write BECAUSE the credential changed under this request reports its own
    /// outcome, distinct from a store that could not accept the write at all.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The distinct code is deliberate and it is not an oracle: reaching this path already requires having
    /// proved the current credential, or the authority to reset it. Both operations are asserted because
    /// the reset path does not present a current credential at all, so it is the one where a lost race is
    /// most likely - an administrator resetting an account while its owner changes its own credential.
    /// </remarks>
    [Theory]
    [InlineData(ChangePasswordRequest.OperationChange)]
    [InlineData(ChangePasswordRequest.OperationReset)]
    public async Task CredentialWrite_ReportsACredentialThatChangedUnderTheRequest(string operation)
    {
        Harness harness = Harness.Ready();
        ActAccordingToTheOperation(harness, operation);
        harness.PasswordWritten = CredentialWriteOutcome.Superseded;
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;

        Result outcome = operation == ChangePasswordRequest.OperationReset
            ? await harness.Service.ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None)
            : await harness.Service.ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordSupersededCode);
        outcome.Reason!.Message.Should().Contain(
            "submit the change again",
            "the caller resolves a lost race by re-reading and deciding again, so the refusal says so");

        harness.SetPasswordExpectations.Should().Equal(
            new[] { StoredHashFor(CurrentPassword) },
            "the write must be conditional on the representation this request read, or it would overwrite "
            + "whatever replaced it");
    }

    /// <summary>
    /// A successful change hashes the new credential, records the moment on the loaded row, and discards
    /// the account's cache.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_HashesRecordsAndDiscardsTheAccountCache()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.SetPasswordHashes.Should().Equal(new[] { StoredHashFor(NewPassword) });
        harness.LookupUser!.LastPasswordChangeDate.Should().Be(Now);
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
    }

    /// <summary>
    /// Satisfying a standing requirement to change a credential clears it and commits; an account under no
    /// such requirement commits nothing, because the credential lives outside the unit of work.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_ClearsAStandingRequirementAndOtherwiseCommitsNothing()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();

        await harness.Service.ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        harness.LookupUser!.UpdatePassword = true;

        await harness.Service.ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        harness.LookupUser!.UpdatePassword.Should().BeFalse();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A credential change is refused to every caller but the account that owns it, and is refused before
    /// the account is read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Verifying the current credential, which the change branch does, is proof of POSSESSION and not of
    /// AUTHORITY: it establishes that the caller knows the credential, so an administrator acting on
    /// somebody else's account has no business submitting one and uses the reset operation instead.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_RefusesAChangeAimedAtAnAccountThatIsNotTheCallers()
    {
        Harness harness = Harness.Ready();
        harness.ActAsAPortalAdministrator();

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordChangeSelfOnlyForbiddenCode);
        outcome.Reason!.Message.Should().Contain("only be performed by the account that owns it");
        harness.Users.Verify(
            u => u.GetAsync(PortalId, UserId, It.IsAny<CancellationToken>()),
            Times.Never);
        harness.SetPasswordHashes.Should().BeEmpty();
    }

    /// <summary>
    /// A credential change is refused when the caller's token names a different tenant than the account it
    /// addresses, even though the account key matches.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Account keys are allocated by a single installation-wide identity, so the same key names one account
    /// across every tenant; the tenant must therefore agree as well, or a token minted for one portal would
    /// carry self-service authority into another.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_RefusesAChangeWhenTheCallersTokenNamesADifferentTenant()
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = UserId;
        harness.CallerPortalId = OtherPortalId;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordChangeSelfOnlyForbiddenCode);
    }

    /// <summary>
    /// An administrative reset is refused to the account that owns the credential, which is the defect that
    /// made the change operation's current-credential requirement optional.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The route policy legitimately admits an account to its own record, so without this check the owner
    /// could choose the operation that presents no current credential and set a new one without proving it
    /// knew the old — turning a stolen access token into a permanent takeover.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_RefusesAResetFromACallerWithNoAdministrativeAuthority()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = ChangePasswordRequest.OperationReset;
        request.CurrentPassword = null;

        Result outcome = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordResetForbiddenCode);
        outcome.Reason!.Message.Should().Contain("administrative authority");
        harness.SetPasswordHashes.Should().BeEmpty();
    }

    /// <summary>
    /// An administrative reset is refused to a caller whose assignment to the tenant's administrator role
    /// has lapsed, and permitted to one whose assignment is in force.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Authority is judged at an instant against the tenant's own designated role, so a lapsed assignment
    /// confers nothing — which a token minted while it was still in force could not have observed.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_JudgesAResettersAuthorityAtTheCurrentInstant()
    {
        Harness harness = Harness.Ready();
        harness.ActAsAPortalAdministrator();
        harness.UserAssignments.Single(assignment => assignment.UserId == CallerId).ExpiryDate =
            Now.AddDays(-1);

        ChangePasswordRequest lapsed = ValidChangeRequest();
        lapsed.Operation = ChangePasswordRequest.OperationReset;
        lapsed.CurrentPassword = null;

        Result refused = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, lapsed, CancellationToken.None);

        refused.IsFailure.Should().BeTrue();
        refused.Reason!.Code.Should().Be(PasswordResetForbiddenCode);

        harness.UserAssignments.Single(assignment => assignment.UserId == CallerId).ExpiryDate = null;

        ChangePasswordRequest inForce = ValidChangeRequest();
        inForce.Operation = ChangePasswordRequest.OperationReset;
        inForce.CurrentPassword = null;

        Result permitted = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, inForce, CancellationToken.None);

        permitted.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// An administrative reset is refused when the tenant designates no administrator role, because an
    /// unset designation is a configuration gap and a gap must not grant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RefusesAResetWhenTheTenantDesignatesNoAdministratorRole()
    {
        Harness harness = Harness.Ready();
        harness.ActAsAPortalAdministrator();
        harness.PortalRow!.AdministratorRoleId = null;

        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = ChangePasswordRequest.OperationReset;
        request.CurrentPassword = null;

        Result outcome = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordResetForbiddenCode);
    }

    /// <summary>
    /// A host account may reset a credential in a tenant it holds no membership of, because
    /// installation-wide authority is read from its own stored row rather than from a tenant assignment.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_PermitsAResetByAHostAccountHoldingNoTenantMembership()
    {
        Harness harness = Harness.Ready();
        harness.ActAsAHostAccount();
        harness.PortalRow!.AdministratorRoleId = null;

        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = ChangePasswordRequest.OperationReset;
        request.CurrentPassword = null;

        Result outcome = await harness.Service
            .ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.SetPasswordHashes.Should().Equal(new[] { StoredHashFor(NewPassword) });
    }

    /// <summary>
    /// Both credential operations are refused to a caller with no identity at all, so reaching the service
    /// outside a route that establishes one grants nothing.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <param name="expectedCode">The reason the refusal is expected to carry.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("change", PasswordChangeSelfOnlyForbiddenCode)]
    [InlineData("reset", PasswordResetForbiddenCode)]
    public async Task ChangePassword_RefusesBothOperationsToAnUnidentifiedCaller(
        string operation,
        string expectedCode)
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = null;
        harness.CallerPortalId = null;

        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;

        Result outcome = await PerformCredentialWriteAsync(harness, operation, request);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(expectedCode);
    }

    /// <summary>
    /// A membership transition cannot be applied to the acting administrator's own account, on all three
    /// transitions, because that is how an administrator would escape a control it is subject to.
    /// </summary>
    /// <param name="transition">Which transition to attempt.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("unlock")]
    [InlineData("approval")]
    [InlineData("requirechange")]
    public async Task MembershipTransition_CannotBeAppliedToTheActingAdministratorsOwnAccount(string transition)
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = UserId;

        Result outcome = transition switch
        {
            "unlock" => await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None),
            "approval" => await harness.Service.SetUserApprovalAsync(PortalId, UserId, true, CancellationToken.None),
            _ => await harness.Service.RequirePasswordChangeAsync(PortalId, UserId, CancellationToken.None),
        };

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MembershipSelfForbiddenCode);
        outcome.Reason!.Message.Should().Be(
            "A membership transition cannot be applied to the acting administrator's own account.");
        harness.Users.Verify(
            u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An unidentified caller is not mistaken for the account being acted on, so an unauthenticated route
    /// does not accidentally block every transition.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task MembershipTransition_DoesNotMistakeAnUnidentifiedCallerForTheAccount()
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = null;
        harness.CredentialLockedOut = true;

        Result outcome = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A transition against another administrator's account is permitted, so the self rule is genuinely
    /// about the acting account rather than about administrators in general.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task MembershipTransition_IsPermittedAgainstAnotherAccount()
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = OtherUserId;
        harness.CredentialLockedOut = true;

        Result outcome = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Unlocking reports an unknown account and an account with no credential as absent, in that order.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The third case this used to assert - an account that is not locked - moved to <see
    /// cref="UnlockUser_IsIdempotentForAnAccountThatIsAlreadyUnlocked"/> when the refusal became a success.
    /// Neither of the two that remain is the requested end state: an account that does not exist cannot be
    /// unlocked, and one holding no credential has no lockout to clear.
    /// </remarks>
    [Fact]
    public async Task UnlockUser_ReportsAnUnknownAccountAndANoCredentialAccountAsAbsent()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = null;

        Result unknown = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);
        unknown.Reason!.Code.Should().Be(NotFoundCode);
        unknown.Reason!.Message.Should().Be($"Account {UserId} does not exist in portal {PortalId}.");

        harness.LookupUser = StoredUser();
        harness.CredentialExists = false;

        Result noCredential = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);
        noCredential.Reason!.Code.Should().Be(NotFoundCode);
        noCredential.Reason!.Message.Should().Be($"Account {UserId} holds no credential.");
    }

    /// <summary>
    /// Clearing a lockout that is already clear succeeds, and writes nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The expectation this replaced required a refusal, and the refusal was the defect: an operator saw a
    /// failed action for work that had in fact been done, and a retry after a lost answer - or a second
    /// administrator clearing the same lockout - reported failure for a lockout that was gone. The
    /// unlocked state IS the requested end state. The store write is asserted absent as well, because
    /// "succeeded" must not be reached by performing the write again.
    /// </remarks>
    [Fact]
    public async Task UnlockUser_IsIdempotentForAnAccountThatIsAlreadyUnlocked()
    {
        Harness harness = Harness.Ready();
        harness.CredentialExists = true;
        harness.CredentialLockedOut = false;

        Result outcome = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Users.Verify(
            u => u.UnlockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A store that reports the account away between the read and the write is reported as absent rather
    /// than as a success.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UnlockUser_ReportsAnAccountTheStoreLosesBetweenReadAndWrite()
    {
        Harness harness = Harness.Ready();
        harness.CredentialLockedOut = true;
        harness.UnlockSucceeded = false;

        Result outcome = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should().Be($"Account {UserId} holds no credential.");
    }

    /// <summary>
    /// A successful unlock corrects the loaded row and discards the account's cache, and commits nothing
    /// because the lock is held outside the unit of work.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UnlockUser_CorrectsTheLoadedRowWithoutCommitting()
    {
        Harness harness = Harness.Ready();
        harness.CredentialLockedOut = true;
        harness.LookupUser!.IsLockedOut = true;

        Result outcome = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupUser!.IsLockedOut.Should().BeFalse();
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Setting approval refuses an unknown account, an account with no credential, and a transition that
    /// would change nothing — the last one worded for the direction that was asked for.
    /// </summary>
    /// <param name="requested">The approval state to request.</param>
    /// <param name="expectedWording">The wording the service is measured to use.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true, "approved")]
    [InlineData(false, "unapproved")]
    public async Task SetUserApproval_RefusesATransitionThatWouldChangeNothing(bool requested, string expectedWording)
    {
        Harness harness = Harness.Ready();
        harness.CredentialApproved = requested;

        Result outcome = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, requested, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ApprovalUnchangedCode);
        outcome.Reason!.Message.Should().Be($"Account {UserId} is already {expectedWording}.");
    }

    /// <summary>A credential change ends every session the account holds, for both operations.</summary>
    /// <param name="operation">The operation named on the request.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The obligation is stated on the contract in terms rather than left to judgement: a credential
    /// changed because it may have been compromised, or reset because its holder lost it, is of no use to
    /// whoever had it - but a refresh token issued under the old credential keeps yielding fresh access
    /// tokens indefinitely, so a change that left one exchangeable would not end the session it was
    /// performed to end.
    /// </remarks>
    [Theory]
    [InlineData(ChangePasswordRequest.OperationChange)]
    [InlineData("reset")]
    public async Task ChangePassword_EndsEverySessionTheAccountHolds(string operation)
    {
        Harness harness = Harness.Ready();
        ActAccordingToTheOperation(harness, operation);

        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;

        Result outcome = await PerformCredentialWriteAsync(harness, operation, request);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().Equal(
            new[] { UserId, UserId },
            "the sessions are ended once before the credential is written and once after it, and both "
            + "sweeps address the account being changed rather than the caller performing the change");
    }

    /// <summary>
    /// A credential written successfully whose lingering sessions cannot then be ended is reported as a
    /// failure rather than as a success.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion of the pre-write ordering test below, and it answers the question that test does not:
    /// what happens when the FIRST sweep succeeds, the write lands, and the second sweep cannot be
    /// persisted.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_WhenLingeringSessionsCannotBeEnded_ReportsTheFailure()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();
        harness.SessionRevocationsBeforeFailure = 1;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue(
            "a credential replaced with sessions that may still be exchangeable must not be reported as a "
            + "completed change");
        outcome.Reason!.Code.Should().Be(SessionRevocationFailedCode);

        harness.SetPasswordHashes.Should().Equal(
            new[] { StoredHashFor(NewPassword) },
            "the write did happen - this failure is about what could not be done afterwards, which is why "
            + "the refusal names an outage rather than a rejected request");
    }

    /// <summary>
    /// A credential change whose sessions cannot be ended is abandoned, and the stored credential is left
    /// exactly as it was.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The ordering assertion is the substance of this test, not the failure code.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_WhenTheSessionsCannotBeEnded_LeavesTheCredentialUnchanged()
    {
        Harness harness = Harness.Ready();
        harness.ActAsTheAccountOwner();
        harness.SessionsRevoked = false;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(SessionRevocationFailedCode);
        harness.SetPasswordHashes.Should().BeEmpty(
            "the credential write sits after the revocation, so a refused revocation must not reach it");
    }

    /// <summary>Withdrawing an approval ends every session the account holds; granting one ends none.</summary>
    /// <param name="isApproved">The approval state requested.</param>
    /// <param name="expectedRevocations">How many accounts should have their sessions ended.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The asymmetry is deliberate and is stated on the contract: withdrawal ends the account's right to
    /// sign in, so leaving it holding exchangeable refresh tokens would let it keep obtaining access tokens
    /// after the withdrawal; granting takes nothing away, so ending a session because an account gained a
    /// right would be gratuitous.
    /// </remarks>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task SetUserApproval_EndsSessionsOnlyWhenWithdrawing(bool isApproved, int expectedRevocations)
    {
        Harness harness = Harness.Ready();
        harness.CredentialApproved = !isApproved;

        Result outcome = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, isApproved, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().HaveCount(expectedRevocations);
    }

    /// <summary>
    /// A withdrawal whose sessions cannot be ended is abandoned, and the approval is left standing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The same ordering rule as the credential change, and the empty write list proves it the same way: an
    /// account recorded as unapproved while every session it holds remains exchangeable is the worst of the
    /// available outcomes, because the operation would have reported failure while the account had in fact
    /// lost its approval and kept its sessions.
    /// </remarks>
    [Fact]
    public async Task SetUserApproval_WhenTheSessionsCannotBeEnded_LeavesTheApprovalStanding()
    {
        Harness harness = Harness.Ready();
        harness.CredentialApproved = true;
        harness.SessionsRevoked = false;

        Result outcome = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, isApproved: false, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(SessionRevocationFailedCode);
        harness.ApprovalWrites.Should().BeEmpty();
    }

    /// <summary>Deleting an account ends every session it held, before anything is removed.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A refresh token outliving the account it names is the worst of the three cases the obligation
    /// covers: the account is gone, so nothing remains for an administrator to inspect or disable, and yet
    /// the token would still be exchanged for access tokens asserting an identity that no longer exists.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_EndsEverySessionTheAccountHeld()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().Equal(new[] { UserId });
    }

    /// <summary>
    /// A deletion ERASES the account's session records, across every tenant when the account row itself
    /// goes.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// PRIV-02. REVOCATION IS NOT DELETION, AND ONLY THE FIRST USED TO HAPPEN. Ending the sessions STAMPS
    /// each record so it can no longer be redeemed, which is right for a sign-out because the stamped
    /// record is what makes a later replay of that family recognisable.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_ErasesTheSessionRecordsOfAnAccountItRemovedOutright()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().Equal(
            new[] { UserId },
            "the sessions are ended before anything is removed, exactly as they were");
        harness.PurgedSessionScopes.Should().Equal(
            new[] { (UserId, (int?)null) },
            "and the records are then ERASED across every tenant, because the account row itself has gone");
    }

    /// <summary>A deletion that only removes a MEMBERSHIP erases the session records of that tenant alone.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// PRIV-02. THE SCOPE IS THE WHOLE POINT OF THIS FACT. The account survives because it still belongs to
    /// another tenant, and the sessions it holds there are legitimate - so an erasure that reached every
    /// tenant would sign it out of a tenant it is still a member of, turning a data-retention fix into an
    /// availability defect.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_ErasesOnlyTheDepartedTenantsRecordsForAnAccountItKeeps()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 2, UserId = UserId, PortalId = PortalId + 5 });
        harness.Membership = harness.LookupUser!.UserPortals.First();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.RemovedUsers.Should().BeEmpty("the account belongs to another tenant and is retained");
        harness.PurgedSessionScopes.Should().Equal(
            new[] { (UserId, (int?)PortalId) },
            "so only the records scoped to the tenant it left may be erased");
    }

    /// <summary>
    /// A deletion whose session records cannot be erased still succeeds, and records that the erasure is
    /// owed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// PRIV-02. THE ASYMMETRY WITH THE REVOCATION ABOVE IS DELIBERATE, AND BOTH DIRECTIONS ARE ASSERTED IN
    /// THIS SUITE. A revocation that fails abandons the deletion, because nothing has been removed yet and
    /// the account can be left whole.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheSessionRecordsCannotBeErased_StillSucceedsAndRecordsTheOmission()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.SessionRecordsErased = false;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "the account has already been deleted, and no retry can un-delete it");
        harness.RemovedUsers.Should().ContainSingle();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_DELETED");
        record.Properties["SessionRecordsErased"].Should().Be(
            "False",
            "an operator has to be able to see that the erasure is still owed");
    }

    /// <summary>A deletion whose session records ARE erased says so on its audit record.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_RecordsThatTheSessionRecordsWereErased()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();

        await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_DELETED");
        record.Properties["SessionRecordsErased"].Should().Be("True");
    }

    /// <summary>A deletion whose sessions cannot be ended removes nothing at all.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The revocation is the first destructive step precisely so that this is possible: every guard has
    /// passed, so the deletion was going to be attempted, and nothing has yet been removed, so a refusal
    /// leaves the account wholly intact rather than half dismantled. Placing it after the cascade would
    /// mean reporting failure over an account whose grants, assignments and credential had already gone.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheSessionsCannotBeEnded_RemovesNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.SessionsRevoked = false;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(SessionRevocationFailedCode);
        harness.CascadedUserPermissions.Should().BeEmpty();
        harness.RemovedAssignments.Should().BeEmpty();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A deletion whose permission cascade refuses removes nothing at all, and reports the cascade's own
    /// reason rather than a reason of its own.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_WhenThePermissionCascadeRefuses_RemovesNothingAndPropagatesTheReason()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.UserAssignments.Add(new UserRole { UserRoleId = 7, UserId = UserId, RoleId = 5 });
        harness.CascadeResult = Result.Failure("permission.portal_not_found", "Portal does not exist.");

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("permission.portal_not_found");

        // The cascade was asked, and asked for exactly this account, before it refused.
        harness.CascadedUserPermissions.Should().Equal(new[] { (PortalId, UserId) });

        // Nothing after the cascade ran, and nothing was committed.
        harness.RemovedAssignments.Should().BeEmpty();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // No cache entry was evicted and no audit record was written for a deletion that did not happen.
        harness.InvalidatedPortalIds.Should().BeEmpty();
        harness.InvalidatedUsers.Should().BeEmpty();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A deletion whose credential removal is refused leaves the account intact and commits nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The credential is the one write in the cascade that leaves the unit of work, so it is the one whose
    /// refusal cannot be undone by simply not committing. Discarding its answer made an unreachable
    /// membership store look exactly like a completed removal, and the account row was then removed anyway
    /// - leaving a credential no administrative screen can reach and no later deletion will revisit.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheCredentialCannotBeRemoved_LeavesTheAccountIntact()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.CredentialRemoval = MembershipWriteOutcome.StoreUnavailable;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CredentialRemovalFailedCode);
        harness.RemovedUsers.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Nothing is committed and the scope is disposed, so the grant cascade the permission service
        // staged earlier in this same transaction is rolled back with everything else.
        harness.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never());
        harness.Transaction.Verify(t => t.DisposeAsync(), Times.Once());
    }

    /// <summary>The whole deletion cascade is published by ONE commit inside ONE transaction.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Four stores are written - both grant tables through the permission contract, the assignment table,
    /// the membership table and the account row through the repositories, and the external credential store
    /// - and the contract promises all or none. The assertion that makes that true is this one: exactly one
    /// transaction, exactly one flush inside it, and exactly one commit.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_PublishesTheWholeCascadeInOneTransaction()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.CascadedUserPermissions.Should().ContainSingle();
        harness.DeletedCredentialUserIds.Should().ContainSingle();
        harness.RemovedUsers.Should().ContainSingle();

        harness.UnitOfWork.Verify(
            u => u.BeginTransactionAsync(TransactionIsolation.Default, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
        harness.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once());
        harness.Transaction.Verify(t => t.DisposeAsync(), Times.Once());
    }

    /// <summary>
    /// The whole deletion cascade runs inside exactly one transaction, which is committed exactly once, and
    /// its permission step is STAGED into that transaction rather than committed on its own.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// So the oracle is threefold and each part is load-bearing: exactly one scope is opened, it is
    /// committed exactly once, and the permission contract is asked for STAGING - never for the committing
    /// sibling, which the unit of work would in any case refuse to nest a scope inside.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_RunsTheWholeCascadeInOneTransactionAndStagesTheGrantRemoval()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.UserAssignments.Add(new UserRole { UserRoleId = 7, UserId = UserId, RoleId = 5 });

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());

        harness.TransactionsOpened.Should().Be(1, "the five writes are one logical unit of work");
        harness.TransactionsCommitted.Should().Be(1);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // The default isolation is correct here: the sequence is atomic-or-nothing but depends on nothing it
        // read staying unchanged, so a serialisable scope would serialise deletions for no benefit.
        harness.UnitOfWork.Verify(
            u => u.BeginTransactionAsync(TransactionIsolation.Default, It.IsAny<CancellationToken>()),
            Times.Once);

        // Staging, never the committing sibling.
        harness.Permissions.Verify(
            p => p.StageUserPermissionRemovalAsync(PortalId, UserId, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Permissions.Verify(
            p => p.DeleteUserPermissionsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The grant-cache eviction the permission contract owns is performed by this service, because this
        // service owned the commit - and only after it.
        harness.EvictedGrantCaches.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// A credential the store answers is ALREADY ABSENT does not abandon the deletion: the cascade completes,
    /// the account row is removed and the deletion is reported as the success it is.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// THE COMPANION TO THE TWO FACTS BELOW, AND THE REASON THIS OUTCOME EXISTS SEPARATELY FROM THEM. An
    /// unreachable store abandons the cascade because nothing is known; a store that answers and holds no
    /// credential has told us the very state this step is trying to produce. Collapsed into one boolean, the
    /// second was answered as the first - so two administrators deleting one account at the same moment had
    /// the loser told the credential store was unavailable and that the account "was left intact", while the
    /// account had already been removed.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheCredentialIsAlreadyAbsent_CompletesTheDeletion()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.CredentialRemoval = MembershipWriteOutcome.NoRecord;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "the credential is gone and the account row was removed, which is the whole of what the caller "
            + "asked for");

        harness.DeletedCredentialUserIds.Should().Equal(new[] { UserId }, "the store was still asked");
        harness.RemovedUsers.Select(removed => removed.UserId).Should().Equal(new[] { UserId });
        harness.TransactionsCommitted.Should().Be(1);
        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(AuditEventNames.UserDeleted);
    }

    /// <summary>
    /// A deletion whose own removals affect no rows - because another caller removed the account first - is
    /// reported as a not-found account rather than as a conflict or a fault.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The answer is deliberately the SAME reason the pre-flight guard reports for an account that is already
    /// gone, so a second deletion is told the same thing whether it arrives a moment after the first or a day
    /// after it. A conflict would invite a retry with fresh state, and there is no fresh state to retry
    /// against; a fault would describe a server that is working correctly.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenAnotherCallerRemovedTheAccountFirst_ReportsItAsNotFound()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.CredentialRemoval = MembershipWriteOutcome.NoRecord;
        harness.CommitFault = new DbUpdateConcurrencyException();

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should().NotContainEquivalentOf(
            "left intact",
            "the account was NOT left intact - the winner removed it - and a message asserting otherwise is "
            + "the defect this fact exists to hold closed");

        harness.TransactionsCommitted.Should().Be(0);
        harness.AuditRecords.Should().BeEmpty("this request removed nothing, so it records nothing");
        harness.InvalidatedPortalIds.Should().BeEmpty();
        harness.EvictedGrantCaches.Should().BeEmpty();
    }

    /// <summary>
    /// A cascade abandoned at its last step opens a transaction and never commits it, so disposal rolls the
    /// staged removals back and nothing is evicted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The credential is the one write that leaves the mapped model, so its refusal is the sharpest case:
    /// the grants and assignments have already been staged by the time it answers.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheCredentialCannotBeRemoved_RollsTheTransactionBackAndEvictsNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.UserAssignments.Add(new UserRole { UserRoleId = 7, UserId = UserId, RoleId = 5 });
        harness.CredentialRemoval = MembershipWriteOutcome.StoreUnavailable;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CredentialRemovalFailedCode);

        // The grant removal WAS staged - the cascade got that far - and is undone by the rollback rather than
        // by a compensation routine.
        harness.CascadedUserPermissions.Should().Equal(new[] { (PortalId, UserId) });
        harness.TransactionsOpened.Should().Be(1);
        harness.TransactionsCommitted.Should().Be(0, "an abandoned cascade must not commit any part of itself");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Transaction.Verify(t => t.DisposeAsync(), Times.Once);

        // Nothing describes a deletion that did not happen.
        harness.EvictedGrantCaches.Should().BeEmpty();
        harness.InvalidatedPortalIds.Should().BeEmpty();
        harness.InvalidatedUsers.Should().BeEmpty();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// Requiring a credential change ends no session, which is a deliberate omission rather than an
    /// oversight.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This operation demands a new credential at the next sign-in without replacing the current one, so
    /// nothing an existing session holds has been invalidated and the account may still sign in.
    /// </remarks>
    [Fact]
    public async Task RequirePasswordChange_EndsNoSession()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service
            .RequirePasswordChangeAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().BeEmpty();
    }

    /// <summary>Setting approval refuses an unknown account and an account that holds no credential.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SetUserApproval_RefusesAnUnknownAccountAndANoCredentialAccount()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = null;

        Result unknown = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, true, CancellationToken.None);
        unknown.Reason!.Code.Should().Be(NotFoundCode);
        unknown.Reason!.Message.Should().Be($"Account {UserId} does not exist in portal {PortalId}.");

        harness.LookupUser = StoredUser();
        harness.CredentialExists = false;

        Result noCredential = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, true, CancellationToken.None);
        noCredential.Reason!.Code.Should().Be(NotFoundCode);
        noCredential.Reason!.Message.Should().Be($"Account {UserId} holds no credential.");
    }

    /// <summary>A store that reports the account away between the read and the write is reported as absent.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SetUserApproval_ReportsAnAccountTheStoreLosesBetweenReadAndWrite()
    {
        Harness harness = Harness.Ready();
        harness.CredentialApproved = false;
        harness.ApprovalSucceeded = false;

        Result outcome = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, true, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
    }

    /// <summary>
    /// A successful approval transition corrects the loaded row in the direction asked for, in both
    /// directions, and commits nothing.
    /// </summary>
    /// <param name="requested">The approval state to request.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetUserApproval_CorrectsTheLoadedRowWithoutCommitting(bool requested)
    {
        Harness harness = Harness.Ready();
        harness.CredentialApproved = !requested;
        harness.LookupUser!.IsApproved = !requested;

        Result outcome = await harness.Service
            .SetUserApprovalAsync(PortalId, UserId, requested, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupUser!.IsApproved.Should().Be(requested);
        harness.ApprovalWrites.Should().Equal(new[] { (UserId, requested) });
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Requiring a credential change refuses an unknown account, refuses an account already under the
    /// requirement, and otherwise records it and commits — because this one lives on the tracked row rather
    /// than in the credential store.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequirePasswordChange_RefusesTheUnknownAndTheAlreadyRequiredAndOtherwiseCommits()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = null;

        Result unknown = await harness.Service
            .RequirePasswordChangeAsync(PortalId, UserId, CancellationToken.None);
        unknown.Reason!.Code.Should().Be(NotFoundCode);

        harness.LookupUser = StoredUser();
        harness.LookupUser.UpdatePassword = true;

        Result already = await harness.Service
            .RequirePasswordChangeAsync(PortalId, UserId, CancellationToken.None);
        already.Reason!.Code.Should().Be(PasswordChangeAlreadyRequiredCode);
        already.Reason!.Message.Should()
            .Be($"Account {UserId} is already required to change its credential.");

        harness.LookupUser.UpdatePassword = false;

        Result applied = await harness.Service
            .RequirePasswordChangeAsync(PortalId, UserId, CancellationToken.None);

        applied.IsSuccess.Should().BeTrue();
        harness.LookupUser.UpdatePassword.Should().BeTrue();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
    }

    /// <summary>
    /// A tenant with no account-management module instance reads the legacy defaults, marked as unstored,
    /// rather than reporting an absence - because a tenant is allowed not to have one and its settings
    /// still apply.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetMembershipSettings_ReadsTheDefaultsWhenTheTenantHasNoAccountModule()
    {
        Harness harness = Harness.Ready();

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull();
        outcome.Value!.IsStored.Should().BeFalse("nothing is stored for a tenant with no settings source");
        outcome.Value.ColumnDisplayName.Should().BeTrue("the measured legacy default applies");
        outcome.Value.RecordsPerPage.Should().Be(10, "the measured legacy default applies");
    }

    /// <summary>
    /// A module instance that carries no settings yields the shipped defaults, so a tenant that has never
    /// been configured still reads a usable set.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetMembershipSettings_YieldsTheShippedDefaultsWhenNothingWasStored()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        MembershipSettingsDto settings = outcome.Value!;
        settings.Should().NotBeNull();
        settings.ColumnFirstName.Should().BeFalse();
        settings.ColumnDisplayName.Should().BeTrue();
        settings.ColumnAddress.Should().BeTrue();
        settings.ColumnTelephone.Should().BeTrue();
        settings.ColumnCreatedDate.Should().BeTrue();
        settings.ColumnAuthorized.Should().BeTrue();
        settings.DisplayMode.Should().Be(2);
        settings.RecordsPerPage.Should().Be(10);
        settings.ProfileDefaultVisibility.Should().Be(2);
        settings.ProfileDisplayVisibility.Should().BeTrue();
        settings.ProfileManageServices.Should().BeTrue();
        settings.SecurityRequireValidProfileAtLogin.Should().BeTrue();
        settings.SecurityEmailValidation.Should().Be(MembershipSettingsDto.DefaultEmailValidationExpression);
        settings.SecurityDisplayNameFormat.Should().BeEmpty();
    }

    /// <summary>
    /// Stored settings are read back, matched without regard to case, and an unreadable value falls back to
    /// the shipped default rather than becoming a zero by accident.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetMembershipSettings_ReadsStoredValuesAndFallsBackOnUnreadableOnes()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("column_firstname", "True");
        harness.StoreSetting("Records_PerPage", "not a number");
        harness.StoreSetting("Display_Mode", "1");
        harness.StoreSetting("Column_DisplayName", "nonsense");

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        outcome.Value!.ColumnFirstName.Should().BeTrue();
        outcome.Value.RecordsPerPage.Should().Be(10);
        outcome.Value.DisplayMode.Should().Be(1);
        outcome.Value.ColumnDisplayName.Should().BeTrue();
    }

    /// <summary>
    /// A redirect page recorded as the out-of-range sentinel is read back as no redirect at all, which is
    /// how the legacy screen recorded "no page chosen".
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetMembershipSettings_TreatsTheOutOfRangeRedirectSentinelAsNoRedirect()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Redirect_AfterLogin", "-1");
        harness.StoreSetting("Redirect_AfterRegistration", "17");

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        outcome.Value!.RedirectAfterLogin.Should().BeNull();
        outcome.Value.RedirectAfterRegistration.Should().Be(17);
        outcome.Value.RedirectAfterLogout.Should().BeNull();
    }

    /// <summary>
    /// When no listing style was configured, one is chosen from the size of the tenant, because a
    /// thousand-account tenant cannot be browsed the way a ten-account tenant can.
    /// </summary>
    /// <param name="accounts">How many accounts the tenant holds.</param>
    /// <param name="expected">The listing style the service is measured to choose.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 0)]
    [InlineData(1001, 1)]
    public async Task GetMembershipSettings_ChoosesAListingStyleFromTheSizeOfTheTenant(int accounts, int expected)
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.UserCount = accounts;

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        outcome.Value!.SecurityUsersControl.Should().Be(expected);
    }

    /// <summary>
    /// A configured listing style wins over the size of the tenant, and the tenant is not counted at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetMembershipSettings_PrefersAConfiguredListingStyleOverTheTenantSize()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_UsersControl", "1");
        harness.UserCount = 1;

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        outcome.Value!.SecurityUsersControl.Should().Be(1);
        harness.Portals.Verify(
            p => p.CountUsersAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>C-03: a required declaration the account has not answered makes the profile incomplete.</summary>
    /// <remarks>
    /// This is <c>ProfileController.ValidateProfile</c>: the walk reports invalid at the first declaration
    /// that is required and whose value is empty. An account with NO stored answers at all is the case a
    /// newly created account presents, and it must be reported incomplete rather than complete.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_ReportsTrueWhenARequiredAnswerIsMissing()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        ProfilePropertyDefinition street = Definition(StreetPropertyId, "Street");
        street.IsRequired = true;
        harness.Definitions.Add(street);

        Result<bool> outcome = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeTrue();
    }

    /// <summary>
    /// C-03: a required declaration answered with whitespace is ANSWERED, and an empty answer is not.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_TreatsWhitespaceAsAnsweredAndEmptyAsUnanswered()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        ProfilePropertyDefinition street = Definition(StreetPropertyId, "Street");
        street.IsRequired = true;
        harness.Definitions.Add(street);
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "   ")];

        Result<bool> whitespace = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        whitespace.IsSuccess.Should().BeTrue();
        whitespace.Value.Should().BeFalse(
            "the legacy compared the answer against the empty string, and a space is not the empty string");

        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, string.Empty)];

        Result<bool> empty = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        empty.IsSuccess.Should().BeTrue();
        empty.Value.Should().BeTrue("an empty answer is exactly what the legacy rule refused");
    }

    /// <summary>C-03: an answered required declaration makes the profile complete.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_ReportsFalseWhenEveryRequiredAnswerIsPresent()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        ProfilePropertyDefinition street = Definition(StreetPropertyId, "Street");
        street.IsRequired = true;
        harness.Definitions.Add(street);
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "Fleet Street")];

        Result<bool> outcome = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value.Should().BeFalse();
    }

    /// <summary>C-03: an unanswered declaration that is NOT required leaves the profile complete.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_IgnoresDeclarationsThatAreNotRequired()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.Definitions.Add(Definition(StreetPropertyId, "Street"));

        Result<bool> outcome = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value.Should().BeFalse();
        harness.Profiles.Verify(
            p => p.GetProfileValuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "with nothing required there is nothing the stored answers could change");
    }

    /// <summary>
    /// C-03: a tenant that does not require a valid profile at sign-in is not asked about completeness at
    /// all.
    /// </summary>
    /// <remarks>
    /// The setting is consulted FIRST and short-circuits, which is what keeps a tenant that has switched
    /// the gate off from paying for the declaration read on every sign-in.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_ShortCircuitsWhenTheTenantDoesNotRequireIt()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_RequireValidProfileAtLogin", bool.FalseString);
        ProfilePropertyDefinition street = Definition(StreetPropertyId, "Street");
        street.IsRequired = true;
        harness.Definitions.Add(street);

        Result<bool> outcome = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value.Should().BeFalse("the tenant has switched the gate off");
        harness.Profiles.Verify(
            p => p.GetDefinitionsByPortalIdAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the setting is consulted first and short-circuits");
    }

    /// <summary>
    /// C-03: a tenant with no settings source falls back to the measured default, which requires the
    /// profile.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_AppliesTheDefaultWhenTheTenantHasNoSettingsSource()
    {
        Harness harness = Harness.Ready();
        ProfilePropertyDefinition street = Definition(StreetPropertyId, "Street");
        street.IsRequired = true;
        harness.Definitions.Add(street);

        Result<bool> outcome = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value.Should().BeTrue();
        MembershipSettingsDto.DefaultRequireValidProfileAtLogin.Should().BeTrue(
            "the fallback above is only correct while the measured default is true");
    }

    /// <summary>
    /// Writing membership settings requires a payload, and is refused when the tenant has nowhere to store
    /// them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_RequiresAPayloadAndSomewhereToStoreIt()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateMembershipSettingsAsync(PortalId, null!, CancellationToken.None));

        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MembershipSettingsSourceMissingCode);
        outcome.Reason!.Message.Should().Be(
            $"Portal {PortalId} has no \"User Accounts\" module instance, so there is nowhere to store "
            + "membership settings. Add the \"User Accounts\" module to one of this portal's pages and try "
            + "again.",
            "the refusal has to name the repair, because the operator cannot infer it from the status");
    }

    /// <summary>
    /// Writing membership settings records the whole set against the module instance, adds the names that
    /// were absent, overwrites the ones that were present without regard to case, and discards both caches
    /// that could hold a stale answer.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_RecordsTheWholeSetAgainstTheModuleInstance()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("records_perpage", "10");
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = 12, PortalId = PortalId });

        // The redirect target has to be a page of THIS tenant, so the page lookup is stubbed for it. That
        // requirement is the point of the check: an identifier arriving over the wire cannot otherwise be
        // distinguished from one belonging to another portal.
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = 12, PortalId = PortalId, TabName = "Landing" });

        var settings = new UpdateMembershipSettingsRequest
        {
            ColumnFirstName = true,
            RecordsPerPage = 40,
            ProfileDefaultVisibility = 1,
            RedirectAfterLogin = 12,
            RedirectAfterLogout = null,
            SecurityUsersControl = 1,
            SecurityDisplayNameFormat = "[LASTNAME]",
        };

        Result outcome = await harness.Service
            .UpdateMembershipSettingsAsync(PortalId, settings, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ModuleSettingsFor(ModuleId)
            .Single(setting => string.Equals(setting.SettingName, "records_perpage", StringComparison.OrdinalIgnoreCase))
            .SettingValue.Should().Be("40");
        harness.AddedSettings.Should().NotContain(setting =>
            string.Equals(setting.SettingName, "Records_PerPage", StringComparison.OrdinalIgnoreCase));

        harness.AddedSettings.Should().HaveCount(22);
        harness.AddedSettings.Should().OnlyContain(setting => setting.ModuleId == ModuleId);
        SettingValue(harness, "Column_FirstName").Should().Be(bool.TrueString);
        SettingValue(harness, "Column_LastName").Should().Be(bool.FalseString);
        SettingValue(harness, "Profile_DefaultVisibility").Should().Be("1");
        SettingValue(harness, "Redirect_AfterLogin").Should().Be("12");
        SettingValue(harness, "Redirect_AfterLogout").Should().Be("-1");
        SettingValue(harness, "Security_UsersControl").Should().Be("1");
        SettingValue(harness, "Security_DisplayNameFormat").Should().Be("[LASTNAME]");

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.InvalidatedProfileDefinitionsPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// A redirect page is tenant data, not merely a syntactically valid integer. A page belonging to
    /// another portal receives the same non-enumerating answer as an unknown page, and no setting is
    /// staged.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_RefusesARedirectOutsideTheTenantBeforeWriting()
    {
        const int otherPortalId = 91;
        const int pageId = 12;

        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(pageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = pageId, PortalId = otherPortalId });

        // RE-POINTED FROM THE READ PROJECTION ONTO THE WRITE SHAPE. This fact was written against an
        // overload taking MembershipSettingsDto - the shape this surface RETURNS - while the write had
        // already been split onto UpdateMembershipSettingsRequest, which is the only shape an endpoint
        // binds.
        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest { RedirectAfterLogin = pageId },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MembershipSettingsRedirectNotInPortalCode);
        outcome.Reason.Message.Should().Be(
            $"{nameof(UpdateMembershipSettingsRequest.RedirectAfterLogin)} names page {pageId}, "
            + $"which does not belong to portal {PortalId}.");

        harness.AddedSettings.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.InvalidatedPortalIds.Should().BeEmpty();
        harness.InvalidatedProfileDefinitionsPortalIds.Should().BeEmpty();
    }

    /// <summary>
    /// Zero and minus one are looked up as page keys rather than reinterpreted as absence. Null alone means
    /// "no redirect" at the API boundary.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_ResolvesZeroAndMinusOneAsRealRedirectKeys()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = 0, PortalId = PortalId });
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(-1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = -1, PortalId = PortalId });

        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest
            {
                RedirectAfterLogin = 0,
                RedirectAfterRegistration = -1,
                RedirectAfterLogout = 0,
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        SettingValue(harness, "Redirect_AfterLogin").Should().Be("0");
        SettingValue(harness, "Redirect_AfterRegistration").Should().Be("-1");
        SettingValue(harness, "Redirect_AfterLogout").Should().Be("0");

        harness.Tabs.Verify(
            tabs => tabs.GetByIdAsync(0, It.IsAny<CancellationToken>()),
            Times.Once,
            "the duplicate login/logout target adds no information");
        harness.Tabs.Verify(
            tabs => tabs.GetByIdAsync(-1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A settings write ignores a module instance of another definition, so it cannot store the tenant's
    /// membership settings against an unrelated module.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_IgnoresAModuleInstanceOfAnotherDefinition()
    {
        Harness harness = Harness.Ready();
        harness.ModuleDefinitionCatalogue.Add(AccountsDefinition());
        harness.ModuleInstances.Add(new Module { ModuleId = 77, PortalId = PortalId, ModuleDefinitionId = 99 });

        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MembershipSettingsSourceMissingCode);
    }

    /// <summary>
    /// Every bounded membership setting is refused outside its range, and the refusal happens before the
    /// settings source is even located.
    /// </summary>
    /// <param name="mutate">Applies one out-of-range value to an otherwise valid request.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [MemberData(nameof(OutOfRangeMembershipSettings))]
    public async Task UpdateMembershipSettings_RefusesAValueOutsideItsRange(
        Action<UpdateMembershipSettingsRequest> mutate)
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        var request = new UpdateMembershipSettingsRequest();
        mutate(request);

        Result outcome = await harness.Service
            .UpdateMembershipSettingsAsync(PortalId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("user.membership-settings.invalid");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.AddedSettings.Should().BeEmpty();
    }

    /// <summary>
    /// A redirect member naming a page the tenant does not own is refused, and so is one naming no page at
    /// all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_RefusesARedirectPageTheTenantDoesNotOwn()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = 500, PortalId = PortalId + 1, TabName = "Another tenant's page" });

        Result foreignPage = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest { RedirectAfterLogin = 500 },
            CancellationToken.None);

        foreignPage.IsFailure.Should().BeTrue();
        foreignPage.Reason!.Code.Should().Be("user.membership-settings.redirect_not_in_portal");
        foreignPage.Reason!.Message.Should().Contain(nameof(UpdateMembershipSettingsRequest.RedirectAfterLogin));

        // A page that does not exist at all is refused identically, so absence and foreign ownership cannot
        // be distinguished by a caller probing for identifiers.
        Result unknownPage = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest { RedirectAfterLogout = 999 },
            CancellationToken.None);

        unknownPage.IsFailure.Should().BeTrue();
        unknownPage.Reason!.Code.Should().Be("user.membership-settings.redirect_not_in_portal");

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A null redirect member is the legitimate "no redirect" answer and is not looked up at all.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Absence is null and only null. The page identity seeds at ZERO, so zero is a legitimate page: a
    /// non-positive test would reject the first page of every portal, and the legacy minus-one marker is
    /// translated by the projection rather than accepted on the wire.
    /// </remarks>
    [Fact]
    public async Task UpdateMembershipSettings_TreatsAnAbsentRedirectAsNoRedirect()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest
            {
                RedirectAfterLogin = null,
                RedirectAfterRegistration = null,
                RedirectAfterLogout = null,
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.Tabs.Verify(
            tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
        SettingValue(harness, "Redirect_AfterLogin").Should().Be("-1");
    }

    /// <summary>
    /// An electronic-mail validation expression that cannot be compiled is refused rather than stored.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_RefusesAnUnusableEmailExpression()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest { SecurityEmailValidation = "([a-z" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("user.membership-settings.invalid");
        harness.AddedSettings.Should().BeEmpty();
    }

    /// <summary>
    /// Adopting a new display-name format rewrites every account in the tenant, and reports how many names
    /// it changed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Reproduces <c>Website/admin/Users/UserSettings.ascx.vb:L175-L182</c>, which compared the submitted
    /// format against the stored one and, when they differed, called
    /// <c>UserController.UpdateDisplayNames</c> to walk the tenant's accounts applying it.
    /// </remarks>
    [Fact]
    public async Task UpdateMembershipSettings_RewritesEveryAccountWhenTheFormatChanges()
    {
        var grace = StoredUser();
        var ada = StoredUser(OtherUserId, "ada");
        ada.FirstName = "Ada";
        ada.LastName = "Lovelace";
        ada.DisplayName = "Ada Lovelace";

        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.UserPage = PagedResult<User>.Unpaged([grace, ada]);

        Result<MembershipSettingsUpdateResultDto> outcome = await harness.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[LASTNAME], [FIRSTNAME]" },
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.DisplayNameFormatChanged.Should().BeTrue();
        outcome.Value.DisplayNamesRewritten.Should().Be(2);

        grace.DisplayName.Should().Be("Hopper, Grace");
        ada.DisplayName.Should().Be("Lovelace, Ada");

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once());
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username), (PortalId, "ada") });
    }

    /// <summary>
    /// Resubmitting an unchanged format sweeps nothing, and neither does clearing the format to blank.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The unchanged case is the legacy comparison: an operator who saves the settings screen without
    /// touching the format must not trigger a tenant-wide rewrite. The cleared case is a genuine change of
    /// policy - the tenant no longer formats names - but there is no format to apply, and the names already
    /// stored are what the accounts have; erasing them would destroy data the tenant never asked to lose.
    /// </remarks>
    [Fact]
    public async Task UpdateMembershipSettings_SweepsNothingForAnUnchangedOrClearedFormat()
    {
        Harness unchanged = Harness.Ready();
        unchanged.AddMembershipSettingsSource();
        unchanged.StoreSetting("Security_DisplayNameFormat", "[LASTNAME]");
        unchanged.UserPage = PagedResult<User>.Unpaged([StoredUser()]);

        Result<MembershipSettingsUpdateResultDto> resubmitted = await unchanged.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[LASTNAME]" },
                CancellationToken.None);

        resubmitted.IsSuccess.Should().BeTrue();
        resubmitted.Value!.DisplayNameFormatChanged.Should().BeFalse();
        resubmitted.Value.DisplayNamesRewritten.Should().Be(0);
        unchanged.InvalidatedUsers.Should().BeEmpty();

        Harness cleared = Harness.Ready();
        cleared.AddMembershipSettingsSource();
        cleared.StoreSetting("Security_DisplayNameFormat", "[LASTNAME]");
        User untouched = StoredUser();
        cleared.UserPage = PagedResult<User>.Unpaged([untouched]);

        Result<MembershipSettingsUpdateResultDto> blanked = await cleared.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = string.Empty },
                CancellationToken.None);

        blanked.IsSuccess.Should().BeTrue();
        blanked.Value!.DisplayNameFormatChanged.Should().BeTrue("the tenant did change its policy");
        blanked.Value.DisplayNamesRewritten.Should().Be(0, "there is no format left to apply");
        untouched.DisplayName.Should().Be("Grace B Hopper", "a stored name is not erased by clearing a format");
        cleared.InvalidatedUsers.Should().BeEmpty();
    }

    /// <summary>The format comparison is ordinal, so a change of case is a change of format.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_ComparesTheFormatOrdinally()
    {
        User account = StoredUser();

        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Security_DisplayNameFormat", "[FIRSTNAME]");
        harness.UserPage = PagedResult<User>.Unpaged([account]);

        Result<MembershipSettingsUpdateResultDto> outcome = await harness.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[firstname]" },
                CancellationToken.None);

        outcome.Value!.DisplayNameFormatChanged.Should().BeTrue();
        outcome.Value.DisplayNamesRewritten.Should().Be(1);
        account.DisplayName.Should().Be("[firstname]", "an unrecognised token is not a token");
    }

    /// <summary>The reported number counts names that CHANGED, not accounts examined.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// An operator reads this number as "how much of my tenant did this alter". Counting accounts examined
    /// would report a tenant-wide change to an operator who caused none, which is worse than reporting
    /// nothing.
    /// </remarks>
    [Fact]
    public async Task UpdateMembershipSettings_CountsChangedNamesRatherThanAccountsExamined()
    {
        var alreadyFormatted = StoredUser();
        alreadyFormatted.DisplayName = "Hopper";
        var stale = StoredUser(OtherUserId, "ada");
        stale.LastName = "Lovelace";

        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.UserPage = PagedResult<User>.Unpaged([alreadyFormatted, stale]);

        Result<MembershipSettingsUpdateResultDto> outcome = await harness.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[LASTNAME]" },
                CancellationToken.None);

        outcome.Value!.DisplayNamesRewritten.Should().Be(1);
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, "ada") });
    }

    /// <summary>
    /// The sweep reads the tenant's accounts unpaged and WITHOUT host accounts, which is the population the
    /// legacy walk covered.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A host account belongs to no single tenant - it is read without a tenant scope
    /// (<c>03.03.03.SqlDataProvider:L74-L83</c> established the host scope as a SQL NULL portal) - so one
    /// tenant's presentation policy has no business rewriting its name. The read is unpaged because the
    /// sweep is tenant-wide by definition; paging it would only decide how many round trips it took.
    /// </remarks>
    [Fact]
    public async Task UpdateMembershipSettings_SweepsTheTenantsAccountsUnpagedAndExcludesHostAccounts()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser()]);

        await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[LASTNAME]" },
            CancellationToken.None);

        harness.Users.Verify(
            users => users.ListAsync(
                PortalId,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                true,
                false,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A format that would overflow the stored column for any one account refuses the WHOLE write: neither
    /// the policy nor any rewritten name is committed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Storing a policy that cannot be applied to every account would leave the tenant in a state where
    /// each subsequent edit of the offending account is refused by the same width guard - a policy that
    /// silently breaks the accounts it governs. The failure names the first account that overflows, which
    /// is the one an operator needs in order to understand the refusal.
    /// </remarks>
    [Fact]
    public async Task UpdateMembershipSettings_RefusesTheWholeWriteWhenTheFormatOverflowsForAnyAccount()
    {
        var fits = StoredUser();
        var overflows = StoredUser(OtherUserId, new string('u', 70));

        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.UserPage = PagedResult<User>.Unpaged([fits, overflows]);

        Result<MembershipSettingsUpdateResultDto> outcome = await harness.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[USERNAME][USERNAME]" },
                CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DisplayNameTooLongCode);
        outcome.Reason.Message.Should().Be(
            $"The submitted display-name format produces 140 characters for account {OtherUserId}; "
            + "the stored limit is 128.");

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.InvalidatedPortalIds.Should().BeEmpty();
        harness.InvalidatedUsers.Should().BeEmpty();
    }

    /// <summary>
    /// The policy and the sweep it causes share ONE transaction boundary, joined rather than begun so a
    /// caller that already opened a scope keeps a single one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateMembershipSettings_WritesThePolicyAndTheSweepInOneTransaction()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.UserPage = PagedResult<User>.Unpaged([StoredUser()]);

        Result<MembershipSettingsUpdateResultDto> outcome = await harness.Service
            .UpdateMembershipSettingsAsync(
                PortalId,
                new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = "[LASTNAME]" },
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.JoinOrBeginTransactionAsync(
                TransactionIsolation.Default,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.BeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The out-of-range cases exercised by <see
    /// cref="UpdateMembershipSettings_RefusesAValueOutsideItsRange"/>.
    /// </summary>
    /// <returns>One mutation per bounded member, above and below where both bounds exist.</returns>
    public static TheoryData<Action<UpdateMembershipSettingsRequest>> OutOfRangeMembershipSettings()
        => new()
        {
            request => request.DisplayMode = -1,
            request => request.DisplayMode = 3,
            request => request.ProfileDefaultVisibility = -1,
            request => request.ProfileDefaultVisibility = 3,
            request => request.SecurityUsersControl = -1,
            request => request.SecurityUsersControl = 2,
            request => request.RecordsPerPage = 0,
            request => request.RecordsPerPage = UpdateMembershipSettingsRequestValidator.MaximumRecordsPerPage + 1,
            request => request.SecurityDisplayNameFormat = new string('x', 2001),
            request => request.SecurityEmailValidation = new string('x', 2001),
            request => request.SecurityEmailValidation = string.Empty,
        };

    /// <summary>
    /// Reading a profile reports absence for an unknown account, and otherwise reports one entry per
    /// declared property whether or not the account recorded a value for it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfile_ReportsAbsenceOrOneEntryPerDeclaredProperty()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = null;

        Result<UserProfileDto?> absent = await harness.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        absent.IsSuccess.Should().BeTrue();
        absent.Value.Should().BeNull();

        harness.LookupUser = StoredUser();
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "Fleet Street", visibility: 0)];

        Result<UserProfileDto?> present = await harness.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        present.Value!.UserId.Should().Be(UserId);
        present.Value.Properties.Should().HaveCount(harness.Definitions.Count);

        UserProfileValueDto street = present.Value.Properties
            .Single(property => property.PropertyDefinitionId == StreetPropertyId);
        street.PropertyValue.Should().Be("Fleet Street");
        street.Visibility.Should().Be(0);
        street.Definition.PropertyName.Should().Be("Street");

        UserProfileValueDto city = present.Value.Properties
            .Single(property => property.PropertyDefinitionId == CityPropertyId);
        city.PropertyValue.Should().BeEmpty();
        city.LastUpdatedDate.Should().BeNull();
    }

    /// <summary>
    /// A property the account never recorded takes the tenant's configured default visibility rather than
    /// zero, because zero is a real visibility choice.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfile_TakesTheTenantsDefaultVisibilityForAnUnrecordedProperty()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Profile_DefaultVisibility", "1");

        Result<UserProfileDto?> outcome = await harness.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value!.Properties.Should().OnlyContain(property => property.Visibility == 1);
        outcome.Value.Properties.Should().OnlyContain(property => property.Definition.Visibility == 1);
    }

    /// <summary>
    /// The profile projection publishes the tenant's decision on whether the account holder may choose a
    /// per-property visibility, in both states, taking the enabled default when the tenant stored nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfile_PublishesTheTenantsVisibilityAffordanceDecision()
    {
        Harness storedOff = Harness.Ready();
        storedOff.AddMembershipSettingsSource();
        storedOff.StoreSetting("Profile_DisplayVisibility", "False");

        Result<UserProfileDto?> off = await storedOff.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        off.Value!.DisplayVisibilityEnabled.Should().BeFalse(
            "the tenant switched the affordance off and the account holder cannot read that anywhere else");

        Harness storedOn = Harness.Ready();
        storedOn.AddMembershipSettingsSource();
        storedOn.StoreSetting("Profile_DisplayVisibility", "True");

        Result<UserProfileDto?> on = await storedOn.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        on.Value!.DisplayVisibilityEnabled.Should().BeTrue();

        // NOTHING STORED AT ALL, which is the state most tenants are in. The fallback is the same
        // MembershipSettingsDto initialiser the settings endpoint itself falls back to, so the two readers
        // cannot disagree about a tenant that has configured nothing.
        Harness storedNothing = Harness.Ready();

        Result<UserProfileDto?> unstored = await storedNothing.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        unstored.Value!.DisplayVisibilityEnabled.Should().Be(
            new MembershipSettingsDto().ProfileDisplayVisibility,
            "an unconfigured tenant must read the same either way it is asked");
    }

    /// <summary>
    /// The visibility affordance decision and the default visibility that seeds an unfilled value are
    /// independent: one settings member does not stand in for the other.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfile_KeepsTheVisibilityAffordanceApartFromTheDefaultVisibility()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoreSetting("Profile_DefaultVisibility", "1");
        harness.StoreSetting("Profile_DisplayVisibility", "False");

        Result<UserProfileDto?> outcome = await harness.Service
            .GetProfileAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value!.DisplayVisibilityEnabled.Should().BeFalse();
        outcome.Value.Properties.Should().OnlyContain(
            property => property.Visibility == 1,
            "the default visibility is still applied even though the holder may not change it");
    }
    /// <summary>
    /// PRIV-01: the export composes the three things the installation holds about one account within one
    /// tenant - the account row, its profile and its role assignments - and stamps the instant it was
    /// taken.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportPersonalData_ComposesTheAccountTheProfileAndTheRoleAssignments()
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = UserId;
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "Fleet Street")];
        harness.AutoAssigned.Add(new Role { RoleId = 5, RoleName = "Registered Users" });
        harness.UserAssignments.Add(new UserRole
        {
            UserRoleId = 7,
            UserId = UserId,
            RoleId = 5,
            EffectiveDate = Now.AddDays(-30),
            ExpiryDate = Now.AddDays(30),
        });

        Result<UserPersonalDataExportDto?> outcome = await harness.Service
            .ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserPersonalDataExportDto export = outcome.Value!;

        export.GeneratedAtUtc.Should().Be(Now, "the instant is taken from the injected clock");
        export.PortalId.Should().Be(PortalId);
        export.UserId.Should().Be(UserId);
        export.Account.UserId.Should().Be(UserId);
        export.Account.Email.Should().Be(Email);
        export.Profile.Should().NotBeNull();
        export.Profile!.Properties
            .Single(property => property.PropertyDefinitionId == StreetPropertyId)
            .PropertyValue.Should().Be("Fleet Street");

        RoleMembershipDto assignment = export.RoleAssignments.Should().ContainSingle().Subject;
        assignment.UserRoleId.Should().Be(7);
        assignment.RoleId.Should().Be(5);
        assignment.RoleName.Should().Be("Registered Users");
        assignment.EffectiveDate.Should().Be(Now.AddDays(-30));
        assignment.ExpiryDate.Should().Be(Now.AddDays(30));
        assignment.Username.Should().Be(Username, "the subject's own account fields, taken from the account");
    }

    /// <summary>
    /// PRIV-01: an account the addressed tenant does not hold reports absence rather than an empty
    /// document, and nothing is recorded, because nothing was exported.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportPersonalData_ReportsAbsenceAndRecordsNothingForAnUnknownAccount()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser = null;

        Result<UserPersonalDataExportDto?> outcome = await harness.Service
            .ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// PRIV-01: an assignment whose role belongs to another tenant, or has been removed under it, keeps the
    /// assignment's own facts and reports no name. Dropping the row would understate what is held; naming
    /// the other tenant's role would disclose it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportPersonalData_KeepsAnAssignmentWhoseRoleThisTenantCannotName()
    {
        Harness harness = Harness.Ready();
        harness.UserAssignments.Add(new UserRole { UserRoleId = 9, UserId = UserId, RoleId = 4_242 });

        Result<UserPersonalDataExportDto?> outcome = await harness.Service
            .ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        RoleMembershipDto assignment = outcome.Value!.RoleAssignments.Should().ContainSingle().Subject;
        assignment.UserRoleId.Should().Be(9);
        assignment.RoleId.Should().Be(4_242);
        assignment.RoleName.Should().BeEmpty();
    }

    /// <summary>
    /// PRIV-01: the exported document carries a CLOSED set of members, and none of them is a secret.
    /// </summary>
    /// <returns>Nothing; this is a static shape assertion.</returns>
    /// <remarks>
    /// An allowlist rather than a substring search, because the account projection legitimately carries
    /// <c>MustChangePassword</c> and <c>LastPasswordChangeDate</c> - facts ABOUT a credential that disclose
    /// nothing OF it - so a rule keyed on the word would reject the correct shape and would have to be
    /// weakened until it caught nothing.
    /// </remarks>
    [Fact]
    public void ExportPersonalData_CarriesAClosedSetOfMembersAndNoSecret()
    {
        typeof(UserPersonalDataExportDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
                "GeneratedAtUtc",
                "PortalId",
                "UserId",
                "Account",
                "Profile",
                "RoleAssignments");

        string[] forbidden =
        [
            "Password",
            "PasswordHash",
            "PasswordSalt",
            "PasswordAnswer",
            "PasswordQuestion",
            "Hash",
            "Salt",
            "Secret",
            "Token",
            "RefreshToken",
            "AccessToken",
        ];

        Type[] exported =
        [
            typeof(UserPersonalDataExportDto),
            typeof(UserDetailDto),
            typeof(UserProfileDto),
            typeof(UserProfileValueDto),
            typeof(RoleMembershipDto),
        ];

        foreach (Type type in exported)
        {
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Should().NotIntersectWith(
                    forbidden,
                    "{0} is exported to the subject and must carry no secret",
                    type.Name);
        }
    }

    /// <summary>
    /// PRIV-01: the export is recorded as COUNTS AND AN ACTOR, never as values. The audit sink is retained
    /// independently of the data it describes, so copying the exported values into it would duplicate the
    /// subject's personal data into a second store every time the subject asked for their own.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportPersonalData_RecordsCountsAndTheActorButNoExportedValue()
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = UserId;
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "Fleet Street")];
        harness.AutoAssigned.Add(new Role { RoleId = 5, RoleName = "Registered Users" });
        harness.UserAssignments.Add(new UserRole { UserRoleId = 7, UserId = UserId, RoleId = 5 });

        Result<UserPersonalDataExportDto?> outcome = await harness.Service
            .ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_DATA_EXPORTED");
        record.PortalId.Should().Be(PortalId);
        record.SubjectUserId.Should().Be(UserId);
        record.ActorUserId.Should().Be(UserId);

        record.Properties.Should().ContainKey("ProfileValues")
            .WhoseValue.Should().Be(outcome.Value!.Profile!.Properties.Count.ToString(CultureInfo.InvariantCulture));
        record.Properties.Should().ContainKey("RoleAssignments").WhoseValue.Should().Be("1");
        record.Properties.Should().ContainKey("SelfService").WhoseValue.Should().Be(bool.TrueString);

        // No exported VALUE reached the sink. Each of these is present in the document above.
        record.Properties.Values.Should().NotContain(Email);
        record.Properties.Values.Should().NotContain(Username);
        record.Properties.Values.Should().NotContain("Fleet Street");
        record.Properties.Values.Should().NotContain("Registered Users");
    }

    /// <summary>
    /// PRIV-01: an export an administrator takes over somebody else's account and one the account holder
    /// takes over their own are different events, and after the fact nothing but this property
    /// distinguishes them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportPersonalData_DistinguishesASelfServiceExportFromAnAdministrativeOne()
    {
        Harness administrative = Harness.Ready();
        administrative.CallerUserId = OtherUserId;

        await administrative.Service.ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        AuditEvent byAdministrator = administrative.AuditRecords.Should().ContainSingle().Subject;
        byAdministrator.Properties["SelfService"].Should().Be(bool.FalseString);
        byAdministrator.ActorUserId.Should().Be(OtherUserId);
        byAdministrator.SubjectUserId.Should().Be(UserId);

        Harness selfService = Harness.Ready();
        selfService.CallerUserId = UserId;

        await selfService.Service.ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        selfService.AuditRecords.Should().ContainSingle().Subject
            .Properties["SelfService"].Should().Be(bool.TrueString);
    }

    /// <summary>
    /// PRIV-01: the export is tenant-scoped in every read it makes, so it cannot answer with another
    /// tenant's view of the same account.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportPersonalData_ReadsOnlyTheAddressedTenant()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ExportPersonalDataAsync(PortalId, UserId, CancellationToken.None);

        harness.Roles.Verify(
            roles => roles.GetUserRolesAsync(PortalId, UserId, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Roles.Verify(
            roles => roles.GetUserRolesAsync(
                It.Is<int>(portalId => portalId != PortalId),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Writing a profile requires a payload, and an unknown account is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_RequiresAPayloadAndRefusesAnUnknownAccount()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateProfileAsync(PortalId, UserId, null!, CancellationToken.None));

        harness.LookupUser = null;

        Result outcome = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            new UserProfileDto { UserId = UserId, Properties = Array.Empty<UserProfileValueDto>() },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
    }

    /// <summary>
    /// A property the tenant does not declare is refused by identifier, so a submission cannot create a
    /// value against a definition that does not exist.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_RefusesAnUndeclaredProperty()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((999, "anything")),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileUnknownPropertyCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} does not define profile property 999.");
        harness.AddedValues.Should().BeEmpty();
    }

    /// <summary>
    /// A value longer than the declared length is refused and names the bound; a declared length of nothing
    /// imposes no bound at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_RefusesAValueLongerThanTheDeclaredLength()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(StreetPropertyId).Length = 5;

        Result refused = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "much too long")),
            CancellationToken.None);

        refused.Reason!.Code.Should().Be(ProfileValueTooLongCode);
        refused.Reason!.Message.Should()
            .Be("Profile property \"Street\" accepts at most 5 characters.");

        harness.DefinitionFor(StreetPropertyId).Length = 0;

        Result permitted = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "much too long")),
            CancellationToken.None);

        permitted.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A declared format is applied to a submitted value, a rule that cannot be applied refuses the value,
    /// and an empty value is not format-checked at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_AppliesTheDeclaredFormatAndFailsClosedOnAnUnapplicableRule()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(TelephonePropertyId).ValidationExpression = @"^\d{3}-\d{4}$";

        Result malformed = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((TelephonePropertyId, "telephone")),
            CancellationToken.None);

        malformed.Reason!.Code.Should().Be(ProfilePropertyValidationFailedCode);
        malformed.Reason!.Message.Should()
            .Be("Profile property \"Telephone\" does not match the format it requires.");

        Result wellFormed = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((TelephonePropertyId, "555-0100")),
            CancellationToken.None);

        wellFormed.IsSuccess.Should().BeTrue();

        Result empty = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((TelephonePropertyId, string.Empty)),
            CancellationToken.None);

        empty.IsSuccess.Should().BeTrue();

        harness.DefinitionFor(TelephonePropertyId).ValidationExpression = "([";

        Result unapplicable = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((TelephonePropertyId, "555-0100")),
            CancellationToken.None);

        unapplicable.Reason!.Code.Should().Be(ProfilePropertyValidationFailedCode);
        unapplicable.Reason!.Message.Should().Be(
            "The validation rule declared for profile property \"Telephone\" could not be applied.");
    }

    /// <summary>
    /// A required property that was omitted or submitted empty is refused by name, so a profile cannot be
    /// saved incomplete.
    /// </summary>
    /// <param name="submittedValue">The value to submit, or null to omit the property entirely.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpdateProfile_RefusesAnOmittedOrEmptyRequiredProperty(string? submittedValue)
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(CityPropertyId).IsRequired = true;

        UserProfileDto profile = submittedValue is null
            ? Profile((StreetPropertyId, "Fleet Street"))
            : Profile((StreetPropertyId, "Fleet Street"), (CityPropertyId, submittedValue));

        Result outcome = await harness.Service
            .UpdateProfileAsync(PortalId, UserId, profile, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileRequiredPropertyMissingCode);
        outcome.Reason!.Message.Should().Be("Profile property \"City\" is required.");
        harness.AddedValues.Should().BeEmpty();
        harness.UpdatedValues.Should().BeEmpty();
    }

    /// <summary>
    /// Every refused property is reported, each under its own declared name, so a form can mark the exact
    /// controls at fault instead of showing one sentence beside none of them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// ⚠ THE REFUSAL WAS ALWAYS CORRECT; ITS SHAPE WAS NOT. Measured against the running API, a profile write
    /// carrying an empty required property and a malformed one was refused with the right status and the right
    /// sentence, but as a FLAT problem document naming no field - so the screen that sent it could mark no
    /// control invalid, set <c>aria-invalid</c> on nothing, and left focus on the document body. The flat
    /// <see cref="ResultReason.Code"/> and <see cref="ResultReason.Message"/> are deliberately unchanged, so
    /// every existing caller reads exactly what it read before; the per-field detail is ADDITIVE.
    /// </remarks>
    [Fact]
    public async Task UpdateProfile_ReportsEveryRefusedPropertyUnderItsOwnDeclaredName()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(CityPropertyId).IsRequired = true;
        harness.DefinitionFor(TelephonePropertyId).ValidationExpression = @"^\d{3}-\d{4}$";

        Result outcome = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile(
                (StreetPropertyId, "Fleet Street"),
                (TelephonePropertyId, "telephone"),
                (CityPropertyId, string.Empty)),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();

        IReadOnlyDictionary<string, IReadOnlyList<string>> fields = outcome.Reason!.FieldErrors!;

        // BOTH failures, not merely the first one encountered: a form told about one fault at a time makes
        // the operator submit once per fault to discover them all.
        fields.Keys.Should().BeEquivalentTo("Telephone", "City");
        fields["Telephone"].Should().ContainSingle()
            .Which.Should().Be("Profile property \"Telephone\" does not match the format it requires.");
        fields["City"].Should().ContainSingle()
            .Which.Should().Be("Profile property \"City\" is required.");

        // The declared property NAME is the key, because that is what the client identifies its controls by -
        // the definition id would name nothing the form could find.
        fields.Keys.Should().NotContain(TelephonePropertyId.ToString(CultureInfo.InvariantCulture));

        // Unchanged for every existing caller: the summary is still the first submitted failure.
        outcome.Reason!.Code.Should().Be(ProfilePropertyValidationFailedCode);
        harness.AddedValues.Should().BeEmpty();
        harness.UpdatedValues.Should().BeEmpty();
    }

    /// <summary>
    /// A required property answered with whitespace is accepted and stored exactly as submitted, matching
    /// the rule the sign-in completeness gate applies.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The two rules are asserted to agree because disagreement is harmful in EITHER direction. A stricter
    /// write rule refuses an answer that would have let the account sign in; a laxer one stores an answer
    /// that will lock the account out on its next sign-in, in front of a form that cannot show it why.
    /// </remarks>
    [Fact]
    public async Task UpdateProfile_AcceptsAWhitespaceAnswerForARequiredProperty()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(CityPropertyId).IsRequired = true;

        Result outcome = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "Fleet Street"), (CityPropertyId, "   ")),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedValues
            .Should().Contain(value => value.PropertyDefinitionId == CityPropertyId)
            .Which.PropertyValue.Should().Be(
                "   ",
                "the answer is stored exactly as submitted rather than normalised");
    }

    /// <summary>
    /// Writing a profile is a replacement rather than a merge: a submitted property is written, a property
    /// that was stored and not submitted is removed, and a submitted property that was never stored is
    /// added.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_ReplacesRatherThanMergesTheStoredValues()
    {
        Harness harness = Harness.Ready();
        harness.ValuesByUserId[UserId] =
        [
            Value(1, UserId, StreetPropertyId, "Old Street"),
            Value(2, UserId, CityPropertyId, "Oldtown"),
        ];

        Result outcome = await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "Fleet Street"), (TelephonePropertyId, "555-0100")),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ValuesByUserId[UserId]
            .Single(value => value.PropertyDefinitionId == StreetPropertyId)
            .PropertyValue.Should().Be("Fleet Street");
        harness.UpdatedValues.Select(value => value.PropertyDefinitionId)
            .Should().BeEquivalentTo(new[] { StreetPropertyId, CityPropertyId });
        harness.UpdatedValues.Single(value => value.PropertyDefinitionId == CityPropertyId)
            .PropertyValue.Should().BeEmpty();
        harness.AddedValues.Should().ContainSingle()
            .Which.PropertyDefinitionId.Should().Be(TelephonePropertyId);
        harness.AddedValues[0].UserId.Should().Be(UserId);
        harness.AddedValues[0].PropertyValue.Should().Be("555-0100");
        harness.AddedValues[0].LastUpdatedDate.Should().Be(Now);
    }

    /// <summary>
    /// A value that exceeds the column the store keeps values in is written to the overflow column instead,
    /// and the two are never both populated, because the effective value is read from one or the other.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_MovesAnOversizeValueToTheOverflowColumn()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(StreetPropertyId).Length = 0;
        string oversize = new string('x', 3751);

        await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, oversize)),
            CancellationToken.None);

        UserProfileValue written = harness.AddedValues.Should().ContainSingle().Which;
        written.PropertyValue.Should().BeNull();
        written.PropertyText.Should().Be(oversize);

        // The entity derives nothing, so the effective value is the coalesce the legacy read procedure
        // performs - the bounded column when it is not null, the overflow column otherwise. Asserted here
        // in that order to prove the row round-trips the whole value.
        (written.PropertyValue ?? written.PropertyText).Should().Be(oversize);
    }

    /// <summary>A value that exactly fills the column stays in it, so the boundary is inclusive.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_KeepsAValueThatExactlyFillsTheColumn()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(StreetPropertyId).Length = 0;
        string exact = new string('x', 3750);

        await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, exact)),
            CancellationToken.None);

        UserProfileValue written = harness.AddedValues.Should().ContainSingle().Which;
        written.PropertyValue.Should().Be(exact);
        written.PropertyText.Should().BeNull();
    }

    /// <summary>
    /// A stored value that overflowed and is resubmitted short enough is moved back into the ordinary
    /// column, and the overflow column is cleared, so the row never keeps two competing answers.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_MovesAnOverflowedValueBackWhenItBecomesShortEnough()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionFor(StreetPropertyId).Length = 0;
        var stored = Value(1, UserId, StreetPropertyId, null);
        stored.PropertyText = new string('x', 4000);
        harness.ValuesByUserId[UserId] = [stored];

        await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "Fleet Street")),
            CancellationToken.None);

        stored.PropertyValue.Should().Be("Fleet Street");
        stored.PropertyText.Should().BeNull();
        (stored.PropertyValue ?? stored.PropertyText).Should().Be("Fleet Street");
    }

    /// <summary>A successful profile write commits once and discards the account's cache.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_CommitsOnceAndDiscardsTheAccountCache()
    {
        Harness harness = Harness.Ready();

        await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "Fleet Street")),
            CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedUsers.Should().Equal(new[] { (PortalId, Username) });
    }

    /// <summary>Portal-scoped profile operations never call the installation-wide value reader.</summary>
    [Fact]
    public async Task ProfileOperations_ReadOnlyValuesOwnedByTheAddressedPortal()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetProfileAsync(PortalId, UserId, CancellationToken.None);
        await harness.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            Profile((StreetPropertyId, "Fleet Street")),
            CancellationToken.None);

        harness.Profiles.Verify(
            profiles => profiles.GetProfileValuesAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        harness.Profiles.Verify(
            profiles => profiles.GetProfileValuesAsync(
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Duplicate definitions and invalid visibility are refused before any value is staged.</summary>
    [Fact]
    public async Task UpdateProfile_RejectsDuplicateDefinitionsAndInvalidVisibility()
    {
        Harness duplicate = Harness.Ready();
        UserProfileDto repeated = Profile(
            (StreetPropertyId, "one"),
            (StreetPropertyId, "two"));

        Result duplicateOutcome = await duplicate.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            repeated,
            CancellationToken.None);

        duplicateOutcome.IsFailure.Should().BeTrue();
        duplicateOutcome.Error!.Code.Should().Be("user.profile.duplicate-property");
        duplicate.AddedValues.Should().BeEmpty();
        duplicate.UpdatedValues.Should().BeEmpty();

        Harness invalidVisibility = Harness.Ready();
        UserProfileDto invalid = Profile((StreetPropertyId, "one"));
        invalid.Properties[0].Visibility = 3;

        Result visibilityOutcome = await invalidVisibility.Service.UpdateProfileAsync(
            PortalId,
            UserId,
            invalid,
            CancellationToken.None);

        visibilityOutcome.IsFailure.Should().BeTrue();
        visibilityOutcome.Error!.Code.Should().Be("user.profile.visibility-invalid");
    }

    /// <summary>Membership redirects must name pages owned by the addressed portal.</summary>
    [Fact]
    public async Task UpdateMembershipSettings_RefusesAForeignRedirectPage()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();

        // The page exists but belongs to another tenant, which is the case a field rule cannot decide and
        // the one the legacy picker made unreachable by only ever offering the portal's own pages.
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(OtherPortalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tab { TabId = OtherPortalId, PortalId = OtherPortalId });

        Result outcome = await harness.Service.UpdateMembershipSettingsAsync(
            PortalId,
            new UpdateMembershipSettingsRequest { RedirectAfterLogin = OtherPortalId },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("user.membership-settings.redirect_not_in_portal");
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The configured email expression is enforced through the typed membership settings.</summary>
    [Fact]
    public async Task IsEmailValid_UsesThePortalExpressionAndFailsClosed()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoredModuleSettings[ModuleId].Add(new ModuleSetting
        {
            ModuleId = ModuleId,
            SettingName = "Security_EmailValidation",
            SettingValue = @"^[^@]+@example\.com$",
        });

        Result<bool> accepted = await harness.Service.IsEmailValidAsync(
            PortalId,
            "ada@example.com",
            CancellationToken.None);
        Result<bool> refused = await harness.Service.IsEmailValidAsync(
            PortalId,
            "ada@elsewhere.test",
            CancellationToken.None);

        accepted.Value.Should().BeTrue();
        refused.Value.Should().BeFalse();
    }

    /// <summary>The general valid-profile requirement is enforced even when the login-specific flag is off.</summary>
    [Fact]
    public async Task RequiresProfileCompletion_HonoursTheGeneralRequirement()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        harness.StoredModuleSettings[ModuleId].AddRange(
        [
            new ModuleSetting
            {
                ModuleId = ModuleId,
                SettingName = "Security_RequireValidProfile",
                SettingValue = bool.TrueString,
            },
            new ModuleSetting
            {
                ModuleId = ModuleId,
                SettingName = "Security_RequireValidProfileAtLogin",
                SettingValue = bool.FalseString,
            },
        ]);
        harness.DefinitionFor(StreetPropertyId).IsRequired = true;

        Result<bool> outcome = await harness.Service.RequiresProfileCompletionAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        outcome.Value.Should().BeTrue();
    }

    /// <summary>The published service the catalogue facts below are built around.</summary>
    private const int FreeServiceRoleId = 40;

    /// <summary>A published service that charges a recurring fee and offers a free trial.</summary>
    private const int PaidServiceRoleId = 41;

    /// <summary>A role the tenant does not publish, reachable only by invitation code.</summary>
    private const int PrivateServiceRoleId = 42;

    /// <summary>The invitation code the private role bears.</summary>
    private const string InvitationCode = "Founders-2026";

    /// <summary>
    /// A request for the WHOLE catalogue, used by the assertions that are about what the catalogue SAYS
    /// rather than about how much of it travels at once.
    /// </summary>
    /// <remarks>
    /// A page size of zero is the application layer's "unpaged" and is unreachable over HTTP, where the
    /// shared validator requires at least one row - so these assertions read every published service while
    /// no caller of the endpoint can.
    /// </remarks>
    private static MemberServicePagedRequest WholeCatalogue => new MemberServicePagedRequest { PageSize = 0 };

    /// <summary>
    /// The catalogue is the tenant's PUBLIC roles, whether or not the account holds them, and every row
    /// carries the three predicates the legacy grid bound.
    /// </summary>
    [Fact]
    public async Task ListMemberServices_PublishesEveryPublicRoleWithThisAccountsOwnState()
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();
        harness.PublishPaidServiceWithFreeTrial();

        Result<PagedResult<MemberServiceDto>> outcome = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Items.Select(row => row.RoleId).Should().Equal(FreeServiceRoleId, PaidServiceRoleId);
        outcome.Value.TotalCount.Should().Be(2, "the total is the whole published catalogue");

        MemberServiceDto free = outcome.Value.Items.Single(row => row.RoleId == FreeServiceRoleId);
        free.IsSubscribed.Should().BeFalse();
        free.SubscriptionAction.Should().Be(MemberServiceActions.Subscribe);
        free.SubscriptionOffered.Should().BeTrue("a free public service was always offered");
        free.SubscriptionRequiresPayment.Should().BeFalse();
        free.TrialOffered.Should().BeFalse("ShowTrial's first arm returns false for a free service");
        free.ExpiryDate.Should().BeNull();
        free.EffectiveDate.Should().BeNull();

        MemberServiceDto paid = outcome.Value.Items.Single(row => row.RoleId == PaidServiceRoleId);
        paid.SubscriptionRequiresPayment.Should().BeTrue();
        paid.TrialOffered.Should().BeTrue("the service fee is non-zero and the trial fee is zero");
    }

    /// <summary>
    /// The fee and trial values published are the role's OWN STORED VALUES, not the legacy projection's
    /// truncated suppression of them.
    /// </summary>
    /// <remarks>
    /// The terminal statement wrapped each in <c>case when convert(int, R.ServiceFee) &lt;&gt; 0 …</c>, and
    /// <c>convert(int, …)</c> truncates - so a service priced at 0.50 came back with a null fee and an
    /// empty frequency, which the screen rendered as "Free" while the subscription still refused to
    /// complete without payment.
    /// </remarks>
    [Fact]
    public async Task ListMemberServices_PublishesASubUnitFeeRatherThanSuppressingItAsFree()
    {
        Harness harness = Harness.Ready();
        Role fractional = harness.PublishPaidServiceWithFreeTrial();
        fractional.ServiceFee = 0.50m;

        Result<PagedResult<MemberServiceDto>> outcome = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);

        MemberServiceDto row = outcome.Value.Items.Single(entry => entry.RoleId == PaidServiceRoleId);
        row.ServiceFee.Should().Be(0.50m);
        row.BillingFrequency.Should().Be(BillingFrequency.Month);
        row.BillingPeriod.Should().Be(1);
        row.SubscriptionRequiresPayment.Should().BeTrue("a fee of 0.50 is still a fee");
    }

    /// <summary>
    /// The command label is the legacy <c>ServiceText</c> ladder: subscribe, unsubscribe, or renew once the
    /// subscription has lapsed.
    /// </summary>
    [Theory]
    [InlineData(false, null, MemberServiceActions.Subscribe)]
    [InlineData(true, null, MemberServiceActions.Unsubscribe)]
    [InlineData(true, 30, MemberServiceActions.Unsubscribe)]
    [InlineData(true, -1, MemberServiceActions.Renew)]
    public async Task ListMemberServices_ReportsTheLegacyCommandLadder(
        bool subscribed,
        int? expiryOffsetInDays,
        string expected)
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();

        if (subscribed)
        {
            harness.Subscribe(
                FreeServiceRoleId,
                expiryOffsetInDays is int offset ? Now.Date.AddDays(offset) : null);
        }

        Result<PagedResult<MemberServiceDto>> outcome = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);

        MemberServiceDto row = outcome.Value.Items.Single();
        row.IsSubscribed.Should().Be(subscribed);
        row.IsExpired.Should().Be(expected == MemberServiceActions.Renew);
        row.SubscriptionAction.Should().Be(expected);
    }

    /// <summary>
    /// A paid service is offered only when the tenant has a payment processor account, which is the second
    /// arm of the legacy subscribe predicate.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("merchant@example.com", true)]
    public async Task ListMemberServices_OffersAPaidServiceOnlyWhereTheTenantCanTakePayment(
        string? processorUserId,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.PublishPaidServiceWithFreeTrial();
        harness.PortalRow!.ProcessorUserId = processorUserId;

        Result<PagedResult<MemberServiceDto>> outcome = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);

        MemberServiceDto row = outcome.Value.Items.Single();
        row.SubscriptionOffered.Should().Be(expected);
        row.SubscriptionRequiresPayment.Should().BeTrue("the offer's terms do not change with the tenant's");
    }

    /// <summary>A trial already consumed is no longer offered.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ListMemberServices_WithdrawsATrialThisAccountHasAlreadyConsumed(
        bool? trialUsed,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.PublishPaidServiceWithFreeTrial();
        harness.Subscribe(PaidServiceRoleId, expiry: null, trialUsed: trialUsed);

        Result<PagedResult<MemberServiceDto>> outcome = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);

        MemberServiceDto row = outcome.Value.Items.Single();
        row.IsTrialUsed.Should().Be(trialUsed ?? false);
        row.TrialOffered.Should().Be(expected);
    }

    /// <summary>A tenant publishing no services answers an EMPTY catalogue rather than a refusal.</summary>
    [Fact]
    public async Task ListMemberServices_AnswersAnEmptyCatalogueRatherThanAnAbsentOne()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<MemberServiceDto>> outcome = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Items.Should().BeEmpty();
        outcome.Value.TotalCount.Should().Be(0, "an empty catalogue reports a total of none, not an absent total");
    }

    /// <summary>
    /// The tenant's own switch refuses every one of the five operations, and it defaults to ENABLED.
    /// </summary>
    [Fact]
    public async Task MemberServices_AreRefusedEntirelyWhenTheTenantHasSwitchedThemOff()
    {
        Harness enabled = Harness.Ready();
        enabled.PublishFreeService();
        enabled.AddMembershipSettingsSource();

        (await enabled.Service.ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None))
            .IsSuccess.Should().BeTrue("the stored default is enabled");

        Harness noModule = Harness.Ready();
        noModule.PublishFreeService();

        (await noModule.Service.ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None))
            .IsSuccess.Should().BeTrue("a tenant with no account module reads as enabled, not as broken");

        Harness disabled = Harness.Ready();
        disabled.PublishFreeService();
        disabled.AddMembershipSettingsSource();
        disabled.StoreSetting("Profile_ManageServices", bool.FalseString);

        (await disabled.Service.ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceDisabledCode);
        (await disabled.Service.SubscribeToServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceDisabledCode);
        (await disabled.Service.CancelServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceDisabledCode);
        (await disabled.Service.StartServiceTrialAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceDisabledCode);
        (await disabled.Service.RedeemServiceCodeAsync(
                PortalId,
                UserId,
                new RedeemServiceCodeRequest { Code = InvitationCode },
                CancellationToken.None))
            .Error!.Code.Should().Be(ServiceDisabledCode);

        disabled.DelegatedAssignments.Should().BeEmpty("a refused operation must reach no write");
        disabled.DelegatedRemovals.Should().BeEmpty();
    }

    /// <summary>An unknown tenant and an unknown account are each reported as absent.</summary>
    [Fact]
    public async Task MemberServices_ReportAnUnknownTenantAndAnUnknownAccountSeparately()
    {
        Harness noTenant = Harness.Ready();
        noTenant.PortalRow = null;

        (await noTenant.Service.ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None))
            .Error!.Code.Should().Be("portal.not_found");

        Harness noAccount = Harness.Ready();
        noAccount.LookupUser = null;

        (await noAccount.Service.ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None))
            .Error!.Code.Should().Be(NotFoundCode);
    }

    /// <summary>
    /// Subscribing delegates the write, and submits NO date, so the role service's own derivation governs
    /// both bounds.
    /// </summary>
    [Fact]
    public async Task SubscribeToService_DelegatesTheAssignmentAndSubmitsNoBound()
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();

        Result outcome = await harness.Service
            .SubscribeToServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.DelegatedAssignments.Should().HaveCount(1);
        (int portalId, int roleId, RoleAssignmentRequest request) = harness.DelegatedAssignments[0];
        portalId.Should().Be(PortalId);
        roleId.Should().Be(FreeServiceRoleId);
        request.UserId.Should().Be(UserId);
        request.EffectiveDate.Should().BeNull("the derivation runs only where the caller submitted nothing");
        request.ExpiryDate.Should().BeNull();
        request.NotifyUser.Should().BeFalse("the legacy panel sent no notification and the mail subsystem is excluded");
    }

    /// <summary>Renewing a lapsed subscription is the SAME operation, reached at the same address.</summary>
    [Fact]
    public async Task SubscribeToService_RenewsALapsedSubscriptionThroughTheSameOperation()
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();
        harness.Subscribe(FreeServiceRoleId, Now.Date.AddDays(-1));

        Result<PagedResult<MemberServiceDto>> before = await harness.Service
            .ListMemberServicesAsync(PortalId, UserId, WholeCatalogue, CancellationToken.None);
        before.Value.Items.Single().SubscriptionAction.Should().Be(MemberServiceActions.Renew);

        Result outcome = await harness.Service
            .SubscribeToServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.DelegatedAssignments.Should().HaveCount(1);
    }

    /// <summary>
    /// A service that charges a fee is refused on BOTH the subscribe and the cancel path, because the
    /// legacy cancel path shared the subscribe gate.
    /// </summary>
    [Fact]
    public async Task SubscribeAndCancel_RefuseAServiceThatWouldRequirePayment()
    {
        Harness harness = Harness.Ready();
        harness.PublishPaidServiceWithFreeTrial();
        harness.Subscribe(PaidServiceRoleId, expiry: null);

        (await harness.Service.SubscribeToServiceAsync(PortalId, UserId, PaidServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServicePaymentRequiredCode);
        (await harness.Service.CancelServiceAsync(PortalId, UserId, PaidServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServicePaymentRequiredCode);

        harness.DelegatedAssignments.Should().BeEmpty();
        harness.DelegatedRemovals.Should().BeEmpty();
    }

    /// <summary>An ABSENT fee is read as no fee, which the legacy comparison could not do.</summary>
    [Fact]
    public async Task SubscribeToService_TreatsAnAbsentFeeAsNoFee()
    {
        Harness harness = Harness.Ready();
        Role role = harness.PublishFreeService();
        role.ServiceFee = null;

        Result outcome = await harness.Service
            .SubscribeToServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.DelegatedAssignments.Should().HaveCount(1);
    }

    /// <summary>
    /// A role the tenant does not publish is refused for self-service, and an unknown role is reported as
    /// absent rather than as unpublished.
    /// </summary>
    /// <remarks>
    /// The first arm of both legacy gates is <c>objRole.IsPublic</c> (<c>:L105</c> and <c>:L124</c>).
    /// Answering "not published" for a role of another tenant would confirm that the identifier exists
    /// somewhere, so an unresolvable role is absent.
    /// </remarks>
    [Fact]
    public async Task MemberServiceWrites_SeparateAnUnpublishedRoleFromAnUnknownOne()
    {
        Harness harness = Harness.Ready();
        harness.PublishPrivateInvitationOnlyService();

        (await harness.Service.SubscribeToServiceAsync(PortalId, UserId, PrivateServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceNotOfferedCode);
        (await harness.Service.CancelServiceAsync(PortalId, UserId, PrivateServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceNotOfferedCode);

        // The trial path reports the SAME condition under the trial code, because a caller of that
        // operation asked about a trial and ShowTrial's first arm is the same test.
        (await harness.Service.StartServiceTrialAsync(PortalId, UserId, PrivateServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceTrialNotOfferedCode);

        const int unknownRoleId = 4242;
        (await harness.Service.SubscribeToServiceAsync(PortalId, UserId, unknownRoleId, CancellationToken.None))
            .Error!.Code.Should().Be("role.not_found");
        (await harness.Service.StartServiceTrialAsync(PortalId, UserId, unknownRoleId, CancellationToken.None))
            .Error!.Code.Should().Be("role.not_found", "an unknown role is absent on every path");

        harness.DelegatedAssignments.Should().BeEmpty();
        harness.DelegatedRemovals.Should().BeEmpty();
    }

    /// <summary>Cancelling delegates the removal and passes its outcome through unchanged.</summary>
    [Fact]
    public async Task CancelService_DelegatesTheRemovalAndPreservesItsReportedOutcome()
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();
        harness.Subscribe(FreeServiceRoleId, expiry: null);
        harness.DelegatedRemovalResult = Result.Success(new ResultReason(
            "role_assignment.expired_not_removed",
            "The assignment was expired rather than deleted."));

        Result outcome = await harness.Service
            .CancelServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("role_assignment.expired_not_removed");
        harness.DelegatedRemovals.Should().Equal((PortalId, FreeServiceRoleId, UserId));
    }

    /// <summary>The delegate's own refusals reach the caller unchanged.</summary>
    /// <remarks>
    /// Not holding the service, and holding one that may not be withdrawn at all, are the delegate's rules
    /// and are not restated here: the codes travel through so the API edge answers 404 and 403
    /// respectively.
    /// </remarks>
    [Fact]
    public async Task CancelService_SurfacesTheDelegatesOwnRefusalsWithoutRewritingThem()
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();
        harness.DelegatedRemovalResult = Result.Failure(
            "role_assignment.not_found",
            "Member does not hold this role.");

        (await harness.Service.CancelServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be("role_assignment.not_found");

        harness.DelegatedRemovalResult = Result.Failure(
            "role_assignment.protected",
            "This assignment is protected.");

        (await harness.Service.CancelServiceAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be("role_assignment.protected");
    }

    /// <summary>
    /// A trial is performable on a PAID service whose trial is free, which the subscription itself is not -
    /// the two gates genuinely differ.
    /// </summary>
    [Fact]
    public async Task StartServiceTrial_SucceedsOnAPaidServiceThatCannotBeSubscribedTo()
    {
        Harness harness = Harness.Ready();
        harness.PublishPaidServiceWithFreeTrial();

        (await harness.Service.SubscribeToServiceAsync(PortalId, UserId, PaidServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServicePaymentRequiredCode);

        Result trial = await harness.Service
            .StartServiceTrialAsync(PortalId, UserId, PaidServiceRoleId, CancellationToken.None);

        trial.IsSuccess.Should().BeTrue();
        harness.DelegatedAssignments.Should().HaveCount(1);
        harness.DelegatedAssignments[0].RoleId.Should().Be(PaidServiceRoleId);
    }

    /// <summary>Every reason a trial is unavailable answers ONE code, and none of them reaches a write.</summary>
    /// <remarks>
    /// The four conditions are <c>ShowTrial</c>'s own: the service charges nothing and so has nothing to
    /// trial, its trial itself carries a fee, or this account has already consumed it - plus the
    /// unpublished case asserted separately. Distinguishing them would tell a caller which of a tenant's
    /// commercial terms it had guessed wrong about.
    /// </remarks>
    [Fact]
    public async Task StartServiceTrial_RefusesEveryUnavailableTrialUnderOneCode()
    {
        Harness free = Harness.Ready();
        free.PublishFreeService();

        (await free.Service.StartServiceTrialAsync(PortalId, UserId, FreeServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceTrialNotOfferedCode, "a free service has nothing to trial");

        Harness paidTrial = Harness.Ready();
        Role charged = paidTrial.PublishPaidServiceWithFreeTrial();
        charged.TrialFee = 5m;

        (await paidTrial.Service.StartServiceTrialAsync(PortalId, UserId, PaidServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceTrialNotOfferedCode, "the trial itself carries a fee");

        Harness consumed = Harness.Ready();
        consumed.PublishPaidServiceWithFreeTrial();
        consumed.Subscribe(PaidServiceRoleId, expiry: null, trialUsed: true);

        (await consumed.Service.StartServiceTrialAsync(PortalId, UserId, PaidServiceRoleId, CancellationToken.None))
            .Error!.Code.Should().Be(ServiceTrialNotOfferedCode, "the trial has already been consumed");

        free.DelegatedAssignments.Should().BeEmpty();
        paidTrial.DelegatedAssignments.Should().BeEmpty();
        consumed.DelegatedAssignments.Should().BeEmpty();
    }

    /// <summary>
    /// An invitation code searches EVERY role of the tenant, published or not, free or not, and enrols the
    /// account in every one that bears it.
    /// </summary>
    [Fact]
    public async Task RedeemServiceCode_EnrolsEveryRoleBearingTheCodeIncludingUnpublishedAndPaidOnes()
    {
        Harness harness = Harness.Ready();
        Role privateRole = harness.PublishPrivateInvitationOnlyService();
        Role paid = harness.PublishPaidServiceWithFreeTrial();
        paid.RsvpCode = InvitationCode;
        harness.PublishFreeService();

        Result<RedeemServiceCodeResultDto> outcome = await harness.Service.RedeemServiceCodeAsync(
            PortalId,
            UserId,
            new RedeemServiceCodeRequest { Code = InvitationCode },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Roles.Select(role => role.RoleId).Should().BeEquivalentTo(
            new[] { privateRole.RoleId, paid.RoleId },
            "the loop has no early exit and neither the public nor the fee test applies");
        outcome.Value.Roles.Select(role => role.RoleName).Should().OnlyHaveUniqueItems();

        harness.DelegatedAssignments.Select(assignment => assignment.RoleId)
            .Should().BeEquivalentTo(new[] { privateRole.RoleId, paid.RoleId });
        harness.DelegatedAssignments.Should().OnlyContain(assignment => assignment.Request.UserId == UserId);
    }

    /// <summary>
    /// The comparison is ORDINAL and the submission is not trimmed, and a role carrying no code never
    /// matches.
    /// </summary>
    /// <remarks>
    /// <c>objRole.RSVPCode = code</c> (<c>:L411</c>) is a Visual Basic string equality in memory and the
    /// file declares no <c>Option Compare Text</c>, so it compared byte for byte. Widening it would let a
    /// code match a role its issuer did not intend.
    /// </remarks>
    [Theory]
    [InlineData("founders-2026")]
    [InlineData("FOUNDERS-2026")]
    [InlineData(" Founders-2026")]
    [InlineData("Founders-2026 ")]
    [InlineData("Founders")]
    public async Task RedeemServiceCode_MatchesOrdinallyAndWithoutTrimming(string submitted)
    {
        Harness harness = Harness.Ready();
        harness.PublishPrivateInvitationOnlyService();
        harness.PublishFreeService();

        Result<RedeemServiceCodeResultDto> outcome = await harness.Service.RedeemServiceCodeAsync(
            PortalId,
            UserId,
            new RedeemServiceCodeRequest { Code = submitted },
            CancellationToken.None);

        outcome.Error!.Code.Should().Be(ServiceCodeNotMatchedCode);
        harness.DelegatedAssignments.Should().BeEmpty();
    }

    /// <summary>
    /// An empty submission is REFUSED rather than silently matching every role that carries no code.
    /// </summary>
    /// <remarks>
    /// The legacy guard <c>If code &lt;&gt; ""</c> (<c>:L403</c>) did nothing at all for an empty box and
    /// posted no message, so an account could not tell a rejected code from an unread one.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RedeemServiceCode_RefusesAnEmptySubmissionRatherThanMatchingCodelessRoles(string? submitted)
    {
        Harness harness = Harness.Ready();
        harness.PublishFreeService();

        Result<RedeemServiceCodeResultDto> outcome = await harness.Service.RedeemServiceCodeAsync(
            PortalId,
            UserId,
            new RedeemServiceCodeRequest { Code = submitted },
            CancellationToken.None);

        outcome.Error!.Code.Should().Be(ServiceCodeRequiredCode);
        harness.DelegatedAssignments.Should().BeEmpty();
    }

    /// <summary>A refusal on one match abandons the redemption rather than reporting a partial success.</summary>
    [Fact]
    public async Task RedeemServiceCode_AbandonsTheRedemptionWhenAMatchIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.PublishPrivateInvitationOnlyService();
        harness.DelegatedAssignmentResult = Result.Failure(
            "persistence.conflict",
            "A concurrent request changed the assignment.");

        Result<RedeemServiceCodeResultDto> outcome = await harness.Service.RedeemServiceCodeAsync(
            PortalId,
            UserId,
            new RedeemServiceCodeRequest { Code = InvitationCode },
            CancellationToken.None);

        outcome.Error!.Code.Should().Be("persistence.conflict");
        harness.DelegatedAssignments.Should().HaveCount(1, "the walk stops at the first refusal");
    }

    /// <summary>
    /// A failed redemption leaves a record, and the record does not contain the code that was submitted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RedeemServiceCode_RecordsAFailedAttemptAndNeverTheSubmittedCode()
    {
        Harness harness = Harness.Ready();
        harness.PublishPrivateInvitationOnlyService();
        harness.PublishFreeService();

        Result<RedeemServiceCodeResultDto> outcome = await harness.Service.RedeemServiceCodeAsync(
            PortalId,
            UserId,
            new RedeemServiceCodeRequest { Code = "not-the-code" },
            CancellationToken.None);

        outcome.Error!.Code.Should().Be(ServiceCodeNotMatchedCode);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("SERVICE_CODE_REDEMPTION_FAILURE");
        record.PortalId.Should().Be(PortalId);
        record.SubjectUserId.Should().Be(UserId);
        record.ResourceType.Should().Be("User");
        record.Properties.Should().ContainKey("CodedServiceCount")
            .WhoseValue.Should().Be("1", "one of the two published roles carries a code");

        AssertCarriesNoCode(record, "not-the-code");
        AssertCarriesNoCode(record, InvitationCode);
    }

    /// <summary>
    /// A successful redemption leaves a record naming the MECHANISM, and it does not contain the code
    /// either.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RedeemServiceCode_RecordsTheGrantAndNeverTheSubmittedCode()
    {
        Harness harness = Harness.Ready();
        Role privateRole = harness.PublishPrivateInvitationOnlyService();
        Role paid = harness.PublishPaidServiceWithFreeTrial();
        paid.RsvpCode = InvitationCode;

        Result<RedeemServiceCodeResultDto> outcome = await harness.Service.RedeemServiceCodeAsync(
            PortalId,
            UserId,
            new RedeemServiceCodeRequest { Code = InvitationCode },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Roles.Select(role => role.RoleId)
            .Should().BeEquivalentTo(new[] { privateRole.RoleId, paid.RoleId });

        AuditEvent record = harness.AuditRecords.Should().ContainSingle(
            candidate => candidate.EventName == "SERVICE_CODE_REDEEMED").Subject;
        record.PortalId.Should().Be(PortalId);
        record.SubjectUserId.Should().Be(UserId);
        record.Properties.Should().ContainKey("GrantedServiceCount")
            .WhoseValue.Should().Be("2", "one code enrolled the account in two services");

        AssertCarriesNoCode(record, InvitationCode);
    }

    /// <summary>Asserts that a record carries a code in no field a reader of the trail can see.</summary>
    /// <param name="record">The record to sweep.</param>
    /// <param name="code">The code that must not appear.</param>
    private static void AssertCarriesNoCode(AuditEvent record, string code)
    {
        record.ResourceId.Should().NotContain(code, "the resource identifier names the account, not the guess");

        foreach (KeyValuePair<string, string?> property in record.Properties)
        {
            property.Key.Should().NotContainEquivalentOf(
                code,
                "a property NAME carrying the submission exposes it exactly as a value would");
            (property.Value ?? string.Empty).Should().NotContainEquivalentOf(
                code,
                "the trail must not become a list of codes for whoever can read it");
        }
    }

    /// <summary>Account creation and deletion each own exactly one outer transaction.</summary>
    /// <remarks>
    /// The two paths open that scope through DIFFERENT members, and the difference is the point. Creation
    /// may be composed inside a wider operation - installing a tenant creates its administrator - so it
    /// JOINS an ambient scope when one exists.
    /// </remarks>
    [Fact]
    public async Task AccountCreationAndDeletion_UseOneOuterTransactionBoundaryEach()
    {
        Harness creation = Harness.Ready();

        Result<UserDetailDto> created = await creation.Service.CreateUserAsync(
            PortalId,
            ValidCreateRequest(),
            CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        creation.UnitOfWork.Verify(
            unit => unit.JoinOrBeginTransactionAsync(
                TransactionIsolation.Default,
                It.IsAny<CancellationToken>()),
            Times.Once());
        creation.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once());

        Harness deletion = Harness.Ready();

        Result removed = await deletion.Service.DeleteUserAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        removed.IsSuccess.Should().BeTrue();
        deletion.Profiles.Verify(
            profiles => profiles.DeleteProfileValuesAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        deletion.UnitOfWork.Verify(
            unit => unit.BeginTransactionAsync(
                TransactionIsolation.Default,
                It.IsAny<CancellationToken>()),
            Times.Once());
        deletion.UnitOfWork.Verify(
            unit => unit.JoinOrBeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        deletion.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The declaration catalogue is read through the cache under a tenant-keyed name, with a lifetime
    /// scaled by the configured multiplier, and is read straight through when caching is disabled.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListProfilePropertyDefinitions_ReadsThroughTheCacheOrBypassesIt()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> cached = await harness.Service
            .ListProfilePropertyDefinitionsAsync(PortalId, CancellationToken.None);

        cached.IsSuccess.Should().BeTrue();
        harness.CacheKey.Should().Be($"ProfileDefinitions{PortalId}");
        harness.CacheExpiration.Should().Be(TimeSpan.FromMinutes(60));

        harness.CacheKey = null;
        harness.Caching.PerformanceMultiplier = 0;

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> direct = await harness.Service
            .ListProfilePropertyDefinitionsAsync(PortalId, CancellationToken.None);

        direct.Value.Should().NotBeEmpty();
        harness.CacheKey.Should().BeNull();
    }

    /// <summary>
    /// The catalogue is ordered by the display order the tenant chose, falling back to the identifier so
    /// the order is total.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListProfilePropertyDefinitions_OrdersByDisplayOrderThenIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.Definitions.Clear();
        harness.Definitions.Add(Definition(30, "Third", viewOrder: 5));
        harness.Definitions.Add(Definition(10, "Second", viewOrder: 1));
        harness.Definitions.Add(Definition(5, "First", viewOrder: 1));

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await harness.Service
            .ListProfilePropertyDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.Value.Select(definition => definition.PropertyName)
            .Should().Equal(new[] { "First", "Second", "Third" });
    }

    /// <summary>
    /// Reading one declaration reports absence for one that does not exist, one that belongs to another
    /// tenant, and one that was withdrawn — none of which a caller may distinguish from the others.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfilePropertyDefinition_ReportsAbsenceForTheUnknownForeignAndWithdrawn()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = null;

        Result<ProfilePropertyDefinitionDto?> unknown = await harness.Service
            .GetProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);
        unknown.IsSuccess.Should().BeTrue();
        unknown.Value.Should().BeNull();

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.PortalId = OtherPortalId;

        Result<ProfilePropertyDefinitionDto?> foreign = await harness.Service
            .GetProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);
        foreign.Value.Should().BeNull();

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.IsDeleted = true;

        Result<ProfilePropertyDefinitionDto?> withdrawn = await harness.Service
            .GetProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);
        withdrawn.Value.Should().BeNull();
    }

    /// <summary>A declaration that exists in this tenant and was not withdrawn is projected.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfilePropertyDefinition_ProjectsADeclarationOfThisTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");

        Result<ProfilePropertyDefinitionDto?> outcome = await harness.Service
            .GetProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);

        outcome.Value.Should().NotBeNull();
        outcome.Value!.PropertyDefinitionId.Should().Be(StreetPropertyId);
        outcome.Value.PropertyName.Should().Be("Street");
        outcome.Value.PortalId.Should().Be(PortalId);
    }

    /// <summary>
    /// The single read passes the tenant it was given straight through, so the first tenant an installation
    /// has - the one keyed -1 - reads its own declaration and not the host scope's.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetProfilePropertyDefinition_PassesTheFirstTenantKeyThroughUnaltered()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.PortalId = FirstTenantPortalId;

        Result<ProfilePropertyDefinitionDto?> tenantOwned = await harness.Service
            .GetProfilePropertyDefinitionAsync(FirstTenantPortalId, StreetPropertyId, CancellationToken.None);

        tenantOwned.Value.Should().NotBeNull(
            "a tenant numbered -1 must be able to read a declaration it owns");
        tenantOwned.Value!.PortalId.Should().Be(
            FirstTenantPortalId,
            "the stored tenant key is published unchanged");

        harness.Profiles.Verify(
            profiles => profiles.GetDefinitionByIdAsync(
                FirstTenantPortalId,
                StreetPropertyId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The host scope is a SQL NULL portal and is a DIFFERENT scope, so the same request must not
        // reach it. This is the half that fails outright if the sentinel translation is reinstated.
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.PortalId = null;

        Result<ProfilePropertyDefinitionDto?> hostLevelRow = await harness.Service
            .GetProfilePropertyDefinitionAsync(FirstTenantPortalId, StreetPropertyId, CancellationToken.None);

        hostLevelRow.Value.Should().BeNull(
            "a tenant-scoped request must not be answered with a host-level declaration");
    }

    /// <summary>
    /// Declaring a property against the first tenant an installation has stores that tenant's key rather
    /// than filing the declaration into the host scope.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateProfilePropertyDefinition_StoresTheFirstTenantKeyRatherThanTheHostScope()
    {
        Harness harness = Harness.Ready();

        Result<ProfilePropertyDefinitionDto> created = await harness.Service
            .CreateProfilePropertyDefinitionAsync(
                FirstTenantPortalId,
                DefinitionRequest("Street"),
                CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        harness.AddedDefinitions.Should().ContainSingle();
        harness.AddedDefinitions[0].PortalId.Should().Be(
            FirstTenantPortalId,
            "the tenant key is stored as given and is never collapsed into the SQL-null host scope");
        created.Value.PortalId.Should().Be(
            FirstTenantPortalId,
            "the response reports the scope the declaration was actually filed under");
    }

    /// <summary>
    /// Declaring a property requires a payload and a name, and a name is a request fault rather than an
    /// outcome because a nameless declaration cannot be addressed.
    /// </summary>
    /// <param name="submittedName">The name to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateProfilePropertyDefinition_RequiresAName(string submittedName)
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateProfilePropertyDefinitionAsync(PortalId, null!, CancellationToken.None));

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.CreateProfilePropertyDefinitionAsync(
                PortalId,
                new CreateProfilePropertyDefinitionRequest { PropertyName = submittedName },
                CancellationToken.None));

        failure.Message.Should().Be("A profile property name is required.");
        harness.AddedDefinitions.Should().BeEmpty();
    }

    /// <summary>A name the tenant already declares is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateProfilePropertyDefinition_RefusesADuplicateName()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionNameOwnerId = StreetPropertyId;

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .CreateProfilePropertyDefinitionAsync(PortalId, DefinitionRequest("Street"), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileDefinitionDuplicateNameCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already declares a profile property named \"Street\".");

        // The check reads the declaration rather than asking for a boolean, matching the legacy
        // provider member, and on a create there is nothing to exclude so any match is a clash.
        harness.Profiles.Verify(
            p => p.GetDefinitionByNameAsync(PortalId, "Street", It.IsAny<CancellationToken>()),
            Times.Once);
        harness.AddedDefinitions.Should().BeEmpty();
    }

    /// <summary>
    /// A property name declared between the check and the commit is refused with exactly the answer the
    /// check gives, and the catalogue cache is left alone because nothing was written.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>IX_ProfilePropertyDefinition</c> is unique over <c>(PortalID, ModuleDefID, PropertyName)</c>, so
    /// two requests declaring the same property in the same tenant arriving together both read "not
    /// declared" and the loser's insert is refused by the index rather than by the read.
    /// </remarks>
    [Fact]
    public async Task CreateProfilePropertyDefinition_RefusesANameDeclaredBetweenTheCheckAndTheCommit()
    {
        Harness harness = Harness.Ready();
        harness.CommitFault = DuplicateKeyException.ForConstraint("IX_ProfilePropertyDefinition", null);

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .CreateProfilePropertyDefinitionAsync(PortalId, DefinitionRequest("Street"), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileDefinitionDuplicateNameCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already declares a profile property named \"Street\".");

        harness.Cache.Verify(
            cache => cache.InvalidateProfileDefinitions(It.IsAny<int>()),
            Times.Never,
            "nothing was committed, so the catalogue every caller reads did not change");
    }

    /// <summary>
    /// An exchange of positions is written as ONE unit of work, so a half-applied swap is not expressible.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THE DEFECT THIS CLOSES. Position is a member of the per-declaration update contract, so reordering
    /// needed no member of its own and had none: the screen exchanged two positions by issuing two
    /// independent replacements. But a position is not a per-row fact. Land the first replacement and lose
    /// the second and BOTH declarations hold the same position - an order that is neither the one the
    /// operator started from nor the one they asked for, and one no amount of precision about which row
    /// failed can describe.
    /// </para>
    /// <para>
    /// The single commit is the whole point, so it is asserted directly rather than inferred from the
    /// outcome.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReorderProfilePropertyDefinitions_WritesEveryPositionInOneUnitOfWork()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await harness.Service
            .ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                new ReorderProfilePropertyDefinitionsRequest
                {
                    Positions =
                    [
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = StreetPropertyId,
                            ViewOrder = 2,
                        },
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = CityPropertyId,
                            ViewOrder = 1,
                        },
                    ],
                },
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Definitions
            .Single(definition => definition.PropertyDefinitionId == StreetPropertyId)
            .ViewOrder.Should().Be(2);
        harness.Definitions
            .Single(definition => definition.PropertyDefinitionId == CityPropertyId)
            .ViewOrder.Should().Be(1);

        harness.Profiles.Verify(
            profiles => profiles.UpdateDefinitionAsync(
                It.IsAny<ProfilePropertyDefinition>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "both declarations are staged");

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "⚠ ONE commit for the whole exchange. Two commits would make a half-applied swap reachable "
            + "again, which is the defect this member exists to remove");

        harness.Cache.Verify(
            cache => cache.InvalidateProfileDefinitions(PortalId),
            Times.Once,
            "the catalogue every caller reads has changed order");
    }

    /// <summary>
    /// The answer carries the whole catalogue in its new order, so a caller rebinds from the response rather
    /// than following it with a read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A follow-up read could observe another writer's work and make the move look as though it had been
    /// lost, so the ordering the caller just established is reported back to them directly.
    /// </remarks>
    [Fact]
    public async Task ReorderProfilePropertyDefinitions_AnswersWithTheWholeCatalogueInItsNewOrder()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await harness.Service
            .ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                new ReorderProfilePropertyDefinitionsRequest
                {
                    Positions =
                    [
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = TelephonePropertyId,
                            ViewOrder = 0,
                        },
                    ],
                },
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        outcome.Value.Select(definition => definition.PropertyDefinitionId).Should().Equal(
            [TelephonePropertyId, StreetPropertyId, CityPropertyId],
            "the declaration moved to position zero leads the catalogue the answer carries");
    }

    /// <summary>
    /// A request naming a declaration the tenant does not hold writes NOTHING, rather than applying the part
    /// of the order it could resolve.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the all-or-nothing property stated as an assertion. The absence is reported BEFORE anything is
    /// staged, so the declarations the request did resolve keep the positions they held.
    /// </remarks>
    [Fact]
    public async Task ReorderProfilePropertyDefinitions_RefusesAnUnknownDeclarationWithoutWritingAnything()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await harness.Service
            .ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                new ReorderProfilePropertyDefinitionsRequest
                {
                    Positions =
                    [
                        // Resolvable, and deliberately FIRST so that a member staging as it went would have
                        // written this one before discovering the second.
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = StreetPropertyId,
                            ViewOrder = 9,
                        },
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = 4242,
                            ViewOrder = 0,
                        },
                    ],
                },
                CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Profile property definition 4242 does not exist in portal {PortalId}.");

        harness.Definitions
            .Single(definition => definition.PropertyDefinitionId == StreetPropertyId)
            .ViewOrder.Should().Be(1, "the resolvable declaration keeps the position it held");

        harness.Profiles.Verify(
            profiles => profiles.UpdateDefinitionAsync(
                It.IsAny<ProfilePropertyDefinition>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "nothing is staged once any named declaration cannot be resolved");

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        harness.Cache.Verify(
            cache => cache.InvalidateProfileDefinitions(It.IsAny<int>()),
            Times.Never,
            "nothing was committed, so the catalogue every caller reads did not change");
    }

    /// <summary>
    /// A withdrawn declaration is absent to this member too, matching every other member of the contract.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ReorderProfilePropertyDefinitions_TreatsAWithdrawnDeclarationAsAbsent()
    {
        Harness harness = Harness.Ready();

        // The catalogue read excludes withdrawn declarations by contract, which is what makes ONE absence
        // test sufficient for unknown, foreign-tenant and withdrawn alike.
        harness.Definitions.RemoveAll(
            definition => definition.PropertyDefinitionId == CityPropertyId);

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await harness.Service
            .ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                new ReorderProfilePropertyDefinitionsRequest
                {
                    Positions =
                    [
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = CityPropertyId,
                            ViewOrder = 0,
                        },
                    ],
                },
                CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);
    }

    /// <summary>A request that repositions nothing is a programming fault rather than a refusal.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The shape is settled by the request validator before the service is reached, so an empty set arriving
    /// here means a caller inside the process assembled one - which is a defect in that caller, not a
    /// decision for an operator to act on.
    /// </remarks>
    [Fact]
    public async Task ReorderProfilePropertyDefinitions_RequiresARequestAndAtLeastOnePosition()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                null!,
                CancellationToken.None));

        await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                new ReorderProfilePropertyDefinitionsRequest(),
                CancellationToken.None));

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Submitted positions are stored EXACTLY as submitted; nothing is renumbered or compacted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy grid exchanged two stored values and persisted them unchanged, leaving the sequence sparse.
    /// Tidying it here would silently move declarations the caller never named.
    /// </remarks>
    [Fact]
    public async Task ReorderProfilePropertyDefinitions_StoresSparsePositionsWithoutRenumbering()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ProfilePropertyDefinitionDto>> outcome = await harness.Service
            .ReorderProfilePropertyDefinitionsAsync(
                PortalId,
                new ReorderProfilePropertyDefinitionsRequest
                {
                    Positions =
                    [
                        new ProfilePropertyDefinitionPosition
                        {
                            PropertyDefinitionId = StreetPropertyId,
                            ViewOrder = 40,
                        },
                    ],
                },
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Definitions
            .Single(definition => definition.PropertyDefinitionId == StreetPropertyId)
            .ViewOrder.Should().Be(40, "the submitted value is stored, not an index derived from it");
        harness.Definitions
            .Single(definition => definition.PropertyDefinitionId == CityPropertyId)
            .ViewOrder.Should().Be(2, "a declaration the request did not name is not moved");
        harness.Definitions
            .Single(definition => definition.PropertyDefinitionId == TelephonePropertyId)
            .ViewOrder.Should().Be(3);
    }

    /// <summary>
    /// A declaration is created against the tenant from the route, is not withdrawn, and the catalogue
    /// cache is discarded so the new property becomes visible.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateProfilePropertyDefinition_CreatesAgainstTheRoutesTenantAndDiscardsTheCatalogue()
    {
        Harness harness = Harness.Ready();
        CreateProfilePropertyDefinitionRequest request = DefinitionRequest("Nickname");
        request.Required = true;
        request.Length = 40;
        request.ViewOrder = 7;

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .CreateProfilePropertyDefinitionAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        ProfilePropertyDefinition created = harness.AddedDefinitions.Should().ContainSingle().Which;
        created.PortalId.Should().Be(PortalId);
        created.IsDeleted.Should().BeFalse();
        created.PropertyName.Should().Be("Nickname");
        created.IsRequired.Should().BeTrue();
        created.Length.Should().Be(40);
        created.ViewOrder.Should().Be(7);
        outcome.Value.PortalId.Should().Be(PortalId);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedProfileDefinitionsPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// Changing a declaration requires a payload, refuses one that does not exist, belongs to another
    /// tenant or was withdrawn, and requires a name.
    /// </summary>
    /// <remarks>
    /// MIGRATION: THE WITHDRAWN CASE IS THE REGRESSION. This member's guard tested only existence and
    /// tenancy, while the single read beside it also tested withdrawal, so a declaration the contract
    /// refused to SHOW stayed editable through this one - and the contract exposes no member that reads or
    /// restores a withdrawn declaration, so the asymmetry had no recycle-bin behind it.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_RefusesTheUnknownTheForeignTheWithdrawnAndTheNameless()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                null!,
                CancellationToken.None));

        harness.LookupDefinition = null;

        Result<ProfilePropertyDefinitionDto> unknown = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("Street"),
                CancellationToken.None);

        unknown.IsFailure.Should().BeTrue();
        unknown.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);
        unknown.Reason!.Message.Should().Be(
            $"Profile property definition {StreetPropertyId} does not exist in portal {PortalId}.");

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.PortalId = OtherPortalId;

        Result<ProfilePropertyDefinitionDto> foreign = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("Street"),
                CancellationToken.None);

        foreign.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.IsDeleted = true;

        Result<ProfilePropertyDefinitionDto> withdrawn = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("Street"),
                CancellationToken.None);

        withdrawn.IsFailure.Should().BeTrue(
            "a declaration the read path reports as absent must not be editable through this one");
        withdrawn.Reason!.Code.Should().Be(
            ProfileDefinitionNotFoundCode,
            "the withdrawn case must be indistinguishable from the unknown and the foreign");

        harness.UnitOfWork.Verify(
            u => u.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");

        DomainException nameless = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                new UpdateProfilePropertyDefinitionRequest { PropertyName = "  " },
                CancellationToken.None));

        nameless.Message.Should().Be("A profile property name is required.");
    }

    /// <summary>
    /// The duplicate-name check excludes the declaration being changed, so resubmitting a name unchanged is
    /// not a clash with itself.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_ExcludesTheDeclarationItselfFromTheNameCheck()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");

        harness.DefinitionNameOwnerId = StreetPropertyId;

        Result<ProfilePropertyDefinitionDto> permitted = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("Street"),
                CancellationToken.None);

        permitted.IsSuccess.Should().BeTrue();
        harness.Profiles.Verify(
            p => p.GetDefinitionByNameAsync(
                PortalId,
                "Street",
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Now the name is held by a DIFFERENT declaration, which is a genuine clash.
        harness.DefinitionNameOwnerId = CityPropertyId;

        Result<ProfilePropertyDefinitionDto> refused = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("City"),
                CancellationToken.None);

        refused.Reason!.Code.Should().Be(ProfileDefinitionDuplicateNameCode);
    }

    /// <summary>A competing write on a declaration is reported as a conflict to retry.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_ReportsACompetingWriteAsAConflict()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.CommitFault = new DbUpdateConcurrencyException();

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("Street"),
                CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PersistenceConflictCode);
        outcome.Reason!.Message.Should()
            .Be("The definition was changed by another request; reload it and try again.");
    }

    /// <summary>
    /// A rename onto a name declared between the check and the commit is reported as the duplicate it is,
    /// and NOT as a stale read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// And the distinction is the point. This member already reported a competing write as a conflict to
    /// retry, which is the right answer for a row that changed underneath - but it is the wrong answer for
    /// a name another request took, because reloading and resubmitting the same rename will be refused
    /// again for ever.
    /// </remarks>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_ReportsANameTakenDuringTheWriteAsADuplicateNotAStaleRead()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.CommitFault = DuplicateKeyException.ForConstraint("IX_ProfilePropertyDefinition", null);

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionUpdate("City"),
                CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(
            ProfileDefinitionDuplicateNameCode,
            "the name is taken, so telling the caller to reload and retry would send it round a loop that "
            + "cannot terminate");
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already declares a profile property named \"City\".");

        harness.Cache.Verify(
            cache => cache.InvalidateProfileDefinitions(It.IsAny<int>()),
            Times.Never);
    }

    /// <summary>A successful change applies the submitted shape and discards the catalogue cache.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_AppliesTheShapeAndDiscardsTheCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        UpdateProfilePropertyDefinitionRequest request = DefinitionUpdate("Street Address");
        request.Length = 120;
        request.Required = false;
        request.Visible = false;
        request.ViewOrder = 3;
        request.PropertyCategory = "Address";
        request.ValidationExpression = ".+";

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                request,
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupDefinition!.PropertyName.Should().Be("Street Address");
        harness.LookupDefinition!.Length.Should().Be(120);
        harness.LookupDefinition!.IsRequired.Should().BeFalse();
        harness.LookupDefinition!.IsVisible.Should().BeFalse();
        harness.LookupDefinition!.ViewOrder.Should().Be(3);
        harness.LookupDefinition!.PropertyCategory.Should().Be("Address");
        harness.LookupDefinition!.ValidationExpression.Should().Be(".+");
        harness.InvalidatedProfileDefinitionsPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// A property submitted as required is stored visible whatever visibility the caller asked for, on both
    /// the creating and the amending verb.
    /// </summary>
    /// <param name="submittedVisibility">The visibility the caller submitted.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateProfilePropertyDefinition_StoresARequiredPropertyAsVisible(bool submittedVisibility)
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        UpdateProfilePropertyDefinitionRequest request = DefinitionUpdate("Street Address");
        request.Required = true;
        request.Visible = submittedVisibility;

        Result<ProfilePropertyDefinitionDto> outcome = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                request,
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupDefinition!.IsRequired.Should().BeTrue();
        harness.LookupDefinition!.IsVisible.Should().BeTrue(
            "the legacy rule promotes visibility for a required property rather than storing a property the "
            + "account must answer and can never see");
        outcome.Value.Visible.Should().BeTrue(
            "the response must publish what was stored, not what was submitted");
    }

    /// <summary>
    /// Removing a declaration refuses one that does not exist, belongs to another tenant, or was already
    /// withdrawn.
    /// </summary>
    /// <remarks>
    /// MIGRATION: THE WITHDRAWN CASE IS THE REGRESSION, AND THIS IS THE VERB WHERE IT MATTERED MOST. This
    /// guard tested only existence and tenancy while the read paths also tested withdrawal, so the one
    /// operation reachable on a declaration the contract refused to show was the destructive one - and
    /// removal here is physical, discarding every stored answer with it, so it destroyed data no caller
    /// could have inspected first.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    /// <summary>
    /// The four reserved declarations cannot be withdrawn, which restores a protection the legacy screen
    /// provided by hiding its delete command.
    /// </summary>
    /// <param name="propertyName">The reserved name, in each of the spellings an operator might send.</param>
    /// <remarks>
    /// ⚠ WHY THIS IS PARITY AND NOT A NEW RULE. `grdProfileProperties_ItemDataBound` set the delete command
    /// invisible for exactly these four, compared against `PropertyName.ToLower`. The grid was the ONLY path to
    /// the operation, so hiding the control was the enforcement - the observable behaviour of the system was
    /// that these four could not be removed. This API is a second path the legacy design never had, so
    /// reproducing only the hidden button would have widened what the system permits while looking faithful.
    /// The casing cases exist because the legacy comparison was case-insensitive and a REST caller, unlike the
    /// grid, chooses its own spelling.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("TimeZone")]
    [InlineData("PreferredLocale")]
    [InlineData("firstname")]
    [InlineData("PREFERREDLOCALE")]
    public async Task DeleteProfilePropertyDefinition_RefusesAReservedDeclaration(string propertyName)
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, propertyName);

        Result outcome = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ProfileDefinitionProtectedCode);
        outcome.Reason.Message.Should().Contain(propertyName, "the refusal names which declaration it means");

        harness.DeletedDefinitionIds.Should().BeEmpty("nothing may be staged for a reserved declaration");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    /// <summary>
    /// ⚠ CONSENT DOES NOT UNLOCK A RESERVED DECLARATION. The two protections are independent, and the
    /// reserved-name refusal is terminal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Worth its own case because the two guards sit next to each other in one method: an ordering mistake that
    /// evaluated consent first would make the reserved rule bypassable by any caller who simply set the flag,
    /// and no other test in this group would notice.
    /// </remarks>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_RefusesAReservedDeclarationEvenWithConsent()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "FirstName");

        Result outcome = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                confirmValueDeletion: true,
                cancellationToken: CancellationToken.None);

        outcome.Reason!.Code.Should().Be(
            ProfileDefinitionProtectedCode,
            "consent addresses the cascade, not the reservation");
        harness.DeletedDefinitionIds.Should().BeEmpty();
    }

    /// <summary>
    /// A declaration accounts have answered is refused until the caller consents, and the refusal reports how
    /// many answers are at stake.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is Area of Concern A1 directly: one authenticated request removed a declaration and the store's
    /// cascade took every recorded answer with it, irreversibly, with nothing in the request indicating the
    /// scale of the loss. The count in the message is what makes the second attempt an informed decision, so it
    /// is asserted rather than merely the code.
    /// </remarks>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_RefusesACascadeUntilItIsConsentedTo()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.Profiles
            .Setup(p => p.CountProfileValuesForDefinitionAsync(
                StreetPropertyId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(6);

        Result refused = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);

        refused.IsFailure.Should().BeTrue();
        refused.Reason!.Code.Should().Be(ProfileDefinitionValueCascadeCode);
        refused.Reason.Message.Should().Contain(
            "6",
            "the number of answers at stake is the one fact that makes the consent informed");
        refused.Reason.Message.Should().Contain(
            "cannot be undone",
            "the operator is told the loss is irreversible, not merely that something will be deleted");
        refused.Reason.Message.Should().Contain(
            "confirmValueDeletion",
            "and the refusal names the parameter that performs it, so it is actionable from the response alone");

        harness.DeletedDefinitionIds.Should().BeEmpty("the first attempt must change nothing");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    /// <summary>The same removal proceeds once consent is given.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_ProceedsOnceTheCascadeIsConsentedTo()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.Profiles
            .Setup(p => p.CountProfileValuesForDefinitionAsync(
                StreetPropertyId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(6);

        Result outcome = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                confirmValueDeletion: true,
                cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "the protection is a confirmation, not a prohibition - an administrator who means it may proceed");
        harness.DeletedDefinitionIds.Should().Contain(StreetPropertyId);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    /// <summary>
    /// A declaration nobody has answered needs no consent, because there is nothing to consent to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The protection must not become ceremony on the ordinary case. A declaration created and then removed
    /// without ever being filled in is the common shape, and requiring a second request for it would train
    /// operators to send the flag reflexively - which would defeat the protection on the case that needs it.
    /// </remarks>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_NeedsNoConsentWhenNothingWouldBeDestroyed()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.Profiles
            .Setup(p => p.CountProfileValuesForDefinitionAsync(
                StreetPropertyId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        Result outcome = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.DeletedDefinitionIds.Should().Contain(StreetPropertyId);
    }

    [Fact]
    public async Task DeleteProfilePropertyDefinition_RefusesTheUnknownTheForeignAndTheWithdrawn()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = null;

        Result unknown = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);
        unknown.IsFailure.Should().BeTrue();
        unknown.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.PortalId = OtherPortalId;

        Result foreign = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);
        foreign.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);
        harness.DeletedDefinitionIds.Should().BeEmpty();

        ProfilePropertyDefinition withdrawnDefinition = Definition(StreetPropertyId, "Street");
        withdrawnDefinition.IsDeleted = true;
        withdrawnDefinition.ProfileValues.Add(Value(1, UserId, StreetPropertyId, "Fleet Street"));
        harness.LookupDefinition = withdrawnDefinition;

        Result withdrawn = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);

        withdrawn.IsFailure.Should().BeTrue(
            "a declaration the read path reports as absent must not be removable through this one");
        withdrawn.Reason!.Code.Should().Be(
            ProfileDefinitionNotFoundCode,
            "the withdrawn case must be indistinguishable from the unknown and the foreign");
        harness.DeletedDefinitionIds.Should().BeEmpty(
            "nothing may be staged for removal, so the answers recorded against it survive");
        harness.UnitOfWork.Verify(
            u => u.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Withdrawing a declaration also removes every value recorded against it, because a value whose
    /// declaration is gone can be neither read nor corrected.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_RemovesEveryValueRecordedAgainstIt()
    {
        Harness harness = Harness.Ready();
        ProfilePropertyDefinition definition = Definition(StreetPropertyId, "Street");
        definition.ProfileValues.Add(Value(1, UserId, StreetPropertyId, "Fleet Street"));
        definition.ProfileValues.Add(Value(2, OtherUserId, StreetPropertyId, "Baker Street"));
        harness.LookupDefinition = definition;

        Result outcome = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        // The declaration is withdrawn by identifier, matching the legacy procedure, and its two recorded
        // answers travel with it: FK_UserProfile_ProfilePropertyDefinition is declared ON DELETE CASCADE
        // and the repository loads the answers before staging the removal, so the service issues no
        // per-answer deletion of its own and none is asserted here.
        harness.DeletedDefinitionIds.Should().Equal(new[] { StreetPropertyId });
        definition.ProfileValues.Should().HaveCount(2);
        harness.UpdatedValues.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedProfileDefinitionsPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// Reads the value recorded for a named setting out of the settings the harness observed being added.
    /// </summary>
    /// <param name="harness">The harness that observed the write.</param>
    /// <param name="settingName">The setting name to read.</param>
    /// <returns>The recorded value.</returns>
    private static string SettingValue(Harness harness, string settingName)
        => harness.AddedSettings
            .Single(setting => string.Equals(setting.SettingName, settingName, StringComparison.Ordinal))
            .SettingValue;

    /// <summary>
    /// Produces the hash the harness's password hasher is measured to produce, so a test can assert on what
    /// reached the credential store without duplicating the hasher.
    /// </summary>
    /// <param name="raw">The credential that was hashed.</param>
    /// <returns>The stored form.</returns>
    private static string StoredHashFor(string raw) => "hash:" + raw;

    /// <summary>
    /// Builds the settled tenant facts of an inbound call, as the request pipeline would have settled them.
    /// </summary>
    /// <param name="portalId">The tenant the request was addressed to.</param>
    /// <param name="administratorId">The account the tenant designates, or <see langword="null"/>.</param>
    /// <returns>An immutable snapshot of the call's tenant facts.</returns>
    private static IPortalContext TenantFacts(int portalId, int? administratorId)
    {
        var facts = new Mock<IPortalContext>(MockBehavior.Loose);
        facts.SetupGet(context => context.PortalId).Returns(portalId);
        facts.SetupGet(context => context.AdministratorId).Returns(administratorId);

        return facts.Object;
    }

    /// <summary>Builds the account fixture the store returns.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="username">The account name.</param>
    /// <returns>An account row.</returns>
    private static User StoredUser(int userId = UserId, string username = Username) => new()
    {
        UserId = userId,
        Username = username,
        FirstName = "Grace",
        LastName = "Hopper",
        DisplayName = "Grace B Hopper",
        Email = Email,
    };

    /// <summary>Builds a profile-value row.</summary>
    /// <param name="profileId">The row identifier.</param>
    /// <param name="userId">The owning account.</param>
    /// <param name="propertyDefinitionId">The declaration the value belongs to.</param>
    /// <param name="value">The recorded value.</param>
    /// <param name="visibility">The recorded visibility.</param>
    /// <returns>A profile-value row.</returns>
    private static UserProfileValue Value(
        int profileId,
        int userId,
        int propertyDefinitionId,
        string? value,
        int visibility = 2) => new()
        {
            ProfileId = profileId,
            UserId = userId,
            PropertyDefinitionId = propertyDefinitionId,
            PropertyValue = value,
            Visibility = visibility,
            LastUpdatedDate = Now.AddDays(-1),
        };

    /// <summary>Builds a profile-property declaration.</summary>
    /// <param name="propertyDefinitionId">The declaration identifier.</param>
    /// <param name="propertyName">The declared name.</param>
    /// <param name="viewOrder">The declared display order.</param>
    /// <returns>A declaration row.</returns>
    private static ProfilePropertyDefinition Definition(
        int propertyDefinitionId,
        string propertyName,
        int viewOrder = 0) => new()
        {
            PropertyDefinitionId = propertyDefinitionId,
            PortalId = PortalId,
            PropertyName = propertyName,
            PropertyCategory = "Contact",
            Length = 0,
            ViewOrder = viewOrder,
            IsVisible = true,
        };

    /// <summary>Builds the module definition that membership settings are stored against.</summary>
    /// <returns>A module definition row.</returns>
    private static ModuleDefinition AccountsDefinition() => new()
    {
        ModuleDefinitionId = AccountsModuleDefinitionId,
        FriendlyName = MembershipSettingsDto.UserAccountsModuleDefinitionName,
        DesktopModuleId = 1,
    };

    /// <summary>Builds a well-formed account-creation request.</summary>
    /// <returns>A creation request.</returns>
    private static CreateUserRequest ValidCreateRequest() => new()
    {
        Username = Username,
        FirstName = "Grace",
        LastName = "Hopper",
        DisplayName = "Grace Hopper",
        Email = Email,
        Password = CurrentPassword,
        ConfirmPassword = CurrentPassword,
        Authorize = true,
    };

    /// <summary>Builds a well-formed account-update request.</summary>
    /// <returns>An update request.</returns>
    private static UpdateUserRequest ValidUpdateRequest() => new()
    {
        FirstName = "Grace",
        LastName = "Hopper",
        DisplayName = "Grace B Hopper",
        Email = "grace.hopper@example.com",
    };

    /// <summary>Builds a well-formed credential-change request.</summary>
    /// <returns>A credential-change request.</returns>
    private static ChangePasswordRequest ValidChangeRequest() => new()
    {
        Operation = ChangePasswordRequest.OperationChange,
        CurrentPassword = CurrentPassword,
        NewPassword = NewPassword,
        ConfirmPassword = NewPassword,
    };

    /// <summary>
    /// Gives the harness the caller the named operation is available to, so a theory that exercises both
    /// operations states the authority each one needs instead of quietly relying on one caller passing
    /// both.
    /// </summary>
    /// <param name="harness">The harness to configure.</param>
    /// <param name="operation">The operation the theory case submits, in whatever spelling it submits it.</param>
    /// <remarks>
    /// The two operations are not available to the same caller and that asymmetry is the security property
    /// under test: a change is proof of possession and is the owner's to make, while a reset presents no
    /// current credential and is therefore administrative.
    /// </remarks>
    private static void ActAccordingToTheOperation(Harness harness, string? operation)
    {
        if (string.Equals(operation, ChangePasswordRequest.OperationReset, StringComparison.OrdinalIgnoreCase))
        {
            harness.ActAsAPortalAdministrator();
        }
        else
        {
            harness.ActAsTheAccountOwner();
        }
    }

    /// <summary>
    /// Invokes the entry point that PERFORMS the named operation, so a theory covering both operations
    /// exercises each one through its own member.
    /// </summary>
    /// <param name="harness">The harness whose service is invoked.</param>
    /// <param name="operation">The operation the theory case submits, in whatever spelling it submits it.</param>
    /// <param name="request">The submitted credential change.</param>
    /// <returns>The outcome the entry point reported.</returns>
    /// <remarks>
    /// The two operations are two MEMBERS, not one member switching on the submitted discriminator.
    /// </remarks>
    private static Task<Result> PerformCredentialWriteAsync(
        Harness harness,
        string? operation,
        ChangePasswordRequest request) =>
        string.Equals(operation, ChangePasswordRequest.OperationReset, StringComparison.OrdinalIgnoreCase)
            ? harness.Service.ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None)
            : harness.Service.ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

    /// <summary>Builds a declaration payload.</summary>
    /// <param name="propertyName">The declared name.</param>
    /// <returns>A declaration payload.</returns>
    private static CreateProfilePropertyDefinitionRequest DefinitionRequest(string propertyName) => new()
    {
        PropertyName = propertyName,
        PropertyCategory = "Contact",
        Visible = true,
    };

    /// <summary>
    /// Builds the update-verb counterpart of <see cref="DefinitionRequest(string)"/>, member for member.
    /// </summary>
    /// <param name="propertyName">The property name to submit.</param>
    /// <returns>An update request.</returns>
    private static UpdateProfilePropertyDefinitionRequest DefinitionUpdate(string propertyName) => new()
    {
        PropertyName = propertyName,
        PropertyCategory = "Contact",
        Visible = true,
    };

    /// <summary>Builds a profile submission from a set of declaration identifiers and values.</summary>
    /// <param name="properties">The identifier and value pairs to submit.</param>
    /// <returns>A profile submission.</returns>
    private static UserProfileDto Profile(params (int PropertyDefinitionId, string Value)[] properties) => new()
    {
        UserId = UserId,
        Properties = properties
            .Select(property => new UserProfileValueDto
            {
                PropertyDefinitionId = property.PropertyDefinitionId,
                PropertyValue = property.Value,
                Visibility = 2,
            })
            .ToList(),
    };

    /// <summary>
    /// A stand-in for the store's concurrency fault, named exactly as the service recognises it by name so
    /// the recognition can be exercised without referencing the persistence assembly.
    /// </summary>
    private sealed class DbUpdateConcurrencyException : Exception
    {
        /// <summary>Initialises a new instance of the <see cref="DbUpdateConcurrencyException"/> class.</summary>
        public DbUpdateConcurrencyException()
            : base("a competing write was detected")
        {
        }
    }

    /// <summary>
    /// Assembles the service over fourteen recording doubles, exposing every answer as mutable state so a
    /// test can change the world after the doubles have been wired.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            PasswordPolicy = new PasswordPolicyOptions();
            Caching = new CachingOptions();
            PortalExists = true;
            PortalRow = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                DefaultLanguage = "en-US",
                HomeDirectory = "Portals/0",
                AdministratorId = AdministratorId,
            };
            LookupUser = StoredUser();
            UserPage = PagedResult<User>.Unpaged([]);
            RoleNamesForUser = [];
            AutoAssigned = [];
            Definitions =
            [
                Definition(StreetPropertyId, "Street", viewOrder: 1),
                Definition(CityPropertyId, "City", viewOrder: 2),
                Definition(TelephonePropertyId, "Telephone", viewOrder: 3),
            ];
            ValuesByUserId = [];
            UserAssignments = [];
            ModuleDefinitionCatalogue = [];
            ModuleInstances = [];
            StoredModuleSettings = [];
            CredentialExists = true;
            CredentialApproved = true;
            CredentialLockedOut = false;
            CredentialCreated = true;
            PasswordWritten = CredentialWriteOutcome.Replaced;
            UnlockSucceeded = true;
            ApprovalSucceeded = true;
            UserCount = 3;

            AddedUsers = [];
            RemovedUsers = [];
            PublishedServices = [];
            DelegatedAssignments = [];
            DelegatedRemovals = [];
            RemovedMemberships = [];
            RemovedAssignments = [];
            AddedValues = [];
            UpdatedValues = [];
            AddedDefinitions = [];
            DeletedDefinitionIds = [];
            AddedSettings = [];
            HashedSecrets = [];
            CreatedCredentials = [];
            DeletedCredentialUserIds = [];
            RevokedSessionUserIds = [];
            SetPasswordHashes = [];
            SetPasswordExpectations = [];
            ApprovalWrites = [];
            CascadedUserPermissions = [];
            EvictedGrantCaches = [];
            InvalidatedPortalIds = [];
            InvalidatedUsers = [];
            InvalidatedProfileDefinitionsPortalIds = [];

            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Profiles = new Mock<IUserProfileRepository>(MockBehavior.Loose);
            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            RoleService = new Mock<IRoleService>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            ModuleDefinitions = new Mock<IModuleDefinitionRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Transaction = new Mock<ITransactionScope>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            AuditRecords = [];
            Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(AuditRecords.Add);

            Tokens = new Mock<ITokenService>(MockBehavior.Loose);
            StoreFailures = new Mock<IStoreFailureClassifier>(MockBehavior.Loose);

            Diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Loose);
            DiagnosedOccurrences = [];
            Diagnostics
                .Setup(diagnostics => diagnostics.Record(
                    It.IsAny<SecurityDiagnosticEvent>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>()))
                .Callback<SecurityDiagnosticEvent, int?, int?, string?>(
                    (occurrence, portalId, userId, reasonCode) =>
                        DiagnosedOccurrences.Add((occurrence, portalId, userId, reasonCode)));

            Transaction = new Mock<ITransactionScope>(MockBehavior.Loose);

            // ⚠ UNRESOLVED BY DEFAULT, WHICH IS THE HONEST DEFAULT FOR A UNIT TEST. A unit test runs
            // outside any request scope, so no middleware has settled the tenant facts - and the holder's
            // contract says reading Current before that point throws.
            PortalContext = new Mock<IPortalContextHolder>(MockBehavior.Loose);
            PortalContext.SetupGet(holder => holder.IsResolved).Returns(false);

            // Both account-lifecycle workflows open ONE explicit transaction - creation so that the account
            // row and its external credential are published together, deletion so that the grant cascade,
            // the assignments, the membership, the account row and the credential removal are
            // all-or-nothing.
            UnitOfWork
                .Setup(unitOfWork => unitOfWork.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Transaction.Object);

            Service = new UserService(
                Users.Object,
                Profiles.Object,
                Roles.Object,
                Permissions.Object,
                RoleService.Object,
                Portals.Object,
                Modules.Object,
                ModuleDefinitions.Object,
                Tabs.Object,
                UnitOfWork.Object,
                PasswordHasher.Object,
                Clock.Object,
                Cache.Object,
                CurrentUser.Object,
                Audit.Object,
                Tokens.Object,
                StoreFailures.Object,
                Diagnostics.Object,
                PasswordPolicy,
                Caching,
                PortalContext.Object);
        }

        public UserService Service { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IUserProfileRepository> Profiles { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IPermissionService> Permissions { get; }

        /// <summary>
        /// The role contract, reached only by the member-services operations and only for the two
        /// membership primitives they delegate: the assignment and the removal. Loose by default, so a
        /// delegated write answers a default outcome unless a test says otherwise.
        /// </summary>
        public Mock<IRoleService> RoleService { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<IModuleDefinitionRepository> ModuleDefinitions { get; }

        /// <summary>
        /// The page repository, read only to prove that a membership-settings redirect target belongs to
        /// the tenant being written.
        /// </summary>
        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        /// <summary>
        /// The transaction scope the unit of work hands back. The deletion cascade opens exactly one around
        /// all five of its writes, so a scope must exist for the await-using to dispose.
        /// </summary>
        public Mock<ITransactionScope> Transaction { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

        public Mock<IClock> Clock { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public Mock<IAuditSink> Audit { get; }

        /// <summary>Every audit record the service emitted, in the order it emitted them.</summary>
        public List<AuditEvent> AuditRecords { get; }
        public Mock<ITokenService> Tokens { get; }

        /// <summary>
        /// Classifies a caught failure as the store's. Loose by default, which answers <c>false</c> for
        /// every exception, so the account-creation guard absorbs nothing unless a test says the store
        /// failed - which is the shape of the production rule rather than a convenience.
        /// </summary>
        public Mock<IStoreFailureClassifier> StoreFailures { get; }
        /// <summary>The tenant-facts holder the detail read consults before falling back to a portal read.</summary>
        public Mock<IPortalContextHolder> PortalContext { get; }
        /// <summary>
        /// A failure raised by the POST-COMMIT grant-cache eviction, or <see langword="null"/> for none.
        /// </summary>
        /// <remarks>
        /// The eviction is the last step after the commit, so it is the one piece of post-commit
        /// maintenance a disconnecting caller can make fail. This knob exists so an assertion can prove
        /// that the audit record of a completed deletion no longer depends on it.
        /// </remarks>
        public Exception? GrantCacheEvictionFault { get; set; }

        /// <summary>The private diagnostics recorder.</summary>
        public Mock<ISecurityDiagnostics> Diagnostics { get; }

        /// <summary>Every occurrence the service recorded privately, in the order it recorded them.</summary>
        public List<(SecurityDiagnosticEvent Occurrence, int? PortalId, int? UserId, string? ReasonCode)>
            DiagnosedOccurrences
        { get; }

        /// <summary>Accounts whose sessions the service asked to have ended, in the order it asked.</summary>
        public List<int> RevokedSessionUserIds { get; }

        /// <summary>Whether the token store can end an account's sessions. Defaults to true.</summary>
        public bool SessionsRevoked { get; set; } = true;

        /// <summary>The scopes the service asked the token store to ERASE, in the order it asked.</summary>
        /// <remarks>
        /// PRIV-02. Distinct from <see cref="RevokedSessionUserIds"/> because revocation and erasure are
        /// different operations with different consequences: a revoked record is retained so that a replay
        /// of its family stays recognisable, and an erased one is gone.
        /// </remarks>
        public List<(int UserId, int? PortalId)> PurgedSessionScopes { get; } = [];

        /// <summary>Whether the token store can erase an account's session records. Defaults to true.</summary>
        public bool SessionRecordsErased { get; set; } = true;

        /// <summary>
        /// How many revocations succeed before the store starts refusing, or <see langword="null"/> for a
        /// store whose behaviour is governed solely by <see cref="SessionsRevoked"/>.
        /// </summary>
        /// <remarks>
        /// Needed because a credential write now sweeps the account's sessions TWICE - once before the
        /// write and once after it - so "the store refuses" is no longer one situation.
        /// </remarks>
        public int? SessionRevocationsBeforeFailure { get; set; }

        /// <summary>
        /// What the credential store reports when an account's credential is removed. Defaults to a recorded
        /// removal.
        /// </summary>
        /// <remarks>
        /// AN OUTCOME RATHER THAN A BOOLEAN, because the service now treats two of its members as opposites:
        /// <see cref="MembershipWriteOutcome.StoreUnavailable"/> abandons the deletion cascade, while <see
        /// cref="MembershipWriteOutcome.NoRecord"/> - the store answered and holds no credential - is the end
        /// state the cascade is reaching for and is passed through. A boolean could not express the
        /// difference, which is exactly how a lost race came to be answered as a store outage.
        /// </remarks>
        public MembershipWriteOutcome CredentialRemoval { get; set; } = MembershipWriteOutcome.Recorded;

        public PasswordPolicyOptions PasswordPolicy { get; }

        public CachingOptions Caching { get; }

        public bool PortalExists { get; set; }

        public Portal? PortalRow { get; set; }

        public User? LookupUser { get; set; }

        public User? UserByUsername { get; set; }

        public bool UsernameTaken { get; set; }

        public bool EmailTaken { get; set; }

        public UserPortal? Membership { get; set; }

        public PagedResult<User> UserPage { get; set; }

        public IReadOnlyList<string> RoleNamesForUser { get; set; }

        public List<Role> AutoAssigned { get; }

        /// <summary>
        /// The tenant's PUBLIC roles - the member-services catalogue, which the subscribable-role read
        /// returns. Kept separate from <see cref="AutoAssigned"/>, which publishes the tenant's whole role
        /// set to the auto-enrolment read, so a test can describe a published service without also making
        /// it auto-assigned at account creation.
        /// </summary>
        public List<Role> PublishedServices { get; }

        /// <summary>
        /// Every assignment the account service DELEGATED to the role service, in order: the tenant, the
        /// role and the request it built. A subscription, a renewal, a trial and each match of a redeemed
        /// invitation code all arrive here.
        /// </summary>
        public List<(int PortalId, int RoleId, RoleAssignmentRequest Request)> DelegatedAssignments { get; }

        /// <summary>Every removal the account service delegated to the role service, in order.</summary>
        public List<(int PortalId, int RoleId, int UserId)> DelegatedRemovals { get; }

        /// <summary>
        /// What the delegated assignment answers. Successful by default; a test that needs the delegate's
        /// own refusal to surface sets it.
        /// </summary>
        public Result DelegatedAssignmentResult { get; set; } = Result.Success();

        /// <summary>What the delegated removal answers. Successful by default.</summary>
        public Result DelegatedRemovalResult { get; set; } = Result.Success();

        public List<ProfilePropertyDefinition> Definitions { get; }

        public ProfilePropertyDefinition? LookupDefinition { get; set; }

        public int? DefinitionNameOwnerId { get; set; }

        public Dictionary<int, List<UserProfileValue>> ValuesByUserId { get; }

        public List<UserRole> UserAssignments { get; }

        public List<ModuleDefinition> ModuleDefinitionCatalogue { get; }

        public List<Module> ModuleInstances { get; }

        public Dictionary<int, List<ModuleSetting>> StoredModuleSettings { get; }

        public bool CredentialExists { get; set; }

        public bool CredentialApproved { get; set; }

        public bool CredentialLockedOut { get; set; }

        public bool CredentialCreated { get; set; }

        public Exception? CredentialFault { get; set; }

        public CredentialWriteOutcome PasswordWritten { get; set; }

        public bool UnlockSucceeded { get; set; }

        public bool ApprovalSucceeded { get; set; }

        public int UserCount { get; set; }

        public int? CallerUserId { get; set; }

        /// <summary>
        /// The tenant the acting caller's token was minted for. Matched against the tenant a credential
        /// operation addresses, because self-service requires the account AND the tenant to agree.
        /// </summary>
        public int? CallerPortalId { get; set; }

        /// <summary>
        /// The stored row the ACTING CALLER resolves to, when a test needs it to differ from the account
        /// under test. Consulted only for the unscoped read the credential-authorisation path performs, so
        /// leaving it null keeps every other test seeing exactly the account it described.
        /// </summary>
        public User? CallerAccount { get; set; }

        public Exception? CommitFault { get; set; }

        public int Commits { get; private set; }

        /// <summary>
        /// The key the store issues to a newly added account when the first commit lands, or <see
        /// langword="null"/> to leave added accounts keyless.
        /// </summary>
        /// <remarks>
        /// Models the one property of a real store that a mocked repository otherwise loses: an identity
        /// key does not exist until the insert commits. The display-name format may substitute the
        /// account's identifier, so a test measuring WHEN the format is applied needs the key to appear at
        /// the same moment it appears in production.
        /// </remarks>
        public int? IssuedUserIdOnCommit { get; set; }

        public int CommitsBeforeCredential { get; private set; }

        public string? CacheKey { get; set; }

        public TimeSpan CacheExpiration { get; private set; }

        public List<User> AddedUsers { get; }

        public List<User> RemovedUsers { get; }

        public List<UserPortal> RemovedMemberships { get; }

        public List<UserRole> RemovedAssignments { get; }

        public List<UserProfileValue> AddedValues { get; }

        public List<UserProfileValue> UpdatedValues { get; }

        public List<ProfilePropertyDefinition> AddedDefinitions { get; }

        public List<int> DeletedDefinitionIds { get; }

        public List<ModuleSetting> AddedSettings { get; }

        public List<string> HashedSecrets { get; }

        public List<(int UserId, string PasswordHash, bool IsApproved, DateTime UtcNow)> CreatedCredentials { get; }

        public List<int> DeletedCredentialUserIds { get; }

        public List<string> SetPasswordHashes { get; }

        /// <summary>The expectation each credential replacement carried, in order.</summary>
        public List<string?> SetPasswordExpectations { get; }

        public List<(int UserId, bool IsApproved)> ApprovalWrites { get; }

        /// <summary>
        /// Every permission cascade the service asked for, in order, as the tenant-and-account pair it
        /// passed. One entry means the cascade was requested exactly once for exactly that account.
        /// </summary>
        public List<(int PortalId, int UserId)> CascadedUserPermissions { get; }

        /// <summary>
        /// The answer the permission cascade gives. Settable so a test can make the cascade refuse and
        /// assert that the deletion is abandoned with the account intact.
        /// </summary>
        public Result CascadeResult { get; set; } = Result.Success();

        /// <summary>
        /// Every portal whose grant-cache entries the service asked the permission contract to evict, in
        /// order. Eviction must happen only after the cascade's single commit has succeeded.
        /// </summary>
        public List<int> EvictedGrantCaches { get; }

        /// <summary>Gets the number of transaction scopes the service opened.</summary>
        public int TransactionsOpened { get; set; }

        /// <summary>Gets the number of transaction scopes the service committed.</summary>
        public int TransactionsCommitted { get; set; }

        public List<int> InvalidatedPortalIds { get; }

        public List<(int PortalId, string UserName)> InvalidatedUsers { get; }

        public List<int> InvalidatedProfileDefinitionsPortalIds { get; }

        /// <summary>
        /// Reads the declaration the harness holds for an identifier, so a test can sharpen one rule
        /// without rebuilding the whole catalogue.
        /// </summary>
        /// <param name="propertyDefinitionId">The declaration identifier.</param>
        /// <returns>The declaration.</returns>
        public ProfilePropertyDefinition DefinitionFor(int propertyDefinitionId)
            => Definitions.Single(definition => definition.PropertyDefinitionId == propertyDefinitionId);

        /// <summary>Reads the settings the harness holds against a module instance.</summary>
        /// <param name="moduleId">The module instance.</param>
        /// <returns>The settings.</returns>
        public List<ModuleSetting> ModuleSettingsFor(int moduleId)
            => StoredModuleSettings.TryGetValue(moduleId, out List<ModuleSetting>? settings)
                ? settings
                : [];

        /// <summary>
        /// Gives the tenant the account-management module instance that membership settings are stored
        /// against, which is what turns the settings surface from absent into present.
        /// </summary>
        public void AddMembershipSettingsSource()
        {
            ModuleDefinitionCatalogue.Add(AccountsDefinition());
            ModuleInstances.Add(new Module
            {
                ModuleId = ModuleId,
                PortalId = PortalId,
                ModuleDefinitionId = AccountsModuleDefinitionId,
            });
            StoredModuleSettings[ModuleId] = [];
        }

        /// <summary>
        /// Publishes a free public service - a role the account may subscribe itself to at no charge.
        /// </summary>
        /// <returns>The published role, so a test may adjust its terms.</returns>
        /// <remarks>
        /// The billing terms are the ones portal provisioning gives a tenant's own system roles, a monthly
        /// frequency with a period, so the role is realistic rather than minimal. The fee is explicitly
        /// zero rather than absent, because a test that needs absence says so.
        /// </remarks>
        public Role PublishFreeService()
        {
            var role = new Role
            {
                RoleId = FreeServiceRoleId,
                PortalId = PortalId,
                RoleName = "Newsletter",
                Description = "Free announcements",
                IsPublic = true,
                ServiceFee = 0m,
                BillingPeriod = 1,
                BillingFrequency = BillingFrequency.Month,
                TrialFee = 0m,
                TrialPeriod = 0,
                TrialFrequency = BillingFrequency.None,
            };

            PublishedServices.Add(role);
            return role;
        }

        /// <summary>
        /// Publishes a public service that charges a recurring fee and offers a free trial - the only shape
        /// for which the legacy trial command was ever rendered.
        /// </summary>
        /// <returns>The published role, so a test may adjust its terms.</returns>
        /// <remarks>
        /// The tenant is also given a payment-processor account, because <c>ShowSubscribe</c>'s second arm
        /// requires one before a paid offer is presented at all; a test asserting the absent case clears
        /// it.
        /// </remarks>
        public Role PublishPaidServiceWithFreeTrial()
        {
            var role = new Role
            {
                RoleId = PaidServiceRoleId,
                PortalId = PortalId,
                RoleName = "Premium",
                Description = "Paid membership",
                IsPublic = true,
                ServiceFee = 12m,
                BillingPeriod = 1,
                BillingFrequency = BillingFrequency.Month,
                TrialFee = 0m,
                TrialPeriod = 14,
                TrialFrequency = BillingFrequency.Day,
            };

            PublishedServices.Add(role);
            PortalRow!.ProcessorUserId = "merchant@example.com";
            return role;
        }

        /// <summary>
        /// Adds a role the tenant does NOT publish but which bears an invitation code, which is the only
        /// way an account can reach it.
        /// </summary>
        /// <returns>The role, so a test may adjust its terms.</returns>
        /// <remarks>
        /// Placed in the tenant's whole-role list rather than in the published catalogue, so it is
        /// invisible to the catalogue read and to the by-identifier lookup's publication test while
        /// remaining resolvable - which is exactly the state the legacy invitation-code search operated on.
        /// </remarks>
        public Role PublishPrivateInvitationOnlyService()
        {
            var role = new Role
            {
                RoleId = PrivateServiceRoleId,
                PortalId = PortalId,
                RoleName = "Founders",
                Description = "By invitation",
                IsPublic = false,
                ServiceFee = 0m,
                BillingPeriod = 1,
                BillingFrequency = BillingFrequency.Year,
                TrialFrequency = BillingFrequency.None,
                RsvpCode = InvitationCode,
            };

            AutoAssigned.Add(role);
            return role;
        }

        /// <summary>Records that the account under test already holds a service.</summary>
        /// <param name="roleId">The role the service is expressed as.</param>
        /// <param name="expiry">When the subscription lapses, or <see langword="null"/> for never.</param>
        /// <param name="trialUsed">
        /// The nullable trial-used flag exactly as the column holds it: absent, false or true, all three of
        /// which the legacy predicate distinguished only as "used" against "not used".
        /// </param>
        public void Subscribe(int roleId, DateTime? expiry, bool? trialUsed = null)
        {
            UserAssignments.Add(new UserRole
            {
                UserRoleId = 500 + roleId,
                UserId = UserId,
                RoleId = roleId,
                EffectiveDate = Now.Date.AddDays(-7),
                ExpiryDate = expiry,
                IsTrialUsed = trialUsed,
            });
        }

        /// <summary>Records a stored setting against the account-management module instance.</summary>
        /// <param name="settingName">The setting name.</param>
        /// <param name="settingValue">The setting value.</param>
        public void StoreSetting(string settingName, string settingValue)
        {
            if (!StoredModuleSettings.TryGetValue(ModuleId, out List<ModuleSetting>? settings))
            {
                settings = [];
                StoredModuleSettings[ModuleId] = settings;
            }

            settings.Add(new ModuleSetting
            {
                ModuleId = ModuleId,
                SettingName = settingName,
                SettingValue = settingValue,
            });
        }

        /// <summary>
        /// Makes the acting caller the account under test, signed in against the tenant under test — which
        /// is what a self-service credential change requires.
        /// </summary>
        public void ActAsTheAccountOwner()
        {
            CallerUserId = UserId;
            CallerPortalId = PortalId;
        }

        /// <summary>
        /// Makes the acting caller a host account: a distinct account, signed in against the tenant under
        /// test, whose own stored row carries the installation-wide super-user flag.
        /// </summary>
        /// <remarks>
        /// The flag is placed on the STORED ROW rather than on the caller's claims, because the service
        /// reads authority from the database for exactly the reason a token cannot be trusted for it: a
        /// token is minted at sign-in and cannot observe an account demoted since.
        /// </remarks>
        public void ActAsAHostAccount()
        {
            CallerUserId = CallerId;
            CallerPortalId = PortalId;
            CallerAccount = new User
            {
                UserId = CallerId,
                Username = "measured_host",
                IsSuperUser = true,
            };
        }

        /// <summary>
        /// Makes the acting caller an administrator of the tenant under test by the only route that confers
        /// it: an in-force assignment to the role the tenant's own <c>AdministratorRoleId</c> column names.
        /// </summary>
        /// <remarks>
        /// Authority is conferred by ROLE KEY and judged AT AN INSTANT, never by role name, because no
        /// unique constraint on <c>Roles.RoleName</c> exists anywhere in the upgrade scripts and the stock
        /// name names a different row in every portal. The assignment is left open-ended so it is in force
        /// at the clock the harness publishes.
        /// </remarks>
        public void ActAsAPortalAdministrator()
        {
            CallerUserId = CallerId;
            CallerPortalId = PortalId;
            CallerAccount = new User
            {
                UserId = CallerId,
                Username = "measured_administrator",
                IsSuperUser = false,
            };

            PortalRow!.AdministratorRoleId = AdministratorRoleId;
            UserAssignments.Add(new UserRole
            {
                UserRoleId = 91,
                UserId = CallerId,
                RoleId = AdministratorRoleId,
            });
        }

        /// <summary>
        /// Builds a harness whose world is consistent: an existing tenant holding one account with a
        /// credential that is present, approved and unlocked, a declared profile catalogue, and a store
        /// that accepts every write.
        /// </summary>
        /// <returns>A wired harness.</returns>
        public static Harness Ready()
        {
            var harness = new Harness();

            harness.Portals
                .Setup(p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalExists);
            harness.Portals
                .Setup(p => p.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRow);
            harness.Portals
                .Setup(p => p.CountUsersAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserCount);

            harness.Users
                .Setup(u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? portalId, int userId, CancellationToken _) =>
                    portalId is null && harness.CallerAccount is User caller && userId == harness.CallerUserId
                        ? caller
                        : harness.LookupUser);
            harness.Users
                .Setup(u => u.GetByUsernameAsync(
                    It.IsAny<int?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserByUsername);
            harness.Users
                .Setup(u => u.UsernameExistsAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UsernameTaken);
            harness.Users
                .Setup(u => u.EmailExistsAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.EmailTaken);
            harness.Users
                .Setup(u => u.GetMembershipAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Membership);
            harness.Users
                .Setup(u => u.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleNamesForUser);
            harness.Users
                .Setup(u => u.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserPage);
            harness.Users
                .Setup(u => u.Add(It.IsAny<User>()))
                .Callback<User>(harness.AddedUsers.Add);
            harness.Users
                .Setup(u => u.Remove(It.IsAny<User>()))
                .Callback<User>(harness.RemovedUsers.Add);
            harness.Users
                .Setup(u => u.RemoveMembership(It.IsAny<UserPortal>()))
                .Callback<UserPortal>(harness.RemovedMemberships.Add);
            harness.Users
                .Setup(u => u.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (
                    harness.CredentialExists,
                    harness.CredentialExists ? StoredHashFor(CurrentPassword) : null,
                    harness.CredentialExists ? PasswordFormat.Hashed : null,
                    harness.CredentialExists ? string.Empty : null,
                    harness.CredentialApproved,
                    harness.CredentialLockedOut));
            harness.Users
                .Setup(u => u.CreateCredentialAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int userId, string hash, bool isApproved, DateTime utcNow, CancellationToken _) =>
                {
                    harness.CommitsBeforeCredential = harness.Commits;
                    if (harness.CredentialFault is Exception fault)
                    {
                        throw fault;
                    }

                    harness.CreatedCredentials.Add((userId, hash, isApproved, utcNow));
                    return Task.FromResult(harness.CredentialCreated);
                });
            harness.Users
                .Setup(u => u.SetPasswordHashAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int _, string hash, string? expected, DateTime __, CancellationToken ___) =>
                {
                    // The EXPECTATION is recorded as well as the hash, because the compare-and-swap is the
                    // whole of what the credential write now guarantees: a test that only observed the hash
                    // could not tell a conditional replacement from the unconditional overwrite this
                    // replaced.
                    harness.SetPasswordHashes.Add(hash);
                    harness.SetPasswordExpectations.Add(expected);
                    return Task.FromResult(harness.PasswordWritten);
                });
            harness.Users
                .Setup(u => u.UnlockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UnlockSucceeded);
            harness.Users
                .Setup(u => u.SetApprovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int userId, bool isApproved, CancellationToken _) =>
                {
                    harness.ApprovalWrites.Add((userId, isApproved));
                    return Task.FromResult(harness.ApprovalSucceeded);
                });
            harness.Users
                .Setup(u => u.DeleteCredentialAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int userId, CancellationToken _) =>
                {
                    harness.DeletedCredentialUserIds.Add(userId);
                    return Task.FromResult(harness.CredentialRemoval);
                });

            // Revocation succeeds by default, because the ordinary case for every operation that ends an
            // account's sessions is that they end. A test that needs the store to refuse says so.
            harness.Tokens
                .Setup(t => t.RevokeAllRefreshTokensAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int userId, CancellationToken _) =>
                {
                    harness.RevokedSessionUserIds.Add(userId);

                    bool refuse = harness.SessionRevocationsBeforeFailure is int allowed
                        ? harness.RevokedSessionUserIds.Count > allowed
                        : !harness.SessionsRevoked;

                    return Task.FromResult(
                        refuse
                            ? Result.Failure("TOKEN_STORE_UNAVAILABLE", "The token store could not be written.")
                            : Result.Success());
                });

            // PRIV-02.
            harness.Tokens
                .Setup(t => t.PurgeAccountSessionRecordsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int userId, int? portalId, CancellationToken _) =>
                {
                    harness.PurgedSessionScopes.Add((userId, portalId));

                    return Task.FromResult(
                        harness.SessionRecordsErased
                            ? Result.Success()
                            : Result.Failure("TOKEN_STORE_UNAVAILABLE", "The token store could not be written."));
                });

            // The declaration catalogue is unpaged and excludes withdrawn declarations, which the
            // repository contract states rather than exposing as a parameter, so the double takes no
            // includeDeleted argument.
            harness.Profiles
                .Setup(p => p.GetDefinitionsByPortalIdAsync(
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Definitions.ToList());
            harness.Profiles
                .Setup(p => p.GetDefinitionByIdAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? portalId, int propertyDefinitionId, CancellationToken _) =>
                {
                    ProfilePropertyDefinition? definition = harness.LookupDefinition;
                    if (definition is null || definition.PropertyDefinitionId != propertyDefinitionId)
                    {
                        return null;
                    }

                    bool inScope = definition.PortalId == portalId;

                    return inScope ? definition : null;
                });

            harness.Profiles
                .Setup(p => p.GetDefinitionByNameAsync(
                    It.IsAny<int?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? _, string name, CancellationToken _) =>
                    harness.DefinitionNameOwnerId is int owner ? Definition(owner, name) : null);

            harness.Profiles
                .Setup(p => p.GetProfileValuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int userId, CancellationToken _) =>
                    harness.ValuesByUserId.TryGetValue(userId, out List<UserProfileValue>? values)
                        ? values.ToList()
                        : []);
            harness.Profiles
                .Setup(p => p.GetProfileValuesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? _, int userId, CancellationToken _) =>
                    harness.ValuesByUserId.TryGetValue(userId, out List<UserProfileValue>? values)
                        ? values.ToList()
                        : []);

            harness.Profiles
                .Setup(p => p.GetProfileValuesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? _, IReadOnlyCollection<int> userIds, CancellationToken _) =>
                    userIds
                        .Distinct()
                        .SelectMany(userId =>
                            harness.ValuesByUserId.TryGetValue(userId, out List<UserProfileValue>? values)
                                ? values
                                : Enumerable.Empty<UserProfileValue>())
                        .ToList());
            harness.Profiles
                .Setup(p => p.DeleteProfileValuesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            harness.Profiles
                .Setup(p => p.AddProfileValueAsync(
                    It.IsAny<UserProfileValue>(),
                    It.IsAny<CancellationToken>()))
                .Returns((UserProfileValue value, CancellationToken _) =>
                {
                    harness.AddedValues.Add(value);
                    return Task.CompletedTask;
                });

            harness.Profiles
                .Setup(p => p.UpdateProfileValueAsync(
                    It.IsAny<UserProfileValue>(),
                    It.IsAny<CancellationToken>()))
                .Returns((UserProfileValue value, CancellationToken _) =>
                {
                    harness.UpdatedValues.Add(value);
                    return Task.CompletedTask;
                });

            harness.Profiles
                .Setup(p => p.AddDefinitionAsync(
                    It.IsAny<ProfilePropertyDefinition>(),
                    It.IsAny<CancellationToken>()))
                .Returns((ProfilePropertyDefinition definition, CancellationToken _) =>
                {
                    harness.AddedDefinitions.Add(definition);
                    return Task.CompletedTask;
                });
            harness.Profiles
                .Setup(p => p.UpdateDefinitionAsync(
                    It.IsAny<ProfilePropertyDefinition>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            harness.UnitOfWork
                .Setup(unit => unit.JoinOrBeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(harness.Transaction.Object);
            harness.Transaction
                .Setup(transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            harness.Portals
                .Setup(portal => portal.TabBelongsToPortalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            harness.Profiles
                .Setup(p => p.DeleteDefinitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int propertyDefinitionId, CancellationToken _) =>
                {
                    harness.DeletedDefinitionIds.Add(propertyDefinitionId);
                    return Task.CompletedTask;
                });

            harness.Roles
                .Setup(r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AutoAssigned.Concat(harness.PublishedServices).ToList());
            harness.Roles
                .Setup(r => r.GetUserRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserAssignments.ToList());
            harness.Roles
                .Setup(r => r.DeleteUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int, CancellationToken>((userId, roleId, _) =>
                {
                    // The contract deletes by the account-and-role pair, so the assignment object the
                    // assertions name is resolved from the world the test described.
                    UserRole? matched = harness.UserAssignments
                        .FirstOrDefault(a => a.UserId == userId && a.RoleId == roleId);
                    harness.RemovedAssignments.Add(matched ?? new UserRole { UserId = userId, RoleId = roleId });
                })
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(r => r.GetSubscribableRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PublishedServices.ToList());

            // A role is resolved from the world the test described, whichever list it was placed in, so a
            // test that publishes a service does not also have to register it as a lookup.
            harness.Roles
                .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int roleId, int portalId, CancellationToken _) =>
                    portalId == PortalId
                        ? harness.PublishedServices.Concat(harness.AutoAssigned)
                            .FirstOrDefault(role => role.RoleId == roleId)
                        : null);

            harness.Roles
                .Setup(r => r.GetUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, int userId, int roleId, CancellationToken _) =>
                    harness.UserAssignments
                        .FirstOrDefault(a => a.UserId == userId && a.RoleId == roleId));

            // THE TWO DELEGATED MEMBERSHIP PRIMITIVES. The account service owns none of the assignment
            // rules - the expiry derivation, the protected bounds, the expire-rather-than-delete retention
            // and the two protected refusals all live on the role service - so what these record is that it
            // asks, with exactly which arguments, and that it never asks when its own gate refuses first.
            harness.RoleService
                .Setup(r => r.AssignUserToRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<RoleAssignmentRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, int roleId, RoleAssignmentRequest request, CancellationToken _) =>
                {
                    harness.DelegatedAssignments.Add((portalId, roleId, request));
                    return harness.DelegatedAssignmentResult;
                });

            harness.RoleService
                .Setup(r => r.RemoveUserFromRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, int roleId, int userId, CancellationToken _) =>
                {
                    harness.DelegatedRemovals.Add((portalId, roleId, userId));
                    return harness.DelegatedRemovalResult;
                });

            // MIGRATION: the account's direct grants live in two tables and the legacy provider declared
            // two members to clear them - DeleteModulePermissionsByUserID, reached from
            // ModulePermissionController.vb:L218, and DeleteTabPermissionsByUserID, reached from
            // TabPermissionController.vb:L209.
            harness.Permissions
                .Setup(p => p.StageUserPermissionRemovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, int userId, CancellationToken _) =>
                {
                    harness.CascadedUserPermissions.Add((portalId, userId));
                    return harness.CascadeResult;
                });
            // THE GRANT-CACHE EVICTION IS ALSO WHERE A POST-COMMIT MAINTENANCE FAILURE IS INJECTED, and it
            // is this call because it is the LAST step after the commit: a fault raised here leaves the
            // deletion committed and the audit record already written, which is precisely the ordering the
            // delete fact asserts.
            harness.Permissions
                .Setup(p => p.InvalidateUserPermissionCaches())
                .Callback(() =>
                {
                    harness.EvictedGrantCaches.Add(PortalId);

                    if (harness.GrantCacheEvictionFault is not null)
                    {
                        throw harness.GrantCacheEvictionFault;
                    }
                });

            // The cascade opens one scope around all five writes, so the scope has to exist: loose behaviour
            // returns null and the await-using would dereference it.
            harness.UnitOfWork
                .Setup(u => u.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Transaction.Object)
                .Callback(() => harness.TransactionsOpened++);
            harness.Transaction
                .Setup(t => t.CommitAsync(It.IsAny<CancellationToken>()))
                .Callback(() => harness.TransactionsCommitted++)
                .Returns(Task.CompletedTask);

            harness.ModuleDefinitions
                .Setup(d => d.GetModuleDefinitionsByPortalIdAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModuleDefinitionCatalogue.ToList());
            harness.ModuleDefinitions
                .Setup(d => d.GetAdministrativeDefinitionByFriendlyNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, string friendlyName, CancellationToken _) =>
                    harness.ModuleDefinitionCatalogue.FirstOrDefault(definition => string.Equals(
                        definition.FriendlyName,
                        friendlyName,
                        StringComparison.OrdinalIgnoreCase)));

            // The module repository contract carries no paging member, so the tenant's modules arrive whole
            // and the service narrows them itself.
            harness.Modules
                .Setup(m => m.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModuleInstances.ToList());
            harness.Modules
                .Setup(m => m.GetModuleSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) => harness.ModuleSettingsFor(moduleId).ToList());
            harness.Modules
                .Setup(m => m.AddModuleSettingAsync(It.IsAny<ModuleSetting>(), It.IsAny<CancellationToken>()))
                .Callback<ModuleSetting, CancellationToken>((setting, _) =>
                {
                    harness.AddedSettings.Add(setting);
                    if (!harness.StoredModuleSettings.TryGetValue(setting.ModuleId, out List<ModuleSetting>? settings))
                    {
                        settings = [];
                        harness.StoredModuleSettings[setting.ModuleId] = settings;
                    }

                    settings.Add(setting);
                })
                .Returns(Task.CompletedTask);

            harness.PasswordHasher
                .Setup(h => h.Hash(It.IsAny<string>()))
                .Returns((string raw) =>
                {
                    harness.HashedSecrets.Add(raw);
                    return StoredHashFor(raw);
                });
            harness.PasswordHasher
                .Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string raw, string hash) => string.Equals(StoredHashFor(raw), hash, StringComparison.Ordinal));

            harness.Clock.SetupGet(c => c.UtcNow).Returns(Now);

            harness.CurrentUser.SetupGet(c => c.IsAuthenticated).Returns(() => harness.CallerUserId is not null);
            harness.CurrentUser.SetupGet(c => c.UserId).Returns(() => harness.CallerUserId);
            harness.CurrentUser.SetupGet(c => c.PortalId).Returns(() => harness.CallerPortalId);

            harness.Cache
                .Setup(c => c.InvalidatePortal(It.IsAny<int>()))
                .Callback<int>(harness.InvalidatedPortalIds.Add);
            harness.Cache
                .Setup(c => c.InvalidateUser(It.IsAny<int>(), It.IsAny<string>()))
                .Callback<int, string>((portalId, userName) => harness.InvalidatedUsers.Add((portalId, userName)));
            harness.Cache
                .Setup(c => c.InvalidateProfileDefinitions(It.IsAny<int>()))
                .Callback<int>(harness.InvalidatedProfileDefinitionsPortalIds.Add);
            harness.Cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<ProfilePropertyDefinitionDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<ProfilePropertyDefinitionDto>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKey = key;
                    harness.CacheExpiration = expiration;
                    return factory(token);
                });

            harness.UnitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    harness.Commits++;
                    if (harness.CommitFault is Exception fault)
                    {
                        throw fault;
                    }

                    // The store issues an identity key AT THE COMMIT, not when the row is staged. A test
                    // that asked for one gets it here, so any work the service does with the key after this
                    // point is measured against a real key rather than against the default zero.
                    if (harness.IssuedUserIdOnCommit is int issued)
                    {
                        foreach (User staged in harness.AddedUsers)
                        {
                            if (staged.UserId == 0)
                            {
                                staged.UserId = issued;
                            }
                        }
                    }

                    return Task.FromResult(1);
                });

            return harness;
        }
    }
}
