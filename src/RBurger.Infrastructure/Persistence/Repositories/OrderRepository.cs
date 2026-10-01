using Microsoft.EntityFrameworkCore;
using RBurger.Application.Common.Interfaces;
using RBurger.Domain.Entities;
using RBurger.Domain.Enums;

namespace RBurger.Infrastructure.Persistence.Repositories;

public class OrderRepository : IOrderRepository
{
    // Approved decision #7: bounded retry count for the MAX(OrderNumber)+1 strategy. This is
    // a best-effort concurrency guard (not a lock), relying on the DB's unique index
    // (OrderConfiguration) as the actual safety net against a duplicate OrderNumber - exactly
    // as approved, in place of an IDENTITY column or a new migration.
    private const int MaxOrderNumberAttempts = 5;

    private readonly ApplicationDbContext _context;

    public OrderRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task AddWithGeneratedOrderNumberAsync(Order order, CancellationToken cancellationToken)
    {
        _context.Orders.Add(order);

        for (var attempt = 1; attempt <= MaxOrderNumberAttempts; attempt++)
        {
            // Queries the database directly - EF Core does not include the locally tracked,
            // not-yet-saved "order" entity in this result set, so no self-exclusion is needed.
            var maxOrderNumber = await _context.Orders
                .MaxAsync(o => (int?)o.OrderNumber, cancellationToken) ?? 0;

            order.OrderNumber = maxOrderNumber + 1;

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxOrderNumberAttempts)
            {
                // Unique index on Orders.OrderNumber (OrderConfiguration) rejected a
                // concurrent duplicate - recompute MAX and retry, per approved decision #7.
            }
        }

        // Exhausted retries under sustained contention - surface the underlying EF failure
        // on the final attempt instead of silently swallowing it.
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<Order?> GetByIdWithDetailsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return _context.Orders
            .Include(o => o.Branch)
            .Include(o => o.OrderItems)
            .Include(o => o.Payment)
            // Day 8 approved decision #2: GET /api/v1/orders/{orderId}'s documented "review"
            // field must reflect an actual review when one exists (§6.1: Order (1)--(0..1)
            // Review) instead of the previous hard-coded null - this Include is the only
            // change needed since DeliverOrderCommandHandler already reuses this same method
            // and does not touch Review, so no other caller's shape is affected.
            .Include(o => o.Review)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    public async Task<(IReadOnlyList<Order> Orders, int TotalCount)> GetCustomerOrdersPagedAsync(
        Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var baseQuery = _context.Orders.Where(o => o.CustomerId == customerId);

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        var orders = await baseQuery
            .Include(o => o.Branch)
            .Include(o => o.Review)
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (orders, totalCount);
    }

