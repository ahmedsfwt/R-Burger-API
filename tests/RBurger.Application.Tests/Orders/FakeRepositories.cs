using RBurger.Application.Common.Interfaces;
using RBurger.Domain.Entities;
using RBurger.Domain.Enums;

namespace RBurger.Application.Tests.Orders;

// Simple in-memory fakes instead of a mocking library, since RBurger.Application.Tests does
// not currently reference one (kept minimal/dependency-free, consistent with the existing
// Day 3 test project setup).
internal class FakeOrderRepository : IOrderRepository
{
    public List<Order> AddedOrders { get; } = new();
    private int _nextOrderNumber = 1;

    public Task AddWithGeneratedOrderNumberAsync(Order order, CancellationToken cancellationToken)
    {
        order.OrderNumber = _nextOrderNumber++;
        order.CreatedAt = DateTime.UtcNow; // simulates the DB's GETUTCDATE() default

        // Day 8 addition: CreateOrderCommandHandler now attaches an OrderStatusEvent
        // (Stage=Confirmed, TriggeredBy="system") to order.OrderStatusEvents directly,
        // rather than via AddOrderStatusEventAsync below - simulates the same
        // OrderStatusEventConfiguration GETUTCDATE() default for any such not-yet-persisted
        // event, mirroring AddOrderStatusEventAsync's identical simulation further down.
        foreach (var statusEvent in order.OrderStatusEvents.Where(e => e.Timestamp == default))
        {
            statusEvent.Timestamp = DateTime.UtcNow;
        }

        AddedOrders.Add(order);
        return Task.CompletedTask;
    }

