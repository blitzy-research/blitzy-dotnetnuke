using System.Collections.Frozen;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;

namespace DnnMigration.Api.ErrorHandling;

/// <summary>Translates an application-layer outcome into an HTTP response.</summary>
/// <remarks>
/// <strong>The mapping keys on the WHOLE code, and every classification is declared.</strong> Failure codes
/// in this solution are dotted paths whose final segment names the reason, and the separator inside that last
/// token is not consistent because different services were written to use underscores and hyphens - so the
/// code is normalised and then looked up in <see cref="StatusByCode"/>. Nothing is inferred from how a code
/// is spelled.
/// </remarks>
public static class ApiResults
{
    /// <summary>
    /// Codes answered with <c>400</c>, because the caller can correct the request and submit it again.
    /// </summary>
    private static readonly string[] BadRequestCodes =
    {
        "auth.remediation.subject_unresolved", "auth.remediation_required", "auth.request_invalid",
        "module.content.not_supplied", "module.content_invalid", "module.content_type_mismatch",
        "module.controller.contract_not_supported", "module.controller.not_registered",
        "module.controller.not_specified", "module.move_destination_invalid", "module.not_portable",
        "module.request_invalid", "module.setting_invalid", "module.update.wide_effect",
        "permission.filter_invalid", "permission.key_invalid", "permission.module_foreign_tenant",
        "permission.module_unknown", "permission.request_invalid", "permission.tab_foreign_tenant",
        "permission.tab_unknown",
        "portal.administrator_invalid", "portal.delete.partially_applied", "portal.paging_invalid",
        "portal.parent_alias_too_deep", "portal.parent_alias_unresolved",
        "portal.permission_catalogue_incomplete", "portal.processor_reference_invalid",
        "portal.tab_reference_invalid", "portal.tenant_unresolved", "portal_alias_ambiguous",
        "portal_context_incomplete", "profile_definition.validation_expression_invalid", "request.failed",
        "request.invalid", "role.paging_invalid", "role.rsvp_code_too_weak",
        "role_assignment.dates_invalid", "role_assignment.expired_not_removed",
        "role_group.scope_invalid", "tab.name_reserved", "tab.paging_invalid", "tab.parent_cross_portal",
        "tab.parent_cycle",
        "tenant_path_prefix_mismatch", "user.choices.sort_unsupported", "user.create.invalid_email",
        "user.create.invalid_password", "user.create.invalid_username", "user.create.password_mismatch",
        "user.create.portal_assignment_failed", "user.display_name.too_long", "user.list.filter_invalid",
        "user.list.sort_unsupported", "user.membership_settings.invalid",
        "user.membership_settings.redirect_invalid", "user.membership_settings.redirect_not_in_portal",
        "user.password.current_incorrect", "user.password.invalid", "user.password.mismatch",
        "user.password.missing", "user.password.reset_failed", "user.password.reset_not_enabled",
        "user.password.unsupported_operation", "user.profile.property_validation_failed",
        "user.profile.required_property_missing", "user.profile.too_many_properties",
        "user.profile.value_too_long", "user.profile.visibility_invalid", "user.service.code_not_matched",
        "user.service.code_required", "user.service.paging_invalid",

        // `user.unlock.not_locked` was removed from this table along with the refusal it classified.
        // Clearing a lockout is idempotent, so an account that is already unlocked is answered as a success
        // rather than refused, and leaving the entry here would suggest a status this edge can no longer
        // produce.
    };

    /// <summary>
    /// Codes answered with <c>401</c>, because the caller has not proved who they are, or the credential
    /// presented is not usable.
    /// </summary>
    private static readonly string[] UnauthorizedCodes =
    {
        "auth.account_not_approved", "auth.insecure_admin_password", "auth.insecure_host_password",
        "auth.invalid_credentials", "auth.invalid_refresh_token", "auth.locked_out", "auth.unauthenticated",
        "auth.verification_code_invalid", "auth.verification_required", "refresh_token_alreadyused",
        "refresh_token_expired", "refresh_token_notfound", "refresh_token_revoked",
    };

