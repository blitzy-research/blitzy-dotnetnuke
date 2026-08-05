using DnnMigration.Application.Dtos.Portal;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates the body of <c>PUT /api/v1/portals/{portalId}/settings</c>.
/// </summary>
/// <remarks>
/// The route is the request's only portal identifier, so this validator applies only the common Site
/// Settings field rules declared by <see cref="PortalSettingsUpdateRequestValidator{TRequest}"/>.
/// </remarks>
public sealed class UpdatePortalSettingsRequestValidator
    : PortalSettingsUpdateRequestValidator<UpdatePortalSettingsRequest>
{
    /// <summary>Initialises the validator.</summary>
    public UpdatePortalSettingsRequestValidator()
    {
    }
}