using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace DnnMigration.Api.Filters;

/// <summary>
/// Runs the declarative validator for each bound action argument and answers with an RFC 7807 validation
/// payload when any rule fails, before the action executes.
/// </summary>
public sealed class FluentValidationActionFilter : IAsyncActionFilter
{
    /// <summary>Prefix under which route values are published into the validation context.</summary>
    /// <remarks>
    /// The convention is deliberately mechanical: the prefix, then the route parameter's name with its
    /// first character upper-cased. A route parameter named <c>portalId</c> is therefore published as
    /// <c>RoutePortalId</c>, which is exactly the key the portal update validator declares as a constant.
    /// </remarks>
    private const string RouteValueKeyPrefix = "Route";

    private readonly ProblemDetailsFactory _problemDetailsFactory;

    /// <summary>Creates the filter.</summary>
    /// <param name="problemDetailsFactory">Builds the validation payload.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="problemDetailsFactory"/> is <see langword="null"/>.
    /// </exception>
    public FluentValidationActionFilter(ProblemDetailsFactory problemDetailsFactory)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);

        _problemDetailsFactory = problemDetailsFactory;
    }

    /// <summary>Validates the bound arguments and either refuses the request or lets it proceed.</summary>
    /// <param name="context">The action about to execute, with its bound arguments.</param>
    /// <param name="next">The remainder of the pipeline.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Every argument is validated before any decision is taken, rather than stopping at the first argument
    /// that fails. A caller correcting a request should see every problem with it, not discover them one
    /// round trip at a time.
    /// </remarks>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await ValidateArgumentsAsync(context).ConfigureAwait(false);

        if (!context.ModelState.IsValid)
        {
            context.Result = BuildValidationFailure(context);
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>Validates every bound argument that has a validator.</summary>
    /// <param name="context">The action about to execute.</param>
    /// <returns>A task that completes when every argument has been considered.</returns>
    private async Task ValidateArgumentsAsync(ActionExecutingContext context)
    {
        IServiceProvider services = context.HttpContext.RequestServices;

        foreach (ParameterDescriptor parameter in context.ActionDescriptor.Parameters)
        {
            if (!context.ActionArguments.TryGetValue(parameter.Name, out object? argument)
                || argument is null)
            {
                continue;
            }

            IValidator? validator = ResolveValidator(services, parameter.ParameterType, argument);

            if (validator is null)
            {
                continue;
            }

            ValidationContext<object> validationContext = new(argument);
            PublishRouteValues(validationContext, context.RouteData);

            FluentValidation.Results.ValidationResult result = await validator
                .ValidateAsync(validationContext, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            RecordFailures(result, context.ModelState);
        }
    }

    /// <summary>Finds the validator for one argument, if there is one.</summary>
    /// <param name="services">The request's service provider.</param>
    /// <param name="declaredType">The parameter's declared type.</param>
    /// <param name="argument">The bound value.</param>
    /// <returns>The validator, or <see langword="null"/> when none is registered.</returns>
    /// <remarks>
    /// The declared type is tried first because that is the contract the action published and therefore the
    /// type a validator is written against.
    /// </remarks>
    private static IValidator? ResolveValidator(
        IServiceProvider services,
        Type declaredType,
        object argument)
    {
        if (services.GetService(typeof(IValidator<>).MakeGenericType(declaredType)) is IValidator declared)
        {
            return declared;
        }

        Type runtimeType = argument.GetType();

        if (runtimeType == declaredType)
        {
            return null;
        }

        return services.GetService(typeof(IValidator<>).MakeGenericType(runtimeType)) as IValidator;
    }

    /// <summary>Copies the request's route values into the validation context.</summary>
    /// <param name="validationContext">The context the validator will read.</param>
    /// <param name="routeData">The matched route's values.</param>
    /// <remarks>
    /// Parsing is culture-invariant. A route value is part of a URL, not a localised display value, so
    /// interpreting it under the server's current culture would make the meaning of a request depend on
    /// where the server is deployed.
    /// </remarks>
    private static void PublishRouteValues(
        ValidationContext<object> validationContext,
        RouteData routeData)
    {
        foreach (KeyValuePair<string, object?> routeValue in routeData.Values)
        {
            if (routeValue.Value is null || string.IsNullOrEmpty(routeValue.Key))
            {
                continue;
            }

            string key = string.Concat(
                RouteValueKeyPrefix,
                char.ToUpperInvariant(routeValue.Key[0]).ToString(CultureInfo.InvariantCulture),
                routeValue.Key.AsSpan(1));

            string text = routeValue.Value.ToString() ?? string.Empty;

            validationContext.RootContextData[key] =
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    ? number
                    : text;
        }
    }

    /// <summary>Records a validator's failures against model state.</summary>
    /// <param name="result">The validation outcome.</param>
    /// <param name="modelState">Model state for the current request.</param>
    private static void RecordFailures(
        FluentValidation.Results.ValidationResult result,
        ModelStateDictionary modelState)
    {
        if (result.IsValid)
        {
            return;
        }

        foreach (FluentValidation.Results.ValidationFailure failure in result.Errors)
        {
            modelState.TryAddModelError(failure.PropertyName ?? string.Empty, failure.ErrorMessage);
        }
    }

    /// <summary>Builds the refusal for a request whose model state is invalid.</summary>
    /// <param name="context">The action that will not be executed.</param>
    /// <returns>A 400 result carrying the validation payload.</returns>
    private ObjectResult BuildValidationFailure(ActionExecutingContext context)
    {
        ValidationProblemDetails problemDetails = _problemDetailsFactory
            .CreateValidationProblemDetails(context.HttpContext, context.ModelState);

        return new ObjectResult(problemDetails)
        {
            StatusCode = problemDetails.Status ?? StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }
}
