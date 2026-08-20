namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for <c>POST /api/v1/users/{userId}/password</c>. Carries every credential mutation the
/// legacy DotNetNuke password-management screen offered, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// THE QUESTION-AND-ANSWER OPERATION IS NOT CARRIED FORWARD, AND ITS MEMBERS ARE REMOVED RATHER THAN LEFT
/// INERT. The recovery pair has no target counterpart at all: <c>IUserService</c> records that the legacy
/// question-and-answer member has no counterpart, that no member declares a question or answer parameter,
/// and that the pair's only real purpose was to guard credential retrieval - which is dropped outright,
/// because the store is now one-way.
/// </para>
/// <para>
/// THE PASSWORD STORE CHANGES, AND SO DOES WHAT A RESET RETURNS. The legacy store is reversible: the
/// membership provider is registered with an encrypted password format and password retrieval enabled, and
/// the key that decrypts every stored password is committed to the legacy configuration file in the clear.
/// The target replaces that with one-way password hashing.
/// </para>
/// </remarks>
public sealed class ChangePasswordRequest
{
    /// <summary>
    /// The value of <see cref="Operation"/> that selects the change-password flow: the legacy
    /// <c>pnlChange</c> panel.
    /// </summary>
    public const string OperationChange = "change";

    /// <summary>
    /// The value of <see cref="Operation"/> that selects the reset-password flow: the legacy
    /// <c>pnlReset</c> panel, and the fallback migration path after the bounded first-login window or when
    /// the owner no longer knows the credential.
    /// </summary>
    /// <remarks>
    /// A request carrying this value must satisfy the portal-administrator policy, which
    /// <c>UsersController.ChangePasswordAsync</c> evaluates before the body is validated.
    /// </remarks>
    public const string OperationReset = "reset";

    /// <summary>
    /// Which of the two supported operations this request performs. Expected to be either <see
    /// cref="OperationChange"/> or <see cref="OperationReset"/>.
    /// </summary>
    /// <remarks>
    /// WHY AN EXPLICIT DISCRIMINATOR RATHER THAN INFERENCE. Inference cannot separate these two operations
    /// safely.
    /// </remarks>
    public string? Operation { get; set; }

    /// <summary>The caller's existing password, supplied to re-authenticate before the change is applied.</summary>
    /// <remarks>
    /// UNCONDITIONALLY REQUIRED FOR A CHANGE, AND FORBIDDEN ON A RESET. The legacy change guard at L284 was
    /// compound - <c>Not IsAdmin And txtOldPassword.Text = ""</c> - because the same button served both a
    /// self-service change and an administrator changing someone else's credential, and the screen hid this
    /// row for the latter.
    /// </remarks>
    public string? CurrentPassword { get; set; }

    /// <summary>The replacement password.</summary>
    /// <remarks>
    /// REQUIRED ON A RESET TOO, WHICH IS A DELIBERATE DIVERGENCE. The legacy reset generated a credential
    /// inside the membership provider and returned it to the caller (L906, returned at L915).
    /// </remarks>
    public string? NewPassword { get; set; }

    /// <summary>The replacement password, repeated, to catch a typing error before it becomes a lockout.</summary>
    public string? ConfirmPassword { get; set; }
}
