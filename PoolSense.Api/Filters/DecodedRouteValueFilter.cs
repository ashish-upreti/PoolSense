using Microsoft.AspNetCore.Mvc.Filters;

namespace PoolSense.Api.Filters;

/// <summary>
/// Decodes escaped slashes (%2F) in route-bound string action parameters.
/// ASP.NET Core routing deliberately does not decode %2F in route values (every other
/// percent-escape is already decoded by model binding), so ids containing a literal
/// slash such as "MOR/FCS/SCS" arrive in the action as "MOR%2FFCS%2FSCS" and fail
/// repository lookups. Only parameters whose bound value equals the matching route
/// value are rewritten, and only the %2F escape is replaced; no full URL unescaping
/// is performed to avoid double-decoding literal percent sequences.
/// </summary>
public sealed class DecodedRouteValueFilter : IActionFilter
{
    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var routeValues = context.HttpContext.Request.RouteValues;
        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            if (parameter.ParameterType != typeof(string))
            {
                continue;
            }

            if (!context.ActionArguments.TryGetValue(parameter.Name, out var bound)
                || bound is not string boundValue
                || !boundValue.Contains("%2F", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (routeValues.TryGetValue(parameter.Name, out var routeValue)
                && routeValue is string routeValueString
                && string.Equals(routeValueString, boundValue, StringComparison.Ordinal))
            {
                context.ActionArguments[parameter.Name] = boundValue.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
