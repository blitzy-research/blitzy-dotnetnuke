namespace DnnMigration.Domain.Common;

// MIGRATION: Result and Result<T> replace the legacy VB.NET ByRef
// mutate-and-return-status idiom. A census of the five in-scope legacy trees under
// Library/Components/ - Portal, Modules, Tabs, Users and Security - counts 30
// ByRef declaration signatures, distributed as Portal 6, Modules 3, Tabs 1,
// Users 20 and Security 0, spread over 33 ByRef tokens because three Portal
// signatures carry two such parameters each. The canonical sites are in
// Library/Components/Users/UserController.vb: L156 CreateUser, the archetype,
// which mutates its argument AND returns a create-status enum, so a single call
// yields two answers that no type ties together; L200 DeleteUser; L433
// GetPassword; L638 GetUserMembership, which is a Sub and therefore pure argument
// mutation with no return value at all; L991 UserLogin; and the two ValidateUser
// overloads at L1110 and L1132. The replacement is uniform: the mutated object
// becomes the Result value and the status enum becomes the reason. Per the
// migration plan, no out-parameter and no ref-parameter appears in any target
// public API, which is why this file offers no value-fetching probe method and no
// conversion to a boolean.
//
// MIGRATION: the reason is carried generically, as a code plus a message, rather
// than as one of the legacy status enumerations. Common/ is the most foundational
// folder in the Domain project and sits upstream of the sibling Enums folder, so
// it may not name a type declared there; the Application layer supplies the
// concrete discriminator when it builds the reason. That indirection also removes
// a numeric trap. Of the two membership status enumerations under
// Library/Components/Users/Membership/, the create-status enum declares 18
// members with values 0 through 17 and its Success member is 13, not 0, while its
// 0 value is the pre-call AddUser state. No "zero means OK" assumption is ever
// safe, so this type never encodes an outcome as a number.

/// <summary>
/// Immutable description of a single outcome carried by a <see cref="Result"/> or
/// a <see cref="Result{T}"/>: a machine-readable <see cref="Code"/> paired with a
/// human-readable <see cref="Message"/>.
/// </summary>
/// <remarks>
/// <para>
/// The description is deliberately generic - two strings, never a typed
/// enumeration - so that this folder stays free of any dependency on the sibling
/// Enums folder. Callers in the Application layer supply the concrete
/// discriminator themselves, typically the name of a legacy membership status
/// member.
/// </para>
/// <para>
/// A reason is not exclusively a failure. It also carries advisory information
/// alongside a success, which is what <see cref="Result.Reason"/> exposes and
/// what <see cref="Result.Error"/> deliberately hides.
/// </para>
/// <para>
/// Declared as a <c>readonly record struct</c> so that value equality, hashing
/// and the equality operators are all synthesised by the compiler rather than
/// hand-written, and so that a reason costs no allocation.
/// </para>
/// </remarks>
public readonly record struct ResultReason
{
    /// <summary>
    /// Initialises a new <see cref="ResultReason"/>.
    /// </summary>
    /// <param name="code">
    /// Stable, machine-readable discriminator identifying which expected outcome
    /// occurred.
    /// </param>
    /// <param name="message">
    /// Human-readable explanation, suitable for surfacing to an administrator or
    /// for translation at the API boundary.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty
    /// or consists only of white-space characters. Both arguments are required:
    /// the code is the discriminator a caller branches on, and the message is the
    /// wording a user reads.
    /// </exception>
    public ResultReason(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
    }

    /// <summary>
    /// Gets the stable, machine-readable discriminator for this outcome.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable explanation of this outcome.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Returns this reason formatted as <c>Code: Message</c>.
    /// </summary>
    /// <returns>A diagnostic representation of this reason.</returns>
    public override string ToString() => $"{Code}: {Message}";
}

