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
/// MIGRATION: the credential store this scheme sits on top of changed shape, and the
/// change is deliberate rather than incidental. The legacy membership provider was
/// registered with <c>passwordFormat="Encrypted"</c> and
/// <c>enablePasswordRetrieval="true"</c> - <c>Website/release.config</c> L236-L246 -
/// so stored credentials were REVERSIBLE and could be read back in cleartext by
/// anything holding the machine key, which the repository itself committed. Credentials
/// are now one-way hashed, by the hasher the infrastructure layer owns, and password
/// RETRIEVAL is deliberately not carried forward at all: no endpoint, screen or
/// service in the target can return an existing credential, only replace one. The
/// legacy password POLICY is preserved verbatim, because tightening it during a
/// migration would lock out existing accounts.
/// </para>
/// <para>
/// Nothing in this file hashes, verifies or mints anything. It configures the scheme
/// that validates what the token service issued, and declares the policies the
/// authorisation handlers decide. The signing key is the one secret it touches, and it
/// only ever reads it - never logs it, never echoes it into a message, and never
/// supplies a default for it.
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
    /// MIGRATION: the legacy check really was by name -
    /// <c>Library/Components/Security/PortalSecurity.vb</c> L519,
    /// <c>IsInRole(PortalSettings.AdministratorRoleName.ToString)</c> - but it was safe
    /// there only because the name it compared came from the ambient per-request portal
    /// settings, so "the administrators of THIS portal" was implicit in the ambient
    /// state. That ambient state is gone by design, and a name lifted out of it is no
    /// longer a tenant-scoped question. The names collide in practice rather than in
    /// theory: every portal creates its own administrator role, all of them called the
    /// same thing - <c>Library/Components/Portal/PortalController.vb</c> L1390,
    /// <c>CreateRole(PortalId, "Administrators", "Portal Administrators", …)</c>. The
    /// requirement restores the scoping the ambient state used to provide, by resolving
    /// the tenant from the route and asking whether this caller administers THAT tenant.
    /// No configured or defaulted role-name string appears anywhere in this file, and
    /// none should be reintroduced.
    /// </para>
    /// <para>
    /// The tenant-scoped handler is registered <b>scoped</b>, not singleton. It
    /// depends on the per-request tenant holder and on a repository bound to the
    /// per-request database context; a singleton handler would capture the first
    /// request's tenant and answer every later request against it.
    /// </para>
    /// <para>
    /// <b>Every requirement any policy below declares has a handler registered here,
    /// and that is an invariant rather than an observation.</b> A requirement with no
    /// registered handler never succeeds, and the framework reports the outcome as a
    /// failed policy rather than as a misconfiguration - so the symptom of forgetting
    /// one is a 403 returned to a caller who genuinely holds the permission, with
    /// nothing in the response to say why. Four requirement types are declared below;
    /// four handlers are registered above. Adding a policy without its handler, or
    /// removing a handler whose requirement is still declared, breaks the invariant
    /// silently in that direction.
    /// </para>
    /// <para>
    /// <b>The permission policies split on the key they claim, and only the edit half
    /// demands an authenticated caller.</b> The reasoning is measured against the
    /// migrated data and is set out on <see cref="AddPermissionPolicy"/>; the short
    /// version is that grants to the "All Users" and "Unauthenticated Users"
    /// pseudo-roles are real rows that an anonymous caller must still be allowed to
    /// satisfy, and conjoining an authentication requirement would delete them without
    /// deleting the rows.
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
    /// <para>
    /// <b>That last sentence is load-bearing, so it is worth being precise about why the
    /// fallback cannot reach the health endpoint.</b> A fallback policy applies only
    /// where an endpoint carries no authorisation metadata at all; the health endpoint is
    /// mapped with an explicit anonymous marker, which is metadata, and an anonymous
    /// marker short-circuits authorisation regardless. Both halves have to hold, because
    /// an authenticated health endpoint does not fail visibly - the container's own probe
    /// never reports healthy, the front-end service waits on that condition forever, and
    /// the end-to-end check that curls the health address fails with both images built
    /// correctly. The integration suite asserts the anonymous reading directly rather
    /// than trusting this paragraph. <b>Neither the anonymous marker at the mapping site
    /// nor this fallback may be changed without re-checking the other.</b>
    /// </para>
    /// <para>
    /// <b>No default policy is set.</b> The framework's own default already requires an
    /// authenticated caller, every protected action names one of the policies below
    /// explicitly, and overriding the default would change the meaning of a bare
    /// authorisation attribute everywhere at once - including on endpoints nobody
    /// revisited.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The one place the two membership questions - "is this a host account" and "does this caller
        // administer the portal the route names" - are answered, shared by all three membership handlers so
        // that none of them can reach a different answer than the others. Scoped, because its repository
        // dependencies are scoped and its answers are only meaningful for one request.
        services.AddScoped<PortalAdministrationEvaluator>();

        // Three distinct questions, three handlers, ONE registration each. Tenant administration; then
        // installation-wide authority, which is a different question and is decided by its own handler for
        // the reasons set out on HostAdministratorRequirement rather than by folding the super-user test into
        // the tenant handler; then "may this caller act on the account the route addresses", which the
        // self-service half of the user resource depends on and which neither of the other two can express.
        // Registering any of them twice would run its handler twice per request for no additional decision.
        services.AddScoped<IAuthorizationHandler, PortalAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, HostAdministratorAuthorizationHandler>();

        // The account family stays on AccountOwnerRequirement, and the reason is a security property rather
        // than a preference. Two account policies are declared below and they differ in exactly one bit:
        // AccountOwner admits the account holder AND NOBODY ELSE, because a credential change proves
        // entitlement by presenting the current credential, while AccountOwnerOrPortalAdministrator also
        // admits an administrator of the addressed tenant. AccountOwnerRequirement supplies precisely those
        // two flavours. A single-flavour "owner or tenant administrator" requirement cannot express the
        // first one, so substituting it would admit administrators to the credential change - collapsing the
        // change and the administrative reset back into one operation whose effect depended on which fields
        // were populated, which is the shape that previously allowed a credential to be overwritten with no
        // proof of entitlement. Consolidating the account requirements is therefore only safe for whoever
        // also splits the replacement in two; it is not a rename.
        services.AddScoped<IAuthorizationHandler, AccountOwnerAuthorizationHandler>();

        // The fourth handler, and it is required alongside the three above rather than an alternative to
        // them. Every requirement a policy declares must have a handler registered for it, and a
        // requirement with no handler never succeeds - the framework reports the policy as failed rather
        // than as misconfigured, so the symptom is a 403 from an endpoint whose caller genuinely holds the
        // permission. The four permission policies below declare PermissionRequirement, which is a fourth
        // requirement type none of the membership handlers answers, so it gets its own handler here. Four
        // requirement types declared, four handlers registered: that count is the invariant to preserve.
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Gives the middleware's own refusals the RFC 7807 body they otherwise omit. Registered here
        // rather than beside the controller services because it is a property of the authorisation stage
        // and would be meaningless without it, and registered by TryAddSingleton's stricter cousin - a
        // plain replacement - because the framework registers its own default and exactly one handler may
        // win. A singleton is correct: it holds no per-request state and both of its dependencies are
        // themselves singletons.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyNames.PortalAdministrator, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(PortalAdministratorRequirement.Instance);
            });

            // For the operations that name no portal anywhere in their route. Kept separate from the policy
            // above rather than expressed as an extra arm of it, because the two answer different questions
            // and a single policy would have to guess which one an action meant from the shape of its route.
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

    /// <summary>
    /// Declares one permission policy.
    /// </summary>
    /// <param name="options">The authorisation options being built.</param>
    /// <param name="policyName">The policy name from <see cref="PolicyNames"/>.</param>
    /// <param name="permission">The permission key the policy claims.</param>
    /// <param name="scope">The kind of item the key is claimed against.</param>
    /// <param name="requireAuthenticatedUser">
    /// Whether the policy additionally demands an authenticated caller. Must be <see langword="false"/> for
    /// a policy claiming the view key and <see langword="true"/> for one claiming the edit key.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why authentication is not demanded for the view key.</b> Requirements inside one policy are ANDed,
    /// so adding <c>RequireAuthenticatedUser</c> to a permission policy makes an anonymous caller fail the
    /// policy before the permission handler is ever consulted. That silently deletes two grants the migrated
    /// data really holds and the evaluator really implements: role identifier -1 is the "All Users"
    /// pseudo-role, whose grants reach everybody, and -3 is "Unauthenticated Users", whose grants reach
    /// exactly the callers with no account at all. A grant to -3 that can never be evaluated is not a grant;
    /// it is a row that looks like one. <c>PermissionAuthorizationHandler</c> passes a null caller
    /// identifier straight through, and <c>PermissionService</c> resolves both pseudo-roles for it, so the
    /// anonymous case is answered correctly the moment the handler is allowed to answer it.
    /// </para>
    /// <para>
    /// <b>Why authentication IS demanded for the edit key.</b> Nothing in the legacy data grants edit to the
    /// unauthenticated pseudo-role, and an anonymous mutation has no account to attribute the change to, so
    /// requiring an identity is both faithful and necessary. The distinction is passed in rather than derived
    /// from the key inside this method so that a future policy cannot acquire the wrong default by omission.
    /// </para>
    /// <para>
    /// <b>The fallback policy is unaffected.</b> It applies only to endpoints that declare no authorisation
    /// metadata of their own, so an action carrying one of these policies is never additionally subjected to
    /// it. What an action must NOT do is carry a bare <c>[Authorize]</c> alongside a view policy - class
    /// level counts - because that reintroduces exactly the conjunction this parameter exists to remove.
    /// </para>
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
                // an anonymous-capable policy names no requirement of its own beyond the permission one. The
                // permission requirement below satisfies that, so nothing further is needed here; the branch
                // is stated explicitly so the asymmetry is visible rather than implied by an absence.
                policy.AuthenticationSchemes.Clear();
            }

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
    /// Because that mapping is off, the name and role claim types are named here
    /// explicitly and are pinned to the claims the issuing service writes rather than
    /// to the framework's defaults. This is a two-sided contract with no compile-time
    /// enforcement: a mismatch does not throw, it silently answers every role question
    /// in the negative. See the comments on those two members for which claim each side
    /// uses and why the default is wrong for one of them.
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

        // Stated explicitly even though nothing consults it under this configuration, because the setting
        // that is never written is the setting a library upgrade is free to redefine. It gates retrieval of
        // discovery metadata over plain HTTP, and metadata is only ever fetched when an authority address is
        // configured - which it deliberately is not, the signing key being symmetric and local. It is
        // therefore set to the safe value unconditionally rather than relaxed for development: there is no
        // metadata request for a development relaxation to enable, so an environment switch here would buy
        // nothing and would leave a weaker value in the file for somebody to promote by accident.
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

            // MIGRATION: the access-token lifetime is exact parity, not an estimate. The legacy
            // application expired its authentication ticket after sixty minutes -
            // Website/release.config:L147, '<forms name=".DOTNETNUKE" protection="All" timeout="60"
            // cookieless="UseCookies"/>' - and JwtOptions.ExpirationMinutes carries the same sixty, with
            // JwtOptions.MaximumExpirationMinutes refusing to let a deployment lengthen it. The lifetime is
            // stamped on the token by the issuing service; what this file contributes is the guarantee that
            // it is actually enforced on the way back in.
            //
            // MIGRATION: and it is the ONLY thing standing between a leaked access token and its holder.
            // The legacy sign-out cleared the ticket cookie server-side -
            // Library/Components/Security/PortalSecurity.vb:L79,
            // 'System.Web.Security.FormsAuthentication.SignOut()' - which a stateless bearer token has no
            // counterpart for: an issued token stays valid until it expires. Sign-out therefore revokes the
            // REFRESH token, in the application service that owns the refresh family, and discards the
            // access token on the client. That is why the lifetime above is short and why the skew below is
            // measured in seconds rather than left at the library's five minutes.
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = PermittedClockSkew,

            // The claim names are pinned to the ones the issuing service actually writes, because inbound
            // claim mapping is switched off above and so nothing rewrites them on arrival. Roles are minted
            // under the framework's own role claim, which is what makes a role requirement or IsInRole
            // resolve. The caller's display name arrives as the registered unique-name claim, NOT under the
            // framework's name claim - leaving this at its default would therefore leave the identity's Name
            // permanently null, which is invisible in every test that only inspects claims directly.
            //
            // Neither value may be changed without changing the issuer to match. A disagreement here is not
            // a compile error and not an exception; it silently turns every role test negative, which
            // presents as a caller who plainly holds a role being refused.
            NameClaimType = DnnClaimTypes.UniqueName,
            RoleClaimType = ClaimTypes.Role,
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
        /// Checks the sliding per-token refresh lifetime against its declared bounds.
        /// </summary>
        /// <param name="days">The configured lifetime in days.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// The SLIDING lifetime only. The absolute family ceiling has bounds of its own and they are
        /// enforced by <see cref="JwtOptions.Validate"/>, whose failures this validator already aggregates
        /// at the top of <c>Validate</c> - checking it here as well would report one misconfiguration
        /// twice. The ceiling is the more consequential of the two and must not be assumed covered by this
        /// method: rotation refreshes the sliding expiry at every exchange, so a caller that keeps
        /// exchanging never reaches the value checked here.
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