    /// <summary>
    /// Codes answered with <c>403</c>, because the caller is authenticated and is still not permitted to do
    /// this.
    /// </summary>
    private static readonly string[] ForbiddenCodes =
    {
        "auth.not_permitted", "module.administrator_forbidden", "module.edit_forbidden",
        "module.settings_protected", "module.tenant_forbidden", "role.protected",
        "role_assignment.protected", "user.delete.administrator_protected",
        "user.delete.superuser_protected", "user.membership.self_forbidden",
        "user.password.change_self_only_forbidden", "user.password.reset_forbidden",
        "user.service.disabled_forbidden", "user.service.not_offered_forbidden",
        "user.service.payment_required_forbidden", "user.service.trial_not_offered_forbidden",
    };

    /// <summary>Codes answered with <c>404</c>, because the thing addressed does not exist.</summary>
    private static readonly string[] NotFoundCodes =
    {
        "auth.user_not_found", "module.definition_not_found", "module.not_found",
        "module.package_not_found", "module.placement_not_found", "module.portal_not_found",
        "module.tab_not_found",
        "permission.module_not_found", "permission.portal_not_found", "permission.role_not_found",
        "permission.tab_not_found", "permission.user_not_found", "portal.alias_not_found",
        "portal.not_found", "portal_alias_not_found", "profile_definition.not_found", "resource.not_found",
        "role.not_found", "role_assignment.not_found", "role_group.not_found", "tab.not_found",
        "tab.parent_not_found", "tab.portal_not_found", "user.list.unknown_profile_property",
        "user.not_found", "user.profile.unknown_property",
    };

    /// <summary>
    /// Codes answered with <c>405</c>, because the method is not supported for the resource.
    /// </summary>
    private static readonly string[] MethodNotAllowedCodes =
    {
        "request.method_not_allowed",
    };

    /// <summary>
    /// Codes answered with <c>406</c>, because no representation acceptable to the caller is available.
    /// </summary>
    private static readonly string[] NotAcceptableCodes =
    {
        "request.not_acceptable",
    };

    /// <summary>
    /// Codes answered with <c>409</c>, because the request conflicts with the state the resource is already
    /// in.
    /// </summary>
    private static readonly string[] ConflictCodes =
    {
        "persistence.conflict", "portal.administrator_duplicate", "portal.alias_duplicate",
        "portal.alias_in_use.conflict", "portal.concurrency_conflict", "portal.creation_conflict",
        "portal.last_remaining", "profile_definition.duplicate_name",
        // Both guards on withdrawing a profile declaration. 409 rather than 403: the caller is entitled to the
        // operation, and it is the state of the thing addressed - a reserved name, or answers that would be
        // destroyed - that refuses it. The second is resolvable by the caller, which is why it names the count
        // to send back rather than simply declining.
        "profile_definition.protected", "profile_definition.value_deletion_unacknowledged",
        "resource.conflict",
        "role.concurrency_conflict", "role.name_duplicate", "role_group.in_use",
        "role_group.name_duplicate", "user.approval.unchanged", "user.create.duplicate_email",
        "user.create.duplicate_username", "user.create.user_already_registered",
        "user.concurrency_conflict", "user.create.username_already_exists",
        "user.membership_settings.storage_conflict",
        "user.password.change_already_required", "user.password.not_different", "user.password.superseded",
        "user.profile.duplicate_property",
    };

    /// <summary>
    /// Codes answered with <c>413</c>, because the body is larger than the endpoint accepts.
    /// </summary>
    private static readonly string[] PayloadTooLargeCodes =
    {
        "request.too_large",
    };

    /// <summary>
    /// Codes answered with <c>415</c>, because the submitted media type is not supported.
    /// </summary>
    private static readonly string[] UnsupportedMediaTypeCodes =
    {
        "request.unsupported_media_type",
    };

    /// <summary>
    /// Codes answered with <c>422</c>, because the request was understood and still could not be processed.
    /// </summary>
    private static readonly string[] UnprocessableContentCodes =
    {
        "request.unprocessable",
    };

    /// <summary>Codes answered with <c>429</c>, because too many requests have been submitted.</summary>
    private static readonly string[] RateLimitedCodes =
    {
        "request.rate_limited",
    };

    /// <summary>
    /// Codes answered with <c>500</c>, because this application, not the caller, is at fault.
    /// </summary>
    private static readonly string[] InternalFailureCodes =
    {
        "module.content.export_failed", "module.content.import_failed",
        "module.controller.capability_probe_failed", "module.export_failed", "module.upgrade_failed",
        "portal.creation_failed", "role.create_failed", "server.unexpected_failure",
    };