// MIGRATION: success is not binary in the legacy login flow, which is precisely
// why a successful Result can still carry a Reason.
// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb branches
// LOGIN_USERNOTAPPROVED away at L168 and then computes
// authenticated = (loginStatus <> LOGIN_FAILURE). That expression sits at L187 in
// this checkout, with the Else at L186 and the End If at L188; the planning note
// cites it as L188. Every other member of the 7-member login-status enumeration
// therefore counts as authenticated, including LOGIN_INSECUREADMINPASSWORD and
// LOGIN_INSECUREHOSTPASSWORD, which are genuine successes carrying a caveat - the
// credential was accepted, but the password is weak and must be changed.
// Modelling that faithfully requires a reason slot that is populated
// independently of the success flag, so this type is not a bare flag plus value.
//
// MIGRATION: the same L187 expression also treats LOGIN_USERLOCKEDOUT as
// authenticated, because a lockout status is neither LOGIN_USERNOTAPPROVED nor
// LOGIN_FAILURE. That is a discovered legacy defect. It is recorded here and
// deliberately NOT corrected: the migration plan's minimal-change clause requires
// a discovered defect to be annotated in place rather than repaired, and the
// login-status mapping decision belongs to the Application layer's authentication
// service, not to this primitive. Result is expressive enough to model either
// behaviour, and takes no position on which is chosen.

/// <summary>
/// Outcome of an operation that either succeeded or failed for a reason the
/// caller is expected to handle.
/// </summary>
/// <remarks>
/// <para>
/// This type, together with <see cref="Result{T}"/>, is the <em>only</em> channel
/// for reporting an <em>expected</em> failure across the Domain and Application
/// layers. Expected failures are ordinary control flow - a duplicate user name, a
/// rejected credential, a missing portal - and are returned, never thrown. The
/// thrown counterpart is the sibling domain-exception type, which signals a
/// violated business invariant, while genuinely unexpected exceptions are left to
/// surface and are translated once, at the API edge.
/// </para>
/// <para>
/// A reason is <em>not</em> the same thing as a failure. <see cref="Reason"/> is
/// populated independently of <see cref="IsSuccess"/>, so a successful outcome may
/// still carry advisory information; <see cref="Error"/> is the narrower
/// projection that yields a reason only when the operation actually failed.
/// Prefer <see cref="Error"/> when reacting to failure and <see cref="Reason"/>
/// when reading an advisory code.
/// </para>
/// <para>
/// Instances are immutable and are built exclusively through the static factory
/// methods; the constructor is not public, so an inconsistent outcome cannot be
/// constructed. No implicit boolean conversion is offered - branch on
/// <see cref="IsSuccess"/> or <see cref="IsFailure"/> explicitly - and no
/// combinator, pipeline or exception-capturing helper is provided, because this is
/// a reporting primitive rather than a functional-composition library.
/// </para>
/// </remarks>
public class Result
{
    /// <summary>
    /// Initialises a new outcome. Not public: outcomes are built through the
    /// static factory methods so that the success flag and the reason cannot be
    /// combined inconsistently. Visible to <see cref="Result{T}"/> only.
    /// </summary>
    /// <param name="isSuccess">
    /// <see langword="true"/> when the operation succeeded.
    /// </param>
    /// <param name="reason">
    /// The advisory or failure reason, or <see langword="null"/> when the outcome
    /// needs no explanation.
    /// </param>
    protected Result(bool isSuccess, ResultReason? reason)
    {
        IsSuccess = isSuccess;
        Reason = reason;
    }

    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets a value indicating whether the operation failed. Always the exact
    /// negation of <see cref="IsSuccess"/>.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Gets the reason describing this outcome, or <see langword="null"/> when
    /// none was supplied.
    /// </summary>
    /// <remarks>
    /// Populated independently of <see cref="IsSuccess"/>. A non-null value here
    /// therefore does <em>not</em> imply failure: on a successful outcome it is an
    /// advisory code, such as the weak-password caveat the legacy login flow
    /// reports while still authenticating the user. Use <see cref="Error"/> to
    /// read a reason that is guaranteed to describe a failure.
    /// </remarks>
    public ResultReason? Reason { get; }

