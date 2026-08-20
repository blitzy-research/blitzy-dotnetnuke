using DnnMigration.Application.Dtos.Portal;

namespace DnnMigration.Application.Validation;

/// <summary>Validates the body of <c>PUT /api/v1/portals/{portalId}/settings</c>.</summary>
public sealed class UpdatePortalSettingsRequestValidator
    : PortalSettingsUpdateRequestValidator<UpdatePortalSettingsRequest>
{
    /// <summary>Initialises the validator.</summary>
    public UpdatePortalSettingsRequestValidator()
    {
    }
}
