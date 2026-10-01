using MediatR;
using RBurger.Application.Admin.Menu.DTOs;
using RBurger.Application.Common.Exceptions;
using RBurger.Application.Common.Interfaces;

namespace RBurger.Application.Admin.Menu.Commands.UpdateMenuItem;

public class UpdateMenuItemCommandHandler : IRequestHandler<UpdateMenuItemCommand, MenuItemAdminResponse>
{
    private readonly IMenuItemRepository _menuItemRepository;
    private readonly IMenuCategoryRepository _menuCategoryRepository;

    public UpdateMenuItemCommandHandler(
        IMenuItemRepository menuItemRepository,
        IMenuCategoryRepository menuCategoryRepository)
    {
        _menuItemRepository = menuItemRepository;
        _menuCategoryRepository = menuCategoryRepository;
    }

    public async Task<MenuItemAdminResponse> Handle(UpdateMenuItemCommand request, CancellationToken cancellationToken)
    {
        // §7.8: 404 "menu item ... doesn't exist".
        var menuItem = await _menuItemRepository.GetByIdAsync(request.Id, cancellationToken);
        if (menuItem is null)
        {
            throw new NotFoundException($"Menu item {request.Id} was not found.");
        }

        if (request.NameAr is not null)
        {
            menuItem.NameAr = request.NameAr;
        }

        if (request.NameEn is not null)
        {
            menuItem.NameEn = request.NameEn;
        }

        if (request.DescriptionAr is not null)
        {
            menuItem.DescriptionAr = request.DescriptionAr;
        }

        if (request.DescriptionEn is not null)
        {
            menuItem.DescriptionEn = request.DescriptionEn;
        }

        if (request.Price is not null)
        {
            menuItem.Price = request.Price.Value;
        }

        if (request.IsAvailable is not null)
        {
            menuItem.IsAvailable = request.IsAvailable.Value;
        }

        if (request.CategoryKey is not null)
        {
            var category = await _menuCategoryRepository.GetByKeyAsync(request.CategoryKey, cancellationToken);
            if (category is null)
            {
                throw new NotFoundException($"Menu category '{request.CategoryKey}' was not found.");
            }

            menuItem.CategoryId = category.Id;
        }

        await _menuItemRepository.SaveChangesAsync(cancellationToken);

        // Re-resolve the category key for the response from the item's current (possibly
        // unchanged) CategoryId - never assume menuItem.Category navigation is loaded, since
        // repository implementations (fake and real EF) aren't guaranteed to populate it here.
        var currentCategory = await _menuCategoryRepository.GetByIdAsync(menuItem.CategoryId, cancellationToken);

        return new MenuItemAdminResponse
        {
            Id = menuItem.Id,
            CategoryKey = currentCategory?.Key ?? string.Empty,
            BranchId = menuItem.BranchId,
            NameAr = menuItem.NameAr,
            NameEn = menuItem.NameEn,
            DescriptionAr = menuItem.DescriptionAr,
            DescriptionEn = menuItem.DescriptionEn,
            Price = menuItem.Price,
            ImageUrl = menuItem.ImageUrl,
            IsAvailable = menuItem.IsAvailable
        };
    }
}
