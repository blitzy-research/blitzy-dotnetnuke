using DnnMigration.Domain.Abstractions.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Commits every change tracked by one <see cref="DnnDbContext"/> as a single unit.
/// </summary>
/// <remarks>
/// <para>
/// The legacy write paths had no unit of work at all. Creating a tenant, for instance, issued
/// separate stored procedure calls against the tenant table, the alias table, the roles table, the
/// pages table and the modules table with no transaction spanning them, so a failure part way
/// through left a half-built tenant behind. Because every repository in this assembly shares the
/// scoped context that this type commits, a single call now sends all of those writes inside one
/// transaction that the provider opens implicitly.
/// </para>
/// <para>
/// The type deliberately exposes nothing but the commit. It does not begin, commit or roll back a
/// transaction explicitly, because the provider already wraps a single save in one, and exposing
/// transaction control here would invite the application layer to hold a transaction open across
/// awaits it does not own. Where a genuinely multi-step sequence cannot be expressed as one save —
/// tenant creation is the one such case, because the identity of the tenant is needed before its
/// dependants can be written — the service performs two saves and compensates explicitly rather than
/// reaching for an ambient transaction.
/// </para>
/// </remarks>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly DnnDbContext _context;

    /// <summary>
    /// Initialises a new instance of the <see cref="UnitOfWork"/> class.
    /// </summary>
    /// <param name="context">The context whose tracked changes this instance commits.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public UnitOfWork(DnnDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// Commits every tracked change.
    /// </summary>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Thrown when a tracked row was changed or removed by another caller. Application services
    /// translate this into a conflict result rather than letting it escape as a server fault.
    /// </exception>
    /// <exception cref="DbUpdateException">
    /// Thrown when the store rejects the write, for example on a unique index violation that a
    /// pre-flight check could not exclude because of a concurrent insert.
    /// </exception>
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);
}
