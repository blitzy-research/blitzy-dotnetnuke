using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="PortalAlias"/> entity to the existing, immutable DotNetNuke 4.9 table
/// <c>dbo.PortalAlias</c> - singular - one row of which maps a single host name to the portal that
/// answers on it.
/// </summary>
/// <remarks>
/// <para>
/// Every column name, store type, length, nullability and constraint below is taken from the
/// <em>terminal</em> state of the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, never from a single script read in
/// isolation. For this table the terminal state is unusually easy to establish with certainty:
/// discounting the uninstall script, exactly four statements in the whole chain ever touch it. The
/// table is created with three columns at <c>02.02.02:L3804-L3808</c>, its foreign key is added at
/// <c>02.02.02:L3811-L3818</c>, its primary key at <c>02.02.02:L3957-L3961</c>, and a unique
/// constraint on the alias column at <c>03.00.07:L14-L18</c>. No later script re-types a column,
/// drops a constraint or adds an index, so the three columns, one primary key, one foreign key and
/// one unique index mapped here are the entire physical table.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything:
/// the mapping describes a table that already exists so that Entity Framework can address it, and no
/// installation is ever reshaped by it.
/// </para>
/// <para>
/// This table is where a legacy comma-delimited list ended up, which explains its shape. Version 1
/// held every host name for a portal in one <c>PortalAlias nvarchar(200) NOT NULL</c> column on
/// <c>dbo.Portals</c> (<c>01.00.00:L78</c>); <c>02.02.02</c> created this table, split that list into
/// one row per host name, and dropped the column. That history is why the table name is singular
/// while the entity models a member of a collection, and why <see cref="Portal"/> exposes an alias
/// collection and no alias scalar.
/// </para>
/// <para>
/// This type declares no constructor, and that is load-bearing rather than incidental.
/// <c>DnnDbContext.OnModelCreating</c> discovers configurations by scanning this assembly, and it
/// instantiates each candidate through its public parameterless constructor. An <c>internal
/// sealed</c> class with no declared constructor receives a compiler-generated public one and is
/// found; declaring a non-public parameterless constructor would make this class silently
/// undiscoverable - no compile error, no model-validation error - and the entity would fall back to
/// convention mapping against a table named <c>PortalAliases</c> and a column named
/// <c>HttpAlias</c>, neither of which exists in any DotNetNuke database.
/// </para>
/// </remarks>
internal sealed class PortalAliasConfiguration : IEntityTypeConfiguration<PortalAlias>
{
    /// <summary>
    /// Applies the mapping for the portal alias entity type.
    /// </summary>
    /// <param name="builder">The builder for the portal alias entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<PortalAlias> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // MIGRATION: the legacy table name is SINGULAR. Entity Framework's pluralising convention
        // would derive a pluralised name from the entity name, and every read and write against this
        // type would then address a table no DotNetNuke database contains, so the name is stated
        // explicitly instead of being left to convention. The legacy provider registration supplies
        // the rest of the identifier: objectQualifier is empty [Website/release.config:L354] and
        // databaseOwner is "dbo" [Website/release.config:L355], so the table is unprefixed and lives
        // in the dbo schema. Terminal creation is at 02.02.02:L3804-L3808.
        builder.ToTable("PortalAlias", "dbo");

        // PK_PortalAlias PRIMARY KEY CLUSTERED (PortalAliasID), added at 02.02.02:L3957-L3961. The
        // physical constraint name is preserved so that anything generated from this model names the
        // constraint the database already carries.
        builder.HasKey(a => a.PortalAliasId).HasName("PK_PortalAlias");

