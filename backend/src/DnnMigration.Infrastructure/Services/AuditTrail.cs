using DnnMigration.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

// MIGRATION: this is the writing half of the audit trail the legacy application had and this migration
// had left unemitted. The events themselves are declared by the application layer, because only the
// application services know that a sign-in was refused or a tenant installed; the technology that writes
// them is here, because the application project declares FluentValidation and nothing else and therefore
// cannot name a logger at all.
//
// MIGRATION: the legacy destination is NOT reproduced. The legacy wrote through
// Services.Log.EventLog.EventLogController.AddLog into its own EventLog table, behind a swappable logging
// provider - a subsystem AAP 0.2.2.2 places out of scope, whose responsibilities AAP 0.1.2.1 assigns to
// structured logging instead. So the entries become log events rather than rows. What is preserved is the
// INTENT and the PROPERTY SET: an entry still exists for each audited business event, still carries the
// facts the legacy attached, and still declines to carry the credential the legacy also declined to carry.
//
// MIGRATION: each event carries a fixed identifier and a fixed name, assigned here. The application layer
// never names either, which is what lets the identifiers stay stable while the events are emitted from a
// layer that cannot see them.

/// <summary>
/// Writes the application's business audit events as structured log events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value is a structured property and no value is interpolated into a template.</b> That is the
/// structural answer to the concern the legacy addressed by filtering: the legacy passed a submitted
/// account name through its input filter with scripting, angle brackets and markup stripped
/// (<c>UserController.vb</c> L77) because the name was concatenated into a record that was later
/// rendered. Here a submitted value can only ever be a property OF an event, so it cannot alter the shape
/// of what is written and no filtering is required to make it safe.
/// </para>
/// <para>
/// <b>Levels are chosen by what the event means, not by how it ended.</b> A refused sign-in is a warning
/// because a run of them is the signal an operator wants; an accepted one is informational; a tenant
/// installation is informational because it is rare and consequential; a contained credential cost
/// upgrade failure is a warning, because the sign-in it happened during succeeded and nothing else would
/// report it.
/// </para>
/// <para>
/// <b>Nothing here is defensive.</b> No member catches, and none needs to: logging is contractually
/// non-throwing, and a handler here would recreate the defect this migration annotated at
/// <c>PortalController.vb</c> L1158-L1160, where the entire legacy audit block sat inside an EMPTY
/// exception handler and therefore lost the one record proving a portal had been installed.
/// </para>
/// <para>
/// The type is stateless beyond its logger and is safe to use from any number of requests at once, so it
/// is registered as a singleton.
/// </para>
/// </remarks>
internal sealed class AuditTrail : IAuditTrail
{
    /// <summary>Identifier of the sign-in outcome event.</summary>
    /// <remarks>
    /// The numbers in this file are the stable identifiers of the audited events and must not be reused
    /// or renumbered: an operator's alert rule and a log query both address an event by its identifier,
    /// so changing one silently detaches whatever was watching it. A new event takes the next number.
    /// </remarks>
    private const int SignInOutcomeEventId = 1001;

    /// <summary>Identifier of the tenant installation event.</summary>
    private const int PortalInstallationEventId = 1002;

    /// <summary>Identifier of the contained credential cost upgrade failure event.</summary>
    private const int CredentialCostUpgradeFailureEventId = 1003;

    /// <summary>
    /// Names of the outcomes that mean the caller was signed in, used to choose the level of the event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two weak-credential outcomes are in this set because the legacy made them ADVISORIES ON A
    /// SUCCESSFUL SIGN-IN rather than refusals: <c>UserController.vb</c> L1144-L1152 REPLACED an
    /// already-successful status with the insecure-administrator or insecure-host member and the caller
    /// was signed in regardless. Recording them as refusals would misreport what happened.
    /// </para>
    /// <para>
    /// The set is matched by NAME rather than by referencing the outcome enumeration, and the reason is
    /// the layering: the names arrive as text on the audited event, and this type deliberately knows
    /// nothing about the type that produced them. A name added to the enumeration without being
    /// considered here is reported as a refusal, which is the safe direction to be wrong in - an
    /// unrecognised outcome is escalated rather than quietly recorded as a success.
    /// </para>
    /// </remarks>
    private static readonly string[] AcceptedOutcomes =
    [
        "Success",
        "SuperUser",
        "InsecureAdminPassword",
        "InsecureHostPassword",
    ];

