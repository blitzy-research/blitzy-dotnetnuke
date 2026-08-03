using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Moq;
using Xunit;
using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Covers the account workflow: the split between the tracked account row and the external credential store,
/// the membership transitions an administrator may apply, the profile reconciliation, and the settings that
/// are stored against a module instance rather than against the tenant.
/// </summary>
/// <remarks>
/// <para>
/// An account is two things in two places. The row in the tenant's own table is tracked and committed through
/// the unit of work; the credential is held in the external membership store and is reached by explicit
/// statements. Every member below is asserted against both, because the interesting failures are exactly the
/// ones where the two disagree: an account row with no credential, a credential the store refuses after the
/// row has already committed, and a membership transition that the row would accept but the credential store
/// reports as already applied. The transitions deliberately do not commit the unit of work at all — they
/// write through the credential store and then correct the loaded row so the answer a caller reads back is
/// consistent.
/// </para>
/// <para>
/// Membership settings are not tenant columns. They are module settings held against the tenant's single
/// account-management module instance, which is why reading them can legitimately answer nothing and why
/// writing them can legitimately fail with nowhere to write. That indirection reaches further than it looks:
/// the display-name format and the default profile visibility both come from it, so an account update and a
/// profile read both depend on a module instance existing. Those couplings are asserted rather than assumed.
/// </para>
/// </remarks>
public class UserServiceTests
{
    private const int PortalId = 0;

    private const int OtherPortalId = 3;

    private const int UserId = 1;

    private const int OtherUserId = 2;

    private const int AdministratorId = 9;

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

    private const string PasswordCurrentIncorrectCode = "user.password.current-incorrect";

    private const string PasswordResetNotEnabledCode = "user.password.reset-not-enabled";

    private const string PasswordUnsupportedOperationCode = "user.password.unsupported-operation";

    private const string UnlockNotLockedCode = "user.unlock.not-locked";

    private const string MembershipSelfForbiddenCode = "user.membership.self-forbidden";

    private const string ApprovalUnchangedCode = "user.approval.unchanged";

    private const string PasswordChangeAlreadyRequiredCode = "user.password.change-already-required";

    private const string MembershipSettingsSourceMissingCode = "user.membership-settings.source-missing";

    private const string ProfileUnknownPropertyCode = "user.profile.unknown-property";

    private const string ProfileRequiredPropertyMissingCode = "user.profile.required-property-missing";

    private const string ProfileValueTooLongCode = "user.profile.value-too-long";

    private const string ProfilePropertyValidationFailedCode = "user.profile.property-validation-failed";

    private const string ProfileDefinitionDuplicateNameCode = "profile-definition.duplicate-name";

    private const string ProfileDefinitionNotFoundCode = "profile-definition.not-found";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The account contract exposes eighteen asynchronous operations, every one of them scoped to a tenant.
    /// </summary>
    [Fact]
    public void UserContract_OffersExactlyEighteenTenantScopedOperations()
    {
        MethodInfo[] members = typeof(IUserService).GetMethods();

        members.Should().HaveCount(18);
        foreach (MethodInfo member in members)
        {
            member.Name.Should().EndWith("Async");
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue();
            member.GetParameters()[0].Name.Should().Be("portalId");
            member.GetParameters()[^1].ParameterType.Should().Be(typeof(CancellationToken));
        }
    }

