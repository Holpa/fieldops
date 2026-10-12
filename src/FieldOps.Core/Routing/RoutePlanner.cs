namespace FieldOps.Core.Routing;

/// <summary>The planned order of a day's stops, plus how it compares with the order it was given.</summary>
/// <param name="Order">Indexes into the input stops, in visiting order.</param>
/// <param name="TotalKm">Length of the planned route, starting at the depot.</param>
/// <param name="OriginalKm">Length of the route if the stops were visited in the order given.</param>
public sealed record RoutePlan(IReadOnlyList<int> Order, double TotalKm, double OriginalKm)
{
    public double SavedKm => Math.Max(0, OriginalKm - TotalKm);
    public double SavedPercent => OriginalKm > 0 ? SavedKm / OriginalKm * 100 : 0;
}

/// <summary>
/// Orders a day's stops into a short route that starts at the depot.
/// Nearest-neighbour builds a sensible first route; 2-opt then removes crossings by reversing
/// segments while that shortens the path. Both start points (the nearest-neighbour route and the
/// order given) are improved and the shorter result wins, so a plan is never longer than the
/// order it started from. Straight-line distances are enough to rank routes well without a road
/// network or any map service.
/// </summary>
public static class RoutePlanner
{
    private const double Epsilon = 1e-9;

    public static RoutePlan Plan(GeoPoint depot, IReadOnlyList<GeoPoint> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        var given = Enumerable.Range(0, stops.Count).ToList();
        double originalKm = Distance.PathKm(depot, stops);
        if (stops.Count < 2) return new RoutePlan(given, originalKm, originalKm);

        var fromNearest = NearestNeighbour(depot, stops);
        TwoOpt(depot, stops, fromNearest);
        TwoOpt(depot, stops, given);

        double nearestKm = Length(depot, stops, fromNearest);
        double givenKm = Length(depot, stops, given);
        var best = nearestKm <= givenKm ? fromNearest : given;

        return new RoutePlan(best, Math.Min(nearestKm, givenKm), originalKm);
    }

    private static List<int> NearestNeighbour(GeoPoint depot, IReadOnlyList<GeoPoint> stops)
    {
        var remaining = new List<int>(Enumerable.Range(0, stops.Count));
        var order = new List<int>(stops.Count);
        var current = depot;

        while (remaining.Count > 0)
        {
            int bestAt = 0;
            double bestKm = double.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                double km = Distance.Km(current, stops[remaining[i]]);
                if (km < bestKm)
                {
                    bestKm = km;
                    bestAt = i;
                }
            }

            int next = remaining[bestAt];
            remaining.RemoveAt(bestAt);
            order.Add(next);
            current = stops[next];
        }

        return order;
    }

    /// <summary>
    /// Reverses segments order[i..j] while doing so shortens the path. On an open path that starts at
    /// the depot, a reversal only changes two edges: the one entering position i and the one leaving
    /// position j, so each candidate is checked in constant time.
    /// </summary>
    private static void TwoOpt(GeoPoint depot, IReadOnlyList<GeoPoint> stops, List<int> order)
    {
        bool improved = true;
        while (improved)
        {
            improved = false;
            for (int i = 0; i < order.Count - 1; i++)
            {
                var before = i == 0 ? depot : stops[order[i - 1]];
                for (int j = i + 1; j < order.Count; j++)
                {
                    var first = stops[order[i]];
                    var last = stops[order[j]];
                    bool hasAfter = j + 1 < order.Count;

                    double current = Distance.Km(before, first) + (hasAfter ? Distance.Km(last, stops[order[j + 1]]) : 0);
                    double reversed = Distance.Km(before, last) + (hasAfter ? Distance.Km(first, stops[order[j + 1]]) : 0);

                    if (reversed + Epsilon < current)
                    {
                        order.Reverse(i, j - i + 1);
                        improved = true;
                    }
                }
            }
        }
    }

    private static double Length(GeoPoint depot, IReadOnlyList<GeoPoint> stops, IEnumerable<int> order) =>
        Distance.PathKm(depot, order.Select(i => stops[i]));
}
