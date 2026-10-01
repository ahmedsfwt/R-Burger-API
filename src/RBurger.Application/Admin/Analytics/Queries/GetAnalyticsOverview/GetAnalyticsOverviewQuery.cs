using MediatR;
using RBurger.Application.Admin.Analytics.DTOs;
using RBurger.Application.Common.Interfaces;

namespace RBurger.Application.Admin.Analytics.Queries.GetAnalyticsOverview;

// §7.6.6 GET /api/v1/admin/analytics/overview?range=today|7d|30d.
//
// Day 15 (Backend Parity Spec §11): "if one field truly remains impossible to calculate...
// isolate ONLY that field rather than returning 501 for the entire analytics endpoint." Applied
// here - totalRevenue/totalOrders/avgOrderValue/deltas.revenue/deltas.orders/
// deltas.avgOrderValue are all fully computable (Backend Parity Spec §2.1/§2.2/§2.3 resolved
// the revenue basis, comparison period, and cancelled-order exclusion). ONLY
// completionRatePercent (and its delta) has no formula anywhere in Documentation v1.2 and is
// returned as null rather than fabricated - the field is still present in the JSON shape
// (nothing removed from the documented contract), its value is just honestly "unknown".
//
// Comparison period (§2.2): "Default comparison period: WEEKLY... compared against the
// immediately previous equivalent weekly period" - applied literally: every delta compares the
// trailing 7 days to the 7 days before that, independent of the requested `range` (which only
// scopes the headline totalRevenue/totalOrders/avgOrderValue window, exactly as its
// today/7d/30d query values already imply).
public record GetAnalyticsOverviewQuery(string Range) : IRequest<AnalyticsOverviewDto>, ICacheableQuery
{
    // Day 15 addition (Backend Parity Spec §12) - §13.1's 60s cache, scoped per "range" value.
    string ICacheableQuery.CacheKey => $"analytics:overview:{Range}";
    int ICacheableQuery.CacheDurationSeconds => 60;
}

public class GetAnalyticsOverviewQueryHandler : IRequestHandler<GetAnalyticsOverviewQuery, AnalyticsOverviewDto>
{
    private readonly IOrderRepository _orderRepository;

    public GetAnalyticsOverviewQueryHandler(IOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<AnalyticsOverviewDto> Handle(
        GetAnalyticsOverviewQuery request, CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);

        var (from, to) = request.Range switch
        {
            "today" => (today, tomorrow),
            "30d" => (today.AddDays(-29), tomorrow),
            _ => (today.AddDays(-6), tomorrow) // "7d" and any unrecognized value default here
        };

        var revenue = await _orderRepository.GetCapturedRevenueAsync(from, to, cancellationToken);
        var orders = await _orderRepository.GetOrderCountAsync(from, to, cancellationToken);
        var avgOrderValue = orders == 0 ? 0m : revenue / orders;
        var deliveredOrders = await _orderRepository.GetDeliveredOrderCountAsync(from, to, cancellationToken);
        var completionRate = orders == 0 ? (decimal?)null : Math.Round((decimal)deliveredOrders / orders * 100m, 2);

        // §2.2: fixed weekly comparison, independent of `range` - this week vs the week before.
        var currentWeekStart = today.AddDays(-6);
        var previousWeekStart = currentWeekStart.AddDays(-7);

        var currentWeekRevenue = await _orderRepository.GetCapturedRevenueAsync(currentWeekStart, tomorrow, cancellationToken);
        var previousWeekRevenue = await _orderRepository.GetCapturedRevenueAsync(previousWeekStart, currentWeekStart, cancellationToken);
        var currentWeekOrders = await _orderRepository.GetOrderCountAsync(currentWeekStart, tomorrow, cancellationToken);
        var previousWeekOrders = await _orderRepository.GetOrderCountAsync(previousWeekStart, currentWeekStart, cancellationToken);
        var currentWeekAov = currentWeekOrders == 0 ? 0m : currentWeekRevenue / currentWeekOrders;
        var previousWeekAov = previousWeekOrders == 0 ? 0m : previousWeekRevenue / previousWeekOrders;

        var currentWeekDelivered = await _orderRepository.GetDeliveredOrderCountAsync(currentWeekStart, tomorrow, cancellationToken);
        var previousWeekDelivered = await _orderRepository.GetDeliveredOrderCountAsync(previousWeekStart, currentWeekStart, cancellationToken);
        var currentWeekCompletionRate = currentWeekOrders == 0 ? 0m : (decimal)currentWeekDelivered / currentWeekOrders * 100m;
        var previousWeekCompletionRate = previousWeekOrders == 0 ? 0m : (decimal)previousWeekDelivered / previousWeekOrders * 100m;

        return new AnalyticsOverviewDto
        {
            TotalRevenue = revenue,
            TotalOrders = orders,
            AvgOrderValue = avgOrderValue,
            CompletionRatePercent = completionRate,
            Deltas = new AnalyticsOverviewDeltasDto
            {
                Revenue = PercentDelta(currentWeekRevenue, previousWeekRevenue),
                Orders = PercentDelta(currentWeekOrders, previousWeekOrders),
                AvgOrderValue = PercentDelta(currentWeekAov, previousWeekAov),
                CompletionRate = previousWeekCompletionRate == 0 && currentWeekCompletionRate == 0
                    ? 0m
                    : PercentDelta(currentWeekCompletionRate, previousWeekCompletionRate)
            }
        };
    }

    // Week-over-week percentage change. previous == 0 is treated as a 100% increase when
    // current > 0 (a conventional way to express "growth from a zero baseline" without
    // dividing by zero or fabricating an arbitrary large number), and 0% when both are zero.
    private static decimal PercentDelta(decimal current, decimal previous)
    {
        if (previous == 0)
        {
            return current == 0 ? 0m : 100m;
        }

        return Math.Round((current - previous) / previous * 100m, 2);
    }

    private static decimal PercentDelta(int current, int previous) => PercentDelta((decimal)current, previous);
}
