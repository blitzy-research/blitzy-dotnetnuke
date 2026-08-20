namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One declaration's requested position, as the reordering contract carries it.
/// </summary>
/// <remarks>
/// A pair rather than a full declaration, because reordering writes exactly one column. Carrying the whole
/// declaration would make an ordering request able to rename a property, change its data type or alter its
/// validation expression as a side effect of a Move Up, and every one of those members would then have to
/// be re-validated on a path whose only purpose is to exchange two positions.
/// </remarks>
public sealed class ProfilePropertyDefinitionPosition
{
    /// <summary>Identifier of the declaration being positioned.</summary>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// The declaration's new display position.
    /// </summary>
    /// <remarks>
    /// Column <c>ViewOrder int</c>. The value is a sort key and not an index: the legacy grid exchanged the
    /// stored values of two neighbours rather than renumbering the list, so positions are expected to be
    /// sparse and are never re-sequenced on the caller's behalf.
    /// </remarks>
    public int ViewOrder { get; set; }
}

/// <summary>
/// Request contract for <c>PUT /api/v1/profile-definitions/order</c>: the display positions of several
/// profile property declarations, written as ONE unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this contract exists at all.</b> Reordering needs no state that the per-declaration update
/// contract does not already carry — <c>ViewOrder</c> is a member of that contract — and for that reason
/// this endpoint did not exist. Ordering, however, is not a per-row fact. A Move Up EXCHANGES two stored
/// positions, so the two writes are only correct together: if the first is applied and the second is
/// refused or lost, two declarations hold the same position and the relation the operator was editing is
/// left in a state neither the old nor the new order describes. A per-row transport cannot express that,
/// however carefully it reports which row failed.
/// </para>
/// <para>
/// <b>All or nothing.</b> Every named declaration is resolved before anything is staged, and the whole set
/// is committed in a single unit of work. A request naming one unknown or withdrawn declaration writes
/// NOTHING — the caller is answered with the absence rather than with a partially applied order.
/// </para>
/// <para>
/// This contract deliberately does not renumber, compact or otherwise normalise the positions it is given.
/// The submitted values are stored exactly as submitted, matching the legacy grid, which exchanged two
/// <c>ViewOrder</c> values and persisted them unchanged.
/// </para>
/// </remarks>
public sealed class ReorderProfilePropertyDefinitionsRequest
{
    /// <summary>
    /// The declarations to reposition, each paired with its new position.
    /// </summary>
    /// <remarks>
    /// A partial set is legitimate and is the normal case: exchanging two neighbours names two
    /// declarations, not the whole catalogue. Declarations the request does not name keep the positions
    /// they hold.
    /// </remarks>
    public IReadOnlyList<ProfilePropertyDefinitionPosition> Positions { get; set; } =
        Array.Empty<ProfilePropertyDefinitionPosition>();
}
