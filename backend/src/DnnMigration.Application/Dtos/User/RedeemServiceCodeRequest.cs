namespace DnnMigration.Application.Dtos.User;

/// <summary>Request contract for redeeming a service invitation code against the signed-in account.</summary>
/// <remarks>
/// <para>
/// The legacy field is spelled <c>RSVPCode</c> - the column is <c>Roles.RSVPCode</c> - and the wording the
/// resource file held calls it an "RSVP Code".
/// </para>
/// <para>
/// An EMPTY code is refused rather than ignored. The legacy handler wrapped its whole body in <c>If code
/// &lt;&gt; "" Then</c> (<c>:L403</c>), so submitting an empty box did nothing at all and posted no message
/// - the account was left unable to tell a rejected code from an unread one.
/// </para>
/// </remarks>
public sealed class RedeemServiceCodeRequest
{
    /// <summary>Gets or sets the invitation code to redeem.</summary>
    /// <remarks>
    /// Matched against <c>Roles.RSVPCode</c>, a <c>nvarchar(50)</c> column, which is why the validator
    /// bounds the submitted length at fifty characters: a longer value cannot match any stored code, and
    /// saying so as a field failure is more useful than reading every role in order to answer "no match".
    /// </remarks>
    public string? Code { get; set; }
}
