using System.Net;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that the store reads an authorisation decision makes are governed by the request's own abort token,
/// so that abandoning a request abandons the reads taken to authorise it.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS GUARDS. The framework's authorisation context carries no cancellation token, so the three
/// handlers in this API had none to pass and passed <see cref="CancellationToken.None"/> at all four of their
/// evaluation call sites. Every store read taken to decide a policy was therefore uncancellable, in a solution
/// whose standing rule is that every I/O-bound path is cancellable (AAP rule T6): a caller that disconnected
/// mid-flight still paid for a completed authorisation decision that nothing would ever read, and a slow or
/// wedged store held the connection for the full duration rather than releasing it with the request.
/// </para>
/// <para>
/// WHY THE ASSERTION IS <c>CanBeCanceled</c> AND NOT A TIMING MEASUREMENT. The distinction between the defect
/// and the fix is exactly the distinction between <see cref="CancellationToken.None"/>, whose
/// <see cref="CancellationToken.CanBeCanceled"/> is <see langword="false"/> by definition, and a request's
/// abort token, whose value is <see langword="true"/>. That makes the fix a total, deterministic property of
/// every recorded call rather than something inferred from how long a cancelled request took - which would be a
/// race dressed up as a fact.
/// </para>
/// <para>
/// EVERY recorded call is asserted rather than the authorisation one alone, and that is deliberate: it needs no
/// rule for telling the authorisation read apart from any other read of the same repository in the same
/// request, and it states the property the rule actually wants - that nothing on this path opts out of
/// cancellation. Nothing in the delivered <c>src</c> tree passes an uncancellable token to a repository, so the
/// stricter form costs nothing and would catch a new call site that did.
/// </para>
/// <para>
/// The host is this suite's own because the repository has to be replaced to observe what it was handed, and
/// the shared fixture's container is shared with every other suite in the run. Replacing a registration on it
/// would leak into them. Declaring a private host for a suite that needs its own composition is the same
/// arrangement <see cref="TenantResolutionTests"/> and <see cref="HealthCheckTests"/> use.
/// </para>
/// </remarks>
/// <param name="fixture">The shared hosted API, which mints the credential this suite presents.</param>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class AuthorizationCancellationTests(ApiTestFixture fixture)
{
    /// <summary>An address guarded by the portal-administrator policy.</summary>
    private static readonly Uri RolesRoute = new("/api/v1/roles", UriKind.Relative);

    /// <summary>An address guarded by the host-administrator policy.</summary>
    private static readonly Uri PortalsRoute = new("/api/v1/portals", UriKind.Relative);

    private readonly ApiTestFixture _fixture = fixture;

    /// <summary>
    /// Every store read taken while authorising a request is handed a token that can be cancelled.
    /// </summary>
    /// <param name="route">The guarded address to request.</param>
    /// <param name="policy">The policy that address is guarded by, for the failure message.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Both guarded families are exercised because they reach the evaluator by different members - the
    /// portal-administrator policy through the administration question and the host policy through the
    /// host-account question - and the defect was present at the call site of each.
    /// </remarks>
    [Theory]
    [InlineData("/api/v1/roles", "PortalAdministrator")]
    [InlineData("/api/v1/portals", "HostAdministrator")]
    public async Task AuthorisingARequest_ReadsUnderTheRequestsOwnAbortToken(string route, string policy)
    {
        List<CancellationToken> observed = [];

        string token = await AuthenticatedClientFactory
            .GetAccessTokenAsync(_fixture, IntegrationSeed.HostUserName, ApiTestFixture.KnownPassword)
            .ConfigureAwait(true);

        using RecordingRepositoryHost host = new(observed);

        using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = _fixture.ClientOptions.BaseAddress,
        });

        AuthenticatedClientFactory.Authenticate(client, token);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri(route, UriKind.Relative))
            .ConfigureAwait(true);

        // The status is asserted only to the extent that authorisation must have RUN AND GRANTED. A refusal
        // would mean the recorded read was never the authorisation read, which would make the assertion below
        // vacuous rather than false.
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "the seeded super user's own token must authenticate");
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            $"the seeded super user satisfies {policy}, so the decision must have been taken from the store");

        observed.Should().NotBeEmpty(
            "authorising this request asks the account store whether the caller is a host account, so at "
            + "least one read must have been recorded");

        observed.Should().OnlyContain(
            recorded => recorded.CanBeCanceled,
            "an uncancellable token is CancellationToken.None, which is what the handlers used to pass - a "
            + "disconnected caller then still paid for an authorisation decision nothing would read");
    }

    /// <summary>
    /// A host whose account repository records the cancellation token each read is handed.
    /// </summary>
    /// <param name="observed">The list every recorded token is appended to.</param>
    /// <remarks>
    /// The repository is REPLACED rather than decorated. Its concrete implementation is internal to the
    /// persistence assembly, so a decorator could not name it, and the one member an authorisation decision
    /// reaches - the host-account read - is answered here with a super user so that the decision is settled at
    /// its first hop. That keeps this suite about the token and not about how many reads a policy happens to
    /// take: the routes chosen make no other account read, so the loose mock answers nothing else.
    /// </remarks>
    private sealed class RecordingRepositoryHost(List<CancellationToken> observed)
        : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                Mock<IUserRepository> accounts = new();

                accounts
                    .Setup(repository => repository.GetAsync(
                        It.IsAny<int?>(),
                        It.IsAny<int>(),
                        It.IsAny<CancellationToken>()))
                    .Returns((int? _, int userId, CancellationToken cancellationToken) =>
                    {
                        lock (observed)
                        {
                            observed.Add(cancellationToken);
                        }

                        return Task.FromResult<User?>(new User
                        {
                            UserId = userId,
                            Username = IntegrationSeed.HostUserName,
                            IsSuperUser = true,
                        });
                    });

                services.RemoveAll<IUserRepository>();
                services.AddScoped(_ => accounts.Object);
            });
        }
    }
}
