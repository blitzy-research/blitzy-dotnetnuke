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
}
