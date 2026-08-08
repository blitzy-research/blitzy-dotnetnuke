namespace DnnMigration.Domain.Common;

// MIGRATION: these two types replace the legacy mutate-and-return-status idiom, where a call reported its
// outcome through a ByRef argument as well as a return value and no type tied the two together. The mutated
// object becomes the Result value and the status becomes its reason; no target public API takes an out or
// ref parameter, which is why this file offers no value-fetching probe and no conversion to bool.
//
// MIGRATION: a reason is a code plus a message rather than one of the legacy status enumerations, for two
// reasons. Common/ sits upstream of the sibling Enums folder and may not name a type declared there, so the
// Application layer supplies the discriminator; and the legacy create-status enumeration numbers Success 13
// while 0 is a pre-call state, so no outcome here is ever encoded as a number.

/// <summary>
/// Immutable description of a single outcome carried by a <see cref="Result"/> or a
/// <see cref="Result{T}"/>: a machine-readable <see cref="Code"/> paired with a human-readable
/// <see cref="Message"/>.
/// </summary>
/// <remarks>
/// A reason is not exclusively a failure: it also carries advisory information alongside a success,
/// which is what <see cref="Result.Reason"/> exposes and <see cref="Result.Error"/> hides.
/// <para>
/// Declared a <c>sealed record</c> - a reference type - so equality is compiler-synthesised while
/// the validating constructor stays the only way to obtain an instance. A value type could be
/// reached through <c>default</c>, which would sidestep that constructor and yield a reason with a
/// null code and message; as a reference type the absent case is simply <see langword="null"/>.
/// </para>
/// </remarks>
public sealed record ResultReason
{
    /// <summary>Initialises a new <see cref="ResultReason"/>.</summary>
    /// <param name="code">
    /// Stable, machine-readable discriminator identifying which expected outcome occurred.
    /// </param>
    /// <param name="message">
    /// Human-readable explanation, suitable for surfacing to an administrator or for translation at
    /// the API boundary.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only
    /// of white-space characters. Both arguments are required: the code is the discriminator a
    /// caller branches on, and the message is the wording a user reads.
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

    /// <summary>Returns this reason formatted as <c>Code: Message</c>.</summary>
    /// <returns>A diagnostic representation of this reason.</returns>
    public override string ToString() => $"{Code}: {Message}";
}

// MIGRATION: success is not binary in the legacy sign-in flow, which is why a successful Result can still
// carry a Reason. Two of its login statuses accepted the credential while reporting that the password was
// weak and had to be changed, so the reason slot is populated independently of the success flag rather than
// this type being a bare flag plus value. Which status maps to which outcome is the authentication service's
// decision; this primitive takes no position on it.

