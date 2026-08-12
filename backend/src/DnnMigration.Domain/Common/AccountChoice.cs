namespace DnnMigration.Domain.Common;

/// <summary>
/// One account as an account PICKER needs it: the key to submit and the two values that caption it.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this is the shape <c>SecurityRoles.ascx</c>'s <c>cboUsers</c> drop-down actually consumed.
/// <c>UserModuleBase.vb:L178-L186</c> filled it from a tenant-wide account read and bound exactly three
/// values - the identifier behind each option, the display name it showed, and the login name it showed in
/// brackets beside it. Nothing else on that control was read, and nothing else is carried here.
/// </para>
/// <para>
/// ⚠ THIS EXISTS SO THAT CHOOSING AN ACCOUNT DOES NOT REQUIRE READING ONE. A performance and privacy review
/// measured the role-assignment screen filling its picker from the account LISTING, whose row carries a
/// postal address, a telephone number, an electronic-mail address, a creation instant, a last-login instant
/// and the approval, lockout and super-user flags. Every one of those fields left the database, crossed the
/// wire and sat in browser memory so that three of them could be rendered. Authorisation to read the
/// account grid does not make it right to send fields the asking screen has no use for, and a picker that
/// may enumerate a whole tenant is the worst place to send them. The projection is therefore declared as its
/// own type rather than left to a caller's discretion: a reader of this type can see at a glance that no
/// personal detail can travel through it, and a future field cannot be added to the picker's payload by
/// accident.
/// </para>
/// <para>
/// It lives in the domain rather than beside the transfer objects because the REPOSITORY produces it. Rule
/// T3 keeps every read behind a repository interface and Rule T1 forbids the domain from referencing the
/// application layer, so the shape a projecting read returns has to be declared here; the application layer
/// maps it onto its own wire contract, exactly as it maps an entity.
/// </para>
/// <para>
/// A record rather than a class, and positional rather than property-initialised, because it is a value: two
/// choices naming the same account with the same captions are the same choice, and there is no state to
/// mutate. It is deliberately NOT an entity - it has no identity of its own, is never tracked, and is never
/// written.
/// </para>
/// </remarks>
/// <param name="UserId">
/// The account's key, from <c>Users.UserID</c> (<c>int IDENTITY (1, 1) NOT NULL</c>).
/// <para>
/// IDENTIFIER TRAP: absence is expressed by an empty result or a null reference and NEVER by a magic
/// number. <c>Users.UserID</c> seeds at one, but the surrounding tables do not - <c>Portals.PortalID</c> is
/// <c>IDENTITY (-1, 1)</c> and <c>Roles.RoleID</c> is <c>IDENTITY (0, 1)</c> - so neither
/// <c>id &lt;= 0</c> nor <c>id == -1</c> is a valid absence test anywhere in this schema, and no such test
/// may be written against this member either.
/// </para>
/// </param>
/// <param name="Username">
/// The login name, from <c>Users.Username</c> (<c>nvarchar(100) NOT NULL</c>). Shown beside the display
/// name so that two accounts sharing a display name can still be told apart, which is the reason the legacy
/// option caption carried both.
/// </param>
/// <param name="DisplayName">
/// The canonical display name, from <c>Users.DisplayName</c> (<c>nvarchar(128) NOT NULL</c> defaulting to
/// the empty string).
/// <para>
/// The EMPTY STRING IS A CONFORMING VALUE and must not be read as absence: the legacy absent-string
/// sentinel is <c>""</c> literally (<c>Library/Components/Shared/Null.vb:L71-L75</c>), so an account whose
/// display name was never set has an empty one rather than a missing one. A caller captioning an option
/// falls back to <see cref="Username"/> when it is empty; it does not treat the row as unusable.
/// </para>
/// </param>
public readonly record struct AccountChoice(int UserId, string Username, string DisplayName);
