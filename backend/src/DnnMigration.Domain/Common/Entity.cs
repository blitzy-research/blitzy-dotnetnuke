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
// MIGRATION: Whether an entity has been written to the database is never DEDUCED from its identity
// value on this type, and no member may ever be added that deduces it. dbo.Portals.PortalID is
// declared IDENTITY(-1, 1), and dbo.Roles.RoleID, dbo.Tabs.TabID and dbo.Modules.ModuleID are each
// declared IDENTITY(0, 1), in the baseline schema script
// Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider at lines 77, 115, 140
// and 221 respectively. The default value of TId is therefore a genuine persisted identity for
// four in-scope aggregates, and -1 is simultaneously a real portal identity and the legacy
// Null.NullInteger marker for "no value" - a collision the domain deliberately does not resolve.
//
// MIGRATION: Persisted state is instead DECLARED, exactly once, by the layer that owns the answer.
// MarkIdentityPersisted records the declaration and IdentityIsPersisted reads it back. That
// inverts the usual arrangement on purpose: because this schema makes every candidate "not saved
// yet" marker a real key, the only trustworthy source of the answer is the layer that actually read
// or wrote the row. The persistence layer therefore carries an obligation - call
// MarkIdentityPersisted on every entity it materialises from a row, and on every entity whose
// generated key it has just written back after a save. Domain and application code must not call
// it; they have no way of knowing.
//
// MIGRATION: Until that declaration is made, two separately constructed instances of the same
// runtime type are two different entities, compared by object reference. Comparing their identity
// values instead would report every freshly constructed role, tab and module as equal to every
// other of its kind, because dbo.Roles, dbo.Tabs and dbo.Modules all seed at zero, and would make a
// freshly constructed portal collide with the genuine portal identified by -1.

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
/// Why persisted state is declared rather than deduced: measured against the baseline schema script
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c>, four of the
/// in-scope tables seed their identity column at a value that other codebases reserve for a row
/// that has not been written yet.
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
/// Consequently this type compares <typeparamref name="TId"/> against no reserved value whatsoever
/// and draws no conclusion from its default. Any predicate that did so would silently misclassify
/// the first row of three aggregates and every portal identified by -1. What it does instead is take
/// the answer from the only layer that holds it: the persistence layer calls
/// <see cref="MarkIdentityPersisted"/> when it materialises a row, and again when it writes a
/// generated key back after a save, and <see cref="IdentityIsPersisted"/> reports the result.
/// </para>
/// <para>
/// Identity-based equality applies only once that declaration has been made on <b>both</b> operands.
/// Before it, two separately constructed instances of the same runtime type are two different
/// entities and are told apart by object reference, which is the only honest answer available while
/// their identity values are still whatever the caller happened to leave in them. The alternative -
/// comparing the raw values immediately - would report every newly constructed role, tab and module
/// as equal to every other of its kind, since all three tables seed at zero.
/// </para>
/// <para>
/// <see cref="GetHashCode"/> is latched on its first call and never changes afterwards, so an entity
/// placed in a hash set or used as a dictionary key does not lose itself when the database later
/// assigns its key. Latching alone would leave the hash code and
/// <see cref="Equals(Entity{TId})"/> free to disagree, because an entity whose hash was taken before
/// its key arrived would still start comparing by identity once the key did arrive; so an instance
/// whose hash was latched while its key was not yet the database's keeps comparing by reference for
/// the rest of its life. Equality and hashing therefore stay mutually consistent at every moment,
/// which is the contract that hash-based collections actually rely on. Instances are not safe for
/// concurrent mutation - they never were, since their identity properties are settable - so latch
/// and declare from a single thread, which is what a unit of work does anyway.
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
    /// Backs <see cref="IdentityIsPersisted"/>. Starts false for every newly constructed instance,
    /// including one the materialiser is in the middle of building, and is set only through
    /// <see cref="MarkIdentityPersisted"/>.
    /// </summary>
    private bool _identityIsPersisted;

    /// <summary>
    /// The hash code <see cref="GetHashCode"/> has already published, or <see langword="null"/> when
    /// it has not been asked yet. Latched on the first call so that the value can never change for
    /// the lifetime of this instance.
    /// </summary>
    private int? _latchedHashCode;

    /// <summary>
    /// Whether the value in <see cref="_latchedHashCode"/> was derived from <see cref="Identity"/>.
    /// Recorded at the same moment the hash code is latched, and meaningful only once it has been.
    /// </summary>
    private bool _latchedHashCodeUsedIdentity;

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
    /// Gets a value indicating whether the persistence layer has declared that
    /// <see cref="Identity"/> now holds the value the database holds for this entity.
    /// </summary>
    /// <value>
    /// <see langword="true"/> once <see cref="MarkIdentityPersisted"/> has been called; otherwise
    /// <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// This is a <b>declaration</b>, never a deduction. Nothing here inspects
    /// <see cref="Identity"/> or compares it with a reserved value, because in this schema no such
    /// value exists to compare against: <c>dbo.Portals.PortalID</c> seeds at -1, which is also the
    /// legacy null-integer marker, while <c>dbo.Roles.RoleID</c>, <c>dbo.Tabs.TabID</c> and
    /// <c>dbo.Modules.ModuleID</c> each seed at zero. Every candidate marker for "no row yet" is a
    /// real key belonging to a real row.
    /// </para>
    /// <para>
    /// Read it to understand which equality rule applies, not to decide whether to insert or update
    /// - that decision belongs to the persistence layer's change tracker, which knows far more than
    /// this flag does.
    /// </para>
    /// </remarks>
    public bool IdentityIsPersisted => _identityIsPersisted;

    /// <summary>
    /// Declares that <see cref="Identity"/> now holds the value the database holds, so that this
    /// entity may be compared with other instances of the same row by identity rather than by
    /// object reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the persistence layer at the two moments it knows the answer: immediately after
    /// materialising an entity from a row, and immediately after writing a generated key back
    /// following a save. Domain and application code must not call it, because neither can tell
    /// whether a row exists.
    /// </para>
    /// <para>
    /// One-way and idempotent. There is no counterpart that revokes the declaration: an entity does
    /// not stop having been written, and allowing the flag to fall back would let an entity change
    /// its equality class in the opposite direction, which is exactly the instability this design
    /// removes. Calling it repeatedly is harmless.
    /// </para>
    /// <para>
    /// Calling it after <see cref="GetHashCode"/> has already been asked for a value does <b>not</b>
    /// change that value, and consequently does not switch this instance over to identity-based
    /// equality either. That is deliberate: a hash code that changed under a live hash set would
    /// strand the entry, so the earlier answer wins and both operations stay consistent with it.
    /// Materialise-then-declare, before anything hashes the entity, and the ordinary identity rule
    /// applies throughout.
    /// </para>
    /// </remarks>
    public void MarkIdentityPersisted() => _identityIsPersisted = true;

    /// <summary>
    /// Gets a value indicating whether this instance may take part in identity-based equality: its
    /// identity has been declared persisted, and no earlier hash code has already committed it to
    /// reference-based comparison.
    /// </summary>
    private bool ComparesByIdentity =>
        _identityIsPersisted && (_latchedHashCode is null || _latchedHashCodeUsedIdentity);

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
    /// <see langword="true"/> when <paramref name="other"/> is the very same object as this one, or
    /// when it is non-null, has exactly the same runtime type, both entities have had their identity
    /// declared persisted through <see cref="MarkIdentityPersisted"/>, and the two identities are
    /// equal; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The runtime types are compared with <see cref="object.GetType"/> rather than with a type
    /// test, so two different aggregates that happen to share an identity value - a portal and
    /// a role both identified by 5, for instance - are never equal, and a persistence proxy or a
    /// further-derived type never compares equal across an aggregate boundary.
    /// </para>
    /// <para>
    /// Identity values are only consulted once <see cref="IdentityIsPersisted"/> holds on both sides.
    /// Reaching for them sooner is the classic mistake this schema punishes hardest: an entity that
    /// has never been written carries whatever its identity property was left at, and
    /// <c>dbo.Roles</c>, <c>dbo.Tabs</c> and <c>dbo.Modules</c> all seed their identity column at
    /// zero, so every newly constructed role would equal every other newly constructed role, and a
    /// newly constructed portal would equal the real portal identified by -1. Until the database has
    /// spoken, two separately constructed objects are two separate entities and only their references
    /// distinguish them.
    /// </para>
    /// <para>
    /// An instance whose hash code was already published while its identity was not yet the
    /// database's stays on reference comparison permanently, so that the hash code it published
    /// remains a valid one for it. See <see cref="GetHashCode"/>.
    /// </para>
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
    /// A hash code combining this entity's runtime type with its <see cref="Identity"/> when the
    /// identity has been declared persisted; otherwise the reference-based hash code of this object.
    /// Either way, any two instances reported as equal always produce the same value.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Latched on the first call and constant thereafter. Without that, an entity constructed in
    /// memory, placed in a hash set, and only then given its generated key by the database would
    /// answer with a different bucket than the one it was filed under, and would become unfindable in
    /// a collection that still contains it. Latching costs one nullable field and removes the whole
    /// failure mode.
    /// </para>
    /// <para>
    /// Because the value is latched, the equality rule has to follow it rather than lead it: an
    /// instance that published a reference-based hash code keeps comparing by reference even after
    /// its identity is declared persisted, so it can never be reported equal to another instance
    /// whose hash code was derived from that same identity. The pair therefore never disagrees, which
    /// is the invariant hash-based collections depend on. The straightforward ordering - materialise
    /// the entity, declare its identity persisted, then hash it - takes the identity-based path
    /// throughout and needs none of this care.
    /// </para>
    /// <para>
    /// Replacing the property behind <see cref="Identity"/> after the value has been latched does not
    /// move the entity either, which is a further reason the identity of a written row should be
    /// treated as settled.
    /// </para>
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