    public Task<Order?> GetByIdWithDetailsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(AddedOrders.FirstOrDefault(o => o.Id == orderId));
    }

    public Task<(IReadOnlyList<Order> Orders, int TotalCount)> GetCustomerOrdersPagedAsync(
        Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matching = AddedOrders.Where(o => o.CustomerId == customerId).ToList();
        var paged = matching.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<Order>)paged, matching.Count));
    }

    // Day 5 addition - no Include() semantics to fake, AddedOrders already holds fully
    // in-memory entities.
    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(AddedOrders.FirstOrDefault(o => o.Id == orderId));
    }

    // Day 5 addition - AddedOrders holds object references, so mutations made by the handler
    // (e.g. setting CustomerReceivedAt) are already visible without any extra bookkeeping here.
    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // ---- Day 7 additions ----

    public List<OrderStatusEvent> AddedStatusEvents { get; } = new();

    public Task<List<Order>> GetNewOrdersForBranchAsync(int branchId, CancellationToken cancellationToken)
    {
        var matching = AddedOrders
            .Where(o => o.BranchId == branchId
                && o.DriverId == null
                && (o.Stage == OrderStage.Confirmed || o.Stage == OrderStage.Preparing))
            .ToList();
        return Task.FromResult(matching);
    }

    public Task<List<Order>> GetActiveOrdersForDriverAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var matching = AddedOrders
            .Where(o => o.DriverId == driverId
                && (o.Stage == OrderStage.Preparing || o.Stage == OrderStage.OnTheWay))
            .ToList();
        return Task.FromResult(matching);
    }

    public Task<List<Order>> GetCompletedOrdersForDriverTodayAsync(
        Guid driverId, DateTime utcToday, CancellationToken cancellationToken)
    {
        var nextDay = utcToday.AddDays(1);
        var deliveredTodayOrderIds = AddedStatusEvents
            .Where(e => e.Stage == OrderStage.Delivered && e.Timestamp >= utcToday && e.Timestamp < nextDay)
            .Select(e => e.OrderId)
            .Distinct()
            .ToHashSet();

        var matching = AddedOrders
            .Where(o => o.DriverId == driverId
                && o.Stage == OrderStage.Delivered
                && deliveredTodayOrderIds.Contains(o.Id))
            .ToList();
        return Task.FromResult(matching);
    }

    public Task<bool> ExistsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(AddedOrders.Any(o => o.Id == orderId));
    }

    // In-memory fake has no separate tracked/untracked concept - simply returns the same
    // object reference as GetByIdAsync, since there is no change-tracker staleness to
    // simulate here (that concern is specific to the real EF Core implementation).
    public Task<Order?> GetByIdNoTrackingAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(AddedOrders.FirstOrDefault(o => o.Id == orderId));
    }

    // Fakes the WHERE DriverId IS NULL conditional UPDATE - claims the order only if it is
    // currently unclaimed, mirroring TryClaimForDriverAsync's real race-safety semantics
    // closely enough for handler-level unit tests (a true concurrency race cannot be
    // exercised without a real database, so this fake only asserts the single-caller path).
    public Task<bool> TryClaimForDriverAsync(Guid orderId, Guid driverId, CancellationToken cancellationToken)
    {
        var order = AddedOrders.FirstOrDefault(o => o.Id == orderId);
        if (order is null || order.DriverId is not null)
        {
            return Task.FromResult(false);
        }

        order.DriverId = driverId;
        if (order.Stage == OrderStage.Confirmed)
        {
            order.Stage = OrderStage.Preparing;
        }

        return Task.FromResult(true);
    }

    public Task AddOrderStatusEventAsync(OrderStatusEvent statusEvent, CancellationToken cancellationToken)
    {
        // Simulates OrderStatusEventConfiguration's DB default (GETUTCDATE()) for a
        // not-yet-persisted event, mirroring FakeReviewRepository's CreatedAt simulation.
        if (statusEvent.Timestamp == default)
        {
            statusEvent.Timestamp = DateTime.UtcNow;
        }

        AddedStatusEvents.Add(statusEvent);
        return Task.CompletedTask;
    }

    // Day 10 addition - see IOrderRepository's XML comment.
    public Task<bool> HasNonTerminalOrdersForBranchAsync(int branchId, CancellationToken cancellationToken)
    {
        var matching = AddedOrders.Any(o => o.BranchId == branchId && o.Stage != OrderStage.Delivered);
        return Task.FromResult(matching);
    }

    // ---- Day 11 additions (§7.6.3/§7.6.4 Admin Driver/Customer Management) ----

    public Task<bool> HasNonTerminalOrdersForDriverAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var matching = AddedOrders.Any(o =>
            o.DriverId == driverId && (o.Stage == OrderStage.Preparing || o.Stage == OrderStage.OnTheWay));
        return Task.FromResult(matching);
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3), mirroring the real
    // OrderRepository's identical filter.
    public Task<Dictionary<Guid, int>> GetCompletedDeliveryCountsByDriverIdsAsync(
        IEnumerable<Guid> driverIds, CancellationToken cancellationToken)
    {
        var idSet = driverIds.ToHashSet();
        var counts = AddedOrders
            .Where(o => o.DriverId is not null && idSet.Contains(o.DriverId.Value)
                        && o.Stage == OrderStage.Delivered && !o.IsCancelled)
            .GroupBy(o => o.DriverId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(counts);
    }

    public Task<Dictionary<Guid, int>> GetOrderCountsByCustomerIdsAsync(
        IEnumerable<Guid> customerIds, CancellationToken cancellationToken)
    {
        var idSet = customerIds.ToHashSet();
        var counts = AddedOrders
            .Where(o => o.CustomerId is not null && idSet.Contains(o.CustomerId.Value))
            .GroupBy(o => o.CustomerId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(counts);
    }

    // ---- Day 12 additions (§7.6.5/§7.6.6 Admin Orders/Analytics) ----

    public Task<(IReadOnlyList<Order> Orders, int TotalCount)> GetPagedForAdminAsync(
        int page,
        int pageSize,
        int? branchId,
        OrderStage? stage,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken cancellationToken)
    {
        var matching = AddedOrders.AsEnumerable();

        if (branchId is not null)
        {
            matching = matching.Where(o => o.BranchId == branchId.Value);
        }

        if (stage is not null)
        {
            matching = matching.Where(o => o.Stage == stage.Value);
        }

        if (dateFrom is not null)
        {
            matching = matching.Where(o => o.CreatedAt >= dateFrom.Value);
        }

        if (dateTo is not null)
        {
            matching = matching.Where(o => o.CreatedAt <= dateTo.Value);
        }

        var ordered = matching.OrderByDescending(o => o.CreatedAt).ToList();
        var paged = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<Order>)paged, ordered.Count));
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3).
    public Task<Dictionary<OrderStage, int>> GetOrderCountsByStatusAsync(CancellationToken cancellationToken)
    {
        var counts = AddedOrders.Where(o => !o.IsCancelled).GroupBy(o => o.Stage).ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(counts);
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3).
    public Task<Dictionary<int, int>> GetOrderCountsByBranchAsync(CancellationToken cancellationToken)
    {
        var counts = AddedOrders.Where(o => !o.IsCancelled).GroupBy(o => o.BranchId).ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(counts);
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3) - iterates AddedOrders
    // directly (rather than OrderItem.Order, which fakes constructed inline never populate)
    // so the IsCancelled filter is applied against the real Order aggregate.
    public Task<List<(int MenuItemId, int UnitsSold)>> GetTopSellingMenuItemIdsAsync(
        int limit, CancellationToken cancellationToken)
    {
        var results = AddedOrders
            .Where(o => !o.IsCancelled)
            .SelectMany(o => o.OrderItems)
            .Where(oi => oi.MenuItemId is not null)
            .GroupBy(oi => oi.MenuItemId!.Value)
            .Select(g => (MenuItemId: g.Key, UnitsSold: g.Sum(oi => oi.Quantity)))
            .OrderByDescending(x => x.UnitsSold)
            .Take(limit)
            .ToList();

        return Task.FromResult(results);
    }

    // ---- Day 14 additions (Backend Parity Spec §1.1/§1.2/§1.3) ----

    public Task<decimal> GetCapturedRevenueAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        var revenue = AddedOrders
            .Where(o => !o.IsCancelled && o.CreatedAt >= fromUtcInclusive && o.CreatedAt < toUtcExclusive
                        && o.Payment is not null && o.Payment.Status == "captured")
            .Sum(o => o.Payment!.Amount);
        return Task.FromResult(revenue);
    }

    public Task<int> GetOrderCountAsync(
    DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        var count = AddedOrders.Count(o =>
            !o.IsCancelled
            && o.CreatedAt >= fromUtcInclusive
            && o.CreatedAt < toUtcExclusive);

        return Task.FromResult(count);
    }

    public Task<int> GetDeliveredOrderCountAsync(
    DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        var count = AddedOrders.Count(o =>
            !o.IsCancelled
            && o.Stage == OrderStage.Delivered
            && o.CreatedAt >= fromUtcInclusive
            && o.CreatedAt < toUtcExclusive);

        return Task.FromResult(count);
    }

    public Task<Dictionary<DateTime, decimal>> GetCapturedRevenueByDayAsync(
        DateTime fromUtcInclusive, DateTime toUtcExclusive, CancellationToken cancellationToken)
    {
        var byDay = AddedOrders
            .Where(o => !o.IsCancelled && o.CreatedAt >= fromUtcInclusive && o.CreatedAt < toUtcExclusive
                        && o.Payment is not null && o.Payment.Status == "captured")
            .GroupBy(o => o.CreatedAt.Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Payment!.Amount));
        return Task.FromResult(byDay);
    }
}

