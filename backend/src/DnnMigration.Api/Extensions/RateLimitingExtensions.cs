using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using DnnMigration.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace DnnMigration.Api.Extensions;

/// <summary>Bounds how often, and how many at once, credential-bearing requests are accepted.</summary>
/// <remarks>
/// <para>
/// MIGRATION: this is the named compensating control for a capability the migration deliberately dropped.
/// The legacy sign-in screen carried an image-based human verification challenge, gated the entire sign-in
/// on that control reporting itself valid, and could be switched on per tenant.
/// </para>
/// <para>
/// <b>An endpoint is classified from its METADATA FIRST and from its path only as a fall-back.</b> <see
/// cref="CredentialEndpointAttribute"/> states that an action handles a credential, and that statement is
/// what brings it under both limiters.
/// </para>
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Name of the opt-in policy an action may declare in addition to the global matcher:
    /// <c>credential-endpoints</c>.
    /// </summary>
    /// <remarks>
    /// Declaring it on an action is additive: the action is then subject to this policy's window as well as
    /// the global one. The two are keyed identically but are separate limiter instances with separate
    /// budgets, and one permit is taken from each per request, so they are consumed in lockstep and the
    /// effective limit is unchanged.
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

    /// <summary>The policy name the caller-description read declares: <c>session-read</c>.</summary>
    /// <remarks>
    /// SEC: A SEPARATE BUDGET, AND THAT SEPARATION IS THE WHOLE POINT. The caller-description read (<c>GET
    /// /auth/me</c>) carries no credential, so it must stay bounded - an unbounded read is something a
    /// stolen token can be probed against indefinitely - but it must NOT share the budget that sign-in
    /// draws on.
    /// </remarks>
    public const string SessionReadPolicyName = "session-read";

    /// <summary>Policy name for session revocation: <c>POST /auth/logout</c>.</summary>
    /// <remarks>
    /// SEC: REVOCATION MUST NOT BE STARVABLE BY SIGN-IN TRAFFIC. Withdrawing a refresh token is the one
    /// operation whose whole purpose is to shut a session down, so refusing it is not a neutral outcome -
    /// it leaves a token the caller has asked to have revoked live until it expires on its own.
    /// </remarks>
    public const string RevocationPolicyName = "revocation";

    /// <summary>Policy name for profile replacements that evaluate tenant-authored regular expressions.</summary>
    public const string ProfileWritePolicyName = "profile-write";

    /// <summary>
    /// Policy name for invitation-code redemption: <c>POST /users/{userId}/services/redemptions</c>.
    /// </summary>
    /// <remarks>
    /// SEC: REDEMPTION IS A CREDENTIAL SUBMISSION IN EVERYTHING BUT NAME, AND IT WAS UNBOUNDED. The action
    /// takes a secret the caller either knows or does not, compares it against every role of the tenant,
    /// and grants membership of every role that bears it - a private, paid or permission-carrying role
    /// included, because an invitation code IS the bypass for a service that is not published.
    /// </remarks>
    public const string RedemptionPolicyName = "service-redemption";

    /// <summary>
    /// The configuration section that sizes the credential window: <c>RateLimiting:Authentication</c>.
    /// </summary>
    /// <remarks>
    /// Two keys are read, <c>PermitLimit</c> and <c>WindowSeconds</c>, and both are optional. Sizing the
    /// window from configuration rather than from a constant is what lets a deployment behind a proxy that
    /// shares one address widen it, and a deployment that recovers the caller's real address tighten it,
    /// without a rebuild.
    /// </remarks>
    public const string ConfigurationSectionName = "RateLimiting:Authentication";

    private const string PermitLimitKey = "PermitLimit";

    private const string WindowSecondsKey = "WindowSeconds";

    /// <summary>Credential requests permitted per partition, per window: 30.</summary>
    /// <remarks>
    /// Sized to be defensible in the pessimistic case described on this type, where the partition collapses
    /// to a single deployment-wide budget: thirty credential-bearing requests a minute is far beyond what
    /// an administration console generates, and far below what automated guessing needs to be worthwhile.
    /// </remarks>
    private const int DefaultCredentialPermitsPerWindow = 30;

    /// <summary>Profile replacements permitted per client per minute.</summary>
    private const int ProfileWritePermitsPerWindow = 20;

    /// <summary>Invitation-code redemptions permitted per account, per client address, per minute: 5.</summary>
    /// <remarks>
    /// MEASURED AGAINST THE GESTURE RATHER THAN AGAINST THE ATTACK, which is the only way to size a bound
    /// like this without guessing. Redeeming an invitation code is a single deliberate act: a person is
    /// handed a code, types it once, and either it works or they check it and try again.
    /// </remarks>
    private const int RedemptionPermitsPerWindow = 5;

    /// <summary>Credential requests processed at once, across the whole process: 4.</summary>
    /// <remarks>
    /// This is the bound that makes processor and memory consumption independent of the caller. Password
    /// hashing at the configured cost takes a substantial fraction of a second by design, so four in flight
    /// is a deliberate ceiling on how much of this process's time can be spent hashing, whatever arrives.
    /// </remarks>
    private const int CredentialConcurrentPermits = 4;

    /// <summary>Credential requests allowed to wait for a concurrency permit: 20.</summary>
    private const int CredentialQueuedPermits = 20;

    /// <summary>Length of the window: one minute.</summary>
    private static readonly TimeSpan DefaultCredentialWindow = TimeSpan.FromMinutes(1);

    /// <summary>Partition key used for every request that is not credential-bearing.</summary>
    private const string UnlimitedPartitionKey = "not-credential-bearing";

    /// <summary>Partition key of the single process-wide concurrency bound.</summary>
    private const string ConcurrencyPartitionKey = "credential-concurrency";

    /// <summary>Partition key suffix used when the remote address cannot be determined.</summary>
    /// <remarks>
    /// Requests with no observable address share one partition rather than being exempted. An
    /// unattributable request is the one most in need of a bound, and exempting it would turn a missing
    /// address into a way around the window.
    /// </remarks>
    private const string UnattributedClientSuffix = "unattributed";

    /// <summary>Prefix distinguishing window partitions from the other keys in play.</summary>
    private const string ClientPartitionKeyPrefix = "credential-window:";

    /// <summary>
    /// Prefix distinguishing the caller-description window's partitions from the credential window's.
    /// </summary>
    private const string SessionReadPartitionKeyPrefix = "session-read-window:";

    /// <summary>Prefix distinguishing the revocation window's partitions from the credential window's.</summary>
    /// <remarks>
    /// This constant IS the separation. Two partitions keyed by the same caller address but under different
    /// prefixes are two independent budgets, so sign-in traffic and revocation traffic can no longer
    /// exhaust one another.
    /// </remarks>
    private const string RevocationPartitionKeyPrefix = "revocation-window:";

    /// <summary>Prefix separating profile-validation work from every credential/session budget.</summary>
    private const string ProfileWritePartitionKeyPrefix = "profile-write-window:";

    /// <summary>Prefix separating invitation-code redemption from every other budget.</summary>
    /// <remarks>
    /// This constant IS the separation, as it is for the two prefixes above: two partitions keyed by the
    /// same caller under different prefixes are two independent budgets, and giving redemption the
    /// credential prefix would let guessing spend the window ordinary sign-in needs.
    /// </remarks>
    private const string RedemptionPartitionKeyPrefix = "service-redemption-window:";

    /// <summary>Route key naming the account a redemption acts on: <c>userId</c>.</summary>
    private const string AccountRouteKey = "userId";

    /// <summary>
    /// Stands in for the account portion of a redemption partition key when the request names no account.
    /// </summary>
    /// <remarks>
    /// Reachable only for a request that matched the route without an integer account segment, which the
    /// route constraint makes impossible, or for one that reached this partitioner without routing having
    /// run.
    /// </remarks>
    private const string UnattributedAccountSuffix = "unattributed-account";

    /// <summary>Path segments that mark a request as credential-bearing.</summary>
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
        // SEC: THE REDEMPTION PATH, WHOSE ABSENCE FROM THIS LIST LEAVES THAT ENDPOINT UNBOUNDED. A
        // submitted invitation code is a secret the caller either knows or does not, and the answer is
        // distinguishable either way, so the route is credential-bearing in substance whatever its
        // vocabulary.
        "redemptions",
    ];

    /// <summary>Request methods that can carry a credential.</summary>
    /// <remarks>
    /// A credential is submitted in a request body, so only the methods that carry one are matched. This is
    /// what keeps an ordinary read of the signed-in caller's own details - a request under a matched path,
    /// but with no body and no credential - out of the window, where a shared budget would otherwise be
    /// spent on it every time the front end loads.
    /// </remarks>
    private static readonly string[] CredentialMethods = ["POST", "PUT", "PATCH"];

    /// <summary>
    /// Adds the credential window and concurrency bounds, and the opt-in policy that names the same window.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The configuration the window is read from.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The two limiters are chained rather than combined into one partitioner. A chain acquires from each
    /// in turn and stops at the first refusal, which is the behaviour wanted here: a caller who has
    /// exhausted the window is refused without being queued for a concurrency permit it would only waste.
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
            // credential-bearing" for exactly the endpoints an author would have declared a policy on, so
            // the declaration resolved to the shared no-limit partition and enforced nothing.
            options.AddPolicy(
                CredentialPolicyName,
                context => BuildWindowPartition(context, permitLimit, window));

            options.AddPolicy(
                AuthenticationPolicyName,
                context => BuildWindowPartition(context, permitLimit, window));

            options.AddPolicy(
                SessionReadPolicyName,
                context => BuildWindowPartition(context, permitLimit, window, SessionReadPartitionKeyPrefix));

            options.AddPolicy(
                RevocationPolicyName,
                context => BuildWindowPartition(context, permitLimit, window, RevocationPartitionKeyPrefix));

            options.AddPolicy(
                ProfileWritePolicyName,
                context => BuildWindowPartition(
                    context,
                    ProfileWritePermitsPerWindow,
                    DefaultCredentialWindow,
                    ProfileWritePartitionKeyPrefix));

            // Its own window, its own prefix and its own partition key: redemption is keyed by the ACCOUNT
            // as well as by the observable address, because every attempt is authenticated and neither key
            // alone bounds a determined guesser. See RedemptionPolicyName.
            options.AddPolicy(
                RedemptionPolicyName,
                context => BuildRedemptionPartition(
                    context,
                    RedemptionPermitsPerWindow,
                    DefaultCredentialWindow));

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(
                    context => ResolveWindowPartition(context, permitLimit, window)),
                PartitionedRateLimiter.Create<HttpContext, string>(ResolveConcurrencyPartition));
        });

        return services;
    }

    /// <summary>Reads the window size from configuration, falling back to the shipped defaults.</summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>The permit count and the window duration to apply.</returns>
    /// <exception cref="InvalidOperationException">A configured value is present but is zero or negative.</exception>
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

    /// <summary>Chooses the window partition for a request.</summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="permitLimit">
    /// The number of credential requests one caller may make per window, as resolved from configuration.
    /// </param>
    /// <param name="window">The length of that window.</param>
    /// <returns>
    /// A window partition keyed by the caller's observable address for a credential-bearing request; the
    /// shared unlimited partition otherwise.
    /// </returns>
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

        // SEC: THE GLOBAL LIMITER MUST AGREE WITH THE NAMED POLICY, OR THE NAMED POLICY IS COSMETIC. A
        // named policy does NOT replace this limiter - the framework applies BOTH - so an endpoint given a
        // budget of its own above is still charged HERE, and charging it under the default prefix would put
        // it straight back into the window it was just separated from.
        return BuildWindowPartition(context, permitLimit, window, ResolveCredentialPartitionPrefix(context));
    }

    /// <summary>Chooses which window an endpoint's requests are charged to.</summary>
    /// <remarks>
    /// Read from the endpoint's OWN rate-limit declaration rather than from a second list of routes kept
    /// here. That is what keeps this limiter and the named policy in step by construction: the action
    /// declares <see cref="RevocationPolicyName"/> once, and both the policy and this partition follow from
    /// that single statement.
    /// </remarks>
    /// <param name="context">The request being classified.</param>
    /// <returns>The partition key prefix, and therefore the budget, for this request.</returns>
    private static string ResolveCredentialPartitionPrefix(HttpContext context)
    {
        IReadOnlyList<EnableRateLimitingAttribute>? declared = context.GetEndpoint()?.Metadata
            .GetOrderedMetadata<EnableRateLimitingAttribute>();

        if (declared is not null)
        {
            foreach (EnableRateLimitingAttribute declaration in declared)
            {
                if (string.Equals(declaration.PolicyName, RevocationPolicyName, StringComparison.Ordinal))
                {
                    return RevocationPartitionKeyPrefix;
                }
            }
        }

        return ClientPartitionKeyPrefix;
    }

    /// <summary>Builds the window partition for a request, without asking whether it is credential-bearing.</summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="permitLimit">Permits per window.</param>
    /// <param name="window">Length of the window.</param>
    /// <param name="partitionKeyPrefix">The prefix that names which budget the partition belongs to.</param>
    /// <returns>A window partition keyed by the caller's observable address.</returns>
    private static RateLimitPartition<string> BuildWindowPartition(
        HttpContext context,
        int permitLimit,
        TimeSpan window,
        string partitionKeyPrefix = ClientPartitionKeyPrefix)
    {
        ArgumentNullException.ThrowIfNull(context);

        return RateLimitPartition.GetFixedWindowLimiter(
            ResolveClientPartitionKey(context, partitionKeyPrefix),
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
    /// Builds the redemption window's partition, keyed by the caller's account AND its observable address.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="permitLimit">Permits per window.</param>
    /// <param name="window">Length of the window.</param>
    /// <returns>A window partition keyed by both identities.</returns>
    /// <remarks>
    /// ⚠ THE ACCOUNT IS TAKEN FROM THE ROUTE, NOT FROM THE CALLER'S CLAIMS, AND THAT IS FORCED RATHER THAN
    /// PREFERRED. This limiter is deliberately registered BEFORE authentication, so that a flood is charged
    /// before any credential work is done on its behalf; at the moment a partition is chosen there is
    /// therefore no authenticated principal to read, and a claims-derived key would resolve to the same
    /// fall-back for every caller and collapse the account half of the partition entirely.
    /// </remarks>
    private static RateLimitPartition<string> BuildRedemptionPartition(
        HttpContext context,
        int permitLimit,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(context);

        string account = AuthorizationClaims.ReadRouteInt(context, AccountRouteKey) is { } userId
            ? userId.ToString(CultureInfo.InvariantCulture)
            : UnattributedAccountSuffix;

        string partitionKey = string.Concat(
            ResolveClientPartitionKey(context, RedemptionPartitionKeyPrefix),
            "|",
            account);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                // Does not queue, for the reason the credential window does not: holding a guessing attempt
                // open consumes exactly the resources the limiter is protecting.
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }

    /// <summary>Chooses the concurrency partition for a request.</summary>
    /// <param name="context">The request being partitioned.</param>
    /// <returns>
    /// The single process-wide concurrency partition for a credential-bearing request; the shared unlimited
    /// partition otherwise.
    /// </returns>
    /// <remarks>
    /// One partition for the whole process, not one per caller. The resource being protected - this
    /// process's processor time - is shared, so a per-caller concurrency bound would let the total grow
    /// with the number of callers, which is the opposite of a bound.
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

    /// <summary>Reports whether a request handles a credential.</summary>
    /// <param name="context">The request being classified.</param>
    /// <returns>
    /// <see langword="true"/> when the matched endpoint declares itself a credential endpoint, or when the
    /// method can carry a body and any whole path segment names a credential concern.
    /// </returns>
    /// <remarks>
    /// METADATA IS ASKED FIRST, AND ITS ANSWER IS FINAL WHEN AFFIRMATIVE. The rate-limiting middleware is
    /// ordered after routing, so the matched endpoint - and therefore <see
    /// cref="CredentialEndpointAttribute"/> - is available here.
    /// </remarks>
    private static bool IsCredentialBearing(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<CredentialEndpointAttribute>() is not null)
        {
            return true;
        }

        if (!IsCredentialMethod(context.Request.Method))
        {
            return false;
        }

        string? path = context.Request.Path.Value;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return ContainsCredentialSegment(path);
    }

    /// <summary>Reports whether a request method can carry a credential.</summary>
    /// <param name="method">The request method.</param>
    /// <returns>
    /// <see langword="true"/> when the method appears in <see cref="CredentialMethods"/>, compared
    /// case-insensitively.
    /// </returns>
    private static bool IsCredentialMethod(string method)
    {
        foreach (string credentialMethod in CredentialMethods)
        {
            if (string.Equals(method, credentialMethod, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reports whether any whole segment of a request path names a credential concern.</summary>
    /// <param name="path">The request path, known to be non-empty.</param>
    /// <returns>
    /// <see langword="true"/> when some segment equals an entry of <see cref="CredentialPathSegments"/>,
    /// compared case-insensitively.
    /// </returns>
    private static bool ContainsCredentialSegment(string path)
    {
        ReadOnlySpan<char> remaining = path.AsSpan();

        while (!remaining.IsEmpty)
        {
            int boundary = remaining.IndexOf('/');
            ReadOnlySpan<char> candidate = boundary < 0 ? remaining : remaining[..boundary];

            if (!candidate.IsEmpty && NamesCredentialConcern(candidate))
            {
                return true;
            }

            if (boundary < 0)
            {
                break;
            }

            remaining = remaining[(boundary + 1)..];
        }

        return false;
    }

    /// <summary>Reports whether one path segment names a credential concern.</summary>
    /// <param name="candidate">The segment, without separators.</param>
    /// <returns>
    /// <see langword="true"/> when the segment equals an entry of <see cref="CredentialPathSegments"/>,
    /// compared case-insensitively.
    /// </returns>
    private static bool NamesCredentialConcern(ReadOnlySpan<char> candidate)
    {
        foreach (string credentialSegment in CredentialPathSegments)
        {
            if (candidate.Equals(credentialSegment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the window partition key from the caller's observable address.</summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="partitionKeyPrefix">
    /// The prefix naming the budget the key belongs to, so that one address occupies one partition per
    /// budget rather than one partition shared across budgets.
    /// </param>
    /// <returns>A bounded, non-null partition key.</returns>
    /// <remarks>
    /// The key is derived from the address alone and never from anything the caller submits. A key drawn
    /// from a header or a body field would let a caller mint a fresh partition per request, which is a
    /// limiter that limits nothing; it would also make the number of live partitions unbounded, which is a
    /// memory leak wearing the costume of a security control.
    /// </remarks>
    private static string ResolveClientPartitionKey(
        HttpContext context,
        string partitionKeyPrefix = ClientPartitionKeyPrefix)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;

        if (address is null)
        {
            return string.Concat(partitionKeyPrefix, UnattributedClientSuffix);
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return string.Concat(partitionKeyPrefix, address.ToString());
    }

    /// <summary>Answers a refused request with a fixed problem-details payload.</summary>
    /// <param name="context">The refusal, carrying the request and the lease.</param>
    /// <param name="cancellationToken">
    /// The request's abort signal, observed while writing the payload so a caller who has already gone away
    /// is not written to.
    /// </param>
    /// <returns>A task that completes when the refusal has been written.</returns>
    /// <remarks>
    /// The retry hint is taken from the lease rather than computed, so it reflects the limiter that
    /// actually refused. A window limiter reports when its permits replenish; a concurrency limiter reports
    /// nothing, and no hint is invented in its place, because a wrong hint is worse than none.
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
