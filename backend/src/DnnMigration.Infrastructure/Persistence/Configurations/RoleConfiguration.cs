using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Role"/> entity to the existing, immutable DotNetNuke <c>dbo.Roles</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The schema is an input to this migration, never an output. Every declaration below reproduces the
/// <em>cumulative terminal</em> state of the eighty-eight upgrade scripts under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, and each carries the script and line that
/// settled it. That distinction is load-bearing for this table in particular, because
/// <c>dbo.Roles</c> is rebuilt twice through a temporary table and then dropped and renamed over, so
/// its baseline declaration describes only eight of the fifteen terminal columns and two of those
/// eight are already dead by the first rebuild. Anyone re-checking the chain must search
/// case-insensitively and across all four naming forms it uses - bare, <c>dbo.</c>-qualified,
/// bracketed and <c>{databaseOwner}{objectQualifier}</c>-templated - because a single-form
/// case-sensitive search reports statements as absent when they are present.
/// </para>
/// <para>
/// <c>Website/release.config</c> registers the data provider with <c>objectQualifier</c> empty (line
/// 354) and <c>databaseOwner</c> set to <c>dbo</c> (line 355), so the table is addressed unqualified
/// in the <c>dbo</c> schema.
/// </para>
/// <para>
/// Three properties of this mapping are easy to get wrong and silently damaging when got wrong: the
/// identity seed is zero rather than one, so a role numbered zero is real; the two frequency columns
/// hold single letters that the legacy billing arithmetic branched on, so persistence must round-trip
/// the character and not the member name or the numeric value behind it; and the service fee is a
/// nullable column that nonetheless carries a store default, which its sibling trial fee does not.
/// Each is documented at the property it governs.
/// </para>
/// <para>
/// Only the two relationships for which this entity is the dependent are declared here. Every other
/// association involving a role is declared by the entity that depends on it, so that each is
/// configured exactly once and no discovery order can decide which delete behaviour survives.
/// </para>
/// </remarks>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <summary>
    /// Applies the table, key, column, index and relationship mapping for the role entity type.
    /// </summary>
    /// <param name="builder">The builder used to configure the entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles", "dbo");

        // MIGRATION: PK_Roles is declared NONCLUSTERED [01.00.05:L2783-2788]. Clustering is physical
        // storage metadata with no EF Core surface, so it is recorded here rather than expressed in
        // code. The earlier, textually identical keys at 01.00.00:L490 and 01.00.04:L1357-1358 were
        // destroyed by the two table rebuilds, and the no-op rename at 02.00.00:L120 changes nothing;
        // the 01.00.05 declaration is the terminal one.
        builder.HasKey(x => x.RoleId).HasName("PK_Roles");

        // MIGRATION: the identity is seeded at ZERO - "RoleID int NOT NULL IDENTITY (0, 1)"
        // [01.00.05:L2748], restating the baseline at 01.00.00:L115 and the first rebuild at
        // 01.00.04:L1322. The first role an installation creates is therefore numbered 0, and in a
        // freshly provisioned database that row is the Administrators role, the most privileged one
        // there is. Zero is also the CLR default of int, which is exactly why no default-valued
        // heuristic may be applied to this key: never test for 0, for a negative value, or for
        // default as a proxy for "unsaved". Whether the row exists is declared by the persistence
        // layer, never inferred. The neighbouring Portals key compounds this, being seeded at -1, so
        // the legacy -1 integer marker for absence collides with live key space in both directions
        // and no marker-to-null conversion is installed anywhere in this mapping.
        builder.Property(x => x.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(0, 1);

        // MIGRATION: SCHEMA CORRECTION - the terminal column is "PortalID int NOT NULL"
        // [01.00.05:L2749], not nullable. The full chain: it is born nullable [01.00.00:L116], is
        // still nullable after the first temporary-table rebuild [01.00.04:L1323], and is tightened
        // by the second and final rebuild [01.00.05:L2746-2756], which then makes that shape
        // physical by dropping the original table [01.00.05:L2777] and renaming the replacement over
        // it [01.00.05:L2780]. It is therefore a committed change and not a transient shape adopted
        // mid-rebuild. An exhaustive sweep of every later script for a column-nullability alteration
        // against this key returns exactly two statements, both targeting ProfilePropertyDefinition
        // [03.03.03:L77-78 and 04.03.03:L77-78], and no further rebuild of this table exists.
        //
        // The domain property is declared int? and the domain layer is owned elsewhere, so the
        // column is pinned here with IsRequired() while the CLR type is left untouched - EF Core
        // supports precisely this combination. The discrepancy is escalated rather than absorbed:
        // either the property tightens to int or this line relaxes, and the two must not disagree
        // indefinitely.
        //
        // Nullability is per table and must not be generalised from this one. RoleGroups.PortalID is
        // likewise NOT NULL, whereas the same key on Tabs, on Modules and on
        // ProfilePropertyDefinition is genuinely nullable.
        builder.Property(x => x.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .IsRequired();

        // MIGRATION: nvarchar(50) NOT NULL [01.00.05:L2750]. Unique per portal, not globally - see
        // the composite index below.
        builder.Property(x => x.RoleName)
            .HasColumnName("RoleName")
            .HasMaxLength(50)
            .IsRequired();

        // MIGRATION: nvarchar(1000) NULL [01.00.05:L2751]. The width matches the sibling role-group
        // description and is deliberately wider than the 500 used by portals and tabs.
        builder.Property(x => x.Description)
            .HasColumnName("Description")
            .HasMaxLength(1000);

        // MIGRATION: the legacy type exposed this as a VB Single [RoleInfo.vb:L48, L164-169] while
        // the terminal column is money [03.01.01:L1173] - already money by the first rebuild
        // [01.00.04:L1326] and originally decimal(5, 2) [01.00.00:L119] - so the destination follows
        // the schema with a decimal rather than inheriting the narrower legacy CLR type. A binary
        // floating-point type here would lose currency precision that the column preserves.
        //
        // MIGRATION: this column is NULL-able and yet carries a store default of 0
        // [DF_Roles_ServiceFee, 03.01.01:L1177], re-added after the repair block above it drops the
        // previous constraint by dynamic name discovery [03.01.01:L1144] and so never names it in
        // the script text. The combination is unusual but real, and it is reproduced exactly. Note
        // that the trial fee below is the same store type with no default at all; the two must not
        // be unified.
        builder.Property(x => x.ServiceFee)
            .HasColumnName("ServiceFee")
            .HasColumnType("money")
            .HasDefaultValue(0m);

        // MIGRATION: char(1) NULL [01.00.05:L2753], and the single-character codes are DATA rather
        // than presentation. The legacy controller switches on them directly - 'N', 'O', 'D', 'W',
        // 'M' and 'Y' select the interval for its date arithmetic [RoleController.vb:L540-547, with
        // the day, week, month and year arms at L543-546] - and installations in the field already
        // hold rows containing those letters. The domain enumeration therefore carries the
        // characters themselves as its values, and persistence must round-trip the CHARACTER.
        //
        // The conversion is written explicitly because both convenient shorthands are wrong and fail
        // silently. Converting through the member name would store "None" or "Month" and overflow a
        // single-character column; letting the enumeration persist as its numeric backing value
        // would store the code point of the letter, so 'D' would arrive as 68. Neither produces a
        // build error and neither is recoverable once written.
        //
        // MIGRATION: the read side maps a store NULL and an empty string onto the same CLR null.
        // That is a normalisation of two indistinguishable legacy encodings of absence, not a
        // reintroduction of the legacy empty-string marker: the legacy sentinel table used the empty
        // string where this column permits a true NULL, so existing rows may carry either. Absence
        // must stay distinct from the declared None member, whose stored code is the letter N.
        //
        // MIGRATION: nothing validates these codes at the database level. The foreign key that once
        // constrained this column against a code lookup table existed [01.00.00:L584] and was
        // recreated by both rebuilds [01.00.04:L1366, 01.00.05:L2802], but it is dropped for good at
        // 03.00.01:L1297 with no recreate. No check constraint replaced it. The exactness of this
        // conversion is consequently the only thing standing between the enumeration and the column.
        builder.Property(x => x.BillingFrequency)
            .HasColumnName("BillingFrequency")
            .HasConversion(
                v => v.HasValue ? ((char)v.Value).ToString() : null,
                s => string.IsNullOrEmpty(s) ? null : (BillingFrequency?)(BillingFrequency)s[0])
            .HasColumnType("char(1)")
            .HasMaxLength(1)
            .IsUnicode(false);

        // MIGRATION: int NULL [01.00.05:L2754], a count of trial-frequency units.
        builder.Property(x => x.TrialPeriod)
            .HasColumnName("TrialPeriod")
            .HasColumnType("int");

        // MIGRATION: char(1) NULL [01.00.05:L2755], carrying the same six codes over the same
        // enumeration as the billing frequency and converted identically. No separate trial-specific
        // enumeration is introduced, mirroring the legacy type, which backed both properties with
        // one String contract [RoleInfo.vb:L49 with L149-154, and L51 with L188-193].
        builder.Property(x => x.TrialFrequency)
            .HasColumnName("TrialFrequency")
            .HasConversion(
                v => v.HasValue ? ((char)v.Value).ToString() : null,
                s => string.IsNullOrEmpty(s) ? null : (BillingFrequency?)(BillingFrequency)s[0])
            .HasColumnType("char(1)")
            .HasMaxLength(1)
            .IsUnicode(false);

        // MIGRATION: the two legacy sources disagree on this one. The membership provider declared
        // the parameter as a String [MembershipProviders/DataProvider/DataProvider.vb:L95 and L97]
        // while the legacy entity exposed an Integer [RoleInfo.vb:L52, L218-223]. The terminal column
        // settles it as "BillingPeriod int NULL" [01.00.08:L6829], and the schema wins: a nullable
        // int.
        builder.Property(x => x.BillingPeriod)
            .HasColumnName("BillingPeriod")
            .HasColumnType("int");

        // MIGRATION: a VB Single in the legacy entity [RoleInfo.vb:L53, L233-238] against a terminal
        // "TrialFee money NULL" [01.00.08:L6830], mapped as decimal for the same precision reason as
        // the service fee. Unlike the service fee it carries NO store default, and none is invented
        // for symmetry.
        builder.Property(x => x.TrialFee)
            .HasColumnName("TrialFee")
            .HasColumnType("money");

        // MIGRATION: added as "bit NOT NULL" with a default of 0 [01.00.08:L6831], re-tightened at
        // 03.01.01:L1174 and its default re-added at 03.01.01:L1179 after the dynamic-name drop
        // block [03.01.01:L1154].
        builder.Property(x => x.IsPublic)
            .HasColumnName("IsPublic")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // MIGRATION: added as "bit NOT NULL" with a default of 0 [01.00.08:L6832], re-tightened at
        // 03.01.01:L1175 and its default re-added at 03.01.01:L1181 after the dynamic-name drop
        // block [03.01.01:L1164].
        builder.Property(x => x.AutoAssignment)
            .HasColumnName("AutoAssignment")
            .HasColumnType("bit")
            .IsRequired()
            .HasDefaultValue(false);

        // MIGRATION: int NULL [04.00.04:L66-67]. This addition and the pair below it each appear
        // twice in the chain - at 03.02.03:L33-34 and again verbatim at 04.00.04, both guarded by a
        // version test so an installation that skipped the earlier script still receives them - and
        // each pair is one logical change, not two. Group membership is optional, so the null here
        // represents an ungrouped role rather than standing in for one.
        builder.Property(x => x.RoleGroupId)
            .HasColumnName("RoleGroupID")
            .HasColumnType("int");

        // MIGRATION: the column name is fully upper-cased in the schema - "RSVPCode nvarchar(50)
        // NULL" [04.00.04:L79-80, equivalently 03.02.03:L44-45] - while the property follows C#
        // casing, so the mapping cannot be left to convention.
        builder.Property(x => x.RsvpCode)
            .HasColumnName("RSVPCode")
            .HasMaxLength(50);

        // MIGRATION: nvarchar(100) NULL [04.00.04:L79-80]. The value is a file reference that is
        // resolved above this layer; persistence stores it verbatim.
        builder.Property(x => x.IconFile)
            .HasColumnName("IconFile")
            .HasMaxLength(100);

        // MIGRATION: declared as a UNIQUE NONCLUSTERED table constraint rather than as a standalone
        // unique index [03.00.09:L304]; EF Core expresses the two forms identically. The key order is
        // the portal first and the name second, and it is reproduced exactly, because the scope of
        // the guarantee is what matters: role names are unique PER PORTAL, so two tenants may each
        // own a role of the same name. An earlier constraint of the same name [02.00.03:L208-209] was
        // dropped at 03.00.09:L298 immediately before this one replaced it. The physical name is
        // singular on a plural table and is carried across verbatim rather than tidied.
        builder.HasIndex(x => new { x.PortalId, x.RoleName })
            .IsUnique()
            .HasDatabaseName("IX_RoleName");

        // MIGRATION: the terminal IX_Roles covers the BILLING FREQUENCY [03.00.09:L302], having been
        // dropped at 03.00.09:L300 and recreated there over that column. Its companion index on the
        // portal key was dropped at 03.00.03:L310 and never recreated, so it is deliberately absent
        // from this mapping. The terminal object set for this table is exactly PK_Roles, IX_RoleName,
        // IX_Roles, FK_Roles_Portals, FK_Roles_RoleGroups and three column defaults - nothing else.
        builder.HasIndex(x => x.BillingFrequency)
            .HasDatabaseName("IX_Roles");

        // MIGRATION: FK_Roles_Portals declares ON DELETE CASCADE explicitly
        // [01.00.05:L2790-2799], so deleting a portal deletes the roles it owns. A real cascade is
        // never quietly downgraded to a weaker behaviour: doing so would strand rows the database
        // currently removes. Earlier versions of this constraint [01.00.00:L590, 01.00.04:L1376,
        // 01.00.05:L1485] were superseded by the rebuilds, and the rename at 02.00.00:L151 is a
        // no-op. The WITH NOCHECK and NOT FOR REPLICATION qualifiers have no EF Core equivalent and
        // are deliberately unrepresented. Multiple cascade paths into a table are acceptable here
        // because the model validator does not reject them - only migration generation objects, and
        // the baseline migration is intentionally inert under the schema-immutability rule.
        builder.HasOne(x => x.Portal)
            .WithMany(p => p.Roles)
            .HasForeignKey(x => x.PortalId)
            .HasConstraintName("FK_Roles_Portals")
            .OnDelete(DeleteBehavior.Cascade);

        // MIGRATION: FK_Roles_RoleGroups declares NO ON DELETE clause at all [04.00.04:L69-70,
        // equivalently 03.02.03:L36-37], so its terminal behaviour is to take no action - a
        // deliberate contrast with the cascade above. A role group cannot be removed while a role
        // still points at it, and removing one never removes its roles.
        //
        // This association is declared here, on the dependent, rather than from the group, so that
        // it is configured exactly once. Configurations are discovered by assembly scan with no
        // ordering guarantee, so declaring it from both ends would leave whichever ran last to
        // decide the delete behaviour, with neither a build error nor a model-validation error to
        // reveal it. Omitting it is equally unsafe: EF Core would infer the key by convention and
        // apply its own default for an optional association, which is not what the schema declares.
        //
        // Requiredness is derived from the key properties and is not restated on either association:
        // the portal key is configured required above, so that association is required, and the
        // group key is genuinely nullable, so this one is optional.
        builder.HasOne(x => x.RoleGroup)
            .WithMany(g => g.Roles)
            .HasForeignKey(x => x.RoleGroupId)
            .HasConstraintName("FK_Roles_RoleGroups")
            .OnDelete(DeleteBehavior.NoAction);

        // MIGRATION: no further association is declared here, and no principal-side collection is
        // declared at all. A role is the principal of the user-assignment table and of the two
        // permission tables, and each of those three is configured by its own dependent so the
        // ownership rule above holds uniformly. The two permission associations are deliberately
        // configured to take no action, because the terminal schema contains no physical foreign key
        // from either permission table back to this one.
        //
        // MIGRATION: no classification column is mapped, because none exists. The pending, active
        // and expired classification of a membership is derived from the effective and expiry dates
        // carried on the user-assignment rows, so it describes an assignment and not a role
        // definition; no such column appears on this table, or on that one, in any of the
        // eighty-eight scripts. Nor is any joined display value mapped - the owning portal's name,
        // the group's name and member counts are projections belonging to the layer above, not
        // columns here.
        //
        // MIGRATION: the abstract identity member the base type declares is get-only and so is
        // naturally excluded by EF Core's writability requirement. It is deliberately not suppressed
        // explicitly, because suppressing it would imply it had otherwise been mapped.
    }
}
