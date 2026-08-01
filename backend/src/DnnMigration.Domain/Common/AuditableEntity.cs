namespace DnnMigration.Domain.Common;

// MIGRATION: This base type is net-new; the legacy VB.NET model has no predecessor for it. The
// closest analogue in the legacy tree, Library/Components/Users/Membership/UserMembership.vb, is
// not a base class at all: it is a User-specific membership class whose five date properties are
// each declared read-only for the Web Forms property editor (lines 103, 143, 163, 183 and 203) and
// which declares no last-modification member of any kind. Four of those five - last activity, last
// lockout, last login and last password change - are membership behaviour belonging to the user
// aggregate alone, and they are deliberately absent from this shared base.
//
// MIGRATION: The four-column audit convention of later DotNetNuke releases does not exist in this
// 4.9.0 schema, so this type provides no user-attribution audit field whatsoever. All 88 upgrade
// scripts under Website/Providers/DataProviders/SqlDataProvider - 83,614 lines - were searched for
// all four of that convention's column names: the creating-user identifier, the last-modifying-user
// identifier, and the two companion timestamp columns whose names end in OnDate. Each of the four
// occurs exactly ZERO times. Only the two timestamp columns this schema genuinely has are modelled.
// The literal spellings of those four absent columns are intentionally written nowhere in the
// domain, so that a repository-wide search for them keeps returning nothing.
//
// MIGRATION: Adoption is OPT-IN rather than universal. Exactly three in-scope tables carry either
// column in the terminal schema: dbo.Users.CreatedDate, dbo.UserPortals.CreatedDate and
// dbo.UserProfile.LastUpdatedDate. Deriving all 21 in-scope entities from this type would oblige
// the other eighteen entity configurations to exclude two properties that have no column behind
// them, contradicting the immutable-schema rule this migration works under.
//
// MIGRATION: The legacy absent-date sentinel is deliberately not reproduced.
// Library/Components/Shared/Null.vb exposes NullDate at lines 66 to 70, returning Date.MinValue -
// the earliest representable date rather than a null - and the legacy readers substituted it for a
// database null on every read. Here absence is a genuine null. Sentinel semantics survive only at
// the DTO and API boundary, where the wire contract is externally observable, never in the domain.

