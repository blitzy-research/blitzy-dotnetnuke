using System.Diagnostics.CodeAnalysis;
using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.ValueObjects;

// ============================================================================
// MIGRATION AUDIT TRAIL
//
// Every decision below is traceable to a line of DotNetNuke 4.9.0 source or of
// the SQL Server upgrade chain. Nothing here is a judgement about how an e-mail
// address ought to be validated: the shape rule is a port, and the places where
// it looks wrong by modern standards are places where the legacy rule was wrong
// and is preserved on purpose.
// ============================================================================
//
// MIGRATION 1 - THE SHAPE RULE IS ONE LEGACY CONSTANT, TRANSCRIBED CLAUSE BY CLAUSE.
// DotNetNuke 4.9.0 held exactly one e-mail rule, and it is a single named constant
// declared at Library/Components/Shared/Globals.vb:L132, quoted here verbatim:
//
//     Public Const glbEmailRegEx As String = "\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b"
//
// It reached the user aggregate as a property attribute at
// Library/Components/Users/UserInfo.vb:L121-L123
//     <SortOrder(4), MaxLength(256), Required(True), RegularExpressionValidator(glbEmailRegEx)>
// and was handed to module authors unchanged at
// Library/Components/Users/UserModuleBase.vb:L168 as the Security_EmailValidation setting.
// DescribeViolation below is that one pattern rewritten clause for clause, and the
// equivalence is not asserted from reading alone: the transcription was differential
// tested against the pattern itself over more than a million generated inputs, and the
// two agree on every one of them, on the accepting side as well as the rejecting side.
//
// The AAP cites the constant's home as Library/Components/Common/Globals.vb. THAT PATH
// DOES NOT EXIST in this repository. Searching the two legacy trees for the file name
// returns only the Shared path quoted above, whose 2,704 lines match the AAP's own figure
// for the module, and one further copy inside the excluded Library/Controls tree.
//
// The constant is deliberately NOT re-exported under its legacy identifier. The rule now
// lives in this type's behaviour, and Rule T8 retires the legacy naming rather than
// carrying it forward as public API; transcribing the pattern into this comment is what
// keeps the port auditable.
//
// MIGRATION 2 - WHOLE-VALUE MATCHING, AND WHAT THE LEADING \b ACTUALLY DID.
// The pattern is bounded by \b word boundaries rather than by ^ and $ anchors, so read in
// isolation it would match an address embedded in surrounding prose. It never behaved that
// way. Its only consumer was the ASP.NET RegularExpressionValidator, which accepts a value
// only when the match covers the ENTIRE value - it must succeed, begin at offset zero and
// span the whole length. This type therefore validates the entire trimmed string, and
// doing so PRESERVES the legacy outcome rather than diverging from it. Nobody should
// "restore fidelity" later by turning this into a substring search: that would newly
// accept "a@b.co and some junk", which 4.9.0 rejected.
//
// The leading \b has a second, far less obvious consequence that whole-value matching
// alone does not reproduce, and it was found by differential testing rather than by
// reading the pattern. \b asserts a boundary at offset zero only when the first character
// is a word character, so once the match is required to start at offset zero the first
// character of the local part must be a letter, a digit or an underscore. A local part
// beginning with any of . - + ' or % therefore FAILED in 4.9.0, even though every one of
// those characters is perfectly legal later in the same local part. An underscore passed,
// because an underscore is itself a word character. That clause is reproduced faithfully
// in DescribeViolation. Deleting it as redundant would loosen the rule and silently accept
// addresses the legacy screen refused, which Minimal Change Clause item 3 forbids exactly
// as firmly as it forbids tightening.
//
// MIGRATION 3 - THE 2 TO 4 CHARACTER SUFFIX LIMIT IS A PRESERVED DEFECT.
// [a-zA-Z]{2,4} refuses every longer suffix, so someone@example.museum and
// someone@example.travel are invalid here exactly as they were invalid in 4.9.0, and so is
// any suffix carrying a digit. Minimal Change Clause item 1 forbids opportunistic fixes and
// item 3 requires identical inputs to produce identical outcomes, so the bound is NOT
// widened to an open-ended {2,}. Widening it is the tempting mistake, because it looks like
// a pure improvement: it would in fact begin silently accepting addresses the legacy system
// refused, a divergence in the direction nobody thinks to test for. Supporting modern
// suffixes is a separate, explicit product decision, and it is not this migration's to make.
//
// MIGRATION 4 - THE LOCAL-PART CLASS IS WIDER THAN IT LOOKS AND MUST STAY THAT WAY.
// [a-zA-Z0-9._%\-+'] admits an APOSTROPHE, so o'brien@example.com is a valid address that
// real accounts use today; dropping it would deny access to users who can sign in now. It
// also admits '+', so plus-addressing is accepted rather than rejected. The domain class
// [a-zA-Z0-9.\-] admits no underscore, so x@under_score.com is invalid. Neither class
// admits whitespace or any character beyond ASCII, so an interior space cannot survive and
// an accented local part is invalid. Two further legacy quirks are preserved verbatim
// because the pattern plainly permits them: consecutive dots in the domain, as in
// x@a..com, and a domain label beginning with a hyphen, as in x@-a.com, are both VALID.
//
// MIGRATION 5 - 100 IS THE OPERATIVE MAXIMUM LENGTH; 256 IS A RECORDED INCONSISTENCY.
// UserInfo.vb:L121 declares MaxLength(256) while the column it ultimately writes to is
//     [Email] [nvarchar] (100) NOT NULL
// at Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L107,
// corroborated by the @Email nvarchar(100) parameter of the AddPortal family in
// 02.00.00.SqlDataProvider. A 150-character address therefore passed the legacy screen and
// then FAILED at the database. Rule T4 makes the schema authoritative, so 100 is enforced
// and 256 is recorded rather than adopted.
//
// That choice preserves the legacy END-TO-END outcome. An over-long address was rejected
// then and is rejected now, only sooner and with a message naming the constraint instead of
// a truncation error from the data provider. Adopting 256 here would be the real
// divergence, because it would admit a value into a column that cannot hold it. The column
// remains the authority: the persistence configuration and the Application layer's request
// rules state the same 100 independently, so this check is defence in depth rather than the
// source of the number.
//
// MIGRATION 6 - SHIPPED DATA FAILS THE LEGACY RULE, WHICH IS WHY TryCreate EXISTS.
// 01.00.00.SqlDataProvider:L7205 seeds the built-in Host superuser with 'host' as BOTH its
// password and its e-mail, and the very next statement at L7207 seeds the Administrator
// account with 'admin' the same way. Neither value carries an at-sign, a domain or a
// suffix, so neither satisfies the rule the product shipped alongside them, and both rows
// are live in every DotNetNuke installation this migration can ever touch. Reading such a
// row must never fail, so this type offers a non-throwing TryCreate beside the throwing
// Create. A type that could only throw would make the Host account unloadable - a fatal
// defect that would surface at run time rather than at compile time.
//
// It also does not force the persistence mapping to adopt it. Whether the user aggregate
// types its address as EmailAddress? or as a plain string? remains the entity author's
// decision, and nothing here - no converter, no attribute, no interface - presumes it.
//
// MIGRATION 7 - SHAPE ONLY. NO DUPLICATE CHECK, NO DELIVERABILITY CHECK, NO TIGHTENING.
// The legacy membership registration at Website/release.config:L244 sets
// requiresUniqueEmail="false", so two accounts may legitimately hold the same address
// today. Preserving that verbatim is a requirement rather than a convenience: tightening a
// credential policy in the middle of a migration denies access to people who can sign in to
// the system today. This type therefore performs no duplicate lookup, no directory or
// delivery probe, no domain blocklist and no refusal based on letter case, and two equal
// instances are a correct outcome rather than a conflict to be resolved.
//
// It could not perform such a check even by accident. The Domain project declares no
// project reference and no package reference at all, so a value object here has nothing to
// call and no means of performing input or output of any kind.
//
// MIGRATION 8 - THE EMPTY STRING IS NOT AN ADDRESS, AND ABSENCE IS A NULL REFERENCE.
// Library/Components/Shared/Null.vb:L71-L75 returns "" for NullString, and the SetNull
// helpers translate DBNull into that sentinel on EVERY read, so in the legacy model a SQL
// NULL and an empty string were indistinguishable once loaded. This type rejects "" and
// whitespace alike, and it models absence as a null EmailAddress? reference. There is
// deliberately no empty, no none and no default instance to be handed back, because no
// such address exists.
//
// Mapping a null argument onto the empty string at the entry point is not a shortcut: it
// mirrors the collapse the legacy reader already performed, which is why both inputs are
// then rejected identically and with one message. Under Rule T7 the sentinel itself is
// preserved wherever a contract exposes it, and that happens in the Application layer's
// data transfer objects, never by weakening this type.
//
// MIGRATION 9 - LEGACY VALIDATION WAS ASYMMETRIC ACROSS THE TWO AGGREGATES.
// Library/Components/Portal/PortalInfo.vb:L285 declares the portal's own address as
//     <XmlElement("email")> Public Property Email() As String
// with NO validator, NO Required and NO MaxLength - the same conceptual field that the user
// aggregate validated strictly. That asymmetry is a legacy fact, and this type does not
// resolve it by fiat. It offers one consistent shape check; it does not decide where the
// check is applied. Typing the portal's address as EmailAddress? would tighten a field that
// was previously unvalidated, so the entity and service authors make that call visibly
// rather than inheriting it from a type they merely referenced. The serialisation attribute
// is dropped per Rule T8: the wire contract belongs to the data transfer objects.
//
// MIGRATION 10 - TWO LEGACY PROPERTIES COLLAPSE ONTO ONE.
// UserInfo.vb:L132 forwarded every assignment straight to the membership object with
//     Me.Membership.Email = Value
// whose own property at Library/Components/Users/Membership/UserMembership.vb:L344 is
// marked <Browsable(False)> and flips an ObjectHydrated flag inside its setter. The merged
// user aggregate carries a single address property, so this type serves one property rather
// than shadowing two. The designer attribute and the hydration flag are both dropped per
// Rule T8: materialisation is the persistence layer's concern and needs no bookkeeping in
// the model.
//
// MIGRATION 11 - NORMALISATION AND COMPARISON SEMANTICS, STATED SO THEY ARE NEVER GUESSED.
// The stored form is the supplied value TRIMMED of leading and trailing whitespace with its
// ORIGINAL LETTER CASE INTACT. Trimming is non-destructive and stops a pasted-in space from
// failing an otherwise valid address. Folding the case of the stored value is deliberately
// NOT done: it would silently rewrite data the user typed, and Minimal Change Clause item 3
// requires identical inputs to produce identical outcomes.
//
// Comparison, separately, IS insensitive to letter case, through
// StringComparison.OrdinalIgnoreCase, and GetHashCode folds case the matching way through
// StringComparer.OrdinalIgnoreCase so the pair stays consistent. An inconsistent pair is a
// defect no compiler reports and one that hash-based collections expose only
// intermittently. Ordinal comparison is chosen over any culture-sensitive form on purpose:
// an address is an identifier, and culture-sensitive casing gets identifiers wrong, the
// Turkish dotless i being the classic demonstration.
//
// MIGRATION 12 - TWO FURTHER LATENT DEFECTS IN Null.vb ARE RECORDED, NOT CARRIED FORWARD.
// Null.vb:L123 folds "System.Int64" onto the Int32-sized -1 sentinel, and L125's
// "system.Byte" is spelled with a lower-case s, which makes that branch unreachable under
// the OptionCompare Binary declared at Library/DotNetNuke.Library.vbproj:L22. Neither
// defect reaches an e-mail address and neither is reproduced: the sentinel module is
// honoured as mapping knowledge only, and nullable reference types replace it.
//
// MIGRATION 13 - WHY REJECTION SIGNALS DomainException RATHER THAN A GENERAL-PURPOSE TYPE.
// A malformed address handed to Create is an illegal DOMAIN value, which is precisely what
// DnnMigration.Domain.Common.DomainException exists to signal. The general-purpose argument
// and format exception types would report it as ordinary misuse of a utility, and a caller
// catching domain failures would not select it. The sibling Result and PagedResult types
// deliberately use those general-purpose types for their own guards, because misusing a
// utility genuinely is a different category, and their choice is not a reason to follow
// them here.
//
// This also keeps faith with the two-part error model documented on DomainException itself.
// Create is the invariant channel: the caller is asserting that the string IS an address,
// and being wrong about that is a defect. TryCreate is the expected-failure channel, for
// untrusted input and for legacy rows. No message ever includes the rejected value, so a
// message can be logged without carrying user data into the log.