    /// <summary>Codes answered with <c>501</c>, because the operation is not implemented.</summary>
    private static readonly string[] NotImplementedCodes =
    {
        "server.not_implemented",
    };

    /// <summary>Codes answered with <c>503</c>, because a dependency could not be reached.</summary>
    /// <remarks>
    /// <c>session_revocation_store_unavailable</c> WAS a member and is deliberately gone. It was the code a
    /// sign-out placed on a family this instance does not hold, and answering that condition 503 partitioned
    /// the token space for an anonymous caller - a live value answered 204 and an unknown one 503, which is a
    /// probe for which sessions exist. <c>AuthService.LogoutAsync</c> now reports a completed sign-out for
    /// every well-formed request and forwards <c>TOKEN_STORE_UNAVAILABLE</c> alone, which is still a member
    /// here, so an unreachable store is still answered 503 and nothing else on that path is.
    /// </remarks>
    private static readonly string[] UnavailableCodes =
    {
        "auth.approval_store_unavailable", "auth.credential_migration_store_unavailable",
        "auth.remediation.store_unavailable", "portal.member.credential.removal_store_unavailable",
        "portal.member.session.revocation_store_unavailable", "server.unavailable",
        "token_store_unavailable", "user.create.provider_error",
        "user.credential.removal_store_unavailable", "user.session.revocation_store_unavailable",
    };

    /// <summary>The status this edge answers each failure code with, keyed on the WHOLE normalised code.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Every entry is declared, and an unrecognised code is never classified by resemblance.</strong>
    /// This table replaced a set of reason-token substring tests, which read the final dotted segment of a
    /// code and asked whether it CONTAINED any of a handful of markers. That mechanism classified every code
    /// this solution actually emits correctly, and it was still wrong in kind: a code acquired a status by
    /// how it was spelled, so a future <c>portal.alias_in_use_check_failed</c> would have answered 409
    /// because its name contains <c>in_use</c>, and a genuinely new failure class would have become a silent
    /// 400. A lookup cannot do either.
    /// </para>
    /// <para>
    /// <strong>The key is the normalised code</strong> - see <see cref="Normalise"/>: trimmed, lower-cased,
    /// with hyphens folded onto underscores. Services disagree about the separator inside a reason token, so
    /// <c>user.not-found</c> and <c>user.not_found</c> are one entry rather than two, and a code spelled in
    /// capitals classifies as its lower-cased twin.
    /// </para>
    /// <para>
    /// <strong>What is in here.</strong> Every code the four layers place on a failed outcome, plus the codes
    /// the status vocabulary in <c>Api/Filters/ValidationProblemDetailsFactory.cs</c> and the authorisation
    /// result handler attach to responses whose status they set themselves. The second group cannot reach
    /// this method today - nothing puts them on a <see cref="Result"/> - and each is registered at the status
    /// its own producer publishes, so that surfacing one through an outcome later answers deliberately rather
    /// than defaulting. The refresh-token family is registered at 401 for the same reason: those codes are
    /// raised inside the token service and translated by <c>AuthService</c> before the edge sees them, and 401
    /// is what the removed <c>refresh_token.</c> prefix rule was written to give them.
    /// </para>
    /// <para>
    /// <strong>What is deliberately NOT in here</strong>, because none of it is an HTTP outcome: the
    /// <c>SYSTEM_*</c> permission-scope labels, the audit <c>FailureCode</c> on a credential-replacement
    /// audit record, the security-diagnostics label for a value that is not code-shaped, and the default
    /// language tag. A test enumerates the reason-code constants of all four assemblies and fails if one is
    /// missing from this table, so an addition cannot ship unclassified.
    /// </para>
    /// <para>
    /// ⚠ <strong>THIS FIELD MUST STAY BELOW THE GROUPS IT READS.</strong> Static field initialisers run in
    /// declaration order, so hoisting this one above the arrays leaves every one of them null when the
    /// builder runs and the type initializer throws - which is a <see cref="TypeInitializationException"/> on
    /// the first error any endpoint tries to report, not a compile error.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<string, int> StatusByCode = BuildStatusByCode();

