namespace DnnMigration.Domain.Common;

/// <summary>
/// Identity-bearing base type for the domain entities of the DnnMigration model, supplying identity-based
/// equality without dictating how the identity is stored or named.
/// </summary>
/// <remarks>
/// <para>
/// The single responsibility of this type is identity-based equality.
/// </para>
/// <para>
/// A base class that forced a single <c>Id</c> member onto every entity would break that. The schema is
/// immutable for this migration and legacy column naming is honoured through the Fluent entity
/// configurations in the Infrastructure layer, so each aggregate keeps its own identity name.
/// </para>
/// </remarks>
/// <typeparam name="TId">The CLR type of the entity's identity.</typeparam>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>
    /// Backs <see cref="IdentityIsPersisted"/>. Starts false for every newly constructed instance,
    /// including one the materialiser is in the middle of building, and is set only through <see
    /// cref="MarkIdentityPersisted"/>.
    /// </summary>
    private bool _identityIsPersisted;

    /// <summary>
    /// The hash code <see cref="GetHashCode"/> has already published, or <see langword="null"/> when it has
    /// not been asked yet. Latched on the first call so that the value can never change for the lifetime of
    /// this instance.
    /// </summary>
    private int? _latchedHashCode;

    /// <summary>
    /// Whether the value in <see cref="_latchedHashCode"/> was derived from <see cref="Identity"/>.
    /// Recorded at the same moment the hash code is latched, and meaningful only once it has been.
    /// </summary>
    private bool _latchedHashCodeUsedIdentity;

    /// <summary>Initialises a new instance of the <see cref="Entity{TId}"/> class.</summary>
    /// <remarks>
    /// Parameterless and protected on purpose. The identity is not a constructor argument: derived entities
    /// expose it as a settable, legacy-named property that the object-relational mapper assigns after
    /// construction, so requiring it here would obstruct materialisation for no benefit.
    /// </remarks>
    protected Entity()
    {
    }

    /// <summary>Gets the value that identifies this entity for the purposes of equality.</summary>
    /// <value>
    /// The implementing entity's own legacy-named identity property - <c>PortalID</c> for a portal,
    /// <c>ModuleID</c> for a module, <c>TabID</c> for a tab, <c>UserID</c> for a user, <c>RoleID</c> for a
    /// role, <c>PermissionID</c> for a permission, and so on.
    /// </value>
    /// <remarks>
    /// This member exists for equality alone; it is not a persistence mapping. It is get-only, it carries
    /// no attribute, and no entity configuration binds it to a column - each configuration maps the
    /// underlying legacy-named property and names that property in an explicit <c>HasKey(...)</c> call
    /// instead.
    /// </remarks>
    public abstract TId Identity { get; }

    /// <summary>
    /// Gets a value indicating whether the persistence layer has declared that <see cref="Identity"/> now
    /// holds the value the database holds for this entity.
    /// </summary>
    /// <value>
    /// <see langword="true"/> once <see cref="MarkIdentityPersisted"/> has been called; otherwise <see
    /// langword="false"/>.
    /// </value>
    /// <remarks>
    /// This is a <b>declaration</b>, never a deduction. Nothing here inspects <see cref="Identity"/> or
    /// compares it with a reserved value, because in this schema no such value exists to compare against:
    /// <c>dbo.Portals.PortalID</c> seeds at -1, which is also the legacy null-integer marker, while
    /// <c>dbo.Roles.RoleID</c>, <c>dbo.Tabs.TabID</c> and <c>dbo.Modules.ModuleID</c> each seed at zero.
    /// </remarks>
    public bool IdentityIsPersisted => _identityIsPersisted;

    /// <summary>
    /// Declares that <see cref="Identity"/> now holds the value the database holds, so that this entity may
    /// be compared with other instances of the same row by identity rather than by object reference.
    /// </summary>
    /// <remarks>
    /// One-way and idempotent. There is no counterpart that revokes the declaration: an entity does not
    /// stop having been written, and allowing the flag to fall back would let an entity change its equality
    /// class in the opposite direction, which is exactly the instability this design removes.
    /// </remarks>
    public void MarkIdentityPersisted() => _identityIsPersisted = true;

    /// <summary>
    /// Gets a value indicating whether this instance may take part in identity-based equality: its identity
    /// has been declared persisted, and no earlier hash code has already committed it to reference-based
    /// comparison.
    /// </summary>
    private bool ComparesByIdentity =>
        _identityIsPersisted && (_latchedHashCode is null || _latchedHashCodeUsedIdentity);

    /// <summary>Determines whether two entities are the same entity.</summary>
    /// <param name="left">The first entity to compare, which may be <see langword="null"/>.</param>
    /// <param name="right">The second entity to compare, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when both operands are <see langword="null"/>, or when both are non-null and
    /// <see cref="Equals(Entity{TId})"/> reports them equal; otherwise <see langword="false"/>.
    /// </returns>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.Equals(right);
    }

    /// <summary>Determines whether two entities are different entities.</summary>
    /// <param name="left">The first entity to compare, which may be <see langword="null"/>.</param>
    /// <param name="right">The second entity to compare, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the operands are not the same entity; otherwise <see langword="false"/>.
    /// </returns>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);

    /// <summary>Determines whether the specified entity is the same entity as this one.</summary>
    /// <param name="other">The entity to compare with this entity, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="other"/> is the very same object as this one, or when it
    /// is non-null, has exactly the same runtime type, both entities have had their identity declared
    /// persisted through <see cref="MarkIdentityPersisted"/>, and the two identities are equal; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The runtime types are compared with <see cref="object.GetType"/> rather than with a type test, so
    /// two different aggregates that happen to share an identity value - a portal and a role both
    /// identified by 5, for instance - are never equal, and a persistence proxy or a further-derived type
    /// never compares equal across an aggregate boundary.
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

        if (!ComparesByIdentity || !other.ComparesByIdentity)
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Identity, other.Identity);
    }

    /// <summary>Determines whether the specified object is the same entity as this one.</summary>
    /// <param name="obj">The object to compare with this entity, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="obj"/> is the very same object, or an entity of exactly
    /// this runtime type whose identity has been declared persisted on both sides and is equal; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    /// <summary>Returns a hash code consistent with <see cref="Equals(Entity{TId})"/>.</summary>
    /// <returns>
    /// A hash code combining this entity's runtime type with its <see cref="Identity"/> when the identity
    /// has been declared persisted at the moment of the first call; otherwise the reference-based hash code
    /// of this object - which, because nothing declares the identity automatically, is what a materialised
    /// entity answers.
    /// </returns>
    /// <remarks>
    /// Latched on the first call and constant thereafter. Without that, an entity that was hashed and filed
    /// in a hash set before its identity was declared persisted would afterwards answer with a different
    /// bucket than the one it was filed under, and would become unfindable in a collection that still
    /// contains it.
    /// </remarks>
    public override int GetHashCode()
    {
        if (_latchedHashCode is null)
        {
            _latchedHashCodeUsedIdentity = _identityIsPersisted;
            _latchedHashCode = _latchedHashCodeUsedIdentity
                ? HashCode.Combine(GetType(), Identity)
                : base.GetHashCode();
        }

        return _latchedHashCode.Value;
    }
}
