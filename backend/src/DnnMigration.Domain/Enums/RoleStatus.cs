namespace DnnMigration.Domain.Enums;

/// <summary>
/// The temporal state of a single user-to-role assignment, derived at read time
/// from that assignment's effective and expiry dates.
/// </summary>
/// <remarks>
/// <para>
/// NET-NEW TYPE WITH NO LEGACY ANCESTOR. Unlike every other enumeration in this
/// folder, this one has no legacy counterpart of any kind: no VB.NET
/// enumeration, no database column, no discriminator integer, not even a label
/// on an administration screen. Three independent negative proofs were re-run
/// against the checkout before it was authored, and a fourth was found while
/// doing so:
/// </para>
/// <list type="number">
/// <item>
/// The identifier exists in no legacy source file. Searching every
/// <c>*.vb</c> file beneath <c>Library/</c> and <c>Website/</c> for
/// "RoleStatus" returns zero hits, as does an extension-agnostic sweep of both
/// trees.
/// </item>
/// <item>
/// The identifier exists nowhere in the schema. Searching all 88
/// <c>*.SqlDataProvider</c> upgrade scripts for "RoleStatus" returns zero hits.
/// </item>
/// <item>
/// Neither table carries a status column and neither legacy type carries a
/// status property. The terminal <c>UserRoles</c> shape is <c>UserRoleID</c>,
/// <c>UserID</c>, <c>RoleID</c>, <c>ExpiryDate</c> and <c>IsTrialUsed</c> from
/// the baseline at <c>01.00.00.SqlDataProvider:L238</c>, plus
/// <c>EffectiveDate datetime NULL</c> added at
/// <c>03.02.03.SqlDataProvider:L380</c> and re-applied under an existence guard
/// at <c>04.00.04.SqlDataProvider:L418</c>; every other statement touching the
/// table only adds or drops a constraint. <c>RoleInfo.vb</c> exposes exactly 15
/// properties and <c>UserRoleInfo.vb</c> exactly 8, and not one of the 23 is
/// named Status.
/// </item>
/// <item>
/// The legacy role-assignment screen shows no status either:
/// <c>securityroles.ascx</c> and its resource file label only "Effective Date"
/// and "Expiry Date".
/// </item>
/// </list>
/// <para>
/// This type therefore replaces no named legacy type. It replaces the inline
/// date comparisons that the legacy code used in place of one, and it exists so
/// that those comparisons are written down once, under names, instead of being
/// repeated at every call site.
/// </para>
/// <para>
/// COMPUTED, NEVER PERSISTED - A DIRECT INSTRUCTION TO THE INFRASTRUCTURE
/// LAYER. No <c>UserRoles</c> or <c>Roles</c> column backs this value, so no
/// entity configuration may map it: do not call <c>HasColumnName</c>,
/// <c>HasConversion</c> or <c>Property</c> for it in
/// <c>UserRoleConfiguration</c>, <c>RoleConfiguration</c> or anywhere else.
/// Mapping it would oblige the baseline migration to create a column that the
/// 88-script chain never creates, which is exactly what the immutable-schema
/// rule forbids. The consuming entity must expose it as a read-only computed
/// property with no setter, which the object-relational mapper ignores of its
/// own accord; the NotMapped annotation is deliberately absent here because
/// this layer takes no package or project reference of any kind.
/// </para>
/// <para>
/// THE ORDINAL IS MEANINGLESS. No explicit numeric values are assigned,
/// precisely so that nothing can read significance into the ordering, and the
/// declaration order below is lifecycle order rather than any stored code.
/// Never persist the ordinal, never send it over the wire, and never compare
/// two members by magnitude. At the outward boundary this value is serialised
/// by name, as a string; that conversion belongs to the central serialisation
/// options configured where the HTTP contract is composed, and never to an
/// annotation on this type.
/// </para>
/// <para>
/// DERIVATION - the specification the consuming entity implements. Three legacy
/// authorities agree on the rules, and all three were read in full:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>RoleController.vb:L530-L535</c> normalises the two dates on every write:
/// "If EffectiveDate &lt; Now Then EffectiveDate = Null.NullDate" and
/// "If ExpiryDate &lt; Now Then ExpiryDate = Now". The effective date gates the
/// start of an assignment and the expiry date gates its end.
/// </item>
/// <item>
/// <c>RoleController.vb:L493-L501</c> is the cancellation path. When the role
/// carries a service fee and the trial has been used, the assignment is expired
/// by setting the expiry date to yesterday, rather than deleted, so that the
/// trial-usage row survives; otherwise the row is deleted outright. An expired
/// assignment is thus a real, retained state in this data model and not merely
/// the absence of a row - which is the whole reason <c>Expired</c> must exist.
/// </item>
/// <item>
/// The stored procedure <c>GetRolesByUser</c> expresses the in-force test in
/// SQL at <c>03.02.03.SqlDataProvider:L405-L406</c> and again at
/// <c>04.00.04.SqlDataProvider:L443-L444</c>: effective date at or before now
/// or unset, AND expiry date at or after now or unset. Note that both bounds
/// are inclusive, so an assignment whose date falls exactly on the current
/// instant is in force, not out of it. That procedure only ever selects the
/// in-force rows; it never names the other two states, and naming them is
/// what this type adds.
/// </item>
/// </list>
/// <para>
/// Evaluate in this order, which makes the classification both total and
/// mutually exclusive. First, if the expiry date is set and is strictly before
/// now, the result is <c>Expired</c>: expiry is terminal and outranks a start
/// date that has not yet arrived. That combination really is reachable, because
/// the cancellation path above back-dates the expiry date without touching the
/// effective date. Second, if the effective date is set and is strictly after
/// now, the result is <c>Pending</c>. Otherwise the result is <c>Active</c>,
/// which is also the answer when both dates are unset.
/// </para>
/// <para>
/// USE THE INJECTED CLOCK. "Now" above means the value read from the
/// <c>IClock</c> abstraction declared in this project under
/// <c>Abstractions/Services/</c>, injected into whichever type performs the
/// classification - normally the assignment entity itself. Never call
/// <c>DateTime.Now</c> or <c>DateTime.UtcNow</c> directly: the legacy code read
/// the ambient clock inline, which is exactly why its date handling could not
/// be tested, and a substitutable clock is the reason the abstraction exists.
/// A test can then pin the instant and assert all three outcomes.
/// </para>
/// <para>
/// "NO DATE SET" IS TWO VALUES HERE, NOT ONE. The legacy columns are nullable
/// but the legacy properties are not, so absence was carried as the sentinel
/// <c>Null.NullDate</c>, which is <c>DateTime.MinValue</c> - see
/// <c>Null.vb:L66-L70</c>. The legacy emptiness test at <c>Null.vb:L183-L186</c>
/// compares only the date part, commented there as avoiding subtle time
/// differences, and <c>SecurityRoles.ascx.vb:L281-L285</c> uses exactly that
/// test before displaying either date. The computed property must therefore
/// treat both a null value and <c>DateTime.MinValue</c> as "not set", comparing
/// the date part, or a migrated row will classify differently from its legacy
/// self.
/// </para>
/// <para>
/// THE TRIAL FLAG IS DELIBERATELY NOT FOLDED IN. <c>IsTrialUsed</c> is a
/// nullable bit on <c>UserRoles</c> and an orthogonal fact: an assignment can
/// be in force and have used its trial at the same time. It stays a separate
/// nullable boolean property on the assignment entity. Folding it into this
/// type would invent a lifecycle the legacy data model does not have.
/// </para>
/// <para>
/// MEMBERS CONSIDERED AND REJECTED. Cancelled, because cancellation resolves to
/// either an expiry or a deleted row and never to a state of its own. Trial,
/// for the orthogonality reason just given. Unknown, NotSet, Undefined and
/// None, because the three rules above cover every combination of the two
/// dates, leaving no unclassifiable case for such a member to describe. No
/// member is valued minus one, a number that is already overloaded in this
/// codebase as both the integer absence sentinel and a live identity seed.
/// </para>
/// </remarks>
// MIGRATION: NET-NEW. This enumeration has NO legacy counterpart: "RoleStatus"
//   appears zero times in Library and Website VB.NET sources, zero times across
//   all 88 SqlDataProvider upgrade scripts, there is no status column on
//   UserRoles, and neither RoleInfo.vb's 15 properties nor UserRoleInfo.vb's 8
//   include one. It replaces the inline date comparisons at
//   RoleController.vb:L530-L535 and the cancellation-to-expiry path at
//   RoleController.vb:L493-L501 with a named classification. It is COMPUTED
//   from the assignment's EffectiveDate and ExpiryDate via the injected IClock
//   and MUST NOT be persisted or mapped by any entity configuration. The trial
//   flag is deliberately NOT folded in. Recorded in MIGRATION_NOTES.md.
public enum RoleStatus
{
    /// <summary>
    /// The assignment has been made but is not yet in force: its effective date
    /// is set and falls strictly after the current instant. Derived from the
    /// guard at <c>RoleController.vb:L530</c>, which leaves a future effective
    /// date in place instead of clearing it, and from the inclusive
    /// effective-date test in <c>GetRolesByUser</c>.
    /// </summary>
    Pending,

    /// <summary>
    /// The assignment is in force: its effective date is unset or at or before
    /// the current instant, and its expiry date is unset or at or after the
    /// current instant. This is also the result when both dates are unset.
    /// Mirrors the in-force predicate of <c>GetRolesByUser</c> at
    /// <c>03.02.03.SqlDataProvider:L405-L406</c>.
    /// </summary>
    Active,

    /// <summary>
    /// The assignment has ended: its expiry date is set and falls strictly
    /// before the current instant. This is a retained state rather than a
    /// deleted row - the cancellation path at
    /// <c>RoleController.vb:L493-L501</c> back-dates the expiry date by one day
    /// precisely so that the trial-usage row is kept. Being terminal, it
    /// outranks a not-yet-arrived effective date.
    /// </summary>
    Expired
}
