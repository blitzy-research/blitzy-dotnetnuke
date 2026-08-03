using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: this type re-authors DotNetNuke.Entities.Portals.PortalAliasInfo
// (Library/Components/Portal/PortalAliasInfo.vb) as a plain persistence POCO. The legacy class was
// three private fields behind three Property Get/Set blocks - PortalID at line 37, PortalAliasID at
// line 45 and HTTPAlias at line 53 - with no Inherits clause, no Implements clause, no attribute and
// no method. Three scalars is therefore the WHOLE legacy contract rather than a chosen subset of it,
// and any further member here would be an invention rather than a port.

// MIGRATION: member names are idiomatic C#; column names are not renamed. The Infrastructure entity
// configuration must bind PortalAliasId to the column PortalAliasID, and HttpAlias to the column
// HTTPAlias in that exact upper-case spelling. Acronym casing is a C# convention with no authority
// over the schema, which is immutable for this migration. SQL Server resolves identifiers
// case-insensitively, so a column renamed to match the member would still bind on this installation
// and would fail nowhere until a case-sensitive collation, a raw SQL statement or a scripted
// comparison met it - which is why the obligation is stated here rather than left to convention.

// MIGRATION: HttpAlias is nullable because the measured column is. The terminal base table is created
// at Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider lines 3804-3808 as
// ([PortalAliasID] [int] IDENTITY (1, 1) NOT NULL, [PortalID] [int] NOT NULL, [HTTPAlias] [nvarchar]
// (200)): the third column carries no NOT NULL clause, and no script in the 88-script chain alters it
// afterwards. The legacy property was declared As String, which in VB.NET states nothing about
// nullability, so it is not evidence for tightening the CLR type. Whether the mapping refuses to
// WRITE a null is a persistence-layer decision and may not propagate back into this type.

// MIGRATION: that column was widened by the split which created this table, not narrowed. Before it,
// a single NOT NULL column named PortalAlias on dbo.Portals held a COMMA-DELIMITED list of host names
// (01.00.00.SqlDataProvider line 79); 02.02.02.SqlDataProvider lines 3886-3927 split that list into
// one row per host name here, skipping empty entries, and then dropped the column.

// MIGRATION: two constraints the Infrastructure configuration must declare, because the schema
// already has them and Rule T4 forbids changing it. HTTPAlias carries the unique nonclustered
// constraint IX_{objectQualifier}PortalAlias added by 03.00.07.SqlDataProvider lines 14-18, and that
// uniqueness is GLOBAL rather than portal-scoped, so no two tenants can ever claim the same host
// name. PortalID carries FK_{objectQualifier}PortalAlias_{objectQualifier}Portals referencing
// dbo.Portals(PortalID), declared ON DELETE CASCADE at 02.02.02.SqlDataProvider lines 3811-3818, so
// deleting a tenant deletes its aliases; the mapping must restate that delete behaviour rather than
// accept a framework default.

// MIGRATION: resolving a request's host name to a tenant is deliberately NOT a member of this type.
// The lookup belongs to PortalAliasResolutionMiddleware and the alias repository, and it matches the
// stored value exactly. That replaces the legacy resolution, whose GetPortalSettings procedure
// selected min(PortalID) from Portals where PortalAlias like '%' + @PortalAlias + '%'
// (01.00.00.SqlDataProvider), under which an alias that was a substring of another tenant's alias
// could resolve to the wrong tenant. The change is a documented behavioural difference recorded in
// MIGRATION_NOTES.md, and keeping the entity free of any comparable matching member is what keeps
// that record the single place the behaviour is decided.

// MIGRATION: the pre-generics companion PortalAliasCollection.vb produces no target file. It derived
// from DictionaryBase in order to key aliases by host name; a keyed read is now a repository query or
// an IReadOnlyDictionary, and this type models one row rather than a collection of them.

