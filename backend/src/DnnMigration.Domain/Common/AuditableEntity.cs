namespace DnnMigration.Domain.Common;

/// <summary>
/// Optional base type supplying the two audit timestamps the DotNetNuke 4.9.0 schema actually has, for the
/// minority of domain entities whose legacy tables genuinely carry them.
/// </summary>
/// <remarks>
/// <para>
/// The four-column audit convention of later DotNetNuke releases - a creating-user identifier and a
/// last-modifying-user identifier, each with a companion timestamp - is absent from this schema; all 88
/// upgrade scripts were searched for each of those column names and every one occurs zero times.
/// </para>
/// <para>
/// The legacy absent-date sentinel - <c>Date.MinValue</c>, published as <c>Null.NullDate</c> - is not
/// reproduced. Absence is a genuine null here, and sentinel semantics survive only at the DTO and API
/// boundary where a wire contract is externally observable.
/// </para>
/// </remarks>
/// <typeparam name="TId">
/// The CLR type of the entity's identity, constrained exactly as <see cref="Entity{TId}"/> constrains it so
/// that this base neither widens nor narrows what may derive from it.
/// </typeparam>
public abstract class AuditableEntity<TId> : Entity<TId>
    where TId : notnull
{
    protected AuditableEntity()
    {
    }

    /// <summary>
    /// Gets or sets the value of the legacy <c>CreatedDate</c> column, or <see langword="null"/> when the
    /// column holds no value or the entity has not been read from the database.
    /// </summary>
    /// <remarks>
    /// The property name matches the column name exactly, so the two have no opportunity to drift apart.
    /// The value is never defaulted here and the legacy earliest-representable-date sentinel is never
    /// substituted for a missing one; an application service assigns it from the injected clock, and any
    /// sentinel a wire contract still owes a caller is reinstated at the DTO boundary.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the value of the legacy <c>LastUpdatedDate</c> column, or <see langword="null"/> when
    /// the column holds no value or the entity has not been read from the database. Among in-scope tables
    /// this column exists only on the user-profile table, where it is mandatory.
    /// </summary>
    /// <remarks>
    /// The property name matches the column name exactly. Like the created timestamp it is assigned by an
    /// application service from the injected clock rather than computed here, which keeps time-dependent
    /// behaviour testable without a machine-clock dependency reaching the domain.
    /// </remarks>
    public DateTime? LastUpdatedDate { get; set; }
}