    /// <summary>
    /// The service refuses to be constructed without every collaborator it depends on.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var users = new Mock<IUserRepository>().Object;
        var profiles = new Mock<IUserProfileRepository>().Object;
        var roles = new Mock<IRoleRepository>().Object;
        var permissions = new Mock<IPermissionRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var modules = new Mock<IModuleRepository>().Object;
        var definitions = new Mock<IModuleDefinitionRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var hasher = new Mock<IPasswordHasher>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var policy = new PasswordPolicyOptions();
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new UserService(null!, profiles, roles, permissions, portals, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("profiles", () =>
        {
            _ = new UserService(users, null!, roles, permissions, portals, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new UserService(users, profiles, null!, permissions, portals, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new UserService(users, profiles, roles, null!, portals, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, null!, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("modules", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, null!, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("definitions", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, null!, unitOfWork, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, null!, hasher, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("passwordHasher", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, unitOfWork, null!, clock, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, unitOfWork, hasher, null!, cache, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, unitOfWork, hasher, clock, null!, currentUser, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, unitOfWork, hasher, clock, cache, null!, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("passwordPolicy", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, unitOfWork, hasher, clock, cache, currentUser, policy, null!);
        });
    }

    /// <summary>
    /// Listing accounts requires a paging request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUsers_RequiresAPagingRequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListUsersAsync(PortalId, null!, cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Malformed paging is refused as a request fault rather than as an outcome, because coordinates outside
    /// the permitted range are a caller error rather than a state the tenant can be in.
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
    /// Two filters at once are refused, because the store applies one and a caller would not be able to tell
    /// which.
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
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A search term of only white space is treated as absent.
    /// </summary>
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
    }

    /// <summary>
    /// A request that asks for no page size receives an unpaged answer; a paged request keeps the store's own
    /// coordinates.
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
    /// Creating an account requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateUserAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// An account with no name or no address is refused before anything is read, because both are columns the
    /// row cannot be written without.
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

    /// <summary>
    /// An account cannot be created in a tenant that does not exist.
    /// </summary>
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
    /// A configured requirement for non-alphanumeric characters is enforced, and is skipped entirely when the
    /// requirement is nothing — which is what the legacy deployment configured.
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
    /// A configured strength rule is applied, and a rule that cannot be applied refuses the credential rather
    /// than admitting it, because failing open would silently drop the requirement.
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
    /// An account name held elsewhere in the installation is refused, and the reason distinguishes an account
    /// that is already a member of this tenant from one that merely holds the name.
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
        harness.AutoAssigned.Add(new Role { RoleId = 5, RoleName = "Registered Users" });
        harness.AutoAssigned.Add(new Role { RoleId = 6, RoleName = "Subscribers" });

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
    /// A credential the store refuses withdraws the account row that had already committed, so no account is
    /// left that cannot sign in.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_WithdrawsTheCommittedRowWhenTheCredentialIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.AutoAssigned.Add(new Role { RoleId = 5, RoleName = "Registered Users" });
        harness.CredentialCreated = false;

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreateDuplicateUsernameCode);
        outcome.Reason!.Message.Should().Be(
            $"The credential store already holds a credential for account name \"{Username}\".");

        harness.RemovedAssignments.Should().ContainSingle();
        harness.RemovedMemberships.Should().ContainSingle();
        harness.RemovedUsers.Should().ContainSingle().Which.Should().BeSameAs(harness.AddedUsers.Single());
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        harness.InvalidatedPortalIds.Should().BeEmpty();
    }

    /// <summary>
    /// A credential store that faults withdraws the row and reports the fault by kind, without letting the
    /// exception escape as a five-hundred.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_WithdrawsTheRowAndNamesTheFaultKind()
    {
        Harness harness = Harness.Ready();
        harness.CredentialFault = new TimeoutException("the store did not answer");

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreateProviderErrorCode);
        outcome.Reason!.Message.Should()
            .Be("The credential store could not be written: TimeoutException.");
        harness.RemovedUsers.Should().ContainSingle();
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

        harness.RemovedUsers.Should().BeEmpty();
    }

    /// <summary>
    /// A created account records its creation and credential moments and is not locked, and both caches that
    /// could hold a stale answer are discarded.
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
    /// The submitted names and address are applied, and a display name that was left out is derived from the
    /// two given names.
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
    /// A tenant that configured a display-name format has it applied over whatever was submitted, because the
    /// format is a tenant-wide presentation rule rather than a per-account choice.
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
    /// A tenant with no account-management module instance has no display-name format, so the submitted name
    /// stands rather than the update failing.
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
    /// A conflict wrapped in another exception is still recognised, because the store may surface it through
    /// a wrapper rather than directly.
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

    /// <summary>
    /// A fault that is not a conflict is not absorbed, because retrying would not help.
    /// </summary>
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

    /// <summary>
    /// Deleting an unknown account is refused.
    /// </summary>
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
    /// The host-account rule is checked before the administrator rule, so the reason a caller is given names
    /// the stronger protection.
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
        harness.DeletedPermissions.Should().Equal(new[] { (PortalId, UserId) });
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
    /// Changing a credential requires a request, and an operation the service does not implement is refused
    /// by name rather than silently treated as one of the two it does.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete")]
    [InlineData("change-question-and-answer")]
    public async Task ChangePassword_RefusesAnUnsupportedOperation(string? operation)
    {
        Harness harness = Harness.Ready();
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordUnsupportedOperationCode);
        outcome.Reason!.Message.Should()
            .Be($"Operation \"{operation}\" is not supported; use \"change\" or \"reset\".");
    }

    /// <summary>
    /// The two supported operations are matched without regard to case.
    /// </summary>
    /// <param name="operation">The operation spelling to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("CHANGE")]
    [InlineData("Reset")]
    public async Task ChangePassword_MatchesTheSupportedOperationsWithoutRegardToCase(string operation)
    {
        Harness harness = Harness.Ready();
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A request requires a payload.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ChangePasswordAsync(PortalId, UserId, null!, CancellationToken.None));
    }

