using RBurger.Domain.Entities;
using RBurger.Domain.Enums;

namespace RBurger.Application.Common.Interfaces;

// Minimal repository abstraction (Day 4 addition, not part of the documented API surface),
// following the exact same pattern as ICustomerRepository from Day 3. Keeps Application
// independent of Infrastructure/EF Core.
public interface IOrderRepository
{
    // Approved decision #7: persists a fully-built Order (with its OrderItems/Payment graph
    // already attached) using an Application-level MAX(OrderNumber)+1 strategy with a bounded
    // retry against the DB's unique index (OrderConfiguration) as the concurrency safety net.
    // The retry itself must live in Infrastructure (not here) because it reacts to an EF Core
    // DbUpdateException, and Application must not depend on EF Core per the architecture rules.
    Task AddWithGeneratedOrderNumberAsync(Order order, CancellationToken cancellationToken);

    // Includes Branch, OrderItems, Payment, Review (Day 8) - everything
    // GET /api/v1/orders/{orderId} needs, including the documented "review" field.
    Task<Order?> GetByIdWithDetailsAsync(Guid orderId, CancellationToken cancellationToken);

    // Includes Branch (for branchNameAr) and Review (for hasReview) - everything
    // GET /api/v1/orders/mine needs. §7.0 pagination convention (page/pageSize/totalCount).
    Task<(IReadOnlyList<Order> Orders, int TotalCount)> GetCustomerOrdersPagedAsync(
        Guid customerId, int page, int pageSize, CancellationToken cancellationToken);