    /// <summary>Builds the code-to-status table from the per-status groups.</summary>
    /// <returns>The frozen lookup <see cref="MapStatusCode"/> reads.</returns>
    /// <exception cref="InvalidOperationException">
    /// A code appears in more than one group, or a code is not spelled in its normalised form - either would
    /// make the table lie about what it answers, so it is refused at construction rather than at a call site.
    /// </exception>
    private static FrozenDictionary<string, int> BuildStatusByCode()
    {
        (int Status, string[] Codes)[] groups =
        {
        (StatusCodes.Status400BadRequest, BadRequestCodes),
        (StatusCodes.Status401Unauthorized, UnauthorizedCodes),
        (StatusCodes.Status403Forbidden, ForbiddenCodes),
        (StatusCodes.Status404NotFound, NotFoundCodes),
        (StatusCodes.Status405MethodNotAllowed, MethodNotAllowedCodes),
        (StatusCodes.Status406NotAcceptable, NotAcceptableCodes),
        (StatusCodes.Status409Conflict, ConflictCodes),
        (StatusCodes.Status413PayloadTooLarge, PayloadTooLargeCodes),
        (StatusCodes.Status415UnsupportedMediaType, UnsupportedMediaTypeCodes),
        (StatusCodes.Status422UnprocessableEntity, UnprocessableContentCodes),
        (StatusCodes.Status429TooManyRequests, RateLimitedCodes),
        (StatusCodes.Status500InternalServerError, InternalFailureCodes),
        (StatusCodes.Status501NotImplemented, NotImplementedCodes),
        (StatusCodes.Status503ServiceUnavailable, UnavailableCodes),
        };

        Dictionary<string, int> map = new(StringComparer.Ordinal);

        foreach ((int status, string[] codes) in groups)
        {
            foreach (string code in codes)
            {
                if (!string.Equals(code, Normalise(code), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant(
                            $"The failure code '{code}' is not in its normalised form, so no request could ever match it."));
                }

                if (!map.TryAdd(code, status))
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant(
                            $"The failure code '{code}' is classified twice, as {map[code]} and as {status}."));
                }
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Published in place of a failed outcome's own message when that message is absent, or when it does
    /// not have the shape of an authored explanation.
    /// </summary>
    /// <remarks>
    /// Deliberately identical in spirit to the unhandled-exception text: it says that the request did not
    /// complete and points at the one diagnostic handle a caller holds. It names no cause, because in the
    /// case that produces it the cause is precisely what must not be published.
    /// </remarks>
    private const string UnauthoredDetail =
        "The request could not be completed. Quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header when reporting this problem.";

    /// <summary>Longest detail this edge will publish from a failed outcome's own message.</summary>
    private const int MaximumPublishedDetailLength = 512;

    /// <summary>The failure code reported when a read succeeded but the thing addressed does not exist.</summary>
    /// <remarks>
    /// Stated as a constant so the problem type a client branches on is identical whichever endpoint
    /// produced it, and so that its status comes from <see cref="StatusByCode"/> rather than from a status
    /// code written out at the call site.
    /// </remarks>
    private const string ResourceNotFoundCode = "resource.not_found";

    /// <summary>The detail reported with a 404 produced from a successful outcome that carried no value.</summary>
    private const string ResourceNotFoundDetail = "The requested resource does not exist.";

    /// <summary>
    /// The media type every problem document is published as. Stated here because the per-field payload is
    /// returned through an <see cref="ObjectResult"/> built in this file rather than through
    /// <see cref="ControllerBase.Problem(string, string, int?, string, string)"/>, which sets it itself.
    /// </summary>
    private const string ProblemContentType = "application/problem+json";

    /// <summary>Translates an outcome that carries no value.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>204 No Content</c> on success, because the operation completed and there is nothing to return;
    /// otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult Complete(this ControllerBase controller, Result result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess ? controller.NoContent() : controller.Problem(result);
    }

    /// <summary>Translates an outcome that carries a value into the shared success envelope.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying an <see cref="ApiResponse{T}"/> around the value on success; <c>404 Not
    /// Found</c> carrying a problem-details payload when the outcome succeeded but carries no value,
    /// because a nullable value on a successful outcome is how this solution expresses "asked, and it is
    /// not there"; otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult<ApiResponse<TValue>> Complete<TValue>(
        this ControllerBase controller,
        Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        TValue value = result.Value;

