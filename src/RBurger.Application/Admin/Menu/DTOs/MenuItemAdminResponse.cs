namespace RBurger.Application.Admin.Menu.DTOs;

// §7.6.1's Create response example shows { id, categoryKey, nameAr, nameEn, price, imageUrl,
// isAvailable }; its Update example shows only the fields sent in the request. Per §7.0's
// "Success envelope: the resource itself", this DTO returns the full current resource state
// (including descriptionAr/En, which the Create example omits but the Create *request*
// requires) uniformly from Create/Update - an implementation decision (approved ahead of
// Day 10 coding) rather than mirroring each example's literal, abbreviated field subset.
public class MenuItemAdminResponse
{
    public int Id { get; set; }
    public string CategoryKey { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string DescriptionAr { get; set; } = string.Empty;
    public string DescriptionEn { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsAvailable { get; set; }
}
