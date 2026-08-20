using System.Security.Claims;
using System.Text;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Enums;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Binds and validates the JWT settings, configures bearer authentication from them, and declares the
/// authorisation policies the API's actions are gated on.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the ASP.NET 2.0 authentication arrangement wholesale. The legacy application authenticated
/// through Forms Authentication with a machine-key-encrypted ticket, resolved identity through a membership
/// provider registered in configuration, and answered authorisation questions imperatively inside each
/// page.
/// </para>
/// <para>
/// The credential store this scheme sits on top of changed shape, and the change is deliberate rather than
/// incidental.
/// </para>
/// </remarks>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Permitted tolerance for clock difference between the signing host and the validating host: 30
    /// seconds.
    /// </summary>
    /// <remarks>
    /// The library's default is five minutes, which silently extends every access token's usable life by
    /// that much and so undermines the whole point of a short lifetime. Thirty seconds absorbs ordinary
    /// clock drift between containers without materially widening the window in which a leaked token still
    /// works.
    /// </remarks>
    private static readonly TimeSpan PermittedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>The one signature algorithm a presented token may carry.</summary>
    /// <remarks>
    /// Stated explicitly rather than left to the library's default set. Naming the algorithm is what makes
    /// an algorithm-substitution attempt fail on the algorithm rather than on the key: a token presented
    /// with a different algorithm - most importantly one asserting that it is unsigned - is rejected before
    /// any key material is consulted.
    /// </remarks>
    private static readonly string[] PermittedSignatureAlgorithms =
        [SecurityAlgorithms.HmacSha256];

    /// <summary>
    /// Binds the JWT settings, schedules their validation for startup, and configures bearer authentication
    /// from the validated values.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">
    /// The application's configuration, read for the section named by <see cref="JwtOptions.SectionName"/>
    /// only.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Validation is scheduled rather than performed inline, and the distinction matters. Options
    /// validation runs when the value is first resolved, which for a lazily used setting could be the first
    /// request that needs it - long after an orchestrator concluded the container had started.
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
    /// Declares the authorisation policies named by <see cref="PolicyNames"/> and registers the handlers
    /// that decide them.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>The tenant-scoped policy is built from its own requirement and never from the framework's role
    /// requirement.</b> A role requirement is satisfied by the presence of a role name in the caller's
    /// token, and a role name in this schema is not unique across tenants - the administrator role of one
    /// portal and the administrator role of another are two different rows that may carry the same name.
    /// </remarks>
    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<PortalAdministrationEvaluator>();

        // Three distinct questions, three handlers, ONE registration each.
        services.AddScoped<IAuthorizationHandler, PortalAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, HostAdministratorAuthorizationHandler>();

        services.AddScoped<IAuthorizationHandler, AccountOwnerAuthorizationHandler>();

        // The fourth requirement handler, and it is required alongside the three above rather than an
        // alternative to them.
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddScoped<IAuthorizationHandler, RemediationAuthorizationHandler>();

        // Gives the middleware's own refusals the RFC 7807 body they otherwise omit.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyNames.PortalAdministrator, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(PortalAdministratorRequirement.Instance);
            });

            // For the operations that name no portal anywhere in their route.
            options.AddPolicy(PolicyNames.HostAdministrator, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(HostAdministratorRequirement.Instance);
            });

            // The account family. Two policies over one requirement type, differing in the single bit that
            // decides whether the portal's administrator is admitted alongside the account holder.
            options.AddPolicy(PolicyNames.AccountOwner, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(AccountOwnerRequirement.OwnerOnly);
            });

            options.AddPolicy(PolicyNames.AccountOwnerOrPortalAdministrator, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(AccountOwnerRequirement.OwnerOrPortalAdministrator);
            });

            // The two VIEW policies deliberately do NOT require an authenticated caller; the two EDIT
            // policies do. See AddPermissionPolicy for the measured reason.
            AddPermissionPolicy(
                options,
                PolicyNames.PortalContentEditor,
                PermissionKey.EDIT,
                PermissionScope.Portal,
                requireAuthenticatedUser: true);

            AddPermissionPolicy(
                options,
                PolicyNames.ModuleView,
                PermissionKey.VIEW,
                PermissionScope.Module,
                requireAuthenticatedUser: false);

            AddPermissionPolicy(
                options,
                PolicyNames.ModuleEdit,
                PermissionKey.EDIT,
                PermissionScope.Module,
                requireAuthenticatedUser: true);

            AddPermissionPolicy(
                options,
                PolicyNames.TabView,
                PermissionKey.VIEW,
                PermissionScope.Tab,
                requireAuthenticatedUser: false);

            AddPermissionPolicy(
                options,
                PolicyNames.TabEdit,
                PermissionKey.EDIT,
                PermissionScope.Tab,
                requireAuthenticatedUser: true);

            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    /// <summary>Declares one permission policy.</summary>
    /// <param name="options">The authorisation options being built.</param>
    /// <param name="policyName">The policy name from <see cref="PolicyNames"/>.</param>
    /// <param name="permission">The permission key the policy claims.</param>
    /// <param name="scope">The kind of item the key is claimed against.</param>
    /// <param name="requireAuthenticatedUser">
    /// Whether the policy additionally demands an authenticated caller.
    /// </param>
    /// <remarks>
    /// <b>Why authentication is not demanded for the view key.</b> Requirements inside one policy are
    /// ANDed, so adding <c>RequireAuthenticatedUser</c> to a permission policy makes an anonymous caller
    /// fail the policy before the permission handler is ever consulted.
    /// </remarks>
    private static void AddPermissionPolicy(
        AuthorizationOptions options,
        string policyName,
        PermissionKey permission,
        PermissionScope scope,
        bool requireAuthenticatedUser)
    {
        options.AddPolicy(policyName, policy =>
        {
            if (requireAuthenticatedUser)
            {
                policy.RequireAuthenticatedUser();
            }
            else
            {
                // A policy must name at least one authentication scheme or requirement to be buildable, and
                // an anonymous-capable policy names no requirement of its own beyond the permission one.
                policy.AuthenticationSchemes.Clear();
            }

            policy.AddRequirements(new PermissionRequirement(permission, scope));
        });
    }

    /// <summary>Configures the bearer scheme from the validated JWT settings.</summary>
    /// <param name="bearer">The scheme's settings.</param>
    /// <param name="accessor">Accessor for the validated JWT settings.</param>
    /// <remarks>
    /// Every validation switch is set explicitly, including the ones whose library default is already
    /// correct. A default can change between library versions, and an accidentally disabled issuer,
    /// audience, signature or lifetime check is not visible in any test that presents a well-formed token.
    /// </remarks>
    private static void ConfigureBearerScheme(JwtBearerOptions bearer, IOptions<JwtOptions> accessor)
    {
        JwtOptions jwt = accessor.Value;

        bearer.MapInboundClaims = false;
        bearer.IncludeErrorDetails = false;
        bearer.SaveToken = false;

        bearer.RequireHttpsMetadata = true;

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

            // The access-token lifetime is exact parity, not an estimate.
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = PermittedClockSkew,

            NameClaimType = DnnClaimTypes.Subject,
            RoleClaimType = ClaimTypes.Role,
        };
    }

    /// <summary>
    /// Checks the bound JWT settings against the bounds <see cref="JwtOptions"/> declares, and reports
    /// every problem it finds.
    /// </summary>
    /// <remarks>
    /// Private and nested, because it is the enforcement half of a policy whose declaration lives on the
    /// settings type. Nothing outside this file has any reason to name it, and keeping it here is what
    /// makes the ownership statement on <see cref="JwtOptions"/> literally true.
    /// </remarks>
    private sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
    {
        /// <summary>Values that must never be accepted as a signing key, whatever their length.</summary>
        /// <remarks>
        /// Matching is by containment, case-insensitively, rather than by equality. Equality would be
        /// defeated by padding a placeholder out to the required length, which is the most likely way one
        /// reaches production.
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

        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">
        /// The named options instance being validated, unused because this type has exactly one instance.
        /// </param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure carrying one message per broken rule.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public ValidateOptionsResult Validate(string? name, JwtOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a package-free
            // Validate() stating the rules that belong to the contract itself, so that the Application
            // project can declare them without referencing the options package.
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

        /// <summary>Checks the signing key for presence, length, variety and placeholder content.</summary>
        /// <param name="secret">The configured signing key.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
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

        /// <summary>Checks an issuer or audience value.</summary>
        /// <param name="key">The setting's name, used in the message.</param>
        /// <param name="value">The configured value.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// Leading and trailing whitespace is rejected rather than trimmed. Both values are compared
        /// character for character when a token is validated, so a stray space makes every token this API
        /// issues fail its own audience check - a failure that is invisible in the configuration file and
        /// mystifying at runtime.
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

        /// <summary>Checks the access-token lifetime against its declared bounds.</summary>
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

        /// <summary>Checks the sliding per-token refresh lifetime against its declared bounds.</summary>
        /// <param name="days">The configured lifetime in days.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// The SLIDING lifetime only. The absolute family ceiling has bounds of its own and they are
        /// enforced by <see cref="JwtOptions.Validate"/>, whose failures this validator already aggregates
        /// at the top of <c>Validate</c> - checking it here as well would report one misconfiguration
        /// twice.
        /// </remarks>
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
