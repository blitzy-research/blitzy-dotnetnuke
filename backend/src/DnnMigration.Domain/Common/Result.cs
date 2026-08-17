namespace DnnMigration.Domain.Common;

/// <summary>
/// Immutable description of a single outcome carried by a <see cref="Result"/> or a <see
/// cref="Result{T}"/>: a machine-readable <see cref="Code"/> paired with a human-readable <see
/// cref="Message"/>.
/// </summary>
public sealed record ResultReason
{
    /// <summary>Initialises a new <see cref="ResultReason"/>.</summary>
    /// <param name="code">Stable, machine-readable discriminator identifying which expected outcome occurred.</param>
    /// <param name="message">
    /// Human-readable explanation, suitable for surfacing to an administrator or for translation at the API
    /// boundary.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only of
    /// white-space characters.
    /// </exception>
    public ResultReason(string code, string message)
        : this(code, message, null)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="ResultReason"/> that additionally attributes its explanation to the
    /// individual fields that caused it.
    /// </summary>
    /// <param name="code">Stable, machine-readable discriminator identifying which expected outcome occurred.</param>
    /// <param name="message">
    /// Human-readable explanation of the outcome as a whole. When <paramref name="fieldErrors"/> names more
    /// than one field this is the explanation of the FIRST of them, so that a caller reading only the
    /// summary still receives an authored sentence rather than a count.
    /// </param>
    /// <param name="fieldErrors">
    /// One entry per field the caller must correct, each carrying that field's own messages;
    /// <see langword="null"/> when the failure is not attributable to particular fields.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only of
    /// white-space characters.
    /// </exception>
    /// <remarks>
    /// <b>WHY A FAILURE NEEDS TO KNOW WHICH FIELDS IT IS ABOUT.</b> A failed outcome used to carry only a
    /// code and one sentence, so the API boundary could only ever publish a flat problem document — and a
    /// client had nowhere to attach the refusal. Measured on the profile screen: a rejected write produced a
    /// banner and nothing else, so not one control was marked invalid and focus never moved to the field at
    /// fault. Naming the fields here is what lets the boundary publish RFC 7807's <c>errors</c> member, which
    /// is the contract the profile action has always ADVERTISED through its declared response type.
    /// </remarks>
    public ResultReason(
        string code,
        string message,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? fieldErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        FieldErrors = fieldErrors;
    }

    /// <summary>Gets the stable, machine-readable discriminator for this outcome.</summary>
    public string Code { get; }

    /// <summary>Gets the human-readable explanation of this outcome.</summary>
    public string Message { get; }

    /// <summary>
    /// Gets the per-field messages this outcome is attributable to, or <see langword="null"/> when it is not
    /// attributable to particular fields.
    /// </summary>
    /// <value>
    /// One entry per field the caller must correct. <see langword="null"/> — not an empty dictionary — is the
    /// representation of "not about any particular field", so the API boundary can tell the two apart and
    /// publish a flat problem document for the latter.
    /// </value>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? FieldErrors { get; }

    /// <summary>Returns this reason formatted as <c>Code: Message</c>.</summary>
    /// <returns>A diagnostic representation of this reason.</returns>
    public override string ToString() => $"{Code}: {Message}";
}

// Success is not binary in the legacy sign-in flow, which is why a successful Result can still carry a
// Reason.

/// <summary>
/// Outcome of an operation that either succeeded or failed for a reason the caller is expected to handle.
/// </summary>
public class Result
{
    /// <summary>
    /// Initialises a new outcome. <c>private protected</c> so that outcomes are built through the static
    /// factories and <see cref="Result{T}"/> is the only derived shape.
    /// </summary>
    /// <param name="isSuccess"><see langword="true"/> when the operation succeeded.</param>
    /// <param name="reason">The advisory or failure reason.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="isSuccess"/> is <see langword="false"/> and <paramref name="reason"/> is
    /// <see langword="null"/>: every failure is explicable.
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

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets a value indicating whether the operation failed. Always the exact negation of <see
    /// cref="IsSuccess"/>.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Gets the reason describing this outcome, or <see langword="null"/> when none was supplied.</summary>
    /// <remarks>
    /// A non-null value does not imply failure: on a success it is an advisory code, such as the
    /// weak-password caveat the legacy sign-in reported while still authenticating. Use <see cref="Error"/>
    /// for a reason guaranteed to describe a failure.
    /// </remarks>
    public ResultReason? Reason { get; }

    /// <summary>Gets the reason this operation failed, or <see langword="null"/> when it succeeded.</summary>
    public ResultReason? Error => IsSuccess ? null : Reason;