    private readonly ILogger<AuditTrail> _logger;

    /// <summary>Initialises a new instance of the <see cref="AuditTrail"/> class.</summary>
    /// <param name="logger">The logger the events are written to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public AuditTrail(ILogger<AuditTrail> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy raised this entry for the refused outcomes ONLY -
    /// <c>UserController.vb</c> L1138-L1141 covers the failure and locked-out members and nothing else.
    /// This emits for every outcome, which is a documented SUPERSET and never a subset. The reason is that
    /// the legacy's selection was a consequence of where the call sat rather than a decision about what is
    /// worth auditing, and a trail that records refusals but not acceptances cannot answer the first
    /// question asked of an authentication trail: who got in.
    /// </remarks>
    public void RecordSignInOutcome(SignInAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        bool accepted = Array.Exists(
            AcceptedOutcomes,
            candidate => string.Equals(candidate, audit.Outcome, StringComparison.Ordinal));

        _logger.Log(
            accepted ? LogLevel.Information : LogLevel.Warning,
            new EventId(SignInOutcomeEventId, "SignInOutcome"),
            "Sign-in {SignInOutcome} for account name {SubmittedUsername} against portal {PortalId} "
            + "({PortalName}), resolving to account {UserId}.",
            audit.Outcome,
            audit.Username,
            audit.PortalId,
            audit.PortalName,
            audit.UserId);
    }

    /// <inheritdoc />
    public void RecordPortalInstallation(PortalInstallationAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        // Written as one event with named properties rather than as the legacy's fourteen separate
        // property assignments. The property NAMES are what a query addresses, and they are spelled here
        // once; the legacy's embedded labels - "Install Portal:" and "Keywords:" - are not reproduced,
        // because a structured property already has a name and does not need one inside its value.
        _logger.Log(
            LogLevel.Information,
            new EventId(PortalInstallationEventId, "PortalInstalled"),
            "Portal {PortalId} ({PortalName}) installed at host {PortalAlias} with home directory "
            + "{HomeDirectory} from template {TemplateFile}; child portal {IsChildPortal}; description "
            + "{PortalDescription}; keywords {PortalKeywords}; administrator {AdministratorUsername} "
            + "({AdministratorFirstName} {AdministratorLastName}, {AdministratorEmail}).",
            audit.PortalId,
            audit.PortalName,
            audit.PortalAlias,
            audit.HomeDirectory,
            audit.TemplateFile,
            audit.IsChildPortal,
            audit.Description,
            audit.Keywords,
            audit.AdministratorUsername,
            audit.AdministratorFirstName,
            audit.AdministratorLastName,
            audit.AdministratorEmail);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The failure is attached AS an exception rather than rendered into the message, which is what keeps
    /// a hostile or verbose provider message out of the template while still recording it in full. The
    /// credential is not a parameter of the contract and therefore cannot reach this method to be logged.
    /// </remarks>
    public void RecordCredentialCostUpgradeFailure(int userId, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _logger.Log(
            LogLevel.Warning,
            new EventId(CredentialCostUpgradeFailureEventId, "CredentialCostUpgradeFailed"),
            failure,
            "The stored credential for account {UserId} could not be re-hashed at the current cost and "
            + "was left at the superseded cost. The sign-in succeeded and the caller was not affected; a "
            + "run of these events means the installation is not moving off the old cost.",
            userId);
    }
}
