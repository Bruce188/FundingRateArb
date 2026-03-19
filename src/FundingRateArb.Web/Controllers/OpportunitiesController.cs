using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class OpportunitiesController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Dashboard");
}
