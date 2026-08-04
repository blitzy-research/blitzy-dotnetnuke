using DnnMigration.Api.Middleware;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Declares the single cross-origin policy this API publishes, restricted to the origins a
/// deployment names and to nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This class is the <b>only</b> place in the solution where a cross-origin policy is declared,
/// and what it declares is a <b>named</b> policy rather than a default one. A default policy is
/// applied by any stage that asks for cross-origin handling without naming a policy, which makes
/// it easy to enable by accident and hard to see where it took effect; a named policy has to be
/// asked for by <see cref="PolicyName"/>, so every use of it is greppable. A wildcard origin is
/// not merely discouraged here - it is refused at start-up, along with any other value that could
/// never match the origin header a browser sends. See <see cref="AddSpaCors"/>.
/// </para>
/// <para>
/// Registration only, deliberately. The pipeline stage that applies this policy to a request is
/// declared in <c>ApplicationBuilderExtensions.cs</c>, which names <see cref="PolicyName"/> at the
/// fifth of ten stages: after the routing stage, and ahead of the authentication and authorisation
/// stages. That position is not a matter of taste. The cross-origin stage reads the metadata of
/// the endpoint routing selected, so placed ahead of routing it has no endpoint to read and falls
/// back to its global behaviour. And a preflight request carries no credential of any kind, so an
/// authorisation stage placed first answers every preflight with 401 - which the browser then
/// reports as a cross-origin failure having nothing whatever to do with the policy declared here.
/// There is no static-file stage for it to precede: the reverse proxy serves the single-page
/// application, and this process serves JSON.
/// </para>
/// <para>
/// Credential support is never requested, and that is a design consequence rather than an
/// omission. This API authenticates with a bearer token that the client attaches to a request
/// header, so a browser holds no ambient credential that a cross-origin caller could ride on.
/// Because of that, the most dangerous cross-origin misconfiguration there is - a permissive
/// origin paired with credential support, which lets any site on the internet act as a signed-in
/// caller - cannot be reached from this file even by editing configuration: a wildcard entry is
/// refused at start-up and credential support is never asked for.
/// </para>
/// </remarks>
public static class CorsExtensions
{
    /// <summary>
    /// Name of the one policy declared here: <c>DnnMigrationSpa</c>.
    /// </summary>
    /// <remarks>
    /// Referenced by the pipeline stage declared in <c>ApplicationBuilderExtensions.cs</c>, so the
    /// policy is never applied through a bare string literal. Treat the value as part of this
    /// API: changing it detaches the declaration below from the stage that applies it, and nothing
    /// in a build reports that - the framework raises it when a request arrives.
    /// </remarks>
    public const string PolicyName = "DnnMigrationSpa";

    /// <summary>
    /// Configuration key holding the permitted origins: <c>Cors:AllowedOrigins</c>, an array of
    /// strings.
    /// </summary>
    /// <remarks>
    /// Private, and spelled once, so the key exists in exactly one place. It is overridable per
    /// deployment in the usual double-underscore environment form, whose first element is
    /// <c>Cors__AllowedOrigins__0</c> - which is how <c>docker/docker-compose.yml</c> supplies the
    /// front end's origin to a container that carries no configuration file of its own.
    /// </remarks>
    private const string AllowedOriginsSectionName = "Cors:AllowedOrigins";

    /// <summary>
    /// The correlation header, <c>X-Correlation-Id</c>: permitted on a request and exposed on a
    /// response.
    /// </summary>
    /// <remarks>
    /// Bound to <see cref="CorrelationIdMiddleware.HeaderName"/> rather than restated as a second
    /// literal. The two spellings have to agree exactly, or the header the middleware writes is
    /// not the header this policy exposes - and nothing in a build or a test run would say so,
    /// because the only symptom is a front end that reads no identifier. Binding the constant to
    /// the middleware's own makes that class of drift impossible to introduce.
    /// </remarks>
    private const string CorrelationIdHeaderName = CorrelationIdMiddleware.HeaderName;

    /// <summary>
    /// Scheme prefix an origin carries when transport security is terminated ahead of this
    /// process, as it is in the shipped topology and in local development.
    /// </summary>
    private const string HttpSchemePrefix = "http://";

    /// <summary>
    /// Scheme prefix an origin carries when the browser reaches a secured host directly.
    /// </summary>
    private const string HttpsSchemePrefix = "https://";

    /// <summary>
    /// Characters that may not appear after the scheme prefix, because an origin carries no path,
    /// query or fragment - and a trailing slash is a path.
    /// </summary>
    private static readonly char[] AuthorityTerminators = ['/', '?', '#'];

    /// <summary>
    /// Methods the policy permits.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than opened to any method. The set is exactly the verbs the controllers
    /// answer plus the preflight verb; no controller in this API declares PATCH, HEAD or a bespoke
    /// verb. A verb that appears in a request without appearing here therefore signals a route
    /// added without this list being revisited, rather than something to wave through.
    /// </remarks>
    private static readonly string[] PermittedMethods =
        ["GET", "POST", "PUT", "DELETE", "OPTIONS"];

    /// <summary>
    /// Request headers the policy permits.
    /// </summary>
    /// <remarks>
    /// Three headers and no more: the bearer token, the media type of a request body - which a
    /// browser does name on a preflight, because a JSON media type is not one of the values it
    /// treats as always safe - and the correlation identifier a caller may supply. The front end
    /// writes exactly these two custom headers, one per interceptor, and every other header a
    /// browser sends on a cross-origin request is either always permitted or not read here.
    /// </remarks>
    private static readonly string[] PermittedRequestHeaders =
        ["Authorization", "Content-Type", CorrelationIdHeaderName];

