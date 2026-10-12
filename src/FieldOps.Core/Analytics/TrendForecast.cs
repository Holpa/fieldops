namespace FieldOps.Core.Analytics;

/// <summary>The outcome of a forecast: either a projection or the reason one was not made.</summary>
public sealed record ForecastResult(
    bool IsProjected,
    double? NextValue,
    double Slope,
    double RSquared,
    double Rmse,
    int Points,
    string? RefusalReason)
{
    public static ForecastResult Refused(string reason, int points, double slope = 0, double rSquared = 0, double rmse = 0) =>
        new(false, null, slope, rSquared, rmse, points, reason);
}

/// <summary>
/// Fits a straight line through a series of monthly counts and projects the next month - but only
/// when the line is worth believing. Too few months, or a fit that explains too little of the
/// variation, returns a refusal that says why, instead of a confident-looking wrong number.
/// </summary>
public static class TrendForecast
{
    public const int MinimumPoints = 12;
    public const double MinimumRSquared = 0.2;

    public static ForecastResult Evaluate(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        int n = values.Count;
        if (n < MinimumPoints)
            return ForecastResult.Refused($"Needs at least {MinimumPoints} months of history; only {n} available.", n);

        // Ordinary least squares with x = 0 .. n-1.
        double meanX = (n - 1) / 2.0;
        double meanY = values.Average();
        double sxx = 0, sxy = 0;
        for (int x = 0; x < n; x++)
        {
            sxx += (x - meanX) * (x - meanX);
            sxy += (x - meanX) * (values[x] - meanY);
        }

        double slope = sxy / sxx;
        double intercept = meanY - slope * meanX;

        double ssRes = 0, ssTot = 0;
        for (int x = 0; x < n; x++)
        {
            double predicted = intercept + slope * x;
            ssRes += (values[x] - predicted) * (values[x] - predicted);
            ssTot += (values[x] - meanY) * (values[x] - meanY);
        }

        // A perfectly flat series is fitted exactly by a flat line.
        double rSquared = ssTot == 0 ? 1 : 1 - ssRes / ssTot;
        double rmse = Math.Sqrt(ssRes / n);

        if (rSquared < MinimumRSquared)
            return ForecastResult.Refused(
                $"The trend line explains only {rSquared:P0} of the month-to-month variation ({MinimumRSquared:P0} required).",
                n, slope, rSquared, rmse);

        double next = Math.Max(0, intercept + slope * n);
        return new ForecastResult(true, next, slope, rSquared, rmse, n, null);
    }
}
