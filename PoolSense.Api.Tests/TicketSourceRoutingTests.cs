using PoolSense.Api.Models;
using PoolSense.Api.Services;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class TicketSourceRoutingTests
{
    [Theory]
    [InlineData("search NYRA for publication", false, true, false)]
    [InlineData("ONLY USE WIKI for the publication flow", false, true, false)]
    [InlineData("look in the repo for Publish", false, false, true)]
    [InlineData("find in source code", false, false, true)]
    [InlineData("search code and wiki for Publish", false, true, true)]
    [InlineData("search wiki and repository", false, true, true)]
    [InlineData("search historical tickets for publication", true, true, true)]
    [InlineData("only use pool", true, true, true)]
    [InlineData("search issues", true, true, true)]
    [InlineData("do not search code; search wiki", false, true, false)]
    [InlineData("search repo, not wiki", false, false, true)]
    [InlineData("now search only wiki", false, true, false)]
    [InlineData("what is publication? search repo", false, false, true)]
    public void ExplicitInstructions_HaveExactPrecedence(string query, bool pool, bool wiki, bool code)
    {
        var plan = TicketSourceRouting.Explicit(query, query);
        Assert.NotNull(plan);
        Assert.Null(plan.ClarificationMessage);
        Assert.Equal(pool, plan.Pool);
        Assert.Equal(wiki, plan.Wiki);
        Assert.Equal(code, plan.Code);
        Assert.Equal("explicit", plan.Origin);
    }

    [Theory]
    [InlineData("why does the UI error show code 500")]
    [InlineData("what does this wiki integration do")]
    [InlineData("explain repository architecture")]
    [InlineData("why do we use code generation")]
    [InlineData("check code 500")]
    [InlineData("onemps publication flow")]
    public void SourceMentions_AreNotInstructions(string query) => Assert.Null(TicketSourceRouting.Explicit(query, query));

    [Fact]
    public void PoolAndExcludedSourceConflict_IsClarifiedBeforeAnySource()
    {
        var plan = TicketSourceRouting.Explicit("search pool but not wiki", "");
        Assert.NotNull(plan!.ClarificationMessage);
        Assert.False(plan.Pool || plan.Wiki || plan.Code);
        var info = TicketSourceRouting.Classified("Info", "do not search code", "");
        Assert.False(info.Pool || info.Code);
        Assert.True(info.Wiki);
        var issue = TicketSourceRouting.Classified("Issue", "Current failure", "");
        Assert.True(issue.Pool && issue.Wiki && issue.Code);
        Assert.Equal(ResolutionAnswerIntent.Incident, issue.Intent);
    }
}