    /// <summary>
    /// Gets the reason this operation failed, or <see langword="null"/> when it
    /// succeeded.
    /// </summary>
    /// <remarks>
    /// The failure-only projection of <see cref="Reason"/>. Because it is empty
    /// for every successful outcome, an advisory code attached to a success can
    /// never be mistaken here for a failure.
    /// </remarks>
    public ResultReason? Error => IsSuccess ? null : Reason;

    /// <summary>
    /// Creates a successful outcome that carries no reason.
    /// </summary>
    /// <returns>A successful <see cref="Result"/>.</returns>
    public static Result Success() => new(true, null);

    /// <summary>
    /// Creates a successful outcome that carries an advisory reason, for the case
    /// where an operation succeeded but the caller still needs to be told
    /// something about how.
    /// </summary>
    /// <param name="reason">The advisory reason to attach to the success.</param>
    /// <returns>A successful <see cref="Result"/> carrying <paramref name="reason"/>.</returns>
    public static Result Success(ResultReason reason) => new(true, reason);

    /// <summary>
    /// Creates a failed outcome.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Failure(ResultReason reason) => new(false, reason);

    /// <summary>
    /// Creates a failed outcome from a discriminator and a message.
    /// </summary>
    /// <param name="code">
    /// Stable, machine-readable discriminator identifying which expected failure
    /// occurred.
    /// </param>
    /// <param name="message">Human-readable explanation of the failure.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty
    /// or consists only of white-space characters.
    /// </exception>
    public static Result Failure(string code, string message) => new(false, new ResultReason(code, message));

    /// <summary>
    /// Returns a diagnostic representation of this outcome, naming the reason when
    /// one is present.
    /// </summary>
    /// <returns>A diagnostic representation of this outcome.</returns>
    public override string ToString()
    {
        string label = IsSuccess ? "Success" : "Failure";

        return Reason is { } reason ? $"{label} ({reason})" : label;
    }
}

// MIGRATION: a null Value on a SUCCESSFUL Result<T> means absent, never failed.
// The legacy null contract is Library/Components/Shared/Null.vb, whose sentinels
// are -1 for Short and Integer, 255 for Byte, MinValue for Single, Double and
// Decimal, Date.MinValue for Date, False for Boolean, Guid.Empty for Guid, and -
// the trap - the EMPTY STRING for String rather than null; its SetNull helpers
// substitute those values on every single read. Those sentinels are preserved at
// the DTO and API boundary, not in the domain, so this file declares no sentinel
// constant and the success factories do not reject a null value. "The lookup
// succeeded and the column is absent" and "the lookup failed" therefore stay
// distinguishable: the first is IsSuccess with a null Value, the second is
// IsFailure with an Error. Express a payload that may legitimately be absent as
// Result<string?> so the nullable annotation states it at the call site.

/// <summary>
/// Outcome of an operation that either succeeded and produced a value, or failed
/// for a reason the caller is expected to handle.
/// </summary>
/// <typeparam name="T">
/// Type of the value produced by a successful operation. Unconstrained: use a
/// nullable type argument, such as <c>Result&lt;string?&gt;</c>, when the value
/// may legitimately be absent on success.
/// </typeparam>
/// <remarks>
/// <para>
/// The generic counterpart of <see cref="Result"/>, and like it the <em>only</em>
/// channel for reporting an <em>expected</em> failure. It derives from
/// <see cref="Result"/> so that a caller holding either shape can branch on
/// <see cref="Result.IsSuccess"/> and read <see cref="Result.Error"/> through one
/// contract.
/// </para>
/// <para>
/// A <see langword="null"/> <see cref="Value"/> on a <em>successful</em> outcome
/// means <em>absent</em>, not failed. That distinction is load-bearing: the legacy
/// data layer represents an absent column with a sentinel rather than with
/// <see langword="null"/>, and collapsing "succeeded with nothing to return" into
/// "failed" would change branch outcomes. The success factories accept
/// <see langword="null"/> for exactly this reason.
/// </para>
/// <para>
/// As with <see cref="Result"/>, a successful outcome may still carry an advisory
/// <see cref="Result.Reason"/>, and reading <see cref="Value"/> on a failed
/// outcome is treated as a programming error rather than as a domain condition.
/// </para>
/// </remarks>
public sealed class Result<T> : Result
{
    /// <summary>
    /// The carried value. Held in a field rather than an auto-property so that
    /// <see cref="Value"/> can guard access without exposing a second, unguarded
    /// accessor.
    /// </summary>
    private readonly T _value;

