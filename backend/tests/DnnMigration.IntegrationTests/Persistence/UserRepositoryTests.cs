using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Covers the account repository, including the external membership store that holds credentials.
/// </summary>
/// <remarks>
/// <para>
/// This repository straddles two stores. Account facts live in the mapped <c>dbo.Users</c> table, while
/// credentials, approval and lockout live in the <c>aspnet_*</c> membership tables that the original upgrade
/// scripts only ever altered and never created. Those tables are therefore not mapped entity types and are
/// reached through explicit statements, which puts them outside anything the model or a migration can
/// verify. Exercising them here is the only place that binding is proved.
/// </para>
/// <para>
/// The listing assertions each work inside a tenant this suite creates and then removes, so the seeded
/// tenant's membership is never disturbed by an account that exists only to satisfy a filter.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class UserRepositoryTests
{
    private const int UnknownPortalId = 987654;
    private const int UnknownUserId = 987654;
    private const int LockoutThreshold = 5;
    private const string StoredHash = "$2a$12$0123456789012345678901ualreadyahashnotaplaintextvalue0";
    private const string ReplacementHash = "$2a$12$abcdefghijklmnopqrstuvwxyzareplacementhashvalue000000000";

    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="UserRepositoryTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public UserRepositoryTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The seeded administrator reads back with the properties that come from the external membership store
    /// rather than from the mapped table.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Approval, lockout and the recorded dates are not columns on the mapped account table. If the batched
    /// membership read were dropped, every one of them would silently read as its default and an unapproved
    /// account would look approved.
    /// </remarks>
    [Fact]
    public async Task GetAsync_PopulatesThePropertiesTakenFromTheMembershipStore()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        User? account = await users.GetAsync(_fixture.Seed.PortalId, _fixture.Seed.AdminUserId);

        account.Should().NotBeNull();
        account!.Username.Should().Be(IntegrationSeed.AdminUserName);
        account.IsSuperUser.Should().BeFalse();
        account.IsApproved.Should().BeTrue();
        account.IsLockedOut.Should().BeFalse();
        account.CreatedDate.Should().NotBeNull();
        account.LastPasswordChangeDate.Should().NotBeNull();
    }

    /// <summary>An account is invisible through a tenant it does not belong to.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAsync_ThroughATenantTheAccountDoesNotBelongTo_ReturnsNull()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        (await users.GetAsync(UnknownPortalId, _fixture.Seed.AdminUserId)).Should().BeNull();
        (await users.GetAsync(_fixture.Seed.PortalId, UnknownUserId)).Should().BeNull();
    }

    /// <summary>Omitting the tenant searches the whole installation.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The installation-wide lookup exists so that a host account, which may hold no membership in the tenant
    /// being administered, can still be resolved. Callers accept the result only when the account turns out
    /// to be a host account, which is why the lookup itself is deliberately unscoped.
    /// </remarks>
    [Fact]
    public async Task GetAsync_WithoutATenant_SearchesTheWholeInstallation()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        User? host = await users.GetAsync(null, _fixture.Seed.HostUserId);

        host.Should().NotBeNull();
        host!.IsSuperUser.Should().BeTrue();
        host.Username.Should().Be(IntegrationSeed.HostUserName);
    }

    /// <summary>A username resolves irrespective of the case it is asked for in.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetByUsernameAsync_IgnoresCase()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        User? upper = await users.GetByUsernameAsync(_fixture.Seed.PortalId, IntegrationSeed.AdminUserName.ToUpperInvariant());
        User? padded = await users.GetByUsernameAsync(_fixture.Seed.PortalId, "  " + IntegrationSeed.AdminUserName + "  ");

        upper.Should().NotBeNull();
        upper!.UserId.Should().Be(_fixture.Seed.AdminUserId);
        padded.Should().NotBeNull();
        padded!.UserId.Should().Be(_fixture.Seed.AdminUserId);
    }

    /// <summary>An account whose membership has not been authorised is still resolvable by name.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is deliberate rather than accidental. Sign-in has to be able to find an account that has not yet
    /// been authorised, because that is precisely the account that may present a verification code to have
    /// its membership approved. Filtering unauthorised accounts out of the lookup would make the verification
    /// path unreachable and turn a legitimate first sign-in into an unknown-account denial.
    /// </remarks>
    [Fact]
    public async Task GetByUsernameAsync_FindsAnAccountWhoseMembershipIsNotAuthorised()
    {
        int portalId = await CreatePortalAsync();
        string userName = FormattableString.Invariant($"unauth_{Suffix()}");
        int userId = await CreateAccountAsync(portalId, userName, authorised: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            User? found = await users.GetByUsernameAsync(portalId, userName);

            found.Should().NotBeNull();
            found!.UserId.Should().Be(userId);

            UserPortal? membership = await users.GetMembershipAsync(portalId, userId);
            membership.Should().NotBeNull();
            membership!.IsAuthorised.Should().BeFalse("the lookup found the account despite the membership being unauthorised");
        }
        finally
        {
            await RemoveAccountAsync(userId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// The username check spans the installation while the address check is confined to one tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The asymmetry follows the schema. A unique index on the username makes it an installation-wide
    /// identifier, so a duplicate must be refused whichever tenant it is offered to. The address carries no
    /// such index and the legacy uniqueness setting was per tenant, so the same person may hold one address
    /// across several tenants.
    /// </remarks>
    [Fact]
    public async Task ExistenceChecks_ScopeTheUsernameInstallationWideAndTheAddressPerTenant()
    {
        int portalId = await CreatePortalAsync();
        string userName = FormattableString.Invariant($"scoped_{Suffix()}");
        string email = FormattableString.Invariant($"{userName}@example.test");
        int userId = await CreateAccountAsync(portalId, userName, email: email);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            (await users.UsernameExistsAsync(userName)).Should().BeTrue();
            (await users.UsernameExistsAsync(userName.ToUpperInvariant())).Should().BeTrue();
            (await users.UsernameExistsAsync(userName, excludingUserId: userId)).Should().BeFalse();
            (await users.UsernameExistsAsync("nobody_" + Suffix())).Should().BeFalse();

            (await users.EmailExistsAsync(portalId, email)).Should().BeTrue();
            (await users.EmailExistsAsync(portalId, email.ToUpperInvariant())).Should().BeTrue();
            (await users.EmailExistsAsync(portalId, email, excludingUserId: userId)).Should().BeFalse();
            (await users.EmailExistsAsync(_fixture.Seed.PortalId, email)).Should()
                .BeFalse("the address check is confined to the tenant it is asked about");
        }
        finally
        {
            await RemoveAccountAsync(userId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The membership row is returned for a real pairing and nothing for an invented one.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetMembershipAsync_ReturnsTheMembershipRowOrNothing()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        UserPortal? membership = await users.GetMembershipAsync(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId);

        membership.Should().NotBeNull();
        membership!.UserId.Should().Be(_fixture.Seed.MemberUserId);
        membership.PortalId.Should().Be(_fixture.Seed.PortalId);
        membership.IsAuthorised.Should().BeTrue();

        (await users.GetMembershipAsync(UnknownPortalId, _fixture.Seed.MemberUserId)).Should().BeNull();
        (await users.GetMembershipAsync(_fixture.Seed.PortalId, UnknownUserId)).Should().BeNull();
    }

    /// <summary>
    /// Role names name only the assignments that are in force at the moment being asked about.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An assignment carries an effective date and an expiry date because paid membership of a role begins
    /// and ends. Reading every assignment regardless of its dates would grant a lapsed subscriber the access
    /// they have stopped paying for, and would grant a future subscriber access before it starts.
    /// </remarks>
    [Fact]
    public async Task ListRoleNamesAsync_NamesOnlyTheAssignmentsInForce()
    {
        // The account holds no membership of the seeded tenant, so its presence cannot affect any listing
        // assertion elsewhere; only the role assignments below matter here.
        int userId = await CreateAccountAsync(portalId: null, FormattableString.Invariant($"dated_{Suffix()}"));
        DateTime now = DateTime.UtcNow;

        try
        {
            await AddAssignmentAsync(userId, _fixture.Seed.AdministratorRoleId, effective: null, expiry: null);
            await AddAssignmentAsync(userId, _fixture.Seed.RegisteredRoleId, effective: now.AddDays(1), expiry: null);
            await AddAssignmentAsync(userId, _fixture.Seed.SubscribersRoleId, effective: null, expiry: now.AddDays(-1));

            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            IReadOnlyList<string> today = await users.ListRoleNamesAsync(_fixture.Seed.PortalId, userId, now);

            today.Should().Equal(IntegrationSeed.AdministratorsRoleName);

            // Asking about a later moment brings the assignment that had not yet started into force, which is
            // what proves the moment is a parameter rather than an implicit "now".
            IReadOnlyList<string> later = await users.ListRoleNamesAsync(_fixture.Seed.PortalId, userId, now.AddDays(2));

            later.Should().Equal(IntegrationSeed.AdministratorsRoleName, IntegrationSeed.RegisteredUsersRoleName);

            // The assignments belong to the seeded tenant's roles, so another tenant sees none of them.
            IReadOnlyList<string> elsewhere = await users.ListRoleNamesAsync(UnknownPortalId, userId, now);
            elsewhere.Should().BeEmpty();
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>Host accounts and unauthorised members are each excluded unless asked for.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Both exclusions are defaults rather than filters the caller must remember to apply. A host account is
    /// not a member of the tenant being administered even when it holds a membership row, and an unauthorised
    /// member has not yet been admitted, so neither belongs in the ordinary account listing.
    /// </remarks>
    [Fact]
    public async Task ListAsync_ExcludesHostAccountsAndUnauthorisedMembersByDefault()
    {
        int portalId = await CreatePortalAsync();
        int ordinaryId = await CreateAccountAsync(portalId, FormattableString.Invariant($"ordinary_{Suffix()}"));
        int unauthorisedId = await CreateAccountAsync(portalId, FormattableString.Invariant($"pending_{Suffix()}"), authorised: false);
        int hostId = await CreateAccountAsync(portalId, FormattableString.Invariant($"superuser_{Suffix()}"), isSuperUser: true);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User> plain = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, null,
                includeUnauthorised: false, includeSuperUsers: false);

            plain.Items.Select(account => account.UserId).Should().Equal(ordinaryId);

            PagedResult<User> withPending = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, null,
                includeUnauthorised: true, includeSuperUsers: false);

            withPending.Items.Select(account => account.UserId)
                .Should().BeEquivalentTo(new[] { ordinaryId, unauthorisedId });

            PagedResult<User> withHost = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, null,
                includeUnauthorised: false, includeSuperUsers: true);

            withHost.Items.Select(account => account.UserId)
                .Should().BeEquivalentTo(new[] { ordinaryId, hostId });

            PagedResult<User> everyone = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, null,
                includeUnauthorised: true, includeSuperUsers: true);

            everyone.Items.Select(account => account.UserId)
                .Should().BeEquivalentTo(new[] { ordinaryId, unauthorisedId, hostId });
        }
        finally
        {
            await RemoveAccountAsync(ordinaryId);
            await RemoveAccountAsync(unauthorisedId);
            await RemoveAccountAsync(hostId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// The username and address filters match a prefix, while the free-text search matches a fragment.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The difference is measured legacy behaviour, not an inconsistency. The legacy account search appended a
    /// single trailing wildcard to the username and address it was given, so both were prefix searches, while
    /// the general search box matched anywhere in the value. Widening the prefix filters to fragments would
    /// change which accounts an administrator finds and would defeat the index on the username.
    /// </remarks>
    [Fact]
    public async Task ListAsync_MatchesTheNamedFiltersAsPrefixesAndTheSearchAsAFragment()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        string userName = FormattableString.Invariant($"prefixed_{marker}");
        string email = FormattableString.Invariant($"mailbox_{marker}@example.test");
        int userId = await CreateAccountAsync(
            portalId,
            userName,
            displayName: FormattableString.Invariant($"Anne Middlename {marker}"),
            email: email);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            (await ListByUserNamePrefixAsync(users, portalId, "prefixed_")).Should().Equal(userId);
            (await ListByUserNamePrefixAsync(users, portalId, "PREFIXED_")).Should().Equal(userId);
            (await ListByUserNamePrefixAsync(users, portalId, "refixed_")).Should()
                .BeEmpty("the username filter anchors at the start of the value");

            (await ListByEmailPrefixAsync(users, portalId, "mailbox_")).Should().Equal(userId);
            (await ListByEmailPrefixAsync(users, portalId, "ailbox_")).Should()
                .BeEmpty("the address filter anchors at the start of the value");

            // The same three fragments the prefix filters rejected are all accepted by the free-text search,
            // one reaching the display name, one the username and one the address.
            (await ListByQueryAsync(users, portalId, "Middlename")).Should()
                .ContainSingle("the search matches anywhere in the display name").Which.Should().Be(userId);
            (await ListByQueryAsync(users, portalId, "refixed_")).Should()
                .ContainSingle("the search matches anywhere in the username").Which.Should().Be(userId);
            (await ListByQueryAsync(users, portalId, "ailbox_")).Should()
                .ContainSingle("the search matches anywhere in the address").Which.Should().Be(userId);
        }
        finally
        {
            await RemoveAccountAsync(userId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The approval filter reads the external membership store rather than the mapped table.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Approval is not a column on the mapped account table, so the filter cannot be expressed against it.
    /// The listing therefore roots itself in a statement over the membership tables and composes the ordinary
    /// filters on top, which keeps the whole query a single round trip and lets the store apply approval
    /// before paging rather than after it.
    /// </remarks>
    [Fact]
    public async Task ListAsync_WithTheApprovalFilter_ReadsTheExternalMembershipStore()
    {
        int portalId = await CreatePortalAsync();
        int approvedId = await CreateAccountAsync(portalId, FormattableString.Invariant($"approved_{Suffix()}"), approved: true);
        int pendingId = await CreateAccountAsync(portalId, FormattableString.Invariant($"awaiting_{Suffix()}"), approved: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User> approved = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, isApproved: true,
                includeUnauthorised: true, includeSuperUsers: false);

            PagedResult<User> pending = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, isApproved: false,
                includeUnauthorised: true, includeSuperUsers: false);

            approved.Items.Select(account => account.UserId).Should().Equal(approvedId);
            pending.Items.Select(account => account.UserId).Should().Equal(pendingId);

            approved.Items[0].IsApproved.Should().BeTrue();
            pending.Items[0].IsApproved.Should().BeFalse();
        }
        finally
        {
            await RemoveAccountAsync(approvedId);
            await RemoveAccountAsync(pendingId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Accounts are ordered by display name, then username, then identifier, so paging cannot repeat or drop
    /// a row.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_OrdersByDisplayNameThenUsername()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        string sharedDisplayName = FormattableString.Invariant($"Ordered Account {marker}");

        int secondId = await CreateAccountAsync(
            portalId, FormattableString.Invariant($"order_b_{marker}"), displayName: sharedDisplayName);
        int firstId = await CreateAccountAsync(
            portalId, FormattableString.Invariant($"order_a_{marker}"), displayName: sharedDisplayName);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User> page = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, null,
                includeUnauthorised: false, includeSuperUsers: false);

            // The two accounts share a display name, so their order is decided entirely by the username
            // tie-break, and the later-created account has to sort first.
            page.Items.Select(account => account.UserId).Should().Equal(new[] { firstId, secondId });

            PagedResult<User> firstPage = await users.ListAsync(
                portalId, 0, 1, null, null, null, null, null, null,
                includeUnauthorised: false, includeSuperUsers: false);

            PagedResult<User> secondPage = await users.ListAsync(
                portalId, 1, 1, null, null, null, null, null, null,
                includeUnauthorised: false, includeSuperUsers: false);

            firstPage.TotalCount.Should().Be(2);
            firstPage.Items.Should().ContainSingle().Which.UserId.Should().Be(firstId);
            secondPage.TotalCount.Should().Be(2);
            secondPage.Items.Should().ContainSingle().Which.UserId.Should().Be(secondId);
        }
        finally
        {
            await RemoveAccountAsync(firstId);
            await RemoveAccountAsync(secondId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>A credential is created, read, replaced and deleted in the external store.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Credential_RoundTripsThroughTheExternalMembershipStore()
    {
        int userId = await CreateAccountAsync(portalId: null, FormattableString.Invariant($"cred_{Suffix()}"), withCredential: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            (bool exists, string? hash, bool approved, bool locked) = await users.GetCredentialStateAsync(userId);
            exists.Should().BeFalse("the account has been created but holds no credential yet");
            hash.Should().BeNull();
            approved.Should().BeFalse();
            locked.Should().BeFalse();

            bool created = await users.CreateCredentialAsync(userId, StoredHash, isApproved: true, DateTime.UtcNow);
            created.Should().BeTrue();

            (exists, hash, approved, locked) = await users.GetCredentialStateAsync(userId);
            exists.Should().BeTrue();
            hash.Should().Be(StoredHash);
            approved.Should().BeTrue();
            locked.Should().BeFalse();

            bool replaced = await users.SetPasswordHashAsync(userId, ReplacementHash, DateTime.UtcNow);
            replaced.Should().BeTrue();

            (_, hash, _, _) = await users.GetCredentialStateAsync(userId);
            hash.Should().Be(ReplacementHash);

            bool deleted = await users.DeleteCredentialAsync(userId);
            deleted.Should().BeTrue();

            (exists, hash, _, _) = await users.GetCredentialStateAsync(userId);
            exists.Should().BeFalse();
            hash.Should().BeNull();
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>A credential operation against an account that does not exist reports failure.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The external store is keyed by username, so every credential operation first has to resolve the
    /// identifier it was given into a name. An unresolvable identifier has to report failure rather than
    /// throwing, because the callers translate the result into an outcome for the requester.
    /// </remarks>
    [Fact]
    public async Task CredentialOperations_ForAnUnknownAccount_ReportFailure()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        (await users.CreateCredentialAsync(UnknownUserId, StoredHash, isApproved: true, DateTime.UtcNow)).Should().BeFalse();
        (await users.SetPasswordHashAsync(UnknownUserId, StoredHash, DateTime.UtcNow)).Should().BeFalse();
        (await users.SetApprovalAsync(UnknownUserId, isApproved: true)).Should().BeFalse();
        (await users.UnlockAsync(UnknownUserId)).Should().BeFalse();
        (await users.DeleteCredentialAsync(UnknownUserId)).Should().BeFalse();

        // The two bookkeeping members report an OUTCOME rather than a boolean, and the distinction this test
        // pins is the whole reason for that: an account that cannot be resolved reports "no record", which is
        // emphatically NOT the same answer as "the store could not be reached". The sign-in path escalates the
        // second as a server fault and proceeds through the first, so a repository that conflated them - as the
        // previous boolean contract did - would turn a deleted account into a false alarm and, far worse, an
        // unreachable store into a silently uncounted credential attempt.
        (await users.RecordSuccessfulLoginAsync(UnknownUserId, DateTime.UtcNow))
            .Should().Be(MembershipWriteOutcome.NoRecord);

        (await users.RecordFailedLoginAsync(UnknownUserId, LockoutThreshold, AttemptWindow, DateTime.UtcNow))
            .Should().Be(
                MembershipWriteOutcome.NoRecord,
                "an account that does not exist has no record to count against, and the store was reachable");

        (bool exists, string? hash, _, _) = await users.GetCredentialStateAsync(UnknownUserId);
        exists.Should().BeFalse();
        hash.Should().BeNull();
    }

    /// <summary>Repeated failures lock the account at the threshold, and unlocking clears the record.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The threshold is inclusive: the attempt that brings the count up to it is the attempt that locks. The
    /// bookkeeping lives in the external membership store, where the original upgrade scripts added it by
    /// altering a procedure Microsoft shipped, so it is reproduced here by explicit statements and is only
    /// verifiable against a real relational store.
    /// </para>
    /// <para>
    /// The returned outcome distinguishes "recorded, and the account is still usable" from "recorded, and the
    /// account is now locked", which is why the early attempts below expect the former: each one is counted
    /// successfully and each one leaves the account usable. Neither is the same fact as whether the write
    /// happened at all, which is what a third outcome carries.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RecordFailedLoginAsync_LocksTheAccountAtTheThreshold()
    {
        string userName = FormattableString.Invariant($"lockout_{Suffix()}");
        int userId = await CreateAccountAsync(portalId: null, userName);
        DateTime now = DateTime.UtcNow;

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            for (int attempt = 1; attempt < LockoutThreshold; attempt++)
            {
                (await users.RecordFailedLoginAsync(userId, LockoutThreshold, AttemptWindow, now)).Should().Be(
                    MembershipWriteOutcome.Recorded,
                    FormattableString.Invariant($"attempt {attempt} is below the threshold of {LockoutThreshold}"));

                (_, _, _, bool lockedYet) = await users.GetCredentialStateAsync(userId);
                lockedYet.Should().BeFalse();
                (await ReadFailedAttemptCountAsync(userName)).Should().Be(attempt);
            }

            (await users.RecordFailedLoginAsync(userId, LockoutThreshold, AttemptWindow, now)).Should().Be(
                MembershipWriteOutcome.RecordedAndLocked,
                "the attempt that reaches the threshold is the attempt that locks");

            (_, _, _, bool locked) = await users.GetCredentialStateAsync(userId);
            locked.Should().BeTrue();

            int attemptCount = await ReadFailedAttemptCountAsync(userName);
            attemptCount.Should().Be(LockoutThreshold);

            // A further failure against an already-locked account reports the lock rather than reporting a
            // failure to record, and leaves the count where it was; the update deliberately excludes locked
            // rows so that the lock instant is not pushed forward by continued attempts.
            (await users.RecordFailedLoginAsync(userId, LockoutThreshold, AttemptWindow, now.AddMinutes(1)))
                .Should().Be(MembershipWriteOutcome.RecordedAndLocked);
            (await ReadFailedAttemptCountAsync(userName)).Should().Be(LockoutThreshold);

            (await users.UnlockAsync(userId)).Should().BeTrue();

            (_, _, _, bool unlocked) = await users.GetCredentialStateAsync(userId);
            unlocked.Should().BeFalse();
            (await ReadFailedAttemptCountAsync(userName)).Should().Be(0, "unlocking clears the failure record");
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>Approval is toggled in the external store and observed through the projection.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SetApprovalAsync_TogglesApprovalInTheExternalStore()
    {
        int userId = await CreateAccountAsync(portalId: null, FormattableString.Invariant($"approval_{Suffix()}"), approved: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            (_, _, bool initiallyApproved, _) = await users.GetCredentialStateAsync(userId);
            initiallyApproved.Should().BeFalse();

            (await users.SetApprovalAsync(userId, isApproved: true)).Should().BeTrue();

            (_, _, bool nowApproved, _) = await users.GetCredentialStateAsync(userId);
            nowApproved.Should().BeTrue();

            User? projected = await users.GetAsync(null, userId);
            projected.Should().NotBeNull();
            projected!.IsApproved.Should().BeTrue("the projection reads approval from the store that was just written");

            (await users.SetApprovalAsync(userId, isApproved: false)).Should().BeTrue();

            (_, _, bool withdrawn, _) = await users.GetCredentialStateAsync(userId);
            withdrawn.Should().BeFalse();
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>Recording a sign-in stamps the date the projection reports.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A never-signed-in account carries the sentinel date the original schema used for "not recorded", and
    /// the projection maps that sentinel to nothing rather than surfacing a date in the eighteenth century.
    /// </remarks>
    [Fact]
    public async Task RecordSuccessfulLoginAsync_ReplacesTheNeverRecordedSentinel()
    {
        int userId = await CreateAccountAsync(portalId: null, FormattableString.Invariant($"signin_{Suffix()}"));
        DateTime moment = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            User? before = await users.GetAsync(null, userId);
            before.Should().NotBeNull();
            before!.LastLoginDate.Should().BeNull("the account has never signed in");

            (await users.RecordSuccessfulLoginAsync(userId, moment)).Should().Be(MembershipWriteOutcome.Recorded);

            User? after = await users.GetAsync(null, userId);
            after.Should().NotBeNull();
            after!.LastLoginDate.Should().NotBeNull();
            after.LastLoginDate!.Value.Should().BeCloseTo(moment, TimeSpan.FromSeconds(1));
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>Membership can be revoked without deleting the account it belonged to.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// One account may belong to several tenants, so removing it from one has to leave both the account and
    /// its other memberships intact. Deleting the account instead would remove a person from every tenant of
    /// an installation because one administrator asked for them to be removed from a single site.
    /// </remarks>
    [Fact]
    public async Task RemoveMembership_RevokesOneTenancyAndLeavesTheAccountIntact()
    {
        int firstPortalId = await CreatePortalAsync();
        int secondPortalId = await CreatePortalAsync();
        int userId = await CreateAccountAsync(firstPortalId, FormattableString.Invariant($"shared_{Suffix()}"));

        try
        {
            using (IServiceScope joining = _fixture.Services.CreateScope())
            {
                IUserRepository users = joining.ServiceProvider.GetRequiredService<IUserRepository>();
                IUnitOfWork unitOfWork = joining.ServiceProvider.GetRequiredService<IUnitOfWork>();

                users.AddMembership(new UserPortal
                {
                    UserId = userId,
                    PortalId = secondPortalId,
                    CreatedDate = DateTime.UtcNow,
                    IsAuthorised = true,
                });

                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope leaving = _fixture.Services.CreateScope())
            {
                IUserRepository users = leaving.ServiceProvider.GetRequiredService<IUserRepository>();
                IUnitOfWork unitOfWork = leaving.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserPortal? membership = await users.GetMembershipAsync(firstPortalId, userId);
                membership.Should().NotBeNull();

                users.RemoveMembership(membership!);
                await unitOfWork.SaveChangesAsync();
            }

            using IServiceScope checking = _fixture.Services.CreateScope();
            IUserRepository confirming = checking.ServiceProvider.GetRequiredService<IUserRepository>();

            (await confirming.GetMembershipAsync(firstPortalId, userId)).Should().BeNull();
            (await confirming.GetMembershipAsync(secondPortalId, userId)).Should().NotBeNull();
            (await confirming.GetAsync(null, userId)).Should().NotBeNull("the account survives the loss of one tenancy");
            (await confirming.GetAsync(firstPortalId, userId)).Should().BeNull();
            (await confirming.GetAsync(secondPortalId, userId)).Should().NotBeNull();
        }
        finally
        {
            await RemoveAccountAsync(userId);
            await RemovePortalAsync(firstPortalId);
            await RemovePortalAsync(secondPortalId);
        }
    }

    /// <summary>
    /// The role-holder reader answers with accounts, matches the name case-insensitively, confines itself to
    /// the requested tenant and composes the external membership snapshot onto every account it returns.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>GetUsersByRolename(PortalID, Rolename)</c> from the membership provider stack at
    /// <c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c>. The core provider at
    /// <c>Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb</c> declares no role procedure at
    /// all, so reading it alone would have produced no contract to test here - both provider stacks are
    /// required reading, and this is one of the members that proves it.
    /// </para>
    /// <para>
    /// Assignment dates are deliberately not evaluated, matching the terminal legacy procedure, which took a
    /// portal identifier and a role name and filtered on nothing else. A holder whose assignment has lapsed is
    /// therefore still a holder here. The time-aware question is answered by <c>ListRoleNamesAsync</c>, which
    /// is asserted separately, and the contrast between the two is the point rather than an inconsistency.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListByRoleNameAsync_AnswersWithTheTenantScopedHoldersAndComposesTheirMembership()
    {
        int portalId = await CreatePortalAsync();
        int otherPortalId = await CreatePortalAsync();
        string roleName = FormattableString.Invariant($"Holders {Suffix()}");

        int roleId = await CreateRoleAsync(portalId, roleName);
        int sameNameElsewhereRoleId = await CreateRoleAsync(otherPortalId, roleName);

        int holderId = await CreateAccountAsync(portalId, FormattableString.Invariant($"holder_{Suffix()}"));
        int lapsedHolderId = await CreateAccountAsync(portalId, FormattableString.Invariant($"lapsed_{Suffix()}"));
        int bystanderId = await CreateAccountAsync(portalId, FormattableString.Invariant($"bystander_{Suffix()}"));
        int elsewhereId = await CreateAccountAsync(otherPortalId, FormattableString.Invariant($"elsewhere_{Suffix()}"));

        try
        {
            await AddAssignmentAsync(holderId, roleId, effective: null, expiry: null);

            // An assignment that expired yesterday. The legacy procedure ignored both bounds, so this holder
            // must still be listed - the assertion pins that deliberately preserved behaviour rather than the
            // more obvious time-aware answer.
            await AddAssignmentAsync(
                lapsedHolderId,
                roleId,
                effective: DateTime.UtcNow.AddDays(-30),
                expiry: DateTime.UtcNow.AddDays(-1));

            await AddAssignmentAsync(elsewhereId, sameNameElsewhereRoleId, effective: null, expiry: null);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            IReadOnlyList<User> holders = await users.ListByRoleNameAsync(portalId, roleName);

            holders.Select(account => account.UserId).Should().BeEquivalentTo(
                new[] { holderId, lapsedHolderId },
                "the reader selects on the role's portal and name alone, so the lapsed assignment still "
                + "counts and the identically named role in another tenant does not");

            holders.Select(account => account.UserId).Should().NotContain(
                bystanderId, "an account holding no assignment is not a holder");
            holders.Select(account => account.UserId).Should().NotContain(
                elsewhereId, "the role name is scoped by tenant, so the other tenant's holder is excluded");

            IReadOnlyList<User> byDifferentCase = await users.ListByRoleNameAsync(portalId, roleName.ToUpperInvariant());
            byDifferentCase.Select(account => account.UserId).Should().BeEquivalentTo(
                holders.Select(account => account.UserId), "the name is matched case-insensitively");

            // The membership snapshot is composed by this read path too. DnnDbContextTests asserts the
            // metadata half - Model_LeavesTheExternallyStoredAccountPropertyUnmapped proves FindProperty
            // answers null for all eleven properties - and this is the behavioural half: unmapped in the
            // model, yet populated on the way out. Neither assertion is meaningful without the other.
            foreach (User holder in holders)
            {
                AssertMembershipSnapshotComposed(holder, nameof(IUserRepository.ListByRoleNameAsync));
            }

            IReadOnlyList<User> unknown = await users.ListByRoleNameAsync(portalId, FormattableString.Invariant($"Absent {Suffix()}"));
            unknown.Should().BeEmpty("an unknown role name is an empty answer rather than a failure");
        }
        finally
        {
            await RemoveAccountAsync(holderId);
            await RemoveAccountAsync(lapsedHolderId);
            await RemoveAccountAsync(bystanderId);
            await RemoveAccountAsync(elsewhereId);
            await RemovePortalAsync(portalId);
            await RemovePortalAsync(otherPortalId);
        }
    }

    /// <summary>
    /// The host-account reader selects on the super-user flag alone, so it reaches an account that holds no
    /// tenancy at all, and it composes the membership snapshot like every other read path.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the whole reason the member exists separately from the listing. A host account need hold no
    /// <c>UserPortals</c> row, and the listing's root filter requires one, so a host account is not reliably
    /// reachable through the listing. The assertion below creates exactly that account - a super-user with no
    /// tenancy - and proves both halves at once: the listing cannot see it, and this member can.
    /// </remarks>
    [Fact]
    public async Task ListSuperUsersAsync_ReachesAHostAccountThatHoldsNoTenancy()
    {
        int portalId = await CreatePortalAsync();
        int untenantedHostId = await CreateAccountAsync(null, FormattableString.Invariant($"host_{Suffix()}"), isSuperUser: true);
        int ordinaryId = await CreateAccountAsync(portalId, FormattableString.Invariant($"ordinary_{Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            IReadOnlyList<User> hosts = await users.ListSuperUsersAsync();

            hosts.Select(account => account.UserId).Should().Contain(
                untenantedHostId, "no portal join is applied, so an account without a tenancy is still found");
            hosts.Select(account => account.UserId).Should().Contain(
                _fixture.Seed.HostUserId, "the seeded host account carries the super-user flag");
            hosts.Select(account => account.UserId).Should().NotContain(
                ordinaryId, "an account without the super-user flag is not a host account");

            hosts.Should().OnlyContain(
                account => account.IsSuperUser,
                "the flag is the only filter the member applies");

            // The snapshot is asserted on the two accounts whose credential records this suite knows exist. An
            // account carrying no credential record legitimately reads back with a null approval, so asserting
            // over the whole installation-wide list would couple this test to whatever else happens to hold the
            // super-user flag.
            foreach (int knownHostId in new[] { untenantedHostId, _fixture.Seed.HostUserId })
            {
                User host = hosts.Single(candidate => candidate.UserId == knownHostId);
                AssertMembershipSnapshotComposed(host, nameof(IUserRepository.ListSuperUsersAsync));
            }

            // The complementary half: the listing requires a membership row, so the untenanted host account is
            // invisible to it even with super-users explicitly included. That is not a defect in either
            // member - it is precisely why both exist.
            PagedResult<User> listed = await users.ListAsync(
                portalId, 0, 0, null, null, null, null, null, null,
                includeUnauthorised: true, includeSuperUsers: true);

            listed.Items.Select(account => account.UserId).Should().NotContain(
                untenantedHostId,
                "the listing is tenant-scoped through UserPortals, so an account with no membership row "
                + "cannot appear in it - ListSuperUsersAsync is the only way to reach that account");
        }
        finally
        {
            await RemoveAccountAsync(untenantedHostId);
            await RemoveAccountAsync(ordinaryId);
            await RemovePortalAsync(portalId);
        }
    }


    /// <summary>
    /// The reported total is never the legacy minus-one sentinel, under every shape of request the listing
    /// accepts - unpaged, paged, past the end, matching nothing, and reaching the external store.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the single most important paging assertion in the suite, and it exists because of a specific
    /// measured defect rather than as a general sanity check. The legacy unpaged idiom passed the literal
    /// triple <c>-1, -1, -1</c> into the provider, so the incoming <c>ByRef totalRecords</c> argument arrived
    /// holding minus one.
    /// </para>
    /// <para>
    /// MIGRATION: <c>FillUserCollection</c> in
    /// <c>Library/Providers/MembershipProviders/AspNetMembershipProvider/AspNetMembershipProvider.vb</c>
    /// wrapped its whole body in a catch-all that logged and returned, so when the second result set could not
    /// be read the total was never assigned and the caller received that incoming minus one as though it were
    /// a real count. Two further defects sat in the same method: its opening comment claimed the total arrived
    /// in the FIRST result set while the code read it from the second, and the boolean captured from
    /// <c>NextResult()</c> was assigned and then never tested. Three sibling XML comments compounded it by
    /// documenting the unpaged sentinel on the wrong parameter - they named <c>pageSize</c> while the code
    /// tested <c>pageIndex</c>.
    /// </para>
    /// <para>
    /// Per the Minimal Change Clause those defects are recorded here and deliberately NOT reproduced: none of
    /// them is carried forward, and nothing in the target swallows. A failure surfaces as an exception rather
    /// than as a plausible-looking count. The total is therefore always a genuine non-negative cardinality,
    /// which is what this test pins.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListAsync_NeverReportsTheLegacyMinusOneTotal()
    {
        int portalId = await CreatePortalAsync();
        int userId = await CreateAccountAsync(portalId, FormattableString.Invariant($"total_{Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User>[] shapes =
            [
                await users.ListAsync(portalId, 0, 0, null, null, null, null, null, null, true, true),
                await users.ListAsync(portalId, 0, 1, null, null, null, null, null, null, true, true),
                await users.ListAsync(portalId, 500, 10, null, null, null, null, null, null, true, true),
                await users.ListAsync(portalId, 0, 10, "no-such-account-fragment", null, null, null, null, null, true, true),
                await users.ListAsync(portalId, 0, 10, null, null, null, null, null, true, true, true),
                await users.ListAsync(portalId, 0, 10, null, null, null, null, null, false, true, true),
                await users.ListAsync(UnknownPortalId, 0, 10, null, null, null, null, null, null, true, true),
                await users.ListAsync(UnknownPortalId, 0, 0, null, null, null, null, null, null, true, true),
            ];

            foreach (PagedResult<User> shape in shapes)
            {
                shape.TotalCount.Should().NotBe(
                    -1,
                    "minus one was the legacy null-integer sentinel and the value a swallowed exception left "
                    + "behind; it must never be reachable as a reported total");

                shape.TotalCount.Should().BeGreaterThanOrEqualTo(
                    0, "a total is a cardinality, so it is never negative for any reason");

                shape.PageIndex.Should().BeGreaterThanOrEqualTo(
                    0, "page coordinates are zero-based and never carry a sentinel");
                shape.PageSize.Should().BeGreaterThanOrEqualTo(
                    0, "a page size of zero means unpaged; a negative size has no meaning");

                shape.Items.Should().NotBeNull("an empty answer is an empty list, never null");
                shape.TotalCount.Should().BeGreaterThanOrEqualTo(
                    shape.Items.Count, "the total spans every page, so it can never be smaller than one page");
            }

            // Positive control: the tenant does hold exactly the one account created above, so the assertions
            // above are not passing merely because every shape returned nothing.
            shapes[0].TotalCount.Should().Be(1);
            shapes[0].Items.Should().ContainSingle().Which.UserId.Should().Be(userId);
        }
        finally
        {
            await RemoveAccountAsync(userId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A page reports the total across every page rather than its own length, and echoes back the coordinates
    /// that were actually used.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the count that travelled as a <c>ByRef totalRecords</c> argument through eight legacy
    /// overloads now travels inside the envelope, so it cannot disagree with the rows it accompanies. Paging is
    /// applied by the database - ordered, then skipped, then taken - which is why the ordering is a request of
    /// the reader and why a caller cannot reorder a page after the fact without silently reordering only the
    /// rows that one page happened to contain. Page indexing is zero-based, as declared on
    /// <c>PagedResult{T}</c>; the legacy minus-one unpaged sentinel is expressed here as a page size of zero.
    /// </remarks>
    [Fact]
    public async Task ListAsync_ReportsTheCrossPageTotalAndEchoesTheRequestedCoordinates()
    {
        int portalId = await CreatePortalAsync();
        string stem = Suffix();
        List<int> created = [];

        try
        {
            for (int ordinal = 0; ordinal < 5; ordinal++)
            {
                created.Add(await CreateAccountAsync(
                    portalId,
                    FormattableString.Invariant($"page{ordinal}_{stem}"),
                    displayName: FormattableString.Invariant($"Paged Member {ordinal} {stem}")));
            }

            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User> first = await ListPageAsync(users, portalId, 0, 2);

            first.PageIndex.Should().Be(0, "the requested zero-based index is echoed back unchanged");
            first.PageSize.Should().Be(2, "the requested size is echoed back unchanged");
            first.Items.Should().HaveCount(2, "the page carries only the rows the size admits");
            first.TotalCount.Should().Be(5, "the total spans every page, not just the one returned");
            first.IsUnpaged.Should().BeFalse("a positive page size is a paged request");
            first.TotalPages.Should().Be(3, "five rows at two per page occupy three pages");
            first.HasPreviousPage.Should().BeFalse("the first page has nothing before it");
            first.HasNextPage.Should().BeTrue("two of five rows have been returned");

            PagedResult<User> second = await ListPageAsync(users, portalId, 1, 2);
            second.PageIndex.Should().Be(1);
            second.Items.Should().HaveCount(2);
            second.TotalCount.Should().Be(5, "the total is identical on every page of one query");
            second.HasPreviousPage.Should().BeTrue();
            second.HasNextPage.Should().BeTrue();

            PagedResult<User> last = await ListPageAsync(users, portalId, 2, 2);
            last.PageIndex.Should().Be(2);
            last.Items.Should().ContainSingle("five rows leave one on the third page");
            last.TotalCount.Should().Be(5);
            last.HasNextPage.Should().BeFalse("the third page exhausts the set");

            // The three pages together reconstruct the set exactly once: no row is skipped and none repeats,
            // which is the property a stable total ordering exists to guarantee.
            IEnumerable<int> walked = first.Items.Concat(second.Items).Concat(last.Items).Select(account => account.UserId);
            walked.Should().BeEquivalentTo(created, "walking every page visits each row exactly once");
        }
        finally
        {
            foreach (int userId in created)
            {
                await RemoveAccountAsync(userId);
            }

            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A page requested past the end of the set is an empty page that still reports the true total, not an
    /// exception and not a zero.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A pager rendered from this answer has to be able to say "page 40 of 2", so the coordinates asked for are
    /// echoed rather than clamped, and the total is the real one. Reporting zero here would make an
    /// over-scrolled grid claim the set was empty.
    /// </remarks>
    [Fact]
    public async Task ListAsync_PastTheLastPage_ReturnsAnEmptyPageWithTheTotalIntact()
    {
        int portalId = await CreatePortalAsync();
        int firstId = await CreateAccountAsync(portalId, FormattableString.Invariant($"beyond1_{Suffix()}"));
        int secondId = await CreateAccountAsync(portalId, FormattableString.Invariant($"beyond2_{Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User> beyond = await ListPageAsync(users, portalId, 40, 1);

            beyond.Items.Should().BeEmpty("no row sits at that offset");
            beyond.TotalCount.Should().Be(2, "the total is a property of the query, not of the page returned");
            beyond.TotalCount.Should().NotBe(-1);
            beyond.PageIndex.Should().Be(40, "the requested coordinate is reported rather than clamped");
            beyond.PageSize.Should().Be(1);
            beyond.HasNextPage.Should().BeFalse("there is nothing beyond an over-scrolled page");
            beyond.HasPreviousPage.Should().BeTrue("earlier pages do hold rows");
        }
        finally
        {
            await RemoveAccountAsync(firstId);
            await RemoveAccountAsync(secondId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A page size of zero is the unpaged request, and it answers with the unpaged representation rather than
    /// with an empty page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: this is the replacement for the legacy sentinel triple. <c>UserController.GetUsers</c>
    /// obtained every row by passing <c>-1, -1, -1</c>, and the provider then rewrote <c>pageIndex</c> to zero
    /// and <c>pageSize</c> to <c>Integer.MaxValue</c>. Minus one is not accepted as a page coordinate here
    /// under any reading, because it is a real key elsewhere in this very schema: <c>Portals.PortalID</c> is
    /// <c>IDENTITY(-1, 1)</c>. Zero carries the request instead, and the envelope reports itself as unpaged so
    /// the distinction survives to the caller.
    /// </remarks>
    [Fact]
    public async Task ListAsync_WithAPageSizeOfZero_AnswersWithTheUnpagedRepresentation()
    {
        int portalId = await CreatePortalAsync();
        int firstId = await CreateAccountAsync(portalId, FormattableString.Invariant($"unpaged1_{Suffix()}"));
        int secondId = await CreateAccountAsync(portalId, FormattableString.Invariant($"unpaged2_{Suffix()}"));
        int thirdId = await CreateAccountAsync(portalId, FormattableString.Invariant($"unpaged3_{Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            PagedResult<User> everything = await ListPageAsync(users, portalId, 0, 0);

            everything.IsUnpaged.Should().BeTrue("a page size of zero is the unpaged request");
            everything.PageSize.Should().Be(0);
            everything.PageIndex.Should().Be(0, "an unpaged answer sits at the only page there is");
            everything.Items.Should().HaveCount(3, "every member of the tenant is returned");
            everything.TotalCount.Should().Be(3, "the unpaged total is the number of rows returned");
            everything.TotalCount.Should().NotBe(-1);
            everything.Items.Select(account => account.UserId).Should().BeEquivalentTo(new[] { firstId, secondId, thirdId });
            everything.HasNextPage.Should().BeFalse("an unpaged answer has no following page");
            everything.HasPreviousPage.Should().BeFalse("an unpaged answer has no preceding page");

            foreach (User account in everything.Items)
            {
                AssertMembershipSnapshotComposed(account, nameof(IUserRepository.ListAsync));
            }
        }
        finally
        {
            await RemoveAccountAsync(firstId);
            await RemoveAccountAsync(secondId);
            await RemoveAccountAsync(thirdId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Staging a write assigns no key. The generated identifier appears only once the unit of work commits, and
    /// the commit reports how many rows it wrote.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: every legacy <c>Add*</c> member returned an <c>Integer</c> because each stored procedure
    /// ended in <c>SCOPE_IDENTITY()</c>, so writing and answering with a key were the same act. Under Entity
    /// Framework they cannot be: the key is generated by the database during the commit. A staging member that
    /// answered with an identifier would therefore have to commit on the caller's behalf, which would dissolve
    /// the unit of work and make a multi-table write non-atomic - exactly the property the legacy portal
    /// creation lacked.
    /// </para>
    /// <para>
    /// The contract expresses this in the strongest available form: <c>Add</c> is a synchronous <c>void</c>, so
    /// there is not even a task to await, let alone a key to misread. Asserting the key is still unassigned
    /// after staging and before committing is the cleanest proof the boundary holds; asserting it is populated
    /// afterwards proves the commit is what fills it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StagedWrites_AssignTheGeneratedKeyOnlyWhenTheUnitOfWorkCommits()
    {
        int portalId = await CreatePortalAsync();
        int userId = 0;

        try
        {
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                User account = new()
                {
                    Username = FormattableString.Invariant($"staged_{Suffix()}"),
                    FirstName = "Staged",
                    LastName = "Write",
                    DisplayName = "Staged Write",
                    Email = "staged@example.test",
                    IsSuperUser = false,
                    UpdatePassword = false,
                };

                account.UserId.Should().Be(0, "an unsaved entity carries no generated key");

                users.Add(account);

                account.UserId.Should().Be(
                    0,
                    "staging registers the insert with the change tracker and performs no input or output, so "
                    + "the database has not yet been asked for a key");
                account.Identity.Should().Be(
                    account.UserId, "the abstract identity projects the key property rather than shadowing it");

                int affected = await unitOfWork.SaveChangesAsync();

                affected.Should().BePositive("the commit reports the number of rows it actually wrote");
                account.UserId.Should().BePositive("the commit is what populates the generated key");
                account.Identity.Should().Be(account.UserId);

                userId = account.UserId;
            }

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IUserRepository reader = verifying.ServiceProvider.GetRequiredService<IUserRepository>();

            User? persisted = await reader.GetAsync(null, userId);
            persisted.Should().NotBeNull("the committed row is readable through a fresh scope");
            persisted!.UserId.Should().Be(userId);
        }
        finally
        {
            if (userId != 0)
            {
                await RemoveAccountAsync(userId);
            }

            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// The account and its per-portal membership are staged separately and committed by a single call, which is
    /// what preserves the atomicity the one legacy statement had.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy membership <c>AddUser</c> took ten positional arguments and wrote both the
    /// <c>Users</c> row and the per-portal <c>UserPortals</c> row in one statement. It had to, because
    /// <c>IsApproved</c> is a per-portal fact while the account itself carries no portal identifier at all -
    /// the <c>User</c> entity has no <c>PortalId</c> scalar, deliberately. The target decomposes the write
    /// across two entities, so the atomicity has to be restored by the unit of work rather than by the
    /// statement.
    /// </para>
    /// <para>
    /// The membership is joined to the account through the navigation property rather than by copying an
    /// identifier, because at staging time there is no identifier to copy. That is the point: the change
    /// tracker orders the two inserts and threads the generated key into the dependent row, so one commit
    /// writes both. Setting the foreign key by hand would have required committing the account first, which is
    /// precisely the split this test exists to rule out.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AddAndAddMembership_CommitAtomicallyUnderASingleSaveChanges()
    {
        int portalId = await CreatePortalAsync();
        int userId = 0;

        try
        {
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                User account = new()
                {
                    Username = FormattableString.Invariant($"atomic_{Suffix()}"),
                    FirstName = "Atomic",
                    LastName = "Member",
                    DisplayName = "Atomic Member",
                    Email = "atomic@example.test",
                    IsSuperUser = false,
                    UpdatePassword = false,
                };

                UserPortal membership = new()
                {
                    PortalId = portalId,
                    CreatedDate = DateTime.UtcNow,
                    IsAuthorised = true,
                    User = account,
                };

                users.Add(account);
                users.AddMembership(membership);

                account.UserId.Should().Be(0, "neither half is written until the unit of work commits");
                membership.UserPortalId.Should().Be(0, "the membership's own generated key is unassigned too");
                membership.UserId.Should().Be(0, "the foreign key is threaded by the commit, not by the caller");

                int affected = await unitOfWork.SaveChangesAsync();

                affected.Should().BeGreaterThanOrEqualTo(
                    2, "one commit wrote both the account row and the membership row");

                account.UserId.Should().BePositive();
                membership.UserId.Should().Be(
                    account.UserId,
                    "the change tracker ordered the inserts and threaded the generated account key into the "
                    + "dependent membership row");
                membership.UserPortalId.Should().BePositive(
                    "UserPortals carries its own identity column alongside the composite primary key");

                userId = account.UserId;
            }

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IUserRepository reader = verifying.ServiceProvider.GetRequiredService<IUserRepository>();

            // Both halves are present after the single commit, which is the atomicity claim itself.
            (await reader.GetAsync(portalId, userId)).Should().NotBeNull(
                "the account resolves through the tenant, which requires the membership row to exist");

            UserPortal? committed = await reader.GetMembershipAsync(portalId, userId);
            committed.Should().NotBeNull();
            committed!.UserId.Should().Be(userId);
            committed.PortalId.Should().Be(portalId);
            committed.IsAuthorised.Should().BeTrue();
        }
        finally
        {
            if (userId != 0)
            {
                await RemoveAccountAsync(userId);
            }

            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Deleting the account removes it and cascades into the membership rows that depended on it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the terminal <c>dbo.Users</c> table carries no deletion flag, so there is no soft delete to
    /// reproduce - a removal is a removal. The legacy administration screens removed a person from one tenant by
    /// deleting the membership row and only deleted the account once no membership remained, and that ordering
    /// is preserved as a caller obligation rather than hidden inside the repository. This test takes the second
    /// step directly and proves the cascade the schema declares actually fires, so an orphaned membership row
    /// cannot survive its account.
    /// </remarks>
    [Fact]
    public async Task Remove_DeletesTheAccountAndCascadesIntoItsMembership()
    {
        int portalId = await CreatePortalAsync();
        int userId = await CreateAccountAsync(portalId, FormattableString.Invariant($"doomed_{Suffix()}"));
        bool removed = false;

        try
        {
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await users.DeleteCredentialAsync(userId);

                User? doomed = await users.GetAsync(null, userId);
                doomed.Should().NotBeNull();

                users.Remove(doomed!);

                (await users.GetMembershipAsync(portalId, userId)).Should().NotBeNull(
                    "staging the deletion has not yet reached the database");

                int affected = await unitOfWork.SaveChangesAsync();
                affected.Should().BePositive("the commit reports the rows it removed");
                removed = true;
            }

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IUserRepository reader = verifying.ServiceProvider.GetRequiredService<IUserRepository>();

            (await reader.GetAsync(null, userId)).Should().BeNull("the account is gone");
            (await reader.GetMembershipAsync(portalId, userId)).Should().BeNull(
                "the membership row cascaded with the account it depended on");
        }
        finally
        {
            if (!removed)
            {
                await RemoveAccountAsync(userId);
            }

            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Two accounts may hold one address. The store enforces no uniqueness on an email, and nothing in the
    /// persistence path adds any.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Website/release.config</c> registers the membership provider with
    /// <c>requiresUniqueEmail="false"</c>, so a shared address was legal in the original installation and
    /// remains legal here. Tightening it during a migration would lock existing people out of their own
    /// accounts, which is why it is preserved rather than corrected. The shipped seed data is the strongest
    /// corroboration available: the Host account was seeded with the address <c>host</c> and the Administrator
    /// with <c>admin</c>, neither of which is even a well-formed address.
    /// </para>
    /// <para>
    /// <c>EmailExistsAsync</c> exists on the contract and is asserted here, but note carefully what it is: a
    /// pre-write question the Application layer asks so it can answer with a <c>Result</c> instead of letting an
    /// exception escape. It is not an enforced constraint, and this test proves that distinction by writing the
    /// duplicate anyway and finding both rows intact afterwards.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoAccountsMayShareOneAddress_AndNothingEnforcesOtherwise()
    {
        int portalId = await CreatePortalAsync();
        string shared = FormattableString.Invariant($"shared_{Suffix()}@example.test");

        int firstId = await CreateAccountAsync(portalId, FormattableString.Invariant($"share1_{Suffix()}"), email: shared);
        int secondId = await CreateAccountAsync(portalId, FormattableString.Invariant($"share2_{Suffix()}"), email: shared);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            User? first = await users.GetAsync(portalId, firstId);
            User? second = await users.GetAsync(portalId, secondId);

            first.Should().NotBeNull("the first account persisted");
            second.Should().NotBeNull("the second account persisted despite reusing the address");
            first!.Email.Should().Be(shared);
            second!.Email.Should().Be(shared);
            first.UserId.Should().NotBe(second.UserId, "they are two distinct accounts, not one");

            IReadOnlyList<int> byAddress = await ListByEmailPrefixAsync(users, portalId, shared);
            byAddress.Should().BeEquivalentTo(
                new[] { firstId, secondId }, "an address filter answers with every holder of that address");

            // The pre-write guard reports the duplicate truthfully. It informs a decision above; it does not
            // prevent the write, as the two persisted rows above already demonstrate.
            (await users.EmailExistsAsync(portalId, shared)).Should().BeTrue(
                "the guard reports that the address is already in use");
            (await users.EmailExistsAsync(portalId, shared, excludingUserId: firstId)).Should().BeTrue(
                "excluding one holder still leaves the other, so the address remains in use");

            string unused = FormattableString.Invariant($"unused_{Suffix()}@example.test");
            (await users.EmailExistsAsync(portalId, unused)).Should().BeFalse(
                "an address nobody holds is reported as unused");
        }
        finally
        {
            await RemoveAccountAsync(firstId);
            await RemoveAccountAsync(secondId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Zero and minus one are never account identifiers, in deliberate contrast with the tenant and role
    /// identifiers of this same schema, where both values are real keys.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: RULE T7. <c>Library/Components/Shared/Null.vb</c> defined <c>NullInteger</c> as minus one and
    /// the legacy data layer used it to mean "absent". That convention is unusable in this schema because the
    /// identity seeds collide with it head-on: <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one
    /// is the first real tenant, while <c>Roles.RoleID</c>, <c>Tabs.TabID</c>, <c>Modules.ModuleID</c> and
    /// <c>RoleGroups.RoleGroupID</c> all seed at zero. <c>Users.UserID</c> seeds at one, and it is the odd one
    /// out - which is exactly why an account identifier of zero or minus one can be recognised as absent while
    /// a tenant or role identifier of the same value cannot.
    /// </para>
    /// <para>
    /// The contrast is the assertion. Testing only that a user identifier of minus one finds nothing would
    /// read as a general rule about identifiers, and generalising it is the mistake this test exists to
    /// prevent: absence is expressed as <see langword="null"/> throughout, never as a sentinel identifier.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ZeroAndMinusOneAreNeverAccountIdentifiers_UnlikeTenantAndRoleIdentifiers()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        (await users.GetAsync(null, 0)).Should().BeNull(
            "Users.UserID seeds at one, so zero can never identify an account");
        (await users.GetAsync(null, -1)).Should().BeNull(
            "minus one was the legacy absence sentinel and is not a valid account identifier either");
        (await users.GetAsync(_fixture.Seed.PortalId, 0)).Should().BeNull();
        (await users.GetAsync(_fixture.Seed.PortalId, -1)).Should().BeNull();

        (await users.GetMembershipAsync(_fixture.Seed.PortalId, 0)).Should().BeNull();
        (await users.GetMembershipAsync(_fixture.Seed.PortalId, -1)).Should().BeNull();

        PagedResult<User> members = await ListPageAsync(users, _fixture.Seed.PortalId, 0, 0);
        members.Items.Should().NotBeEmpty("the seeded tenant has members, so this is not a vacuous check");
        members.Items.Should().OnlyContain(
            account => account.UserId >= 1, "every account identifier the store issues starts at one");

        IReadOnlyList<User> hosts = await users.ListSuperUsersAsync();
        hosts.Should().OnlyContain(account => account.UserId >= 1);

        // The contrast. The seeded tenant resolves through its own identifier even though the seed of that
        // identity column is minus one, and a role identifier of zero is likewise a real key. Neither value
        // may be read as "absent" for those entities.
        _fixture.Seed.PortalId.Should().BeGreaterThanOrEqualTo(
            -1, "Portals.PortalID is IDENTITY(-1, 1), so the first tenant of an installation bears minus one");
        (await users.GetAsync(_fixture.Seed.PortalId, _fixture.Seed.AdminUserId)).Should().NotBeNull(
            "the tenant identifier resolves an account, which is only possible because it is a real key rather "
            + "than a sentinel");

        _fixture.Seed.AdministratorRoleId.Should().BeGreaterThanOrEqualTo(
            0, "Roles.RoleID is IDENTITY(0, 1), so zero is a legitimate role identifier");
        (await roles.GetByIdAsync(_fixture.Seed.AdministratorRoleId, _fixture.Seed.PortalId)).Should().NotBeNull(
            "the role resolves by that identifier, so zero-or-above must never be treated as absent");
    }

    /// <summary>
    /// An empty string round-trips as an empty string and a false boolean is stored as false. Neither collapses
    /// into a database null.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: RULE T7, and this is the divergence with the largest reach. <c>Null.NullString</c> was the
    /// EMPTY STRING rather than <see langword="null"/>, and <c>Null.NullBoolean</c> was <c>False</c>. The
    /// legacy <c>GetNull</c> helper converted a value equal to its type's sentinel into <c>DBNull</c> on the way
    /// to the database, so <b>every false boolean was persisted as SQL NULL</b> and an empty string was
    /// indistinguishable from one. Writing <c>false</c> where the legacy layer wrote NULL changes the stored
    /// row, so the behaviour cannot be left implicit.
    /// </para>
    /// <para>
    /// The chosen behaviour, pinned here, is that the value written is the value supplied: an empty string
    /// stores as an empty string and <c>false</c> stores as <c>false</c>. The terminal schema is what makes this
    /// the only coherent option - <c>Users.IsSuperUser</c> and <c>UserPortals.Authorised</c> are both
    /// <c>NOT NULL</c>, so a NULL could not be written even if fidelity to the sentinel were wanted. Absence is
    /// carried by nullable types instead, which is why the assertions below read the raw columns as well as the
    /// entity: only the raw read can tell an empty string apart from a null.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEmptyStringAndAFalseBooleanAreStoredAsThemselvesRatherThanAsNull()
    {
        int portalId = await CreatePortalAsync();
        string userName = FormattableString.Invariant($"sentinel_{Suffix()}");

        int userId = await CreateAccountAsync(
            portalId,
            userName,
            email: string.Empty,
            authorised: false,
            isSuperUser: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            User? account = await users.GetAsync(portalId, userId);
            account.Should().NotBeNull();

            account!.Email.Should().NotBeNull(
                "the legacy sentinel for an absent string was the empty string, so a round trip that produced "
                + "null would have silently changed the value");
            account.Email.Should().BeEmpty("the value supplied is the value stored");
            account.IsSuperUser.Should().BeFalse("false is stored as false, not as the absence of a value");

            UserPortal? membership = await users.GetMembershipAsync(portalId, userId);
            membership.Should().NotBeNull();
            membership!.IsAuthorised.Should().BeFalse(
                "an unauthorised membership is recorded as false rather than as null, which is what makes the "
                + "unauthorised listing filter answerable at all");

            // Only a raw read can distinguish an empty string from a null, because the materialiser presents
            // both as an absent-looking value on a nullable property. These two scalars are therefore the
            // actual assertion; the entity assertions above are the caller-visible consequence of them.
            int emailIsNull = await _fixture.Database.ScalarAsync<int>(
                """
                SELECT CASE WHEN [Email] IS NULL THEN 1 ELSE 0 END
                FROM [dbo].[Users]
                WHERE [UserID] = @userId;
                """,
                new Dictionary<string, object?> { ["userId"] = userId });

            emailIsNull.Should().Be(0, "the stored column holds an empty string, not a null");

            int superUserFlag = await _fixture.Database.ScalarAsync<int>(
                """
                SELECT CAST([IsSuperUser] AS int)
                FROM [dbo].[Users]
                WHERE [UserID] = @userId;
                """,
                new Dictionary<string, object?> { ["userId"] = userId });

            superUserFlag.Should().Be(0, "the false flag is stored as zero rather than as null");

            int authorisedFlag = await _fixture.Database.ScalarAsync<int>(
                """
                SELECT CAST([Authorised] AS int)
                FROM [dbo].[UserPortals]
                WHERE [UserId] = @userId AND [PortalId] = @portalId;
                """,
                new Dictionary<string, object?> { ["userId"] = userId, ["portalId"] = portalId });

            authorisedFlag.Should().Be(
                0,
                "the British-spelled Authorised column stores the false value it was given; the legacy layer "
                + "would have written NULL here because NullBoolean was False");
        }
        finally
        {
            await RemoveAccountAsync(userId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A staged write reports no sentinel. An expected clash is decided before the write by the existence
    /// guard, and the staging member neither swallows nor invents a return value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy membership <c>AddUser</c> wrapped its entire body in a bare
    /// <c>Catch ... Return -1</c>, so a duplicate username, a constraint violation and a dead connection were
    /// indistinguishable to the caller, and every one of them was reported as an ordinary expected outcome.
    /// That single line is what makes minus one carry a <b>fifth</b> meaning in this codebase, alongside the
    /// <c>Null.NullInteger</c> sentinel, the real <c>Portals.PortalID</c> key, the "all portals" wildcard in the
    /// alias reader, and the "All Users" pseudo-principal in the permission tables.
    /// </para>
    /// <para>
    /// Neither half is carried forward: nothing is caught and no sentinel is returned. Per the Minimal Change
    /// Clause the defect is annotated rather than corrected in place, and the correction lives in the target's
    /// shape instead - <c>Add</c> returns <c>void</c>, so there is no channel through which a sentinel could be
    /// smuggled back even if someone wanted to.
    /// </para>
    /// <para>
    /// The honest finding this test records is that the terminal <c>dbo.Users</c> table declares <b>no</b>
    /// unique index on either <c>Username</c> or <c>Email</c>, so the store itself rejects nothing. The guard is
    /// therefore <c>UsernameExistsAsync</c>, asked before the write by the Application service, and the
    /// assertions below pin that division of labour rather than asserting a constraint that does not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExistenceGuardsDecideAClashBeforeTheWrite_AndNoSentinelIsEverReturned()
    {
        int portalId = await CreatePortalAsync();
        string userName = FormattableString.Invariant($"guarded_{Suffix()}");
        int firstId = await CreateAccountAsync(portalId, userName);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            // The guard is installation-wide rather than tenant-scoped, matching the single Users table the
            // whole installation shares.
            (await users.UsernameExistsAsync(userName)).Should().BeTrue(
                "the name is taken, which is the answer the Application service turns into a Result failure "
                + "instead of letting an exception escape");

            (await users.UsernameExistsAsync(userName.ToUpperInvariant())).Should().BeTrue(
                "the guard matches case-insensitively, so a differently cased duplicate is still caught");

            (await users.UsernameExistsAsync(userName, excludingUserId: firstId)).Should().BeFalse(
                "excluding the holder itself is what lets an account be saved without clashing with its own "
                + "stored name");

            (await users.UsernameExistsAsync(FormattableString.Invariant($"free_{Suffix()}"))).Should().BeFalse(
                "an unused name is free");

            User? persisted = await users.GetAsync(portalId, firstId);
            persisted.Should().NotBeNull();
            persisted!.UserId.Should().NotBe(-1, "no member of this contract can answer with the legacy sentinel");
            persisted.UserId.Should().BePositive();

            // The absence of a match is null, never a sentinel-bearing instance. A caller that tested a
            // returned identifier for minus one would be testing something that cannot occur.
            (await users.GetByUsernameAsync(portalId, FormattableString.Invariant($"absent_{Suffix()}")))
                .Should().BeNull("an unmatched lookup answers with null rather than with a sentinel row");
        }
        finally
        {
            await RemoveAccountAsync(firstId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Profile declarations and profile values persist through their own repository, and the values they hold
    /// drive the profile-property filter of the account listing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the seam between the two contracts. <c>IUserProfileRepository</c> owns the declaration and the
    /// value rows, while the only profile-aware member of the account contract is the listing's paired filter -
    /// a definition identifier and a value prefix. Exercising the filter without writing real values through the
    /// profile repository would prove nothing, so both are driven here together.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy surface had a single insert-or-update member,
    /// <c>UpdateProfileProperty(ProfileId, UserId, PropertyDefinitionID, PropertyValue, Visibility,
    /// LastUpdatedDate)</c>, which the target splits into an explicit add and an explicit update. Its
    /// <c>LastUpdatedDate</c> argument becomes an entity property rather than a parameter -
    /// <c>UserProfile</c> is the only in-scope table that carries the column. Note also that
    /// <c>GetProfileValuesAsync</c> takes only a user identifier and is deliberately <b>not</b> tenant-scoped:
    /// the surviving legacy row path was keyed by user alone, and only the superseded serialised-blob
    /// personalisation path was portal-scoped. That structural difference is the evidence they were two
    /// generations of the same idea, and the blob path is out of scope entirely.
    /// </para>
    /// <para>
    /// MIGRATION: the value filters are matched as PREFIXES rather than as substrings, because each legacy
    /// search branch appended one trailing percent sign to the search text before handing it to the provider.
    /// The declaration's own visibility is a plain integer rather than an enumeration, deliberately - the legacy
    /// visibility mode is not among the domain enumerations.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProfileValues_PersistThroughTheirOwnRepositoryAndDriveTheListingFilter()
    {
        int portalId = await CreatePortalAsync();
        string propertyName = FormattableString.Invariant($"City{Suffix()}");
        int matchingUserId = await CreateAccountAsync(portalId, FormattableString.Invariant($"profiled_{Suffix()}"));
        int bareUserId = await CreateAccountAsync(portalId, FormattableString.Invariant($"unprofiled_{Suffix()}"));
        int definitionId = 0;

        try
        {
            // ---- the declaration, staged then committed -------------------------------------------------
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition definition = new()
                {
                    PortalId = portalId,
                    PropertyName = propertyName,
                    PropertyCategory = "Address",
                    DataType = 0,
                    Length = 50,
                    IsRequired = false,
                    IsVisible = true,
                    IsDeleted = false,
                    ViewOrder = 1,
                };

                await profiles.AddDefinitionAsync(definition);

                definition.PropertyDefinitionId.Should().Be(
                    0, "the declaration is staged, so its generated key is not yet assigned");

                await unitOfWork.SaveChangesAsync();

                definition.PropertyDefinitionId.Should().BePositive("the commit assigns the key");
                definitionId = definition.PropertyDefinitionId;
            }

            // ---- the declaration reads back through all three lookups ----------------------------------
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                ProfilePropertyDefinition? byId = await profiles.GetDefinitionByIdAsync(definitionId);
                byId.Should().NotBeNull();
                byId!.PropertyName.Should().Be(propertyName);
                byId.PortalId.Should().Be(
                    portalId,
                    "a tenant-owned declaration carries its tenant; the property is nullable so that a "
                    + "host-level declaration can be a genuine null rather than the legacy minus-one sentinel");
                byId.PropertyCategory.Should().Be("Address");
                byId.IsVisible.Should().BeTrue();
                byId.IsDeleted.Should().BeFalse();

                ProfilePropertyDefinition? byName = await profiles.GetDefinitionByNameAsync(portalId, propertyName);
                byName.Should().NotBeNull("the name lookup is tenant-scoped and takes a non-nullable identifier");
                byName!.PropertyDefinitionId.Should().Be(definitionId);

                (await profiles.GetDefinitionByNameAsync(UnknownPortalId, propertyName)).Should().BeNull(
                    "a declaration belonging to another tenant is not visible through this tenant");
                (await profiles.GetDefinitionByIdAsync(UnknownUserId)).Should().BeNull(
                    "an unknown key answers with null rather than with a sentinel-bearing instance");

                IReadOnlyList<ProfilePropertyDefinition> forPortal = await profiles.GetDefinitionsByPortalIdAsync(portalId);
                forPortal.Select(candidate => candidate.PropertyDefinitionId).Should().Contain(definitionId);
            }

            // ---- the declaration is amended in place ---------------------------------------------------
            // MIGRATION: the legacy UpdatePropertyDefinition took ten positional arguments - data type,
            //            default value, category, name, required, validation expression, view order, visible
            //            and length among them. All ten collapse into the single entity parameter amended here,
            //            so no member of this contract exceeds two arguments plus the cancellation token.
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition? amending = await profiles.GetDefinitionByIdAsync(definitionId);
                amending.Should().NotBeNull();

                amending!.PropertyCategory = "Contact";
                amending.IsRequired = true;
                amending.IsVisible = false;
                amending.ViewOrder = 7;
                amending.ValidationExpression = "^[A-Za-z ]+$";
                amending.Length = 80;

                await profiles.UpdateDefinitionAsync(amending);
                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                ProfilePropertyDefinition? amended = await profiles.GetDefinitionByIdAsync(definitionId);
                amended.Should().NotBeNull();
                amended!.PropertyCategory.Should().Be("Contact", "the amendment persisted");
                amended.IsRequired.Should().BeTrue();
                amended.IsVisible.Should().BeFalse();
                amended.ViewOrder.Should().Be(7);
                amended.ValidationExpression.Should().Be("^[A-Za-z ]+$");
                amended.Length.Should().Be(80);
                amended.PropertyName.Should().Be(propertyName, "the amendment left the identifying name alone");
                amended.PortalId.Should().Be(portalId, "and left the owning tenant alone");
            }

            // ---- a value, staged then committed --------------------------------------------------------
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserProfileValue value = new()
                {
                    UserId = matchingUserId,
                    PropertyDefinitionId = definitionId,
                    PropertyValue = "London",
                    Visibility = 0,
                    LastUpdatedDate = DateTime.UtcNow,
                };

                await profiles.AddProfileValueAsync(value);

                value.ProfileId.Should().Be(0, "the value is staged like every other write");

                await unitOfWork.SaveChangesAsync();

                value.ProfileId.Should().BePositive("the commit assigns the value's key");
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                IReadOnlyList<UserProfileValue> held = await profiles.GetProfileValuesAsync(matchingUserId);
                held.Should().ContainSingle("the account holds exactly the one value written");
                held[0].PropertyValue.Should().Be("London");
                held[0].PropertyDefinitionId.Should().Be(definitionId);
                held[0].Visibility.Should().Be(0, "visibility is a plain integer rather than an enumeration");
                held[0].LastUpdatedDate.Should().NotBe(
                    default, "the timestamp is an entity property, so the caller supplies it and it persists");

                (await profiles.GetProfileValuesAsync(bareUserId)).Should().BeEmpty(
                    "an account holding no value answers with an empty list");
            }

            // ---- the listing filter, driven by those values --------------------------------------------
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

                PagedResult<User> byDefinitionAndPrefix = await users.ListAsync(
                    portalId, 0, 0, null, null, null, definitionId, "Lon", null,
                    includeUnauthorised: true, includeSuperUsers: true);

                byDefinitionAndPrefix.Items.Select(account => account.UserId).Should().BeEquivalentTo(
                    new[] { matchingUserId }, "only the holder of a matching value is selected");
                byDefinitionAndPrefix.TotalCount.Should().Be(1);
                byDefinitionAndPrefix.TotalCount.Should().NotBe(-1);

                foreach (User account in byDefinitionAndPrefix.Items)
                {
                    AssertMembershipSnapshotComposed(account, nameof(IUserRepository.ListAsync));
                }

                PagedResult<User> byDefinitionAlone = await users.ListAsync(
                    portalId, 0, 0, null, null, null, definitionId, null, null,
                    includeUnauthorised: true, includeSuperUsers: true);

                byDefinitionAlone.Items.Select(account => account.UserId).Should().BeEquivalentTo(
                    new[] { matchingUserId }, "the definition alone selects everyone holding any value for it");

                PagedResult<User> unmatchedPrefix = await users.ListAsync(
                    portalId, 0, 0, null, null, null, definitionId, "Man", null,
                    includeUnauthorised: true, includeSuperUsers: true);

                unmatchedPrefix.Items.Should().BeEmpty("the stored value does not begin with that prefix");
                unmatchedPrefix.TotalCount.Should().Be(0, "an empty match is a zero total");
                unmatchedPrefix.TotalCount.Should().NotBe(-1, "and never the legacy sentinel");

                PagedResult<User> notASubstring = await users.ListAsync(
                    portalId, 0, 0, null, null, null, definitionId, "ondon", null,
                    includeUnauthorised: true, includeSuperUsers: true);

                notASubstring.Items.Should().BeEmpty(
                    "the value is matched as a prefix, not as a substring - the legacy search appended a single "
                    + "trailing wildcard and nothing more");
            }

            // ---- the update path replaces the value ----------------------------------------------------
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                IReadOnlyList<UserProfileValue> held = await profiles.GetProfileValuesAsync(matchingUserId);
                UserProfileValue existing = held[0];

                existing.PropertyValue = "Manchester";
                existing.LastUpdatedDate = DateTime.UtcNow;

                await profiles.UpdateProfileValueAsync(existing);
                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                IReadOnlyList<UserProfileValue> held = await profiles.GetProfileValuesAsync(matchingUserId);
                held.Should().ContainSingle("the update replaced the value rather than adding a second row");
                held[0].PropertyValue.Should().Be("Manchester");

                PagedResult<User> nowMatching = await users.ListAsync(
                    portalId, 0, 0, null, null, null, definitionId, "Man", null,
                    includeUnauthorised: true, includeSuperUsers: true);
                nowMatching.Items.Select(account => account.UserId).Should().BeEquivalentTo(new[] { matchingUserId });

                PagedResult<User> noLongerMatching = await users.ListAsync(
                    portalId, 0, 0, null, null, null, definitionId, "Lon", null,
                    includeUnauthorised: true, includeSuperUsers: true);
                noLongerMatching.Items.Should().BeEmpty("the previous value is gone");
            }

            // ---- removing the declaration cascades into its values -------------------------------------
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await profiles.DeleteDefinitionAsync(definitionId);
                await unitOfWork.SaveChangesAsync();
            }

            definitionId = 0;

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                (await profiles.GetDefinitionByNameAsync(portalId, propertyName)).Should().BeNull(
                    "the declaration is removed outright - the deletion flag the entity carries is not used as a "
                    + "soft delete by this contract, which takes only an identifier and no mode argument");

                (await profiles.GetProfileValuesAsync(matchingUserId)).Should().BeEmpty(
                    "the values cascaded with the declaration they belonged to");
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // Removing an absent declaration is a no-op rather than a failure, which is what lets a
                // clean-up path run twice without special-casing.
                await profiles.DeleteDefinitionAsync(UnknownUserId);
                await unitOfWork.SaveChangesAsync();
            }
        }
        finally
        {
            if (definitionId != 0)
            {
                using IServiceScope scope = _fixture.Services.CreateScope();
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await profiles.DeleteDefinitionAsync(definitionId);
                await unitOfWork.SaveChangesAsync();
            }

            await RemoveAccountAsync(matchingUserId);
            await RemoveAccountAsync(bareUserId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Lists the identifiers a username-prefix filter selects.</summary>
    /// <param name="users">The repository.</param>
    /// <param name="portalId">The tenant to search.</param>
    /// <param name="prefix">The prefix to match.</param>
    /// <returns>The identifiers the filter selected.</returns>
    private static async Task<IReadOnlyList<int>> ListByUserNamePrefixAsync(IUserRepository users, int portalId, string prefix)
    {
        PagedResult<User> page = await users.ListAsync(
            portalId, 0, 0, null, prefix, null, null, null, null,
            includeUnauthorised: true, includeSuperUsers: true);

        return page.Items.Select(account => account.UserId).ToList();
    }

    /// <summary>Lists the identifiers an address-prefix filter selects.</summary>
    /// <param name="users">The repository.</param>
    /// <param name="portalId">The tenant to search.</param>
    /// <param name="prefix">The prefix to match.</param>
    /// <returns>The identifiers the filter selected.</returns>
    private static async Task<IReadOnlyList<int>> ListByEmailPrefixAsync(IUserRepository users, int portalId, string prefix)
    {
        PagedResult<User> page = await users.ListAsync(
            portalId, 0, 0, null, null, prefix, null, null, null,
            includeUnauthorised: true, includeSuperUsers: true);

        return page.Items.Select(account => account.UserId).ToList();
    }

    /// <summary>Lists the identifiers a free-text search selects.</summary>
    /// <param name="users">The repository.</param>
    /// <param name="portalId">The tenant to search.</param>
    /// <param name="query">The fragment to match.</param>
    /// <returns>The identifiers the search selected.</returns>
    private static async Task<IReadOnlyList<int>> ListByQueryAsync(IUserRepository users, int portalId, string query)
    {
        PagedResult<User> page = await users.ListAsync(
            portalId, 0, 0, query, null, null, null, null, null,
            includeUnauthorised: true, includeSuperUsers: true);

        return page.Items.Select(account => account.UserId).ToList();
    }

    /// <summary>Creates a bare tenant through the repository.</summary>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreatePortalAsync()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal portal = new()
        {
            PortalName = FormattableString.Invariant($"Account Tenant {Suffix()}"),
            UserRegistration = UserRegistrationMode.PrivateRegistration,
            BannerAdvertising = BannerAdvertisingMode.None,
            Currency = "USD",
            HostFee = 0m,
            HostSpace = 0,
            PortalGuid = Guid.NewGuid(),
            DefaultLanguage = "en-US",
            TimeZoneOffset = -8,
            HomeDirectory = string.Empty,
            PageQuota = 0,
            UserQuota = 0,
        };

        await portals.AddAsync(portal);
        await unitOfWork.SaveChangesAsync();

        return portal.PortalId;
    }

    /// <summary>Removes a tenant created by this suite.</summary>
    /// <param name="portalId">The tenant to remove.</param>
    /// <returns>A task that completes when the tenant is gone.</returns>
    private async Task RemovePortalAsync(int portalId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal? doomed = await portals.GetByIdAsync(portalId);

        if (doomed is not null)
        {
            await portals.DeleteAsync(doomed.PortalId);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Creates an account, optionally with a tenancy and a credential.</summary>
    /// <param name="portalId">The tenant to join, or <see langword="null"/> for no tenancy.</param>
    /// <param name="userName">The username, which must be unique across the installation.</param>
    /// <param name="displayName">The display name; defaults to the username.</param>
    /// <param name="email">The address; defaults to one derived from the username.</param>
    /// <param name="authorised">Whether the tenancy is authorised.</param>
    /// <param name="approved">Whether the credential is approved.</param>
    /// <param name="isSuperUser">Whether the account is a host account.</param>
    /// <param name="withCredential">Whether to record a credential in the external store.</param>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreateAccountAsync(
        int? portalId,
        string userName,
        string? displayName = null,
        string? email = null,
        bool authorised = true,
        bool approved = true,
        bool isSuperUser = false,
        bool withCredential = true)
    {
        int userId;

        using (IServiceScope scope = _fixture.Services.CreateScope())
        {
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            User account = new()
            {
                Username = userName,
                FirstName = "Persistence",
                LastName = "Suite",
                DisplayName = displayName ?? userName,
                Email = email ?? FormattableString.Invariant($"{userName}@example.test"),
                IsSuperUser = isSuperUser,
                UpdatePassword = false,
            };

            users.Add(account);
            await unitOfWork.SaveChangesAsync();

            userId = account.UserId;

            if (portalId.HasValue)
            {
                users.AddMembership(new UserPortal
                {
                    UserId = userId,
                    PortalId = portalId.Value,
                    CreatedDate = DateTime.UtcNow,
                    IsAuthorised = authorised,
                });

                await unitOfWork.SaveChangesAsync();
            }
        }

        if (withCredential)
        {
            using IServiceScope credentialScope = _fixture.Services.CreateScope();
            IUserRepository users = credentialScope.ServiceProvider.GetRequiredService<IUserRepository>();

            bool created = await users.CreateCredentialAsync(userId, StoredHash, approved, DateTime.UtcNow);
            created.Should().BeTrue("the account was just created, so its credential must be recordable");
        }

        return userId;
    }

    /// <summary>Removes an account created by this suite, including its credential.</summary>
    /// <param name="userId">The account to remove.</param>
    /// <returns>A task that completes when the account is gone.</returns>
    /// <remarks>
    /// The credential is deleted first and explicitly. The external membership tables carry no foreign key to
    /// the mapped account table, so removing the account cannot cascade into them and a credential left behind
    /// would keep the username reserved in the store.
    /// </remarks>
    private async Task RemoveAccountAsync(int userId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await users.DeleteCredentialAsync(userId);

        User? doomed = await users.GetAsync(null, userId);

        if (doomed is not null)
        {
            users.Remove(doomed);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Records a role assignment with explicit dates.</summary>
    /// <param name="userId">The account being assigned.</param>
    /// <param name="roleId">The role being assigned.</param>
    /// <param name="effective">When the assignment begins, or <see langword="null"/> for immediately.</param>
    /// <param name="expiry">When the assignment ends, or <see langword="null"/> for never.</param>
    /// <returns>A task that completes when the assignment exists.</returns>
    private async Task AddAssignmentAsync(int userId, int roleId, DateTime? effective, DateTime? expiry)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await roles.AddUserRoleAsync(new UserRole
        {
            UserId = userId,
            RoleId = roleId,
            EffectiveDate = effective,
            ExpiryDate = expiry,
            IsTrialUsed = false,
        });

        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Reads the recorded failure count straight out of the external membership store.</summary>
    /// <param name="userName">The account to read.</param>
    /// <returns>The recorded failure count.</returns>
    private Task<int> ReadFailedAttemptCountAsync(string userName)
    {
        return _fixture.Database.ScalarAsync<int>(
            """
            SELECT am.[FailedPasswordAttemptCount]
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });
    }

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Reads one page of a tenant's membership with every optional filter left open, so that only the paging
    /// coordinates vary between calls.
    /// </summary>
    /// <param name="users">The repository.</param>
    /// <param name="portalId">The tenant to page through.</param>
    /// <param name="pageIndex">The zero-based page index, as declared by the envelope.</param>
    /// <param name="pageSize">The page size; zero requests the unpaged representation.</param>
    /// <returns>The page the store answered with.</returns>
    /// <remarks>
    /// Unauthorised members and host accounts are both included deliberately. The paging assertions are about
    /// the envelope rather than about the filters, so leaving a filter engaged would let a defect in paging hide
    /// behind a smaller result set.
    /// </remarks>
    private static Task<PagedResult<User>> ListPageAsync(
        IUserRepository users,
        int portalId,
        int pageIndex,
        int pageSize)
    {
        return users.ListAsync(
            portalId, pageIndex, pageSize, null, null, null, null, null, null,
            includeUnauthorised: true, includeSuperUsers: true);
    }

    /// <summary>
    /// Asserts that an account carries the membership snapshot the repository composes rather than the defaults
    /// an unpopulated instance would show.
    /// </summary>
    /// <param name="account">The account a read path answered with.</param>
    /// <param name="readPath">The member that produced the account, named for the failure message.</param>
    /// <remarks>
    /// <para>
    /// This is the behavioural half of a two-part contract. <c>UserConfiguration</c> calls <c>Ignore</c> on
    /// eleven properties because they live in the externally installed <c>aspnet_*</c> tables, which the
    /// original upgrade scripts only ever ALTERed and never CREATEd, so they cannot be mapped as columns on
    /// <c>dbo.Users</c>. <c>DnnDbContextTests.Model_LeavesTheExternallyStoredAccountPropertyUnmapped</c> asserts
    /// the metadata half - that <c>FindProperty</c> answers null for each of them while the CLR property still
    /// exists. This helper asserts the consequence: unmapped in the model, yet populated on the way out of every
    /// read path.
    /// </para>
    /// <para>
    /// MIGRATION: <c>UserController.GetUserMembership(ByRef objUser)</c> grafted these facts onto an account
    /// through a separate call that a caller could simply forget, leaving an unapproved account
    /// indistinguishable from an approved one. There is deliberately no equivalent member to call here, which is
    /// what allows the <c>ByRef</c> argument to disappear rather than merely change shape - and it is why this
    /// assertion is applied to every read path rather than to one of them.
    /// </para>
    /// <para>
    /// SEVEN of the eleven ignored properties are composed, not all eleven, and the split is deliberate on both
    /// sides. Composed: approval, lockout, the creation instant and the four recorded timestamps. Deliberately
    /// left null: <c>IsOnline</c>, because the legacy presence records were kept current by a scheduled purge
    /// job that is out of scope, so deriving presence from a store nothing updates would invent an answer that
    /// looks authoritative and decays silently; and the three credential properties, because the snapshot type
    /// the read paths can obtain has no field capable of carrying a hash at all.
    /// </para>
    /// <para>
    /// That second half is asserted here as a POSITIVE requirement rather than merely left untested. A password
    /// hash reaching a listing would be a credential leak, and the assertion below is what makes its absence a
    /// tested property of every read path instead of an implementation detail that a later change could quietly
    /// undo. Only the credential reader may serve a hash, and only so that verification can compare one.
    /// </para>
    /// <para>
    /// Of the composed properties only approval, lockout and the creation instant are asserted as present. The
    /// four remaining timestamps are legitimately null for an account that has never signed in, been locked out
    /// or changed its password, so requiring them would be asserting a fiction.
    /// </para>
    /// </remarks>
    private static void AssertMembershipSnapshotComposed(User account, string readPath)
    {
        string composed = FormattableString.Invariant(
            $"{readPath} composes the external membership snapshot, so this property must not read as its default merely because it is unmapped");

        account.IsApproved.Should().NotBeNull(composed);
        account.IsLockedOut.Should().NotBeNull(composed);
        account.CreatedDate.Should().NotBeNull(composed);

        account.CreatedDate.Should().NotBe(
            default(DateTime),
            "the creation instant comes from the credential record and is a real timestamp there");

        // MIGRATION: the deliberate omissions, asserted rather than assumed.
        string withheld = FormattableString.Invariant(
            $"{readPath} must never carry credential material - the snapshot type its read path can obtain has no field able to carry a hash, and that is the mechanism preventing a leak rather than a convention");

        account.PasswordHash.Should().BeNull(withheld);
        account.PasswordAnswer.Should().BeNull(withheld);
        account.PasswordQuestion.Should().BeNull(withheld);

        account.IsOnline.Should().BeNull(
            "presence is reported but never maintained: the legacy users-online window depended on records kept "
            + "current by a scheduled purge job that is out of scope, so a null is the honest answer and its "
            + "nullable type exists to express exactly that");
    }

    /// <summary>Creates a role in a tenant so that assignments can be recorded against it.</summary>
    /// <param name="portalId">The tenant that owns the role.</param>
    /// <param name="roleName">The role name, which is unique within a tenant.</param>
    /// <returns>The identifier the store assigned.</returns>
    /// <remarks>
    /// The role is staged and committed through its own repository, which keeps this suite's set-up on the same
    /// abstractions it is testing. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so the returned identifier may
    /// legitimately be zero - it must never be tested for truthiness.
    /// </remarks>
    private async Task<int> CreateRoleAsync(int portalId, string roleName)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Role role = new()
        {
            PortalId = portalId,
            RoleName = roleName,
            Description = "Created by the account persistence suite.",
            IsPublic = false,
            AutoAssignment = false,
        };

        await roles.AddAsync(role);
        await unitOfWork.SaveChangesAsync();

        return role.RoleId;
    }
}
