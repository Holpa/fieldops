using FieldOps.Core.Routing;

namespace FieldOps.Core.Tests;

public class RoutePlannerTests
{
    private static readonly GeoPoint Depot = new(40.00, -83.00);

    [Fact]
    public void Empty_day_has_an_empty_plan()
    {
        var plan = RoutePlanner.Plan(Depot, []);

        Assert.Empty(plan.Order);
        Assert.Equal(0, plan.TotalKm);
    }

    [Fact]
    public void Single_stop_is_visited_directly()
    {
        var stop = new GeoPoint(40.05, -83.05);

        var plan = RoutePlanner.Plan(Depot, [stop]);

        Assert.Equal(new[] { 0 }, plan.Order);
        Assert.Equal(Distance.Km(Depot, stop), plan.TotalKm, precision: 9);
    }

    [Fact]
    public void Every_stop_is_visited_exactly_once()
    {
        var stops = RandomStops(seed: 7, count: 25);

        var plan = RoutePlanner.Plan(Depot, stops);

        Assert.Equal(Enumerable.Range(0, stops.Count), plan.Order.OrderBy(i => i));
    }

    [Fact]
    public void A_zigzag_booking_order_is_straightened_out()
    {
        // Four stops on a line east of the depot, booked far-near-far-near.
        GeoPoint[] stops = [new(40.00, -82.60), new(40.00, -82.90), new(40.00, -82.70), new(40.00, -82.80)];

        var plan = RoutePlanner.Plan(Depot, stops);

        Assert.Equal(new[] { 1, 3, 2, 0 }, plan.Order);
        Assert.True(plan.SavedKm > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_plan_is_never_longer_than_the_order_it_was_given(int seed)
    {
        var stops = RandomStops(seed, count: 12);

        var plan = RoutePlanner.Plan(Depot, stops);

        Assert.True(plan.TotalKm <= plan.OriginalKm + 1e-9);
        Assert.Equal(Distance.PathKm(Depot, plan.Order.Select(i => stops[i])), plan.TotalKm, precision: 9);
    }

    [Fact]
    public void Haversine_matches_a_known_distance()
    {
        // One degree of latitude is about 111.2 km anywhere on the globe.
        var km = Distance.Km(new GeoPoint(40, -83), new GeoPoint(41, -83));

        Assert.InRange(km, 111.0, 111.4);
    }

    private static List<GeoPoint> RandomStops(int seed, int count)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => new GeoPoint(40 + random.NextDouble() * 0.3, -83 + random.NextDouble() * 0.3))
            .ToList();
    }
}
