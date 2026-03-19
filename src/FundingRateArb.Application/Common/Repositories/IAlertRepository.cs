using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Common.Repositories;

public interface IAlertRepository
{
    Task<List<Alert>> GetAllAsync(bool unreadOnly = false);
    Task<Alert?> GetByIdAsync(int id);
    Task<List<Alert>> GetByUserAsync(string userId, bool unreadOnly = false, int skip = 0, int take = 500);
    Task<List<Alert>> GetByPositionAsync(int positionId);
    Task<Alert?> GetRecentAsync(string userId, int? positionId, AlertType type, TimeSpan within);
    Task<List<Alert>> GetRecentUnreadAsync(TimeSpan within);
    void Add(Alert alert);
    void Update(Alert alert);
}
