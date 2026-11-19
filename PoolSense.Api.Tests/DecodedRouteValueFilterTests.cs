using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using PoolSense.Api.Filters;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class DecodedRouteValueFilterTests
{
    private static ActionExecutingContext CreateContext(
        RouteValueDictionary routeValues,
        IReadOnlyList<ParameterDescriptor> parameters,
        IDictionary<string, object?> actionArguments)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues = routeValues;
        var actionDescriptor = new ActionDescriptor { Parameters = [.. parameters] };
        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), actionDescriptor),
            new List<IFilterMetadata>(),
            actionArguments,
            new object());
    }

    [Fact]
    public void OnActionExecuting_DecodesEscapedSlashInRouteBoundParameter()
    {
        var arguments = new Dictionary<string, object?> { ["projectId"] = "MOR%2FFCS%2FSCS" };
        var context = CreateContext(
            new RouteValueDictionary { ["projectId"] = "MOR%2FFCS%2FSCS" },
            [new ParameterDescriptor { Name = "projectId", ParameterType = typeof(string) }],
            arguments);

        new DecodedRouteValueFilter().OnActionExecuting(context);

        Assert.Equal("MOR/FCS/SCS", context.ActionArguments["projectId"]);
    }

    [Fact]
    public void OnActionExecuting_LeavesValuesWithoutEscapedSlashUntouched()
    {
        var arguments = new Dictionary<string, object?> { ["projectId"] = "MOR FCS SCS" };
        var context = CreateContext(
            new RouteValueDictionary { ["projectId"] = "MOR FCS SCS" },
            [new ParameterDescriptor { Name = "projectId", ParameterType = typeof(string) }],
            arguments);

        new DecodedRouteValueFilter().OnActionExecuting(context);

        Assert.Equal("MOR FCS SCS", context.ActionArguments["projectId"]);
    }

    [Fact]
    public void OnActionExecuting_LeavesArgumentsWithoutMatchingRouteValueUntouched()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["projectId"] = "MOR%2FFCS%2FSCS",
            ["payload"] = "MOR%2FFCS%2FSCS"
        };
        var context = CreateContext(
            new RouteValueDictionary { ["projectId"] = "MOR%2FFCS%2FSCS" },
            [new ParameterDescriptor { Name = "payload", ParameterType = typeof(string) }],
            arguments);

        new DecodedRouteValueFilter().OnActionExecuting(context);

        Assert.Equal("MOR%2FFCS%2FSCS", context.ActionArguments["payload"]);
    }

    [Fact]
    public void OnActionExecuting_DecodesLowercaseEscapedSlash()
    {
        var arguments = new Dictionary<string, object?> { ["projectId"] = "MOR%2fFCS%2fSCS" };
        var context = CreateContext(
            new RouteValueDictionary { ["projectId"] = "MOR%2fFCS%2fSCS" },
            [new ParameterDescriptor { Name = "projectId", ParameterType = typeof(string) }],
            arguments);

        new DecodedRouteValueFilter().OnActionExecuting(context);

        Assert.Equal("MOR/FCS/SCS", context.ActionArguments["projectId"]);
    }
}
