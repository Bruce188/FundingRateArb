using Microsoft.AspNetCore.Mvc.Rendering;

namespace FundingRateArb.Web.ViewModels.Admin;

public class UserRoleViewModel
{
    public string UserId { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public IList<string> Roles { get; set; } = [];
    public IEnumerable<SelectListItem>? AvailableRoles { get; set; }
}
