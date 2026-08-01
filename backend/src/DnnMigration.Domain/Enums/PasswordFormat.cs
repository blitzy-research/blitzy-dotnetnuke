namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a user's password was persisted by the legacy DotNetNuke
/// membership layer. Ported member for member from
/// <c>DotNetNuke.Security.Membership.PasswordFormat</c>, declared in
/// <c>Library/Components/Users/Membership/PasswordFormat.vb</c>.
/// </summary>
/// <remarks>
/// <para>
/// The three ordinals below are legacy persisted values. The original
/// application wrote them into the DotNetNuke database and named them in
/// configuration, and existing rows still carry them, so they form part of the
/// data contract rather than an implementation detail. Members must therefore
/// never be renamed, reordered, renumbered, added to or removed. Where a
/// consuming column is nullable, that is expressed by declaring the consuming
/// property <c>PasswordFormat?</c>, never by inventing an extra member to stand
/// in for the absence of a value.
/// </para>
/// <para>
/// This installation ran with <c>passwordFormat="Encrypted"</c> - ordinal 2,
/// reversible Triple-DES - on the <c>AspNetSqlMembershipProvider</c>
/// registration at <c>Website/release.config:L236-L246</c>. That same
/// registration fixed the password policy the target preserves verbatim as
/// validation rules: minimum length seven, no required non-alphanumeric
/// characters, no question-and-answer requirement, and email uniqueness not
/// enforced.
/// </para>
/// <para>
/// The target platform reproduces none of these formats. Passwords are hashed
/// one way with BCrypt, and password retrieval is deliberately not carried
/// forward to any endpoint or screen. This type nevertheless survives the change
/// because a legacy stored-credential format has to remain expressible while
/// existing credentials are migrated: until a given user's password has been
/// re-hashed, the system must still be able to state which legacy format that
/// row is held in.
/// </para>
/// <para>
/// The migration path is re-hash on first successful login, with an
/// administrative reset as the fallback for accounts that never sign in again.
/// Once a row has been re-hashed, the value held here is purely historical.
/// </para>
/// </remarks>
public enum PasswordFormat
{
    /// <summary>
    /// Plaintext, stored exactly as supplied. Historically real rather than
    /// theoretical: the original schema declared the password column as
    /// <c>[Password] [nvarchar] (20) NOT NULL</c> directly on
    /// <c>[dbo].[Users]</c> in
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L97-L110</c>,
    /// and that script's seed rows populated the column with literal values.
    /// </summary>
    Clear = 0,

    /// <summary>
    /// One-way hash, SHA1 under the legacy ASP.NET membership provider. The only
    /// legacy format whose stored value cannot be reversed, and so the closest
    /// legacy analogue of the BCrypt hashing the target platform performs.
    /// </summary>
    Hashed = 1,

    // MIGRATION: reversible password storage is not reproduced. The legacy
    // provider paired this format with enablePasswordRetrieval="true"
    // (Website/release.config:L236-L246) and with a Triple-DES machine key whose
    // decryption key is committed to the legacy repository in plaintext
    // (Website/release.config:L89-L93), so any holder of that source could
    // recover every stored password. The target platform replaces reversible
    // storage with one-way BCrypt hashing and exposes no retrieval path at all.
    // See the remarks on this type for the re-hash migration window, and
    // MIGRATION_NOTES.md for the itemised behavioural difference.
    /// <summary>
    /// Reversible encryption, Triple-DES under the legacy ASP.NET membership
    /// provider, and the format this installation actually used. Retained so
    /// that a credential still held in the legacy format remains expressible
    /// throughout the migration window described in the remarks on this type.
    /// </summary>
    Encrypted = 2
}
