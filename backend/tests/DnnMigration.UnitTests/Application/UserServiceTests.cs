using System.Globalization;
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

namespace DnnMigration.UnitTests.Application;

/// <summary>
/// Pins the account aggregate's ported legacy contract: the four by-reference arguments that became
/// returned results, the eighteen-member status ladder that became a reason catalogue, the four search
/// procedures that became one parameter set, the untyped collections that became typed envelopes, and the
/// absence marker that must not be mistaken for a date.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS SUITE IS FOR, AND WHAT IT DELIBERATELY LEAVES ALONE. A sibling suite already exercises the
/// account WORKFLOW in full - the two-stage creation and its compensation, the credential-write
/// authorisation ladder, session revocation, profile overflow columns and definition management. Repeating
/// any of it here would buy nothing and would leave two suites free to disagree about one rule. This suite
/// asserts what is invisible to a workflow test because it is a property of the TRANSLATION rather than of
/// the behaviour: that a filter named for account names still reaches the parameter that matches account
/// names and not its same-typed neighbour, that "return every row" still means every row rather than none,
/// that one legacy boolean refusal now says which of three things went wrong, and that the number the
/// legacy code wrote to mean "no date" has not become a date in the year one. A transposition between two
/// adjacent same-typed arguments is the specific fault this file exists to catch, because neither the
/// compiler nor a workflow test can see one.
/// </para>
/// <para>
/// Mocked collaborators only, and only the domain and application abstractions the service declares.
/// Nothing here reaches a persistence context, a query root or SQL text: the context is internal to the
/// infrastructure project and the application layer cannot name it, so a test in this project could not
/// construct one even deliberately. Hashing and token minting are substituted and only their ORCHESTRATION
/// is observed, because the algorithms themselves are verified against their real implementations by the
/// security suites. Nothing reads the ambient clock.
/// </para>
/// <para>
/// EXPLICITLY OWNED ELSEWHERE, AND NOT RESTATED HERE. The eighteen creation-outcome ordinals and the seven
/// sign-in ordinals are pinned by the domain enumeration suite; the sign-in verdict ladder, the two
/// promotions that accompany an acceptance rather than refusing it, and the audit names that survive the
/// PascalCase rename are pinned by the sign-in suite; the shipped credential policy and its rule-for-rule
/// parity with the legacy configuration are pinned by the policy and validator suites; and every entity
/// projection is pinned by the mapping suite. What this suite adds is the SERVICE-side half of those same
/// translations, which none of them can see: that the account service's own refusal vocabulary is one code
/// per legacy outcome, that it draws no outcome from a CLR default, and that its request contract offers no
/// member the legacy surface had and the target deliberately dropped.
/// </para>
/// </remarks>
public class UserServiceApplicationTests
{
    /// <summary>
    /// The tenant these assertions address, chosen as the identity seed on purpose.
    /// </summary>
    /// <remarks>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c>, so -1 is simultaneously the first
    /// identifier the column ever issues AND the value the legacy null contract used to mean "absent"
    /// (<c>Library/Components/Shared/Null.vb</c>, <c>NullInteger</c>). Addressing it by default keeps that
    /// collision under test on every path rather than only on the one test that names it.
    /// </remarks>
    private const int SeedPortalId = -1;

    /// <summary>
    /// The account these assertions address.
    /// </summary>
    private const int AccountId = 42;

    /// <summary>
    /// The account that acts, which is deliberately never <see cref="AccountId"/>: the legacy audit record
    /// carried a single user field and so could not tell an administrator apart from the account they
    /// changed.
    /// </summary>
    private const int ActingAccountId = 7;

    /// <summary>
    /// The sign-in name of the account under test.
    /// </summary>
    private const string AccountName = "member.one";

    /// <summary>
    /// The electronic-mail address of the account under test.
    /// </summary>
    private const string AccountEmail = "member.one@example.test";

    /// <summary>
    /// A deliberately fake stand-in for a submitted credential. Seven characters and purely alphanumeric,
    /// which is exactly what the preserved legacy policy admits, so it passes on the happy path without a
    /// test having to relax the policy. It is not a real credential and matches no provider's format.
    /// </summary>
    private const string FakeSubmittedCredential = "notreal";

    /// <summary>
    /// A deliberately fake stand-in for a stored one-way hash. It is not a credential, cannot be one, and
    /// matches no provider's format.
    /// </summary>
    private const string FakeStoredHash = "REDACTED_PASSWORD_HASH";

    /// <summary>
    /// The definition name the legacy settings read located its module instance by
    /// (<c>UserController.vb:L662</c>), restated here as a literal so a rename of the contract constant
    /// cannot silently move the settings source.
    /// </summary>
    private const string AccountsModuleDefinitionName = "User Accounts";

    /// <summary>
    /// The legacy settings cache key shape, <c>SettingsKey(portalId)</c> at <c>UserController.vb:L924</c> -
    /// <c>"UserSettings|" + portalId.ToString</c> - preserved as a literal so the decision not to
    /// reintroduce that cache stays auditable against the original.
    /// </summary>
    private const string LegacySettingsCacheKeyPrefix = "UserSettings|";

    /// <summary>
    /// The value the legacy unpaged overloads passed for page index, page size and total alike
    /// (<c>UserController.vb:L686</c>, <c>L705</c>), meaning "return every row, do not page".
    /// </summary>
    private const int LegacyUnpagedArgument = -1;

    /// <summary>
    /// The page size that carries the legacy unpaged request in the target contract.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy marker was -1 and the target marker is 0, because the request contract
    /// declares a plain <see cref="int"/> whose absent value is zero and the paged envelope reports
    /// <c>IsUnpaged</c> by testing the same. The translation therefore has to be asserted rather than
    /// assumed: a mapping that carried -1 through unchanged would be refused by the negative-size guard,
    /// and one that substituted the default page size would silently answer a first page where the caller
    /// asked for the whole collection.
    /// </remarks>
    private const int UnpagedPageSize = 0;

    /// <summary>
    /// Each refusal the account-creation member can reach, paired with the legacy
    /// <c>UserCreateStatus</c> member it stands for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This table is the substance of the creation assertions. The legacy member returned one of eighteen
    /// enumeration values and mutated its argument to carry the account
    /// (<c>UserController.vb:L156</c>); the target returns a result whose reason carries a code. The
    /// mapping between the two is what a reviewer needs in order to check a target refusal against the
    /// legacy procedure without holding both in their head, so it is written down once here and asserted
    /// from one place.
    /// </para>
    /// <para>
    /// The legacy statuses NOT present are absent for stated reasons rather than by omission.
    /// <c>AddUser</c> (0) is the not-yet-attempted seed and is never an outcome. <c>InvalidAnswer</c> (6)
    /// and <c>InvalidQuestion</c> (10) guarded the recovery question-and-answer pair, which the preserved
    /// policy does not require and the target does not store. <c>DuplicateProviderUserKey</c> (4) and
    /// <c>InvalidProviderUserKey</c> (9) named a provider-assigned key that the target has no analogue
    /// for. <c>UnexpectedError</c> (14) and <c>UserRejected</c> (15) were never returned by the ported
    /// path. <c>Success</c> (13) is not a refusal.
    /// </para>
    /// </remarks>
    private static readonly (UserCreateStatus LegacyStatus, string ReasonCode)[] CreationRefusals =
    [
        (UserCreateStatus.InvalidUserName, "user.create.invalid-username"),
        (UserCreateStatus.InvalidEmail, "user.create.invalid-email"),
        (UserCreateStatus.InvalidPassword, "user.create.invalid-password"),
        (UserCreateStatus.PasswordMismatch, "user.create.password-mismatch"),
        (UserCreateStatus.AddUserToPortal, "user.create.portal-assignment-failed"),
        (UserCreateStatus.UserAlreadyRegistered, "user.create.user-already-registered"),
        (UserCreateStatus.UsernameAlreadyExists, "user.create.username-already-exists"),
        (UserCreateStatus.DuplicateEmail, "user.create.duplicate-email"),
        (UserCreateStatus.DuplicateUserName, "user.create.duplicate-username"),
        (UserCreateStatus.ProviderError, "user.create.provider-error"),
    ];

    /// <summary>
    /// The three search procedures the legacy surface offered, each paired with the argument name the one
    /// target member carries it on.
    /// </summary>
    /// <remarks>
    /// The legacy grid dispatched to exactly one of four procedures per postback:
    /// <c>GetUsers</c> (<c>L725</c>, <c>L746</c>), <c>GetUsersByEmail</c> (<c>L769</c>, <c>L793</c>),
    /// <c>GetUsersByUserName</c> (<c>L816</c>, <c>L840</c>) and <c>GetUsersByProfileProperty</c>
    /// (<c>L864</c>, <c>L889</c>) - eight overloads in all, every one of them returning an
    /// <c>ArrayList</c> and reporting its grand total through a by-reference argument. The unfiltered
    /// shape is the fourth and takes no filter, which is why only three appear here.
    /// </remarks>
    private static readonly string[] LegacySearchShapes =
    [
        "GetUsersByUserName",
        "GetUsersByEmail",
        "GetUsersByProfileProperty",
    ];