// Day 7 addition - in-memory fake for IOrderRealtimeNotifier, records every call made so
// handler tests can assert the documented event/group/payload behavior without a real Hub.
internal class FakeOrderRealtimeNotifier : IOrderRealtimeNotifier
{
    public List<(Guid OrderId, OrderStage Stage, DateTime Timestamp, string TriggeredBy)> StatusChangedCalls
    {
        get;
    } = new();

    public List<(Guid OrderId, int BranchId)> NewOrderAvailableCalls { get; } = new();

    public List<(Guid OrderId, string Status)> PaymentConfirmedCalls { get; } = new();

    public Task NotifyOrderStatusChangedAsync(
        Guid orderId, OrderStage stage, DateTime timestamp, string triggeredBy, CancellationToken cancellationToken)
    {
        StatusChangedCalls.Add((orderId, stage, timestamp, triggeredBy));
        return Task.CompletedTask;
    }

    public Task NotifyNewOrderAvailableAsync(Guid orderId, int branchId, CancellationToken cancellationToken)
    {
        NewOrderAvailableCalls.Add((orderId, branchId));
        return Task.CompletedTask;
    }

    // ---- Day 12 addition (§7.7/§9.3 Payments) ----

    public Task NotifyPaymentConfirmedAsync(Guid orderId, string status, CancellationToken cancellationToken)
    {
        PaymentConfirmedCalls.Add((orderId, status));
        return Task.CompletedTask;
    }
}

