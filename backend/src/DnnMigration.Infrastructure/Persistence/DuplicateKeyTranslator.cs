using System.Text.RegularExpressions;
using DnnMigration.Domain.Common;
using Microsoft.Data.SqlClient;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Recognises a store refusal that means "this value is already taken", and reports the constraint the store
/// named when it disclosed one.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: SEC-F6. Separated from <see cref="UnitOfWork"/> so the recognition can be exercised on its own:
/// it is the one piece of this translation that depends on provider error numbers and on the shape of provider
/// text, and neither is something a test should have to provoke a real race to reach.
/// </para>
/// <para>
/// Only the persistence assembly may name <see cref="SqlException"/>, and only this file does. Everything the
/// layers above see is <see cref="DuplicateKeyException"/>, which the Domain declares and which carries no
/// provider type of its own.
/// </para>
/// </remarks>
internal static partial class DuplicateKeyTranslator
{
    /// <summary>Error number the store raises for a unique CONSTRAINT violation.</summary>
    /// <remarks>
    /// Raised for a PRIMARY KEY or a UNIQUE constraint. This schema carries both kinds, so both numbers must
    /// be matched rather than whichever one a particular table happens to produce.
    /// </remarks>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>Error number the store raises for a unique INDEX violation.</summary>
    /// <remarks>
    /// Raised for a UNIQUE INDEX created as an index rather than as a constraint, which is what the
    /// <c>CREATE UNIQUE INDEX</c> statements in the terminal legacy schema leave behind - <c>IX_RoleName</c>
    /// over <c>(PortalID, RoleName)</c> among them.
    /// </remarks>
    private const int UniqueIndexViolation = 2601;

    /// <summary>
    /// Tests whether a store failure means a duplicated unique value, and reports the constraint it named.
    /// </summary>
    /// <param name="exception">The failure raised by the flush.</param>
    /// <param name="constraintName">
    /// The constraint the store named, or <see langword="null"/> when it named none or its wording could not
    /// be read. Best effort by construction: see <see cref="DuplicateKeyException.ConstraintName"/>.
    /// </param>
    /// <returns><see langword="true"/> when the failure is a duplicate-value refusal.</returns>
    /// <remarks>
    /// <para>
    /// The WHOLE inner chain is walked rather than only the immediate inner exception. The provider wraps its
    /// own fault at more than one depth depending on whether an execution strategy, a retrying strategy or a
    /// transaction scope was in play, and a translation that only looked one level down would classify the
    /// same race correctly on one code path and report it as a server fault on another.
    /// </para>
    /// <para>
    /// The number is the decision; the name is only ever extra. A store that raises one of these numbers has
    /// refused a duplicate whatever its message says, so an unreadable message costs the caller nothing.
    /// </para>
    /// </remarks>
    public static bool Describes(Exception exception, out string? constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        constraintName = null;

        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is not SqlException sqlException)
            {
                continue;
            }

            // Every error the batch reported is examined, not just the one the exception surfaces as its
            // own Number: a flush that stages several statements can carry more than one, and the
            // duplicate need not be the first.
            foreach (SqlError error in sqlException.Errors)
            {
                if (error.Number is UniqueConstraintViolation or UniqueIndexViolation)
                {
                    constraintName = ExtractConstraintName(error.Message);
                    return true;
                }
            }

            if (sqlException.Number is UniqueConstraintViolation or UniqueIndexViolation)
            {
                constraintName = ExtractConstraintName(sqlException.Message);
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads the constraint name out of a store message, when one is legible.</summary>
    /// <param name="message">The provider message.</param>
    /// <returns>The name, or <see langword="null"/> when the message does not disclose one.</returns>
    /// <remarks>
    /// The two numbers this translator matches are reported with the object name in single quotes -
    /// "Violation of UNIQUE KEY constraint 'IX_Name'" and "Cannot insert duplicate key row in object
    /// 'dbo.Roles' with unique index 'IX_RoleName'" - so the LAST quoted token is the constraint in both
    /// wordings. Read with a bounded, non-backtracking pattern and a hard time limit, because the input is
    /// provider text rather than anything this solution authored, and a failure to read it is not a failure
    /// at all: the caller's answer is decided by the error number.
    /// </remarks>
    private static string? ExtractConstraintName(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            MatchCollection matches = QuotedName().Matches(message);
            return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Matches a single-quoted object name, bounded so it cannot run away on provider text.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        "'([^']{1,256})'",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex QuotedName();
}