    // Day 5 addition: a lean, no-Include read used by customer-received/review, both of which
    // only ever touch scalar columns on Order itself (CustomerId, Stage, CustomerReceivedAt) -
    // no navigation properties are needed, unlike GetByIdWithDetailsAsync above.
    Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken);

    // Day 5 addition: explicit persist for an already-tracked Order mutated in place
    // (customer-received sets CustomerReceivedAt), following the same explicit
    // AddAsync/SaveChangesAsync split already established by ICustomerRepository (Day 3).
    Task SaveChangesAsync(CancellationToken cancellationToken);

    // ---- Day 7 additions (§7.5 Orders — Driver) ----

    // §7.5 GET /api/v1/driver/orders/new: "orders at Stage=0/1 ready for pickup at the
    // driver's own branch". Necessarily scoped to unclaimed orders (DriverId == null) -
    // once a driver has received an order it belongs on that driver's "mine" list instead,
    // not on every driver's "new" list at the branch (flagged as an inferred, not literally
    // spelled out, filter in the Day 7 report).
    Task<List<Order>> GetNewOrdersForBranchAsync(int branchId, CancellationToken cancellationToken);

    // §7.5 GET /api/v1/driver/orders/mine?status=active: orders currently assigned to this
    // driver that are not yet Delivered (Stage 1 or 2 - DriverId can only be set once Stage
    // is already >= 1 per the /receive contract, so Stage 0 with a DriverId cannot occur).
    Task<List<Order>> GetActiveOrdersForDriverAsync(Guid driverId, CancellationToken cancellationToken);

    // §7.5 GET /api/v1/driver/orders/mine?status=completed: "completed by them today".
    // Order has no persisted DeliveredAt scalar column (§6.2), so "today" is resolved via the
    // OrderStatusEvents row recorded for the Stage=Delivered transition (§6.2's audit trail),
    // filtered to the given UTC calendar day.
    Task<List<Order>> GetCompletedOrdersForDriverTodayAsync(
        Guid driverId, DateTime utcToday, CancellationToken cancellationToken);

    // Lightweight existence check (no tracking), used to distinguish 404 (order does not
    // exist) from 409 (order exists but was already claimed) after TryClaimForDriverAsync
    // reports no rows affected.
    Task<bool> ExistsAsync(Guid orderId, CancellationToken cancellationToken);

    // Untracked read used to fetch fresh column values after a raw, change-tracker-bypassing
    // update (TryClaimForDriverAsync) - re-querying via GetByIdAsync on the same DbContext
    // would otherwise return the stale already-tracked instance from the identity map instead
    // of the just-written values.
    Task<Order?> GetByIdNoTrackingAsync(Guid orderId, CancellationToken cancellationToken);

    // §6.3: "DriverId can only be set once (on Received) ... a second driver attempting to
    // Receive an already-received order gets 409 Conflict." Implemented as a single
    // conditional UPDATE (EF Core ExecuteUpdateAsync) guarded by "DriverId IS NULL" in the
    // WHERE clause, so two concurrent /receive calls on the same order cannot both succeed -
    // this is the DB-level safety net Application's read-then-write handlers elsewhere rely
    // on a unique index for; no such index exists for this nullable FK, so this conditional
    // UPDATE is the mechanism instead (no migration/schema change required).
    // Returns true if this call claimed the order, false if it was already claimed.
    Task<bool> TryClaimForDriverAsync(Guid orderId, Guid driverId, CancellationToken cancellationToken);

    // §6.2: OrderStatusEvents "audit trail of every stage change, who triggered it, and when".
    // §7.5: each of receive/ship/deliver "appends an OrderStatusEvent".
    Task AddOrderStatusEventAsync(OrderStatusEvent statusEvent, CancellationToken cancellationToken);

    // ---- Day 10 addition (§7.6.2 Branch deletion rule) ----

    // §7.6.2: "Rejected with 422 if the branch still has non-terminal (Stage 0-2) orders".
    Task<bool> HasNonTerminalOrdersForBranchAsync(int branchId, CancellationToken cancellationToken);

    // ---- Day 11 additions (§7.6.3/§7.6.4 Admin Driver/Customer Management) ----

    // §7.6.3 DELETE /api/v1/admin/drivers/{id}: "Rejected with 422 if the driver has
    // non-terminal (Stage 1-2) assigned orders." Driver-scoped counterpart of
    // HasNonTerminalOrdersForBranchAsync above - deliberately a *separate* method (not a
    // reuse/overload of the branch-scoped one) per Ahmed's explicit instruction, since the two
    // queries filter on different FK columns (Order.DriverId vs Order.BranchId) and the
    // documented "non-terminal" ranges differ (Stage 1-2 here vs Stage 0-2 for branches,
    // because DriverId is never set before Stage 1 - see TryClaimForDriverAsync).
    Task<bool> HasNonTerminalOrdersForDriverAsync(Guid driverId, CancellationToken cancellationToken);

    // §7.6.3 GET /api/v1/admin/drivers: "lifetime completed-deliveries count" (deliveriesCompleted).
    // Unlike GetCompletedOrdersForDriverTodayAsync (Day 7, scoped to "today" via
    // OrderStatusEvents), this is a lifetime count read directly off Order.Stage - no
    // OrderStatusEvents join needed, since a Delivered order's current Stage already proves it
    // was completed at some point (§6.3: Stage only moves forward, never backward). Batched by
    // a single GroupBy query across the current page's driver ids to avoid N+1 queries.
    Task<Dictionary<Guid, int>> GetCompletedDeliveryCountsByDriverIdsAsync(
        IEnumerable<Guid> driverIds, CancellationToken cancellationToken);

    // §7.6.4 GET /api/v1/admin/customers: "lifetime order count" (ordersCount). Batched by a
    // single GroupBy query across the current page's customer ids, mirroring
    // GetCompletedDeliveryCountsByDriverIdsAsync's batching approach above.
    Task<Dictionary<Guid, int>> GetOrderCountsByCustomerIdsAsync(
        IEnumerable<Guid> customerIds, CancellationToken cancellationToken);

    // ---- Day 12 additions (§7.6.5 Admin Order Monitoring) ----

    // §7.6.5 GET /api/v1/admin/orders: "paginated, filterable by branchId/stage/date range".
    // All four filters are optional (applied only when non-null), combined with AND, exactly
    // mirroring how every other list endpoint's optional query params behave in this codebase.
    // §12.3 Decision 3 (approved): defaults to CreatedAt descending, matching every other list
    // endpoint's ordering. dateFrom/dateTo bound Orders.CreatedAt (the only documented Orders
    // date column - §6.2 has no separate "order date" field), inclusive on both ends.
    Task<(IReadOnlyList<Order> Orders, int TotalCount)> GetPagedForAdminAsync(
        int page,
        int pageSize,
        int? branchId,
        OrderStage? stage,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken cancellationToken);

    // ---- Day 12 additions (§7.6.6 Analytics) ----

    // §7.6.6 GET /api/v1/admin/analytics/orders-by-status: "Current order counts grouped by
    // Stage 0-3". "Current" (not a time-windowed metric, unlike /overview's range param) -
    // counts every Order row regardless of CreatedAt, grouped by its present Stage.
    Task<Dictionary<OrderStage, int>> GetOrderCountsByStatusAsync(CancellationToken cancellationToken);

    // §7.6.6 GET /api/v1/admin/analytics/orders-by-branch: "Order counts grouped by branch" -
    // no query params documented on this route, so (like orders-by-status) this is an all-time,
    // unfiltered count, not scoped by a range.
    Task<Dictionary<int, int>> GetOrderCountsByBranchAsync(CancellationToken cancellationToken);

    // §7.6.6 GET /api/v1/admin/analytics/top-items?limit=5: "Best-selling menu items by order
    // count" with response field "unitsSold" - implemented as SUM(OrderItems.Quantity) grouped
    // by MenuItemId (the literal reading of "units sold"; custom-built burgers have
    // MenuItemId=null per §7.4 and are naturally excluded since this endpoint's response is
    // keyed by menuItemId). No date range is documented on this route - all-time.
    Task<List<(int MenuItemId, int UnitsSold)>> GetTopSellingMenuItemIdsAsync(
        int limit, CancellationToken cancellationToken);

    // ---- Day 14 additions (Backend Parity Spec §1.1/§1.2/§1.3) ----

    // "Captured payments only" revenue (§1.1) for a [from, to) UTC window, excluding cancelled
    // orders (§1.3). Used by both GetAnalyticsOverviewQueryHandler and
    // GetRevenueTrendQueryHandler so the revenue basis is computed identically everywhere.
    Task<decimal> GetCapturedRevenueAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken);

    // Non-cancelled order count for a [from, to) UTC window (§1.3) - the "totalOrders" basis.
    Task<int> GetOrderCountAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken);

    Task<int> GetDeliveredOrderCountAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken);

    // Per-calendar-day captured revenue (§1.1) across a [from, to) UTC window, excluding
    // cancelled orders (§1.3) - powers GET /api/v1/admin/analytics/revenue-trend.
    Task<Dictionary<DateTime, decimal>> GetCapturedRevenueByDayAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken);
}