    /// <summary>
    /// Response headers a cross-origin caller is allowed to read.
    /// </summary>
    /// <remarks>
    /// Only the correlation identifier, and it is not optional. A browser hides every response
    /// header from script unless the response exposes it, so without this entry the client half of
    /// the correlation loop is blind: the interceptor that stamps an identifier on the way in
    /// cannot read the one the server answered with, and a caller reporting a failure has no
    /// identifier to quote - which is the entire reason the identifier is issued.
    /// </remarks>
    private static readonly string[] ExposedResponseHeaders = [CorrelationIdHeaderName];

    /// <summary>
    /// How long a browser may cache the answer to a preflight.
    /// </summary>
    /// <remarks>
    /// Ten minutes: long enough to keep a burst of calls from re-asking permission ahead of each
    /// one, short enough that narrowing the origin list takes effect promptly.
    /// </remarks>
    private static readonly TimeSpan PreflightLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Adds the named cross-origin policy, reading its permitted origins from configuration.
    /// </summary>
    /// <param name="services">The service collection being populated.</param>
    /// <param name="configuration">
    /// The application's configuration. Read for <c>Cors:AllowedOrigins</c> and for nothing else.
    /// </param>
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
    /// origin list. That distinction is the whole reason the empty case is handled here rather
    /// than skipped: a registered policy holding no origin refuses every cross-origin caller,
    /// whereas skipping registration would leave the pipeline stage asking for a policy that does
    /// not exist, which fails a request instead of refusing it.
    /// </para>
    /// <para>
    /// The empty case is a refusal and never a permission, and there is deliberately no built-in
    /// origin to fall back to. The shipped <c>appsettings.json</c> already names the one
    /// development origin, and the compose file overrides even that, so an empty list means a
    /// deployment cleared the key on purpose - and the correct answer to that is to permit
    /// nothing, not to reinstate a localhost origin the deployment did not ask for.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSpaCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // MIGRATION: the legacy application was same-origin by construction - one site served the
        // markup and the data together - so it needed no cross-origin policy and had none. This
        // file is net-new, and it exists only because the presentation layer moved into a
        // separately served single-page application.
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

        // MIGRATION: in the shipped container topology the supplied nginx configuration proxies
        // /api/ to this service, so the browser addresses this API through the very origin that
        // served the application and no preflight is ever issued. The list below therefore matters
        // in exactly one situation - the development server and this process on different ports of
        // one host - and otherwise stands as defence in depth.
        foreach (string? entry in configured)
        {
            // Trimmed, because an origin supplied through an environment variable arrives with
            // whatever spacing the surrounding tooling left behind, and the comparison the runtime
            // performs against an incoming origin header would fail on a stray space with no
            // indication of why.
            string origin = entry?.Trim() ?? string.Empty;

            // A blank entry is dropped rather than refused. It is what a deployment leaves behind
            // when it clears an origin it no longer serves; it grants nothing at all, so failing
            // start-up over one would refuse to run over a value that changes no behaviour.
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

            // Ordinal, because ordinal is how the runtime compares an incoming origin header
            // against this list. A repeated entry is dropped rather than refused: it is redundant,
            // not wrong, and a deployment assembling the list from several sources can produce one
            // without having made a mistake.
            if (!permitted.Contains(origin, StringComparer.Ordinal))
            {
                permitted.Add(origin);
            }
        }

        return [.. permitted];
    }

    /// <summary>
    /// Describes why an entry cannot be an origin, or reports that it can.
    /// </summary>
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
    /// browser's origin header ordinally, so that trailing slash matches nothing whatever. Both
    /// cases were measured on the pinned runtime before these rules were written.
    /// </para>
    /// <para>
    /// A wildcard is refused rather than honoured even though credential support is never
    /// requested. A wildcard already there invites the policy to be widened later by someone who
    /// adds credential support to it, and this API has no legitimate caller it cannot name.
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

        return null;
    }

    /// <summary>
    /// Populates the named policy from the checked origin list.
    /// </summary>
    /// <param name="policy">The policy being built.</param>
    /// <param name="origins">The checked origins, possibly empty.</param>
    /// <remarks>
    /// <para>
    /// The origin list is applied only when it holds something. Applying an empty one would be
    /// harmless but would read as though a permission had been granted; leaving it unset states
    /// plainly that no cross-origin caller is permitted, and the effect is identical - the stage
    /// writes no permission header, and the browser withholds the response from script.
    /// </para>
    /// <para>
    /// Credential support is never requested, which is a decision and not an oversight. See the
    /// class remarks: a bearer token on a request header is what authenticates a caller here, so
    /// there is no cookie for a cross-origin request to carry, and asking for credential support
    /// would narrow what the framework tolerates while adding a real hazard for no benefit
    /// whatever.
    /// </para>
    /// </remarks>
    private static void BuildPolicy(CorsPolicyBuilder policy, string[] origins)
    {
        if (origins.Length > 0)
        {
            policy.WithOrigins(origins);
        }

        // MIGRATION: the legacy application authenticated with a forms cookie named .DOTNETNUKE
        // (Website/release.config:L147), which a browser would have attached to a cross-origin
        // request only where credential support was granted. This API carries a bearer token on a
        // request header instead, which is why the permitted-header list names the authorization
        // header and why credential support is deliberately absent below.
        policy
            .WithMethods(PermittedMethods)
            .WithHeaders(PermittedRequestHeaders)
            .WithExposedHeaders(ExposedResponseHeaders)
            .SetPreflightMaxAge(PreflightLifetime);
    }
}
