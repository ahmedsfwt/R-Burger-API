namespace RBurger.Application.Admin.Orders.DTOs;

using RBurger.Domain.Enums;

// §7.6.5 GET /api/v1/admin/orders list item example:
// { "orderId","orderNumber","branchId","customerName","total","stage","createdAt" }.
// Stage kept as the OrderStage enum type, matching every other order DTO's convention
// (OrdersMineItemDto, OrderDetailResponse, etc.) rather than a raw byte.
public class AdminOrderListItemDto
{
    public Guid OrderId { get; set; }
    public int OrderNumber { get; set; }
    public int BranchId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public OrderStage Stage { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime CreatedAt { get; set; }
}