/// <summary>
/// Optional base type supplying the two audit timestamps that the DotNetNuke 4.9.0 schema actually
/// has, for the small minority of domain entities whose legacy tables genuinely carry them.
/// </summary>
/// <remarks>
/// <para>
/// Derive from this type only when the entity's own legacy table really has the column. This base
/// is opt-in by design and must never be applied wholesale: of the twenty-one in-scope entities,
/// only three have a table behind either timestamp. Forcing it onto the rest would oblige their
/// entity configurations to exclude properties that map to nothing, which is precisely the kind of
/// invented schema this migration forbids - the existing database is immutable here, and the domain
/// model is expected to match it rather than to improve on it.
/// </para>
/// <para>
/// The wider audit convention that later DotNetNuke releases use - a creating-user identifier
/// alongside a last-modifying-user identifier, each paired with its own timestamp - is absent from
/// this schema entirely. All 88 upgrade scripts under
/// <c>Website/Providers/DataProviders/SqlDataProvider</c>, 83,614 lines in total, were searched for
/// each of those four column names, and every one of them occurs exactly zero times. That is why
/// this type exposes no notion of who created or last changed a row: there is nowhere to persist
/// it. The only two audit columns the schema does have are the two modelled below.
/// </para>
/// <list type="table">
///   <listheader>
///     <term>In-scope table and column</term>
///     <description>Declared nullability and provenance</description>
///   </listheader>
///   <item>
///     <term>dbo.Users.CreatedDate</term>
///     <description>
///     Declared as an optional datetime in the baseline script <c>01.00.00.SqlDataProvider</c> at
///     line 108, and never made mandatory by any later script.
///     </description>
///   </item>
///   <item>
///     <term>dbo.UserPortals.CreatedDate</term>
///     <description>
///     Mandatory. Absent from the baseline table definition, added by
///     <c>03.00.10.SqlDataProvider</c> with a <c>getdate()</c> default, and confirmed mandatory
///     again by <c>03.01.01.SqlDataProvider</c> at line 1335. The baseline definition alone is
///     therefore misleading: only the terminal state of the upgrade chain describes the real
///     schema.
///     </description>
///   </item>
///   <item>
///     <term>dbo.UserProfile.LastUpdatedDate</term>
///     <description>
///     Mandatory, declared by <c>03.02.03.SqlDataProvider</c> at line 1372 and threaded through the
///     profile-update procedure in the same script. Corroborated by the legacy provider contract at
///     <c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c> line 119, whose
///     profile-property update takes the value as a required argument.
///     </description>
///   </item>
/// </list>
/// <para>
/// Both timestamps are nullable, and that is a measured decision rather than a stylistic one. The
/// same column name carries different nullability on two different in-scope tables: it is optional
/// on the users table and mandatory on the user-portals table. A non-nullable property would
/// therefore force the domain model to misrepresent one of the two. Keeping both nullable lets each
/// entity configuration decide, per table, whether the column is required, so the model stays
/// truthful and no schema change is implied.
/// </para>
/// <para>
/// No in-scope table carries both columns. The users and user-portals tables have only the created
/// timestamp; the user-profile table has only the last-updated timestamp. An entity that opts in
/// therefore maps the one column its own table really has, and its configuration excludes the other
/// rather than inventing a column to satisfy it.
/// </para>
/// <para>
/// Stamping is the Application layer's responsibility, never this type's. Both properties are
/// settable so that the persistence materialiser can populate them when a row is read and an
/// application service can assign them when one is written, taking the value from the injected
/// <c>IClock</c> abstraction. This type reads no ambient machine clock and offers no stamping
/// method of its own: a self-stamping entity would have to reach out for the current time, and the
/// Domain layer references nothing at all - no project, no package, and no framework beyond the
/// base class library.
/// </para>
/// <para>
/// Also deliberately absent, each for a measured reason: any form of user attribution, because no
/// such column exists anywhere in the schema; a soft-delete marker, which is not a concept this
/// schema expresses; and a concurrency token, for which there is likewise no column. Adding any of
/// them would mean either inventing schema or carrying dead weight through every configuration.
/// </para>
/// <para>
/// This type carries no attribute of any kind. Table and column binding, nullability and key
/// selection all belong to the Fluent entity configurations in the Infrastructure layer, and the
/// externally observable wire contract belongs to the DTOs at the API boundary.
/// </para>
/// </remarks>
/// <typeparam name="TId">
/// The CLR type of the entity's identity, constrained exactly as <see cref="Entity{TId}"/>
/// constrains it so that this base neither widens nor narrows what may derive from it. Identity
/// storage, naming and equality remain entirely the concern of <see cref="Entity{TId}"/>; this type
/// adds timestamps and nothing else.
/// </typeparam>
/// <example>
/// A user opts in, because its table really has the created timestamp, and it still supplies its
/// own legacy-named identity exactly as <see cref="Entity{TId}"/> requires:
/// <code>
/// public sealed class User : AuditableEntity&lt;int&gt;
/// {
///     public int UserID { get; set; }
///
///     public override int Identity => UserID;
/// }
/// </code>
/// A role, a tab or a permission has neither column, so it derives from <see cref="Entity{TId}"/>
/// directly and never sees these timestamps at all.
/// </example>
public abstract class AuditableEntity<TId> : Entity<TId>
    where TId : notnull
{
    /// <summary>
    /// Initialises a new instance of the <see cref="AuditableEntity{TId}"/> class.
    /// </summary>
    /// <remarks>
    /// Parameterless and protected on purpose, mirroring <see cref="Entity{TId}"/>. Neither
    /// timestamp is a constructor argument: both are assigned after construction, by the
    /// persistence materialiser when a row is read and by an application service when one is
    /// written, so requiring either here would obstruct materialisation for no benefit.
    /// </remarks>
    protected AuditableEntity()
    {
    }

    /// <summary>
    /// Gets or sets the moment at which the row this entity represents was created, or
    /// <see langword="null"/> when the underlying column holds no value or the entity has not been
    /// read from the database.
    /// </summary>
    /// <value>
    /// The value of the legacy <c>CreatedDate</c> column: optional on the users table, mandatory on
    /// the user-portals table, and absent from the user-profile table. The owning entity
    /// configuration is what decides which of those three applies.
    /// </value>
    /// <remarks>
    /// The property name matches the legacy column name exactly, so no column remapping is needed
    /// for it and the two names have no opportunity to drift apart. The value is never defaulted
    /// here, and in particular the legacy earliest-representable-date sentinel is not substituted
    /// for a missing value: absence is expressed as a genuine null in the domain, and any sentinel
    /// the wire contract still owes a caller is reinstated at the DTO boundary instead.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the moment at which the row this entity represents was last updated, or
    /// <see langword="null"/> when the underlying column holds no value or the entity has not been
    /// read from the database.
    /// </summary>
    /// <value>
    /// The value of the legacy <c>LastUpdatedDate</c> column, which among in-scope tables exists
    /// only on the user-profile table, where it is mandatory. An entity whose table does not have
    /// it leaves this property unmapped.
    /// </value>
    /// <remarks>
    /// The property name matches the legacy column name exactly, so no column remapping is needed
    /// for it. Like the created timestamp, it is assigned by an application service from the
    /// injected clock abstraction rather than computed here, which is what keeps time-dependent
    /// behaviour testable without a machine-clock dependency reaching the domain model.
    /// </remarks>
    public DateTime? LastUpdatedDate { get; set; }
}
