namespace DnnMigration.Domain.Common;

// MIGRATION: This base type is net-new; the legacy VB.NET model has no predecessor for it. The
// legacy business objects each declare "Public Class <Name>" with no Inherits clause, so there was
// no common base to port - verified in Library/Components/Portal/PortalInfo.vb (line 29) and
// Library/Components/Users/UserInfo.vb (line 41).
//
// MIGRATION: The decoration those legacy classes carried is deliberately not carried forward.
// PortalInfo.vb decorates the type with an XML-serialisation root attribute (line 29) and its
// properties with XML element attributes (PortalID at line 85); UserInfo.vb decorates properties
// with Web-Forms editor and validation attributes (lines 104, 121, 144, 178 and 301) imported from
// DotNetNuke.UI.WebControls, a tree this migration excludes. In the target the wire contract
// belongs to the DTOs at the API boundary and request validation belongs to the Application layer,
// so a domain entity carries no attribute of any kind.
//
// MIGRATION: No "has this been saved yet" convenience member exists on this type, and none may be
// added. dbo.Portals.PortalID is declared IDENTITY(-1, 1), and dbo.Roles.RoleID, dbo.Tabs.TabID
// and dbo.Modules.ModuleID are each declared IDENTITY(0, 1), in the baseline schema script
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider at lines 77, 115, 140
// and 221 respectively. The default value of TId is therefore a genuine persisted identity for
// four in-scope aggregates, and -1 is simultaneously a real portal identity and the legacy
// Null.NullInteger marker for "no value" - a collision the domain deliberately does not resolve.

