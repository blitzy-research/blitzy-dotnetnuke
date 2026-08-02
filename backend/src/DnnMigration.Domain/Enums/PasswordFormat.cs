namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a user's password was persisted by the legacy DotNetNuke membership layer. Ported
/// member for member from <c>DotNetNuke.Security.Membership.PasswordFormat</c>, declared in
/// <c>Library/Components/Users/Membership/PasswordFormat.vb</c>.
/// </summary>
/// <remarks>
/// <para>
/// The three ordinals are legacy persisted values that existing rows still carry, so they form part
/// of the data contract: members must never be renamed, reordered, renumbered, added to or removed.
/// A nullable consuming column is expressed by declaring the consuming property
/// <c>PasswordFormat?</c>, never by inventing a member to stand in for an absent value.
/// </para>
/// <para>
/// The target platform reproduces none of these formats - passwords are hashed one way and there is
/// no retrieval path - yet this type survives the change because a legacy stored format has to
/// remain expressible while existing credentials migrate: until a row has been re-hashed, the system
/// must still be able to state which legacy format it is held in. The migration path is re-hash on
/// first successful sign-in, with an administrative reset as the fallback; once a row is re-hashed
/// the value held here is purely historical.
/// </para>
/// </remarks>
public enum PasswordFormat
{
    /// <summary>
    /// Plaintext, stored exactly as supplied. Historically real rather than theoretical: the original
    /// schema declared the password column as a mandatory 20-character string directly on
    /// <c>dbo.Users</c>, before credentials moved into the ASP.NET membership tables.
    /// </summary>
    Clear = 0,

    /// <summary>
    /// One-way hash, SHA1 under the legacy ASP.NET membership provider. The only
    /// legacy format whose stored value cannot be reversed, and so the closest
    /// legacy analogue of the BCrypt hashing the target platform performs.
    /// </summary>
    Hashed = 1,

    // MIGRATION: reversible password storage is not reproduced. This legacy format was paired with
    // password retrieval enabled and with a Triple-DES machine key that was itself committed to
    // source control, so any holder of the legacy sources could recover every stored password. The
    // target replaces reversible storage with one-way hashing and exposes no retrieval path at all;
    // see the remarks on this type for the re-hash migration window.
    /// <summary>
    /// Reversible encryption, Triple-DES under the legacy ASP.NET membership provider, and the format
    /// this installation actually used. Retained so that a credential still held in the legacy format
    /// remains expressible throughout the migration window described in the remarks on this type.
    /// </summary>
    Encrypted = 2
}
