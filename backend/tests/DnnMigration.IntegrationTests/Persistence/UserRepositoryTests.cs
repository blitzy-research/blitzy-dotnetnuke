using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>Covers the account repository, including the external membership store that holds credentials.</summary>
/// <remarks>
/// <para>
/// This repository straddles two stores. Account facts live in the mapped <c>dbo.Users</c> table, while
/// credentials, approval and lockout live in the <c>aspnet_*</c> membership tables that the original
/// upgrade scripts only ever altered and never created.
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
    /// <summary>The first key <c>dbo.Portals.PortalID</c> issues, which the seeded tenant holds.</summary>
    /// <remarks>
    /// This is a REAL TENANT and not the host scope. <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), so -1 is the first tenant an installation has and is always
    /// addressable; the host scope is a SQL <c>NULL</c> portal, which
    /// <c>03.03.03.SqlDataProvider:L74-L83</c> established when it widened the column and migrated the rows
    /// with <c>SET PortalId = NULL WHERE PortalId = -1</c>.
    /// </remarks>
    private const int SeededTenantPortalId = -1;

    private const int UnknownPortalId = 987654;
    private const int UnknownUserId = 987654;
    private const int LockoutThreshold = 5;
    private const string StoredHash = "$2a$12$0123456789012345678901ualreadyahashnotaplaintextvalue0";
    private const string ReplacementHash = "$2a$12$abcdefghijklmnopqrstuvwxyzareplacementhashvalue000000000";

    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);

    /// <summary>Application name the external membership tables are keyed by.</summary>
    /// <remarks>
    /// Restated here rather than read from the store, which declares it privately. The value is the legacy
    /// installation's own application name and is a fact about the schema, so a disagreement between the
    /// two would make every membership seed in this suite attach to nothing - which is precisely what the
    /// dependant-row assertions would then report.
    /// </remarks>
    private const string MembershipApplication = "DotNetNuke";

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
    /// The installation-wide lookup exists so that a host account, which may hold no membership in the
    /// tenant being administered, can still be resolved. Callers accept the result only when the account
    /// turns out to be a host account, which is why the lookup itself is deliberately unscoped.
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
    /// This is deliberate rather than accidental. Sign-in has to be able to find an account that has not
    /// yet been authorised, because that is precisely the account that may present a verification code to
    /// have its membership approved.
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
    /// identifier, so a duplicate must be refused whichever tenant it is offered to.
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

    /// <summary>Role names name only the assignments that are in force at the moment being asked about.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An assignment carries an effective date and an expiry date because paid membership of a role begins
    /// and ends. Reading every assignment regardless of its dates would grant a lapsed subscriber the
    /// access they have stopped paying for, and would grant a future subscriber access before it starts.
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
    /// The difference is measured legacy behaviour, not an inconsistency. The legacy account search
    /// appended a single trailing wildcard to the username and address it was given, so both were prefix
    /// searches, while the general search box matched anywhere in the value.
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
    /// The listing therefore roots itself in a statement over the membership tables and composes the
    /// ordinary filters on top, which keeps the whole query a single round trip and lets the store apply
    /// approval before paging rather than after it.
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
    /// Accounts are ordered by display name, then username, then identifier, so paging cannot repeat or
    /// drop a row.
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

            (
                bool exists,
                string? hash,
                PasswordFormat? format,
                string? salt,
                bool approved,
                bool locked) = await users.GetCredentialStateAsync(userId);
            exists.Should().BeFalse("the account has been created but holds no credential yet");
            hash.Should().BeNull();
            format.Should().BeNull();
            salt.Should().BeNull();
            approved.Should().BeFalse();
            locked.Should().BeFalse();

            bool created = await users.CreateCredentialAsync(userId, StoredHash, isApproved: true, DateTime.UtcNow);
            created.Should().BeTrue();

            (exists, hash, format, salt, approved, locked) = await users.GetCredentialStateAsync(userId);
            exists.Should().BeTrue();
            hash.Should().Be(StoredHash);
            format.Should().Be(PasswordFormat.Hashed);
            salt.Should().BeEmpty("BCrypt embeds its salt in the stored representation");
            approved.Should().BeTrue();
            locked.Should().BeFalse();

            // The replacement is a COMPARE-AND-SWAP, so it carries the representation the read above returned.
            CredentialWriteOutcome replaced = await users.SetPasswordHashAsync(
                userId, ReplacementHash, StoredHash, DateTime.UtcNow);
            replaced.Should().Be(CredentialWriteOutcome.Replaced);

            CredentialWriteOutcome stale = await users.SetPasswordHashAsync(
                userId, StoredHash, StoredHash, DateTime.UtcNow);
            stale.Should().Be(
                CredentialWriteOutcome.Superseded,
                "an expectation that no longer holds must refuse the write rather than overwrite the newer "
                    + "credential");

            (_, hash, format, salt, _, _) = await users.GetCredentialStateAsync(userId);
            hash.Should().Be(ReplacementHash);
            format.Should().Be(PasswordFormat.Hashed);
            salt.Should().BeEmpty();

            MembershipWriteOutcome deleted = await users.DeleteCredentialAsync(userId);
            deleted.Should().Be(MembershipWriteOutcome.Recorded);

            (exists, hash, format, salt, _, _) = await users.GetCredentialStateAsync(userId);
            exists.Should().BeFalse();
            hash.Should().BeNull();
            format.Should().BeNull();
            salt.Should().BeNull();
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>
    /// The credential compare-and-swap compares BYTES, so an expectation that differs from the stored value
    /// only in letter case is refused even though the database itself is case-insensitive.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CredentialReplacement_ComparesTheExpectationByBytesRatherThanByCollation()
    {
        int userId = await CreateAccountAsync(
            portalId: null,
            FormattableString.Invariant($"credcase_{Suffix()}"),
            withCredential: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            (await users.CreateCredentialAsync(userId, StoredHash, isApproved: true, DateTime.UtcNow))
                .Should().BeTrue();

            string caseFlipped = StoredHash.ToUpperInvariant();
            caseFlipped.Should().NotBe(StoredHash, "the fixture value must actually contain lower-case letters");

            CredentialWriteOutcome refused = await users.SetPasswordHashAsync(
                userId, ReplacementHash, caseFlipped, DateTime.UtcNow);

            refused.Should().Be(
                CredentialWriteOutcome.Superseded,
                "an expectation that differs by case is a different BCrypt digest, so it names a credential "
                    + "this caller never read and the write must be refused");

            (_, string? unchanged, _, _, _, _) = await users.GetCredentialStateAsync(userId);
            unchanged.Should().Be(StoredHash, "the refused write must have changed nothing");

            // And the exact representation still succeeds, so the refusal above is evidence of a byte
            // comparison rather than of a predicate that refuses everything.
            (await users.SetPasswordHashAsync(userId, ReplacementHash, StoredHash, DateTime.UtcNow))
                .Should().Be(CredentialWriteOutcome.Replaced);
        }
        finally
        {
            await RemoveAccountAsync(userId);
        }
    }

    /// <summary>
    /// Removing a credential clears every dependant membership row first, so an account that held a
    /// membership role, a profile and a personalisation entry is removed rather than refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CredentialRemoval_ClearsEveryDependantMembershipRow()
    {
        string userName = FormattableString.Invariant($"dep_{Suffix()}");
        int userId = await CreateAccountAsync(portalId: null, userName);

        try
        {
            await SeedMembershipDependantsAsync(userName);

            (await CountMembershipDependantsAsync(userName)).Should().Be(
                3,
                "the test is worthless unless the dependant rows really exist before the deletion");

            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            MembershipWriteOutcome deleted = await users.DeleteCredentialAsync(userId);

            deleted.Should().Be(
                MembershipWriteOutcome.Recorded,
                "the credential row was present, so its removal is what the outcome reports - and a "
                + "reference violation raised by a dependant row would have surfaced here as an exception");

            (await CountMembershipUsersAsync(userName)).Should().Be(
                0,
                "the membership user row is what the dependants blocked, so its absence is the proof the "
                + "ordering worked");
            (await CountMembershipDependantsAsync(userName)).Should().Be(
                0,
                "every dependant the stock procedure clears must be gone; a row left behind would outlive "
                + "the account and be inherited by the next account to reuse the identifier");
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
        (await users.SetPasswordHashAsync(UnknownUserId, StoredHash, StoredHash, DateTime.UtcNow))
            .Should().Be(
                CredentialWriteOutcome.NoRecord,
                "an account that cannot be resolved to a membership user name has no record to refuse "
                    + "against, which is emphatically not the same answer as a refused expectation");
        (await users.SetApprovalAsync(UnknownUserId, isApproved: true)).Should().BeFalse();
        (await users.UnlockAsync(UnknownUserId)).Should().BeFalse();

        // REPORTED AS AN ABSENT RECORD, NEVER AS AN UNREACHABLE STORE, and the distinction is the one the
        // two bookkeeping members below already drew. An identifier that resolves to no membership user name
        // carries no credential for this store to remove, so the end state a removal asks for already holds;
        // reported as a store failure instead, it made a deletion cascade abandon itself - and tell its
        // caller the account had been left intact - over an account whose credential was already gone.
        (await users.DeleteCredentialAsync(UnknownUserId)).Should().Be(
            MembershipWriteOutcome.NoRecord,
            "an unresolvable identifier has no credential record to remove, and the store was reachable");

        // The two bookkeeping members report an OUTCOME rather than a boolean, and the distinction this
        // test pins is the whole reason for that: an account that cannot be resolved reports "no record",
        // which is emphatically NOT the same answer as "the store could not be reached".
        (await users.RecordSuccessfulLoginAsync(UnknownUserId, DateTime.UtcNow))
            .Should().Be(MembershipWriteOutcome.NoRecord);

        (await users.RecordFailedLoginAsync(UnknownUserId, LockoutThreshold, AttemptWindow, DateTime.UtcNow))
            .Should().Be(
                MembershipWriteOutcome.NoRecord,
                "an account that does not exist has no record to count against, and the store was reachable");

        (bool exists, string? hash, _, _, _, _) =
            await users.GetCredentialStateAsync(UnknownUserId);
        exists.Should().BeFalse();
        hash.Should().BeNull();
    }

    /// <summary>Repeated failures lock the account at the threshold, and unlocking clears the record.</summary>
    /// <returns>A task representing the test.</returns>
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

                (_, _, _, _, _, bool lockedYet) = await users.GetCredentialStateAsync(userId);
                lockedYet.Should().BeFalse();
                (await ReadFailedAttemptCountAsync(userName)).Should().Be(attempt);
            }

            (await users.RecordFailedLoginAsync(userId, LockoutThreshold, AttemptWindow, now)).Should().Be(
                MembershipWriteOutcome.RecordedAndLocked,
                "the attempt that reaches the threshold is the attempt that locks");

            (_, _, _, _, _, bool locked) = await users.GetCredentialStateAsync(userId);
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

            (_, _, _, _, _, bool unlocked) = await users.GetCredentialStateAsync(userId);
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

            (_, _, _, _, bool initiallyApproved, _) = await users.GetCredentialStateAsync(userId);
            initiallyApproved.Should().BeFalse();

            (await users.SetApprovalAsync(userId, isApproved: true)).Should().BeTrue();

            (_, _, _, _, bool nowApproved, _) = await users.GetCredentialStateAsync(userId);
            nowApproved.Should().BeTrue();

            User? projected = await users.GetAsync(null, userId);
            projected.Should().NotBeNull();
            projected!.IsApproved.Should().BeTrue("the projection reads approval from the store that was just written");

            (await users.SetApprovalAsync(userId, isApproved: false)).Should().BeTrue();

            (_, _, _, _, bool withdrawn, _) = await users.GetCredentialStateAsync(userId);
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
    /// its other memberships intact. Deleting the account instead would remove a person from every tenant
    /// of an installation because one administrator asked for them to be removed from a single site.
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
    /// The role-holder reader answers with accounts, matches the name case-insensitively, confines itself
    /// to the requested tenant and composes the external membership snapshot onto every account it returns.
    /// </summary>
    /// <returns>A task representing the test.</returns>
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

            // The snapshot is asserted on the two accounts whose credential records this suite knows exist.
            foreach (int knownHostId in new[] { untenantedHostId, _fixture.Seed.HostUserId })
            {
                User host = hosts.Single(candidate => candidate.UserId == knownHostId);
                AssertMembershipSnapshotComposed(host, nameof(IUserRepository.ListSuperUsersAsync));
            }

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
    /// A page reports the total across every page rather than its own length, and echoes back the
    /// coordinates that were actually used.
    /// </summary>
    /// <returns>A task representing the test.</returns>
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
    /// A pager rendered from this answer has to be able to say "page 40 of 2", so the coordinates asked for
    /// are echoed rather than clamped, and the total is the real one. Reporting zero here would make an
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
    /// A page size of zero is the unpaged request, and it answers with the unpaged representation rather
    /// than with an empty page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the replacement for the legacy sentinel triple. <c>UserController.GetUsers</c> obtained
    /// every row by passing <c>-1, -1, -1</c>, and the provider then rewrote <c>pageIndex</c> to zero and
    /// <c>pageSize</c> to <c>Integer.MaxValue</c>.
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
    /// Staging a write assigns no key. The generated identifier appears only once the unit of work commits,
    /// and the commit reports how many rows it wrote.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Every legacy <c>Add*</c> member returned an <c>Integer</c> because each stored procedure ended in
    /// <c>SCOPE_IDENTITY()</c>, so writing and answering with a key were the same act. Under Entity
    /// Framework they cannot be: the key is generated by the database during the commit.
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
    /// The account and its per-portal membership are staged separately and committed by a single call,
    /// which is what preserves the atomicity the one legacy statement had.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The membership is joined to the account through the navigation property rather than by copying an
    /// identifier, because at staging time there is no identifier to copy. That is the point: the change
    /// tracker orders the two inserts and threads the generated key into the dependent row, so one commit
    /// writes both.
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

    /// <summary>Deleting the account removes it and cascades into the membership rows that depended on it.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The terminal <c>dbo.Users</c> table carries no deletion flag, so there is no soft delete to
    /// reproduce - a removal is a removal. The legacy administration screens removed a person from one
    /// tenant by deleting the membership row and only deleted the account once no membership remained, and
    /// that ordering is preserved as a caller obligation rather than hidden inside the repository.
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
    /// <c>Website/release.config</c> registers the membership provider with
    /// <c>requiresUniqueEmail="false"</c>, so a shared address was legal in the original installation and
    /// remains legal here. Tightening it during a migration would lock existing people out of their own
    /// accounts, which is why it is preserved rather than corrected.
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
    /// The contrast is the assertion. Testing only that a user identifier of minus one finds nothing would
    /// read as a general rule about identifiers, and generalising it is the mistake this test exists to
    /// prevent: absence is expressed as <see langword="null"/> throughout, never as a sentinel identifier.
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
        // identity column is minus one, and a role identifier of zero is likewise a real key.
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
    /// An empty string round-trips as an empty string and a false boolean is stored as false. Neither
    /// collapses into a database null.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: RULE T7, and this is the divergence with the largest reach. <c>Null.NullString</c> was
    /// the EMPTY STRING rather than <see langword="null"/>, and <c>Null.NullBoolean</c> was <c>False</c>.
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
    /// The legacy membership <c>AddUser</c> wrapped its entire body in a bare <c>Catch ... Return -1</c>,
    /// so a duplicate username, a constraint violation and a dead connection were indistinguishable to the
    /// caller, and every one of them was reported as an ordinary expected outcome.
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
    /// Collection, name and key reads keep the SQL-null host scope and the tenant keyed -1 apart: each
    /// scope answers with its own declarations and never with the other's.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The fixture is deliberately DUAL - one declaration stored with a SQL <c>NULL</c> portal and one
    /// stored with the tenant key -1 - because a single-scope fixture cannot tell an exact match from a
    /// translation. Both directions are asserted on every read member, so neither a reinstated translation
    /// nor a widening of one scope into both can pass.
    /// </remarks>
    [Fact]
    public async Task ProfileDefinitionReads_SeparateTheSqlNullhostScopeFromTheTenantKeyedMinusOne()
    {
        _fixture.Seed.PortalId.Should().Be(
            SeededTenantPortalId,
            "the collision is exercised against the real first portal key rather than a synthetic value");

        // The host scope is a SQL NULL portal, so it is expressed as a null and never as an identifier.
        int? hostScope = null;

        string hostName = FormattableString.Invariant($"HostDefinition{Suffix()}");
        string tenantName = FormattableString.Invariant($"TenantDefinition{Suffix()}");
        int hostDefinitionId = 0;
        int tenantDefinitionId = 0;

        try
        {
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition hostLevel = DefinitionForScope(hostScope, hostName, viewOrder: 1);
                ProfilePropertyDefinition tenantOwned =
                    DefinitionForScope(SeededTenantPortalId, tenantName, viewOrder: 2);

                await profiles.AddDefinitionAsync(hostLevel);
                await profiles.AddDefinitionAsync(tenantOwned);
                await unitOfWork.SaveChangesAsync();

                hostDefinitionId = hostLevel.PropertyDefinitionId;
                tenantDefinitionId = tenantOwned.PropertyDefinitionId;
            }

            // The two rows really are stored under different scopes, so the assertions below cannot pass
            // by both rows sharing one encoding.
            (await _fixture.Database.ScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM [dbo].[ProfilePropertyDefinition]
                WHERE [PropertyDefinitionID] = @definitionId AND [PortalID] IS NULL;
                """,
                new Dictionary<string, object?> { ["definitionId"] = hostDefinitionId }))
                .Should().Be(1, "the host declaration is stored with a SQL NULL portal");

            (await _fixture.Database.ScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM [dbo].[ProfilePropertyDefinition]
                WHERE [PropertyDefinitionID] = @definitionId AND [PortalID] = -1;
                """,
                new Dictionary<string, object?> { ["definitionId"] = tenantDefinitionId }))
                .Should().Be(1, "the tenant declaration is stored with the literal key -1");

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                IReadOnlyList<ProfilePropertyDefinition> tenantCatalogue =
                    await profiles.GetDefinitionsByPortalIdAsync(SeededTenantPortalId);

                tenantCatalogue.Select(definition => definition.PropertyDefinitionId)
                    .Should().Contain(
                        tenantDefinitionId,
                        "a tenant numbered -1 must be able to read its own declarations")
                    .And.NotContain(
                        hostDefinitionId,
                        "a tenant-scoped read must not be answered with the host scope's rows");

                IReadOnlyList<ProfilePropertyDefinition> hostCatalogue =
                    await profiles.GetDefinitionsByPortalIdAsync(hostScope);

                hostCatalogue.Select(definition => definition.PropertyDefinitionId)
                    .Should().Contain(
                        hostDefinitionId,
                        "the host scope is addressed by a null and remains reachable")
                    .And.NotContain(
                        tenantDefinitionId,
                        "the host scope must not widen to a row that physically stores a tenant key");

                (await profiles.GetDefinitionByNameAsync(SeededTenantPortalId, tenantName))
                    .Should().NotBeNull("the name read uses the same exact scope as the catalogue");
                (await profiles.GetDefinitionByNameAsync(SeededTenantPortalId, hostName))
                    .Should().BeNull("a tenant scope does not reach a SQL-null declaration");
                (await profiles.GetDefinitionByNameAsync(hostScope, hostName))
                    .Should().NotBeNull("the host scope reaches its own declaration by name");
                (await profiles.GetDefinitionByNameAsync(hostScope, tenantName))
                    .Should().BeNull("the host scope does not reach a tenant declaration by name");

                (await profiles.GetDefinitionByIdAsync(SeededTenantPortalId, tenantDefinitionId))
                    .Should().NotBeNull("the single read must agree with the tenant catalogue");
                (await profiles.GetDefinitionByIdAsync(SeededTenantPortalId, hostDefinitionId))
                    .Should().BeNull("the single read must not reintroduce the old sentinel translation");
                (await profiles.GetDefinitionByIdAsync(hostScope, hostDefinitionId))
                    .Should().NotBeNull("the single read must agree with the host catalogue");
                (await profiles.GetDefinitionByIdAsync(hostScope, tenantDefinitionId))
                    .Should().BeNull("the single read must not reintroduce the old unscoped fallback");
                (await profiles.GetDefinitionByIdAsync(UnknownPortalId, hostDefinitionId))
                    .Should().BeNull("an ordinary foreign tenant cannot address a host declaration");
                (await profiles.GetDefinitionByIdAsync(UnknownPortalId, tenantDefinitionId))
                    .Should().BeNull("an ordinary foreign tenant cannot address another tenant's declaration");
            }
        }
        finally
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserProfileRepository profiles =
                scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            if (hostDefinitionId > 0)
            {
                await profiles.DeleteDefinitionAsync(hostDefinitionId);
            }

            if (tenantDefinitionId > 0)
            {
                await profiles.DeleteDefinitionAsync(tenantDefinitionId);
            }

            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>
    /// The scoped answer read and the scoped answer purge address exactly the scope they are given: a
    /// tenant keyed -1 sees and removes its own answers, and the SQL-null host scope keeps its own.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The answer members scope themselves through the same one statement of what a scope's declarations
    /// are, so the same sentinel defect reached them. Read through the defect, a tenant numbered -1 was
    /// shown the host scope's answers and none of its own; purged through it, the same tenant's membership
    /// removal deleted the host scope's answers and left its own behind.
    /// </remarks>
    [Fact]
    public async Task ScopedProfileValueReadAndPurge_AddressExactlyTheScopeTheyAreGiven()
    {
        // The host scope is a SQL NULL portal, so it is expressed as a null and never as an identifier.
        int? hostScope = null;

        string hostName = FormattableString.Invariant($"HostAnswer{Suffix()}");
        string tenantName = FormattableString.Invariant($"TenantAnswer{Suffix()}");
        int hostDefinitionId = 0;
        int tenantDefinitionId = 0;
        int userId = 0;

        try
        {
            userId = await CreateAccountAsync(
                SeededTenantPortalId,
                FormattableString.Invariant($"scoped_{Suffix()}"));

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition hostLevel = DefinitionForScope(hostScope, hostName, viewOrder: 1);
                ProfilePropertyDefinition tenantOwned =
                    DefinitionForScope(SeededTenantPortalId, tenantName, viewOrder: 2);

                await profiles.AddDefinitionAsync(hostLevel);
                await profiles.AddDefinitionAsync(tenantOwned);
                await unitOfWork.SaveChangesAsync();

                hostDefinitionId = hostLevel.PropertyDefinitionId;
                tenantDefinitionId = tenantOwned.PropertyDefinitionId;

                // LastUpdatedDate is a NOT NULL datetime column, and DateTime.MinValue is outside the
                // SQL Server datetime range, so an explicit instant is supplied rather than left defaulted.
                DateTime answeredAt = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                await profiles.AddProfileValueAsync(new UserProfileValue
                {
                    UserId = userId,
                    PropertyDefinitionId = hostDefinitionId,
                    PropertyValue = "host answer",
                    Visibility = 0,
                    LastUpdatedDate = answeredAt,
                });
                await profiles.AddProfileValueAsync(new UserProfileValue
                {
                    UserId = userId,
                    PropertyDefinitionId = tenantDefinitionId,
                    PropertyValue = "tenant answer",
                    Visibility = 0,
                    LastUpdatedDate = answeredAt,
                });
                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                (await profiles.GetProfileValuesAsync(SeededTenantPortalId, userId))
                    .Select(value => value.PropertyDefinitionId)
                    .Should().Contain(
                        tenantDefinitionId,
                        "a tenant numbered -1 must be able to read its own answers")
                    .And.NotContain(
                        hostDefinitionId,
                        "a tenant-scoped answer read must not be served the host scope's answers");

                (await profiles.GetProfileValuesAsync(hostScope, userId))
                    .Select(value => value.PropertyDefinitionId)
                    .Should().Contain(hostDefinitionId, "the host scope reaches its own answers")
                    .And.NotContain(
                        tenantDefinitionId,
                        "the host scope must not widen to a tenant's answers");
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await profiles.DeleteProfileValuesAsync(SeededTenantPortalId, userId);
                await unitOfWork.SaveChangesAsync();
            }

            (await _fixture.Database.ScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM [dbo].[UserProfile]
                WHERE [UserID] = @userId AND [PropertyDefinitionID] = @definitionId;
                """,
                new Dictionary<string, object?>
                {
                    ["userId"] = userId,
                    ["definitionId"] = tenantDefinitionId,
                }))
                .Should().Be(0, "the tenant-scoped purge removes the tenant's own answer");

            (await _fixture.Database.ScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM [dbo].[UserProfile]
                WHERE [UserID] = @userId AND [PropertyDefinitionID] = @definitionId;
                """,
                new Dictionary<string, object?>
                {
                    ["userId"] = userId,
                    ["definitionId"] = hostDefinitionId,
                }))
                .Should().Be(1, "the tenant-scoped purge leaves the host scope's answer untouched");
        }
        finally
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IUserProfileRepository profiles =
                scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            if (userId > 0)
            {
                await profiles.DeleteProfileValuesAsync(hostScope, userId);
                await unitOfWork.SaveChangesAsync();
            }

            if (hostDefinitionId > 0)
            {
                await profiles.DeleteDefinitionAsync(hostDefinitionId);
            }

            if (tenantDefinitionId > 0)
            {
                await profiles.DeleteDefinitionAsync(tenantDefinitionId);
            }

            await unitOfWork.SaveChangesAsync();

            if (userId > 0)
            {
                await RemoveAccountAsync(userId);
            }
        }
    }

    /// <summary>
    /// The BATCHED answer read - the one an account listing issues once for its whole page - answers every
    /// account it names and no account it does not, scopes itself exactly as the per-account read does, and
    /// returns the rows in the order a caller can group by walking them once.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS ALONGSIDE THE PER-ACCOUNT CASES ABOVE. The listing path is the only caller of this
    /// overload, and the service that calls it is covered against a substitute repository, so before this
    /// case the batched statement had never been EXECUTED - not once, in either assembly. Three constructs
    /// in it fail only at runtime and only against the real schema: the <c>Include</c> of the owning
    /// declaration, the scoped-declaration subquery, and the three-level ordering. A defect in any of them
    /// would have shipped green.
    /// </para>
    /// <para>
    /// EVERY SEEDED ROW IS DELIBERATELY OUT OF ORDER. The answers are inserted highest-account-first and
    /// highest-declaration-first, so the identity keys the store assigns run OPPOSITE to the order the read
    /// must return. An ordering clause that was dropped, or that ordered by the primary key alone, would
    /// return them as inserted and this case would report it.
    /// </para>
    /// <para>
    /// The tenant is created by this case rather than borrowed. A bare tenant carries no profile
    /// declarations of its own, so the scope under test contains exactly the three declarations named below
    /// and nothing a sibling case left behind.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BatchedProfileValueRead_AnswersEveryNamedAccountInScopeInStoredOrder()
    {
        // The host scope is a SQL NULL portal, so it is expressed as a null and never as an identifier.
        int? hostScope = null;

        string suffix = Suffix();
        int tenantId = await CreatePortalAsync();

        int firstUserId = 0;
        int secondUserId = 0;
        int unnamedUserId = 0;
        int firstDefinitionId = 0;
        int secondDefinitionId = 0;
        int hostDefinitionId = 0;

        try
        {
            firstUserId = await CreateAccountAsync(
                tenantId,
                FormattableString.Invariant($"batched_a_{suffix}"));
            secondUserId = await CreateAccountAsync(
                tenantId,
                FormattableString.Invariant($"batched_b_{suffix}"));
            unnamedUserId = await CreateAccountAsync(
                tenantId,
                FormattableString.Invariant($"batched_c_{suffix}"));

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition first = DefinitionForScope(
                    tenantId,
                    FormattableString.Invariant($"BatchedFirst{suffix}"),
                    viewOrder: 1);
                ProfilePropertyDefinition second = DefinitionForScope(
                    tenantId,
                    FormattableString.Invariant($"BatchedSecond{suffix}"),
                    viewOrder: 2);
                ProfilePropertyDefinition hostLevel = DefinitionForScope(
                    hostScope,
                    FormattableString.Invariant($"BatchedHost{suffix}"),
                    viewOrder: 3);

                await profiles.AddDefinitionAsync(first);
                await profiles.AddDefinitionAsync(second);
                await profiles.AddDefinitionAsync(hostLevel);
                await unitOfWork.SaveChangesAsync();

                firstDefinitionId = first.PropertyDefinitionId;
                secondDefinitionId = second.PropertyDefinitionId;
                hostDefinitionId = hostLevel.PropertyDefinitionId;
            }

            firstDefinitionId.Should().BeLessThan(
                secondDefinitionId,
                "the declaration key is an identity, so the ordering assertions below can rely on the "
                + "first declaration sorting before the second");

            // LastUpdatedDate is a NOT NULL datetime column and DateTime.MinValue is outside the SQL Server
            // datetime range, so an explicit instant is supplied rather than left defaulted.
            DateTime answeredAt = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // INSERTED IN REVERSE OF THE ORDER THE READ MUST RETURN, so ProfileID - the identity the
                // rows receive here - descends as the required order ascends.
                (int UserId, int DefinitionId, string Value)[] seeded =
                [
                    (secondUserId, secondDefinitionId, "b-second"),
                    (secondUserId, firstDefinitionId, "b-first"),
                    (firstUserId, secondDefinitionId, "a-second"),

                    // TWO ROWS FOR ONE ACCOUNT AND ONE DECLARATION. dbo.UserProfile carries no unique
                    // constraint over (UserID, PropertyDefinitionID) - only IX_UserProfile on UserID - so
                    // legacy data can hold a duplicate pair, and it is the only shape in which the third
                    // ordering key, ProfileID, decides anything at all. These two are named for the keys
                    // they receive rather than for their position here, because the identity is assigned in
                    // insertion order and the read returns them by it.
                    (firstUserId, firstDefinitionId, "a-first-lower-key"),
                    (firstUserId, firstDefinitionId, "a-first-higher-key"),

                    // The account nobody names, to prove the read is bounded by the collection it is given.
                    (unnamedUserId, firstDefinitionId, "c-unnamed"),

                    // The host-level declaration's answer, to prove the scope is matched exactly.
                    (firstUserId, hostDefinitionId, "a-host"),
                ];

                foreach ((int userId, int definitionId, string value) in seeded)
                {
                    await profiles.AddProfileValueAsync(new UserProfileValue
                    {
                        UserId = userId,
                        PropertyDefinitionId = definitionId,
                        PropertyValue = value,
                        Visibility = 0,
                        LastUpdatedDate = answeredAt,
                    });

                    // Saved one at a time so the identities are assigned in the seeded order rather than in
                    // whatever order a batched insert chooses.
                    await unitOfWork.SaveChangesAsync();
                }
            }

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                IReadOnlyList<UserProfileValue> answered = await profiles.GetProfileValuesAsync(
                    tenantId,
                    new[] { firstUserId, secondUserId });

                answered.Select(value => value.PropertyValue).Should().BeEquivalentTo(
                    new[] { "a-first-lower-key", "a-first-higher-key", "a-second", "b-first", "b-second" },
                    "the read answers both named accounts in one statement, excludes the account it was "
                    + "not given, and excludes the host-level declaration's answer because the scope is "
                    + "matched exactly rather than widened");

                answered.Select(value => value.PropertyValue).Should().NotContain(
                    "c-unnamed",
                    "an account absent from the collection contributes no row, however many answers it holds");
                answered.Select(value => value.PropertyValue).Should().NotContain(
                    "a-host",
                    "a host-level declaration is not part of a tenant's scope, so its answer stays out of "
                    + "the tenant's batched read exactly as it stays out of the per-account read");

                answered.Should().BeInAscendingOrder(
                    value => value.UserId,
                    "the listing groups the flat result by account, which it can only do in one pass while "
                    + "each account's rows are contiguous");

                answered.Select(value => (value.UserId, value.PropertyDefinitionId, value.PropertyValue))
                    .Should().Equal(
                        [
                            (firstUserId, firstDefinitionId, "a-first-lower-key"),
                            (firstUserId, firstDefinitionId, "a-first-higher-key"),
                            (firstUserId, secondDefinitionId, "a-second"),
                            (secondUserId, firstDefinitionId, "b-first"),
                            (secondUserId, secondDefinitionId, "b-second"),
                        ],
                        "the order is account, then declaration, then the row's own key, and every row was "
                        + "inserted in the opposite order of the first two - so a read that returned them as "
                        + "stored, or ordered them by the primary key alone, would fail here");

                answered.Select(value => value.ProfileId).Should().NotBeInAscendingOrder(
                    "the row's own key is the LAST tiebreak rather than the sort, which is exactly what a "
                    + "read ordered by the primary key alone would get right by accident");

                answered
                    .Where(value => value.UserId == firstUserId
                        && value.PropertyDefinitionId == firstDefinitionId)
                    .Select(value => value.ProfileId)
                    .Should().BeInAscendingOrder(
                        "two answers to one declaration are separated only by their own key, so it decides "
                        + "their order and does so oldest first");

                answered.Should().OnlyContain(
                    value => value.PropertyDefinition != null,
                    "the listing reads the declaration's name and length off each answer, so the owning "
                    + "declaration travels with the row rather than being fetched per row afterwards");

                answered
                    .Where(value => value.PropertyDefinitionId == firstDefinitionId)
                    .Should().OnlyContain(
                        value => value.PropertyDefinition!.PropertyName
                            == FormattableString.Invariant($"BatchedFirst{suffix}"),
                        "the included declaration is the one the answer actually points at");

                IReadOnlyList<UserProfileValue> repeated = await profiles.GetProfileValuesAsync(
                    tenantId,
                    new[] { firstUserId, firstUserId, secondUserId, secondUserId });

                repeated.Select(value => value.ProfileId).Should().Equal(
                    answered.Select(value => value.ProfileId),
                    "a repeated account identifier is de-duplicated before the statement is composed, so "
                    + "naming an account twice cannot return its answers twice");

                IReadOnlyList<UserProfileValue> hostAnswers = await profiles.GetProfileValuesAsync(
                    hostScope,
                    new[] { firstUserId, secondUserId });

                hostAnswers.Select(value => value.PropertyValue).Should().Equal(
                    ["a-host"],
                    "the host scope reaches its own declaration's answers and does not widen to the "
                    + "tenant's, which is the same exact match asserted from the other side above");

                (await profiles.GetProfileValuesAsync(tenantId, Array.Empty<int>()))
                    .Should().BeEmpty(
                        "a page whose window landed past the end of the collection names no account, and "
                        + "that is answered without a statement rather than refused");

                await Assert.ThrowsAsync<ArgumentNullException>(
                    () => profiles.GetProfileValuesAsync(tenantId, null!));
            }
        }
        finally
        {
            // THE ANSWERS ARE REMOVED BY STATEMENT RATHER THAN THROUGH THE PURGE MEMBER, and the reason is
            // the duplicate pair this case seeds deliberately. DeleteProfileValuesAsync reads the answers it
            // is about to remove through a query the scoped-declaration subquery has already marked
            // AsNoTracking, so no identity resolution is applied and two answers sharing one declaration
            // materialise two instances of it; RemoveRange then attaches both and the change tracker
            // refuses the second. That is a property of the duplicate shape, not of this case's subject, and
            // reproducing it in a teardown would report it as a failure of the read under test. It is
            // recorded as an out-of-scope observation instead, and the rows go out by identifier - only the
            // three accounts created above, so nothing else can be reached.
            await _fixture.Database.ExecuteAsync(
                """
                DELETE FROM [dbo].[UserProfile]
                WHERE [UserID] IN (@firstUserId, @secondUserId, @unnamedUserId);
                """,
                new Dictionary<string, object?>
                {
                    ["firstUserId"] = firstUserId,
                    ["secondUserId"] = secondUserId,
                    ["unnamedUserId"] = unnamedUserId,
                });

            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                foreach (int definitionId in new[] { firstDefinitionId, secondDefinitionId, hostDefinitionId })
                {
                    if (definitionId > 0)
                    {
                        await profiles.DeleteDefinitionAsync(definitionId);
                    }
                }

                await unitOfWork.SaveChangesAsync();
            }

            foreach (int userId in new[] { firstUserId, secondUserId, unnamedUserId })
            {
                if (userId > 0)
                {
                    await RemoveAccountAsync(userId);
                }
            }

            await RemovePortalAsync(tenantId);
        }
    }

    /// <summary>
    /// The first tenant, a second tenant and the host level are three distinct scopes on a profile
    /// declaration, on the way in and on the way out.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The three cases are asserted together because the defect they guard against was a COLLAPSE of two of
    /// them into one, and a test that exercised any single value could not see it. <c>-1</c> is a real
    /// tenant - the first one of every installation, because <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1,
    /// 1)</c> - while <c>NULL</c> is the host-level scope that <c>dbo.ProfilePropertyDefinition</c> shares
    /// with every portal.
    /// </remarks>
    [Fact]
    public async Task ProfileDefinitionScopes_KeepEachTenantAndTheHostLevelDistinct()
    {
        string nameSuffix = Suffix();
        var written = new List<(int? Scope, int DefinitionId)>();
        int secondTenantId = await CreatePortalAsync();

        try
        {
            foreach (int? scope in new int?[] { SeededTenantPortalId, secondTenantId, null })
            {
                using IServiceScope scope0 = _fixture.Services.CreateScope();
                IUserProfileRepository profiles =
                    scope0.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope0.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition definition = DefinitionForScope(
                    scope,
                    FormattableString.Invariant($"Scope{(scope is int named ? named : 9)}{nameSuffix}"),
                    viewOrder: 1);

                await profiles.AddDefinitionAsync(definition);
                await unitOfWork.SaveChangesAsync();

                written.Add((scope, definition.PropertyDefinitionId));
            }

            // What reached the store, read straight from the column.
            foreach ((int? scope, int definitionId) in written)
            {
                int nullCount = await _fixture.Database.ScalarAsync<int>(
                    """
                    SELECT COUNT(*) FROM [dbo].[ProfilePropertyDefinition]
                    WHERE [PropertyDefinitionID] = @definitionId AND [PortalID] IS NULL;
                    """,
                    new Dictionary<string, object?> { ["definitionId"] = definitionId });

                if (scope is null)
                {
                    nullCount.Should().Be(1, "the host-level scope is a genuine SQL null");
                    continue;
                }

                nullCount.Should().Be(
                    0,
                    FormattableString.Invariant($"tenant {scope} is a tenant, so its declaration is not host-level"));

                (await _fixture.Database.ScalarAsync<int>(
                    """
                    SELECT [PortalID] FROM [dbo].[ProfilePropertyDefinition]
                    WHERE [PropertyDefinitionID] = @definitionId;
                    """,
                    new Dictionary<string, object?> { ["definitionId"] = definitionId }))
                    .Should().Be(scope!.Value, "the tenant identifier is stored verbatim");
            }

            // What each scope can read: its own row, and neither of the other two.
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    reading.ServiceProvider.GetRequiredService<IUserProfileRepository>();

                foreach (int tenant in new[] { SeededTenantPortalId, secondTenantId })
                {
                    IReadOnlyList<int> visible =
                        (await profiles.GetDefinitionsByPortalIdAsync(tenant))
                        .Select(definition => definition.PropertyDefinitionId)
                        .ToList();

                    visible.Should().Contain(
                        written.Single(entry => entry.Scope == tenant).DefinitionId);

                    foreach ((int? other, int otherId) in written.Where(entry => entry.Scope != tenant))
                    {
                        visible.Should().NotContain(
                            otherId,
                            FormattableString.Invariant(
                                $"tenant {tenant} must not see the {(other is null ? "host-level" : "other tenant's")} declaration"));
                    }
                }
            }
        }
        finally
        {
            using (IServiceScope cleanup = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles =
                    cleanup.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = cleanup.ServiceProvider.GetRequiredService<IUnitOfWork>();

                foreach ((int? _, int definitionId) in written)
                {
                    await profiles.DeleteDefinitionAsync(definitionId);
                }

                await unitOfWork.SaveChangesAsync();
            }

            await RemovePortalAsync(secondTenantId);
        }
    }

    /// <summary>
    /// Profile declarations and profile values persist through their own repository, and the values they
    /// hold drive the profile-property filter of the account listing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The value filters are matched as PREFIXES rather than as substrings, because each legacy search
    /// branch appended one trailing percent sign to the search text before handing it to the provider. The
    /// declaration's own visibility is a plain integer rather than an enumeration, deliberately - the
    /// legacy visibility mode is not among the domain enumerations.
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

                ProfilePropertyDefinition? byId =
                    await profiles.GetDefinitionByIdAsync(portalId, definitionId);
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
                (await profiles.GetDefinitionByIdAsync(portalId, UnknownUserId)).Should().BeNull(
                    "an unknown key answers with null rather than with a sentinel-bearing instance");
                (await profiles.GetDefinitionByIdAsync(UnknownPortalId, definitionId)).Should().BeNull(
                    "the key read is tenant-scoped just like the name and collection reads");

                IReadOnlyList<ProfilePropertyDefinition> forPortal = await profiles.GetDefinitionsByPortalIdAsync(portalId);
                forPortal.Select(candidate => candidate.PropertyDefinitionId).Should().Contain(definitionId);
            }

            // the declaration is amended in place
            // The legacy UpdatePropertyDefinition took ten positional arguments - data type, default value,
            // category, name, required, validation expression, view order, visible and length among them.
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IUserProfileRepository profiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                ProfilePropertyDefinition? amending =
                    await profiles.GetDefinitionByIdAsync(portalId, definitionId);
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

                ProfilePropertyDefinition? amended =
                    await profiles.GetDefinitionByIdAsync(portalId, definitionId);
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

    /// <summary>Builds one live profile declaration in the requested persisted scope.</summary>
    /// <param name="portalId">The stored portal key, or <see langword="null"/> for host scope.</param>
    /// <param name="propertyName">The unique property name.</param>
    /// <param name="viewOrder">The display order.</param>
    /// <returns>An unsaved declaration.</returns>
    private static ProfilePropertyDefinition DefinitionForScope(
        int? portalId,
        string propertyName,
        int viewOrder) =>
        new()
        {
            PortalId = portalId,
            PropertyName = propertyName,
            PropertyCategory = "Scope",
            DataType = 0,
            Length = 50,
            IsRequired = false,
            IsVisible = true,
            IsDeleted = false,
            ViewOrder = viewOrder,
        };

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
    /// The credential is deleted first and explicitly. The external membership tables carry no foreign key
    /// to the mapped account table, so removing the account cannot cascade into them and a credential left
    /// behind would keep the username reserved in the store.
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

    /// <summary>
    /// Seeds one row in each membership table that references the membership user row, so a deletion has
    /// something to be blocked by.
    /// </summary>
    /// <param name="userName">The account whose membership user row the rows hang off.</param>
    /// <returns>A task that completes when all three rows exist.</returns>
    /// <remarks>
    /// The membership user identifier is resolved through the same application-name join the store
    /// performs, rather than being captured when the credential was created, so the seed cannot silently
    /// attach itself to the wrong row.
    /// </remarks>
    private Task SeedMembershipDependantsAsync(string userName) =>
        _fixture.Database.ExecuteAsync(
            """
            DECLARE @applicationId uniqueidentifier;
            DECLARE @membershipUserId uniqueidentifier;
            DECLARE @roleId uniqueidentifier = NEWID();
            DECLARE @pathId uniqueidentifier = NEWID();

            SELECT @applicationId = aa.[ApplicationId], @membershipUserId = au.[UserId]
            FROM [dbo].[aspnet_Users] au
            INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
            WHERE aa.[LoweredApplicationName] = @loweredApplication AND au.[LoweredUserName] = @loweredUserName;

            INSERT INTO [dbo].[aspnet_Roles]
                ([ApplicationId], [RoleId], [RoleName], [LoweredRoleName], [Description])
            VALUES (@applicationId, @roleId, @roleName, LOWER(@roleName), NULL);

            INSERT INTO [dbo].[aspnet_UsersInRoles] ([UserId], [RoleId])
            VALUES (@membershipUserId, @roleId);

            INSERT INTO [dbo].[aspnet_Profile]
                ([UserId], [PropertyNames], [PropertyValuesString], [PropertyValuesBinary], [LastUpdatedDate])
            VALUES (@membershipUserId, N'Suite:S:0:0:', N'', 0x00, SYSUTCDATETIME());

            INSERT INTO [dbo].[aspnet_Paths] ([ApplicationId], [PathId], [Path], [LoweredPath])
            VALUES (@applicationId, @pathId, @path, LOWER(@path));

            INSERT INTO [dbo].[aspnet_PersonalizationPerUser]
                ([Id], [PathId], [UserId], [PageSettings], [LastUpdatedDate])
            VALUES (NEWID(), @pathId, @membershipUserId, 0x00, SYSUTCDATETIME());
            """,
            new Dictionary<string, object?>
            {
                ["@loweredApplication"] = MembershipApplication.ToLowerInvariant(),
                ["@loweredUserName"] = userName.ToLowerInvariant(),
                ["@roleName"] = FormattableString.Invariant($"suite_{userName}"),
                ["@path"] = FormattableString.Invariant($"~/suite/{userName}.aspx"),
            });

    /// <summary>Counts the rows that reference an account's membership user row.</summary>
    /// <param name="userName">The account to count against.</param>
    /// <returns>The number of dependant rows across the three tables.</returns>
    private Task<int> CountMembershipDependantsAsync(string userName) =>
        _fixture.Database.ScalarAsync<int>(
            """
            DECLARE @membershipUserId uniqueidentifier;

            SELECT @membershipUserId = au.[UserId]
            FROM [dbo].[aspnet_Users] au
            INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
            WHERE aa.[LoweredApplicationName] = @loweredApplication AND au.[LoweredUserName] = @loweredUserName;

            SELECT
                (SELECT COUNT(*) FROM [dbo].[aspnet_UsersInRoles] WHERE [UserId] = @membershipUserId)
                + (SELECT COUNT(*) FROM [dbo].[aspnet_Profile] WHERE [UserId] = @membershipUserId)
                + (SELECT COUNT(*) FROM [dbo].[aspnet_PersonalizationPerUser] WHERE [UserId] = @membershipUserId);
            """,
            new Dictionary<string, object?>
            {
                ["@loweredApplication"] = MembershipApplication.ToLowerInvariant(),
                ["@loweredUserName"] = userName.ToLowerInvariant(),
            });

    /// <summary>Counts the membership user rows an account name resolves to.</summary>
    /// <param name="userName">The account to count.</param>
    /// <returns>One while the account holds a membership user row, zero once it does not.</returns>
    private Task<int> CountMembershipUsersAsync(string userName) =>
        _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[aspnet_Users] au
            INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
            WHERE aa.[LoweredApplicationName] = @loweredApplication AND au.[LoweredUserName] = @loweredUserName;
            """,
            new Dictionary<string, object?>
            {
                ["@loweredApplication"] = MembershipApplication.ToLowerInvariant(),
                ["@loweredUserName"] = userName.ToLowerInvariant(),
            });

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
    /// Reads one page of a tenant's membership with every optional filter left open, so that only the
    /// paging coordinates vary between calls.
    /// </summary>
    /// <param name="users">The repository.</param>
    /// <param name="portalId">The tenant to page through.</param>
    /// <param name="pageIndex">The zero-based page index, as declared by the envelope.</param>
    /// <param name="pageSize">The page size; zero requests the unpaged representation.</param>
    /// <returns>The page the store answered with.</returns>
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
    /// Asserts that an account carries the membership snapshot the repository composes rather than the
    /// defaults an unpopulated instance would show.
    /// </summary>
    /// <param name="account">The account a read path answered with.</param>
    /// <param name="readPath">The member that produced the account, named for the failure message.</param>
    /// <remarks>
    /// SEVEN of the eleven ignored properties are composed, not all eleven, and the split is deliberate on
    /// both sides. Composed: approval, lockout, the creation instant and the four recorded timestamps.
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
    /// The role is staged and committed through its own repository, which keeps this suite's set-up on the
    /// same abstractions it is testing. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so the returned
    /// identifier may legitimately be zero - it must never be tested for truthiness.
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
