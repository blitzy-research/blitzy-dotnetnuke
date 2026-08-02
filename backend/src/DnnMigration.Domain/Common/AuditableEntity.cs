namespace DnnMigration.Domain.Common;

/// <summary>
/// Optional base type supplying the two audit timestamps the DotNetNuke 4.9.0 schema actually has,
/// for the minority of domain entities whose legacy tables genuinely carry them.
/// </summary>
/// <remarks>
/// <para>
/// Opt in only when the entity's own legacy table really has the column. Of the twenty-one in-scope
/// entities exactly three do: <c>dbo.Users.CreatedDate</c>, which is optional;
/// <c>dbo.UserPortals.CreatedDate</c>, which is mandatory and was added later in the upgrade chain
/// with a <c>getdate()</c> default; and <c>dbo.UserProfile.LastUpdatedDate</c>, which is mandatory.
/// No in-scope table carries both. Applying this base wholesale would oblige the other eighteen
/// entity configurations to exclude properties that map to nothing.
/// </para>
/// <para>
/// Both properties are nullable, which is a measured decision: the same column name carries
/// different nullability on different in-scope tables, so a non-nullable property would force the
/// model to misrepresent one of them. Each entity configuration decides, per table, whether the
/// column is required.
/// </para>
/// <para>
/// The four-column audit convention of later DotNetNuke releases - a creating-user identifier and a
/// last-modifying-user identifier, each with a companion timestamp - is absent from this schema; all
/// 88 upgrade scripts were searched for each of those column names and every one occurs zero times.
/// User attribution, a soft-delete marker and a concurrency token are therefore all deliberately
/// absent, because there is nowhere to persist them.
/// </para>
/// <para>
/// Stamping belongs to the Application layer. Both properties are settable so that the persistence
/// materialiser can populate them when a row is read and an application service can assign them
/// when one is written, taking the value from the injected <c>IClock</c>. This type reads no ambient
/// machine clock and offers no stamping method: the Domain layer references nothing, so a
/// self-stamping entity would have no way to acquire the current time.
/// </para>
/// <para>
/// The legacy absent-date sentinel - <c>Date.MinValue</c>, published as <c>Null.NullDate</c> - is
/// not reproduced. Absence is a genuine null here, and sentinel semantics survive only at the DTO
/// and API boundary where a wire contract is externally observable. No attribute of any kind is
/// declared: table and column binding, nullability and key selection belong to the Fluent entity
/// configurations in the Infrastructure layer.
/// </para>
/// </remarks>
/// <typeparam name="TId">
/// The CLR type of the entity's identity, constrained exactly as <see cref="Entity{TId}"/>
/// constrains it so that this base neither widens nor narrows what may derive from it. Identity
/// storage, naming and equality remain the concern of <see cref="Entity{TId}"/>.
/// </typeparam>
public abstract class AuditableEntity<TId> : Entity<TId>
    where TId : notnull
{
    protected AuditableEntity()
    {
    }

    /// <summary>
    /// Gets or sets the value of the legacy <c>CreatedDate</c> column, or <see langword="null"/>
    /// when the column holds no value or the entity has not been read from the database.
    /// </summary>
    /// <remarks>
    /// The property name matches the column name exactly, so the two have no opportunity to drift
    /// apart. The value is never defaulted here and the legacy earliest-representable-date sentinel
    /// is never substituted for a missing one; an application service assigns it from the injected
    /// clock, and any sentinel a wire contract still owes a caller is reinstated at the DTO boundary.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the value of the legacy <c>LastUpdatedDate</c> column, or <see langword="null"/>
    /// when the column holds no value or the entity has not been read from the database. Among
    /// in-scope tables this column exists only on the user-profile table, where it is mandatory.
    /// </summary>
    /// <remarks>
    /// The property name matches the column name exactly. Like the created timestamp it is assigned
    /// by an application service from the injected clock rather than computed here, which keeps
    /// time-dependent behaviour testable without a machine-clock dependency reaching the domain.
    /// </remarks>
    public DateTime? LastUpdatedDate { get; set; }
}
