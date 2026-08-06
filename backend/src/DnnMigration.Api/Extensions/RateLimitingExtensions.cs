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
/// The removed guard is measured, not recalled. At
/// <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L162</c> the sign-in
/// handler opened with <c>If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha)
/// Then</c>, so no credential was checked until the challenge reported itself satisfied, and
/// the credential check itself followed on the next executable line, <c>:L164</c>. Its
/// registration is still visible in the legacy configuration, which binds a handler for
/// <c>*.captcha.aspx</c> twice - once per hosting mode. Nothing in the target stack answers
/// that address.
/// </para>
/// <para>
/// That same line is also why the window is keyed by caller address rather than by account.
/// The legacy call passed the caller's address as an argument on every attempt, alongside the
/// status it reported back, so bounding attempts per address preserves the identifier the
/// legacy system already tracked per attempt instead of inventing a new one. Keying by
/// submitted account name was rejected for a second reason: the caller chooses that value, so
/// it would let one caller mint a fresh budget per guess.
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
/// MIGRATION: state the residual limitation plainly rather than implying a per-client
/// budget that the shipped topology does not yet deliver. The proxy does forward the
/// originating address, and the pipeline does process forwarded headers - but a forwarded
/// address is only believed when the immediate hop is a declared trusted proxy, and the
/// shipped configuration trusts nothing beyond loopback. Until a deployment names its
/// proxies, every containerised request therefore partitions to the proxy's own address and
/// the window degrades from per-caller to one deployment-wide authentication throttle. That
/// degradation is strictly more restrictive, never less, so it cannot open a gap; it only
/// spends one shared budget where separate budgets were intended, which is why the permit
/// count is sized for the shared case.
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
    /// The policy name the caller-description read declares: <c>session-read</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEC: A SEPARATE BUDGET, AND THAT SEPARATION IS THE WHOLE POINT. The caller-description read
    /// (<c>GET /auth/me</c>) carries no credential, so it must stay bounded - an unbounded read is something
    /// a stolen token can be probed against indefinitely - but it must NOT share the budget that sign-in
    /// draws on. Sharing them is a denial of service in both directions: a client that polls its own
    /// description exhausts the window every other caller needs in order to SIGN IN, and a burst of
    /// credential guessing locks legitimate clients out of describing themselves. One controller-wide
    /// declaration produced exactly that coupling.
    /// </para>
    /// <para>
    /// The window size is the same, taken from the same configuration section, because the traffic it bounds
    /// is of the same order. What differs is the partition key prefix, which is what gives it a budget of its
    /// own; see <see cref="SessionReadPartitionKeyPrefix"/>.
    /// </para>
    /// </remarks>
    public const string SessionReadPolicyName = "session-read";

    /// <summary>
    /// Policy name for session revocation: <c>POST /auth/logout</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEC: REVOCATION MUST NOT BE STARVABLE BY SIGN-IN TRAFFIC. Withdrawing a refresh token is the one
    /// operation whose whole purpose is to shut a session down, so refusing it is not a neutral outcome -
    /// it leaves a token the caller has asked to have revoked live until it expires on its own. While it
    /// shared <see cref="AuthenticationPolicyName"/>, a burst of failed sign-in attempts spent the very
    /// window revocation needed, and because the window partitions on the caller's address, that burst
    /// did not even have to come from the same person: any peer sharing an address - everyone behind one
    /// NAT or one corporate egress - could exhaust it. Credential GUESSING could therefore suppress
    /// credential WITHDRAWAL, which inverts what the limiter is for.
    /// </para>
    /// <para>
    /// This is the same separation, for the same reason, that <see cref="SessionReadPolicyName"/> already
    /// makes for the caller-description read; that policy's remarks describe the coupling in full. As
    /// there, the window SIZE is taken from the same configuration section because the traffic is of the
    /// same order, and what makes the budget independent is the partition key prefix - see
    /// <see cref="RevocationPartitionKeyPrefix"/>.
    /// </para>
    /// <para>
    /// Revocation stays BOUNDED rather than becoming exempt. The endpoint is <c>AllowAnonymous</c> by
    /// design, so that a caller whose access token has already expired can still withdraw its refresh
    /// token, which means an unbounded revocation endpoint would be an unauthenticated one that anybody
    /// could hammer. It also keeps its <c>CredentialEndpoint</c> mark, so the concurrency bound and the
    /// body limit that protect this process's own processor and memory continue to apply: this changes
    /// WHICH budget revocation draws on, and nothing else about how it is protected.
    /// </para>
    /// </remarks>
    public const string RevocationPolicyName = "revocation";

    /// <summary>
    /// Policy name for profile replacements that evaluate tenant-authored regular expressions.
    /// </summary>
    public const string ProfileWritePolicyName = "profile-write";

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

    /// <summary>Profile replacements permitted per client per minute.</summary>
    private const int ProfileWritePermitsPerWindow = 20;

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
    /// Partition key suffix used when the remote address cannot be determined.
    /// </summary>
    /// <remarks>
    /// Requests with no observable address share one partition rather than being
    /// exempted. An unattributable request is the one most in need of a bound, and
    /// exempting it would turn a missing address into a way around the window. The
    /// suffix is combined with the caller's own prefix, so an unattributed credential
    /// request and an unattributed caller-description read remain separate budgets for
    /// the same reason attributed ones do.
    /// </remarks>
    private const string UnattributedClientSuffix = "unattributed";

    /// <summary>
    /// Prefix distinguishing window partitions from the other keys in play.
    /// </summary>
    private const string ClientPartitionKeyPrefix = "credential-window:";

    /// <summary>
    /// Prefix distinguishing the caller-description window's partitions from the credential window's.
    /// </summary>
    /// <remarks>
    /// Two partitions keyed by the same address but under different prefixes are two independent budgets,
    /// which is precisely what separates the caller-description read from sign-in. Changing this to match
    /// <see cref="ClientPartitionKeyPrefix"/> would silently re-merge them.
    /// </remarks>
    private const string SessionReadPartitionKeyPrefix = "session-read-window:";

    /// <summary>
    /// Prefix distinguishing the revocation window's partitions from the credential window's.
    /// </summary>
    /// <remarks>
    /// This constant IS the separation. Two partitions keyed by the same caller address but under
    /// different prefixes are two independent budgets, so sign-in traffic and revocation traffic can no
    /// longer exhaust one another. Changing this to match <see cref="ClientPartitionKeyPrefix"/> would
    /// silently re-merge them and restore the starvation described on
    /// <see cref="RevocationPolicyName"/> - with nothing failing to compile.
    /// </remarks>
    private const string RevocationPartitionKeyPrefix = "revocation-window:";

    /// <summary>Prefix separating profile-validation work from every credential/session budget.</summary>
    private const string ProfileWritePartitionKeyPrefix = "profile-write-window:";

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

            // Same window, separate partition prefix, therefore a separate budget. See
            // SessionReadPolicyName for why the caller-description read must be bounded without drawing on
            // the budget sign-in needs.
            options.AddPolicy(
                SessionReadPolicyName,
                context => BuildWindowPartition(context, permitLimit, window, SessionReadPartitionKeyPrefix));

            // Same window, separate partition prefix, therefore a separate budget. See
            // RevocationPolicyName for why withdrawing a refresh token must not be starvable by the
            // sign-in traffic it used to share a window with.
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

        // SEC: THE GLOBAL LIMITER MUST AGREE WITH THE NAMED POLICY, OR THE NAMED POLICY IS COSMETIC.
        // A named policy does NOT replace this limiter - the framework applies BOTH - so an endpoint
        // given a budget of its own above is still charged HERE, and charging it under the default
        // prefix would put it straight back into the window it was just separated from. Revocation is
        // the case that exposes this: unlike the caller-description read, which is a GET and so is
        // never credential-bearing, revocation is credential-bearing on two independent grounds - its
        // CredentialEndpoint mark and its path with a POST - so it reaches this line every time.
        return BuildWindowPartition(context, permitLimit, window, ResolveCredentialPartitionPrefix(context));
    }

    /// <summary>
    /// Chooses which window an endpoint's requests are charged to.
    /// </summary>
    /// <remarks>
    /// Read from the endpoint's OWN rate-limit declaration rather than from a second list of routes kept
    /// here. That is what keeps this limiter and the named policy in step by construction: the action
    /// declares <see cref="RevocationPolicyName"/> once, and both the policy and this partition follow
    /// from that single statement. A route list duplicated here could disagree with the attribute
    /// silently, and the symptom - a separated budget quietly re-merged - is invisible until something
    /// exhausts it.
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

    /// <summary>
    /// Builds the window partition for a request, without asking whether it is credential-bearing.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="permitLimit">Permits per window.</param>
    /// <param name="window">Length of the window.</param>
    /// <param name="partitionKeyPrefix">
    /// The prefix that names which budget the partition belongs to. Defaults to the credential window's
    /// prefix; the caller-description policy passes its own so that the two do not share a budget.
    /// </param>
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

    /// <summary>
    /// Reports whether a request method can carry a credential.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <returns>
    /// <see langword="true"/> when the method appears in <see cref="CredentialMethods"/>, compared
    /// case-insensitively.
    /// </returns>
    /// <remarks>
    /// The declared list is walked directly rather than through a sequence operator taking a comparer.
    /// That operator reaches the array through its interface, which materialises an enumerator on the
    /// heap, and this test runs on every request that reaches the application - so the convenient spelling
    /// bought one allocation per request to compare against three constants.
    /// </remarks>
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

    /// <summary>
    /// Reports whether any whole segment of a request path names a credential concern.
    /// </summary>
    /// <param name="path">The request path, known to be non-empty.</param>
    /// <returns>
    /// <see langword="true"/> when some segment equals an entry of
    /// <see cref="CredentialPathSegments"/>, compared case-insensitively.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The path is walked as spans over the original string and nothing is materialised: no segment
    /// list, no substring per segment, and no enumerator. That matters because this runs on every
    /// body-carrying request the application receives, including all the ones the limiter will not
    /// bound, so its cost is paid by traffic that derives no benefit from it.
    /// </para>
    /// <para>
    /// Segments are the maximal runs between separators and empty runs are skipped, so a leading,
    /// trailing or doubled separator contributes no segment and cannot be mistaken for one. Comparison
    /// is whole-segment, which is what keeps the word list from over-reaching onto a resource whose name
    /// merely begins with one of these words.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Reports whether one path segment names a credential concern.
    /// </summary>
    /// <param name="candidate">The segment, without separators.</param>
    /// <returns>
    /// <see langword="true"/> when the segment equals an entry of
    /// <see cref="CredentialPathSegments"/>, compared case-insensitively.
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

    /// <summary>
    /// Builds the window partition key from the caller's observable address.
    /// </summary>
    /// <param name="context">The request being partitioned.</param>
    /// <param name="partitionKeyPrefix">
    /// The prefix naming the budget the key belongs to, so that one address occupies one partition per
    /// budget rather than one partition shared across budgets.
    /// </param>
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
