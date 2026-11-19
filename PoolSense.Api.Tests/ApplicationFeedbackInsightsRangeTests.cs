using Microsoft.Data.SqlClient;
using PoolSense.Api.Data;
using System.Data;
using Xunit;

namespace PoolSense.Api.Tests;

public sealed class ApplicationFeedbackInsightsRangeTests
{
    [Fact]
    public void AllTime_UsesNoLowerBoundAndNullableDateTime2Parameters()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var range = ApplicationFeedbackInsightsRange.Create(0, now);
        using var command = new SqlCommand();

        range.AddSqlParameters(command);

        Assert.Equal(0, range.RangeDays);
        Assert.Null(range.RangeStart);
        Assert.Null(range.PreviousRangeStart);
        Assert.Equal(now.Date.AddDays(-89), range.ChartStart);
        Assert.Equal(SqlDbType.DateTime2, command.Parameters["@rangeStart"].SqlDbType);
        Assert.Equal(SqlDbType.DateTime2, command.Parameters["@previousRangeStart"].SqlDbType);
        Assert.Same(DBNull.Value, command.Parameters["@rangeStart"].Value);
        Assert.Same(DBNull.Value, command.Parameters["@previousRangeStart"].Value);
    }

    [Fact]
    public void RecentRange_PreservesCurrentAndPreviousCutoffs()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var range = ApplicationFeedbackInsightsRange.Create(7, now);
        using var command = new SqlCommand();

        range.AddSqlParameters(command);

        Assert.Equal(7, range.RangeDays);
        Assert.Equal(now.AddDays(-7), range.RangeStart);
        Assert.Equal(now.AddDays(-14), range.PreviousRangeStart);
        Assert.Equal(now.Date.AddDays(-6), range.ChartStart);
        Assert.Equal(now.AddDays(-7), command.Parameters["@rangeStart"].Value);
        Assert.Equal(now.AddDays(-14), command.Parameters["@previousRangeStart"].Value);
    }
}
