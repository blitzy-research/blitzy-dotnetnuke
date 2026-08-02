namespace DnnMigration.Domain.Enums;

/// <summary>
/// The temporal state of a single user-to-role assignment, derived at read time from that
/// assignment's effective and expiry dates.
/// </summary>
/// <remarks>
/// <para>
/// Computed, never persisted. No <c>UserRoles</c> or <c>Roles</c> column backs this value, so no
/// entity configuration may map it - not with <c>Property</c>, <c>HasColumnName</c> or
/// <c>HasConversion</c>, anywhere. Mapping it would oblige the baseline migration to create a column
/// the 88-script chain never creates, which the immutable-schema rule forbids. The consuming entity
/// must expose it as a read-only computed property with no setter, which the object-relational
/// mapper ignores of its own accord; a not-mapped annotation is deliberately absent because this
/// layer takes no package or project reference of any kind.
/// </para>
/// <para>
/// The ordinal is meaningless. No explicit numeric values are assigned, precisely so that nothing
/// can read significance into the ordering, and declaration order is lifecycle order rather than a
/// stored code. Never persist the ordinal, send it over the wire or compare two members by
/// magnitude; at the outward boundary this value is serialised by name, through the central
/// serialisation options rather than an annotation on this type.
/// </para>
/// <para>
/// Derivation - the specification a consuming entity must implement, and on which three legacy
/// authorities agree. <c>RoleController.vb</c> L530-L535 normalises both dates on every write, so
/// the effective date gates the start of an assignment and the expiry date gates its end;
/// <c>RoleController.vb</c> L493-L501 expires an assignment by back-dating its expiry date instead
/// of deleting the row whenever the role carries a service fee and the trial has been used, which is
/// why <c>Expired</c> must exist as a retained state; and the <c>GetRolesByUser</c> procedure
/// expresses the in-force test in SQL with both bounds inclusive, so an assignment whose date falls
/// exactly on the current instant is in force.
/// </para>
/// <para>
/// Evaluate in this order, which makes the classification total and mutually exclusive. If the
/// expiry date is set and strictly before now, the result is <c>Expired</c> - expiry is terminal and
/// outranks a start date that has not yet arrived, a combination that really is reachable because
/// the cancellation path back-dates expiry without touching the effective date. Otherwise, if the
/// effective date is set and strictly after now, the result is <c>Pending</c>. Otherwise the result
/// is <c>Active</c>, which is also the answer when both dates are unset.
/// </para>
/// <para>
/// Two subtleties the classifier must honour. "Now" means the value read from the <c>IClock</c>
/// abstraction in this project, injected into whichever type performs the classification; never call
/// <c>DateTime.Now</c> or <c>DateTime.UtcNow</c> directly, because the legacy code read the ambient
/// clock inline and that is exactly why its date handling could not be tested. And "no date set" is
/// two values, not one: the legacy columns are nullable but the legacy properties were not, so
/// absence was carried as the <c>DateTime.MinValue</c> sentinel, and the legacy emptiness test
/// compared only the date part. A migrated row will classify differently from its legacy self unless
/// both a null and <c>DateTime.MinValue</c> are treated as unset, comparing the date part.
/// </para>
/// <para>
/// The trial flag is deliberately not folded in. <c>IsTrialUsed</c> is an orthogonal nullable bit on
/// <c>UserRoles</c> - an assignment can be in force and have used its trial at once - so it stays a
/// separate property. Folding it in would invent a lifecycle the legacy data model does not have.
/// </para>
/// </remarks>
// MIGRATION: this enumeration has no legacy counterpart of any kind: no VB.NET enum, no column on
// UserRoles, no discriminator and no label on an administration screen. It replaces the inline date
// comparisons at RoleController.vb L530-L535 and the cancellation-to-expiry path at L493-L501 with
// one named classification, so the status is derived and never persisted.
public enum RoleStatus
{
    /// <summary>
    /// The assignment has been made but is not yet in force: its effective date is set and falls
    /// strictly after the current instant. Derived from the guard at <c>RoleController.vb</c> L530,
    /// which leaves a future effective date in place instead of clearing it, and from the inclusive
    /// effective-date test in <c>GetRolesByUser</c>.
    /// </summary>
    Pending,

    /// <summary>
    /// The assignment is in force: its effective date is unset or at or before the current instant,
    /// and its expiry date is unset or at or after it. This is also the result when both dates are
    /// unset. Mirrors the in-force predicate of <c>GetRolesByUser</c>.
    /// </summary>
    Active,

    /// <summary>
    /// The assignment has ended: its expiry date is set and falls strictly before the current instant.
    /// This is a retained state rather than a deleted row - the cancellation path at
    /// <c>RoleController.vb</c> L493-L501 back-dates the expiry date by one day precisely so the
    /// trial-usage row is kept. Being terminal, it outranks a not-yet-arrived effective date.
    /// </summary>
    Expired
}