    /// <summary>
    /// Every collaborator of <see cref="UserService"/>, stood up as a mock, together with the recordings
    /// the assertions read back.
    /// </summary>
    /// <remarks>
    /// Nested inside the test class on purpose. A shared fixture file would couple this suite to its
    /// siblings, and the whole value of these assertions is that they pin one aggregate's translation
    /// independently of anything else in the project.
    /// </remarks>
    private sealed class Subject
    {
        private Subject()
        {
            // Both account-lifecycle workflows now open ONE explicit transaction - creation so that the
            // account row and its external credential are published together, deletion so that the grant
            // cascade, the assignments, the membership, the account row and the credential removal are
            // all-or-nothing. Loose behaviour would hand back a null scope and the await-using would
            // dereference it, so this stub is required rather than decorative.
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
                Definitions.Object,
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

        /// <summary>Gets the service under test.</summary>
        public UserService Service { get; }

        /// <summary>Gets the account repository mock.</summary>
        public Mock<IUserRepository> Users { get; } = new();

        /// <summary>Gets the profile repository mock.</summary>
        public Mock<IUserProfileRepository> Profiles { get; } = new();

        /// <summary>Gets the role repository mock.</summary>
        public Mock<IRoleRepository> Roles { get; } = new();

        /// <summary>Gets the permission contract mock, which owns the grant cascade.</summary>
        public Mock<IPermissionService> Permissions { get; } = new();

        /// <summary>
        /// Gets the role contract mock, reached only by the member-services operations and only for the
        /// two membership primitives they delegate.
        /// </summary>
        public Mock<IRoleService> RoleService { get; } = new();

        /// <summary>Gets the tenant repository mock.</summary>
        public Mock<IPortalRepository> Portals { get; } = new();

        /// <summary>Gets the module repository mock, which holds the settings source.</summary>
        public Mock<IModuleRepository> Modules { get; } = new();

        /// <summary>Gets the module-definition repository mock.</summary>
        public Mock<IModuleDefinitionRepository> Definitions { get; } = new();

        /// <summary>
        /// Gets the page repository mock, read only to prove that a membership-settings redirect target
        /// belongs to the tenant being written.
        /// </summary>
        public Mock<ITabRepository> Tabs { get; } = new();

        /// <summary>Gets the unit-of-work mock, which owns the single transactional commit point.</summary>
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        /// <summary>
        /// Gets the transaction-scope mock the unit of work hands back, so a multi-write cascade can open,
        /// commit and dispose one under test.
        /// </summary>
        public Mock<ITransactionScope> Transaction { get; } = new();

        /// <summary>Gets the hashing mock, observed for orchestration only.</summary>
        public Mock<IPasswordHasher> PasswordHasher { get; } = new();

        /// <summary>Gets the clock mock, so no assertion ever reads the real time of day.</summary>
        public Mock<IClock> Clock { get; } = new();

        /// <summary>Gets the cache mock.</summary>
        public Mock<ICacheService> Cache { get; } = new();

        /// <summary>Gets the acting-caller mock.</summary>
        public Mock<ICurrentUser> CurrentUser { get; } = new();

        /// <summary>Gets the audit sink mock.</summary>
        public Mock<IAuditSink> Audit { get; } = new();

        /// <summary>Gets the token contract mock, which ends the sessions a deletion invalidates.</summary>
        public Mock<ITokenService> Tokens { get; } = new();

        /// <summary>
        /// Gets the store-failure classifier mock. Loose by default, so it answers <c>false</c> for every
        /// exception and the account-creation guard absorbs nothing unless a test says the store failed.
        /// </summary>
        public Mock<IStoreFailureClassifier> StoreFailures { get; } = new();
        /// <summary>
        /// Gets the tenant-facts holder the detail read consults before falling back to a portal read.
        /// </summary>
        /// <remarks>
        /// Left reporting itself unresolved, which is what a caller outside a request scope genuinely is, so
        /// every case here exercises the persistence fall-back exactly as it did before the holder existed.
        /// </remarks>
        public Mock<IPortalContextHolder> PortalContext { get; } = new();
        /// <summary>
        /// Gets the private diagnostics mock, which receives the anomalies a caller is not told about -
        /// among them the CLR type of a credential-store fault, which must never reach a failure message.
        /// </summary>
        public Mock<ISecurityDiagnostics> Diagnostics { get; } = new();

        /// <summary>Gets the bound credential policy, taken as a plain settings object by the service.</summary>
        public PasswordPolicyOptions PasswordPolicy { get; } = new();

        /// <summary>Gets the bound caching settings, taken as a plain settings object by the service.</summary>
        public CachingOptions Caching { get; } = new();

        /// <summary>Gets the fixed instant the clock reports.</summary>
        public static DateTime Instant { get; } = new(2030, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        /// <summary>Gets the accounts staged for insertion, in the order they were staged.</summary>
        public List<User> StagedAccounts { get; } = [];

        /// <summary>Gets the accounts withdrawn, in the order they were withdrawn.</summary>
        public List<User> WithdrawnAccounts { get; } = [];

        /// <summary>Gets the audit records the service emitted, in order.</summary>
        public List<AuditEvent> AuditEvents { get; } = [];

        /// <summary>Gets the ordered log of the calls the sequencing assertions read back.</summary>
        public List<string> CallLog { get; } = [];

        /// <summary>Gets the tenant identifiers whose cache entries were discarded, in order.</summary>
        public List<int> DiscardedTenants { get; } = [];

        /// <summary>Gets the account cache entries discarded, in order.</summary>
        public List<(int PortalId, string UserName)> DiscardedAccounts { get; } = [];

        /// <summary>Gets or sets the account the repository answers a read with.</summary>
        public User? StoredAccount { get; set; }

        /// <summary>Gets or sets the tenant the repository answers a read with.</summary>
        public Portal? StoredTenant { get; set; }

        /// <summary>Gets or sets the roles the tenant declares.</summary>
        public IReadOnlyList<Role> TenantRoles { get; set; } = [];

        /// <summary>Gets or sets the page the account repository answers a listing with.</summary>
        public PagedResult<User> AccountPage { get; set; } = PagedResult<User>.Empty;

        /// <summary>Gets or sets the page the account repository answers a picker read with.</summary>
        public PagedResult<AccountChoice> ChoicePage { get; set; } = PagedResult<AccountChoice>.Empty;

        /// <summary>Gets or sets the profile declarations the tenant holds.</summary>
        public IReadOnlyList<ProfilePropertyDefinition> ProfileDeclarations { get; set; } = [];

        /// <summary>Gets or sets the settings stored against the account module instance.</summary>
        public IReadOnlyList<ModuleSetting> StoredSettings { get; set; } = [];

        /// <summary>Gets or sets the module definitions the tenant declares.</summary>
        public IReadOnlyList<ModuleDefinition> ModuleDefinitions { get; set; } = [];

        /// <summary>Gets or sets the module instances the tenant holds.</summary>
        public IReadOnlyList<Module> ModuleInstances { get; set; } = [];

        /// <summary>Gets the arguments the account listing forwarded to the store, once it did.</summary>
        public ListingArguments? Listing { get; private set; }

        /// <summary>Gets the arguments the account picker forwarded to the store, once it did.</summary>
        public ChoiceArguments? ChoiceListing { get; private set; }

        /// <summary>Gets the number of times the single commit point was reached.</summary>
        public int CommitCount { get; private set; }

        /// <summary>
        /// The arguments of <c>IUserRepository.ListAsync</c>, captured verbatim so an assertion can prove
        /// each legacy filter landed on its own parameter.
        /// </summary>
        /// <param name="PortalId">The tenant the page was taken within.</param>
        /// <param name="PageIndex">The zero-based page index.</param>
        /// <param name="PageSize">The page size, zero meaning unpaged.</param>
        /// <param name="Query">The free-text search term, or absent.</param>
        /// <param name="UserNamePrefix">The account-name prefix, or absent.</param>
        /// <param name="EmailPrefix">The electronic-mail prefix, or absent.</param>
        /// <param name="ProfilePropertyDefinitionId">The resolved profile declaration, or absent.</param>
        /// <param name="ProfilePropertyValuePrefix">The profile-value prefix, or absent.</param>
        /// <param name="IsApproved">The approval filter, or absent.</param>
        /// <param name="IncludeUnauthorised">Whether unapproved accounts are admitted.</param>
        /// <param name="IncludeSuperUsers">Whether installation-wide accounts are admitted.</param>
        /// <param name="SortBy">The ordering field, or absent.</param>
        /// <param name="Descending">Whether the ordering is descending.</param>
        public sealed record ListingArguments(
            int PortalId,
            int PageIndex,
            int PageSize,
            string? Query,
            string? UserNamePrefix,
            string? EmailPrefix,
            int? ProfilePropertyDefinitionId,
            string? ProfilePropertyValuePrefix,
            bool? IsApproved,
            bool IncludeUnauthorised,
            bool IncludeSuperUsers,
            string? SortBy,
            bool Descending);

        /// <summary>
        /// The arguments of <c>IUserRepository.ListAccountChoicesAsync</c>, captured verbatim.
        /// </summary>
        /// <param name="PortalId">The tenant the page was taken within.</param>
        /// <param name="PageIndex">The zero-based page index.</param>
        /// <param name="PageSize">The page size, zero meaning unpaged.</param>
        /// <param name="NamePrefix">The caption prefix, or absent.</param>
        /// <param name="SortBy">The ordering caption, or absent.</param>
        /// <param name="Descending">Whether the ordering is reversed.</param>
        public sealed record ChoiceArguments(
            int PortalId,
            int PageIndex,
            int PageSize,
            string? NamePrefix,
            string? SortBy,
            bool Descending);

        /// <summary>
        /// Builds a subject whose every collaborator answers plausibly, so a test need only override the
        /// one fact it is about.
        /// </summary>
        /// <returns>A ready subject.</returns>
        public static Subject Ready()
        {
            var subject = new Subject();

            subject.StoredAccount = StoredRow();
            subject.StoredTenant = StoredTenantRow();

            subject.Clock.SetupGet(clock => clock.UtcNow).Returns(Instant);

            subject.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
            subject.CurrentUser.SetupGet(caller => caller.UserId).Returns(ActingAccountId);
            subject.CurrentUser.SetupGet(caller => caller.UserName).Returns("administrator");
            subject.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(true);

            subject.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns(FakeStoredHash);
            subject.PasswordHasher.Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(true);
            subject.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(false);

            subject.Portals.Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            subject.Portals.Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.StoredTenant);

            subject.Users.Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.StoredAccount);
            subject.Users.Setup(users => users.GetByUsernameAsync(
                    It.IsAny<int?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            subject.Users.Setup(users => users.UsernameExistsAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            subject.Users.Setup(users => users.EmailExistsAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            subject.Users.Setup(users => users.GetMembershipAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new UserPortal
                {
                    UserPortalId = 900,
                    PortalId = SeedPortalId,
                    UserId = AccountId,
                    CreatedDate = Instant,
                });
            subject.Users.Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());
            subject.Users.Setup(users => users.GetCredentialStateAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    true,
                    FakeStoredHash,
                    PasswordFormat.Hashed,
                    string.Empty,
                    true,
                    false));
            subject.Users.Setup(users => users.CreateCredentialAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true)
                .Callback(() => subject.CallLog.Add("credential.create"));
            subject.Users.Setup(users => users.DeleteCredentialAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true)
                .Callback(() => subject.CallLog.Add("credential.delete"));
            subject.Users.Setup(users => users.Add(It.IsAny<User>()))
                .Callback<User>(account =>
                {
                    subject.StagedAccounts.Add(account);
                    subject.CallLog.Add("account.add");
                });
            subject.Users.Setup(users => users.Remove(It.IsAny<User>()))
                .Callback<User>(account =>
                {
                    subject.WithdrawnAccounts.Add(account);
                    subject.CallLog.Add("account.remove");
                });
            subject.Users.Setup(users => users.RemoveMembership(It.IsAny<UserPortal>()))
                .Callback(() => subject.CallLog.Add("membership.remove"));
            subject.Users.Setup(users => users.ListAsync(
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
                .ReturnsAsync(() => subject.AccountPage)
                .Callback(new InvocationAction(invocation => subject.Listing = new ListingArguments(
                    (int)invocation.Arguments[0]!,
                    (int)invocation.Arguments[1]!,
                    (int)invocation.Arguments[2]!,
                    (string?)invocation.Arguments[3],
                    (string?)invocation.Arguments[4],
                    (string?)invocation.Arguments[5],
                    (int?)invocation.Arguments[6],
                    (string?)invocation.Arguments[7],
                    (bool?)invocation.Arguments[8],
                    (bool)invocation.Arguments[9]!,
                    (bool)invocation.Arguments[10]!,
                    (string?)invocation.Arguments[11],
                    (bool)invocation.Arguments[12]!)));
            subject.Users.Setup(users => users.ListAccountChoicesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.ChoicePage)
                .Callback(new InvocationAction(invocation => subject.ChoiceListing = new ChoiceArguments(
                    (int)invocation.Arguments[0]!,
                    (int)invocation.Arguments[1]!,
                    (int)invocation.Arguments[2]!,
                    (string?)invocation.Arguments[3],
                    (string?)invocation.Arguments[4],
                    (bool)invocation.Arguments[5]!)));

            subject.Roles.Setup(roles => roles.GetByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.TenantRoles)
                .Callback(() => subject.CallLog.Add("roles.read"));
            subject.Roles.Setup(roles => roles.GetUserRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<UserRole>());
            subject.Roles.Setup(roles => roles.DeleteUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback(() => subject.CallLog.Add("assignment.delete"));

            // The account-deletion cascade calls the STAGE-ONLY member, never its committing sibling: the
            // whole cascade is one transaction and a suboperation that committed inside it would make the
            // sequence partially durable. The call log records the staging so the ordering assertions can
            // prove it happens before the single commit.
            subject.Permissions.Setup(permissions => permissions.StageUserPermissionRemovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success())
                .Callback(() => subject.CallLog.Add("grants.delete"));
            subject.Permissions.Setup(permissions => permissions.InvalidateUserPermissionCaches())
                .Callback(() => subject.CallLog.Add("grants.evict"));

            // Loose behaviour hands back a null scope, which the await-using would then dereference, so this
            // stub is required rather than decorative.
            subject.UnitOfWork.Setup(unit => unit.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.Transaction.Object)
                .Callback(() => subject.CallLog.Add("transaction.begin"));
            subject.Transaction.Setup(transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback(() => subject.CallLog.Add("transaction.commit"));

            subject.Tokens.Setup(tokens => tokens.RevokeAllRefreshTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success())
                .Callback(() => subject.CallLog.Add("sessions.end"));

            // PRIV-02. Logged like every other step, because the ORDER is what these facts measure: the
            // erasure must follow the commit, never precede it.
            subject.Tokens.Setup(tokens => tokens.PurgeAccountSessionRecordsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success())
                .Callback(() => subject.CallLog.Add("sessions.erase"));

            subject.Profiles.Setup(profiles => profiles.GetDefinitionsByPortalIdAsync(
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.ProfileDeclarations);
            subject.Profiles.Setup(profiles => profiles.GetProfileValuesAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<UserProfileValue>());
            subject.Profiles.Setup(profiles => profiles.GetProfileValuesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<UserProfileValue>());

            // The batched read a listing performs answers as emptily as the single-account read above, which
            // is what these facts assert against: this harness declares no stored profile answers at all.
            subject.Profiles.Setup(profiles => profiles.GetProfileValuesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<UserProfileValue>());
            subject.Profiles.Setup(profiles => profiles.DeleteProfileValuesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback(() => subject.CallLog.Add("profile-values.delete"));

            subject.Definitions.Setup(definitions => definitions.GetModuleDefinitionsByPortalIdAsync(
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.ModuleDefinitions)
                .Callback(() => subject.CallLog.Add("definitions.read"));
            subject.Definitions.Setup(definitions => definitions.GetAdministrativeDefinitionByFriendlyNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, string friendlyName, CancellationToken _) =>
                    subject.ModuleDefinitions.FirstOrDefault(definition => string.Equals(
                        definition.FriendlyName,
                        friendlyName,
                        StringComparison.OrdinalIgnoreCase)))
                .Callback(() => subject.CallLog.Add("definitions.read"));
            subject.Modules.Setup(modules => modules.GetByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.ModuleInstances)
                .Callback(() => subject.CallLog.Add("modules.read"));
            subject.Modules.Setup(modules => modules.GetModuleSettingsAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.StoredSettings)
                .Callback(() => subject.CallLog.Add("settings.read"));

            subject.UnitOfWork.Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1)
                .Callback(() =>
                {
                    subject.CommitCount++;
                    subject.CallLog.Add("commit");
                });
            subject.UnitOfWork.Setup(unit => unit.JoinOrBeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(subject.Transaction.Object)
                .Callback(() => subject.CallLog.Add("transaction.begin-or-join"));
            subject.Transaction.Setup(scope => scope.CommitAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Callback(() => subject.CallLog.Add("transaction.commit"));

            subject.Portals.Setup(portals => portals.TabBelongsToPortalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            subject.Cache.Setup(cache => cache.InvalidatePortal(It.IsAny<int>()))
                .Callback<int>(portalId =>
                {
                    subject.DiscardedTenants.Add(portalId);
                    subject.CallLog.Add("cache.tenant");
                });
            subject.Cache.Setup(cache => cache.InvalidateUser(It.IsAny<int>(), It.IsAny<string>()))
                .Callback<int, string>((portalId, userName) =>
                {
                    subject.DiscardedAccounts.Add((portalId, userName));
                    subject.CallLog.Add("cache.account");
                });
            subject.Audit.Setup(audit => audit.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(recorded =>
                {
                    subject.AuditEvents.Add(recorded);
                    subject.CallLog.Add("audit");
                });

            return subject;
        }

        /// <summary>
        /// Builds the account row the repository answers a read with.
        /// </summary>
        /// <returns>A plausible stored account holding exactly one tenant membership.</returns>
        public static User StoredRow()
        {
            var account = new User
            {
                UserId = AccountId,
                Username = AccountName,
                FirstName = "Member",
                LastName = "One",
                DisplayName = "Member One",
                Email = AccountEmail,
                IsSuperUser = false,
                IsApproved = true,
                IsLockedOut = false,
                CreatedDate = Instant,
                PasswordHash = FakeStoredHash,
            };

            account.UserPortals.Add(new UserPortal
            {
                UserPortalId = 900,
                PortalId = SeedPortalId,
                UserId = AccountId,
                CreatedDate = Instant,
            });

            return account;
        }

        /// <summary>
        /// Builds the tenant row the repository answers a read with, whose designated administrator is
        /// deliberately somebody other than the account under test.
        /// </summary>
        /// <returns>A plausible stored tenant.</returns>
        public static Portal StoredTenantRow() => new()
        {
            PortalId = SeedPortalId,
            PortalName = "Seed Tenant",
            AdministratorId = ActingAccountId,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // The contract shape: what the four by-reference arguments and the retrieval member became.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves the by-reference idiom is gone from the account contract entirely.
    /// </summary>
    /// <remarks>
    /// The legacy surface reported through its arguments in four places, all of them
    /// <c>Public Shared</c>: <c>CreateUser(ByRef objUser As UserInfo) As UserCreateStatus</c>
    /// (<c>UserController.vb:L156</c>) mutated the account and returned a status;
    /// <c>DeleteUser(ByRef objUser, notify, deleteAdmin) As Boolean</c> (<c>L200</c>) mutated it and
    /// returned a flag; <c>GetPassword(ByRef user, passwordAnswer) As String</c> (<c>L433</c>) wrote a
    /// clear-text credential onto it; and <c>GetUserMembership(ByRef objUser)</c> (<c>L638</c>) was a
    /// <c>Sub</c> that mutated it and reported nothing at all. A fifth idiom returned the grand total the
    /// same way, in eight overloads. Every one of them produced two answers that no single type tied
    /// together, so a caller could read the object and never look at the status, or read the status and
    /// never notice the object had changed underneath it. This test is the standing guarantee that none of
    /// them can come back: an argument the callee writes to is invisible at the call site.
    /// </remarks>
    [Fact]
    public void AccountContract_DeclaresNoByReferenceArgumentAnywhere()
    {
        MethodInfo[] members = typeof(IUserService).GetMethods();

        members.Should().NotBeEmpty("the account contract must declare members for this to prove anything");

        foreach (MethodInfo member in members)
        {
            foreach (ParameterInfo argument in member.GetParameters())
            {
                argument.IsOut.Should().BeFalse(
                    "{0} declares '{1}' as an output argument, and the by-reference status idiom the "
                    + "legacy surface used in four places is not carried forward",
                    member.Name,
                    argument.Name);

                argument.ParameterType.IsByRef.Should().BeFalse(
                    "{0} declares '{1}' by reference, and a mutated argument is exactly the second answer "
                    + "the returned result now carries",
                    member.Name,
                    argument.Name);
            }
        }
    }

    /// <summary>
    /// Proves every account operation is asynchronous and reports its outcome through the returned result
    /// rather than through a flag, a status or a thrown exception for an expected failure.
    /// </summary>
    /// <remarks>
    /// Rule T6 requires that every input-output bound member return a task, and the result type is what
    /// replaced the eighteen-member status enumeration and the boolean the deletion returned. Both are
    /// asserted here in one place so a member added later cannot quietly return a bare value.
    /// </remarks>
    [Fact]
    public void AccountContract_IsAsynchronousThroughoutAndReportsThroughAResult()
    {
        MethodInfo[] members = typeof(IUserService).GetMethods();

        foreach (MethodInfo member in members)
        {
            Type returned = member.ReturnType;

            returned.IsGenericType.Should().BeTrue(
                "{0} must return a task carrying a result, not {1}",
                member.Name,
                returned.Name);

            returned.GetGenericTypeDefinition().Should().Be(
                typeof(Task<>),
                "{0} must be awaitable so no caller can block on it",
                member.Name);

            Type carried = returned.GetGenericArguments()[0];
            Type outcome = carried.IsGenericType ? carried.GetGenericTypeDefinition() : carried;

            outcome.Should().Match<Type>(
                candidate => candidate == typeof(Result) || candidate == typeof(Result<>),
                "{0} must report its outcome through a result, which is what the legacy status "
                + "enumeration and the deletion's boolean became",
                member.Name);
        }
    }

    /// <summary>
    /// Proves the account contract offers no way to obtain a stored credential, and that no response it
    /// returns carries one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: password retrieval is deliberately not carried forward, and this is the assertion that
    /// keeps it out. The legacy store was reversible by configuration - the membership provider was
    /// registered with <c>passwordFormat="Encrypted"</c> and <c>enablePasswordRetrieval="true"</c>
    /// (<c>Website/release.config:L239</c>, <c>L245</c>) with the key that decrypts every stored
    /// credential committed to source control at <c>L90-L92</c> - and <c>GetPassword</c>
    /// (<c>UserController.vb:L433-L445</c>) existed solely to hand one out, throwing only when the
    /// installation had switched retrieval off. The target hashes one way, so there is nothing to return
    /// and no member that could return it. The divergence and the administrative reset that replaces it
    /// are recorded in <c>MIGRATION_NOTES.md</c> under the password-storage heading.
    /// </para>
    /// <para>
    /// The rule is applied to response shapes only. A submission legitimately carries a credential -
    /// <see cref="ChangePasswordRequest"/> exists to accept one - and the flags and instants a response
    /// does carry about a credential, such as whether a change is outstanding and when one last happened,
    /// disclose nothing because they are not text.
    /// </para>
    /// </remarks>
    [Fact]
    public void AccountContract_OffersNoWayToObtainAStoredCredential()
    {
        IEnumerable<string> memberNames = typeof(IUserService).GetMethods().Select(member => member.Name);

        memberNames.Should().NotContain(
            name => name.Contains("GetPassword", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RetrievePassword", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Reveal", StringComparison.OrdinalIgnoreCase),
            "the legacy retrieval member is not carried forward");

        foreach (MethodInfo member in typeof(IUserService).GetMethods())
        {
            Type carried = member.ReturnType.GetGenericArguments()[0];
            Type? value = carried.IsGenericType ? carried.GetGenericArguments()[0] : null;

            value.Should().NotBe(
                typeof(string),
                "{0} returns bare text, which is the shape a recovered credential would travel in",
                member.Name);
        }

        Type[] responses =
        [
            typeof(UserListItemDto),
            typeof(UserDetailDto),
            typeof(UserProfileDto),
            typeof(MembershipSettingsDto),
            typeof(ProfilePropertyDefinitionDto),
        ];

        foreach (Type response in responses)
        {
            foreach (PropertyInfo property in response.GetProperties())
            {
                bool isCredentialText = property.PropertyType == typeof(string)
                    && (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("Answer", StringComparison.OrdinalIgnoreCase));

                isCredentialText.Should().BeFalse(
                    "{0}.{1} would carry credential material out to a caller",
                    response.Name,
                    property.Name);
            }
        }
    }

    /// <summary>
    /// Proves no untyped collection survives anywhere on the account contract or in the shapes it exchanges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy account controller returned <c>ArrayList</c> twenty times and <c>Hashtable</c> once
    /// (<c>GetUserSettings</c>, <c>UserController.vb:L656</c>). Neither declares what it holds, so the
    /// caller cast every element and the compiler checked none of it - which is how a grid came to render
    /// a collection whose contents it could only discover at run time. Every one of them is now a typed
    /// envelope or a typed contract, and this test is what stops one coming back.
    /// </para>
    /// <para>
    /// A sequence that declares no element type is refused for the same reason as the two named classes:
    /// the non-generic interfaces are exactly as silent about their contents.
    /// </para>
    /// </remarks>
    [Fact]
    public void AccountContract_CarriesNoUntypedCollectionOnAnySignatureOrShape()
    {
        Type[] untyped =
        [
            typeof(System.Collections.ArrayList),
            typeof(System.Collections.Hashtable),
            typeof(System.Collections.IList),
            typeof(System.Collections.IDictionary),
            typeof(System.Collections.ICollection),
            typeof(System.Collections.IEnumerable),
        ];

        var carried = new List<(string Owner, string Member, Type Type)>();

        foreach (MethodInfo member in typeof(IUserService).GetMethods())
        {
            carried.Add(("IUserService", member.Name, member.ReturnType));
            carried.AddRange(member.GetParameters()
                .Select(argument => ("IUserService", $"{member.Name}({argument.Name})", argument.ParameterType)));
        }

        Type[] shapes =
        [
            typeof(UserListItemDto),
            typeof(UserDetailDto),
            typeof(UserProfileDto),
            typeof(UserProfileValueDto),
            typeof(MembershipSettingsDto),
            typeof(ProfilePropertyDefinitionDto),
            typeof(CreateUserRequest),
            typeof(UpdateUserRequest),
            typeof(ChangePasswordRequest),
            typeof(PagedRequest),
        ];

        foreach (Type shape in shapes)
        {
            carried.AddRange(shape.GetProperties()
                .Select(property => (shape.Name, property.Name, property.PropertyType)));
        }

        foreach ((string owner, string member, Type type) in carried)
        {
            foreach (Type declared in Flatten(type))
            {
                untyped.Should().NotContain(
                    declared,
                    "{0}.{1} exchanges {2}, which declares nothing about what it holds",
                    owner,
                    member,
                    declared.Name);
            }
        }
    }

    /// <summary>
    /// Proves cancellation reaches every account operation, so no caller is left unable to abandon one.
    /// </summary>
    [Fact]
    public void AccountContract_AcceptsCancellationOnEveryMember()
    {
        foreach (MethodInfo member in typeof(IUserService).GetMethods())
        {
            ParameterInfo last = member.GetParameters()[^1];

            last.ParameterType.Should().Be(
                typeof(CancellationToken),
                "{0} must take cancellation, and take it last so it can be defaulted",
                member.Name);

            last.HasDefaultValue.Should().BeTrue(
                "{0} must let a caller omit the token",
                member.Name);
        }
    }

    /// <summary>
    /// Proves the deletion guard offers no override, so the legacy escape hatch cannot be reopened.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy signature was
    /// <c>DeleteUser(ByRef objUser, ByVal notify As Boolean, ByVal deleteAdmin As Boolean) As Boolean</c>
    /// (<c>UserController.vb:L200</c>), and <c>L209-L210</c> made the tenant's designated administrator
    /// deletable whenever the CALLER passed <c>deleteAdmin</c> as true. That is an authorisation decision
    /// delegated to whichever screen happened to be calling, and the target does not carry it: the
    /// administrator is refused unconditionally, and reassigning the designation is the supported way to
    /// remove that account. The <c>notify</c> switch goes with the excluded mail subsystem. Both
    /// divergences are recorded in <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    [Fact]
    public void AccountContract_OffersNoCallerSuppliedOverrideOfTheAdministratorGuard()
    {
        MethodInfo delete = typeof(IUserService).GetMethod(nameof(IUserService.DeleteUserAsync))!;

        IEnumerable<string?> arguments = delete.GetParameters().Select(argument => argument.Name);

        arguments.Should().BeEquivalentTo(
            ["portalId", "userId", "cancellationToken"],
            "the deletion takes the tenant, the account and cancellation, and nothing a caller could use "
            + "to talk the guard out of refusing");
    }

    // ---------------------------------------------------------------------------------------------
    // Creation: the eighteen-member status ladder becomes a reason catalogue.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Supplies every refusal the account-creation member can reach, paired with the legacy status it
    /// stands for.
    /// </summary>
    /// <returns>One case per reachable refusal.</returns>
    public static TheoryData<UserCreateStatus, string> CreationRefusalCases()
    {
        var cases = new TheoryData<UserCreateStatus, string>();

        foreach ((UserCreateStatus status, string code) in CreationRefusals)
        {
            cases.Add(status, code);
        }

        return cases;
    }

    /// <summary>
    /// Supplies the refusals decided before anything is staged, which are every reachable refusal except
    /// the two the external credential store reports after the account row has already been committed.
    /// </summary>
    /// <returns>One case per refusal that precedes the enrolment.</returns>
    public static TheoryData<UserCreateStatus> RefusalsBeforeStagingCases()
    {
        var cases = new TheoryData<UserCreateStatus>();

        foreach ((UserCreateStatus status, string _) in CreationRefusals)
        {
            if (status is not (UserCreateStatus.DuplicateUserName or UserCreateStatus.ProviderError))
            {
                cases.Add(status);
            }
        }

        return cases;
    }

    /// <summary>
    /// Proves each legacy creation outcome is reported under its own code, so a caller can still tell the
    /// ten refusals apart.
    /// </summary>
    /// <param name="legacyStatus">The legacy status the refusal stands for.</param>
    /// <param name="expectedCode">The code the target reports it under.</param>
    /// <remarks>
    /// The legacy member returned one of eighteen enumeration values and the caller compared against
    /// whichever ones it cared about (<c>UserController.vb:L156-L183</c>). Collapsing ten distinguishable
    /// outcomes onto one generic failure would be the easy translation and the wrong one: a screen that
    /// cannot tell "that name is taken" from "that address is taken" from "the credential store is
    /// unreachable" cannot tell the account holder what to do next. This is the mapping, asserted one
    /// legacy status at a time.
    /// </remarks>
    [Theory]
    [MemberData(nameof(CreationRefusalCases))]
    public async Task CreateUser_NamesEachLegacyRefusalWithItsOwnCode(
        UserCreateStatus legacyStatus,
        string expectedCode)
    {
        (Subject _, Result<UserDetailDto> outcome) = await RefuseCreationAsync(legacyStatus);

        outcome.IsFailure.Should().BeTrue(
            "the legacy path returned {0} here, which is not success",
            legacyStatus);

        outcome.Error.Should().NotBeNull();
        outcome.Error!.Code.Should().Be(
            expectedCode,
            "legacy status {0} is reported under this code and no other",
            legacyStatus);
    }

    /// <summary>
    /// Proves the refusal catalogue is one code per legacy outcome, in both directions.
    /// </summary>
    /// <remarks>
    /// Two legacy statuses sharing a code would make them indistinguishable, which is the collapse the
    /// per-status test above exists to prevent; one legacy status carrying two codes would make the
    /// mapping ambiguous from the other side, so a reviewer checking a target refusal against the legacy
    /// procedure could not tell which statement produced it. Both are refused here.
    /// </remarks>
    [Fact]
    public void CreationRefusals_MapEachLegacyOutcomeToItsOwnCode()
    {
        CreationRefusals.Select(refusal => refusal.LegacyStatus).Should().OnlyHaveUniqueItems(
            "a legacy status appearing twice would give one outcome two codes");

        CreationRefusals.Select(refusal => refusal.ReasonCode).Should().OnlyHaveUniqueItems(
            "a code appearing twice would make two legacy outcomes indistinguishable");

        CreationRefusals.Should().AllSatisfy(refusal =>
            refusal.ReasonCode.Should().StartWith(
                "user.create.",
                "every creation refusal belongs to the creation family so an edge can route it"));

        CreationRefusals.Should().NotContain(
            refusal => refusal.LegacyStatus == UserCreateStatus.Success
                || refusal.LegacyStatus == UserCreateStatus.AddUser,
            "success is not a refusal, and the seed the legacy member initialised to is never an outcome");
    }

    /// <summary>
    /// Proves no outcome is inferred from an unset value: a success carries no reason and every refusal
    /// carries a populated one.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy member initialised its local to <c>UserCreateStatus.AddUser</c> at
    /// <c>UserController.vb:L158</c> and overwrote it at <c>L161</c>, so the value zero meant "not
    /// attempted yet" rather than any outcome at all. Code that leaned on the unset value would therefore
    /// have read a not-yet-attempted creation as a definite one. The result type removes the whole class
    /// of fault by construction - success and failure are separate states rather than two values of one
    /// integer - and this test is what proves the service never lands in a third, half-populated one.
    /// </remarks>
    [Fact]
    public async Task CreateUser_DrawsNoOutcomeFromAnUnsetValue()
    {
        Subject accepted = Subject.Ready();

        Result<UserDetailDto> success = await accepted.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        success.IsSuccess.Should().BeTrue();
        success.IsFailure.Should().BeFalse("the two states are complementary and never both true");
        success.Error.Should().BeNull("a success reports no reason at all");
        success.Value.Should().NotBeNull("a success carries the account it created");

        foreach ((UserCreateStatus status, string _) in CreationRefusals)
        {
            (Subject _, Result<UserDetailDto> refused) = await RefuseCreationAsync(status);

            refused.IsFailure.Should().BeTrue();
            refused.IsSuccess.Should().BeFalse();
            refused.Error.Should().NotBeNull("legacy status {0} must arrive with a reason", status);
            refused.Error!.Code.Should().NotBeNullOrWhiteSpace(
                "a blank code would leave an edge nothing to route on for {0}",
                status);
            refused.Error.Message.Should().NotBeNullOrWhiteSpace(
                "a blank message would leave an operator nothing to read for {0}",
                status);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Creation: the automatic-enrolment gate, and the absence marker that must not become a date.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves only the roles the tenant flags for automatic assignment are enrolled, and that the flag is
    /// the whole of the test.
    /// </summary>
    /// <remarks>
    /// The legacy loop read every role the tenant declared and enrolled the new account in each one whose
    /// <c>AutoAssignment</c> was true (<c>UserController.vb:L172-L179</c>). The flag is the only
    /// condition: a public role is not automatically assigned, and a role carrying membership terms is
    /// enrolled if it is flagged, so a filter written over any other column would enrol the wrong set. The
    /// mixed collection below is arranged so that a filter testing visibility, or one testing nothing at
    /// all, produces a different answer from the correct one.
    /// </remarks>
    [Fact]
    public async Task CreateUser_EnrolsOnlyTheRolesTheTenantFlagsForAutomaticAssignment()
    {
        Subject subject = Subject.Ready();
        subject.TenantRoles =
        [
            DeclaredRole(10, "Registered Users", automatic: true, isPublic: false),
            DeclaredRole(11, "Subscribers", automatic: false, isPublic: true),
            DeclaredRole(12, "Newsletter", automatic: true, isPublic: true),
            DeclaredRole(13, "Administrators", automatic: false, isPublic: false),
        ];

        Result<UserDetailDto> outcome = await subject.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        User staged = subject.StagedAccounts.Should().ContainSingle().Which;

        staged.UserRoles.Select(enrolment => enrolment.RoleId).Should().Equal(
            [10, 12],
            "the flagged roles are enrolled and the unflagged ones are not, whatever else they carry");

        outcome.Value.Roles.Should().Equal(
            ["Registered Users", "Newsletter"],
            "the response names the roles the account was actually enrolled in");
    }

    /// <summary>
    /// Proves both bounds of an automatic enrolment are left unset, and specifically that the legacy
    /// absence marker has not become a date in the year one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy call passed the marker explicitly for both bounds -
    /// <c>AddUserRole(PortalID, UserID, RoleID, Null.NullDate, Null.NullDate)</c> at
    /// <c>UserController.vb:L177</c>, repeated at <c>RoleController.vb:L76</c> - and
    /// <c>Null.NullDate</c> is <c>Date.MinValue</c> (<c>Null.vb:L66-L70</c>). Under Rule T7 the nullable
    /// type replaces the marker, and BOTH assertions below are deliberate: the first states the target
    /// value, and the second states that the legacy marker has not leaked into the domain in its place.
    /// </para>
    /// <para>
    /// THE TWO ARE EQUIVALENT AT THE COLUMN, WHICH IS WHY THIS IS PARITY RATHER THAN A DIVERGENCE, and the
    /// evidence is in the legacy write path itself. <c>Null.GetNull</c> (<c>Null.vb:L183-L186</c>)
    /// substitutes <c>DBNull</c> for any date whose date part equals <c>NullDate.Date</c> - the comment
    /// there says it compares only the date part to avoid subtle time differences - so the marker was
    /// converted on the way out and the column received NULL every time. It could not have received
    /// anything else in any case: SQL Server's <c>datetime</c> range begins at 1753-01-01, so
    /// 0001-01-01 was never a storable value. A migrated row and its legacy self therefore hold the same
    /// thing, and a target that wrote <c>DateTime.MinValue</c> instead would be the one diverging.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_LeavesBothEnrolmentBoundsUnsetRatherThanAtTheLegacyAbsenceMarker()
    {
        Subject subject = Subject.Ready();
        subject.TenantRoles = [DeclaredRole(10, "Registered Users", automatic: true, isPublic: false)];

        Result<UserDetailDto> outcome = await subject.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        User staged = subject.StagedAccounts.Should().ContainSingle().Which;
        UserRole enrolment = staged.UserRoles.Should().ContainSingle().Which;

        enrolment.EffectiveDate.Should().BeNull(
            "an unbounded enrolment starts immediately, and absence is how the column says so");
        enrolment.ExpiryDate.Should().BeNull(
            "an unbounded enrolment never lapses, and absence is how the column says so");

        enrolment.EffectiveDate.Should().NotBe(
            DateTime.MinValue,
            "the legacy Null.NullDate marker must not leak into the domain as a real instant");
        enrolment.ExpiryDate.Should().NotBe(
            DateTime.MinValue,
            "the legacy Null.NullDate marker must not leak into the domain as a real instant");
    }

    /// <summary>
    /// Proves a refusal decided before the enrolment reads no role, stages nothing and commits nothing.
    /// </summary>
    /// <param name="legacyStatus">The legacy status the refusal stands for.</param>
    /// <remarks>
    /// The legacy member gated the whole of its enrolment block on the status being success
    /// (<c>UserController.vb:L163</c>), so a refused creation touched neither the role collection nor the
    /// tenant cache. Reading roles for an account that will not exist would be harmless in itself; staging
    /// or committing one would not be, and the same guard covers both.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RefusalsBeforeStagingCases))]
    public async Task CreateUser_ReadsNoRoleAndStagesNothingWhenARefusalPrecedesTheEnrolment(
        UserCreateStatus legacyStatus)
    {
        (Subject subject, Result<UserDetailDto> outcome) = await RefuseCreationAsync(legacyStatus);

        outcome.IsFailure.Should().BeTrue();

        subject.Roles.Verify(
            roles => roles.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        subject.StagedAccounts.Should().BeEmpty();
        subject.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        subject.CommitCount.Should().Be(0);
    }

    /// <summary>
    /// Proves the creation request cannot ask for an installation-wide account, which is how the legacy
    /// guard on the enrolment block is carried forward.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy block skipped enrolment entirely for a host account -
    /// <c>If Not objUser.IsSuperUser Then</c> at <c>UserController.vb:L166</c> - because a host account
    /// is beyond any single tenant's roles. The guard survives as an absence rather than as a branch: the
    /// request contract exposes no way to declare one, so the case the legacy branch existed to handle
    /// cannot arise through this member at all. That is the stronger translation, because a branch can be
    /// reached with the wrong value and a missing member cannot be reached at all. The deletion guard
    /// enforces the same rule from the other end, where the account already exists and the flag can
    /// therefore be true.
    /// </remarks>
    [Fact]
    public async Task CreateUser_CannotBeAskedToCreateAnInstallationWideAccount()
    {
        typeof(CreateUserRequest).GetProperties().Should().NotContain(
            property => property.Name.Contains("SuperUser", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("IsHost", StringComparison.OrdinalIgnoreCase),
            "a caller that could raise the host flag on a creation would bypass tenant administration "
            + "entirely");

        Subject subject = Subject.Ready();
        subject.TenantRoles = [DeclaredRole(10, "Registered Users", automatic: true, isPublic: false)];

        Result<UserDetailDto> outcome = await subject.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.IsSuperUser.Should().BeFalse("a created account is never installation-wide");
        subject.StagedAccounts.Should().ContainSingle().Which.IsSuperUser.Should().BeFalse();
    }

    /// <summary>
    /// Proves every instant a creation records comes from the injected clock.
    /// </summary>
    /// <remarks>
    /// The legacy code read the ambient clock inline wherever it needed an instant, which is why its date
    /// handling could not be asserted at all. Every instant below is the one the substituted clock
    /// reported, and the same value reaches the external credential store, so a row and its credential
    /// cannot disagree about when the account came into being.
    /// </remarks>
    [Fact]
    public async Task CreateUser_TakesEveryRecordedInstantFromTheInjectedClock()
    {
        Subject subject = Subject.Ready();

        Result<UserDetailDto> outcome = await subject.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.CreatedDate.Should().Be(Subject.Instant);
        outcome.Value.LastPasswordChangeDate.Should().Be(Subject.Instant);

        User staged = subject.StagedAccounts.Should().ContainSingle().Which;
        staged.UserPortals.Should().ContainSingle().Which.CreatedDate.Should().Be(Subject.Instant);

        subject.Users.Verify(
            users => users.CreateCredentialAsync(
                It.IsAny<int>(),
                FakeStoredHash,
                true,
                Subject.Instant,
                It.IsAny<CancellationToken>()),
            Times.Once);

        subject.Clock.VerifyGet(clock => clock.UtcNow, Times.AtLeastOnce);
    }

    // ---------------------------------------------------------------------------------------------
    // The single commit point, and the two coarse legacy cache clears.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves an accepted creation reaches the commit point exactly once.
    /// </summary>
    /// <remarks>
    /// The legacy creation had no commit point at all: the membership provider wrote the account, the
    /// enrolment loop issued one statement per flagged role, and nothing enclosed them, so a failure part
    /// way through left an account enrolled in some of its roles and not others. The object graph now
    /// flushes once, which is what makes the row and its enrolments atomic.
    /// </remarks>
    [Fact]
    public async Task CreateUser_ReachesTheCommitPointExactlyOnceOnAnAcceptedCreation()
    {
        Subject subject = Subject.Ready();
        subject.TenantRoles =
        [
            DeclaredRole(10, "Registered Users", automatic: true, isPublic: false),
            DeclaredRole(12, "Newsletter", automatic: true, isPublic: true),
        ];

        Result<UserDetailDto> outcome = await subject.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        subject.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        subject.CommitCount.Should().Be(
            1,
            "the row and both enrolments are one write, not three");
    }

    /// <summary>
    /// Proves the two cache entries the legacy creation cleared are discarded only once the credential has
    /// been accepted, and never on a refusal.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy member cleared the whole tenant cache inside its success branch -
    /// <c>DataCache.ClearPortalCache(objUser.PortalID, False)</c> at <c>UserController.vb:L164</c> - and
    /// the target replaces that coarse sweep with two scoped discards, the tenant's entries and this
    /// account's. Scoping is the improvement; the placement is the parity: discarding before the
    /// credential store has accepted the credential would evict entries for an account that is about to
    /// be withdrawn again.
    /// </remarks>
    [Fact]
    public async Task CreateUser_DiscardsTheTenantAndAccountEntriesOnlyOnAnAcceptedCreation()
    {
        Subject accepted = Subject.Ready();

        Result<UserDetailDto> outcome = await accepted.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        accepted.DiscardedTenants.Should().Equal([SeedPortalId]);
        accepted.DiscardedAccounts.Should().Equal([(SeedPortalId, AccountName)]);
        accepted.Cache.Verify(cache => cache.InvalidatePortal(SeedPortalId), Times.Once);
        accepted.Cache.Verify(cache => cache.InvalidateUser(SeedPortalId, AccountName), Times.Once);
        accepted.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);

        foreach ((UserCreateStatus status, string _) in CreationRefusals)
        {
            (Subject refused, Result<UserDetailDto> outcomeOfRefusal) = await RefuseCreationAsync(status);

            outcomeOfRefusal.IsFailure.Should().BeTrue();
            refused.DiscardedTenants.Should().BeEmpty(
                "legacy status {0} took the failing branch, which cleared nothing",
                status);
            refused.DiscardedAccounts.Should().BeEmpty(
                "legacy status {0} took the failing branch, which cleared nothing",
                status);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Deletion: one legacy boolean becomes three named outcomes, and the cascade keeps its order.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves the three refusals a deletion can reach carry three distinct reasons, where the legacy member
    /// carried one boolean for all of them.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>DeleteUser</c> returned <c>Boolean</c> (<c>UserController.vb:L200</c>) and set it
    /// false from three unrelated places - the administrator guard at <c>L209-L210</c>, a refusal from the
    /// membership provider at <c>L231</c>, and the exception handler at the foot of the method, which
    /// swallowed every fault into the same false. A caller therefore learned that the deletion had not
    /// happened and nothing whatever about why: the account holder saw the same outcome whether the
    /// account did not exist, was protected, or the store was unreachable. Each is now its own reason, and
    /// this test is what stops them collapsing back together.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_SeparatesTheRefusalsTheLegacyBooleanCouldNotTellApart()
    {
        Subject absent = Subject.Ready();
        absent.StoredAccount = null;

        Result missing = await absent.Service.DeleteUserAsync(SeedPortalId, AccountId, CancellationToken.None);

        Subject host = Subject.Ready();
        host.StoredAccount!.IsSuperUser = true;

        Result installationWide = await host.Service.DeleteUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        Subject designated = Subject.Ready();
        designated.StoredTenant!.AdministratorId = AccountId;

        Result administrator = await designated.Service.DeleteUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        missing.Error!.Code.Should().Be("user.not-found");
        installationWide.Error!.Code.Should().Be("user.delete.superuser-protected");
        administrator.Error!.Code.Should().Be("user.delete.administrator-protected");

        new[] { missing.Error.Code, installationWide.Error.Code, administrator.Error.Code }
            .Should().OnlyHaveUniqueItems(
                "three unrelated refusals must not be reported as one");

        new[] { missing.Error.Message, installationWide.Error.Message, administrator.Error.Message }
            .Should().AllSatisfy(message => message.Should().NotBeNullOrWhiteSpace());
    }

    /// <summary>
    /// Proves refusing the tenant's designated administrator leaves everything else untouched.
    /// </summary>
    /// <remarks>
    /// The legacy guard read the designation off the tenant row - <c>Convert.ToInt32(dr("AdministratorId"))</c>
    /// at <c>UserController.vb:L209</c>, an explicit conversion in a file whose companion screens compiled
    /// with Option Strict off - and, when the deletion was not permitted, skipped the whole cascade at
    /// <c>L217</c>. The target reads the designation from a typed nullable column, so there is no
    /// conversion left to get wrong, and the guard still stands ahead of every destructive step.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_RefusesTheDesignatedAdministratorWithoutTouchingAnythingElse()
    {
        Subject subject = Subject.Ready();
        subject.StoredTenant!.AdministratorId = AccountId;

        Result outcome = await subject.Service.DeleteUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();

        subject.Tokens.Verify(
            tokens => tokens.RevokeAllRefreshTokensAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        subject.Permissions.Verify(
            permissions => permissions.DeleteUserPermissionsAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        subject.Users.Verify(
            users => users.DeleteCredentialAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        subject.Users.Verify(users => users.Remove(It.IsAny<User>()), Times.Never);
        subject.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        subject.CallLog.Should().BeEmpty("nothing at all happens once the guard refuses");
        subject.DiscardedTenants.Should().BeEmpty();
        subject.DiscardedAccounts.Should().BeEmpty();
        subject.AuditEvents.Should().BeEmpty();
    }

    /// <summary>
    /// Proves every grant and enrolment is released before the account itself, and that the whole cascade
    /// precedes the single commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is part of the ported contract rather than an incidental detail. The legacy member cleared
    /// folder grants at <c>UserController.vb:L220</c>, module grants at <c>L224</c> and page grants at
    /// <c>L228</c>, and only then asked the membership provider to remove the account at <c>L231</c>. A
    /// grant outliving the account that held it is inherited by the next account to be issued the same
    /// identifier, which is precisely why the legacy code cleared first and why the target still does.
    /// </para>
    /// <para>
    /// MIGRATION: the three separate calls are one call to the permission contract, which owns both grant
    /// tables and states the direct-grants-only rule once. The consolidation and its reasoning are recorded
    /// in <c>MIGRATION_NOTES.md</c>; what is asserted here is that consolidating them did not move them
    /// after the removal they must precede, and that the whole cascade is staged rather than committed
    /// piecemeal - the legacy sequence was several independent statements with nothing enclosing them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DeleteUser_ReleasesEveryGrantAndEnrolmentBeforeTheAccountAndCommitsOnceAfterwards()
    {
        Subject subject = Subject.Ready();
        subject.Roles
            .Setup(roles => roles.GetUserRolesAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new UserRole { UserId = AccountId, RoleId = 10 },
                new UserRole { UserId = AccountId, RoleId = 12 },
            ]);

        Result outcome = await subject.Service.DeleteUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        int sessions = subject.CallLog.IndexOf("sessions.end");
        int grants = subject.CallLog.IndexOf("grants.delete");
        int lastEnrolment = subject.CallLog.LastIndexOf("assignment.delete");
        int membership = subject.CallLog.IndexOf("membership.remove");
        int credential = subject.CallLog.IndexOf("credential.delete");
        int account = subject.CallLog.IndexOf("account.remove");
        int commit = subject.CallLog.IndexOf("commit");

        sessions.Should().BeGreaterThanOrEqualTo(0, "the sessions are ended before anything is released");
        grants.Should().BeGreaterThan(sessions);
        lastEnrolment.Should().BeGreaterThan(grants, "both enrolments are released after the grants");
        membership.Should().BeGreaterThan(lastEnrolment);
        credential.Should().BeGreaterThan(membership);
        account.Should().BeGreaterThan(
            credential,
            "an orphaned credential is unreachable once its account row is gone, so the credential goes first");
        commit.Should().BeGreaterThan(account, "the whole cascade is staged and then flushed once");

        subject.CallLog.Count(entry => entry == "assignment.delete").Should().Be(2);
        subject.CallLog.Count(entry => entry == "commit").Should().Be(1);
        subject.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Proves both cache entries the legacy deletion cleared are discarded, and discarded after the commit
    /// rather than before it.
    /// </summary>
    /// <remarks>
    /// The legacy member cleared two entries in its success branch - the whole tenant cache at
    /// <c>UserController.vb:L249</c> and the account's own entry at <c>L250</c>, the only place in the
    /// file where both were cleared together. Both survive as scoped discards, and both are issued after
    /// the commit: discarding before it would repopulate an entry from rows the commit was about to
    /// remove.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_DiscardsBothLegacyCacheEntriesAfterTheCommit()
    {
        Subject subject = Subject.Ready();

        Result outcome = await subject.Service.DeleteUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        subject.DiscardedTenants.Should().Equal([SeedPortalId]);
        subject.DiscardedAccounts.Should().Equal([(SeedPortalId, AccountName)]);

        subject.CallLog.IndexOf("cache.tenant").Should().BeGreaterThan(subject.CallLog.IndexOf("commit"));
        subject.CallLog.IndexOf("cache.account").Should().BeGreaterThan(subject.CallLog.IndexOf("commit"));
    }

    /// <summary>
    /// Proves the deletion is recorded under the legacy event name, against the account deleted and the
    /// caller who deleted it.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy entry was written by
    /// <c>objEventLog.AddLog("Username", objUser.Username, _portalSettings, objUser.UserID,
    /// EventLogType.USER_DELETED)</c> at <c>UserController.vb:L240</c>, into a store the logging provider
    /// family owned and which is out of scope. The store is gone and the NAME is not: the event travels to
    /// an audit sink under the same <c>USER_DELETED</c> string, so an operator's existing expectation of
    /// what a deletion looks like in the trail still holds. The legacy record carried a single user field
    /// and so could not distinguish the administrator from the account they removed; the target carries
    /// both, which is why the acting caller below is deliberately not the subject.
    /// </remarks>
    [Fact]
    public async Task DeleteUser_RecordsTheDeletionUnderItsLegacyEventName()
    {
        Subject subject = Subject.Ready();

        Result outcome = await subject.Service.DeleteUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent recorded = subject.AuditEvents.Should().ContainSingle().Which;

        recorded.EventName.Should().Be(
            "USER_DELETED",
            "the legacy event-type name is the stable audit name, not the enumeration member's spelling");
        recorded.EventName.Should().Be(AuditEventNames.UserDeleted);
        recorded.PortalId.Should().Be(SeedPortalId);
        recorded.SubjectUserId.Should().Be(AccountId);
        recorded.ActorUserId.Should().Be(
            ActingAccountId,
            "the acting caller and the account acted on are separate fields, which the legacy record "
            + "could not express");
        recorded.ResourceId.Should().Be(AccountId.ToString(CultureInfo.InvariantCulture));
        recorded.Outcome.Should().Be(AuditOutcome.Succeeded);
        recorded.Properties.Should().NotContainKey(
            "Password",
            "no credential material reaches an audit record");

        subject.CallLog.IndexOf("audit").Should().BeGreaterThan(
            subject.CallLog.IndexOf("commit"),
            "a deletion is recorded once it has actually happened");
    }

    // ---------------------------------------------------------------------------------------------
    // Listing: four legacy search procedures and eight overloads become one member's parameter set.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves each legacy search procedure lands on its own argument, and that the same-typed neighbours
    /// are not transposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE ASSERTION THE FILE EXISTS FOR. The legacy grid dispatched to one of four procedures per
    /// postback, selected by a drop-down: the unfiltered <c>GetUsers</c>, <c>GetUsersByEmail</c>,
    /// <c>GetUsersByUserName</c> and <c>GetUsersByProfileProperty</c>. Each is now a nullable argument on
    /// one member, and three of those arguments sit adjacent and are text. A transposition between two of
    /// them compiles cleanly, satisfies every workflow test - a page still comes back, still typed, still
    /// paged - and quietly searches the wrong column. Nothing but an assertion on the forwarded arguments
    /// can see it, so the values below are chosen so that no two could be mistaken for one another.
    /// </para>
    /// <para>
    /// The filters are asserted one at a time because the member refuses more than one, which preserves
    /// the legacy dispatch exactly: the grid could search by name or by address or by profile answer, never
    /// by two at once.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_LandsEachLegacySearchProcedureOnItsOwnArgument()
    {
        const string nameText = "mem";
        const string addressText = "one@example";
        const string answerText = "Springfield";
        const string answerProperty = "Nickname";
        const int answerPropertyId = 55;

        LegacySearchShapes.Should().HaveCount(
            3,
            "three of the four legacy procedures took a filter; the fourth took none");

        Subject unfiltered = Subject.Ready();
        await unfiltered.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            cancellationToken: CancellationToken.None);

        unfiltered.Listing.Should().NotBeNull();
        unfiltered.Listing!.UserNamePrefix.Should().BeNull("the unfiltered shape supplies no filter");
        unfiltered.Listing.EmailPrefix.Should().BeNull();
        unfiltered.Listing.ProfilePropertyDefinitionId.Should().BeNull();
        unfiltered.Listing.ProfilePropertyValuePrefix.Should().BeNull();

        Subject byName = Subject.Ready();
        await byName.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            userNameFilter: nameText,
            cancellationToken: CancellationToken.None);

        byName.Listing!.UserNamePrefix.Should().Be(
            nameText,
            "{0} matched the account name, so its text must reach the account-name argument",
            LegacySearchShapes[0]);
        byName.Listing.EmailPrefix.Should().BeNull(
            "an account-name search must not become an address search");
        byName.Listing.ProfilePropertyDefinitionId.Should().BeNull();
        byName.Listing.ProfilePropertyValuePrefix.Should().BeNull();

        Subject byAddress = Subject.Ready();
        await byAddress.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            emailFilter: addressText,
            cancellationToken: CancellationToken.None);

        byAddress.Listing!.EmailPrefix.Should().Be(
            addressText,
            "{0} matched the electronic-mail address, so its text must reach the address argument",
            LegacySearchShapes[1]);
        byAddress.Listing.UserNamePrefix.Should().BeNull(
            "an address search must not become an account-name search");
        byAddress.Listing.ProfilePropertyDefinitionId.Should().BeNull();
        byAddress.Listing.ProfilePropertyValuePrefix.Should().BeNull();

        Subject byAnswer = Subject.Ready();
        byAnswer.ProfileDeclarations = [Declaration(answerPropertyId, answerProperty)];
        await byAnswer.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            profilePropertyName: answerProperty,
            profilePropertyValue: answerText,
            cancellationToken: CancellationToken.None);

        byAnswer.Listing!.ProfilePropertyDefinitionId.Should().Be(
            answerPropertyId,
            "{0} took a property NAME and the store takes its identifier, so the name is resolved first",
            LegacySearchShapes[2]);
        byAnswer.Listing.ProfilePropertyValuePrefix.Should().Be(
            answerText,
            "the answer being searched for must reach the answer argument and not a name argument");
        byAnswer.Listing.UserNamePrefix.Should().BeNull();
        byAnswer.Listing.EmailPrefix.Should().BeNull();
    }

    /// <summary>
    /// Proves one text value placed in each filter position in turn reaches a different argument each time.
    /// </summary>
    /// <remarks>
    /// The test above uses distinguishable values, which catches a transposition by the value that arrives.
    /// This one uses the SAME value three times, which catches the narrower fault of a member that forwards
    /// whatever it is given to one fixed argument regardless of which one the caller filled - a mistake the
    /// distinguishable-values test would also catch, but only for two of the three positions.
    /// </remarks>
    [Fact]
    public async Task ListUsers_SendsOneValuePlacedInEachPositionToADifferentArgument()
    {
        const string sameText = "ambiguous";
        const string declaredProperty = "Nickname";

        Subject byName = Subject.Ready();
        await byName.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest(),
            userNameFilter: sameText,
            cancellationToken: CancellationToken.None);

        Subject byAddress = Subject.Ready();
        await byAddress.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest(),
            emailFilter: sameText,
            cancellationToken: CancellationToken.None);

        Subject byAnswer = Subject.Ready();
        byAnswer.ProfileDeclarations = [Declaration(55, declaredProperty)];
        await byAnswer.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest(),
            profilePropertyName: declaredProperty,
            profilePropertyValue: sameText,
            cancellationToken: CancellationToken.None);

        (byName.Listing!.UserNamePrefix, byName.Listing.EmailPrefix, byName.Listing.ProfilePropertyValuePrefix)
            .Should().Be((sameText, null, (string?)null));
        (byAddress.Listing!.UserNamePrefix, byAddress.Listing.EmailPrefix, byAddress.Listing.ProfilePropertyValuePrefix)
            .Should().Be(((string?)null, sameText, (string?)null));
        (byAnswer.Listing!.UserNamePrefix, byAnswer.Listing.EmailPrefix, byAnswer.Listing.ProfilePropertyValuePrefix)
            .Should().Be(((string?)null, (string?)null, sameText));
    }

    /// <summary>
    /// Proves the account picker performs NONE of the four supporting reads the account listing performs,
    /// which is the whole reason it is a separate member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The listing reads the tenant's account-policy settings to decide which columns it may publish, reads
    /// the tenant's profile-property declarations, issues a batched profile-value read to fill the address
    /// and telephone columns, and reads the portal itself to learn which account it must not offer for
    /// deletion. A picker renders a key and two captions, so it needs none of them - and this test is what
    /// stops one being reintroduced by a later edit that reuses the listing's body.
    /// </para>
    /// <para>
    /// The portal read is the sharpest of the four, because it is the one whose removal from the account
    /// DETAIL read was a separate finding: a read that exists to decide an affordance the payload does not
    /// carry is a round trip with no reader.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAccountChoices_PerformsNoSupportingReads()
    {
        Subject subject = Subject.Ready();
        subject.ChoicePage = PagedResult<AccountChoice>.Create(
            [new AccountChoice(7, "member_one", "Member One")],
            totalCount: 1,
            pageIndex: 0,
            pageSize: 10);

        Result<PagedResult<UserChoiceDto>> outcome = await subject.Service.ListAccountChoicesAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        subject.Portals.Verify(
            portals => portals.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a picker publishes no affordance that depends on the tenant row");

        subject.Profiles.Verify(
            profiles => profiles.GetProfileValuesAsync(
                It.IsAny<int>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "the address and telephone columns are not in this projection, so no profile value is read");

        subject.Profiles.Verify(
            profiles => profiles.GetDefinitionsByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no profile declaration is needed to caption an option");

        subject.Users.Verify(
            users => users.ListAsync(
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
            Times.Never,
            "the picker must not be served by the fat account listing, which is the defect it closes");
    }

    /// <summary>
    /// Proves the picker's projection carries the key and the two captions and cannot carry anything else.
    /// </summary>
    /// <remarks>
    /// Asserted against the CONTRACT TYPE rather than against one instance, because the exposure this
    /// endpoint closes would return the moment a member were added to the transfer object - and every
    /// existing caller would keep compiling. The three names are written out so that widening the type
    /// fails this test rather than passing silently.
    /// </remarks>
    [Fact]
    public void AccountChoiceContract_CarriesTheKeyAndTwoCaptionsOnly()
    {
        IReadOnlyList<string> members = typeof(UserChoiceDto)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        members.Should().BeEquivalentTo(
            new[] { "DisplayName", "UserId", "Username" },
            "an account picker needs a key to submit and two captions to render; every further member is "
            + "personal data sent to a screen that cannot use it");
    }

    /// <summary>
    /// Proves the picker refuses an ordering by a value it does not return, rather than accepting and
    /// discarding it.
    /// </summary>
    /// <remarks>
    /// The account listing admits <c>Email</c> as an ordering and this member does not, and the difference is
    /// the projection: ordering a drop-down by a value none of its options shows is an ordering the operator
    /// cannot verify. The refusal carries its own code so a client can tell which set it was measured
    /// against.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAccountChoices_OrderedByAFieldItDoesNotReturn_IsRefused()
    {
        Subject subject = Subject.Ready();

        Result<PagedResult<UserChoiceDto>> outcome = await subject.Service.ListAccountChoicesAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10, SortBy = "Email" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Error.Should().NotBeNull();
        outcome.Error!.Code.Should().Be("user.choices.sort-unsupported");

        subject.ChoiceListing.Should().BeNull("a refused request reaches no store");
    }

    /// <summary>
    /// Proves both captions ARE accepted as orderings and travel to the store, so the two names the
    /// boundary admits are honoured rather than advertised.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAccountChoices_OrderedByACaptionItReturns_ForwardsTheOrdering()
    {
        Subject subject = Subject.Ready();

        Result<PagedResult<UserChoiceDto>> outcome = await subject.Service.ListAccountChoicesAsync(
            SeedPortalId,
            new PagedRequest
            {
                PageIndex = 0,
                PageSize = 10,
                SortBy = "Username",
                SortDir = SortDirection.Descending,
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        subject.ChoiceListing.Should().NotBeNull();
        subject.ChoiceListing!.SortBy.Should().Be("Username");
        subject.ChoiceListing.Descending.Should().BeTrue();
        subject.ChoiceListing.PortalId.Should().Be(SeedPortalId);
    }

    /// <summary>
    /// Proves the paging bounds are re-checked here, so a caller reaching the application layer without
    /// passing through request validation cannot ask for an unbounded page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAccountChoices_AboveThePageSizeCeiling_IsRefused()
    {
        Subject subject = Subject.Ready();

        Func<Task> beyondTheCeiling = () => subject.Service.ListAccountChoicesAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = PagedRequestValidator.MaximumPageSize + 1 },
            CancellationToken.None);

        await beyondTheCeiling.Should().ThrowAsync<DomainException>();

        subject.ChoiceListing.Should().BeNull("a refused request reaches no store");
    }

    /// <summary>
    /// Proves the legacy "return every row" request is forwarded as an unpaged request rather than
    /// silently becoming a first page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy unpaged overloads passed -1 for the page index, the page size and the total
    /// alike - <c>Return GetUsers(portalId, False, -1, -1, -1)</c> at <c>UserController.vb:L686</c>,
    /// repeated at <c>L705</c> - and the marker meant "do not page, return everything". The target marker
    /// is 0 rather than -1, because the request contract declares a plain integer whose absent value is
    /// zero and the envelope reports unpaged by testing the same. That translation is the fault line: a
    /// mapping that carried -1 through unchanged would be refused by the negative-size guard, and one that
    /// substituted the contract's default page size would answer a first page of ten where the caller asked
    /// for the whole collection - a wrong answer that looks entirely well-formed.
    /// </para>
    /// <para>
    /// Both halves are asserted: the size the store is asked for, and the envelope the caller reads back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_ForwardsTheLegacyEveryRowRequestWithoutSubstitutingAPageSize()
    {
        Subject subject = Subject.Ready();
        subject.AccountPage = PagedResult<User>.Unpaged([Subject.StoredRow(), SecondStoredRow()]);

        Result<PagedResult<UserListItemDto>> outcome = await subject.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 0, PageSize = UnpagedPageSize },
            cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        subject.Listing!.PageSize.Should().Be(
            UnpagedPageSize,
            "the unpaged marker reaches the store as the unpaged marker");
        subject.Listing.PageSize.Should().NotBe(
            10,
            "the contract's default page size must not be substituted for an explicit request for "
            + "every row");
        subject.Listing.PageSize.Should().NotBe(
            LegacyUnpagedArgument,
            "the legacy marker is translated rather than forwarded, because a negative size is refused");
        subject.Listing.PageIndex.Should().Be(0);

        outcome.Value.IsUnpaged.Should().BeTrue("the envelope says the collection was not paged");
        outcome.Value.Items.Should().HaveCount(2, "every row is returned, not a page of them");
        outcome.Value.TotalCount.Should().Be(
            2,
            "an unpaged envelope's total is the whole collection it carries");
        outcome.Value.PageSize.Should().Be(UnpagedPageSize);
    }

    /// <summary>
    /// Proves the grand total travels on the same value as the records it counts.
    /// </summary>
    /// <remarks>
    /// The legacy overloads reported the total through a by-reference argument the caller had declared -
    /// eight of them did, at <c>UserController.vb:L725</c>, <c>L746</c>, <c>L769</c>, <c>L793</c>,
    /// <c>L816</c>, <c>L840</c>, <c>L864</c> and <c>L889</c> - so the records and their total were two
    /// separate answers a caller could pair up wrongly, or forget to read at all. One envelope carries
    /// both, and a pager that renders the wrong number of pages is no longer reachable.
    /// </remarks>
    [Fact]
    public async Task ListUsers_CarriesTheGrandTotalOnTheSameValueAsTheRecords()
    {
        Subject subject = Subject.Ready();
        subject.AccountPage = PagedResult<User>.Create(
            [Subject.StoredRow(), SecondStoredRow()],
            totalCount: 47,
            pageIndex: 2,
            pageSize: 2);

        Result<PagedResult<UserListItemDto>> outcome = await subject.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 2, PageSize = 2 },
            cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        PagedResult<UserListItemDto> page = outcome.Value;

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(47, "the total is the whole collection, not the page");
        page.PageIndex.Should().Be(2);
        page.PageSize.Should().Be(2);
        page.IsUnpaged.Should().BeFalse();
        page.TotalPages.Should().Be(24, "forty-seven records in pages of two is twenty-four pages");
        page.HasPreviousPage.Should().BeTrue();
        page.HasNextPage.Should().BeTrue();

        page.Items.Should().AllBeOfType<UserListItemDto>(
            "the element type is declared, which the legacy ArrayList never was");
    }

    /// <summary>
    /// Proves a page past the end of the collection is an empty page that still knows the total.
    /// </summary>
    /// <remarks>
    /// The legacy pairing made this case particularly easy to get wrong: an empty <c>ArrayList</c> came
    /// back and the by-reference total was whatever the procedure had assigned, so a caller that treated
    /// "no rows" as "no records" rendered a pager claiming the collection was empty when it was not.
    /// Emptiness is a successful answer here, and the total is unaffected by which page was asked for.
    /// </remarks>
    [Fact]
    public async Task ListUsers_ReportsAPageBeyondTheLastAsAnEmptySuccessThatKeepsTheTotal()
    {
        Subject subject = Subject.Ready();
        subject.AccountPage = PagedResult<User>.Create([], totalCount: 47, pageIndex: 99, pageSize: 2);

        Result<PagedResult<UserListItemDto>> outcome = await subject.Service.ListUsersAsync(
            SeedPortalId,
            new PagedRequest { PageIndex = 99, PageSize = 2 },
            cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an empty page is an answer, not a fault");
        outcome.Error.Should().BeNull();

        outcome.Value.Items.Should().BeEmpty();
        outcome.Value.TotalCount.Should().Be(47, "asking for a page that does not exist counts nothing out");
        outcome.Value.PageIndex.Should().Be(99);
        outcome.Value.HasNextPage.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Membership settings: the one Hashtable becomes a typed contract, and the cache is not reproduced.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves the tenant's account settings are a typed contract rather than a keyed bag.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>GetUserSettings</c> returned <c>Hashtable</c> (<c>UserController.vb:L656</c>), so
    /// every consumer indexed it by a string literal and coerced whatever came back. A key spelt wrongly
    /// silently yielded nothing, a value of the wrong type failed at the point of use rather than the point
    /// of reading, and no tooling could enumerate what the collection was supposed to hold. The typed
    /// contract makes each setting a named member of a declared type, which is what turns those three
    /// classes of run-time fault into compile-time ones.
    /// </remarks>
    [Fact]
    public async Task MembershipSettings_AreATypedContractRatherThanAKeyedBag()
    {
        Subject subject = Subject.Ready();
        WithAccountsModule(subject);
        subject.StoredSettings =
        [
            new ModuleSetting { ModuleId = 300, SettingName = "Records_PerPage", SettingValue = "25" },
            new ModuleSetting { ModuleId = 300, SettingName = "Column_Email", SettingValue = "True" },
        ];

        Result<MembershipSettingsDto?> outcome = await subject.Service.GetMembershipSettingsAsync(
            SeedPortalId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        MembershipSettingsDto settings = outcome.Value.Should().NotBeNull().And.Subject.As<MembershipSettingsDto>();

        settings.RecordsPerPage.Should().Be(25, "a stored value is read onto its own typed member");
        settings.ColumnEmail.Should().BeTrue();

        settings.GetType().GetProperties().Should().NotBeEmpty();
        settings.GetType().GetProperties().Should().AllSatisfy(property =>
            property.PropertyType.Should().Match<Type>(
                candidate => candidate.IsValueType
                    || candidate == typeof(string)
                    || Nullable.GetUnderlyingType(candidate) != null,
                "every setting is a named member of a declared type, never an entry in a collection"));

        typeof(MembershipSettingsDto).GetProperties().Should().NotContain(
            property => property.Name == "Item",
            "an indexer would reintroduce exactly the string-keyed access the Hashtable forced");

        MembershipSettingsDto.UserAccountsModuleDefinitionName.Should().Be(
            AccountsModuleDefinitionName,
            "the settings source is still located by the definition name the legacy read used");
    }

    /// <summary>
    /// Proves the settings source is consulted on every read, because the legacy cache is deliberately not
    /// reproduced.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy read was cached under <c>SettingsKey(portalId)</c> - <c>"UserSettings|"</c>
    /// concatenated with the tenant identifier, composed at <c>UserController.vb:L924</c> - with an expiry
    /// of <c>TimeSpan.FromMinutes(Globals.PerformanceSetting)</c> at <c>L665</c>. That entry is not
    /// reintroduced, and the reason is correctness rather than performance: the rows behind it are ordinary
    /// module settings, writable through the module surface as well as through the settings write beside
    /// this read, so an entry only this member knew how to evict would go stale on a write it never saw.
    /// The decision, and the measured legacy lifetime of three minutes that anyone reinstating it must
    /// reproduce rather than round up, are recorded in <c>MIGRATION_NOTES.md</c>. What is asserted here is
    /// that the read really is uncached, so nobody reads a stale answer believing it fresh.
    /// </remarks>
    [Fact]
    public async Task GetMembershipSettings_ConsultsTheSourceOnEveryReadBecauseTheLegacyCacheIsNotReproduced()
    {
        Subject subject = Subject.Ready();
        WithAccountsModule(subject);

        await subject.Service.GetMembershipSettingsAsync(SeedPortalId, CancellationToken.None);
        await subject.Service.GetMembershipSettingsAsync(SeedPortalId, CancellationToken.None);

        subject.CallLog.Count(entry => entry == "definitions.read").Should().Be(
            2,
            "a cached answer would have spared the second read");
        subject.CallLog.Count(entry => entry == "settings.read").Should().Be(2);

        subject.Cache.Verify(
            cache => cache.Get<MembershipSettingsDto>(It.IsAny<string>()),
            Times.Never);
        subject.Cache.Verify(
            cache => cache.Get<MembershipSettingsDto>(It.Is<string>(key =>
                key.StartsWith(LegacySettingsCacheKeyPrefix, StringComparison.Ordinal))),
            Times.Never);
        subject.Cache.Verify(
            cache => cache.Set(
                It.IsAny<string>(),
                It.IsAny<MembershipSettingsDto>(),
                It.IsAny<TimeSpan>()),
            Times.Never);
    }

    /// <summary>
    /// Proves an absent settings source reads as the legacy DEFAULTS - flagged as unstored - and still
    /// refuses a write, which is the asymmetry the legacy read left the caller to discover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy read could legitimately return <c>Nothing</c>. The guard at
    /// <c>UserController.vb:L663</c> only populated and cached the collection when the tenant actually had
    /// an account module instance, and the member returned the uninitialised local otherwise - so a caller
    /// received a null reference with no indication that this was a defined outcome rather than a fault,
    /// and indexing it threw. Every legacy CONSUMER of that null then applied the same defaults
    /// <c>UserModuleBase.GetSettings</c> applied for an absent key (<c>UserModuleBase.vb:L94-L194</c>), so
    /// the values an operator saw were never undefined.
    /// </para>
    /// <para>
    /// This member therefore returns those defaults directly and records the absence of a store on the
    /// document instead of in the status line. The earlier arrangement - a successful result carrying no
    /// value - was translated by the API surface into <c>404 Not Found</c>, and runtime testing measured the
    /// cost: four screens that read this document only to decide which columns to render raised a
    /// screen-level "not found" alert above healthy content, and the settings screen disabled its own save
    /// control while the server was healthy. Because there is still nowhere to write settings that have no
    /// source, the WRITE continues to report its own reason - now as a conflict rather than as a missing
    /// resource, since the read for the same address answers 200.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MembershipSettings_ReadAsDefaultsAndRefuseAWriteWhenTheTenantHasNoSource()
    {
        Subject subject = Subject.Ready();
        subject.ModuleDefinitions = [];
        subject.ModuleInstances = [];

        Result<MembershipSettingsDto?> read = await subject.Service.GetMembershipSettingsAsync(
            SeedPortalId,
            CancellationToken.None);

        read.IsSuccess.Should().BeTrue("absence of a store is a defined answer, not a fault");
        read.Error.Should().BeNull();

        // A TENANT WITH NO SETTINGS SOURCE IS ANSWERED WITH THE LEGACY DEFAULTS, NOT WITH AN ABSENCE, and
        // this assertion is the one that changed. Returning no value made the API surface answer 404, which
        // runtime testing showed was both untrue - the tenant exists, so its settings resource does - and
        // damaging: four screens that merely read this document to decide which columns to render raised a
        // screen-level "not found" alert over healthy content, and the settings screen disabled its own save
        // control against a healthy server. The legacy reader applied a measured default for every absent
        // key (UserModuleBase.vb:L94-L194), so defaults ARE the legacy answer; what the caller needs to know
        // additionally is that nothing is stored, and that now travels in the document.
        read.Value.Should().NotBeNull("the legacy defaults are the answer when nothing is stored");
        read.Value!.IsStored.Should().BeFalse("no settings source exists for this tenant");
        read.Value.RecordsPerPage.Should().Be(10, "the measured legacy default applies");
        read.Value.SecurityEmailValidation.Should().Be(
            MembershipSettingsDto.DefaultEmailValidationExpression,
            "an absent store yields the legacy default for every key");

        Result write = await subject.Service.UpdateMembershipSettingsAsync(
            SeedPortalId,
            new UpdateMembershipSettingsRequest(),
            CancellationToken.None);

        write.IsFailure.Should().BeTrue("there is nowhere to store them");
        write.Error!.Code.Should().Be("user.membership-settings.storage-conflict");
        write.Error.Message.Should().Contain(
            AccountsModuleDefinitionName,
            "the reason names the module instance the tenant is missing");

        subject.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        subject.DiscardedTenants.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // The preserved credential policy, and the fourth by-reference site that became a read.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Proves the credential boundary is the one the bound policy states rather than a constant the service
    /// carries, and that the shipped boundary is the legacy one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy boundary lived in configuration: the membership provider was registered with
    /// <c>minRequiredPasswordLength="7"</c> and <c>minRequiredNonalphanumericCharacters="0"</c>
    /// (<c>Website/release.config:L242-L243</c>), so seven purely alphanumeric characters were acceptable
    /// and six were not. That policy is preserved verbatim rather than tightened, because tightening one
    /// during a migration locks out existing account holders who chose a credential the old rules allowed.
    /// </para>
    /// <para>
    /// What this asserts, and why it is not the enforcement test a sibling suite already owns: the boundary
    /// MOVES when the bound policy moves. A service that had hard-coded seven would pass an enforcement
    /// test at seven and quietly ignore a hardened installation, so the third case below raises the
    /// configured minimum and requires the previously acceptable credential to be refused.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_HonoursTheBoundCredentialPolicyRatherThanAConstantOfItsOwn()
    {
        Subject shipped = Subject.Ready();

        shipped.PasswordPolicy.MinRequiredPasswordLength.Should().Be(
            7,
            "the shipped minimum is the legacy configured minimum");
        shipped.PasswordPolicy.MinRequiredNonAlphanumericCharacters.Should().Be(
            0,
            "the legacy configuration required no non-alphanumeric characters");
        shipped.PasswordPolicy.RequiresQuestionAndAnswer.Should().BeFalse(
            "the legacy configuration required no recovery pair");
        shipped.PasswordPolicy.RequiresUniqueEmail.Should().BeFalse(
            "the legacy configuration did not enforce address uniqueness");

        CreateUserRequest atTheBoundary = ValidCreationRequest();
        atTheBoundary.Password = "notreal";
        atTheBoundary.ConfirmPassword = atTheBoundary.Password;
        atTheBoundary.Password.Should().HaveLength(7).And.MatchRegex("^[a-z]+$");

        Result<UserDetailDto> accepted = await shipped.Service.CreateUserAsync(
            SeedPortalId,
            atTheBoundary,
            CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue(
            "seven purely alphanumeric characters were acceptable to the legacy policy");

        Subject shortOfIt = Subject.Ready();
        CreateUserRequest belowTheBoundary = ValidCreationRequest();
        belowTheBoundary.Password = "notrea";
        belowTheBoundary.ConfirmPassword = belowTheBoundary.Password;

        Result<UserDetailDto> refused = await shortOfIt.Service.CreateUserAsync(
            SeedPortalId,
            belowTheBoundary,
            CancellationToken.None);

        refused.IsFailure.Should().BeTrue("six characters were below the legacy minimum");
        refused.Error!.Code.Should().Be("user.create.invalid-password");
        refused.Error.Message.Should().Contain(
            "7",
            "the message quotes the configured minimum rather than a number of its own");

        Subject hardened = Subject.Ready();
        hardened.PasswordPolicy.MinRequiredPasswordLength = 9;

        Result<UserDetailDto> refusedByConfiguration = await hardened.Service.CreateUserAsync(
            SeedPortalId,
            ValidCreationRequest(),
            CancellationToken.None);

        refusedByConfiguration.IsFailure.Should().BeTrue(
            "the boundary follows the bound policy, so a hardened installation refuses what the shipped "
            + "one accepted");
        refusedByConfiguration.Error!.Message.Should().Contain("9");
    }

    /// <summary>
    /// Proves the fourth by-reference site became a read, so the membership facts arrive on the value the
    /// caller is handed rather than on an argument the callee wrote to.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>GetUserMembership(ByRef objUser As UserInfo)</c> (<c>UserController.vb:L638</c>) was a
    /// <c>Sub</c>. It returned nothing at all and its whole effect was to reach the external credential
    /// store and write what it found onto the account object the caller had passed in, so a caller that
    /// forgot to call it read an account whose approval and lock-out facts were simply absent - and could
    /// not tell that apart from an account that really was neither approved nor locked. The member has no
    /// counterpart: the facts are materialised by the read path and travel on the returned response, which
    /// is why nothing here has to be called in the right order for them to be present. The omission is
    /// recorded in <c>MIGRATION_NOTES.md</c> among the named legacy members that are not ported.
    /// </remarks>
    [Fact]
    public async Task AccountContract_HasNoMutatingMembershipReaderBecauseTheFactsArriveOnTheResponse()
    {
        typeof(IUserService).GetMethods().Should().NotContain(
            member => member.Name.Contains("GetUserMembership", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("HydrateMembership", StringComparison.OrdinalIgnoreCase),
            "the mutating membership reader has no counterpart");

        Subject subject = Subject.Ready();
        subject.StoredAccount!.IsApproved = true;
        subject.StoredAccount.IsLockedOut = false;
        subject.StoredAccount.LastLoginDate = Subject.Instant;

        Result<UserDetailDto?> outcome = await subject.Service.GetUserAsync(
            SeedPortalId,
            AccountId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        UserDetailDto detail = outcome.Value.Should().NotBeNull().And.Subject.As<UserDetailDto>();

        detail.IsApproved.Should().BeTrue("the approval fact arrives on the response, not on an argument");
        detail.IsLockedOut.Should().BeFalse();
        detail.LastLoginDate.Should().Be(Subject.Instant);
        detail.UserId.Should().Be(AccountId);
        detail.PortalId.Should().Be(SeedPortalId);

        typeof(UserDetailDto).GetProperties().Should().NotContain(
            property => property.Name == "PasswordHash",
            "the stored representation never travels on a response");
    }

    /// <summary>
    /// Gives the tenant an account module instance, which is where the legacy settings lived and where the
    /// target still reads and writes them.
    /// </summary>
    /// <param name="subject">The subject to arrange.</param>
    private static void WithAccountsModule(Subject subject)
    {
        subject.ModuleDefinitions =
        [
            new ModuleDefinition
            {
                ModuleDefinitionId = 200,
                DesktopModuleId = 100,
                FriendlyName = AccountsModuleDefinitionName,
            },
        ];

        subject.ModuleInstances =
        [
            new Module
            {
                ModuleId = 300,
                ModuleDefinitionId = 200,
                PortalId = SeedPortalId,
                ModuleTitle = AccountsModuleDefinitionName,
                IsDeleted = false,
            },
        ];
    }

    /// <summary>
    /// Builds a profile declaration the tenant holds.
    /// </summary>
    /// <param name="propertyDefinitionId">The declaration identifier the store filters on.</param>
    /// <param name="propertyName">The name a caller filters by.</param>
    /// <returns>A declared profile property.</returns>
    private static ProfilePropertyDefinition Declaration(int propertyDefinitionId, string propertyName) => new()
    {
        PropertyDefinitionId = propertyDefinitionId,
        PortalId = SeedPortalId,
        PropertyName = propertyName,
        PropertyCategory = "Preferences",
        Length = 100,
        ViewOrder = 1,
        IsVisible = true,
    };

    /// <summary>
    /// Builds a second stored account, so a page can hold more than one row.
    /// </summary>
    /// <returns>A plausible second stored account.</returns>
    private static User SecondStoredRow()
    {
        var account = new User
        {
            UserId = AccountId + 1,
            Username = "member.two",
            FirstName = "Member",
            LastName = "Two",
            DisplayName = "Member Two",
            Email = "member.two@example.test",
            IsSuperUser = false,
            IsApproved = true,
            IsLockedOut = false,
            CreatedDate = Subject.Instant,
        };

        account.UserPortals.Add(new UserPortal
        {
            UserPortalId = 901,
            PortalId = SeedPortalId,
            UserId = AccountId + 1,
            CreatedDate = Subject.Instant,
        });

        return account;
    }

    /// <summary>
    /// Builds a creation request the preserved policy accepts, so a test need only spoil the one field it
    /// is about.
    /// </summary>
    /// <returns>A well-formed request.</returns>
    private static CreateUserRequest ValidCreationRequest() => new()
    {
        Username = AccountName,
        FirstName = "Member",
        LastName = "One",
        DisplayName = "Member One",
        Email = AccountEmail,
        Password = FakeSubmittedCredential,
        ConfirmPassword = FakeSubmittedCredential,
        Authorize = true,
    };

    /// <summary>
    /// Builds a role the tenant declares.
    /// </summary>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="roleName">The role name.</param>
    /// <param name="automatic">Whether the role is assigned automatically on creation.</param>
    /// <param name="isPublic">Whether the role may be subscribed to, which the enrolment must ignore.</param>
    /// <returns>A declared role.</returns>
    private static Role DeclaredRole(int roleId, string roleName, bool automatic, bool isPublic) => new()
    {
        RoleId = roleId,
        PortalId = SeedPortalId,
        RoleName = roleName,
        AutoAssignment = automatic,
        IsPublic = isPublic,
    };

    /// <summary>
    /// Drives the account-creation member to the refusal that stands for one legacy status.
    /// </summary>
    /// <param name="legacyStatus">The legacy status to reach.</param>
    /// <returns>The subject, so its recordings can be read, and the refusal it produced.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the status is not one this member can reach, which keeps the refusal table and this
    /// driver from drifting apart.
    /// </exception>
    private static async Task<(Subject Subject, Result<UserDetailDto> Outcome)> RefuseCreationAsync(
        UserCreateStatus legacyStatus)
    {
        Subject subject = Subject.Ready();
        CreateUserRequest request = ValidCreationRequest();

        switch (legacyStatus)
        {
            case UserCreateStatus.InvalidUserName:
                request.Username = string.Empty;
                break;

            case UserCreateStatus.InvalidEmail:
                request.Email = string.Empty;
                break;

            case UserCreateStatus.InvalidPassword:
                request.Password = string.Empty;
                request.ConfirmPassword = string.Empty;
                break;

            case UserCreateStatus.PasswordMismatch:
                request.ConfirmPassword = FakeSubmittedCredential + "-other";
                break;

            case UserCreateStatus.AddUserToPortal:
                subject.Portals
                    .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);
                break;

            case UserCreateStatus.UserAlreadyRegistered:
                subject.Users
                    .Setup(users => users.GetByUsernameAsync(
                        It.IsAny<int?>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Subject.StoredRow());
                break;

            case UserCreateStatus.UsernameAlreadyExists:
                subject.Users
                    .Setup(users => users.GetByUsernameAsync(
                        It.IsAny<int?>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Subject.StoredRow());
                subject.Users
                    .Setup(users => users.GetMembershipAsync(
                        It.IsAny<int>(),
                        It.IsAny<int>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((UserPortal?)null);
                break;

            case UserCreateStatus.DuplicateEmail:
                subject.PasswordPolicy.RequiresUniqueEmail = true;
                subject.Users
                    .Setup(users => users.EmailExistsAsync(
                        It.IsAny<int>(),
                        It.IsAny<string>(),
                        It.IsAny<int?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
                break;

            case UserCreateStatus.DuplicateUserName:
                subject.Users
                    .Setup(users => users.CreateCredentialAsync(
                        It.IsAny<int>(),
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<DateTime>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);
                break;

            case UserCreateStatus.ProviderError:
                subject.Users
                    .Setup(users => users.CreateCredentialAsync(
                        It.IsAny<int>(),
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<DateTime>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("the credential store is unreachable"));

                // THE CLASSIFIER IS ARRANGED, and it has to be. This case stands for the store itself failing,
                // and the service now absorbs only a failure the Domain classifier POSITIVELY attributes to the
                // store - so simulating the outage means saying it is one, not merely throwing something. The
                // service previously converted every non-cancellation exception into this refusal, which meant a
                // programming fault inside the request was reported to the caller as an external store fault;
                // this theory is about the reason-CODE mapping, so it states the premise the mapping needs and
                // leaves the classification rule to be asserted where it belongs.
                subject.StoreFailures
                    .Setup(classifier => classifier.IsStoreUnavailable(It.IsAny<Exception>()))
                    .Returns(true);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(legacyStatus),
                    legacyStatus,
                    "the account-creation member cannot reach this legacy status");
        }

        Result<UserDetailDto> outcome = await subject.Service.CreateUserAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        return (subject, outcome);
    }

    /// <summary>
    /// Expands a type into itself and every type it closes over, so a collection nested inside a task or a
    /// result is inspected rather than skipped.
    /// </summary>
    /// <param name="type">The declared type.</param>
    /// <returns>The type and each of its generic arguments, recursively.</returns>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }
}