// Day 5 addition - simple in-memory fake for IReviewRepository, following the exact same
// dependency-free pattern as the fakes above (Day 3/4).
internal class FakeReviewRepository : IReviewRepository
{
    public List<Review> AddedReviews { get; } = new();

    public Task<bool> ExistsByOrderIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(AddedReviews.Any(r => r.OrderId == orderId));
    }

    public Task AddAsync(Review review, CancellationToken cancellationToken)
    {
        AddedReviews.Add(review);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        // Simulates ReviewConfiguration's DB default (GETUTCDATE()) for any review that
        // hasn't had CreatedAt set yet - mirrors FakeOrderRepository's CreatedAt simulation.
        foreach (var review in AddedReviews.Where(r => r.CreatedAt == default))
        {
            review.CreatedAt = DateTime.UtcNow;
        }

        return Task.CompletedTask;
    }

    // ---- Day 12 additions (§7.6.6 Reviews & Analytics) ----

    public Task<(IReadOnlyList<Review> Reviews, int TotalCount)> GetPagedForAdminAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var ordered = AddedReviews.OrderByDescending(r => r.CreatedAt).ToList();
        var paged = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<Review>)paged, ordered.Count));
    }

    public Task<Review?> GetByIdAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        return Task.FromResult(AddedReviews.FirstOrDefault(r => r.Id == reviewId));
    }

    public Task DeleteAsync(Review review, CancellationToken cancellationToken)
    {
        AddedReviews.RemoveAll(r => r.Id == review.Id);
        return Task.CompletedTask;
    }

    // Day 14: cancelled orders excluded (Backend Parity Spec §1.3).
    public Task<Dictionary<byte, int>> GetRatingDistributionAsync(CancellationToken cancellationToken)
    {
        var counts = AddedReviews.Where(r => !r.Order.IsCancelled)
            .GroupBy(r => r.Rating).ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(counts);
    }
}

internal class FakeBranchRepository : IBranchRepository
{
    public List<Branch> Branches { get; } = new();
    private int _nextId = 1;

    public Task<Branch?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return Task.FromResult(Branches.FirstOrDefault(b => b.Id == id));
    }

    // ---- Day 10 additions ----

    public Task AddAsync(Branch branch, CancellationToken cancellationToken)
    {
        if (branch.Id == 0)
        {
            branch.Id = _nextId++;
        }

        Branches.Add(branch);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Branch branch, CancellationToken cancellationToken)
    {
        Branches.RemoveAll(b => b.Id == branch.Id);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        // In-memory fake has no FK-Restrict concept to simulate (that's BranchRepository's
        // real-EF-only defensive DbUpdateException fallback - see its XML comment); tests for
        // that specific edge case are not exercisable without a real database and are
        // deferred accordingly (see Day 10 final report's Deferred Items).
        return Task.CompletedTask;
    }

    // ---- Day 12 addition (§7.6.6 Analytics) ----

    public Task<List<Branch>> GetAllAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Branches.ToList());
    }

    // Day 15 addition (§7.3 - public endpoint).
    public Task<List<Branch>> GetActiveBranchesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Branches.Where(b => b.IsActive).OrderBy(b => b.Id).ToList());
    }
}

internal class FakeMenuItemRepository : IMenuItemRepository
{
    public List<MenuItem> MenuItems { get; } = new();
    private int _nextId = 1;

    public Task<List<MenuItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var idSet = ids.ToHashSet();
        return Task.FromResult(MenuItems.Where(mi => idSet.Contains(mi.Id)).ToList());
    }

    // ---- Day 10 additions ----

    public Task<MenuItem?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return Task.FromResult(MenuItems.FirstOrDefault(mi => mi.Id == id));
    }

    public Task<List<MenuItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(MenuItems.ToList());
    }

    public Task AddAsync(MenuItem menuItem, CancellationToken cancellationToken)
    {
        if (menuItem.Id == 0)
        {
            menuItem.Id = _nextId++;
        }

        MenuItems.Add(menuItem);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(MenuItem menuItem, CancellationToken cancellationToken)
    {
        MenuItems.RemoveAll(mi => mi.Id == menuItem.Id);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // Day 15 addition (§7.3 - public endpoint).
    public Task<List<MenuItem>> GetAvailableByBranchIdAsync(int branchId, CancellationToken cancellationToken)
    {
        return Task.FromResult(MenuItems.Where(mi => mi.BranchId == branchId && mi.IsAvailable).ToList());
    }
}
