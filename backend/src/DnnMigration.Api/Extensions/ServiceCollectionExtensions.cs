using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Application.Serialization;
using DnnMigration.Application.Validation;
using Microsoft.AspNetCore.HttpOverrides;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Registers the services the API layer itself contributes, and binds and validates
/// every configuration section other than the JWT settings.
/// </summary>
/// <remarks>
/// <para>
/// This file and its pipeline counterpart are what keep <c>Program.cs</c> a
/// composition root rather than a registration dump. The JWT settings are bound
/// elsewhere, by the file the settings type itself names as the owner of its startup
/// guard; every other section is bound here.
/// </para>
/// <para>
/// <b>Every bound section is validated, and validation is scheduled for startup.</b>
/// A settings type declares the bounds its values must satisfy; the validators
/// nested below enforce them. Scheduling the check for startup is the part that
/// matters: options validation otherwise runs when a value is first read, which for a
/// setting used on one code path could be long after an orchestrator concluded the
/// container had started successfully. A configuration this application cannot
/// safely run under must stop the host, not the hundredth request.
/// </para>
/// <para>
/// <b>The validators are nested and private.</b> Each one is the enforcement half of a
/// policy declared on a settings type in the Application layer, and nothing outside
/// this file has any reason to name one. Nesting them beside the binding they guard
/// also means a new setting cannot be bound here without its bounds being in view.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Largest request body the API accepts: one mebibyte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's own default is thirty megabytes, which for a JSON administration
    /// API is thirty megabytes of attacker-chosen allocation on every unauthenticated
    /// endpoint. This API's largest legitimate request is a portal or user record of a
    /// few kilobytes, so a mebibyte is generous by three orders of magnitude and still
    /// bounds the work a caller can create before any validator has run.
    /// </para>
    /// <para>
    /// This is the outer half of a pair. It bounds the whole body; the ceiling on an
    /// individual credential field is applied by the request validators, because a
    /// body limit cannot make one field bounded - a caller can spend the entire
    /// allowance on a single password. Neither substitutes for the other.
    /// </para>
    /// <para>
    /// An endpoint that must accept more - a module import carrying a package, for
    /// instance - raises the limit for itself with the framework's per-action request
    /// size attribute rather than raising this value for everything.
    /// </para>
    /// </remarks>
    public const long MaximumRequestBodyBytes = 1L * 1024L * 1024L;

    /// <summary>
    /// Configuration key listing the addresses of reverse proxies whose forwarded
    /// headers may be trusted: <c>Proxy:KnownProxies</c>.
    /// </summary>
    public const string KnownProxiesSectionName = "Proxy:KnownProxies";

    /// <summary>
    /// Configuration key listing the networks, in prefix notation, whose forwarded
    /// headers may be trusted: <c>Proxy:KnownNetworks</c>.
    /// </summary>
    public const string KnownNetworksSectionName = "Proxy:KnownNetworks";

    /// <summary>
    /// Adds the API layer's own services and binds every configuration section it
    /// owns.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The order of the calls below is not arbitrary. The problem-details factory is
    /// registered before the controller services, because those services register the
    /// framework's own factory only if none is present; registering afterwards would
    /// leave the framework's factory in place and this application's error payload
    /// unused, with nothing to indicate it.
    /// </remarks>
    public static IServiceCollection AddApiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddValidatedOptions(services, configuration);
        AddRequestContextAccessors(services);
        AddErrorHandling(services);
        AddControllerServices(services);
        AddRequestLimits(services);
        AddForwardedHeaders(services, configuration);

        return services;
    }

    /// <summary>
    /// Binds the password-policy, portal and caching sections and schedules their
    /// validation for startup.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <remarks>
    /// The password-policy section is bound here rather than alongside the JWT
    /// settings even though both are security configuration, because three request
    /// validators resolve it and none of them is reachable from the authentication
    /// scheme. Binding it in one place means those validators cannot be activated
    /// against an unbound instance, which would silently apply the type's defaults
    /// instead of the deployment's configuration.
    /// </remarks>
    private static void AddValidatedOptions(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PasswordPolicyOptions>()
            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PasswordPolicyOptions>, PasswordPolicyOptionsValidator>();

        // The three password validators take a bound PasswordPolicyOptions directly, not
        // IOptions<PasswordPolicyOptions>: the Application project references FluentValidation
        // and nothing else, so the options abstraction is not available to it and a validator
        // cannot be written against it. Projecting the bound value here is what makes those
        // constructors satisfiable. Resolving it goes through IOptions, so the ValidateOnStart
        // registration above still runs and an invalid policy still fails loudly rather than
        // silently applying the type's defaults.
        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<PasswordPolicyOptions>>().Value);

        services
            .AddOptions<PortalOptions>()
            .Bind(configuration.GetSection(PortalOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PortalOptions>, PortalOptionsValidator>();

        // PermissionService takes a bound PortalOptions directly, for the same reason the
        // password validators do: the Application project references FluentValidation and
        // nothing else, so IOptions is not available to a constructor declared there. The
        // projection below is what makes that constructor satisfiable. Its absence is not a
        // compile error - the container discovers it only when the type is first activated,
        // and PermissionAuthorizationHandler is activated by the authorization middleware on
        // EVERY request, so a missing projection turns every route, including the anonymous
        // health endpoint, into a 500.
        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<PortalOptions>>().Value);

        services
            .AddOptions<CachingOptions>()
            .Bind(configuration.GetSection(CachingOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<CachingOptions>, CachingOptionsValidator>();

        // Four application services - portal, module, user and tab - take a bound
        // CachingOptions directly, so the same projection is required for them.
        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<CachingOptions>>().Value);
    }

    /// <summary>
    /// Registers the accessor for the current request and the projection of the
    /// authenticated caller's claims.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// Both belong to this layer because both describe a request. MIGRATION: the legacy
    /// equivalents were ambient statics - <c>HttpContext.Current.Items("PortalSettings")</c>
    /// at <c>PortalController.vb:L1209</c> and <c>UserController.GetCurrentUserInfo()</c> at
    /// <c>UserController.vb:L381</c> - reachable from anywhere, mutable in place, and
    /// impossible to substitute in a test. The caller is now injected, immutable, and
    /// resolved once per request.
    /// </para>
    /// <para>
    /// The claims projection is scoped rather than singleton because it reads the principal
    /// of the request being handled; capturing one in a singleton would pin the first
    /// caller's identity for the lifetime of the process. The tenant snapshot it sits
    /// alongside is registered by the persistence layer, which owns the repository the
    /// snapshot is resolved from.
    /// </para>
    /// <para>
    /// The context accessor is registered because the authorisation handler for
    /// <c>PermissionRequirement</c> reads route values to learn which module or page a
    /// permission is claimed against, and an authorisation handler is given a filter context
    /// rather than the request. It is the only component in this application that needs it.
    /// </para>
    /// </remarks>
    private static void AddRequestContextAccessors(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddScoped<ICurrentUser, CurrentUser>();
    }

    /// <summary>
    /// Registers the problem-details service, this application's problem-details
    /// factory, and the handler that turns an escaped exception into a problem
    /// document.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// The problem-details service is not optional decoration. Two pipeline stages -
    /// the tenant resolver and the rate limiter's refusal path - resolve it directly
    /// in order to write their own refusals, and neither can be activated without it.
    /// </para>
    /// <para>
    /// The exception handler is registered as the framework's handler abstraction
    /// rather than being invoked by hand. It reports whether it wrote the response, so
    /// a case it declines still reaches the framework's own handling instead of being
    /// swallowed.
    /// </para>
    /// </remarks>
    private static void AddErrorHandling(IServiceCollection services)
    {
        services.AddProblemDetails();

        services.AddSingleton<ProblemDetailsFactory, ValidationProblemDetailsFactory>();

        // The writer behind IProblemDetailsService serialises through the minimal-API options
        // object, not the controller formatters, so the enumeration policy has to be applied to
        // both. Without this a value carried in a problem-details extension member would be
        // spelled one way in a successful result and another in an error, and a client parsing
        // one contract has no reason to expect that. The policy is the same one the controller
        // formatters apply - the explicit per-type converters and no blanket enumeration
        // converter - so the two surfaces cannot drift apart.
        services.Configure<JsonOptions>(options =>
        {
            DnnJsonConverters.AddTo(options.SerializerOptions);
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
    }

    /// <summary>
    /// Registers the controller services and the validation filter.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// The validation filter is added globally rather than annotated on each action.
    /// A per-action annotation is a thing that can be forgotten, and forgetting it
    /// produces an endpoint whose declared validation rules are never consulted -
    /// which looks exactly like an endpoint that has none.
    /// </para>
    /// <para>
    /// The filter is added by type rather than as an instance, so it is activated per
    /// request from the container. It depends on the problem-details factory, and
    /// registering an instance would require constructing it here, before that factory
    /// exists.
    /// </para>
    /// </remarks>
    private static void AddControllerServices(IServiceCollection services)
    {
        services
            .AddControllers(options => options.Filters.Add<FluentValidationActionFilter>())
            .AddJsonOptions(options =>
            {
                // THE APPLICATION'S OWN ENUMERATION CONVERTERS ARE ADDED FIRST, AND THE ORDER
                // IS LOAD-BEARING. A converter is selected by walking this collection and
                // taking the first entry that reports it can handle the type, so a general
                // enumeration converter placed ahead of a specific one would claim every
                // enumeration and the specific one would never run.
                //
                // MIGRATION: BillingFrequency is the reason this matters. Its members carry the
                // legacy char(1) codes N, O, D, W, M and Y that dbo.Roles.BillingFrequency and
                // dbo.Roles.TrialFrequency store, so the wire form has to stay "M" and not
                // become "Month". A general converter writes the member NAME, which would put a
                // spelling on the wire that no legacy consumer and no stored value recognises.
                DnnJsonConverters.AddTo(options.JsonSerializerOptions);

                // NO BLANKET ENUMERATION CONVERTER IS REGISTERED, and that is a decision rather
                // than an omission. The remaining enumerations on the wire - the portal
                // registration and banner advertising modes and the module visibility - carry the
                // integer discriminator values their columns store, and the Angular models consume
                // them as numeric literal unions: MODULE_VISIBILITY is {maximized:0, minimized:1,
                // none:2}, USER_REGISTRATION_MODE is {none:0, private:1, public:2, verified:3} and
                // BANNER_ADVERTISING_MODE is {none:0, site:1, host:2}. Registering a general
                // string-enumeration converter would send "Maximized" to a client whose type
                // admits only 0, 1 or 2.
                //
                // Leaving it out also removes an ordering hazard. With a blanket converter present,
                // correctness would depend on it being registered AFTER the specific ones, because
                // selection walks this collection and takes the first entry that reports it can
                // handle the type - so reordering two lines would silently change the wire form.
                // One explicit converter per type has no such dependency, and it makes every
                // enumeration's wire form a decision taken here rather than one inherited by
                // default.

                // Null members are omitted. Absence in this API is carried by a member being
                // absent, never by a sentinel value, so writing nulls adds bytes without adding
                // information. MIGRATION: this is also what keeps the legacy sentinels out of the
                // wire contract - the mapping layer converts -1 and the empty string to absence,
                // and absence is then simply not written.
                options.JsonSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            });

        // The framework maps sixteen status codes to a problem-type link and a title, and 429 is
        // not one of them - it postdates the specification the defaults cite. Without an entry a
        // refusal from the credential rate limiter is published without a "type" member, so it is
        // the single response in the application whose shape differs from every other, which a
        // client parsing one contract has no reason to expect. The link is the section that
        // DEFINES the status code, exactly as the framework's own entries are, and it is
        // registered declaratively here rather than passed at the limiter's call site so that any
        // future producer of a 429 inherits it.
        services.Configure<ApiBehaviorOptions>(options =>
            options.ClientErrorMapping[StatusCodes.Status429TooManyRequests] = new ClientErrorData
            {
                Title = "Too Many Requests",
                Link = "https://tools.ietf.org/html/rfc6585#section-4",
            });
    }

    /// <summary>
    /// Bounds the size of a request body.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// Applied to the web server's own limits rather than through a filter, so it is
    /// enforced while the body is still being read off the socket. A limit applied
    /// after model binding would be applied after the allocation it exists to prevent.
    /// </remarks>
    private static void AddRequestLimits(IServiceCollection services)
    {
        services.Configure<KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes);
    }

    /// <summary>
    /// Configures which reverse proxies, if any, are trusted to report the original
    /// client's address and scheme.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// A configured proxy address or network cannot be parsed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Nothing is trusted unless a deployment names it.</b> With no configuration
    /// the framework's default trust - the loopback address only - is left in place,
    /// which in a container means forwarded headers are effectively ignored and the
    /// address this application observes is the proxy's own. That is the safe default
    /// and it is why the credential rate limiter is sized to remain sensible when the
    /// address it partitions on is shared.
    /// </para>
    /// <para>
    /// <b>Declaring a wider trust than a deployment operates is worse than declaring
    /// none.</b> A forwarded address is supplied by whoever made the request, so
    /// trusting one from a hop the deployment does not control lets a caller name its
    /// own address - and therefore choose its own rate-limit partition, defeating the
    /// window entirely. List the addresses of the proxies you run, and nothing else.
    /// </para>
    /// <para>
    /// Only the forwarded address and scheme are honoured. The forwarded host is not:
    /// this application resolves the tenant from the host name, so accepting a
    /// forwarded host would let a caller behind a trusted proxy select which tenant it
    /// is served, which is a cross-tenant escalation reachable from a header.
    /// </para>
    /// <para>
    /// The hop limit is one. Each entry consumed walks one step further back through a
    /// chain the deployment has to trust in full, and the shipped topology has exactly
    /// one proxy in front of this application.
    /// </para>
    /// </remarks>
    private static void AddForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        string[] knownProxies = configuration.GetSection(KnownProxiesSectionName).Get<string[]>() ?? [];
        string[] knownNetworks = configuration.GetSection(KnownNetworksSectionName).Get<string[]>() ?? [];

        IPAddress[] proxies = knownProxies.Select(ParseProxyAddress).ToArray();
        (IPAddress Prefix, int PrefixLength)[] networks = knownNetworks.Select(ParseProxyNetwork).ToArray();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            if (proxies.Length == 0 && networks.Length == 0)
            {
                return;
            }

            // Replaced rather than added to. The default trust list contains the
            // loopback address and network, which inside a container is this process
            // itself; leaving them in place alongside an explicit list would trust a
            // hop the deployment did not name.
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (IPAddress proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach ((IPAddress prefix, int prefixLength) in networks)
            {
                // Target-typed deliberately. Two distinct types spell "IP network" on
                // this framework - the runtime's and the middleware's - and the one
                // this collection holds is the middleware's business, not this file's.
                // Letting the compiler pick it from the parameter keeps the code
                // correct without this file having to name, or track, which is which.
                options.KnownNetworks.Add(new(prefix, prefixLength));
            }
        });
    }

    /// <summary>
    /// Parses one trusted proxy address.
    /// </summary>
    /// <param name="value">The configured address.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="InvalidOperationException">
    /// The value is not an address.
    /// </exception>
    private static IPAddress ParseProxyAddress(string value)
    {
        if (IPAddress.TryParse(value, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"The value '{value}' in '{KnownProxiesSectionName}' is not an IP address.");
    }

    /// <summary>
    /// Parses one trusted network in prefix notation.
    /// </summary>
    /// <param name="value">The configured network, for example <c>10.0.0.0/8</c>.</param>
    /// <returns>
    /// The network's base address and prefix length, both already checked for
    /// consistency with each other.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The value is not a network in prefix notation, or its prefix length is not
    /// valid for the address family.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A malformed entry stops the host rather than being skipped. A silently dropped
    /// network produces a deployment that starts, appears configured, and then
    /// partitions its rate limiter on the proxy's address - the exact failure the
    /// configuration was added to avoid, with nothing in the log to say so.
    /// </para>
    /// <para>
    /// The validated parts are returned rather than a constructed network, so that the
    /// caller constructs whichever network type the middleware's collection holds.
    /// </para>
    /// </remarks>
    private static (IPAddress Prefix, int PrefixLength) ParseProxyNetwork(string value)
    {
        string[] parts = value.Split('/', 2);

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out IPAddress? prefix)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefixLength))
        {
            throw new InvalidOperationException(
                $"The value '{value}' in '{KnownNetworksSectionName}' is not a network in prefix notation, such as '10.0.0.0/8'.");
        }

        int maximumPrefixLength = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;

        if (prefixLength < 0 || prefixLength > maximumPrefixLength)
        {
            throw new InvalidOperationException(
                $"The prefix length {prefixLength} in '{value}' from '{KnownNetworksSectionName}' is not valid for the address family; it must be between 0 and {maximumPrefixLength}.");
        }

        return (prefix, prefixLength);
    }

    /// <summary>
    /// Checks the bound password policy against the bounds
    /// <see cref="PasswordPolicyOptions"/> declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule here is either a <em>floor</em> that stops the legacy policy being
    /// weakened, or a check that a setting is <em>satisfiable</em>. None of them
    /// tightens the policy: the migration preserves it verbatim precisely so that
    /// users the legacy installation accepted are not locked out at the moment they
    /// are migrated.
    /// </para>
    /// <para>
    /// Two flags are required to be off, and the reason is worth stating plainly:
    /// nothing in this solution enforces either of them. A flag that a deployment can
    /// switch on, that changes no behaviour, and that reads as though it hardened
    /// something is worse than an absent flag - it is a control believed to be active
    /// while it is inert. Refusing the value at startup is the honest alternative.
    /// When an implementation for one of them lands, the rule for that flag is removed
    /// in the same change.
    /// </para>
    /// </remarks>
    private sealed class PasswordPolicyOptionsValidator : IValidateOptions<PasswordPolicyOptions>
    {
        /// <summary>
        /// Validates <paramref name="options"/>.
        /// </summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>
        /// A successful result, or a failure carrying one message per broken rule.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, PasswordPolicyOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];
            string section = PasswordPolicyOptions.SectionName;

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a
            // package-free Validate() stating the rules that belong to the contract itself, so that
            // the Application project can declare them without referencing the options package. That
            // method is the canonical statement of those rules; this validator is what WIRES it into
            // the host's ValidateOnStart, and then adds the deployment rules that only a host can
            // decide. Without this call the contract's own rules are declared and never enforced.
            //
            // A defect that both halves cover is reported twice, in each half's own wording. That is
            // deliberate: the report is a start-up abort listing every problem found, and naming the
            // same setting twice is not misleading, whereas dropping a rule to keep the list tidy is.
            failures.AddRange(options.Validate());

            if (options.MinRequiredPasswordLength < PasswordPolicyOptions.MinimumConfigurablePasswordLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredPasswordLength)}' is {options.MinRequiredPasswordLength}; it must be at least {PasswordPolicyOptions.MinimumConfigurablePasswordLength}, which is the measured legacy minimum. Lowering it would accept credentials the legacy installation rejected.");
            }

            if (options.MinRequiredPasswordLength > CredentialBounds.MaximumByteLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredPasswordLength)}' is {options.MinRequiredPasswordLength}, which exceeds the {CredentialBounds.MaximumByteLength}-byte ceiling every credential is bounded by. No password could satisfy the policy.");
            }

            if (options.MinRequiredNonAlphanumericCharacters < 0)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)}' is {options.MinRequiredNonAlphanumericCharacters}; a count cannot be negative.");
            }
            else if (options.MinRequiredNonAlphanumericCharacters > options.MinRequiredPasswordLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)}' is {options.MinRequiredNonAlphanumericCharacters}, which exceeds '{nameof(PasswordPolicyOptions.MinRequiredPasswordLength)}' of {options.MinRequiredPasswordLength}. No password could satisfy both.");
            }

            if (options.RequiresQuestionAndAnswer)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.RequiresQuestionAndAnswer)}' must be false. No endpoint in this application collects or verifies a security question, so a true value would be enforced nowhere while appearing to be a requirement.");
            }

            if (options.RequiresUniqueEmail)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.RequiresUniqueEmail)}' must be false. Nothing in this application checks an email address for uniqueness, so a true value would be enforced nowhere; the legacy installation permitted duplicates and the existing data may contain them.");
            }

            if (options.MaxInvalidPasswordAttempts <= 0)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MaxInvalidPasswordAttempts)}' is {options.MaxInvalidPasswordAttempts}; zero or less would lock every account on its first mistyped password. The measured legacy value is 5.");
            }

            if (options.PasswordAttemptWindowMinutes <= 0)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.PasswordAttemptWindowMinutes)}' is {options.PasswordAttemptWindowMinutes}; a window has to be a period. The measured legacy value is 10.");
            }

            ValidateStrengthPattern(options.PasswordStrengthRegularExpression, section, failures);

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        /// <summary>
        /// Checks the optional strength pattern.
        /// </summary>
        /// <param name="pattern">The configured pattern, possibly empty.</param>
        /// <param name="section">The section name, used in messages.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// <para>
        /// An empty pattern is the shipped value and means "no strength rule"; it is
        /// not checked, because there is nothing to check.
        /// </para>
        /// <para>
        /// A non-empty pattern is compiled here so that an invalid one stops the host.
        /// The alternative is a pattern that throws the first time a password is
        /// submitted, on an unauthenticated endpoint, from inside a validator - which
        /// turns a configuration typo into a broken sign-in path discovered by a user.
        /// </para>
        /// <para>
        /// The compiled instance is deliberately discarded. This is a validity check,
        /// not a cache: the validator that applies the pattern owns its compilation and
        /// its match timeout, and handing a pre-compiled instance across from here
        /// would split that ownership.
        /// </para>
        /// </remarks>
        private static void ValidateStrengthPattern(string pattern, string section, List<string> failures)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return;
            }

            if (pattern.Length > PasswordPolicyOptions.MaximumPasswordStrengthRegularExpressionLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.PasswordStrengthRegularExpression)}' is {pattern.Length} characters long; at most {PasswordPolicyOptions.MaximumPasswordStrengthRegularExpressionLength} are permitted.");
                return;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.PasswordStrengthRegularExpression)}' is not a valid regular expression: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Checks the bound portal settings against the bounds
    /// <see cref="PortalOptions"/> declares.
    /// </summary>
    /// <remarks>
    /// Two of these rules are security controls rather than tidiness. Both configured
    /// path values are combined with a directory this application owns, so neither may
    /// be rooted or reach upward out of it; and the home-directory format must keep
    /// its per-portal placeholder, because a format that has lost it expands to the
    /// same directory for every tenant.
    /// </remarks>
    private sealed class PortalOptionsValidator : IValidateOptions<PortalOptions>
    {
        /// <summary>
        /// Validates <paramref name="options"/>.
        /// </summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>
        /// A successful result, or a failure carrying one message per broken rule.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, PortalOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];
            string section = PortalOptions.SectionName;

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a
            // package-free Validate() stating the rules that belong to the contract itself, so that
            // the Application project can declare them without referencing the options package. That
            // method is the canonical statement of those rules; this validator is what WIRES it into
            // the host's ValidateOnStart, and then adds the deployment rules that only a host can
            // decide. Without this call the contract's own rules are declared and never enforced.
            //
            // A defect that both halves cover is reported twice, in each half's own wording. That is
            // deliberate: the report is a start-up abort listing every problem found, and naming the
            // same setting twice is not misleading, whereas dropping a rule to keep the list tidy is.
            failures.AddRange(options.Validate());

            ValidatePathValue(
                nameof(PortalOptions.AdminTemplateFileName),
                options.AdminTemplateFileName,
                requireBareFileName: true,
                section,
                failures);

            ValidatePathValue(
                nameof(PortalOptions.HomeDirectoryFormat),
                options.HomeDirectoryFormat,
                requireBareFileName: false,
                section,
                failures);

            if (!string.IsNullOrWhiteSpace(options.HomeDirectoryFormat)
                && !options.HomeDirectoryFormat.Contains(PortalOptions.HomeDirectoryPortalPlaceholder, StringComparison.Ordinal))
            {
                failures.Add(
                    $"'{section}:{nameof(PortalOptions.HomeDirectoryFormat)}' does not contain the placeholder '{PortalOptions.HomeDirectoryPortalPlaceholder}'. Without it every portal would resolve to the same directory and tenants would share their files.");
            }

            ValidateRoleName(nameof(PortalOptions.UnauthenticatedRoleName), options.UnauthenticatedRoleName, section, failures);
            ValidateRoleName(nameof(PortalOptions.AllUsersRoleName), options.AllUsersRoleName, section, failures);

            if (!string.IsNullOrWhiteSpace(options.UnauthenticatedRoleName)
                && string.Equals(options.UnauthenticatedRoleName, options.AllUsersRoleName, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"'{section}:{nameof(PortalOptions.UnauthenticatedRoleName)}' and '{section}:{nameof(PortalOptions.AllUsersRoleName)}' name the same role. They denote two different audiences - callers who have not signed in, and every caller - so conflating them would grant one the other's access.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        /// <summary>
        /// Checks a value that becomes part of a file-system path.
        /// </summary>
        /// <param name="key">The setting's name, used in messages.</param>
        /// <param name="value">The configured value.</param>
        /// <param name="requireBareFileName">
        /// <see langword="true"/> when the value must be a single path segment with no
        /// separator at all.
        /// </param>
        /// <param name="section">The section name, used in messages.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// A parent-directory segment and a rooted value are both rejected outright
        /// rather than normalised. Normalising means combining the value with a base
        /// directory and then reasoning about where the result landed, which is a
        /// comparison that is easy to get subtly wrong; refusing the shapes that make
        /// escape possible needs no such reasoning.
        /// </remarks>
        private static void ValidatePathValue(
            string key,
            string value,
            bool requireBareFileName,
            string section,
            List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"'{section}:{key}' must not be blank.");
                return;
            }

            if (value.Length > PortalOptions.MaximumPathValueLength)
            {
                failures.Add(
                    $"'{section}:{key}' is {value.Length} characters long; at most {PortalOptions.MaximumPathValueLength} are permitted.");
            }

            if (value.Contains(PortalOptions.ParentDirectorySegment, StringComparison.Ordinal))
            {
                failures.Add(
                    $"'{section}:{key}' contains '{PortalOptions.ParentDirectorySegment}'. The value is combined with a directory this application owns, so it must not be able to reach outside it.");
            }

            if (Path.IsPathRooted(value) || value.StartsWith('/') || value.StartsWith('\\'))
            {
                failures.Add(
                    $"'{section}:{key}' is an absolute path. The value is combined with a directory this application owns, so it must be relative to it.");
            }

            if (requireBareFileName && (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal)))
            {
                failures.Add(
                    $"'{section}:{key}' contains a directory separator; it must name a single file.");
            }
        }

        /// <summary>
        /// Checks a configured role name.
        /// </summary>
        /// <param name="key">The setting's name, used in messages.</param>
        /// <param name="value">The configured value.</param>
        /// <param name="section">The section name, used in messages.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        private static void ValidateRoleName(string key, string value, string section, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"'{section}:{key}' must not be blank.");
                return;
            }

            if (value.Length > PortalOptions.MaximumRoleNameLength)
            {
                failures.Add(
                    $"'{section}:{key}' is {value.Length} characters long; the role name column stores at most {PortalOptions.MaximumRoleNameLength}, so a longer value could not be matched against stored data.");
            }
        }
    }

    /// <summary>
    /// Checks the bound caching settings against the bounds
    /// <see cref="CachingOptions"/> declares.
    /// </summary>
    /// <remarks>
    /// Zero is accepted, and that is the interesting part: it is the legacy-sanctioned
    /// way to disable caching, so the floor is zero rather than one. A negative value
    /// is refused because it yields a negative expiry, which no cache entry can carry.
    /// </remarks>
    private sealed class CachingOptionsValidator : IValidateOptions<CachingOptions>
    {
        /// <summary>
        /// Validates <paramref name="options"/>.
        /// </summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure describing the problem.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, CachingOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.PerformanceMultiplier is >= CachingOptions.MinimumPerformanceMultiplier
                and <= CachingOptions.MaximumPerformanceMultiplier)
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(
                $"'{CachingOptions.SectionName}:{nameof(CachingOptions.PerformanceMultiplier)}' is {options.PerformanceMultiplier}; it must be between {CachingOptions.MinimumPerformanceMultiplier} and {CachingOptions.MaximumPerformanceMultiplier} inclusive. Zero is permitted and disables caching.");
        }
    }
}