    /// <summary>
    /// An administrative reset is refused when the deployment switched resets off, and is refused before the
    /// account is even read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RefusesAResetWhenResetsAreSwitchedOff()
    {
        Harness harness = Harness.Ready();
        harness.PasswordPolicy.PasswordResetEnabled = false;
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = ChangePasswordRequest.OperationReset;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

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

    /// <summary>
    /// A change requires the current credential and refuses an incorrect one; a reset does not ask for it at
    /// all, which is what makes it administrative.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_RequiresTheCurrentCredentialOnlyForAChange()
    {
        Harness harness = Harness.Ready();

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

        ChangePasswordRequest reset = ValidChangeRequest();
        reset.Operation = ChangePasswordRequest.OperationReset;
        reset.CurrentPassword = null;

        Result administrative = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, reset, CancellationToken.None);

        administrative.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A new credential that matches the stored one is refused, on both operations, because a change that
    /// changes nothing would report success without improving anything.
    /// </summary>
    /// <param name="operation">The operation to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("change")]
    [InlineData("reset")]
    public async Task ChangePassword_RefusesANewCredentialThatMatchesTheStoredOne(string operation)
    {
        Harness harness = Harness.Ready();
        ChangePasswordRequest request = ValidChangeRequest();
        request.Operation = operation;
        request.NewPassword = CurrentPassword;
        request.ConfirmPassword = CurrentPassword;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

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
        harness.PasswordWritten = false;

        Result outcome = await harness.Service
            .ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PasswordResetFailedCode);
        outcome.Reason!.Message.Should().Be("The credential store refused the change.");
    }

