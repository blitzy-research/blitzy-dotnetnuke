using DnnMigration.Api.Middleware;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Declares the single cross-origin policy this API publishes, restricted to the origins a
/// deployment names and to nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This class is the <b>only</b> place in the solution where a cross-origin policy is declared, and
/// what it declares is a <b>named</b> policy rather than a default one. A wildcard origin is not
/// merely discouraged here - it is refused at start-up, along with any other value that could never
/// match the origin header a browser sends.
/// </para>
/// <para>
/// Registration only, deliberately. The pipeline stage that applies this policy to a request is
/// declared in <c>ApplicationBuilderExtensions.cs</c>, which names <see cref="PolicyName"/> at the
/// fifth of ten stages: after the routing stage, and ahead of the authentication and authorisation
/// stages.
/// </para>
/// </remarks>
public static class CorsExtensions
{
    /// <summary>Name of the one policy declared here: <c>DnnMigrationSpa</c>.</summary>
    /// <remarks>
    /// Referenced by the pipeline stage declared in <c>ApplicationBuilderExtensions.cs</c>, so the
    /// policy is never applied through a bare string literal.
    /// </remarks>
    public const string PolicyName = "DnnMigrationSpa";

    /// <summary>
    /// Configuration key holding the permitted origins: <c>Cors:AllowedOrigins</c>, an array of
    /// strings.
    /// </summary>
    /// <remarks>Private, and spelled once, so the key exists in exactly one place.</remarks>
    private const string AllowedOriginsSectionName = "Cors:AllowedOrigins";

    /// <summary>
    /// The correlation header, <c>X-Correlation-Id</c>: permitted on a request and exposed on a
    /// response.
    /// </summary>
    /// <remarks>
    /// Bound to <see cref="CorrelationIdMiddleware.HeaderName"/> rather than restated as a second
    /// literal. The two spellings have to agree exactly, or the header the middleware writes is not
    /// the header this policy exposes - and nothing in a build or a test run would say so, because
    /// the only symptom is a front end that reads no identifier.
    /// </remarks>
    private const string CorrelationIdHeaderName = CorrelationIdMiddleware.HeaderName;

    /// <summary>
    /// Scheme prefix an origin carries when transport security is terminated ahead of this process,
    /// as it is in the shipped topology and in local development.
    /// </summary>
    private const string HttpSchemePrefix = "http://";

    private const string HttpsSchemePrefix = "https://";

    /// <summary>
    /// Characters that may not appear after the scheme prefix, because an origin carries no path,
    /// query or fragment - and a trailing slash is a path.
    /// </summary>
    private static readonly char[] AuthorityTerminators = ['/', '?', '#'];

    /// <summary>Methods the policy permits.</summary>
    /// <remarks>Enumerated rather than opened to any method.</remarks>
    private static readonly string[] PermittedMethods =
        ["GET", "POST", "PUT", "DELETE", "OPTIONS"];

    /// <summary>Request headers the policy permits.</summary>
    /// <remarks>
    /// Three headers and no more: the bearer token, the media type of a request body - which a
    /// browser does name on a preflight, because a JSON media type is not one of the values it
    /// treats as always safe - and the correlation identifier a caller may supply.
    /// </remarks>
    private static readonly string[] PermittedRequestHeaders =
        ["Authorization", "Content-Type", CorrelationIdHeaderName];

    /// <summary>Response headers a cross-origin caller is allowed to read.</summary>
    /// <remarks>Only the correlation identifier, and it is not optional.</remarks>
    private static readonly string[] ExposedResponseHeaders = [CorrelationIdHeaderName];

    /// <summary>How long a browser may cache the answer to a preflight.</summary>
    /// <remarks>
    /// Ten minutes: long enough to keep a burst of calls from re-asking permission ahead of each
    /// one, short enough that narrowing the origin list takes effect promptly.
    /// </remarks>
    private static readonly TimeSpan PreflightLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Adds the named cross-origin policy, reading its permitted origins from configuration.
    /// </summary>
    /// <param name="services">The service collection being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so registrations can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A configured entry cannot be an origin: it carries a wildcard, an upper-case character, a
    /// scheme other than HTTP or HTTPS, a path, a query, a fragment or a trailing slash, or it is
    /// not a well-formed absolute address.
    /// </exception>
    /// <remarks>
    /// <para>
    /// When configuration names no usable origin, the policy is still registered - with an empty
    /// origin list.
    /// </para>
    /// <para>
    /// The empty case is a refusal and never a permission, and there is deliberately no built-in
    /// origin to fall back to.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSpaCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // MIGRATION: the legacy application was same-origin by construction - one site served the markup and
        // the data together - so it needed no cross-origin policy and had none. This file is net-new, and it
        // exists only because the presentation layer moved into a separately served single-page application.
        string[] origins = ReadPermittedOrigins(configuration);

        services.AddCors(options => options.AddPolicy(
            PolicyName,
            policy => BuildPolicy(policy, origins)));

