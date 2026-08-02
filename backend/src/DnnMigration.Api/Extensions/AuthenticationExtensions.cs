using System.Text;
using DnnMigration.Api.Authorization;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Binds and validates the JWT settings, configures bearer authentication from
/// them, and declares the authorisation policies the API's actions are gated on.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the ASP.NET 2.0 authentication arrangement wholesale. The
/// legacy application authenticated through Forms Authentication with a
/// machine-key-encrypted ticket, resolved identity through a membership provider
/// registered in configuration, and answered authorisation questions imperatively
/// inside each page. The ticket becomes a signed bearer token, the provider model
/// becomes an authentication handler, and the imperative page checks become the
/// declarative policies declared below.
/// </para>
/// <para>
/// <b>This file is the startup guard for the signing configuration.</b> That
/// ownership is asserted by <c>Application/Options/JwtOptions.cs</c> and honoured
/// here: the settings are bound to their configuration section, a validator
/// implementing the options-validation contract checks every value against the
/// bounds that type declares, and validation is scheduled to run at startup so a
/// deployment configured with a blank, placeholder or weak signing key stops the
/// host instead of signing tokens with it.
/// </para>
/// <para>
/// Nothing here reads a configuration value by string literal. Section names and
/// bounds both come from the options type, so a rename cannot leave a stale copy
/// behind in the composition root.
/// </para>
/// </remarks>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Permitted tolerance for clock difference between the signing host and the
    /// validating host: 30 seconds.
    /// </summary>
    /// <remarks>
    /// The library's default is five minutes, which silently extends every access
    /// token's usable life by that much and so undermines the whole point of a short
    /// lifetime. Thirty seconds absorbs ordinary clock drift between containers
    /// without materially widening the window in which a leaked token still works.
    /// </remarks>
    private static readonly TimeSpan PermittedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The one signature algorithm a presented token may carry.
    /// </summary>
    /// <remarks>
    /// Stated explicitly rather than left to the library's default set. Naming the
    /// algorithm is what makes an algorithm-substitution attempt fail on the
    /// algorithm rather than on the key: a token presented with a different
    /// algorithm - most importantly one asserting that it is unsigned - is rejected
    /// before any key material is consulted.
    /// </remarks>
    private static readonly string[] PermittedSignatureAlgorithms =
        [SecurityAlgorithms.HmacSha256];

    /// <summary>
    /// Binds the JWT settings, schedules their validation for startup, and
    /// configures bearer authentication from the validated values.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">
    /// The application's configuration, read for the section named by
    /// <see cref="JwtOptions.SectionName"/> only.
    /// </param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Validation is scheduled rather than performed inline, and the distinction
    /// matters. Options validation runs when the value is first resolved, which for
    /// a lazily used setting could be the first request that needs it - long after
    /// an orchestrator concluded the container had started. Asking for validation at
    /// startup moves the failure to the point where it stops the host.
    /// </para>
    /// <para>
    /// The bearer scheme's own settings are configured through a dependency on the
    /// bound settings rather than by reading configuration a second time. That is
    /// what guarantees the signing key the scheme uses is the validated one: if
    /// validation fails, resolving the settings throws, and the scheme is never
    /// configured at all.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>(ConfigureBearerScheme);

        return services;
    }

    /// <summary>
    /// Declares the authorisation policies named by
    /// <see cref="PolicyNames"/> and registers the handlers that decide them.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The tenant-scoped policy is built from its own requirement and never from
    /// the framework's role requirement.</b> A role requirement is satisfied by the
    /// presence of a role name in the caller's token, and a role name in this schema
    /// is not unique across tenants - the administrator role of one portal and the
    /// administrator role of another are two different rows that may carry the same
    /// name. Binding the policy to a role name would therefore let an administrator
    /// of any portal satisfy it while a different portal was being served, which is
    /// precisely the cross-tenant escalation the requirement and its handler exist to
    /// close. The prohibition is restated on the policy name itself.
    /// </para>
    /// <para>
    /// The tenant-scoped handler is registered <b>scoped</b>, not singleton. It
    /// depends on the per-request tenant holder and on a repository bound to the
    /// per-request database context; a singleton handler would capture the first
    /// request's tenant and answer every later request against it.
    /// </para>
    /// <para>
    /// <b>The four permission policies are declared, and they deny every request.</b>
    /// Each is built from the permission requirement its documentation names, and
    /// this solution contains no handler for that requirement, so no request can
    /// satisfy one. That is deliberate and it is the safe direction: an unsatisfiable
    /// policy produces a 403, whereas a policy that is merely absent produces an
    /// exception the moment an action referencing it is invoked. Declaring them keeps
    /// the catalogue honest - the names exist, they are wired to the right
    /// requirement, and the only thing missing is the decision.
    /// </para>
    /// <para>
    /// A fallback policy requires an authenticated caller for every endpoint that
    /// declares no authorisation metadata of its own. This inverts the usual failure
    /// mode: forgetting to protect an endpoint yields a 401 that is noticed
    /// immediately, rather than a silently public endpoint that is noticed by
    /// somebody else. The endpoints that must stay anonymous - the health endpoint
    /// above all, because a container orchestrator probes it before any credential
    /// exists - say so explicitly where they are mapped.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAuthorizationHandler, PortalAdministratorAuthorizationHandler>();

        // Both handlers are required, not alternatives. Every requirement a policy declares must have a
        // handler registered for it, and a requirement with no handler never succeeds - the framework
        // reports the policy as failed rather than as misconfigured, so the symptom is a 403 from an
        // endpoint whose caller genuinely holds the permission. The four permission policies below declare
        // PermissionRequirement, so the handler for it is registered here alongside the tenant one.
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyNames.PortalAdministrator, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(PortalAdministratorRequirement.Instance);
            });

            AddPermissionPolicy(options, PolicyNames.ModuleView, PermissionKey.VIEW, PermissionScope.Module);
            AddPermissionPolicy(options, PolicyNames.ModuleEdit, PermissionKey.EDIT, PermissionScope.Module);
            AddPermissionPolicy(options, PolicyNames.TabView, PermissionKey.VIEW, PermissionScope.Tab);
            AddPermissionPolicy(options, PolicyNames.TabEdit, PermissionKey.EDIT, PermissionScope.Tab);

            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    /// <summary>
    /// Declares one permission policy.
    /// </summary>
    /// <param name="options">The authorisation options being built.</param>
    /// <param name="policyName">The policy name from <see cref="PolicyNames"/>.</param>
    /// <param name="permission">The permission key the policy claims.</param>
    /// <param name="scope">The kind of item the key is claimed against.</param>
    private static void AddPermissionPolicy(
        AuthorizationOptions options,
        string policyName,
        PermissionKey permission,
        PermissionScope scope)
    {
        options.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new PermissionRequirement(permission, scope));
        });
    }

    /// <summary>
    /// Configures the bearer scheme from the validated JWT settings.
    /// </summary>
    /// <param name="bearer">The scheme's settings.</param>
    /// <param name="accessor">Accessor for the validated JWT settings.</param>
    /// <remarks>
    /// <para>
    /// Reading the settings here is what triggers their validation, so an invalid
    /// configuration surfaces as a failure to configure the scheme rather than as a
    /// scheme configured with an unsafe key.
    /// </para>
    /// <para>
    /// Every validation switch is set explicitly, including the ones whose library
    /// default is already correct. A default can change between library versions, and
    /// an accidentally disabled issuer, audience, signature or lifetime check is not
    /// visible in any test that presents a well-formed token.
    /// </para>
    /// <para>
    /// Inbound claim mapping is switched off, so the claims in the token keep the
    /// names the token gave them. The alternative rewrites short claim names into
    /// long legacy URIs, which leaves the claim names in code looking nothing like
    /// the claim names on the wire. The tenant-scoped authorisation handler reads the
    /// subject under the framework's name-identifier claim and falls back to the
    /// registered <c>sub</c> claim, so it finds the caller either way - and that
    /// fallback is exactly why switching the mapping off is safe.
    /// </para>
    /// <para>
    /// Error details are withheld from the challenge header. The front end triggers a
    /// token refresh on any unauthorised response and therefore needs no reason for
    /// it, while the reason itself is recorded by the framework's own authentication
    /// logging where an operator can read it and an anonymous caller cannot.
    /// </para>
    /// <para>
    /// The token is not retained on the authentication result. Keeping it would place
    /// a live credential into per-request state where any later component could read
    /// it or log it, for no benefit: nothing in this application needs to replay the
    /// caller's own token.
    /// </para>
    /// </remarks>
    private static void ConfigureBearerScheme(JwtBearerOptions bearer, IOptions<JwtOptions> accessor)
    {
        JwtOptions jwt = accessor.Value;

        bearer.MapInboundClaims = false;
        bearer.IncludeErrorDetails = false;
        bearer.SaveToken = false;

        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,

            ValidateAudience = true,
            ValidAudience = jwt.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),

            RequireSignedTokens = true,
            ValidAlgorithms = PermittedSignatureAlgorithms,

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = PermittedClockSkew,
        };
    }

    /// <summary>
    /// Checks the bound JWT settings against the bounds
    /// <see cref="JwtOptions"/> declares, and reports every problem it finds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Private and nested, because it is the enforcement half of a policy whose
    /// declaration lives on the settings type. Nothing outside this file has any
    /// reason to name it, and keeping it here is what makes the ownership statement on
    /// <see cref="JwtOptions"/> literally true.
    /// </para>
    /// <para>
    /// Failures are accumulated rather than reported one at a time, so an operator
    /// correcting a configuration sees every problem in a single startup attempt
    /// instead of discovering them one restart apart.
    /// </para>
    /// <para>
    /// <b>No message ever quotes the configured signing key</b>, not even a fragment
    /// of it. A startup failure is written to whatever log the host has, and a log is
    /// not a place a signing key belongs. Each message names the key that is wrong and
    /// the rule it broke, which is everything an operator needs and nothing an
    /// attacker can use.
    /// </para>
    /// </remarks>
    private sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
    {
        /// <summary>
        /// Values that must never be accepted as a signing key, whatever their
        /// length.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are the strings that appear in tutorials, templates and sample
        /// configurations. A key drawn from one of them is public knowledge, so its
        /// length is irrelevant: anyone can mint a token this API will accept.
        /// </para>
        /// <para>
        /// The list is held with the enforcement rather than on the settings type, so
        /// that adding a newly observed placeholder is a change to this file alone.
        /// </para>
        /// <para>
        /// Matching is by containment, case-insensitively, rather than by equality.
        /// Equality would be defeated by padding a placeholder out to the required
        /// length, which is the most likely way one reaches production. The cost of
        /// containment is that a randomly generated key could in principle contain one
        /// of these sequences by chance; for a key of the required length that
        /// probability is negligible, and the remedy - generate another one - takes
        /// seconds, whereas accepting a padded placeholder is a complete compromise of
        /// every token the deployment issues.
        /// </para>
        /// </remarks>
        private static readonly string[] ForbiddenSecretFragments =
        [
            "changeme",
            "change-me",
            "change_me",
            "replaceme",
            "replace-me",
            "your-secret",
            "your_secret",
            "yoursecret",
            "supersecret",
            "super-secret",
            "secretkey",
            "secret-key",
            "placeholder",
            "insert-secret",
            "samplesecret",
            "sample-secret",
            "examplesecret",
            "example-secret",
            "notasecret",
            "todo",
        ];

        /// <summary>
        /// Validates <paramref name="options"/>.
        /// </summary>
        /// <param name="name">
        /// The named options instance being validated, unused because this type has
        /// exactly one instance.
        /// </param>
        /// <param name="options">The bound settings.</param>
        /// <returns>
        /// A successful result, or a failure carrying one message per broken rule.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, JwtOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];

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

            ValidateSecret(options.Secret, failures);
            ValidateIdentifier(nameof(JwtOptions.Issuer), options.Issuer, failures);
            ValidateIdentifier(nameof(JwtOptions.Audience), options.Audience, failures);
            ValidateAccessLifetime(options.ExpirationMinutes, failures);
            ValidateRefreshLifetime(options.RefreshTokenExpirationDays, failures);

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        /// <summary>
        /// Checks the signing key for presence, length, variety and placeholder
        /// content.
        /// </summary>
        /// <param name="secret">The configured signing key.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// The blank case is reported on its own and the remaining checks are skipped,
        /// because reporting that an absent key is also too short and also lacks
        /// variety tells an operator nothing further.
        /// </remarks>
        private static void ValidateSecret(string secret, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                failures.Add(
                    $"'{JwtOptions.SectionName}:{nameof(JwtOptions.Secret)}' is not configured. Supply it per environment - for example as the environment variable 'Jwt__Secret' - from a generated cryptographic random value of at least {JwtOptions.MinimumSecretByteLength} bytes. This repository intentionally ships no signing key.");
                return;
            }

            int byteLength = Encoding.UTF8.GetByteCount(secret);

            if (byteLength < JwtOptions.MinimumSecretByteLength)
            {
                failures.Add(
                    $"'{JwtOptions.SectionName}:{nameof(JwtOptions.Secret)}' is {byteLength} UTF-8 bytes long; at least {JwtOptions.MinimumSecretByteLength} are required.");
            }

            int distinctCharacters = secret.Distinct().Count();

            if (distinctCharacters < JwtOptions.MinimumSecretDistinctCharacters)
            {
                failures.Add(
                    $"'{JwtOptions.SectionName}:{nameof(JwtOptions.Secret)}' contains only {distinctCharacters} distinct characters; at least {JwtOptions.MinimumSecretDistinctCharacters} are required. Generate the key from a cryptographic random source rather than composing it by hand.");
            }

            foreach (string fragment in ForbiddenSecretFragments)
            {
                if (secret.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"'{JwtOptions.SectionName}:{nameof(JwtOptions.Secret)}' contains a well-known placeholder sequence and is therefore public knowledge. Generate a fresh key from a cryptographic random source.");
                    break;
                }
            }
        }

        /// <summary>
        /// Checks an issuer or audience value.
        /// </summary>
        /// <param name="key">The setting's name, used in the message.</param>
        /// <param name="value">The configured value.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// <para>
        /// Leading and trailing whitespace is rejected rather than trimmed. Both values
        /// are compared character for character when a token is validated, so a stray
        /// space makes every token this API issues fail its own audience check - a
        /// failure that is invisible in the configuration file and mystifying at
        /// runtime.
        /// </para>
        /// <para>
        /// <b>The issuer and the audience are deliberately NOT required to differ.</b>
        /// A reviewer suggested requiring it; the requirement is declined, with the
        /// reason recorded rather than left implicit. This API is a
        /// backend-for-frontend with a single client, so it is the only audience for
        /// the tokens it issues, and the repository's own published configuration sets
        /// both values to the same string - which the migration plan adopts by
        /// reference. Enforcing distinctness would make the documented, sanctioned
        /// configuration fail at startup. Nothing is lost by allowing them to match:
        /// the audience check still binds a token to this API, which is the whole
        /// purpose of the claim, and the confusion a shared value can cause needs two
        /// different token types signed by one key - a situation this application does
        /// not have.
        /// </para>
        /// </remarks>
        private static void ValidateIdentifier(string key, string value, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"'{JwtOptions.SectionName}:{key}' must not be blank.");
                return;
            }

            if (value.Length > JwtOptions.MaximumIssuerOrAudienceLength)
            {
                failures.Add(
                    $"'{JwtOptions.SectionName}:{key}' is {value.Length} characters long; at most {JwtOptions.MaximumIssuerOrAudienceLength} are permitted.");
            }

            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                failures.Add(
                    $"'{JwtOptions.SectionName}:{key}' begins or ends with whitespace. The value is compared exactly when a token is validated, so the whitespace must be removed rather than tolerated.");
            }
        }

        /// <summary>
        /// Checks the access-token lifetime against its declared bounds.
        /// </summary>
        /// <param name="minutes">The configured lifetime in minutes.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        private static void ValidateAccessLifetime(int minutes, List<string> failures)
        {
            if (minutes is >= JwtOptions.MinimumExpirationMinutes and <= JwtOptions.MaximumExpirationMinutes)
            {
                return;
            }

            failures.Add(
                $"'{JwtOptions.SectionName}:{nameof(JwtOptions.ExpirationMinutes)}' is {minutes}; it must be between {JwtOptions.MinimumExpirationMinutes} and {JwtOptions.MaximumExpirationMinutes} minutes inclusive. A bearer token cannot be recalled once issued, so its lifetime is the whole of the exposure a leaked token creates.");
        }

        /// <summary>
        /// Checks the refresh-token lifetime against its declared bounds.
        /// </summary>
        /// <param name="days">The configured lifetime in days.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        private static void ValidateRefreshLifetime(int days, List<string> failures)
        {
            if (days is >= JwtOptions.MinimumRefreshTokenExpirationDays
                and <= JwtOptions.MaximumRefreshTokenExpirationDays)
            {
                return;
            }

            failures.Add(
                $"'{JwtOptions.SectionName}:{nameof(JwtOptions.RefreshTokenExpirationDays)}' is {days}; it must be between {JwtOptions.MinimumRefreshTokenExpirationDays} and {JwtOptions.MaximumRefreshTokenExpirationDays} days inclusive. This value fixes a refresh family's absolute deadline when the family is created, so it is the longest a stolen family can remain redeemable.");
        }
    }
}