    /// <summary>Creates a successful outcome that carries no reason.</summary>
    /// <returns>A successful <see cref="Result"/>.</returns>
    public static Result Success() => new(true, null);

    /// <summary>Creates a successful outcome that carries an advisory reason.</summary>
    /// <param name="reason">The advisory reason to attach to the success.</param>
    /// <returns>A successful <see cref="Result"/> carrying <paramref name="reason"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>; use <see cref="Success()"/> for a
    /// success that needs no explanation.
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
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>: a failure always names the expected
    /// condition that occurred.
    /// </exception>
    public static Result Failure(ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result(false, reason);
    }

    /// <summary>Creates a failed outcome from a discriminator and a message.</summary>
    /// <param name="code">Stable, machine-readable discriminator identifying which expected failure occurred.</param>
    /// <param name="message">Human-readable explanation of the failure.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only of
    /// white-space characters.
    /// </exception>
    public static Result Failure(string code, string message) => new(false, new ResultReason(code, message));

    /// <summary>Returns a diagnostic representation of this outcome, naming the reason when one is present.</summary>
    /// <returns>A diagnostic representation of this outcome.</returns>
    public override string ToString()
    {
        string label = IsSuccess ? "Success" : "Failure";

        return Reason is { } reason ? $"{label} ({reason})" : label;
    }
}

/// <summary>
/// Outcome of an operation that either succeeded and produced a value, or failed for a reason the caller is
/// expected to handle.
/// </summary>
/// <typeparam name="T">Type of the value produced by a successful operation.</typeparam>
public sealed class Result<T> : Result
{
    /// <summary>
    /// The carried value, held in a field so that <see cref="Value"/> can guard access without exposing a
    /// second, unguarded accessor.
    /// </summary>
    private readonly T _value;

    /// <summary>
    /// Initialises a new outcome. Private, so that a failed outcome can never be paired with a meaningful
    /// value.
    /// </summary>
    /// <param name="isSuccess"><see langword="true"/> when the operation succeeded.</param>
    /// <param name="value">The produced value, or the type default when the operation failed.</param>
    /// <param name="reason">
    /// The advisory or failure reason, or <see langword="null"/> when the outcome needs no explanation.
    /// </param>
    private Result(bool isSuccess, T value, ResultReason? reason)
        : base(isSuccess, reason)
    {
        _value = value;
    }

    /// <summary>Gets the value produced by a successful operation.</summary>
    /// <value>
    /// The produced value, which may itself be <see langword="null"/> when <typeparamref name="T"/> permits
    /// it - meaning the operation succeeded and the value is absent, <em>not</em> that it failed.
    /// </value>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the operation failed: the caller skipped its <see cref="Result.IsSuccess"/> check.
    /// </exception>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException(
            "The value of a failed result cannot be read. Check IsSuccess before reading Value; " +
            $"the failure is described by Error. Reason code: {Reason?.Code ?? "(none supplied)"}.");

    /// <summary>Creates a successful outcome carrying <paramref name="value"/> and no reason.</summary>
    /// <param name="value">The produced value.</param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>Creates a successful outcome carrying both <paramref name="value"/> and an advisory reason.</summary>
    /// <param name="value">The produced value.</param>
    /// <param name="reason">The advisory reason to attach to the success.</param>
    /// <returns>A successful <see cref="Result{T}"/> carrying <paramref name="reason"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>; the guard constrains the reason
    /// only, since <paramref name="value"/> may legitimately be <see langword="null"/>.
    /// </exception>
    public static Result<T> Success(T value, ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result<T>(true, value, reason);
    }

    /// <summary>
    /// Creates a failed outcome. Hides the non-generic <see cref="Result.Failure(ResultReason)"/> so that
    /// the strongly typed outcome is returned instead of its base type.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reason"/> is <see langword="null"/>: a failure always names the expected
    /// condition that occurred.
    /// </exception>
    public static new Result<T> Failure(ResultReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return new Result<T>(false, default!, reason);
    }

    /// <summary>
    /// Creates a failed outcome from a discriminator and a message. Hides the non-generic <see
    /// cref="Result.Failure(string, string)"/> so that the strongly typed outcome is returned instead of
    /// its base type.
    /// </summary>
    /// <param name="code">Stable, machine-readable discriminator identifying which expected failure occurred.</param>
    /// <param name="message">Human-readable explanation of the failure.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code"/> or <paramref name="message"/> is empty or consists only of
    /// white-space characters.
    /// </exception>
    public static new Result<T> Failure(string code, string message) => new(false, default!, new ResultReason(code, message));
}
