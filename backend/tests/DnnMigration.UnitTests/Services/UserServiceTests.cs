using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
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
    /// <summary>
    /// The first key <c>dbo.Portals.PortalID</c> issues, which is a REAL TENANT and not the host scope.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), so -1 is the first tenant an installation has. The host
    /// scope is a SQL <c>NULL</c> portal, which <c>03.03.03.SqlDataProvider:L74-L83</c> established. The
    /// constant is named for what it is so that no assertion below can be read as treating -1 as an
    /// absence.
    /// </remarks>
    private const int FirstTenantPortalId = -1;

    private const int PortalId = 0;

    private const int OtherPortalId = 3;

    private const int UserId = 1;

    private const int OtherUserId = 2;

    private const int AdministratorId = 9;

    /// <summary>
    /// The account that ACTS in a test, kept distinct from every account a test acts upon so that a caller and
    /// a subject can never be confused for one another.
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
    /// Reported when an operation that must end an account's sessions could not, so the operation itself was
    /// abandoned. Its reason token ends in <c>store_unavailable</c>, so the Api edge answers <c>503</c>.
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

    /// <summary>
    /// The refusal a credential change carries when the caller is not the account that owns it.
    /// </summary>
    private const string PasswordChangeSelfOnlyForbiddenCode = "user.password.change-self-only-forbidden";

    /// <summary>
    /// The refusal an administrative reset carries when the caller does not administer the tenant.
    /// </summary>
    private const string PasswordResetForbiddenCode = "user.password.reset-forbidden";

    private const string UnlockNotLockedCode = "user.unlock.not-locked";

    private const string MembershipSelfForbiddenCode = "user.membership.self-forbidden";

    private const string ApprovalUnchangedCode = "user.approval.unchanged";

    private const string PasswordChangeAlreadyRequiredCode = "user.password.change-already-required";

    private const string MembershipSettingsSourceMissingCode = "user.membership-settings.source-missing";

    private const string MembershipSettingsRedirectNotInPortalCode =
        "user.membership-settings.redirect_not_in_portal";

    private const string DisplayNameTooLongCode = "user.display-name.too-long";

    private const string ProfileUnknownPropertyCode = "user.profile.unknown-property";

    private const string ProfileRequiredPropertyMissingCode = "user.profile.required-property-missing";

    private const string ProfileValueTooLongCode = "user.profile.value-too-long";

    private const string ProfilePropertyValidationFailedCode = "user.profile.property-validation-failed";

    private const string ProfileDefinitionDuplicateNameCode = "profile-definition.duplicate-name";

    private const string ProfileDefinitionNotFoundCode = "profile-definition.not-found";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The account contract exposes exactly twenty-one named asynchronous operations, every one of them scoped
    /// to a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inventory is asserted BY NAME rather than by count. A count fails identically for a correct
    /// addition and an incorrect one and names neither, so it forces the next reader to work out which member
    /// moved; a named set says exactly what the contract is and fails with the missing or surplus name in the
    /// message.
    /// </para>
    /// <para>
    /// Two of the twenty are recent and both are named deliberately.
    /// <c>RequiresProfileCompletionAsync</c> lets the sign-in path evaluate the legacy profile gate without
    /// the completeness rule acquiring a second implementation. <c>ResetPasswordAsync</c> exists because the
    /// self-service credential change and the administrative reset were once a single member that chose
    /// between them by reading a discriminator out of the caller's own request body, which let the caller
    /// decide whether the current credential had to be proved; they are two members now precisely so the two
    /// authorisation policies can differ, and this inventory asserts that the split is still in place.
    /// </para>
    /// </remarks>
    [Fact]
    public void UserContract_OffersExactlyTwentyOneNamedTenantScopedOperations()
    {
        MethodInfo[] members = typeof(IUserService).GetMethods();

        members.Select(member => member.Name).Should().BeEquivalentTo(
        [
            "ListUsersAsync",
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
            "UpdateProfileAsync",
            "ListProfilePropertyDefinitionsAsync",
            "GetProfilePropertyDefinitionAsync",
            "CreateProfilePropertyDefinitionAsync",
            "UpdateProfilePropertyDefinitionAsync",
            "DeleteProfilePropertyDefinitionAsync",
        ]);

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
        var permissions = new Mock<IPermissionService>().Object;
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
        var policy = new PasswordPolicyOptions();
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new UserService(null!, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("profiles", () =>
        {
            _ = new UserService(users, null!, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new UserService(users, profiles, null!, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new UserService(users, profiles, roles, null!, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, null!, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("modules", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, null!, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("definitions", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, null!, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, null!, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, null!, hasher, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("passwordHasher", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, null!, clock, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, null!, cache, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, null!, currentUser, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, null!, audit, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, null!, tokens, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("tokens", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, null!, policy, caching);
        });
        Assert.Throws<ArgumentNullException>("passwordPolicy", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new UserService(users, profiles, roles, permissions, portals, modules, definitions, tabs, unitOfWork, hasher, clock, cache, currentUser, audit, tokens, policy, null!);
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
    /// An ordering this listing cannot honour is refused, whichever collection the name belongs to.
    /// </summary>
    /// <param name="field">A field name a caller might reach for.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The shared request validator applies the union of every collection's sortable set, so each of
    /// these names passes it. Each is nonetheless unhonourable HERE, and for a measured reason. The
    /// creation instant, the last sign-in instant, the approval flag and the lock flag are read from the
    /// external membership store AFTER the page has been taken, so ordering by one of them would sort
    /// page two among itself and leave the collection order untouched - not an ordering at all. The
    /// remaining names belong to other collections entirely. Refusing says so; accepting and then
    /// ignoring would hand back a page the caller could not account for and could not detect.
    /// </para>
    /// <para>
    /// MIGRATION: this listing is NOT orderless, and an earlier revision of this fact said it was. The
    /// mapped columns of the entity the query pages over can be ordered by the store, so they are
    /// honoured rather than refused; the companion fact below asserts the accepted half of the same
    /// vocabulary, so the declared set and the ordering the repository actually applies cannot drift
    /// apart in either direction.
    /// </para>
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

    /// <summary>
    /// Every ordering the store can honour is accepted and passed through to it verbatim.
    /// </summary>
    /// <param name="field">A mapped column of the entity the listing pages over.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion of the refusal above, and the reason the pair exists: the declared set and the
    /// ordering the repository applies are two independently editable places, so widening one without the
    /// other would either advertise an ordering that is silently discarded or refuse one that works.
    /// Every name below is honoured by the repository's own ordering member, so each must reach it - which
    /// is asserted by pinning the argument rather than merely by the call succeeding.
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
    /// A request that names no ordering is answered normally, so the refusal above is scoped to an
    /// explicit preference and does not make the listing unusable.
    /// </summary>
    /// <param name="sortBy">The absent-or-blank sort field to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The three spellings are the ones the request contract itself treats as "no preference expressed",
    /// and the refusal is keyed to that same test rather than to a second interpretation of it - so a
    /// caller who sends an empty sort parameter is not asked to justify a field it never named.
    /// </remarks>
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
    /// Hidden tenant columns suppress the profile READS a user-list row would otherwise cost, and leave the
    /// account columns the row already carries exactly as stored.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this asserts the OPPOSITE of an earlier reading of the legacy grid, deliberately. The
    /// legacy settings decided whether a column was RENDERED (<c>UserModuleBase.vb</c>:L98-L115 reads them
    /// and the grid omits the column); they never rewrote the value behind it. Overwriting the value is a
    /// different behaviour and a lossy one - an empty name is indistinguishable from an account that holds
    /// no name, so a caller cannot tell a minimised row from an incomplete one - and it concealed nothing,
    /// because the same settings are published verbatim by the membership-settings read. Deciding whether
    /// to render a column belongs to the client.
    /// </para>
    /// <para>
    /// What the settings still govern is the WORK: address and telephone are profile values costing one
    /// read per row, and that read is skipped entirely when the tenant hides them, which is what the
    /// <c>Times.Never()</c> verification at the end measures. Their absence therefore reports "not
    /// requested" rather than "overwritten", and that distinction is the point of the split.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_SuppressesTheProfileReadsAndKeepsTheAccountColumnsAsStored()
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
        row.Username.Should().Be(Username);

        // Account columns: projected as stored, whatever the tenant's presentation settings say.
        row.FirstName.Should().Be("Grace", "a hidden column is not rendered, not emptied");
        row.LastName.Should().Be("Hopper", "a hidden column is not rendered, not emptied");
        row.DisplayName.Should().Be("Grace B Hopper", "a hidden column is not rendered, not emptied");
        row.Email.Should().Be(Email, "a hidden column is not rendered, not emptied");
        row.CreatedDate.Should().Be(stored.CreatedDate, "a hidden instant is not rendered, not erased");
        row.LastLoginDate.Should().Be(stored.LastLoginDate, "a hidden instant is not rendered, not erased");
        row.IsApproved.Should().BeTrue("a hidden flag is not rendered, and must never be reported as false");

        // Profile values: the per-row read is skipped, so absence here means "not requested".
        row.Address.Should().BeNull("the address read is skipped when the tenant hides the column");
        row.Telephone.Should().BeNull("the telephone read is skipped when the tenant hides the column");

        harness.Profiles.Verify(
            profiles => profiles.GetProfileValuesAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
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
    /// A committed creation is recorded on the audit trail under the legacy event name, with the operator as
    /// the actor and the new account as the subject.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The event name is asserted as a LITERAL rather than through the published constant. The constant
    /// exists so the wording cannot drift from the legacy event-log vocabulary at
    /// <c>EventLogController.vb:L39</c>; comparing it against itself would let a rename pass unnoticed.
    /// </remarks>
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
    /// A creation whose credential the store refuses records nothing, so a record never describes an account
    /// that was withdrawn again.
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
    /// <remarks>
    /// THIS ASSERTION IS INVERTED FROM THE ONE IT REPLACES, deliberately. The earlier revision committed the
    /// account, its membership and its automatic enrolments and then deleted them again in a second commit
    /// when the credential store refused - a routine that could fail for the same reason the credential write
    /// did, and that could not run at all if the process were terminated between the two commits. Both
    /// outcomes left an account that could never be signed in to. The account and its credential are now
    /// written inside ONE transaction, so the reversal is the store's own rollback: the correct assertion is
    /// therefore that no compensating write was issued at all, and that no commit was taken.
    /// </remarks>
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
    /// A credential store that faults rolls the transaction back and reports the fault by kind, without
    /// letting the exception escape as a five-hundred.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateUser_RollsBackAndNamesTheFaultKind()
    {
        Harness harness = Harness.Ready();
        harness.CredentialFault = new TimeoutException("the store did not answer");

        Result<UserDetailDto> outcome = await harness.Service
            .CreateUserAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreateProviderErrorCode);
        outcome.Reason!.Message.Should()
            .Be("The credential store could not be written: TimeoutException.");
        harness.RemovedUsers.Should().BeEmpty();
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Transaction.Verify(transaction => transaction.DisposeAsync(), Times.Once());
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
    /// The computed-width guard is inclusive: a value that occupies all 128 UTF-16 code units of the
    /// column is valid and is committed.
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
    /// A committed removal is recorded under the legacy event name, carrying the two facts the legacy record
    /// carried - the account name and the account identifier - plus which arm of the removal ran.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy call site is <c>UserController.vb:L240</c>:
    /// <c>AddLog("Username", objUser.Username, _portalSettings, objUser.UserID, EventLogType.USER_DELETED)</c>.
    /// </remarks>
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
    /// An operation this entry point does not perform is refused by name rather than silently treated as the
    /// one it does. THE RESET DISCRIMINATOR IS AMONG THEM, and that is the security assertion of this fact:
    /// the self-service change and the administrative reset were once one member that read this discriminator
    /// to decide whether the current credential had to be proved, so a caller who could reach the endpoint
    /// could skip that proof by naming the other operation in its own request body. Refusing it here means
    /// the decision belongs to the endpoint - and therefore to the endpoint's authorisation policy - and can
    /// no longer be chosen by the caller.
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

    /// <summary>
    /// Each entry point matches its own operation without regard to case.
    /// </summary>
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
    /// The administrative reset requires a payload for the same reason.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ResetPassword_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ResetPasswordAsync(PortalId, UserId, null!, CancellationToken.None));
    }

    /// <summary>
    /// An administrative reset is refused when the deployment switched resets off, and is refused before the
    /// account is even read.
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

    /// <summary>
    /// A change requires the current credential and refuses an incorrect one.
    /// </summary>
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
    /// administrative - and is precisely why its endpoint requires administration of the account's own portal
    /// rather than mere authentication. The absent credential check is compensated by an authorisation check,
    /// and this fact together with the refusal above is what proves the two can no longer be confused.
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
    /// A credential change is refused to every caller but the account that owns it, and is refused before the
    /// account is read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Verifying the current credential, which the change branch does, is proof of POSSESSION and not of
    /// AUTHORITY: it establishes that the caller knows the credential, so an administrator acting on somebody
    /// else's account has no business submitting one and uses the reset operation instead.
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
    /// An administrative reset is refused to a caller whose assignment to the tenant's administrator role has
    /// lapsed, and permitted to one whose assignment is in force.
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
    /// An administrative reset is refused when the tenant designates no administrator role, because an unset
    /// designation is a configuration gap and a gap must not grant.
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
    /// A host account may reset a credential in a tenant it holds no membership of, because installation-wide
    /// authority is read from its own stored row rather than from a tenant assignment.
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
    /// A credential change ends every session the account holds, for both operations.
    /// </summary>
    /// <param name="operation">The operation named on the request.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The obligation is stated on the contract in terms rather than left to judgement: a credential changed
    /// because it may have been compromised, or reset because its holder lost it, is of no use to whoever had
    /// it - but a refresh token issued under the old credential keeps yielding fresh access tokens
    /// indefinitely, so a change that left one exchangeable would not end the session it was performed to
    /// end. The reset case matters most: it ends the sessions of the account being reset, not the
    /// administrator's own.
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
        harness.RevokedSessionUserIds.Should().Equal(new[] { UserId });
    }

    /// <summary>
    /// A credential change whose sessions cannot be ended is abandoned, and the stored credential is left
    /// exactly as it was.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The ordering assertion is the substance of this test, not the failure code. Ending the sessions first
    /// and failing leaves an account signed out of sessions it may simply re-establish, which costs its holder
    /// an inconvenience; writing the credential first and then failing to end them would report the operation
    /// as failed while the credential had in fact been replaced and every session was still live - a
    /// falsehood to the caller and the exact exposure the revocation exists to close.
    /// </para>
    /// <para>
    /// The empty hash list is what proves the order, because it can only be empty if the write was never
    /// reached.
    /// </para>
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

    /// <summary>
    /// Withdrawing an approval ends every session the account holds; granting one ends none.
    /// </summary>
    /// <param name="isApproved">The approval state requested.</param>
    /// <param name="expectedRevocations">How many accounts should have their sessions ended.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The asymmetry is deliberate and is stated on the contract: withdrawal ends the account's right to sign
    /// in, so leaving it holding exchangeable refresh tokens would let it keep obtaining access tokens after
    /// the withdrawal; granting takes nothing away, so ending a session because an account gained a right
    /// would be gratuitous.
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

    /// <summary>
    /// Deleting an account ends every session it held, before anything is removed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A refresh token outliving the account it names is the worst of the three cases the obligation covers:
    /// the account is gone, so nothing remains for an administrator to inspect or disable, and yet the token
    /// would still be exchanged for access tokens asserting an identity that no longer exists.
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
    /// A deletion whose sessions cannot be ended removes nothing at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The revocation is the first destructive step precisely so that this is possible: every guard has
    /// passed, so the deletion was going to be attempted, and nothing has yet been removed, so a refusal
    /// leaves the account wholly intact rather than half dismantled. Placing it after the cascade would mean
    /// reporting failure over an account whose grants, assignments and credential had already gone.
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
    /// <remarks>
    /// The cascade stages its removals into the same unit of work as everything below it, and the commit is
    /// still ahead when it answers, so abandoning here leaves the account whole. Swallowing the refusal
    /// would be the one genuinely bad outcome available: the account row would be committed as deleted
    /// while its grants stayed in place, and a later account reusing the identifier would inherit them.
    /// The reason is propagated unchanged because the cascade knows why it refused and this service does
    /// not - restating it as a generic account failure would discard the only useful diagnostic.
    /// </remarks>
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
    /// membership store look exactly like a completed removal, and the account row was then removed anyway -
    /// leaving a credential no administrative screen can reach and no later deletion will revisit. Refusing
    /// before the removal is committed is what keeps the two consistent.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheCredentialCannotBeRemoved_LeavesTheAccountIntact()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.CredentialRemoved = false;

        Result outcome = await harness.Service.DeleteUserAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CredentialRemovalFailedCode);
        harness.RemovedUsers.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Nothing is committed and the scope is disposed, so the grant cascade the permission service staged
        // earlier in this same transaction is rolled back with everything else. That is the whole point of
        // the enclosing transaction: before it existed the cascade committed on its own, so this refusal
        // reported failure over an account that was intact but had lost every grant it held.
        harness.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never());
        harness.Transaction.Verify(t => t.DisposeAsync(), Times.Once());
    }

    /// <summary>
    /// The whole deletion cascade is published by ONE commit inside ONE transaction.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Four stores are written - both grant tables through the permission contract, the assignment table, the
    /// membership table and the account row through the repositories, and the external credential store - and
    /// the contract promises all or none. The assertion that makes that true is this one: exactly one
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
    /// <para>
    /// THE DEFECT THIS PINS. Five writes make up a deletion - the account's direct grants, its role
    /// assignments, its tenant membership, its credential in the external membership store, and the account
    /// row - and they cannot be one <c>SaveChanges</c>, because the credential is written through its own
    /// statement outside the mapped model. An earlier revision had no enclosing scope AND delegated the
    /// permission step to the member that commits on its own, so the grant removal became durable before
    /// anything after it: a credential removal that then failed left the account intact with its grants
    /// already destroyed, and this method reported that the account had been left alone.
    /// </para>
    /// <para>
    /// So the oracle is threefold and each part is load-bearing: exactly one scope is opened, it is committed
    /// exactly once, and the permission contract is asked for STAGING - never for the committing sibling,
    /// which the unit of work would in any case refuse to nest a scope inside.
    /// </para>
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
    /// A cascade abandoned at its last step opens a transaction and never commits it, so disposal rolls the
    /// staged removals back and nothing is evicted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The credential is the one write that leaves the mapped model, so its refusal is the sharpest case: the
    /// grants and assignments have already been staged by the time it answers. Leaving the scope uncommitted
    /// is what undoes them, which is why the abstraction declares no rollback member - the safe outcome is
    /// what disposal does by default, including on a path whose author did not think about failure.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_WhenTheCredentialCannotBeRemoved_RollsTheTransactionBackAndEvictsNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupUser!.UserPortals.Add(new UserPortal { UserPortalId = 1, UserId = UserId, PortalId = PortalId });
        harness.Membership = harness.LookupUser!.UserPortals.First();
        harness.UserAssignments.Add(new UserRole { UserRoleId = 7, UserId = UserId, RoleId = 5 });
        harness.CredentialRemoved = false;

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
    /// Requiring a credential change ends no session, which is a deliberate omission rather than an oversight.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This operation demands a new credential at the next sign-in without replacing the current one, so
    /// nothing an existing session holds has been invalidated and the account may still sign in. The standing
    /// requirement travels on every subsequent exchange, because the sign-in service re-reads the credential
    /// advisories on each one, so the client is told to act on it without being signed out first. The three
    /// operations that DO end sessions are the ones the token contract names, and this is not among them.
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
    /// C-03: a required declaration the account has not answered makes the profile incomplete.
    /// </summary>
    /// <remarks>
    /// This is <c>ProfileController.ValidateProfile</c> (L305-L319): the walk reports invalid at the first
    /// declaration that is required and whose value is empty. An account with NO stored answers at all is the
    /// case a newly created account presents, and it must be reported incomplete rather than complete.
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
    /// C-03: a required declaration answered with whitespace is still unanswered.
    /// </summary>
    /// <remarks>
    /// The legacy compared the value against <c>Null.NullString</c>, which is the EMPTY STRING rather than a
    /// null reference (Rule T7), so it could not distinguish a blank from an absent answer. Treating whitespace
    /// as an answer would let a single space satisfy a required property.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiresProfileCompletion_TreatsWhitespaceAsUnanswered()
    {
        Harness harness = Harness.Ready();
        harness.AddMembershipSettingsSource();
        ProfilePropertyDefinition street = Definition(StreetPropertyId, "Street");
        street.IsRequired = true;
        harness.Definitions.Add(street);
        harness.ValuesByUserId[UserId] = [Value(1, UserId, StreetPropertyId, "   ")];

        Result<bool> outcome = await harness.Service
            .RequiresProfileCompletionAsync(PortalId, UserId, CancellationToken.None);

        outcome.Value.Should().BeTrue();
    }

    /// <summary>
    /// C-03: an answered required declaration makes the profile complete.
    /// </summary>
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

    /// <summary>
    /// C-03: an unanswered declaration that is NOT required leaves the profile complete.
    /// </summary>
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
    /// C-03: a tenant that does not require a valid profile at sign-in is not asked about completeness at all.
    /// </summary>
    /// <remarks>
    /// The setting is consulted FIRST and short-circuits, which is what keeps a tenant that has switched the
    /// gate off from paying for the declaration read on every sign-in.
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
    /// C-03: a tenant with no settings source falls back to the measured default, which requires the profile.
    /// </summary>
    /// <remarks>
    /// Measured rather than guessed: <c>GetUserSettings</c> returned nothing for such a tenant,
    /// <c>UserModuleBase.GetSetting</c> therefore yielded the key's own default, and that default is
    /// <see langword="true"/> (<c>UserModuleBase.vb</c> L175-L177). Skipping the gate instead would silently
    /// disable it for exactly the tenants whose configuration is least complete.
    /// </remarks>
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
    /// another portal receives the same non-enumerating answer as an unknown page, and no setting is staged.
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

        // MIGRATION: RE-POINTED FROM THE READ PROJECTION ONTO THE WRITE SHAPE. This fact was written against
        // an overload taking MembershipSettingsDto - the shape this surface RETURNS - while the write had
        // already been split onto UpdateMembershipSettingsRequest, which is the only shape an endpoint binds.
        // The rule, the refusal and the "nothing staged" assertions are unchanged; only the request type and
        // the reason code follow the surviving surface.
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
    /// <remarks>
    /// MIGRATION: the legacy screen constrained these values through its CHOICE OF CONTROL - a drop-down
    /// bound to an enumeration cannot submit a member the enumeration does not have, and a page picker bound
    /// to the portal's pages cannot offer another tenant's page (UserSettings.ascx.vb:L58-L104). None of that
    /// constraint survives a JSON body, so what the control expressed implicitly is stated explicitly here.
    /// Before these rules existed every one of the twenty-three values reached the store unchecked.
    /// </remarks>
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

        // A real page, owned by a DIFFERENT tenant. This is the case a range rule cannot catch and the one
        // the legacy page picker made unreachable by construction.
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

    /// <summary>
    /// A null redirect member is the legitimate "no redirect" answer and is not looked up at all.
    /// </summary>
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
    /// <remarks>
    /// This is the one setting whose stored value is later EXECUTED, by the registration and profile
    /// validators. Storing an uncompilable expression would therefore leave every subsequent submission
    /// failing inside a validator with no field to name, so the failure belongs to the request that stored it.
    /// </remarks>
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
    /// The out-of-range cases exercised by <see cref="UpdateMembershipSettings_RefusesAValueOutsideItsRange"/>.
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

    /// <summary>Account creation and deletion each own exactly one outer transaction.</summary>
    /// <remarks>
    /// The two paths open that scope through DIFFERENT members, and the difference is the point. Creation
    /// may be composed inside a wider operation - installing a tenant creates its administrator - so it
    /// JOINS an ambient scope when one exists. Deletion is a top-level operation whose whole cascade must be
    /// atomic, and every write inside it stages rather than commits, so it BEGINS its own scope: the unit of
    /// work refuses to nest, which is what proves no suboperation is committing underneath it.
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
    /// The single read passes the tenant it was given straight through, so the first tenant an
    /// installation has - the one keyed -1 - reads its own declaration and not the host scope's.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: this replaces an assertion that encoded a measured defect. The service used to reach a
    /// repository that rewrote a requested portal of -1 into a <c>PortalID IS NULL</c> predicate, so a
    /// declaration physically owned by the tenant keyed -1 read as an absence while the host scope's rows
    /// were served in its place. Because <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), that tenant is a real one, and the scope it names is passed
    /// through unaltered by every layer.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the create half of the same defect. The mapper used to rewrite an incoming -1 into a
    /// null scope, reproducing <c>AddPropertyDefinition</c>'s <c>GetNull</c> wrapper
    /// (<c>SqlDataProvider.vb:L1021</c>). Since <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), that filed a real tenant's declaration where the same
    /// tenant's scoped read could never find it, so a create and the read after it disagreed. This asserts
    /// the stored scope on the staged entity, which is the only place the disagreement is visible.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: this test previously submitted a CONFLICTING owning portal on the body and asserted that the
    /// route won. It can no longer do so, because the create contract carries no portal member at all - the
    /// tenant is a parameter beside the request. That is a stronger guarantee than the one the withdrawn line
    /// asserted: the route cannot be overridden because there is nothing to override it with, and
    /// <c>ProfileDefinitionWriteContractValidatorTests.NeitherWriteContract_AdvertisesAMemberItCannotHonour</c>
    /// is the standing proof that the member has not been re-added. The tenant assertion below is retained
    /// because it is what proves the parameter is actually the value written.
    /// </remarks>
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
    /// restores a withdrawn declaration, so the asymmetry had no recycle-bin behind it. The withdrawn case is
    /// asserted alongside the other two, and against the SAME failure code, because a caller must not be
    /// able to tell the three apart: distinguishing them would confirm that a declaration exists in a tenant
    /// the caller cannot read.
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

        // The name IS held - by the very declaration being edited. The exclusion is therefore a real
        // identifier comparison rather than a flag: the lookup answers with the declaration, and the
        // service permits the write because the holder is the row it is editing.
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
                DefinitionUpdate("Street"),
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
        UpdateProfilePropertyDefinitionRequest request = DefinitionUpdate("Street Address");
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
    /// Removing a declaration refuses one that does not exist, belongs to another tenant, or was already
    /// withdrawn.
    /// </summary>
    /// <remarks>
    /// MIGRATION: THE WITHDRAWN CASE IS THE REGRESSION, AND THIS IS THE VERB WHERE IT MATTERED MOST. This
    /// guard tested only existence and tenancy while the read paths also tested withdrawal, so the one
    /// operation reachable on a declaration the contract refused to show was the destructive one - and
    /// removal here is physical, discarding every stored answer with it, so it destroyed data no caller could
    /// have inspected first. The assertion checks the staged removal did not happen as well as the reported
    /// code, because a refusal that still removed the row would satisfy the code alone.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteProfilePropertyDefinition_RefusesTheUnknownTheForeignAndTheWithdrawn()
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

        ProfilePropertyDefinition withdrawnDefinition = Definition(StreetPropertyId, "Street");
        withdrawnDefinition.IsDeleted = true;
        withdrawnDefinition.ProfileValues.Add(Value(1, UserId, StreetPropertyId, "Fleet Street"));
        harness.LookupDefinition = withdrawnDefinition;

        Result withdrawn = await harness.Service
            .DeleteProfilePropertyDefinitionAsync(PortalId, StreetPropertyId, CancellationToken.None);

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
    /// Gives the harness the caller the named operation is available to, so a theory that exercises both
    /// operations states the authority each one needs instead of quietly relying on one caller passing both.
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
    /// MIGRATION: the two operations are two MEMBERS, not one member switching on the submitted
    /// discriminator. Whether the current credential is verified is now the entry point's answer rather than
    /// the caller's, which is what stops a caller from selecting the branch that skips verification; the
    /// discriminator survives only as a cross-check, and naming the operation the other member performs is
    /// refused rather than obeyed. A theory that submitted "reset" to the change member would therefore be
    /// asserting the cross-check rather than the reset behaviour it means to assert, so it dispatches here.
    /// </remarks>
    private static Task<Result> PerformCredentialWriteAsync(
        Harness harness,
        string? operation,
        ChangePasswordRequest request) =>
        string.Equals(operation, ChangePasswordRequest.OperationReset, StringComparison.OrdinalIgnoreCase)
            ? harness.Service.ResetPasswordAsync(PortalId, UserId, request, CancellationToken.None)
            : harness.Service.ChangePasswordAsync(PortalId, UserId, request, CancellationToken.None);

    /// <summary>
    /// Builds a declaration payload.
    /// </summary>
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
    /// <remarks>
    /// MIGRATION: the two verbs bind two request types because the terminal procedures honour different
    /// member sets - the insert declares a module-definition key that the update does not - so the two
    /// builders exist rather than one. They are otherwise identical, which is what keeps a test that asserts
    /// the same behaviour on both paths comparing like with like.
    /// </remarks>
    private static UpdateProfilePropertyDefinitionRequest DefinitionUpdate(string propertyName) => new()
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
            RevokedSessionUserIds = [];
            SetPasswordHashes = [];
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
            Transaction = new Mock<ITransactionScope>(MockBehavior.Loose);

            // Both account-lifecycle workflows open ONE explicit transaction - creation so that the account
            // row and its external credential are published together, deletion so that the grant cascade,
            // the assignments, the membership, the account row and the credential removal are all-or-nothing.
            // Loose behaviour would hand back a null scope and the await-using would dereference it, so this
            // stub is required rather than decorative.
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
                PasswordPolicy,
                Caching);
        }

        public UserService Service { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IUserProfileRepository> Profiles { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IPermissionService> Permissions { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<IModuleDefinitionRepository> ModuleDefinitions { get; }

        /// <summary>
        /// The page repository, read only to prove that a membership-settings redirect target belongs to the
        /// tenant being written.
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
        /// Accounts whose sessions the service asked to have ended, in the order it asked.
        /// </summary>
        public List<int> RevokedSessionUserIds { get; }

        /// <summary>
        /// Whether the token store can end an account's sessions. Defaults to true.
        /// </summary>
        public bool SessionsRevoked { get; set; } = true;

        /// <summary>
        /// Whether the credential store can remove an account's credential. Defaults to true.
        /// </summary>
        public bool CredentialRemoved { get; set; } = true;

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
        /// Makes the acting caller the account under test, signed in against the tenant under test — which is
        /// what a self-service credential change requires.
        /// </summary>
        /// <remarks>
        /// This is deliberately NOT the harness default. Most members of this service are administrative and
        /// refuse a transition an administrator aimed at their own account, so a harness that silently made the
        /// caller the account under test would make those refusals fire everywhere and hide what each test
        /// meant to measure. The acting caller is therefore stated by the tests that depend on one.
        /// </remarks>
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
        /// The flag is placed on the STORED ROW rather than on the caller's claims, because the service reads
        /// authority from the database for exactly the reason a token cannot be trusted for it: a token is
        /// minted at sign-in and cannot observe an account demoted since. A host account is read without a
        /// tenant scope, so it is published through <see cref="CallerAccount"/> rather than through
        /// <see cref="LookupUser"/>, which continues to describe the account under test.
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
        /// Authority is conferred by ROLE KEY and judged AT AN INSTANT, never by role name, because no unique
        /// constraint on <c>Roles.RoleName</c> exists anywhere in the upgrade scripts and the stock name names
        /// a different row in every portal. The assignment is left open-ended so it is in force at the clock
        /// the harness publishes.
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
                    return Task.FromResult(harness.CredentialRemoved);
                });

            // Revocation succeeds by default, because the ordinary case for every operation that ends an
            // account's sessions is that they end. A test that needs the store to refuse says so.
            harness.Tokens
                .Setup(t => t.RevokeAllRefreshTokensAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int userId, CancellationToken _) =>
                {
                    harness.RevokedSessionUserIds.Add(userId);
                    return Task.FromResult(
                        harness.SessionsRevoked
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

                    // MIGRATION: the scope is matched EXACTLY, as the repository now matches it. This
                    // double previously recognised -1 as a request for the SQL-null host rows, which
                    // reproduced the defect the repository carried: dbo.Portals.PortalID is
                    // IDENTITY(-1, 1) (01.00.00.SqlDataProvider:L77), so -1 is the first real tenant of
                    // an installation and addresses its own rows. null is the host scope and nothing
                    // else is.
                    bool inScope = definition.PortalId == portalId;

                    return inScope ? definition : null;
                });

            // The name lookup answers with the DECLARATION, as the legacy provider member did, so a
            // caller editing a declaration can tell a real clash from the row it is already editing.
            // DefinitionNameOwnerId names which declaration currently holds the submitted name, and
            // null leaves the name free.
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

            // The auto-assignment set is now selected by the service from the portal's own roles, which
            // is what the legacy caller did over GetPortalRoles - the membership provider had no
            // auto-assigned procedure. The harness therefore publishes the portal's roles and lets the
            // service apply the flag, so AutoAssigned still describes the world the test intends.
            harness.Roles
                .Setup(r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AutoAssigned.ToList());
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

            // MIGRATION: the account's direct grants live in two tables and the legacy provider declared
            //            two members to clear them - DeleteModulePermissionsByUserID, reached from
            //            ModulePermissionController.vb:L218, and DeleteTabPermissionsByUserID, reached from
            //            TabPermissionController.vb:L209. The service under test no longer issues those two
            //            table deletes itself: it asks the permission contract that owns both tables to
            //            release the account, so what is stubbed and recorded here is that single request.
            //            That both tables are in fact cleared, and that grants held THROUGH A ROLE are left
            //            alone, is that contract's guarantee and is asserted against its own implementation
            //            rather than restated against a mock here - a mock cannot verify a promise it makes
            //            up. What these tests own is that the account service asks exactly once, for
            //            exactly the tenant and account being deleted, and abandons the deletion when the
            //            answer is a refusal.
            //
            //            THE MEMBER ASKED FOR IS THE STAGE-ONLY ONE, and that is a correctness property
            //            rather than a naming detail. The whole cascade runs inside one transaction, so a
            //            permission step that committed on its own would make the grant removal durable
            //            ahead of everything after it - and a credential removal failing afterwards would
            //            then leave an account intact but stripped of its grants, while this method reported
            //            that the account had been left alone.
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
            harness.Permissions
                .Setup(p => p.InvalidateUserPermissionCachesAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((portalId, _) => harness.EvictedGrantCaches.Add(portalId))
                .Returns(Task.CompletedTask);

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

                    return Task.FromResult(1);
                });

            return harness;
        }
    }
}
