using FieldOps.Core.Analytics;

namespace FieldOps.Core.Tests;

public class TrendForecastTests
{
    [Fact]
    public void Refuses_with_too_little_history()
    {
        var result = TrendForecast.Evaluate([5, 6, 7, 8, 9, 10]);

        Assert.False(result.IsProjected);
        Assert.Null(result.NextValue);
        Assert.Contains("12 months", result.RefusalReason);
    }

    [Fact]
    public void Projects_a_clean_trend()
    {
        int[] values = [10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32];

        var result = TrendForecast.Evaluate(values);

        Assert.True(result.IsProjected);
        Assert.Equal(2, result.Slope, precision: 9);
        Assert.Equal(1, result.RSquared, precision: 9);
        Assert.Equal(34, result.NextValue!.Value, precision: 9);
    }

    [Fact]
    public void Refuses_when_the_line_explains_too_little()
    {
        // Pure alternation: no trend at all, so a straight line explains almost nothing.
        int[] values = [5, 20, 5, 20, 5, 20, 5, 20, 5, 20, 5, 20];

        var result = TrendForecast.Evaluate(values);

        Assert.False(result.IsProjected);
        Assert.True(result.RSquared < TrendForecast.MinimumRSquared);
        Assert.NotNull(result.RefusalReason);
    }

    [Fact]
    public void A_flat_series_projects_the_same_value()
    {
        var result = TrendForecast.Evaluate(Enumerable.Repeat(7, 12).ToList());

        Assert.True(result.IsProjected);
        Assert.Equal(7, result.NextValue!.Value, precision: 9);
    }

    [Fact]
    public void A_falling_trend_never_projects_below_zero()
    {
        int[] values = [22, 20, 18, 16, 14, 12, 10, 8, 6, 4, 2, 0];

        var result = TrendForecast.Evaluate(values);

        Assert.True(result.IsProjected);
        Assert.Equal(0, result.NextValue!.Value);
    }
}
