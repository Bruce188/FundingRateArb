using FundingRateArb.Application.Common.Exchanges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
[Route("[area]/[controller]/[action]")]
public class CredentialsController : Controller
{
    private readonly IExchangeConnectorFactory _factory;
    private readonly ILogger<CredentialsController> _logger;

    public CredentialsController(
        IExchangeConnectorFactory factory,
        ILogger<CredentialsController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Performs a per-field dYdX credential validation for the given user.
    /// Returns only booleans, the failure-reason enum, and the missing-field name.
    /// Never returns credential values.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DydxValidate(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { error = "userId required" });

        try
        {
            var result = await _factory.ValidateDydxAsync(userId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
