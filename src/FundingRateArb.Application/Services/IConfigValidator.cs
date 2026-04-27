using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public record ConfigValidationResult(bool IsValid, List<string> Errors, List<string>? Warnings = null);

public interface IConfigValidator
{
    ConfigValidationResult Validate(BotConfiguration config);
}