    /// <summary>
    /// Initialises a new outcome. Private: outcomes are built through the static
    /// factory methods so that a failed outcome can never be paired with a
    /// meaningful value, nor a successful one with a missing value slot.
    /// </summary>
    /// <param name="isSuccess">
    /// <see langword="true"/> when the operation succeeded.
    /// </param>
    /// <param name="value">
    /// The produced value, or the type default when the operation failed.
    /// </param>
    /// <param name="reason">
    /// The advisory or failure reason, or <see langword="null"/> when the outcome
    /// needs no explanation.
    /// </param>
    private Result(bool isSuccess, T value, ResultReason? reason)
        : base(isSuccess, reason)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the value produced by a successful operation.
    /// </summary>
    /// <value>
    /// The produced value, which may itself be <see langword="null"/> when
    /// <typeparamref name="T"/> permits it - meaning the operation succeeded and
    /// the value is absent, <em>not</em> that it failed.
    /// </value>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the operation failed. Reading the value of a failed outcome is
    /// a programming error - the caller skipped its <see cref="Result.IsSuccess"/>
    /// check - and not a violated business invariant, so this is a plain
    /// framework exception rather than the sibling domain-exception type. The two
    /// are deliberately not conflated: this type reports <em>expected</em>
    /// failures through its own surface, and misuse of that surface is a defect in
    /// the calling code.
    /// </exception>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException(
            "The value of a failed result cannot be read. Check IsSuccess before reading Value; " +
            $"the failure is described by Error. Reason code: {Reason?.Code ?? "(none supplied)"}.");

    /// <summary>
    /// Creates a successful outcome carrying <paramref name="value"/> and no
    /// reason.
    /// </summary>
    /// <param name="value">
    /// The produced value. May be <see langword="null"/> when
    /// <typeparamref name="T"/> permits it, which records an absent value rather
    /// than a failure.
    /// </param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>
    /// Creates a successful outcome carrying both <paramref name="value"/> and an
    /// advisory reason, for the case where an operation succeeded but the caller
    /// still needs to be told something about how.
    /// </summary>
    /// <param name="value">
    /// The produced value. May be <see langword="null"/> when
    /// <typeparamref name="T"/> permits it.
    /// </param>
    /// <param name="reason">The advisory reason to attach to the success.</param>
    /// <returns>
    /// A successful <see cref="Result{T}"/> carrying <paramref name="reason"/>.
    /// </returns>
    public static Result<T> Success(T value, ResultReason reason) => new(true, value, reason);

    /// <summary>
    /// Creates a failed outcome. Hides the non-generic
    /// <see cref="Result.Failure(ResultReason)"/> so that the strongly typed
    /// outcome is returned instead of its base type.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> Failure(ResultReason reason) => new(false, default!, reason);

    /// <summary>
    /// Creates a failed outcome from a discriminator and a message. Hides the
    /// non-generic <see cref="Result.Failure(string, string)"/> so that the
    /// strongly typed outcome is returned instead of its base type.
    /// </summary>
    /// <param name="code">
    /// Stable, machine-readable discriminator identifying which expected failure
    /// occurred.
    /// </param>
    /// <param name="message">Human-readable explanation of the failure.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty
    /// or consists only of white-space characters.
    /// </exception>
    public static new Result<T> Failure(string code, string message) => new(false, default!, new ResultReason(code, message));
}