    /// <summary>
    /// A successful change hashes the new credential, records the moment on the loaded row, and discards the
    /// account's cache.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ChangePassword_HashesRecordsAndDiscardsTheAccountCache()
    {
        Harness harness = Harness.Ready();

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

        await harness.Service.ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        harness.LookupUser!.UpdatePassword = true;

        await harness.Service.ChangePasswordAsync(PortalId, UserId, ValidChangeRequest(), CancellationToken.None);

        harness.LookupUser!.UpdatePassword.Should().BeFalse();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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
    /// A transition against another administrator's account is permitted, so the self rule is genuinely about
    /// the acting account rather than about administrators in general.
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
    /// Unlocking refuses an unknown account, an account with no credential, and an account that is not
    /// locked, in that order.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UnlockUser_RefusesAnUnknownAccountANoCredentialAccountAndAnUnlockedAccount()
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

        harness.CredentialExists = true;
        harness.CredentialLockedOut = false;

        Result notLocked = await harness.Service.UnlockUserAsync(PortalId, UserId, CancellationToken.None);
        notLocked.Reason!.Code.Should().Be(UnlockNotLockedCode);
        notLocked.Reason!.Message.Should().Be($"Account {UserId} is not locked.");
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

    /// <summary>
    /// Setting approval refuses an unknown account and an account that holds no credential.
    /// </summary>
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

    /// <summary>
    /// A store that reports the account away between the read and the write is reported as absent.
    /// </summary>
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
    /// A tenant with no account-management module instance reports no membership settings rather than
    /// failing, because a tenant is allowed not to have one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetMembershipSettings_ReportsNothingWhenTheTenantHasNoAccountModule()
    {
        Harness harness = Harness.Ready();

        Result<MembershipSettingsDto?> outcome = await harness.Service
            .GetMembershipSettingsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
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
    /// A redirect page recorded as the out-of-range sentinel is read back as no redirect at all, which is how
    /// the legacy screen recorded "no page chosen".
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
            new MembershipSettingsDto(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MembershipSettingsSourceMissingCode);
        outcome.Reason!.Message.Should().Be(
            $"Portal {PortalId} has no \"User Accounts\" module instance to store membership settings against.");
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

        var settings = new MembershipSettingsDto
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
            new MembershipSettingsDto(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MembershipSettingsSourceMissingCode);
    }

    /// <summary>
    /// Reading a profile reports absence for an unknown account, and otherwise reports one entry per declared
    /// property whether or not the account recorded a value for it.
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
    /// Writing a profile requires a payload, and an unknown account is refused.
    /// </summary>
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
    /// A required property that was omitted or submitted blank is refused by name, so a profile cannot be
    /// saved incomplete.
    /// </summary>
    /// <param name="submittedValue">The value to submit, or null to omit the property entirely.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateProfile_RefusesAnOmittedOrBlankRequiredProperty(string? submittedValue)
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
    /// Writing a profile is a replacement rather than a merge: a submitted property is written, a property
    /// that was stored and not submitted is removed, and a submitted property that was never stored is added.
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
        // The omitted City answer is BLANKED rather than deleted - the legacy write path had no
        // delete for a value row, and clearing was an upsert carrying an empty value. Both stored
        // rows are therefore staged as updates, and the one that was dropped from the submission
        // reads back empty.
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

        // The entity derives nothing, so the effective value is the coalesce the legacy read
        // procedure performs - the bounded column when it is not null, the overflow column
        // otherwise. Asserted here in that order to prove the row round-trips the whole value.
        (written.PropertyValue ?? written.PropertyText).Should().Be(oversize);
    }

    /// <summary>
    /// A value that exactly fills the column stays in it, so the boundary is inclusive.
    /// </summary>
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

    /// <summary>
    /// A successful profile write commits once and discards the account's cache.
    /// </summary>
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

    /// <summary>
    /// The declaration catalogue is read through the cache under a tenant-keyed name, with a lifetime scaled
    /// by the configured multiplier, and is read straight through when caching is disabled.
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
    /// The catalogue is ordered by the display order the tenant chose, falling back to the identifier so the
    /// order is total.
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