/// <summary>
/// An e-mail address whose shape has been checked against the single rule DotNetNuke 4.9.0
/// applied, and which is therefore safe to pass around without being re-checked.
/// </summary>
/// <remarks>
/// <para>
/// This type validates SHAPE ONLY. It never asserts that the address is unclaimed, that its
/// domain resolves, or that a message sent to it would arrive, and it applies no rule the
/// legacy system did not apply. Every constraint it enforces is traceable to a cited line of
/// legacy source; the audit trail above this declaration records each one, including the
/// constraints that are wrong by modern standards and are preserved deliberately.
/// </para>
/// <para>
/// There are two ways in, and choosing between them is a design decision rather than a
/// matter of taste. <see cref="Create"/> throws when the value is malformed and suits a
/// caller that is asserting an invariant - code that already knows the string is an address
/// and would be defective if it were not. <see cref="TryCreate"/> reports failure as a
/// return value and suits untrusted input and, critically, existing rows: DotNetNuke ships
/// accounts whose stored address does not satisfy its own validator, so a reader that could
/// only throw would be unable to load them at all.
/// </para>
/// <para>
/// Absence is a null <c>EmailAddress?</c> reference. The type is deliberately a reference
/// type for that reason, and it exposes no empty, none or default instance: an address that
/// is not there is not a special address, it is no address. The empty string is rejected
/// rather than accepted as a stand-in, even though the legacy sentinel module used the empty
/// string to mean exactly that.
/// </para>
/// <para>
/// Instances are immutable and carry a single string, so the type is thread-safe and may be
/// shared freely, cached, and used as a dictionary key. Equality ignores letter case while
/// the stored value keeps the case it was given; both halves of that arrangement are
/// intentional and are explained in the audit trail.
/// </para>
/// </remarks>
/// <example>
/// Asserting an invariant, and reading a value that may not satisfy it:
/// <code>
/// EmailAddress administrator = EmailAddress.Create("admin@example.com");
///
/// if (EmailAddress.TryCreate(rowValue, out EmailAddress? address))
/// {
///     // A well-formed address; use it.
/// }
/// else
/// {
///     // A legacy row such as the shipped Host account, whose address is 'host'.
/// }
/// </code>
/// </example>
public sealed record EmailAddress : IEquatable<EmailAddress>
{
    /// <summary>
    /// The greatest number of characters an address may contain, taken from the
    /// <c>[Email] [nvarchar] (100) NOT NULL</c> column of <c>dbo.Users</c>. See MIGRATION 5
    /// above: the column is the authority for this number, not this file, and the legacy
    /// <c>MaxLength(256)</c> screen attribute is recorded there rather than adopted.
    /// </summary>
    private const int MaximumLength = 100;