        return value is null
            ? controller.NotFoundProblem()
            : controller.Ok(ApiResponse<TValue>.Success(value));
    }

    /// <summary>Translates an outcome that carries a domain page into the shared paging envelope.</summary>
    /// <typeparam name="TItem">The element type of the page.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="PagedResponse{T}"/> on success; otherwise a problem-details
    /// payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult<PagedResponse<TItem>> Complete<TItem>(
        this ControllerBase controller,
        Result<PagedResult<TItem>> result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        PagedResult<TItem> page = result.Value;

        if (page is null)
        {
            throw new InvalidOperationException(
                "A listing reported success but produced no page to return.");
        }

        return controller.Ok(PagedResponse<TItem>.From(page));
    }

    /// <summary>Builds the problem-details payload reported when a successful outcome carried no value.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <returns>A <c>404 Not Found</c> problem-details response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is <see langword="null"/>.</exception>
    public static ObjectResult NotFoundProblem(this ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        return controller.Problem(
            detail: ResourceNotFoundDetail,
            statusCode: StatusCodes.Status404NotFound,
            type: BuildProblemType(ResourceNotFoundCode));
    }

    /// <summary>
    /// Builds the problem-details payload reported when an authenticated caller is not permitted to perform
    /// an operation.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="code">
    /// The failure code identifying the refusal, so a client can branch on it without parsing prose.
    /// </param>
    /// <returns>A <c>403 Forbidden</c> problem-details response.</returns>
    /// <remarks>
    /// The detail is fixed and names neither the missing grant nor the resource. A refusal that explained
    /// itself would tell an unauthorised caller which identifiers exist and which privilege to acquire, and
    /// it would differ from the refusal the authorisation middleware produces for the same cause - which is
    /// precisely the drift that made the two paths distinguishable before.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ObjectResult ForbiddenProblem(this ControllerBase controller, string code)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return controller.Problem(
            detail: AuthorizationProblemDetails.ForbiddenDetail,
            statusCode: StatusCodes.Status403Forbidden,
            type: BuildProblemType(code));
    }

    /// <summary>Translates a paged outcome, projecting the domain page onto the wire envelope.</summary>
    /// <typeparam name="TRow">The row contract the page carries.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="PagedResponse{T}"/> on success; otherwise a problem-details
    /// payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// A successful outcome carrying no page is refused rather than answered with <c>404</c>, which is
    /// where this member deliberately differs from <see cref="Complete{TValue}(ControllerBase,
    /// Result{TValue})"/>.
    /// </remarks>
    public static ActionResult<PagedResponse<TRow>> CompletePage<TRow>(
        this ControllerBase controller,
        Result<PagedResult<TRow>> result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        PagedResult<TRow> page = result.Value
            ?? throw new InvalidOperationException(
                "A listing reported success but produced no page to return.");

        return controller.Ok(PagedResponse<TRow>.From(page));
    }
    /// <summary>Translates the outcome of a creation into a <c>201 Created</c> response.</summary>
    /// <typeparam name="TValue">The created representation's type.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <param name="identify">Extracts the new resource's identifier for the location header.</param>
    /// <returns>
    /// <c>201 Created</c> carrying the representation and a location header, or a problem-details payload
    /// with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// THE PATH BASE IS PART OF THAT ADDRESS AND OMITTING IT NAMES THE WRONG TENANT. A child portal is
    /// addressed by a path segment beneath a shared host name, and <see
    /// cref="Middleware.TenantPathBaseMiddleware"/> moves that segment out of the routable path and into
    /// the path base before routing runs - it has to, or the request matches no route at all.
    /// </remarks>
    public static ActionResult<ApiResponse<TValue>> Created<TValue>(
        this ControllerBase controller,
        Result<TValue> result,
        Func<TValue, object> identify)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(identify);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        TValue value = result.Value;

        if (value is null)
        {
            throw new InvalidOperationException(
                "A creation reported success but produced no representation to return.");
        }

        // The collection address is the PATH BASE plus the PATH, not the path alone.
        string collectionPath = (controller.Request.PathBase.ToUriComponent()
                + controller.Request.Path.ToUriComponent())
            .TrimEnd('/');

        string location = FormattableString.Invariant($"{collectionPath}/{identify(value)}");

        return controller.Created(location, ApiResponse<TValue>.Success(value));
    }

    /// <summary>
    /// Builds the problem-details payload for a failed outcome whose successful counterpart is not a JSON
    /// representation.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The failed outcome.</param>
    /// <returns>The problem-details response.</returns>
    /// <remarks>
    /// Almost every action reaches the failure path through <c>Complete</c>, which needs no separate entry
    /// point. The exception is an action whose success is not a JSON envelope at all - the module export,
    /// which returns an XML document - and which therefore cannot use <c>Complete</c> for either outcome.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    internal static ObjectResult Failed(this ControllerBase controller, Result result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return controller.Problem(result);
    }

    /// <summary>Builds the problem-details payload for a failed outcome.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The failed outcome.</param>
    /// <returns>The problem-details response.</returns>
    private static ObjectResult Problem(this ControllerBase controller, Result result)
    {
        ResultReason? error = result.Error;
        string code = error?.Code ?? "request.failed";
        string detail = SafeDetail(error?.Message);
        int status = MapStatusCode(code);
        string type = BuildProblemType(code);

        // A FAILURE THAT NAMES ITS FIELDS IS PUBLISHED WITH RFC 7807'S `errors` MEMBER, and one that does not
        // is published flat exactly as before. This is the ONE place in the solution where an expected failure
        // becomes a response, so extending it here is what gives every action the per-field shape without any
        // action having to opt in.
        //
        // ⚠ THE DISTINCTION IS `null` VERSUS EMPTY, NOT PRESENT VERSUS ABSENT. A reason that carries no field
        // attribution leaves the property `null`; an empty dictionary would publish an `errors` member with
        // nothing in it, which tells a client there are field errors and then names none.
        if (error?.FieldErrors is { Count: > 0 } fieldErrors)
        {
            // ⚠ ROUTED THROUGH MODEL STATE AND THE SHARED FACTORY, NOT BUILT BY HAND. The factory is what
            // attaches `traceId` and `correlationId` and resolves the problem-type link, so a payload
            // constructed here directly would be the one problem document in the solution missing them. Going
            // through a model-state dictionary reaches the factory's own override, which is the same path a
            // request-shape failure takes — so a client cannot tell the two apart, which is the point.
            var modelState = new ModelStateDictionary();

            foreach (KeyValuePair<string, IReadOnlyList<string>> entry in fieldErrors)
            {
                foreach (string message in entry.Value)
                {
                    // Each message passes the same publication guard as the summary detail, because these are
                    // published verbatim for the same reason and carry the same risk.
                    modelState.AddModelError(entry.Key, SafeDetail(message));
                }
            }

            ValidationProblemDetails validationProblem = controller.ProblemDetailsFactory
                .CreateValidationProblemDetails(
                    controller.HttpContext,
                    modelState,
                    statusCode: status,
                    title: ReasonPhrases.GetReasonPhrase(status),
                    type: type,
                    detail: detail);

            return new ObjectResult(validationProblem)
            {
                StatusCode = status,
                ContentTypes = { ProblemContentType },
            };
        }

        return controller.Problem(
            detail: detail,
            statusCode: status,
            type: type);
    }

    /// <summary>Publishes an expected failure's explanation, or a stand-in when it does not look authored.</summary>
    /// <param name="message">The message carried by the failed outcome, if any.</param>
    /// <returns>The message to publish; never <see langword="null"/> and never empty.</returns>
    /// <remarks>
    /// <b>This is defence in depth and not a substitute for authoring safe messages.</b> Every message a
    /// service places on a failed outcome is meant to be caller-safe by construction, because this method
    /// publishes it verbatim as the RFC 7807 <c>detail</c>.
    /// </remarks>
    private static string SafeDetail(string? message)
    {
        // Unreachable through the public contract, and kept anyway so this method is total.
        if (string.IsNullOrWhiteSpace(message))
        {
            return UnauthoredDetail;
        }

        bool looksAuthored = message.Length <= MaximumPublishedDetailLength
            && message.IndexOf('\n', StringComparison.Ordinal) < 0
            && message.IndexOf('\r', StringComparison.Ordinal) < 0
            && !NamesAnExceptionType(message);

        // ⚠ THE SHAPE TEST DECIDES WHETHER TO PUBLISH; THE REDACTION DECIDES WHAT. They are separate steps
        // because a message can be perfectly well authored and still name the tenant it resolved to - the
        // services write it that way on purpose, so the log says which tenant a support report concerns. The
        // stand-in text names nothing, so it needs no redaction.
        return looksAuthored ? PublishedDetail.Redact(message) : UnauthoredDetail;
    }

    /// <summary>Reports whether a message contains a word that names a CLR exception type.</summary>
    /// <param name="message">The message a failed outcome carried; never blank.</param>
    /// <returns>
    /// <see langword="true"/> when any whitespace-delimited word ends in <c>Exception</c>, ignoring
    /// trailing punctuation.
    /// </returns>
    /// <remarks>
    /// THE THIRD REFUSAL, ADDED BECAUSE THE FIRST TWO DID NOT CATCH THE CASE THAT ACTUALLY OCCURRED. A
    /// service caught a credential-store failure and put <c>exception.GetType().Name</c> into the message
    /// on its failed outcome, reasoning that the underlying cause should survive for a caller to report.
    /// </remarks>
    private static bool NamesAnExceptionType(string message)
    {
        const string suffix = "Exception";

        ReadOnlySpan<char> remaining = message.AsSpan();

        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> word = separator < 0 ? remaining : remaining[..separator];

            // Trailing punctuation is trimmed because the value that prompted this test ended in a full
            // stop, so comparing the raw word would have missed the instance it exists for.
            if (word.TrimEnd(".,;:)]}\"'").EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }

            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];
        }

        return false;
    }

    /// <summary>Chooses the status code for one failure code.</summary>
    /// <param name="code">The failure code, in any of the spellings the services use.</param>
    /// <returns>The HTTP status code declared for it, or <c>400</c> when the code is not declared.</returns>
    /// <remarks>
    /// <para>
    /// One normalisation and one lookup. A code that is absent, blank or unrecognised answers
    /// <c>400 Bad Request</c>, which is the status a malformed request already received and the safest
    /// reading of a failure nothing has classified: it tells a caller to change the request rather than to
    /// retry, to acquire a privilege, or to conclude that a resource is missing.
    /// </para>
    /// <para>
    /// <strong>Reaching that default in production would be a defect, not a fallback.</strong> Every code the
    /// solution can raise is in <see cref="StatusByCode"/> and a test enumerates the reason-code constants of
    /// all four assemblies to keep it that way, so the default exists for a code that does not yet exist.
    /// </para>
    /// </remarks>
    public static int MapStatusCode(string? code)
    {
        string normalised = Normalise(code);

        return StatusByCode.TryGetValue(normalised, out int status)
            ? status
            : StatusCodes.Status400BadRequest;
    }

    /// <summary>Reports whether a failure code has a declared status rather than the unclassified default.</summary>
    /// <param name="code">The failure code, in any of the spellings the services use.</param>
    /// <returns><see langword="true"/> when the code appears in <see cref="StatusByCode"/>.</returns>
    /// <remarks>
    /// Exists because <see cref="MapStatusCode"/> alone cannot answer the question a coverage test has to
    /// ask: <c>400</c> is both a declared classification and the answer for a code nobody classified, so a
    /// status comparison cannot tell a registered code from a missing one.
    /// </remarks>
    internal static bool IsClassified(string? code) => StatusByCode.ContainsKey(Normalise(code));

    /// <summary>Reduces a failure code to a stable comparison form.</summary>
    /// <param name="code">The failure code.</param>
    /// <returns>The lower-cased code with hyphens folded onto underscores.</returns>
    /// <remarks>
    /// Hyphens and underscores are folded together because the services disagree about which they use
    /// inside a reason token, and that disagreement is not worth a hundred duplicate table entries.
    /// Upper-cased codes are folded too, so a code spelled in capitals classifies the same as its
    /// lower-cased twin.
    /// </remarks>
    private static string Normalise(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToLowerInvariant().Replace('-', '_');
    }

    /// <summary>Builds the problem type URI-shaped identifier for a failure code.</summary>
    /// <param name="code">The failure code.</param>
    /// <returns>The problem type.</returns>
    /// <remarks>
    /// Visible to the rest of the API assembly so that a refusal decided outside a controller - by the
    /// authorisation middleware result handler - carries a problem type built by this same method, rather
    /// than a second spelling of the same convention.
    /// </remarks>
    internal static string BuildProblemType(string code)
    {
        return FormattableString.Invariant($"urn:dnnmigration:error:{Normalise(code)}");
    }
}