        return services;
    }

    /// <summary>
    /// Reads the permitted origins, drops what is blank, and refuses what could not work.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// The permitted origins, de-duplicated and in the order configured, which may be empty when a
    /// deployment names none.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// An entry cannot be an origin, for one of the reasons named in the message.
    /// </exception>
    private static string[] ReadPermittedOrigins(IConfiguration configuration)
    {
        string[] configured = configuration
            .GetSection(AllowedOriginsSectionName)
            .Get<string[]>() ?? [];

        List<string> permitted = new(configured.Length);

        // MIGRATION: in the shipped container topology the supplied nginx configuration proxies /api/ to
        // this service, so the browser addresses this API through the very origin that served the
        // application and no preflight is ever issued. The list below therefore matters in exactly one
        // situation - the development server and this process on different ports of one host - and otherwise
        // stands as defence in depth.
        foreach (string? entry in configured)
        {
            // Trimmed, because an origin supplied through an environment variable arrives with whatever
            // spacing the surrounding tooling left behind, and the comparison the runtime performs against
            // an incoming origin header would fail on a stray space with no indication of why.
            string origin = entry?.Trim() ?? string.Empty;

            // A blank entry is dropped rather than refused. It is what a deployment leaves behind when it
            // clears an origin it no longer serves; it grants nothing at all, so failing start-up over one
            // would refuse to run over a value that changes no behaviour.
            if (origin.Length == 0)
            {
                continue;
            }

            string? rejection = DescribeRejection(origin);

            if (rejection is not null)
            {
                throw new InvalidOperationException(
                    $"'{origin}' cannot be an entry of '{AllowedOriginsSectionName}': {rejection} An " +
                    "origin is a scheme, a host and an optional port, spelled exactly as a browser " +
                    "sends it and carrying no trailing slash - for example 'http://localhost:4200'.");
            }

            // Ordinal, because ordinal is how the runtime compares an incoming origin header against this
            // list. A repeated entry is dropped rather than refused: it is redundant, not wrong, and a
            // deployment assembling the list from several sources can produce one without having made a
            // mistake.
            if (!permitted.Contains(origin, StringComparer.Ordinal))
            {
                permitted.Add(origin);
            }
        }

        return [.. permitted];
    }

    /// <summary>Describes why an entry cannot be an origin, or reports that it can.</summary>
    /// <param name="origin">A trimmed, non-empty configured entry.</param>
    /// <returns>
    /// A sentence naming the defect, or <see langword="null"/> when the entry is usable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every defect here becomes a start-up failure rather than a silently discarded entry, because
    /// discarding produces the most confusing result available: the application starts, the front
    /// end's requests are refused by the browser, and nothing in this process's log mentions the
    /// configuration that caused it.
    /// </para>
    /// <para>
    /// The checks are deliberately string-shaped. Parsing the value and inspecting the parsed parts
    /// looks stricter and is in fact weaker: a bare host with a port parses happily as an absolute
    /// address whose scheme is the host name, and a value carrying a trailing slash parses to the
    /// same parts as one without it - yet the runtime compares the configured text against the
    /// browser's origin header ordinally, so that trailing slash matches nothing whatever.
    /// </para>
    /// </remarks>
    private static string? DescribeRejection(string origin)
    {
        if (origin.Contains('*', StringComparison.Ordinal))
        {
            return "it carries a wildcard, and this API has no legitimate caller it cannot name.";
        }

        if (origin.Any(char.IsUpper))
        {
            return "it carries an upper-case character, and a browser lower-cases the scheme and "
                + "host it sends, so the comparison could never match.";
        }

        if (!origin.StartsWith(HttpSchemePrefix, StringComparison.Ordinal)
            && !origin.StartsWith(HttpsSchemePrefix, StringComparison.Ordinal))
        {
            return $"it does not begin with '{HttpSchemePrefix}' or '{HttpsSchemePrefix}'.";
        }

        int authorityStart = origin.StartsWith(HttpsSchemePrefix, StringComparison.Ordinal)
            ? HttpsSchemePrefix.Length
            : HttpSchemePrefix.Length;

        string authority = origin[authorityStart..];

        if (authority.Length == 0)
        {
            return "it names no host.";
        }

        if (authority.IndexOfAny(AuthorityTerminators) >= 0)
        {
            return "it carries a path, a query or a fragment, and a trailing slash counts as a path.";
        }

        if (!Uri.IsWellFormedUriString(origin, UriKind.Absolute))
        {
            return "it is not a well-formed absolute address.";
        }

        // The last check, and the only one about WHICH host rather than about the value's shape. A
        // documentation name or an unreplaced editing marker is well-formed, lower-case, scheme-prefixed and
        // path-free - it passes every check above - and it is still certainly wrong: no browser can ever
        // send it as an origin, so the policy it configures would admit nothing while looking configured.
        // The TLS overlay derives this value and the API's host filter from ONE deployment setting, so the
        // same illustration reaching both is exactly the mistake worth failing on.
        string? reservation = ReservedDeploymentHosts.DescribeRejection(origin[authorityStart..]);

        if (reservation is not null)
        {
            return reservation;
        }

        return null;
    }

    /// <summary>Populates the named policy from the checked origin list.</summary>
    /// <param name="policy">The policy being built.</param>
    /// <param name="origins">The checked origins, possibly empty.</param>
    /// <remarks>
    /// <para>
    /// The origin list is applied only when it holds something.
    /// </para>
    /// <para>
    /// Credential support is never requested, which is a decision and not an oversight.
    /// </para>
    /// </remarks>
    private static void BuildPolicy(CorsPolicyBuilder policy, string[] origins)
    {
        if (origins.Length > 0)
        {
            policy.WithOrigins(origins);
        }

        // MIGRATION: the legacy application authenticated with a forms cookie named.DOTNETNUKE
        // (Website/release.config), which a browser would have attached to a cross-origin request only where
        // credential support was granted. This API carries a bearer token on a request header instead, which
        // is why the permitted-header list names the authorization header and why credential support is
        // deliberately absent below.
        policy
            .WithMethods(PermittedMethods)
            .WithHeaders(PermittedRequestHeaders)
            .WithExposedHeaders(ExposedResponseHeaders)
            .SetPreflightMaxAge(PreflightLifetime);
    }
}