        // PortalAliasID int IDENTITY(1, 1) NOT NULL [02.02.02:L3805]. This identity seeds at 1,
        // unlike Portals.PortalID at IDENTITY(-1, 1) and Roles.RoleID at IDENTITY(0, 1), so stored
        // alias keys are always positive. The seed is still recorded rather than defaulted, because
        // the model is now the only machine-readable description of the real table's identity
        // specification. It is not a licence to read a non-positive key as "unsaved" either:
        // persisted state is declared through Entity<int>.MarkIdentityPersisted by code that already
        // knows the row exists, never inferred from the value of a key. Persistence declares nothing
        // automatically, so an alias read through this mapping reports IdentityIsPersisted as false.
        builder.Property(a => a.PortalAliasId)
            .HasColumnName("PortalAliasID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        // PortalID int NOT NULL [02.02.02:L3806], so an alias always belongs to a portal and the
        // relationship below is required rather than optional. Both -1 and 0 are legitimate portal
        // keys, because Portals.PortalID is IDENTITY(-1, 1) and -1 was simultaneously the legacy
        // Null.NullInteger sentinel; neither value may ever be read here as an absent, unset or
        // not-yet-saved portal. No sentinel is moved off the CLR default for this property: it is a
        // plain foreign key rather than part of the primary key, so the provider has no reason to
        // treat a zero as "still to be supplied by the principal row".
        builder.Property(a => a.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: the column is spelled HTTPAlias in full upper case while the CLR member is
        // HttpAlias, so the column name is pinned explicitly - acronym casing is a C# convention and
        // carries no authority over an immutable schema. The legacy class agrees on the spelling:
        // PortalAliasInfo.vb declares the field _HTTPAlias at L34 and exposes it as the property
        // HTTPAlias at L53, and the legacy provider passes it under that name too
        // [SqlDataProvider.vb:L1315-L1316]. SQL Server resolves identifiers case-insensitively, which
        // is precisely why this difference is easy to miss and impossible to rely on: a mis-spelled
        // mapping would bind on this installation and fail only under a case-sensitive collation or a
        // scripted schema comparison.
        //
        // MIGRATION: the column is NULLABLE and is deliberately left that way. Its terminal
        // declaration is "HTTPAlias nvarchar (200)" [02.02.02:L3807] with no NOT NULL clause, and no
        // script in the chain adds one, so the store permits null; the CLR property is string?, which
        // matches. Asserting requiredness here would refuse to write rows the legacy application can
        // legitimately store and would leave this mapping describing a table that is not the one in
        // the database. Normalising and validating a submitted host name belongs to the Application
        // layer, so nothing here trims it, lower-cases it, strips a port from it or substitutes an
        // empty string for a null.
        //
        // MIGRATION: nullable SQL columns map to nullable CLR properties and to nothing else. The
        // legacy Null.vb sentinel table - NullInteger -1, NullString the empty string, NullDate
        // DateTime.MinValue, NullByte 255 - is honoured here as mapping knowledge only: no value
        // converter folds a null into a sentinel or a sentinel into a null, in either direction.
        // Sentinel semantics survive only at the DTO and API boundary, where they are externally
        // observable and a legacy consumer may still depend on them.
        //
        // nvarchar is already Entity Framework's store type for a string property, so only the length
        // needs declaring; restating Unicode would add nothing.
        builder.Property(a => a.HttpAlias)
            .HasColumnName("HTTPAlias")
            .HasMaxLength(200);

        // FK_PortalAlias_Portals FOREIGN KEY (PortalID) REFERENCES Portals (PortalID) ON DELETE
        // CASCADE [02.02.02:L3811-L3818]. The cascade is declared rather than inherited from a
        // framework default, so a tracked graph behaves in memory exactly as the database behaves on
        // disk: deleting a portal deletes the aliases that resolve to it. Unlike several other
        // foreign keys in this schema, this one carries no NOT FOR REPLICATION clause, so the cascade
        // is unconditional and mapping it faithfully needs no qualification. NOT FOR REPLICATION has
        // no framework equivalent and is deliberately unrepresented across every configuration in
        // this folder rather than emulated here.
        //
        // The relationship is declared here, once, from the DEPENDENT side, because PortalAlias is
        // the end that holds the foreign key column. Assembly scanning gives no ordering guarantee
        // among configurations, so declaring the same relationship a second time from the principal
        // side would leave the surviving delete behaviour to whichever call happened to run last -
        // with no compile error and no model-validation error to reveal which one won.
        // PortalConfiguration therefore declares no relationship at all, and this is the single place
        // the portal-alias-to-portal association is defined.
        //
        // PortalId is bound to the column PortalID above, so naming that same property as the foreign
        // key reuses the existing mapping instead of introducing a second, shadow key property
        // alongside it.
        builder.HasOne(a => a.Portal)
            .WithMany(p => p.PortalAliases)
            .HasForeignKey(a => a.PortalId)
            .HasConstraintName("FK_PortalAlias_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: IX_PortalAlias UNIQUE NONCLUSTERED (HTTPAlias), added at 03.00.07:L14-L18 and
        // never dropped by a later script. The uniqueness is INSTALLATION-WIDE rather than
        // portal-scoped, so no two tenants can ever claim the same host name - and that is exactly
        // what makes exact-match tenant resolution sound. Legacy resolution was not exact: the
        // GetPortalSettings procedure read the tenant as "select @PortalID = min(PortalID) from
        // Portals where PortalAlias like '%' + @PortalAlias + '%'" [01.00.00:L4569-L4582, dropped for
        // good at 02.02.00:L267], under which an alias that is a substring of another tenant's alias
        // resolves to the wrong tenant and min() silently picks one of several matches.
        // PortalAliasResolutionMiddleware matches the stored value exactly instead, and that
        // deliberate behavioural difference is recorded in MIGRATION_NOTES.md. THIS FILE IMPLEMENTS
        // NO LOOKUP OF ANY KIND - the note belongs here only because this unique index is the
        // constraint that exact-match resolution relies on.
        //
        // The physical index name is preserved; the object qualifier is empty on this installation,
        // so the unqualified name is the real one. This is the only index on the table besides the
        // clustered primary key, and the chain creates no other at any version.
        //
        // MIGRATION: THE FILTER IS EXPLICITLY SUPPRESSED. HTTPAlias is nullable - the column is
        // declared without a NOT NULL clause at 02.02.02:L3807 - and the SQL Server provider attaches a
        // "HTTPAlias IS NOT NULL" predicate to a unique index over a nullable column unless told
        // otherwise. The terminal object is a plain UNIQUE NONCLUSTERED table constraint with no
        // predicate, which admits exactly one host-name-less row installation-wide; a filtered index
        // would admit any number of them. Because installation-wide uniqueness of the host name is the
        // property exact-match tenant resolution depends on, weakening it here would weaken tenant
        // isolation. Passing null as the filter removes the predicate.
        builder.HasIndex(a => a.HttpAlias)
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("IX_PortalAlias");

        // Entity<int>.Identity is not ignored explicitly: it is a get-only expression-bodied property
        // over PortalAliasId with no setter and no backing field, and property discovery requires a
        // writable property, so it is never a candidate for mapping in the first place.
    }
}
