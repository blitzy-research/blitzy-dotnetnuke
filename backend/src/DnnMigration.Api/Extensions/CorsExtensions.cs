using DnnMigration.Api.Middleware;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Declares the single cross-origin policy this API publishes, restricted to the
/// origins a deployment names.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy application needed no cross-origin policy at all. Its
/// pages, its user controls and its data all came from one origin, because the
/// server rendered the markup and the browser never addressed a second host. This
/// file exists purely because the presentation layer moved into a separately served
/// single-page application, and it is the narrowest thing that makes that split
/// work.
/// </para>
/// <para>
/// <b>The containerised topology needs no cross-origin permission whatsoever.</b>
/// <c>docker/nginx.conf</c> proxies <c>/api/</c> to the API container, so the
/// browser addresses this API through the very origin that served the application
/// and no preflight is ever issued. The policy therefore matters in exactly one
/// situation: local development, where the development server and the API listen on
/// different ports of <c>localhost</c> and are consequently different origins. That
/// is why the origin list defaults to <em>empty</em> rather than to anything
/// permissive - an unconfigured deployment permits no cross-origin caller, which is
/// correct for the topology this solution actually ships.
/// </para>
/// <para>
/// <b>Credentials are never enabled, and that is a deliberate design consequence
/// rather than an omission.</b> This API authenticates with a bearer token carried
/// in a request header, not with a cookie, so the browser has no ambient credential
/// to withhold or send. Because credentials are off, the single most dangerous CORS
/// misconfiguration - a wildcard origin combined with credential support, which
/// lets any site on the internet make authenticated requests on a signed-in user's
/// behalf - cannot be reached from this file even by editing the origin list, since
/// a wildcard entry is rejected outright at startup.
/// </para>
/// </remarks>
public static class CorsExtensions
{
    /// <summary>
    /// Name of the one policy declared here: <c>DnnMigrationSpa</c>.
    /// </summary>
    /// <remarks>
    /// A named policy rather than a default one. A default policy is applied by any
    /// call that asks for cross-origin handling without naming a policy, which makes
    /// it easy to enable accidentally and hard to see where it took effect; a named
    /// policy must be asked for by this constant, so every use of it is greppable.
    /// </remarks>
    public const string PolicyName = "DnnMigrationSpa";

    /// <summary>
    /// Configuration key holding the permitted origins:
    /// <c>Cors:AllowedOrigins</c>, an array.
    /// </summary>
    /// <remarks>
    /// Overridable per environment in the usual double-underscore form - the first
    /// entry is <c>Cors__AllowedOrigins__0</c> - which is how a container supplies
    /// the front end's origin without a configuration file.
    /// </remarks>
    public const string AllowedOriginsSectionName = "Cors:AllowedOrigins";

    /// <summary>
    /// Methods the policy permits.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than opened to any method. The set is exactly the verbs
    /// this API answers plus the preflight verb, so a method that appears in a
    /// future request without appearing here is a signal that a route was added
    /// without being thought about, not something to be waved through.
    /// </remarks>
    private static readonly string[] PermittedMethods =
        ["GET", "POST", "PUT", "DELETE", "OPTIONS"];

    /// <summary>
    /// Request headers the policy permits.
    /// </summary>
    /// <remarks>
    /// Three headers and no more: the bearer token, the media type of a request
    /// body, and the correlation identifier a caller may supply. Everything else a
    /// browser sends on a cross-origin request is either always permitted or not
    /// something this API reads.
    /// </remarks>
    private static readonly string[] PermittedRequestHeaders =
        ["Authorization", "Content-Type", CorrelationIdMiddleware.HeaderName];

    /// <summary>
    /// Response headers a cross-origin caller is allowed to read.
    /// </summary>
    /// <remarks>
    /// Only the correlation identifier. A browser hides every response header from
    /// script unless it is exposed, and this one has to be readable for the front
    /// end to quote the identifier a user reports back - which is the entire point of
    /// issuing it.
    /// </remarks>
    private static readonly string[] ExposedResponseHeaders =
        [CorrelationIdMiddleware.HeaderName];

    /// <summary>
    /// Adds the named cross-origin policy, reading its permitted origins from
    /// configuration.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">
    /// The application's configuration, read for <see cref="AllowedOriginsSectionName"/>
    /// only.
    /// </param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A configured origin is blank, is a wildcard, is not an absolute HTTP or HTTPS
    /// address, or carries a path, query or fragment.
    /// </exception>
    public static IServiceCollection AddSpaCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string[] origins = ReadPermittedOrigins(configuration);

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
            BuildPolicy(policy, origins)));

        return services;
    }

    /// <summary>
    /// Reads the permitted origins and refuses any value that cannot be a safe
    /// origin.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// The configured origins, which may be empty when none is configured.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// An entry is unusable, for one of the reasons named in the message.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Every rejection here is a startup failure rather than a silently dropped
    /// entry. An origin list that quietly discards a malformed entry produces the
    /// most confusing possible outcome: the application starts, the front end's
    /// requests are blocked by the browser, and nothing in the server's log
    /// mentions the configuration that caused it.
    /// </para>
    /// <para>
    /// A wildcard is rejected rather than honoured. Even without credential support
    /// a wildcard invites the policy to be widened later by someone who sees the
    /// wildcard already there and adds credentials to it, and this API has no
    /// legitimate caller it cannot name.
    /// </para>
    /// <para>
    /// A path, query or fragment is rejected because an origin has none. A value
    /// such as <c>https://example.test/app</c> matches nothing at all, so accepting
    /// it would look configured while permitting no caller.
    /// </para>
    /// </remarks>
    private static string[] ReadPermittedOrigins(IConfiguration configuration)
    {
        string[] configured = configuration
            .GetSection(AllowedOriginsSectionName)
            .Get<string[]>() ?? [];

        foreach (string origin in configured)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                throw new InvalidOperationException(
                    $"A blank entry appears in '{AllowedOriginsSectionName}'. Remove it, or supply the front end's origin such as 'https://app.example.test'.");
            }

            if (origin.Contains('*', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"A wildcard origin is not accepted in '{AllowedOriginsSectionName}'. Name each permitted origin in full.");
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"The origin '{origin}' in '{AllowedOriginsSectionName}' is not an absolute HTTP or HTTPS address.");
            }

            if (parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The origin '{origin}' in '{AllowedOriginsSectionName}' carries a path, query or fragment. An origin is a scheme, host and port only.");
            }
        }

        return configured;
    }

    /// <summary>
    /// Populates the policy from the checked origin list.
    /// </summary>
    /// <param name="policy">The policy being built.</param>
    /// <param name="origins">The checked origins, possibly empty.</param>
    /// <remarks>
    /// <para>
    /// When the list is empty the policy is left with no origin, which means the
    /// cross-origin middleware writes no permission header and the browser refuses
    /// the request. That is the correct outcome for a deployment that serves the
    /// front end through a reverse proxy on the same origin, where a cross-origin
    /// request should not be happening in the first place.
    /// </para>
    /// <para>
    /// A preflight lifetime is set so a browser does not re-ask permission before
    /// every request. Ten minutes is short enough that tightening the policy takes
    /// effect promptly and long enough to remove the round trip from a burst of
    /// calls.
    /// </para>
    /// </remarks>
    private static void BuildPolicy(CorsPolicyBuilder policy, string[] origins)
    {
        if (origins.Length > 0)
        {
            policy.WithOrigins(origins);
        }

        policy
            .WithMethods(PermittedMethods)
            .WithHeaders(PermittedRequestHeaders)
            .WithExposedHeaders(ExposedResponseHeaders)
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    }
}
