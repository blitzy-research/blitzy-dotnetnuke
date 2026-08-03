using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Bounds how often, and how many at once, credential-bearing requests are
/// accepted.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this is the named compensating control for a capability the migration
/// deliberately dropped. The legacy sign-in screen carried an image-based human
/// verification challenge, gated the entire sign-in on that control reporting itself
/// valid, and could be switched on per tenant. The control belongs to a tree this
/// migration excludes in full, so there is nothing for a request contract to bind
/// to and the challenge is gone. Its purpose - making automated credential guessing
/// expensive - is met here instead, and the request contracts that used to name the
/// challenge now name this file.
/// </para>
/// <para>
/// <b>Two limiters, because the problem has two halves.</b> A window limiter bounds
/// how many credential requests one caller may make in a period, which is what makes
/// guessing slow. A concurrency limiter bounds how many are being processed at any
/// instant, which is what bounds the work itself: password hashing is deliberately
/// expensive, so without a concurrency bound the amount of processor time and memory
/// this application spends is chosen by whoever is calling it. Neither limiter
/// substitutes for the other, and neither substitutes for the ceiling on credential
/// length applied by the request validators before a credential ever reaches the
/// hasher.
/// </para>
/// <para>
/// <b>An endpoint is classified from its METADATA FIRST and from its path only as a
/// fall-back.</b> <see cref="CredentialEndpointAttribute"/> states that an action
/// handles a credential, and that statement is what brings it under both limiters.
/// The whole-segment path matcher is retained underneath - it is what still catches an
/// endpoint whose author forgets the attribute, and it is the only classifier
/// available for a request that matched no endpoint - but it is no longer the only
/// source of truth, because a path is not a statement about what an action does. That
/// distinction was not academic: the path list matched none of the three endpoints
/// that hash a credential outside the sign-in flow - account creation on
/// <c>/users</c>, tenant provisioning on <c>/portals</c>, which hashes the
/// administrator credential it creates, and the administrative reset, whose
/// <c>password-reset</c> segment is not equal to <c>password</c> - so all three ran
/// with no window and no concurrency bound. Requests that are not credential-bearing
/// pass through a single shared no-limit partition, so this file imposes no cost on
/// the rest of the API.
/// </para>
/// <para>
/// <b>What the window partition is, and what it is not.</b> The partition key is the
/// remote address as this process observes it. Behind a reverse proxy - which is the
/// topology this solution ships, with the proxy forwarding to the API container -
/// that address is the proxy's, so the window becomes one budget shared by the whole
/// deployment rather than a budget per client. The permit count below is therefore
/// chosen so that it remains a sensible budget in that case, and a deployment that
/// wants a genuine per-client budget declares its proxies in configuration so that
/// forwarded-header processing can recover the original address. Declaring a wider
/// set of trusted proxies than a deployment actually operates is worse than not
/// declaring any: a forwarded address is supplied by the caller, so trusting one
/// from an untrusted hop lets the caller choose its own partition and step around
/// the window entirely. The concurrency limiter is unaffected by any of this,
/// because it is a single bound on the whole process.
/// </para>
/// <para>
/// <b>A refusal discloses nothing.</b> The response is a fixed problem-details
/// payload with a fixed title and detail. It does not name the account, does not
/// echo any submitted value, and does not differ between a caller who guessed an
/// existing account and one who guessed a name that does not exist - so it cannot be
/// used to enumerate accounts, which would hand an attacker exactly the shortlist
/// that makes guessing worthwhile.
/// </para>
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Name of the opt-in policy an action may declare in addition to the global
    /// matcher: <c>credential-endpoints</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declaring it on an action is additive: the action is then subject to this policy's window as well as
    /// the global one. The two are keyed identically but are separate limiter instances with separate
    /// budgets, and one permit is taken from each per request, so they are consumed in lockstep and the
    /// effective limit is unchanged.
    /// </para>
    /// <para>
    /// APPLIED UNCONDITIONALLY. This policy's partitioner used to ask the same path matcher the global
    /// limiter asks, and therefore returned the shared no-limit partition for precisely the paths the policy
    /// was documented as existing to cover - so declaring it on an unmatched credential endpoint changed
    /// nothing whatsoever. An author who names this policy has already made the statement the matcher was
    /// guessing at, and second-guessing a declaration with a heuristic is what made the opt-in inert.
    /// </para>
    /// <para>
    /// A named policy contributes exactly ONE partition, so it can carry the window but not also the
    /// process-wide concurrency bound. Both bounds come from the global chained limiter, and what brings an
    /// endpoint under that chain is <see cref="CredentialEndpointAttribute"/>. Marking an action is
    /// therefore the complete measure; declaring this policy in addition documents the intent at the action.
    /// </para>
    /// </remarks>
    public const string CredentialPolicyName = "credential-endpoints";

    /// <summary>
    /// The policy name an action declares to opt into the credential window explicitly.
    /// </summary>
    /// <remarks>
    /// The sign-in, refresh and sign-out actions declare this policy by name. It applies the same window the
    /// global limiter applies, unconditionally and for the same reason
    /// <see cref="CredentialPolicyName"/> does, so declaring it adds a second, identically keyed budget
    /// rather than a different rule: the two are consumed in lockstep and the effective limit is unchanged.
    /// Both exist on purpose - the annotation documents the intent at the action, and the global classifier
    /// is what makes the control impossible to omit on an endpoint whose author forgets it.
    /// </remarks>
    public const string AuthenticationPolicyName = "authentication";

    /// <summary>
    /// The configuration section that sizes the credential window: <c>RateLimiting:Authentication</c>.
    /// </summary>
    /// <remarks>
    /// Two keys are read, <c>PermitLimit</c> and <c>WindowSeconds</c>, and both are optional. Sizing the
    /// window from configuration rather than from a constant is what lets a deployment behind a proxy that
    /// shares one address widen it, and a deployment that recovers the caller's real address tighten it,
    /// without a rebuild. The concurrency bound is deliberately not configurable: it protects this
    /// process's own processor and memory, which is a property of the host rather than of the topology.
    /// </remarks>
    public const string ConfigurationSectionName = "RateLimiting:Authentication";

    private const string PermitLimitKey = "PermitLimit";

    private const string WindowSecondsKey = "WindowSeconds";

    /// <summary>
    /// Credential requests permitted per partition, per window: 30.
    /// </summary>
    /// <remarks>
    /// Sized to be defensible in the pessimistic case described on this type, where
    /// the partition collapses to a single deployment-wide budget: thirty
    /// credential-bearing requests a minute is far beyond what an administration
    /// console generates, and far below what automated guessing needs to be
    /// worthwhile. Where the original client address is recoverable the same number
    /// becomes a per-client budget, which is stricter again.
    /// </remarks>
    private const int DefaultCredentialPermitsPerWindow = 30;

    /// <summary>
    /// Credential requests processed at once, across the whole process: 4.
    /// </summary>
    /// <remarks>
    /// This is the bound that makes processor and memory consumption independent of
    /// the caller. Password hashing at the configured cost takes a substantial
    /// fraction of a second by design, so four in flight is a deliberate ceiling on
    /// how much of this process's time can be spent hashing, whatever arrives.
    /// </remarks>
    private const int CredentialConcurrentPermits = 4;

    /// <summary>
    /// Credential requests allowed to wait for a concurrency permit: 20.
    /// </summary>
    /// <remarks>
    /// A queue rather than an immediate refusal, because a short burst of legitimate
    /// sign-ins should be served slightly late rather than rejected. The queue is
    /// bounded so the burst cannot itself become the memory consumption the limiter
    /// exists to prevent; beyond it, requests are refused.
    /// </remarks>
    private const int CredentialQueuedPermits = 20;

    /// <summary>
    /// Length of the window: one minute.
    /// </summary>
    private static readonly TimeSpan DefaultCredentialWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Partition key used for every request that is not credential-bearing.
    /// </summary>
    /// <remarks>
    /// One constant key, deliberately, so that the ordinary traffic of the API shares
    /// a single unlimited partition rather than creating one per caller. A partition
    /// per caller would allocate a limiter for every distinct address that ever
    /// reaches the application, to enforce nothing.
    /// </remarks>
    private const string UnlimitedPartitionKey = "not-credential-bearing";

    /// <summary>
    /// Partition key of the single process-wide concurrency bound.
    /// </summary>
    private const string ConcurrencyPartitionKey = "credential-concurrency";

    /// <summary>
    /// Partition key used when the remote address cannot be determined.
    /// </summary>
    /// <remarks>
    /// Requests with no observable address share one partition rather than being
    /// exempted. An unattributable request is the one most in need of a bound, and
    /// exempting it would turn a missing address into a way around the window.
    /// </remarks>
    private const string UnattributedClientPartitionKey = "credential-window:unattributed";

    /// <summary>
    /// Prefix distinguishing window partitions from the other keys in play.
    /// </summary>
    private const string ClientPartitionKeyPrefix = "credential-window:";

    /// <summary>
    /// Path segments that mark a request as credential-bearing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched as whole segments, case-insensitively, and therefore independent of
    /// both the API version in the path and the route templates the controllers
    /// eventually declare. Whole-segment matching is what keeps the list from
    /// over-reaching: a resource whose name merely begins with one of these words is
    /// not matched.
    /// </para>
    /// <para>
    /// The list is deliberately wider than the endpoints the migration plan names. It
    /// covers sign-in, sign-out, token refresh, password change and password
    /// recovery, and it errs toward including a segment rather than excluding it,
    /// because the cost of a false match is a bounded endpoint that did not need
    /// bounding while the cost of a miss is an unbounded credential endpoint.
    /// </para>
    /// </remarks>
    private static readonly string[] CredentialPathSegments =
    [
        "auth",
        "login",
        "logout",
        "password",
        "passwords",
        "recover",
        "refresh",
        "reset",
        "token",
        "tokens",
    ];

    /// <summary>
    /// Request methods that can carry a credential.
    /// </summary>
    /// <remarks>
    /// A credential is submitted in a request body, so only the methods that carry
    /// one are matched. This is what keeps an ordinary read of the signed-in caller's
    /// own details - a request under a matched path, but with no body and no
    /// credential - out of the window, where a shared budget would otherwise be spent
    /// on it every time the front end loads.
    /// </remarks>
    private static readonly string[] CredentialMethods = ["POST", "PUT", "PATCH"];

    /// <summary>
    /// Adds the credential window and concurrency bounds, and the opt-in policy that
    /// names the same window.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">
    /// The configuration the window is read from. The two keys under
    /// <c>RateLimiting:Authentication</c> are optional; absent, the shipped defaults apply,
    /// and present-but-not-positive is refused at start-up rather than silently corrected,
    /// because a zero permit count would lock every caller out of authentication.
    /// </param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The two limiters are chained rather than combined into one partitioner. A
    /// chain acquires from each in turn and stops at the first refusal, which is the
    /// behaviour wanted here: a caller who has exhausted the window is refused
    /// without being queued for a concurrency permit it would only waste.
    /// </remarks>
    public static IServiceCollection AddCredentialRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        (int permitLimit, TimeSpan window) = ReadWindowSize(configuration);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRefusalAsync;

            // The two named policies apply the window UNCONDITIONALLY. Routing them through the same
            // classifier the global limiter uses is what made them inert: the classifier answered "not
            // credential-bearing" for exactly the endpoints an author would have declared a policy on,
            // so the declaration resolved to the shared no-limit partition and enforced nothing.
            options.AddPolicy(
                CredentialPolicyName,
                context => BuildWindowPartition(context, permitLimit, window));

            options.AddPolicy(
                AuthenticationPolicyName,
                context => BuildWindowPartition(context, permitLimit, window));

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(
                    context => ResolveWindowPartition(context, permitLimit, window)),
                PartitionedRateLimiter.Create<HttpContext, string>(ResolveConcurrencyPartition));
        });

        return services;
    }

    /// <summary>
    /// Reads the window size from configuration, falling back to the shipped defaults.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>The permit count and the window duration to apply.</returns>
    /// <exception cref="InvalidOperationException">
    /// A configured value is present but is zero or negative.
    /// </exception>
    /// <remarks>
    /// A missing key falls back to the shipped default, because a deployment that says nothing about rate
    /// limiting must still be rate limited. A present but unusable value is refused rather than silently
    /// replaced: zero permits would refuse every sign-in, and a non-positive window is not a period at
    /// all, so both are configuration faults an operator has to see while the host is starting.
    /// </remarks>
    private static (int PermitLimit, TimeSpan Window) ReadWindowSize(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(ConfigurationSectionName);

        int permitLimit = section.GetValue<int?>(PermitLimitKey) ?? DefaultCredentialPermitsPerWindow;
        int windowSeconds =
            section.GetValue<int?>(WindowSecondsKey) ?? (int)DefaultCredentialWindow.TotalSeconds;

        if (permitLimit <= 0)
        {
            throw new InvalidOperationException(
                $"'{ConfigurationSectionName}:{PermitLimitKey}' is {permitLimit.ToString(CultureInfo.InvariantCulture)}, which would refuse every credential request. Supply a positive count, or remove the key to accept the shipped default of {DefaultCredentialPermitsPerWindow.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (windowSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"'{ConfigurationSectionName}:{WindowSecondsKey}' is {windowSeconds.ToString(CultureInfo.InvariantCulture)}, which is not a period. Supply a positive number of seconds, or remove the key to accept the shipped default of {((int)DefaultCredentialWindow.TotalSeconds).ToString(CultureInfo.InvariantCulture)}.");
        }

        return (permitLimit, TimeSpan.FromSeconds(windowSeconds));
    }

    /// <summary>
    /// Chooses the window partition for a request.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="permitLimit">
    /// The number of credential requests one caller may make per window, as resolved from
    /// configuration.
    /// </param>
    /// <param name="window">The length of that window.</param>
    /// <returns>
    /// A window partition keyed by the caller's observable address for a
    /// credential-bearing request; the shared unlimited partition otherwise.
    /// </returns>
    /// <remarks>
    /// The window does not queue. A caller who has spent the budget is told to wait
    /// rather than held open, because holding a guessing attempt open consumes exactly
    /// the resources the limiter is protecting.
    /// </remarks>
    private static RateLimitPartition<string> ResolveWindowPartition(
        HttpContext context,
        int permitLimit,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsCredentialBearing(context))
        {
            return RateLimitPartition.GetNoLimiter(UnlimitedPartitionKey);
        }

        return BuildWindowPartition(context, permitLimit, window);
    }

    /// <summary>
    /// Builds the window partition for a request, without asking whether it is credential-bearing.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="permitLimit">Permits per window.</param>
    /// <param name="window">Length of the window.</param>
    /// <returns>A window partition keyed by the caller's observable address.</returns>
    /// <remarks>
    /// <para>
    /// This is what the two NAMED policies use, and the absence of a classification test here is the whole
    /// point of separating it from <see cref="ResolveWindowPartition"/>. A named policy is reached only
    /// because an action declared it, and that declaration is a statement that the action handles a
    /// credential - so re-deriving the same conclusion from the request's path can only ever contradict the
    /// author, which is exactly what it used to do.
    /// </para>
    /// <para>
    /// The window does not queue. A caller who has spent the budget is told to wait rather than held open,
    /// because holding a guessing attempt open consumes exactly the resources the limiter is protecting.
    /// </para>
    /// </remarks>
    private static RateLimitPartition<string> BuildWindowPartition(
        HttpContext context,
        int permitLimit,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(context);

        return RateLimitPartition.GetFixedWindowLimiter(
            ResolveClientPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }

    /// <summary>
    /// Chooses the concurrency partition for a request.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <returns>
    /// The single process-wide concurrency partition for a credential-bearing
    /// request; the shared unlimited partition otherwise.
    /// </returns>
    /// <remarks>
    /// One partition for the whole process, not one per caller. The resource being
    /// protected - this process's processor time - is shared, so a per-caller
    /// concurrency bound would let the total grow with the number of callers, which
    /// is the opposite of a bound.
    /// </remarks>
    private static RateLimitPartition<string> ResolveConcurrencyPartition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsCredentialBearing(context))
        {
            return RateLimitPartition.GetNoLimiter(UnlimitedPartitionKey);
        }

        return RateLimitPartition.GetConcurrencyLimiter(
            ConcurrencyPartitionKey,
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = CredentialConcurrentPermits,
                QueueLimit = CredentialQueuedPermits,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    }

    /// <summary>
    /// Reports whether a request handles a credential.
    /// </summary>
    /// <param name="context">The request being classified.</param>
    /// <returns>
    /// <see langword="true"/> when the matched endpoint declares itself a credential endpoint, or when the
    /// method can carry a body and any whole path segment names a credential concern.
    /// </returns>
    /// <remarks>
    /// <para>
    /// METADATA IS ASKED FIRST, AND ITS ANSWER IS FINAL WHEN AFFIRMATIVE. The rate-limiting middleware is
    /// ordered after routing, so the matched endpoint - and therefore
    /// <see cref="CredentialEndpointAttribute"/> - is available here. A marked action is credential-bearing
    /// whatever its path and whatever its method: the mark is a statement about what the action DOES, and no
    /// property of the request can contradict it.
    /// </para>
    /// <para>
    /// THE PATH MATCHER REMAINS AS A FALL-BACK RATHER THAN AS THE RULE. It still catches an endpoint whose
    /// author forgot the mark, and it is the only classifier available for a request that matched no endpoint
    /// at all. It is deliberately not narrowed now that the mark exists: a false match costs a bounded
    /// endpoint that did not need bounding, while a miss costs an unbounded credential endpoint, and three
    /// such misses are what this correction was written for.
    /// </para>
    /// </remarks>
    private static bool IsCredentialBearing(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<CredentialEndpointAttribute>() is not null)
        {
            return true;
        }

        if (!CredentialMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string? path = context.Request.Path.Value;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (Range segment in SplitIntoSegments(path))
        {
            ReadOnlySpan<char> candidate = path.AsSpan()[segment];

            foreach (string credentialSegment in CredentialPathSegments)
            {
                if (candidate.Equals(credentialSegment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a request path into its non-empty segments.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>The bounds of each non-empty segment, in order.</returns>
    /// <remarks>
    /// Ranges rather than substrings, so classifying a request allocates nothing. This
    /// runs on every request that reaches the application, including the ones the
    /// limiter will not bound, so its cost is paid by all traffic.
    /// </remarks>
    private static List<Range> SplitIntoSegments(string path)
    {
        List<Range> segments = [];
        int start = 0;

        for (int index = 0; index <= path.Length; index++)
        {
            bool atBoundary = index == path.Length || path[index] == '/';

            if (!atBoundary)
            {
                continue;
            }

            if (index > start)
            {
                segments.Add(new Range(start, index));
            }

            start = index + 1;
        }

        return segments;
    }

    /// <summary>
    /// Builds the window partition key from the caller's observable address.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <returns>A bounded, non-null partition key.</returns>
    /// <remarks>
    /// <para>
    /// An address mapped from version four into version six is normalised back, so
    /// the same client cannot occupy two partitions - and therefore two budgets -
    /// depending on how the socket was accepted.
    /// </para>
    /// <para>
    /// The key is derived from the address alone and never from anything the caller
    /// submits. A key drawn from a header or a body field would let a caller mint a
    /// fresh partition per request, which is a limiter that limits nothing; it would
    /// also make the number of live partitions unbounded, which is a memory leak
    /// wearing the costume of a security control.
    /// </para>
    /// </remarks>
    private static string ResolveClientPartitionKey(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;

        if (address is null)
        {
            return UnattributedClientPartitionKey;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return string.Concat(ClientPartitionKeyPrefix, address.ToString());
    }

    /// <summary>
    /// Answers a refused request with a fixed problem-details payload.
    /// </summary>
    /// <param name="context">The refusal, carrying the request and the lease.</param>
    /// <param name="cancellationToken">
    /// The request's abort signal, observed while writing the payload so a caller who
    /// has already gone away is not written to.
    /// </param>
    /// <returns>A task that completes when the refusal has been written.</returns>
    /// <remarks>
    /// <para>
    /// The retry hint is taken from the lease rather than computed, so it reflects
    /// the limiter that actually refused. A window limiter reports when its permits
    /// replenish; a concurrency limiter reports nothing, and no hint is invented in
    /// its place, because a wrong hint is worse than none.
    /// </para>
    /// <para>
    /// Every word of the payload is fixed. It carries no account name, no submitted
    /// value, no address and no count, so two callers - one who guessed at an account
    /// that exists and one who guessed at an account that does not - receive
    /// byte-identical responses.
    /// </para>
    /// <para>
    /// The shared members are filled by the registered
    /// <see cref="ProblemDetailsFactory"/> rather than written here, which is the same
    /// arrangement the global exception handler uses and is what keeps this refusal's
    /// member vocabulary identical to every other problem response the application
    /// produces - including the problem-type link and the trace identifier, both of
    /// which a hand-built payload silently omits. Only the two members that are
    /// specific to a refusal are supplied; the status code is passed once and the
    /// factory copies it into the body, so the status line and the body cannot
    /// disagree.
    /// </para>
    /// </remarks>
    private static async ValueTask WriteRefusalAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext httpContext = context.HttpContext;

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        IProblemDetailsService problemDetails =
            httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        ProblemDetailsFactory problemDetailsFactory =
            httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetailsFactory.CreateProblemDetails(
                httpContext,
                StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                type: null,
                detail: "Too many requests have been received. Wait a moment and try again.",
                instance: null),
        }).ConfigureAwait(false);
    }
}
