namespace DnnMigration.Domain.Common;

/// <summary>
/// One account as an account PICKER needs it: the key to submit and the two values that caption it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THIS EXISTS SO THAT CHOOSING AN ACCOUNT DOES NOT REQUIRE READING ONE. A performance and privacy review
/// measured the role-assignment screen filling its picker from the account LISTING, whose row carries a
/// postal address, a telephone number, an electronic-mail address, a creation instant, a last-login instant
/// and the approval, lockout and super-user flags.
/// </para>
/// <para>
/// A record rather than a class, and positional rather than property-initialised, because it is a value:
/// two choices naming the same account with the same captions are the same choice, and there is no state to
/// mutate. It is deliberately NOT an entity - it has no identity of its own, is never tracked, and is never
/// written.
/// </para>
/// </remarks>
/// <param name="UserId">The account's key, from <c>Users.UserID</c> (<c>int IDENTITY (1, 1) NOT NULL</c>).</param>
/// <param name="Username">The login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>).</param>
/// <param name="DisplayName">
/// The canonical display name, from <c>Users.DisplayName</c> (<c>nvarchar(128) NOT NULL</c> defaulting to
/// the empty string).
/// </param>
public readonly record struct AccountChoice(int UserId, string Username, string DisplayName);
