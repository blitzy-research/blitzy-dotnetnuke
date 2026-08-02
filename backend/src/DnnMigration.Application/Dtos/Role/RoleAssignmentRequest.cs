namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/portals/{portalId}/roles/{roleId}/users</c>: the user to
/// assign to a role, together with the optional dates that bound the membership.
/// </summary>
/// <remarks>
/// <para>
/// Collapses the three measured legacy assignment members - <c>RoleController.vb</c> lines 277, 295
/// and 647 - and the assigning half of the two revision members at lines 472 and 489. The measured
/// behaviour at lines 295 to 315 is an <em>upsert</em>: it inserts when the user does not yet hold
/// the role and otherwise revises the two dates. The owning member is deliberately idempotent in
/// the same way, so submitting this request twice revises rather than duplicates.
/// </para>
/// <para>
/// The portal and the role are taken from the route and are not carried here, for the reason
/// recorded on <see cref="UpdateRoleRequest"/>: <c>IRoleService</c> requires only that any
/// identifier the request carries must agree with the route, and carrying none satisfies that
/// requirement unconditionally while keeping the tenant unrestatable by a caller.
/// </para>
/// <para>
/// <b>Expiry derivation happens in the service, not here.</b> When <see cref="ExpiryDate"/> is
/// omitted the service derives one from the role's trial and billing terms, reproducing
/// <c>RoleController.vb</c> lines 503 to 558 exactly: an absent period yields no expiry at all; an
/// effective date already in the past is cleared and an expiry already in the past is advanced to
/// the current instant so the offset runs forward from now; the trial terms govern only while the
/// trial is unconsumed and the trial frequency is not the never code, otherwise the billing terms
/// govern; and the frequency code then selects the offset, with the never code yielding no expiry,
/// the one-off code yielding the perpetual far-future date, and the day, week, month and year codes
/// offsetting by the period, by seven times the period in days, by months and by years. The current
/// instant comes from the injected clock rather than from an ambient reading, so the arithmetic is
/// pinned by unit tests.
/// </para>
/// <para>
/// <b>Sentinel dates are preserved at the boundary.</b> The legacy store represents "never expires"
/// with the minimum date value and "perpetual" with the explicit far-future date 9999-12-31. The
/// first is absence and is carried here as a null; the second is a real, externally observable value
/// that a legacy consumer reading the same row expects to see, and it is carried through unchanged.
/// Neither is silently converted into the other, per AAP Rule T7.
/// </para>
/// <para>
/// Two legacy affordances are deliberately absent. The notification switch the legacy shared member
/// carried is not reproduced, because outbound mail is out of scope, and neither is its dependency
/// on the ambient per-request composite. The trial-consumed flag is also absent: it is a fact the
/// service records about an assignment rather than an instruction a caller gives, so allowing a
/// caller to set it would let a member grant itself a fresh trial.
/// </para>
/// <para>
/// The type is an inert data carrier; field rules live in <c>Application/Validation</c>.
/// </para>
/// </remarks>
public sealed class RoleAssignmentRequest
{
    /// <summary>
    /// Identifier of the user to assign to the role. Required.
    /// </summary>
    /// <remarks>
    /// Target column <c>UserRoles.UserID int NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 240), constrained by
    /// <c>FK_UserRoles_Users</c> with <c>ON DELETE CASCADE</c>
    /// (<c>03.00.13.SqlDataProvider</c> lines 55 to 63), so deleting a user removes the assignment.
    /// </remarks>
    // MIGRATION: non-nullable, because the assignment has no meaning without a user and the column
    // is NOT NULL. Users.UserID is seeded IDENTITY(1,1), so a value below 1 cannot identify a user
    // and the validator rejects it - a different valid range from the role and portal identifiers
    // on this endpoint's route, which seed at 0 and -1 respectively. A user that does not exist is
    // reported by the service as user.not_found.
    //
    // MIGRATION: nothing in the schema makes the pair of user and role unique - there is no unique
    // constraint over UserRoles(UserID, RoleID) anywhere in the 88-script chain - so the store
    // would happily accept a duplicate assignment. The idempotent upsert is therefore a deliberate
    // service policy that reproduces the measured legacy behaviour, not something the database
    // enforces.
    public int UserId { get; set; }

    /// <summary>
    /// Instant from which the membership takes effect, or <see langword="null"/> for immediately.
    /// </summary>
    /// <remarks>
    /// Target column <c>UserRoles.EffectiveDate datetime NULL</c>, added at
    /// <c>03.02.03.SqlDataProvider</c> line 380 with a version-guarded re-add at
    /// <c>04.00.04.SqlDataProvider</c> lines 415 to 419.
    /// </remarks>
    // MIGRATION: null means "in force now" and is the honest representation of the nullable column;
    // the legacy minimum-date sentinel must never be submitted. The membership window the legacy
    // queries apply is (EffectiveDate <= now OR EffectiveDate IS NULL) AND (ExpiryDate >= now OR
    // ExpiryDate IS NULL), measured at 04.00.04.SqlDataProvider lines 443 and 444, so a null here
    // satisfies the opening half unconditionally.
    //
    // MIGRATION: a date already in the past is CLEARED by the service rather than stored, which is
    // measured legacy behaviour. A caller must therefore not assume that what it submits here is
    // what a subsequent read returns.
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Instant at which the membership lapses, or <see langword="null"/> to have the service derive
    /// one from the role's trial and billing terms.
    /// </summary>
    /// <remarks>
    /// Target column <c>UserRoles.ExpiryDate datetime NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 242).
    /// </remarks>
    // MIGRATION: null does NOT mean "never expires" on this request - it means "derive it". That is
    // the one place where this contract's null differs in meaning from the stored column's null,
    // and it is deliberate: the legacy screen left the field blank to get the role's own terms
    // applied. A caller wanting an unbounded membership must give the role no billing or trial
    // period, in which case the derivation itself yields no expiry.
    //
    // MIGRATION: a date already in the past is advanced to the current instant by the service so
    // that the derived offset runs forward from now, which is measured legacy behaviour and another
    // reason a read need not echo the submitted value.
    //
    // MIGRATION: the far-future date 9999-12-31 is a real, externally observable value meaning
    // "perpetual" and is stored and returned unchanged. It must not be normalised to null, and null
    // must not be normalised to it.
    public DateTime? ExpiryDate { get; set; }
}
