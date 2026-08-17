using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="ReplaceModulePermissionsRequest"/>, the payload submitted to
/// <c>PUT /api/v1/modules/{moduleId}/permissions</c> to replace a module's grants.
/// </summary>
/// <remarks>
/// <para>
/// The rules here are about SHAPE only. Whether a role exists, whether an account belongs to the portal and
/// whether a permission is declared by the addressed module are all tenant-scoped questions that need the
/// store to answer, so they belong to the service and are refused there with codes a client can act on.
/// Duplicating them here as guesses would produce two different refusals for one mistake.
/// </para>
/// <para>
/// No rule is placed on <see cref="ReplaceModulePermissionsRequest.InheritViewPermissions"/>. Both values
/// are legitimate, and the consequence of setting it - that explicit view grants are withdrawn - is
/// behaviour rather than invalidity.
/// </para>
/// </remarks>
public sealed class ReplaceModulePermissionsRequestValidator
    : AbstractValidator<ReplaceModulePermissionsRequest>
{
    /// <summary>
    /// Largest number of grants one submission may carry. Must agree with the service's own copy of this
    /// bound.
    /// </summary>
    private const int GrantsMaximum = 1000;

    private const string GrantsMissingMessage =
        "The grant list is required. Send an empty array to withdraw every grant.";

    private static readonly string GrantsTooManyMessage = FormattableString.Invariant(
        $"A module may carry no more than {GrantsMaximum} grants.");

    private const string PrincipalAmbiguousMessage =
        "Each grant must name exactly one of a role or an account.";

    private const string PermissionMissingMessage =
        "Each grant must name the permission it applies to.";

    /// <summary>
    /// Initialises a new instance of the <see cref="ReplaceModulePermissionsRequestValidator"/> class and
    /// declares its rules.
    /// </summary>
    public ReplaceModulePermissionsRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.Grants)
            .NotNull()
            .WithMessage(GrantsMissingMessage)
            .Must(grants => grants.Count <= GrantsMaximum)
            .WithMessage(GrantsTooManyMessage);

        RuleForEach(request => request.Grants)
            .ChildRules(grant =>
            {
                // The catalogue column is IDENTITY (1, 1), so zero and every negative value name no
                // definition at all and can be refused without reading the store.
                grant.RuleFor(entry => entry.PermissionId)
                    .GreaterThan(0)
                    .WithMessage(PermissionMissingMessage);

                grant.RuleFor(entry => entry)
                    .Must(entry => entry.RoleId is null != entry.UserId is null)
                    .WithName(nameof(ModulePermissionGrantRequest.RoleId))
                    .WithMessage(PrincipalAmbiguousMessage);
            })
            .When(request => request.Grants is not null);
    }
}
