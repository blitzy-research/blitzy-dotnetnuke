using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Services;
using DnnMigration.Application.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DnnMigration.Application;

/// <summary>
/// Registers everything this layer contributes to the application's service
/// container, behind the single entry point <see cref="AddApplication"/>.
/// </summary>
/// <remarks>
/// <para>
/// One extension per layer, called once from the composition root, is what keeps
/// <c>Api/Program.cs</c> a composition root rather than a registration dump. The
/// companion for the persistence and security layer is
/// <c>Infrastructure/DependencyInjection.cs</c>.
/// </para>
/// <para>
/// The namespace is this layer's own rather than
/// <c>Microsoft.Extensions.DependencyInjection</c>. Placing an extension method in
/// the framework's namespace makes it appear on <c>IServiceCollection</c> without
/// any using directive naming this assembly, which reads as though the method
/// were part of the framework. An explicit <c>using DnnMigration.Application;</c>
/// in the composition root states plainly where the registrations come from.
/// </para>
/// <para>
/// <b>No configuration is read here, and none is bound here.</b> This layer
/// declares its settings types under <c>Options/</c> and the bounds those settings
/// must satisfy, but the binding of a configuration section onto an instance, and
/// the startup validation that refuses an unsafe value, both belong to the
/// composition root: that is the only place a rejected configuration can still
/// stop the host before it serves a request. It is also why this method takes no
/// configuration argument - accepting one would require this project to reference
/// the configuration abstractions, widening a package surface that currently
/// closes over the dependency-injection abstractions, options and primitives
/// alone.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Adds this layer's application services and request validators to
    /// <paramref name="services"/>.
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
    /// The seven application services are listed one by one rather than scanned. There are
    /// exactly seven, one per aggregate, and the list is short enough that an omission shows up
    /// as a controller that cannot be activated at start-up - a loud failure. Validators are the
    /// opposite case, as the next paragraph explains.
    /// </para>
    /// <para>
    /// Validators are discovered by scanning this assembly rather than listed one
    /// by one. A list is a second place the truth lives, and the failure mode of
    /// forgetting an entry is silent: an unregistered validator is simply never
    /// resolved, so its rules never run and the endpoint accepts input nobody
    /// checked. Scanning cannot forget.
    /// </para>
    /// <para>
    /// The lifetime is stated explicitly even though it matches the scanner's own
    /// default, because it is load-bearing. A validator is resolved once per
    /// request and may depend on request-scoped services, so capturing one in a
    /// singleton - a controller field, a static cache, a captured closure - would
    /// pin the first request's dependencies for the lifetime of the process.
    /// </para>
    /// <para>
    /// Internal types are deliberately excluded from the scan. Every validator in
    /// this layer is public, so including them would widen the scan without adding
    /// a registration, and an internal helper that happened to derive from the
    /// base validator type would be registered as a request validator by accident.
    /// </para>
    /// <para>
    /// Registration alone does not cause a validator to run: nothing in the
    /// framework invokes <see cref="IValidator{T}"/> for a bound action argument
    /// on its own, and this solution deliberately does not reference the retired
    /// automatic-validation package. The component that resolves a validator for a
    /// bound argument and turns its failures into a problem-details response is
    /// <c>Api/Filters/FluentValidationActionFilter.cs</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One service per aggregate, each scoped because each holds repositories that share the
        // request's database context. These registrations belong here rather than in the hosting
        // layer: the contracts and their implementations are both owned by this project, so naming
        // them anywhere else would make the composition root decide this layer's internal wiring.
        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<IModuleService, ModuleService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<ITabService, TabService>();
        services.AddScoped<IAuthService, AuthService>();

        // The type argument only names the assembly to scan; it carries no other
        // significance, and any public validator in this project would do.
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(
            lifetime: ServiceLifetime.Scoped,
            includeInternalTypes: false);

        // PagedRequestValidator has four sealed derivations, each narrowing the sortable set
        // for one list endpoint. All five validate PagedRequest, so the scan above leaves five
        // descriptors registered against IValidator<PagedRequest> and the container answers a
        // request for that contract with whichever one metadata order happened to put last -
        // that is, a bare PagedRequest could be judged against another endpoint's sortable set.
        // Re-registering the base validator last makes the unspecialised contract resolve to
        // the unspecialised rules. The derivations remain reachable by their own concrete types,
        // which the scan also registers, and that is how an endpoint asks for its own rules.
        services.AddScoped<IValidator<PagedRequest>, PagedRequestValidator>();

        return services;
    }
}
