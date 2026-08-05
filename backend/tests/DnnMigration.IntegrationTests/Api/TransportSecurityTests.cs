using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the transport half of the request pipeline: that cleartext is refused by default outside
/// development, that the health probe is exempt from that refusal, and that the forwarded scheme is
/// honoured from a trusted hop and from no other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> The shipped configuration used to disable HTTPS redirection outright -
/// in the base settings file AND in the production overlay - and the pipeline never called
/// <c>UseForwardedHeaders</c>, so the trusted-proxy configuration that was registered could not take
/// effect. Together those two omissions meant a production deployment served sign-in credentials and
/// bearer tokens over cleartext, and could not have distinguished a proxied HTTPS request from a
/// cleartext one even if it had wanted to. Both are now closed, and each fact below pins one half so
/// that reopening either fails a test rather than a security review.
/// </para>
/// <para>
/// <b>Why the hosts here are ad-hoc.</b> The shared fixture pins <c>Https__RedirectEnabled</c> to
/// <c>false</c>, because a redirect to an https authority the in-memory server does not listen on would
/// fail every other suite for a reason unrelated to what it asserts. Enforcement therefore has to be
/// proved on a host built for it, which is what the surrounding environment override and the nested host
/// types below produce - the same technique the credential rate-limit suite uses.
/// </para>
/// <para>
/// <b>Why the remote address is set by a startup filter.</b> The in-memory test server assigns no
/// connection address at all, and the forwarded-headers middleware refuses to honour a header when it
/// cannot match the caller against its trusted list - so with no address, no forwarded header is ever
/// applied and neither the positive nor the negative case could be told apart. The filter supplies one
/// before the application's own pipeline runs, which is the only place a test can, and it changes
/// nothing else: it does not touch the headers, the scheme or the trusted list. The trusted list itself
/// is left at the framework default of loopback only, so the positive case also proves that the default
/// is what the API ships with.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TransportSecurityTests
{
    /// <summary>The health endpoint, as the container and the compose topology address it.</summary>
    private static readonly Uri HealthEndpoint = new("/health", UriKind.Relative);

    /// <summary>
    /// An address inside the framework's default trusted network, <c>127.0.0.0/8</c>.
    /// </summary>
    private static readonly IPAddress TrustedHop = IPAddress.Loopback;

    /// <summary>
    /// A routable address that is in no trusted list, standing in for an arbitrary internet caller.
    /// </summary>
    /// <remarks>
    /// Taken from the documentation range reserved by RFC 5737, so it can never be a real host.
    /// </remarks>
    private static readonly IPAddress UntrustedHop = IPAddress.Parse("203.0.113.7");

    /// <summary>
    /// The authority every fact below addresses, which is deliberately NOT a loopback name.
    /// </summary>
    /// <remarks>
    /// MIGRATION: THESE FACTS WERE WRITTEN AGAINST A PREDICATE THAT EXEMPTED ONLY THE HEALTH PATH, AND THE
    /// SURVIVING ONE ALSO EXEMPTS A LOOPBACK-ADDRESSED REQUEST. Two revisions decided where transport
    /// enforcement applies. The surviving predicate refuses to redirect a request addressed to <c>localhost</c>
    /// or to a loopback literal, because the container's own probe and an operator's diagnostics reach the
    /// process that way and there is no TLS listener on the private network to redirect them to - a redirect
    /// there points at an authority nothing serves. The default client the test host hands out addresses
    /// <c>localhost</c>, so every fact here would have exercised that exemption instead of the switch under
    /// test and would have read an ordinary authentication refusal as "no redirect". Naming a documentation
    /// host restores what these facts are for. The host needs no alias row: a redirect is issued before
    /// routing, and the fixture widens the static host filter for its own invented hosts already.
    /// </remarks>
    private static readonly Uri PerimeterAddress =
        new("http://transport.example.test/", UriKind.Absolute);

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="TransportSecurityTests"/> class.</summary>
    /// <param name="fixture">The shared composed host, used only for its configuration and seed.</param>
    public TransportSecurityTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// With enforcement on, an ordinary API request arriving over cleartext is redirected to HTTPS rather
    /// than served.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The route is a real one and the client carries no credential, so without the redirect the answer
    /// would be a <c>401</c>. Asserting a redirect therefore also proves the stage runs BEFORE
    /// authentication: a cleartext credential must never be read at all, and a pipeline that
    /// authenticated first and redirected afterwards would have read it.
    /// </remarks>
    [Fact]
    public async Task ApiRequestOverCleartext_WithEnforcementEnabled_IsRedirectedToHttps()
    {
        using (ApiTestFixture.OverrideEnvironment(EnforcingConfiguration()))
        {
            await using var host = new TransportHost(TrustedHop);
            using HttpClient client = host.CreateClient(NoRedirects);
            client.BaseAddress = PerimeterAddress;

            using HttpResponseMessage response = await client.GetAsync(PortalsEndpoint());

            // MIGRATION: 308, NOT THE FRAMEWORK DEFAULT OF 307. The registration chooses a PERMANENT
            // redirect deliberately, and the trade-off is recorded on it: both codes preserve the method and
            // the body, which is the property that matters here, and a permanent answer is the honest one for
            // a deployment whose whole perimeter terminates TLS. The reversibility it gives up is bought back
            // by the bounded strict-transport-security lifetime registered beside it.
            response.StatusCode.Should().Be(
                HttpStatusCode.PermanentRedirect,
                "a cleartext API request must be refused before anything reads its credential");
            response.Headers.Location.Should().NotBeNull();
            response.Headers.Location!.Scheme.Should().Be(
                Uri.UriSchemeHttps,
                "the redirect must name the secure scheme");
        }
    }

    /// <summary>
    /// With enforcement on, the health probe is still answered over cleartext.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the fact the container topology depends on. The image probes
    /// <c>http://127.0.0.1:8080/health</c> with wget, and the compose file holds the front-end service
    /// back until that probe reports healthy; a redirect would point at an https authority the container
    /// does not serve, the probe would never succeed, and nothing would start. A <c>503</c> is tolerated
    /// for the same reason the health suite tolerates it - a dependency outage is a correct answer here -
    /// while a redirect is not tolerated at all.
    /// </remarks>
    [Fact]
    public async Task HealthProbeOverCleartext_WithEnforcementEnabled_IsNotRedirected()
    {
        using (ApiTestFixture.OverrideEnvironment(EnforcingConfiguration()))
        {
            await using var host = new TransportHost(TrustedHop);
            using HttpClient client = host.CreateClient(NoRedirects);
            client.BaseAddress = PerimeterAddress;

            using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);

            ((int)response.StatusCode).Should().BeOneOf(
                StatusCodes.Ok,
                StatusCodes.ServiceUnavailable);
            response.Headers.Location.Should().BeNull(
                "the container probe speaks cleartext to a loopback address that serves no TLS");
        }
    }

    /// <summary>
    /// SEC-B4 REGRESSION. A cleartext request that CLAIMS a loopback authority in its own <c>Host</c> header
    /// is redirected like any other, because the predicate no longer reads the host at all.
    /// </summary>
    /// <param name="claimedAuthority">The loopback authority the caller asserts.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// WHAT THIS REVERSES. The predicate used to withhold the redirect from a request whose
    /// <c>Request.Host</c> was <c>localhost</c> or a loopback literal, reasoning that such a request never
    /// traverses a network. The host is the authority the CALLER asked for: a remote client reaching a
    /// published listener could send one header, be exempted from transport enforcement, and be served over
    /// cleartext - an opt-out from HTTPS published to the internet. The remote address here is a
    /// documentation address precisely so that the request is unambiguously remote while its header claims
    /// otherwise.
    /// </para>
    /// <para>
    /// Both spellings are asserted because a fix that special-cased only the literal name would leave the
    /// address form as a working bypass, and vice versa.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task ApiRequestOverCleartext_ClaimingALoopbackAuthority_IsStillRedirected(
        string claimedAuthority)
    {
        using (ApiTestFixture.OverrideEnvironment(EnforcingConfiguration()))
        {
            await using var host = new TransportHost(UntrustedHop);
            using HttpClient client = host.CreateClient(NoRedirects);
            client.BaseAddress = new Uri($"http://{claimedAuthority}/", UriKind.Absolute);

            using HttpResponseMessage response = await client.GetAsync(PortalsEndpoint());

            response.StatusCode.Should().Be(
                HttpStatusCode.PermanentRedirect,
                "a caller must not be able to exempt itself from transport enforcement with a header");
            response.Headers.Location.Should().NotBeNull();
            response.Headers.Location!.Scheme.Should().Be(Uri.UriSchemeHttps);
        }
    }

    /// <summary>
    /// SEC-B4 REGRESSION. A request the perimeter redirects is still correlated and still recorded, because
    /// transport enforcement now runs INSIDE the correlation and request-logging stages.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// WHAT THIS REVERSES. Forwarded headers, strict transport security and the redirect were registered
    /// between the exception handler and the correlation stage, so a redirected request short-circuited
    /// before a correlation identifier existed and before the request envelope was written. The requests
    /// most worth seeing - the ones the perimeter refuses, and the only evidence that enforcement is working
    /// - were the one class that produced no log entry and no correlation header for the client's
    /// interceptor to pair with.
    /// </para>
    /// <para>
    /// The host is built immediately before the request because a Serilog logger is process-wide: every host
    /// built in this process replaces the logger the shared fixture installed, so a fact asserting on
    /// records has to own the host that writes them and register the sink on it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RedirectedCleartextRequest_IsCorrelatedAndRecorded()
    {
        using (ApiTestFixture.OverrideEnvironment(EnforcingConfiguration()))
        {
            await using var host = new TransportHost(UntrustedHop);
            using HttpClient client = host.CreateClient(NoRedirects);
            client.BaseAddress = PerimeterAddress;

            int recordsBefore = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage response = await client.GetAsync(PortalsEndpoint());

            response.StatusCode.Should().Be(HttpStatusCode.PermanentRedirect);

            response.Headers.TryGetValues(CorrelationHeader, out IEnumerable<string>? correlation)
                .Should()
                .BeTrue("a redirected request must still carry the correlation identifier");
            correlation!.Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();

            LogRecord[] envelopes = [.. RecordedLogs.Snapshot()
                .Skip(recordsBefore)
                .Where(record =>
                    record.Properties.TryGetValue("SourceContext", out object? source)
                    && string.Equals(source as string, EnvelopeSourceContext, StringComparison.Ordinal))];

            envelopes.Should().NotBeEmpty(
                "the request envelope is written for a redirected request, not only for a served one");

            bool recordedTheRedirect = envelopes.Any(record =>
                record.Properties.TryGetValue("StatusCode", out object? status)
                && status is int code
                && code == (int)HttpStatusCode.PermanentRedirect);

            recordedTheRedirect.Should().BeTrue(
                "the recorded envelope must report the status the caller actually received");
        }
    }

    /// <summary>
    /// A request forwarded as HTTPS by a TRUSTED hop is treated as secure and is not redirected.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the fact that proves <c>UseForwardedHeaders</c> is actually in the pipeline. Before it was,
    /// the header below was recorded by the proxy and ignored here, so a browser request that reached the
    /// proxy over TLS presented as cleartext to the application - which is why enforcement could not be
    /// switched on without breaking every proxied deployment. The answer is a <c>401</c> because the
    /// request is genuinely served and carries no credential, which is exactly the outcome a redirect
    /// would have replaced.
    /// </remarks>
    [Fact]
    public async Task ApiRequestForwardedAsHttpsByATrustedHop_IsServedRatherThanRedirected()
    {
        using (ApiTestFixture.OverrideEnvironment(EnforcingConfiguration()))
        {
            await using var host = new TransportHost(TrustedHop);
            using HttpClient client = host.CreateClient(NoRedirects);
            client.BaseAddress = PerimeterAddress;
            client.DefaultRequestHeaders.Add(ForwardedProtoHeader, Uri.UriSchemeHttps);

            using HttpResponseMessage response = await client.GetAsync(PortalsEndpoint());

            response.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                "a request forwarded as https by a trusted proxy is already secure");
            response.Headers.Location.Should().BeNull();
        }
    }

    /// <summary>
    /// THE NEGATIVE CONTROL. The same header from an UNTRUSTED hop is ignored, and the request is still
    /// redirected.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Without this, the fact above would pass equally well against a pipeline that trusted every caller's
    /// claim about its own scheme - which would let any client disable transport enforcement for itself by
    /// adding one header, and would let it choose its own rate-limit partition by adding another. Nothing
    /// is trusted unless a deployment names it, and the framework default names only the loopback
    /// network.
    /// </remarks>
    [Fact]
    public async Task ApiRequestForwardedAsHttpsByAnUntrustedHop_IsStillRedirected()
    {
        using (ApiTestFixture.OverrideEnvironment(EnforcingConfiguration()))
        {
            await using var host = new TransportHost(UntrustedHop);
            using HttpClient client = host.CreateClient(NoRedirects);
            client.BaseAddress = PerimeterAddress;
            client.DefaultRequestHeaders.Add(ForwardedProtoHeader, Uri.UriSchemeHttps);

            using HttpResponseMessage response = await client.GetAsync(PortalsEndpoint());

            response.StatusCode.Should().Be(
                HttpStatusCode.PermanentRedirect,
                "a forwarded scheme from a hop the deployment never named must not be believed");
        }
    }

    /// <summary>
    /// The header the proxy sends and the pipeline now reads.
    /// </summary>
    /// <remarks>
    /// Spelled as a literal rather than taken from <see cref="ForwardedHeadersDefaults"/> deliberately:
    /// this is the name <c>docker/nginx.conf</c> sends, and the suite must fail if the pipeline stops
    /// reading that exact name.
    /// </remarks>
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    /// <summary>The header the correlation identifier travels back on.</summary>
    /// <remarks>
    /// A literal rather than a reference to the middleware's own constant, for the same reason the forwarded
    /// header above is: this is the name the client's interceptor and the proxy both spell, so a rename that
    /// silently broke the loop must fail here.
    /// </remarks>
    private const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>The logger category the request envelope is written under.</summary>
    private const string EnvelopeSourceContext = "DnnMigration.Api.Middleware.RequestLoggingMiddleware";

    /// <summary>A client that reports a redirect instead of following it.</summary>
    private static WebApplicationFactoryClientOptions NoRedirects => new() { AllowAutoRedirect = false };

    /// <summary>The portals collection, which requires a credential and is not exempt from the redirect.</summary>
    /// <returns>The relative address.</returns>
    private static Uri PortalsEndpoint() => new("/api/v1/portals", UriKind.Relative);

    /// <summary>
    /// The fixture's own configuration with transport enforcement switched back on.
    /// </summary>
    /// <returns>The environment for the ad-hoc hosts below.</returns>
    /// <remarks>
    /// Built from <see cref="ApiTestFixture.HostConfiguration"/> so that the database, the signing key and
    /// the credential policy are identical to every other suite's, and the ONE difference is the switch
    /// under test. Nothing about the trusted-proxy list is set, so the framework default of loopback only
    /// is what the positive and negative cases above are distinguished by.
    /// </remarks>
    private IReadOnlyDictionary<string, string?> EnforcingConfiguration()
    {
        var configuration = new Dictionary<string, string?>(_fixture.HostConfiguration(), StringComparer.Ordinal)
        {
            ["Https__RedirectEnabled"] = "true",

            // MIGRATION: THE TRUSTED HOP IS NAMED, AND IT HAS TO BE. These facts were written against a
            // pipeline that processed forwarded headers unconditionally and relied on the framework's
            // loopback-only default to distinguish the trusted case from the untrusted one. The surviving
            // pipeline does not register forwarded-header processing AT ALL unless a deployment names a proxy
            // or a network - "nothing is trusted unless a deployment names it" - so with no declaration the
            // positive case below could never be served: its forwarded scheme would be ignored exactly like
            // the untrusted one, and the fact would have passed for the wrong reason while its negative
            // control passed for no reason. Naming the trusted hop here is what makes the pair
            // discriminating: the middleware runs, honours the address it was told to honour, and refuses the
            // documentation address the negative control arrives from.
            ["Proxy__KnownProxies__0"] = TrustedHop.ToString(),
        };

        return configuration;
    }

    /// <summary>The two status codes the health probe may legitimately answer with.</summary>
    private static class StatusCodes
    {
        /// <summary>Every registered probe reported healthy.</summary>
        internal const int Ok = 200;

        /// <summary>A dependency is unreachable, which is a correct answer rather than a fault.</summary>
        internal const int ServiceUnavailable = 503;
    }

    /// <summary>
    /// A host built with the fixture's environment, whose connections report a chosen remote address.
    /// </summary>
    /// <param name="remoteAddress">The address every request appears to arrive from.</param>
    /// <remarks>
    /// The recording sink is registered as well as the address filter. A Serilog logger is process-wide, so
    /// this host replaces the one the shared fixture installed for as long as it lives; without the
    /// registration the events its own requests write would reach no sink at all, and the envelope fact above
    /// would observe nothing. Registering it changes nothing for the facts that assert only on responses.
    /// </remarks>
    private sealed class TransportHost(IPAddress remoteAddress) : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(remoteAddress));
                services.AddSingleton<ILogEventSink>(RecordedLogs.Sink);
            });
        }
    }

    /// <summary>
    /// Assigns a connection address before the application's own pipeline runs.
    /// </summary>
    /// <param name="remoteAddress">The address to report.</param>
    /// <remarks>
    /// A startup filter is the only hook that runs OUTSIDE the pipeline the application composes, which is
    /// what this needs: the forwarded-headers stage is the first stage of that pipeline, and the address
    /// has to exist before it reads. Nothing else is altered.
    /// </remarks>
    private sealed class RemoteAddressStartupFilter(IPAddress remoteAddress) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return builder =>
            {
                builder.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = remoteAddress;
                    await continuation().ConfigureAwait(false);
                });

                next(builder);
            };
        }
    }
}
