namespace DnnMigration.Domain.Common;

/// <summary>
/// Arithmetic shared by every paged read: turning a page address into the number of records to skip
/// without the multiplication overflowing.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because the same product was computed in five places - two repositories and three
/// application services - as <c>pageIndex * pageSize</c> in unchecked <see cref="int"/> arithmetic. A
/// security review found that a page index large enough to overflow produced a <b>negative</b> skip count
/// rather than an exception, and a negative skip count is refused by the query provider deep inside the
/// read, so a request a caller could correct surfaced as an unhandled server fault instead of a
/// field-level refusal.
/// </para>
/// <para>
/// It lives in the domain because both the application services and the infrastructure repositories
/// perform the same computation and neither may depend on the other. Five copies of a clamp is how one of
/// them comes to be forgotten.
/// </para>
/// </remarks>
public static class Paging
{
    /// <summary>
    /// Reports how many records a paged read must skip to reach the addressed page.
    /// </summary>
    /// <param name="pageIndex">
    /// The zero-based page address. A negative value is treated as the first page, because a page address
    /// is a position rather than an identifier and there is no position before the first.
    /// </param>
    /// <param name="pageSize">
    /// The number of records a page holds. A non-positive value yields no offset, because a page of no
    /// records addresses nothing to skip past.
    /// </param>
    /// <returns>
    /// The number of records to skip, always non-negative and always representable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The product is computed in <see cref="long"/> and then clamped to <see cref="int.MaxValue"/>, which
    /// is exact rather than approximate: an offset beyond <see cref="int.MaxValue"/> addresses a page past
    /// any table this schema can hold, so it selects no records - and skipping
    /// <see cref="int.MaxValue"/> records selects no records either. The clamp therefore preserves the
    /// answer while removing the overflow, which is why it is used in preference to raising.
    /// </para>
    /// <para>
    /// <b>Raising was considered and rejected here.</b> A page address whose offset cannot be represented
    /// is a request a caller can correct, and the place to say so is the request validator, which refuses
    /// it with the field named. By the time a read is running, the validator has already passed and this
    /// method's job is to be total rather than to re-report. Throwing at this depth would turn a bound
    /// that request validation already enforces into a server fault whenever some future caller reached a
    /// repository without passing through it.
    /// </para>
    /// <para>
    /// Both defensive coercions are deliberate and neither hides a caller's mistake: a negative index and
    /// a non-positive size are refused by request validation with the offending field named, so reaching
    /// this method with either means an internal caller supplied it, and answering "no offset" is the
    /// benign reading of an internally malformed request rather than a silent reinterpretation of a
    /// caller's.
    /// </para>
    /// </remarks>
    public static int SkipCount(int pageIndex, int pageSize)
    {
        if (pageIndex <= 0 || pageSize <= 0)
        {
            return 0;
        }

        long offset = (long)pageIndex * pageSize;

        return offset > int.MaxValue ? int.MaxValue : (int)offset;
    }
}
