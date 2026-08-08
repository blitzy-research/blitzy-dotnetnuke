namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Request contract for redeeming a service invitation code against the signed-in account.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the invitation-code box and its command on the legacy member-services panel -
/// <c>txtRSVPCode</c> and <c>cmdRSVP</c> at <c>Website/admin/Users/MemberServices.ascx:L14-L15</c>, handled
/// at <c>MemberServices.ascx.vb:L397-L433</c>. The legacy handler read the box, walked EVERY role of the
/// tenant, and subscribed the signed-in account to each role whose <c>RSVPCode</c> equalled the value.
/// </para>
/// <para>
/// The legacy field is spelled <c>RSVPCode</c> - the column is <c>Roles.RSVPCode</c> - and the wording the
/// resource file held calls it an "RSVP Code". This contract calls the member <see cref="Code"/> and the
/// operation a redemption because the abbreviation names a social convention rather than the thing it
/// identifies, and because the column name is preserved where it matters, which is the Fluent mapping in
/// Infrastructure. Nothing about the stored value changes.
/// </para>
/// <para>
/// MIGRATION: an EMPTY code is refused rather than ignored. The legacy handler wrapped its whole body in
/// <c>If code &lt;&gt; "" Then</c> (<c>:L403</c>), so submitting an empty box did nothing at all and posted
/// no message - the account was left unable to tell a rejected code from an unread one. The declarative
/// validator refuses an empty value with a field-level failure instead. The guard itself is load-bearing
/// and is preserved beneath it too: an absent <c>RSVPCode</c> reached the legacy comparison as
/// <c>Null.NullString</c>, the EMPTY STRING, so without that guard an empty submission would have matched
/// every role that carries no code at all.
/// </para>
/// <para>
/// MIGRATION: the comparison is ORDINAL and the value is NOT trimmed, which is what the legacy comparison
/// was. <c>objRole.RSVPCode = code</c> (<c>:L411</c>) is a Visual Basic string equality with no
/// <c>Option Compare Text</c> anywhere in the file, so it compared byte for byte and a leading space
/// prevented a match. Both properties are preserved by the service rather than "improved", because
/// widening the comparison would let a code match a role its issuer did not intend.
/// </para>
/// </remarks>
public sealed class RedeemServiceCodeRequest
{
    /// <summary>
    /// Gets or sets the invitation code to redeem.
    /// </summary>
    /// <remarks>
    /// Matched against <c>Roles.RSVPCode</c>, a <c>nvarchar(50)</c> column, which is why the validator
    /// bounds the submitted length at fifty characters: a longer value cannot match any stored code, and
    /// saying so as a field failure is more useful than reading every role in order to answer "no match".
    /// Declared nullable so that a body omitting the member reaches the validator as an absent value and
    /// is refused by it, rather than being silently read as the empty string this operation must not
    /// accept.
    /// </remarks>
    public string? Code { get; set; }
}