    /// <summary>
    /// A declaration that exists in this tenant and was not withdrawn is projected.
    /// </summary>
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
                new ProfilePropertyDefinitionDto { PropertyName = submittedName },
                CancellationToken.None));

        failure.Message.Should().Be("A profile property name is required.");
        harness.AddedDefinitions.Should().BeEmpty();
    }

    /// <summary>
    /// A name the tenant already declares is refused.
    /// </summary>
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
    /// A declaration is created against the tenant from the route, is not withdrawn, and the catalogue cache
    /// is discarded so the new property becomes visible.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateProfilePropertyDefinition_CreatesAgainstTheRoutesTenantAndDiscardsTheCatalogue()
    {
        Harness harness = Harness.Ready();
        ProfilePropertyDefinitionDto request = DefinitionRequest("Nickname");
        request.PortalId = OtherPortalId;
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
    /// Changing a declaration requires a payload, refuses one that does not exist or belongs to another
    /// tenant, and requires a name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_RefusesTheUnknownTheForeignAndTheNameless()
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
                DefinitionRequest("Street"),
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
                DefinitionRequest("Street"),
                CancellationToken.None);

        foreign.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");

        DomainException nameless = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                new ProfilePropertyDefinitionDto { PropertyName = "  " },
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

        // The name IS held - by the very declaration being edited. The exclusion is therefore a real
        // identifier comparison rather than a flag: the lookup answers with the declaration, and the
        // service permits the write because the holder is the row it is editing.
        harness.DefinitionNameOwnerId = StreetPropertyId;

        Result<ProfilePropertyDefinitionDto> permitted = await harness.Service
            .UpdateProfilePropertyDefinitionAsync(
                PortalId,
                StreetPropertyId,
                DefinitionRequest("Street"),
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
                DefinitionRequest("City"),
                CancellationToken.None);

        refused.Reason!.Code.Should().Be(ProfileDefinitionDuplicateNameCode);
    }

    /// <summary>
    /// A competing write on a declaration is reported as a conflict to retry.
    /// </summary>
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
                DefinitionRequest("Street"),
                CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PersistenceConflictCode);
        outcome.Reason!.Message.Should()
            .Be("The definition was changed by another request; reload it and try again.");
    }

    /// <summary>
    /// A successful change applies the submitted shape and discards the catalogue cache.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfilePropertyDefinition_AppliesTheShapeAndDiscardsTheCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        ProfilePropertyDefinitionDto request = DefinitionRequest("Street Address");
        request.Length = 120;
        request.Required = true;
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
        harness.LookupDefinition!.IsRequired.Should().BeTrue();
        harness.LookupDefinition!.IsVisible.Should().BeFalse();
        harness.LookupDefinition!.ViewOrder.Should().Be(3);
        harness.LookupDefinition!.PropertyCategory.Should().Be("Address");
        harness.LookupDefinition!.ValidationExpression.Should().Be(".+");
        harness.InvalidatedProfileDefinitionsPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// Withdrawing a declaration refuses one that does not exist or belongs to another tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_RefusesTheUnknownAndTheForeign()
    {
        Harness harness = Harness.Ready();
        harness.LookupDefinition = null;

        Result unknown = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);
        unknown.IsFailure.Should().BeTrue();
        unknown.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);

        harness.LookupDefinition = Definition(StreetPropertyId, "Street");
        harness.LookupDefinition.PortalId = OtherPortalId;

        Result foreign = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);
        foreign.Reason!.Code.Should().Be(ProfileDefinitionNotFoundCode);
        harness.DeletedDefinitionIds.Should().BeEmpty();
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
            .DeleteProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        // The declaration is withdrawn by identifier, matching the legacy procedure, and its two
        // recorded answers travel with it: FK_UserProfile_ProfilePropertyDefinition is declared
        // ON DELETE CASCADE and the repository loads the answers before staging the removal, so the
        // service issues no per-answer deletion of its own and none is asserted here.
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
    /// Builds the account fixture the store returns.
    /// </summary>
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

    /// <summary>
    /// Builds a profile-value row.
    /// </summary>
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

    /// <summary>
    /// Builds a profile-property declaration.
    /// </summary>
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

    /// <summary>
    /// Builds the module definition that membership settings are stored against.
    /// </summary>
    /// <returns>A module definition row.</returns>
    private static ModuleDefinition AccountsDefinition() => new()
    {
        ModuleDefinitionId = AccountsModuleDefinitionId,
        FriendlyName = MembershipSettingsDto.UserAccountsModuleDefinitionName,
        DesktopModuleId = 1,
    };

    /// <summary>
    /// Builds a well-formed account-creation request.
    /// </summary>
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

    /// <summary>
    /// Builds a well-formed account-update request.
    /// </summary>
    /// <returns>An update request.</returns>
    private static UpdateUserRequest ValidUpdateRequest() => new()
    {
        FirstName = "Grace",
        LastName = "Hopper",
        DisplayName = "Grace B Hopper",
        Email = "grace.hopper@example.com",
    };

    /// <summary>
    /// Builds a well-formed credential-change request.
    /// </summary>
    /// <returns>A credential-change request.</returns>
    private static ChangePasswordRequest ValidChangeRequest() => new()
    {
        Operation = ChangePasswordRequest.OperationChange,
        CurrentPassword = CurrentPassword,
        NewPassword = NewPassword,
        ConfirmPassword = NewPassword,
    };

    /// <summary>
    /// Builds a declaration payload.
    /// </summary>
    /// <param name="propertyName">The declared name.</param>
    /// <returns>A declaration payload.</returns>
    private static ProfilePropertyDefinitionDto DefinitionRequest(string propertyName) => new()
    {
        PropertyName = propertyName,
        PropertyCategory = "Contact",
        Visible = true,
    };

    /// <summary>
    /// Builds a profile submission from a set of declaration identifiers and values.
    /// </summary>
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
        /// <summary>
        /// Initialises a new instance of the <see cref="DbUpdateConcurrencyException"/> class.
        /// </summary>
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
            PasswordWritten = true;
            UnlockSucceeded = true;
            ApprovalSucceeded = true;
            UserCount = 3;

            AddedUsers = [];
            RemovedUsers = [];
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
            SetPasswordHashes = [];
            ApprovalWrites = [];
            DeletedPermissions = [];
            InvalidatedPortalIds = [];
            InvalidatedUsers = [];
            InvalidatedProfileDefinitionsPortalIds = [];

            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Profiles = new Mock<IUserProfileRepository>(MockBehavior.Loose);
            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            ModuleDefinitions = new Mock<IModuleDefinitionRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);

            Service = new UserService(
                Users.Object,
                Profiles.Object,
                Roles.Object,
                Permissions.Object,
                Portals.Object,
                Modules.Object,
                ModuleDefinitions.Object,
                UnitOfWork.Object,
                PasswordHasher.Object,
                Clock.Object,
                Cache.Object,
                CurrentUser.Object,
                PasswordPolicy,
                Caching);
        }

        public UserService Service { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IUserProfileRepository> Profiles { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<IModuleDefinitionRepository> ModuleDefinitions { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

        public Mock<IClock> Clock { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

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

        public bool PasswordWritten { get; set; }

        public bool UnlockSucceeded { get; set; }

        public bool ApprovalSucceeded { get; set; }

        public int UserCount { get; set; }

        public int? CallerUserId { get; set; }

        public Exception? CommitFault { get; set; }

        public int Commits { get; private set; }

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

        public List<(int UserId, bool IsApproved)> ApprovalWrites { get; }

        public List<(int PortalId, int UserId)> DeletedPermissions { get; }

        public List<int> InvalidatedPortalIds { get; }

        public List<(int PortalId, string UserName)> InvalidatedUsers { get; }

        public List<int> InvalidatedProfileDefinitionsPortalIds { get; }

        /// <summary>
        /// Reads the declaration the harness holds for an identifier, so a test can sharpen one rule without
        /// rebuilding the whole catalogue.
        /// </summary>
        /// <param name="propertyDefinitionId">The declaration identifier.</param>
        /// <returns>The declaration.</returns>
        public ProfilePropertyDefinition DefinitionFor(int propertyDefinitionId)
            => Definitions.Single(definition => definition.PropertyDefinitionId == propertyDefinitionId);

        /// <summary>
        /// Reads the settings the harness holds against a module instance.
        /// </summary>
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
        /// Records a stored setting against the account-management module instance.
        /// </summary>
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
        /// Builds a harness whose world is consistent: an existing tenant holding one account with a
        /// credential that is present, approved and unlocked, a declared profile catalogue, and a store that
        /// accepts every write.
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
                .ReturnsAsync(() => harness.LookupUser);
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
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int _, string hash, DateTime __, CancellationToken ___) =>
                {
                    harness.SetPasswordHashes.Add(hash);
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
                    return Task.FromResult(true);
                });

            // The declaration catalogue is unpaged and excludes withdrawn declarations, which the
            // repository contract states rather than exposing as a parameter, so the double takes no
            // includeDeleted argument.
            harness.Profiles
                .Setup(p => p.GetDefinitionsByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Definitions.ToList());
            harness.Profiles
                .Setup(p => p.GetDefinitionByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.LookupDefinition);

            // The name lookup answers with the DECLARATION, as the legacy provider member did, so a
            // caller editing a declaration can tell a real clash from the row it is already editing.
            // DefinitionNameOwnerId names which declaration currently holds the submitted name, and
            // null leaves the name free.
            harness.Profiles
                .Setup(p => p.GetDefinitionByNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, string name, CancellationToken _) =>
                    harness.DefinitionNameOwnerId is int owner ? Definition(owner, name) : null);

            harness.Profiles
                .Setup(p => p.GetProfileValuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int userId, CancellationToken _) =>
                    harness.ValuesByUserId.TryGetValue(userId, out List<UserProfileValue>? values)
                        ? values.ToList()
                        : []);
            harness.Profiles
                .Setup(p => p.AddProfileValueAsync(
                    It.IsAny<UserProfileValue>(),
                    It.IsAny<CancellationToken>()))
                .Returns((UserProfileValue value, CancellationToken _) =>
                {
                    harness.AddedValues.Add(value);
                    return Task.CompletedTask;
                });

            // An answer the submitted set omits is BLANKED through the update member rather than
            // deleted, because no legacy member ever removed a UserProfile row.
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

            // Removal is addressed by identifier, matching the legacy procedure, and the answers
            // recorded against the declaration cascade inside the repository rather than being
            // removed one at a time by the service.
            harness.Profiles
                .Setup(p => p.DeleteDefinitionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int propertyDefinitionId, CancellationToken _) =>
                {
                    harness.DeletedDefinitionIds.Add(propertyDefinitionId);
                    return Task.CompletedTask;
                });

            harness.Roles
                .Setup(r => r.ListAutoAssignedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AutoAssigned.ToList());
            harness.Roles
                .Setup(r => r.ListUserAssignmentsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserAssignments.ToList());
            harness.Roles
                .Setup(r => r.RemoveAssignment(It.IsAny<UserRole>()))
                .Callback<UserRole>(harness.RemovedAssignments.Add);

            harness.Permissions
                .Setup(p => p.DeleteUserPermissionsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int portalId, int userId, CancellationToken _) =>
                {
                    harness.DeletedPermissions.Add((portalId, userId));
                    return Task.FromResult(0);
                });

            harness.ModuleDefinitions
                .Setup(d => d.GetModuleDefinitionsByPortalIdAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModuleDefinitionCatalogue.ToList());

            harness.Modules
                .Setup(m => m.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => PagedResult<Module>.Unpaged(harness.ModuleInstances.ToList()));
            harness.Modules
                .Setup(m => m.ListSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) => harness.ModuleSettingsFor(moduleId).ToList());
            harness.Modules
                .Setup(m => m.AddSetting(It.IsAny<ModuleSetting>()))
                .Callback<ModuleSetting>(setting =>
                {
                    harness.AddedSettings.Add(setting);
                    if (!harness.StoredModuleSettings.TryGetValue(setting.ModuleId, out List<ModuleSetting>? settings))
                    {
                        settings = [];
                        harness.StoredModuleSettings[setting.ModuleId] = settings;
                    }

                    settings.Add(setting);
                });

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

            harness.CurrentUser.SetupGet(c => c.UserId).Returns(() => harness.CallerUserId);

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

                    return Task.FromResult(1);
                });

            return harness;
        }
    }
}