/// <summary>
/// One host name a portal answers on, modelling the terminal <c>dbo.PortalAlias</c> base table.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a persisted column of that table, in the table's own column order, plus the single
/// navigation to the owning portal. The type holds no behaviour: matching an incoming host name to a
/// tenant belongs to the resolution middleware and the alias repository, uniqueness enforcement to
/// the database constraint the Infrastructure configuration declares, and normalising and validating
/// a submitted host name to the Application layer.
/// </para>
/// <para>
/// One portal owns many aliases - <see cref="Portal.PortalAliases"/> is the inverse end - and every
/// alias belongs to exactly one portal, which is why <see cref="PortalId"/> is not nullable.
/// </para>
/// <para>
/// Two invariants govern consumers. <see cref="HttpAlias"/> may be null, because the column permits
/// null, so code that requires a host name tests for one instead of assuming it. And
/// <see cref="PortalId"/> may legitimately hold -1 or 0, because <c>dbo.Portals.PortalID</c> is
/// declared <c>IDENTITY(-1, 1)</c> and -1 was simultaneously the legacy null-integer sentinel;
/// neither value may be read here as an unset or missing portal.
/// </para>
/// </remarks>
public sealed class PortalAlias : Entity<int>
{
    /// <summary>The value equality is based on, which is always <see cref="PortalAliasId"/>.</summary>
    /// <remarks>
    /// Never mapped to a column; the entity configuration names <see cref="PortalAliasId"/> in its
    /// key declaration.
    /// </remarks>
    public override int Identity => PortalAliasId;

    /// <summary>
    /// The <c>PortalAliasID</c> column: <c>int IDENTITY(1, 1) NOT NULL</c>, the primary key
    /// <c>PK_PortalAlias</c>, generated by the database.
    /// </summary>
    /// <remarks>
    /// The column name must be stated explicitly by the entity configuration, because the member
    /// spells the acronym <c>Id</c> while the column spells it <c>ID</c>. Unlike the portal and role
    /// keys, this identity seeds at 1, so no negative or zero value occurs in stored data - which is
    /// still not licence to read one as "unsaved": persisted state is declared through
    /// <see cref="Entity{TId}.MarkIdentityPersisted"/>, never inferred from the value.
    /// </remarks>
    public int PortalAliasId { get; set; }

    /// <summary>
    /// The <c>PortalID</c> column: <c>int NOT NULL</c>, the foreign key to <c>dbo.Portals</c> under
    /// <c>FK_PortalAlias_Portals</c>, which the schema declares <c>ON DELETE CASCADE</c>.
    /// </summary>
    /// <remarks>
    /// Required, so an alias always belongs to a portal. Both -1 and 0 key real portals, so no
    /// comparison against either may be read as absence.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// The <c>HTTPAlias</c> column: <c>nvarchar(200)</c>, nullable, carrying the installation-wide
    /// unique constraint <c>IX_PortalAlias</c>. Holds the host name a request may arrive on,
    /// optionally with a port and a virtual path - for example <c>www.example.com</c>,
    /// <c>localhost:8080</c> or <c>www.example.com/child</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable because the measured column declares no <c>NOT NULL</c>, and stored exactly as
    /// written: nothing here trims, lower-cases, strips a port, or substitutes an empty string for
    /// null. Case-insensitive comparison belongs to the repository, and normalising a submitted value
    /// belongs to the Application layer, so that the stored value stays the one the schema holds.
    /// </para>
    /// <para>
    /// The entity configuration must name the column <c>HTTPAlias</c> in that exact spelling; this
    /// member name is only the idiomatic C# rendering of it and carries no mapping authority.
    /// </para>
    /// </remarks>
    public string? HttpAlias { get; set; }

    /// <summary>
    /// The portal this alias resolves to: the principal end of <c>FK_PortalAlias_Portals</c>, paired
    /// with <see cref="Portal.PortalAliases"/>.
    /// </summary>
    /// <remarks>
    /// Non-nullable because the foreign key is: every row references a portal. Whether it is loaded
    /// remains the reader's choice, as with any reference navigation - a query that does not include
    /// it leaves it unset - so a caller either asks for the portal explicitly or works from
    /// <see cref="PortalId"/>. Assigning it instead of <see cref="PortalId"/> is how a new alias is
    /// bound to a portal whose database-generated key does not exist yet.
    /// </remarks>
    public Portal Portal { get; set; }
}
