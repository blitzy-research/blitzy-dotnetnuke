using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Services;
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
/// closes over FluentValidation and the dependency-injection abstractions and
/// nothing else. That surface is narrower than it may appear: the options
/// abstractions are absent from it, which is why the services and validators in
/// this layer take a bound settings object directly rather than an
/// <c>IOptions&lt;T&gt;</c> wrapper they have no way to name.
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

        // MIGRATION: these registrations replace the legacy reflection-based service location.
        // Library/Components/Providers/Data/DataProvider.vb declared a shared constructor (L38-L40)
        // that called Framework.Reflection.CreateObject (L44) and published the result through a
        // static Instance() accessor (L48), so a collaborator was named by a configuration string
        // and reached from anywhere without being declared. A mistyped provider name therefore
        // failed at first use rather than at startup. Resolving the same collaborators through the
        // container moves that failure to startup and makes each dependency explicit in a
        // constructor. The counterpart for the persistence and security contracts is
        // Infrastructure/DependencyInjection.cs; the divergence is recorded in MIGRATION_NOTES.md.

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

        // The anchor only names the assembly to scan; it carries no other significance. This class
        // is the anchor rather than one of the validators because it is the one type here guaranteed
        // to survive any reorganisation of Validation/ - a validator used as the anchor would
        // silently take the whole scan with it if it were ever renamed, moved or merged, and an
        // unregistered validator fails silently: its rules simply never run.
        //
        // The typeof form is required, not stylistic. The generic overload cannot be used here
        // because this class is static and C# forbids a static type as a generic type argument
        // (CS0718), so AddValidatorsFromAssemblyContaining<DependencyInjection>() does not compile.
        // The Type overload is the same scan with the same anchor, and naming the assembly this way
        // still needs no System.Reflection import because the Assembly type is never named.
        services.AddValidatorsFromAssemblyContaining(
            typeof(DependencyInjection),
            lifetime: ServiceLifetime.Scoped,
            includeInternalTypes: false);

        // Nothing is registered after the scan, and the paged-request validators are why that is
        // worth stating. Their base, PagedRequestValidator<TRequest>, is an open generic and so is
        // skipped by the scan; each collection binds its own derived request type, and the sealed
        // validator closing over that type is what the scan finds. One descriptor per closed
        // contract results, so IValidator<PortalPagedRequest> resolves the portal validator and
        // applies that collection's sortable fields alone, while IValidator<PagedRequest> resolves
        // the unspecialised validator for the bare contract. There is consequently nothing to
        // disambiguate and no descriptor to override by hand. Deliberately absent: any count of the
        // validators registered here. The scan is authoritative precisely because it needs no
        // census, and a number written down in a comment is a number that goes stale silently.

        return services;
    }
}
