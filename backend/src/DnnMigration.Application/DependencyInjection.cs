using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DnnMigration.Application;

/// <summary>
/// Registers everything this layer contributes to the application's service container, behind the single
/// entry point <see cref="AddApplication"/>.
/// </summary>
/// <remarks>
/// <para>
/// One extension per layer, called once from the composition root, is what keeps <c>Api/Program.cs</c> a
/// composition root rather than a registration dump. The companion for the persistence and security layer
/// is <c>Infrastructure/DependencyInjection.cs</c>.
/// </para>
/// <para>
/// <b>No configuration is read here, and none is bound here.</b> This layer declares its settings types
/// under <c>Options/</c> and the bounds those settings must satisfy, but the binding of a configuration
/// section onto an instance, and the startup validation that refuses an unsafe value, both belong to the
/// composition root: that is the only place a rejected configuration can still stop the host before it
/// serves a request.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Adds this layer's application services and request validators to <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The lifetime is stated explicitly even though it matches the scanner's own default, because it is
    /// load-bearing. A validator is resolved once per request and may depend on request-scoped services, so
    /// capturing one in a singleton - a controller field, a static cache, a captured closure - would pin
    /// the first request's dependencies for the lifetime of the process.
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // These registrations replace the legacy reflection-based service location.

        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<IModuleService, ModuleService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<ITabService, TabService>();
        services.AddScoped<IAuthService, AuthService>();

        services.AddValidatorsFromAssemblyContaining(
            typeof(DependencyInjection),
            lifetime: ServiceLifetime.Scoped,
            includeInternalTypes: false);

        return services;
    }
}
