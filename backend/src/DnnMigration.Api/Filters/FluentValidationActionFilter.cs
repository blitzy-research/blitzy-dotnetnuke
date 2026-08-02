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
/// Runs the declarative validator for each bound action argument and answers with an
/// RFC 7807 validation payload when any rule fails, before the action executes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file has to exist.</b> Registering validators with the service
/// container makes them resolvable; it does not make them run. Nothing in the
/// framework asks a validator about a bound argument on its own, and this solution
/// deliberately does not reference the retired package that used to hook validation
/// into model binding - that package is superseded, and its automatic mode was
/// withdrawn by its own authors. So the hook is written here, explicitly, where its
/// order and its failure shape are visible. Without it every rule in the Application
/// layer's validators would be dead code: present, registered, resolvable and never
/// consulted.
/// </para>
/// <para>
/// <b>The failure shape is the framework's own.</b> Failures are recorded against
/// model state and the response is built by the same problem-details factory the
/// framework uses for a binding failure, so a rule violation and a malformed field
/// produce the same envelope. Building a bespoke payload here would give the API two
/// error shapes for one class of problem, and a client would have to understand
/// both.
/// </para>
/// <para>
/// <b>Route values are published to the validators.</b> A rule that has to compare a
/// value in the body against a value in the route - the identifier in
/// <c>PUT /portals/{portalId}</c> against the identifier the body carries - cannot
/// reach the route from inside a validator, because a validator sees only the object
/// it is validating. This filter therefore copies every route value into the
/// validation context under a stable convention, described on the method that does
/// it. A validator whose rule depends on a route value fails closed when the value is
/// absent, so this copying is not a convenience: omitting it would refuse every such
/// request.
/// </para>
/// <para>
/// <b>Validators are resolved per request.</b> They are registered with a scoped
/// lifetime and may depend on other scoped services, so each one is taken from the
/// request's own service provider and none is cached in a field. This type holds no
/// validator and no request state; the only thing it keeps is the problem-details
/// factory, which is a singleton by design.
/// </para>
/// </remarks>
public sealed class FluentValidationActionFilter : IAsyncActionFilter
{
    /// <summary>
    /// Prefix under which route values are published into the validation context.
    /// </summary>
    /// <remarks>
    /// The convention is deliberately mechanical: the prefix, then the route
    /// parameter's name with its first character upper-cased. A route parameter named
    /// <c>portalId</c> is therefore published as <c>RoutePortalId</c>, which is
    /// exactly the key the portal update validator declares as a constant. Because
    /// the rule is mechanical, adding a route parameter makes it available to
    /// validators with no change to this file.
    /// </remarks>
    private const string RouteValueKeyPrefix = "Route";

    private readonly ProblemDetailsFactory _problemDetailsFactory;

    /// <summary>
    /// Creates the filter.
    /// </summary>
    /// <param name="problemDetailsFactory">
    /// Builds the validation payload. This is the application's own factory, so the
    /// payload matches the one produced for a binding failure.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="problemDetailsFactory"/> is <see langword="null"/>.
    /// </exception>
    public FluentValidationActionFilter(ProblemDetailsFactory problemDetailsFactory)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);

        _problemDetailsFactory = problemDetailsFactory;
    }

    /// <summary>
    /// Validates the bound arguments and either refuses the request or lets it
    /// proceed.
    /// </summary>
    /// <param name="context">The action about to execute, with its bound arguments.</param>
    /// <param name="next">The remainder of the pipeline.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="next"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Every argument is validated before any decision is taken, rather than stopping
    /// at the first argument that fails. A caller correcting a request should see
    /// every problem with it, not discover them one round trip at a time.
    /// </para>
    /// <para>
    /// Model state that is already invalid on entry - a body that could not be parsed,
    /// a route value that is not a number - is left exactly as it is and added to.
    /// The framework's own invalid-model-state filter runs before this one and
    /// short-circuits when it finds a problem, so reaching this code at all means
    /// binding succeeded; the merge is written to be correct regardless, because a
    /// suppressed invalid-model-state filter would change that assumption silently.
    /// </para>
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

    /// <summary>
    /// Validates every bound argument that has a validator.
    /// </summary>
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

    /// <summary>
    /// Finds the validator for one argument, if there is one.
    /// </summary>
    /// <param name="services">The request's service provider.</param>
    /// <param name="declaredType">The parameter's declared type.</param>
    /// <param name="argument">The bound value.</param>
    /// <returns>The validator, or <see langword="null"/> when none is registered.</returns>
    /// <remarks>
    /// The declared type is tried first because that is the contract the action
    /// published and therefore the type a validator is written against. The runtime
    /// type is tried only when the two differ and the declared type has no validator,
    /// which covers a parameter declared as a base type and bound to a derived one;
    /// without the fallback such an argument would pass unvalidated even though a
    /// validator for its actual type exists.
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

    /// <summary>
    /// Copies the request's route values into the validation context.
    /// </summary>
    /// <param name="validationContext">The context the validator will read.</param>
    /// <param name="routeData">The matched route's values.</param>
    /// <remarks>
    /// <para>
    /// A value that reads as a whole number is published as one, and everything else
    /// is published as its text. That distinction matters: a rule comparing a route
    /// identifier with a body identifier tests the published value's type, and treats
    /// the wrong type as "no route value supplied" so that it fails closed rather than
    /// silently skipping the comparison. Publishing an identifier as text would
    /// therefore refuse every such request.
    /// </para>
    /// <para>
    /// Parsing is culture-invariant. A route value is part of a URL, not a localised
    /// display value, so interpreting it under the server's current culture would make
    /// the meaning of a request depend on where the server is deployed.
    /// </para>
    /// <para>
    /// Every route value is published, including the framework's own controller and
    /// action entries. Filtering them out would need a list of names to exclude, which
    /// is one more thing to keep in step with the framework for no benefit: a
    /// validator reads only the keys it names.
    /// </para>
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

    /// <summary>
    /// Records a validator's failures against model state.
    /// </summary>
    /// <param name="result">The validation outcome.</param>
    /// <param name="modelState">Model state for the current request.</param>
    /// <remarks>
    /// The property name is used as the model-state key, which is what places each
    /// message beside the field it concerns in the response payload. A failure raised
    /// against the object as a whole carries an empty property name and is recorded
    /// against the empty key, which the payload renders as a request-level error.
    /// </remarks>
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

    /// <summary>
    /// Builds the refusal for a request whose model state is invalid.
    /// </summary>
    /// <param name="context">The action that will not be executed.</param>
    /// <returns>A 400 result carrying the validation payload.</returns>
    /// <remarks>
    /// The media type is stated explicitly so the response is served as a problem
    /// document rather than as ordinary JSON. A client distinguishing an error
    /// envelope from a successful payload by media type - which is the reason the
    /// problem media type exists - would otherwise see no difference.
    /// </remarks>
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