    // Day 5 addition: no Include() calls - customer-received/review only read/write scalar
    // columns on Order itself (CustomerId, Stage, CustomerReceivedAt), so no navigation
    // properties are needed here, unlike GetByIdWithDetailsAsync above.
    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }

    // ---- Day 7 additions (§7.5 Orders — Driver) ----

    // §7.5: "orders at Stage=0/1 ready for pickup at the driver's own branch". Scoped to
    // unclaimed orders (DriverId == null) - see IOrderRepository's XML comment.
    public Task<List<Order>> GetNewOrdersForBranchAsync(int branchId, CancellationToken cancellationToken)
    {
        return _context.Orders
            .Where(o => o.BranchId == branchId
                && o.DriverId == null
                && (o.Stage == OrderStage.Confirmed || o.Stage == OrderStage.Preparing))
            .ToListAsync(cancellationToken);
    }

    // §7.5: "orders currently assigned to this driver" (Active tab). DriverId can only be set
    // once Stage is already >= 1 (per TryClaimForDriverAsync below), so Stage=0 with a
    // DriverId cannot occur - only Preparing/OnTheWay are queried.
    public Task<List<Order>> GetActiveOrdersForDriverAsync(Guid driverId, CancellationToken cancellationToken)
    {
        return _context.Orders
        .Include(o => o.Customer)
        .Where(o => o.DriverId == driverId
              && (o.Stage == OrderStage.Preparing
              || o.Stage == OrderStage.OnTheWay
              || o.Stage == OrderStage.AwaitingCustomerConfirmation))
        .ToListAsync(cancellationToken);
    }

    // §7.5: "completed by them today". Order has no persisted DeliveredAt scalar column
    // (§6.2), so "today" is resolved via the OrderStatusEvents row recorded for the
    // Stage=Delivered transition, filtered to the given UTC calendar day.
    public async Task<List<Order>> GetCompletedOrdersForDriverTodayAsync(
        Guid driverId, DateTime utcToday, CancellationToken cancellationToken)
    {
        var nextDay = utcToday.AddDays(1);

        var deliveredTodayOrderIds = await _context.OrderStatusEvents
            .Where(e => e.Stage == OrderStage.Delivered && e.Timestamp >= utcToday && e.Timestamp < nextDay)
            .Select(e => e.OrderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return await _context.Orders
            .Include(o => o.Customer)
            .Where(o => o.DriverId == driverId
                && o.Stage == OrderStage.Delivered
                && deliveredTodayOrderIds.Contains(o.Id))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return _context.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId, cancellationToken);
    }

    public Task<Order?> GetByIdNoTrackingAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    // §6.3: DriverId can only be set once. A single conditional UPDATE (WHERE DriverId IS
    // NULL) guarantees two concurrent /receive calls on the same order cannot both succeed,
    // without needing a new unique index/migration. Also advances Stage 0 -> 1 "if not
    // already" (§7.5), leaving an already-Preparing order's Stage untouched.
    public async Task<bool> TryClaimForDriverAsync(Guid orderId, Guid driverId, CancellationToken cancellationToken)
    {
        var rowsAffected = await _context.Orders
            .Where(o => o.Id == orderId && o.DriverId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(o => o.DriverId, driverId)
                    .SetProperty(o => o.Stage, o => o.Stage == OrderStage.Confirmed ? OrderStage.Preparing : o.Stage),
                cancellationToken);

        return rowsAffected > 0;
    }

    public Task AddOrderStatusEventAsync(OrderStatusEvent statusEvent, CancellationToken cancellationToken)
    {
        _context.OrderStatusEvents.Add(statusEvent);
        return Task.CompletedTask;
    }

    // Day 10 addition (§7.6.2 Branch deletion rule): non-terminal = Stage 0-2 (not Delivered).
    public Task<bool> HasNonTerminalOrdersForBranchAsync(int branchId, CancellationToken cancellationToken)
    {
        return _context.Orders
            .AnyAsync(o => o.BranchId == branchId && o.Stage != OrderStage.Delivered, cancellationToken);
    }

    // ---- Day 11 additions (§7.6.3/§7.6.4 Admin Driver/Customer Management) ----

    // §7.6.3: "non-terminal (Stage 1-2) assigned orders" - narrower range than the branch-scoped
    // check above (Stage 0-2), since DriverId is never set while Stage is still 0 (Confirmed) -
    // see TryClaimForDriverAsync, which only ever transitions Stage 0->1 at the same moment it
    // assigns DriverId.
    public Task<bool> HasNonTerminalOrdersForDriverAsync(Guid driverId, CancellationToken cancellationToken)
    {
        return _context.Orders
            .AnyAsync(
                o => o.DriverId == driverId && (o.Stage == OrderStage.Preparing || o.Stage == OrderStage.OnTheWay),
                cancellationToken);
    }

    // §7.6.3: "lifetime completed-deliveries count" - batched GroupBy across the given driver
    // ids (the current results page) to avoid one round-trip per driver.
    // Day 14 addition: cancelled orders excluded from all analytics aggregations
    // (Backend Parity Spec §1.3) - a driver's lifetime "completed deliveries" figure must not
    // include a delivery that was later administratively cancelled.
    public async Task<Dictionary<Guid, int>> GetCompletedDeliveryCountsByDriverIdsAsync(
        IEnumerable<Guid> driverIds, CancellationToken cancellationToken)
    {
        var idSet = driverIds.ToList();

        return await _context.Orders
            .Where(o => o.DriverId != null && idSet.Contains(o.DriverId.Value)
                        && o.Stage == OrderStage.Delivered && !o.IsCancelled)
            .GroupBy(o => o.DriverId!.Value)
            .Select(g => new { DriverId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DriverId, x => x.Count, cancellationToken);
    }

    // §7.6.4: "lifetime order count" - batched GroupBy across the given customer ids, mirroring
    // GetCompletedDeliveryCountsByDriverIdsAsync above.
    public async Task<Dictionary<Guid, int>> GetOrderCountsByCustomerIdsAsync(
        IEnumerable<Guid> customerIds, CancellationToken cancellationToken)
    {
        var idSet = customerIds.ToList();

        return await _context.Orders
            .Where(o => o.CustomerId != null && idSet.Contains(o.CustomerId.Value))
            .GroupBy(o => o.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Count, cancellationToken);
    }

    // ---- Day 12 additions (§7.6.5 Admin Order Monitoring) ----

    // §7.6.5: "paginated, filterable by branchId/stage/date range." All four filters are
    // applied only when supplied (AND-combined). §12.1 Decision 3 (approved): CreatedAt
    // descending default order, mirroring GetCustomerOrdersPagedAsync's identical convention.
    public async Task<(IReadOnlyList<Order> Orders, int TotalCount)> GetPagedForAdminAsync(
        int page,
        int pageSize,
        int? branchId,
        OrderStage? stage,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken cancellationToken)
    {
        var query = _context.Orders.AsQueryable();

        if (branchId is not null)
        {
            query = query.Where(o => o.BranchId == branchId.Value);
        }

        if (stage is not null)
        {
            query = query.Where(o => o.Stage == stage.Value);
        }

        // §6.2 has no separate "order date" column - Orders.CreatedAt is the only documented
        // date field on this table, so the date-range filter is applied against it.
        if (dateFrom is not null)
        {
            query = query.Where(o => o.CreatedAt >= dateFrom.Value);
        }

        if (dateTo is not null)
        {
            query = query.Where(o => o.CreatedAt <= dateTo.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (orders, totalCount);
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3).
    public async Task<Dictionary<OrderStage, int>> GetOrderCountsByStatusAsync(
        CancellationToken cancellationToken)
    {
        var counts = await _context.Orders
            .Where(o => !o.IsCancelled)
            .GroupBy(o => o.Stage)
            .Select(g => new { Stage = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.Stage, x => x.Count);
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3).
    public async Task<Dictionary<int, int>> GetOrderCountsByBranchAsync(
        CancellationToken cancellationToken)
    {
        var counts = await _context.Orders
            .Where(o => !o.IsCancelled)
            .GroupBy(o => o.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.BranchId, x => x.Count);
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3) - joins back to Orders
    // (previously queried OrderItems directly) purely to apply the IsCancelled filter.
    public async Task<List<(int MenuItemId, int UnitsSold)>> GetTopSellingMenuItemIdsAsync(
        int limit, CancellationToken cancellationToken)
    {
        var results = await _context.OrderItems
            .Where(oi => oi.MenuItemId != null && !oi.Order.IsCancelled)
            .GroupBy(oi => oi.MenuItemId!.Value)
            .Select(g => new { MenuItemId = g.Key, UnitsSold = g.Sum(oi => oi.Quantity) })
            .OrderByDescending(x => x.UnitsSold)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results.Select(x => (x.MenuItemId, x.UnitsSold)).ToList();
    }

    // Day 14 addition (Backend Parity Spec §1.1/§1.2/§1.3): captured-payments-only revenue for
    // a set of days, used by GetRevenueTrendQueryHandler and GetAnalyticsOverviewQueryHandler.
    // "orderDate" groups by the Order's CreatedAt date (§6.2's only order-level timestamp);
    // cancelled orders are excluded regardless of their payment's captured status.
    public async Task<decimal> GetCapturedRevenueAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        return await _context.Orders
            .Where(o => !o.IsCancelled
                        && o.CreatedAt >= fromUtcInclusive && o.CreatedAt < toUtcExclusive
                        && o.Payment != null && o.Payment.Status == "captured")
            .SumAsync(o => o.Payment!.Amount, cancellationToken);
    }

    // Day 14 addition (Backend Parity Spec §1.1/§1.3): non-cancelled order count for a window -
    // the "totalOrders" basis for GetAnalyticsOverviewQueryHandler.
    public async Task<int> GetOrderCountAsync(
    DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        return await _context.Orders
            .Where(o => !o.IsCancelled
                        && o.CreatedAt >= fromUtcInclusive
                        && o.CreatedAt < toUtcExclusive)
            .CountAsync(cancellationToken);
    }

    public async Task<int> GetDeliveredOrderCountAsync(
    DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        return await _context.Orders
            .Where(o => !o.IsCancelled
                        && o.Stage == OrderStage.Delivered
                        && o.CreatedAt >= fromUtcInclusive
                        && o.CreatedAt < toUtcExclusive)
            .CountAsync(cancellationToken);
    }

    // Day 14 addition (Backend Parity Spec §1.1): per-day captured revenue for GetRevenueTrend,
    // trailing `days` calendar days grouped by day - the convention the route's own `days`
    // query param already established (see GetRevenueTrendQuery's XML comment).
    public async Task<Dictionary<DateTime, decimal>> GetCapturedRevenueByDayAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        var rows = await _context.Orders
            .Where(o => !o.IsCancelled
                        && o.CreatedAt >= fromUtcInclusive && o.CreatedAt < toUtcExclusive
                        && o.Payment != null && o.Payment.Status == "captured")
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Revenue = g.Sum(o => o.Payment!.Amount) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.Day, x => x.Revenue);
    }
}