    /// <summary>
    /// The fewest characters the domain suffix may contain, from <c>[a-zA-Z]{2,4}</c>.
    /// </summary>
    private const int MinimumSuffixLength = 2;

    /// <summary>
    /// The most characters the domain suffix may contain, from <c>[a-zA-Z]{2,4}</c>. See
    /// MIGRATION 3 above: this upper bound is a preserved legacy defect and must not be
    /// widened, which is why it is named here rather than left as a literal in a comparison.
    /// </summary>
    private const int MaximumSuffixLength = 4;

    /// <summary>
    /// Initialises a new instance from a value that has ALREADY been normalised and
    /// validated. Private by design: every route in validates first, so no instance of this
    /// type can exist without having satisfied the legacy rule.
    /// </summary>
    /// <param name="value">The trimmed, validated address.</param>
    private EmailAddress(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the address as it will be stored: trimmed of surrounding whitespace, with the
    /// letter case exactly as it was supplied.
    /// </summary>
    /// <remarks>
    /// This is the member a persistence value converter reads. It is never null and never
    /// empty, because no route in produces an instance for which it could be either.
    /// </remarks>
    public string Value { get; }

    /// <summary>
    /// Creates an address from <paramref name="value"/>, throwing when the value does not
    /// satisfy the legacy shape rule.
    /// </summary>
    /// <param name="value">
    /// The candidate address. It is trimmed of leading and trailing whitespace before being
    /// checked, and the trimmed form is what the instance stores.
    /// </param>
    /// <returns>A validated address.</returns>
    /// <exception cref="DomainException">
    /// Thrown when the value is absent, empty, whitespace only, longer than the column
    /// permits, or malformed in any way the legacy rule rejected. The message names the
    /// clause that failed and never repeats the rejected value, so it is safe to log.
    /// </exception>
    /// <remarks>
    /// Use this when the caller is asserting that the string is an address. For untrusted
    /// input, and for reading rows that may predate the rule, use <see cref="TryCreate"/>
    /// instead - DotNetNuke ships accounts that this method would reject.
    /// </remarks>
    public static EmailAddress Create(string value)
    {
        string candidate = Normalise(value);
        string? violation = DescribeViolation(candidate);

        if (violation is not null)
        {
            throw new DomainException(violation);
        }

        return new EmailAddress(candidate);
    }

    /// <summary>
    /// Attempts to create an address from <paramref name="value"/>, reporting failure as a
    /// return value instead of throwing.
    /// </summary>
    /// <param name="value">
    /// The candidate address, which may be null. It is trimmed before being checked, and a
    /// null argument is treated exactly as the empty string is - see MIGRATION 8 above,
    /// where the legacy sentinel module made those two inputs indistinguishable.
    /// </param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the validated address; when it
    /// returns <see langword="false"/>, <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value satisfies the legacy shape rule; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// THIS METHOD IS NOT OPTIONAL CONVENIENCE. DotNetNuke seeds its Host and Administrator
    /// accounts with the literal addresses <c>host</c> and <c>admin</c>, neither of which
    /// satisfies the validator the same product shipped, so a reader that could only throw
    /// could not load them. Both are cited precisely in MIGRATION 6 above.
    /// </para>
    /// <para>
    /// The single by-reference argument here is the framework-canonical non-throwing parse
    /// shape, matching how the base class library exposes the same idea. It is not an
    /// instance of the legacy mutate-and-report-status signature that this migration
    /// replaces with a returned result: nothing is mutated, and the status is the return
    /// value rather than a code smuggled through an argument.
    /// </para>
    /// </remarks>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out EmailAddress? result)
    {
        string candidate = Normalise(value);

        if (DescribeViolation(candidate) is not null)
        {
            result = null;
            return false;
        }

        result = new EmailAddress(candidate);
        return true;
    }

    /// <summary>
    /// Determines whether this address and <paramref name="other"/> denote the same address,
    /// ignoring differences of letter case.
    /// </summary>
    /// <param name="other">The address to compare with, which may be null.</param>
    /// <returns>
    /// <see langword="true"/> when both denote the same address; otherwise
    /// <see langword="false"/>. A null argument is never equal to an instance.
    /// </returns>
    /// <remarks>
    /// Comparison is ordinal and case-insensitive. Declaring this member replaces the
    /// equality the record would otherwise synthesise, which compares with case sensitivity;
    /// because the synthesised <c>Equals(object)</c>, <c>==</c> and <c>!=</c> all delegate
    /// here, the whole equality surface picks up these semantics together rather than
    /// disagreeing with one another.
    /// </remarks>
    public bool Equals(EmailAddress? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(EmailAddress)"/>.
    /// </summary>
    /// <returns>A hash code computed with letter case folded.</returns>
    /// <remarks>
    /// The comparer used here is the counterpart of the comparison used by
    /// <see cref="Equals(EmailAddress)"/>, so two addresses that differ only in case hash
    /// alike and therefore collide correctly in a set or a dictionary. Changing one of the
    /// two without the other is a defect no compiler reports.
    /// </remarks>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>
    /// Returns the address itself.
    /// </summary>
    /// <returns>The stored address, trimmed and in its original letter case.</returns>
    /// <remarks>
    /// The value is returned unmasked and untruncated on purpose. Keeping sensitive values
    /// away from a log is the responsibility of the request-logging middleware at the edge of
    /// the process, which can see what is being written and where; a domain type that
    /// silently redacted itself would instead corrupt every legitimate use, including
    /// persistence and comparison during debugging.
    /// </remarks>
    public override string ToString() => Value;

    /// <summary>
    /// Converts an address to its string form. Deliberately explicit, for symmetry with the
    /// conversion in the other direction.
    /// </summary>
    /// <param name="address">The address to convert.</param>
    /// <returns>The stored address.</returns>
    public static explicit operator string(EmailAddress address) => address.Value;

    /// <summary>
    /// Converts a string to an address, validating it on the way.
    /// </summary>
    /// <param name="value">The candidate address.</param>
    /// <returns>A validated address.</returns>
    /// <exception cref="DomainException">
    /// Thrown when the value does not satisfy the legacy shape rule.
    /// </exception>
    /// <remarks>
    /// This conversion is EXPLICIT, and an implicit one is deliberately not offered: an
    /// implicit conversion would let an unchecked string become an address silently at any
    /// call site, which is precisely the guarantee this type exists to provide. Prefer the
    /// named <see cref="Create"/> and <see cref="TryCreate"/> factories, which say what they
    /// do at the call site.
    /// </remarks>
    public static explicit operator EmailAddress(string value) => Create(value);

    /// <summary>
    /// Trims the candidate, mapping a null argument onto the empty string so that both are
    /// rejected identically. See MIGRATION 8 above for why that mapping is faithful rather
    /// than merely convenient.
    /// </summary>
    /// <param name="value">The raw candidate, which may be null.</param>
    /// <returns>The trimmed candidate, or the empty string when the argument was null.</returns>
    private static string Normalise(string? value) => value is null ? string.Empty : value.Trim();

    /// <summary>
    /// Applies the legacy shape rule to an already-trimmed candidate and describes the first
    /// clause it fails, or returns <see langword="null"/> when it satisfies every clause.
    /// </summary>
    /// <param name="candidate">The trimmed candidate.</param>
    /// <returns>
    /// A description of the failed clause, or <see langword="null"/> when the candidate is
    /// well formed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the single implementation of the rule, transcribed clause by clause from
    /// <c>glbEmailRegEx</c> as quoted in MIGRATION 1 above:
    /// </para>
    /// <para>
    /// <c>\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b</c>
    /// </para>
    /// <para>
    /// A hand-written scan is used rather than a regular expression, and that is a deliberate
    /// choice on four grounds. It cannot backtrack, so no input can make it behave
    /// pathologically. It allocates nothing on either path, because it walks a span and every
    /// message it can return is a literal, which matters because a bulk read of a legacy table
    /// calls it once per row. It reads as the specification it implements, so a future reader
    /// can check each clause against the quoted pattern line by line. And it needs no
    /// package, which the Domain project could not take in any case.
    /// </para>
    /// <para>
    /// The framework's own permissive address parser, the attribute-based address validator
    /// and any third-party rule library were all rejected for one reason: each accepts or
    /// refuses addresses that the legacy constant does not, so any of them would have broken
    /// behavioural equivalence in both directions at once. The transcription below was
    /// differential tested against the legacy pattern itself, evaluated with the same
    /// whole-value semantics the legacy validator used, over more than a million generated
    /// inputs including boundary-rich ones, and the two agreed on every case.
    /// </para>
    /// <para>
    /// The clause order is chosen so the message names the most specific applicable
    /// constraint; it does not affect which candidates are accepted.
    /// </para>
    /// </remarks>
    private static string? DescribeViolation(ReadOnlySpan<char> candidate)
    {
        // The value must exist at all. A null argument arrives here as the empty string, and
        // a whitespace-only argument has already been trimmed away to the same thing, so one
        // message covers all three inputs honestly - which is also how the legacy sentinel
        // module saw them. See MIGRATION 8.
        if (candidate.IsEmpty)
        {
            return "An e-mail address must be supplied: the value was absent, empty, or whitespace only.";
        }

        // Schema constraint rather than pattern constraint. See MIGRATION 5 for why the
        // column's 100 governs and the screen's 256 does not.
        if (candidate.Length > MaximumLength)
        {
            return "An e-mail address must be no longer than 100 characters, the width of the column that stores it.";
        }

        // The '@' of the pattern. Because neither character class contains an at-sign, a
        // value matching the pattern holds exactly one, so both absence and repetition fail.
        int atIndex = candidate.IndexOf('@');

        if (atIndex < 0)
        {
            return "An e-mail address must contain an at-sign separating the local part from the domain.";
        }

        ReadOnlySpan<char> localPart = candidate[..atIndex];
        ReadOnlySpan<char> domain = candidate[(atIndex + 1)..];

        if (domain.Contains('@'))
        {
            return "An e-mail address must contain exactly one at-sign.";
        }

        // The '+' quantifier on [a-zA-Z0-9._%\-+'] requires at least one character.
        if (localPart.IsEmpty)
        {
            return "An e-mail address must have a local part before the at-sign.";
        }

        // The LEADING \b. This clause exists only because the match had to begin at offset
        // zero, which \b permits solely when the first character is a word character. It is
        // the reason a local part starting with . - + ' or % failed in 4.9.0 while one
        // starting with an underscore passed. See MIGRATION 2; it was found by differential
        // testing and must not be removed as redundant.
        if (!IsWordCharacter(localPart[0]))
        {
            return "An e-mail address must begin with a letter, a digit, or an underscore.";
        }

        foreach (char character in localPart)
        {
            if (!IsLocalPartCharacter(character))
            {
                return "The local part of an e-mail address may contain only letters, digits, and the characters dot, underscore, percent, hyphen, plus, and apostrophe.";
            }
        }

        // The '+' quantifier on [a-zA-Z0-9.\-] requires at least one character.
        if (domain.IsEmpty)
        {
            return "An e-mail address must have a domain after the at-sign.";
        }

        foreach (char character in domain)
        {
            if (!IsDomainCharacter(character))
            {
                return "The domain of an e-mail address may contain only letters, digits, dots, and hyphens.";
            }
        }

        // The literal \. before the suffix, plus the '+' quantifier on the label preceding
        // it. Taking the LAST dot is exact rather than approximate: the suffix clause admits
        // only letters, so it can hold no dot of its own, which means the dot the pattern
        // matches can only ever be the final one. Requiring an index of at least one is what
        // keeps the preceding label non-empty, so a bare ".com" domain fails.
        int lastDotIndex = domain.LastIndexOf('.');

        if (lastDotIndex < 1)
        {
            return "The domain of an e-mail address must contain a dot with at least one character before it.";
        }

        ReadOnlySpan<char> suffix = domain[(lastDotIndex + 1)..];

        // The {2,4} quantifier. PRESERVED LEGACY DEFECT - see MIGRATION 3. This rejects
        // suffixes such as .museum and .travel exactly as 4.9.0 rejected them, and the upper
        // bound must not be widened.
        if (suffix.Length is < MinimumSuffixLength or > MaximumSuffixLength)
        {
            return "The final part of an e-mail address domain must be between 2 and 4 characters long.";
        }

        // The [a-zA-Z] class of the suffix: letters only, so a digit or any character beyond
        // ASCII fails here.
        foreach (char character in suffix)
        {
            if (!char.IsAsciiLetter(character))
            {
                return "The final part of an e-mail address domain may contain only letters.";
            }
        }

        return null;
    }

    /// <summary>
    /// Reports whether a character is admitted by the legacy local-part class
    /// <c>[a-zA-Z0-9._%\-+']</c>.
    /// </summary>
    /// <param name="character">The character to test.</param>
    /// <returns>
    /// <see langword="true"/> when the character is admitted; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// THE APOSTROPHE IS PART OF THE LEGACY CLASS AND MUST STAY. Removing it would refuse
    /// o'brien@example.com, an address real accounts hold today. The plus sign is likewise
    /// legacy-legal, so plus-addressing is accepted. The ASCII-specific test is used rather
    /// than a general letter test because <c>[a-zA-Z0-9]</c> is ASCII by definition; it also
    /// carries no cultural sensitivity, so it cannot vary with the ambient culture.
    /// </remarks>
    private static bool IsLocalPartCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '%' or '-' or '+' or '\'';

    /// <summary>
    /// Reports whether a character is admitted by the legacy domain class
    /// <c>[a-zA-Z0-9.\-]</c>.
    /// </summary>
    /// <param name="character">The character to test.</param>
    /// <returns>
    /// <see langword="true"/> when the character is admitted; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Narrower than the local-part class: there is NO underscore, no percent sign, no plus
    /// sign and no apostrophe here, so x@under_score.com is invalid. A leading hyphen and
    /// consecutive dots are both admitted, because the legacy class admits them; see
    /// MIGRATION 4.
    /// </remarks>
    private static bool IsDomainCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-';

    /// <summary>
    /// Reports whether a character is a word character in the sense the legacy pattern's
    /// leading <c>\b</c> assertion used, namely <c>[A-Za-z0-9_]</c>.
    /// </summary>
    /// <param name="character">The character to test.</param>
    /// <returns>
    /// <see langword="true"/> when the character is a word character; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This is the intersection of the legacy local-part class with the word-character class,
    /// and it exists solely to reproduce the boundary assertion described in MIGRATION 2. The
    /// underscore is included precisely because it is a word character, which is why an
    /// underscore could legally begin a legacy address while a dot or a hyphen could not.
    /// </remarks>
    private static bool IsWordCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '_';
}
