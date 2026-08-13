namespace DnnMigration.Domain.Common;

/// <summary>
/// Arithmetic shared by every paged read: turning a page address into the number of records to skip without
/// the multiplication overflowing.
/// </summary>
/// <remarks>
/// This type exists because the same product was computed in five places - two repositories and three
/// application services - as <c>pageIndex * pageSize</c> in unchecked <see cref="int"/> arithmetic.
/// </remarks>
public static class Paging
{
    /// <summary>Reports how many records a paged read must skip to reach the addressed page.</summary>
    /// <param name="pageIndex">The zero-based page address.</param>
    /// <param name="pageSize">The number of records a page holds.</param>
    /// <returns>The number of records to skip, always non-negative and always representable.</returns>
    /// <remarks>
    /// The product is computed in <see cref="long"/> and then clamped to <see cref="int.MaxValue"/>, which
    /// is exact rather than approximate: an offset beyond <see cref="int.MaxValue"/> addresses a page past
    /// any table this schema can hold, so it selects no records - and skipping <see cref="int.MaxValue"/>
    /// records selects no records either.
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
