using MediatR;
using RBurger.Application.Admin.Orders.DTOs;
using RBurger.Application.Common.Interfaces;
using RBurger.Application.Common.Models;

namespace RBurger.Application.Admin.Orders.Queries.GetAdminOrders;

public class GetAdminOrdersQueryHandler
    : IRequestHandler<GetAdminOrdersQuery, PagedResponse<AdminOrderListItemDto>>
{
    private readonly IOrderRepository _orderRepository;

    public GetAdminOrdersQueryHandler(IOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<PagedResponse<AdminOrderListItemDto>> Handle(
        GetAdminOrdersQuery request, CancellationToken cancellationToken)
    {
        // §7.0 defensive clamp, mirroring every other paginated Admin query handler.
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 20 : request.PageSize;

        var (orders, totalCount) = await _orderRepository.GetPagedForAdminAsync(
            page, pageSize, request.BranchId, request.Stage, request.DateFrom, request.DateTo,
            cancellationToken);

        var items = orders.Select(o => new AdminOrderListItemDto
        {
            OrderId = o.Id,
            OrderNumber = o.OrderNumber,
            BranchId = o.BranchId,
            CustomerName = o.CustomerName,
            Total = o.Total,
            Stage = o.Stage,
            IsCancelled = o.IsCancelled,
            CreatedAt = o.CreatedAt
        }).ToList();

        return new PagedResponse<AdminOrderListItemDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }
}
