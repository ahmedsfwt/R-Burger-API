using MediatR;
using RBurger.Application.Admin.Menu.DTOs;
using RBurger.Application.Common.Interfaces;

namespace RBurger.Application.Admin.Menu.Queries.GetMenuItemsAdmin;

public class GetMenuItemsAdminQueryHandler
    : IRequestHandler<GetMenuItemsAdminQuery, List<MenuItemAdminResponse>>
{
    private readonly IMenuItemRepository _menuItemRepository;

    public GetMenuItemsAdminQueryHandler(IMenuItemRepository menuItemRepository)
    {
        _menuItemRepository = menuItemRepository;
    }

    public async Task<List<MenuItemAdminResponse>> Handle(
        GetMenuItemsAdminQuery request, CancellationToken cancellationToken)
    {
        var menuItems = await _menuItemRepository.GetAllAsync(cancellationToken);

        return menuItems.Select(mi => new MenuItemAdminResponse
        {
            Id = mi.Id,
            CategoryKey = mi.Category.Key,
            BranchId = mi.BranchId,
            NameAr = mi.NameAr,
            NameEn = mi.NameEn,
            DescriptionAr = mi.DescriptionAr,
            DescriptionEn = mi.DescriptionEn,
            Price = mi.Price,
            ImageUrl = mi.ImageUrl,
            IsAvailable = mi.IsAvailable
        }).ToList();
    }
}