/// <summary>
/// Outcome of an operation that either succeeded or failed for a reason the caller is expected to
/// handle.
/// </summary>
/// <remarks>
/// With <see cref="Result{T}"/> this is the only channel for reporting an <em>expected</em> failure
/// across the Domain and Application layers: a duplicate user name, a rejected credential, a
/// missing portal are returned, never thrown. A violated invariant throws the sibling domain
/// exception, and a genuinely unexpected exception is translated once, at the API edge.
/// <para>
/// <see cref="Reason"/> is populated independently of <see cref="IsSuccess"/>, so prefer
/// <see cref="Error"/> - the failure-only projection - when reacting to failure.
/// </para>
/// <para>
/// Instances are immutable and built only through the static factories; the
/// <c>private protected</c> constructor closes the hierarchy to this assembly, which is what makes
/// <see cref="Error"/> guaranteed non-null whenever <see cref="IsFailure"/> holds. No implicit bool
/// conversion and no combinator is offered: this is a reporting primitive, not a composition
/// library.
/// </para>
/// </remarks>
public class Result
{
    /// <summary>
    /// Initialises a new outcome. <c>private protected</c> so that outcomes are built through the
    /// static factories and <see cref="Result{T}"/> is the only derived shape.
    /// </summary>
    /// <param name="isSuccess"><see langword="true"/> when the operation succeeded.</param>
    /// <param name="reason">
    /// The advisory or failure reason. Optional on a success, where <see langword="null"/> records
    /// that the outcome needs no explanation; mandatory on a failure.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="isSuccess"/> is <see langword="false"/> and
    /// <paramref name="reason"/> is <see langword="null"/>: every failure is explicable.
    /// </exception>
    private protected Result(bool isSuccess, ResultReason? reason)
    {
        if (!isSuccess && reason is null)
        {
            throw new ArgumentNullException(
                nameof(reason),
                "A failed outcome must carry a reason. Build failures through the Failure factory methods so that a code and a message are always supplied.");
        }

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
    /// Gets the reason describing this outcome, or <see langword="null"/> when none was supplied.
    /// </summary>
    /// <remarks>
    /// A non-null value does not imply failure: on a success it is an advisory code, such as the
    /// weak-password caveat the legacy sign-in reported while still authenticating. Use
    /// <see cref="Error"/> for a reason guaranteed to describe a failure.
    /// </remarks>
    public ResultReason? Reason { get; }

    /// <summary>
    /// Gets the reason this operation failed, or <see langword="null"/> when it succeeded.
    /// </summary>
    /// <remarks>
    /// The failure-only projection of <see cref="Reason"/>, so an advisory code on a success is
    /// never mistaken for a failure here, and non-null whenever <see cref="IsFailure"/> holds.
    /// </remarks>
    public ResultReason? Error => IsSuccess ? null : Reason;

    /// <summary>
    /// Creates a successful outcome that carries no reason.
    /// </summary>
    /// <returns>A successful <see cref="Result"/>.</returns>
    public static Result Success() => new(true, null);

    /// <summary>Creates a successful outcome that carries an advisory reason.</summary>
    /// <param name="reason">The advisory reason to attach to the success.</param>
    /// <returns>A successful <see cref="Result"/> carrying <paramref name="reason"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>; use <see cref="Success()"/>
    /// for a success that needs no explanation.
    /// </exception>
    public static Result Success(ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result(true, reason);
    }

    /// <summary>Creates a failed outcome.</summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>: a failure always names the
    /// expected condition that occurred.
    /// </exception>
    public static Result Failure(ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result(false, reason);
    }

    /// <summary>Creates a failed outcome from a discriminator and a message.</summary>
    /// <param name="code">
    /// Stable, machine-readable discriminator identifying which expected failure occurred.
    /// </param>
    /// <param name="message">Human-readable explanation of the failure.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only
    /// of white-space characters.
    /// </exception>
    public static Result Failure(string code, string message) => new(false, new ResultReason(code, message));

    /// <summary>
    /// Returns a diagnostic representation of this outcome, naming the reason when one is present.
    /// </summary>
    /// <returns>A diagnostic representation of this outcome.</returns>
    public override string ToString()
    {
        string label = IsSuccess ? "Success" : "Failure";

        return Reason is { } reason ? $"{label} ({reason})" : label;
    }
}

// MIGRATION: a null Value on a SUCCESSFUL Result<T> means absent, never failed. Legacy sentinels - notably
// the empty string standing in for a null string - are preserved at the DTO and API boundary, not here, so
// no sentinel constant is declared and the success factories accept null. "Succeeded, and the value is
// absent" and "failed" therefore stay distinguishable. Express a payload that may legitimately be absent as
// Result<string?>, so the nullable annotation states it at the call site.

/// <summary>
/// Outcome of an operation that either succeeded and produced a value, or failed for a reason the
/// caller is expected to handle.
/// </summary>
/// <typeparam name="T">
/// Type of the value produced by a successful operation. Unconstrained: use a nullable type
/// argument, such as <c>Result&lt;string?&gt;</c>, when the value may legitimately be absent on
/// success.
/// </typeparam>
/// <remarks>
/// Derives from <see cref="Result"/> so a caller holding either shape branches on
/// <see cref="Result.IsSuccess"/> and reads <see cref="Result.Error"/> through one contract.
/// <para>
/// A <see langword="null"/> <see cref="Value"/> on a <em>successful</em> outcome means absent, not
/// failed - collapsing "succeeded with nothing to return" into "failed" would change branch
/// outcomes, which is why the success factories accept null. Reading <see cref="Value"/> on a
/// failed outcome is a programming error, not a domain condition.
/// </para>
/// </remarks>
public sealed class Result<T> : Result
{
    /// <summary>
    /// The carried value, held in a field so that <see cref="Value"/> can guard access without
    /// exposing a second, unguarded accessor.
    /// </summary>
    private readonly T _value;

    /// <summary>
    /// Initialises a new outcome. Private, so that a failed outcome can never be paired with a
    /// meaningful value.
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

    /// <summary>Gets the value produced by a successful operation.</summary>
    /// <value>
    /// The produced value, which may itself be <see langword="null"/> when <typeparamref name="T"/>
    /// permits it - meaning the operation succeeded and the value is absent, <em>not</em> that it
    /// failed.
    /// </value>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the operation failed: the caller skipped its <see cref="Result.IsSuccess"/>
    /// check. A plain framework exception rather than the domain exception, because misusing this
    /// surface is a defect in the calling code and not a violated business invariant.
    /// </exception>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException(
            "The value of a failed result cannot be read. Check IsSuccess before reading Value; " +
            $"the failure is described by Error. Reason code: {Reason?.Code ?? "(none supplied)"}.");

    /// <summary>
    /// Creates a successful outcome carrying <paramref name="value"/> and no reason.
    /// </summary>
    /// <param name="value">
    /// The produced value. May be <see langword="null"/> when <typeparamref name="T"/> permits it,
    /// which records an absent value rather than a failure.
    /// </param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>
    /// Creates a successful outcome carrying both <paramref name="value"/> and an advisory reason.
    /// </summary>
    /// <param name="value">
    /// The produced value. May be <see langword="null"/> when <typeparamref name="T"/> permits it.
    /// </param>
    /// <param name="reason">The advisory reason to attach to the success.</param>
    /// <returns>A successful <see cref="Result{T}"/> carrying <paramref name="reason"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>; the guard constrains the
    /// reason only, since <paramref name="value"/> may legitimately be <see langword="null"/>.
    /// </exception>
    public static Result<T> Success(T value, ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result<T>(true, value, reason);
    }

    /// <summary>
    /// Creates a failed outcome. Hides the non-generic <see cref="Result.Failure(ResultReason)"/>
    /// so that the strongly typed outcome is returned instead of its base type.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>: a failure always names the
    /// expected condition that occurred.
    /// </exception>
    public static new Result<T> Failure(ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result<T>(false, default!, reason);
    }

    /// <summary>
    /// Creates a failed outcome from a discriminator and a message. Hides the non-generic
    /// <see cref="Result.Failure(string, string)"/> so that the strongly typed outcome is returned
    /// instead of its base type.
    /// </summary>
    /// <param name="code">
    /// Stable, machine-readable discriminator identifying which expected failure occurred.
    /// </param>
    /// <param name="message">Human-readable explanation of the failure.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only
    /// of white-space characters.
    /// </exception>
    public static new Result<T> Failure(string code, string message) => new(false, default!, new ResultReason(code, message));
}