/// <summary>
/// Identity-bearing base type for the domain entities of the DnnMigration model, supplying
/// identity-based equality without dictating how the identity is stored or named.
/// </summary>
/// <remarks>
/// <para>
/// The single responsibility of this type is identity-based equality. It deliberately declares no
/// identity storage of its own: the identity is exposed abstractly through <see cref="Identity"/>
/// so that every derived entity keeps the legacy-named property that the existing SQL Server
/// schema actually uses - <c>PortalID</c>, <c>ModuleID</c>, <c>TabID</c>, <c>UserID</c>,
/// <c>RoleID</c>, <c>PermissionID</c>, <c>ModuleDefID</c>, <c>UserRoleID</c>, <c>RoleGroupID</c>
/// and so on - as the one real, mapped column.
/// </para>
/// <para>
/// A base class that forced a single <c>Id</c> member onto every entity would break that. The
/// schema is immutable for this migration and legacy column naming is honoured through the Fluent
/// entity configurations in the Infrastructure layer, so each aggregate keeps its own identity
/// name. A member literally named <c>Id</c> would additionally be claimed by EF Core's
/// by-convention primary-key discovery and would then compete with the explicit
/// <c>HasKey(...)</c> call that each of those configurations makes.
/// </para>
/// <para>
/// Why there is no "not yet persisted" helper: measured against the baseline schema script
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c>, four of the
/// in-scope tables seed their identity column at a value that other codebases reserve for
/// "unsaved".
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Table and column</term>
///     <description>Seed and consequence</description>
///   </listheader>
///   <item>
///     <term>dbo.Portals.PortalID (script line 77)</term>
///     <description>
///     IDENTITY(-1, 1). The first real portal row is identified by -1, which is simultaneously the
///     legacy <c>Null.NullInteger</c> sentinel, so -1 may never be read as "absent".
///     </description>
///   </item>
///   <item>
///     <term>dbo.Roles.RoleID (script line 115)</term>
///     <description>IDENTITY(0, 1). Zero is a legitimate, persisted role identity.</description>
///   </item>
///   <item>
///     <term>dbo.Tabs.TabID (script line 140)</term>
///     <description>IDENTITY(0, 1). Zero is a legitimate, persisted tab identity.</description>
///   </item>
///   <item>
///     <term>dbo.Modules.ModuleID (script line 221)</term>
///     <description>IDENTITY(0, 1). Zero is a legitimate, persisted module identity.</description>
///   </item>
/// </list>
/// <para>
/// Consequently this type offers no transience predicate of any kind and compares
/// <typeparamref name="TId"/> against no reserved value whatsoever. Because the default value of
/// <typeparamref name="TId"/> is a valid persisted identity in this schema, any such helper would
/// silently misclassify the first row of three aggregates and every portal identified by -1. Do
/// not add one: whether an entity has been persisted is a question for the persistence layer's
/// change tracker, never for the domain model.
/// </para>
/// <para>
/// The legacy null sentinels (-1 for the integral types, 255 for a byte, <c>MinValue</c> for the
/// floating-point and decimal types, the earliest representable date, and the empty string for
/// text) are for the same reason not represented here. The domain expresses absence with nullable
/// CLR types; sentinel semantics are preserved only at the DTO and API boundary, where the wire
/// contract is externally observable.
/// </para>
/// <para>
/// Entities with composite keys - the module-setting and tab-module-setting key/value pairs - are
/// under no obligation to derive from this type. One scalar identity cannot express a composite
/// key, and nothing here demands one: the base is intentionally permissive and mapping-agnostic.
/// </para>
/// </remarks>
/// <typeparam name="TId">
/// The CLR type of the entity's identity. Constrained to <c>notnull</c> rather than to
/// <c>struct</c> so that the constraint also admits the identity wrapper types that live alongside
/// the entities; every identity in the current schema happens to be a 32-bit integer. Equality is
/// evaluated through <see cref="EqualityComparer{T}"/>, so any type with sane equality is
/// acceptable.
/// </typeparam>
/// <example>
/// A derived entity keeps its legacy-named property and forwards it:
/// <code>
/// public sealed class Portal : Entity&lt;int&gt;
/// {
///     public int PortalID { get; set; }
///
///     public override int Identity => PortalID;
/// }
/// </code>
/// </example>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>
    /// Initialises a new instance of the <see cref="Entity{TId}"/> class.
    /// </summary>
    /// <remarks>
    /// Parameterless and protected on purpose. The identity is not a constructor argument: derived
    /// entities expose it as a settable, legacy-named property that the object-relational mapper
    /// assigns after construction, so requiring it here would obstruct materialisation for no
    /// benefit.
    /// </remarks>
    protected Entity()
    {
    }

    /// <summary>
    /// Gets the value that identifies this entity for the purposes of equality.
    /// </summary>
    /// <value>
    /// The implementing entity's own legacy-named identity property - <c>PortalID</c> for a
    /// portal, <c>ModuleID</c> for a module, <c>TabID</c> for a tab, <c>UserID</c> for a user,
    /// <c>RoleID</c> for a role, <c>PermissionID</c> for a permission, and so on.
    /// </value>
    /// <remarks>
    /// <para>
    /// This member exists for equality alone; it is not a persistence mapping. It is get-only, it
    /// carries no attribute, and no entity configuration binds it to a column - each configuration
    /// maps the underlying legacy-named property and names that property in an explicit
    /// <c>HasKey(...)</c> call instead.
    /// </para>
    /// <para>
    /// The name avoids <c>Id</c>, <c>ID</c>, <c>Key</c> and every <c>&lt;TypeName&gt;Id</c> form on
    /// purpose. Any of those would be claimed by EF Core's by-convention primary-key discovery,
    /// which would compete with the explicit configuration and, for the composite-key settings
    /// entities, simply be wrong.
    /// </para>
    /// </remarks>
    public abstract TId Identity { get; }

    /// <summary>
    /// Determines whether two entities are the same entity.
    /// </summary>
    /// <param name="left">The first entity to compare, which may be <see langword="null"/>.</param>
    /// <param name="right">The second entity to compare, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when both operands are <see langword="null"/>, or when both are
    /// non-null and <see cref="Equals(Entity{TId})"/> reports them equal; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.Equals(right);
    }

    /// <summary>
    /// Determines whether two entities are different entities.
    /// </summary>
    /// <param name="left">The first entity to compare, which may be <see langword="null"/>.</param>
    /// <param name="right">The second entity to compare, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the operands are not the same entity; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);

    /// <summary>
    /// Determines whether the specified entity is the same entity as this one.
    /// </summary>
    /// <param name="other">
    /// The entity to compare with this entity, which may be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="other"/> is non-null, has exactly the same
    /// runtime type as this entity, and carries an equal <see cref="Identity"/>; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The runtime types are compared with <see cref="object.GetType"/> rather than with a type
    /// test, so two different aggregates that happen to share an identity value - a portal and
    /// a role both identified by 5, for instance - are never equal, and a persistence proxy or a
    /// further-derived type never compares equal across an aggregate boundary.
    /// </remarks>
    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (GetType() != other.GetType())
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Identity, other.Identity);
    }

    /// <summary>
    /// Determines whether the specified object is the same entity as this one.
    /// </summary>
    /// <param name="obj">
    /// The object to compare with this entity, which may be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="obj"/> is an entity of exactly this runtime type
    /// carrying an equal <see cref="Identity"/>; otherwise <see langword="false"/>.
    /// </returns>
    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(Entity{TId})"/>.
    /// </summary>
    /// <returns>
    /// A hash code combining this entity's runtime type with its <see cref="Identity"/>, so that
    /// any two instances reported as equal always produce the same value.
    /// </returns>
    /// <remarks>
    /// The identity of a persisted entity is stable, so an entity may safely be used as a
    /// dictionary key or placed in a hash set. Replacing the property behind
    /// <see cref="Identity"/> after such use invalidates the placement, exactly as it would for any
    /// other mutable key.
    /// </remarks>
    public override int GetHashCode() => HashCode.Combine(GetType(), Identity);
}
